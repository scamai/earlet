using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dictation.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.App;

/// <summary>
/// The offscreen render modes, the Mac's <c>DictationHUDRenderMode</c> and
/// <c>DictationWindowRenderMode</c> for Windows: launched with
/// <c>DICTATION_HUD_RENDER_DIR</c> or <c>DICTATION_WINDOW_RENDER_DIR</c> set
/// (<see cref="DictationEnvironment"/>), Dictation draws the REAL indicator or
/// window to PNGs in that directory, with <c>RenderTargetBitmap</c>, and
/// exits. Every build honours them, release included. Never reachable from
/// the UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not the app.</b> A render run opens no log, reads no settings, claims no
/// single instance, so it runs beside a running Dictation and stays out of the
/// user's <c>dictation.log</c>. The window is built over
/// <see cref="DictationRenderComposition"/>'s inert parts in a scratch folder
/// (no microphone, hook, worker process, clipboard, registry or Explorer), and
/// driven into each scenario through its controller's and Vocabulary
/// section's public members (<see cref="DictationWindowRenderScript"/>). The
/// indicator is drawn from <see cref="DictationHudRenderFrames"/>, the
/// feed's own models. Nothing is ever shown: neither window, nor the icon.
/// </para>
/// <para>
/// <b>What it writes.</b> The indicator: every frame at 100, 150 and 200% in
/// light, dark and high contrast (<see cref="DictationRender"/>), each image
/// exactly the window's size, its shadow ring included, on a transparent
/// background. The window: every scenario at 420, 520 and 768 DIPs wide, the
/// whole column, at the same scales and appearances, without its title bar
/// and frame, its disclosures opened as the scenario says; then the
/// notification-area icon's every picture at each of its sizes in each
/// appearance's inks. One line per image, and the check's verdict, go to
/// stderr and to <see cref="DictationRender.ReportFile"/> beside the images
/// as each is written (<see cref="DictationRenderReport"/>), the run's own
/// failure too.
/// </para>
/// <para>
/// <b>The exit code.</b> 0 when every image was written and the indicator's
/// passed <see cref="DictationHudRenderCheck"/>: on the layout, INDICATOR's
/// ring holds the whole shadow and HINTS' busy width is one width for every
/// busy pill, measured in the real fonts at each scale; on the pixels, no
/// shadow cut at the window's edge, every busy pill of a scale and
/// appearance one size, no ending narrower than them. 1 otherwise, or when a
/// window scenario did not reach its state; 2 when the run itself failed. The
/// Mac's window render exits 0 whatever it wrote; this one does not.
/// </para>
/// <para>
/// <b>Appearances are a stand-in.</b> Each one puts its colours in place of
/// the system colours through WPF's <c>SystemColors</c> keys, on the host the
/// surface is drawn in, so a run draws the same on any machine that is not
/// itself in High Contrast (whose control theme WPF picks from the system).
/// High contrast adds WPF's AeroLite control theme, and tells each surface
/// that reads <c>SystemParameters.HighContrast</c> in the app that High
/// Contrast is on (<see cref="DictationRender.HighContrast(DictationRenderAppearance)"/>):
/// the pill and the window's header glyph, in place of the render machine's
/// own setting; the icon takes the same palette's inks. None of this has
/// been compared with what Windows draws.
/// </para>
/// <para>
/// Built on Linux; never run. WPF needs Windows, and no Windows machine has
/// run it yet: it is an open device check.
/// </para>
/// </remarks>
internal static class DictationRenderMode
{
    /// <summary>WPF's control theme for High Contrast.</summary>
    private const string HighContrastTheme = "/PresentationFramework.AeroLite;component/themes/AeroLite.NormalColor.xaml";

    /// <summary>Start the render <paramref name="request"/> asks for on the
    /// dispatcher, and end the process with its exit code when it is done.</summary>
    public static void Start(Application app, DictationRenderRequest request)
    {
        // An exception nothing caught on the dispatcher (a posted callback of
        // a scenario's window, say) ends the run at once with code 2: left to
        // WPF, an unattended render could wait on a crash dialog. Either
        // failure goes in the report too: a run started from Explorer has no
        // stderr to read.
        app.DispatcherUnhandledException += (_, unhandled) =>
        {
            DictationRenderReport.Failed(request, unhandled.Exception);
            Environment.Exit(2);
        };
        app.Dispatcher.InvokeAsync(async () =>
        {
            int code;
            try
            {
                code = await RunAsync(request);
            }
            catch (Exception error)
            {
                DictationRenderReport.Failed(request, error);
                code = 2;
            }

            // Not Shutdown: that would close the render's windows, and a
            // closing Dictation window ends the app its own way.
            Environment.Exit(code);
        });
    }

