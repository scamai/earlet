namespace Dictation.Core.Tests;

/// <summary>
/// The render run's report (<see cref="DictationRenderReport"/>). A run started
/// from Explorer has no stderr, so the report beside the images is all it
/// leaves. It holds every line as soon as the line is written, and the exit
/// code says whether the check passed and the report was written.
/// </summary>
public sealed class DictationRenderReportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dictation-render-report-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] Read(string directory) =>
        File.ReadAllLines(Path.Combine(directory, DictationRender.ReportFile));

    [Fact]
    public void Each_line_is_in_the_report_as_soon_as_it_is_written()
    {
        var directory = Dir("hud");
        var echo = new StringWriter();
        var report = new DictationRenderReport(directory, "hud-render", echo);

        report.Line("a.png written 10x10");

        // Before Finish: a run that stops here still left the line.
        Assert.Equal(["[hud-render] a.png written 10x10"], Read(directory));
        Assert.Equal("[hud-render] a.png written 10x10" + Environment.NewLine, echo.ToString());
        Assert.Equal(Path.Combine(directory, DictationRender.ReportFile), report.ReportPath);
    }

    [Fact]
    public void Finish_returns_0_only_when_there_is_no_problem()
    {
        var passed = Dir("passed");
        Assert.Equal(0, new DictationRenderReport(passed, "hud-render", TextWriter.Null).Finish([], "387 images"));
        Assert.Equal(["[hud-render] check passed: 387 images"], Read(passed));

        var failed = Dir("failed");
        var code = new DictationRenderReport(failed, "window-render", TextWriter.Null)
            .Finish(["paused: TimeoutException", "a.png: not written (IOException)"], "567 images");
        Assert.Equal(1, code);
        Assert.Equal(
            ["[window-render] check FAILED: paused: TimeoutException", "[window-render] check FAILED: a.png: not written (IOException)"],
            Read(failed));
    }

    /// <summary>A report that cannot be written fails the run even when the
    /// check passed: otherwise a run from Explorer would pass and leave
    /// nothing to read.</summary>
    [Fact]
    public void A_report_that_cannot_be_written_fails_the_run_and_says_so_once()
    {
        var directory = Dir("blocked");
        // A folder where the report goes: every write to it fails.
        Directory.CreateDirectory(Path.Combine(directory, DictationRender.ReportFile));
        var echo = new StringWriter();
        var report = new DictationRenderReport(directory, "hud-render", echo);

        report.Line("a.png written 10x10");
        report.Line("b.png written 10x10");

        Assert.Equal(1, report.Finish([], "2 images"));
        var said = echo.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(said, line => line.EndsWith(" not written", StringComparison.Ordinal));
        Assert.Contains("[hud-render] check passed: 2 images", said);
    }

    [Fact]
    public void A_run_replaces_an_earlier_report_and_makes_its_directories()
    {
        var used = Dir("used");
        File.WriteAllText(Path.Combine(used, DictationRender.ReportFile), "an earlier run\n");
        var missing = Path.Combine(_root, "new", "window");

        DictationRenderReport.Fresh(new DictationRenderRequest(used, missing));

        Assert.False(File.Exists(Path.Combine(used, DictationRender.ReportFile)));
        Assert.True(Directory.Exists(missing));
        Assert.Equal([used], DictationRenderReport.Directories(new DictationRenderRequest(used, used)));
        Assert.Equal([missing], DictationRenderReport.Directories(new DictationRenderRequest(null, missing)));
    }

    /// <summary>The run's own failure (exit code 2) goes in the report of each
    /// directory asked for, after the lines already there. A directory that
    /// cannot take it is skipped without a second exception.</summary>
    [Fact]
    public void A_failed_run_leaves_its_line_in_every_directory_asked_for()
    {
        var hud = Dir("hud");
        var window = Dir("window");
        new DictationRenderReport(hud, "hud-render", TextWriter.Null).Line("a.png written 10x10");
        var echo = new StringWriter();

        DictationRenderReport.Failed(new DictationRenderRequest(hud, window), new InvalidOperationException("no composition"), echo);

        const string Line = "[render] FAILED: InvalidOperationException: no composition";
        Assert.Equal(["[hud-render] a.png written 10x10", Line], Read(hud));
        Assert.Equal([Line], Read(window));
        Assert.Equal(Line + Environment.NewLine, echo.ToString());

        var gone = Path.Combine(_root, "never-made", "hud");
        DictationRenderReport.Failed(new DictationRenderRequest(gone, null), new InvalidOperationException("x"), TextWriter.Null);
        Assert.False(Directory.Exists(gone));
    }
}
