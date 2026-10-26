namespace Dictation.Services;

/// <summary>
/// What one keyboard event means to a hold-to-talk trigger.
/// </summary>
/// <remarks>
/// <b>A flags set, and it has to be.</b> One event routinely means two things:
/// the release of a hold on a bare Alt both ends the hold and is the moment
/// the menu-suppression phantom must fire, so the trigger's key-up returns
/// <see cref="HoldEnded"/> and <see cref="SuppressMenu"/> together. Callers
/// must test with <c>&amp;</c> or <c>HasFlag</c> and never with <c>==</c> — an
/// equality check against <see cref="HoldEnded"/> silently drops the phantom,
/// which is precisely the shape of the defect that let Windows 11 Notepad eat
/// a 358-character transcript.
/// </remarks>
[Flags]
public enum HoldKeyTransition
{
    /// <summary>
    /// Nothing to do. Every event for a key that is not the trigger lands here,
    /// and so does an autorepeat on a trigger that does not arm menu mode, and
    /// so does the second and every later autorepeat within one hold.
    /// <b>Zero deliberately</b>: a defaulted value must mean "do nothing", not
    /// "open the microphone".
    /// </summary>
    None = 0,

    /// <summary>The trigger went down and the hold is now open.</summary>
    HoldStarted = 1,

    /// <summary>The trigger came up and the hold is now closed.</summary>
    HoldEnded = 2,

    /// <summary>
    /// The caller must inject the menu-suppression keystroke now, because the
    /// trigger is a bare Alt and the target is about to see a lone Alt tap.
    /// See <see cref="HoldKeyState"/>'s remarks for when it fires and why.
    /// </summary>
    SuppressMenu = 4,

    /// <summary>
    /// Escape was pressed while the host had the Escape arm up — a dictation
    /// listening or transcribing, or, in the standalone app, a press waiting
    /// for its password-field check. The caller must abandon it: nothing is
    /// transcribed, nothing is inserted.
    /// </summary>
    /// <remarks>
    /// Also raised, when the state machine was built with
    /// <c>cancelOnChord</c>, the moment another key goes down while the
    /// trigger is held — see <see cref="HoldKeyState"/>'s chord remarks.
    /// For Escape it fires <em>only</em> while
    /// <see cref="HoldKeyState.DictationInFlight"/>
    /// is set. Escape at rest is one of the most-pressed keys on the keyboard
    /// and must stay completely inert here — a trigger layer that reacted to it
    /// while idle would be doing work in the system input path on every dialog
    /// somebody dismissed.
    /// </remarks>
    CancelRequested = 8,

    /// <summary>
    /// The caller must consume this event: it never reaches the focused
    /// application. From this machine it is raised only in
    /// <see cref="HoldKeyTriggerMode.HoldOrDoubleTapHandsFree"/>, only for
    /// Space and Enter, and only for the down that ended a latch, its
    /// autorepeats and its matching up. The one exception to "the keyboard
    /// hook observes and never swallows": a stop key that went through would
    /// type a space or a newline, or send a half-written chat message, before
    /// the dictated words land. The mouse hook adds it for the bound side
    /// button (<see cref="DictationMouseMessages.Swallows"/>).
    /// </summary>
    Swallow = 16,

    /// <summary>
    /// Escape was pressed on a dictation the host asked to confirm first
    /// (<see cref="HoldKeyState.EscapeAsksFirst"/>): nothing was abandoned and
    /// no latch ended, and the next Escape cancels. The caller tells the host,
    /// which asks the user. Never raised while the host has not asked.
    /// </summary>
    EscapeAsked = 32,
}

/// <summary>
/// How the trigger key starts and stops a dictation.
/// </summary>
public enum HoldKeyTriggerMode
{
    /// <summary>
    /// Hold the key for the whole sentence. The default, and <b>zero
    /// deliberately</b>: a caller that sets nothing gets the plainest gesture.
    /// (The app arms <see cref="HoldOrDoubleTapHandsFree"/> explicitly.)
    /// </summary>
    HoldOnly = 0,

    /// <summary>
    /// Hold works exactly as before, AND a double-tap starts a dictation that
    /// keeps running with the key released until a second double-tap ends it.
    /// </summary>
    /// <remarks>
    /// <b>Double-tap and not single-tap</b>, which is the difference between
    /// this and the mouse button's <see cref="MouseHoldOrClickState"/>, as on
    /// the Mac. That one toggles on a single click because a spare mouse
    /// button is pressed for no other reason. The keyboard triggers this app offers are bare modifiers —
    /// Right Alt, Right Ctrl, Right Shift — which people press by accident and
    /// on purpose all day long. A single tap latching the microphone open would
    /// mean a brushed Alt started recording, which is the one outcome a
    /// dictation feature must never produce.
    /// </remarks>
    HoldOrDoubleTapToggle = 1,

    /// <summary>
    /// Hold works as before, AND two presses of the trigger no more than
    /// <see cref="HoldKeyState.HandsFreeWindowMs"/> apart, press to press,
    /// latch the dictation open with the key released, until ONE more press,
    /// or Space or Enter (swallowed), ends it. The macOS Dictation app's
    /// gesture, and the standalone Windows app's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not <see cref="HoldOrDoubleTapToggle"/> with different numbers.</b>
    /// That mode measures its pair from a release to the next press, latches
    /// on the second release and stops on a second double-tap. This one
    /// measures press to press, latches on the second press, stops on one
    /// press and keeps a short press's hold open until press + window, so a
    /// lone quick tap still transcribes. See <c>HoldKeyState.HandsFree.cs</c>.
    /// </para>
    /// <para>
    /// A separate toggle key is not used in this mode.
    /// </para>
    /// </remarks>
    HoldOrDoubleTapHandsFree = 2,
}

