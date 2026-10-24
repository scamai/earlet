using Dictation.Services;
using Dictation.Speech;
using System.Xml.Linq;

namespace Dictation.Core.Tests;

// The indicator window's announcements and look: what WPF is told, pinned on
// the window's source because WPF cannot run here, and the arithmetic behind
// it, which can. Pins on the source, not evidence that the window behaves:
// each fails if the thing it names is deleted or reordered.

/// <summary>How the indicator reaches Narrator.</summary>
public sealed class IndicatorAnnouncementTests
{
    private static string Code => File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs")).ReplaceLineEndings("\n");

    private static string Notification => File.ReadAllText(DictationHudGeometryTests.AppFile("UiaNotification.cs")).ReplaceLineEndings("\n");

    /// <summary>
    /// Every announced sentence is also raised as a UI Automation
    /// notification, which a reader speaks wherever its focus is (the Mac's
    /// <c>announcementRequested</c>), behind the live region's own gate: only
    /// a new, non-empty sentence, and never for a pill shown unannounced, so
    /// never while the microphone is open.
    /// </summary>
    [Fact]
    public void Every_announced_sentence_is_also_a_notification_behind_the_same_gate()
    {
        var announce = DictationHudGeometryTests.Member(Code, "private void Announce(");
        var gate = announce.IndexOf("LiveRegionAnnouncement.ShouldRaise(", StringComparison.Ordinal);
        var live = announce.IndexOf("LiveRegion.Raise(StatusLine);", StringComparison.Ordinal);
        var notify = announce.IndexOf("UiaNotification.Raise(StatusLine, sentence);", StringComparison.Ordinal);

        Assert.True(gate >= 0, "the shared rule decides");
        Assert.True(live > gate, "the live region is raised behind it");
        Assert.True(notify > gate, "the notification is raised behind it");
        Assert.Contains("if (announce)", DictationHudGeometryTests.Member(Code, "void IDictationHud.Show("), StringComparison.Ordinal);
    }

    /// <summary>One activity, most recent first, so a newer ending replaces
    /// an older one still waiting to be spoken; asked only when a reader
    /// listens, and never throwing on a Windows without the event.</summary>
    [Fact]
    public void The_notification_is_one_activity_most_recent_first_and_never_throws()
    {
        var raise = DictationHudGeometryTests.Member(Notification, "public static void Raise(UIElement element, string sentence)");

        Assert.Contains("AutomationPeer.ListenerExists(AutomationEvents.Notification)", raise, StringComparison.Ordinal);
        Assert.Contains("AutomationNotificationProcessing.ImportantMostRecent", raise, StringComparison.Ordinal);
        Assert.Contains("sentence, Activity)", raise, StringComparison.Ordinal);
        Assert.Contains("catch (EntryPointNotFoundException)", raise, StringComparison.Ordinal);
        Assert.Contains("catch (ElementNotAvailableException)", raise, StringComparison.Ordinal);
    }
}

/// <summary>The shadow is drawn whole, and an ending's reason is wrapped,
/// not cut.</summary>
public sealed class IndicatorLookTests
{
    private static string Code => File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs")).ReplaceLineEndings("\n");

