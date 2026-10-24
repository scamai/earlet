using System.Xml.Linq;
using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

// ─── The level bars on the floating indicator ───────────────────────────────
//
// Ported one for one from the macOS suites, under the Mac's scenario names:
// macos/Tests/DictationAppCoreTests/DictationHUDTests.swift
//   "The HUD waveform tracks the live input level" (DictationHUDLevelTests) and
//   "The model's input level lives only for the hold" (InputLevelTests), and
// macos/Tests/DictationAppCoreTests/DictationHUDPanelTests.swift
//   "Silence is a flat line, not a row of dots; speech is the bars" and the
//   Reduce Motion test. "the frozen title reference is the full caption
//   budget" belongs to the live preview, not to the bars, and is not here.
// The Windows-only scenarios follow each port.

/// <summary>The level → bar mapping: the dB window, the bar heights, the
/// flat line, smoothing, easing and Reduce Motion.</summary>
public sealed class DictationHudLevelTests
{
    // ─── DictationHUDLevelTests ─────────────────────────────────────────

    /// <summary>"silence is a flat floor — no pulse, no breathing, no randomness"</summary>
    [Fact]
    public void Silence_is_a_flat_floor_no_pulse_no_breathing_no_randomness()
    {
        var heights = DictationHudLevel.BarHeights(0);
        Assert.Equal([DictationHudLevel.MinHeight, DictationHudLevel.MinHeight, DictationHudLevel.MinHeight], heights);
        // A function of the level alone: again, at any "phase", the same still shape.
        Assert.Equal(heights, DictationHudLevel.BarHeights(0));
    }

    /// <summary>"level 0.5 lands the tall bar near the middle of the range"</summary>
    [Fact]
    public void Level_half_lands_the_tall_bar_near_the_middle_of_the_range()
    {
        var heights = DictationHudLevel.BarHeights(0.5f);
        var mid = DictationHudLevel.MinHeight + (DictationHudLevel.MaxHeight - DictationHudLevel.MinHeight) / 2;
        Assert.Equal(mid, heights[0]);
        Assert.True(heights[1] < mid && heights[2] < mid, "a fixed wave silhouette, not one column");
        Assert.All(heights, h => Assert.True(h >= DictationHudLevel.MinHeight));
    }

    /// <summary>"level 1 tops the tall bar at the maximum"</summary>
    [Fact]
    public void Level_one_tops_the_tall_bar_at_the_maximum()
    {
        var heights = DictationHudLevel.BarHeights(1);
        Assert.Equal(DictationHudLevel.MaxHeight, heights[0]);
        Assert.All(heights, h => Assert.True(h > DictationHudLevel.MinHeight));
        // Out-of-range input is clamped, never taller or inverted.
        Assert.Equal(DictationHudLevel.MaxHeight, DictationHudLevel.BarHeights(2)[0]);
    }

    /// <summary>Windows: below the range, and NaN, is the floor, never a bar
    /// shorter than it.</summary>
    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void A_level_under_the_range_or_not_a_number_is_the_floor(float level) =>
        Assert.All(DictationHudLevel.BarHeights(level), h => Assert.Equal(DictationHudLevel.MinHeight, h));

    /// <summary>"the per-bar easing converges toward the target and never overshoots"</summary>
    [Fact]
    public void The_per_bar_easing_converges_toward_the_target_and_never_overshoots()
    {
        var level = 0f;
        var previous = 0f;
        for (var i = 0; i < 10; i++)
        {
            level = DictationHudLevel.Eased(level, 1);
            Assert.True(level > previous, "every step approaches the target");
            Assert.True(level <= 1, "and never overshoots it");
            previous = level;
        }

        Assert.True(level > 0.99f, "ten samples at ~12 Hz is under a second to settle");
        // Two samples ≈ 170 ms covers ~90% of a step.
        var two = DictationHudLevel.Eased(DictationHudLevel.Eased(0, 1), 1);
        Assert.True(two > 0.85f);
    }

    /// <summary>"silence and room tone map to the floor of the dB window"</summary>
    [Fact]
    public void Silence_and_room_tone_map_to_the_floor_of_the_dB_window()
    {
        Assert.Equal(0, DictationHudLevel.Normalized(0));
        Assert.Equal(0, DictationHudLevel.Normalized(0.001f)); // -60 dB is under the floor
        Assert.Equal(0, DictationHudLevel.Normalized(0.005f)); // -46 dB is still room tone
    }

    /// <summary>Windows: a negative or NaN peak has no logarithm; it is the
    /// floor, not NaN carried into the bars.</summary>
    [Theory]
    [InlineData(-0.5f)]
    [InlineData(float.NaN)]
    public void A_peak_that_is_not_a_positive_number_maps_to_the_floor(float peak) =>
        Assert.Equal(0, DictationHudLevel.Normalized(peak));

    /// <summary>"ordinary speech peaks land visibly above half the window"</summary>
    [Theory]
    [InlineData(0.05f)]
    [InlineData(0.08f)]
    [InlineData(0.1f)]
    public void Ordinary_speech_peaks_land_visibly_above_half_the_window(float peak)
    {
        var level = DictationHudLevel.Normalized(peak);
        Assert.True(level > 0.5f, $"peak {peak} should read well above half, got {level}");
        Assert.True(level < 0.75f, $"and leave headroom for a shout, got {level}");
    }

    /// <summary>"a shout reaches the ceiling and clipping clamps to it"</summary>
    [Fact]
    public void A_shout_reaches_the_ceiling_and_clipping_clamps_to_it()
    {
        Assert.Equal(1, DictationHudLevel.Normalized(0.5f));
        Assert.Equal(1, DictationHudLevel.Normalized(1));
        Assert.Equal(1, DictationHudLevel.Normalized(4));
    }

