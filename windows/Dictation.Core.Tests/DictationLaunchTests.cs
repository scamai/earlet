using System.Text.Json.Nodes;
using Dictation.Core.Tests.Learning;
using Dictation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// Dictation comes back the way the user left it: on after a launch when it
/// was on, never asking anything to get there, off only when they paused it.
/// </summary>
public sealed class DictationLaunchTests
{
    private static Action<Rig> LeftOn => r => r.Settings.Save(new DictationSettings(Enabled: true));

    [Fact]
    public async Task Enabling_is_remembered_and_the_next_launch_comes_back_on_by_itself()
    {
        using (var first = new Rig())
        {
            await first.EnableAsync();
            Assert.True(first.Settings.Load().Enabled);
        }

        using var next = new Rig(beforeStart: LeftOn);
        await next.Startup.Enabling;
        next.Dispatcher.Drain();

        Assert.True(next.Controller.IsEnabled);
        Assert.True(next.Startup.ShowWindow);
        Assert.Equal(DictationCopy.Armed(next.Controller.HoldKey), next.View.Last.Status);

        // And it dictates, with nobody having clicked anything.
        next.Dictate(v => v.Status.StartsWith("Text inserted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fresh_install_starts_paused_and_starts_nothing()
    {
        using var rig = new Rig();
        await rig.Startup.Enabling;

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(0, rig.Platform.CreateWorkerCalls);
        Assert.Equal(DictationCopy.Ready, rig.View.Last.Status);
        Assert.True(rig.Startup.ShowWindow);
    }

    [Fact]
    public async Task Pause_is_remembered()
    {
        using var rig = new Rig(beforeStart: LeftOn);
        await rig.Startup.Enabling;
        rig.Controller.Pause();

        Assert.False(rig.Settings.Load().Enabled);

        using var next = new Rig(beforeStart: r => r.Settings.Save(rig.Settings.Load()));
        await next.Startup.Enabling;
        Assert.False(next.Controller.IsEnabled);
        Assert.Equal(0, next.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task Quitting_is_not_pausing()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        Assert.Equal(DictationCloseAction.Close, rig.Controller.RequestClose(quit: true).Action);
        rig.Controller.Dispose();

        Assert.True(rig.Settings.Load().Enabled);
    }

    [Fact]
    public async Task Missing_resources_at_launch_say_so_and_start_no_worker()
    {
        using var rig = new Rig(beforeStart: r =>
        {
            LeftOn(r);
            r.Copy.Delete("resources/model/model.bin");
        });
        await rig.Startup.Enabling;

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(0, rig.Platform.CreateWorkerCalls);
        Assert.Equal(DictationCopy.ResourcesMissing, rig.View.Last.Status);
        // Still remembered: reinstalling the package brings it back.
        Assert.True(rig.Settings.Load().Enabled);
    }

    [Fact]
    public async Task A_model_that_will_not_load_at_launch_keeps_the_choice()
    {
        using var rig = new Rig(beforeStart: r =>
        {
            LeftOn(r);
            r.Platform.Worker.InitSucceeds = false;
        });
        await rig.Startup.Enabling;

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
        Assert.True(rig.Settings.Load().Enabled);
    }

    // ─── Starting at sign-in ────────────────────────────────────────────

    [Fact]
    public async Task At_sign_in_dictation_comes_back_on_with_the_window_hidden()
    {
        using var rig = new Rig(beforeStart: LeftOn, atLogin: true);

        Assert.False(rig.Startup.ShowWindow);
        await rig.Startup.Enabling;
        rig.Dispatcher.Drain();

        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(0, rig.View.RevealRequests);
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task At_sign_in_a_failure_brings_the_window_forward()
    {
        using var rig = new Rig(
            beforeStart: r =>
            {
                LeftOn(r);
                r.Platform.Worker.InitSucceeds = false;
            },
            atLogin: true);
        await rig.Startup.Enabling;

        Assert.Equal(1, rig.View.RevealRequests);
        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
    }

    [Fact]
    public async Task At_sign_in_with_dictation_off_the_window_shows()
    {
        using var rig = new Rig(atLogin: true);
        await rig.Startup.Enabling;

        Assert.True(rig.Startup.ShowWindow);
        Assert.False(rig.Controller.IsEnabled);
    }

    [Fact]
    public async Task Missing_resources_at_sign_in_bring_the_window_forward()
    {
        using var rig = new Rig(
            beforeStart: r =>
            {
                LeftOn(r);
                r.Copy.Delete("resources/python/python.exe");
            },
            atLogin: true);
        await rig.Startup.Enabling;

        Assert.Equal(1, rig.View.RevealRequests);
        Assert.Equal(DictationCopy.ResourcesMissing, rig.View.Last.Status);
    }

    [Fact]
    public async Task Once_back_on_after_sign_in_a_later_failure_stays_in_the_window()
    {
        using var rig = new Rig(beforeStart: LeftOn, atLogin: true);
        await rig.Startup.Enabling;
        rig.Controller.Pause();
        rig.Platform.Worker.InitSucceeds = false;

        await rig.EnableAsync();

        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
        Assert.Equal(0, rig.View.RevealRequests);
    }

    [Fact]
    public async Task A_manual_enable_that_fails_does_not_pull_the_window_forward()
    {
        using var rig = new Rig();
        rig.Platform.Worker.InitSucceeds = false;

        await rig.EnableAsync();

        Assert.Equal(0, rig.View.RevealRequests);
    }

    // ─── The settings file ──────────────────────────────────────────────

    private static DictationSettingsStore Store(out string path, Action<TimeSpan>? retryWait = null)
    {
        var dir = Directory.CreateTempSubdirectory("dictation-settings-").FullName;
        path = Path.Combine(dir, "settings.json");
        return new DictationSettingsStore(path, NullLogger<DictationSettingsStore>.Instance, retryWait);
    }

    [Fact]
    public void Every_setting_round_trips_and_keys_it_does_not_own_survive()
    {
        var store = Store(out var path);
        File.WriteAllText(path, """{ "hold_virtual_key": 0, "a_newer_switch": false, "later": { "x": 1 } }""");

        var settings = new DictationSettings(
            DictationHotkeys.VkF13, Enabled: true, Language: "ja", TrayNoticeShown: true, LearnFromCorrections: false,
            CompletedFirstDictation: true);
        Assert.True(store.Save(settings));

        Assert.Equal(settings, store.Load());
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.False(file["a_newer_switch"]!.GetValue<bool>());
        Assert.Equal(1, file["later"]!["x"]!.GetValue<int>());
        Assert.True(file["enabled"]!.GetValue<bool>());
        Assert.Equal("ja", file["language"]!.GetValue<string>());
        Assert.False(file["learn_from_corrections"]!.GetValue<bool>());
        Assert.True(file["completed_first_dictation"]!.GetValue<bool>());
    }

    [Fact]
    public void Learning_from_corrections_is_on_unless_the_user_turned_it_off()
    {
        var store = Store(out var path);
        Assert.True(store.Load().LearnFromCorrections);

        // A file from before the switch existed.
        File.WriteAllText(path, """{ "enabled": true, "language": "de" }""");
        Assert.True(store.Load().LearnFromCorrections);

        File.WriteAllText(path, """{ "learn_from_corrections": false }""");
        Assert.False(store.Load().LearnFromCorrections);

        Assert.True(store.Save(new DictationSettings()));
        Assert.True(JsonNode.Parse(File.ReadAllText(path))!["learn_from_corrections"]!.GetValue<bool>());
    }

    [Fact]
    public void A_language_this_build_does_not_offer_reads_as_automatic()
    {
        var store = Store(out var path);
        File.WriteAllText(path, """{ "language": "tlh", "enabled": true }""");

        var settings = store.Load();

        Assert.Null(settings.Language);
        Assert.True(settings.Enabled);
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_that_setting_s_default()
    {
        var store = Store(out var path);
        File.WriteAllText(
            path, """{ "enabled": "yes", "hold_virtual_key": "F13", "language": 7, "learn_from_corrections": "no" }""");

        Assert.Equal(new DictationSettings(), store.Load());
    }

    [Fact]
    public void A_file_that_is_not_an_object_is_the_defaults_and_is_never_replaced()
    {
        var store = Store(out var path);
        File.WriteAllText(path, "[1, 2, 3]");

        Assert.Equal(new DictationSettings(), store.Load());
        Assert.False(store.Save(new DictationSettings(Enabled: true)));
        Assert.Equal("[1, 2, 3]", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_that_does_not_parse_is_the_defaults_and_is_never_replaced_until_it_does()
    {
        // Truncated by a crash mid-write: the user's choices may still be in
        // there, and overwriting them with the defaults is the one save that
        // must never happen (the rule for a file that cannot be read; unlike
        // learning.json, which is moved aside, this file stays where it is).
        var store = Store(out var path);
        File.WriteAllText(path, """{ "hold_virtual_key": 124, "enabled": true, "lang""");

        Assert.Equal(new DictationSettings(), store.Load());
        Assert.False(store.Save(new DictationSettings(Enabled: true)));
        Assert.False(store.Save(new DictationSettings()));
        Assert.Contains("lang", File.ReadAllText(path), StringComparison.Ordinal);

        // Repaired, a later load reads it and saving works again, still
        // keeping the keys it does not own.
        File.WriteAllText(path, """{ "hold_virtual_key": 124, "enabled": true, "a_newer_switch": false }""");
        Assert.Equal(DictationHotkeys.VkF13, store.Load().HoldVirtualKey);
        Assert.True(store.Save(new DictationSettings(LearnFromCorrections: false)));
        Assert.False(store.Load().LearnFromCorrections);
        Assert.False(JsonNode.Parse(File.ReadAllText(path))!["a_newer_switch"]!.GetValue<bool>());
    }

    [Fact]
    public void A_file_that_turns_bad_between_the_load_and_the_save_is_kept_until_it_is_repaired()
    {
        var store = Store(out var path);
        File.WriteAllText(path, Chosen);
        var chosen = store.Load();
        Assert.False(chosen.LearnFromCorrections);

        File.WriteAllText(path, "{ not json");
        Assert.False(store.Save(chosen with { TrayNoticeShown = true }));
        Assert.Equal("{ not json", File.ReadAllText(path));

        // This run did read the user's choices, so once the file parses again
        // the next save merges into it as any save does, with no reload.
        File.WriteAllText(path, """{ "a_newer_switch": false }""");
        Assert.True(store.Save(chosen with { TrayNoticeShown = true }));
        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.False(file["a_newer_switch"]!.GetValue<bool>());
        Assert.False(file["learn_from_corrections"]!.GetValue<bool>());
        Assert.True(file["tray_notice_shown"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    public void An_empty_file_is_the_defaults_and_is_replaced_on_save(string empty)
    {
        // Nothing in it to protect: the same as a missing file, so a choice
        // made in this run is kept rather than refused for the whole run.
        var store = Store(out var path);
        File.WriteAllText(path, empty);

        Assert.Equal(new DictationSettings(), store.Load());
        Assert.True(store.Save(new DictationSettings(LearnFromCorrections: false)));
        Assert.False(store.Load().LearnFromCorrections);
    }

    [Fact]
    public void A_file_emptied_between_the_load_and_the_save_is_written()
    {
        var store = Store(out var path);
        File.WriteAllText(path, Chosen);
        var chosen = store.Load();

        File.WriteAllText(path, "");
        Assert.True(store.Save(chosen));
        Assert.Equal(chosen, store.Load());
    }

    [Theory]
    [InlineData("""{ "hold_virtual_key": 124, "enabled": true, "lang""")]
    [InlineData("[1, 2, 3]")]
    public void A_bad_file_repaired_by_hand_is_not_overwritten_until_a_load_reads_it(string bad)
    {
        // The load found nothing it could use, so this run holds the defaults.
        // The user then repairs the file while the app runs: it parses again,
        // but this store has not read it, and a save would write the defaults
        // over the repair — turning learning back on — exactly what an
        // unreadable file is protected from.
        var store = Store(out var path);
        File.WriteAllText(path, bad);
        Assert.Equal(new DictationSettings(), store.Load());

        File.WriteAllText(path, Chosen);
        Assert.False(store.Save(new DictationSettings(TrayNoticeShown: true)));
        Assert.Equal(Chosen, File.ReadAllText(path));

        // The next load reads the repair, and saving works again.
        Assert.False(store.Load().LearnFromCorrections);
        Assert.True(store.Save(new DictationSettings(DictationHotkeys.VkF13, LearnFromCorrections: false)));
        Assert.False(store.Load().LearnFromCorrections);
    }

    /// <summary>The user's choices: learning off is the one a reset would
    /// silently undo.</summary>
    private const string Chosen =
        """{ "hold_virtual_key": 124, "enabled": true, "language": "de", "learn_from_corrections": false }""";

    [UnreadableFileFact]
    public void A_settings_file_that_could_not_be_read_is_not_overwritten_until_it_can_be()
    {
        var store = Store(out var path);
        File.WriteAllText(path, Chosen);

        using (UnreadableFileFactAttribute.Lock(path))
        {
            Assert.Equal(new DictationSettings(), store.Load());
        }

        // Readable again, but this store has not read it: what it holds are
        // the defaults, and writing them would turn learning back on.
        Assert.False(store.Save(new DictationSettings(TrayNoticeShown: true)));
        Assert.Equal(Chosen, File.ReadAllText(path));

        Assert.False(store.Load().LearnFromCorrections);
        Assert.True(store.Save(new DictationSettings(DictationHotkeys.VkF13, LearnFromCorrections: false)));
        Assert.False(store.Load().LearnFromCorrections);
    }

    [UnreadableFileFact]
    public async Task A_launch_that_could_not_read_its_settings_never_resets_them()
    {
        // Locked at launch, then free: every save this run would otherwise
        // write the defaults over the hold key, the language and learning.
        IDisposable? locked = null;
        using (var rig = new Rig(beforeStart: r =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r.Settings.Path)!);
            File.WriteAllText(r.Settings.Path, Chosen);
            locked = UnreadableFileFactAttribute.Lock(r.Settings.Path);
        }))
        {
            locked!.Dispose();
            Assert.True(rig.View.Last.LearnFromCorrections);

            await rig.EnableAsync();
            rig.Controller.ChooseHoldKey(DictationHotkeys.VkLeftAlt);
            Assert.Equal(DictationCopy.HoldKeyNotSaved, rig.View.Last.Status);
            rig.Controller.Pause();

            Assert.Equal(Chosen, File.ReadAllText(rig.Settings.Path));
        }
    }

    [Fact]
    public async Task A_launch_whose_settings_do_not_parse_never_rewrites_them()
    {
        // The corrupt file is the defaults for the run, kept on disk byte for
        // byte through every save the run tries.
        const string corrupt = """{ "hold_virtual_key": 124, "enabled": true, "lang""";
        using (var rig = new Rig(beforeStart: r =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r.Settings.Path)!);
            File.WriteAllText(r.Settings.Path, corrupt);
        }))
        {
            Assert.True(rig.View.Last.LearnFromCorrections);

            await rig.EnableAsync();
            rig.Controller.ChooseHoldKey(DictationHotkeys.VkLeftAlt);
            Assert.Equal(DictationCopy.HoldKeyNotSaved, rig.View.Last.Status);
            rig.Controller.Pause();

            Assert.Equal(corrupt, File.ReadAllText(rig.Settings.Path));
        }
    }

    [Fact]
    public void Turning_learning_off_in_a_run_that_cannot_save_says_it_will_reset()
    {
        // A privacy switch must not turn back on at the next launch without a
        // word: it shows off for this run, and the status line says the next
        // launch will not keep that.
        const string corrupt = """{ "learn_from_corrections": fal""";
        using (var rig = new Rig(beforeStart: r =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r.Settings.Path)!);
            File.WriteAllText(r.Settings.Path, corrupt);
        }))
        {
            rig.Controller.SetLearnFromCorrections(false);

            Assert.False(rig.View.Last.LearnFromCorrections);
            Assert.Equal(DictationCopy.LearnFromCorrectionsNotSaved, rig.View.Last.Status);
            Assert.Equal(corrupt, File.ReadAllText(rig.Settings.Path));

            // What the next launch reads: the defaults, learning on.
            var next = new DictationSettingsStore(rig.Settings.Path, NullLogger<DictationSettingsStore>.Instance);
            Assert.True(next.Load().LearnFromCorrections);
        }

        // A switch that saved says nothing of the kind.
        using var saving = new Rig();
        saving.Controller.SetLearnFromCorrections(false);
        Assert.NotEqual(DictationCopy.LearnFromCorrectionsNotSaved, saving.View.Last.Status);
        Assert.False(saving.Settings.Load().LearnFromCorrections);
    }

    [Fact]
    public void A_save_the_disk_refuses_leaves_no_temporary_file()
    {
        var waits = 0;
        var store = Store(out var path, _ => waits++);
        // A directory where the file should be: every replace fails.
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "occupied"), "x");

        Assert.Equal(new DictationSettings(), store.Load());
        Assert.False(store.Save(new DictationSettings(Enabled: true)));

        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        // Retried, and then given up on: a save never spins on a file it
        // cannot have.
        Assert.InRange(waits, 1, 10);
    }

