namespace Dictation.Core;

/// <summary>
/// Whether a take's microphone has stopped delivering: the Mac's in-hold
/// watchdog verdict (D15, <c>DictationCapture.watchdogVerdict</c>), its
/// no-chunks trigger, on the controller's clock. Pure, so the rule is checked
/// without a device or a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ground truth is buffer flow.</b> The Mac's three triggers are a stale
/// stream flag that stood through silence, the bound device leaving the
/// roster, and no chunk for <see cref="Gap"/> after the startup grace.
/// Windows has no stale flag; its device-gone is the endpoint notification
/// (<c>IAudioCaptureEndpointWatch</c>) and a stream that dies says so through
/// <c>IAudioCaptureFaultWatch</c>, so only the gap is judged here.
/// </para>
/// <para>
/// <b>The graces</b> count from the capture being up, not from the press:
/// the Mac's count from a microphone already open, while Windows opens the
/// device at the press, and a headset switching profiles can take seconds to
/// return. Inside <see cref="StartupGrace"/> (the Mac's 0.8 s)
/// nothing is a fault. Windows cannot tell the watchdog the transport, so
/// until the first buffer arrives the Mac's Bluetooth grace,
/// <see cref="FirstBufferGrace"/> (2.5 s), applies to every device: a
/// headset's wake-up delivers nothing at first, and stopping it as stalled
/// would cost the user the press. Once buffers flow, a gap of
/// <see cref="Gap"/> after the short grace is a stall. WASAPI's shared-mode
/// capture hands over a buffer every 10 to 50 ms; whether 0.3 s is enough
/// headroom on a loaded laptop is unmeasured (LIMIT-DESIGN §3.6).
/// </para>
/// <para>
/// <b>What a stall does.</b> Unlike the Mac today, it never discards: the
/// take ends there and what was captured is transcribed and inserted, with
/// "Microphone stopped at m:ss" (DictationController.Takes.cs).
/// </para>
/// </remarks>
public static class DictationCaptureWatchdog
{
    /// <summary>The Mac's <c>interruptionGapSeconds</c>.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(300);

    /// <summary>The Mac's <c>defaultStartupGraceMsFallback</c>.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(800);

    /// <summary>The Mac's <c>bluetoothStartupGraceMsFallback</c>, for the
    /// first buffer of every take.</summary>
    public static readonly TimeSpan FirstBufferGrace = TimeSpan.FromMilliseconds(2500);

    /// <summary>
    /// Whether the take whose capture came up at
    /// <paramref name="capturingSince"/> has stalled at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">The controller's clock now.</param>
    /// <param name="capturingSince">When the take's microphone was first
    /// seen open, after its device returned from opening.</param>
    /// <param name="lastBufferAt">When the last buffer of this take arrived,
    /// or null when none has.</param>
    /// <param name="gap">The longest silence from the device that is not a
    /// fault.</param>
    /// <param name="startupGrace">How long after the capture came up nothing
    /// is a fault, once buffers flow.</param>
    /// <param name="firstBufferGrace">How long the first buffer may take.</param>
    public static bool Stalled(
        TimeSpan now,
        TimeSpan capturingSince,
        TimeSpan? lastBufferAt,
        TimeSpan gap,
        TimeSpan startupGrace,
        TimeSpan firstBufferGrace)
    {
        var since = now - capturingSince;
        if (lastBufferAt is not { } last || last < capturingSince)
        {
            // No buffer of this take yet (one stamped before the capture came
            // up is the last capture's, in flight): a device still waking up,
            // until the long grace.
            return since >= firstBufferGrace;
        }

        return since >= startupGrace && now - last >= gap;
    }
}
