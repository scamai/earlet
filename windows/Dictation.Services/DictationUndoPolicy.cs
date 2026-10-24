namespace Dictation.Services;

/// <summary>
/// What Dictation last typed, and where it typed it.
/// </summary>
/// <remarks>
/// <para>
/// macOS's <c>LastInsertion</c>, with the identity widened. There it is a
/// bundle id — one string naming an application. Here it is a window: the
/// <c>HWND</c> that had focus and the class name Win32 reports for it.
/// <see cref="DictationUndoPolicy.Decide"/> says why that matters.
/// </para>
/// <para>
/// <paramref name="WindowClass"/> is nullable because
/// <c>GetClassName</c> can fail — a window that died between the insertion
/// and the question, or a process this one may not query. Null is “we could
/// not tell”, and the policy treats that as a refusal rather than a pass.
/// </para>
/// </remarks>
/// <param name="Text">
/// Exactly what was inserted, as inserted. The undo counts keystrokes off
/// this string, so a caller that trims or reformats it before recording is
/// deciding how much of the user's document gets deleted.
/// </param>
/// <param name="WindowHandle">
/// The focused <c>HWND</c> at insertion time. <c>0</c> means unknown, which is
/// a refusal.
/// </param>
/// <param name="WindowClass">
/// The window's class name, or <c>null</c> when it could not be read.
/// </param>
/// <param name="AtUtc">
/// When the insertion happened. UTC in the name because a record stamped in
/// local time gets an hour added or removed by a DST change, which lands
/// straight in <see cref="DictationUndoRefusal.Expired"/>'s arithmetic.
/// </param>
public sealed record DictationInsertionRecord(
    string Text,
    nint WindowHandle,
    string? WindowClass,
    DateTimeOffset AtUtc);

/// <summary>
/// Why an undo was refused.
/// </summary>
/// <remarks>
/// One value per reason rather than a bool, because “Dictation would not delete
/// that” is a sentence the HUD has to finish. “You have moved to another
/// window” and “that was two minutes ago” are different facts, and a user who
/// is told the wrong one goes looking in the wrong place.
/// </remarks>
public enum DictationUndoRefusal
{
    /// <summary>
    /// Nothing is refused: the undo may proceed.
    /// <b>Zero deliberately</b> — but note that a defaulted
    /// <see cref="DictationUndoDecision"/> is <c>None</c> with a count of
    /// <c>0</c>, which sends no keystrokes. The permissive value is safe here
    /// only because the count travels with it.
    /// </summary>
    None = 0,

    /// <summary>
    /// Nothing has been inserted this run, or the last insertion was already
    /// undone.
    /// </summary>
    NothingRecorded,

    /// <summary>
    /// The cursor is in a password field. Never press a key here.
    /// </summary>
    SecureField,

    /// <summary>
    /// The insertion is older than <see cref="DictationUndoPolicy.MaxAge"/>,
    /// or is dated in the future.
    /// </summary>
    Expired,

    /// <summary>
    /// Focus has moved to another window — or to a window neither side could
    /// identify.
    /// </summary>
    DifferentWindow,

    /// <summary>
    /// The record is empty, or longer than
    /// <see cref="DictationDeleteCommand.MaxBackspaces"/>.
    /// </summary>
    TooLong,
}

/// <summary>
/// The answer: whether to press backspace, and how many times.
/// </summary>
/// <remarks>
/// The count is part of the decision rather than something the caller
/// recomputes, so there is exactly one place in the product that decides how
/// many keystrokes leave the app. A caller that has a
/// <see cref="DictationUndoDecision"/> needs no further judgement — it checks
/// <see cref="Allowed"/> and sends <see cref="BackspaceCount"/>.
/// </remarks>
/// <param name="Refusal">Why not, or <see cref="DictationUndoRefusal.None"/>.</param>
/// <param name="BackspaceCount">
/// Keystrokes to send. <b>Always <c>0</c> on a refusal</b>, so a caller that
/// forgets to check <see cref="Allowed"/> sends nothing rather than deleting
/// into a password field.
/// </param>
public readonly record struct DictationUndoDecision(
    DictationUndoRefusal Refusal,
    int BackspaceCount)
{
    /// <summary>Whether Dictation may press backspace at all.</summary>
    public bool Allowed => Refusal == DictationUndoRefusal.None;
}

