using Dictation.Services;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationAttemptLogTests;
using static Dictation.Core.Tests.DictationAttemptWiringTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The hands-free latch with the Windows features that landed beside it, on
/// the real chain (<see cref="HandsFree"/>): the attempt receipt through the
/// Mac's latch paths, the level bars under the hands-free clock, and the
/// recent result and Copy last text.
/// </summary>
public sealed class HandsFreeIntegrationTests
{
    /// <summary>A hands-free rig whose log is kept, with words and a target
    /// distinctive enough to be caught if either reaches a receipt.</summary>
    private static (HandsFree Hands, MemoryLog Log) Logged(
        bool checks = false,
        Func<DictationControllerOptions, DictationControllerOptions>? options = null)
    {
        var log = new MemoryLog();
        var hands = new HandsFree(options, checks, log);
        hands.Rig.Platform.Worker.Transcript = Words;
        hands.Rig.Platform.Target.Next = FocusedApplicationResult.Resolved("secretwriter", 7, "Secret Plans - Editor");
        return (hands, log);
    }

    private static List<string> Events(MemoryLog log) => log.Receipts.Select(Event).ToList();

    /// <summary>A held dictation, released to transcription: longer than the
    /// pair window, so its key-up is its release.</summary>
    private static void Hold(HandsFree hands)
    {
        hands.Press();
        hands.Speak();
        hands.Advance(500);
        hands.Release();
    }

    // ─── The attempt receipt ────────────────────────────────────────────
    //
    // The Mac (DictationModel) makes one attempt at the first press
    // (holdStarted), and marks its release where the hold is let go to
    // transcription (finishRecording): the latch's end, or a quick tap's
    // settle, never the pair's own key-ups. The Windows hook keeps such a hold
    // reading down until then, so the push-to-talk release the receipt
    // follows is that same moment.

    /// <summary>The pair's first key-up, and the second press that takes the
    /// parked hold over, are neither a release nor a new attempt.</summary>
    [Fact]
    public async Task A_latched_hold_is_one_attempt_from_the_first_press_released_at_the_stop_press()
    {
        var (hands, log) = Logged();
        using var _ = hands;
        await hands.EnableAsync();

        hands.DoubleTap();   // presses at 0 and 160 ms, key-ups at 60 and 220
        Assert.True(hands.Latched && hands.Listening);
        hands.Wait(1_000);
        Assert.Equal(["started"], Events(log));

        hands.Press();       // the stop, 1 220 ms after the first press
        Assert.Equal(["started", "released"], Events(log).Take(2));   // the ending may follow on the pipeline's thread
        hands.DrainUntil(() => hands.Inserted);
        hands.Release();     // the stop press's own key-up starts nothing
        AwaitEndings(hands.Rig, log);

        Assert.Equal(["started", "released", "finished"], Events(log));
        var ending = Endings(log).Single();
        Assert.Contains("result=inserted", ending);
        Assert.EndsWith("elapsed_ms=1220 release_ms=0", ending);
        AssertWellFormed(log);
    }

    /// <summary>A quick tap waits out the pair window before it is
    /// transcribed, so its release is the window's end, as the Mac's settle
    /// calls finishRecording, not its key-up.</summary>
    [Fact]
    public async Task A_lone_quick_tap_is_released_when_its_pair_window_runs_out_not_at_its_key_up()
    {
        var (hands, log) = Logged();
        using var _ = hands;
        await hands.EnableAsync();

        hands.Tap();         // key-up 60 ms after the press
        Assert.Equal(["started"], Events(log));
        hands.Wait(HoldKeyState.HandsFreeWindowMs);
        Assert.Equal(["started", "released"], Events(log).Take(2));   // the ending may follow on the pipeline's thread
        hands.DrainUntil(() => hands.Inserted);
        AwaitEndings(hands.Rig, log);

        Assert.Contains("result=inserted", Endings(log).Single());
        Assert.EndsWith("elapsed_ms=410 release_ms=0", Endings(log).Single());   // the first poll past 350 ms
        AssertWellFormed(log);
    }

