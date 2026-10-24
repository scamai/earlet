import Foundation
import Darwin

// Long takes are never lost (LIMIT 2026-10-10, rule zero: no stop loses
// words) — the Mac half of the contract the Windows app shipped in
// DictationTakeSpool.cs. While a take records, its audio is appended to a
// local spool file as 16 kHz PCM16 (32 KB a second), flushed with every
// buffer, so a crash keeps everything said before it. The spool is deleted
// once the take's words went in, and at most one take that could not be
// transcribed is kept for Recover Last Dictation.

/// One take as the spool writes it: its file, its start, and how much audio
/// it holds so far.
public final class DictationTake: @unchecked Sendable {
    /// Where its audio is written.
    public let path: URL
    /// When the press was admitted, on the wall clock.
    public let startedAt: Date

    private let lock = NSLock()
    private var appendedSamples = 0
    private var failedFlag = false
    private var droppedChunks = 0

    init(path: URL, startedAt: Date) {
        self.path = path
        self.startedAt = startedAt
    }

    /// How much audio the take holds, from its 16 kHz sample count: what
    /// "0:42 long" names.
    public var captured: TimeInterval {
        TimeInterval(samples) / TimeInterval(DictationTakeSpool.sampleRate)
    }

    /// The samples appended so far (counted at conversion, as they arrive).
    public var samples: Int { lock.lock(); defer { lock.unlock() }; return appendedSamples }

    /// Whether a write failed: the file no longer holds the whole take, so
    /// it is never offered as one.
    public var failed: Bool { lock.lock(); defer { lock.unlock() }; return failedFlag }

    /// Buffers dropped for arriving at another rate mid-take.
    public var dropped: Int { lock.lock(); defer { lock.unlock() }; return droppedChunks }

    /// The open file. The spool's file work only.
    var handle: FileHandle?

    func noteAppended(_ count: Int) {
        lock.lock(); appendedSamples += count; lock.unlock()
    }

    func noteDropped() {
        lock.lock(); droppedChunks += 1; lock.unlock()
    }

    func markFailed() {
        lock.lock(); failedFlag = true; lock.unlock()
    }
}

/// The take kept for Recover Last Dictation.
public struct DictationKeptTake: Equatable, Sendable {
    /// When it was recorded.
    public var recordedAt: Date
    /// How much audio it holds.
    public var length: TimeInterval
    /// It was found at launch, left by a Dictation that closed or crashed
    /// while the take was recorded or transcribed; false for a take kept
    /// because it could not be transcribed.
    public var interrupted: Bool
    /// Keeping it deleted an earlier take that was still offered: one is
    /// kept at a time, and the surface says so.
    public var replacedEarlier: Bool

    public init(recordedAt: Date, length: TimeInterval, interrupted: Bool, replacedEarlier: Bool = false) {
        self.recordedAt = recordedAt
        self.length = length
        self.interrupted = interrupted
        self.replacedEarlier = replacedEarlier
    }
}

/// What reading the kept take back found.
public struct DictationKeptAudio: Equatable, Sendable {
    /// Its audio at 16 kHz, as the transcriber takes it; nil when there is
    /// none to give.
    public var samples: [Float]?
    /// It could not be opened or read just now (its permissions deny it,
    /// say): it is not damaged, so it stays for another try. False with no
    /// audio means it is gone, is not a take this build wrote, or holds
    /// nothing.
    public var unavailable: Bool

    public init(samples: [Float]?, unavailable: Bool) {
        self.samples = samples
        self.unavailable = unavailable
    }
}

/// The take spool's knobs, one value the model reads (the Windows app's
/// `DictationTakeOptions`). Tests point the directory at a scratch folder
/// and run file work at once.
public struct DictationTakeOptions: Sendable {
    /// Where a take's audio is written while it records, and where the one
    /// kept take lives.
    public var directory: URL
    /// How long a kept take is offered: older ones are deleted at the next
    /// launch, unheard (the lead's default for owner decision 3).
    public var keptFor: TimeInterval
    /// How the spool's file work runs: in order, off the capture's collector
    /// task, one item at a time.
    public var runFileWork: @Sendable (@escaping @Sendable () -> Void) -> Void