/// <summary>
/// The decision half of hold-to-talk: which keyboard events open a hold, which
/// close it, and when the menu-suppression keystroke has to be injected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and platform-neutral, on purpose.</b> This is the same shape as
/// the mouse button's <see cref="MouseHoldOrClickState"/>, for the same
/// stated reason: the trigger layer is where every dictation begins, a
/// regression in it does not
/// degrade dictation but removes it, and none of that logic can be exercised
/// while it lives inside a <c>WH_KEYBOARD_LL</c> callback. Before this type
/// existed, <c>LowLevelHoldKeyRegistration</c> — the held flag, the
/// close-an-open-hold-on-teardown rule, and the phantom-keystroke branch that
/// only fires for a bare Alt — had zero references from any test in the tree,
/// and the end-to-end gate could not have caught a break because it drives the
/// worker protocol rather than the hook.
/// </para>
/// <para>
/// <b>The phantom keystroke, and why it is a branch worth testing.</b> The hook
/// observes and never swallows the trigger (the one key it ever swallows is a
/// hands-free stop key, <see cref="HoldKeyTransition.Swallow"/>), so from the
/// focused application's point of view
/// a dictation hold on Alt is also a lone Alt tap: on release the target arms
/// its menu bar, and the Ctrl+V the inserter sends a moment later is eaten
/// navigating a menu instead of pasting the transcript. That was a real shipped
/// defect. The answer is to break the "nothing was pressed between Alt-down and
/// Alt-up" condition by injecting one inert keystroke while the hold is open.
/// It must happen for a bare Alt and must NOT happen for anything else — three
/// of the five triggers a user can now pick (Right Ctrl, Right Shift, F13) take
/// the other path — and it must happen once, not once per autorepeat.
/// </para>
/// <para>
/// <b>On the repeat, not on the initial press.</b> The hook runs BEFORE the
/// event it is inspecting is delivered, so an injection made while processing
/// the initial down reaches the target ahead of the Alt itself; the target then
/// still sees a lone Alt and arms its menu anyway. That was measured. The
/// repeat event proves the delivered Alt-down is behind us, so the phantom
/// lands where it must — between the target's Alt-down and its Alt-up. A tap
/// too quick to repeat is also too quick to dictate: no transcript, no paste,
/// nothing for an armed menu to eat.
/// </para>
/// <para>
/// <b>And again on the release, which is the one that actually holds.</b> The
/// repeat phantom above is necessary and was never sufficient: a menu bar
/// re-arms on EVERY Alt key-down, and a hold delivers autorepeat downs for its
/// whole length, so a phantom placed once at the front of that stream is undone
/// by every repeat behind it. That shipped, and Windows 11 Notepad ate a
/// 358-character transcript with every log line reporting success. The phantom
/// that decides is the LAST Alt-adjacent event before the Alt-up, so
/// <see cref="OnUp"/> emits one too — measured end to end, and the comment
/// there carries the three-way result. The repeat phantom is kept because it is
/// one inert key event and it covers a target that arms on the down edge
/// instead. What is deliberately NOT done is a phantom per repeat: measured to
/// help nothing, and a stream of injected events through the user's sentence is
/// exactly what this design refuses.
/// </para>
/// <para>
/// <b>Threading.</b> Every mutation happens on the hook thread, which is the
/// thread that installed the hook, which is the WPF dispatcher thread — the
/// same thread the release poll reads <see cref="IsHeld"/> from. The backing
/// field is <c>volatile</c> anyway, carried over from the registration this was
/// lifted out of: it costs nothing and it removes the need for a reader to
/// re-derive that argument.
/// </para>
/// </remarks>
public sealed partial class HoldKeyState
{
    /// <summary><c>WM_KEYDOWN</c>.</summary>
    public const int WmKeyDown = 0x0100;

    /// <summary><c>WM_KEYUP</c>.</summary>
    public const int WmKeyUp = 0x0101;

    /// <summary>
    /// <c>WM_SYSKEYDOWN</c> — what a bare Alt produces instead of
    /// <see cref="WmKeyDown"/>. Missing this pair is why an Alt binding would
    /// appear dead.
    /// </summary>
    public const int WmSysKeyDown = 0x0104;

    /// <summary><c>WM_SYSKEYUP</c>.</summary>
    public const int WmSysKeyUp = 0x0105;

    private const int VkLeftAlt = 0xA4;
    private const int VkRightAlt = 0xA5;

    /// <summary><c>VK_LWIN</c>.</summary>
    private const int VkLeftWin = 0x5B;

    /// <summary><c>VK_RWIN</c>.</summary>
    private const int VkRightWin = 0x5C;

    /// <summary>
    /// <c>VK_ESCAPE</c> — the way out of a dictation you have already decided
    /// against.
    /// </summary>
    /// <remarks>
    /// macOS has had this since the beginning: <c>startEscapeWatch()</c> installs
    /// a global and a local monitor for keyCode 53 for as long as a dictation is
    /// running, and Escape ends it with nothing typed. Windows had the whole
    /// cancelled path — the outcome, the result, the neutral pill — reachable
    /// only from the supersede guards, so no key a user could press led to it.
    /// The hook was already seeing every Escape; it simply had no opinion.
    /// </remarks>
    public const int VkEscape = 0x1B;

    /// <summary>
    /// The longest a press-and-release can be and still count as a TAP rather
    /// than as a (very short) hold.
    /// </summary>
    /// <remarks>
    /// Comfortably longer than a deliberate double-tap's individual presses and
    /// comfortably shorter than the shortest hold anybody dictates a word into
    /// — <c>MicrophoneTapOptions.MinimumHold</c> already refuses anything under
    /// its own floor as "a key brushed on the way past". A number in between is
    /// what makes the two gestures distinguishable without asking the user to
    /// be precise.
    /// </remarks>
    public const int TapMaxMs = 300;

