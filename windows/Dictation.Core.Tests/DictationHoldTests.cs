using System.Diagnostics;
using Dictation.Services;

namespace Dictation.Core.Tests;

/// <summary>
/// One hold, start to end: the window's Cancel button, a shortcut that ends a
/// hold, a password field refused before the microphone opens, and the
/// recording limit. The silence stops and the rest of the hold policy are
/// HoldPolicyTests.
/// </summary>
public sealed class DictationHoldTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static int Transcribes(Rig rig) => rig.Platform.Worker.Ops().Count(op => op == "transcribe");

    // ─── Cancel ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_while_listening_discards_the_recording_and_the_release_starts_nothing()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        Assert.False(rig.View.Last.CanCancel);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.True(rig.View.Last.CanCancel);

        rig.Controller.Cancel();
        rig.Dispatcher.Drain();

        Assert.False(rig.Platform.Microphone.Open);
        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
        Assert.False(rig.View.Last.CanCancel);

        // The key was still down when the button was clicked; letting go of
        // it belongs to the hold that just ended.
        rig.Release();
        rig.Dispatcher.Drain();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();
        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
        Assert.Equal(0, Transcribes(rig));
        Assert.Equal(0, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task Cancel_while_transcribing_inserts_nothing()
    {
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.True(rig.View.Last.CanCancel);

        rig.Controller.Cancel();
        hold.Set();
        Thread.Sleep(100);
        rig.Dispatcher.Drain();

        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task A_cancelled_transcription_brings_the_worker_back_without_a_word()
    {
        // Withdrawing a decode stops the worker process; the next hold must
        // not pay a cold start, and the window must not call it a failure.
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => Transcribes(rig) == 1);

        rig.Controller.Cancel();
        hold.Set();
        rig.TickUntil(() => rig.Platform.Worker.Starts == 2 && rig.Platform.Worker.IsAlive);
        rig.Platform.Worker.HoldTranscribe = null;

        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped", StringComparison.Ordinal));
        rig.Dictate(Inserted);
    }

    [Fact]
    public async Task Cancelling_transcriptions_again_and_again_is_never_reported_as_a_failure()
    {
        // Each withdrawn decode stops the worker; those restarts are the
        // user's doing and must not use up the budget kept for crashes.
        using var rig = new Rig();
        await rig.EnableAsync();

        for (var cancel = 1; cancel <= 5; cancel++)
        {
            using var hold = new ManualResetEventSlim();
            rig.Platform.Worker.HoldTranscribe = hold;
            rig.Press();
            rig.Release();
            // On the wire, so the cancel really withdraws a decode.
            rig.DrainUntil(() => Transcribes(rig) == cancel);
            rig.Controller.Cancel();
            hold.Set();
            rig.TickUntil(() => rig.Platform.Worker.Starts == cancel + 1 && rig.Platform.Worker.IsAlive);
        }

        Assert.DoesNotContain(rig.View.States, s => s.Status == DictationCopy.WorkerRestartFailed);
        rig.Platform.Worker.HoldTranscribe = null;
        rig.Dictate(Inserted);
    }

    [Fact]
    public async Task Cancel_is_not_offered_while_the_text_goes_in()
    {
        using var rig = new Rig();
        using var pasteHold = new ManualResetEventSlim();
        rig.Platform.Paste.Hold = pasteHold;
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Inserting);

        Assert.False(rig.View.Last.CanCancel);
        rig.Controller.Cancel();
        pasteHold.Set();

        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    // ─── A shortcut ends the hold ───────────────────────────────────────

    [Fact]
    public async Task A_shortcut_names_the_key_that_was_used()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        // Right Ctrl+C: the hook latches a chord cancel and reads the hold as over.
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Equal(
            "Cancelled because Right Ctrl was used in a shortcut. Nothing was inserted.",
            rig.View.Last.Status);
        Assert.Equal(0, rig.Platform.Paste.Count);
    }

    [Theory]
    [InlineData(TriggerCancel.Chord)]
    [InlineData(TriggerCancel.Escape)]
    public async Task A_cancel_latched_while_the_text_goes_in_changes_nothing(TriggerCancel cancel)
    {
        // Once the paste has begun it can no longer be cancelled. A chord the
        // hook latched now — the paste's own Ctrl+V, with the key still held
        // after the limit's stop — used to abandon the session: the words
        // landed anyway, under "Cancelled because Right Ctrl was used in a
        // shortcut", with no result and no clipboard wait at quit.
        using var rig = new Rig();
        using var pasteHold = new ManualResetEventSlim();
        rig.Platform.Paste.Hold = pasteHold;
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Inserting);

        rig.Platform.Trigger.PendingCancel = cancel;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.Inserting, rig.View.Last.Status);
        Assert.Equal(DictationCloseAction.Wait, rig.Controller.RequestClose(quit: true).Action);

        pasteHold.Set();
        rig.DrainUntil(() => rig.View.QuitRequests == 1 && Inserted(rig.View.Last));
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.DoesNotContain(rig.View.States, s => s.Status.StartsWith("Cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Escape_keeps_the_plain_sentence()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
    }

    [Fact]
    public async Task A_shortcut_before_the_press_was_admitted_says_nothing()
    {
        // Right Ctrl+C typed fast: the chord lands while the field check is
        // out, so no dictation ever started and there is nothing to report.
        using var rig = new Rig(options: o => o with { RunFieldCheck = check => Task.Run(check) });
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();
        var before = rig.View.Last.Status;

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        gate.Set();
        rig.TickUntil(() => rig.Dispatcher.Pending == 0 && rig.Platform.SecureField.Calls == 1);
        Thread.Sleep(50);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.False(rig.Platform.Microphone.Open);
        Assert.Equal(before, rig.View.Last.Status);
    }

    [Fact]
    public async Task A_shortcut_while_the_field_check_is_out_still_withdraws_the_press()
    {
        // Nothing is listening yet, but the press is: a chord here is the
        // user's shortcut, and the check answering afterwards must not open
        // the microphone even if the key still reads down.
        using var rig = new Rig(options: o => o with { RunFieldCheck = check => Task.Run(check) });
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Controller.Tick();
        gate.Set();
        rig.TickUntil(() => rig.Dispatcher.Pending == 0 && rig.Platform.SecureField.Calls == 1);
        Thread.Sleep(50);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.Platform.Microphone.Starts);
    }

    // ─── Password fields ────────────────────────────────────────────────

    [Fact]
    public async Task A_password_field_refuses_the_press_before_the_microphone_opens()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.SecureField.Next = SecureFieldState.Secure;

        rig.Press();
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.Platform.Microphone.Starts);
        Assert.Equal("Earlet does not work in password fields. Nothing was recorded.", rig.View.Last.Status);
        Assert.False(rig.Platform.Trigger.DictationInFlight);

        rig.Release();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.SecureFieldAtPress, rig.View.Last.Status);
        Assert.Equal(0, Transcribes(rig));
        Assert.Empty(rig.Platform.Clipboard.Writes);
    }

    [Fact]
    public async Task The_field_check_runs_off_the_thread_that_polls_the_key()
    {
        // The shipped runner. A target slow to answer UI Automation must not
        // stall the dispatcher that watches the hold key.
        using var rig = new Rig(options: o => o with { RunFieldCheck = new DictationControllerOptions().RunFieldCheck });
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        var tick = Stopwatch.StartNew();
        rig.Controller.Tick();
        tick.Stop();

        Assert.True(tick.Elapsed < TimeSpan.FromSeconds(2), $"the tick waited {tick.Elapsed}");
        Assert.False(rig.Platform.Microphone.Open);

        gate.Set();
        rig.DrainUntil(() => rig.Platform.Microphone.Open);
        Assert.NotEqual(Environment.CurrentManagedThreadId, rig.Platform.SecureField.ThreadId);
    }

    [Fact]
    public async Task A_check_that_answers_after_the_release_records_nothing()
    {
        using var rig = new Rig(options: o => o with { RunFieldCheck = check => Task.Run(check) });
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        gate.Set();
        Thread.Sleep(100);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.Platform.Microphone.Starts);
        Assert.Equal(0, Transcribes(rig));
    }

    [Fact]
    public async Task A_probe_that_cannot_answer_lets_the_press_through()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.SecureField.Throws = true;

        rig.Dictate(Inserted);
    }

    [Fact]
    public async Task Focus_moving_into_a_password_field_mid_hold_keeps_nothing()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        Assert.True(rig.Platform.Microphone.Open);

        rig.Platform.SecureField.Next = SecureFieldState.Secure;
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtRelease);

        Assert.DoesNotContain("recorded", rig.View.Last.Status, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Clipboard.Writes);
        Assert.Equal(0, Transcribes(rig));
    }

    // ─── Back to back ───────────────────────────────────────────────────

    /// <summary>The user holds the key again while the last dictation is
    /// still going in, and the next poll sees it.</summary>
    private static void PressAgain(Rig rig)
    {
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
    }

    [Fact]
    public async Task A_press_while_the_last_dictation_is_transcribing_is_refused_and_the_first_still_lands()
    {
        // Dictating sentence by sentence: the next hold begins while the
        // first is still being decoded. Admitted, it superseded the first,
        // whose words were then dropped without a word on screen.
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);

        PressAgain(rig);

        Assert.Equal(DictationCopy.StillTypingLastDictation(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Microphone.Starts);
        Assert.False(rig.Platform.Microphone.Open);

        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Equal(1, Transcribes(rig));

        // Letting go of the refused hold starts nothing, and the next press
        // is an ordinary dictation.
        rig.Release();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();
        Assert.Equal(1, Transcribes(rig));
        Assert.True(Inserted(rig.View.Last));
        rig.Platform.Worker.HoldTranscribe = null;
        var before = rig.View.States.Count;
        rig.Dictate(_ => rig.View.States.Skip(before).Any(Inserted));
        Assert.Equal(2, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_shortcut_made_on_a_refused_press_leaves_the_last_dictation_alone()
    {
        // Right Ctrl, refused while the last sentence decodes, then Right
        // Ctrl+C. The hook latches a chord cancel for any key that goes down
        // under the held trigger; honoured, it threw away the very dictation
        // the refusal was protecting and said "that was a shortcut".
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        PressAgain(rig);

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Equal(1, Transcribes(rig));
        Assert.DoesNotContain(rig.View.States, s => s.Status.StartsWith("Cancelled", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Hud.Shows, m => m.Text == DictationCopy.HudCancelledByShortcut);
    }

    [Fact]
    public async Task Escape_on_a_refused_press_still_cancels_the_last_dictation()
    {
        // The refusal guards against the hook's guess, not against the user:
        // Escape is a request to cancel whichever hold it lands on.
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        PressAgain(rig);

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
        hold.Set();
        rig.Release();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_press_while_the_last_dictation_is_being_typed_keeps_its_result()
    {
        // During the paste the words land either way; what a superseding
        // press lost was the result: no "Text inserted", no correction watch,
        // and a status line stuck on "Inserting…".
        using var rig = new Rig();
        using var pasteHold = new ManualResetEventSlim();
        rig.Platform.Paste.Hold = pasteHold;
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Inserting);

        PressAgain(rig);
        Assert.Equal(DictationCopy.StillTypingLastDictation(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Microphone.Starts);

        pasteHold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task A_refused_press_does_not_cost_the_last_dictation_its_correction_watch()
    {
        // The key is down when the result arrives, which ordinarily means the
        // next dictation has begun and no watch should start. A refused
        // press records nothing, so the watch over the words that landed runs.
        using var rig = new Rig(learning: true);
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        rig.Time.Limit = rig.Time.NowMs + 3_500;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);

        PressAgain(rig);
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        rig.Time.WaitUntilParked();
        Assert.True(rig.Controller.Corrections!.IsWatching);
    }

    // ─── The recording limit ────────────────────────────────────────────

    private static readonly DictationHoldEnding AtTheLimit = new(DictationHoldStop.Ceiling, DictationHoldPolicy.Windows.Ceiling);

    private static string LimitReached(Rig rig) => DictationCopy.StoppedTranscribing(AtTheLimit, rig.Controller.HoldKey);

    private static string InsertedAtTheLimit(Rig rig) => DictationCopy.InsertedAfterStop(AtTheLimit, rig.Controller.HoldKey);

    /// <summary>A rig for a test that waits for the limit with the key held
    /// and nobody speaking: without silence stops, which would end the hold
    /// at a minute (HoldPolicyTests).</summary>
    private static Rig ForTheLimit() => new(options: o => o with { HoldPolicy = o.HoldPolicy.WithoutSilenceStops() });

    [Fact]
    public async Task At_the_limit_the_hold_is_transcribed_and_inserted_without_a_release()
    {
        using var rig = ForTheLimit();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        rig.Clock.Advance(TimeSpan.FromSeconds(299));
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        Assert.True(rig.Platform.Microphone.Open);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Controller.Tick();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Contains(rig.View.States, s => s.Status == LimitReached(rig));
        Assert.Equal("Five minutes is the longest recording, so it stopped there. Transcribing…", LimitReached(rig));
        Assert.Equal(
            "Text inserted. A recording stops at five minutes; hold Right Ctrl again to continue.",
            rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);

        // The key is still down: nothing reopens until it comes up.
        for (var i = 0; i < 5; i++)
        {
            rig.Controller.Tick();
        }

        rig.Dispatcher.Drain();
        Assert.Single(rig.Platform.Microphones);
        Assert.False(rig.Platform.Microphone.Open);

        rig.Release();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();
        Assert.Equal(1, Transcribes(rig));

        // And the next press is an ordinary dictation.
        rig.Dictate(v => Inserted(v) && v.Status != InsertedAtTheLimit(rig));
        Assert.Equal(2, Transcribes(rig));
    }

    [Fact]
    public async Task A_shortcut_after_the_limit_s_stop_does_not_cancel_what_was_captured()
    {
        // The limit ended the hold, but the key is still down: another key
        // now is a shortcut in the other app, not a sign that five minutes of
        // speech were never a dictation.
        using var rig = ForTheLimit();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Clock.Advance(TimeSpan.FromSeconds(300));
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == LimitReached(rig));

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Equal(InsertedAtTheLimit(rig), rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.DoesNotContain(rig.View.States, s => s.Status.StartsWith("Cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_hold_is_not_logged_as_a_microphone_that_never_opened()
    {
        // The release after a cancel used to land in the tap's "no capture
        // running" branch and report the aborted hold as "the endpoint never
        // opened for this hold" — a device failure that never happened,
        // written over a deliberate cancel.
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var rig = new Rig(logs: logs);
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Controller.Cancel();
        rig.Release();
        rig.Dispatcher.Drain();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();

        var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.Contains("Capture aborted", log, StringComparison.Ordinal);
        Assert.DoesNotContain("never opened", log, StringComparison.Ordinal);

        // A microphone that really never opened still says so.
        rig.Platform.Microphone.StartSucceeds = false;
        rig.Press();
        rig.Release();
        rig.Dispatcher.Drain();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();

        log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.Contains("never opened", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_hold_is_not_later_counted_against_the_limit()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var rig = new Rig(logs: logs);
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Controller.Cancel();

        // Still holding the key long after the cancel: past the limit and
        // past every silence stop.
        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        rig.Controller.Tick();
        rig.Release();
        rig.Dispatcher.Drain();

        rig.Dictate(Inserted);
        Assert.DoesNotContain(rig.View.States, s => s.Status == LimitReached(rig));
        var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.DoesNotContain("hold reached", log, StringComparison.Ordinal);
        Assert.DoesNotContain("without speech", log, StringComparison.Ordinal);
    }
}
