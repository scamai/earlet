namespace Dictation.Core;

/// <summary>
/// Why a take ended before the user ended it, by a fault that keeps it: the
/// take is transcribed and inserted up to there, as a release would, and the
/// ending says where it stopped (LIMIT 2026-10-10, design §3.6).
/// </summary>
public enum DictationTakeStop
{
    /// <summary>The user ended it: a release, a stop key, the limit.</summary>
    None,

    /// <summary>The microphone stopped delivering audio
    /// (<see cref="DictationCaptureWatchdog"/>).</summary>
    MicrophoneStalled,

    /// <summary>Windows stopped the microphone's stream with an error
    /// (<c>IAudioCaptureFaultWatch</c>).</summary>
    MicrophoneFailed,

    /// <summary>The microphone was unplugged, disabled or removed
    /// (<c>IAudioCaptureEndpointWatch</c>).</summary>
    MicrophoneRemoved,

    /// <summary>The speech worker stopped answering and could not be
    /// started again while the user spoke.</summary>
    WorkerLost,
}
