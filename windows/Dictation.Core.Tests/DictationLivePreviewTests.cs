using System.Collections.Concurrent;
using System.Text.Json;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// The live preview's pieces on their own: the Mac's
/// <c>DictationLivePreviewTests</c> ported scenario for scenario — the wire
/// the worker slot speaks, the caption's text and window, the session's
/// bounded push, its ordered stop and its three-second backlog — and what
/// the Windows worker slot adds: a preview never starts a worker, waits for
/// no load, and a stream dies with the worker that opened it.
/// </summary>
/// <remarks>
/// The Mac's "capture publishes post-trigger chunks but never warm pre-roll"
/// is <see cref="LivePreviewTests.Audio_before_the_press_never_reaches_the_stream"/>:
/// the Windows capture has no warm pre-roll, and the preview is fed only from
/// the admitted press.
/// </remarks>
public sealed class DictationLivePreviewTests : IDisposable
{
    private readonly InstalledCopy _copy = new();
    private readonly FakePlatform _platform = new();

    private DictationWorkerSlot Slot(string? language = null) =>
        new(_platform, NullLoggerFactory.Instance,
            DictationWorkerPlan.Resolve(_copy.Root, _ => null) with { Language = language });

    public void Dispose()
    {
        _platform.Worker.Dispose();
        _copy.Dispose();
    }

    // ─── The Mac's scenarios (DictationLivePreviewTests.swift) ──────────

