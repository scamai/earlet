import Foundation
import DictationKit

/// One attempt owns its deadlines and retries only the recognition step.
/// A cancelled attempt never restarts a newer worker or reaches insertion.
final class DictationRetryTransport: DictationAsrTransport, @unchecked Sendable {
    let slot: WorkerSlot
    let gate: InsertionGate
    let timing: DictationTiming
    let restart: @MainActor @Sendable (any DictationWorker) async -> Bool
    let log: @Sendable (String) -> Void

    init(slot: WorkerSlot, gate: InsertionGate, timing: DictationTiming,
         restart: @escaping @MainActor @Sendable (any DictationWorker) async -> Bool,
         log: @escaping @Sendable (String) -> Void) {
        self.slot = slot; self.gate = gate; self.timing = timing
        self.restart = restart; self.log = log
    }

    func start() async -> Bool { await slot.start() }
    func shutdown() async { await slot.shutdown() }
    func setLanguageCode(_ code: String?) { slot.setLanguageCode(code) }
    func setVocabularyScene(_ scene: String?) { slot.setVocabularyScene(scene) }
    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        switch await transcribeDetailed(samples: samples, sampleRate: sampleRate, compareBaseline: false) {
        case .result(let detail): return .text(detail.text)
        case .failed(let failure): return .failed(failure)
        }
    }

    func transcribeDetailed(samples: [Float], sampleRate: Int,
                            compareBaseline: Bool) async -> DictationAsrDetailedOutcome {
        let duration = Double(samples.count) / Double(max(1, sampleRate))
        for index in 0...1 {
            guard !gate.isAbandoned, let worker = slot.worker else { return .failed(.workerUnavailable) }
            let seconds = index == 0
                ? timing.transcribeDeadline(audioSeconds: duration)
                : timing.transcribeSeconds + 2 * duration * timing.transcribeSecondsPerAudioSecond
            let answer = await DictationDeadline.run(seconds: seconds, work: {
                await worker.transcribeDetailed(samples: samples, sampleRate: sampleRate,
                                                compareBaseline: compareBaseline)
            }, onAbandon: { worker.forceTerminate() })
            let failure: DictationAsrFailure
            switch answer {
            case .result(let detail): return .result(detail)
            case .failed(let error):
                switch error {
                case .engineError: return .failed(error)
                default: failure = error
                }
            case nil: failure = .ioError("recognition deadline exceeded")
            }
            guard index == 0, !gate.isAbandoned else { return .failed(failure) }
            log("recognition failed; restarting and retrying the same recording once")
            guard await restart(worker), !gate.isAbandoned else { return .failed(failure) }
        }
        return .failed(.workerUnavailable)
    }
}
