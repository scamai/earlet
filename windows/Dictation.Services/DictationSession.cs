using Microsoft.Extensions.Logging;
using Dictation.Speech;

namespace Dictation.Services;

/// <summary>
/// Where one dictation is, right now.
/// </summary>
/// <remarks>
/// <para>
/// Five states rather than a <c>bool IsDictating</c>, because the three busy
/// states fail differently and the UI has to say which one it is in: a stuck
/// <see cref="Transcribing"/> is a worker problem, a stuck
/// <see cref="Inserting"/> is a focus problem, and telling a user "dictation is
/// busy" for either is how a bug report arrives with nothing in it.
/// </para>
/// <para>
/// <see cref="Idle"/> and <see cref="Armed"/> are also kept apart deliberately.
/// Both are "not dictating", but only one of them will respond to the hotkey,
/// and the difference is the entire content of "why did nothing happen when I
/// pressed it".
/// </para>
/// </remarks>
public enum DictationState
{
    /// <summary>Not listening and not armed — the hotkey is not claimed.</summary>
    Idle,

    /// <summary>Hotkey claimed, waiting for a press.</summary>
    Armed,

    /// <summary>The key is down and the microphone is open.</summary>
    Listening,

    /// <summary>The key is up and the recognizer has the audio.</summary>
    Transcribing,

    /// <summary>There are words, and they are going into the focused window.</summary>
    Inserting,
}

/// <summary>
/// How one dictation ended.
/// </summary>
/// <remarks>
/// Flat rather than nested, and every stage's failures are kept distinct even
/// where the user-visible advice is the same. <see cref="NoSpeech"/> and
/// <see cref="HoldTooShort"/> both mean "nothing was written", but one is a
/// microphone that heard silence and the other is a key brushed on the way
/// past, and a single "nothing heard" would have thrown that away.
/// </remarks>
public enum DictationOutcome
{
    /// <summary>
    /// Deliberately first, so <c>default</c> is not a success. Same reasoning
    /// as <see cref="FocusedInsertionOutcome.InsertionRefused"/> sitting at
    /// zero: a defaulted struct that reads as "we typed it" is a lie that
    /// compiles.
    /// </summary>
    Cancelled = 0,

    /// <summary>Text reached the focused window and the clipboard was restored.</summary>
    Inserted = 1,

    /// <summary>The hold was too brief to be a sentence.</summary>
    HoldTooShort = 2,

    /// <summary>The microphone would not open.</summary>
    MicrophoneUnavailable = 3,

    /// <summary>The microphone opened and delivered nothing.</summary>
    NoAudio = 4,

    /// <summary>The recognizer ran and heard nothing worth returning.</summary>
    NoSpeech = 5,

    /// <summary>There is no recognizer to ask.</summary>
    RecognizerUnavailable = 6,

    /// <summary>The recognizer was there and the attempt failed.</summary>
    RecognizerFailed = 7,

    /// <summary>There were words, and no window to put them in.</summary>
    NoFocusedWindow = 8,

    /// <summary>There were words and a window, and the paste did not land.</summary>
    InsertionRefused = 9,

    /// <summary>
    /// The text landed and the user's clipboard did not come back. Separate
    /// from <see cref="Inserted"/> because the dictation worked and something
    /// the user owns was still lost, and one enum value cannot say both.
    /// </summary>
    ClipboardRestoreFailed = 10,

    /// <summary>
    /// The cursor was in a password field, so nothing was stored, copied or
    /// typed. Found at the release, nothing was transcribed either; found at
    /// the paste (<see cref="DictationSessionResult.SecureFieldAtPaste"/>), the
    /// words were dropped there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own outcome and not folded into <see cref="Cancelled"/> or
    /// <see cref="InsertionRefused"/>, because the sentence the user needs is
    /// completely different. "The paste did not land" reads as a broken app;
    /// "you are in a password field" reads as an app that noticed. macOS's
    /// <c>SecureInputMonitor</c> cites the North Star on exactly this — silent
    /// failure here is a trust-killer.
    /// </para>
    /// <para>
    /// <b>Not a delivered outcome</b>, so
    /// <see cref="DictationSessionResult.TextWasDelivered"/> is false, so the
    /// session carries no <c>Text</c> and no <c>Audio</c> and the recorder
    /// writes no WAV. That is structural rather than a rule somebody has to
    /// remember at the call site.
    /// </para>
    /// </remarks>
    SecureField = 11,

    /// <summary>
    /// The words were a workflow, and it ran. Nothing was typed and nothing
    /// was meant to be.
    /// </summary>
    /// <remarks>
    /// Not a delivered outcome — <see cref="DictationSessionResult.TextWasDelivered"/>
    /// is false — so the session carries no text and the history records
    /// nothing. "Send Casey a message" is a thing somebody did, not a
    /// sentence they wrote, and a dictation history full of commands would be
    /// a log of instructions nobody can reuse.
    /// </remarks>
    RanWorkflow = 13,

    /// <summary>
    /// A router took the words as addressed to it and no command answered.
    /// Nothing was typed.
    /// </summary>
    /// <remarks>
    /// Its own outcome rather than <see cref="NoSpeech"/> or a silent
    /// <see cref="Inserted"/>, because it is the one failure a shared key
    /// introduces and the user has to be able to see it. Somebody who misspoke
    /// a command was giving an instruction, not writing; typing that into
    /// their document would be worse than doing nothing, and doing nothing
    /// without saying so would look like a broken microphone.
    /// </remarks>
    WorkflowNotMatched = 14,

    /// <summary>
    /// Windows is refusing this application the microphone — the per-app switch
    /// under Privacy &amp; security is off, or an administrative policy denies
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own outcome and not folded into
    /// <see cref="MicrophoneUnavailable"/>, for the same reason
    /// <see cref="SecureField"/> is not folded into
    /// <see cref="InsertionRefused"/>: the sentence the user needs is
    /// completely different, and it is the difference between an app that
    /// failed and an app that noticed. "The microphone would not open" reads as
    /// a broken feature; "Windows is blocking the microphone" reads as
    /// something the user can go and change — and on a managed machine the
    /// per-app switch being off is the likeliest first-run failure there is.
    /// </para>
    /// <para>
    /// macOS has drawn this line since the beginning:
    /// <c>DictationAvailability.needsMicrophone</c> and
    /// <c>microphoneBusy(owner:)</c> are named states with their own copy and
    /// their own deep link, while Windows collapsed every microphone problem
    /// into one pill.
    /// </para>
    /// <para>
    /// Reached only when the probe is CERTAIN. An unreadable or absent consent
    /// store answers <c>Unknown</c> and the outcome stays
    /// <see cref="MicrophoneUnavailable"/> — see
    /// <see cref="MicrophoneAccess.Unknown"/> for why that direction and not
    /// the other.
    /// </para>
    /// </remarks>
    MicrophoneBlocked = 12,

