import Foundation

/// A request envelope: {"op": "<name>", "body": { ... }}.
public struct IpcRequest: Encodable, Sendable {
    public let op: String
    public let body: JSONValue

    public init(op: String, body: JSONValue) {
        self.op = op
        self.body = body
    }
}

/// A response envelope. The engine replies with either {"ok": true, ...} or
/// {"error": "..."}. Extra fields land in `extra` for flexible access.
public struct IpcResponse: Decodable, Sendable {
    public let ok: Bool
    public let error: String?
    /// The full decoded payload, for accessing op-specific fields such as
    /// `engine_loaded`, `result`, `frame_id`, etc.
    public let raw: [String: JSONValue]

    private struct DynamicKey: CodingKey {
        var stringValue: String
        var intValue: Int? { nil }
        init?(stringValue: String) { self.stringValue = stringValue }
        init?(intValue: Int) { return nil }
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: DynamicKey.self)
        var dict: [String: JSONValue] = [:]
        for key in container.allKeys {
            dict[key.stringValue] = try container.decode(JSONValue.self, forKey: key)
        }
        self.raw = dict
        self.ok = dict["ok"]?.boolValue ?? false
        self.error = dict["error"]?.stringValue
    }

    public subscript(key: String) -> JSONValue? { raw[key] }

    /// True when the engine reported a failure (`error` present or `ok` false).
    public var isError: Bool { error != nil || !ok }
}

/// Errors surfaced by the IPC client itself (transport-level, not engine-level).
public enum IpcClientError: Error, Sendable {
    case notStarted
    case alreadyStarted
    case processTerminated(status: Int32)
    case encodingFailed
    case decodingFailed(underlying: String)
    case engineError(String)
    case eof
}
