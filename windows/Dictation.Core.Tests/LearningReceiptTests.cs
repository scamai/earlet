using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Dictation.Services.Learning;

namespace Dictation.Core.Tests;

/// <summary>
/// The learning receipt and Undo learning in the Vocabulary section
/// (docs/design-macos.md, Learning feedback, receipts and Undo): the macOS
/// <c>LearningFeedbackTests</c> scenarios and the receipt halves of its
/// <c>ObservationFeedbackTests</c>, one for one under the same names, then
/// what Windows adds — the posted events' fences and what the window is told.
/// </summary>
/// <remarks>
/// The Mac's model runs learning itself; on Windows the correction watch
/// does, on the thread pool, and the section hears the outcome. So these
/// ports learn for real where the Mac does and hand the outcome over the way
/// the watch does: an Observed event raised on the pool and posted to the UI
/// thread. The Mac's first scenario, through the app's own observer, is in
/// <see cref="LearningReceiptFlowTests"/>.
/// </remarks>
public sealed class LearningReceiptTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private sealed class Section : IDisposable
    {
        /// <param name="seed">What the file holds before the section first reads it.</param>
        /// <param name="gated">Hold every change the section hands its work
        /// runner until the test runs it, as a slow disk would; otherwise it
        /// runs before the call returns.</param>
        public Section(LearningState? seed = null, bool gated = false)
        {
            Store = new LearningStore(LearningGoldenFileTests.TempDirectory());
            if (seed is not null)
            {
                var writer = new LearningStore(Store.Directory);
                writer.Load();
                Assert.True(writer.Save(seed));
            }

            Learning = new DictationLearning(Store, new WordListLexicon(LearningGoldenTests.SharedLexicon));
            Model = new VocabularyModel(Learning, View, Dispatcher.Post, Watch, gated ? Work.Run : null);
            Model.CorrectionRemembered += (_, _) => Remembered++;
            Model.Refresh();
        }

        public LearningStore Store { get; }
        public DictationLearning Learning { get; }
        public FakeCorrectionWatch Watch { get; } = new();
        public RecordingVocabularyView View { get; } = new();
        public DeferredDispatcher Dispatcher { get; } = new();
        public VocabularySaveTests.GatedWork Work { get; } = new();
        public VocabularyModel Model { get; }
        public int Remembered { get; private set; }

        public string? Receipt => View.Last.LearningReceipt;

        public string? Notice => View.Last.CorrectionNotice;

        public LearningState OnDisk() => new LearningStore(Store.Directory).Load().State;

        public string? FileText()
        {
            var file = Path.Combine(Store.Directory, LearningStore.FileName);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }

        public Guid Begin()
        {
            var watch = Guid.NewGuid();
            Watch.Start(watch);
            Dispatcher.Drain();
            return watch;
        }

        /// <summary>What a watch does with a settled revision: learn on its
        /// thread, then tell. A watch of its own when none is given.</summary>
        public LearningObservation Observe(
            string dictated, string edited, Guid? watch = null, string? dictation = null, DateTimeOffset? at = null)
        {
            var id = watch ?? Begin();
            var outcome = Learning.Observe(dictation ?? id.ToString("N"), dictated, edited, at ?? T);
            Watch.Observe(new CorrectionWatchObserved(id, 1, dictated != edited, outcome));
            Dispatcher.Drain();
            return outcome;
        }

        /// <summary>The Mac's <c>learn</c>: "We use trovio" fixed to "We use Trovioo".</summary>
        public LearningObservation Learn(string dictation = "one", Guid? watch = null) =>
            Observe("We use trovio", "We use Trovioo", watch, dictation);

        /// <summary>A file where the store's folder should be, the folder
        /// kept aside: every save is refused until <see cref="Unblock"/> puts
        /// it back as it was.</summary>
        public void Block()
        {
            Directory.CreateDirectory(Store.Directory);
            Directory.Move(Store.Directory, Store.Directory + ".aside");
            File.WriteAllText(Store.Directory, "a file where the folder should be");
        }

        public void Unblock()
        {
            File.Delete(Store.Directory);
            Directory.Move(Store.Directory + ".aside", Store.Directory);
        }

        /// <summary>Run every change still held, then hear the answers.</summary>
        public void Settle()
        {
            Work.RunAll();
            Dispatcher.Drain();
        }

        public void Dispose()
        {
            Work.RunAll();
            Model.Dispose();
        }
    }

    // ─── The Mac's LearningFeedbackTests ────────────────────────────────

    [Fact]
    public async Task Undo_restores_the_previous_alias_and_word_counts_not_an_empty_list()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", "sky dock"));
        s.Learn();
        var before = s.FileText();
        s.Learn("two");
        Assert.Contains(s.OnDisk().Aliases, a => a.Heard == "trovio");

        s.Model.UndoLastLearning();

        Assert.Equal(before, s.FileText());
        Assert.Contains(s.View.Last.Rows, r => r.Text == "SkyDock");
    }

    [Fact]
    public async Task A_later_manual_edit_expires_the_undo_token_and_cannot_be_overwritten()
    {
        using var s = new Section();
        var token = s.Learn().UndoToken;
        Assert.NotNull(token);

        Assert.True(await s.Model.Teach("SkyDock", ""));

        Assert.False(s.Model.CanUndoLearning);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Null(s.Receipt);
        Assert.Null(s.Learning.UndoLearning(token!.Value));
        Assert.Equal(["SkyDock", "Trovioo"], s.OnDisk().Words.Select(w => w.Text).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_unchanged_next_dictation_leaves_the_saved_receipt_and_undo_available()
    {
        using var s = new Section();
        s.Learn();

        s.Observe("We use it", "We use it", dictation: "unchanged");

        Assert.True(s.View.Last.CanUndoLearning);
        Assert.Equal(DictationCopy.LearningRemembered, s.Receipt);
        s.Model.UndoLastLearning();
        Assert.Empty(s.OnDisk().Words);
    }

    [Fact]
    public void Undoing_a_newly_winning_alias_restores_its_previous_contested_state()
    {
        var now = T.ToUnixTimeMilliseconds();
        var existing = new LearningState();
        existing.Words.Add(new LearnedWord { Text = "Trovion", Origin = LearningOrigins.Correction, Count = 2, FirstSeen = now, LastSeen = now });
        existing.Aliases.Add(new LearnedAlias { Heard = "trovio", Meant = "Trovion", Origin = LearningOrigins.Correction, Count = 2, FirstSeen = now, LastSeen = now });
        using var s = new Section(existing);
        s.Learn();
        s.Learn("second");
        s.Learn("third");
        var before = s.FileText();
        Assert.Empty(s.OnDisk().Aliases); // three against two is still contested

        s.Learn("fourth");
        Assert.Equal("Trovioo", s.OnDisk().Aliases.Single().Meant);
        s.Model.UndoLastLearning();

        Assert.Equal(before, s.FileText());
        Assert.Empty(s.OnDisk().Aliases);
    }

    [Fact]
    public void Nothing_learned_and_a_repeated_pending_correction_never_claim_a_new_saved_word()
    {
        using var s = new Section();
        var none = s.Observe("We use it", "We use it", dictation: "none");
        Assert.Null(none.UndoToken);
        Assert.Null(s.Receipt);

        s.Learn();
        Assert.Equal(DictationCopy.LearningRemembered, s.Receipt);
        var repeated = s.Learn();

        Assert.Equal((0, 0), (repeated.NewWords, repeated.NewAliases));
        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Equal(1, s.Remembered);
    }

    [Fact]
    public async Task Failed_saves_do_not_produce_success_feedback_or_an_undo_promise()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", ""));
        s.Block();

        var result = s.Learn();

        Assert.Equal(LearningWrite.NotSaved, result.Write);
        Assert.Null(result.UndoToken);
        Assert.Null(s.Receipt);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.View.Last.NotSavedText);
        Assert.Equal(0, s.Remembered);
        s.Unblock();
        Assert.Equal(["SkyDock"], s.OnDisk().Words.Select(w => w.Text));
    }

    [Fact]
    public void An_undo_whose_save_fails_stays_unsaved_and_never_reports_saved_success()
    {
        using var s = new Section();
        s.Learn();
        var learned = s.FileText();
        s.Block();

        s.Model.UndoLastLearning();

        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.View.Last.NotSavedText);
        Assert.Empty(s.View.Last.Rows);
        s.Unblock();
        Assert.Equal(learned, s.FileText());
        // The last save at quit, which LearningReceiptFlowTests runs through the controller.
        Assert.Equal(LearningWrite.Saved, s.Learning.Flush());
        Assert.Empty(s.OnDisk().Words);
    }

    // ─── The Mac's ObservationFeedbackTests, receipt halves ─────────────

    [Fact]
    public void An_unchanged_edit_reports_no_correction_and_keeps_an_existing_undo_receipt()
    {
        using var s = new Section();
        s.Learn("learn");
        var receipt = s.Receipt;
        Assert.NotNull(receipt);

        var id = s.Begin();
        s.Observe("we use it", "we use it", id, "same");

        Assert.Equal("No correction detected.", s.Notice);
        Assert.True(s.View.Last.CanUndoLearning);
        Assert.Equal(receipt, s.Receipt);
    }

    [Fact]
    public void A_saved_correction_uses_the_existing_receipt_and_clears_observation_feedback()
    {
        using var s = new Section();
        var id = s.Begin();
        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);

        s.Observe("we use trovio", "we use Trovioo", id, "saved");

        Assert.Null(s.Notice);
        Assert.Contains("Remembered", s.Receipt, StringComparison.Ordinal);
        Assert.Equal(["Trovioo"], s.OnDisk().Words.Select(w => w.Text));
    }

    // ─── What the section says ──────────────────────────────────────────

    [Fact]
    public void The_receipt_says_what_the_mac_says_and_undo_says_it_is_undone()
    {
        using var s = new Section();
        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);

        s.Learn();
        Assert.Equal("Remembered your correction for next time.", s.Receipt);
        Assert.True(s.View.Last.CanUndoLearning);

        s.Model.UndoLastLearning();

        Assert.Equal("Learning undone. Your previous vocabulary is restored.", s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Empty(s.View.Last.Rows);
        Assert.Empty(s.OnDisk().Words);
        Assert.Empty(s.Watch.EndRequests); // the Mac keeps observing through an undo
    }

    [Fact]
    public void An_undo_the_vocabulary_has_moved_past_says_so_and_changes_nothing()
    {
        using var s = new Section();
        s.Learn();
        // Changed off the section's path — another watch's revision, still
        // queued — so the section still offers Undo.
        s.Learning.Observe("other", "the wifi password is sunshine", "the wifi password is Sunshine42x!", T);
        Assert.True(s.Model.CanUndoLearning);
        var after = s.FileText();

        s.Model.UndoLastLearning();

        Assert.Equal("Vocabulary has changed. Review it below to remove a word.", s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Equal(after, s.FileText());
        Assert.Contains("Trovioo", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_does_nothing_once_it_is_no_longer_offered()
    {
        using var s = new Section();
        s.Learn();
        s.Model.UndoLastLearning();
        var undone = s.FileText();
        var renders = s.View.States.Count;

        s.Model.UndoLastLearning();

        Assert.Equal(renders, s.View.States.Count);
        Assert.Equal(undone, s.FileText());
    }

    public static TheoryData<string> Clears => new()
    {
        "dismiss", "teach", "a refused teach", "remove", "forget all", "switch off",
    };

    [Theory]
    [MemberData(nameof(Clears))]
    public async Task The_receipt_and_its_undo_go_on_dismiss_teach_remove_forget_all_and_switch_off(string how)
    {
        using var s = new Section();
        s.Learn();
        Assert.NotNull(s.Receipt);

        switch (how)
        {
            case "dismiss":
                s.Model.DismissLearningReceipt();
                break;
            case "teach":
                Assert.True(await s.Model.Teach("SkyDock", null));
                break;
            case "a refused teach":
                Assert.False(await s.Model.Teach("", null));
                break;
            case "remove":
                s.Model.Remove(s.View.Last.Rows.Single());
                break;
            case "forget all":
                s.Model.AskForgetAll();
                s.Model.ConfirmForgetAll();
                break;
            default:
                s.Model.ApplyLearningSwitch(false);
                break;
        }

        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.False(s.Model.CanUndoLearning);
        // The four the user does in the section also stop the watch, as the
        // Mac's observe stops learning for it; the switch is the controller's
        // to end (SwitchedOff), and nothing here ends it twice.
        Assert.Equal(
            how == "switch off" ? [] : [CorrectionWatchEnd.NoticesCleared],
            s.Watch.EndRequests);
    }

    [Fact]
    public void Dismiss_keeps_the_vocabulary_and_a_closed_section_neither_undoes_nor_repaints()
    {
        using var s = new Section();
        s.Learn();
        s.Model.DismissLearningReceipt();
        Assert.Equal(["Trovioo"], s.OnDisk().Words.Select(w => w.Text));

        s.Learn("two");
        Assert.True(s.Model.CanUndoLearning);
        var renders = s.View.States.Count;
        s.Model.Dispose();

        s.Model.UndoLastLearning();
        s.Model.DismissLearningReceipt();

        Assert.Equal(renders, s.View.States.Count);
        Assert.Contains(s.OnDisk().Aliases, a => a.Heard == "trovio");
    }

    // ─── Windows: the posted outcome and its fences ─────────────────────

    /// <summary>An outcome raised before the window closed, still in the
    /// queue: a closed section neither shows a receipt nor tells the
    /// indicator it did.</summary>
    [Fact]
    public void An_outcome_queued_before_the_section_closes_is_dropped()
    {
        using var s = new Section();
        var id = s.Begin();
        var outcome = s.Learning.Observe(id.ToString("N"), "we use trovio", "we use Trovioo", T);
        Assert.NotNull(outcome.UndoToken);
        s.Watch.Observe(new CorrectionWatchObserved(id, 1, true, outcome));
        var renders = s.View.States.Count;

        s.Model.Dispose();
        s.Dispatcher.Drain();

        Assert.Equal(0, s.Remembered);
        Assert.False(s.Model.CanUndoLearning);
        Assert.Equal(renders, s.View.States.Count);
    }

    [Fact]
    public void A_receipt_is_given_only_while_the_list_is_all_on_disk()
    {
        using var s = new Section();
        var id = s.Begin();
        var outcome = s.Learning.Observe(id.ToString("N"), "we use trovio", "we use Trovioo", T);
        Assert.NotNull(outcome.UndoToken);
        // A change the disk refused lands before the outcome is heard.
        s.Block();
        Assert.Equal(LearningWrite.NotSaved, s.Learning.RemoveWord("Trovioo", T));

        s.Watch.Observe(new CorrectionWatchObserved(id, 1, true, outcome));
        s.Dispatcher.Drain();

        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Equal(0, s.Remembered);
    }

    [Fact]
    public void An_outcome_from_an_older_watch_cannot_give_a_receipt()
    {
        using var s = new Section();
        var old = s.Begin();
        s.Begin();

        s.Learn(watch: old);

        Assert.Null(s.Receipt);
        Assert.Equal(0, s.Remembered);
    }

    /// <summary>A watch's start and its outcome both still queued when the
    /// user dismisses: the start must not bring the watch back, and with it
    /// the receipt.</summary>
    [Fact]
    public void An_outcome_queued_before_a_dismiss_cannot_bring_the_receipt_back()
    {
        using var s = new Section();
        var id = Guid.NewGuid();
        s.Watch.Start(id);
        var outcome = s.Learning.Observe(id.ToString("N"), "we use trovio", "we use Trovioo", T);
        s.Watch.Observe(new CorrectionWatchObserved(id, 1, true, outcome));

        s.Model.DismissLearningReceipt();
        s.Dispatcher.Drain();

        Assert.Null(s.Receipt);
        Assert.Equal(0, s.Remembered);
    }

    [Fact]
    public void The_next_watch_and_any_ending_leave_the_receipt_in_place()
    {
        using var s = new Section();
        var first = s.Begin();
        s.Learn(watch: first);
        var receipt = s.Receipt;

        s.Watch.End(new CorrectionWatchEnded(first, CorrectionWatch.TerminalAbandoned, null, CorrectionWatchEnd.NextDictation, 1, true));
        var next = s.Begin();
        s.Watch.End(new CorrectionWatchEnded(next, SpanObservation.TerminalUnobservable, SpanObservation.ReasonAnchorLost, null, 0, false));
        s.Dispatcher.Drain();

        Assert.Equal(receipt, s.Receipt);
        Assert.True(s.View.Last.CanUndoLearning);
    }

    [Fact]
    public void A_later_change_or_an_edit_that_taught_nothing_takes_the_receipt_down()
    {
        using var s = new Section();
        s.Learn();
        s.Observe("the wifi password is sunshine", "the wifi password is Sunshine42x!", dictation: "pending");
        Assert.Equal(DictationCopy.CorrectionPending, s.Notice);
        Assert.Null(s.Receipt);

        s.Learn("two");
        Assert.NotNull(s.Receipt);
        s.Observe("we have a big meeting", "we have a large meeting", dictation: "rejected");
        Assert.Equal(DictationCopy.CorrectionNothingLearned, s.Notice);
        Assert.Null(s.Receipt);
    }

    /// <summary>A change with no edit behind it: a month on, an untouched
    /// dictation's check prunes the pair the learned word left waiting. The
    /// vocabulary changed, so the receipt's Undo no longer stands.</summary>
    [Fact]
    public void A_later_change_with_no_edit_behind_it_takes_the_receipt_down()
    {
        using var s = new Section();
        Assert.Equal(1, s.Learn().PendingCorrections);
        Assert.NotNull(s.Receipt);

        var later = s.Observe("We use it", "We use it", dictation: "untouched", at: T.AddDays(31));

        Assert.Equal((LearningWrite.Saved, 0), (later.Write, s.OnDisk().Pending.Count));
        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
    }

    [Fact]
    public void Each_new_receipt_is_announced_to_the_composer_once()
    {
        using var s = new Section();

        s.Learn();
        s.Observe("We use it", "We use it", dictation: "unchanged");
        s.Model.UndoLastLearning();

        Assert.Equal(1, s.Remembered);
        s.Learn("again");
        Assert.Equal(2, s.Remembered);
    }

    [Fact]
    public void No_receipt_or_undo_line_names_a_word()
    {
        using var s = new Section();
        s.Learn();
        var shown = new List<string?> { s.Receipt };
        s.Learning.Teach("Sentinelword", null, T); // expires it off the section's path
        s.Model.UndoLastLearning();
        shown.Add(s.Receipt);
        s.Learn("two");
        s.Model.UndoLastLearning();
        shown.Add(s.Receipt);

        Assert.Equal(3, shown.Distinct().Count());
        Assert.All(shown, line =>
        {
            Assert.NotNull(line);
            Assert.DoesNotContain("rovio", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("entinel", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ─── With the save outcomes: Undo through the work runner ───────────

    /// <summary>Undo learning writes <c>learning.json</c> like Add and
    /// Remove: on the work runner, off the UI thread where the keyboard hook
    /// runs, and the section answers once the write is done.</summary>
    [Fact]
    public void Undo_learning_writes_through_the_work_runner_and_answers_when_it_is_done()
    {
        using var s = new Section(gated: true);
        s.Learn();
        var learned = s.FileText();

        s.Model.UndoLastLearning();

        Assert.Equal(1, s.Work.Pending);
        Assert.Equal(learned, s.FileText());
        Assert.False(s.View.Last.CanUndoLearning); // gone at the click: nothing left to undo
        Assert.Equal(DictationCopy.LearningRemembered, s.Receipt); // the line waits for the answer

        s.Settle();

        Assert.Equal(DictationCopy.LearningUndone, s.Receipt);
        Assert.Empty(s.OnDisk().Words);
        Assert.Empty(s.View.Last.Rows);
    }

    /// <summary>Undo's answer is fenced like every other: one that arrives
    /// after the user dismissed the receipt says nothing, though the undo
    /// itself still ran.</summary>
    [Fact]
    public void An_undo_answer_overtaken_by_a_dismiss_says_nothing()
    {
        using var s = new Section(gated: true);
        s.Learn();
        s.Model.UndoLastLearning();

        s.Model.DismissLearningReceipt();
        s.Settle();

        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Empty(s.OnDisk().Words);
    }

    /// <summary>
    /// Undo learning is a newer operation: an answer still in flight when it
    /// is clicked says nothing. A change off the section's path — another
    /// watch's revision, its outcome still queued — was refused, so Save again
    /// shows beside the receipt; the user clicks it, then Undo, before either
    /// answers. Save again's "Changes saved…" must not land beside Undo's own
    /// line.
    /// </summary>
    [Fact]
    public void Undo_learning_overtakes_an_answer_still_in_flight()
    {
        using var s = new Section(gated: true);
        s.Learn();
        var pending = s.OnDisk().Pending.Count;
        s.Block();
        Assert.Equal(
            LearningWrite.NotSaved,
            s.Learning.Observe("other", "the wifi password is sunshine", "the wifi password is Sunshine42x!", T).Write);
        s.Dispatcher.Drain();
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.View.Last.NotSavedText);
        s.Unblock();

        s.Model.SaveAgain();
        s.Model.UndoLastLearning();
        s.Settle();

        Assert.Null(s.View.Last.ManagementNotice);
        Assert.Null(s.View.Last.NotSavedText);
        Assert.False(s.View.Last.SavingAgain);
        // The refused change expired the token, so the undo changed nothing.
        Assert.Equal(DictationCopy.LearningUndoExpired, s.Receipt);
        Assert.Equal(["Trovioo"], s.OnDisk().Words.Select(w => w.Text));
        Assert.Equal(pending + 1, s.OnDisk().Pending.Count); // Save again saved the refused change
    }

    // ─── With the save outcomes: which line wins ────────────────────────

    /// <summary>The Mac's receipt branch sets <c>managementNotice = nil</c>:
    /// "Removed from your vocabulary." does not stay beside "Remembered your
    /// correction for next time.". The next watch's start keeps it; the
    /// receipt takes it down.</summary>
    [Fact]
    public async Task A_new_receipt_takes_down_the_save_line_as_on_the_mac()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null));
        s.Model.Remove(s.View.Last.Rows.Single());
        Assert.Equal(DictationCopy.VocabularyRemoved, s.View.Last.ManagementNotice);
        var next = s.Begin();
        Assert.Equal(DictationCopy.VocabularyRemoved, s.View.Last.ManagementNotice);

        s.Learn(watch: next);

        Assert.Equal(DictationCopy.LearningRemembered, s.Receipt);
        Assert.Null(s.View.Last.ManagementNotice);
        Assert.Null(s.View.Last.NotSavedText);
    }

    /// <summary>The next dictation's fix lands while an Undo is still being
    /// written: the new receipt and its Undo stand, and the old undo's answer
    /// — which found the vocabulary changed — says nothing over them.</summary>
    [Fact]
    public void A_new_receipt_overtakes_an_undo_still_in_flight()
    {
        using var s = new Section(gated: true);
        s.Learn();
        s.Model.UndoLastLearning();

        s.Learn("two");
        Assert.True(s.View.Last.CanUndoLearning);
        s.Settle();

        Assert.Equal(DictationCopy.LearningRemembered, s.Receipt);
        Assert.True(s.View.Last.CanUndoLearning);
        Assert.Contains(s.OnDisk().Aliases, a => a.Heard == "trovio");
    }

    public static TheoryData<bool> LateWrites => new() { true, false };

    /// <summary>
    /// A watch already past its check when the next dictation began still
    /// learns on its own thread, and its outcome arrives after the next
    /// watch's receipt. As the Mac's <c>dismissLearningNotice</c> takes the
    /// receipt and the save line together, the receipt goes: refused, it
    /// would otherwise say "Remembered…" beside "Your vocabulary isn't saved
    /// yet…"; saved, it would offer an Undo the vocabulary has moved past.
    /// </summary>
    [Theory]
    [MemberData(nameof(LateWrites))]
    public void Another_watchs_later_change_takes_the_receipt_down_with_the_save_line(bool refused)
    {
        using var s = new Section();
        var old = s.Begin();
        s.Learn(watch: s.Begin());
        Assert.Equal(DictationCopy.LearningRemembered, s.Receipt);
        if (refused)
        {
            s.Block();
        }

        var late = s.Observe(
            "the wifi password is sunshine", "the wifi password is Sunshine42x!", old, "late");

        Assert.Equal(refused ? LearningWrite.NotSaved : LearningWrite.Saved, late.Write);
        Assert.Null(s.Receipt);
        Assert.False(s.View.Last.CanUndoLearning);
        Assert.Null(s.View.Last.ManagementNotice);
        Assert.Equal(refused ? DictationCopy.VocabularyNotSaved : null, s.View.Last.NotSavedText);
        Assert.Equal(1, s.Remembered);
    }
}
