using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

// LIMIT-WIN-B's stops through LIMIT-WIN-A's rule zero, "no stop loses words":
// the recording limit, the memory cap behind it, the hands-free silence stop,
// nothing heard since the latch and the held stuck-key stop each end the take
// as a release does, so its spool settles as a release's does. The words go
// in and the file goes, or the decode fails and the take is kept for Recover
// last dictation; the receipt names the stop either way. Every scenario runs
// the product's hold policy, silence stops included, over the real chain
// (HandsFree: the hook's gesture, the controller, the tap, the session, the
// worker's framing and the paste).

/// <summary>Each hold-policy stop, and what becomes of its take.</summary>
public sealed class HoldStopTakeTests
{
    /// <summary>How the app ends the hold.</summary>
    public enum Stop
    {
        /// <summary>Held five minutes, speaking every 30 s.</summary>
        Limit,

        /// <summary>Hands-free, speech, then 30 s of a quiet room.</summary>
        Silence,

        /// <summary>Held, speech, then a minute of a quiet room.</summary>
        StuckKey,

        /// <summary>Hands-free in a noisy room with a voice the silence
        /// tracker cannot hear, 15 s: the session's quiet-room check finds
        /// the voice, so it is a take with words, not an empty one.</summary>
        NothingHeard,
    }

    /// <summary>−35 dBFS of white noise: a fan, a café.</summary>
    private const float Noisy = 0.0178f;

    /// <summary>A voice 4 dB over <see cref="Noisy"/>, under the tracker's
    /// 10 dB margin.</summary>
    private const float SoftVoice = 0.04f;

    private static string[] Files(Rig rig) =>
        Directory.Exists(rig.RecoveryDirectory)
            ? [.. Directory.GetFiles(rig.RecoveryDirectory).Select(Path.GetFileName)!]
            : [];

    private static string Finished(MemoryLog log) =>
        Assert.Single(log.Receipts, l => l.Contains(" event=finished ", StringComparison.Ordinal));

    private static string Field(Stop stop) => stop switch
    {
        Stop.Limit => "stop=recording_limit",
        Stop.Silence => "stop=silence",
        Stop.StuckKey => "stop=stuck_key",
        _ => "stop=nothing_heard",
    };

    /// <summary>The window's sentence once a stop's words went in.</summary>
    private static string InsertedAfter(Stop stop) => stop switch
    {
        Stop.Limit => "Text inserted. A recording stops at five minutes; hold Right Ctrl again to continue.",
        Stop.Silence =>
            "Text inserted. Hands-free dictation stopped after 30 seconds without speech; double-tap Right Ctrl to keep going.",
        Stop.StuckKey =>
            "Text inserted. Recording stopped after a minute without speech; release Right Ctrl, then hold it again to keep going.",
        _ => "Text inserted. Hands-free dictation stopped after 15 seconds without speech; double-tap Right Ctrl to keep going.",
    };

    /// <summary>
    /// One second of audio, then the next poll: a quiet room, the voice, or
    /// for <see cref="Stop.NothingHeard"/> the soft voice in the noisy room.
    /// Returns whether the hold still listens.
    /// </summary>
    private static bool Second(HandsFree hands, Stop stop, bool talk)
    {
        var microphone = hands.Rig.Platform.Microphone;
        if (stop == Stop.NothingHeard)
        {
            microphone.Talk(TimeSpan.FromSeconds(1), Noisy, SoftVoice);
        }
        else if (talk)
        {
            microphone.Talk(TimeSpan.FromSeconds(1));
        }
        else
        {
            microphone.Room(TimeSpan.FromSeconds(1));
        }

        hands.Wait(1_000);
        return hands.Listening;
    }

    /// <summary>A double-tap with nothing said at either tap: the latch's
    /// audio is only what the test sends.</summary>
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

    /// <summary>
    /// Start the hold <paramref name="stop"/> needs and feed it, a second at
    /// a time, until the app ends it; asserts it was the app, on the second
    /// the policy names, with nothing sent to the worker before.
    /// </summary>
    private static void RunUntilTheAppStops(HandsFree hands, Stop stop)
    {
        int stopsAfter;
        switch (stop)
        {
            case Stop.Limit:
                hands.Press();
                hands.Wait(400);
                stopsAfter = 300;
                break;
            case Stop.StuckKey:
                hands.Press();
                hands.Wait(400);
                for (var second = 0; second < 3; second++)
                {
                    Second(hands, stop, talk: true);
                }

                stopsAfter = 60;
                break;
            case Stop.Silence:
                hands.DoubleTap();
                for (var second = 0; second < 3; second++)
                {
                    Second(hands, stop, talk: true);
                }

                stopsAfter = 30;
                break;
            default:
                SilentDoubleTap(hands);
                stopsAfter = 15;
                break;
        }

        Assert.True(hands.Listening, hands.Status);
        var seconds = 0;
        // Held to the limit, a word every 30 s keeps the stuck-key stop away.
        while (Second(hands, stop, talk: stop == Stop.Limit && seconds % 30 == 0))
        {
            seconds++;
            Assert.True(seconds < stopsAfter + 5, $"still listening after {seconds} s");
        }

        Assert.InRange(seconds + 1, stopsAfter - 1, stopsAfter + 1);
        Assert.Equal(0, hands.Pastes);
    }

