import CoreGraphics
import Foundation

/// A modifier key, in the one representation that is stable across a
/// relaunch.
///
/// Deliberately NOT `CGEventFlags` on disk. `CGEventFlags` is an OS-defined
/// bitfield: persisting its `rawValue` would store a number whose meaning
/// belongs to Apple, and the device-dependent bits (`maskNonCoalesced`, the
/// left/right discriminator bits) ride in the same word. A user's saved
/// hotkey must not be able to change meaning because a different bit got set
/// on a different keyboard.
public enum HotkeyModifier: String, Codable, Sendable, CaseIterable {
    case command
    case option
    case control
    case shift
    case function

    /// The mask to test an incoming `CGEventFlags` against.
    ///
    /// These are the SIDE-AGNOSTIC masks: `.maskAlternate` matches Option on
    /// either side of the keyboard. That is intentional and worth stating,
    /// because the shipped default's own comment claimed "right Option" for
    /// months while the code has always accepted both — the kind of drift
    /// between a description and its behaviour this module exists to stop.
    public var eventFlag: CGEventFlags {
        switch self {
        case .command: return .maskCommand
        case .option: return .maskAlternate
        case .control: return .maskControl
        case .shift: return .maskShift
        case .function: return .maskSecondaryFn
        }
    }

    /// Glyph as macOS itself renders it in menus.
    public var symbol: String {
        switch self {
        case .command: return "\u{2318}"
        case .option: return "\u{2325}"
        case .control: return "\u{2303}"
        case .shift: return "\u{21E7}"
        case .function: return "fn"
        }
    }

    public var displayName: String {
        switch self {
        case .command: return "Command"
        case .option: return "Option"
        case .control: return "Control"
        case .shift: return "Shift"
        case .function: return "Function"
        }
    }

    /// Stable order for display, so "⌃⌥" never renders as "⌥⌃".
    /// Matches Apple's own menu ordering.
    public var sortOrder: Int {
        switch self {
        case .control: return 0
        case .option: return 1
        case .shift: return 2
        case .command: return 3
        case .function: return 4
        }
    }
}

/// How a modifier set activates dictation.
public enum HotkeyActivation: String, Codable, Sendable, CaseIterable {
    /// Hold the modifiers; dictation runs until release. Push-to-talk.
    case hold
    /// Tap the modifiers twice quickly; dictation toggles on, then off.
    case doubleTap

    public var verb: String {
        switch self {
        case .hold: return "Hold"
        case .doubleTap: return "Double-tap"
        }
    }
}

/// A user-chosen hotkey: which modifiers, and how they activate.
///
/// MODIFIERS ONLY, by design rather than by omission. The tap subscribes to
/// `CGEventType.flagsChanged` and nothing else, so the app is *structurally*
/// unable to observe the letters you type — not merely programmed not to.
/// Supporting a plain-key binding (F13, ⌘⇧D) would require adding `keyDown`
/// to the tap mask, at which point a dictation app that already inserts text
/// would also be watching every keystroke everywhere. That is a materially
/// stronger privacy claim and is not made here; if it is ever added it needs
/// its own disclosed opt-in, not a quiet widening of this type.
/// Which physical key of a modifier pair a binding wants.
///
/// macOS reports both Options through one `CGEventFlags.maskAlternate` bit, so
/// a flags-only binding cannot say "the right one" — and a flags-only diagnostic printed
/// "Hold Right Option" for years over a tap that fired on either. The keycode
/// rides along on the same `flagsChanged` event, so honouring a side costs
/// nothing: the tap still subscribes to modifier events only and still cannot
/// see a character.
public enum HotkeyKeySide: String, Codable, Sendable, Equatable {
    /// Either physical key. What every binding meant before sides existed, and
    /// what a saved binding decodes to, so nobody's setting changes under them.
    case either
    case left
    case right

    /// The keycodes macOS reports for each modifier, by side. `flagsChanged`
    /// carries these in `.keyboardEventKeycode`.
    static func keyCodes(for modifier: HotkeyModifier, side: HotkeyKeySide) -> Set<Int64> {
        let pair: (left: Int64, right: Int64)
        switch modifier {
        case .command: pair = (55, 54)
        case .shift: pair = (56, 60)
        case .option: pair = (58, 61)
        case .control: pair = (59, 62)
        case .function:
            // There is exactly one Fn key. A side on it is not a preference
            // that cannot be honoured, it is a question with no referent, so
            // it matches whatever was asked rather than pretending to filter.
            return [63]
        }
        switch side {
        case .either: return [pair.left, pair.right]
        case .left: return [pair.left]
        case .right: return [pair.right]
        }
    }

    var displayPrefix: String {
        switch self {
        case .either: return ""
        case .left: return "Left "
        case .right: return "Right "
        }
    }
}

public struct HotkeyBinding: Codable, Sendable, Equatable {

    public var modifiers: Set<HotkeyModifier>
    public var activation: HotkeyActivation

    /// Which physical key, when the binding is a single modifier.
    ///
    /// Only meaningful for a one-modifier binding: a COMBINATION like ⌃⌥ is
    /// already specific enough that asking which ⌥ would be pedantry, and the
    /// keycode stream only ever names the key that just changed, so a
    /// two-sided combination could not be checked from one event anyway.
    public var side: HotkeyKeySide

    public init(
        modifiers: Set<HotkeyModifier>,
        activation: HotkeyActivation,
        side: HotkeyKeySide = .either
    ) {
        self.modifiers = modifiers
        self.activation = activation
        self.side = side
    }

    private enum CodingKeys: String, CodingKey {
        case modifiers, activation, side
    }

