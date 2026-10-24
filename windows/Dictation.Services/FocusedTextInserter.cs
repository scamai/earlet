using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Services;

/// <summary>What the insert managed to do.</summary>
/// <remarks>
/// The vocabulary is deliberately small, and one invariant runs through all of
/// it: <see cref="Success"/> means the paste keystroke went <em>and</em> the
/// user's clipboard is back the way they left it. Anything less has its own
/// name.
/// </remarks>
public enum FocusedInsertionOutcome
{
    /// <summary>
    /// The insert did not happen, and the reason is on the result. Covers an
    /// empty transcript, a clipboard that would not take the text, a paste
    /// keystroke that reported failure, and a paste that threw.
    /// <b>Zero is this state deliberately</b>, for the same reason
    /// <see cref="FocusedApplicationStatus.NoFocusedWindow"/> is: a defaulted
    /// result must claim the thing that did not happen, never the thing that
    /// did.
    /// </summary>
    InsertionRefused = 0,

    /// <summary>
    /// The text was pasted into the focused window and the clipboard the user
    /// had was put back. The <em>only</em> outcome that claims both.
    /// </summary>
    /// <remarks>
    /// One reading of "put back" needs saying: when something else wrote the
    /// clipboard while the paste was settling — the user copied again, a
    /// password manager filled a field — that newer content is left where it
    /// is and this is still Success, with the reason on the result. The user's
    /// clipboard holds what they or their tools last chose, which is the thing
    /// a restore exists to protect; writing the older snapshot over it would be
    /// the clipboard-eating bug arriving from the other side.
    /// </remarks>
    Success = 1,

    /// <summary>
    /// Nothing had focus, so there was nowhere to type. Nothing was
    /// synthesised and the clipboard was not touched.
    /// </summary>
    NoFocusedWindow = 2,

    /// <summary>
    /// The paste went, but the user's clipboard could not be restored — so
    /// their copied text is gone and the dictated text is sitting in its place.
    /// Separate from <see cref="Success"/> because the insert worked and the
    /// side effect did not, and a caller that treats those as the same thing
    /// will never tell the user their clipboard was eaten.
    /// </summary>
    ClipboardRestoreFailed = 3,

    /// <summary>
    /// The window that would receive the keystroke changed between resolving
    /// the target and sending it, so nothing was typed. A refusal, and
    /// deliberately its own name rather than
    /// <see cref="InsertionRefused"/>: "the user alt-tabbed mid-insert" and
    /// "the clipboard would not take the text" want different words in a log
    /// and, one day, different words on screen.
    /// </summary>
    /// <remarks>
    /// <c>SendInput</c> injects at the keyboard, not at a window — see
    /// <c>Win32PasteKeystroke</c>'s remarks. The only thing that makes a paste
    /// land where it was aimed is that focus has not moved since it was aimed,
    /// and until this outcome existed nothing checked.
    /// </remarks>
    FocusMoved = 4,
}

/// <summary>
/// What happened, where, and why.
/// </summary>
/// <remarks>
/// A <c>readonly record struct</c> for the same reason
/// <see cref="FocusedApplicationResult"/> is one: there is no null answer to
/// "what happened", and no ordinary failure is worth an exception.
/// </remarks>
public readonly record struct FocusedInsertionResult
{
    private readonly string? _reason;

    private FocusedInsertionResult(
        FocusedInsertionOutcome outcome,
        string? reason,
        FocusedApplicationResult target)
    {
        Outcome = outcome;
        _reason = reason;
        Target = target;
    }

    /// <summary>What the insert managed to do.</summary>
    public FocusedInsertionOutcome Outcome { get; }

    /// <summary>
    /// The underlying reason, in the words of whatever produced it — a Win32
    /// seam returning <c>false</c>, or the message of an exception it threw.
    /// Empty rather than null when there is nothing to add, so this is always
    /// safe to log.
    /// </summary>
    public string Reason => _reason ?? string.Empty;

    /// <summary>
    /// The window this was aimed at, carried whole so a failure reads as one
    /// sentence rather than as an outcome with no subject.
    /// </summary>
    public FocusedApplicationResult Target { get; }

    /// <summary>
    /// Whether the paste keystroke was actually delivered. True for
    /// <see cref="FocusedInsertionOutcome.Success"/> and
    /// <see cref="FocusedInsertionOutcome.ClipboardRestoreFailed"/> — the two
    /// outcomes that got there by typing — and false for both refusals. This
    /// property, not the enum's name, is the thing a caller should branch on
    /// when the question is "did the user's document change?".
    /// </summary>
    public bool TextWasDelivered => Outcome
        is FocusedInsertionOutcome.Success
            or FocusedInsertionOutcome.ClipboardRestoreFailed;

    internal static FocusedInsertionResult Succeeded(FocusedApplicationResult target, string? note = null) =>
        new(FocusedInsertionOutcome.Success, note, target);

    internal static FocusedInsertionResult NoFocusedWindow(FocusedApplicationResult target) =>
        new(FocusedInsertionOutcome.NoFocusedWindow, null, target);

    internal static FocusedInsertionResult Refused(FocusedApplicationResult target, string reason) =>
        new(FocusedInsertionOutcome.InsertionRefused, reason, target);

    internal static FocusedInsertionResult RestoreFailed(FocusedApplicationResult target, string reason) =>
        new(FocusedInsertionOutcome.ClipboardRestoreFailed, reason, target);

    internal static FocusedInsertionResult FocusMoved(FocusedApplicationResult target, string reason) =>
        new(FocusedInsertionOutcome.FocusMoved, reason, target);
}

