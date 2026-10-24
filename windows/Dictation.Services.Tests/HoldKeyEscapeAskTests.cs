namespace Dictation.Services.Tests;

/// <summary>
/// The Escape that asks before it throws a long take away
/// (<see cref="HoldKeyState.EscapeAsksFirst"/>), which the Dictation app
/// raises while its take holds more than thirty seconds of speech (LIMIT
/// 2026-10-10). The app's own scenarios, through its controller, are in
/// Dictation.Core.Tests' TakeEscapeTests.
/// </summary>
public sealed class HoldKeyEscapeAskTests
{
    private const int VkRightControl = 0xA3;
    private const int Down = HoldKeyState.WmKeyDown;
    private const int Up = HoldKeyState.WmKeyUp;
    private const int Escape = HoldKeyState.VkEscape;

    private static HoldKeyState HandsFree() =>
        new(VkRightControl, HoldKeyTriggerMode.HoldOrDoubleTapHandsFree, cancelOnChord: true) { DictationInFlight = true };

    [Fact]
    public void Escape_asks_once_and_lowers_the_flag_so_the_next_cancels()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 0);
        state.EscapeAsksFirst = true;

        Assert.Equal(HoldKeyTransition.EscapeAsked, state.Observe(Escape, Down, 1_000));
        Assert.False(state.EscapeAsksFirst);
        Assert.Equal(HoldKeyTransition.None, state.Observe(Escape, Up, 1_050));

        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(Escape, Down, 2_000));
        Assert.False(state.IsHeldAt(2_001));
    }

    [Fact]
    public void An_autorepeat_of_the_asking_Escape_discards_nothing()
    {
        // Held, Escape repeats after about half a second with no key-up in
        // between: that is the same slip, not an answer. Only a new press
        // discards.
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 0);
        state.EscapeAsksFirst = true;

        Assert.Equal(HoldKeyTransition.EscapeAsked, state.Observe(Escape, Down, 1_000));
        Assert.Equal(HoldKeyTransition.None, state.Observe(Escape, Down, 1_500));
        Assert.Equal(HoldKeyTransition.None, state.Observe(Escape, HoldKeyState.WmSysKeyDown, 1_533));
        Assert.True(state.IsHeldAt(1_534));
        Assert.Equal(HoldKeyTransition.None, state.Observe(Escape, Up, 1_600));

        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(Escape, Down, 2_000));
        Assert.False(state.IsHeldAt(2_001));
    }

    [Fact]
    public void An_asking_Escape_abandons_no_held_key()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 0);
        state.EscapeAsksFirst = true;

        state.Observe(Escape, Down, 1_000);
        state.Observe(Escape, Up, 1_050);

        Assert.True(state.IsHeldAt(1_100));
        // Its autorepeats are the same hold, and its release ends it.
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Down, 1_200));
        Assert.Equal(HoldKeyTransition.HoldEnded, state.Observe(VkRightControl, Up, 1_500));
    }

    [Fact]
    public void An_asking_Escape_ends_no_latch()
    {
        var state = HandsFree();
        state.Observe(VkRightControl, Down, 1_000);
        state.Observe(VkRightControl, Up, 1_060);
        state.Observe(VkRightControl, Down, 1_160);
        state.Observe(VkRightControl, Up, 1_220);
        Assert.True(state.IsLatched);
        state.EscapeAsksFirst = true;

        Assert.Equal(HoldKeyTransition.EscapeAsked, state.Observe(Escape, Down, 5_000));

        Assert.True(state.IsLatched);
        Assert.True(state.IsHeldAt(5_001));
        Assert.Equal(LatchEnd.None, state.ConsumeLatchEnd());
        // A second press, after the first one's key-up.
        state.Observe(Escape, Up, 5_050);
        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(Escape, Down, 6_000));
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void The_flag_is_off_unless_raised_and_means_nothing_without_a_dictation()
    {
        var state = HandsFree();
        Assert.False(state.EscapeAsksFirst);
        state.Observe(VkRightControl, Down, 0);
        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(Escape, Down, 1_000));

        var idle = new HoldKeyState(VkRightControl, HoldKeyTriggerMode.HoldOrDoubleTapHandsFree) { EscapeAsksFirst = true };
        Assert.Equal(HoldKeyTransition.None, idle.Observe(Escape, Down, 1_000));
        Assert.True(idle.EscapeAsksFirst);
    }
}
