using Microsoft.Extensions.Logging;

namespace Dictation.Speech;

/// <summary>How an attempt to turn a hold into words ended.</summary>
public enum TranscriptionOutcome
{
    /// <summary>The recognizer returned words, and they are in the result.</summary>
    Transcribed,

    /// <summary>
    /// The recognizer ran and heard nothing worth returning — silence, or a
    /// decode the worker dropped on purpose. The microphone was fine.
    /// </summary>
    NoSpeech,

    /// <summary>
    /// There is no recognizer to ask: the worker would not start, or would not
    /// initialise. A machine fact, not an utterance fact.
    /// </summary>
    RecognizerUnavailable,

    /// <summary>
    /// The recognizer was there and the attempt failed anyway — a bad reply, a
    /// timeout, or a worker that died mid-utterance.
    /// </summary>
    RecognizerFailed,
}

/// <summary>One hold's worth of audio, as words.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Text">
/// The transcript, and only when <paramref name="Outcome"/> is
/// <see cref="TranscriptionOutcome.Transcribed"/>. Every other outcome carries
/// <c>null</c> rather than an empty string — the same discipline
/// <see cref="MicrophoneTapResult"/> keeps, and for the same reason: a caller
/// must not be able to read "the recognizer is not running" as "they said
/// nothing".
/// </param>
/// <param name="Latency">
/// How long the decode took, measured on this side by
/// <see cref="AsrWorkerTranscriber"/>: from encoding the audio for the wire to
/// the reply landing. So it is the whole wait — base64, IPC, and whatever the
/// worker queued in front of the decode — and deliberately not the worker's own
/// <c>stats.dec_ms</c>, which excludes everything the operator is standing
/// there for. It excludes the model load, which is not a typical wait, and the
/// insertion that follows, which is measured by whoever performs it.
/// <para>
/// <c>null</c> unless a decode actually ran and answered, which is the same
/// discipline <paramref name="Text"/> keeps: a hold that never reached the wire,
/// and one that timed out, have no duration to report. A caller that filled
/// those in with zero would put a fabricated "0 ms" in a statistic the operator
/// reads as a typical wait — worse than showing them nothing.
/// </para>
/// </param>
/// <param name="RanOnNpu">
/// Whether the recognizer that answered had loaded on the NPU, as the worker
/// itself reported at init — not as this app asked for.
/// <see cref="TranscriberOptions.UseQnn"/> is the request, and the worker falls
/// back to CPU on its own when the context binaries are missing, so the two
/// disagree on exactly the machine where the claim would be false.
/// <c>null</c> means no worker has been asked yet or it did not say, which a
/// surface has to render as unknown rather than as CPU.
/// </param>
/// <param name="DetectedLanguage">
/// The Whisper language code the decode actually ran under, as the worker
/// reported it in <c>stats.language</c> — <c>en</c>, <c>zh</c>, <c>ko</c>. Not
/// what was requested: nothing is requested by default
/// (<see cref="TranscriberOptions.Language"/> is null for auto-detect), so this
/// is the model's own decision.
/// <para>
/// Here for the same reason <paramref name="RanOnNpu"/> is: the bridge was
/// dropping a fact the worker had already sent, and the fact is the difference
/// between a baffling defect and an obvious one. On 2026-08-19 a hold with no
/// speech in it typed <c>날씨였습니다.</c> into a document; the worker had said
/// <c>ko</c> the whole time and nothing on this side kept it. A surface that
/// can say "that came back as Korean" turns that into a one-line diagnosis.
/// </para>
/// <para>
/// <c>null</c> means no decode ran, or one ran and emitted no language token at
/// all — which is what silence does on the greedy path. As with the two fields
/// above, unknown must not be rendered as a language.
/// </para>
/// </param>
/// <param name="NBest">
/// The recogniser's alternative hypotheses, texts only, best first, as the
/// worker reported them (CPL Setup 6a carries them; Setup 2 put them on the
/// wire). The edit classifier's strongest signal is "was the corrected form
/// among these", so they ride the result to the history line, where a later
/// correction can find them. <c>null</c> is "not measured" — an older worker,
/// or a greedy decode, which reports none — and must never be read as "the
/// recogniser offered nothing". Capped by the history store, not here: the
/// bridge carries what the worker said.
/// </param>
/// <param name="AvgLogprob">
/// Token-weighted mean raw log-probability of the decode, the worker's own
/// number. <c>null</c> is "not measured", same rule as <paramref name="NBest"/>.
/// </param>
public sealed record TranscriptionResult(
    TranscriptionOutcome Outcome,
    string? Text,
    TimeSpan? Latency = null,
    bool? RanOnNpu = null,
    string? DetectedLanguage = null,
    IReadOnlyList<string>? NBest = null,
    double? AvgLogprob = null,
    IReadOnlyList<AsrFiredPhrase>? Fired = null,
    string? EncoderId = null,
    string? BaselineText = null)
{
    /// <summary>The shape of "no recognizer", named so call sites do not each
    /// spell it out.</summary>
    public static TranscriptionResult Unavailable { get; } =
        new(TranscriptionOutcome.RecognizerUnavailable, null);
}

