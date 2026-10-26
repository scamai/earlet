// The hold key (product decision D1): configurable, persisted in Dictation's
// own `ai.scam.dictation` domain, Right Command by default, and a caution
// physical-key caveats; observed conflicts have their own classifier tests.

#if canImport(Testing)
import CoreGraphics
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Hold key choice")
struct DictationHoldKeyTests {

    /// macOS `flagsChanged` keycodes, from the key itself rather than from the
    /// code under test.
    static let expectedKeyCodes: [DictationModifierKey: (Int64, CGEventFlags)] = [
        .rightCommand: (54, .maskCommand), .rightOption: (61, .maskAlternate),
        .leftOption: (58, .maskAlternate), .rightControl: (62, .maskControl),
        .leftControl: (59, .maskControl), .rightShift: (60, .maskShift),
        .function: (63, .maskSecondaryFn), .leftShift: (56, .maskShift),
    ]

    @Test("the default is Right Command, keycode 54, in the live trigger the app builds")
    func defaultIsRightCommand() {
        #expect(DictationHoldKey.standard == .rightCommand)
        let trigger = LiveHoldTrigger(key: .standard)
        guard let live = trigger.backend as? HotkeyHoldTrigger else {
            Issue.record("the default key is not on the modifier tap"); return
        }
        let config = live.hotkey.configuration
        #expect(config.holdToTalkKeyCodes == [54])
        #expect(config.holdToTalkFlagMask == .maskCommand)
    }

    @Test("the picker's keys are the seven of earlier versions, Left Command never")
    func presetModifiers() {
        let modifiers = DictationHoldKey.presets.compactMap(\.modifierKey)
        #expect(modifiers == [.rightCommand, .rightOption, .leftOption, .rightControl, .leftControl, .rightShift, .function])
        #expect(modifiers.count == DictationHoldKey.presets.count)
        #expect(DictationModifierKey(keyCode: 55) == nil, "Left Command is not a hold key")
    }

    @Test("a modifier key's tap configuration")
    func modifierTriggerConfiguration() {
        let trigger = HotkeyHoldTrigger(key: .rightCommand)
        let config = trigger.hotkey.configuration
        #expect(config.holdToTalkKeyCodes == [54])
        #expect(config.holdToTalkFlagMask == .maskCommand)
    }

    @Test("every choice listens to exactly its own physical key")
    func everyChoiceMapsToOneKey() {
        for key in DictationModifierKey.allCases {
            let config = key.hotkeyConfig
            guard let (code, mask) = Self.expectedKeyCodes[key] else {
                Issue.record("no expectation for \(key)"); continue
            }
            #expect(DictationModifierKey(keyCode: code) == key, "\(key)")
            #expect(config.holdToTalkKeyCodes == [code], "\(key)")
            #expect(config.holdToTalkFlagMask == mask, "\(key)")
        }
    }

    @Test("every choice double-taps that same physical key")
    func everyChoiceDoubleTapsItsOwnKey() {
        for key in DictationModifierKey.allCases {
            let config = key.hotkeyConfig
            guard let (code, mask) = Self.expectedKeyCodes[key] else {
                Issue.record("no expectation for \(key)"); continue
            }
            #expect(config.doubleTapWindow == DictationHoldKey.doubleTapWindow, "\(key)")
            #expect(config.doubleTapKeyCodes == [code], "\(key)")
            #expect(config.doubleTapFlagMask == mask, "\(key)")
            #expect(config.doubleTapFlagMask != HotkeyBinding.defaultDoubleTap.eventFlagMask || key.modifier == .control,
                    "\(key): the toggle follows the chosen key, not the global default")
        }
    }

    @Test("changing the key moves a live trigger to the new key")
    func updateReconfiguresTheLiveTrigger() {
        let trigger = HotkeyHoldTrigger(key: .rightCommand)
        trigger.update(.rightOption)
        #expect(trigger.hotkey.configuration.holdToTalkKeyCodes == [61])
        #expect(trigger.hotkey.configuration.doubleTapKeyCodes == [61])
        #expect(trigger.hotkey.configuration.doubleTapFlagMask == .maskAlternate)
    }

    @Test("physical-key caveats do not guess another app's gestures")
    func physicalKeyCaveats() {
        #expect(DictationHoldKey.standard.keyNote == nil)
        #expect(DictationHoldKey.rightOption.keyNote == nil)
        #expect(DictationHoldKey.rightControl.keyNote?.contains("no Right Control") == true)
        #expect(DictationHoldKey.function.keyNote?.contains("Keyboard settings") == true)
    }

}

@Suite("Dictation settings live in Dictation's own domain")
struct DictationSettingsTests {

    @Test("an empty or unreadable store gives the default key, and a choice round-trips")
    func holdKeyPersists() {
        let scratch = ScratchDefaults()
        let settings = DictationSettings(defaults: scratch.defaults)
        #expect(settings.holdKey == .rightCommand)
        settings.holdKey = .rightOption
        #expect(DictationSettings(defaults: scratch.defaults).holdKey == .rightOption)
        scratch.defaults.set("capsLock", forKey: DictationSettings.holdKeyKey)
        #expect(settings.holdKey == .rightCommand)
    }

    @Test("the domain and defaults key are product-owned")
    func domainIsDictations() {
        #expect(DictationSettings.domain == "ai.scam.dictation")
        #expect(DictationSettings.holdKeyKey == "dictation.holdKey")
        // Inside the bundle the domain is the standard defaults; anywhere
        // else it is opened by name.
        #expect(DictationSettings.defaultsForDomain(bundleIdentifier: "ai.scam.dictation") === UserDefaults.standard)
        #expect(DictationSettings.defaultsForDomain(bundleIdentifier: "com.example.runner") !== UserDefaults.standard)
    }
}

@MainActor
@Suite("The model follows the chosen hold key")
struct DictationModelHoldKeyTests {

    @Test("the model starts on the persisted key and builds its trigger with it")
    func persistedKeyIsUsed() async {
        let harness = Harness()
        harness.settings.holdKey = .leftOption
        let model = harness.model()
        #expect(model.holdKey == .leftOption)
        await model.toggle()
        #expect(harness.trigger.keys.first == .leftOption)
        #expect(model.status.contains("Left Option"))
    }

    @Test("choosing a key persists it, re-arms the live trigger and updates the caution")
    func selectingAKeyTakesEffect() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.caution == nil)

        model.selectHoldKey(.rightOption)
        #expect(harness.settings.holdKey == .rightOption)
        #expect(harness.trigger.keys.last == .rightOption)
        #expect(model.caution == nil)
        #expect(model.status.contains("Right Option"))

        model.selectHoldKey(.rightControl)
        #expect(model.caution?.contains("no Right Control") == true)
    }
}
#endif
