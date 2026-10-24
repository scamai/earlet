using Dictation.Speech;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Dictation.Platform.Audio;

/// <summary>
/// Windows' endpoint notifications (<c>IMMNotificationClient</c>, through
/// NAudio's <see cref="MMDeviceNotificationClient"/>) for one capture device,
/// filtered to the ones that concern the capture open at the time by
/// <see cref="AudioCaptureEndpointChangeRule"/>, and reported once per capture.
/// </summary>
/// <remarks>
/// <para>
/// <b>One registration for the device's life.</b> It is made when the device
/// gains its first listener (the Dictation controller subscribes when it arms,
/// at Enable, never inside a press) and released when the device is disposed.
/// A capture only opens and closes it (<see cref="Open"/>, <see cref="Close"/>),
/// which are field writes: the press path, on the UI thread that also runs the
/// low-level keyboard hook, makes no COM call for it. Between captures every
/// notification is dropped.
/// </para>
/// <para>
/// <b>Threads.</b> The notifications arrive on a Windows audio worker thread
/// that holds a lock of the audio system while it calls out, so the handlers
/// here only compare strings and queue the rest: resolving the next
/// endpoint's name (a call back into the audio stack) and the report both run
/// on the thread pool. Disposing unregisters the client; it is called from
/// the device's own Dispose, never from a notification.
/// </para>
/// <para>
/// <b>Once per capture.</b> One change usually arrives as several
/// notifications (a headset unplugged is a state change and a removal), and
/// the capture's listener ends the hold on the first, so the rest are dropped
/// here.
/// </para>
/// <para>
/// <b>No default changes.</b> The Mac binds a device at the press and ends a
/// hold only when that device dies (its in-hold watchdog, D15); a new default
/// that appears mid-hold, a headset plugged in, leaves the hold to finish on
/// the microphone it opened. So the client hears state changes and removals
/// only, and never subscribes to Windows' default-device notification.
/// </para>
/// <para>
/// Built for Windows and cross-compiled on Linux; it has never run. The rules
/// it applies are tested on Linux (<c>MicrophoneChangeTests</c>).
/// </para>
/// </remarks>
internal sealed class WasapiEndpointChangeWatch : IDisposable
{
    /// <summary>The capture open now, as the notification thread reads it.</summary>
    private sealed class OpenCapture(string endpointId, int capture)
    {
        public string EndpointId { get; } = endpointId;

        public int Capture { get; } = capture;

        /// <summary>1 once this capture's one report is on its way.</summary>
        public int Reported;
    }

    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDeviceNotificationClient _client;
    private readonly Func<string, string?> _nextEndpointName;
    private readonly Action<AudioCaptureEndpointChangeKind, string?, int> _report;
    private readonly ILogger _logger;
    private OpenCapture? _open;
    private volatile bool _disposed;

    private WasapiEndpointChangeWatch(
        MMDeviceEnumerator enumerator,
        Func<string, string?> nextEndpointName,
        Action<AudioCaptureEndpointChangeKind, string?, int> report,
        ILogger logger)
    {
        _enumerator = enumerator;
        _nextEndpointName = nextEndpointName;
        _report = report;
        _logger = logger;

        // Raised on the audio worker thread, not marshalled to the UI
        // thread: that thread also runs the keyboard hook, and a
        // notification has no business queueing there.
        _client = enumerator.CreateNotificationClient(useSynchronizationContext: false);
        _client.DeviceStateChanged += OnDeviceStateChanged;
        _client.DeviceRemoved += OnDeviceRemoved;
    }

    /// <summary>
    /// Register for the device's life, or return null when Windows would not
    /// take the registration: the device then records as it always did, with
    /// no mid-hold change noticed.
    /// </summary>
    /// <param name="nextEndpointName">The friendly name of the endpoint the
    /// next open would use, given the endpoint the capture had open; null when
    /// there is none to name. Called on the thread pool.</param>
    /// <param name="report">The one report per capture, with the capture's
    /// number. Called on the thread pool.</param>
    /// <param name="logger">Where a refused registration is logged.</param>
    public static WasapiEndpointChangeWatch? TryStart(
        Func<string, string?> nextEndpointName,
        Action<AudioCaptureEndpointChangeKind, string?, int> report,
        ILogger logger)
    {
        MMDeviceEnumerator? enumerator = null;
        try
        {
            // Its own enumerator, kept for the device's life: the client's
            // registration ends when the enumerator that made it is disposed,
            // and the one each open uses is disposed at once.
            enumerator = new MMDeviceEnumerator();
            return new WasapiEndpointChangeWatch(enumerator, nextEndpointName, report, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Microphone changes during a hold will not be noticed: the notification registration failed");
            try
            {
                enumerator?.Dispose();
            }
            catch (Exception)
            {
                // Already failing; the warning above is the record.
            }

            return null;
        }
    }

    /// <summary>A capture opened <paramref name="endpointId"/>: notifications
    /// from now on are judged against it.</summary>
    /// <param name="endpointId">The endpoint the capture opened.</param>
    /// <param name="capture">The capture's number.</param>
    public void Open(string endpointId, int capture) =>
        Volatile.Write(ref _open, new OpenCapture(endpointId, capture));

    /// <summary>The capture closed: a change seen from here on concerns none.</summary>
    public void Close() => Volatile.Write(ref _open, null);

    private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
    {
        if (Volatile.Read(ref _open) is { } open)
        {
            Notice(
                open,
                AudioCaptureEndpointChangeRule.StateChanged(open.EndpointId, e.DeviceId, stillActive: e.NewState == DeviceState.Active));
        }
    }

    private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e)
    {
        if (Volatile.Read(ref _open) is { } open)
        {
            Notice(open, AudioCaptureEndpointChangeRule.Removed(open.EndpointId, e.DeviceId));
        }
    }

    /// <summary>On the audio worker thread: decide, and queue the rest.</summary>
    private void Notice(OpenCapture open, AudioCaptureEndpointChangeKind? change)
    {
        if (change is not { } kind || _disposed || Interlocked.Exchange(ref open.Reported, 1) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed)
            {
                return;
            }

            string? name = null;
            try
            {
                name = _nextEndpointName(open.EndpointId);
            }
            catch (Exception ex)
            {
                // The sentence then names no device, as the Mac's does when
                // its rebind reopened nothing.
                _logger.LogDebug(ex, "The next microphone's name could not be read");
            }

            try
            {
                _report(kind, name, open.Capture);
            }
            catch (Exception ex)
            {
                // A throw on a pool thread is a dead process.
                _logger.LogWarning(ex, "A microphone change could not be reported");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        try
        {
            _client.DeviceStateChanged -= OnDeviceStateChanged;
            _client.DeviceRemoved -= OnDeviceRemoved;
            _client.Dispose();
            _enumerator.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "The microphone change registration did not close cleanly");
        }
    }
}
