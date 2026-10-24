using System.Text.Json;

namespace Dictation.Core.Tests;

/// <summary>
/// The speech worker between dictations: restarted straight away when it
/// dies, a bounded number of times; unloaded after three idle minutes and
/// loaded again by the next press; never restarted for a new language, which
/// reaches the next decode as it is.
/// </summary>
public sealed class DictationWorkerTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    // ─── A worker that dies ─────────────────────────────────────────────

    [Fact]
    public async Task A_worker_that_dies_between_dictations_is_restarted_straight_away()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Platform.Worker.Crash();
        rig.Controller.Tick();
        Assert.Equal(DictationCopy.WorkerStoppedWhileIdle, rig.View.Last.Status);

        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));
        Assert.Equal(2, rig.Platform.CreateWorkerCalls);
        // A fresh process gets its own startup log window.
        Assert.Equal(2, rig.Platform.StartupFinishedCalls);
        Assert.True(rig.Controller.IsEnabled);

        rig.Dictate(Inserted);
    }

    [Fact]
    public async Task A_worker_that_dies_mid_dictation_is_restarted_and_the_window_says_so()
    {
        // It dies on the decode and again on its one retry (LIMIT-WIN-A):
        // the take is kept for Recover last dictation, and the worker restarts.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Worker.DieOnTranscribe = true;

        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.States.Any(s => s.Status == DictationCopy.TakeSavedWorkerRestarting));
        rig.Platform.Worker.DieOnTranscribe = false;

        Assert.Equal(2, rig.Platform.Worker.Ops().Count(op => op == "transcribe"));
        Assert.NotNull(rig.View.Last.SavedTake);
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("Pause and enable", StringComparison.Ordinal));
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));

        rig.Dictate(Inserted);
    }

    [Fact]
    public async Task A_restart_started_before_the_queued_result_keeps_the_worker_stopped_message()
    {
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        using var load = new ManualResetEventSlim();
        await rig.EnableAsync();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.Platform.Worker.Ops().Contains("transcribe")
            && rig.View.Last.Status == DictationCopy.Transcribing);

        try
        {
            // The decode's one retry (LIMIT-WIN-A) dies too, leaving the
            // worker dead behind the result.
            rig.Platform.Worker.DieOnTranscribe = true;
            rig.Platform.Worker.Crash();
            // The session posts Armed, then its result. Leave both queued:
            // a UI timer can notice the dead worker before those callbacks.
            Assert.True(SpinWait.SpinUntil(() => rig.Dispatcher.Pending >= 2,
                TimeSpan.FromSeconds(10)));
            rig.Platform.Worker.DieOnTranscribe = false;
            rig.Platform.Worker.HoldInit = load;
            rig.Controller.Tick();
            // The first worker, the retry's, and the timer's restart.
            Assert.Equal(3, rig.Platform.Worker.Starts);
            rig.Dispatcher.Drain();

            Assert.Equal(DictationCopy.TakeSavedWorkerRestarting, rig.View.Last.Status);
            Assert.Equal(DictationCopy.HudTakeSaved, rig.Hud.Shows[^1].Text);
        }
        finally
        {
            decode.Set();
            load.Set();
        }

        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));
        Assert.Equal(2, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task Restarts_are_bounded_and_then_the_window_says_what_to_do()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Worker.InitSucceeds = false;

        rig.Platform.Worker.Crash();
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestartFailed);

        Assert.Equal(1 + 3, rig.Platform.CreateWorkerCalls);
        Assert.Contains(DictationCopy.OpenLogFolder, rig.View.Last.Status, StringComparison.Ordinal);

        // And it stops there rather than looping.
        for (var i = 0; i < 20; i++)
        {
            rig.Controller.Tick();
            rig.Dispatcher.Drain();
        }

        Assert.Equal(4, rig.Platform.CreateWorkerCalls);
        Assert.Equal(DictationCopy.WorkerRestartFailed, rig.View.Last.Status);
    }

    [Fact]
    public async Task A_worker_that_keeps_dying_without_a_dictation_between_is_given_up_on()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        for (var crash = 1; crash <= 3; crash++)
        {
            rig.Platform.Worker.Crash();
            rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey)
                && rig.Platform.CreateWorkerCalls == crash + 1);
        }

        rig.Platform.Worker.Crash();
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestartFailed);
        Assert.Equal(4, rig.Platform.CreateWorkerCalls);

        // Said once: the dead worker is not rediscovered on every tick.
        var rendered = rig.View.States.Count;
        for (var i = 0; i < 20; i++)
        {
            rig.Controller.Tick();
            rig.Dispatcher.Drain();
        }

        Assert.Equal(rendered, rig.View.States.Count);
    }

    [Fact]
    public async Task A_dictation_that_reaches_the_recogniser_renews_the_restart_budget()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        for (var crash = 1; crash <= 4; crash++)
        {
            rig.Platform.Worker.Crash();
            rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));
            rig.Dictate(Inserted);
        }

        Assert.Equal(5, rig.Platform.CreateWorkerCalls);
    }

    /// <summary>
    /// A rig with one restart attempt, whose worker died and whose attempt is
    /// loading — held on <paramref name="load"/> — and bound to fail. One
    /// attempt, so that attempt is the last: a report of it that ran on would
    /// give up restarting, where an earlier one would only schedule another.
    /// </summary>
    private static async Task<Rig> DuringTheLastRestartAttempt(ManualResetEventSlim load)
    {
        var rig = new Rig(options: o => o with { WorkerRestartAttempts = 1 });
        await rig.EnableAsync();
        rig.Platform.Worker.InitSucceeds = false;
        rig.Platform.Worker.HoldInit = load;
        rig.Platform.Worker.Crash();
        rig.TickUntil(() => rig.Platform.Worker.Ops().Count(op => op == "init") == 2);
        return rig;
    }

    /// <summary>Pause, and let the attempt in flight end and report back —
    /// queued behind the pause, not yet run.</summary>
    private static void PauseAndLetTheAttemptReport(Rig rig, ManualResetEventSlim load)
    {
        // The pause cancels the attempt, which may then report at once, on
        // the pool. Its report is posted after its model load is over, so the
        // count is taken there rather than after the pause returns.
        var postedWhenOver = -1;
        rig.Platform.OnStartupFinished = () => Volatile.Write(ref postedWhenOver, rig.Dispatcher.Posted);
        rig.Controller.Pause();
        load.Set();
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref postedWhenOver) >= 0 && rig.Dispatcher.Posted > Volatile.Read(ref postedWhenOver),
            TimeSpan.FromSeconds(10)));
        rig.Platform.OnStartupFinished = null;
    }

    [Fact]
    public async Task Pausing_during_the_last_restart_attempt_leaves_the_window_paused()
    {
        using var load = new ManualResetEventSlim();
        using var rig = await DuringTheLastRestartAttempt(load);

        PauseAndLetTheAttemptReport(rig, load);
        rig.Dispatcher.Drain();

        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
        Assert.DoesNotContain(rig.View.States, s => s.Status == DictationCopy.WorkerRestartFailed);
        Assert.False(rig.Controller.IsEnabled);
    }

    [Fact]
    public async Task An_enable_right_after_pausing_mid_restart_keeps_its_new_worker()
    {
        using var load = new ManualResetEventSlim();
        using var reload = new ManualResetEventSlim();
        using var rig = await DuringTheLastRestartAttempt(load);
        PauseAndLetTheAttemptReport(rig, load);

        // Enabled again at once: the new worker is loading when the old
        // attempt's report runs.
        rig.Platform.Worker.InitSucceeds = true;
        rig.Platform.Worker.HoldInit = reload;
        var enabling = rig.Controller.EnableAsync();
        rig.DrainUntil(() => rig.Platform.Worker.Ops().Count(op => op == "init") == 3);
        rig.Dispatcher.Drain();
        reload.Set();
        await enabling;
        rig.Dispatcher.Drain();

        Assert.True(rig.Controller.IsEnabled);
        Assert.True(rig.Platform.Worker.IsAlive);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.Equal(3, rig.Platform.CreateWorkerCalls);
        rig.Dictate(Inserted);
    }

    // ─── Idle unload ────────────────────────────────────────────────────

    [Fact]
    public async Task An_idle_model_is_unloaded_after_three_minutes_and_the_next_press_loads_it()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var armed = rig.View.Last.Status;

        rig.Clock.Advance(TimeSpan.FromSeconds(179));
        rig.Controller.Tick();
        Assert.True(rig.Platform.Worker.IsAlive);

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Controller.Tick();
        Assert.False(rig.Platform.Worker.IsAlive);

        // Unloading is not a crash: nothing restarts it and nothing is said.
        for (var i = 0; i < 10; i++)
        {
            rig.Controller.Tick();
        }

        rig.Dispatcher.Drain();
        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
        Assert.Equal(armed, rig.View.Last.Status);

        // The press starts it while the user speaks, and the words are kept.
        rig.Press();
        Assert.Equal(2, rig.Platform.CreateWorkerCalls);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.DoesNotContain(rig.View.States, s => s.Status.Contains("stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_idle_clock_starts_when_a_dictation_ends()
    {
        // A long decode: pressed at 0, finished at 170 s. Three minutes of
        // idle are counted from the end, not the press.
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        rig.Clock.Advance(TimeSpan.FromSeconds(170));
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        rig.Clock.Advance(TimeSpan.FromSeconds(170));
        rig.Controller.Tick();

        Assert.True(rig.Platform.Worker.IsAlive);
        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task An_engine_that_refuses_one_sentence_is_not_restarted()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Worker.RefuseTranscribe = true;

        // A refusal is not retried (the worker is alive and said no), and its
        // take is kept for Recover last dictation (LIMIT-WIN-A).
        rig.Dictate(v => v.Status == DictationCopy.TakeSaved);
        Assert.Equal(1, rig.Platform.Worker.Ops().Count(op => op == "transcribe"));
        Assert.NotNull(rig.View.Last.SavedTake);
        for (var i = 0; i < 5; i++)
        {
            rig.Controller.Tick();
            rig.Dispatcher.Drain();
        }

        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
        Assert.DoesNotContain(rig.View.States, s => s.Status == DictationCopy.WorkerStopped);
    }

    [Fact]
    public async Task A_model_is_not_unloaded_under_a_dictation()
    {
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Controller.Tick();

        Assert.True(rig.Platform.Worker.IsAlive);
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Fact]
    public async Task After_giving_up_the_next_press_tries_once_more()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Worker.InitSucceeds = false;
        rig.Platform.Worker.Crash();
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestartFailed);

        rig.Platform.Worker.InitSucceeds = true;
        rig.Dictate(Inserted);

        Assert.Equal(5, rig.Platform.CreateWorkerCalls);
    }

    // ─── A press between two restart attempts ───────────────────────────

    /// <summary>The dictation is over, whichever way it went.</summary>
    private static bool Settled(DictationViewState v) =>
        Inserted(v)
        || v.Status.Contains("nothing was inserted", StringComparison.Ordinal)
        || v.Status.StartsWith("Speech recognition failed", StringComparison.Ordinal);

    /// <summary>
    /// The worker died and the first restart failed, so the second attempt is
    /// waiting out its delay — until <paramref name="retry"/> completes. The
    /// rig's usual zero delay would run the attempt before any press could
    /// land in between.
    /// </summary>
    private static async Task<Rig> BetweenRestartAttempts(TaskCompletionSource retry)
    {
        var waits = 0;
        var rig = new Rig(options: o => o with
        {
            RestartWait = (_, ct) =>
            {
                Interlocked.Increment(ref waits);
                return retry.Task.WaitAsync(ct);
            },
        });
        await rig.EnableAsync();
        rig.Platform.Worker.InitSucceeds = false;
        rig.Platform.Worker.Crash();
        rig.TickUntil(() => Volatile.Read(ref waits) == 1);
        Assert.Equal(2, rig.Platform.CreateWorkerCalls);
        rig.Platform.Worker.InitSucceeds = true;
        return rig;
    }

    /// <summary>Complete the wait and run the attempt it releases. Called
    /// with nothing else left to post, so the post waited for is the attempt's.</summary>
    private static void ComeDue(Rig rig, TaskCompletionSource retry)
    {
        var posted = rig.Dispatcher.Posted;
        retry.SetResult();
        rig.DrainUntil(() => rig.Dispatcher.Posted > posted);
        rig.Dispatcher.Drain();
    }

    [Fact]
    public async Task A_restart_that_comes_due_during_a_decode_leaves_the_worker_the_press_loaded()
    {
        var retry = new TaskCompletionSource();
        using var decode = new ManualResetEventSlim();
        using var rig = await BetweenRestartAttempts(retry);
        rig.Platform.Worker.HoldTranscribe = decode;

        // The press loads a worker for its own hold, and the words reach it.
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.Platform.Worker.Ops().Contains("transcribe")
            && rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.Equal(3, rig.Platform.CreateWorkerCalls);
        var mark = rig.View.States.Count;

        ComeDue(rig, retry);
        decode.Set();
        rig.DrainUntil(() => rig.View.States.Skip(mark).Any(Settled));

        Assert.True(Inserted(rig.View.Last), rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Equal(3, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_restart_that_comes_due_while_the_press_is_loading_joins_that_load()
    {
        var retry = new TaskCompletionSource();
        using var load = new ManualResetEventSlim();
        using var rig = await BetweenRestartAttempts(retry);
        rig.Platform.Worker.HoldInit = load;

        // The press starts loading a worker; the release waits for it.
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.Platform.Worker.Ops().Count(op => op == "init") == 3
            && rig.View.Last.Status == DictationCopy.Transcribing);
        var mark = rig.View.States.Count;

        ComeDue(rig, retry);
        load.Set();
        rig.DrainUntil(() => rig.View.States.Skip(mark).Any(Settled));

        Assert.True(Inserted(rig.View.Last), rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Equal(3, rig.Platform.CreateWorkerCalls);
    }

    // ─── Language ───────────────────────────────────────────────────────

    private static IReadOnlyList<string?> TranscribeLanguages(Rig rig) =>
        rig.Platform.Worker.Frames
            .Select(f => JsonDocument.Parse(f).RootElement)
            .Where(f => f.GetProperty("op").GetString() == "transcribe")
            .Select(f => f.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()
                : null)
            .ToArray();

    [Fact]
    public void The_picker_offers_automatic_first_and_ten_languages()
    {
        using var rig = new Rig();

        Assert.Equal(11, rig.Controller.LanguageChoices.Count);
        Assert.Same(SpeechLanguages.Automatic, rig.Controller.LanguageChoices[0]);
        Assert.Same(SpeechLanguages.Automatic, rig.View.Last.Language);
        Assert.Equal(
            ["en", "zh", "ja", "ko", "es", "fr", "de", "pt", "it", "ru"],
            rig.Controller.LanguageChoices.Skip(1).Select(c => c.Code));
    }

    [Fact]
    public async Task Automatic_sends_no_language()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Dictate(Inserted);

        Assert.Equal([null], TranscribeLanguages(rig));
    }

    [Fact]
    public async Task A_chosen_language_is_remembered_and_sent_with_every_dictation()
    {
        using var rig = new Rig();
        rig.Controller.ChooseLanguage("ja");

        Assert.Equal("ja", rig.Settings.Load().Language);
        Assert.Equal("Earlet now expects Japanese (日本語).", rig.View.Last.Status);

        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.Equal(["ja"], TranscribeLanguages(rig));

        using var next = new Rig(beforeStart: r => r.Settings.Save(rig.Settings.Load()));
        Assert.Equal("ja", next.View.Last.Language.Code);
    }

    [Fact]
    public async Task Changing_the_language_while_on_reaches_the_next_decode_without_a_restart()
    {
        // The engine takes the language per request: the loaded worker must
        // not be restarted, and the next hold's frame carries the new code.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);

        rig.Controller.ChooseLanguage("de");
        rig.Dictate(v => Inserted(v) && TranscribeLanguages(rig).Contains("de"));

        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
        Assert.Equal("de", rig.Settings.Load().Language);
        Assert.Equal([null, "de"], TranscribeLanguages(rig));

        // And back to automatic, still with no restart.
        rig.Controller.ChooseLanguage(null);
        rig.Dictate(v => Inserted(v) && TranscribeLanguages(rig).Count == 3);
        Assert.Equal([null, "de", null], TranscribeLanguages(rig));
        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task Going_back_to_automatic_sends_no_language_to_a_worker_started_with_one()
    {
        // The worker was launched with Japanese in its options. Automatic is a
        // null language, and that null must reach the next decode as "detect
        // it" — not fall back to the language the worker was started with.
        using var rig = new Rig();
        rig.Controller.ChooseLanguage("ja");
        await rig.EnableAsync();
        rig.Dictate(Inserted);

        rig.Controller.ChooseLanguage(null);
        rig.Dictate(v => Inserted(v) && TranscribeLanguages(rig).Count == 2);

        Assert.Equal(["ja", null], TranscribeLanguages(rig));
        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_language_chosen_while_the_model_is_unloaded_is_the_one_the_next_load_starts_with()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(3));
        rig.Controller.Tick();
        Assert.False(rig.Platform.Worker.IsAlive);

        rig.Controller.ChooseLanguage("fr");
        rig.Dictate(v => Inserted(v) && TranscribeLanguages(rig).Count == 1);

        Assert.Equal(2, rig.Platform.CreateWorkerCalls);
        Assert.Equal("fr", rig.Platform.Plans[^1].Language);
        Assert.Equal(["fr"], TranscribeLanguages(rig));
    }

    [Fact]
    public async Task The_language_cannot_change_under_a_dictation()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        Assert.False(rig.View.Last.CanChooseLanguage);
        rig.Controller.ChooseLanguage("fr");

        Assert.Null(rig.Settings.Load().Language);
        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public void A_language_the_picker_does_not_offer_is_ignored()
    {
        using var rig = new Rig();
        rig.Controller.ChooseLanguage("es");

        rig.Controller.ChooseLanguage("tlh");

        Assert.Equal("es", rig.Settings.Load().Language);
        Assert.Equal("es", rig.View.Last.Language.Code);
    }

    [Fact]
    public void A_language_that_cannot_be_saved_still_applies_and_says_it_will_not_last()
    {
        using var rig = new Rig(beforeStart: r => Directory.CreateDirectory(r.Settings.Path));

        rig.Controller.ChooseLanguage("ko");

        Assert.Equal("ko", rig.View.Last.Language.Code);
        Assert.Equal(DictationCopy.LanguageNotSaved, rig.View.Last.Status);
    }
}
