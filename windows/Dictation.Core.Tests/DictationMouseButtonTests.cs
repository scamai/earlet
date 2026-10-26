using System.Text.Json;
using System.Xml.Linq;
using Dictation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// The side mouse button: none by default, chosen in the window's "Mouse
/// button" picker under the hold key, remembered in Dictation's own settings,
/// handed to the hook beside the key, and named by the window and the
/// indicator whenever it is what holds the dictation.
/// </summary>
public sealed class DictationMouseButtonTests
{
    // ─── The setting ────────────────────────────────────────────────────

    [Fact]
    public async Task A_fresh_install_binds_no_button()
    {
        using var rig = new Rig();

        await rig.EnableAsync();

        Assert.Equal(DictationMouseButton.None, rig.Controller.MouseButton);
        Assert.Equal(DictationMouseButton.None, rig.Platform.Trigger.MouseButton);
        Assert.Equal(DictationMouseButton.None, rig.View.Last.MouseButton);
        Assert.True(rig.View.Last.CanChooseMouseButton);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
    }

    [Fact]
    public void The_picker_offers_none_and_both_side_buttons()
    {
        using var rig = new Rig();

        Assert.Equal(DictationMouseButtons.Choices, rig.Controller.MouseButtonChoices);
    }

    [Fact]
    public void A_chosen_button_is_saved_by_id_and_read_back()
    {
        using var rig = new Rig();

        rig.Controller.ChooseMouseButton(DictationMouseButton.Button4);

        Assert.Equal(DictationMouseButton.Button4, rig.View.Last.MouseButton);
        var file = JsonDocument.Parse(File.ReadAllText(rig.Settings.Path)).RootElement;
        Assert.Equal("button4", file.GetProperty("mouse_button").GetString());

        using var next = new Rig(beforeStart: r => r.Settings.Save(rig.Settings.Load()));
        Assert.Equal(DictationMouseButton.Button4, next.Controller.MouseButton);
    }

    [Theory]
    [InlineData(DictationMouseButton.None)]
    [InlineData(DictationMouseButton.Button4)]
    [InlineData(DictationMouseButton.Button5)]
    public void The_store_round_trips_every_offered_button(DictationMouseButton button)
    {
        using var copy = new InstalledCopy();
        var store = new DictationSettingsStore(
            Path.Combine(copy.Root, "settings.json"), NullLogger<DictationSettingsStore>.Instance);

        Assert.True(store.Save(new DictationSettings(MouseButton: button)));

        Assert.Equal(button, store.Load().MouseButton);
    }

    [Theory]
    [InlineData("{\"mouse_button\": \"button9\"}")]
    [InlineData("{\"mouse_button\": 4}")]
    [InlineData("{\"hold_virtual_key\": 165}")]
    public void An_unknown_or_missing_button_reads_as_none(string json)
    {
        // Never a hook the user did not ask for: a hand-edited, downgraded or
        // older file binds nothing.
        using var copy = new InstalledCopy();
        var path = Path.Combine(copy.Root, "settings.json");
        Directory.CreateDirectory(copy.Root);
        File.WriteAllText(path, json);
        var store = new DictationSettingsStore(path, NullLogger<DictationSettingsStore>.Instance);

        Assert.Equal(DictationMouseButton.None, store.Load().MouseButton);
    }

    [Fact]
    public void A_button_that_cannot_be_saved_still_applies_and_says_it_will_not_last()
    {
        using var rig = new Rig(beforeStart: r => Directory.CreateDirectory(r.Settings.Path));

        rig.Controller.ChooseMouseButton(DictationMouseButton.Button5);

        Assert.Equal(DictationMouseButton.Button5, rig.Controller.MouseButton);
        Assert.Equal(DictationCopy.MouseButtonNotSaved, rig.View.Last.Status);
    }

    // ─── Choosing one ───────────────────────────────────────────────────

    [Fact]
    public async Task Choosing_a_button_while_enabled_re_arms_the_hook_with_it_and_keeps_the_worker()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var first = rig.Platform.Trigger;

        rig.Controller.ChooseMouseButton(DictationMouseButton.Button5);

        Assert.True(first.Disposed);
        Assert.Equal(DictationMouseButton.Button5, rig.Platform.Trigger.MouseButton);
        Assert.True(rig.Platform.Trigger.Registered);
        // The key is untouched: the button is a second trigger, not a new one.
        Assert.Equal(first.Binding, rig.Platform.Trigger.Binding);
        Assert.Equal("Enabled. Hold Right Ctrl or mouse button 5 in a text field.", rig.View.Last.Status);
        Assert.Single(rig.Platform.Worker.Ops(), op => op == "init");

