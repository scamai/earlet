import Foundation

// The warm microphone between dictations: kept open just long enough that the
// next press does not lose its first word, then closed, counted from the end
// of the last dictation rather than at the next housekeeping tick.

extension DictationModel {
    /// The grace for the bound microphone (`DictationTiming`).
    var microphoneIdleSeconds: Double {
        env.capture.usesBluetoothInput ? env.timing.bluetoothMicrophoneIdleSeconds : env.timing.microphoneIdleSeconds
    }

    /// Starts watching for the moment the warm microphone has been idle for
    /// its grace. A new press cancels it; the housekeeping tick does the
    /// same check as a backstop.
    func scheduleMicrophoneCoolDown() {
        microphoneCoolDown?.cancel()
        microphoneCoolDown = nil
        // Pause and quit close the microphone themselves.
        guard enabled, !isQuitting else { return }
        let interval = max(0.05, env.timing.microphoneCheckSeconds)
        microphoneCoolDown = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(interval))
                guard let self, !Task.isCancelled, self.env.capture.isCurrentlyWarm else { return }
                if await self.coolDownIdleMicrophone() { return }
            }
        }
    }

    /// Closes the warm microphone when nothing is using it and it has been
    /// idle for its grace. `shouldCoolDown` refuses while a capture is
    /// retaining audio, so a long hold is never cut off here.
    @discardableResult
    func coolDownIdleMicrophone() async -> Bool {
        let grace = microphoneIdleSeconds
        guard !busy, attempt == nil, env.capture.shouldCoolDown(idleSeconds: grace) else { return false }
        await env.capture.coolDown()
        env.log("microphone closed after \(grace.formatted()) s idle (\(env.capture.usesBluetoothInput ? "bluetooth" : "other"))")
        return true
    }
}
