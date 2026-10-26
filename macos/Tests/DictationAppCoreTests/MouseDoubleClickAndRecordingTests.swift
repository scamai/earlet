// The mouse button after the first device session: a double-click keeps
// dictation running as a click does (the hold key latches on a double-tap,
// so a double-click is what people try first, and before this it started and
// stopped at once); any extra button can be recorded; and every attempt's
// first log line says what started it.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
final class MouseClock {
    var now: TimeInterval = 1_000
}

@MainActor
private func enabledModel(_ harness: Harness, button: DictationMouseSideButton? = .back,
                          clock: MouseClock = MouseClock()) async -> DictationModel {
    harness.settings.mouseButton = button
    var environment = harness.environment()
    environment.holdKeyUptime = { clock.now }
    let model = DictationModel(environment: environment)
    await model.toggle()
    #expect(model.enabled)
    return model
}

// ─── Double-click ───

@MainActor
@Suite("A double-click on the mouse button keeps dictation running")
struct MouseDoubleClickTests {

    /// Measured on the owner's mouse (2026-10-25): the second click went down
    /// 55 and 64 ms after the first one came up.
    @Test("the second click of a double-click is absorbed, and the next press stops")
    func doubleClickLatches() async {
        let harness = Harness()
        let clock = MouseClock()
        let model = await enabledModel(harness, clock: clock)

        harness.mouse.press()
        clock.now += 0.12
        harness.mouse.release()
        #expect(await eventually { model.latched && model.recording })
        clock.now += 0.06
        harness.mouse.press()
        clock.now += 0.1
        harness.mouse.release()
        try? await Task.sleep(for: .milliseconds(200))
        #expect(model.latched && model.recording, "the double-click must not stop what its first click started")
        #expect(harness.workers.made[0].state.get.transcribes == 0)
        #expect(harness.logLines.get.contains { $0.contains("mouse button double-clicked: dictation keeps running") })

        clock.now += 4
        harness.mouse.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        clock.now += 0.1
        harness.mouse.release()
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.transcribes == 1)
    }

    @Test("a press after the double-click window is the stop, as before")
    func pressAfterTheWindowStops() async {
        let harness = Harness()
        let clock = MouseClock()
        let model = await enabledModel(harness, clock: clock)

        harness.mouse.press()
        clock.now += 0.12
        harness.mouse.release()
        #expect(await eventually { model.latched && model.recording })
        clock.now += DictationMouseSideButton.doubleClickWindow + 0.05
        harness.mouse.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.logLines.get.contains { $0.contains("latch ended by a mouse button press") })
    }

    @Test("only one press is absorbed: a third quick press stops")
    func tripleClickStops() async {
        let harness = Harness()
        let clock = MouseClock()
        let model = await enabledModel(harness, clock: clock)

        harness.mouse.press()
        clock.now += 0.1
        harness.mouse.release()
        #expect(await eventually { model.latched && model.recording })
        clock.now += 0.05
        harness.mouse.press()
        clock.now += 0.08
        harness.mouse.release()
        clock.now += 0.05
        harness.mouse.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
    }

    /// A key latch has no click behind it: a quick mouse press stops it.
    @Test("a mouse press soon after a key latch began still stops it")
    func keyLatchIsNotAClick() async {
        let harness = Harness()
        let clock = MouseClock()
        let model = await enabledModel(harness, clock: clock)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        clock.now += 0.05
        harness.mouse.press()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
    }
}

// ─── Any extra button ───

@Suite("Any side or extra mouse button can be the mouse button")
struct ExtraMouseButtonTests {

    @Test("the stored ids number buttons as the mouse does; the side buttons keep the ids Windows saves")
    func storedIds() {
        #expect(DictationMouseSideButton.back.rawValue == "button4")
        #expect(DictationMouseSideButton.forward.rawValue == "button5")
        let seventh = DictationMouseSideButton(rawValue: "button7")
        #expect(seventh?.buttonNumber == 6)
        #expect(seventh?.label == "Mouse button 7")
        #expect(seventh?.name == "mouse button 7")
        #expect(seventh?.caution.contains("mouse button 7 no longer does") == true)
        #expect(DictationMouseSideButton(rawValue: "button32")?.buttonNumber == 31)
        for refused in ["button1", "button2", "button3", "button33", "button", "button04", "buttonx", "4", ""] {
            #expect(DictationMouseSideButton(rawValue: refused) == nil, "\(refused)")
        }
        let scratch = ScratchDefaults()
        let settings = DictationSettings(defaults: scratch.defaults)
        settings.mouseButton = seventh
        #expect(DictationSettings(defaults: scratch.defaults).mouseButton == seventh)
    }

