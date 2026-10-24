using System.Text.Json;
using Dictation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// The hold key: Right Ctrl by default, chosen in the window, remembered in
/// Dictation's own settings, overridable by <c>DICTATION_HOLD_KEY</c>, and
/// accompanied by key-specific and configuration advice.
/// </summary>
public sealed class DictationHoldKeyTests
{
    [Fact]
    public async Task A_fresh_install_holds_Right_Ctrl()
    {
        using var rig = new Rig();

        await rig.EnableAsync();

        Assert.Equal(DictationHotkeys.VkRightControl, rig.Platform.Trigger.Binding.VirtualKey);
        Assert.Equal(DictationHotkeys.VkRightControl, rig.View.Last.HoldKey.VirtualKey);
        Assert.Contains("Hold Right Ctrl", rig.View.Last.Instructions, StringComparison.Ordinal);
        Assert.True(rig.View.Last.CanChooseHoldKey);
    }

    [Fact]
    public void The_picker_offers_the_whole_list()
    {
        using var rig = new Rig();

        Assert.Equal(DictationHotkeys.HoldChoices, rig.Controller.HoldKeyChoices);
    }

    [Fact]
    public void A_chosen_key_is_saved_in_Dictation_s_settings_file_and_read_back()
    {
        using var rig = new Rig();

        rig.Controller.ChooseHoldKey(DictationHotkeys.VkF13);

        Assert.Equal(DictationHotkeys.VkF13, rig.View.Last.HoldKey.VirtualKey);
        var file = JsonDocument.Parse(File.ReadAllText(rig.Settings.Path)).RootElement;
        Assert.Equal(DictationHotkeys.VkF13, file.GetProperty("hold_virtual_key").GetUInt32());

        using var next = new Rig(beforeStart: r => r.Settings.Save(rig.Settings.Load()));
        Assert.Equal(DictationHotkeys.VkF13, next.Controller.HoldKey.VirtualKey);
    }

    [Fact]
    public void The_settings_path_is_Dictation_s_own()
    {
        var paths = new DictationPaths(Path.Combine("C:", "Users", "u", "AppData", "Local", "Dictation"));

        Assert.EndsWith(Path.Combine("Dictation", "settings.json"), paths.SettingsFile, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("Dictation", "logs"), paths.LogDirectory, StringComparison.Ordinal);
        Assert.Equal(Path.Combine("C:", "Users", "u", "AppData", "Local", "Dictation", "settings.json"),
            paths.SettingsFile);
    }

    [Fact]
    public async Task Choosing_a_key_while_enabled_moves_the_hook_and_keeps_the_worker()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var first = rig.Platform.Trigger;

        rig.Controller.ChooseHoldKey(DictationHotkeys.VkF13);

        Assert.True(first.Disposed);
        Assert.Equal(DictationHotkeys.VkF13, rig.Platform.Trigger.Binding.VirtualKey);
        Assert.True(rig.Platform.Trigger.Registered);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.Single(rig.Platform.Worker.Ops(), op => op == "init");

        // And the new key dictates.
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_key_cannot_change_under_a_held_dictation()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        Assert.False(rig.View.Last.CanChooseHoldKey);
        rig.Controller.ChooseHoldKey(DictationHotkeys.VkF13);

        Assert.Single(rig.Platform.Triggers);
        Assert.Equal(DictationHotkeys.VkRightControl, rig.Controller.HoldKey.VirtualKey);
    }

    [Fact]
    public void The_override_wins_and_locks_the_picker()
    {
        using var rig = new Rig(
            environment: name => name == DictationEnvironment.HoldKey ? "0x7C" : null,
            beforeStart: r => r.Settings.Save(new DictationSettings(DictationHotkeys.VkRightAlt)));

        Assert.Equal(DictationHotkeys.VkF13, rig.View.Last.HoldKey.VirtualKey);
        Assert.False(rig.View.Last.CanChooseHoldKey);
        Assert.Contains("DICTATION_HOLD_KEY", rig.View.Last.HoldKeyNotice, StringComparison.Ordinal);

        rig.Controller.ChooseHoldKey(DictationHotkeys.VkLeftAlt);
        Assert.Equal(DictationHotkeys.VkF13, rig.Controller.HoldKey.VirtualKey);
    }

    [Fact]
    public void An_unreadable_settings_file_is_the_defaults()
    {
        using var rig = new Rig(beforeStart: r =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r.Settings.Path)!);
            File.WriteAllText(r.Settings.Path, "{ not json");
        });

        Assert.Equal(DictationHotkeys.VkRightControl, rig.Controller.HoldKey.VirtualKey);
    }

    [Fact]
    public void A_key_that_cannot_be_saved_still_applies_and_says_it_will_not_last()
    {
        using var rig = new Rig(beforeStart: r => Directory.CreateDirectory(r.Settings.Path));

        rig.Controller.ChooseHoldKey(DictationHotkeys.VkF13);

        Assert.Equal(DictationHotkeys.VkF13, rig.Controller.HoldKey.VirtualKey);
        Assert.Equal(DictationCopy.HoldKeyNotSaved, rig.View.Last.Status);
    }

    // ─── Hold-key advice ──────────────────────────────────────────────

    [Fact]
    public void Choosing_a_lock_key_explains_its_effect()
    {
        using var rig = new Rig();
        rig.Controller.ChooseHoldKey(DictationHotkeys.VkCapsLock);

        Assert.Equal(DictationHotkeys.CautionFor(DictationHotkeys.VkCapsLock),
            rig.View.Last.HoldKeyNotice);
    }

    [Fact]
    public void A_key_without_a_caution_has_no_notice()
    {
        using var rig = new Rig();
        rig.Controller.ChooseHoldKey(DictationHotkeys.VkF13);

        Assert.Null(rig.View.Last.HoldKeyNotice);
    }

    [Fact]
    public void An_environment_override_and_key_caution_are_both_shown_once()
    {
        var key = DictationHotkeys.HoldChoices.Single(c => c.VirtualKey == DictationHotkeys.VkCapsLock);
        var notice = HoldKeyAdvice.For(key, fromEnvironment: true)!;
        var caution = DictationHotkeys.CautionFor(key.VirtualKey)!;

        Assert.StartsWith("DICTATION_HOLD_KEY sets the key; clear it to choose here.", notice);
        Assert.EndsWith(caution, notice);
        Assert.Equal(1, notice.Split(caution).Length - 1);
    }

    [Fact]
    public async Task Enabling_preserves_the_chosen_keys_advice()
    {
        using var rig = new Rig();
        rig.Controller.ChooseHoldKey(DictationHotkeys.VkCapsLock);
        var notice = rig.View.Last.HoldKeyNotice;

        await rig.EnableAsync();

        Assert.Equal(notice, rig.View.Last.HoldKeyNotice);
    }

    [Fact]
    public void The_settings_store_round_trips()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-settings-").FullName;
        var store = new DictationSettingsStore(Path.Combine(dir, "settings.json"),
            NullLogger<DictationSettingsStore>.Instance);

        Assert.Equal(new DictationSettings(), store.Load());
        Assert.True(store.Save(new DictationSettings(DictationHotkeys.VkF19)));
        Assert.Equal(new DictationSettings(DictationHotkeys.VkF19), store.Load());
        Assert.False(File.Exists(store.Path + ".tmp"));
    }
}
