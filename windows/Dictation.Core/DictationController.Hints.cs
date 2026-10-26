using Dictation.Services;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): what the
// indicator's busy pills say about the hold — the held and hands-free hints,
// the hands-free clock, the countdown in the last minute before the limit and
// the silence warning — and the hold key and policy every pill is sized for.
// The shared partials only call in here: the feed's construction
// (DictationController.cs), ShowSessionState (Session.cs) and SyncLatch
// (Latch.cs), once per tick.
public sealed partial class DictationController
{
    /// <summary>The hold key's name and the hold policy, as the indicator
    /// names and counts them — or the mouse button's name, when the last hold
    /// that listened was on the button. Read at each pill, so a key chosen at
    /// rest is the one the next hold's hints name.</summary>
    private DictationHudHold HudHold =>
        _holdByMouse && DictationMouseButtons.SpokenName(_settings.MouseButton) is { } button
            ? new(button, Policy, ByMouse: true)
            : new(_holdKey.KeyName, Policy);

    /// <summary>
    /// The listening pill's clock, while the session listens: the held hint,
    /// or the hands-free clock, counting down in the policy's last minute
    /// before the limit either way (the Mac's <c>DictationHUDContent.hint</c>),
    /// or, close to a silence stop (hands-free, or the held stuck-key stop),
    /// its warning. The time runs from the moment the microphone came up, as
    /// the limit does (<see cref="EnforceHoldPolicy"/>), and as the Mac counts
    /// both. The time left is rounded up, so the last countdown shown is
    /// "Stops in 0:01": the tick that reaches the limit ends the hold before
    /// this runs. The feed redraws only when the words change, so at most
    /// once a second.
    /// </summary>
    /// <remarks>
    /// Only once the window side has been told the hold listens
    /// (<see cref="_shownState"/>, set by the posted repaint, which puts the
    /// listening pill up itself): the session reaches Listening a tick or
    /// more before that repaint runs, and a pill put up from here first would
    /// be put up twice for one press, which also spends the one level draw a
    /// hold gets when the window cannot draw them. Not after the session has
    /// moved on either, while the repaint that says so is still queued.
    /// </remarks>
    private void ShowListeningClock()
    {
        if (_shownState != DictationState.Listening
            || _session?.State != DictationState.Listening
            || _holdStartedAt is not { } started)
        {
            return;
        }

        var now = _options.Clock();
        _hud?.Listening(_trigger?.IsLatched == true, now - started, SilenceWarningNow(now, started));
    }
}