/// <summary>
/// Everything that decides whether Dictation is allowed to press backspace.
/// </summary>
/// <remarks>
/// <para>
/// macOS's <c>TextInserter.decideUndo</c>, ported, and kept pure for the same
/// reason it is pure there: the branches that matter most are the ones hardest
/// to stage live — a two-minute wait, a second window, a password field — so
/// the gate is a function of its arguments and nothing else. No clock read, no
/// Win32 call, no I/O. Lane C's Win32 side gathers the facts; this decides.
/// </para>
/// </remarks>
public static class DictationUndoPolicy
{
    /// <summary>
    /// How stale an insertion may be and still be undoable.
    /// </summary>
    /// <remarks>
    /// Two minutes, as macOS. Long enough to cover “read what it typed, decide
    /// it is wrong, say scratch that” including a phone-call-length pause, and
    /// short enough that a record left over from before lunch cannot eat a
    /// paragraph the user has since written by hand into the same field.
    /// </remarks>
    public static TimeSpan MaxAge { get; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The whole gate. Returns the FIRST refusal that applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order is load-bearing, not stylistic.</b> Each step below says
    /// what it is doing there rather than one step earlier or later.
    /// </para>
    /// </remarks>
    /// <param name="record">The last insertion, or <c>null</c> if there is none.</param>
    /// <param name="currentWindow">
    /// The <c>HWND</c> focused right now. <c>0</c> means it could not be read.
    /// </param>
    /// <param name="currentWindowClass">
    /// Its class name, or <c>null</c> when <c>GetClassName</c> gave nothing.
    /// </param>
    /// <param name="secureFieldFocused">
    /// Whether the focused element is a password field. The caller maps
    /// <see cref="SecureFieldState"/> onto this bool, and note that the
    /// direction of the <see cref="SecureFieldState.Unknown"/> mapping is a
    /// different question here than it is on the insert path: refusing to TYPE
    /// on an unanswered probe would break dictation for people nowhere near a
    /// password box, whereas refusing to DELETE on one costs a user four words
    /// they retype.
    /// </param>
    /// <param name="nowUtc">The current time, passed in rather than read.</param>
    public static DictationUndoDecision Decide(
        DictationInsertionRecord? record,
        nint currentWindow,
        string? currentWindowClass,
        bool secureFieldFocused,
        DateTimeOffset nowUtc)
    {
        // 1. Nothing to delete. First, so that a fresh session answers "there
        //    is nothing to remove" rather than blaming whatever field happens
        //    to be focused — the field is not the user's problem when the
        //    record is empty.
        if (record is null)
        {
            return new DictationUndoDecision(DictationUndoRefusal.NothingRecorded, 0);
        }

        // 2. The password field, and it is HERE — before the age and the
        //    window — on purpose.
        //
        //    macOS shipped this gate missing for one review cycle and the hole
        //    was exact and reachable: dictate into a normal field, click a
        //    password field IN THE SAME APP, say "scratch that". The bundle-id
        //    guard passed (same app), the age guard passed, and the backspaces
        //    went into the password field while the HUD reported a clean
        //    delete. Pressing backspace in a field is no less a write than
        //    typing into it.
        //
        //    Putting it above the window check is what makes it hold on
        //    Windows too: a password box and the field above it can be the
        //    SAME HWND's children, so a same-window undo is exactly the case
        //    that would otherwise slip through.
        if (secureFieldFocused)
        {
            return new DictationUndoDecision(DictationUndoRefusal.SecureField, 0);
        }

        // 3. Age, in both directions. The negative branch is not defensive
        //    tidiness: an NTP step or a sleep/wake resume moves the clock
        //    backwards, and a record that then reads as dated in the future
        //    would pass any "age > max" test forever. A clock that went
        //    backwards is not a licence to delete.
        var age = nowUtc - record.AtUtc;
        if (age > MaxAge || age < TimeSpan.Zero)
        {
            return new DictationUndoDecision(DictationUndoRefusal.Expired, 0);
        }

        // 4. Same window, and "same" means the handle AND the class.
        //
        //    This is deliberately FINER than macOS, which compares bundle ids
        //    and therefore accepts a known residual risk in its own
        //    KNOWN_ISSUES as K-1: a different ordinary field in the same
        //    application is undo-eligible there. Windows can close that, and
        //    this is where it closes.
        //
        //    An unknown value on EITHER side is a refusal, never a pass. "We
        //    could not tell which window this is" must never resolve to "so go
        //    ahead and delete in it" — that reading turns every failed
        //    GetClassName into a licence.
        //
        //    The class is compared as well as the handle because HWNDs are
        //    recycled: a window closed and another opened inside the two-minute
        //    window can be handed the same numeric handle, and the class is the
        //    cheapest thing that disagrees when that happens.
        if (record.WindowHandle == 0
            || currentWindow == 0
            || record.WindowHandle != currentWindow
            || string.IsNullOrWhiteSpace(record.WindowClass)
            || string.IsNullOrWhiteSpace(currentWindowClass)
            || !string.Equals(record.WindowClass, currentWindowClass, StringComparison.Ordinal))
        {
            return new DictationUndoDecision(DictationUndoRefusal.DifferentWindow, 0);
        }

        // 5. Length. Empty is TooLong rather than an allowed no-op: a zero
        //    record means the bookkeeping is wrong, and "succeed by sending
        //    nothing" would report a clean delete for text still on screen.
        var backspaces = DictationDeleteCommand.BackspaceCount(record.Text);
        if (backspaces == 0 || backspaces > DictationDeleteCommand.MaxBackspaces)
        {
            return new DictationUndoDecision(DictationUndoRefusal.TooLong, 0);
        }

        return new DictationUndoDecision(DictationUndoRefusal.None, backspaces);
    }
}
