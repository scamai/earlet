using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The notification-area menu: the macOS MenuTests, scenario for scenario
/// and by the same names, on the model the tray draws
/// (<see cref="DictationTrayMenu"/>) and the commands its items run
/// (<see cref="DictationTrayCommands"/>); then the lines the Mac's scene adds
/// (the recent result's and the learning receipt's), the Windows layout, and
/// the WinForms half read from source.
/// </summary>
public sealed class TrayMenuTests
{
    private const DictationTrayItem Sep = DictationTrayItem.Separator;
    private const DictationTrayItem Recovery = DictationTrayItem.RecentRecovery;
    private const DictationTrayItem Copy = DictationTrayItem.CopyLastText;
    private const DictationTrayItem Remembered = DictationTrayItem.CorrectionRemembered;
    private const DictationTrayItem Review = DictationTrayItem.ReviewVocabulary;
    private const DictationTrayItem Undo = DictationTrayItem.UndoLearning;
    private const DictationTrayItem Toggle = DictationTrayItem.Toggle;
    private const DictationTrayItem Cancel = DictationTrayItem.Cancel;
    private const DictationTrayItem Show = DictationTrayItem.ShowWindow;
    private const DictationTrayItem Log = DictationTrayItem.OpenLogFolder;
    private const DictationTrayItem Quit = DictationTrayItem.Quit;

    private const string Heard = "trovio";
    private const string Meant = "Trovioo";

    private static IReadOnlyList<DictationTrayItem> Items(Rig rig, bool learningUndoable = false) =>
        DictationTrayMenu.Items(rig.View.Last.Tray, learningUndoable);

    private static IReadOnlyList<DictationTrayEntry> Entries(Rig rig, bool learningUndoable = false) =>
        DictationTrayMenu.Entries(rig.View.Last.Tray, learningUndoable);

    private static DictationTrayEntry Entry(IEnumerable<DictationTrayEntry> entries, DictationTrayItem item) =>
        Assert.Single(entries, e => e.Item == item);

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    /// <summary>What the window's three callbacks were asked to do.</summary>
    private sealed class WindowCalls
    {
        public int Shown;
        public int Reviewed;
        public int Quit;
    }

    /// <summary>The controller and the Vocabulary section, wired as
    /// MainWindow wires them, with the menu's commands over both.</summary>
    private sealed class App : IDisposable
    {
        public App(bool learning = false)
        {
            Rig = new Rig(learning: learning);
            if (learning)
            {
                Rig.Platform.Worker.Transcript = "we use trovio for the checks";
                Rig.Platform.Field.FixAfterEachPaste(Heard, Meant);
                Vocabulary = new VocabularyModel(Rig.Learning!, View, Rig.Dispatcher.Post, Rig.Controller.Corrections);
                Rig.View.OnRender = state => Vocabulary.ApplyLearningSwitch(state.LearnFromCorrections);
                Vocabulary.Refresh();
            }
            else
            {
                // Under the rig's copy, which its Dispose removes: a rig
                // without learning never writes there itself.
                Vocabulary = new VocabularyModel(
                    new DictationLearning(
                        new LearningStore(Rig.LearningDirectory),
                        new WordListLexicon(LearningGoldenTests.SharedLexicon)),
                    View,
                    Rig.Dispatcher.Post);
            }

            Commands = new DictationTrayCommands(
                Rig.Controller, Vocabulary, () => Window.Shown++, () => Window.Reviewed++, () => Window.Quit++);
        }

        public Rig Rig { get; }
        public RecordingVocabularyView View { get; } = new();
        public VocabularyModel Vocabulary { get; }
        public DictationTrayCommands Commands { get; }
        public WindowCalls Window { get; } = new();

        /// <summary>The menu as the tray draws it now, both halves.</summary>
        public IReadOnlyList<DictationTrayItem> Items() =>
            DictationTrayMenu.Items(Rig.View.Last.Tray, View.States.Count > 0 && View.Last.CanUndoLearning);

        public async Task Choose(DictationTrayItem item)
        {
            await Commands.ChooseAsync(item);
            Rig.Dispatcher.Drain();
        }

        public void DictateAndWatch()
        {
            var inserted = Rig.View.States.Count(Inserted);
            Rig.Press();
            Rig.Release();
            Rig.DrainUntil(() => Rig.View.States.Count(Inserted) > inserted);
            Rig.Dispatcher.Drain();
            Rig.WaitForWatch();
            Rig.Dispatcher.Drain();
        }

        public LearningState OnDisk() => new LearningStore(Rig.LearningDirectory).Load().State;

        public void Dispose()
        {
            Rig.Time.Release();
            Vocabulary.Dispose();
            Rig.Dispose();
        }
    }

    // ─── The macOS MenuTests ────────────────────────────────────────────

    [Fact]
    public async Task Idle_the_menu_offers_the_toggle_Show_Dictation_Open_log_folder_and_Quit_in_that_order()
    {
        using var rig = new Rig();
        Assert.Equal([Toggle, Show, Log, Quit], Items(rig));

        await rig.EnableAsync();
        Assert.Equal([Toggle, Show, Log, Quit], Items(rig));
    }

    [Fact]
    public async Task While_recording_Cancel_follows_the_toggle()
    {
        using var app = new App();
        await app.Rig.EnableAsync();

        app.Rig.Press();
        app.Rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.TrayListening, app.Rig.View.Last.Tray.ToolTip);
        Assert.Equal([Toggle, Cancel, Show, Log, Quit], Items(app.Rig));

