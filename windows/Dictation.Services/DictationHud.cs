using Dictation.Speech;

namespace Dictation.Services;

/// <summary>
/// What the floating dictation indicator says, and how long it stays.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a named field failure, recorded in
/// <c>the Windows surface contract</c>: <em>"the user had no way to tell
/// whether anything was listening — a worse bug than a bad transcript."</em>
/// Windows dictation armed on the device with no indicator at all. Holding the
/// shortcut and speaking produced either text or silence, and silence covered
/// every one of eleven distinct failures.
/// </para>
/// <para>
/// <b>All of the judgement is here, in plain <c>net9.0</c>.</b> The WPF window
/// is left with a brush lookup, a timer and a rectangle. That split is what
/// lets the rules below — which are product rules, not chrome — be asserted on
/// a machine with no WPF.
/// </para>
/// <para>
/// <b>Almost never says what you dictated.</b> A HUD is visible to whoever is
/// behind you and to whatever is screen-sharing, so every OUTCOME and PROCESS
/// pill in this file — the "Typed" / "Cancelled" receipts, "Working" /
/// "Typing", every failure sentence — still names none of the words, no
/// command text, and no process name where naming one would itself be a fact
/// worth withholding (see <see cref="DictationOutcome.SecureField"/> and
/// <see cref="DictationOutcome.RanWorkflow"/> / <see cref="DictationOutcome.WorkflowNotMatched"/>
/// below for why). The one exception is the short, continuously-overwritten
/// live-preview caption described below, added 2026-08-22 precisely because it
/// is a different, smaller exposure from a whole receipt's sentence — see that
/// paragraph before assuming this rule is absolute. This rule is about
/// <em>display</em>, not storage (Dictation keeps only its one recent
/// result, in memory, in <c>Dictation.Core</c>'s
/// <c>DictationRecentResult</c>): what a settings pane shows to a user who
/// opened it on purpose,
/// what the live-preview caption shows while a hold is still in progress, and
/// what an outcome pill's receipt says once it is over, are three different
/// exposures, and only the settings pane and the caption ever show any words.
/// </para>
/// <para>
/// <b>SUPERSEDED 2026-08-21 → 2026-08-22: "there is no live hypothesis here,
/// and that is a decision."</b> The pill carried one for a while — the words
/// the recogniser thought it had heard SO FAR, in a dimmed second run while
/// the key was held. The owner dropped it on 2026-08-21 and asked for Windows
/// and macOS to be identical without it, for the reason recorded here at the
/// time: the pill is always-on-top and therefore visible to whoever is behind
/// the user and to whatever is screen-sharing, and a half-heard sentence is
/// still what they said. That reasoning is kept, not deleted, because the
/// paragraph below does not contradict it — it answers it.
/// </para>
/// <para>
/// <b>The live-preview caption, added 2026-08-22.</b> The owner reversed the
/// 2026-08-21 decision above, with its own privacy reasoning put back to them
/// explicitly before they decided. While the live preview is on
/// (<c>DICTATION_PREVIEW=1</c>) and the streaming recogniser has produced a
/// hypothesis for the hold in progress, the indicator window
/// (<c>DictationHudWindow</c>) draws a rolling window of the TRAILING few
/// words in the pill's title place, from <see cref="DictationHudModel.Caption"/>
/// — see <see cref="DictationPreviewCaption.Window"/> for the windowing rule
/// itself.
/// Say plainly what this does and does not change: a short fragment that is
/// continuously overwritten and never accumulates past
/// <see cref="DictationPreviewCaption.DefaultWordWindow"/> words (or
/// <see cref="DictationPreviewCaption.DefaultCjkCharacterBudget"/> CJK
/// characters) REDUCES the shoulder-surfing and screen-share exposure the
/// 2026-08-21 decision existed to prevent — a glance at the pill mid-hold
/// reads a handful of trailing words, never the sentence building toward
/// them and never anything already scrolled out of the window. It does NOT
/// eliminate that exposure: anyone watching continuously, or capturing the
/// screen the whole time, still reads the dictation as it is said, a few
/// words at a time. That is a materially smaller exposure than the whole
/// half-heard sentence a permanent hypothesis pill would have shown, and it
/// is why the reversal is defensible on the ORIGINAL 2026-08-21 grounds rather
/// than a rejection of them — but it is a reduction, not a fix, and nothing
/// here should be read as claiming otherwise.
/// </para>
/// <para>
/// <b>The window's width is a privacy parameter, not a layout preference.</b>
/// The 5-word / 12-CJK-character budget above is the entire reason the
/// previous paragraph's "reduces, does not eliminate" claim is true rather
/// than aspirational: a caption widened back toward a whole sentence — for
/// legibility, for a bigger pill, for any reason that is not this one — would
/// silently re-open exactly the exposure 2026-08-21 was about. Any future
/// change to <see cref="DictationPreviewCaption.DefaultWordWindow"/> or
/// <see cref="DictationPreviewCaption.DefaultCjkCharacterBudget"/> is a repeat
/// of THIS decision, not a UI tweak, and needs the same reasoning written down
/// again, not a diff that just changes a number.
/// </para>
/// <para>
/// <b>Post-release quiet, added 2026-08-22, is a separate rule from the
/// caption above and does not by itself decide what renders.</b>
/// <see cref="For(DictationState, bool)"/> and
/// <see cref="For(DictationSessionResult, bool, bool)"/> take a
/// <c>previewShowedWords</c> flag and, when it is true, hide the "Working" /
/// "Typing" process pills and the ordinary "Typed" / "Cancelled" receipts —
/// on the owner's separate call that a user who has been watching the
/// preview assemble their sentence needs no further confirmation that
/// dictation is doing its job. That flag answers only "did the preview show
/// ANY words this hold" — a fact the presenter tracks as a plain latch
/// alongside the caption above, not a re-reading of whatever the caption
/// currently holds (the caption is itself a window and can go back to empty
/// mid-hold). This file is never handed the words themselves, only that one
/// fact, and every failure sentence in
/// <see cref="For(DictationSessionResult, bool, bool)"/> stays exactly as
/// loud regardless of the flag; see <see cref="IsQuietedByLivePreview"/> for
/// which two outcomes are the exception and why the other thirteen are not.
/// </para>
/// </remarks>
public sealed record DictationHudModel(
    string Text, HudTone Tone, TimeSpan? Dwell)
{
    /// <summary>
    /// The lines this pill is sized for besides its own text, or null. The
    /// Dictation app sets every busy line a hold can show (held, hands-free
    /// with its clock and countdown, transcribing, typing) on every pill it
    /// shows: a busy pill is then exactly as wide as the widest of them, so
    /// it neither cuts a hint off nor resizes and moves from the press to the
    /// ending, and an ending starts at that width and grows for its sentence,
    /// as the Mac's capsule does. Null keeps the compact hold width for a
    /// state and the sentence's own width for an outcome.
    /// </summary>
    public IReadOnlyList<string>? SizedFor { get; init; }

    /// <summary>
    /// The name a screen reader finds on this pill when it is shown without
    /// being announced, or null. The Dictation app sets it on the listening
    /// pill, which is never announced because the microphone is open: a
    /// constant line for the state ("Listening", "Listening, hands-free"),
    /// never the hint or the clock, so a reader who reaches the pill mid-hold
    /// hears the state rather than the last ending, and nothing about the
    /// name changes each second. The window sets it without raising a
    /// live-region event.
    /// </summary>
    public string? UnannouncedName { get; init; }

    /// <summary>
    /// The live preview's caption, drawn in place of the title while a hold
    /// listens, or null. The Dictation app sets it on the listening pill
    /// when the preview is on (<c>DICTATION_PREVIEW=1</c>): the last few words
    /// heard so far, display only. It is never part of <see cref="Text"/>,
    /// <see cref="AnnouncedText"/> or <see cref="QuietName"/>, so a screen
    /// reader is never given the words, and it never changes the pill's
    /// size: the window fits it to the width the pill already has, cutting
    /// the oldest words.
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>
    /// Nothing on screen. Distinct from a blank line: the window is closed, not
    /// showing an empty pill.
    /// </summary>
    public static readonly DictationHudModel Hidden =
        new(string.Empty, HudTone.Neutral, TimeSpan.Zero);

    /// <summary>Whether there is anything to show at all.</summary>
    public bool IsVisible => Text.Length > 0;

    /// <summary>
    /// What a screen reader says when this pill appears or changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the HUD needed this at all.</b> The window is
    /// <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c> and carried no automation
    /// name anywhere. Its own comment reasoned that the waveform is decoration
    /// "because the status sentence beside it" carries the meaning — which is
    /// true, and which is exactly why the sentence had to be announced. It was
    /// a plain <c>TextBlock</c> in a window that never takes focus, so nothing
    /// ever read it. A Narrator user held the key and got no "Listening", no
    /// "Hearing you", and no reason when nothing was typed: not the microphone
    /// denial, not the password-field refusal, not the paste that did not land.
    /// macOS has asserted a non-empty, DISTINCT accessibility label for every
    /// phase and every failure since <c>DictationHUDSelfCheck</c>.
    /// </para>
    /// <para>
    /// <b>It is the visible sentence, and nothing more.</b> A listener and a
    /// reader get the same words — the property worth holding, because the
    /// alternative is two copies of the product's own vocabulary drifting
    /// apart.
    /// </para>
    /// </remarks>
    public string AnnouncedText => Text;

    /// <summary>
    /// Whether this model should be announced at all.
    /// </summary>
    /// <remarks>
    /// A hidden pill announces nothing. Raising a live-region event for an
    /// empty string is how a screen reader ends up saying "blank" every time a
    /// dictation finishes.
    /// </remarks>
    public bool ShouldAnnounce => IsVisible;

    /// <summary>
    /// The name the window gives this pill when it shows it without an
    /// announcement: <see cref="UnannouncedName"/> where one is set (the
    /// listening pill's state), otherwise the whole sentence an announced
    /// pill is named by, <see cref="AnnouncedText"/>. Never the first line
    /// alone: the second is out of the automation tree, so a quiet pill named
    /// by its title (the learned correction while the Dictation window is in
    /// front) would lose its "what to do" half. The Mac's label for an ending
    /// is its title and detail too.
    /// </summary>
    public string QuietName => UnannouncedName ?? AnnouncedText;

    // ── How long the last pill stays ─────────────────────────────────────
    //
    // Three numbers rather than two, and the axis is not success/failure. It
    // is: WHO IS CARRYING THE MESSAGE. When the outcome is already legible
    // somewhere else — most of all in the user's own document — the pill is a
    // full stop and should behave like one. When the pill is the only place
    // the reason exists, it has to stay long enough to be read by somebody who
    // was not expecting to have to read anything.
    //
    // The measurement that forced this: the the local dictation flow-history.jsonl
    // records DecMs 769-1778 and InsMs ~500 per dictation, so the HUD was on
    // screen for 3.3-4.3 s after the user stopped speaking — and the last two
    // of those seconds began AFTER the words had already appeared in their
    // application. That tail is what this split removes, and it removes it
    // from exactly the outcomes where nothing is lost by removing it.
    //
    // None of these touch the ~500 ms insertion figure, which is not latency
    // the user sees: it is FocusedTextInserter's clipboard-restore settle,
    // running after the paste keystroke has already been sent. The text is on
    // screen well before it elapses, and shortening it loses the race that
    // makes the target application paste the user's OLD clipboard.

    /// <summary>
    /// How long a pill stays when the outcome is already visible somewhere
    /// other than the pill: 600 ms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was two seconds, and by the time they began the words were in the
    /// user's document. A receipt for something you are looking at does not
    /// need to be held up; the words ARE the receipt, and this is the full
    /// stop after them.
    /// </para>
    /// <para>
    /// <b>Deliberately not zero.</b> "Typed into Slack" is the only pill that
    /// names where the text went, and a pill that is put up and taken down in
    /// the same frame is never seen at all — the state before it ("Typing")
    /// would be the last thing on screen, and a HUD that vanishes the instant
    /// its final state lands reads as a crash rather than as a finish. 600 ms
    /// clears the ~400 ms it takes a change at the edge of vision to be
    /// noticed and read, and stops well short of the point where it becomes
    /// something to wait out.
    /// </para>
    /// <para>
    /// <b>Why a number and not a fade.</b> A fade is opacity, opacity belongs
    /// to the window, and a duration that lives in an animation is a duration
    /// no test on this machine can see. It also cuts the wrong way for the
    /// case that matters: a fade is a period during which the sentence is
    /// still on screen and progressively harder to read, which is a fine
    /// ending for "Typed" and a bad one for a failure nobody has finished
    /// reading. If the window ever grows one, it belongs AFTER the dwell
    /// rather than inside it — see the note on <see cref="FailureDwell"/>.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan ReceiptDwell = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// How long a pill stays when nothing was typed and the reason is about
    /// this attempt rather than about the machine: 2.5 s.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The middle tier exists because the old pair was a binary, and the
    /// binary put "That was too short to be a sentence" on screen for as long
    /// as "Windows is blocking the microphone — see Settings → Earlet".
    /// Both are failures. Only one of them is news, and only one of them has
    /// somewhere to send the user.
    /// </para>
    /// <para>
    /// The number is a reading budget rather than a preference: about half a
    /// second to notice a pill that arrived unexpectedly, plus the five or six
    /// words these sentences run to at a glancing reading speed. It is a floor
    /// the tests hold against the copy itself, so a longer sentence written
    /// into this tier later fails rather than being shown for too little time
    /// to read.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan NoticeDwell = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// How long a failure stays up: 5 s, unchanged. The longest of the three
    /// on purpose — nothing was typed, so the pill is the only place the
    /// reason is said, and there is nothing on screen to read it off instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the tier for outcomes that say something about the machine or
    /// about the user's data rather than about the sentence they just tried to
    /// say: a microphone Windows is refusing, a recogniser that is not there, a
    /// paste that did not land, a clipboard that did not come back. Several of
    /// them carry somewhere to go — "see Settings → Earlet" — and a path is
    /// the slowest thing on this surface to read.
    /// </para>
    /// <para>
    /// <b>Anything added around the dismissal must not eat into this.</b> A
    /// fade, a slide, a collapse: whatever it is, it starts when the dwell ends
    /// rather than overlapping it, or the effective reading time for the one
    /// tier that needs reading time is silently shorter than this number says.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan FailureDwell = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The pill for a state the session is passing through. Null dwell — these
    /// last exactly as long as the state does, and a timer that dismissed
    /// "Listening" mid-sentence would be lying about the microphone.
    /// </summary>
    /// <remarks>
    /// <see cref="DictationState.Idle"/> and <see cref="DictationState.Armed"/>
    /// are both <see cref="Hidden"/>. Armed is the resting state of a feature
    /// that is on all day, and macOS's always-present resting pill is a
    /// separate product decision about permanent always-on-top chrome — not one
    /// to make by accident inside a fix for "is it listening right now".
    /// </remarks>
    public static DictationHudModel For(DictationState state) =>
        For(state, previewShowedWords: false);

    /// <summary>
    /// As <see cref="For(DictationState)"/>, but told whether the live preview
    /// (<c>DICTATION_PREVIEW=1</c>) already showed the user words during
    /// THIS hold — see <see cref="For(DictationSessionResult, bool, bool)"/>'s
    /// remarks for the full reasoning; this overload is the same rule applied
    /// to the two PROCESS pills rather than the outcome pill.
    /// </summary>
    /// <param name="previewShowedWords">
    /// True only when the streaming preview produced at least one non-empty
    /// hypothesis THIS hold. <c>default(bool)</c> is <c>false</c>, which is
    /// exactly the value <see cref="For(DictationState)"/> passes — a caller
    /// that has never heard of the preview, including every build with the
    /// flag off, gets the identical pill this method has always produced.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The owner's decision, verbatim (2026-08-22):</b> "after we release
    /// the button currently it's showing two extra states and I think this is
    /// less important because the user has already seen the persistent flow of
    /// things. It doesn't have to ask for or confirm that it is already typed.
    /// We should only pop up when there are error messages coming from the
    /// dictation." "Working" and "Typing" are exactly the two extra states —
    /// neither is an error message, both fire on every single hold, and with
    /// the live preview on the user has been reading the sentence assemble
    /// itself in the pill for the whole hold already.
    /// </para>
    /// <para>
    /// <b>Gated on <paramref name="previewShowedWords"/>, not on the feature
    /// flag.</b> A flag that is on but produced nothing this hold (the
    /// streaming model is missing, the recogniser errored, or the user simply
    /// said nothing worth transcribing before letting go) has not shown the
    /// user any "persistent flow" to lean on — the owner's OWN justification
    /// for quieting these pills does not hold, and hiding them anyway would
    /// reintroduce exactly the silence <see cref="DictationHudModel"/>'s
    /// header exists to end: a hold that produced no evidence of anything,
    /// anywhere. So the presenter is the one required to have actually WATCHED
    /// a hypothesis arrive before it may pass <c>true</c> here — this method
    /// only ever acts on what it is told.
    /// </para>
    /// </remarks>
    public static DictationHudModel For(DictationState state, bool previewShowedWords) => state switch
    {
        // The quiet half of the pair below. A state change carries no level
        // with it — the microphone has only just opened — so the pill starts at
        // "Listening" and the drain moves it to "Hearing you" the moment there
        // is something to hear.
        DictationState.Listening => Listening(0f),

        // Both are PROCESS pills, not outcomes — they say the machine is busy,
        // never that anything went right or wrong, so there is no failure
        // tier here to protect. Quieted the instant the live preview has
        // already shown the user words this hold; left exactly as before
        // otherwise, which covers the flag being off AND the flag being on
        // with nothing yet shown.
        DictationState.Transcribing => previewShowedWords
            ? Hidden
            : new("Working", HudTone.Neutral, null),
        DictationState.Inserting => previewShowedWords
            ? Hidden
            : new("Typing", HudTone.Neutral, null),
        DictationState.Idle or DictationState.Armed => Hidden,
        // Not a catch-all standing in for the members above — they are all
        // listed. A state added later lands here and shows its own name, which
        // is odd on screen rather than silent, and odd is reportable.
        //
        // The LONGEST dwell, not the shortest. This pill exists to be reported,
        // and the thing being reported is a bare enum member name that means
        // nothing to the person reading it — six hundred milliseconds of that
        // is a flicker somebody half-saw and cannot describe.
        _ => new(state.ToString(), HudTone.Neutral, FailureDwell),
    };

    /// <summary>
    /// The level at or below which the microphone is treated as hearing
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One number, read by three surfaces: the presenter's gate (a residue
    /// below this is published as a hard zero), the window's settle (bars park
    /// flat), and <see cref="Listening(float)"/> below. It used to be declared
    /// separately in each of them — three copies of a constant that has to
    /// agree or the pill says one thing while the bars say another.
    /// </para>
    /// <para>
    /// It is also, deliberately, the same figure the audio path uses to decide
    /// a hold contained no speech, so a meter that twitched on room noise the
    /// recogniser then discarded is not possible.
    /// </para>
    /// </remarks>
    public const float AudibleLevel = 0.004f;

    /// <summary>
    /// The listening pill at a given microphone level.
    /// </summary>
    /// <param name="level">
    /// The smoothed, gated level the meter is drawing, 0..1.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why the label changes at all.</b> macOS's HUD switches between
    /// "Hearing you" and "Listening" at exactly this threshold
    /// (<c>DictationHUDView.swift</c>), and its header records why: in the
    /// field report the waveform alone was too subtle — seven small bars are
    /// something you have to look AT, and the answer to "is this hearing me"
    /// should be readable at a glance.
    /// </para>
    /// <para>
    /// Windows had the same seven bars, the same threshold and a label frozen
    /// at "Listening" at every level, so a muted microphone and a working one
    /// produced the same word. The bars settling flat was the only difference,
    /// and flat bars look like an idle HUD.
    /// </para>
    /// <para>
    /// Strictly greater than, matching the presenter's own gate: a level
    /// exactly at the threshold is published as a hard zero, and a pill that
    /// said "Hearing you" while the bars drew silence would be the
    /// disagreement this constant exists to prevent.
    /// </para>
    /// </remarks>
    public static DictationHudModel Listening(float level) =>
        new(level > AudibleLevel ? "Hearing you" : "Listening",
            HudTone.Neutral,
            null);

    /// <summary>The pill for a dictation that has finished, however it finished.</summary>
    /// <param name="result">The outcome, and the window it was aimed at.</param>
    /// <remarks>
    /// Every member of <see cref="DictationOutcome"/> is spelled out. The enum
    /// keeps <c>NoSpeech</c> and <c>HoldTooShort</c> apart because one is a
    /// microphone that heard silence and the other is a key brushed on the way
    /// past; folding them into "nothing was typed" here would throw away the
    /// distinction the enum exists to preserve, on the one surface the user
    /// actually reads.
    /// </remarks>
    public static DictationHudModel For(DictationSessionResult result)
        => For(result, firstEverInsert: false, previewShowedWords: false);

    /// <summary>
    /// As <see cref="For(DictationSessionResult)"/>, but with the one-time
    /// teaching moment: the user's FIRST successful dictation ever replaces
    /// the terse "Typed" receipt with a sentence that names the feature's
    /// whole contract — it types anywhere the cursor is. Shown exactly once
    /// (the caller owns the once-ness), at notice dwell rather than receipt
    /// dwell because this line teaches instead of confirms. Every later
    /// success gets the ordinary receipt: a pill that kept explaining the
    /// feature would be noise wearing a lesson's clothes.
    /// </summary>
    public static DictationHudModel For(DictationSessionResult result, bool firstEverInsert)
        => For(result, firstEverInsert, previewShowedWords: false);

    /// <summary>
    /// As <see cref="For(DictationSessionResult, bool)"/>, but told whether
    /// the live preview (<c>DICTATION_PREVIEW=1</c>) already showed the
    /// user words during THIS hold — the release-time half of the same
    /// product decision <see cref="For(DictationState, bool)"/> applies to the
    /// process pills. Defaults to <c>false</c> on both overloads above, so
    /// every existing caller — and every run with the preview off —
    /// gets byte-for-byte the receipt this method has always produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only two outcomes are ever quieted by this — see
    /// <see cref="IsQuietedByLivePreview"/> for the full per-member reasoning.
    /// Every failure keeps its dwell and its sentence regardless of
    /// <paramref name="previewShowedWords"/>: the owner's own line drew the
    /// boundary — "we should only pop up when there are error messages coming
    /// from the dictation" — and this file's whole reason for existing is that
    /// silence on a failure is the one thing worse than a pill nobody asked
    /// for.</b>
    /// </para>
    /// <para>
    /// <b>The one-time teaching hint is never quieted.</b> It does not confirm
    /// "your words were typed" — the live preview already did that, which is
    /// exactly why the ordinary receipt IS quieted — it teaches a fact the
    /// preview cannot: that the cursor, wherever it is, is where the words
    /// will land. A user whose preview is always on would otherwise never see
    /// this sentence at all, which is a worse outcome than the one extra pill
    /// it costs, once, ever.
    /// </para>
    /// </remarks>
    public static DictationHudModel For(
        DictationSessionResult result, bool firstEverInsert, bool previewShowedWords)
    {
        ArgumentNullException.ThrowIfNull(result);

        // A comparison hold (one that asked for a baseline; Dictation never
        // asks): the receipt carries the stock decode of the same clip. Checked BEFORE the live-preview
        // quieting — the whole point of the beat is that this line is seen —
        // and only for a delivered hold, so a failure keeps its own sentence.
        // One pill line, not a second row: the HUD is a fixed-width pill with
        // a single status TextBlock (DictationHudWindow.xaml), and the caption
        // slot is the live preview's and fades the status out, so a true
        // second line was not cheap. The sentence is trimmed with an
        // ellipsis by the window when it is longer than the pill.
        if (result.BaselineText is { } baseline && result.TextWasDelivered)
        {
            return Compare(result, baseline);
        }

        if (previewShowedWords
            && IsQuietedByLivePreview(result.Outcome)
            && !(result.Outcome == DictationOutcome.Inserted && firstEverInsert))
        {
            return Hidden;
        }

        var target = result.Target?.ProcessName;
        var into = string.IsNullOrWhiteSpace(target) ? string.Empty : $" into {target}";

        return result.Outcome switch
        {
            // The one-time hint outranks the receipt — see the overload doc.
            DictationOutcome.Inserted when firstEverInsert =>
                new("That worked — it types anywhere your cursor is",
                    HudTone.Neutral, NoticeDwell),

            // The whole reason the tiers exist. The words are already in the
            // user's application by the time this renders — the pill is
            // confirming something they can read for themselves — so it says
            // where they went and gets out of the way.
            DictationOutcome.Inserted =>
                new($"Typed{into}", HudTone.Neutral, ReceiptDwell),

            // The one outcome that is both a delivery and a failure. Caution,
            // and the long dwell, because something the user owns was lost and
            // they will want to know before they next press paste.
            DictationOutcome.ClipboardRestoreFailed =>
                new($"Typed{into} — your clipboard did not come back",
                    HudTone.Caution, FailureDwell),

            // Cancelling is something the user did on purpose. Acknowledged,
            // not warned about — and not explained either, which is why it
            // shares the receipt tier with a success rather than the notice
            // tier with the outcomes that have something to tell. The user
            // already knows; they are the one who did it.
            DictationOutcome.Cancelled =>
                new("Cancelled", HudTone.Neutral, ReceiptDwell),

            // The notice tier: nothing was typed, and the reason is about the
            // sentence the user just tried to say rather than about the
            // machine. There is nowhere to send them and nothing to go and
            // change — the answer to both of these is "hold it down and say it
            // again" — so they need long enough to read six words and no
            // longer.
            DictationOutcome.HoldTooShort =>
                new("That was too short to be a sentence", HudTone.Caution, NoticeDwell),
            DictationOutcome.MicrophoneUnavailable =>
                new("The microphone would not open", HudTone.Caution, FailureDwell),

            // Named, not folded into the line above. The two are the same
            // failure to the audio stack and completely different failures to
            // the person: one is "something is wrong", the other is "a switch
            // on this computer is off and you can turn it on". The pill cannot
            // carry the button, so it carries where the button is — the pane's
            // arm card offers the deep link.
            DictationOutcome.MicrophoneBlocked =>
                new("Windows is blocking the microphone — see Settings → Earlet",
                    HudTone.Caution, FailureDwell),
            // Back to the failure tier, and the line between it and NoSpeech
            // above is the line the whole three-way split is drawn on: this is
            // the machine not working, not the user not talking. A device that
            // opens and delivers no samples is something wrong with the
            // computer, and the user is about to go looking for it.
            DictationOutcome.NoAudio =>
                new("The microphone opened and delivered nothing", HudTone.Caution, FailureDwell),
            // Notice, not failure, and the pair with HoldTooShort above: the
            // microphone worked, the recogniser ran, and the two of them
            // disagree with the user about whether anything was said. Kept
            // apart from NoAudio below on purpose — that one is the machine
            // failing, this one is a room being quieter than it sounded.
            DictationOutcome.NoSpeech =>
                new("Nothing worth typing was heard", HudTone.Caution, NoticeDwell),
            DictationOutcome.RecognizerUnavailable =>
                new("There is no speech recogniser to ask", HudTone.Caution, FailureDwell),
            DictationOutcome.RecognizerFailed =>
                new("The speech recogniser failed", HudTone.Caution, FailureDwell),
            DictationOutcome.NoFocusedWindow =>
                new("There were words and no window to put them in", HudTone.Caution, FailureDwell),
            DictationOutcome.InsertionRefused =>
                new($"The paste did not land{into}", HudTone.Caution, FailureDwell),

            // The sentence macOS insists on. "Dictation is broken" is what a
            // user concludes from silence here, and the difference between that
            // and "you are in a password field" is the difference between an
            // app that failed and an app that noticed. Caution rather than
            // neutral: nothing was typed, and the user is waiting for words.
            //
            // The process name is deliberately NOT appended. The pill is
            // visible to whoever is behind the user and to whatever is
            // screen-sharing, and naming the application somebody is signing
            // into is a fact worth withholding on that surface.
            DictationOutcome.SecureField =>
                new("You are in a password field — nothing was recorded",
                    HudTone.Caution, FailureDwell),

            // Neutral, because the command ran — there is nothing to warn
            // about. But the NOTICE tier, not the receipt tier beside Inserted,
            // and TheReceiptTierIsONLYForOutcomesTheUserCanSeeForThemselves is
            // right to pin that set closed. The receipt tier is for outcomes
            // the user can already see: Inserted puts the words in front of
            // them, Cancelled is their own hand. This one is the opposite —
            // they spoke expecting text and got a command instead, and this
            // pill is the ONLY place they learn Dictation made that choice. An
            // outcome that carries information nobody else says has to stay up
            // long enough to be read.
            //
            // The command text is NOT shown. Same rule as the password field
            // below: this pill is visible to whoever is behind the user and to
            // whatever is screen-sharing, and "send Casey a message" names a
            // person.
            // Two members, not one: `SpokenCommand` on main and
            // `RanWorkflow`/`WorkflowNotMatched` on demo were the same idea
            // built twice, and they collided on enum value 13. The pair wins
            // because it carries the failure the single member cannot say —
            // the router was addressed and nothing answered, which is otherwise
            // indistinguishable from a dead microphone.
            //
            // Neutral, not caution: nothing failed. The words went somewhere,
            // just not to the cursor. This pill only has to stop the user
            // waiting for text that is not coming.
            DictationOutcome.RanWorkflow =>
                new("Ran a workflow", HudTone.Neutral, NoticeDwell),

            // The one failure the shared key introduces. What was heard stays
            // off this pill by the same rule as above: it is visible to
            // whoever is behind the user and to whatever is screen-sharing.
            DictationOutcome.WorkflowNotMatched =>
                new("Heard you, but nothing matched", HudTone.Caution, NoticeDwell),

            // The only pill in this file that confirms text was REMOVED, and
            // the one place a neutral tone would be wrong: the user is watching
            // words disappear from a document Dictation does not own, and a
            // pill that reads like an ordinary receipt would not tell them
            // Dictation meant it. Says what went, not what was said — the phrase itself
            // is never shown here, for the same shoulder-surfing reason the
            // workflow arms above keep the transcript off this pill.
            DictationOutcome.VoiceDeleted =>
                new("Deleted the last thing I typed", HudTone.Neutral, NoticeDwell),

            // Not a failure, and the wording has to carry that: the feature
            // worked and found nothing of its own to remove. "Nothing to
            // delete" would read as a fault; "I have nothing here to delete"
            // says whose text is in scope, which is the whole safety rule —
            // Dictation only ever erases its own last insertion.
            DictationOutcome.VoiceDeleteNothingToDelete =>
                new("I have nothing here to delete", HudTone.Neutral, NoticeDwell),

            // All four guarded refusals land here — password field, a different
            // window, too old, too long. Caution rather than neutral because
            // the user asked for something and did not get it, and the sentence
            // is deliberately about WHERE rather than why: the caret has moved
            // on from the text Dictation typed, which is true of every one of the
            // four and is the only thing the user can act on.
            // FailureDwell, not NoticeDwell, and the dwell test is what settled
            // it: this sentence carries a REASON, and at the notice tier's
            // 2.5 s it was on screen for less time than it takes to read. It
            // also belongs with its siblings — InsertionRefused and SecureField
            // are the same shape of event, the user asked for something and did
            // not get it, and this file's rule is that every failure sentence
            // stays exactly as loud.
            DictationOutcome.VoiceDeleteRefused =>
                new("Can't delete here — the cursor has moved on", HudTone.Caution, FailureDwell),

            // As above: the arm a NEW enum member lands in. It renders the
            // member name rather than nothing, so an unhandled outcome shows up
            // as something odd on screen instead of as a pill that never came.
            _ => new(result.Outcome.ToString(), HudTone.Caution, FailureDwell),
        };
    }

    /// <summary>
    /// How long the comparison receipt (stock decode against the biased one)
    /// stays: about 4 s, greyed. Longer than <see cref="NoticeDwell"/> because the audience is
    /// reading a sentence they did not write, against one they just heard.
    /// </summary>
    public static readonly TimeSpan CompareDwell = TimeSpan.FromSeconds(4);

    /// <summary>The prefix the compare receipt puts before the stock decode —
    /// the phrase lens-4 specifies, and the string a test can look for.</summary>
    public const string CompareLabel = "without your vocabulary:";

    private static DictationHudModel Compare(DictationSessionResult result, string baseline)
    {
        var target = result.Target?.ProcessName;
        var into = string.IsNullOrWhiteSpace(target) ? string.Empty : $" into {target}";
        var stock = baseline.Trim();
        var shown = stock.Length == 0 ? "(nothing)" : $"\u201c{stock}\u201d";
        return new($"Typed{into} \u00b7 {CompareLabel} {shown}", HudTone.Neutral, CompareDwell);
    }

    /// <summary>
    /// Whether this outcome is a plain confirmation with nothing left to say
    /// once the live preview has already shown the user words this hold — the
    /// classification <see cref="For(DictationSessionResult, bool, bool)"/>
    /// gates on. <c>true</c> for exactly two members. Every other member is
    /// listed too, explicitly <c>false</c>, and the reasoning for each is
    /// worth writing down individually: silence is the failure this whole file
    /// exists to prevent, and a wrong <c>true</c> here would reintroduce it one
    /// outcome at a time, quietly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inserted and Cancelled: quieted.</b> Both are the user watching
    /// their own action land — the words they saw assembled live now sitting
    /// in the document, or the hold they themselves cut short — and a receipt
    /// for either is confirming something already witnessed. (The one-time
    /// teaching hint on <c>Inserted</c> is intercepted one call up, in
    /// <see cref="For(DictationSessionResult, bool, bool)"/>, before this
    /// method is even consulted — it is not a receipt, so this classification
    /// never has to account for it.)
    /// </para>
    /// <para>
    /// <b>HoldTooShort, NoAudio, NoSpeech: never quieted, though the gate
    /// rarely reaches them.</b> Each means "the preview had nothing to show
    /// either" by construction — a hold too brief, a dead microphone, or a
    /// recognizer that heard nothing worth returning gives the streaming pass
    /// nothing to display, so <c>previewShowedWords</c> is already <c>false</c>
    /// for essentially every real occurrence and
    /// <see cref="For(DictationSessionResult, bool, bool)"/>'s own gate never
    /// reaches these three regardless of what this method answers. Declared
    /// <c>false</c> anyway, on purpose: the day a streaming engine changes and
    /// produces a hypothesis the accurate pass then disagrees with, "the user
    /// watched words fill the pill and was then told nothing worth typing was
    /// heard" is precisely the silence this HUD exists to prevent, and this
    /// line is what keeps that true without anyone having to remember why.
    /// </para>
    /// <para>
    /// <b>MicrophoneUnavailable, MicrophoneBlocked, RecognizerUnavailable,
    /// RecognizerFailed, NoFocusedWindow, InsertionRefused: never quieted.</b>
    /// The owner's own boundary — "we should only pop up when there are error
    /// messages coming from the dictation" — and these ARE the error messages.
    /// </para>
    /// <para>
    /// <b>ClipboardRestoreFailed: never quieted, and this is the interesting
    /// one.</b> <see cref="DictationSessionResult.TextWasDelivered"/> is
    /// <c>true</c> here — the words DID land, and a live-preview user DID
    /// watch them appear — so by the "already witnessed" reasoning above this
    /// looks like a candidate for silence. It is not one, because what this
    /// pill reports is not "did the words arrive" but "your clipboard was just
    /// overwritten and did not come back", a fact the live preview cannot
    /// possibly have shown (it streams recognised speech, never clipboard
    /// state) and one with a real, DELAYED cost: the next thing the user
    /// pastes — a password, a link, a block of code — is silently wrong,
    /// possibly minutes later, with no other cue that anything happened.
    /// Quieting this is the one silence in this feature that could cost a user
    /// actual data, not just a moment's reassurance.
    /// </para>
    /// <para>
    /// <b>SecureField: never quieted, and structurally can't be reached
    /// wrongly.</b> Same belt-and-braces reasoning as the trio above:
    /// <c>DictationPreviewSession.Start()</c> probes for a password field at
    /// the PRESS and refuses to open a stream at all when it finds one, so
    /// <c>previewShowedWords</c> is already <c>false</c> for every real
    /// <c>SecureField</c> outcome. Declared <c>false</c> here too, because a
    /// privacy notice must never depend on a second, independent signal
    /// agreeing to let it through.
    /// </para>
    /// <para>
    /// <b>RanWorkflow, WorkflowNotMatched: never quieted.</b> The live preview
    /// shows WORDS, never what Dictation decided to do with them. Whether a spoken
    /// sentence ran as a command, or was heard and matched nothing, is
    /// information the preview cannot carry at all — these two pills are the
    /// only place that fact is ever said, not a restatement of something
    /// already on screen.
    /// </para>
    /// <para>
    /// <b>The default arm: never quieted.</b> Matches
    /// <see cref="For(DictationSessionResult, bool, bool)"/>'s own default —
    /// an outcome added later and not named above is left alone rather than
    /// silenced by omission, so a missing decision about a NEW failure fails
    /// loud instead of disappearing because it happened to land on a
    /// preview-active hold.
    /// </para>
    /// </remarks>
    private static bool IsQuietedByLivePreview(DictationOutcome outcome) => outcome switch
    {
        DictationOutcome.Inserted => true,
        DictationOutcome.Cancelled => true,

        DictationOutcome.HoldTooShort => false,
        DictationOutcome.NoAudio => false,
        DictationOutcome.NoSpeech => false,

        DictationOutcome.MicrophoneUnavailable => false,
        DictationOutcome.MicrophoneBlocked => false,
        DictationOutcome.RecognizerUnavailable => false,
        DictationOutcome.RecognizerFailed => false,
        DictationOutcome.NoFocusedWindow => false,
        DictationOutcome.InsertionRefused => false,

        DictationOutcome.ClipboardRestoreFailed => false,
        DictationOutcome.SecureField => false,

        DictationOutcome.RanWorkflow => false,
        DictationOutcome.WorkflowNotMatched => false,

        // All three are false, and VoiceDeleted is the one worth arguing.
        // The live preview showing words this hold is exactly what makes a
        // "Typed" receipt redundant — the user watched the sentence assemble.
        // It makes a DELETION receipt more necessary, not less: what they
        // watched assemble is the thing that just disappeared, and the preview
        // said nothing about that. The other two are refusals, and this file's
        // rule is that every failure sentence stays exactly as loud.
        DictationOutcome.VoiceDeleted => false,
        DictationOutcome.VoiceDeleteNothingToDelete => false,
        DictationOutcome.VoiceDeleteRefused => false,

        _ => false,
    };
}

