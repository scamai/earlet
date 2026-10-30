// Double-tap the hold key to latch dictation on; one press of the key, or
// Return, stops. The toggle is derived from the chosen key, the pair's first
// tap never transcribes on its own, and a latch is an ordinary hold for
// everything else: maximum hold, Escape, Cancel.

#if canImport(Testing)
import CoreGraphics
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Double-tap latches dictation on the hold key")
struct LatchTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("a double-tap latches, and the next double-tap stops and transcribes")
    func latchThenUnlatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        // Recording is published once the press passes the privacy gate.
        #expect(await eventually { model.latched && model.recording })
        #expect(model.status == "Listening hands-free. Press Right Command, Space or Return to insert.")

        harness.trigger.doubleTapAgain()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(!model.latched)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.transcribes == 1,
                "the pair's first tap never transcribed on its own")
    }

    @Test("a latch is bounded by the maximum hold")
    func latchHitsTheCeiling() async {
        var harness = Harness()
        harness.timing.maximumHoldSeconds = 0.4
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(model.latched)
        #expect(await eventually { !model.latched && !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("Escape during a latch aborts without inserting")
    func escapeCancelsALatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(model.latched)
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && !model.latched && model.status.contains("cancelled") })
        try? await Task.sleep(for: .milliseconds(100))
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.workers.made[0].state.get.transcribes == 0)
    }

    @Test("a single quick press-release never latches, and still transcribes")
    func singleTapDoesNotLatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(!model.latched)
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(!model.latched)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("the pair's first tap does not transcribe when the second completes the latch")
    func parkedTapNeverTranscribes() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        // Past the window: the parked first tap's finish was cancelled by
        // the latch, so nothing transcribed on its own.
        try? await Task.sleep(for: .milliseconds(600))
        #expect(model.latched && model.recording, "still dictating past the window")
        #expect(harness.workers.made[0].state.get.transcribes == 0)
        harness.trigger.doubleTapAgain()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }
}

/// Once the hold key is up, the shortcut rule is over: of the keys, only Escape
/// cancels (the window's and the menu's Cancel still do).
@MainActor
@Suite("Typing during a latch or a parked tap keeps the recording")
struct LatchKeysTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("a letter and ⌘-Tab during a latch keep recording")
    func keysDuringALatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        harness.keyWatch.type(8)   // C
        // Return arrives over the commit-key trap, not this passive watch.
        harness.trigger.modifiers(.maskCommand)
        harness.keyWatch.type(48)  // Tab, with Command down: ⌘-Tab
        harness.trigger.modifiers([])
        try? await Task.sleep(for: .milliseconds(100))
        #expect(model.latched && model.recording, "\(model.status)")
        #expect(!model.status.contains("shortcut"))

        harness.trigger.doubleTapAgain()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("Shift during a latch keeps recording")
    func shiftDuringALatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        harness.trigger.modifiers(.maskShift)
        harness.trigger.modifiers([])
        try? await Task.sleep(for: .milliseconds(100))
        #expect(model.latched && model.recording, "\(model.status)")

        harness.trigger.doubleTapAgain()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
    }

    @Test("Escape still cancels a latch")
    func escapeStillCancels() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        #expect(harness.stopTrap.active, "the commit-key trap is installed for the latch")
        harness.keyWatch.type(8)
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && !model.busy && model.status.contains("cancelled") })
        #expect(!harness.stopTrap.active, "the trap leaves with the latch")
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("typing right after a quick tap, while it waits out the window, keeps it")
    func keysDuringAParkedTap() async {
        let harness = Harness()
        let model = await enabled(harness)
        let worker = harness.workers.made[0]

        harness.trigger.press()
        #expect(await eventually { worker.state.get.previewStarts == 1 }, "capture is up")
        harness.trigger.release()
        harness.keyWatch.type(8)
        harness.trigger.modifiers(.maskShift)
        harness.trigger.modifiers([])
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") }, "\(model.status)")
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("typing after a release that came before capture was up keeps it")
    func keysBeforeCaptureIsUp() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.capture.state.mutate { $0.holdWarmUp = true }

        harness.trigger.press()
        harness.trigger.release()
        #expect(await eventually { harness.capture.state.get.warmUpWaiting })
        harness.keyWatch.type(8)
        harness.capture.finishWarmUp()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") }, "\(model.status)")
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a latch whose second tap started the hold also stops treating keys as shortcuts",
          .timeLimit(.minutes(1)))
    func latchMadeByTheSecondPress() async {
        // The first tap lands while the last dictation is still finishing
        // and is refused; the second one, inside the window, starts a hold
        // and completes the pair. That hold is the latch, begun by a press.
        var harness = Harness()
        harness.usesDetector = true
        harness.timing.transcribeSeconds = 30
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }
        harness.detector.down()
        #expect(await eventually { model.recording })
        try? await Task.sleep(for: .milliseconds(400))
        harness.detector.up()
        #expect(await eventually { model.transcribing && worker.state.get.transcribes == 1 })
        try? await Task.sleep(for: .milliseconds(400))

        harness.detector.tap()
        worker.finishTranscribe("hello world")
        #expect(await eventually { !model.busy })
        harness.detector.tap()
        #expect(await eventually { model.latched && model.recording })
        harness.keyWatch.type(8)
        try? await Task.sleep(for: .milliseconds(100))
        #expect(model.latched && model.recording, "\(model.status)")

        harness.detector.doubleTap()
        #expect(await eventually { !model.busy && worker.state.get.transcribes == 2 })
        #expect(harness.inserts.texts.get == ["Hello world", "Hello world"])
    }

    @Test("while the key is held, a letter is still a shortcut")
    func heldKeyStillMakesAShortcut() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.keyWatch.type(8)
        #expect(await eventually { !model.recording && model.status.contains("shortcut") })
        harness.trigger.release()
    }
}

