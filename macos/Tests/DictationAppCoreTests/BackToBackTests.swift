// Dictating in chunks: release, then press again at once. A press that
// lands while the last dictation is still being transcribed or typed is not
// recorded (queueing it is a later design), but it is refused out loud, and
// the dictation already on its way is never lost.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("A press while the last dictation finishes is refused visibly")
struct BackToBackTests {

    private func enabled(_ harness: inout Harness) async -> DictationModel {
        harness.timing.transcribeSeconds = 30
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("a press during the transcription keeps the first dictation and shows the refusal",
          .timeLimit(.minutes(1)))
    func pressDuringTranscription() async {
        var harness = Harness()
        let model = await enabled(&harness)
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }
        let recorder = HUDStateRecorder(model)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { model.transcribing && worker.state.get.transcribes == 1 })

        harness.trigger.press()
        #expect(model.status == DictationCopy.stillFinishing(holdKey: .standard))
        #expect(model.hudOutcome?.title == "Still finishing the last one")
        #expect(recorder.last == .outcome(DictationHUDContent.outcome(for: .stillFinishing,
                                                                      holdKeyName: "Right Command")),
                "shown over Transcribing, to the user who is holding the key")
        #expect(!model.recording, "the second press is not recorded")
        #expect(harness.logLines.get.contains { $0.contains("press refused") })
        harness.trigger.release()

        worker.finishTranscribe("hello world")
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"], "the first dictation still landed")
        #expect(model.hudOutcome?.title == "Inserted")
        #expect(worker.state.get.transcribes == 1)

        // The press after that is an ordinary dictation.
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && harness.inserts.texts.get.count == 2 })
    }

    @Test("a press during the paste is refused the same way", .timeLimit(.minutes(1)))
    func pressDuringThePaste() async {
        var harness = Harness()
        harness.inserts.duration.mutate { $0 = 0.5 }
        let model = await enabled(&harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { harness.inserts.texts.get.count == 1 })
        harness.trigger.press()
        #expect(model.hudOutcome?.title == "Still finishing the last one")
        #expect(!model.recording)
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("an idle press is never refused")
    func idlePressIsNotRefused() async {
        var harness = Harness()
        let model = await enabled(&harness)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(model.hudOutcome == nil)
        #expect(!harness.logLines.get.contains { $0.contains("press refused") })
        harness.trigger.release()
        #expect(await eventually { !model.busy })
    }
}
#endif
