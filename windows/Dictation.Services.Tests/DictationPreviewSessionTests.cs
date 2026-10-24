using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The live-preview session's contract. The first test is the privacy one and
/// is the reason <see cref="IPreviewTranscriber"/> is a seam at all: it asserts
/// that no stream is opened, which is only assertable because the recogniser is
/// behind an interface.
/// </summary>
public sealed class DictationPreviewSessionTests
{
    // ── fakes ───────────────────────────────────────────────────────────────

    private sealed class InMemoryPreviewTranscriber : IPreviewTranscriber
    {
        private readonly Queue<PreviewHypothesis?> _replies = new();

        public List<string> Ops { get; } = new();
        public List<short[]> Pushed { get; } = new();
        public string? StreamIdToReturn { get; set; } = "s1";

        public void Script(string? text, bool isEndpoint) =>
            _replies.Enqueue(new PreviewHypothesis(text, isEndpoint));

        public void ScriptNoAnswer() => _replies.Enqueue(null);

        public Task<string?> StreamStartAsync(CancellationToken ct = default)
        {
            Ops.Add("start");
            return Task.FromResult(StreamIdToReturn);
        }

        public Task<PreviewHypothesis?> StreamPushAsync(
            string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default)
        {
            Ops.Add("push");
            Pushed.Add(pcm.ToArray());
            return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);
        }

        public Task StreamCancelAsync(string streamId, CancellationToken ct = default)
        {
            Ops.Add("cancel");
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingStartPreviewTranscriber : IPreviewTranscriber
    {
        private readonly TaskCompletionSource<string?> _startReply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _startCalled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken StartToken { get; private set; }
        public Task Started => _startCalled.Task;
        public Task<string> Cancelled => _cancelled.Task;

        public Task<string?> StreamStartAsync(CancellationToken ct = default)
        {
            StartToken = ct;
            _startCalled.TrySetResult(true);
            return _startReply.Task.WaitAsync(ct);
        }

        public Task<PreviewHypothesis?> StreamPushAsync(
            string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default) =>
            Task.FromResult<PreviewHypothesis?>(null);

        public Task StreamCancelAsync(string streamId, CancellationToken ct = default)
        {
            _cancelled.TrySetResult(streamId);
            return Task.CompletedTask;
        }

        public void CompleteStart(string streamId) => _startReply.TrySetResult(streamId);
    }

    private sealed class StubSecureFieldProbe : ISecureFieldProbe
    {
        private readonly SecureFieldState _state;
        public StubSecureFieldProbe(SecureFieldState state) => _state = state;
        public SecureFieldState Probe() => _state;
    }

    private sealed class ThrowingSecureFieldProbe : ISecureFieldProbe
    {
        public SecureFieldState Probe() => throw new InvalidOperationException("UIA said no");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static DictationPreviewSession NewSession(
        IPreviewTranscriber transcriber, ISecureFieldProbe? probe = null) =>
        new(transcriber, NullLogger<DictationPreviewSession>.Instance, probe);

    /// <summary>One second of 16 kHz mono — comfortably over the 320 ms push.</summary>
    private static AudioChunk Audio(double seconds = 1.0, int rateHz = 16_000)
    {
        var samples = new float[(int)(seconds * rateHz)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.25f * MathF.Sin(i * 0.05f);
        }

        return new AudioChunk(samples, rateHz, 1);
    }

    private static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    // ── the assertion that matters ──────────────────────────────────────────

    [Fact]
    public void A_password_field_opens_no_stream_at_all()
    {
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr, new StubSecureFieldProbe(SecureFieldState.Secure));

        session.Start();
        session.Append(Audio());

        // Not "started then discarded" — never started. No audio spoken into a
        // password box reaches a recogniser.
        Assert.Empty(asr.Ops);
        Assert.Empty(asr.Pushed);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public void An_unknown_probe_result_still_previews()
    {
        // Unknown is frequent — UI Automation is a cross-process query a busy
        // application can decline. Reading it as Secure would silently switch
        // the preview off for people nowhere near a password box.
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr, new StubSecureFieldProbe(SecureFieldState.Unknown));

        session.Start();

        Assert.True(WaitFor(() => asr.Ops.Contains("start")));
    }

    [Fact]
    public void A_probe_that_throws_is_treated_as_an_ordinary_field()
    {
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr, new ThrowingSecureFieldProbe());

        session.Start();

        Assert.True(WaitFor(() => asr.Ops.Contains("start")));
    }

