using System.Runtime.InteropServices;

namespace Dictation.App;

/// <summary>Whether the window in front is one of Dictation's own
/// (<see cref="Dictation.Core.OwnWindowSecureFieldProbe"/>).</summary>
/// <remarks>Two user32 calls with no message to any window, so it cannot block
/// on a hung application, and safe from any thread: the password-field probe
/// asks from the UI thread at release and from the pool at press.</remarks>
internal static class ForegroundWindow
{
    public static bool IsThisProcess()
    {
        var window = GetForegroundWindow();
        return window != IntPtr.Zero
            && GetWindowThreadProcessId(window, out var processId) != 0
            && processId == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
