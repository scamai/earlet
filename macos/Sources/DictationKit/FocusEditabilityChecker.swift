import Foundation
import ApplicationServices
import AppKit

/// Result of checking whether the system's currently-focused UI element
/// will accept inserted text.
public enum FocusCheckResult: Sendable, Equatable {
    case editable
    case notEditable
    case noFocusedElement
    case permissionMissing
    /// The focused element is a PASSWORD field — `AXSecureTextField`.
    ///
    /// Split out of `.editable`, which is exactly what it used to be
    /// classified as. A secure field publishes `AXTextField` as its role and
    /// only announces itself through its SUBROLE, which this checker never
    /// read, so a password field and an ordinary one were indistinguishable
    /// here. Every native `NSSecureTextField` also engages macOS Secure Input,
    /// and `TextInserter.secureInputGate()` catches that — but Secure Input is
    /// a process-wide assertion an app can decline to make, and the AX subrole
    /// is the field's own statement about itself. Two independent signals, and
    /// this is the one that does not depend on the target app cooperating.
    ///
    /// The app refuses to type into it, and — the reason this case exists at all
    /// rather than folding into `.notEditable` — refuses to write the text or
    /// the recording ANYWHERE. `.notEditable` falls through to the clipboard
    /// fallback, which preserves the text on the pasteboard and records it in
    /// history; for a password that is precisely the hole Windows shipped on
    /// 2026-08-19.
    case secureField
}

/// AX-based editability gate: the focused element is checked before
/// anything else happens, BEFORE the pasteboard is touched or a keystroke is
/// synthesized.
///
/// Deliberately READ-ONLY: uses AX metadata to ask whether the focused
/// element is a text-entry target, never
/// `AXUIElementSetAttributeValue` to actually perform one. Insertion is
/// paste-only (decided 2026-08-06), so every AX use is on the read and
/// observation side, and this checker can never accidentally become a
/// second insertion path.
public enum FocusEditabilityChecker {
    /// Web/Electron content-editable controls often expose a text-entry role
    /// while refusing direct AXValue writes. That does not mean they refuse
    /// paste: Command-V is their supported insertion path. Requiring only a
    /// settable AXValue rejected exactly those fields before the app ever sent
    /// the paste (the field report showed this for Witty and WeChat).
    private static let textEntryRoles: Set<String> = [
        "AXTextField", "AXTextArea", "AXComboBox",
    ]

    /// The AX subrole a password field carries. `AXSecureTextArea` is not a
    /// documented AppKit subrole today, but it is cheap to honour and the
    /// cost of missing one is a stored password.
    private static let secureSubroles: Set<String> = [
        "AXSecureTextField", "AXSecureTextArea",
    ]

    public static func check() -> FocusCheckResult {
        guard AXIsProcessTrusted() else { return .permissionMissing }
        guard let element = focusedElement() else {
            return .noFocusedElement
        }
        return classifyElement(element)
    }

    /// The same classification for `element` itself rather than for whatever
    /// has focus now. The correction observer asks this of the exact field it
    /// is about to watch, so a password field is refused even if focus moved
    /// between two AX calls.
    public static func check(element: AXUIElement) -> FocusCheckResult {
        guard AXIsProcessTrusted() else { return .permissionMissing }
        return classifyElement(element)
    }

    /// The focused element, by the fallbacks `check()` uses.
    static func focusedElement() -> AXUIElement? {
        let systemWide = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(systemWide, 0.3)
        return focusedElement(systemWide: systemWide)
    }

    private static func classifyElement(_ element: AXUIElement) -> FocusCheckResult {
        AXUIElementSetMessagingTimeout(element, 0.3)
        return classify(
            valueSettable: isSettable(element, attribute: kAXValueAttribute),
            selectedTextSettable: isSettable(element, attribute: kAXSelectedTextAttribute),
            role: stringAttribute(element, attribute: kAXRoleAttribute),
            subrole: stringAttribute(element, attribute: kAXSubroleAttribute))
    }

    /// Pure classification seam used by the TCC-free self-check. A positive
    /// signal from either standard text attribute is enough; otherwise a
    /// known text-entry role is enough to ATTEMPT Command-V. TextInserter's
    /// delayed provider remains the receipt: if the target does not actually
    /// accept paste, it never asks for the data and insertion still fails.
    /// `subrole` defaults to nil so the existing role-only call sites (the
    /// TCC-free self-check) keep compiling and keep asserting what they always
    /// asserted. A nil subrole is "the element did not say", which is the
    /// honest reading for every non-secure field — no field is treated as
    /// secure by omission.
    public static func classify(
        valueSettable: Bool, selectedTextSettable: Bool, role: String?,
        subrole: String? = nil
    ) -> FocusCheckResult {
        // FIRST, and above every positive signal below. A secure field is
        // settable and carries a text-entry role, so any ordering that checks
        // those first classifies a password field as an ordinary one — which
        // is what this checker did until 2026-08-20.
        if let subrole, secureSubroles.contains(subrole) { return .secureField }
        if valueSettable || selectedTextSettable { return .editable }
        if let role, textEntryRoles.contains(role) { return .editable }
        return .notEditable
    }

    /// Prefer the system-wide focused element. Some web/Electron apps do not
    /// publish it there consistently even though the focused application can
    /// answer the same attribute, so retry through the focused app and then
    /// the workspace's frontmost app before declaring there is no target.
    private static func focusedElement(systemWide: AXUIElement) -> AXUIElement? {
        if let element = elementAttribute(systemWide, attribute: kAXFocusedUIElementAttribute) {
            return element
        }
        if let app = elementAttribute(systemWide, attribute: kAXFocusedApplicationAttribute) {
            AXUIElementSetMessagingTimeout(app, 0.3)
            if let element = elementAttribute(app, attribute: kAXFocusedUIElementAttribute) {
                return element
            }
        }
        guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier else {
            return nil
        }
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.3)
        return elementAttribute(app, attribute: kAXFocusedUIElementAttribute)
    }

    private static func elementAttribute(
        _ element: AXUIElement, attribute: String
    ) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element, attribute as CFString, &value) == .success,
              let value else { return nil }
        // Focused-element/application attributes are AXUIElement values on
        // success by the Accessibility API contract.
        return (value as! AXUIElement) // swiftlint:disable:this force_cast
    }

    private static func isSettable(_ element: AXUIElement, attribute: String) -> Bool {
        var settable: DarwinBoolean = false
        return AXUIElementIsAttributeSettable(
            element, attribute as CFString, &settable) == .success && settable.boolValue
    }

    private static func stringAttribute(_ element: AXUIElement, attribute: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            element, attribute as CFString, &value) == .success else { return nil }
        return value as? String
    }
}
