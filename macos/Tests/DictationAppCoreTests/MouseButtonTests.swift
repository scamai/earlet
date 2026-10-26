// A mouse side button starts a dictation beside the hold key: hold it to
// talk, click it to keep listening, press again to stop. The model reads the
// button's raw edges, so `latched` stays the one record of a latch however it
// began and however it ends.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("A mouse side button starts a dictation")
struct MouseButtonTests {

    /// The press and release clock the click window reads.
    @MainActor
    final class Clock {
        var now: TimeInterval = 1_000
    }

    private func enabled(_ harness: Harness, button: DictationMouseSideButton? = .back,
                         clock: Clock = Clock()) async -> DictationModel {
        harness.settings.mouseButton = button
        var environment = harness.environment()
        environment.holdKeyUptime = { clock.now }
        let model = DictationModel(environment: environment)
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("holding the button records, and its release inserts")
    func holdToTalk() async {
        let harness = Harness()
        let clock = Clock()
        let model = await enabled(harness, clock: clock)
        #expect(model.status == "Enabled. Hold Right Command or mouse button 4 in a text field.")

        harness.mouse.press()
        #expect(await eventually { model.recording })
        #expect(model.status == "Listening… Release mouse button 4 to insert.")
        clock.now += 1
        harness.mouse.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(!model.latched)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a click keeps dictation running, and the next press stops it")
    func clickLatches() async {
        let harness = Harness()
        let clock = Clock()
        let model = await enabled(harness, clock: clock)

        harness.mouse.press()
        clock.now += 0.1
        harness.mouse.release()
        #expect(await eventually { model.latched && model.recording })
        #expect(model.status == "Dictating. Press mouse button 4, Space or Return to stop.")
        #expect(harness.stopTrap.active, "Space and Return stop a mouse latch too")
        try? await Task.sleep(for: .milliseconds(200))
        #expect(harness.workers.made[0].state.get.transcribes == 0, "the click did not finish anything")

        clock.now += 5
        harness.mouse.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        // The stopping press's own release, however quick, starts nothing.
        clock.now += 0.1
        harness.mouse.release()
        try? await Task.sleep(for: .milliseconds(100))
        #expect(!model.latched && !model.recording)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.transcribes == 1)
    }

    /// The latch the button began is the model's, not the button's: ended by
    /// Return, the next press is a fresh dictation rather than a stop.
    @Test("after Return ends a mouse latch, the next press starts a new dictation")
    func nextPressAfterAStopKeyStarts() async {
        let harness = Harness()
        let clock = Clock()
        let model = await enabled(harness, clock: clock)

        harness.mouse.press()
        clock.now += 0.1
        harness.mouse.release()
        #expect(await eventually { model.latched && model.recording })
        harness.stopTrap.stopKey()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })

        clock.now += 5
        harness.mouse.press()
        #expect(await eventually { model.recording })
        clock.now += 1
        harness.mouse.release()
        #expect(await eventually { !model.busy && harness.inserts.texts.get.count == 2 })
    }

