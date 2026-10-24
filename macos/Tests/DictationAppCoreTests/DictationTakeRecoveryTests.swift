// The model's disk backed take is the last line of defence for a long or
// interrupted Mac dictation. These tests use only a scratch spool and the
// existing fakes; no microphone or owner learning store is touched.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Mac take recovery")
struct DictationTakeRecoveryTests {
    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func admit(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
    }

    private func finish(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    private func emitAudio(_ harness: Harness) {
        // FakeCapture.samples is the authoritative trigger buffer, but does
        // not itself exercise the collector callback that writes the spool.
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
    }

    private func leaveTake(_ harness: Harness) async {
        let spool = DictationTakeSpool(
            directory: harness.spoolFiles.url,
            runFileWork: { work in work() })
        let take = spool.begin(startedAt: Date())
        spool.append(take, samples: [Float](repeating: 0.2, count: 16_000), sampleRate: 16_000)
        await spool.leave(take)
    }

    @Test("enabling and immediately holding does not deadlock")
    func freshEnableAndHold() async {
        let harness = Harness()
        let model = await enabled(harness)
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a successful paste removes the live take")
    func successfulPasteRemovesLiveTake() async {
        let harness = Harness()
        let model = await enabled(harness)
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
    }

    @Test("an ASR engine error keeps the take for recovery")
    func engineErrorKeepsTake() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate {
            $0.replies = [.failure(.engineError("bad audio"))]
        }
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        #expect(model.savedTake != nil)
        #expect(harness.takeSpoolFiles() == [DictationTakeSpool.keptFileName])
    }

    @Test("recovery transcribes, copies, and then deletes the kept take")
    func recoveryCopiesAndDeletes() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate {
            $0.replies = [.failure(.engineError("bad audio"))]
        }
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        harness.workers.made[0].state.mutate { $0.replies = [.text("recovered words")] }

        await model.recoverLastDictation()
        #expect(harness.clipboard.get == ["Recovered words"])
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
    }

    @Test("clipboard failure leaves the take and recent text available")
    func clipboardFailurePreservesRecovery() async {
        let harness = Harness()
        harness.clipboardWorks.mutate { $0 = false }
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate {
            $0.replies = [.failure(.engineError("bad audio"))]
        }
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        harness.workers.made[0].state.mutate { $0.replies = [.text("try again")] }

        await model.recoverLastDictation()
        #expect(model.savedTake != nil)
        #expect(model.recentResult.entry?.text == "Try again")
        #expect(harness.takeSpoolFiles() == [DictationTakeSpool.keptFileName])
    }

    @Test("empty and digital-zero captures never reach ASR", arguments: [false, true])
    func quietCaptureDoesNotTranscribe(digitalZero: Bool) async {
        let harness = Harness()
        harness.capture.samples.mutate {
            $0 = digitalZero ? [Float](repeating: 0, count: 16_000) : []
        }
        let model = await enabled(harness)
        await admit(harness, model)
        if digitalZero { emitAudio(harness) }
        await finish(harness, model)
        #expect(harness.workers.made[0].state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.takeSpoolFiles().isEmpty)
    }

    @Test("a slow recovery keeps its worker alive through housekeeping")
    func delayedRecoveryDoesNotIdleUnloadWorker() async {
        var harness = Harness()
        harness.timing.workerIdleSeconds = 0.05
        harness.timing.housekeepingSeconds = 0.01
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate {
            $0.replies = [.failure(.engineError("bad audio"))]
        }
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        harness.workers.made[0].state.mutate {
            $0.replies = [.delayed("recovered", seconds: 0.3)]
        }

        let recovery = Task { await model.recoverLastDictation() }
        #expect(await eventually { model.recoveringSavedTake })
        try? await Task.sleep(for: .milliseconds(150))
        #expect(harness.workers.made[0].state.get.shutdowns == 0)
        await recovery.value
        #expect(model.savedTake == nil)
    }

    @Test("a recovery in flight refuses a new hold and quit does not discard it")
    func recoveryInFlightIsExclusive() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate {
            $0.replies = [.failure(.engineError("bad audio"))]
        }
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        harness.workers.made[0].state.mutate { $0.replies = [.hang] }

        let recovery = Task { await model.recoverLastDictation() }
        #expect(await eventually { model.recoveringSavedTake })
        let relaunches = harness.relaunches.get
        harness.trigger.press()
        await model.toggle()
        #expect(model.enabled, "pause is refused while recovery owns the model")
        model.quitAndReopen()
        #expect(harness.relaunches.get == relaunches)
        #expect(model.savedTake != nil)
        #expect(model.recoveringSavedTake)

