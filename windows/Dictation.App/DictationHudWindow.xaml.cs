using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Dictation.Core;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.App;

/// <summary>A <see cref="TextBlock"/> that is not in the UI Automation tree.</summary>
/// <remarks>
/// For text a screen reader has already been given another way. WPF has no
/// property for this (<c>AccessibilityView="Raw"</c> is UWP's); returning no
/// peer is WPF's way, and is safe for a leaf with no automation children.
/// </remarks>
internal sealed class UnannouncedTextBlock : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}

/// <summary>Makes a polite live region speak.</summary>
/// <remarks>
/// <c>AutomationProperties.LiveSetting</c> only marks an element as a live
/// region. WPF tells a screen reader about a change only when the app raises
/// <see cref="AutomationEvents.LiveRegionChanged"/> on its peer, so without
/// this call Narrator says nothing. Never throws: an announcement that cannot
/// be made must not take a dictation or a window down with it.
/// </remarks>
internal static class LiveRegion
{
    public static void Raise(UIElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        try
        {
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (ElementNotAvailableException)
        {
            // The element is being torn down; there is nobody left to tell.
        }
    }
}

/// <summary>
/// The floating indicator, as a window. <see cref="DictationHudFeed"/> decides
/// what it says and for how long; this draws it, places it and announces it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It must never take the focus.</b> Dictation pastes into whatever window
/// has the focus, so an indicator that took it would receive the user's
/// sentence. <c>ShowActivated="False"</c> in the markup shows it without
/// activating it, and <see cref="ApplyNoActivateStyles"/> adds
/// <see cref="DictationHudWindowStyle.Required"/> when the handle is made and
/// again on every show, then logs what Windows read back. The handle is made
/// in the constructor, hidden, so the styles are on it before it is ever seen.
/// </para>
/// <para>
/// <b>Placed in physical pixels.</b> The monitor is the one the foreground
/// window is on, from <c>MonitorFromWindow</c>; its work area and scale go to
/// <see cref="DictationHudGeometry.Place"/>, and the result goes straight to
/// <c>SetWindowPos</c>. WPF sizes the window from its content, so a change of
/// size or of monitor scale places it again. A pill coming up from hidden is
/// shown first and placed after, but transparent until it has
/// been laid out and placed.
/// </para>
/// <para>
/// <b>Heard once per sentence.</b> The window is never focused, so it reaches
/// Narrator two ways, raised together and only when the sentence changes
/// (<see cref="LiveRegionAnnouncement"/>): the polite live region on
/// <c>StatusLine</c>, whose name is the whole sentence
/// (<see cref="LiveRegion.Raise"/>), and a UI Automation notification with
/// that sentence (<see cref="UiaNotification.Raise"/>), which a reader speaks
/// wherever its focus is. Whether Narrator then says a sentence once or
/// twice has not been heard on a device. The latch is cleared when the pill
/// goes, so the same ending twice in a row is announced twice. A pill shown
/// without an announcement gets its name with no event raised
/// (<see cref="NameQuietly"/>): the listening pill the state's constant
/// name, "Listening" or "Listening, hands-free", so a reader exploring the
/// pill mid-hold hears the state, never the clock or the last ending; any
/// other (the learned correction while the Dictation window is in front)
/// its whole sentence, as if announced.
/// </para>
/// <para>
/// <b>Level bars while the hold listens.</b> The feed calls
/// <see cref="IDictationHud.Level"/> right after it shows the listening pill
/// and about twelve times a second after that; the bars take the tone dot's
/// place in its slot, each sample eased and glided over one sample
/// (<see cref="DictationHudBars"/>, which holds the level shown; the window
/// only applies its frames), and snap when Windows' animation
/// effects are off (<c>SystemParameters.ClientAreaAnimation</c>). Every show
/// and every hide puts the dot back. The level is never in the name a reader
/// is given. They are the only thing in the window that moves.
/// </para>
/// <para>
/// <b>One width for every busy pill.</b> Every pill the feed shows names the
/// busy lines of the hold (<see cref="DictationHudModel.SizedFor"/>): the
/// window measures each in its line's font and
/// <see cref="DictationHudGeometry"/> makes a busy pill exactly as wide as the
/// widest of them, and an ending at least that wide, as the Mac's capsule. A
/// busy pill with no second line (Typing) keeps that line's room, so the
/// busy pills are one height as well.
/// </para>
/// <para>
/// <b>The live preview's words, only with DICTATION_PREVIEW=1.</b> While the
/// hold listens, a caption the feed puts on the pill
/// (<see cref="DictationHudModel.Caption"/>) is drawn in the title's place,
/// in an element out of the automation tree, cut at its start to the width
/// the pill already has. Never announced, never in a name.
/// </para>
/// <para>
/// <b>High Contrast.</b> While a contrast theme is on
/// (<c>SystemParameters.HighContrast</c>), the pill is drawn in the theme's
/// own colours (<see cref="DictationHudContrast"/>): its text on its
/// background, a hairline in the text's colour, the selection colour for the
/// caution dot and the level bars, and no shadow. Each is a resource
/// reference to the system brush, so a change of contrast theme repaints the
/// pill; turning High Contrast on or off while it runs is followed through
/// WPF's own notice of the change, raised from the same
/// <c>WM_SETTINGCHANGE</c> as <c>UserPreferenceChanged</c>. Off, the designed
/// palette is put back as it was.
/// </para>
/// <para>
/// Not here yet, by decision: dragging, docking and a resting island.
/// </para>
/// </remarks>
public partial class DictationHudWindow : Window, IDictationHud
{
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 0x0000_0002;
    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary><c>HWND_TOPMOST</c>: every placement also puts the pill back
    /// on top of other topmost windows that came up since.</summary>
    private static readonly IntPtr TopmostOrder = new(-1);

