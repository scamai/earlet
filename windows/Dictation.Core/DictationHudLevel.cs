using Dictation.Speech;

namespace Dictation.Core;

/// <summary>
/// The level bars on the floating indicator: how a microphone peak becomes
/// three bar heights. A port of the macOS level pipeline
/// (<c>DictationHUDContent.normalizedLevel</c>, <c>barHeights</c> and
/// <c>easedLevel</c> in <c>macos/Sources/DictationAppCore/DictationHUD.swift</c>,
/// and <c>DictationModel.noteInputLevel</c>'s smoothing), with the Mac's
/// numbers, so the two indicators read a voice the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the Mac's.</b> The peak of each sample period is smoothed
/// with a fast attack (<see cref="Smoothed"/>), handed to the window, eased
/// toward (<see cref="Eased"/>, through <see cref="DictationHudBars"/>), and
/// only then mapped through the decibel
/// window (<see cref="Normalized"/>) to bar heights (<see cref="BarHeights"/>).
/// Smoothing and easing work on the amplitude, not on the mapped level.
/// </para>
/// <para>
/// <b>Plain <c>net9.0</c>.</b> Every number here is a product rule, checked on
/// a machine with no WPF; the window only applies it.
/// </para>
/// </remarks>
public static class DictationHudLevel
{
    /// <summary>How many bars stand in for the mark while the hold listens.</summary>
    public const int BarCount = 3;

    /// <summary>A bar's height at the floor of the window, in DIPs.</summary>
    public const double MinHeight = 3;

    /// <summary>The tallest bar's height at the ceiling, in DIPs.</summary>
    public const double MaxHeight = 13;

    /// <summary>
    /// The bottom of the perceptual window, in dB. Real speech peaks on the
    /// Mac measured about 0.05–0.09 (−26 to −21 dB, capture forensics,
    /// 2026-09-29); a linear map drew them at the floor. This window puts
    /// ordinary speech at half the bar range or more.
    /// </summary>
    /// <remarks>
    /// Set from the Mac's capture. WASAPI shared-mode gain and automatic gain
    /// control differ, so on Windows the window is unmeasured: it may need
    /// recalibrating once speech has been captured on Windows hardware.
    /// </remarks>
    public const float FloorDb = -45;

    /// <summary>The top of the window, in dB: a shout or clipping is 1.</summary>
    public const float CeilingDb = -8;

    /// <summary>
    /// The least time between two samples of the peak while the hold listens.
    /// The controller's 30 ms tick takes a sample on the first tick at least
    /// this long after the last, which is every third tick (90 ms, about 11 a
    /// second), close to the Mac's one sample per capture chunk (about 85 ms).
    /// The smoothing and easing factors are per sample, so this rate is part
    /// of their meaning.
    /// </summary>
    /// <remarks>
    /// 75, not the Mac's 85: the controller's clock is
    /// <c>Environment.TickCount64</c>, which moves in steps of 15.625 ms. With
    /// the poll firing every 30 ms, three ticks can read as 78 ms, and an
    /// 85 ms gate would then wait for a fourth, so samples would land unevenly
    /// every 90 or 120 ms. Two ticks read at most 63 ms and three at least
    /// 78 ms, whether the poll fires every 30 or every 31.25 ms, so a 75 ms
    /// gate is every third tick on either.
    /// </remarks>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(75);

    /// <summary>The silhouette the bars keep around the level, left to
    /// right, the first the tallest: they move only when the level moves.</summary>
    private static readonly double[] Silhouette = [1.0, 0.62, 0.85];

    /// <summary>
    /// The next smoothed level for a sample whose peak is
    /// <paramref name="peak"/>: a louder peak is taken at once (fast attack, so
    /// a word shows on the first sample), a quieter one blends in (0.55 of the
    /// old level, 0.45 of the new), so the bars follow speech instead of
    /// strobing between syllables.
    /// </summary>
    public static float Smoothed(float current, float peak) =>
        peak > current ? peak : current * 0.55f + peak * 0.45f;

