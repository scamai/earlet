using Dictation.Core.Learning;

namespace Dictation.Core;

/// <summary>
/// The learning receipt, as the Mac's "Learning from you" section gives it
/// (docs/design-macos.md, Learning feedback, receipts and Undo): one line when a
/// correction saved a new word or rewrite, never naming it, with Undo
/// learning and Dismiss.
/// </summary>
/// <remarks>
/// <para>
/// <b>Made from the watch's outcome.</b> The receipt comes from the same
/// posted, fenced Observed event as the correction notice
/// (<c>VocabularyModel.Notices.cs</c>), and only when that outcome carries an
/// undo token and the list is all on disk, so a change the disk refused, or a
/// pair still waiting for a second dictation, never claims to be remembered.
/// Putting it up takes down the Remove, Forget all or Save again line, as the
/// Mac's does, and overtakes any answer still in flight, an Undo's included
/// (<c>VocabularyModel.Saving.cs</c>).
/// </para>
/// <para>
/// <b>What replaces it.</b> A later outcome that changed or saved something,
/// or an edit that taught nothing, takes it down, with the save line, from
/// any watch, as the Mac's <c>dismissLearningNotice</c> takes both: on
/// Windows a watch already past its check still learns on its own thread
/// after the next one starts, where the Mac's stops, and its refused write
/// must not leave "Remembered…" beside "…isn't saved yet". A dictation the
/// user left as it was does not. Add, Remove, Forget all, Dismiss and switching learning
/// off clear it with the notice (<see cref="DismissNotices"/>), and the first
/// four also end the watch still running, so it learns nothing the receipt
/// would not report (<see cref="ClearNoticesAndEndWatch"/>). The learning
/// facade expires the token on any later change by itself, so an Undo that
/// meets a changed vocabulary says so instead of overwriting it.
/// </para>
/// <para>
/// <b>Through the work runner, like Add and Remove.</b> The undo hands its
/// write of <c>learning.json</c> to the runner and answers through
/// <c>post</c>, fenced like every other change the section makes
/// (<c>VocabularyModel.Saving.cs</c>): it is a newer operation for any answer
/// still in flight, and its own line is dropped when Dismiss, Add, Remove,
/// Forget all, switching learning off or a new receipt comes first. Undo goes
/// at the click, so a second click has nothing to undo; the line stays until
/// the answer, with no "Undoing…" state of its own.
/// </para>
/// </remarks>
public sealed partial class VocabularyModel
{
    /// <summary>The line the receipt section shows, or null when it is hidden.</summary>
    private string? _receipt;

    /// <summary>What Undo learning hands back, while the receipt offers it.</summary>
    private Guid? _undoToken;

    /// <summary>
    /// The section has just shown a receipt for a newly saved correction:
    /// the moment the Mac's indicator says "Remembered your correction".
    /// Raised on the UI thread, after the section is painted.
    /// </summary>
    public event EventHandler? CorrectionRemembered;

    /// <summary>Whether Undo learning is offered now.</summary>
    public bool CanUndoLearning => _undoToken is not null;

    /// <summary>
    /// The receipt's Undo learning: put the vocabulary back as it was before
    /// the remembered correction. Says it is undone only once that is saved;
    /// a save the disk refused leaves the not-saved line to say so; an undo
    /// the vocabulary has moved past says so and changes nothing.
    /// </summary>
    public void UndoLastLearning()
    {
        if (_disposed || _undoToken is not { } token)
        {
            return;
        }

        _undoToken = null;
        var generation = ++_responseGeneration;
        Refresh();
        Run(
            () => _learning.UndoLearning(token),
            write => Respond(generation, _ => _receipt = write switch
            {
                LearningWrite.Saved or LearningWrite.Unchanged => DictationCopy.LearningUndone,
                LearningWrite.NotSaved => null,
                _ => DictationCopy.LearningUndoExpired,
            }));
    }

    /// <summary>The receipt's Dismiss: the receipt, its Undo and the
    /// correction notice go, nothing already queued brings them back, and
    /// the watch still running learns nothing more.</summary>
    public void DismissLearningReceipt()
    {
        ClearNoticesAndEndWatch();
        Refresh();
    }

    /// <summary>
    /// What the current watch's outcome does to the receipt and to the
    /// Remove, Forget all or Save again line, in the order of the Mac's
    /// <c>DictationVocabularyModel.observe</c>: on the UI thread, already
    /// fenced to the current watch.
    /// </summary>
    /// <returns>True when it put up a new receipt.</returns>
    private bool TakeReceipt(CorrectionWatchObserved observed)
    {
        var outcome = observed.Outcome;
        if (outcome.UndoToken is { } token && !_learning.Snapshot().Unsaved)
        {
            // The Mac's managementNotice = nil: the receipt is the news now,
            // and no Remove, Forget all, Save again or Undo answer still in
            // flight may put a line of its own back beside it.
            ExpireManagementNotice();
            _undoToken = token;
            _receipt = DictationCopy.LearningRemembered;
            return true;
        }

        // Anything learned, saved or refused since, or an edit that taught
        // nothing: neither the receipt nor the save line is the news any
        // more. A dictation left as it was is not a vocabulary change.
        ObservedOutcome(observed);
        return false;
    }

    private void ClearReceipt()
    {
        _receipt = null;
        _undoToken = null;
    }

    private void OnCorrectionRemembered() => CorrectionRemembered?.Invoke(this, EventArgs.Empty);
}
