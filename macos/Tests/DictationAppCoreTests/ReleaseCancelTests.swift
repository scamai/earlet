// Escape and Cancel still work after the release, while the words wait for
// the engine or are being transcribed: nothing is inserted, even when the
// text arrives later. Once the paste has begun they are refused.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Escape and Cancel work while the words are on their way")
struct ReleaseCancelTests {

    /// Enabled, by default with a transcription bound long enough that only
    /// a cancel can end the wait.
    private func enabledModel(_ harness: inout Harness,
                              transcribeSeconds: Double = 30) async -> DictationModel {
        harness.timing.transcribeSeconds = transcribeSeconds
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    /// One hold whose transcription is parked in the worker.
    private func releaseIntoAHang(_ harness: Harness, _ model: DictationModel) async -> FakeWorker {
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { worker.state.get.transcribes == 1 })
        return worker
    }

    @Test("Escape during the transcription inserts nothing, even when the text arrives later",
          .timeLimit(.minutes(1)))
    func escapeDuringTranscription() async {
        var harness = Harness()
        let model = await enabledModel(&harness)
        let worker = await releaseIntoAHang(harness, model)
        #expect(model.transcribing && model.canCancel)
        #expect(harness.keyWatch.active, "Escape is watched while the words are transcribed")

        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && !model.transcribing && model.status.contains("cancelled") })
        #expect(!harness.keyWatch.active)
        #expect(!model.canCancel)

        worker.finishTranscribe("late words")
        try? await Task.sleep(for: .milliseconds(200))
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.clipboard.get.isEmpty, "a cancel is not a refused paste")
        #expect(model.recentResult.entry == nil, "late cancelled text is never recoverable")
        #expect(!model.hasCompletedFirstDictation)
        #expect(harness.logLines.get.filter { $0.contains("event=finished") }.count == 1)
        #expect(harness.logLines.get.contains { $0.contains("event=finished result=cancelled") })
        #expect(model.status.contains("cancelled"))
        #expect(worker.state.get.kills == 0, "a cancel does not cost the next dictation a model load")

        // The next hold is untouched by the cancelled one.
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("the Cancel button cancels a transcription too", .timeLimit(.minutes(1)))
    func cancelButtonDuringTranscription() async {
        var harness = Harness()
        let model = await enabledModel(&harness)
        let worker = await releaseIntoAHang(harness, model)

        await model.cancel()
        #expect(await eventually { !model.busy && model.status.contains("cancelled") })
        worker.finishTranscribe("late words")
        try? await Task.sleep(for: .milliseconds(200))
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("another key while the words are transcribed is not a cancel", .timeLimit(.minutes(1)))
    func otherKeysDoNotCancel() async {
        var harness = Harness()
        let model = await enabledModel(&harness)
        let worker = await releaseIntoAHang(harness, model)

        harness.keyWatch.type(8) // kVK_ANSI_C: typing elsewhere, not a shortcut
        try? await Task.sleep(for: .milliseconds(100))
        #expect(model.transcribing)
        worker.finishTranscribe("hello world")
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("once the paste has begun, Escape is too late and the text lands", .timeLimit(.minutes(1)))
    func escapeAfterThePasteBegan() async {
        var harness = Harness()
        harness.inserts.duration.mutate { $0 = 0.5 }
        let model = await enabledModel(&harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        // The recorder notes the text as the paste begins.
        #expect(await eventually { harness.inserts.texts.get.count == 1 })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        await model.cancel()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("Escape while the engine is still starting drops the words and stops the capture",
          .timeLimit(.minutes(1)))
    func escapeWhileTheEngineStarts() async {
        var harness = Harness()
        harness.timing.startSeconds = 30
        let model = await enabledModel(&harness)
        let worker = harness.workers.made[0]
        // Died between holds, and its restart is slow.
        worker.state.mutate { $0.initialized = false; $0.alive = false; $0.startReply = .hang }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { model.status.contains("Starting the speech engine") })
        #expect(model.canCancel)
        let stopsBefore = harness.capture.state.get.stops

        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && model.status.contains("cancelled") })
        #expect(harness.capture.state.get.stops == stopsBefore + 1)
        #expect(!harness.capture.state.get.retaining, "the microphone no longer keeps these words")

        worker.finishStart(true)
        try? await Task.sleep(for: .milliseconds(200))
        #expect(worker.state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("a cancelled transcription that never returns is still killed at its deadline",
          .timeLimit(.minutes(1)))
    func aCancelledWedgeIsStillKilled() async {
        var harness = Harness()
        let model = await enabledModel(&harness, transcribeSeconds: 1)
        let worker = await releaseIntoAHang(harness, model)
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && model.status.contains("cancelled") })
        #expect(worker.state.get.kills == 0)
        // The next hold would queue behind the wedge; the deadline frees it.
        #expect(await eventually(10) { worker.state.get.kills == 1 })
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("Cancel is offered while recording and while transcribing, and not otherwise",
          .timeLimit(.minutes(1)))
    func cancelIsOfferedOnlyWhenThereIsSomethingToCancel() async {
        var harness = Harness()
        let model = await enabledModel(&harness)
        #expect(!model.menuItems.contains(.cancel))

        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(model.menuItems.contains(.cancel))
        harness.trigger.release()
        #expect(await eventually { worker.state.get.transcribes == 1 })
        #expect(!model.recording && model.transcribing)
        #expect(model.menuItems.contains(.cancel))

        worker.finishTranscribe("hello world")
        #expect(await eventually { !model.busy && !model.transcribing })
        #expect(!model.menuItems.contains(.cancel))
    }
}

