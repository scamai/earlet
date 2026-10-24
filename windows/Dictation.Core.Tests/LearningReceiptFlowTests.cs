using System.Xml.Linq;
using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The learning receipt end to end, as the app composes it: the controller's
/// real correction watch over the fake field, the Vocabulary section on the
/// same dispatcher, the section's receipt handed back to the controller for
/// the indicator, and the window's wiring read from source.
/// </summary>
public sealed class LearningReceiptFlowTests
{
    private const string Heard = "trovio";
    private const string Meant = "Trovioo";

    /// <summary>The controller and the Vocabulary section, wired as
    /// MainWindow wires them.</summary>
    private sealed class App : IDisposable
    {
        public App(Action<Rig>? setUp = null, Microsoft.Extensions.Logging.ILoggerFactory? logs = null,
            Func<DictationControllerOptions, DictationControllerOptions>? options = null)
        {
            Rig = new Rig(learning: true, logs: logs, options: options);
            Rig.Platform.Worker.Transcript = "we use trovio for the checks";
            Rig.Platform.Field.FixAfterEachPaste(Heard, Meant);
            setUp?.Invoke(Rig);
            Vocabulary = new VocabularyModel(Rig.Learning!, View, Rig.Dispatcher.Post, Rig.Controller.Corrections);
            Vocabulary.CorrectionRemembered += (_, _) => Rig.Controller.ShowCorrectionRemembered(announce: !WindowActive);
            Rig.View.OnRender = state => Vocabulary.ApplyLearningSwitch(state.LearnFromCorrections);
            Vocabulary.Refresh();
        }

        public Rig Rig { get; }
        public RecordingVocabularyView View { get; } = new();

        /// <summary>The Dictation window is in front (MainWindow's IsActive).</summary>
        public bool WindowActive { get; set; }
        public VocabularyModel Vocabulary { get; }

        public string? Receipt => View.Last.LearningReceipt;

        public IEnumerable<string> Pills => Rig.Hud.Shows.Select(m => m.Text);

        public LearningState OnDisk() => new LearningStore(Rig.LearningDirectory).Load().State;

        public void DictateOnce()
        {
            var inserted = Rig.View.States.Count(Inserted);
            Rig.Press();
            Rig.Release();
            Rig.DrainUntil(() => Rig.View.States.Count(Inserted) > inserted);
            Rig.Dispatcher.Drain();
        }

        public void DictateAndWatch()
        {
            DictateOnce();
            Rig.WaitForWatch();
            Rig.Dispatcher.Drain();
        }

        /// <summary>One dictation whose watch is parked with the fix in hand.</summary>
        public void DictateAndPark()
        {
            Rig.Time.Limit = Rig.Time.NowMs + 3_500;
            DictateOnce();
            Rig.Time.WaitUntilParked();
        }

        /// <summary>Let the parked watch settle its fix, and hear everything it said.</summary>
        public void SettleTheFix()
        {
            Rig.Time.Release();
            Rig.WaitForWatch();
            Rig.Dispatcher.Drain();
        }

        public void Dispose()
        {
            Rig.Time.Release();
            Vocabulary.Dispose();
            Rig.Dispose();
        }
    }

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static (FileLoggerFactory Logs, string Path) FileLog()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        return (FileLoggerFactory.TryCreate(dir, out _)!, Path.Combine(dir, FileLoggerFactory.FileName));
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    // ─── The Mac's first LearningFeedbackTests scenario ─────────────────