    // ─── The model's smoothing (DictationModel.noteInputLevel) ──────────

    /// <summary>The smoothing half of "a chunk peak raises the level with a
    /// fast attack": a louder peak is taken at once, a quieter one blends in.</summary>
    [Fact]
    public void A_louder_peak_is_taken_at_once_and_a_quieter_one_blends_in()
    {
        Assert.Equal(0.8f, DictationHudLevel.Smoothed(0.1f, 0.8f));
        Assert.Equal(0.8f * 0.55f + 0.1f * 0.45f, DictationHudLevel.Smoothed(0.8f, 0.1f));
        Assert.Equal(0, DictationHudLevel.Smoothed(0, 0));
    }

    // ─── DictationHUDWaveformShapeTests ─────────────────────────────────

    /// <summary>"digital silence and room tone under the floor draw one flat line"</summary>
    [Fact]
    public void Digital_silence_and_room_tone_under_the_floor_draw_one_flat_line()
    {
        Assert.True(new DictationHudWaveform(0).IsFlatLine);
        Assert.True(new DictationHudWaveform(0.001f).IsFlatLine); // -60 dB, well under -45
    }

    /// <summary>"ordinary speech draws the bars, raised off the floor"</summary>
    [Fact]
    public void Ordinary_speech_draws_the_bars_raised_off_the_floor()
    {
        var shape = new DictationHudWaveform(0.08f);
        Assert.False(shape.IsFlatLine);
        Assert.All(shape.BarHeights, h => Assert.True(h > DictationHudLevel.MinHeight));
        Assert.Equal(DictationHudLevel.BarCount, shape.BarHeights.Count);
    }

    /// <summary>"the flat line spans the bars, and the glide is one level sample"</summary>
    [Fact]
    public void The_flat_line_spans_the_bars_and_the_glide_is_one_level_sample()
    {
        const double count = DictationHudLevel.BarCount;
        Assert.Equal(DictationHudWaveform.BarWidth * count + DictationHudWaveform.BarSpacing * (count - 1), DictationHudWaveform.Width);
        Assert.True(DictationHudWaveform.FlatLineHeight < DictationHudLevel.MinHeight,
            "a line as tall as a floor bar would read as dots again");
        Assert.True(DictationHudWaveform.Glide > TimeSpan.Zero && DictationHudWaveform.Glide <= TimeSpan.FromMilliseconds(100));
        // The Mac's one sample. On Windows a sample lands every third 30 ms
        // tick, so the glide ends just before the next one arrives.
        Assert.Equal(TimeSpan.FromMilliseconds(85), DictationHudWaveform.Glide);
        Assert.True(DictationHudWaveform.Glide <= 3 * DictationLevelMeterTests.Tick);
    }

    // ─── Reduce Motion (DictationHUDPanelTests) ─────────────────────────

    /// <summary>"Reduce Motion, from either source, drops the rise and the
    /// frame's ease". Windows has one source, the system's animation effects,
    /// and no rise or frame ease to drop: Reduce Motion drops the bars' ease
    /// and glide, and they still follow the level.</summary>
    [Fact]
    public void Reduce_Motion_from_the_systems_animation_setting_drops_the_ease_and_the_glide()
    {
        var still = DictationHudLevelStep.Next(0.2f, 0.8f, reduceMotion: true);
        Assert.Equal(new DictationHudLevelStep(0.8f, TimeSpan.Zero), still);

        var moving = DictationHudLevelStep.Next(0.2f, 0.8f, reduceMotion: false);
        Assert.Equal(DictationHudLevel.Eased(0.2f, 0.8f), moving.Level);
        Assert.Equal(DictationHudWaveform.Glide, moving.Glide);

        Assert.True(DictationHudLevelStep.IsReduced(clientAreaAnimation: false));
        Assert.False(DictationHudLevelStep.IsReduced(clientAreaAnimation: true));
    }

    /// <summary>Windows, the Mac waveform's initial state: the first level
    /// after the bars replace the dot is drawn as it is, so a listening pill
    /// never eases in from the last hold's level.</summary>
    [Fact]
    public void The_first_level_after_the_dot_is_drawn_as_it_is()
    {
        Assert.Equal(new DictationHudLevelStep(0.3f, TimeSpan.Zero), DictationHudLevelStep.Next(null, 0.3f, reduceMotion: false));
        Assert.Equal(new DictationHudLevelStep(0f, TimeSpan.Zero), DictationHudLevelStep.Next(null, 0f, reduceMotion: false));
    }
}

/// <summary>The bars from one sample to the next, the state the window
/// only applies: what they show, the ease and glide, the flat line, and the
/// reset when the dot comes back.</summary>
public sealed class DictationHudBarsTests
{
    [Fact]
    public void The_first_level_after_the_dot_snaps_and_the_next_ones_ease_and_glide_from_what_is_shown()
    {
        var bars = new DictationHudBars();
        Assert.Null(bars.Shown);

        var first = bars.Step(0.5f, reduceMotion: false);
        Assert.Equal(TimeSpan.Zero, first.Glide);
        Assert.Equal(0.5f, bars.Shown);
        Assert.Equal(new DictationHudWaveform(0.5f).BarHeights, first.BarHeights);

        var second = bars.Step(0.1f, reduceMotion: false);
        var eased = DictationHudLevel.Eased(0.5f, 0.1f);
        Assert.Equal(DictationHudWaveform.Glide, second.Glide);
        Assert.Equal(eased, bars.Shown);
        Assert.Equal(new DictationHudWaveform(eased).BarHeights, second.BarHeights);

        // The third eases from what the second drew, not from the second
        // sample: 70% of the way again, never landing on 0.1 at once.
        var third = bars.Step(0.1f, reduceMotion: false);
        Assert.Equal(DictationHudLevel.Eased(eased, 0.1f), bars.Shown);
        Assert.Equal(DictationHudWaveform.Glide, third.Glide);
        Assert.True(bars.Shown > 0.1f);
    }

