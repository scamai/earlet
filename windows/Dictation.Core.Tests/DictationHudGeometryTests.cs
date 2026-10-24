using System.Reflection;
using System.Xml.Linq;
using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The floating indicator's arithmetic: where the pill goes on the monitor
/// the user is working on, how wide it is, how its sentence splits into two
/// lines, the window styles that keep it from taking the focus, and when a
/// live region speaks.
/// </summary>
/// <remarks>
/// No window is built here and none can be: the WPF window is Windows-only
/// and there is no Windows machine to show it on. These are the numbers and
/// rules the window is handed. That it puts them on screen, never takes the
/// focus and is heard by Narrator is unverified until a device runs it.
/// </remarks>
public sealed class DictationHudGeometryTests
{
    // ─── Placement ──────────────────────────────────────────────────────

    /// <summary>
    /// A 1920 × 1080 (in DIPs) screen with a 48-DIP taskbar at the bottom, at
    /// 100, 150 and 200 %, so its physical work area grows with the scale. The
    /// expected numbers are written out: the compact 240-DIP capsule plus its
    /// 29-DIP shadow ring on each side (a 24-DIP blur and 5 DIP of depth), and
    /// its bottom edge 120 DIPs above the work area, each times the scale.
    /// </summary>
    [Theory]
    [InlineData(96u, 1920, 1032, 298, 98, 120)]
    [InlineData(144u, 2880, 1548, 447, 147, 180)]
    [InlineData(192u, 3840, 2064, 596, 196, 240)]
    public void On_one_monitor_the_pill_is_centred_with_the_capsule_the_inset_above_the_work_area(
        uint dpi, int workRight, int workBottom, int width, int height, int inset)
    {
        var scale = DictationHudGeometry.Scale(dpi);
        var work = new HudPixelRect(0, 0, workRight, workBottom);

        var window = DictationHudGeometry.Place(work, scale, DictationHudWidth.Compact, 40);

        Assert.Equal(width, window.Width);
        Assert.Equal(height, window.Height);
        Assert.Equal((workRight - width) / 2, window.Left);
        // An odd width (447 at 150 %) cannot be centred to the pixel: the
        // spare pixel goes to the right.
        Assert.InRange((work.Right - window.Right) - (window.Left - work.Left), 0, 1);

        var shadow = (int)(DictationHudGeometry.ShadowMargin * scale);
        Assert.Equal(inset, work.Bottom - (window.Bottom - shadow));
        Assert.True(window.Top > work.Top);
    }

    /// <summary>
    /// The monitor is the one the foreground window is on, which the window
    /// asks Windows for. What is checked here is what happens with that
    /// monitor's numbers: the pill lands inside its work area, at its scale,
    /// and nothing of the primary monitor's leaks in, which is the mistake
    /// placing from <c>SystemParameters.WorkArea</c> makes.
    /// </summary>
    [Fact]
    public void On_a_second_monitor_the_pill_lands_in_that_monitors_work_area_at_its_own_scale()
    {
        // The primary is 1920 × 1080 at 100 % with its origin at 0,0. The
        // second screen is a 2560 × 1440 panel at 150 %, to the left of the
        // primary and 180 pixels higher, with its own taskbar.
        var primary = new HudPixelRect(0, 0, 1920, 1032);
        var left = new HudPixelRect(-2560, -180, 0, 1188);
        const double leftScale = 1.5;

        var onLeft = DictationHudGeometry.Place(left, leftScale, DictationHudWidth.Compact, 40);
        var onPrimary = DictationHudGeometry.Place(primary, 1, DictationHudWidth.Compact, 40);

        Assert.True(onLeft.Left >= left.Left && onLeft.Right <= left.Right, $"{onLeft} is not inside {left}");
        Assert.True(onLeft.Right <= primary.Left, $"{onLeft} overlaps the primary monitor");
        Assert.InRange((onLeft.Left - left.Left) - (left.Right - onLeft.Right), -1, 1);

        // Its own scale, not the primary's: half as wide again in pixels.
        Assert.Equal((298, 98), (onPrimary.Width, onPrimary.Height));
        Assert.Equal((447, 147), (onLeft.Width, onLeft.Height));
        // (120 − 29) × 1.5 = 136.5, rounded away from zero.
        Assert.Equal(1188 - 137, onLeft.Bottom);
    }

