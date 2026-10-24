using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

/// <summary>
/// <see cref="PushToTalkMicrophoneTap.AbortCapture"/> on its own: the release
/// after a cancel reports nothing — no "the endpoint never opened" line and no
/// <see cref="MicrophoneTapOutcome.DeviceUnavailable"/> result for a
/// microphone that was open — and the memory of that abort ends with the hold
/// it ended. <c>DictationHoldTests</c> covers the same rule through the
/// controller, where only the log line is visible.
/// </summary>
public sealed class MicrophoneTapAbortTests
{
    private readonly FakeHotkey _hotkey = new();
    private readonly FakeMicrophone _microphone = new();
    private readonly LinesLogger _log = new();
    private readonly List<MicrophoneTapResult> _results = [];

    private PushToTalkMicrophoneTap Tap()
    {
        var tap = new PushToTalkMicrophoneTap(_hotkey, _microphone, DictationHoldPolicy.Windows.TapOptions, _log);
        tap.CaptureCompleted += (_, result) => _results.Add(result);
        return tap;
    }

    [Fact]
    public void The_release_after_a_cancel_reports_no_device_failure()
    {
        using var tap = Tap();
        _hotkey.Press();
        Assert.True(tap.IsCapturing);

        tap.AbortCapture();
        _hotkey.Release();

        Assert.Empty(_results);
        Assert.False(_microphone.Open);
        Assert.Contains(_log.Lines, line => line.Contains("Capture aborted", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, line => line.Contains("never opened", StringComparison.Ordinal));
    }

    [Fact]
    public void A_microphone_that_will_not_open_is_still_reported_on_the_hold_after_a_cancel()
    {
        using var tap = Tap();
        _hotkey.Press();
        tap.AbortCapture();
        _hotkey.Release();

        _microphone.StartSucceeds = false;
        _hotkey.Press();
        _hotkey.Release();

        Assert.Equal(MicrophoneTapOutcome.DeviceUnavailable, Assert.Single(_results).Outcome);
        Assert.Single(_log.Lines, line => line.Contains("never opened", StringComparison.Ordinal));
    }

    [Fact]
    public void The_memory_of_a_cancel_ends_at_the_next_press_even_if_its_release_never_came()
    {
        // A hotkey that broke its one-release-per-press contract: the abort
        // must not outlive the hold it ended, or it would hide a real device
        // failure on the next hold behind a cancel the user made earlier.
        using var tap = Tap();
        _hotkey.Press();
        tap.AbortCapture();

        _microphone.StartSucceeds = false;
        _hotkey.Press();
        _hotkey.Release();

        Assert.Equal(MicrophoneTapOutcome.DeviceUnavailable, Assert.Single(_results).Outcome);
        Assert.Contains(_log.Lines, line => line.Contains("never opened", StringComparison.Ordinal));
    }

    /// <summary>A hotkey the test presses and releases by hand.</summary>
    private sealed class FakeHotkey : IPushToTalkHotkey
    {
        public event EventHandler? Pressed;

        public event EventHandler? Released;

        public bool IsHeld { get; private set; }

        public bool IsRegistered => true;

        public bool Start() => true;

        public void NotifyHotkeyMessage()
        {
        }

        public void PollForRelease()
        {
        }

        public void Press()
        {
            IsHeld = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        public void Release()
        {
            IsHeld = false;
            Released?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Each line the tap logs.</summary>
    private sealed class LinesLogger : ILogger<PushToTalkMicrophoneTap>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
