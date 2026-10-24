using Dictation.Services;

namespace Dictation.Core;

/// <summary>A rectangle in physical pixels, laid out like Win32's
/// <c>RECT</c>: the right and bottom edges are exclusive.</summary>
public readonly record struct HudPixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// The floating indicator's two lines: what happened, and then, quieter, what
/// it means or what to do. The macOS indicator has the same two tiers.
/// </summary>
/// <param name="Title">The sentence up to its first dash.</param>
/// <param name="Detail">The rest of the sentence after that dash, starting
/// with a capital letter, or null when the sentence has no dash.</param>
/// <remarks>
/// Every indicator line in <see cref="DictationCopy"/> and
/// <see cref="DictationHudModel"/> is written as "what happened — what to do",
/// for example "Couldn't type here — it's on the clipboard, press Ctrl+V". On
/// one line the longest of them needs the widest pill there is. On two lines
/// the part that matters is first and bold. The split is display only. A
/// screen reader is given the whole sentence, unchanged
/// (<see cref="DictationHudModel.AnnouncedText"/>).
/// </remarks>
public sealed record DictationHudLines(string Title, string? Detail)
{
    /// <summary>The dash the indicator lines use between their two halves.</summary>
    public const string Separator = " — ";

    /// <summary>The two lines for <paramref name="text"/>.</summary>
    public static DictationHudLines For(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var at = text.IndexOf(Separator, StringComparison.Ordinal);
        if (at <= 0)
        {
            return new DictationHudLines(text.Trim(), null);
        }

        var title = text[..at].Trim();
        var detail = text[(at + Separator.Length)..].Trim();
        if (detail.Length == 0)
        {
            return new DictationHudLines(title, null);
        }

        // A second line that starts in lower case reads as a fragment
        // someone forgot to finish.
        return new DictationHudLines(title, char.ToUpperInvariant(detail[0]) + detail[1..]);
    }
}

/// <summary>
/// Where the floating indicator sits and how wide it is: the arithmetic of
/// the WPF window, kept here so a machine with no Windows can check it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bottom-centre of the screen the user is working on.</b> That is where
/// the macOS indicator sits (120 pt above the bottom of the visible frame),
/// and it is where the eye already is while text goes into a document. The
/// window asks Windows which monitor the foreground window is on
/// (<c>MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST)</c>)
/// and passes that monitor's work area and scale to <see cref="Place"/>.
/// </para>
/// <para>
/// <b>Physical pixels, with the scale of the monitor the pill is going to.</b>
/// The app is per-monitor DPI aware. <c>SystemParameters.WorkArea</c> is the
/// primary monitor's work area in the primary monitor's DIPs, so placing from
/// it puts the pill in the wrong place on any second screen with a different
/// scale. Here the work area comes from
/// <c>GetMonitorInfo</c> in physical pixels and every DIP length is scaled by
/// that same monitor's DPI, so no other monitor's factor can get into the sum.
/// </para>
/// <para>
/// <b>One busy width, as on the Mac.</b> Every busy pill (held, hands-free,
/// transcribing, typing) is as wide as the widest line any of them can show
/// for the hold key (<see cref="DictationCopy.HudBusyLines"/>), so the pill
/// does not move or resize from the press to the ending. An outcome starts at
/// that width and takes what its longest line needs, up to the cap, and its
/// second line wraps to two lines there (<see cref="DetailWraps"/>), so its
/// reason is not cut off.
/// </para>
/// </remarks>
public static class DictationHudGeometry
{
    /// <summary>How far the capsule's bottom edge sits above the bottom of
    /// the work area, in DIPs. The macOS indicator's 120 pt.</summary>
    public const double BottomInset = 120;

    /// <summary>The drop shadow's blur radius, in DIPs: how far the shadow
    /// spreads past the capsule's edge.</summary>
    public const double ShadowBlur = 24;

    /// <summary>How far the drop shadow falls below the capsule, in DIPs.</summary>
    public const double ShadowDepth = 5;

    /// <summary>The transparent ring around the capsule that its drop shadow
    /// is drawn into. The window is this much larger than the capsule on every
    /// side: the blur plus the depth, the most the shadow reaches past the
    /// capsule on any side (below it), so the window's edge never cuts it. It
    /// was 16 DIP, under a shadow that reaches 29; the Mac's render check
    /// proves its own edge transparent, and Windows' has not run.</summary>
    public const double ShadowMargin = ShadowBlur + ShadowDepth;

    /// <summary>How many lines an ending's second line may take, at the width
    /// the pill has, before the rest ends in an ellipsis: the Mac wraps an
    /// outcome's lines at its 420 pt cap rather than cutting them.</summary>
    public const int DetailMaxLines = 2;

    /// <summary>The height of one line of the second line, in DIPs: its
    /// 12 DIP text at Segoe UI's own line spacing (about 1.33), rounded up.
    /// Fixed, so <see cref="DetailMaxLines"/> lines are a known height.</summary>
    public const double DetailLineHeight = 16;

