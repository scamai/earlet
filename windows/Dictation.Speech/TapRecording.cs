namespace Dictation.Speech;

/// <summary>
/// One hold's audio as it arrives: downmixed, resampled to
/// <see cref="WorkflowAudio.SampleRateHz"/> and quantised to signed 16-bit per
/// buffer, kept in fixed one-second blocks; and, beside it, the level
/// statistics of the audio as the device delivered it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why as it arrives.</b> The tap used to keep every buffer at the
/// device's rate as 32-bit floats and convert at the release: 192 KB a
/// second at 48 kHz, about 230 MB for twenty minutes, plus about 170 MB of
/// copies at the release, held in a list whose capacity doubles. Here a
/// second costs 32 KB whatever the device's rate, so twenty minutes is about
/// 38 MB, and the release makes one copy. That is what lets the recording
/// limit be an experience choice rather than a memory bound.
/// </para>
/// <para>
/// <b>Exactly the old arithmetic.</b> The samples are the ones
/// <c>WorkflowAudio.ToPcm16(WorkflowAudio.Resample(mono, rate))</c> made from
/// the whole hold at the release, bit for bit: the same interpolation
/// positions, the same clamp, and the same count, so a sample the batch
/// count would not have kept is trimmed at <see cref="Finish"/>. The levels
/// are the ones <see cref="WorkflowAudio.Level"/>,
/// <see cref="WorkflowAudio.FrameRmsPeak"/> and
/// <see cref="WorkflowAudio.VoicedSeconds"/> read from the source-rate mono,
/// accumulated in the same order.
/// </para>
/// <para>
/// Not thread-safe: the tap appends on the capture thread and finishes on
/// the UI thread, both under its own lock.
/// </para>
/// </remarks>
internal sealed class TapRecording
{
    /// <summary>One block: a second of 16 kHz audio, 32 KB. The most a
    /// recording holds beyond its samples is one partly filled block.</summary>
    public const int BlockSamples = WorkflowAudio.SampleRateHz;

    private readonly List<short[]> _blocks = [];
    private int _lastBlockFill;

    private int _sourceRateHz;
    private double _step;
    private long _sourceSamples;
    private long _outputSamples;
    private float _last;

    private float _peak;
    private double _sumSquares;
    private int _frameLength;
    private double _frameSum;
    private int _frameFill;
    private float _loudestFrame;
    private int _voicedFrames;

    /// <summary>Mono samples the device delivered and the recording kept.</summary>
    public long SourceSamples => _sourceSamples;

    /// <summary>The bytes the blocks hold now: what the hold costs in memory
    /// until the release.</summary>
    public long RetainedBytes => (long)_blocks.Count * BlockSamples * sizeof(short);

    /// <summary>Forget everything: a new hold.</summary>
    public void Clear()
    {
        _blocks.Clear();
        _lastBlockFill = 0;
        _sourceRateHz = 0;
        _step = 0;
        _sourceSamples = 0;
        _outputSamples = 0;
        _last = 0;
        _peak = 0;
        _sumSquares = 0;
        _frameLength = 0;
        _frameSum = 0;
        _frameFill = 0;
        _loudestFrame = 0;
        _voicedFrames = 0;
    }

    /// <summary>
    /// Add <paramref name="mono"/>, at <paramref name="rateHz"/>, keeping at
    /// most <paramref name="limitSeconds"/> of source audio in all.
    /// </summary>
    /// <returns>True when the limit cut this buffer or an earlier one: the
    /// recording is full and takes nothing more.</returns>
    public bool Append(ReadOnlySpan<float> mono, int rateHz, double limitSeconds)
    {
        if (_sourceRateHz == 0)
        {
            _sourceRateHz = rateHz;
            _step = (double)rateHz / WorkflowAudio.SampleRateHz;
            _frameLength = (int)(rateHz * WorkflowAudio.FrameSeconds);
        }

        var limit = limitSeconds > 0 ? (long)(limitSeconds * _sourceRateHz) : long.MaxValue;
        var room = limit - _sourceSamples;
        if (room <= 0)
        {
            return true;
        }

        var capped = mono.Length >= room;
        if (mono.Length > room)
        {
            mono = mono[..(int)room];
        }

        Measure(mono);
        Resample(mono);
        return capped;
    }

