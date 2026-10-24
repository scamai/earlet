using System.Buffers.Binary;
using System.Xml.Linq;
using Dictation.Services;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The window's status header: the glyph and headline the Mac's header shows
/// (<c>DictationStatusHeader</c>) from the function the notification-area icon
/// is drawn from (<see cref="DictationTrayGlyph"/>), one Enable/Pause button,
/// and Cancel dictation only while there is something to cancel. The macOS
/// MenuGlyphTests ("The menu bar glyph", "The menu bar glyph follows the
/// model") for the headline, scenario for scenario where Windows has the
/// state, by the Mac's names; then the button and Cancel through the real
/// controller; then the WPF half, pinned on its source only (WPF cannot run
/// here, and no one has seen the header on Windows).
/// </summary>
public sealed class WindowHeaderTests
{
    private static readonly HotkeyBinding Key = new(0, DictationHotkeys.VkRightControl, "Hold Right Ctrl");

    /// <summary>Every input off except the ones a case names: the Mac's helper.</summary>
    private static DictationGlyph State(
        bool armed = false,
        bool enabling = false,
        DictationState shown = DictationState.Idle,
        bool microphoneBlocked = false,
        HookHealth hook = HookHealth.Healthy,
        bool adminWindowInFront = false) =>
        DictationTrayGlyph.State(armed, enabling, shown, microphoneBlocked, hook, adminWindowInFront);

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    // ─── The glyph and the headline (MenuGlyphTests) ────────────────────

    [Fact]
    public void Paused_is_the_crossed_out_microphone()
    {
        var glyph = State();

        Assert.Equal(DictationGlyph.Off, glyph);
        Assert.Equal(TrayIcon.Off, DictationTrayGlyph.IconOf(glyph));
        Assert.Equal("Earlet is off", DictationTrayGlyph.Headline(glyph));
        Assert.Equal(DictationGlyphTint.Secondary, DictationTrayGlyph.Tint(glyph));
    }

    [Fact]
    public void An_enable_still_loading_the_engine_is_starting_and_the_microphone_stays_crossed_out()
    {
        var glyph = State(enabling: true);

        Assert.Equal(DictationGlyph.Starting, glyph);
        Assert.Equal(TrayIcon.Off, DictationTrayGlyph.IconOf(glyph));
        Assert.Equal("Starting dictation…", DictationTrayGlyph.Headline(glyph));
        Assert.Equal(DictationGlyphTint.Secondary, DictationTrayGlyph.Tint(glyph));
    }

    [Fact]
    public void Enabled_and_idle_is_the_outline_microphone()
    {
        var glyph = State(armed: true, shown: DictationState.Armed);

        Assert.Equal(DictationGlyph.Ready, glyph);
        Assert.Equal(TrayIcon.Ready, DictationTrayGlyph.IconOf(glyph));
        Assert.Equal("Earlet is on", DictationTrayGlyph.Headline(glyph));
        Assert.Equal(DictationGlyphTint.Positive, DictationTrayGlyph.Tint(glyph));
        // The state shown before the arm's first render is Idle: still on.
        Assert.Equal(DictationGlyph.Ready, State(armed: true));
    }

    [Fact]
    public void A_release_or_a_pause_in_progress_while_enabled_still_reads_as_on()
    {
        // The Mac's busy while enabled: an Enable flag beside an armed session
        // is not "starting".
        Assert.Equal(DictationGlyph.Ready, State(armed: true, enabling: true, shown: DictationState.Armed));
    }

    [Fact]
    public void Recording_is_the_filled_microphone()
    {
        var glyph = State(armed: true, shown: DictationState.Listening);

        Assert.Equal(DictationGlyph.Listening, glyph);
        Assert.Equal(TrayIcon.Listening, DictationTrayGlyph.IconOf(glyph));
        Assert.Equal("Listening…", DictationTrayGlyph.Headline(glyph));
        Assert.Equal(DictationGlyphTint.Recording, DictationTrayGlyph.Tint(glyph));
    }

