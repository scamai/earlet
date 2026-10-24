namespace Dictation.Core;

/// <summary>One look the render modes draw a surface in.</summary>
public enum DictationRenderAppearance
{
    /// <summary>Windows' default light colours.</summary>
    Light,

    /// <summary>Windows with dark mode on. Neither the window nor the pill
    /// follows it: the window draws with WPF's classic system colours, which
    /// stay light, and the indicator is always dark. So these images are the
    /// light ones again, which is what a dark-mode user sees; a difference
    /// between the two in a later render is a change worth looking at. The
    /// notification-area icon does follow it, as it follows the taskbar:
    /// its dark images are drawn for a dark taskbar.</summary>
    Dark,

    /// <summary>A High Contrast theme: Windows' High Contrast Black colours in
    /// place of the system colours, with WPF's AeroLite control theme, the
    /// one WPF ships for High Contrast, and every surface told High Contrast
    /// is on (<see cref="DictationRender.HighContrast(DictationRenderAppearance)"/>): the
    /// window's header glyph, the pill and the icon in the theme's colours
    /// and the Mac's three microphone shapes. A stand-in: what Windows itself
    /// draws in a High Contrast theme has never been compared with it.</summary>
    HighContrast,
}

/// <summary>The render modes the environment asks for: a directory for the
/// indicator's PNGs, the window's, or both.</summary>
/// <param name="HudDirectory"><see cref="DictationEnvironment.HudRenderDirectory"/>, or null.</param>
/// <param name="WindowDirectory"><see cref="DictationEnvironment.WindowRenderDirectory"/>, or null.</param>
public sealed record DictationRenderRequest(string? HudDirectory, string? WindowDirectory);

/// <summary>A Win32 system colour a render puts in place of the render
/// host's own, through WPF's <c>SystemColors</c> resource keys.</summary>
public enum DictationSystemColor
{
    Window,
    WindowText,
    GrayText,
    Highlight,
    HighlightText,
    Control,
    ControlText,
    ControlLight,
    ControlDark,
    ActiveBorder,
    HotTrack,
}

/// <summary>
/// What the offscreen render modes draw, and the names they write it under:
/// three scales, three appearances, and the file names of the window's and
/// the indicator's PNGs. The Mac draws its window at 420, 520 and 768 points
/// in light and dark, and its indicator once, at the screen's backing scale
/// (<c>DictationWindowRender.swift</c>, <c>DictationHUD.swift</c>).
/// </summary>
/// <remarks>
/// Pure, so the matrix and the palettes are tested on Linux. The WPF half
/// (<c>Dictation.App/DictationRenderMode.cs</c>) has never run: WPF needs
/// Windows.
/// </remarks>
public static class DictationRender
{
    /// <summary>The scales every image is drawn at, in percent: Windows'
    /// 100, 150 and 200% display scaling.</summary>
    public static IReadOnlyList<int> ScalePercents { get; } = [100, 150, 200];

    /// <summary>Every appearance, in the order the images are written.</summary>
    public static IReadOnlyList<DictationRenderAppearance> Appearances { get; } =
        [DictationRenderAppearance.Light, DictationRenderAppearance.Dark, DictationRenderAppearance.HighContrast];

    /// <summary>The window's content widths, in DIPs: the Mac's 420 (the
    /// declared minimum), 520 (the default) and 768. The Windows window's own
    /// <c>Width</c> and <c>MinWidth</c> count its frame as well, so its client
    /// area at the minimum is a little narrower than 420.</summary>
    public static IReadOnlyList<int> WindowWidths { get; } = [420, 520, 768];

    /// <summary>The run's summary, written beside the images: the same lines
    /// the run writes to stderr, which a GUI executable started from Explorer
    /// shows nowhere.</summary>
    public const string ReportFile = "render-report.txt";

