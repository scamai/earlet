using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Infrastructure.Tests;

/// <summary>
/// The hook path with the chord cancel on: a chord reaches the cancel latch
/// the controller polls, and the key reads as up.
/// </summary>
public sealed class LowLevelHoldKeyChordTests
{
    private const int VkC = 0x43;

    private static LowLevelHoldKeyRegistration Build(bool cancelOnChord) =>
        new(
            new HotkeyBinding(0, DictationHotkeys.VkRightControl, "Hold Right Ctrl"),
            NullLogger<LowLevelHoldKeyRegistration>.Instance,
            cancelOnChord: cancelOnChord);

    [Fact]
    public void A_chord_latches_one_cancel_and_releases_the_key()
    {
        using var registration = Build(cancelOnChord: true);

        registration.ObserveKeyboard((int)DictationHotkeys.VkRightControl, HoldKeyState.WmKeyDown, 0);
        Assert.True(registration.IsKeyDown());

        registration.ObserveKeyboard(VkC, HoldKeyState.WmKeyDown, 40);

        Assert.False(registration.IsKeyDown());
        Assert.True(registration.ConsumeCancelRequest());
        Assert.False(registration.ConsumeCancelRequest());
    }

    [Fact]
    public void The_latch_says_a_chord_ended_the_hold_while_the_key_is_still_down()
    {
        // The window names the key that was used in a shortcut. The hold's own
        // chord verdict (LastHoldWasChord) is only published at the trigger's
        // key-up, so the latch has to carry it.
        using var registration = Build(cancelOnChord: true);

        registration.ObserveKeyboard((int)DictationHotkeys.VkRightControl, HoldKeyState.WmKeyDown, 0);
        registration.ObserveKeyboard(VkC, HoldKeyState.WmKeyDown, 40);

        Assert.True(registration.ConsumeCancelRequest(out var byChord));
        Assert.True(byChord);
        Assert.False(registration.LastHoldWasChord);
    }

    [Fact]
    public void Escape_on_a_running_dictation_is_not_reported_as_a_chord()
    {
        using var registration = Build(cancelOnChord: true);
        registration.DictationInFlight = true;

        registration.ObserveKeyboard((int)DictationHotkeys.VkRightControl, HoldKeyState.WmKeyDown, 0);
        registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 40);

        Assert.True(registration.ConsumeCancelRequest(out var byChord));
        Assert.False(byChord);
        Assert.False(registration.ConsumeCancelRequest(out _));
    }

    [Fact]
    public void Without_the_option_the_hook_keeps_the_inherited_behaviour()
    {
        using var registration = Build(cancelOnChord: false);

        registration.ObserveKeyboard((int)DictationHotkeys.VkRightControl, HoldKeyState.WmKeyDown, 0);
        registration.ObserveKeyboard(VkC, HoldKeyState.WmKeyDown, 40);

        Assert.True(registration.IsKeyDown());
        Assert.False(registration.ConsumeCancelRequest());
    }
}
