using System.Collections.Immutable;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>
/// The speech worker, as one <see cref="ITranscriber"/> the session keeps for
/// as long as dictation is on while the process behind it is stopped, started
/// and replaced.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a slot.</b> The session is built once per arm and holds its
/// transcriber for life. The worker behind it does not live that long: it is
/// unloaded after <see cref="DictationControllerOptions.WorkerIdleUnload"/>
/// without a dictation (the model is gigabytes), and restarted when it dies.
/// A language change does NOT restart it — the engine takes the language per
/// request (<see cref="Language"/>). Each start is a fresh
/// <see cref="AsrWorkerTranscriber"/> over a fresh process from
/// <see cref="IDictationPlatform.CreateWorker"/>, so every start gets the
/// startup-only stderr capture <see cref="DictationWorker"/> describes.
/// </para>
/// <para>
/// <b>One start at a time.</b> <see cref="StartAsync"/> is single flight: the
/// press that reloads an unloaded model while the user speaks, the release
/// that needs it, and a background restart all wait for the same start
/// instead of sending a second init down a pipe.
/// </para>
/// <para>
/// <b>Stopping wins.</b> <see cref="Unload"/> and <see cref="Dispose"/> cancel a
/// start in flight; a start that finishes after either is discarded and its
/// process killed, never installed.
/// </para>
/// <para>
/// <b>The live preview rides the installed worker, or nothing.</b> As an
/// <see cref="IPreviewTranscriber"/> (DICTATION_PREVIEW=1) the slot opens a
/// stream only on a worker that is installed and ready — never while one
/// loads, and it never starts one: a hold right after an idle unload has no
/// preview and its final pass waits for the load, as on the Mac
/// (<c>DictationAsrTransport.startPreview</c>). (A worker process that dies
/// in the instant between that check and the frame is started again by the
/// host itself, the self-heal every exchange has: the same restart the
/// release's decode would make a moment later.) A stream belongs to the
/// worker that opened it and dies with it: every stop, unload or restart
/// forgets the open streams (<see cref="TakeUnderLock"/>), and a push or
/// cancel for one goes nowhere unless its worker is still the installed
/// one and still ready (<see cref="OwnerUnderLock"/>). So a stream id never
/// reaches the worker that replaced its own, and a cancel for a stream
/// whose process died never reaches the host's exchange, which would start
/// a new process just to cancel a stream that died with the old one — a
/// restart outside the controller's restart policy.
/// </para>
/// </remarks>
public sealed partial class DictationWorkerSlot : ITranscriber, IPreviewTranscriber
{
    private readonly IDictationPlatform _platform;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly object _sync = new();

    private DictationWorkerPlan _plan;
    private AsrWorkerTranscriber? _current;
    private Task<bool>? _starting;
    private CancellationTokenSource _startCancel = new();
    private int _generation;
    private bool _disposed;
    private int _transcribing;

    /// <summary>The live-preview streams open now, each with the worker that
    /// opened it. Emptied with every change of worker.</summary>
    private readonly Dictionary<string, AsrWorkerTranscriber> _previewStreams = new(StringComparer.Ordinal);

    public DictationWorkerSlot(IDictationPlatform platform, ILoggerFactory logs, DictationWorkerPlan plan)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _log = logs.CreateLogger<DictationWorkerSlot>();
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    /// <summary>What the next start launches, with <see cref="Language"/> as
    /// it is now.</summary>
    public DictationWorkerPlan Plan
    {
        get { lock (_sync) { return _plan; } }
    }

    /// <summary>
    /// The recognition language every transcription asks for, as a Whisper
    /// code, or null for automatic detection. Live: the worker takes the
    /// language per request, so a change reaches the NEXT decode of the
    /// worker already loaded — no restart, no model reload — and, being part
    /// of <see cref="Plan"/>, a later start launches with it too.
    /// </summary>
    public string? Language
    {
        get { lock (_sync) { return _plan.Language; } }
        set { lock (_sync) { _plan = _plan with { Language = value }; } }
    }

    /// <summary>Whether a worker is installed (it may have died since).</summary>
    public bool IsLoaded
    {
        get { lock (_sync) { return _current is not null; } }
    }

    /// <summary>Whether a start is in flight.</summary>
    public bool IsStarting
    {
        get { lock (_sync) { return _starting is not null; } }
    }

    /// <summary>Whether the installed worker is alive with its model loaded.</summary>
    public bool IsReady
    {
        get { lock (_sync) { return _current?.Wire.IsReady == true; } }
    }

