import Foundation

/// A user-chosen mouse button as a dictation trigger.
///
/// A separate type from `HotkeyBinding` rather than a case added to it: a
/// mouse button is a single discrete control (no modifier combination, no
/// "hold vs double-tap" choice to make — the tap only ever treats a mouse
/// button as hold-to-talk), and mixing the two shapes into one type would
/// force every keyboard call site to reason about a `buttonNumber` that
/// never applies to it.
public struct MouseButtonBinding: Codable, Sendable, Equatable {

    /// CG button numbering: 0 left, 1 right, 2 middle, 3 = "Button 4", up to
    /// CGEvent's own ceiling of 31. Stored as the raw CG number (not the
    /// display number) because that is what the event tap hands back and
    /// what has to be compared against on every click — keeping storage and
    /// comparison in the same units means there is nowhere to introduce an
    /// off-by-one.
    public var buttonNumber: Int

    public init(buttonNumber: Int) {
        self.buttonNumber = buttonNumber
    }

    /// The tap subscribes to `.otherMouseDown`/`.otherMouseUp` only, which
    /// structurally excludes left (0) and right (1) — those arrive as
    /// `.leftMouseDown`/`.rightMouseDown` and are never seen. 31 is CGEvent's
    /// own ceiling for `kCGMouseEventButtonNumber`. Binding left or right
    /// would therefore save a control that can never fire, and if the tap
    /// mask ever widened to see them, swallowing a left click would brick
    /// the machine's pointer — so the range is closed at the type level
    /// rather than left to whichever call site remembers to check.
    public static let bindableRange = 2...31

    /// Why this binding cannot be saved, or nil when it can.
    public var rejection: String? {
        Self.bindableRange.contains(buttonNumber)
            ? nil
            : "Only side or extra mouse buttons can be bound — not left or right click."
    }

    /// The everyday action this binding would swallow while armed.
    ///
    /// Every bindable button gets one, never nil: binding a mouse button
    /// always takes over its default OS/app behavior, so there is no
    /// "harmless" button to skip warning about. The middle button gets its
    /// own text because its usual jobs (closing browser tabs, starting
    /// autoscroll) are specific and worth naming, unlike the generic
    /// back/forward side buttons where one shared sentence covers all of
    /// them.
    public var caution: String? {
        guard rejection == nil else { return nil }
        if buttonNumber == 2 {
            return "The middle button often closes tabs and starts autoscroll. "
                + "While bound, those stop working."
        }
        return "While bound, this button no longer does its usual action "
            + "(like Back or Forward in your browser) — dictation uses it instead."
    }

    /// Human numbering = CG + 1 — what the mouse's own labeling and Chrome's
    /// own settings call the button. Off by one here ships a settings row
    /// that names a button the user's hand disagrees with.
    public var displayString: String { "Button \(buttonNumber + 1)" }

    /// Spoken form for the status line, where a bare number reads poorly.
    public var spokenString: String { "mouse button \(buttonNumber + 1)" }
}