/// <summary>
/// A delay that can be cancelled before it runs — the one seam between the
/// dwell rule and a clock.
/// </summary>
/// <remarks>
/// <para>
/// It exists so the rule below can be asserted without a dispatcher and
/// without sleeping. A test that proves "a hide scheduled two seconds ago does
/// not fire once a new dictation has started" by waiting two seconds is a test
/// that takes two seconds, runs on one machine's timer resolution, and passes
/// for the wrong reason on a loaded build agent.
/// </para>
/// <para>
/// The shape is deliberately the smallest one that can carry the rule.
/// Disposing the handle cancels; that is the entire contract, and it is what a
/// <c>DispatcherTimer</c>, a <c>System.Threading.Timer</c> and a list in a test
/// can all honour without either side learning anything about the other.
/// </para>
/// </remarks>
public interface IDictationHudDelay
{
    /// <summary>
    /// Run <paramref name="run"/> after <paramref name="delay"/>. Disposing
    /// the returned handle cancels it if it has not run yet.
    /// </summary>
    IDisposable After(TimeSpan delay, Action run);
}

/// <summary>
/// The pending "take the pill away", and the single place it is cancelled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not just a timer at the call site.</b> Shortening the
/// success dwell to 600 ms moves the hide from "long after the user has moved
/// on" to "possibly while they are already holding the key down again". The
/// rule that makes that safe — a new pill cancels the previous pill's hide
/// rather than being hidden by it — is a product rule, and until now it existed
/// only as a side effect of the WPF window restarting a <c>DispatcherTimer</c>
/// inside <c>Update</c>. That is a real implementation and a fine one; it is
/// also in a file that is chrome, that is periodically rewritten, and that no
/// test on a machine without WPF can reach. The rule now has an owner that can
/// be read and asserted on its own.
/// </para>
/// <para>
/// <b>The two failure modes it is shaped around.</b> A STRANDED hide is a pill
/// that never goes away because the timer was cancelled and nothing re-armed
/// it — prevented by arming from the model that is going on screen, every
/// time, in the same call. A MIS-FIRED hide is the previous dictation's timer
/// taking down the current dictation's pill; cancellation alone does not
/// prevent it, because a timer whose tick is already queued on the dispatcher
/// will deliver that tick after it has been stopped. The generation counter is
/// what makes the late arrival a no-op instead of a HUD that disappears
/// mid-sentence.
/// </para>
/// </remarks>
public sealed class DictationHudDwell : IDisposable
{
    private readonly IDictationHudDelay _delay;
    private readonly Action _hide;