/// <summary>
/// Audio in, words out. Implemented against the shipped speech worker by
/// <see cref="AsrWorkerTranscriber"/>, and by <see cref="NullTranscriber"/>
/// when there is no worker to talk to.
/// </summary>
/// <remarks>
/// The interface takes a <see cref="MicrophoneCapture"/> rather than a byte
/// array so the format travels with the audio: the worker rejects anything that
/// is not 16 kHz mono, and a bridge that accepted loose bytes would find that
/// out one IPC round trip later.
/// </remarks>
public interface ITranscriber : IAsyncDisposable, IDisposable
{
    // See IAsrWorkerProcess for why both: the container is disposed
    // synchronously at app exit, and async-only singletons throw there.

    /// <summary>
    /// Turn one captured hold into text. Never throws for an ordinary failure —
    /// every one of those is an outcome, because this is called from the
    /// release of a key on the UI thread.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(MicrophoneCapture capture, CancellationToken ct);

    /// <summary>
    /// Whether the loaded recognizer is running on the NPU, as it reported at
    /// init. <c>null</c> until one has been asked, and after one has gone away.
    /// </summary>
    /// <remarks>
    /// Here as well as on <see cref="TranscriptionResult.RanOnNpu"/> because a
    /// pane that wants to say "runs on this computer's NPU" has to be able to
    /// ask before anyone has dictated anything — and it must ask this rather
    /// than <see cref="TranscriberOptions.UseQnn"/>, which is only the request.
    /// Defaulted to "unknown" so an implementation with no worker behind it is
    /// not forced to claim a hardware fact it cannot know; a double that
    /// answered <c>false</c> would be asserting CPU on no evidence.
    /// </remarks>
    bool? RunsOnNpu => null;
}

/// <summary>
/// The recognizer that is not there. Registered when the speech worker cannot
/// be located on this machine, so the rest of the chain composes and reports
/// <see cref="TranscriptionOutcome.RecognizerUnavailable"/> instead of the
/// graph failing to build.
/// </summary>
/// <remarks>
/// The house <c>Null&lt;Interface&gt;</c> pattern —
/// <c>NullAuthEventReporter</c> is the reference. It logs at Debug and does
/// nothing else.
/// </remarks>
public sealed class NullTranscriber : ITranscriber
{
    private readonly ILogger<NullTranscriber> _logger;