    private static async Task<int> RunAsync(DictationRenderRequest request)
    {
        DictationRenderReport.Fresh(request);
        var code = 0;
        if (request.HudDirectory is { } hud)
        {
            code = Math.Max(code, RenderIndicator(hud));
        }

        if (request.WindowDirectory is { } window)
        {
            code = Math.Max(code, await RenderWindowAsync(window));
        }

        return code;
    }

    // ─── The indicator ──────────────────────────────────────────────────

    private static int RenderIndicator(string directory)
    {
        var report = new DictationRenderReport(directory, "hud-render");
        // The layout first: INDICATOR's ring holds the whole shadow, by the
        // geometry the window is built from.
        var problems = new List<string>(DictationHudRenderCheck.ShadowProblems());
        var rendered = new List<DictationHudRenderCheck.Rendered>();
        var frames = DictationHudRenderFrames.All(DictationHudRenderFrames.DefaultHold);
        var window = new DictationHudWindow(NullLogger<DictationHudWindow>.Instance, offscreen: true);
        try
        {
            // The capsule and its shadow margin, out of the window: what the
            // window's SizeToContent makes it, drawn on its own.
            var pill = window.Capsule;
            window.Content = null;
            IDictationHud hud = window;
            foreach (var appearance in DictationRender.Appearances)
            {
                var host = new Border { Child = pill, UseLayoutRounding = true };
                try
                {
                    if (!Apply(host, appearance, problems, report))
                    {
                        continue;
                    }

                    // The pill in the theme's colours, or its own, whatever
                    // the render machine's setting.
                    window.HighContrastForRender = DictationRender.HighContrast(appearance);
                    foreach (var percent in DictationRender.ScalePercents)
                    {
                        // Before any frame is shown: the window measures each
                        // line at the DPI of the pill its text is in.
                        AtScale(host, percent);
                        if (appearance == DictationRenderAppearance.Light)
                        {
                            // HINTS' busy width in the real fonts at this
                            // scale; no appearance changes a font.
                            problems.AddRange(DictationHudRenderCheck.BusyWidthProblems(
                                frames, window.TitleWidthForRender, window.DetailWidthForRender, $"{percent}%"));
                        }

                        foreach (var frame in frames)
                        {
                            hud.Show(frame.Model, announce: false);
                            if (frame.Level is { } level)
                            {
                                hud.Level(level);
                            }

                            var name = DictationRender.HudFile(frame.Name, percent, appearance);
                            if (Write(host, width: null, percent, directory, name, problems, report) is { } image)
                            {
                                rendered.Add(new DictationHudRenderCheck.Rendered(
                                    name, DictationRender.Group(percent, appearance), image.PixelWidth, image.PixelHeight,
                                    frame.IsBusy, EdgeAlpha(image)));
                            }
                        }
                    }
                }
                finally
                {
                    host.Child = null;
                }
            }
        }
        finally
        {
            window.Close();
        }

        problems.AddRange(DictationHudRenderCheck.Problems(rendered));
        return report.Finish(problems, $"{rendered.Count} images, the shadow inside its ring, every edge transparent, every busy pill one size per scale and appearance");
    }

    // ─── The window ─────────────────────────────────────────────────────