    [Fact]
    public async Task The_apps_correction_observer_delivers_a_saved_receipt_and_a_working_undo()
    {
        var (logs, log) = FileLog();
        using (logs)
        {
            using var app = new App(logs: logs);
            await app.Rig.EnableAsync();

            app.DictateAndWatch();

            Assert.True(app.View.Last.CanUndoLearning);
            Assert.Equal([Meant], app.OnDisk().Words.Select(w => w.Text));
            Assert.Contains("Remembered", app.Receipt, StringComparison.Ordinal);
            Assert.Equal(DictationCopy.HudCorrectionRemembered, app.Rig.Hud.Current?.Text);

            app.Vocabulary.UndoLastLearning();

            var state = app.OnDisk();
            Assert.Empty(state.Words);
            Assert.Empty(state.Aliases);
            Assert.Empty(state.Pending);
            Assert.Empty(app.View.Last.Rows);
            Assert.False(app.View.Last.CanUndoLearning);
            Assert.Contains("undone", app.Receipt, StringComparison.Ordinal);
            var text = ReadLog(log);
            Assert.Contains("Undid the last learned change; file Saved", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Meant, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Heard, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─── The indicator ──────────────────────────────────────────────────

    [Fact]
    public async Task The_indicator_says_it_remembered_once_neutral_for_three_seconds_and_aloud()
    {
        using var app = new App();
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        var pill = Assert.Single(app.Rig.Hud.Shows, m => m.Text == DictationCopy.HudCorrectionRemembered);
        Assert.Equal("Remembered your correction — review or undo in Earlet", pill.Text);
        Assert.Equal((HudTone.Neutral, (TimeSpan?)TimeSpan.FromSeconds(3)), (pill.Tone, pill.Dwell));
        Assert.True(app.Rig.Hud.Announced[^1]);

        app.Rig.Clock.Now += TimeSpan.FromSeconds(3);
        app.Rig.Controller.Tick();
        Assert.Null(app.Rig.Hud.Current);
    }

    /// <summary>With the Dictation window in front, its own live line reads
    /// the receipt, so the indicator shows its line without saying it:
    /// Narrator hears the receipt once, as on the Mac.</summary>
    [Fact]
    public async Task In_front_of_the_window_the_indicator_shows_the_line_without_saying_it()
    {
        using var app = new App { WindowActive = true };
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        var at = app.Rig.Hud.Shows.FindLastIndex(m => m.Text == DictationCopy.HudCorrectionRemembered);
        Assert.True(at >= 0);
        Assert.False(app.Rig.Hud.Announced[at]);
        Assert.Equal(DictationCopy.LearningRemembered, app.Receipt);
        // The name the window gives it quietly: the whole line, never the
        // title without its "what to do" half (HINTS' NameQuietly).
        Assert.Equal(DictationCopy.HudCorrectionRemembered, app.Rig.Hud.Shows[at].QuietName);
    }

    /// <summary>
    /// The pill is unrelated news, so a caution still up — "Couldn't copy",
    /// a recogniser that could not restart, a refusal — keeps its "what to
    /// do" half; a neutral line gives way. Once the caution has gone, the
    /// next receipt shows.
    /// </summary>
    [Fact]
    public void A_caution_still_on_the_indicator_keeps_it_and_a_neutral_line_gives_way()
    {
        var clock = new FakeClock();
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold);

        feed.Copied(false);
        feed.CorrectionRemembered();
        Assert.Equal(DictationCopy.HudCopyFailed, hud.Current?.Text);
        feed.RecognizerCouldNotRestart();
        feed.CorrectionRemembered();
        Assert.Equal(DictationCopy.HudRecognizerCouldNotRestart, hud.Current?.Text);

        feed.Copied(true);
        feed.CorrectionRemembered(announce: false);
        Assert.Equal(DictationCopy.HudCorrectionRemembered, hud.Current?.Text);
        Assert.False(hud.Announced[^1]);

        feed.Copied(false);
        clock.Advance(DictationHudModel.FailureDwell);
        feed.Tick();
        Assert.Null(hud.Current);
        feed.CorrectionRemembered();
        Assert.Equal(DictationCopy.HudCorrectionRemembered, hud.Current?.Text);
        Assert.True(hud.Announced[^1]);
    }

    [Fact]
    public async Task A_dictation_that_taught_nothing_new_says_nothing_on_the_indicator()
    {
        using var app = new App(rig => rig.Platform.Field.Fix = null);
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        Assert.Null(app.Receipt);
        Assert.DoesNotContain(DictationCopy.HudCorrectionRemembered, app.Pills);
    }

    [Fact]
    public async Task No_remembered_pill_while_the_next_hold_listens()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndPark();
        app.Rig.Press();
        app.Rig.Dispatcher.Drain();
        Assert.True(app.Rig.Controller.Shown?.MicrophoneOpen);

        app.SettleTheFix();

        Assert.Equal(DictationCopy.LearningRemembered, app.Receipt);
        Assert.DoesNotContain(DictationCopy.HudCorrectionRemembered, app.Pills);
        app.Rig.Release();
    }

    /// <summary>
    /// HINTS redraws the listening pill from the controller's clock, once a
    /// second through the countdown. A fix settled while the hold listens is
    /// refused by the controller's at-rest gate and never slips in between
    /// those redraws: every pill from the press to the release's "Typed" is
    /// the hold's own.
    /// </summary>
    [Fact]
    public async Task No_remembered_pill_through_the_held_clock_and_countdown()
    {
        // Nobody speaks into the held key for four minutes: without silence
        // stops, which would end it at one (HoldPolicyTests).
        using var app = new App(options: o => o with { HoldPolicy = o.HoldPolicy.WithoutSilenceStops() });
        await app.Rig.EnableAsync();
        app.DictateAndPark();
        app.Rig.Press();
        app.Rig.Dispatcher.Drain();
        var pressed = app.Rig.Hud.Shows.Count;

        app.SettleTheFix();
        Assert.Equal(DictationCopy.LearningRemembered, app.Receipt);
        foreach (var step in new[] { 1_000, 120_000, 120_000, 5_000, 1_000, 1_000 })
        {
            app.Rig.Clock.Advance(TimeSpan.FromMilliseconds(step));
            app.Rig.Controller.Tick();
            app.Rig.Dispatcher.Drain();
        }

        var held = app.Pills.Skip(pressed - 1).ToArray();
        Assert.Contains("Listening — Stops in 0:54 · Release to insert", held);
        Assert.Contains("Listening — Stops in 0:52 · Release to insert", held);
        Assert.All(held, text => Assert.StartsWith("Listening", text, StringComparison.Ordinal));

        app.Rig.Release();
        app.Rig.DrainUntil(() => app.Rig.View.States.Count(Inserted) == 2);
        var released = app.Pills.Skip(pressed - 1).TakeWhile(text => text != "Typed").ToArray();
        Assert.Contains(DictationCopy.HudTranscribing, released);
        Assert.DoesNotContain(DictationCopy.HudCorrectionRemembered, released);
    }

    [Fact]
    public async Task No_remembered_pill_while_a_press_waits_on_its_password_check()
    {
        var checking = new TaskCompletionSource<SecureFieldState>();
        var hold = false;
        using var app = new App(options: o => o with
        {
            RunFieldCheck = check => hold ? checking.Task : Task.FromResult(check()),
        });
        await app.Rig.EnableAsync();
        app.DictateAndPark();
        hold = true;
        app.Rig.Press();
        app.Rig.Dispatcher.Drain();
        Assert.False(app.Rig.Controller.Shown?.MicrophoneOpen);

        app.SettleTheFix();

        Assert.Equal(DictationCopy.LearningRemembered, app.Receipt);
        Assert.DoesNotContain(DictationCopy.HudCorrectionRemembered, app.Pills);
        checking.SetResult(SecureFieldState.NotSecure);
        app.Rig.Release();
    }

    [Fact]
    public async Task No_remembered_pill_once_dictation_is_paused()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndPark();
        // The fix settles, and its outcome is on its way to the UI thread,
        // when the user pauses.
        app.Rig.Time.Release();
        app.Rig.WaitForWatch();

        app.Rig.Controller.Pause();
        app.Rig.Dispatcher.Drain();

        Assert.Equal(DictationCopy.LearningRemembered, app.Receipt);
        Assert.DoesNotContain(DictationCopy.HudCorrectionRemembered, app.Pills);
    }