    /// <summary>
    /// The render the environment asks for, or null for none: the app then
    /// starts as itself. A variable set to nothing but spaces is not set. With
    /// both set, both run, the indicator first (the Mac runs only the
    /// indicator's then).
    /// </summary>
    public static DictationRenderRequest? Requested(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var hud = Directory(environment(DictationEnvironment.HudRenderDirectory));
        var window = Directory(environment(DictationEnvironment.WindowRenderDirectory));
        return hud is null && window is null ? null : new DictationRenderRequest(hud, window);

        static string? Directory(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary><paramref name="percent"/> as a factor: 150 is 1.5.</summary>
    public static double Scale(int percent) => percent / 100.0;

    /// <summary>The most a size in pixels may sit above a whole number and
    /// still be read as that number: floating-point error, not a sliver of
    /// the drawing.</summary>
    public const double PixelTolerance = 0.001;

    /// <summary>
    /// The pixels an image of <paramref name="dips"/> needs at
    /// <paramref name="percent"/>. The count is rounded up, never down: a pixel short would
    /// cut the shadow's outermost row. A size within
    /// <see cref="PixelTolerance"/> above a whole number is that number. The
    /// render lays each surface out on the scale's own pixel grid, so its size
    /// is a whole number of pixels up to floating-point error. Rounding that
    /// error up would make the image a pixel larger than the window.
    /// </summary>
    public static int Pixels(double dips, int percent) =>
        (int)Math.Ceiling(Math.Max(0, (dips * Scale(percent)) - PixelTolerance));

    /// <summary>
    /// <paramref name="value"/>'s name as it appears in a file name:
    /// <c>FirstRun</c> is <c>first-run</c>, <c>StillTyping</c> is
    /// <c>still-typing</c>.
    /// </summary>
    public static string Kebab(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.ToString();
        var kebab = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                kebab.Append('-');
            }

            kebab.Append(char.ToLowerInvariant(name[i]));
        }

        return kebab.ToString();
    }

    /// <summary>
    /// Whether <paramref name="appearance"/> is drawn with High Contrast on:
    /// what the render sets on each surface that reads
    /// <c>SystemParameters.HighContrast</c> in the app (the pill, the window's
    /// header glyph) in place of the render machine's own setting, and which
    /// inks the icon is drawn in (<see cref="TrayInksFor"/>).
    /// </summary>
    public static bool HighContrast(DictationRenderAppearance appearance) =>
        appearance == DictationRenderAppearance.HighContrast;

    /// <summary>
    /// The notification-area icon's inks for <paramref name="appearance"/>:
    /// a light taskbar's, a dark taskbar's, or the contrast theme's
    /// (<see cref="TrayInks.HighContrast"/>) from this render's own palette,
    /// so the icon and the window's high-contrast images share one theme.
    /// </summary>
    public static TrayInks TrayInksFor(DictationRenderAppearance appearance)
    {
        var palette = Palette(appearance);
        return TrayInks.Choose(
            HighContrast(appearance),
            lightTaskbar: appearance == DictationRenderAppearance.Light,
            () => (Argb(palette[DictationSystemColor.WindowText]), Argb(palette[DictationSystemColor.Highlight]),
                Argb(palette[DictationSystemColor.GrayText])));

        static uint Argb(int rgb) => 0xFF00_0000 | (uint)rgb;
    }

    /// <summary>Every picture the icon can show, in the order the images are
    /// written.</summary>
    public static IReadOnlyList<TrayIcon> TrayIcons { get; } = Enum.GetValues<TrayIcon>();

    /// <summary><c>tray-&lt;picture&gt;-&lt;pixels&gt;-&lt;appearance&gt;.png</c>:
    /// one of the icon's sizes (<see cref="TrayIconArt.Sizes"/>), each the one
    /// Windows picks at a scale.</summary>
    public static string TrayFile(TrayIcon icon, int pixels, DictationRenderAppearance appearance) =>
        $"tray-{Kebab(icon)}-{pixels}-{Name(appearance)}.png";

