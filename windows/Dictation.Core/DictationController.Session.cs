using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): arming, session events and rendering.
public sealed partial class DictationController
{
    // ─── Arm and stop ───────────────────────────────────────────────────

    private bool Arm()
    {
        // Chords cancel: Right Ctrl+C is a copy, not a dictation. A
        // double-tap of the same key latches hands-free, as on the Mac.
        // A bound side button holds and latches beside the key, in the same
        // hook (LowLevelHoldKeyRegistration's mouse half).
        _trigger = _platform.CreateTrigger(
            _holdKey, cancelOnChord: true, handsFree: true, mouseButton: _settings.MouseButton);
        _gate = new HoldGate(_trigger);
        _hotkey = new PushToTalkController(_gate, _logs.CreateLogger<PushToTalkController>());
        _hotkey.Released += OnHoldReleased;
        _microphone = _platform.CreateMicrophone();
        WatchMicrophoneEndpoint();
        WatchTakeBuffers();
        _tap = new PushToTalkMicrophoneTap(
            _hotkey, _microphone, Policy.TapOptions, _logs.CreateLogger<PushToTalkMicrophoneTap>());
        _tap.BufferLimitReached += OnBufferLimitReached;

        var edges = _platform.CreateInsertionEdges();
        _edges = edges;
        var insertion = new FocusedTextInsertion(
            edges.Target, edges.Clipboard, edges.Paste,
            _logs.CreateLogger<FocusedTextInsertion>(),
            pasteSettleDelay: _options.PasteSettleDelay,
            secureField: edges.SecureField);

        // No formatting switch is passed: the session's default (on —
        // sentence capitals, no trailing ellipsis) is the Windows behaviour.
        var session = new DictationSession(
            _hotkey, _tap, ComposePreview(_worker!), insertion,
            _logs.CreateLogger<DictationSession>(),
            targetResolver: edges.Target,
            secureField: edges.SecureField,
            microphoneAccess: _platform.MicrophoneAccess,
            pasteFieldCheck: _options.RunPasteFieldCheck);
        session.StateChanged += OnSessionStateChanged;
        session.Completed += OnSessionCompleted;
        _session = session;
        _shownState = DictationState.Armed;
        return session.Arm();
    }

    private void Stop()
    {
        StopTrigger();
        _hud?.Clear();
        _worker?.Dispose();
        _worker = null;
        _restarting = false;
        _workerResetExpected = false;
        _shownState = DictationState.Idle;
    }

    /// <summary>Everything <see cref="Arm"/> built; the worker stays up.</summary>
    private void StopTrigger()
    {
        StopLevel();
        StopSilence();
        DisposePreview();
        _fieldCheck = null;
        _press++;
        _holdStartedAt = null;
        _holdEnding = null;
        // A take under way is left on disk for the next launch (DictationController.Takes.cs).
        LeaveTake();

        // Detach first, so the Armed/Cancelled pair Cancel raises below is
        // already from a session this controller no longer holds.
        var session = _session;
        _session = null;
        session?.Cancel();
        session?.Dispose();

        _tap?.Dispose();
        _tap = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _gate = null;
        _trigger?.Dispose();
        _trigger = null;
        StopWatchingMicrophoneEndpoint();
        StopWatchingTakeBuffers();
        ForgetHookWatch();
        _microphone?.Dispose();
        _microphone = null;
        _edges = null;
        _wasDown = false;
        _pressRefused = false;
        _latchShown = false;
        _holdByMouse = false;
        // After the session's Cancel above, which answered a live hold.
        Attempts.Abandon();
    }

    private void Fail(string status) => Fail(new DictationStatus(status));

    private void Fail(DictationStatus status)
    {
        Stop();
        _enabling = false;
        if (_disposed)
        {
            return;
        }

        Render(status);
        if (_revealOnFailure)
        {
            // Hidden — started at sign-in, closed while enabling, or enabled
            // from the notification area — and could not come on: the icon
            // alone would hide the reason.
            _revealOnFailure = false;
            _view.Reveal();
        }
    }

    // ─── Session events ─────────────────────────────────────────────────

