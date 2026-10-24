import CoreGraphics
import Foundation
import DictationKit

/// The stop keys (Space, Return, keypad Enter) that end a latched dictation.
///
/// The passive `DictationKeyWatch` observes and cannot consume — its NSEvent
/// monitors never can — so a stop key seen there would still reach the
/// focused app: a Space types into the text, a Return adds a newline, or in
/// a chat app sends the half-written message, before the transcribed text
/// lands. This trap is the ACTIVE sibling (`DictationMouseButton` is the
/// precedent): installed only while a latch records, it swallows the stop
/// keys' down AND up — the up half so the focused app never sees a release
/// for a press it never got — and reports the stop. Every other event
/// passes through untouched.
@MainActor
public protocol DictationLatchStopTrap: AnyObject {
    /// Installs the trap; `onStopKey` fires on the main actor for the down of
    /// a stop key, which is consumed. Returns false when the tap could not
    /// be created (Input Monitoring missing): the latch still works, the key
    /// just types normally.
    @discardableResult
    func begin(_ onStopKey: @escaping @MainActor () -> Void) -> Bool
    func end()
}

/// The trap's swallow table, pure and unisolated so a test can pin it
/// without a live tap (which needs the Input Monitoring grant and cannot
/// run in CI) and the tap's C callback can ask it synchronously. Only the
/// stop keys' down AND up are consumed — the up half so the focused app
/// never sees a release for a press it never got. Everything else,
/// including the tap-disable notifications that arrive through the same
/// callback, passes; the disable branch deals with those first.
public enum LatchStopKeys {
    /// `kVK_Space`.
    public static let spaceKeyCode: Int64 = 49
    /// `kVK_Return`.
    public static let returnKeyCode: Int64 = 36
    /// `kVK_ANSI_KeypadEnter`.
    public static let keypadEnterKeyCode: Int64 = 76

    public static func consumes(type: CGEventType, keyCode: Int64) -> Bool {
        guard type == .keyDown || type == .keyUp else { return false }
        return keyCode == spaceKeyCode || keyCode == returnKeyCode || keyCode == keypadEnterKeyCode
    }
}

/// The live trap: one `CGEventTap` on `keyDown`/`keyUp`, active so its
/// callback can return nil to consume. All state lives on the main thread —
/// the tap's run-loop source is added to the main run loop, so the callback
/// is delivered there, which `MainActor.assumeIsolated` states.
///
/// `@unchecked Sendable` only for the C callback's `Unmanaged` capture, the
/// same shape as `DictationHotkey`/`DictationMouseButton`.
@MainActor
public final class CGEventLatchStopTrap: DictationLatchStopTrap, @unchecked Sendable {
    private var tap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var handler: (@MainActor () -> Void)?

    public init() {}

    @discardableResult
    public func begin(_ onStopKey: @escaping @MainActor () -> Void) -> Bool {
        end()
        handler = onStopKey
        return createTap()
    }

    public func end() {
        EventTapTeardown.invalidate(tap: tap, runLoopSource: runLoopSource)
        tap = nil
        runLoopSource = nil
        handler = nil
    }

    private func createTap() -> Bool {
        let status = DictationHotkey.permissionStatus()
        guard status.isGranted else { return false }
        let mask: CGEventMask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue)
        let selfPointer = Unmanaged.passUnretained(self).toOpaque()
        guard let created = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                guard let refcon else { return Unmanaged.passUnretained(event) }
                let trap = Unmanaged<CGEventLatchStopTrap>.fromOpaque(refcon).takeUnretainedValue()
                return trap.handleTapEvent(type: type, event: event)
            },
            userInfo: selfPointer
        ) else {
            // The grant raced the check, or the OS refused: no trap, no
            // crash — the caller logs that the stop keys type normally this
            // latch.
            return false
        }
        tap = created
        let source = CFMachPortCreateRunLoopSource(nil, created, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: created, enable: true)
        return true
    }

    private func handleTapEvent(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            // Self-heal once, and never retry-loop: if the grant was revoked
            // this keeps failing silently on the OS side, which is correct
            // (DictationHotkey.handleTapEvent's contract, copied).
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        guard LatchStopKeys.consumes(type: type, keyCode: keyCode) else {
            return Unmanaged.passUnretained(event)
        }
        if type == .keyDown {
            MainActor.assumeIsolated { handler?() }
        }
        return nil
    }
}