    /// <summary>
    /// A peak amplitude (0…1) mapped into the perceptual 0…1 window through
    /// 20·log10, clamped: digital silence, anything under
    /// <see cref="FloorDb"/> and anything that is not a positive number is 0;
    /// anything over <see cref="CeilingDb"/> is 1.
    /// </summary>
    public static float Normalized(float peak)
    {
        if (!(peak > 0))
        {
            // Zero, negative and NaN: the logarithm has no answer for them,
            // and NaN would survive the clamp below.
            return 0;
        }

        var db = 20f * MathF.Log10(peak);
        return Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0f, 1f);
    }

    /// <summary>
    /// The three bar heights, in DIPs, for a perceptual
    /// <paramref name="level"/> (0…1, see <see cref="Normalized"/>).
    /// </summary>
    /// <remarks>
    /// Silence is a flat floor: a zero level is every bar at
    /// <see cref="MinHeight"/>, with no pulse and no breathing, because a quiet
    /// room that wiggles reads as a bug. Above it the bars hold a fixed
    /// silhouette around the level. A level outside 0…1 is clamped, never
    /// taller than <see cref="MaxHeight"/> or under the floor.
    /// </remarks>
    public static double[] BarHeights(float level)
    {
        // Written so NaN lands on the floor too: it fails "> 0".
        var clamped = level > 0 ? Math.Min(1d, level) : 0d;
        var levelHeight = MinHeight + (MaxHeight - MinHeight) * clamped;
        return Silhouette.Select(factor => MinHeight + (levelHeight - MinHeight) * factor).ToArray();
    }

    /// <summary>
    /// One easing step from the level drawn toward the level just sampled.
    /// At the sample rate a factor of 0.7 lands within about 9% of a step
    /// change in two samples, about 170 ms: the bars ease instead of jumping,
    /// and every movement still comes from the voice.
    /// </summary>
    public static float Eased(float current, float target) => current + (target - current) * 0.7f;
}

/// <summary>
/// What the level bars draw for one smoothed peak: three bars, or, at a
/// silent level, one flat line as wide as the bars. Three bars at their floor
/// are three square dots, the universal "loading" glyph, which made a quiet
/// room read as busy. The macOS <c>DictationHUDWaveformShape</c>.
/// </summary>
public readonly struct DictationHudWaveform
{
    /// <summary>Each bar's width, in DIPs.</summary>
    public const double BarWidth = 3;

    /// <summary>The gap between two bars, in DIPs.</summary>
    public const double BarSpacing = 3;

    /// <summary>The silent line's height, in DIPs: under a floor bar's, so
    /// it never reads as dots again.</summary>
    public const double FlatLineHeight = 2;

    /// <summary>The height of the box the bars stand in, in DIPs: the tallest
    /// bar plus a pixel, the Mac's 14 pt frame. A fixed box, so a level never
    /// changes the indicator's size.</summary>
    public const double FrameHeight = 14;

    /// <summary>The bars' full width, which the flat line also spans.</summary>
    public const double Width = BarWidth * DictationHudLevel.BarCount + BarSpacing * (DictationHudLevel.BarCount - 1);

    /// <summary>
    /// How long the bars glide from one sample to the next: the Mac's one
    /// sample, 85 ms, so each glide ends about as the next sample lands (every
    /// 90 ms on Windows, <see cref="DictationHudLevel.SampleInterval"/>)
    /// instead of the bars jumping at every sample. With the system's
    /// animation effects off they snap (<see cref="DictationHudLevelStep"/>).
    /// </summary>
    public static readonly TimeSpan Glide = TimeSpan.FromMilliseconds(85);

    public DictationHudWaveform(float peak)
    {
        var level = DictationHudLevel.Normalized(peak);
        IsFlatLine = level == 0;
        BarHeights = DictationHudLevel.BarHeights(level);
    }

    /// <summary>Silence: draw the flat line instead of the bars.</summary>
    public bool IsFlatLine { get; }

    /// <summary>The three bar heights, left to right.</summary>
    public IReadOnlyList<double> BarHeights { get; }
}

