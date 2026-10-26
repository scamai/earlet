// Hold keys beyond the picker's modifier keys: anything the user records in
// Settings, a modifier key on its own or a key combination registered as a
// hot key. One gesture for both.

#if canImport(Testing)
import Carbon.HIToolbox
import CoreGraphics
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

private let optionSpace = DictationKeyShortcut(keyCode: UInt16(kVK_Space), modifiers: .option)

// ─── Storage ───

@Suite("A hold key's stored form")
struct HoldControlStorageTests {

    @Test("presets and combinations round-trip; earlier versions' values still read")
    func roundTrip() {
        let keys = DictationHoldKey.presets + [
            .shortcut(optionSpace), .modifier(.leftShift),
            .shortcut(DictationKeyShortcut(keyCode: UInt16(kVK_F13), modifiers: [])),
        ]
        for key in keys {
            #expect(DictationHoldKey(rawValue: key.rawValue) == key, "\(key.rawValue)")
        }
        #expect(DictationHoldKey(rawValue: "rightOption") == .rightOption)
        #expect(DictationHoldKey(rawValue: "function") == .function)
        #expect(DictationHoldKey.shortcut(optionSpace).rawValue == "shortcut:49:2048")
    }

    @Test("an unreadable or no-longer-allowed value is no control, and the settings read the default")
    func refusedValues() {
        let refused = [
            "mouseButton:3", "button4",
            "shortcut:0:0", "shortcut:53:2048", "shortcut:55:256", "shortcut:49", "shortcut:a:b",
            "leftCommand", "",
        ]
        for raw in refused {
            #expect(DictationHoldKey(rawValue: raw) == nil, "\(raw)")
        }
        let scratch = ScratchDefaults()
        let settings = DictationSettings(defaults: scratch.defaults)
        scratch.defaults.set("shortcut:0:0", forKey: DictationSettings.holdKeyKey)
        #expect(settings.holdKey == .standard)
        settings.holdKey = .shortcut(optionSpace)
        #expect(DictationSettings(defaults: scratch.defaults).holdKey == .shortcut(optionSpace))
    }
}

// ─── Words ───

@MainActor
@Suite("How a hold key is named")
struct HoldControlWordsTests {

    @Test("a combination says it no longer reaches other apps")
    func notes() {
        #expect(DictationHoldKey.shortcut(optionSpace).keyNote?.contains("other apps no longer receive it") == true)
        #expect(DictationHoldKey.shortcut(optionSpace).keyNote?.contains("⌥Space") == true)
    }

    @Test("a combination prints its modifiers in menu order")
    func shortcutNames() {
        let all = DictationKeyShortcut(keyCode: UInt16(kVK_F12), modifiers: [.command, .shift, .option, .control])
        #expect(all.displayName == "⌃⌥⇧⌘F12")
        #expect(optionSpace.displayName == "⌥Space")
        #expect(DictationHoldKey.shortcut(optionSpace).name == "⌥Space")
        #expect(DictationHoldKey.shortcut(optionSpace).keycapLabel == "⌥Space")
        #expect(DictationKeyShortcut(keyCode: UInt16(kVK_F13), modifiers: []).displayName == "F13")
        #expect(DictationKeyShortcut(keyCode: UInt16(kVK_LeftArrow), modifiers: .control).displayName == "⌃←")
    }

    @Test("a letter is named from the keyboard layout on the main thread, and by its code elsewhere")
    func letterNames() async {
        let onMain = DictationKeyShortcut.keyName(UInt16(kVK_ANSI_D))
        #expect(onMain.count == 1 && onMain == onMain.uppercased(), "\(onMain)")
        let elsewhere = await Task.detached { DictationKeyShortcut.keyName(UInt16(kVK_ANSI_D)) }.value
        #expect(elsewhere == "Key 2")
    }

    @Test("menu headlines stay one line for the longest names a control can have")
    func longNamesFit() {
        let longest = DictationKeyShortcut(keyCode: UInt16(kVK_ForwardDelete), modifiers: [.command, .shift, .option, .control])
        for key in [DictationHoldKey.shortcut(longest)] {
            let line = DictationMenuGlyph.ready.menuHeadline(holdKeyName: key.name)
            #expect(line.count <= DictationMenuGlyph.menuHeadlineLimit, "\(line)")
        }
    }
}

// ─── What a combination may be ───

@Suite("Which key combinations can be the hold control")
struct ShortcutRuleTests {

