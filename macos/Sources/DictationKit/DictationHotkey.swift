import Foundation
import CoreGraphics
import ApplicationServices

/// The two TCC grants a listening `CGEventTap` needs. Accessibility
/// (`AXIsProcessTrusted`) and Input Monitoring (`CGPreflightListenEventAccess`)
/// are checked and reported SEPARATELY: a user fixing one but not the other
/// is a real, common half-onboarded state we need to explain rather than lump
/// into one generic "permission denied".
public enum DictationPermissionStatus: Sendable, Equatable {
    case granted
    case missingAccessibility
    case missingInputMonitoring
    case missingBoth

    public var isGranted: Bool { self == .granted }
}

/// Global push-to-talk (hold) + double-tap-modifier (toggle) hotkey via
/// `CGEventTapCreate`, **not** Carbon `RegisterEventHotKey`: both gestures
/// are a modifier key on its own, which a registered hotkey cannot express.
///
/// Degrades cleanly when Accessibility/Input Monitoring is not granted:
/// `start()` checks permission FIRST and returns `false` + calls
/// `onDegraded` exactly once rather than looping `CGEventTapCreate` (which
/// returns nil without the grant — retrying it in a loop would spin a CPU
/// core for no gain and give the user zero signal about why nothing is
/// happening). If macOS disables an already-running tap mid-session
/// (`kCGEventTapDisabledByTimeout`/`ByUserInput` — happens if the tap's
/// callback blocks too long, or the user flips the TCC grant off while
/// running), the callback re-enables it exactly once per disable event; it
/// does not retry-loop that either.
public final class DictationHotkey: @unchecked Sendable {

    public struct Config: Sendable {
        /// Modifier held down for push-to-talk. Default: right Option — a
        /// key rarely bound to anything else and reachable one-handed.
        public var holdToTalkFlagMask: CGEventFlags
        /// Modifier double-tapped to toggle dictation on/off. Deliberately
        /// a DIFFERENT key from `holdToTalkFlagMask` — see
        /// `DoubleTapModifierState`'s doc comment for why the two are
        /// independent.
        public var doubleTapFlagMask: CGEventFlags
        public var doubleTapWindow: TimeInterval
        /// Modifier held down for the SECOND push-to-talk gesture: voice
        /// workflows. `nil` — the default, and the only value the app uses
        /// with `feature.voice_workflows` off — means no such gesture is
        /// registered at all.
        ///
        /// That distinction is load-bearing rather than cosmetic. With this
        /// nil the arbiter has exactly one candidate, so `owner(for:)` is
        /// `flags.contains(holdToTalkFlagMask)` and this tap behaves
        /// byte-for-byte as it did before workflows existed — the flag-off
        /// path is not a workflow branch that declines to run, it is an
        /// absent binding. With it set, the arbiter can hand a gesture to
        /// workflows INSTEAD of dictation, which is the only way two
        /// modifier holds can share one `flagsChanged` stream without both
        /// firing (see `HotkeyArbiter`).
        public var workflowFlagMask: CGEventFlags?

        /// The physical keys the hold binding accepts, or empty for either.
        ///
        /// Separate from the mask because macOS reports both Options through
        /// one flag bit: the mask says WHICH MODIFIER, this says WHICH KEY.
        /// Only the hold carries one — the double-tap is a temporal gesture
        /// with no arbiter in front of it, and the workflows chord is a
        /// combination, which is already specific.
        public var holdToTalkKeyCodes: Set<Int64> = []

        /// The physical keys the double-tap detector accepts, or empty for
        /// either side. Same reason as `holdToTalkKeyCodes`: one flag bit
        /// covers both sides of a modifier, and only the event's keycode
        /// says which one was tapped.
        public var doubleTapKeyCodes: Set<Int64> = []

        public init(
            holdToTalkFlagMask: CGEventFlags = .maskAlternate,
            doubleTapFlagMask: CGEventFlags = .maskControl,
            doubleTapWindow: TimeInterval = 0.35,
            workflowFlagMask: CGEventFlags? = nil
        ) {
            self.holdToTalkFlagMask = holdToTalkFlagMask
            self.doubleTapFlagMask = doubleTapFlagMask
            self.doubleTapWindow = doubleTapWindow
            self.workflowFlagMask = workflowFlagMask
        }