    /// <summary>
    /// The recording as one array, at the count the release-time conversion
    /// would have made: <c>floor(source samples × 16 kHz ÷ rate)</c>. The
    /// samples whose right neighbour had not yet arrived are the last source
    /// sample, as the batch clamp made them; any the batch count would not
    /// have kept are dropped.
    /// </summary>
    public short[] Finish()
    {
        if (_sourceSamples == 0 || _sourceRateHz <= 0)
        {
            return [];
        }

        var count = _sourceRateHz == WorkflowAudio.SampleRateHz
            ? _sourceSamples
            : _sourceSamples * WorkflowAudio.SampleRateHz / _sourceRateHz;
        var pcm = new short[count];
        var kept = (int)Math.Min(count, _outputSamples);
        var copied = 0;
        foreach (var block in _blocks)
        {
            var take = Math.Min(BlockSamples, kept - copied);
            if (take <= 0)
            {
                break;
            }

            Array.Copy(block, 0, pcm, copied, take);
            copied += take;
        }

        var tail = Quantise(_last);
        for (var i = kept; i < count; i++)
        {
            pcm[i] = tail;
        }

        return pcm;
    }

    /// <summary>
    /// The loudest sample and the RMS, as <see cref="WorkflowAudio.Level"/>
    /// reads them.
    /// </summary>
    public (float Peak, float Rms) Level() =>
        _sourceSamples == 0 ? (0f, 0f) : (_peak, (float)Math.Sqrt(_sumSquares / _sourceSamples));

    /// <summary>The loudest 30 ms frame's RMS, as
    /// <see cref="WorkflowAudio.FrameRmsPeak"/> reads it: whole-recording RMS
    /// when it is shorter than one frame.</summary>
    public float FrameRmsPeak()
    {
        if (_sourceSamples == 0)
        {
            return 0f;
        }

        return _frameLength <= 0 || _sourceSamples < _frameLength ? Level().Rms : _loudestFrame;
    }

    /// <summary>Seconds in frames above the audible floor, as
    /// <see cref="WorkflowAudio.VoicedSeconds"/> reads them.</summary>
    public float VoicedSeconds() =>
        _frameLength <= 0 || _sourceSamples < _frameLength ? 0f : _voicedFrames * WorkflowAudio.FrameSeconds;

    private void Measure(ReadOnlySpan<float> mono)
    {
        foreach (var sample in mono)
        {
            var magnitude = MathF.Abs(sample);
            if (magnitude > _peak)
            {
                _peak = magnitude;
            }

            var square = (double)sample * sample;
            _sumSquares += square;
            if (_frameLength <= 0)
            {
                continue;
            }

            _frameSum += square;
            if (++_frameFill < _frameLength)
            {
                continue;
            }

            var mean = _frameSum / _frameLength;
            var rms = (float)Math.Sqrt(mean);
            if (rms > _loudestFrame)
            {
                _loudestFrame = rms;
            }

            if (Math.Sqrt(mean) > WorkflowAudio.AudibleFloor)
            {
                _voicedFrames++;
            }

            _frameSum = 0;
            _frameFill = 0;
        }
    }

    /// <summary>
    /// Emit every 16 kHz sample whose two source neighbours have now arrived:
    /// output <c>i</c> sits at source position <c>i × step</c>, between the
    /// samples <c>left</c> and <c>left + 1</c>. Only the last source sample
    /// is carried to the next buffer, because no later output needs an
    /// earlier one.
    /// </summary>
    private void Resample(ReadOnlySpan<float> mono)
    {
        if (mono.Length == 0)
        {
            return;
        }

        var first = _sourceSamples;
        var total = first + mono.Length;
        var carried = _last;
        if (_sourceRateHz == WorkflowAudio.SampleRateHz)
        {
            foreach (var sample in mono)
            {
                Add(Quantise(sample));
            }
        }
        else
        {
            while (true)
            {
                var position = _outputSamples * _step;
                var left = (long)position;
                if (left + 1 >= total)
                {
                    break;
                }

                var a = left < first ? carried : mono[(int)(left - first)];
                var b = mono[(int)(left + 1 - first)];
                var fraction = (float)(position - left);
                Add(Quantise(a + ((b - a) * fraction)));
            }
        }

        _sourceSamples = total;
        _last = mono[^1];
    }

    private void Add(short sample)
    {
        if (_blocks.Count == 0 || _lastBlockFill == BlockSamples)
        {
            _blocks.Add(new short[BlockSamples]);
            _lastBlockFill = 0;
        }

        _blocks[^1][_lastBlockFill++] = sample;
        _outputSamples++;
    }

    /// <summary><see cref="WorkflowAudio.ToPcm16"/> for one sample.</summary>
    private static short Quantise(float sample)
    {
        var scaled = (int)MathF.Round(sample * short.MaxValue);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
