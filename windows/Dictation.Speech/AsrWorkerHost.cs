using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Dictation.Speech;

/// <summary>
/// Who is asking for the wire. The gate in <see cref="AsrWorkerHost"/> is a
/// PRIORITY gate: every <see cref="Interactive"/> waiter enters before any
/// <see cref="Background"/> waiter, regardless of arrival order, and the host
/// withdraws an in-flight background frame when interactive work arrives.
/// </summary>
/// <remarks>
/// Dictation holds, the preview stream and the learning probes are
/// interactive — a person is standing there, or the probe was scheduled into
/// an idle window that is about to close. Meeting-notes work is background:
/// the accurate pass at meeting stop can wait a hold out, and a live-caption
/// push is skip-on-busy anyway (<see cref="IAsrWire.TryExchangeAsync"/>).
/// the worker contract §2 principle 3.
/// </remarks>
public enum AsrWirePriority
{
    Interactive = 0,
    Background = 1,
}

/// <summary>How one exchange on the wire ended.</summary>
public enum AsrExchangeStatus
{
    /// <summary>A reply frame was read. It may still say <c>ok:false</c>.</summary>
    Replied,

    /// <summary>There is no worker to ask: it would not start, or <c>init</c>
    /// was refused (and the one permitted demotion, if any, was spent).</summary>
    Unavailable,

    /// <summary>The worker was asked and did not answer — a timeout, or a
    /// clean EOF. The host has forgotten the worker; the next exchange
    /// re-spawns and re-inits.</summary>
    NoReply,

    /// <summary>
    /// The exchange failed. <see cref="AsrExchange{TReply}.Fault"/> carries the
    /// exception.
    /// </summary>
    /// <remarks>
    /// <b>Two causes, and the worker's fate differs between them.</b> A broken
    /// pipe mid-exchange forgets the worker — the stream is desynchronised and
    /// nothing on it can be trusted again. An <see cref="AsrReplyShapeException"/>
    /// does NOT: the frame arrived whole, so the pipe is still aligned and only
    /// this request is lost. That distinction exists because one worker is
    /// shared by dictation, voice workflows and secondary capture, and a reply that
    /// did not match its contract must not take the other two down.
    /// </remarks>
    Faulted,

    /// <summary>Skip-on-busy only: the gate was held and nothing was sent.</summary>
    Busy,
}

/// <summary>One request's outcome on the wire.</summary>
public readonly record struct AsrExchange<TReply>(
    AsrExchangeStatus Status,
    TReply? Reply,
    Exception? Fault = null)
    where TReply : class
{
    public static AsrExchange<TReply> Unavailable => new(AsrExchangeStatus.Unavailable, null);
    public static AsrExchange<TReply> NoReply => new(AsrExchangeStatus.NoReply, null);
    public static AsrExchange<TReply> Busy => new(AsrExchangeStatus.Busy, null);
}

/// <summary>
/// The one pipe to the one speech worker, as its clients see it: a gate,
/// a lazily-initialised worker, and request/reply framing. Implemented by
/// <see cref="AsrWorkerHost"/>; clients (<see cref="AsrWorkerTranscriber"/>)
/// format ops and parse replies and never spawn, init or gate anything
/// themselves.
/// </summary>
public interface IAsrWire
{
    /// <summary>Whether the loaded recogniser is on the NPU, as the worker
    /// reported at init; null until one has answered or after it went away.</summary>
    bool? RunsOnNpu { get; }

    /// <summary>Initialised and alive right now. A hint, not a lease: the
    /// worker can exit between this read and the next frame.</summary>
    bool IsReady { get; }

    /// <summary>The model directory the worker is actually running (after any
    /// demotion), or the configured one before init. For log lines.</summary>
    string? ModelDirectory { get; }

    /// <summary>
    /// Spawn + <c>init</c> now, on nobody's clock, under interactive priority.
    /// Swallows every failure (the next exchange retries and reports).
    /// Idempotent.
    /// </summary>
    Task WarmUpAsync(CancellationToken ct = default);

