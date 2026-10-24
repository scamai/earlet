using Dictation.Services;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationAttemptLogTests;

namespace Dictation.Core.Tests;

// A microphone that changes while a hold listens. The trigger is the Mac's
// D15 device-gone one: the microphone the hold opened unplugged, disabled or
// removed (AudioCaptureEndpointChangeRule); a default that moves mid-hold
// ends nothing, as on the Mac. What it does follows LIMIT-WIN-A (2026-10-10,
// rule zero: no stop loses words): the take ends and what it captured is
// transcribed and inserted, "The microphone stopped at m:ss …". The Mac
// still rebinds and discards the hold with "Mic changed to … — could you say
// that again?" until its own stage 1; the scenarios that were ported from it
// (TriggerCaptureTests.swift interruptedHoldSaysMicChanged and
// failedRebindNamesNoDevice, DictationHUDTests.swift's notice suite) keep
// their shape here with the new ending. The dead-stream triggers are the
// take watchdog's (TakeWatchdogTests).

/// <summary>Which endpoint notifications concern an open capture
/// (<see cref="AudioCaptureEndpointChangeRule"/>), as a table.</summary>
public sealed class MicrophoneChangeRuleTests
{
    private const string Open = "{0.0.1.00000000}.{aaaa}";
    private const string Other = "{0.0.1.00000000}.{bbbb}";

    [Theory]
    [InlineData(Open, Open, false, AudioCaptureEndpointChangeKind.Removed)]
    [InlineData(Open, "{0.0.1.00000000}.{AAAA}", false, AudioCaptureEndpointChangeKind.Removed)]
    [InlineData(Open, Open, true, null)]
    [InlineData(Open, Other, false, null)]
    // A headset plugged in mid-hold: another microphone becoming active (and
    // usually the new default) concerns nothing, as on the Mac.
    [InlineData(Open, Other, true, null)]
    [InlineData(null, Open, false, null)]
    [InlineData(Open, null, false, null)]
    public void A_state_change_concerns_the_open_microphone_going_inactive(
        string? open, string? device, bool stillActive, AudioCaptureEndpointChangeKind? kind) =>
        Assert.Equal(kind, AudioCaptureEndpointChangeRule.StateChanged(open, device, stillActive));

    [Theory]
    [InlineData(Open, Open, AudioCaptureEndpointChangeKind.Removed)]
    [InlineData(Open, Other, null)]
    [InlineData(null, Open, null)]
    [InlineData(Open, null, null)]
    public void A_removal_concerns_the_open_microphone_only(string? open, string? device, AudioCaptureEndpointChangeKind? kind) =>
        Assert.Equal(kind, AudioCaptureEndpointChangeRule.Removed(open, device));

    [Theory]
    // Windows resolved another active microphone: named.
    [InlineData(Open, Other, true, "AirPods Pro", "AirPods Pro")]
    // Resolved the one just removed (its default not yet moved): not named,
    // ids compared ignoring case.
    [InlineData(Open, Open, true, "Headset", null)]
    [InlineData(Open, "{0.0.1.00000000}.{AAAA}", true, "Headset", null)]
    // Resolved one that is not active.
    [InlineData(Open, Other, false, "AirPods Pro", null)]
    // Resolved none, or one with no name to show.
    [InlineData(Open, null, true, "AirPods Pro", null)]
    [InlineData(Open, Other, true, "  ", null)]
    [InlineData(Open, Other, true, null, null)]
    public void The_sentence_names_only_another_active_microphone(
        string open, string? next, bool active, string? name, string? shown) =>
        Assert.Equal(shown, AudioCaptureEndpointChangeRule.NameOfNext(open, next, active, name));
}