    /// <summary>
    /// The words were a delete command, and Dictation's last insertion was erased.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a delivered outcome</b>, so no text and no audio reach the
    /// history. The phrase itself is never typed and never recorded: a history
    /// full of "scratch that" rows would be a junk line every single time the
    /// feature worked, and the words were an instruction rather than something
    /// the user wrote.
    /// </para>
    /// <para>
    /// Its own outcome rather than a silent <see cref="Cancelled"/>, because a
    /// hold that deletes text is the one outcome where saying nothing is
    /// indistinguishable from a bug — the user is watching their words vanish
    /// and needs to see that Dictation meant to do it.
    /// </para>
    /// </remarks>
    VoiceDeleted = 15,

    /// <summary>
    /// A delete command with nothing of Dictation's left to delete.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="VoiceDeleteRefused"/> because the sentences are
    /// different and so is the user's next move: "there is nothing to undo"
    /// means the feature worked and had no work to do, while a refusal means it
    /// declined for a reason. Reached when nothing was recorded, or when a
    /// previous delete already spent the record.
    /// </remarks>
    VoiceDeleteNothingToDelete = 16,

    /// <summary>
    /// A delete command Dictation understood and refused to act on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Covers every guarded refusal: the caret is in a password field, the
    /// focus moved to a different window, the insertion is older than the undo
    /// window, or the span is too long to erase safely.
    /// </para>
    /// <para>
    /// Folded into ONE outcome on purpose. The four refusals differ in cause
    /// and not in remedy — in every case Dictation did not touch the document and
    /// the user's next action is the same. Four near-identical pills would be
    /// four chances to word one of them wrongly about text somebody else owns.
    /// </para>
    /// </remarks>
    VoiceDeleteRefused = 17,

    // Next free value is 18. RanWorkflow and WorkflowNotMatched are declared
    // above, beside SecureField, where they read in context — this member was
    // appended later and took 12, which is why they are 13 and 14.
}

/// <summary>One dictation, start to finish.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Text">
/// What was inserted, and only when it was. Carries <c>null</c> for every other
/// outcome — the same discipline <see cref="TranscriptionResult"/> and
/// <see cref="MicrophoneTapResult"/> keep, so no caller can read "the
/// recognizer is down" as "they said nothing".
/// </param>
/// <param name="Target">
/// The window the text was aimed at, when one was resolved. Present on the
/// insertion failures too, because "which window refused it" is the whole
/// question there.
/// </param>
/// <param name="DecodeDuration">
/// How long the recognizer took, when it ran. Null on every outcome that never
/// reached it, and null on a build whose transcriber does not report it —
/// absent is not zero, and a Recent pane that rendered a missing measurement as
/// 0 ms would be inventing one.
/// </param>
/// <param name="InsertDuration">
/// How long insertion took, when it was attempted. Same null discipline. For
/// a password field found at the paste (<paramref name="SecureFieldAtPaste"/>)
/// it is the check that refused the insertion, as the macOS app's paste
/// timing on a refused insertion is its gate check. Measured separately from the decode because they fail differently and
/// because on macOS this stage turned out to cost a flat ~535 ms on 85% of
/// dictations — a cost that is invisible in a single end-to-end number.
/// </param>
/// <param name="TotalDuration">
/// The whole wait: from the instant the user let go of the key to the instant
/// the words were in the window — <c>releaseToDone</c> in
/// <see cref="DictationSession.RunAsync"/>, the number macOS's
/// <c>DictationHistoryEntry.latencyMs</c> calls "the number the user actually
/// feels".
/// <para>
/// <b>It is not <paramref name="DecodeDuration"/> plus
/// <paramref name="InsertDuration"/>, and it is bigger.</b> The microphone
/// tail, the secure-field probe, the target-window resolve, the formatter and
/// every dispatcher hop between the stages sit inside this span and outside
/// both of those. The two stages stay underneath it because they fail
/// differently and a single end-to-end number hides which one was slow; this
/// exists because the sum of them was being printed under a heading that
/// claimed to be the wait.
/// </para>
/// <para>
/// Same null discipline as the two stages: absent on every outcome that never
/// reached the recogniser, and absent — not zero — on a build that did not
/// carry it.
/// </para>
/// </param>
/// <param name="Audio">
/// The captured hold, and only on the delivered outcomes — the same
/// <see cref="TextWasDelivered"/> discipline <paramref name="Text"/> keeps,
/// and for the same reason: a hold that produced nothing the user kept must
/// not linger in memory as audio either, so every refusal and failure carries
/// <c>null</c>. For a completion handler that keeps the clip; Dictation's
/// keeps no audio and never reads this member, and nothing about the session
/// itself reads it back either.
/// </param>
/// <param name="NBest">
/// The recogniser's alternative hypotheses for this hold, texts only, best
/// first — <see cref="TranscriptionResult.NBest"/> carried through (CPL Setup
/// 6a) so the history line can keep them and a later correction can ask
/// "did the recogniser already offer the right word". Same delivery gate as
/// <paramref name="Text"/> and <paramref name="Audio"/>: a hold whose words
/// never landed leaves no hypotheses behind either. <c>null</c> is "not
/// measured", never "no alternatives".
/// </param>
/// <param name="AvgLogprob">
/// The decode's mean log-probability, <see cref="TranscriptionResult.AvgLogprob"/>
/// carried through under the same gate and the same null rule.
/// </param>
/// <param name="DetectedLanguage">
/// <see cref="TranscriptionResult.DetectedLanguage"/> carried through for the
/// learning loop's <c>hold</c> line (its <c>lang</c> field); same
/// gate and null rule as <paramref name="NBest"/>. loop-v2, item D.
/// </param>
/// <param name="Fired">
/// <see cref="TranscriptionResult.Fired"/> — the bias phrases on the winning
/// path, as host indexes into the list this hold sent — carried through so the
/// <c>hold</c> line can name the claims that fired.
/// <c>null</c> from a worker or a build without the loop. loop-v2, item D.
/// </param>
/// <param name="UndeliveredText">
/// The text that was about to be typed when the insertion did NOT land —
/// <see cref="DictationOutcome.InsertionRefused"/> (which covers a focus move
/// between aiming and typing) and <see cref="DictationOutcome.NoFocusedWindow"/>
/// — and <c>null</c> on every other outcome. The mirror image of
/// <paramref name="Text"/>, kept apart from it so that "what was inserted"
/// never reads as the refused words. Carried so the host can offer the words
/// another way (the standalone app leaves them on the clipboard, as its macOS
/// sibling does, and keeps them in memory for Copy last text until the next
/// result, Dismiss or quitting); nothing here logs, records or stores it. A
/// password field never carries it: found at the release, that hold ends as
/// <see cref="DictationOutcome.SecureField"/> before the recogniser, and found
/// at the paste (<paramref name="SecureFieldAtPaste"/>), its words are dropped
/// there and kept nowhere.
/// </param>
/// <param name="SecureFieldAtPaste">
/// <see cref="DictationOutcome.SecureField"/> only: the password field was
/// found by the second check, just before the paste, so the recogniser had
/// already run and its words were dropped there — the macOS app's refusal at
/// the paste. False for a password field found at the release, before the
/// recogniser, and for every other outcome.
/// </param>
public sealed record DictationSessionResult(
    DictationOutcome Outcome,
    string? Text,
    FocusedApplicationResult? Target,
    TimeSpan? DecodeDuration = null,
    TimeSpan? InsertDuration = null,
    MicrophoneCapture? Audio = null,
    TimeSpan? TotalDuration = null,
    IReadOnlyList<string>? NBest = null,
    double? AvgLogprob = null,
    string? DetectedLanguage = null,
    IReadOnlyList<Speech.AsrFiredPhrase>? Fired = null,
    string? BaselineText = null,
    string? UndeliveredText = null,
    bool SecureFieldAtPaste = false)
{
    /// <summary>
    /// Whether the words reached the window. True for
    /// <see cref="DictationOutcome.Inserted"/> and for
    /// <see cref="DictationOutcome.ClipboardRestoreFailed"/>, which is the one
    /// outcome that is both a delivery and a failure.
    /// </summary>
    public bool TextWasDelivered => Outcome
        is DictationOutcome.Inserted
        or DictationOutcome.ClipboardRestoreFailed;

    internal static DictationSessionResult Cancelled { get; } =
        new(DictationOutcome.Cancelled, null, null);
}

