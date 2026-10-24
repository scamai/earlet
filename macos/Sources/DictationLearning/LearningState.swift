import Foundation

// ─── The file (LEARNING.md §5, `dictation.learning/1`) ───
//
// Every key is spelled out, snake_case, because the worker reads this file in
// Python and the Windows host writes the same schema. Times are Unix
// milliseconds (UTC). The file holds only corrected spans, counts and times:
// no dictated sentence, no field contents, no application names.
//
// Reading is tolerant within the schema, as the worker and the Windows port
// read it: a missing top-level list (or a missing half of `removed`) is empty,
// an `origin` this version does not know is kept as it is, and keys this
// version does not know are kept in `extra` and written back unchanged. Every
// field of an entry is still required: an entry the reference could not have
// written makes the file undecodable, and the store moves it aside.

public enum LearningOrigin: Codable, Hashable, Sendable {
    /// Learned from the user's corrections; may be re-cased and evicted.
    case correction
    /// Typed by the user; never re-cased by learning, never evicted by it.
    case taught
    /// An origin this version does not know (a newer app wrote it). Kept as it
    /// is: never evicted, never re-cased, written back verbatim.
    case other(String)

    public init(rawValue: String) {
        switch rawValue {
        case "correction": self = .correction
        case "taught": self = .taught
        default: self = .other(rawValue)
        }
    }

    /// The file's spelling.
    public var rawValue: String {
        switch self {
        case .correction: return "correction"
        case .taught: return "taught"
        case .other(let value): return value
        }
    }

    public init(from decoder: Decoder) throws {
        self.init(rawValue: try decoder.singleValueContainer().decode(String.self))
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

// ─── Keys this version does not know ───

/// A JSON value kept only to be written back (LEARNING.md §5: unknown keys are
/// written back unchanged). Integers stay integers.
public enum LearningJSON: Codable, Equatable, Sendable {
    case null
    case bool(Bool)
    case integer(Int64)
    case number(Double)
    case string(String)
    case array([LearningJSON])
    case object([String: LearningJSON])

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Int64.self) {
            self = .integer(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([LearningJSON].self) {
            self = .array(value)
        } else {
            self = .object(try container.decode([String: LearningJSON].self))
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null: try container.encodeNil()
        case .bool(let value): try container.encode(value)
        case .integer(let value): try container.encode(value)
        case .number(let value): try container.encode(value)
        case .string(let value): try container.encode(value)
        case .array(let value): try container.encode(value)
        case .object(let value): try container.encode(value)
        }
    }
}

/// Any key of a JSON object.
struct LearningAnyKey: CodingKey {
    let stringValue: String
    var intValue: Int? { nil }

    init(_ stringValue: String) { self.stringValue = stringValue }
    init?(stringValue: String) { self.stringValue = stringValue }
    init?(intValue: Int) { nil }
}

/// Reads the keys of one object that `Known` does not name, and writes them
/// back beside the known ones.
enum LearningExtraKeys {
    static func decode<Known: CodingKey & CaseIterable>(_ decoder: Decoder, known: Known.Type) throws
        -> [String: LearningJSON] {
        let container = try decoder.container(keyedBy: LearningAnyKey.self)
        let names = Set(Known.allCases.map(\.stringValue))
        var extra: [String: LearningJSON] = [:]
        for key in container.allKeys where !names.contains(key.stringValue) {
            extra[key.stringValue] = try container.decode(LearningJSON.self, forKey: key)
        }
        return extra
    }

    static func encode(_ extra: [String: LearningJSON], to encoder: Encoder) throws {
        guard !extra.isEmpty else { return }
        var container = encoder.container(keyedBy: LearningAnyKey.self)
        for (key, value) in extra {
            try container.encode(value, forKey: LearningAnyKey(key))
        }
    }
}

// ─── Entries ───

public struct LearnedWord: Codable, Equatable, Sendable {
    public var text: String
    public var origin: LearningOrigin
    public var count: Int
    public var firstSeen: Int64
    public var lastSeen: Int64
    /// Keys this version does not know, written back unchanged.
    public var extra: [String: LearningJSON] = [:]

    public init(text: String, origin: LearningOrigin, count: Int, firstSeen: Int64, lastSeen: Int64) {
        self.text = text
        self.origin = origin
        self.count = count
        self.firstSeen = firstSeen
        self.lastSeen = lastSeen
    }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case text, origin, count
        case firstSeen = "first_seen"
        case lastSeen = "last_seen"
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        text = try container.decode(String.self, forKey: .text)
        origin = try container.decode(LearningOrigin.self, forKey: .origin)
        count = try container.decode(Int.self, forKey: .count)
        firstSeen = try container.decode(Int64.self, forKey: .firstSeen)
        lastSeen = try container.decode(Int64.self, forKey: .lastSeen)
        extra = try LearningExtraKeys.decode(decoder, known: CodingKeys.self)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(text, forKey: .text)
        try container.encode(origin, forKey: .origin)
        try container.encode(count, forKey: .count)
        try container.encode(firstSeen, forKey: .firstSeen)
        try container.encode(lastSeen, forKey: .lastSeen)
        try LearningExtraKeys.encode(extra, to: encoder)
    }
}

public struct LearnedAlias: Codable, Equatable, Sendable {
    /// The heard key: lowercase, whitespace collapsed.
    public var heard: String
    public var meant: String
    public var origin: LearningOrigin
    public var count: Int
    public var firstSeen: Int64
    public var lastSeen: Int64
    /// Keys this version does not know, written back unchanged.
    public var extra: [String: LearningJSON] = [:]

