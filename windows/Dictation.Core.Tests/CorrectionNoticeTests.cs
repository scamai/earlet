using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Dictation.Services.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

/// <summary>
/// A correction watch as the Vocabulary section hears it: its events, raised
/// on the thread pool as <see cref="CorrectionWatch"/> raises them.
/// </summary>
internal sealed class FakeCorrectionWatch : ICorrectionWatchEvents
{
    public event EventHandler<CorrectionWatchStarted>? Started;

    public event EventHandler<CorrectionWatchObserved>? Observed;

    public event EventHandler<CorrectionWatchEnded>? Ended;

    /// <summary>Handlers still attached, over all three events.</summary>
    public int Listeners =>
        (Started?.GetInvocationList().Length ?? 0)
        + (Observed?.GetInvocationList().Length ?? 0)
        + (Ended?.GetInvocationList().Length ?? 0);

    public void Start(Guid watch) => OnPool(() => Started?.Invoke(this, new CorrectionWatchStarted(watch)));

    public void Observe(CorrectionWatchObserved observed) => OnPool(() => Observed?.Invoke(this, observed));

    public void End(CorrectionWatchEnded ended) => OnPool(() => Ended?.Invoke(this, ended));

    /// <summary>Why the section asked the watch to end, in order.</summary>
    public List<CorrectionWatchEnd> EndRequests { get; } = [];

    /// <inheritdoc/>
    public void End(CorrectionWatchEnd why) => EndRequests.Add(why);

    private static void OnPool(Action raise) => Task.Run(raise).Wait();
}