    /// <summary>
    /// Whether <paramref name="model"/>'s second line may wrap: an ending's
    /// does, up to <see cref="DetailMaxLines"/> lines, so its "what to do"
    /// half is not cut at the cap; a busy pill's never does, as the Mac's
    /// busy lines are one line each. Its lines are measured to fit the one
    /// busy width, and a wrap would make one busy pill taller than another.
    /// </summary>
    public static bool DetailWraps(DictationHudModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Dwell is not null;
    }

    /// <summary>Space between the capsule's left and right edges and its content.</summary>
    public const double HorizontalPadding = 18;

    /// <summary>Space between the capsule's top and bottom edges and its content.</summary>
    public const double VerticalPadding = 10;

    /// <summary>The tone dot before the text.</summary>
    public const double MarkSize = 8;

    /// <summary>The slot the tone dot and the level bars share, as wide as
    /// the bars, with the dot centred in it: every pill has the same chrome
    /// whichever of the two it shows, as the Mac's icon slot is one width
    /// for every state.</summary>
    public const double MarkSlot = DictationHudWaveform.Width;

    /// <summary>The gap between the slot and the text.</summary>
    public const double MarkGap = 10;

    /// <summary>The capsule's border, on each side.</summary>
    public const double Border = 1;

    /// <summary>Everything in the capsule's width that is not text.</summary>
    public const double Chrome = 2 * (HorizontalPadding + Border) + MarkSlot + MarkGap;

    /// <summary>The scale of a monitor whose effective DPI is
    /// <paramref name="dpi"/>. Zero, which is what a failed
    /// <c>GetDpiForMonitor</c> leaves behind, counts as 100%.</summary>
    public static double Scale(uint dpi) => dpi == 0 ? 1 : dpi / 96.0;

    /// <summary><paramref name="dip"/> in physical pixels at
    /// <paramref name="scale"/>, rounded to the nearest pixel (halves away
    /// from zero).</summary>
    public static int Pixels(double dip, double scale) =>
        (int)Math.Round(dip * Sane(scale), MidpointRounding.AwayFromZero);

    /// <summary>Room left beside the widest busy line, so the window's own
    /// layout never trims a line this arithmetic measured as fitting: the
    /// Mac's two points (<c>DictationHUDMetrics.textColumnWidth</c>).</summary>
    public const double BusySpare = 2;

    /// <summary>
    /// How wide the capsule is for <paramref name="model"/>, in DIPs.
    /// </summary>
    /// <param name="model">What the pill shows.</param>
    /// <param name="widestLine">The widest of its lines as measured
    /// (<see cref="WidestLine"/>), in DIPs.</param>
    /// <remarks>
    /// A model that names the lines it is <see cref="DictationHudModel.SizedFor"/>
    /// (every pill the Dictation app shows) is as wide as the widest of
    /// them and of its own, plus <see cref="BusySpare"/>, between
    /// <see cref="DictationHudWidth.Compact"/> and the cap: a busy pill, whose
    /// own text is one of those lines, is then the one busy width, and an
    /// outcome starts there and grows for its sentence. Without them, a
    /// state that lasts as long as the session state does (no dwell) keeps
    /// <see cref="DictationHudWidth.Compact"/>, and an outcome gets
    /// <see cref="DictationHudWidth.ForSentence"/>.
    /// </remarks>
    public static double PillWidth(DictationHudModel model, double widestLine)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.SizedFor is not null)
        {
            return DictationHudWidth.ForSentence(widestLine + BusySpare, Chrome);
        }

        return model.Dwell is null
            ? DictationHudWidth.Compact
            : DictationHudWidth.ForSentence(widestLine, Chrome);
    }

    /// <summary>
    /// The widest line <paramref name="model"/> must hold, in DIPs: its own
    /// two lines and those of every line it is
    /// <see cref="DictationHudModel.SizedFor"/>, each split into its title
    /// and detail (<see cref="DictationHudLines"/>) and measured in that
    /// line's font. The window measures; this decides what.
    /// </summary>
    /// <param name="model">What the pill shows.</param>
    /// <param name="title">The width of a text in the title's font.</param>
    /// <param name="detail">The width of a text in the detail's font.</param>
    public static double WidestLine(DictationHudModel model, Func<string, double> title, Func<string, double> detail)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(detail);
        var widest = 0.0;
        foreach (var text in model.SizedFor is { } lines ? lines.Prepend(model.Text) : [model.Text])
        {
            var split = DictationHudLines.For(text);
            widest = Math.Max(widest, title(split.Title));
            if (split.Detail is { } second)
            {
                widest = Math.Max(widest, detail(second));
            }
        }

        return widest;
    }

    /// <summary>
    /// The indicator window's rectangle, in physical pixels: centred in
    /// <paramref name="workArea"/>, with the capsule's bottom edge
    /// <see cref="BottomInset"/> above the work area's bottom.
    /// </summary>
    /// <param name="workArea">The work area of the monitor the foreground
    /// window is on, in physical pixels (<c>MONITORINFO.rcWork</c>). It may
    /// start at negative coordinates: a monitor left of or above the primary.</param>
    /// <param name="scale">That monitor's scale (<see cref="Scale"/>).</param>
    /// <param name="pillWidth">The capsule's width in DIPs.</param>
    /// <param name="pillHeight">The capsule's height in DIPs.</param>
    /// <remarks>
    /// The window includes <see cref="ShadowMargin"/> on every side, so its
    /// bottom edge sits that much below the capsule's. A work area too small
    /// for the pill (a tiny or rotated screen) keeps it on screen from the
    /// work area's top-left corner instead of pushing it off the edge.
    /// </remarks>
    public static HudPixelRect Place(HudPixelRect workArea, double scale, double pillWidth, double pillHeight)
    {
        var width = Pixels(pillWidth + 2 * ShadowMargin, scale);
        var height = Pixels(pillHeight + 2 * ShadowMargin, scale);
        var left = Math.Max(workArea.Left, workArea.Left + (workArea.Width - width) / 2);
        var bottom = workArea.Bottom - Pixels(BottomInset - ShadowMargin, scale);
        var top = Math.Max(workArea.Top, bottom - height);
        return new HudPixelRect(left, top, left + width, top + height);
    }

    /// <summary>A scale that is not a positive, finite number is 100%.</summary>
    private static double Sane(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1;
}

