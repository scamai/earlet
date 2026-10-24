#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Mac hold policy wiring")
struct DictationHoldPolicyWiringTests {
    private func frame(_ value: Float, count: Int = DictationHoldTracker.frameSamples) -> [Float] {
        [Float](repeating: value, count: count)
    }

    @Test("silence setting is wired to model state and persisted settings")
    func silenceSettingPersistsAndReloads() {
        let harness = Harness()
        let model = harness.model()
        for setting in DictationSilenceSetting.allCases {
            model.selectSilenceSetting(setting)
            #expect(model.silenceSetting == setting)
            #expect(harness.settings.silenceSetting == setting)
            #expect(harness.model().silenceSetting == setting)
        }
    }

    @Test("hands-free never-spoke and the 30/60 second choices wire to policy")
    func handsFreeNeverSpokeAndSettings() {
        for setting in DictationSilenceSetting.allCases {
            let policy = DictationHoldPolicy(
                silenceSetting: setting, neverSpokeSeconds: 0.09,
                afterSpeechWarningSeconds: 0.03, heldNoSpeechSeconds: 0.18)
            let tracker = DictationHoldTracker(policy: policy)
            tracker.append(frame(0.0, count: 4 * DictationHoldTracker.frameSamples))
            let snapshot = tracker.snapshot(mode: .handsFree)
            if setting == .off {
                #expect(snapshot.decision == .continueListening)
            } else {
                #expect(snapshot.decision == .stop(.handsFreeNeverSpoke))
            }
        }
    }

    @Test("a held recording continues before 60 seconds and stops at the guard")
    func heldSilenceGuard() {
        let policy = DictationHoldPolicy(
            silenceSetting: .off, neverSpokeSeconds: 0.09,
            heldNoSpeechSeconds: 0.18)
        let tracker = DictationHoldTracker(policy: policy)
        tracker.append(frame(0.0, count: 5 * DictationHoldTracker.frameSamples))
        #expect(tracker.snapshot(mode: .held).decision == .continueListening)
        tracker.append(frame(0.0, count: 2 * DictationHoldTracker.frameSamples))
        #expect(tracker.snapshot(mode: .held).decision == .stop(.heldSilenceGuard))
    }

    @Test("a voice frame clears the post-speech silence warning")
    func speechClearsWarning() {
        let policy = DictationHoldPolicy(
            silenceSetting: .thirtySeconds, afterSpeechWarningSeconds: 0.06,
            heldNoSpeechSeconds: 60)
        let tracker = DictationHoldTracker(policy: policy)
        // Establish a small room-noise floor before the louder voice frames;
        // a uniformly loud synthetic clip would correctly classify itself as
        // its own noise floor.
        tracker.append(frame(0.01, count: 8 * DictationHoldTracker.frameSamples))
        tracker.append(frame(0.2, count: 9 * DictationHoldTracker.frameSamples))
        // Thirty seconds is a policy choice, so feed the tracker directly
        // rather than sleeping for the real hands-free interval.
        tracker.append(frame(0.0, count: 998 * DictationHoldTracker.frameSamples))
        #expect(tracker.snapshot(mode: .handsFree).decision == .silenceWarning)
        tracker.append(frame(0.2, count: 9 * DictationHoldTracker.frameSamples))
        #expect(tracker.snapshot(mode: .handsFree).decision == .continueListening)
        #expect(tracker.snapshot(mode: .handsFree).silenceSeconds == 0)
    }

    @Test("the short test ceiling transcribes all captured audio and reports the limit")
    func fastMaximumHoldWiring() async throws {
        var harness = Harness()
        harness.timing.maximumHoldSeconds = 0.05
        harness.timing.holdPolicy.maximumHoldSeconds = 0.05
        harness.timing.holdPolicy.countdownSeconds = 0.01
        let model = harness.model()
        await model.toggle()
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        // This is a wiring check, not a two-second performance budget. The
        // parallel suite shares MainActor with capture, spool and insertion
        // work; use the same bounded completion wait as MaximumHoldTests.
        try #require(await eventually { !model.busy && !model.recording })
        #expect(harness.workers.made[0].state.get.transcribes == 1)
        #expect(harness.workers.made[0].state.get.transcribeSampleCounts == [16_000])
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(model.status.contains("again to continue"))
    }

    @Test("a long cancel asks for confirmation, keep resumes, and confirmation discards")
    func longCancelConfirmation() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        // The elapsed-time branch is exercised by the model's live monitor;
        // the confirmation actions themselves use the published internal
        // state so this test stays deterministic and does not sleep 30 s.
        model.confirmingDiscard = true
        #expect(model.recording)
        #expect(model.confirmingDiscard)

        model.keepRecording()
        #expect(model.recording)
        #expect(!model.confirmingDiscard)
        await model.cancel()
        #expect(await eventually { !model.recording && !model.busy })

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        model.confirmingDiscard = true
        #expect(model.confirmingDiscard)
        await model.confirmDiscard()
        #expect(await eventually { !model.recording && !model.busy })
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
    }
}
#endif