    [Fact]
    public void Silence_shows_the_flat_line_and_speech_the_bars()
    {
        var bars = new DictationHudBars();

        var silent = bars.Step(0, reduceMotion: false);
        Assert.True(silent.Shape.IsFlatLine);
        Assert.Equal((1d, 0d), (silent.FlatLineOpacity, silent.BarsOpacity));

        var speech = bars.Step(0.08f, reduceMotion: false);
        Assert.False(speech.Shape.IsFlatLine);
        Assert.Equal((0d, 1d), (speech.FlatLineOpacity, speech.BarsOpacity));
        Assert.All(speech.BarHeights, h => Assert.True(h > DictationHudLevel.MinHeight));
    }

    /// <summary>Animation effects off: every sample snaps, and the bars still
    /// follow the level, so turning effects back on mid-hold eases from where
    /// the bars are.</summary>
    [Fact]
    public void With_animation_effects_off_every_sample_snaps_and_the_bars_still_follow_the_level()
    {
        var bars = new DictationHudBars();
        bars.Step(0.5f, reduceMotion: false);

        var snapped = bars.Step(0.1f, reduceMotion: true);
        Assert.Equal(TimeSpan.Zero, snapped.Glide);
        Assert.Equal(0.1f, bars.Shown);

        var resumed = bars.Step(0.6f, reduceMotion: false);
        Assert.Equal(DictationHudWaveform.Glide, resumed.Glide);
        Assert.Equal(DictationHudLevel.Eased(0.1f, 0.6f), bars.Shown);
    }

    [Fact]
    public void After_the_dot_comes_back_the_next_level_snaps_again()
    {
        var bars = new DictationHudBars();
        bars.Step(0.8f, reduceMotion: false);
        bars.Step(0.4f, reduceMotion: false);

        bars.Reset();
        Assert.Null(bars.Shown);

        var first = bars.Step(0.05f, reduceMotion: false);
        Assert.Equal(TimeSpan.Zero, first.Glide);
        Assert.Equal(0.05f, bars.Shown);
    }
}

/// <summary>The peak-hold between the capture thread and the controller's
/// tick, and how often it is sampled.</summary>
public sealed class DictationLevelMeterTests
{
    private static readonly TimeSpan Start = TimeSpan.FromHours(1);
    private static readonly TimeSpan Period = DictationHudLevel.SampleInterval;

    /// <summary>The app's poll while dictation is on, which ticks the
    /// controller (<c>MainWindow.poll</c>; pinned below).</summary>
    internal static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(30);

    [Fact]
    public void A_sample_is_the_loudest_buffer_since_the_last_one_not_the_last_buffer()
    {
        var meter = new DictationLevelMeter(Start);
        meter.Offer([0.1f]);
        meter.Offer([0.6f, -0.2f]);
        meter.Offer([0.2f]);

        Assert.Equal(0.6f, meter.Sample(Start + Period));
    }

    /// <summary>Reset on read: a sample with no buffers in it is silence, so
    /// a microphone that stops delivering mid-hold falls to the flat line
    /// instead of pinning the bars at its last level.</summary>
    [Fact]
    public void A_sample_with_no_buffers_in_it_is_silence_so_the_bars_fall_to_the_flat_line()
    {
        var meter = new DictationLevelMeter(Start);
        meter.Offer([0.8f]);
        var now = Start + Period;
        Assert.Equal(0.8f, meter.Sample(now));

        var previous = 0.8f;
        for (var i = 0; i < 20; i++)
        {
            now += Period;
            var level = meter.Sample(now)!.Value;
            Assert.True(level < previous, "every empty sample is quieter");
            previous = level;
        }

        Assert.True(new DictationHudWaveform(previous).IsFlatLine, $"still {previous} after twenty empty samples");
    }

