using System.Collections.Concurrent;
using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

/// <summary>
/// What a correction watch tells the rest of the app
/// (<see cref="ICorrectionWatchEvents"/>): that it began, what each settled
/// revision taught, and how it ended — in that order, with counts and the
/// observer's terminal only, through the controller's real watch over the
/// fake field.
/// </summary>
public sealed class CorrectionWatchEventTests
{
    private const string Heard = "trovio";
    private const string Meant = "Trovioo";

    /// <summary>What one watch said, in the order it said it, from whichever
    /// thread said it.</summary>
    private sealed class Recorder
    {
        public ConcurrentQueue<object> Events { get; } = new();

        public void Attach(ICorrectionWatchEvents watch)
        {
            watch.Started += (_, e) => Events.Enqueue(e);
            watch.Observed += (_, e) => Events.Enqueue(e);
            watch.Ended += (_, e) => Events.Enqueue(e);
        }
    }

    /// <summary>A watch outside the controller, on the observer's clock, with
    /// its learning in a fresh store, and the Vocabulary section hearing it as
    /// the window wires it: through a posted queue.</summary>
    private sealed class Standalone : IDisposable
    {
        public Standalone(
            Func<SpanObserverKeyboardSignal, SpanObserverOptions, ISpanObserverTarget> resolve,
            Microsoft.Extensions.Logging.ILogger? log = null,
            Action<CorrectionWatch>? first = null)
        {
            Store = new LearningStore(Learning.LearningGoldenFileTests.TempDirectory());
            var learning = new DictationLearning(Store, new WordListLexicon(Learning.LearningGoldenTests.SharedLexicon));
            Watch = new CorrectionWatch(
                learning, resolve, log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                time: Time, delay: Time.Delay);
            // A listener that must hear each event before everyone else.
            first?.Invoke(Watch);
            Recorder.Attach(Watch);
            Section = new VocabularyModel(learning, View, Ui.Post, Watch);
            Section.Refresh();
        }

        public ObserverTime Time { get; } = new();
        public LearningStore Store { get; }
        public CorrectionWatch Watch { get; }
        public Recorder Recorder { get; } = new();
        public RecordingVocabularyView View { get; } = new();
        public DeferredDispatcher Ui { get; } = new();
        public VocabularyModel Section { get; }

        /// <summary>The section's line, once the UI thread has heard everything.</summary>
        public string? Notice
        {
            get
            {
                Ui.Drain();
                return View.Last.CorrectionNotice;
            }
        }

        public void Finish() => Assert.True(Watch.Completion.Wait(TimeSpan.FromSeconds(10)), "the watch never ended");

        public void Dispose() => Section.Dispose();
    }

    private static readonly FocusedApplicationResult Notepad = FocusedApplicationResult.Resolved("notepad", 42, "Untitled - Notepad");