/// <summary>
/// Which window inside the foreground application would actually receive a
/// synthesised keystroke.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the top-level window is not enough.</b> Windows 11 Notepad ate a
/// 358-character transcript while <c>GetForegroundWindow</c> kept answering
/// "Notepad" the whole time: the keystroke reached the right PROCESS and the
/// wrong WINDOW inside it, because a lone Alt had armed the menu bar and
/// keyboard focus had moved from the document surface
/// (<c>RichEditD2DPT</c>) onto the menu-bar island
/// (<c>InputSiteWindowClass</c>). Measured on the device, 2026-08-20. A
/// resolver that answers with a process name cannot see that; this can.
/// </para>
/// <para>
/// <b>Opaque, not an HWND.</b>
/// <see cref="IFocusedApplicationResolver"/>'s remarks refuse a seam that
/// hands back or takes a window handle, because that would put the platform
/// back in the abstraction and let a unit test reach a P/Invoke through it.
/// A <c>long</c> and a class name are values: a fake returns them, and the
/// class name is there because it is the half a human reads in a log.
/// </para>
/// </remarks>
public readonly record struct KeyboardFocusTarget
{
    private readonly string? _className;

    private KeyboardFocusTarget(bool wasRead, long handle, string? className)
    {
        WasRead = wasRead;
        Handle = handle;
        _className = className;
    }

    /// <summary>
    /// Whether the focus target was read at all. <c>false</c> means this
    /// process could not find out — never "nothing has focus", which is
    /// <see cref="Handle"/> zero on a successful read.
    /// </summary>
    public bool WasRead { get; }

    /// <summary>
    /// The focused window, as an opaque number. Zero when nothing inside the
    /// foreground thread holds keyboard focus, which is itself an answer.
    /// </summary>
    public long Handle { get; }

    /// <summary>
    /// The focused window's class — <c>RichEditD2DPT</c>,
    /// <c>InputSiteWindowClass</c>, <c>Edit</c>. Empty rather than null, so
    /// this is always safe to log.
    /// </summary>
    public string ClassName => _className ?? string.Empty;

    /// <summary>Nothing could be read.</summary>
    public static KeyboardFocusTarget Unknown { get; } = new(false, 0, null);

    /// <summary>A read that succeeded, whatever it found.</summary>
    public static KeyboardFocusTarget Of(long handle, string? className) =>
        new(true, handle, className ?? string.Empty);

    /// <summary>
    /// Whether this and <paramref name="other"/> are known to be DIFFERENT
    /// windows. Two unread targets, or one, answer <c>false</c>: an unknown is
    /// not evidence of a move, and refusing to type on the strength of a
    /// failed read would break every host that has no probe wired at all.
    /// </summary>
    public bool DiffersFrom(KeyboardFocusTarget other) =>
        WasRead && other.WasRead && Handle != other.Handle;

    /// <summary>How this reads in a log line.</summary>
    public override string ToString() =>
        !WasRead ? "unknown"
            : Handle == 0 ? "nothing"
            : ClassName.Length > 0 ? $"{ClassName} #0x{Handle:x}"
            : $"#0x{Handle:x}";
}

/// <summary>
/// Reading which window keyboard focus is actually on, so the thing that
/// synthesises a keystroke can tell whether it is still aimed where it was
/// pointed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovered, not injected</b>, exactly like
/// <see cref="IClipboardTextSnapshot"/> next door and for the same reason:
/// <see cref="FocusedTextInsertion"/> tests the
/// <see cref="IFocusedApplicationResolver"/> it was given for this interface,
/// so the thing that resolved the foreground window and the thing that reads
/// the focus inside it are necessarily looking at the same desktop. A
/// separately injected probe could be wired to a different one and would then
/// report a move on every insertion.
/// </para>
/// <para>
/// Optional. A resolver that does not implement it costs the inserter nothing
/// but the check — every outcome behaves as it did before, and the log line
/// says <c>unknown</c> instead of naming a window.
/// </para>
/// </remarks>
public interface IKeyboardFocusProbe
{
    /// <summary>
    /// Reads the window that would receive a keystroke synthesised right now.
    /// Returns <see cref="KeyboardFocusTarget.Unknown"/> rather than throwing
    /// when it cannot be read — this is called on the path that types the
    /// user's sentence, and an exception here must never be the reason the
    /// sentence is lost.
    /// </summary>
    KeyboardFocusTarget Read();
}

/// <summary>
/// Reading the clipboard, so the thing that overwrites it can put it back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all:</b> <see cref="IClipboard"/> is write-only by
/// design, and its own documentation says why — the voice-workflows inserter
/// leaves the message on the clipboard on purpose, so a human can finish a
/// failed paste with Ctrl+V. Dictation is the opposite case. The user pressed
/// a key to put a sentence in a text box, and eating whatever they had copied
/// on the way is a bug, not a fallback. So dictation needs a read that
/// workflows deliberately does not, and the honest way to get it is an
/// additive capability rather than a second method on
/// <see cref="IClipboard"/>: adding one there would change a public interface
/// two projects and a test fake already implement, for a guarantee only this
/// caller wants.
/// </para>
/// <para>
/// <b>Why it is discovered rather than injected:</b>
/// <see cref="FocusedTextInsertion"/> tests the <see cref="IClipboard"/> it was
/// given for this interface instead of taking a second constructor parameter.
/// One clipboard object, so the thing read from and the thing written to are
/// necessarily the same clipboard — a separately injected reader could be
/// wired to a different one, and restore would then write somebody else's text
/// back over the user's. It also keeps the constructor to exactly the three
/// seams this class is allowed to compose from.
/// </para>
/// <para>
/// Text only. A clipboard holding an image or a file list reads as
/// "no text", and this seam cannot put such a payload back — see
/// <see cref="FocusedTextInsertion"/> for what that does to the outcome.
/// <see cref="IClipboardSnapshot"/> is the whole-clipboard version and wins
/// when a clipboard offers both; this one stays as the fallback for a
/// clipboard that can only read text, so every implementation and fake
/// written against it keeps compiling and keeps its behaviour.
/// </para>
/// </remarks>
public interface IClipboardTextSnapshot
{
    /// <summary>
    /// Reads the clipboard's text. <c>true</c> when the clipboard was read at
    /// all — <paramref name="text"/> is then null if it simply held no text,
    /// which is a successful read of an empty answer. <c>false</c> when it
    /// could not be read, which is the case that makes a restore impossible.
    /// </summary>
    bool TryRead(out string? text);
}

