using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The Windows half of the hands-free latch beyond the Mac's scenarios
/// (<see cref="LatchTests"/>): that the latch ends on every path that ends a
/// hold without a key edge, at once; the net under those; the window's and
/// the indicator's words; the notification-area Cancel; and the tap floor.
/// </summary>
public sealed class HandsFreeTests
{
    [Fact]
    public async Task The_controller_arms_the_hands_free_gesture_and_says_how_to_use_it()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        Assert.True(hands.Key.HandsFree);
        Assert.True(hands.Key.CancelOnChord);
        Assert.Equal(
            "Speak wherever you type. Hold Right Ctrl, speak, then release to insert. Double-tap it to keep listening.",
            hands.Rig.View.Last.Instructions);
    }

    /// <summary>The default tap options keep a 300 ms floor; the hold
    /// policy's have none, as on the Mac, so a lone quick tap whose audio is
    /// shorter than that is still transcribed.</summary>
    [Fact]
    public async Task A_lone_quick_tap_is_transcribed_however_little_audio_it_caught()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(300), MicrophoneTapOptions.Default.MinimumHold);
        Assert.Equal(TimeSpan.Zero, DictationHoldPolicy.Windows.TapOptions.MinimumHold);
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.Press();
        hands.Rig.Platform.Microphone.Speak(TimeSpan.FromMilliseconds(200));
        hands.Advance(60);
        hands.Release();
        hands.Wait(HoldKeyState.HandsFreeWindowMs);
        hands.DrainUntilEnded();

        Assert.True(hands.Inserted, hands.Status);
    }

    // ─── Every early end ends the latch, at once ────────────────────────

    /// <summary>
    /// The net: a latch whose hold is not listening, and whose press is not
    /// waiting for the password-field check, is ended at the next tick,
    /// whatever left it there. Driven by hand, because every path the
    /// controller knows ends its own; the shape is the one that can still
    /// slip past them: a press refused while the last dictation goes in,
    /// then the pair completing in the hook before the next poll, so the key
    /// never reads up in between and no new press is seen.
    /// </summary>
    [Fact]
    public async Task A_latch_with_no_dictation_behind_it_is_ended_at_the_next_tick()
    {
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);

        rig.Press();
        Assert.StartsWith("Still typing", rig.View.Last.Status, StringComparison.Ordinal);
        var ends = rig.Platform.Trigger.EndLatchCalls;
        rig.Platform.Trigger.Latched = true;
        rig.Controller.Tick();

        Assert.False(rig.Platform.Trigger.IsLatched);
        Assert.Equal(ends + 1, rig.Platform.Trigger.EndLatchCalls);
        decode.Set();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task A_latch_waiting_for_the_password_field_check_is_kept()
    {
        using var hands = new HandsFree(checks: true);
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Wait(30);

        Assert.True(hands.Latched);
        hands.Checks.Answer();
        hands.Wait(30);
        Assert.True(hands.Latched && hands.Listening);
    }

    [Fact]
    public async Task Cancel_ends_the_latch_at_once()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.DoubleTap();

        hands.Rig.Controller.Cancel();

        // Before any tick: Space types again from the moment Cancel is chosen.
        Assert.False(hands.Latched);
        Assert.Equal(HoldKeyTransition.None, hands.Key.KeyEvent(HoldKeyState.VkSpace, HandsFree.Down));
        hands.DrainUntil(() => hands.Status.Contains("Cancelled", StringComparison.Ordinal));
        Assert.Equal(0, hands.Pastes);
    }

    [Fact]
    public async Task The_limit_ends_the_latch_in_the_tick_that_reaches_it()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-latch-log-").FullName;
        try
        {
            using (var logs = FileLoggerFactory.TryCreate(dir, out _)!)
            {
                using var hands = new HandsFree(logs: logs);
                await hands.EnableAsync();
                hands.DoubleTap();

                hands.Advance(300_000);
                hands.Rig.Controller.Tick();

                Assert.False(hands.Latched);
                hands.DrainUntil(() => hands.Inserted);
            }

            // Said by the limit itself, not by the net a tick later; and
            // never the words.
            var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
            Assert.Contains("Dictation latched hands-free", log, StringComparison.Ordinal);
            Assert.Contains("Hands-free dictation ended: the recording limit", log, StringComparison.Ordinal);
            Assert.DoesNotContain("had no dictation behind it", log, StringComparison.Ordinal);
            Assert.DoesNotContain("hello world", log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A key ends a latch inside the hook, before the controller
    /// looks; the log still says which: the hold key by name, a stop key as
    /// one, Escape. Never which stop key, never the words.</summary>
    [Fact]
    public async Task The_log_says_which_key_ended_a_latch()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-latch-end-").FullName;
        try
        {
            using (var logs = FileLoggerFactory.TryCreate(dir, out _)!)
            {
                using var hands = new HandsFree(logs: logs);
                await hands.EnableAsync();

                hands.DoubleTap();
                hands.Event(HoldKeyState.VkSpace, HandsFree.Down);
                hands.Event(HoldKeyState.VkSpace, HandsFree.Up);
                hands.DrainUntil(() => hands.Inserted && hands.Pastes == 1);

                hands.Wait(1_000);
                hands.DoubleTap();
                hands.Wait(1_000);
                hands.Tap();
                hands.DrainUntil(() => hands.Inserted && hands.Pastes == 2);

                hands.Wait(1_000);
                hands.DoubleTap();
                hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
                hands.DrainUntil(() => hands.Status.Contains("Cancelled", StringComparison.Ordinal));
            }

            var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
            Assert.Contains("Hands-free dictation ended: a stop key (Space or Enter) was pressed", log, StringComparison.Ordinal);
            Assert.Contains("Hands-free dictation ended: Right Ctrl was pressed", log, StringComparison.Ordinal);
            Assert.Contains("Hands-free dictation ended: Escape was pressed", log, StringComparison.Ordinal);
            Assert.Contains("Dictation latched hands-free; Right Ctrl, Space or Enter stops it", log, StringComparison.Ordinal);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(log, "Hands-free dictation ended: Escape"));
            Assert.DoesNotContain("had no dictation behind it", log, StringComparison.Ordinal);
            Assert.DoesNotContain("hello world", log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_refused_press_ends_the_latch_it_completed()
    {
        using var hands = new HandsFree();
        using var decode = new ManualResetEventSlim();
        hands.Rig.Platform.Worker.HoldTranscribe = decode;
        await hands.EnableAsync();
        hands.Press();
        hands.Speak();
        hands.Advance(400);
        hands.Release();
        hands.DrainUntil(() => hands.Status == DictationCopy.Transcribing);
        var ends = hands.Key.EndLatchCalls;

        hands.DoubleTap();

        Assert.False(hands.Latched);
        Assert.True(hands.Key.EndLatchCalls >= ends + 2, "each refused press ended what it began");
        decode.Set();
        hands.DrainUntil(() => hands.Inserted);
    }

    /// <summary>A latch torn down. The internal Pause, called directly: the window and
    /// the menu refuse a pause under a dictation (TryPause), so this pins the
    /// teardown of a live hold that quitting and a failure share.</summary>
    [Fact]
    public async Task The_pause_teardown_ends_a_latch_and_frees_the_stop_keys()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.DoubleTap();
        var trigger = hands.Key;

        hands.Rig.Controller.Pause();

        Assert.False(trigger.IsLatched);
        Assert.False(hands.Listening);
        Assert.Equal(HoldKeyTransition.None, trigger.KeyEvent(HoldKeyState.VkSpace, HandsFree.Down));
    }

    // ─── What the window, the indicator and the tray say ───────────────

    /// <summary>The clock and the countdown, with nobody speaking: without
    /// silence stops, which would end the latch at 15 s (HoldPolicyTests).</summary>
    [Fact]
    public async Task The_hands_free_clock_is_silent_and_counts_down_in_the_last_minute()
    {
        using var hands = new HandsFree(options: o => o with { HoldPolicy = o.HoldPolicy.WithoutSilenceStops() });
        await hands.EnableAsync();
        var hud = hands.Rig.Hud;

        hands.DoubleTap();
        Assert.Equal("Listening — 0:00 · Press Right Ctrl, Space or Enter to stop", hud.Current!.Text);
        Assert.Equal(DictationCopy.HudBusyLines("Right Ctrl", DictationHoldPolicy.Windows), hud.Current.SizedFor);
        Assert.Null(hud.Current.Dwell);

        hands.Wait(1_000);
        Assert.Equal("Listening — 0:01 · Press Right Ctrl, Space or Enter to stop", hud.Current!.Text);
        var shows = hud.Shows.Count;
        hands.Wait(30);
        Assert.Equal(shows, hud.Shows.Count);   // redrawn once a second, not once a tick

        hands.Wait(239_000 - 1_030);
        Assert.Equal("Listening — 3:59 · Press Right Ctrl, Space or Enter to stop", hud.Current!.Text);
        hands.Wait(1_000);
        Assert.Equal("Listening — Stops in 1:00 · Press Right Ctrl, Space or Enter to stop", hud.Current!.Text);
        hands.Wait(59_500);
        Assert.Equal("Listening — Stops in 0:01 · Press Right Ctrl, Space or Enter to stop", hud.Current!.Text);

        // Nothing is spoken while the microphone is open.
        var latched = hud.Shows.Select((model, i) => (model, i))
            .Where(s => s.model.Text.StartsWith("Listening —", StringComparison.Ordinal))
            .ToArray();
        Assert.True(latched.Length >= 5);
        Assert.All(latched, s => Assert.False(hud.Announced[s.i]));
    }

    [Fact]
    public async Task A_queued_listening_repaint_keeps_the_latched_line_and_clock()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        // Both taps before the dispatcher runs the session's Listening repaint.
        foreach (var (key, message, wait) in new[]
        {
            (HandsFree.Trigger, HandsFree.Down, 60), (HandsFree.Trigger, HandsFree.Up, 100),
            (HandsFree.Trigger, HandsFree.Down, 60), (HandsFree.Trigger, HandsFree.Up, 0),
        })
        {
            hands.Key.KeyEvent(key, message);
            hands.Rig.Controller.Tick();
            hands.Advance(wait);
        }

        hands.Rig.Dispatcher.Drain();

        Assert.Equal(DictationCopy.Latched(hands.Rig.Platform.Trigger.Binding), hands.Status);
        Assert.StartsWith("Listening — 0:00 · Press Right Ctrl", hands.Rig.Hud.Current!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_line_goes_from_listening_to_latched_to_transcribing()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        using var decode = new ManualResetEventSlim();
        hands.Press();
        hands.Speak();
        Assert.Equal("Listening… Release Right Ctrl to insert.", hands.Status);
        hands.Advance(60);
        hands.Release();
        hands.Advance(100);
        hands.Press();
        Assert.Equal("Dictating. Press Right Ctrl, Space or Enter to stop.", hands.Status);

        hands.Rig.Platform.Worker.HoldTranscribe = decode;
        hands.Wait(1_000);
        hands.Release();
        hands.Wait(1_000);
        hands.Press();
        hands.DrainUntil(() => hands.Status == DictationCopy.Transcribing);
        decode.Set();
        hands.DrainUntil(() => hands.Inserted);
    }

    /// <summary>The window's status line is shown, not announced, while the
    /// microphone is open, held or hands-free; the ending is announced.</summary>
    [Fact]
    public async Task The_window_says_the_microphone_is_open_while_listening_held_or_hands_free()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        Assert.False(hands.Rig.View.Last.MicrophoneOpen);

        hands.Press();
        hands.Speak();
        Assert.True(hands.Rig.View.Last.MicrophoneOpen);
        hands.Advance(60);
        hands.Release();
        hands.Advance(100);
        hands.Press();
        Assert.Equal(DictationCopy.Latched(hands.Key.Binding), hands.Status);
        Assert.True(hands.Rig.View.Last.MicrophoneOpen);

        hands.Wait(1_000);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Down);
        hands.DrainUntil(() => hands.Inserted);
        Assert.False(hands.Rig.View.Last.MicrophoneOpen);
    }

    [Fact]
    public async Task The_notification_area_offers_Cancel_only_while_there_is_something_to_cancel()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        Assert.False(hands.Rig.View.Last.Tray.CanCancel);

        hands.DoubleTap();
        Assert.True(hands.Rig.View.Last.Tray.CanCancel);

        // The tray item calls the window's Cancel.
        hands.Rig.Controller.Cancel();
        hands.DrainUntil(() => hands.Status.Contains("Cancelled", StringComparison.Ordinal));
        Assert.False(hands.Rig.View.Last.Tray.CanCancel);
        Assert.False(hands.Latched);
    }
}
