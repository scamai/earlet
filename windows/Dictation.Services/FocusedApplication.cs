namespace Dictation.Services;

/// <summary>
/// How much is known about the window that had focus.
/// </summary>
/// <remarks>
/// The three states are exhaustive on purpose: every way "which window is the
/// user typing into?" can turn out is one of them, so a caller that switches
/// over this has handled the question, and there is no fourth case hiding in a
/// null or an exception.
/// </remarks>
public enum FocusedApplicationStatus
{
    /// <summary>
    /// There is no foreground window at all — the desktop has focus, or the
    /// session is locked, or a shell transition is mid-flight.
    /// <b>Zero is this state deliberately.</b> A defaulted
    /// <see cref="FocusedApplicationResult"/> — one that came out of an
    /// uninitialised array slot, or a struct field nobody assigned — must not
    /// read as a resolved window, because "resolved" is the value that
    /// authorises typing. The safe answer is the one you get for free.
    /// </summary>
    NoFocusedWindow = 0,

    /// <summary>A window has focus and its process and title were read.</summary>
    Resolved = 1,

    /// <summary>
    /// A window has focus, but the process behind it could not be identified —
    /// it exited between the two calls, or it is running at an integrity level
    /// this process cannot query. The handle was real; the identity is not
    /// known. <see cref="FocusedApplicationResult.WindowTitle"/> may still be
    /// populated, since the caption is readable when the process is not.
    /// </summary>
    Unresolvable = 2,
}

/// <summary>
/// What had focus when the question was asked, whatever the answer turned out
/// to be.
/// </summary>
/// <remarks>
/// <para>
/// A <c>readonly record struct</c> rather than a class, so
/// <see cref="IFocusedApplicationResolver.Resolve"/> <em>cannot</em> answer
/// with null: "no window" is a value in this type, not an absence of one. The
/// string accessors coalesce to <see cref="string.Empty"/> rather than
/// exposing the backing fields directly, so even a defaulted instance hands
/// back a usable empty string instead of a null reference into a log format.
/// </para>
/// <para>
/// The result is a snapshot, not a handle. By the time a caller reads it the
/// foreground may already have changed — which is why nothing here carries an
/// <c>HWND</c>. A keystroke synthesised later lands wherever focus is
/// <em>then</em>, and no value in this record can promise otherwise; it is
/// evidence about an instant, and the window between reading it and acting on
/// it is the caller's risk to keep short.
/// </para>
/// </remarks>
public readonly record struct FocusedApplicationResult
{
    private readonly string? _processName;
    private readonly string? _windowTitle;

    private FocusedApplicationResult(
        FocusedApplicationStatus status,
        string? processName,
        int processId,
        string? windowTitle)
    {
        Status = status;
        _processName = processName;
        ProcessId = processId;
        _windowTitle = windowTitle;
    }

    /// <summary>Which of the three answers this is.</summary>
    public FocusedApplicationStatus Status { get; }

    /// <summary>
    /// The process name without extension — <c>slack</c>, <c>devenv</c>. Empty
    /// for every status other than <see cref="FocusedApplicationStatus.Resolved"/>,
    /// never null.
    /// </summary>
    public string ProcessName => _processName ?? string.Empty;

    /// <summary>
    /// The owning process id, or <c>0</c> when there is no window or the owner
    /// could not be established.
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// The window caption, which is how one conversation or document is told
    /// from another. Empty rather than null when the window has no title, and
    /// possibly populated even when the status is
    /// <see cref="FocusedApplicationStatus.Unresolvable"/>.
    /// </summary>
    public string WindowTitle => _windowTitle ?? string.Empty;

    /// <summary>Whether the process behind the focused window is known.</summary>
    public bool IsResolved => Status == FocusedApplicationStatus.Resolved;

    /// <summary>
    /// Whether something has focus at all — true for
    /// <see cref="FocusedApplicationStatus.Resolved"/> and
    /// <see cref="FocusedApplicationStatus.Unresolvable"/> alike, because both
    /// mean a real window would receive a keystroke. The distinction between
    /// them is who owns it, not whether it exists.
    /// </summary>
    public bool HasFocusedWindow => Status != FocusedApplicationStatus.NoFocusedWindow;

    /// <summary>Nothing has focus.</summary>
    public static FocusedApplicationResult NoFocusedWindow { get; } =
        new(FocusedApplicationStatus.NoFocusedWindow, null, 0, null);

    /// <summary>A window, its process, and its caption.</summary>
    public static FocusedApplicationResult Resolved(string processName, int processId, string windowTitle) =>
        new(
            FocusedApplicationStatus.Resolved,
            processName ?? string.Empty,
            processId,
            windowTitle ?? string.Empty);

    /// <summary>
    /// A window whose owner could not be established. The caption is kept
    /// because it is often the only thing left to log, and a process id is
    /// accepted for the case where the id was read but the name was not.
    /// </summary>
    public static FocusedApplicationResult Unresolvable(string windowTitle, int processId = 0) =>
        new(FocusedApplicationStatus.Unresolvable, null, processId, windowTitle ?? string.Empty);
}

/// <summary>
/// Answers "what is the user looking at right now?", once, without side
/// effects.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that keeps Win32 out of the dictation policy. The only
/// implementation that touches <c>user32</c> is
/// <see cref="Win32FocusedApplicationResolver"/>, in its own file; anything
/// under test substitutes a value-returning fake and never loads a native
/// library. That is the property to preserve when this interface grows — a
/// method that handed back an <c>HWND</c>, or took one, would put the platform
/// back in the abstraction and let a unit test reach a P/Invoke through it.
/// </para>
/// <para>
/// Read-only by construction: nothing here focuses, raises, or activates a
/// window. Deciding what has focus and changing what has focus are different
/// authorities, and this interface holds only the first.
/// </para>
/// </remarks>
public interface IFocusedApplicationResolver
{
    /// <summary>
    /// Reads the foreground window. Returns
    /// <see cref="FocusedApplicationResult.NoFocusedWindow"/> when there is
    /// none and
    /// <see cref="FocusedApplicationStatus.Unresolvable"/> when there is one
    /// this process cannot identify — <b>never null, and never an exception for
    /// either of those</b>. "The desktop has focus" is an ordinary Tuesday for
    /// a dictation hotkey, not an error, and a caller that has to wrap this in
    /// a try/catch to find that out will eventually forget to.
    /// </summary>
    FocusedApplicationResult Resolve();
}