    private void OnSessionStateChanged(object? sender, DictationState state)
    {
        if (state == DictationState.Transcribing)
        {
            // Released to transcription: this hold is the next dictation, and
            // it ends the watch over the last one, unlearned (LEARNING.md §7).
            // Here, on the thread that raised it, not in the post below: the
            // session raises this before it asks the recogniser, so the watch
            // is over before this dictation's words are pasted, while a post
            // can run after they land. Thread-safe, and a no-op without a watch.
            _watch?.End(CorrectionWatchEnd.NextDictation);
        }

        _post(() => ShowSessionState(sender, state));
    }

    private void ShowSessionState(object? sender, DictationState state)
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        if (_trigger is not null)
        {
            // A press waiting for its password-field check keeps Escape
            // armed through a state change the last dictation posted late.
            _trigger.DictationInFlight =
                state is DictationState.Listening or DictationState.Transcribing || _fieldCheck is not null;
        }

        _shownState = state;
        FollowLevel(state);
        FollowSilence(state);
        FollowPreview(state);
        _hud?.State(state);
        // A latch, or a countdown, already under way when this repaint
        // reached the UI keeps its line.
        ShowListeningClock();
        if (state is DictationState.Armed or DictationState.Idle)
        {
            if (_closePending)
            {
                // The insertion is over — the session leaves Inserting only
                // after the clipboard restore returned — so the quit the user
                // asked for can go ahead. Posted, not called: the session
                // raised this dictation's Completed right after this state,
                // and quitting here would dispose the controller before that
                // ran, losing what it keeps (the first-dictation flag).
                _closePending = false;
                _post(() =>
                {
                    if (!_disposed)
                    {
                        _view.Quit();
                    }
                });
                return;
            }

            // Back at rest: the outcome sentence that follows says what
            // happened, and repainting "Enabled" first would only flash.
            Render();
            return;
        }

