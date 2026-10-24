using Dictation.Core.Learning;
using Dictation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// The window render mode's half that runs on Linux: each scenario's script
/// drives the real controller and Vocabulary section, over the inert
/// <see cref="DictationRenderPlatform"/>, into the state the scenario names,
/// and the parts stay inside their scratch folder. Composed as
/// <c>MainWindow</c> composes them for the render
/// (<see cref="DictationRenderModeSurfaceTests"/> pins that), with a queue in
/// place of the dispatcher, drained on the one thread that drives, as WPF's
/// dispatcher runs everything on the UI thread. The pictures themselves need
/// WPF and Windows.
/// </summary>
public sealed class DictationWindowRenderTests
{
    /// <summary>The window as the render builds it, minus the WPF.</summary>
    private sealed class Stage : IDisposable
    {
        /// <summary>The thread a drive runs on, or 0 between drives.</summary>
        private int _driver;

        /// <summary>Renders of the window seen on another thread during a drive.</summary>
        private int _offThread;

        public Stage(DictationWindowRenderScenario scenario)
        {
            Parts = DictationRenderComposition.For(scenario);
            Controller = new DictationController(
                Parts.Platform,
                View,
                Dispatcher.Post,
                NullLoggerFactory.Instance,
                Parts.Settings,
                Parts.Environment,
                Parts.AppDirectory,
                Parts.Options,
                learning: Parts.Learning);
            Vocabulary = new VocabularyModel(
                Parts.Learning, VocabularyView, Dispatcher.Post, Parts.Relay(Controller.Corrections), new SerialWorkQueue().Run);
            View.OnRender = _ =>
            {
                var driver = Volatile.Read(ref _driver);
                if (driver != 0 && driver != Environment.CurrentManagedThreadId)
                {
                    Interlocked.Increment(ref _offThread);
                }
            };
            Controller.Start();
            Vocabulary.Refresh();
        }

        public DictationRenderComposition Parts { get; }

        public RecordingView View { get; } = new();

        public RecordingVocabularyView VocabularyView { get; } = new();

        public DeferredDispatcher Dispatcher { get; } = new();

        public DictationController Controller { get; }

        public VocabularyModel Vocabulary { get; }

        public DictationViewState Shown => Controller.Shown!;

        public VocabularyViewState Section => Vocabulary.Shown!;

        /// <summary>
        /// Drive <paramref name="scenario"/> on this thread, as the app drives
        /// it on WPF's: the script's continuations, the controller's own
        /// awaits and the window's posts all run here, one at a time. Under
        /// xunit's context, two could run at once on two threads (a tick
        /// beside the end of an Enable), which the app never does; this
        /// checks that none of the window's renders ran elsewhere.
        /// </summary>
        /// <param name="deadline">The stage's
        /// <see cref="DictationRenderStage.Deadline"/>, when not the script's.</param>
        public void Drive(DictationWindowRenderScenario scenario, TimeSpan? deadline = null)
        {
            var stage = StageOf(deadline);
            _driven = scenario;
            OnTheDriver(() => DictationWindowRenderScript.DriveAsync(scenario, stage));
        }

        /// <summary>The scenario the stage was driven into, finished as the
        /// render finishes it once its pictures are taken.</summary>
        public void Finish()
        {
            if (_driven is { } scenario)
            {
                _driven = null;
                OnTheDriver(() => DictationWindowRenderScript.FinishAsync(scenario, StageOf(null)));
            }
        }

        /// <summary>Finished first, as the render finishes a scenario before
        /// its window lets go.</summary>
        public void Dispose()
        {
            try
            {
                Finish();
            }
            finally
            {
                _thread.Close();
                Controller.Dispose();
                Vocabulary.Dispose();
                Dispatcher.Drain();
                Parts.Dispose();
            }
        }

        /// <summary>The UI thread every drive and finish of this stage runs on.</summary>
        private readonly OneThread _thread = new();

        /// <summary>The scenario last driven and not yet finished.</summary>
        private DictationWindowRenderScenario? _driven;

        private DictationRenderStage StageOf(TimeSpan? deadline)
        {
            var stage = new DictationRenderStage(
                Controller,
                Vocabulary,
                Parts,
                () =>
                {
                    Dispatcher.Drain();
                    return Task.Delay(1);
                });
            return deadline is { } limit ? stage with { Deadline = limit } : stage;
        }