    [Fact]
    public void No_sample_is_due_until_a_period_has_passed_since_the_last()
    {
        var meter = new DictationLevelMeter(Start);
        meter.Offer([0.5f]);

        Assert.Null(meter.Sample(Start + Period - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(0.5f, meter.Sample(Start + Period));
        Assert.Null(meter.Sample(Start + Period + TimeSpan.FromMilliseconds(30)));
        Assert.NotNull(meter.Sample(Start + Period + Period));
    }

    /// <summary>
    /// Windows' clock is coarse. The controller reads
    /// <c>Environment.TickCount64</c>, which moves in steps of 15.625 ms, and
    /// the poll fires every 30 ms when some app has raised the timer
    /// resolution, or every 31.25 ms (two steps) when none has. Either way a
    /// sample lands on every third tick, never a fourth, so the bars move at
    /// an even rate, whatever the phase between the poll and the clock.
    /// </summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(31.25)]
    public void On_Windows_coarse_tick_count_a_sample_lands_on_every_third_tick(double tickMs)
    {
        const double step = 1000.0 / 64;
        for (var phase = 0.0; phase < step; phase += 0.25)
        {
            // What TickCount64 reads at the poll's nth tick: whole
            // milliseconds of whole steps.
            TimeSpan Read(int tick) => TimeSpan.FromMilliseconds((long)(Math.Floor((phase + tick * tickMs) / step) * step));

            var meter = new DictationLevelMeter(Read(0));
            var last = 0;
            for (var tick = 1; tick <= 300; tick++)
            {
                if (meter.Sample(Read(tick)) is not null)
                {
                    Assert.True(tick - last == 3, $"a sample {tick - last} ticks after the last (poll {tickMs} ms, phase {phase} ms)");
                    last = tick;
                }
            }

            Assert.Equal(300, last);
        }
    }

    /// <summary>The arithmetic above assumes the app's poll: if it changes,
    /// <see cref="DictationHudLevel.SampleInterval"/> must be worked out again.
    /// The poll slows only while dictation is off, when there are no bars.</summary>
    [Fact]
    public void The_app_polls_the_controller_every_30_ms()
    {
        var code = File.ReadAllText(DictationHudGeometryTests.AppFile("MainWindow.xaml.cs"));
        Assert.Contains("poll = new() { Interval = DictationController.BusyPoll }", code, StringComparison.Ordinal);
        Assert.Equal(Tick, DictationController.BusyPoll);
    }

    /// <summary>After a stall (a busy dispatcher, a sleep) one sample, not a
    /// burst of catch-up samples.</summary>
    [Fact]
    public void A_stall_is_followed_by_one_sample_not_a_burst()
    {
        var meter = new DictationLevelMeter(Start);
        var late = Start + TimeSpan.FromSeconds(2);

        Assert.NotNull(meter.Sample(late));
        Assert.Null(meter.Sample(late + TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public void A_sample_that_is_not_a_finite_number_is_skipped()
    {
        var meter = new DictationLevelMeter(Start);
        meter.Offer([float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.3f]);
        Assert.Equal(0.3f, meter.Sample(Start + Period));

        meter.Offer([float.NaN, float.PositiveInfinity]);
        var after = meter.Sample(Start + Period + Period)!.Value;
        Assert.True(float.IsFinite(after) && after < 0.3f, $"a buffer of nothing finite is silence, got {after}");
    }

    /// <summary>What the recogniser hears, as on the Mac: the peak of the
    /// tap's downmix (each frame's channels averaged), not of the hottest
    /// channel. A headset whose second channel is dead reads 6 dB down, as
    /// it does to the recogniser.</summary>
    [Fact]
    public void A_buffer_is_read_after_the_taps_downmix_as_the_recogniser_hears_it()
    {
        var meter = new DictationLevelMeter(Start);
        // The hottest sample is -0.7; the frames average to 0.4, -0.35 and 0.5.
        float[] stereo = [0.2f, 0.6f, 0f, -0.7f, 0.5f, 0.5f];
        meter.Offer(null, new AudioChunk(stereo, 48_000, 2));

        Assert.Equal(0.5f, meter.Sample(Start + Period));
        Assert.Equal(0.5f, WorkflowAudio.Level(WorkflowAudio.ToMono(stereo, 2)).Peak);

        var dead = new DictationLevelMeter(Start);
        dead.Offer(null, new AudioChunk([0.8f, 0f, -0.4f, 0f], 48_000, 2));
        Assert.Equal(0.4f, dead.Sample(Start + Period));
    }

    /// <summary>A buffer that says it has no channels is not audio; the tap
    /// drops it too.</summary>
    [Fact]
    public void A_buffer_with_no_channels_is_ignored()
    {
        var meter = new DictationLevelMeter(Start);
        meter.Offer(null, new AudioChunk([0.9f], 16_000, 0));
        Assert.Equal(0, meter.Sample(Start + Period));
    }

    [Fact]
    public void A_missing_buffer_is_ignored()
    {
        var meter = new DictationLevelMeter(Start);
        meter.Offer(null, null!);
        meter.Offer(null, new AudioChunk([], 16_000, 1));
        Assert.Equal(0, meter.Sample(Start + Period));
    }

    /// <summary>
    /// Several threads offering at once, for real: nothing throws, and the
    /// loudest is kept on this run. A smoke test of the lock-free path, not a
    /// guard: replacing the compare-and-swap with a plain read-then-write
    /// keeps it green, because the race it would lose is too narrow to hit
    /// reliably. The app has one writer, the capture thread, and against one
    /// writer the two give the same result (<see cref="DictationLevelMeter"/>).
    /// </summary>
    [Fact]
    public void Offers_from_many_threads_at_once_run_without_a_lock()
    {
        var meter = new DictationLevelMeter(Start);
        Parallel.For(1, 20_001, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => meter.Offer([i / 20_000f]));

        Assert.Equal(1f, meter.Sample(Start + Period));
    }
}

/// <summary>The bars through the controller the app runs: they live only
/// while a hold listens.</summary>
public sealed class DictationInputLevelTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    /// <summary>Enabled, the key held, and the listening pill up.</summary>
    private static async Task<Rig> Listening(Rig rig)
    {
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current?.Text);
        return rig;
    }

    /// <summary>One sample period on the controller's clock, then a tick.</summary>
    private static void NextSample(Rig rig)
    {
        rig.Clock.Advance(DictationHudLevel.SampleInterval);
        rig.Controller.Tick();
    }

    // ─── InputLevelTests ────────────────────────────────────────────────

    /// <summary>"a chunk peak raises the level with a fast attack"</summary>
    [Fact]
    public async Task A_chunk_peak_raises_the_level_with_a_fast_attack()
    {
        using var rig = await Listening(new Rig());

        rig.Platform.Microphone.Buffer(0.8f);
        NextSample(rig);
        Assert.True(rig.Hud.Levels[^1] > 0.7f, $"got {rig.Hud.Levels[^1]}");

        // Quiet buffers decay it rather than strobing it off.
        rig.Platform.Microphone.Buffer(0.1f);
        NextSample(rig);
        Assert.InRange(rig.Hud.Levels[^1], 0.0001f, 0.6999f);

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    /// <summary>"releasing the hold resets the level to zero", and the
    /// capture stops reporting too.</summary>
    [Fact]
    public async Task Releasing_the_hold_resets_the_level_to_zero()
    {
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        await rig.EnableAsync();
        var microphone = rig.Platform.Microphone;
        var armed = microphone.Listeners;
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(armed + 2, microphone.Listeners); // the meter and the silence tracker

        microphone.Buffer(0.9f);
        NextSample(rig);
        Assert.True(rig.Hud.Levels[^1] > 0.8f);

        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.Equal(0, rig.Hud.Levels[^1]);
        Assert.Equal(armed, microphone.Listeners);

        // Nothing more reaches the bars, however loud.
        var drawn = rig.Hud.Levels.Count;
        microphone.Buffer(1);
        NextSample(rig);
        Assert.Equal(drawn, rig.Hud.Levels.Count);

        decode.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    /// <summary>"cancelling the hold resets the level to zero"</summary>
    [Fact]
    public async Task Cancelling_the_hold_resets_the_level_to_zero()
    {
        using var rig = await Listening(new Rig());
        var microphone = rig.Platform.Microphone;
        microphone.Buffer(0.9f);
        NextSample(rig);
        Assert.True(rig.Hud.Levels[^1] > 0.8f);

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        rig.DrainUntil(() => !rig.View.Last.CanCancel);

        Assert.Equal(0, rig.Hud.Levels[^1]);
        Assert.Equal(FakeMicrophone.ArmedListeners, microphone.Listeners); // the tap's and the spool's, nothing of the meter's
    }

    /// <summary>"a level arriving outside a hold is ignored": nothing listens
    /// to the microphone's level while armed, and a loud room reaches no bars.</summary>
    [Fact]
    public async Task A_level_arriving_outside_a_hold_is_ignored()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        Assert.Equal(FakeMicrophone.ArmedListeners, rig.Platform.Microphone.Listeners); // the tap's and the spool's

        rig.Platform.Microphone.Buffer(1);
        NextSample(rig);
        rig.Dispatcher.Drain();

        Assert.Empty(rig.Hud.Levels);
        Assert.Null(rig.Hud.Current);
    }

    // ─── Windows ────────────────────────────────────────────────────────

    /// <summary>A pause is not a session event the controller hears (the
    /// session is detached first), so it lets go of the level itself, and
    /// the next hold on the new arm reads levels again. The internal Pause,
    /// called directly: the window and the menu refuse a pause under a
    /// dictation (TryPause); quitting and a failure tear a hold down the
    /// same way.</summary>
    [Fact]
    public async Task The_pause_teardown_mid_hold_lets_go_of_the_microphone_and_the_next_hold_meters_again()
    {
        using var rig = await Listening(new Rig());
        var first = rig.Platform.Microphone;
        first.Buffer(0.9f);
        NextSample(rig);

        rig.Controller.Pause();
        rig.Dispatcher.Drain();
        Assert.Equal(0, first.Listeners);
        Assert.Null(rig.Hud.Current);

        rig.Platform.Trigger.Down = false;
        await rig.EnableAsync();
        Assert.NotSame(first, rig.Platform.Microphone);
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Platform.Microphone.Buffer(0.4f);
        NextSample(rig);
        Assert.Equal(0.4f, rig.Hud.Levels[^1]);
    }

    /// <summary>Escape lets go of the microphone and zeroes the level in the
    /// same tick, before the cancel's posted state change reaches the window.</summary>
    [Fact]
    public async Task Escape_lets_go_of_the_level_in_the_same_tick()
    {
        using var rig = await Listening(new Rig());
        rig.Platform.Microphone.Buffer(0.9f);
        NextSample(rig);

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();

        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current?.Text);
        Assert.Equal(0, rig.Hud.Levels[^1]);
        Assert.Equal(FakeMicrophone.ArmedListeners, rig.Platform.Microphone.Listeners);
        rig.Dispatcher.Drain();
    }

    /// <summary>The window's Cancel button ends the hold as Escape does, and
    /// lets go of the level with it.</summary>
    [Fact]
    public async Task The_windows_Cancel_button_lets_go_of_the_level()
    {
        using var rig = await Listening(new Rig());
        var microphone = rig.Platform.Microphone;
        microphone.Buffer(0.9f);
        NextSample(rig);
        // The arm's own, the meter's and the silence tracker's.
        Assert.Equal(FakeMicrophone.ArmedListeners + 2, microphone.Listeners);

        rig.Controller.Cancel();
        rig.Dispatcher.Drain();

        Assert.Equal("Cancelled. Nothing was inserted.", rig.View.Last.Status);
        Assert.Equal(0, rig.Hud.Levels[^1]);
        Assert.Equal(FakeMicrophone.ArmedListeners, microphone.Listeners); // the tap's and the spool's
    }

    /// <summary>The recording limit ends a hold as a release does: the key
    /// is still down, and nothing reads the microphone's level any more.</summary>
    [Fact]
    public async Task The_recording_limit_lets_go_of_the_level_with_the_key_still_down()
    {
        using var rig = await Listening(new Rig());
        var microphone = rig.Platform.Microphone;
        microphone.Buffer(0.9f);
        NextSample(rig);
        Assert.True(rig.Hud.Levels[^1] > 0.8f);

        rig.Clock.Advance(DictationHoldPolicy.Windows.Ceiling);
        rig.Controller.Tick();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.True(rig.Platform.Trigger.Down);
        Assert.Equal(0, rig.Hud.Levels[^1]);
        Assert.Equal(FakeMicrophone.ArmedListeners, microphone.Listeners); // the tap's and the spool's
        var drawn = rig.Hud.Levels.Count;
        microphone.Buffer(1);
        NextSample(rig);
        Assert.Equal(drawn, rig.Hud.Levels.Count);
        rig.Release();
        rig.Dispatcher.Drain();
    }

    /// <summary>The app quitting mid-hold lets go of the microphone
    /// entirely: the meter's handler goes with the tap's.</summary>
    [Fact]
    public async Task Quitting_mid_hold_leaves_nothing_on_the_microphone()
    {
        using var rig = await Listening(new Rig());
        var microphone = rig.Platform.Microphone;
        microphone.Buffer(0.9f);
        NextSample(rig);
        // The arm's own, the meter's and the silence tracker's.
        Assert.Equal(FakeMicrophone.ArmedListeners + 2, microphone.Listeners);

        rig.Controller.Dispose();
        rig.Dispatcher.Drain();

        Assert.Equal(0, microphone.Listeners);
    }

    [Fact]
    public async Task A_hold_starts_from_the_flat_line_not_the_last_holds_level()
    {
        using var rig = await Listening(new Rig());
        rig.Platform.Microphone.Buffer(0.9f);
        NextSample(rig);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        var before = rig.Hud.Levels.Count;
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current?.Text);
        Assert.Equal([0f], rig.Hud.Levels.Skip(before));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    /// <summary>The bars are sampled about twelve times a second from the
    /// loudest of the buffers in between, not once per 10 ms buffer.</summary>
    [Fact]
    public async Task Levels_are_sampled_about_twelve_times_a_second_not_per_buffer()
    {
        using var rig = await Listening(new Rig());
        var microphone = rig.Platform.Microphone;
        var before = rig.Hud.Levels.Count;

        float[] peaks = [0.1f, 0.2f, 0.3f, 0.6f, 0.2f, 0.1f, 0.3f, 0.2f, 0.1f];
        for (var tick = 0; tick < 3; tick++)
        {
            foreach (var peak in peaks.Skip(tick * 3).Take(3))
            {
                microphone.Buffer(peak);
            }

            rig.Clock.Advance(TimeSpan.FromMilliseconds(30));
            rig.Controller.Tick();
            Assert.Equal(tick < 2 ? before : before + 1, rig.Hud.Levels.Count);
        }

        Assert.Equal(0.6f, rig.Hud.Levels[^1]);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    /// <summary>The Mac title stays "Listening" at every level; the shared
    /// model's "Hearing you" is not used.</summary>
    [Fact]
    public async Task The_title_stays_Listening_at_every_level()
    {
        using var rig = await Listening(new Rig());
        var shows = rig.Hud.Shows.Count;
        foreach (var peak in new[] { 0.001f, 0.08f, 1f })
        {
            rig.Platform.Microphone.Buffer(peak);
            NextSample(rig);
        }

        Assert.Equal(shows, rig.Hud.Shows.Count);
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current?.Text);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    /// <summary>A level is never a pill and never announced: Narrator says
    /// nothing while the microphone is open.</summary>
    [Fact]
    public async Task A_level_is_never_announced()
    {
        using var rig = await Listening(new Rig());
        var shows = rig.Hud.Shows.Count;
        rig.Platform.Microphone.Buffer(0.5f);
        NextSample(rig);

        Assert.True(rig.Hud.Levels.Count >= 2);
        Assert.Equal(shows, rig.Hud.Announced.Count);
        Assert.False(rig.Hud.Announced[^1]);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Fact]
    public async Task Without_an_indicator_nothing_reads_the_microphones_level()
    {
        using var rig = new Rig(hud: false);
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        // The arm's own (the tap's and the spool's) and the silence
        // tracker's, which stops holds with or without an indicator; no meter's.
        Assert.Equal(FakeMicrophone.ArmedListeners + 1, rig.Platform.Microphone.Listeners);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    /// <summary>A window that cannot draw a level is logged once for the
    /// pill, not twelve times a second, and the dictation is typed.</summary>
    [Fact]
    public async Task A_window_that_cannot_draw_a_level_is_tried_once_per_pill_and_dictation_carries_on()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Hud.Throws = true;
        rig.Press();
        rig.Dispatcher.Drain();
        for (var i = 0; i < 5; i++)
        {
            rig.Platform.Microphone.Buffer(0.5f);
            NextSample(rig);
        }

        Assert.Equal(1, rig.Hud.LevelAttempts);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    /// <summary>
    /// Copy last text's endings after a hold that drew the bars: "Your words
    /// are still here" (the paste and the clipboard both refused), then
    /// "Couldn't copy" and "Copied". Each puts the tone dot back in the slot,
    /// nothing more reaches the bars, and the next hold's bars start from the
    /// flat line.
    /// </summary>
    [Fact]
    public async Task Copy_last_texts_endings_show_the_dot_and_the_next_hold_meters_from_the_flat_line()
    {
        using var rig = await Listening(new Rig());
        rig.Platform.Clipboard.Accepts = false;
        rig.Platform.Paste.Succeeds = false;
        var microphone = rig.Platform.Microphone;
        microphone.Buffer(0.9f);
        NextSample(rig);
        Assert.True(rig.Hud.Bars);

        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.WordsStillHere);

        Assert.Equal(DictationCopy.HudWordsStillHere, rig.Hud.Current?.Text);
        Assert.False(rig.Hud.Bars);
        Assert.Equal(0, rig.Hud.Levels[^1]);
        Assert.Equal(FakeMicrophone.ArmedListeners, microphone.Listeners); // the tap's and the spool's
        var drawn = rig.Hud.Levels.Count;
        microphone.Buffer(1);
        NextSample(rig);

        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudCopyFailed, rig.Hud.Current?.Text);
        Assert.False(rig.Hud.Bars);

        rig.Platform.Clipboard.Accepts = true;
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudCopied, rig.Hud.Current?.Text);
        Assert.False(rig.Hud.Bars);
        Assert.Equal(drawn, rig.Hud.Levels.Count);

        rig.Platform.Paste.Succeeds = true;
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current?.Text);
        Assert.True(rig.Hud.Bars);
        Assert.Equal([0f], rig.Hud.Levels.Skip(drawn));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }
}

/// <summary>The feed's half: a level is drawn only on the listening pill.</summary>
public sealed class DictationHudFeedLevelTests
{
    private readonly FakeHud _hud = new();
    private readonly FakeClock _clock = new();

