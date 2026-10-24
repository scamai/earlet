namespace Dictation.Core;

/// <summary>
/// A render run's lines (<see cref="DictationRender.ReportFile"/>). Each line
/// goes to stderr and is added to the report beside the images as soon as it
/// is written. A GUI executable started from Explorer shows no stderr, so the
/// report is the only record such a run leaves. A run that stops part-way, or
/// ends with code 2, keeps every line it wrote before it stopped.
/// </summary>
/// <remarks>
/// The render mode's WPF half (<c>Dictation.App/DictationRenderMode.cs</c>)
/// writes through this. It is kept here so the exit code and the report run
/// under test on Linux.
/// </remarks>
public sealed class DictationRenderReport
{
    /// <summary>The tag the run's own failure (exit code 2) is reported under.</summary>
    public const string FailedTag = "render";

    private readonly string _tag;
    private readonly TextWriter _echo;

    /// <summary>A line did not reach the report.</summary>
    private bool _unwritten;

    /// <param name="directory">Where the images and the report go.</param>
    /// <param name="tag">What every line starts with: <c>hud-render</c> or <c>window-render</c>.</param>
    /// <param name="echo">Where each line is also written: stderr when null.</param>
    public DictationRenderReport(string directory, string tag, TextWriter? echo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        _tag = tag;
        _echo = echo ?? Console.Error;
        ReportPath = Path.Combine(directory, DictationRender.ReportFile);
    }

    /// <summary>The report file.</summary>
    public string ReportPath { get; }

    /// <summary>The directories <paramref name="request"/> names: one, or two
    /// when the variables name different ones.</summary>
    public static IReadOnlyList<string> Directories(DictationRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new[] { request.HudDirectory, request.WindowDirectory }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Create <paramref name="request"/>'s directories and remove any report
    /// an earlier run left in them, so a run into a directory used before
    /// replaces that report instead of adding to it.
    /// </summary>
    public static void Fresh(DictationRenderRequest request)
    {
        foreach (var directory in Directories(request))
        {
            Directory.CreateDirectory(directory);
            File.Delete(Path.Combine(directory, DictationRender.ReportFile));
        }
    }

    /// <summary>
    /// The run itself failed, so it exits with code 2. The line goes to stderr
    /// and, if possible, to the report in each of
    /// <paramref name="request"/>'s directories. A directory that cannot take
    /// it is skipped: the run is already failing.
    /// </summary>
    public static void Failed(DictationRenderRequest request, Exception error, TextWriter? echo = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(error);
        var line = $"[{FailedTag}] FAILED: {error.GetType().Name}: {error.Message}";
        (echo ?? Console.Error).WriteLine(line);
        foreach (var directory in Directories(request))
        {
            Append(Path.Combine(directory, DictationRender.ReportFile), line);
        }
    }

    /// <summary>One line, to stderr and to the report.</summary>
    public void Line(string line)
    {
        var tagged = $"[{_tag}] {line}";
        _echo.WriteLine(tagged);
        if (!Append(ReportPath, tagged) && !_unwritten)
        {
            _unwritten = true;
            _echo.WriteLine($"[{_tag}] {ReportPath} not written");
        }
    }

    /// <summary>The check's verdict: each problem, or <paramref name="passed"/>
    /// when there is none.</summary>
    /// <returns>The exit code: 0 when there is no problem and every line
    /// reached the report; 1 otherwise.</returns>
    public int Finish(IReadOnlyList<string> problems, string passed)
    {
        ArgumentNullException.ThrowIfNull(problems);
        foreach (var problem in problems)
        {
            Line($"check FAILED: {problem}");
        }

        if (problems.Count == 0)
        {
            Line($"check passed: {passed}");
        }

        return problems.Count == 0 && !_unwritten ? 0 : 1;
    }

    private static bool Append(string file, string line)
    {
        try
        {
            File.AppendAllLines(file, [line]);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