        private void OnTheDriver(Func<Task> drive)
        {
            Volatile.Write(ref _driver, Environment.CurrentManagedThreadId);
            try
            {
                _thread.Run(drive);
            }
            finally
            {
                Volatile.Write(ref _driver, 0);
            }

            Assert.Equal(0, Volatile.Read(ref _offThread));
        }
    }

    /// <summary>
    /// WPF's dispatcher as a stage sees it: what is posted to it is queued and
    /// run, one at a time, on the thread that called <see cref="Run"/>, while
    /// a drive or a finish runs. Between the two it is queued and waits, as
    /// WPF's dispatcher waits while the render draws: an Enable or a
    /// dictation a scenario left under way comes back to the one context it
    /// started on. Once the stage is closed, whatever is posted, and whatever
    /// was still queued, runs on the thread pool, as it would have under
    /// xunit's own context: nothing pumps this thread any more.
    /// </summary>
    private sealed class OneThread : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private bool _open = true;

        public void Run(Func<Task> drive)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                var task = drive();
                while (!task.IsCompleted)
                {
                    if (!RunNext())
                    {
                        Thread.Sleep(1);
                    }
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_gate)
            {
                if (_open)
                {
                    _queue.Enqueue((d, state));
                    return;
                }
            }

            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Nothing here waits on the UI thread.");

        public override SynchronizationContext CreateCopy() => this;

        private bool RunNext()
        {
            (SendOrPostCallback Callback, object? State) next;
            lock (_gate)
            {
                if (!_queue.TryDequeue(out next))
                {
                    return false;
                }
            }

            next.Callback(next.State);
            return true;
        }

        public void Close()
        {
            (SendOrPostCallback Callback, object? State)[] left;
            lock (_gate)
            {
                _open = false;
                left = [.. _queue];
                _queue.Clear();
            }

            foreach (var (callback, state) in left)
            {
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
        }
    }

    public static TheoryData<DictationWindowRenderScenario> Scenarios() => [.. DictationWindowRenderScenarios.All];

