using System.Collections.Immutable;

namespace Dictation.Services;

/// <summary>
/// Which spare mouse button, if any, starts a dictation.
/// </summary>
/// <remarks>
/// <para>
/// macOS has shipped a mouse trigger since the beginning:
/// <c>DictationTabView</c> renders an always-visible "Mouse button" row with a
/// live recorder, "Optional. Hold to talk, or click to toggle", per-binding
/// caution copy and a degraded-state note. It is behind no flag. Holding a
/// spare side button is one of the commonest push-to-talk setups there is, and
/// on Windows the affordance was simply missing.
/// </para>
/// <para>
/// <b>Why this is an enum of two and not a recorder.</b> A
/// <c>WH_MOUSE_LL</c> hook can see every button, but binding the LEFT or RIGHT
/// button to dictation would take the pointer away from the user — the hook
/// observes rather than swallows, so a left-click would still click, but every
/// click would also open a microphone. The middle button is a paste on X11
/// habits and an autoscroll on Windows. What is left is exactly the two
/// buttons that exist for nothing else, which is the same reason macOS's own
/// recorder is used for side buttons in practice.
/// </para>
/// </remarks>
public enum DictationMouseButton
{
    /// <summary>
    /// No mouse trigger. <b>The shipped default, and zero deliberately</b>: a
    /// defaulted value must be the behaviour every existing install already
    /// has, and no install has ever had a mouse trigger.
    /// </summary>
    None = 0,

    /// <summary>The first side button — <c>XBUTTON1</c>, "back" on most mice.</summary>
    Button4 = 1,

    /// <summary>The second side button — <c>XBUTTON2</c>, "forward".</summary>
    Button5 = 2,
}

/// <summary>
/// The persisted ids, the picker's copy, and the normaliser.
/// </summary>
public static class DictationMouseButtons
{
    /// <summary>Off. Nothing changes for anybody who does not go looking.</summary>
    public const DictationMouseButton Default = DictationMouseButton.None;

    /// <summary>
    /// How long a press can last and still count as a CLICK rather than a hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 300 ms, matching macOS's <c>MouseHoldOrClickState.clickWindow</c>. The
    /// number has to separate two gestures a user makes on purpose: a tap to
    /// latch dictation open, and a press held through a sentence. Anything
    /// under about 200 ms starts refusing deliberate taps from people who do
    /// not tap quickly; anything over about 350 ms starts stealing the
    /// beginning of a genuine hold, which is the worse failure — the user has
    /// already begun speaking.
    /// </para>
    /// <para>
    /// <b>This was 250 ms, and the comment claiming it matched macOS was
    /// wrong.</b> macOS has been 300 ms since the gesture shipped there. The
    /// gap is inside the band where people who tap slowly are refused, so the
    /// symptom was a tap that opened a dictation and never latched — the user
    /// tapped to start hands-free and got a 250 ms recording instead. Two
    /// platforms disagreeing by 50 ms is not a thing anyone can debug from the
    /// outside, which is why the number is now stated once and cited.
    /// </para>
    /// <para>
    /// The hold is opened on the press either way, so a click and a hold are
    /// indistinguishable until the release. That is deliberate: the microphone
    /// must already be open by the time somebody who meant to hold starts
    /// talking, and 300 ms of "wait and see" would clip every first word.
    /// </para>
    /// </remarks>
    public const long ClickMilliseconds = 300;

    /// <summary>One offered option, as the picker renders it.</summary>
    /// <param name="Value">The choice itself.</param>
    /// <param name="Id">What goes in settings.json.</param>
    /// <param name="Label">The row.</param>
    /// <param name="Detail">What it costs, said plainly.</param>
    public sealed record Choice(
        DictationMouseButton Value,
        string Id,
        string Label,
        string Detail);