    /// <summary>
    /// How long after a tap's release a second press still counts as the same
    /// double-tap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This window is also, deliberately, how long the microphone stays open
    /// after a single stray tap in toggle mode: the hold is held OPEN across
    /// the gap so that a real double-tap flows into one continuous capture with
    /// no gap in the audio and no spurious "that was too short" in between.
    /// </para>
    /// <para>
    /// The cost of that is bounded and already familiar: a stray Alt brush ends
    /// as a ~700 ms capture of silence rather than an ~80 ms one, and both end
    /// the same way — a caution pill saying nothing was heard, which is exactly
    /// what a stray Alt brush produces today.
    /// </para>
    /// </remarks>
    public const int DoubleTapGapMs = 400;

    private readonly HoldKeyTriggerMode _mode;

    /// <summary>
    /// The separate toggle key, or 0. See the constructor parameter.
    /// </summary>
    private readonly int _toggleVirtualKey;

    /// <summary>
    /// When the separate toggle key was last tapped, plus
    /// <see cref="DoubleTapGapMs"/>; 0 when no tap is pending.
    /// </summary>
    /// <remarks>
    /// Its OWN window, kept apart from <see cref="_graceUntilMs"/>. That one
    /// also holds the microphone open across the gap, because on the shared-key
    /// gesture the first tap has already opened a capture that a real
    /// double-tap must flow into without a seam. A separate toggle key has
    /// opened nothing, so its first tap must NOT make
    /// <see cref="IsHeldAt"/> true — a stray brush of the toggle key would
    /// otherwise record 400 ms of audio for no reason.
    /// </remarks>
    private long _toggleGraceUntilMs;

    /// <summary>When the separate toggle key went down. Hook thread only.</summary>
    private long _toggleDownAtMs;

    /// <summary>Whether the toggle key is physically down right now.</summary>
    private bool _toggleDown;

    private volatile bool _physicallyDown;

    /// <summary>
    /// Whether the press now held was cancelled by a chord or by Escape. The
    /// key is still physically down, but the hold is over:
    /// <see cref="IsHeldAt"/> answers false and autorepeats open nothing
    /// until the key comes up.
    /// </summary>
    private volatile bool _holdAbandoned;

    private readonly bool _cancelOnChord;
    private volatile bool _latched;
    private volatile bool _dictationInFlight;
    private bool _menuSuppressedThisHold;

    /// <summary>When the current physical press started. Hook thread only.</summary>
    private long _downAtMs;

    /// <summary>
    /// The instant the post-tap grace window closes, or <c>0</c> when no tap is
    /// pending. Volatile because <see cref="IsHeldAt"/> reads it from the poll.
    /// </summary>
    private long _graceUntilMs;

    /// <summary>
    /// Whether the press now in progress arrived inside another tap's grace
    /// window, and so is the second half of a double-tap. Decided on the DOWN
    /// and acted on at the UP, because a press that turns into a long hold is
    /// not a double-tap at all.
    /// </summary>
    private bool _pressCompletesDoubleTap;

    /// <summary>
    /// Whether ANOTHER key went down while the trigger was held — a chord,
    /// not a gesture. Hook thread only; published at the UP edge.
    /// </summary>
    /// <remarks>
    /// This is the AltGr guard (2026-08-21 QA audit, ship-blocker S1). Right
    /// Alt IS AltGr on every non-US layout, and AltGr is pressed dozens of
    /// times a minute to type @ € { } — each one a sub-300 ms "tap" that the
    /// tap-toggle latch would read as "open the microphone". A chorded press
    /// is typing; only a CLEAN tap may latch. Windows also synthesises a
    /// LEFT CTRL down immediately before every AltGr down on those layouts,
    /// so a trigger press arriving inside <see cref="AltGrSynthesisWindowMs"/>
    /// of a Left Ctrl down is marked chorded from the start — which vetoes
    /// even a bare AltGr brush with no second key.
    /// </remarks>
    private bool _chordSeenThisHold;

    /// <summary>
    /// Whether the most recently COMPLETED hold was chorded. Volatile: the
    /// latch veto reads it from the UI thread at release time.
    /// </summary>
    private volatile bool _lastHoldWasChord;

    /// <summary>When Left Ctrl last went down. Hook thread only.</summary>
    private long _lastLeftCtrlDownAtMs;

    /// <summary><c>VK_LCONTROL</c> — the key AltGr synthesis presses first.</summary>
    public const int VkLeftControl = 0xA2;

    /// <summary><c>VK 0xE8</c> — the unassigned key the hook injects to break
    /// menu-mode arming. Excluded from chord detection: it is our own
    /// injection, not the user typing.</summary>
    public const int VkMenuSuppression = 0xE8;

    /// <summary>
    /// How close a Left Ctrl down must precede the trigger down to read as
    /// AltGr synthesis rather than a user chording Ctrl themselves. The
    /// synthesised pair arrives back-to-back in the same input batch —
    /// single-digit milliseconds apart; 50 is far past that and far under any
    /// deliberate two-key press.
    /// </summary>
    public const int AltGrSynthesisWindowMs = 50;

