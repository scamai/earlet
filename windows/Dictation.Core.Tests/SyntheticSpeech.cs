using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// Synthetic level streams for the in-hold silence tracker
/// (<see cref="DictationSilenceTracker"/>), repeatable from their seeds: a
/// room's white noise, speech as syllables over it, and key clicks. They show
/// which way the tracker's rule fails, not where a real voice sits. Used on
/// their own by SilenceStopTests and through <see cref="FakeMicrophone"/> by
/// every test that talks into a hold.
/// </summary>
internal static class SyntheticSpeech
{
    /// <summary>The rate every stream is made at.</summary>
    public const int Rate = 16_000;

    /// <summary>A quiet room: 0.0005 RMS, under the fixed floor.</summary>
    public const float QuietRoom = 0.0005f;

    /// <summary>A syllable's loudness: a 220 Hz tone at 0.3.</summary>
    public const float Voice = 0.3f;

    /// <summary><paramref name="count"/> samples of uniform white noise at
    /// <paramref name="rms"/>.</summary>
    public static float[] Noise(int count, float rms, int seed)
    {
        var random = new Random(seed);
        var samples = new float[count];
        var scale = rms * MathF.Sqrt(3);
        for (var i = 0; i < count; i++)
        {
            samples[i] = scale * ((2 * random.NextSingle()) - 1);
        }

        return samples;
    }

    /// <summary><paramref name="seconds"/> of white noise at <paramref name="rms"/>.</summary>
    public static float[] Noise(double seconds, float rms, int seed) => Noise((int)(Rate * seconds), rms, seed);

    /// <summary>
    /// <paramref name="seconds"/> of speech as the silence tracker hears it:
    /// 400 ms syllables of a 220 Hz tone at <paramref name="level"/>, with
    /// 100 ms dips to the room between them, over <paramref name="room"/> RMS
    /// of noise. A steady tone is not speech to the tracker: it becomes the
    /// room's floor.
    /// </summary>
    public static float[] Talk(double seconds, float room, int seed = 1, float level = Voice)
    {
        var samples = Noise(seconds, room, seed);
        for (var i = 0; i < samples.Length; i++)
        {
            if (i % (Rate / 2) < Rate * 2 / 5)
            {
                samples[i] += level * MathF.Sin(2 * MathF.PI * 220 * i / Rate);
            }
        }

        return samples;
    }

    /// <summary>A key click over the first 30 ms of <paramref name="samples"/>:
    /// full scale, shorter than any syllable.</summary>
    public static float[] WithClick(float[] samples)
    {
        for (var i = 0; i < Math.Min(samples.Length, Rate * 30 / 1000); i++)
        {
            samples[i] += i % 2 == 0 ? 0.9f : -0.9f;
        }

        return samples;
    }
}

// Part of FakeMicrophone (TestPlatform.cs): the synthetic streams above,
// handed over as the device would hand them, each with a seed of its own.
internal sealed partial class FakeMicrophone
{
    private int _noiseSeed = 1;

    /// <summary>One buffer, as the device hands it over.</summary>
    public void Emit(AudioChunk chunk) => BufferCaptured?.Invoke(this, chunk);

    /// <summary><paramref name="length"/> of speech over <paramref name="room"/>
    /// (<see cref="SyntheticSpeech.Talk"/>); a <paramref name="level"/> under
    /// the room's floor plus 10 dB is speech the tracker cannot hear.</summary>
    public void Talk(TimeSpan length, float room = SyntheticSpeech.QuietRoom, float level = SyntheticSpeech.Voice) =>
        Emit(new AudioChunk(SyntheticSpeech.Talk(length.TotalSeconds, room, _noiseSeed++, level), SyntheticSpeech.Rate, 1));

    /// <summary><paramref name="length"/> of a room with no one speaking:
    /// white noise at <paramref name="rms"/> (a quiet room by default;
    /// 0.0178 is −35 dBFS).</summary>
    public void Room(TimeSpan length, float rms = SyntheticSpeech.QuietRoom) =>
        Emit(new AudioChunk(SyntheticSpeech.Noise(length.TotalSeconds, rms, _noiseSeed++), SyntheticSpeech.Rate, 1));

    /// <summary>One key click: 30 ms at full scale over the room.</summary>
    public void Click(float room = SyntheticSpeech.QuietRoom) =>
        Emit(new AudioChunk(
            SyntheticSpeech.WithClick(SyntheticSpeech.Noise(SyntheticSpeech.Rate * 30 / 1000, room, _noiseSeed++)),
            SyntheticSpeech.Rate,
            1));
}
