namespace Dictation.Services.Tests;

/// <summary>
/// A key pressed while the hold key is held cancels that hold, when the
/// state machine is built to (the standalone Dictation app builds it so).
/// </summary>
public sealed class HoldKeyChordCancelTests
{
    private const int VkRightControl = 0xA3;
    private const int VkRightAlt = 0xA5;
    private const int VkC = 0x43;
    private const int VkE = 0x45;
    private const int Down = HoldKeyState.WmKeyDown;
    private const int Up = HoldKeyState.WmKeyUp;

    [Fact]
    public void Right_Ctrl_C_cancels_the_hold_and_the_held_key_cannot_restart_it()
    {
        var state = new HoldKeyState(VkRightControl, cancelOnChord: true);

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 0));
        Assert.True(state.IsHeldAt(10));

        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(VkC, Down, 40));
        Assert.False(state.IsHeldAt(41));

        // Ctrl is still down and autorepeating while the user copies. None of
        // it opens a microphone again.
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Down, 500));
        Assert.False(state.IsHeldAt(501));
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkC, Up, 510));
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkRightControl, Up, 900));
        Assert.True(state.LastHoldWasChord);

        // The next clean press is an ordinary hold.
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 2000));
        Assert.True(state.IsHeldAt(2001));
    }

    [Fact]
    public void One_chord_cancels_once()
    {
        var state = new HoldKeyState(VkRightControl, cancelOnChord: true);
        state.Observe(VkRightControl, Down, 0);

        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(VkC, Down, 40));
        Assert.Equal(HoldKeyTransition.None, state.Observe(VkC, Down, 540));
    }

    [Fact]
    public void Without_the_option_a_chord_only_marks_the_hold()
    {
        // The inherited behaviour, kept for every other caller: the chord
        // vetoes a latch and does not end the hold.
        var state = new HoldKeyState(VkRightControl);
        state.Observe(VkRightControl, Down, 0);

        Assert.Equal(HoldKeyTransition.None, state.Observe(VkC, Down, 40));
        Assert.True(state.IsHeldAt(41));
    }

    [Fact]
    public void A_key_down_with_the_trigger_at_rest_is_nothing()
    {
        var state = new HoldKeyState(VkRightControl, cancelOnChord: true);

        Assert.Equal(HoldKeyTransition.None, state.Observe(VkC, Down, 0));
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 10));
    }

    [Fact]
    public void The_injected_menu_phantom_is_not_a_chord()
    {
        var state = new HoldKeyState(VkRightAlt, cancelOnChord: true);
        state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, 0);

        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(HoldKeyState.VkMenuSuppression, Down, 500));
        Assert.True(state.IsHeldAt(501));
    }

    [Fact]
    public void AltGr_s_own_Left_Ctrl_does_not_cancel_a_Right_Alt_hold_but_a_letter_does()
    {
        // On an AltGr layout every Right Alt press and repeat comes with a
        // synthesised Left Ctrl press. That is the trigger, not a chord.
        var state = new HoldKeyState(VkRightAlt, cancelOnChord: true);
        state.Observe(HoldKeyState.VkLeftControl, Down, 0);
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightAlt, HoldKeyState.WmSysKeyDown, 2));

        Assert.Equal(HoldKeyTransition.None, state.Observe(HoldKeyState.VkLeftControl, Down, 530));
        Assert.True(state.IsHeldAt(531));

        // AltGr+E for the euro sign is typing.
        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(VkE, Down, 600));
        Assert.False(state.IsHeldAt(601));
    }

    [Fact]
    public void Left_Ctrl_does_cancel_a_hold_on_any_other_trigger()
    {
        var state = new HoldKeyState(VkRightControl, cancelOnChord: true);
        state.Observe(VkRightControl, Down, 0);

        Assert.Equal(
            HoldKeyTransition.CancelRequested,
            state.Observe(HoldKeyState.VkLeftControl, Down, 100));
    }

    [Fact]
    public void A_cancelled_hold_followed_by_Escape_still_lets_the_next_press_dictate()
    {
        // Escape's own arm drops the physical flag, so the trigger's key-up
        // that follows is ignored; the next press must still open a hold.
        var state = new HoldKeyState(VkRightControl, cancelOnChord: true) { DictationInFlight = true };
        state.Observe(VkRightControl, Down, 0);
        state.Observe(VkC, Down, 40);
        Assert.Equal(HoldKeyTransition.CancelRequested, state.Observe(HoldKeyState.VkEscape, Down, 60));
        state.Observe(VkRightControl, Up, 100);
        state.DictationInFlight = false;

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 1000));
        Assert.True(state.IsHeldAt(1001));
    }

    [Fact]
    public void Teardown_clears_a_cancelled_hold()
    {
        var state = new HoldKeyState(VkRightControl, cancelOnChord: true);
        state.Observe(VkRightControl, Down, 0);
        state.Observe(VkC, Down, 40);

        Assert.Equal(HoldKeyTransition.None, state.Release());
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Observe(VkRightControl, Down, 100));
    }
}
