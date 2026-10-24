using Dictation.Services;
using Dictation.Speech;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The keys a user may bind, and the two things this build says about them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Five was not a policy, it was a list nobody had grown.</b> The doc
/// comment argued for a list over a chord recorder — "a picker that accepted
/// arbitrary chords would let someone choose a combination this mechanism
/// cannot hold" — which is sound and never justified the number. A user whose
/// Right Alt is AltGr on an international layout, or who has all five spoken
/// for, had no binding available at all.
/// </para>
/// <para>
/// The list could only be widened honestly by also porting macOS's
/// per-combination caution copy, because the keys worth adding are exactly the
/// ones with a cost.
/// </para>
/// </remarks>
public sealed class DictationHotkeysTests
{
    [Fact]
    public void The_shipped_trigger_is_still_first()
    {
        // Order is the picker's order AND the default's preference. Widening
        // the list must not move what a fresh install claims.
        //
        // Dictation's default is Right Ctrl, not the source app's Right Alt:
        // both products can be installed, and a key bound in both starts two
        // dictations. Keep this assertion concrete so a future default change
        // cannot hide behind PushToTalk moving with it.
        Assert.Equal(DictationHotkeys.PushToTalk, DictationHotkeys.HoldChoices[0]);
        Assert.Equal(DictationHotkeys.VkRightControl, DictationHotkeys.HoldChoices[0].VirtualKey);
        Assert.Contains(
            DictationHotkeys.HoldChoices,
            c => c.VirtualKey == DictationHotkeys.VkRightAlt);
        Assert.Contains(
            DictationHotkeys.HoldChoices,
            c => c.VirtualKey == DictationHotkeys.VkRightShift);
    }

    [Fact]
    public void Right_shift_states_its_one_rough_edge()
    {
        // A 300 ms floor means shifted capitals never open the microphone, but
        // holding Shift to extend a selection does. Keep that warning when
        // Right Shift remains an explicit user choice rather than the default.
        Assert.False(string.IsNullOrWhiteSpace(
            DictationHotkeys.CautionFor(DictationHotkeys.VkRightShift)));
    }

    [Fact]
    public void There_are_more_than_the_original_five()
    {
        // The gap, stated as a number. Not a target — a floor, so a future
        // shrink back to "pick from these five or do not use the feature" is a
        // red run rather than a quiet regression.
        Assert.True(
            DictationHotkeys.HoldChoices.Count > 5,
            $"only {DictationHotkeys.HoldChoices.Count} hold keys are offered");
    }