    /// <summary>The three, in the order the picker shows them: off first.</summary>
    public static readonly ImmutableArray<Choice> Choices =
    [
        new(
            DictationMouseButton.None,
            "none",
            "No mouse button",
            "The keyboard shortcut above is the only way to start a dictation. This is the "
            + "default."),
        new(
            DictationMouseButton.Button4,
            "button4",
            "Mouse button 4 (back)",
            "Hold it to talk, or click it once to keep dictation running and click again to stop. "
            + "The app watches the button, it does not swallow it — so it still goes Back in your "
            + "browser."),
        new(
            DictationMouseButton.Button5,
            "button5",
            "Mouse button 5 (forward)",
            "Hold it to talk, or click it once to keep dictation running and click again to stop. "
            + "The app watches the button, it does not swallow it — so it still goes Forward in your "
            + "browser."),
    ];

    /// <summary>
    /// The persisted id for a value. Total over the enum, so a member added
    /// without an id here is a compile-time hole rather than a setting that
    /// silently saves as the default.
    /// </summary>
    public static string Id(DictationMouseButton value) =>
        Choices.First(c => c.Value == value).Id;

    /// <summary>
    /// A persisted id back to a value. Anything unrecognised becomes
    /// <see cref="Default"/> — which is OFF, and that direction is deliberate:
    /// a hand-edited or downgraded setting must never install a mouse hook the
    /// user did not ask for.
    /// </summary>
    public static DictationMouseButton Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Default;
        }

        foreach (var choice in Choices)
        {
            if (string.Equals(choice.Id, id.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return choice.Value;
            }
        }

        return Default;
    }

    /// <summary>
    /// What a user should know before choosing this button, or <c>null</c> when
    /// there is nothing to say.
    /// </summary>
    /// <remarks>
    /// macOS's <c>caution</c>, ported, and the rule it keeps is the one that
    /// matters: name the specific everyday action that would ALSO fire, rather
    /// than warning in the abstract. The hook observes and never swallows, so
    /// both of these buttons keep doing what they already did — which is
    /// exactly the fact a user needs before they bind one.
    /// </remarks>
    public static string? CautionFor(DictationMouseButton value) => value switch
    {
        DictationMouseButton.Button4 =>
            "Button 4 is Back in most browsers and file managers. The app does not swallow it, so "
            + "going back will also start a very short dictation — which comes back as “that was "
            + "too short to be a sentence”.",
        DictationMouseButton.Button5 =>
            "Button 5 is Forward in most browsers and file managers. The app does not swallow it, so "
            + "going forward will also start a very short dictation — which comes back as “that "
            + "was too short to be a sentence”.",
        DictationMouseButton.None => null,
        // The arm a NEW member lands in. It says something rather than nothing:
        // a binding offered with no caution reads as a binding with no cost.
        _ => "The app watches this button, it does not swallow it, so whatever it already does it "
             + "will go on doing.",
    };
}

/// <summary>
/// Turning <c>WH_MOUSE_LL</c> traffic into a button this feature has an opinion
/// about.
/// </summary>
/// <remarks>
/// <para>
/// Split out of the hook for the same reason <see cref="HoldKeyState"/> was:
/// the callback runs on the system input path with 300 ms before Windows
/// removes the hook, and none of this can be exercised from inside one. What
/// is left in the hook is reading an <c>MSLLHOOKSTRUCT</c>.
/// </para>
/// <para>
/// <b>The high word of <c>mouseData</c>.</b> Both side buttons arrive as the
/// same two messages, <c>WM_XBUTTONDOWN</c> and <c>WM_XBUTTONUP</c>; which one
/// it was lives in the top 16 bits of <c>mouseData</c> as <c>XBUTTON1</c> (1)
/// or <c>XBUTTON2</c> (2). Reading the low word instead — the obvious mistake —
/// yields zero for both, which is a mouse trigger that never fires.
/// </para>
/// </remarks>
public static class DictationMouseMessages
{
    /// <summary><c>WM_XBUTTONDOWN</c>.</summary>
    public const int WmXButtonDown = 0x020B;

