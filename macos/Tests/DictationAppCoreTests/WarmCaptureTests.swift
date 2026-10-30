// The microphone starts off the main actor, stays warm between presses, and
// closes after an idle period instead of after every dictation.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Capture is warmed off the main actor and cooled down when idle")
struct WarmCaptureTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("the press-time warm-up runs off the main thread")
    func coldStartIsOffMain() async {
        let harness = Harness()
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(harness.capture.state.get.startedOnMainThread == [false])
    }

    @Test("the engine stays warm after a dictation, so the next press only arms it")
    func secondPressIsWarm() async {
        let harness = Harness()
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(harness.capture.state.get.coolDowns == 0)
        #expect(harness.capture.isCurrentlyWarm)

        await dictate(harness, model)
        let state = harness.capture.state.get
        // The first press warms the engine once (the press-time precursor);
        // the second only arms it.
        #expect(state.warmUps == 1)
        #expect(state.starts == 0)
        #expect(state.arms == 2)
        #expect(harness.inserts.texts.get == ["Hello world", "Hello world"])
    }

    @Test("an idle warm engine is closed by the housekeeping tick")
    func idleEngineIsCooledDown() async {
        let harness = Harness()
        let model = await enabled(harness)
        await dictate(harness, model)

        await model.housekeepingTick()
        #expect(harness.capture.state.get.coolDowns == 0, "not idle long enough yet")
        harness.capture.state.mutate { $0.idleLongEnough = true }
        await model.housekeepingTick()
        #expect(harness.capture.state.get.coolDowns == 1)
        #expect(!harness.capture.isCurrentlyWarm)
    }

    // ─── How long the microphone stays open ───
    //
    // Measured over 545 real dictations on one Mac: a built-in microphone
    // gives audio 85 ms after a cold start, a Bluetooth one 269 ms (862 ms
    // at p90). The fake's `idleFor` stands in for the time since the last
    // dictation; housekeeping is pushed out of reach, so only the timer the
    // end of a dictation starts can close the microphone.

    private func enabledQuietHousekeeping(_ harness: Harness) async -> DictationModel {
        var harness = harness
        harness.timing.housekeepingSeconds = 3_600
        harness.timing.microphoneCheckSeconds = 0.02
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("the graces: 3 s for a built-in or wired microphone, 10 s for Bluetooth, checked every 0.5 s")
    func graces() {
        let timing = DictationTiming()
        #expect(timing.microphoneIdleSeconds == 3)
        #expect(timing.bluetoothMicrophoneIdleSeconds == 10)
        #expect(timing.microphoneCheckSeconds == 0.5)
    }

    @Test("a built-in microphone closes once the dictation has been idle 3 s, without waiting for housekeeping")
    func builtInClosesSoon() async {
        let harness = Harness()
        let model = await enabledQuietHousekeeping(harness)
        await dictate(harness, model)
        harness.capture.state.mutate { $0.idleFor = 3.1 }
        #expect(await eventually { harness.capture.state.get.coolDowns == 1 })
        #expect(!harness.capture.isCurrentlyWarm)
        #expect(harness.capture.state.get.gracesAsked.allSatisfy { $0 == 3 })
        // The model logs after `coolDown()` returns, so the line can trail the count.
        #expect(await eventually { harness.logLines.get.contains { $0.contains("microphone closed after 3 s idle (other)") } })
    }

    @Test("a Bluetooth microphone stays open for 10 s, then closes")
    func bluetoothStaysOpenLonger() async {
        let harness = Harness()
        harness.capture.state.mutate { $0.bluetooth = true }
        let model = await enabledQuietHousekeeping(harness)
        await dictate(harness, model)
        harness.capture.state.mutate { $0.idleFor = 4 }
        try? await Task.sleep(for: .milliseconds(200))
        #expect(harness.capture.state.get.coolDowns == 0, "4 s is inside the Bluetooth grace")
        #expect(harness.capture.isCurrentlyWarm)
        harness.capture.state.mutate { $0.idleFor = 10.5 }
        #expect(await eventually { harness.capture.state.get.coolDowns == 1 })
        #expect(await eventually { harness.logLines.get.contains { $0.contains("microphone closed after 10 s idle (bluetooth)") } })
    }

    @Test("a press before the grace ends keeps the microphone warm for that dictation")
    func pressKeepsItWarm() async {
        let harness = Harness()
        let model = await enabledQuietHousekeeping(harness)
        await dictate(harness, model)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.state.mutate { $0.idleFor = 60 }
        try? await Task.sleep(for: .milliseconds(200))
        #expect(harness.capture.state.get.coolDowns == 0, "never closed under a hold")
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(await eventually { harness.capture.state.get.coolDowns == 1 }, "and closed once that one is idle too")
        #expect(harness.capture.state.get.arms == 2, "the second press used the warm engine")
    }

    @Test("pausing stops the timer: nothing closes or reopens afterwards")
    func pauseStopsTheTimer() async {
        let harness = Harness()
        let model = await enabledQuietHousekeeping(harness)
        await dictate(harness, model)
        await model.toggle()
        #expect(!model.enabled)
        let closed = harness.capture.state.get.coolDowns
        harness.capture.state.mutate { $0.idleFor = 60; $0.warm = true }
        try? await Task.sleep(for: .milliseconds(200))
        #expect(harness.capture.state.get.coolDowns == closed)
    }

    @Test("a cancel keeps the engine warm")
    func cancelKeepsTheEngineWarm() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.recording && harness.capture.state.get.warmUps == 1 })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && !model.busy })
        #expect(harness.capture.state.get.coolDowns == 0)
        #expect(harness.capture.state.get.stops == 1)
    }

    @Test("the window stays responsive while a slow start is under way, and a quick release is kept",
          .timeLimit(.minutes(1)))
    func slowStartKeepsTheMainActorFree() async {
        let harness = Harness()
        harness.capture.startDelay.mutate { $0 = 0.5 }
        let model = await enabled(harness)

        harness.trigger.press()
        harness.trigger.release() // before capture is up
        // This test runs on the main actor. Had the 0.5 s start run there
        // too, this sleep could only end after it, and the start would show.
        try? await Task.sleep(for: .milliseconds(100))
        #expect(harness.capture.state.get.warmUps == 0, "the main actor waited for the microphone")
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("Escape during a slow start discards the recording once capture is up",
          .timeLimit(.minutes(1)))
    func escapeDuringSlowStart() async {
        let harness = Harness()
        harness.capture.startDelay.mutate { $0 = 0.5 }
        let model = await enabled(harness)

        harness.trigger.press()
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        harness.trigger.release()
        #expect(await eventually { harness.capture.state.get.stops == 1 && !model.busy })
        #expect(model.status.contains("cancelled"))
        #expect(harness.inserts.texts.get.isEmpty)
    }
}
#endif
