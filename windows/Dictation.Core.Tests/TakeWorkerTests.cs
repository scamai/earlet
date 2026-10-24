namespace Dictation.Core.Tests;

// LIMIT-WIN-A, the speech worker under a long take (LIMIT-DESIGN §3.6 and §5
// step 5): a release whose worker dies or misses its deadline keeps the
// audio, restarts it and retries once with 30 s + 2x the length, then offers
// the take for recovery; and while a take longer than 30 s is recorded, the
// worker is pinged every 10 s, started again when it stopped, and the take
// ended and kept when two restarts fail. Through the real worker host and
// framing, with the fake worker answering late or never.

/// <summary>A decode the worker lost, and the health check during a long take.</summary>
public sealed class TakeWorkerTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static int Count(Rig rig, string op) => rig.Platform.Worker.Ops().Count(o => o == op);

    private static void Tick(Rig rig, TimeSpan after)
    {
        rig.Clock.Advance(after);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
    }

    /// <summary>The health check on, as the product has it, answered and
    /// reported back before the next step.</summary>
    /// <summary>Health checks every 10 s, and no silence stop: these takes
    /// run a minute of fake clock without speech, which the held stuck-key
    /// stop would end first (HoldStopTakeTests covers that one).</summary>
    private static Rig Pinged(MemoryLog? log = null) =>
        new(logs: log, options: o => o with
        {
            Takes = o.Takes with { PingInterval = TimeSpan.FromSeconds(10) },
            HoldPolicy = o.HoldPolicy.WithoutSilenceStops(),
        });

    /// <summary>Tick after <paramref name="after"/> and wait for any health
    /// check the tick sent to be answered back on the controller's thread.</summary>
    private static void TickAndAwaitChecks(Rig rig, TimeSpan after)
    {
        Tick(rig, after);
        rig.DrainUntil(() => !rig.Controller.CheckingWorker);
    }

    // ─── A decode the worker lost ───────────────────────────────────────

    [Fact]
    public async Task An_injected_timeout_leads_to_a_retry_with_the_longer_deadline_and_its_words_go_in()
    {
        // 1.5 s of audio and a 0.5 s deadline: the first try may take 2 s and
        // never answers; the retry may take 3.5 s and answers at 2.75 s,
        // past the first deadline. Only the longer budget lets it in.
        using var rig = new Rig(options: o => o with { Takes = o.Takes with { DecodeDeadline = TimeSpan.FromSeconds(0.5) } });
        await rig.EnableAsync();
        rig.Platform.Worker.TranscribeDelays.Enqueue(null);
        rig.Platform.Worker.TranscribeDelays.Enqueue(TimeSpan.FromSeconds(2.75));

        rig.Press();
        rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(1));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Contains(rig.View.States, s => s.Status == DictationCopy.RetryingTranscription);
        Assert.Equal(2, Count(rig, "transcribe"));
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Null(rig.View.Last.SavedTake);
        Assert.Empty(Directory.GetFiles(rig.RecoveryDirectory));
    }

    [Fact]
    public async Task A_retry_that_misses_its_deadline_too_keeps_the_take_for_recovery()
    {
        using var rig = new Rig(options: o => o with { Takes = o.Takes with { DecodeDeadline = TimeSpan.FromSeconds(0.2) } });
        await rig.EnableAsync();
        rig.Platform.Worker.TranscribeDelays.Enqueue(null);
        rig.Platform.Worker.TranscribeDelays.Enqueue(null);

        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.SavedTake is not null);

        Assert.Equal(2, Count(rig, "transcribe"));
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Contains(rig.View.States, s => s.Status == DictationCopy.TakeSavedWorkerRestarting && s.SavedTake is not null);
        Assert.Contains(rig.Hud.Shows, m => m.Text == DictationCopy.HudTakeSaved);

        // Recovered once the worker is back.
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));
        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);
        Assert.Equal(rig.Platform.Worker.Transcript, rig.View.Last.Recent?.Text);
    }

    [Fact]
    public async Task A_worker_that_dies_under_the_decode_is_started_again_for_the_retry()
    {
        // It dies once, mid-decode: the retry's exchange starts a new
        // process, and the words go in.
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        await rig.EnableAsync();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Press();
        rig.Release();
        Assert.True(SpinWait.SpinUntil(() => Count(rig, "transcribe") == 1, TimeSpan.FromSeconds(10)));
        rig.Platform.Worker.HoldTranscribe = null;
        rig.Platform.Worker.Crash();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Equal(2, Count(rig, "transcribe"));
        Assert.Equal(2, rig.Platform.Worker.Starts);
        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    // ─── The health check during a long take ────────────────────────────

    [Fact]
    public async Task A_take_is_checked_every_ten_seconds_once_it_has_run_thirty()
    {
        using var rig = Pinged();
        await rig.EnableAsync();
        rig.Press();

        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(29));
        Assert.Equal(0, Count(rig, "ping"));
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(1));
        Assert.Equal(1, Count(rig, "ping"));
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(9));
        Assert.Equal(1, Count(rig, "ping"));
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(1));
        Assert.Equal(2, Count(rig, "ping"));

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(30));
        Assert.Equal(2, Count(rig, "ping"));
    }

    [Fact]
    public async Task A_short_take_is_never_checked()
    {
        using var rig = Pinged();
        await rig.EnableAsync();
        rig.Press();
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(29));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Equal(0, Count(rig, "ping"));
    }

    [Fact]
    public async Task A_worker_that_died_during_a_long_take_is_started_again_while_the_user_speaks()
    {
        var log = new MemoryLog();
        using var rig = Pinged(log);
        await rig.EnableAsync();
        rig.Press();
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(30));
        Assert.Equal(1, Count(rig, "ping"));

        rig.Platform.Worker.Crash();
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(10));
        Assert.True(SpinWait.SpinUntil(() => rig.Platform.Worker.IsAlive && Count(rig, "ping") == 2, TimeSpan.FromSeconds(5)));
        rig.Dispatcher.Drain();

        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Assert.Equal(2, rig.Platform.Worker.Starts);
        Assert.Contains(log.Lines, l => l.StartsWith("The speech worker had stopped during a long take; it was started again", StringComparison.Ordinal));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_check_that_is_not_answered_in_time_is_followed_at_once_by_a_restart()
    {
        using var rig = new Rig(options: o => o with
        {
            Takes = o.Takes with { PingInterval = TimeSpan.FromSeconds(10), PingTimeout = TimeSpan.FromMilliseconds(200) },
        });
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.Worker.PingDelays.Enqueue(null);

        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(30));
        // The next tick checks again, without waiting ten seconds: a restart.
        TickAndAwaitChecks(rig, TimeSpan.FromMilliseconds(30));
        Assert.True(SpinWait.SpinUntil(() => Count(rig, "ping") == 2, TimeSpan.FromSeconds(5)));

        Assert.Equal(2, rig.Platform.Worker.Starts);
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Fact]
    public async Task When_two_restarts_fail_the_take_ends_and_is_kept_for_recovery()
    {
        var log = new MemoryLog();
        using var rig = Pinged(log);
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(4));
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(30));

        rig.Platform.Worker.InitSucceeds = false;
        rig.Platform.Worker.Crash();
        // The check that finds it dead, then the two restarts.
        for (var check = 1; check <= 3; check++)
        {
            var failures = $"({check} in a row)";
            TickAndAwaitChecks(rig, TimeSpan.FromSeconds(10));
            rig.TickUntil(() => log.Lines.Any(l => l.EndsWith(failures, StringComparison.Ordinal)));
        }

        rig.TickUntil(() => rig.View.Last.SavedTake is not null);
        Assert.Contains(log.Lines, l => l.StartsWith("The take ended at 4.5 s: WorkerLost", StringComparison.Ordinal));
        Assert.Equal(DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(4.5)), rig.View.Last.SavedTake!.Detail);
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.True(rig.Platform.Trigger.Down);
    }

    [Fact]
    public async Task When_the_checks_end_a_take_and_the_worker_starts_at_its_release_the_words_go_in()
    {
        // The take ends where the checks gave up; its release's decode tries
        // to start the worker once more, as every exchange on a dead worker
        // does, and this time it starts: the words up to the stop go in, and
        // nothing is kept.
        var log = new MemoryLog();
        using var rig = new Rig(logs: log, options: o => o with
        {
            Takes = o.Takes with { PingInterval = TimeSpan.FromSeconds(10), PingRestarts = 0 },
        });
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(4));
        TickAndAwaitChecks(rig, TimeSpan.FromSeconds(30));

        rig.Platform.Worker.InitSucceeds = false;
        rig.Platform.Worker.Crash();
        Tick(rig, TimeSpan.FromSeconds(10));
        rig.DrainUntil(() => log.Lines.Any(l => l.StartsWith("The take ended at 4.5 s: WorkerLost", StringComparison.Ordinal)));
        rig.Platform.Worker.InitSucceeds = true;
        rig.TickUntil(() => Inserted(rig.View.Last) || rig.View.Last.Status.Contains("stopped at", StringComparison.Ordinal)
            && !rig.View.Last.Status.Contains("Transcribing", StringComparison.Ordinal));

        Assert.Equal(DictationCopy.TakeStoppedInserted(DictationTakeStop.WorkerLost, TimeSpan.FromSeconds(4.5)), rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Null(rig.View.Last.SavedTake);
        Assert.Empty(Directory.GetFiles(rig.RecoveryDirectory));
    }

    [Fact]
    public async Task A_check_answered_after_a_newer_press_changes_nothing()
    {
        // The check goes out during one take and fails only once the next
        // take has begun: its failure is the old take's, not the new one's.
        // One failure would end a take here.
        using var rig = new Rig(options: o => o with
        {
            Takes = o.Takes with
            {
                PingInterval = TimeSpan.FromSeconds(10),
                PingTimeout = TimeSpan.FromSeconds(2),
                PingRestarts = 0,
            },
        });
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.Worker.PingDelays.Enqueue(null);
        Tick(rig, TimeSpan.FromSeconds(30));
        Assert.True(rig.Controller.CheckingWorker);

        // Escape ends the take, and the next press begins another, before
        // the check gives up.
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        rig.Release();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.True(rig.Controller.CheckingWorker, "the check answered before the next press began; the scenario did not happen");

        Assert.True(SpinWait.SpinUntil(
            () =>
            {
                rig.Dispatcher.Drain();
                return !rig.Controller.CheckingWorker;
            },
            TimeSpan.FromSeconds(10)));
        Tick(rig, TimeSpan.FromMilliseconds(30));

        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Assert.DoesNotContain(rig.View.States, v => v.Status.Contains("stopped at", StringComparison.Ordinal));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Fact]
    public async Task A_worker_still_loading_during_a_long_take_counts_as_alive()
    {
        // Unloaded after idle, the worker is loaded again at the press; a
        // model that takes over thirty seconds to load is not a dead worker,
        // and the take carries on. One failure would end it here.
        using var init = new ManualResetEventSlim();
        using var rig = new Rig(options: o => o with
        {
            Takes = o.Takes with { PingInterval = TimeSpan.FromSeconds(10), PingRestarts = 0 },
        });
        await rig.EnableAsync();
        Tick(rig, TimeSpan.FromMinutes(10));
        Assert.False(rig.Platform.Worker.IsAlive);
        rig.Platform.Worker.HoldInit = init;
        try
        {
            rig.Press();
            TickAndAwaitChecks(rig, TimeSpan.FromSeconds(30));
            TickAndAwaitChecks(rig, TimeSpan.FromSeconds(10));
            TickAndAwaitChecks(rig, TimeSpan.FromSeconds(10));

            Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
            Assert.Equal(0, Count(rig, "ping"));
        }
        finally
        {
            init.Set();
        }

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.DoesNotContain(rig.View.States, v => v.Status.Contains("stopped at", StringComparison.Ordinal));
    }
}
