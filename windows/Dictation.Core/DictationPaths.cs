namespace Dictation.Core;

/// <summary>
/// Where the Windows app keeps its own files: <c>%LOCALAPPDATA%\Dictation</c>,
/// the same root the worker's alias table uses. Never the source app's data
/// directory.
/// </summary>
/// <param name="Root">The product data directory.</param>
public sealed record DictationPaths(string Root)
{
    /// <summary>The directory name under <c>%LOCALAPPDATA%</c>.</summary>
    public const string ProductDirectoryName = "Dictation";

    /// <summary>This user's data directory.</summary>
    public static DictationPaths ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductDirectoryName));

    /// <summary>The app's settings: the hold key.</summary>
    public string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The log directory's name under <see cref="Root"/>, which
    /// <see cref="DictationCopy.LogFolderNotOpened"/> names too.</summary>
    public const string LogDirectoryName = "logs";

    /// <summary>The diagnostic log directory.</summary>
    public string LogDirectory => Path.Combine(Root, LogDirectoryName);

    /// <summary>Where a take's audio is spooled while it is recorded, and
    /// where the last take that could not be transcribed is kept for Recover
    /// last dictation (<see cref="DictationTakeSpool"/>).</summary>
    public string RecoveryDirectory => Path.Combine(Root, "recovery");
}
