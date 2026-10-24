using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Services;

/// <summary>Privacy-safe measurements for one live-preview hold.</summary>
public sealed record DictationPreviewMetrics(
    string OperationId,
    long SessionMs,
    long? StreamOpenMs,
    long? FirstAudioMs,
    long? FirstPushMs,
    long? FirstPartialMs,
    int PushCount,
    int PartialCount,
    int ChangedPartialCount,
    int BackpressuredBufferCount,
    long? ChangedGapP50Ms,
    long? ChangedGapP95Ms,
    int LastPreviewLength)
{
    /// <summary>Audio dropped, oldest first, because more than
    /// <see cref="DictationPreviewSession.MaxPendingSeconds"/> of it was
    /// waiting for a stream (the Mac's <c>droppedSampleCount</c>): mono
    /// samples at the device's rate.</summary>
    public long DroppedSampleCount { get; init; }

    /// <summary>Characters in the last caption drawn: the Mac's
    /// <c>lastCaptionCharacterCount</c>. <see cref="LastPreviewLength"/> is
    /// the whole text the preview heard, which the comparison uses.</summary>
    public int LastCaptionLength { get; init; }
}

/// <summary>
/// The preview text after a push answered, or empty at a stop, with the run
/// it belongs to: the number <see cref="DictationPreviewSession.Start"/>
/// returned for that run. Taken in the same lock as the check that the run
/// is still the current one, so a presenter that compares it with the run
/// it is showing never draws one hold's words on another, however late the
/// event reaches it.
/// </summary>
public readonly record struct DictationPreviewUpdate(long Run, string Text);

/// <summary>
/// Numeric-only comparison between the preview and accurate final result.
/// Neither text is retained by this value or written to the rolling log.
/// </summary>
public sealed record DictationPreviewComparison(
    double Agreement,
    int PreviewLength,
    int FinalLength);

/// <summary>
/// Drives the live preview for one hold: opens a stream at the press, feeds it
/// the microphone as it arrives, and abandons it at the release.
/// </summary>
/// <remarks>
/// <para>
/// <b>The preview is never the transcript.</b> Nothing here can insert text.
/// The words that reach the user's document come from the accurate
/// <c>transcribe</c> that <see cref="DictationSession"/> runs on the released
/// audio; this object only produces something to look at while that is still
/// impossible. The two disagreeing is expected and is not a defect —
/// the live greedy decode has less context and search than the released final
/// pass. The preview receives the current bias vocabulary, but the final pass
/// remains authoritative and is the only text that can be inserted.
/// </para>
/// <para>
/// <b>Cost and priority.</b> The preview reuses the one Whisper model the
/// worker already loaded for the final pass (CTranslate2 on the CPU in the
/// Windows Dictation app); it does not load a second large model. Caption pushes are skip-on-busy, and the shared worker admits the
/// released final hold ahead of queued caption decodes. If Whisper cannot open
/// a live session, preview fails explicitly and final dictation still runs; a
/// different recognizer is never substituted.
/// </para>
/// <para>
/// <b>Never throws into the caller.</b> Audio arrives on the device's capture
/// thread, and that thread must not be taken down by a recogniser problem. A
/// preview that fails costs a hypothesis, not a dictation, so every failure
/// here is swallowed to a debug log and the preview simply stops updating.
/// </para>
/// <para>
/// One push in flight at a time, and a backlog is DROPPED rather than queued.
/// A preview that fell behind and then replayed stale audio would print words
/// after the user stopped saying them, which is worse than a preview that
/// skipped them. The audio waiting for a push is held to the newest
/// <see cref="MaxPendingSeconds"/>, as on the Mac, so a stream that is slow
/// to open cannot grow it for the length of the hold.
/// </para>
/// <para>
/// <b>Stopping is bounded by the caller and ordered here.</b>
/// <see cref="StopAsync"/> detaches the run first, so no late reply can put
/// text back on screen, then waits for an open and a push already on the
/// wire, then cancels the stream; the Mac's <c>stop()</c>. A caller that is
/// about to send the final decode down the same worker awaits it, for a
/// bounded time.
/// </para>
/// </remarks>
public sealed class DictationPreviewSession : IDisposable
{
    /// <summary>
    /// Audio per push. An A/B on a Snapdragon X Elite laptop, same ten clips, measured 160 ms at
    /// identical WER to 320 ms, with first visible text improving from
    /// p50/p95 960/1280 ms to 800/1120 ms. The recogniser still decodes only
    /// when ready; the intervening pull is cheap and exposes readiness sooner.
    /// <c>ops_stream.CHUNK_SECONDS</c> is the same number.
    /// </summary>
    public const double ChunkSeconds = 0.16;

