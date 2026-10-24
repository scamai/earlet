// A hold has a ceiling (`DictationTiming.maximumHoldSeconds`, 120 s in the
// product). At the ceiling the words already spoken are transcribed and
// inserted without waiting for the release, and the late release does
// nothing.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("A hold that reaches the maximum finishes on its own")
struct MaximumHoldTests {

    @Test("the product's ceiling is ten minutes")
    func productCeiling() {
        #expect(DictationTiming().maximumHoldSeconds == 600)
    }

    @Test("at the ceiling the recording is transcribed and inserted without a release",
          .timeLimit(.minutes(1)))
    func ceilingInserts() async {
        var harness = Harness()
        harness.timing.maximumHoldSeconds = 0.3
        let model = harness.model()
        await model.toggle()

        harness.trigger.press()
        #expect(await eventually { model.recording })
        // The key stays down.
        #expect(await eventually { !model.recording && !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(!harness.capture.state.get.retaining, "the microphone stopped keeping audio at the ceiling")

        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(200))
        #expect(harness.inserts.texts.get.count == 1, "the late release inserts nothing more")
        #expect(harness.workers.made[0].state.get.transcribes == 1)
    }

    @Test("the ceiling says so, in the status line and on the HUD", .timeLimit(.minutes(1)))
    func ceilingSaysSo() async {
        var harness = Harness()
        harness.timing.maximumHoldSeconds = 0.3
        let model = harness.model()
        model.hudOutcomeSecondsOverride = 5
        await model.toggle()
        let history = StatusHistory(model)

        harness.trigger.press()
        #expect(await eventually { !model.recording && !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(history.contains(DictationCopy.maximumHoldReached),
                "while it transcribes: two minutes is the longest recording")
        #expect(model.status == DictationCopy.insertedAfterMaximumHold(holdKey: .standard))
        #expect(model.status == "Text inserted. Hold Right Command again to continue.")
        #expect(model.hudOutcome == DictationHUDContent.outcome(
            for: .stoppedAtLimit(seconds: 0.3), holdKeyName: "Right Command"))
        harness.trigger.release()
    }

    @Test("the hold's clock is published while it records, and only then", .timeLimit(.minutes(1)))
    func holdClock() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.holdStartedAt == nil)
        #expect(model.maximumHoldSeconds == harness.timing.maximumHoldSeconds)

        harness.trigger.press()
        #expect(await eventually { model.holdStartedAt != nil })
        let started = model.holdStartedAt ?? .distantPast
        #expect(abs(started.timeIntervalSinceNow) < 2)
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(model.holdStartedAt == nil)
    }

    @Test("a release before the ceiling ends the timer with the hold", .timeLimit(.minutes(1)))
    func releaseBeforeTheCeiling() async {
        var harness = Harness()
        harness.timing.maximumHoldSeconds = 0.4
        let model = harness.model()
        await model.toggle()

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        try? await Task.sleep(for: .milliseconds(600))
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.transcribes == 1)
        #expect(model.status == "Text inserted. Hold Right Command for your next sentence.",
                "an ordinary release never claims the ceiling")
    }
}
#endif
