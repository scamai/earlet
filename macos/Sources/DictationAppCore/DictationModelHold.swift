import Foundation

extension DictationModel {
    public func selectSilenceSetting(_ setting: DictationSilenceSetting) {
        guard !recording, !busy, !recoveringSavedTake else { return }
        silenceSetting = setting
        env.settings.silenceSetting = setting
    }

    public func keepRecording() {
        confirmingDiscard = false
        holdNotice = nil
    }

    public func confirmDiscard() async {
        guard recording else { return }
        // SwiftUI may dismiss the dialog before this async action runs.
        confirmingDiscard = true
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
        switch snapshot.decision {
        case .continueListening:
            if !confirmingDiscard { holdNotice = nil }
        case .silenceWarning:
            if !confirmingDiscard { holdNotice = "Quiet now — keep speaking to continue." }
        case .stop(let reason):
            env.log("hold stopped for silence: \(reason)")
            env.playCaution()
            await finishRecording()
        }
    }
}
