using System.Text.Json.Nodes;
using Dictation.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// How long a Windows hold may listen (<see cref="DictationHoldPolicy"/>):
/// the five-minute limit, the minute's countdown and its one sound, the clock
/// that starts when the microphone comes up, the memory cap behind the limit,
/// and the hands-free silence choice. Ported from the Mac's
/// MaximumHoldTests.swift where it has the scenario ("the product's ceiling
/// is …"); the rest is the limit design's stage 1, which the Mac follows in
/// its next wave. The silence stops themselves are SilenceStopTests.
/// </summary>
public sealed class HoldPolicyTests
{
    private static readonly DictationHoldPolicy Product = DictationHoldPolicy.Windows;

    private static DictationHoldMoment Held(double elapsed, double silent, bool heard = true) =>
        new(TimeSpan.FromSeconds(elapsed), Latched: false, heard, TimeSpan.FromSeconds(silent));

    private static DictationHoldMoment Latched(double elapsed, double silent, bool heard = true) =>
        new(TimeSpan.FromSeconds(elapsed), Latched: true, heard, TimeSpan.FromSeconds(silent));

    private static DictationHoldStop? StopAt(DictationHoldMoment moment, DictationSilenceStop setting = DictationSilenceStop.After30Seconds) =>
        Product.Judge(moment, setting).Ending?.Stop;

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static Rig Quiet(Action<Rig>? beforeStart = null, Microsoft.Extensions.Logging.ILoggerFactory? logs = null) =>
        new(options: o => o with { HoldPolicy = o.HoldPolicy.WithoutSilenceStops() }, beforeStart: beforeStart, logs: logs);

    private static void Wait(Rig rig, double seconds)
    {
        rig.Clock.Advance(TimeSpan.FromSeconds(seconds));
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
    }

    // ─── The policy (MaximumHoldTests, and the design's numbers) ────────

