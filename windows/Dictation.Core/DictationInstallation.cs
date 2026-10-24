using System.Reflection;

namespace Dictation.Core;

/// <summary>
/// The copy that is running: its version, its build stamp and the path of its
/// executable. Logged at launch so the installed copy can be told apart from
/// any other (the Mac's <c>DictationInstallation</c>).
/// </summary>
/// <param name="Version">The product version the build was given.</param>
/// <param name="Build">The packager's UTC <c>yyyyMMddHHmmss</c> stamp, or
/// null for a build that was not packaged.</param>
/// <param name="Path">The running executable.</param>
public sealed record DictationInstallation(string Version, string? Build, string Path)
{
    /// <summary>The assembly metadata key <c>tools/package.py</c> stamps,
    /// through <c>-p:DictationBuild</c> and Dictation.App.csproj.</summary>
    public const string BuildMetadataKey = "DictationBuild";

    /// <summary>"Version 0.1.0 · Build 20261009120000", or "… · Build not
    /// recorded" for a developer build.</summary>
    public string VersionLabel => string.IsNullOrWhiteSpace(Build)
        ? $"Version {Version} · Build not recorded"
        : $"Version {Version} · Build {Build}";

    /// <summary>What <see cref="Path"/> says when Windows did not report the
    /// executable's path.</summary>
    public const string UnknownPath = "unknown";

    /// <summary>Whether <see cref="Path"/> names a file File Explorer can
    /// show (Troubleshooting's Show this copy in File Explorer).</summary>
    public bool PathKnown => !string.IsNullOrWhiteSpace(Path) && Path != UnknownPath;

    /// <summary>The log's launch line, as the Mac writes it.</summary>
    public string LaunchLine => $"launch {VersionLabel} path={Path}";

    /// <summary>
    /// File Explorer's command line for Show this copy in File Explorer: its
    /// folder, with <paramref name="file"/> selected. The switch, then the
    /// path in quotes, the form Explorer documents. Explorer
    /// splits its switches on commas, so an unquoted path with a comma
    /// ("C:\Users\Smith, John\…") can be cut there; and quoting the whole
    /// switch, which <c>ProcessStartInfo.ArgumentList</c> does to any path
    /// with a space ("C:\Program Files\…"), is a form Explorer does not
    /// document. A Windows path cannot hold a double quote, so nothing needs
    /// escaping. Neither form has been run on Windows here.
    /// </summary>
    public static string SelectInExplorerArguments(string file) => $"/select,\"{file}\"";

    /// <summary>Reads <paramref name="assembly"/>'s version and build stamp.</summary>
    /// <param name="processPath"><see cref="Environment.ProcessPath"/>.</param>
    public static DictationInstallation Of(Assembly assembly, string? processPath)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Development";
        var build = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == BuildMetadataKey)?.Value;
        return new DictationInstallation(version, build, string.IsNullOrEmpty(processPath) ? UnknownPath : processPath);
    }
}
