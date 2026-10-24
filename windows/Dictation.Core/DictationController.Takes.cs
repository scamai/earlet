using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>The window's Saved recording section, while a take is kept.</summary>
/// <param name="Title">"Saved recording".</param>
/// <param name="Detail">Why it is kept and how long it is, and, while
/// dictation is off, that turning it on recovers it.</param>
/// <param name="Time">When it was recorded, in the culture's short time.</param>
/// <param name="RecoverLabel">"Recover last dictation", or "Recovering…".</param>
/// <param name="CanRecover">A recovery may start now.</param>
/// <param name="Recovering">A recovery is running.</param>
/// <param name="CanDiscard">Discard is live (not during a recovery).</param>
public sealed record DictationSavedTakeView(
    string Title,
    string Detail,
    string Time,
    string RecoverLabel,
    bool CanRecover,
    bool Recovering,
    bool CanDiscard);

/// <summary>The notification-area menu's Recover last dictation.</summary>
/// <param name="Label">"Recover last dictation · &lt;time&gt;", or "Recovering…".</param>
/// <param name="CanRecover">Whether the item is live.</param>
public sealed record DictationSavedTakeMenu(string Label, bool CanRecover);

// Part of DictationController (see DictationController.cs): long takes are
// never lost (LIMIT 2026-10-10, Windows stage 1, LIMIT-WIN-A). Every take is
// spooled to disk as it is recorded (DictationTakeSpool) and deleted once its
// words went in; a take that cannot be transcribed is kept for Recover last
// dictation, and so is one a closing Dictation leaves, offered at the next
// launch. A fault ends a take and transcribes what was captured: a
// microphone that stops delivering (DictationCaptureWatchdog), one whose
// stream Windows stops with an error, and one that is unplugged; and while a
// long take is recorded the speech worker is asked whether it is alive, and
// started again when it is not. Escape asks before it throws a long take
// away. The shared partials only call in here, one line each: Start
// (OfferSavedTakeAtLaunch), Tick (ExpireKeptTake, WatchTake), RouteCancel
// (AskToDiscard), CopyFinished and DismissLastText (RecoveredWordsCopied,
// RecoveredWordsDismissed),
// Admit (BeginTake), Arm and StopTrigger (WatchTakeBuffers,
// StopWatchingTakeBuffers, LeaveTake), ShowSessionState (TakeStatusFor),
// OnSessionCompleted and Completed (TakeOfResult, SettleTake, TakeStatus,
// TakeHudContext), Render (SavedTakeView, SavedTakeMenu), Housekeeping and
// PauseAllowed (_recovering), MicrophoneEndpointChanged (StopTake) and
// EnableAsync (the decode deadline and DecodeRetried).
public sealed partial class DictationController
{
    // ─── The spool ──────────────────────────────────────────────────────

    private DictationTakeSpool? _spool;

    /// <summary>Made on first use, on the UI thread, before any take.</summary>
    private DictationTakeSpool Spool => _spool ??= new DictationTakeSpool(
        _options.Takes.Directory, _options.Takes.RunFileWork, _logs.CreateLogger<DictationTakeSpool>());

    /// <summary>The take being recorded or transcribed, from its admission
    /// to the session's result for it. Read on the capture thread.</summary>
    private volatile DictationTake? _take;

    /// <summary>When the take's last buffer arrived, as clock ticks; 0 while
    /// none has. Written on the capture thread.</summary>
    private long _lastBufferTicks;

    /// <summary>The microphone whose buffers and faults the takes hear: this
    /// arm's.</summary>
    private IAudioCaptureDevice? _takeMicrophone;

    /// <summary>A fault that ended the take, how much it held then, and the
    /// microphone Windows named for the next hold; read by its ending.</summary>
    private (DictationTakeStop Why, TimeSpan At, string? NextMicrophone) _takeStop;

    /// <summary>When the take's capture was first seen up, on the
    /// controller's clock: what the watchdog's graces count from, as the
    /// Mac's count from a microphone already open. Windows opens the device
    /// at the press, on this thread, and a Bluetooth headset switching
    /// profiles can take seconds to return; counted from the admission, that
    /// wait would use up the first buffer's grace.</summary>
    private TimeSpan? _captureUpAt;

