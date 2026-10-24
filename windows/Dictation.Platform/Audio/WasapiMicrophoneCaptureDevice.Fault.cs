using System.Runtime.InteropServices;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace Dictation.Platform.Audio;

// Part of WasapiMicrophoneCaptureDevice (see WasapiMicrophoneCaptureDevice.cs):
// a capture that stops by itself with an error (IAudioCaptureFaultWatch).
// Built on Linux; never run (WASAPI needs Windows).
public sealed partial class WasapiMicrophoneCaptureDevice
{
    /// <summary>The capture <see cref="Stop"/> asked to stop: its own
    /// <c>RecordingStopped</c> is the answer to that, not a fault. Held as an
    /// object: only its identity is compared.</summary>
    private object? _stopping;

    /// <summary>The capture a fault was already reported for, so one capture
    /// reports at most once.</summary>
    private object? _faulted;

    /// <inheritdoc />
    public event EventHandler<AudioCaptureFault>? CaptureFailed;

    /// <summary>
    /// NAudio's <c>RecordingStopped</c>: raised through the synchronisation
    /// context the capture was made on (the UI thread, for this app), or on
    /// the capture thread without one. A stop that carries an exception and
    /// that <see cref="Stop"/> did not ask for is the endpoint failing under
    /// the open stream (for example <c>AUDCLNT_E_DEVICE_INVALIDATED</c>):
    /// reported once, by type and HRESULT, never by message, on the thread
    /// pool. Every other stop is the one <see cref="Stop"/> asked for.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not { } error
            || sender is null
            || !ReferenceEquals(sender, _capture)
            || ReferenceEquals(sender, _stopping)
            || ReferenceEquals(sender, _faulted))
        {
            return;
        }

        _faulted = sender;
        var fault = new AudioCaptureFault(Describe(error), Capture);
        _logger.LogWarning(
            "The microphone stream stopped by itself during a capture ({Error}); no more audio will come from it",
            fault.Error);
        ThreadPool.QueueUserWorkItem(_ => CaptureFailed?.Invoke(this, fault));
    }

    /// <summary>The exception's type, and a COM error's HRESULT: the message
    /// is the driver's, and could carry anything.</summary>
    private static string Describe(Exception error) =>
        error is COMException com
            ? $"{error.GetType().Name} 0x{com.HResult:X8}"
            : error.GetType().Name;
}