    private static XDocument Xaml => XDocument.Load(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml"));

    /// <summary>The transparent ring holds the whole shadow on every side:
    /// the blur, plus the depth below. It was 16 DIP under a shadow that
    /// reaches 29.</summary>
    [Fact]
    public void The_ring_around_the_capsule_holds_the_whole_shadow()
    {
        Assert.True(
            DictationHudGeometry.ShadowMargin >= DictationHudGeometry.ShadowBlur + DictationHudGeometry.ShadowDepth,
            $"a {DictationHudGeometry.ShadowMargin} DIP ring cuts a shadow reaching {DictationHudGeometry.ShadowBlur + DictationHudGeometry.ShadowDepth}");
        // The look is unchanged: the blur and depth the window always drew.
        Assert.Equal(24, DictationHudGeometry.ShadowBlur);
        Assert.Equal(5, DictationHudGeometry.ShadowDepth);
    }

    /// <summary>The window draws the shadow with the geometry's lengths, and
    /// the markup names none of its own: one opinion about each value.</summary>
    [Fact]
    public void The_window_draws_the_shadow_the_ring_was_sized_for()
    {
        var shadow = Assert.Single(Xaml.Descendants(), e => e.Name.LocalName == "DropShadowEffect");
        Assert.Null(shadow.Attribute("BlurRadius"));
        Assert.Null(shadow.Attribute("ShadowDepth"));

        var build = DictationHudGeometryTests.Member(Code, "public DictationHudWindow(");
        Assert.Contains("Capsule.Margin = new Thickness(DictationHudGeometry.ShadowMargin);", build, StringComparison.Ordinal);
        Assert.Contains("shadow.BlurRadius = DictationHudGeometry.ShadowBlur;", build, StringComparison.Ordinal);
        Assert.Contains("shadow.ShadowDepth = DictationHudGeometry.ShadowDepth;", build, StringComparison.Ordinal);
    }

    /// <summary>An ending's second line wraps; a busy pill's never does, so
    /// the busy pills keep one width and one height (the hint rules in
    /// <see cref="IndicatorHintTests"/>).</summary>
    [Fact]
    public void An_endings_second_line_wraps_and_a_busy_pills_never_does()
    {
        Assert.Equal(2, DictationHudGeometry.DetailMaxLines);
        foreach (var busy in DictationCopy.HudBusyLines("Right Ctrl", DictationHoldPolicy.Windows))
        {
            Assert.False(DictationHudGeometry.DetailWraps(new DictationHudModel(busy, HudTone.Neutral, null)), busy);
        }

        Assert.True(DictationHudGeometry.DetailWraps(
            new DictationHudModel(DictationCopy.HudNoSound, HudTone.Caution, DictationHudModel.FailureDwell)));
        Assert.True(DictationHudGeometry.DetailWraps(
            new DictationHudModel(DictationCopy.HudCorrectionRemembered, HudTone.Neutral, DictationHudFeed.RememberedDwell)));
    }

    /// <summary>The window wraps by that rule, at a fixed line height, to at
    /// most two lines; the markup's ellipsis is for what two cannot hold.</summary>
    [Fact]
    public void The_window_wraps_an_endings_second_line_to_two_lines_at_a_fixed_height()
    {
        var show = DictationHudGeometryTests.Member(Code, "void IDictationHud.Show(");
        Assert.Contains(
            "DetailLine.TextWrapping = DictationHudGeometry.DetailWraps(model) ? TextWrapping.Wrap : TextWrapping.NoWrap;",
            show, StringComparison.Ordinal);
        var build = DictationHudGeometryTests.Member(Code, "public DictationHudWindow(");
        Assert.Contains("DetailLine.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;", build, StringComparison.Ordinal);
        Assert.Contains("DetailLine.LineHeight = DictationHudGeometry.DetailLineHeight;", build, StringComparison.Ordinal);
        Assert.Contains(
            "DetailLine.MaxHeight = DictationHudGeometry.DetailMaxLines * DictationHudGeometry.DetailLineHeight;",
            build, StringComparison.Ordinal);
        var detail = DictationHudGeometryTests.Named(Xaml, "DetailLine");
        Assert.Equal("CharacterEllipsis", (string?)detail.Attribute("TextTrimming"));
        Assert.Null(detail.Attribute("TextWrapping"));
    }

    /// <summary>
    /// Every line the indicator can show fits a pill at the cap with nothing
    /// cut, in the stand-in face the busy-width rules use: the title on one
    /// line, the second line in at most two, wrapped at word breaks. Real
    /// font widths are not measured here; the arithmetic is.
    /// </summary>
    [Fact]
    public void Every_line_the_indicator_can_show_fits_at_the_cap_without_an_ellipsis()
    {
        var column = DictationHudWidth.Max - DictationHudGeometry.Chrome;
        var texts = DictationHudGeometryTests.IndicatorTexts().Distinct().ToArray();
        Assert.True(texts.Length >= 25, $"only {texts.Length} indicator lines found");

        foreach (var text in texts)
        {
            var lines = DictationHudLines.For(text);
            Assert.True(Title(lines.Title) <= column, $"\"{lines.Title}\" does not fit one line at the cap");
            if (lines.Detail is { } detail)
            {
                var wrapped = Wrapped(detail, column);
                Assert.True(wrapped <= DictationHudGeometry.DetailMaxLines, $"\"{detail}\" takes {wrapped} lines at the cap");
            }
        }
    }

    /// <summary>The wrap the arithmetic above assumes, checked on its own: a
    /// line that does not fit breaks at a word, and only then.</summary>
    [Fact]
    public void The_stand_in_wrap_breaks_at_words()
    {
        Assert.Equal(1, Wrapped("aaa bbb", 1_000));
        Assert.Equal(2, Wrapped("aaa bbb", Detail("aaa ") + 1));
        Assert.Equal(3, Wrapped("aaa bbb ccc", Detail("aaa ") + 1));
    }

    private static double Detail(string text) => text.Sum(IndicatorHintTests.Glyph);

    private static double Title(string text) => 1.2 * Detail(text);

    /// <summary>How many lines <paramref name="text"/> takes in
    /// <paramref name="column"/>, greedily, breaking at spaces.</summary>
    internal static int Wrapped(string text, double column)
    {
        var lines = 1;
        var line = string.Empty;
        foreach (var word in text.Split(' '))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Detail(next) > column)
            {
                lines++;
                line = word;
            }
            else
            {
                line = next;
            }
        }

