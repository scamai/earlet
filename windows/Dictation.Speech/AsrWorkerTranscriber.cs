using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Dictation.Speech;

/// <summary>
/// PCM as the worker wants it on the wire: base64 of little-endian int16.
/// </summary>
public static class AsrPcm
{
    /// <summary>
    /// Encode 16-bit samples, writing each one little-endian explicitly rather
    /// than reinterpreting the array's memory.
    /// </summary>
    /// <remarks>
    /// A span cast would be host byte order, which is right on every machine
    /// this ships to and wrong on the one that eventually runs the tests. The
    /// worker does <c>np.frombuffer(..., dtype=np.int16)</c>, which is
    /// little-endian by declaration, so writing it explicitly costs a loop and
    /// removes an assumption.
    /// </remarks>
    public static string ToBase64(ReadOnlySpan<short> pcm)
    {
        var bytes = new byte[pcm.Length * 2];

        for (var i = 0; i < pcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
        }

        return System.Convert.ToBase64String(bytes);
    }
}

/// <summary>
/// workflows P-04 — the recognizer bridge: a held key's audio, sent to the
/// speech worker, and the words it sends back.
///
/// <para>
/// Since the worker contract this is a CLIENT of <see cref="IAsrWire"/>: it
/// formats <c>transcribe</c>, the learning ops and the preview-stream ops,
/// parses their replies, and owns nothing else. The process, the one
/// <c>init</c>, the priority gate and the model demotion live in
/// <see cref="AsrWorkerHost"/>, which dictation, voice workflows and meeting
/// notes share. Holds and learning probes enter the gate at
/// <see cref="AsrWirePriority.Interactive"/>, ahead of queued notes work.
/// </para>
///
/// <para>
/// Everything here is <c>net9.0</c> on purpose. Framing, JSON, base64 and the
/// outcome table are the parts that can be wrong in a way a test would catch;
/// spawning a Python interpreter is behind <see cref="IAsrWorkerProcess"/>.
/// </para>
///
/// <para>
/// Every decode frame this client sends carries <c>client:"dictation"</c>,
/// <c>text_passes:true</c>, the WHOLE bias list for that utterance and, when
/// composed with one, the personal <c>profile</c>. Nothing is process-global
/// in the worker any more, so a notes decode on the same worker inherits none
/// of it (§2 principle 4).
/// </para>
/// </summary>
public sealed class AsrWorkerTranscriber : ITranscriber, IPreviewTranscriber
{
    private readonly IAsrWire _wire;

    /// <summary>The host this instance built for itself over a bare process
    /// (the legacy constructor), and therefore owns and disposes. Null when
    /// the wire was injected — then the container owns the host.</summary>
    private readonly AsrWorkerHost? _ownedHost;

    private readonly TranscriberOptions _options;
    private readonly ImmutableArray<string> _biasPhrases;

    /// <summary>
    /// The CURRENT phrase list, asked once per utterance, or <c>null</c> when
    /// the caller has no live source and the composition-time list is the whole
    /// answer.
    /// </summary>
    /// <remarks>
    /// Without one, <see cref="_biasPhrases"/> is sent as-is, which suits a
    /// list fixed for the life of the process. Dictation passes one, because
    /// the user's vocabulary is a file they edit while the app is running.
    /// Either way the list goes out on EVERY transcribe frame and is the whole
    /// list — the init frame carries none (it used to, and a word the user
    /// REMOVED kept biasing until the process restarted).
    /// </remarks>
    private readonly Func<ImmutableArray<string>>? _currentBiasPhrases;

    /// <summary>
    /// The per-phrase δ for the list about to be sent, or <c>null</c> when
    /// nothing in the product assigns one. Given the list ACTUALLY being sent
    /// and answering in that list's indices, because the wire contract is
    /// index-keyed and the list is rebuilt per utterance.
    /// </summary>
    private readonly Func<IReadOnlyList<string>, IReadOnlyDictionary<int, double>?>? _currentPhraseDeltas;

    /// <summary>The personal prior to stamp on every decode, or null for none.</summary>
    private readonly AsrProfile? _profile;