    /// <param name="triggerVirtualKey">
    /// The one key this trigger watches. Rejected at construction when zero:
    /// a binding with no virtual key can never fire, and a state machine that
    /// silently never fires is indistinguishable from a broken hook.
    /// </param>
    /// <param name="mode">
    /// Whether a double-tap latches. Defaults to
    /// <see cref="HoldKeyTriggerMode.HoldOnly"/>, which is the shipped gesture
    /// and leaves every code path below behaving exactly as it did before the
    /// toggle existed.
    /// </param>
    /// <param name="toggleVirtualKey">
    /// A SECOND key whose double-tap latches a dictation open, leaving the
    /// trigger to do nothing but hold. Zero — the default — keeps the shipped
    /// behaviour, where the toggle rides the trigger key itself.
    /// <para>
    /// Two keys is macOS's shape: <c>defaultHold</c> is Option-hold and
    /// <c>defaultDoubleTap</c> is Control-double-tap, two separate bindings a
    /// user can hold in their head separately. On Windows the toggle reused the
    /// hold key, so turning it on CHANGED what the hold key did rather than
    /// adding a second gesture — and a user who wanted hold on one key and
    /// toggle on another simply could not have it.
    /// </para>
    /// </param>
    /// <param name="cancelOnChord">
    /// Whether another key going down while the trigger is held cancels that
    /// hold. Off by default, which keeps the chord a latch veto only.
    /// <para>
    /// The standalone Dictation app turns it on. Its default trigger is Right
    /// Ctrl, which is half of every Ctrl shortcut, and the hook never swallows
    /// the trigger or a key chorded with it: without this, Right
    /// Ctrl+Shift+Arrow held past the 300 ms floor
    /// was a full dictation whose transcript pasted over the selection the
    /// user was making. A chorded hold is typing, and typing is not
    /// dictation. The cancel is sticky until the trigger comes up, so the
    /// trigger's own autorepeat cannot restart what the chord ended.
    /// </para>
    /// <para>
    /// Two keys do not count: the phantom this hook injects
    /// (<see cref="VkMenuSuppression"/>), and — for a Right Alt trigger — Left
    /// Ctrl, because on every layout with AltGr Windows synthesises a Left
    /// Ctrl press alongside Right Alt, repeats included.
    /// </para>
    /// </param>
    public HoldKeyState(
        int triggerVirtualKey,
        HoldKeyTriggerMode mode = HoldKeyTriggerMode.HoldOnly,
        int toggleVirtualKey = 0,
        bool cancelOnChord = false)
    {
        if (triggerVirtualKey == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(triggerVirtualKey),
                "A hold trigger needs a virtual key; a binding with none can never fire.");
        }

        TriggerVirtualKey = triggerVirtualKey;
        TriggerArmsMenuMode = ArmsMenuMode(triggerVirtualKey);
        _mode = mode;
        _cancelOnChord = cancelOnChord;

