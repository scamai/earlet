using System.Text.RegularExpressions;

namespace Dictation.Services.Tests;

/// <summary>
/// Where this suite finds the files it did not write itself.
/// </summary>
/// <remarks>
/// A fixture is committed under this project's <c>Fixtures/</c>, the csproj
/// copies that folder next to the assembly, and a test reads it from there
/// without searching anywhere, so a copy of apps/dictation with nothing beside
/// it runs the same tests as the monorepo. No test reads one today;
/// <see cref="Fixture"/> stays as the way to read the next one, which is what
/// <see cref="TestPathsTests"/> tells an offender to use.
/// <see cref="AppRoot"/> is for the rare test whose subject is one of this
/// product's own source files; it stops at Dictation's <c>product.json</c> and
/// never looks past it.
/// </remarks>
internal static class TestPaths
{
    /// <summary>A committed fixture under <c>Fixtures/</c>, as copied beside the test assembly.</summary>
    public static string Fixture(params string[] parts)
    {
        var path = Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);
        Assert.True(
            Directory.Exists(path) || File.Exists(path),
            $"fixture missing: {path}. Commit it under Fixtures/ in the test project; the csproj copies that folder beside the tests.");
        return path;
    }

    /// <summary>The apps/dictation directory this test assembly was built from.</summary>
    public static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !(File.Exists(Path.Combine(dir.FullName, "product.json")) &&
                 Directory.Exists(Path.Combine(dir.FullName, "windows"))))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}