        /// The arbiter this config implies — one candidate per registered
        /// gesture, and nothing for a gesture whose binding is absent.
        ///
        /// Built here rather than in the tap so the mapping from config to
        /// candidates is one expression a check can read, and so "flag off ⇒
        /// one candidate" is assertable without a `CGEventTap`.
        public var arbiter: HotkeyArbiter {
            var candidates = [HotkeyArbiter.Candidate(
                gesture: .dictation, mask: holdToTalkFlagMask,
                keyCodes: holdToTalkKeyCodes)]
            if let workflowFlagMask {
                candidates.append(
                    HotkeyArbiter.Candidate(gesture: .workflows, mask: workflowFlagMask))
            }
            return HotkeyArbiter(candidates: candidates)
        }

        /// Build from the user's saved bindings.
        ///
        /// An EMPTY binding must never reach here: `CGEventFlags()` is the
        /// empty set and `contains` on an empty set is always true, so an
        /// unset hotkey would report "held" on every event and pin dictation
        /// permanently open. `HotkeyBinding.rejection` blocks that at the UI,
        /// and this init falls back rather than trusting it — the guard is
        /// cheap and the failure is unrecoverable from the user's side.
        public init(
            hold: HotkeyBinding,
            doubleTap: HotkeyBinding,
            doubleTapWindow: TimeInterval = 0.35,
            workflows: HotkeyBinding? = nil
        ) {
            let effectiveHold = hold.isEmpty ? HotkeyBinding.defaultHold : hold
            self.holdToTalkFlagMask = effectiveHold.eventFlagMask
            // A side is only expressible for a SINGLE modifier. On ⌃⌥ the
            // keycode names whichever of the two changed last, so demanding a
            // side would reject the half of the presses that arrive in the
            // other order — a hold that works only if you press the keys the
            // right way round is worse than one that ignores the side.
            if effectiveHold.side != .either, effectiveHold.modifiers.count == 1,
               let only = effectiveHold.modifiers.first {
                self.holdToTalkKeyCodes = HotkeyKeySide.keyCodes(
                    for: only, side: effectiveHold.side)
            }
            self.doubleTapFlagMask = doubleTap.isEmpty ? HotkeyBinding.defaultDoubleTap.eventFlagMask : doubleTap.eventFlagMask
            self.doubleTapWindow = doubleTapWindow
            // An EMPTY workflow binding is dropped rather than defaulted:
            // "the caller passed no workflow gesture" and "the caller passed
            // a broken one" must both come out as no gesture. Defaulting the
            // second to `.defaultWorkflows` would arm a global chord nobody
            // asked for, on a feature whose whole posture is default-OFF.
            self.workflowFlagMask = (workflows?.isEmpty == false) ? workflows?.eventFlagMask : nil
        }
    }

    private var config: Config
    private let lock = NSLock()
    private var tap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var doubleTapState: DoubleTapModifierState
    /// Both hold gestures, behind one arbiter. Driven from a SINGLE owner
    /// answer, which is what makes "dictation and workflows both fired on
    /// one gesture" unrepresentable rather than merely unlikely — see
    /// `HotkeyGestureRouter`, where that property is exercised directly.
    private var router: HotkeyGestureRouter

    public var onPushToTalkStart: (() -> Void)?
    public var onPushToTalkEnd: (() -> Void)?
    public var onDoubleTapActivate: (() -> Void)?
    public var onDoubleTapDeactivate: (() -> Void)?
    /// The workflow gesture went down. Never fires while dictation holds the
    /// keyboard, and never fires at all when `Config.workflowFlagMask` is
    /// nil — with the feature flag off there is no candidate to win.
    public var onWorkflowStart: (() -> Void)?
    /// The workflow gesture came up. Every `onWorkflowStart` is followed by
    /// exactly one of these, including when the owner changes underneath it.
    public var onWorkflowEnd: (() -> Void)?
    /// Fired exactly once per `start()` call that could not create a live
    /// tap — the "report it, never crash, never spin" contract.
    public var onDegraded: ((DictationPermissionStatus) -> Void)?