    /// <summary>The icon's brightened violet: an ordinary line.</summary>
    private static readonly Brush NeutralMark = Frozen(Color.FromRgb(0x9F, 0x7A, 0xEA));

    /// <summary>Orange: a line that says nothing was typed, or why.</summary>
    private static readonly Brush CautionMark = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    private readonly ILogger<DictationHudWindow> log;

    /// <summary>The capsule's size in DIPs for what is shown now, or null
    /// while nothing is.</summary>
    private Size? pill;

    /// <summary>The sentence a screen reader was last given; cleared with the pill.</summary>
    private string lastAnnounced = string.Empty;

    /// <summary>What the last placement log line said, so a show on the same
    /// monitor with the same styles adds nothing to the log.</summary>
    private string? lastLogged;

    /// <summary>How many times the pill has come up from hidden, so a reveal
    /// meant for an earlier showing does nothing.</summary>
    private int showings;

    private bool closed;

    /// <summary>The bars, left to right, in the order
    /// <see cref="DictationHudWaveform.BarHeights"/> gives their heights.</summary>
    private readonly Rectangle[] bars;

    /// <summary>The level the bars show and how each sample moves them;
    /// reset whenever the dot comes back, so the first level after it is
    /// drawn as it is.</summary>
    private readonly DictationHudBars levelBars = new();

    /// <summary>The indicator render mode's window (<see cref="DictationRenderMode"/>):
    /// a pill is drawn and sized, never shown, placed or announced.</summary>
    private readonly bool offscreen;