    /// <summary>The kept file's length, as its header counts it.</summary>
    private static TimeSpan KeptLength(Rig rig)
    {
        var header = DictationTakeSpool.ReadHeader(Path.Combine(rig.RecoveryDirectory, DictationTakeSpool.KeptFileName));
        Assert.NotNull(header);
        return TimeSpan.FromSeconds(header!.Value.Samples / 16_000.0);
    }

    /// <summary>
    /// The stop's words go in, and the take's spool goes with them: nothing
    /// is kept, and the receipt ends as an insert that the app's stop
    /// ended, not as a release.
    /// </summary>
    [Theory]
    [InlineData(Stop.Limit)]
    [InlineData(Stop.Silence)]
    [InlineData(Stop.StuckKey)]
    [InlineData(Stop.NothingHeard)]
    public async Task A_stop_s_words_go_in_and_its_recording_is_deleted(Stop stop)
    {
        var log = new MemoryLog();
        using var hands = new HandsFree(logs: log);
        await hands.EnableAsync();

        RunUntilTheAppStops(hands, stop);
        // On disk until the words are in.
        Assert.Single(Files(hands.Rig));
        hands.DrainUntil(() => hands.Inserted);

        Assert.Equal(InsertedAfter(stop), hands.Status);
        Assert.Equal(1, hands.Pastes);
        Assert.Empty(Files(hands.Rig));
        Assert.Null(hands.Rig.View.Last.SavedTake);
        Assert.Null(hands.Rig.View.Last.Tray.SavedTake);
        var finished = Finished(log);
        Assert.Contains(" event=finished result=inserted ", finished, StringComparison.Ordinal);
        Assert.EndsWith(" " + Field(stop), finished, StringComparison.Ordinal);
        Assert.Single(log.Receipts, l => l.EndsWith(" event=released", StringComparison.Ordinal));
    }

    /// <summary>
    /// The stop's decode fails (the worker is alive and refuses it, so it is
    /// not retried): the take is kept for Recover last dictation, whole, as
    /// a release's would be, and Recover brings its words back. A stop the
    /// app made never throws a take away.
    /// </summary>
    [Theory]
    [InlineData(Stop.Limit)]
    [InlineData(Stop.Silence)]
    [InlineData(Stop.StuckKey)]
    [InlineData(Stop.NothingHeard)]
    public async Task A_stop_whose_decode_fails_keeps_the_take_for_recovery(Stop stop)
    {
        var log = new MemoryLog();
        using var hands = new HandsFree(logs: log);
        await hands.EnableAsync();
        var worker = hands.Rig.Platform.Worker;
        worker.RefuseTranscribe = true;

        RunUntilTheAppStops(hands, stop);
        hands.DrainUntil(() => hands.Status == DictationCopy.TakeSaved);

        var rig = hands.Rig;
        Assert.Equal(0, hands.Pastes);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        var length = KeptLength(rig);
        Assert.True(length >= TimeSpan.FromSeconds(15), $"{length} kept");
        Assert.Equal(DictationCopy.SavedTakeDetail(length), rig.View.Last.SavedTake!.Detail);
        Assert.NotNull(rig.View.Last.Tray.SavedTake);
        Assert.Equal(DictationCopy.HudTakeSaved, rig.Hud.Current!.Text);
        var finished = Finished(log);
        Assert.Contains(" event=finished result=failed stage=asr reason=recognition_failed ", finished, StringComparison.Ordinal);
        Assert.EndsWith(" " + Field(stop), finished, StringComparison.Ordinal);

        // Recovered, once the key is up and the worker answers again.
        if (hands.Key.IsKeyDown())
        {
            hands.Release();
        }

        worker.RefuseTranscribe = false;
        worker.Transcript = "the words the stop kept";
        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);