/// <summary>The sentences, and the names they may carry.</summary>
public sealed class MicrophoneChangeCopyTests
{
    [Fact]
    public void The_window_says_where_the_take_stopped_and_names_the_next_microphone()
    {
        var at = TimeSpan.FromSeconds(42);
        Assert.Equal(
            "The microphone stopped at 0:42, so the recording ended there. Your words up to then went in. "
            + "The next hold uses AirPods Pro.",
            DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, at, "AirPods Pro"));
        Assert.Equal(
            "The microphone stopped at 0:42, so the recording ended there. Your words up to then went in.",
            DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, at, null));
        Assert.Equal(
            DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, at, null),
            DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, at, " \t "));
        // Only a removed microphone names the next one.
        Assert.Equal(
            "The microphone stopped at 0:42, so the recording ended there. Your words up to then went in.",
            DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneStalled, at, "AirPods Pro"));
        Assert.Equal(
            "Speech recognition stopped at 0:42, so the recording ended there. Your words up to then went in.",
            DictationCopy.TakeStoppedInserted(DictationTakeStop.WorkerLost, at, "AirPods Pro"));
    }

    [Fact]
    public void The_indicator_reads_what_happened_then_what_to_do()
    {
        var stopped = DictationHudLines.For(
            DictationCopy.HudTakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, TimeSpan.FromSeconds(252)));
        Assert.Equal(new DictationHudLines("Microphone stopped at 4:12", "Your words up to then went in"), stopped);

        var unheard = DictationHudLines.For(
            DictationCopy.HudTakeStoppedNothingHeard(DictationTakeStop.MicrophoneFailed, TimeSpan.FromSeconds(3)));
        Assert.Equal(new DictationHudLines("Microphone stopped at 0:03", "Nothing was heard before it"), unheard);
    }

    [Theory]
    [InlineData("Headset Microphone (Jabra Evolve2 65)", "Headset Microphone (Jabra Evolve2 65)")]
    [InlineData("  Line\tIn \r\n (USB)  ", "Line In (USB)")]
    [InlineData("Mic\u0000Array", "Mic Array")]
    // The indicator splits its two lines at the first " — ".
    [InlineData("Studio — Desk", "Studio - Desk")]
    [InlineData("Microphone Array (Intel® Smart Sound Technology for Digital Microphones)", "Microphone Array (Intel® Smart Sound Te…")]
    [InlineData("\u0007\u0008", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_name_is_one_short_line(string? raw, string? shown) =>
        Assert.Equal(shown, DictationCopy.MicrophoneForSentence(raw));

    [Fact]
    public void A_long_name_is_cut_at_the_limit_and_never_inside_a_surrogate_pair()
    {
        var limit = DictationCopy.MicrophoneNameLimit;
        var exact = new string('m', limit);
        Assert.Equal(exact, DictationCopy.MicrophoneForSentence(exact));

        var longer = DictationCopy.MicrophoneForSentence(exact + "m")!;
        Assert.Equal(limit, longer.Length);
        Assert.EndsWith("…", longer, StringComparison.Ordinal);

        // An astral character (two UTF-16 units) straddling the cut is
        // dropped whole; one that fits before the ellipsis is kept whole.
        const string Microphone = "\U0001F3A4";
        var straddling = new string('m', limit - 2) + Microphone + "mmmm";
        Assert.Equal(new string('m', limit - 2) + "…", DictationCopy.MicrophoneForSentence(straddling));
        var fitting = new string('m', limit - 3) + Microphone + "mmmm";
        Assert.Equal(new string('m', limit - 3) + Microphone + "…", DictationCopy.MicrophoneForSentence(fitting));
    }
}

/// <summary>The ending through the controller the app runs: since LIMIT-WIN-A
/// (2026-10-10) a microphone that goes away ends the take and its words go
/// in, where it used to end the hold unheard with "Mic changed to … — could
/// you say that again?".</summary>
public sealed class MicrophoneChangeTests
{
    private const string AirPods = "AirPods Pro";

    /// <summary>The window's ending after the rig's half second of speech.</summary>
    private static readonly string MicStopped =
        DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, TimeSpan.FromSeconds(0.5), AirPods);

    /// <summary>The indicator's.</summary>
    private static readonly string HudMicStopped =
        DictationCopy.HudTakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, TimeSpan.FromSeconds(0.5));

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static int Ops(Rig rig, string op) => rig.Platform.Worker.Ops().Count(o => o == op);

    private static int Transcribes(Rig rig) => Ops(rig, "transcribe");

    /// <summary>A hold that listens and has heard something.</summary>
    private static async Task<Rig> Listening(MemoryLog? log = null)
    {
        var rig = new Rig(logs: log);
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        return rig;
    }

    /// <summary>Windows reports the change; the next drain applies it.</summary>
    private static void Change(
        Rig rig, AudioCaptureEndpointChangeKind kind = AudioCaptureEndpointChangeKind.Removed, string? next = AirPods, int? capture = null)
    {
        rig.Platform.Microphone.Change(kind, next, capture);
        rig.Dispatcher.Drain();
    }

    /// <summary>The change, then the ticks that end the take and the drains
    /// that bring its ending back.</summary>
    private static void ChangeUntilEnded(Rig rig, string? next = AirPods)
    {
        Change(rig, next: next);
        // Past "… Transcribing what was recorded…" to the ending.
        rig.TickUntil(() => rig.View.Last.Status.Contains("stopped at", StringComparison.Ordinal)
            && !rig.View.Last.Status.Contains("Transcribing", StringComparison.Ordinal));
    }

    // ─── The take ends, and its words go in ─────────────────────────────

    [Fact]
    public async Task A_hold_interrupted_by_a_microphone_change_inserts_what_it_heard_and_says_where_it_stopped()
    {
        var log = new MemoryLog();
        using var rig = await Listening(log);

        ChangeUntilEnded(rig);

        Assert.Equal(MicStopped, rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Equal(1, Transcribes(rig));
        Assert.False(rig.Platform.Microphone.Open);
        Assert.Null(rig.View.Last.SavedTake);
        Assert.Contains(log.Lines, l => l.StartsWith("The microphone changed while the hold listened (Removed); transcribing what was captured", StringComparison.Ordinal));
        DictationAttemptWiringTests.AwaitEndings(rig, log);
        var ending = Assert.Single(DictationAttemptWiringTests.Endings(log));
        Assert.Contains(" event=finished result=inserted ", ending, StringComparison.Ordinal);
        DictationAttemptWiringTests.AssertWellFormed(log);
        Assert.DoesNotContain(log.Receipts, l => l.Contains(AirPods, StringComparison.Ordinal));
    }

    [Fact]
    public async Task While_what_it_heard_is_transcribed_the_window_says_the_microphone_stopped()
    {
        using var rig = await Listening();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        try
        {
            ChangeUntilTranscribing(rig);
            Assert.Equal(
                DictationCopy.TakeStoppedTranscribing(DictationTakeStop.MicrophoneRemoved, TimeSpan.FromSeconds(0.5)),
                rig.View.Last.Status);
        }
        finally
        {
            decode.Set();
        }

        rig.DrainUntil(() => rig.View.Last.Status == MicStopped);
    }

    private static void ChangeUntilTranscribing(Rig rig)
    {
        Change(rig);
        rig.TickUntil(() => rig.View.Last.Tray.Icon == TrayIcon.Transcribing);
    }

    [Fact]
    public async Task A_change_that_names_no_microphone_names_no_device()
    {
        using var rig = await Listening();

        ChangeUntilEnded(rig, next: null);

        Assert.Equal(
            DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, TimeSpan.FromSeconds(0.5)),
            rig.View.Last.Status);
        Assert.DoesNotContain("next hold", rig.View.Last.Status, StringComparison.Ordinal);
        Assert.Equal(HudMicStopped, rig.Hud.Current!.Text);
    }

    [Fact]
    public async Task An_interruption_publishes_its_ending_on_the_indicator()
    {
        // A caution, spoken once the microphone is closed.
        using var rig = await Listening();

        ChangeUntilEnded(rig);

        var pill = rig.Hud.Current!;
        Assert.Equal(HudMicStopped, pill.Text);
        Assert.Equal(HudTone.Caution, pill.Tone);
        Assert.True(rig.Hud.Announced[^1]);
        Assert.False(rig.View.Last.MicrophoneOpen);
    }

    [Fact]
    public async Task The_ending_clears_itself_after_its_seconds()
    {
        // Windows' failure tier (5 s); the status line keeps the sentence.
        using var rig = await Listening();
        ChangeUntilEnded(rig);
        Assert.Equal(DictationHudModel.FailureDwell, rig.Hud.Current!.Dwell);

        rig.Clock.Advance(DictationHudModel.FailureDwell - TimeSpan.FromMilliseconds(30));
        rig.Controller.Tick();
        Assert.NotNull(rig.Hud.Current);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(30));
        rig.Controller.Tick();

        Assert.Null(rig.Hud.Current);
        Assert.Equal(MicStopped, rig.View.Last.Status);
    }

    [Fact]
    public async Task The_next_press_clears_the_ending_however_much_time_is_left()
    {
        using var rig = await Listening();
        ChangeUntilEnded(rig);
        rig.Release();
        rig.Dispatcher.Drain();

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);
        Assert.NotEqual(MicStopped, rig.View.Last.Status);

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(2, rig.Platform.Paste.Count);
        Assert.NotEqual(HudMicStopped, rig.Hud.Current?.Text);
    }

    // ─── Windows: what ends, and what is left alone ─────────────────────

    [Fact]
    public async Task A_key_still_held_starts_nothing_until_it_is_pressed_again()
    {
        using var rig = await Listening();
        ChangeUntilEnded(rig);

        for (var i = 0; i < 5; i++)
        {
            rig.Controller.Tick();
            rig.Dispatcher.Drain();
        }

        Assert.Equal(1, rig.Platform.Microphone.Starts);
        Assert.Equal(MicStopped, rig.View.Last.Status);
        Assert.NotEqual(TrayIcon.Listening, rig.View.Last.Tray.Icon);

        rig.Release();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(2, rig.Platform.Microphone.Starts);
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task A_hands_free_latch_ends_with_its_take_and_its_words_go_in()
    {
        using var rig = await Listening();
        rig.Platform.Trigger.Latched = true;
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        var ends = rig.Platform.Trigger.EndLatchCalls;

        ChangeUntilEnded(rig);

        Assert.True(rig.Platform.Trigger.EndLatchCalls > ends);
        Assert.False(rig.Platform.Trigger.IsLatched);
        Assert.Equal(MicStopped, rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_change_once_the_hold_is_transcribing_changes_nothing()
    {
        using var rig = await Listening();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);

        Change(rig);
        decode.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_change_seen_during_an_earlier_capture_is_ignored()
    {
        using var rig = await Listening();
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(2, rig.Platform.Microphone.Capture);

        rig.Platform.Microphone.Change(AudioCaptureEndpointChangeKind.Removed, AirPods, capture: 1);
        rig.Dispatcher.Drain();
        rig.Controller.Tick();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last) && rig.Platform.Paste.Count == 2);
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_change_from_the_microphone_an_earlier_arm_opened_is_ignored()
    {
        // Raised at rest and still queued when a new hold key re-arms with a
        // new microphone, whose first capture then listens.
        using var rig = new Rig();
        await rig.EnableAsync();
        var old = rig.Platform.Microphone;
        old.Change(AudioCaptureEndpointChangeKind.Removed, AirPods);
        rig.Controller.ChooseHoldKey(DictationHotkeys.VkRightShift);
        Assert.NotSame(old, rig.Platform.Microphone);
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Controller.Tick();

        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_late_change_from_the_last_capture_leaves_a_press_being_decided_alone()
    {
        // The change was seen during the last capture and reaches the UI
        // thread while the next press's field check is out. No capture has
        // opened for that press, so the capture numbers still match; only
        // "a hold is listening" tells the change it concerns nothing.
        var log = new MemoryLog();
        var checks = new FieldChecks();
        using var rig = new Rig(logs: log, options: o => o with { RunFieldCheck = checks.Run });
        await rig.EnableAsync();
        rig.Press();
        checks.Answer();
        rig.Dispatcher.Drain();
        rig.Platform.Microphone.Speak();
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        var last = rig.Platform.Microphone.Capture;

        rig.Press();
        rig.Dispatcher.Drain();
        Change(rig, capture: last);
        checks.Answer();
        rig.Dispatcher.Drain();

        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Assert.Equal(last + 1, rig.Platform.Microphone.Capture);
        rig.Platform.Microphone.Speak();
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last) && rig.Platform.Paste.Count == 2);
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
        DictationAttemptWiringTests.AwaitEndings(rig, log, 2);
        Assert.All(DictationAttemptWiringTests.Endings(log), l => Assert.Contains(" result=inserted ", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_change_at_rest_changes_nothing()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var shown = rig.View.States.Count;

        Change(rig);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Equal(shown, rig.View.States.Count);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
    }

    [Fact]
    public async Task Pausing_stops_hearing_the_old_microphone_and_enabling_hears_the_new_one()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var first = rig.Platform.Microphone;
        Assert.Equal(1, first.ChangeListeners);

        Assert.True(rig.Controller.TryPause());
        Assert.Equal(0, first.ChangeListeners);

        await rig.EnableAsync();
        Assert.NotSame(first, rig.Platform.Microphone);
        Assert.Equal(1, rig.Platform.Microphone.ChangeListeners);
    }

    [Fact]
    public async Task A_microphone_change_stops_the_preview_and_the_next_press_opens_a_fresh_one()
    {
        // LivePreviewTests.swift, "D15 discard stops the preview and the
        // rebind starts a fresh run". Windows has no rebind: the change ends
        // the take, whose words go in, and the next press's run is the fresh one.
        using var rig = LivePreviewTests.PreviewRig();
        await rig.EnableAsync();
        LivePreviewTests.PressAndPreview(rig);

        ChangeUntilEnded(rig);
        Assert.True(SpinWait.SpinUntil(() => Ops(rig, "stream_cancel") == 1, 5_000));
        rig.DrainUntil(() => rig.View.Last.Status == MicStopped);
        Assert.Null(rig.Hud.Current?.Caption);
        Assert.Equal(1, Transcribes(rig));

        rig.Release();
        LivePreviewTests.PressAndPreview(rig, streams: 2);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(2, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_changed_take_s_words_become_the_last_text()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var held = rig.View.Last.Recent;
        Assert.NotNull(held);

        rig.Press();
        rig.Dispatcher.Drain();
        ChangeUntilEnded(rig);

        Assert.NotNull(rig.View.Last.Recent);
        Assert.NotEqual(held!.Id, rig.View.Last.Recent!.Id);
    }

    // ─── Beside the password-field checks (FieldCheckTests) ─────────────
    //
    // A take the microphone change ended is released like any other, so it
    // meets the release's password-field check; a change that reaches the UI
    // thread once the check at the paste or the press-time check is out
    // concerns no listening hold and changes nothing.

    [Fact]
    public async Task A_take_the_microphone_change_ended_still_meets_the_password_field_check_at_its_release()
    {
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        rig.Platform.Worker.Transcript = DictationAttemptWiringTests.Words;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        rig.Platform.SecureField.Next = SecureFieldState.Secure;

        Change(rig);
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtRelease);
        DictationAttemptWiringTests.AwaitEndings(rig, log);

        Assert.Equal(0, Transcribes(rig));
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Null(rig.View.Last.SavedTake);
        var ending = Assert.Single(DictationAttemptWiringTests.Endings(log));
        Assert.Contains(" event=finished result=failed stage=capture reason=secure_field ", ending, StringComparison.Ordinal);
        DictationAttemptWiringTests.AssertWellFormed(log);
    }

    [Theory]
    [InlineData(SecureFieldState.Secure)]
    [InlineData(SecureFieldState.NotSecure)]
    public async Task A_change_while_the_field_is_checked_at_the_paste_leaves_the_check_its_answer(SecureFieldState field)
    {
        // Reported for the capture that just closed, so its number matches:
        // only "a hold is listening" tells it the dictation is past it.
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
        rig.Platform.Worker.Transcript = DictationAttemptWiringTests.Words;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref asked) == 1, TimeSpan.FromSeconds(10)), "the paste check never ran");
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.Transcribing, rig.View.Last.Status);

        Change(rig);
        Assert.Equal(DictationCopy.Transcribing, rig.View.Last.Status);
        answer.SetResult(field);
        rig.DrainUntil(() => field == SecureFieldState.Secure
            ? rig.View.Last.Status == DictationCopy.SecureFieldAtPaste
            : Inserted(rig.View.Last));
        DictationAttemptWiringTests.AwaitEndings(rig, log);

        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
        var ending = Assert.Single(DictationAttemptWiringTests.Endings(log));
        if (field == SecureFieldState.Secure)
        {
            Assert.Contains(" event=finished result=failed stage=paste reason=secure_field ", ending, StringComparison.Ordinal);
            Assert.Equal(0, rig.Platform.Paste.Count);
        }
        else
        {
            Assert.Contains(" event=finished result=inserted ", ending, StringComparison.Ordinal);
            Assert.Equal(1, rig.Platform.Paste.Count);
        }

        DictationAttemptWiringTests.AssertWellFormed(log);
    }

    [Fact]
    public async Task Escape_before_the_press_time_check_answers_is_a_cancel_after_a_changed_take()
    {
        // The take before ended on a microphone change, and its words went
        // in; a second report from that capture lands while the next press's
        // check is out, and Escape comes before the answer. That press ends
        // Cancelled, and the change touches nothing of it.
        var log = new MemoryLog();
        var checks = new FieldChecks();
        using var rig = new Rig(logs: log, options: o => o with { RunFieldCheck = checks.Run });
        await rig.EnableAsync();
        rig.Press();
        checks.Answer();
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        // Spoken once the check let the microphone open.
        rig.Platform.Microphone.Speak();
        ChangeUntilEnded(rig);
        rig.Release();
        rig.Dispatcher.Drain();
        Assert.Equal(MicStopped, rig.View.Last.Status);
        var last = rig.Platform.Microphone.Capture;
        var shown = rig.Hud.Shows.Count;

        rig.Press();
        rig.Dispatcher.Drain();
        Change(rig, capture: last);
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        checks.Answer();
        rig.Dispatcher.Drain();
        rig.Release();
        DictationAttemptWiringTests.AwaitEndings(rig, log, 2);

        Assert.Equal(1, rig.Platform.Microphone.Starts);
        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
        Assert.Contains(rig.Hud.Shows.Skip(shown), m => m.Text == "Cancelled");
        Assert.DoesNotContain(rig.Hud.Shows.Skip(shown), m => m.Text.Contains("stopped at", StringComparison.Ordinal));
        var endings = DictationAttemptWiringTests.Endings(log);
        Assert.Equal(2, endings.Count);
        Assert.Contains(" event=finished result=inserted ", endings[0], StringComparison.Ordinal);
        Assert.Contains(" event=finished result=cancelled ", endings[1], StringComparison.Ordinal);
        Assert.EndsWith("release_ms=n/a", endings[1], StringComparison.Ordinal);
        DictationAttemptWiringTests.AssertWellFormed(log);
    }
}