    @Test("a recording takes side and extra buttons, refuses the middle one and leaves left and right alone")
    func recordedClicks() {
        #expect(DictationMouseSideButton.recorded(buttonNumber: 0) == nil)
        #expect(DictationMouseSideButton.recorded(buttonNumber: 1) == nil)
        if case .rejected(let reason)? = DictationMouseSideButton.recorded(buttonNumber: 2) {
            #expect(reason.contains("middle button"))
        } else {
            Issue.record("the middle button was not refused")
        }
        #expect(DictationMouseSideButton.recorded(buttonNumber: 3) == .recorded(.back))
        #expect(DictationMouseSideButton.recorded(buttonNumber: 9) == .recorded(DictationMouseSideButton(buttonNumber: 9)!))
        #expect(DictationMouseSideButton.recorded(buttonNumber: 32) == nil)
        #expect(DictationMouseSideButton.presets == [.back, .forward])
    }
}

@MainActor
@Suite("Recording a mouse button in Settings")
struct MouseButtonRecordingTests {

    @Test("recording silences the mouse listener and every press, then listens on the recorded button")
    func recordingChoosesTheButton() async {
        let harness = Harness()
        let model = await enabledModel(harness)
        #expect(harness.mouse.running)

        #expect(model.beginRecordingMouseButton())
        #expect(model.recordingMouseButton)
        #expect(!harness.mouse.running, "the tap would swallow the button before the window saw it")
        #expect(!model.beginRecordingHoldKey(), "one recording at a time")
        harness.trigger.press()
        harness.mouse.press()
        try? await Task.sleep(for: .milliseconds(50))
        #expect(!model.recording, "no press dictates while a button is being recorded")
        harness.trigger.release()
        harness.mouse.release()

        let seventh = DictationMouseSideButton(buttonNumber: 6)!
        model.endRecordingMouseButton(choosing: seventh)
        #expect(!model.recordingMouseButton)
        #expect(model.mouseButton == seventh)
        #expect(harness.settings.mouseButton == seventh)
        #expect(harness.mouse.running)
        #expect(harness.mouse.buttons.last == seventh)
        #expect(model.status.contains("mouse button 7"))
    }

    @Test("a cancelled recording keeps the button and listens again")
    func cancelledRecording() async {
        let harness = Harness()
        let model = await enabledModel(harness)
        #expect(model.beginRecordingMouseButton())
        model.endRecordingMouseButton(choosing: nil)
        #expect(model.mouseButton == .back)
        #expect(harness.mouse.running)
        harness.mouse.press()
        #expect(await eventually { model.recording }, "after the recording the button works again")
        harness.mouse.release()
        #expect(await eventually { !model.busy })
    }

    @Test("a button recorded while dictation is off is saved, and listened to once it is on")
    func recordedWhileOff() async {
        let harness = Harness()
        let model = harness.model()
        #expect(model.beginRecordingMouseButton())
        let ninth = DictationMouseSideButton(buttonNumber: 8)!
        model.endRecordingMouseButton(choosing: ninth)
        #expect(harness.settings.mouseButton == ninth)
        #expect(!harness.mouse.running)
        await model.toggle()
        #expect(harness.mouse.running && harness.mouse.buttons.last == ninth)
    }
}

// ─── What started it ───

@MainActor
@Suite("Each attempt's log says what started it")
struct AttemptTriggerLogTests {

    private func startedLine(_ harness: Harness) -> String {
        harness.logLines.get.last { $0.contains("event=started") } ?? ""
    }

    @Test("the hold key, a recorded combination and the mouse button are told apart")
    func triggers() async {
        let harness = Harness()
        let clock = MouseClock()
        let model = await enabledModel(harness, clock: clock)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(startedLine(harness).hasSuffix("event=started trigger=key"), "\(startedLine(harness))")
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })

        harness.mouse.press()
        #expect(await eventually { model.recording })
        #expect(startedLine(harness).hasSuffix("event=started trigger=mouse"), "\(startedLine(harness))")
        clock.now += 1 // a hold, not a click
        harness.mouse.release()
        #expect(await eventually { !model.busy && !model.recording })

        model.selectHoldKey(.shortcut(DictationKeyShortcut(keyCode: 49, modifiers: .option)))
        harness.trigger.press(flags: [])
        #expect(await eventually { model.recording })
        #expect(startedLine(harness).hasSuffix("event=started trigger=combination"), "\(startedLine(harness))")
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("a double-tap of the hold key that latches says so")
    func doubleTapLogged() async {
        let harness = Harness()
        let model = await enabledModel(harness, button: nil)
        harness.trigger.doubleTap()
        #expect(await eventually { model.latched })
        #expect(harness.logLines.get.contains { $0.contains("hold key double-tapped: dictation keeps running") })
        harness.stopTrap.stopKey()
        #expect(await eventually { !model.busy && !model.latched })
    }
}
#endif
