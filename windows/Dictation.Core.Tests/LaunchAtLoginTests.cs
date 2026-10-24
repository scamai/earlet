using System.Xml.Linq;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// "Start Dictation when I sign in": off until the user turns it on, read
/// back from Windows rather than remembered (again whenever the window is
/// activated, which keeps a refusal's note while the box reads back as the
/// refusal left it), and honest when Windows refuses, with a note under the box.
/// The macOS LaunchAtLoginTests ("Launch at login"), scenario for scenario
/// and by the same names; the Mac's "needs approval" is Windows' entry
/// switched off in Settings → Apps → Startup. The registry half is
/// <see cref="LaunchAtLoginEntry"/>, tested here; reading it on Windows has
/// never run.
/// </summary>
public sealed class LaunchAtLoginTests
{
    private const string Exe = @"C:\Dictation\Dictation.exe";

    [Fact]
    public async Task Off_by_default_launching_and_enabling_never_register_Dictation()
    {
        using var rig = new Rig(beforeStart: r => r.Settings.Save(new DictationSettings(Enabled: true)));
        Assert.False(rig.View.Last.LaunchAtLogin);
        await rig.Startup.Enabling;
        rig.Dispatcher.Drain();
        Assert.True(rig.Controller.IsEnabled);
        Assert.True(rig.Controller.TryPause());
        await rig.EnableAsync();

        Assert.Empty(rig.Platform.Login.Requests);
        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Null(rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void The_state_is_read_from_Windows_which_the_user_can_change_in_Settings()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.IsEnabled = true);
        Assert.True(rig.View.Last.LaunchAtLogin);

        // Turned off in Settings while Dictation runs; the window is activated.
        rig.Platform.Login.IsEnabled = false;
        rig.Platform.Login.SwitchedOffInSettings = true;
        rig.Controller.RefreshLaunchAtLogin();

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginSwitchedOff, rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void Turning_it_on_registers_and_turning_it_off_unregisters()
    {
        using var rig = new Rig();
        var status = rig.View.Last.Status;

        rig.Controller.SetLaunchAtLogin(true);
        Assert.True(rig.View.Last.LaunchAtLogin);
        Assert.Null(rig.View.Last.LaunchAtLoginNote);

        rig.Controller.SetLaunchAtLogin(false);
        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal([true, false], rig.Platform.Login.Requests);
        // The box says it; the status line keeps what it said.
        Assert.Equal(status, rig.View.Last.Status);
    }

    [Fact]
    public void When_Windows_refuses_the_box_stays_off_and_says_why()
    {
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        var status = rig.View.Last.Status;
        rig.Platform.Login.Accepts = false;

        rig.Controller.SetLaunchAtLogin(true);

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, rig.View.Last.LaunchAtLoginNote);
        Assert.Contains(DictationCopy.OpenLogFolder, rig.View.Last.LaunchAtLoginNote, StringComparison.Ordinal);
        Assert.Equal(status, rig.View.Last.Status);
        Assert.Contains(log.Lines, l => l.Contains("Start at sign-in", StringComparison.Ordinal) && l.Contains("refused", StringComparison.Ordinal));
    }

    [Fact]
    public void A_change_that_throws_is_a_refusal_too()
    {
        using var rig = new Rig();
        rig.Platform.Login.Accepts = false;
        rig.Platform.Login.ReadThrows = true;

        rig.Controller.SetLaunchAtLogin(true);

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void A_change_Windows_reports_written_but_does_not_show_is_a_refusal_too()
    {
        // The box shows what Windows has, and the note says the change did not take.
        using var rig = new Rig();
        rig.Platform.Login.Takes = false;

        rig.Controller.SetLaunchAtLogin(true);
        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, rig.View.Last.LaunchAtLoginNote);

        rig.Platform.Login.IsEnabled = true;
        rig.Controller.RefreshLaunchAtLogin();
        rig.Controller.SetLaunchAtLogin(false);
        Assert.True(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOff, rig.View.Last.LaunchAtLoginNote);
    }