/// <summary>
/// AGENTS.md: tests must close inside apps/dictation. The run that proves it
/// (a copy with nothing beside it) is manual and easy to skip, so the cheap
/// half runs every time: no test source may name a path into a folder that
/// sits beside this product, whatever that folder is called.
/// </summary>
public sealed class TestPathsTests
{
    /// <summary>
    /// The two shapes such a path takes: a <c>Path.Combine</c> with "apps" (or
    /// "..") followed by the sibling's name as the next argument, and a string
    /// literal holding apps/&lt;sibling&gt;/ or ../&lt;sibling&gt;/. The name must be
    /// the whole segment, so a project or a word that only starts with it does
    /// not match. Case-insensitive, as Windows paths are.
    /// </summary>
    internal static Regex IntoAny(IReadOnlyCollection<string> siblings)
    {
        Assert.NotEmpty(siblings);
        var names = string.Join("|", siblings.Select(Regex.Escape));
        return new Regex(
            $$"""("apps"|"\.\.")\s*,\s*"({{names}})"|"[^"\n]*(apps|\.\.)[/\\]+({{names}})(?=[/\\"])""",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// The folders beside the product root <paramref name="root"/>: in the
    /// monorepo, the other products under apps/; in a copy, whatever its
    /// parent folder holds. Two kinds are left out. A hidden folder is
    /// nobody's product. And a folder named like one of the product's own top
    /// two levels (macos, windows, tests, a project under windows/) cannot be
    /// told apart by the scan: <c>"..", "macos"</c> from a test project
    /// points inside the product, and a sibling of that name would make it an
    /// offender.
    /// </summary>
    internal static string[] Siblings(string root)
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(root) };
        foreach (var top in Directory.EnumerateDirectories(root))
        {
            own.Add(Path.GetFileName(top));
            foreach (var second in Directory.EnumerateDirectories(top))
            {
                own.Add(Path.GetFileName(second));
            }
        }

        return Directory.EnumerateDirectories(Path.GetDirectoryName(root)!)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith('.') && !own.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Every C# source of the test projects under <paramref name="windows"/>, build output left out.</summary>
    internal static List<string> TestSources(string windows) =>
        Directory.EnumerateDirectories(windows)
            .Where(d => Path.GetFileName(d).EndsWith(".Tests", StringComparison.Ordinal)
                        || Path.GetFileName(d) == "TestSupport")
            .SelectMany(d => Directory
                .EnumerateFiles(d, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(d, f)
                    .Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj")))
            .ToList();

    /// <summary>Each line of <paramref name="sources"/> that reaches into a sibling, as file:line: text.</summary>
    internal static List<string> Offenders(string windows, IEnumerable<string> sources, IReadOnlyCollection<string> siblings)
    {
        var reach = IntoAny(siblings);
        return (
            from file in sources
            from line in File.ReadLines(file).Select((text, i) => (text, number: i + 1))
            let trimmed = line.text.TrimStart()
            where !trimmed.StartsWith("//", StringComparison.Ordinal)
                  && !trimmed.StartsWith('*')
                  && reach.IsMatch(line.text)
            select $"{Path.GetRelativePath(windows, file)}:{line.number}: {trimmed}").ToList();
    }

    [Fact]
    public void No_test_source_names_a_path_into_a_folder_beside_this_product()
    {
        var root = TestPaths.AppRoot();
        var windows = Path.Combine(root, "windows");
        var sources = TestSources(windows);

        // A scan that found no files would pass. It must at least see this one
        // and the other test project's sources.
        Assert.Contains(sources, f => Path.GetFileName(f) == "TestPaths.cs");
        Assert.Contains(sources, f => Path.GetFileName(f) == "Win32ReadableClipboardTests.cs");

        // A sparse checkout of apps/dictation, or a copy with nothing beside
        // it, has no folder a test could reach into. That a scan with names
        // finds them is checked on a built layout below, whatever this
        // checkout has beside it.
        var siblings = Siblings(root);
        if (siblings.Length == 0)
        {
            return;
        }

        // And every sibling is one the scan would catch.
        var reach = IntoAny(siblings);
        Assert.All(siblings, name => Assert.Matches(reach, $"Path.Combine(root, \"..\", \"{name}\")"));

        var offenders = Offenders(windows, sources, siblings);
        Assert.True(
            offenders.Count == 0,
            "These test sources reach outside apps/dictation. Commit the fixture under Fixtures/ "
            + "and read it with TestPaths.Fixture, or compare against this product's own file via "
            + "TestPaths.AppRoot:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void A_copy_s_siblings_are_the_folders_beside_it_except_hidden_ones_and_the_product_s_own_names()
    {
        var layout = Directory.CreateTempSubdirectory("dictation-test-paths-");
        try
        {
            var root = Layout(layout.FullName, "other-app", "macos", "Tests", "Dictation.Core.Tests", ".cache");

            Assert.Equal(new[] { "other-app" }, Siblings(root));

            // Nothing beside it: a sparse checkout, or apps/dictation alone.
            Directory.Delete(Path.Combine(Path.GetDirectoryName(root)!, "other-app"));
            Assert.Empty(Siblings(root));
        }
        finally
        {
            layout.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_path_into_the_product_s_own_folder_is_not_one_into_a_sibling_of_that_name()
    {
        var layout = Directory.CreateTempSubdirectory("dictation-test-paths-");
        try
        {
            var root = Layout(layout.FullName, "other-app", "macos");
            var windows = Path.Combine(root, "windows");
            var planted = Path.Combine(windows, "Dictation.Core.Tests", "Planted.cs");
            File.WriteAllLines(planted,
            [
                Decode("var mac = Path.Combine(WindowsRoot(), '..', 'macos', 'Sources');"),
                Decode("var out = Path.Combine(root, '..', 'other-app', 'demo');"),
            ]);

            var offenders = Offenders(windows, TestSources(windows), Siblings(root));

            var offender = Assert.Single(offenders);
            Assert.Equal(
                $"{Path.Combine("Dictation.Core.Tests", "Planted.cs")}:2: "
                + Decode("var out = Path.Combine(root, '..', 'other-app', 'demo');"),
                offender);
        }
        finally
        {
            layout.Delete(recursive: true);
        }
    }

    /// <summary>
    /// <paramref name="parent"/>/apps/dictation with this product's shape
    /// (product.json; macos, tests and windows with one test project), and
    /// <paramref name="beside"/> as folders next to it. Returns the product root.
    /// </summary>
    private static string Layout(string parent, params string[] beside)
    {
        var apps = Path.Combine(parent, "apps");
        var root = Path.Combine(apps, "dictation");
        Directory.CreateDirectory(Path.Combine(root, "windows", "Dictation.Core.Tests"));
        Directory.CreateDirectory(Path.Combine(root, "macos", "Sources"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        File.WriteAllText(Path.Combine(root, "product.json"), "{}");
        foreach (var name in beside)
        {
            Directory.CreateDirectory(Path.Combine(apps, name));
        }

        return root;
    }

    // Samples are written with ' for " and % or ~ for / or \, so no source
    // holds a real path; "other-app" stands in for a sibling's name.
    private static readonly Regex IntoOtherApp = IntoAny(["other-app"]);

    [Theory]
    [InlineData("Path.Combine(dir.FullName, 'apps', 'other-app', 'native', 'transcription_worker')")]
    [InlineData("Path.Combine(root, '..', 'other-app', 'demo')")]
    [InlineData("var script = 'apps%other-app%native%scripts%install.ps1';")]
    [InlineData("var up = @'..~..~Other-App~demo';")]
    public void The_scan_recognises_each_shape_a_path_out_of_the_product_takes(string sample)
    {
        Assert.Matches(IntoOtherApp, Decode(sample));
    }

    [Theory]
    [InlineData("[InlineData('Dictation.Services.csproj')]")]
    [InlineData("Path.Combine('..', 'Dictation.Services', 'Dictation.Services.csproj')")]
    [InlineData("Path.Combine(WindowsRoot(), '..', 'macos', 'Sources')")]
    [InlineData("var said = 'send this to other-app';")]
    [InlineData("var longer = '..%other-apps';")]
    public void The_scan_leaves_this_product_s_own_paths_and_ordinary_words_alone(string sample)
    {
        Assert.DoesNotMatch(IntoOtherApp, Decode(sample));
    }

    private static string Decode(string sample) =>
        sample.Replace('\'', '"').Replace('%', '/').Replace('~', '\\');
}
