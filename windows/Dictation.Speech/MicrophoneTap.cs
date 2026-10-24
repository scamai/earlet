using Microsoft.Extensions.Logging;

namespace Dictation.Speech;

/// <summary>
/// The one audio format this feature speaks: what
/// <c>transcription_worker/asr_server.py</c>'s <c>transcribe</c> op takes, and
/// therefore what a hold has to end up as.
/// </summary>
public static class WorkflowAudio
{
    /// <summary>16 kHz. Whisper's own input rate; anything else is resampled
    /// somewhere, and doing it here means doing it once.</summary>
    public const int SampleRateHz = 16_000;

    /// <summary>Mono. A microphone endpoint that reports two channels is
    /// reporting one microphone twice.</summary>
    public const int Channels = 1;

    /// <summary>16-bit signed, little-endian — the worker's <c>int16</c>.</summary>
    public const int BitsPerSample = 16;

    /// <summary>
    /// The peak below which a hold is treated, in the log, as silence rather
    /// than speech.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 0.0040, which is macOS's number: <c>DictationController.logUtteranceLevel</c>
    /// prints "(audible floor 0.0040)" beside every peak it reports, and a
    /// second floor on this side would make the two platforms' logs
    /// incomparable for the one question they are both asked — "was there any
    /// sound in it".
    /// </para>
    /// <para>
    /// It gates a LOG LINE and nothing else. No audio is discarded for being
    /// below it: the 2026-08-19 microphone defect was diagnosed precisely
    /// because a peak of 0.0029 was recorded and handed on, and a floor that
    /// dropped the buffer would have deleted its own evidence.
    /// </para>
    /// </remarks>
    public const float AudibleFloor = 0.004f;

    /// <summary>
    /// The analysis window the speech-energy verdict is measured over, in
    /// seconds.
    /// </summary>
    /// <remarks>
    /// 0.03, the same window as the macOS segmenter's
    /// <c>SegmenterConstants.frameSeconds</c>, the worker's
    /// <c>audio_energy.FRAME_SECONDS</c> and the meeting-notes eval reference.
    /// Four copies of "what is audible" is how two pipelines drifted apart
    /// once already; if you change this, change them all in the same commit.
    /// </remarks>
    public const float FrameSeconds = 0.03f;

    /// <summary>
    /// Whether <paramref name="mono"/> contains any speech-level energy: does
    /// the loudest <see cref="FrameSeconds"/> frame clear
    /// <see cref="AudibleFloor"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a frame and not the whole buffer.</b> Whole-buffer RMS is the
    /// wrong statistic for a hold: a 30 s press carrying one 150 ms word reads
    /// about 0.006, one and a half times the floor, so a slightly quieter room
    /// deletes a word the user said. The loudest frame is unchanged at 0.09.
    /// Whole-buffer PEAK is wrong the other way — the hold measured on this
    /// device on 2026-08-20 read peak 0.0351 over RMS 0.0016 across 6.7 s, a
    /// working microphone pointed at a room nobody was speaking into, and its
    /// peak clears the floor by 8.8x while its loudest frame does not clear it
    /// at all.
    /// </para>
    /// <para>
    /// <b>What the separation is.</b> Measured on the eight-clip dictation
    /// parity corpus on the Snapdragon X Elite, 2026-08-20: the silent 0.5 s
    /// hold that typed <c>날씨였습니다.</c> peaks at frame-RMS 0.00227,
    /// and the quietest real dictation hold peaks at 0.16944. The floor sits
    /// 1.76x above the first and 42x below the second.
    /// </para>
    /// <para>
    /// <b>What it does not claim.</b> This is a rule about energy density, not
    /// about impulses: one full-scale sample spreads to 0.041 over a 30 ms
    /// frame and passes. A loud click is not filtered here.
    /// </para>
    /// <para>
    /// This is the same rule the worker applies one process later
    /// (<c>transcription_worker/audio_energy.py</c>). Both exist on purpose —
    /// this one keeps a phantom sentence out of the user's document, and the
    /// worker's protects every other client of it (Notes, workflow hotkeys,
    /// anything on the IPC directly), none of which come through this tap.
    /// </para>
    /// </remarks>
    public static bool HasSpeechEnergy(ReadOnlySpan<float> mono, int sampleRateHz) =>
        FrameRmsPeak(mono, sampleRateHz) > AudibleFloor;

    /// <summary>
    /// The RMS of the loudest <see cref="FrameSeconds"/> frame of
    /// <paramref name="mono"/> — the reading
    /// <see cref="HasSpeechEnergy(ReadOnlySpan{float}, int)"/> makes its verdict
    /// from, so a rejection can show its margin instead of only its category.
    /// </summary>
    /// <remarks>
    /// Audio shorter than one frame falls back to whole-buffer RMS: "too short
    /// to frame" is not the same fact as "silent", and answering 0 for a
    /// 20 ms buffer would be the second dressed as the first.
    /// </remarks>
    public static float FrameRmsPeak(ReadOnlySpan<float> mono, int sampleRateHz)
    {
        if (mono.Length == 0 || sampleRateHz <= 0)
        {
            return 0f;
        }

        var frame = (int)(sampleRateHz * FrameSeconds);
        if (frame <= 0 || mono.Length < frame)
        {
            return Level(mono).Rms;
        }

        var best = 0f;
        for (var start = 0; start + frame <= mono.Length; start += frame)
        {
            var sumSquares = 0.0;
            for (var i = start; i < start + frame; i++)
            {
                sumSquares += (double)mono[i] * mono[i];
            }

            var rms = (float)Math.Sqrt(sumSquares / frame);
            if (rms > best)
            {
                best = rms;
            }
        }

        return best;
    }