    /// <summary>
    /// The recognition language asked once per utterance, or <c>null</c> to
    /// keep <see cref="TranscriberOptions.Language"/> for the life of this
    /// instance. Dictation passes one: the user can change the picker's
    /// language while the worker stays loaded, and the engine takes the
    /// language per request — nothing about it is process-global — so a change
    /// must reach the NEXT decode without a model reload. The delegate's null
    /// means automatic detection, exactly as a null <c>Language</c> does.
    /// </summary>
    private readonly Func<string?>? _currentLanguage;

    private readonly string _client;
    private readonly ILogger<AsrWorkerTranscriber> _logger;
    private bool _disposed;

    /// <summary>
    /// The composition-root constructor: a client over the shared wire.
    /// </summary>
    /// <param name="wire">The one wire to the one worker (<see cref="AsrWorkerHost"/>).</param>
    /// <param name="options">Per-request knobs — beam, language, timeouts, learning
    /// ops. The init-level fields (model, backend) are the HOST's; they are
    /// read here only for log lines.</param>
    /// <param name="profile">The personal prior for this client's decodes, or
    /// null. Dictation passes one; voice workflows and a test container pass none.</param>
    /// <param name="client">The <c>client</c> tag; <see cref="AsrClients.Dictation"/>
    /// for every consumer of this class today.</param>
    /// <param name="currentLanguage">The language to send, asked once per
    /// utterance, or null to keep <paramref name="options"/>'s. Lets a live
    /// language change reach the next decode with no worker restart.</param>
    public AsrWorkerTranscriber(
        IAsrWire wire,
        TranscriberOptions options,
        ImmutableArray<string> biasPhrases,
        ILogger<AsrWorkerTranscriber> logger,
        Func<ImmutableArray<string>>? currentBiasPhrases = null,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<int, double>?>? currentPhraseDeltas = null,
        AsrProfile? profile = null,
        string client = AsrClients.Dictation,
        Func<string?>? currentLanguage = null)
    {
        _wire = wire ?? throw new ArgumentNullException(nameof(wire));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _biasPhrases = biasPhrases;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _currentBiasPhrases = currentBiasPhrases;
        _currentPhraseDeltas = currentPhraseDeltas;
        _profile = profile;
        _client = string.IsNullOrWhiteSpace(client) ? AsrClients.Dictation : client;
        _currentLanguage = currentLanguage;
    }

    /// <summary>
    /// A self-contained transcriber over a bare process: builds and OWNS an
    /// <see cref="AsrWorkerHost"/>. The shape the protocol tests drive, and
    /// the shape a tool that wants one worker for itself would use.
    /// </summary>
    public AsrWorkerTranscriber(
        IAsrWorkerProcess worker,
        TranscriberOptions options,
        ImmutableArray<string> biasPhrases,
        ILogger<AsrWorkerTranscriber> logger,
        Func<ImmutableArray<string>>? currentBiasPhrases = null,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<int, double>?>? currentPhraseDeltas = null,
        Func<string?>? currentLanguage = null)
        : this(
            new AsrWorkerHost(
                worker ?? throw new ArgumentNullException(nameof(worker)),
                options ?? throw new ArgumentNullException(nameof(options)),
                logger ?? throw new ArgumentNullException(nameof(logger))),
            options,
            biasPhrases,
            logger,
            currentBiasPhrases,
            currentPhraseDeltas,
            currentLanguage: currentLanguage)
    {
        _ownedHost = (AsrWorkerHost)_wire;
    }

    /// <summary>The wire this client speaks over. For composition roots that
    /// want to hand the same one to another client.</summary>
    public IAsrWire Wire => _wire;