/// <summary>
/// Places a string at the caret of whatever window the user is looking at.
/// </summary>
public interface IFocusedTextInserter
{
    /// <summary>
    /// Reads the foreground window and, if there is one, pastes
    /// <paramref name="text"/> into it, leaving the clipboard as it was found.
    /// Never throws for an ordinary failure — every way this can not work is a
    /// <see cref="FocusedInsertionOutcome"/>.
    /// </summary>
    /// <remarks>
    /// Async because a correct paste has a WAIT in the middle of it: the
    /// synthesised Ctrl+V is queued, but the target application reads the
    /// clipboard on its own message loop some time later, so the borrowed
    /// clipboard must not be restored until that read has plausibly happened.
    /// The macOS inserter carries the same fixed delay for the same reason.
    /// <paramref name="ct"/> is honoured only BEFORE the clipboard is
    /// overwritten — from that point the operation must run to the restore,
    /// because cancelling out of it would leave the transcript squatting on
    /// the user's clipboard.
    /// </remarks>
    Task<FocusedInsertionResult> InsertAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// What an undo managed to do.
/// </summary>
/// <remarks>
/// <para>
/// A <c>readonly record struct</c> for the reason
/// <see cref="FocusedInsertionResult"/> is one, and with the same posture: no
/// ordinary failure here is worth an exception, and a defaulted value must
/// claim the thing that did NOT happen. A default is
/// <see cref="Undone"/> false with a count of zero — nothing was deleted —
/// which is safe even though <see cref="DictationUndoRefusal.None"/> is the
/// permissive member of its enum, because the two fields are read together.
/// </para>
/// <para>
/// <b>Two different "no" answers, deliberately kept apart.</b>
/// <see cref="Refusal"/> is <see cref="DictationUndoPolicy"/>'s verdict,
/// carried through UNCHANGED so the HUD can finish the user's sentence —
/// "you have moved to another window" and "that was two minutes ago" are
/// different facts and a user told the wrong one goes looking in the wrong
/// place. <see cref="Refusal"/> of <see cref="DictationUndoRefusal.None"/>
/// with <see cref="Undone"/> false is the OTHER kind: the policy allowed the
/// delete and something below it could not carry it out, and
/// <see cref="Reason"/> says what.
/// </para>
/// <para>
/// No new member on <c>DictationOutcome</c>. That enum is switched over
/// exhaustively in the HUD and it belongs to the dictation state machine, not
/// to the inserter; a caller turns this into whatever that enum needs.
/// </para>
/// </remarks>
public readonly record struct FocusedUndoResult
{
    private readonly string? _reason;

    private FocusedUndoResult(
        bool undone, DictationUndoRefusal refusal, int backspaceCount, string? reason)
    {
        Undone = undone;
        Refusal = refusal;
        BackspaceCount = backspaceCount;
        _reason = reason;
    }

    /// <summary>
    /// Whether any backspace was actually delivered. The field a caller
    /// branches on when the question is "did the user's document change?",
    /// exactly as <see cref="FocusedInsertionResult.TextWasDelivered"/> is on
    /// the way in.
    /// </summary>
    public bool Undone { get; }

    /// <summary>
    /// The policy's verdict, verbatim.
    /// <see cref="DictationUndoRefusal.None"/> when it did not refuse — which
    /// is not the same as the delete having happened; see <see cref="Undone"/>.
    /// </summary>
    public DictationUndoRefusal Refusal { get; }

    /// <summary>
    /// How many backspaces were sent. Zero on every refusal, and on a partial
    /// run it is what actually went rather than what was decided — a number
    /// that claimed the decision would hide exactly the case where half a
    /// sentence is still on screen.
    /// </summary>
    public int BackspaceCount { get; }

    /// <summary>
    /// Why the delete did not happen when the policy allowed it, in the words
    /// of whatever produced it. Empty rather than null when there is nothing to
    /// add, so this is always safe to log.
    /// </summary>
    public string Reason => _reason ?? string.Empty;

    internal static FocusedUndoResult Refused(DictationUndoRefusal refusal) =>
        new(false, refusal, 0, null);

    internal static FocusedUndoResult Undid(int backspaces) =>
        new(true, DictationUndoRefusal.None, backspaces, null);

    internal static FocusedUndoResult PartlyUndid(int sent, int decided) =>
        new(
            true,
            DictationUndoRefusal.None,
            sent,
            $"only {sent} of {decided} backspaces were delivered");

    internal static FocusedUndoResult CouldNotSend(string reason) =>
        new(false, DictationUndoRefusal.None, 0, reason);
}

/// <summary>
/// Taking back the last thing that was inserted — the Windows half of
/// "scratch that".
/// </summary>
/// <remarks>
/// <para>
/// <b>A separate interface rather than a second method on
/// <see cref="IFocusedTextInserter"/>, and the reason is the one
/// <see cref="IClipboardTextSnapshot"/> already gives next door:</b> adding a
/// member there would change a public interface two projects and several test
/// fakes already implement, for a capability exactly one caller wants. So it
/// is additive, and a caller discovers it on the inserter it was handed
/// (<c>if (inserter is IFocusedTextUndo undo)</c>) the same way
/// <see cref="FocusedTextInsertion"/> discovers
/// <see cref="IKeyboardFocusProbe"/> on its resolver.
/// </para>
/// <para>
/// Implemented by the same object that does the inserting, and that is not
/// decoration: the record an undo acts on is written by the insert, and a
/// second class holding the delete would be a second opinion about what was
/// typed.
/// </para>
/// </remarks>
public interface IFocusedTextUndo
{
    /// <summary>
    /// Removes what Dictation last typed, if every gate in
    /// <see cref="DictationUndoPolicy"/> agrees. Never throws for an ordinary
    /// failure — every way this can not work is on the result.
    /// </summary>
    /// <remarks>
    /// One-shot. The record is spent by the attempt whether or not the delete
    /// is allowed, so a second "scratch that" has nothing to act on rather than
    /// walking backwards into text from an earlier insertion.
    /// </remarks>
    Task<FocusedUndoResult> UndoLastInsertionAsync(CancellationToken ct = default);
}

/// <summary>
/// dictation M1 — the paste, the clipboard it borrows, and the check that stops
/// it typing into nowhere.
///
/// <para>
/// Composed from three seams and nothing else:
/// <see cref="IFocusedApplicationResolver"/> to find out whether there is a
/// window at all, and workflows' existing <see cref="IClipboard"/> and
/// <see cref="IPasteKeystroke"/> to do the typing. Reusing those two rather
/// than declaring dictation-local copies is the point — there is one place in
/// this product where a synthetic keystroke is produced, and it stays one.
/// </para>
///
/// <para>
/// <b>The borrowed clipboard.</b> A synthesised Ctrl+V pastes whatever the
/// clipboard holds, so inserting text means putting it there first, which means
/// standing on top of something the user chose to copy. This reads that first
/// and puts it back in a <c>finally</c>, so the restore runs on the paths where
/// the paste returns false and on the path where it throws — the two ways a
/// naive implementation quietly keeps the user's clipboard. A clipboard that
/// offers <see cref="IClipboardSnapshot"/> is read and restored whole, every
/// format, and only if nothing else wrote it while the paste settled; one that
/// offers only <see cref="IClipboardTextSnapshot"/> gets its text back.
/// </para>
///
/// <para>
/// <b>What Success promises.</b> Only that the paste keystroke was delivered
/// to the window that had keyboard focus when this insert was authorised, and
/// that the clipboard was put back. It does not promise the text landed in a
/// text box: the focused window may take the keystroke as a shortcut, or eat
/// it entirely.
/// </para>
///
/// <para>
/// <b>What it can now rule out, and what it still cannot.</b> Until
/// 2026-08-20 this class said "nothing this process can observe" and stopped
/// there, and Windows 11 Notepad then ate a 358-character transcript under a
/// log line reading <c>Inserted 358 characters into Notepad</c>. One thing IS
/// observable and is now checked: which window inside the foreground process
/// holds keyboard focus — see <see cref="IKeyboardFocusProbe"/>. A move
/// between resolving the target and firing the keystroke is refused outright
/// (<see cref="FocusedInsertionOutcome.FocusMoved"/>), and the focus target
/// is named in every line this class logs, so "the paste went to Notepad's
/// menu-bar island rather than its document" is one grep instead of a day of
/// probes.
/// </para>
///
/// <para>
/// <b>What was tried and rejected, with measurements</b>, so the next person
/// does not spend the afternoon again. Reading the target's text back to
/// confirm the paste landed was measured on the device against the three
/// applications this must not regress: the focused window is a
/// <c>ControlType.Pane</c> with <c>IsTextPatternAvailable=False</c> on
/// Notepad, Windows Terminal and a WPF app alike, so there is no text to read at
/// the focused element, and a UIA descendant search finds an arbitrary
/// text-bearing element rather than the document.
/// </para>
///
/// <para>
/// Menu-mode arming is not visible either. <c>hwndMenuOwner</c> measured null
/// both in the working state and in the state where the paste was eaten — the
/// WinUI menu bar is not a Win32 menu, so the field that would name its owner
/// never fills in.
/// </para>
///
/// <para>
/// <b>And the caret is a trap, which is the one worth reading before
/// "improving" this.</b> <c>GUITHREADINFO</c> does tell Notepad's two states
/// apart, measured on the device 2026-08-20:
/// <code>
/// notepad, text surface focused   flags=0x1  hwndCaret=0x505B8  rcCaret=944,32,945,66
/// notepad, menu bar armed         flags=0x0  hwndCaret=0x0      rcCaret=0,0,0,0
/// </code>
/// So "no caret on the focus target" looks exactly like a test for "this
/// paste is about to be eaten". It is not, and wiring it in would break the
/// application that never had the bug — measured in the same run:
/// <code>
/// windows terminal, working fine  flags=0x0  hwndCaret=0x0      rcCaret=0,0,0,0
/// </code>
/// Windows Terminal has no caret and takes a paste perfectly. A caret check
/// would refuse it every time. The caret separates Notepad-armed from
/// Notepad-working, not "will take text" from "will eat it", and the only
/// reason it appears to be the latter is that Notepad is the app the defect
/// was found in.
/// </para>
///
/// <para>
/// Ground truth for "the text landed" therefore lives in the device harness
/// (<c>native/tools/insertion_gate.ps1</c>), which drives a real hold with its
/// autorepeat stream and reads the document back through UIA, and NOT in this
/// class, where every available signal was measured to be absent, wrong, or —
/// in the caret's case — right for one application and wrong for the next.
/// </para>
///
/// <para>
/// <b>And it can now take it back — narrowly.</b>
/// <see cref="UndoLastInsertionAsync"/> is "scratch that": the insert writes
/// what it typed, where and when into <see cref="LastInsertionRegistry"/> on
/// the ONE branch where the paste keystroke was reported delivered, and the
/// undo spends that record, asks <see cref="DictationUndoPolicy"/>, and
/// presses backspace only if every gate agrees. This does not weaken the
/// rule that dictation never retracts text on its own — that rule is about a
/// recogniser changing its mind mid-utterance,
/// where there is no user intent to appeal to and the caret can have moved for
/// a dozen invisible reasons. Here the user has said the words. The delete is
/// bounded to one insertion, to one window, to two minutes, and to
/// <see cref="DictationDeleteCommand.MaxBackspaces"/> keystrokes, and the
/// streaming path still has no verb that can reach it.
/// </para>
///
/// <para>
/// <b>Retries.</b> A caller must not automatically re-run on
/// <see cref="FocusedInsertionOutcome.InsertionRefused"/>. When the paste seam
/// throws, this reports refused because it cannot claim a delivery it never saw
/// confirmed — but a throw after the keystroke went would make a retry a second
/// paste into the user's sentence. Refused means "no evidence it was
/// delivered", not "proof it was not".
/// </para>
/// </summary>
public sealed class FocusedTextInsertion : IFocusedTextInserter, IFocusedTextUndo
{
    private readonly IFocusedApplicationResolver _resolver;
    private readonly IClipboard _clipboard;
    private readonly IPasteKeystroke _paste;
    private readonly ILogger<FocusedTextInsertion> _logger;
    private readonly IBackspaceKeystroke? _backspace;
    private readonly ISecureFieldProbe? _secureField;
    private readonly Func<DateTimeOffset> _utcNow;

