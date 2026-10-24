using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the hands-free
// latch. The gesture itself — the pair, the one-press stop, the swallowed stop
// keys, the window a short press waits out — is decided in the hook
// (HoldKeyState, HoldKeyTriggerMode.HoldOrDoubleTapHandsFree); to the
// controller a latched hold is an ordinary hold whose key reads as down. What
// is here is everything that has to agree with it: the latch ended on every
// path that ends a hold without a key edge and the window's status line. The
// indicator's clock, held or hands-free, is DictationController.Hints.cs.
public sealed partial class DictationController
{
    /// <summary>Whether the window and the indicator were last told that
    /// the listening hold is latched.</summary>
    private bool _latchShown;

    /// <summary>
    /// End the hook's latch, or a short press's parked hold, because this
    /// hold ended some other way: the recording limit, a silence stop, Cancel
    /// or a refused press. The hook and the controller share one latch flag; leaving it
    /// set would keep the microphone open and Space and Enter swallowed.
    /// </summary>
    private void EndHandsFree(string why)
    {
        if (_trigger is null)
        {
            return;
        }

        var wasLatched = _trigger.IsLatched;
        _trigger.EndLatch();
        if (wasLatched)
        {
            _log.LogInformation("Hands-free dictation ended: {Why}", why);
        }
    }

    /// <summary>
    /// Once per tick, after the release poll: keep the latch, the window and
    /// the indicator in step.
    /// </summary>
    /// <remarks>
    /// A latch is legitimate only while its hold is listening, or while the
    /// press that made it waits for the password-field check. Any other
    /// latch has no dictation behind it, whatever left it there, and is
    /// ended here, within one tick: a latch nobody can see would keep Space
    /// and Enter swallowed system-wide. The explicit ends (the limit, a
    /// silence stop, Cancel, refusals) are not left to this; it is the net
    /// under them.
    /// </remarks>
    private void SyncLatch()
    {
        if (_trigger is null)
        {
            return;
        }

        LogLatchEndedByKey(_trigger.ConsumeLatchEnd());

        var latched = _trigger.IsLatched;
        var listening = _session?.State == DictationState.Listening;
        if (latched && !listening && _fieldCheck is null)
        {
            _log.LogInformation("A hands-free latch had no dictation behind it, so it was ended");
            _trigger.EndLatch();
            latched = false;
        }

        var shown = latched && listening;
        if (shown && !_latchShown)
        {
            _log.LogInformation(
                "Dictation latched hands-free; {Key}, Space or Enter stops it", _holdKey.KeyName);
            Render(DictationCopy.Latched(_holdKey));
        }

        _latchShown = shown;
        ShowListeningClock();
    }

    /// <summary>
    /// Say which key ended a latch: the hook ends it on the key, before the
    /// controller looks, so <see cref="EndHandsFree"/> never sees these. The
    /// hold key by name, a stop key as one (never which), and Escape; no
    /// other key and no words.
    /// </summary>
    private void LogLatchEndedByKey(LatchEnd endedBy)
    {
        var why = endedBy switch
        {
            LatchEnd.HoldKey => $"{_holdKey.KeyName} was pressed",
            LatchEnd.StopKey => "a stop key (Space or Enter) was pressed",
            LatchEnd.Escape => "Escape was pressed",
            _ => null,
        };

        if (why is not null)
        {
            _log.LogInformation("Hands-free dictation ended: {Why}", why);
        }
    }

    /// <summary>What the window says while a session is in
    /// <paramref name="state"/>: the hands-free sentence while a listening
    /// hold is latched, otherwise <see cref="DictationCopy.ForState"/>.</summary>
    private string StatusFor(DictationState state) =>
        state == DictationState.Listening && _trigger?.IsLatched == true
            ? DictationCopy.Latched(_holdKey)
            : DictationCopy.ForState(state, _holdKey);
}