    @Test("a key held with Command, Option or Control, or a function key alone")
    func allowed() {
        let allowed: [DictationKeyShortcut] = [
            optionSpace,
            DictationKeyShortcut(keyCode: UInt16(kVK_ANSI_D), modifiers: [.control, .option]),
            DictationKeyShortcut(keyCode: UInt16(kVK_ANSI_F), modifiers: .command),
            DictationKeyShortcut(keyCode: UInt16(kVK_F13), modifiers: []),
            DictationKeyShortcut(keyCode: UInt16(kVK_F5), modifiers: .shift),
        ]
        for shortcut in allowed {
            #expect(shortcut.rejection == nil, "\(shortcut.displayName)")
        }
    }

    @Test("a typing key on its own or with Shift alone, Escape, and a modifier's own code are refused")
    func refused() {
        let typing = [
            DictationKeyShortcut(keyCode: UInt16(kVK_ANSI_A), modifiers: []),
            DictationKeyShortcut(keyCode: UInt16(kVK_ANSI_A), modifiers: .shift),
            DictationKeyShortcut(keyCode: UInt16(kVK_Space), modifiers: []),
            DictationKeyShortcut(keyCode: UInt16(kVK_Return), modifiers: .shift),
        ]
        for shortcut in typing {
            #expect(shortcut.rejection?.contains("needed for typing") == true, "\(shortcut.displayName)")
        }
        #expect(DictationKeyShortcut(keyCode: UInt16(kVK_Escape), modifiers: .option).rejection?.contains("Escape") == true)
        #expect(DictationKeyShortcut(keyCode: 54, modifiers: .command).rejection != nil)
    }

    @Test("fn and Caps Lock are not part of a stored combination")
    func onlyHotKeyModifiers() {
        let flags: CGEventFlags = [.maskSecondaryFn, .maskAlphaShift, .maskAlternate]
        #expect(DictationKeyShortcut.Modifiers(flags) == .option)
        #expect(DictationKeyShortcut(keyCode: 49, modifiers: .init(rawValue: 0xFFFF_FFFF)).modifiers
                == [.command, .shift, .option, .control])
    }
}

// ─── The recorder ───

@Suite("Recording a hold key in Settings")
struct HoldKeyRecorderTests {
    typealias Recorder = DictationHoldKeyRecorder

    @Test("a modifier pressed and released on its own is that key, fn and Left Shift included")
    func loneModifiers() {
        var recorder = Recorder()
        #expect(recorder.handle(.flagsChanged(keyCode: 61, flags: .maskAlternate)) == nil)
        #expect(recorder.handle(.flagsChanged(keyCode: 61, flags: [])) == .recorded(.rightOption))
        #expect(recorder.handle(.flagsChanged(keyCode: 63, flags: .maskSecondaryFn)) == nil)
        #expect(recorder.handle(.flagsChanged(keyCode: 63, flags: [])) == .recorded(.function))
        #expect(recorder.handle(.flagsChanged(keyCode: 56, flags: .maskShift)) == nil)
        #expect(recorder.handle(.flagsChanged(keyCode: 56, flags: [])) == .recorded(.modifier(.leftShift)))
    }

    @Test("Left Command on its own is refused with the reason")
    func leftCommand() {
        var recorder = Recorder()
        #expect(recorder.handle(.flagsChanged(keyCode: 55, flags: .maskCommand)) == nil)
        #expect(recorder.handle(.flagsChanged(keyCode: 55, flags: [])) == .rejected(Recorder.leftCommandRefusal))
    }