/// <summary>
/// dictation M1 — hold the key, speak, and the words land where you were
/// typing.
///
/// <para>
/// This is the sequencer and nothing else. It owns no device, no window handle
/// and no P/Invoke: the hold comes from <see cref="IPushToTalkHotkey"/>, the
/// audio from <see cref="IMicrophoneTap"/>, the words from
/// <see cref="ITranscriber"/> and the paste from
/// <see cref="IFocusedTextInserter"/>. That is what lets the whole state
/// machine be exercised on a machine with no Windows in it, which is where its
/// failure paths actually get tested.
/// </para>
///
/// <para>
/// <b>It does not subscribe to the hotkey for audio.</b>
/// <see cref="PushToTalkMicrophoneTap"/> already does, and deliberately offers
/// no public <c>Start</c>, so that "the microphone is open only while the key
/// is held" is a structural fact rather than a convention. Reaching around it
/// to open a second path would give that guarantee back up. The press
/// subscription here is for <em>state only</em> — so the UI can say
/// "listening" — and the audio arrives once, through
/// <see cref="IMicrophoneTap.CaptureCompleted"/>.
/// </para>
///
/// <para>
/// <b>Threading.</b> Press and capture-completed both arrive on the WPF UI
/// thread, the same as the two collaborators upstream, and this class is not
/// thread-safe for the same reason they are not. The one asynchronous stretch
/// is transcription: the handler starts it and returns, so the message pump is
/// not blocked while a worker decodes. <see cref="Cancel"/> may therefore be
/// called while that task is in flight, and the guarantee it carries — that
/// nothing is inserted afterwards — is enforced by re-checking the generation
/// after every await rather than by hoping the token was observed.
/// </para>
/// </summary>
public sealed class DictationSession : IDisposable
{
    private readonly IPushToTalkHotkey _hotkey;
    private readonly IMicrophoneTap _tap;
    private readonly ITranscriber _transcriber;
    private readonly IFocusedTextInserter _inserter;
    private readonly ILogger<DictationSession> _log;

    /// <summary>
    /// Whether to run <see cref="DictationTextFormatter"/> over the transcript
    /// before inserting it — asked once per dictation, not captured once at
    /// construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A delegate rather than a bool for the reason the model picker's restart
    /// hint exists to apologise for: a value read at construction would leave a
    /// user who flips the switch dictating under the old setting until the next
    /// arm toggle rebuilt this object, and nothing on screen would say so. Read
    /// per dictation, the switch is live.
    /// </para>
    /// <para>
    /// Null means ON, which is the shipped default
    /// (<c>SettingsData.DictationFormatting = true</c>, mirroring macOS's
    /// <c>DictationFormattingStore.shippedDefault</c>). A test that builds a
    /// session without one gets the shipped behaviour rather than a silently
    /// unformatted path that no user has.
    /// </para>
    /// </remarks>
    private readonly Func<bool>? _formattingEnabled;

    /// <summary>
    /// Applications the user told Dictation not to format for, asked once per
    /// dictation.
    /// </summary>
    /// <remarks>
    /// A delegate for the same reason <see cref="_formattingEnabled"/> is one:
    /// a list read at construction would leave a user who just added an
    /// application still being formatted in it until the next arm toggle
    /// rebuilt this object, with nothing on screen saying so. Null means "no
    /// exceptions", which is what this session did before the list existed —
    /// the built-in terminal set is not affected either way, because it is a
    /// floor rather than a member of this list.
    /// </remarks>
    private readonly Func<IReadOnlySet<string>>? _formattingExceptions;

    /// <summary>
    /// Reads which window the words are about to go into, so the formatter can
    /// be told. Optional: null means every target is treated as an ordinary
    /// application, which is the conservative global default and exactly what
    /// this session did before the per-app gate existed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a SECOND read of the foreground window</b> — the inserter
    /// resolves it again, a moment later, through its own copy of this same
    /// seam. That duplication is deliberate and it is the cheap option:
    /// formatting has to be decided before the text is handed over, and the
    /// inserter's answer does not exist until after. Both reads are one
    /// <c>GetForegroundWindow</c> plus one process lookup, and each is asked at
    /// the instant it is about — which is the property that matters, because a
    /// single read shared between them would be answering the later question
    /// with the earlier moment's window.
    /// </para>
    /// <para>
    /// A delegate would not do here, unlike <see cref="_formattingEnabled"/>:
    /// the answer is a measurement of the desktop, not a setting, so there is
    /// nothing to read lazily — the seam IS the laziness.
    /// </para>
    /// </remarks>
    private readonly IFocusedApplicationResolver? _targetResolver;

    /// <summary>
    /// Asks whether the cursor is in a password field. Optional: null answers
    /// <see cref="SecureFieldState.Unknown"/> for every hold, which every
    /// caller treats as "carry on" — so a composition without one behaves
    /// exactly as this session did before the guard existed.
    /// </summary>
    private readonly ISecureFieldProbe? _secureField;

    /// <summary>
    /// Runs <see cref="_secureField"/> again just before the paste, off the
    /// calling thread, and the pipeline awaits it. Optional: null asks only
    /// at the release. The app passes one, so
    /// a field that became a password field while the words were transcribed
    /// is not typed into (the macOS app refuses there too).
    /// </summary>
    private readonly Func<Func<SecureFieldState>, Task<SecureFieldState>>? _pasteFieldCheck;

    /// <summary>
    /// Asks whether Windows is refusing this application the microphone.
    /// Optional: null answers <see cref="MicrophoneAccess.Unknown"/> for every
    /// hold, which leaves the failure reported exactly as it was before this
    /// distinction existed.
    /// </summary>
    /// <remarks>
    /// Consulted ONLY on the way to
    /// <see cref="DictationOutcome.MicrophoneUnavailable"/>, never on the
    /// ordinary path. A registry walk on every successful dictation would be
    /// work bought with nothing, and — more to the point — a permission that
    /// was fine a moment ago is not evidence about a hold that succeeded.
    /// </remarks>
    private readonly IMicrophoneAccessProbe? _microphoneAccess;

