using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The render modes' WPF half (<c>Dictation.App/DictationRenderMode.cs</c>),
/// pinned on its source: WPF cannot run here, so these say only that the
/// source is wired as described, never that a picture comes out right. Each
/// fails if the thing it names is removed. What runs on Linux is in
/// <see cref="DictationWindowRenderTests"/>, <see cref="DictationHudRenderFramesTests"/>
/// and <see cref="DictationHudRenderCheckTests"/>.
/// </summary>
public sealed class DictationRenderModeSurfaceTests
{
    private static string Code(string file) => File.ReadAllText(AppFile(file)).ReplaceLineEndings("\n");

    private static void InOrder(string code, params string[] lines)
    {
        var at = 0;
        foreach (var line in lines)
        {
            var found = code.IndexOf(line, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"not found after the line before it: {line}");
            at = found + line.Length;
        }
    }

    // ─── Starting a render ──────────────────────────────────────────────

    [Fact]
    public void A_render_starts_before_the_log_and_the_single_instance_claim_and_never_reaches_the_app()
    {
        var app = Code("App.xaml.cs");
        var asked = app.IndexOf("if (DictationRender.Requested(DictationEnvironment.Process) is { } render)", StringComparison.Ordinal);

        Assert.True(asked > 0, "the app never asks for a render");
        Assert.True(asked < app.IndexOf("FileLoggerFactory.TryCreate(", StringComparison.Ordinal), "the log opens first");
        Assert.True(asked < app.IndexOf("instance.Claim()", StringComparison.Ordinal), "the single instance is claimed first");
        InOrder(app[asked..], "DictationRenderMode.Start(this, render);", "return;", "FileLoggerFactory.TryCreate(");
    }

