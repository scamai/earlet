import Foundation
import CoreGraphics
import ApplicationServices

/// Global mouse-button dictation trigger via `CGEventTapCreate` — the ACTIVE-tap
/// sibling of `DictationHotkey`. Its threading (`NSLock`, masks read under the
/// lock inside the callback), degrade-once contract, re-enable-once-on-disable,
/// and run-loop plumbing are deliberate near-copies of `DictationHotkey` — see
/// that type's doc comment for the full rationale; it is not repeated here.
///
/// The ONE deliberate difference: this tap is created with `.defaultTap`
/// (active), not `.listenOnly`. `DictationHotkey` only ever *observes*
/// `flagsChanged` — modifier keys have no OS-level "consume" story worth
/// taking on. A bound mouse button is different: while dictation owns it, the
/// button's normal action (Back/Forward navigation, tab-close, autoscroll —
/// see `MouseButtonBinding.caution`) must NOT also fire underneath dictation.
/// An active tap lets the callback return `nil` for the bound button's events,
/// which CoreGraphics treats as "this event is consumed, do not deliver it
/// further" — the only way to actually swallow a click rather than just
/// observe it.
public final class DictationMouseButton: @unchecked Sendable {

    /// What the tap callback should do with one event, factored out as a pure
    /// function so tests can pin it without a live tap (which needs TCC
    /// grants and therefore can't run in CI).
    public enum MouseTapDecision: Sendable, Equatable {
        case passThrough
        case swallowAndHandle(down: Bool)
    }

    private let lock = NSLock()
    private var tap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var binding: MouseButtonBinding?
    /// The bound button is physically down, by a press this tap saw and
    /// swallowed. Only that press's release is swallowed in turn.
    private var pressed = false
    private var started = false

    /// The bound button went down. What a press means — the start of a
    /// hold, the click that keeps dictation running, or the press that
    /// stops it — is the consumer's to decide: it owns the dictation's
    /// state, and a second copy of that state here would drift from it
    /// whenever a dictation ends some other way (a stop key, Escape, the
    /// time limit).
    public var onPress: (() -> Void)?
    /// The bound button came up after a press this tap reported.
    public var onRelease: (() -> Void)?
    /// Fired exactly once per `start()`/`updateBinding` call that could not
    /// create a live tap — same "report it, never crash, never spin" contract
    /// as `DictationHotkey.onDegraded`.
    public var onDegraded: ((DictationPermissionStatus) -> Void)?
    /// Fired for EVERY otherMouse event the OS delivers, BEFORE the binding
    /// comparison — same liveness-vs-trigger separation as
    /// `DictationHotkey.onTapActivity`, for the same reason: a surface
    /// watching only `onPress`/`onRelease` cannot tell "no events are
    /// arriving" from "the user is clicking a button that isn't bound".
    public var onTapActivity: (() -> Void)?

    public init() {}

    /// The button THIS tap is actually comparing against — lets a UI surface
    /// render the live binding rather than a value it merely asked to set.
    public var boundBinding: MouseButtonBinding? {
        lock.lock(); defer { lock.unlock() }
        return binding
    }

    /// TEST-ONLY SEAM: one edge of the bound button exactly as
    /// `handleTapEvent` would take it, without a live `CGEventTap` — which
    /// needs TCC grants and therefore cannot run in CI. This is the only way
    /// to pin `updateBinding`'s mid-press release against the real
    /// `onPress`/`onRelease` callbacks; it is not part of the tap's own event
    /// flow, which always goes through `handleTapEvent`.
    public func simulateEdgeForTesting(down: Bool) {
        lock.lock()
        let fire = takeEdgeLocked(down: down)
        lock.unlock()
        if fire { down ? onPress?() : onRelease?() }
    }

    /// The swallow/pass-through decision, pure and static so it can be pinned
    /// without a live tap. Non-mouse event types (tap-disable notifications
    /// arrive through the same callback) are always pass-through here — the
    /// disable-handling branch deals with those before `decide` is ever
    /// called on them; this still answers safely if it is.
    ///
    /// A down of the bound button is always swallowed; only the up half is
    /// gated on `isDown`. An orphaned up (no tracked down) either predates
    /// the current binding, or followed an `updateBinding` that already
    /// reported its own release — either way it is not this tap's release
    /// to consume, and the frontmost app must see it.
    public static func decide(
        type: CGEventType, buttonNumber: Int64, bound: Int?, isDown: Bool
    ) -> MouseTapDecision {
        guard type == .otherMouseDown || type == .otherMouseUp else { return .passThrough }
        guard let bound, Int64(bound) == buttonNumber else { return .passThrough }
        let down = type == .otherMouseDown
        if !down, !isDown { return .passThrough }
        return .swallowAndHandle(down: down)
    }

    /// Records one swallowed edge; `true` when it is news. A repeat (a down
    /// while down) is swallowed but reported only once, so a consumer never
    /// sees two presses without a release between them. Must be called with
    /// `lock` held.
    private func takeEdgeLocked(down: Bool) -> Bool {
        guard down != pressed else { return false }
        pressed = down
        return true
    }