    /// <summary>
    /// How many seconds of <paramref name="mono"/> sit in frames above
    /// <see cref="AudibleFloor"/>. Diagnostics only — nothing decides on it;
    /// a minimum-voiced-duration is a policy, and this feature does not have
    /// one.
    /// </summary>
    public static float VoicedSeconds(ReadOnlySpan<float> mono, int sampleRateHz)
    {
        if (mono.Length == 0 || sampleRateHz <= 0)
        {
            return 0f;
        }

        var frame = (int)(sampleRateHz * FrameSeconds);
        if (frame <= 0 || mono.Length < frame)
        {
            return 0f;
        }

        var voiced = 0;
        for (var start = 0; start + frame <= mono.Length; start += frame)
        {
            var sumSquares = 0.0;
            for (var i = start; i < start + frame; i++)
            {
                sumSquares += (double)mono[i] * mono[i];
            }

            if (Math.Sqrt(sumSquares / frame) > AudibleFloor)
            {
                voiced++;
            }
        }

        return voiced * FrameSeconds;
    }

    /// <summary>
    /// The loudest sample and the RMS of <paramref name="mono"/>, both as
    /// absolute magnitudes in [0, 1].
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two numbers rather than one because they fail apart. A muted endpoint
    /// reads 0.0000/0.0000; a live microphone in a quiet room reads a small
    /// peak over a smaller RMS; a driver emitting one click into silence reads
    /// a large peak over a near-zero RMS. "No audio detected" cannot tell those
    /// three apart and a support call about any of them is unanswerable
    /// without the numbers.
    /// </para>
    /// <para>
    /// One pass over the buffer. The microphone tap no longer keeps a hold's
    /// source-rate audio to pass here at the release: it accumulates the same
    /// two numbers, and the frame readings below, a buffer at a time as the
    /// audio arrives (<c>TapRecording</c>), in the same order, so they are
    /// equal to this function's to the bit.
    /// </para>
    /// </remarks>
    public static (float Peak, float Rms) Level(ReadOnlySpan<float> mono)
    {
        if (mono.Length == 0)
        {
            return (0f, 0f);
        }

        var peak = 0f;
        var sumSquares = 0.0;

        for (var i = 0; i < mono.Length; i++)
        {
            var sample = mono[i];
            var magnitude = MathF.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }

            sumSquares += (double)sample * sample;
        }

