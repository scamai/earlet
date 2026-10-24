namespace Dictation.Core;

/// <summary>
/// The knobs of "long takes are never lost" (LIMIT 2026-10-10, Windows stage
/// 1): where a take's audio is spooled, when a microphone that stopped
/// delivering ends the take, how often the speech worker is asked whether it
/// is alive during a long take, how a failed decode is retried, and when
/// Escape asks before it throws a take away. The defaults are the product's;
/// tests turn them.
/// </summary>
public sealed record DictationTakeOptions
{
    // ─── The spool ──────────────────────────────────────────────────────

    /// <summary>Where a take's audio is written while it is recorded, and the
    /// last failed take is kept (<see cref="DictationTakeSpool"/>).</summary>
    public string Directory { get; init; } = DictationPaths.ForCurrentUser().RecoveryDirectory;

    /// <summary>
    /// How the spool's file work runs: in order, off the capture thread and
    /// off the UI thread, one item at a time. Tests pass a synchronous runner
    /// so a file is on disk when the call returns.
    /// </summary>
    public Func<Action, Task> RunFileWork { get; init; } = new SerialWorkQueue().Run;

    /// <summary>How long a kept take is offered: older ones are deleted at
    /// the next launch, unheard (owner decision 3's default, LIMIT-DESIGN §6).</summary>
    public TimeSpan KeptFor { get; init; } = TimeSpan.FromDays(7);

    // ─── A microphone that stops delivering ─────────────────────────────

    /// <summary>
    /// No buffer for this long, once the take is past
    /// <see cref="StartupGrace"/>, is a capture that stalled: the take ends
    /// there and what was captured is transcribed. The Mac's
    /// <c>interruptionGapSeconds</c>, 0.3 s. Null turns the watchdog off, as
    /// <see cref="DictationEnvironment.CaptureWatchdog"/> <c>=0</c> does in the field.
    /// </summary>
    public TimeSpan? StallGap { get; init; } = DictationCaptureWatchdog.Gap;

    /// <summary>How long after the capture came up the watchdog has no
    /// opinion once buffers flow: the Mac's short grace, 0.8 s.</summary>
    public TimeSpan StartupGrace { get; init; } = DictationCaptureWatchdog.StartupGrace;

    /// <summary>How long the first buffer may take: the Mac's Bluetooth grace,
    /// 2.5 s, because Windows cannot tell the watchdog the transport.</summary>
    public TimeSpan FirstBufferGrace { get; init; } = DictationCaptureWatchdog.FirstBufferGrace;

    // ─── The speech worker during a long take ───────────────────────────

    /// <summary>How long a take must have run before the worker is checked
    /// at all: 30 s. A shorter take's release finds out soon enough.</summary>
    public TimeSpan PingAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the worker is asked whether it is alive during a
    /// longer take: every 10 s. Null turns the check off.</summary>
    public TimeSpan? PingInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long an answer may take: 5 s (inferred, LIMIT-DESIGN
    /// §3.6). The worker answers a ping between two frames in milliseconds.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many restarts a worker that stopped answering gets while
    /// the user speaks before the take is stopped and kept for Recover last
    /// dictation: 2.</summary>
    public int PingRestarts { get; init; } = 2;

    /// <summary>
    /// The fixed part of a decode's deadline (<see cref="DictationWorkerPlan.TranscribeTimeout"/>):
    /// a release may take this plus one second per second of audio, and its
    /// one retry this plus two. 30 s, as shipped.
    /// </summary>
    public TimeSpan DecodeDeadline { get; init; } = TimeSpan.FromSeconds(30);

    // ─── Escape on a long take ──────────────────────────────────────────

    /// <summary>A take with more speech than this asks at the first Escape
    /// and discards at the second: 30 s, as Superwhisper asks.</summary>
    public TimeSpan EscapeAsksAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the question stands: a second Escape within it
    /// discards; after it the take carries on and the next Escape asks
    /// again. The indicator's failure dwell, 5 s, so the line is up exactly
    /// as long as it is true.</summary>
    public TimeSpan EscapeAskWindow { get; init; } = TimeSpan.FromSeconds(5);
}