    /// <summary>A monitor above the primary, rotated to portrait: negative
    /// coordinates on the vertical axis too.</summary>
    [Fact]
    public void A_monitor_above_the_primary_places_the_pill_at_negative_coordinates()
    {
        var above = new HudPixelRect(0, -1920, 1080, -48);

        var window = DictationHudGeometry.Place(above, 1, DictationHudWidth.Compact, 40);

        Assert.True(window.Top >= above.Top && window.Bottom <= above.Bottom, $"{window} is not inside {above}");
        Assert.Equal(above.Bottom - 91, window.Bottom);
        Assert.Equal((1080 - 298) / 2, window.Left);
    }

    /// <summary>
    /// At 150 % an odd number of DIPs is a half pixel. Every edge is a whole
    /// pixel, rounded rather than truncated, and the arithmetic is exact.
    /// </summary>
    [Fact]
    public void A_150_percent_scale_rounds_to_whole_pixels()
    {
        var work = new HudPixelRect(0, 0, 2880, 1728);

        var window = DictationHudGeometry.Place(work, 1.5, 241, 39);

        // (241 + 58) × 1.5 = 448.5 → 449; (39 + 58) × 1.5 = 145.5 → 146.
        Assert.Equal(449, window.Width);
        Assert.Equal(146, window.Height);
        Assert.Equal((2880 - 449) / 2, window.Left);
        // (120 − 29) × 1.5 = 136.5 → 137 above the bottom of the work area.
        Assert.Equal(1728 - 137, window.Bottom);
        Assert.Equal(1728 - 137 - 146, window.Top);
    }

