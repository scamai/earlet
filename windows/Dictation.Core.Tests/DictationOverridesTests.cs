using System.Text.RegularExpressions;

namespace Dictation.Core.Tests;

/// <summary>
/// One set of <c>DICTATION_*</c> overrides, read in one place.
/// </summary>
/// <remarks>
/// Source-tree checks: they read the <c>.cs</c> files of this checkout (or
/// of a copy of <c>apps/dictation</c>), found by walking up from the test
/// binary.
/// </remarks>
public sealed partial class DictationOverridesTests
{
    [GeneratedRegex("\"(DICTATION_[A-Z0-9_]+)\"")]
    private static partial Regex Literal();

    internal static string WindowsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dictation.Core", "Dictation.Core.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("no windows/ source tree above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Sources(params string[] relative)
    {
        var root = WindowsRoot();
        foreach (var entry in relative)
        {
            var path = Path.Combine(root, entry);
            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                  && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            {
                yield return file;
            }
        }
    }

    /// <summary>The Windows app's own code and the worker launch it uses.</summary>
    private static readonly string[] AppComposition =
    [
        "Dictation.App",
        "Dictation.Core",
        Path.Combine("Dictation.Platform", "Transcription", "PythonAsrWorkerProcess.cs"),
    ];

    [Fact]
    public void The_app_reads_the_environment_in_exactly_one_place()
    {
        var files = Sources(AppComposition).ToArray();
        Assert.True(files.Length > 5, $"only {files.Length} source files were scanned");

        var readers = files
            .Where(f => File.ReadAllText(f).Contains("GetEnvironmentVariable(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        Assert.Equal(["DictationEnvironment.cs"], readers);
    }

    [Fact]
    public void Every_DICTATION_name_in_the_app_is_a_documented_override_or_a_worker_input()
    {
        // The worker inputs are variables the app SETS for the child process:
        // the engine choice and the reply pipe. Everything else must be in
        // DictationEnvironment.Overrides, so there is one list to document.
        var allowed = DictationEnvironment.Overrides
            .Concat(["DICTATION_ASR_ENGINE", "DICTATION_ASR_PROTOCOL_PIPE"])
            .ToHashSet();

        var found = Sources(AppComposition)
            .SelectMany(f => Literal().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet();

        Assert.Contains(DictationEnvironment.ModelDirectory, found);
        Assert.Empty(found.Except(allowed));
    }

    [Fact]
    public void No_doubled_prefix_survives_anywhere_in_the_Windows_tree()
    {
        // The mechanical rename of the source app's prefix to DICTATION_
        // doubled the word in every variable that already named dictation.
        // The spelling searched for is assembled so this file does not match.
        var doubled = "DICTATION_" + "DICTATION";
        var files = Sources(".").ToArray();
        Assert.True(files.Length > 100, $"only {files.Length} source files were scanned");

        var hits = files
            .Where(f => File.ReadAllText(f).Contains(doubled, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(WindowsRoot(), f))
            .ToArray();

        Assert.Empty(hits);
    }
}
