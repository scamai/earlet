namespace Dictation.Core.Tests;

// The Mac's DictationInHoldWatchdogTests.swift,
// its verdict and its grace scenarios, ported for Windows' no-chunks trigger
// (DictationCaptureWatchdog). Not ported: the stale-flag scenarios (Windows
// has no such flag; a stream that dies says so through
// IAudioCaptureFaultWatch), device-gone (the endpoint watch,
// MicrophoneChangeTests), and the rebind ones (Windows ends the take and
// keeps it instead: TakeFaultTests).

/// <summary>The watchdog's verdict, pure.</summary>
public sealed class TakeWatchdogTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>The Mac's thresholds: 0.3 s gap, 0.8 s grace, and its
    /// Bluetooth 2.5 s grace before the first buffer.</summary>
    private static bool Stalled(double now, double? lastBufferAt, double capturingSince = 0) =>
        DictationCaptureWatchdog.Stalled(
            S(now),
            S(capturingSince),
            lastBufferAt is { } last ? S(last) : null,
            DictationCaptureWatchdog.Gap,
            DictationCaptureWatchdog.StartupGrace,
            DictationCaptureWatchdog.FirstBufferGrace);

    [Fact]
    public void The_grace_gates_every_trigger_and_the_gap_is_measured_from_the_last_buffer()
    {
        // testTheGraceGatesEveryTriggerAndTheGapIsMeasuredFromTheLastChunk.
        // Inside the grace nothing is a fault, however long the device has
        // been quiet since its first buffer.
        Assert.False(Stalled(now: 0.7, lastBufferAt: 0.05));
        // Past the grace, 0.3 s with no buffer is.
        Assert.True(Stalled(now: 3.0, lastBufferAt: 2.6));
        Assert.True(Stalled(now: 3.0, lastBufferAt: 2.7));
        Assert.False(Stalled(now: 3.0, lastBufferAt: 2.71));
        // A buffer 0.1 s ago is a live device, however long the take has run.
        Assert.False(Stalled(now: 9.0, lastBufferAt: 8.9));
        Assert.False(Stalled(now: 600.0, lastBufferAt: 599.95));
    }

    [Fact]
    public void A_take_whose_buffers_keep_arriving_is_never_interrupted()
    {
        // testAHoldWhoseChunksKeepArrivingIsNeverInterrupted: five seconds of
        // 100 ms buffers, every tick in between.
        for (var step = 1; step <= 50; step++)
        {
            var buffer = step * 0.1;
            Assert.False(Stalled(now: buffer + 0.05, lastBufferAt: buffer), $"t={buffer}");
        }
    }

    [Fact]
    public void A_two_second_wait_for_the_first_buffer_is_not_an_interruption()
    {
        // testATwoSecondSilenceAtTheStartOfABluetoothHoldIsNotAnInterruption.
        // Windows cannot tell the transport, so every device gets the
        // Bluetooth grace before its first buffer.
        for (var step = 1; step <= 20; step++)
        {
            Assert.False(Stalled(now: step * 0.1, lastBufferAt: null));
        }

        Assert.False(Stalled(now: 2.49, lastBufferAt: null));
        Assert.True(Stalled(now: 2.5, lastBufferAt: null));
    }

    [Fact]
    public void A_buffer_from_before_the_capture_came_up_is_not_this_take_s_first()
    {
        // One in flight from the last capture: the take still waits for its
        // own first buffer, with the long grace.
        Assert.False(Stalled(now: 12.49, lastBufferAt: 9.0, capturingSince: 10.0));
        Assert.True(Stalled(now: 12.5, lastBufferAt: 9.0, capturingSince: 10.0));
    }

    [Fact]
    public void The_thresholds_are_the_Mac_s_and_the_product_s_defaults()
    {
        // DictationCapture.interruptionGapSeconds, defaultStartupGraceMsFallback
        // and bluetoothStartupGraceMsFallback.
        Assert.Equal(TimeSpan.FromMilliseconds(300), DictationCaptureWatchdog.Gap);
        Assert.Equal(TimeSpan.FromMilliseconds(800), DictationCaptureWatchdog.StartupGrace);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), DictationCaptureWatchdog.FirstBufferGrace);

        var takes = new DictationControllerOptions().Takes;
        Assert.Equal(DictationCaptureWatchdog.Gap, takes.StallGap);
        Assert.Equal(DictationCaptureWatchdog.StartupGrace, takes.StartupGrace);
        Assert.Equal(DictationCaptureWatchdog.FirstBufferGrace, takes.FirstBufferGrace);
        // The worker's health check and Escape's question, LIMIT 2026-10-10.
        Assert.Equal(TimeSpan.FromSeconds(10), takes.PingInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), takes.PingAfter);
        Assert.Equal(TimeSpan.FromSeconds(5), takes.PingTimeout);
        Assert.Equal(2, takes.PingRestarts);
        Assert.Equal(TimeSpan.FromSeconds(30), takes.EscapeAsksAfter);
        Assert.Equal(TimeSpan.FromSeconds(30), takes.DecodeDeadline);
        Assert.Equal(TimeSpan.FromDays(7), takes.KeptFor);
        Assert.EndsWith(Path.Combine("Dictation", "recovery"), takes.Directory, StringComparison.Ordinal);
    }
}