    public init(
        directory: URL = DictationTakeSpool.defaultDirectory(),
        keptFor: TimeInterval = 7 * 24 * 60 * 60,
        runFileWork: @escaping @Sendable (@escaping @Sendable () -> Void) -> Void = DictationTakeSpool.runSerially
    ) {
        self.directory = directory
        self.keptFor = keptFor
        self.runFileWork = runFileWork
    }
}

/// The take spool: every take's audio on disk while it is recorded, deleted
/// once its words went in, and the last one that could not be transcribed
/// kept for Recover Last Dictation.
///
/// The files. In `DictationTakeOptions.directory`
/// (`~/Library/Application Support/Dictation/dictation/recovery`): one
/// `take-<id>.pcm` per take while it is recorded and transcribed, and at
/// most one `kept.pcm`. Each is a 24-byte header (`DICTTAKE`, format 1, the
/// sample rate, the start as microseconds since 1970 UTC) and then the take
/// as little-endian 16-bit mono at 16 kHz, appended as it arrives, so a
/// crash keeps everything said before it. Nothing else is written: no words,
/// no app, no window.
///
/// Off the collector task, in order. The audio tap only converts; opening,
/// every write, closing, keeping, deleting and reading run one at a time
/// through `DictationTakeOptions.runFileWork`, so a slow disk never stalls
/// the microphone and a take's file is closed before it is moved. A failure
/// is logged without its path (a path names the user's home folder) and
/// marks the take; dictation goes on without it.
///
/// Never deleted for a failure to touch it. A file that cannot be opened
/// just now is not a damaged one: it is neither read as nothing nor deleted.
/// A take whose move to `kept.pcm` fails stays the kept take where it is.
public final class DictationTakeSpool: @unchecked Sendable {
    /// The one kept take's file name.
    public static let keptFileName = "kept.pcm"
    /// A take's file name before its id.
    public static let takePrefix = "take-"
    /// Every spool file's extension.
    public static let fileExtension = "pcm"
    /// The header's length in bytes.
    public static let headerBytes = 24
    /// The header's format number.
    public static let format: Int32 = 1
    /// The one rate the spool writes and the capture delivers.
    public static let sampleRate = 16_000

    private static let magic = Data("DICTTAKE".utf8)

    /// The default file-work runner: one serial queue per process, so a slow
    /// disk never stalls the audio tap and a take's file work stays in
    /// order.
    private static let fileQueue = DispatchQueue(label: "ai.scam.dictation.take-spool")

    /// The product's runner; tests pass `{ work in work() }` so a file is on
    /// disk when the call returns.
    public static let runSerially: @Sendable (@escaping @Sendable () -> Void) -> Void = { work in
        fileQueue.async(execute: work)
    }

    private let directory: URL
    private let run: @Sendable (@escaping @Sendable () -> Void) -> Void
    private let log: @Sendable (String) -> Void
    /// Where the kept take is: `keptPath`, or a take's own file when moving
    /// it there failed. File-work runner only, and `atLaunch` before any
    /// take.
    private var keptFile: URL?
    private var pendingDiscards: [URL: DictationTake] = [:]

    public init(
        directory: URL,
        runFileWork: @escaping @Sendable (@escaping @Sendable () -> Void) -> Void = DictationTakeSpool.runSerially,
        log: @escaping @Sendable (String) -> Void = { _ in }
    ) {
        self.directory = directory
        self.run = runFileWork
        self.log = log
    }

    /// `~/Library/Application Support/Dictation/dictation/recovery` — beside
    /// `history.json` and `outcomes.json`, the same user's dictation data.
    public static func defaultDirectory() -> URL {
        let fm = FileManager.default
        let base = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? fm.homeDirectoryForCurrentUser
                .appendingPathComponent("Library/Application Support", isDirectory: true)
        return base
            .appendingPathComponent("Dictation", isDirectory: true)
            .appendingPathComponent("dictation", isDirectory: true)
            .appendingPathComponent("recovery", isDirectory: true)
    }

