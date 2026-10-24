using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Dictation.Services;

/// <summary>
/// <see cref="IFocusedApplicationResolver"/> against the real desktop.
/// </summary>
/// <remarks>
/// <para>
/// The whole reason this is a separate file from
/// <c>FocusedApplication.cs</c>: the interface, the result type and the status
/// enum are what the rest of dictation compiles against, and none of them
/// mention <c>user32</c>. A test that substitutes
/// <see cref="IFocusedApplicationResolver"/> gets values and cannot reach a
/// P/Invoke, because every <c>DllImport</c> in this feature is private to this
/// class. Keeping them here rather than in the seam file makes that checkable
/// by looking at one file rather than by reading a convention.
/// </para>
/// <para>
/// One read, no retry and no wait. A dictation hotkey fires while the user is
/// already looking at their target; if the foreground is not what they meant,
/// polling until it is would mean typing into a window they moved away from.
/// The answer is a snapshot of the instant it was asked for, and that is all it
/// claims to be.
/// </para>
/// <para>
/// Compiled into a plain <c>net9.0</c> assembly, which is why the class is
/// attributed rather than the project retargeted: the declarations below bind
/// lazily, so this file costs a macOS or Linux checkout nothing until something
/// calls it, and nothing on those platforms does.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class Win32FocusedApplicationResolver
    : IFocusedApplicationResolver, IKeyboardFocusProbe
{
    private readonly ILogger<Win32FocusedApplicationResolver> _logger;

    public Win32FocusedApplicationResolver(ILogger<Win32FocusedApplicationResolver> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public FocusedApplicationResult Resolve()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            // Not a failure. The desktop has focus, or the workstation is
            // locked, or the shell is between windows. Ordinary enough that
            // logging it at anything above debug would be noise on a hotkey
            // the user can press whenever they like.
            _logger.LogDebug("No foreground window.");
            return FocusedApplicationResult.NoFocusedWindow;
        }

        // Read the caption first, so it is available to describe the window
        // even on the path where the process cannot be identified. It is the
        // only human-readable handle on an Unresolvable result.
        var title = ReadWindowTitle(window);

        var thread = GetWindowThreadProcessId(window, out var rawProcessId);
        if (thread == 0 || rawProcessId == 0)
        {
            _logger.LogDebug(
                "Foreground window {Title} has no owning thread (error {Error}).",
                title,
                Marshal.GetLastWin32Error());
            return FocusedApplicationResult.Unresolvable(title);
        }

        if (rawProcessId > int.MaxValue)
        {
            // A DWORD that will not fit the int this result exposes. Windows
            // does not hand out ids this large today, but the cast is the kind
            // that goes wrong silently and negative if it ever does, and a
            // negative process id in a log is worse than an honest
            // "could not identify".
            _logger.LogWarning("Foreground process id {ProcessId} does not fit an int.", rawProcessId);
            return FocusedApplicationResult.Unresolvable(title);
        }

        var processId = (int)rawProcessId;

        try
        {
            using var process = Process.GetProcessById(processId);
            return FocusedApplicationResult.Resolved(process.ProcessName, processId, title);
        }
        catch (Exception ex) when (
            ex is ArgumentException          // exited between the two calls
                or InvalidOperationException // handle went away mid-read
                or Win32Exception            // querying it is not permitted
                or NotSupportedException)    // remote-machine process handle
        {
            // The window is real and would receive a keystroke; only its
            // identity is missing. Callers that refuse to type on this are
            // making a policy choice, and they get the title and the id to
            // make it with.
            _logger.LogDebug(
                ex,
                "Could not identify process {ProcessId} behind foreground window {Title}.",
                processId,
                title);
            return FocusedApplicationResult.Unresolvable(title, processId);
        }
    }

    /// <summary>
    /// <see cref="IKeyboardFocusProbe.Read"/> — which window inside the
    /// foreground thread would receive a synthesised keystroke.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetGUIThreadInfo</c> on the FOREGROUND thread rather than
    /// <c>GetFocus</c>, which only ever answers about the calling thread's own
    /// queue and would return zero here every time. Passing the foreground
    /// thread id is what makes it a cross-process read, and it is the same
    /// call the device harness uses for ground truth — so the app and its
    /// verifier are reading the same field rather than two things that agree
    /// by hope.
    /// </para>
    /// <para>
    /// Cheap by measurement: this is on the path that types the user's
    /// sentence, and it is two user32 calls with no cross-process message and
    /// no marshalled string from another process's heap. It cannot block on a
    /// hung target the way <c>SendMessage</c> would.
    /// </para>
    /// </remarks>
    public KeyboardFocusTarget Read()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            // A successful read of "nothing has focus" — distinct from a read
            // that failed, which is what Unknown means.
            return KeyboardFocusTarget.Of(0, null);
        }

        var thread = GetWindowThreadProcessId(foreground, out _);
        if (thread == 0)
        {
            return KeyboardFocusTarget.Unknown;
        }

        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info))
        {
            _logger.LogDebug(
                "Could not read the foreground thread's GUI info (error {Error}).",
                Marshal.GetLastWin32Error());
            return KeyboardFocusTarget.Unknown;
        }

        return KeyboardFocusTarget.Of(
            info.hwndFocus.ToInt64(),
            info.hwndFocus == IntPtr.Zero ? null : ReadClassName(info.hwndFocus));
    }

    private static string ReadClassName(IntPtr window)
    {
        // 256 is the documented ceiling on a window class name.
        var buffer = new StringBuilder(256);
        var copied = GetClassName(window, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString(0, copied) : string.Empty;
    }

    private static string ReadWindowTitle(IntPtr window)
    {
        // GetWindowTextLength returns 0 both for an untitled window and for a
        // failure. Neither is worth distinguishing here: the answer to both is
        // an empty caption, and no other behaviour changes on the difference.
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        // +1 for the terminator GetWindowText writes. The title of a window in
        // another process can change between these two calls; GetWindowText
        // truncates to the buffer it is given, so the worst case is a short
        // caption rather than an overrun.
        var buffer = new StringBuilder(length + 1);
        var copied = GetWindowText(window, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString(0, copied) : string.Empty;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Unicode explicitly, on both text calls. The ANSI variants would mangle a
    // caption the moment somebody dictates into a window titled in Chinese,
    // and "wrong window title in the log" is the failure that costs an hour.
    // GetWindowText reads another process's cached caption rather than sending
    // it a WM_GETTEXT, so a hung application cannot block this call.
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW", SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    // Every field below is written by the OS through the ref parameter and
    // most are never touched from C#, which is exactly what CS0649 warns
    // about. Deleting them to silence it would change the struct's size and
    // GetGUIThreadInfo rejects a cbSize that does not match.
#pragma warning disable CS0649

    /// <summary>
    /// <c>RECT</c>, declared only because <c>GUITHREADINFO</c> ends with one
    /// and <c>cbSize</c> has to match the real struct or the call fails.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// The foreground thread's input state. Only <c>hwndFocus</c> is read; the
    /// rest of the fields exist so the struct is the size the API expects.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

#pragma warning restore CS0649
}
