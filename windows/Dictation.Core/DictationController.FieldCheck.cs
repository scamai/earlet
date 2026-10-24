using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the two edges of
// the password-field check the Mac has and Windows lacked.
//
// Escape is armed at the press, so it cancels a press whose check has not
// answered yet; before, the hook armed it only once the dictation listened,
// so Escape after a quick tap or a double-tap went unheard and the press went
// ahead when the check answered.
//
// The session asks again just before the paste (DictationSession's
// pasteFieldCheck, run by DictationControllerOptions.RunPasteFieldCheck), so
// words decoded for a field that became a password field are not typed, not
// put on the clipboard and not kept for Copy last text: the Mac's refusal at
// the paste.
public sealed partial class DictationController
{
    // ─── Escape before the press-time check answers ─────────────────────

    /// <summary>
    /// BeginPress's hook, as the press-time check goes out: the hook's Escape
    /// arm (<see cref="IDictationTrigger.DictationInFlight"/>) is live from the
    /// press, as the Mac's key watch begins at the press. The press is the
    /// user's from here, so Escape is a request about it. With the key still
    /// held the hook hears Escape anyway, as a key pressed with the hold key;
    /// the arm is what covers the key already up while the hold stays open (a
    /// quick tap waiting out the pair window, a double-tap that latched).
    /// </summary>
    private void ArmEscapeForCheck()
    {
        if (_trigger is not null)
        {
            _trigger.DictationInFlight = true;
        }
    }

    /// <summary>
    /// The press ended without being admitted — a password field, the key
    /// already up when the check answered, or a cancel — so Escape goes back
    /// to inert unless a dictation is still listening or transcribing. Left
    /// armed, every Escape the user pressed at rest would latch a cancel in
    /// the hook.
    /// </summary>
    private void DisarmEscape()
    {
        if (_trigger is not null)
        {
            _trigger.DictationInFlight = _session?.State is DictationState.Listening or DictationState.Transcribing;
        }
    }

    /// <summary>
    /// Escape or a shortcut while the press's password-field check is out,
    /// read from the hook's cancel latch by whichever runs first: the poll,
    /// or the check's answer (ResolvePress reads the latch before it acts).
    /// The press is withdrawn there: nothing is open yet, and an answer that
    /// lands later finds the press counter moved on and admits nothing. The
    /// Mac stores the reason (attempt.cancelReason) and honours it once its
    /// capture is up, because its warm-up cannot be stopped; here the hook's
    /// latch is that store, and the answer honours it. Escape ends as a
    /// cancel does while listening ("Cancelled", in the window and on the
    /// indicator, the Mac's discard); a shortcut says nothing, as before: the
    /// user meant the shortcut, and no dictation had started.
    /// </summary>
    private void CancelPressBeforeAnswer(TriggerCancel cancel)
    {
        CancelHold(cancel);
        DisarmEscape();
        if (cancel != TriggerCancel.Escape)
        {
            _log.LogDebug("A shortcut before the password-field check answered withdrew the press; nothing was recorded");
            return;
        }

        _log.LogInformation("Escape before the password-field check answered cancelled the press; the microphone was not opened");
        var cancelled = new DictationSessionResult(DictationOutcome.Cancelled, null, null);
        Render(DictationCopy.ForResult(cancelled, _holdKey));
        _hud?.Result(cancelled, new DictationHudContext());
    }

    // ─── A password field at the paste ──────────────────────────────────

    /// <summary>
    /// Completed's ending for a password field the session found just before
    /// the paste. Nothing was typed, and the clipboard fallback does not run:
    /// keeping the words is the leak. RememberResult has already cleared the
    /// recent result, so the words are kept nowhere, and the receipt's
    /// recovery line says so (<c>clipboard=false available_in_app=false</c>),
    /// as the Mac's does after its secure refusal.
    /// </summary>
    private DictationStatus RefusedAtPaste(DictationSessionResult result, DictationAttemptLog? attempt)
    {
        LogRecovery(attempt, clipboard: false);
        return DictationCopy.ForResult(result, _holdKey);
    }
}
