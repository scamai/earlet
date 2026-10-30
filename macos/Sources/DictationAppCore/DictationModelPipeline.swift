import Foundation
import DictationHistory
import DictationKit

extension DictationModel {
    func finishRecording() async {
        guard let attempt, attempt.captureStarted, !busy else { return }
        attempt.diagnostics.markReleased()
        let session = attempt.session
        let release = ReleaseCancel(gate: attempt.gate)
        let atCeiling = attempt.reachedMaximumHold
        // The hold's length stands in for the clip's: the audio is measured
        // only inside `triggerUp`, and the pre-roll it leaves out is covered
        // by the fixed part of the bound.
        let heldSeconds = ProcessInfo.processInfo.systemUptime - attempt.startedAt
        // Transcribing goes up BEFORE recording comes down. The HUD resolves
        // every change it is sent, and a release that passed through "neither"
        // hid the capsule and brought it straight back: "Transcribing…"
        // blinked out on every dictation and the hide's fade could order the
        // panel out after it had been shown again.
        transcribing = true
        endHold()
        // Released to transcription: this hold is the next dictation, and it
        // ends the watch on the last paste, unlearned (LEARNING.md §7). Not at
        // the press, which may yet be a shortcut on the hold key (Right Shift
        // + T typing the capital a fix needs, Right Option + Delete), a press
        // the privacy gate refuses, or one cancelled. Before the paste, so the
        // old watch never sees this dictation's words.
        abandonObservation("next dictation")
        busy = true
        beginRelease(release)
        keyCheck.released()
        defer {
            endRelease()
            if !isQuitting, let notice = keyCheck.takeRuntimeNotice() { status += " " + notice }
        }

        // Close the microphone at release, before waiting for a cold worker.
        // The session later consumes this cached result exactly once; its
        // forensic record is kept for the ending's words.
        var released: DictationCapture.CaptureResult?
        if let capture = attempt.capture {
            released = await DictationDeadline.run(seconds: 3, work: {
                await capture.stop(keepWarm: capture.isCurrentlyWarm)
            })
        }
        guard !isQuitting else { return }

        // Invalidate before waiting for startup: a cold hold that has already
        // ended needs only its final decode, never a late preview stream.
        await stopLivePreview()

        // The capture may have started while the worker was still loading
        // (after a rebuild, say). Wait for it here, at release, so the words
        // already spoken are kept rather than refused as "not ready".
        if worker?.isReady != true { status = "Starting the speech engine…" }
        guard let readiness = await release.unlessCancelled({ [weak self] in
            await self?.ensureWorkerStarted() ?? .failed
        }) else {
            // Cancelled while the engine started. The start carries on for
            // the next hold; these words are dropped with the capture, and
            // so is the preview run, which would otherwise stay open and
            // refuse the next hold's.
            session.reset()
            _ = await env.capture.stop(keepWarm: env.capture.isCurrentlyWarm)
            env.log("release cancelled while the engine started")
            attempt.diagnostics.finish(.cancelled)
            await settleTake(worthKeeping: false)
            status = Self.releaseCancelled
            showOutcome(.cancelled)
            return
        }
        guard !isQuitting else { return }
        guard readiness == .started, let worker else {
            attempt.diagnostics.finish(.engineUnavailable)
            session.reset()
            _ = await env.capture.stop(keepWarm: env.capture.isCurrentlyWarm)
            // The words were never transcribed: the take is kept for Recover
            // Last Dictation (rule zero).
            let kept = await settleTake(worthKeeping: true)
            status = !kept
                ? (readiness == .timedOut
                    ? "The speech engine took too long to start, so nothing was inserted. Try again in a moment."
                    : "The speech engine could not start, so nothing was inserted. Check the Earlet installation.")
                : (readiness == .timedOut
                    ? DictationCopy.takeSavedEngineStartTimedOut
                    : DictationCopy.takeSavedEngineNotStarted)
            showOutcome(.engineUnavailable)
            return
        }

        // Windows' sentence: in a hands-free latch the user may still be
        // talking, and what they say from here is not recorded.
        status = atCeiling ? DictationCopy.maximumHoldReached : "Transcribing on this device…"
        // Preview has already been cancelled before this authoritative decode
        // uses the worker's one-request/one-reply pipe.
        let gate = release.gate
        // Each recognition request has its own timeout. This outer bound also
        // covers capture stop and one engine restart, before the paste gate.
        let seconds = 2 * env.timing.transcribeSeconds
            + 3 * heldSeconds * env.timing.transcribeSecondsPerAudioSecond
            + env.timing.startSeconds + 5
        let finished = await release.unlessCancelled {
            await DictationDeadline.run(
                seconds: seconds,
                work: { await session.triggerUp() },
                canAbandon: { gate.abandon() },
                // The request is blocked in the worker's pipe; killing the
                // worker is what lets it return.
                onAbandon: { worker.forceTerminate() })
        }
        guard !isQuitting else { return }
        markWorkerUsed()

        guard let finished else {
            // The transcription carries on in the worker and its text is
            // refused at the gate. A wedged one still meets its deadline.
            env.log("transcription cancelled by the user")
            attempt.diagnostics.finish(.cancelled)
            await settleTake(worthKeeping: false)
            status = Self.releaseCancelled
            showOutcome(.cancelled)
            return
        }
        guard let result = finished else {
            env.log("transcription exceeded \(seconds) s; worker killed")
            attempt.diagnostics.finish(.timedOut)
            // That worker is finished. Build and start a fresh transport so
            // the next hold works; a release before it is ready waits for it.
            if self.worker === worker { self.worker = nil }
            // The decode never answered: the take is kept for Recover Last
            // Dictation (rule zero). Retrying it first is a later step.
            let kept = await settleTake(worthKeeping: true)
            status = kept
                ? DictationCopy.takeSavedEngineNotResponding
                : "The speech engine stopped responding, so nothing was inserted. It is restarting; try again in a moment."
            showOutcome(.engineRestarting)
            Task { await restartWorker() }
            return
        }
        if case .failed(let stage, let failure, _) = result {
            env.log("dictation failed at \(stage): \(failure)")
        }
        let interruption = env.capture.lastCaptureInterruption
        let interrupted = interruption != nil
        if let interruption { env.log("hold interrupted mid-press (\(interruption.reason))") }
        if case .failed(.capture, .emptyCapture, _) = result, !interrupted {
            // A zero or empty capture means the warm engine did not deliver
            // usable input. Tear it down before the next physical hold so
            // that hold can warm a fresh capture instead of reusing the dead
            // one. D15 already returned above and owns its own recovery path.
            await env.capture.coolDown()
        }
        attempt.diagnostics.finish(result)
        // What the history keeps besides the words, read before the take's
        // file goes: every branch below that saves a dictation settles the
        // take right after.
        let detectedLanguage: String? = {
            if case .success(_, _, let language, _) = result { return language }
            return nil
        }()
        let target = attempt.target?.value
        if case .success = result, !hasCompletedFirstDictation {
            hasCompletedFirstDictation = true
            env.settings.hasCompletedFirstDictation = true
        }
        switch result {
        case .success where atCeiling:
            // The words went in: the spool goes, after the history's copy.
            let take = await takeForHistory()
            await settleTake(worthKeeping: false)
            recentResult.remember(attempt.transcript.text, needsRecovery: false)
            let inHistory = saveToHistory(attempt.transcript.text, take: take, heldSeconds: heldSeconds,
                                          app: target, delivery: .inserted, language: detectedLanguage)
            recentResult.noteInHistory(inHistory, for: recentResult.entry?.id)
            status = DictationCopy.insertedAfterMaximumHold(holdKey: holdKey)
            showOutcome(.stoppedAtLimit(seconds: env.timing.maximumHoldSeconds))
        case .success:
            let take = await takeForHistory()
            await settleTake(worthKeeping: false)
            recentResult.remember(attempt.transcript.text, needsRecovery: false)
            let inHistory = saveToHistory(attempt.transcript.text, take: take, heldSeconds: heldSeconds,
                                          app: target, delivery: .inserted, language: detectedLanguage)
            recentResult.noteInHistory(inHistory, for: recentResult.entry?.id)
            if interrupted {
                status = "Microphone stopped at \(DictationHUDContent.clock(heldSeconds)). Your captured words were inserted."
                showMicChangedNotice(status)
            } else {
                status = "Text inserted. Hold \(holdKey.name) for your next sentence."
                showOutcome(.inserted)
            }
        case .failed(_, .asr(let failure), _):
            // The words were never transcribed: the take is kept for Recover
            // Last Dictation (rule zero).
            let kept = await settleTake(worthKeeping: true)
            if Self.workerDied(failure) {
                // The worker is gone. Restart it now rather than on the next
                // press, so the user is not left holding a key that records and
                // then fails again.
                status = kept ? DictationCopy.takeSavedWorkerRestarting : DictationCopy.workerStopped
                showOutcome(.engineRestarting)
                Task { await restartWorker() }
            } else {
                status = kept
                    ? DictationCopy.takeSaved
                    : DictationCopy.failure(.asr(failure), stage: .asr, holdKey: holdKey)
                showOutcome(DictationHUDContent.ending(for: .asr(failure), stage: .asr))
            }
        case .failed(_, .insert(let failure), _):
            // The words are on the clipboard or in Copy last text: the spool
            // goes. A password field or Secure Input keeps nothing, and the
            // history keeps nothing of it either: the words may be a
            // password.
            let secure = Self.isSecureRefusal(failure)
            let take = secure ? nil : await takeForHistory()
            await settleTake(worthKeeping: false)
            recentResult.rememberRefused(attempt.transcript.text, failure: failure)
            let remembered = recentResult.entry?.id
            let kept = await keepOnClipboard(failure, text: attempt.transcript.text)
            if !secure {
                let inHistory = saveToHistory(attempt.transcript.text, take: take, heldSeconds: heldSeconds,
                                              app: target, delivery: kept ? .clipboard : .notInserted,
                                              language: detectedLanguage)
                recentResult.noteInHistory(inHistory, for: remembered)
            }
            attempt.diagnostics.recovery(clipboard: kept, retained: recentResult.entry != nil)
            let pane = env.permissions.accessibilitySettingsName
            status = DictationCopy.insert(failure, keptOnClipboard: kept, accessibilitySettingsName: pane)
            if !kept, recentResult.entry != nil {
                if case .accessibilityPermissionMissing = failure {
                    status = "Allow \(pane) to type here. "
                        + "Your words are kept: use Copy last text on Home or from Earlet's menu bar icon."
                } else {
                    status = "The text could not be typed here. Your words are still available below, "
                        + "or from Earlet's menu bar icon → Copy last text."
                }
                showOutcome(.textRecoverable)
            } else {
                showOutcome(DictationHUDContent.ending(forRefusedPaste: failure, keptOnClipboard: kept))
            }
        case .failed(.asr, .emptyTranscript, let timings) where !interrupted:
            // Audible audio, no words: the ending says why, and a take a
            // recognition gate emptied is kept for Recover Last Dictation
            // (rule zero), whose decode skips those gates.
            let reason = DictationNoWordsReason(dropReason: timings.dropReason, capturePeak: timings.capturePeak)
            let kept = await settleTake(worthKeeping: reason.keepsTake)
            env.log("no words: \(reason), recording \(kept ? "kept" : "not kept")")
            status = DictationCopy.noWords(reason, kept: kept)
            showOutcome(DictationHUDContent.ending(forNoWords: reason, kept: kept))
        case .failed(let stage, let failure, _):
            // A device failure with unreadable RAM still keeps its disk copy.
            let kept = await settleTake(worthKeeping: interrupted)
            if let interruption {
                status = kept ? "The microphone stopped. Your recording is saved in Recover last dictation."
                    : (interruption.deviceName.isEmpty
                        ? "The microphone changed while you were speaking. Could you say that again?"
                        : "Mic changed to \(interruption.deviceName) — could you say that again?")
                showMicChangedNotice(status)
            } else {
                // How long the microphone recorded, how loud, and from what:
                // a cold start or a parked tap makes the time since the press
                // longer.
                let record = released?.forensics
                let recorded = record?.holdSeconds ?? heldSeconds
                status = DictationCopy.failure(failure, stage: stage, holdKey: holdKey,
                                               heldSeconds: recorded, peak: record?.peak)
                showOutcome(DictationHUDContent.ending(for: failure, stage: stage, heldSeconds: recorded,
                                                       peak: record?.peak, deviceName: record?.deviceName))
            }
        case .commandHandled, .spokenCommand:
            await settleTake(worthKeeping: false)
            status = "Command completed."
            showOutcome(.commandDone)
        }
    }