        // A fault's stop (DictationController.Takes.cs) or the policy's
        // (DictationController.HoldPolicy.cs): one hold has at most one.
        Render(TakeStatusFor(state) ?? TranscribingStatus(state));
    }

    private void OnSessionCompleted(object? sender, DictationSessionResult result)
    {
        // A cancel raises this synchronously, inside the Cancel call that
        // CancelHold made, so this is the moment its reason is known.
        var cancelledBy = result.Outcome == DictationOutcome.Cancelled ? _cancelling : TriggerCancel.None;
        // The receipt ends here, before the post: a result the post would
        // drop as stale (a quit as the words land) still gets its own ending.
        // The post carries the receipt on for a refused paste's recovery line
        // and for Copy last text of the words it leaves.
        var attempt = Attempts.Completed(result, cancelledBy);
        // The take this result answers, taken here for the same reason.
        var take = TakeOfResult();
        // A timer can start (or finish) replacing the dead worker before
        // this callback reaches the UI. Preserve the failed hold's state,
        // rather than classifying it from the replacement's current state.
        var workerStopped = result.Outcome == DictationOutcome.RecognizerUnavailable
            || (result.Outcome == DictationOutcome.RecognizerFailed
                && (_restarting || _worker is { IsReady: false, IsStarting: false }));
        _post(() => Completed(sender, result, cancelledBy, workerStopped, attempt, take));
    }

    private void Completed(
        object? sender,
        DictationSessionResult result,
        TriggerCancel cancelledBy,
        bool workerStopped,
        DictationAttemptLog? attempt,
        DictationTake? take)
    {
        if (!ReferenceEquals(sender, _session))
        {
            if (take is not null)
            {
                // A session detached under its take (DictationController.Takes.cs).
                SettleDetachedTake(take, result, cancelledBy);
            }

            return;
        }

        _lastWorkerUse = _options.Clock();
        var stoppedBy = _holdEnding;
        _holdEnding = null;
        // Kept for Recover last dictation, or deleted (DictationController.Takes.cs),
        // whoever ended the take: the user, a fault, or the hold policy.
        var kept = SettleTake(take, result, cancelledBy);
        var stop = TakeStopOfResult();

        if (RecogniserAnswered(result))
        {
            _restartAttempts = 0;
            _workerResetExpected = false;
        }

        if (workerStopped)
        {
            // The worker is gone. Restart it now rather than at the next
            // press, so the user is not left holding a key that records and
            // then fails again.
            _workerResetExpected = false;
            var stopped = new DictationStatus(kept ? DictationCopy.TakeSavedWorkerRestarting : DictationCopy.WorkerStopped);
            if (_restarting)
            {
                _restartAnnouncement = stopped;
                Render(_restartAnnouncement);
            }
            else if (RestartBudgetSpent)
            {
                // Spent before this dictation: no restart follows, and the
                // give-up says so in the window and on the indicator. This
                // result's own pill would only replace that line.
                GiveUpRestarting();
                return;
            }
            else
            {
                RestartWorker(stopped);
            }

            _hud?.Result(result, new DictationHudContext(RecognizerRestarting: _restarting, TakeSaved: kept));
            return;
        }

        RememberResult(result, attempt);
        LogPreviewComparison(result);
        var status = TakeStatus(result, cancelledBy, kept, stop) ?? result.Outcome switch
        {
            DictationOutcome.Cancelled when cancelledBy == TriggerCancel.Chord =>
                new DictationStatus(DictationCopy.CancelledByShortcut(_holdKey)),
            DictationOutcome.InsertionRefused or DictationOutcome.NoFocusedWindow =>
                KeepOnClipboard(result, attempt),
            DictationOutcome.SecureField when result.SecureFieldAtPaste => RefusedAtPaste(result, attempt),
            _ => StatusAfterStop(result, stoppedBy)
                ?? DictationCopy.ForResult(result, _holdKey, _platform.MicrophoneAccess.Probe),
        };
        AfterDelivery(result);
        Render(status);
        _hud?.Result(result, new DictationHudContext(
            KeptOnClipboard: status.Text is DictationCopy.KeptOnClipboard or DictationCopy.KeptOnClipboardNoWindow,
            CancelledByShortcut: cancelledBy == TriggerCancel.Chord,
            Stopped: stoppedBy,
            WordsKeptInApp: status.Text is DictationCopy.WordsStillHere or DictationCopy.WordsStillHereNoWindow,
            TakeStop: stop.Why,
            TakeStoppedAt: stop.At,
            TakeSaved: kept));
        WatchForCorrections(result);
    }

    /// <summary>
    /// After a dictation whose words landed, watch that text for the user's
    /// fixes while "Learn from my corrections" is on. Never after a refused
    /// paste, the clipboard fallback or a password field: none of them
    /// delivered anything to watch.
    /// </summary>
    private void WatchForCorrections(DictationSessionResult result)
    {
        if (_watch is null || !_settings.LearnFromCorrections || !result.TextWasDelivered)
        {
            return;
        }

        if (_wasDown && !_pressRefused)
        {
            // The key is already down again before this result reached the
            // UI thread: most likely the next dictation, whose release would
            // end a watch at once, and whose words must not land in one. A
            // press refused because this dictation was still going in is not
            // one: it records nothing, so it does not cost this watch.
            _log.LogDebug("Not watching for corrections: the next dictation has begun");
            return;
        }

        // The hook's key-down feed goes live first. Begin starts the watch on
        // the thread pool at once, so a feed attached after it would race the
        // watch's first reads: a fix typed in that gap would be judged untyped
        // and never learned. SyncKeyboardFeed then takes it down again if the
        // watch has already ended.
        if (_trigger is not null)
        {
            _trigger.KeyboardInputObserved = _keyboardFeed;
        }

        _watch.Begin(result.Text ?? string.Empty, result.Target ?? default);
        SyncKeyboardFeed();
    }

    /// <summary>
    /// After a refused paste, leave the words on the clipboard and say so —
    /// the macOS app's fallback. Only for the two refusals that carry words: a
    /// password field, at the release or the paste, leaves nothing to keep.
    /// </summary>
    private DictationStatus KeepOnClipboard(DictationSessionResult result, DictationAttemptLog? attempt)
    {
        var text = result.UndeliveredText;
        var kept = false;
        if (!string.IsNullOrEmpty(text) && _edges?.Clipboard is { } clipboard)
        {
            try
            {
                kept = clipboard.TrySet(text);
            }
            catch (Exception error)
            {
                _log.LogWarning(error, "The refused text could not be put on the clipboard");
            }
        }

        // A count, never the words.
        _log.LogInformation(
            "The paste was refused; {Characters} characters {Kept} on the clipboard",
            text?.Length ?? 0,
            kept ? "were left" : "could not be left");
        LogRecovery(attempt, clipboard: kept);

        if (!kept)
        {
            // Still held in memory, for Copy last text (DictationController.Recent.cs).
            return WordsKeptInApp(result) ?? DictationCopy.ForResult(result, _holdKey);
        }

        return new DictationStatus(result.Outcome == DictationOutcome.NoFocusedWindow
            ? DictationCopy.KeptOnClipboardNoWindow
            : DictationCopy.KeptOnClipboard);
    }

    // ─── Render ─────────────────────────────────────────────────────────

    private void Render(string status) => Render(new DictationStatus(status));

    private void Render(DictationStatus? status = null)
    {
        if (status is not null)
        {
            _status = status;
            if (DictationCopy.IsMicrophoneBlocked(status))
            {
                _microphoneBlocked = true;
                _microphoneBlockedBy = status;
            }
        }

        var armed = _session is not null;
        var canEnable = !armed && !_enabling;
        // One rule for the window's Pause and the menu's.
        var canPause = PauseAllowed;
        var canCancel = armed && _shownState is DictationState.Listening or DictationState.Transcribing;
        // One state for the icon and the window's header (DictationController.Window.cs),
        // with the icon's whole precedence, DictationTrayGlyph's: the hook watch
        // and the window in front only report.
        var glyph = DictationTrayGlyph.State(
            armed, _enabling, _shownState, _microphoneBlocked, _hookWatch.Health, AdminWindowInFront);
        var (icon, toolTip) = DictationTrayGlyph.Picture(glyph, _holdKey);
        var header = Header(glyph, armed, canEnable, canPause);
        Shown = new DictationViewState(
            Status: _status.Line,
            OffersMicrophoneSettings: _status.OffersMicrophoneSettings,
            HoldKey: _holdKey,
            CanChooseHoldKey: CanChooseHoldKey,
            HoldKeyNotice: HoldKeyAdvice.For(_holdKey, _holdKeyFromEnvironment),
            MouseButton: _settings.MouseButton,
            CanChooseMouseButton: CanChooseMouseButton,
            MouseButtonNotice: HoldKeyAdvice.ForMouse(_settings.MouseButton, _trigger?.MouseTriggerDegraded == true),
            Instructions: ShortcutLine,
            CanCancel: canCancel,
            Language: SpeechLanguages.Find(_settings.Language),
            CanChooseLanguage: CanChooseLanguage,
            LaunchAtLogin: _launchAtLogin,
            Tray: new DictationTrayState(
                icon,
                toolTip,
                header.ToggleLabel,
                header.CanToggle,
                RecentMenu(),
                CanCancel: canCancel,
                SavedTake: SavedTakeMenu()),
            LearnFromCorrections: _settings.LearnFromCorrections,
            PracticeVisible: PracticeVisible,
            PracticeStartsExpanded: !_settings.CompletedFirstDictation,
            MicrophoneOpen: armed && _shownState == DictationState.Listening,
            Recent: RecentView(),
            Header: header,
            LaunchAtLoginNote: _launchAtLoginNote,
            SavedTake: SavedTakeView(),
            SilenceStop: _settings.SilenceStop);
        _view.Render(Shown);
    }

    /// <summary>
    /// Pause dictation is live, in the window and in the notification-area
    /// menu alike: on, and no dictation listening, transcribing or typing. The
    /// Mac disables its toggle in both places while it records or is busy
    /// (DictationHomeView.swift, DictationApp.swift), and its toggle()
    /// refuses then too (<see cref="TryPause"/>). Cancel dictation sits right
    /// beside it then, and a slip onto Pause would discard the recording and
    /// unload the speech model; and a Pause while the words are typed would
    /// cut into the clipboard restore.
    /// </summary>
    private bool PauseAllowed =>
        _session is not null
        && _shownState is not (DictationState.Listening or DictationState.Transcribing or DictationState.Inserting)
        // Nor while Recover last dictation transcribes on the worker a pause would stop.
        && !_recovering;

    /// <summary>
    /// The hook as the push-to-talk controller reads it: the physical key,
    /// except that a hold the app already ended — at the recording limit, a
    /// silence stop, or from the Cancel button — reads as released until the
    /// key really comes up, so that one press is one dictation.
    /// </summary>
    private sealed class HoldGate(IDictationTrigger trigger) : IHotkeyRegistration
    {
        public bool EndedEarly { get; set; }

        public HotkeyBinding Binding => trigger.Binding;

        public bool Register() => trigger.Register();

        public void Unregister() => trigger.Unregister();

        public bool IsKeyDown() => !EndedEarly && trigger.IsKeyDown();
    }
}
