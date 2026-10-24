using Dictation.Core.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>The teach row's two fields, as the window holds them.</summary>
public interface IVocabularyTeachRow
{
    /// <summary>The word field's text now.</summary>
    string Written { get; }

    /// <summary>The "Sounds like" field's text now.</summary>
    string SoundsLike { get; }

    /// <summary>Empty both fields.</summary>
    void Clear();
}

/// <summary>
/// The Vocabulary section's save outcomes (docs/design-macos.md, Correction notices,
/// save receipts and Save again): what Add, Remove, Forget all and Save again
/// say once their write is done, and the Save again button.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a clean saved snapshot says so.</b> A receipt ("Saved to your
/// vocabulary on this PC.", "Removed from your vocabulary.", "Your vocabulary
/// is empty.", "Changes saved…") needs the snapshot read on the UI thread
/// after the write to hold nothing unsaved. In the same turn that agrees with
/// the write's own result; after a race it is the newer truth, so the write's
/// result is not consulted. Otherwise a Remove or Forget all says "This
/// change isn't saved yet…" and Save again "…still isn't saved…", beside the
/// Save again button; an Add says nothing of its own. The Remove, Forget all
/// or Save again line is shown only while everything is on disk and a warning
/// only while something is not, so a later change on another thread can
/// never leave that line beside the warning. The Add line under the teach row
/// is the Mac's: it stays until the next Add, Remove or Forget all, even when
/// a later correction leaves something unsaved.
/// </para>
/// <para>
/// <b>Off the UI thread, then fenced.</b> The learning call runs on the work
/// runner and the response is posted back. By then a newer operation may have
/// started; each one bumps a generation, and a response whose generation is
/// no longer current repaints the list but says nothing. Newer operations
/// are Add, Remove, Forget all, Save again, Undo learning, Dismiss, switching
/// learning off, a new learning receipt (the Mac's receipt clears this line
/// too), and a correction watch's outcome that changed the file, was
/// rejected or edited the text (the one observation that clears a receipt).
/// That outcome counts from any watch raised after the click, not only the
/// one the notice follows: on Windows a watch that began before an Add,
/// Remove or Forget all still learns on its own thread, where the Mac's stops
/// learning. A watch's start or end, and a dictation with no edit, are not:
/// they change no vocabulary, and docs/design-macos.md keeps the receipts through them.
/// The Mac's single notice generation also drops a pending response at a
/// watch's start or end, and its Undo learning drops none.
/// </para>
/// <para>
/// <b>One paint per answer.</b> While a change of the section's own is
/// unanswered, the list's Changed event does not repaint: the answer does,
/// from a snapshot read after it, so a refused Remove shows its own warning
/// once instead of the plain unsaved notice first.
/// </para>
/// <para>
/// <b>A slow Add keeps new typing.</b> The window's Add hands over the teach
/// row itself (<see cref="Teach(IVocabularyTeachRow)"/>), and the row is
/// emptied only when it still holds what was added.
/// </para>
/// </remarks>
public sealed partial class VocabularyModel
{
    /// <summary>How long closing the section waits for a write still in
    /// flight, so a click just before Quit still lands
    /// (<see cref="LetPendingWorkFinish"/>).</summary>
    private static readonly TimeSpan PendingWorkAtClose = TimeSpan.FromSeconds(2);

    private readonly Func<Action, Task> _work;
    private readonly ILogger _log;

    /// <summary>The last work handed to the runner; the runner keeps order,
    /// so this finishes last.</summary>
    private Task _lastWork = Task.CompletedTask;

    /// <summary>The latest Remove, Forget all or Save again sentence.</summary>
    private string? _managementNotice;

    /// <summary><see cref="_managementNotice"/> says something is not saved.</summary>
    private bool _managementWarns;

    private bool _savingAgain;

    /// <summary>Bumped by every newer operation; UI thread only.</summary>
    private int _responseGeneration;

    /// <summary>Changes handed to the runner and not answered yet; UI thread only.</summary>
    private int _unanswered;

    /// <summary>
    /// The teach row's Add (its button, or Enter in either field): add what
    /// the row holds and, once it is added, empty the row — unless the user
    /// has typed into it since, so a slow save never erases new text.
    /// </summary>
    public void Teach(IVocabularyTeachRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var written = row.Written;
        var soundsLike = row.SoundsLike;
        Teach(written, soundsLike, added =>
        {
            if (added && !_disposed && row.Written == written && row.SoundsLike == soundsLike)
            {
                row.Clear();
            }
        });
    }

    /// <summary>
    /// Save again: try once more to write what is not on disk. It reads
    /// "Saving…" and is disabled until the write answers, and a second click
    /// meanwhile does nothing.
    /// </summary>
    public void SaveAgain()
    {
        if (_disposed || _savingAgain)
        {
            return;
        }

        var generation = ++_responseGeneration;
        _savingAgain = true;
        Refresh();
        Run(
            _learning.Flush,
            write =>
            {
                _savingAgain = false;
                _log.LogInformation("Save again: file {Write}", write);
                Respond(generation, unsaved =>
                {
                    _managementNotice = unsaved ? DictationCopy.SaveAgainFailed : DictationCopy.SaveAgainSucceeded;
                    _managementWarns = unsaved;
                });
            },
            () => _savingAgain = false);
    }

    // ─── Running a change ───

