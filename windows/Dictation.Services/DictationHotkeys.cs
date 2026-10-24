using Dictation.Speech;

namespace Dictation.Services;

/// <summary>
/// dictation M1 — the key dictation holds, and the keys a user may pick instead.
///
/// <para>
/// A separate type from <see cref="WorkflowHotkeys"/> rather than two more
/// members on it, because the two features are gated by different flags and
/// must be able to ship, break and be turned off independently. Nothing in
/// <c>Dictation.Speech</c> changes for this; the numbers below are
/// <em>read</em> from there so there is one copy of each <c>winuser.h</c>
/// constant in the tree rather than two that can drift.
/// </para>
///
/// <para>
/// It lives in the <c>net9.0</c> core for the same reason
/// <see cref="WorkflowHotkeys"/> does: "the dictation trigger is not the
/// push-to-talk chord" is a property worth proving, and it is provable by
/// arithmetic on a machine with no Windows in it. The Win32 half lives beside
/// the mechanism that needs it — dictation's is
/// <c>SetWindowsHookEx(WH_KEYBOARD_LL)</c> in
/// <c>Dictation.Infrastructure</c>, not workflows'
/// <c>RegisterHotKey</c>/<c>GetAsyncKeyState</c> pair, which cannot express a
/// held modifier at all.
/// </para>
/// </summary>
public static class DictationHotkeys
{
    /// <summary>
    /// The <c>RegisterHotKey</c> id reserved for dictation. <b>Nothing
    /// registers it.</b> The shipped trigger is a single held key, which
    /// <c>RegisterHotKey</c> cannot express, so dictation runs on a
    /// <c>WH_KEYBOARD_LL</c> hook and no <c>WM_HOTKEY</c> ever carries this
    /// value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reserved rather than deleted, so a later <c>RegisterHotKey</c> fallback
    /// has an id of its own: nothing in the app calls <c>RegisterHotKey</c>
    /// today, and <c>0x4A20</c>–<c>0x4A2F</c> is dictation's block.
    /// </para>
    /// <para>
    /// Do not put it in front of a user. It shipped on the settings pane as
    /// "Shortcut id", where it named a registration that does not exist — the
    /// one fact on that card that could not be checked against anything the app
    /// does, on a pane whose whole point is that every fact on it is measured.
    /// </para>
    /// </remarks>
    public const int HotkeyId = 0x4A20;

    /// <summary>
    /// Dictation's default hold trigger: <b>Right Ctrl, held</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not Right Alt, which is what the source app defaults to. Both products
    /// can be installed side by side, and the hook observes rather than
    /// swallows, so one key bound in both would start two dictations and type
    /// the text twice. Right Ctrl is none of the source app's default gestures
    /// (hold Right Alt; its double-tap toggle is off by default and rides the
    /// same key when on). The user can pick any of <see cref="HoldChoices"/>,
    /// and the window warns when the pick is the source app's default.
    /// </para>
    /// <para>
    /// A lone modifier is unreachable through <c>RegisterHotKey</c>, which
    /// requires a non-modifier virtual key, so this binding is served by
    /// <c>LowLevelHoldKeyRegistration</c>. A key pressed while it is held (the
    /// C of Right Ctrl+C) cancels that hold rather than dictating; see
    /// <c>HoldKeyState</c>.
    /// </para>
    /// <para>
    /// Modifiers are 0 deliberately. This is a single-key hold, and
    /// <c>LowLevelHoldKeyRegistration</c> reads only
    /// <see cref="HotkeyBinding.VirtualKey"/>; carrying stale modifier bits
    /// would make the record disagree with the behaviour.
    /// </para>
    /// </remarks>
    public static readonly HotkeyBinding PushToTalk = new(
        0,
        VkRightControl,
        "Hold Right Ctrl");

    /// <summary><c>VK_LSHIFT</c> — the left Shift key.</summary>
    public const uint VkLeftShift = 0xA0;

    /// <summary><c>VK_RMENU</c> — the right Alt key.</summary>
    public const uint VkRightAlt = 0xA5;

    /// <summary><c>VK_LMENU</c> — the left Alt key.</summary>
    public const uint VkLeftAlt = 0xA4;

    /// <summary><c>VK_RCONTROL</c>.</summary>
    public const uint VkRightControl = 0xA3;