    /// Fired for EVERY `flagsChanged` the OS delivers, whether or not it
    /// matches a binding.
    ///
    /// The liveness signal, kept deliberately separate from the trigger
    /// callbacks. `onPushToTalkStart` only fires when the *bound* modifier is
    /// pressed, so a surface watching only that cannot distinguish "macOS is
    /// sending us nothing" from "the user pressed a different key" — and on
    /// 2026-08-08 a surface watching only that told a user their permissions
    /// were broken when they were fine. This fires first and unconditionally,
    /// so the two causes separate.
    public var onTapActivity: (() -> Void)?

    /// The modifier flags of every `flagsChanged`, delivered BEFORE any
    /// gesture that event produces.
    ///
    /// Lets a caller notice a second modifier going down during a hold (the
    /// hold key used in a shortcut) without its own tap. The order matters:
    /// the flags that started a hold have already been seen when
    /// `onPushToTalkStart` fires.
    public var onModifierFlags: ((CGEventFlags) -> Void)?

    public init(config: Config = Config()) {
        self.config = config
        self.doubleTapState = DoubleTapModifierState(window: config.doubleTapWindow)
        self.router = HotkeyGestureRouter(arbiter: config.arbiter)
    }

    /// The config THIS tap is actually listening for.
    ///
    /// Exists so a UI surface can render the gesture from the live object
    /// rather than a hardcoded "Hold Right Option" string. A hardcoded
    /// label is the same defect class as the dictation model override that
    /// shipped for a full run while the product path silently kept its
    /// default: the description and the behaviour drift apart and nothing
    /// fails. `DictationController.hotkeyDescription` reads this.
    public var configuration: Config {
        lock.lock()
        defer { lock.unlock() }
        return config
    }

    /// Swap in a new binding on a live tap.
    ///
    /// Does NOT tear the tap down: the tap's subscription is `flagsChanged`
    /// regardless of which modifiers are bound, so only the comparison masks
    /// change. Rebuilding it would drop the TCC-validated port and re-run the
    /// permission path for no reason.
    ///
    /// Resets both gesture state machines. Without that, a binding changed
    /// while a key is physically held leaves `HoldToTalkState` believing the
    /// OLD modifier is still down, and the next release of an unrelated key
    /// emits a `pushToTalkEnd` for a session that never started.
    public func updateConfig(_ newConfig: Config) {
        lock.lock()
        defer { lock.unlock() }
        config = newConfig
        doubleTapState = DoubleTapModifierState(window: newConfig.doubleTapWindow)
        router = HotkeyGestureRouter(arbiter: newConfig.arbiter)
    }

    /// Read-only permission probe — does NOT prompt the user (uses
    /// `CGPreflightListenEventAccess`, not `CGRequestListenEventAccess`).
    /// Onboarding UI (not built in this spike — M1 is module+CLI+self-check
    /// only) is expected to call the requesting variant explicitly, on a
    /// deliberate user action: permission is an onboarding step, not
    /// something signing can grant.
    public static func permissionStatus() -> DictationPermissionStatus {
        let accessibility = AXIsProcessTrusted()
        let inputMonitoring = CGPreflightListenEventAccess()
        switch (accessibility, inputMonitoring) {
        case (true, true): return .granted
        case (false, true): return .missingAccessibility
        case (true, false): return .missingInputMonitoring
        case (false, false): return .missingBoth
        }
    }

    @discardableResult
    public func start() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard tap == nil else { return true }

        let status = Self.permissionStatus()
        guard status.isGranted else {
            onDegraded?(status)
            return false
        }

