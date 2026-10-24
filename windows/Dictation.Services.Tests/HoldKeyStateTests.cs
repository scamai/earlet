using Dictation.Services;

namespace Dictation.Services.Tests;

/// <summary>
/// The trigger layer's decision logic, which had no test at all.
/// </summary>
/// <remarks>
/// <para>
/// <c>LowLevelHoldKeyRegistration</c> was 340 lines with zero references from
/// any test file in the tree, and the untested part was not trivial: the held
/// flag, the teardown path that deliberately closes an open hold, and the
/// phantom-keystroke branch that fires on the first autorepeat and <em>only</em>
/// when the trigger is a bare Alt. That last branch's absence caused a real
/// shipped defect — the pasted transcript eaten by an armed menu bar — and
/// round 1 turned one hard-coded key into five user-selectable ones, three of
/// which take the other path.
/// </para>
/// <para>
/// Not visible until it breaks, and then total: every dictation begins here. A
/// regression does not degrade dictation, it removes it, and the 41-case
/// end-to-end gate would stay green because it drives the worker protocol
/// rather than the hook.
/// </para>
/// </remarks>
public sealed class HoldKeyStateTests
{
    private const int VkRightAlt = 0xA5;
    private const int VkLeftAlt = 0xA4;
    private const int VkRightControl = 0xA3;
    private const int VkRightShift = 0xA1;
    private const int VkF13 = 0x7C;

    /// <summary>Some key that is never a trigger in these tests.</summary>
    private const int VkA = 0x41;

    // ─── Construction ───────────────────────────────────────────────