/// The model wired to the real double-tap detector: the detector's own
/// latched flag drifts from the model's whenever a latch ends some other
/// way, and a cold microphone can come up between the two taps.
@MainActor
@Suite("A double-tap through the real detector latches whatever ended the last one",
       .serialized)
struct DetectorLatchTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func detecting() -> Harness {
        var harness = Harness()
        harness.usesDetector = true
        return harness
    }

    /// A double-tap latches, nothing is transcribed for it, and a second
    /// double-tap inserts exactly one more dictation.
    private func latchesAndFinishes(_ harness: Harness, _ model: DictationModel,
                                    transcribedBefore: Int) async {
        let worker = harness.workers.made[0]
        harness.detector.doubleTap()
        #expect(await eventually { model.latched && model.recording }, "the double-tap latched")
        // Past the double-tap window: a parked first tap would have
        // transcribed on its own by now.
        try? await Task.sleep(for: .milliseconds(500))
        #expect(model.latched && model.recording)
        #expect(worker.state.get.transcribes == transcribedBefore, "the pair's first tap never transcribed")
        harness.detector.doubleTap()
        #expect(await eventually { !model.busy && !model.latched && model.status.hasPrefix("Text inserted") })
        #expect(worker.state.get.transcribes == transcribedBefore + 1)
    }

    @Test("after Escape ended a latch, the next double-tap latches again")
    func relatchAfterEscape() async {
        let harness = detecting()
        let model = await enabled(harness)

        harness.detector.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && !model.recording && model.status.contains("cancelled") })

        await latchesAndFinishes(harness, model, transcribedBefore: 0)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("after the ceiling ended a latch, the next double-tap latches again", .timeLimit(.minutes(1)))
    func relatchAfterTheCeiling() async {
        var harness = detecting()
        harness.timing.maximumHoldSeconds = 0.8
        let model = await enabled(harness)

        harness.detector.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        #expect(await eventually { !model.busy && !model.latched && model.status.hasPrefix("Text inserted") })
        #expect(harness.workers.made[0].state.get.transcribes == 1)

        await latchesAndFinishes(harness, model, transcribedBefore: 1)
    }

    @Test("after a refused press, the next double-tap latches again")
    func relatchAfterARefusal() async {
        let harness = detecting()
        let model = await enabled(harness)
        harness.privacy.mutate { $0 = .secureFieldFocused }

        harness.detector.doubleTap()
        #expect(await eventually { model.status.contains("password fields") && !model.latched })
        harness.privacy.mutate { $0 = nil }
        try? await Task.sleep(for: .milliseconds(400))

        await latchesAndFinishes(harness, model, transcribedBefore: 0)
    }

    @Test("a double-tap made while the last dictation transcribes leaves the next one working",
          .timeLimit(.minutes(1)))
    func doubleTapWhileTranscribing() async {
        var harness = detecting()
        harness.timing.transcribeSeconds = 30
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }

        harness.detector.down()
        #expect(await eventually { model.recording })
        try? await Task.sleep(for: .milliseconds(400))
        harness.detector.up()
        #expect(await eventually { worker.state.get.transcribes == 1 && model.transcribing })
        try? await Task.sleep(for: .milliseconds(400))
        harness.detector.doubleTap()
        #expect(!model.latched, "a double-tap while busy does not latch")
        worker.finishTranscribe("hello world")
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        try? await Task.sleep(for: .milliseconds(400))

        await latchesAndFinishes(harness, model, transcribedBefore: 1)
        #expect(harness.inserts.texts.get == ["Hello world", "Hello world"])
    }

    @Test("a cold microphone that comes up between the taps still latches", .timeLimit(.minutes(1)))
    func captureComesUpBetweenTheTaps() async {
        let harness = detecting()
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        harness.capture.state.mutate { $0.holdWarmUp = true }

        // Tap one: released while the microphone is still starting.
        harness.detector.tap()
        #expect(await eventually { harness.capture.state.get.warmUpWaiting })
        // The microphone comes up before the second tap.
        harness.capture.finishWarmUp()
        #expect(await eventually { worker.state.get.previewStarts == 1 }, "capture is up")
        // Tap two, still inside the double-tap window.
        harness.detector.tap()

        #expect(await eventually { model.latched && model.recording })
        try? await Task.sleep(for: .milliseconds(500))
        #expect(model.latched, "still dictating past the window")
        #expect(worker.state.get.transcribes == 0, "the first tap did not transcribe on its own")
        harness.detector.doubleTap()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(worker.state.get.transcribes == 1)
    }

    @Test("a lone quick tap with a cold microphone still transcribes after the window")
    func loneColdTapTranscribes() async {
        let harness = detecting()
        let model = await enabled(harness)
        harness.capture.state.mutate { $0.holdWarmUp = true }

        harness.detector.tap()
        #expect(await eventually { harness.capture.state.get.warmUpWaiting })
        harness.capture.finishWarmUp()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(!model.latched)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }
}