    /// <summary>The watchdog's kill switch, read once a run:
    /// <see cref="DictationEnvironment.CaptureWatchdog"/>.</summary>
    private bool? _watchdogOn;

    /// <summary>The take kept for Recover last dictation, or null.</summary>
    private DictationKeptTake? _kept;

    /// <summary>A recovery is reading and transcribing <see cref="_kept"/>.</summary>
    private bool _recovering;

    /// <summary>
    /// A recovery's words, waiting in the Recent result (this entry) to reach
    /// the clipboard, and the kept take they came from, which stays on disk
    /// until then: the Recent result is one slot, and the next dictation's
    /// result replaces it, so until the words are copied the recording is
    /// their only other copy (rule zero).
    /// </summary>
    private (Guid Entry, DictationKeptTake Kept)? _recovered;

    // ─── The worker's health during a long take ─────────────────────────

    private Task<bool>? _ping;
    private TimeSpan? _lastPingAt;
    private int _pingFailures;

    /// <summary>A health check is out and has not been answered back on the
    /// controller's thread. For the tests, which wait for it.</summary>
    internal bool CheckingWorker => _ping is not null;

    // ─── Escape's question ──────────────────────────────────────────────

    /// <summary>When Escape asked to discard the take, while the question stands.</summary>
    private TimeSpan? _discardAskedAt;

    /// <summary>The hook's flag was raised for the question now pending: not
    /// raised again until that question has been asked and has run out, so
    /// an Escape the hook took between this tick's cancel read and its sync
    /// is not answered by raising the flag under it.</summary>
    private bool _escapeAskRaised;

    // ─── Launch ─────────────────────────────────────────────────────────

    /// <summary>
    /// Start's: what the last run left. A take a crash or a quit left behind
    /// is offered (and brings the window forward, <see cref="Start"/>); a
    /// kept take older than <see cref="DictationTakeOptions.KeptFor"/> is
    /// deleted unheard.
    /// </summary>
    private void OfferSavedTakeAtLaunch()
    {
        _kept = Spool.AtLaunch(_options.Now(), _options.Takes.KeptFor);
        if (_kept is { } kept)
        {
            _log.LogInformation(
                "A saved recording of {Seconds:0.#} s is offered for recovery: {Why}",
                kept.Length.TotalSeconds,
                kept.Interrupted ? "Dictation closed before it was transcribed" : "it could not be transcribed");
        }
    }

    /// <summary>Whether the launch found a take a closing Dictation left: the
    /// window comes forward with it, even at sign-in.</summary>
    private bool OfferedInterruptedTake => _kept is { Interrupted: true };

    // ─── One take ───────────────────────────────────────────────────────

    /// <summary>Admit's, before the microphone opens: the take's file, so its
    /// first buffer is spooled too.</summary>
    private void BeginTake()
    {
        if (Interlocked.Exchange(ref _take, null) is { } unanswered)
        {
            // The session never answered the last take (its pipeline threw):
            // kept, not lost.
            _log.LogWarning("A take the session never answered is kept for recovery");
            KeepTake(unanswered);
        }

        EndAskToDiscard();
        _takeStop = default;
        _captureUpAt = null;
        _pingFailures = 0;
        _lastPingAt = null;
        Interlocked.Exchange(ref _lastBufferTicks, 0);
        try
        {
            _take = Spool.Begin(_options.Now());
        }
        catch (Exception error)
        {
            // Dictation goes on unspooled.
            _log.LogWarning("The take could not be spooled ({Error})", error.GetType().Name);
            _take = null;
        }
    }

    /// <summary>Arm's: hear this arm's microphone's buffers and faults.</summary>
    private void WatchTakeBuffers()
    {
        if (_microphone is not { } microphone)
        {
            return;
        }

        microphone.BufferCaptured += OnTakeBuffer;
        if (microphone is IAudioCaptureFaultWatch faults)
        {
            faults.CaptureFailed += OnCaptureFailed;
        }

        _takeMicrophone = microphone;
    }

    /// <summary>StopTrigger's, before the microphone is disposed.</summary>
    private void StopWatchingTakeBuffers()
    {
        if (_takeMicrophone is not { } microphone)
        {
            return;
        }

        microphone.BufferCaptured -= OnTakeBuffer;
        if (microphone is IAudioCaptureFaultWatch faults)
        {
            faults.CaptureFailed -= OnCaptureFailed;
        }

        _takeMicrophone = null;
    }

