namespace Dictation.Core;

/// <summary>
/// The decisions behind "Start Dictation when I sign in", kept out of the
/// registry code so they run on any machine: which value is written, what it
/// says, and when an existing value counts as on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per user, no elevation.</b> The value lives under
/// <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c>,
/// which every user can write and which Windows reads at that user's sign-in.
/// Off is the default: nothing is written until the user checks the box.
/// </para>
/// <para>
/// <b>This copy, or not at all.</b> The value names the executable's full
/// path. A value that names another path — a portable folder the user moved,
/// or another copy — does not count as on for this one; the box shows
/// unchecked, and checking it rewrites the value to point here.
/// </para>
/// <para>
/// <b>Settings can switch it off without deleting it.</b> Task Manager's and
/// Settings' startup pages disable an entry by writing a flag under
/// <c>Explorer\StartupApproved\Run</c> and leave the <c>Run</c> value in place.
/// An odd first byte there is the disabled state (<c>03</c>; enabled is
/// <c>02</c>), so such an entry reads as off. Checking the box removes that
/// flag, because the user has just asked for the opposite.
/// </para>
/// </remarks>
public static class LaunchAtLoginEntry
{
    /// <summary>The key the value lives in, under <c>HKEY_CURRENT_USER</c>.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Windows records that the user switched an entry off.</summary>
    public const string StartupApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The value's name: the product's own.</summary>
    public const string ValueName = "Dictation";

    /// <summary>
    /// The argument the sign-in launch carries, so the app can tell it from a
    /// launch the user made and start in the notification area.
    /// </summary>
    public const string LoginArgument = "--login";

    /// <summary>The value's data for <paramref name="executable"/>: the quoted
    /// path, then <see cref="LoginArgument"/>.</summary>
    public static string Command(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return $"\"{executable}\" {LoginArgument}";
    }

    /// <summary>Whether the process was started by the sign-in entry.</summary>
    public static bool IsLoginLaunch(IEnumerable<string>? arguments) =>
        arguments?.Any(a => string.Equals(a?.Trim(), LoginArgument, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>
    /// Whether the entry as found starts <paramref name="executable"/> at the
    /// next sign-in.
    /// </summary>
    /// <param name="runValue">The <c>Run</c> value's data, or null when absent.</param>
    /// <param name="approved">The <c>StartupApproved</c> flag's bytes, or null when absent.</param>
    /// <param name="executable">This copy's full path.</param>
    public static bool IsOn(string? runValue, byte[]? approved, string executable) =>
        runValue is not null
        && string.Equals(runValue.Trim(), Command(executable), StringComparison.OrdinalIgnoreCase)
        && !SwitchedOffInSettings(approved);

    /// <summary>Whether Windows' startup page switched the entry off.</summary>
    public static bool SwitchedOffInSettings(byte[]? approved) =>
        approved is { Length: > 0 } && (approved[0] & 1) == 1;

    /// <summary>
    /// Whether the entry would start <paramref name="executable"/> but for
    /// the switch in Settings → Apps → Startup (or Task Manager): the one
    /// "off" the window explains under the box, because the user did not make
    /// it there and checking the box undoes it. An entry for another copy, or
    /// none, is plain off.
    /// </summary>
    /// <param name="runValue">The <c>Run</c> value's data, or null when absent.</param>
    /// <param name="approved">The <c>StartupApproved</c> flag's bytes, or null when absent.</param>
    /// <param name="executable">This copy's full path.</param>
    public static bool IsSwitchedOff(string? runValue, byte[]? approved, string executable) =>
        runValue is not null
        && string.Equals(runValue.Trim(), Command(executable), StringComparison.OrdinalIgnoreCase)
        && SwitchedOffInSettings(approved);
}