/// <summary>
/// The extended window styles that keep the indicator from ever taking the
/// focus, kept here so the set is checked on every platform.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why they matter more than anything else about the window.</b>
/// Dictation pastes into whatever window has the focus. An indicator that
/// took the focus on its way up would become that window, and the user's
/// sentence would be pasted into it instead of their document.
/// </para>
/// <para>
/// <c>WS_EX_NOACTIVATE</c> keeps it from being activated, by showing or by a
/// click. <c>WS_EX_TRANSPARENT</c> on a layered window (WPF makes it layered
/// for <c>AllowsTransparency</c>) passes clicks through to whatever is
/// underneath, so a pill over a button never swallows the click.
/// <c>WS_EX_TOOLWINDOW</c> keeps it out of Alt+Tab and the taskbar, so it
/// cannot be switched to either. The window sets these when its handle is
/// created and again on every show, and logs what it read back.
/// </para>
/// </remarks>
public static class DictationHudWindowStyle
{
    /// <summary><c>WS_EX_TRANSPARENT</c>.</summary>
    public const long Transparent = 0x0000_0020;

    /// <summary><c>WS_EX_TOOLWINDOW</c>.</summary>
    public const long ToolWindow = 0x0000_0080;

    /// <summary><c>WS_EX_NOACTIVATE</c>.</summary>
    public const long NoActivate = 0x0800_0000;

    /// <summary>Every style the indicator must have.</summary>
    public const long Required = NoActivate | Transparent | ToolWindow;

    /// <summary><paramref name="exStyle"/> with the required styles added
    /// and every other bit kept (<c>WS_EX_LAYERED</c> and
    /// <c>WS_EX_TOPMOST</c> are WPF's).</summary>
    public static long Apply(long exStyle) => exStyle | Required;

    /// <summary>Whether <paramref name="exStyle"/> has every required style.</summary>
    public static bool Holds(long exStyle) => (exStyle & Required) == Required;
}

/// <summary>When a polite live region tells a screen reader about new text.</summary>
/// <remarks>
/// WPF announces a <c>LiveSetting</c> element only when the app raises
/// <c>LiveRegionChanged</c> on it. Raised for every render it would repeat a
/// sentence that has not changed (the window re-renders on every state change,
/// the indicator on every show). Raised for an empty string, a reader says
/// "blank". So it is raised only for new, non-empty text.
/// </remarks>
public static class LiveRegionAnnouncement
{
    /// <summary>
    /// How long a sentence the user asked for again, and got back unchanged,
    /// stays out of sight before it returns: long enough to see it go, short
    /// enough not to look like something went wrong.
    /// </summary>
    public static readonly TimeSpan RepeatPause = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether showing <paramref name="next"/> where
    /// <paramref name="previous"/> was is news.</summary>
    public static bool ShouldRaise(string? previous, string? next) =>
        !string.IsNullOrEmpty(next) && !string.Equals(previous, next, StringComparison.Ordinal);

    /// <summary>
    /// Whether the answer to something the user just asked for,
    /// <paramref name="after"/>, is the sentence that was already showing,
    /// <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ShouldRaise"/> stays silent for it and nothing on screen
    /// changes, so a button that asks again (Check again) would seem to do
    /// nothing. For any non-empty answer exactly one of the two is true: it is
    /// either news or a repeat, and the window shows and announces a repeat
    /// again on purpose.
    /// </remarks>
    public static bool IsRepeat(string? before, string? after) =>
        !string.IsNullOrEmpty(after) && string.Equals(before, after, StringComparison.Ordinal);
}