    static let releaseCancelled = "Dictation cancelled. Nothing inserted."

    /// A refusal whose words must never be kept anywhere: the field was a
    /// password field, or another app held Secure Input.
    static func isSecureRefusal(_ failure: DictationInsertFailure) -> Bool {
        switch failure {
        case .secureFieldFocused, .secureInputActive: return true
        case .accessibilityPermissionMissing, .focusedElementNotEditable, .noFocusedElement,
             .pasteNeverRequested, .keyEventPostFailed: return false
        }
    }

    /// Uses the same clipboard-safe writer as a refused paste. Block a new
    /// insertion until this deliberate copy finishes settling the old one.
    public func copyRecentResult() async {
        guard !busy, !recording, !isQuitting else { return }
        busy = true
        defer { busy = false }
        if let copied = await recentResult.copy() {
            env.log("recent text copy: \(copied ? "copied" : "failed; available for retry")")
            if copied { await recoveredWordsCopied() }
            showOutcome(copied ? .resultCopied : .resultCopyFailed)
        }
    }

    /// `transcribing` is already up (`finishRecording` raises it before the
    /// hold ends).
    func beginRelease(_ release: ReleaseCancel) {
        self.release = release
        // The hold's key watch ended with the hold. This one listens for
        // Escape only: typing elsewhere while the words are on their way is
        // not a shortcut.
        env.keyWatch.begin { [weak self] code in
            if code == HoldChordGuard.escapeKeyCode { self?.cancelRelease() }
        }
    }