    /// <param name="offscreen">True for the indicator render mode.</param>
    public DictationHudWindow(ILogger<DictationHudWindow> log, bool offscreen = false)
    {
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.offscreen = offscreen;
        InitializeComponent();

        Capsule.Margin = new Thickness(DictationHudGeometry.ShadowMargin);
        if (Capsule.Effect is DropShadowEffect shadow)
        {
            // Lengths from the geometry, whose ring is sized to hold them.
            shadow.BlurRadius = DictationHudGeometry.ShadowBlur;
            shadow.ShadowDepth = DictationHudGeometry.ShadowDepth;
        }

        // A fixed line height, so an ending's second line is at most
        // DetailMaxLines lines high and the ellipsis takes the rest.
        DetailLine.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        DetailLine.LineHeight = DictationHudGeometry.DetailLineHeight;
        DetailLine.MaxHeight = DictationHudGeometry.DetailMaxLines * DictationHudGeometry.DetailLineHeight;
        Capsule.BorderThickness = new Thickness(DictationHudGeometry.Border);
        Capsule.Padding = new Thickness(
            DictationHudGeometry.HorizontalPadding,
            DictationHudGeometry.VerticalPadding,
            DictationHudGeometry.HorizontalPadding,
            DictationHudGeometry.VerticalPadding);
        MarkSlot.Width = DictationHudGeometry.MarkSlot;
        MarkSlot.Height = DictationHudWaveform.FrameHeight;
        MarkSlot.Margin = new Thickness(0, 0, DictationHudGeometry.MarkGap, 0);
        Mark.Width = DictationHudGeometry.MarkSize;
        Mark.Height = DictationHudGeometry.MarkSize;
        FlatLine.Width = DictationHudWaveform.Width;
        FlatLine.Height = DictationHudWaveform.FlatLineHeight;
        FlatLine.RadiusX = FlatLine.RadiusY = DictationHudWaveform.FlatLineHeight / 2;
        bars = [Bar0, Bar1, Bar2];
        foreach (var bar in bars)
        {
            bar.Width = DictationHudWaveform.BarWidth;
            bar.Height = DictationHudLevel.MinHeight;
            bar.RadiusX = bar.RadiusY = DictationHudWaveform.BarWidth / 2;
        }

        Bar1.Margin = new Thickness(DictationHudWaveform.BarSpacing, 0, DictationHudWaveform.BarSpacing, 0);

        WatchContrast();

        SizeChanged += (_, _) => PlaceIfShown();
        DpiChanged += (_, _) => PlaceIfShown();
        Closed += (_, _) => closed = true;

        // The handle now, while hidden: OnSourceInitialized runs inside this
        // call, so the styles are set before the first show.
        new WindowInteropHelper(this).EnsureHandle();
    }

    void IDictationHud.Show(DictationHudModel model, bool announce)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (closed)
        {
            return;
        }

        if (!model.IsVisible)
        {
            HidePill();
            return;
        }

        // The dot, whatever was in the slot: the feed draws the bars again
        // after this when the pill is the listening one.
        UseMark();
        var lines = DictationHudLines.For(model.Text);
        StatusLine.Text = lines.Title;
        DetailLine.Text = lines.Detail ?? string.Empty;
        // An ending's second line wraps at the pill's width, as the Mac's
        // outcome does at its cap; a busy pill's stays one line, measured to
        // fit the one busy width, so the busy pills stay one height.
        DetailLine.TextWrapping = DictationHudGeometry.DetailWraps(model) ? TextWrapping.Wrap : TextWrapping.NoWrap;
        // A busy pill with nothing to say under its title (Typing) keeps the
        // second line's room, so every busy pill is one height as well as
        // one width; an ending with one line is one line high.
        DetailLine.Visibility = lines.Detail is not null
            ? Visibility.Visible
            : model.Dwell is null && model.SizedFor is not null ? Visibility.Hidden : Visibility.Collapsed;
        PaintFor(model.Tone);
        if (!announce)
        {
            // Before the show and the placement: a pill that comes up from
            // hidden carries its name already. The state's name on a
            // listening pill; the whole sentence on any other, as Announce
            // names it, never the title alone (DictationHudModel.QuietName).
            NameQuietly(model.QuietName);
        }