        await app.Choose(Cancel);
        Assert.Equal([Toggle, Show, Log, Quit], Items(app.Rig));
        app.Rig.Release();
        app.Rig.Dispatcher.Drain();
        Assert.Equal(0, app.Rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task Open_log_folder_opens_it_and_leaves_the_status_alone()
    {
        using var app = new App();
        var before = app.Rig.View.Last.Status;

        await app.Choose(Log);

        Assert.Equal([app.Rig.LogDirectory], app.Rig.Platform.OpenedFolders);
        Assert.Equal(before, app.Rig.View.Last.Status);
        Assert.Equal(0, app.Window.Shown);
    }

    [Fact]
    public async Task A_log_folder_that_will_not_open_is_named_so_the_user_can_find_it()
    {
        using var app = new App();
        app.Rig.Platform.FolderOpens = false;

        await app.Choose(Log);

        Assert.Contains(@"%LOCALAPPDATA%\Dictation\logs", app.Rig.View.Last.Status, StringComparison.Ordinal);
        // The sentence is on the status line, and the menu is usually used
        // with the window hidden: the window comes forward to show it, as it
        // does for an Enable from the menu that fails.
        Assert.Equal(1, app.Window.Shown);
        // The folder the sentence names is the one the app logs to.
        var paths = new DictationPaths(@"C:\Users\someone\AppData\Local\" + DictationPaths.ProductDirectoryName);
        Assert.EndsWith(
            Path.Combine(DictationPaths.ProductDirectoryName, DictationPaths.LogDirectoryName),
            paths.LogDirectory,
            StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dictation", "logs"),
            DictationPaths.ForCurrentUser().LogDirectory);
    }

    // ─── The Mac scene's recent-result and learning lines ──────────────

    [Fact]
    public async Task A_held_result_puts_Copy_last_text_with_its_time_first_and_never_the_words()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.Rig.Dictate(Inserted);

        var entries = Entries(app.Rig);
        Assert.Equal([Copy, Sep, Toggle, Sep, Show, Log, Sep, Quit], entries.Select(e => e.Item));
        var copy = Entry(entries, Copy);
        Assert.Equal(DictationCopy.CopyLastTextAt(DictationCopy.ShortTime(app.Rig.Now)), copy.Label);
        Assert.True(copy.Enabled);
        Assert.All(entries, e => Assert.DoesNotContain("hello", e.Label, StringComparison.OrdinalIgnoreCase));

        var writes = app.Rig.Platform.Clipboard.Writes.Count;
        await app.Choose(Copy);
        Assert.Equal(writes + 1, app.Rig.Platform.Clipboard.Writes.Count);
        Assert.Equal(DictationCopy.HudCopied, app.Rig.Hud.Current?.Text);
    }

    [Fact]
    public async Task After_a_failed_paste_Your_words_are_still_here_stands_over_Copy_last_text_as_an_inactive_line()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.Rig.Platform.Clipboard.Accepts = false;
        app.Rig.Platform.Paste.Succeeds = false;
        app.Rig.Dictate(v => v.Status == DictationCopy.WordsStillHere);

        var entries = Entries(app.Rig);
        Assert.Equal([Recovery, Copy, Sep, Toggle, Sep, Show, Log, Sep, Quit], entries.Select(e => e.Item));
        Assert.Equal(new DictationTrayEntry(Recovery, DictationCopy.RecentTitleRecovery, Enabled: false), entries[0]);
        Assert.True(Entry(entries, Copy).Enabled);

        app.Rig.Controller.DismissLastText();
        Assert.Equal([Toggle, Show, Log, Quit], Items(app.Rig));
    }

    [Fact]
    public async Task Copy_last_text_is_inactive_while_a_dictation_records()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.Rig.Dictate(Inserted);
        var writes = app.Rig.Platform.Clipboard.Writes.Count;

        app.Rig.Press();
        app.Rig.Dispatcher.Drain();