    // Decodes an older binding, which had no side, as `.either` — the
    // behaviour it actually had. A saved hotkey must not change meaning
    // because the app updated.
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        modifiers = try c.decode(Set<HotkeyModifier>.self, forKey: .modifiers)
        activation = try c.decode(HotkeyActivation.self, forKey: .activation)
        side = try c.decodeIfPresent(HotkeyKeySide.self, forKey: .side) ?? .either
    }

    /// The shipped push-to-talk default: hold the RIGHT Option.
    ///
    /// Right, not either, and not Shift. The binding is intentionally explicit,
    /// the reasoning is worth keeping: Shift collides with capitalisation —
    /// a person who holds it to type a capital would start a recording — and
    /// the left-hand modifiers are the ones every editor shortcut already
    /// uses. The right-hand key of a pair is the one a keyboard gives you for
    /// free.
    ///
    /// Windows reached the same physical key from the other direction and then
    /// left it: it moved off Right Alt because non-US layouts label that key
    /// AltGr and use it to type €, @ and friends. That risk is real on a
    /// non-US keyboard and it is why the picker exists — but it does not
    /// transfer to macOS, where ⌥ is already the dead-key modifier on every
    /// layout and holding it is not how anyone types a character.
    public static let defaultHold = HotkeyBinding(
        modifiers: [.option], activation: .hold, side: .right)
    /// The shipped toggle default: double-tap Control.
    public static let defaultDoubleTap = HotkeyBinding(modifiers: [.control], activation: .doubleTap)

    /// Mask to test against a live `CGEventFlags`.
    ///
    /// `CGEventFlags.contains(_:)` is an OptionSet superset test, so a
    /// two-modifier binding fires only while BOTH are down — which is what
    /// makes combinations safe to bind even when each key alone is common.
    public var eventFlagMask: CGEventFlags {
        modifiers.reduce(into: CGEventFlags()) { $0.insert($1.eventFlag) }
    }

    /// Modifiers in Apple's menu order.
    public var orderedModifiers: [HotkeyModifier] {
        modifiers.sorted { $0.sortOrder < $1.sortOrder }
    }

    /// "⌥" / "⌃⌥" — the glyph string alone.
    public var symbolString: String {
        orderedModifiers.map(\.symbol).joined()
    }

    /// "Hold ⌥" / "Double-tap ⌃⌥" — what the tab shows.
    public var displayString: String {
        guard !modifiers.isEmpty else { return "Not set" }
        return "\(activation.verb) \(symbolString)"
    }

    /// Spoken form for the status line, where glyphs alone read poorly and a
    /// screen reader would announce nothing useful: "Hold Option".
    public var spokenString: String {
        guard !modifiers.isEmpty else { return "no hotkey set" }
        let names = orderedModifiers.map(\.displayName)
        let joined: String
        switch names.count {
        case 1: joined = names[0]
        case 2: joined = "\(names[0]) and \(names[1])"
        default: joined = names.dropLast().joined(separator: ", ") + ", and " + names[names.count - 1]
        }
        return "\(activation.verb) \(joined)"
    }

    public var isEmpty: Bool { modifiers.isEmpty }

    /// The everyday action that would ALSO fire this binding, or nil when
    /// there isn't one.
    ///
    /// Returns the specific collision rather than a generic "this may
    /// conflict", because the specific one is checkable by the user in two
    /// seconds and the generic one is not. A hold-binding on Shift alone
    /// starts dictation on every capital letter — a bug report that would
    /// arrive as "dictation turns itself on randomly" and be near-impossible
    /// to connect back to this screen.
    public var caution: String? {
        guard !modifiers.isEmpty else { return nil }
        guard modifiers.count == 1, let only = modifiers.first else { return nil }
        switch (only, activation) {
        case (.shift, .hold):
            return "Shift is held every time you type a capital letter, so this would start dictation constantly."
        case (.shift, .doubleTap):
            return "Two capital letters in a row would toggle dictation."
        case (.command, .hold):
            return "Command is held for every shortcut — \u{2318}C, \u{2318}Tab — so this would start dictation constantly."
        case (.command, .doubleTap):
            return "Command is used for every shortcut, so this may toggle dictation while you work."
        case (.control, .hold):
            return "Control is used by some apps for text shortcuts, so this may start dictation unexpectedly."
        case (.function, .hold):
            return "fn is held for the function keys, the arrow shortcuts and forward delete, "
                + "so this would start dictation constantly."
        case (.function, .doubleTap):
            return "The fn / \u{1F310} key usually already has a system action in Keyboard settings "
                + "\u{2014} often macOS's own dictation. Check there if both react."
        default:
            return nil
        }
    }

    /// Why this binding cannot be saved, or nil when it can.
    ///
    /// Separate from `caution`: a rejection blocks, a caution informs. The
    /// only hard rejection that does not depend on the other binding is an
    /// empty set — a hotkey matching no modifiers would match `CGEventFlags()`
    /// on every event and hold dictation permanently open.
    public var rejection: String? {
        modifiers.isEmpty ? "Press at least one modifier key." : nil
    }

    /// Whether two bindings would fight each other.
    ///
    /// Identical modifier sets collide even across activation modes: holding
    /// a key long enough to push-to-talk also produces the down/up pair a
    /// double-tap detector counts, so the same set bound to both would toggle
    /// on the second push-to-talk. The existing `Config` comment already
    /// required these be different keys; this makes it checkable instead of
    /// advisory.
    public static func collide(_ a: HotkeyBinding, _ b: HotkeyBinding) -> Bool {
        !a.modifiers.isEmpty && a.modifiers == b.modifiers
    }
}