    [Fact]
    public void Transcribing_is_the_waveform_and_so_is_Windows_typing()
    {
        var transcribing = State(armed: true, shown: DictationState.Transcribing);
        var typing = State(armed: true, shown: DictationState.Inserting);

        Assert.Equal(DictationGlyph.Transcribing, transcribing);
        Assert.Equal(DictationGlyph.Typing, typing);
        Assert.Equal(TrayIcon.Transcribing, DictationTrayGlyph.IconOf(transcribing));
        Assert.Equal(TrayIcon.Transcribing, DictationTrayGlyph.IconOf(typing));
        Assert.Equal("Transcribing…", DictationTrayGlyph.Headline(transcribing));
        Assert.Equal("Typing…", DictationTrayGlyph.Headline(typing));
        Assert.Equal(DictationGlyphTint.Accent, DictationTrayGlyph.Tint(transcribing));
        Assert.Equal(DictationGlyphTint.Accent, DictationTrayGlyph.Tint(typing));
    }

    [Fact]
    public void A_missing_grant_draws_the_warning_triangle()
    {
        // Windows' one grant is the microphone; the headline names it where
        // the Mac's says "Needs permission".
        var glyph = State(armed: true, shown: DictationState.Armed, microphoneBlocked: true);

        Assert.Equal(DictationGlyph.MicrophoneBlocked, glyph);
        Assert.Equal(TrayIcon.MicrophoneBlocked, DictationTrayGlyph.IconOf(glyph));
        Assert.Equal("Microphone blocked", DictationTrayGlyph.Headline(glyph));
        Assert.Equal(DictationGlyphTint.Attention, DictationTrayGlyph.Tint(glyph));
    }

    [Fact]
    public void Recording_beats_needs_attention_a_live_hold_proves_the_grants_were_there()
    {
        Assert.Equal("Listening…", DictationTrayGlyph.Headline(State(armed: true, shown: DictationState.Listening, microphoneBlocked: true)));
        Assert.Equal("Transcribing…", DictationTrayGlyph.Headline(State(armed: true, shown: DictationState.Transcribing, microphoneBlocked: true)));
        Assert.Equal("Typing…", DictationTrayGlyph.Headline(State(armed: true, shown: DictationState.Inserting, microphoneBlocked: true)));
    }

    [Fact]
    public void Needs_attention_beats_paused_and_ready_so_a_broken_setup_never_looks_fine()
    {
        Assert.Equal(DictationGlyph.MicrophoneBlocked, State(microphoneBlocked: true));
        Assert.Equal(DictationGlyph.MicrophoneBlocked, State(enabling: true, microphoneBlocked: true));
        Assert.Equal(DictationGlyph.MicrophoneBlocked, State(armed: true, shown: DictationState.Armed, microphoneBlocked: true));
    }

    [Fact]
    public void Paused_ready_and_needs_attention_are_three_different_icons_and_every_headline_tells_its_state_apart()
    {
        var pictures = new[] { DictationGlyph.Off, DictationGlyph.Ready, DictationGlyph.MicrophoneBlocked }
            .Select(DictationTrayGlyph.IconOf).ToHashSet();
        Assert.Equal(3, pictures.Count);

        // The Mac's "VoiceOver tells every state apart": the headline is what
        // a reader hears, the glyph being a picture only.
        var headlines = Enum.GetValues<DictationGlyph>().Select(DictationTrayGlyph.Headline).ToHashSet();
        Assert.Equal(Enum.GetValues<DictationGlyph>().Length, headlines.Count);
    }

    [Fact]
    public void The_headline_fits_one_short_line_for_every_state()
    {
        // The Mac's menu-headline limit, kept for the window's one line.
        foreach (var glyph in Enum.GetValues<DictationGlyph>())
        {
            var line = DictationTrayGlyph.Headline(glyph);
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.True(line.Length <= 40, line);
        }
    }

