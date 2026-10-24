// The hold health monitor must recover a dead worker without losing the
// capture, and must be cancelled as soon as the hold ends.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Dictation hold worker health", .serialized)
struct DictationHoldHealthTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func fastHealth(_ harness: inout Harness) {
        harness.timing.workerHealthAfterSeconds = 0.02
        harness.timing.workerHealthIntervalSeconds = 0.02
        harness.timing.workerHealthReplySeconds = 0.02
    }

    @Test("a worker crash during a hold is replaced before release and inserts once",
          .timeLimit(.minutes(1)))
    func crashDuringHoldKeepsCapture() async throws {
        var harness = Harness()
        fastHealth(&harness)
        let model = await enabled(harness)
        let original = harness.workers.made[0]

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        // Preview preparation may still start the shared worker after
        // recording is published. Inject the crash only after it is open,
        // so startup cannot revive this fake before the health probe sees it.
        try #require(await eventually {
            model.holdHealth != nil && model.workerStart == nil
                && original.state.get.previewStarts == 1 && original.isReady
        })
        original.crash()

        #expect(await eventually(15) {
            harness.logLines.get.contains { $0.contains("worker health check failed") }
        })
        #expect(await eventually(15) { harness.workers.made.count >= 2 })
        #expect(await eventually(15) { harness.workers.made.dropFirst().contains(where: { $0.isReady }) })
        #expect(model.recording, "health recovery does not end the live capture")
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a worker still starting past the health delay is not killed or replaced",
          .timeLimit(.minutes(1)))
    func parkedInitialStartIsNotHealthFailure() async {
        var harness = Harness()
        fastHealth(&harness)
        harness.timing.startSeconds = 0.4
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        worker.state.mutate {
            $0.initialized = false
            $0.alive = false
            $0.startReply = .hang
        }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(await eventually { !worker.state.get.parkedStarts.isEmpty })
        #expect(!(await eventually(0.15) { worker.state.get.kills > 0 }))
        #expect(harness.workers.made.count == 1)

        worker.finishStart(true)
        harness.trigger.release()
        #expect(await eventually(5) { !model.busy && model.status.hasPrefix("Text inserted") })
    }

    @Test("two failed health restarts end the hold and preserve a recovery take",
          .timeLimit(.minutes(1)))
    func failedRestartsKeepTake() async throws {
        var harness = Harness()
        fastHealth(&harness)
        let model = await enabled(harness)
        let factory = harness.workers
        harness.workers.configure = { worker in
            if !factory.made.isEmpty {
                worker.state.mutate { $0.startReply = .fail }
            }
        }
        let original = factory.made[0]
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        try #require(await eventually {
            model.holdHealth != nil && model.workerStart == nil
                && original.state.get.previewStarts == 1 && original.isReady
        })
        original.crash()

        #expect(await eventually { !model.recording && !model.busy })
        #expect(factory.made.count >= 3, "the health monitor made two replacement attempts before release")
        #expect(model.status.contains("could not start") || model.status.contains("saved"))
        #expect(!harness.takeSpoolFiles().isEmpty, "the final readiness failure keeps the captured take")
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("release before the health delay cancels the monitor",
          .timeLimit(.minutes(1)))
    func releaseCancelsPendingHealth() async {
        var harness = Harness()
        harness.timing.workerHealthAfterSeconds = 0.2
        harness.timing.workerHealthIntervalSeconds = 0.02
        harness.timing.workerHealthReplySeconds = 0.02
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4_000))
        harness.trigger.release()
        #expect(await eventually { !model.busy })
        #expect(harness.workers.made.count == 1)
        #expect(worker.state.get.starts == 1)
    }

    @Test("quit before the health delay does not restart the old worker",
          .timeLimit(.minutes(1)))
    func quitCancelsPendingHealth() async {
        var harness = Harness()
        harness.timing.workerHealthAfterSeconds = 0.2
        harness.timing.workerHealthIntervalSeconds = 0.02
        harness.timing.workerHealthReplySeconds = 0.02
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 4_000))
        await model.prepareForQuit()
        #expect(harness.workers.made.count == 1)
        #expect(worker.state.get.starts == 1)
    }
}
#endif
