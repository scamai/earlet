namespace Dictation.Services.Tests;

/// <summary>
/// <see cref="HoldKeyTriggerMode.HoldOrDoubleTapHandsFree"/>: the macOS
/// Dictation app's double-tap latch, decided in the pure state machine the
/// Windows hook runs. The first two tests port the Mac's
/// <c>LatchEdgeTests</c> and the stop-key table test ports
/// <c>LatchStopKeyTests</c> (macos/Tests/DictationAppCoreTests/LatchTests.swift);
/// the rest pin the edges of each rule.
/// </summary>
public sealed class HoldKeyHandsFreeTests
{
    private const int VkRightControl = 0xA3;
    private const int VkLeftControl = 0xA2;
    private const int VkRightAlt = 0xA5;
    private const int VkC = 0x43;
    private const int VkTab = 0x09;
    private const int VkLeftShift = 0xA0;
    private const int Down = HoldKeyState.WmKeyDown;
    private const int Up = HoldKeyState.WmKeyUp;
    private const int Window = HoldKeyState.HandsFreeWindowMs;

    private static HoldKeyState HandsFree(int trigger = VkRightControl) =>
        new(trigger, HoldKeyTriggerMode.HoldOrDoubleTapHandsFree, cancelOnChord: true);

    /// <summary>A press at <paramref name="at"/> and its release 60 ms later.</summary>
    private static (HoldKeyTransition Down, HoldKeyTransition Up) Tap(HoldKeyState state, long at, int key = VkRightControl) =>
        (state.Observe(key, Down, at), state.Observe(key, Up, at + 60));

    /// <summary>Two taps 160 ms apart, press to press: a latch.</summary>
    private static void Latch(HoldKeyState state, long at)
    {
        Tap(state, at);
        Tap(state, at + 160);
        Assert.True(state.IsLatched);
    }

    // ─── Ported from the Mac's LatchEdgeTests ───────────────────────────