    [Fact]
    public void A_binding_with_no_virtual_key_is_refused_rather_than_never_firing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoldKeyState(0));
    }

    [Theory]
    [InlineData(VkRightAlt, true)]
    [InlineData(VkLeftAlt, true)]
    [InlineData(VkRightControl, false)]
    [InlineData(VkRightShift, false)]
    [InlineData(VkF13, false)]
    public void Only_a_bare_Alt_arms_menu_mode(int vk, bool arms)
    {
        Assert.Equal(arms, new HoldKeyState(vk).TriggerArmsMenuMode);
    }

    [Fact]
    public void A_fresh_state_is_not_held()
    {
        Assert.False(new HoldKeyState(VkF13).IsHeld);
    }

    // ─── The hold ───────────────────────────────────────────────────

    [Fact]
    public void The_first_press_opens_the_hold()
    {
        var state = new HoldKeyState(VkF13);

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkF13, HoldKeyState.WmKeyDown));
        Assert.True(state.IsHeld);
    }

    [Fact]
    public void The_release_closes_it()
    {
        var state = new HoldKeyState(VkF13);
        state.Observe(VkF13, HoldKeyState.WmKeyDown);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkF13, HoldKeyState.WmKeyUp));
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void A_bare_Alt_is_driven_by_the_SYSKEY_pair_and_not_the_ordinary_one()
    {
        // The reason both pairs are handled. A hook that watched only WM_KEYDOWN
        // and WM_KEYUP would see nothing at all for a lone Alt, and the shipped
        // default binding is Right Alt — the whole feature would be dead.
        var state = new HoldKeyState(VkRightAlt);

        Assert.Equal(
            HoldKeyTransition.HoldStarted,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown));
        Assert.Equal(
            HoldKeyTransition.HoldEnded,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp));
    }

    [Fact]
    public void A_release_with_no_hold_open_is_not_reported_as_one()
    {
        // What the very first event looks like if the user was already resting
        // on the trigger when the hook went in. Reporting it would hand the
        // caller a hold it never saw start.
        var state = new HoldKeyState(VkF13);

        Assert.Equal(HoldKeyTransition.None, state.Observe(VkF13, HoldKeyState.WmKeyUp));
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void A_key_that_is_not_the_trigger_changes_nothing()
    {
        var state = new HoldKeyState(VkF13);

        Assert.Equal(HoldKeyTransition.None, state.Observe(VkA, HoldKeyState.WmKeyDown));
        Assert.False(state.IsHeld);

        state.Observe(VkF13, HoldKeyState.WmKeyDown);
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkA, HoldKeyState.WmKeyUp));
        // Somebody else's key-up must not close this hold.
        Assert.True(state.IsHeld);
    }

    [Fact]
    public void A_message_this_trigger_has_no_opinion_about_changes_nothing()
    {
        // A low-level hook sees more than four messages. WM_CHAR (0x0102) is
        // one; the state machine must ignore it rather than fall into a branch.
        var state = new HoldKeyState(VkF13);

        Assert.Equal(HoldKeyTransition.None, state.Observe(VkF13, 0x0102));
        Assert.False(state.IsHeld);
    }

    // ─── The phantom keystroke ──────────────────────────────────────

    [Fact]
    public void A_tap_too_short_to_repeat_produces_no_phantom()
    {
        // Down, up, nothing in between. A tap that quick is also too quick to
        // dictate — no transcript, no paste, nothing for an armed menu to eat —
        // so injecting there would be a keystroke sent into the user's session
        // for no reason at all.
        var state = new HoldKeyState(VkRightAlt);

        Assert.Equal(
            HoldKeyTransition.HoldStarted,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown));
        Assert.Equal(
            HoldKeyTransition.HoldEnded,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp));
    }

    [Fact]
    public void A_hold_on_Right_Alt_produces_exactly_one_phantom_across_its_repeat_stream()
    {
        // The first autorepeat fires it; every later repeat in the same hold
        // must not. A phantom per repeat would put a stream of injected key
        // events into the target for the length of every sentence — and it was
        // measured to buy nothing, because the repeat AFTER the last phantom
        // re-arms the menu exactly like the repeat after the first one does.
        var state = new HoldKeyState(VkRightAlt);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown);

        Assert.Equal(
            HoldKeyTransition.SuppressMenu,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown));

        for (var repeat = 0; repeat < 20; repeat++)
        {
            Assert.Equal(
                HoldKeyTransition.None,
                state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown));
        }
    }

    [Fact]
    public void A_hold_on_Right_Alt_fires_its_last_phantom_on_the_release()
    {
        // THE ONE THAT SHIPPED BROKEN. A menu bar re-arms on every Alt key-down
        // it sees, and a physical hold delivers autorepeat downs for its whole
        // length, so the phantom on the FIRST repeat is undone by all twenty
        // that follow it. Windows 11 Notepad then took the inserter's Ctrl+V
        // into its menu-bar island and the document stayed empty while the log
        // said "Inserted 358 characters into Notepad".
        //
        // Measured on the device: same repeat stream, phantom moved to just
        // before the Alt-up, and focus stays on RichEditD2DPT with the paste
        // landing. So the release edge is where the guarantee lives.
        var state = new HoldKeyState(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);

        for (var repeat = 0; repeat < 20; repeat++)
        {
            clock += 33;
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        }

        clock += 33;
        var release = state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock);

        Assert.True(release.HasFlag(HoldKeyTransition.SuppressMenu));
        // One event, two things to do — the hold ends here as well, which is
        // why the transition is a flags value.
        Assert.True(release.HasFlag(HoldKeyTransition.HoldEnded));
    }

    [Fact]
    public void A_hold_on_Right_Win_fires_its_last_phantom_on_the_release_too()
    {
        // A lone Win tap does worse than arm a menu: the Start menu STEALS
        // FOCUS, so the transcript lands in the Start search box. Same repeat
        // stream, same fix.
        var state = new HoldKeyState(VkRightWin);
        long clock = 1_000;

        state.Observe(VkRightWin, HoldKeyState.WmKeyDown, clock);

        for (var repeat = 0; repeat < 20; repeat++)
        {
            clock += 33;
            state.Observe(VkRightWin, HoldKeyState.WmKeyDown, clock);
        }

        Assert.True(state.Observe(VkRightWin, HoldKeyState.WmKeyUp, clock + 33)
            .HasFlag(HoldKeyTransition.SuppressMenu));
    }

    [Theory]
    [InlineData(VkRightControl)]
    [InlineData(VkRightShift)]
    [InlineData(VkF13)]
    public void A_hold_on_a_non_Alt_trigger_stays_silent_on_the_release_edge_as_well(int vk)
    {
        // The release-edge phantom must not leak to the three triggers that arm
        // nothing. An injected keystroke there is an unexplained key event in
        // the user's document with no benefit behind it — and the fix for
        // Notepad is not a licence to send one on every Right-Shift release.
        var state = new HoldKeyState(vk);
        long clock = 1_000;

        state.Observe(vk, HoldKeyState.WmKeyDown, clock);
        clock += 33;
        state.Observe(vk, HoldKeyState.WmKeyDown, clock);
        clock += 4_000;

        Assert.Equal(
            HoldKeyTransition.HoldEnded,
            state.Observe(vk, HoldKeyState.WmKeyUp, clock));
    }

    [Fact]
    public void The_next_hold_gets_its_own_phantom()
    {
        // Once per hold, not once per process. A user who dictates twice must
        // get menu suppression both times.
        var state = new HoldKeyState(VkRightAlt);

        for (var hold = 0; hold < 3; hold++)
        {
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown);
            Assert.Equal(
                HoldKeyTransition.SuppressMenu,
                state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown));
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp);
        }
    }

    [Theory]
    [InlineData(VkRightControl)]
    [InlineData(VkRightShift)]
    [InlineData(VkF13)]
    public void A_hold_on_a_non_Alt_trigger_produces_none(int vk)
    {
        // Three of the five triggers round 1 offered take this path, and none
        // of them arms a menu bar — an injected keystroke there would be an
        // unexplained key event in the user's document for no benefit.
        var state = new HoldKeyState(vk);
        state.Observe(vk, HoldKeyState.WmKeyDown);

        for (var repeat = 0; repeat < 20; repeat++)
        {
            Assert.Equal(HoldKeyTransition.None, state.Observe(vk, HoldKeyState.WmKeyDown));
        }
    }

    [Fact]
    public void The_left_Alt_takes_the_same_path_as_the_right_one()
    {
        // VK_LMENU is not a trigger the picker offers, but the branch reads
        // both and a future picker entry must not be a silent regression.
        var state = new HoldKeyState(VkLeftAlt);
        state.Observe(VkLeftAlt, HoldKeyState.WmSysKeyDown);

        Assert.Equal(
            HoldKeyTransition.SuppressMenu,
            state.Observe(VkLeftAlt, HoldKeyState.WmSysKeyDown));
    }

    // ─── Teardown ───────────────────────────────────────────────────

    [Fact]
    public void Unregister_mid_hold_reports_the_key_up()
    {
        // The rule Unregister documents: a hold that was open when the hook
        // came down is closed, so a teardown mid-utterance cannot leave the
        // caller believing the key is still pressed and the microphone still
        // wanted.
        var state = new HoldKeyState(VkRightAlt);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Release());
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void Unregister_at_rest_synthesises_nothing()
    {
        Assert.Equal(HoldKeyTransition.None, new HoldKeyState(VkRightAlt).Release());
    }

    [Fact]
    public void A_hold_reopened_after_a_teardown_still_gets_its_phantom()
    {
        // Release has to clear the once-per-hold latch as well as the held
        // flag. Clearing only the flag would leave the NEXT hold's menu
        // suppression silently skipped — a defect that reproduces only after a
        // disarm/rearm, which is exactly the shape that ships.
        var state = new HoldKeyState(VkRightAlt);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown);
        state.Release();

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown);
        Assert.Equal(
            HoldKeyTransition.SuppressMenu,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown));
    }

    // ─── Double-tap to toggle ───────────────────────────────────────
    //
    // Anything longer than a sentence meant physically holding a key down for
    // its whole duration; on macOS you double-tap once, talk, and double-tap
    // again. The whole gesture is driven from a monotonic clock passed in, so
    // every timing below is exact rather than slept through.

    private static HoldKeyState Toggle(int vk = VkF13) =>
        new(vk, HoldKeyTriggerMode.HoldOrDoubleTapToggle);

    /// <summary>One press-and-release, as a tap of the given length.</summary>
    private static void Tap(HoldKeyState state, int vk, ref long clock, int lengthMs = 60)
    {
        state.Observe(vk, HoldKeyState.WmKeyDown, clock);
        clock += lengthMs;
        state.Observe(vk, HoldKeyState.WmKeyUp, clock);
    }

    [Fact]
    public void Hold_to_talk_still_works_unchanged_in_toggle_mode()
    {
        // The toggle ADDS a gesture; it must not take one away. A user who
        // turns it on and then holds the key as they always have gets exactly
        // the same dictation.
        var state = Toggle();
        long clock = 1_000;

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkF13, HoldKeyState.WmKeyDown, clock));
        clock += 4_000;
        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkF13, HoldKeyState.WmKeyUp, clock));
        Assert.False(state.IsHeldAt(clock));
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_double_tap_latches_the_dictation_open_with_the_key_released()
    {
        var state = Toggle();
        long clock = 1_000;

        Tap(state, VkF13, ref clock);
        clock += 100;
        Tap(state, VkF13, ref clock);

        Assert.True(state.IsLatched);
        // Ten seconds later, with nothing touched, the microphone is still
        // wanted. That is the entire feature.
        Assert.True(state.IsHeldAt(clock + 10_000));
    }

    [Fact]
    public void The_capture_never_drops_between_the_two_taps()
    {
        // The property that makes a double-tap ONE dictation instead of two
        // aborted ones. Sampled at every millisecond across the whole gesture,
        // because a single frame of "not held" is a released hold to the 30 ms
        // poll and would end the capture with "that was too short".
        var state = Toggle();
        long clock = 1_000;
        var start = clock;

        Tap(state, VkF13, ref clock);
        clock += 120;
        Tap(state, VkF13, ref clock);

        for (var t = start; t <= clock; t++)
        {
            Assert.True(state.IsHeldAt(t), $"the hold dropped at +{t - start}ms");
        }
    }

    [Fact]
    public void A_second_double_tap_ends_it()
    {
        var state = Toggle();
        long clock = 1_000;
        Tap(state, VkF13, ref clock);
        clock += 100;
        Tap(state, VkF13, ref clock);
        Assert.True(state.IsLatched);

        // Talk for a while.
        clock += 6_000;

        Tap(state, VkF13, ref clock);
        clock += 100;
        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        clock += 60;

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkF13, HoldKeyState.WmKeyUp, clock));
        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock));
    }

    [Fact]
    public void A_single_stray_tap_does_not_latch_anything()
    {
        // The reason this is a DOUBLE-tap and not macOS's single click. The
        // keyboard triggers on offer are bare modifiers people press by
        // accident all day; a single tap latching the microphone open would be
        // a brushed Alt starting a recording.
        var state = Toggle();
        long clock = 1_000;

        Tap(state, VkF13, ref clock);

        Assert.False(state.IsLatched);
        // The grace window holds the hold open while a second tap could still
        // arrive, and then it closes on its own.
        Assert.True(state.IsHeldAt(clock + HoldKeyState.DoubleTapGapMs - 1));
        Assert.False(state.IsHeldAt(clock + HoldKeyState.DoubleTapGapMs));
    }

    [Fact]
    public void A_second_tap_arriving_too_late_is_a_new_first_tap()
    {
        var state = Toggle();
        long clock = 1_000;
        Tap(state, VkF13, ref clock);

        clock += HoldKeyState.DoubleTapGapMs + 50;
        Tap(state, VkF13, ref clock);

        Assert.False(state.IsLatched);
    }

    [Fact]
    public void Two_slow_presses_are_two_holds_and_not_a_double_tap()
    {
        // A press longer than TapMaxMs is a hold, whatever follows it. Someone
        // dictating two short sentences back to back must not find themselves
        // latched.
        var state = Toggle();
        long clock = 1_000;

        Tap(state, VkF13, ref clock, lengthMs: HoldKeyState.TapMaxMs + 1);
        clock += 100;
        Tap(state, VkF13, ref clock, lengthMs: HoldKeyState.TapMaxMs + 1);

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock));
    }

    [Fact]
    public void A_long_hold_during_a_latched_dictation_ends_it()
    {
        // Holding the key through a sentence and letting go means the same "I
        // am done" the double-tap means. Leaving the latch on after it would
        // strand the microphone open with the user believing they had stopped.
        var state = Toggle();
        long clock = 1_000;
        Tap(state, VkF13, ref clock);
        clock += 100;
        Tap(state, VkF13, ref clock);
        Assert.True(state.IsLatched);

        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        clock += 2_000;

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkF13, HoldKeyState.WmKeyUp, clock));
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_press_during_a_latched_dictation_is_not_a_new_hold()
    {
        // Restarting the capture here would cut the user's sentence in two.
        var state = Toggle();
        long clock = 1_000;
        Tap(state, VkF13, ref clock);
        clock += 100;
        Tap(state, VkF13, ref clock);

        clock += 3_000;
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkF13, HoldKeyState.WmKeyDown, clock));
    }

    [Fact]
    public void A_press_inside_the_grace_window_is_not_a_new_hold_either()
    {
        var state = Toggle();
        long clock = 1_000;
        Tap(state, VkF13, ref clock);

        clock += 100;
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkF13, HoldKeyState.WmKeyDown, clock));
    }

    [Fact]
    public void Latching_and_unlatching_can_be_repeated()
    {
        var state = Toggle();
        long clock = 1_000;

        for (var round = 0; round < 3; round++)
        {
            Tap(state, VkF13, ref clock);
            clock += 100;
            Tap(state, VkF13, ref clock);
            Assert.True(state.IsLatched, $"round {round}: did not latch");

            clock += 2_000;

            Tap(state, VkF13, ref clock);
            clock += 100;
            Tap(state, VkF13, ref clock);
            Assert.False(state.IsLatched, $"round {round}: did not unlatch");

            clock += 2_000;
        }
    }

    [Fact]
    public void Teardown_closes_a_latched_dictation()
    {
        // The latch is the only thing holding the microphone open. A teardown
        // that cleared the physical flag and left the latch set would leave the
        // capture running with no hook left to ever end it.
        var state = Toggle();
        long clock = 1_000;
        Tap(state, VkF13, ref clock);
        clock += 100;
        Tap(state, VkF13, ref clock);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Release());
        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock + 10_000));
    }

    [Fact]
    public void Hold_only_mode_never_latches_however_fast_you_tap()
    {
        // The default, and the guarantee that turning the feature off actually
        // turns it off.
        var state = new HoldKeyState(VkF13);
        long clock = 1_000;

        Tap(state, VkF13, ref clock);
        clock += 50;
        Tap(state, VkF13, ref clock);

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock));
        Assert.Equal(HoldKeyTriggerMode.HoldOnly, state.Mode);
    }

    [Fact]
    public void Hold_only_mode_has_no_grace_window()
    {
        // A tap in hold-only mode ends the hold the instant the key comes up,
        // exactly as it always has. A grace window here would extend every
        // stray brush into a longer capture for no benefit at all.
        var state = new HoldKeyState(VkF13);
        long clock = 1_000;

        Tap(state, VkF13, ref clock);

        Assert.False(state.IsHeldAt(clock));
    }

    // ─── Menu-mode suppression in toggle mode ───────────────────────
    //
    // Every tap on a bare Alt arms the target's menu bar, and in toggle mode a
    // tap IS a gesture with a paste behind it — so the phantom keystroke has to
    // fire on the tap's key-up, which an autorepeat never reaches.

    [Fact]
    public void Every_tap_on_an_Alt_toggle_trigger_asks_for_the_phantom()
    {
        var state = Toggle(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        clock += 60;

        Assert.True(state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock)
            .HasFlag(HoldKeyTransition.SuppressMenu));
    }

    [Fact]
    public void The_tap_that_ends_a_latched_dictation_asks_for_it_too()
    {
        // This is the one that matters most: the paste happens seconds after
        // this tap, and an armed menu bar is still armed when it arrives.
        var state = Toggle(VkRightAlt);
        long clock = 1_000;
        Tap(state, VkRightAlt, ref clock);
        clock += 100;
        Tap(state, VkRightAlt, ref clock);
        clock += 4_000;

        Tap(state, VkRightAlt, ref clock);
        clock += 100;
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        clock += 60;
        var final = state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock);

        Assert.True(final.HasFlag(HoldKeyTransition.SuppressMenu));
        // And it is the same event that ends the dictation — one event, two
        // things to do, which is why the transition is a flags value.
        Assert.True(final.HasFlag(HoldKeyTransition.HoldEnded));
    }

    [Theory]
    [InlineData(VkRightControl)]
    [InlineData(VkRightShift)]
    [InlineData(VkF13)]
    public void A_non_Alt_toggle_trigger_never_asks_for_the_phantom(int vk)
    {
        // An injected keystroke in the user's document for a key that arms no
        // menu is an unexplained key event with no benefit behind it.
        var state = Toggle(vk);
        long clock = 1_000;

        state.Observe(vk, HoldKeyState.WmKeyDown, clock);
        clock += 60;

        Assert.False(state.Observe(vk, HoldKeyState.WmKeyUp, clock)
            .HasFlag(HoldKeyTransition.SuppressMenu));
    }

    [Fact]
    public void A_long_hold_on_an_Alt_toggle_trigger_uses_both_edges()
    {
        // Same as hold-only, because the defect is the same: the repeat phantom
        // opens the hold and the release phantom is the one the target's menu
        // bar actually obeys, every repeat in between having re-armed it.
        var state = Toggle(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        Assert.True(state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock + 500)
            .HasFlag(HoldKeyTransition.SuppressMenu));

        clock += 4_000;
        Assert.True(state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock)
            .HasFlag(HoldKeyTransition.SuppressMenu));
    }

    [Fact]
    public void A_tap_in_hold_only_mode_still_produces_no_phantom()
    {
        // The original rule, unchanged: a tap too quick to dictate is too quick
        // to have a paste behind it, so there is nothing for an armed menu to
        // eat and nothing to inject for.
        var state = new HoldKeyState(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        clock += 60;

        Assert.Equal(
            HoldKeyTransition.HoldEnded,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock));
    }

    // ─── Escape, the way out of a dictation you already decided against ──

    [Fact]
    public void Escape_while_nothing_is_running_is_completely_inert()
    {
        // Escape is one of the most-pressed keys on the keyboard. A trigger
        // layer that reacted to it while idle would be doing work inside the
        // system input path on every dialog anybody dismissed, and — worse —
        // the shipped composition would cancel a dictation that had not begun.
        var state = new HoldKeyState(VkRightAlt);

        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 1_000));
        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyUp, 1_050));
    }

    [Fact]
    public void Escape_while_the_key_is_merely_armed_is_still_inert()
    {
        // Armed is not running. The distinction is the whole gate: the hook is
        // installed for the entire session, and Escape must only mean something
        // for the seconds a dictation is actually open.
        var state = new HoldKeyState(VkRightAlt) { DictationInFlight = false };

        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 1_000));
    }

    [Fact]
    public void Escape_during_a_running_dictation_asks_for_a_cancel()
    {
        // The gap this closes: DictationOutcome.Cancelled, the Cancelled
        // result and the neutral HUD pill all existed on Windows and no key a
        // user could press reached any of them.
        var state = new HoldKeyState(VkRightAlt);
        long clock = 1_000;

        Assert.Equal(
            HoldKeyTransition.HoldStarted,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock));
        state.DictationInFlight = true;

        Assert.Equal(
            HoldKeyTransition.CancelRequested,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, clock + 800));
    }

    [Fact]
    public void A_cancel_closes_the_hold_so_the_microphone_does_not_stay_open()
    {
        // Without this the capture runs until the user thinks to let go of a
        // key whose dictation they have already abandoned — seconds of audio
        // nobody will ever use, with the HUD already saying "Cancelled".
        var state = new HoldKeyState(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        state.DictationInFlight = true;
        Assert.True(state.IsHeldAt(clock + 10));

        state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, clock + 800);

        Assert.False(state.IsHeldAt(clock + 810));
    }

    [Fact]
    public void The_physical_release_after_a_cancel_reports_nothing()
    {
        // The user is still holding the trigger when they hit Escape. That
        // key-up has to be swallowed: reporting it as HoldEnded would hand the
        // caller the release of a hold it has already been told was cancelled,
        // and the poll would drive a second teardown.
        var state = new HoldKeyState(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        state.DictationInFlight = true;
        state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, clock + 800);

        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 2_000));
    }

    [Fact]
    public void The_held_keys_autorepeats_after_a_cancel_open_nothing()
    {
        // The key is still down when Escape lands, so its autorepeats keep
        // coming. Read as a fresh press, the first of them reopened the
        // microphone on a dictation the user had just cancelled.
        var state = new HoldKeyState(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        state.DictationInFlight = true;
        state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, clock + 800);
        state.DictationInFlight = false;

        for (var at = clock + 833; at < clock + 1_500; at += 33)
        {
            Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, at) & HoldKeyTransition.HoldStarted);
            Assert.False(state.IsHeldAt(at + 1));
        }

        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 1_600));
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock + 3_000));
    }

    [Fact]
    public void Escape_cancels_a_latched_toggle_dictation_too()
    {
        // The toggle gesture is the one where the key is NOT down — so without
        // this, the only way out of a latched dictation you regret is to finish
        // it and double-tap, which is exactly the trap macOS's Escape avoids.
        var state = Toggle(VkRightAlt);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 60);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock + 200);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 260);
        Assert.True(state.IsLatched);

        state.DictationInFlight = true;
        Assert.Equal(
            HoldKeyTransition.CancelRequested,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, clock + 3_000));

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock + 3_010));
    }

    [Fact]
    public void Only_the_down_edge_cancels()
    {
        // One press, one cancel. Acting on the key-up as well would consume the
        // next dictation's first moments if the user held Escape across them.
        var state = new HoldKeyState(VkRightAlt) { DictationInFlight = true };

        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyUp, 1_000));
    }

    [Fact]
    public void A_trigger_bound_to_Escape_stays_a_trigger()
    {
        // Not a binding the picker offers, and the rule is worth pinning
        // anyway: a key cannot both start a dictation and abandon one. The
        // trigger comparison runs first, so Escape-as-trigger keeps its hold
        // behaviour and grows no second meaning.
        var state = new HoldKeyState(HoldKeyState.VkEscape) { DictationInFlight = true };

        Assert.Equal(
            HoldKeyTransition.HoldStarted,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 1_000));
    }

    // ─── A separate key for the double-tap toggle ───────────────────

    private const int VkRightWin = 0x5C;
    private const int VkCapsLock = 0x14;

    /// <summary>Hold on <paramref name="hold"/>, latch on <paramref name="toggle"/>.</summary>
    private static HoldKeyState Split(int hold, int toggle) =>
        new(hold, HoldKeyTriggerMode.HoldOrDoubleTapToggle, toggle);

    [Fact]
    public void The_hold_key_still_holds_when_the_toggle_lives_elsewhere()
    {
        // The property the split exists for. On Windows the toggle rode the
        // hold key, so turning it on CHANGED what the hold key did; macOS keeps
        // two bindings a user can hold in their head separately.
        var state = Split(VkRightAlt, VkF13);
        long clock = 1_000;

        Assert.Equal(
            HoldKeyTransition.HoldStarted,
            state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock));

        clock += 3_000;
        Assert.True(state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock)
            .HasFlag(HoldKeyTransition.HoldEnded));
        Assert.False(state.IsHeldAt(clock + 1));
    }

    [Fact]
    public void A_double_tap_of_the_hold_key_no_longer_latches_when_the_toggle_moved()
    {
        // Two ways to latch, one of which the user never asked for, is worse
        // than one. With a separate toggle key the trigger is hold-only.
        var state = Split(VkRightAlt, VkF13);
        long clock = 1_000;

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 60);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock + 200);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 260);

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock + 1_500));
    }

    [Fact]
    public void A_double_tap_of_the_toggle_key_latches_and_a_second_ends_it()
    {
        var state = Split(VkRightAlt, VkF13);
        long clock = 1_000;

        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(VkF13, HoldKeyState.WmKeyDown, clock));
        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 60));

        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock + 200);
        Assert.Equal(
            HoldKeyTransition.HoldStarted,
            state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 260));

        Assert.True(state.IsLatched);
        Assert.True(state.IsHeldAt(clock + 10_000));

        clock += 20_000;
        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 60);
        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock + 200);
        Assert.Equal(
            HoldKeyTransition.HoldEnded,
            state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 260));

        Assert.False(state.IsLatched);
    }

    [Fact]
    public void One_stray_tap_of_a_separate_toggle_key_opens_nothing()
    {
        // The difference from the shared-key gesture, and it is the whole
        // reason the two grace windows are separate. There, the first tap has
        // already opened a capture the double-tap must flow into with no seam,
        // so the window holds the microphone open. Here nothing is open, so a
        // brushed toggle key costs nothing at all rather than 400 ms of a room.
        var state = Split(VkRightAlt, VkF13);
        long clock = 1_000;

        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 60);

        Assert.False(state.IsHeldAt(clock + 100));
        Assert.False(state.IsHeldAt(clock + 300));
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_long_press_of_the_toggle_key_is_not_a_gesture()
    {
        // Somebody using the key for its own purpose. It must not latch, and it
        // must not leave a half-finished double-tap pending either.
        var state = Split(VkRightAlt, VkF13);
        long clock = 1_000;

        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 2_000));

        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_hold_during_a_latched_dictation_does_not_end_it()
    {
        // The two gestures are independent once they are on separate keys. A
        // hold that happened while a latch was open is a key the user pressed
        // for some other reason, and ending their dictation on it would be a
        // surprise with a paste behind it.
        var state = Split(VkRightAlt, VkF13);
        long clock = 1_000;

        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 60);
        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock + 200);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 260);
        Assert.True(state.IsLatched);

        clock += 5_000;
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 2_000);

        Assert.True(state.IsLatched);
        Assert.True(state.IsHeldAt(clock + 3_000));
    }

    [Fact]
    public void A_toggle_key_equal_to_the_trigger_is_the_shipped_gesture()
    {
        // Normalised at construction rather than left as a second code path
        // that behaves almost the same.
        var state = new HoldKeyState(
            VkRightAlt, HoldKeyTriggerMode.HoldOrDoubleTapToggle, VkRightAlt);

        Assert.Equal(0, state.ToggleVirtualKey);

        long clock = 1_000;
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 60);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, clock + 200);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, clock + 260);

        Assert.True(state.IsLatched);
    }

    [Fact]
    public void A_separate_toggle_key_is_inert_while_the_gesture_is_off()
    {
        // Hold-only means hold-only. A key configured for a gesture nobody
        // turned on must do nothing.
        var state = new HoldKeyState(VkRightAlt, HoldKeyTriggerMode.HoldOnly, VkF13);
        long clock = 1_000;

        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 60);
        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock + 200);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 260);

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock + 300));
    }

    [Fact]
    public void Escape_cancels_a_dictation_latched_from_a_separate_key()
    {
        var state = Split(VkRightAlt, VkF13);
        long clock = 1_000;

        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 60);
        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock + 200);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock + 260);
        Assert.True(state.IsLatched);

        state.DictationInFlight = true;
        Assert.Equal(
            HoldKeyTransition.CancelRequested,
            state.Observe(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, clock + 3_000));

        Assert.False(state.IsLatched);
    }

    // ─── Menu-mode suppression covers the Windows keys too ──────────

    [Theory]
    [InlineData(VkRightWin)]
    public void A_Windows_key_trigger_asks_for_the_phantom(int vk)
    {
        // Without it a lone Win tap opens the Start menu, which also STEALS
        // FOCUS — so the inserter's Ctrl+V would land in the Start search box
        // rather than in the user's document. This is why Right Win can be
        // offered as a trigger at all.
        var state = new HoldKeyState(vk);
        long clock = 1_000;

        state.Observe(vk, HoldKeyState.WmKeyDown, clock);

        Assert.True(state.Observe(vk, HoldKeyState.WmKeyDown, clock + 500)
            .HasFlag(HoldKeyTransition.SuppressMenu));
    }

    [Fact]
    public void A_tap_of_a_Windows_toggle_key_asks_for_the_phantom()
    {
        // In toggle mode a tap IS a gesture, with a paste behind it, so the
        // phantom has to fire on the tap rather than on an autorepeat a tap
        // never reaches.
        var state = Split(VkRightAlt, VkRightWin);
        long clock = 1_000;

        state.Observe(VkRightWin, HoldKeyState.WmKeyDown, clock);

        Assert.True(state.Observe(VkRightWin, HoldKeyState.WmKeyUp, clock + 60)
            .HasFlag(HoldKeyTransition.SuppressMenu));
    }

    [Fact]
    public void A_Caps_Lock_toggle_key_asks_for_no_phantom()
    {
        // It arms no menu. An injected keystroke in the user's document for a
        // key that needs none is an unexplained key event with no benefit.
        var state = Split(VkRightAlt, VkCapsLock);
        long clock = 1_000;

        state.Observe(VkCapsLock, HoldKeyState.WmKeyDown, clock);

        Assert.False(state.Observe(VkCapsLock, HoldKeyState.WmKeyUp, clock + 60)
            .HasFlag(HoldKeyTransition.SuppressMenu));
    }

    // ─── The chord guard (QA audit S1, 2026-08-21) ──────────────────
    //
    // Right Alt IS AltGr on non-US layouts, and every AltGr character is a
    // sub-300 ms "tap" of the trigger. A chorded hold — any other key down
    // while the trigger is held, or the AltGr-synthesised LCtrl arriving
    // just before the trigger — is typing, and the latch veto must see it.

    [Fact]
    public void A_clean_tap_is_not_a_chord()
    {
        var state = new HoldKeyState(VkF13);

        state.Observe(VkF13, HoldKeyState.WmKeyDown, nowMs: 1_000);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, nowMs: 1_080);

        Assert.False(state.LastHoldWasChord);
    }

    [Fact]
    public void Another_key_during_the_hold_marks_it_as_a_chord()
    {
        var state = new HoldKeyState(VkF13);

        state.Observe(VkF13, HoldKeyState.WmKeyDown, nowMs: 1_000);
        state.Observe(VkA, HoldKeyState.WmKeyDown, nowMs: 1_030);
        state.Observe(VkA, HoldKeyState.WmKeyUp, nowMs: 1_060);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, nowMs: 1_080);

        Assert.True(state.LastHoldWasChord);
    }

    [Fact]
    public void AltGr_synthesis_marks_the_hold_chorded_from_its_first_instant()
    {
        // Windows presses LEFT CTRL immediately before Right Alt on AltGr
        // layouts — even for a bare AltGr brush with no second key. That
        // pair must read as a chord.
        var state = new HoldKeyState(VkRightAlt);

        state.Observe(HoldKeyState.VkLeftControl, HoldKeyState.WmKeyDown, nowMs: 1_000);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, nowMs: 1_004);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, nowMs: 1_060);

        Assert.True(state.LastHoldWasChord);
    }

    [Fact]
    public void A_left_ctrl_long_before_the_press_does_not_taint_the_hold()
    {
        // A user who typed Ctrl+C two seconds ago and then taps the trigger
        // performed a clean gesture — only the synthesis window counts.
        var state = new HoldKeyState(VkRightAlt);

        state.Observe(HoldKeyState.VkLeftControl, HoldKeyState.WmKeyDown, nowMs: 1_000);
        state.Observe(HoldKeyState.VkLeftControl, HoldKeyState.WmKeyUp, nowMs: 1_050);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, nowMs: 3_000);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, nowMs: 3_080);

        Assert.False(state.LastHoldWasChord);
    }

    [Fact]
    public void The_chord_verdict_belongs_to_the_completed_hold_not_the_next_one()
    {
        var state = new HoldKeyState(VkF13);

        // Chorded hold…
        state.Observe(VkF13, HoldKeyState.WmKeyDown, nowMs: 1_000);
        state.Observe(VkA, HoldKeyState.WmKeyDown, nowMs: 1_030);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, nowMs: 1_080);
        Assert.True(state.LastHoldWasChord);

        // …then a clean one: the verdict resets with the new hold.
        state.Observe(VkF13, HoldKeyState.WmKeyDown, nowMs: 2_000);
        state.Observe(VkF13, HoldKeyState.WmKeyUp, nowMs: 2_080);
        Assert.False(state.LastHoldWasChord);
    }

    [Fact]
    public void The_injected_menu_suppression_key_does_not_chord_the_hold()
    {
        // QA verify NEW-10: the hook injects VK 0xE8 DURING an Alt hold to
        // break menu-mode arming. Our own injection must not read as the
        // user chording — on a zero-repeat-delay keyboard a deliberate
        // 250-300 ms tap otherwise self-vetoed its latch.
        var state = new HoldKeyState(VkRightAlt);

        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, nowMs: 1_000);
        state.Observe(HoldKeyState.VkMenuSuppression, HoldKeyState.WmKeyDown, nowMs: 1_020);
        state.Observe(HoldKeyState.VkMenuSuppression, HoldKeyState.WmKeyUp, nowMs: 1_021);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyUp, nowMs: 1_080);

        Assert.False(state.LastHoldWasChord);
    }
}