    private DictationHudFeed Feed() => new(_hud, () => _clock.Now, () => FakeHud.Hold);

    [Fact]
    public void The_listening_pill_draws_the_last_level_right_after_it_is_shown()
    {
        using var feed = Feed();
        feed.Level(0.4f);
        Assert.Empty(_hud.Levels);

        feed.State(DictationState.Listening);

        Assert.Equal(DictationCopy.HudHeld, _hud.Current?.Text);
        Assert.Equal([0.4f], _hud.Levels);
        feed.Level(0.6f);
        Assert.Equal([0.4f, 0.6f], _hud.Levels);
    }

    /// <summary>Any other pill, even one shown while the hold still listens,
    /// puts the mark back and gets no level; so does hiding.</summary>
    [Fact]
    public void A_level_on_any_other_pill_or_none_is_remembered_but_not_drawn()
    {
        using var feed = Feed();
        feed.Level(0.5f);
        Assert.Empty(_hud.Levels);

        feed.State(DictationState.Listening);
        feed.RecognizerCouldNotRestart();
        feed.Level(0.7f);
        Assert.Equal([0.5f], _hud.Levels);

        feed.State(DictationState.Transcribing);
        feed.Level(0.2f);
        Assert.Equal([0.5f], _hud.Levels);

        // Taken down while it listened (a pause): no bars on nothing.
        feed.State(DictationState.Listening);
        feed.Clear();
        feed.Level(0.3f);
        Assert.Equal([0.5f, 0.2f], _hud.Levels);
    }