    private static string ReadLog(string directory)
    {
        using var stream = new FileStream(
            Path.Combine(directory, FileLoggerFactory.FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    private static Rig LearningRig(Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
    {
        var rig = new Rig(logs: logs, learning: true);
        rig.Platform.Worker.Transcript = "we use trovio for the checks";
        rig.Platform.Field.FixAfterEachPaste(Heard, Meant);
        return rig;
    }

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

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

    /// <summary>Dictate, and park the watch's clock with the fix in hand.</summary>
    private static void DictateAndPark(Rig rig)
    {
        rig.Time.Limit = rig.Time.NowMs + 3_500;
        DictateOnce(rig);
        rig.Time.WaitUntilParked();
    }

    [Fact]
    public async Task A_watch_tells_its_start_each_revision_and_its_end_in_order_with_counts_only()
    {
        using var rig = LearningRig();
        var recorder = new Recorder();
        recorder.Attach(rig.Controller.Corrections!);
        await rig.EnableAsync();

        DictateAndWatch(rig);

        var events = recorder.Events.ToArray();
        Assert.Equal(3, events.Length);
        var started = Assert.IsType<CorrectionWatchStarted>(events[0]);
        var observed = Assert.IsType<CorrectionWatchObserved>(events[1]);
        var ended = Assert.IsType<CorrectionWatchEnded>(events[2]);
        Assert.Equal(started.Watch, observed.Watch);
        Assert.Equal(started.Watch, ended.Watch);
        // A saved new word: the outcome carries the undo token the receipt offers.
        Assert.NotNull(observed.Outcome.UndoToken);
        Assert.Equal(
            (1, true, new LearningObservation(1, 0, 1, 0, LearningWrite.Saved, PendingCorrections: 1, observed.Outcome.UndoToken)),
            (observed.Revision, observed.Edited, observed.Outcome));
        Assert.Equal(
            (SpanObservation.TerminalSettled, (string?)null, (CorrectionWatchEnd?)null, 1, true),
            (ended.Terminal, ended.Reason, ended.EndedBy, ended.Revisions, ended.SawChange));

        // The watch's id is the dictation id its learning recorded, and not text.
        Assert.Contains(started.Watch.ToString("N"), File.ReadAllText(rig.LearningFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_watch_ended_by_the_next_dictation_says_so_and_learned_nothing()
    {
        using var rig = LearningRig();
        var recorder = new Recorder();
        recorder.Attach(rig.Controller.Corrections!);
        await rig.EnableAsync();
        DictateAndPark(rig);
        var first = recorder.Events.OfType<CorrectionWatchStarted>().Single().Watch;

        rig.Press();
        rig.Release();
        rig.Time.Release();
        rig.DrainUntil(() => recorder.Events.OfType<CorrectionWatchStarted>().Count() == 2);
        rig.WaitForWatch();

        var ended = Assert.Single(recorder.Events.OfType<CorrectionWatchEnded>(), e => e.Watch == first);
        Assert.Equal(
            (CorrectionWatch.TerminalAbandoned, (string?)null, (CorrectionWatchEnd?)CorrectionWatchEnd.NextDictation, 0),
            (ended.Terminal, ended.Reason, ended.EndedBy, ended.Revisions));
        Assert.DoesNotContain(recorder.Events.OfType<CorrectionWatchObserved>(), e => e.Watch == first);
    }

    [Fact]
    public async Task A_listener_that_throws_costs_neither_the_watch_nor_the_listeners_after_it()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var rig = LearningRig(logs);
        var watch = rig.Controller.Corrections!;
        watch.Started += (_, _) => throw new InvalidOperationException("a listener with a bug");
        watch.Observed += (_, _) => throw new InvalidOperationException("a listener with a bug");
        watch.Ended += (_, _) => throw new InvalidOperationException("a listener with a bug");
        var recorder = new Recorder();
        recorder.Attach(watch);
        await rig.EnableAsync();

        DictateAndWatch(rig);

        Assert.True(watch.Completion.IsCompletedSuccessfully);
        Assert.Equal(3, recorder.Events.Count);
        Assert.Equal(Meant, Assert.Single(new LearningStore(rig.LearningDirectory).Load().State.Words).Text);
        var log = ReadLog(dir);
        Assert.Contains("A correction watch listener failed (InvalidOperationException)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("a listener with a bug", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A revision that puts the pasted words back is handed to learning (it
    /// withdraws what the earlier one taught) but is not an edit: "No
    /// correction detected", where the earlier rewording taught nothing. A
    /// paste that spans two lines is compared as the observer reports the
    /// span, with CRLF read as LF, so putting it back is not an edit either.
    /// </summary>
    [Theory]
    [InlineData("we have a big meeting")]
    [InlineData("we have a big meeting\r\nabout the launch")]
    public void A_revision_that_puts_the_pasted_text_back_is_not_an_edit(string pasted)
    {
        FakeField? field = null;
        using var s = new Standalone((keyboard, _) =>
        {
            field!.Keyboard = keyboard;
            return field;
        });
        field = new FakeField(s.Time, () => s.Watch.Keyboard.Record);
        field.FixAfterEachPaste("big", "large");
        field.Paste(pasted);
        // Eight seconds in, after "large" has settled: the user types the
        // original word back.
        s.Time.When(s.Time.NowMs + 8_000, () =>
        {
            field.Fix = null;
            s.Watch.Keyboard.Record(s.Time.NowMs, field.Identity.ProcessId);
            field.Paste(pasted);
        });

        s.Watch.Begin(pasted, Notepad);

        s.Finish();
        var revisions = s.Recorder.Events.OfType<CorrectionWatchObserved>().ToArray();
        Assert.Equal([(1, true), (2, false)], revisions.Select(r => (r.Revision, r.Edited)));
        Assert.Equal(DictationCopy.CorrectionNothingLearned, CorrectionNotices.ForOutcome(revisions[0]));
        Assert.Equal(DictationCopy.CorrectionNotDetected, CorrectionNotices.ForOutcome(revisions[1]));
        Assert.Equal(2, s.Recorder.Events.OfType<CorrectionWatchEnded>().Single().Revisions);
    }

    /// <summary>
    /// The inserter refuses a window whose process it cannot name, so a
    /// delivered dictation has always had a named one; the watch does not
    /// rely on that. Given a paste into an unnamed process it neither
    /// resolves nor reads a field, as the Mac does for an unknown target app.
    /// </summary>
    [Fact]
    public void A_paste_into_an_application_nobody_could_name_is_neither_resolved_nor_read()
    {
        var resolved = 0;
        using var s = new Standalone((_, _) =>
        {
            Interlocked.Increment(ref resolved);
            throw new InvalidOperationException("resolved a field it could not have shown to be the pasted one");
        });

        s.Watch.Begin("we use trovio", FocusedApplicationResult.Unresolvable("Untitled - Notepad"));

        s.Finish();
        Assert.Equal(0, resolved);
        var ended = Assert.IsType<CorrectionWatchEnded>(s.Recorder.Events.Last());
        Assert.Equal("unobservable:target_unknown", ended.Label);
        Assert.False(File.Exists(s.Store.FilePath));
    }

    // ─── Order, and a watch that fails ─────────────────────────────────

    public static TheoryData<string, string> EndsAtOnce => new()
    {
        { "an application nobody could name", DictationCopy.CorrectionFieldUncheckable },
        { "another application", DictationCopy.CorrectionFieldNotChecked },
    };

    /// <summary>
    /// Started is told before the watch is even scheduled, so a watch that
    /// ends at once is still heard to start first — even by a Started listener
    /// as slow as can be, one that waits for the watch to finish. Heard the
    /// other way round, the section's id fence would drop the end and leave
    /// "Checking…" on screen until the next watch.
    /// </summary>
    [Theory]
    [MemberData(nameof(EndsAtOnce))]
    public void A_watch_that_ends_at_once_is_heard_to_start_first(string pastedInto, string notice)
    {
        FakeField? field = null;
        using var s = new Standalone(
            (_, _) => field!,
            first: watch => watch.Started += (_, _) => watch.Completion.Wait(TimeSpan.FromSeconds(10)));
        field = new FakeField(s.Time, () => null)
        {
            Identity = new SpanObserverTargetIdentity("explorer", 4_243, "Edit", 9, false),
        };
        var into = pastedInto == "another application" ? Notepad : FocusedApplicationResult.Unresolvable("Untitled - Notepad");

        for (var dictation = 0; dictation < 2; dictation++)
        {
            s.Watch.Begin("we use trovio", into);
            s.Finish();
        }

        var events = s.Recorder.Events.ToArray();
        Assert.Equal(4, events.Length);
        for (var i = 0; i < events.Length; i += 2)
        {
            var started = Assert.IsType<CorrectionWatchStarted>(events[i]);
            Assert.Equal(started.Watch, Assert.IsType<CorrectionWatchEnded>(events[i + 1]).Watch);
        }

        Assert.Equal(notice, s.Notice);
    }

    /// <summary>
    /// A UI Automation call that throws ends the watch as unobservable, logs
    /// the exception's type and never its message, and leaves the section
    /// saying the field could not be checked — not "Checking…" for good.
    /// </summary>
    [Fact]
    public void A_watch_whose_field_throws_ends_unobservable_and_logs_only_the_type()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var s = new Standalone(
            (_, _) => throw new InvalidOperationException($"could not read '{Heard}' in the field"),
            logs.CreateLogger<CorrectionWatch>());

        s.Watch.Begin($"we use {Heard}", Notepad);

        s.Finish();
        Assert.IsType<CorrectionWatchStarted>(s.Recorder.Events.First());
        var ended = Assert.Single(s.Recorder.Events.OfType<CorrectionWatchEnded>());
        Assert.Equal(
            ("unobservable:exception", (CorrectionWatchEnd?)null, 0, false),
            (ended.Label, ended.EndedBy, ended.Revisions, ended.SawChange));
        Assert.Equal(DictationCopy.CorrectionFieldUncheckable, s.Notice);
        var log = ReadLog(dir);
        Assert.Contains("Correction watch ended: failed (InvalidOperationException)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Heard, log, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(s.Store.FilePath));
    }

    /// <summary>
    /// An outside end that lands while the field is failing still reports
    /// the watch as ended from outside, so the section shows nothing, as for
    /// any watch the user moved on from — not "could not be checked".
    /// </summary>
    [Fact]
    public void A_watch_ended_from_outside_while_its_field_throws_leaves_no_notice()
    {
        var resolving = new ManualResetEventSlim();
        var fail = new ManualResetEventSlim();
        using var s = new Standalone((_, _) =>
        {
            resolving.Set();
            fail.Wait(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("the field went away");
        });

        s.Watch.Begin("we use trovio", Notepad);
        Assert.True(resolving.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(DictationCopy.CorrectionChecking, s.Notice);
        s.Watch.End(CorrectionWatchEnd.NextDictation);
        fail.Set();

        s.Finish();
        var ended = Assert.Single(s.Recorder.Events.OfType<CorrectionWatchEnded>());
        Assert.Equal(
            (SpanObservation.TerminalUnobservable, SpanObserverFailures.Exception, (CorrectionWatchEnd?)CorrectionWatchEnd.NextDictation),
            (ended.Terminal, ended.Reason, ended.EndedBy));
        Assert.Null(s.Notice);
    }
}