    func endRelease() {
        env.keyWatch.end()
        release = nil
        transcribing = false
        busy = false
    }

    /// Escape, or Cancel, after the release. Refused once the paste has
    /// begun: those words are already landing.
    func cancelRelease() {
        release?.cancel()
    }

    /// Failures after which the worker process has to be started again.
    static func workerDied(_ failure: DictationAsrFailure) -> Bool {
        switch failure {
        case .ioError, .notInitialized, .workerUnavailable: return true
        case .engineError: return false
        }
    }

    /// Starts a replacement worker in the background and says so when it is
    /// ready, unless something newer has taken the status line.
    func restartWorker() async {
        guard enabled else { return }
        let before = status
        let result = await ensureWorkerStarted()
        env.log("worker restart: \(result)")
        guard enabled, !busy, !recording, status == before else { return }
        status = result == .started
            ? "The speech engine restarted. Hold \(holdKey.name) to try again."
            : "The speech engine could not restart. Pause and enable dictation to try again."
    }

    /// The session's insertion step, behind the hold's gate: an attempt
    /// abandoned at its deadline never pastes late.
    func insert(_ text: String, gate: InsertionGate, transcript: TranscriptHolder, target: PasteTarget)
    async -> (outcome: TextInserter.InsertOutcome, timing: TextInserter.InsertTiming) {
        guard !isQuitting, gate.beginInsertion() else {
            // Abandoned: the outcome goes nowhere, the user has already been
            // told nothing was inserted.
            return (.failed(.pasteNeverRequested), TextInserter.InsertTiming())
        }
        transcript.text = text
        let revision = learningRevision
        var beforePaste: DictationCorrectionObserver.PasteSnapshot?
        if learningEnabled, env.learning != nil, let app = target.value {
            beforePaste = await env.correctionWatch.prepare(targetApp: app)
        }
        guard !isQuitting else {
            return (.failed(.pasteNeverRequested), TextInserter.InsertTiming())
        }
        let inserted = await env.insertText(text)
        // Typed into the field: only then is there a span to watch. A refused
        // paste (a password field, Secure Input, nothing focused) and the
        // clipboard fallback that follows one are never watched.
        if case .success = inserted.outcome, revision == learningRevision {
            watchPaste(text, into: target.value, beforePaste: beforePaste)
        }
        return inserted
    }

    /// After a refused paste: the words go on the clipboard, except where
    /// keeping them is the leak (a password field, Secure Input). Returns
    /// whether they are on it.
    func keepOnClipboard(_ failure: DictationInsertFailure, text: String?) async -> Bool {
        switch failure {
        case .secureFieldFocused, .secureInputActive:
            return false
        case .accessibilityPermissionMissing, .focusedElementNotEditable, .noFocusedElement,
             .pasteNeverRequested, .keyEventPostFailed:
            guard let text else { return false }
            return await env.clipboardFallback(text)
        }
    }

}