/// <summary>
/// The level the bars draw next, and how long they take to get there.
/// </summary>
/// <param name="Level">The smoothed peak to draw (through
/// <see cref="DictationHudWaveform"/>).</param>
/// <param name="Glide">How long the bars move to it; zero snaps.</param>
public readonly record struct DictationHudLevelStep(float Level, TimeSpan Glide)
{
    /// <summary>
    /// The step from what is drawn, <paramref name="shown"/>, to the sample
    /// that just arrived, <paramref name="target"/>.
    /// </summary>
    /// <param name="shown">The level the bars show, or null when they have
    /// just replaced the mark: the first sample is drawn as it is, as the
    /// Mac's waveform starts from the level it appears with.</param>
    /// <param name="target">The sample, a smoothed peak.</param>
    /// <param name="reduceMotion">The system's animation effects are off
    /// (<see cref="IsReduced"/>): snap, no easing and no glide. The bars stop
    /// gliding; they do not stop responding.</param>
    public static DictationHudLevelStep Next(float? shown, float target, bool reduceMotion) =>
        shown is not { } current || reduceMotion
            ? new DictationHudLevelStep(target, TimeSpan.Zero)
            : new DictationHudLevelStep(DictationHudLevel.Eased(current, target), DictationHudWaveform.Glide);

    /// <summary>
    /// Whether motion is reduced. Windows has one source, Settings →
    /// Accessibility → Visual effects → Animation effects, which WPF reads as
    /// <c>SystemParameters.ClientAreaAnimation</c>
    /// (<c>SPI_GETCLIENTAREAANIMATION</c>); the Mac also has SwiftUI's
    /// environment value.
    /// </summary>
    public static bool IsReduced(bool clientAreaAnimation) => !clientAreaAnimation;
}

/// <summary>
/// The bars as the window draws them, from one sample to the next: the level
/// they show, and for each new sample where they go and how long they take.
/// The window applies each <see cref="DictationHudBarsFrame"/> and keeps no
/// level of its own.
/// </summary>
/// <remarks>
/// Here, not in the window, because this state carries the Mac's motion: a
/// window that forgot the level it showed would treat every sample as the
/// first after the dot and snap, and the ease and the glide would be gone
/// with nothing on a machine without WPF to notice.
/// </remarks>
public sealed class DictationHudBars
{
    /// <summary>The level the bars show, or null while the dot is in the
    /// slot.</summary>
    public float? Shown { get; private set; }

    /// <summary>The bars' move to a new sample (<paramref name="level"/>, a
    /// smoothed peak), eased from <see cref="Shown"/> and glided, or snapped
    /// for the first sample after the dot and whenever
    /// <paramref name="reduceMotion"/> (<see cref="DictationHudLevelStep.Next"/>).</summary>
    public DictationHudBarsFrame Step(float level, bool reduceMotion)
    {
        var step = DictationHudLevelStep.Next(Shown, level, reduceMotion);
        Shown = step.Level;
        return new DictationHudBarsFrame(new DictationHudWaveform(step.Level), step.Glide);
    }

    /// <summary>The dot is back in the slot: the next sample is drawn as it
    /// is, not eased in from this hold's last level.</summary>
    public void Reset() => Shown = null;
}

/// <summary>One sample's drawing: what the window sets, and over how long.</summary>
/// <param name="Shape">The bars or the flat line, at the level drawn.</param>
/// <param name="Glide">How long the move takes; zero snaps.</param>
public readonly record struct DictationHudBarsFrame(DictationHudWaveform Shape, TimeSpan Glide)
{
    /// <summary>The flat line's opacity: whole in silence, gone otherwise.</summary>
    public double FlatLineOpacity => Shape.IsFlatLine ? 1 : 0;

    /// <summary>The bars' opacity, the line's opposite: the two cross over
    /// the same glide the heights take.</summary>
    public double BarsOpacity => 1 - FlatLineOpacity;

    /// <summary>The three bar heights, left to right.</summary>
    public IReadOnlyList<double> BarHeights => Shape.BarHeights;
}

