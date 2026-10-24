// After the worker dies, dictation comes back on its own and says so; an
// idle worker gives its model back and is started again by the next press.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("The worker recovers from a crash and is released when idle")
struct WorkerRecoveryTests {

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("a worker that dies mid-transcription retries once on a fresh worker",
          .timeLimit(.minutes(1)))
    func crashRestartsTheWorker() async {
        let harness = Harness()
        let model = harness.model()
        let history = StatusHistory(model)
        await model.toggle()
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.failure(.ioError("eof"))] }

        await dictate(harness, model)
        #expect(history.contains("Retrying your recording"))
        #expect(await eventually { harness.workers.made.count == 2 && harness.workers.made[1].isReady })
        #expect(worker.state.get.transcribes == 1)
        #expect(harness.workers.made[1].state.get.transcribes == 1)
        #expect(worker.state.get.transcribeSampleCounts == harness.workers.made[1].state.get.transcribeSampleCounts)
        #expect(model.enabled)

        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a worker that died between holds is started before the transcription, keeping the words",
          .timeLimit(.minutes(1)))
    func idleDeathIsRecoveredAtRelease() async {
        let harness = Harness()
        let model = harness.model()
        let history = StatusHistory(model)
        await model.toggle()
        let worker = harness.workers.made[0]
        worker.crash()

        await dictate(harness, model)
        #expect(history.contains("Starting the speech engine") || worker.state.get.starts == 2)
        #expect(worker.state.get.starts == 2)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(model.status.hasPrefix("Text inserted"))
    }

    @Test("an engine error is not treated as a dead worker")
    func engineErrorDoesNotRestart() async {
        let harness = Harness()
        let model = harness.model()
        let history = StatusHistory(model)
        await model.toggle()
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.failure(.engineError("bad audio"))] }

        await dictate(harness, model)
        try? await Task.sleep(for: .milliseconds(100))
        #expect(worker.state.get.starts == 1)
        #expect(!history.contains("stopped unexpectedly") && !history.contains("restart"),
                "a worker that answered with an error has not stopped")
    }

    @Test("an idle worker is shut down, and the next hold starts a fresh one", .timeLimit(.minutes(1)))
    func idleWorkerIsReleased() async {
        var harness = Harness()
        harness.timing.workerIdleSeconds = 0.2
        harness.timing.housekeepingSeconds = 0.05
        let model = harness.model()
        await model.toggle()
        let first = harness.workers.made[0]

        // The housekeeping timer alone gives it back.
        #expect(await eventually { first.state.get.shutdowns == 1 })
        #expect(!first.isReady)
        #expect(model.enabled)

        await dictate(harness, model)
        #expect(harness.workers.made.count == 2)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a worker in use is not released")
    func busyWorkerIsKept() async {
        var harness = Harness()
        harness.timing.workerIdleSeconds = 60
        let model = harness.model()
        await model.toggle()
        await model.housekeepingTick()
        #expect(harness.workers.made[0].state.get.shutdowns == 0)
    }
}
#endif