    /// <summary>
    /// One request, one reply, under <paramref name="timeout"/>, after the
    /// worker is known to be initialised. Waits for the gate at
    /// <paramref name="priority"/>. An interactive exchange preempts an
    /// in-flight background exchange and restarts the worker on a clean pipe.
    /// Throws only for the caller's own cancellation; every worker-side
    /// failure is a status.
    /// </summary>
    Task<AsrExchange<TReply>> ExchangeAsync<TRequest, TReply>(
        TRequest request,
        TimeSpan timeout,
        AsrWirePriority priority,
        CancellationToken ct,
        string phase = "this request")
        where TReply : class;

    /// <summary>
    /// Skip-on-busy: send only if the gate is free RIGHT NOW and the worker
    /// is already initialised; otherwise <see cref="AsrExchangeStatus.Busy"/>
    /// (gate held) or <see cref="AsrExchangeStatus.Unavailable"/> (no worker)
    /// without touching the wire. Never initialises — a push is not the op
    /// that should pay a model load.
    /// </summary>
    Task<AsrExchange<TReply>> TryExchangeAsync<TRequest, TReply>(
        TRequest request,
        TimeSpan timeout,
        CancellationToken ct)
        where TReply : class;
}

/// <summary>
/// A one-lease gate with two queues. Release hands the lease to the oldest
/// interactive waiter, else the oldest background waiter, else frees it.
/// </summary>
/// <remarks>
/// <para>
/// Not a <see cref="SemaphoreSlim"/> because a semaphore is FIFO and the whole
/// point is that it must not be: a dictation hold arriving behind three queued
/// notes ops would wait out three NPU decodes. Two queues and an explicit
/// hand-off is the smallest thing that gives the ordering, and it is provable
/// without a worker (<c>AsrWorkerHostTests</c>).
/// </para>
/// <para>
/// Hand-off rather than wake-and-race: the releasing thread marks the lease
/// as still held and completes the waiter's task, so a background waiter that
/// happened to be scheduled first cannot slip in ahead of the interactive one
/// it was queued behind.
/// </para>
/// </remarks>
public sealed class AsrPriorityGate : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _interactive = new();
    private readonly Queue<Waiter> _background = new();
    private bool _held;
    private bool _disposed;

    private sealed class Waiter
    {
        public readonly TaskCompletionSource<bool> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Whether the lease is out. For tests and log lines.</summary>
    public bool IsHeld
    {
        get { lock (_sync) { return _held; } }
    }

    /// <summary>How many are queued at each priority. For tests.</summary>
    public (int Interactive, int Background) Queued
    {
        get { lock (_sync) { return (_interactive.Count, _background.Count); } }
    }

    /// <summary>Take the lease if it is free right now; never waits.</summary>
    public bool TryEnter()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_held)
            {
                return false;
            }

            _held = true;
            return true;
        }
    }

    /// <summary>Wait for the lease at <paramref name="priority"/>.</summary>
    public async Task EnterAsync(AsrWirePriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_held)
            {
                _held = true;
                return;
            }

            waiter = new Waiter();
            (priority == AsrWirePriority.Interactive ? _interactive : _background).Enqueue(waiter);
        }

        using var registration = ct.CanBeCanceled
            ? ct.Register(static (state, token) => ((Waiter)state!).Tcs.TrySetCanceled(token), waiter)
            : default;

        // A cancelled waiter is left in its queue and skipped by Release,
        // which sees TrySetResult fail. Draining it eagerly would need a
        // second pass over the queue for no observable difference.
        await waiter.Tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Wait for the lease up to <paramref name="timeout"/>; false when it did
    /// not arrive. For the bounded wait a shutdown makes.
    /// </summary>
    public async Task<bool> EnterAsync(AsrWirePriority priority, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await EnterAsync(priority, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>Give the lease back — to the next waiter, or to nobody.</summary>
    public void Release()
    {
        lock (_sync)
        {
            if (!_held || _disposed)
            {
                // Released twice, or released after Dispose. Neither has
                // anyone left to hand to, and neither is worth an exception on
                // a shutdown path.
                _held = false;
                return;
            }

            while (true)
            {
                var queue = _interactive.Count > 0 ? _interactive
                    : _background.Count > 0 ? _background
                    : null;

                if (queue is null)
                {
                    _held = false;
                    return;
                }

                var next = queue.Dequeue();
                if (next.Tcs.TrySetResult(true))
                {
                    // Hand-off: _held stays true, the waiter owns it now.
                    return;
                }

                // That one was cancelled while queued; try the next.
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var w in _interactive)
            {
                w.Tcs.TrySetException(new ObjectDisposedException(nameof(AsrPriorityGate)));
            }

            foreach (var w in _background)
            {
                w.Tcs.TrySetException(new ObjectDisposedException(nameof(AsrPriorityGate)));
            }

            _interactive.Clear();
            _background.Clear();
        }
    }
}

/// <summary>
/// The singleton that owns the speech worker: ONE <see cref="IAsrWorkerProcess"/>,
/// ONE <c>init</c> (with the one permitted model demotion), and the priority
/// gate every client's frames go through.
/// </summary>
/// <remarks>
/// <para>
/// the worker contract §2: dictation and secondary capture used to each spawn an
/// <c>asr_server</c> with its own backend context (+3.7 GB, two NPU contexts).
/// Everything that decides WHICH worker and WHEN it is initialised now lives
/// here, once; <see cref="AsrWorkerTranscriber"/> is a thin client over
/// <see cref="IAsrWire"/>.
/// </para>
/// <para>
/// The init frame carries only <c>{backend, model_dir, hf_id, use_qnn}</c>
/// and, when the options set it, <c>allow_model_download</c>.
/// Nothing process-global: every adaptation knob (bias phrases, per-phrase
/// deltas, personal prior, text passes) travels per request under a
/// <c>client</c> tag, so notes can never inherit dictation's vocabulary by
/// sharing the process (§2 principle 4).
/// </para>
/// <para>
/// Lifecycle and error handling are those of the former
/// <c>AsrWorkerTranscriber</c>, moved: lazy spawn on first use, a dead worker
/// re-spawned on the next frame, a timed-out frame desynchronising the pipe
/// and therefore forgetting the worker, a refused init demoted ONCE to the
/// fallback model, and sync/async disposal that never holds app exit open.
/// </para>
/// </remarks>
public sealed class AsrWorkerHost : IAsrWire, IDisposable, IAsyncDisposable
{
    private readonly IAsrWorkerProcess _worker;
    private readonly TranscriberOptions _options;
    private readonly ILogger _logger;
    private readonly AsrPriorityGate _gate = new();
    private readonly object _preemptionGate = new();
    private CancellationTokenSource? _backgroundExchange;

    private bool _initialized;
    private bool _disposed;
    private bool _demoted;
    private string? _modelDirectory;
    private string? _huggingFaceId;

    // Tri-state as an int for the same reason the transcriber kept it that
    // way: read from the UI thread while a decode is in flight elsewhere.
    private const int NpuUnknown = -1;
    private const int NpuNo = 0;
    private const int NpuYes = 1;
    private int _npu = NpuUnknown;

    /// <summary>How many <c>init</c> frames this host has sent. One per
    /// worker lifetime is the contract; a test asserts it.</summary>
    public int InitCount { get; private set; }

    public AsrWorkerHost(IAsrWorkerProcess worker, TranscriberOptions options, ILogger logger)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modelDirectory = options.ModelDirectory;
        _huggingFaceId = options.HuggingFaceId;
    }

    /// <summary>The options this host was built from — the one model identity.</summary>
    public TranscriberOptions Options => _options;

    /// <summary>The gate, exposed for tests that assert ordering.</summary>
    public AsrPriorityGate Gate => _gate;

    /// <inheritdoc />
    public bool? RunsOnNpu => Volatile.Read(ref _npu) switch
    {
        NpuYes => true,
        NpuNo => false,
        _ => null,
    };

    /// <inheritdoc />
    public bool IsReady => !_disposed && _initialized && _worker.IsAlive;

    /// <inheritdoc />
    public string? ModelDirectory => _modelDirectory;

    /// <inheritdoc />
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _gate.EnterAsync(AsrWirePriority.Interactive, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled before the gate arrived, or disposed under us. The app
            // has somewhere better to be.
            return;
        }

        try
        {
            await EnsureInitializedAsync(ct, "the warm-up").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown withdrew mid-init. Not a failure to report.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Speech worker warm-up failed; the first hold pays the cold start instead");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<AsrExchange<TReply>> ExchangeAsync<TRequest, TReply>(
        TRequest request,
        TimeSpan timeout,
        AsrWirePriority priority,
        CancellationToken ct,
        string phase = "this request")
        where TReply : class
    {
        if (_disposed)
        {
            return AsrExchange<TReply>.Unavailable;
        }

        // Priority must apply to the request already ON the wire as well as to
        // queued requests. Meeting finalization can spend minutes in one
        // diarized_transcribe frame; merely putting an interactive dictation
        // at the front of the queue still leaves the person holding the key in
        // silence for those minutes. Withdraw the background frame. Its caller
        // retains the meeting scratch/rolling transcript, while the worker is
        // restarted below so the interactive request cannot inherit a late
        // reply from the abandoned frame.
        if (priority == AsrWirePriority.Interactive)
        {
            CancelBackgroundExchange();
        }

        try
        {
            await _gate.EnterAsync(priority, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return AsrExchange<TReply>.Unavailable;
        }

        CancellationTokenSource? background = null;
        CancellationToken requestToken = ct;
        if (priority == AsrWirePriority.Background)
        {
            background = CancellationTokenSource.CreateLinkedTokenSource(ct);
            requestToken = background.Token;
            lock (_preemptionGate)
            {
                _backgroundExchange = background;
                // Close the tiny acquire-to-publication race: an interactive
                // caller may have queued after this background request took
                // the lease but before its cancellation source became visible.
                if (_gate.Queued.Interactive > 0)
                {
                    background.Cancel();
                }
            }
        }

        try
        {
            if (!await EnsureInitializedAsync(requestToken, phase).ConfigureAwait(false))
            {
                return AsrExchange<TReply>.Unavailable;
            }

            return await SendUnderGateAsync<TRequest, TReply>(request, timeout, requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The request frame is already on the pipe. Abandoning only the
            // read would leave its eventual reply for the next caller, and a
            // long background diarization would still occupy the process.
            // Reset immediately so a live-caption start gets a clean worker.
            ForgetWorker();
            try
            {
                await _worker.StopAsync(TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* cancellation still releases the gate */ }
            throw;
        }
        catch (OperationCanceledException) when (background?.IsCancellationRequested == true)
        {
            ForgetWorker();
            _logger.LogInformation(
                "Background speech request was preempted by interactive voice typing; restarting worker");
            try
            {
                await _worker.StopAsync(TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch { /* the interactive request still gets the gate and retries */ }
            return AsrExchange<TReply>.Unavailable;
        }
        catch (Exception ex)
        {
            ForgetWorker();
            return new AsrExchange<TReply>(AsrExchangeStatus.Faulted, null, ex);
        }
        finally
        {
            if (background is not null)
            {
                lock (_preemptionGate)
                {
                    if (ReferenceEquals(_backgroundExchange, background))
                    {
                        _backgroundExchange = null;
                    }
                }
                background.Dispose();
            }
            _gate.Release();
        }
    }

    private void CancelBackgroundExchange()
    {
        lock (_preemptionGate)
        {
            try { _backgroundExchange?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <inheritdoc />
    public async Task<AsrExchange<TReply>> TryExchangeAsync<TRequest, TReply>(
        TRequest request,
        TimeSpan timeout,
        CancellationToken ct)
        where TReply : class
    {
        if (_disposed)
        {
            return AsrExchange<TReply>.Unavailable;
        }

        bool entered;
        try
        {
            entered = _gate.TryEnter();
        }
        catch (ObjectDisposedException)
        {
            return AsrExchange<TReply>.Unavailable;
        }

        if (!entered)
        {
            return AsrExchange<TReply>.Busy;
        }

        try
        {
            if (!_initialized || !_worker.IsAlive)
            {
                return AsrExchange<TReply>.Unavailable;
            }

            return await SendUnderGateAsync<TRequest, TReply>(request, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ForgetWorker();
            return new AsrExchange<TReply>(AsrExchangeStatus.Faulted, null, ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Write one frame, read one frame. Caller holds the gate and
    /// has ensured init. Throws on a broken pipe (the caller forgets the
    /// worker); returns NoReply on timeout or EOF (this method forgets it).</summary>
    private async Task<AsrExchange<TReply>> SendUnderGateAsync<TRequest, TReply>(
        TRequest request, TimeSpan timeout, CancellationToken ct)
        where TReply : class
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await AsrFraming
                .WriteFrameAsync(_worker.StandardIn, request, timeoutCts.Token)
                .ConfigureAwait(false);

            var reply = await AsrFraming
                .ReadFrameAsync<TReply>(_worker.StandardOut, timeoutCts.Token)
                .ConfigureAwait(false);

            if (reply is null)
            {
                // EOF: the worker closed stdout. The next frame starts a
                // fresh one.
                ForgetWorker();
                return AsrExchange<TReply>.NoReply;
            }

            return new AsrExchange<TReply>(AsrExchangeStatus.Replied, reply);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A frame that timed out has desynchronised the pipe: the next
            // read would be THIS reply. Forgetting the logical init flag is
            // not enough: StartAsync deliberately reuses a live child, which
            // would let a retry read this late reply and leave its own reply
            // queued behind it. Stop the physical process so the next frame
            // really does get a new pipe and a new worker.
            ForgetWorker();
            _logger.LogWarning(
                "Speech worker did not answer within {Timeout:0.#}s", timeout.TotalSeconds);
            try
            {
                await _worker.StopAsync(TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Timed-out speech worker could not be stopped; the next request may not recover");
            }
            return AsrExchange<TReply>.NoReply;
        }
        catch (AsrReplyShapeException ex)
        {
            // Degrade the REQUEST, and deliberately NOT the worker. The frame
            // arrived whole, so unlike the timeout above the pipe is still
            // aligned and the next op proceeds on the same process.
            //
            // Returning rather than rethrowing is the entire fix: a rethrow
            // reaches this class's outer catch-all, which calls ForgetWorker()
            // and takes down a worker that dictation and voice workflows SHARE
            // with secondary capture. One notes reply that did not match its
            // contract used to stop the user's dictation working.
            _logger.LogWarning(
                ex, "Speech worker sent a reply this request could not read; the worker is kept");
            return new AsrExchange<TReply>(AsrExchangeStatus.Faulted, null, ex);
        }
    }

    private void ForgetWorker()
    {
        _initialized = false;
        Volatile.Write(ref _npu, NpuUnknown);
    }

    /// <summary>
    /// Start the worker and send <c>init</c> if that has not already happened,
    /// or has stopped being true. Caller holds the gate.
    /// </summary>
    /// <param name="phase">Which caller is asking, in the words the log should
    /// use — "this hold", "the warm-up", "a learning probe", "the meeting
    /// transcript". The warm-up and the first real hold take this same path
    /// and used to produce byte-identical error lines seconds apart.</param>
    private async Task<bool> EnsureInitializedAsync(CancellationToken ct, string phase)
    {
        if (_initialized && _worker.IsAlive)
        {
            return true;
        }

        if (_initialized)
        {
            _logger.LogWarning("Speech worker exited; restarting it for {Phase}", phase);
            ForgetWorker();
        }

        var coldStart = Stopwatch.StartNew();
        try
        {
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(_options.InitTimeout);
            await _worker.StartAsync(startCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speech worker would not start for {Phase}", phase);
            return false;
        }

        // {backend, model_dir, hf_id, use_qnn, allow_model_download} and
        // nothing else. No
        // default_bias_phrases, no db_path: adaptation is per request now,
        // and an init-time list could never be unsent (the worker contract §3).
        var request = new AsrInitRequest
        {
            Backend = _options.Backend,
            ModelDir = _modelDirectory,
            HuggingFaceId = _huggingFaceId,
            UseQnn = _options.UseQnn,
            AllowModelDownload = _options.AllowModelDownload,
        };

        AsrResponse? reply;
        InitCount++;
        try
        {
            var exchange = await SendUnderGateAsync<AsrInitRequest, AsrResponse>(
                request, _options.InitTimeout, ct).ConfigureAwait(false);
            reply = exchange.Reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speech worker init failed at the wire for {Phase}", phase);
            return false;
        }

        if (reply is null || !reply.Ok)
        {
            _logger.LogError(
                "Speech worker init failed for {Phase}: {Error} (model={ModelDir}, backend={Backend}, qnn_requested={UseQnn})",
                phase,
                reply?.Error ?? "no reply",
                _modelDirectory ?? "(the worker's own default)",
                _options.Backend,
                _options.UseQnn);

            // THE ROLLBACK: one demotion, here, where a refused init is
            // already known. Once per process — a second attempt would be at
            // the model that just refused, and a loop of refused inits turns
            // a bad payload into a hang. The worker process is still alive
            // (asr_server keeps serving after a failed init) so the recursive
            // call re-sends init at the fallback without a respawn.
            if (!_demoted && _options.HasFallbackModel)
            {
                _demoted = true;
                _logger.LogWarning(
                    "Falling back from {Failed} to {Fallback} for the rest of this run — "
                    + "the chosen model would not initialise. Dictation and meeting notes "
                    + "continue on the fallback; no reinstall is needed, and Settings can pin "
                    + "the choice permanently.",
                    _modelDirectory ?? "(the worker's own default)",
                    _options.FallbackModelDirectory);

                _modelDirectory = _options.FallbackModelDirectory;
                _huggingFaceId = _options.FallbackHuggingFaceId ?? _options.HuggingFaceId;

                return await EnsureInitializedAsync(ct, phase).ConfigureAwait(false);
            }

            return false;
        }

        _initialized = true;
        Volatile.Write(ref _npu, reply.OnNpu switch
        {
            true => NpuYes,
            false => NpuNo,
            _ => NpuUnknown,
        });

        coldStart.Stop();
        _logger.LogInformation(
            "Speech worker ready in {ColdMs} ms — spawn + model init, paid once per worker lifetime "
            + "(backend={Backend} qnn_requested={UseQnn} on_npu={OnNpu}, model={ModelDir}, "
            + "tokenizer={Tokenizer}; shared by dictation, voice workflows and meeting notes; "
            + "vocabulary travels per request)",
            coldStart.ElapsedMilliseconds,
            _options.Backend ?? "(the worker's own default)",
            _options.UseQnn,
            reply.OnNpu,
            _modelDirectory ?? "(the worker's own default)",
            _huggingFaceId ?? "(the worker's own default)");

        return true;
    }

    /// <summary>
    /// The synchronous close, for the container dispose at app exit. Kills
    /// the worker without waiting on the gate — a wedged decode must not hold
    /// the exit open.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _worker.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech worker did not close cleanly on exit");
        }

        ForgetWorker();
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var gateHeld = await _gate
            .EnterAsync(AsrWirePriority.Interactive, _options.TranscribeTimeout, CancellationToken.None)
            .ConfigureAwait(false);

        try
        {
            if (gateHeld && _initialized && _worker.IsAlive)
            {
                try
                {
                    await SendUnderGateAsync<AsrShutdownRequest, AsrResponse>(
                        new AsrShutdownRequest(), _options.TranscribeTimeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Speech worker shutdown op failed; killing instead");
                }
            }

            await _worker
                .StopAsync(_options.TranscribeTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech worker did not stop cleanly");
        }
        finally
        {
            ForgetWorker();
            if (gateHeld)
            {
                _gate.Release();
            }

            _gate.Dispose();
        }
    }
}