    /// <summary>The most audio held for the next push, in seconds of the
    /// device's rate: the Mac's <c>maxPendingSeconds</c>. Older audio is
    /// dropped, and counted (<see cref="DictationPreviewMetrics.DroppedSampleCount"/>).</summary>
    public const double MaxPendingSeconds = 3.0;

    private readonly IPreviewTranscriber _transcriber;
    private readonly ISecureFieldProbe? _secureField;
    private readonly ILogger<DictationPreviewSession> _log;
    private readonly DictationPreviewAccumulator _accumulator = new();
    private readonly object _sync = new();
    private readonly List<float> _pending = new();

    /// <summary>
    /// Identity of the current hold. Preview wire calls are deliberately not
    /// cancelled at key-up: cancellation after a frame reaches the singleton
    /// ASR pipe forces <see cref="AsrWorkerHost"/> to tear that shared worker
    /// down to keep framing aligned. A late reply is instead recognized by
    /// this identity and its stream is cancelled cleanly on the same pipe.
    /// </summary>
    private sealed class PreviewRun(long number)
    {
        public long Number { get; } = number;
    }

    private PreviewRun? _run;
    private long _runCount;
    private string? _streamId;
    private int _sourceRateHz;
    private bool _pushInFlight;
    private bool _running;
    private bool _disposed;
    private string _operationId = "untracked";
    private long _startedTicks;
    private long? _streamOpenedTicks;
    private long? _firstAudioTicks;
    private long? _firstPushTicks;
    private long? _firstPartialTicks;
    private long? _lastChangedTicks;
    private int _pushCount;
    private int _partialCount;
    private int _changedPartialCount;
    private int _backpressuredBufferCount;
    private long _droppedSampleCount;
    private Task? _openTask;
    private Task? _pushTask;
    private Task _stopping = Task.CompletedTask;
    private readonly List<long> _changedGapsMs = new();
    private string _lastObservedText = string.Empty;
    private string _lastCaption = string.Empty;
    private string _lastCompletedPreviewText = string.Empty;

