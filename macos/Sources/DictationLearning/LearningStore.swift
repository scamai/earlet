import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif
// `NSClassFromString` lives in ObjectiveC's runtime shim on Darwin, re-exported
// by Foundation; imported explicitly so the test guard cannot silently stop
// resolving if that re-export changes. Linux has no ObjectiveC module.
#if canImport(ObjectiveC)
import ObjectiveC
#endif

/// Reads and writes `learning.json` (LEARNING.md §5).
///
/// ## One writer, never a torn file
///
/// The host is the only writer; the worker re-reads the file whenever its size
/// or mtime changes. A write therefore goes to a sibling temp file, is flushed,
/// and lands with `rename(2)`, which replaces the old file atomically on the
/// same volume: the worker sees the old file or the new one, never half of
/// either. A reader that saw truncated JSON would treat it as empty and drop
/// every learned word until the next write.
///
/// A directory this store creates is 0700, and the file is 0600 from before the
/// rename. `FileManager.replaceItemAt` is not used: Apple documents that it
/// keeps the replaced file's permissions, so a 0644 file would stay 0644.
///
/// ## A file this build has not read is never overwritten
///
/// Two failures differ (LEARNING.md §5):
///
/// - **Undecodable**: the bytes were read but do not parse, or carry another
///   schema (a newer build's, or a hand edit gone wrong). The file is renamed
///   to `learning.json.bad`, replacing an older one, and learning starts empty.
/// - **Unreadable**: the read itself failed (an I/O or permission error). The
///   file is left exactly where it is.
///
/// After an unreadable file, or an undecodable one that could not be moved
/// aside, the store refuses every write until a later `load()` reads the file
/// (or finds it gone, or moves it aside), so the only copy of the user's
/// entries is never replaced by a list built without it.
public final class LearningStore: @unchecked Sendable {

    public enum Failure: Error, Equatable {
        case noLearningLocation
        /// A test tried to reach the real user's file. See `isUnderTest`.
        case refusedUnderTest
        case encodingFailed
        case notWritable(String)
        /// The last load could not read the existing file, or could not move
        /// an undecodable one aside; writing would destroy it.
        case keepingUnreadableFile
    }

    public enum LoadOutcome: Equatable, Sendable {
        /// No file yet: first run, or after the user cleared it by hand.
        case missing
        case loaded
        /// Undecodable (not JSON, or another schema): moved to
        /// `learning.json.bad`.
        case quarantined
        /// Undecodable and could not be moved aside; writes are refused until
        /// a later load succeeds.
        case quarantineFailed
        /// The read failed (an I/O or permission error). The file is left in
        /// place and writes are refused until a later load reads it.
        case unreadable

        /// True when the file on disk was not read and must not be replaced.
        public var keepsFileUnread: Bool {
            self == .unreadable || self == .quarantineFailed
        }
    }

    public let url: URL
    private let fileManager: FileManager
    private let lock = NSLock()
    // Guarded by `lock`.
    private var writesRefused = false

    /// A store at an explicit location. Tests always use this.
    public init(url: URL, fileManager: FileManager = .default) {
        self.url = url
        self.fileManager = fileManager
    }

    /// The store at the user's real file.
    ///
    /// Refuses under XCTest, for the reason `DictationVocabularyWriter` spells
    /// out: on 2026-08-17 a test that forgot one URL parameter replaced the
    /// owner's 32 curated vocabulary terms with a three-word fixture. Under a
    /// test every store must name its file.
    public static func live(fileManager: FileManager = .default) throws -> LearningStore {
        if isUnderTest { throw Failure.refusedUnderTest }
        guard let url = defaultURL(fileManager: fileManager) else { throw Failure.noLearningLocation }
        return LearningStore(url: url, fileManager: fileManager)
    }

    /// `~/Library/Application Support/Dictation/dictation/learning.json`,
    /// beside the user's `bias_aliases.json`; the worker resolves the same
    /// directory.
    public static func defaultURL(fileManager: FileManager = .default) -> URL? {
        guard let base = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
        else { return nil }
        return base
            .appendingPathComponent("Dictation", isDirectory: true)
            .appendingPathComponent("dictation", isDirectory: true)
            .appendingPathComponent("learning.json", isDirectory: false)
    }