    /// <summary>
    /// The wire form of the per-phrase δ for <paramref name="phrases"/>, or
    /// <c>null</c> to omit the field. Never throws: a δ source that fails
    /// costs the boost, not the dictation.
    /// </summary>
    internal Dictionary<string, double>? CurrentPhraseDeltas(string[]? phrases)
    {
        if (phrases is null || phrases.Length == 0 || _currentPhraseDeltas is null) return null;

        IReadOnlyDictionary<int, double>? deltas;
        try
        {
            deltas = _currentPhraseDeltas(phrases);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Per-phrase bias could not be resolved; sending the uniform delta");
            return null;
        }

        if (deltas is null || deltas.Count == 0) return null;

        Dictionary<string, double>? wire = null;
        foreach (var (index, delta) in deltas)
        {
            if (index < 0 || index >= phrases.Length || !double.IsFinite(delta)) continue;
            wire ??= new Dictionary<string, double>(deltas.Count);
            // Clamped to the worker's own [-10, 6] (PHRASE_DELTA_MIN/MAX):
            // a value the wire carries should be one the decoder is safe
            // with, whichever side is the older build.
            wire[index.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Math.Clamp(delta, -10.0, 6.0);
        }
        return wire;
    }

    /// <summary>
    /// The phrase list for the frame about to be sent, or <c>null</c> to omit
    /// the field entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live source when there is one, else the composition-time list.
    /// Null for an empty answer from either: sending <c>[]</c> would be
    /// indistinguishable in effect and would put an empty array on the wire
    /// for every utterance of every install that has no vocabulary.
    /// </para>
    /// <para>
    /// Deliberately does NOT catch around the live source. A live source is
    /// documented never to throw (Dictation passes none); swallowing here would
    /// hide a source that broke that contract.
    /// </para>
    /// </remarks>
    private string[]? CurrentBiasPhrases()
    {
        if (_currentBiasPhrases is null)
        {
            return _biasPhrases.IsDefaultOrEmpty ? null : _biasPhrases.ToArray();
        }

        var phrases = _currentBiasPhrases();
        return phrases.IsDefaultOrEmpty ? null : phrases.ToArray();
    }

    /// <inheritdoc />
    public bool? RunsOnNpu => _disposed ? null : _wire.RunsOnNpu;

    /// <summary>
    /// The wire budget for one hold's decode. One extra second per second of
    /// audio is the 1x-realtime floor, so a slow worker on long
    /// (tap-to-toggle) audio fails late and honestly instead of being
    /// declared dead at 15 s; with <see cref="TranscriberOptions.CompareBaseline"/>
    /// the worker decodes the clip TWICE, so the audio is counted twice
    /// (P6a F7).
    /// </summary>
    public static TimeSpan TranscribeTimeoutFor(TranscriberOptions options, TimeSpan audio)
    {
        ArgumentNullException.ThrowIfNull(options);

        var timeout = options.TranscribeTimeout + audio;
        return options.CompareBaseline ? timeout + audio : timeout;
    }

    /// <summary>
    /// The budget for the one retry after a decode whose worker died or
    /// missed <see cref="TranscribeTimeoutFor"/>: two extra seconds per second
    /// of audio, so a worker that was merely slow on a long take finishes
    /// this time. The Dictation app keeps the audio and retries once with it
    /// before it offers the take for recovery (LIMIT 2026-10-10).
    /// </summary>
    public static TimeSpan RetryTimeoutFor(TranscriberOptions options, TimeSpan audio) =>
        TranscribeTimeoutFor(options, audio) + audio;

    public Task<TranscriptionResult> TranscribeAsync(MicrophoneCapture capture, CancellationToken ct)
        => TranscribeWithSceneAsync(capture, ct, null);

    /// <param name="capture">The hold's audio.</param>
    /// <param name="ct">The caller's withdrawal.</param>
    /// <param name="scene">The vocabulary scene, or null.</param>
    /// <param name="timeout">The wire budget, or null for
    /// <see cref="TranscribeTimeoutFor"/>: a retry passes
    /// <see cref="RetryTimeoutFor"/>.</param>
    public async Task<TranscriptionResult> TranscribeWithSceneAsync(
        MicrophoneCapture capture, CancellationToken ct, string? scene, TimeSpan? timeout = null)
    {
        if (_disposed)
        {
            return TranscriptionResult.Unavailable;
        }

        if (capture is null || capture.Pcm.Length == 0)
        {
            // Empty audio feature-extracts to 30 s of padding and invites a
            // hallucinated sentence. Not sending it is cheaper than trusting
            // the guard on the far side of an IPC call.
            _logger.LogDebug("Nothing to transcribe");
            return new TranscriptionResult(TranscriptionOutcome.NoSpeech, null);
        }

        if (capture.SampleRateHz != WorkflowAudio.SampleRateHz
            || capture.Channels != WorkflowAudio.Channels)
        {
            _logger.LogError(
                "Refusing to transcribe {Rate} Hz / {Channels}ch audio; the worker takes {Expected} Hz mono",
                capture.SampleRateHz,
                capture.Channels,
                WorkflowAudio.SampleRateHz);

            return new TranscriptionResult(TranscriptionOutcome.RecognizerFailed, null);
        }

        // Read ONCE and used for both fields: the δ map is indexed into
        // this exact array, and a second read of a file the user is
        // editing could return a different list.
        var biasPhrases = CurrentBiasPhrases();

        var request = new AsrTranscribeRequest
        {
            PcmBase64 = AsrPcm.ToBase64(capture.Pcm),
            SampleRate = capture.SampleRateHz,
            BeamSize = _options.BeamSize,
            Client = _client,
            TextPasses = true,
            Profile = _profile,

            // Null by default, which the worker reads as auto-detect — see
            // TranscriberOptions.Language for why that is not "en". Asked
            // live when composed with a source, so a language change reaches
            // this decode without a worker restart; its null means automatic,
            // not "fall back to the composition-time value".
            Language = _currentLanguage is { } languageSource ? languageSource() : _options.Language,

            // The user's vocabulary as it is at this instant — the whole list.
            BiasPhrases = biasPhrases,
            VocabularyScene = scene is "terminal" or "wechat" or "other" ? scene : null,

            // The Boost rung's per-phrase δ (CPL setup 6b), resolved against
            // the list above. Omitted unless a correction has been learned.
            PhraseDeltas = CurrentPhraseDeltas(biasPhrases),

            // loop-v2: only when the learning loop is on. Null keeps the
            // frame byte-identical to a build without the loop.
            ReturnFired = _options.LearningOps ? true : null,
            KeepEncoder = _options.LearningOps ? true : null,

            // Only when asked (Dictation never asks).
            // Absent otherwise, so the frame is byte-for-byte yesterday's.
            CompareBaseline = _options.CompareBaseline ? true : null,
        };

        var budget = timeout ?? TranscribeTimeoutFor(_options, capture.Duration);

        // The clock starts here, not around the init the wire may perform:
        // a cold model load folded into "typical wait" would move the median
        // by two orders of magnitude on every first hold.
        var started = Stopwatch.GetTimestamp();

        AsrExchange<AsrResponse> exchange;
        try
        {
            exchange = await _wire
                .ExchangeAsync<AsrTranscribeRequest, AsrResponse>(
                    request, budget, AsrWirePriority.Interactive, ct, "this hold")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller withdrew — the app is shutting down mid-hold.
            throw;
        }

        var latency = Stopwatch.GetElapsedTime(started);

        // Read once, here, so every outcome below carries the same answer.
        var onNpu = RunsOnNpu;

        switch (exchange.Status)
        {
            case AsrExchangeStatus.Unavailable:
                // RecognizerUnavailable is the same outcome whether the worker
                // binary is missing, crashed on start, or failed its init
                // handshake — the host's error line says which. This line pins
                // the failure to the hold: the user really did speak for this
                // long. Whether the recording is kept is the caller's (the
                // Dictation app keeps it for recovery).
                _logger.LogWarning(
                    "There is no speech recogniser for this hold, so {Duration:0.##}s of captured "
                    + "audio was not decoded — the worker could not be started or "
                    + "initialised (the error above says which; model={ModelDir})",
                    capture.Duration.TotalSeconds,
                    _wire.ModelDirectory ?? "(the worker's own default)");
                return TranscriptionResult.Unavailable;

            case AsrExchangeStatus.NoReply:
                _logger.LogWarning(
                    "Speech worker did not answer within {Budget:0.#}s; this decode has no words",
                    budget.TotalSeconds);
                // No latency: what was measured is how long the timeout took
                // to expire, and a "typical wait" built from those would
                // report the ceiling as the norm.
                return new TranscriptionResult(
                    TranscriptionOutcome.RecognizerFailed, null, null, onNpu);

            case AsrExchangeStatus.Faulted:
                _logger.LogError(exchange.Fault, "Speech worker exchange failed");
                return new TranscriptionResult(TranscriptionOutcome.RecognizerFailed, null);

            case AsrExchangeStatus.Busy:
                // Not possible on a waiting exchange; named so the switch is
                // exhaustive rather than defaulted.
                return new TranscriptionResult(TranscriptionOutcome.RecognizerFailed, null);
        }

        var reply = exchange.Reply!;
        var language = Language(reply);

        if (!reply.Ok)
        {
            _logger.LogError("Speech worker refused the utterance: {Error}", reply.Error);
            // Refused, not decoded — timing it would measure the refusal.
            return new TranscriptionResult(
                TranscriptionOutcome.RecognizerFailed, null, null, onNpu);
        }

        var text = reply.Text?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            // Two different silences, told apart only here: DroppedReason is
            // the HOST's decision, GateExplanation is the WORKER's.
            _logger.LogInformation(
                "Speech final transcript completed: client={Client} engine=whisper model={Model} "
                + "on_npu={OnNpu} outcome=no_speech audio_s={Duration:0.##} chars=0 "
                + "elapsed_ms={ElapsedMs:0} wait_ms={WaitedMs} language={Language}{Reason}{Gate}",
                _client,
                _wire.ModelDirectory ?? "(the worker's own default)",
                onNpu,
                capture.Duration.TotalSeconds,
                latency.TotalMilliseconds,
                reply.Stats?.WaitedMilliseconds,
                language ?? "unknown",
                reply.DroppedReason is { Length: > 0 } dropped ? $" (dropped: {dropped})" : string.Empty,
                reply.Stats?.GateExplanation() is { Length: > 0 } gate ? $" ({gate})" : string.Empty);

            // Timed even though there are no words: the operator waited
            // exactly as long as they would have for a transcript.
            return new TranscriptionResult(
                TranscriptionOutcome.NoSpeech, null, latency, onNpu, language);
        }

        _logger.LogInformation(
            "Speech final transcript completed: client={Client} engine=whisper model={Model} "
            + "on_npu={OnNpu} outcome=text audio_s={Duration:0.##} chars={Length} "
            + "elapsed_ms={ElapsedMs:0} wait_ms={WaitedMs} language={Language}",
            _client,
            _wire.ModelDirectory ?? "(the worker's own default)",
            onNpu,
            capture.Duration.TotalSeconds,
            text.Length,
            latency.TotalMilliseconds,
            reply.Stats?.WaitedMilliseconds,
            language ?? "unknown");

        var nbest = NBestTexts(reply.NBest);
        var avgLogprob = reply.AvgLogprob is { } lp && double.IsFinite(lp) ? lp : (double?)null;

        // The stock decode of the same clip, only when the worker says the
        // unbiased pass ran (or, on a worker that predates `baseline_ran`,
        // whenever it sent a non-empty baseline). An empty string is kept
        // as "" rather than null so the log can show that the stock decode
        // heard nothing — distinct from "no comparison was made".
        string? baseline = null;
        if (reply.BaselineText is { } bt && (reply.BaselineRan ?? bt.Length > 0))
        {
            baseline = bt.Trim();
        }

        return new TranscriptionResult(
            TranscriptionOutcome.Transcribed, text, latency, onNpu, language, nbest, avgLogprob,
            reply.Fired, reply.EncoderId, baseline);
    }