    /// <summary>
    /// A buffer, on the device's capture thread: stamped for the watchdog and
    /// spooled, while the tap is capturing for the take. Conversion only
    /// here; the write runs on the spool's own thread.
    /// </summary>
    private void OnTakeBuffer(object? sender, AudioChunk chunk)
    {
        if (_tap is not { IsCapturing: true } || chunk is null || chunk.Samples.Length == 0)
        {
            return;
        }

        // The watchdog's stamp whether or not the take could be spooled.
        Interlocked.Exchange(ref _lastBufferTicks, Math.Max(1, _options.Clock().Ticks));
        if (_take is { } take)
        {
            _spool?.Append(take, chunk);
        }
    }

    /// <summary>StopTrigger's, before the session is cancelled: Dictation is
    /// pausing or closing under a take. Its file stays, and the next launch
    /// offers it (rule zero: a quit mid-take loses nothing).</summary>
    private void LeaveTake()
    {
        EndAskToDiscard();
        LowerEscapeAsk();
        if (Interlocked.Exchange(ref _take, null) is { } take)
        {
            _log.LogInformation("Dictation stopped during a take; its recording is left for the next launch to offer");
            Spool.Leave(take);
        }
    }

    /// <summary>
    /// OnSessionCompleted's, on the thread that raised the result: the take
    /// this result answers. In the app no newer press can have begun one:
    /// the session's pipeline starts on the UI thread and every await in it
    /// resumes there (<c>ConfigureAwait(true)</c>), so its Finish brings it
    /// back to rest and raises the result in one call on the thread that
    /// handles presses (as <see cref="DictationAttemptLog"/> relies on too).
    /// The tests' fakes have no UI thread and finish on the pool. A pipeline
    /// that faults raises no result at all: the next press keeps that take
    /// (<see cref="BeginTake"/>).
    /// </summary>
    private DictationTake? TakeOfResult() => Interlocked.Exchange(ref _take, null);