    /// <summary>
    /// Whether a transcription has been asked for and has not returned: a
    /// decode on the worker's pipe, or one waiting for its worker to start.
    /// A cancel then stops the worker (the host cannot leave a reply on the
    /// pipe for the next caller); once the words are back, a cancel leaves
    /// it running.
    /// </summary>
    public bool IsTranscribing => Volatile.Read(ref _transcribing) > 0;

    public bool? RunsOnNpu
    {
        get { lock (_sync) { return _current?.RunsOnNpu; } }
    }

    /// <summary>
    /// Start a worker and load the model, unless a ready one is installed or a
    /// start is already running (which this then waits for).
    /// </summary>
    /// <returns>Whether a worker is ready afterwards.</returns>
    /// <exception cref="Exception">Whatever creating the worker threw; a
    /// model that will not load is <c>false</c>, not an exception.</exception>
    public Task<bool> StartAsync()
    {
        TaskCompletionSource<bool> started;
        AsrWorkerTranscriber? previous;
        DictationWorkerPlan plan;
        CancellationToken token;
        int generation;
        lock (_sync)
        {
            if (_disposed)
            {
                return Task.FromResult(false);
            }

            if (_starting is not null)
            {
                return _starting;
            }

            if (_current is { } current && current.Wire.IsReady)
            {
                return Task.FromResult(true);
            }

            // A worker that died is replaced, not revived in place: a fresh
            // process gets a fresh startup log window.
            previous = _current;
            _current = null;
            _previewStreams.Clear();
            started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _starting = started.Task;
            plan = _plan;
            token = _startCancel.Token;
            generation = _generation;
        }

        previous?.Dispose();
        _ = RunStartAsync(plan, started, token, generation);
        return started.Task;
    }

