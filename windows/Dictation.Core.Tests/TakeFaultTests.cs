namespace Dictation.Core.Tests;

// LIMIT-WIN-A, faults keep the take (LIMIT-DESIGN §3.6 and §5 step 5: "a
// fake device that raises RecordingStopped(exception) ends the hold and
// transcribes the captured audio; a 300 ms stall does the same"), through
// the controller the app runs. The Mac's watchdog scenarios that apply are
// ported in TakeWatchdogTests; its interruption discards the hold, which
// Windows no longer does.

/// <summary>A microphone that fails or stalls under a take.</summary>
public sealed class TakeFaultTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static int Transcribes(Rig rig) => rig.Platform.Worker.Ops().Count(op => op == "transcribe");

    /// <summary>The rig with the watchdog on, as the product has it.</summary>
    private static Rig Watched() => new(options: o => o with { Takes = o.Takes with { StallGap = DictationCaptureWatchdog.Gap } });

    /// <summary>Ticks until the take's ending, past "Transcribing what was recorded…".</summary>
    private static void UntilEnded(Rig rig) =>
        rig.TickUntil(() => rig.View.Last.Status.Contains("stopped at", StringComparison.Ordinal)
            && !rig.View.Last.Status.Contains("Transcribing", StringComparison.Ordinal));

    private static void Tick(Rig rig, TimeSpan after)
    {
        rig.Clock.Advance(after);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
    }

    // ─── Windows stops the stream with an error ─────────────────────────

    [Fact]
    public async Task A_device_that_raises_RecordingStopped_with_an_exception_ends_the_take_and_transcribes_what_it_captured()
    {
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(2));

        rig.Platform.Microphone.Fail();
        rig.Dispatcher.Drain();
        UntilEnded(rig);

        var at = TimeSpan.FromSeconds(2.5);
        Assert.Equal(DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneFailed, at), rig.View.Last.Status);
        Assert.Equal(DictationCopy.HudTakeStoppedInserted(DictationTakeStop.MicrophoneFailed, at), rig.Hud.Current!.Text);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Equal(1, Transcribes(rig));
        Assert.False(rig.Platform.Microphone.Open);
        // The words went in: nothing is kept, and the spool is gone.
        Assert.Null(rig.View.Last.SavedTake);
        Assert.Empty(Directory.GetFiles(rig.RecoveryDirectory));
        Assert.Contains(log.Lines, l => l.StartsWith("The microphone's stream stopped by itself while the take listened (COMException 0x88890004)", StringComparison.Ordinal));
        // The key still held starts nothing until it is pressed again.
        Assert.Equal(1, rig.Platform.Microphone.Starts);
        Assert.True(rig.Platform.Trigger.Down);
    }

    [Fact]
    public async Task While_what_a_fault_left_is_transcribed_the_window_says_so()
    {
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        await rig.EnableAsync();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Press();

        rig.Platform.Microphone.Fail();
        rig.Dispatcher.Drain();
        try
        {
            rig.TickUntil(() => rig.View.Last.Tray.Icon == TrayIcon.Transcribing);
            Assert.Equal(
                DictationCopy.TakeStoppedTranscribing(DictationTakeStop.MicrophoneFailed, TimeSpan.FromSeconds(0.5)),
                rig.View.Last.Status);
        }
        finally
        {
            decode.Set();
        }

        UntilEnded(rig);
    }

    [Fact]
    public async Task A_fault_before_anything_was_heard_says_so()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        rig.Platform.Microphone.Fail();
        rig.Dispatcher.Drain();
        UntilEnded(rig);

        Assert.Equal(
            DictationCopy.TakeStoppedNothingHeard(DictationTakeStop.MicrophoneFailed, TimeSpan.Zero, rig.Controller.HoldKey),
            rig.View.Last.Status);
        Assert.Equal(DictationCopy.HudTakeStoppedNothingHeard(DictationTakeStop.MicrophoneFailed, TimeSpan.Zero), rig.Hud.Current!.Text);
        Assert.Equal(0, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_fault_from_an_earlier_capture_or_at_rest_changes_nothing()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Microphone.Fail();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);

        rig.Dictate(Inserted);
        rig.Press();
        rig.Platform.Microphone.Fail(capture: 1);
        rig.Dispatcher.Drain();
        rig.Controller.Tick();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last) && rig.Platform.Paste.Count == 2);
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pausing_stops_hearing_the_old_microphone_s_faults_and_enabling_hears_the_new_one_s()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var first = rig.Platform.Microphone;
        Assert.Equal(1, first.FaultListeners);

        Assert.True(rig.Controller.TryPause());
        Assert.Equal(0, first.FaultListeners);
        Assert.Equal(0, first.Listeners);

        await rig.EnableAsync();
        Assert.Equal(1, rig.Platform.Microphone.FaultListeners);
    }

    // ─── The microphone stops delivering ────────────────────────────────

    [Fact]
    public async Task A_300_ms_stall_after_the_grace_ends_the_take_and_transcribes_what_it_captured()
    {
        using var rig = Watched();
        await rig.EnableAsync();
        rig.Press();
        Tick(rig, TimeSpan.FromMilliseconds(500));
        rig.Platform.Microphone.Buffer(0.3f);
        Tick(rig, TimeSpan.FromMilliseconds(250));
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);

        Tick(rig, TimeSpan.FromMilliseconds(50));
        UntilEnded(rig);

        var at = TimeSpan.FromSeconds(0.51);
        Assert.Equal(DictationCopy.TakeStoppedInserted(DictationTakeStop.MicrophoneStalled, at), rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Null(rig.View.Last.SavedTake);
    }

    [Fact]
    public async Task A_take_whose_buffers_keep_arriving_is_never_stopped()
    {
        using var rig = Watched();
        await rig.EnableAsync();
        rig.Press();

        for (var i = 0; i < 50; i++)
        {
            Tick(rig, TimeSpan.FromMilliseconds(100));
            rig.Platform.Microphone.Buffer(0.3f);
        }

        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_two_second_wait_for_the_first_buffer_is_not_a_stall_but_two_and_a_half_is()
    {
        using var rig = Watched();
        await rig.EnableAsync();
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Tick(rig, TimeSpan.FromSeconds(2));
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Tick(rig, TimeSpan.FromSeconds(0.5));
        UntilEnded(rig);

        Assert.Equal(
            DictationCopy.TakeStoppedNothingHeard(DictationTakeStop.MicrophoneStalled, TimeSpan.Zero, rig.Controller.HoldKey),
            rig.View.Last.Status);
    }

    [Fact]
    public async Task The_watchdog_is_disarmed_by_the_release_so_a_tick_after_the_take_does_nothing()
    {
        using var rig = Watched();
        using var decode = new ManualResetEventSlim();
        await rig.EnableAsync();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Press();
        rig.Release();
        try
        {
            Tick(rig, TimeSpan.FromSeconds(5));
            Tick(rig, TimeSpan.FromSeconds(5));
        }
        finally
        {
            decode.Set();
        }

        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stopped_hands_free_take_ends_its_latch()
    {
        using var rig = Watched();
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.Trigger.Latched = true;
        rig.Platform.Trigger.Down = false;
        Tick(rig, TimeSpan.FromMilliseconds(30));
        Assert.True(rig.Platform.Trigger.IsLatched);

        Tick(rig, TimeSpan.FromSeconds(1));
        UntilEnded(rig);

        Assert.False(rig.Platform.Trigger.IsLatched);
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_microphone_that_takes_seconds_to_open_is_not_stopped_while_its_first_buffer_comes()
    {
        // Windows opens the device at the press, on the controller's thread;
        // a Bluetooth headset switching to its call profile can take seconds
        // to return. The graces count from the capture being up, as the
        // Mac's count from a microphone already open: two seconds to open
        // and 0.6 s to the first buffer is a live device, not a stall.
        using var rig = Watched();
        await rig.EnableAsync();
        rig.Platform.Microphone.Opening = () => rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Tick(rig, TimeSpan.FromSeconds(0.6));
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        for (var i = 0; i < 20; i++)
        {
            rig.Platform.Microphone.Buffer(0.3f);
            Tick(rig, TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_kill_switch_is_DICTATION_CAPTURE_WATCHDOG_and_off_means_no_watchdog_at_all()
    {
        // The Mac's testTheKillSwitchIsTheRegistryFlagAndOffMeansNoWatchdogAtAll:
        // its dictation.capture.watchdog flag, as Windows' environment
        // switch. On by default; only 0 turns it off.
        Assert.Equal("DICTATION_CAPTURE_WATCHDOG", DictationEnvironment.CaptureWatchdog);
        Assert.Contains(DictationEnvironment.CaptureWatchdog, DictationEnvironment.Overrides);
        Assert.True(DictationEnvironment.CaptureWatchdogIsOn(null));
        Assert.True(DictationEnvironment.CaptureWatchdogIsOn(""));
        Assert.True(DictationEnvironment.CaptureWatchdogIsOn("1"));
        Assert.False(DictationEnvironment.CaptureWatchdogIsOn("0"));
        Assert.False(DictationEnvironment.CaptureWatchdogIsOn(" 0 "));

        using var rig = new Rig(
            environment: name => name == DictationEnvironment.CaptureWatchdog ? "0" : null,
            options: o => o with { Takes = o.Takes with { StallGap = DictationCaptureWatchdog.Gap } });
        await rig.EnableAsync();
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        // Everything a live take would trip on, and nothing happens.
        Tick(rig, TimeSpan.FromSeconds(9));
        Tick(rig, TimeSpan.FromSeconds(1));

        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped at", StringComparison.Ordinal));
        rig.Platform.Microphone.Speak();
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    // ─── The receipt ────────────────────────────────────────────────────

    [Fact]
    public async Task A_take_a_fault_ended_says_so_on_its_receipt_and_a_released_one_does_not()
    {
        // The take's words went in either way (result=inserted); only the
        // stop field tells the cut-short one from a release.
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        rig.Press();
        rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(2));
        rig.Platform.Microphone.Fail();
        rig.Dispatcher.Drain();
        UntilEnded(rig);
        DictationAttemptWiringTests.AwaitEndings(rig, log, count: 2);

        var endings = DictationAttemptWiringTests.Endings(log);
        Assert.Contains("result=inserted", endings[0], StringComparison.Ordinal);
        Assert.DoesNotContain("stop=", endings[0], StringComparison.Ordinal);
        Assert.Contains("result=inserted", endings[1], StringComparison.Ordinal);
        Assert.EndsWith(" stop=microphone_failed", endings[1], StringComparison.Ordinal);
        DictationAttemptWiringTests.AssertWellFormed(log);

        Assert.Equal("stop=microphone_stalled", DictationAttemptReceipt.Stop(DictationTakeStop.MicrophoneStalled));
        Assert.Equal("stop=microphone_failed", DictationAttemptReceipt.Stop(DictationTakeStop.MicrophoneFailed));
        Assert.Equal("stop=microphone_removed", DictationAttemptReceipt.Stop(DictationTakeStop.MicrophoneRemoved));
        Assert.Equal("stop=worker_lost", DictationAttemptReceipt.Stop(DictationTakeStop.WorkerLost));
        Assert.Null(DictationAttemptReceipt.Stop(DictationTakeStop.None));
    }
}
