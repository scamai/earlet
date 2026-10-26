import Carbon.HIToolbox
import CoreGraphics
import DictationKit

/// Turns what the user presses while Settings records into a hold key.
///
/// - A modifier key pressed and released on its own is that key (Left
///   Command refused: it is part of nearly every shortcut).
/// - A key pressed with or without modifiers is a key combination, if
///   `DictationKeyShortcut.rejection` allows it.
/// - Escape on its own cancels.
///
/// Pure: the Settings view feeds it the window's own key events from a
/// local monitor, which sees nothing typed into other apps.
public struct DictationHoldKeyRecorder: Sendable, Equatable {
    public enum Input: Sendable, Equatable {
        case flagsChanged(keyCode: UInt16, flags: CGEventFlags)
        case keyDown(keyCode: UInt16, flags: CGEventFlags)
    }

    public typealias Outcome = DictationRecorded<DictationHoldKey>

    /// `kVK_Command`, which `DictationModifierKey` leaves out.
    static let leftCommandKeyCode: UInt16 = 55
    static let leftCommandRefusal =
        "Left Command is part of almost every shortcut. Choose Right Command, or hold Left Command with another key."

    /// The modifier key pressed alone, while it is still the only one down.
    private var loneModifier: UInt16?

    public init() {}

    public mutating func handle(_ input: Input) -> Outcome? {
        switch input {
        case .flagsChanged(let keyCode, let flags):
            return modifierChanged(keyCode: keyCode, flags: flags)
        case .keyDown(let keyCode, let flags):
            loneModifier = nil
            let modifiers = DictationKeyShortcut.Modifiers(flags)
            if keyCode == UInt16(kVK_Escape), modifiers.isEmpty { return .cancelled }
            let shortcut = DictationKeyShortcut(keyCode: keyCode, modifiers: modifiers)
            if let rejection = shortcut.rejection { return .rejected(rejection) }
            return .recorded(.shortcut(shortcut))
        }
    }

    private mutating func modifierChanged(keyCode: UInt16, flags: CGEventFlags) -> Outcome? {
        let held = flags.intersection(HoldChordGuard.shortcutModifiers)
        guard let mask = Self.mask(ofModifierKeyCode: keyCode) else {
            loneModifier = nil
            return nil
        }
        if let lone = loneModifier {
            guard lone == keyCode, !held.contains(mask) else {
                // A second modifier joined: this is the start of a
                // combination, which its key-down will record.
                loneModifier = nil
                return nil
            }
            loneModifier = nil
            guard held.isEmpty else { return nil }
            if keyCode == Self.leftCommandKeyCode { return .rejected(Self.leftCommandRefusal) }
            guard let key = DictationModifierKey(keyCode: Int64(keyCode)) else { return nil }
            return .recorded(.modifier(key))
        }
        // A press counts only when it is the one modifier down.
        if held == mask { loneModifier = keyCode }
        return nil
    }

    /// The flag a modifier key's `flagsChanged` carries, or nil for any
    /// other key code (Caps Lock included: it is a lock, not a held key).
    static func mask(ofModifierKeyCode keyCode: UInt16) -> CGEventFlags? {
        if keyCode == leftCommandKeyCode { return .maskCommand }
        return DictationModifierKey(keyCode: Int64(keyCode))?.binding.eventFlagMask
    }
}