    /// <summary>The run ends the process itself with its code: Shutdown would
    /// close the render's windows, and a closing Dictation window ends the
    /// app its own way, with code 0. An exception nothing caught ends it with
    /// code 2 rather than a crash dialog an unattended run would wait on, and
    /// leaves its line in the report. Each half's verdict
    /// (<see cref="DictationRenderReport.Finish"/>, tested in
    /// <see cref="DictationRenderReportTests"/>) reaches that code: neither
    /// half's is dropped, and the worse one wins.</summary>
    [Fact]
    public void A_render_ends_the_process_with_its_own_exit_code()
    {
        var mode = Code("DictationRenderMode.cs");
        var start = Member(mode, "public static void Start(");

        InOrder(
            start,
            "app.DispatcherUnhandledException += ",
            "DictationRenderReport.Failed(request, unhandled.Exception);",
            "Environment.Exit(2);",
            "app.Dispatcher.InvokeAsync(",
            "code = await RunAsync(request);",
            "DictationRenderReport.Failed(request, error);",
            "code = 2;",
            "Environment.Exit(code);");
        Assert.DoesNotContain("Shutdown(", mode, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Error", mode, StringComparison.Ordinal);

        InOrder(
            Member(mode, "private static async Task<int> RunAsync("),
            "DictationRenderReport.Fresh(request);",
            "var code = 0;",
            "code = Math.Max(code, RenderIndicator(hud));",
            "code = Math.Max(code, await RenderWindowAsync(window));",
            "return code;");
        foreach (var half in new[] { "private static int RenderIndicator(", "private static async Task<int> RenderWindowAsync(" })
        {
            var body = Member(mode, half);
            Assert.Contains("var report = new DictationRenderReport(directory, ", body, StringComparison.Ordinal);
            Assert.Contains("return report.Finish(problems, ", body, StringComparison.Ordinal);
        }
    }

    // ─── Never the user's parts ─────────────────────────────────────────

    /// <summary>The render reads and writes none of the user's files and
    /// opens none of Windows' devices: it builds every window over the inert
    /// composition, with no indicator and no icon.</summary>
    [Fact]
    public void The_window_render_builds_every_window_over_the_inert_parts()
    {
        var mode = Code("DictationRenderMode.cs");
        foreach (var usersOwn in new[]
        {
            "WindowsDictationPlatform", "DictationPaths.ForCurrentUser", "DictationLearning.ForUser", "FileLoggerFactory",
            "ForUser(", "DictationSettingsStore(",
        })
        {
            Assert.DoesNotContain(usersOwn, mode, StringComparison.Ordinal);
        }

        var window = Member(mode, "private static async Task<int> RenderWindowAsync(");
        InOrder(
            window,
            "using var parts = DictationRenderComposition.For(scenario);",
            "new MainWindow(NullLoggerFactory.Instance, atLogin: false, DictationWindowParts.ForRender(parts))",
            "var stage = new DictationRenderStage(",
            "await DictationWindowRenderScript.DriveAsync(scenario, stage);",
            "window.OpenForRender(DictationWindowRenderScenarios.Opens(scenario));",
            "window.TakeContentForRender()",
            "foreach (var appearance in DictationRender.Appearances)",
            "window.DrawHighContrastForRender(DictationRender.HighContrast(appearance));",
            "foreach (var width in DictationRender.WindowWidths)",
            // Every picture taken: a held load, transcript or paste finishes
            // before the window lets go.
            "await DictationWindowRenderScript.FinishAsync(scenario, stage);",
            "window?.ReleaseForRender();",
            "RenderTray(directory, problems, report);");

        var parts = Code("DictationWindowParts.cs");
        var render = Member(parts, "internal static DictationWindowParts ForRender(");
        foreach (var line in new[] { "parts.Platform,", "parts.Settings,", "parts.Learning,", "parts.AppDirectory,", "parts.Options,", "parts.Relay,", "Indicator: false,", "TrayIcon: false)" })
        {
            Assert.Contains(line, render, StringComparison.Ordinal);
        }

        var user = Member(parts, "internal static DictationWindowParts ForUser(");
        foreach (var line in new[] { "new WindowsDictationPlatform(logs),", "DictationLearning.ForUser(paths, logs),", "Indicator: true,", "TrayIcon: true);" })
        {
            Assert.Contains(line, user, StringComparison.Ordinal);
        }
    }

    /// <summary>The window is the app's own composition over whichever parts
    /// it is given, the app's when none are.</summary>
    [Fact]
    public void The_window_builds_its_controller_and_section_from_its_parts()
    {
        var constructor = Member(Code("MainWindow.xaml.cs"), "public MainWindow(");

        InOrder(
            constructor,
            "parts ??= DictationWindowParts.ForUser(logs);",
            "tray = new DictationTray(logs.CreateLogger<DictationTray>(), parts.TrayIcon);",
            "if (parts.Indicator)",
            "hud = new DictationHudWindow(",
            "controller = new DictationController(\n                parts.Platform,",
            "parts.Settings,\n                parts.Environment,\n                parts.AppDirectory,\n                parts.Options,",
            "parts.Corrections(controller.Corrections),");
        Assert.DoesNotContain("new WindowsDictationPlatform(", constructor, StringComparison.Ordinal);
        Assert.DoesNotContain("DictationPaths.ForCurrentUser()", constructor, StringComparison.Ordinal);
        Assert.Contains("Visible = visible,", Member(Code("DictationTray.cs"), "public DictationTray("), StringComparison.Ordinal);
    }

    /// <summary>The render's window takes the content out only once the
    /// scenario is driven, stops the poll so nothing changes under the
    /// pictures, and lets go without closing.</summary>
    [Fact]
    public void The_render_window_stops_its_poll_before_its_content_is_drawn_and_lets_go_without_closing()
    {
        var render = Code("MainWindow.Render.cs");

        InOrder(Member(render, "internal UIElement TakeContentForRender()"), "poll.Stop();", "Content = null;");
        Assert.Contains("=> LetGo()", Member(render, "internal void ReleaseForRender()"), StringComparison.Ordinal);
        Assert.DoesNotContain("Close()", render, StringComparison.Ordinal);
        var host = Member(render, "internal static Border RenderHost(");
        Assert.Contains("SystemColors.WindowBrushKey", host, StringComparison.Ordinal);
        Assert.Contains("SystemColors.WindowTextBrushKey", host, StringComparison.Ordinal);
    }

    // ─── The indicator ──────────────────────────────────────────────────

    /// <summary>An offscreen indicator draws and sizes its pill like the
    /// app's, then stops: it is never shown, placed, styled or announced.</summary>
    [Fact]
    public void The_offscreen_indicator_is_sized_like_the_apps_and_never_shown_placed_or_announced()
    {
        var window = Code("DictationHudWindow.xaml.cs");
        var show = Member(window, "void IDictationHud.Show(");
        var sized = show.IndexOf("pill = new Size(width, height);", StringComparison.Ordinal);
        var stop = show.IndexOf("if (offscreen)", StringComparison.Ordinal);

        Assert.True(sized >= 0 && stop > sized, "the pill is sized before an offscreen show stops");
        Assert.True(show.IndexOf("return;", stop, StringComparison.Ordinal) > stop);
        foreach (var later in new[] { "ApplyNoActivateStyles()", "Place()", "Show();", "Announce(model);" })
        {
            Assert.True(show.IndexOf(later, StringComparison.Ordinal) > stop, $"{later} runs before an offscreen show stops");
        }

        Assert.Contains("this.offscreen = offscreen;", Member(window, "public DictationHudWindow("), StringComparison.Ordinal);
    }

    /// <summary>Every frame is drawn through the real window and checked
    /// twice: on the layout (INDICATOR's shadow ring before anything is
    /// drawn; HINTS' busy width in the window's own fonts at each scale) and
    /// on the pixels.</summary>
    [Fact]
    public void Every_indicator_frame_is_drawn_through_the_window_and_checked()
    {
        var indicator = Member(Code("DictationRenderMode.cs"), "private static int RenderIndicator(");

        InOrder(
            indicator,
            "var problems = new List<string>(DictationHudRenderCheck.ShadowProblems());",
            "DictationHudRenderFrames.All(DictationHudRenderFrames.DefaultHold)",
            "new DictationHudWindow(NullLogger<DictationHudWindow>.Instance, offscreen: true)",
            "var pill = window.Capsule;",
            "window.Content = null;",
            "foreach (var appearance in DictationRender.Appearances)",
            "window.HighContrastForRender = DictationRender.HighContrast(appearance);",
            "foreach (var percent in DictationRender.ScalePercents)",
            "AtScale(host, percent);",
            "problems.AddRange(DictationHudRenderCheck.BusyWidthProblems(",
            "frames, window.TitleWidthForRender, window.DetailWidthForRender, $\"{percent}%\"));",
            "foreach (var frame in frames)",
            "hud.Show(frame.Model, announce: false);",
            "hud.Level(level);",
            "frame.IsBusy, EdgeAlpha(image)",
            "problems.AddRange(DictationHudRenderCheck.Problems(rendered));");

        // The busy width is measured as the window measures the pill it sizes.
        var hud = Code("DictationHudWindow.xaml.cs");
        Assert.Contains("internal double TitleWidthForRender(string text) => TextWidth(StatusLine, text);", hud, StringComparison.Ordinal);
        Assert.Contains("internal double DetailWidthForRender(string text) => TextWidth(DetailLine, text);", hud, StringComparison.Ordinal);
        Assert.Contains("title => TextWidth(StatusLine, title), detail => TextWidth(DetailLine, detail)", hud, StringComparison.Ordinal);
    }

    /// <summary>The icon is drawn by the code the tray draws it with, in each
    /// appearance's inks, every picture and every size it carries.</summary>
    [Fact]
    public void The_icon_is_drawn_as_the_tray_draws_it_in_each_appearances_inks()
    {
        var tray = Member(Code("DictationRenderMode.cs"), "private static int RenderTray(");

        InOrder(
            tray,
            "foreach (var appearance in DictationRender.Appearances)",
            "var inks = DictationRender.TrayInksFor(appearance);",
            "foreach (var icon in DictationRender.TrayIcons)",
            "TrayIconArt.Render(icon, inks)",
            "if (!sizes.SequenceEqual(TrayIconArt.Sizes))",
            "DictationRender.TrayFile(icon, frame.PixelWidth, appearance);");
        Assert.Contains("TrayIconArt.Render(state, inks)", Code("DictationTray.cs"), StringComparison.Ordinal);
    }

    /// <summary>Each disclosure a scenario opens is the window's own
    /// section, set from the scenario's list before the content is taken.</summary>
    [Fact]
    public void Each_disclosure_opens_its_own_section()
    {
        var open = Member(Code("MainWindow.Render.cs"), "internal void OpenForRender(");
        var sections = new Dictionary<DictationWindowDisclosure, string>
        {
            [DictationWindowDisclosure.RecentText] = "RecentTextExpander",
            [DictationWindowDisclosure.Troubleshooting] = "TroubleshootingSection",
        };

        Assert.Equal(Enum.GetValues<DictationWindowDisclosure>().Order(), sections.Keys.Order());
        foreach (var (disclosure, section) in sections)
        {
            Assert.Contains(
                $"{section}.IsExpanded = open.Contains(DictationWindowDisclosure.{disclosure});", open, StringComparison.Ordinal);
            Assert.Contains($"<Expander x:Name=\"{section}\"", Code("MainWindow.xaml"), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The high-contrast images stand for High Contrast only while every
    /// surface they draw takes it from what the render sets: the
    /// <c>SystemColors</c> keys, the AeroLite theme, and, for a surface that
    /// reads <c>SystemParameters.HighContrast</c> in the app, the render's own
    /// High Contrast for the appearance in its place. A read of the setting
    /// with no render input before it follows the render machine (VERIFY asks
    /// for one not in High Contrast) and draws the ordinary look under a
    /// high-contrast name: it fails here (mutation: drop the
    /// <c>ForRender ??</c> from either read). The pill and the header glyph
    /// read it today, and the render sets both for each appearance.
    /// </summary>
    [Fact]
    public void Every_high_contrast_read_in_a_drawn_surface_is_one_the_render_sets()
    {
        var drawn = Directory.GetFiles(Path.GetDirectoryName(AppFile("MainWindow.xaml"))!)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.StartsWith("DictationHudWindow.", StringComparison.Ordinal)
                || (name.StartsWith("MainWindow", StringComparison.Ordinal) && name is not "MainWindow.Tray.cs" and not "MainWindow.Render.cs"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("DictationHudWindow.Contrast.cs", drawn);
        Assert.Contains("MainWindow.Header.cs", drawn);
        const string Read = "SystemParameters.HighContrast";
        var reads = 0;
        foreach (var file in drawn)
        {
            var code = Code(file);
            for (var at = code.IndexOf(Read, StringComparison.Ordinal); at >= 0; at = code.IndexOf(Read, at + Read.Length, StringComparison.Ordinal))
            {
                if (code[..at].EndsWith("nameof(", StringComparison.Ordinal) || code[..at].EndsWith("<c>", StringComparison.Ordinal))
                {
                    continue;
                }

                reads++;
                Assert.True(
                    code[..at].EndsWith("ForRender ?? ", StringComparison.Ordinal),
                    $"{file} reads {Read} with no render input before it, so its high-contrast images follow the render machine");
            }
        }

        Assert.True(reads >= 2, $"only {reads} reads of {Read} found");
        Assert.Contains(
            "DictationHudContrast.For(HighContrastForRender ?? SystemParameters.HighContrast)",
            Code("DictationHudWindow.Contrast.cs"), StringComparison.Ordinal);
        Assert.Contains("var highContrast = highContrastForRender ?? SystemParameters.HighContrast;", Code("MainWindow.Header.cs"), StringComparison.Ordinal);
        InOrder(
            Member(Code("MainWindow.Render.cs"), "internal void DrawHighContrastForRender("),
            "highContrastForRender = on;",
            "RenderGlyph(shown.Glyph);");

        // Only the render sets either input: in the app both stay null, and
        // the surfaces follow Windows.
        var app = Path.GetDirectoryName(AppFile("MainWindow.xaml"))!;
        foreach (var file in Directory.GetFiles(app, "*.cs").Select(Path.GetFileName).OfType<string>())
        {
            var code = Code(file);
            var sets = code.Contains("HighContrastForRender = ", StringComparison.Ordinal)
                || code.Contains("highContrastForRender = ", StringComparison.Ordinal);
            Assert.True(!sets || file is "DictationRenderMode.cs" or "MainWindow.Render.cs", $"{file} sets a render's High Contrast");
        }
    }

    // ─── Drawing ────────────────────────────────────────────────────────

    /// <summary>Every system colour an appearance sets reaches WPF's own key
    /// for it; each surface is laid out at the DPI of the scale it is drawn
    /// at, not the render machine's; and the bitmap is the layout's whole
    /// pixels (<see cref="DictationRender.Pixels"/>, tested in
    /// <see cref="DictationHudRenderFramesTests"/>).</summary>
    [Fact]
    public void Each_appearance_sets_wpfs_system_colour_keys_and_each_image_is_laid_out_at_its_scale()
    {
        var mode = Code("DictationRenderMode.cs");
        var keys = Member(mode, "private static (ResourceKey Color, ResourceKey Brush) Keys(");
        foreach (var color in Enum.GetNames<DictationSystemColor>())
        {
            Assert.Contains(
                $"DictationSystemColor.{color} => (SystemColors.{color}ColorKey, SystemColors.{color}BrushKey),",
                keys, StringComparison.Ordinal);
        }

        var apply = Member(mode, "private static bool Apply(");
        InOrder(apply, "DictationRender.Palette(appearance)", "resources[colorKey] = color;", "resources[brushKey] = brush;", "host.Resources = resources;");

        var write = Member(mode, "private static BitmapSource? Write(");
        InOrder(
            write,
            "AtScale(root, percent);",
            "root.Measure(",
            "DictationRender.Pixels(size.Width, percent), DictationRender.Pixels(size.Height, percent),",
            "96 * scale, 96 * scale, PixelFormats.Pbgra32");
        Assert.Contains("pixels[(y * stride) + (x * 4) + 3]", Member(mode, "private static int EdgeAlpha("), StringComparison.Ordinal);

        var atScale = Member(mode, "private static void AtScale(");
        InOrder(atScale, "VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));", "Invalidate(root);");
        InOrder(
            Member(mode, "private static void Invalidate("),
            "element.InvalidateMeasure();",
            "element.InvalidateVisual();",
            "Invalidate(VisualTreeHelper.GetChild(node, i));");

        // The pill's lines are measured at the DPI they are drawn at.
        var hud = Code("DictationHudWindow.xaml.cs");
        Assert.Contains("VisualTreeHelper.GetDpi(block).PixelsPerDip", Member(hud, "private double TextWidth("), StringComparison.Ordinal);
        Assert.DoesNotContain("GetDpi(this)", hud, StringComparison.Ordinal);
    }
}
