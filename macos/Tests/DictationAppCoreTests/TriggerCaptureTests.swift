// The press-time capture lifecycle ported from the original controller: a stale
// warm engine is rebound at the trigger, a cold engine is warmed before the
// arm, a failed warm-up is an honest capture failure, and an interrupted
// hold says "mic changed" instead of blaming the user.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("The press rebinds a stale engine and warms a cold one before arming")
struct TriggerCaptureTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("a warm engine with a stale binding is cooled and warmed again at the press")
    func staleWarmEngineIsReboundAtPress() async {
        let harness = Harness()
        let model = await enabled(harness)
        // A warm engine whose device moved since it was opened.
        harness.capture.state.mutate { $0.warm = true; $0.stale = true }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })

        let state = harness.capture.state.get
        #expect(state.coolDowns == 1, "the stale engine was torn down")
        #expect(state.warmUps == 1, "and a fresh one warmed before the arm")
        #expect(state.arms == 1)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.logLines.get.contains { $0.contains("stale") })
    }

    @Test("a warm engine with a healthy binding arms without a rebuild")
    func healthyWarmEngineOnlyArms() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.capture.state.mutate { $0.warm = true }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })

        let state = harness.capture.state.get
        #expect(state.coolDowns == 0 && state.warmUps == 0)
        #expect(state.arms == 1)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a failed warm-up fails the hold as a capture error, not a permission one")
    func warmUpFailureIsACaptureFailure() async {
        struct WarmUpRefused: Error {}
        let harness = Harness()
        harness.capture.state.mutate { $0.warmUpError = WarmUpRefused() }
        let model = await enabled(harness)

        harness.trigger.press()
        #expect(await eventually { !model.recording && model.status.contains("no audio") })
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(100))

        #expect(harness.capture.state.get.warmUps == 0, "the warm-up threw before it counted")
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(!model.status.contains("cannot use the microphone"),
                "a device error is not labelled a permission problem")
    }

    @Test("a hold interrupted by the mic watchdog says the microphone changed")
    func interruptedHoldSaysMicChanged() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.capture.state.mutate {
            $0.interruption = DictationCaptureInterruption(reason: "no-chunks", deviceName: "AirPods Pro")
        }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually {
            !model.busy && model.status == "Mic changed to AirPods Pro — could you say that again?"
        })
        #expect(harness.inserts.texts.get.isEmpty, "the discarded hold is never inserted")
        #expect(harness.logLines.get.contains { $0.contains("interrupted") })
    }

    @Test("an interruption that reopened nothing names no device")
    func failedRebindNamesNoDevice() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.capture.state.mutate {
            $0.interruption = DictationCaptureInterruption(reason: "device-gone", deviceName: "")
        }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually {
            !model.busy && model.status == "The microphone changed while you were speaking. Could you say that again?"
        })
        #expect(harness.inserts.texts.get.isEmpty)
    }
}
#endif
