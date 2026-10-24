namespace Dictation.Speech;

/// <summary>
/// The capture a device had open stopped by itself, with an error: WASAPI's
/// <c>RecordingStopped</c> carrying an exception, such as
/// <c>AUDCLNT_E_DEVICE_INVALIDATED</c> when the endpoint goes away under an
/// open stream. No more audio will come from that capture.
/// </summary>
/// <param name="Error">The exception's type name and, for a COM error, its
/// HRESULT in hex: never its message, which a driver may fill with anything.</param>
/// <param name="Capture">Which open of the device it happened to
/// (<see cref="IAudioCaptureFaultWatch.Capture"/>), so a fault that reaches
/// its listener after another capture opened is recognised as stale.</param>
public sealed record AudioCaptureFault(string Error, int Capture);

/// <summary>
/// A capture device that says when its open capture stops by itself with an
/// error, mid-capture.
/// </summary>
/// <remarks>
/// <para>
/// A capability interface, as <see cref="IAudioCaptureEndpointWatch"/> is: a
/// fake that does not implement it simply never faults, and a listener asks
/// for it with a type test.
/// </para>
/// <para>
/// Before 2026-10-10 nothing listened for this on Windows: a capture that
/// died mid-hold left the hold "listening" to nothing until the key came up.
/// The Dictation app now ends the hold there and transcribes what was
/// captured up to the fault (LIMIT 2026-10-10, "faults keep the take").
/// </para>
/// </remarks>
public interface IAudioCaptureFaultWatch
{
    /// <summary>
    /// The open capture stopped with an error. Raised at most once per
    /// capture, only for a stop the caller did not ask for, on a thread-pool
    /// thread: never on the audio thread, and never on the caller's.
    /// </summary>
    event EventHandler<AudioCaptureFault>? CaptureFailed;

    /// <summary>How many captures this device has opened, counting from 1;
    /// 0 before the first. A fault names the capture it happened to.</summary>
    int Capture { get; }
}