        return lines;
    }
}

/// <summary>Windows' High Contrast: the pill and the notification-area icon
/// in the contrast theme's own colours, and the designed palette, which is
/// the owner's, untouched outside it.</summary>
public sealed class IndicatorContrastTests
{
    private static string Hud => File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs")).ReplaceLineEndings("\n");

    private static string Contrast => File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.Contrast.cs")).ReplaceLineEndings("\n");

    private static string Tray => File.ReadAllText(DictationHudGeometryTests.AppFile("DictationTray.cs")).ReplaceLineEndings("\n");

    private const uint Text = 0x00FF_FF00;
    private const uint Selection = 0xFF1A_EBFF;
    private const uint Disabled = 0x003F_F23F;

    [Fact]
    public void Outside_High_Contrast_the_designed_palette_stands() => Assert.Null(DictationHudContrast.For(highContrast: false));

    /// <summary>The theme's text on the theme's background, an outline in the
    /// text's colour, a caution dot set apart from the neutral one, and no
    /// shadow: nothing of the designed palette's fixed colours.</summary>
    [Fact]
    public void Under_High_Contrast_the_pill_is_the_themes_text_on_its_background_with_no_shadow()
    {
        var inks = DictationHudContrast.For(highContrast: true);

        Assert.NotNull(inks);
        Assert.Equal(HudSystemColor.Window, inks.Capsule);
        Assert.Equal(HudSystemColor.WindowText, inks.Text);
        Assert.Equal(HudSystemColor.WindowText, inks.Border);
        Assert.NotEqual(inks.NeutralMark, inks.CautionMark);
        Assert.NotEqual(inks.Capsule, inks.CautionMark);
        Assert.NotEqual(inks.Capsule, inks.Level);
        Assert.False(inks.Shadow);
    }

