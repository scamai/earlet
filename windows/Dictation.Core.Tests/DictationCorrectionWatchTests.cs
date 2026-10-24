using System.Text.Json;
using Dictation.Core.Learning;
using Dictation.Services;

namespace Dictation.Core.Tests;

/// <summary>
/// Learning from the user's fixes, end to end through the controller: a
/// delivered dictation is watched through the real span observer over a fake
/// field, a fix the user typed lands in <c>learning.json</c> through the real
/// policy, and everything that must stop a watch — the switch, a refused or
/// cancelled dictation, the next press, pause, close, a focus that moved —
/// leaves the file alone. Nothing around the span, and no word, reaches a
/// file or the log.
/// </summary>
public sealed class DictationCorrectionWatchTests
{
    private const string Heard = "trovio";
    private const string Meant = "Trovioo";

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    /// <summary>A rig that learns, whose worker hears "trovio" and whose
    /// user fixes it two seconds after each paste.</summary>
    private static Rig LearningRig(Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
    {
        var rig = new Rig(logs: logs, learning: true);
        rig.Platform.Worker.Transcript = "we use trovio for the checks";
        rig.Platform.Field.FixAfterEachPaste(Heard, Meant);
        return rig;
    }

    private static LearningState OnDisk(Rig rig)
    {
        var load = new LearningStore(rig.LearningDirectory).Load();
        Assert.Equal(LearningFileStatus.Loaded, load.Status);
        return load.State;
    }

    /// <summary>One more dictation, drained until its insertion is
    /// reported — which is also when its watch, if any, has begun.</summary>
    private static void DictateOnce(Rig rig)
    {
        var inserted = rig.View.States.Count(Inserted);
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.States.Count(Inserted) > inserted);
    }

    private static void DictateAndWatch(Rig rig)
    {
        DictateOnce(rig);
        rig.WaitForWatch();
    }

    /// <summary>Dictate, and stop the watch's clock between the fix and the
    /// moment it would settle: the watch is running with an edit in hand.</summary>
    private static void DictateAndPark(Rig rig)
    {
        rig.Time.Limit = rig.Time.NowMs + 3_500;
        DictateOnce(rig);
        rig.Time.WaitUntilParked();
        Assert.True(rig.Controller.Corrections!.IsWatching);
    }

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

    // ─── Learning ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_typed_fix_teaches_the_word_and_the_same_fix_on_a_second_dictation_the_alias()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        Assert.True(rig.View.Last.LearnFromCorrections);

        DictateAndWatch(rig);

        Assert.Equal(1, rig.Platform.SpanTargets);
        var first = OnDisk(rig);
        var word = Assert.Single(first.Words);
        Assert.Equal((Meant, LearningOrigins.Correction), (word.Text, word.Origin));
        Assert.Empty(first.Aliases);
        Assert.Equal((Heard, Meant), (Assert.Single(first.Pending).Heard, first.Pending[0].Meant));

        DictateAndWatch(rig);