    /// <summary><see cref="StartAsync"/> with nobody waiting: failures are logged.</summary>
    public void StartInBackground() =>
        _ = StartAsync().ContinueWith(
            t => _log.LogError(t.Exception, "Speech recognition could not be started"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Stop the worker and any start in flight, and start again.</summary>
    public Task<bool> RestartAsync()
    {
        Unload();
        return StartAsync();
    }

    /// <summary>
    /// Stop the worker and give its memory back. The next
    /// <see cref="StartAsync"/> or transcription starts a new one.
    /// </summary>
    public void Unload()
    {
        AsrWorkerTranscriber? previous;
        lock (_sync)
        {
            previous = TakeUnderLock();
        }

        previous?.Dispose();
    }

    private string? _vocabularyScene;
    public void SetVocabularyScene(string? scene)
    {
        lock (_sync) { _vocabularyScene = Learning.LearningScene.Normalize(scene); }
    }

    /// <summary>
    /// Transcribe <paramref name="capture"/>; when the worker dies under the
    /// decode or misses its deadline, start it again and try once more with
    /// the longer deadline (<see cref="AsrWorkerTranscriber.RetryTimeoutFor"/>),
    /// so a slow decode of a long take is not the end of it
    /// (DictationWorkerSlot.Retry.cs).
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(MicrophoneCapture capture, CancellationToken ct)
    {
        Interlocked.Increment(ref _transcribing);
        try
        {
            return await TranscribeWithRetryAsync(capture, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _transcribing);
        }
    }

    /// <param name="timeout">The wire budget, or null for the deadline the
    /// worker's options give a first try.</param>
    private async Task<(TranscriptionResult Result, AsrWorkerTranscriber? Worker)> TranscribeOnWorkerAsync(
        MicrophoneCapture capture, CancellationToken ct, TimeSpan? timeout = null)
    {
        AsrWorkerTranscriber? current;
        string? scene;
        lock (_sync)
        {
            if (_disposed)
            {
                return (TranscriptionResult.Unavailable, null);
            }

            current = _starting is null ? _current : null;
            scene = _vocabularyScene;
        }

        if (current is null)
        {
            // Unloaded after idle, or loading since the press: wait for the
            // start, so the words already spoken are kept rather than refused.
            bool ready;
            try
            {
                ready = await StartAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                _log.LogError(error, "Speech recognition could not be started for this hold");
                return (TranscriptionResult.Unavailable, null);
            }

            lock (_sync)
            {
                current = ready ? _current : null;
            }

            if (current is null)
            {
                return (TranscriptionResult.Unavailable, null);
            }
        }

        // A worker that died since it was installed is restarted by the
        // host itself on this exchange, as it always was.
        var result = await current.TranscribeWithSceneAsync(capture, ct, scene, timeout).ConfigureAwait(false);
        return (result, current);
    }

    // ─── The live preview ───────────────────────────────────────────────

    /// <summary>
    /// Open a live-preview stream on the installed, ready worker, for the
    /// vocabulary scene of this hold and in the language the next decode
    /// will use; null when no worker is ready (none installed, one loading,
    /// one that died) or the worker could not open one. Never starts a
    /// worker, never throws.
    /// </summary>
    public async Task<string?> StreamStartAsync(CancellationToken ct = default)
    {
        AsrWorkerTranscriber? current;
        string? scene;
        lock (_sync)
        {
            // No worker is installed while one starts: StartAsync takes the
            // last one out before it starts the next.
            if (_disposed || _current is not { } installed || !installed.Wire.IsReady)
            {
                return null;
            }

            current = installed;
            scene = _vocabularyScene;
        }

        var id = await current.StreamStartAsync(scene, ct).ConfigureAwait(false);
        if (id is null)
        {
            return null;
        }

        lock (_sync)
        {
            if (!_disposed && ReferenceEquals(_current, current))
            {
                _previewStreams[id] = current;
                return id;
            }
        }

        // The worker was unloaded or replaced while the stream opened. Its
        // process is gone with it, and with it the stream.
        _log.LogDebug("A live-preview stream opened on a worker that has since gone; dropped");
        return null;
    }

    /// <summary>A push to <paramref name="streamId"/>'s worker while it is
    /// still the installed one; null (no answer) otherwise.</summary>
    public Task<PreviewHypothesis?> StreamPushAsync(
        string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default)
    {
        AsrWorkerTranscriber? owner;
        lock (_sync)
        {
            owner = OwnerUnderLock(streamId);
        }

        return owner is null
            ? Task.FromResult<PreviewHypothesis?>(null)
            : owner.StreamPushAsync(streamId, pcm, ct);
    }

    /// <summary>Cancel <paramref name="streamId"/> on its worker while it is
    /// still the installed, ready one; a stream whose worker has gone or
    /// died needs nothing, and its id is forgotten either way.</summary>
    public Task StreamCancelAsync(string streamId, CancellationToken ct = default)
    {
        AsrWorkerTranscriber? owner;
        lock (_sync)
        {
            owner = OwnerUnderLock(streamId);
            if (streamId is not null)
            {
                _previewStreams.Remove(streamId);
            }
        }

        return owner is null ? Task.CompletedTask : owner.StreamCancelAsync(streamId!, ct);
    }

    /// <summary>The worker that opened <paramref name="streamId"/>, while it
    /// is still installed and its process alive with the model loaded; null
    /// otherwise. A push would find a dead worker unavailable by itself, but
    /// a cancel goes through the host's exchange, which starts a dead
    /// worker again first. Caller holds <see cref="_sync"/>.</summary>
    private AsrWorkerTranscriber? OwnerUnderLock(string? streamId) =>
        !_disposed
        && streamId is not null
        && _previewStreams.TryGetValue(streamId, out var owner)
        && ReferenceEquals(owner, _current)
        && owner.Wire.IsReady
            ? owner
            : null;

    public void Dispose()
    {
        AsrWorkerTranscriber? previous;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            previous = TakeUnderLock();
        }

        previous?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Detach the installed worker and cancel a start in flight.
    /// Caller holds <see cref="_sync"/> and disposes what is returned.</summary>
    private AsrWorkerTranscriber? TakeUnderLock()
    {
        _generation++;
        // A stream dies with the worker that opened it.
        _previewStreams.Clear();
        if (_starting is not null)
        {
            _startCancel.Cancel();
            // Not disposed: the start in flight still holds its token.
            _startCancel = new CancellationTokenSource();
            _starting = null;
        }

        var previous = _current;
        _current = null;
        return previous;
    }

    private async Task RunStartAsync(
        DictationWorkerPlan plan, TaskCompletionSource<bool> started, CancellationToken token, int generation)
    {
        AsrWorkerTranscriber? transcriber = null;
        try
        {
            var worker = _platform.CreateWorker(plan);
            transcriber = new AsrWorkerTranscriber(
                worker.Process, plan.Transcriber, ImmutableArray<string>.Empty,
                _logs.CreateLogger<AsrWorkerTranscriber>(),
                currentLanguage: () => Language);
            try
            {
                await transcriber.WarmUpAsync(token).ConfigureAwait(false);
            }
            finally
            {
                worker.StartupFinished();
            }

            var ready = transcriber.RunsOnNpu is not null;
            lock (_sync)
            {
                if (ReferenceEquals(_starting, started.Task))
                {
                    _starting = null;
                }

                if (ready && !_disposed && generation == _generation)
                {
                    _current = transcriber;
                    transcriber = null;
                }
                else
                {
                    ready = false;
                }
            }

            transcriber?.Dispose();
            started.SetResult(ready);
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_starting, started.Task))
                {
                    _starting = null;
                }
            }

            transcriber?.Dispose();
            started.SetException(error);
        }
    }
}