/// <summary>
/// The Win32 half, pinned on its source: NAudio's notifications cannot run
/// here. Each pin fails if the thing it names is deleted or moved; none is
/// evidence the notifications arrive on a device.
/// </summary>
public sealed class MicrophoneChangeSourceTests
{
    private static string Audio(string name) =>
        File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.Platform", "Audio", name))
            .ReplaceLineEndings("\n");

    [Fact]
    public void The_device_registers_once_when_it_gains_a_listener_and_releases_at_Dispose()
    {
        // The COM registration is made at the controller's Arm, never inside
        // a press on the UI thread that also runs the keyboard hook.
        var device = Audio("WasapiMicrophoneCaptureDevice.cs");
        var subscribe = DictationHudGeometryTests.Member(device, "public event EventHandler<AudioCaptureEndpointChange>? EndpointChanged");
        Assert.Contains("WatchForChanges();", subscribe, StringComparison.Ordinal);
        var watch = DictationHudGeometryTests.Member(device, "private void WatchForChanges()");
        Assert.Contains("if (_changesTried || _disposed)", watch, StringComparison.Ordinal);
        Assert.Contains("WasapiEndpointChangeWatch.TryStart(", watch, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(device, @"WasapiEndpointChangeWatch\.TryStart\("));

        var start = DictationHudGeometryTests.Member(device, "public bool Start()");
        Assert.DoesNotContain("TryStart(", start, StringComparison.Ordinal);
        var recording = start.IndexOf("capture.StartRecording();", StringComparison.Ordinal);
        var open = start.IndexOf("_changes?.Open(endpoint.ID, number);", StringComparison.Ordinal);
        Assert.True(recording >= 0 && open > recording, "a capture is announced to the watch once it records");

        var release = DictationHudGeometryTests.Member(device, "private void Release()");
        var close = release.IndexOf("_changes?.Close();", StringComparison.Ordinal);
        var unhook = release.IndexOf("_capture.DataAvailable -= OnDataAvailable;", StringComparison.Ordinal);
        Assert.True(close >= 0 && unhook > close, "the watch lets go of the capture before it closes");

        var dispose = DictationHudGeometryTests.Member(device, "public void Dispose()");
        Assert.Contains("_changes?.Dispose();", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void The_next_microphone_is_named_through_the_rule_with_the_open_one()
    {
        var device = Audio("WasapiMicrophoneCaptureDevice.cs");
        var next = DictationHudGeometryTests.Member(device, "private string? NextEndpointName(");
        Assert.Contains("AudioCaptureEndpointChangeRule.NameOfNext(", next, StringComparison.Ordinal);
        Assert.Contains("openEndpointId,", next, StringComparison.Ordinal);
        Assert.Contains("next.State == DeviceState.Active", next, StringComparison.Ordinal);

        var watch = Audio("WasapiEndpointChangeWatch.cs");
        Assert.Contains("_nextEndpointName(open.EndpointId)", watch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_watch_decides_on_the_audio_thread_and_does_the_rest_on_the_pool_once()
    {
        var watch = Audio("WasapiEndpointChangeWatch.cs");

        Assert.Contains("CreateNotificationClient(useSynchronizationContext: false)", watch, StringComparison.Ordinal);
        var notice = DictationHudGeometryTests.Member(watch, "private void Notice(");
        Assert.Contains("Interlocked.Exchange(ref open.Reported, 1) != 0", notice, StringComparison.Ordinal);
        var queued = notice.IndexOf("ThreadPool.QueueUserWorkItem(", StringComparison.Ordinal);
        var named = notice.IndexOf("_nextEndpointName(open.EndpointId)", StringComparison.Ordinal);
        Assert.True(queued >= 0 && named > queued, "the next microphone's name is read on the pool, not the audio thread");
    }

    [Fact]
    public void The_watch_hears_the_open_microphone_go_away_and_no_default_move()
    {
        // The Mac's watchdog (D15) ends a hold only when the device it bound
        // at the press dies; a headset plugged in mid-hold becomes the new
        // default and the hold still inserts. So the client subscribes to
        // state changes and removals, and never to the default moving.
        var watch = Audio("WasapiEndpointChangeWatch.cs");

        Assert.Contains("_client.DeviceStateChanged += OnDeviceStateChanged;", watch, StringComparison.Ordinal);
        Assert.Contains("_client.DeviceRemoved += OnDeviceRemoved;", watch, StringComparison.Ordinal);
        Assert.Contains("AudioCaptureEndpointChangeRule.StateChanged(open.EndpointId, e.DeviceId, stillActive: e.NewState == DeviceState.Active)", watch, StringComparison.Ordinal);
        Assert.Contains("AudioCaptureEndpointChangeRule.Removed(open.EndpointId, e.DeviceId)", watch, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultDeviceChanged", watch, StringComparison.Ordinal);
        Assert.Equal(
            [AudioCaptureEndpointChangeKind.Removed],
            Enum.GetValues<AudioCaptureEndpointChangeKind>());
    }
}