        let mask: CGEventMask = 1 << CGEventType.flagsChanged.rawValue
        let selfPtr = Unmanaged.passUnretained(self).toOpaque()
        guard let createdTap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: mask,
            callback: { _, type, event, refcon in
                guard let refcon else { return Unmanaged.passUnretained(event) }
                let hotkey = Unmanaged<DictationHotkey>.fromOpaque(refcon).takeUnretainedValue()
                hotkey.handleTapEvent(type: type, event: event)
                return Unmanaged.passUnretained(event)
            },
            userInfo: selfPtr
        ) else {
            // CGEventTapCreate returns nil without the grant (and, rarely,
            // for other OS-level reasons) — same degrade path as the
            // pre-check above, so a status flip between the check and the
            // create call is still reported honestly rather than crashing
            // on a force-unwrap.
            onDegraded?(status)
            return false
        }

        tap = createdTap
        let source = CFMachPortCreateRunLoopSource(nil, createdTap, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: createdTap, enable: true)
        return true
    }

    public func stop() {
        lock.lock()
        defer { lock.unlock() }
        EventTapTeardown.invalidate(tap: tap, runLoopSource: runLoopSource)
        tap = nil
        runLoopSource = nil
        doubleTapState = DoubleTapModifierState(window: config.doubleTapWindow)
        router.reset()
    }

    private func handleTapEvent(type: CGEventType, event: CGEvent) {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            // Self-heal once; if the underlying grant was actually revoked
            // this will keep failing silently on the OS side, which is
            // correct — we must not spin retrying it.
            lock.lock()
            let currentTap = tap
            lock.unlock()
            if let currentTap { CGEvent.tapEnable(tap: currentTap, enable: true) }
            return
        }
        guard type == .flagsChanged else { return }

        // Unconditional, and BEFORE any binding comparison — the whole point
        // is that this fires for keys the binding does not match.
        onTapActivity?()

        // Off the SAME event as the flags. `flagsChanged` carries the keycode
        // of the key that changed, which is how a binding can say "the right
        // Option" without this tap ever subscribing to `keyDown` — the
        // privacy property (the original tap cannot see a character) is unchanged.
        process(flags: event.flags, keyCode: event.getIntegerValueField(.keyboardEventKeycode))
    }

    /// Everything the tap does with one `flagsChanged` after reading it off
    /// the event, so a test can drive the real fan-out without a `CGEvent`.
    func process(flags: CGEventFlags, keyCode: Int64) {
        onModifierFlags?(flags)
        let timestamp = ProcessInfo.processInfo.systemUptime

        lock.lock()
        // Read the masks UNDER the lock. `config` became mutable when the
        // hotkey became user-settable, so reading it on the tap thread while
        // `updateConfig` writes it on the main thread is a genuine race —
        // and one that would only ever bite at the moment a user saves a new
        // binding, which is the least reproducible moment there is.
        //
        // ONE arbiter answer drives BOTH hold detectors. This is the whole
        // non-collision construction: `owner` is a single optional value, so
        // at most one of `holdDown`/`workflowDown` can be true, and no
        // sequence of `flagsChanged` events can put dictation and workflows
        // down together. Two independent `flags.contains(...)` tests — the
        // obvious implementation, and what this line used to be — would fire
        // both on ⌘⌥, because `contains` is a superset test and ⌘⌥ ⊇ ⌥.
        let emission = router.handle(flags: flags, keyCode: keyCode)
        // The side check the hold gets from the arbiter's keycode filter:
        // the mask names the modifier, the keycode names the physical key.
        // A non-matching event reads as "not down" — the detector ignores
        // release edges, so a tap of the other side leaves no trace.
        let doubleTapDown = flags.contains(config.doubleTapFlagMask)
            && (config.doubleTapKeyCodes.isEmpty || config.doubleTapKeyCodes.contains(keyCode))
        let doubleTapEvent = doubleTapState.handle(down: doubleTapDown, timestamp: timestamp)
        lock.unlock()

        switch emission.dictation {
        case .pushToTalkStart: onPushToTalkStart?()
        case .pushToTalkEnd: onPushToTalkEnd?()
        default: break
        }
        switch emission.workflows {
        case .pushToTalkStart: onWorkflowStart?()
        case .pushToTalkEnd: onWorkflowEnd?()
        default: break
        }
        switch doubleTapEvent {
        case .doubleTapActivate: onDoubleTapActivate?()
        case .doubleTapDeactivate: onDoubleTapDeactivate?()
        default: break
        }
    }
}