    [Fact]
    public void The_icon_is_drawn_from_the_same_state_for_every_input()
    {
        // The icon's picture and tooltip are functions of State alone: one
        // decision, so the icon and the header cannot disagree. That the
        // controller hands both the same State is AssertWindowAndIconAgree's.
        foreach (var armed in new[] { false, true })
        foreach (var enabling in new[] { false, true })
        foreach (var shown in Enum.GetValues<DictationState>())
        foreach (var blocked in new[] { false, true })
        foreach (var hook in Enum.GetValues<HookHealth>())
        foreach (var admin in new[] { false, true })
        {
            var glyph = DictationTrayGlyph.State(armed, enabling, shown, blocked, hook, admin);
            Assert.Equal(
                (DictationTrayGlyph.IconOf(glyph), DictationTrayGlyph.ToolTipOf(glyph, Key)),
                DictationTrayGlyph.Picture(glyph, Key));
        }
    }

    // ─── The glyph's picture is the icon's ──────────────────────────────

    [Theory]
    [InlineData(TrayIcon.Off)]
    [InlineData(TrayIcon.Ready)]
    [InlineData(TrayIcon.Listening)]
    [InlineData(TrayIcon.Transcribing)]
    [InlineData(TrayIcon.MicrophoneBlocked)]
    public void The_header_glyph_is_the_icon_s_own_picture(TrayIcon picture)
    {
        // The 32-pixel image of the icon, whose alpha is coverage times the
        // ink's opacity: full for every picture but off's, which is faded.
        var ico = TrayIconArt.Render(picture, lightTaskbar: true);
        var entry = 6 + 16 * (TrayIconArt.Sizes.Count - 1);
        Assert.Equal(32, ico[entry]);
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 12)) + 40;
        var mask = TrayIconArt.Mask(picture, 32);
        var opacity = picture == TrayIcon.Off ? TrayIconArt.PausedAlpha / 255.0 : 1.0;

        Assert.Equal(32 * 32, mask.Length);
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                // The icon is stored bottom-up; the mask top row first.
                var alpha = ico[offset + ((31 - y) * 32 + x) * 4 + 3];
                // Off's fade is rounded once there and twice here.
                Assert.InRange(Math.Abs(alpha - Math.Round(mask[y * 32 + x] * opacity)), 0, picture == TrayIcon.Off ? 1 : 0);
            }
        }

        Assert.Contains(mask, a => a == 255);
        Assert.Equal(0, mask[0]);
    }

    [Fact]
    public void The_microphone_the_waveform_and_the_warning_are_three_shapes_and_the_tint_tells_the_microphones_apart()
    {
        // Off, ready and listening are the icon's one microphone, told apart
        // by the icon's fade and red and by the header's tint; the waveform
        // and the triangle are shapes of their own.
        var microphone = TrayIconArt.Mask(TrayIcon.Ready, 32);
        Assert.Equal(microphone, TrayIconArt.Mask(TrayIcon.Off, 32));
        Assert.Equal(microphone, TrayIconArt.Mask(TrayIcon.Listening, 32));
        var shapes = new[] { TrayIcon.Ready, TrayIcon.Transcribing, TrayIcon.MicrophoneBlocked }
            .Select(p => Convert.ToBase64String(TrayIconArt.Mask(p, 32))).ToHashSet();
        Assert.Equal(3, shapes.Count);
        var tints = new[] { DictationGlyph.Off, DictationGlyph.Ready, DictationGlyph.Listening }
            .Select(DictationTrayGlyph.Tint).ToHashSet();
        Assert.Equal(3, tints.Count);

        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconArt.Mask(TrayIcon.Ready, 0));
    }

    // ─── Through the controller (ModelMenuGlyphTests) ───────────────────

    /// <summary>Every render: the window's glyph is the icon's state, and the
    /// window's one button is the menu's Enable/Pause item.</summary>
    private static void AssertWindowAndIconAgree(Rig rig)
    {
        foreach (var view in rig.View.States)
        {
            var header = view.Header;
            Assert.Equal(DictationTrayGlyph.IconOf(header.Glyph), view.Tray.Icon);
            Assert.Equal(DictationTrayGlyph.ToolTipOf(header.Glyph, rig.Controller.HoldKey), view.Tray.ToolTip);
            Assert.Equal(DictationTrayGlyph.Headline(header.Glyph), header.Headline);
            Assert.Equal(view.Tray.ToggleLabel, header.ToggleLabel);
            Assert.Equal(view.Tray.CanToggle, header.CanToggle);
            Assert.Equal(header.ToggleEnables ? DictationCopy.EnableLabel : DictationCopy.PauseLabel, header.ToggleLabel);
            // One button: Enable and Pause are never both live, and the one it
            // offers is live exactly when the button is.
            Assert.False(view.CanEnable && view.CanPause);
            Assert.Equal(header.CanToggle, view.CanEnable || view.CanPause);
        }
    }

    [Fact]
    public async Task Paused_starting_on_listening_transcribing_typing_and_back_to_on_through_the_controller()
    {
        using var rig = new Rig();
        using var load = new ManualResetEventSlim();
        using var decode = new ManualResetEventSlim();
        using var paste = new ManualResetEventSlim();
        Assert.Equal(DictationGlyph.Off, rig.View.Last.Header.Glyph);
        Assert.Equal((DictationCopy.EnableLabel, true, true),
            (rig.View.Last.Header.ToggleLabel, rig.View.Last.Header.ToggleEnables, rig.View.Last.Header.CanToggle));

        rig.Platform.Worker.HoldInit = load;
        var enabling = rig.Controller.EnableAsync();
        Assert.Equal(DictationGlyph.Starting, rig.View.Last.Header.Glyph);
        Assert.Equal("Starting dictation…", rig.View.Last.Header.Headline);
        Assert.False(rig.View.Last.Header.CanToggle);
        load.Set();
        await enabling;
        rig.Dispatcher.Drain();
        Assert.Equal(DictationGlyph.Ready, rig.View.Last.Header.Glyph);
        Assert.Equal((DictationCopy.PauseLabel, false, true),
            (rig.View.Last.Header.ToggleLabel, rig.View.Last.Header.ToggleEnables, rig.View.Last.Header.CanToggle));
        Assert.False(rig.View.Last.CanCancel);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationGlyph.Listening, rig.View.Last.Header.Glyph);
        Assert.False(rig.View.Last.Header.CanToggle);
        Assert.True(rig.View.Last.CanCancel);

        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Platform.Paste.Hold = paste;
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Header.Glyph == DictationGlyph.Transcribing);
        Assert.Equal("Transcribing…", rig.View.Last.Header.Headline);
        Assert.False(rig.View.Last.Header.CanToggle);
        Assert.True(rig.View.Last.CanCancel);

        decode.Set();
        Assert.True(rig.Platform.Paste.Entered.Wait(TimeSpan.FromSeconds(10)), "the paste never started");
        rig.DrainUntil(() => rig.View.Last.Header.Glyph == DictationGlyph.Typing);
        Assert.Equal("Typing…", rig.View.Last.Header.Headline);
        Assert.False(rig.View.Last.Header.CanToggle);
        Assert.False(rig.View.Last.CanCancel);

        paste.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(DictationGlyph.Ready, rig.View.Last.Header.Glyph);
        Assert.True(rig.View.Last.Header.CanToggle);
        Assert.False(rig.View.Last.CanCancel);

        Assert.True(rig.Controller.TryPause());
        Assert.Equal(DictationGlyph.Off, rig.View.Last.Header.Glyph);
        Assert.Equal((DictationCopy.EnableLabel, true, true),
            (rig.View.Last.Header.ToggleLabel, rig.View.Last.Header.ToggleEnables, rig.View.Last.Header.CanToggle));
        AssertWindowAndIconAgree(rig);
    }

    [Fact]
    public async Task With_the_microphone_blocked_the_header_warns_on_or_off_until_access_is_back()
    {
        // The Mac's "a grant revoked while enabled turns the icon into the
        // warning": the header says it with the icon, and a pause does not
        // hide it.
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.True(rig.Controller.IsEnabled, "on; only the header and the icon can say it does not work");
        Assert.Equal(DictationGlyph.MicrophoneBlocked, rig.View.Last.Header.Glyph);
        Assert.Equal("Microphone blocked", rig.View.Last.Header.Headline);
        Assert.Equal(DictationCopy.PauseLabel, rig.View.Last.Header.ToggleLabel);

        Assert.True(rig.Controller.TryPause());
        Assert.Equal(DictationGlyph.MicrophoneBlocked, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationCopy.EnableLabel, rig.View.Last.Header.ToggleLabel);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(DictationGlyph.Off, rig.View.Last.Header.Glyph);
        await rig.EnableAsync();
        Assert.Equal(DictationGlyph.Ready, rig.View.Last.Header.Glyph);
        AssertWindowAndIconAgree(rig);
    }

    [Fact]
    public async Task A_failed_enable_reads_as_off_and_the_status_line_says_why()
    {
        // The Mac publishes no engine-failed flag: off, with the sentence.
        using var rig = new Rig();
        rig.Platform.Worker.InitSucceeds = false;
        await rig.EnableAsync();

        Assert.Equal(DictationGlyph.Off, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
        Assert.True(rig.View.Last.Header.CanToggle);
        Assert.True(rig.View.Last.Header.ToggleEnables);
    }

    [Fact]
    public async Task Cancel_takes_the_dictation_and_itself_away_and_Pause_comes_back()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Controller.Cancel();
        rig.Release();
        rig.Dispatcher.Drain();

        Assert.Equal(DictationGlyph.Ready, rig.View.Last.Header.Glyph);
        Assert.False(rig.View.Last.CanCancel, "nothing left to cancel");
        Assert.True(rig.View.Last.Header.CanToggle);
        AssertWindowAndIconAgree(rig);
    }

    // ─── The WPF half: pins on the source, not evidence it behaves ──────

    [Fact]
    public void The_header_is_the_first_thing_in_the_window_with_the_glyph_the_headline_the_status_and_the_buttons()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var header = Named(xaml, "HeaderSection");
        var column = header.Parent!.Elements().ToList();

        Assert.Equal(0, column.IndexOf(header));
        // No large "Dictation" title: the title bar says it, as on the Mac.
        Assert.DoesNotContain(xaml.Descendants(), e => (string?)e.Attribute("Text") == "Dictation");

        var glyph = Named(xaml, "StateGlyph");
        Assert.Equal("Rectangle", glyph.Name.LocalName);
        Assert.Same(header, glyph.Parent);
        Assert.Equal("False", (string?)glyph.Attribute("IsHitTestVisible"));

        var lines = Named(xaml, "Headline").Parent!;
        Assert.Same(header, lines.Parent);
        Assert.Equal(
            ["Headline", "Status", "MicrophoneActions"],
            lines.Elements().Take(3).Select(e => (string?)e.Attribute(X + "Name")));
        // The headline is not a live region: the status line is the voice.
        Assert.DoesNotContain(Named(xaml, "Headline").Attributes(), a => a.Name.LocalName == "AutomationProperties.LiveSetting");
        Assert.Equal("Polite", (string?)Named(xaml, "Status").Attributes().Single(a => a.Name.LocalName == "AutomationProperties.LiveSetting"));

        var toggle = Named(xaml, "EnablePauseButton");
        var cancel = Named(xaml, "CancelButton");
        Assert.Same(toggle.Parent, cancel.Parent);
        Assert.Same(lines, toggle.Parent!.Parent);
        Assert.Equal("EnablePauseClick", (string?)toggle.Attribute("Click"));
        Assert.Equal("CancelClick", (string?)cancel.Attribute("Click"));
        Assert.Equal("Collapsed", (string?)cancel.Attribute("Visibility"));
        // One button: the two permanent ones are gone.
        Assert.DoesNotContain(xaml.Descendants(), e => (string?)e.Attribute(X + "Name") is "EnableButton" or "PauseButton");
    }

    [Fact]
    public void The_header_is_drawn_from_every_render_and_Cancel_shows_only_while_there_is_something_to_cancel()
    {
        var window = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var render = Member(window, "public void Render(DictationViewState state)");

        Assert.Contains("RenderHeader(state.Header);", render, StringComparison.Ordinal);
        Assert.Contains("CancelButton.IsEnabled = state.CanCancel;", render, StringComparison.Ordinal);
        Assert.Contains(
            "CancelButton.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;", render, StringComparison.Ordinal);
        Assert.Contains("private void CancelClick(object sender, RoutedEventArgs e) => controller.Cancel();", window, StringComparison.Ordinal);

        var code = File.ReadAllText(AppFile("MainWindow.Header.cs")).ReplaceLineEndings("\n");
        var draw = Member(code, "private void RenderHeader(");
        Assert.Contains("RenderGlyph(state.Glyph);", draw, StringComparison.Ordinal);
        Assert.Contains("Headline.Text = state.Headline;", draw, StringComparison.Ordinal);
        Assert.Contains("EnablePauseButton.Content = WithAccessKey(state.ToggleLabel, state.ToggleLabel[..1]);", draw, StringComparison.Ordinal);
        Assert.Contains("EnablePauseButton.IsEnabled = state.CanToggle;", draw, StringComparison.Ordinal);
        Assert.Contains("CancelButton.Content = WithAccessKey(DictationCopy.CancelLabel, \"C\");", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_glyph_is_the_icon_s_picture_in_the_mac_s_tint_or_the_icon_s_own_drawing_in_high_contrast()
    {
        var code = File.ReadAllText(AppFile("MainWindow.Header.cs")).ReplaceLineEndings("\n");
        var glyph = Member(code, "private void RenderGlyph(");

        Assert.Contains("var picture = DictationTrayGlyph.IconOf(glyph);", glyph, StringComparison.Ordinal);
        // High Contrast first, as the icon draws it there: its shapes and its
        // system colours (WindowHeaderIntegrationTests); the Mac's tints otherwise.
        var contrast = glyph.IndexOf("if (highContrast)", StringComparison.Ordinal);
        var text = glyph.IndexOf("SystemColors.WindowTextBrushKey", StringComparison.Ordinal);
        var tint = glyph.IndexOf("switch (DictationTrayGlyph.Tint(glyph))", StringComparison.Ordinal);
        Assert.True(contrast >= 0 && text > contrast && tint > text, "High Contrast first, in the icon's system colours");
        foreach (var tone in Enum.GetValues<DictationGlyphTint>().Where(t => t != DictationGlyphTint.Secondary))
        {
            Assert.Contains($"case DictationGlyphTint.{tone}:", glyph, StringComparison.Ordinal);
        }

        Assert.Contains("TrayIconArt.Mask(picture, GlyphPixels, byShape)", Member(code, "private ImageBrush MaskFor("), StringComparison.Ordinal);
        // A High Contrast switch repaints the glyph at once.
        var labels = Member(code, "private void ShowHeaderAndTroubleshootingLabels()");
        Assert.Contains("SystemParameters.StaticPropertyChanged +=", labels, StringComparison.Ordinal);
        Assert.Contains("nameof(SystemParameters.HighContrast)", labels, StringComparison.Ordinal);
        Assert.Contains(
            "ShowHeaderAndTroubleshootingLabels();",
            Member(File.ReadAllText(AppFile("MainWindow.xaml.cs")), "public MainWindow("),
            StringComparison.Ordinal);
    }
}