    private static async Task<int> RenderWindowAsync(string directory)
    {
        var report = new DictationRenderReport(directory, "window-render");
        var problems = new List<string>();
        var written = 0;
        foreach (var scenario in DictationWindowRenderScenarios.All)
        {
            using var parts = DictationRenderComposition.For(scenario);
            MainWindow? window = null;
            try
            {
                window = new MainWindow(NullLoggerFactory.Instance, atLogin: false, DictationWindowParts.ForRender(parts));
                var stage = new DictationRenderStage(
                    window.RenderController,
                    window.RenderVocabulary,
                    parts,
                    // The dispatcher runs the poll, the posted callbacks and
                    // the layout while this waits.
                    () => Task.Delay(10));
                await DictationWindowRenderScript.DriveAsync(scenario, stage);
                window.OpenForRender(DictationWindowRenderScenarios.Opens(scenario));
                var content = window.TakeContentForRender();
                foreach (var appearance in DictationRender.Appearances)
                {
                    var host = MainWindow.RenderHost(content);
                    try
                    {
                        if (!Apply(host, appearance, problems, report))
                        {
                            continue;
                        }

                        window.DrawHighContrastForRender(DictationRender.HighContrast(appearance));
                        foreach (var width in DictationRender.WindowWidths)
                        {
                            foreach (var percent in DictationRender.ScalePercents)
                            {
                                var name = DictationRender.WindowFile(scenario, width, percent, appearance);
                                if (Write(host, width, percent, directory, name, problems, report) is not null)
                                {
                                    written++;
                                }
                            }
                        }
                    }
                    finally
                    {
                        host.Child = null;
                    }
                }

                // Every picture is taken: a held load, transcript or paste
                // finishes before the window lets go.
                await DictationWindowRenderScript.FinishAsync(scenario, stage);
            }
            catch (Exception error)
            {
                // A scenario that failed is a failed run, and the next one
                // still gets its window. The render's words are its own
                // sample, never a user's, so the message is reported whole.
                problems.Add($"{DictationWindowRenderScenarios.Name(scenario)}: {error.GetType().Name}");
                report.Line($"{DictationWindowRenderScenarios.Name(scenario)} FAILED: {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                window?.ReleaseForRender();
            }
        }

        var icons = RenderTray(directory, problems, report);
        return report.Finish(problems, $"{written} window images, every scenario reached, and {icons} icon images");
    }

    // ─── The notification-area icon ─────────────────────────────────────

    /// <summary>
    /// Every picture the icon can show, at each of its sizes, in each
    /// appearance's inks (<see cref="DictationRender.TrayInksFor"/>): the
    /// <c>.ico</c> the app hands Windows (<see cref="TrayIconArt.Render(TrayIcon, TrayInks)"/>),
    /// read back by WPF's decoder, one PNG per size.
    /// </summary>
    /// <returns>How many images were written.</returns>
    private static int RenderTray(string directory, List<string> problems, DictationRenderReport report)
    {
        var written = 0;
        foreach (var appearance in DictationRender.Appearances)
        {
            var inks = DictationRender.TrayInksFor(appearance);
            foreach (var icon in DictationRender.TrayIcons)
            {
                IReadOnlyList<BitmapFrame> frames;
                try
                {
                    using var bytes = new MemoryStream(TrayIconArt.Render(icon, inks));
                    frames = BitmapDecoder.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
                }
                catch (Exception error) when (error is IOException or NotSupportedException or FileFormatException or ArgumentException)
                {
                    problems.Add($"tray-{DictationRender.Kebab(icon)}-{DictationRender.Name(appearance)}: not read ({error.GetType().Name})");
                    continue;
                }

                var sizes = frames.Select(frame => frame.PixelWidth).Order().ToArray();
                if (!sizes.SequenceEqual(TrayIconArt.Sizes))
                {
                    problems.Add($"tray-{DictationRender.Kebab(icon)}-{DictationRender.Name(appearance)}: sizes {string.Join(", ", sizes)}");
                }

                foreach (var frame in frames)
                {
                    var name = DictationRender.TrayFile(icon, frame.PixelWidth, appearance);
                    if (Save(frame, directory, name, problems, report))
                    {
                        written++;
                    }
                }
            }
        }

        return written;
    }

    // ─── Drawing ────────────────────────────────────────────────────────

    /// <summary>Put <paramref name="appearance"/>'s system colours, and for
    /// high contrast WPF's AeroLite theme, in <paramref name="host"/>'s
    /// resources, where everything drawn inside it finds them first.</summary>
    private static bool Apply(FrameworkElement host, DictationRenderAppearance appearance, List<string> problems, DictationRenderReport report)
    {
        var resources = new ResourceDictionary();
        foreach (var (key, rgb) in DictationRender.Palette(appearance))
        {
            var color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var (colorKey, brushKey) = Keys(key);
            resources[colorKey] = color;
            resources[brushKey] = brush;
        }

        if (appearance == DictationRenderAppearance.HighContrast)
        {
            try
            {
                resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(HighContrastTheme, UriKind.Relative) });
            }
            catch (Exception error)
            {
                problems.Add($"{DictationRender.Name(appearance)}: WPF's AeroLite theme did not load ({error.GetType().Name})");
                report.Line($"{DictationRender.Name(appearance)} skipped: {error.Message}");
                return false;
            }
        }