        Assert.False(Entry(Entries(app.Rig), Copy).Enabled);
        await app.Choose(Copy);
        Assert.Equal(writes, app.Rig.Platform.Clipboard.Writes.Count);
        app.Rig.Controller.Cancel();
        app.Rig.Release();
    }

    [Fact]
    public async Task A_saved_correction_adds_Remembered_Review_vocabulary_and_Undo_last_learning_and_Undo_restores_it()
    {
        using var app = new App(learning: true);
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        Assert.True(app.View.Last.CanUndoLearning);
        Assert.Equal([Meant], app.OnDisk().Words.Select(w => w.Text));
        var entries = DictationTrayMenu.Entries(app.Rig.View.Last.Tray, learningUndoable: true);
        Assert.Equal(
            [Copy, Sep, Remembered, Review, Undo, Sep, Toggle, Sep, Show, Log, Sep, Quit],
            entries.Select(e => e.Item));
        Assert.Equal(new DictationTrayEntry(Remembered, DictationCopy.TrayCorrectionRemembered, Enabled: false), Entry(entries, Remembered));
        Assert.Equal(new DictationTrayEntry(Review, DictationCopy.ReviewVocabulary, Enabled: true), Entry(entries, Review));
        Assert.Equal(new DictationTrayEntry(Undo, DictationCopy.UndoLastLearning, Enabled: true), Entry(entries, Undo));
        // The receipt names no word, and nor does the menu.
        Assert.All(entries, e => Assert.DoesNotContain(Meant, e.Label, StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, e => Assert.DoesNotContain(Heard, e.Label, StringComparison.OrdinalIgnoreCase));

        await app.Choose(Review);
        Assert.Equal(1, app.Window.Reviewed);
        Assert.Contains(Undo, app.Items());

        await app.Choose(Undo);

        Assert.Empty(app.OnDisk().Words);
        Assert.Equal(DictationCopy.LearningUndone, app.View.Last.LearningReceipt);
        Assert.DoesNotContain(Remembered, app.Items());
        Assert.DoesNotContain(Undo, app.Items());

        // An Undo from a menu drawn before it went changes nothing more.
        await app.Choose(Undo);
        Assert.Equal(DictationCopy.LearningUndone, app.View.Last.LearningReceipt);
    }

    [Fact]
    public async Task The_learning_lines_go_with_the_receipt_s_Dismiss()
    {
        using var app = new App(learning: true);
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        Assert.Contains(Undo, app.Items());

        app.Vocabulary.DismissLearningReceipt();

        Assert.DoesNotContain(Remembered, app.Items());
        Assert.DoesNotContain(Undo, app.Items());
        Assert.Equal([Meant], app.OnDisk().Words.Select(w => w.Text));
    }

    [Fact]
    public async Task The_learning_lines_go_when_learning_is_turned_off()
    {
        using var app = new App(learning: true);
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        Assert.Contains(Undo, app.Items());

        app.Rig.Controller.SetLearnFromCorrections(false);
        app.Rig.Dispatcher.Drain();

        Assert.DoesNotContain(Remembered, app.Items());
        Assert.DoesNotContain(Undo, app.Items());
    }

    [Fact]
    public async Task A_dictation_left_as_it_was_keeps_the_learning_lines()
    {
        using var app = new App(learning: true);
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        Assert.Contains(Undo, app.Items());

        app.Rig.Platform.Field.Fix = null;
        app.DictateAndWatch();

        Assert.Equal([Remembered, Review, Undo], app.Items().Where(i => i is Remembered or Review or Undo));
    }

    // ─── The rest of the menu's commands ────────────────────────────────

    [Fact]
    public async Task Show_Dictation_is_the_default_item_and_Show_and_Quit_go_to_the_window()
    {
        Assert.Equal(Show, DictationTrayMenu.DefaultItem);
        using var app = new App();

        await app.Choose(Show);
        await app.Choose(Quit);

        Assert.Equal((1, 0, 1), (app.Window.Shown, app.Window.Reviewed, app.Window.Quit));
        var entries = Entries(app.Rig);
        Assert.Equal(new DictationTrayEntry(Show, DictationCopy.ShowDictation, Enabled: true), Entry(entries, Show));
        Assert.Equal(new DictationTrayEntry(Quit, DictationCopy.QuitDictation, Enabled: true), Entry(entries, Quit));
        Assert.Equal(new DictationTrayEntry(Log, DictationCopy.OpenLogFolder, Enabled: true), Entry(entries, Log));
    }

    [Fact]
    public async Task The_toggle_enables_and_pauses_and_says_which()
    {
        using var app = new App();
        Assert.Equal(DictationCopy.EnableLabel, Entry(Entries(app.Rig), Toggle).Label);

        await app.Choose(Toggle);
        Assert.True(app.Rig.Controller.IsEnabled);
        Assert.Equal(new DictationTrayEntry(Toggle, DictationCopy.PauseLabel, Enabled: true), Entry(Entries(app.Rig), Toggle));

        await app.Choose(Toggle);
        Assert.False(app.Rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.EnableLabel, Entry(Entries(app.Rig), Toggle).Label);
    }

    [Fact]
    public async Task The_toggle_is_inactive_while_the_text_goes_in()
    {
        using var app = new App();
        using var pasteHold = new ManualResetEventSlim();
        app.Rig.Platform.Paste.Hold = pasteHold;
        await app.Rig.EnableAsync();
        app.Rig.Press();
        app.Rig.ReleaseToHeldPaste();
        app.Rig.DrainUntil(() => app.Rig.View.Last.Status == DictationCopy.Inserting);

        Assert.False(Entry(Entries(app.Rig), Toggle).Enabled);
        Assert.False(app.Rig.View.Last.CanPause);
        Assert.DoesNotContain(Cancel, Items(app.Rig));
        // The window's Pause clicked as the paste starts changes nothing.
        Assert.False(app.Rig.Controller.TryPause());
        Assert.True(app.Rig.Controller.IsEnabled);

        pasteHold.Set();
        app.Rig.DrainUntil(() => Inserted(app.Rig.View.Last));
        Assert.True(Entry(Entries(app.Rig), Toggle).Enabled);
        Assert.True(app.Rig.View.Last.CanPause);
        Assert.Equal("the user's clipboard", app.Rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task The_separators_and_inactive_lines_do_nothing_when_chosen()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        var before = app.Rig.View.States.Count;

        foreach (var item in new[] { Sep, Recovery, Remembered })
        {
            await app.Choose(item);
        }

        Assert.Equal(before, app.Rig.View.States.Count);
        Assert.Equal((0, 0, 0), (app.Window.Shown, app.Window.Reviewed, app.Window.Quit));
        Assert.True(app.Rig.Controller.IsEnabled);
    }

    [Fact]
    public async Task Pause_is_inactive_in_the_menu_and_the_window_while_a_dictation_listens_or_transcribes_and_a_stale_Pause_discards_nothing()
    {
        // The Mac disables its toggle in the menu and the window while it
        // records or is busy, and its toggle() refuses then. Cancel dictation
        // sits right beside Pause then, and a slip onto Pause would discard
        // the recording and unload the speech model too.
        using var app = new App();
        using var decode = new ManualResetEventSlim();
        app.Rig.Platform.Worker.HoldTranscribe = decode;
        await app.Rig.EnableAsync();
        Assert.True(Entry(Entries(app.Rig), Toggle).Enabled);
        Assert.True(app.Rig.View.Last.CanPause);

        app.Rig.Press();
        app.Rig.Dispatcher.Drain();
        Assert.Equal(new DictationTrayEntry(Toggle, DictationCopy.PauseLabel, Enabled: false), Entry(Entries(app.Rig), Toggle));
        Assert.Equal([Toggle, Cancel], Items(app.Rig).Take(2));
        // The window's own Pause by the same rule, beside its live Cancel.
        Assert.False(app.Rig.View.Last.CanPause);
        Assert.True(app.Rig.View.Last.CanCancel);
        // A Pause from a menu or a window drawn before the press discards nothing.
        await app.Choose(Toggle);
        Assert.False(app.Rig.Controller.TryPause());
        Assert.True(app.Rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.TrayListening, app.Rig.View.Last.Tray.ToolTip);

        app.Rig.Release();
        app.Rig.DrainUntil(() => app.Rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.False(Entry(Entries(app.Rig), Toggle).Enabled);
        Assert.False(app.Rig.View.Last.CanPause);
        await app.Choose(Toggle);
        Assert.False(app.Rig.Controller.TryPause());
        Assert.True(app.Rig.Controller.IsEnabled);

        decode.Set();
        app.Rig.DrainUntil(() => Inserted(app.Rig.View.Last));
        Assert.Equal(1, app.Rig.Platform.Paste.Count);
        Assert.True(Entry(Entries(app.Rig), Toggle).Enabled);
        Assert.True(app.Rig.View.Last.CanPause);
        Assert.True(app.Rig.Controller.TryPause());
        Assert.False(app.Rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.Paused, app.Rig.View.Last.Status);
    }

    [Fact]
    public async Task A_Pause_while_the_password_check_is_out_discards_nothing()
    {
        // The Mac's toggle() refuses while any attempt exists, its privacy
        // gate included, though its button stays live then. Windows' Pause
        // and the menu's are live too (nothing has repainted yet), and a
        // click on either changes nothing: the press goes on.
        using var rig = new Rig(options: o => o with { RunFieldCheck = check => Task.Run(check) });
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        Assert.True(rig.View.Last.CanPause);
        Assert.True(rig.View.Last.Tray.CanToggle);

        Assert.False(rig.Controller.TryPause());
        await rig.Controller.ToggleAsync();
        Assert.True(rig.Controller.IsEnabled);

        gate.Set();
        rig.TickUntil(() => rig.Platform.Microphone.Starts == 1);
        rig.Dispatcher.Drain();
        Assert.True(rig.View.Last.MicrophoneOpen);
        Assert.False(rig.View.Last.CanPause);
    }

    [Fact]
    public async Task A_Pause_clicked_before_the_hold_s_repaint_discards_nothing()
    {
        // The session is listening from the poll that admitted the press;
        // the window hears it at the next dispatcher turn. A click between
        // the two, on a Pause still drawn live, is refused all the same.
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Press();
        Assert.True(rig.Dispatcher.Pending > 0, "the hold's repaint has not run yet");
        Assert.True(rig.View.Last.CanPause);

        Assert.False(rig.Controller.TryPause());
        await rig.Controller.ToggleAsync();
        Assert.True(rig.Controller.IsEnabled);

        rig.Dispatcher.Drain();
        Assert.True(rig.View.Last.MicrophoneOpen);
        Assert.Equal(1, rig.Platform.Microphone.Starts);
    }

    [Fact]
    public async Task In_every_render_the_window_s_Enable_and_Pause_are_the_menu_s_toggle()
    {
        // One rule, decided once per render: whatever the window's buttons
        // offer, the menu's toggle offers, through enable, a whole dictation
        // (listening, transcribing, typing) and a pause.
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        using var pasteHold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Platform.Paste.Hold = pasteHold;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        decode.Set();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Inserting);
        pasteHold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        rig.Controller.TryPause();

        foreach (var state in rig.View.States)
        {
            var on = state.Tray.ToggleLabel == DictationCopy.PauseLabel;
            Assert.Equal(on ? state.CanPause : state.CanEnable, state.Tray.CanToggle);
        }

        // Not vacuous: the run held Pause both live and inactive.
        Assert.Contains(rig.View.States, s => s.Tray.ToggleLabel == DictationCopy.PauseLabel && s.CanPause);
        Assert.Contains(rig.View.States, s => s.MicrophoneOpen && !s.CanPause);
        Assert.Contains(rig.View.States, s => s.Status == DictationCopy.Transcribing && !s.CanPause);
        Assert.Contains(rig.View.States, s => s.Status == DictationCopy.Inserting && !s.CanPause);
    }

    // ─── The tooltip: the Mac headline's states ─────────────────────────

    [Fact]
    public async Task The_tooltip_says_off_not_paused_before_the_first_enable_and_after_one_that_failed()
    {
        // The Mac's words: only a pause is the user's choice, so the state
        // reads "off" (DictationMenuGlyph.paused).
        using var rig = new Rig();
        Assert.Equal((TrayIcon.Off, "Earlet — off"), (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));

        rig.Platform.Worker.InitSucceeds = false;
        await rig.EnableAsync();

        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
        Assert.Equal((TrayIcon.Off, DictationCopy.TrayOff), (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
    }

    [Fact]
    public async Task While_an_enable_loads_the_tooltip_says_starting_over_the_off_picture()
    {
        // A sign-in launch keeps the window hidden for the whole load, so the
        // icon is the only surface; the Mac's headline reads "Starting
        // dictation…" then, over the off picture.
        using var rig = new Rig();
        using var load = new ManualResetEventSlim();
        rig.Platform.Worker.HoldInit = load;

        var enabling = rig.Controller.EnableAsync();

        Assert.Equal(DictationCopy.Preparing, rig.View.Last.Status);
        Assert.Equal(
            new DictationTrayState(TrayIcon.Off, DictationCopy.TrayStarting, DictationCopy.EnableLabel, CanToggle: false),
            rig.View.Last.Tray);

        load.Set();
        await enabling;
        rig.Dispatcher.Drain();
        Assert.Equal(
            (TrayIcon.Ready, DictationCopy.TrayReady(rig.Controller.HoldKey)),
            (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
    }

    [Fact]
    public async Task An_enable_that_fails_while_starting_goes_back_to_off()
    {
        using var rig = new Rig();
        using var load = new ManualResetEventSlim();
        rig.Platform.Worker.HoldInit = load;
        rig.Platform.Worker.InitSucceeds = false;

        var enabling = rig.Controller.EnableAsync();
        Assert.Equal(DictationCopy.TrayStarting, rig.View.Last.Tray.ToolTip);

        load.Set();
        await enabling;
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.TrayOff, rig.View.Last.Tray.ToolTip);
    }

    [Fact]
    public async Task With_the_microphone_blocked_the_icon_warns_and_the_tooltip_says_so_until_access_is_back()
    {
        // The Mac's "Needs permission" and its warning triangle: a hold
        // outranks it, then it outranks ready.
        var blocked = (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked);
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.Equal(blocked, (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal((TrayIcon.Listening, DictationCopy.TrayListening), (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
        rig.Controller.Cancel();
        rig.Release();
        rig.Dispatcher.Drain();
        Assert.Equal(blocked, (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(
            (TrayIcon.Ready, DictationCopy.TrayReady(rig.Controller.HoldKey)),
            (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
    }

    [Fact]
    public async Task Paused_with_the_microphone_blocked_the_icon_still_warns_and_the_window_offers_the_fix_until_access_is_back()
    {
        // The Mac checks the grants before whether dictation is enabled, so
        // its needs-permission glyph shows while paused too: a pause does not
        // hide a microphone that will not open. Its header shows the setup
        // step and its action then, from the same resolution as the icon, so
        // the window says what the icon warns of and offers the fix.
        var blocked = (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked);
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();

        Assert.True(rig.Controller.TryPause());
        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.PausedMicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);
        Assert.Equal(blocked, (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
        // Off: the menu still offers Enable.
        Assert.Equal(new DictationTrayEntry(Toggle, DictationCopy.EnableLabel, Enabled: true), Entry(Entries(rig), Toggle));

        // Still blocked when the window is activated: nothing changes.
        rig.Controller.RecheckMicrophone();
        Assert.Equal(blocked, (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
        Assert.Equal(DictationCopy.PausedMicrophoneBlocked, rig.View.Last.Status);

        // Check again while off and still blocked: the same paused sentence,
        // which the window shows again so the press visibly did something.
        rig.Controller.RecheckMicrophone(checkAgain: true);
        Assert.Equal(DictationCopy.PausedMicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);
        Assert.Equal(blocked, (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));

        // Access back, seen when the window is activated: off again, and the
        // window says paused, with nothing left to fix.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal((TrayIcon.Off, DictationCopy.TrayOff), (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
        Assert.False(rig.View.Last.OffersMicrophoneSettings);
    }

    [Fact]
    public async Task Paused_with_the_microphone_blocked_by_policy_the_window_says_so_without_the_privacy_page()
    {
        // A policy the user cannot change: the paused sentence names it and
        // offers neither button, as the blocked sentence while on offers none.
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.DeniedByPolicy;
        await rig.EnableAsync();
        Assert.Equal(DictationCopy.MicrophoneBlockedByPolicy, rig.View.Last.Status);

        Assert.True(rig.Controller.TryPause());

        Assert.Equal(DictationCopy.PausedMicrophoneBlockedByPolicy, rig.View.Last.Status);
        Assert.False(rig.View.Last.OffersMicrophoneSettings);
        Assert.Equal(
            (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked),
            (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
    }

    [Fact]
    public async Task An_enable_from_the_menu_that_comes_on_with_the_microphone_blocked_brings_the_window_forward_with_the_fix()
    {
        // The Mac's menu offers "Finish permission setup…" while off with a
        // grant missing, which opens its window. Windows keeps Enable there
        // (it asks Windows again), and when the answer is still blocked the
        // window, hidden behind the icon, comes forward with the fix.
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        await rig.Controller.ToggleAsync();
        rig.Dispatcher.Drain();

        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);
        Assert.Equal(1, rig.View.RevealRequests);

        // Paused from the menu and enabled from it again, still blocked: again.
        await rig.Controller.ToggleAsync();
        Assert.Equal(DictationCopy.PausedMicrophoneBlocked, rig.View.Last.Status);
        await rig.Controller.ToggleAsync();
        Assert.Equal(2, rig.View.RevealRequests);

        // The window's own Enable, with the window in front: nothing to bring forward.
        Assert.True(rig.Controller.TryPause());
        await rig.EnableAsync();
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.Equal(2, rig.View.RevealRequests);

        // A menu Enable with access back stays in the notification area.
        Assert.True(rig.Controller.TryPause());
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        await rig.Controller.ToggleAsync();
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.Equal(2, rig.View.RevealRequests);
    }

    [Fact]
    public async Task At_sign_in_dictation_back_on_with_the_microphone_blocked_brings_the_window_forward()
    {
        // The Mac's sign-in launch shows no window only "with every grant in".
        using var rig = new Rig(
            beforeStart: r =>
            {
                r.Settings.Save(new DictationSettings(Enabled: true));
                r.Platform.Access.Access = MicrophoneAccess.Denied;
            },
            atLogin: true);
        Assert.False(rig.Startup.ShowWindow);

        await rig.Startup.Enabling;
        rig.Dispatcher.Drain();

        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.Equal(1, rig.View.RevealRequests);
    }

    [Fact]
    public async Task An_enable_loading_while_the_microphone_is_known_blocked_warns_rather_than_starting()
    {
        // The Mac's resolve(busy: true, allPermissionsGranted: false) is
        // needs-permission. The Enable asks again once the model is up, and
        // that answer replaces the last one.
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        rig.Controller.TryPause();
        using var load = new ManualResetEventSlim();
        rig.Platform.Worker.HoldInit = load;

        var enabling = rig.Controller.EnableAsync();
        Assert.Equal(DictationCopy.Preparing, rig.View.Last.Status);
        Assert.Equal(
            new DictationTrayState(TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked, DictationCopy.EnableLabel, CanToggle: false),
            rig.View.Last.Tray);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        load.Set();
        await enabling;
        rig.Dispatcher.Drain();
        Assert.Equal(
            (TrayIcon.Ready, DictationCopy.TrayReady(rig.Controller.HoldKey)),
            (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
    }

    [Fact]
    public async Task A_hold_Windows_kept_the_microphone_from_leaves_the_warning()
    {
        // The Mac's "a grant revoked while enabled turns the icon into the
        // warning at the next poll". Windows has no poll and says nothing when
        // access changes; the hold that could not open the microphone is how
        // this run learns it.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        rig.Platform.Microphone.StartSucceeds = false;

        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.OffersMicrophoneSettings);

        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(
            (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked),
            (rig.View.Last.Tray.Icon, rig.View.Last.Tray.ToolTip));
    }

    // ─── What the tray shows, and when it redraws ───────────────────────

    [Fact]
    public void Before_the_first_render_the_tray_shows_dictation_off_with_its_toggle_inactive()
    {
        var showing = new DictationTrayMenuState();

        Assert.Equal(
            new DictationTrayState(TrayIcon.Off, DictationCopy.TrayOff, DictationCopy.EnableLabel, CanToggle: false),
            showing.Shown);
        Assert.False(showing.LearningUndoable);
        Assert.Equal(DictationTrayMenu.Slots.Count, showing.Placed.Count);
        var entries = showing.Placed.OfType<DictationTrayEntry>().ToArray();
        Assert.Equal([Toggle, Sep, Show, Log, Sep, Quit], entries.Select(e => e.Item));
        Assert.False(Entry(entries, Toggle).Enabled);
    }

    [Fact]
    public void The_tray_redraws_when_either_half_changes_and_never_for_a_repeat()
    {
        var showing = new DictationTrayMenuState();
        var listening = State(recent: 1, cancel: true);

        Assert.False(showing.Update(DictationTrayMenuState.BeforeFirstRender));
        Assert.True(showing.Update(listening));
        Assert.Equal(listening, showing.Shown);
        Assert.Equal(DictationTrayMenu.Place(DictationTrayMenu.Entries(listening, learningUndoable: false)), showing.Placed);
        // An equal state is not a change, even as another object.
        Assert.False(showing.Update(listening with { Recent = listening.Recent! with { } }));

        Assert.True(showing.UpdateLearning(true));
        Assert.True(showing.LearningUndoable);
        Assert.Equal(DictationTrayMenu.Place(DictationTrayMenu.Entries(listening, learningUndoable: true)), showing.Placed);
        Assert.False(showing.UpdateLearning(true));

        // A controller render keeps the learning half, and the other way round.
        var idle = State(recent: 0, cancel: false);
        Assert.True(showing.Update(idle));
        Assert.Equal(DictationTrayMenu.Place(DictationTrayMenu.Entries(idle, learningUndoable: true)), showing.Placed);
        Assert.Contains(showing.Placed, e => e?.Item == Undo);
        Assert.DoesNotContain(showing.Placed, e => e?.Item == Cancel);

        Assert.True(showing.UpdateLearning(false));
        Assert.Equal(idle, showing.Shown);
        Assert.DoesNotContain(showing.Placed, e => e?.Item == Undo);
    }

    // ─── The layout ─────────────────────────────────────────────────────

    /// <summary>Every state the menu can be drawn in.</summary>
    public static TheoryData<int, bool, bool> EveryState()
    {
        var data = new TheoryData<int, bool, bool>();
        for (var recent = 0; recent < 3; recent++)
        {
            foreach (var learning in new[] { false, true })
            {
                foreach (var cancel in new[] { false, true })
                {
                    data.Add(recent, learning, cancel);
                }
            }
        }

        return data;
    }

    private static DictationTrayState State(int recent, bool cancel, bool saved = false) => new(
        TrayIcon.Ready,
        DictationCopy.TrayListening,
        DictationCopy.PauseLabel,
        CanToggle: true,
        recent switch
        {
            0 => null,
            1 => new DictationRecentMenu("Copy last text · 3:41 PM", CanCopy: true, NeedsRecovery: false),
            _ => new DictationRecentMenu("Copy last text · 3:41 PM", CanCopy: false, NeedsRecovery: true),
        },
        CanCancel: cancel,
        // A kept take's Recover last dictation (TakeTrayTests).
        SavedTake: saved ? new DictationSavedTakeMenu("Recover last dictation · 3:40 PM", CanRecover: true) : null);

    [Theory]
    [MemberData(nameof(EveryState))]
    public void A_separator_stands_only_between_two_groups(int recent, bool learning, bool cancel)
    {
        var entries = DictationTrayMenu.Entries(State(recent, cancel), learning);
        var items = entries.Select(e => e.Item).ToArray();

        Assert.NotEqual(Sep, items[0]);
        Assert.NotEqual(Sep, items[^1]);
        Assert.DoesNotContain(items.Zip(items.Skip(1)), pair => pair is (Sep, Sep));
        Assert.Equal(DictationTrayMenu.Items(State(recent, cancel), learning), items.Where(i => i != Sep));
        // Quit is last and alone, and the default item is always there.
        Assert.Equal([Sep, Quit], items[^2..]);
        Assert.Single(items, Show);
        Assert.All(entries.Where(e => e.Item == Sep), e => Assert.Equal(DictationTrayEntry.Separator, e));
        Assert.All(entries.Where(e => e.Item != Sep), e => Assert.False(string.IsNullOrEmpty(e.Label)));
    }

    [Fact]
    public void With_everything_on_offer_the_menu_reads_in_the_mac_s_order()
    {
        var entries = DictationTrayMenu.Entries(State(recent: 2, cancel: true), learningUndoable: true);

        Assert.Equal(
            [Recovery, Copy, Sep, Remembered, Review, Undo, Sep, Toggle, Cancel, Sep, Show, Log, Sep, Quit],
            entries.Select(e => e.Item));
        Assert.Equal(
            [
                DictationCopy.RecentTitleRecovery, "Copy last text · 3:41 PM", "",
                "Remembered your correction", "Review vocabulary…", "Undo last learning", "",
                "Pause dictation", "Cancel dictation", "",
                "Show Earlet", "Open log folder", "",
                "Quit Earlet",
            ],
            entries.Select(e => e.Label));
        // The Copy line follows its state; the two lines over the groups never act.
        Assert.False(Entry(entries, Copy).Enabled);
        Assert.False(Entry(entries, Recovery).Enabled);
        Assert.False(Entry(entries, Remembered).Enabled);
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void Every_line_has_a_slot_in_order_so_the_tray_only_shows_and_hides(int recent, bool learning, bool cancel)
    {
        var entries = DictationTrayMenu.Entries(State(recent, cancel), learning);

        var placed = DictationTrayMenu.Place(entries);

        Assert.Equal(DictationTrayMenu.Slots.Count, placed.Count);
        Assert.Equal(entries, placed.OfType<DictationTrayEntry>());
        Assert.All(
            placed.Select((entry, slot) => (entry, slot)).Where(p => p.entry is not null),
            p => Assert.Equal(DictationTrayMenu.Slots[p.slot], p.entry!.Item));
    }

    [Fact]
    public void The_slots_are_the_whole_menu_and_lines_out_of_order_are_refused()
    {
        Assert.Equal(
            DictationTrayMenu.Entries(State(recent: 2, cancel: true, saved: true), learningUndoable: true).Select(e => e.Item),
            DictationTrayMenu.Slots);

        var quit = new DictationTrayEntry(Quit, DictationCopy.QuitDictation, Enabled: true);
        var toggle = new DictationTrayEntry(Toggle, DictationCopy.PauseLabel, Enabled: true);
        Assert.Throws<ArgumentException>(() => DictationTrayMenu.Place([quit, toggle]));
        Assert.Throws<ArgumentException>(() => DictationTrayMenu.Place([toggle, toggle]));
    }

    [Fact]
    public void Layout_drops_separators_it_is_handed_and_puts_its_own_between_groups()
    {
        Assert.Equal(
            [Copy, Sep, Toggle, Sep, Quit],
            DictationTrayMenu.Layout([Sep, Copy, Sep, Sep, Toggle, Quit, Sep]));
        Assert.Empty(DictationTrayMenu.Layout([]));
    }

    [Fact]
    public void Every_tooltip_fits_what_NotifyIcon_accepts()
    {
        // NotifyIcon.Text throws past 127 characters (.NET 6 and later), and
        // the tooltip is where the state lives, the menu having no headline.
        var tips = new List<string>
        {
            DictationCopy.TrayOff, DictationCopy.TrayStarting, DictationCopy.TrayMicrophoneBlocked,
            DictationCopy.TrayListening, DictationCopy.TrayTranscribing, DictationCopy.TrayTyping,
        };
        tips.AddRange(DictationHotkeys.HoldChoices.Select(DictationCopy.TrayReady));

        Assert.All(tips, tip => Assert.InRange(tip.Length, 1, 127));
    }

    // ─── The WinForms half, read from source ────────────────────────────
    //
    // Pins on the source, not evidence that the menu behaves: WinForms cannot
    // run here, and no one has opened this menu on Windows.

    [Fact]
    public void The_tray_draws_the_model_s_entries_and_raises_what_was_chosen()
    {
        var code = File.ReadAllText(AppFile("DictationTray.cs")).ReplaceLineEndings("\n");
        var redraw = Member(code, "private void Redraw()");

        // What is shown, and when a change needs drawing, is the tested
        // DictationTrayMenuState's; the tray applies it, guards exactly as
        // written, so an inverted one is noticed here.
        Assert.Contains("private readonly DictationTrayMenuState showing = new();", code, StringComparison.Ordinal);
        Assert.Contains("var placed = showing.Placed;", redraw, StringComparison.Ordinal);
        Assert.Contains("line.Text = entry.Label;", redraw, StringComparison.Ordinal);
        Assert.Contains("line.Enabled = entry.Enabled;", redraw, StringComparison.Ordinal);
        Assert.Contains("lines[slot].Available = placed[slot] is not null;", redraw, StringComparison.Ordinal);
        var update = Member(code, "public void Update(DictationTrayState tray)");
        Assert.Contains("if (disposed || !showing.Update(tray))\n        {\n            return;\n        }", update, StringComparison.Ordinal);
        Assert.Contains("icon.Icon = Picture(tray.Icon);", update, StringComparison.Ordinal);
        Assert.Contains("icon.Text = tray.ToolTip;", update, StringComparison.Ordinal);
        Assert.Contains("Redraw();", update, StringComparison.Ordinal);
        var learning = Member(code, "public void UpdateLearning(bool undoable)");
        Assert.Contains(
            "if (disposed || !showing.UpdateLearning(undoable))\n        {\n            return;\n        }", learning, StringComparison.Ordinal);
        Assert.Contains("Redraw();", learning, StringComparison.Ordinal);
        // The icon is first drawn from what the state holder shows.
        Assert.Contains("Text = showing.Shown.ToolTip,", Member(code, "public DictationTray("), StringComparison.Ordinal);

        // Every slot's line is made once and put in the strip before the first draw.
        var constructor = Member(code, "public DictationTray(");
        var made = constructor.IndexOf("lines = [.. DictationTrayMenu.Slots.Select(Line)];", StringComparison.Ordinal);
        var added = constructor.IndexOf("menu.Items.AddRange(lines);", StringComparison.Ordinal);
        var drawn = constructor.IndexOf("Redraw();", StringComparison.Ordinal);
        Assert.True(made >= 0 && added > made && drawn > added, "slots made, added, then drawn");
        // A redraw never takes a line out of the strip: the item whose click
        // caused it keeps its parent.
        Assert.DoesNotContain("Items.Clear", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Items.Remove", code, StringComparison.Ordinal);

        var line = Member(code, "private System.Windows.Forms.ToolStripItem Line(DictationTrayItem item)");
        Assert.Contains("return new System.Windows.Forms.ToolStripSeparator();", line, StringComparison.Ordinal);
        Assert.Contains("line.Click += (_, _) => Chosen?.Invoke(this, item);", line, StringComparison.Ordinal);
        Assert.Contains("if (item == DictationTrayMenu.DefaultItem)", line, StringComparison.Ordinal);
        Assert.Contains("line.Font = boldFont;", line, StringComparison.Ordinal);
        // A left click on the icon is the default item.
        Assert.Contains("Chosen?.Invoke(this, DictationTrayMenu.DefaultItem);", constructor, StringComparison.Ordinal);
        // No fixed list of items survives beside the model's.
        Assert.DoesNotContain("new System.Windows.Forms.ToolStripMenuItem(\"", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new System.Windows.Forms.ToolStripMenuItem(DictationCopy", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_hands_the_menu_both_halves_and_runs_its_choices_through_the_commands()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var constructor = Member(code, "public MainWindow(");

        Assert.Contains(
            "new DictationTrayCommands(controller, vocabulary, ShowFromOutside, ReviewVocabulary, Quit);",
            constructor,
            StringComparison.Ordinal);
        Assert.Contains("tray.Chosen += async (_, item) => await trayCommands.ChooseAsync(item);", constructor, StringComparison.Ordinal);
        Assert.Contains("tray.Update(state.Tray);", Member(code, "public void Render(DictationViewState state)"), StringComparison.Ordinal);
        Assert.Contains(
            "tray.UpdateLearning(state.CanUndoLearning);",
            Member(code, "public void Render(VocabularyViewState state)"),
            StringComparison.Ordinal);

        var review = Member(
            File.ReadAllText(AppFile("MainWindow.Tray.cs")).ReplaceLineEndings("\n"), "private void ReviewVocabulary()");
        Assert.Contains("ShowFromOutside();", review, StringComparison.Ordinal);
        var scrolled = review.IndexOf("VocabularySection.BringIntoView();", StringComparison.Ordinal);
        var focused = review.IndexOf("TeachWord.Focus();", StringComparison.Ordinal);
        Assert.True(scrolled >= 0 && focused > scrolled, "scrolled to the section, then the keyboard in its first field");
        Assert.Contains("DispatcherPriority.Loaded", review, StringComparison.Ordinal);
        Assert.DoesNotContain("LearnBox.Focus", review, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_s_Pause_button_goes_through_the_same_refusal_as_the_menu_s()
    {
        // One rule: the window's one Enable/Pause button is drawn from the
        // header, whose liveness is the menu item's (WindowHeaderTests), and a
        // click on Pause that lands after a press began is refused by the
        // same TryPause the menu calls.
        var header = File.ReadAllText(AppFile("MainWindow.Header.cs")).ReplaceLineEndings("\n");
        var window = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

        Assert.Contains("EnablePauseButton.IsEnabled = state.CanToggle;", Member(header, "private void RenderHeader("), StringComparison.Ordinal);
        var click = Member(header, "private async void EnablePauseClick(");
        Assert.Contains("case { ToggleEnables: false }:\n                controller.TryPause();", click, StringComparison.Ordinal);
        Assert.Contains("case { ToggleEnables: true }:\n                await controller.EnableAsync();", click, StringComparison.Ordinal);
        Assert.DoesNotContain("controller.Pause()", header + window, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleAsync", header + window, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pause is off for a whole dictation, and Enable with it, so a control
    /// that goes with the keyboard on it hands the keyboard to the first
    /// live and shown one of Enable/Pause and Cancel (shown while a dictation
    /// listens or transcribes), then to the Troubleshooting section (always
    /// there), never to a disabled or collapsed button, which would take
    /// nothing; and Render decides after this render's buttons are set, not
    /// before.
    /// </summary>
    [Fact]
    public void A_control_that_goes_with_the_keyboard_hands_it_to_a_live_button_even_during_a_dictation()
    {
        var window = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var recent = File.ReadAllText(AppFile("MainWindow.Recent.cs")).ReplaceLineEndings("\n");

        var helper = Member(window, "private void FocusRestingButton()");
        Assert.Contains("new UIElement[] { EnablePauseButton, CancelButton }", helper, StringComparison.Ordinal);
        Assert.Contains(
            ".FirstOrDefault(button => button.IsEnabled && button.Visibility == Visibility.Visible)", helper, StringComparison.Ordinal);
        Assert.Contains("?? TroubleshootingSection)", helper, StringComparison.Ordinal);
        Assert.Contains(".Focus()", helper, StringComparison.Ordinal);

        // The four places a control goes with the keyboard on it.
        Assert.Contains("FocusRestingButton();", Member(window, "public void Render(DictationViewState state)"), StringComparison.Ordinal);
        Assert.Contains("FocusRestingButton();", Member(window, "private void RenderLearningReceipt("), StringComparison.Ordinal);
        Assert.Contains("FocusRestingButton();", Member(window, "private void RenderPractice("), StringComparison.Ordinal);
        Assert.Contains("FocusRestingButton();", Member(recent, "private void RenderRecent("), StringComparison.Ordinal);
        Assert.DoesNotContain("? PauseButton : EnableButton", window + recent, StringComparison.Ordinal);

        // Check again's buttons, or Cancel, going: asked before they hide,
        // moved after this render's Enable/Pause and Cancel are set.
        var render = Member(window, "public void Render(DictationViewState state)");
        var asked = render.IndexOf(
            "var actionsGoing = !state.OffersMicrophoneSettings && MicrophoneActions.IsKeyboardFocusWithin;", StringComparison.Ordinal);
        var cancelAsked = render.IndexOf(
            "var cancelGoing = !state.CanCancel && CancelButton.IsKeyboardFocusWithin;", StringComparison.Ordinal);
        var header = render.IndexOf("RenderHeader(state.Header);", StringComparison.Ordinal);
        var set = render.IndexOf("CancelButton.IsEnabled = state.CanCancel;", StringComparison.Ordinal);
        var collapsed = render.IndexOf(
            "CancelButton.Visibility = state.CanCancel ? Visibility.Visible : Visibility.Collapsed;", StringComparison.Ordinal);
        var moved = render.IndexOf("if (actionsGoing || cancelGoing)\n        {", StringComparison.Ordinal);
        var hidden = render.IndexOf("MicrophoneActions.Visibility =", StringComparison.Ordinal);
        Assert.True(asked >= 0 && cancelAsked > asked && header > cancelAsked && set > header && collapsed > set
            && moved > collapsed && hidden > moved,
            "asked, buttons set and Cancel shown or collapsed, keyboard moved, then the actions hidden");
        Assert.Contains("FocusRestingButton();", render[moved..hidden], StringComparison.Ordinal);
    }
}