    // ── the accumulation contract, end to end ───────────────────────────────

    [Fact]
    public void Segments_committed_at_an_endpoint_survive_into_the_preview_text()
    {
        var asr = new InMemoryPreviewTranscriber();
        asr.Script("hello there", isEndpoint: true);
        asr.Script("and then some", isEndpoint: false);

        using var session = NewSession(asr);
        var seen = new List<string>();
        session.PreviewUpdated += (_, update) => seen.Add(update.Text);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));

        session.Append(Audio());
        Assert.True(WaitFor(() => seen.Count >= 1));
        session.Append(Audio());
        Assert.True(WaitFor(() => seen.Count >= 2));

        Assert.Equal("hello there and then some", seen[^1]);
    }

    [Fact]
    public void A_push_with_no_answer_leaves_the_text_alone()
    {
        var asr = new InMemoryPreviewTranscriber();
        asr.Script("still here", isEndpoint: false);
        asr.ScriptNoAnswer();

        using var session = NewSession(asr);
        var seen = new List<string>();
        session.PreviewUpdated += (_, update) => seen.Add(update.Text);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));

        session.Append(Audio());
        Assert.True(WaitFor(() => seen.Count >= 1));
        session.Append(Audio());
        Assert.True(WaitFor(() => asr.Pushed.Count >= 2));

        // The second push produced no event at all, rather than an empty one.
        Assert.Equal(new[] { "still here" }, seen);
    }

    // ── lifecycle ───────────────────────────────────────────────────────────

    [Fact]
    public void Stop_cancels_and_never_finishes()
    {
        // stream_finish would return the streaming engine's own text, and this
        // host must never be able to insert that.
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Stop();

        Assert.True(WaitFor(() => asr.Ops.Contains("cancel")));
        Assert.DoesNotContain("finish", asr.Ops);
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public async Task Stop_during_stream_start_keeps_the_shared_wire_alive_and_cancels_the_late_stream()
    {
        // StreamStartAsync speaks over the singleton AsrWorkerHost. Cancelling
        // its token after a frame may be on the pipe deliberately tears that
        // shared worker down to keep framing aligned. A short hold can end
        // while Turbo is still opening the preview, but that must not make the
        // accurate final pass pay for a worker restart (or race it).
        var asr = new BlockingStartPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        // Await explicit cross-thread signals instead of polling a mutable
        // List while occupying a ThreadPool worker. The full parallel suite
        // can otherwise starve the continuation this test is waiting for.
        await asr.Started.WaitAsync(TimeSpan.FromSeconds(30));
        session.Stop();

        Assert.False(asr.StartToken.IsCancellationRequested);
        asr.CompleteStart("late-stream");
        Assert.Equal(
            "late-stream",
            await asr.Cancelled.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(session.IsStreaming);
    }

    [Fact]
    public void Stop_clears_the_preview_so_the_next_hold_starts_blank()
    {
        var asr = new InMemoryPreviewTranscriber();
        asr.Script("previous hold", isEndpoint: true);

        using var session = NewSession(asr);
        var seen = new List<string>();
        session.PreviewUpdated += (_, update) => seen.Add(update.Text);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Append(Audio());
        Assert.True(WaitFor(() => seen.Count >= 1));

        session.Stop();

        Assert.Equal(string.Empty, seen[^1]);
    }

    [Fact]
    public void Stop_preserves_privacy_safe_latency_metrics_and_quality_comparison()
    {
        var asr = new InMemoryPreviewTranscriber();
        asr.Script("Hello, WORLD!", isEndpoint: false);

        using var session = NewSession(asr);
        var seen = new List<string>();
        session.PreviewUpdated += (_, update) => seen.Add(update.Text);
        session.Start("hold-123");
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Append(Audio());
        Assert.True(WaitFor(() => seen.Count == 1));
        session.Stop();

        var metrics = Assert.IsType<DictationPreviewMetrics>(session.LastMetrics);
        Assert.Equal("hold-123", metrics.OperationId);
        Assert.NotNull(metrics.StreamOpenMs);
        Assert.NotNull(metrics.FirstAudioMs);
        Assert.NotNull(metrics.FirstPushMs);
        Assert.NotNull(metrics.FirstPartialMs);
        Assert.Equal(1, metrics.PushCount);
        Assert.Equal(1, metrics.PartialCount);
        Assert.Equal(1, metrics.ChangedPartialCount);
        Assert.Equal(13, metrics.LastPreviewLength);

        var comparison = Assert.IsType<DictationPreviewComparison>(
            session.CompareLastToFinal("hello world"));
        Assert.Equal(1.0, comparison.Agreement, 3);
        Assert.Equal(13, comparison.PreviewLength);
        Assert.Equal(11, comparison.FinalLength);
    }

    [Fact]
    public void Preview_final_comparison_reports_disagreement_without_exposing_text()
    {
        var asr = new InMemoryPreviewTranscriber();
        asr.Script("completely wrong", isEndpoint: false);

        using var session = NewSession(asr);
        var seen = new List<string>();
        session.PreviewUpdated += (_, update) => seen.Add(update.Text);
        session.Start("hold-private");
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Append(Audio());
        Assert.True(WaitFor(() => seen.Count == 1));
        session.Stop();

        var comparison = Assert.IsType<DictationPreviewComparison>(
            session.CompareLastToFinal("the accurate final sentence"));
        Assert.InRange(comparison.Agreement, 0.0, 0.5);
        Assert.DoesNotContain("completely", comparison.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("accurate", comparison.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_after_stop_is_ignored()
    {
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Stop();
        session.Append(Audio());

        Assert.Empty(asr.Pushed);
    }

    // ── graceful degradation: the model is not in this bundle ───────────────

    [Fact]
    public void A_build_with_no_streaming_model_previews_nothing_and_does_not_throw()
    {
        // op_stream_start answers "streaming model not found", which
        // StreamStartAsync reports as a null id. Dictation must carry on
        // exactly as it did before this feature existed.
        var asr = new InMemoryPreviewTranscriber { StreamIdToReturn = null };
        using var session = NewSession(asr);
        var seen = new List<string>();
        session.PreviewUpdated += (_, update) => seen.Add(update.Text);

        session.Start();
        Assert.True(WaitFor(() => asr.Ops.Contains("start")));
        session.Append(Audio());

        Assert.False(session.IsStreaming);
        Assert.Empty(asr.Pushed);
        Assert.Empty(seen);
    }

    // ── cadence ─────────────────────────────────────────────────────────────

    [Fact]
    public void Audio_shorter_than_one_chunk_is_held_back()
    {
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Append(Audio(seconds: 0.1));

        // 100 ms is under the measured 160 ms pull cadence.
        Assert.False(WaitFor(() => asr.Pushed.Count > 0, ms: 250));
    }

    [Fact]
    public void One_hundred_sixty_ms_is_enough_for_the_first_preview_push()
    {
        // X Elite A/B, 2026-08-23: 320 -> 160 ms preserved transcript WER
        // while first visible text improved from p50/p95 960/1280 ms to
        // 800/1120 ms on the same ten clips. This pins the host half of that
        // cadence; ops_stream pins the worker half.
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Append(Audio(seconds: 0.16));

        Assert.True(WaitFor(() => asr.Pushed.Count == 1));
    }

    [Fact]
    public void A_device_rate_that_is_not_16k_is_resampled_before_it_goes_out()
    {
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Append(Audio(seconds: 1.0, rateHz: 48_000));

        Assert.True(WaitFor(() => asr.Pushed.Count >= 1));
        // One second in at 48 kHz is one second out at 16 kHz.
        Assert.InRange(asr.Pushed[0].Length, 15_500, 16_500);
    }

    [Fact]
    public void A_device_that_changes_rate_mid_hold_is_dropped_not_blended()
    {
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Append(Audio(seconds: 1.0, rateHz: 16_000));
        Assert.True(WaitFor(() => asr.Pushed.Count >= 1));

        session.Append(Audio(seconds: 1.0, rateHz: 44_100));

        // Resampling two rates as one is not honest; the second buffer is
        // dropped, so no second push appears.
        Assert.False(WaitFor(() => asr.Pushed.Count >= 2, ms: 250));
    }

    [Fact]
    public void Starting_twice_opens_one_stream()
    {
        var asr = new InMemoryPreviewTranscriber();
        using var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Start();

        Assert.Single(asr.Ops, o => o == "start");
    }

    [Fact]
    public void Dispose_stops_a_running_preview()
    {
        var asr = new InMemoryPreviewTranscriber();
        var session = NewSession(asr);

        session.Start();
        Assert.True(WaitFor(() => session.IsStreaming));
        session.Dispose();

        Assert.True(WaitFor(() => asr.Ops.Contains("cancel")));
    }
}
