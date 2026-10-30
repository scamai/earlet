// A microphone that is connected but sends nothing is not a user who said
// nothing. Headset holds of a second or more whose capture line read
// `peak=0.0000` with no exact zeros ended as "Nothing heard" and the hint to
// hold the key, because only an exactly all-zero buffer counted as no audio.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit

/// A warm capture that returns one scripted clip, with the forensic record
/// the real capture would attach to it (or none, like a scripted source).
private final class ScriptedAudioSource: DictationAudioSource, @unchecked Sendable {
    let samples: [Float]
    let forensics: DictationCaptureForensics?

    init(samples: [Float], forensics: DictationCaptureForensics?) {
        self.samples = samples
        self.forensics = forensics
    }

    var isCurrentlyWarm: Bool { true }
    func start() throws {}
    func armTriggerDown() {}
    func stop(keepWarm: Bool) async -> DictationCapture.CaptureResult {
        DictationCapture.CaptureResult(samples: samples, sampleRate: 16_000, forensics: forensics)
    }
}

/// Never reached: every clip here is refused before recognition.
private struct UnreachedAsr: DictationAsrTransport {
    func start() async -> Bool { true }
    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        Issue.record("a clip the silence gate refused reached recognition")
        return .failed(.workerUnavailable)
    }
    func shutdown() async {}
}

/// A clock the test moves by hand.
private final class ManualClock: @unchecked Sendable {
    private let lock = NSLock()
    private var nanoseconds: UInt64 = 1_000_000_000
    func advance(seconds: Double) {
        lock.lock(); nanoseconds += UInt64(seconds * 1_000_000_000); lock.unlock()
    }
    func now() -> DispatchTime {
        lock.lock(); defer { lock.unlock() }
        return DispatchTime(uptimeNanoseconds: nanoseconds)
    }
}

@Suite("A whole hold under the silent-input floor is no sound from the microphone")
struct DictationSilentInputTests {

    private static func clip(seconds: Double, level: Float) -> [Float] {
        [Float](repeating: level, count: Int(seconds * 16_000))
    }

    /// Press and release one hold whose capture measured `heldSeconds`.
    private static func failure(
        _ samples: [Float], heldSeconds: Double
    ) async -> DictationSession.SessionFailure? {
        let source = ScriptedAudioSource(
            samples: samples,
            forensics: DictationCaptureForensics(deviceName: "AirPods Pro", holdSeconds: heldSeconds))
        let session = DictationSession(capture: source, asr: UnreachedAsr())
        guard case .success = session.triggerDown() else { return nil }
        guard case .failed(let stage, let failure, _) = await session.triggerUp() else { return nil }
        #expect(stage == .capture)
        return failure
    }

    @Test("two seconds that never reach the floor, with no exact zero, are an empty capture")
    func nearSilentHoldIsAnEmptyCapture() async {
        // A stalled headset: every sample a few millionths, none exactly 0.
        let samples = Self.clip(seconds: 2, level: 0.00002)
        #expect(samples.allSatisfy { $0 != 0 })
        #expect(await Self.failure(samples, heldSeconds: 2) == .emptyCapture)
    }

    @Test("a near-silent tap is still a tap: nothing heard")
    func nearSilentTapIsNothingHeard() async {
        #expect(await Self.failure(Self.clip(seconds: 0.3, level: 0.00002), heldSeconds: 0.3) == .emptyTranscript)
        #expect(await Self.failure(Self.clip(seconds: 0.99, level: 0.00002), heldSeconds: 0.99) == .emptyTranscript)
    }

    @Test("quiet audio above the floor but under the speech gate is nothing heard, however long")
    func quietRoomIsNothingHeard() async {
        #expect(await Self.failure(Self.clip(seconds: 2, level: 0.001), heldSeconds: 2) == .emptyTranscript)
        // One sample at the floor is a signal.
        var samples = Self.clip(seconds: 2, level: 0.00002)
        samples[16_000] = DictationSession.silentInputPeak
        #expect(await Self.failure(samples, heldSeconds: 2) == .emptyTranscript)
    }

    @Test("exact zeros stay an empty capture at any length")
    func exactZerosStayEmpty() async {
        #expect(await Self.failure(Self.clip(seconds: 0.3, level: 0), heldSeconds: 0.3) == .emptyCapture)
    }

    @Test("without a forensic record the session's own clock measures the hold")
    func sessionClockMeasuresAScriptedHold() async {
        let holds: [(Double, DictationSession.SessionFailure)] = [(2.0, .emptyCapture), (0.3, .emptyTranscript)]
        for (held, expected) in holds {
            let clock = ManualClock()
            let source = ScriptedAudioSource(samples: Self.clip(seconds: held, level: 0.00002), forensics: nil)
            let session = DictationSession(capture: source, asr: UnreachedAsr(), clock: { clock.now() })
            guard case .success = session.triggerDown() else {
                Issue.record("the press was refused"); continue
            }
            clock.advance(seconds: held)
            guard case .failed(.capture, let failure, _) = await session.triggerUp() else {
                Issue.record("a refused clip was not a capture failure"); continue
            }
            #expect(failure == expected, "\(held) s")
        }
    }

    @Test("the rule itself: both a long hold and a peak under the floor")
    func ruleNeedsBothHalves() {
        let floor = DictationSession.silentInputPeak
        let hold = DictationSession.silentInputMinimumHoldSeconds
        #expect(DictationSession.isSilentInput([floor / 2], heldSeconds: hold))
        #expect(!DictationSession.isSilentInput([floor / 2], heldSeconds: hold - 0.01))
        #expect(!DictationSession.isSilentInput([floor / 2, -floor], heldSeconds: hold))
    }

    @Test("the per-press log line names a silent input, as the indicator does, not a gain to raise")
    func logCauseAgreesWithTheIndicator() throws {
        func record(peak: Float, held: Double) -> DictationCaptureForensics {
            DictationCaptureForensics(deviceName: "AirPods Pro", holdSeconds: held,
                                      sampleCount: Int(held * 16_000), durationSeconds: held, peak: peak)
        }
        let silent = try #require(record(peak: 0.00002, held: 2).likelyCause())
        #expect(silent.hasPrefix("AirPods Pro sent no signal"))
        #expect(!silent.contains("Raise the input gain"))
        #expect(record(peak: 0.00002, held: 2).summaryLine().contains("cause=\"AirPods Pro sent no signal"))
        // Above the floor and under the gate it is still about level.
        #expect(record(peak: 0.001, held: 2).likelyCause()?.hasPrefix("The input was audible but too quiet") == true)
        // A short hold under the floor is not reported as a silent input.
        #expect(record(peak: 0.00002, held: 0.8).likelyCause()?.contains("sent no signal") != true)
    }
}
#endif
