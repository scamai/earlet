import CoreGraphics
import Foundation
import Carbon.HIToolbox

/// macOS Secure Input (password fields) disables event taps system-wide,
/// so it has to be detected and explained: left silent, it reads as
/// "dictation is broken" when the truth is "you are in a password field".
public enum SecureInputStatus: Sendable, Equatable {
    /// Secure Input is not active.
    case inactive
    /// Active, and the session-dictionary lookup told us WHICH process
    /// holds it.
    case activeHeldByPID(pid_t)
    /// Active, and the holder is THIS process. Kept apart from another
    /// holder because the person needs a different explanation: this app
    /// turned it on itself, rather than another app blocking dictation.
    case activeHeldBySelf
    /// Active, but the holder lookup found no answer — we only know the
    /// holder-blind public answer.
    case activeUnknownHolder
}

/// Best-effort Secure Input detection. Prefers the
/// `kCGSSessionSecureInputPID` entry of the window server's session
/// dictionary (the same key `ioreg -l -w 0` lists for the process holding
/// Secure Input), because it is the only way to name the holder.
///
/// The dictionary itself is public — `CGSessionCopyCurrentDictionary`
/// (CGSession.h). Only the KEY is undocumented: no header declares it, so the
/// read is treated as something Apple can silently rename or drop in any OS
/// release, and EVERY failure mode falls through to the public, holder-blind
/// `IsSecureEventInputEnabled()` rather than crashing, asserting, or hanging.
public enum SecureInputMonitor {

    public static func currentStatus() -> SecureInputStatus {
        // HIToolbox declares this API not thread-safe. Observer work runs on
        // background queues, while insertion runs on the main actor; all
        // reads meet on the main thread without moving AX field reads there.
        if Thread.isMainThread {
            return MainActor.assumeIsolated { readStatus() }
        }
        return DispatchQueue.main.sync { readStatus() }
    }

    @MainActor private static func readStatus() -> SecureInputStatus {
        status(
            holderPID: sessionSecureInputHolderPID(),
            secureInputEnabled: IsSecureEventInputEnabled())
    }

    /// The status decision off its two inputs, pure so a test can pin every
    /// branch without a window server session. The documented flag decides
    /// whether Secure Input is on. A stale undocumented holder entry must
    /// not keep shortcuts disabled after Secure Input has ended.
    static func status(holderPID: pid_t?, secureInputEnabled: Bool) -> SecureInputStatus {
        guard secureInputEnabled else { return .inactive }
        if let pid = holderPID, pid > 0 {
            return pid == ProcessInfo.processInfo.processIdentifier
                ? .activeHeldBySelf
                : .activeHeldByPID(pid)
        }
        return .activeUnknownHolder
    }

    /// The holder read off the session dictionary. Any surprise — no
    /// dictionary (no window server session), the key absent, renamed, or
    /// holding something that is not a number — comes out as nil, which the
    /// caller reads as "ask the public API instead".
    static func holderPID(fromSessionDictionary dict: [String: Any]?) -> pid_t? {
        guard let pidNumber = dict?["kCGSSessionSecureInputPID"] as? NSNumber else { return nil }
        guard CFGetTypeID(pidNumber) != CFBooleanGetTypeID() else { return nil }
        let value = pidNumber.doubleValue
        guard value.isFinite, value > 0, value <= Double(pid_t.max),
              value.rounded(.towardZero) == value else { return nil }
        return pid_t(value)
    }

    private static func sessionSecureInputHolderPID() -> pid_t? {
        guard let dict = CGSessionCopyCurrentDictionary() else { return nil }
        return holderPID(fromSessionDictionary: dict as? [String: Any])
    }
}
