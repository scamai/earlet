import Foundation
import DictationKit

/// The mouse button Dictation can also listen to, beside the hold key.
///
/// The picker offers the two side buttons; "Record a Mouse Button…" takes
/// any extra button the mouse sends, up to CoreGraphics' last, for mice with
/// more than two. Left and right are the pointer, and the middle button
/// opens links in new tabs and starts autoscroll, so none of those three is
/// ever a choice. `DictationMouseButton` swallows the bound button, so its
/// usual action (Back, Forward, whatever the mouse's software gave it)
/// never fires underneath a dictation.
///
/// The raw values are the ids the Windows app saves for the same choice:
/// `button4` and `button5`, and on the Mac `button6` and up for the rest.
public struct DictationMouseSideButton: Hashable, Identifiable, Sendable {
    /// CoreGraphics numbering: 3 is the fourth button.
    public let buttonNumber: Int

    /// CoreGraphics numbers that can be chosen: the fourth button up to the
    /// last CoreGraphics reports.
    public static let choosable = 3...31

    public static let back = DictationMouseSideButton(validated: 3)
    public static let forward = DictationMouseSideButton(validated: 4)

    /// What the picker lists before any recorded button.
    public static let presets: [DictationMouseSideButton] = [.back, .forward]

    /// How long a press can last and still count as a click. A click keeps
    /// dictation running until the next press; a longer press is a hold,
    /// and its release inserts. The microphone opens on the press either
    /// way, so waiting to tell the two apart never clips a first word.
    public static let clickWindow: TimeInterval = 0.3

    /// How soon after a click's release another press is the second half of
    /// a double-click, which keeps dictation running as the click did,
    /// rather than the press that stops it. The hold key latches on a
    /// double-tap, so a double-click is what people try first.
    public static let doubleClickWindow: TimeInterval = DictationHoldKey.doubleTapWindow

    private init(validated buttonNumber: Int) {
        self.buttonNumber = buttonNumber
    }

    /// Nil outside `choosable`.
    public init?(buttonNumber: Int) {
        guard Self.choosable.contains(buttonNumber) else { return nil }
        self.buttonNumber = buttonNumber
    }

    /// `button4`, `button5`, … as the mouse numbers them. Nil for anything
    /// else, which the settings read as no button.
    public init?(rawValue: String) {
        guard rawValue.hasPrefix("button"), let number = Int(rawValue.dropFirst("button".count)),
              String(number) == rawValue.dropFirst("button".count) else { return nil }
        self.init(buttonNumber: number - 1)
    }

    public var rawValue: String { "button\(buttonNumber + 1)" }
    public var id: String { rawValue }

    public var binding: MouseButtonBinding { MouseButtonBinding(buttonNumber: buttonNumber) }

    /// For sentences: "Release mouse button 4 to insert."
    public var name: String { binding.spokenString }

    /// For the picker: the number printed in the mouse's own settings, and
    /// for the side buttons the job they usually do.
    public var label: String {
        switch buttonNumber {
        case 3: return "Mouse button 4 (Back)"
        case 4: return "Mouse button 5 (Forward)"
        default: return "Mouse button \(buttonNumber + 1)"
        }
    }

    /// What choosing it costs, said where it is chosen: never nothing,
    /// because a chosen button always gives up its own job.
    var caution: String {
        switch buttonNumber {
        case 3: return "While it is chosen, mouse button 4 no longer goes Back in browsers or Finder."
        case 4: return "While it is chosen, mouse button 5 no longer goes Forward in browsers or Finder."
        default: return "While it is chosen, mouse button \(buttonNumber + 1) no longer does what your mouse's software or other apps give it."
        }
    }

    /// What a click on CoreGraphics button `buttonNumber` means while
    /// Settings records the mouse button. Nil for left and right click,
    /// which stay the pointer's so the window can still be used.
    public static func recorded(buttonNumber: Int) -> DictationRecorded<DictationMouseSideButton>? {
        switch buttonNumber {
        case 0, 1:
            return nil
        case 2:
            return .rejected("The middle button opens links in new tabs and starts autoscroll, so Earlet leaves it alone. Click a side or extra button.")
        default:
            guard let button = DictationMouseSideButton(buttonNumber: buttonNumber) else { return nil }
            return .recorded(button)
        }
    }
}

/// What a recording in Settings came to.
public enum DictationRecorded<Value: Sendable & Equatable>: Sendable, Equatable {
    case recorded(Value)
    case rejected(String)
    case cancelled
}