        harness.workers.made[0].finishTranscribe("recovered")
        _ = await recovery.value
        #expect(await eventually { !model.recoveringSavedTake })
    }

    @Test("quitting a parked decode prevents its late result from being copied")
    func quitDuringParkedDecodeDropsLateResult() async {
        var harness = Harness()
        harness.timing.transcribeSeconds = 30
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate { $0.replies = [.hang] }
        await admit(harness, model)
        emitAudio(harness)
        harness.trigger.release()
        #expect(await eventually { model.transcribing })

        let quitting = Task { await model.prepareForQuit() }
        #expect(await eventually { model.isQuitting })
        harness.workers.made[0].finishTranscribe("late words")
        await quitting.value
        try? await Task.sleep(for: .milliseconds(100))
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.clipboard.get.isEmpty)
        #expect(model.recentResult.entry == nil)
    }

    @Test("a take left before launch is offered for recovery")
    func launchOffersLeftTake() async {
        let harness = Harness()
        await leaveTake(harness)
        let model = harness.model()
        await model.launch()
        #expect(model.savedTake != nil)
        #expect(model.status.contains("recording is saved"))
        #expect(harness.takeSpoolFiles() == [DictationTakeSpool.keptFileName])
    }

    @Test("capture interruption inserts the already spooled words once")
    func interruptionInsertsRetainedWords() async {
        let harness = Harness()
        let model = await enabled(harness)
        await admit(harness, model)
        emitAudio(harness)
        harness.capture.state.mutate {
            $0.interruption = DictationCaptureInterruption(reason: "device-gone", deviceName: "Headset")
        }
        await finish(harness, model)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
    }

    @Test("explicit cancel deletes the live take")
    func cancelDeletesTake() async {
        let harness = Harness()
        let model = await enabled(harness)
        await admit(harness, model)
        emitAudio(harness)
        await model.cancel()
        #expect(await eventually { !model.recording && !model.busy })
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
    }

    @Test("quitting mid capture leaves the take for the next launch")
    func quitMidCaptureLeavesTake() async {
        let harness = Harness()
        let model = await enabled(harness)
        await admit(harness, model)
        emitAudio(harness)
        await model.prepareForQuit()
        #expect(!harness.takeSpoolFiles().isEmpty)

        let relaunched = harness.model()
        await relaunched.launch()
        #expect(relaunched.savedTake != nil)
    }

    @Test("discard failure keeps the saved offer and reports an error")
    func discardFailureKeepsSavedOffer() async throws {
        let harness = Harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate {
            $0.replies = [.failure(.engineError("bad audio"))]
        }
        await admit(harness, model)
        emitAudio(harness)
        await finish(harness, model)
        #expect(model.savedTake != nil)
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: harness.spoolFiles.url.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: harness.spoolFiles.url.path) }

        await model.discardSavedTake()
        #expect(model.savedTake != nil)
        #expect(model.status.contains("could not be deleted"))
        #expect(FileManager.default.fileExists(
            atPath: harness.spoolFiles.url.appendingPathComponent(DictationTakeSpool.keptFileName).path))
    }

    @Test("a paused launch expires a short-lived saved take without loading a worker")
    func pausedLaunchExpiresSavedTake() async {
        var harness = Harness()
        harness.keptFor = 60
        harness.timing.housekeepingSeconds = 0.05
        await leaveTake(harness)
        let model = harness.model()
        await model.launch()
        #expect(model.savedTake != nil)
        #expect(harness.workers.made.isEmpty)
        if var kept = model.savedTake {
            kept.recordedAt = Date().addingTimeInterval(-61)
            model.savedTake = kept
        }
        #expect(await eventually(15) { model.savedTake == nil },
                "the paused app's timer deletes without a manual housekeeping tick")
        #expect(harness.takeSpoolFiles().isEmpty)
        #expect(harness.workers.made.isEmpty)
    }

    @Test("quitting during a delayed release leaves no late insert or recent result")
    func quitDuringDelayedReleaseLeavesTake() async {
        var harness = Harness()
        harness.timing.transcribeSeconds = 30
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate {
            $0.replies = [.delayed("late words", seconds: 0.4)]
        }
        await admit(harness, model)
        emitAudio(harness)
        harness.trigger.release()
        #expect(await eventually { model.transcribing })

        await model.prepareForQuit()
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.clipboard.get.isEmpty)
        #expect(model.recentResult.entry == nil)
        #expect(!harness.takeSpoolFiles().isEmpty)
    }
}
#endif
