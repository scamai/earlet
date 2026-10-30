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

    // ─── Escape's question on a long take ───

    /// Enabled, with every take "long" from its first moment, and a held
    /// key whose capture is up.
    private func longTake(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        harness.trigger.press()
        #expect(await eventually { model.recording && model.holdStartedAt != nil })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        return model
    }

    private func escape(_ harness: Harness) {
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
    }

    @Test("the question waits 30 s and stands 5 s, as on Windows")
    func escapeQuestionDefaults() {
        let timing = DictationTiming()
        #expect(timing.escapeAsksAfterSeconds == 30)
        #expect(timing.escapeQuestionSeconds == 5)
        #expect(DictationHUDContent.discardQuestion(seconds: 222.7) == "Discard 3:42 of speech?")
        #expect(DictationHUDContent.discardQuestionHint == "Press Esc again to discard. Keep talking to continue.")
    }

    @Test("Escape on a long take asks in the indicator, never the window, and a second Escape discards")
    func escapeAsksInTheIndicator() async {
        var harness = Harness()
        harness.timing.escapeAsksAfterSeconds = 0
        harness.timing.escapeQuestionSeconds = 30
        let model = await longTake(harness)
        let attention = model.windowAttention

        escape(harness)
        #expect(await eventually { model.confirmingDiscard })
        // The take carries on and the window stays where it is: bringing
        // it forward took the keyboard from the field being dictated into.
        #expect(model.recording)
        #expect(model.windowAttention == attention)
        #expect(model.discardQuestion?.hasPrefix("Discard 0:") == true)
        #expect(model.discardQuestion?.hasSuffix(" of speech?") == true)
        #expect(model.holdNotice == DictationHUDContent.discardQuestionHint)
        #expect(!model.status.contains("cancelled"))

        escape(harness)
        #expect(await eventually { !model.recording && !model.busy })
        #expect(model.status == "Recording cancelled. Nothing inserted.")
        #expect(model.discardQuestion == nil)
        #expect(model.holdNotice == nil)
        #expect(model.windowAttention == attention)
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
        #expect(harness.workers.made[0].state.get.transcribes == 0)
        harness.trigger.release()
    }

    @Test("Escape's question lapses after its time, and the next Escape asks again")
    func escapeQuestionLapses() async {
        var harness = Harness()
        harness.timing.escapeAsksAfterSeconds = 0
        harness.timing.escapeQuestionSeconds = 0.5
        let model = await longTake(harness)

        escape(harness)
        #expect(await eventually { model.confirmingDiscard })
        #expect(await eventually { !model.confirmingDiscard })
        #expect(model.recording)
        #expect(model.discardQuestion == nil)
        #expect(model.holdNotice == nil)

        // A lapsed question is no answer: this Escape asks again.
        escape(harness)
        #expect(await eventually { model.confirmingDiscard })
        #expect(model.recording)

        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(harness.inserts.texts.get == ["Hello world"], "letting go inserts, question or not")
        #expect(model.discardQuestion == nil)
    }

    @Test("a hands-free take asks too, on the latch's Escape watch")
    func latchedEscapeAsks() async {
        var harness = Harness()
        harness.timing.escapeAsksAfterSeconds = 0
        harness.timing.escapeQuestionSeconds = 30
        let model = harness.model()
        await model.toggle()
        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.holdStartedAt != nil })

        escape(harness)
        #expect(await eventually { model.confirmingDiscard })
        #expect(model.latched && model.recording)

        escape(harness)
        #expect(await eventually { !model.recording && !model.latched && model.status.contains("cancelled") })
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("the window's and the menu's Cancel discard a long take without asking")
    func cancelNeverAsks() async {
        var harness = Harness()
        harness.timing.escapeAsksAfterSeconds = 0
        let model = await longTake(harness)
        let attention = model.windowAttention

        await model.cancel()
        #expect(!model.recording)
        #expect(model.status == "Recording cancelled. Nothing inserted.")
        #expect(model.discardQuestion == nil)
        #expect(model.windowAttention == attention)
        #expect(await eventually { !model.busy })
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
        harness.trigger.release()
    }

    @Test("Cancel answers a standing question too")
    func cancelAnswersTheQuestion() async {
        var harness = Harness()
        harness.timing.escapeAsksAfterSeconds = 0
        harness.timing.escapeQuestionSeconds = 30
        let model = await longTake(harness)

        escape(harness)
        #expect(await eventually { model.confirmingDiscard })
        await model.cancel()
        #expect(!model.recording)
        #expect(model.discardQuestion == nil)
        #expect(await eventually { !model.busy })
        harness.trigger.release()
    }

    @Test("a short take is cancelled by its first Escape")
    func shortTakeEscapeCancels() async {
        let harness = Harness()
        let model = await longTake(harness)

        escape(harness)
        #expect(await eventually { !model.recording && !model.busy })
        #expect(model.status == "Recording cancelled. Nothing inserted.")
        #expect(model.discardQuestion == nil)
        harness.trigger.release()
    }
}
#endif
