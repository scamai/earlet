using Xunit;

namespace Dictation.TestSupport;

/// <summary>
/// A fact whose subject is a Windows platform guarantee, so it reports SKIP
/// with a reason off Windows instead of failing there.
///
/// Four kinds of subject need this, and they are all statements about
/// Windows rather than about the code under test:
///
/// <list type="bullet">
/// <item>File-system sharing. Windows enforces sharing modes in the kernel:
/// a handle opened without <c>FileShare.Write</c>/<c>FileShare.Delete</c>
/// makes another process's write or <c>MoveFileEx(REPLACE_EXISTING)</c>
/// fail, and path comparison is case-insensitive. On Linux and macOS none of
/// that holds — .NET's <c>FileShare</c> is advisory, a rename over an open
/// file succeeds, and <c>/tmp</c> and <c>/TMP</c> are two directories.</item>
/// <item>The registry, which only Windows has.</item>
/// <item>DPAPI and Credential Manager, whose managed wrappers throw
/// <c>PlatformNotSupportedException</c> everywhere else.</item>
/// <item>GDI+. <c>System.Drawing</c> is Windows-only in .NET 6 and later, so
/// a test that builds a <c>Bitmap</c> to stand in for a captured frame dies
/// loading <c>gdiplus.dll</c> before it asserts anything.</item>
/// </list>
///
/// A plain <c>[Fact]</c> made those five tests fail on every POSIX host from
/// the day they landed (2026-08-21), one day after
/// <c>tools/coverage_floors.json</c> recorded its numbers on macOS. Because
/// <c>coverage_collect.py</c> refuses to report coverage from a red suite,
/// that single day ended coverlet collection everywhere but Windows and
/// nothing said so out loud.
///
/// An early <c>return</c> guarded by <c>OperatingSystem.IsWindows()</c>
/// would
/// also make the run green, but green by silence: the runner would print
/// "passed" for a test that asserted nothing. Setting <c>Skip</c> keeps the
/// count honest, and it costs nothing on Windows, where <c>Skip</c> stays
/// null and the test runs exactly as before.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute(string because)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: " + because;
        }
    }
}