    /// <summary>Copy last text's pills, and the press a copy refuses, are
    /// pills of their own: each puts the dot back, even straight after the
    /// listening pill drew the bars, and gets no level.</summary>
    [Theory]
    [InlineData(DictationCopy.HudWordsStillHere)]
    [InlineData(DictationCopy.HudCopied)]
    [InlineData(DictationCopy.HudCopyFailed)]
    [InlineData(DictationCopy.HudStillCopying)]
    public void Copy_last_texts_pills_put_the_dot_back_and_get_no_level(string pill)
    {
        using var feed = Feed();
        feed.State(DictationState.Listening);
        feed.Level(0.6f);
        Assert.True(_hud.Bars);
        var drawn = _hud.Levels.Count;

        switch (pill)
        {
            case DictationCopy.HudWordsStillHere:
                feed.Result(
                    new DictationSessionResult(DictationOutcome.InsertionRefused, null, null),
                    new DictationHudContext(WordsKeptInApp: true));
                break;
            case DictationCopy.HudCopied:
                feed.Copied(copied: true);
                break;
            case DictationCopy.HudCopyFailed:
                feed.Copied(copied: false);
                break;
            default:
                feed.PressRefused(DictationPressRefusal.StillCopying);
                break;
        }

        Assert.Equal(pill, _hud.Current?.Text);
        Assert.False(_hud.Bars);
        feed.Level(0.8f);
        Assert.False(_hud.Bars);
        Assert.Equal(drawn, _hud.Levels.Count);
    }
}

/// <summary>
/// The window's markup and wiring for the bars. Pins on the source, not
/// evidence that the window behaves: WPF cannot run here, and no one has seen
/// the bars on Windows.
/// </summary>
public sealed class DictationHudLevelWindowTests
{
    private static readonly XNamespace X = DictationHudGeometryTests.X;