    /// <summary><c>WM_XBUTTONUP</c>.</summary>
    public const int WmXButtonUp = 0x020C;

    /// <summary><c>XBUTTON1</c>, in the high word of <c>mouseData</c>.</summary>
    public const int XButton1 = 0x0001;

    /// <summary><c>XBUTTON2</c>.</summary>
    public const int XButton2 = 0x0002;

    /// <summary>Which side button a <c>mouseData</c> value names.</summary>
    public static DictationMouseButton ButtonOf(uint mouseData) => (mouseData >> 16) switch
    {
        XButton1 => DictationMouseButton.Button4,
        XButton2 => DictationMouseButton.Button5,
        _ => DictationMouseButton.None,
    };

    /// <summary>
    /// Whether this event is a press, a release, or nothing this trigger cares
    /// about.
    /// </summary>
    /// <returns>
    /// <c>true</c> for a press, <c>false</c> for a release, <c>null</c> for
    /// every other message — and a low-level mouse hook sees a great many of
    /// them, one per pointer move.
    /// </returns>
    public static bool? EdgeOf(int message) => message switch
    {
        WmXButtonDown => true,
        WmXButtonUp => false,
        _ => null,
    };
}

/// <summary>
/// Hold-to-talk on a mouse button, with a click that latches.
/// </summary>
/// <remarks>
/// <para>
/// macOS's <c>MouseHoldOrClickState</c>, ported. It is a separate machine from
/// <see cref="HoldKeyState"/> rather than a mode on it, and the reason is
/// stated in that type's own remarks: a keyboard trigger latching on a SINGLE
/// tap would mean a brushed Right Alt started recording, so the keyboard needs
/// a double-tap. A spare mouse button is pressed for no other reason, so a
/// single click is safe there — and giving one machine both gestures would put
/// the dangerous one a boolean away from the safe one.
/// </para>
/// <para>
/// <b>The hold opens on the press, always.</b> A click and a hold are
/// indistinguishable until the release, and waiting
/// <see cref="DictationMouseButtons.ClickMilliseconds"/> to find out which one
/// it was would clip the first word off every genuine hold. So the microphone
/// opens immediately; the release decides whether it closes.
/// </para>
/// <para>
/// Pure and platform-neutral, so the whole gesture is exercised on a machine
/// with no mouse hook in it.
/// </para>
/// </remarks>
public sealed class MouseHoldOrClickState
{
    private readonly long _clickMs;
    private bool _physicallyDown;
    private bool _latched;
    private long _downAtMs;

    /// <summary>
    /// Set while the button is physically down on the press that ENDED a
    /// latched dictation. That press holds nothing open — the dictation is
    /// already closed — so <see cref="IsHeld"/> must read false through it, and
    /// the release that follows must not be mistaken for a second gesture.
    /// </summary>
    private bool _stoppingPress;

    /// <summary>The latch was ended from outside while the button was down:
    /// its release still ends the hold, and cannot latch.</summary>
    private bool _externalEndDuringPress;

    /// <param name="clickMilliseconds">
    /// How long a press can last and still count as a click. Injectable for
    /// tests; the shipped value is
    /// <see cref="DictationMouseButtons.ClickMilliseconds"/>.
    /// </param>
    public MouseHoldOrClickState(long clickMilliseconds = DictationMouseButtons.ClickMilliseconds)
    {
        _clickMs = clickMilliseconds > 0
            ? clickMilliseconds
            : throw new ArgumentOutOfRangeException(
                nameof(clickMilliseconds),
                "A click threshold of zero or less makes every press a hold, which silently "
                + "removes half the gesture.");
    }

    /// <summary>
    /// Whether the microphone should be open right now — because the button is
    /// physically down, or because a click latched it.
    /// </summary>
    /// <remarks>
    /// The <see cref="_stoppingPress"/> term is what keeps the poll honest.
    /// <c>IsKeyDown</c> is read every 30 ms and is the only thing the session
    /// acts on, so a press that just closed a latched dictation must not read
    /// as held for however long the user keeps their finger down — otherwise
    /// stopping by press-and-hold reopens the microphone on the next tick.
    /// </remarks>
    public bool IsHeld => (_physicallyDown && !_stoppingPress) || _latched;

