using System.Text.Json;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// <see cref="DictationWorkerSlot"/> on its own: one start at a time, a stop
/// that beats a start in flight, and a transcription that waits for a load
/// rather than refusing the words.
/// </summary>
public sealed class DictationWorkerSlotTests : IDisposable
{
    private readonly InstalledCopy _copy = new();
    private readonly FakePlatform _platform = new();

    private DictationWorkerSlot Slot() =>
        new(_platform, NullLoggerFactory.Instance, DictationWorkerPlan.Resolve(_copy.Root, _ => null));

    private static MicrophoneCapture Audio() => new(new short[16000], 16000, 1, TimeSpan.FromSeconds(1));

    public void Dispose()
    {
        _platform.Worker.Dispose();
        _copy.Dispose();
    }

    [Fact]
    public async Task A_fake_process_crash_cannot_close_the_replacement_pipes()
    {
        using var worker = new FakeSpeechWorker();
        await using var process = worker.CreateProcess();
        await process.StartAsync(CancellationToken.None);
        worker.BeforeCrashPipesClose = () =>
        {
            // Force the same interleaving as a host retry after the old pipe
            // closes. The fake starts synchronously, before cleanup resumes.
            Assert.True(process.StartAsync(CancellationToken.None).IsCompletedSuccessfully);
        };

        worker.Crash();
        worker.BeforeCrashPipesClose = null;

        // Observe the replacement's real framed exchange, not just IsAlive.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await AsrFraming.WriteFrameAsync(process.StandardIn, new { op = "init" }, deadline.Token);
        var reply = await AsrFraming.ReadFrameAsync<AsrResponse>(process.StandardOut, deadline.Token);
        Assert.NotNull(reply);
        Assert.True(reply.Ok);
    }

    [Fact]
    public async Task Two_callers_share_one_start()
    {
        using var slot = Slot();
        // The model load is held, so the second call finds the first start
        // still in flight. Unheld, a test thread preempted between the calls
        // could find it finished and get a fresh completed task back.
        using var init = new ManualResetEventSlim();
        _platform.Worker.HoldInit = init;

        var first = slot.StartAsync();
        var second = slot.StartAsync();

        Assert.Same(first, second);
        Assert.False(first.IsCompleted, "the start finished while its model load was held");
        init.Set();
        Assert.True(await first);
        Assert.Equal(1, _platform.CreateWorkerCalls);
        Assert.True(slot.IsReady);

        // Ready already: nothing is started again.
        Assert.True(await slot.StartAsync());
        Assert.Equal(1, _platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_model_that_will_not_load_leaves_nothing_installed()
    {
        using var slot = Slot();
        _platform.Worker.InitSucceeds = false;

        Assert.False(await slot.StartAsync());

        Assert.False(slot.IsLoaded);
        Assert.False(slot.IsStarting);
        Assert.False(_platform.Worker.IsAlive);
    }

    [Fact]
    public async Task A_worker_that_cannot_be_created_fails_the_start_with_its_error()
    {
        using var slot = Slot();
        _platform.WorkerFailure = new InvalidOperationException("no interpreter");

        await Assert.ThrowsAsync<InvalidOperationException>(slot.StartAsync);
        Assert.False(slot.IsStarting);
    }

    [Fact]
    public async Task Unloading_during_a_start_wins_and_the_late_worker_is_not_installed()
    {
        using var slot = Slot();
        using var init = new ManualResetEventSlim();
        _platform.Worker.HoldInit = init;

        var start = slot.StartAsync();
        slot.Unload();
        init.Set();

        Assert.False(await start);
        Assert.False(slot.IsLoaded);
        Assert.False(slot.IsStarting);
    }

    [Fact]
    public async Task A_stop_after_the_model_loaded_but_before_it_was_installed_still_wins()
    {
        // The load finished without seeing the cancel; only the slot's own
        // bookkeeping can tell that the start it belongs to was stopped.
        using var slot = Slot();
        _platform.OnStartupFinished = slot.Unload;

        Assert.False(await slot.StartAsync());

        Assert.False(slot.IsLoaded);
        Assert.False(_platform.Worker.IsAlive);
    }

    [Fact]
    public async Task A_transcription_with_nothing_loaded_starts_the_worker_and_keeps_the_words()
    {
        using var slot = Slot();

        var result = await slot.TranscribeAsync(Audio(), default);

        Assert.Equal(TranscriptionOutcome.Transcribed, result.Outcome);
        Assert.Equal(1, _platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_transcription_waits_for_the_start_already_running()
    {
        using var slot = Slot();
        using var init = new ManualResetEventSlim();
        _platform.Worker.HoldInit = init;

        var start = slot.StartAsync();
        var transcription = slot.TranscribeAsync(Audio(), default);
        init.Set();

        Assert.True(await start);
        Assert.Equal(TranscriptionOutcome.Transcribed, (await transcription).Outcome);
        Assert.Equal(1, _platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_disposed_slot_starts_nothing_and_transcribes_nothing()
    {
        var slot = Slot();
        Assert.True(await slot.StartAsync());

        slot.Dispose();

        Assert.False(_platform.Worker.IsAlive);
        Assert.False(await slot.StartAsync());
        Assert.Equal(
            TranscriptionOutcome.RecognizerUnavailable,
            (await slot.TranscribeAsync(Audio(), default)).Outcome);
        Assert.Equal(1, _platform.CreateWorkerCalls);
    }

    private IReadOnlyList<string?> TranscribeLanguages() =>
        _platform.Worker.Frames
            .Select(f => JsonDocument.Parse(f).RootElement)
            .Where(f => f.GetProperty("op").GetString() == "transcribe")
            .Select(f => f.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()
                : null)
            .ToArray();

    [Fact]
    public async Task A_language_change_reaches_the_next_decode_with_no_restart_and_the_next_start_too()
    {
        using var slot = Slot();
        Assert.True(await slot.StartAsync());

        // Live: the worker already loaded decodes the next request with it.
        slot.Language = "de";
        Assert.Equal(TranscriptionOutcome.Transcribed, (await slot.TranscribeAsync(Audio(), default)).Outcome);
        Assert.Equal(1, _platform.CreateWorkerCalls);

        // Kept in step with the plan, so a later start launches with it.
        Assert.Equal("de", slot.Plan.Language);
        Assert.True(await slot.RestartAsync());
        Assert.Equal([null, "de"], _platform.Plans.Select(p => p.Language));

        // And the restarted worker takes the next change live as well.
        slot.Language = "fr";
        Assert.Equal(TranscriptionOutcome.Transcribed, (await slot.TranscribeAsync(Audio(), default)).Outcome);

        Assert.Equal(["de", "fr"], TranscribeLanguages());
        Assert.Equal(2, _platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_restart_uses_the_plan_as_it_is_now()
    {
        using var slot = Slot();
        Assert.True(await slot.StartAsync());

        slot.Language = "fr";
        Assert.True(await slot.RestartAsync());

        Assert.Equal([null, "fr"], _platform.Plans.Select(p => p.Language));
    }
}