    /// True when the XCTest runtime is loaded in this process. The shipped app
    /// never links XCTest; a test bundle always has. On Darwin XCTestCase is an
    /// Objective-C class; on Linux it is a Swift class that resolves only by
    /// its module-qualified name (checked with swift-corelibs-xctest, Swift 6.2).
    static var isUnderTest: Bool {
        NSClassFromString("XCTestCase") != nil || NSClassFromString("XCTest.XCTestCase") != nil
    }

    /// Where an undecodable file is kept: `learning.json.bad`.
    public var quarantineURL: URL {
        url.deletingLastPathComponent().appendingPathComponent(url.lastPathComponent + ".bad", isDirectory: false)
    }

    // ─── Reading ───

    /// The stored state, or empty. Never throws: every failure is an outcome.
    /// Each call reads the file again, and decides again whether writes are
    /// allowed.
    public func load() -> (state: LearningState, outcome: LoadOutcome) {
        let result = read()
        lock.lock()
        writesRefused = result.outcome.keepsFileUnread
        lock.unlock()
        return result
    }

    private func read() -> (state: LearningState, outcome: LoadOutcome) {
        guard fileManager.fileExists(atPath: url.path) else {
            return (.empty, .missing)
        }
        let data: Data
        do {
            data = try Data(contentsOf: url)
        } catch {
            // Gone since the check: nothing to keep.
            if !fileManager.fileExists(atPath: url.path) { return (.empty, .missing) }
            // Not read, so not known to be bad: left where it is (a directory
            // in its place too).
            return (.empty, .unreadable)
        }
        if let state = Self.decode(data) {
            return (state, .loaded)
        }
        if Self.rename(url, to: quarantineURL) {
            return (.empty, .quarantined)
        }
        return (.empty, .quarantineFailed)
    }

    /// A state only when the bytes parse and carry this schema.
    public static func decode(_ data: Data) -> LearningState? {
        guard let state = try? JSONDecoder().decode(LearningState.self, from: data),
              state.schema == LearningLimits.schema
        else { return nil }
        return state
    }

    /// Deterministic bytes (sorted keys), so an unchanged state encodes to an
    /// unchanged file and a caller can skip the write.
    public static func encode(_ state: LearningState) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        do {
            return try encoder.encode(state)
        } catch {
            throw Failure.encodingFailed
        }
    }

    // ─── Writing ───

    public func save(_ state: LearningState) throws {
        try write(Self.encode(state))
    }

    /// Writes already-encoded bytes; see the type comment for the protocol.
    public func write(_ data: Data) throws {
        lock.lock()
        defer { lock.unlock() }
        if writesRefused { throw Failure.keepingUnreadableFile }

        let directory = url.deletingLastPathComponent()
        if !fileManager.fileExists(atPath: directory.path) {
            do {
                try fileManager.createDirectory(at: directory, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
            } catch {
                throw Failure.notWritable(error.localizedDescription)
            }
        }
        let temp = directory.appendingPathComponent(".\(url.lastPathComponent).\(UUID().uuidString).tmp",
                                                    isDirectory: false)
        do {
            // 0600 before the rename, so the file never exists at its real
            // name with wider permissions.
            guard fileManager.createFile(atPath: temp.path, contents: data,
                                         attributes: [.posixPermissions: 0o600]) else {
                throw Failure.notWritable("could not create a temporary file")
            }
            // Flushed before it replaces anything: a rename that reaches disk
            // ahead of the data would leave an empty learning.json after a crash.
            let handle = try FileHandle(forWritingTo: temp)
            try handle.synchronize()
            try handle.close()
            guard Self.rename(temp, to: url) else {
                throw Failure.notWritable("could not replace the file (errno \(errno))")
            }
        } catch {
            try? fileManager.removeItem(at: temp)
            if let failure = error as? Failure { throw failure }
            throw Failure.notWritable(error.localizedDescription)
        }
    }

    /// `rename(2)`: atomic, replaces an existing file at `destination`.
    private static func rename(_ source: URL, to destination: URL) -> Bool {
        source.withUnsafeFileSystemRepresentation { from in
            destination.withUnsafeFileSystemRepresentation { to in
                guard let from, let to else { return false }
                #if canImport(Darwin)
                return Darwin.rename(from, to) == 0
                #else
                return Glibc.rename(from, to) == 0
                #endif
            }
        }
    }
}
