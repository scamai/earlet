import Foundation

/// Wire framing for the stdio IPC protocol:
///   [4-byte BIG-ENDIAN uint32 length][UTF-8 JSON payload]
///
/// Pure functions with no I/O so they can be unit-tested without spawning a
/// subprocess.
public enum IpcFraming {

    /// Errors raised while decoding a frame from a byte buffer.
    public enum FramingError: Error, Equatable, Sendable {
        /// Fewer than 4 bytes were available for the length header.
        case truncatedHeader(have: Int)
        /// The header was read but the payload was shorter than declared.
        case truncatedPayload(have: Int, want: Int)
        /// The header declares a reply longer than any reply can be.
        case oversizedPayload(length: Int, limit: Int)
    }

    /// The longest reply the host reads: 64 MiB.
    ///
    /// A real reply is a transcript and a few numbers, kilobytes at most. A
    /// longer header means the stream is no longer framed: a native library
    /// wrote to the worker's stdout before the protocol owned it, and
    /// "Starting…" read as the length `Star`, 1.4 GB (`asr_server.py`
    /// `_reserve_protocol_stdout`). Trusting it reserved that much memory and
    /// then waited, until the transcription deadline, for bytes that never
    /// came.
    public static let maxResponseLength = 64 << 20

    /// Encode a payload into a framed buffer: 4-byte big-endian length + payload.
    public static func encode(_ payload: Data) -> Data {
        var frame = Data(capacity: 4 + payload.count)
        let length = UInt32(payload.count).bigEndian
        withUnsafeBytes(of: length) { frame.append(contentsOf: $0) }
        frame.append(payload)
        return frame
    }

    /// Read the big-endian uint32 length header from the first 4 bytes.
    public static func decodeLength(_ header: Data) throws -> Int {
        guard header.count >= 4 else {
            throw FramingError.truncatedHeader(have: header.count)
        }
        // Reconstruct big-endian uint32 from the first 4 bytes, independent of
        // the buffer's startIndex.
        let bytes = Array(header.prefix(4))
        let value =
            (UInt32(bytes[0]) << 24) |
            (UInt32(bytes[1]) << 16) |
            (UInt32(bytes[2]) << 8) |
            UInt32(bytes[3])
        return Int(value)
    }

    /// A reply's length header, refused when no reply could be that long.
    /// `decodeLength` stays the plain parser.
    public static func decodeResponseLength(_ header: Data, limit: Int = maxResponseLength) throws -> Int {
        let length = try decodeLength(header)
        guard length <= limit else {
            throw FramingError.oversizedPayload(length: length, limit: limit)
        }
        return length
    }

    /// Decode a complete frame (header + payload) from a single buffer,
    /// returning the payload and the total number of bytes consumed.
    public static func decode(_ buffer: Data) throws -> (payload: Data, consumed: Int) {
        let length = try decodeLength(buffer)
        let available = buffer.count - 4
        guard available >= length else {
            throw FramingError.truncatedPayload(have: max(0, available), want: length)
        }
        let start = buffer.index(buffer.startIndex, offsetBy: 4)
        let end = buffer.index(start, offsetBy: length)
        let payload = Data(buffer[start..<end])
        return (payload, 4 + length)
    }
}
