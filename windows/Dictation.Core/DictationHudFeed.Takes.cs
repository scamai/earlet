using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

// Part of DictationHudFeed (see DictationHudFeed.cs): the pills of "long
// takes are never lost" — a take a fault ended, a take kept for Recover last
// dictation, a recovery that failed, and the question Escape asks before it
// throws a long take away. Every one a caution: none is an ordinary "Typed".
public sealed partial class DictationHudFeed
{
    /// <summary>Escape's question is up: the listening pill's clock does not
    /// replace it until the controller ends it.</summary>
    private bool _askingToDiscard;

    /// <summary>
    /// The pill for a take that a fault ended or that is kept, or null for
    /// every other ending, which <see cref="For"/> then words as before.
    /// </summary>
    /// <remarks>
    /// A kept take outranks the recogniser's own lines: the recording is what
    /// the user needs to hear about, and the restart is background work the
    /// window reports. A fault's ending replaces the ordinary "Typed" and
    /// "Nothing heard" only; a refused paste, a password field and the rest
    /// keep their own lines, which say what the user must do next.
    /// </remarks>
    private static DictationHudModel? ForTake(DictationSessionResult result, DictationHudContext context)
    {
        const HudTone caution = HudTone.Caution;
        return result.Outcome switch
        {
            DictationOutcome.RecognizerUnavailable or DictationOutcome.RecognizerFailed when context.TakeSaved =>
                new(DictationCopy.HudTakeSaved, caution, DictationHudModel.FailureDwell),
            DictationOutcome.Cancelled when context.CancelledByShortcut && context.TakeSaved =>
                new(DictationCopy.HudCancelledByShortcutTakeSaved, caution, DictationHudModel.FailureDwell),
            DictationOutcome.Inserted or DictationOutcome.ClipboardRestoreFailed
                when context.TakeStop != DictationTakeStop.None =>
                new(DictationCopy.HudTakeStoppedInserted(context.TakeStop, context.TakeStoppedAt), caution,
                    DictationHudModel.FailureDwell),
            DictationOutcome.NoSpeech or DictationOutcome.NoAudio when context.TakeStop != DictationTakeStop.None =>
                new(DictationCopy.HudTakeStoppedNothingHeard(context.TakeStop, context.TakeStoppedAt), caution,
                    DictationHudModel.FailureDwell),
            _ => null,
        };
    }

    /// <summary>
    /// The first Escape on a long take: "Discard 3:42 of speech? — press Esc
    /// again to discard", until <see cref="EndAskToDiscard"/>. Shown, never spoken, while
    /// the hold listens (the microphone is open); spoken once it transcribes.
    /// </summary>
    /// <param name="length">How much speech the take holds.</param>
    /// <param name="listening">The hold is still listening.</param>
    public void AskToDiscard(TimeSpan length, bool listening)
    {
        _askingToDiscard = true;
        // Up as long as the question stands (DictationTakeOptions.EscapeAskWindow,
        // the failure tier): the controller ends it at the same moment.
        Show(
            new DictationHudModel(DictationCopy.HudAskToDiscard(length), HudTone.Caution, DictationHudModel.FailureDwell),
            announce: !listening);
    }

    /// <summary>The question's time is up, or the take is over: the pill goes
    /// back to what the hold is doing. Listening, the controller's next clock
    /// redraws it; transcribing, it is drawn here.</summary>
    /// <param name="state">The session's state now.</param>
    public void EndAskToDiscard(DictationState state)
    {
        if (!_askingToDiscard)
        {
            return;
        }

        _askingToDiscard = false;
        if (state == DictationState.Transcribing)
        {
            State(state);
        }
    }

    /// <summary>Recover last dictation could not transcribe the saved
    /// recording; it is still saved.</summary>
    public void RecoveryFailed() =>
        Show(new DictationHudModel(DictationCopy.HudRecoveryFailed, HudTone.Caution, DictationHudModel.FailureDwell));
}