    @Test("modifiers then a key record the combination, and neither modifier")
    func combination() {
        var recorder = Recorder()
        #expect(recorder.handle(.flagsChanged(keyCode: 59, flags: .maskControl)) == nil)
        #expect(recorder.handle(.flagsChanged(keyCode: 58, flags: [.maskControl, .maskAlternate])) == nil)
        let expected = DictationKeyShortcut(keyCode: UInt16(kVK_ANSI_D), modifiers: [.control, .option])
        #expect(recorder.handle(.keyDown(keyCode: UInt16(kVK_ANSI_D), flags: [.maskControl, .maskAlternate]))
                == .recorded(.shortcut(expected)))
        #expect(recorder.handle(.flagsChanged(keyCode: 58, flags: .maskControl)) == nil)
        #expect(recorder.handle(.flagsChanged(keyCode: 59, flags: [])) == nil)
    }

    @Test("a modifier released after another one joined records nothing, in either release order")
    func spoiledModifier() {
        for releaseFirst: UInt16 in [61, 54] {
            var recorder = Recorder()
            #expect(recorder.handle(.flagsChanged(keyCode: 54, flags: .maskCommand)) == nil)
            #expect(recorder.handle(.flagsChanged(keyCode: 61, flags: [.maskCommand, .maskAlternate])) == nil)
            let remaining: CGEventFlags = releaseFirst == 61 ? .maskCommand : .maskAlternate
            #expect(recorder.handle(.flagsChanged(keyCode: releaseFirst, flags: remaining)) == nil)
            #expect(recorder.handle(.flagsChanged(keyCode: releaseFirst == 61 ? 54 : 61, flags: [])) == nil)
        }
    }

    @Test("Escape cancels, a typing key is refused, Caps Lock is ignored")
    func escapeTypingAndCapsLock() {
        var recorder = Recorder()
        #expect(recorder.handle(.keyDown(keyCode: UInt16(kVK_Escape), flags: [])) == .cancelled)
        if case .rejected(let reason)? = recorder.handle(.keyDown(keyCode: UInt16(kVK_ANSI_A), flags: [])) {
            #expect(reason.contains("needed for typing"))
        } else {
            Issue.record("a plain letter was not refused")
        }
        #expect(recorder.handle(.flagsChanged(keyCode: 57, flags: .maskAlphaShift)) == nil)
        #expect(recorder.handle(.flagsChanged(keyCode: 57, flags: [])) == nil)
    }
}

// ─── The gesture ───

@Suite("One gesture on a recorded combination")
struct HoldGestureStateTests {

    @Test("press and release are the hold's two edges")
    func hold() {
        var gesture = HoldGestureState()
        #expect(gesture.handle(down: true, timestamp: 0) == [.pushToTalkStart])
        #expect(gesture.isDown)
        #expect(gesture.handle(down: false, timestamp: 1) == [.pushToTalkEnd])
        #expect(!gesture.isDown)
    }

    @Test("a second press inside the window latches, after its own hold edge, as the modifier tap orders them")
    func doublePress() {
        var gesture = HoldGestureState()
        #expect(gesture.handle(down: true, timestamp: 0) == [.pushToTalkStart])
        #expect(gesture.handle(down: false, timestamp: 0.1) == [.pushToTalkEnd])
        #expect(gesture.handle(down: true, timestamp: 0.3) == [.pushToTalkStart, .doubleTapActivate])
        #expect(gesture.handle(down: false, timestamp: 0.4) == [.pushToTalkEnd])
        #expect(gesture.handle(down: true, timestamp: 5) == [.pushToTalkStart])
        #expect(gesture.handle(down: false, timestamp: 5.1) == [.pushToTalkEnd])
        #expect(gesture.handle(down: true, timestamp: 5.2) == [.pushToTalkStart, .doubleTapDeactivate])
    }

    @Test("presses further apart than the window are two holds; a repeated down is one press")
    func slowPressesAndRepeats() {
        var gesture = HoldGestureState()
        #expect(gesture.handle(down: true, timestamp: 0) == [.pushToTalkStart])
        #expect(gesture.handle(down: true, timestamp: 0.01) == [])
        #expect(gesture.handle(down: false, timestamp: 0.1) == [.pushToTalkEnd])
        #expect(gesture.handle(down: true, timestamp: 0.1 + DictationHoldKey.doubleTapWindow) == [.pushToTalkStart])
    }
}

// ─── The live trigger ───

@MainActor
@Suite("The live trigger follows the chosen key")
struct LiveHoldTriggerTests {

    @Test("a change of kind swaps the listener and keeps the callbacks the model set")
    func kindChange() {
        let trigger = LiveHoldTrigger(key: .rightCommand)
        let startsOnTheModifierTap = trigger.backend is HotkeyHoldTrigger
        #expect(startsOnTheModifierTap)
        var events: [String] = []
        trigger.onHoldStart = { events.append("start") }
        trigger.onHoldEnd = { events.append("end") }

        trigger.update(.shortcut(optionSpace))
        guard let shortcut = trigger.backend as? ShortcutHoldTrigger else {
            Issue.record("a combination is not on the hot-key listener"); return
        }
        #expect(shortcut.shortcut == optionSpace)
        shortcut.edge(down: true, timestamp: 0)
        shortcut.edge(down: false, timestamp: 1)
        #expect(events == ["start", "end"])

        // Same kind: the same listener, on the new combination.
        let f13 = DictationKeyShortcut(keyCode: UInt16(kVK_F13), modifiers: [])
        trigger.update(.shortcut(f13))
        let sameListener = (trigger.backend as? ShortcutHoldTrigger) === shortcut
        #expect(sameListener)
        #expect(shortcut.shortcut == f13)

        trigger.update(.rightOption)
        let modifierCodes = (trigger.backend as? HotkeyHoldTrigger)?.hotkey.configuration.holdToTalkKeyCodes
        #expect(modifierCodes == [61])
    }