        // Its own lines and every busy line of the hold, each in its font:
        // a busy pill keeps one width from the press to the ending, and an
        // ending starts at it (DictationHudGeometry.PillWidth).
        var widest = DictationHudGeometry.WidestLine(
            model, title => TextWidth(StatusLine, title), detail => TextWidth(DetailLine, detail));
        var width = DictationHudGeometry.PillWidth(model, widest);
        Capsule.Width = width;
        // The text column is held to what is left of the capsule, so an
        // ending's second line wraps there, and a line longer than the pill
        // can hold ends in an ellipsis instead of running past the rounded
        // edge.
        Lines.MaxWidth = Math.Max(0, width - DictationHudGeometry.Chrome);
        // The live preview's words over the title, fitted to that column: the
        // caption is not measured into the width, so it never resizes the pill.
        ShowCaption(model.Caption, Lines.MaxWidth);
        Capsule.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var height = Math.Max(0, Capsule.DesiredSize.Height - 2 * DictationHudGeometry.ShadowMargin);
        Capsule.CornerRadius = new CornerRadius(height / 2);
        pill = new Size(width, height);
        if (offscreen)
        {
            // The render mode draws the capsule itself, at every scale.
            return;
        }

        var style = ApplyNoActivateStyles();
        var placed = Place();
        if (!IsVisible)
        {
            // Placed once while hidden, so the window appears on the right
            // monitor, then shown and placed again: a move to a
            // monitor with another scale resizes the window, and PlaceIfShown,
            // which follows that, does nothing while it is hidden. It comes up
            // fully transparent and is revealed only once laid out and placed
            // a last time, so neither the old spot nor the correction is seen.
            Opacity = 0;
            Show();
            placed = Place();
            RevealWhenLaidOut();
        }
        else if (Opacity < 1)
        {
            // Shown, but its reveal never ran: the heartbeat found it
            // transparent and drew it again. Revealed as a new show is.
            RevealWhenLaidOut();
        }

        // After Show: a reader may ignore an event from an element nobody can see.
        // Not while the hold listens: the microphone would hear the reader.
        if (announce)
        {
            Announce(model);
        }
        LogShow(model.Tone, style, placed);
    }

    /// <summary>On screen: the window itself shown and revealed. The heartbeat
    /// reads this from the controller's tick, a Background-priority timer,
    /// so a reveal queued at Loaded priority has always run by then: a pill
    /// still transparent at a tick lost its reveal. Read from the window,
    /// not from <c>pill</c>, which <see cref="HidePill"/> clears before it
    /// hides: a hide that did not take leaves a window still on screen,
    /// which the heartbeat must see as showing to hide it again.</summary>
    bool IDictationHud.IsShowing => !closed && IsVisible && Opacity > 0;

    void IDictationHud.Hide()
    {
        if (!closed)
        {
            HidePill();
        }
    }