@MainActor
@Suite("The transcription bound grows with the audio")
struct TranscribeDeadlineTests {

    @Test("two minutes of audio get the fixed 30 s plus their own length")
    func productBound() {
        let timing = DictationTiming()
        #expect(timing.transcribeDeadline(audioSeconds: 0) == 30)
        #expect(timing.transcribeDeadline(audioSeconds: 120) == 150)
        #expect(timing.transcribeDeadline(audioSeconds: -1) == 30, "a clock step never shrinks it")
    }

    @Test("a decode slower than the fixed bound still inserts when the clip is long enough",
          .timeLimit(.minutes(1)))
    func longClipGetsLongerBound() async {
        // The fixed part is 0.3 s here; a 1 s hold makes the bound ~1.3 s,
        // and the decode takes 0.8 s.
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.delayed("hello world", seconds: 0.8)] }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        try? await Task.sleep(for: .seconds(1))
        harness.trigger.release()
        // An outcome is cleared when a hold is admitted: one now means this
        // release has ended, inserted or not.
        #expect(await eventually { !model.busy && model.hudOutcome != nil }, "\(model.status)")
        #expect(model.status.hasPrefix("Text inserted"), "\(model.status)")
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(worker.state.get.kills == 0, "the worker was not killed mid-decode")
    }

    @Test("a short clip retries once, then keeps a second failure", .timeLimit(.minutes(1)))
    func shortClipKeepsTheBound() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.delayed("hello world", seconds: 3)] }
        harness.workers.configure = { replacement in
            if replacement.name == "worker2" {
                replacement.state.mutate { $0.replies = [.hang] }
            }
        }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.contains("restarted") }, "\(model.status)")
        #expect(worker.state.get.kills >= 1)
        #expect(await eventually { harness.workers.made.count >= 2 })
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(worker.state.get.transcribes == 1)
        #expect(harness.workers.made[1].state.get.transcribes == 1)
        #expect(harness.workers.made.dropFirst().reduce(0) { $0 + $1.state.get.transcribes } == 1,
                "a second ASR attempt is the retry limit")
    }
}

@Suite("The insertion gate after a cancel")
struct InsertionGateCancelTests {

    @Test("abandoning twice still says yes, so a deadline after a cancel can act")
    func abandonIsIdempotent() {
        let gate = InsertionGate()
        #expect(gate.abandon())
        #expect(gate.abandon())
        #expect(!gate.beginInsertion())
    }

    @Test("a cancel is refused once the paste has begun")
    func cancelAfterThePasteIsRefused() {
        let release = ReleaseCancel(gate: InsertionGate())
        #expect(release.gate.beginInsertion())
        #expect(!release.cancel())
    }

    @Test("a wait that begins after the cancel ends at once", .timeLimit(.minutes(1)))
    func waitAfterCancelReturnsNil() async {
        let release = ReleaseCancel(gate: InsertionGate())
        #expect(release.cancel())
        #expect(!release.cancel(), "only the first cancel reports that it cancelled")
        let value = await release.unlessCancelled { () -> Int in
            try? await Task.sleep(for: .seconds(30))
            return 1
        }
        #expect(value == nil)
    }
}
#endif