@MainActor
@Suite("The live trigger fires the latch edges for a real double-tap")
struct LatchEdgeTests {

    @Test("two quick pairs latch and unlatch, in hold-edge order")
    func pairsLatchAndUnlatch() {
        let trigger = HotkeyHoldTrigger(key: .rightCommand)
        var events: [String] = []
        trigger.onHoldStart = { events.append("start") }
        trigger.onHoldEnd = { events.append("end") }
        trigger.onLatchStart = { events.append("latch") }
        trigger.onLatchEnd = { events.append("unlatch") }

        func tap() {
            trigger.hotkey.process(flags: .maskCommand, keyCode: 54)
            trigger.hotkey.process(flags: [], keyCode: 54)
        }
        tap()
        tap()
        #expect(events == ["start", "end", "start", "latch", "end"])
        tap()
        tap()
        #expect(events == ["start", "end", "start", "latch", "end",
                           "start", "end", "start", "unlatch", "end"])
    }

    @Test("a lone tap is only a short hold, and the other side never latches")
    func loneTapAndOtherSide() async {
        let trigger = HotkeyHoldTrigger(key: .rightCommand)
        var latches = 0
        var starts = 0
        trigger.onHoldStart = { starts += 1 }
        trigger.onLatchStart = { latches += 1 }

        trigger.hotkey.process(flags: .maskCommand, keyCode: 54)
        trigger.hotkey.process(flags: [], keyCode: 54)
        // Past the window: the next press starts a new pair, not a latch.
        try? await Task.sleep(for: .milliseconds(450))
        trigger.hotkey.process(flags: .maskCommand, keyCode: 54)
        trigger.hotkey.process(flags: [], keyCode: 54)
        #expect(latches == 0)
        #expect(starts == 2)

        // Two quick taps of LEFT Command: same flag bit, different key.
        trigger.hotkey.process(flags: .maskCommand, keyCode: 55)
        trigger.hotkey.process(flags: [], keyCode: 55)
        trigger.hotkey.process(flags: .maskCommand, keyCode: 55)
        trigger.hotkey.process(flags: [], keyCode: 55)
        #expect(latches == 0)
        #expect(starts == 2, "the left side starts nothing either")
    }
}