    /// <summary><c>VK_RSHIFT</c>.</summary>
    public const uint VkRightShift = 0xA1;

    /// <summary><c>VK_F13</c> — the key many external keyboards map a spare
    /// thumb button to, and the only one here that no application already
    /// means something by.</summary>
    public const uint VkF13 = 0x7C;

    /// <summary><c>VK_F14</c>.</summary>
    public const uint VkF14 = 0x7D;

    /// <summary><c>VK_F15</c>.</summary>
    public const uint VkF15 = 0x7E;

    /// <summary><c>VK_F16</c>.</summary>
    public const uint VkF16 = 0x7F;

    /// <summary><c>VK_F17</c>.</summary>
    public const uint VkF17 = 0x80;

    /// <summary><c>VK_F18</c>.</summary>
    public const uint VkF18 = 0x81;

    /// <summary><c>VK_F19</c>.</summary>
    public const uint VkF19 = 0x82;

    /// <summary><c>VK_RCONTROL</c>'s left twin — offered, with a caution.</summary>
    public const uint VkLeftControl = 0xA2;

    /// <summary>
    /// <c>VK_RWIN</c> — the right Windows key.
    /// </summary>
    /// <remarks>
    /// Offerable only because <c>HoldKeyState.ArmsMenuMode</c> now covers the
    /// Windows keys as well as Alt. Without the phantom keystroke a lone Win
    /// tap opens the Start menu, which also STEALS FOCUS — so the inserter's
    /// Ctrl+V would land in the Start search box instead of the user's
    /// document.
    /// </remarks>
    public const uint VkRightWin = 0x5C;

    /// <summary><c>VK_SCROLL</c> — Scroll Lock.</summary>
    public const uint VkScrollLock = 0x91;

    /// <summary><c>VK_CAPITAL</c> — Caps Lock. See <see cref="CautionFor"/>.</summary>
    public const uint VkCapsLock = 0x14;

    /// <summary>
    /// The triggers a user may pick, in the order a settings picker should show
    /// them. Each is a single key that can be held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Still a list, not a recorder</b>, and the reasoning for that has not
    /// changed: a picker that accepted arbitrary chords would let someone
    /// choose a combination this mechanism cannot hold or that swallows a
    /// system shortcut, and the honest answer to "which keys work for
    /// hold-to-talk" is a list.
    /// </para>
    /// <para>
    /// <b>But that reasoning never justified FIVE.</b> A user whose Right Alt
    /// is AltGr on an international layout, or who has all five spoken for, had
    /// no binding available at all — the honest answer on Windows was "pick
    /// from these five or do not use the feature". Every key below is a single
    /// key the <c>WH_KEYBOARD_LL</c> hook can hold; widening the list needs no
    /// new mechanism.
    /// </para>
    /// <para>
    /// <b>What is deliberately NOT here: the mouse buttons.</b>
    /// <c>VK_XBUTTON1</c> and <c>VK_XBUTTON2</c> exist as virtual-key codes and
    /// look like the obvious way to give Windows macOS's mouse trigger — but a
    /// <c>WH_KEYBOARD_LL</c> hook never receives them. They arrive on
    /// <c>WH_MOUSE_LL</c>, which this feature does not install. Offering them
    /// would be offering a binding that can never fire, which is worse than not
    /// offering one: the user would conclude dictation is broken rather than
    /// that the key is unsupported. A mouse trigger needs a second hook — and
    /// it now HAS one: <c>LowLevelHoldKeyRegistration</c> installs
    /// <c>WH_MOUSE_LL</c> beside the keyboard hook when, and only when, the
    /// user has bound a button. The picker is separate
    /// (<c>DictationMouseButtons.Choices</c>) and so is the gesture
    /// (<c>MouseHoldOrClickState</c>, which latches on a single click because a
    /// spare side button is pressed for no other reason). The reasoning above
    /// still stands and is why the two lists stayed apart: a mouse button in
    /// THIS list would still be a binding that could never fire.
    /// </para>
    /// <para>
    /// Order is the picker's order and the default's preference, so the shipped
    /// trigger stays first and the keys most likely to be free follow it.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<HotkeyBinding> HoldChoices = new[]
    {
        PushToTalk,
        new HotkeyBinding(0, VkRightAlt, "Hold Right Alt"),
        new HotkeyBinding(0, VkLeftAlt, "Hold Left Alt"),
        new HotkeyBinding(0, VkRightShift, "Hold Right Shift"),
        new HotkeyBinding(0, VkLeftShift, "Hold Left Shift"),
        new HotkeyBinding(0, VkRightWin, "Hold Right Win"),
        new HotkeyBinding(0, VkLeftControl, "Hold Left Ctrl"),
        new HotkeyBinding(0, VkCapsLock, "Hold Caps Lock"),
        new HotkeyBinding(0, VkScrollLock, "Hold Scroll Lock"),
        new HotkeyBinding(0, VkF13, "Hold F13"),
        new HotkeyBinding(0, VkF14, "Hold F14"),
        new HotkeyBinding(0, VkF15, "Hold F15"),
        new HotkeyBinding(0, VkF16, "Hold F16"),
        new HotkeyBinding(0, VkF17, "Hold F17"),
        new HotkeyBinding(0, VkF18, "Hold F18"),
        new HotkeyBinding(0, VkF19, "Hold F19"),
    };