/// <summary>
/// The Vocabulary section's correction notice (docs/design-macos.md, Correction notices):
/// the macOS <c>ObservationFeedbackTests</c> scenarios, one for one under the
/// same names, then what Windows adds — the posted events and their fences,
/// and the map from span-observer terminals to the Mac's sentences.
/// </summary>
/// <remarks>
/// The Mac's model runs learning itself (<c>observe</c>); on Windows the watch
/// does, on its own thread, and the section only hears the outcome. So these
/// ports run the real learning where the Mac does and hand its outcome to the
/// section the way the watch does: an event raised on the pool, marshalled
/// through the post delegate. The Mac scenarios' receipt halves are in
/// <see cref="LearningReceiptTests"/>.
/// </remarks>
public sealed class CorrectionNoticeTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private sealed class Section : IDisposable
    {
        public Section(Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
        {
            Store = new LearningStore(LearningGoldenFileTests.TempDirectory());
            Learning = new DictationLearning(
                Store, new WordListLexicon(LearningGoldenTests.SharedLexicon), log: logs?.CreateLogger<DictationLearning>());
            Model = new VocabularyModel(Learning, View, Dispatcher.Post, Watch);
            Model.Refresh();
        }

        public LearningStore Store { get; }
        public DictationLearning Learning { get; }
        public FakeCorrectionWatch Watch { get; } = new();
        public RecordingVocabularyView View { get; } = new();
        public DeferredDispatcher Dispatcher { get; } = new();
        public VocabularyModel Model { get; }

        public string? Notice => View.Last.CorrectionNotice;

        /// <summary>A watch begins, and the UI thread hears of it.</summary>
        public Guid Begin(Guid? id = null)
        {
            var watch = id ?? Guid.NewGuid();
            Watch.Start(watch);
            Dispatcher.Drain();
            return watch;
        }

        /// <summary>What the watch does with a settled revision: learn, on
        /// its thread, then tell. <paramref name="dictation"/> defaults to the
        /// watch's id, as the watch uses it.</summary>
        public LearningObservation Observe(Guid watch, string dictated, string edited, string? dictation = null)
        {
            var outcome = Learning.Observe(dictation ?? watch.ToString("N"), dictated, edited, T);
            Watch.Observe(new CorrectionWatchObserved(watch, 1, dictated != edited, outcome));
            Dispatcher.Drain();
            return outcome;
        }

        public void End(Guid watch, string terminal, string? reason = null, CorrectionWatchEnd? by = null,
            int revisions = 0, bool sawChange = false)
        {
            Watch.End(new CorrectionWatchEnded(watch, terminal, reason, by, revisions, sawChange));
            Dispatcher.Drain();
        }

        /// <summary>A file where the store's folder should be: every save is refused.</summary>
        public void Block()
        {
            if (Directory.Exists(Store.Directory))
            {
                Directory.Delete(Store.Directory, recursive: true);
            }

            File.WriteAllText(Store.Directory, "a file where the folder should be");
        }

        public void Dispose() => Model.Dispose();
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

    private const string Unobservable = SpanObservation.TerminalUnobservable;

    // ─── The Mac's scenarios ────────────────────────────────────────────

    [Fact]
    public void An_unreadable_field_gives_a_generic_manual_add_fallback()
    {
        using var s = new Section();
        var id = s.Begin();
        Assert.Equal("Checking this field for corrections…", s.Notice);

        // The pasted text was not found once in the field: the Mac's span_missing.
        s.End(id, Unobservable, SpanObservation.ReasonAnchorAmbiguous);

        Assert.Equal("The field could not be read. You can add the word manually below.", s.Notice);
    }

    [Fact]
    public void A_stale_completion_cannot_replace_the_current_observation()
    {
        using var s = new Section();
        var first = s.Begin();
        var current = s.Begin();

        s.End(first, Unobservable, SpanObservation.ReasonNoFocus);

        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);
        s.End(current, Unobservable, SpanObservation.ReasonAnchorLost);
        Assert.Contains("manually", s.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void An_observation_result_for_an_older_begin_cannot_replace_current_checking()
    {
        using var s = new Section();
        var old = s.Begin();
        s.Begin();

        // Learned, saved, a new word: it would clear the line were it current.
        s.Watch.Observe(new CorrectionWatchObserved(
            old, 1, true, new LearningObservation(1, 0, 1, 0, LearningWrite.Saved, 1)));
        s.Dispatcher.Drain();

        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);
    }

    [Fact]
    public void Abandoning_an_observation_clears_its_transient_feedback()
    {
        using var s = new Section();
        var id = s.Begin();

        s.End(id, CorrectionWatch.TerminalAbandoned, by: CorrectionWatchEnd.Paused);

        Assert.Null(s.Notice);
        s.Begin();
        s.End(id, CorrectionWatch.TerminalAbandoned, by: CorrectionWatchEnd.NextDictation);
        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);
    }

    [Fact]
    public void A_real_pending_pair_says_another_matching_correction_is_needed()
    {
        var (logs, log) = FileLog();
        using (logs)
        {
            using var s = new Section(logs);
            var id = s.Begin();

            var result = s.Observe(id, "the wifi password is sunshine", "the wifi password is Sunshine42x!");

            Assert.Equal(0, result.NewWords);
            Assert.Equal(1, result.PendingCorrections);
            Assert.Contains("Another matching correction", s.Notice, StringComparison.Ordinal);
            Assert.DoesNotContain("Sunshine", s.Notice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sunshine", ReadLog(log), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_rejected_edit_offers_manual_add_without_echoing_text()
    {
        var (logs, log) = FileLog();
        using (logs)
        {
            using var s = new Section(logs);
            var id = s.Begin();

            s.Observe(id, "we have a big meeting", "we have a large meeting");

            Assert.Contains("No vocabulary change", s.Notice, StringComparison.Ordinal);
            Assert.DoesNotContain("big", s.Notice, StringComparison.Ordinal);
            Assert.DoesNotContain("meeting", ReadLog(log), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unchanged_edit_reports_no_correction()
    {
        using var s = new Section();
        var id = s.Begin();

        s.Observe(id, "we use it", "we use it");

        Assert.Equal("No correction detected.", s.Notice);
    }

    [Fact]
    public void A_saved_correction_clears_observation_feedback()
    {
        using var s = new Section();
        var id = s.Begin();

        s.Observe(id, "we use trovio", "we use Trovioo");

        // The learning receipt takes over here (LearningReceiptTests).
        Assert.Null(s.Notice);
        Assert.Equal(DictationCopy.LearningRemembered, s.View.Last.LearningReceipt);
        Assert.Equal(["Trovioo"], new LearningStore(s.Store.Directory).Load().State.Words.Select(w => w.Text));
        Assert.Contains(s.View.Last.Rows, r => r.Text == "Trovioo");
    }

    [Fact]
    public async Task A_failed_save_leaves_the_existing_unsaved_warning()
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null));
        s.Block();
        var id = s.Begin();

        var result = s.Observe(id, "we use trovio", "we use Trovioo");

        Assert.Equal(LearningWrite.NotSaved, result.Write);
        Assert.Equal(DictationCopy.VocabularyNotSaved, s.View.Last.NotSavedText);
        Assert.Null(s.Notice);
    }

    public static TheoryData<string> Clears => new() { "dismiss", "teach", "a refused teach", "remove", "forget all", "switch off" };

    [Theory]
    [MemberData(nameof(Clears))]
    public async Task Dismiss_and_manual_teach_clear_stale_observation_feedback(string how)
    {
        using var s = new Section();
        Assert.True(await s.Model.Teach("SkyDock", null));
        var id = s.Begin();
        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);

        await Act(s, how);

        Assert.Null(s.Notice);
        // And the rest of that watch says nothing.
        s.Watch.Observe(new CorrectionWatchObserved(id, 1, true, new LearningObservation(0, 1, 0, 0, LearningWrite.Unchanged, 0)));
        s.Dispatcher.Drain();
        s.End(id, Unobservable, SpanObservation.ReasonAnchorLost);
        Assert.Null(s.Notice);
    }

    private static async Task Act(Section s, string how)
    {
        switch (how)
        {
            case "dismiss":
                s.Model.DismissLearningReceipt();
                break;
            case "teach":
                Assert.True(await s.Model.Teach("Trovioo", null));
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
    }

    [Fact]
    public void An_already_known_correction_update_is_not_reported_as_pending()
    {
        using var s = new Section();
        s.Learning.Observe("first", "we use trovio", "we use Trovioo", T);
        s.Learning.Observe("second", "we use trovio", "we use Trovioo", T);
        var id = s.Begin();

        var result = s.Observe(id, "we use trovio", "we use Trovioo", dictation: "third");

        Assert.Equal(0, result.PendingCorrections);
        Assert.DoesNotContain("Another matching correction", s.Notice, StringComparison.Ordinal);
        Assert.Equal("This correction is already in your vocabulary.", s.Notice);
    }

    // ─── Windows: the posted events and their fences ────────────────────

    [Fact]
    public void Watch_events_change_the_line_only_on_the_ui_thread()
    {
        using var s = new Section();
        var renders = s.View.States.Count;

        s.Watch.Start(Guid.NewGuid());

        Assert.Equal(renders, s.View.States.Count);
        Assert.Equal(1, s.Dispatcher.Pending);
        s.Dispatcher.Drain();
        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);
    }

    [Fact]
    public async Task An_event_raised_before_the_user_cleared_the_line_does_not_bring_it_back()
    {
        using var s = new Section();
        var id = Guid.NewGuid();

        // Raised and queued, then an Add on the UI thread before the queue runs.
        s.Watch.Start(id);
        Assert.True(await s.Model.Teach("SkyDock", null));
        s.Dispatcher.Drain();

        Assert.Null(s.Notice);
        s.End(id, Unobservable, SpanObservation.ReasonAnchorLost);
        Assert.Null(s.Notice);

        // A watch that begins after the Add is heard again.
        s.Begin();
        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);
    }

    [Fact]
    public void With_learning_off_a_watch_shows_nothing_and_turning_it_back_on_shows_the_next()
    {
        using var s = new Section();
        s.Model.ApplyLearningSwitch(false);

        var id = s.Begin();
        s.End(id, Unobservable, SpanObservation.ReasonAnchorAmbiguous);

        Assert.Null(s.Notice);
        s.Model.ApplyLearningSwitch(true);
        Assert.Null(s.Notice);
        s.Begin();
        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);
    }

    [Fact]
    public void A_revision_notice_stands_when_the_watch_runs_its_course_and_goes_when_the_field_was_lost()
    {
        using var s = new Section();
        var id = s.Begin();
        s.Observe(id, "the wifi password is sunshine", "the wifi password is Sunshine42x!");
        Assert.Equal(DictationCopy.CorrectionPending, s.Notice);

        s.End(id, SpanObservation.TerminalSettled, revisions: 1, sawChange: true);

        Assert.Equal(DictationCopy.CorrectionPending, s.Notice);

        var next = s.Begin();
        s.Observe(next, "the wifi password is sunshine", "the wifi password is Sunshine43x!");
        s.End(next, Unobservable, SpanObservation.ReasonAnchorLost, revisions: 1, sawChange: true);
        Assert.Null(s.Notice);
    }

    [Fact]
    public void A_closed_section_lets_go_of_the_watch_and_ignores_what_was_queued()
    {
        using var s = new Section();
        Assert.Equal(3, s.Watch.Listeners);
        s.Watch.Start(Guid.NewGuid());
        var renders = s.View.States.Count;

        s.Model.Dispose();

        Assert.Equal(0, s.Watch.Listeners);
        s.Dispatcher.Drain();
        Assert.Equal(renders, s.View.States.Count);
    }

    // ─── The map ────────────────────────────────────────────────────────

    public static TheoryData<string, string?, string> Endings => new()
    {
        // Could not be read: the Mac's no_focused_field and span_missing.
        { Unobservable, SpanObservation.ReasonNoFocus, DictationCopy.CorrectionFieldUnread },
        { Unobservable, SpanObservation.ReasonNoTextPattern, DictationCopy.CorrectionFieldUnread },
        { Unobservable, SpanObservation.ReasonAnchorAmbiguous, DictationCopy.CorrectionFieldUnread },
        // Moved: span_context_lost.
        { Unobservable, SpanObservation.ReasonAnchorLost, DictationCopy.CorrectionTextMoved },
        // Not checked, on purpose: secure_field and other_application.
        { Unobservable, SpanObservation.ReasonSecure, DictationCopy.CorrectionFieldNotChecked },
        { Unobservable, SpanObservation.ReasonSecureUnknown, DictationCopy.CorrectionFieldNotChecked },
        { Unobservable, SpanObservation.ReasonSelf, DictationCopy.CorrectionFieldNotChecked },
        { Unobservable, SpanObservation.ReasonTerminal, DictationCopy.CorrectionFieldNotChecked },
        { Unobservable, CorrectionWatch.ReasonOtherApplication, DictationCopy.CorrectionFieldNotChecked },
        // Everything else that stopped the observer, the unknown target included.
        { Unobservable, CorrectionWatch.ReasonTargetUnknown, DictationCopy.CorrectionFieldUncheckable },
        { Unobservable, SpanObservation.ReasonDocTooLong, DictationCopy.CorrectionFieldUncheckable },
        { Unobservable, SpanObserverFailures.UiaTimeout, DictationCopy.CorrectionFieldUncheckable },
        { Unobservable, SpanObserverFailures.NoSelection, DictationCopy.CorrectionFieldUncheckable },
        { Unobservable, SpanObserverFailures.Exception, DictationCopy.CorrectionFieldUncheckable },
        { Unobservable, "a reason a newer observer adds", DictationCopy.CorrectionFieldUncheckable },
    };

    [Theory]
    [MemberData(nameof(Endings))]
    public void Each_reason_the_observer_stops_lands_on_the_macs_sentence(string terminal, string? reason, string expected)
    {
        Assert.True(CorrectionNotices.TryForEnding(Ended(terminal, reason), out var notice));
        Assert.Equal(expected, notice);
    }

    [Theory]
    [InlineData(SpanObservation.TerminalSettled)]
    [InlineData(SpanObservation.TerminalNavigated)]
    [InlineData(SpanObservation.TerminalCleared)]
    [InlineData(SpanObservation.TerminalTimeout)]
    public void A_watch_that_ran_its_course_without_a_revision_says_whether_the_text_changed(string terminal)
    {
        Assert.True(CorrectionNotices.TryForEnding(Ended(terminal, null), out var untouched));
        Assert.Equal("No correction detected.", untouched);

        Assert.True(CorrectionNotices.TryForEnding(Ended(terminal, null, sawChange: true), out var changed));
        Assert.Equal("No vocabulary change was learned. You can add the word manually below.", changed);

        // After a revision, that revision's own notice stands.
        Assert.False(CorrectionNotices.TryForEnding(Ended(terminal, null, revisions: 1, sawChange: true), out _));
    }

    [Theory]
    [InlineData(CorrectionWatchEnd.NextDictation, 0)]
    [InlineData(CorrectionWatchEnd.Paused, 0)]
    [InlineData(CorrectionWatchEnd.Closed, 2)]
    [InlineData(CorrectionWatchEnd.SwitchedOff, 1)]
    [InlineData(CorrectionWatchEnd.NoticesCleared, 1)]
    public void A_watch_ended_from_outside_leaves_no_notice(CorrectionWatchEnd why, int revisions)
    {
        Assert.True(CorrectionNotices.TryForEnding(
            Ended(CorrectionWatch.TerminalAbandoned, null, revisions, by: why), out var notice));
        Assert.Null(notice);

        // Whatever the observer itself said on the way out.
        Assert.True(CorrectionNotices.TryForEnding(
            Ended(Unobservable, SpanObservation.ReasonAnchorLost, revisions, by: why), out notice));
        Assert.Null(notice);
    }

    [Fact]
    public void An_observer_that_stopped_after_a_revision_takes_that_revisions_notice_away()
    {
        Assert.True(CorrectionNotices.TryForEnding(Ended(Unobservable, SpanObservation.ReasonAnchorLost, revisions: 2), out var notice));
        Assert.Null(notice);
    }

    public static TheoryData<int, int, int, int, LearningWrite, int, bool, string?> Outcomes => new()
    {
        // Learned and saved: the receipt's, not a notice.
        { 1, 0, 1, 0, LearningWrite.Saved, 1, true, null },
        { 1, 0, 0, 1, LearningWrite.Saved, 0, true, null },
        // Not saved: the unsaved notice says it.
        { 1, 0, 1, 0, LearningWrite.NotSaved, 1, true, null },
        { 1, 0, 0, 0, LearningWrite.NotSaved, 0, true, null },
        // Written, nothing new: pending first, then known, then nothing.
        { 1, 0, 0, 0, LearningWrite.Saved, 1, true, DictationCopy.CorrectionPending },
        { 1, 0, 0, 0, LearningWrite.Saved, 0, true, DictationCopy.CorrectionAlreadyKnown },
        { 0, 1, 0, 0, LearningWrite.Saved, 0, true, DictationCopy.CorrectionNothingLearned },
        // Nothing written: an edit or a rejection taught nothing; no edit, none detected.
        { 0, 1, 0, 0, LearningWrite.Unchanged, 0, true, DictationCopy.CorrectionNothingLearned },
        { 0, 1, 0, 0, LearningWrite.Unchanged, 0, false, DictationCopy.CorrectionNothingLearned },
        { 1, 0, 0, 0, LearningWrite.Unchanged, 1, true, DictationCopy.CorrectionNothingLearned },
        { 0, 0, 0, 0, LearningWrite.Unchanged, 0, false, DictationCopy.CorrectionNotDetected },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Each_learning_outcome_has_the_macs_notice(
        int candidates, int rejections, int words, int aliases, LearningWrite write, int pending, bool edited, string? expected)
    {
        var outcome = new LearningObservation(candidates, rejections, words, aliases, write, pending);

        Assert.Equal(expected, CorrectionNotices.ForOutcome(new CorrectionWatchObserved(Guid.NewGuid(), 1, edited, outcome)));
    }

    private static CorrectionWatchEnded Ended(
        string terminal, string? reason, int revisions = 0, bool sawChange = false, CorrectionWatchEnd? by = null) =>
        new(Guid.NewGuid(), terminal, reason, by, revisions, sawChange);

    // ─── The copy ───────────────────────────────────────────────────────

    public static TheoryData<string> Sentences => new()
    {
        DictationCopy.CorrectionChecking,
        DictationCopy.CorrectionFieldUnread,
        DictationCopy.CorrectionTextMoved,
        DictationCopy.CorrectionFieldNotChecked,
        DictationCopy.CorrectionFieldUncheckable,
        DictationCopy.CorrectionPending,
        DictationCopy.CorrectionAlreadyKnown,
        DictationCopy.CorrectionNothingLearned,
        DictationCopy.CorrectionNotDetected,
    };

    /// <summary>Word for word the Mac's, read from its source: the two
    /// windows change these together or not at all.</summary>
    [Theory]
    [MemberData(nameof(Sentences))]
    public void Every_notice_is_the_macs_sentence_word_for_word(string sentence)
    {
        var mac = File.ReadAllText(Path.Combine(
            DictationOverridesTests.WindowsRoot(), "..", "macos", "Sources", "DictationAppCore", "DictationVocabulary.swift"));

        Assert.Contains($"\"{sentence}\"", mac, StringComparison.Ordinal);
    }
}