    [Fact]
    public async Task The_worker_transport_speaks_the_shared_stream_wire_and_never_finishes_it()
    {
        _platform.Worker.PreviewReply = "[You] settled\n[You] newest words";
        using var slot = Slot();
        Assert.True(await slot.StartAsync());
        slot.SetVocabularyScene("terminal");

        var streamId = await slot.StreamStartAsync();
        Assert.NotNull(streamId);
        var hypothesis = await slot.StreamPushAsync(streamId!, new short[] { 8192, -8192 });
        Assert.Equal("newest words", hypothesis?.Text);
        await slot.StreamCancelAsync(streamId!);

        var start = Assert.Single(_platform.Worker.FramesOf("stream_start"));
        Assert.Equal("whisper", start.GetProperty("engine").GetString());
        Assert.Equal("dictation", start.GetProperty("client").GetString());
        Assert.Equal(16_000, start.GetProperty("sample_rate").GetInt32());
        Assert.False(start.GetProperty("rolling_stable").GetBoolean());
        Assert.Equal(["zh", "en"], start.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal("terminal", start.GetProperty("vocabulary_scene").GetString());
        Assert.False(start.TryGetProperty("model_dir", out _));
        var push = Assert.Single(_platform.Worker.FramesOf("stream_push"));
        Assert.Equal(streamId, push.GetProperty("stream_id").GetString());
        Assert.Equal("mic", push.GetProperty("channel").GetString());
        Assert.Equal(4, Convert.FromBase64String(push.GetProperty("pcm_b64").GetString()!).Length);
        Assert.Equal(streamId, Assert.Single(_platform.Worker.FramesOf("stream_cancel")).GetProperty("stream_id").GetString());
        Assert.DoesNotContain("stream_finish", _platform.Worker.Ops());
    }

    [Fact]
    public async Task A_pinned_language_is_the_streams_only_language()
    {
        using var slot = Slot(language: "ja");
        Assert.True(await slot.StartAsync());

        Assert.NotNull(await slot.StreamStartAsync());

        var start = Assert.Single(_platform.Worker.FramesOf("stream_start"));
        Assert.Equal(["ja"], start.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        Assert.False(start.TryGetProperty("vocabulary_scene", out _));
        Assert.Equal(["zh", "en"], AsrWorkerTranscriber.PreviewLanguages(null));
        Assert.Equal(["zh", "en"], AsrWorkerTranscriber.PreviewLanguages("  "));
        Assert.Equal(["en"], AsrWorkerTranscriber.PreviewLanguages(" EN "));
    }

    [Fact]
    public void Speaker_rendering_keeps_only_the_newest_unlabelled_line()
    {
        Assert.Equal("current words", PreviewText.LiveTail("[You] settled words\n[You] current words"));
        Assert.Equal("[unfinished words", PreviewText.LiveTail("[unfinished words"));
        Assert.Equal("words [inside] stay", PreviewText.LiveTail("words [inside] stay"));
        Assert.Equal(string.Empty, PreviewText.LiveTail("\n  \n"));
    }

    [Fact]
    public void Caption_uses_independent_seven_word_and_sixteen_CJK_bounds()
    {
        Assert.Equal("two three four five six seven eight",
            DictationPreviewCaption.Window("one two three four five six seven eight"));
        Assert.Equal("五六七八九十一二三四五六七八九十",
            DictationPreviewCaption.Window("一二三四五六七八九十一二三四五六七八九十"));
        Assert.Equal("two three four five six seven eight 我觉得这个方案没有任何问题",
            DictationPreviewCaption.Window("one two three four five six seven eight 我觉得这个方案没有任何问题"));
        Assert.Equal("QNN我觉得没问题", DictationPreviewCaption.Window("QNN我觉得没问题"));
        Assert.Equal(string.Empty, DictationPreviewCaption.Window("You…"));
    }

    [Fact]
    public async Task One_bounded_push_updates_the_preview_and_stop_cancels_then_clears()
    {
        var transport = new PreviewTransportSpy();
        var log = new MemoryLog();
        var texts = new ConcurrentQueue<string>();
        using var session = new DictationPreviewSession(transport, log.CreateLogger<DictationPreviewSession>());
        session.PreviewUpdated += (_, update) => texts.Enqueue(update.Text);

        session.Start();
        Assert.True(Eventually(() => transport.Starts == 1));
        session.Append(new AudioChunk(Enumerable.Repeat(0.1f, 2_560).ToArray(), 16_000, 1));
        Assert.True(Eventually(() => texts.Any(t => DictationPreviewCaption.Window(t) == "two three four five six seven eight")));
        await session.StopAsync();

        Assert.Equal(1, transport.Pushes);
        Assert.Equal(["preview-1"], transport.Cancels);
        Assert.Equal(string.Empty, texts.Last());
        Assert.Equal(1, session.LastMetrics?.PushCount);
        Assert.Equal("one two three four five six seven eight".Length, session.LastMetrics?.LastPreviewLength);
        var profile = Assert.Single(log.Lines);
        Assert.Contains("pushes 1;", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("two three", profile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_racing_a_late_open_cancels_the_orphaned_worker_stream()
    {
        var transport = new DelayedPreviewOpen();
        var texts = new ConcurrentQueue<string>();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.PreviewUpdated += (_, update) => texts.Enqueue(update.Text);
        session.Start();
        await transport.Waiting.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = session.StopAsync();
        Assert.False(stop.IsCompleted);
        transport.Complete("late-stream");
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        // Awaited, the stop is over only once the late stream is cancelled.
        Assert.Equal(["late-stream"], transport.Cancels);
        Assert.Equal(string.Empty, texts.Last());
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public async Task Audio_waiting_on_a_slow_open_stays_bounded_to_three_seconds()
    {
        var transport = new DelayedPreviewOpen();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.Start();
        await transport.Waiting.WaitAsync(TimeSpan.FromSeconds(10));
        session.Append(new AudioChunk(Enumerable.Repeat(0.1f, 80_000).ToArray(), 16_000, 1));

        var stop = session.StopAsync();
        transport.Complete(null);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(32_000, session.LastMetrics?.DroppedSampleCount);
        Assert.Equal(0, session.LastMetrics?.PushCount);
    }

    // ─── The session: what Windows adds ─────────────────────────────────

    [Fact]
    public async Task The_backlog_is_the_newest_three_seconds_at_the_device_rate()
    {
        // 48 kHz stereo, five seconds while the stream opens: the oldest two
        // seconds go, and the push that follows the open carries the newest
        // three, resampled to 16 kHz.
        var transport = new DelayedPreviewOpen();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.Start();
        await transport.Waiting.WaitAsync(TimeSpan.FromSeconds(10));
        var samples = new float[48_000 * 5 * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i < 48_000 * 2 * 2 ? 0.9f : 0.1f;
        }

        session.Append(new AudioChunk(samples, 48_000, 2));
        transport.Complete("slow-stream");
        Assert.True(Eventually(() => transport.Pushed.Count == 1));
        var pushed = transport.Pushed.Single();
        Assert.InRange(pushed.Length, 47_500, 48_500);
        // Nothing of the loud, oldest two seconds is left.
        Assert.All(pushed, sample => Assert.InRange(sample, (short)0, (short)(0.2 * short.MaxValue)));

        await session.StopAsync();
        Assert.Equal(96_000, session.LastMetrics?.DroppedSampleCount);
    }

    [Fact]
    public async Task A_stop_while_a_push_is_on_the_wire_cancels_after_it()
    {
        var transport = new HeldPush();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.Start();
        Assert.True(Eventually(() => session.IsStreaming));
        session.Append(new AudioChunk(new float[16_000], 16_000, 1));
        await transport.PushEntered.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = session.StopAsync();
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);
        Assert.DoesNotContain("cancel", transport.Events);

        transport.ReleasePush();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["push", "cancel"], transport.Events);
    }

    [Fact]
    public async Task Audio_that_arrived_during_a_push_goes_as_soon_as_it_answers()
    {
        var transport = new HeldPush();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.Start();
        Assert.True(Eventually(() => session.IsStreaming));
        session.Append(new AudioChunk(new float[16_000], 16_000, 1));
        await transport.PushEntered.WaitAsync(TimeSpan.FromSeconds(10));
        session.Append(new AudioChunk(new float[8_000], 16_000, 1));

        // No further audio: the half second that waited goes with the reply,
        // as on the Mac, not with the next buffer.
        transport.ReleasePush();
        Assert.True(Eventually(() => transport.Pushes == 2));
        Assert.Equal(8_000, transport.Lengths.Last());
        await session.StopAsync();
    }

    [Fact]
    public async Task A_reply_that_lands_after_the_stop_puts_nothing_back_on_screen()
    {
        var transport = new HeldPush();
        var texts = new ConcurrentQueue<string>();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.PreviewUpdated += (_, update) => texts.Enqueue(update.Text);
        session.Start();
        Assert.True(Eventually(() => session.IsStreaming));
        session.Append(new AudioChunk(new float[16_000], 16_000, 1));
        await transport.PushEntered.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = session.StopAsync();
        transport.ReleasePush();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([string.Empty], texts);
        Assert.Equal(0, session.LastMetrics?.PartialCount);
    }

    [Fact]
    public async Task A_second_stop_is_the_first_one()
    {
        var transport = new DelayedPreviewOpen();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.Start();
        await transport.Waiting.WaitAsync(TimeSpan.FromSeconds(10));

        var first = session.StopAsync();
        Assert.Same(first, session.StopAsync());
        transport.Complete("late");
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(transport.Cancels);
        Assert.True(session.StopAsync().IsCompleted);
    }

    [Fact]
    public async Task The_first_caption_and_its_changes_count_what_is_drawn_as_on_the_Mac()
    {
        // A lone "you" draws nothing (DictationPreviewCaption.Window), so it
        // is no first caption; the same words again are no change.
        var transport = new ScriptedPush("You", "hello there", "hello there");
        var texts = new ConcurrentQueue<string>();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.PreviewUpdated += (_, update) => texts.Enqueue(update.Text);

        session.Start();
        Assert.True(Eventually(() => session.IsStreaming));
        session.Append(new AudioChunk(new float[2_560], 16_000, 1));
        Assert.True(Eventually(() => texts.Count == 1));
        await session.StopAsync();

        var heard = session.LastMetrics!;
        Assert.Equal(1, heard.PartialCount);
        Assert.Null(heard.FirstPartialMs);
        Assert.Equal(0, heard.ChangedPartialCount);
        Assert.Equal(0, heard.LastCaptionLength);
        Assert.Equal(3, heard.LastPreviewLength);

        session.Start();
        Assert.True(Eventually(() => session.IsStreaming));
        texts.Clear();
        for (var i = 1; i <= 2; i++)
        {
            session.Append(new AudioChunk(new float[2_560], 16_000, 1));
            Assert.True(Eventually(() => texts.Count == i));
        }

        await session.StopAsync();
        var drawn = session.LastMetrics!;
        Assert.Equal(2, drawn.PartialCount);
        Assert.NotNull(drawn.FirstPartialMs);
        Assert.Equal(1, drawn.ChangedPartialCount);
        Assert.Null(drawn.ChangedGapP50Ms);
        Assert.Equal("hello there".Length, drawn.LastCaptionLength);
    }

    [Fact]
    public async Task Every_update_names_the_run_it_belongs_to_however_late_it_is_raised()
    {
        var transport = new PreviewTransportSpy();
        var log = new HeldProfileLog();
        var updates = new ConcurrentQueue<DictationPreviewUpdate>();
        using var session = new DictationPreviewSession(transport, log);
        session.PreviewUpdated += (_, update) => updates.Enqueue(update);

        var first = session.Start();
        Assert.True(first > 0);
        Assert.Equal(first, session.Start());
        Assert.True(Eventually(() => session.IsStreaming));
        session.Append(new AudioChunk(new float[2_560], 16_000, 1));
        Assert.True(Eventually(() => !updates.IsEmpty));
        Assert.All(updates, update => Assert.Equal(first, update.Run));

        // The first run's stop is held between its lock and its event while
        // the next run starts: the event still names the first run.
        try
        {
            var stop = Task.Run(() => session.StopAsync());
            Assert.True(log.Entered.Wait(TimeSpan.FromSeconds(5)));
            var second = session.Start();
            Assert.True(second > first);
            log.Release();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new DictationPreviewUpdate(first, string.Empty), updates.Last());
        }
        finally
        {
            log.Release();
        }
    }

    [Fact]
    public void A_stop_cancels_on_the_pool_not_on_the_callers_thread_nor_under_the_lock()
    {
        // A stop comes from the UI thread and from the release's pipeline; a
        // cancel is a frame on the worker's pipe, and Append needs the lock.
        // The cancel is held until the finally below, so whatever returns while
        // it is still held did not wait for it to finish. That order check
        // cannot see a Stop that waits for the cancel for a bounded time
        // shorter than the hold, so Stop also has a wall-clock limit: it only
        // takes the lock and queues the cancel, and the 1 s limit this
        // replaced never failed under the load that made Append's limit flaky.
        var transport = new BlockingCancel();
        using var session = new DictationPreviewSession(transport, NullLogger<DictationPreviewSession>.Instance);
        session.Start();
        Assert.True(Eventually(() => session.IsStreaming));
        try
        {
            var stopping = System.Diagnostics.Stopwatch.StartNew();
            session.Stop();
            stopping.Stop();
            Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(2), $"Stop took {stopping.Elapsed}");
            Assert.False(transport.Exited, "Stop returned only after the cancel did");
            Assert.True(transport.Entered.Wait(TimeSpan.FromSeconds(10)), "the cancel never ran");
            Assert.NotEqual(Environment.CurrentManagedThreadId, transport.CancelThread);

            session.Start();
            // On a thread of its own: a pool busy with other tests' waits must
            // not make an Append that is free to run look blocked.
            var append = Task.Factory.StartNew(
                () => session.Append(new AudioChunk(new float[16], 16_000, 1)),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(append.Wait(TimeSpan.FromSeconds(10)), "Append is waiting for the lock");
            Assert.False(transport.Exited, "Append returned only after the cancel did");
        }
        finally
        {
            transport.Release();
        }
    }

    // ─── The worker slot ────────────────────────────────────────────────

    [Fact]
    public async Task A_preview_never_starts_a_worker()
    {
        using var slot = Slot();

        Assert.Null(await slot.StreamStartAsync());

        Assert.Equal(0, _platform.CreateWorkerCalls);
        Assert.Empty(_platform.Worker.Frames);
    }

    [Fact]
    public async Task A_preview_waits_for_no_load()
    {
        using var slot = Slot();
        using var init = new ManualResetEventSlim();
        _platform.Worker.HoldInit = init;
        var start = slot.StartAsync();
        Assert.True(Eventually(() => _platform.Worker.Ops().Contains("init")));

        Assert.Null(await slot.StreamStartAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        init.Set();
        Assert.True(await start);
        Assert.NotNull(await slot.StreamStartAsync());
        Assert.Equal(1, _platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_worker_that_died_opens_no_stream_and_is_not_restarted_for_one()
    {
        using var slot = Slot();
        Assert.True(await slot.StartAsync());
        _platform.Worker.Crash();
        Assert.True(Eventually(() => !slot.IsReady));
        var starts = _platform.Worker.Starts;

        Assert.Null(await slot.StreamStartAsync());

        Assert.Equal(starts, _platform.Worker.Starts);
        Assert.DoesNotContain("stream_start", _platform.Worker.Ops());
    }

    [Fact]
    public async Task A_cancel_for_a_stream_whose_worker_died_starts_no_worker()
    {
        // The host's exchange starts a dead worker again before it sends a
        // frame; a stream that died with the old process needs no cancel.
        using var slot = Slot();
        Assert.True(await slot.StartAsync());
        var streamId = await slot.StreamStartAsync();
        Assert.NotNull(streamId);
        _platform.Worker.Crash();
        Assert.True(Eventually(() => !slot.IsReady));
        var starts = _platform.Worker.Starts;

        await slot.StreamCancelAsync(streamId!);
        Assert.Null(await slot.StreamPushAsync(streamId!, new short[2_560]));

        Assert.Equal(starts, _platform.Worker.Starts);
        Assert.DoesNotContain("stream_cancel", _platform.Worker.Ops());
        Assert.Equal(1, _platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_stream_dies_with_the_worker_that_opened_it()
    {
        using var slot = Slot();
        Assert.True(await slot.StartAsync());
        var streamId = await slot.StreamStartAsync();
        Assert.NotNull(streamId);

        // Restarted (a language change used to do this; an idle unload or a
        // crash still does): the new worker never hears of the old stream.
        Assert.True(await slot.RestartAsync());
        Assert.Null(await slot.StreamPushAsync(streamId!, new short[2_560]));
        await slot.StreamCancelAsync(streamId!);
        Assert.DoesNotContain("stream_push", _platform.Worker.Ops());
        Assert.DoesNotContain("stream_cancel", _platform.Worker.Ops());

        // A stream on the new worker is its own.
        var next = await slot.StreamStartAsync();
        Assert.NotNull(next);
        Assert.NotNull(await slot.StreamPushAsync(next!, new short[2_560]));
        await slot.StreamCancelAsync(next!);
        Assert.Single(_platform.Worker.FramesOf("stream_cancel"));
        // Cancelled once: a second cancel of the same stream sends nothing.
        await slot.StreamCancelAsync(next!);
        Assert.Single(_platform.Worker.FramesOf("stream_cancel"));
    }

    [Fact]
    public async Task An_unloaded_slot_forgets_its_streams()
    {
        using var slot = Slot();
        Assert.True(await slot.StartAsync());
        var streamId = await slot.StreamStartAsync();

        slot.Unload();

        Assert.Null(await slot.StreamPushAsync(streamId!, new short[2_560]));
        await slot.StreamCancelAsync(streamId!);
        Assert.Equal(1, _platform.CreateWorkerCalls);
        Assert.DoesNotContain("stream_cancel", _platform.Worker.Ops());
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    internal static bool Eventually(Func<bool> condition, int ms = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    /// <summary>The Mac's <c>PreviewTransportSpy</c>: opens "preview-1",
    /// answers every push with eight words behind a speaker tag.</summary>
    private sealed class PreviewTransportSpy : IPreviewTranscriber
    {
        private int _starts;
        private int _pushes;
        private readonly ConcurrentQueue<string> _cancels = new();

        public int Starts => Volatile.Read(ref _starts);

        public int Pushes => Volatile.Read(ref _pushes);

        public IReadOnlyList<string> Cancels => _cancels.ToArray();

        public Task<string?> StreamStartAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _starts);
            return Task.FromResult<string?>("preview-1");
        }

        public Task<PreviewHypothesis?> StreamPushAsync(string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _pushes);
            return Task.FromResult<PreviewHypothesis?>(new PreviewHypothesis(
                PreviewText.LiveTail("[You] one two three four five six seven eight"), false));
        }

        public Task StreamCancelAsync(string streamId, CancellationToken ct = default)
        {
            _cancels.Enqueue(streamId);
            return Task.CompletedTask;
        }
    }

    /// <summary>The Mac's <c>DelayedPreviewOpen</c>: an open that waits for
    /// the test to complete it.</summary>
    private sealed class DelayedPreviewOpen : IPreviewTranscriber
    {
        private readonly TaskCompletionSource<string?> _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _cancels = new();

        public Task Waiting => _waiting.Task;

        public IReadOnlyList<string> Cancels => _cancels.ToArray();

        public ConcurrentQueue<short[]> Pushed { get; } = new();

        public void Complete(string? streamId) => _open.TrySetResult(streamId);

        public Task<string?> StreamStartAsync(CancellationToken ct = default)
        {
            _waiting.TrySetResult();
            return _open.Task;
        }

        public Task<PreviewHypothesis?> StreamPushAsync(string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default)
        {
            Pushed.Enqueue(pcm.ToArray());
            return Task.FromResult<PreviewHypothesis?>(null);
        }

        public Task StreamCancelAsync(string streamId, CancellationToken ct = default)
        {
            _cancels.Enqueue(streamId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Opens at once and answers each push with the next scripted
    /// text, the last one again once the script runs out.</summary>
    private sealed class ScriptedPush(params string[] replies) : IPreviewTranscriber
    {
        private int _pushes;

        public Task<string?> StreamStartAsync(CancellationToken ct = default) => Task.FromResult<string?>("scripted");

        public Task<PreviewHypothesis?> StreamPushAsync(string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default)
        {
            var index = Math.Min(Interlocked.Increment(ref _pushes), replies.Length) - 1;
            return Task.FromResult<PreviewHypothesis?>(new PreviewHypothesis(replies[index], false));
        }

        public Task StreamCancelAsync(string streamId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A cancel whose frame takes the calling thread until the
    /// test lets it go, as a write to a full pipe would.</summary>
    private sealed class BlockingCancel : IPreviewTranscriber
    {
        private readonly ManualResetEventSlim _release = new();

        private int _exited;

        public ManualResetEventSlim Entered { get; } = new();

        /// <summary>The cancel has returned: released by the test, or after
        /// 30 s so that a stop that waits for it fails instead of hanging.</summary>
        public bool Exited => Volatile.Read(ref _exited) == 1;

        public int CancelThread { get; private set; }

        public void Release() => _release.Set();

        public Task<string?> StreamStartAsync(CancellationToken ct = default) => Task.FromResult<string?>("blocking");

        public Task<PreviewHypothesis?> StreamPushAsync(string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default) =>
            Task.FromResult<PreviewHypothesis?>(null);

        public Task StreamCancelAsync(string streamId, CancellationToken ct = default)
        {
            CancelThread = Environment.CurrentManagedThreadId;
            Entered.Set();
            _release.Wait(TimeSpan.FromSeconds(30));
            Volatile.Write(ref _exited, 1);
            return Task.CompletedTask;
        }
    }

    /// <summary>The session's log, holding the thread that writes the
    /// stop's profile line until the test lets it go: the stop has left its
    /// lock by then and not yet raised its event.</summary>
    private sealed class HeldProfileLog : ILogger<DictationPreviewSession>
    {
        private readonly ManualResetEventSlim _release = new();

        public ManualResetEventSlim Entered { get; } = new();

        public void Release() => _release.Set();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Dictation preview profile:", StringComparison.Ordinal))
            {
                Entered.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>A push that stays on the wire until the test lets it go;
    /// every later push answers at once.</summary>
    private sealed class HeldPush : IPreviewTranscriber
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PreviewHypothesis?> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _events = new();
        private readonly ConcurrentQueue<int> _lengths = new();

        public Task PushEntered => _entered.Task;

        public IReadOnlyList<string> Events => _events.ToArray();

        public IReadOnlyList<int> Lengths => _lengths.ToArray();

        public int Pushes => _lengths.Count;

        public void ReleasePush() => _reply.TrySetResult(new PreviewHypothesis("late words", false));

        public Task<string?> StreamStartAsync(CancellationToken ct = default) => Task.FromResult<string?>("held");

        public async Task<PreviewHypothesis?> StreamPushAsync(string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default)
        {
            _lengths.Enqueue(pcm.Length);
            _entered.TrySetResult();
            var reply = await _reply.Task;
            _events.Enqueue("push");
            return reply;
        }

        public Task StreamCancelAsync(string streamId, CancellationToken ct = default)
        {
            _events.Enqueue("cancel");
            return Task.CompletedTask;
        }
    }
}
