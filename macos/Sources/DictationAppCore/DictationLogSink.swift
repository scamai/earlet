import Foundation

/// A size-capped, thread-safe file log at `~/Library/Logs/Dictation/dictation.log`.
///
/// Every diagnostic in the copied dictation code goes to stderr: the paste
/// gate's rejections, microphone bind failures, the worker's own traceback
/// lines. Launched from Finder or the Dock, stderr is connected to nothing,
/// so a failed model load or a refused paste left no trace on the user's
/// machine. This gives those lines somewhere to live.
///
/// The directory and file names belong to this product.
///
/// - **Tee, never replace.** `start(teeStandardError: true)` splices a pipe
///   onto fd 2 and forwards every byte to BOTH the original stderr and the
///   file, so a developer running from a terminal still sees everything.
/// - **Bounded.** `maxBytesPerFile` per file, `dictation.log` plus two
///   rotated generations.
/// - **Private.** Files are created 0600. Transcripts are never logged here:
///   callers log classifications and worker diagnostics, not dictated text.
public final class DictationLogSink: @unchecked Sendable {

    public static let activeFileName = "dictation.log"
    public static let rotatedGenerations = 2

    public static func defaultDirectory() -> URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/Dictation", isDirectory: true)
    }

    public static let shared = DictationLogSink()

    public let maxBytesPerFile: Int

    // All guarded by `lock`.
    private let lock = NSLock()
    private var directory: URL?
    private var handle: FileHandle?
    private var currentBytes = 0
    private var savedStandardError: Int32 = -1
    private var teeReadFD: Int32 = -1
    private var teeSource: DispatchSourceRead?
    private var teeBuffer = Data()

    private let teeQueue = DispatchQueue(label: "ai.scam.dictation.logsink.tee")
    private let timestampFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd'T'HH:mm:ss.SSSZ"
        formatter.locale = Locale(identifier: "en_US_POSIX")
        return formatter
    }()

    public init(maxBytesPerFile: Int = 5 * 1024 * 1024) {
        self.maxBytesPerFile = maxBytesPerFile
    }

    // ─── Lifecycle ───

    /// Opens (or creates) the log and optionally installs the stderr tee.
    /// Idempotent. `false` only when the file could not be opened; the app
    /// carries on unlogged, since logging is never worth failing a launch.
    @discardableResult
    public func start(directory: URL = DictationLogSink.defaultDirectory(),
                      teeStandardError: Bool = true) -> Bool {
        lock.lock()
        if handle != nil { lock.unlock(); return true }
        let fileManager = FileManager.default
        guard (try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)) != nil
        else { lock.unlock(); return false }
        let fileURL = directory.appendingPathComponent(Self.activeFileName)
        if !fileManager.fileExists(atPath: fileURL.path) {
            fileManager.createFile(atPath: fileURL.path, contents: nil,
                                   attributes: [.posixPermissions: 0o600])
        }
        guard let opened = try? FileHandle(forWritingTo: fileURL) else { lock.unlock(); return false }
        currentBytes = (try? opened.seekToEnd()).map(Int.init) ?? 0
        self.directory = directory
        handle = opened
        lock.unlock()

        log("log opened", subsystem: "logsink")
        if teeStandardError { installStandardErrorTee() }
        return true
    }

    /// Closes the file and restores the original fd 2. Lets a test run a
    /// sink against a temporary directory without leaking the redirect.
    public func stop() {
        teeSource?.cancel()
        teeSource = nil
        lock.lock()
        let leftover = teeBuffer
        teeBuffer = Data()
        lock.unlock()
        if !leftover.isEmpty { log(String(decoding: leftover, as: UTF8.self), subsystem: "stderr") }

        lock.lock()
        if savedStandardError >= 0 {
            dup2(savedStandardError, STDERR_FILENO)
            close(savedStandardError)
            savedStandardError = -1
        }
        if teeReadFD >= 0 {
            close(teeReadFD)
            teeReadFD = -1
        }
        try? handle?.close()
        handle = nil
        currentBytes = 0
        directory = nil
        lock.unlock()
    }

    public var activeFileURL: URL? {
        lock.lock(); defer { lock.unlock() }
        return directory?.appendingPathComponent(Self.activeFileName)
    }

    // ─── Writing ───

    /// Appends one timestamped line per line of `message`, from any thread:
    /// `2026-09-28T14:03:11.418+0100 [subsystem] message`.
    public func log(_ message: String, subsystem: String = "app") {
        let body = message.hasSuffix("\n") ? String(message.dropLast()) : message
        lock.lock()
        let stamp = timestampFormatter.string(from: Date())
        lock.unlock()
        var out = ""
        for line in body.split(separator: "\n", omittingEmptySubsequences: false) {
            out += "\(stamp) [\(subsystem)] \(line)\n"
        }
        append(Data(out.utf8))
    }

    private func append(_ data: Data) {
        lock.lock(); defer { lock.unlock() }
        guard handle != nil else { return }
        if currentBytes + data.count > maxBytesPerFile { rotateLocked() }
        guard let live = handle else { return }
        // A failed log write must never propagate; the line is dropped.
        if (try? live.write(contentsOf: data)) != nil { currentBytes += data.count }
    }

    /// Caller holds `lock`. Only ever touches the names this type owns.
    private func rotateLocked() {
        guard let directory else { return }
        try? handle?.close()
        handle = nil
        let fileManager = FileManager.default
        func generation(_ n: Int) -> URL { directory.appendingPathComponent("dictation.\(n).log") }
        try? fileManager.removeItem(at: generation(Self.rotatedGenerations))
        var n = Self.rotatedGenerations - 1
        while n >= 1 {
            if fileManager.fileExists(atPath: generation(n).path) {
                try? fileManager.moveItem(at: generation(n), to: generation(n + 1))
            }
            n -= 1
        }
        let active = directory.appendingPathComponent(Self.activeFileName)
        try? fileManager.moveItem(at: active, to: generation(1))
        fileManager.createFile(atPath: active.path, contents: nil, attributes: [.posixPermissions: 0o600])
        handle = try? FileHandle(forWritingTo: active)
        currentBytes = 0
    }

    // ─── The stderr tee ───

    /// `dup(2)` first, to keep the real stderr, then `dup2` a pipe over fd 2.
    /// Everything the tee writes goes to the saved descriptor, never to fd 2,
    /// or it would feed itself.
    private func installStandardErrorTee() {
        lock.lock()
        guard savedStandardError < 0 else { lock.unlock(); return }
        var fds: [Int32] = [-1, -1]
        guard pipe(&fds) == 0 else { lock.unlock(); return }
        let readFD = fds[0], writeFD = fds[1]
        let saved = dup(STDERR_FILENO)
        guard saved >= 0, dup2(writeFD, STDERR_FILENO) >= 0 else {
            close(readFD); close(writeFD)
            if saved >= 0 { close(saved) }
            lock.unlock()
            return
        }
        close(writeFD)
        _ = fcntl(readFD, F_SETFL, fcntl(readFD, F_GETFL, 0) | O_NONBLOCK)
        savedStandardError = saved
        teeReadFD = readFD
        lock.unlock()

        let source = DispatchSource.makeReadSource(fileDescriptor: readFD, queue: teeQueue)
        source.setEventHandler { [weak self] in self?.drainTee(readFD) }
        source.resume()
        teeSource = source
    }

    private func drainTee(_ fd: Int32) {
        var scratch = [UInt8](repeating: 0, count: 16 * 1024)
        while true {
            let count = scratch.withUnsafeMutableBytes { read(fd, $0.baseAddress, $0.count) }
            if count <= 0 { break }
            let data = Data(scratch[0..<count])

            // Terminal first, byte for byte, partial lines included.
            lock.lock()
            let terminal = savedStandardError
            lock.unlock()
            if terminal >= 0 {
                data.withUnsafeBytes { raw in
                    var offset = 0
                    while offset < raw.count {
                        let written = write(terminal, raw.baseAddress!.advanced(by: offset), raw.count - offset)
                        if written <= 0 { break }
                        offset += written
                    }
                }
            }

            // Then whole lines to the file, each with its own timestamp.
            lock.lock()
            teeBuffer.append(data)
            var lines: [String] = []
            while let newline = teeBuffer.firstIndex(of: 0x0A) {
                lines.append(String(decoding: teeBuffer[teeBuffer.startIndex..<newline], as: UTF8.self))
                teeBuffer.removeSubrange(teeBuffer.startIndex...newline)
            }
            lock.unlock()
            for line in lines { log(line, subsystem: "stderr") }
        }
    }
}
