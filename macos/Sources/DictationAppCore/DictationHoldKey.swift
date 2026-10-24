import AppKit
import CoreGraphics
import DictationKit

/// The keys Dictation can listen to for hold-to-talk.
///
/// Single modifier keys only. `DictationHotkey`'s tap subscribes to
/// `flagsChanged` and nothing else, so the product cannot see the letters a
/// user types; binding a letter or function key would need a key-down tap,
/// which is a different privacy claim.
public enum DictationHoldKey: String, CaseIterable, Identifiable, Sendable {
    case rightCommand
    case rightOption
    case leftOption
    case rightControl
    case leftControl
    case rightShift
    case function

    /// Right Command: present on every Apple keyboard, built-in and Magic,
    /// on supported keyboards. Right Control, the first
    /// default, does not exist on Apple keyboards at all.
    public static let standard: DictationHoldKey = .rightCommand

    public var id: String { rawValue }

    var modifier: HotkeyModifier {
        switch self {
        case .rightCommand: return .command
        case .rightOption, .leftOption: return .option
        case .rightControl, .leftControl: return .control
        case .rightShift: return .shift
        case .function: return .function
        }
    }

    var side: HotkeyKeySide {
        switch self {
        case .rightCommand, .rightOption, .rightControl, .rightShift: return .right
        case .leftOption, .leftControl: return .left
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
        }
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
        var config = DictationHotkey.Config(hold: binding, doubleTap: doubleTapBinding)
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
        case .function:
            return "Depending on Keyboard settings, fn can also open the emoji picker or macOS dictation."
        }
    }

}
