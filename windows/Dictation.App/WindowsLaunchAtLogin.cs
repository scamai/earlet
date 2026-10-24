using System.IO;
using Dictation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Dictation.App;

/// <summary>
/// <see cref="ILaunchAtLogin"/> against the current user's <c>Run</c> key.
/// What the value says and when it counts as on are
/// <see cref="LaunchAtLoginEntry"/>'s decisions; this only reads and writes.
/// </summary>
internal sealed class WindowsLaunchAtLogin(string executable, ILogger<WindowsLaunchAtLogin> log) : ILaunchAtLogin
{
    public bool IsEnabled => Read(LaunchAtLoginEntry.IsOn);

    public bool SwitchedOffInSettings => Read(LaunchAtLoginEntry.IsSwitchedOff);

    /// <summary>The <c>Run</c> value and its <c>StartupApproved</c> flag, as
    /// <paramref name="decide"/> reads them for this copy.</summary>
    private bool Read(Func<string?, byte[]?, string, bool> decide)
    {
        using var run = Registry.CurrentUser.OpenSubKey(LaunchAtLoginEntry.RunKeyPath);
        using var approved = Registry.CurrentUser.OpenSubKey(LaunchAtLoginEntry.StartupApprovedKeyPath);
        return decide(
            run?.GetValue(LaunchAtLoginEntry.ValueName) as string,
            approved?.GetValue(LaunchAtLoginEntry.ValueName) as byte[],
            executable);
    }

    public bool TrySet(bool enabled)
    {
        try
        {
            using (var run = Registry.CurrentUser.CreateSubKey(LaunchAtLoginEntry.RunKeyPath, writable: true))
            {
                if (enabled)
                {
                    run.SetValue(
                        LaunchAtLoginEntry.ValueName, LaunchAtLoginEntry.Command(executable), RegistryValueKind.String);
                }
                else
                {
                    run.DeleteValue(LaunchAtLoginEntry.ValueName, throwOnMissingValue: false);
                }
            }

            // Either way the user has just decided: a "switched off" flag the
            // Settings startup page left behind must not overrule them.
            using var approved = Registry.CurrentUser.OpenSubKey(LaunchAtLoginEntry.StartupApprovedKeyPath, writable: true);
            approved?.DeleteValue(LaunchAtLoginEntry.ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            log.LogWarning(error, "The sign-in entry could not be changed");
            return false;
        }
    }
}
