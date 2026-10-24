// Framing unit tests.
//
// Environment note: the machine running CI here has only the Command Line
// Tools toolchain, which ships NEITHER the `XCTest` module NOR the
// swift-testing `Testing` module (both come with full Xcode). To keep
// `swift test` green today AND have real assertions run under full Xcode in
// Phase 4, the tests are written with swift-testing and guarded by
// `#if canImport(Testing)`.
//
// To run the SAME assertions today (CLT-only), the package exposes them as a
// plain function `runFramingChecks()` that is also invoked by the
// `framing-check` executable target — see `swift run framing-check`. The
// README documents both paths.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationIPC

@Suite("IPC framing")
struct FramingTests {

    @Test("encode emits big-endian length header")
    func encodeBigEndian() throws {
        let payload = Data("hello".utf8)
        let frame = IpcFraming.encode(payload)
        #expect(frame.count == 4 + 5)
        #expect(Array(frame.prefix(4)) == [0x00, 0x00, 0x00, 0x05])
        #expect(Data(frame.suffix(5)) == payload)
    }

    @Test("decodeLength is big-endian")
    func decodeLengthBigEndian() throws {
        #expect(try IpcFraming.decodeLength(Data([0x00, 0x00, 0x01, 0x00])) == 256)
        #expect(try IpcFraming.decodeLength(Data([0x12, 0x34, 0x56, 0x78])) == 0x12345678)
    }

    @Test("round-trips a JSON payload")
    func roundTrip() throws {
        let json = #"{"op":"ping","body":{}}"#
        let payload = Data(json.utf8)
        let frame = IpcFraming.encode(payload)
        let (decoded, consumed) = try IpcFraming.decode(frame)
        #expect(decoded == payload)
        #expect(consumed == frame.count)
    }

    @Test("handles payloads larger than 255 bytes")
    func largePayload() throws {
        let payload = Data(String(repeating: "A", count: 1000).utf8)
        let frame = IpcFraming.encode(payload)
        #expect(Array(frame.prefix(4)) == [0x00, 0x00, 0x03, 0xE8])
        let (decoded, consumed) = try IpcFraming.decode(frame)
        #expect(decoded.count == 1000)
        #expect(consumed == 1004)
    }

    @Test("truncated header throws")
    func truncatedHeader() {
        #expect(throws: IpcFraming.FramingError.truncatedHeader(have: 2)) {
            try IpcFraming.decodeLength(Data([0x00, 0x00]))
        }
    }

    @Test("truncated payload throws")
    func truncatedPayload() {
        var frame = Data([0x00, 0x00, 0x00, 0x0A])
        frame.append(Data([0x01, 0x02, 0x03]))
        #expect(throws: IpcFraming.FramingError.truncatedPayload(have: 3, want: 10)) {
            try IpcFraming.decode(frame)
        }
    }

    @Test("response decodes ok + extra fields")
    func responseOk() throws {
        let resp = try JSONDecoder().decode(
            IpcResponse.self,
            from: Data(#"{"ok":true,"engine_loaded":false}"#.utf8)
        )
        #expect(resp.ok)
        #expect(resp.error == nil)
        #expect(resp["engine_loaded"]?.boolValue == false)
    }

    @Test("response decodes error")
    func responseError() throws {
        let resp = try JSONDecoder().decode(
            IpcResponse.self,
            from: Data(#"{"error":"engine not initialized"}"#.utf8)
        )
        #expect(!resp.ok)
        #expect(resp.error == "engine not initialized")
        #expect(resp.isError)
    }
}
#endif
