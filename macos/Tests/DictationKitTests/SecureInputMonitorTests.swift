// The holder lookup reads the PUBLIC session dictionary
// (`CGSessionCopyCurrentDictionary`, CGSession.h) plus one undocumented key.
// The status decision and the key read are pure and pinned here without a
// window server session; `currentStatus()` is checked against the same two
// reads done directly, which also proves the public symbol is really linked —
// the previous dlsym of a symbol macOS does not export could only ever fail,
// silently, at runtime.

#if canImport(Testing)
import CoreGraphics
import Foundation
import Testing
import Carbon.HIToolbox
@testable import DictationKit

@Suite("Secure Input status")
@MainActor struct SecureInputMonitorTests {

    @Test("a holder pid names the holder — this process or another")
    func holderPIDNamesHolder() {
        let own = ProcessInfo.processInfo.processIdentifier
        #expect(SecureInputMonitor.status(holderPID: own, secureInputEnabled: true) == .activeHeldBySelf)
        // A stale session entry cannot override the documented inactive flag.
        #expect(SecureInputMonitor.status(holderPID: own, secureInputEnabled: false) == .inactive)
        let other = own + 1
        #expect(SecureInputMonitor.status(holderPID: other, secureInputEnabled: true) == .activeHeldByPID(other))
        #expect(SecureInputMonitor.status(holderPID: other, secureInputEnabled: false) == .inactive)
    }

    @Test("no holder read falls back to the public, holder-blind flag")
    func noHolderFallsBackToPublicFlag() {
        #expect(SecureInputMonitor.status(holderPID: nil, secureInputEnabled: true) == .activeUnknownHolder)
        #expect(SecureInputMonitor.status(holderPID: nil, secureInputEnabled: false) == .inactive)
    }

    @Test("the undocumented key is read defensively")
    func keyReadIsDefensive() {
        #expect(SecureInputMonitor.holderPID(
            fromSessionDictionary: ["kCGSSessionSecureInputPID": NSNumber(value: 4242)]) == 4242)
        // Secure Input off (key absent), the key renamed, or a value that is
        // not a number: all read as "no answer", never a trap.
        #expect(SecureInputMonitor.holderPID(fromSessionDictionary: [:]) == nil)
        #expect(SecureInputMonitor.holderPID(
            fromSessionDictionary: ["kCGSSessionSecureInputPID": "not a number"]) == nil)
        #expect(SecureInputMonitor.holderPID(fromSessionDictionary: nil) == nil)
        for number in [NSNumber(value: true), NSNumber(value: 0), NSNumber(value: -1),
                       NSNumber(value: Int64.max), NSNumber(value: 1.5),
                       NSNumber(value: Double.nan), NSNumber(value: Double.infinity)] {
            #expect(SecureInputMonitor.holderPID(
                fromSessionDictionary: ["kCGSSessionSecureInputPID": number]) == nil)
        }
    }

    @Test("background observer reads return through the main thread")
    func backgroundReadCompletes() async {
        _ = await Task.detached { SecureInputMonitor.currentStatus() }.value
    }

    @Test("currentStatus composes the public session dictionary and the public flag")
    func currentStatusComposesTheTwoReads() {
        let dict = CGSessionCopyCurrentDictionary().flatMap { $0 as? [String: Any] }
        let expected = SecureInputMonitor.status(
            holderPID: SecureInputMonitor.holderPID(fromSessionDictionary: dict),
            secureInputEnabled: IsSecureEventInputEnabled())
        #expect(SecureInputMonitor.currentStatus() == expected)
    }
}
#endif
