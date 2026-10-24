using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the hold key,
// presses and the field check. How long an admitted hold may listen is
// DictationController.HoldPolicy.cs.
public sealed partial class DictationController
{
    // ─── The hold key ───────────────────────────────────────────────────

    /// <summary>
    /// One poll of the hold key — the 30 ms dispatcher tick. A low-level hook
    /// sees presses but the controller is told about them here, on the thread
    /// that can afford the work. Also where the idle worker is unloaded and a
    /// dead one noticed.
    /// </summary>
    public void Tick()
    {
        _hud?.Tick();
        GiveUpOnASlowCopy();
        SampleLevel();
        WatchMicrophoneAccess();
        // Paused too: the saved recording's week runs while Dictation does.
        ExpireKeptTake();
        if (_trigger is null || _hotkey is null || _gate is null)
        {
            return;
        }

        RouteCancel(_trigger.ConsumeCancelRequest());

        // Before the key is read, so a release the hook missed ends the hold
        // in this same poll (DictationController.HookWatch.cs).
        WatchHook();
        var down = _trigger.IsKeyDown();
        if (!down)
        {
            // The physical release ends a hold the app already ended, or one
            // it never admitted.
            _gate.EndedEarly = false;
            _pressRefused = false;
            Attempts.KeyUp();
        }

        if (down && !_wasDown)
        {
            BeginPress();
        }

        _wasDown = down;
        ResolvePress();
        EnforceHoldPolicy();
        WatchTake();
        _hotkey?.PollForRelease();
        SyncLatch();
        Housekeeping();
        SyncKeyboardFeed();
    }

    /// <summary>
    /// A cancel the hook latched — Escape or a shortcut — read by the poll,
    /// or by a press-time check's answer that runs before the poll does.
    /// </summary>
    private void RouteCancel(TriggerCancel cancel)
    {
        if (cancel == TriggerCancel.None || _gate is null)
        {
            return;
        }

        if (cancel == TriggerCancel.EscapeAsked)
        {
            // A long take asks before Escape throws it away (DictationController.Takes.cs).
            AskToDiscard();
            return;
        }

        if (cancel == TriggerCancel.Chord && (_pressRefused || _gate.EndedEarly))
        {
            // The shortcut was made on a hold this app refused, or on one
            // it already ended itself (the limit, a silence stop), so it says
            // nothing about the dictation still on its way in. The hook
            // cannot tell: the key is still down, and Right Ctrl+C typed
            // while the last sentence decodes would otherwise throw that
            // sentence away under "that was a shortcut". Escape still cancels
            // it, as the user asked.
            _log.LogDebug("A shortcut on a hold that was already over was ignored; the dictation carries on");
        }
        else if (_session?.State is DictationState.Listening or DictationState.Transcribing)
        {
            CancelHold(cancel);
        }
        else if (_fieldCheck is not null)
        {
            // Before the password-field check answered: Escape (armed at
            // the press) or a shortcut. See DictationController.FieldCheck.cs.
            CancelPressBeforeAnswer(cancel);
        }
        else
        {
            // Too late, or for nothing. Once the paste has begun it can no
            // longer be cancelled: the session would abandon the insertion,
            // the words would land anyway, and the window would say a
            // shortcut cancelled them. The paste's own Ctrl+V is such a
            // chord when the key is still held after the app ended the hold.
            _log.LogDebug("A cancel ({Cancel}) arrived while the dictation was {State}; ignored",
                cancel, _session?.State);
        }
    }

    /// <summary>
    /// The hook reports key-downs (a time and a process id, never a key) to
    /// the correction watch while one runs, and to nothing otherwise.
    /// </summary>
    private void SyncKeyboardFeed()
    {
        if (_trigger is not null)
        {
            _trigger.KeyboardInputObserved = _watch?.IsWatching == true ? _keyboardFeed : null;
        }
    }

    /// <summary>The window's Cancel button and the notification-area menu's
    /// Cancel dictation: discard the dictation that is listening or
    /// transcribing, exactly as Escape does. Nothing is inserted.</summary>
    public void Cancel()
    {
        if (_session?.State is DictationState.Listening or DictationState.Transcribing)
        {
            _log.LogInformation("Dictation cancelled from the window or the notification-area menu");
            CancelHold(TriggerCancel.Escape);
        }
    }

    private void CancelHold(TriggerCancel reason)
    {
        // A field check still out can no longer admit this press.
        _fieldCheck = null;
        _press++;
        Attempts.PressEnded(DictationAttemptReceipt.Ending(reason));

        if (_session?.State == DictationState.Transcribing && _worker is { IsTranscribing: true })
        {
            // Withdrawing a decode stops the worker (the host cannot leave a
            // reply on the pipe for the next caller); bring it back quietly.
            // Not once the words are back (the check at the paste, the
            // formatter): the worker is left running, and a death after that
            // is a crash to report.
            _workerResetExpected = true;
        }

        _cancelling = reason;
        try
        {
            _session?.Cancel();
        }
        finally
        {
            _cancelling = TriggerCancel.None;
        }

        EndHandsFree("cancelled");
        _tap?.AbortCapture();
        StopLevel();
        StopSilence();
        StopPreview();

        // The key may still be down (the button was clicked mid-hold): that
        // hold is over, and must not reach the limit or a silence stop later.
        _holdStartedAt = null;
        _holdEnding = null;
    }