        host.Resources = resources;
        return true;
    }

    private static (ResourceKey Color, ResourceKey Brush) Keys(DictationSystemColor color) => color switch
    {
        DictationSystemColor.Window => (SystemColors.WindowColorKey, SystemColors.WindowBrushKey),
        DictationSystemColor.WindowText => (SystemColors.WindowTextColorKey, SystemColors.WindowTextBrushKey),
        DictationSystemColor.GrayText => (SystemColors.GrayTextColorKey, SystemColors.GrayTextBrushKey),
        DictationSystemColor.Highlight => (SystemColors.HighlightColorKey, SystemColors.HighlightBrushKey),
        DictationSystemColor.HighlightText => (SystemColors.HighlightTextColorKey, SystemColors.HighlightTextBrushKey),
        DictationSystemColor.Control => (SystemColors.ControlColorKey, SystemColors.ControlBrushKey),
        DictationSystemColor.ControlText => (SystemColors.ControlTextColorKey, SystemColors.ControlTextBrushKey),
        DictationSystemColor.ControlLight => (SystemColors.ControlLightColorKey, SystemColors.ControlLightBrushKey),
        DictationSystemColor.ControlDark => (SystemColors.ControlDarkColorKey, SystemColors.ControlDarkBrushKey),
        DictationSystemColor.ActiveBorder => (SystemColors.ActiveBorderColorKey, SystemColors.ActiveBorderBrushKey),
        DictationSystemColor.HotTrack => (SystemColors.HotTrackColorKey, SystemColors.HotTrackBrushKey),
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, null),
    };

    /// <summary>
    /// Give <paramref name="root"/>, which has no window and no parent,
    /// <paramref name="percent"/>'s DPI, so it is laid out, rounded and drawn
    /// on that scale's pixel grid, as a window on a monitor at that scale is.
    /// Without it a host has the render machine's system DPI, and layout
    /// rounding snaps it to that machine's grid: a 1-DIP border lands on half
    /// a pixel at 150% on a 100% machine.
    /// </summary>
    private static void AtScale(FrameworkElement root, int percent)
    {
        var scale = DictationRender.Scale(percent);
        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        // Nothing measured, rounded or drawn at the last DPI is kept.
        Invalidate(root);
    }

    private static void Invalidate(DependencyObject node)
    {
        if (node is UIElement element)
        {
            element.InvalidateMeasure();
            element.InvalidateVisual();
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            Invalidate(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <summary>
    /// Lay <paramref name="root"/> out at <paramref name="percent"/>'s DPI —
    /// at <paramref name="width"/> DIPs and its own height, or at its own
    /// size — draw it at that scale, and write it as <paramref name="name"/>.
    /// </summary>
    /// <returns>The image, or null when it could not be written (a problem,
    /// reported).</returns>
    private static BitmapSource? Write(
        FrameworkElement root, double? width, int percent, string directory, string name, List<string> problems, DictationRenderReport report)
    {
        var path = Path.Combine(directory, name);
        try
        {
            AtScale(root, percent);
            root.Measure(new Size(width ?? double.PositiveInfinity, double.PositiveInfinity));
            var size = new Size(width ?? root.DesiredSize.Width, root.DesiredSize.Height);
            root.Arrange(new Rect(size));
            root.UpdateLayout();

            // Rounded up, never down: a pixel short would cut the shadow's
            // outermost row itself (DictationRender.Pixels).
            var scale = DictationRender.Scale(percent);
            var image = new RenderTargetBitmap(
                DictationRender.Pixels(size.Width, percent), DictationRender.Pixels(size.Height, percent),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(root);
            image.Freeze();
            return Save(image, directory, name, problems, report) ? image : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            problems.Add($"{name}: not written ({error.GetType().Name})");
            report.Line($"{path} FAILED: {error.Message}");
            return null;
        }
    }

    /// <summary>Write <paramref name="image"/> as the PNG <paramref name="name"/>.</summary>
    /// <returns>Whether it was written (a problem, reported, when not).</returns>
    private static bool Save(BitmapSource image, string directory, string name, List<string> problems, DictationRenderReport report)
    {
        var path = Path.Combine(directory, name);
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path))
            {
                encoder.Save(file);
            }

            report.Line($"{path} written {image.PixelWidth}x{image.PixelHeight}");
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            problems.Add($"{name}: not written ({error.GetType().Name})");
            report.Line($"{path} FAILED: {error.Message}");
            return false;
        }
    }

    /// <summary>The largest alpha on <paramref name="image"/>'s outermost rows
    /// and columns (<see cref="DictationHudRenderCheck.EdgeAlpha"/>).</summary>
    private static int EdgeAlpha(BitmapSource image)
    {
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return DictationHudRenderCheck.EdgeAlpha(image.PixelWidth, image.PixelHeight, (x, y) => pixels[(y * stride) + (x * 4) + 3]);
    }
}
