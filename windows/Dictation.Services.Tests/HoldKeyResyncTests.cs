namespace Dictation.Services.Tests;

/// <summary>
/// <see cref="HoldKeyState.Resync"/>: putting the hook's view of the trigger
/// right after it missed an edge, which the hook watch does before it hooks
/// the keyboard again. A release it never saw is replayed; a press it never
/// saw is adopted as holding nothing, never replayed into a hold that would
/// start in the middle of what the user is saying.
/// </summary>
public sealed class HoldKeyResyncTests
{
    private const int VkRightControl = 0xA3;
    private const int VkC = 0x43;
    private const int Down = HoldKeyState.WmKeyDown;
    private const int Up = HoldKeyState.WmKeyUp;
    private const int Window = HoldKeyState.HandsFreeWindowMs;

    private static HoldKeyState HandsFree() =>
        new(VkRightControl, HoldKeyTriggerMode.HoldOrDoubleTapHandsFree, cancelOnChord: true);

    private static HoldKeyState HoldOnly() => new(VkRightControl, HoldKeyTriggerMode.HoldOnly, cancelOnChord: true);

    public static TheoryData<HoldKeyTriggerMode> Modes => new()
    {
        HoldKeyTriggerMode.HoldOnly,
        HoldKeyTriggerMode.HoldOrDoubleTapToggle,
        HoldKeyTriggerMode.HoldOrDoubleTapHandsFree,
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public void Views_that_already_agree_change_nothing(HoldKeyTriggerMode mode)
    {
        var state = new HoldKeyState(VkRightControl, mode, cancelOnChord: true);
        Assert.Equal(HoldKeyTransition.None, state.Resync(keyDown: false, 1_000));
        Assert.False(state.IsPhysicallyDown);

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 2_000));
        Assert.Equal(HoldKeyTransition.None, state.Resync(keyDown: true, 3_000));
        Assert.True(state.IsHeldAt(3_000));
        Assert.True(state.IsPhysicallyDown);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void A_missed_release_ends_a_long_hold_as_the_release_would(HoldKeyTriggerMode mode)
    {
        var state = new HoldKeyState(VkRightControl, mode, cancelOnChord: true);
        state.Observe(VkRightControl, Down, 1_000);
        Assert.True(state.IsHeldAt(5_000));

        var transition = state.Resync(keyDown: false, 5_000);

        Assert.True((transition & HoldKeyTransition.HoldEnded) != 0, transition.ToString());
        Assert.False(state.IsPhysicallyDown);
        Assert.False(state.IsHeldAt(5_001));
    }

    [Fact]
    public void In_the_hands_free_mode_a_missed_release_soon_after_the_press_parks_as_a_short_tap_does()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 1_000);

        Assert.Equal(HoldKeyTransition.None, state.Resync(keyDown: false, 1_100));
        Assert.True(state.IsHeldAt(1_000 + Window));
        Assert.False(state.IsHeldAt(1_000 + Window + 1));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void A_missed_press_is_adopted_and_holds_nothing_through_its_repeats_and_its_release(HoldKeyTriggerMode mode)
    {
        var state = new HoldKeyState(VkRightControl, mode, cancelOnChord: true);

        Assert.Equal(HoldKeyTransition.None, state.Resync(keyDown: true, 1_000));

        Assert.True(state.IsPhysicallyDown);
        Assert.False(state.IsHeldAt(1_000));
        // Its autorepeats open nothing, a chord on it cancels nothing...
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Down, 1_500));
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkC, Down, 1_600));
        Assert.False(state.IsHeldAt(1_600));
        // ...and its release ends nothing.
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 2_000));
        Assert.False(state.IsPhysicallyDown);
        Assert.False(state.IsHeldAt(2_000));

        // The next press is an ordinary one.
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 5_000));
        Assert.True(state.IsHeldAt(5_000));
    }

    [Fact]
    public void An_adopted_press_is_never_the_first_half_of_a_double_tap()
    {
        var state = HandsFree();
        // A tap the hook heard, waiting for its second half...
        state.Observe(VkRightControl, Down, 1_000);
        state.Observe(VkRightControl, Up, 1_060);
        // ...then a press it missed, adopted, and released.
        state.Resync(keyDown: true, 1_100);
        state.Observe(VkRightControl, Up, 1_150);

        // A press still inside the first tap's window does not pair with it:
        // the adopted press in between ended the pair.
        state.Observe(VkRightControl, Down, 1_300);
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_latch_keeps_running_through_an_adopted_press_and_the_next_press_stops_it()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 1_000);
        state.Observe(VkRightControl, Up, 1_060);
        state.Observe(VkRightControl, Down, 1_160);
        state.Observe(VkRightControl, Up, 1_220);
        Assert.True(state.IsLatched);

        // The stopping press the dead hook missed: adopted, not replayed.
        Assert.Equal(HoldKeyTransition.None, state.Resync(keyDown: true, 9_000));
        Assert.True(state.IsLatched);
        Assert.True(state.IsHeldAt(9_000));
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 9_100));
        Assert.True(state.IsLatched);

        // The next press, heard, stops it.
        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkRightControl, Down, 10_000));
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_release_missed_after_an_adopted_press_is_put_right_quietly()
    {
        var state = HoldOnly();
        state.Resync(keyDown: true, 1_000);

        Assert.Equal(HoldKeyTransition.None, state.Resync(keyDown: false, 2_000));
        Assert.False(state.IsPhysicallyDown);
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 3_000));
    }
}
