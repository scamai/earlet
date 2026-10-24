// Escape cancels a hold, and the hold key used in a shortcut (Right Command
// + C) never dictates: the recording is dropped without inserting.

#if canImport(Testing)
import CoreGraphics
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@Suite("A hold is told apart from a shortcut")
struct HoldChordGuardTests {

    @Test("another key or modifier during the hold is a shortcut; Escape is a cancel")
    func verdicts() {
        var g = HoldChordGuard()
        let beforeHold = g.keyDown(8)
        #expect(beforeHold == .none, "no verdicts before a hold begins")
        let began = g.begin(holdMask: .maskCommand, flags: .maskCommand)
        #expect(began)
        let sameKey = g.modifiersChanged(.maskCommand)
        let shift = g.modifiersChanged([.maskCommand, .maskShift])
        let escape = g.keyDown(HoldChordGuard.escapeKeyCode)
        let letter = g.keyDown(8)
        let released = g.modifiersChanged([])
        #expect(sameKey == .none)
        #expect(shift == .chord)
        #expect(escape == .escape)
        #expect(letter == .chord)
        #expect(released == .none, "releasing the hold key is not a shortcut")
        g.end()
        let afterHold = g.keyDown(8)
        #expect(afterHold == .none)
    }

    @Test("a hold that begins with another modifier already down never starts")
    func shortcutInProgressIsNotAHold() {
        var g = HoldChordGuard()
        let began = g.begin(holdMask: .maskCommand, flags: [.maskCommand, .maskShift])
        #expect(!began)
        #expect(!g.isActive)
    }

    @Test("Caps Lock and device bits are not a shortcut")
    func nonShortcutFlagsAreIgnored() {
        var g = HoldChordGuard()
        let began = g.begin(holdMask: .maskCommand, flags: [.maskCommand, .maskAlphaShift])
        let verdict = g.modifiersChanged([.maskCommand, .maskAlphaShift, .maskNonCoalesced])
        #expect(began)
        #expect(verdict == .none)
    }
}

@MainActor
@Suite("The live trigger reports modifier flags before the hold, synchronously")
struct HotkeyHoldTriggerOrderTests {

    @Test("the flags that start a hold arrive before the hold, and a second modifier is reported")
    func flagsPrecedeTheHold() {
        let trigger = HotkeyHoldTrigger(key: .rightCommand)
        var events: [String] = []
        trigger.onModifierFlags = { flags in events.append("flags:\(flags.rawValue & CGEventFlags.maskCommand.rawValue != 0 ? "cmd" : "-")\(flags.contains(.maskShift) ? "+shift" : "")") }
        trigger.onHoldStart = { events.append("start") }
        trigger.onHoldEnd = { events.append("end") }

        trigger.hotkey.process(flags: .maskCommand, keyCode: 54)
        // Delivered in the tap's own call on the main thread, not in a later
        // task: the key watch must exist before the next key can arrive.
        #expect(events == ["flags:cmd", "start"])
        trigger.hotkey.process(flags: [.maskCommand, .maskShift], keyCode: 56)
        trigger.hotkey.process(flags: [], keyCode: 54)
        #expect(events == ["flags:cmd", "start", "flags:cmd+shift", "flags:-", "end"])
    }

    @Test("the left Command key does not start a Right Command hold")
    func otherSideDoesNotStart() {
        let trigger = HotkeyHoldTrigger(key: .rightCommand)
        var starts = 0
        trigger.onHoldStart = { starts += 1 }
        trigger.hotkey.process(flags: .maskCommand, keyCode: 55)
        #expect(starts == 0)
    }
}

@MainActor
@Suite("Escape and shortcuts cancel a hold without inserting")
struct DictationModelCancelTests {

    private func enabledModel(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("Escape during a hold cancels it: nothing transcribed or inserted")
    func escapeCancels() async {
        let harness = Harness()
        let model = await enabledModel(harness)
        #expect(!harness.keyWatch.active, "no key watch outside a hold")

        harness.trigger.press()
        #expect(harness.keyWatch.active)
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && !model.busy && model.status.contains("cancelled") })
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(100))

        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.workers.made.first?.state.get.transcribes == 0)
        #expect(!harness.keyWatch.active)
    }

    @Test("Right Command + C copies instead of dictating")
    func keyChordCancels() async {
        let harness = Harness()
        let model = await enabledModel(harness)

        harness.trigger.press()
        harness.keyWatch.type(8) // kVK_ANSI_C
        #expect(await eventually { !model.recording && model.status.contains("shortcut") })
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(100))

        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.workers.made.first?.state.get.transcribes == 0)
    }

    @Test("a second modifier during the hold cancels it")
    func modifierChordCancels() async {
        let harness = Harness()
        let model = await enabledModel(harness)

        harness.trigger.press()
        harness.trigger.modifiers([.maskCommand, .maskShift])
        #expect(await eventually { !model.recording && model.status.contains("shortcut") })
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(100))
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("a hold key pressed while another modifier is down never records")
    func shortcutAlreadyInProgress() async {
        let harness = Harness()
        let model = await enabledModel(harness)

        harness.trigger.press(flags: [.maskCommand, .maskShift])
        #expect(!model.recording)
        #expect(!harness.keyWatch.active)
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(100))
        #expect(harness.capture.state.get.starts == 0)
    }

    @Test("an ordinary hold still inserts, and the key watch ends with it")
    func plainHoldStillWorks() async {
        let harness = Harness()
        let model = await enabledModel(harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(!harness.keyWatch.active)
    }
}
#endif
