using System.Xml.Linq;
using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The hands-free latch's words, the indicator's width for them, and the WPF
/// wiring that shows them. The wiring is pinned on the source, which is the
/// most a machine with no Windows can say: none of it has run on Windows.
/// </summary>
public sealed class HandsFreeSurfaceTests
{
    private static readonly DictationHoldPolicy Policy = DictationHoldPolicy.Windows;

    // ─── The Mac's hint, in Windows' words (DictationHUDTests) ──────────

    [Theory]
    [InlineData(0.0, "Listening — 0:00 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(0.99, "Listening — 0:00 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(65.4, "Listening — 1:05 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(239.99, "Listening — 3:59 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(240.0, "Listening — Stops in 1:00 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(240.01, "Listening — Stops in 1:00 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(250.5, "Listening — Stops in 0:50 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(299.5, "Listening — Stops in 0:01 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(300.0, "Listening — Stops in 0:00 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(301.0, "Listening — Stops in 0:00 · Press Right Ctrl, Space or Enter to stop")]
    public void The_latched_hint_floors_the_clock_and_rounds_the_countdown_up(double elapsed, string expected)
    {
        Assert.Equal(expected, DictationCopy.HudListening("Right Ctrl", latched: true, TimeSpan.FromSeconds(elapsed), Policy));
    }

    [Theory]
    [InlineData(-3.0, "0:00")]
    [InlineData(0.0, "0:00")]
    [InlineData(9.99, "0:09")]
    [InlineData(60.0, "1:00")]
    [InlineData(605.0, "10:05")]
    public void The_clock_is_whole_seconds_floored_and_never_negative(double seconds, string expected)
    {
        Assert.Equal(expected, DictationCopy.HudClock(seconds));
    }

    [Fact]
    public void The_latched_lines_split_into_Listening_and_the_hint()
    {
        var lines = DictationHudLines.For(DictationCopy.HudListening("Right Ctrl", latched: true, TimeSpan.FromSeconds(7), Policy));

        Assert.Equal("Listening", lines.Title);
        Assert.Equal("0:07 · Press Right Ctrl, Space or Enter to stop", lines.Detail);
    }

    [Fact]
    public void The_window_names_the_double_tap_and_the_stop_keys_in_Windows_words()
    {
        var key = new HotkeyBinding(0, DictationHotkeys.VkRightControl, "Hold Right Ctrl");

        Assert.EndsWith("Double-tap it to keep listening.", DictationCopy.Instructions(key), StringComparison.Ordinal);
        Assert.Equal("Dictating. Press Right Ctrl, Space or Enter to stop.", DictationCopy.Latched(key));
        Assert.DoesNotContain("Return", DictationCopy.Latched(key), StringComparison.Ordinal);
        Assert.Equal("Cancel dictation", DictationCopy.CancelLabel);
    }

    // ─── Width ──────────────────────────────────────────────────────────

    [Fact]
    public void A_state_that_names_its_lines_takes_the_width_they_need()
    {
        var latched = new DictationHudModel(
            DictationCopy.HudListening("Right Ctrl", latched: true, TimeSpan.Zero, Policy), HudTone.Neutral, null)
        {
            SizedFor = DictationCopy.HudBusyLines("Right Ctrl", Policy),
        };
        var unsized = new DictationHudModel(latched.Text, HudTone.Neutral, null);

        Assert.Equal(
            Math.Ceiling(300.4 + DictationHudGeometry.BusySpare + DictationHudGeometry.Chrome),
            DictationHudGeometry.PillWidth(latched, 300.4));
        Assert.Equal(DictationHudWidth.Max, DictationHudGeometry.PillWidth(latched, 2_000));
        Assert.Equal(DictationHudWidth.Compact, DictationHudGeometry.PillWidth(unsized, 300.4));
    }

    // ─── The WPF wiring, on the source ──────────────────────────────────

    [Fact]
    public void The_indicator_measures_every_line_it_is_sized_for_before_it_sizes_the_pill()
    {
        var show = Member(File.ReadAllText(AppFile("DictationHudWindow.xaml.cs")), "void IDictationHud.Show(");
        var measured = show.IndexOf("var widest = DictationHudGeometry.WidestLine(", StringComparison.Ordinal);
        var sized = show.IndexOf("var width = DictationHudGeometry.PillWidth(model, widest);", StringComparison.Ordinal);

        Assert.True(measured >= 0 && sized > measured, "every line is measured into the width");
        Assert.Contains("title => TextWidth(StatusLine, title), detail => TextWidth(DetailLine, detail)", show, StringComparison.Ordinal);
    }

    [Fact]
    public void The_indicator_s_second_line_has_tabular_digits()
    {
        var xaml = XDocument.Load(AppFile("DictationHudWindow.xaml"));
        var detail = xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "DetailLine");

        Assert.Equal("Tabular", (string?)detail.Attributes().Single(a => a.Name.LocalName == "Typography.NumeralAlignment"));
    }

    [Fact]
    public void The_tray_menu_shows_Cancel_from_the_state_and_the_window_cancels_through_the_controller()
    {
        // Right after Enable/Pause, as on the Mac, and only while there is
        // something to cancel (TrayMenuTests drives it through the controller).
        var idle = new DictationTrayState(TrayIcon.Ready, DictationCopy.TrayListening, DictationCopy.PauseLabel, true);
        Assert.DoesNotContain(DictationTrayItem.Cancel, DictationTrayMenu.Items(idle, learningUndoable: false));
        var latched = idle with { CanCancel = true };
        var cancel = Assert.Single(DictationTrayMenu.Entries(latched, learningUndoable: false), e => e.Item == DictationTrayItem.Cancel);
        Assert.Equal((DictationCopy.CancelLabel, true), (cancel.Label, cancel.Enabled));
        Assert.Equal(
            [DictationTrayItem.Toggle, DictationTrayItem.Cancel],
            DictationTrayMenu.Items(latched, learningUndoable: false).Take(2));

        var commands = File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.Core", "DictationTrayMenu.cs"));
        Assert.Contains(
            "case DictationTrayItem.Cancel:\n                controller.Cancel();",
            commands.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        var window = File.ReadAllText(AppFile("MainWindow.xaml.cs"));
        Assert.Contains("tray.Chosen += async (_, item) => await trayCommands.ChooseAsync(item);", Member(window, "public MainWindow("), StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_shows_the_status_line_without_announcing_it_while_the_microphone_is_open()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs"));

        // Through the one rule every live text in the window keeps
        // (DictationPracticeTests pins it): no second flag of its own.
        Assert.Contains(
            "SetLiveText(Status, state.Status);",
            Member(code, "public void Render(DictationViewState state)"),
            StringComparison.Ordinal);
        Assert.Contains("!microphoneOpen", Member(code, "private void SetLiveText("), StringComparison.Ordinal);
    }

    [Fact]
    public void The_Windows_platform_arms_the_hook_in_the_hands_free_mode_and_forwards_the_latch()
    {
        var code = File.ReadAllText(AppFile("WindowsDictationPlatform.cs")).ReplaceLineEndings("\n");

        Assert.Contains(
            "triggerMode: handsFree ? HoldKeyTriggerMode.HoldOrDoubleTapHandsFree : HoldKeyTriggerMode.HoldOnly",
            code, StringComparison.Ordinal);
        Assert.Contains("public bool IsLatched => inner.IsLatched;", code, StringComparison.Ordinal);
        Assert.Contains("public void EndLatch() => inner.EndLatch();", code, StringComparison.Ordinal);
        Assert.Contains("public LatchEnd ConsumeLatchEnd() => inner.ConsumeLatchEnd();", code, StringComparison.Ordinal);
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppFile(string name) =>
        Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", name);

    /// <summary>The member that starts with <paramref name="signature"/>, up
    /// to its closing brace.</summary>
    private static string Member(string code, string signature)
    {
        code = code.ReplaceLineEndings("\n");
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{signature}'");
        var end = code.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"no end to '{signature}'");
        return code[start..end];
    }
}