    /// <summary>
    /// Bumped by every cancel and every completion. A dictation that was in
    /// flight across an await compares the generation it started under against
    /// this before it inserts; a mismatch means "you were cancelled, or someone
    /// else started" and it drops its result.
    /// </summary>
    /// <remarks>
    /// A <see cref="CancellationTokenSource"/> alone would not be enough. The
    /// token is passed to the transcriber, and an implementation that finishes
    /// its work without observing it — perfectly legal, and what a fast decode
    /// looks like — would hand back a transcript that then got typed into the
    /// window the user switched to. The generation check is what makes "cancel
    /// means nothing is inserted" true regardless of how the transcriber
    /// behaves.
    /// </remarks>
    private int _generation;

    /// <summary>
    /// Who decides whether a hold was speech or an instruction — <c>null</c>
    /// when none is composed, which types everything; Dictation composes
    /// none. See <see cref="ITranscriptRouter"/>.
    /// </summary>
    private readonly ITranscriptRouter? _router;

    /// <summary>
    /// Whether the spoken delete phrases are armed, read PER DICTATION.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A delegate rather than a captured bool, and for a sharper reason than
    /// <see cref="_formattingEnabled"/> has: this switch governs DELETING TEXT
    /// in an application Dictation does not own. "The engine still thinks it is on"
    /// is not an acceptable failure mode for that, so the answer is fetched at
    /// the moment it is acted on and never cached.
    /// </para>
    /// <para>
    /// Null means OFF, which is also the shipped default — matching macOS,
    /// where the reasoning is recorded as: turning it on makes eight phrases
    /// permanently undictatable.
    /// </para>
    /// </remarks>
    private readonly Func<bool>? _voiceDeleteEnabled;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    public DictationSession(
        IPushToTalkHotkey hotkey,
        IMicrophoneTap tap,
        ITranscriber transcriber,
        IFocusedTextInserter inserter,
        ILogger<DictationSession> log,
        Func<bool>? formattingEnabled = null,
        IFocusedApplicationResolver? targetResolver = null,
        ISecureFieldProbe? secureField = null,
        IMicrophoneAccessProbe? microphoneAccess = null,
        Func<IReadOnlySet<string>>? formattingExceptions = null,
        ITranscriptRouter? router = null,
        Func<bool>? voiceDeleteEnabled = null,
        Func<Func<SecureFieldState>, Task<SecureFieldState>>? pasteFieldCheck = null)
    {
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _tap = tap ?? throw new ArgumentNullException(nameof(tap));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _inserter = inserter ?? throw new ArgumentNullException(nameof(inserter));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        // Not null-checked: null is a meaningful value here (see the fields).
        _formattingEnabled = formattingEnabled;
        _targetResolver = targetResolver;
        _secureField = secureField;
        _microphoneAccess = microphoneAccess;
        _formattingExceptions = formattingExceptions;
        _router = router;
        _voiceDeleteEnabled = voiceDeleteEnabled;
        _pasteFieldCheck = pasteFieldCheck;

        _hotkey.Pressed += OnPressed;
        _tap.CaptureCompleted += OnCaptureCompleted;
    }

    /// <summary>Where this dictation is.</summary>
    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>One dictation finished, however it finished.</summary>
    public event EventHandler<DictationSessionResult>? Completed;

    /// <summary>Fired on every state change, for the UI.</summary>
    public event EventHandler<DictationState>? StateChanged;