    [Fact]
    public void A_save_retries_a_replace_that_fails_for_a_moment()
    {
        // Something occupies the target just as the save replaces it, and
        // lets go before the next attempt. The portable stand-in for a reader
        // holding the file open on Windows: a directory in its place, removed
        // in the wait between two attempts rather than by a timer racing them.
        string? path = null;
        var waits = 0;
        var store = Store(out path, _ =>
        {
            if (waits++ == 0)
            {
                Directory.Delete(path!, recursive: true);
            }
        });
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "occupied"), "x");

        Assert.True(store.Save(new DictationSettings(LearnFromCorrections: false)));
        Assert.Equal(1, waits);
        Assert.False(store.Load().LearnFromCorrections);
    }

    [Dictation.TestSupport.WindowsOnlyFact("replacing a file another handle holds open without delete sharing fails only on Windows")]
    public void A_save_waits_briefly_for_a_reader_that_holds_the_file_open()
    {
        var store = Store(out var path);
        Assert.True(store.Save(new DictationSettings()));
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(() =>
        {
            Thread.Sleep(15);
            reader.Dispose();
        });

        Assert.True(store.Save(new DictationSettings(LearnFromCorrections: false)));
        release.Wait();
        Assert.False(store.Load().LearnFromCorrections);
    }
}