        return (peak, (float)Math.Sqrt(sumSquares / mono.Length));
    }

    /// <summary>
    /// Interleaved float frames from a device to mono, by averaging the
    /// channels of each frame.
    /// </summary>
    /// <remarks>
    /// Averaging rather than taking channel 0: a stereo capture endpoint with
    /// a dead right channel is common enough (a headset plugged into a jack
    /// that expects TRRS), and averaging degrades that to a 6 dB drop instead
    /// of silence on the wrong pick. A trailing partial frame is dropped —
    /// half a frame is not a sample.
    /// </remarks>
    public static float[] ToMono(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels <= 0 || interleaved.Length == 0)
        {
            return [];
        }

        if (channels == 1)
        {
            return interleaved.ToArray();
        }

        var frames = interleaved.Length / channels;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var offset = frame * channels;

            for (var channel = 0; channel < channels; channel++)
            {
                sum += interleaved[offset + channel];
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    /// <summary>
    /// Mono float samples at <paramref name="sourceRateHz"/> to mono float
    /// samples at <see cref="SampleRateHz"/>, by linear interpolation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Linear interpolation and not a windowed-sinc filter, deliberately. The
    /// aliasing it leaves is above 8 kHz in a signal that is about to be turned
    /// into words by a model trained on telephone-grade audio; the cost of
    /// getting this wrong is a slightly worse transcript, and the cost of
    /// hand-writing a polyphase resampler for a demo is a day.
    /// </para>
    /// <para>
    /// A device already at 16 kHz is copied, not interpolated — the common
    /// path stays exact rather than accumulating rounding through an identity
    /// transform.
    /// </para>
    /// </remarks>
    public static float[] Resample(ReadOnlySpan<float> mono, int sourceRateHz)
    {
        if (sourceRateHz <= 0 || mono.Length == 0)
        {
            return [];
        }

        if (sourceRateHz == SampleRateHz)
        {
            return mono.ToArray();
        }

        var count = (int)((long)mono.Length * SampleRateHz / sourceRateHz);
        if (count <= 0)
        {
            return [];
        }

        var step = (double)sourceRateHz / SampleRateHz;
        var resampled = new float[count];

        for (var i = 0; i < count; i++)
        {
            var position = i * step;
            var left = (int)position;
            var right = Math.Min(left + 1, mono.Length - 1);
            var fraction = (float)(position - left);

            resampled[i] = mono[left] + ((mono[right] - mono[left]) * fraction);
        }

        return resampled;
    }

    /// <summary>
    /// Float samples in [-1, 1] to signed 16-bit, clamping rather than
    /// wrapping.
    /// </summary>
    /// <remarks>
    /// A sample above 1.0 is a device that ran hot, which happens when someone
    /// leans into a headset mic to be heard on stage. Wrapping turns that into
    /// a full-scale click of the opposite sign — audible, and worse for the
    /// recognizer than the clipping it replaces.
    /// </remarks>
    public static short[] ToPcm16(ReadOnlySpan<float> mono)
    {
        var pcm = new short[mono.Length];

        for (var i = 0; i < mono.Length; i++)
        {
            var scaled = (int)MathF.Round(mono[i] * short.MaxValue);
            pcm[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }

        return pcm;
    }
}

/// <summary>
/// One buffer as a capture device handed it over: interleaved float samples,
/// plus the format they are in.
/// </summary>
/// <remarks>
/// The format travels with every buffer rather than being asked for once,
/// because the device is the only thing that knows it — WASAPI shared mode
/// hands out the endpoint's mix format, which is whatever the user last set in
/// Sound settings, and a tap that assumed 48 kHz stereo would silently produce
/// chipmunk audio on a 44.1 kHz headset.
/// </remarks>
/// <param name="Samples">Interleaved frames, nominally in [-1, 1].</param>
/// <param name="SampleRateHz">The device's rate, not this feature's.</param>
/// <param name="Channels">The device's channel count, not this feature's.</param>
public sealed record AudioChunk(float[] Samples, int SampleRateHz, int Channels);

/// <summary>
/// The microphone as the three things a push-to-talk tap needs. Implemented for
/// real by <c>Dictation.Platform</c>'s
/// <c>WasapiMicrophoneCaptureDevice</c>, and by a fake in the test project.
/// </summary>
public interface IAudioCaptureDevice : IDisposable
{
    /// <summary>A buffer arrived. Raised on the device's own capture thread.</summary>
    event EventHandler<AudioChunk>? BufferCaptured;

    /// <summary>
    /// Open the capture endpoint and start delivering buffers. <c>false</c>
    /// when there is no microphone, or it is in exclusive use, or the user has
    /// denied microphone access — all ordinary states, none of them errors.
    /// </summary>
    bool Start();

    /// <summary>Stop delivering buffers and release the endpoint. Called only
    /// after a <see cref="Start"/> that returned <c>true</c>.</summary>
    void Stop();
}

/// <summary>
/// A capture device that can name the endpoint it currently has open.
/// </summary>
/// <remarks>
/// <para>
/// Optional and separate from <see cref="IAudioCaptureDevice"/> on purpose: it
/// exists for one log line, and a device that cannot answer must not be forced
/// to invent a name. <see cref="PushToTalkMicrophoneTap"/> asks with a type
/// test and falls back to a generic phrase.
/// </para>
/// <para>
/// It exists at all because "the captured audio is silent" and "the captured
/// audio is silent AND it came from the HDMI line-in you forgot was default"
/// are the same fact with and without the thing the user has to go and change.
/// </para>
/// </remarks>
public interface INamedAudioCaptureDevice
{
    /// <summary>
    /// The endpoint this device opened most recently, whether or not it is
    /// still open — the name Windows shows in Sound settings, so it can be
    /// matched by eye. Null only before the first open.
    /// </summary>
    /// <remarks>
    /// <b>Most recently opened, not currently open</b>, and that is the whole
    /// point of the property. The tap stops the device and THEN converts, so a
    /// "currently open" reading would be null at exactly the moment the
    /// silence warning wants to name the microphone — the property would have
    /// existed to add a name and would have contributed the word "the capture
    /// endpoint" instead.
    /// </remarks>
    string? LastOpenedEndpointName { get; }
}

/// <summary>How a hold ended.</summary>
public enum MicrophoneTapOutcome
{
    /// <summary>Audio was captured and is in the result.</summary>
    Captured,

    /// <summary>The endpoint would not open. No microphone, or something else
    /// holds it exclusively, or access is denied.</summary>
    DeviceUnavailable,

    /// <summary>The endpoint opened and delivered nothing — a muted or dead
    /// microphone, which looks identical to a working one until you look.
    /// </summary>
    NoAudio,

    /// <summary>The hold was shorter than <see cref="MicrophoneTapOptions.MinimumHold"/>.
    /// A brushed key, not a sentence.</summary>
    TooShort,
}

/// <summary>What a hold produced, at this feature's format.</summary>
/// <param name="Pcm">Signed 16-bit mono samples at 16 kHz.</param>
/// <param name="SampleRateHz">Always <see cref="WorkflowAudio.SampleRateHz"/>.</param>
/// <param name="Channels">Always <see cref="WorkflowAudio.Channels"/>.</param>
/// <param name="Duration">Derived from the sample count, not from a clock.</param>
public sealed record MicrophoneCapture(
    short[] Pcm,
    int SampleRateHz,
    int Channels,
    TimeSpan Duration)
{
    /// <summary>
    /// RMS of the loudest <see cref="WorkflowAudio.FrameSeconds"/> frame of
    /// this hold, or <c>null</c> when nobody measured it.
    /// </summary>
    /// <remarks>
    /// <b>Null means "not measured", and it must never read as "silent".</b>
    /// It is nullable rather than defaulting to zero for that one reason: a
    /// capture built by a caller that does not measure — a test rig, a future
    /// second tap — would otherwise arrive at the gate below the floor and be
    /// discarded as a quiet room. Absence of evidence carries on, the same
    /// direction <c>SecureFieldState.Unknown</c> chose and for the same
    /// reason.
    /// </remarks>
    public float? FrameRmsPeak { get; init; }

    /// <summary>
    /// Seconds of this hold in frames above <see cref="WorkflowAudio.AudibleFloor"/>,
    /// or <c>null</c> when unmeasured. Diagnostics: nothing decides on it.
    /// </summary>
    public float? VoicedSeconds { get; init; }

    /// <summary>
    /// Whether this hold carries speech-level energy — false ONLY when it was
    /// measured and the loudest frame did not clear
    /// <see cref="WorkflowAudio.AudibleFloor"/>.
    /// </summary>
    public bool HasSpeechEnergy =>
        FrameRmsPeak is not { } peak || peak > WorkflowAudio.AudibleFloor;
}

/// <summary>One hold, one result.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Audio">
/// The audio, and only when <paramref name="Outcome"/> is
/// <see cref="MicrophoneTapOutcome.Captured"/>. Every other outcome carries
/// <c>null</c> rather than an empty buffer, so a caller cannot mistake "nothing
/// was said" for "there is no microphone".
/// </param>
/// <summary>
/// <see cref="PushToTalkMicrophoneTap.BufferLimitReached"/>'s report: which
/// capture reached the buffer bound.
/// </summary>
/// <param name="OperationId">That capture's
/// <see cref="PushToTalkMicrophoneTap.CurrentOperationId"/>, read when the
/// bound was reached.</param>
public sealed record MicrophoneTapLimit(string? OperationId);

public sealed record MicrophoneTapResult(
    MicrophoneTapOutcome Outcome,
    MicrophoneCapture? Audio,
    string? OperationId = null);

/// <summary>The two limits on a hold.</summary>
/// <param name="MinimumHold">
/// Below this, the hold is discarded unheard. Guards the tap against a key
/// brushed on the way to something else.
/// </param>
/// <param name="BufferLimit">
/// Above this, further audio is dropped, the buffer stops growing and
/// <see cref="PushToTalkMicrophoneTap.BufferLimitReached"/> is raised. This is
/// a memory bound, not the length a recording may run to: Dictation's
/// recording limit is its own and shorter (<c>DictationHoldPolicy</c>), so
/// this is only reached when that limit was not enforced in time.
/// </param>
public sealed record MicrophoneTapOptions(TimeSpan MinimumHold, TimeSpan BufferLimit)
{
    /// <summary>
    /// 300 ms and 30 s — the loop's picks, written in time so they are one edit
    /// to change.
    /// </summary>
    /// <remarks>
    /// 300 ms is under the shortest plausible trigger phrase and over the
    /// longest accidental brush. 30 s is far past "send this to maya"
    /// and far short of a memory problem: 30 s of 48 kHz float mono is about
    /// 5.7 MB before conversion.
    /// </remarks>
    public static readonly MicrophoneTapOptions Default =
        new(TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30));

    /// <summary>
    /// When true, a release under <see cref="MinimumHold"/> LATCHES the
    /// capture open instead of discarding it as TooShort, and the next
    /// release stops it — tap to start, tap to stop, hold still works
    /// exactly as before, no mode setting (2026-08-21 round-2 grill,
    /// owner-settled for dictation). Off by default so voice workflows
    /// keep strict push-to-talk: their trigger-phrase grammar assumes a
    /// deliberate hold, and a brushed key opening a latched microphone
    /// there would be exactly the surprise this product exists to catch.
    /// </summary>
    public bool TapTogglesLatch { get; init; }

    /// <summary>
    /// Consulted at the release of a would-latch tap; answering true refuses
    /// the latch and the tap is discarded as the TooShort brush it always was.
    /// The shipped veto is "the hold was a chord" — Right Alt is AltGr on
    /// non-US layouts, and every AltGr character is a sub-300 ms tap that must
    /// NOT open a latched microphone (2026-08-21 QA audit, ship-blocker S1).
    /// Null means no veto, which keeps every existing caller's behaviour.
    /// </summary>
    public Func<bool>? LatchVeto { get; init; }
}

