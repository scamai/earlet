namespace Dictation.Services;

/// <summary>
/// Which key ended a latch, as the hook saw it. For the log: the shell ends
/// a latch some other way (the limit, Cancel, a refusal) and knows why, but a
/// key ends it inside the hook, before the shell looks.
/// </summary>
public enum LatchEnd
{
    /// <summary>No latch was ended by a key since the last read.</summary>
    None = 0,

    /// <summary>One press of the hold key.</summary>
    HoldKey,

    /// <summary>Space or Enter. Which of the two is not kept.</summary>
    StopKey,

    /// <summary>Escape, on the running dictation.</summary>
    Escape,
}

// Part of HoldKeyState (see HoldKeyState.cs): the hands-free mode, the macOS
// Dictation app's double-tap latch, and ending a latch from outside the
// keyboard stream.
public sealed partial class HoldKeyState
{
    // ─── The hands-free mode ────────────────────────────────────────────
    //
    // HoldKeyTriggerMode.HoldOrDoubleTapHandsFree. The rules are the Mac's
    // (DESIGN.md, "Hold key, shortcuts and the hands-free latch"; macOS
    // DoubleTapModifierState and DictationModel.holdStarted / releaseCaptured /
    // latchStarted / latchEnded), so the two apps answer the same gesture the
    // same way:
    //
    //   - Two DOWN edges of the trigger no more than HandsFreeWindowMs apart,
    //     measured press to press, latch. Releases are not part of it.
    //   - The second press takes over the first press's hold, which the
    //     window below kept open: one capture, no gap, nothing transcribed
    //     twice.
    //   - A release sooner than the window after its press parks the hold:
    //     the capture keeps running until press + window, then ends as a
    //     release would. A lone quick tap therefore still transcribes.
    //   - While latched, ONE press of the trigger ends the latch, and for a
    //     window after it a press starts nothing (the trailing tap of a
    //     habitual double-tap stop). Space and Enter end it too, and are
    //     swallowed: the down, its autorepeats and its matching up.
    //   - The press that latches, the press that stops and an absorbed press
    //     hold nothing: chords on them cancel nothing and their releases
    //     release nothing.

    /// <summary>
    /// The hands-free window, in milliseconds: the most two presses can be
    /// apart (press to press) and still latch; how long a short press's hold
    /// is kept open after its press; and how long after a stop press a press
    /// starts nothing. The Mac's <c>doubleTapWindow</c>, 0.35 s.
    /// </summary>
    public const int HandsFreeWindowMs = 350;

    /// <summary><c>VK_SPACE</c>: ends a latch, and is swallowed.</summary>
    public const int VkSpace = 0x20;

    /// <summary>
    /// <c>VK_RETURN</c>: ends a latch, and is swallowed. Keypad Enter is the
    /// same virtual key with <c>LLKHF_EXTENDED</c> set, so this covers it too.
    /// </summary>
    public const int VkReturn = 0x0D;

    /// <summary>
    /// Whether a press is waiting for its second half: the first press of a
    /// possible pair, at <see cref="_pairPressAtMs"/>. A chorded press never
    /// is one, which keeps the AltGr guard: every AltGr press on a layout
    /// that has it is chorded from its first instant.
    /// </summary>
    private bool _pairPending;

    /// <summary>When the pending first press went down.</summary>
    private long _pairPressAtMs;

    /// <summary>
    /// Whether a short press's hold is parked: kept open with the key up
    /// until <see cref="_parkUntilMs"/>, inclusive. Read by
    /// <see cref="IsHeldAt"/>, so the poll sees the hold end just after press
    /// + window without any event to mark it. Inclusive so that a second
    /// press exactly the window after the first, which pairs, still finds the
    /// first press's hold open to take over.
    /// </summary>
    private bool _parked;

    /// <summary>The last instant a parked hold is open: press + window.</summary>
    private long _parkUntilMs;

