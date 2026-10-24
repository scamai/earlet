using Dictation.Speech;

namespace Dictation.Core;

/// <summary>
/// A take's device buffers to the worker's format as they arrive: mono, 16
/// kHz, signed 16-bit (<see cref="WorkflowAudio"/>). The spool writes what
/// this returns, so a take is on disk at 32 KB a second while it is recorded
/// rather than as 48 kHz float in memory only.
/// </summary>
/// <remarks>
/// <para>
/// The same arithmetic as the tap's one-shot conversion at the release
/// (<see cref="WorkflowAudio.ToMono"/>, then <see cref="WorkflowAudio.Resample"/>'s
/// linear interpolation, then <see cref="WorkflowAudio.ToPcm16"/>), carried
/// across buffer edges: the position of the next output sample and the last
/// input sample of the buffer before are kept, so a take converted buffer by
/// buffer is the take converted at once, but for the one sample the one-shot
/// conversion takes past the end (its right neighbour clamped to the last).
/// </para>
/// <para>
/// Single producer: <see cref="Convert"/> is called from the device's capture
/// thread only. <see cref="Samples"/> may be read from any thread.
/// </para>
/// </remarks>
public sealed class DictationTakeConverter
{
    private int _sourceRateHz;
    private bool _havePrevious;
    private float _previous;

    /// <summary>Source samples consumed before the current buffer.</summary>
    private long _consumed;

    /// <summary>Output samples produced so far.</summary>
    private long _samples;

    /// <summary>Output samples produced so far, at
    /// <see cref="WorkflowAudio.SampleRateHz"/>.</summary>
    public long Samples => Interlocked.Read(ref _samples);

    /// <summary>The audio converted so far, from the sample count.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples / WorkflowAudio.SampleRateHz);

    /// <summary>Buffers dropped because their rate was not the take's: the
    /// endpoint's format does not change mid-capture, so a device doing so
    /// is dropped from, as the tap drops it.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// <paramref name="chunk"/>, downmixed, resampled from its rate and
    /// quantised: as many 16 kHz samples as its input completes.
    /// </summary>
    public short[] Convert(AudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Samples.Length == 0 || chunk.Channels <= 0 || chunk.SampleRateHz <= 0)
        {
            return [];
        }

        if (_sourceRateHz == 0)
        {
            _sourceRateHz = chunk.SampleRateHz;
        }
        else if (_sourceRateHz != chunk.SampleRateHz)
        {
            Dropped++;
            return [];
        }

        var mono = WorkflowAudio.ToMono(chunk.Samples, chunk.Channels);
        if (mono.Length == 0)
        {
            return [];
        }

        var converted = _sourceRateHz == WorkflowAudio.SampleRateHz
            ? mono
            : Resample(mono);
        _consumed += mono.Length;
        _previous = mono[^1];
        _havePrevious = true;
        Interlocked.Add(ref _samples, converted.Length);
        return WorkflowAudio.ToPcm16(converted);
    }

    /// <summary>The outputs whose left and right neighbours are both known
    /// once <paramref name="mono"/> has arrived.</summary>
    private float[] Resample(float[] mono)
    {
        var step = (double)_sourceRateHz / WorkflowAudio.SampleRateHz;
        var last = _consumed + mono.Length - 1;
        var output = new List<float>((int)(mono.Length / step) + 2);
        var produced = Interlocked.Read(ref _samples);
        while (true)
        {
            var position = produced * step;
            var left = (long)position;
            if (left + 1 > last)
            {
                break;
            }

            var leftValue = Sample(left, mono);
            var rightValue = Sample(left + 1, mono);
            var fraction = (float)(position - left);
            output.Add(leftValue + ((rightValue - leftValue) * fraction));
            produced++;
        }

        return [.. output];
    }

    /// <summary>The source sample at <paramref name="index"/>: in this
    /// buffer, or the last one of the buffer before.</summary>
    private float Sample(long index, float[] mono)
    {
        var at = index - _consumed;
        if (at >= 0)
        {
            return mono[at];
        }

        // Only the sample just before this buffer is ever asked for: every
        // output before it was produced while its own buffer was current.
        return _havePrevious ? _previous : mono[0];
    }
}
