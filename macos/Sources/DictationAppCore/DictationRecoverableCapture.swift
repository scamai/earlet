import Foundation
import DictationKit

/// On device failure the native capture clears its RAM buffer. The take's
/// ordered disk copy supplies the same session with the audio captured so far.
final class DictationRecoverableCapture: DictationAudioSource, @unchecked Sendable {
    let capture: any DictationCaptureDevice
    let tap: DictationTakeTap
    private let lock = NSLock()
    private var stopTask: Task<DictationCapture.CaptureResult, Never>?
    private var retained: (DictationTake, DictationTakeSpool)?

    init(capture: any DictationCaptureDevice, tap: DictationTakeTap) {
        self.capture = capture; self.tap = tap
    }
    var isCurrentlyWarm: Bool { capture.isCurrentlyWarm }
    private func rememberTake() {
        let live = tap.live()
        lock.lock(); retained = live.map { ($0.take, $0.spool) }; lock.unlock()
    }
    private func currentTake() -> (DictationTake, DictationTakeSpool)? {
        lock.lock(); defer { lock.unlock() }; return retained
    }
    func start() throws { rememberTake(); try capture.start() }
    func armTriggerDown() { rememberTake(); capture.armTriggerDown() }
    private func stopping(keepWarm: Bool) -> Task<DictationCapture.CaptureResult, Never> {
        lock.lock(); defer { lock.unlock() }
        if let stopTask { return stopTask }
        let take = retained
        let capture = capture
        let task = Task {
            let result = await capture.stop(keepWarm: keepWarm)
            guard capture.lastCaptureInterruption != nil,
                  let (take, spool) = take, !take.failed,
                  let samples = await spool.read(take).samples else { return result }
            return DictationCapture.CaptureResult(
                samples: samples, sampleRate: Double(DictationTakeSpool.sampleRate),
                forensics: result.forensics, interruption: result.interruption)
        }
        stopTask = task
        return task
    }
    func stop(keepWarm: Bool) async -> DictationCapture.CaptureResult {
        await stopping(keepWarm: keepWarm).value
    }
}