    /// The kept take's path.
    public var keptPath: URL { directory.appendingPathComponent(Self.keptFileName) }

    // ─── One take ───

    /// A take starts: its file is created and its header written, so its
    /// first buffer is spooled too.
    public func begin(startedAt: Date) -> DictationTake {
        let take = DictationTake(
            path: directory.appendingPathComponent(
                "\(Self.takePrefix)\(UUID().uuidString).\(Self.fileExtension)"),
            startedAt: startedAt)
        let directory = self.directory
        queue("open a take", take) {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true,
                                                    attributes: [.posixPermissions: 0o700])
            guard FileManager.default.createFile(atPath: take.path.path, contents: nil,
                                                  attributes: [.posixPermissions: 0o600]) else {
                throw CocoaError(.fileWriteUnknown)
            }
            let handle = try FileHandle(forWritingTo: take.path)
            take.handle = handle
            // FileHandle writes go straight to the kernel: every buffer is
            // on disk as far as a process crash is concerned.
            try handle.write(contentsOf: Self.header(startedAt: startedAt))
        }
        return take
    }

    /// A buffer of the take arrived, on the capture's collector task:
    /// converts there (bounded, no I/O), writes on the file-work runner.
    public func append(_ take: DictationTake, samples: [Float], sampleRate: Int) {
        guard !samples.isEmpty, !take.failed else { return }
        guard sampleRate == Self.sampleRate else {
            // The capture delivers one rate; a chunk at another is dropped,
            // never written at the wrong speed.
            take.noteDropped()
            take.markFailed()
            return
        }
        var data = Data(capacity: samples.count * 2)
        for sample in samples {
            var pcm = Self.quantise(sample).littleEndian
            withUnsafeBytes(of: &pcm) { data.append(contentsOf: $0) }
        }
        let bytes = data
        take.noteAppended(samples.count)
        queue("write a buffer", take) {
            if let handle = take.handle, !take.failed {
                try handle.write(contentsOf: bytes)
            }
        }
    }

    /// The take's words went in, or it was cancelled: its file goes.
    @discardableResult
    public func discard(_ take: DictationTake) async -> Bool {
        await queued("delete a take", take) { self.discardOnFileQueue(take) } ?? false
    }

    /// Retry failed cleanup without ever offering a cancelled take as a
    /// recovery. Erase through the still-open file before unlinking: a
    /// read-only directory can refuse unlink while the file remains writable.
    public func retryDiscards() async -> Bool {
        await queued("retry recording cleanup") {
            for take in Array(self.pendingDiscards.values) {
                _ = self.discardOnFileQueue(take)
            }
            return self.pendingDiscards.isEmpty
        } ?? false
    }

    private func discardOnFileQueue(_ take: DictationTake) -> Bool {
        take.markFailed() // Late collector work cannot refill erased audio.
        defer { Self.close(take) }
        if FileManager.default.fileExists(atPath: take.path.path) {
            do {
                let handle = try take.handle ?? FileHandle(forWritingTo: take.path)
                take.handle = handle
                try handle.truncate(atOffset: 0)
                try handle.synchronize()
            } catch {
                logLine("a cancelled recording could not be erased (\(Self.describe(error))); deletion is still attempted")
            }
        }
        do {
            try Self.removeIfPresent(take.path)
            pendingDiscards.removeValue(forKey: take.path)
            return true
        } catch {
            pendingDiscards[take.path] = take
            logLine("recording cleanup is pending (\(Self.describe(error)))")
            return false
        }
    }

    /// The take ends and stays where it is: Dictation is closing under it,
    /// and the next launch offers it (`atLaunch`).
    public func leave(_ take: DictationTake) async {
        await queued("close a take", take) { Self.close(take) }
    }

    /// The take could not be transcribed: it becomes the kept take,
    /// replacing any older one. A move that fails leaves it the kept one
    /// where it is, still recoverable, rather than reporting it lost while
    /// its file stays on disk.
    ///
    /// - Returns: Whether it is kept: false when the take had already failed
    ///   half-written or held no audio, and its file is deleted.
    public func keep(_ take: DictationTake) async -> Bool {
        await queued("keep a take", take) { () throws -> Bool in
            Self.close(take)
            guard !take.failed, take.samples > 0 else {
                try Self.removeIfPresent(take.path)
                return false
            }
            let earlier = self.keptFile
            var destinationUnavailable = false
            if FileManager.default.fileExists(atPath: self.keptPath.path) {
                _ = Self.readHeader(self.keptPath, unavailable: &destinationUnavailable)
            }
            do {
                guard !destinationUnavailable else { throw CocoaError(.fileReadNoPermission) }
                try Self.replaceAtomically(take.path, at: self.keptPath)
                self.keptFile = self.keptPath
            } catch {
                guard FileManager.default.fileExists(atPath: take.path.path) else {
                    // A take whose own file is gone is not kept anywhere.
                    throw error
                }
                // kept.pcm is held; the take is kept where it was recorded.
                self.logLine("the take could not be moved into place (\(Self.describe(error))); it is kept where it was recorded")
                self.keptFile = take.path
            }
            if let earlier, earlier != self.keptFile, !(earlier == self.keptPath && destinationUnavailable) {
                // One kept at a time: the earlier one, wherever it was.
                self.tryDelete(earlier)
            }
            return true
        } ?? false
    }

    // ─── The kept take ───

    /// At launch, on the caller's thread: what is left from before. A take a
    /// crash or a quit left behind becomes the kept take if it is the
    /// newest; every other file is deleted, and so is a kept take older than
    /// `keptFor`, an empty one and one that cannot be read. A file that
    /// cannot be opened just now is left alone, neither offered nor deleted,
    /// for a later launch.
    public func atLaunch(now: Date, keptFor: TimeInterval) -> DictationKeptTake? {
        struct Found {
            var path: URL
            var header: (startedAt: Date, samples: Int)?
            var unavailable: Bool
        }
        // No folder, no offer: a first launch is not an error.
        guard FileManager.default.fileExists(atPath: directory.path) else { return nil }
        let names: [String]
        do {
            names = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        } catch {
            logLine("the recovery folder could not be read at launch (\(Self.describe(error)))")
            return nil
        }
        var found: [Found] = []
        for name in names where name == Self.keptFileName
            || (name.hasPrefix(Self.takePrefix) && name.hasSuffix(".\(Self.fileExtension)")) {
            let path = directory.appendingPathComponent(name)
            var unavailable = false
            let header = Self.readHeader(path, unavailable: &unavailable)
            found.append(Found(path: path, header: header, unavailable: unavailable))
        }
        func offerable(_ file: Found) -> Bool {
            guard let header = file.header, header.samples > 0 else { return false }
            return now.timeIntervalSince(header.startedAt) < keptFor
        }
        let chosen = found.filter(offerable).max { $0.header!.startedAt < $1.header!.startedAt }
        // Another take that was still offerable goes with the rest: the
        // offer says the one kept replaced it.
        let replaced = found.contains { $0.path != chosen?.path && offerable($0) }
        for file in found where file.path != chosen?.path && !file.unavailable {
            tryDelete(file.path)
        }
        guard let chosen, let header = chosen.header else { return nil }
        let interrupted = chosen.path.lastPathComponent != Self.keptFileName
        keptFile = chosen.path
        if interrupted && !found.contains(where: { $0.path == keptPath && $0.unavailable }) {
            do {
                try Self.replaceAtomically(chosen.path, at: keptPath)
                keptFile = keptPath
            } catch {
                // Offered where it is.
                logLine("a take left at launch could not be moved into place (\(Self.describe(error)))")
            }
        }
        if replaced {
            logLine("at launch, an earlier saved recording was deleted: one is kept at a time, the newest")
        }
        return DictationKeptTake(
            recordedAt: header.startedAt,
            length: TimeInterval(header.samples) / TimeInterval(Self.sampleRate),
            interrupted: interrupted,
            replacedEarlier: replaced)
    }

    /// The kept take's audio, read on the file-work runner. A file that
    /// could not be opened or read just now says so
    /// (`DictationKeptAudio.unavailable`) rather than read as nothing, so
    /// the caller keeps it.
    func read(_ take: DictationTake) async -> DictationKeptAudio {
        await queued("read the interrupted take") {
            Self.readAudio(take.path)
        } ?? DictationKeptAudio(samples: nil, unavailable: true)
    }

    /// The take's audio as written: 16 kHz mono little-endian PCM16, without
    /// its header; nil when there is none or it could not be read. Read on
    /// the file-work runner, after every buffer queued before it, so the
    /// history can keep the recording before the take's file is deleted. A
    /// read that fails costs only the history's copy: the take is not marked.
    func pcm(_ take: DictationTake) async -> Data? {
        let read = await queued("read a take for the history") { () throws -> Data? in
            let data = try Data(contentsOf: take.path)
            guard data.count > Self.headerBytes else { return nil }
            return data.subdata(in: Self.headerBytes..<data.count)
        }
        return read ?? nil
    }

    public func readKept() async -> DictationKeptAudio {
        await queued("read the kept take") { () throws -> DictationKeptAudio in
            Self.readAudio(self.keptFile ?? self.keptPath)
        } ?? DictationKeptAudio(samples: nil, unavailable: true)
    }

    /// The kept take was recovered or discarded: it goes.
    @discardableResult
    public func discardKept() async -> Bool {
        await queued("delete the kept take") {
            let path = self.keptFile ?? self.keptPath
            try Self.removeIfPresent(path)
            self.keptFile = nil
            return true
        } ?? false
    }

    // ─── The format ───

    /// The header for a take started at `startedAt`.
    public static func header(startedAt: Date) -> Data {
        var header = Data(count: headerBytes)
        header.replaceSubrange(0..<8, with: magic)
        var format = format.littleEndian
        var rate = Int32(sampleRate).littleEndian
        var started = Int64((startedAt.timeIntervalSince1970 * 1_000_000).rounded()).littleEndian
        withUnsafeBytes(of: &format) { header.replaceSubrange(8..<12, with: $0) }
        withUnsafeBytes(of: &rate) { header.replaceSubrange(12..<16, with: $0) }
        withUnsafeBytes(of: &started) { header.replaceSubrange(16..<24, with: $0) }
        return header
    }

    /// A spool file's start and sample count; nil when it is not one this
    /// build wrote, or cannot be opened.
    public static func readHeader(_ url: URL) -> (startedAt: Date, samples: Int)? {
        var unavailable = false
        return readHeader(url, unavailable: &unavailable)
    }

    /// `readHeader(_:)`, telling a file that could not be opened
    /// (`unavailable`) from one that is not a take or is gone.
    static func readHeader(_ url: URL, unavailable: inout Bool) -> (startedAt: Date, samples: Int)? {
        unavailable = false
        do {
            let handle = try FileHandle(forReadingFrom: url)
            defer { try? handle.close() }
            guard let header = try handle.read(upToCount: headerBytes), header.count == headerBytes,
                  header.prefix(8).elementsEqual(magic),
                  header.subdata(in: 8..<12).withUnsafeBytes({ $0.loadUnaligned(as: Int32.self) }).littleEndian == format,
                  header.subdata(in: 12..<16).withUnsafeBytes({ $0.loadUnaligned(as: Int32.self) }).littleEndian == Int32(sampleRate)
            else { return nil }
            let ticks = header.subdata(in: 16..<24).withUnsafeBytes { $0.loadUnaligned(as: Int64.self) }.littleEndian
            guard ticks > 0 else { return nil }
            let size = try FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int ?? headerBytes
            return (Date(timeIntervalSince1970: TimeInterval(ticks) / 1_000_000), max(0, size - headerBytes) / 2)
        } catch let error as NSError where error.domain == NSCocoaErrorDomain
            && (error.code == NSFileReadNoSuchFileError || error.code == NSFileNoSuchFileError) {
            return nil
        } catch {
            unavailable = true
            return nil
        }
    }

    /// A spool file's audio, as the transcriber takes it; nil when it is not
    /// one this build wrote, holds nothing, or cannot be read. A file that
    /// could not be opened or read just now reads as
    /// `DictationKeptAudio.unavailable`, never as nothing.
    static func readAudio(_ url: URL) -> DictationKeptAudio {
        var unavailable = false
        guard let header = readHeader(url, unavailable: &unavailable), header.samples > 0 else {
            return DictationKeptAudio(samples: nil, unavailable: unavailable)
        }
        do {
            let data = try Data(contentsOf: url)
            guard data.count >= headerBytes else {
                return DictationKeptAudio(samples: nil, unavailable: false)
            }
            let count = min(header.samples, (data.count - headerBytes) / 2)
            var samples = [Float]()
            samples.reserveCapacity(count)
            data.withUnsafeBytes { raw in
                for index in 0..<count {
                    let pcm = raw.loadUnaligned(fromByteOffset: headerBytes + index * 2, as: Int16.self).littleEndian
                    samples.append(Float(pcm) / 32768)
                }
            }
            return DictationKeptAudio(samples: samples, unavailable: false)
        } catch let error as NSError where error.domain == NSCocoaErrorDomain
            && (error.code == NSFileReadNoSuchFileError || error.code == NSFileNoSuchFileError) {
            return DictationKeptAudio(samples: nil, unavailable: false)
        } catch {
            return DictationKeptAudio(samples: nil, unavailable: true)
        }
    }

    /// One sample, float to little-endian PCM16: rounded from
    /// `sample * 32767` and clamped, the same arithmetic the Windows spool
    /// writes.
    public static func quantise(_ sample: Float) -> Int16 {
        guard sample.isFinite else { return 0 }
        let scaled = (sample * 32767).rounded()
        return Int16(min(32767, max(-32768, scaled)))
    }

    // ─── The file-work runner ───

    private static func close(_ take: DictationTake) {
        let handle = take.handle
        take.handle = nil
        try? handle?.close()
    }

    private static func replaceAtomically(_ source: URL, at destination: URL) throws {
        // Both paths are in the recovery directory. POSIX rename replaces
        // atomically; a failed rename leaves the previous recording intact.
        guard rename(source.path, destination.path) == 0 else {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
    }

    private static func removeIfPresent(_ url: URL) throws {
        if FileManager.default.fileExists(atPath: url.path) {
            try FileManager.default.removeItem(at: url)
        }
    }

    /// Delete one file; a failure is logged without its path and costs only
    /// that file.
    private func tryDelete(_ url: URL) {
        do {
            try Self.removeIfPresent(url)
        } catch {
            logLine("a file in the recovery folder could not be deleted (\(Self.describe(error)))")
        }
    }

    /// One item of file work: in order, and never throwing into the caller.
    private func queue(_ what: String, _ take: DictationTake? = nil, _ work: @escaping @Sendable () throws -> Void) {
        run {
            do { try work() } catch { self.failed(what, take, error) }
        }
    }

    /// File work the caller awaits.
    private func queued<T: Sendable>(
        _ what: String, _ take: DictationTake? = nil, _ work: @escaping @Sendable () throws -> T
    ) async -> T? {
        await withCheckedContinuation { continuation in
            queue(what, take) {
                do {
                    continuation.resume(returning: try work())
                } catch {
                    self.failed(what, take, error)
                    continuation.resume(returning: nil)
                }
            }
        }
    }

    /// A failure, once per take. The take's file is closed by the take's own
    /// end (`discard`, `keep`, `leave`), on the file-work runner, never
    /// here.
    private func failed(_ what: String, _ take: DictationTake?, _ error: Error) {
        let first = take.map { !$0.failed } ?? false
        // A take that failed half-written is never offered as whole.
        take?.markFailed()
        if take == nil || first {
            logLine("the take spool could not \(what) (\(Self.describe(error)))")
        }
    }

    /// Domain and code only: an I/O error's full text carries the path, and
    /// the path the user's name.
    private static func describe(_ error: Error) -> String {
        let nsError = error as NSError
        return "\(nsError.domain) \(nsError.code)"
    }

    private func logLine(_ line: String) {
        log(line)
    }
}
