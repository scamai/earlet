// Regression coverage for capture paths that contain no usable audio.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Zero input cools the capture before the next hold")
struct ZeroInputTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("empty or exact-zero PCM is reported as no audio, cooled, and the next hold rewarms", arguments: [false, true])
    func exactZeroPcmCoolsAndNextHoldRewarms(empty: Bool) async {
        let harness = Harness()
        harness.capture.samples.mutate { $0 = [Float](repeating: 0, count: empty ? 0 : 16_000) }
        let model = await enabled(harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })

        var state = harness.capture.state.get
        #expect(model.status.contains("microphone delivered no audio"))
        #expect(state.coolDowns == 1)
        #expect(state.warmUps == 1)
        #expect(harness.workers.made.last?.state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)

        harness.capture.samples.mutate { $0 = [Float](repeating: 0.2, count: 16_000) }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })

        state = harness.capture.state.get
        #expect(state.warmUps == 2)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("ordinary quiet nonzero PCM remains the nothing-heard path and stays warm")
    func quietNonzeroPcmStaysWarm() async {
        let harness = Harness()
        harness.capture.samples.mutate { $0 = [Float](repeating: 0.0001, count: 16_000) }
        let model = await enabled(harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })

        let state = harness.capture.state.get
        #expect(model.status.contains("Nothing was heard"))
        #expect(state.coolDowns == 0)
        #expect(state.warmUps == 1)
        #expect(harness.workers.made.last?.state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("a watchdog-discarded capture keeps its recovered engine warm")
    func interruptedCaptureDoesNotCoolDownAgain() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.state.mutate {
            $0.interruption = DictationCaptureInterruption(reason: "no-chunks", deviceName: "Headset")
        }
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(model.status.contains("Mic changed to Headset"))
        #expect(harness.capture.state.get.coolDowns == 0)
        #expect(harness.capture.state.get.warm)
        #expect(harness.inserts.texts.get.isEmpty)
    }
}
#endif