    /// <summary>Run <paramref name="work"/> on the runner, then
    /// <paramref name="respond"/> on the UI thread: at once when the runner
    /// finished before returning, else through <c>post</c>.</summary>
    /// <param name="failed">On the UI thread when the work threw, before the repaint.</param>
    private void Run<T>(Func<T> work, Action<T> respond, Action? failed = null)
    {
        var result = default(T)!;
        Task task;
        _unanswered++;
        try
        {
            task = _work(() => result = work());
        }
        catch (Exception error)
        {
            task = Task.FromException(error);
        }

        _lastWork = task;
        if (task.IsCompleted)
        {
            Finish(task, () => respond(result), failed);
            return;
        }

        task.ContinueWith(
            done => _post(() => Finish(done, () => respond(result), failed)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Finish(Task done, Action respond, Action? failed)
    {
        _unanswered--;
        if (done.IsCompletedSuccessfully)
        {
            respond();
            return;
        }

        // The type only: an exception message can carry a path or a word.
        _log.LogWarning(
            "A vocabulary change failed ({Error})", done.Exception?.GetBaseException().GetType().Name ?? "cancelled");
        failed?.Invoke();
        Refresh();
    }

    /// <summary>Paint the section after a change. <paramref name="say"/> sets
    /// the change's own line first, against what is on disk now, unless a
    /// newer operation has started since the change began.</summary>
    private void Respond(int generation, Action<bool> say)
    {
        // Not what keeps a closed section unpainted (Paint checks that
        // itself): it only spares a snapshot read under the learning lock
        // once the section has closed.
        if (_disposed)
        {
            return;
        }

        var snapshot = _learning.Snapshot();
        if (generation == _responseGeneration)
        {
            say(snapshot.Unsaved);
        }
        else
        {
            _log.LogDebug("A vocabulary response was overtaken by a newer operation and is not shown");
        }

        Paint(snapshot);
    }

    /// <summary>
    /// The list changed (posted from the changing thread). While one of the
    /// section's own changes is still to be answered, its answer repaints
    /// from a snapshot read after this change anyway; painting now would only
    /// show the plain unsaved notice a moment before a refused Remove's own
    /// sentence, and Narrator would read both.
    /// </summary>
    private void RefreshAfterChange()
    {
        if (_unanswered == 0)
        {
            Refresh();
        }
    }

    /// <summary>A Remove's or Forget all's line: <paramref name="success"/>
    /// only when nothing is unsaved.</summary>
    private void ShowManagementReceipt(bool unsaved, string success)
    {
        _managementNotice = unsaved ? DictationCopy.VocabularyChangeNotSaved : success;
        _managementWarns = unsaved;
    }

    // ─── What clears or overtakes a response ───

    /// <summary>Clear the Remove, Forget all or Save again line, and let no
    /// response already in flight set one: the user cleared the section, or a
    /// correction changed the vocabulary.</summary>
    private void ExpireManagementNotice()
    {
        _managementNotice = null;
        _managementWarns = false;
        _responseGeneration++;
    }

    /// <summary>A correction watch's outcome reached the section, from any
    /// watch raised after the last clear: another watch's
    /// (<c>ShowOutcome</c>), or the current one's when it gave no learning
    /// receipt (<c>TakeReceipt</c>, which clears the line itself when it
    /// does). One that changed the file, was rejected or edited the text
    /// clears the line and the learning receipt together, as the Mac's
    /// <c>dismissLearningNotice</c> on <c>save != .unchanged</c> or
    /// <c>rejections &gt; 0 || dictated != edited</c>; a dictation with no
    /// edit leaves both.</summary>
    /// <returns>Whether it cleared them.</returns>
    private bool ObservedOutcome(CorrectionWatchObserved observed)
    {
        var outcome = observed.Outcome;
        if (outcome.Write != LearningWrite.Unchanged || outcome.Rejections > 0 || observed.Edited)
        {
            ClearReceipt();
            ExpireManagementNotice();
            return true;
        }

        return false;
    }

    // ─── What the view shows ───

    /// <summary>The one warning beside Save again, while something is unsaved.</summary>
    private string? SaveWarning(bool unsaved) =>
        !unsaved ? null : (_managementWarns ? _managementNotice : null) ?? DictationCopy.VocabularyNotSaved;

    /// <summary>The receipt, while everything is saved.</summary>
    private string? SaveReceipt(bool unsaved) => !unsaved && !_managementWarns ? _managementNotice : null;

    // ─── The runner ───

    /// <summary>The runner when none is given: on this thread, before returning.</summary>
    private static Task RunHere(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Closing: wait a little for a write still in flight, so a change
    /// clicked just before Quit is not lost with the process; then, when
    /// something is still not on disk, try the save once more.
    /// </summary>
    /// <remarks>
    /// The window lets the controller go first (<c>MainWindow.LetGo</c>), so
    /// the quit's last save can run before a change still queued here; a
    /// change the disk refused then would get no last try of its own. The
    /// controller is gone by now, and the keyboard hook with it, so this
    /// thread may wait on the disk. A write still running after the wait is
    /// left alone: a save now would only queue behind it on the learning lock.
    /// </remarks>
    private void LetPendingWorkFinish()
    {
        try
        {
            if (!_lastWork.Wait(PendingWorkAtClose))
            {
                _log.LogWarning("A vocabulary change was still being written when the section closed");
                return;
            }
        }
        catch (AggregateException)
        {
            // Already logged when it was answered, or it never will be: the
            // section is closing either way, and the save below still runs.
        }

        try
        {
            if (_learning.Snapshot().Unsaved)
            {
                _learning.Flush(); // logs its own outcome, with no words
            }
        }
        catch (Exception error)
        {
            // The type only, as the controller's last save: the window still has to close.
            _log.LogWarning("The vocabulary's save at close failed ({Error})", error.GetType().Name);
        }
    }
}
