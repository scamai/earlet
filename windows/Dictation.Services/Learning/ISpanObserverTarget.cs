namespace Dictation.Services.Learning;

/// <summary>
/// What the platform adapter provides to the span observer (the span-observer contract
/// §1, §3). Everything here is a value or a bounded call; nothing hands back a
/// UI Automation element, so the state machine in <see cref="SpanObservation"/>
/// is driven in tests by a fake and never loads a platform library (the rule
/// <see cref="IFocusedApplicationResolver"/> states).
/// </summary>
/// <remarks>
/// <para>
/// <b>Offsets.</b> Every offset is a UTF-16 character offset into the text of
/// the element the adapter is currently reading: the focused element until
/// <see cref="ReadText"/> succeeds, the text-bearing descendant it found after
/// that (§3.2 step B). The adapter owns that switch; the observer only asks.
/// </para>
/// <para>
/// <b>Failure is a value.</b> A call that times out, throws or is unsupported
/// answers with <see cref="SpanObserverResult{T}.Failure"/> set; the observer
/// turns every failure into an <c>unobservable</c> terminal (invariant O2, §0).
/// </para>
/// </remarks>
public interface ISpanObserverTarget
{
    /// <summary>The focused control's identity, resolved once at <c>t0</c>.</summary>
    SpanObserverTargetIdentity Identity { get; }

    /// <summary>
    /// §3.1: the secure-field gate, re-asked at <c>t0</c> because focus may
    /// have moved during the paste. <see cref="SecureFieldState.Unknown"/> is
    /// read as "stop" by the observer — the opposite of the dictation guard.
    /// </summary>
    SpanObserverResult<SecureFieldState> ProbeSecureField();

    /// <summary>Step A: the current selection (or collapsed caret) in the focused element.</summary>
    SpanObserverResult<SpanObserverSelection> GetSelection();

    /// <summary>
    /// One bounded read of <c>[start, start + length)</c>, clamped to the
    /// document. Used to confirm the selection anchor (step A) and to track the
    /// anchored range (§3.3). The text is compared and discarded by the observer.
    /// </summary>
    SpanObserverResult<string> ReadRange(int start, int length);

    /// <summary>
    /// Step B: the one bounded whole-field read per hold, at most
    /// <paramref name="maxChars"/> characters (the observer passes
    /// <c>L_read + 1</c> and treats a longer answer as <c>doc_too_long</c>).
    /// Exactly one descendant search is allowed behind this call; the result
    /// lives in a local and is discarded after anchoring (invariant O1).
    /// </summary>
    SpanObserverResult<string> ReadText(int maxChars);

    /// <summary>
    /// Change notifications. Returns <c>null</c> when the provider cannot
    /// deliver them, in which case the observer polls every <c>t_poll</c>.
    /// The handler may be invoked on any thread; the observer only flips flags.
    /// </summary>
    IDisposable? Subscribe(Action<SpanObserverSignal> handler);

    /// <summary>
    /// §5.1: timestamp and foreground pid of the last keyboard input the
    /// low-level hook saw — never key codes. <c>null</c> when none was seen.
    /// </summary>
    SpanObserverKeyboardInput? LastKeyboardInput { get; }
}

/// <summary>Identity of the focused control at <c>t0</c>. No window title (titles leak document names, §7).</summary>
public readonly record struct SpanObserverTargetIdentity(
    string ProcessName,
    int ProcessId,
    string WindowClass,
    long FocusHandle,
    bool InProcess)
{
    /// <summary>No focused element could be resolved — the observer answers <c>unobservable:no_focus</c>.</summary>
    public static SpanObserverTargetIdentity Unresolved { get; } = new(string.Empty, 0, string.Empty, 0, false);

    /// <summary>§4.3: one of this app's own WPF editors, observed in-process with no UIA.</summary>
    public static SpanObserverTargetIdentity OwnPane(string paneName, long handle) =>
        new("Dictation", Environment.ProcessId, paneName, handle, InProcess: true);

    public bool IsResolved => InProcess || ProcessId != 0;

    /// <summary>§6 <c>t_redo</c>: same process and same focused control.</summary>
    public bool SameControlAs(SpanObserverTargetIdentity other) =>
        ProcessId == other.ProcessId && FocusHandle == other.FocusHandle;
}

/// <summary>A selection as character offsets; a collapsed caret has <see cref="Length"/> 0.</summary>
public readonly record struct SpanObserverSelection(int Start, int Length);

/// <summary>Notification from the adapter (§3.3).</summary>
public enum SpanObserverSignal
{
    TextChanged = 0,
    FocusLost = 1,
}

/// <summary>The last keyboard input the hook saw: when, and which process was in the foreground.</summary>
public readonly record struct SpanObserverKeyboardInput(long UtcMs, int ProcessId);

/// <summary>
/// The keyboard-origin feed (§5.1). The low-level hook dictation already
/// installs calls <see cref="Record"/> with a timestamp and the foreground pid
/// — timestamps and pids only, never key codes (the Activity contract's
/// "no keystroke content anywhere").
/// </summary>
public sealed class SpanObserverKeyboardSignal
{
    private long _utcMs = -1;
    private int _pid;
    private readonly object _gate = new();

    public void Record(long utcMs, int foregroundProcessId)
    {
        lock (_gate)
        {
            _utcMs = utcMs;
            _pid = foregroundProcessId;
        }
    }

    public SpanObserverKeyboardInput? Last
    {
        get
        {
            lock (_gate)
            {
                return _utcMs < 0 ? null : new SpanObserverKeyboardInput(_utcMs, _pid);
            }
        }
    }
}

/// <summary>
/// The answer to one bounded adapter call: a value, or a failure reason
/// (<c>uia_timeout</c>, <c>no_textpattern</c>, <c>no_selection</c>,
/// <c>exception</c>…) that becomes the <c>unobservable</c> reason. Elapsed
/// milliseconds feed the E4 anchor trace (§8).
/// </summary>
public readonly record struct SpanObserverResult<T>(T? Value, string? Failure, double ElapsedMs)
{
    public bool Ok => Failure is null;

    public static SpanObserverResult<T> Success(T value, double elapsedMs = 0) => new(value, null, elapsedMs);

    public static SpanObserverResult<T> Failed(string reason, double elapsedMs = 0) => new(default, reason, elapsedMs);
}

/// <summary>Failure reasons shared by the adapters and the observer.</summary>
public static class SpanObserverFailures
{
    public const string UiaTimeout = "uia_timeout";
    public const string NoTextPattern = "no_textpattern";
    public const string NoSelection = "no_selection";
    public const string NoFocus = "no_focus";
    public const string Exception = "exception";
}
