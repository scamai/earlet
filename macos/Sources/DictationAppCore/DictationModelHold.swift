import Foundation

extension DictationModel {
    public func selectSilenceSetting(_ setting: DictationSilenceSetting) {
        guard !recording, !busy, !recoveringSavedTake else { return }
        silenceSetting = setting
        env.settings.silenceSetting = setting
    }

    // ─── Escape's question ───

    /// Escape's question stands: the next Escape discards.
    var confirmingDiscard: Bool { discardQuestion != nil }

    /// Escape during a hold. On a take longer than
    /// `escapeAsksAfterSeconds` the first press only asks, in the HUD: the
    /// window is not brought forward, so the field being dictated into
    /// keeps the keyboard. A second press within `escapeQuestionSeconds`
    /// discards; after that the take carries on and the next press asks
    /// again. The key watch drops autorepeats (`isARepeat`), so Escape held
    /// down asks once and never answers. The window's and the menu's Cancel
    /// never ask (`cancel()`): a button is a choice, a key can be a slip.
    func escapePressed() async {
        guard let attempt, !busy else { return }
        let timing = env.timing
        if !confirmingDiscard, let start = holdStartedAt {
            let length = Date().timeIntervalSince(start)
            if length > timing.escapeAsksAfterSeconds {
                discardQuestion = DictationHUDContent.discardQuestion(seconds: length)
                holdNotice = DictationHUDContent.discardQuestionHint
                env.log("Escape on a take of \(Int(length)) s asks before it discards")
                Task { [weak self] in
                    do { try await Task.sleep(for: .seconds(timing.escapeQuestionSeconds)) } catch { return }
                    guard let self, self.attempt === attempt, self.confirmingDiscard else { return }
                    self.discardQuestion = nil
                    // A silence warning that is due comes back on the hold
                    // monitor's next check.
                    self.holdNotice = nil
                    self.env.log("Escape's question lapsed; the take carries on")
                }
                return
            }
        }
        await cancelHold(.user)
    }

    func startHoldMonitor(_ attempt: HoldAttempt) {
        startHoldHealth(attempt)
        let started = ProcessInfo.processInfo.systemUptime
        let policy = env.timing.holdPolicy
        holdMonitor = Task { [weak self] in
            var warned = false
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(min(0.25, policy.maximumHoldSeconds))) }
                catch { return }
                guard let self, self.attempt === attempt, !self.isQuitting else { return }
                let elapsed = ProcessInfo.processInfo.systemUptime - started
                if elapsed >= policy.maximumHoldSeconds {
                    attempt.reachedMaximumHold = true
                    self.env.log("the hold reached its \(policy.maximumHoldSeconds) s ceiling")
                    await self.finishRecording()
                    return
                }
                if !warned, elapsed >= policy.maximumHoldSeconds - policy.countdownSeconds {
                    warned = true
                    self.env.playCaution()
                }
                await self.checkHoldSilence(attempt)
            }
        }
    }

    func startHoldHealth(_ attempt: HoldAttempt) {
        let timing = env.timing
        holdHealth = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(timing.workerHealthAfterSeconds)) } catch { return }
            var failedRestarts = 0
            while !Task.isCancelled {
                guard let self, self.attempt === attempt, !self.isQuitting else { return }
                // Loading has its own deadline. A ping cannot succeed until
                // init replies, and must never shorten that allowed load.
                if let loading = self.workerStart {
                    _ = await loading.value
                    guard !Task.isCancelled, self.attempt === attempt, !self.isQuitting else { return }
                    await Task.yield() // Let the startup owner clear its completed flight.
                    continue
                }
                if let target = self.worker {
                    let replied = await DictationDeadline.run(seconds: timing.workerHealthReplySeconds, work: { await target.ping() })
                    guard !Task.isCancelled, self.attempt === attempt else { return }
                    if replied != true {
                        await self.stopLivePreview()
                        guard self.attempt === attempt, self.worker === target else { return }
                        target.forceTerminate()
                        self.worker = nil
                        self.env.log("recording worker health check failed; restarting")
                        let started = await self.ensureWorkerStarted()
                        guard !Task.isCancelled, self.attempt === attempt else { return }
                        if started == .started { failedRestarts = 0 }
                        else { failedRestarts += 1 }
                        if failedRestarts >= 2 {
                            await self.finishRecording()
                            return
                        }
                    }
                } else {
                    let started = await self.ensureWorkerStarted()
                    guard !Task.isCancelled, self.attempt === attempt else { return }
                    if started != .started { failedRestarts += 1 }
                    if failedRestarts >= 2 { await self.finishRecording(); return }
                }
                do { try await Task.sleep(for: .seconds(timing.workerHealthIntervalSeconds)) } catch { return }
            }
        }
    }

    /// Also called directly by tests with retained PCM, without a real clock.
    func checkHoldSilence(_ attempt: HoldAttempt) async {
        guard self.attempt === attempt, attempt.captureStarted,
              let snapshot = takeTap.live()?.tracker.snapshot(mode: latched ? .handsFree : .held) else { return }
        if case .stop(let reason) = snapshot.decision {
            env.log("hold stopped for silence: \(reason)")
            env.playCaution()
            await finishRecording()
            return
        }
        guard !confirmingDiscard else { return }
        // An input sending nothing outranks the quiet warning: speaking up
        // cannot help.
        guard snapshot.silentInputSeconds >= DictationHoldTracker.silentInputNoticeSeconds else {
            attempt.silentInputNotice = nil
            holdNotice = snapshot.decision == .silenceWarning ? "Quiet now — keep speaking to continue." : nil
            return
        }
        guard let notice = attempt.silentInputNotice else {
            // Naming the input reads Core Audio: off the main actor, bounded,
            // once per silent run. Then decide again on the audio by now,
            // which may no longer be silent.
            let capture = env.capture
            let name = await DictationDeadline.run(seconds: 0.5, work: { capture.boundInputName })
            guard self.attempt === attempt, recording else { return }
            attempt.silentInputNotice = DictationHUDMetrics.silentInputNotice(
                deviceName: name, holdKeyName: holdKey.name, maximumHoldSeconds: maximumHoldSeconds)
            await checkHoldSilence(attempt)
            return
        }
        holdNotice = notice
    }
}