    [Fact]
    public async Task A_refusal_still_on_the_indicator_keeps_it()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndPark();
        app.Rig.Platform.SecureField.Next = SecureFieldState.Secure;
        app.Rig.Press();
        app.Rig.Dispatcher.Drain();
        var refusal = app.Rig.Hud.Current;
        Assert.Equal(DictationCopy.HudPasswordFieldAtPress, refusal?.Text);

        app.SettleTheFix();

        Assert.Equal(DictationCopy.LearningRemembered, app.Receipt);
        Assert.Same(refusal, app.Rig.Hud.Current);
        app.Rig.Release();
    }

    // ─── The section, through the controller ────────────────────────────

    [Fact]
    public async Task Switching_learning_off_takes_the_receipt_and_its_undo_down()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        Assert.True(app.View.Last.CanUndoLearning);

        app.Rig.Controller.SetLearnFromCorrections(false);

        Assert.Null(app.Receipt);
        Assert.False(app.View.Last.CanUndoLearning);
        Assert.Equal([Meant], app.OnDisk().Words.Select(w => w.Text));
    }

    public static TheoryData<string> UserClears => new() { "dismiss", "teach", "remove", "forget all" };

    /// <summary>
    /// The Mac's <c>observe</c> ignores a watch whose feedback the user
    /// cleared (its <c>currentObservationID</c> guard), so nothing more is
    /// learned from it. Windows learns on the watch's own thread, so Dismiss,
    /// Add, Remove and Forget all end the watch: otherwise its fix would be
    /// saved, with an Undo nobody is offered.
    /// </summary>
    [Theory]
    [MemberData(nameof(UserClears))]
    public async Task Dismiss_add_remove_or_forget_all_while_a_watch_runs_stops_it_learning_as_the_mac_does(string how)
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        Assert.True(await app.Vocabulary.Teach("SkyDock", ""));
        app.DictateAndPark();

        switch (how)
        {
            case "dismiss":
                app.Vocabulary.DismissLearningReceipt();
                break;
            case "teach":
                Assert.True(await app.Vocabulary.Teach("Sentinel", ""));
                break;
            case "remove":
                app.Vocabulary.Remove(app.View.Last.Rows.Single(r => r.Text == "SkyDock"));
                break;
            default:
                app.Vocabulary.AskForgetAll();
                app.Vocabulary.ConfirmForgetAll();
                break;
        }

        app.SettleTheFix();

        Assert.DoesNotContain(Meant, app.OnDisk().Words.Select(w => w.Text));
        Assert.Empty(app.OnDisk().Aliases);
        Assert.Null(app.Receipt);
        Assert.False(app.View.Last.CanUndoLearning);
        Assert.DoesNotContain(DictationCopy.HudCorrectionRemembered, app.Pills);
    }

    /// <summary>Undo is not one of them: the Mac keeps the observation
    /// through an undo, and so does Windows.</summary>
    [Fact]
    public async Task Undo_leaves_the_watch_running()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        Assert.True(app.View.Last.CanUndoLearning);
        app.DictateAndPark();
        app.Vocabulary.UndoLastLearning();
        Assert.Empty(app.OnDisk().Words);

        app.SettleTheFix();

        Assert.Equal([Meant], app.OnDisk().Words.Select(w => w.Text));
        Assert.Equal(DictationCopy.LearningRemembered, app.Receipt);
    }

    /// <summary>The Mac's "an undo whose save fails stays unsaved until flush",
    /// through the app's own last save at quit.</summary>
    [Fact]
    public async Task An_undo_whose_save_fails_is_saved_by_the_last_save_at_quit()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        var folder = app.Rig.LearningDirectory;
        Directory.Move(folder, folder + ".aside");
        File.WriteAllText(folder, "a file where the folder should be");

        app.Vocabulary.UndoLastLearning();

        Assert.Null(app.Receipt);
        Assert.Equal(DictationCopy.VocabularyNotSaved, app.View.Last.NotSavedText);
        File.Delete(folder);
        Directory.Move(folder + ".aside", folder);
        Assert.Equal([Meant], app.OnDisk().Words.Select(w => w.Text));

        app.Rig.Controller.Dispose();

        Assert.Empty(app.OnDisk().Words);
    }

    // ─── The window ─────────────────────────────────────────────────────
    //
    // Pins on the source, not evidence that the window behaves: WPF cannot
    // run here, and no one has seen this section on Windows.

    [Fact]
    public void The_section_sits_after_the_recent_result_and_before_try_dictation_hidden_with_a_polite_line()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var section = Named(xaml, "LearningReceiptSection");
        var column = section.Parent!.Elements().ToList();

        // The Mac's order: Recent result, Learning from you, Try dictation.
        Assert.Equal(column.IndexOf(Named(xaml, "RecentSection")) + 1, column.IndexOf(section));
        Assert.Equal(column.IndexOf(section) + 1, column.IndexOf(Named(xaml, "PracticeSection")));
        Assert.Equal("Collapsed", (string?)section.Attribute("Visibility"));
        var line = Named(xaml, "LearningReceipt");
        Assert.Same(section, line.Parent);
        Assert.Equal("Polite", (string?)line.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.LiveSetting"));
        var undo = Named(xaml, "UndoLearningButton");
        var dismiss = Named(xaml, "DismissLearningButton");
        Assert.Same(undo.Parent, dismiss.Parent);
        Assert.Same(section, undo.Parent!.Parent);
        Assert.Equal("UndoLearningClick", (string?)undo.Attribute("Click"));
        Assert.Equal("DismissLearningClick", (string?)dismiss.Attribute("Click"));
    }

    [Fact]
    public void The_window_hands_a_receipt_to_the_controller_and_renders_it_from_the_section()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var constructor = Member(code, "public MainWindow(");

        // Spoken from behind only: in front, SetLiveText reads the section's line.
        Assert.Contains(
            "vocabulary.CorrectionRemembered += (_, _) => controller.ShowCorrectionRemembered(announce: !IsActive);",
            constructor, StringComparison.Ordinal);
        Assert.Contains("if (IsActive && !microphoneOpen && LiveRegionAnnouncement.ShouldRaise(previous, text))",
            Member(code, "private void SetLiveText("), StringComparison.Ordinal);
        Assert.Contains("RenderLearningReceipt(state);", Member(code, "public void Render(VocabularyViewState state)"), StringComparison.Ordinal);
        var render = Member(code, "private void RenderLearningReceipt(");
        // Hidden with no receipt: no empty heading and stray Dismiss in every window.
        Assert.Contains("var shown = state.LearningReceipt is not null;", render, StringComparison.Ordinal);
        Assert.Contains(
            "LearningReceiptSection.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;", render, StringComparison.Ordinal);
        Assert.Contains(
            "UndoLearningButton.Visibility = state.CanUndoLearning ? Visibility.Visible : Visibility.Collapsed;", render, StringComparison.Ordinal);
        Assert.Contains("SetLiveText(LearningReceipt, state.LearningReceipt ?? string.Empty);", render, StringComparison.Ordinal);
        Assert.True(
            render.IndexOf("LearningReceiptSection.Visibility", StringComparison.Ordinal)
            < render.IndexOf("SetLiveText(LearningReceipt", StringComparison.Ordinal),
            "the line is shown before its text is announced");
        Assert.Contains("vocabulary.UndoLastLearning()", Member(code, "private void UndoLearningClick("), StringComparison.Ordinal);
        Assert.Contains("vocabulary.DismissLearningReceipt()", Member(code, "private void DismissLearningClick("), StringComparison.Ordinal);
    }

    /// <summary>A button that goes with the focus on it would leave the
    /// keyboard on nothing: Undo hands it to Dismiss, the section to the
    /// window's resting button (Pause or Enable, Cancel during a dictation),
    /// before either is hidden. Asked of the logical focus, which an
    /// inactive window keeps and gives back to the keyboard on activation;
    /// keyboard focus is false for every element then, which is when a pool
    /// outcome usually takes the section down.</summary>
    [Fact]
    public void A_button_about_to_go_hands_the_focus_on_first_even_with_the_window_behind()
    {
        var render = Member(File.ReadAllText(AppFile("MainWindow.xaml.cs")), "private void RenderLearningReceipt(");

        var toDismiss = render.IndexOf("DismissLearningButton.Focus();", StringComparison.Ordinal);
        var toButtons = render.IndexOf("FocusRestingButton();", StringComparison.Ordinal);
        Assert.True(toDismiss >= 0 && toButtons >= 0);
        Assert.True(toDismiss < render.IndexOf("UndoLearningButton.Visibility", StringComparison.Ordinal));
        Assert.True(toButtons < render.IndexOf("LearningReceiptSection.Visibility", StringComparison.Ordinal));
        Assert.Contains("var focused = FocusManager.GetFocusedElement(this);", render, StringComparison.Ordinal);
        Assert.Contains("if (shown && !state.CanUndoLearning && ReferenceEquals(focused, UndoLearningButton))", render, StringComparison.Ordinal);
        Assert.Contains(
            "else if (!shown && (ReferenceEquals(focused, UndoLearningButton) || ReferenceEquals(focused, DismissLearningButton)))",
            render, StringComparison.Ordinal);
        Assert.DoesNotContain("IsKeyboardFocus", render, StringComparison.Ordinal);
    }

    [Fact]
    public void The_section_is_labelled_with_the_macs_words()
    {
        var labels = Member(File.ReadAllText(AppFile("MainWindow.xaml.cs")), "private void ShowVocabularyLabels(");

        Assert.Contains("LearningReceiptHeading.Text = DictationCopy.LearningReceiptHeading;", labels, StringComparison.Ordinal);
        Assert.Contains("DictationCopy.UndoLearning", labels, StringComparison.Ordinal);
        Assert.Contains("DismissLearningButton.Content = DictationCopy.DismissLearningReceipt;", labels, StringComparison.Ordinal);
        Assert.Equal("Learning from you", DictationCopy.LearningReceiptHeading);
        Assert.Equal("Undo learning", DictationCopy.UndoLearning);
        Assert.Equal("Dismiss", DictationCopy.DismissLearningReceipt);

        // Word for word what the Mac's section and model say.
        var mac = string.Concat(
            File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "..", "macos", "Sources", "DictationApp", "DictationActivityView.swift")),
            File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "..", "macos", "Sources", "DictationAppCore", "DictationVocabulary.swift")));
        foreach (var line in new[]
                 {
                     DictationCopy.LearningReceiptHeading, DictationCopy.UndoLearning, DictationCopy.DismissLearningReceipt,
                     DictationCopy.LearningRemembered, DictationCopy.LearningUndone, DictationCopy.LearningUndoExpired,
                 })
        {
            Assert.Contains($"\"{line}\"", mac, StringComparison.Ordinal);
        }
    }
}
