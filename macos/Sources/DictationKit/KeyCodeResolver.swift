import Foundation
import Carbon.HIToolbox

/// Resolves the physical virtual keycode that types a character on the
/// CURRENT keyboard layout, instead of assuming one. `kVK_ANSI_V` (9) is only
/// correct on ANSI-QWERTY; Dvorak, AZERTY, and other layouts put a different
/// physical key (or none) under the ⌘V gesture users expect to fire paste, so
/// hardcoding it would synthesize the wrong keystroke on a meaningful
/// fraction of real keyboards.
public enum KeyCodeResolver {

    /// Hardcoded ANSI-QWERTY fallback — used ONLY when TIS layout data is
    /// unavailable (e.g. this module's self-check runs with no active
    /// keyboard-layout input source, which is the normal state in a
    /// headless/CI/sandboxed process with no window server session).
    public static let fallbackVKeyCode: CGKeyCode = 9

    /// Scans virtual keycodes 0..<128 against the given layout data (or the
    /// system's CURRENT keyboard layout when `layoutData` is nil) for the
    /// one that types `character` with no modifiers. Returns
    /// `fallbackVKeyCode` if layout data can't be obtained or no keycode
    /// produces the character, the same graceful fallback Secure Input
    /// detection uses: a keycode-resolution failure must degrade to a
    /// reasonable guess, never crash the insertion path.
    public static func resolveKeyCode(for character: Character, layoutData: Data? = nil) -> CGKeyCode {
        guard let data = layoutData ?? currentLayoutData() else { return fallbackVKeyCode }
        return data.withUnsafeBytes { raw -> CGKeyCode in
            guard let keyLayoutPtr = raw.bindMemory(to: UCKeyboardLayout.self).baseAddress else {
                return fallbackVKeyCode
            }
            var deadKeyState: UInt32 = 0
            for keyCode: UInt16 in 0..<128 {
                var chars = [UniChar](repeating: 0, count: 4)
                var length = 0
                // kbType 0 is accepted by UCKeyTranslate as "use the layout's
                // own default" — avoids depending on the deprecated/private
                // `LMGetKbdType()` just to fill in a value the layout data
                // already encodes.
                let status = UCKeyTranslate(
                    keyLayoutPtr,
                    keyCode,
                    UInt16(kUCKeyActionDown),
                    0,
                    0,
                    OptionBits(kUCKeyTranslateNoDeadKeysBit),
                    &deadKeyState,
                    chars.count,
                    &length,
                    &chars
                )
                guard status == noErr, length > 0, let scalar = Unicode.Scalar(chars[0]) else { continue }
                if Character(scalar).lowercased() == character.lowercased() {
                    return CGKeyCode(keyCode)
                }
            }
            return fallbackVKeyCode
        }
    }

    /// Convenience for the actual paste path: resolves 'v' specifically.
    public static func resolveVKeyCode() -> CGKeyCode {
        resolveKeyCode(for: "v")
    }

    static func currentLayoutData() -> Data? {
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue() else { return nil }
        guard let layoutDataPtr = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else {
            return nil
        }
        let cfData = Unmanaged<CFData>.fromOpaque(layoutDataPtr).takeUnretainedValue()
        return cfData as Data
    }
}

/// Test-only access to `KeyCodeResolver`'s layout lookup, so
/// `DictationSelfCheck` can exercise the real `UCKeyTranslate` scan against
/// whatever layout data (if any) is actually available in the process
/// running the check, without duplicating the TIS lookup logic.
enum KeyCodeResolverTestSupport {
    static func currentLayoutDataForTesting() -> Data? {
        KeyCodeResolver.currentLayoutData()
    }
}