    [Fact]
    public async Task Space_ending_a_latch_is_its_release_and_the_swallowed_key_is_no_attempt()
    {
        var (hands, log) = Logged();
        using var _ = hands;
        await hands.EnableAsync();
        hands.DoubleTap();
        hands.Wait(1_000);

        Assert.Equal(
            HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow,
            hands.Event(HoldKeyState.VkSpace, HandsFree.Down));
        Assert.Equal(["started", "released"], Events(log).Take(2));   // the ending may follow on the pipeline's thread
        hands.Event(HoldKeyState.VkSpace, HandsFree.Up);
        hands.DrainUntil(() => hands.Inserted);
        AwaitEndings(hands.Rig, log);

        Assert.Equal(["started", "released", "finished"], Events(log));
        Assert.Contains("result=inserted", Endings(log).Single());
        Assert.EndsWith("elapsed_ms=1220 release_ms=0", Endings(log).Single());
        AssertWellFormed(log);
    }

    /// <summary>The limit is measured from the first press, as the hold
    /// is, and ends the latch as a release would (the Mac's ceiling calls
    /// finishRecording): one release, one ending, the words typed. The
    /// ending names the stop, which the Mac's has no field for.</summary>
    [Fact]
    public async Task The_limit_ends_a_latch_with_its_release_and_one_ending()
    {
        var (hands, log) = Logged();
        using var _ = hands;
        await hands.EnableAsync();
        hands.DoubleTap();

        hands.Wait(300_000 - 220);
        Assert.False(hands.Latched);
        Assert.Equal(["started", "released"], Events(log).Take(2));   // the ending may follow on the pipeline's thread
        hands.DrainUntil(() => hands.Inserted);
        AwaitEndings(hands.Rig, log);

        Assert.Equal(["started", "released", "finished"], Events(log));
        Assert.Contains("result=inserted", Endings(log).Single());
        Assert.EndsWith("elapsed_ms=300000 release_ms=0 stop=recording_limit", Endings(log).Single());
        AssertWellFormed(log);
    }

    /// <summary>The Mac discards without a release (DictationModel.discard):
    /// one ending, cancelled, with no release time; the hold's end that the
    /// poll sees after the cancel is not one.</summary>
    [Fact]
    public async Task Escape_during_a_latch_is_one_cancelled_ending_with_no_release()
    {
        var (hands, log) = Logged();
        using var _ = hands;
        await hands.EnableAsync();
        hands.DoubleTap();
        hands.Wait(1_000);

        hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
        hands.Event(HoldKeyState.VkEscape, HandsFree.Up);
        hands.DrainUntilEnded();
        AwaitEndings(hands.Rig, log);

        Assert.Equal(["started", "finished"], Events(log));
        Assert.Contains("result=cancelled", Endings(log).Single());
        Assert.EndsWith("release_ms=n/a", Endings(log).Single());
        Assert.Equal(0, hands.Pastes);
        AssertWellFormed(log);
    }

    /// <summary>A double-tap while the last dictation goes in: each press
    /// the controller sees is refused out loud and is its own attempt, as a
    /// held press is, and no latch is left for the net to find. The first
    /// dictation keeps its own ending.</summary>
    [Fact]
    public async Task A_double_tap_while_the_last_dictation_goes_in_is_refused_press_by_press_and_leaves_no_latch()
    {
        var (hands, log) = Logged();
        using var _ = hands;
        using var decode = new ManualResetEventSlim();
        hands.Rig.Platform.Worker.HoldTranscribe = decode;
        await hands.EnableAsync();
        Hold(hands);
        hands.DrainUntil(() => hands.Status == DictationCopy.Transcribing);

        hands.DoubleTap();
        Assert.False(hands.Latched);
        Assert.StartsWith("Still typing", hands.Status, StringComparison.Ordinal);
        decode.Set();
        hands.DrainUntil(() => hands.Inserted);
        AwaitEndings(hands.Rig, log, 3);

        var endings = Endings(log);
        Assert.Equal(2, endings.Count(e => e.Contains("result=still_finishing", StringComparison.Ordinal)));
        Assert.Contains("result=inserted", endings[^1]);
        Assert.Equal(3, log.Receipts.Select(Attempt).Distinct().Count());
        Assert.DoesNotContain(log.Lines, l => l.Contains("no dictation behind it", StringComparison.Ordinal));
        Assert.Equal(1, hands.Rig.Platform.Microphone.Starts);
        AssertWellFormed(log);
    }

