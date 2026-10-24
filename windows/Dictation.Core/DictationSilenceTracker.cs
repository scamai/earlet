using Dictation.Speech;

namespace Dictation.Core;

/// <summary>
/// Whether a listening hold is hearing speech: the in-hold silence detector
/// behind the hands-free silence stop and the held stuck-key stop
/// (<see cref="DictationHoldPolicy"/>). Fed the microphone's buffers on the
/// capture thread; read by the controller's tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> The audio is cut into 30 ms frames. A frame is speech
/// when its RMS exceeds the larger of <see cref="Floor"/> and the 10th
/// percentile of the frames in the last 10 s raised by 10 dB
/// (<see cref="NoiseMargin"/>): the floor follows the room, so a fan or a
/// noisy café is not speech and the pauses in it are found. Only a run of
/// speech frames at least 250 ms long (<see cref="SpeechRun"/>, nine frames)
/// counts, so a key click or a cough does not keep the microphone open.
/// </para>
/// <para>
/// <b>Which way it fails.</b> A steady sound becomes the floor and is never
/// speech; a second talker, music or typing that varies like speech is
/// speech, and keeps the hold open until the limit. Measured on synthetic
/// probes (no real voices yet): at −45 and −35 dBFS of noise it still found
/// the pauses where the fixed floor found none, and its longest false
/// silence inside speech was 2.9 s, far below any stop. When it is wrong it
/// is wrong toward not stopping.
/// </para>
/// <para>
/// <b>Its own constants.</b> <see cref="Floor"/> has the value of
/// <see cref="WorkflowAudio.AudibleFloor"/> and the frame its length, but they
/// are not shared: the four copies of the fixed floor decide what is sent to
/// the recogniser, and this decides when to stop listening.
/// </para>
/// <para>
/// Thread-safe: <see cref="Offer(object?, AudioChunk)"/> runs on the capture
/// thread under a lock, and the counters are read without one.
/// </para>
/// </remarks>
public sealed class DictationSilenceTracker
{
    /// <summary>The fixed floor under the adaptive one: 0.004 RMS.</summary>
    public const float Floor = 0.004f;

    /// <summary>The frame: 30 ms.</summary>
    public const double FrameSeconds = 0.03;

    /// <summary>How far above the room a frame must be: 10 dB, ×3.16 in RMS.</summary>
    public static readonly double NoiseMargin = Math.Pow(10, 10 / 20.0);

    /// <summary>The percentile of recent frames that stands for the room.</summary>
    public const double NoisePercentile = 0.10;

    /// <summary>How much recent audio the room is read from: 10 s.</summary>
    public static readonly TimeSpan NoiseWindow = TimeSpan.FromSeconds(10);

    /// <summary>The shortest run of speech frames that counts as speech: 250 ms.</summary>
    public static readonly TimeSpan SpeechRun = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly float[] _recent = new float[(int)Math.Round(NoiseWindow.TotalSeconds / FrameSeconds)];
    private readonly float[] _sorted;
    private readonly int _runNeeded = (int)Math.Ceiling(SpeechRun.TotalSeconds / FrameSeconds - 1e-9);

    private int _rateHz;
    private int _frameLength;
    private double _frameSum;
    private int _frameFill;
    private int _recentCount;
    private int _recentNext;
    private int _run;
    private long _frames;
    private long _speechFrames;
    private long _lastSpeechFrame = -1;

    public DictationSilenceTracker() => _sorted = new float[_recent.Length];

    /// <summary>Frames counted as speech so far: it grows while speech goes
    /// on, so a reader that sees it change knows speech was heard since it
    /// last looked.</summary>
    public long SpeechFrames => Interlocked.Read(ref _speechFrames);

    /// <summary>Whether any speech has been heard.</summary>
    public bool HeardSpeech => SpeechFrames > 0;

    /// <summary>Audio judged so far, in whole frames.</summary>
    public TimeSpan Heard => TimeSpan.FromSeconds(Interlocked.Read(ref _frames) * FrameSeconds);

    /// <summary>Audio since the last speech frame, or since the start when
    /// none was heard: the silence in audio time. The controller judges by
    /// its clock instead, so a microphone that stops delivering is silence
    /// too; this is for tests of the rule on its own.</summary>
    public TimeSpan SilentFor
    {
        get
        {
            lock (_sync)
            {
                return TimeSpan.FromSeconds((_frames - (_lastSpeechFrame + 1)) * FrameSeconds);
            }
        }
    }

    /// <summary>The microphone's handler: one buffer, downmixed. Capture
    /// thread; never throws.</summary>
    public void Offer(object? sender, AudioChunk chunk)
    {
        if (chunk?.Samples is not { } samples || chunk.Channels <= 0 || chunk.SampleRateHz <= 0)
        {
            return;
        }

        Offer(chunk.Channels == 1 ? samples : WorkflowAudio.ToMono(samples, chunk.Channels), chunk.SampleRateHz);
    }

    /// <summary>Judge <paramref name="mono"/>, at <paramref name="rateHz"/>.
    /// A partial frame waits for the next buffer; a change of rate starts the
    /// frame again.</summary>
    public void Offer(ReadOnlySpan<float> mono, int rateHz)
    {
        if (rateHz <= 0)
        {
            return;
        }

        lock (_sync)
        {
            if (rateHz != _rateHz)
            {
                _rateHz = rateHz;
                _frameLength = Math.Max(1, (int)Math.Round(rateHz * FrameSeconds));
                _frameSum = 0;
                _frameFill = 0;
            }

            foreach (var raw in mono)
            {
                // One NaN or infinity from a driver must not poison the room's
                // floor, which would then call every frame silence.
                var sample = float.IsFinite(raw) ? raw : 0f;
                _frameSum += (double)sample * sample;
                if (++_frameFill == _frameLength)
                {
                    Judge((float)Math.Sqrt(_frameSum / _frameLength));
                    _frameSum = 0;
                    _frameFill = 0;
                }
            }
        }
    }

    /// <summary>The threshold a frame must clear now: the fixed floor, or the
    /// room's 10th percentile over the last 10 s plus 10 dB, whichever is
    /// higher. With no frames yet, the fixed floor.</summary>
    public float Threshold
    {
        get
        {
            lock (_sync)
            {
                return CurrentThreshold();
            }
        }
    }

    private float CurrentThreshold()
    {
        if (_recentCount == 0)
        {
            return Floor;
        }

        Array.Copy(_recent, _sorted, _recentCount);
        Array.Sort(_sorted, 0, _recentCount);
        var room = _sorted[(int)Math.Floor(NoisePercentile * (_recentCount - 1))];
        return (float)Math.Max(Floor, room * NoiseMargin);
    }

    private void Judge(float rms)
    {
        // Against the frames before this one: a frame is not part of the
        // room it is measured against.
        var speech = rms > CurrentThreshold();
        _recent[_recentNext] = rms;
        _recentNext = (_recentNext + 1) % _recent.Length;
        _recentCount = Math.Min(_recentCount + 1, _recent.Length);

        if (!speech)
        {
            _run = 0;
        }
        else if (++_run == _runNeeded)
        {
            // The run just became long enough: all of it was speech.
            Interlocked.Add(ref _speechFrames, _run);
            _lastSpeechFrame = _frames;
        }
        else if (_run > _runNeeded)
        {
            Interlocked.Increment(ref _speechFrames);
            _lastSpeechFrame = _frames;
        }

        Interlocked.Increment(ref _frames);
    }
}