/// <summary>
/// Hold the hotkey, and the microphone is open for exactly that long.
/// </summary>
public interface IMicrophoneTap : IDisposable
{
    /// <summary>
    /// A hold finished. Raised exactly once per press/release pair, whatever
    /// happened in between — including when the microphone never opened —
    /// except for a hold whose open capture its owner discarded with
    /// <see cref="PushToTalkMicrophoneTap.AbortCapture"/>, which raises none.
    /// (A hold whose microphone never opened still reports
    /// <see cref="MicrophoneTapOutcome.DeviceUnavailable"/> at its release,
    /// aborted or not: there was no capture to discard.)
    /// </summary>
    event EventHandler<MicrophoneTapResult>? CaptureCompleted;

    /// <summary>Whether the endpoint is open right now.</summary>
    bool IsCapturing { get; }
}

/// <summary>
/// workflows P-03 — the microphone tap.
///
/// <para>
/// This subscribes to <see cref="IPushToTalkHotkey"/> itself rather than
/// offering a <c>Start</c> a caller could call. That is the same structural
/// move P-07 makes with focus: the guarantee wanted here is "the microphone is
/// open only between deliberate hotkey gestures", and a public <c>Start</c>
/// would demote it to a convention that holds until someone writes a second
/// call site. There is no path through this class that opens the endpoint
/// except a press. With <see cref="MicrophoneTapOptions.TapTogglesLatch"/> on
/// (dictation), a tap latches the capture open until the next release — the
/// endpoint still opens only on a press and closes on a release or on
/// <see cref="Dispose"/>; what the latch relaxes is which release closes it,
/// never who can open it.
/// </para>
///
/// <para>
/// It also owns the conversion, and that is why it is here in <c>net9.0</c>
/// rather than in the Windows adapter. WASAPI hands over the endpoint's mix
/// format — 48 kHz stereo float on this laptop, 44.1 kHz on the next one — and
/// the worker wants 16 kHz mono int16. Downmix, resample and quantise are
/// arithmetic, so they are provable on any machine, and what is left for
/// Windows is opening a device and pushing buffers. They run a buffer at a
/// time as the audio arrives, so a hold is kept at 16 kHz PCM16, 32 KB a
/// second, and never at the device's rate (<c>TapRecording</c>).
/// </para>
///
/// <para>
/// <b>It must not take the app down.</b> The endpoint is the default
/// <em>capture</em> endpoint, and every device call here is wrapped, so a
/// microphone that throws cannot throw out of the hotkey handler: that handler
/// runs on the UI thread, and an unhandled exception there does not fail this
/// feature, it fails the app.
/// </para>
///
/// <para>
/// Not thread-safe by the same reasoning as
/// <see cref="PushToTalkController"/>: press and release both arrive on the UI
/// thread. The one exception is buffer arrival, which is on the device's
/// capture thread, and which is why the recording is only ever appended to
/// there and only ever read after <see cref="IAudioCaptureDevice.Stop"/> has
/// returned.
/// </para>
/// </summary>
public sealed class PushToTalkMicrophoneTap : IMicrophoneTap
{
    private readonly IPushToTalkHotkey _hotkey;
    private readonly IAudioCaptureDevice _device;
    private readonly MicrophoneTapOptions _options;
    private readonly ILogger<PushToTalkMicrophoneTap> _logger;

