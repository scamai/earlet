using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

/// <summary>
/// The Vocabulary section's save outcomes (docs/design-macos.md, Correction notices, save
/// receipts and Save again): the macOS <c>VocabularyRecoveryTests</c> scenarios
/// one for one under the same names, the save halves of its
/// <c>VocabularyTests</c> and <c>LearningFeedbackTests</c>, then what Windows
/// adds — the file work off the UI thread and the fences on its responses —
/// and the window's wiring, read from source.
/// </summary>
/// <remarks>
/// A blocked store is a file where its folder should be, with the folder moved
/// aside, so a test can still read what the file held. The Mac scenarios' undo
/// halves belong to the learning receipt, in <see cref="LearningReceiptTests"/>,
/// which also holds what the receipt and these outcomes do together.
/// </remarks>
public sealed class VocabularySaveTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private sealed class Section : IDisposable
    {
        public Section(bool gated = false, Func<Action, Task>? work = null)
        {
            Store = new LearningStore(LearningGoldenFileTests.TempDirectory());
            Learning = new DictationLearning(
                Store, new WordListLexicon(LearningGoldenTests.SharedLexicon), log: Log.For<DictationLearning>());
            Model = new VocabularyModel(
                Learning, View, Dispatcher.Post, Watch, work ?? (gated ? Work.Run : null), Log.For<VocabularyModel>());
            Model.Refresh();
        }

        public LearningStore Store { get; }
        public DictationLearning Learning { get; }
        public FakeCorrectionWatch Watch { get; } = new();
        public RecordingVocabularyView View { get; } = new();
        public DeferredDispatcher Dispatcher { get; } = new();
        public GatedWork Work { get; } = new();
        public LogLines Log { get; } = new();
        public VocabularyModel Model { get; }

        public VocabularyViewState Last => View.Last;

        private string Aside => Store.Directory + ".aside";

        /// <summary>A gated change: run everything queued, then hear it.</summary>
        public void Settle()
        {
            Work.RunAll();
            Dispatcher.Drain();
        }

        /// <summary>Every save is refused until <see cref="Unblock"/>; the
        /// folder is kept aside, file and all.</summary>
        public void Block()
        {
            if (Directory.Exists(Store.Directory))
            {
                Directory.Move(Store.Directory, Aside);
            }

            File.WriteAllText(Store.Directory, "a file where the folder should be");
        }

        public void Unblock()
        {
            File.Delete(Store.Directory);
            if (Directory.Exists(Aside))
            {
                Directory.Move(Aside, Store.Directory);
            }
        }

        /// <summary>What the file holds; while blocked, what it held.</summary>
        public LearningState OnDisk() =>
            new LearningStore(Directory.Exists(Aside) ? Aside : Store.Directory).Load().State;

        public Guid Begin()
        {
            var watch = Guid.NewGuid();
            Watch.Start(watch);
            Dispatcher.Drain();
            return watch;
        }

        /// <summary>What a watch does with a settled revision: learn on its
        /// thread, then tell the section.</summary>
        public LearningObservation Observe(Guid watch, string dictated, string edited)
        {
            var outcome = Learning.Observe(watch.ToString("N"), dictated, edited, T);
            Watch.Observe(new CorrectionWatchObserved(watch, 1, dictated != edited, outcome));
            Dispatcher.Drain();
            return outcome;
        }

        public void Dispose()
        {
            Work.RunAll();
            Model.Dispose();
        }
    }

    /// <summary>A work runner that holds every item until the test runs it,
    /// as a slow disk would.</summary>
    internal sealed class GatedWork
    {
        private readonly Queue<(Action Work, TaskCompletionSource Done)> _queue = new();

        public int Pending => _queue.Count;

        public Task Run(Action work)
        {
            var done = new TaskCompletionSource();
            _queue.Enqueue((work, done));
            return done.Task;
        }

        /// <summary>Run the oldest item now, on this thread.</summary>
        public void RunNext()
        {
            var (work, done) = _queue.Dequeue();
            try
            {
                work();
                done.SetResult();
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        }

        public void RunAll()
        {
            while (_queue.Count > 0)
            {
                RunNext();
            }
        }
    }

    internal sealed class LogLines
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger<T> For<T>() => new Logger<T>(_lines);

        private sealed class Logger<T>(ConcurrentQueue<string> lines) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }

    private sealed class FakeTeachRow(string written, string soundsLike) : IVocabularyTeachRow
    {
        public string Written { get; set; } = written;

        public string SoundsLike { get; set; } = soundsLike;

        public int Cleared { get; private set; }

        public void Clear()
        {
            Written = "";
            SoundsLike = "";
            Cleared++;
        }
    }

    // ─── The Mac's VocabularyRecoveryTests ──────────────────────────────

    [Fact]
    public async Task A_successful_removal_gives_a_generic_persisted_receipt()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());

        s.Model.Remove(s.Last.Rows.First());

        Assert.Equal("Removed from your vocabulary.", s.Last.ManagementNotice);
        Assert.Null(s.Last.NotSavedText);
        Assert.DoesNotContain(s.Log.Lines, l => l.Contains("SkyDock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_failed_removal_remains_visibly_recoverable_and_retry_saves_it()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Block();

        s.Model.Remove(s.Last.Rows.First());

        Assert.Equal(DictationCopy.VocabularyChangeNotSaved, s.Last.NotSavedText);
        Assert.Contains("isn't saved", s.Last.NotSavedText, StringComparison.Ordinal);
        Assert.Null(s.Last.ManagementNotice);

        s.Model.SaveAgain();

        Assert.False(s.Last.SavingAgain);
        Assert.Equal("Your vocabulary still isn't saved. Try again when the file is available.", s.Last.NotSavedText);

        s.Unblock();
        s.Model.SaveAgain();

        Assert.Equal("Changes saved to your vocabulary on this PC.", s.Last.ManagementNotice);
        Assert.Null(s.Last.NotSavedText);
        Assert.Empty(s.OnDisk().Words);
        Assert.Equal(["skydock"], s.OnDisk().Removed.Words.Select(w => w.Key));
    }

    [Fact]
    public async Task A_failed_forget_all_remains_visibly_recoverable_and_retry_clears_the_file()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Block();

        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();

        Assert.Equal(DictationCopy.VocabularyChangeNotSaved, s.Last.NotSavedText);
        Assert.Null(s.Last.TeachMessage);

        s.Model.SaveAgain();

        Assert.False(s.Last.SavingAgain);
        Assert.Equal(DictationCopy.SaveAgainFailed, s.Last.NotSavedText);

        s.Unblock();
        s.Model.SaveAgain();

        Assert.Equal(DictationCopy.SaveAgainSucceeded, s.Last.ManagementNotice);
        Assert.Null(s.Last.NotSavedText);
        Assert.Equal(LearningStore.Serialize(new LearningState()), LearningStore.Serialize(s.OnDisk()));
    }

    [Fact]
    public async Task Forget_all_success_has_no_word_content_in_its_receipt_or_log()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());

        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();

        Assert.Equal("Your vocabulary is empty.", s.Last.ManagementNotice);
        foreach (var line in s.Log.Lines.Append(s.Last.ManagementNotice!))
        {
            Assert.DoesNotContain("SkyDock", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The Mac's "…while preserving undo": the undo half is the
    /// learning receipt's.</summary>
    [Fact]
    public async Task A_newer_observation_expires_a_management_receipt()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();
        Assert.NotNull(s.Last.ManagementNotice);

        var outcome = s.Observe(s.Begin(), "We use trovio", "We use Trovioo");

        Assert.Equal(1, outcome.NewWords);
        Assert.Null(s.Last.ManagementNotice);
    }

    [Fact]
    public async Task An_unchanged_observation_preserves_the_last_management_receipt()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();
        var receipt = s.Last.ManagementNotice;
        Assert.NotNull(receipt);

        var outcome = s.Observe(s.Begin(), "same", "same");

        Assert.Equal((0, 0, LearningWrite.Unchanged), (outcome.NewWords, outcome.NewAliases, outcome.Write));
        Assert.Equal(receipt, s.Last.ManagementNotice);
        Assert.Equal(DictationCopy.CorrectionNotDetected, s.Last.CorrectionNotice);
    }

    // ─── The save halves of VocabularyTests and LearningFeedbackTests ───

    /// <summary>The Mac's "Forget all that could not be saved says so, and
    /// the last save at quit makes it stick", through the controller's quit.</summary>
    [Fact]
    public async Task Forget_all_that_could_not_be_saved_says_so_and_the_last_save_at_quit_makes_it_stick()
    {
        using var rig = new Rig(learning: true);
        var view = new RecordingVocabularyView();
        using var vocabulary = new VocabularyModel(rig.Learning!, view, rig.Dispatcher.Post);
        vocabulary.Refresh();
        Assert.True(await vocabulary.Teach("Trovioo", "trovio").Soon());
        Assert.Null(view.Last.NotSavedText);
        var aside = rig.LearningDirectory + ".aside";
        Directory.Move(rig.LearningDirectory, aside);
        File.WriteAllText(rig.LearningDirectory, "a file where the folder should be");

        vocabulary.AskForgetAll();
        vocabulary.ConfirmForgetAll();

        Assert.Empty(view.Last.Rows);
        Assert.Equal(DictationCopy.VocabularyChangeNotSaved, view.Last.NotSavedText);
        Assert.Equal(["Trovioo"], new LearningStore(aside).Load().State.Words.Select(w => w.Text));

        File.Delete(rig.LearningDirectory);
        Directory.Move(aside, rig.LearningDirectory);
        rig.Controller.Dispose();

        var relaunched = new LearningStore(rig.LearningDirectory).Load().State;
        Assert.Empty(relaunched.Words);
        Assert.Empty(relaunched.Aliases);
        rig.Dispatcher.Drain();
        Assert.Null(view.Last.NotSavedText);
    }

    [Fact]
    public async Task Teaching_removing_forgetting_and_saving_again_log_outcomes_never_words()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", "sky dock").Soon());
        Assert.True(await s.Model.Teach("Kubectl", null).Soon());
        Assert.False(await s.Model.Teach("Fable Notes", "notes").Soon());
        s.Block();
        s.Model.Remove(s.Last.Rows.Single(r => r.Text == "SkyDock"));
        s.Model.SaveAgain();
        s.Unblock();
        s.Model.SaveAgain();
        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();

        var lines = s.Log.Lines;
        Assert.Contains(lines, l => l.StartsWith("Teach: Ok", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Teach: SelfContaining", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Removed a learned word; file NotSaved", StringComparison.Ordinal));
        Assert.Contains("Save again: file NotSaved", lines);
        Assert.Contains("Save again: file Saved", lines);
        Assert.Contains(lines, l => l.StartsWith("Cleared everything learned; file Saved", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            foreach (var word in new[] { "SkyDock", "sky dock", "Fable Notes" })
            {
                Assert.DoesNotContain(word, line, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>The Mac's "failed saves do not produce success feedback":
    /// a correction whose save failed takes an earlier receipt away and
    /// leaves only the unsaved warning.</summary>
    [Fact]
    public async Task Failed_saves_do_not_produce_success_feedback()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        Assert.True(await s.Model.Teach("Kubectl", null).Soon());
        s.Model.Remove(s.Last.Rows.Single(r => r.Text == "Kubectl"));
        Assert.Equal(DictationCopy.VocabularyRemoved, s.Last.ManagementNotice);
        s.Block();

        var outcome = s.Observe(s.Begin(), "we use trovio", "we use Trovioo");

        Assert.Equal(LearningWrite.NotSaved, outcome.Write);
        Assert.Null(s.Last.ManagementNotice);
        Assert.Null(s.Last.CorrectionNotice);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.Last.NotSavedText);
        Assert.Equal(["SkyDock"], s.OnDisk().Words.Select(w => w.Text));
    }

    // ─── Only a clean save says so ──────────────────────────────────────

    [Fact]
    public async Task An_add_that_was_not_saved_shows_no_line_of_its_own()
    {
        using var s = new Section();
        s.Block();

        Assert.True(await s.Model.Teach("Trovioo", null).Soon());

        Assert.Equal(["Trovioo"], s.Last.Rows.Select(r => r.Text));
        Assert.Null(s.Last.TeachMessage);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.Last.NotSavedText);
    }

    /// <summary>Its own write landed, but by the time it is answered a
    /// correction on another thread has left something unsaved: not "Removed".</summary>
    [Fact]
    public async Task A_change_whose_write_landed_while_something_else_went_unsaved_is_not_called_saved()
    {
        using var s = new Section(gated: true);
        var teach = s.Model.Teach("SkyDock", null);
        s.Settle();
        Assert.True(await teach.Soon());
        s.Model.Remove(s.Last.Rows.Single());
        s.Work.RunNext();

        s.Block();
        Assert.Equal(LearningWrite.NotSaved, s.Learning.Observe("other", "we use trovio", "we use Trovioo", T).Write);
        s.Dispatcher.Drain();

        Assert.Null(s.Last.ManagementNotice);
        Assert.Equal(DictationCopy.VocabularyChangeNotSaved, s.Last.NotSavedText);
    }

    [Fact]
    public async Task A_save_again_that_landed_while_something_else_went_unsaved_is_not_called_saved()
    {
        using var s = new Section(gated: true);
        s.Block();
        var teach = s.Model.Teach("SkyDock", null);
        s.Settle();
        Assert.True(await teach.Soon());
        s.Unblock();
        s.Model.SaveAgain();
        s.Work.RunNext();

        s.Block();
        Assert.Equal(LearningWrite.NotSaved, s.Learning.Observe("other", "we use trovio", "we use Trovioo", T).Write);
        s.Dispatcher.Drain();

        Assert.Null(s.Last.ManagementNotice);
        Assert.Equal(DictationCopy.SaveAgainFailed, s.Last.NotSavedText);
    }

    [Fact]
    public async Task A_removal_receipt_never_sits_beside_the_unsaved_warning()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Model.Remove(s.Last.Rows.Single());
        Assert.Equal(DictationCopy.VocabularyRemoved, s.Last.ManagementNotice);

        // A correction's write fails on its own thread; its outcome event has
        // not arrived yet, only the repaint.
        s.Block();
        s.Learning.Observe("other", "we use trovio", "we use Trovioo", T);
        s.Dispatcher.Drain();

        Assert.Null(s.Last.ManagementNotice);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.Last.NotSavedText);
    }

    /// <summary>The Mac keeps <c>teachMessage</c> until the next Add, Remove
    /// or Forget all (DESIGN.md, The Vocabulary section): it said what that
    /// Add's own save did. Only the Remove, Forget all and Save again line
    /// follows what is on disk now.</summary>
    [Fact]
    public async Task The_add_line_stays_until_the_next_change_as_on_the_mac()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        Assert.Equal(DictationCopy.TeachSaved, s.Last.TeachMessage);

        s.Block();
        s.Learning.Observe("other", "we use trovio", "we use Trovioo", T);
        s.Dispatcher.Drain();

        Assert.Equal(DictationCopy.TeachSaved, s.Last.TeachMessage);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.Last.NotSavedText);
    }

    [Fact]
    public async Task An_add_takes_the_last_refusal_away_at_once_even_when_its_answer_is_overtaken()
    {
        using var s = new Section(gated: true);
        var refused = s.Model.Teach("", null);
        s.Settle();
        Assert.False(await refused.Soon());
        Assert.Equal(DictationCopy.TeachEmpty, s.Last.TeachMessage);

        var teach = s.Model.Teach("SkyDock", null);
        Assert.Null(s.Last.TeachMessage);
        s.Model.SaveAgain();
        s.Settle();

        Assert.True(await teach.Soon());
        Assert.Null(s.Last.TeachMessage);
        Assert.Equal(["SkyDock"], s.Last.Rows.Select(r => r.Text));
    }

    [Fact]
    public async Task A_warning_never_stays_once_everything_is_saved()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Block();
        s.Model.Remove(s.Last.Rows.Single());
        Assert.Equal(DictationCopy.VocabularyChangeNotSaved, s.Last.NotSavedText);

        // Saved by another way: the last save at quit, or a correction's write.
        s.Unblock();
        Assert.Equal(LearningWrite.Saved, s.Learning.Flush());
        s.Dispatcher.Drain();

        Assert.Null(s.Last.NotSavedText);
        Assert.Null(s.Last.ManagementNotice);
    }

    [Fact]
    public async Task Remove_and_forget_all_clear_the_add_line()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        Assert.Equal(DictationCopy.TeachSaved, s.Last.TeachMessage);

        s.Model.Remove(s.Last.Rows.Single());

        Assert.Null(s.Last.TeachMessage);
        Assert.True(await s.Model.Teach("Trovioo", null).Soon());
        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();
        Assert.Null(s.Last.TeachMessage);
    }

    [Fact]
    public async Task Turning_learning_off_clears_the_save_receipt()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Model.Remove(s.Last.Rows.Single());
        Assert.NotNull(s.Last.ManagementNotice);

        s.Model.ApplyLearningSwitch(false);

        Assert.Null(s.Last.ManagementNotice);
    }

    // ─── Save again ─────────────────────────────────────────────────────

    [Fact]
    public async Task Save_again_reads_saving_and_is_disabled_while_it_runs_and_a_second_press_does_nothing()
    {
        using var s = new Section(gated: true);
        s.Block();
        var teach = s.Model.Teach("SkyDock", null);
        s.Settle();
        Assert.True(await teach.Soon());
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.Last.NotSavedText);
        Assert.False(s.Last.SavingAgain);

        s.Model.SaveAgain();
        s.Model.SaveAgain();

        Assert.True(s.Last.SavingAgain);
        Assert.Equal(1, s.Work.Pending);
        s.Settle();
        Assert.False(s.Last.SavingAgain);
        Assert.Equal(DictationCopy.SaveAgainFailed, s.Last.NotSavedText);
    }

    // ─── Responses off the UI thread, and their fences ──────────────────

    [Fact]
    public async Task Every_change_waits_for_the_work_runner()
    {
        using var s = new Section(gated: true);

        var teach = s.Model.Teach("SkyDock", null);
        Assert.Equal(1, s.Work.Pending);
        Assert.False(teach.IsCompleted);
        Assert.Empty(s.Learning.Snapshot().Words);
        s.Settle();
        Assert.True(await teach.Soon());

        s.Model.Remove(s.Last.Rows.Single());
        Assert.Equal(1, s.Work.Pending);
        Assert.Single(s.Learning.Snapshot().Words);
        s.Settle();
        Assert.Empty(s.Learning.Snapshot().Words);

        _ = s.Model.Teach("Trovioo", null);
        s.Settle();
        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();
        Assert.Equal(1, s.Work.Pending);
        Assert.Single(s.Learning.Snapshot().Words);
        s.Settle();
        Assert.Empty(s.Learning.Snapshot().Words);

        s.Block();
        _ = s.Model.Teach("Kubectl", null);
        s.Settle();
        s.Unblock();
        s.Model.SaveAgain();
        Assert.Equal(1, s.Work.Pending);
        Assert.True(s.Learning.Snapshot().Unsaved);
        s.Settle();
        Assert.False(s.Learning.Snapshot().Unsaved);
    }

    [Fact]
    public async Task A_response_overtaken_by_a_newer_operation_says_nothing()
    {
        using var s = new Section(gated: true);
        var teach = s.Model.Teach("SkyDock", null);
        s.Settle();
        Assert.True(await teach.Soon());
        s.Block();

        s.Model.Remove(s.Last.Rows.Single());
        s.Model.SaveAgain();
        s.Work.RunNext();
        s.Dispatcher.Drain();

        // The removal was refused, but Save again was pressed after it: only
        // the plain unsaved warning, not the removal's own sentence.
        Assert.Empty(s.Last.Rows);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.Last.NotSavedText);
        Assert.True(s.Last.SavingAgain);

        s.Unblock();
        s.Settle();

        Assert.Equal(DictationCopy.SaveAgainSucceeded, s.Last.ManagementNotice);
        Assert.Null(s.Last.NotSavedText);
    }

    [Fact]
    public async Task A_correction_learned_while_a_removal_saves_overtakes_its_receipt()
    {
        using var s = new Section(gated: true);
        var teach = s.Model.Teach("SkyDock", null);
        s.Settle();
        Assert.True(await teach.Soon());
        s.Model.Remove(s.Last.Rows.Single());

        var watch = s.Begin();
        s.Watch.Observe(new CorrectionWatchObserved(
            watch, 1, true, new LearningObservation(1, 0, 1, 0, LearningWrite.Saved, 0)));
        s.Dispatcher.Drain();
        s.Settle();

        Assert.Empty(s.Last.Rows);
        Assert.Null(s.Last.ManagementNotice);
    }

    /// <summary>The Mac's <c>result.rejections &gt; 0 || dictated != edited</c>,
    /// each half on its own: an outcome that wrote nothing still clears the
    /// receipt when the text was edited or a candidate was rejected.</summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task An_edit_or_a_rejection_that_wrote_nothing_still_clears_a_receipt(bool edited, int rejections)
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        s.Model.Remove(s.Last.Rows.Single());
        Assert.Equal(DictationCopy.VocabularyRemoved, s.Last.ManagementNotice);

        var watch = s.Begin();
        s.Watch.Observe(new CorrectionWatchObserved(
            watch, 1, edited, new LearningObservation(0, rejections, 0, 0, LearningWrite.Unchanged, 0)));
        s.Dispatcher.Drain();

        Assert.Null(s.Last.ManagementNotice);
        Assert.Equal(DictationCopy.CorrectionNothingLearned, s.Last.CorrectionNotice);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task An_edit_or_a_rejection_that_wrote_nothing_overtakes_a_pending_removal(bool edited, int rejections)
    {
        using var s = new Section(gated: true);
        var teach = s.Model.Teach("SkyDock", null);
        s.Settle();
        Assert.True(await teach.Soon());
        s.Model.Remove(s.Last.Rows.Single());

        var watch = s.Begin();
        s.Watch.Observe(new CorrectionWatchObserved(
            watch, 1, edited, new LearningObservation(0, rejections, 0, 0, LearningWrite.Unchanged, 0)));
        s.Dispatcher.Drain();
        s.Settle();

        Assert.Empty(s.Last.Rows);
        Assert.Null(s.Last.ManagementNotice);
    }

    /// <summary>On Windows a watch learns on its own thread whatever the
    /// section does (the Mac's stops at the click), so its outcome clears the
    /// receipt even when the notice no longer follows it.</summary>
    [Fact]
    public async Task A_watch_that_began_before_forget_all_and_learned_after_it_takes_the_receipt_away()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        var watch = s.Begin();
        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();
        Assert.Equal(DictationCopy.VocabularyForgotten, s.Last.ManagementNotice);

        var outcome = s.Observe(watch, "We use trovio", "We use Trovioo");

        Assert.Equal(1, outcome.NewWords);
        Assert.Equal(["Trovioo"], s.Last.Rows.Select(r => r.Text));
        Assert.Null(s.Last.ManagementNotice);
        Assert.Null(s.Last.CorrectionNotice);
    }

    [Fact]
    public async Task A_refused_removal_shows_its_own_warning_without_the_plain_one_first()
    {
        using var s = new Section(gated: true);
        var teach = s.Model.Teach("SkyDock", null);
        s.Settle();
        Assert.True(await teach.Soon());
        s.Block();
        var before = s.View.States.Count;

        s.Model.Remove(s.Last.Rows.Single());
        s.Settle();

        // One sentence for Narrator to read, not "isn't saved yet" and then
        // "This change isn't saved yet" a turn later.
        Assert.Equal(
            [DictationCopy.VocabularyChangeNotSaved],
            s.View.States.Skip(before).Select(v => v.NotSavedText).OfType<string>().Distinct());
    }

    [Fact]
    public async Task A_correction_raised_before_a_removal_cannot_clear_its_receipt()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null).Soon());
        var watch = Guid.NewGuid();

        // Raised and queued before the click, heard after it.
        s.Watch.Start(watch);
        s.Watch.Observe(new CorrectionWatchObserved(
            watch, 1, true, new LearningObservation(0, 1, 0, 0, LearningWrite.Unchanged, 0)));
        s.Model.Remove(s.Last.Rows.Single());
        s.Dispatcher.Drain();

        Assert.Equal(DictationCopy.VocabularyRemoved, s.Last.ManagementNotice);
    }

    // ─── The teach row ──────────────────────────────────────────────────

    [Fact]
    public void The_add_empties_the_row_once_the_word_is_saved()
    {
        using var s = new Section(gated: true);
        var row = new FakeTeachRow("Trovioo", "true leo");

        s.Model.Teach(row);

        Assert.Equal(0, row.Cleared);
        s.Settle();
        Assert.Equal(1, row.Cleared);
        Assert.Equal(["Trovioo", "true leo → Trovioo"], s.Last.Rows.Select(r => r.Text));
    }

    [Theory]
    [InlineData("word")]
    [InlineData("sounds like")]
    public void A_slow_add_does_not_erase_what_the_user_typed_meanwhile(string field)
    {
        using var s = new Section(gated: true);
        var row = new FakeTeachRow("Trovioo", "");

        s.Model.Teach(row);
        if (field == "word")
        {
            row.Written = "Trovioo Cloud";
        }
        else
        {
            row.SoundsLike = "true leo";
        }

        s.Settle();

        Assert.Equal(0, row.Cleared);
        Assert.Equal("Trovioo", s.Last.Rows.Single().Text);
    }

    [Fact]
    public void A_refused_add_keeps_the_row()
    {
        using var s = new Section();
        var row = new FakeTeachRow("Trovioo Cloud", "trovioo");

        s.Model.Teach(row);

        Assert.Equal(0, row.Cleared);
        Assert.Equal(DictationCopy.TeachSelfContaining, s.Last.TeachMessage);
    }

    // ─── Closing, and a change that throws ──────────────────────────────

    [Fact]
    public void Closing_waits_for_a_change_still_being_written()
    {
        var queue = new SerialWorkQueue();
        using var s = new Section(work: work => queue.Run(() =>
        {
            Thread.Sleep(300);
            work();
        }));
        Assert.Equal(TeachResult.Ok, s.Learning.Teach("SkyDock"));
        s.Model.Refresh();

        s.Model.Remove(s.Last.Rows.Single());
        s.Model.Dispose();

        Assert.Empty(s.OnDisk().Words);
    }

    /// <summary>The window lets the controller go first, so the quit's last
    /// save can run while a change is still queued here; closing then gives
    /// that change, if the disk refused it, one more try.</summary>
    [Fact]
    public void A_change_still_queued_at_the_quit_save_gets_one_more_try_when_the_section_closes()
    {
        using var go = new ManualResetEventSlim();
        var queue = new SerialWorkQueue();
        Section? section = null;
        using var s = new Section(work: work => queue.Run(() =>
        {
            go.Wait(TimeSpan.FromSeconds(10));
            work();              // refused: the folder is blocked
            section!.Unblock();  // and writable again by the time the section closes
        }));
        section = s;
        Assert.Equal(TeachResult.Ok, s.Learning.Teach("SkyDock"));
        s.Model.Refresh();
        s.Block();
        s.Model.Remove(s.Last.Rows.Single());

        // The controller's last save at quit: the removal has not run yet.
        Assert.Equal(LearningWrite.Unchanged, s.Learning.Flush());
        go.Set();
        s.Model.Dispose();

        Assert.Empty(s.OnDisk().Words);
        Assert.Equal(["skydock"], s.OnDisk().Removed.Words.Select(w => w.Key));
    }

    [Fact]
    public void A_write_still_running_after_the_wait_gets_no_save_at_close()
    {
        using var release = new ManualResetEventSlim();
        var queue = new SerialWorkQueue();
        using var s = new Section(work: work => queue.Run(() =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            work();
        }));
        Assert.Equal(TeachResult.Ok, s.Learning.Teach("SkyDock"));
        s.Model.Refresh();
        s.Block();
        Assert.Equal(TeachResult.Ok, s.Learning.Teach("Trovioo")); // refused: something is unsaved
        s.Model.Remove(s.Last.Rows.Single());

        s.Model.Dispose();
        release.Set();

        Assert.Contains("A vocabulary change was still being written when the section closed", s.Log.Lines);
        Assert.DoesNotContain(s.Log.Lines, l => l.StartsWith("Flushed learned vocabulary", StringComparison.Ordinal));
    }

    [Fact]
    public void A_closed_section_leaves_the_teach_row_alone()
    {
        using var s = new Section(gated: true);
        var row = new FakeTeachRow("Trovioo", "");
        s.Model.Teach(row);
        s.Work.RunNext();

        s.Model.Dispose();
        s.Dispatcher.Drain();

        Assert.Equal(0, row.Cleared);
        Assert.Equal("Trovioo", row.Written);
    }

    [Fact]
    public async Task A_response_after_the_section_closed_paints_nothing_and_the_add_still_answers()
    {
        var s = new Section(gated: true);
        var teach = s.Model.Teach("SkyDock", null);
        s.Work.RunNext();
        var renders = s.View.States.Count;

        s.Model.Dispose();
        s.Dispatcher.Drain();

        Assert.Equal(renders, s.View.States.Count);
        Assert.True(await teach.Soon());
    }

    public static TheoryData<string> Faults => new() { "faulted", "thrown" };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task A_change_that_fails_is_logged_by_type_and_the_section_recovers(string how)
    {
        using var s = new Section(work: work => how == "faulted"
            ? Task.FromException(new IOException(@"C:\Users\someone\SkyDock"))
            : throw new IOException(@"C:\Users\someone\SkyDock"));

        var taught = await s.Model.Teach("SkyDock", null).Soon();
        s.Model.SaveAgain();

        Assert.False(taught);
        Assert.False(s.Last.SavingAgain);
        Assert.Contains("A vocabulary change failed (IOException)", s.Log.Lines);
        Assert.DoesNotContain(s.Log.Lines, l => l.Contains("SkyDock", StringComparison.OrdinalIgnoreCase)
            || l.Contains("someone", StringComparison.Ordinal));
    }

    // ─── The work queue ─────────────────────────────────────────────────

    [Fact]
    public async Task Work_runs_off_the_calling_thread_one_item_at_a_time_in_order()
    {
        var queue = new SerialWorkQueue();
        var order = new ConcurrentQueue<int>();
        using var first = new ManualResetEventSlim();
        var ranOn = 0;
        Task one = Task.CompletedTask, two = Task.CompletedTask, three = Task.CompletedTask;

        // The caller is a thread of its own, as the UI thread is, never one of
        // the pool's: an async test's thread goes back to the pool at its first
        // await and may then run the item itself, which would read as the
        // item running on the caller.
        var caller = new Thread(() =>
        {
            one = queue.Run(() =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                first.Wait(TimeSpan.FromSeconds(10));
                order.Enqueue(1);
            });
            two = queue.Run(() => order.Enqueue(2));
            three = queue.Run(() => order.Enqueue(3));
        });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)), "Run waited for its work on the caller's thread");

        await Task.WhenAny(two, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.False(two.IsCompleted, "the second item ran while the first was still running");
        Assert.Empty(order);
        first.Set();
        await Task.WhenAll(one, two, three).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1, 2, 3], order);
        Assert.NotEqual(caller.ManagedThreadId, ranOn);
    }

    [Fact]
    public async Task An_item_that_throws_does_not_stop_the_next()
    {
        var queue = new SerialWorkQueue();
        var ran = false;

        var failing = queue.Run(() => throw new IOException("disk"));
        var next = queue.Run(() => ran = true);

        await next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ran);
        Assert.True(failing.IsFaulted);
    }

    // ─── The copy ───────────────────────────────────────────────────────

    public static TheoryData<string> Sentences => new()
    {
        DictationCopy.TeachSaved,
        DictationCopy.VocabularyRemoved,
        DictationCopy.VocabularyForgotten,
        DictationCopy.VocabularyChangeNotSaved,
        DictationCopy.SaveAgain,
        DictationCopy.SavingAgain,
        DictationCopy.SaveAgainFailed,
        DictationCopy.SaveAgainSucceeded,
    };

    /// <summary>The Mac's words, read from its source, with "on this PC"
    /// where the Mac says "on this Mac".</summary>
    [Theory]
    [MemberData(nameof(Sentences))]
    public void Every_save_line_is_the_macs_with_this_pc_for_this_mac(string sentence)
    {
        var mac = Path.Combine(DictationOverridesTests.WindowsRoot(), "..", "macos", "Sources");
        var source = File.ReadAllText(Path.Combine(mac, "DictationAppCore", "DictationVocabulary.swift"))
            + File.ReadAllText(Path.Combine(mac, "DictationApp", "DictationVocabularyView.swift"));

        Assert.DoesNotContain("Mac", sentence, StringComparison.Ordinal);
        Assert.Contains($"\"{sentence.Replace("on this PC", "on this Mac", StringComparison.Ordinal)}\"", source,
            StringComparison.Ordinal);
    }

    // ─── The window, read from source ───────────────────────────────────

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppFile(string name) =>
        Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", name);

    private static string Code() => File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{signature}'");
        var end = code.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return code[start..end];
    }

    [Fact]
    public void The_window_writes_vocabulary_off_the_ui_thread_in_order_and_logs_the_outcomes()
    {
        Assert.Contains(
            "parts.Corrections(controller.Corrections),\n"
            + "                // learning.json is written and flushed off this thread, where\n"
            + "                // the keyboard hook runs, one change at a time in click order.\n"
            + "                new SerialWorkQueue().Run, logs.CreateLogger<VocabularyModel>());",
            Code(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every learning call the section makes, other than reading the list,
    /// is an argument of <c>Run(…)</c>: so it runs off the UI thread, in
    /// click order, and is answered through the fenced response. A new one
    /// written as a direct call (an Undo, say) fails here. The one exception
    /// is the save at close, which runs once the runner has drained.
    /// </summary>
    [Fact]
    public void Every_learning_change_the_section_makes_goes_through_the_work_runner()
    {
        var core = Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.Core");
        var guarded = new HashSet<string>();
        foreach (var file in Directory.GetFiles(core, "VocabularyModel*.cs"))
        {
            var code = File.ReadAllText(file).ReplaceLineEndings("\n");
            var close = Span(code, "private void LetPendingWorkFinish()");
            foreach (Match call in Regex.Matches(code, @"\b_learning\.(\w+)"))
            {
                var name = call.Groups[1].Value;
                if (name is "Snapshot" or "Changed" || Within(close, call.Index))
                {
                    continue;
                }

                Assert.True(
                    InsideRunArguments(code, call.Index),
                    $"{Path.GetFileName(file)}: _learning.{name} is called outside Run(...)");
                guarded.Add(name);
            }
        }

        // Not vacuous: the section's own changes were found, each inside Run,
        // the learning receipt's Undo learning included.
        Assert.Superset(
            new HashSet<string> { "Teach", "RemoveWord", "RemoveAlias", "Clear", "Flush", "UndoLearning" }, guarded);
    }

    /// <summary>Where a member's body runs in <paramref name="code"/>, or an
    /// empty range when the file has no such member.</summary>
    private static Range Span(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        return start < 0 ? new Range(0, 0) : new Range(start, code.IndexOf("\n    }\n", start, StringComparison.Ordinal));
    }

    private static bool Within(Range range, int at) => at >= range.Start.Value && at < range.End.Value;

    /// <summary>Whether <paramref name="at"/> sits between the parentheses of
    /// a <c>Run(</c> or <c>Run&lt;T&gt;(</c> call that is still open there.</summary>
    private static bool InsideRunArguments(string code, int at)
    {
        foreach (Match open in Regex.Matches(code[..at], @"\bRun\s*(?:<[^<>()]*>)?\("))
        {
            var depth = 0;
            var i = open.Index + open.Length - 1;
            for (; i < at; i++)
            {
                if (code[i] == '(')
                {
                    depth++;
                }
                else if (code[i] == ')' && --depth == 0)
                {
                    break;
                }
            }

            if (i == at)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void The_add_hands_the_section_the_teach_row_so_a_slow_save_keeps_new_typing()
    {
        var code = Code();

        Assert.Contains("private void Teach() => vocabulary.Teach(this);", code, StringComparison.Ordinal);
        Assert.Contains("string IVocabularyTeachRow.Written => TeachWord.Text;", code, StringComparison.Ordinal);
        Assert.Contains("string IVocabularyTeachRow.SoundsLike => TeachSoundsLike.Text;", code, StringComparison.Ordinal);
        var clear = Body(code, "void IVocabularyTeachRow.Clear()");
        Assert.Contains("TeachWord.Clear();", clear, StringComparison.Ordinal);
        Assert.Contains("TeachSoundsLike.Clear();", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("vocabulary.Teach(TeachWord.Text", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_again_sits_under_the_unsaved_warning_and_reads_saving_while_it_runs()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var section = xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "VocabularySection");
        var names = section.Elements().Select(e => (string?)e.Attribute(X + "Name")).ToList();
        var at = names.IndexOf("TeachMessage");
        Assert.Equal(["TeachMessage", "VocabularySaveReceipt", "VocabularyNotSaved", "SaveAgainButton"], names[at..(at + 4)]);
        var receipt = section.Elements().ElementAt(at + 1);
        Assert.Equal("Polite", (string?)receipt.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.LiveSetting"));
        var button = section.Elements().ElementAt(at + 3);
        Assert.Equal(("Collapsed", "SaveAgainClick"), ((string?)button.Attribute("Visibility"), (string?)button.Attribute("Click")));

        var code = Code();
        var render = Body(code, "public void Render(VocabularyViewState state)");
        Assert.Contains("SetLiveText(VocabularySaveReceipt, state.ManagementNotice ?? string.Empty);", render, StringComparison.Ordinal);
        Assert.Contains("RenderSaveAgain(state);", render, StringComparison.Ordinal);
        var saveAgain = Body(code, "private void RenderSaveAgain(VocabularyViewState state)");
        Assert.Contains("var shown = state.NotSavedText is not null;", saveAgain, StringComparison.Ordinal);
        Assert.Contains("state.SavingAgain ? DictationCopy.SavingAgain : WithAccessKey(DictationCopy.SaveAgain, \"v\")", saveAgain, StringComparison.Ordinal);
        Assert.Contains("SaveAgainButton.IsEnabled = !state.SavingAgain;", saveAgain, StringComparison.Ordinal);
        // The keyboard goes back only from where losing it left it, never off
        // a control the user moved to during the save.
        Assert.Contains(
            "if (KeyboardStillWithSaveAgain())\n            {\n                (shown ? (UIElement)SaveAgainButton : TeachWord).Focus();",
            saveAgain, StringComparison.Ordinal);
        Assert.Contains(
            "Keyboard.FocusedElement is not DependencyObject focused\n"
            + "        || ReferenceEquals(focused, SaveAgainButton)\n"
            + "        || (focused is System.Windows.Media.Visual visual && visual.IsAncestorOf(SaveAgainButton));",
            code, StringComparison.Ordinal);
        Assert.Contains("vocabulary.SaveAgain();", Body(code, "private void SaveAgainClick("), StringComparison.Ordinal);
    }
}

internal static class Answered
{
    /// <summary>The task, or a failure after 10 s: a change that never
    /// answers fails its test instead of hanging the run.</summary>
    public static Task<T> Soon<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));
}
