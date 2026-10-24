using System.Xml.Linq;
using Dictation.Services;

namespace Dictation.Core.Tests;

/// <summary>
/// The hold policy's Windows surface: the window's "Silence stops
/// hands-free" picker and the countdown's sound, pinned on the source (none
/// of it has run on Windows), and the indicator's silence warning beside the
/// live preview's caption, through the feed.
/// </summary>
public sealed class HoldPolicySurfaceTests
{
    private static string Code(string file) => File.ReadAllText(DictationHudGeometryTests.AppFile(file)).ReplaceLineEndings("\n");

    [Fact]
    public void The_window_offers_the_silence_choice_beside_the_language()
    {
        var xaml = XDocument.Load(DictationHudGeometryTests.AppFile("MainWindow.xaml"));
        var picker = DictationHudGeometryTests.Named(xaml, "SilenceStopPicker");
        Assert.Equal("Name", (string?)picker.Attribute("DisplayMemberPath"));
        Assert.Equal("SilenceStopChanged", (string?)picker.Attribute("SelectionChanged"));
        Assert.Equal("Silence stops hands-free", (string?)picker.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name"));
        var label = xaml.Descendants().Single(e => e.Name.LocalName == "Label" && ((string?)e.Attribute("Target"))?.Contains("SilenceStopPicker") == true);
        Assert.Equal("S_ilence stops hands-free:", (string?)label.Attribute("Content"));
        Assert.Equal(DictationHudGeometryTests.Named(xaml, "LanguagePicker").Parent, picker.Parent);

        var code = Code("MainWindow.xaml.cs");
        Assert.Contains("SilenceStopPicker.ItemsSource = controller.SilenceStopChoices;", code, StringComparison.Ordinal);
        var render = DictationHudGeometryTests.Member(code, "public void Render(DictationViewState state)");
        var drawn = render.IndexOf("SilenceStopPicker.SelectedItem = DictationSilenceStopChoice.For(state.SilenceStop);", StringComparison.Ordinal);
        Assert.True(drawn > render.IndexOf("rendering = true;", StringComparison.Ordinal), "selected outside the rendering guard");
        Assert.True(drawn < render.IndexOf("rendering = false;", StringComparison.Ordinal), "selected outside the rendering guard");
        var changed = DictationHudGeometryTests.Member(code, "private void SilenceStopChanged(");
        Assert.Contains("!rendering && SilenceStopPicker.SelectedItem is DictationSilenceStopChoice choice", changed, StringComparison.Ordinal);
        Assert.Contains("controller.ChooseSilenceStop(choice.Value);", changed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_countdown_s_sound_is_Windows_own_alert()
    {
        var play = DictationHudGeometryTests.Member(Code("WindowsDictationPlatform.cs"), "public void PlayAlertSound()");
        Assert.Contains("System.Media.SystemSounds.Asterisk.Play()", play, StringComparison.Ordinal);
    }

    /// <summary>
    /// The silence warning is the line to read: the live preview's caption,
    /// which stands in the listening pill's title, gives way to it, and comes
    /// back with the next listening pill. A caption that arrives while the
    /// warning is up waits for it to go.
    /// </summary>
    [Fact]
    public void The_silence_warning_is_not_covered_by_the_live_preview_s_caption()
    {
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => TimeSpan.Zero, () => FakeHud.Hold);
        feed.State(DictationState.Listening);
        feed.Caption("hello there");
        feed.Listening(latched: true, TimeSpan.FromSeconds(40));
        Assert.Equal("hello there", hud.Current!.Caption);

        var warning = new DictationSilenceWarning(TimeSpan.FromSeconds(21), TimeSpan.FromSeconds(9));
        feed.Listening(latched: true, TimeSpan.FromSeconds(41), warning);
        Assert.Equal("No speech for 21 s — stopping in 9 s. Keep talking to continue", hud.Current!.Text);
        Assert.Null(hud.Current.Caption);
        var shows = hud.Shows.Count;
        feed.Caption("thank you");
        Assert.Equal(shows, hud.Shows.Count);
        Assert.Null(hud.Current!.Caption);

        feed.Listening(latched: true, TimeSpan.FromSeconds(42));
        Assert.Equal("thank you", hud.Current!.Caption);
        Assert.StartsWith("Listening — 0:42", hud.Current.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A hold that ended while its silence warning was up leaves nothing
    /// behind: the next hold's caption is shown, though its pill keeps the
    /// same words all along (a held hold's "Release to insert") and so never
    /// redraws the warning away itself.
    /// </summary>
    [Fact]
    public void A_warning_the_last_hold_ended_on_does_not_hide_the_next_hold_s_caption()
    {
        var now = TimeSpan.Zero;
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => now, () => FakeHud.Hold);
        feed.State(DictationState.Listening);
        feed.Listening(latched: true, TimeSpan.FromSeconds(40), new DictationSilenceWarning(TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(1)));
        Assert.StartsWith("No speech for 29 s", hud.Current!.Text, StringComparison.Ordinal);
        now += TimeSpan.FromSeconds(1);
        feed.State(DictationState.Transcribing);
        now += TimeSpan.FromSeconds(5);
        feed.State(DictationState.Armed);

        now += TimeSpan.FromSeconds(5);
        feed.State(DictationState.Listening);
        feed.Listening(latched: false, TimeSpan.FromSeconds(1));
        Assert.Equal(DictationCopy.HudHeld, hud.Current!.Text);
        feed.Caption("next words");

        Assert.Equal("next words", hud.Current!.Caption);
    }

    /// <summary>The limit's countdown outranks a silence stop that would come
    /// after it; a silence stop that comes first is the one shown.</summary>
    [Fact]
    public void The_stop_that_comes_first_is_the_one_the_pill_shows()
    {
        var policy = DictationHoldPolicy.Windows;
        var soon = new DictationSilenceWarning(TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(5));

        Assert.StartsWith("No speech", DictationCopy.HudListening("Right Ctrl", true, TimeSpan.FromSeconds(280), policy, soon), StringComparison.Ordinal);
        Assert.Equal(
            "Listening — Stops in 0:03 · Press Right Ctrl, Space or Enter to stop",
            DictationCopy.HudListening("Right Ctrl", true, TimeSpan.FromSeconds(297), policy, soon));
    }
}