    /// <summary>
    /// Completed's: what becomes of the take, by its ending. A decode that
    /// failed even on its retry keeps it for Recover last dictation, and so
    /// does a shortcut that ended a long one (only Escape and Cancel throw a
    /// take away); every other ending deletes it: its words went in, or are
    /// on the clipboard or in Copy last text, or nothing was heard, or it was
    /// near a password field, or the user cancelled it.
    /// </summary>
    /// <returns>Whether the take is being kept.</returns>
    private bool SettleTake(DictationTake? take, DictationSessionResult result, TriggerCancel cancelledBy)
    {
        EndAskToDiscard();
        LowerEscapeAsk();
        if (take is null)
        {
            return false;
        }

        if (!WorthKeeping(take, result, cancelledBy))
        {
            Spool.Discard(take);
            return false;
        }

        KeepTake(take);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="take"/> must outlive its ending: a decode that
    /// failed even on its retry (refused, or no worker), or a shortcut that
    /// ended a take longer than Escape's question. Never a take that failed
    /// half-written or holds no audio.
    /// </summary>
    private bool WorthKeeping(DictationTake take, DictationSessionResult result, TriggerCancel cancelledBy) =>
        !take.Failed
        && take.Captured > TimeSpan.Zero
        && (result.Outcome is DictationOutcome.RecognizerFailed or DictationOutcome.RecognizerUnavailable
            || (result.Outcome == DictationOutcome.Cancelled
                && cancelledBy == TriggerCancel.Chord
                && take.Captured > _options.Takes.EscapeAsksAfter));

    /// <summary>
    /// Completed's, for a result whose session was detached before it was
    /// posted (a pause or a re-arm got there first): no window to tell, so a
    /// take worth keeping is left for the next launch to offer and any other
    /// is deleted.
    /// </summary>
    private void SettleDetachedTake(DictationTake take, DictationSessionResult result, TriggerCancel cancelledBy)
    {
        if (WorthKeeping(take, result, cancelledBy))
        {
            Spool.Leave(take);
        }
        else
        {
            Spool.Discard(take);
        }
    }

    /// <summary>Keep <paramref name="take"/>: offered at once, and taken back
    /// if the spool could not keep it after all, when the earlier one it
    /// replaced (which the spool then never touched) is offered again. One is
    /// kept at a time: an earlier one still offered is replaced, and the
    /// section says so.</summary>
    private void KeepTake(DictationTake take)
    {
        var previous = _kept;
        var replaced = previous is not null;
        var kept = new DictationKeptTake(take.StartedAt, take.Captured, Interrupted: false, ReplacedEarlier: replaced);
        _kept = kept;
        _log.LogInformation("The take ({Seconds:0.#} s) is kept for Recover last dictation", take.Captured.TotalSeconds);
        if (replaced)
        {
            _log.LogInformation("It replaces the earlier saved recording: one is kept at a time");
        }

        Spool.Keep(take).ContinueWith(
            t => _post(() => KeepAnswered(kept, t.IsCompletedSuccessfully && t.Result, previous)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void KeepAnswered(DictationKeptTake kept, bool ok, DictationKeptTake? previous)
    {
        if (ok || _disposed || !ReferenceEquals(_kept, kept))
        {
            return;
        }

        _log.LogWarning("The take could not be kept for recovery after all");
        // Its file failed before the spool moved it, so the earlier one is
        // still where it was: offered again, not left on disk unseen.
        _kept = previous;
        Render(_status.Text is DictationCopy.TakeSaved or DictationCopy.TakeSavedWorkerRestarting
            || _status.Text == DictationCopy.CancelledByShortcutTakeSaved(_holdKey)
            ? new DictationStatus(DictationCopy.TakeNotSaved)
            : null);
    }

    // ─── A fault ends a take, and keeps it ──────────────────────────────

    /// <summary>
    /// End the take that is listening as a release would, so what was
    /// captured is transcribed and inserted, and say why at its ending. The
    /// key, still down, reads as released until it really comes up
    /// (<see cref="HoldGate.EndedEarly"/>); a hands-free latch ends.
    /// </summary>
    private void StopTake(DictationTakeStop why, string? nextMicrophone = null)
    {
        if (_tap is not { IsCapturing: true } || _gate is not { EndedEarly: false } || _hotkey is not { IsHeld: true })
        {
            return;
        }

        var at = _take?.Captured
            ?? (_holdStartedAt is { } started ? _options.Clock() - started : TimeSpan.Zero);
        _takeStop = (why, at, nextMicrophone);
        _log.LogWarning(
            "The take ended at {Seconds:0.#} s: {Why}; transcribing what was captured", at.TotalSeconds, why);
        // The receipt's ending says the take was cut short, and by what.
        Attempts.Stopped(why);
        _holdStartedAt = null;
        _gate.EndedEarly = true;
        EndHandsFree("a fault ended the take");
    }

    /// <summary>On the thread pool: nothing is read here.</summary>
    private void OnCaptureFailed(object? sender, AudioCaptureFault fault) => _post(() => CaptureFailed(sender, fault));

    /// <summary>Windows stopped the open capture's stream with an error: the
    /// take ends there and keeps what it captured. Only for the capture open
    /// now, as a microphone change is (<see cref="MicrophoneEndpointChanged"/>).</summary>
    private void CaptureFailed(object? sender, AudioCaptureFault fault)
    {
        if (!ReferenceEquals(sender, _microphone)
            || sender is not IAudioCaptureFaultWatch watch
            || fault.Capture != watch.Capture
            || _tap is not { IsCapturing: true })
        {
            _log.LogDebug("A microphone fault ({Error}) that concerned no listening take was ignored", fault.Error);
            return;
        }

        _log.LogWarning("The microphone's stream stopped by itself while the take listened ({Error})", fault.Error);
        StopTake(DictationTakeStop.MicrophoneFailed);
    }

    /// <summary>
    /// The tick's, before the release poll: the watchdog, the worker's health
    /// check, and Escape's question.
    /// </summary>
    private void WatchTake()
    {
        var now = _options.Clock();
        if (_discardAskedAt is { } asked && now - asked >= _options.Takes.EscapeAskWindow)
        {
            EndAskToDiscard();
        }

        SyncEscapeAsk();
        if (_session?.State != DictationState.Listening
            || _tap is not { IsCapturing: true }
            || _holdStartedAt is not { } admitted
            || _gate is not { EndedEarly: false })
        {
            return;
        }

        // The first tick that sees the capture up: Admit opened it on this
        // thread, in this tick or the field check's answer before it.
        var up = _captureUpAt ??= now;
        var takes = _options.Takes;
        if (takes.StallGap is { } gap && WatchdogOn)
        {
            var last = Interlocked.Read(ref _lastBufferTicks);
            if (DictationCaptureWatchdog.Stalled(
                    now, up, last == 0 ? null : TimeSpan.FromTicks(last), gap, takes.StartupGrace, takes.FirstBufferGrace))
            {
                StopTake(DictationTakeStop.MicrophoneStalled);
                return;
            }
        }

        CheckWorker(now, admitted);
    }

    /// <summary>The in-hold watchdog runs unless
    /// <see cref="DictationEnvironment.CaptureWatchdog"/> is <c>0</c>, the
    /// Mac's <c>dictation.capture.watchdog</c> flag: a field switch for a
    /// device whose buffers the 0.3 s gap misreads, read once a run.</summary>
    private bool WatchdogOn =>
        _watchdogOn ??= DictationEnvironment.CaptureWatchdogIsOn(_environment(DictationEnvironment.CaptureWatchdog));

    /// <summary>
    /// Every <see cref="DictationTakeOptions.PingInterval"/> once a take has
    /// run <see cref="DictationTakeOptions.PingAfter"/>: is the worker alive
    /// (<see cref="DictationWorkerSlot.CheckAsync"/>, which starts a dead one
    /// again)? After a check fails the next one goes at once, as the restart
    /// it is; when <see cref="DictationTakeOptions.PingRestarts"/> of those
    /// fail too, the take ends there. Its release's decode tries to start the
    /// worker once more, as every exchange on a dead worker does: if it
    /// starts, the words go in ("Speech recognition stopped at m:ss — your
    /// words up to then went in"); if not, the take is kept for recovery. The
    /// recording itself never waits on any of it.
    /// </summary>
    private void CheckWorker(TimeSpan now, TimeSpan admitted)
    {
        var takes = _options.Takes;
        if (takes.PingInterval is not { } every
            || _ping is not null
            || _worker is not { } slot
            || now - admitted < takes.PingAfter
            || (_lastPingAt is { } last && now - last < (_pingFailures > 0 ? TimeSpan.Zero : every)))
        {
            return;
        }

        _lastPingAt = now;
        var press = _press;
        Task<bool> ping;
        try
        {
            ping = slot.CheckAsync(takes.PingTimeout, _lifetime.Token);
        }
        catch (Exception error)
        {
            _log.LogDebug(error, "The worker's health check could not start");
            ping = Task.FromResult(false);
        }

        _ping = ping;
        ping.ContinueWith(
            _ => _post(() => WorkerChecked(slot, ping, press)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void WorkerChecked(DictationWorkerSlot slot, Task<bool> ping, int press)
    {
        if (ReferenceEquals(_ping, ping))
        {
            _ping = null;
        }

        if (_disposed || !ReferenceEquals(slot, _worker) || press != _press)
        {
            // Another worker or another take since it was asked.
            return;
        }

        if (ping.IsCompletedSuccessfully && ping.Result)
        {
            if (_pingFailures > 0)
            {
                _log.LogInformation("The speech worker answers again; the take carries on");
            }

            _pingFailures = 0;
            return;
        }

        _pingFailures++;
        _log.LogWarning(
            "The speech worker did not answer a health check during the take ({Failures} in a row)", _pingFailures);
        if (_pingFailures > _options.Takes.PingRestarts)
        {
            StopTake(DictationTakeStop.WorkerLost);
        }
    }

    // ─── Escape's question ──────────────────────────────────────────────

    /// <summary>
    /// The hook asks at the first Escape only while the take holds more than
    /// <see cref="DictationTakeOptions.EscapeAsksAfter"/> of speech and no
    /// question stands. Raised once per question: the hook lowers the flag at
    /// the Escape that asks, and only the question running out (or the take
    /// ending) lets this raise it again. Re-raised on every tick, it could go
    /// back up under an Escape the hook took after this tick's cancel read,
    /// and a quick second Escape would then only ask again.
    /// </summary>
    private void SyncEscapeAsk()
    {
        if (_trigger is null)
        {
            return;
        }

        var ask = _take is { } take
            && _discardAskedAt is null
            && _session?.State is DictationState.Listening or DictationState.Transcribing
            && take.Captured > _options.Takes.EscapeAsksAfter;
        if (!ask)
        {
            LowerEscapeAsk();
            return;
        }

        if (!_escapeAskRaised)
        {
            _escapeAskRaised = true;
            _trigger.EscapeAsksFirst = true;
        }
    }

    /// <summary>
    /// RouteCancel's: the first Escape on a long take. Nothing is discarded:
    /// the indicator asks "Discard 3:42 of speech? — press Esc again", and an
    /// Escape within <see cref="DictationTakeOptions.EscapeAskWindow"/>
    /// discards. The take carries on as if nothing happened otherwise.
    /// </summary>
    private void AskToDiscard()
    {
        if (_take is not { } take || _session?.State is not (DictationState.Listening or DictationState.Transcribing))
        {
            return;
        }

        _discardAskedAt = _options.Clock();
        _log.LogInformation(
            "Escape on a take of {Seconds:0.#} s asks before it discards; Escape again discards it", take.Captured.TotalSeconds);
        _hud?.AskToDiscard(take.Captured, listening: _session.State == DictationState.Listening);
    }

    /// <summary>The take is over, or a question stands: the hook stops
    /// asking, so the first Escape of the next take, before its first poll,
    /// cancels as Escape always did, and one during the question discards.</summary>
    private void LowerEscapeAsk()
    {
        _escapeAskRaised = false;
        if (_trigger is { EscapeAsksFirst: true } trigger)
        {
            trigger.EscapeAsksFirst = false;
        }
    }

    private void EndAskToDiscard()
    {
        if (_discardAskedAt is null)
        {
            return;
        }

        _discardAskedAt = null;
        _hud?.EndAskToDiscard(_session?.State ?? DictationState.Idle);
        ShowListeningClock();
    }

    // ─── What the window and the indicator say ──────────────────────────

    /// <summary>ShowSessionState's, while what a fault left is transcribed.</summary>
    private string? TakeStatusFor(DictationState state) =>
        state == DictationState.Transcribing && _takeStop.Why != DictationTakeStop.None
            ? DictationCopy.TakeStoppedTranscribing(_takeStop.Why, _takeStop.At)
            : null;

    /// <summary>The fault that ended the take this result answers, taken once.</summary>
    private (DictationTakeStop Why, TimeSpan At, string? NextMicrophone) TakeStopOfResult()
    {
        var stop = _takeStop;
        _takeStop = default;
        return stop;
    }

    /// <summary>Completed's sentence for a kept take or one a fault ended, or
    /// null for every other ending.</summary>
    private DictationStatus? TakeStatus(
        DictationSessionResult result,
        TriggerCancel cancelledBy,
        bool kept,
        (DictationTakeStop Why, TimeSpan At, string? NextMicrophone) stop) => result.Outcome switch
        {
            DictationOutcome.RecognizerFailed or DictationOutcome.RecognizerUnavailable when kept =>
                new DictationStatus(DictationCopy.TakeSaved),
            DictationOutcome.Cancelled when kept && cancelledBy == TriggerCancel.Chord =>
                new DictationStatus(DictationCopy.CancelledByShortcutTakeSaved(_holdKey)),
            DictationOutcome.Inserted or DictationOutcome.ClipboardRestoreFailed when stop.Why != DictationTakeStop.None =>
                new DictationStatus(DictationCopy.TakeStoppedInserted(stop.Why, stop.At, stop.NextMicrophone)),
            DictationOutcome.NoSpeech or DictationOutcome.NoAudio when stop.Why != DictationTakeStop.None =>
                new DictationStatus(DictationCopy.TakeStoppedNothingHeard(stop.Why, stop.At, _holdKey)),
            _ => null,
        };

    /// <summary>The decode's worker died or missed its deadline and the slot
    /// is trying once more; on the decode's thread.</summary>
    private void OnDecodeRetried(object? sender, EventArgs e) => _post(() =>
    {
        if (ReferenceEquals(sender, _worker) && _session?.State == DictationState.Transcribing && !_disposed)
        {
            Render(DictationCopy.RetryingTranscription);
        }
    });

    // ─── Recover last dictation ─────────────────────────────────────────

    /// <summary>
    /// A recovery may start: a take is kept, dictation is on with its speech
    /// engine, and nothing is recording, waiting on the press-time field
    /// check, transcribing, typing, copying, restarting or recovering.
    /// </summary>
    private bool CanRecover =>
        _kept is not null
        && !_recovering
        && _session is not null
        && _worker is not null
        && !_restarting
        && AtRest
        && _fieldCheck is null
        && !_recent.Copying;

    /// <summary>
    /// Recover last dictation, from the window's Saved recording section or
    /// the notification-area menu: transcribe the kept take again. Its words
    /// go to Last available text and, when nothing else is under way by
    /// then, onto the clipboard through Copy last text (never pasted: the
    /// user is in this window or this menu, not in the field they spoke
    /// into). The recording goes once its words are on the clipboard (or the
    /// user dismissed them), or once it proves to hold nothing or to be
    /// damaged; a failure, or a file that cannot be opened just now, keeps it
    /// for another try.
    /// </summary>
    public void RecoverLastDictation()
    {
        if (!CanRecover || _kept is not { } kept || _worker is not { } slot)
        {
            return;
        }

        _recovering = true;
        // Not unloaded under the decode by the idle housekeeping.
        _lastWorkerUse = _options.Clock();
        _log.LogInformation("Recovering the saved recording ({Seconds:0.#} s)", kept.Length.TotalSeconds);
        Render(DictationCopy.Recovering);
        _ = RecoverAsync(Spool, slot, kept);
    }

    /// <summary>Off the UI thread: read the kept take, transcribe it (with
    /// the slot's one retry), and report back.</summary>
    private async Task RecoverAsync(DictationTakeSpool spool, DictationWorkerSlot slot, DictationKeptTake kept)
    {
        // Not read is not damaged: a failure before the read answered keeps it.
        var audio = new DictationKeptAudio(null, Unavailable: true);
        TranscriptionResult? result = null;
        try
        {
            audio = await spool.ReadKept().ConfigureAwait(false);
            if (audio.Audio is { } capture)
            {
                result = await slot.TranscribeAsync(capture, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                _log.LogWarning("Recovering the saved recording failed ({Error})", error.GetType().Name);
            }
        }

        _post(() => Recovered(slot, kept, audio, result));
    }

    private void Recovered(
        DictationWorkerSlot slot, DictationKeptTake kept, DictationKeptAudio audio, TranscriptionResult? result)
    {
        _recovering = false;
        _lastWorkerUse = _options.Clock();
        if (_disposed)
        {
            return;
        }

        if (!ReferenceEquals(slot, _worker))
        {
            // Paused under it: the recording stays kept.
            Render();
            return;
        }

        // Still the take this recovery read: a newer failure may have
        // replaced it, and that one must not be deleted for this one.
        var same = ReferenceEquals(_kept, kept);
        if (audio.Unavailable)
        {
            // Another program holds it, say: it is not damaged.
            _log.LogWarning("The saved recording could not be opened just now; it is still kept");
            Render(DictationCopy.RecoveryNotOpened);
            _hud?.RecoveryFailed();
            return;
        }

        if (audio.Audio is null)
        {
            _log.LogWarning("The saved recording could not be read; it is discarded");
            ForgetKept(same);
            Render(DictationCopy.RecoveryUnreadable);
            return;
        }

        if (result is not { } transcript
            || transcript.Outcome is TranscriptionOutcome.RecognizerFailed or TranscriptionOutcome.RecognizerUnavailable)
        {
            _log.LogWarning("The saved recording could not be transcribed; it is still kept");
            Render(DictationCopy.RecoveryFailed);
            _hud?.RecoveryFailed();
            return;
        }

        if (transcript.Outcome != TranscriptionOutcome.Transcribed || string.IsNullOrWhiteSpace(transcript.Text))
        {
            _log.LogInformation("The saved recording held nothing to type; it is discarded");
            ForgetKept(same);
            Render(DictationCopy.RecoveredNothingHeard);
            return;
        }

        // A count, never the words.
        _log.LogInformation("The saved recording was recovered: {Characters} characters", transcript.Text.Length);
        _recent.Remember(transcript.Text, needsRecovery: false, _options.Now());
        // The recording stays until its words are on the clipboard
        // (RecoveredWordsCopied): until then a dictation that finishes first
        // would replace them in the Recent result, their only other copy.
        _recovered = same && _recent.Current is { } entry ? (entry.Id, kept) : null;
        if (CanCopyLastText)
        {
            Render(DictationCopy.Recovered);
            CopyLastText();
        }
        else
        {
            Render(DictationCopy.RecoveredUseCopy);
        }
    }

    /// <summary>The kept take is done with: forgotten, and its file deleted
    /// when it is still the one kept.</summary>
    private void ForgetKept(bool same)
    {
        if (!same)
        {
            return;
        }

        _kept = null;
        _recovered = null;
        Spool.DiscardKept();
    }

    /// <summary>CopyFinished's, for a copy that reached the clipboard: when
    /// it carried a recovery's words, the recording they came from goes.</summary>
    private void RecoveredWordsCopied(Guid entry)
    {
        if (_recovered is not { } recovered || recovered.Entry != entry)
        {
            return;
        }

        _log.LogInformation("The saved recording's words are on the clipboard; the recording is deleted");
        ForgetKept(ReferenceEquals(_kept, recovered.Kept));
        _recovered = null;
    }

    /// <summary>DismissLastText's, before it clears: dismissing a recovery's
    /// words is done with them, and the recording they came from goes too.</summary>
    private void RecoveredWordsDismissed(Guid entry)
    {
        if (_recovered is not { } recovered || recovered.Entry != entry)
        {
            return;
        }

        _log.LogInformation("The saved recording's words were dismissed; the recording is deleted");
        ForgetKept(ReferenceEquals(_kept, recovered.Kept));
        _recovered = null;
    }

    /// <summary>
    /// Tick's, paused or not: a kept take older than
    /// <see cref="DictationTakeOptions.KeptFor"/> is deleted unheard while
    /// Dictation runs, as the next launch would. Dictation starts at sign-in
    /// and can run for weeks, and the section promises 7 days at most. Not
    /// under a recovery: the first tick after it ends does it, if the
    /// recording is still kept.
    /// </summary>
    private void ExpireKeptTake()
    {
        if (_kept is not { } kept || _recovering || _disposed || _options.Now() - kept.RecordedAt < _options.Takes.KeptFor)
        {
            return;
        }

        _log.LogInformation(
            "The saved recording is older than {Days:0} days and was deleted unheard", _options.Takes.KeptFor.TotalDays);
        ForgetKept(same: true);
        Render();
    }

    /// <summary>Discard, in the Saved recording section: the kept take is
    /// deleted unheard. Not during a recovery.</summary>
    public void DiscardSavedTake()
    {
        if (_disposed || _recovering || _kept is null)
        {
            return;
        }

        _kept = null;
        Spool.DiscardKept();
        _log.LogInformation("The saved recording was discarded");
        Render(DictationCopy.SavedTakeDiscarded);
    }

    /// <summary>The window's Saved recording section, or null.</summary>
    private DictationSavedTakeView? SavedTakeView()
    {
        if (_kept is not { } kept)
        {
            return null;
        }

        var detail = kept.Interrupted
            ? DictationCopy.SavedTakeDetailInterrupted(kept.Length)
            : DictationCopy.SavedTakeDetail(kept.Length);
        if (kept.ReplacedEarlier)
        {
            detail += " " + DictationCopy.SavedTakeReplacedEarlier;
        }

        if (_recovered is { } recovered && ReferenceEquals(recovered.Kept, kept) && _recent.Current?.Id == recovered.Entry)
        {
            detail += " " + DictationCopy.SavedTakeAwaitsCopy;
        }

        return new DictationSavedTakeView(
            DictationCopy.SavedTakeTitle,
            _session is null ? detail + " " + DictationCopy.SavedTakeNeedsDictation : detail,
            DictationCopy.ShortTime(kept.RecordedAt),
            _recovering ? DictationCopy.RecoveringLastDictation : DictationCopy.RecoverLastDictation,
            CanRecover,
            _recovering,
            CanDiscard: !_recovering);
    }

    /// <summary>The notification-area menu's item, or null: the time, never
    /// the words.</summary>
    private DictationSavedTakeMenu? SavedTakeMenu() =>
        _kept is { } kept
            ? new DictationSavedTakeMenu(
                _recovering
                    ? DictationCopy.RecoveringLastDictation
                    : DictationCopy.RecoverLastDictationAt(DictationCopy.ShortTime(kept.RecordedAt)),
                CanRecover)
            : null;
}