    public DictationPreviewSession(
        IPreviewTranscriber transcriber,
        ILogger<DictationPreviewSession> log,
        ISecureFieldProbe? secureField = null)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _secureField = secureField;
    }

    /// <summary>
    /// The preview text after a push answered, and empty at a stop, tagged
    /// with its run. Raised on whichever thread the push completed on — a
    /// presenter must marshal, and should compare the run with the one it
    /// shows: the event can reach it after that run has stopped and another
    /// has started.
    /// </summary>
    public event EventHandler<DictationPreviewUpdate>? PreviewUpdated;

    /// <summary>The most recently stopped hold's numeric profile.</summary>
    public DictationPreviewMetrics? LastMetrics { get; private set; }

    /// <summary>Whether a stream is open right now.</summary>
    public bool IsStreaming
    {
        get { lock (_sync) { return _streamId is not null; } }
    }

    /// <summary>
    /// Open a stream for a hold that has just begun.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refuses outright when the cursor is in a password field.</b> That is
    /// the one assertion that matters about this whole path, and it is checked
    /// HERE — at the press — rather than borrowed from
    /// <see cref="DictationSession"/>'s check, which happens at the release and
    /// would therefore run after the audio had already reached a recogniser.
    /// </para>
    /// <para>
    /// Returns immediately; the stream opens on a background task. Nothing
    /// waits on it, because a preview that made the user's keypress feel slow
    /// would have cost more than it delivered.
    /// </para>
    /// </remarks>
    /// <returns>The run listening now, as <see cref="DictationPreviewUpdate.Run"/>
    /// will name it: a new one, or the one already running (a second start
    /// opens nothing); 0 when the preview was withheld or the session is
    /// disposed.</returns>
    public long Start(string? operationId = null)
    {
        if (_disposed)
        {
            return 0;
        }

        if (ProbeSecureField() == SecureFieldState.Secure)
        {
            _log.LogDebug(
                "Live preview withheld for operation {OperationId}: the focused field is a password box",
                operationId ?? "untracked");
            return 0;
        }

        PreviewRun run;
        lock (_sync)
        {
            if (_running)
            {
                return _run?.Number ?? 0;
            }

            _running = true;
            _operationId = string.IsNullOrWhiteSpace(operationId) ? "untracked" : operationId;
            _startedTicks = Stopwatch.GetTimestamp();
            _streamOpenedTicks = null;
            _firstAudioTicks = null;
            _firstPushTicks = null;
            _firstPartialTicks = null;
            _lastChangedTicks = null;
            _pushCount = 0;
            _partialCount = 0;
            _changedPartialCount = 0;
            _backpressuredBufferCount = 0;
            _droppedSampleCount = 0;
            _changedGapsMs.Clear();
            _lastObservedText = string.Empty;
            _lastCaption = string.Empty;
            _lastCompletedPreviewText = string.Empty;
            LastMetrics = null;
            _accumulator.Reset();
            _pending.Clear();
            _sourceRateHz = 0;
            _pushInFlight = false;
            _pushTask = null;
            _run = run = new PreviewRun(++_runCount);
            // Under the lock, so a stop racing this start sees the open it
            // must wait for. The open runs on the pool: nothing on the
            // press path waits for it.
            _openTask = Task.Run(() => OpenAsync(run));
        }

        return run.Number;
    }

    /// <summary>
    /// Hand over a buffer from the microphone. Safe to call on the capture
    /// thread, and safe to call before the stream has finished opening —
    /// audio buffers until there is somewhere to send it.
    /// </summary>
    public void Append(AudioChunk chunk)
    {
        if (_disposed || chunk is null || chunk.Samples.Length == 0
            || chunk.Channels <= 0 || chunk.SampleRateHz <= 0)
        {
            return;
        }

        PendingPush? push;
        lock (_sync)
        {
            if (!_running)
            {
                return;
            }

            _firstAudioTicks ??= Stopwatch.GetTimestamp();

            if (_sourceRateHz == 0)
            {
                _sourceRateHz = chunk.SampleRateHz;
            }
            else if (_sourceRateHz != chunk.SampleRateHz)
            {
                // Same call the tap makes: an endpoint does not change format
                // mid-capture, so this is a device misbehaving. Dropping is
                // honest; resampling two rates as one is not.
                return;
            }

            _pending.AddRange(WorkflowAudio.ToMono(chunk.Samples, chunk.Channels));
            var maximum = Math.Max(1, (int)(MaxPendingSeconds * _sourceRateHz));
            if (_pending.Count > maximum)
            {
                // The oldest goes: the caption is about what is being said
                // now, and the final pass has the whole hold anyway.
                var overflow = _pending.Count - maximum;
                _pending.RemoveRange(0, overflow);
                _droppedSampleCount += overflow;
            }

            if (_pushInFlight)
            {
                // The samples stay pending and ride along with the next push
                // rather than queueing a second.
                _backpressuredBufferCount++;
            }

            push = TakePushLocked();
        }

        push?.Send();
    }

    /// <summary>
    /// One push, taken under the lock and sent after it. Its
    /// <see cref="Done"/> is the session's <see cref="_pushTask"/> from the
    /// moment it is taken, so a stop that detaches the run in between still
    /// waits for it before it cancels the stream.
    /// </summary>
    private sealed record PendingPush(
        DictationPreviewSession Owner, string StreamId, short[] Pcm, PreviewRun Run, TaskCompletionSource Done)
    {
        public void Send() => _ = Owner.PushAsync(this);
    }

    /// <summary>
    /// Everything pending, as the next push, when there is a stream, nothing
    /// else is out, and at least <see cref="ChunkSeconds"/> of audio waits;
    /// otherwise null. Caller holds <see cref="_sync"/>.
    /// </summary>
    private PendingPush? TakePushLocked()
    {
        var need = (int)(ChunkSeconds * _sourceRateHz);
        if (!_running || _run is null || need <= 0 || _pending.Count < need || _streamId is null || _pushInFlight)
        {
            return null;
        }

        // Take everything pending, not just one chunk: if pushes fell
        // behind, the recogniser should receive the audio contiguously.
        var slice = _pending.ToArray();
        _pending.Clear();
        var pcm = WorkflowAudio.ToPcm16(WorkflowAudio.Resample(slice, _sourceRateHz));

        _pushInFlight = true;
        _pushCount++;
        _firstPushTicks ??= Stopwatch.GetTimestamp();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pushTask = done.Task;
        return new PendingPush(this, _streamId, pcm, _run, done);
    }

    /// <summary>
    /// End the preview for this hold, without waiting for the worker.
    /// </summary>
    /// <remarks>
    /// <see cref="StopAsync"/>, unawaited: for a hold that ended without a
    /// decode to follow it (a cancel, a hold too short, dictation paused).
    /// </remarks>
    public void Stop() => _ = StopAsync();

    /// <summary>
    /// End the preview for this hold: detach it, wait for an open and a push
    /// already under way, then cancel the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cancels rather than finishes, always, and for two reasons.
    /// <c>stream_finish</c> would return the streaming engine's own text and
    /// this host must never be able to insert that; and cancelling releases
    /// the worker, so the preview cannot still be decoding while the
    /// transcript the user is waiting for queues behind it.
    /// </para>
    /// <para>
    /// The run is detached before anything is awaited, so a reply that lands
    /// later puts nothing back on screen and the next hold can start its own
    /// run at once. The task completes when the stream is cancelled, or at
    /// once when there was none; it never faults. A second stop while one is
    /// under way returns that one.
    /// </para>
    /// <para>
    /// The waiting and the cancel run on the pool, never on the caller's
    /// thread and never under the session's lock: a stop comes from the UI
    /// thread (Escape, a pause) and from the release's pipeline, the capture
    /// thread's <see cref="Append"/> needs the lock, and a cancel is a frame
    /// on the worker's pipe.
    /// </para>
    /// </remarks>
    public Task StopAsync()
    {
        string? streamId;
        Task? open;
        Task? push;
        DictationPreviewMetrics metrics;
        long stoppedRun;

        lock (_sync)
        {
            if (!_running)
            {
                return _stopping;
            }

            _running = false;
            var stoppedTicks = Stopwatch.GetTimestamp();
            metrics = new DictationPreviewMetrics(
                _operationId,
                ElapsedMs(_startedTicks, stoppedTicks),
                ElapsedMs(_startedTicks, _streamOpenedTicks),
                ElapsedMs(_startedTicks, _firstAudioTicks),
                ElapsedMs(_startedTicks, _firstPushTicks),
                ElapsedMs(_startedTicks, _firstPartialTicks),
                _pushCount,
                _partialCount,
                _changedPartialCount,
                _backpressuredBufferCount,
                Percentile(_changedGapsMs, 0.50),
                Percentile(_changedGapsMs, 0.95),
                _lastObservedText.Length)
            {
                DroppedSampleCount = _droppedSampleCount,
                LastCaptionLength = _lastCaption.Length,
            };
            LastMetrics = metrics;
            _lastCompletedPreviewText = _lastObservedText;
            streamId = _streamId;
            open = _openTask;
            push = _pushTask;
            stoppedRun = _run?.Number ?? 0;
            _streamId = null;
            _run = null;
            _openTask = null;
            _pushTask = null;
            _pushInFlight = false;
            _pending.Clear();
            _accumulator.Reset();
            _lastCaption = string.Empty;
            _stopping = Task.Run(() => FinishStopAsync(streamId, open, push));
        }

        _log.LogInformation(
            "Dictation preview profile: operation {OperationId}; session {SessionMs} ms; "
            + "stream-open {StreamOpenMs} ms; first-audio {FirstAudioMs} ms; "
            + "first-push {FirstPushMs} ms; first-partial {FirstPartialMs} ms; "
            + "pushes {PushCount}; partials {PartialCount}; changed {ChangedCount}; "
            + "changed-gap p50/p95 {GapP50Ms}/{GapP95Ms} ms; backpressured buffers {Backpressure}; "
            + "dropped samples {Dropped}; preview length {PreviewLength}; caption length {CaptionLength}",
            metrics.OperationId,
            metrics.SessionMs,
            metrics.StreamOpenMs,
            metrics.FirstAudioMs,
            metrics.FirstPushMs,
            metrics.FirstPartialMs,
            metrics.PushCount,
            metrics.PartialCount,
            metrics.ChangedPartialCount,
            metrics.ChangedGapP50Ms,
            metrics.ChangedGapP95Ms,
            metrics.BackpressuredBufferCount,
            metrics.DroppedSampleCount,
            metrics.LastPreviewLength,
            metrics.LastCaptionLength);
        PreviewUpdated?.Invoke(this, new DictationPreviewUpdate(stoppedRun, string.Empty));
        lock (_sync)
        {
            return _stopping;
        }
    }

    /// <summary>The ordered half of a stop: an open still out first (a
    /// stream it opens after the stop is cancelled by
    /// <see cref="OpenAsync"/> itself), then a push on the wire, then the
    /// cancel. Never faults.</summary>
    private async Task FinishStopAsync(string? streamId, Task? open, Task? push)
    {
        try
        {
            if (open is not null)
            {
                await open.ConfigureAwait(false);
            }

            if (push is not null)
            {
                await push.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Live preview work failed while it was stopped");
        }

        if (streamId is not null)
        {
            await CancelAsync(streamId).ConfigureAwait(false);
        }
    }

    private async Task OpenAsync(PreviewRun run)
    {
        try
        {
            // Do not withdraw a frame from the shared wire at key-up. If the
            // hold ends first, the completed stream is cancelled below before
            // the accurate final pass uses the same worker.
            var id = await _transcriber.StreamStartAsync(CancellationToken.None)
                .ConfigureAwait(false);
            var cancelLateStream = false;
            PendingPush? push = null;

            lock (_sync)
            {
                if (!_running || !ReferenceEquals(_run, run) || id is null)
                {
                    // Either the hold ended while the worker was starting, or
                    // the selected Whisper model could not open a live session. Both are ordinary:
                    // dictation carries on exactly as it did before. A worker
                    // that did open after key-up still needs its stream closed.
                    if (ReferenceEquals(_run, run))
                    {
                        _streamId = null;
                    }

                    cancelLateStream = id is not null;
                }
                else
                {
                    _streamId = id;
                    _streamOpenedTicks = Stopwatch.GetTimestamp();
                    // The audio that waited for the stream goes now, as on
                    // the Mac, not with the next buffer.
                    push = TakePushLocked();
                }
            }

            push?.Send();
            if (cancelLateStream)
            {
                await CancelAsync(id!).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Live preview could not start");
        }
    }

    private async Task PushAsync(PendingPush push)
    {
        var run = push.Run;
        PendingPush? next = null;
        try
        {
            var reply = await _transcriber
                .StreamPushAsync(push.StreamId, push.Pcm, CancellationToken.None)
                .ConfigureAwait(false);

            if (reply is not { } hypothesis)
            {
                // No answer is not "no words" — leave what is on screen alone.
                return;
            }

            DictationPreviewUpdate update;
            lock (_sync)
            {
                if (!_running || !ReferenceEquals(_run, run))
                {
                    return;
                }

                _partialCount++;
                var text = _accumulator.Observe(hypothesis.Text, hypothesis.IsEndpoint);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    // What the preview heard, for the comparison with the
                    // final words.
                    _lastObservedText = text;
                }

                // The first caption and its changes are counted as the Mac
                // counts them (completePush): from the caption a presenter
                // draws, so an answer that draws nothing (no words, or a
                // lone "you") is no caption, and one that draws the same
                // words again is no change.
                var caption = DictationPreviewCaption.Window(text);
                if (caption.Length > 0)
                {
                    var now = Stopwatch.GetTimestamp();
                    _firstPartialTicks ??= now;
                    if (!string.Equals(caption, _lastCaption, StringComparison.Ordinal))
                    {
                        if (_lastChangedTicks is { } previous)
                        {
                            _changedGapsMs.Add(ElapsedMs(previous, now));
                        }
                        _lastChangedTicks = now;
                        _changedPartialCount++;
                        _lastCaption = caption;
                    }
                }

                // Tagged here, in the region that found the run current: a
                // stop and the next start can land before the event is
                // raised, and the update must still name this run.
                update = new DictationPreviewUpdate(run.Number, text);
            }

            PreviewUpdated?.Invoke(this, update);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Live preview push failed");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_run, run))
                {
                    _pushInFlight = false;
                    // The audio that arrived meanwhile goes now, as on the
                    // Mac, rather than with the next buffer.
                    next = TakePushLocked();
                }
            }

            push.Done.TrySetResult();
            next?.Send();
        }
    }

    private async Task CancelAsync(string streamId)
    {
        try
        {
            await _transcriber.StreamCancelAsync(streamId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Live preview cancel failed");
        }
    }

    private SecureFieldState ProbeSecureField()
    {
        if (_secureField is null)
        {
            return SecureFieldState.Unknown;
        }

        try
        {
            return _secureField.Probe();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The password-field probe threw; treating the field as ordinary");
            return SecureFieldState.Unknown;
        }
    }

    /// <summary>Compare the stopped preview with the accurate result.</summary>
    public DictationPreviewComparison? CompareLastToFinal(string? finalText)
    {
        string preview;
        lock (_sync)
        {
            preview = _lastCompletedPreviewText;
        }

        if (string.IsNullOrWhiteSpace(preview) || string.IsNullOrWhiteSpace(finalText))
        {
            return null;
        }

        var normalizedPreview = NormalizeForComparison(preview);
        var normalizedFinal = NormalizeForComparison(finalText);
        if (normalizedPreview.Length == 0 || normalizedFinal.Length == 0)
        {
            return null;
        }

        var distance = EditDistance(normalizedPreview, normalizedFinal);
        var agreement = 1.0 - (double)distance
            / Math.Max(normalizedPreview.Length, normalizedFinal.Length);
        return new DictationPreviewComparison(
            Math.Clamp(agreement, 0.0, 1.0),
            preview.Length,
            finalText.Length);
    }

    private static long ElapsedMs(long start, long end) =>
        (long)Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private static long? ElapsedMs(long start, long? end) =>
        end is { } value ? ElapsedMs(start, value) : null;

    private static long? Percentile(List<long> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.Order().ToArray();
        var index = (int)Math.Round((ordered.Length - 1) * percentile);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static string NormalizeForComparison(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(rune.ToString().ToLower(CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    private static int EditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return previous[^1];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
