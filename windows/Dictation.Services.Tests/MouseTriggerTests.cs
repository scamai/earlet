using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// Dictating with a spare mouse button.
/// </summary>
/// <remarks>
/// <para>
/// macOS has shipped this since the beginning — a "Mouse button" row with a
/// live recorder, "Optional. Hold to talk, or click to toggle" — and Windows
/// had nothing. Round 3 established WHY the obvious approach could not work:
/// <c>VK_XBUTTON1</c> and <c>VK_XBUTTON2</c> never reach a
/// <c>WH_KEYBOARD_LL</c> hook, so putting them in the key picker would offer a
/// binding that could never fire. They arrive on <c>WH_MOUSE_LL</c>, which
/// needed installing.
/// </para>
/// <para>
/// The gesture is <em>not</em> the keyboard's. <see cref="HoldKeyState"/>
/// latches on a DOUBLE tap because its triggers are bare modifiers people brush
/// all day; a spare side button is pressed for no other reason, so a single
/// click is safe there. Two machines, so the dangerous gesture is never a
/// boolean away from the safe one.
/// </para>
/// </remarks>
public sealed class MouseTriggerTests
{
    // ── Reading the hook's struct ────────────────────────────────────────────

    [Fact]
    public void The_side_button_is_in_the_HIGH_word_of_mouseData()
    {
        // The obvious mistake is reading the low word, which is zero for both
        // buttons — a mouse trigger that installs, runs and never fires.
        Assert.Equal(DictationMouseButton.Button4, DictationMouseMessages.ButtonOf(0x0001_0000));
        Assert.Equal(DictationMouseButton.Button5, DictationMouseMessages.ButtonOf(0x0002_0000));
    }

    [Fact]
    public void The_low_word_is_ignored()
    {
        // WM_XBUTTON* carries other flags down there. A value with the button
        // in the low half is not a button this trigger knows.
        Assert.Equal(DictationMouseButton.None, DictationMouseMessages.ButtonOf(0x0000_0001));
        Assert.Equal(DictationMouseButton.None, DictationMouseMessages.ButtonOf(0x0000_0002));
    }

    [Fact]
    public void A_button_this_feature_does_not_offer_is_None()
    {
        Assert.Equal(DictationMouseButton.None, DictationMouseMessages.ButtonOf(0x0003_0000));
        Assert.Equal(DictationMouseButton.None, DictationMouseMessages.ButtonOf(0));
    }

    [Theory]
    [InlineData(DictationMouseMessages.WmXButtonDown, true)]
    [InlineData(DictationMouseMessages.WmXButtonUp, false)]
    public void The_two_messages_this_trigger_cares_about(int message, bool isDown)
    {
        Assert.Equal(isDown, DictationMouseMessages.EdgeOf(message));
    }

    [Theory]
    [InlineData(0x0200)] // WM_MOUSEMOVE — the flood a low-level hook actually sees
    [InlineData(0x0201)] // WM_LBUTTONDOWN
    [InlineData(0x0204)] // WM_RBUTTONDOWN
    [InlineData(0x0207)] // WM_MBUTTONDOWN
    [InlineData(0x020A)] // WM_MOUSEWHEEL
    public void Every_other_mouse_message_is_nothing(int message)
    {
        // WM_MOUSEMOVE is the one that matters for cost: it arrives on every
        // pointer motion, hundreds a second, on the system input path that
        // Windows unhooks for overrunning 300 ms.
        Assert.Null(DictationMouseMessages.EdgeOf(message));
    }

