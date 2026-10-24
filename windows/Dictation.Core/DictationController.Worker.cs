using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the speech worker and closing.
public sealed partial class DictationController
{
    // ─── The worker ─────────────────────────────────────────────────────

    /// <summary>
    /// Between dictations: notice a worker that died, and give back an idle
    /// model.
    /// </summary>
    private void Housekeeping()
    {
        if (_worker is not { } worker || _session?.State != DictationState.Armed || _restarting || _recovering)
        {
            // A recovery's own decode restarts a dead worker, and an idle
            // unload must not stop it (DictationController.Takes.cs).
            return;
        }

        if (worker.IsLoaded && !worker.IsStarting && !worker.IsReady)
        {
            if (_workerResetExpected)
            {
                // The user's own cancel stopped it: not a failure, and not
                // counted against the restart budget.
                _workerResetExpected = false;
                RestartQuietly("after a cancelled transcription");
                return;
            }

            _log.LogWarning("The speech worker stopped between dictations; restarting it");
            RestartWorker(new DictationStatus(DictationCopy.WorkerStoppedWhileIdle));
            return;
        }

        if (worker.IsReady && !worker.IsStarting
            && _options.Clock() - _lastWorkerUse >= _options.WorkerIdleUnload)
        {
            _log.LogInformation(
                "Speech recognition unloaded after {Seconds:0} s without a dictation; the next hold loads it again",
                _options.WorkerIdleUnload.TotalSeconds);
            worker.Unload();
        }
    }