    [Fact]
    public void Every_offered_key_is_distinct()
    {
        // Two rows with one virtual key would make the picker's selection
        // ambiguous and ResolveHold's walk order load-bearing in a way nothing
        // documents.
        var keys = DictationHotkeys.HoldChoices.Select(c => c.VirtualKey).ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Every_offered_key_is_a_single_key_with_no_modifiers()
    {
        // This mechanism is a single held key. A binding carrying modifier bits
        // would make the record disagree with the behaviour — the hook reads
        // only VirtualKey.
        foreach (var choice in DictationHotkeys.HoldChoices)
        {
            Assert.Equal(0u, choice.Modifiers);
            Assert.NotEqual(0u, choice.VirtualKey);
        }
    }

    [Fact]
    public void The_mouse_buttons_are_deliberately_absent()
    {
        // VK_XBUTTON1 and VK_XBUTTON2 exist as virtual-key codes and look like
        // the obvious way to give Windows macOS's mouse trigger — but a
        // WH_KEYBOARD_LL hook never receives them; they arrive on WH_MOUSE_LL,
        // which this feature does not install. Offering one would be offering a
        // binding that can never fire, and a user would read that as dictation
        // being broken rather than as the key being unsupported.
        foreach (var mouseVk in new uint[] { 0x05, 0x06 })
        {
            Assert.DoesNotContain(
                DictationHotkeys.HoldChoices, c => c.VirtualKey == mouseVk);
        }
    }

    [Fact]
    public void Escape_is_not_offered_as_a_trigger()
    {
        // It is the cancel key. A key cannot both start a dictation and abandon
        // one — HoldKeyState resolves that in the trigger's favour, so offering
        // it would silently remove the way out.
        Assert.DoesNotContain(
            DictationHotkeys.HoldChoices, c => c.VirtualKey == HoldKeyState.VkEscape);
    }

    // ─── Caution copy ───────────────────────────────────────────────────

    [Fact]
    public void The_keys_with_a_real_cost_say_so()
    {
        // Caps Lock is the sharp one and it is not hypothetical: the hook
        // observes rather than consumes, so a hold on Caps Lock is also a real
        // Caps Lock press and the lock is left ON afterwards.
        var caps = DictationHotkeys.CautionFor(DictationHotkeys.VkCapsLock);

        Assert.False(string.IsNullOrWhiteSpace(caps));
        Assert.Contains("Caps Lock", caps, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DictationHotkeys.VkCapsLock)]
    [InlineData(DictationHotkeys.VkScrollLock)]
    [InlineData(DictationHotkeys.VkLeftControl)]
    [InlineData(DictationHotkeys.VkLeftAlt)]
    [InlineData(DictationHotkeys.VkRightWin)]
    public void Every_key_added_beyond_the_quiet_ones_carries_a_caution(uint vk)
    {
        // Both halves, and the second is what makes this a test of the LIST
        // rather than of a switch statement: a caution for a key the picker
        // does not offer is dead copy, and a key removed from the picker while
        // its caution stays is a widening quietly rolled back.
        Assert.Contains(DictationHotkeys.HoldChoices, c => c.VirtualKey == vk);
        Assert.False(string.IsNullOrWhiteSpace(DictationHotkeys.CautionFor(vk)));
    }

    [Fact]
    public void The_default_says_it_may_not_exist_on_this_keyboard()
    {
        // Many 2024+ Windows laptops replace Right Ctrl with the Copilot key
        // (a hardware fact from vendor sources, not measured here). The
        // default is the one key every user sees the caution for, which is
        // what sends someone without one to the picker.
        var caution = DictationHotkeys.CautionFor(DictationHotkeys.VkRightControl);

        Assert.False(string.IsNullOrWhiteSpace(caution));
        Assert.Contains("Copilot", caution, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DictationHotkeys.VkRightAlt)]
    // Left Shift is quiet and Right Shift is not, which looks backwards until
    // you see which one is the DEFAULT: the caution belongs to the binding
    // nobody chose. Somebody who goes and picks Shift has chosen Shift.
    [InlineData(DictationHotkeys.VkLeftShift)]
    [InlineData(DictationHotkeys.VkF13)]
    [InlineData(DictationHotkeys.VkF19)]
    public void A_quiet_key_says_nothing(uint vk)
    {
        // An ordinary choice must gain no extra line. A caution on every row is
        // a caution nobody reads.
        Assert.Null(DictationHotkeys.CautionFor(vk));
    }

    // ─── The toggle's own key ───────────────────────────────────────────

    [Fact]
    public void The_toggle_list_opens_with_the_shipped_behaviour()
    {
        // "The same key as the hold" is what Windows shipped and the only thing
        // it could express, so it stays first and it is the zero value.
        Assert.Equal(
            DictationHotkeys.SameAsHold, DictationHotkeys.ToggleChoices[0].VirtualKey);
        Assert.Equal(0u, DictationHotkeys.SameAsHold);
    }

    [Fact]
    public void Every_hold_key_can_also_be_the_toggle_key()
    {
        foreach (var hold in DictationHotkeys.HoldChoices)
        {
            Assert.Contains(
                DictationHotkeys.ToggleChoices, c => c.VirtualKey == hold.VirtualKey);
        }
    }

    [Fact]
    public void A_toggle_bound_to_the_hold_key_collapses_to_the_shipped_gesture()
    {
        // One key cannot both start a hold and latch a toggle. Keeping both
        // would give the user a state machine that behaves differently from
        // what the two pickers say — HoldKeyState resolves it in the trigger's
        // favour, so the toggle picker would be showing a binding the hook does
        // not have.
        Assert.Equal(
            DictationHotkeys.SameAsHold,
            DictationHotkeys.NormalizeToggle(
                DictationHotkeys.VkRightAlt, DictationHotkeys.VkRightAlt));
    }

    [Fact]
    public void A_toggle_on_a_different_offered_key_survives()
    {
        Assert.Equal(
            DictationHotkeys.VkF13,
            DictationHotkeys.NormalizeToggle(
                DictationHotkeys.VkF13, DictationHotkeys.VkRightAlt));
    }

    [Theory]
    [InlineData(0x99u)]   // never offered
    [InlineData(0x00u)]   // the sentinel itself
    public void An_unknown_toggle_key_degrades_to_the_shipped_gesture(uint vk)
    {
        // A hand-edited file, or a choice made by a newer build and read after
        // a downgrade. Degrade rather than bind something this build cannot
        // hold — the same posture the hold key's own normaliser takes.
        Assert.Equal(
            DictationHotkeys.SameAsHold,
            DictationHotkeys.NormalizeToggle(vk, DictationHotkeys.VkRightAlt));
    }

    [Fact]
    public void Resolving_a_newly_offered_key_actually_returns_it()
    {
        // The end of the widening: a key in the list has to survive
        // ResolveHold, or the picker offers something the hook never claims.
        foreach (var choice in DictationHotkeys.HoldChoices)
        {
            var resolved = DictationHotkeys.ResolveHold(null, choice.VirtualKey, environmentOverride: null);
            Assert.Equal(choice.VirtualKey, resolved.VirtualKey);
        }
    }

    // ─── The override ───────────────────────────────────────────────────

    [Fact]
    public void The_override_has_one_prefix()
    {
        Assert.Equal("DICTATION_HOLD_KEY", DictationHotkeys.HoldKeyEnvVar);
    }

    [Theory]
    [InlineData("0x7C")]
    [InlineData("124")]
    [InlineData(" 0x7c ")]
    public void The_override_wins_over_the_saved_key(string raw)
    {
        var resolved = DictationHotkeys.ResolveHold(null, DictationHotkeys.VkRightAlt, raw);

        Assert.Equal(DictationHotkeys.VkF13, resolved.VirtualKey);
    }

    [Fact]
    public void An_override_that_is_not_offered_falls_back_and_says_so()
    {
        var warnings = new List<string>();

        var resolved = DictationHotkeys.ResolveHold(warnings.Add, 0, "0x41");

        Assert.Equal(DictationHotkeys.PushToTalk, resolved);
        Assert.Contains(warnings, w => w.Contains("DICTATION_HOLD_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void A_saved_key_this_build_does_not_offer_falls_back_and_says_so()
    {
        var warnings = new List<string>();

        var resolved = DictationHotkeys.ResolveHold(warnings.Add, 0x41, environmentOverride: null);

        Assert.Equal(DictationHotkeys.PushToTalk, resolved);
        Assert.Single(warnings);
    }
}
