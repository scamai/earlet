using System.Collections.Concurrent;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

// The fakes' halves for "long takes are never lost" (DictationController.Takes.cs):
// Escape as the hook sees it on a long take, a microphone whose stream
// Windows stops with an error, and the spool's file work run at once.

/// <summary>The spool's file work, run on the caller's thread: a file is on
/// disk when the call returns.</summary>
internal static class TakeFiles
{
    public static Task Now(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    /// <summary>The rig's take options: the spool in
    /// <paramref name="directory"/>, written at once, with the watchdog and
    /// the worker's health check off. Every other test moves the clock
    /// through holds that deliver no audio, which the watchdog would end.</summary>
    public static DictationTakeOptions Options(string directory) => new()
    {
        Directory = directory,
        RunFileWork = Now,
        StallGap = null,
        PingInterval = null,
    };
}

internal sealed partial class FakeTrigger
{
    private bool _escapeAsksFirst;

    private Action? _afterConsume;

    /// <summary>Runs once, right after the next poll has read the cancel
    /// latch: a key the hook sees between that read and the rest of the
    /// poll.</summary>
    public Action? AfterConsume
    {
        get => Volatile.Read(ref _afterConsume);
        set => Volatile.Write(ref _afterConsume, value);
    }

    /// <summary>The hook's flag, as the controller pushes it: the gesture's
    /// own when the fake runs it.</summary>
    public bool EscapeAsksFirst
    {
        get => Gesture?.EscapeAsksFirst ?? _escapeAsksFirst;
        set
        {
            _escapeAsksFirst = value;
            if (Gesture is not null)
            {
                Gesture.EscapeAsksFirst = value;
            }
        }
    }

    /// <summary>Escape goes down and up, as the hook sees it: through the
    /// gesture when the fake runs it, otherwise by the hook's own rule (asks
    /// once while the flag is up, then cancels; nothing while no dictation is
    /// in flight).</summary>
    public void Escape()
    {
        if (Gesture is not null)
        {
            KeyEvent(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown);
            KeyEvent(HoldKeyState.VkEscape, HoldKeyState.WmKeyUp);
            return;
        }

        if (!DictationInFlight)
        {
            return;
        }

        if (_escapeAsksFirst)
        {
            _escapeAsksFirst = false;
            if (PendingCancel == TriggerCancel.None)
            {
                PendingCancel = TriggerCancel.EscapeAsked;
            }

            return;
        }

        PendingCancel = TriggerCancel.Escape;
    }
}

/// <summary>The fake microphone can also fail under its open capture, as
/// WASAPI's <c>RecordingStopped</c> with an exception does.</summary>
internal sealed partial class FakeMicrophone : IAudioCaptureFaultWatch
{
    /// <summary>The handlers on an armed microphone's buffers for the arm's
    /// whole life: the tap's, and the take spool's, which writes a take as
    /// it is recorded (DictationController.Takes.cs). A meter or a preview
    /// adds its own only while a hold listens.</summary>
    public const int ArmedListeners = 2;

    public event EventHandler<AudioCaptureFault>? CaptureFailed;

    /// <summary>The stream stops with an error, during capture
    /// <paramref name="capture"/> (the open one by default). On the test's
    /// thread; the controller posts it to its own either way.</summary>
    public void Fail(int? capture = null) =>
        CaptureFailed?.Invoke(this, new AudioCaptureFault("COMException 0x88890004", capture ?? Capture));

    /// <summary>How many handlers hear about a fault now.</summary>
    public int FaultListeners => CaptureFailed?.GetInvocationList().Length ?? 0;

    /// <summary>Runs inside <see cref="Start"/>, before it answers: a device
    /// that takes a while to open (a Bluetooth headset switching to its
    /// call profile), on the thread that opens it.</summary>
    public Action? Opening { get; set; }
}

/// <summary>The fake worker can answer late, or never: each transcribe and
/// each ping takes the next delay of its queue, null meaning no answer until
/// the host gives up and stops the process. An empty queue answers at once.</summary>
internal sealed partial class FakeSpeechWorker
{
    public ConcurrentQueue<TimeSpan?> TranscribeDelays { get; } = new();

    public ConcurrentQueue<TimeSpan?> PingDelays { get; } = new();

    private async Task DelayAsync(string? op, CancellationToken ct)
    {
        var delays = op switch
        {
            "transcribe" => TranscribeDelays,
            "ping" => PingDelays,
            _ => null,
        };

        if (delays is not null && delays.TryDequeue(out var delay))
        {
            await Task.Delay(delay ?? Timeout.InfiniteTimeSpan, ct);
        }
    }
}

/// <summary>The spool's file work held until the test runs it: a disk that
/// answers late, so a test can change the world between a take's ending and
/// its file work.</summary>
internal sealed class DeferredFiles
{
    private readonly ConcurrentQueue<(Action Work, TaskCompletionSource Done)> _queue = new();

    public Task Run(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue((work, done));
        return done.Task;
    }

    public void RunAll()
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                item.Work();
                item.Done.SetResult();
            }
            catch (Exception error)
            {
                item.Done.SetException(error);
            }
        }
    }
}

/// <summary>A log that throws once, at the first line that starts with
/// <see cref="Prefix"/>: how a test makes the session's pipeline fault after
/// it typed the words, a bug path that raises no result.</summary>
internal sealed class ThrowOnceLog(string prefix) : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _lines = new();
    private int _thrown;

    public string Prefix { get; } = prefix;

    public IReadOnlyList<string> Lines => _lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(ThrowOnceLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            log._lines.Enqueue(line);
            if (line.StartsWith(log.Prefix, StringComparison.Ordinal) && Interlocked.Exchange(ref log._thrown, 1) == 0)
            {
                throw new InvalidOperationException("a bug in the pipeline, for the test");
            }
        }
    }
}
