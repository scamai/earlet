import CoreGraphics

/// Tells a dictation hold from the same key used in a shortcut.
///
/// The hold key is an ordinary modifier, so Right Command + C must copy, not
/// dictate. Once another modifier or any key goes down while the hold key is
/// held, the press was a shortcut and the recording is dropped without
/// inserting. Escape is the one key with its own meaning: cancel.
///
/// Pure: no tap, no monitor, no clock. `DictationModel` feeds it the
/// `flagsChanged` flags from the tap and the key-down codes from a key watch
/// that exists only while a hold is in progress.
public struct HoldChordGuard: Sendable, Equatable {
    public enum Verdict: Sendable, Equatable {
        case none
        case escape
        case chord
    }

    /// `kVK_Escape`.
    public static let escapeKeyCode: UInt16 = 53

    /// The modifiers that make a shortcut. Caps Lock and the device-specific
    /// bits are left out: neither is a key the user is holding down.
    static let shortcutModifiers: CGEventFlags = [
        .maskCommand, .maskAlternate, .maskControl, .maskShift, .maskSecondaryFn,
    ]

    private var holdMask: CGEventFlags = []
    public private(set) var isActive = false

    public init() {}

    /// Starts watching a hold. Returns `false` when another modifier is
    /// already down: then the hold key completed a shortcut the user had
    /// started, and no recording should begin at all.
    public mutating func begin(holdMask: CGEventFlags, flags: CGEventFlags) -> Bool {
        guard others(in: flags, besides: holdMask).isEmpty else { return false }
        self.holdMask = holdMask
        isActive = true
        return true
    }

    public mutating func modifiersChanged(_ flags: CGEventFlags) -> Verdict {
        guard isActive else { return .none }
        return others(in: flags, besides: holdMask).isEmpty ? .none : .chord
    }

    public mutating func keyDown(_ keyCode: UInt16) -> Verdict {
        guard isActive else { return .none }
        return keyCode == Self.escapeKeyCode ? .escape : .chord
    }

    public mutating func end() {
        isActive = false
    }

    private func others(in flags: CGEventFlags, besides holdMask: CGEventFlags) -> CGEventFlags {
        flags.intersection(Self.shortcutModifiers).subtracting(holdMask)
    }
}