    /// <summary>The hold's audio, at 16 kHz as it arrives (TapRecording.cs):
    /// 32 KB a second, whatever the device's rate.</summary>
    private readonly TapRecording _audio = new();
    private readonly object _sync = new();

    private int _sourceRateHz;
    private int _droppedChunks;
    private bool _capped;

    /// <summary>
    /// Correlates the press, capture, preview, accurate decode, routing and
    /// insertion for the current hold. It contains no user data.
    /// </summary>
    public string? CurrentOperationId { get; private set; }
    private bool _disposed;

    /// <summary>
    /// How many buffers the device handed over inside this hold, and how long
    /// after the endpoint opened the first of them arrived.
    /// </summary>
    /// <remarks>
    /// These exist so that "the microphone delivered nothing" is a MEASUREMENT
    /// rather than an adjective. Zero buffers over a 4 s hold is a dead or
    /// muted endpoint; forty buffers whose samples are all zero is a live
    /// endpoint on a muted channel; and before these counters the log said the
    /// same thing about both. Written under <see cref="_sync"/> on the capture
    /// thread, read once on release.
    /// </remarks>
    private int _buffers;
    private long _firstBufferTicks;

    /// <summary>
    /// Wall clock from the press to the release. Distinct from the duration
    /// derived from the sample count, and the gap between the two is the whole
    /// finding when a device under-delivers — macOS shipped a hold of 2.3 s
    /// that produced 0.30 s of audio, and nothing in its log put the two
    /// numbers on the same line.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _hold = new();

    /// <summary>
    /// Whether arriving buffers belong to an open hold. Distinct from
    /// <see cref="IsCapturing"/>, which is the UI thread's view: this one is
    /// read on the device's capture thread and so is only ever touched under
    /// <see cref="_sync"/>.
    /// </summary>
    private bool _collecting;

    /// <summary>
    /// A tap (release under MinimumHold, with
    /// <see cref="MicrophoneTapOptions.TapTogglesLatch"/> on) left the
    /// capture running; the NEXT release stops it. Set and read on the UI
    /// thread only — the latch decision and the stop both happen inside
    /// hotkey handlers. The stop rides the release rather than the press
    /// deliberately: other subscribers on the same Pressed event (the
    /// dictation session's Listening transition) must not observe a
    /// completed capture mid-press.
    /// </summary>
    private bool _latched;

    public PushToTalkMicrophoneTap(
        IPushToTalkHotkey hotkey,
        IAudioCaptureDevice device,
        MicrophoneTapOptions options,
        ILogger<PushToTalkMicrophoneTap> logger)
    {
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;
        _device.BufferCaptured += OnBufferCaptured;
    }

    public event EventHandler<MicrophoneTapResult>? CaptureCompleted;

    /// <summary>
    /// The capture's buffer just reached <see cref="MicrophoneTapOptions.BufferLimit"/>,
    /// held or latched, and drops everything after it. Raised once per
    /// capture, from a worker thread (the capture callback must not run
    /// subscriber code). The host marshals to the UI thread and ends the hold
    /// there, so it is transcribed at the cap instead of recording on into
    /// audio this tap already drops: Dictation ends it as it ends a hold at
    /// its recording limit; a host using the tap's own latch can call
    /// <see cref="CompleteLatched"/>. Formerly <c>LatchedCapReached</c>,
    /// which no Dictation code subscribed to.
    /// </summary>
    /// <remarks>
    /// The report names its capture (<see cref="MicrophoneTapLimit.OperationId"/>,
    /// read on the capture thread as the cap is reached): by the time a host
    /// reads it on its own thread, that capture may be over and the next one
    /// listening, and only the id tells them apart.
    /// </remarks>
    public event EventHandler<MicrophoneTapLimit>? BufferLimitReached;

    /// <summary>
    /// The bytes this hold's audio holds now: one second of 16 kHz PCM16 is
    /// 32 KB, so this is about the length kept, rounded up to a second. A
    /// diagnostic, read by the memory tests.
    /// </summary>
    public long RetainedAudioBytes
    {
        get
        {
            lock (_sync)
            {
                return _audio.RetainedBytes;
            }
        }
    }

    /// <summary>
    /// End a latched capture NOW, exactly as the ending tap would: stop the
    /// device and complete with the audio collected so far. UI thread only
    /// (same thread as the hotkey handlers). A no-op unless a latched capture
    /// is actually running.
    /// </summary>
    public void CompleteLatched()
    {
        if (!_disposed && _latched && IsCapturing)
        {
            OnReleased(this, EventArgs.Empty);
            // AFTER the completion, not before: the completion itself runs
            // the release path, and a deadline set first would be consumed
            // by it. The user has no idea the cap fired — their NEXT tap is
            // the stop gesture for a capture that already stopped, and
            // without this window it would OPEN a new capture and latch it
            // for another bound (QA verify NEW-2: the stop gesture must
            // never start a recording). A tap inside the window is consumed
            // as the ack; a deliberate hold still dictates normally.
            _capAckDeadlineMs = Environment.TickCount64 + CapAckWindowMs;
        }
    }

