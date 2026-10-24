using System.Xml.Linq;
using Dictation.Services;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The live preview's caption on the indicator: the feed puts it on the
/// listening pill only, never announced and never in a name; the pill keeps
/// its one busy width; the caption is fitted by cutting its oldest words; and
/// the window draws it in an element out of the automation tree (pinned on its
/// source: WPF cannot run here).
/// </summary>
public sealed class LivePreviewSurfaceTests
{
    private static (DictationHudFeed Feed, FakeHud Hud, FakeClock Clock) Feed()
    {
        var hud = new FakeHud();
        var clock = new FakeClock();
        return (new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold), hud, clock);
    }

    // ─── The feed ───────────────────────────────────────────────────────

    [Fact]
    public void A_caption_stands_on_the_listening_pill_unannounced_and_named_for_the_state()
    {
        var (feed, hud, _) = Feed();
        feed.State(DictationState.Listening);
        var listening = hud.Current!;

        feed.Caption("hello world");

        var captioned = hud.Current!;
        Assert.Equal("hello world", captioned.Caption);
        Assert.False(hud.Announced[^1]);
        Assert.Equal(listening.Text, captioned.Text);
        Assert.Equal("Listening", captioned.QuietName);
        Assert.DoesNotContain("hello", captioned.AnnouncedText, StringComparison.Ordinal);
        // The same busy lines: the caption never resizes the pill.
        Assert.Same(listening.SizedFor, captioned.SizedFor);
        // Still a listening pill: the bars are drawn again beside it.
        Assert.True(hud.Bars);
    }

    [Fact]
    public void A_caption_that_did_not_change_or_has_no_words_redraws_nothing()
    {
        var (feed, hud, _) = Feed();
        feed.State(DictationState.Listening);
        feed.Caption("hello world");
        var shows = hud.Shows.Count;

        feed.Caption("hello world");
        feed.Caption("");
        feed.Caption("   ");
        feed.Caption(null);

        Assert.Equal(shows, hud.Shows.Count);
        Assert.Equal("hello world", hud.Current!.Caption);
    }

    [Fact]
    public void A_caption_with_no_listening_pill_up_is_dropped()
    {
        var (feed, hud, _) = Feed();
        feed.Caption("too early");
        Assert.Empty(hud.Shows);

        feed.State(DictationState.Listening);
        feed.State(DictationState.Transcribing);
        var shows = hud.Shows.Count;
        feed.Caption("too late");

        Assert.Equal(shows, hud.Shows.Count);
        Assert.Null(hud.Current!.Caption);
    }

    [Fact]
    public void The_hands_free_clock_keeps_the_caption_and_the_next_hold_starts_without_one()
    {
        var (feed, hud, _) = Feed();
        feed.State(DictationState.Listening);
        feed.Caption("hello world");

        feed.Listening(latched: true, TimeSpan.FromSeconds(3));
        Assert.Equal("hello world", hud.Current!.Caption);
        Assert.Contains("0:03", hud.Current.Text, StringComparison.Ordinal);
        Assert.Equal("Listening, hands-free", hud.Current.QuietName);

        feed.State(DictationState.Transcribing);
        feed.Result(new DictationSessionResult(DictationOutcome.Inserted, "hello world", null), new DictationHudContext());
        feed.State(DictationState.Listening);
        Assert.Null(hud.Current!.Caption);
        feed.Listening(latched: true, TimeSpan.FromSeconds(4));
        Assert.Null(hud.Current!.Caption);
    }

    [Fact]
    public void No_pill_but_a_listening_one_ever_carries_a_caption()
    {
        var (feed, hud, _) = Feed();
        feed.State(DictationState.Listening);
        feed.Caption("hello world");
        feed.PressRefused(DictationPressRefusal.StillTyping);
        feed.State(DictationState.Listening);
        feed.Caption("hello again");
        feed.State(DictationState.Transcribing);
        feed.State(DictationState.Inserting);
        feed.Result(new DictationSessionResult(DictationOutcome.Inserted, "hello again", null), new DictationHudContext());

        var captioned = hud.Shows.Where(p => p.Caption is not null).ToArray();
        Assert.Equal(2, captioned.Length);
        Assert.All(captioned, p => Assert.Equal("Listening", p.QuietName));
        Assert.All(hud.Shows.Where(p => p.UnannouncedName is null), p => Assert.Null(p.Caption));
    }

    [Fact]
    public void The_pill_is_as_wide_with_a_caption_as_without_one()
    {
        var pill = new DictationHudModel(DictationCopy.HudHeld, HudTone.Neutral, null)
        {
            SizedFor = DictationCopy.HudBusyLines("Right Ctrl", DictationHoldPolicy.Windows),
            UnannouncedName = "Listening",
        };
        var long_ = pill with { Caption = string.Join(' ', Enumerable.Repeat("supercalifragilistic", 7)) };
        static double Width(string text) => text.Length * 7.0;

        var widest = DictationHudGeometry.WidestLine(pill, Width, Width);
        Assert.Equal(widest, DictationHudGeometry.WidestLine(long_, Width, Width));
        Assert.Equal(DictationHudGeometry.PillWidth(pill, widest), DictationHudGeometry.PillWidth(long_, widest));
    }

    // ─── Fitting the caption ────────────────────────────────────────────

    /// <summary>A stand-in font: every character 10 wide.</summary>
    private static double Ten(string text) => text.Length * 10.0;

    [Fact]
    public void A_caption_that_fits_is_shown_whole()
    {
        Assert.Equal("hello world", DictationHudCaption.HeadTrimmed("hello world", 110, Ten));
        Assert.Equal(string.Empty, DictationHudCaption.HeadTrimmed("", 110, Ten));
        Assert.Equal(string.Empty, DictationHudCaption.HeadTrimmed(null, 110, Ten));
    }

    [Fact]
    public void A_caption_too_wide_loses_its_oldest_words_behind_an_ellipsis()
    {
        // "one two three four" is 180 wide; 100 holds the ellipsis and nine more.
        var fitted = DictationHudCaption.HeadTrimmed("one two three four", 100, Ten);

        Assert.Equal("…hree four", fitted);
        Assert.True(Ten(fitted) <= 100);
        // The cut never leaves the space it fell on.
        Assert.Equal("…four", DictationHudCaption.HeadTrimmed("one two three four", 50, Ten));
    }

    [Fact]
    public void A_caption_is_cut_between_text_elements_only()
    {
        // "e" + combining acute, and a surrogate pair: never split.
        var caption = "cafe\u0301 \U0001F600 ok";
        for (var room = 0; room <= 120; room += 5)
        {
            var fitted = DictationHudCaption.HeadTrimmed(caption, room, Ten);
            var body = fitted.StartsWith(DictationHudCaption.Ellipsis, StringComparison.Ordinal) ? fitted[1..] : fitted;
            Assert.NotEmpty(body);
            Assert.False(char.IsLowSurrogate(body[0]), fitted);
            Assert.NotEqual(System.Globalization.UnicodeCategory.NonSpacingMark, char.GetUnicodeCategory(body[0]));
            Assert.EndsWith("k", fitted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void When_nothing_fits_the_newest_character_is_kept()
    {
        Assert.Equal("…了", DictationHudCaption.HeadTrimmed("我觉得没问题了", 5, Ten));
    }

    // ─── The window, on its source ──────────────────────────────────────

    [Fact]
    public void The_caption_line_is_out_of_the_automation_tree_in_the_titles_place_and_font()
    {
        var xaml = XDocument.Load(AppFile("DictationHudWindow.xaml"));
        var caption = Named(xaml, "CaptionLine");
        var status = Named(xaml, "StatusLine");

        Assert.Equal("UnannouncedTextBlock", caption.Name.LocalName);
        Assert.DoesNotContain(caption.Attributes(), a => a.Name.LocalName.StartsWith("AutomationProperties", StringComparison.Ordinal));
        Assert.Same(Named(xaml, "Lines"), caption.Parent);
        foreach (var attribute in new[] { "Grid.Row", "FontSize", "FontWeight", "Foreground" })
        {
            Assert.Equal((string?)status.Attribute(attribute), (string?)caption.Attribute(attribute));
        }

        Assert.NotEqual((string?)status.Attribute("Grid.Row"), (string?)Named(xaml, "DetailLine").Attribute("Grid.Row"));
        Assert.Equal("Collapsed", (string?)caption.Attribute("Visibility"));
        Assert.Equal("NoWrap", (string?)caption.Attribute("TextWrapping"));
    }

    [Fact]
    public void The_window_draws_the_caption_fitted_to_the_pill_and_never_names_or_announces_it()
    {
        var code = File.ReadAllText(AppFile("DictationHudWindow.xaml.cs")).ReplaceLineEndings("\n");
        var show = Member(code, "void IDictationHud.Show(");
        var capped = show.IndexOf("Lines.MaxWidth = Math.Max(0, width - DictationHudGeometry.Chrome);", StringComparison.Ordinal);
        var drawn = show.IndexOf("ShowCaption(model.Caption, Lines.MaxWidth);", StringComparison.Ordinal);
        var measured = show.IndexOf("Capsule.Measure(", StringComparison.Ordinal);
        Assert.True(capped >= 0 && drawn > capped && measured > drawn, "fitted to the capped column, before the capsule is measured");

        var draw = Member(code, "private void ShowCaption(");
        Assert.Contains("CaptionLine.Text = DictationHudCaption.HeadTrimmed(caption, room, text => TextWidth(CaptionLine, text));", draw, StringComparison.Ordinal);
        Assert.Contains("StatusLine.Opacity = 0;", draw, StringComparison.Ordinal);
        Assert.Contains("StatusLine.Opacity = 1;", draw, StringComparison.Ordinal);
        foreach (var word in new[] { "AutomationProperties", "LiveRegion", "Announce", "NameQuietly", "StatusLine.Text" })
        {
            Assert.DoesNotContain(word, draw, StringComparison.Ordinal);
        }

        // Only this member touches the caption; nothing names the pill by it.
        Assert.Equal(1, code.Split("model.Caption").Length - 1);
        Assert.Contains("ShowCaption(null, 0);", Member(code, "private void HidePill()"), StringComparison.Ordinal);
        // The pill's width is worked out without the caption.
        Assert.DoesNotContain("Caption", Member(code, "private double TextWidth("), StringComparison.Ordinal);
    }
}
