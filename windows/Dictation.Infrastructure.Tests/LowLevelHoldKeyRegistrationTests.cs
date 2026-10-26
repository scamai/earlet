using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Infrastructure.Tests;

/// <summary>
/// The half of <see cref="LowLevelHoldKeyRegistration"/> that can be held to
/// account without a live input stack.
/// </summary>
/// <remarks>
/// <para>
/// This file exists because that class had <b>zero</b> references from any test
/// in the tree — the only files naming it were itself, <c>DictationHotkeyHost</c>,
/// <c>DictationSettingsViewModel</c> and three docs — while being the layer
/// every dictation begins at.
/// </para>
/// <para>
/// <b>What is testable here and what is not.</b> <c>Register</c> is
/// <c>SetWindowsHookEx</c> and needs a message pump on the calling thread;
/// <c>HookProc</c> is only ever called by Windows, from inside the system input
/// path; the phantom keystroke is <c>SendInput</c> into a live session. None of
/// those is reachable from a test host, and none of them is where the decisions
/// live any more — <see cref="HoldKeyState"/> is, in the portable project,
/// with <c>HoldKeyStateTests</c> driving every branch. What is left here is the
/// wiring, and the wiring is what this asserts: that the binding actually
/// reaches the state machine, that a binding which can never fire is refused at
/// construction rather than silently arming nothing, and that teardown closes
/// an open hold.
/// </para>
/// </remarks>
public sealed class LowLevelHoldKeyRegistrationTests
{
    private static LowLevelHoldKeyRegistration Build(uint virtualKey) =>
        new(
            new HotkeyBinding(0, virtualKey, "Hold Something"),
            NullLogger<LowLevelHoldKeyRegistration>.Instance);

    [Fact]
    public void A_binding_with_no_virtual_key_is_refused_at_construction()
    {
        // Refused rather than accepted-and-never-fired. A registration that
        // installs a hook and then matches nothing looks exactly like a hook
        // Windows dropped, and the two need completely different responses.
        var ex = Assert.Throws<ArgumentException>(() => Build(0));
        Assert.Equal("binding", ex.ParamName);
    }

    [Fact]
    public void A_fresh_registration_reports_the_key_up()
    {
        using var registration = Build(DictationHotkeys.VkRightAlt);
        Assert.False(registration.IsKeyDown());
    }

    [Fact]
    public void The_binding_it_was_given_is_the_binding_it_reports()
    {
        // Every surface that tells a user which key to hold reads this. A
        // registration that reported the default while listening for something
        // else is the defect the Binding property's own remarks describe.
        var binding = new HotkeyBinding(0, DictationHotkeys.VkF13, "Hold F13");
        using var registration = new LowLevelHoldKeyRegistration(
            binding, NullLogger<LowLevelHoldKeyRegistration>.Instance);

        Assert.Same(binding, registration.Binding);
    }

    [Fact]
    public void Unregister_on_a_registration_that_never_registered_is_a_no_op()
    {
        // Dispose calls Unregister, and Dispose runs on the teardown path
        // whether or not the hook was ever installed — on a machine where
        // Windows refused it, for instance.
        using var registration = Build(DictationHotkeys.VkRightAlt);

        registration.Unregister();
        registration.Unregister();

        Assert.False(registration.IsKeyDown());
    }

    [Fact]
    public void A_hook_that_was_never_installed_claims_to_have_seen_nothing()
    {
        // The counter behind the deafness warning. It must start at zero and
        // stay there without a hook, because the whole value of "observed 0
        // keyboard events" is that it is a count and not a guess — a counter
        // that could be non-zero without Windows having delivered anything
        // would make the one line that separates "the hook is deaf" from "the
        // user pressed a different key" unreliable in the direction that
        // matters.
        //
        // The WARNING itself is gated on how long the hook was armed, and that
        // gate is not reachable from a test host: the log lines sit behind
        // Unregister's installed-hook guard, and installing one needs a message
        // pump in a live session. It exists because the first draft fired on
        // this shell's own startup arm/disarm/re-arm — 176 ms on the device —
        // which was a false alarm in a change whose entire purpose is to not
        // add noise. The evidence for the gate is the device trace, not this
        // file, and saying so is better than a test that asserts nothing.
        using var registration = Build(DictationHotkeys.VkRightAlt);

        Assert.Equal(0, registration.ObservedKeyEvents);

        registration.Unregister();

        Assert.Equal(0, registration.ObservedKeyEvents);
    }