    /// <summary>
    /// What a user should know before choosing a particular key, or
    /// <c>null</c> when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// macOS's <c>HotkeyBinding.caution</c>, ported. The list above cannot be
    /// only keys with no cost — that is how it stayed at five — so the ones
    /// with a cost are offered WITH the cost stated, at the moment the user is
    /// choosing.
    /// </para>
    /// <para>
    /// Caps Lock's is the sharp one and it is not hypothetical: the hook
    /// observes rather than consumes, so a hold on Caps Lock is also a real
    /// Caps Lock press, and the lock is left ON afterwards. That is a fine
    /// trade for someone who has already disabled the key in Windows — a very
    /// common push-to-talk setup — and a nasty surprise for anyone who has not.
    /// </para>
    /// </remarks>
    public static string? CautionFor(uint virtualKey) => virtualKey switch
    {
        // The default, and it still has one: the key is not on every keyboard.
        VkRightControl =>
            "Many recent laptops have a Copilot key where Right Ctrl used to be. If yours "
            + "has no Right Ctrl, pick another key.",

        // Typing capitals is safe — those presses are cancelled by the letter
        // that follows — so the sentence names the case that is not.
        VkRightShift =>
            "Holding Right Shift on its own for more than a moment also starts Earlet "
            + "listening. Shift with another key, as in typing capitals, does not.",


        VkCapsLock =>
            "Holding Caps Lock also toggles Caps Lock — Earlet watches the key, it does not "
            + "swallow it. Pick this only if you have already turned Caps Lock off in Windows.",

        VkScrollLock =>
            "Holding Scroll Lock also toggles the Scroll Lock light. Almost nothing reads it, "
            + "so this is usually harmless.",

        VkLeftControl =>
            "Left Ctrl is half of most shortcuts you already use. Earlet does not swallow it: "
            + "Ctrl+C still copies, and the C cancels the recording, but a Ctrl+click held for a "
            + "moment still starts one.",

        VkLeftAlt =>
            "Left Alt is the one Alt+Tab and every menu mnemonic use. Right Alt is quieter.",

        VkRightWin =>
            "Earlet suppresses the Start menu while you hold this. Some keyboards have no "
            + "right Windows key at all — check yours before choosing it.",

        _ => null,
    };

    /// <summary>
    /// The keys offered for the DOUBLE-TAP toggle, which is a separate binding
    /// from the hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same list, plus a first entry meaning "the same key as the hold" —
    /// which is what Windows shipped and the only thing it could express.
    /// macOS has kept these apart from the beginning: <c>defaultHold</c> is
    /// Option-hold and <c>defaultDoubleTap</c> is Control-double-tap, two
    /// bindings a user holds in their head separately. Here, turning the toggle
    /// on CHANGED what the hold key did instead of adding a second gesture.
    /// </para>
    /// <para>
    /// <see cref="SameAsHold"/> is <c>0</c> so an absent setting means the
    /// shipped behaviour, exactly as an absent hold key means
    /// <see cref="PushToTalk"/>.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<HotkeyBinding> ToggleChoices =
        new[] { new HotkeyBinding(0, SameAsHold, "The same key as the hold") }
            .Concat(HoldChoices.Select(c =>
                new HotkeyBinding(0, c.VirtualKey, "Double-tap " + c.KeyName)))
            .ToArray();