    @Test("a combination's presses reach the same callbacks")
    func shortcutEdges() {
        let trigger = LiveHoldTrigger(key: .shortcut(optionSpace))
        var events: [String] = []
        trigger.onHoldStart = { events.append("start") }
        trigger.onHoldEnd = { events.append("end") }
        trigger.onLatchStart = { events.append("latch") }
        guard let shortcut = trigger.backend as? ShortcutHoldTrigger else {
            Issue.record("a combination is not on the hot-key listener"); return
        }
        shortcut.edge(down: true, timestamp: 0)
        shortcut.edge(down: false, timestamp: 0.1)
        shortcut.edge(down: true, timestamp: 0.2)
        #expect(events == ["start", "end", "start", "latch"])
        // Stopping mid-press ends the hold.
        shortcut.stop()
        #expect(events.last == "end")
    }
}

// ─── Registration ───

@MainActor
@Suite("A recorded combination registered with macOS")
struct ShortcutHotKeyCenterTests {
    /// A combination nothing on a Mac uses.
    let unused = DictationKeyShortcut(keyCode: UInt16(kVK_F19), modifiers: [.control, .option, .shift, .command])

    @Test("one registration serves every listener, and the last to stop releases it")
    func sharedRegistration() throws {
        let center = ShortcutHotKeyCenter()
        #expect(center.canRegister(unused))
        #expect(center.registeredCount == 0, "probing must not keep the registration")
        let first = try #require(center.subscribe(unused) { _ in })
        let second = try #require(center.subscribe(unused) { _ in })
        #expect(center.registeredCount == 1)
        center.unsubscribe(first)
        #expect(center.registeredCount == 1, "a listener is still using it")
        center.unsubscribe(second)
        #expect(center.registeredCount == 0)
        #expect(center.canRegister(unused))
    }

    @Test("a hot-key press and release reach the listener through the process's one handler")
    func delivery() throws {
        let center = ShortcutHotKeyCenter()
        var edges: [Bool] = []
        let subscription = try #require(center.subscribe(unused) { edges.append($0) })
        defer { center.unsubscribe(subscription) }
        let id = try #require(center.hotKeyID(for: unused))
        for kind in [kEventHotKeyPressed, kEventHotKeyReleased] {
            var event: EventRef?
            #expect(CreateEvent(nil, OSType(kEventClassKeyboard), UInt32(kind), 0,
                                EventAttributes(kEventAttributeNone), &event) == noErr)
            var hotKey = EventHotKeyID(signature: 0x4572_6C74, id: id)
            #expect(SetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                      MemoryLayout<EventHotKeyID>.size, &hotKey) == noErr)
            SendEventToEventTarget(event, GetEventDispatcherTarget())
            ReleaseEvent(event)
        }
        #expect(edges == [true, false])
    }

    @Test("the combination listener registers on start and releases on stop")
    func triggerLifecycle() {
        let center = ShortcutHotKeyCenter()
        let trigger = ShortcutHoldTrigger(shortcut: unused, center: center)
        #expect(trigger.start())
        #expect(center.registeredCount == 1)
        #expect(trigger.start(), "a second start keeps the one registration")
        #expect(center.registeredCount == 1)
        trigger.stop()
        #expect(center.registeredCount == 0)
    }
}

// ─── The model ───

@MainActor
@Suite("The model with a recorded key")
struct HoldControlModelTests {