    /// <summary>Mac <c>productCeiling</c>, "the product's ceiling is 120
    /// seconds", at Windows' stage-1 numbers.</summary>
    [Fact]
    public void The_product_s_ceiling_is_five_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Product.Ceiling);
        Assert.Equal(TimeSpan.FromSeconds(60), Product.Countdown);
        Assert.Equal(TimeSpan.FromMinutes(6), Product.MemoryCap);
        Assert.Equal(TimeSpan.FromSeconds(15), Product.NothingHeardStop);
        Assert.Equal(TimeSpan.FromSeconds(10), Product.SilenceWarning);
        Assert.Equal(TimeSpan.FromSeconds(60), Product.StuckKeyStop);
        Assert.True(Product.StopsOnSilence);
        Assert.Equal(Product, new DictationControllerOptions().HoldPolicy);
        Assert.Equal(TimeSpan.FromSeconds(30), DictationHoldPolicy.SilenceStop(DictationSilenceStop.After30Seconds));
        Assert.Equal(TimeSpan.FromSeconds(60), DictationHoldPolicy.SilenceStop(DictationSilenceStop.After60Seconds));
        Assert.Null(DictationHoldPolicy.SilenceStop(DictationSilenceStop.Off));
        Assert.Equal(DictationSilenceStop.After30Seconds, new DictationSettings().SilenceStop);
    }

    [Fact]
    public void The_limit_comes_first_whatever_the_silence_says()
    {
        Assert.Equal(DictationHoldStop.Ceiling, StopAt(Held(300, 0)));
        Assert.Equal(DictationHoldStop.Ceiling, StopAt(Held(300, 300)));
        Assert.Equal(DictationHoldStop.Ceiling, StopAt(Latched(300, 300, heard: false)));
        Assert.Equal(DictationHoldStop.Ceiling, StopAt(Latched(301, 0), DictationSilenceStop.Off));
        Assert.Equal(Product.Ceiling, Product.Judge(Held(300, 0), DictationSilenceStop.Off).Ending!.After);
        Assert.Null(StopAt(Held(299.99, 0)));
    }

    [Fact]
    public void The_countdown_is_the_last_minute_held_or_hands_free()
    {
        Assert.False(Product.Judge(Held(239.99, 0), DictationSilenceStop.After30Seconds).CountingDown);
        Assert.True(Product.Judge(Held(240, 0), DictationSilenceStop.After30Seconds).CountingDown);
        Assert.True(Product.Judge(Latched(250, 0), DictationSilenceStop.After30Seconds).CountingDown);
        Assert.True(Product.Judge(Latched(299, 0), DictationSilenceStop.Off).CountingDown);
    }

    /// <summary>A held key has no silence stop, only the stuck-key one, and
    /// warns over its last 10 s as the hands-free stop does: what is said
    /// after it, with the key still down, is not recorded.</summary>
    [Fact]
    public void A_held_key_warns_at_50_s_and_stops_only_after_a_minute_without_speech()
    {
        Assert.Null(StopAt(Held(100, 30)));
        Assert.Null(StopAt(Held(100, 59.9)));
        Assert.Null(Product.Judge(Held(100, 30), DictationSilenceStop.After30Seconds).Warning);
        Assert.Null(Product.Judge(Held(100, 49.9), DictationSilenceStop.After30Seconds).Warning);
        Assert.Equal(
            new DictationSilenceWarning(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(10)),
            Product.Judge(Held(100, 50), DictationSilenceStop.After30Seconds).Warning);
        Assert.Equal(TimeSpan.FromSeconds(5), Product.Judge(Held(100, 55), DictationSilenceStop.Off).Warning!.Value.StopsIn);
        Assert.Equal(DictationHoldStop.StuckKey, StopAt(Held(100, 60)));
        Assert.Equal(DictationHoldStop.StuckKey, StopAt(Held(60, 60, heard: false)));
        Assert.Equal(DictationHoldStop.StuckKey, StopAt(Held(100, 60), DictationSilenceStop.Off));
        Assert.Equal(TimeSpan.FromSeconds(60), Product.Judge(Held(100, 60), default).Ending!.After);
    }

    [Fact]
    public void Hands_free_with_nothing_heard_stops_at_15_s_without_a_warning()
    {
        Assert.Null(StopAt(Latched(14.9, 14.9, heard: false)));
        Assert.Null(Product.Judge(Latched(10, 10, heard: false), default).Warning);
        Assert.Equal(DictationHoldStop.NothingHeard, StopAt(Latched(15, 15, heard: false)));
        Assert.Equal(DictationHoldStop.NothingHeard, StopAt(Latched(15, 15, heard: false), DictationSilenceStop.After60Seconds));
        Assert.Null(StopAt(Latched(15, 15, heard: false), DictationSilenceStop.Off));
    }

    [Fact]
    public void Hands_free_after_speech_warns_at_20_s_and_stops_at_30_s()
    {
        Assert.Null(Product.Judge(Latched(60, 19.9), default).Warning);
        Assert.Equal(new DictationSilenceWarning(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10)), Product.Judge(Latched(60, 20), default).Warning);
        Assert.Equal(TimeSpan.FromSeconds(0.5), Product.Judge(Latched(60, 29.5), default).Warning!.Value.StopsIn);
        Assert.Null(StopAt(Latched(60, 29.9)));
        var stop = Product.Judge(Latched(60, 30), default).Ending!;
        Assert.Equal(new DictationHoldEnding(DictationHoldStop.Silence, TimeSpan.FromSeconds(30)), stop);
    }

    [Fact]
    public void The_60_s_choice_warns_at_50_s_and_stops_at_60_s()
    {
        const DictationSilenceStop sixty = DictationSilenceStop.After60Seconds;
        Assert.Null(StopAt(Latched(100, 45), sixty));
        Assert.Null(Product.Judge(Latched(100, 49.9), sixty).Warning);
        Assert.Equal(TimeSpan.FromSeconds(10), Product.Judge(Latched(100, 50), sixty).Warning!.Value.StopsIn);
        Assert.Equal(new DictationHoldEnding(DictationHoldStop.Silence, TimeSpan.FromSeconds(60)), Product.Judge(Latched(100, 60), sixty).Ending);
    }

    [Fact]
    public void Off_never_stops_hands_free_on_silence()
    {
        var off = Product.Judge(Latched(250, 250), DictationSilenceStop.Off);
        Assert.Null(off.Ending);
        Assert.Null(off.Warning);
        Assert.True(off.CountingDown);
    }

    [Fact]
    public void Without_silence_stops_only_the_limit_ends_a_hold()
    {
        var quiet = Product.WithoutSilenceStops();
        Assert.Null(quiet.Judge(Held(299, 299), default).Ending);
        Assert.Null(quiet.Judge(Latched(299, 299, heard: false), default).Ending);
        Assert.Null(quiet.Judge(Latched(100, 25), default).Warning);
        Assert.Equal(DictationHoldStop.Ceiling, quiet.Judge(Held(300, 0), default).Ending!.Stop);
    }

    [Fact]
    public void A_policy_whose_numbers_cannot_work_together_is_refused()
    {
        Assert.Throws<ArgumentException>(() => (Product with { MemoryCap = Product.Ceiling }).Checked());
        Assert.Throws<ArgumentException>(() => (Product with { Countdown = Product.Ceiling }).Checked());
        Assert.Throws<ArgumentException>(() => (Product with { Countdown = TimeSpan.FromSeconds(-1) }).Checked());
        Assert.Throws<ArgumentException>(() => (Product with { SilenceWarning = TimeSpan.FromSeconds(30) }).Checked());
        Assert.Throws<ArgumentException>(() => (Product with { StuckKeyStop = Product.SilenceWarning }).Checked());
        Assert.Throws<ArgumentException>(() => (Product with { NothingHeardStop = TimeSpan.Zero }).Checked());
        Assert.Throws<ArgumentException>(() => (Product with { StuckKeyStop = TimeSpan.Zero }).Checked());
        Assert.Throws<ArgumentException>(() => (Product with { Ceiling = TimeSpan.Zero }).Checked());

        // The controller checks the one it is given.
        Assert.Throws<ArgumentException>(() => new Rig(options: o => o with { HoldPolicy = Product with { MemoryCap = TimeSpan.FromMinutes(1) } }));
    }

    // ─── The clock, the countdown and its sound, through the controller ─

    /// <summary>
    /// The design's "start the hold clock when capture comes up, as the Mac
    /// does": a device that takes two seconds to open costs the user none of
    /// the limit. Windows used to count from the admission before it.
    /// </summary>
    [Fact]
    public async Task The_hold_s_clock_starts_when_the_microphone_comes_up()
    {
        using var rig = Quiet();
        await rig.EnableAsync();
        rig.Platform.Microphone.Opening = () => rig.Clock.Advance(TimeSpan.FromSeconds(2));
        rig.Press();
        rig.Dispatcher.Drain();

        // Five minutes after the press, but not after the microphone opened.
        Wait(rig, 298);
        Assert.True(rig.Platform.Microphone.Open);
        Assert.Equal("Listening — Stops in 0:02 · Release to insert", rig.Hud.Current!.Text);

        Wait(rig, 2);
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.False(rig.Platform.Microphone.Open);
        rig.Release();
    }

    /// <summary>
    /// The design's "a tone, then a 60 s countdown": the one sound plays once
    /// per hold as the countdown starts, held or hands-free, and never before.
    /// </summary>
    [Fact]
    public async Task The_countdown_sounds_once_a_minute_before_the_limit()
    {
        using var rig = Quiet();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        Wait(rig, 239);
        Assert.Equal(0, rig.Platform.AlertSounds);
        Wait(rig, 1);
        Assert.Equal(1, rig.Platform.AlertSounds);
        Assert.Equal("Listening — Stops in 1:00 · Release to insert", rig.Hud.Current!.Text);
        for (var second = 0; second < 30; second++)
        {
            Wait(rig, 1);
        }

        Assert.Equal(1, rig.Platform.AlertSounds);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        // The next hold sounds again, once.
        rig.Press();
        rig.Dispatcher.Drain();
        Wait(rig, 30);
        Assert.Equal(1, rig.Platform.AlertSounds);
        Wait(rig, 220);
        Assert.Equal(2, rig.Platform.AlertSounds);
        rig.Release();
        rig.DrainUntil(() => rig.View.States.Count(Inserted) == 2);
    }

    /// <summary>A sound that cannot play is logged and changes nothing: the
    /// countdown is drawn and the limit still inserts.</summary>
    [Fact]
    public async Task A_sound_that_cannot_play_never_stops_a_dictation()
    {
        using var rig = Quiet();
        rig.Platform.AlertSoundThrows = true;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        Wait(rig, 240);
        Assert.Equal(1, rig.Platform.AlertSounds);
        Assert.Equal("Listening — Stops in 1:00 · Release to insert", rig.Hud.Current!.Text);
        Wait(rig, 60);
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(1, rig.Platform.Paste.Count);
        rig.Release();
    }

    // ─── The memory cap behind the limit ────────────────────────────────

    /// <summary>
    /// The design's "LatchedCapReached ends the hold as a ceiling stop": a UI
    /// thread that stalled past the limit never ran the tick that ends the
    /// hold, so the tap stops keeping audio at the memory cap, says so, and
    /// the hold ends there as at the limit, with the six minutes it kept.
    /// </summary>
    [Fact]
    public async Task The_memory_cap_ends_the_hold_as_the_limit_does()
    {
        using var rig = Quiet();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        var listening = rig.View.States.Count;

        // Six minutes and more of audio, and no tick: the UI thread is stuck.
        for (var second = 0; second < 365; second++)
        {
            rig.Platform.Microphone.Room(TimeSpan.FromSeconds(1));
        }

        rig.DrainUntil(() => TapSaidItsCap(rig));
        rig.DrainUntil(() => rig.View.States.Skip(listening).Any(Inserted));

        Assert.Contains(rig.View.States, s => s.Status == "Five minutes is the longest recording, so it stopped there. Transcribing…");
        Assert.Equal("Text inserted. A recording stops at five minutes; hold Right Ctrl again to continue.", rig.View.Last.Status);
        Assert.Equal(DictationCopy.HudStoppedAtLimit("Right Ctrl", TimeSpan.FromMinutes(5)), rig.Hud.Current!.Text);
        var sent = Convert.FromBase64String(
            Assert.Single(rig.Platform.Worker.FramesOf("transcribe")).GetProperty("pcm_b64").GetString()!).Length / 2;
        Assert.Equal(6 * 60 * 16_000, sent);
        rig.Release();
    }

    /// <summary>A cap the tap reported for a hold the user released before
    /// the report reached the UI thread changes nothing: the release's words
    /// go in as an ordinary release's, not as a stop at the limit.</summary>
    [Fact]
    public async Task A_cap_reported_after_the_release_changes_nothing()
    {
        using var rig = Quiet();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        var posted = rig.Dispatcher.Posted;
        for (var second = 0; second < 361; second++)
        {
            rig.Platform.Microphone.Room(TimeSpan.FromSeconds(1));
        }

        Assert.True(SpinWait.SpinUntil(() => rig.Dispatcher.Posted > posted, TimeSpan.FromSeconds(10)), "the cap was never said");
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        decode.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Equal("Text inserted. Hold Right Ctrl for your next sentence.", rig.View.Last.Status);
        Assert.Equal(DictationHudModel.For(new DictationSessionResult(DictationOutcome.Inserted, null, null)).Text, rig.Hud.Current!.Text);
    }

    /// <summary>
    /// A cap the tap reported for a capture that is over by the time the
    /// report reaches the UI thread (cancelled, and the key held again)
    /// changes nothing for the hold listening then: the report names its own
    /// capture, and that is no longer the tap's.
    /// </summary>
    [Fact]
    public async Task A_cap_reported_for_a_capture_that_is_over_does_not_end_the_next_hold()
    {
        using var rig = Quiet();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        var posted = rig.Dispatcher.Posted;
        for (var second = 0; second < 361; second++)
        {
            rig.Platform.Microphone.Room(TimeSpan.FromSeconds(1));
        }

        Assert.True(SpinWait.SpinUntil(() => rig.Dispatcher.Posted > posted, TimeSpan.FromSeconds(10)), "the cap was never said");

        // Before the cap's post runs: Cancel, let go, and hold again.
        rig.Controller.Cancel();
        rig.Release();
        rig.Press();
        rig.Controller.Tick();
        Assert.True(rig.Platform.Microphone.Open, "the next hold listens");

        rig.Dispatcher.Drain();
        Wait(rig, 1);

        Assert.True(rig.Platform.Microphone.Open, "the earlier capture's cap ended the next hold");
        Assert.DoesNotContain(rig.View.States, s => s.Status.StartsWith("Five minutes is the longest recording", StringComparison.Ordinal));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal("Text inserted. Hold Right Ctrl for your next sentence.", rig.View.Last.Status);
    }

    /// <summary>The cap's post has run: the hold read as released.</summary>
    private static bool TapSaidItsCap(Rig rig)
    {
        rig.Dispatcher.Drain();
        rig.Controller.Tick();
        return !rig.Platform.Microphone.Open;
    }

    // ─── The hands-free silence choice ──────────────────────────────────

    [Fact]
    public async Task The_silence_choice_is_shown_saved_and_read_back()
    {
        using (var rig = new Rig())
        {
            await rig.EnableAsync();
            Assert.Equal(DictationSilenceStop.After30Seconds, rig.View.Last.SilenceStop);
            Assert.Equal(
                ["After 30 seconds", "After 60 seconds", "Never"], rig.Controller.SilenceStopChoices.Select(c => c.Name).ToArray());

            rig.Controller.ChooseSilenceStop(DictationSilenceStop.After60Seconds);
            Assert.Equal(DictationSilenceStop.After60Seconds, rig.View.Last.SilenceStop);
            Assert.Equal(60, (int)JsonNode.Parse(File.ReadAllText(rig.Settings.Path))!["hands_free_silence_stop_seconds"]!);

            rig.Controller.ChooseSilenceStop(DictationSilenceStop.Off);
            Assert.Equal(0, (int)JsonNode.Parse(File.ReadAllText(rig.Settings.Path))!["hands_free_silence_stop_seconds"]!);
            Assert.Equal(DictationSilenceStop.Off, rig.Settings.Load().SilenceStop);
        }
    }

    [Theory]
    [InlineData("{\"hands_free_silence_stop_seconds\": 60}", DictationSilenceStop.After60Seconds)]
    [InlineData("{\"hands_free_silence_stop_seconds\": 0}", DictationSilenceStop.Off)]
    [InlineData("{\"hands_free_silence_stop_seconds\": 30}", DictationSilenceStop.After30Seconds)]
    [InlineData("{\"hands_free_silence_stop_seconds\": 45}", DictationSilenceStop.After30Seconds)]
    [InlineData("{\"hands_free_silence_stop_seconds\": \"never\"}", DictationSilenceStop.After30Seconds)]
    [InlineData("{\"enabled\": true}", DictationSilenceStop.After30Seconds)]
    public void A_settings_file_reads_its_silence_choice_or_the_default(string json, DictationSilenceStop expected)
    {
        var dir = Directory.CreateTempSubdirectory("dictation-silence-").FullName;
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, json);
            var store = new DictationSettingsStore(path, NullLogger<DictationSettingsStore>.Instance);

            Assert.Equal(expected, store.Load().SilenceStop);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A choice that could not be written lasts the run and says so,
    /// as the other choices do; a file this run could not read is never
    /// overwritten.</summary>
    [Fact]
    public async Task A_silence_choice_that_could_not_be_saved_says_so()
    {
        using var rig = new Rig(beforeStart: r =>
        {
            Directory.CreateDirectory(r.DataDirectory);
            File.WriteAllText(r.Settings.Path, "not json");
        });
        await rig.EnableAsync();

        rig.Controller.ChooseSilenceStop(DictationSilenceStop.After60Seconds);

        Assert.Equal(DictationCopy.SilenceStopNotSaved, rig.View.Last.Status);
        Assert.Equal(DictationSilenceStop.After60Seconds, rig.View.Last.SilenceStop);
        Assert.Equal("not json", File.ReadAllText(rig.Settings.Path));
    }

    [Fact]
    public async Task Choosing_the_same_silence_again_writes_nothing()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var renders = rig.View.States.Count;

        rig.Controller.ChooseSilenceStop(DictationSilenceStop.After30Seconds);
        rig.Controller.ChooseSilenceStop((DictationSilenceStop)42);

        Assert.Equal(renders, rig.View.States.Count);
        Assert.Equal(DictationSilenceStop.After30Seconds, rig.View.Last.SilenceStop);
    }
}