    /// With a nil binding this is a successful no-op: no tap, no permission
    /// probe, zero overhead and zero failure modes for the common case where
    /// the user has never armed a mouse trigger.
    @discardableResult
    public func start() -> Bool {
        lock.lock()
        var degraded: DictationPermissionStatus?
        defer {
            lock.unlock()
            if let degraded { onDegraded?(degraded) }
        }
        started = true
        guard binding != nil else { return true }
        return createTapLocked(degradedOut: &degraded)
    }

    public func stop() {
        lock.lock(); defer { lock.unlock() }
        started = false
        tearDownTapLocked()
    }

    /// `nil` on a live tap tears it down; a binding with no live tap (but a
    /// `start()`ed instance) creates one; binding→binding just swaps the
    /// compared number under the lock — same mid-hold-reset rationale as
    /// `DictationHotkey.updateConfig`: a press mid-flight on the OLD number
    /// must not leave its consumer waiting for a release the new binding
    /// will never report.
    public func updateBinding(_ newBinding: MouseButtonBinding?) {
        lock.lock()
        var degraded: DictationPermissionStatus?
        // Read under the lock, BEFORE the reset below — this is the only
        // moment the old press is still visible. A rebind or clear that
        // lands while the bound button is physically down must not silently
        // forget it: the hold that press started would never get a release,
        // leaving dictation stuck listening.
        let wasPressed = pressed
        defer {
            lock.unlock()
            // Outside the lock, same discipline as every other callback in
            // this class — never call out to arbitrary user code while
            // holding `lock`, or a callback that re-enters (e.g. reads
            // `boundBinding`) deadlocks on itself.
            if wasPressed { onRelease?() }
            if let degraded { onDegraded?(degraded) }
        }
        binding = newBinding
        pressed = false
        guard started else { return }
        switch (newBinding, tap) {
        case (nil, .some):
            tearDownTapLocked()
        case (.some, nil):
            _ = createTapLocked(degradedOut: &degraded)
        default:
            break // live tap just compares the new number from here on
        }
    }

    /// Must be called with `lock` held; `degradedOut` is filled in rather
    /// than firing `onDegraded` directly, so every caller can unlock before
    /// the callback runs — a callback re-entering this class (e.g. reading
    /// `boundBinding`) must never deadlock on its own lock.
    private func createTapLocked(degradedOut: inout DictationPermissionStatus?) -> Bool {
        guard tap == nil else { return true }

        let status = DictationHotkey.permissionStatus()
        guard status.isGranted else {
            degradedOut = status
            return false
        }

        let mask: CGEventMask = (1 << CGEventType.otherMouseDown.rawValue) | (1 << CGEventType.otherMouseUp.rawValue)
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        guard let createdTap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                guard let refcon else { return Unmanaged.passUnretained(event) }
                let button = Unmanaged<DictationMouseButton>.fromOpaque(refcon).takeUnretainedValue()
                return button.handleTapEvent(type: type, event: event)
            },
            userInfo: selfPtr
        ) else {
            // Same status-flip-between-check-and-create honesty as
            // DictationHotkey.start(): report it rather than force-unwrap.
            degradedOut = status
            return false
        }

        tap = createdTap
        let source = CFMachPortCreateRunLoopSource(nil, createdTap, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: createdTap, enable: true)
        return true
    }

    /// Must be called with `lock` held.
    private func tearDownTapLocked() {
        EventTapTeardown.invalidate(tap: tap, runLoopSource: runLoopSource)
        tap = nil
        runLoopSource = nil
        pressed = false
    }

    private func handleTapEvent(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            // Self-heal once; if the grant was actually revoked this keeps
            // failing silently on the OS side, which is correct — we must
            // not spin retrying it.
            lock.lock()
            let currentTap = tap
            lock.unlock()
            if let currentTap { CGEvent.tapEnable(tap: currentTap, enable: true) }
            return Unmanaged.passUnretained(event)
        }

        // Unconditional, and BEFORE any binding comparison — the whole point
        // is that this fires for buttons the binding does not match.
        onTapActivity?()

        let buttonNumber = event.getIntegerValueField(.mouseEventButtonNumber)

        lock.lock()
        // Read the bound number UNDER the lock — `binding` is mutable from
        // `updateBinding` on the main thread while this runs on the tap
        // thread, the same genuine race `DictationHotkey.handleTapEvent`
        // guards against.
        let boundNumber = binding?.buttonNumber
        let decision = Self.decide(
            type: type, buttonNumber: buttonNumber, bound: boundNumber, isDown: pressed)
        var fire = false
        if case .swallowAndHandle(let down) = decision {
            fire = takeEdgeLocked(down: down)
        }
        lock.unlock()

        switch decision {
        case .passThrough:
            return Unmanaged.passUnretained(event)
        case .swallowAndHandle(let down):
            if fire { down ? onPress?() : onRelease?() }
            return nil // the swallow — both down and up, unconditionally
        }
    }
}