    private static XDocument Xaml() => XDocument.Load(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml"));

    private static string Code() =>
        File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs")).ReplaceLineEndings("\n");

    private static XElement Named(XDocument xaml, string name) => DictationHudGeometryTests.Named(xaml, name);

    [Fact]
    public void The_bars_share_the_marks_slot_and_stay_hidden_until_a_level_arrives()
    {
        var xaml = Xaml();
        var slot = Named(xaml, "MarkSlot");
        var meter = Named(xaml, "Meter");

        Assert.Same(slot, Named(xaml, "Mark").Parent);
        Assert.Same(slot, meter.Parent);
        Assert.Equal("Collapsed", (string?)meter.Attribute("Visibility"));
        Assert.Same(meter, Named(xaml, "FlatLine").Parent);
        Assert.Same(meter, Named(xaml, "Bars").Parent);
        var bars = Named(xaml, "Bars").Elements().ToArray();
        Assert.Equal(DictationHudLevel.BarCount, bars.Length);
        Assert.All(bars, bar => Assert.Equal("Rectangle", bar.Name.LocalName));
        Assert.Equal(["Bar0", "Bar1", "Bar2"], bars.Select(b => (string?)b.Attribute(X + "Name")));
        // The slot comes before the text, inside the capsule.
        Assert.True(slot.IsBefore(Named(xaml, "Lines")));
    }

    /// <summary>The Mac's violet, foot to top, on every bar and on the line.</summary>
    [Fact]
    public void The_bars_and_the_line_are_the_macs_violet_gradient()
    {
        var xaml = Xaml();
        var fill = xaml.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "LevelFill");
        Assert.Equal("LinearGradientBrush", fill.Name.LocalName);
        Assert.Equal(("0.5,1", "0.5,0"), ((string?)fill.Attribute("StartPoint"), (string?)fill.Attribute("EndPoint")));
        Assert.Equal(["#7A4FD4", "#9F7AEA"], fill.Elements().Select(s => (string?)s.Attribute("Color")));
        foreach (var name in new[] { "FlatLine", "Bar0", "Bar1", "Bar2" })
        {
            Assert.Equal("{StaticResource LevelFill}", (string?)Named(xaml, name).Attribute("Fill"));
        }
    }