    void IDictationHud.Level(float level)
    {
        if (closed || pill is null)
        {
            return;
        }

        var frame = levelBars.Step(level, DictationHudLevelStep.IsReduced(SystemParameters.ClientAreaAnimation));
        Mark.Visibility = Visibility.Collapsed;
        Meter.Visibility = Visibility.Visible;

        // Silence is one flat line; anything audible is the bars. The two
        // cross over the same glide the heights take.
        Glide(FlatLine, OpacityProperty, frame.FlatLineOpacity, frame.Glide);
        Glide(Bars, OpacityProperty, frame.BarsOpacity, frame.Glide);
        for (var i = 0; i < bars.Length; i++)
        {
            Glide(bars[i], HeightProperty, frame.BarHeights[i], frame.Glide);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var style = ApplyNoActivateStyles();
        if (!DictationHudWindowStyle.Holds(style))
        {
            log.LogWarning("The dictation indicator's window styles did not take (0x{Style:X8})", style);
        }
    }

    private void HidePill()
    {
        pill = null;
        UseMark();
        ShowCaption(null, 0);
        // The latch goes with the pill: a second dictation that ends the way
        // the first did must be announced too.
        lastAnnounced = string.Empty;
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// The live preview's caption in the title's place, or the title back.
    /// The words go only into <c>CaptionLine</c>, which is out of the
    /// automation tree; <c>StatusLine</c> stays under it, transparent but in
    /// the tree and in the layout, with the name the pill was given — the
    /// state's, "Listening" — so the words are never in any name or
    /// announcement, and the pill keeps its height. Cut at the start to fit
    /// <paramref name="room"/> (<see cref="DictationHudCaption.HeadTrimmed"/>).
    /// </summary>
    private void ShowCaption(string? caption, double room)
    {
        if (string.IsNullOrEmpty(caption))
        {
            CaptionLine.Text = string.Empty;
            CaptionLine.Visibility = Visibility.Collapsed;
            StatusLine.Opacity = 1;
            return;
        }

        CaptionLine.Text = DictationHudCaption.HeadTrimmed(caption, room, text => TextWidth(CaptionLine, text));
        CaptionLine.Visibility = Visibility.Visible;
        StatusLine.Opacity = 0;
    }

    /// <summary>The tone dot in the slot and the bars out of sight. The bars
    /// keep their last heights; the next listening pill's first level snaps
    /// them to its own.</summary>
    private void UseMark()
    {
        levelBars.Reset();
        Meter.Visibility = Visibility.Collapsed;
        Mark.Visibility = Visibility.Visible;
    }

    /// <summary>Move <paramref name="property"/> to <paramref name="to"/>,
    /// linearly over <paramref name="glide"/>, or at once when it is zero
    /// (any glide still running is dropped first).</summary>
    private static void Glide(UIElement element, DependencyProperty property, double to, TimeSpan glide)
    {
        if (glide <= TimeSpan.Zero)
        {
            element.BeginAnimation(property, null);
            element.SetValue(property, to);
            return;
        }

        element.BeginAnimation(property, new DoubleAnimation(to, new Duration(glide)) { FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>
    /// The one place the indicator's extended styles are set: adds
    /// <see cref="DictationHudWindowStyle.Required"/> to whatever WPF set
    /// (layered, topmost), and returns what Windows reads back.
    /// </summary>
    /// <remarks>
    /// Called when the handle is made and on every show. WPF re-applies parts
    /// of a window's style when some of its properties change; setting the
    /// three again before each show costs two calls and means a style lost
    /// along the way cannot survive to the next hold.
    /// </remarks>
    private long ApplyNoActivateStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var wanted = DictationHudWindowStyle.Apply(current);
        if (wanted != current)
        {
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(wanted));
        }

        return GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
    }

    /// <summary>
    /// Reveal a pill that was just shown transparent, placing it once more
    /// first.
    /// </summary>
    /// <remarks>
    /// Loaded priority runs after WPF's layout pass and after SizeToContent
    /// has resized the window: a size or scale change from the show has landed
    /// by then. A pill
    /// taken down or shown again before this runs is not revealed by it; the
    /// newer show reveals its own.
    /// </remarks>
    private void RevealWhenLaidOut()
    {
        var showing = ++showings;
        Dispatcher.InvokeAsync(
            () =>
            {
                if (closed || showing != showings || !IsVisible)
                {
                    return;
                }

                Place();
                Opacity = 1;
            },
            DispatcherPriority.Loaded);
    }

    private void PlaceIfShown()
    {
        if (!closed && pill is not null && IsVisible)
        {
            Place();
        }
    }

    /// <summary>
    /// Put the pill bottom-centre on the monitor the foreground window is on,
    /// on top of other topmost windows, without activating it.
    /// </summary>
    /// <returns>Where it went and that monitor's scale, or null when Windows
    /// could not say which monitor that is (the pill stays where it was).</returns>
    private (HudPixelRect Window, double Scale)? Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || pill is not { } size)
        {
            return null;
        }

        // The pill is about a sentence going into someone else's window, so
        // it belongs on that window's screen. With no foreground window (the
        // desktop has it, or it is mid-switch), the nearest to this one.
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground == IntPtr.Zero ? hwnd : foreground, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            log.LogDebug("The dictation indicator could not read its monitor; it stays where it was");
            return null;
        }

        var dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX : 0u;
        var scale = DictationHudGeometry.Scale(dpi);
        var work = new HudPixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        var rect = DictationHudGeometry.Place(work, scale, size.Width, size.Height);
        SetWindowPos(hwnd, TopmostOrder, rect.Left, rect.Top, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        return (rect, scale);
    }

    /// <summary>
    /// The name a reader finds on a pill shown without an announcement
    /// (<see cref="DictationHudModel.QuietName"/>), set without raising
    /// <see cref="AutomationEvents.LiveRegionChanged"/>: Narrator is not
    /// asked to speak while the microphone is open, and a reader who reaches
    /// the listening pill hears "Listening" and not the last ending, or the
    /// learned correction's whole line while the window is in front, its
    /// "what to do" half included. Remembered as the
    /// last sentence, as the Mac's announcer remembers a state it did not
    /// speak: the ending after the hold is then news, named and announced
    /// even when it repeats the one before.
    /// </summary>
    private void NameQuietly(string name)
    {
        lastAnnounced = name;
        AutomationProperties.SetName(StatusLine, name);
    }

    private void Announce(DictationHudModel model)
    {
        var sentence = model.AnnouncedText;
        if (!model.ShouldAnnounce || !LiveRegionAnnouncement.ShouldRaise(lastAnnounced, sentence))
        {
            return;
        }

        lastAnnounced = sentence;
        // The whole sentence, not just the bold half on screen: DetailLine is
        // out of the automation tree, so this name is all a reader gets.
        AutomationProperties.SetName(StatusLine, sentence);
        LiveRegion.Raise(StatusLine);
        // And spoken wherever the reader's focus is: the window is never in
        // front, and the Mac's announcement does not depend on focus either.
        UiaNotification.Raise(StatusLine, sentence);
    }

    /// <summary>
    /// One line when the pill shows on a new monitor or with new styles, never
    /// the words: the tone, the window's rectangle and scale, and the extended
    /// styles Windows read back. A style that did not take is a warning,
    /// because it is the one way this window can break a paste.
    /// </summary>
    private void LogShow(HudTone tone, long style, (HudPixelRect Window, double Scale)? placed)
    {
        var where = placed is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"{p.Window.Width}x{p.Window.Height} at {p.Window.Left},{p.Window.Top}, scale {p.Scale:0.##}")
            : "unplaced";
        var line = string.Create(CultureInfo.InvariantCulture, $"{where}, extended style 0x{style:X8}");
        if (!DictationHudWindowStyle.Holds(style))
        {
            log.LogWarning("The dictation indicator is showing without its no-activate styles: {Placement} ({Tone})", line, tone);
        }
        else if (line != lastLogged)
        {
            log.LogInformation("Dictation indicator shown: {Placement} ({Tone})", line, tone);
        }

        lastLogged = line;
    }

    /// <summary>How wide <paramref name="text"/> is in the title's font, in DIPs,
    /// for the render mode's busy-width check (<see cref="DictationHudRenderCheck.BusyWidthProblems"/>).</summary>
    internal double TitleWidthForRender(string text) => TextWidth(StatusLine, text);

    /// <summary>How wide <paramref name="text"/> is in the second line's font, in DIPs,
    /// for the render mode's busy-width check.</summary>
    internal double DetailWidthForRender(string text) => TextWidth(DetailLine, text);

    /// <summary>How wide <paramref name="text"/> is in <paramref name="block"/>'s font, in DIPs,
    /// at the DPI the block is drawn at: this window's in the app, the render host's in the
    /// render mode (<see cref="DictationRenderMode"/>).</summary>
    private double TextWidth(TextBlock block, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ─── Win32 ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLongPtr32(hWnd, nIndex, value);
}
