using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The in-hold silence tracker (<see cref="DictationSilenceTracker"/>) on
/// synthetic level streams, and the stops it drives through the controller:
/// the hands-free warning and stop, the never-spoke stop, the held stuck-key
/// stop and the 30 s / 60 s / Never choice. The scenario names are the limit
/// design's step 7 ("quiet room with a 31 s pause stops; −35 dBFS noise with
/// a pause stops; speech with 3 s gaps never warns; isolated clicks do not
/// reset the timer"), so the Mac's stage 1 can port them by name. The
/// streams are synthetic: they show which way the rule fails, not where a
/// real voice sits.
/// </summary>
public sealed class SilenceStopTests
{
    private const int Rate = SyntheticSpeech.Rate;

    /// <summary>−35 dBFS of white noise: a fan, a café.</summary>
    private const float Noisy = 0.0178f;

    /// <summary>A quiet room.</summary>
    private const float QuietRoom = SyntheticSpeech.QuietRoom;

    // ─── Synthetic streams (SyntheticSpeech.cs) ─────────────────────────

    private static float[] Noise(double seconds, float rms, int seed) => SyntheticSpeech.Noise(seconds, rms, seed);

    private static float[] Talk(double seconds, float room, int seed = 1) => SyntheticSpeech.Talk(seconds, room, seed);