        var second = OnDisk(rig);
        var alias = Assert.Single(second.Aliases);
        Assert.Equal((Heard, Meant, LearningOrigins.Correction), (alias.Heard, alias.Meant, alias.Origin));
        Assert.Equal(2L, Assert.Single(second.Words).Count);
    }

    [Theory]
    [InlineData(80_000, true)]
    [InlineData(91_000, false)]
    public async Task A_fix_counts_for_ninety_seconds_after_the_paste(long afterMs, bool counts)
    {
        using var rig = LearningRig();
        rig.Platform.Field.FixAfterEachPaste(Heard, Meant, afterMs);
        await rig.EnableAsync();

        DictateAndWatch(rig);

        Assert.Equal(counts, File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task A_change_nobody_typed_is_not_learned()
    {
        using var rig = LearningRig();
        rig.Platform.Field.FixAfterEachPaste(Heard, Meant, typed: false);
        await rig.EnableAsync();

        DictateAndWatch(rig);

        Assert.Equal(1, rig.Platform.SpanTargets);
        Assert.Contains(Meant, rig.Platform.Field.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task A_focus_that_moved_to_another_application_is_not_read()
    {
        using var rig = LearningRig();
        rig.Platform.Field.Identity = rig.Platform.Field.Identity with { ProcessId = 4_243 };
        await rig.EnableAsync();

        DictateAndWatch(rig);

        Assert.Equal(1, rig.Platform.SpanTargets);
        Assert.Equal(0, rig.Platform.Field.Reads);
        Assert.False(File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task The_worker_is_sent_no_vocabulary_by_the_host_after_learning()
    {
        // The worker reads learning.json itself; the host is not a second
        // merge point.
        using var rig = LearningRig();
        await rig.EnableAsync();
        DictateAndWatch(rig);
        DictateAndWatch(rig);
        Assert.NotEmpty(OnDisk(rig).Aliases);

        DictateAndWatch(rig);

        var transcribes = rig.Platform.Worker.Frames
            .Select(f => JsonDocument.Parse(f).RootElement)
            .Where(f => f.GetProperty("op").GetString() == "transcribe")
            .ToList();
        Assert.Equal(3, transcribes.Count);
        foreach (var frame in transcribes)
        {
            Assert.True(
                !frame.TryGetProperty("bias_phrases", out var phrases)
                || phrases.ValueKind == JsonValueKind.Null
                || phrases.GetArrayLength() == 0,
                frame.ToString());
            Assert.False(frame.TryGetProperty("phrase_deltas", out var deltas) && deltas.ValueKind != JsonValueKind.Null);
        }
    }

    // ─── What is never watched ──────────────────────────────────────────

    [Fact]
    public async Task With_the_switch_off_nothing_is_watched_or_written_and_the_choice_is_kept()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();

        rig.Controller.SetLearnFromCorrections(false);

        Assert.False(rig.View.Last.LearnFromCorrections);
        Assert.False(rig.Settings.Load().LearnFromCorrections);
        DictateAndWatch(rig);
        Assert.Equal(0, rig.Platform.SpanTargets);
        Assert.False(File.Exists(rig.LearningFile));

        rig.Controller.SetLearnFromCorrections(true);

        Assert.True(rig.View.Last.LearnFromCorrections);
        DictateAndWatch(rig);
        Assert.Equal(Meant, Assert.Single(OnDisk(rig).Words).Text);
    }

    public static TheoryData<string> Undelivered => new() { "refused", "no window", "password field", "cancelled" };

    [Theory]
    [MemberData(nameof(Undelivered))]
    public async Task A_dictation_that_delivered_nothing_is_never_watched(string how)
    {
        using var rig = LearningRig();
        await rig.EnableAsync();

        switch (how)
        {
            case "refused":
                // The clipboard fallback: the words are on the clipboard, not in a field.
                rig.Platform.Paste.Succeeds = false;
                rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboard);
                break;
            case "no window":
                rig.Platform.Target.Next = FocusedApplicationResult.NoFocusedWindow;
                rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboardNoWindow);
                break;
            case "password field":
                rig.Press();
                rig.Platform.SecureField.Next = SecureFieldState.Secure;
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtRelease);
                break;
            default:
                rig.Press();
                rig.Controller.Cancel();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
                break;
        }

        rig.WaitForWatch();
        Assert.Equal(0, rig.Platform.SpanTargets);
        Assert.Null(rig.Platform.Trigger.KeyboardInputObserved);
        Assert.False(File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task A_dictation_whose_result_arrives_after_the_next_press_is_not_watched()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        var posted = rig.Dispatcher.Posted;
        rig.Press();
        rig.Release();

        // Listening, transcribing, inserting, armed and the result: the
        // dictation is over, and the UI thread has not heard about it yet.
        Assert.True(SpinWait.SpinUntil(() => rig.Dispatcher.Posted >= posted + 5, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, rig.Platform.Paste.Count);

        // The next hold is seen first.
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.States.Any(Inserted));
        rig.WaitForWatch();

        Assert.Equal(0, rig.Platform.SpanTargets);
        Assert.False(File.Exists(rig.LearningFile));
    }

    // ─── What ends a watch without learning ─────────────────────────────

    [Fact]
    public async Task Pausing_mid_watch_learns_nothing_and_the_log_says_why()
    {
        var (logs, log) = FileLog();
        using (logs)
        {
            using var rig = LearningRig(logs);
            await rig.EnableAsync();
            DictateAndPark(rig);
            var reads = rig.Platform.Field.Reads;

            rig.Controller.Pause();
            rig.Time.Release();
            rig.WaitForWatch();

            // Stopped where it was: not left to run out its ninety seconds.
            Assert.Equal(reads, rig.Platform.Field.Reads);
            Assert.False(File.Exists(rig.LearningFile));
            Assert.Contains("Correction watch ended: dictation was paused", ReadLog(log), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_or_quitting_mid_watch_learns_nothing(bool quit)
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        DictateAndPark(rig);

        var decision = rig.Controller.RequestClose(quit);
        rig.Time.Release();
        rig.WaitForWatch();

        Assert.Equal(quit ? DictationCloseAction.Close : DictationCloseAction.Hide, decision.Action);
        Assert.False(File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task Quitting_the_app_mid_watch_learns_nothing()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        DictateAndPark(rig);

        rig.Controller.Dispose();
        rig.Time.Release();
        rig.WaitForWatch();

        Assert.False(File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task An_end_that_lands_as_the_fix_settles_wins()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        // The fix is seen two seconds after the paste and settles three
        // seconds later. End the watch on the very read of the clock that
        // settles it.
        rig.Time.When(rig.Time.NowMs + 5_000, () => rig.Controller.Corrections!.End(CorrectionWatchEnd.Paused));

        DictateAndWatch(rig);

        Assert.Contains(Meant, rig.Platform.Field.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task A_watch_ended_while_the_field_is_resolved_reads_nothing_and_logs_the_first_reason()
    {
        var (logs, log) = FileLog();
        using (logs)
        {
            using var rig = LearningRig(logs);
            rig.Platform.OnResolve = () =>
            {
                rig.Controller.Corrections!.End(CorrectionWatchEnd.Paused);
                rig.Controller.Corrections!.End(CorrectionWatchEnd.Closed);
            };
            await rig.EnableAsync();

            DictateAndWatch(rig);

            Assert.Equal(1, rig.Platform.SpanTargets);
            Assert.Equal(0, rig.Platform.Field.Reads);
            Assert.False(File.Exists(rig.LearningFile));
            Assert.Contains("Correction watch ended: dictation was paused", ReadLog(log), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Switching_off_mid_watch_learns_nothing()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        DictateAndPark(rig);

        rig.Controller.SetLearnFromCorrections(false);
        rig.Time.Release();
        rig.WaitForWatch();

        Assert.False(File.Exists(rig.LearningFile));
    }

    [Fact]
    public async Task The_next_dictation_ends_the_watch_when_it_is_released_before_its_words_land()
    {
        var (logs, log) = FileLog();
        using (logs)
        {
            using var rig = LearningRig(logs);
            using var paste = new ManualResetEventSlim();
            await rig.EnableAsync();
            DictateAndPark(rig);
            var watch = rig.Controller.Corrections!.Completion;

            // A press alone ends nothing: it may yet be a shortcut.
            rig.Press();
            rig.Dispatcher.Drain();
            Assert.True(rig.Controller.Corrections.IsWatching);

            // Released to transcription, with its paste held and nothing
            // drained: the watch ends before the new words can reach it.
            rig.Platform.Paste.Hold = paste;
            rig.ReleaseToHeldPaste();
            Assert.True(SpinWait.SpinUntil(() => rig.Platform.Paste.Count == 2, TimeSpan.FromSeconds(10)));
            Assert.True(watch.Wait(TimeSpan.FromSeconds(10)), "the release did not end the watch");

            Assert.False(File.Exists(rig.LearningFile));
            Assert.Contains(
                "Correction watch ended: the next dictation began", ReadLog(log), StringComparison.Ordinal);
            var inserted = rig.View.States.Count(Inserted);
            paste.Set();
            rig.DrainUntil(() => rig.View.States.Count(Inserted) > inserted);
        }
    }

    public static TheoryData<string> HoldsThatTranscribeNothing =>
        new() { "a shortcut", "escape", "a password field at the press", "nothing recorded" };

    [Theory]
    [MemberData(nameof(HoldsThatTranscribeNothing))]
    public async Task A_hold_that_transcribes_nothing_leaves_the_watch_running_and_its_fix_is_learned(string how)
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        DictateAndPark(rig);

        switch (how)
        {
            case "a shortcut":
                // Right Ctrl held and another key: the user editing the text
                // being watched, not a dictation.
                rig.Press();
                rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
                rig.Controller.Tick();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.CancelledByShortcut(rig.Controller.HoldKey));
                break;
            case "escape":
                rig.Press();
                rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
                rig.Controller.Tick();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
                break;
            case "a password field at the press":
                rig.Platform.SecureField.Next = SecureFieldState.Secure;
                rig.Press();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtPress);
                rig.Platform.SecureField.Next = SecureFieldState.NotSecure;
                break;
            default:
                // Pressed and let go with no sound from the microphone.
                rig.Platform.Trigger.Down = true;
                rig.Controller.Tick();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status.StartsWith("The microphone opened but sent no sound", StringComparison.Ordinal));
                break;
        }

        Assert.True(rig.Controller.Corrections!.IsWatching);
        rig.Time.Release();
        rig.WaitForWatch();

        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Equal(Meant, Assert.Single(OnDisk(rig).Words).Text);
    }

    [Fact]
    public async Task The_hook_reports_key_downs_only_while_a_watch_runs()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        rig.Controller.Tick();
        Assert.Null(rig.Platform.Trigger.KeyboardInputObserved);

        DictateAndPark(rig);
        rig.Controller.Tick();
        var feed = rig.Platform.Trigger.KeyboardInputObserved;
        Assert.NotNull(feed);
        feed(rig.Time.NowMs, 42);
        Assert.Equal(42, rig.Controller.Corrections!.Keyboard.Last!.Value.ProcessId);

        rig.Time.Release();
        rig.WaitForWatch();
        rig.Controller.Tick();
        Assert.Null(rig.Platform.Trigger.KeyboardInputObserved);
    }

    /// <summary>
    /// The race the feed once lost: Begin starts the watch on the thread pool
    /// at once, and the feed was attached only after Begin returned. A fix
    /// typed in that gap was judged untyped and nothing was learned. The feed
    /// is read when Begin raises Started, on the caller's thread just before
    /// the watch starts, so a feed attached late fails every run instead of
    /// only when the pool wins.
    /// </summary>
    [Fact]
    public async Task The_hooks_feed_is_live_before_the_watch_can_read_a_key_down()
    {
        using var rig = LearningRig();
        var watch = rig.Controller.Corrections!;
        var feeds = new List<Action<long, int>?>();
        watch.Started += (_, _) => feeds.Add(rig.Platform.Trigger.KeyboardInputObserved);
        await rig.EnableAsync();

        DictateOnce(rig);

        var feed = Assert.Single(feeds);
        Assert.Equal((Action<long, int>)watch.Keyboard.Record, feed);
        rig.WaitForWatch();
        Assert.Equal(Meant, Assert.Single(OnDisk(rig).Words).Text);
    }

    [Fact]
    public void A_new_watch_ends_the_one_still_running()
    {
        var time = new ObserverTime();
        var store = new LearningStore(Learning.LearningGoldenFileTests.TempDirectory());
        var learning = new DictationLearning(store, new WordListLexicon(Learning.LearningGoldenTests.SharedLexicon));
        CorrectionWatch? watch = null;
        var field = new FakeField(time, () => watch!.Keyboard.Record);
        watch = new CorrectionWatch(
            learning,
            (keyboard, _) =>
            {
                field.Keyboard = keyboard;
                return field;
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            time: time,
            delay: time.Delay);
        var notepad = FocusedApplicationResult.Resolved("notepad", 42, "Untitled - Notepad");
        field.FixAfterEachPaste(Heard, Meant);
        field.Paste("we use trovio");
        time.Limit = time.NowMs + 3_500;
        watch.Begin("we use trovio", notepad);
        var first = watch.Completion;
        time.WaitUntilParked();

        // Text that is not in the field: the new watch ends at once, on its own.
        watch.Begin("something else entirely", notepad);
        time.Release();

        Assert.True(first.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(watch.Completion.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(File.Exists(store.FilePath));
    }

    // ─── The last save at quit ──────────────────────────────────────────

    [Fact]
    public void Quitting_tries_once_more_to_save_what_the_disk_refused()
    {
        using var rig = new Rig(learning: true, beforeStart: r =>
        {
            Directory.CreateDirectory(r.DataDirectory);
            File.WriteAllText(r.LearningDirectory, "a file where the folder should be");
        });
        Assert.Equal(TeachResult.Ok, rig.Learning!.Teach(Meant));
        Assert.True(rig.Learning.Snapshot().Unsaved);
        File.Delete(rig.LearningDirectory);

        rig.Controller.Dispose();

        Assert.False(rig.Learning.Snapshot().Unsaved);
        Assert.Equal(Meant, Assert.Single(OnDisk(rig).Words).Text);
    }

    [Learning.UnreadableFileFact]
    public void A_last_save_that_throws_does_not_stop_the_app_closing()
    {
        using var rig = new Rig(learning: true, beforeStart: r =>
        {
            var seed = new LearningStore(r.LearningDirectory);
            seed.Load();
            var state = new LearningState();
            state.Words.Add(new LearnedWord { Text = "Kubectl", Origin = LearningOrigins.Taught, LastSeen = 1 });
            Assert.True(seed.Save(state));
        });
        using (Learning.UnreadableFileFactAttribute.Lock(rig.LearningFile))
        {
            Assert.Equal(TeachResult.Ok, rig.Learning!.Teach(Meant));
        }

        // The flush reads the file again, merges and saves, then tells a
        // listener that throws.
        rig.Learning.Changed += (_, _) => throw new InvalidOperationException("a listener with a bug");

        rig.Controller.Dispose();

        Assert.Equal(["Kubectl", Meant], OnDisk(rig).Words.Select(w => w.Text).Order(StringComparer.Ordinal));
    }

    // ─── Privacy ────────────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_but_the_corrected_words_reaches_a_file_and_no_words_reach_the_log()
    {
        var (logs, log) = FileLog();
        using (logs)
        {
            using var rig = LearningRig(logs);
            await rig.EnableAsync();
            DictateAndWatch(rig);
            DictateAndWatch(rig);
            Assert.NotEmpty(OnDisk(rig).Aliases);

            var logText = ReadLog(log);
            Assert.Contains("Correction watch ended: settled on a typed edit", logText, StringComparison.Ordinal);
            Assert.Contains("Correction observed: 1 accepted, 0 rejected, 1 new words", logText, StringComparison.Ordinal);
            foreach (var secret in new[]
                     {
                         FakeField.SentinelBefore, FakeField.SentinelAfter, "Tuesday", "invoice", Heard, "checks",
                     })
            {
                Assert.DoesNotContain(secret, logText, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var file in Directory.EnumerateFiles(rig.Copy.Root, "*", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                Assert.DoesNotContain(FakeField.SentinelBefore, text, StringComparison.Ordinal);
                Assert.DoesNotContain(FakeField.SentinelAfter, text, StringComparison.Ordinal);
                Assert.DoesNotContain("Tuesday", text, StringComparison.Ordinal);
                Assert.DoesNotContain("checks", text, StringComparison.Ordinal);
            }

            var learned = File.ReadAllText(rig.LearningFile);
            Assert.DoesNotContain("we use", learned, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_word_removed_from_the_list_is_not_learned_again()
    {
        using var rig = LearningRig();
        await rig.EnableAsync();
        DictateAndWatch(rig);
        DictateAndWatch(rig);
        var view = new RecordingVocabularyView();
        using var vocabulary = new VocabularyModel(rig.Learning!, view, rig.Dispatcher.Post);
        vocabulary.Refresh();

        vocabulary.Remove(view.Last.Rows.Single(r => r.Kind == VocabularyRowKind.Word));
        DictateAndWatch(rig);
        DictateAndWatch(rig);

        var state = OnDisk(rig);
        Assert.Empty(state.Words);
        Assert.Empty(state.Aliases);
        Assert.Equal(Meant.ToLowerInvariant(), Assert.Single(state.Removed.Words).Key);
    }
}
