import Foundation

/// A single named assertion failure.
public struct CheckFailure: Error, CustomStringConvertible, Sendable {
    public let message: String
    public var description: String { message }
}

/// Executes the framing/protocol assertions and returns the list of failures
/// (empty == all passed). This mirrors the swift-testing suite in
/// `FramingTests.swift` so the SAME assertions can run today on a
/// Command-Line-Tools-only toolchain (which lacks XCTest and swift-testing)
/// via the `framing-check` executable: `swift run framing-check`.
public func runFramingChecks() -> [CheckFailure] {
    var failures: [CheckFailure] = []

    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(CheckFailure(message: message())) }
    }

    // encode emits big-endian length header
    do {
        let payload = Data("hello".utf8)
        let frame = IpcFraming.encode(payload)
        check(frame.count == 4 + 5, "encode: wrong frame length \(frame.count)")
        check(Array(frame.prefix(4)) == [0x00, 0x00, 0x00, 0x05],
              "encode: header not big-endian: \(Array(frame.prefix(4)))")
        check(Data(frame.suffix(5)) == payload, "encode: payload mismatch")
    }

    // decodeLength is big-endian
    do {
        let a = (try? IpcFraming.decodeLength(Data([0x00, 0x00, 0x01, 0x00]))) ?? -1
        check(a == 256, "decodeLength 0x00000100 == 256, got \(a)")
        let b = (try? IpcFraming.decodeLength(Data([0x12, 0x34, 0x56, 0x78]))) ?? -1
        check(b == 0x12345678, "decodeLength 0x12345678, got \(b)")
    }

    // round-trip
    do {
        let json = #"{"op":"ping","body":{}}"#
        let payload = Data(json.utf8)
        let frame = IpcFraming.encode(payload)
        if let (decoded, consumed) = try? IpcFraming.decode(frame) {
            check(decoded == payload, "roundtrip: payload mismatch")
            check(consumed == frame.count, "roundtrip: consumed \(consumed) != \(frame.count)")
        } else {
            check(false, "roundtrip: decode threw")
        }
    }

    // large payload exercises upper length bytes
    do {
        let payload = Data(String(repeating: "A", count: 1000).utf8)
        let frame = IpcFraming.encode(payload)
        check(Array(frame.prefix(4)) == [0x00, 0x00, 0x03, 0xE8],
              "large: header \(Array(frame.prefix(4)))")
        if let (decoded, consumed) = try? IpcFraming.decode(frame) {
            check(decoded.count == 1000, "large: decoded \(decoded.count)")
            check(consumed == 1004, "large: consumed \(consumed)")
        } else {
            check(false, "large: decode threw")
        }
    }

    // truncated header
    do {
        do {
            _ = try IpcFraming.decodeLength(Data([0x00, 0x00]))
            check(false, "truncatedHeader: expected throw")
        } catch let e as IpcFraming.FramingError {
            check(e == .truncatedHeader(have: 2), "truncatedHeader: wrong error \(e)")
        } catch {
            check(false, "truncatedHeader: wrong error type")
        }
    }

    // truncated payload
    do {
        var frame = Data([0x00, 0x00, 0x00, 0x0A])
        frame.append(Data([0x01, 0x02, 0x03]))
        do {
            _ = try IpcFraming.decode(frame)
            check(false, "truncatedPayload: expected throw")
        } catch let e as IpcFraming.FramingError {
            check(e == .truncatedPayload(have: 3, want: 10),
                  "truncatedPayload: wrong error \(e)")
        } catch {
            check(false, "truncatedPayload: wrong error type")
        }
    }

    // response decodes ok + extra fields
    do {
        if let resp = try? JSONDecoder().decode(
            IpcResponse.self,
            from: Data(#"{"ok":true,"engine_loaded":false}"#.utf8)
        ) {
            check(resp.ok, "responseOk: ok false")
            check(resp.error == nil, "responseOk: error present")
            check(resp["engine_loaded"]?.boolValue == false, "responseOk: engine_loaded")
        } else {
            check(false, "responseOk: decode failed")
        }
    }

    // response decodes error
    do {
        if let resp = try? JSONDecoder().decode(
            IpcResponse.self,
            from: Data(#"{"error":"engine not initialized"}"#.utf8)
        ) {
            check(!resp.ok, "responseError: ok true")
            check(resp.error == "engine not initialized", "responseError: message")
            check(resp.isError, "responseError: isError")
        } else {
            check(false, "responseError: decode failed")
        }
    }

    return failures
}
