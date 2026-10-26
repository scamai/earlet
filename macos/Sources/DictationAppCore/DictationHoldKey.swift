import AppKit
import CoreGraphics
import DictationKit

// ─── Modifier keys ───

/// The single modifier keys Dictation can listen to for hold-to-talk.
///
/// `DictationHotkey`'s tap subscribes to `flagsChanged` and nothing else, so
/// these are seen without the product seeing the letters a user types. Left
/// Command is missing on purpose: it is part of nearly every shortcut, so
/// holding it to talk would open a recording at every ⌘C
/// (`DictationHoldKeyRecorder` refuses it with that reason).
public enum DictationModifierKey: String, CaseIterable, Identifiable, Sendable {
    case rightCommand
    case rightOption
    case leftOption
    case rightControl
    case leftControl
    case rightShift
    case function
    /// Only from the recorder; the picker offers Right Shift.
    case leftShift

    public var id: String { rawValue }

    var modifier: HotkeyModifier {
        switch self {
        case .rightCommand: return .command
        case .rightOption, .leftOption: return .option
        case .rightControl, .leftControl: return .control
        case .rightShift, .leftShift: return .shift
        case .function: return .function
        }
    }

    var side: HotkeyKeySide {
        switch self {
        case .rightCommand, .rightOption, .rightControl, .rightShift: return .right
        case .leftOption, .leftControl, .leftShift: return .left
        case .function: return .either
        }
    }

    public var binding: HotkeyBinding {
        HotkeyBinding(modifiers: [modifier], activation: .hold, side: side)
    }

    /// The `flagsChanged` keycode of the physical key.
    public var keyCode: Int64 {
        switch self {
        case .rightCommand: return 54
        case .rightOption: return 61
        case .leftOption: return 58
        case .rightControl: return 62
        case .leftControl: return 59
        case .rightShift: return 60
        case .function: return 63
        case .leftShift: return 56
        }
    }

    /// The modifier key whose `flagsChanged` keycode this is, Left Command
    /// excluded (`DictationHoldKeyRecorder` names it separately).
    public init?(keyCode: Int64) {
        guard let key = Self.allCases.first(where: { $0.keyCode == keyCode }) else { return nil }
        self = key
    }

    /// The toggle gesture on the same key: pressed twice quickly, dictation
    /// stays on until the next double-tap. Same modifier and same side as
    /// the hold; other applications are checked from observed key reactions.
    var doubleTapBinding: HotkeyBinding {
        HotkeyBinding(modifiers: [modifier], activation: .doubleTap, side: side)
    }

    /// The tap configuration for this key: hold it to talk, double-tap it
    /// to latch dictation on; once latched, one press of it (or Return)
    /// stops.
    ///
    /// The keycodes are set explicitly: the copied `Config(hold:)` leaves a
    /// binding without a side unfiltered, which for fn would accept any
    /// `flagsChanged` that happens to carry the fn flag.
    public var hotkeyConfig: DictationHotkey.Config {
        var config = DictationHotkey.Config(hold: binding, doubleTap: doubleTapBinding,
                                            doubleTapWindow: DictationHoldKey.doubleTapWindow)
        config.holdToTalkKeyCodes = [keyCode]
        config.doubleTapKeyCodes = [keyCode]
        return config
    }

    /// For sentences: "Hold Right Command…".
    public var name: String {
        switch self {
        case .rightCommand: return "Right Command"
        case .rightOption: return "Right Option"
        case .leftOption: return "Left Option"
        case .rightControl: return "Right Control"
        case .leftControl: return "Left Control"
        case .rightShift: return "Right Shift"
        case .function: return "fn (Globe)"
        case .leftShift: return "Left Shift"
        }
    }

    /// For the picker: the name plus the glyph printed on the key.
    public var label: String {
        switch self {
        case .function: return name
        default: return "\(name) (\(modifier.symbol))"
        }
    }

    /// For the keycap on Home: the side and the glyph printed on the key
    /// ("Right ⌘"), or "fn" for the Globe key, which has one key and no
    /// side. VoiceOver reads `name` instead.
    public var keycapLabel: String {
        switch self {
        case .function: return "fn"
        default: return "\(side == .left ? "Left" : "Right") \(modifier.symbol)"
        }
    }

    /// Something about the key itself worth knowing before choosing it.
    var keyNote: String? {
        switch self {
        case .rightCommand, .rightOption, .leftControl: return nil
        case .rightControl: return "Apple keyboards have no Right Control key."
        case .leftOption: return "Option is also held to type special characters."
        case .rightShift: return "Right Shift is also held to type capital letters."
        case .leftShift: return "Left Shift is also held to type capital letters."
        case .function:
            return "Depending on Keyboard settings, fn can also open the emoji picker or macOS dictation."
        }
    }
}