    public NullTranscriber(ILogger<NullTranscriber> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<TranscriptionResult> TranscribeAsync(
        MicrophoneCapture capture, CancellationToken ct)
    {
        _logger.LogDebug(
            "No speech worker configured; discarding {Duration:0.##}s of audio",
            capture?.Duration.TotalSeconds ?? 0);

        return Task.FromResult(TranscriptionResult.Unavailable);
    }

    /// <summary>Unknown, not <c>false</c> — there is no worker here that could
    /// have failed to reach the NPU.</summary>
    public bool? RunsOnNpu => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Nothing was ever opened, so there is nothing to close — but it
    /// still has to exist, or a synchronous container dispose throws.</summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// The knobs on the bridge. Written here rather than inline so the two picks
/// that want an owner's eye — the beam size and the timeouts — are one edit
/// each.
/// </summary>
/// <param name="InitTimeout">
/// How long the worker gets to load a model. Whisper on a cold QNN context is
/// the slow case the detection pipeline already budgets 1–77 s for.
/// </param>
/// <param name="TranscribeTimeout">
/// Base time one utterance gets in addition to its audio duration. The client
/// uses <c>TranscribeTimeout + audio.Duration</c>, so long-form dictation gets
/// a proportional wire budget while a stuck worker remains bounded.
/// </param>
/// <param name="BeamSize">
/// Above 1, the worker runs beam search with the bias phrases applied during
/// decoding rather than after it. That is the whole reason the user's
/// vocabulary is sent with every transcribe frame at all (see
/// <see cref="AsrWorkerTranscriber"/>).
/// </param>
/// <param name="ModelDirectory">Where the worker looks for model files. Null
/// leaves the worker's own default in place.</param>
/// <param name="HuggingFaceId">Which Whisper to load.</param>
/// <param name="UseQnn">
/// Ask for the NPU path. The worker falls back to CPU by itself when the
/// context binaries are missing, and reports which one it took.
/// </param>
/// <param name="Language">
/// Which language to decode as — a Whisper code, <c>en</c> / <c>zh</c>. Null,
/// the default, is auto-detect.
/// <para>
/// The worker has accepted this since it was written and this host never sent
/// it, so every utterance was auto-detected. That is what let
/// 1.0 s of quiet hiss come back as Korean on 2026-08-19: whisper-base's
/// language head is weakest on exactly the short, quiet audio a mis-fired hold
/// produces.
/// </para>
/// <para>
/// The default stays null on purpose. Whisper does not fail on a language
/// mismatch, it TRANSLATES, so pinning <c>en</c> would give a bilingual user
/// fluent English they never said — a worse defect than the one being fixed,
/// and one that looks like a correct transcript. Pinning is a choice a user
/// makes, not a default this record makes for them.
/// </para>
/// </param>
/// <param name="AllowModelDownload">
/// Whether the worker may fetch model files it does not find locally. Null
/// sends nothing and leaves the worker's own default, which allows it.
/// Dictation passes <c>false</c>: its model is a local directory, and the
/// worker's offline check (weights, config and tokenizer all present, or a
/// refusal naming what is missing) only runs when the init frame says so.
/// </param>
public sealed record TranscriberOptions(
    TimeSpan InitTimeout,
    TimeSpan TranscribeTimeout,
    int BeamSize,
    string? ModelDirectory,
    string HuggingFaceId,
    bool UseQnn,
    string? Language = "en",
    string? Backend = null,
    string? FallbackModelDirectory = null,
    string? FallbackHuggingFaceId = null,
    bool LearningOps = false,
    bool CompareBaseline = false,
    bool? AllowModelDownload = null)
{
    /// <summary>
    /// Whether this options set names a second model to fall back to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rollback for a bad model payload, and it costs no reinstall. Set by
    /// a composition root that stages a second model beside the chosen one
    /// (the standalone Dictation app stages one model and leaves it unset).
    /// <see cref="AsrWorkerTranscriber"/> uses it exactly once per
    /// process: if <c>init</c> refuses for the chosen model, it re-points at
    /// this one and asks again, and dictation carries on.
    /// </para>
    /// <para>
    /// Why it exists: <c>whisper-large-v3-turbo-qnn</c> is a 2.2 GB payload,
    /// 1.76 GB of it a single QAIRT context binary, and it takes 3.7 GB of
    /// working set to load against base's 0.57 GB. A truncated download, a
    /// QAIRT version bump, or a machine that cannot spare the memory all end
    /// at the same place — a worker whose init says no — and before this the
    /// user's only recovery was to find the model picker, which they cannot
    /// reach if the answer they get is "there is no speech recogniser to ask".
    /// </para>
    /// </remarks>
    public bool HasFallbackModel => FallbackModelDirectory is { Length: > 0 };

    /// <summary>
    /// 90 s to initialise, 15 s to decode, beam 4, <c>openai/whisper-base</c>
    /// on the NPU. The loop's picks.
    /// </summary>
    /// <remarks>
    /// Beam 4 rather than the worker's greedy default because greedy ignores
    /// <c>bias_phrases</c> entirely: the phrases are applied during beam
    /// search, so beam 1 would send a vocabulary the decoder never consults.
    /// 90 s mirrors the detection pipeline's cold-QNN budget; 15 s is three
    /// times the longest hold anyone rehearses.
    /// <para>
    /// No language, i.e. auto-detect — see <see cref="Language"/> for why that
    /// is not "en". Worth knowing while reading the beam 4 above it: those two
    /// picks interact. Beam 4 is the shipped configuration, and it is the one
    /// where a weakly-detected language is least constrained, so auto-detect
    /// and beam search are the pair that produced Korean out of silence.
    /// </para>
    /// </remarks>
    public static readonly TranscriberOptions Default = new(
        InitTimeout: TimeSpan.FromSeconds(90),
        TranscribeTimeout: TimeSpan.FromSeconds(15),
        BeamSize: 4,
        ModelDirectory: null,
        HuggingFaceId: "openai/whisper-base",
        UseQnn: true,
        // Auto-detect, stated rather than inherited: a reader who finds this
        // line has to see that no language was chosen for them, and a later
        // edit that pins one has to do it here where the reasoning is.
        Language: "en");

    /// <summary>
    /// Whether this is the on-device NPU engine — the only one the staged
    /// Whisper payload is for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two engines mean opposite things by a model directory, and giving
    /// one the other's is fatal rather than ignored. <c>qnn</c> loads
    /// <c>encoder.onnx</c> and <c>decoder.onnx</c> out of a directory it must
    /// be handed, and reads a saved tokenizer beside them. The portable engine
    /// passes both straight to faster-whisper, which takes an <em>existing</em>
    /// directory or a size name — <c>base</c>, <c>small</c>, <c>turbo</c> — and
    /// raises <c>ValueError: Invalid model size</c> on a path that is not
    /// there.
    /// </para>
    /// <para>
    /// Which is what a defaulted staged path did on x64: every hold came back
    /// <see cref="TranscriptionOutcome.RecognizerUnavailable"/> from an engine
    /// that works perfectly when left to resolve <see cref="HuggingFaceId"/>
    /// itself. So the staged payload is applied only when this is true.
    /// </para>
    /// <para>
    /// A null backend counts: it leaves the worker its own default, which is
    /// <c>qnn</c> everywhere except macOS.
    /// </para>
    /// </remarks>
    public bool UsesStagedNpuPayload =>
        Backend is null
        || string.Equals(Backend, TranscriberBackends.Qnn, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The engines <c>asr_server.py</c> will load, spelled the way it spells them.
/// </summary>
/// <remarks>
/// Constants rather than an enum because these are wire values: an enum would
/// invite a rename, and a renamed backend is one the worker answers
/// <c>unknown backend</c> to.
/// </remarks>
public static class TranscriberBackends
{
    /// <summary>Snapdragon. Whisper on the Hexagon NPU through QAIRT.</summary>
    /// <remarks>Its module imports <c>onnxruntime_qnn</c> at module scope, so
    /// asking for it on any other machine fails at init, not at decode.</remarks>
    public const string Qnn = "qnn";

    /// <summary>
    /// The portable CPU recognizer, despite the name — MLX, else
    /// faster-whisper, else openai-whisper, first import wins.
    /// </summary>
    public const string Portable = "mac";

    /// <summary>A fixed sentence, no model, no download. What the bridge's own
    /// tests drive.</summary>
    public const string Stub = "stub";
}
