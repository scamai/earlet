namespace Dictation.Services;

// Part of HoldKeyState (see HoldKeyState.cs): what the hook watch needs. The
// hook's own view of the trigger, and how that view is put right when Windows
// says the key is somewhere the hook never saw it go.
public sealed partial class HoldKeyState
{
    // ─── The hook watch ─────────────────────────────────────────────────
    //
    // A low-level hook that Windows removed (a callback that overran
    // LowLevelHooksTimeout) gets no more events, so this machine's idea of
    // the trigger goes stale: a press it never saw leaves it up, a release it
    // never saw leaves it down with the microphone open. The shell compares
    // that idea with Windows' own key state (GetAsyncKeyState) and, when they
    // disagree for long enough, calls Resync before it hooks the keyboard
    // again (Dictation.Core's DictationHookWatch decides when).

    /// <summary>
    /// Whether this machine last saw the trigger go down and not come up: the
    /// hook's own view of the physical key, which the hook watch compares with
    /// Windows' key state. Unlike <see cref="IsHeldAt"/> it ignores latches,
    /// parked taps and abandoned holds, which are about the dictation, not the
    /// key.
    /// </summary>
    public bool IsPhysicallyDown => _physicallyDown;

    /// <summary>
    /// Put this machine's view of the trigger right after the hook missed an
    /// edge of it, so it agrees with Windows again.
    /// </summary>
    /// <param name="keyDown">Where Windows says the trigger is now.</param>
    /// <param name="nowMs">A monotonic millisecond clock; see <see cref="IsHeldAt"/>.</param>
    /// <returns>What the corrected view means for the hold, as
    /// <see cref="Observe(int, int, long)"/> would say it: a missed release
    /// that ended a hold is <see cref="HoldKeyTransition.HoldEnded"/>.</returns>
    /// <remarks>
    /// <para>
    /// <b>A release it never saw</b> is replayed as the release, now, through
    /// the ordinary path: the user did let go, so the hold ends as a release
    /// ends it (a long hold transcribes; in the hands-free mode a press shorter
    /// than the window parks, a latch is untouched because its key was up).
    /// Left alone, the microphone would stay open until the recording limit.
    /// </para>
    /// <para>
    /// <b>A press it never saw is NOT replayed.</b> It is adopted as down and
    /// holding nothing, as a press a chord cancelled is: its autorepeats open
    /// nothing, a key chorded with it cancels nothing, and its release ends
    /// nothing. Replayed, the hold would start now, a second or more into what
    /// the user is saying, and paste a sentence missing its beginning with
    /// nothing to say so; the shell tells the user to hold the key again
    /// instead. A running latch keeps running: the user's next press stops it.
    /// Never the first press of a double-tap either.
    /// </para>
    /// <para>
    /// Nothing when the views already agree, so a late call is harmless.
    /// Hook thread only, as every other mutation here is.
    /// </para>
    /// </remarks>
    public HoldKeyTransition Resync(bool keyDown, long nowMs)
    {
        if (keyDown == _physicallyDown)
        {
            return HoldKeyTransition.None;
        }

        if (!keyDown)
        {
            // Through the ordinary release, whichever mode: the WM_KEYUP the
            // hook would have been handed.
            return Observe(TriggerVirtualKey, WmKeyUp, nowMs);
        }

        BeginPhysicalPress(nowMs);
        _holdAbandoned = true;
        _pressCompletesDoubleTap = false;
        _pairPending = false;
        return HoldKeyTransition.None;
    }
}