    /// <summary>
    /// Pay the spawn + model init now, on nobody's clock — so the first hold
    /// finds a warm worker instead of financing an 11-second cold start.
    /// Delegates to the shared host: idempotent, failures swallowed, the
    /// next hold retries on the cold path and reports.
    /// </summary>
    public Task WarmUpAsync(CancellationToken ct = default) =>
        _disposed ? Task.CompletedTask : _wire.WarmUpAsync(ct);

    /// <summary>
    /// The hypothesis texts, in the worker's order, blanks dropped. Null when
    /// the worker sent no array — "not measured" — and ALSO when it sent an
    /// empty one, because an empty N-best is what a greedy decode reports.
    /// </summary>
    public static IReadOnlyList<string>? NBestTexts(AsrHypothesis[]? hypotheses)
    {
        if (hypotheses is null || hypotheses.Length == 0)
        {
            return null;
        }

        var texts = new List<string>(hypotheses.Length);
        foreach (var h in hypotheses)
        {
            var t = h?.Text?.Trim();
            if (!string.IsNullOrEmpty(t))
            {
                texts.Add(t);
            }
        }

        return texts.Count == 0 ? null : texts;
    }

    private static string? Language(AsrResponse? reply) =>
        reply?.Stats?.Language is { Length: > 0 } language && language.Trim().Length > 0
            ? language.Trim()
            : null;

