namespace Dictation.Services.Tests;

/// <summary>
/// External cancellation must end only the latch, leaving input edges usable:
/// <see cref="HoldKeyState.EndLatch"/> in the toggle mode and
/// <see cref="MouseHoldOrClickState.EndLatch"/>. Ported from the unmerged
/// Windows latch branch in the latch regression; the hands-free mode's own cases are in
/// <see cref="HoldKeyHandsFreeTests"/>.
/// </summary>
public sealed class ExternalLatchEndTests
{
    private const int VkF13 = 0x7C;

    [Fact]
    public void Ending_a_keyboard_latch_clears_pending_grace_and_requires_a_fresh_double_tap()
    {
        var state = new HoldKeyState(VkF13, HoldKeyTriggerMode.HoldOrDoubleTapToggle);
        long clock = 1_000;

        Tap(state, ref clock);
        clock += 100;
        Tap(state, ref clock);
        Assert.True(state.IsLatched);

        // Begin another possible double-tap, then cancel it from outside the
        // input stream. The old grace must not survive the cancellation.
        clock += 100;
        Tap(state, ref clock);
        Assert.True(state.IsHeldAt(clock + 1));

        state.EndLatch();

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeldAt(clock + 1));

        // This tap is inside the old grace window, but must be treated as the
        // first tap of a new sequence rather than completing stale state.
        clock += 100;
        Tap(state, ref clock);
        Assert.False(state.IsLatched);

        // A complete, new double-tap still works after external cancellation.
        clock += HoldKeyState.DoubleTapGapMs + 1;
        Tap(state, ref clock);
        clock += 100;
        Tap(state, ref clock);
        Assert.True(state.IsLatched);
    }

    [Fact]
    public void Ending_a_keyboard_latch_does_not_swallow_the_physical_key_up()
    {
        var state = new HoldKeyState(VkF13, HoldKeyTriggerMode.HoldOrDoubleTapToggle);

        long clock = 1_000;
        Tap(state, ref clock);
        clock += 100;
        Tap(state, ref clock);
        Assert.True(state.IsLatched);

        Assert.Equal(
            HoldKeyTransition.None,
            state.Observe(VkF13, HoldKeyState.WmKeyDown, 2_000));

        state.EndLatch();

        Assert.Equal(
            HoldKeyTransition.HoldEnded,
            state.Observe(VkF13, HoldKeyState.WmKeyUp, 2_000 + HoldKeyState.TapMaxMs + 1));
        Assert.False(state.IsHeldAt(3_000));
    }

    [Fact]
    public void Ending_a_mouse_latch_clears_it_and_a_new_click_can_latch_again()
    {
        var state = new MouseHoldOrClickState();

        state.Down(1_000);
        Assert.Equal(HoldKeyTransition.None, state.Up(1_100));
        Assert.True(state.IsLatched);

        state.EndLatch();

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeld);

        state.Down(2_000);
        Assert.Equal(HoldKeyTransition.None, state.Up(2_100));
        Assert.True(state.IsLatched);
    }

    [Fact]
    public void Cancelling_during_a_short_press_does_not_make_its_release_a_new_first_tap()
    {
        var state = new HoldKeyState(VkF13, HoldKeyTriggerMode.HoldOrDoubleTapToggle);
        state.Observe(VkF13, HoldKeyState.WmKeyDown, 1_000);
        state.EndLatch();
        state.Observe(VkF13, HoldKeyState.WmKeyUp, 1_050);
        Assert.False(state.IsHeldAt(1_050));
        long clock = 1_100;
        Tap(state, ref clock);
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void Cancelling_during_a_short_mouse_press_does_not_latch_on_release()
    {
        var state = new MouseHoldOrClickState();
        state.Down(1_000);
        state.EndLatch();
        state.Up(1_050);
        Assert.False(state.IsLatched);
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void Ending_a_mouse_latch_does_not_swallow_the_physical_button_up()
    {
        var state = new MouseHoldOrClickState();

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Down(1_000));
        state.EndLatch();

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Up(1_000 + DictationMouseButtons.ClickMilliseconds + 1));
        Assert.False(state.IsHeld);
    }

    private static void Tap(HoldKeyState state, ref long clock)
    {
        state.Observe(VkF13, HoldKeyState.WmKeyDown, clock);
        clock += 50;
        state.Observe(VkF13, HoldKeyState.WmKeyUp, clock);
    }
}