    private func enabled(_ harness: Harness, key: DictationHoldKey) async -> DictationModel {
        harness.settings.holdKey = key
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("typing during a combination's hold is not a shortcut: the words still go in")
    func typingDuringACombinationHold() async {
        let harness = Harness()
        let model = await enabled(harness, key: .shortcut(optionSpace))
        #expect(harness.trigger.keys.first == .shortcut(optionSpace))
        #expect(model.status.contains("⌥Space"))

        harness.trigger.press(flags: [])
        #expect(await eventually { model.recording })
        harness.keyWatch.type(8) // kVK_ANSI_C
        try? await Task.sleep(for: .milliseconds(100))
        #expect(model.recording, "a key typed during a combination's hold must not cancel it")
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("Escape still cancels a combination's hold")
    func escapeCancelsACombinationHold() async {
        let harness = Harness()
        let model = await enabled(harness, key: .shortcut(optionSpace))
        harness.trigger.press(flags: [])
        #expect(await eventually { model.recording })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && model.status.contains("cancelled") })
        harness.trigger.release()
        try? await Task.sleep(for: .milliseconds(100))
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("the combination's own modifier, held at the press, does not stop the hold as it would a modifier key's")
    func ownModifierHeld() async {
        let harness = Harness()
        let model = await enabled(harness, key: .shortcut(optionSpace))
        harness.trigger.press(flags: [.maskAlternate])
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
    }

    @Test("recording silences the live listener, and what was recorded becomes the key")
    func recordingChoosesTheKey() async {
        let harness = Harness()
        let model = await enabled(harness, key: .rightCommand)
        #expect(harness.trigger.running)

        #expect(model.beginRecordingHoldKey())
        #expect(model.recordingHoldKey)
        #expect(!harness.trigger.running)
        harness.trigger.press()
        try? await Task.sleep(for: .milliseconds(50))
        #expect(!model.recording, "the press being recorded must not dictate")
        harness.trigger.release()

        let recorded = DictationHoldKey.shortcut(optionSpace)
        model.endRecordingHoldKey(choosing: recorded)
        #expect(!model.recordingHoldKey)
        #expect(harness.trigger.running)
        #expect(model.holdKey == recorded)
        #expect(harness.settings.holdKey == recorded)
        #expect(harness.trigger.keys.last == recorded)
        #expect(model.caution?.contains("⌥Space") == true)
        #expect(model.status.contains("⌥Space"))
    }

    @Test("the mouse button does not dictate while a key is being recorded")
    func mouseIdleWhileRecording() async {
        let harness = Harness()
        harness.settings.mouseButton = .back
        let model = await enabled(harness, key: .rightCommand)
        #expect(model.beginRecordingHoldKey())
        harness.mouse.press()
        try? await Task.sleep(for: .milliseconds(50))
        #expect(!model.recording)
        harness.mouse.release()
        model.endRecordingHoldKey(choosing: nil)
        harness.mouse.press()
        #expect(await eventually { model.recording }, "after the recording the button works again")
        harness.mouse.release()
        #expect(await eventually { !model.busy })
    }

    @Test("a cancelled recording keeps the key and turns the listener back on")
    func cancelledRecording() async {
        let harness = Harness()
        let model = await enabled(harness, key: .rightOption)
        #expect(model.beginRecordingHoldKey())
        model.endRecordingHoldKey(choosing: nil)
        #expect(harness.trigger.running)
        #expect(model.holdKey == .rightOption)
        #expect(harness.settings.holdKey == .rightOption)
        model.endRecordingHoldKey(choosing: .shortcut(optionSpace))
        #expect(model.holdKey == .rightOption, "an end with no recording under way changes nothing")
    }

    @Test("no recording starts during a dictation")
    func noRecordingDuringADictation() async {
        let harness = Harness()
        let model = await enabled(harness, key: .rightCommand)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(!model.beginRecordingHoldKey())
        harness.trigger.release()
        #expect(await eventually { !model.busy })
    }

    @Test("a combination macOS will not reserve is refused before it is chosen")
    func unreservableCombination() {
        var harness = Harness()
        harness.reservesShortcuts = false
        let model = harness.model()
        #expect(model.refusalForRecordedHoldKey(.shortcut(optionSpace))?.contains("would not reserve ⌥Space") == true)
        #expect(model.refusalForRecordedHoldKey(.rightOption) == nil)
        harness.reservesShortcuts = true
        #expect(harness.model().refusalForRecordedHoldKey(.shortcut(optionSpace)) == nil)
    }

    @Test("a combination refused when dictation turns on says so, and asks for no permission")
    func combinationRefusedAtEnable() async {
        let harness = Harness()
        harness.settings.holdKey = .shortcut(optionSpace)
        harness.trigger.startResult = false
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        #expect(model.status.contains("would not reserve ⌥Space"))
        #expect(!model.reopenNeeded)
    }
}
#endif