    /// <summary>Mac <c>pairsLatchAndUnlatch</c>: "two quick pairs latch and
    /// unlatch, in hold-edge order". Here one capture runs from the first
    /// press to the third, and the fourth is the absorbed trailing tap.</summary>
    [Fact]
    public void PairsLatchAndUnlatch()
    {
        var state = HandsFree();

        // Pair one: the first press starts the hold, its release parks it,
        // the second press latches it with no new start.
        Assert.Equal((HoldKeyTransition.HoldStarted, HoldKeyTransition.None), Tap(state, 1_000));
        Assert.True(state.IsHeldAt(1_100));
        Assert.Equal((HoldKeyTransition.None, HoldKeyTransition.None), Tap(state, 1_160));
        Assert.True(state.IsLatched);
        Assert.True(state.IsHeldAt(60_000));

        // Pair two: the first press stops (the hold ends on the press), the
        // second starts nothing.
        Assert.Equal((HoldKeyTransition.HoldEnded, HoldKeyTransition.None), Tap(state, 5_000));
        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(5_001));
        Assert.Equal((HoldKeyTransition.None, HoldKeyTransition.None), Tap(state, 5_160));
        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(5_200));
        Assert.False(state.IsHeldAt(10_000));
    }

    /// <summary>Mac <c>loneTapAndOtherSide</c>: "a lone tap is only a short
    /// hold, and the other side never latches".</summary>
    [Fact]
    public void LoneTapAndOtherSide()
    {
        var state = HandsFree();

        Assert.Equal(HoldKeyTransition.HoldStarted, Tap(state, 1_000).Down);
        Assert.True(state.IsHeldAt(1_000 + Window), "the short hold waits out the window");
        Assert.False(state.IsHeldAt(1_000 + Window + 1), "and ends at press + window");

        // Past the window: the next press starts a new pair, not a latch.
        Assert.Equal(HoldKeyTransition.HoldStarted, Tap(state, 1_450).Down);
        Assert.False(state.IsLatched);

        // Two quick taps of LEFT Ctrl: a different key, so nothing at all.
        var other = HandsFree();
        Assert.Equal((HoldKeyTransition.None, HoldKeyTransition.None), Tap(other, 3_000, VkLeftControl));
        Assert.Equal((HoldKeyTransition.None, HoldKeyTransition.None), Tap(other, 3_100, VkLeftControl));
        Assert.False(other.IsLatched);
        Assert.False(other.IsHeldAt(3_200));
    }

    // ─── Ported from the Mac's LatchStopKeyTests ────────────────────────

    /// <summary>Mac <c>stopKeysAreConsumed</c>: "Space, Return and keypad
    /// Enter, down and up, are consumed; everything else passes". Keypad
    /// Enter is VK_RETURN with LLKHF_EXTENDED, so it is the same row.</summary>
    [Fact]
    public void StopKeysAreConsumed()
    {
        foreach (var stop in new[] { HoldKeyState.VkSpace, HoldKeyState.VkReturn })
        {
            var state = HandsFree();
            Latch(state, 1_000);

            Assert.Equal(HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow, state.Observe(stop, Down, 2_000));
            Assert.False(state.IsLatched);
            Assert.False(state.IsHeldAt(2_001));
            Assert.Equal(HoldKeyTransition.Swallow, state.Observe(stop, Up, 2_050));
        }

        foreach (var other in new[] { HoldKeyState.VkEscape, VkTab, VkC, VkLeftShift })
        {
            var state = HandsFree();
            Latch(state, 1_000);

            Assert.Equal(HoldKeyTransition.None, state.Observe(other, Down, 2_000) & HoldKeyTransition.Swallow);
            Assert.Equal(HoldKeyTransition.None, state.Observe(other, Up, 2_050) & HoldKeyTransition.Swallow);
            Assert.True(state.IsLatched, $"0x{other:X2} ended the latch");
        }
    }

    // ─── Arming ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Window, true)]
    [InlineData(Window + 1, false)]
    public void Two_presses_latch_when_no_more_than_the_window_apart_press_to_press(int gap, bool latches)
    {
        var state = HandsFree();

        // The first press is held almost the whole window, so the release to
        // the second press is short whichever way the pair goes: it is the
        // press-to-press interval that decides, as on the Mac.
        state.Observe(VkRightControl, Down, 1_000);
        state.Observe(VkRightControl, Up, 1_000 + Window - 20);
        var second = state.Observe(VkRightControl, Down, 1_000 + gap);

        Assert.Equal(latches, state.IsLatched);
        Assert.Equal(latches ? HoldKeyTransition.None : HoldKeyTransition.HoldStarted, second);
    }

    [Fact]
    public void Releases_are_not_part_of_the_pair()
    {
        var state = HandsFree();

        // Released at once, pressed again well inside a release-to-press
        // window but outside the press-to-press one: no latch.
        Tap(state, 1_000);
        state.Observe(VkRightControl, Down, 1_000 + Window + 30);

        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_reversed_clock_is_a_fresh_first_press_not_a_pair()
    {
        var state = HandsFree();
        Tap(state, 2_000);
        state.Observe(VkRightControl, Down, 1_900);

        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_shortcut_on_the_first_press_is_never_half_of_a_pair()
    {
        var state = HandsFree();

        state.Observe(VkRightControl, Down, 1_000);
        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(VkC, Down, 1_040));
        state.Observe(VkC, Up, 1_060);
        state.Observe(VkRightControl, Up, 1_080);

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 1_200));
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_chorded_first_press_is_never_half_of_a_pair_even_when_chords_do_not_cancel()
    {
        var state = new HoldKeyState(VkRightControl, HoldKeyTriggerMode.HoldOrDoubleTapHandsFree);

        state.Observe(VkRightControl, Down, 1_000);
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkC, Down, 1_040));
        state.Observe(VkC, Up, 1_060);
        state.Observe(VkRightControl, Up, 1_080);
        state.Observe(VkRightControl, Down, 1_200);

        Assert.False(state.IsLatched);
    }

    /// <summary>The AltGr guard: on a layout with AltGr, Windows presses Left
    /// Ctrl just before every Right Alt, so every Right Alt press is chorded
    /// from its first instant and can never be half of a pair.</summary>
    [Fact]
    public void An_AltGr_press_never_latches()
    {
        var state = HandsFree(VkRightAlt);

        for (var at = 1_000L; at < 1_400; at += 150)
        {
            state.Observe(VkLeftControl, Down, at - 2);
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, at);
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, at + 40);
            state.Observe(VkLeftControl, Up, at + 41);
        }

        Assert.False(state.IsLatched);
    }

    /// <summary>The other half of the guard: a clean first press, then a
    /// second press that is an AltGr press (Left Ctrl synthesised just before
    /// it) is typing, and does not complete the pair.</summary>
    [Fact]
    public void A_press_chorded_from_its_start_never_completes_a_pair()
    {
        var state = HandsFree(VkRightAlt);

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, 1_000);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, 1_050);
        state.Observe(VkLeftControl, Down, 1_158);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, 1_160);

        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_bare_Right_Alt_on_a_layout_without_AltGr_latches()
    {
        var state = HandsFree(VkRightAlt);

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, 1_000);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, 1_050);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, 1_150);

        Assert.True(state.IsLatched);
    }

    // ─── Waiting out a short hold ───────────────────────────────────────

    [Fact]
    public void A_release_at_or_after_the_window_ends_the_hold_on_the_release()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 1_000);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkRightControl, Up, 1_000 + Window));
        Assert.False(state.IsHeldAt(1_000 + Window));
    }

    [Fact]
    public void A_release_sooner_than_the_window_parks_the_hold_until_press_plus_window()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 1_000);

        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 1_000 + Window - 1));
        Assert.True(state.IsHeldAt(1_000 + Window));
        Assert.False(state.IsHeldAt(1_000 + Window + 1));
    }

    // ─── While latched ──────────────────────────────────────────────────

    [Fact]
    public void One_press_stops_a_latch_on_the_press_and_its_release_releases_nothing()
    {
        var state = HandsFree();
        Latch(state, 1_000);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkRightControl, Down, 9_000));
        Assert.False(state.IsHeldAt(9_001));
        // Held a long while: still nothing to release, and no park.
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 12_000));
        Assert.False(state.IsHeldAt(12_001));
    }

    [Fact]
    public void A_press_inside_the_window_after_a_stop_starts_nothing_and_one_after_it_does()
    {
        var state = HandsFree();
        Latch(state, 1_000);
        state.Observe(VkRightControl, Down, 9_000);
        state.Observe(VkRightControl, Up, 9_060);

        Assert.Equal((HoldKeyTransition.None, HoldKeyTransition.None), Tap(state, 9_000 + Window - 1));
        Assert.False(state.IsHeldAt(9_000 + Window));
        Assert.False(state.IsLatched);

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 9_000 + Window + 50));
        Assert.True(state.IsHeldAt(9_000 + Window + 51));
    }

    [Fact]
    public void Keys_with_the_hold_key_up_do_not_cancel_a_latch()
    {
        var state = HandsFree();
        Latch(state, 1_000);

        foreach (var key in new[] { VkC, VkTab, VkLeftShift, 0xA4 /* Left Alt, for Alt+Tab */ })
        {
            Assert.Equal(HoldKeyTransition.None, state.Observe(key, Down, 2_000));
            Assert.Equal(HoldKeyTransition.None, state.Observe(key, Up, 2_050));
        }

        Assert.True(state.IsLatched);
        Assert.True(state.IsHeldAt(2_100));
    }

    [Fact]
    public void Keys_while_the_latching_press_is_still_down_do_not_cancel_it()
    {
        var state = HandsFree();
        Tap(state, 1_000);
        state.Observe(VkRightControl, Down, 1_160);
        Assert.True(state.IsLatched);

        // The press that latched holds nothing: a letter is the user working.
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkC, Down, 1_200));
        Assert.True(state.IsLatched);
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 1_400));
        Assert.True(state.IsHeldAt(1_500));
    }

    [Fact]
    public void While_the_key_is_held_another_key_is_still_a_shortcut()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 1_000);

        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(VkC, Down, 1_040));
        Assert.False(state.IsHeldAt(1_041));
    }

    [Fact]
    public void Escape_on_a_running_dictation_ends_a_latch_and_the_next_double_tap_latches()
    {
        var state = HandsFree();
        state.DictationInFlight = true;
        Latch(state, 1_000);

        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(HoldKeyState.VkEscape, Down, 2_000));
        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(2_001));

        Latch(state, 3_000);
    }

    /// <summary>Escape with the hold key still down abandons the press: its
    /// autorepeats start nothing (no minimum hold would discard what they
    /// opened) and its release releases nothing. The same for the press that
    /// latched, still held.</summary>
    [Fact]
    public void Escape_with_the_key_held_leaves_its_autorepeats_and_release_inert()
    {
        var held = HandsFree();
        held.DictationInFlight = true;
        held.Observe(VkRightControl, Down, 1_000);

        Assert.Equal(HoldKeyTransition.CancelRequested, held.Observe(HoldKeyState.VkEscape, Down, 1_600));
        held.DictationInFlight = false;
        Assert.Equal(HoldKeyTransition.None, held.Observe(VkRightControl, Down, 1_633));
        Assert.False(held.IsHeldAt(1_634));
        Assert.Equal(HoldKeyTransition.None, held.Observe(VkRightControl, Down, 1_666));
        Assert.Equal(HoldKeyTransition.None, held.Observe(VkRightControl, Up, 1_700));
        Assert.False(held.IsHeldAt(1_700 + Window));
        Assert.False(held.IsLatched);

        var latching = HandsFree();
        latching.DictationInFlight = true;
        Tap(latching, 3_000);
        latching.Observe(VkRightControl, Down, 3_160);
        Assert.True(latching.IsLatched);

        Assert.Equal(HoldKeyTransition.CancelRequested, latching.Observe(HoldKeyState.VkEscape, Down, 3_300));
        latching.DictationInFlight = false;
        Assert.Equal(HoldKeyTransition.None, latching.Observe(VkRightControl, Down, 3_333));
        Assert.Equal(HoldKeyTransition.None, latching.Observe(VkRightControl, Up, 3_400));
        Assert.False(latching.IsHeldAt(3_401));
        Assert.False(latching.IsLatched);

        // And the next double-tap still latches.
        Latch(latching, 5_000);
    }

    [Fact]
    public void Escape_forgets_a_first_press_so_the_next_press_is_not_its_pair()
    {
        var state = HandsFree();
        state.DictationInFlight = true;
        Tap(state, 1_000);

        state.Observe(HoldKeyState.VkEscape, Down, 1_080);
        Assert.False(state.IsHeldAt(1_081), "Escape closes the parked hold");
        state.Observe(VkRightControl, Down, 1_160);

        Assert.False(state.IsLatched);
    }

    // ─── The stop keys ──────────────────────────────────────────────────

    [Fact]
    public void Space_and_Enter_outside_a_latch_pass_through()
    {
        var state = HandsFree();
        Tap(state, 1_000);

        // A parked tap is not a latch: Space types.
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Down, 1_100));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Up, 1_150));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkReturn, Down, 5_000));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkReturn, Up, 5_050));
    }

    [Fact]
    public void A_stop_keys_autorepeat_is_swallowed_with_it_and_the_next_press_types()
    {
        var state = HandsFree();
        Latch(state, 1_000);

        Assert.Equal(HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow, state.Observe(HoldKeyState.VkSpace, Down, 2_000));
        Assert.Equal(HoldKeyTransition.Swallow, state.Observe(HoldKeyState.VkSpace, Down, 2_500));
        Assert.Equal(HoldKeyTransition.Swallow, state.Observe(HoldKeyState.VkSpace, Down, 2_530));
        Assert.Equal(HoldKeyTransition.Swallow, state.Observe(HoldKeyState.VkSpace, Up, 2_600));

        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Down, 3_000));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Up, 3_050));
    }

    [Fact]
    public void An_up_is_swallowed_only_if_its_own_down_was()
    {
        var state = HandsFree();

        // Space goes down before the latch and comes up during it: the app
        // saw the down, so it gets the up.
        state.Observe(HoldKeyState.VkSpace, Down, 900);
        Latch(state, 1_000);
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Up, 1_400));
        Assert.True(state.IsLatched);

        // Enter's down ends the latch; Space's later down is typed, and the
        // swallowed Enter's up is still eaten.
        Assert.Equal(HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow, state.Observe(HoldKeyState.VkReturn, Down, 2_000));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Down, 2_010));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Up, 2_020));
        Assert.Equal(HoldKeyTransition.Swallow, state.Observe(HoldKeyState.VkReturn, Up, 2_030));
    }

    /// <summary>A stop key held since before the latch keeps reaching the app
    /// through it: its autorepeats are the press the app saw go down, not a
    /// stop, so they pass and so does its up. The key's next press stops.</summary>
    [Theory]
    [InlineData(HoldKeyState.VkSpace)]
    [InlineData(HoldKeyState.VkReturn)]
    public void A_stop_key_held_since_before_the_latch_autorepeats_through_it(int stop)
    {
        var state = HandsFree();

        Assert.Equal(HoldKeyTransition.None, state.Observe(stop, Down, 900));
        Latch(state, 1_000);
        Assert.Equal(HoldKeyTransition.None, state.Observe(stop, Down, 1_400));   // autorepeat
        Assert.Equal(HoldKeyTransition.None, state.Observe(stop, Down, 1_433));
        Assert.True(state.IsLatched);
        Assert.Equal(HoldKeyTransition.None, state.Observe(stop, Up, 1_500));
        Assert.True(state.IsLatched);

        Assert.Equal(HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow, state.Observe(stop, Down, 2_000));
        Assert.Equal(HoldKeyTransition.Swallow, state.Observe(stop, Up, 2_050));
    }

    /// <summary>With the hold key held on an ordinary, unlatched hold, Space
    /// and Enter are a shortcut like any other key: the hold is cancelled and
    /// the key reaches the app, down and up.</summary>
    [Theory]
    [InlineData(HoldKeyState.VkSpace)]
    [InlineData(HoldKeyState.VkReturn)]
    public void Space_or_Enter_with_the_hold_key_held_is_a_shortcut_not_a_stop(int stop)
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 1_000);

        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(stop, Down, 1_500));
        Assert.False(state.IsHeldAt(1_501));
        Assert.Equal(HoldKeyTransition.None, state.Observe(stop, Up, 1_550));
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 1_600));
        Assert.False(state.IsLatched);
    }

    [Theory]
    [InlineData(HoldKeyTriggerMode.HoldOnly)]
    [InlineData(HoldKeyTriggerMode.HoldOrDoubleTapToggle)]
    public void The_other_modes_never_swallow(HoldKeyTriggerMode mode)
    {
        var state = new HoldKeyState(VkRightControl, mode, cancelOnChord: true);
        for (var at = 1_000L; at < 1_400; at += 100)
        {
            Tap(state, at);
        }

        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Down, 1_500) & HoldKeyTransition.Swallow);
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkReturn, Down, 1_510) & HoldKeyTransition.Swallow);
    }

    // ─── Which key ended a latch ────────────────────────────────────────

    [Fact]
    public void The_key_that_ended_a_latch_is_read_once()
    {
        var state = HandsFree();
        Assert.Equal(LatchEnd.None, state.ConsumeLatchEnd());

        Latch(state, 1_000);
        Assert.Equal(LatchEnd.None, state.ConsumeLatchEnd());
        state.Observe(VkRightControl, Down, 2_000);
        Assert.Equal(LatchEnd.HoldKey, state.ConsumeLatchEnd());
        Assert.Equal(LatchEnd.None, state.ConsumeLatchEnd());
        state.Observe(VkRightControl, Up, 2_060);

        Latch(state, 3_000);
        state.Observe(HoldKeyState.VkReturn, Down, 4_000);
        Assert.Equal(LatchEnd.StopKey, state.ConsumeLatchEnd());
        state.Observe(HoldKeyState.VkReturn, Up, 4_050);

        state.DictationInFlight = true;
        Latch(state, 5_000);
        state.Observe(HoldKeyState.VkEscape, Down, 6_000);
        Assert.Equal(LatchEnd.Escape, state.ConsumeLatchEnd());

        // Escape with no latch, and the ends the shell makes itself, say
        // nothing: the shell knows why.
        state.Observe(VkRightControl, Down, 7_000);
        state.Observe(HoldKeyState.VkEscape, Down, 7_600);
        Assert.Equal(LatchEnd.None, state.ConsumeLatchEnd());
        state.Observe(VkRightControl, Up, 7_700);
        Latch(state, 9_000);
        state.EndLatch();
        Assert.Equal(LatchEnd.None, state.ConsumeLatchEnd());

        // And a teardown forgets one nobody read.
        Latch(state, 11_000);
        state.Observe(HoldKeyState.VkSpace, Down, 12_000);
        state.Release();
        Assert.Equal(LatchEnd.None, state.ConsumeLatchEnd());
    }

    // ─── Ending from outside, and teardown ──────────────────────────────

    [Fact]
    public void Ending_a_latch_from_outside_stops_the_hold_and_the_swallowing()
    {
        var state = HandsFree();
        Latch(state, 1_000);

        state.EndLatch();

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(1_500));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Down, 1_600));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkReturn, Down, 1_610));
    }

    [Fact]
    public void Ending_a_parked_hold_from_outside_closes_it_at_once()
    {
        var state = HandsFree();
        Tap(state, 1_000);
        Assert.True(state.IsHeldAt(1_100));

        state.EndLatch();

        Assert.False(state.IsHeldAt(1_100));
    }

    /// <summary>A press the shell refused while it was still down holds
    /// nothing from then on: its release neither ends anything nor parks, and
    /// a second press inside the window both starts a hold and latches it (the
    /// Mac's "latch made by the second press").</summary>
    [Fact]
    public void A_press_ended_from_outside_holds_nothing_and_can_still_begin_a_pair()
    {
        var state = HandsFree();
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 1_000));

        state.EndLatch();
        Assert.False(state.IsHeldAt(1_001));
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 1_060));
        Assert.False(state.IsHeldAt(1_100), "a refused press does not park");

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 1_200));
        Assert.True(state.IsLatched);
    }

    [Fact]
    public void Release_clears_a_latch_and_the_swallowing()
    {
        var state = HandsFree();
        Latch(state, 1_000);
        state.Observe(HoldKeyState.VkSpace, Down, 1_500);

        Assert.Equal(HoldKeyTransition.None, state.Release());
        Latch(state, 3_000);
        state.Release();

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(3_500));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Up, 3_600));
        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkSpace, Down, 3_700));
    }

    // ─── The menu phantom ───────────────────────────────────────────────

    /// <summary>Every release of a bare Alt trigger in this mode has a paste
    /// behind it or near it, so each one gets the phantom; see OnUp's
    /// measurements for why a lone Alt tap must never reach the target.</summary>
    [Fact]
    public void Every_release_of_a_bare_Alt_trigger_gets_the_menu_phantom()
    {
        var state = HandsFree(VkRightAlt);
        const int SysDown = HoldKeyState.WmSysKeyDown;
        const int SysUp = HoldKeyState.WmSysKeyUp;

        state.Observe(VkRightAlt, SysDown, 1_000);
        Assert.Equal(HoldKeyTransition.SuppressMenu, state.Observe(VkRightAlt, SysUp, 1_060));   // parked
        state.Observe(VkRightAlt, SysDown, 1_160);
        Assert.Equal(HoldKeyTransition.SuppressMenu, state.Observe(VkRightAlt, SysUp, 1_220));   // latched
        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkRightAlt, SysDown, 5_000));
        Assert.Equal(HoldKeyTransition.SuppressMenu, state.Observe(VkRightAlt, SysUp, 5_060));   // stopped
        state.Observe(VkRightAlt, SysDown, 5_100);
        Assert.Equal(HoldKeyTransition.SuppressMenu, state.Observe(VkRightAlt, SysUp, 5_160));   // absorbed
        state.Observe(VkRightAlt, SysDown, 9_000);
        Assert.Equal(
            HoldKeyTransition.HoldEnded | HoldKeyTransition.SuppressMenu,
            state.Observe(VkRightAlt, SysUp, 12_000));                                           // a hold
    }
}
