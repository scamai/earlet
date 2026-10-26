import Carbon.HIToolbox
import CoreGraphics
import Foundation

/// A key combination the user recorded as their hold control: one
/// non-modifier key plus the modifiers held with it, such as ⌥Space, ⌃⌥D
/// or F13.
///
/// macOS registers it as a hot key (`ShortcutHotKeyCenter`), so the app is
/// told about that combination alone and never sees other keys. While
/// Earlet is on, other apps no longer receive the combination.
public struct DictationKeyShortcut: Hashable, Sendable {
    /// The modifiers held with the key, in Carbon's hot-key bits so the
    /// stored value is what `RegisterEventHotKey` takes.
    public struct Modifiers: OptionSet, Hashable, Sendable {
        public let rawValue: UInt32
        public init(rawValue: UInt32) { self.rawValue = rawValue }

        public static let command = Modifiers(rawValue: UInt32(cmdKey))
        public static let shift = Modifiers(rawValue: UInt32(shiftKey))
        public static let option = Modifiers(rawValue: UInt32(optionKey))
        public static let control = Modifiers(rawValue: UInt32(controlKey))

        static let all: Modifiers = [.command, .shift, .option, .control]

        /// From an event's flags; fn, Caps Lock and the device bits are not
        /// part of a hot key.
        public init(_ flags: CGEventFlags) {
            var modifiers: Modifiers = []
            if flags.contains(.maskCommand) { modifiers.insert(.command) }
            if flags.contains(.maskShift) { modifiers.insert(.shift) }
            if flags.contains(.maskAlternate) { modifiers.insert(.option) }
            if flags.contains(.maskControl) { modifiers.insert(.control) }
            self = modifiers
        }

        /// In the order macOS menus print them: ⌃⌥⇧⌘.
        var symbols: String {
            var text = ""
            if contains(.control) { text += "⌃" }
            if contains(.option) { text += "⌥" }
            if contains(.shift) { text += "⇧" }
            if contains(.command) { text += "⌘" }
            return text
        }
    }

    /// The virtual key code (`kVK_*`).
    public var keyCode: UInt16
    public var modifiers: Modifiers

    public init(keyCode: UInt16, modifiers: Modifiers) {
        self.keyCode = keyCode
        self.modifiers = modifiers.intersection(.all)
    }

    // ─── What can be recorded ───

    /// F1 to F20: keys with no typing job, so they may stand alone.
    static let functionKeyCodes: [UInt16: String] = [
        UInt16(kVK_F1): "F1", UInt16(kVK_F2): "F2", UInt16(kVK_F3): "F3", UInt16(kVK_F4): "F4",
        UInt16(kVK_F5): "F5", UInt16(kVK_F6): "F6", UInt16(kVK_F7): "F7", UInt16(kVK_F8): "F8",
        UInt16(kVK_F9): "F9", UInt16(kVK_F10): "F10", UInt16(kVK_F11): "F11", UInt16(kVK_F12): "F12",
        UInt16(kVK_F13): "F13", UInt16(kVK_F14): "F14", UInt16(kVK_F15): "F15", UInt16(kVK_F16): "F16",
        UInt16(kVK_F17): "F17", UInt16(kVK_F18): "F18", UInt16(kVK_F19): "F19", UInt16(kVK_F20): "F20",
    ]

    /// The modifier keys' own codes: they arrive as `flagsChanged`, and a
    /// hot key cannot be a modifier alone.
    static let modifierKeyCodes: Set<UInt16> = [54, 55, 56, 57, 58, 59, 60, 61, 62, 63]

    /// Why this combination cannot be the hold control, or nil when it can.
    public var rejection: String? {
        if Self.modifierKeyCodes.contains(keyCode) {
            return "A modifier key on its own is chosen by pressing and releasing it alone."
        }
        if keyCode == UInt16(kVK_Escape) {
            return "Escape cancels a dictation, so it cannot start one."
        }
        let holdsATypingModifier = !modifiers.intersection([.command, .option, .control]).isEmpty
        if !holdsATypingModifier, Self.functionKeyCodes[keyCode] == nil {
            return "On its own, \(Self.keyName(keyCode)) is needed for typing. Hold Command, Option or Control with it, or use a function key."
        }
        return nil
    }