    /// <summary>
    /// One restart the user caused, in the background and without a word. A
    /// failure leaves the worker unloaded, and the next press loads it again
    /// (where a second failure is reported, and counted).
    /// </summary>
    private void RestartQuietly(string why)
    {
        if (_worker is not { } slot)
        {
            return;
        }

        _log.LogInformation("Restarting speech recognition {Why}", why);
        _lastWorkerUse = _options.Clock();
        slot.RestartAsync().ContinueWith(
            t => _log.LogError(t.Exception, "Speech recognition did not restart {Why}", why),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Start a replacement worker in the background, a bounded number of times,
    /// and say so when it is back — unless something newer took the status line.
    /// </summary>
    /// <param name="announce">What the window says while it restarts.</param>
    private void RestartWorker(DictationStatus announce)
    {
        if (_worker is not { } slot || _restarting)
        {
            return;
        }

        if (RestartBudgetSpent)
        {
            GiveUpRestarting();
            return;
        }

        _restarting = true;
        _restartAnnouncement = announce;
        Render(announce);
        AttemptRestart(slot, announce);
    }

    private void AttemptRestart(DictationWorkerSlot slot, DictationStatus announced)
    {
        _restartAttempts++;
        _log.LogInformation(
            "Restarting speech recognition (attempt {Attempt} of {Limit})",
            _restartAttempts, _options.WorkerRestartAttempts);

        // StartAsync, not RestartAsync: the worker this replaces is already
        // dead or gone, so there is nothing to stop. A press during the wait
        // between two attempts loads a worker for its own dictation, and
        // StartAsync keeps that one if it is ready and joins its start if it
        // is still loading; RestartAsync would unload it under the dictation.
        slot.StartAsync().ContinueWith(
            t => _post(() => AfterRestart(slot, announced, t)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void AfterRestart(DictationWorkerSlot slot, DictationStatus announced, Task<bool> attempt)
    {
        if (_disposed || !ReferenceEquals(slot, _worker))
        {
            // Paused (or quit) while it ran: the slot is already disposed.
            return;
        }

        if (attempt.IsFaulted)
        {
            _log.LogError(attempt.Exception, "Restarting speech recognition failed");
        }

        if (attempt.IsCompletedSuccessfully && attempt.Result)
        {
            _restarting = false;
            _lastWorkerUse = _options.Clock();
            _log.LogInformation("Speech recognition restarted");
            if (ReferenceEquals(_status, _restartAnnouncement))
            {
                Render(ReadyUnlessBlocked(DictationCopy.WorkerRestarted(_holdKey)));
            }

            return;
        }

        if (RestartBudgetSpent)
        {
            _restarting = false;
            GiveUpRestarting();
            return;
        }

        _options.RestartWait(_options.WorkerRestartDelay, _lifetime.Token).ContinueWith(
            _ => _post(() =>
            {
                if (!_disposed && ReferenceEquals(slot, _worker) && _restarting)
                {
                    AttemptRestart(slot, announced);
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void GiveUpRestarting()
    {
        _log.LogError(
            "Speech recognition could not be restarted after {Attempts} attempts; waiting for the user",
            _restartAttempts);

        // Unloaded, so the housekeeping tick stops seeing a dead worker to
        // restart. The next press still tries once: the user asked for it.
        _worker?.Unload();
        Render(DictationCopy.WorkerRestartFailed);

        // On the indicator too. The window is usually hidden, and the last
        // pill may have said "restarting it" — a hopeful line to end on. Shown
        // even with no hold under way: the next hold would record and fail.
        // Not while a hold listens: the hold outranks an ending, as on the
        // Mac, the microphone would hear Narrator read it, and the listening
        // pill would replace it within a tick. The release tries once more to
        // load the recogniser for its own words; if that fails too, its ending
        // is this same give-up, at rest.
        if (_session?.State != DictationState.Listening)
        {
            _hud?.RecognizerCouldNotRestart();
        }
    }

    private bool RestartBudgetSpent => _restartAttempts >= _options.WorkerRestartAttempts;

    /// <summary>Whether the recogniser answered this dictation — the evidence
    /// that a restarted worker is healthy again.</summary>
    private static bool RecogniserAnswered(DictationSessionResult result) =>
        result.Outcome is DictationOutcome.Inserted or DictationOutcome.ClipboardRestoreFailed
            or DictationOutcome.InsertionRefused or DictationOutcome.NoFocusedWindow
        || (result.Outcome == DictationOutcome.NoSpeech && result.DecodeDuration is not null)
        // A password field found at the paste: the words came back first.
        || result is { Outcome: DictationOutcome.SecureField, SecureFieldAtPaste: true };

    // ─── Closing ────────────────────────────────────────────────────────

    /// <summary>
    /// The window is closing. Closing the window only hides it, dictation on
    /// or off, as closing the Mac's window never quits: the notification-area
    /// icon keeps the app reachable, and a paused app keeps what it holds
    /// (the recent result for Copy last text, the learning receipt with its
    /// Undo). The first time that happens the answer carries a notice saying
    /// so. Only Quit, in the notification-area menu, exits; that can be
    /// deferred while an insertion has the user's clipboard.
    /// </summary>
    /// <param name="quit">The notification-area menu's Quit, a deferred quit
    /// coming back, or Windows signing out; false for the window's own close
    /// button.</param>
    public DictationCloseDecision RequestClose(bool quit)
    {
        // Hidden or quitting, the window took the focus from the field.
        _watch?.End(CorrectionWatchEnd.Closed);
        if (!quit && !_disposed)
        {
            var on = _session is not null || _enabling;
            if (_enabling)
            {
                // Hidden before the enable finished, after a notice that says
                // dictation is on: if it fails, the window comes back to say so.
                _revealOnFailure = true;
            }

            string? notice = null;
            if (!_settings.TrayNoticeShown)
            {
                _settings = _settings with { TrayNoticeShown = true };
                _settingsStore.Save(_settings);
                notice = on ? DictationCopy.StillRunningInTray : DictationCopy.StillInTrayWhileOff;
            }

            _log.LogInformation(
                on
                    ? "Window hidden; dictation keeps running from the notification area"
                    : "Window hidden; Dictation stays in the notification area, off");
            return new DictationCloseDecision(DictationCloseAction.Hide, notice);
        }

        return TryQuit()
            ? new DictationCloseDecision(DictationCloseAction.Close)
            : new DictationCloseDecision(DictationCloseAction.Wait);
    }

    /// <summary>
    /// False means "not yet": an insertion has the transcript on the clipboard
    /// and has not put the user's clipboard back, and the controller will ask
    /// the view to quit again once it has.
    /// </summary>
    /// <remarks>
    /// The restore runs on a thread-pool continuation after the paste settles.
    /// Exiting ends the process, and that continuation with it, so a quit in
    /// that half second used to leave the transcript on the clipboard in place
    /// of whatever the user had copied. Only the insertion is waited for: a
    /// quit while listening or transcribing cancels the dictation before
    /// anything touches the clipboard.
    /// </remarks>
    private bool TryQuit()
    {
        if (_disposed || _closeForced || _session?.State != DictationState.Inserting)
        {
            return true;
        }

        if (!_closePending)
        {
            _closePending = true;
            _log.LogInformation("Quit deferred until the insertion restores the clipboard");
            _ = ForceQuitAfterAsync(_options.CloseWaitLimit);
        }

        Render(DictationCopy.FinishingBeforeClose);
        return false;
    }

    private async Task ForceQuitAfterAsync(TimeSpan limit)
    {
        try
        {
            await Task.Delay(limit, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _post(() =>
        {
            if (_disposed || !_closePending)
            {
                return;
            }

            _log.LogWarning(
                "The insertion did not finish within {Limit}; quitting anyway", limit);
            _closeForced = true;
            _view.Quit();
        });
    }

    /// <summary>The app is exiting: stop everything for good, and try once
    /// more to save learned vocabulary the disk refused. Whether dictation was
    /// on is left as it was, so the next launch comes back the same way.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        // Quitting discards the recent result, as on the Mac: it never outlives the process.
        _recent.Clear();
        _watch?.End(CorrectionWatchEnd.Closed);
        Stop();
        _lifetime.Dispose();
        _hud?.Dispose();
        FlushLearning();
    }

    /// <summary>
    /// The last save at quit (LEARNING.md §5): a change the disk refused, or
    /// made while learning.json could not be read, is tried once more before
    /// the process goes. Nothing is read or written when everything is saved.
    /// </summary>
    private void FlushLearning()
    {
        try
        {
            _learning?.Flush();
        }
        catch (Exception error)
        {
            // The type only, never the message. Whatever went wrong, the app
            // still has to close: this runs on the way out of the window.
            _log.LogWarning("The last save of learned vocabulary failed ({Error})", error.GetType().Name);
        }
    }
}
