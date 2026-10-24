import Foundation
import ApplicationServices
import AppKit

/// Iteration 38's gap: `DictationSession` has taken a `targetBundleIDProvider`
/// since Iteration 34, and `DictationFormatter` has had `profile(forApp:)`
/// since the same iteration, but nothing in the codebase could actually
/// answer "what application am I about to type into?" — the provider had no
/// real implementation. That is a missing primitive, not a formatting
/// problem: per-app formatting, M2's correction observer, and any future
/// per-app behaviour (a per-app format or voice setting) all need the same
/// answer.
///
/// Two plausible sources, and they can disagree:
///   - `NSWorkspace.shared.frontmostApplication?.bundleIdentifier` — which
///     app owns the frontmost window/menu bar.
///   - the AX system-wide focused element's owning PID -> bundle id — which
///     app actually holds keyboard focus.
/// They differ with floating panels, Spotlight, and some Electron apps
/// (frontmost != focused). The app cares where the TEXT goes, i.e. the focused
/// element, not merely which app is frontmost — so the AX-focused path is
/// preferred, with `NSWorkspace` as the fallback for every case AX can't
/// answer: permission not granted, no focused element, or a focused element
/// whose owning process has no bundle id (some helper processes).
public enum FrontmostAppSource: Sendable, Equatable {
    /// The AX system-wide focused element's owning process — the more
    /// truthful signal for insertion targeting (see type doc comment).
    case axFocusedElement
    /// `NSWorkspace.shared.frontmostApplication` — used whenever the AX path
    /// did not produce a bundle id, for any reason.
    case nsWorkspace
    /// Neither signal produced a bundle id (e.g. nothing focused AND no
    /// frontmost app reported — narrow but real: `frontmostApplication` is
    /// itself optional).
    case none
}

/// One resolution — the bundle id (if any) and which signal answered it, so
/// a caller (or this task's own measurement pass) can report which path
/// fired rather than just the final value.
public struct FrontmostApplicationResult: Sendable, Equatable {
    public let bundleID: String?
    public let source: FrontmostAppSource

    public init(bundleID: String?, source: FrontmostAppSource) {
        self.bundleID = bundleID
        self.source = source
    }
}

/// Answers "what application is about to receive the text the app is about to
/// insert" — the missing primitive `DictationSession`'s
/// `targetBundleIDProvider` and `DictationFormatter.profile(forApp:)` were
/// both built around but never wired to a real answer.
///
/// Every case returns a defined `FrontmostApplicationResult`, never crashes
/// or hangs:
///   - no focused element -> falls through to `NSWorkspace`.
///   - a focused element whose process has no bundle id (some helpers) ->
///     falls through to `NSWorkspace`.
///   - the app itself frontmost (dictating into its own window) -> resolves
///     normally to its own bundle id; `DictationFormatter.profile(forApp:)`
///     has no special case for it and falls back to `.standard`,
///     same as any other unrecognized app — a defined result, not a crash.
///   - Accessibility permission not granted -> `AXIsProcessTrusted()` is
///     checked FIRST, before any AX call, and short-circuits straight to
///     `NSWorkspace`.
///   - a hung target application -> `AXUIElementSetMessagingTimeout` is set
///     on the system-wide element before the one AX round trip this makes
///     (`kAXFocusedUIElementAttribute`), the same discipline
///     `DictationCorrectionObserver` and `TextInserter`'s
///     `FocusEditabilityChecker` gate already use — a stuck Accessibility
///     server on the target side cannot hang dictation's arm-to-insert path.
public enum FrontmostApplicationResolver {
    /// Bound on the one AX round trip this makes. 0.3s matches
    /// `DictationCorrectionObserver.Config.messagingTimeoutSeconds`'s
    /// default (design §4.3's "proposed: 300ms") — the same order-of-
    /// magnitude budget already accepted elsewhere in this module for
    /// "how long may a hung target app block an AX call before the app gives
    /// up and moves on."
    public static let axMessagingTimeoutSeconds: Float = 0.3

    public static func resolve() -> FrontmostApplicationResult {
        resolveChain(axResolver: resolveViaAXFocusedElement, workspaceResolver: resolveViaNSWorkspace)
    }

    /// The pure fallback-chain logic, isolated from the real AX/NSWorkspace
    /// calls behind it so it can be exercised with synthetic inputs in a
    /// self-check — no TCC, no window-server round trip, no `NSWorkspace`
    /// call. `resolve()` is this function called with the two real system
    /// sources; every other case (permission missing, no focused element,
    /// a helper with no bundle id, a hung target bounded by the messaging
    /// timeout) is already folded into "the AX resolver returned `nil`" by
    /// `resolveViaAXFocusedElement()`, so this ordering is the ENTIRE
    /// decision `resolve()` makes.
    static func resolveChain(axResolver: () -> String?, workspaceResolver: () -> String?) -> FrontmostApplicationResult {
        if let bundleID = axResolver() {
            return FrontmostApplicationResult(bundleID: bundleID, source: .axFocusedElement)
        }
        if let bundleID = workspaceResolver() {
            return FrontmostApplicationResult(bundleID: bundleID, source: .nsWorkspace)
        }
        return FrontmostApplicationResult(bundleID: nil, source: .none)
    }

    /// `nil` covers every AX-side "couldn't answer" case uniformly (no
    /// permission, no focused element, timeout, a PID that doesn't resolve
    /// to a running app, or a resolved app with no bundle id) — `resolve()`
    /// treats all of them the same way: fall through to `NSWorkspace`.
    static func resolveViaAXFocusedElement() -> String? {
        guard AXIsProcessTrusted() else { return nil }

        let systemWide = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(systemWide, axMessagingTimeoutSeconds)

        var focusedRef: CFTypeRef?
        let copyErr = AXUIElementCopyAttributeValue(
            systemWide, kAXFocusedUIElementAttribute as CFString, &focusedRef)
        guard copyErr == .success, let focusedRef else { return nil }
        // Safe: AXUIElementCopyAttributeValue for kAXFocusedUIElementAttribute
        // always returns an AXUIElement on `.success` — same guarantee
        // `FocusEditabilityChecker.check()` already relies on.
        let element = focusedRef as! AXUIElement // swiftlint:disable:this force_cast
        AXUIElementSetMessagingTimeout(element, axMessagingTimeoutSeconds)

        var pid: pid_t = 0
        guard AXUIElementGetPid(element, &pid) == .success, pid > 0 else { return nil }

        // A helper process (or one that has already exited between the AX
        // call and here) has no `NSRunningApplication`, or has one with a
        // `nil` bundleIdentifier — both are real, expected outcomes, not
        // error conditions, and both fall through to NSWorkspace via `nil`.
        guard let app = NSRunningApplication(processIdentifier: pid) else { return nil }
        return app.bundleIdentifier
    }

    static func resolveViaNSWorkspace() -> String? {
        NSWorkspace.shared.frontmostApplication?.bundleIdentifier
    }
}