/// The stop signals while latched: one press of the hold key, or the trap's
/// stop key. The trailing tap of the old double-tap stop habit is absorbed.
@MainActor
@Suite("A latch ends on one press, a stop key, or the old double-tap habit")
struct LatchStopTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("one press of the hold key stops a latch and transcribes")
    func pressStopsALatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        #expect(harness.stopTrap.active, "the commit-key trap is installed for the latch")

        harness.trigger.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(!model.latched)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.transcribes == 1)
        #expect(!harness.stopTrap.active, "the trap leaves with the latch")
        // The stop press's own release starts nothing.
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(100))
        #expect(!model.recording && harness.workers.made[0].state.get.transcribes == 1)
    }

    @Test("the trailing tap of a habitual double-tap stop starts nothing")
    func trailingTapIsAbsorbed() async {
        let harness = Harness()
        var keyTime: TimeInterval = 1_000
        var environment = harness.environment()
        environment.holdKeyUptime = { keyTime }
        let model = DictationModel(environment: environment)
        await model.toggle()
        #expect(model.enabled)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        // The old habit: two presses to stop. The first stops the latch; the
        // second — press, release and the detector's stray edge — must not
        // open a fresh dictation.
        harness.trigger.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        // Recognition may finish after the real double-tap window under
        // concurrent test load. Keep the key events inside their own window.
        keyTime += 0.1
        harness.trigger.doubleTapAgain()
        #expect(model.attempt == nil, "the trailing tap must not create a new hold")
        #expect(!model.recording && !model.latched, "\(model.status)")
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.transcribes == 1,
                "nothing new was recorded or transcribed")

        // Past the window the next hold works normally.
        keyTime += 1
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && harness.inserts.texts.get == ["Hello world", "Hello world"] })
    }

    @Test("a stop key during a latch stops it and transcribes")
    func stopKeyStopsALatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })

        harness.stopTrap.stopKey()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(!model.latched)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(!harness.stopTrap.active, "the trap leaves with the latch")
    }

    @Test("the stop press also ends a latch before capture is up")
    func pressStopsALatchBeforeCapture() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.capture.state.mutate { $0.holdWarmUp = true }

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched })
        #expect(harness.stopTrap.active)
        // The stop arrives while the microphone is still starting; it is
        // honoured the moment capture is up, the way a release is.
        harness.trigger.press()
        harness.capture.finishWarmUp()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }
}

/// The trap's pure swallow table, pinned without a live tap.
@Suite("The latch stop trap swallows only the stop keys")
struct LatchStopKeyTests {
    @Test("Space, Return and keypad Enter, down and up, are consumed; everything else passes")
    func stopKeysAreConsumed() {
        #expect(LatchStopKeys.consumes(type: .keyDown, keyCode: LatchStopKeys.spaceKeyCode))
        #expect(LatchStopKeys.consumes(type: .keyUp, keyCode: LatchStopKeys.spaceKeyCode))
        #expect(LatchStopKeys.consumes(type: .keyDown, keyCode: LatchStopKeys.returnKeyCode))
        #expect(LatchStopKeys.consumes(type: .keyUp, keyCode: LatchStopKeys.returnKeyCode))
        #expect(LatchStopKeys.consumes(type: .keyDown, keyCode: LatchStopKeys.keypadEnterKeyCode))
        #expect(LatchStopKeys.consumes(type: .keyUp, keyCode: LatchStopKeys.keypadEnterKeyCode))
        #expect(!LatchStopKeys.consumes(type: .keyDown, keyCode: Int64(HoldChordGuard.escapeKeyCode)))
        #expect(!LatchStopKeys.consumes(type: .keyDown, keyCode: 48))  // Tab
        #expect(!LatchStopKeys.consumes(type: .keyDown, keyCode: 8))   // C
        #expect(!LatchStopKeys.consumes(type: .flagsChanged, keyCode: LatchStopKeys.returnKeyCode))
    }
}
#endif