    /// <summary>
    /// A press: check the focused field before anything opens. A password
    /// field refuses the press here, so the microphone never records it.
    /// </summary>
    private void BeginPress()
    {
        // A press alone does not end the watch over the last dictation: it may
        // yet be a shortcut (the hold key with another key is how the user
        // types a capital, an accent or an undo into the text being watched),
        // a password field or a brushed key. The watch ends when a hold is
        // released to transcription — see OnSessionStateChanged.
        _press++;
        _pressRefused = false;
        Attempts.Pressed();
        if (RefusePressWhileCopying())
        {
            // As every refusal: left to the hook, this press would still hold
            // its gesture, so the second press of a double-tap would latch
            // with nothing behind it instead of being refused in its turn.
            EndHandsFree("the press was refused");
            return;
        }

        if (_session?.State is DictationState.Transcribing or DictationState.Inserting)
        {
            // The last dictation is still on its way into the field. Admitted,
            // this press would supersede it: the session drops a transcript
            // whose generation has moved on, raises no result, and its words
            // are never typed, with nothing on screen to say so. As on macOS,
            // such a press is refused out loud instead, and
            // records nothing, so the earlier dictation finishes as it would
            // have. Queuing the new hold behind it is the better design, later.
            _pressRefused = true;
            EndHandsFree("the press was refused");
            _log.LogInformation(
                "Press refused: the last dictation is still {State}; the microphone was not opened", _session.State);
            Render(DictationCopy.StillTypingLastDictation(_holdKey));
            _hud?.PressRefused(DictationPressRefusal.StillTyping);
            Attempts.PressEnded(DictationAttemptEnding.StillFinishing);
            return;
        }

        if (_edges?.SecureField is not { } field)
        {
            Admit();
            return;
        }

        var press = _press;
        var check = _options.RunFieldCheck(() => ProbeField(field));
        _fieldCheck = check;
        ArmEscapeForCheck();
        // Answered on the dispatcher as soon as it lands; the next tick would
        // also pick it up, whichever comes first.
        check.ContinueWith(
            _ => _post(() =>
            {
                if (press == _press)
                {
                    ResolvePress();
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private SecureFieldState ProbeField(ISecureFieldProbe field)
    {
        try
        {
            return field.Probe();
        }
        catch (Exception error)
        {
            // Unknown carries on, as at the release: a probe that cannot
            // answer must never be able to switch dictation off.
            _log.LogDebug(error, "The password-field probe threw at the press; treating the field as ordinary");
            return SecureFieldState.Unknown;
        }
    }

    private void ResolvePress()
    {
        if (_fieldCheck is not { IsCompleted: true } check || _gate is null)
        {
            return;
        }

        // A cancel the hook latched before this answer ran is the user's last
        // word on the press, as the Mac's stored attempt.cancelReason is. The
        // answer is posted at a higher priority than the poll that would read
        // it, and Escape has already taken the key up in the hook, so unread
        // it would end the press as a key released before capture.
        RouteCancel(_trigger?.ConsumeCancelRequest() ?? TriggerCancel.None);
        if (_fieldCheck is null)
        {
            return;
        }

        _fieldCheck = null;
        var field = check.IsCompletedSuccessfully ? check.Result : SecureFieldState.Unknown;
        if (field == SecureFieldState.Secure)
        {
            DisarmEscape();
            EndHandsFree("the press was refused");
            _log.LogInformation("Press refused: the focused field is a password field; the microphone was not opened");
            Render(DictationCopy.SecureFieldAtPress);
            _hud?.PressRefused(DictationPressRefusal.PasswordField);
            Attempts.PressEnded(DictationAttemptEnding.PrivacyRefused);
            return;
        }

        if (!_gate.IsKeyDown())
        {
            DisarmEscape();
            _log.LogDebug("The key came up before the field check answered; nothing was recorded");
            // The answer can land before the tick that would see the key up.
            Attempts.KeyUp();
            Attempts.PressEnded(DictationAttemptEnding.ReleasedBeforeCapture);
            return;
        }

        Admit();
    }

    private void Admit()
    {
        string? scene = null;
        try { scene = Learning.LearningScene.ForApplication(_edges?.Target.Resolve().ProcessName); }
        catch (Exception) { /* Unavailable focus keeps the global vocabulary. */ }
        _worker?.SetVocabularyScene(scene);
        if (_worker is { IsLoaded: false, IsStarting: false })
        {
            // Unloaded after idle, or failed to restart: load it while the
            // user speaks rather than after they let go.
            _log.LogInformation("Loading speech recognition for this hold");
            _worker.StartInBackground();
        }

        Attempts.Admitted();
        // Before the microphone opens, so its first buffer is spooled too.
        BeginTake();
        _hotkey?.NotifyHotkeyMessage();
        // After the press opened the microphone (DictationController.HoldPolicy.cs).
        StartHoldClock();
        StartPreview();
    }
}
