// A real CFMachPort needs no TCC grant, unlike a live CGEvent tap.
#if canImport(Testing)
import CoreGraphics
import Foundation
import Testing
@testable import DictationKit

@Suite("Event tap teardown invalidates the port")
struct EventTapTeardownTests {
    @Test("teardown invalidates the port and detaches its run-loop source")
    func invalidateDropsTheRegistration() {
        guard let port = CFMachPortCreate(nil, nil, nil, nil),
              let source = CFMachPortCreateRunLoopSource(nil, port, 0) else {
            Issue.record("CFMachPortCreate failed; the teardown cannot be exercised")
            return
        }
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        #expect(CFMachPortIsValid(port))
        #expect(CFRunLoopSourceIsValid(source))
        EventTapTeardown.invalidate(tap: port, runLoopSource: source)
        #expect(!CFMachPortIsValid(port))
        #expect(!CFRunLoopSourceIsValid(source))
        #expect(!CFRunLoopContainsSource(CFRunLoopGetMain(), source, .commonModes))
    }

    @Test("teardown with no tap is a no-op")
    func nilTapIsANoOp() {
        EventTapTeardown.invalidate(tap: nil, runLoopSource: nil)
    }
}
#endif