    /// <summary>The appearance as it appears in a file name.</summary>
    public static string Name(DictationRenderAppearance appearance) => appearance switch
    {
        DictationRenderAppearance.Light => "light",
        DictationRenderAppearance.Dark => "dark",
        DictationRenderAppearance.HighContrast => "high-contrast",
        _ => throw new ArgumentOutOfRangeException(nameof(appearance), appearance, null),
    };

    /// <summary>The images whose sizes are compared with each other: one
    /// scale and one appearance.</summary>
    public static string Group(int percent, DictationRenderAppearance appearance) =>
        $"{percent}%-{Name(appearance)}";

    /// <summary><c>hud-&lt;frame&gt;-&lt;percent&gt;-&lt;appearance&gt;.png</c>.</summary>
    public static string HudFile(string frame, int percent, DictationRenderAppearance appearance) =>
        $"hud-{frame}-{percent}-{Name(appearance)}.png";

    /// <summary><c>window-&lt;scenario&gt;-&lt;width&gt;-&lt;percent&gt;-&lt;appearance&gt;.png</c>.</summary>
    public static string WindowFile(DictationWindowRenderScenario scenario, int width, int percent, DictationRenderAppearance appearance) =>
        $"window-{DictationWindowRenderScenarios.Name(scenario)}-{width}-{percent}-{Name(appearance)}.png";

    /// <summary>
    /// The system colours <paramref name="appearance"/> puts in place of the
    /// render host's, as 0xRRGGBB, so a run draws the same on any machine.
    /// Light and dark are Windows' default colours (the dark images are the
    /// light ones, see <see cref="DictationRenderAppearance.Dark"/>); high
    /// contrast is Windows' High Contrast Black.
    /// </summary>
    public static IReadOnlyDictionary<DictationSystemColor, int> Palette(DictationRenderAppearance appearance) =>
        appearance switch
        {
            DictationRenderAppearance.Light or DictationRenderAppearance.Dark => WindowsDefault,
            DictationRenderAppearance.HighContrast => HighContrastBlack,
            _ => throw new ArgumentOutOfRangeException(nameof(appearance), appearance, null),
        };

    /// <summary>Windows 10 and 11's default system colours.</summary>
    private static readonly IReadOnlyDictionary<DictationSystemColor, int> WindowsDefault =
        new Dictionary<DictationSystemColor, int>
        {
            [DictationSystemColor.Window] = 0xFFFFFF,
            [DictationSystemColor.WindowText] = 0x000000,
            [DictationSystemColor.GrayText] = 0x6D6D6D,
            [DictationSystemColor.Highlight] = 0x0078D7,
            [DictationSystemColor.HighlightText] = 0xFFFFFF,
            [DictationSystemColor.Control] = 0xF0F0F0,
            [DictationSystemColor.ControlText] = 0x000000,
            [DictationSystemColor.ControlLight] = 0xE3E3E3,
            [DictationSystemColor.ControlDark] = 0xA0A0A0,
            [DictationSystemColor.ActiveBorder] = 0xB4B4B4,
            [DictationSystemColor.HotTrack] = 0x0066CC,
        };

    /// <summary>Windows' High Contrast Black: white text on black, green
    /// disabled text, cyan selection, yellow links; borders in the text colour.</summary>
    private static readonly IReadOnlyDictionary<DictationSystemColor, int> HighContrastBlack =
        new Dictionary<DictationSystemColor, int>
        {
            [DictationSystemColor.Window] = 0x000000,
            [DictationSystemColor.WindowText] = 0xFFFFFF,
            [DictationSystemColor.GrayText] = 0x3FF23F,
            [DictationSystemColor.Highlight] = 0x1AEBFF,
            [DictationSystemColor.HighlightText] = 0x000000,
            [DictationSystemColor.Control] = 0x000000,
            [DictationSystemColor.ControlText] = 0xFFFFFF,
            [DictationSystemColor.ControlLight] = 0xFFFFFF,
            [DictationSystemColor.ControlDark] = 0xFFFFFF,
            [DictationSystemColor.ActiveBorder] = 0xFFFFFF,
            [DictationSystemColor.HotTrack] = 0xFFFF00,
        };
}