        // And going back to none takes the hook's mouse half away again.
        rig.Controller.ChooseMouseButton(DictationMouseButton.None);
        Assert.Equal(DictationMouseButton.None, rig.Platform.Trigger.MouseButton);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
    }

    [Fact]
    public async Task Choosing_the_button_already_bound_does_not_re_arm()
    {
        using var rig = new Rig(beforeStart: r => r.Settings.Save(new DictationSettings(MouseButton: DictationMouseButton.Button4)));
        await rig.EnableAsync();

        rig.Controller.ChooseMouseButton(DictationMouseButton.Button4);

        Assert.Single(rig.Platform.Triggers);
        Assert.False(rig.Platform.Trigger.Disposed);
    }

    [Fact]
    public async Task The_button_cannot_change_under_a_held_dictation()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        Assert.False(rig.View.Last.CanChooseMouseButton);
        rig.Controller.ChooseMouseButton(DictationMouseButton.Button4);

        Assert.Single(rig.Platform.Triggers);
        Assert.Equal(DictationMouseButton.None, rig.Controller.MouseButton);
    }

    [Fact]
    public void A_value_the_picker_does_not_offer_is_ignored()
    {
        using var rig = new Rig();

        rig.Controller.ChooseMouseButton((DictationMouseButton)99);

        Assert.Equal(DictationMouseButton.None, rig.Controller.MouseButton);
        Assert.False(File.Exists(rig.Settings.Path));
    }

    // ─── What the window and the indicator say ──────────────────────────

    [Fact]
    public async Task The_notice_under_the_picker_states_the_gesture_and_the_cost()
    {
        using var rig = new Rig(beforeStart: r => r.Settings.Save(new DictationSettings(MouseButton: DictationMouseButton.Button4)));
        await rig.EnableAsync();

        var notice = rig.View.Last.MouseButtonNotice!;
        Assert.Contains("click it again to stop", notice, StringComparison.Ordinal);
        Assert.Contains("no longer goes Back", notice, StringComparison.Ordinal);
        Assert.DoesNotContain(DictationCopy.MouseButtonRefused, notice, StringComparison.Ordinal);
        Assert.Equal("Enabled. Hold Right Ctrl or mouse button 4 in a text field.", rig.View.Last.Status);
    }

    [Fact]
    public async Task A_refused_mouse_hook_keeps_the_key_working_and_says_the_button_does_nothing()
    {
        using var rig = new Rig(beforeStart: r =>
        {
            r.Settings.Save(new DictationSettings(MouseButton: DictationMouseButton.Button5));
            r.Platform.MouseHookRefused = true;
        });

        await rig.EnableAsync();

        // Enabled, not failed: the key is the trigger the user has always had.
        Assert.True(rig.Controller.IsEnabled);
        Assert.True(rig.Platform.Trigger.Registered);
        Assert.EndsWith(DictationCopy.MouseButtonRefused, rig.View.Last.MouseButtonNotice, StringComparison.Ordinal);

        rig.Dictate(state => state.Status.StartsWith("Text inserted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_hold_beside_a_bound_button_names_the_key()
    {
        using var rig = new Rig(beforeStart: r => r.Settings.Save(new DictationSettings(MouseButton: DictationMouseButton.Button4)));
        await rig.EnableAsync();

        rig.Press();
        rig.Dispatcher.Drain();

        Assert.Equal("Listening… Release Right Ctrl to insert.", rig.View.Last.Status);
    }

    [Fact]
    public async Task A_mouse_hold_names_the_button_and_a_click_latch_says_how_to_stop_it()
    {
        using var rig = new Rig(beforeStart: r => r.Settings.Save(new DictationSettings(MouseButton: DictationMouseButton.Button4)));
        await rig.EnableAsync();
        var trigger = rig.Platform.Trigger;

        trigger.MouseHolds = true;
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal("Listening… Release mouse button 4 to insert.", rig.View.Last.Status);

        // A click: the hook reads the button as held through the latch.
        trigger.Latched = true;
        trigger.MouseLatched = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Equal(DictationCopy.LatchedByMouse(DictationMouseButton.Button4), rig.View.Last.Status);
        Assert.Equal("Dictating. Click mouse button 4 again to stop.", rig.View.Last.Status);
        Assert.DoesNotContain("Space", rig.View.Last.Status, StringComparison.Ordinal);
        Assert.StartsWith("Listening — 0:00 · Click mouse button 4 to stop", rig.Hud.Current!.Text, StringComparison.Ordinal);

        // Another press of the button ends it, and the words go in.
        trigger.Latched = false;
        trigger.MouseLatched = false;
        trigger.MouseHolds = false;
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
    }

    [Fact]
    public void The_mouse_hold_copy_falls_back_to_the_key_with_no_button_bound()
    {
        var key = Dictation.Services.DictationHotkeys.PushToTalk;

        Assert.Equal(
            $"Listening… Release {key.KeyName} to insert.",
            DictationCopy.ForState(DictationState.Listening, key, DictationMouseButton.None, mouseHolds: true));
        Assert.Equal(DictationCopy.Armed(key), DictationCopy.Armed(key, DictationMouseButton.None));
    }

    // ─── The WPF wiring, pinned on the source ───────────────────────────
    //
    // None of this has run on Windows from here: what is asserted is that the
    // window and the platform say what the tests above exercise.

    [Fact]
    public void The_Windows_platform_hands_the_button_to_the_hook_and_forwards_its_state()
    {
        var code = File.ReadAllText(AppFile("WindowsDictationPlatform.cs")).ReplaceLineEndings("\n");

        Assert.Contains("mouseButton: mouseButton,", code, StringComparison.Ordinal);
        Assert.Contains("public bool MouseHolds => inner.MouseHolds;", code, StringComparison.Ordinal);
        Assert.Contains("public bool MouseLatched => inner.MouseLatched;", code, StringComparison.Ordinal);
        Assert.Contains("public bool MouseTriggerDegraded => inner.MouseTriggerDegraded;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_offers_the_picker_under_the_hold_key_and_wires_it()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var picker = Named(xaml, "MouseButtonPicker");
        var holdKey = Named(xaml, "HoldKeyPicker");

        Assert.Same(holdKey.Parent, picker.Parent);
        Assert.Equal(int.Parse((string)holdKey.Attribute("Grid.Row")!) + 1, int.Parse((string)picker.Attribute("Grid.Row")!));
        Assert.Equal("MouseButtonChanged", (string?)picker.Attribute("SelectionChanged"));
        Assert.Equal("Label", (string?)picker.Attribute("DisplayMemberPath"));
        Assert.NotNull(Named(xaml, "MouseButtonNotice"));

        // Every row of the grid holds its own controls.
        var rows = picker.Parent!.Elements()
            .Where(e => e.Attribute("Grid.Row") is not null && (string?)e.Attribute("Grid.Column") == "1")
            .Select(e => (string)e.Attribute("Grid.Row")!)
            .ToArray();
        Assert.Equal(rows.Length, rows.Distinct().Count());

        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        Assert.Contains("controller.ChooseMouseButton(choice.Value);", code, StringComparison.Ordinal);
        Assert.Contains("MouseButtonPicker.ItemsSource = controller.MouseButtonChoices;", code, StringComparison.Ordinal);
        Assert.Contains("MouseButtonPicker.IsEnabled = state.CanChooseMouseButton;", code, StringComparison.Ordinal);
        Assert.Contains("SetLiveText(MouseButtonNotice, state.MouseButtonNotice", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mouse_button_label_has_an_access_key_no_other_control_uses()
    {
        var xaml = File.ReadAllText(AppFile("MainWindow.xaml"));
        var code = string.Concat(Directory.GetFiles(Path.GetDirectoryName(AppFile("MainWindow.xaml"))!, "MainWindow*.cs")
            .Select(File.ReadAllText));

        Assert.Contains("Content=\"Mouse butto_n:\"", xaml, StringComparison.Ordinal);
        var others = System.Text.RegularExpressions.Regex.Matches(xaml, "Content=\"[^\"]*_([A-Za-z])")
            .Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Concat(System.Text.RegularExpressions.Regex.Matches(code, "WithAccessKey\\([^,]+, \"([A-Za-z])\"\\)")
                .Select(m => m.Groups[1].Value.ToLowerInvariant()))
            .ToList();
        Assert.Single(others, key => key == "n");
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Named(XDocument xaml, string name) =>
        xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string AppFile(string name) =>
        Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", name);
}