    @Test("the hold key stops a latch the button began")
    func holdKeyStopsAMouseLatch() async {
        let harness = Harness()
        let clock = Clock()
        let model = await enabled(harness, clock: clock)

        harness.mouse.press()
        clock.now += 0.1
        harness.mouse.release()
        #expect(await eventually { model.latched && model.recording })
        harness.trigger.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("the button stops a latch the hold key began")
    func buttonStopsAKeyLatch() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        harness.mouse.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        harness.mouse.release()
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    /// Each trigger's release ends only the hold it started.
    @Test("the hold key's release does not end a hold the button started")
    func keyReleaseLeavesAMouseHold() async {
        let harness = Harness()
        let clock = Clock()
        let model = await enabled(harness, clock: clock)

        harness.mouse.press()
        #expect(await eventually { model.recording })
        harness.trigger.press()
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(600))
        #expect(model.recording, "\(model.status)")
        #expect(harness.workers.made[0].state.get.transcribes == 0)

        clock.now += 2
        harness.mouse.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("the button's release does not end a hold the key started")
    func mouseReleaseLeavesAKeyHold() async {
        let harness = Harness()
        let clock = Clock()
        let model = await enabled(harness, clock: clock)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.mouse.press()
        clock.now += 2
        harness.mouse.release()
        // Past the key's double-tap window, where a release taken for the
        // key's would have finished the hold.
        try? await Task.sleep(for: .milliseconds(600))
        #expect(model.recording && !model.latched, "\(model.status)")
        #expect(harness.workers.made[0].state.get.transcribes == 0)

        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    /// The shortcut rule exists for the hold key, a modifier. Typing while
    /// the button is held is the user working.
    @Test("typing during a held button keeps recording, and Escape cancels")
    func typingDuringAMouseHold() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.mouse.press()
        #expect(await eventually { model.recording })
        harness.keyWatch.type(8)   // C
        try? await Task.sleep(for: .milliseconds(100))
        #expect(model.recording, "\(model.status)")

        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && model.status.contains("cancelled") })
        #expect(harness.inserts.texts.get.isEmpty)
    }

    /// The hold key's release waits out its double-tap window; the button's
    /// release already told a click from a hold, so nothing waits.
    @Test("a held button's release finishes without waiting for a second tap")
    func mouseReleaseIsNotParked() async {
        let harness = Harness()
        let clock = Clock()
        let model = await enabled(harness, clock: clock)

        harness.mouse.press()
        #expect(await eventually { model.recording && model.attempt?.captureStarted == true })
        // Inside the key's double-tap window by the wall clock, whatever the
        // load on this machine; a hold by the click window's clock.
        model.attempt?.startedAt = ProcessInfo.processInfo.systemUptime
        clock.now += 1
        harness.mouse.release()
        #expect(model.attempt?.tapParked != true)
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
    }

    @Test("with no button chosen there is no mouse listener")
    func noButtonNoListener() async {
        let harness = Harness()
        let model = await enabled(harness, button: nil)
        #expect(harness.mouse.starts == 0)
        #expect(model.status == "Enabled. Hold Right Command in a text field.")
    }

    @Test("choosing, changing and clearing the button while enabled")
    func chooseWhileEnabled() async {
        let harness = Harness()
        let model = await enabled(harness, button: nil)

        model.selectMouseButton(.back)
        #expect(harness.mouse.running && harness.mouse.buttons == [.back])
        #expect(harness.settings.mouseButton == .back)
        #expect(model.status == "Enabled. Hold Right Command or mouse button 4 in a text field.")

        model.selectMouseButton(.forward)
        #expect(harness.mouse.buttons == [.back, .forward], "the live listener moves; no second one")
        #expect(harness.mouse.starts == 1)

        model.selectMouseButton(nil)
        #expect(!harness.mouse.running)
        #expect(harness.settings.mouseButton == nil)
    }

    @Test("the button cannot change under a dictation")
    func noChangeWhileRecording() async {
        let harness = Harness()
        let model = await enabled(harness)

        harness.mouse.press()
        #expect(await eventually { model.recording })
        model.selectMouseButton(.forward)
        #expect(model.mouseButton == .back)
        #expect(harness.settings.mouseButton == .back)
    }

    @Test("a refused listener is reported, and the hold key still enables")
    func refusedListener() async {
        let harness = Harness()
        harness.mouse.startResult = false
        let model = await enabled(harness)
        #expect(model.mouseButtonRefused)
        #expect(!harness.mouse.running)

        harness.trigger.press()
        #expect(await eventually { model.recording })
    }

    @Test("pausing removes the mouse listener")
    func pauseStopsTheListener() async {
        let harness = Harness()
        let model = await enabled(harness)
        #expect(harness.mouse.running)
        await model.toggle()
        #expect(!model.enabled)
        #expect(!harness.mouse.running)
    }

    @Test("the choice is saved, and an unknown or missing value is none")
    func settings() {
        let harness = Harness()
        #expect(harness.settings.mouseButton == nil)
        harness.settings.mouseButton = .forward
        #expect(harness.settings.mouseButton == .forward)
        #expect(harness.scratch.defaults.string(forKey: DictationSettings.mouseButtonKey) == "button5",
                "the id the Windows app saves for the same choice")
        harness.scratch.defaults.set("button3", forKey: DictationSettings.mouseButtonKey)
        #expect(harness.settings.mouseButton == nil)
        harness.settings.mouseButton = nil
        #expect(harness.scratch.defaults.object(forKey: DictationSettings.mouseButtonKey) == nil)
    }

    @Test("the picker offers the side buttons, numbered as the mouse names them")
    func choices() {
        #expect(DictationMouseSideButton.presets.map(\.binding.buttonNumber) == [3, 4])
        #expect(DictationMouseSideButton.back.name == "mouse button 4")
        #expect(DictationMouseSideButton.forward.label == "Mouse button 5 (Forward)")
        #expect(DictationMouseSideButton.back.caution.contains("no longer goes Back"),
                "choosing a button always costs its usual action, named")
        #expect(DictationMouseSideButton.forward.caution.contains("no longer goes Forward"))
    }
}
#endif