/// <summary>
/// The microphone's level while a hold listens: the loudest sample since the
/// last sample period, held without a lock, and smoothed once a period on the
/// controller's thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the recogniser hears.</b> The Mac's level is the peak of the
/// 16 kHz mono stream its recogniser is fed, and the decibel window was set on
/// that signal. The Windows tap averages each frame's channels before the
/// recogniser gets them (<see cref="WorkflowAudio.ToMono"/>), so the meter
/// reads the same average: a headset with one dead channel reads 6 dB down
/// here, as it does to the recogniser. The peak is taken at the device's
/// rate, before the resample to 16 kHz.
/// </para>
/// <para>
/// <b>Two threads, no lock.</b> <see cref="Offer(object?, AudioChunk)"/> runs
/// on the capture thread, which also feeds the recogniser: a stall there is a
/// gap in a sentence and an exception is a dead capture, so it only scans the
/// buffer and raises a shared maximum. <see cref="Sample"/> runs on the
/// controller's thread and takes the maximum and resets it in one exchange.
/// The maximum is raised with a compare-and-swap, which keeps the loudest
/// even if a device raised buffers from more than one thread. In the app the
/// capture thread is the only writer, and against one writer and the
/// exchange a plain read-then-write gives the same result, so no test here
/// can tell the two apart: the compare-and-swap is a precaution, not a
/// checked guard.
/// </para>
/// <para>
/// <b>Reset on read.</b> If the maximum survived a sample, one loud syllable
/// would pin the bars for the rest of the sentence. Read and reset, a sample
/// with no buffers in it is silence, so a microphone that stops delivering
/// mid-hold falls to the flat line instead of freezing at its last level (the
/// Mac only updates when a chunk arrives).
/// </para>
/// <para>
/// <b>One per hold.</b> The controller makes a new meter each time a hold
/// starts listening and drops it when the hold ends, so a buffer still in
/// flight from an earlier hold lands in a meter nobody reads.
/// </para>
/// </remarks>
public sealed class DictationLevelMeter
{
    /// <summary>The loudest sample since the last period, as float bits. For
    /// non-negative floats the bit patterns order the same way as the
    /// values, so the maximum can be kept with integer compare-and-swap.</summary>
    private int _peakBits;

    private TimeSpan _sampledAt;

    /// <param name="now">The controller's clock when the hold started
    /// listening; the first sample is one period later.</param>
    public DictationLevelMeter(TimeSpan now) => _sampledAt = now;

    /// <summary>The smoothed level after the last sample (0 before the
    /// first). Controller thread only.</summary>
    public float Level { get; private set; }

    /// <summary>The capture device's handler: offer one buffer, downmixed as
    /// the tap downmixes it for the recogniser. Capture thread; never throws.</summary>
    public void Offer(object? sender, AudioChunk chunk)
    {
        if (chunk?.Samples is not { } samples)
        {
            return;
        }

        // A buffer that claims no channels downmixes to nothing, as the tap
        // drops it: it is not audio.
        Offer(chunk.Channels == 1 ? samples : WorkflowAudio.ToMono(samples, chunk.Channels));
    }

    /// <summary>Offer the loudest of <paramref name="samples"/>, mono. A
    /// sample that is not a finite number is skipped: one NaN or infinity
    /// from a driver would otherwise hold the bars at the top for the rest of
    /// the hold.</summary>
    public void Offer(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = MathF.Abs(sample);
            if (magnitude > peak && float.IsFinite(magnitude))
            {
                peak = magnitude;
            }
        }

        if (peak <= 0)
        {
            return;
        }

        var bits = BitConverter.SingleToInt32Bits(peak);
        var seen = Volatile.Read(ref _peakBits);
        while (bits > seen)
        {
            var was = Interlocked.CompareExchange(ref _peakBits, bits, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    /// <summary>
    /// Take a sample when a period has passed since the last one: the loudest
    /// sample offered since then, smoothed (<see cref="DictationHudLevel.Smoothed"/>).
    /// </summary>
    /// <returns>The new smoothed level, or null when no sample is due yet.</returns>
    public float? Sample(TimeSpan now)
    {
        if (now - _sampledAt < DictationHudLevel.SampleInterval)
        {
            return null;
        }

        // From now, not from the last due time: after a stall (a busy
        // dispatcher, a sleep) the bars take one sample, not a burst of
        // catch-up samples.
        _sampledAt = now;
        var peak = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));
        Level = DictationHudLevel.Smoothed(Level, peak);
        return Level;
    }
}