    /// <summary>
    /// loop-v2: a <c>score</c> / <c>score_release</c> /
    /// <c>rescore</c> frame through the same gate a hold takes, at the same
    /// priority, so a probe and a hold can never interleave frames and a
    /// queued notes op never delays either. Returns null when the worker did
    /// not answer in time or is unavailable; the caller treats that as
    /// "verifier unavailable", never as a crash.
    /// </summary>
    /// <remarks>
    /// Stamps this client's <c>profile</c> (and <c>text_passes</c> on a
    /// rescore) onto a request that left them null, so the learning ops see
    /// the same adaptation the hold saw. <c>client</c> is already
    /// <c>dictation</c> by the records' defaults — the ops are dictation-only
    /// by contract.
    /// </remarks>
    public async Task<TReply?> ProbeAsync<TRequest, TReply>(
        TRequest request, TimeSpan timeout, CancellationToken ct)
        where TReply : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        object frame = request!;
        frame = frame switch
        {
            AsrScoreRequest s when s.Profile is null && _profile is not null => s with { Profile = _profile },
            AsrRescoreRequest r => r with
            {
                Profile = r.Profile ?? _profile,
                TextPasses = r.TextPasses ?? true,
            },
            _ => frame,
        };

        var exchange = await _wire
            .ExchangeAsync<object, TReply>(frame, timeout, AsrWirePriority.Interactive, ct, "a learning probe")
            .ConfigureAwait(false);