    // ─── Words ───

    /// "⌥Space", "⌃⌥D", "F13".
    public var displayName: String {
        modifiers.symbols + Self.keyName(keyCode)
    }

    /// Keys whose printed name is not a character.
    static let namedKeys: [UInt16: String] = [
        UInt16(kVK_Space): "Space", UInt16(kVK_Return): "Return", UInt16(kVK_Tab): "Tab",
        UInt16(kVK_Delete): "Delete", UInt16(kVK_ForwardDelete): "Forward Delete",
        UInt16(kVK_Escape): "Escape", UInt16(kVK_Help): "Help",
        UInt16(kVK_Home): "Home", UInt16(kVK_End): "End",
        UInt16(kVK_PageUp): "Page Up", UInt16(kVK_PageDown): "Page Down",
        UInt16(kVK_LeftArrow): "←", UInt16(kVK_RightArrow): "→",
        UInt16(kVK_UpArrow): "↑", UInt16(kVK_DownArrow): "↓",
        UInt16(kVK_ANSI_KeypadEnter): "Keypad Enter", UInt16(kVK_ANSI_KeypadClear): "Keypad Clear",
        UInt16(kVK_ANSI_KeypadDecimal): "Keypad .", UInt16(kVK_ANSI_KeypadMultiply): "Keypad *",
        UInt16(kVK_ANSI_KeypadPlus): "Keypad +", UInt16(kVK_ANSI_KeypadDivide): "Keypad /",
        UInt16(kVK_ANSI_KeypadMinus): "Keypad -", UInt16(kVK_ANSI_KeypadEquals): "Keypad =",
        UInt16(kVK_ANSI_Keypad0): "Keypad 0", UInt16(kVK_ANSI_Keypad1): "Keypad 1",
        UInt16(kVK_ANSI_Keypad2): "Keypad 2", UInt16(kVK_ANSI_Keypad3): "Keypad 3",
        UInt16(kVK_ANSI_Keypad4): "Keypad 4", UInt16(kVK_ANSI_Keypad5): "Keypad 5",
        UInt16(kVK_ANSI_Keypad6): "Keypad 6", UInt16(kVK_ANSI_Keypad7): "Keypad 7",
        UInt16(kVK_ANSI_Keypad8): "Keypad 8", UInt16(kVK_ANSI_Keypad9): "Keypad 9",
    ]

    /// The key's name: a named key, a function key, else the character the
    /// current keyboard layout prints on it ("D" on a US layout, "Ö" on a
    /// German one).
    static func keyName(_ keyCode: UInt16) -> String {
        if let name = namedKeys[keyCode] ?? functionKeyCodes[keyCode] { return name }
        if let character = layoutCharacter(keyCode) { return character.uppercased() }
        return "Key \(keyCode)"
    }

    /// The character on the key in the current keyboard layout. The input
    /// source API is main-thread only, so elsewhere this answers nil and the
    /// name falls back to the key code.
    private static func layoutCharacter(_ keyCode: UInt16) -> String? {
        guard Thread.isMainThread,
              let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let pointer = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else {
            return nil
        }
        let data = Unmanaged<CFData>.fromOpaque(pointer).takeUnretainedValue() as Data
        var deadKeys: UInt32 = 0
        var length = 0
        var characters = [UniChar](repeating: 0, count: 4)
        let status = data.withUnsafeBytes { bytes -> OSStatus in
            guard let layout = bytes.baseAddress?.assumingMemoryBound(to: UCKeyboardLayout.self) else {
                return OSStatus(paramErr)
            }
            return UCKeyTranslate(layout, keyCode, UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                                  OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeys,
                                  characters.count, &length, &characters)
        }
        guard status == noErr, length > 0 else { return nil }
        let text = String(utf16CodeUnits: characters, count: length)
        return text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : text
    }
}