    // ── Hold ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_long_press_opens_and_closes_a_hold()
    {
        var state = new MouseHoldOrClickState();

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Down(1_000));
        Assert.True(state.IsHeld);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Up(4_000));
        Assert.False(state.IsHeld);
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void The_microphone_opens_on_the_PRESS_not_on_the_verdict()
    {
        // A click and a hold are indistinguishable until the release. Waiting
        // the whole click window to find out which one it was would clip the
        // first word off every genuine hold — the user has already started
        // talking, and the window is 300 ms.
        var state = new MouseHoldOrClickState();

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Down(0));
        Assert.True(state.IsHeld);
    }

    [Fact]
    public void A_duplicate_press_does_not_restart_the_dictation()
    {
        // A low-level hook can see a second down with no up between, after a
        // focus change or an injected event. Reopening the hold would restart a
        // dictation the user is in the middle of speaking.
        var state = new MouseHoldOrClickState();
        state.Down(0);

        Assert.Equal(HoldKeyTransition.None, state.Down(50));
        Assert.True(state.IsHeld);
    }

    [Fact]
    public void A_release_for_a_press_this_machine_never_saw_does_nothing()
    {
        // The hook was installed with the button already down, or the press
        // went to another desktop.
        var state = new MouseHoldOrClickState();

        Assert.Equal(HoldKeyTransition.None, state.Up(1_000));
        Assert.False(state.IsHeld);
    }

    // ── Click, which latches ────────────────────────────────────────────────

    [Fact]
    public void A_click_leaves_dictation_running_with_the_button_released()
    {
        var state = new MouseHoldOrClickState();

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Down(1_000));
        // Released well inside the click window.
        Assert.Equal(HoldKeyTransition.None, state.Up(1_100));

        Assert.True(state.IsHeld);
        Assert.True(state.IsLatched);
    }

    [Fact]
    public void A_second_click_stops_it_ON_THE_PRESS()
    {
        // The stop lands on the DOWN edge, which is macOS's behaviour and was
        // not Windows'. Windows ended the latch on the release, so everything
        // said between deciding to stop and letting go was still being
        // dictated — words the user did not mean to say. Nothing is learned by
        // waiting for the release either: any press ends a latch however long
        // it is held, so the outcome is already known at the press.
        var state = new MouseHoldOrClickState();
        state.Down(1_000);
        state.Up(1_100);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Down(5_000));
        // And the RELEASE closes nothing. Reporting HoldEnded again would end
        // whatever dictation had started in between; reporting a click would
        // latch straight back open, which is the stop-does-not-stop bug.
        Assert.Equal(HoldKeyTransition.None, state.Up(5_080));

        Assert.False(state.IsHeld);
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void The_stopping_press_reads_as_UNHELD_while_it_is_still_physically_down()
    {
        // The poll is the whole reason this matters. IsHeld is read every
        // 30 ms and is the only thing the session acts on, so a press that has
        // just closed a latched dictation must not read as held for as long as
        // the user keeps their finger on the button — otherwise the very next
        // tick reopens the microphone and stopping does not stop.
        var state = new MouseHoldOrClickState();
        state.Down(1_000);
        state.Up(1_100);
        Assert.True(state.IsLatched);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Down(5_000));

        // Still physically down, and already false.
        Assert.False(state.IsHeld);
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void A_press_and_HOLD_also_stops_a_latched_dictation()
    {
        // The one way this gesture could strand somebody: if only a QUICK click
        // ended the latch, a user who pressed and held to stop would let go and
        // find the microphone still open. Four seconds of holding changes
        // nothing — the stop already happened on the press.
        var state = new MouseHoldOrClickState();
        state.Down(1_000);
        state.Up(1_100);
        Assert.True(state.IsLatched);

        Assert.Equal(HoldKeyTransition.HoldEnded, state.Down(5_000));
        Assert.False(state.IsHeld);

        Assert.Equal(HoldKeyTransition.None, state.Up(9_000));
        Assert.False(state.IsHeld);
        Assert.False(state.IsLatched);
    }

    [Fact]
    public void The_release_after_a_stopping_press_cannot_re_latch_however_quick_it_is()
    {
        // The release that ends a stopping press is INSIDE the click window —
        // it is a click, by the clock. Judged as one it would latch straight
        // back open, so the user's second click would look like a stop that
        // instantly restarted. The stopping-press flag is what makes the clock
        // irrelevant here, and this is the test that fails if it is dropped.
        var state = new MouseHoldOrClickState();
        state.Down(1_000);
        state.Up(1_100);

        state.Down(5_000);
        Assert.Equal(HoldKeyTransition.None, state.Up(5_010));

        Assert.False(state.IsLatched);
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void A_press_after_a_completed_stop_starts_a_fresh_hold()
    {
        // The flag is per-gesture, not sticky. Left set, the next press would
        // be swallowed as the tail of the previous stop and the user would have
        // to click twice to start dictating again.
        var state = new MouseHoldOrClickState();
        state.Down(1_000);
        state.Up(1_100);
        state.Down(5_000);
        state.Up(5_010);

        Assert.Equal(HoldKeyTransition.HoldStarted, state.Down(9_000));
        Assert.True(state.IsHeld);
    }

    [Fact]
    public void The_shipped_click_window_is_300ms_because_macOS_is()
    {
        // Stated against the constant AND exercised through the default
        // constructor, because those were allowed to disagree: the number was
        // 250 while the comment beside it claimed macOS parity, and macOS has
        // been 300 since the gesture shipped there. Fifty milliseconds inside
        // the band where a slow tap is refused is not debuggable from the
        // outside — the symptom is a tap that opens a dictation and never
        // latches, so the user gets a 250 ms recording instead of hands-free.
        Assert.Equal(300, DictationMouseButtons.ClickMilliseconds);

        var exactly = new MouseHoldOrClickState();
        exactly.Down(0);
        Assert.Equal(HoldKeyTransition.None, exactly.Up(300));
        Assert.True(exactly.IsLatched);

        var justOver = new MouseHoldOrClickState();
        justOver.Down(0);
        Assert.Equal(HoldKeyTransition.HoldEnded, justOver.Up(301));
        Assert.False(justOver.IsLatched);
    }

    [Fact]
    public void The_injected_threshold_is_the_one_that_is_used()
    {
        // A deliberately unshipped number, so this fails if the constructor
        // argument is ever ignored in favour of the constant — which would
        // leave every test above passing while the seam quietly did nothing.
        var exactly = new MouseHoldOrClickState(clickMilliseconds: 120);
        exactly.Down(0);
        Assert.Equal(HoldKeyTransition.None, exactly.Up(120));
        Assert.True(exactly.IsLatched);

        var justOver = new MouseHoldOrClickState(clickMilliseconds: 120);
        justOver.Down(0);
        Assert.Equal(HoldKeyTransition.HoldEnded, justOver.Up(121));
        Assert.False(justOver.IsLatched);
    }

    [Fact]
    public void A_click_threshold_of_zero_is_refused()
    {
        // It would make every press a hold, silently removing half the gesture
        // — the kind of configuration failure that reads as "the click stopped
        // working" and has no error anywhere.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MouseHoldOrClickState(clickMilliseconds: 0));
    }

    // ── Teardown ────────────────────────────────────────────────────────────

    [Fact]
    public void Release_closes_an_open_hold()
    {
        // Same rule HoldKeyState.Release keeps: a teardown mid-utterance must
        // not leave the caller believing the button is still pressed and the
        // microphone still wanted.
        var state = new MouseHoldOrClickState();
        state.Down(0);

        state.Release();

        Assert.False(state.IsHeld);
    }

    [Fact]
    public void Release_clears_a_latch()
    {
        var state = new MouseHoldOrClickState();
        state.Down(0);
        state.Up(100);
        Assert.True(state.IsLatched);

        state.Release();

        Assert.False(state.IsHeld);
        Assert.False(state.IsLatched);
        // And the button coming up afterwards does not reopen anything.
        Assert.Equal(HoldKeyTransition.None, state.Up(200));
        Assert.False(state.IsHeld);
    }

    [Fact]
    public void Release_clears_a_stopping_press_too()
    {
        // The third piece of state, and the one a teardown could plausibly
        // forget because it only exists for a few hundred milliseconds. Left
        // set across a re-arm, the FIRST press of the next hook would be
        // discarded as the tail of a stop that belonged to the previous one —
        // a mouse button that needs pressing twice after every settings change.
        var state = new MouseHoldOrClickState();
        state.Down(0);
        state.Up(100);
        state.Down(1_000); // Ends the latch; the button is still down.

        state.Release();

        Assert.False(state.IsHeld);
        Assert.False(state.IsLatched);
        Assert.Equal(HoldKeyTransition.HoldStarted, state.Down(2_000));
        Assert.True(state.IsHeld);
    }

    // ── The setting ─────────────────────────────────────────────────────────

    [Fact]
    public void The_shipped_default_is_off()
    {
        // No install has ever had a mouse trigger, so a defaulted value must be
        // "none" — and it is the zero of the enum, so a struct that was never
        // written cannot install a hook either.
        Assert.Equal(DictationMouseButton.None, DictationMouseButtons.Default);
        Assert.Equal(DictationMouseButton.None, default(DictationMouseButton));
    }

    [Theory]
    [InlineData("none", DictationMouseButton.None)]
    [InlineData("button4", DictationMouseButton.Button4)]
    [InlineData("button5", DictationMouseButton.Button5)]
    public void Ids_round_trip(string id, DictationMouseButton value)
    {
        Assert.Equal(value, DictationMouseButtons.Normalize(id));
        Assert.Equal(id, DictationMouseButtons.Id(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("mouse4")]
    [InlineData("Button 4")]
    public void Anything_unrecognised_degrades_to_OFF(string? id)
    {
        // The direction matters. Degrading to a BUTTON would install a mouse
        // hook because somebody hand-edited a settings file or downgraded a
        // build — a background hook the user never asked for.
        Assert.Equal(DictationMouseButton.None, DictationMouseButtons.Normalize(id));
    }

    [Theory]
    [InlineData("BUTTON4")]
    [InlineData(" button5 ")]
    public void Ids_survive_casing_and_stray_whitespace(string id)
    {
        Assert.NotEqual(DictationMouseButton.None, DictationMouseButtons.Normalize(id));
    }

    [Fact]
    public void Every_offered_button_states_what_it_costs()
    {
        foreach (var choice in DictationMouseButtons.Choices)
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.Label));
            Assert.False(string.IsNullOrWhiteSpace(choice.Detail));

            if (choice.Value == DictationMouseButton.None)
            {
                Assert.Null(DictationMouseButtons.CautionFor(choice.Value));
                continue;
            }

            // The caution names the everyday action that would ALSO fire —
            // macOS's rule, because "this key has a cost" without naming the
            // cost is not a warning.
            var caution = DictationMouseButtons.CautionFor(choice.Value);
            Assert.False(string.IsNullOrWhiteSpace(caution));
            Assert.Contains("does not swallow", caution!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_two_cautions_name_different_actions()
    {
        Assert.Contains("Back",
                        DictationMouseButtons.CautionFor(DictationMouseButton.Button4)!,
                        StringComparison.Ordinal);
        Assert.Contains("Forward",
                        DictationMouseButtons.CautionFor(DictationMouseButton.Button5)!,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void A_member_added_later_still_gets_a_caution()
    {
        Assert.False(string.IsNullOrWhiteSpace(
            DictationMouseButtons.CautionFor((DictationMouseButton)99)));
    }
}