    /// <summary>Until when a tap is read as "acknowledging the auto-stop"
    /// rather than "start a new latched capture". UI thread only.</summary>
    private long _capAckDeadlineMs;

    /// <summary>How long after a cap-completion a tap reads as the stop
    /// acknowledgment. Long enough for the human reaction, short enough that
    /// a genuinely new dictation minutes later latches normally.</summary>
    private const long CapAckWindowMs = 5_000;

    /// <summary>
    /// Whether the last capture ended in <see cref="AbortCapture"/> rather than
    /// in a release. Read at the release that follows one: a hold the user
    /// cancelled DID have the microphone open, and reporting it as "the
    /// endpoint never opened for this hold" — the branch an aborted capture
    /// otherwise lands in — writes a false diagnosis over a deliberate cancel.
    /// Cleared only at the next press, so it never outlives the hold it ended.
    /// UI thread only, like the latch beside it.
    /// </summary>
    private bool _aborted;

    /// <summary>
    /// Stop any capture immediately and DISCARD it — no
    /// <see cref="CaptureCompleted"/> is raised. UI thread only. This is the
    /// Escape path: the session has already cancelled, so completing here
    /// would hand it audio it must not transcribe, while doing nothing left
    /// the endpoint open with the HUD gone (QA audit S2). The release that
    /// follows is silent too: the abort is remembered, so the cancelled hold
    /// is not logged as one the microphone never opened for.
    /// </summary>
    public void AbortCapture()
    {
        if (_disposed || !IsCapturing)
        {
            _latched = false;
            return;
        }

        _latched = false;
        _aborted = true;
        IsCapturing = false;
        _hold.Stop();

        lock (_sync)
        {
            _collecting = false;
            _audio.Clear();
        }

        try
        {
            _device.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Microphone capture failed to close cleanly on abort");
        }

        CurrentOperationId = null;
        _logger.LogInformation("Capture aborted; the audio was discarded unheard");
    }

    public bool IsCapturing { get; private set; }

    private void OnPressed(object? sender, EventArgs e)
    {
        if (_disposed || IsCapturing)
        {
            return;
        }

        CurrentOperationId = Guid.NewGuid().ToString("N")[..12];

        lock (_sync)
        {
            _collecting = false;
            _audio.Clear();
            _sourceRateHz = 0;
            _droppedChunks = 0;
            _capped = false;
            _buffers = 0;
            _firstBufferTicks = 0;
        }

        _latched = false;
        _aborted = false;
        _hold.Restart();

        // A device that throws on open is the same outcome as one that returns
        // false, and neither may reach the message pump.
        var started = false;
        try
        {
            started = _device.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Microphone capture failed to open");
        }

        if (!started)
        {
            // Deliberately not raised here. One result per hold, at release —
            // a caller that got a result mid-hold would have to decide whether
            // the one at release replaced it or followed it.
            _logger.LogWarning("Microphone unavailable; this hold will capture nothing");
            return;
        }

        lock (_sync)
        {
            _collecting = true;
        }

        IsCapturing = true;
        _logger.LogDebug(
            "Microphone capture started for operation {OperationId}",
            CurrentOperationId);
    }

    private void OnReleased(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsCapturing)
        {
            _hold.Stop();

            if (_aborted)
            {
                // The hold was cancelled: the microphone WAS open for it and
                // AbortCapture already said how it ended. Completing here
                // would report a device failure that never happened — both in
                // the log ("the endpoint never opened") and to subscribers as
                // a DeviceUnavailable result for a deliberate cancel.
                return;
            }

            // Named at the release as well as at the press. The press-time
            // warning says the endpoint would not open; this says the hold the
            // user just finished is the one that paid for it, and carries how
            // long they held a key that was recording nothing. Without it a
            // reader has to pair two lines by timestamp to learn that the
            // gesture and the failure belong together — and the failure must
            // never be reported downstream as "nothing was heard", which is
            // why the outcome below is DeviceUnavailable and not NoAudio.
            _logger.LogInformation(
                "Operation {OperationId} held for {HoldMs} ms with no microphone open — the endpoint never opened for this hold, "
                + "so no audio was captured (this is not silence)",
                CurrentOperationId,
                _hold.ElapsedMilliseconds);

            Complete(new MicrophoneTapResult(MicrophoneTapOutcome.DeviceUnavailable, null));
            return;
        }

        // A tap inside the cap-ack window is the user acknowledging the
        // auto-stop of a capture they think is still running — consume it
        // (the short capture discards as TooShort below) instead of latching
        // a fresh recording off a stop gesture (QA verify NEW-2).
        var isCapAckTap = false;
        if (_capAckDeadlineMs != 0 && _hold.Elapsed < _options.MinimumHold)
        {
            isCapAckTap = Environment.TickCount64 <= _capAckDeadlineMs;
            _capAckDeadlineMs = 0;
        }

        if (!_latched
            && !isCapAckTap
            && _options.TapTogglesLatch
            && _hold.Elapsed < _options.MinimumHold
            && _options.LatchVeto?.Invoke() != true)
        {
            // A tap, not a hold — and with the latch opted in, a tap is the
            // toggle-on gesture rather than a discarded brush. The capture
            // keeps running (the pill keeps showing Listening over a
            // genuinely open microphone) and the NEXT release stops it. The
            // wall clock decides, not the audio duration: the driver's first
            // buffer can lag the press by hundreds of ms, and the gesture is
            // what the user performed.
            _latched = true;
            _logger.LogInformation(
                "Tap ({HoldMs} ms, under the {MinimumMs} ms hold bar) latched the microphone open — "
                + "the next tap stops it (bound {MaxSeconds:0.#}s)",
                _hold.ElapsedMilliseconds,
                (int)_options.MinimumHold.TotalMilliseconds,
                _options.BufferLimit.TotalSeconds);
            return;
        }