    /// <summary>Until when a press starts nothing: set by the press that
    /// stopped a latch, to absorb the second tap of a habitual double-tap
    /// stop.</summary>
    private long _absorbUntilMs;

    /// <summary>
    /// The press now down holds nothing: it latched (the latch holds the
    /// capture), it stopped a latch, it was absorbed, or the hold it began
    /// was ended from outside (<see cref="EndLatch"/>). It reads as not held,
    /// a chord on it cancels nothing, and its release releases nothing.
    /// </summary>
    /// <remarks>The same rule the mouse gesture keeps for its stopping press
    /// (<c>MouseHoldOrClickState._stoppingPress</c>).</remarks>
    private bool _pressHoldsNothing;

    /// <summary>Whether the down of Space, or of Enter, was swallowed and its
    /// up has not come yet. Per key, so an up is eaten only if its own down
    /// was: a Space pressed before a latch reaches the focused app whole, its
    /// autorepeats and its release included.</summary>
    private bool _spaceSwallowed;

    /// <summary>See <see cref="_spaceSwallowed"/>.</summary>
    private bool _returnSwallowed;

    /// <summary>
    /// Whether Space, or Enter, is physically down: set by its down, cleared
    /// by its up, in and out of a latch. A low-level hook cannot tell an
    /// autorepeat from a press any other way, and without it the autorepeat
    /// of a Space held since before the latch would read as a stop press:
    /// it would end the latch and be swallowed, and so would that key's up,
    /// so the app that saw the key go down would never see it come up.
    /// </summary>
    /// <remarks>
    /// An up the hook never sees (focus moved to an elevated window
    /// mid-press; see the class remarks on UIPI) leaves this set until the
    /// key's next up, so the one press in between is typed instead of
    /// stopping a latch. It heals on that press's own release.
    /// </remarks>
    private bool _spaceDown;

    /// <summary>See <see cref="_spaceDown"/>.</summary>
    private bool _returnDown;

    /// <summary>
    /// Set while the key is physically down on a press whose gesture was
    /// ended from outside in the toggle mode: its release still lands, as a
    /// release, but cannot seed a new double-tap.
    /// </summary>
    private bool _externalEndDuringPress;

    private bool HandsFree => _mode == HoldKeyTriggerMode.HoldOrDoubleTapHandsFree;

    /// <summary>The <see cref="LatchEnd"/> of the last latch a key ended,
    /// until <see cref="ConsumeLatchEnd"/> reads it. An <c>int</c> for the
    /// read-and-clear.</summary>
    private int _latchEndedBy;

    /// <summary>
    /// Which key ended the most recent latch, once, then
    /// <see cref="LatchEnd.None"/> until a key ends another. A latch ended by
    /// <see cref="EndLatch"/>, <see cref="Release"/> or a chord reads
    /// <see cref="LatchEnd.None"/>: its caller already knows why.
    /// </summary>
    /// <remarks>Read-and-clear, as the hook's cancel latch is, so the hook
    /// thread only stores an int and the shell logs it on its own thread,
    /// once.</remarks>
    public LatchEnd ConsumeLatchEnd() => (LatchEnd)Interlocked.Exchange(ref _latchEndedBy, (int)LatchEnd.None);

    /// <summary>A key ended the latch: remember which, for the shell's log.</summary>
    private void LatchEndedBy(LatchEnd key) => Volatile.Write(ref _latchEndedBy, (int)key);

