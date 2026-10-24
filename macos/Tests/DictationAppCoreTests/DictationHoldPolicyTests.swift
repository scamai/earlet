import Foundation
import Testing
@testable import DictationAppCore

@Suite("Dictation hold policy")
struct DictationHoldPolicyTests {
    private let rate = DictationHoldTracker.sampleRate

    private func feed(_ tracker: DictationHoldTracker, _ seconds: Double,
                      level: Float, buffers: [Int] = [480]) {
        let total = Int(seconds * Double(rate))
        var cursor = 0
        var index = 0
        while cursor < total {
            let count = min(buffers[index % buffers.count], total - cursor)
            tracker.append([Float](repeating: level, count: count))
            cursor += count
            index += 1
        }
    }

    private func speech(_ tracker: DictationHoldTracker) {
        feed(tracker, 0.3, level: 0.01)
        feed(tracker, 1, level: 0.2)
    }

    @Test("stage one exposes the Mac ceiling, countdown, and silence defaults")
    func defaults() {
        let policy = DictationHoldPolicy.stage1
        #expect(policy.maximumHoldSeconds == 600)
        #expect(policy.countdownSeconds == 60)
        #expect(policy.silenceSetting == .thirtySeconds)
        #expect(policy.neverSpokeSeconds == 15)
        #expect(policy.afterSpeechWarningSeconds == 10)
        #expect(policy.heldNoSpeechSeconds == 60)
    }

    @Test("hands-free quiet input stops after the never-spoke limit")
    func neverSpoke() {
        let tracker = DictationHoldTracker()
        feed(tracker, 15.1, level: 0)
        #expect(tracker.snapshot().decision == .stop(.handsFreeNeverSpoke))
    }

    @Test("a confirmed speech run resets silence and warns ten seconds before stop")
    func speechThenSilence() {
        let tracker = DictationHoldTracker()
        speech(tracker)
        #expect(tracker.snapshot().speechDetected)
        // Cross the boundary by more than one 30 ms analysis frame.
        feed(tracker, 20.1, level: 0)
        #expect(tracker.snapshot().decision == .silenceWarning)
        feed(tracker, 10.1, level: 0)
        #expect(tracker.snapshot().decision == .stop(.handsFreeSilence))
    }

    @Test("continuous speech never warns or stops")
    func continuousSpeech() {
        let tracker = DictationHoldTracker()
        for _ in 0..<90 {
            feed(tracker, 0.3, level: 0.01, buffers: [17, 61, 997, 3])
            feed(tracker, 0.7, level: 0.2, buffers: [17, 61, 997, 3])
        }
        let snapshot = tracker.snapshot()
        #expect(snapshot.speechDetected)
        #expect(snapshot.decision == .continueListening)
    }

    @Test("an isolated click does not count as speech")
    func isolatedClick() {
        let tracker = DictationHoldTracker()
        feed(tracker, 5, level: 0)
        var click = [Float](repeating: 0, count: 480)
        click[0] = 1
        tracker.append(click)
        feed(tracker, 10, level: 0)
        #expect(!tracker.snapshot().speechDetected)
    }

    @Test("adaptive floor rejects a quiet noise floor but accepts louder speech")
    func adaptiveNoiseFloor() {
        let tracker = DictationHoldTracker()
        feed(tracker, 12, level: 0.012)
        #expect(!tracker.snapshot().speechDetected)
        feed(tracker, 1, level: 0.2)
        #expect(tracker.snapshot().speechDetected)
    }

    @Test("silence settings support sixty seconds and off")
    func silenceModes() {
        let sixty = DictationHoldTracker(policy: DictationHoldPolicy(silenceSetting: .sixtySeconds))
        speech(sixty)
        feed(sixty, 59, level: 0)
        #expect(sixty.snapshot().decision == .silenceWarning)
        feed(sixty, 2, level: 0)
        #expect(sixty.snapshot().decision == .stop(.handsFreeSilence))

        let off = DictationHoldTracker(policy: DictationHoldPolicy(silenceSetting: .off))
        speech(off)
        feed(off, 120, level: 0)
        #expect(off.snapshot().decision == .continueListening)
    }

    @Test("held mode uses the sixty second stuck-key guard")
    func heldGuard() {
        let tracker = DictationHoldTracker()
        speech(tracker)
        feed(tracker, 60.1, level: 0)
        #expect(tracker.snapshot(mode: .held).decision == .stop(.heldSilenceGuard))
    }

    @Test("arbitrary PCM buffer boundaries preserve frame accounting")
    func arbitraryBoundaries() {
        let tracker = DictationHoldTracker()
        feed(tracker, 0.3, level: 0.01, buffers: [1, 7, 31, 127, 479, 481, 1600])
        feed(tracker, 1, level: 0.2, buffers: [1, 7, 31, 127, 479, 481, 1600])
        let snapshot = tracker.snapshot()
        #expect(snapshot.speechDetected)
        #expect(abs(snapshot.audioSeconds - 1.29) < 0.001)
    }

    @Test("policy and tracker expose a ten minute ceiling without sleeping")
    func ceilingValues() {
        let policy = DictationHoldPolicy()
        let tracker = DictationHoldTracker(policy: policy)
        #expect(policy.maximumHoldSeconds == 10 * 60)
        #expect(policy.countdownSeconds == 60)
        #expect(tracker.snapshot().audioSeconds == 0)
    }
}