    /// <param name="backspace">
    /// The delete half of "scratch that". <b>Optional, and its absence
    /// disables undo rather than degrading it</b> —
    /// <see cref="UndoLastInsertionAsync"/> refuses when nothing was wired,
    /// because there is no partial way to remove half a sentence. Optional at
    /// all so that the three seams above stay the ones this class is composed
    /// from and the existing composition roots keep compiling unchanged.
    /// </param>
    /// <param name="secureField">
    /// The password-field probe, the same seam
    /// <see cref="DictationOutcome"/>'s secure-field refusal already uses.
    /// <b>Optional, and its absence also disables undo</b> — see
    /// <see cref="UndoLastInsertionAsync"/>, which cannot press a key it
    /// cannot first prove is not landing in a password box.
    /// </param>
    /// <param name="utcNow">
    /// The clock, passed in so the two-minute expiry in
    /// <see cref="DictationUndoPolicy"/> can be tested without waiting two
    /// minutes. Nothing else here reads a clock.
    /// </param>
    public FocusedTextInsertion(
        IFocusedApplicationResolver resolver,
        IClipboard clipboard,
        IPasteKeystroke paste,
        ILogger<FocusedTextInsertion> logger,
        TimeSpan? pasteSettleDelay = null,
        IBackspaceKeystroke? backspace = null,
        ISecureFieldProbe? secureField = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _paste = paste ?? throw new ArgumentNullException(nameof(paste));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Tests pass TimeSpan.Zero so the suite does not sleep half a second
        // per insertion; the shipped composition passes nothing and gets the
        // macOS-parity default.
        _pasteSettleDelay = pasteSettleDelay ?? DefaultPasteSettleDelay;
        _backspace = backspace;
        _secureField = secureField;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// How long the borrowed clipboard keeps the transcript after the paste
    /// keystroke goes, before the user's own content is put back.
    /// </summary>
    /// <remarks>
    /// This wait IS the correctness of the whole clipboard-borrowing scheme.
    /// <c>SendInput</c> queues Ctrl+V and returns; the target application
    /// processes the keystroke and reads the clipboard on ITS message loop,
    /// milliseconds-to-hundreds-of-milliseconds later. Restoring immediately —
    /// which this class did from its first commit — loses that race on real
    /// targets (Windows Terminal reliably), and the target then pastes the
    /// RESTORED clipboard: the user speaks a sentence and their old clipboard
    /// content appears instead, with every log line reporting success. 500 ms
    /// matches the macOS inserter's fixed restore delay, whose e2e harness
    /// measures the same paste path. Overridable for tests only.
    /// </remarks>
    internal static readonly TimeSpan DefaultPasteSettleDelay =
        TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _pasteSettleDelay;

    public async Task<FocusedInsertionResult> InsertAsync(
        string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // Nothing to insert, so nothing to disturb. Refusing before the
            // resolver runs keeps an empty transcript from stomping the
            // clipboard on its way to doing nothing.
            return FocusedInsertionResult.Refused(
                FocusedApplicationResult.NoFocusedWindow,
                "there was no text to insert");
        }

        // The last cancellation point. Past the TrySet below the operation
        // must run to the restore — see the interface remarks.
        ct.ThrowIfCancellationRequested();

        var target = _resolver.Resolve();

        if (!target.HasFocusedWindow)
        {
            _logger.LogDebug("Nothing has focus; not inserting {Length} characters.", text.Length);
            return FocusedInsertionResult.NoFocusedWindow(target);
        }

        if (!target.IsResolved)
        {
            // There is a window and it would take the keystroke, but this
            // process could not find out whose it is. Typing blind into an
            // unidentified window is how a dictated sentence ends up in a
            // password box or a terminal, and the reversible half of that
            // choice is to not type. If a real target turns out to be
            // unidentifiable in practice — an elevated editor is the likely
            // one — this is the line to revisit, with the process name from
            // the log in hand.
            _logger.LogWarning(
                "Foreground window {Title} could not be identified; not inserting.",
                target.WindowTitle);
            return FocusedInsertionResult.Refused(
                target,
                "the foreground window's process could not be identified");
        }

        // Which window inside that process would take the keystroke. Read here
        // so the comparison below spans the whole gap between aiming and
        // firing, and so the log lines have something to name even when
        // nothing moves.
        var focusAtResolve = ReadFocusTarget();

        // Read before writing. Everything after this point may have to put it
        // back.
        var stash = ReadClipboard();

        if (!_clipboard.TrySet(text))
        {
            // No keystroke: pasting whatever is on the clipboard now into the
            // user's document is worse than inserting nothing. But a failed
            // write has not always left the clipboard alone — the writer
            // empties it before the text goes on — so the snapshot goes back
            // when that is what happened; see UndoRefusedWrite.
            _logger.LogWarning(
                "Clipboard would not take {Length} characters; not typing into {Process}.",
                text.Length,
                target.ProcessName);

            const string refusal = "the clipboard would not take the text";
            var undo = UndoRefusedWrite(stash);
            if (undo.Failure is { } undoFailure)
            {
                _logger.LogWarning("The clipboard was not restored after the refused write: {Reason}.", undoFailure);
                return FocusedInsertionResult.Refused(
                    target, $"{refusal}; the clipboard was also not restored ({undoFailure})");
            }

            return FocusedInsertionResult.Refused(target, refusal);
        }

        // The clipboard's change count with the transcript on it, read the
        // moment the write returns. The restore compares against this after
        // the settle: any other number means somebody wrote the clipboard in
        // between, and their content is newer than the snapshot. Null when the
        // clipboard cannot count, which leaves the restore unconditional, as
        // it always was. (A third-party write landing in the microseconds
        // between the write and this read would be counted as ours; there is
        // no way to read the count inside IClipboard.TrySet's own session.)
        var writtenAt = ReadSequenceNumber();

        var delivered = false;
        var focusMoved = false;
        string? deliveryFailure = null;

        // Declared out here because the finally below assigns it and the
        // outcome is decided after the try: a null Failure means the clipboard
        // is the user's again, anything else is why it is not.
        var restore = ClipboardRestore.Done;

        // Read as late as it can be — the last instant before the keystroke
        // goes — and kept for the log lines, which is half of why it is read
        // at all.
        var focusAtSend = KeyboardFocusTarget.Unknown;

        try
        {
            focusAtSend = ReadFocusTarget();

            if (focusAtResolve.DiffersFrom(focusAtSend))
            {
                // Not typed. SendInput injects at the keyboard, so a keystroke
                // sent now goes to whatever took focus, not to the window this
                // insert was authorised against — and a dictated sentence
                // arriving in a window the user just moved to is the failure
                // that reads as "Dictation typed into my password box".
                //
                // Falls through to the finally: the clipboard is already
                // holding the transcript and has to go back either way.
                focusMoved = true;
                deliveryFailure =
                    $"keyboard focus moved from {focusAtResolve} to {focusAtSend} "
                    + "between resolving the target and typing";
            }
            else
            {
                delivered = _paste.TrySend();
                if (!delivered)
                {
                    deliveryFailure = "the paste keystroke could not be sent";
                }
            }
        }
        catch (Exception ex)
        {
            // Not rethrown. Every ordinary failure of this class is an
            // outcome, and a caller that has to catch to find out whether its
            // text was typed will eventually not catch. The message is carried
            // instead.
            deliveryFailure = ex.Message;
        }
        finally
        {
            // Only after a delivered keystroke, and NOT cancellable: the wait
            // exists so the target's message loop reads the transcript before
            // the clipboard under it changes (DefaultPasteSettleDelay's
            // remarks carry the failure this prevents). When nothing was sent
            // there is no reader to wait for, and the user's clipboard goes
            // back immediately.
            if (delivered && _pasteSettleDelay > TimeSpan.Zero)
            {
                await Task.Delay(_pasteSettleDelay, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // The whole reason this is a finally: the clipboard is the user's,
            // it has been overwritten by the TrySet above, and it has to go
            // back whether the paste succeeded, returned false, or threw.
            restore = RestoreClipboard(stash, writtenAt);
        }

        if (!delivered)
        {
            var reason = restore.Failure is null
                ? deliveryFailure!
                : $"{deliveryFailure}; the clipboard was also not restored ({restore.Failure})";

            _logger.LogWarning(
                "Did not type into {Process}: {Reason}.",
                target.ProcessName,
                reason);

            return focusMoved
                ? FocusedInsertionResult.FocusMoved(target, reason)
                : FocusedInsertionResult.Refused(target, reason);
        }

        // ── What "scratch that" will act on ──────────────────────────────
        //
        // THE ONE BRANCH. Past the `if (!delivered)` return above, `delivered`
        // is the value _paste.TrySend() gave back: the paste keystroke was
        // accepted for the window this insert was authorised against, and
        // focus had not moved since it was authorised. That is the only place
        // in this method where the target application really took the text,
        // and it is therefore the only place a record may be written.
        //
        // Every other exit is a refusal that must leave the registry alone —
        // an empty transcript, a foreground window whose process could not be
        // identified, a clipboard that would not take the text, a focus move
        // between aiming and firing, a paste that returned false, a paste that
        // threw. A record written on any of them arms a run of backspaces over
        // text Dictation never typed, in somebody else's document, with no undo
        // stack to appeal to. That is the single failure this feature must
        // never produce, and the gate against it is this line's position.
        //
        // Deliberately ABOVE the ClipboardRestoreFailed branch rather than
        // beside the Success one. Both of those outcomes have
        // TextWasDelivered true — the user's document changed on both — and a
        // user who has just lost their clipboard is the last person who should
        // also be told there is nothing to undo. The enum's name is not the
        // question here; `delivered` is.
        //
        // The focus target is the one read at SEND time, not at resolve time:
        // it is the window the keystroke actually went to, which is the window
        // DictationUndoPolicy has to see again before it will press backspace.
        // An unread target records handle 0 and a null class, which that
        // policy refuses — a host with no focus probe wired can insert and
        // cannot undo, which is the safe direction.
        LastInsertionRegistry.Record(new DictationInsertionRecord(
            text,
            (nint)focusAtSend.Handle,
            focusAtSend.WasRead ? focusAtSend.ClassName : null,
            _utcNow()));

        if (restore.Failure is { } restoreFailure)
        {
            _logger.LogWarning(
                "Typed into {Process} ({Focus}), but the clipboard was not restored: {Reason}.",
                target.ProcessName,
                focusAtSend,
                restoreFailure);
            return FocusedInsertionResult.RestoreFailed(target, restoreFailure);
        }

        // NOT "Inserted N characters". That sentence was printed over an empty
        // Notepad document for a whole afternoon and cost an investigation its
        // first hour: what this class knows is that a Ctrl+V was delivered to
        // the keyboard, not that a document changed. The verb says what
        // happened, and the focus target names the window the keystroke went
        // to — which is the field that separates "Notepad's text surface" from
        // "Notepad's menu bar" and is the whole difference between a paste that
        // lands and one that vanishes.
        _logger.LogDebug(
            "Typed {Length} characters into {Process} ({Title}), keyboard focus {Focus}.",
            text.Length,
            target.ProcessName,
            target.WindowTitle,
            focusAtSend);
        return FocusedInsertionResult.Succeeded(target, restore.Note);
    }

    /// <summary>
    /// "Scratch that": gather the facts, ask
    /// <see cref="DictationUndoPolicy"/>, and press backspace only if it says
    /// so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method decides nothing.</b> It reads the window, reads the
    /// password probe, reads the clock, and hands all of it to a pure function
    /// that lives one file over. The reason is the one that file states: the
    /// branches that matter most here — a two-minute wait, a second window, a
    /// password field — are the ones hardest to stage in front of a real
    /// desktop, so the gate is a function of its arguments and this is only the
    /// thing that gathers them. When the decision refuses, its refusal is
    /// returned UNCHANGED and no keystroke is synthesised. There is no branch
    /// below that sends a key on a refused decision, and adding one is what a
    /// reviewer should be looking for.
    /// </para>
    /// <para>
    /// <b>The record is taken first, before anything is decided.</b> "The user
    /// said scratch that" spends it whether or not the delete goes ahead. A
    /// refusal that put it back would let the same insertion be re-offered
    /// later against a window the user has since returned to, which is exactly
    /// the stale delete <see cref="DictationUndoRefusal.Expired"/> exists to
    /// stop, arriving through the back door.
    /// </para>
    /// </remarks>
    public async Task<FocusedUndoResult> UndoLastInsertionAsync(CancellationToken ct = default)
    {
        // Before the record is spent. Past this line the attempt has happened
        // as far as the registry is concerned.
        ct.ThrowIfCancellationRequested();

        var record = LastInsertionRegistry.Take();

        // Resolved for the log line, not for the decision — the policy compares
        // WINDOWS, and the window is what the focus probe answers. The process
        // name is here because "Dictation would not undo that" with no subject is
        // the shape of log line this whole file was rewritten to stop printing.
        var target = _resolver.Resolve();
        var focus = ReadFocusTarget();

        var decision = DictationUndoPolicy.Decide(
            record,
            (nint)focus.Handle,
            // Null rather than the empty string when the probe could not be
            // asked at all: the policy reads both as "we could not tell" and
            // refuses, but only one of them is honest about why.
            focus.WasRead ? focus.ClassName : null,
            SecureFieldBlocksDelete(),
            _utcNow());

        if (!decision.Allowed)
        {
            _logger.LogDebug(
                "Not undoing the last insertion in {Process} ({Focus}): {Refusal}.",
                target.ProcessName,
                focus,
                decision.Refusal);
            return FocusedUndoResult.Refused(decision.Refusal);
        }

        if (_backspace is null)
        {
            // The policy said yes and this build has nothing to press with.
            // Reported rather than silently returning a clean refusal, because
            // "undo never works on this machine" is otherwise indistinguishable
            // from "there was nothing to undo" — and an implementation that
            // cannot delete can never report a delete, which is the pressure
            // that keeps the seam wired. Same argument as the readable
            // clipboard's, one method up.
            _logger.LogWarning(
                "No backspace seam is wired, so the last insertion in {Process} "
                + "cannot be undone.",
                target.ProcessName);
            return FocusedUndoResult.CouldNotSend("no backspace keystroke seam is wired");
        }

        int sent;

        try
        {
            sent = await _backspace.SendAsync(decision.BackspaceCount, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not rethrown, for InsertAsync's reason: every ordinary failure of
            // this class is a result, and a caller that has to catch to find
            // out whether the user's document changed will eventually not
            // catch. Note what this cannot say — a throw after some keystrokes
            // went is indistinguishable from a throw before any did, so the
            // count is not claimed.
            _logger.LogWarning(
                ex, "The backspaces for the last insertion in {Process} threw.",
                target.ProcessName);
            return FocusedUndoResult.CouldNotSend(ex.Message);
        }

        // Nothing to clear: the record was taken at the top of this method, so
        // the registry has been empty since before the first keystroke — which
        // also means a second "scratch that" arriving mid-run finds nothing
        // rather than starting a second delete over the same text.

        if (sent <= 0)
        {
            _logger.LogWarning(
                "The last insertion in {Process} was not undone: no backspace was delivered.",
                target.ProcessName);
            return FocusedUndoResult.CouldNotSend("no backspace could be delivered");
        }

        if (sent < decision.BackspaceCount)
        {
            // Its own answer rather than a success with a smaller number. Part
            // of the sentence is still on screen and the record is gone, so
            // there is no second attempt — the caller has to be able to say so.
            _logger.LogWarning(
                "Undid only {Sent} of {Decided} characters in {Process} ({Focus}).",
                sent,
                decision.BackspaceCount,
                target.ProcessName,
                focus);
            return FocusedUndoResult.PartlyUndid(sent, decision.BackspaceCount);
        }

        // "Sent", not "deleted", for the reason the insert's line says "typed"
        // rather than "inserted": what this class knows is that key events were
        // delivered to the keyboard, not that a document lost characters.
        _logger.LogDebug(
            "Sent {Count} backspaces to {Process} ({Title}), keyboard focus {Focus}.",
            sent,
            target.ProcessName,
            target.WindowTitle,
            focus);
        return FocusedUndoResult.Undid(sent);
    }

    /// <summary>
    /// Whether the caret may be in a password field, for the purpose of
    /// deciding whether to press backspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Unknown answers YES here, and that is the opposite of the insert
    /// path.</b> <see cref="SecureFieldState.Unknown"/> is documented as
    /// "treated as NotSecure by every caller" because refusing to TYPE on an
    /// unanswered UIA query would switch dictation off for people nowhere near
    /// a password box — "dictation is broken" is the reading that guard exists
    /// to prevent. Deleting is not the same trade.
    /// <see cref="DictationUndoPolicy.Decide"/> spells it out: refusing to
    /// delete on an unanswered probe costs a user four words they retype,
    /// while pressing backspace into a password box is a write into a field
    /// nobody can see, whose contents nobody can restore, and which the user
    /// will next find out about at a failed sign-in.
    /// </para>
    /// <para>
    /// A build with no probe wired reads the same way, so undo is off there
    /// rather than unguarded. That is deliberate pressure to wire it: a
    /// composition root that forgets the probe loses "scratch that", which
    /// somebody notices, instead of gaining a silent delete into password
    /// boxes, which nobody does.
    /// </para>
    /// <para>
    /// Catches, although <see cref="ISecureFieldProbe.Probe"/> is documented
    /// never to throw — and a throw is read as Unknown, which is a refusal.
    /// The guard failing loudly must not be a way past it.
    /// </para>
    /// </remarks>
    private bool SecureFieldBlocksDelete()
    {
        if (_secureField is null)
        {
            return true;
        }

        try
        {
            return _secureField.Probe() != SecureFieldState.NotSecure;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read whether the caret is in a password field.");
            return true;
        }
    }

    /// <summary>
    /// Which window would take a keystroke right now, if the injected
    /// resolver is one that can be asked at all.
    /// </summary>
    /// <remarks>
    /// Never throws and never fails the insert: an unread focus target is
    /// <see cref="KeyboardFocusTarget.Unknown"/>, which
    /// <see cref="KeyboardFocusTarget.DiffersFrom"/> refuses to treat as
    /// evidence of anything. A probe that started throwing must not become
    /// the reason a user's sentence is dropped.
    /// </remarks>
    private KeyboardFocusTarget ReadFocusTarget()
    {
        if (_resolver is not IKeyboardFocusProbe probe)
        {
            return KeyboardFocusTarget.Unknown;
        }

        try
        {
            return probe.Read();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read which window has keyboard focus.");
            return KeyboardFocusTarget.Unknown;
        }
    }

    /// <summary>
    /// Takes what the clipboard holds, if the injected
    /// <see cref="IClipboard"/> is one that can be read at all — every format
    /// when it offers <see cref="IClipboardSnapshot"/>, its text when it offers
    /// only <see cref="IClipboardTextSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// A whole-clipboard reader that cannot read does NOT fall back to the
    /// text read. The two look at the same clipboard, and what stops the full
    /// read is almost always another process holding it open, which stops the
    /// text read too; a fallback that sometimes worked would quietly put back
    /// the text and drop the picture beside it, which is the bug this seam
    /// exists to end.
    /// </remarks>
    private ClipboardStash ReadClipboard()
    {
        if (_clipboard is IClipboardSnapshot whole)
        {
            try
            {
                return whole.Capture() is { } contents
                    ? ClipboardStash.Of(contents)
                    : ClipboardStash.Unreadable;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the clipboard before inserting.");
                return ClipboardStash.Unreadable;
            }
        }

        if (_clipboard is not IClipboardTextSnapshot readable)
        {
            return ClipboardStash.Unreadable;
        }

        try
        {
            return readable.TryRead(out var previous)
                ? ClipboardStash.Of(previous)
                : ClipboardStash.Unreadable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the clipboard before inserting.");
            return ClipboardStash.Unreadable;
        }
    }

    /// <summary>
    /// The clipboard's change count, or null when the injected clipboard
    /// cannot count or the read threw. Never fails the insert.
    /// </summary>
    private uint? ReadSequenceNumber()
    {
        if (_clipboard is not IClipboardSnapshot whole)
        {
            return null;
        }

        try
        {
            return whole.SequenceNumber;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the clipboard's change count.");
            return null;
        }
    }

    /// <summary>
    /// Puts the stash back, and says whether the clipboard is the user's
    /// again or why it is not.
    /// </summary>
    private ClipboardRestore RestoreClipboard(ClipboardStash stash, uint? writtenAt)
    {
        if (!stash.WasRead)
        {
            // The text was never captured, so there is nothing to write back
            // and the dictated text is now sitting where the user's clipboard
            // used to be. That is a restore failure and is reported as one —
            // an implementation whose clipboard cannot be read can never
            // return Success, which is exactly the pressure that keeps the
            // read seam wired.
            return ClipboardRestore.Failed(
                "the clipboard could not be read before the insert, so the dictated text was left on it");
        }

        if (stash.Contents is { } contents)
        {
            var whole = RestoreEveryFormat(
                contents, writtenAt is { } ours ? new ClipboardRestoreCondition(ours) : null);
            if (whole.Note is not null)
            {
                _logger.LogDebug(
                    "The clipboard changed while the paste settled; leaving the newer contents in place.");
            }

            return whole;
        }

        if (string.IsNullOrEmpty(stash.Text))
        {
            // The clipboard held no text to begin with — it was empty, or it
            // held an image or a file list this seam cannot see. There is no
            // way to write "nothing" through IClipboard.TrySet, which takes a
            // string: the empty string would put an empty TEXT format there,
            // which is not the empty clipboard the user had.
            // Treated as nothing to restore rather than as a failure: raising
            // ClipboardRestoreFailed for every dictation done with an empty
            // clipboard would bury the case where real copied text was lost.
            // Only a text-only clipboard reaches this; one that offers
            // IClipboardSnapshot restores emptiness as emptiness.
            _logger.LogDebug("Clipboard held no text before the insert; leaving the inserted text on it.");
            return ClipboardRestore.Done;
        }

        try
        {
            return _clipboard.TrySet(stash.Text)
                ? ClipboardRestore.Done
                : ClipboardRestore.Failed("the clipboard would not take the previous contents back");
        }
        catch (Exception ex)
        {
            return ClipboardRestore.Failed(ex.Message);
        }
    }

    /// <summary>
    /// The whole-clipboard restore: every format the snapshot kept goes back,
    /// unless the clipboard changed hands while the paste was settling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count is compared at the end of the settle, by the restore
    /// itself, with the clipboard open.</b> The window it guards is the half
    /// second the transcript sat on the clipboard; a check made before the
    /// wait would guard nothing. And a check made here, before the restore
    /// opens the clipboard, would leave a gap between the comparison and the
    /// emptying in which another copy could still land and be overwritten —
    /// so the expected count goes into the restore, which compares it where
    /// no other process can write. A count that cannot be read leaves the
    /// restore unconditional, as it was before the count existed — an unknown
    /// is not evidence that anybody else wrote.
    /// </para>
    /// <para>
    /// <b>Restored first, judged second.</b> A snapshot that lost a format
    /// still writes back everything it kept — the text and the formatting are
    /// worth having even when the oversized picture beside them is gone — and
    /// THEN reports the loss, so the user is told before their next paste.
    /// </para>
    /// </remarks>
    private ClipboardRestore RestoreEveryFormat(ClipboardContents contents, ClipboardRestoreCondition? condition)
    {
        var whole = (IClipboardSnapshot)_clipboard;

        SnapshotRestoreResult result;
        try
        {
            result = whole.Restore(contents, condition);
        }
        catch (Exception ex)
        {
            return ClipboardRestore.Failed(ex.Message);
        }

        if (result == SnapshotRestoreResult.Changed)
        {
            return ClipboardRestore.LeftAlone(
                "the clipboard changed after the dictated text was written, so the newer contents were left in place");
        }

        if (result != SnapshotRestoreResult.Restored)
        {
            return ClipboardRestore.Failed("the clipboard would not take the previous contents back");
        }

        if (contents.Lost.Count > 0)
        {
            // Format names only — never the bytes, which are the user's.
            return ClipboardRestore.Failed(
                $"part of the clipboard could not be kept ({string.Join(", ", contents.Lost)})");
        }

        return ClipboardRestore.Done;
    }

    /// <summary>
    /// Puts the snapshot back after a transcript write that failed, if the
    /// failure emptied the clipboard on its way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer empties the clipboard before it puts the text on, and a
    /// text refused after that — rare, and the writer cannot take the
    /// emptying back — left nothing where the user's screenshot or file list
    /// had been, under an outcome that said nothing had changed. The restore
    /// decides whether that happened, with the clipboard open: the count read
    /// right after the failed write must still stand, and the clipboard must
    /// hold nothing. A write that failed before emptying anything — another
    /// process holding the clipboard, the usual reason — finds the user's
    /// content still there, so nothing is written. Neither is anything when
    /// another process wrote in that same moment: its content is there and it
    /// is newer. The one case this cannot tell from its own emptying is
    /// another process emptying the clipboard, writing nothing, in that same
    /// moment — a password manager clearing a secret — and then the snapshot
    /// taken just before goes back; the gap between the snapshot and the
    /// write already has that exposure on every insert.
    /// </para>
    /// <para>
    /// Only a whole snapshot with a count can be checked like this. A
    /// clipboard that reads only text cannot say whether the failed write
    /// touched it, and its text written back over a copy somebody else may
    /// just have made would be worse than leaving it, so it is left.
    /// </para>
    /// </remarks>
    private ClipboardRestore UndoRefusedWrite(ClipboardStash stash)
    {
        if (stash.Contents is not { } contents || ReadSequenceNumber() is not { } afterWrite)
        {
            return ClipboardRestore.Done;
        }

        var undo = RestoreEveryFormat(contents, new ClipboardRestoreCondition(afterWrite, HoldsNothing: true));

        // Left alone means the failed write left nothing of this insert's to
        // undo, which is not news for the result.
        return undo.Note is null ? undo : ClipboardRestore.Done;
    }

    /// <summary>
    /// What the clipboard held, and whether that is even known —
    /// distinguishing "the user had nothing copied" from "this could not find
    /// out", which are the same null and very different obligations.
    /// <see cref="Contents"/> is set when the whole clipboard was read and
    /// <see cref="Text"/> when only its text could be.
    /// </summary>
    private readonly record struct ClipboardStash(bool WasRead, string? Text, ClipboardContents? Contents)
    {
        internal static ClipboardStash Unreadable { get; } = new(false, null, null);

        internal static ClipboardStash Of(string? text) => new(true, text, null);

        internal static ClipboardStash Of(ClipboardContents contents) => new(true, null, contents);
    }

    /// <summary>
    /// How the restore went. A null <see cref="Failure"/> means the clipboard
    /// is the user's again; <see cref="Note"/> carries why it was deliberately
    /// left alone, for the result's reason.
    /// </summary>
    private readonly record struct ClipboardRestore(string? Failure, string? Note)
    {
        internal static ClipboardRestore Done { get; } = new(null, null);

        internal static ClipboardRestore Failed(string reason) => new(reason, null);

        internal static ClipboardRestore LeftAlone(string note) => new(null, note);
    }
}