    /// <summary>
    /// End a latched hold, or a short press's parked hold, from outside the
    /// keyboard stream: the recording limit or a silence stop, Cancel in the
    /// window or the notification-area menu, a press the shell refused, or a
    /// latch the shell found with no dictation behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists.</b> The shell's idea of whether a hold is latched
    /// and this machine's must never drift apart. The Mac learned that the
    /// hard way (its detector kept a flag of its own, and every latch that
    /// ended some other way left the next double-tap backwards), and here a
    /// stale latch is worse: it keeps the microphone open and keeps Space and
    /// Enter swallowed system-wide. Every path that ends a hold without a key
    /// edge must call this.
    /// </para>
    /// <para>
    /// <b>In the hands-free mode</b> a press still physically down stops
    /// being a hold, so its release neither ends anything nor parks; it stays
    /// a possible first press of a pair, because the pair is the user's
    /// gesture and does not care why the shell refused the press (the Mac's
    /// detector is likewise independent of the refusal). A swallowed stop
    /// key's up is still swallowed.
    /// </para>
    /// <para>
    /// <b>In the toggle mode</b> the physical key's own edges keep their
    /// meaning: only the latch and the post-tap grace are cleared, and the
    /// release of a press that was down at the time cannot seed a new
    /// double-tap.
    /// </para>
    /// </remarks>
    public void EndLatch()
    {
        _latched = false;
        _parked = false;
        _graceUntilMs = 0;
        _pressCompletesDoubleTap = false;
        _toggleGraceUntilMs = 0;

        if (!_physicallyDown)
        {
            return;
        }

        if (HandsFree)
        {
            _pressHoldsNothing = true;
        }
        else
        {
            _externalEndDuringPress = true;
        }
    }

    /// <summary>A trigger event in the hands-free mode.</summary>
    private HoldKeyTransition ObserveHandsFreeTrigger(int message, long nowMs)
    {
        if (message is WmKeyDown or WmSysKeyDown)
        {
            return OnHandsFreeDown(nowMs);
        }

        if (message is WmKeyUp or WmSysKeyUp)
        {
            return OnHandsFreeUp(nowMs);
        }

        return HoldKeyTransition.None;
    }

    private HoldKeyTransition OnHandsFreeDown(long nowMs)
    {
        if (_physicallyDown)
        {
            return OnAutorepeat();
        }

        BeginPhysicalPress(nowMs);
        _pressHoldsNothing = false;

        if (_latched)
        {
            // The stop: one press of the trigger ends the latch, and the
            // capture with it, on the press (as the mouse gesture and the Mac
            // do — words spoken while the finger is down were not meant).
            // For a window after it a press starts nothing, so the second tap
            // of a habitual double-tap stop cannot open a fresh dictation.
            _latched = false;
            _parked = false;
            _pairPending = false;
            _absorbUntilMs = nowMs + HandsFreeWindowMs;
            _pressHoldsNothing = true;
            LatchEndedBy(LatchEnd.HoldKey);
            return HoldKeyTransition.HoldEnded;
        }

        if (nowMs < _absorbUntilMs)
        {
            _pressHoldsNothing = true;
            return HoldKeyTransition.None;
        }

        // A short press's parked hold is still open: this press does not
        // start a new one.
        var wasHeld = IsParkedAt(nowMs);
        _parked = false;

        var interval = nowMs - _pairPressAtMs;
        if (_pairPending && !_chordSeenThisHold && interval >= 0 && interval <= HandsFreeWindowMs)
        {
            // The pair. The first press's hold, parked by the window, becomes
            // the latch: the capture keeps running with the key up. When the
            // shell refused the first press there is nothing parked, and this
            // press both starts the hold and latches it.
            _pairPending = false;
            _latched = true;
            _pressHoldsNothing = true;
            return wasHeld ? HoldKeyTransition.None : HoldKeyTransition.HoldStarted;
        }

        // A first press. A reversed clock reads as a fresh one rather than
        // as a negative-length pair, as on the Mac.
        _pairPending = !_chordSeenThisHold;
        _pairPressAtMs = nowMs;
        return wasHeld ? HoldKeyTransition.None : HoldKeyTransition.HoldStarted;
    }