        Assert.Equal(DictationCopy.Recovered, hands.Status);
        Assert.Equal("the words the stop kept", rig.Platform.Clipboard.Text);
        Assert.Empty(Files(rig));
        var frame = worker.FramesOf("transcribe")[^1];
        Assert.Equal(
            (int)Math.Round(length.TotalSeconds * 16_000) * 2,
            Convert.FromBase64String(frame.GetProperty("pcm_b64").GetString()!).Length);
    }

    /// <summary>
    /// A five-minute take whose decode misses its deadline, and misses the
    /// retry's longer one too, is kept, not lost: the limit's long take is
    /// the one most likely to miss (a slower model, a slower PC).
    /// </summary>
    [Fact]
    public async Task A_take_the_limit_ended_whose_decode_misses_its_deadline_twice_is_kept()
    {
        // Silence stops off: half a second of speech, then five minutes of a
        // microphone that sends nothing, keeps the decode's deadline short.
        using var rig = new Rig(options: o => o with
        {
            HoldPolicy = o.HoldPolicy.WithoutSilenceStops(),
            Takes = o.Takes with { DecodeDeadline = TimeSpan.FromSeconds(0.2) },
        });
        await rig.EnableAsync();
        rig.Platform.Worker.TranscribeDelays.Enqueue(null);
        rig.Platform.Worker.TranscribeDelays.Enqueue(null);
        rig.Press();
        rig.Dispatcher.Drain();

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.SavedTake is not null);

        Assert.True(rig.Platform.Trigger.Down);
        Assert.Contains(rig.View.States, s => s.Status == "Five minutes is the longest recording, so it stopped there. Transcribing…");
        Assert.Contains(rig.View.States, s => s.Status == DictationCopy.RetryingTranscription);
        Assert.Equal(2, rig.Platform.Worker.Ops().Count(op => op == "transcribe"));
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        Assert.Equal(DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(0.5)), rig.View.Last.SavedTake!.Detail);
        Assert.Contains(rig.View.States, s => s.Status == DictationCopy.TakeSavedWorkerRestarting);
        rig.Release();
    }

    /// <summary>
    /// The memory cap behind the limit (a UI thread stalled past it) ends
    /// the hold as the limit does, and a decode that fails keeps the take:
    /// the spool, which has no cap, holds everything the hold heard, the
    /// audio past the tap's cap included.
    /// </summary>
    [Fact]
    public async Task A_take_the_memory_cap_ended_whose_decode_fails_is_kept_whole()
    {
        var log = new MemoryLog();
        using var rig = new Rig(options: o => o with { HoldPolicy = o.HoldPolicy.WithoutSilenceStops() }, logs: log);
        await rig.EnableAsync();
        rig.Platform.Worker.RefuseTranscribe = true;
        rig.Press();
        rig.Dispatcher.Drain();

        // Six minutes and more, and no tick until the cap has been said.
        for (var second = 0; second < 365; second++)
        {
            rig.Platform.Microphone.Room(TimeSpan.FromSeconds(1));
        }

        // The cap's report ends the hold; the next poll sees it released.
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.TakeSaved);

        var sent = Convert.FromBase64String(
            Assert.Single(rig.Platform.Worker.FramesOf("transcribe")).GetProperty("pcm_b64").GetString()!).Length / 2;
        Assert.Equal(6 * 60 * 16_000, sent);
        Assert.Equal(TimeSpan.FromSeconds(365.5), KeptLength(rig));
        Assert.Equal(DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(365.5)), rig.View.Last.SavedTake!.Detail);
        Assert.EndsWith(" stop=recording_limit", Finished(log), StringComparison.Ordinal);
        rig.Release();
    }

    /// <summary>
    /// A latch nobody spoke into, in a quiet room, holds nothing worth
    /// keeping: the never-spoke stop's take is deleted with nothing sent to
    /// the worker, and the receipt says which stop it was.
    /// </summary>
    [Fact]
    public async Task A_silent_latch_s_take_holds_nothing_and_is_deleted()
    {
        var log = new MemoryLog();
        using var hands = new HandsFree(logs: log);
        await hands.EnableAsync();
        hands.Rig.Platform.Worker.RefuseTranscribe = true;
        SilentDoubleTap(hands);

        for (var second = 0; second < 15 && hands.Listening; second++)
        {
            hands.Rig.Platform.Microphone.Room(TimeSpan.FromSeconds(1));
            hands.Wait(1_000);
        }

        hands.DrainUntil(() => hands.Status.StartsWith("Nothing was heard for 15 seconds", StringComparison.Ordinal));

        Assert.Equal(0, hands.Transcribes);
        Assert.Empty(Files(hands.Rig));
        Assert.Null(hands.Rig.View.Last.SavedTake);
        var finished = Finished(log);
        Assert.Contains(" event=finished result=failed stage=asr reason=no_text ", finished, StringComparison.Ordinal);
        Assert.EndsWith(" stop=nothing_heard", finished, StringComparison.Ordinal);
    }

    /// <summary>A release is no stop: its receipt carries no stop field.</summary>
    [Fact]
    public async Task A_release_s_receipt_names_no_stop()
    {
        var log = new MemoryLog();
        using var hands = new HandsFree(logs: log);
        await hands.EnableAsync();
        hands.Press();
        hands.Wait(400);
        Second(hands, Stop.StuckKey, talk: true);
        hands.Release();
        hands.DrainUntil(() => hands.Inserted);

        Assert.DoesNotContain("stop=", Finished(log), StringComparison.Ordinal);
    }

    [Fact]
    public void Each_policy_stop_has_its_receipt_field()
    {
        Assert.Equal("stop=recording_limit", DictationAttemptReceipt.Stop(DictationHoldStop.Ceiling));
        Assert.Equal("stop=silence", DictationAttemptReceipt.Stop(DictationHoldStop.Silence));
        Assert.Equal("stop=nothing_heard", DictationAttemptReceipt.Stop(DictationHoldStop.NothingHeard));
        Assert.Equal("stop=stuck_key", DictationAttemptReceipt.Stop(DictationHoldStop.StuckKey));
        Assert.Equal(
            Enum.GetValues<DictationHoldStop>().Length,
            Enum.GetValues<DictationHoldStop>().Select(DictationAttemptReceipt.Stop).Distinct().Count());
    }
}
