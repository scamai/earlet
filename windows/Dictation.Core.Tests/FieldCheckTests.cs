using Dictation.Services;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationAttemptWiringTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The password-field check's two edges, on the controller the app runs: a
/// password field found just before the paste (the Mac's refusal at the
/// paste: ClipboardFallbackTests <c>secureTargetsKeepNothing</c>,
/// RecentResultTests <c>secureTargets</c>, the <c>.insert(.secureFieldFocused)</c>
/// ending of DictationHUDTests, AttemptDiagnosticsTests' receipt), and Escape
/// before the press-time check answers (WarmCaptureTests
/// <c>escapeDuringSlowStart</c>: Windows waits for the check where the Mac
/// waits for its microphone to warm up).
/// </summary>
public sealed class FieldCheckTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static int Transcribes(Rig rig) => rig.Platform.Worker.Ops().Count(op => op == "transcribe");

    /// <summary>
    /// One dictation whose field turns into a password field while its words
    /// are transcribed: the press and the release see an ordinary field, the
    /// check just before the paste a password field.
    /// </summary>
    private static void DictateIntoAFieldThatTurnsSecure(Rig rig)
    {
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        rig.Platform.SecureField.Next = SecureFieldState.Secure;
        decode.Set();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtPaste);
        rig.Platform.Worker.HoldTranscribe = null;
        rig.Platform.SecureField.Next = SecureFieldState.NotSecure;
    }

    // ─── A password field at the paste ──────────────────────────────────

    /// <summary>Mac <c>secureTargetsKeepNothing</c>, for the password field:
    /// the clipboard stays empty and the status says nothing was kept.</summary>
    [Fact]
    public async Task A_field_that_becomes_a_password_field_while_transcribing_is_not_typed_or_kept()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        DictateIntoAFieldThatTurnsSecure(rig);

        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Empty(rig.Platform.Clipboard.Writes);
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
        Assert.Contains("Nothing was", rig.View.Last.Status, StringComparison.Ordinal);
        // The recogniser ran, and its words went nowhere: no section, no menu
        // item, no "Your words are still here".
        Assert.Equal(1, Transcribes(rig));
        Assert.Null(rig.View.Last.Recent);
        Assert.Null(rig.View.Last.Tray.Recent);
        // Asked at the press, the release and the paste.
        Assert.Equal(3, rig.Platform.SecureField.Calls);
        // The Mac's .passwordField ending: the release's line says the same.
        Assert.Equal(DictationCopy.HudPasswordFieldAtRelease, rig.Hud.Current?.Text);
        Assert.Equal(HudTone.Caution, rig.Hud.Current?.Tone);
        Assert.True(rig.View.Last.PracticeStartsExpanded, "a refused dictation is no first success");
    }

    /// <summary>Mac <c>secureTargets</c> (RecentResultTests), for the
    /// password field at the paste: the held text is discarded too.</summary>
    [Fact]
    public async Task Secure_targets_at_the_paste_discard_the_current_recovery_buffer()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.NotNull(rig.View.Last.Recent);
        var writes = rig.Platform.Clipboard.Writes.Count;

        DictateIntoAFieldThatTurnsSecure(rig);

        Assert.Null(rig.View.Last.Recent);
        Assert.Null(rig.View.Last.Tray.Recent);
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(writes, rig.Platform.Clipboard.Writes.Count);
    }

    /// <summary>The Mac's receipt for its refusal at the paste: one ending,
    /// <c>result=failed stage=paste reason=secure_field</c> with the check's
    /// time as <c>paste_ms</c>, then the recovery
    /// line saying the words are on the clipboard nowhere and in Dictation
    /// nowhere; and no line names them.</summary>
    [Fact]
    public async Task The_receipt_names_the_paste_stage_and_keeps_the_words_nowhere()
    {
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        rig.Platform.Worker.Transcript = Words;
        await rig.EnableAsync();

        DictateIntoAFieldThatTurnsSecure(rig);
        AwaitEndings(rig, log);

        var ending = Endings(log).Single();
        Assert.Contains("event=finished result=failed stage=paste reason=secure_field", ending);
        // The Mac's paste_ms on a refused insertion is its gate check's time.
        Assert.Matches(@"paste_ms=\d", ending);
        Assert.Contains(log.Receipts, l => l.EndsWith("event=recovery clipboard=false available_in_app=false", StringComparison.Ordinal));
        AssertWellFormed(log);
    }

    [Theory]
    [InlineData(false, "result=failed stage=capture reason=secure_field")]
    [InlineData(true, "result=failed stage=paste reason=secure_field")]
    public void A_password_field_s_receipt_names_the_stage_it_was_found_at(bool atPaste, string ending) =>
        Assert.Equal(ending, DictationAttemptReceipt.For(
            new DictationSessionResult(DictationOutcome.SecureField, null, null, SecureFieldAtPaste: atPaste)));

    [Theory]
    [InlineData(false, DictationCopy.SecureFieldAtRelease)]
    [InlineData(true, DictationCopy.SecureFieldAtPaste)]
    public void A_password_field_s_sentence_says_what_was_not_done_there(bool atPaste, string sentence) =>
        Assert.Equal(sentence, DictationCopy.ForResult(
            new DictationSessionResult(DictationOutcome.SecureField, null, null, SecureFieldAtPaste: atPaste),
            DictationHotkeys.HoldChoices[0]).Text);

    [Fact]
    public async Task Escape_while_the_field_is_checked_at_the_paste_types_nothing()
    {
        // The check is awaited while the dictation still reads Transcribing,
        // so Escape still cancels it; the answer that lands afterwards must
        // not paste.
        var answer = new TaskCompletionSource<SecureFieldState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        var log = new MemoryLog();
        using var rig = new Rig(logs: log, options: o => o with
        {
            RunPasteFieldCheck = _ =>
            {
                Interlocked.Increment(ref asked);
                return answer.Task;
            },
        });
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref asked) == 1, TimeSpan.FromSeconds(10)), "the paste check never ran");
        rig.Dispatcher.Drain();
        Assert.True(rig.View.Last.CanCancel);

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
        answer.SetResult(SecureFieldState.NotSecure);
        log.AwaitRaised();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Empty(rig.Platform.Clipboard.Writes);
        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
        Assert.Null(rig.View.Last.Recent);
    }

    // ─── The worker around the check at the paste ───────────────────────

    /// <summary>Windows' own bookkeeping, which the Mac does not keep: the words came back
    /// before the check refused the paste, so the recogniser answered, and a
    /// restarted worker counts as healthy again, as after any dictation it
    /// decoded. Without that, four crashes with a refused paste between each
    /// would spend the three restarts kept for a worker that keeps
    /// dying.</summary>
    [Fact]
    public async Task A_refusal_at_the_paste_renews_the_restart_budget()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        for (var crash = 1; crash <= 4; crash++)
        {
            rig.Platform.Worker.Crash();
            rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));
            DictateIntoAFieldThatTurnsSecure(rig);
        }

        Assert.Equal(5, rig.Platform.CreateWorkerCalls);
    }

    /// <summary>Escape while the check at the paste runs withdraws no decode:
    /// the words are back and the worker keeps running, so a death after it
    /// is a crash to report, not the user's cancel restarted
    /// quietly.</summary>
    [Fact]
    public async Task Escape_during_the_check_at_the_paste_leaves_a_later_crash_reported()
    {
        var answer = new TaskCompletionSource<SecureFieldState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        using var rig = new Rig(options: o => o with
        {
            RunPasteFieldCheck = _ =>
            {
                Interlocked.Increment(ref asked);
                return answer.Task;
            },
        });
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref asked) == 1, TimeSpan.FromSeconds(10)), "the paste check never ran");
        rig.Dispatcher.Drain();

        rig.Controller.Cancel();
        rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
        answer.SetResult(SecureFieldState.NotSecure);
        Assert.True(rig.Platform.Worker.IsAlive, "no decode was withdrawn");

        rig.Platform.Worker.Crash();
        rig.Controller.Tick();

        Assert.Equal(DictationCopy.WorkerStoppedWhileIdle, rig.View.Last.Status);
    }

    [Fact]
    public async Task A_check_at_the_paste_that_cannot_run_lets_the_words_through()
    {
        // Fail open, as at the press and the release: a guard that cannot
        // answer must never be what stops dictation.
        using var rig = new Rig(options: o => o with
        {
            RunPasteFieldCheck = _ => Task.FromException<SecureFieldState>(new InvalidOperationException("UIA is having a day")),
        });
        await rig.EnableAsync();

        rig.Dictate(Inserted);

        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task The_check_at_the_paste_runs_off_the_thread_that_asks()
    {
        // The shipped runner. In the app the session resumes on the
        // dispatcher, which also runs the keyboard hook: a target slow to
        // answer UI Automation must not stall it.
        var caller = Environment.CurrentManagedThreadId;
        var probedOn = caller;

        var answer = new DictationControllerOptions().RunPasteFieldCheck(() =>
        {
            probedOn = Environment.CurrentManagedThreadId;
            return SecureFieldState.Secure;
        });

        Assert.Equal(SecureFieldState.Secure, await answer.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(caller, probedOn);
    }

    // ─── Escape before the press-time check answers ─────────────────────

    /// <summary>Mac <c>escapeDuringSlowStart</c>: "Escape during a slow start
    /// discards the recording once capture is up". Windows' slow start is
    /// the press-time check, and nothing has opened yet: the press is
    /// withdrawn at once, the microphone never opens, and the window and the
    /// indicator say Cancelled. With the key still held, the hook already
    /// heard Escape as a key pressed with the hold key; with the key up (a
    /// quick tap waiting out the pair window, or a double-tap that latched
    /// while the first press's check was out), only the arm made at the press
    /// lets it hear Escape at all.</summary>
    [Theory]
    [InlineData("held")]
    [InlineData("tapped")]
    [InlineData("latched")]
    public async Task Escape_during_a_slow_field_check_cancels_the_press(string how)
    {
        var log = new MemoryLog();
        using var hands = new HandsFree(checks: true, logs: log);
        await hands.EnableAsync();

        hands.Press();
        if (how != "held")
        {
            hands.Advance(60);
            hands.Release();
        }

        if (how == "latched")
        {
            hands.Advance(100);
            hands.Press();
            hands.Release();
            Assert.True(hands.Latched, "the double-tap latched while the check was out");
        }

        Assert.True(hands.Key.IsKeyDown(), "the hold is still open");
        Assert.False(hands.Listening);
        hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
        hands.Event(HoldKeyState.VkEscape, HandsFree.Up);
        if (how == "held")
        {
            hands.Release();
        }

        hands.Checks.Answer();
        hands.Wait(500);

        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);
        Assert.False(hands.Latched);
        Assert.Equal("Cancelled. Nothing was inserted.", hands.Status);
        Assert.Contains(hands.Rig.Hud.Shows, m => m.Text == "Cancelled");
        Assert.Equal(0, hands.Transcribes);
        Assert.Equal(0, hands.Pastes);
        var ending = Endings(log).Single();
        Assert.Contains("event=finished result=cancelled", ending);
        // As on the Mac: no release, the hold never reached capture.
        Assert.EndsWith("release_ms=n/a", ending, StringComparison.Ordinal);
        AssertWellFormed(log);
    }

    /// <summary>The same Escape, with the check's answer run before the next
    /// poll reads the cancel the hook latched. In WPF the answer is posted at
    /// Normal priority and the 30 ms poll is a Background-priority timer, so
    /// on a real machine the answer can come first; Escape has already taken
    /// the key up in the hook. The answer reads that cancel before it acts,
    /// so the press still ends Cancelled, not as a key released before
    /// capture.</summary>
    [Theory]
    [InlineData("held")]
    [InlineData("tapped")]
    [InlineData("latched")]
    public async Task Escape_before_the_check_answers_cancels_when_the_answer_runs_before_the_poll(string how)
    {
        var log = new MemoryLog();
        using var hands = new HandsFree(checks: true, logs: log);
        await hands.EnableAsync();
        var before = hands.Status;

        hands.Press();
        if (how != "held")
        {
            hands.Advance(60);
            hands.Release();
        }

        if (how == "latched")
        {
            hands.Advance(100);
            hands.Press();
            hands.Release();
            Assert.True(hands.Latched, "the double-tap latched while the check was out");
        }

        // Through the hook alone: no poll reads the cancel it latches.
        hands.Key.KeyEvent(HoldKeyState.VkEscape, HandsFree.Down);
        hands.Key.KeyEvent(HoldKeyState.VkEscape, HandsFree.Up);
        hands.Checks.Answer();
        hands.Rig.Dispatcher.Drain();

        // Settled by the answer, before any poll.
        Assert.NotEqual(before, hands.Status);
        Assert.Equal("Cancelled. Nothing was inserted.", hands.Status);
        if (how == "held")
        {
            hands.Release();
        }

        hands.Wait(500);

        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);
        Assert.False(hands.Latched);
        Assert.False(hands.Key.DictationInFlight, "Escape is inert again");
        Assert.Equal("Cancelled. Nothing was inserted.", hands.Status);
        Assert.Contains(hands.Rig.Hud.Shows, m => m.Text == "Cancelled");
        var ending = Endings(log).Single();
        Assert.Contains("event=finished result=cancelled", ending);
        Assert.EndsWith("release_ms=n/a", ending, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Receipts, l => l.Contains("event=released", StringComparison.Ordinal));
        AssertWellFormed(log);
    }

    /// <summary>The answer after an unread Escape, on a trigger that still
    /// reads down (set by hand here; the hook's own gesture lets the key go
    /// at Escape): the press Escape withdrew is not admitted, and the
    /// microphone never opens.</summary>
    [Fact]
    public async Task An_answer_after_an_unread_Escape_admits_nothing_with_the_key_still_down()
    {
        var checks = new FieldChecks();
        using var rig = new Rig(options: o => o with { RunFieldCheck = checks.Run });
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        checks.Answer();
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.Platform.Microphone.Starts);
        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
        rig.Controller.Tick();
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        Assert.Equal(0, rig.Platform.Microphone.Starts);
        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
    }

    /// <summary>A shortcut made with the hold key while the check is out,
    /// with the answer run first: the receipt names the shortcut, not a key
    /// released before capture, and the window says nothing, as when the poll
    /// reads it first.</summary>
    [Fact]
    public async Task A_shortcut_before_the_check_answers_is_a_shortcut_when_the_answer_runs_before_the_poll()
    {
        const int vkC = 0x43;
        var log = new MemoryLog();
        using var hands = new HandsFree(checks: true, logs: log);
        await hands.EnableAsync();
        var before = hands.Status;

        hands.Press();
        hands.Key.KeyEvent(vkC, HandsFree.Down);
        hands.Checks.Answer();
        hands.Rig.Dispatcher.Drain();
        hands.Key.KeyEvent(vkC, HandsFree.Up);
        hands.Release();
        hands.Wait(500);

        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);
        Assert.Equal(before, hands.Status);
        Assert.Contains("event=finished result=shortcut", Endings(log).Single());
        Assert.False(hands.Key.DictationInFlight, "Escape is inert again");
        AssertWellFormed(log);
    }

    /// <summary>Escape is armed for the press, and only for it: once the
    /// press ends without being admitted, Escape is inert again, as it is at
    /// rest. The hook would otherwise latch a cancel on every Escape the user
    /// pressed in any app.</summary>
    [Theory]
    [InlineData("password field")]
    [InlineData("released first")]
    [InlineData("escape")]
    public async Task The_Escape_arm_comes_down_with_a_press_that_was_never_admitted(string how)
    {
        using var hands = new HandsFree(checks: true);
        await hands.EnableAsync();

        hands.Press();
        Assert.True(hands.Key.DictationInFlight, "Escape is armed at the press");
        switch (how)
        {
            case "password field":
                hands.Checks.Answer(SecureFieldState.Secure);
                hands.Wait(0);
                Assert.Equal(DictationCopy.SecureFieldAtPress, hands.Status);
                hands.Release();
                break;
            case "released first":
                hands.Release();
                // Past the hands-free pair window, which keeps a quick tap's
                // hold open: the key now reads up.
                hands.Wait(500);
                hands.Checks.Answer();
                hands.Wait(0);
                break;
            default:
                hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
                hands.Event(HoldKeyState.VkEscape, HandsFree.Up);
                hands.Release();
                hands.Checks.Answer();
                hands.Wait(0);
                break;
        }

        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);
        Assert.False(hands.Key.DictationInFlight);
        Assert.Equal(HoldKeyTransition.None, hands.Event(HoldKeyState.VkEscape, HandsFree.Down));
    }

    [Fact]
    public async Task A_state_the_last_dictation_posted_late_leaves_Escape_armed_for_the_next_press()
    {
        // The last dictation goes back to rest on its own thread and posts
        // that state; a press made before the post runs starts its check, and
        // the post then repainted the hook's arm from the state alone, taking
        // Escape away from the press.
        var log = new MemoryLog();
        using var hands = new HandsFree(checks: true, logs: log);
        await hands.EnableAsync();
        hands.Press();
        hands.Checks.Answer();
        hands.Wait(0);
        hands.Speak();
        hands.Advance(500);
        using (var decode = new ManualResetEventSlim())
        {
            // Held, so the release's own drain cannot run the posts below.
            hands.Rig.Platform.Worker.HoldTranscribe = decode;
            hands.Release();
            decode.Set();
            // Back at rest: its state and its result are posted, not run.
            log.AwaitRaised();
            hands.Rig.Platform.Worker.HoldTranscribe = null;
        }

        // A quick tap: the hold waits out the pair window with the key up,
        // where only the press's arm lets the hook hear Escape.
        hands.Press();
        hands.Advance(60);
        hands.Release();
        hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
        hands.Checks.Answer();
        hands.Wait(0);

        Assert.Equal(1, hands.Rig.Platform.Microphone.Starts);
        Assert.Equal("Cancelled. Nothing was inserted.", hands.Status);
    }
}