    [Theory]
    [InlineData(96u, 1.0)]
    [InlineData(120u, 1.25)]
    [InlineData(144u, 1.5)]
    [InlineData(192u, 2.0)]
    [InlineData(0u, 1.0)]
    public void The_scale_is_the_effective_dpi_over_96_and_a_failed_read_is_100_percent(uint dpi, double scale) =>
        Assert.Equal(scale, DictationHudGeometry.Scale(dpi));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.PositiveInfinity)]
    public void A_scale_that_is_not_a_positive_number_is_100_percent(double scale)
    {
        var work = new HudPixelRect(0, 0, 1920, 1032);

        Assert.Equal(
            DictationHudGeometry.Place(work, 1, DictationHudWidth.Compact, 40),
            DictationHudGeometry.Place(work, scale, DictationHudWidth.Compact, 40));
    }

    [Fact]
    public void A_work_area_smaller_than_the_pill_keeps_it_on_screen_from_the_top_left()
    {
        var tiny = new HudPixelRect(100, 50, 300, 150);

        var window = DictationHudGeometry.Place(tiny, 1, DictationHudWidth.Compact, 40);

        Assert.Equal(100, window.Left);
        Assert.Equal(50, window.Top);
    }

    // ─── Width ──────────────────────────────────────────────────────────

    /// <summary>The states that last as long as the hold or its transcription
    /// does, named no lines to be sized for (the shared model's, and
    /// Dictation's own before the feed sizes them): whatever their text
    /// measures, the pill does not resize. The app's own busy pills take
    /// the one busy width instead (<see cref="IndicatorHintTests"/>).</summary>
    public static TheoryData<string, DictationHudModel> LiveStates => new()
    {
        { "listening", DictationHudModel.For(DictationState.Listening) },
        { "hearing you", DictationHudModel.Listening(1f) },
        { "working", DictationHudModel.For(DictationState.Transcribing) },
        { "typing", DictationHudModel.For(DictationState.Inserting) },
        { "held", new DictationHudModel(DictationCopy.HudHeld, HudTone.Neutral, null) },
        { "transcribing", new DictationHudModel(DictationCopy.HudTranscribing, HudTone.Neutral, null) },
    };

    [Theory]
    [MemberData(nameof(LiveStates))]
    public void A_state_with_no_lines_to_be_sized_for_keeps_the_compact_width_whatever_it_measures(string name, DictationHudModel model)
    {
        Assert.Null(model.Dwell);
        Assert.True(model.IsVisible, name);
        Assert.Equal(DictationHudWidth.Compact, DictationHudGeometry.PillWidth(model, 10));
        Assert.Equal(DictationHudWidth.Compact, DictationHudGeometry.PillWidth(model, 900));
    }

    [Fact]
    public void An_outcome_takes_the_width_its_longest_line_needs_up_to_the_cap()
    {
        var outcome = new DictationHudModel(DictationCopy.HudKeptOnClipboard, HudTone.Caution, DictationHudModel.FailureDwell);

        Assert.Equal(DictationHudWidth.Compact, DictationHudGeometry.PillWidth(outcome, 60));
        Assert.Equal(Math.Ceiling(300.4 + DictationHudGeometry.Chrome), DictationHudGeometry.PillWidth(outcome, 300.4));
        Assert.Equal(DictationHudWidth.Max, DictationHudGeometry.PillWidth(outcome, 2000));
        Assert.Equal(DictationHudWidth.Compact, DictationHudGeometry.PillWidth(outcome, double.NaN));
    }

    [Fact]
    public void The_chrome_is_everything_in_the_capsule_but_the_text() =>
        Assert.Equal(
            2 * DictationHudGeometry.HorizontalPadding + 2 * DictationHudGeometry.Border
                + DictationHudGeometry.MarkSlot + DictationHudGeometry.MarkGap,
            DictationHudGeometry.Chrome);

    /// <summary>The dot and the level bars share one slot, as wide as the
    /// bars, so a pill's text column is the same with either in it, and the
    /// bars' box is fixed, so no level resizes the pill.</summary>
    [Fact]
    public void The_mark_slot_holds_the_dot_and_the_bars_at_one_width()
    {
        Assert.Equal(DictationHudWaveform.Width, DictationHudGeometry.MarkSlot);
        Assert.True(DictationHudGeometry.MarkSlot >= DictationHudGeometry.MarkSize);
        Assert.True(DictationHudWaveform.FrameHeight >= DictationHudLevel.MaxHeight);
    }

    // ─── Two lines ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Listening", "Listening", null)]
    [InlineData("Couldn't type here — it's on the clipboard, press Ctrl+V", "Couldn't type here", "It's on the clipboard, press Ctrl+V")]
    [InlineData("Typed — your clipboard did not come back", "Typed", "Your clipboard did not come back")]
    [InlineData("First — second — third", "First", "Second — third")]
    [InlineData("Dangling — ", "Dangling", null)]
    [InlineData("Well-known words", "Well-known words", null)]
    public void A_sentence_splits_at_its_first_dash_into_a_title_and_a_capitalised_detail(
        string text, string title, string? detail)
    {
        var lines = DictationHudLines.For(text);

        Assert.Equal(title, lines.Title);
        Assert.Equal(detail, lines.Detail);
    }

    /// <summary>
    /// Every line the feed can put on the pill: the controller's own
    /// <c>Hud*</c> sentences and the shared model's, for every state and
    /// outcome. Each has a title, and a detail, when there is one, reads as a
    /// sentence of its own. The count guards against reflection finding
    /// nothing and the loop passing over an empty list.
    /// </summary>
    [Fact]
    public void Every_line_the_indicator_can_show_has_a_title_and_a_capitalised_detail()
    {
        var texts = IndicatorTexts().Distinct().ToArray();
        Assert.True(texts.Length >= 25, $"only {texts.Length} indicator lines found");

        Assert.All(texts, text =>
        {
            var lines = DictationHudLines.For(text);
            Assert.False(string.IsNullOrWhiteSpace(lines.Title), text);
            Assert.StartsWith(lines.Title, text, StringComparison.Ordinal);
            if (lines.Detail is { } detail)
            {
                Assert.True(char.IsUpper(detail[0]), $"'{detail}' does not start a sentence");
            }
        });
        Assert.Contains(texts, t => DictationHudLines.For(t).Detail is not null);
    }

    internal static IEnumerable<string> IndicatorTexts()
    {
        var copy = typeof(DictationCopy)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Hud", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();
        Assert.True(copy.Length >= 10, $"only {copy.Length} Hud lines in DictationCopy");
        foreach (var text in copy)
        {
            yield return text;
        }

        foreach (var state in Enum.GetValues<DictationState>())
        {
            var model = DictationHudModel.For(state);
            if (model.IsVisible)
            {
                yield return model.Text;
            }
        }

        // Every ending, for every hold key and every context the controller
        // gives one (a microphone change, with the longest name a sentence
        // carries, included), and every notice: one list, the feed tests', so
        // a line added there is held to these rules too.
        foreach (var model in DictationHudFeedTests.EveryEnding())
        {
            if (model.IsVisible)
            {
                yield return model.Text;
            }
        }
    }

    // ─── Window styles ──────────────────────────────────────────────────

    [Fact]
    public void The_styles_are_the_win32_no_activate_transparent_and_tool_window_flags()
    {
        Assert.Equal(0x0800_0000L, DictationHudWindowStyle.NoActivate);
        Assert.Equal(0x0000_0020L, DictationHudWindowStyle.Transparent);
        Assert.Equal(0x0000_0080L, DictationHudWindowStyle.ToolWindow);
        Assert.Equal(0x0800_00A0L, DictationHudWindowStyle.Required);
    }

    [Fact]
    public void Applying_the_styles_adds_every_one_and_keeps_wpfs_own()
    {
        const long Layered = 0x0008_0000;
        const long Topmost = 0x0000_0008;

        var applied = DictationHudWindowStyle.Apply(Layered | Topmost);

        Assert.True(DictationHudWindowStyle.Holds(applied));
        Assert.Equal(Layered | Topmost, applied & (Layered | Topmost));
        Assert.False(DictationHudWindowStyle.Holds(Layered | Topmost));
        foreach (var flag in new[] { DictationHudWindowStyle.NoActivate, DictationHudWindowStyle.Transparent, DictationHudWindowStyle.ToolWindow })
        {
            Assert.False(DictationHudWindowStyle.Holds(applied & ~flag), $"0x{flag:X} missing still reads as held");
        }
    }

    // ─── The window's markup and wiring ─────────────────────────────────
    //
    // Pins on the source, not evidence that the window behaves: WPF cannot
    // run here. Each one fails if the thing it names is deleted, which is the
    // most a machine with no Windows can say about a window that must never
    // take the focus.

    internal static string AppFile(string name) =>
        Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", name);

    internal static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static XElement Named(XDocument xaml, string name) =>
        xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    /// <summary>The text of the member that starts with
    /// <paramref name="signature"/>, up to its closing brace, or to the end of
    /// its expression when it has an arrow body.</summary>
    internal static string Member(string code, string signature)
    {
        code = code.ReplaceLineEndings("\n");
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{signature}'");
        var arrow = code.IndexOf("=>", start, StringComparison.Ordinal);
        var brace = code.IndexOf("\n    {", start, StringComparison.Ordinal);
        var end = arrow >= 0 && (brace < 0 || arrow < brace)
            ? code.IndexOf(";\n", arrow, StringComparison.Ordinal)
            : code.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"no end to '{signature}'");
        return code[start..end];
    }

    [Theory]
    [InlineData("ShowActivated", "False")]
    [InlineData("ShowInTaskbar", "False")]
    [InlineData("Focusable", "False")]
    [InlineData("Topmost", "True")]
    [InlineData("WindowStyle", "None")]
    [InlineData("AllowsTransparency", "True")]
    public void The_indicator_window_is_declared_never_to_take_the_focus(string attribute, string value)
    {
        var window = XDocument.Load(AppFile("DictationHudWindow.xaml")).Root!;

        Assert.Equal(value, (string?)window.Attribute(attribute));
    }

    [Fact]
    public void The_indicator_speaks_through_one_polite_live_region_and_keeps_its_second_line_out_of_the_tree()
    {
        var xaml = XDocument.Load(AppFile("DictationHudWindow.xaml"));
        var live = xaml.Descendants()
            .Where(e => e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.LiveSetting"))
            .ToArray();

        var status = Assert.Single(live);
        Assert.Equal("StatusLine", (string?)status.Attribute(X + "Name"));
        Assert.Equal("Polite", (string?)status.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.LiveSetting"));
        Assert.Equal("UnannouncedTextBlock", Named(xaml, "DetailLine").Name.LocalName);
    }

    [Fact]
    public void The_indicator_sets_its_styles_when_its_handle_is_made_and_again_on_every_show()
    {
        var code = File.ReadAllText(AppFile("DictationHudWindow.xaml.cs"));

        Assert.Contains("DictationHudWindowStyle.Apply(", Member(code, "private long ApplyNoActivateStyles()"), StringComparison.Ordinal);
        Assert.Contains("ApplyNoActivateStyles()", Member(code, "protected override void OnSourceInitialized("), StringComparison.Ordinal);
        Assert.Contains("ApplyNoActivateStyles()", Member(code, "void IDictationHud.Show("), StringComparison.Ordinal);
        Assert.Contains("EnsureHandle()", Member(code, "public DictationHudWindow("), StringComparison.Ordinal);
    }

    [Fact]
    public void The_indicator_announces_through_the_shared_rule_and_forgets_with_the_pill()
    {
        var code = File.ReadAllText(AppFile("DictationHudWindow.xaml.cs"));
        var announce = Member(code, "private void Announce(");

        Assert.Contains("Announce(model)", Member(code, "void IDictationHud.Show("), StringComparison.Ordinal);
        // Only when the feed allows it: never while the microphone is open.
        Assert.Contains("if (announce)", Member(code, "void IDictationHud.Show("), StringComparison.Ordinal);
        Assert.Contains("LiveRegionAnnouncement.ShouldRaise(", announce, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StatusLine, sentence)", announce, StringComparison.Ordinal);
        Assert.Contains("LiveRegion.Raise(StatusLine)", announce, StringComparison.Ordinal);
        Assert.Contains("lastAnnounced = string.Empty", Member(code, "private void HidePill()"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A pill coming up from hidden is shown before it is placed, so a move
    /// to a monitor with another scale ends with the window
    /// visible and following its own size changes. It is transparent until a
    /// later pass, after layout, has placed it again; and it is announced only
    /// once it is showing.
    /// </summary>
    [Fact]
    public void A_pill_coming_up_is_shown_transparent_then_placed_then_revealed()
    {
        var code = File.ReadAllText(AppFile("DictationHudWindow.xaml.cs")).ReplaceLineEndings("\n");
        var show = Member(code, "void IDictationHud.Show(");
        var transparent = show.IndexOf("Opacity = 0;", StringComparison.Ordinal);
        var shown = show.IndexOf("Show();", StringComparison.Ordinal);
        Assert.True(transparent >= 0 && shown > transparent, "transparent before it is shown");
        var placed = show.IndexOf("placed = Place();", shown, StringComparison.Ordinal);
        Assert.True(placed > shown, "placed after it is shown");
        Assert.True(show.IndexOf("RevealWhenLaidOut();", placed, StringComparison.Ordinal) > placed, "revealed after it is placed");
        Assert.True(show.IndexOf("Announce(model);", shown, StringComparison.Ordinal) > shown, "announced after it is shown");
        Assert.DoesNotContain("Opacity = 1", show, StringComparison.Ordinal);

        var reveal = Member(code, "private void RevealWhenLaidOut()");
        Assert.Contains("DispatcherPriority.Loaded", reveal, StringComparison.Ordinal);
        Assert.Contains("showing != showings", reveal, StringComparison.Ordinal);
        var again = reveal.IndexOf("Place();", StringComparison.Ordinal);
        Assert.True(again >= 0 && reveal.IndexOf("Opacity = 1;", StringComparison.Ordinal) > again, "placed once more before it is made visible");
    }

    /// <summary>
    /// The text column is held to the capsule's width less its chrome, after
    /// that width is worked out, and both lines trim with an ellipsis: a line
    /// longer than the pill can hold (an ending's second line after its two
    /// wrapped lines, <see cref="IndicatorLookTests"/>) ends in "…" inside the
    /// rounded edge instead of running past it.
    /// </summary>
    [Fact]
    public void The_indicator_holds_its_text_to_the_capsule_and_trims_what_does_not_fit()
    {
        var show = Member(File.ReadAllText(AppFile("DictationHudWindow.xaml.cs")), "void IDictationHud.Show(");
        var width = show.IndexOf("var width = DictationHudGeometry.PillWidth(", StringComparison.Ordinal);
        var capped = show.IndexOf("Lines.MaxWidth = Math.Max(0, width - DictationHudGeometry.Chrome);", StringComparison.Ordinal);
        Assert.True(width >= 0 && capped > width, "the text column is capped to the pill's width");
        Assert.True(show.IndexOf("Capsule.Measure(", StringComparison.Ordinal) > capped, "the capsule is measured with the cap in place");

        var xaml = XDocument.Load(AppFile("DictationHudWindow.xaml"));
        foreach (var line in new[] { "StatusLine", "DetailLine" })
        {
            Assert.Equal("CharacterEllipsis", (string?)Named(xaml, line).Attribute("TextTrimming"));
            Assert.Same(Named(xaml, "Lines"), Named(xaml, line).Parent);
        }
    }

    [Fact]
    public void The_indicator_is_placed_by_the_tested_geometry_on_the_foreground_windows_monitor()
    {
        var place = Member(File.ReadAllText(AppFile("DictationHudWindow.xaml.cs")), "private (HudPixelRect Window, double Scale)? Place()");

        Assert.Contains("MonitorFromWindow(", place, StringComparison.Ordinal);
        Assert.Contains("GetForegroundWindow()", place, StringComparison.Ordinal);
        Assert.Contains("DictationHudGeometry.Scale(", place, StringComparison.Ordinal);
        Assert.Contains("DictationHudGeometry.Place(", place, StringComparison.Ordinal);
        Assert.Contains("SWP_NOACTIVATE", place, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_hands_the_indicator_to_the_controller_and_closes_it_after_the_controller()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var constructor = Member(code, "public MainWindow(");

        Assert.Contains("hud = new DictationHudWindow(", constructor, StringComparison.Ordinal);
        Assert.Contains("hud: hud", constructor, StringComparison.Ordinal);
        var letGo = Member(code, "private void LetGo()");
        var disposed = letGo.IndexOf("Step(() => controller?.Dispose());", StringComparison.Ordinal);
        var closed = letGo.IndexOf("Step(() => hud?.Close());", StringComparison.Ordinal);
        Assert.True(disposed >= 0 && closed > disposed, "the indicator must close after the controller lets go of it");
    }

    /// <summary>
    /// Closing the window ends the process even when letting go of something
    /// throws: App holds the single-instance mutex until it exits. And each
    /// thing is let go of on its own, so a controller that throws on the way
    /// out does not leave the notification-area icon drawn after the exit.
    /// </summary>
    [Fact]
    public void Closing_the_window_ends_the_process_and_takes_the_icon_down_whatever_throws_first()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var constructor = Member(code, "public MainWindow(");
        var onClosed = constructor[constructor.IndexOf("Closed += ", StringComparison.Ordinal)..];
        var @finally = onClosed.IndexOf("finally", StringComparison.Ordinal);
        var shutdown = onClosed.IndexOf("Application.Current.Shutdown();", StringComparison.Ordinal);
        Assert.True(@finally > onClosed.IndexOf("LetGo();", StringComparison.Ordinal), "LetGo must run inside the try");
        Assert.True(shutdown > @finally, "Shutdown must be in the finally");

        var letGo = Member(code, "private void LetGo()");
        Assert.Contains("catch (Exception error)", letGo, StringComparison.Ordinal);
        foreach (var step in new[] { "poll.Stop", "() => controller?.Dispose()", "() => hud?.Close()", "() => vocabulary?.Dispose()", "tray.Dispose" })
        {
            Assert.Contains($"Step({step});", letGo, StringComparison.Ordinal);
        }

        Assert.True(
            letGo.IndexOf("first?.Throw();", StringComparison.Ordinal) > letGo.IndexOf("Step(tray.Dispose);", StringComparison.Ordinal),
            "the first failure is thrown only after every step has run");
    }

    /// <summary>
    /// Every polite live region in the window gets its text through the one
    /// method that raises <c>LiveRegionChanged</c>. A <c>LiveSetting</c> whose
    /// text is assigned directly is marked as live and never heard.
    /// <c>ForgetAllQuestion</c> is the exception: assertive, its text never
    /// changes, and the confirmation it heads moves the focus to its Cancel
    /// button, which a reader announces.
    /// </summary>
    [Fact]
    public void Every_polite_live_region_in_the_window_is_set_through_the_method_that_announces_it()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var polite = xaml.Descendants()
            .Where(e => (string?)e.Attributes().SingleOrDefault(a => a.Name.LocalName == "AutomationProperties.LiveSetting") == "Polite")
            .Select(e => (string)e.Attribute(X + "Name")!)
            .ToArray();
        var setLiveText = Member(code, "private void SetLiveText(");

        Assert.Contains("Status", polite);
        Assert.True(polite.Length >= 4, $"only {polite.Length} polite regions: {string.Join(", ", polite)}");
        Assert.All(polite, name =>
        {
            Assert.Contains($"SetLiveText({name}, ", code, StringComparison.Ordinal);
            Assert.DoesNotContain($"{name}.Text = ", code, StringComparison.Ordinal);
        });
        Assert.Contains("LiveRegionAnnouncement.ShouldRaise(previous, text)", setLiveText, StringComparison.Ordinal);
        Assert.Contains("LiveRegion.Raise(block)", setLiveText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The window's live regions speak only while the user is in the window.
    /// During a hold they are in another app, and the indicator says the same
    /// ending there; two polite announcements of one ending would be read one
    /// after the other. And not while the hold listens, even in the window
    /// (<see cref="DictationPracticeTests"/>).
    /// </summary>
    [Fact]
    public void The_window_announces_only_while_the_user_is_in_it()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var setLiveText = Member(code, "private void SetLiveText(");

        Assert.Contains("if (IsActive && !microphoneOpen && LiveRegionAnnouncement.ShouldRaise(previous, text))", setLiveText, StringComparison.Ordinal);
        Assert.Single(setLiveText.Split("LiveRegion.Raise(").Skip(1));
    }

    /// <summary>
    /// A constructor that fails after the notification-area icon is up lets
    /// go of it before App reports the failure and exits: Closed never runs
    /// for that window, and the icon would stay drawn until the mouse passed
    /// over it. Everything made after the icon is inside the guarded block,
    /// and letting go tolerates what was never made and runs once.
    /// </summary>
    [Fact]
    public void A_window_that_fails_to_start_takes_its_icon_down_before_the_error_goes_on()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var constructor = Member(code, "public MainWindow(");
        var icon = constructor.IndexOf("tray = new DictationTray(", StringComparison.Ordinal);
        var guarded = constructor.IndexOf("\n        try\n        {", StringComparison.Ordinal);
        var failed = constructor.IndexOf("\n        catch\n        {", StringComparison.Ordinal);
        Assert.True(icon >= 0 && guarded > icon && failed > guarded, "the icon is made, then everything after it is guarded");
        foreach (var made in new[] { "hud = new DictationHudWindow(", "controller = new DictationController(", "controller.Start(atLogin)", "vocabulary.Refresh()" })
        {
            var at = constructor.IndexOf(made, StringComparison.Ordinal);
            Assert.True(at > guarded && at < failed, $"'{made}' is outside the guarded block");
        }

        var handler = constructor[failed..];
        var letGoAt = handler.IndexOf("LetGo();", StringComparison.Ordinal);
        Assert.True(letGoAt >= 0 && handler.IndexOf("throw;", StringComparison.Ordinal) > letGoAt, "let go, then let the failure reach App");
        var letGo = Member(code, "private void LetGo()");
        var once = letGo.IndexOf("if (released)", StringComparison.Ordinal);
        Assert.True(once >= 0 && once < letGo.IndexOf("Step(", StringComparison.Ordinal), "runs once");
    }

    /// <summary>The sentence that sends the user to Check again names the
    /// button the window shows, beside the privacy-page link.</summary>
    [Fact]
    public void Check_again_sits_beside_the_privacy_link_and_is_labelled_the_way_the_sentence_names_it()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var recheck = Named(xaml, "MicrophoneRecheckButton");

        Assert.Equal(DictationCopy.CheckMicrophoneAgain, ((string?)recheck.Attribute("Content"))?.Replace("_", string.Empty));
        Assert.Contains(DictationCopy.CheckMicrophoneAgain, DictationCopy.MicrophoneStillBlocked, StringComparison.Ordinal);
        Assert.Same(Named(xaml, "MicrophoneSettingsButton").Parent, recheck.Parent);
        Assert.Equal("MicrophoneRecheckClick", (string?)recheck.Attribute("Click"));
    }

    [Fact]
    public void The_window_asks_about_the_microphone_again_when_activated_and_when_Check_again_is_chosen()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

        Assert.Contains("Activated += (_, _) => controller.RecheckMicrophone();", Member(code, "public MainWindow("), StringComparison.Ordinal);
        Assert.Contains("controller.RecheckMicrophone(checkAgain: true)", Member(code, "private void MicrophoneRecheckClick("), StringComparison.Ordinal);
        Assert.Contains("MicrophoneActions.Visibility", Member(code, "public void Render(DictationViewState state)"), StringComparison.Ordinal);
    }

    // ─── Live regions ───────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "Listening", true)]
    [InlineData("", "Listening", true)]
    [InlineData("Listening", "Working", true)]
    [InlineData("Listening", "Listening", false)]
    [InlineData("Listening", "", false)]
    [InlineData("Listening", null, false)]
    [InlineData("listening", "Listening", true)]
    public void A_live_region_speaks_only_for_new_non_empty_text(string? previous, string? next, bool raise) =>
        Assert.Equal(raise, LiveRegionAnnouncement.ShouldRaise(previous, next));

    [Theory]
    [InlineData("Still blocked", "Still blocked", true)]
    [InlineData("Blocked", "Still blocked", false)]
    [InlineData(null, "Still blocked", false)]
    [InlineData("still blocked", "Still blocked", false)]
    [InlineData("", "", false)]
    [InlineData("Still blocked", "", false)]
    [InlineData(null, null, false)]
    public void An_answer_is_a_repeat_only_when_it_is_the_same_non_empty_sentence(string? before, string? after, bool repeat)
    {
        Assert.Equal(repeat, LiveRegionAnnouncement.IsRepeat(before, after));
        if (!string.IsNullOrEmpty(after))
        {
            // News or a repeat, never neither: no answer goes unshown.
            Assert.NotEqual(LiveRegionAnnouncement.ShouldRaise(before, after), LiveRegionAnnouncement.IsRepeat(before, after));
        }
    }

    /// <summary>
    /// Check again with the switch still off, through the controller the
    /// window calls. The first press changes the sentence, and the window
    /// announces it like any change. The second gets the same sentence back,
    /// which no change rule can see: that is the repeat the window shows and
    /// announces again. Once access is on, the answer is news again.
    /// </summary>
    [Fact]
    public async Task A_second_Check_again_with_the_switch_still_off_is_a_repeat_and_every_press_is_one_or_the_other()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();

        (bool News, bool Repeat) Press()
        {
            var before = rig.View.Last.Status;
            rig.Controller.RecheckMicrophone(checkAgain: true);
            var after = rig.View.Last.Status;
            return (LiveRegionAnnouncement.ShouldRaise(before, after), LiveRegionAnnouncement.IsRepeat(before, after));
        }

        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.Equal((true, false), Press());
        Assert.Equal(DictationCopy.MicrophoneStillBlocked, rig.View.Last.Status);
        Assert.Equal((false, true), Press());
        Assert.Equal((false, true), Press());
        Assert.Equal(DictationCopy.MicrophoneStillBlocked, rig.View.Last.Status);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Assert.Equal((true, false), Press());
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
    }

    /// <summary>
    /// The window's half, pinned on the source: a Check again whose answer is
    /// a repeat takes the status line out of sight for
    /// <see cref="LiveRegionAnnouncement.RepeatPause"/>, then brings it back
    /// and raises its live region while the window is active.
    /// </summary>
    [Fact]
    public void Check_again_shows_and_announces_a_repeated_answer_again()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var click = Member(code, "private void MicrophoneRecheckClick(");
        var asked = click.IndexOf("controller.RecheckMicrophone(checkAgain: true);", StringComparison.Ordinal);
        var repeat = click.IndexOf("LiveRegionAnnouncement.IsRepeat(before, Status.Text)", StringComparison.Ordinal);
        Assert.True(asked >= 0 && repeat > asked, "the repeat is judged on the answer");
        var read = click.IndexOf("var before = Status.Text;", StringComparison.Ordinal);
        Assert.True(read >= 0 && read < asked, "the sentence before is read before asking");
        Assert.Contains("Status.Opacity = 0;", click, StringComparison.Ordinal);
        Assert.Contains("recheckPause.Start();", click, StringComparison.Ordinal);
        Assert.Contains("Interval = LiveRegionAnnouncement.RepeatPause", code, StringComparison.Ordinal);
        Assert.Contains("recheckPause.Tick += (_, _) => EndRecheckPause();", code, StringComparison.Ordinal);

        var end = Member(code, "private void EndRecheckPause()");
        Assert.Contains("Status.Opacity = 1;", end, StringComparison.Ordinal);
        Assert.Contains("IsActive && Status.Text == recheckRepeated", end, StringComparison.Ordinal);
        Assert.Contains("LiveRegion.Raise(Status);", end, StringComparison.Ordinal);
    }
}