    private HoldKeyTransition OnHandsFreeUp(long nowMs)
    {
        if (!_physicallyDown)
        {
            // A release for a press this machine never saw start.
            return HoldKeyTransition.None;
        }

        _physicallyDown = false;
        _menuSuppressedThisHold = false;
        _lastHoldWasChord = _chordSeenThisHold;
        _chordSeenThisHold = false;

        if (_holdAbandoned)
        {
            // A chord or Escape ended this hold and said so; see OnUp.
            _holdAbandoned = false;
            return HoldKeyTransition.None;
        }

        // Every release in this mode has a paste behind it or near it (a
        // parked tap transcribes, a stop press transcribes, an absorbed tap
        // lands while the stopped dictation is still going in), so a bare Alt
        // or Win trigger always gets the phantom: see OnUp's measurements.
        var suppress = TriggerArmsMenuMode ? HoldKeyTransition.SuppressMenu : HoldKeyTransition.None;

        if (_pressHoldsNothing || _latched)
        {
            _pressHoldsNothing = false;
            return suppress;
        }

        if (nowMs - _downAtMs < HandsFreeWindowMs)
        {
            // Parked: the hold stays open until press + window, so the
            // second press of a pair takes it over, and a lone tap ends there
            // and transcribes. Nothing ends here.
            _parked = true;
            _parkUntilMs = _downAtMs + HandsFreeWindowMs;
            return suppress;
        }

        return HoldKeyTransition.HoldEnded | suppress;
    }

    /// <summary>
    /// Space or Enter, in the hands-free mode: while latched, the down ends
    /// the latch and is swallowed, and so are its autorepeats and its up.
    /// <see cref="HoldKeyTransition.None"/> for every other key, mode and
    /// moment, so the caller carries on with its ordinary checks.
    /// </summary>
    private HoldKeyTransition ObserveStopKey(int virtualKey, int message)
    {
        if (!HandsFree || virtualKey is not (VkSpace or VkReturn))
        {
            return HoldKeyTransition.None;
        }

        ref var swallowed = ref virtualKey == VkSpace ? ref _spaceSwallowed : ref _returnSwallowed;
        ref var down = ref virtualKey == VkSpace ? ref _spaceDown : ref _returnDown;

        if (message is WmKeyDown or WmSysKeyDown)
        {
            var autorepeat = down;
            down = true;

            if (swallowed)
            {
                // An autorepeat of the stop key: the same press.
                return HoldKeyTransition.Swallow;
            }

            if (autorepeat || !_latched)
            {
                // Typed normally: an autorepeat of a press the app already
                // saw go down, or a press outside a latch, where these keys
                // are ordinary (and, with the trigger held, a chord like any
                // other).
                return HoldKeyTransition.None;
            }

            swallowed = true;
            _latched = false;
            _parked = false;
            _pairPending = false;
            LatchEndedBy(LatchEnd.StopKey);
            return HoldKeyTransition.HoldEnded | HoldKeyTransition.Swallow;
        }

        if (message is WmKeyUp or WmSysKeyUp)
        {
            down = false;
            if (swallowed)
            {
                swallowed = false;
                return HoldKeyTransition.Swallow;
            }
        }

        return HoldKeyTransition.None;
    }

    /// <summary>
    /// What Escape on a running dictation clears besides the latch: the pair
    /// in progress and a parked hold, so the next double-tap latches afresh.
    /// </summary>
    private void ForgetHandsFreeGesture()
    {
        _pairPending = false;
        _parked = false;
    }

    /// <summary>Whether a parked hold is still open at <paramref name="nowMs"/>.</summary>
    private bool IsParkedAt(long nowMs) => _parked && nowMs <= _parkUntilMs;

    /// <summary>Everything the hands-free mode keeps, cleared for a teardown.</summary>
    private void ResetHandsFree()
    {
        ForgetHandsFreeGesture();
        _absorbUntilMs = 0;
        _pressHoldsNothing = false;
        _spaceSwallowed = false;
        _returnSwallowed = false;
        _spaceDown = false;
        _returnDown = false;
        _externalEndDuringPress = false;
        Volatile.Write(ref _latchEndedBy, (int)LatchEnd.None);
    }
}
