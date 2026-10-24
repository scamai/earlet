// A wedged worker must not leave the app busy forever. Enable and transcribe
// have deadlines; at a deadline the worker is killed from outside its actor
// and a fresh transport is built.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@Suite("DictationDeadline bounds work that ignores cancellation")
struct DictationDeadlineTests {

    @Test("work that finishes first returns its value and never times out")
    func fastWorkWins() async {
        let timeouts = Locked(0)
        let value = await DictationDeadline.run(
            seconds: 5, work: { 42 }, onAbandon: { timeouts.mutate { $0 += 1 } })
        #expect(value == 42)
        try? await Task.sleep(for: .milliseconds(50))
        #expect(timeouts.get == 0)
    }

    @Test("work parked on a continuation that ignores cancellation is cut off at the bound",
          .timeLimit(.minutes(1)))
    func uncancellableWorkIsBounded() async {
        // The stand-in for a pipe read blocked in the IPC actor: nothing but
        // an outside action resumes it. A task group would wait forever.
        let parked = Locked<CheckedContinuation<Int, Never>?>(nil)
        let started = Date()
        let value = await DictationDeadline.run(
            seconds: 0.2,
            work: { await withCheckedContinuation { c in parked.mutate { $0 = c } } })
        #expect(value == nil)
        #expect(Date().timeIntervalSince(started) < 5)
        parked.mutate { $0?.resume(returning: 0); $0 = nil }
    }

    @Test("an abandon that makes the work return at once still reports the deadline",
          .timeLimit(.minutes(1)))
    func killDoesNotRaceTheResult() async {
        // The kill in `onAbandon` releases the parked work immediately, the
        // way a killed worker's pipe read returns EOF at once, and the work
        // then gets to run before the kill call has returned.
        for _ in 0..<5 {
            let parked = Locked<CheckedContinuation<Int, Never>?>(nil)
            let value = await DictationDeadline.run(
                seconds: 0.02,
                work: { await withCheckedContinuation { c in parked.mutate { $0 = c } } },
                onAbandon: {
                    parked.mutate { $0?.resume(returning: -1); $0 = nil }
                    usleep(50_000)
                })
            #expect(value == nil)
        }
    }

    @Test("canAbandon returning false keeps waiting for the work")
    func timeoutCanDefer() async {
        let value = await DictationDeadline.run(
            seconds: 0.1,
            work: { try? await Task.sleep(for: .milliseconds(400)); return 7 },
            canAbandon: { false })
        #expect(value == 7)
    }

    @Test("the gate lets exactly one of paste and abandon win")
    func gateIsExclusive() {
        let pasted = InsertionGate()
        #expect(pasted.beginInsertion())
        #expect(!pasted.abandon())
        let abandoned = InsertionGate()
        #expect(abandoned.abandon())
        #expect(!abandoned.beginInsertion())
    }
}

@MainActor
@Suite("A wedged worker is killed, rebuilt and never leaves the app busy")
struct DictationModelDeadlineTests {

    @Test("a transcription deadline retries once with the same recording", .timeLimit(.minutes(1)))
    func wedgedTranscribe() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        let first = harness.workers.made[0]
        first.state.mutate { $0.replies = [.hang] }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()

        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(first.state.get.kills >= 1, "the wedged worker is killed from outside")
        #expect(await eventually { harness.workers.made.count == 2 && harness.workers.made[1].isReady })
        #expect(first.state.get.transcribeSampleCounts == harness.workers.made[1].state.get.transcribeSampleCounts)
        #expect(harness.inserts.texts.get == ["Hello world"], "the recovered result is inserted once")
    }

    @Test("a model load that never finishes cannot hold Enable hostage", .timeLimit(.minutes(1)))
    func wedgedStart() async {
        let harness = Harness()
        harness.workers.configure = { $0.state.mutate { $0.startReply = .hang } }
        let model = harness.model()
        #expect(await finishes(within: 10) { await model.toggle() })
        #expect(!model.enabled)
        #expect(!model.busy)
        #expect(model.status.contains("took too long"))
        #expect(harness.workers.made[0].state.get.kills == 1)
    }

    @Test("pausing while the worker cannot shut down kills it instead", .timeLimit(.minutes(1)))
    func wedgedShutdown() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.workers.made[0].state.mutate { $0.shutdownHangs = true }
        #expect(await finishes(within: 10) { await model.toggle() })
        #expect(!model.enabled)
        #expect(!model.busy)
        #expect(harness.workers.made[0].state.get.kills == 1)
    }

    @Test("a paste that began before the deadline is allowed to finish", .timeLimit(.minutes(1)))
    func pasteInProgressIsNotAbandoned() async {
        let harness = Harness()
        harness.inserts.duration.mutate { $0 = 0.6 } // longer than the 0.3 s bound
        let model = harness.model()
        await model.toggle()

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.workers.made[0].state.get.kills == 0)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("release stops capture before a retry worker is started", .timeLimit(.minutes(1)))
    func releaseWaitsForARestartingWorker() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.workers.made[0].state.mutate { $0.replies = [.hang] }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { harness.capture.state.get.stops >= 1 })
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(await eventually { harness.workers.made.count == 2 && harness.workers.made[1].isReady })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }
}
#endif