    [Fact]
    public void Disposing_twice_is_safe()
    {
        var registration = Build(DictationHotkeys.VkRightControl);
        registration.Dispose();
        registration.Dispose();
    }

    [Fact]
    public void Registering_after_disposal_is_refused_rather_than_installing_a_hook()
    {
        var registration = Build(DictationHotkeys.VkRightShift);
        registration.Dispose();

        Assert.Throws<ObjectDisposedException>(() => registration.Register());
    }

    [Theory]
    // Every trigger the picker offers, so a key added to DictationHotkeys
    // without a thought for the menu-mode branch fails here rather than in a
    // user's document.
    [InlineData(DictationHotkeys.VkRightAlt)]
    [InlineData(DictationHotkeys.VkLeftAlt)]
    [InlineData(DictationHotkeys.VkRightControl)]
    [InlineData(DictationHotkeys.VkRightShift)]
    [InlineData(DictationHotkeys.VkF13)]
    public void Every_offered_trigger_builds_a_registration_that_starts_at_rest(uint vk)
    {
        using var registration = Build(vk);
        Assert.False(registration.IsKeyDown());
    }

    // ─── The Escape cancel latch ────────────────────────────────────────

    [Fact]
    public void A_fresh_registration_has_no_cancel_pending()
    {
        // Zero deliberately: a latch that read true before any Escape was
        // pressed would cancel the first dictation of every run.
        using var registration = Build(DictationHotkeys.VkRightAlt);

        Assert.False(registration.ConsumeCancelRequest());
    }

    [Fact]
    public void Dictation_in_flight_starts_false_and_round_trips()
    {
        // False at construction is the property that keeps Escape inert for a
        // caller that has never heard of this seam. The shell sets it from the
        // session's own phase; nothing else may.
        using var registration = Build(DictationHotkeys.VkRightAlt);

        Assert.False(registration.DictationInFlight);
        registration.DictationInFlight = true;
        Assert.True(registration.DictationInFlight);
    }

    [Fact]
    public void An_asking_Escape_latches_as_asked_and_never_as_a_cancel()
    {
        // LIMIT-WIN-A: the Dictation app reads the asked latch with the
        // two-argument read; a caller that knows only the one-argument read
        // sees nothing to cancel.
        using var registration = Build(DictationHotkeys.VkRightControl);
        registration.DictationInFlight = true;
        registration.EscapeAsksFirst = true;

        Assert.Equal(
            HoldKeyTransition.EscapeAsked,
            registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 1_000));
        Assert.False(registration.EscapeAsksFirst);
        Assert.True(registration.ConsumeCancelRequest(out var byChord, out var asked));
        Assert.False(byChord);
        Assert.True(asked);
        Assert.False(registration.ConsumeCancelRequest(out _, out _));