    /// <summary>
    /// Claim the hotkey. Returns whether dictation is available afterwards; a
    /// <c>false</c> means the combination is held by another process and the
    /// feature is off for this run, not that the call failed.
    /// </summary>
    public bool Arm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_hotkey.Start())
        {
            _log.LogWarning("Dictation hotkey unavailable — push-to-talk is off for this run");
            return false;
        }

        if (State == DictationState.Idle)
        {
            Transition(DictationState.Armed);
        }

        return true;
    }

    /// <summary>
    /// Abandon whatever is in flight and return to <see cref="DictationState.Armed"/>
    /// (or <see cref="DictationState.Idle"/> if the hotkey was never claimed).
    /// Nothing is inserted after this returns — including by a transcription
    /// that was already running and finishes a moment later.
    /// </summary>
    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        // Bump first. Anything awaiting downstream re-reads this before it
        // inserts, so the ordering here is what makes the guarantee hold even
        // if the token below is never observed.
        unchecked { _generation++; }

        var cts = _cts;
        _cts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with the pipeline's own disposal. The generation bump has
            // already done the work that matters.
        }
        cts?.Dispose();

        if (State is not (DictationState.Idle or DictationState.Armed))
        {
            Transition(_hotkey.IsRegistered ? DictationState.Armed : DictationState.Idle);
            Raise(DictationSessionResult.Cancelled);
        }
    }

    /// <summary>
    /// Run one captured hold through recognition and insertion.
    /// </summary>
    /// <remarks>
    /// Public and awaitable rather than hidden behind the event handler, so the
    /// pipeline can be driven directly in tests without a fake hotkey and a
    /// fake device just to reach it. The event path calls exactly this.
    /// </remarks>
    public async Task<DictationSessionResult> RunAsync(
        MicrophoneTapResult capture,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A logger that renders scopes shows this operation id on every line
        // below, the transcriber's and the workflow router's too, because the
        // scope surrounds every await. (The app's file log, FileLoggerFactory,
        // does not render scopes.)
        // Direct test/tool calls made without the real microphone tap remain
        // explicit as "untracked" rather than inventing an unrelated id here.
        using var operationScope = _log.BeginScope(new Dictionary<string, object?>
        {
            ["OperationId"] = capture.OperationId ?? "untracked",
            ["OperationKind"] = "dictation",
        });

        var generation = _generation;

        // Which window this hold was for, asked at the release and kept for
        // every outcome below.
        //
        // Failures were ANONYMOUS before this. The success path resolves a
        // target for the formatter and the inserter resolves one again for the
        // paste, so an inserted dictation names its app — but every microphone
        // and recogniser failure carried null, and the shell's outcome line
        // printed "(target null)". So a report of "dictation never works in
        // Slack" could not be checked against the log, because the failures in
        // Slack were the ones with no app on them.
        //
        // Deliberately NOT reused for the formatter below. That read happens
        // after the decode and is about a possibly different foreground window;
        // collapsing the two would make one line's answer stand in for the
        // other's moment, which is exactly the confusion macOS shipped when its
        // gate named one app and its result named another for the same press.
        var releaseTarget = ResolveTargetAtRelease();
        var holdWall = System.Diagnostics.Stopwatch.StartNew();
        var recognizerInvoked = false;

        // Every hold gets exactly one privacy-safe terminal record, including
        // microphone failures and cancellations that never reach Whisper. The
        // transcriber's model/backend line shares this operation id when ASR
        // does run; this line supplies the lifecycle outcome and total timing.
        DictationSessionResult Complete(DictationSessionResult proposed)
        {
            var completed = Finish(generation, proposed);
            holdWall.Stop();
            var target = completed.Target ?? releaseTarget;
            _log.LogInformation(
                "Dictation completed: operation={Operation} outcome={Outcome} "
                + "target={Target} asr_invoked={AsrInvoked} audio_s={AudioSeconds} "
                + "chars={Characters} elapsed_ms={ElapsedMilliseconds} "
                + "recognizer_ms={RecognizerMilliseconds} insert_ms={InsertMilliseconds} "
                + "release_to_done_ms={ReleaseToDoneMilliseconds}",
                capture.OperationId ?? "untracked",
                completed.Outcome,
                target?.ProcessName is { Length: > 0 } process ? process : "none",
                recognizerInvoked,
                capture.Audio?.Duration.TotalSeconds,
                completed.Text?.Length ?? 0,
                holdWall.ElapsedMilliseconds,
                completed.DecodeDuration?.TotalMilliseconds,
                completed.InsertDuration?.TotalMilliseconds,
                completed.TotalDuration?.TotalMilliseconds);
            return completed;
        }

        if (capture.Outcome != MicrophoneTapOutcome.Captured || capture.Audio is null)
        {
            // Audio is null on every non-captured outcome by MicrophoneTapResult's
            // own contract; the null check is here so this stays correct if that
            // ever loosens, rather than dereferencing on trust.
            return Complete(new DictationSessionResult(
                capture.Outcome switch
                {
                    MicrophoneTapOutcome.TooShort => DictationOutcome.HoldTooShort,
                    // The one branch that asks WHY. Every other microphone
                    // failure is what it says; this one has a cause the user
                    // can act on, and collapsing it into "the microphone would
                    // not open" is indistinguishable from a broken feature.
                    MicrophoneTapOutcome.DeviceUnavailable when ProbeMicrophoneAccessIsBlocked()
                        => DictationOutcome.MicrophoneBlocked,
                    MicrophoneTapOutcome.DeviceUnavailable => DictationOutcome.MicrophoneUnavailable,
                    MicrophoneTapOutcome.NoAudio => DictationOutcome.NoAudio,
                    _ => DictationOutcome.NoAudio,
                },
                null,
                releaseTarget));
        }

        // ── The password-field guard ──────────────────────────────────────
        //
        // Before the recogniser, before the history line, before the wav,
        // before the clipboard. This is the last moment at which nothing about
        // this hold has been kept, and it is therefore the only honest place
        // for the check: the audio is in memory and about to be dropped on the
        // floor, and no other stage has run.
        //
        // Windows needs this MORE than macOS does. There, Secure Input
        // disables event taps outright, so dictation physically cannot run in a
        // password box and the only question is which sentence to show. Here a
        // WH_KEYBOARD_LL hook is not disabled by anything, so without this the
        // whole pipeline completes: a spoken password is transcribed, written
        // to dictation-history.jsonl, recorded as a wav, put on the clipboard
        // and pasted.
        //
        // Unknown carries on. A probe that could not answer must never be able
        // to switch dictation off — see SecureFieldState.Unknown for why that
        // direction and not the other. A host that passes pasteFieldCheck is
        // asked again just before the paste, for a field that becomes a
        // password field while the words decode.
        if (ProbeSecureField() == SecureFieldState.Secure)
        {
            _log.LogInformation(
                "Dictation refused: the focused element is a password field. Nothing was transcribed, stored or typed.");

            // Nulls, not the transcript that does not exist yet: TextWasDelivered
            // is false for this outcome, so the recorder writes no wav and the
            // history line carries no words.
            return Complete(new DictationSessionResult(
                DictationOutcome.SecureField,
                null,
                releaseTarget));
        }

        // ── The quiet-room guard ──────────────────────────────────────────
        //
        // The recogniser does not get asked about audio with no speech-level
        // energy in it, because for two years the honest answer to "what does
        // Whisper return for silence" has been "a sentence".
        //
        // Measured on this device on 2026-08-20, through the shipped IPC path
        // at beam 4, on a real 0.5 s hold whose loudest 30 ms frame reads
        // 0.00227 against the 0.0040 floor: `whisper-base-qnn` typed
        // `날씨였습니다.` and `whisper-large-v3-turbo-qnn` typed `Thank you.`
        // Neither model's own non-speech gate fired, and turbo's structurally
        // cannot — it puts ~1e-11 on its `<|nospeech|>` token on every input
        // including speech, so no threshold on that number exists
        // (the platform measurements §9.3, §9.7). Over 224 real microphone
        // segments turbo rejected zero and typed into 130 of the 131 that base
        // left empty, 85 of them the phrase "Thank you."
        //
        // So the verdict is taken from the AUDIO, where the separation is not
        // marginal: the floor sits 1.76x above that silent hold's loudest
        // frame and 42x below the quietest real hold in the corpus, and it
        // reads the same on both checkpoints because it never asks one.
        //
        // NoSpeech and not a new outcome: "Nothing worth typing was heard" is
        // already the sentence for this, and the enum already keeps it apart
        // from NoAudio (the endpoint delivered nothing — a dead or muted
        // device) and HoldTooShort (a brushed key). Those three stay distinct
        // because they are fixed by three different actions, and a user told
        // to speak up when their microphone is dead is a lie this app has
        // shipped once.
        //
        // The audio is NOT discarded on the capture side to make this work —
        // MicrophoneTap still returns it, so the reading that explains the
        // rejection is still in the log and a support report still has its
        // evidence. What stops here is the request to the recogniser.
        if (!capture.Audio.HasSpeechEnergy)
        {
            _log.LogInformation(
                "Dictation heard a quiet room: {Duration:0.##}s of audio whose loudest "
                + "{FrameMs} ms frame reads {FramePeak:F5} against the {Floor:F4} audible "
                + "floor, {Voiced:0.##}s voiced. Nothing was sent to the recogniser — asked "
                + "about audio with no speech in it, Whisper does not return nothing, it "
                + "returns a sentence.",
                capture.Audio.Duration.TotalSeconds,
                (int)(WorkflowAudio.FrameSeconds * 1000),
                capture.Audio.FrameRmsPeak,
                WorkflowAudio.AudibleFloor,
                capture.Audio.VoicedSeconds);

            return Complete(new DictationSessionResult(
                DictationOutcome.NoSpeech,
                null,
                releaseTarget));
        }

        Transition(DictationState.Transcribing);

        // Wall-clock from the user's release to the words landing, split by
        // stage. transcript.Latency is the recogniser's own number and does
        // NOT include the worker spawn/model init a cold first hold pays, nor
        // the wire wait behind a workflow decode — decodeWall does, and the
        // gap between the two is exactly the wait that has no other witness.
        var releaseToDone = System.Diagnostics.Stopwatch.StartNew();
        var decodeWall = System.Diagnostics.Stopwatch.StartNew();

        TranscriptionResult transcript;
        try
        {
            recognizerInvoked = true;
            transcript = await _transcriber.TranscribeAsync(capture.Audio, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return Complete(DictationSessionResult.Cancelled);
        }

        if (generation != _generation)
        {
            // Cancelled while the worker was decoding. The transcript is real
            // and is dropped on purpose: the window it was meant for is not
            // necessarily the window in front of the user now.
            _log.LogDebug("Dictation cancelled during transcription — transcript discarded");
            return Complete(DictationSessionResult.Cancelled);
        }

        if (transcript.Outcome != TranscriptionOutcome.Transcribed
            || string.IsNullOrWhiteSpace(transcript.Text))
        {
            decodeWall.Stop();
            releaseToDone.Stop();

            // The stage line for a hold that never reached the cursor. Its
            // successful twin below has been printing since it was written and
            // this path printed nothing, so "why was that so slow before it
            // gave up" had no answer at all — and "the recogniser took 15
            // seconds to time out" and "it refused in 4 ms" are completely
            // different reports that arrived as the same one line from the
            // shell. The audio duration is here too, because a failure on a
            // 0.4 s hold and a failure on a 12 s hold are not the same event.
            _log.LogInformation(
                "Dictation ended at the recogniser: {Outcome} — audio {AudioS:F1}s from {Process}, "
                + "decode {DecodeWallMs} ms (recognizer {RecognizerMs} ms), release-to-done {TotalMs} ms",
                transcript.Outcome,
                capture.Audio.Duration.TotalSeconds,
                releaseTarget?.ProcessName is { Length: > 0 } p ? p : "an unidentified window",
                decodeWall.ElapsedMilliseconds,
                (long?)transcript.Latency?.TotalMilliseconds,
                releaseToDone.ElapsedMilliseconds);

            // Whitespace counts as nothing said. A recognizer that returns " "
            // has not given us something worth pasting over a selection.
            return Complete(new DictationSessionResult(
                transcript.Outcome switch
                {
                    TranscriptionOutcome.NoSpeech => DictationOutcome.NoSpeech,
                    TranscriptionOutcome.RecognizerUnavailable => DictationOutcome.RecognizerUnavailable,
                    TranscriptionOutcome.RecognizerFailed => DictationOutcome.RecognizerFailed,
                    _ => DictationOutcome.NoSpeech,
                },
                null,
                releaseTarget,
                transcript.Latency));
        }

        // VOICE DELETE. Before the router, and before the formatter.
        //
        // Before the ROUTER because "scratch that" must never be spent as a
        // workflow attempt: the router's own failure outcome is
        // WorkflowNotMatched, and a user who said a delete phrase with the
        // feature off would otherwise be told Dictation did not recognise a command
        // it deliberately does not have.
        //
        // Before the FORMATTER because the formatter cannot change the answer —
        // matching normalises away casing and punctuation, which is all the
        // formatter adds — so running it first would be work on a string nobody
        // will ever read.
        //
        // The phrase is never typed, never inserted, never written to history
        // and never put on the clipboard. Returning here is what guarantees
        // that: there is no suppression rule to forget further down.
        if ((_voiceDeleteEnabled?.Invoke() ?? false)
            && DictationDeleteCommand.IsDeleteCommand(transcript.Text))
        {
            // Discovered on the inserter rather than taken as its own
            // dependency, so that the thing that typed and the thing that
            // un-types are necessarily the same object — the same argument
            // IClipboardTextSnapshot makes one layer down. A composition that
            // never wired the undo capability answers "nothing to delete"
            // rather than pretending the phrase was dictation.
            if (_inserter is not IFocusedTextUndo undo)
            {
                _log.LogWarning(
                    "Voice delete is on but the inserter has no undo capability; "
                        + "the phrase was swallowed and nothing was deleted.");
                return Complete(new DictationSessionResult(
                    DictationOutcome.VoiceDeleteNothingToDelete,
                    null,
                    releaseTarget,
                    transcript.Latency));
            }

            FocusedUndoResult undone;
            try
            {
                undone = await undo.UndoLastInsertionAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return Complete(DictationSessionResult.Cancelled);
            }
            catch (Exception ex)
            {
                // A throw costs the delete, never the dictation — and never a
                // half-finished one. The gate runs before any keystroke is
                // synthesised, so an exception here means nothing was sent.
                _log.LogWarning(ex, "Voice delete failed; nothing was deleted");
                return Complete(new DictationSessionResult(
                    DictationOutcome.VoiceDeleteRefused,
                    null,
                    releaseTarget,
                    transcript.Latency));
            }

            if (generation != _generation)
            {
                // Cancelled while the backspaces were going out. Same rule the
                // router keeps below, and it matters more here: the window this
                // was meant for is not necessarily the window in front of the
                // user now.
                _log.LogDebug("Dictation cancelled during a voice delete");
                return Complete(DictationSessionResult.Cancelled);
            }

            decodeWall.Stop();
            releaseToDone.Stop();

            var deleteOutcome = undone.Undone
                ? DictationOutcome.VoiceDeleted
                : undone.Refusal == DictationUndoRefusal.NothingRecorded
                    ? DictationOutcome.VoiceDeleteNothingToDelete
                    : DictationOutcome.VoiceDeleteRefused;

            _log.LogInformation(
                "Voice delete: {Outcome} ({Refusal}), {Count} backspaces — "
                    + "release-to-done {TotalMs} ms",
                deleteOutcome,
                undone.Refusal,
                undone.BackspaceCount,
                releaseToDone.ElapsedMilliseconds);

            return Complete(new DictationSessionResult(
                deleteOutcome,
                null,
                releaseTarget,
                transcript.Latency));
        }

        // ONE KEY (C-3). Before the formatter and before the cursor: was this
        // something to type, or something to do?
        //
        // Here rather than anywhere else for two reasons. Earlier there are no
        // words to decide on. Later the formatter has already run, and running
        // sentence-casing and punctuation over a spoken command would be
        // work done on a string nobody will ever read — and, worse, would make
        // the text the router sees differ from the text the user said.
        //
        // A router that throws costs the command, not the dictation: the words
        // are typed. Losing a command costs a repeat; losing a sentence
        // somebody dictated costs their words.
        if (_router is not null)
        {
            TranscriptRouting routing;
            try
            {
                routing = await _router
                    .RouteAsync(transcript.Text, ct)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return Complete(DictationSessionResult.Cancelled);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Routing the transcript failed; typing it instead");
                routing = TranscriptRouting.Dictate(transcript.Text);
            }

            if (generation != _generation)
            {
                // Cancelled while the router was deciding. Same rule as a
                // cancellation during the decode: the window this was meant for
                // is not necessarily the window in front of the user now.
                _log.LogDebug("Dictation cancelled during routing — transcript discarded");
                return Complete(DictationSessionResult.Cancelled);
            }

            if (!routing.ShouldInsert)
            {
                decodeWall.Stop();
                releaseToDone.Stop();

                _log.LogInformation(
                    "Dictation hold was an instruction: {Routing} — audio {AudioS:F1}s, "
                        + "release-to-done {TotalMs} ms",
                    routing.Outcome,
                    capture.Audio.Duration.TotalSeconds,
                    releaseToDone.ElapsedMilliseconds);

                // No text on the result, deliberately: neither of these is a
                // delivered outcome, so nothing reaches the history. What was
                // said is on the HUD, which is where a command belongs.
                return Complete(new DictationSessionResult(
                    routing.Outcome == RoutingOutcome.RanWorkflow
                        ? DictationOutcome.RanWorkflow
                        : DictationOutcome.WorkflowNotMatched,
                    null,
                    releaseTarget,
                    transcript.Latency));
            }

            // A router may rewrite what gets typed — it is the seam that knows
            // whether a leading word was addressed to it or dictated.
            transcript = transcript with { Text = routing.TextToInsert ?? transcript.Text };
        }

        // The two shipped text rules, between the recogniser and the cursor.
        //
        // This is the last point at which the text is Dictation's. After the
        // insert it is in the user's document, and a formatter that ran there
        // would be editing something they own. Applied here, the character
        // count in the result and in the history is the count of what actually
        // landed — formatting before measuring rather than after is what stops
        // Recent reporting a length nobody typed.
        //
        // Only the transcript's TEXT changes. transcript.Latency, the outcome
        // and every stage timing below are the recogniser's own numbers and
        // must stay untouched: this is not a stage the user waits through in
        // any measurable sense (two string scans over a sentence), and folding
        // it into the decode figure would put a cost in a bucket that has to
        // keep meaning "the worker".
        // Which window is about to receive this, asked here and not earlier:
        // the answer decides whether the formatter runs at all, and a terminal
        // the user alt-tabbed to while the recogniser was working is still the
        // window the paste is going to land in.
        //
        // Never fatal. A resolver that throws — it is a seam, and a future
        // implementation could — costs the per-app gate, not the dictation:
        // the target falls back to null, which IsTerminal reads as an ordinary
        // application and so as the conservative global default.
        string? targetProcess = null;
        if (_targetResolver is not null)
        {
            try
            {
                var resolved = _targetResolver.Resolve();
                targetProcess = resolved.IsResolved ? resolved.ProcessName : null;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not resolve the target window for formatting; using the default rules");
            }
        }

        // The user's own exception list, asked at the same moment the target is
        // — and never fatal, for the same reason the resolver above is not: a
        // list that cannot be read costs the exceptions, not the dictation.
        IReadOnlySet<string>? exceptions = null;
        if (_formattingExceptions is not null)
        {
            try
            {
                exceptions = _formattingExceptions.Invoke();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Could not read the formatting exceptions; using the built-in rules");
            }
        }

        var textToInsert = (_formattingEnabled?.Invoke() ?? true)
            ? DictationTextFormatter.Format(transcript.Text, targetProcess, exceptions)
            : transcript.Text;

        // ── The password-field guard, again, at the paste ────────────────
        //
        // The focus can move into a password field while the words decode.
        // Asked here, still Transcribing, so Escape still cancels and a press
        // is still refused while it runs; awaited, so the thread that polls
        // the hold key never waits on UI Automation. Unknown carries on, as at
        // the release.
        if (_pasteFieldCheck is not null)
        {
            var pasteCheck = System.Diagnostics.Stopwatch.StartNew();
            var field = await CheckFieldAtPasteAsync().ConfigureAwait(true);
            pasteCheck.Stop();
            if (generation != _generation)
            {
                _log.LogDebug("Dictation cancelled during the password-field check at the paste — transcript discarded");
                return Complete(DictationSessionResult.Cancelled);
            }

            if (field == SecureFieldState.Secure)
            {
                releaseToDone.Stop();
                _log.LogInformation(
                    "Dictation refused at the paste: the focused element is a password field. Nothing was typed, copied or kept.");

                // No Text, no UndeliveredText and no Audio: the words go nowhere.
                // The insertion stage is the check that refused it, as the
                // macOS app's paste timing on a gate rejection is its gate
                // check (TextInserter's gateCheckMs).
                return Complete(new DictationSessionResult(
                    DictationOutcome.SecureField,
                    null,
                    releaseTarget,
                    transcript.Latency,
                    pasteCheck.Elapsed,
                    TotalDuration: releaseToDone.Elapsed,
                    SecureFieldAtPaste: true));
            }
        }

        Transition(DictationState.Inserting);

        // Timed here rather than inside the inserter: this is the stage
        // boundary the user actually waits through, and the inserter has
        // several implementations that should not each have to remember to
        // clock themselves.
        // ConfigureAwait(true) like every await above: Finish transitions
        // state the UI binds to. No generation re-check after this one —
        // the paste has already happened, and Finish is where a stale
        // generation is arbitrated. Not passing the session's token:
        // cancelling mid-insert would skip the clipboard restore, and the
        // inserter documents that it must run to completion once the
        // clipboard is borrowed.
        decodeWall.Stop();
        var insertStarted = System.Diagnostics.Stopwatch.StartNew();
        var insertion = await _inserter.InsertAsync(textToInsert).ConfigureAwait(true);
        insertStarted.Stop();
        releaseToDone.Stop();

        // The one line to read when someone says "dictation felt slow".
        // audio is what they spoke; decode wall minus the recogniser's own
        // number is worker spawn/init/queue; insert includes the fixed paste
        // settle. INF, not DBG: a latency complaint arrives without a repro,
        // so the numbers have to already be in the log when it does.
        _log.LogInformation(
            "Dictation stages: audio {AudioS:F1}s -> decode {DecodeWallMs} ms (recognizer {RecognizerMs} ms, spawn/init/queue {OverheadMs} ms) -> insert {InsertMs} ms (incl. paste settle) -> release-to-done {TotalMs} ms",
            capture.Audio.Duration.TotalSeconds,
            decodeWall.ElapsedMilliseconds,
            (long?)transcript.Latency?.TotalMilliseconds,
            transcript.Latency is { } l ? decodeWall.ElapsedMilliseconds - (long)l.TotalMilliseconds : null,
            insertStarted.ElapsedMilliseconds,
            releaseToDone.ElapsedMilliseconds);

        // The comparison line, for a hold that asked for a baseline (Dictation
        // never asks, so it never writes this). One clip, two
        // decodes. Lengths and the claims that fired only — never the
        // transcript text, which is the user's (G6; P6a F9): the full pair is
        // in holds.jsonl (`text` / `baseline_text`) for whoever needs it.
        // Absent entirely without a baseline — no behaviour change.
        if (transcript.BaselineText is { } baselineText && insertion.TextWasDelivered)
        {
            _log.LogDebug(
                "Dictation compare: text {TextLength} chars, baseline_text {BaselineLength} chars, fired=[{Fired}]",
                textToInsert.Length,
                baselineText.Length,
                transcript.Fired is { Count: > 0 } firedPhrases
                    ? string.Join(", ", firedPhrases.Select(x => x.Phrase))
                    : string.Empty);
        }

        return Complete(new DictationSessionResult(
            insertion.Outcome switch
            {
                FocusedInsertionOutcome.Success => DictationOutcome.Inserted,
                FocusedInsertionOutcome.NoFocusedWindow => DictationOutcome.NoFocusedWindow,
                FocusedInsertionOutcome.ClipboardRestoreFailed => DictationOutcome.ClipboardRestoreFailed,
                _ => DictationOutcome.InsertionRefused,
            },
            insertion.TextWasDelivered ? textToInsert : null,
            insertion.Target,
            transcript.Latency,
            insertStarted.Elapsed,
            // Same delivery gate as Text, deliberately on the insertion's own
            // verdict rather than a re-derivation: the audio of a hold whose
            // words never landed is audio of a failure, and no store should
            // ever be handed it.
            insertion.TextWasDelivered ? capture.Audio : null,
            // The span the log line above has been printing since it was
            // written, now carried instead of discarded. It was measured, it
            // was logged, and it reached nothing a user could read — so the
            // Insights pane reported DecodeMs + InsertMs under a heading macOS
            // uses for this number, which is systematically smaller than the
            // wait the user experiences. NOT gated on delivery: how long a
            // failure took to fail is exactly as measured as how long a success
            // took to succeed.
            releaseToDone.Elapsed,
            // The recogniser's N-best and confidence (Setup 6a), under the
            // delivery gate Text and Audio keep: what a refused hold heard as
            // alternatives is still speech the user got nothing for.
            insertion.TextWasDelivered ? transcript.NBest : null,
            insertion.TextWasDelivered ? transcript.AvgLogprob : null,
            // loop-v2: the hold event's language and the
            // claims that fired on the winning path, under the same delivery gate.
            insertion.TextWasDelivered ? transcript.DetectedLanguage : null,
            insertion.TextWasDelivered ? transcript.Fired : null,
            // A comparison hold's baseline (Dictation never asks for one): the
            // stock decode of the SAME clip, beside the text that landed. Same
            // delivery gate — a comparison for words that never landed is a
            // comparison nobody saw.
            insertion.TextWasDelivered ? transcript.BaselineText : null,
            // The inverse gate: the words the user spoke and the paste did not
            // deliver, so the host can still hand them over. Only on a refused
            // insertion — every earlier exit returned above with nothing to
            // carry.
            insertion.TextWasDelivered ? null : textToInsert));
    }

    /// <summary>
    /// Whether Windows is definitely refusing the microphone, with every
    /// failure folded into "no".
    /// </summary>
    /// <remarks>
    /// Same posture as <see cref="ProbeSecureField"/>: a probe that could not
    /// answer must never be able to change what the user is told. An unreadable
    /// consent store leaves the outcome at
    /// <see cref="DictationOutcome.MicrophoneUnavailable"/>, which is exactly
    /// what shipped before this existed.
    /// </remarks>
    private bool ProbeMicrophoneAccessIsBlocked()
    {
        if (_microphoneAccess is null)
        {
            return false;
        }

        try
        {
            var access = _microphoneAccess.Probe();
            if (MicrophoneAccessCopy.IsBlocked(access))
            {
                _log.LogInformation(
                    "Dictation could not open the microphone because Windows is blocking it ({Access})",
                    access);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The microphone access probe threw; reporting the plain device failure");
            return false;
        }
    }

    /// <summary>
    /// Which window had focus at the release, folded to <c>null</c> on every
    /// failure.
    /// </summary>
    /// <remarks>
    /// Same posture as the two probes beside it: a resolver that cannot answer
    /// costs this dictation its NAME in the log, never the dictation itself.
    /// It is asked once per hold, before anything can fail, so that a hold
    /// which dies at the microphone still says which application the user was
    /// trying to type into.
    /// </remarks>
    private FocusedApplicationResult? ResolveTargetAtRelease()
    {
        if (_targetResolver is null)
        {
            return null;
        }

        try
        {
            var resolved = _targetResolver.Resolve();
            return resolved.Status == FocusedApplicationStatus.NoFocusedWindow ? null : resolved;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not resolve the window focused at release; this hold will be reported without one");
            return null;
        }
    }

    /// <summary>
    /// The probe, with every failure folded into
    /// <see cref="SecureFieldState.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ISecureFieldProbe.Probe"/> is documented never to throw, and
    /// this catches anyway. The cost of being wrong about that is a crash
    /// raised from inside the one guard whose entire purpose is to make a
    /// dictation fail politely, and the cost of the catch is four lines.
    /// </remarks>
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

    /// <summary>
    /// <see cref="ProbeSecureField"/> through the host's runner, just before
    /// the paste. A runner that faults answers
    /// <see cref="SecureFieldState.Unknown"/>, as a probe that throws does.
    /// </summary>
    private async Task<SecureFieldState> CheckFieldAtPasteAsync()
    {
        try
        {
            return await _pasteFieldCheck!(ProbeSecureField).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "The password-field check at the paste could not run; treating the field as ordinary");
            return SecureFieldState.Unknown;
        }
    }

    private void OnPressed(object? sender, EventArgs e)
    {
        if (_disposed || State == DictationState.Idle)
        {
            return;
        }

        // A press while a previous dictation is still transcribing supersedes
        // it. The generation bump is what stops the older one inserting.
        if (State is DictationState.Transcribing or DictationState.Inserting)
        {
            unchecked { _generation++; }
        }

        Transition(DictationState.Listening);
    }

    private void OnCaptureCompleted(object? sender, MicrophoneTapResult result)
    {
        if (_disposed || State != DictationState.Listening)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        // Fire-and-forget on purpose: this handler runs on the UI thread, and
        // awaiting a decode here would freeze the window the text is going
        // into. Every failure inside is an outcome rather than an exception,
        // and the continuation exists so a bug that breaks that promise is
        // logged instead of becoming an unobserved-task crash at finalization.
        _ = RunAsync(result, cts.Token).ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    _log.LogError(t.Exception, "Dictation pipeline threw — this is a bug, not an outcome");
                    Transition(_hotkey.IsRegistered ? DictationState.Armed : DictationState.Idle);
                }
                if (ReferenceEquals(_cts, cts))
                {
                    _cts = null;
                }
                cts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private DictationSessionResult Finish(int generation, DictationSessionResult result)
    {
        if (generation != _generation)
        {
            return DictationSessionResult.Cancelled;
        }

        unchecked { _generation++; }
        Transition(_hotkey.IsRegistered ? DictationState.Armed : DictationState.Idle);
        Raise(result);
        return result;
    }

    private void Transition(DictationState next)
    {
        if (State == next)
        {
            return;
        }

        State = next;
        StateChanged?.Invoke(this, next);
    }

    private void Raise(DictationSessionResult result)
        => Completed?.Invoke(this, result);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkey.Pressed -= OnPressed;
        _tap.CaptureCompleted -= OnCaptureCompleted;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _cts?.Dispose();
        _cts = null;
    }
}