    [Fact]
    public void The_slot_and_the_bars_are_sized_from_the_tested_numbers()
    {
        var constructor = DictationHudGeometryTests.Member(Code(), "public DictationHudWindow(");
        foreach (var line in new[]
        {
            "MarkSlot.Width = DictationHudGeometry.MarkSlot;",
            "MarkSlot.Height = DictationHudWaveform.FrameHeight;",
            "MarkSlot.Margin = new Thickness(0, 0, DictationHudGeometry.MarkGap, 0);",
            "FlatLine.Width = DictationHudWaveform.Width;",
            "FlatLine.Height = DictationHudWaveform.FlatLineHeight;",
            "bar.Width = DictationHudWaveform.BarWidth;",
            "Bar1.Margin = new Thickness(DictationHudWaveform.BarSpacing, 0, DictationHudWaveform.BarSpacing, 0);",
        })
        {
            Assert.Contains(line, constructor, StringComparison.Ordinal);
        }
    }

    /// <summary>Each level goes through the tested frame
    /// (<see cref="DictationHudBarsTests"/>): eased and glided, or snapped when
    /// Windows' animation effects are off. The window swaps the dot for the
    /// meter and applies every part of the frame, in that order, and keeps no
    /// level of its own.</summary>
    [Fact]
    public void A_level_steps_through_the_tested_rule_and_snaps_when_animation_effects_are_off()
    {
        var code = Code();
        Assert.Contains("private readonly DictationHudBars levelBars = new();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("meterLevel", code, StringComparison.Ordinal);

        var level = DictationHudGeometryTests.Member(code, "void IDictationHud.Level(");
        var at = 0;
        foreach (var line in new[]
        {
            "if (closed || pill is null)",
            "var frame = levelBars.Step(level, DictationHudLevelStep.IsReduced(SystemParameters.ClientAreaAnimation));",
            "Mark.Visibility = Visibility.Collapsed;",
            "Meter.Visibility = Visibility.Visible;",
            "Glide(FlatLine, OpacityProperty, frame.FlatLineOpacity, frame.Glide);",
            "Glide(Bars, OpacityProperty, frame.BarsOpacity, frame.Glide);",
            "Glide(bars[i], HeightProperty, frame.BarHeights[i], frame.Glide);",
        })
        {
            var found = level.IndexOf(line, at, StringComparison.Ordinal);
            Assert.True(found >= 0, $"IDictationHud.Level no longer has, after the line before it: {line}");
            at = found + line.Length;
        }

        var glide = DictationHudGeometryTests.Member(code, "private static void Glide(");
        var snap = glide.IndexOf("if (glide <= TimeSpan.Zero)", StringComparison.Ordinal);
        Assert.True(snap >= 0 && glide.IndexOf("element.BeginAnimation(property, null);", snap, StringComparison.Ordinal) > snap,
            "a snap drops any glide still running");
    }

    /// <summary>Every show and every hide puts the dot back, and forgets the
    /// level, so a bar never stands beside a sentence that is not "Listening".</summary>
    [Fact]
    public void Every_show_and_hide_puts_the_dot_back()
    {
        var code = Code();
        var show = DictationHudGeometryTests.Member(code, "void IDictationHud.Show(");
        var mark = show.IndexOf("UseMark();", StringComparison.Ordinal);
        Assert.True(mark >= 0 && mark < show.IndexOf("StatusLine.Text = ", StringComparison.Ordinal));
        Assert.Contains("UseMark();", DictationHudGeometryTests.Member(code, "private void HidePill()"), StringComparison.Ordinal);
        var useMark = DictationHudGeometryTests.Member(code, "private void UseMark()");
        foreach (var line in new[] { "levelBars.Reset();", "Meter.Visibility = Visibility.Collapsed;", "Mark.Visibility = Visibility.Visible;" })
        {
            Assert.Contains(line, useMark, StringComparison.Ordinal);
        }
    }

    /// <summary>The level is never given to a screen reader.</summary>
    [Fact]
    public void A_level_is_never_given_to_a_screen_reader()
    {
        var level = DictationHudGeometryTests.Member(Code(), "void IDictationHud.Level(");
        foreach (var word in new[] { "Announce", "AutomationProperties", "LiveRegion", "StatusLine" })
        {
            Assert.DoesNotContain(word, level, StringComparison.Ordinal);
        }
    }
}