        // A separate toggle key only exists when it is BOTH set and different.
        // Zero means "the toggle rides the trigger", which is the shipped
        // gesture and every existing caller's behaviour; setting it to the
        // trigger's own key means the same thing and is normalised to it here
        // rather than left as a second code path.
        _toggleVirtualKey =
            toggleVirtualKey != 0 && toggleVirtualKey != triggerVirtualKey
                ? toggleVirtualKey
                : 0;
        ToggleArmsMenuMode = _toggleVirtualKey != 0 && ArmsMenuMode(_toggleVirtualKey);
    }

    /// <summary>
    /// Whether a lone press-and-release of this key arms a system menu, and so
    /// needs the phantom keystroke.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alt arms the focused application's menu bar. The Windows key opens the
    /// Start menu, which is the same mechanism one layer up — a lone tap opens
    /// it, and any other key pressed between the down and the up suppresses it.
    /// The Start menu also STEALS FOCUS, so a Right Win hold that opened it
    /// would leave the inserter's Ctrl+V going into the search box rather than
    /// into the user's document.
    /// </para>
    /// <para>
    /// This is why Right Win can be offered as a trigger at all: without the
    /// suppression it is a key that opens Start after every sentence.
    /// </para>
    /// </remarks>
    private static bool ArmsMenuMode(int virtualKey) =>
        virtualKey is VkLeftAlt or VkRightAlt or VkLeftWin or VkRightWin;

    /// <summary>The virtual key this trigger watches.</summary>
    public int TriggerVirtualKey { get; }

    /// <summary>
    /// Whether this trigger is a bare Alt — the one key family whose
    /// press-and-release, seen by the focused application, arms Windows
    /// keyboard menu mode, and therefore the only one that needs the phantom
    /// keystroke.
    /// </summary>
    public bool TriggerArmsMenuMode { get; }

    /// <summary>Whether a double-tap latches on this trigger.</summary>
    public HoldKeyTriggerMode Mode => _mode;

    /// <summary>
    /// The separate key whose double-tap latches, or <c>0</c> when the toggle
    /// rides <see cref="TriggerVirtualKey"/>.
    /// </summary>
    public int ToggleVirtualKey => _toggleVirtualKey;

    /// <summary>
    /// Whether the separate toggle key is one whose lone tap arms a system menu
    /// — see <see cref="TriggerArmsMenuMode"/>. Always false when there is no
    /// separate toggle key.
    /// </summary>
    public bool ToggleArmsMenuMode { get; }

    /// <summary>
    /// Whether Escape means anything to this type right now: a dictation is
    /// running, or the host has a press it may still withdraw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pushed in rather than derived.</b> This type sees keyboard events and
    /// nothing else; whether the recogniser currently holds audio is the
    /// session's fact, and the session is three layers away in a project this
    /// one must not reference. The app's controller sets it from
    /// <c>DictationSession.StateChanged</c> — true for <c>Listening</c> and
    /// <c>Transcribing</c>, false for everything else — and also sets it at
    /// the press while that press waits for its password-field
    /// check, as macOS's Escape monitor is installed at the press, and lowers
    /// it when such a press ends without being admitted.
    /// </para>
    /// <para>
    /// <b>Default false, deliberately.</b> A state machine constructed by a
    /// caller that has never heard of this property behaves exactly as it did
    /// before the property existed: every Escape is
    /// <see cref="HoldKeyTransition.None"/>.
    /// </para>
    /// <para>
    /// <c>volatile</c> for the same reason <see cref="IsHeld"/>'s backing field
    /// is: it is written by the dispatcher thread and read inside the hook
    /// callback, which is the same thread in the shipped composition but does
    /// not have to be for this to be correct.
    /// </para>
    /// </remarks>
    public bool DictationInFlight
    {
        get => _dictationInFlight;
        set => _dictationInFlight = value;
    }

    /// <summary>
    /// Whether a dictation is latched open with the key released — true only
    /// between the double-tap that started it and whatever ended it: in the
    /// toggle mode a second double-tap, in the hands-free mode one press or a
    /// stop key, and in both Escape, a chord, <see cref="EndLatch"/> or
    /// <see cref="Release"/>.
    /// </summary>
    public bool IsLatched => _latched;

    /// <summary>
    /// Whether the most recently completed hold had another key pressed
    /// during it (or was AltGr-synthesised) — typing, not a gesture. The
    /// tap-toggle latch consults this and refuses to latch on a chorded tap.
    /// </summary>
    public bool LastHoldWasChord => _lastHoldWasChord;

    /// <summary>
    /// Whether the trigger is effectively down right now, at the given instant.
    /// </summary>
    /// <param name="nowMs">
    /// A monotonic millisecond clock — <c>Environment.TickCount64</c> in the
    /// shipped caller. Passed in rather than read here so the grace window can
    /// be driven from a test without sleeping through it.
    /// </param>
    /// <remarks>
    /// Three ways this is true, and only the first exists in
    /// <see cref="HoldKeyTriggerMode.HoldOnly"/>: the key is physically down
    /// on a press that holds something; a dictation is latched; or a tap has
    /// just ended and the second half of a possible double-tap has not
    /// arrived yet (the toggle mode's grace after the release, the hands-free
    /// mode's park until press + window). The third is what makes a
    /// double-tap produce ONE continuous capture instead of two aborted ones
    /// with a gap between them.
    /// </remarks>
    public bool IsHeldAt(long nowMs) =>
        (_physicallyDown && !_holdAbandoned && !_pressHoldsNothing)
        || _latched
        || nowMs < _graceUntilMs
        || IsParkedAt(nowMs);

    /// <summary>
    /// <see cref="IsHeldAt"/> against the process uptime clock. The shipped
    /// read; monotonic, so a clock change cannot make a hold appear to end.
    /// </summary>
    public bool IsHeld => IsHeldAt(Environment.TickCount64);

    /// <summary>
    /// Fold one keyboard event in and say what it means.
    /// </summary>
    /// <param name="virtualKey">The event's virtual key code.</param>
    /// <param name="message">
    /// The window message — one of the four <c>WM_</c> constants on this type.
    /// Anything else is <see cref="HoldKeyTransition.None"/>: the hook is
    /// installed low-level and sees traffic this trigger has no opinion about.
    /// </param>
    /// <param name="nowMs">A monotonic millisecond clock; see <see cref="IsHeldAt"/>.</param>
    /// <returns>
    /// What the caller must do about it, if anything. A flags value: one event
    /// can both end a hold and require the menu-suppression keystroke.
    /// </returns>
    public HoldKeyTransition Observe(int virtualKey, int message, long nowMs)
    {
        if (virtualKey != TriggerVirtualKey)
        {
            // ── The Escape arm ────────────────────────────────────────────
            //
            // Checked here, AFTER the trigger comparison has already failed, so
            // a user who has somehow bound the trigger to Escape keeps the
            // trigger: a key cannot both start a dictation and abandon it.
            //
            // Gated on DictationInFlight and on the DOWN edge only. Escape at
            // rest returns None from the same single comparison every other
            // non-trigger key does, which is what keeps this off the cost of
            // the system input path. The Escape that asked, held, repeats:
            // its autorepeats are no second press (HoldKeyState.EscapeAsk.cs).
            if (virtualKey == VkEscape && IsAskingEscapeRepeat(message))
            {
                return HoldKeyTransition.None;
            }

            if (virtualKey == VkEscape
                && _dictationInFlight
                && message is WmKeyDown or WmSysKeyDown)
            {
                if (TakeEscapeAsk())
                {
                    // A long take asks before it is thrown away
                    // (HoldKeyState.EscapeAsk.cs): this Escape abandons
                    // nothing and ends no latch, and the next one cancels.
                    return HoldKeyTransition.EscapeAsked;
                }

                // The hold is closed here, not left for the user's eventual
                // release. Otherwise the microphone stays open — audio nobody
                // will ever use — until they think to let go of a key whose
                // dictation they just abandoned. The release poll sees the
                // hold over and runs the ordinary teardown.
                //
                // A trigger still held is ABANDONED, as a chord abandons it,
                // and is not marked up: it is still down, and its autorepeats
                // keep arriving. Marked up, the next autorepeat read as a
                // fresh press and opened a new dictation the user had just
                // cancelled, which (with no minimum hold in the hands-free
                // mode) was then transcribed and pasted. Abandoned, its
                // autorepeats open nothing and its key-up reports nothing.
                if (_physicallyDown)
                {
                    _holdAbandoned = true;
                }

                if (_latched)
                {
                    LatchEndedBy(LatchEnd.Escape);
                }

                _latched = false;
                _graceUntilMs = 0;
                _pressCompletesDoubleTap = false;
                // And the separate toggle key's own pending tap, or the next
                // tap of it would complete a double-tap the user began before
                // they changed their mind.
                _toggleGraceUntilMs = 0;
                // And the hands-free pair in progress: the next double-tap
                // latches afresh.
                ForgetHandsFreeGesture();
                return HoldKeyTransition.CancelRequested;
            }

            // ── The hands-free stop keys ──────────────────────────────────
            //
            // Space and Enter, only while a hands-free latch records. None
            // for everything else, which carries on below.
            var stop = ObserveStopKey(virtualKey, message);
            if (stop != HoldKeyTransition.None)
            {
                return stop;
            }

            // ── The separate toggle key ───────────────────────────────────
            //
            // Only when one was configured AND the gesture is on. It does
            // nothing but latch: the trigger key above keeps doing nothing but
            // hold, which is the whole point of splitting them.
            if (_toggleVirtualKey != 0
                && virtualKey == _toggleVirtualKey
                && _mode == HoldKeyTriggerMode.HoldOrDoubleTapToggle)
            {
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    return OnToggleDown(nowMs);
                }

                if (message is WmKeyUp or WmSysKeyUp)
                {
                    return OnToggleUp(nowMs);
                }
            }

            // The chord guard. Any OTHER key going down while the trigger is
            // physically held marks this hold as typing, not a gesture — and
            // a Left Ctrl down is remembered so the AltGr-synthesised pair
            // (LCtrl-down then RMenu-down) marks the NEXT trigger press as
            // chorded from its first instant. See _chordSeenThisHold.
            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (virtualKey == VkLeftControl)
                {
                    _lastLeftCtrlDownAtMs = nowMs;
                }

                // NOT the menu-suppression key. The hook injects VK 0xE8
                // DURING the hold (on the first autorepeat) to break menu-mode
                // arming — our own injection must not read as the user
                // chording, or a deliberate tap on a zero-repeat-delay
                // keyboard self-vetoes its latch (QA verify NEW-10; the guard
                // was previously correct only by grace of the OS repeat
                // delay exceeding TapMaxMs).
                if (_physicallyDown && virtualKey != VkMenuSuppression)
                {
                    _chordSeenThisHold = true;
                    // A chorded press is typing, never the first half of a
                    // hands-free pair.
                    _pairPending = false;

                    // A press that holds nothing (one that latched or stopped
                    // a hands-free latch) is not a hold a chord could cancel:
                    // with the key down on the press that latched, a letter is
                    // the user working, as on the Mac.
                    if (_cancelOnChord && !_holdAbandoned && !_pressHoldsNothing && !IsAltGrCompanion(virtualKey))
                    {
                        AbandonHold();
                        return HoldKeyTransition.CancelRequested;
                    }
                }
            }

            // Includes the injected suppression key on its way back through the
            // hook. One comparison, and it is discarded — which is what keeps
            // the injection from re-entering itself.
            return HoldKeyTransition.None;
        }

        // The trigger key, with a separate toggle key configured, does nothing
        // but hold. Reached below through OnDown/OnUp, which read _mode — so
        // the split is enforced there rather than by a second copy of the
        // gesture logic.

        if (HandsFree)
        {
            return ObserveHandsFreeTrigger(message, nowMs);
        }

        if (message is WmKeyDown or WmSysKeyDown)
        {
            return OnDown(nowMs);
        }

        if (message is WmKeyUp or WmSysKeyUp)
        {
            return OnUp(nowMs);
        }

        return HoldKeyTransition.None;
    }

    /// <summary><see cref="Observe(int, int, long)"/> against the process uptime clock.</summary>
    public HoldKeyTransition Observe(int virtualKey, int message) =>
        Observe(virtualKey, message, Environment.TickCount64);

    private HoldKeyTransition OnDown(long nowMs)
    {
        if (_physicallyDown)
        {
            return OnAutorepeat();
        }

        // Was anything effectively open before this press? A press inside a
        // grace window, or during a latched dictation, must NOT be reported as
        // a new hold — the capture is already running and restarting it would
        // cut the sentence in two.
        var wasHeld = IsHeldAt(nowMs);

        // ToggleRidesTrigger, not _mode: with a SEPARATE toggle key the
        // trigger is hold-only, and treating a quick double press of it as a
        // latch would give the user two ways to latch, one of which they never
        // asked for.
        _pressCompletesDoubleTap = ToggleRidesTrigger && nowMs < _graceUntilMs;

        BeginPhysicalPress(nowMs);

        return wasHeld ? HoldKeyTransition.None : HoldKeyTransition.HoldStarted;
    }

    /// <summary>
    /// An autorepeat of the held trigger. On the FIRST one, not the initial
    /// press: this hook runs BEFORE the event it is inspecting is delivered,
    /// so an injection made while processing the initial down reaches the
    /// target ahead of the Alt itself and the target still sees a lone Alt
    /// (measured; the initial-press variant changed nothing). The repeat
    /// proves the delivered Alt-down is behind us.
    /// </summary>
    private HoldKeyTransition OnAutorepeat()
    {
        if (TriggerArmsMenuMode && !_menuSuppressedThisHold)
        {
            _menuSuppressedThisHold = true;
            return HoldKeyTransition.SuppressMenu;
        }

        return HoldKeyTransition.None;
    }

    /// <summary>The trigger physically went down: what every mode records
    /// about a new press.</summary>
    private void BeginPhysicalPress(long nowMs)
    {
        _physicallyDown = true;
        _holdAbandoned = false;
        _externalEndDuringPress = false;
        _downAtMs = nowMs;
        _menuSuppressedThisHold = false;
        // Chorded from the start when this press is the second half of an
        // AltGr synthesis pair (see the field's remarks). Otherwise the hold
        // begins clean and only another key's down can mark it.
        _chordSeenThisHold =
            _lastLeftCtrlDownAtMs != 0
            && nowMs - _lastLeftCtrlDownAtMs <= AltGrSynthesisWindowMs;
    }

    private HoldKeyTransition OnUp(long nowMs)
    {
        if (!_physicallyDown)
        {
            // A key-up for a press this state machine never saw start. It
            // happens on the very first event after the hook is installed if
            // the user was already resting on the trigger, and reporting it as
            // a release would hand the caller a hold it never saw open.
            return HoldKeyTransition.None;
        }

        _physicallyDown = false;
        _menuSuppressedThisHold = false;
        // Publish the chord verdict for THIS hold; the latch veto reads it
        // at the release that follows within the same input batch.
        _lastHoldWasChord = _chordSeenThisHold;
        _chordSeenThisHold = false;

        if (_holdAbandoned)
        {
            // A chord or Escape already ended this hold and said so. Nothing
            // opens, nothing closes, and no phantom is needed: nothing will be
            // pasted for an armed menu to eat, and a chord key was itself the
            // keystroke between the trigger's down and up that stops a menu
            // arming.
            _holdAbandoned = false;
            return HoldKeyTransition.None;
        }

        var wasTap = nowMs - _downAtMs <= TapMaxMs;

        // ── The phantom on the RELEASE edge ───────────────────────────────
        //
        // The phantom OnDown fires on the first autorepeat is not enough, and
        // this is the line that carries the guarantee. A menu bar re-arms on
        // EVERY Alt key-down it sees, and a physical hold delivers a stream of
        // autorepeat downs for its whole length — so a phantom placed once, at
        // the front of that stream, is undone by every repeat that follows it.
        // The last Alt-adjacent event the target sees before the Alt-up is what
        // decides, so the phantom has to go HERE.
        //
        // MEASURED on the Snapdragon X Elite, 2026-08-20, against Windows 11
        // Notepad, driving a real Right-Alt hold with its full autorepeat
        // stream and then reading GUITHREADINFO.hwndFocus and the document's
        // UIA TextPattern:
        //
        //   phantom on the first repeat only (what shipped)
        //                                   focus = InputSiteWindowClass, paste LOST
        //   no phantom at all               focus = InputSiteWindowClass, paste LOST
        //   phantom just before the Alt-up  focus = RichEditD2DPT,        paste LANDS
        //
        // The ordering works because the hook runs BEFORE the event it is
        // inspecting is delivered: an injection made while observing the up
        // lands between the target's last Alt-down and its Alt-up, which is
        // exactly the "something was pressed in between" the arming tests for.
        // That is the same reasoning the toggle-mode tap already documented —
        //
        //   baseline                        focus = Document "Text editor"
        //   lone Alt tap, no injection      focus = MenuItem "File"      <- armed
        //   lone Alt tap, injection on up   focus = Document "Text editor"
        //
        // — now applied to the edge that a HOLD actually reaches.
        //
        // NOT once per repeat: a stream of injected key events through the
        // user's sentence was measured to help nothing (repeated phantoms lose
        // to the repeat that follows the last one just as a single early one
        // does) and it is the thing this module's remarks refuse outright.
        //
        // A hold-only TAP still gets nothing. It is too quick to dictate, so
        // there is no paste behind it for an armed menu to eat, and injecting
        // there would put a keystroke in the user's session for no reason.
        // In toggle mode a tap IS a gesture with a paste behind it, which is
        // why that mode's taps are included.
        var suppress = TriggerArmsMenuMode && (!wasTap || ToggleRidesTrigger)
            ? HoldKeyTransition.SuppressMenu
            : HoldKeyTransition.None;

        if (_externalEndDuringPress)
        {
            // EndLatch already ended this gesture (the limit, a cancel, a
            // refused press). Its physical release still lands, but cannot
            // seed a new double-tap window.
            _externalEndDuringPress = false;
            return HoldKeyTransition.HoldEnded | suppress;
        }

        if (!ToggleRidesTrigger)
        {
            // Hold-only, either because the gesture is off or because the latch
            // lives on its own key. A latch opened by that other key is NOT
            // ended here: the two gestures are independent, and a hold that
            // happened during a latched dictation is a key the user pressed for
            // some other reason.
            _pressCompletesDoubleTap = false;
            return (_latched ? HoldKeyTransition.None : HoldKeyTransition.HoldEnded)
                | suppress;
        }

        // ── Toggle mode from here down ────────────────────────────────────

        if (!wasTap)
        {
            // A real hold ended. If a dictation was latched, this ends that too:
            // holding the key through a sentence and letting go is the same
            // "I am done" the double-tap means, and leaving the latch on after
            // it would strand the microphone open.
            _pressCompletesDoubleTap = false;
            _graceUntilMs = 0;
            _latched = false;
            return HoldKeyTransition.HoldEnded | suppress;
        }

        if (_pressCompletesDoubleTap)
        {
            _pressCompletesDoubleTap = false;
            _graceUntilMs = 0;

            if (_latched)
            {
                // The second double-tap. The dictation ends here, which is what
                // the poll sees on its next tick.
                _latched = false;
                return HoldKeyTransition.HoldEnded | suppress;
            }

            // The first double-tap. The capture that the grace window has been
            // holding open simply keeps running, with the key released.
            _latched = true;
            return suppress;
        }

        if (_latched)
        {
            // A single stray tap during a latched dictation. It opens a grace
            // window like any other tap — a second tap inside it ends the
            // dictation — but on its own it changes nothing, because the latch
            // already holds the capture open.
            _graceUntilMs = nowMs + DoubleTapGapMs;
            return suppress;
        }

        // The first tap of a possible double-tap. The hold is held OPEN across
        // the gap: this is what makes a real double-tap one continuous capture
        // instead of two aborted ones, and it is why nothing is reported as
        // ended here.
        _graceUntilMs = nowMs + DoubleTapGapMs;
        return suppress;
    }

    /// <summary>
    /// Whether the double-tap latch rides the trigger key itself — the shipped
    /// gesture — rather than a separate key.
    /// </summary>
    private bool ToggleRidesTrigger =>
        _mode == HoldKeyTriggerMode.HoldOrDoubleTapToggle && _toggleVirtualKey == 0;

    /// <summary>
    /// The separate toggle key went down.
    /// </summary>
    /// <remarks>
    /// Nothing is decided here. A press that turns into a long hold is not a
    /// double-tap at all, so the verdict waits for the release — the same rule
    /// the shared-key gesture keeps.
    /// </remarks>
    private HoldKeyTransition OnToggleDown(long nowMs)
    {
        if (_toggleDown)
        {
            // An autorepeat. A held toggle key is not a gesture, so nothing to
            // do — and deliberately no phantom keystroke either: the phantom
            // exists to stop a lone TAP arming a menu, and the tap path below
            // is where it belongs.
            return HoldKeyTransition.None;
        }

        _toggleDown = true;
        _toggleDownAtMs = nowMs;
        return HoldKeyTransition.None;
    }

    /// <summary>
    /// The separate toggle key came up. A tap inside another tap's window
    /// flips the latch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The grace window here does NOT hold the microphone open.</b> On the
    /// shared-key gesture it must, because the first tap already opened a
    /// capture that a real double-tap has to flow into with no seam in the
    /// audio. A separate toggle key has opened nothing, so a stray brush of it
    /// costs nothing at all instead of recording 400 ms of a room.
    /// </para>
    /// <para>
    /// The phantom keystroke fires on a tap of a menu-arming toggle key for the
    /// same reason it fires on a tap of a menu-arming trigger: a tap IS a
    /// gesture here, with a paste behind it, and a lone Alt or Win tap would
    /// arm a menu that eats the inserter's Ctrl+V.
    /// </para>
    /// </remarks>
    private HoldKeyTransition OnToggleUp(long nowMs)
    {
        if (!_toggleDown)
        {
            // A release for a press this machine never saw start — the user was
            // resting on the key when the hook was installed.
            return HoldKeyTransition.None;
        }

        _toggleDown = false;

        var suppress = ToggleArmsMenuMode
            ? HoldKeyTransition.SuppressMenu
            : HoldKeyTransition.None;

        if (nowMs - _toggleDownAtMs > TapMaxMs)
        {
            // Held rather than tapped. Not a gesture, and it cancels any
            // pending first tap: a tap followed by a long press is somebody
            // using the key for its own purpose.
            _toggleGraceUntilMs = 0;
            return HoldKeyTransition.None;
        }

        if (nowMs < _toggleGraceUntilMs)
        {
            // The second tap. Flip.
            _toggleGraceUntilMs = 0;

            if (_latched)
            {
                _latched = false;
                return HoldKeyTransition.HoldEnded | suppress;
            }

            _latched = true;
            return HoldKeyTransition.HoldStarted | suppress;
        }

        // The first tap of a possible double-tap. Nothing opens.
        _toggleGraceUntilMs = nowMs + DoubleTapGapMs;
        return suppress;
    }

    /// <summary>
    /// A key press that is part of the trigger itself rather than a chord:
    /// the Left Ctrl Windows synthesises with every AltGr (Right Alt) press
    /// and repeat on layouts that have AltGr.
    /// </summary>
    private bool IsAltGrCompanion(int virtualKey) =>
        virtualKey == VkLeftControl && TriggerVirtualKey == VkRightAlt;

    /// <summary>
    /// End the current hold because a chord cancelled it, while the key stays
    /// physically down. Clears every way the capture could be kept open, as
    /// the Escape arm does.
    /// </summary>
    private void AbandonHold()
    {
        _holdAbandoned = true;
        _latched = false;
        _graceUntilMs = 0;
        _pressCompletesDoubleTap = false;
        _toggleGraceUntilMs = 0;
    }

    /// <summary>
    /// Close an open hold from outside the keyboard stream — what teardown
    /// does when the hook comes down mid-utterance.
    /// </summary>
    /// <returns>
    /// <see cref="HoldKeyTransition.HoldEnded"/> when a hold was actually open,
    /// <see cref="HoldKeyTransition.None"/> when there was nothing to close.
    /// The distinction is the point: a teardown mid-hold must not leave the
    /// caller believing the key is still pressed and the microphone still
    /// wanted, and a teardown at rest must not synthesise a release nobody
    /// performed.
    /// </returns>
    /// <remarks>
    /// A latched dictation is closed too, and it has to be: the latch is the
    /// only thing holding the microphone open, so a teardown that cleared the
    /// physical flag and left the latch set would leave the capture running
    /// with no hook left to ever end it.
    /// </remarks>
    public HoldKeyTransition Release()
    {
        var wasHeld = IsHeldAt(Environment.TickCount64);

        _menuSuppressedThisHold = false;
        _pressCompletesDoubleTap = false;
        _graceUntilMs = 0;
        _physicallyDown = false;
        _holdAbandoned = false;
        _latched = false;
        _toggleDown = false;
        _toggleGraceUntilMs = 0;
        _chordSeenThisHold = false;
        _askingEscapeDown = false;
        ResetHandsFree();

        return wasHeld ? HoldKeyTransition.HoldEnded : HoldKeyTransition.None;
    }
}
