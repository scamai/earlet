namespace Dictation.Services;

/// <summary>
/// Whether the thing the user's cursor is in is a password field.
/// </summary>
/// <remarks>
/// Three states, not a bool, and the third one is the important one. "We could
/// not find out" is a real and frequent answer — UI Automation is a
/// cross-process query that a busy or hostile application can decline, and a
/// guard that read that as "secure" would silently switch dictation off for
/// people who are not near a password box at all. See
/// <see cref="SecureFieldState.Unknown"/>.
/// </remarks>
public enum SecureFieldState
{
    /// <summary>
    /// The focused element is not a password field, as far as anything can
    /// tell. <b>Zero deliberately</b>: a defaulted value must mean "carry on",
    /// because the alternative is a struct nobody initialised turning dictation
    /// off.
    /// </summary>
    NotSecure = 0,

    /// <summary>
    /// The focused element reports itself as a password field. This is the only
    /// value that stops a dictation.
    /// </summary>
    Secure = 1,

    /// <summary>
    /// The question could not be answered — no automation element, a query that
    /// threw, a query that took too long, or a build with no probe wired at
    /// all.
    /// <para>
    /// <b>Treated as <see cref="NotSecure"/> by every caller, and that is the
    /// deliberate direction.</b> A guard that failed closed would make
    /// dictation stop working, with no explanation, on any machine where UIA is
    /// slow or restricted — and "dictation is broken" is precisely the reading
    /// this whole feature exists to prevent. Kept as its own value rather than
    /// folded into <see cref="NotSecure"/> so the log can tell "we asked and
    /// the answer was no" from "we never got an answer", which are different
    /// facts when somebody reports that the guard did not fire.
    /// </para>
    /// </summary>
    Unknown = 2,
}

/// <summary>
/// Answers "is the user typing into a password box right now?", once, without
/// side effects.
/// </summary>
/// <remarks>
/// <para>
/// The Windows counterpart of macOS's <c>SecureInputMonitor</c>, and the
/// counterpart is not exact. macOS detects a system-wide Secure Input mode that
/// disables event taps outright — dictation there is refused because it
/// physically cannot work, and the sentence the user needs is "you are in a
/// password field", not "dictation is broken". Windows has no such mode: a
/// <c>WH_KEYBOARD_LL</c> hook is NOT disabled by a password box, so the hold
/// key fires normally and the whole pipeline runs to completion — transcribed,
/// written to the history file, recorded as a WAV, put on the clipboard, and
/// pasted. Windows therefore needs this guard MORE than macOS does, not less:
/// there, the platform refuses; here, nothing does.
/// </para>
/// <para>
/// <b>A seam, so the policy is testable and the platform is not.</b> The one
/// implementation that touches UI Automation lives in the shell project;
/// anything under test substitutes a value-returning fake. Same rule
/// <see cref="IFocusedApplicationResolver"/> states, and for the same reason —
/// a method that handed back an automation element would put the platform back
/// in the abstraction.
/// </para>
/// </remarks>
public interface ISecureFieldProbe
{
    /// <summary>
    /// Reads the focused element. <b>Never throws</b> — every way this can fail
    /// is <see cref="SecureFieldState.Unknown"/>. A probe that threw would take
    /// a dictation down from inside a guard whose entire purpose is to make one
    /// fail politely.
    /// </summary>
    SecureFieldState Probe();
}