    /// <summary>
    /// The toggle key value meaning "whatever the hold key is". Zero, so an
    /// unset preference is the shipped gesture.
    /// </summary>
    public const uint SameAsHold = 0;

    /// <summary>
    /// Coerce a saved toggle key to one this build offers, or to
    /// <see cref="SameAsHold"/>.
    /// </summary>
    /// <remarks>
    /// A key the hold is already bound to normalises to
    /// <see cref="SameAsHold"/> rather than being kept as a separate binding:
    /// one key cannot both start a hold and latch a toggle, and silently
    /// keeping both would give the user a state machine that behaves
    /// differently from what the two pickers say.
    /// </remarks>
    public static uint NormalizeToggle(uint toggleVirtualKey, uint holdVirtualKey)
    {
        if (toggleVirtualKey == SameAsHold || toggleVirtualKey == holdVirtualKey)
        {
            return SameAsHold;
        }

        foreach (var choice in HoldChoices)
        {
            if (choice.VirtualKey == toggleVirtualKey)
            {
                return toggleVirtualKey;
            }
        }

        return SameAsHold;
    }

    /// <summary>
    /// The developer override for the hold key: a virtual-key code, decimal or
    /// <c>0x</c>-prefixed hex, that must be one of <see cref="HoldChoices"/>.
    /// </summary>
    public const string HoldKeyEnvVar = "DICTATION_HOLD_KEY";

    /// <summary>
    /// The user's chosen trigger, or <see cref="PushToTalk"/>, reading the
    /// override from the process environment.
    /// </summary>
    public static HotkeyBinding ResolveHold(
        Action<string>? warn = null, uint preferredVirtualKey = 0) =>
        ResolveHold(warn, preferredVirtualKey, Environment.GetEnvironmentVariable(HoldKeyEnvVar));

    /// <summary>
    /// The user's chosen trigger, or <see cref="PushToTalk"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="preferredVirtualKey"/> is the saved choice, passed in
    /// because this project is platform-neutral and knows nothing about where
    /// settings live — the composition root owns persistence and this owns
    /// which keys are legal.
    /// </para>
    /// <para>
    /// <paramref name="environmentOverride"/> is the value of
    /// <see cref="HoldKeyEnvVar"/>, and it wins over the saved choice on
    /// purpose: a developer pointing at a specific key should not have to
    /// discover that a saved preference silently outranks them. An
    /// unrecognised value falls back rather than disabling dictation, and says
    /// so — a typo in an environment variable should not silently cost
    /// someone the feature.
    /// </para>
    /// </remarks>
    public static HotkeyBinding ResolveHold(
        Action<string>? warn, uint preferredVirtualKey, string? environmentOverride)
    {
        if (preferredVirtualKey != 0 && string.IsNullOrWhiteSpace(environmentOverride))
        {
            foreach (var choice in HoldChoices)
            {
                if (choice.VirtualKey == preferredVirtualKey)
                {
                    return choice;
                }
            }

            // Saved, but no longer offered — an older build's key, or a list
            // that shrank. Fall through to the default rather than registering
            // something this build does not support, and say so: silently
            // reverting a chosen trigger is how "my shortcut stopped working"
            // becomes unanswerable.
            warn?.Invoke(
                $"The saved dictation hold key 0x{preferredVirtualKey:X2} is not one this build "
                + $"offers; falling back to {PushToTalk.Display}.");
        }

        if (string.IsNullOrWhiteSpace(environmentOverride))
        {
            return PushToTalk;
        }

        var text = environmentOverride.Trim();
        var parsed = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hex)
                ? hex : 0u
            : uint.TryParse(text, out var dec) ? dec : 0u;

        foreach (var choice in HoldChoices)
        {
            if (choice.VirtualKey == parsed)
            {
                return choice;
            }
        }

        warn?.Invoke(
            $"{HoldKeyEnvVar}={environmentOverride} is not one of the supported hold keys; "
            + $"using {PushToTalk.Display}.");
        return PushToTalk;
    }
}