    /// <summary>A double-tap into a password field whose check is still out
    /// when the second press lands: the pair takes the first press over, so
    /// it is one attempt, refused once, and the latch ends with the refusal,
    /// before the next poll.</summary>
    [Fact]
    public async Task A_double_tap_into_a_password_field_is_one_privacy_refused_attempt_that_ends_the_latch()
    {
        var (hands, log) = Logged(checks: true);
        using var _ = hands;
        await hands.EnableAsync();
        hands.DoubleTap();
        Assert.True(hands.Latched);   // kept while its press waits for the check
        Assert.Equal(["started"], Events(log));

        hands.Checks.Answer(SecureFieldState.Secure);
        hands.Rig.Dispatcher.Drain();

        Assert.False(hands.Latched);
        Assert.Equal(DictationCopy.SecureFieldAtPress, hands.Status);
        hands.Wait(30);
        Assert.Equal(["started", "finished"], Events(log));
        Assert.Contains("result=privacy_refused", Endings(log).Single());
        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);
        AssertWellFormed(log);
    }

    /// <summary>Windows opens the microphone only once the press-time check
    /// answers (DESIGN.md, the latch's departures), so a latch stopped
    /// before then recorded nothing: released at the stop, ended as a
    /// key-up before capture.</summary>
    [Fact]
    public async Task A_latch_stopped_before_the_password_check_answers_ends_released_before_capture()
    {
        var (hands, log) = Logged(checks: true);
        using var _ = hands;
        await hands.EnableAsync();
        hands.DoubleTap();
        hands.Wait(500);

        hands.Press();       // the stop
        Assert.False(hands.Latched);
        Assert.Equal(["started", "released"], Events(log));
        hands.Checks.Answer();
        hands.Wait(30);
        hands.Release();

        Assert.Equal(["started", "released", "finished"], Events(log));
        Assert.Contains("result=failed stage=capture reason=released_before_capture", Endings(log).Single());
        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);
        Assert.Equal(0, hands.Transcribes);
        AssertWellFormed(log);
    }

    // ─── The level bars under the hands-free clock ──────────────────────
    //
    // A latched hold is listening, so the meter runs as it does for a held
    // one (DictationController.Level.cs follows the session's state). The
    // hands-free pill is the listening pill with a clock: the bars stay in
    // its mark slot, which the pill's chrome reserves beside the text column
    // (DictationHudGeometry.Chrome), and the window puts the dot back on
    // every show, so each second's redraw must draw the bars again.

    /// <summary>The feed on its own: the hands-free pill meters, and each
    /// second's redraw draws the bars again at once, from the current level;
    /// Transcribing puts the dot back.</summary>
    [Fact]
    public void The_hands_free_pill_keeps_the_bars_and_draws_them_again_with_each_second()
    {
        var hud = new FakeHud();
        var clock = new FakeClock();
        using var feed = new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold);
        feed.State(DictationState.Listening);
        feed.Level(0.4f);

        feed.Listening(latched: true, TimeSpan.Zero);
        Assert.StartsWith("Listening — 0:00", hud.Current!.Text, StringComparison.Ordinal);
        Assert.True(hud.Bars);
        Assert.Equal(0.4f, hud.Levels[^1]);
        feed.Level(0.6f);
        Assert.Equal(0.6f, hud.Levels[^1]);

        feed.Listening(latched: true, TimeSpan.FromSeconds(1));
        Assert.StartsWith("Listening — 0:01", hud.Current!.Text, StringComparison.Ordinal);
        Assert.True(hud.Bars);
        Assert.Equal(0.6f, hud.Levels[^1]);

        clock.Advance(DictationHudPacer.VisibleDwell);
        feed.State(DictationState.Transcribing);
        var drawn = hud.Levels.Count;
        feed.Level(0.9f);
        Assert.Equal(drawn, hud.Levels.Count);
        Assert.False(hud.Bars);
    }

    [Fact]
    public async Task The_level_bars_keep_moving_beside_the_hands_free_clock()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        var hud = hands.Rig.Hud;
        var microphone = hands.Rig.Platform.Microphone;
        var armed = microphone.Listeners;
        var sample = (int)DictationHudLevel.SampleInterval.TotalMilliseconds;

        hands.DoubleTap();
        Assert.StartsWith("Listening — 0:00", hud.Current!.Text, StringComparison.Ordinal);
        Assert.Equal(armed + 2, microphone.Listeners);   // one meter and one silence tracker for the hold

        microphone.Buffer(0.8f);
        hands.Wait(sample);
        Assert.True(hud.Levels[^1] > 0.7f, $"got {hud.Levels[^1]}");
        Assert.True(hud.Bars);

        // The clock's next second redraws the pill, and the bars with it.
        var shows = hud.Shows.Count;
        hands.Wait(1_000);
        Assert.True(hud.Shows.Count > shows);
        Assert.StartsWith("Listening — 0:01", hud.Current!.Text, StringComparison.Ordinal);
        Assert.True(hud.Bars);
        microphone.Buffer(0.1f);
        hands.Wait(sample);
        Assert.InRange(hud.Levels[^1], 0.0001f, 0.6999f);
        Assert.True(hud.Bars);

        // Stopped: the dot comes back and the meter lets go of the microphone.
        hands.Event(HoldKeyState.VkSpace, HandsFree.Down);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Up);
        hands.DrainUntil(() => hands.Inserted);
        Assert.False(hud.Bars);
        Assert.Equal(0, hud.Levels[^1]);
        Assert.Equal(armed, microphone.Listeners);
    }

    /// <summary>LEVEL's guard for a window that cannot draw a level (tried
    /// once, not twelve times a second) holds through a latch, whose pill is
    /// shown again every second: once for the hold. The next hold tries
    /// again.</summary>
    [Fact]
    public async Task A_window_that_cannot_draw_a_level_is_tried_once_per_hold_through_a_latch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        var hud = hands.Rig.Hud;
        hud.Throws = true;

        hands.DoubleTap();
        for (var second = 0; second < 5; second++)
        {
            hands.Rig.Platform.Microphone.Buffer(0.5f);
            hands.Wait(1_000);
        }

        Assert.Equal(1, hud.LevelAttempts);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Down);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Up);
        hands.DrainUntil(() => hands.Inserted);

        hud.Throws = false;
        Hold(hands);
        hands.DrainUntil(() => hands.Inserted && hands.Pastes == 2);
        Assert.True(hud.LevelAttempts > 1);
    }

    // ─── The recent result and Copy last text ───────────────────────────

    /// <summary>A latched dictation's words are held like a held one's, and
    /// a copy of them is logged under the latched attempt's id.</summary>
    [Fact]
    public async Task A_latched_dictation_is_kept_for_Copy_last_text_and_its_copy_is_under_its_attempt()
    {
        var (hands, log) = Logged();
        using var _ = hands;
        await hands.EnableAsync();
        Assert.Null(hands.Rig.View.Last.Recent);

        hands.DoubleTap();
        hands.Wait(1_000);
        hands.Event(HoldKeyState.VkReturn, HandsFree.Down);
        hands.Event(HoldKeyState.VkReturn, HandsFree.Up);
        hands.DrainUntil(() => hands.Inserted);

        Assert.True(hands.Rig.Controller.HoldsRecentText);
        var recent = Assert.IsType<DictationRecentView>(hands.Rig.View.Last.Recent);
        Assert.Equal(hands.Rig.Platform.Clipboard.Writes[0], recent.Text);
        Assert.True(recent.CanCopy);

        hands.Rig.Controller.CopyLastText();
        hands.DrainUntil(() => hands.Rig.View.Last.Recent?.CopyMessage == DictationCopy.Copied);
        var latched = Attempt(log.Receipts[0]);
        Assert.Equal(1, log.Receipts.Select(Attempt).Distinct().Count());
        Assert.Contains(log.Receipts, l => Attempt(l) == latched && l.EndsWith("event=copy result=copied", StringComparison.Ordinal));
        AssertWellFormed(log);
    }

    /// <summary>A latch is listening, so Copy last text waits for it, as for
    /// a held hold: in the window, in the menu and from a direct call.</summary>
    [Fact]
    public async Task Copy_last_text_waits_while_a_latch_listens()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        Hold(hands);
        hands.DrainUntil(() => hands.Inserted);
        var writes = hands.Rig.Platform.Clipboard.Writes.Count;

        hands.DoubleTap();
        Assert.True(hands.Latched && hands.Listening);
        Assert.False(hands.Rig.View.Last.Recent!.CanCopy);
        Assert.False(hands.Rig.View.Last.Tray.Recent!.CanCopy);
        hands.Rig.Controller.CopyLastText();
        Assert.Equal(writes, hands.Rig.Platform.Clipboard.Writes.Count);
        Assert.False(hands.Rig.View.Last.Recent!.Copying);

        hands.Event(HoldKeyState.VkSpace, HandsFree.Down);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Up);
        hands.DrainUntil(() => hands.Inserted && hands.Pastes == 2);
        Assert.True(hands.Rig.View.Last.Recent!.CanCopy);
    }

    /// <summary>
    /// RECENT refuses a press while a copy writes the clipboard
    /// (<c>still_copying</c>). A double-tap then is two presses, and each is
    /// refused out loud and is its own attempt, as a double-tap during the
    /// last dictation is; the refusal ends the gesture as every other
    /// refusal does, so no latch is left for the net to find, and the next
    /// double-tap after the copy latches.
    /// </summary>
    [Fact]
    public async Task A_double_tap_during_a_copy_is_refused_press_by_press_and_leaves_no_latch()
    {
        var copy = new RecentResultTests.HeldCopy();
        var (hands, log) = Logged(options: o => o with { RunClipboardCopy = copy.Run });
        using var _ = hands;
        await hands.EnableAsync();
        Hold(hands);
        hands.DrainUntil(() => hands.Inserted);
        var before = hands.Status;
        hands.Rig.Controller.CopyLastText();
        Assert.True(hands.Rig.View.Last.Recent!.Copying);
        var starts = hands.Rig.Platform.Microphone.Starts;

        hands.DoubleTap();
        Assert.False(hands.Latched);
        Assert.Equal(DictationCopy.StillCopyingLastText(hands.Rig.Controller.HoldKey), hands.Status);
        Assert.Equal(DictationCopy.HudStillCopying, hands.Rig.Hud.Current?.Text);
        hands.Wait(30);
        Assert.Equal(starts, hands.Rig.Platform.Microphone.Starts);
        Assert.Equal(2, Endings(log).Count(e => e.Contains("result=still_copying", StringComparison.Ordinal)));
        Assert.DoesNotContain(log.Lines, l => l.Contains("no dictation behind it", StringComparison.Ordinal));

        RecentResultTests.Answer(hands.Rig, copy);
        Assert.Equal(DictationCopy.Copied, hands.Rig.View.Last.Recent!.CopyMessage);
        Assert.Equal(before, hands.Status);   // the copy's end puts the line back

        hands.DoubleTap();
        Assert.True(hands.Latched && hands.Listening);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Down);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Up);
        hands.DrainUntil(() => hands.Inserted && hands.Pastes == 2);
        AwaitEndings(hands.Rig, log, 4);
        AssertWellFormed(log);
    }
}