    /// <summary>A burst of <paramref name="milliseconds"/> at 0.3 in a quiet room.</summary>
    private static float[] Burst(int milliseconds, int seed = 3)
    {
        var samples = Noise(milliseconds / 1000.0, QuietRoom, seed);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] += 0.3f * MathF.Sin(2 * MathF.PI * 220 * i / Rate);
        }

        return samples;
    }

    /// <summary>Feed <paramref name="stream"/> in 10 ms buffers and return the
    /// longest silence the tracker read along the way.</summary>
    private static TimeSpan Feed(DictationSilenceTracker tracker, float[] stream)
    {
        var longest = TimeSpan.Zero;
        for (var start = 0; start < stream.Length; start += Rate / 100)
        {
            tracker.Offer(stream.AsSpan(start, Math.Min(Rate / 100, stream.Length - start)), Rate);
            if (tracker.HeardSpeech && tracker.SilentFor > longest)
            {
                longest = tracker.SilentFor;
            }
        }

        return longest;
    }

    // ─── The tracker on its own ─────────────────────────────────────────

    [Fact]
    public void Quiet_room_with_a_31_s_pause_reads_as_31_s_of_silence()
    {
        var tracker = new DictationSilenceTracker();
        Feed(tracker, Talk(5, QuietRoom));
        Assert.True(tracker.HeardSpeech);
        Assert.True(tracker.SilentFor < TimeSpan.FromSeconds(0.2), $"{tracker.SilentFor} after speech");

        Feed(tracker, Noise(31, QuietRoom, 7));

        Assert.InRange(tracker.SilentFor.TotalSeconds, 30.9, 31.2);
        Assert.Equal(DictationSilenceTracker.Floor, tracker.Threshold);
    }

    /// <summary>The fixed floor alone calls −35 dBFS of noise speech forever,
    /// and a pause in it is never found; the room's own floor finds it.</summary>
    [Fact]
    public void Noise_at_minus_35_dBFS_with_a_pause_reads_as_silence()
    {
        Assert.True(Noisy > DictationSilenceTracker.Floor, "the noise clears the fixed floor");
        var tracker = new DictationSilenceTracker();
        Feed(tracker, Noise(2, Noisy, 5));
        Feed(tracker, Talk(5, Noisy));
        Assert.True(tracker.HeardSpeech);

        Feed(tracker, Noise(31, Noisy, 9));

        Assert.InRange(tracker.SilentFor.TotalSeconds, 30.5, 31.2);
        Assert.True(tracker.Threshold > Noisy * 2, $"the room's floor {tracker.Threshold} sits above its noise");
    }

    [Fact]
    public void Noise_alone_is_never_speech()
    {
        var tracker = new DictationSilenceTracker();
        Feed(tracker, Noise(20, Noisy, 11));

        Assert.False(tracker.HeardSpeech);
        Assert.InRange(tracker.SilentFor.TotalSeconds, 19.9, 20.1);
    }

    [Fact]
    public void Speech_with_3_s_gaps_never_reads_as_more_than_3_s_of_silence()
    {
        var tracker = new DictationSilenceTracker();
        var longest = TimeSpan.Zero;
        for (var n = 0; n < 30; n++)
        {
            var heard = Feed(tracker, Talk(1.5, QuietRoom, n));
            var gap = Feed(tracker, Noise(3, QuietRoom, 100 + n));
            longest = new[] { longest, heard, gap }.Max();
        }

        Assert.True(tracker.HeardSpeech);
        Assert.InRange(longest.TotalSeconds, 2.9, 3.4);
    }

    [Fact]
    public void Isolated_clicks_are_not_speech()
    {
        var tracker = new DictationSilenceTracker();
        Feed(tracker, Talk(3, QuietRoom));
        for (var second = 0; second < 31; second++)
        {
            Feed(tracker, SyntheticSpeech.WithClick(Noise(1, QuietRoom, 200 + second)));
        }

        Assert.InRange(tracker.SilentFor.TotalSeconds, 30.9, 31.2);
    }

    /// <summary>Nine whole frames, 270 ms, is the shortest run that counts:
    /// eight (240 ms) is not. Frame-aligned here; a burst that starts mid-frame
    /// lights up to one frame more at each end, so the rule's edge is a frame
    /// wide either way, and far above a key click's 30 ms.</summary>
    [Fact]
    public void A_run_shorter_than_250_ms_is_not_speech_and_a_longer_one_is()
    {
        var tracker = new DictationSilenceTracker();
        Feed(tracker, Noise(1.98, QuietRoom, 1));
        Feed(tracker, Burst(240));
        Feed(tracker, Noise(0.99, QuietRoom, 2));
        Assert.False(tracker.HeardSpeech);

        Feed(tracker, Burst(270));
        Assert.True(tracker.HeardSpeech);
        Assert.Equal(9, Math.Ceiling(DictationSilenceTracker.SpeechRun.TotalSeconds / DictationSilenceTracker.FrameSeconds));
    }

    /// <summary>A driver that sends NaN or infinity in every frame for two
    /// seconds: those samples are read as silence, not as a room floor that
    /// is not a number, which would call every later frame silence and stop
    /// a hold the user is talking into.</summary>
    [Fact]
    public void A_sample_that_is_not_a_number_does_not_hide_speech()
    {
        var tracker = new DictationSilenceTracker();
        var poisoned = Noise(2, QuietRoom, 4);
        for (var i = 0; i < poisoned.Length; i += 400)
        {
            poisoned[i] = i % 800 == 0 ? float.NaN : float.PositiveInfinity;
        }

        Feed(tracker, poisoned);

        Assert.Equal(DictationSilenceTracker.Floor, tracker.Threshold);
        Feed(tracker, Talk(3, QuietRoom));
        Assert.True(tracker.HeardSpeech);
    }

    /// <summary>Stereo is heard downmixed, and a buffer that claims no rate
    /// or no channels is not audio.</summary>
    [Fact]
    public void Stereo_buffers_are_downmixed_and_empty_ones_ignored()
    {
        var tracker = new DictationSilenceTracker();
        var mono = Talk(3, QuietRoom);
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = mono[i];
            stereo[(2 * i) + 1] = mono[i];
        }

        tracker.Offer(this, new AudioChunk(stereo, Rate, 2));
        tracker.Offer(this, new AudioChunk(mono, 0, 1));
        tracker.Offer(this, new AudioChunk(mono, Rate, 0));

        Assert.True(tracker.HeardSpeech);
        Assert.InRange(tracker.Heard.TotalSeconds, 2.9, 3.01);
    }

    // ─── Through the controller ─────────────────────────────────────────

    /// <summary>A latch, then speech, so the hold has heard someone.</summary>
    private static async Task<HandsFree> LatchedAndTalking(
        float room = QuietRoom, Func<DictationControllerOptions, DictationControllerOptions>? options = null,
        Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
    {
        var hands = new HandsFree(options: options, logs: logs);
        await hands.EnableAsync();
        hands.DoubleTap();
        Assert.True(hands.Latched && hands.Listening);
        Talk(hands, 3, room);
        return hands;
    }

    /// <summary><paramref name="seconds"/> of speech, a second at a time,
    /// polled as the app does.</summary>
    private static void Talk(HandsFree hands, int seconds, float room = QuietRoom)
    {
        for (var second = 0; second < seconds; second++)
        {
            hands.Rig.Platform.Microphone.Talk(TimeSpan.FromSeconds(1), room);
            hands.Wait(1_000);
        }
    }

    /// <summary><paramref name="seconds"/> of the room, a second at a time.</summary>
    private static void Silence(HandsFree hands, int seconds, float room = QuietRoom, Action? each = null)
    {
        for (var second = 0; second < seconds && hands.Listening; second++)
        {
            hands.Rig.Platform.Microphone.Room(TimeSpan.FromSeconds(1), room);
            each?.Invoke();
            hands.Wait(1_000);
        }
    }

    private static string Shown(HandsFree hands) => hands.Rig.Hud.Current?.Text ?? string.Empty;

    private static bool Warned(HandsFree hands) =>
        hands.Rig.Hud.Shows.Any(m => m.Text.StartsWith("No speech for", StringComparison.Ordinal));

    private static readonly string StoppedAfterSilence =
        "Text inserted. Hands-free dictation stopped after 30 seconds without speech; double-tap Right Ctrl to keep going.";

    /// <summary>
    /// The design's "quiet room with a 31 s pause stops": a warning at 20 s
    /// of silence, shown and never spoken, counting down each second; at
    /// 30 s the words go in and the latch ends, with no sound at all.
    /// </summary>
    [Fact]
    public async Task Quiet_room_with_a_31_s_pause_stops()
    {
        using var hands = await LatchedAndTalking();
        var hud = hands.Rig.Hud;

        Silence(hands, 19);
        Assert.False(Warned(hands));
        Silence(hands, 1);
        Assert.Equal("No speech for 20 s — stopping in 10 s. Keep talking to continue", Shown(hands));
        Assert.Equal("Listening, hands-free", hud.Current!.UnannouncedName);
        Assert.False(hud.Announced[^1]);
        Silence(hands, 5);
        Assert.Equal("No speech for 25 s — stopping in 5 s. Keep talking to continue", Shown(hands));
        Assert.True(hands.Listening && hands.Latched);

        Silence(hands, 6);
        hands.DrainUntil(() => hands.Inserted);

        Assert.False(hands.Latched);
        Assert.False(hands.Listening);
        Assert.Equal(1, hands.Pastes);
        Assert.Equal(StoppedAfterSilence, hands.Status);
        Assert.Contains(hands.Rig.View.States, s => s.Status == "No speech for 30 seconds, so hands-free dictation stopped. Transcribing…");
        Assert.Equal("Stopped after 30 s of silence — your words went in, double-tap Right Ctrl to keep going", Shown(hands));
        Assert.Equal(HudTone.Caution, hud.Current!.Tone);
        Assert.Equal(0, hands.Rig.Platform.AlertSounds);
        var warnings = hud.Shows.Select((m, i) => (m, i)).Where(s => s.m.Text.StartsWith("No speech", StringComparison.Ordinal)).ToArray();
        Assert.Equal(10, warnings.Length);
        Assert.All(warnings, s => Assert.False(hud.Announced[s.i]));
    }

    [Fact]
    public async Task Noise_at_minus_35_dBFS_with_a_pause_stops()
    {
        using var hands = await LatchedAndTalking(Noisy);

        Silence(hands, 20, Noisy);
        Assert.StartsWith("No speech for 20 s", Shown(hands), StringComparison.Ordinal);
        Silence(hands, 11, Noisy);
        hands.DrainUntil(() => hands.Inserted);

        Assert.Equal(StoppedAfterSilence, hands.Status);
        Assert.Equal(1, hands.Pastes);
    }

    [Fact]
    public async Task Speech_with_3_s_gaps_never_warns()
    {
        using var hands = await LatchedAndTalking();
        for (var phrase = 0; phrase < 40; phrase++)
        {
            Talk(hands, 1);
            Silence(hands, 3);
        }

        Assert.True(hands.Latched && hands.Listening, hands.Status);
        Assert.False(Warned(hands));
        hands.Press();
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal("Text inserted. Hold Right Ctrl for your next sentence.", hands.Status);
    }

    [Fact]
    public async Task Isolated_clicks_do_not_reset_the_timer()
    {
        using var hands = await LatchedAndTalking();

        Silence(hands, 31, each: () => hands.Rig.Platform.Microphone.Click());
        hands.DrainUntil(() => hands.Inserted);

        Assert.Equal(StoppedAfterSilence, hands.Status);
    }

    /// <summary>Speaking again inside the warning takes it down without a
    /// word, and the silence starts again from there.</summary>
    [Fact]
    public async Task Speaking_again_clears_the_warning_silently()
    {
        using var hands = await LatchedAndTalking();
        Silence(hands, 22);
        Assert.StartsWith("No speech for 22 s", Shown(hands), StringComparison.Ordinal);
        var spoken = hands.Rig.Hud.Announced.Count(a => a);

        Talk(hands, 2);
        Assert.StartsWith("Listening — 0:", Shown(hands), StringComparison.Ordinal);
        Silence(hands, 19);
        Assert.True(hands.Listening);
        Assert.StartsWith("Listening — 0:", Shown(hands), StringComparison.Ordinal);
        Assert.Equal(spoken, hands.Rig.Hud.Announced.Count(a => a));
        Silence(hands, 12);
        hands.DrainUntil(() => hands.Inserted);
    }

    /// <summary>A double-tap with nothing said at either tap
    /// (<see cref="HandsFree.DoubleTap"/> speaks at each): the hold's audio is
    /// only what the test sends afterwards.</summary>
    private static void SilentDoubleTap(HandsFree hands)
    {
        hands.Press();
        hands.Advance(60);
        hands.Release();
        hands.Advance(100);
        hands.Press();
        hands.Advance(60);
        hands.Release();
        Assert.True(hands.Latched && hands.Listening, hands.Status);
    }

    /// <summary>A voice 4 dB over the −35 dBFS room: a 220 Hz tone at 0.04,
    /// under the tracker's 10 dB margin, so it hears nobody.</summary>
    private const float SoftVoice = 0.04f;

    /// <summary>The design's "never spoke → stop at 15 s with Nothing heard,
    /// insert nothing", in a quiet room. The stop ends the hold as a release,
    /// as every stop does; the session's quiet-room check finds nothing to
    /// send, so the recogniser is not asked to invent a sentence for the room
    /// and nothing goes in.</summary>
    [Fact]
    public async Task A_latch_nobody_speaks_into_stops_at_15_s_and_inserts_nothing()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-nothing-heard-").FullName;
        try
        {
            using (var logs = FileLoggerFactory.TryCreate(dir, out _)!)
            {
                using var hands = new HandsFree(logs: logs);
                await hands.EnableAsync();
                SilentDoubleTap(hands);

                Silence(hands, 14);
                Assert.True(hands.Latched && hands.Listening);
                Silence(hands, 1);
                Assert.False(hands.Latched);
                hands.DrainUntil(() => hands.Status.StartsWith("Nothing was heard", StringComparison.Ordinal));

                Assert.False(hands.Listening);
                Assert.Equal(0, hands.Transcribes);
                Assert.Equal(0, hands.Pastes);
                Assert.Equal(
                    "Nothing was heard for 15 seconds, so hands-free dictation stopped. Nothing was inserted. Double-tap Right Ctrl to try again.",
                    hands.Status);
                Assert.Equal("Nothing heard in 15 s — stopped, double-tap Right Ctrl to try again", Shown(hands));
                Assert.Equal(HudTone.Caution, hands.Rig.Hud.Current!.Tone);
                Assert.False(hands.Rig.Controller.HoldsRecentText);

                // The next double-tap latches as usual.
                hands.Wait(400);
                hands.LatchesAndFinishes(transcribedBefore: 0);
            }

            var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
            Assert.Contains("Hands-free dictation ended: nothing was heard in 15 s", log, StringComparison.Ordinal);
            Assert.Contains("Nothing was sent to the recogniser", log, StringComparison.Ordinal);
            Assert.Contains("event=released", log, StringComparison.Ordinal);
            Assert.Contains("result=failed stage=asr reason=no_text", log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Every hold counts its own speech: a latch nobody speaks into, right
    /// after a dictation that heard plenty, still stops at 15 s as nothing
    /// heard, not after 30 s as if the last hold's speech were its own.
    /// </summary>
    [Fact]
    public async Task A_silent_latch_after_a_spoken_one_still_stops_at_15_s_as_nothing_heard()
    {
        using var hands = await LatchedAndTalking();
        hands.Press();
        hands.Release();
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(1, hands.Pastes);
        hands.Wait(400);

        SilentDoubleTap(hands);
        Silence(hands, 14);
        Assert.True(hands.Latched && hands.Listening);
        Silence(hands, 1);
        Assert.False(hands.Latched);
        hands.DrainUntil(() => hands.Status.StartsWith("Nothing was heard for 15 seconds", StringComparison.Ordinal));
        Assert.Equal(1, hands.Transcribes);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>
    /// Rule zero at the never-spoke stop: speech a few decibels over a noisy
    /// room is under the tracker's margin, so the tracker hears nobody and
    /// the latch stops at 15 s; the stop is a release, the session finds the
    /// room is not silent, and the words are transcribed and inserted instead
    /// of being thrown away under "Nothing heard".
    /// </summary>
    [Fact]
    public async Task Soft_speech_the_tracker_misses_in_a_noisy_room_still_goes_in_at_15_s()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        SilentDoubleTap(hands);

        for (var second = 0; second < 14; second++)
        {
            hands.Rig.Platform.Microphone.Talk(TimeSpan.FromSeconds(1), Noisy, SoftVoice);
            hands.Wait(1_000);
        }

        Assert.True(hands.Latched && hands.Listening);
        hands.Rig.Platform.Microphone.Talk(TimeSpan.FromSeconds(1), Noisy, SoftVoice);
        hands.Wait(1_000);
        Assert.False(hands.Latched);
        hands.DrainUntil(() => hands.Inserted);

        Assert.Equal(1, hands.Transcribes);
        Assert.Equal(1, hands.Pastes);
        Assert.Contains(hands.Rig.View.States, s => s.Status == "No speech for 15 seconds, so hands-free dictation stopped. Transcribing…");
        Assert.Equal(
            "Text inserted. Hands-free dictation stopped after 15 seconds without speech; double-tap Right Ctrl to keep going.",
            hands.Status);
        Assert.Equal("Stopped after 15 s of silence — your words went in, double-tap Right Ctrl to keep going", Shown(hands));
    }

    /// <summary>The clock decides, not the audio: a microphone that sends
    /// nothing at all is silence too, and the latch still ends at 15 s, with
    /// the microphone's own ending, not "Nothing heard".</summary>
    [Fact]
    public async Task A_microphone_that_sends_nothing_is_silence_too()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        SilentDoubleTap(hands);

        for (var second = 0; second < 14; second++)
        {
            hands.Wait(1_000);
        }

        Assert.True(hands.Latched && hands.Listening);
        hands.Wait(1_000);
        Assert.False(hands.Latched);
        hands.DrainUntilEnded();
        Assert.Equal("The microphone opened but sent no sound. Check the input device in Windows sound settings.", hands.Status);
        Assert.Equal(0, hands.Transcribes);
    }

    /// <summary>The design's held row: no silence stop (a held key is the
    /// user meaning to talk), and only a key held a minute with no speech is
    /// let go, its words inserted. Windows warns over that minute's last
    /// 10 s, as the hands-free stop does, since what is said after the stop
    /// is not recorded. The key is still down, so nothing records until it
    /// comes up and is held again.</summary>
    [Fact]
    public async Task A_held_key_stops_only_after_a_minute_without_speech()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.Press();
        hands.Wait(400);
        Talk(hands, 3);

        Silence(hands, 49);
        Assert.True(hands.Listening, hands.Status);
        Assert.False(Warned(hands));
        Assert.Equal(DictationCopy.HudHeld, Shown(hands));
        Silence(hands, 1);
        Assert.Equal("No speech for 50 s — stopping in 10 s. Keep talking to continue", Shown(hands));
        Assert.Equal("Listening", hands.Rig.Hud.Current!.UnannouncedName);
        Assert.False(hands.Rig.Hud.Announced[^1]);
        Silence(hands, 9);
        Assert.Equal("No speech for 59 s — stopping in 1 s. Keep talking to continue", Shown(hands));
        Assert.True(hands.Listening, hands.Status);

        Silence(hands, 1);
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(
            "Text inserted. Recording stopped after a minute without speech; release Right Ctrl, then hold it again to keep going.",
            hands.Status);
        Assert.Contains(hands.Rig.View.States, s => s.Status == "No speech for a minute with Right Ctrl held, so it stopped. Transcribing…");
        Assert.Equal("Stopped — no speech for a minute with Right Ctrl held. Your words went in", Shown(hands));

        // Still held: nothing records until the key comes up.
        hands.Wait(5_000);
        Assert.False(hands.Listening);
        Assert.Equal(1, hands.Rig.Platform.Microphone.Starts);
        hands.Release();
        hands.Wait(400);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>Held, speaking inside the stuck-key warning takes it down
    /// without a word, and the minute starts again from there.</summary>
    [Fact]
    public async Task Speaking_while_held_takes_the_stuck_key_warning_down()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.Press();
        hands.Wait(400);
        Talk(hands, 2);
        Silence(hands, 52);
        Assert.StartsWith("No speech for 52 s", Shown(hands), StringComparison.Ordinal);
        var spoken = hands.Rig.Hud.Announced.Count(a => a);

        Talk(hands, 2);
        Assert.Equal(DictationCopy.HudHeld, Shown(hands));
        Silence(hands, 49);
        Assert.Equal(DictationCopy.HudHeld, Shown(hands));
        Assert.True(hands.Listening, hands.Status);
        Assert.Equal(spoken, hands.Rig.Hud.Announced.Count(a => a));
        hands.Release();
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal("Text inserted. Hold Right Ctrl for your next sentence.", hands.Status);
    }

    [Fact]
    public async Task The_60_s_choice_stops_after_a_minute_of_silence()
    {
        using var hands = await LatchedAndTalking();
        hands.Rig.Controller.ChooseSilenceStop(DictationSilenceStop.After60Seconds);

        Silence(hands, 49);
        Assert.False(Warned(hands));
        Silence(hands, 1);
        Assert.Equal("No speech for 50 s — stopping in 10 s. Keep talking to continue", Shown(hands));
        Silence(hands, 11);
        hands.DrainUntil(() => hands.Inserted);

        Assert.Equal(
            "Text inserted. Hands-free dictation stopped after 60 seconds without speech; double-tap Right Ctrl to keep going.",
            hands.Status);
        Assert.Equal("Stopped after 60 s of silence — your words went in, double-tap Right Ctrl to keep going", Shown(hands));
    }

    /// <summary>
    /// The countdown's sound plays through the speakers while the microphone
    /// is open, and the microphone hears it as speech. What the tracker hears
    /// in the two seconds after it does not restart the silence, so a user
    /// who fell silent before the countdown is still stopped when due, not
    /// given a second wait.
    /// </summary>
    [Fact]
    public async Task The_countdown_s_own_sound_does_not_restart_the_silence()
    {
        using var hands = await LatchedAndTalking();
        Talk(hands, 222);

        var silent = 0;
        var heardBack = false;
        while (hands.Listening && silent < 60)
        {
            if (hands.Rig.Platform.AlertSounds == 1 && !heardBack)
            {
                // The sound, heard back: a second the tracker calls speech.
                hands.Rig.Platform.Microphone.Talk(TimeSpan.FromSeconds(1));
                heardBack = true;
            }
            else
            {
                hands.Rig.Platform.Microphone.Room(TimeSpan.FromSeconds(1));
            }

            hands.Wait(1_000);
            silent++;
        }

        Assert.True(heardBack, "the countdown sounded during the silence");
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(30, silent);
        Assert.Equal(StoppedAfterSilence, hands.Status);
    }

    /// <summary>Never: a silent latch stays open, with no warning, until the
    /// limit ends it; nothing heard does not end it either.</summary>
    [Fact]
    public async Task Never_keeps_a_silent_latch_open_until_the_limit()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.Rig.Controller.ChooseSilenceStop(DictationSilenceStop.Off);
        hands.DoubleTap();

        Silence(hands, 120);
        Assert.True(hands.Latched && hands.Listening);
        Assert.False(Warned(hands));
        hands.Wait(180_000);
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal("Text inserted. A recording stops at five minutes; hold Right Ctrl again to continue.", hands.Status);
    }

    /// <summary>A cancel lets go of the microphone at once, the silence
    /// tracker with the meter, before the repaint that follows it arrives.</summary>
    [Fact]
    public async Task A_cancel_lets_go_of_the_tracker_before_its_repaint_arrives()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        // The arm's own (the tap's and the take spool's), the meter's and the tracker's.
        Assert.Equal(FakeMicrophone.ArmedListeners + 2, rig.Platform.Microphone.Listeners);

        rig.Controller.Cancel();

        Assert.Equal(FakeMicrophone.ArmedListeners, rig.Platform.Microphone.Listeners); // the arm's own, before any drain
        rig.Dispatcher.Drain();
        rig.Release();
    }

    /// <summary>A silence stop ends the latch in the tick that reaches it:
    /// Space types again at once, and the next double-tap latches.</summary>
    [Fact]
    public async Task A_silence_stop_ends_the_latch_at_once()
    {
        using var hands = await LatchedAndTalking();
        Silence(hands, 30);

        Assert.False(hands.Latched);
        Assert.Equal(HoldKeyTransition.None, hands.Key.KeyEvent(HoldKeyState.VkSpace, HandsFree.Down));
        hands.Key.KeyEvent(HoldKeyState.VkSpace, HandsFree.Up);
        hands.DrainUntil(() => hands.Inserted);
        hands.Wait(400);
        hands.LatchesAndFinishes(transcribedBefore: 1);
    }
}