    /// <summary>
    /// Whether a click has latched a dictation open with the button released.
    /// </summary>
    public bool IsLatched => _latched;

    /// <summary>The button went down.</summary>
    public HoldKeyTransition Down(long nowMs)
    {
        if (_physicallyDown)
        {
            // A duplicate press with no release between. A low-level hook can
            // see one after a focus change or an injected event; reopening the
            // hold here would restart a dictation the user is in the middle of.
            return HoldKeyTransition.None;
        }

        _physicallyDown = true;
        _externalEndDuringPress = false;
        _downAtMs = nowMs;

        if (_latched)
        {
            // Any press ends a latched dictation, however long it is held. The
            // alternative — only a QUICK click stops it — leaves a user who
            // pressed and held to stop with the microphone still open, which
            // is the one way this gesture could strand somebody.
            //
            // It ends on the PRESS, not on the release, which is what macOS
            // does and is the better half of the choice: the user has decided
            // to stop, and the words spoken between their press and their
            // release are words they did not mean to dictate. Ending on the
            // release also made the stop feel late on a slow click, for no
            // gain — nothing is learned by waiting, because the outcome is the
            // same however long the button is held.
            _latched = false;
            _stoppingPress = true;
            return HoldKeyTransition.HoldEnded;
        }

        _stoppingPress = false;
        return HoldKeyTransition.HoldStarted;
    }

    /// <summary>The button came up.</summary>
    public HoldKeyTransition Up(long nowMs)
    {
        if (!_physicallyDown)
        {
            // A release for a press this machine never saw start: the hook was
            // installed with the button already down, or the press went to
            // another desktop. Nothing to close.
            return HoldKeyTransition.None;
        }

        _physicallyDown = false;

        if (_stoppingPress)
        {
            // The press already ended the latched dictation. This release is
            // the tail of that same gesture and closes nothing: reporting
            // HoldEnded twice would end the NEXT dictation if one had started
            // in between, and reporting a click here would latch straight back
            // open — which is the stop-does-not-stop bug.
            _stoppingPress = false;
            return HoldKeyTransition.None;
        }

        if (_externalEndDuringPress)
        {
            // EndLatch already ended this gesture; a quick release must not
            // latch it straight back open.
            _externalEndDuringPress = false;
            return HoldKeyTransition.HoldEnded;
        }

        if (nowMs - _downAtMs <= _clickMs)
        {
            // A click. The hold that opened on the press stays open, with the
            // button released, until the next press.
            _latched = true;
            return HoldKeyTransition.None;
        }

        return HoldKeyTransition.HoldEnded;
    }

    /// <summary>
    /// End a latched hold from outside the input stream: the recording limit,
    /// a cancel from the window, or a press the shell refused. Clears only the
    /// latch, never the physical button's own state, so a real release
    /// afterwards still lands. The mouse twin of
    /// <see cref="HoldKeyState.EndLatch"/>.
    /// </summary>
    public void EndLatch()
    {
        _externalEndDuringPress = _physicallyDown;
        _latched = false;
    }

    /// <summary>
    /// Force everything closed — a teardown, or a dictation the user cancelled
    /// with Escape.
    /// </summary>
    /// <remarks>
    /// Same rule <see cref="HoldKeyState.Release"/> keeps: a hold that was open
    /// when the hook came down is CLOSED, so a teardown mid-utterance cannot
    /// leave the caller believing the button is still pressed and the
    /// microphone still wanted.
    /// </remarks>
    public void Release()
    {
        _physicallyDown = false;
        _latched = false;
        _stoppingPress = false;
        _externalEndDuringPress = false;
        _downAtMs = 0;
    }
}