    /// <summary>The designed palette is the owner's decision (violet and
    /// orange, the dark capsule, the violet level gradient): pinned, so High
    /// Contrast work cannot drift it.</summary>
    [Fact]
    public void The_designed_palette_is_unchanged()
    {
        var xaml = XDocument.Load(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml"));
        Assert.Equal("#F21C1C1F", (string?)DictationHudGeometryTests.Named(xaml, "Capsule").Attribute("Background"));
        var stops = xaml.Descendants().Where(e => e.Name.LocalName == "GradientStop").Select(e => (string?)e.Attribute("Color")).ToArray();
        Assert.Equal(["#7A4FD4", "#9F7AEA", "#9925216B", "#997A4FD4"], stops);
        Assert.Contains("NeutralMark = Frozen(Color.FromRgb(0x9F, 0x7A, 0xEA));", Hud, StringComparison.Ordinal);
        Assert.Contains("CautionMark = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));", Hud, StringComparison.Ordinal);

        Assert.Equal(new TrayInks(TrayIconArt.DarkInk, TrayIconArt.RecordingInk, TrayIconArt.DarkInk, TrayIconArt.PausedAlpha), TrayInks.For(lightTaskbar: true));
        Assert.Equal(new TrayInks(TrayIconArt.LightInk, TrayIconArt.RecordingInk, TrayIconArt.LightInk, TrayIconArt.PausedAlpha), TrayInks.For(lightTaskbar: false));
        foreach (var state in Enum.GetValues<TrayIcon>())
        {
            Assert.Equal(TrayIconArt.Render(state, lightTaskbar: true), TrayIconArt.Render(state, TrayInks.For(lightTaskbar: true)));
        }
    }

    /// <summary>Under High Contrast every icon is drawn in the theme's
    /// colours, opaque: its text colour, its selection colour while the
    /// microphone is open, and its disabled-text colour, not faded, while
    /// paused. The shapes are the ones drawn by shape
    /// (<see cref="TrayIconContrastTests"/>).</summary>
    [Theory]
    [InlineData(TrayIcon.Ready, Text)]
    [InlineData(TrayIcon.Transcribing, Text)]
    [InlineData(TrayIcon.MicrophoneBlocked, Text)]
    [InlineData(TrayIcon.Listening, Selection)]
    [InlineData(TrayIcon.Off, Disabled)]
    public void Under_High_Contrast_the_tray_icon_is_the_themes_colour_at_full_strength(TrayIcon state, uint expected)
    {
        var inks = TrayInks.HighContrast(Text, Selection, Disabled);
        var image = DictationDesktopTests.Image(TrayIconArt.Render(state, inks), 3);
        var designed = DictationDesktopTests.Image(TrayIconArt.Render(state, TrayInks.For(lightTaskbar: false) with { ByShape = true }), 3);
        var ink = expected | 0xFF00_0000;

        var inked = 0;
        for (var y = 0; y < image.Size; y++)
        {
            for (var x = 0; x < image.Size; x++)
            {
                var pixel = DictationDesktopTests.Pixel(image, x, y);
                Assert.Equal(DictationDesktopTests.Pixel(designed, x, y).A > 0, pixel.A > 0);
                if (pixel.A == 0)
                {
                    continue;
                }

                inked++;
                Assert.Equal(((int)(ink & 0xFF), (int)((ink >> 8) & 0xFF), (int)((ink >> 16) & 0xFF)), (pixel.B, pixel.G, pixel.R));
            }
        }

        Assert.True(inked > 20, $"{state} drew {inked} pixels");
        Assert.Contains(image.Pixels.Where((_, i) => i % 4 == 3), a => a == 255);
    }

    /// <summary>High Contrast wins over the taskbar's theme, and the system
    /// colours are read only then.</summary>
    [Fact]
    public void The_contrast_theme_wins_over_the_taskbar_and_is_read_only_when_on()
    {
        var reads = 0;
        (uint, uint, uint) System()
        {
            reads++;
            return (Text, Selection, Disabled);
        }

        Assert.Equal(TrayInks.For(lightTaskbar: true), TrayInks.Choose(highContrast: false, lightTaskbar: true, System));
        Assert.Equal(TrayInks.For(lightTaskbar: false), TrayInks.Choose(highContrast: false, lightTaskbar: false, System));
        Assert.Equal(0, reads);

        var expected = TrayInks.HighContrast(Text, Selection, Disabled);
        Assert.Equal(expected, TrayInks.Choose(highContrast: true, lightTaskbar: true, System));
        Assert.Equal(expected, TrayInks.Choose(highContrast: true, lightTaskbar: false, System));
        Assert.Equal(2, reads);
        Assert.Equal(255, expected.PausedAlpha);
    }

    /// <summary>The window, pinned on its source: every show reads the
    /// setting before the dot is painted, a change while it runs is
    /// followed, the parts are resource references to the system brushes,
    /// and the shadow goes.</summary>
    [Fact]
    public void The_pill_follows_High_Contrast_on_every_show_and_when_it_changes()
    {
        var show = DictationHudGeometryTests.Member(Hud, "void IDictationHud.Show(");
        Assert.Contains("PaintFor(model.Tone);", show, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark.Fill =", show, StringComparison.Ordinal);
        var paint = DictationHudGeometryTests.Member(Contrast, "private void PaintFor(");
        var follow = paint.IndexOf("FollowContrast();", StringComparison.Ordinal);
        Assert.True(follow >= 0 && paint.IndexOf("PaintMark();", follow, StringComparison.Ordinal) > follow);

        var apply = DictationHudGeometryTests.Member(Contrast, "private void FollowContrast()");
        // Windows' setting, unless the render mode draws an appearance of its own.
        Assert.Contains("DictationHudContrast.For(HighContrastForRender ?? SystemParameters.HighContrast)", apply, StringComparison.Ordinal);
        Assert.Contains("Paint(Capsule, Border.BackgroundProperty, wanted.Capsule);", apply, StringComparison.Ordinal);
        Assert.Contains("Paint(line, TextBlock.ForegroundProperty, wanted.Text);", apply, StringComparison.Ordinal);
        Assert.Contains("Paint(level, Shape.FillProperty, wanted.Level);", apply, StringComparison.Ordinal);
        Assert.Contains("Capsule.Effect = wanted.Shadow ? designed.Shadow : null;", apply, StringComparison.Ordinal);
        Assert.Contains("Capsule.Effect = designed.Shadow;", apply, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(", DictationHudGeometryTests.Member(Contrast, "private static void Paint("), StringComparison.Ordinal);

        Assert.Contains("WatchContrast();", DictationHudGeometryTests.Member(Hud, "public DictationHudWindow("), StringComparison.Ordinal);
        var watch = DictationHudGeometryTests.Member(Contrast, "private void WatchContrast()");
        Assert.Contains("FollowContrast();", watch, StringComparison.Ordinal);
        Assert.Contains("SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;", watch, StringComparison.Ordinal);
        Assert.Contains("SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;", watch, StringComparison.Ordinal);
        Assert.Contains(
            "e.PropertyName != nameof(SystemParameters.HighContrast)",
            DictationHudGeometryTests.Member(Contrast, "private void OnSystemParameterChanged("), StringComparison.Ordinal);
    }

    /// <summary>The icon, pinned on its source: its inks are chosen with
    /// High Contrast, redrawn when the setting or the theme changes, and its
    /// pictures are kept for the inks in use only (<see cref="TrayPictures{TPicture}"/>).</summary>
    [Fact]
    public void The_tray_follows_High_Contrast_when_it_changes()
    {
        var read = DictationHudGeometryTests.Member(Tray, "private TrayInks ReadInks()");
        Assert.Contains("TrayInks.Choose(", read, StringComparison.Ordinal);
        Assert.Contains("SystemInformation.HighContrast", read, StringComparison.Ordinal);

        var changed = DictationHudGeometryTests.Member(Tray, "private void OnUserPreferenceChanged(");
        Assert.Contains("UserPreferenceCategory.Accessibility", changed, StringComparison.Ordinal);
        Assert.Contains("UserPreferenceCategory.Color", changed, StringComparison.Ordinal);
        Assert.Contains("ReadInks()", changed, StringComparison.Ordinal);
        Assert.Contains("icon.Icon = Picture(showing.Shown.Icon);", changed, StringComparison.Ordinal);
        Assert.Contains("pictures.For(state, inks)", DictationHudGeometryTests.Member(Tray, "private System.Drawing.Icon Picture("), StringComparison.Ordinal);
        Assert.Contains("TrayIconArt.Render(state, inks)", DictationHudGeometryTests.Member(Tray, "private static System.Drawing.Icon Draw("), StringComparison.Ordinal);
        Assert.Contains("TrayPictures<System.Drawing.Icon> pictures = new(Draw);", Tray, StringComparison.Ordinal);
        Assert.Contains("pictures.Dispose();", DictationHudGeometryTests.Member(Tray, "public void Dispose()"), StringComparison.Ordinal);
    }
}

/// <summary>The tray icon's pictures are held for the inks in use only: each
/// switch of theme or contrast colours would otherwise keep up to one icon
/// handle per state until the app quits.</summary>
public sealed class TrayPicturesTests
{
    private sealed class Picture(TrayIcon state, TrayInks inks) : IDisposable
    {
        public TrayIcon State { get; } = state;

        public TrayInks Inks { get; } = inks;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static readonly TrayInks Dark = TrayInks.For(lightTaskbar: false);
    private static readonly TrayInks Light = TrayInks.For(lightTaskbar: true);
    private static readonly TrayInks Contrast = TrayInks.HighContrast(0x00FF_FF00, 0xFF1A_EBFF, 0x003F_F23F);

    [Fact]
    public void A_picture_is_drawn_once_per_state_and_inks()
    {
        var drawn = 0;
        using var pictures = new TrayPictures<Picture>((state, inks) => { drawn++; return new Picture(state, inks); });

        var ready = pictures.For(TrayIcon.Ready, Dark);
        Assert.Same(ready, pictures.For(TrayIcon.Ready, Dark));
        var listening = pictures.For(TrayIcon.Listening, Dark);

        Assert.Equal(2, drawn);
        Assert.Equal(2, pictures.Count);
        Assert.Equal((TrayIcon.Listening, Dark), (listening.State, listening.Inks));
    }

    [Fact]
    public void New_inks_free_every_picture_drawn_with_the_old_ones()
    {
        using var pictures = new TrayPictures<Picture>((state, inks) => new Picture(state, inks));
        var old = new[] { pictures.For(TrayIcon.Ready, Dark), pictures.For(TrayIcon.Listening, Dark), pictures.For(TrayIcon.Off, Dark) };

        var contrast = pictures.For(TrayIcon.Ready, Contrast);

        Assert.All(old, p => Assert.True(p.Disposed));
        Assert.False(contrast.Disposed);
        Assert.Equal(1, pictures.Count);
        Assert.Equal(Contrast, contrast.Inks);

        // Back and forth, as a user trying contrast themes does: still one
        // set held, however many switches.
        for (var i = 0; i < 10; i++)
        {
            foreach (var state in Enum.GetValues<TrayIcon>())
            {
                pictures.For(state, i % 2 == 0 ? Light : Contrast);
            }
        }

        Assert.Equal(Enum.GetValues<TrayIcon>().Length, pictures.Count);
    }

    [Fact]
    public void Disposing_frees_the_set_held()
    {
        var pictures = new TrayPictures<Picture>((state, inks) => new Picture(state, inks));
        var held = new[] { pictures.For(TrayIcon.Ready, Light), pictures.For(TrayIcon.Transcribing, Light) };

        pictures.Dispose();

        Assert.All(held, p => Assert.True(p.Disposed));
        Assert.Equal(0, pictures.Count);
    }
}