    /// <summary>Every scenario has a script, and it ends where the scenario
    /// says: a render never writes a picture of the wrong state under the
    /// right name.</summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Each_scenario_shows_the_state_it_is_named_for(DictationWindowRenderScenario scenario)
    {
        using var stage = new Stage(scenario);
        stage.Drive(scenario);

        var shown = stage.Shown;
        var section = stage.Section;
        switch (scenario)
        {
            case DictationWindowRenderScenario.FirstRun:
                Assert.False(stage.Controller.IsEnabled);
                Assert.True(shown.CanEnable);
                Assert.False(shown.PracticeVisible);
                Assert.Equal(DictationCopy.VocabularyEmpty, section.EmptyText);
                break;
            case DictationWindowRenderScenario.MicrophoneBlocked:
                Assert.True(shown.OffersMicrophoneSettings);
                Assert.Equal(DictationCopy.MicrophoneBlocked, shown.Status);
                Assert.Equal(TrayIcon.MicrophoneBlocked, shown.Tray.Icon);
                break;
            case DictationWindowRenderScenario.ShortcutRefused:
                Assert.False(stage.Controller.IsEnabled);
                Assert.Equal(DictationCopy.ShortcutRefused, shown.Status);
                break;
            case DictationWindowRenderScenario.Starting:
                Assert.Equal(DictationGlyph.Starting, shown.Header.Glyph);
                Assert.Equal(DictationCopy.HeadlineStarting, shown.Header.Headline);
                Assert.False(shown.Header.CanToggle);
                Assert.False(stage.Controller.IsEnabled);
                // Finished, the Enable the picture caught completes.
                stage.Finish();
                Assert.True(stage.Controller.IsEnabled);
                break;
            case DictationWindowRenderScenario.Transcribing:
                Assert.Equal(DictationGlyph.Transcribing, shown.Header.Glyph);
                Assert.True(shown.CanCancel);
                Assert.Null(shown.Recent);
                stage.Finish();
                Assert.Equal(DictationRenderPlatform.DefaultTranscript, stage.Shown.Recent?.Text);
                break;
            case DictationWindowRenderScenario.Typing:
                Assert.Equal(DictationGlyph.Typing, shown.Header.Glyph);
                Assert.False(shown.CanCancel);
                Assert.False(shown.CanPause);
                stage.Finish();
                Assert.Equal(DictationRenderPlatform.DefaultTranscript, stage.Shown.Recent?.Text);
                break;
            case DictationWindowRenderScenario.HoldKeyUnheard:
                Assert.Equal(DictationGlyph.HoldKeyUnheard, shown.Header.Glyph);
                Assert.Equal(DictationCopy.HoldKeyUnheard(stage.Controller.HoldKey), shown.Status);
                Assert.Equal(TrayIcon.MicrophoneBlocked, shown.Tray.Icon);
                break;
            case DictationWindowRenderScenario.HoldKeyReconnected:
                Assert.Equal(DictationGlyph.HoldKeyReconnected, shown.Header.Glyph);
                Assert.Equal(DictationCopy.HoldKeyReconnected(stage.Controller.HoldKey), shown.Status);
                Assert.False(shown.MicrophoneOpen);
                break;
            case DictationWindowRenderScenario.AdminWindow:
                Assert.Equal(DictationGlyph.AdminWindowInFront, shown.Header.Glyph);
                Assert.Equal(DictationCopy.InstructionsWithAdminWindow(stage.Controller.HoldKey), shown.Instructions);
                break;
            case DictationWindowRenderScenario.MicrophoneChanged:
                Assert.Equal(DictationWindowRenderScript.MicrophoneStoppedLine, shown.Status);
                Assert.StartsWith("The microphone stopped at 0:42, so the recording ended there.", shown.Status, StringComparison.Ordinal);
                Assert.EndsWith("The next hold uses Microphone Array (Intel® Smart Sound Te….", shown.Status, StringComparison.Ordinal);
                Assert.False(shown.MicrophoneOpen);
                // The words up to the change went in, and nothing is kept.
                Assert.NotNull(shown.Recent);
                Assert.Null(shown.SavedTake);
                break;
            case DictationWindowRenderScenario.PasswordFieldAtPaste:
                Assert.Equal(DictationCopy.SecureFieldAtPaste, shown.Status);
                Assert.Null(shown.Recent);
                Assert.Null(stage.Parts.Platform.Clipboard is IClipboardTextSnapshot clipboard && clipboard.TryRead(out var text) ? text : null);
                break;
            case DictationWindowRenderScenario.SignInRefused:
                Assert.False(shown.LaunchAtLogin);
                Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, shown.LaunchAtLoginNote);
                break;
            case DictationWindowRenderScenario.SignInSwitchedOff:
                Assert.False(shown.LaunchAtLogin);
                Assert.Equal(DictationCopy.LaunchAtLoginSwitchedOff, shown.LaunchAtLoginNote);
                break;
            case DictationWindowRenderScenario.Troubleshooting:
                Assert.True(stage.Controller.IsEnabled);
                Assert.Equal(DictationGlyph.Ready, shown.Header.Glyph);
                Assert.Equal([DictationWindowDisclosure.Troubleshooting], DictationWindowRenderScenarios.Opens(scenario));
                break;
            case DictationWindowRenderScenario.Ready:
                Assert.True(stage.Controller.IsEnabled);
                Assert.True(shown.CanPause);
                Assert.True(shown.PracticeVisible);
                Assert.True(shown.PracticeStartsExpanded);
                Assert.Null(shown.Recent);
                break;
            case DictationWindowRenderScenario.ReturningUser:
                Assert.True(stage.Controller.IsEnabled);
                Assert.True(shown.PracticeVisible);
                Assert.False(shown.PracticeStartsExpanded);
                break;
            case DictationWindowRenderScenario.Paused:
                Assert.False(stage.Controller.IsEnabled);
                Assert.True(shown.CanEnable);
                Assert.False(shown.CanPause);
                Assert.Equal(TrayIcon.Off, shown.Tray.Icon);
                break;
            case DictationWindowRenderScenario.Recording:
                Assert.True(shown.MicrophoneOpen);
                Assert.True(shown.CanCancel);
                Assert.False(shown.CanPause);
                Assert.Equal(TrayIcon.Listening, shown.Tray.Icon);
                break;
            case DictationWindowRenderScenario.Vocabulary:
                Assert.Equal(
                    ["Brivioo", "Kubernetes", "Saoirse"],
                    section.Rows.Where(r => r.Kind == VocabularyRowKind.Word).Select(r => r.Written).Order(StringComparer.Ordinal));
                Assert.Null(section.NotSavedText);
                break;
            case DictationWindowRenderScenario.VocabularyUnsaved:
                Assert.NotNull(section.NotSavedText);
                Assert.Null(section.ManagementNotice);
                break;
            case DictationWindowRenderScenario.VocabularySaved:
                Assert.Null(section.NotSavedText);
                Assert.NotNull(section.ManagementNotice);
                break;
            case DictationWindowRenderScenario.RecentResult:
                Assert.Equal(DictationRenderPlatform.DefaultTranscript, shown.Recent?.Text);
                Assert.Equal(DictationCopy.RecentTitle, shown.Recent?.Title);
                Assert.False(shown.Tray.Recent?.NeedsRecovery);
                Assert.Null(shown.Recent?.CopyMessage);
                Assert.Equal(DictationCopy.ShortTime(DictationRenderComposition.Time), shown.Recent?.Time);
                // The controller's own correction watch began over the render's
                // field, and its events reached the section through the relay,
                // as a user sees right after a dictation.
                Assert.Equal(DictationCopy.CorrectionChecking, section.CorrectionNotice);
                Assert.True(stage.Controller.Corrections?.IsWatching);
                break;
            case DictationWindowRenderScenario.Recovery:
                Assert.Equal(DictationRenderPlatform.DefaultTranscript, shown.Recent?.Text);
                Assert.True(shown.Tray.Recent?.NeedsRecovery);
                Assert.Equal(DictationCopy.RecentTitleRecovery, shown.Recent?.Title);
                Assert.Equal(DictationCopy.WordsStillHere, shown.Status);
                break;
            case DictationWindowRenderScenario.CopiedResult:
                Assert.Equal(DictationCopy.Copied, shown.Recent?.CopyMessage);
                break;
            case DictationWindowRenderScenario.SavedRecording:
                Assert.Equal(DictationCopy.TakeSaved, shown.Status);
                Assert.Equal(DictationCopy.SavedTakeTitle, shown.SavedTake?.Title);
                Assert.True(shown.SavedTake?.CanRecover);
                Assert.NotNull(shown.Tray.SavedTake);
                Assert.Null(shown.Recent);
                break;
            case DictationWindowRenderScenario.RecentText:
                Assert.Equal(DictationRenderPlatform.DefaultTranscript, shown.Recent?.Text);
                Assert.Equal([DictationWindowDisclosure.RecentText], DictationWindowRenderScenarios.Opens(scenario));
                break;
            case DictationWindowRenderScenario.Learned:
                Assert.Equal(DictationCopy.LearningRemembered, section.LearningReceipt);
                Assert.True(section.CanUndoLearning);
                Assert.Contains(section.Rows, r => r.Written == "Brivioo");
                break;
            case DictationWindowRenderScenario.LearningUndone:
                Assert.Equal(DictationCopy.LearningUndone, section.LearningReceipt);
                Assert.False(section.CanUndoLearning);
                Assert.DoesNotContain(section.Rows, r => r.Written == "Brivioo");
                break;
            case DictationWindowRenderScenario.LearningChecking:
                Assert.Equal(DictationCopy.CorrectionChecking, section.CorrectionNotice);
                break;
            case DictationWindowRenderScenario.LearningUnavailable:
                Assert.Equal(DictationCopy.CorrectionFieldUnread, section.CorrectionNotice);
                break;
            case DictationWindowRenderScenario.LearningPending:
                Assert.Equal(DictationCopy.CorrectionPending, section.CorrectionNotice);
                Assert.Null(section.LearningReceipt);
                break;
            case DictationWindowRenderScenario.LearningRejected:
                Assert.Equal(DictationCopy.CorrectionNothingLearned, section.CorrectionNotice);
                Assert.Null(section.LearningReceipt);
                break;
            case DictationWindowRenderScenario.LearnedRepo:
                Assert.Equal(DictationCopy.LearningRemembered, section.LearningReceipt);
                Assert.Contains(section.Rows, r => r.Kind == VocabularyRowKind.Word && r.Written == "repo");
                break;
            case DictationWindowRenderScenario.MissingResources:
                Assert.False(stage.Controller.IsEnabled);
                Assert.Equal(DictationCopy.ResourcesMissing, shown.Status);
                Assert.Equal(0, stage.Parts.Platform.WorkersStarted);
                break;
            default:
                Assert.Fail($"no expectation for {scenario}");
                break;
        }
    }

    /// <summary>A step that never shows its state fails the scenario with
    /// that state's name once the stage's deadline has passed, rather than
    /// let the render draw what the window shows instead. Here the hold key's
    /// hook will not register, so the press never opens the microphone.
    /// </summary>
    [Fact]
    public void A_scenario_that_never_reaches_its_state_fails_at_the_stages_deadline()
    {
        using var stage = new Stage(DictationWindowRenderScenario.Recording);
        stage.Parts.Platform.HookRegisters = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Two seconds a step: enabling over the render's in-process worker
        // takes milliseconds, so only the press can run out of time.
        var error = Assert.Throws<TimeoutException>(
            () => stage.Drive(DictationWindowRenderScenario.Recording, TimeSpan.FromSeconds(2)));

        Assert.Equal("the window never showed a listening hold", error.Message);
        Assert.False(stage.Shown.MicrophoneOpen);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}: the stage's deadline was not the one used");
        Assert.Equal(TimeSpan.FromSeconds(10), DictationWindowRenderScript.Deadline);
    }

    /// <summary>What each scenario shows once driven: one pass over every
    /// scenario, shared by the tests that compare them.</summary>
    private static readonly Lazy<IReadOnlyDictionary<DictationWindowRenderScenario, (DictationViewState Shown, VocabularyViewState Section)>> Driven =
        new(() =>
        {
            var driven = new Dictionary<DictationWindowRenderScenario, (DictationViewState, VocabularyViewState)>();
            foreach (var scenario in DictationWindowRenderScenarios.All)
            {
                using var stage = new Stage(scenario);
                stage.Drive(scenario);
                driven[scenario] = (stage.Shown, stage.Section);
            }

            return driven;
        });

    /// <summary>Every scenario ends in a state no other one shows: two
    /// scenarios whose window and section are the same would be one picture
    /// under two names.</summary>
    [Fact]
    public void No_two_scenarios_show_the_same_window()
    {
        var seen = new Dictionary<string, DictationWindowRenderScenario>();
        foreach (var (scenario, (shown, section)) in Driven.Value)
        {
            var key = string.Join(
                " | ",
                shown.Header,
                shown.Status,
                shown.Instructions,
                shown.CanEnable,
                shown.CanPause,
                shown.CanCancel,
                shown.MicrophoneOpen,
                shown.OffersMicrophoneSettings,
                shown.PracticeVisible,
                shown.PracticeStartsExpanded,
                shown.LaunchAtLogin,
                shown.LaunchAtLoginNote,
                shown.Recent?.Title,
                shown.Recent?.CopyMessage,
                string.Join(",", section.Rows.Select(r => r.Text)),
                section.CorrectionNotice,
                section.LearningReceipt,
                section.NotSavedText,
                section.ManagementNotice,
                string.Join(",", DictationWindowRenderScenarios.Opens(scenario).Order()));
            Assert.False(seen.TryGetValue(key, out var twin), $"{scenario} shows what {twin} shows");
            seen[key] = scenario;
        }
    }

    /// <summary>
    /// Every state the window's header can show (<see cref="DictationGlyph"/>,
    /// the same state the icon is drawn from) is the state some scenario ends
    /// in: one added later fails here until a scenario reaches it.
    /// </summary>
    [Fact]
    public void Every_header_state_is_drawn()
    {
        var drawn = Driven.Value.Values.Select(v => v.Shown.Header.Glyph).ToHashSet();
        foreach (var glyph in Enum.GetValues<DictationGlyph>())
        {
            Assert.True(drawn.Contains(glyph), $"no scenario draws the header's {glyph}");
        }

        // Each is drawn as the icon draws it, with its headline.
        Assert.All(Driven.Value.Values, v => Assert.Equal(DictationTrayGlyph.Headline(v.Shown.Header.Glyph), v.Shown.Header.Headline));
        Assert.All(Driven.Value.Values, v => Assert.Equal(DictationTrayGlyph.IconOf(v.Shown.Header.Glyph), v.Shown.Tray.Icon));
    }

    /// <summary>
    /// Every disclosure the user opens is drawn open by some scenario and
    /// closed by the rest, and Try dictation, which the controller opens, is
    /// drawn both ways too (<c>ready</c> and <c>returning-user</c>). Which
    /// sections are disclosures is read from <c>MainWindow.xaml</c>, so an
    /// <c>Expander</c> added there fails here until it is one.
    /// </summary>
    [Fact]
    public void Every_disclosure_is_drawn_open_and_closed()
    {
        var expanders = System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(DictationHudGeometryTests.AppFile("MainWindow.xaml")), "<Expander x:Name=\"(\\w+)\"")
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["PracticeSection", "RecentTextExpander", "TroubleshootingSection"], expanders);

        foreach (var disclosure in Enum.GetValues<DictationWindowDisclosure>())
        {
            var open = DictationWindowRenderScenarios.All.Where(s => DictationWindowRenderScenarios.Opens(s).Contains(disclosure)).ToArray();
            Assert.NotEmpty(open);
            Assert.True(open.Length < DictationWindowRenderScenarios.All.Count, $"{disclosure} is never drawn closed");
        }

        Assert.True(Driven.Value[DictationWindowRenderScenario.Ready].Shown.PracticeStartsExpanded);
        Assert.True(Driven.Value[DictationWindowRenderScenario.ReturningUser].Shown is { PracticeVisible: true, PracticeStartsExpanded: false });
    }

    /// <summary>The render never touches the user's files: every path its
    /// parts write is inside one scratch folder, which goes when they do.</summary>
    [Fact]
    public void A_render_writes_only_inside_its_scratch_folder_and_removes_it()
    {
        string root;
        using (var stage = new Stage(DictationWindowRenderScenario.RecentResult))
        {
            stage.Drive(DictationWindowRenderScenario.RecentResult);
            var parts = stage.Parts;
            root = parts.Root;
            Assert.StartsWith(Path.GetTempPath(), root, StringComparison.Ordinal);
            foreach (var path in new[] { parts.Settings.Path, parts.LearningDirectory, parts.AppDirectory, parts.Options.LogDirectory })
            {
                Assert.StartsWith(root + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
            }

            var user = DictationPaths.ForCurrentUser();
            Assert.NotEqual(user.SettingsFile, parts.Settings.Path);
            Assert.DoesNotContain(user.LogDirectory, parts.Options.LogDirectory, StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(root), "the scratch folder outlived the render");
    }

    /// <summary>The words go to the render's own clipboard and field, never
    /// Windows'; a refused paste and clipboard keep them only in the app.</summary>
    [Fact]
    public void The_render_platform_keeps_the_words_to_itself()
    {
        using (var stage = new Stage(DictationWindowRenderScenario.RecentResult))
        {
            stage.Drive(DictationWindowRenderScenario.RecentResult);
            Assert.Equal(1, stage.Parts.Platform.WorkersStarted);
            Assert.False(stage.Parts.Platform.Microphone?.Open);
        }

        using (var stage = new Stage(DictationWindowRenderScenario.Recovery))
        {
            stage.Drive(DictationWindowRenderScenario.Recovery);
            Assert.False(stage.Parts.Platform.Clipboard.TrySet("anything"));
        }
    }

    /// <summary>The scenario list is the Mac's, adapted, with names the Mac
    /// uses where the scenario is the same.</summary>
    [Fact]
    public void The_scenarios_are_named_as_on_the_Mac()
    {
        var names = DictationWindowRenderScenarios.All.Select(DictationWindowRenderScenarios.Name).ToArray();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches("^[a-z]+(-[a-z]+)*$", name));
        foreach (var mac in new[]
        {
            "first-run", "ready", "returning-user", "paused", "recording", "vocabulary", "vocabulary-unsaved",
            "vocabulary-saved", "recent-result", "recovery", "copied-result", "learned", "learning-undone",
            "learning-checking", "learning-unavailable", "learning-pending", "learning-rejected", "learned-repo",
            "missing-resources", "troubleshooting",
        })
        {
            Assert.Contains(mac, names);
        }

        // The Mac's 20 adapted, Windows' own, and LIMIT-WIN-A's saved-recording.
        Assert.Contains("saved-recording", names);
        Assert.Equal(34, names.Length);
    }
}