    /// <summary>
    /// Bumped by every cancel and by every hide that actually ran. A callback
    /// holding a stale value is a tick from a pill that is no longer on screen.
    /// </summary>
    private int _generation;

    private IDisposable? _pending;
    private bool _disposed;

    /// <param name="delay">How a delay is obtained. See <see cref="IDictationHudDelay"/>.</param>
    /// <param name="hide">
    /// What taking the pill away means. Called at most once per armed dwell,
    /// and never after <see cref="Cancel"/> or <see cref="Dispose"/>.
    /// </param>
    public DictationHudDwell(IDictationHudDelay delay, Action hide)
    {
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _hide = hide ?? throw new ArgumentNullException(nameof(hide));
    }

    /// <summary>Whether a hide is armed and has not run.</summary>
    public bool HideIsPending => _pending is not null;

    /// <summary>
    /// <paramref name="model"/> is now the pill on screen. Cancels whatever the
    /// previous pill armed, then arms this one's own dwell if it has one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called for EVERY model, including the ones that carry no dwell and the
    /// ones that are not visible at all. That is the whole overlap rule: a
    /// second dictation reaching <see cref="DictationState.Listening"/> during
    /// the first one's 600 ms passes a null dwell through here, which cancels
    /// and arms nothing — so "Listening" cannot be taken down by a receipt for
    /// a dictation that finished before it started.
    /// </para>
    /// </remarks>
    public void Showing(DictationHudModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Unconditionally, and before anything else. Every path out of this
        // method leaves the previous pill's hide cancelled.
        Cancel();

        if (_disposed
            || !model.IsVisible
            || model.Dwell is not { } dwell
            || dwell <= TimeSpan.Zero)
        {
            return;
        }

        var mine = _generation;
        var handle = _delay.After(dwell, () =>
        {
            if (_disposed || mine != _generation)
            {
                return;
            }

            // Spent. A second delivery of the same tick — which a stopped
            // DispatcherTimer can still make — now looks stale to the check
            // above rather than hiding a pill that has been replaced since.
            _generation++;
            _pending = null;
            _hide();
        });

        // A delay implementation that ran synchronously has already finished
        // and bumped the generation; assigning its handle here would leave
        // HideIsPending claiming a hide that has been and gone.
        if (mine == _generation)
        {
            _pending = handle;
        }
    }

    /// <summary>Forget any armed hide. The pill stays until something else takes it down.</summary>
    public void Cancel()
    {
        _generation++;
        var pending = _pending;
        _pending = null;
        pending?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