        _latched = false;
        IsCapturing = false;
        _hold.Stop();

        try
        {
            _device.Stop();
        }
        catch (Exception ex)
        {
            // The buffers already collected are still good; a device that
            // throws on the way down has not un-said the sentence.
            _logger.LogWarning(ex, "Microphone capture failed to close cleanly");
        }

        // After Stop, not before: a buffer still in flight when the key came up
        // is the end of the sentence, and the next press clears the list anyway.
        lock (_sync)
        {
            _collecting = false;
        }

        Complete(Convert());
    }

    private void OnBufferCaptured(object? sender, AudioChunk chunk)
    {
        if (chunk is null || chunk.Samples.Length == 0 || chunk.Channels <= 0 || chunk.SampleRateHz <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (!_collecting || _capped)
            {
                // A buffer that arrives outside a hold belongs to nothing.
                // Appending it would put the tail of one sentence at the head
                // of the next.
                return;
            }

            _buffers++;
            if (_firstBufferTicks == 0)
            {
                // Elapsed, not a timestamp: the hold clock is started at the
                // press, so this is "how long after the key went down the
                // driver produced its first sample". A Bluetooth link
                // renegotiating reads in the hundreds of ms here and clips the
                // front of the sentence; a healthy internal array reads single
                // digits. Both look identical once the samples are in a list.
                _firstBufferTicks = _hold.ElapsedTicks;
            }

            if (_sourceRateHz == 0)
            {
                _sourceRateHz = chunk.SampleRateHz;
            }
            else if (_sourceRateHz != chunk.SampleRateHz)
            {
                // The endpoint's format does not change mid-capture, so this
                // is a device doing something unexpected rather than a case to
                // handle. Dropping is honest; resampling two rates as one is
                // not.
                _droppedChunks++;
                return;
            }

            // Downmixed, resampled and quantised here, a buffer at a time, so
            // the hold costs 32 KB a second rather than the device's rate in
            // floats (TapRecording). A mono buffer is read in place.
            var mono = chunk.Channels == 1
                ? (ReadOnlySpan<float>)chunk.Samples
                : WorkflowAudio.ToMono(chunk.Samples, chunk.Channels);
            if (_audio.Append(mono, _sourceRateHz, _options.BufferLimit.TotalSeconds))
            {
                // First time only: tell the host the bound is reached, so the
                // hold ends here rather than recording audio it has already
                // agreed to drop. Raised off-thread — this callback is on the
                // device's capture path and must not run subscriber code.
                _capped = true;
                ThreadPool.QueueUserWorkItem(
                    static s => s.Tap.BufferLimitReached?.Invoke(s.Tap, s.Limit),
                    (Tap: this, Limit: new MicrophoneTapLimit(CurrentOperationId)),
                    preferLocal: false);
            }
        }
    }

    private MicrophoneTapResult Convert()
    {
        using var operationScope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["OperationId"] = CurrentOperationId ?? "untracked",
            ["OperationKind"] = "dictation",
        });

        short[] pcm;
        long samples;
        float peak;
        float rms;
        float frameRmsPeak;
        float voicedSeconds;
        int sourceRateHz;
        int dropped;
        bool capped;
        int buffers;
        long firstBufferTicks;

        lock (_sync)
        {
            // The one copy a release makes; the blocks go with the clear.
            pcm = _audio.Finish();
            samples = _audio.SourceSamples;
            (peak, rms) = _audio.Level();
            frameRmsPeak = _audio.FrameRmsPeak();
            voicedSeconds = _audio.VoicedSeconds();
            _audio.Clear();
            sourceRateHz = _sourceRateHz;
            dropped = _droppedChunks;
            capped = _capped;
            buffers = _buffers;
            firstBufferTicks = _firstBufferTicks;
        }

        var heldMs = _hold.ElapsedMilliseconds;
        var firstBufferMs = firstBufferTicks == 0
            ? (long?)null
            : (long)(firstBufferTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        if (dropped > 0)
        {
            _logger.LogWarning(
                "Dropped {Count} buffer(s) whose sample rate differed from {Rate} Hz",
                dropped,
                sourceRateHz);
        }

        if (capped)
        {
            _logger.LogWarning(
                "Capture exceeded {Seconds:0.#}s; the tail was dropped",
                _options.BufferLimit.TotalSeconds);
        }

        if (samples == 0)
        {
            // The endpoint opened — IsCapturing was true to reach here — and
            // then produced nothing. Counted rather than asserted, because the
            // count is what separates the two devices that reach this line: a
            // driver that never called back at all (0 buffers) and one that
            // called back with buffers this tap refused (dropped > 0, a
            // mid-capture format change). "Delivered no audio" named neither.
            _logger.LogWarning(
                "Microphone was open for {HoldMs} ms and delivered {Buffers} buffer(s), 0 samples "
                + "({Dropped} buffer(s) dropped on a rate change) — the endpoint opened and the "
                + "driver produced no audio; this is a dead or muted device, not a quiet room",
                heldMs,
                buffers,
                dropped);

            return new MicrophoneTapResult(MicrophoneTapOutcome.NoAudio, null);
        }

        var duration = TimeSpan.FromSeconds((double)pcm.Length / WorkflowAudio.SampleRateHz);

        if (duration < _options.MinimumHold)
        {
            // INF, not DBG. "I held the key and nothing happened" is a support
            // report, and this is the outcome that produces it most often —
            // the numbers that answer it (how long they actually held, against
            // the bar) have to already be in the log when it arrives.
            _logger.LogInformation(
                "Hold of {Duration:0.###}s discarded as shorter than {Minimum:0.###}s "
                + "(key held {HoldMs} ms, peak {Peak:F4})",
                duration.TotalSeconds,
                _options.MinimumHold.TotalSeconds,
                heldMs,
                peak);

            return new MicrophoneTapResult(MicrophoneTapOutcome.TooShort, null);
        }

        // Everything one hold's capture stage can be asked about, on one line:
        // how long the key was down against how much audio came back (an 8x
        // gap between those two is a device under-delivering and was invisible
        // before), how loud it was against the floor, where the samples came
        // from, and how long the driver took to produce the first one.
        //
        // The loudest frame is on this line because it is the reading the
        // speech-energy verdict is actually made from, and peak/rms are not:
        // a hold rejected as a quiet room and one that squeaked through are
        // told apart by this number and by nothing else in the log.
        _logger.LogInformation(
            "Captured {Duration:0.##}s of {Rate} Hz mono audio — key held {HoldMs} ms, "
            + "{Samples} sample(s) from {Buffers} buffer(s) at {SourceRate} Hz, "
            + "peak {Peak:F4} rms {Rms:F4} (audible floor {Floor:F4}), "
            + "loudest {FrameMs} ms frame {FramePeak:F4} over {Voiced:0.##}s voiced, "
            + "first buffer {FirstMs} ms after the press",
            duration.TotalSeconds,
            WorkflowAudio.SampleRateHz,
            heldMs,
            samples,
            buffers,
            sourceRateHz,
            peak,
            rms,
            WorkflowAudio.AudibleFloor,
            (int)(WorkflowAudio.FrameSeconds * 1000),
            frameRmsPeak,
            voicedSeconds,
            firstBufferMs);

        if (peak < WorkflowAudio.AudibleFloor || rms < WorkflowAudio.AudibleFloor)
        {
            // The macOS defect, said out loud on this side. There, a bound
            // device that was in fact unbound reported "peak 0.0029" and a
            // reader had to know that 0.0029 was small; the audio still went to
            // the recogniser, which returned nothing, and the user was told
            // nothing was heard. The audio still goes on from here too — a
            // measurement must not become a gate — but the log now names the
            // microphone as the suspect BEFORE the recogniser gets the blame.
            //
            // BOTH readings are checked against the one floor, and the reason
            // is a hold measured on this device: peak 0.0351, rms 0.0016 over
            // 6.7 seconds. The peak alone cleared the floor and said nothing;
            // the rms says what actually happened, which is that there was no
            // speech-level energy in the whole hold — a microphone that is
            // working, pointed at a room where nobody is close enough to it.
            // Ordinary speech sits an order of magnitude above this.
            _logger.LogWarning(
                "Captured audio is below the audible floor on {Which}: peak {Peak:F4}, "
                + "rms {Rms:F4}, floor {Floor:F4}, over {Samples} sample(s) from {Device}. "
                + "The endpoint is open and delivering, and what it is delivering has no speech "
                + "in it — a muted microphone, a channel with no signal on it, the wrong "
                + "endpoint, or a speaker too far from it. The recogniser will hear nothing, "
                + "and that will not be the recogniser's fault",
                peak < WorkflowAudio.AudibleFloor && rms < WorkflowAudio.AudibleFloor
                    ? "both peak and rms"
                    : peak < WorkflowAudio.AudibleFloor ? "peak" : "rms",
                peak,
                rms,
                WorkflowAudio.AudibleFloor,
                samples,
                (_device as INamedAudioCaptureDevice)?.LastOpenedEndpointName ?? "the capture endpoint");
        }

        return new MicrophoneTapResult(
            MicrophoneTapOutcome.Captured,
            new MicrophoneCapture(
                pcm,
                WorkflowAudio.SampleRateHz,
                WorkflowAudio.Channels,
                duration)
            {
                // Measured HERE and carried, rather than decided here.
                //
                // This tap deliberately does not gate: the 2026-08-19
                // microphone defect was diagnosable precisely because a peak
                // of 0.0029 was recorded and handed on, and a capture stage
                // that dropped the buffer would have deleted its own evidence.
                // What changed is that the reading now TRAVELS, so the layer
                // that owns the user-facing outcome (DictationSession) can
                // decline to hand a silent hold to a recogniser that will
                // invent a sentence for it — while the audio itself is still
                // in the result for the wav recorder and for anyone reading a
                // support report.
                //
                // Measured on the source-rate mono as it arrived, before the
                // resample, so it describes what the device actually
                // delivered (TapRecording).
                FrameRmsPeak = frameRmsPeak,
                VoicedSeconds = voicedSeconds,
            });
    }

    private void Complete(MicrophoneTapResult result)
    {
        var completed = result.OperationId is null
            ? result with { OperationId = CurrentOperationId }
            : result;
        CaptureCompleted?.Invoke(this, completed);
        CurrentOperationId = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _hotkey.Pressed -= OnPressed;
        _hotkey.Released -= OnReleased;
        _device.BufferCaptured -= OnBufferCaptured;

        // An open endpoint outliving the app is the one leak that is audible:
        // Windows shows a microphone-in-use indicator for a process that has
        // gone. PushToTalkController raises Released on its own Dispose, but
        // disposal order is not something to depend on.
        lock (_sync)
        {
            _collecting = false;
        }

        if (IsCapturing)
        {
            IsCapturing = false;

            try
            {
                _device.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Microphone capture failed to close on shutdown");
            }
        }

        CurrentOperationId = null;
    }
}