        // Each Escape below is its own press, after the last one's key-up:
        // an autorepeat of the asking one means nothing (HoldKeyState.EscapeAsk.cs).
        registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyUp, 1_050);
        registration.EscapeAsksFirst = true;
        registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 2_000);
        Assert.False(registration.ConsumeCancelRequest(out _));
        registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyUp, 2_050);

        // The next Escape cancels, and an asking one never overwrites it.
        registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 3_000);
        registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyUp, 3_050);
        registration.EscapeAsksFirst = true;
        registration.ObserveKeyboard(HoldKeyState.VkEscape, HoldKeyState.WmKeyDown, 3_100);
        Assert.True(registration.ConsumeCancelRequest(out _, out asked));
        Assert.False(asked);
    }

    [Fact]
    public void Unregister_clears_a_pending_cancel_and_the_in_flight_flag()
    {
        // A cancel that outlived its own dictation would be consumed by the
        // NEXT one — a re-arm silently cancelling the first thing the user says
        // after it. Rearm() tears down and rebuilds, so this path runs whenever
        // the hold key or the gesture changes.
        using var registration = Build(DictationHotkeys.VkRightAlt);
        registration.DictationInFlight = true;

        registration.Unregister();

        Assert.False(registration.DictationInFlight);
        Assert.False(registration.ConsumeCancelRequest());
    }


    // ── The mouse trigger ────────────────────────────────────────────────────
    //
    // macOS has shipped a mouse trigger since the beginning and Windows had
    // none. Round 3 pinned WHY the obvious approach was wrong — VK_XBUTTON1/2
    // never reach a WH_KEYBOARD_LL hook — so the fix is a SECOND hook,
    // WH_MOUSE_LL, feeding a gesture machine of its own.
    //
    // MouseHookProc is only ever called by Windows from inside the system
    // input path, so what is asserted here is what ObserveMouse decides: the
    // wiring, exactly as this file asserts the keyboard wiring.

    private const int Down = DictationMouseMessages.WmXButtonDown;
    private const int Up = DictationMouseMessages.WmXButtonUp;
    private const uint Button4Data = 0x0001_0000;
    private const uint Button5Data = 0x0002_0000;

    private static LowLevelHoldKeyRegistration BuildWithMouse(DictationMouseButton button) =>
        new(
            new HotkeyBinding(0, DictationHotkeys.VkRightAlt, "Hold Right Alt"),
            NullLogger<LowLevelHoldKeyRegistration>.Instance,
            mouseButton: button);

    [Fact]
    public void A_registration_with_no_mouse_button_ignores_a_synthesised_press()
    {
        // THE RED PROOF, standing. Delete the mouse arm from the constructor
        // and this still passes; delete it from ObserveMouse and the test below
        // fails. Both halves are needed, which is why both are here.
        using var registration = Build(DictationHotkeys.VkRightAlt);

        Assert.Equal(DictationMouseButton.None, registration.MouseButton);
        Assert.Equal(HoldKeyTransition.None,
                     registration.ObserveMouse(Down, Button4Data, 1_000));
        Assert.False(registration.IsKeyDown());
    }

    [Fact]
    public void A_synthesised_press_on_the_bound_button_opens_a_hold()
    {
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        // Swallowed both ways: the press because the button is bound, the
        // release because its press was tracked.
        Assert.Equal(HoldKeyTransition.HoldStarted | HoldKeyTransition.Swallow,
                     registration.ObserveMouse(Down, Button4Data, 1_000));
        Assert.True(registration.IsKeyDown());
        Assert.True(registration.MouseHolds);

        Assert.Equal(HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow,
                     registration.ObserveMouse(Up, Button4Data, 5_000));
        Assert.False(registration.IsKeyDown());
        Assert.False(registration.MouseHolds);
    }

    [Fact]
    public void The_OTHER_side_button_does_nothing()
    {
        // Both buttons arrive as the same two messages and are told apart only
        // by the high word of mouseData. A user who bound button 4 must not
        // have button 5 opening a microphone every time they go Forward.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        Assert.Equal(HoldKeyTransition.None,
                     registration.ObserveMouse(Down, Button5Data, 1_000));
        Assert.False(registration.IsKeyDown());
    }

    [Theory]
    [InlineData(0x0200)] // WM_MOUSEMOVE
    [InlineData(0x0201)] // WM_LBUTTONDOWN
    [InlineData(0x0204)] // WM_RBUTTONDOWN
    [InlineData(0x0207)] // WM_MBUTTONDOWN
    public void Ordinary_mouse_traffic_opens_nothing(int message)
    {
        // WM_MOUSEMOVE is the one that matters: it arrives on every pointer
        // motion, and a left button that opened a microphone would make the
        // computer unusable rather than the feature broken.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        Assert.Equal(HoldKeyTransition.None,
                     registration.ObserveMouse(message, Button4Data, 1_000));
        Assert.False(registration.IsKeyDown());
    }

    [Fact]
    public void A_click_latches_and_the_registration_reports_it()
    {
        // The gesture macOS offers and the keyboard deliberately does not:
        // "Hold to talk, or click to toggle". IsLatched is what the poll tick
        // reads to know a dictation is running with nothing held down.
        using var registration = BuildWithMouse(DictationMouseButton.Button5);

        registration.ObserveMouse(Down, Button5Data, 1_000);
        registration.ObserveMouse(Up, Button5Data, 1_100);

        Assert.True(registration.IsLatched);
        Assert.True(registration.IsKeyDown());
        Assert.True(registration.MouseLatched);
        Assert.True(registration.MouseHolds);
    }

    [Fact]
    public void A_press_on_a_latched_mouse_dictation_ends_it_and_the_release_does_nothing()
    {
        // The stop is on the DOWN edge — macOS's behaviour, and not what
        // Windows did. Asserted HERE as well as in MouseTriggerTests because
        // the poll reads IsKeyDown off THIS object every 30 ms: it is the
        // registration, not the state machine, that the session believes, and
        // a press that still read as held would have the next tick reopen the
        // microphone the user just closed.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);
        registration.ObserveMouse(Down, Button4Data, 1_000);
        registration.ObserveMouse(Up, Button4Data, 1_100);
        Assert.True(registration.IsLatched);

        Assert.Equal(HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow,
                     registration.ObserveMouse(Down, Button4Data, 5_000));

        // Still physically down, and IsKeyDown is already false.
        Assert.False(registration.IsKeyDown());
        Assert.False(registration.IsLatched);

        // The release that follows closes nothing and cannot re-latch, even
        // though by the clock it is well inside the 300 ms click window. It
        // is still swallowed: its press was.
        Assert.Equal(HoldKeyTransition.Swallow,
                     registration.ObserveMouse(Up, Button4Data, 5_010));
        Assert.False(registration.IsKeyDown());
        Assert.False(registration.IsLatched);
    }

    [Fact]
    public void Unregister_closes_a_latched_mouse_dictation()
    {
        // A latch that outlived its own hook would leave IsKeyDown answering
        // true with nothing left able to set it false — a microphone open until
        // the app exits.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);
        registration.ObserveMouse(Down, Button4Data, 1_000);
        registration.ObserveMouse(Up, Button4Data, 1_100);
        Assert.True(registration.IsKeyDown());

        registration.Unregister();

        Assert.False(registration.IsKeyDown());
        Assert.False(registration.IsLatched);
    }

    [Fact]
    public void A_release_whose_press_this_hook_never_saw_passes_through()
    {
        // The press began before the hook was armed — a re-arm, or a new
        // binding chosen with the button held — and reached the application,
        // which must get the release too or be left with a button stuck down.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        var transition = registration.ObserveMouse(Up, Button4Data, 1_000);

        Assert.False(LowLevelHoldKeyRegistration.Consumes(transition));
        Assert.Equal(HoldKeyTransition.None, transition);
    }

    [Fact]
    public void A_duplicate_press_is_swallowed_and_opens_nothing_new()
    {
        using var registration = BuildWithMouse(DictationMouseButton.Button5);
        registration.ObserveMouse(Down, Button5Data, 1_000);

        Assert.Equal(HoldKeyTransition.Swallow, registration.ObserveMouse(Down, Button5Data, 1_050));
        Assert.True(registration.IsKeyDown());
    }

    [Fact]
    public void Nothing_the_unbound_registration_sees_is_swallowed()
    {
        using var registration = Build(DictationHotkeys.VkRightAlt);

        Assert.False(LowLevelHoldKeyRegistration.Consumes(registration.ObserveMouse(Down, Button4Data, 1_000)));
        Assert.False(LowLevelHoldKeyRegistration.Consumes(registration.ObserveMouse(Up, Button4Data, 1_100)));
        Assert.False(registration.MouseHolds);
        Assert.False(registration.MouseLatched);
    }

    [Fact]
    public void A_key_hold_beside_a_bound_button_is_the_keys()
    {
        // The window names the mouse button only for a dictation the button
        // holds: a key hold beside a bound button is still the key's.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        registration.ObserveKeyboard((int)DictationHotkeys.VkRightAlt, HoldKeyState.WmKeyDown, 1_000);

        Assert.True(registration.IsKeyDown());
        Assert.False(registration.MouseHolds);
        Assert.False(registration.MouseLatched);
    }

    /// <summary>
    /// Pins on the source, not evidence the hook behaves: <c>MouseHookProc</c>
    /// returns the consumed value for exactly the events
    /// <see cref="LowLevelHoldKeyRegistration.Consumes"/> names in
    /// <see cref="LowLevelHoldKeyRegistration.ObserveMouse"/>'s answer, and
    /// passes everything else to <c>CallNextHookEx</c>.
    /// </summary>
    [Fact]
    public void MouseHookProc_consumes_what_ObserveMouse_marks_and_passes_everything_else_on()
    {
        var code = File.ReadAllText(Path.Combine(WindowsRoot(), "Dictation.Infrastructure", "LowLevelHoldKeyRegistration.cs"))
            .ReplaceLineEndings("\n");
        var start = code.IndexOf("    private IntPtr MouseHookProc(int code, IntPtr wParam, IntPtr lParam)", StringComparison.Ordinal);
        Assert.True(start >= 0, "no MouseHookProc");
        var hook = code[start..code.IndexOf("\n    }\n", start, StringComparison.Ordinal)];

        var observe = hook.IndexOf("var transition = ObserveMouse(", StringComparison.Ordinal);
        var consume = hook.IndexOf("if (Consumes(transition))", StringComparison.Ordinal);
        var consumed = hook.IndexOf("return ConsumedEvent;", StringComparison.Ordinal);
        var next = hook.IndexOf("return CallNextHookEx(", StringComparison.Ordinal);

        Assert.True(observe >= 0 && consume > observe, "the decision is ObserveMouse's");
        Assert.True(consumed > consume && next > consumed, "a kept event never reaches the next hook");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(hook, "return ConsumedEvent;"));
    }

    private static string WindowsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dictation.Infrastructure", "LowLevelHoldKeyRegistration.cs")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("no windows/ source tree above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_keyboard_trigger_still_works_when_a_mouse_button_is_bound()
    {
        // Two hooks, one feature. The keyboard half must be untouched by the
        // mouse half's presence — a user who binds a side button has not given
        // up Right Alt.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        Assert.Equal(DictationHotkeys.VkRightAlt, registration.Binding.VirtualKey);
        Assert.False(registration.IsKeyDown());
    }

    [Theory]
    [InlineData(DictationMouseButton.None)]
    [InlineData(DictationMouseButton.Button4)]
    [InlineData(DictationMouseButton.Button5)]
    public void Every_offered_button_builds_a_registration_that_starts_at_rest(
        DictationMouseButton button)
    {
        using var registration = BuildWithMouse(button);

        Assert.Equal(button, registration.MouseButton);
        Assert.False(registration.IsKeyDown());
        Assert.False(registration.IsLatched);
    }

    // ── A refused mouse hook, made visible ───────────────────────────────
    //
    // Register() returns TRUE when the WH_MOUSE_LL hook is refused, and that
    // is deliberate — a mouse hook Windows will not install must not take
    // hold-to-talk down with it. The cost was that nothing above this class
    // could tell "button 4 is armed" from "button 4 was refused and does
    // nothing", so the user's whole experience of the failure was a button
    // that did not work and a pane that said it was set.
    //
    // ObserveMouseHookOutcome is the seam, and it exists for the reason
    // ObserveMouse does: Register is SetWindowsHookEx, which needs a live
    // input stack, and the outcome that matters here — Windows refusing —
    // is one a developer machine essentially never produces. Without the seam
    // this whole surface could be deleted with no test going red.

    [Fact]
    public void No_bound_button_is_not_a_degradation()
    {
        // The shipped default. A property that read true for every install
        // would put an amber fault line under a row nobody had touched, which
        // is worse than the silence it replaced.
        using var registration = Build(DictationHotkeys.VkRightAlt);

        Assert.False(registration.MouseTriggerDegraded);
        Assert.False(registration.ObserveMouseHookOutcome(installed: false));
        Assert.False(registration.MouseTriggerDegraded);
    }

    [Fact]
    public void A_bound_button_whose_hook_was_refused_is_degraded()
    {
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        Assert.True(registration.ObserveMouseHookOutcome(installed: false));
        Assert.True(registration.MouseTriggerDegraded);
    }

    [Fact]
    public void A_bound_button_whose_hook_installed_is_not_degraded()
    {
        using var registration = BuildWithMouse(DictationMouseButton.Button5);

        Assert.False(registration.ObserveMouseHookOutcome(installed: true));
        Assert.False(registration.MouseTriggerDegraded);
    }

    [Fact]
    public void A_registration_that_has_not_tried_yet_has_not_failed()
    {
        // Constructed with a button and never armed. Nothing has been refused,
        // so nothing may be reported as refused — the shell reads this straight
        // after Attach, and a true here before Register ran would show a fault
        // for a hook that was never attempted.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        Assert.Equal(DictationMouseButton.Button4, registration.MouseButton);
        Assert.False(registration.MouseTriggerDegraded);
    }

    [Fact]
    public void Unregister_clears_the_degrade()
    {
        // A torn-down trigger is OFF, not degraded. Left set, a Disarm after a
        // refused run would leave the pane showing an amber "this button does
        // nothing" for a hook that no longer exists.
        //
        // Cleared above Unregister's own hook check, which matters: on the
        // refusal path there IS no hook to unhook, and a reset that sat below
        // the guard would never run on precisely the machine that needs it.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);
        registration.ObserveMouseHookOutcome(installed: false);
        Assert.True(registration.MouseTriggerDegraded);

        registration.Unregister();

        Assert.False(registration.MouseTriggerDegraded);
    }

    [Fact]
    public void A_degraded_mouse_trigger_leaves_the_keyboard_alone()
    {
        // The rule the surface must not quietly change. The keyboard half is
        // what the user has been dictating with all along, and the copy beside
        // the amber line promises it is unaffected — so a refused mouse hook
        // must leave the binding, the state machine and the gesture untouched.
        using var registration = BuildWithMouse(DictationMouseButton.Button4);

        registration.ObserveMouseHookOutcome(installed: false);

        Assert.True(registration.MouseTriggerDegraded);
        Assert.Equal(DictationHotkeys.VkRightAlt, registration.Binding.VirtualKey);
        Assert.False(registration.IsKeyDown());
        Assert.False(registration.ConsumeCancelRequest());
    }

    // ── loop-v2 keyboard-origin feed (span-observer.md §5.1) ─────────────

    [Fact]
    public void Keyboard_origin_feed_gets_timestamp_and_foreground_pid_on_key_down_only()
    {
        using var registration = Build(DictationHotkeys.VkRightAlt);
        var seen = new List<(long Ms, int Pid)>();
        registration.KeyboardInputObserved = (ms, pid) => seen.Add((ms, pid));

        Assert.True(registration.ForwardKeyboardInput(HoldKeyState.WmKeyDown, 1_000, 4242));
        Assert.True(registration.ForwardKeyboardInput(HoldKeyState.WmSysKeyDown, 1_010, 4243));
        Assert.False(registration.ForwardKeyboardInput(HoldKeyState.WmKeyUp, 1_020, 4242));
        Assert.False(registration.ForwardKeyboardInput(HoldKeyState.WmSysKeyUp, 1_030, 4242));

        Assert.Equal(new[] { (1_000L, 4242), (1_010L, 4243) }, seen);
    }

    [Fact]
    public void Keyboard_origin_feed_is_a_no_op_without_a_sink()
    {
        // feature.dictation_learning_v2 off: nothing is attached and nothing happens.
        using var registration = Build(DictationHotkeys.VkRightAlt);
        Assert.Null(registration.KeyboardInputObserved);
        Assert.False(registration.ForwardKeyboardInput(HoldKeyState.WmKeyDown, 1_000, 4242));
    }
}