        switch (exchange.Status)
        {
            case AsrExchangeStatus.Replied:
                return exchange.Reply;
            case AsrExchangeStatus.NoReply:
                _logger.LogWarning(
                    "Speech worker did not answer a learning probe within {Timeout:0.#}s", timeout.TotalSeconds);
                return null;
            case AsrExchangeStatus.Faulted:
                _logger.LogError(exchange.Fault, "Speech worker probe exchange failed");
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The synchronous close, for the container dispose at app exit. Closes
    /// the host only when this instance built it; an injected wire belongs
    /// to the container.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ownedHost?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownedHost is not null)
        {
            await _ownedHost.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Live-preview stream API ──────────────────────────────────────────
    //
    // Every failure below is a null or a silent return rather than an
    // exception, on purpose — a preview that cannot start must cost the
    // preview and nothing else.

    /// <summary>How long a preview op may hold the wire — a deadlock ceiling
    /// against a measured 0–2.6 ms, not a budget.</summary>
    private static readonly TimeSpan StreamPushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Open a live-preview stream, or return <c>null</c> when this build cannot.
    /// </summary>
    public Task<string?> StreamStartAsync(CancellationToken ct = default) =>
        StreamStartAsync(scene: null, ct);

    /// <summary>
    /// Open a dictation live-preview stream for a hold going into
    /// <paramref name="scene"/>'s app, or return <c>null</c> when this build
    /// cannot. Never throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame is the Mac's (<c>PythonAsrTransport.startPreview</c>): engine
    /// <c>whisper</c>, client <c>dictation</c>, 16 kHz, <c>rolling_stable</c>
    /// false (the mutable tail is what a dictation caption wants; stable-window
    /// commits are a meeting presentation), the scene, the bias list, and the
    /// languages (<see cref="PreviewLanguages"/>): the pinned one, or Chinese
    /// and English for Automatic. Without them the worker detects the language
    /// on every caption decode, which on faster-whisper is a second encoder
    /// pass each time.
    /// </para>
    /// <para>
    /// It reuses the one model the worker already loaded for the final pass —
    /// CTranslate2 Whisper on the CPU in the Windows app — and never a second
    /// recogniser: a failed Whisper start stays a visible failure. The
    /// worker's engine lock keeps the released hold's decode ahead of queued
    /// caption decodes.
    /// </para>
    /// </remarks>
    /// <param name="scene">The learning scene of the app the words go into,
    /// or null for the global vocabulary.</param>
    public async Task<string?> StreamStartAsync(string? scene, CancellationToken ct)
    {
        if (_disposed)
        {
            return null;
        }

        try
        {
            var exchange = await _wire.ExchangeAsync<AsrStreamStartRequest, AsrResponse>(
                new AsrStreamStartRequest
                {
                    Engine = AsrStreamStartRequest.WhisperEngine,
                    SampleRate = 16000,
                    Client = _client,
                    BiasPhrases = CurrentBiasPhrases(),
                    Languages = PreviewLanguages(_currentLanguage is { } languageSource ? languageSource() : _options.Language),
                    RollingStable = false,
                    VocabularyScene = scene is "terminal" or "wechat" or "other" ? scene : null,

                    // Deliberately no model_dir. The shared worker's init owns
                    // the single selected Whisper model identity.
                },
                _options.TranscribeTimeout,
                AsrWirePriority.Interactive,
                ct,
                "the live preview").ConfigureAwait(false);

            var reply = exchange.Reply;
            if (reply is null || !reply.Ok || string.IsNullOrEmpty(reply.StreamId))
            {
                _logger.LogDebug(
                    "Live preview unavailable: {Error}", reply?.Error ?? exchange.Status.ToString());
                return null;
            }

            _logger.LogInformation(
                "Dictation live preview started: stream={Stream} engine={Engine} "
                + "model={Model} on_npu={OnNpu}",
                reply.StreamId,
                reply.Engine ?? "unknown",
                _wire.ModelDirectory ?? "(the worker's own default)",
                RunsOnNpu);
            return reply.StreamId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live preview could not start");
            return null;
        }
    }

    /// <summary>
    /// The languages a dictation preview stream accepts for
    /// <paramref name="language"/> (a Whisper code, or null for automatic
    /// detection): that one code, trimmed and in lower case, or Chinese and
    /// English for Automatic — the Mac's <c>previewLanguages</c>. A singleton
    /// lets the worker pin every short
    /// caption window instead of trusting Whisper's per-window detection.
    /// </summary>
    public static string[] PreviewLanguages(string? language) =>
        string.IsNullOrWhiteSpace(language) ? ["zh", "en"] : [language.Trim().ToLowerInvariant()];

    /// <summary>
    /// Hand over the audio captured since the last push and take whatever
    /// hypothesis is ready, or <c>null</c> when there is nothing new.
    /// </summary>
    /// <remarks>
    /// Skip-on-busy, never queued — if a transcribe holds the gate the
    /// preview skips this tick; queueing behind it would deliver a hypothesis
    /// about audio the user finished speaking a second ago. The caller keeps
    /// the unsent audio in its bounded buffer, so the next accepted push
    /// carries the backlog without letting stale requests queue on the wire.
    /// </remarks>
    public async Task<PreviewHypothesis?> StreamPushAsync(
        string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrEmpty(streamId))
        {
            return null;
        }

        try
        {
            var exchange = await _wire.TryExchangeAsync<AsrStreamPushRequest, AsrResponse>(
                new AsrStreamPushRequest
                {
                    StreamId = streamId,
                    PcmBase64 = AsrPcm.ToBase64(pcm.Span),
                    // The local user, as the Mac sends it: the worker's
                    // default, named so the frame says which side it is.
                    Channel = "mic",
                },
                StreamPushTimeout,
                ct).ConfigureAwait(false);

            // IsEndpoint defaults to false when the worker omitted it: an
            // engine that does not segment simply never closes one.
            // `reply.Text` is the NOTES rendering — every committed line, each
            // tagged with its speaker. A dictation hold has one speaker and
            // this side already accumulates, so take the live tail only. See
            // PreviewText.
            return exchange.Reply is { Ok: true } reply
                ? new PreviewHypothesis(PreviewText.LiveTail(reply.Text), reply.IsEndpoint ?? false)
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live preview push failed");
            return null;
        }
    }

    /// <summary>
    /// Abandon a preview stream. Always cancel, never finish — see
    /// <see cref="AsrStreamCancelRequest"/>.
    /// </summary>
    public async Task StreamCancelAsync(string streamId, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrEmpty(streamId))
        {
            return;
        }

        try
        {
            await _wire.ExchangeAsync<AsrStreamCancelRequest, AsrResponse>(
                new AsrStreamCancelRequest { StreamId = streamId },
                StreamPushTimeout,
                AsrWirePriority.Interactive,
                ct,
                "the live preview").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live preview could not be cancelled cleanly");
        }
    }
}