    public init(heard: String, meant: String, origin: LearningOrigin, count: Int, firstSeen: Int64, lastSeen: Int64) {
        self.heard = heard
        self.meant = meant
        self.origin = origin
        self.count = count
        self.firstSeen = firstSeen
        self.lastSeen = lastSeen
    }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case heard, meant, origin, count
        case firstSeen = "first_seen"
        case lastSeen = "last_seen"
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        heard = try container.decode(String.self, forKey: .heard)
        meant = try container.decode(String.self, forKey: .meant)
        origin = try container.decode(LearningOrigin.self, forKey: .origin)
        count = try container.decode(Int.self, forKey: .count)
        firstSeen = try container.decode(Int64.self, forKey: .firstSeen)
        lastSeen = try container.decode(Int64.self, forKey: .lastSeen)
        extra = try LearningExtraKeys.decode(decoder, known: CodingKeys.self)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(heard, forKey: .heard)
        try container.encode(meant, forKey: .meant)
        try container.encode(origin, forKey: .origin)
        try container.encode(count, forKey: .count)
        try container.encode(firstSeen, forKey: .firstSeen)
        try container.encode(lastSeen, forKey: .lastSeen)
        try LearningExtraKeys.encode(extra, to: encoder)
    }
}

/// A heard → meant pair waiting for its second dictation.
public struct PendingAlias: Codable, Equatable, Sendable {
    public var heard: String
    public var meant: String
    public var count: Int
    /// The random per-insertion ids already counted, newest last.
    public var dictations: [String]
    public var firstSeen: Int64
    public var lastSeen: Int64
    /// Keys this version does not know, written back unchanged.
    public var extra: [String: LearningJSON] = [:]

    public init(heard: String, meant: String, count: Int, dictations: [String], firstSeen: Int64, lastSeen: Int64) {
        self.heard = heard
        self.meant = meant
        self.count = count
        self.dictations = dictations
        self.firstSeen = firstSeen
        self.lastSeen = lastSeen
    }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case heard, meant, count, dictations
        case firstSeen = "first_seen"
        case lastSeen = "last_seen"
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        heard = try container.decode(String.self, forKey: .heard)
        meant = try container.decode(String.self, forKey: .meant)
        count = try container.decode(Int.self, forKey: .count)
        dictations = try container.decode([String].self, forKey: .dictations)
        firstSeen = try container.decode(Int64.self, forKey: .firstSeen)
        lastSeen = try container.decode(Int64.self, forKey: .lastSeen)
        extra = try LearningExtraKeys.decode(decoder, known: CodingKeys.self)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(heard, forKey: .heard)
        try container.encode(meant, forKey: .meant)
        try container.encode(count, forKey: .count)
        try container.encode(dictations, forKey: .dictations)
        try container.encode(firstSeen, forKey: .firstSeen)
        try container.encode(lastSeen, forKey: .lastSeen)
        try LearningExtraKeys.encode(extra, to: encoder)
    }
}

/// A word the user removed; learning never adds it back by itself.
public struct RemovedWord: Codable, Equatable, Sendable {
    /// The lowercase word.
    public var key: String
    public var at: Int64
    /// Keys this version does not know, written back unchanged.
    public var extra: [String: LearningJSON] = [:]

