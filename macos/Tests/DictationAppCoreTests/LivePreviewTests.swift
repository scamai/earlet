// The live streaming preview: starts when capture is up, feeds the HUD's
// title line, and is always cancelled before the authoritative decode or
// with any cancel path. The caption windowing itself is covered by
// DictationLivePreviewTests (byte-identical session file) — these tests
// pin the model's wiring.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("The live preview streams while a hold records", .serialized)
struct LivePreviewTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func pressAndPreview(_ harness: Harness, _ model: DictationModel) async -> FakeWorker {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        let worker = harness.workers.made[0]
        #expect(await eventually { worker.state.get.previewStarts == 1 },
                "the stream opens once capture is up")
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4000))
        #expect(await eventually { model.livePreviewText == "hello world from preview" })
        return worker
    }

    @Test("a hold streams a windowed caption to the HUD, cleared at the end")
    func holdStreamsPreviewText() async {
        let harness = Harness()
        let model = await enabled(harness)
        let worker = await pressAndPreview(harness, model)
        #expect(worker.state.get.previewPushes >= 1)

        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(model.livePreviewText == "")
        #expect(harness.inserts.texts.get == ["Hello world"],
                "the preview is never inserted; the authoritative decode is")
    }

    @Test("the preview stream is cancelled before the authoritative decode")
    func stopPrecedesTriggerUp() async {
        let harness = Harness()
        let model = await enabled(harness)
        let worker = await pressAndPreview(harness, model)

        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        // The worker pipe is one request, one reply: the cancel must land
        // before the final transcribe on the same worker.
        #expect(worker.state.get.events == ["preview-cancel", "transcribe"])
    }

    @Test("Escape stops the preview and clears the caption")
    func cancelStopsThePreview() async {
        let harness = Harness()
        let model = await enabled(harness)
        let worker = await pressAndPreview(harness, model)

        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && !model.busy && model.status.contains("cancelled") })
        #expect(worker.state.get.previewCancels == 1)
        #expect(model.livePreviewText == "")
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("a microphone fault ends the take and a late rebind cannot restart its preview")
    func interruptionResetsThePreview() async {
        let harness = Harness()
        let model = await enabled(harness)
        let worker = await pressAndPreview(harness, model)

        harness.capture.emitInterruptionDiscardedAudio()
        #expect(await eventually { worker.state.get.previewCancels == 1 })
        #expect(await eventually { model.livePreviewText == "" })
        harness.capture.emitInterruptionRebound()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(worker.state.get.previewStarts == 1)
        #expect(harness.inserts.texts.get == ["Hello world"])
        harness.trigger.release()
        harness.trigger.press()
        #expect(await eventually { model.recording && worker.state.get.previewStarts == 2 },
                "only a new hold starts another preview")
        await model.cancel()
    }

    @Test("Escape while the engine starts ends the preview run, so the next hold gets one",
          .timeLimit(.minutes(1)))
    func escapeDuringEngineStartFreesThePreview() async {
        var harness = Harness()
        harness.timing.startSeconds = 30
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        // Died between holds, and its restart is slow: the first hold's
        // preview run opens no stream while the worker loads.
        worker.state.mutate { $0.initialized = false; $0.alive = false; $0.startReply = .hang }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { model.status.contains("Starting the speech engine") })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && model.status.contains("cancelled") })
        #expect(worker.state.get.previewStarts == 0, "no stream while the worker loaded")

        worker.finishStart(true)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(await eventually { worker.state.get.previewStarts == 1 }, "the next hold opens its stream")
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4000))
        #expect(await eventually { model.livePreviewText == "hello world from preview" })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
    }

    @Test("a hold waits for the shared worker startup before opening preview")
    func previewWaitsForWorkerStartupDuringSameHold() async {
        var harness = Harness()
        harness.timing.startSeconds = 30
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        let startsBeforeRestart = worker.state.get.starts
        worker.state.mutate { $0.initialized = false; $0.alive = false; $0.startReply = .hang }

        harness.trigger.press()
        #expect(await eventually { model.holdStartedAt != nil && !worker.state.get.parkedStarts.isEmpty })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4000))
        #expect(worker.state.get.previewStarts == 0)

        worker.finishStart(true)
        #expect(await eventually { worker.state.get.previewStarts == 1 })
        #expect(await eventually { model.livePreviewText == "hello world from preview" })
        #expect(worker.state.get.starts == startsBeforeRestart + 1,
                "preview and final decode share one startup")

        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(worker.state.get.starts == startsBeforeRestart + 1)
    }

    @Test("stopping while preview waits prevents a late stream open")
    func stopBeforeWorkerStartupPreventsLateOpen() async {
        var harness = Harness()
        harness.timing.startSeconds = 30
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.initialized = false; $0.alive = false; $0.startReply = .hang }

        harness.trigger.press()
        #expect(await eventually { model.holdStartedAt != nil && !worker.state.get.parkedStarts.isEmpty })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4000))
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && model.status.contains("cancelled") })

        worker.finishStart(true)
        try? await Task.sleep(for: .milliseconds(100))
        #expect(worker.state.get.previewStarts == 0)
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("an engine that could not start ends the preview run too", .timeLimit(.minutes(1)))
    func failedEngineStartFreesThePreview() async {
        let harness = Harness()
        let model = await enabled(harness)
        // The worker dies; every replacement fails to start until the test
        // lets one succeed.
        harness.workers.made[0].state.mutate { $0.initialized = false; $0.alive = false; $0.startReply = .fail }
        harness.workers.configure = { $0.state.mutate { $0.startReply = .fail } }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.contains("could not start") })

        harness.workers.configure = { _ in }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(await eventually { harness.workers.made.last?.state.get.previewStarts == 1 },
                "the next hold opens its stream")
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
    }

    @Test("release before engine readiness does not open a preview after recording ends")
    func releaseBeforeReadySkipsLatePreview() async {
        var harness = Harness()
        harness.timing.startSeconds = 30
        harness.timing.previewStopSeconds = 0.05
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.initialized = false; $0.alive = false; $0.startReply = .hang }
        harness.trigger.press()
        #expect(await eventually { model.holdStartedAt != nil && !worker.state.get.parkedStarts.isEmpty })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4000))
        harness.trigger.release()
        #expect(await eventually { model.status.contains("Starting the speech engine") })
        worker.finishStart(true)
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(worker.state.get.previewStarts == 0)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("quit cancels the preview and clears its caption before shutting down the worker")
    func quitStopsPreview() async {
        let harness = Harness()
        let model = await enabled(harness)
        let worker = await pressAndPreview(harness, model)
        await model.prepareForQuit()
        #expect(worker.state.get.previewCancels == 1)
        #expect(worker.state.get.shutdowns == 1)
        #expect(model.livePreviewText.isEmpty)
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("a preview cancel stuck in the worker holds up neither a release nor Escape",
          .timeLimit(.minutes(1)))
    func stuckPreviewStopIsBounded() async {
        var harness = Harness()
        harness.timing.previewStopSeconds = 0.2
        harness.timing.transcribeSeconds = 5
        let model = await enabled(harness)
        let worker = await pressAndPreview(harness, model)
        worker.state.mutate { $0.previewCancelHangs = true }

        harness.trigger.release()
        #expect(await eventually(3) { !model.busy && model.status.hasPrefix("Text inserted") }, "\(model.status)")
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(model.livePreviewText == "")

        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(await eventually { worker.state.get.previewStarts == 2 }, "the run was detached anyway")
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually(3) { !model.busy && !model.recording && model.status.contains("cancelled") })
        worker.forceTerminate()
    }

    @Test("DICTATION_PREVIEW=0 opens no stream while the recovery spool still captures")
    func gateOffMeansNoSession() async {
        let harness = Harness()
        let model = harness.model()
        model.livePreviewEnabled = false
        await model.toggle()
        #expect(model.enabled)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4000))
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })

        let worker = harness.workers.made[0]
        #expect(worker.state.get.previewStarts == 0)
        #expect(harness.capture.onRetainedAudio != nil, "the recovery spool still receives audio")
        #expect(model.livePreviewText == "")
        #expect(harness.inserts.texts.get == ["Hello world"],
                "dictation itself is untouched by the gate")
    }
}
#endif