    /// <summary>The Mac's "when macOS waits for approval, the toggle is on and
    /// points at Login Items": Windows' Settings switch is the one off the
    /// user did not make in this window, so the box is off with a note, and
    /// checking it turns the entry back on.</summary>
    [Fact]
    public void Switched_off_in_Settings_the_box_is_off_and_points_at_Startup_until_it_is_checked()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.SwitchedOffInSettings = true);
        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginSwitchedOff, rig.View.Last.LaunchAtLoginNote);
        Assert.Contains("Settings → Apps → Startup", rig.View.Last.LaunchAtLoginNote, StringComparison.Ordinal);

        rig.Controller.SetLaunchAtLogin(true);

        Assert.True(rig.View.Last.LaunchAtLogin);
        Assert.Null(rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void A_refused_unregister_leaves_it_on_and_tells_the_user_where_to_remove_it()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.IsEnabled = true);
        rig.Platform.Login.Accepts = false;

        rig.Controller.SetLaunchAtLogin(false);

        Assert.True(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOff, rig.View.Last.LaunchAtLoginNote);
        Assert.Contains("Settings → Apps → Startup", rig.View.Last.LaunchAtLoginNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_copy_Windows_cannot_read_reads_as_off_with_nothing_to_explain_until_the_user_asks()
    {
        using var rig = new Rig(beforeStart: r =>
        {
            r.Platform.Login.IsEnabled = true;
            r.Platform.Login.ReadThrows = true;
        });

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Null(rig.View.Last.LaunchAtLoginNote);
    }

    // ─── Windows: read again whenever the window is activated ───────────

    [Fact]
    public void A_box_that_was_on_reads_as_off_when_the_entry_can_no_longer_be_read()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.IsEnabled = true);
        Assert.True(rig.View.Last.LaunchAtLogin);

        rig.Platform.Login.ReadThrows = true;
        rig.Controller.RefreshLaunchAtLogin();

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Null(rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void Activating_the_window_reads_the_entry_again_and_repaints_only_what_changed()
    {
        using var rig = new Rig();
        var renders = rig.View.States.Count;
        var reads = rig.Platform.Login.Reads;

        rig.Controller.RefreshLaunchAtLogin();
        Assert.True(rig.Platform.Login.Reads > reads, "asked Windows again");
        Assert.Equal(renders, rig.View.States.Count);

        // Turned on outside the window (another copy's installer, a script):
        // the box follows.
        rig.Platform.Login.IsEnabled = true;
        rig.Controller.RefreshLaunchAtLogin();
        Assert.Equal(renders + 1, rig.View.States.Count);
        Assert.True(rig.View.Last.LaunchAtLogin);

        rig.Controller.RefreshLaunchAtLogin();
        Assert.Equal(renders + 1, rig.View.States.Count);
    }

    /// <summary>The note says to open the log folder; Explorer takes the
    /// focus, and coming back activates the window. The Mac re-reads only when
    /// its window appears, so its note is still there then; here the
    /// activation's re-read finds the box where the refusal left it and keeps
    /// the note, and repaints nothing.</summary>
    [Fact]
    public void A_refusal_note_stays_through_a_round_trip_to_the_log_folder()
    {
        using var rig = new Rig();
        rig.Platform.Login.Accepts = false;
        rig.Controller.SetLaunchAtLogin(true);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, rig.View.Last.LaunchAtLoginNote);
        var (renders, reads) = (rig.View.States.Count, rig.Platform.Login.Reads);

        Assert.True(rig.Controller.OpenLogFolder());
        rig.Controller.RefreshLaunchAtLogin();
        rig.Controller.RefreshLaunchAtLogin();

        Assert.True(rig.Platform.Login.Reads > reads, "asked Windows again");
        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, rig.View.Last.LaunchAtLoginNote);
        Assert.Equal(renders, rig.View.States.Count);
    }

    [Fact]
    public void A_refused_unregister_s_note_stays_while_the_entry_is_still_there()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.IsEnabled = true);
        rig.Platform.Login.Accepts = false;
        rig.Controller.SetLaunchAtLogin(false);

        rig.Controller.RefreshLaunchAtLogin();

        Assert.True(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOff, rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void A_refusal_note_goes_when_the_entry_changes_outside_the_window()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.IsEnabled = true);
        rig.Platform.Login.Accepts = false;
        rig.Controller.SetLaunchAtLogin(false);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOff, rig.View.Last.LaunchAtLoginNote);

        // The user followed the note and removed it in Settings → Apps → Startup.
        rig.Platform.Login.IsEnabled = false;
        rig.Controller.RefreshLaunchAtLogin();

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Null(rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void A_refusal_note_gives_way_to_Settings_when_Settings_switched_the_entry_off()
    {
        using var rig = new Rig();
        rig.Platform.Login.Accepts = false;
        rig.Controller.SetLaunchAtLogin(true);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, rig.View.Last.LaunchAtLoginNote);

        // The entry is there now, switched off in Settings: the box is still
        // off, and the Settings note says what to do about it.
        rig.Platform.Login.SwitchedOffInSettings = true;
        rig.Controller.RefreshLaunchAtLogin();

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginSwitchedOff, rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void Switched_back_on_in_Settings_the_note_goes_and_the_box_is_checked()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.SwitchedOffInSettings = true);

        rig.Platform.Login.SwitchedOffInSettings = false;
        rig.Platform.Login.IsEnabled = true;
        rig.Controller.RefreshLaunchAtLogin();

        Assert.True(rig.View.Last.LaunchAtLogin);
        Assert.Null(rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void After_quitting_nothing_is_read_or_drawn()
    {
        using var rig = new Rig();
        rig.Controller.Dispose();
        var (renders, reads) = (rig.View.States.Count, rig.Platform.Login.Reads);

        rig.Controller.RefreshLaunchAtLogin();
        rig.Controller.SetLaunchAtLogin(true);

        Assert.Equal((renders, reads), (rig.View.States.Count, rig.Platform.Login.Reads));
        Assert.Empty(rig.Platform.Login.Requests);
    }

    // ─── The registry's decision ────────────────────────────────────────

    [Fact]
    public void Only_this_copy_s_entry_switched_off_in_Settings_counts_as_switched_off()
    {
        var command = LaunchAtLoginEntry.Command(Exe);
        byte[] disabled = [0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];
        byte[] enabled = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.True(LaunchAtLoginEntry.IsSwitchedOff(command, disabled, Exe));
        Assert.True(LaunchAtLoginEntry.IsSwitchedOff(" " + command.ToUpperInvariant() + " ", disabled, Exe));
        Assert.False(LaunchAtLoginEntry.IsSwitchedOff(command, enabled, Exe));
        Assert.False(LaunchAtLoginEntry.IsSwitchedOff(command, null, Exe));
        Assert.False(LaunchAtLoginEntry.IsSwitchedOff(command, [], Exe));
        // Another copy's entry, or none: plain off, nothing to explain here.
        Assert.False(LaunchAtLoginEntry.IsSwitchedOff(LaunchAtLoginEntry.Command(@"D:\Old\Dictation.exe"), disabled, Exe));
        Assert.False(LaunchAtLoginEntry.IsSwitchedOff(null, disabled, Exe));
        // Never on and switched off at once.
        Assert.False(LaunchAtLoginEntry.IsOn(command, disabled, Exe));
    }

    // ─── The WPF half: pins on the source, not evidence it behaves ──────

    [Fact]
    public void The_window_reads_the_entry_again_when_activated_and_shows_the_note_under_the_box_as_a_polite_line()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        Assert.Contains(
            "Activated += (_, _) => controller.RefreshLaunchAtLogin();", Member(code, "public MainWindow("), StringComparison.Ordinal);
        var render = Member(code, "public void Render(DictationViewState state)");
        var shown = render.IndexOf(
            "LaunchAtLoginNote.Visibility = state.LaunchAtLoginNote is null ? Visibility.Collapsed : Visibility.Visible;",
            StringComparison.Ordinal);
        var set = render.IndexOf("SetLiveText(LaunchAtLoginNote, state.LaunchAtLoginNote ?? string.Empty);", StringComparison.Ordinal);
        Assert.True(shown >= 0 && set > shown, "shown before its text changes, so it is announced from a visible line");

        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var note = Named(xaml, "LaunchAtLoginNote");
        var column = note.Parent!.Elements().ToList();
        Assert.Equal(column.IndexOf(Named(xaml, "LaunchAtLoginBox")) + 1, column.IndexOf(note));
        Assert.Equal("Collapsed", (string?)note.Attribute("Visibility"));
        Assert.Equal("Polite", (string?)note.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.LiveSetting"));
        // The Mac's General section follows its Vocabulary.
        Assert.True(column.IndexOf(Named(xaml, "VocabularySection")) < column.IndexOf(Named(xaml, "LaunchAtLoginBox")));
    }

    [Fact]
    public void The_Windows_entry_reads_both_answers_from_the_one_decision()
    {
        var code = File.ReadAllText(AppFile("WindowsLaunchAtLogin.cs")).ReplaceLineEndings("\n");

        Assert.Contains("public bool IsEnabled => Read(LaunchAtLoginEntry.IsOn);", code, StringComparison.Ordinal);
        Assert.Contains("public bool SwitchedOffInSettings => Read(LaunchAtLoginEntry.IsSwitchedOff);", code, StringComparison.Ordinal);
    }
}