    public init(key: String, at: Int64) {
        self.key = key
        self.at = at
    }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case key, at
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        key = try container.decode(String.self, forKey: .key)
        at = try container.decode(Int64.self, forKey: .at)
        extra = try LearningExtraKeys.decode(decoder, known: CodingKeys.self)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(key, forKey: .key)
        try container.encode(at, forKey: .at)
        try LearningExtraKeys.encode(extra, to: encoder)
    }
}

/// An alias the user removed; learning never adds it back by itself.
public struct RemovedAlias: Codable, Equatable, Sendable {
    public var heard: String
    /// Lowercase.
    public var meant: String
    public var at: Int64
    /// Keys this version does not know, written back unchanged.
    public var extra: [String: LearningJSON] = [:]

    public init(heard: String, meant: String, at: Int64) {
        self.heard = heard
        self.meant = meant
        self.at = at
    }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case heard, meant, at
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        heard = try container.decode(String.self, forKey: .heard)
        meant = try container.decode(String.self, forKey: .meant)
        at = try container.decode(Int64.self, forKey: .at)
        extra = try LearningExtraKeys.decode(decoder, known: CodingKeys.self)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(heard, forKey: .heard)
        try container.encode(meant, forKey: .meant)
        try container.encode(at, forKey: .at)
        try LearningExtraKeys.encode(extra, to: encoder)
    }
}

public struct RemovedEntries: Codable, Equatable, Sendable {
    public var words: [RemovedWord]
    public var aliases: [RemovedAlias]
    /// Keys this version does not know, written back unchanged.
    public var extra: [String: LearningJSON] = [:]

    public init(words: [RemovedWord] = [], aliases: [RemovedAlias] = []) {
        self.words = words
        self.aliases = aliases
    }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case words, aliases
    }

    /// A missing list is empty.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        words = try container.decodeIfPresent([RemovedWord].self, forKey: .words) ?? []
        aliases = try container.decodeIfPresent([RemovedAlias].self, forKey: .aliases) ?? []
        extra = try LearningExtraKeys.decode(decoder, known: CodingKeys.self)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(words, forKey: .words)
        try container.encode(aliases, forKey: .aliases)
        try LearningExtraKeys.encode(extra, to: encoder)
    }
}

// ─── The whole file ───

/// The whole of `learning.json`. The operations that change it are in
/// LearningRules.swift.
public struct LearningState: Codable, Equatable, Sendable {
    public var schema: String
    /// Most recently seen first; the worker reads that order as relevance.
    public var words: [LearnedWord]
    public var aliases: [LearnedAlias]
    public var pending: [PendingAlias]
    public var removed: RemovedEntries
    /// Keys this version does not know, written back unchanged.
    public var extra: [String: LearningJSON] = [:]

    public init(schema: String = LearningLimits.schema,
                words: [LearnedWord] = [],
                aliases: [LearnedAlias] = [],
                pending: [PendingAlias] = [],
                removed: RemovedEntries = RemovedEntries()) {
        self.schema = schema
        self.words = words
        self.aliases = aliases
        self.pending = pending
        self.removed = removed
    }

    public static var empty: LearningState { LearningState() }

    enum CodingKeys: String, CodingKey, CaseIterable {
        case schema, words, aliases, pending, removed
    }

    /// The schema is required; a missing list (or a missing `removed`) is
    /// empty, as the worker and the Windows port read it.
    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        schema = try container.decode(String.self, forKey: .schema)
        words = try container.decodeIfPresent([LearnedWord].self, forKey: .words) ?? []
        aliases = try container.decodeIfPresent([LearnedAlias].self, forKey: .aliases) ?? []
        pending = try container.decodeIfPresent([PendingAlias].self, forKey: .pending) ?? []
        removed = try container.decodeIfPresent(RemovedEntries.self, forKey: .removed) ?? RemovedEntries()
        extra = try LearningExtraKeys.decode(decoder, known: CodingKeys.self)
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(schema, forKey: .schema)
        try container.encode(words, forKey: .words)
        try container.encode(aliases, forKey: .aliases)
        try container.encode(pending, forKey: .pending)
        try container.encode(removed, forKey: .removed)
        try LearningExtraKeys.encode(extra, to: encoder)
    }
}
