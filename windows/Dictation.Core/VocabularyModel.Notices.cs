namespace Dictation.Core;

/// <summary>
/// The Vocabulary section's correction notice: one line above the teach row
/// that says how the last correction watch went, made from the watch's
/// events by <see cref="CorrectionNotices"/> (docs/design-macos.md, Correction notices).
/// </summary>
/// <remarks>
/// <para>
/// <b>Marshalled, then fenced twice.</b> The watch raises its events on the
/// thread pool (Started on the caller's thread), so each one is posted to the
/// UI thread before it is looked at. By then two things may have overtaken it:
/// </para>
/// <list type="bullet">
/// <item>A newer watch. Only the watch named by the last Started may change
/// the line (the id fence), so a late ending or outcome from an older watch
/// cannot replace "Checking…" for the newer one.</item>
/// <item>The user. Add, Remove, Forget all and switching learning off clear
/// the line and bump a generation; an event raised before that, still in the
/// queue, is dropped (the generation fence). Without it a Started posted just
/// before an Add would bring back the line the Add had cleared, and the rest
/// of that watch with it. Dismiss, Add, Remove and Forget all also end the
/// watch itself, as the Mac stops learning from it (<see cref="ClearNoticesAndEndWatch"/>).</item>
/// </list>
/// <para>
/// <b>Only while learning is on.</b> Off clears the line and keeps it clear;
/// a watch cannot start until it is on again.
/// </para>
/// </remarks>
public sealed partial class VocabularyModel
{
    private ICorrectionWatchEvents? _corrections;

    /// <summary>The watch whose events may change the line, or null.</summary>
    private Guid? _observation;

    private string? _correctionNotice;
    private bool _learningOn = true;

    /// <summary>Bumped on the UI thread by every clear the user caused; read
    /// on the raising thread when an event is posted.</summary>
    private int _noticeGeneration;

    /// <summary>
    /// The "Learn from my corrections" switch as the window shows it. Turning
    /// it off clears the notice and keeps it clear until it is on again; the
    /// watch it ends says nothing more.
    /// </summary>
    public void ApplyLearningSwitch(bool on)
    {
        if (on == _learningOn)
        {
            return;
        }

        _learningOn = on;
        if (!on)
        {
            DismissNotices();
            Refresh();
        }
    }

    private void Listen(ICorrectionWatchEvents? corrections)
    {
        _corrections = corrections;
        if (corrections is null)
        {
            return;
        }

        corrections.Started += OnWatchStarted;
        corrections.Observed += OnWatchObserved;
        corrections.Ended += OnWatchEnded;
    }

    private void StopListening()
    {
        if (_corrections is not { } corrections)
        {
            return;
        }

        corrections.Started -= OnWatchStarted;
        corrections.Observed -= OnWatchObserved;
        corrections.Ended -= OnWatchEnded;
        _corrections = null;
    }

    // ─── From the watch's thread ───

    private void OnWatchStarted(object? sender, CorrectionWatchStarted started) =>
        Deliver(() => BeginObservation(started.Watch));

    private void OnWatchObserved(object? sender, CorrectionWatchObserved observed) =>
        Deliver(() => ShowOutcome(observed));

    private void OnWatchEnded(object? sender, CorrectionWatchEnded ended) =>
        Deliver(() => EndObservation(ended));

    /// <summary>Run <paramref name="apply"/> on the UI thread, unless the
    /// user cleared the line after this event was raised, or the section has
    /// closed since: a closed section neither paints (<see cref="Refresh"/>)
    /// nor tells the indicator it remembered a correction.</summary>
    private void Deliver(Action apply)
    {
        var generation = Volatile.Read(ref _noticeGeneration);
        _post(() =>
        {
            if (!_disposed && generation == _noticeGeneration)
            {
                apply();
            }
        });
    }

    // ─── On the UI thread ───

    private void BeginObservation(Guid watch)
    {
        if (!_learningOn)
        {
            return;
        }

        _observation = watch;
        _correctionNotice = CorrectionNotices.Checking;
        Refresh();
    }

    private void ShowOutcome(CorrectionWatchObserved observed)
    {
        if (_observation != observed.Watch)
        {
            // Past the id fence, but not ignored: a watch that began before
            // an Add, Remove or Forget all, or before the next dictation's,
            // still learns on its own thread, and neither receipt may outlive
            // what it learned (VocabularyModel.Saving.cs).
            if (ObservedOutcome(observed))
            {
                Refresh();
            }

            return;
        }

        // The current watch's outcome: the receipt, or else the same rule
        // for the save receipt (VocabularyModel.Receipt.cs).
        _correctionNotice = CorrectionNotices.ForOutcome(observed);
        var remembered = TakeReceipt(observed);
        Refresh();
        if (remembered)
        {
            OnCorrectionRemembered();
        }
    }

    private void EndObservation(CorrectionWatchEnded ended)
    {
        if (_observation != ended.Watch)
        {
            return;
        }

        // Nothing for this watch follows its end (the watch's own promise);
        // let go of its id anyway, so the line is no one's until the next Started.
        _observation = null;
        if (CorrectionNotices.TryForEnding(ended, out var notice))
        {
            _correctionNotice = notice;
            Refresh();
        }
    }

    /// <summary>
    /// The user cleared the notices from the section — Dismiss, Add, Remove,
    /// Forget all — so also end the watch still running, as the Mac does:
    /// its <c>observe</c> learns nothing for a watch whose feedback was
    /// dismissed. Here the watch learns on its own thread, and a fix it
    /// settled afterwards would be saved, with an Undo, behind a section
    /// that no longer listens to it. Switching learning off is not one of
    /// these: the controller ends the watch for that itself.
    /// </summary>
    private void ClearNoticesAndEndWatch()
    {
        DismissNotices();
        _corrections?.End(CorrectionWatchEnd.NoticesCleared);
    }

    /// <summary>Clear the line and the learning receipt, and let no event
    /// already raised bring either back. The save receipt goes too
    /// (<c>VocabularyModel.Saving.cs</c>).</summary>
    private void DismissNotices()
    {
        _observation = null;
        _correctionNotice = null;
        ClearReceipt();
        Interlocked.Increment(ref _noticeGeneration);
        ExpireManagementNotice();
    }
}
