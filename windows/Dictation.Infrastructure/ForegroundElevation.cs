using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Dictation.Infrastructure;

/// <summary>
/// Whether the window in front belongs to a process running as
/// administrator, where the hold-key hook cannot hear the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> User Interface Privilege Isolation keeps a
/// low-level hook in a process at medium integrity from seeing keys sent to a
/// window at a higher one, so with an elevated window in front the hold key,
/// the stop keys and Escape all go unseen, and nothing used to say why. The
/// shell asks this from its poll, cached for the process in front (see
/// Dictation.Core's <c>AdminWindowWatch</c>), and never from the hook's
/// callback, which must not make cross-process calls.
/// </para>
/// <para>
/// <b>How.</b> <c>OpenProcess</c> for limited query access, then the
/// process token's <c>TokenElevation</c>. A process this one may not open
/// at all, or may open but whose token it may not read, is counted as
/// elevated: an administrator's elevated token typically refuses a
/// non-elevated caller, a program a standard user elevated with an
/// administrator's name and password runs as that administrator and refuses
/// to be opened, and a process above this one is exactly the case UIPI cuts
/// off. Any other failure is null, which the watch treats as "cannot tell":
/// never a lost hook. Not measured on Windows: which processes refuse the
/// token or the handle to a medium-integrity caller, and whether an elevated
/// window's token can be read at all.
/// </para>
/// <para>
/// Calls the window manager and the process in front, never sends a message,
/// so a hung application cannot block it.
/// </para>
/// </remarks>
public sealed class ForegroundElevation(ILogger<ForegroundElevation> logger)
{
    private readonly Lazy<bool> _thisProcess = new(() => IsElevatedProcess(GetCurrentProcess()) == true);

    /// <summary>
    /// Whether Dictation itself runs elevated, when no window can be above it
    /// for UIPI's purposes and the question never needs asking.
    /// </summary>
    public bool ThisProcessElevated => _thisProcess.Value;

    /// <summary>The process that owns the window in front, or 0 when there is
    /// none (a secure desktop, a switch in progress).</summary>
    public int ForegroundProcessId() => LowLevelHoldKeyRegistration.ForegroundProcessId();

    /// <summary>Whether <paramref name="processId"/> runs elevated; null when
    /// it cannot be told (the process is gone, or could not be opened for a
    /// reason other than access denied).</summary>
    public bool? IsElevated(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            logger.LogDebug("The process in front could not be opened (error {Error})", error);
            // Refused outright: another user's process or one above this
            // one, as a refused token below (see remarks).
            return error == ErrorAccessDenied ? true : null;
        }

        try
        {
            return IsElevatedProcess(process);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static bool? IsElevatedProcess(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            // Opened, but its token refused: above this process (see remarks).
            return Marshal.GetLastWin32Error() == ErrorAccessDenied ? true : null;
        }

        try
        {
            return GetTokenInformation(token, TokenElevationClass, out var elevation, sizeof(int), out _)
                ? elevation != 0
                : null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;
    private const int ErrorAccessDenied = 5;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token, int informationClass, out int information, int length, out int returned);
}
