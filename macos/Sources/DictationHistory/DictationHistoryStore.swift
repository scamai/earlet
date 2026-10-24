import CryptoKit
import Darwin
import Foundation

// The history folder: one sealed file per dictation and one per kept
// recording, written whole and renamed into place, read back only with the
// history key. The format is DictationHistorySeal.swift, the key
// DictationHistoryKeys.swift.

/// The dictation history on disk.
///
/// The folder (`defaultDirectory()`,
/// `~/Library/Application Support/Dictation/dictation/history`, 0700) holds
/// `entries/<id>.sealed` for each dictation and `audio/<id>.sealed` for each
/// recording kept with one (directories 0700, files 0600). Each file is
/// sealed whole (AES-256-GCM, `DictationHistorySeal`); the file names are
/// the dictations' random ids and say nothing else, though the file system
/// still records when each file was written. The recording is the take's
/// 16 kHz mono PCM16, about 32 KB a second.
///
/// Never a plaintext fallback: without its key the history reads as locked,
/// and a save that cannot get the key is not made. A keychain that refused
/// is not asked again until `reload()` (Try Again), so saves queued behind a
/// denied prompt do not raise it once each. A file sealed under a key the
/// keychain no longer holds, or that fails its tag, is counted as unreadable
/// and offered for deletion; one that cannot be opened just now (its
/// permissions, say) is neither counted nor deleted, and nor is one that
/// opens but that this version cannot decode: a newer version wrote it.
public actor DictationHistoryStore {
    /// What the history holds, newest first.
    public struct Snapshot: Equatable, Sendable {
        public var entries: [DictationHistoryEntry]
        /// The dictations whose recording is kept.
        public var recordings: Set<UUID>
        /// The kept recordings' size on disk.
        public var recordingBytes: Int64
        /// Sealed files this key cannot open: sealed under a key the
        /// keychain no longer holds, or damaged.
        public var unreadable: Int
        /// Dictations this key opens but this version cannot read: written
        /// by a newer version of the app. Kept as they are, with their
        /// recordings, and never offered for deletion.
        public var newer: Int

        public init(entries: [DictationHistoryEntry] = [], recordings: Set<UUID> = [],
                    recordingBytes: Int64 = 0, unreadable: Int = 0, newer: Int = 0) {
            self.entries = entries
            self.recordings = recordings
            self.recordingBytes = recordingBytes
            self.unreadable = unreadable
            self.newer = newer
        }
    }

    public enum Failure: Error, Equatable, Sendable {
        /// The key could not be read or made: nothing can be shown or saved.
        case locked(DictationHistoryKeyError)
        /// A file operation failed; carries its domain and code, never a path.
        case disk(String)
    }

    /// How long the history keeps what, as Settings chooses it. Nil keeps
    /// forever; zero keeps none.
    public struct Retention: Equatable, Sendable {
        public var dictations: TimeInterval?
        public var recordings: TimeInterval?
        /// The recordings' total size at most; the oldest go first.
        public var recordingBytes: Int64

        public init(dictations: TimeInterval?, recordings: TimeInterval?, recordingBytes: Int64) {
            self.dictations = dictations
            self.recordings = recordings
            self.recordingBytes = recordingBytes
        }
    }

    /// What one prune removed.
    public struct Pruned: Equatable, Sendable {
        public var dictations = 0
        public var recordings = 0
        public init(dictations: Int = 0, recordings: Int = 0) {
            self.dictations = dictations
            self.recordings = recordings
        }
    }

    static let sealedExtension = "sealed"

    private let directory: URL
    private let keys: any DictationHistoryKeys
    private let log: @Sendable (String) -> Void
    private var key: SymmetricKey?
    /// The keychain's refusal, kept until `reload()` (Try Again): a save
    /// queued behind a prompt the user denied must not raise it again.
    private var refusal: DictationHistoryKeyError?
    private var snapshot: Snapshot?
    private var recordingSizes: [UUID: Int64] = [:]
    private var unreadableFiles: [URL] = []

    public init(directory: URL, keys: any DictationHistoryKeys, log: @escaping @Sendable (String) -> Void = { _ in }) {
        self.directory = directory
        self.keys = keys
        self.log = log
    }

    /// `~/Library/Application Support/Dictation/dictation/history`, beside
    /// the recovery folder and `learning.json`.
    public static func defaultDirectory() -> URL {
        let fm = FileManager.default
        let base = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support", isDirectory: true)
        return base
            .appendingPathComponent("Dictation", isDirectory: true)
            .appendingPathComponent("dictation", isDirectory: true)
            .appendingPathComponent("history", isDirectory: true)
    }

    var entriesDirectory: URL { directory.appendingPathComponent("entries", isDirectory: true) }
    var audioDirectory: URL { directory.appendingPathComponent("audio", isDirectory: true) }

    func entryURL(_ id: UUID) -> URL {
        entriesDirectory.appendingPathComponent("\(id.uuidString).\(Self.sealedExtension)")
    }

    func audioURL(_ id: UUID) -> URL {
        audioDirectory.appendingPathComponent("\(id.uuidString).\(Self.sealedExtension)")
    }

    // ─── Reading ───

    /// The history, read once and then kept in memory: every later change
    /// goes through this actor, which updates both.
    public func load() throws(Failure) -> Snapshot {
        if let snapshot { return snapshot }
        let key = try readKey()
        var entries: [DictationHistoryEntry] = []
        var unreadable: [URL] = []
        var newer: Set<UUID> = []
        // Dates in Foundation's own form, seconds since 2001: exact both
        // ways. Seconds since 1970 lost the last bits of a Date on the way
        // back, so a saved dictation no longer equalled itself.
        let decoder = JSONDecoder()
        for file in sealedFiles(in: entriesDirectory) {
            guard let id = UUID(uuidString: file.deletingPathExtension().lastPathComponent) else {
                unreadable.append(file)
                continue
            }
            let data: Data
            do {
                data = try Data(contentsOf: file)
            } catch {
                log("history: a dictation could not be read just now (\(Self.describe(error))); it is left alone")
                continue
            }
            guard let key else {
                unreadable.append(file)
                continue
            }
            let plain: Data
            do {
                plain = try DictationHistorySeal.open(data, kind: .entry, id: id, key: key)
            } catch {
                unreadable.append(file)
                continue
            }
            // Opened with this key, so it is the user's own dictation: one
            // this version cannot decode was written by a newer one, and it
            // is kept as it is rather than offered for deletion.
            guard let entry = try? decoder.decode(DictationHistoryEntry.self, from: plain) else {
                newer.insert(id)
                continue
            }
            guard entry.id == id else {
                unreadable.append(file)
                continue
            }
            entries.append(entry)
        }
        entries.sort { $0.recordedAt > $1.recordedAt }
        let known = Set(entries.map(\.id)).union(newer)
        var sizes: [UUID: Int64] = [:]
        for file in sealedFiles(in: audioDirectory) {
            guard let id = UUID(uuidString: file.deletingPathExtension().lastPathComponent), known.contains(id) else {
                // A recording whose dictation is gone, or never was written:
                // nothing can list it, so nothing should keep it.
                if !unreadable.contains(where: { $0.deletingPathExtension().lastPathComponent
                    == file.deletingPathExtension().lastPathComponent }) {
                    remove(file, what: "a recording without its dictation")
                }
                continue
            }
            // A newer version's dictation keeps its recording, unlisted here.
            if !newer.contains(id) { sizes[id] = Self.size(of: file) }
        }
        unreadableFiles = unreadable
        recordingSizes = sizes
        let loaded = Snapshot(entries: entries, recordings: Set(sizes.keys),
                              recordingBytes: sizes.values.reduce(0, +), unreadable: unreadable.count,
                              newer: newer.count)
        snapshot = loaded
        if !unreadable.isEmpty {
            log("history: \(unreadable.count) saved file(s) cannot be opened with this Mac's history key")
        }
        if !newer.isEmpty {
            log("history: \(newer.count) dictation(s) saved by a newer version are kept as they are")
        }
        return loaded
    }

    /// A kept recording: its 16 kHz mono PCM16, or nil when none is kept.
    public func recording(_ id: UUID) throws(Failure) -> Data? {
        _ = try load()
        guard recordingSizes[id] != nil else { return nil }
        guard let key = try readKey() else { return nil }
        do {
            let data = try Data(contentsOf: audioURL(id))
            return try DictationHistorySeal.open(data, kind: .audio, id: id, key: key)
        } catch {
            log("history: a recording could not be opened (\(Self.describe(error)))")
            return nil
        }
    }

    // ─── Writing ───

    /// Saves one dictation, and its recording when one is given. The key is
    /// made here on the first save. A recording that cannot be written costs
    /// only the recording; the dictation stays saved.
    @discardableResult
    public func append(_ entry: DictationHistoryEntry, recording: Data?) throws(Failure) -> Snapshot {
        var current = try load()
        let key = try writingKey()
        try prepareDirectories()
        let encoder = JSONEncoder()
        let sealed: Data
        do {
            sealed = try DictationHistorySeal.seal(try encoder.encode(entry), kind: .entry, id: entry.id, key: key)
        } catch {
            throw .disk(Self.describe(error))
        }
        try write(sealed, to: entryURL(entry.id))
        current.entries.removeAll { $0.id == entry.id }
        let index = current.entries.firstIndex { $0.recordedAt <= entry.recordedAt } ?? current.entries.endIndex
        current.entries.insert(entry, at: index)
        if let recording, !recording.isEmpty {
            do {
                let sealedAudio = try DictationHistorySeal.seal(recording, kind: .audio, id: entry.id, key: key)
                try write(sealedAudio, to: audioURL(entry.id))
                let bytes = Int64(sealedAudio.count)
                recordingSizes[entry.id] = bytes
                current.recordings.insert(entry.id)
            } catch {
                log("history: a recording could not be saved; its dictation is (\(Self.describe(error)))")
            }
        }
        current.recordingBytes = recordingSizes.values.reduce(0, +)
        snapshot = current
        return current
    }

    /// Deletes one dictation and its recording.
    @discardableResult
    public func delete(_ id: UUID) throws(Failure) -> Snapshot {
        var current = try load()
        try removeOrThrow(entryURL(id))
        // Gone from the list as soon as its file is, even when its recording
        // then cannot be deleted (the next load sweeps a recording left
        // without its dictation).
        current.entries.removeAll { $0.id == id }
        snapshot = current
        try removeOrThrow(audioURL(id))
        recordingSizes[id] = nil
        current.recordings.remove(id)
        current.recordingBytes = recordingSizes.values.reduce(0, +)
        snapshot = current
        return current
    }

    /// Deletes the files sealed under a key this Mac no longer holds, or
    /// damaged ones.
    @discardableResult
    public func deleteUnreadable() throws(Failure) -> Snapshot {
        var current = try load()
        for file in unreadableFiles {
            try removeOrThrow(file)
            let name = file.deletingPathExtension().lastPathComponent
            if let id = UUID(uuidString: name) { try removeOrThrow(audioURL(id)) }
        }
        unreadableFiles = []
        current.unreadable = 0
        snapshot = current
        return current
    }

    /// Deletes everything, and destroys the key first: from then on nothing
    /// left on the disk (a file a failed delete left, a block the file
    /// system has not reused) can be opened. The next save makes a new key.
    public func deleteAll() throws(Failure) {
        do {
            try keys.destroy()
        } catch {
            throw .locked(error)
        }
        key = nil
        snapshot = Snapshot()
        recordingSizes = [:]
        unreadableFiles = []
        for folder in [entriesDirectory, audioDirectory] {
            for file in (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? [] {
                remove(file, what: "a history file")
            }
        }
    }

    /// Applies the retention: dictations older than it go with their
    /// recordings, recordings older than theirs go alone, and then the
    /// oldest recordings until the rest fit the size limit.
    @discardableResult
    public func prune(_ retention: Retention, now: Date) throws(Failure) -> Pruned {
        do {
            return try pruneFiles(retention, now: now)
        } catch {
            // Some files may have gone before the failure: read the folder
            // again next time rather than keep listing them.
            snapshot = nil
            recordingSizes = [:]
            throw error
        }
    }

    private func pruneFiles(_ retention: Retention, now: Date) throws(Failure) -> Pruned {
        var current = try load()
        var pruned = Pruned()
        if let age = retention.dictations {
            let cutoff = now.addingTimeInterval(-age)
            for entry in current.entries where entry.recordedAt < cutoff {
                try removeOrThrow(entryURL(entry.id))
                if recordingSizes[entry.id] != nil {
                    try removeOrThrow(audioURL(entry.id))
                    recordingSizes[entry.id] = nil
                    pruned.recordings += 1
                }
                pruned.dictations += 1
            }
            current.entries.removeAll { $0.recordedAt < cutoff }
        }
        let dates = Dictionary(uniqueKeysWithValues: current.entries.map { ($0.id, $0.recordedAt) })
        var kept = recordingSizes.keys.sorted { (dates[$0] ?? .distantPast) < (dates[$1] ?? .distantPast) }
        if let age = retention.recordings {
            let cutoff = now.addingTimeInterval(-age)
            for id in kept where (dates[id] ?? .distantPast) < cutoff {
                try removeOrThrow(audioURL(id))
                recordingSizes[id] = nil
                pruned.recordings += 1
            }
            kept.removeAll { recordingSizes[$0] == nil }
        }
        var total = recordingSizes.values.reduce(0, +)
        for id in kept where total > retention.recordingBytes {
            try removeOrThrow(audioURL(id))
            total -= recordingSizes[id] ?? 0
            recordingSizes[id] = nil
            pruned.recordings += 1
        }
        current.recordings = Set(recordingSizes.keys)
        current.recordingBytes = recordingSizes.values.reduce(0, +)
        snapshot = current
        return pruned
    }

    /// Forgets what was read, so the next `load()` reads the disk and the
    /// keychain again: Try Again after the history was locked.
    public func reload() {
        snapshot = nil
        key = nil
        refusal = nil
    }

    // ─── The key ───

    private func readKey() throws(Failure) -> SymmetricKey? {
        if let key { return key }
        if let refusal { throw .locked(refusal) }
        do {
            key = try keys.existing()
        } catch {
            refusal = error
            throw .locked(error)
        }
        return key
    }

    private func writingKey() throws(Failure) -> SymmetricKey {
        if let key = try readKey() { return key }
        do {
            let made = try keys.create()
            key = made
            log("history: made the history key")
            return made
        } catch {
            refusal = error
            throw .locked(error)
        }
    }

    // ─── Files ───

    private func prepareDirectories() throws(Failure) {
        for folder in [directory, entriesDirectory, audioDirectory] {
            do {
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                        attributes: [.posixPermissions: 0o700])
                try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder.path)
            } catch {
                throw .disk(Self.describe(error))
            }
        }
    }

    /// Written whole to a private temporary file beside the target, then
    /// renamed over it: a crash leaves the old file or the new one, never
    /// half of either.
    private func write(_ data: Data, to url: URL) throws(Failure) {
        let temporary = url.deletingLastPathComponent()
            .appendingPathComponent(".\(UUID().uuidString).tmp")
        guard FileManager.default.createFile(atPath: temporary.path, contents: data,
                                             attributes: [.posixPermissions: 0o600]) else {
            throw .disk("NSCocoaErrorDomain \(CocoaError.fileWriteUnknown.rawValue)")
        }
        // Flushed before it replaces anything: a rename that reaches the disk
        // ahead of the data would leave an empty file after a crash, which
        // then fails its tag.
        do {
            let handle = try FileHandle(forWritingTo: temporary)
            try handle.synchronize()
            try handle.close()
        } catch {
            unlink(temporary.path)
            throw .disk(Self.describe(error))
        }
        guard rename(temporary.path, url.path) == 0 else {
            let code = errno
            unlink(temporary.path)
            throw .disk("NSPOSIXErrorDomain \(code)")
        }
    }

    private func sealedFiles(in folder: URL) -> [URL] {
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        for file in files where file.lastPathComponent.hasPrefix(".") && file.pathExtension == "tmp" {
            // A write that died before its rename.
            remove(file, what: "an unfinished history file")
        }
        return files.filter { $0.pathExtension == Self.sealedExtension }
    }

    private func removeOrThrow(_ url: URL) throws(Failure) {
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do {
            try FileManager.default.removeItem(at: url)
        } catch {
            throw .disk(Self.describe(error))
        }
    }

    private func remove(_ url: URL, what: String) {
        do {
            try FileManager.default.removeItem(at: url)
        } catch {
            log("history: \(what) could not be deleted (\(Self.describe(error)))")
        }
    }

    private static func size(of url: URL) -> Int64 {
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size]) as? NSNumber
        return size?.int64Value ?? 0
    }

    /// Domain and code only: an I/O error's text carries the path, and the
    /// path the user's name.
    static func describe(_ error: Error) -> String {
        let nsError = error as NSError
        return "\(nsError.domain) \(nsError.code)"
    }
}
