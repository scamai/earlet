using Dictation.Speech;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Dictation.Platform.Audio;

/// <summary>
/// workflows P-03 — <see cref="IAudioCaptureDevice"/> over the default WASAPI
/// capture endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the microphone.</b> It opens the default <em>capture</em>
/// endpoint, the person holding the push-to-talk key, never the
/// <em>render</em> endpoint's loopback mix of what the computer plays.
/// </para>
/// <para>
/// <b>Other apps keep the microphone.</b> The endpoint is opened in shared
/// mode, which admits several clients per endpoint, so a call or a recorder
/// that already has it goes on hearing it while a dictation runs. The one
/// coupling left is the thread: <see cref="Start"/> and <see cref="Stop"/>
/// are called from the message pump, so every call here is wrapped rather
/// than allowed to throw into it.
/// </para>
/// <para>
/// <b>The device's format is taken, not asked for.</b> Shared mode gives out
/// the endpoint's mix format — 48 kHz stereo float on most machines — and a
/// <c>WasapiCapture</c> asked for something else fails to initialise on the
/// hardware that does not want to convert. So the format travels out with every
/// buffer and <c>PushToTalkMicrophoneTap</c> converts to 16 kHz mono, where it
/// is arithmetic that a Linux checkout can prove.
/// </para>
/// <para>
/// <c>Role.Communications</c> rather than <c>Role.Console</c>: Windows lets a
/// user nominate a separate communications microphone, and a headset is the
/// likely demo input.
/// </para>
/// </remarks>
public sealed partial class WasapiMicrophoneCaptureDevice
    : IAudioCaptureDevice, IAudioCaptureEndpointDirectory, INamedAudioCaptureDevice, IAudioCaptureEndpointWatch,
        IAudioCaptureFaultWatch
{
    private readonly ILogger<WasapiMicrophoneCaptureDevice> _logger;

    private WasapiCapture? _capture;
    private bool _disposed;

    /// <summary>Windows' endpoint notifications, registered once, when the
    /// first listener subscribes to <see cref="EndpointChanged"/>, and
    /// released in <see cref="Dispose"/>; null before that, or when Windows
    /// refused the registration. Each capture opens and closes it.</summary>
    private WasapiEndpointChangeWatch? _changes;

    private bool _changesTried;

    private readonly object _listeners = new();

    private EventHandler<AudioCaptureEndpointChange>? _endpointChanged;

    private int _captures;

    /// <inheritdoc />
    /// <remarks>
    /// The first subscription registers for Windows' endpoint notifications
    /// (<see cref="WatchForChanges"/>), so the COM registration is made when
    /// the listener arms, never inside a press on the UI thread that also runs
    /// the keyboard hook, and not at all for a caller that does not listen.
    /// </remarks>
    public event EventHandler<AudioCaptureEndpointChange>? EndpointChanged
    {
        add
        {
            lock (_listeners)
            {
                _endpointChanged += value;
            }

            WatchForChanges();
        }

        remove
        {
            lock (_listeners)
            {
                _endpointChanged -= value;
            }
        }
    }

    /// <inheritdoc />
    public int Capture => Volatile.Read(ref _captures);

    /// <summary>
    /// What the currently open endpoint is called, and what it delivered while
    /// it was open.
    /// </summary>
    /// <remarks>
    /// Kept here rather than derived at close because the endpoint object is
    /// gone by then: <see cref="Release"/> disposes the capture, and asking
    /// CoreAudio a second time can answer about a different device if the
    /// default moved. These are what THIS open actually was.
    /// </remarks>
    private string? _openEndpointName;
    private long _openedTicks;
    private long _firstBufferTicks;
    private int _buffers;
    private long _bytes;

    /// <inheritdoc />
    public string? LastOpenedEndpointName => _openEndpointName;

    /// <summary>
    /// The endpoint the user pinned, or null for "whatever Windows calls the
    /// communications microphone".
    /// </summary>
    /// <remarks>
    /// Held here rather than read from settings on every <see cref="Start"/>:
    /// this project has no idea settings exist, and giving the audio layer a
    /// dependency on the settings graph to answer "which microphone" would put
    /// the shell underneath the device. The shell pushes the saved id in once
    /// at startup and again whenever the user picks — the same direction every
    /// other preference in dictation travels.
    /// </remarks>
    private string? _selectedEndpointId;

    public WasapiMicrophoneCaptureDevice(ILogger<WasapiMicrophoneCaptureDevice> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public event EventHandler<AudioChunk>? BufferCaptured;

    public bool Start()
    {
        if (_disposed)
        {
            return false;
        }

        if (_capture is not null)
        {
            // Reported rather than returned silently. The caller reads a false
            // here as DeviceUnavailable and tells the user the microphone would
            // not open — which would be a lie about a device that is open right
            // now, and would send whoever reads the log looking at drivers
            // instead of at the press/release pairing that left it open.
            _logger.LogWarning(
                "Microphone open was refused because {Device} is still open from a previous hold "
                + "that was never closed; this hold captures nothing",
                _openEndpointName ?? "the capture endpoint");
            return false;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // Asked before ResolveEndpoint's fallback runs, so the two answers
            // below are about the same instant and the line can say whether the
            // endpoint that opened is the one the user pinned.
            var pinned = _selectedEndpointId;

            var endpoint = ResolveEndpoint(enumerator);
            if (endpoint is null)
            {
                _logger.LogWarning("No capture endpoint is available; the microphone cannot open");
                return false;
            }

            var capture = new WasapiCapture(endpoint);
            capture.DataAvailable += OnDataAvailable;
            // A stop this device did not ask for, with its error: the
            // endpoint went away under the stream (WasapiMicrophoneCaptureDevice.Fault.cs).
            capture.RecordingStopped += OnRecordingStopped;

            _openEndpointName = endpoint.FriendlyName;
            _firstBufferTicks = 0;
            _buffers = 0;
            _bytes = 0;
            _openedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            capture.StartRecording();
            _capture = capture;
            // A field write, no COM call: the registration was made when the
            // listener subscribed.
            var number = Interlocked.Increment(ref _captures);
            _changes?.Open(endpoint.ID, number);

            // INF, not DBG, and it now says WHICH microphone in the sense the
            // user means: "the one I picked" or "whatever Windows nominated".
            // "It recorded from the wrong microphone" was previously
            // unanswerable from this line — it named a device without saying
            // whether that device was a choice or a default, so a pinned
            // headset that had been unplugged and silently fallen back to the
            // laptop array read exactly like a working pin.
            _logger.LogInformation(
                "Microphone endpoint open: {Device} [{Source}], {Rate} Hz, {Channels} channel(s), {Encoding}",
                endpoint.FriendlyName,
                pinned is null
                    ? "system default for communications"
                    : string.Equals(endpoint.ID, pinned, StringComparison.Ordinal)
                        ? "the microphone you chose"
                        : "system default — the microphone you chose is not available",
                capture.WaveFormat.SampleRate,
                capture.WaveFormat.Channels,
                capture.WaveFormat.Encoding);

            return true;
        }
        catch (Exception ex)
        {
            // No capture endpoint at all, or one held exclusively, or the
            // microphone privacy setting is off. All ordinary; the caller
            // reports DeviceUnavailable and the demo falls back to D-01.
            _logger.LogWarning(ex, "Microphone endpoint would not open");
            Release();
            return false;
        }
    }

    public void Stop()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        // Asked for: the stop it raises is not a fault.
        _stopping = capture;
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Microphone endpoint did not close cleanly");
        }

        var openMs = _openedTicks == 0
            ? 0
            : (long)((System.Diagnostics.Stopwatch.GetTimestamp() - _openedTicks)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        if (_buffers == 0)
        {
            // The one line that separates "Windows opened the device and the
            // driver never produced a sample" from "the user was silent". They
            // are the same event from every other angle — the endpoint opened,
            // the format was read, the hold ran its length, the transcript came
            // back empty — and only this count tells them apart. WRN because
            // an endpoint that opens and delivers nothing is a fault, not one
            // of the ordinary ways a hold ends.
            _logger.LogWarning(
                "Microphone endpoint closed: {Device} delivered 0 buffers in {OpenMs} ms. "
                + "The endpoint opened and the driver produced no audio at all — nothing was "
                + "heard because nothing arrived, which is a device fault and not silence",
                _openEndpointName ?? "the capture endpoint",
                openMs);
        }
        else
        {
            _logger.LogInformation(
                "Microphone endpoint closed: {Device}, {Buffers} buffer(s) / {Bytes} bytes over "
                + "{OpenMs} ms, first buffer {FirstMs} ms after the endpoint opened",
                _openEndpointName ?? "the capture endpoint",
                _buffers,
                _bytes,
                openMs,
                _firstBufferTicks == 0
                    ? 0
                    : (long)((_firstBufferTicks - _openedTicks)
                        * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        Release();
    }

    // ── Which microphone ──────────────────────────────────────────────────

    /// <inheritdoc />
    public string? SelectedEndpointId => _selectedEndpointId;

    /// <inheritdoc />
    public IReadOnlyList<AudioCaptureEndpoint> Enumerate()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // The default is resolved with the SAME DataFlow and Role that
            // Start uses. Asking with Role.Console here and Role.Communications
            // there is exactly how a pane ends up naming a device the tap is
            // not using.
            string? defaultId = null;
            try
            {
                defaultId = enumerator
                    .GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                    .ID;
            }
            catch (Exception ex)
            {
                // A machine with endpoints present but no default nominated.
                // The list is still worth returning; only the "which one is
                // default" annotation is lost.
                _logger.LogDebug(ex, "No default capture endpoint to annotate the list with");
            }

            var endpoints = new List<AudioCaptureEndpoint>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                // DeviceState.Active only. An unplugged or disabled endpoint is
                // a row that cannot be opened, and offering one is offering a
                // choice that fails on the next hold.
                endpoints.Add(new AudioCaptureEndpoint(
                    device.ID,
                    string.IsNullOrWhiteSpace(device.FriendlyName) ? device.ID : device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
            }

            return endpoints;
        }
        catch (Exception ex)
        {
            // No CoreAudio service, a privacy setting that hides the device
            // list, a COM failure. All the same fact from a settings pane's
            // side: there is no list to show.
            _logger.LogWarning(ex, "Could not enumerate capture endpoints");
            return [];
        }
    }

    /// <inheritdoc />
    public bool Select(string? endpointId)
    {
        if (_disposed)
        {
            return false;
        }

        var normalized = string.IsNullOrWhiteSpace(endpointId) ? null : endpointId.Trim();

        if (string.Equals(normalized, _selectedEndpointId, StringComparison.Ordinal))
        {
            // Already what was asked for. Reported as success rather than
            // refused: the caller's request is satisfied, and returning false
            // here would make a settings pane show a failure for a no-op.
            return true;
        }

        if (_capture is not null)
        {
            // The endpoint is open and a hold is in flight. Swapping now would
            // mean the sentence the user is part-way through speaking is cut in
            // half by a device change, so it is refused and the caller leaves
            // the picker showing what is actually in use. macOS gates its own
            // picker the same way.
            _logger.LogInformation(
                "A microphone change was refused because a capture is in progress");
            return false;
        }

        _selectedEndpointId = normalized;
        _logger.LogInformation(
            "Dictation microphone set to {Endpoint}",
            normalized ?? "the system default");
        return true;
    }

    /// <inheritdoc />
    public string ResolvedEndpointName
    {
        get
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var endpoint = ResolveEndpoint(enumerator);
                if (endpoint is null)
                {
                    return "No microphone found";
                }

                var name = endpoint.FriendlyName;
                return string.IsNullOrWhiteSpace(name) ? "The system default input" : name;
            }
            catch (Exception ex)
            {
                // Every failure here is the same fact from the user's side:
                // there is no capture endpoint this build can name.
                _logger.LogDebug(ex, "Could not resolve the capture endpoint's name");
                return "No microphone found";
            }
        }
    }

    /// <summary>
    /// The endpoint a hold would open: the pinned one when it is still present,
    /// otherwise the system default.
    /// </summary>
    /// <remarks>
    /// <b>A pinned endpoint that has been unplugged falls back rather than
    /// failing.</b> The alternative — refuse to open anything because the
    /// headset the user chose last month is not here today — turns a laptop
    /// microphone into no microphone, and the user has no way to know why. The
    /// pin is kept, not cleared: plugging the headset back in restores their
    /// choice with no second visit to the pane.
    /// </remarks>
    private MMDevice? ResolveEndpoint(MMDeviceEnumerator enumerator)
    {
        if (_selectedEndpointId is { } pinned)
        {
            try
            {
                var device = enumerator.GetDevice(pinned);
                if (device is not null && device.State == DeviceState.Active)
                {
                    return device;
                }

                _logger.LogInformation(
                    "The chosen microphone is not available; falling back to the system default");
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    ex,
                    "The chosen microphone could not be opened; falling back to the system default");
            }
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "There is no default capture endpoint");
            return null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // On NAudio's capture thread. Do the least possible here: unpack to
        // float and hand it over. The tap appends under a lock and converts
        // once, on release.
        var format = _capture?.WaveFormat;
        if (format is null || e.BytesRecorded <= 0)
        {
            return;
        }

        // Counted before the decode, so a format this build cannot read (which
        // ToFloat answers with an empty array) still shows as buffers arriving.
        // Otherwise a 24-bit endpoint would look byte-identical to a dead one.
        if (_firstBufferTicks == 0)
        {
            _firstBufferTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        _buffers++;
        _bytes += e.BytesRecorded;

        try
        {
            var samples = ToFloat(e.Buffer, e.BytesRecorded, format);
            if (samples.Length == 0)
            {
                // Named once per open, not once per buffer — this arrives every
                // 10 ms and a per-buffer warning is exactly the shape that
                // wrote 137 MB in 70 seconds. First buffer only: the format
                // does not change mid-capture, so the second one would say the
                // same thing.
                if (_buffers == 1)
                {
                    _logger.LogWarning(
                        "{Device} is delivering {Bits}-bit {Encoding}, which this build cannot read; "
                        + "every buffer from this endpoint is being discarded and the hold will "
                        + "produce no audio",
                        _openEndpointName ?? "the capture endpoint",
                        format.BitsPerSample,
                        format.Encoding);
                }
            }

            if (samples.Length > 0)
            {
                BufferCaptured?.Invoke(
                    this, new AudioChunk(samples, format.SampleRate, format.Channels));
            }
        }
        catch (Exception ex)
        {
            // A dropped buffer is a gap in a sentence. A throw on the capture
            // thread is a dead process.
            _logger.LogTrace(ex, "Microphone buffer skipped");
        }
    }

    /// <summary>
    /// The endpoint's bytes as interleaved floats. Two encodings in practice:
    /// IEEE float, which every modern mix format uses, and 16-bit PCM, which
    /// some USB headsets still report.
    /// </summary>
    private static float[] ToFloat(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = new float[bytesRecorded / sizeof(float)];
            Buffer.BlockCopy(buffer, 0, samples, 0, samples.Length * sizeof(float));
            return samples;
        }

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var samples = new float[bytesRecorded / sizeof(short)];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(buffer, i * sizeof(short)) / (float)short.MaxValue;
            }

            return samples;
        }

        // Anything else — 24-bit packed, 32-bit PCM — is a format this demo
        // has not met. Silence is better than noise made of misread bytes.
        return [];
    }

    // ── A change while the capture is open ────────────────────────────────

    /// <summary>
    /// The first listener's subscription: ask Windows, once for this device's
    /// life, to say when an endpoint is unplugged, disabled or removed
    /// (<see cref="IAudioCaptureEndpointWatch"/>). Each capture then opens and
    /// closes the watch, and only the endpoint it has open going away is
    /// reported, as the Mac's watchdog ends a hold only when its bound device
    /// dies; a default that moves mid-hold is not reported. The Dictation app
    /// ends the hold then and asks the user to say it again in the Mac's
    /// words. A registration Windows refuses is not asked again, and leaves
    /// the captures exactly as they were before this existed.
    /// </summary>
    private void WatchForChanges()
    {
        lock (_listeners)
        {
            if (_changesTried || _disposed)
            {
                return;
            }

            _changesTried = true;
        }

        _changes = WasapiEndpointChangeWatch.TryStart(
            NextEndpointName,
            (kind, name, capture) =>
            {
                // The name, not the id: the line a reader matches against
                // Sound settings, as the open and close lines do.
                _logger.LogInformation(
                    "The microphone changed during a capture ({Change}); the next hold opens {Device}",
                    kind,
                    name ?? "no microphone named");
                EventHandler<AudioCaptureEndpointChange>? listeners;
                lock (_listeners)
                {
                    listeners = _endpointChanged;
                }

                listeners?.Invoke(this, new AudioCaptureEndpointChange(kind, name, capture));
            },
            _logger);
    }

    /// <summary>
    /// The friendly name of the endpoint the next open would use: whatever
    /// <see cref="ResolveEndpoint"/> picks now. Null when there is none, or
    /// when what Windows resolved is the endpoint the capture had open or not
    /// active (<see cref="AudioCaptureEndpointChangeRule.NameOfNext"/>): a
    /// removal can be reported before Windows moves its default off the
    /// headset just unplugged. On the thread pool, never on the audio
    /// system's notification thread.
    /// </summary>
    private string? NextEndpointName(string openEndpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var next = ResolveEndpoint(enumerator);
        return AudioCaptureEndpointChangeRule.NameOfNext(
            openEndpointId,
            next?.ID,
            nextActive: next is not null && next.State == DeviceState.Active,
            next?.FriendlyName);
    }

    private void Release()
    {
        // First: a change seen from here on concerns no capture.
        _changes?.Close();

        if (_capture is null)
        {
            return;
        }

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;

        try
        {
            _capture.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Microphone endpoint dispose failed");
        }

        _capture = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        // After Stop, which closed any open capture; the registration made
        // for the first listener goes with the device.
        _changes?.Dispose();
        _changes = null;
    }
}