// ─── The hold control ───

/// What the user holds to dictate: a modifier key, or a key combination
/// they recorded. (A mouse button is a separate choice beside it,
/// `DictationMouseSideButton`.)
///
/// One gesture for both: hold to talk and release to insert, double-tap to
/// keep listening, press once more to stop. Each kind has its own listener
/// (`LiveHoldTrigger`), and each sees only its own key: modifier changes, or
/// the one registered combination.
public enum DictationHoldKey: Hashable, Identifiable, Sendable {
    case modifier(DictationModifierKey)
    case shortcut(DictationKeyShortcut)

    public static let rightCommand = DictationHoldKey.modifier(.rightCommand)
    public static let rightOption = DictationHoldKey.modifier(.rightOption)
    public static let leftOption = DictationHoldKey.modifier(.leftOption)
    public static let rightControl = DictationHoldKey.modifier(.rightControl)
    public static let leftControl = DictationHoldKey.modifier(.leftControl)
    public static let rightShift = DictationHoldKey.modifier(.rightShift)
    public static let function = DictationHoldKey.modifier(.function)

    /// Right Command: present on every Apple keyboard, built-in and Magic,
    /// on supported keyboards. Right Control, the first
    /// default, does not exist on Apple keyboards at all.
    public static let standard = rightCommand

    /// Two presses no more than this far apart, press to press, latch
    /// dictation on. The same for both kinds of key.
    public static let doubleTapWindow: TimeInterval = 0.35

    /// What the picker lists, in order. Anything else comes from the
    /// recorder and is listed after these while it is chosen.
    public static let presets: [DictationHoldKey] = [
        .rightCommand, .rightOption, .leftOption, .rightControl, .leftControl, .rightShift, .function,
    ]

    public var id: String { rawValue }

    // ─── Storage ───

    /// The stored form in `dictation.holdKey`: a modifier key's own name
    /// (the only form earlier versions wrote), or
    /// `shortcut:<key code>:<modifier flags>`.
    public var rawValue: String {
        switch self {
        case .modifier(let key): return key.rawValue
        case .shortcut(let shortcut): return "shortcut:\(shortcut.keyCode):\(shortcut.modifiers.rawValue)"
        }
    }

    /// Nil for anything unreadable or no longer allowed, which the settings
    /// read as the default key.
    public init?(rawValue: String) {
        if let key = DictationModifierKey(rawValue: rawValue) {
            self = .modifier(key)
            return
        }
        let parts = rawValue.split(separator: ":", omittingEmptySubsequences: false).map(String.init)
        switch (parts.first, parts.count) {
        case ("shortcut", 3):
            guard let code = UInt16(parts[1]), let flags = UInt32(parts[2]) else { return nil }
            let shortcut = DictationKeyShortcut(keyCode: code, modifiers: .init(rawValue: flags))
            guard shortcut.rejection == nil else { return nil }
            self = .shortcut(shortcut)
        default:
            return nil
        }
    }

    // ─── What the listener needs ───

    /// The modifier key, when this control is one.
    public var modifierKey: DictationModifierKey? {
        if case .modifier(let key) = self { return key }
        return nil
    }

    /// The modifier flag of the held key, for telling a dictation from the
    /// same key used in a shortcut (`HoldChordGuard`). Nil for a recorded
    /// combination: it is a shortcut of its own, not a key others are built
    /// on, so during its hold only Escape means anything.
    var chordMask: CGEventFlags? {
        modifierKey?.binding.eventFlagMask
    }

    // ─── Words ───

    /// For sentences: "Hold Right Command…", "Hold ⌥Space…".
    public var name: String {
        switch self {
        case .modifier(let key): return key.name
        case .shortcut(let shortcut): return shortcut.displayName
        }
    }

    /// For the picker.
    public var label: String {
        switch self {
        case .modifier(let key): return key.label
        case .shortcut: return name
        }
    }

    /// For the keycap on Home. VoiceOver reads `name` instead.
    public var keycapLabel: String {
        switch self {
        case .modifier(let key): return key.keycapLabel
        case .shortcut(let shortcut): return shortcut.displayName
        }
    }

    /// Something about the key itself worth knowing before choosing it.
    var keyNote: String? {
        switch self {
        case .modifier(let key):
            return key.keyNote
        case .shortcut(let shortcut):
            return "While Earlet is on, \(shortcut.displayName) only dictates: other apps no longer receive it."
        }
    }
}
