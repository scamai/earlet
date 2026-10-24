import Foundation

/// Configuration for launching the Python inference engine subprocess.
public struct EngineConfig: Sendable {
    /// Absolute path to the Python interpreter (the portable runtime's
    /// `runtime/bin/python3.11`).
    public var pythonPath: String
    /// Directory containing the worker package passed in `arguments`.
    public var workingDirectory: String
    /// Explicit worker module or script arguments.
    public var arguments: [String]
    /// Extra environment overrides merged onto the inherited environment.
    public var environment: [String: String]

    public init(
        pythonPath: String,
        workingDirectory: String,
        arguments: [String],
        environment: [String: String] = [:]
    ) {
        self.pythonPath = pythonPath
        self.workingDirectory = workingDirectory
        self.arguments = arguments
        self.environment = environment
    }
}

/// Control over a worker PROCESS that works while its IPC actor is blocked.
///
/// Every request holds `PythonEngineClient`'s actor while it waits in a
/// synchronous pipe read, so a worker that stays alive without answering (a
/// stuck decode, a model load that never finishes) also blocks `stop()` and
/// `shutdown()`, which queue behind that read. Killing the child is the one
/// thing that unblocks it: the pipe reaches EOF and the request throws. So
/// the kill switch has to be reachable without entering the actor.
public protocol WorkerProcessControl: Sendable {
    /// Whether the child process is alive right now. `false` before the
    /// first start and after it exits, whichever way it exited.
    var isProcessRunning: Bool { get }
    /// SIGKILL the child if it is running. Safe from any thread, idempotent,
    /// and never waits on the IPC actor.
    func forceTerminate()
}

/// The launched child, readable and killable from any thread.
final class ChildProcessHandle: @unchecked Sendable {
    private let lock = NSLock()
    private var process: Process?

    func set(_ process: Process?) {
        lock.lock(); self.process = process; lock.unlock()
    }

    var isRunning: Bool {
        lock.lock(); defer { lock.unlock() }
        return process?.isRunning ?? false
    }

    /// SIGKILL rather than `Process.terminate()`'s SIGTERM: the case this
    /// exists for is a worker that is not responding, and it must not be
    /// given the chance to ignore the request.
    func sigkill() {
        lock.lock(); let current = process; lock.unlock()
        guard let current, current.isRunning else { return }
        _ = kill(current.processIdentifier, SIGKILL)
    }
}

/// Drives the Python inference engine over a length-prefixed stdio JSON
/// protocol. Isolated as an actor so concurrent callers are serialized — the
/// engine processes one request at a time and replies in FIFO order, so we
/// send a request and then read exactly one framed response.
public actor PythonEngineClient: WorkerProcessControl {

    private let config: EngineConfig
    private var process: Process?
    private var stdinPipe: Pipe?
    private var stdoutHandle: FileHandle?
    private var stderrDrainTask: Task<Void, Never>?
    /// The same child as `process`, reachable without the actor. See
    /// `WorkerProcessControl` for why both exist.
    private nonisolated let child = ChildProcessHandle()

    /// Optional sink for engine stderr (logging) lines.
    private let stderrSink: (@Sendable (String) -> Void)?

    public init(
        config: EngineConfig,
        stderrSink: (@Sendable (String) -> Void)? = nil
    ) {
        self.config = config
        self.stderrSink = stderrSink
    }

    public var isRunning: Bool {
        process?.isRunning ?? false
    }

    public nonisolated var isProcessRunning: Bool { child.isRunning }

    public nonisolated func forceTerminate() { child.sigkill() }

    /// Builds the environment for every bundled Python child process.
    ///
    /// No inherited `PYTHON*` variable reaches the child. The interpreter is
    /// ours, so the parent's `PYTHONPATH`, `PYTHONHOME` or `PYTHONSTARTUP`
    /// (set by `launchctl setenv`, or carried in from a terminal) can only
    /// replace bundled packages or run code inside a process that holds this
    /// app's privacy grants. `PYTHONNOUSERSITE` keeps the user's
    /// site-packages out for the same reason. The ASR worker's `-E -s` flags
    /// say the same to the interpreter; this keeps it true for anything the
    /// worker spawns, and for a launch that forgets the flags. It is the
    /// rule `worker_environment` in `tools/package.py` applies to the
    /// packaged import smoke.
    ///
    /// `PYTHONDONTWRITEBYTECODE` is an integrity boundary, not a caller
    /// preference: writing `__pycache__` below `Contents/Resources` mutates a
    /// sealed app after signing and makes macOS TCC grants unreliable. Apply
    /// caller overrides second, then force the process invariants so an
    /// injected `PYTHONDONTWRITEBYTECODE=0` cannot reopen that failure mode.
    static func subprocessEnvironment(
        inherited: [String: String] = ProcessInfo.processInfo.environment,
        overrides: [String: String]
    ) -> [String: String] {
        var environment = inherited.filter { !$0.key.hasPrefix("PYTHON") }
        for (key, value) in overrides { environment[key] = value }
        environment["PYTHONUNBUFFERED"] = "1"
        environment["PYTHONDONTWRITEBYTECODE"] = "1"
        environment["PYTHONNOUSERSITE"] = "1"
        return environment
    }

    // MARK: - Lifecycle

    /// Launch the subprocess. Throws if a GENUINELY running process already
    /// exists. Tolerates a STALE handle from a worker that died on its own
    /// (crash, OOM-kill, an operator `kill -9`) — `process` is only cleared
    /// by an explicit `stop()`, so before this fix a caller whose worker had
    /// died out from under it (e.g. `DictationKit.PythonAsrTransport`,
    /// which owns exactly one long-lived `PythonEngineClient` for its whole
    /// process lifetime, unlike a client that rebuilds a worker for each request,
    /// fresh client per `start()`) needed to call `start()` TWICE to
    /// recover: the first call threw `.alreadyStarted` against the dead
    /// handle (its `catch` block does call `stop()` as a side effect,
    /// clearing `process`, but still reports failure to the caller), and
    /// only the second call actually respawned. No caller in this codebase
    /// retries `start()` a second time, so worker-death recovery was
    /// silently broken end to end. Found + verified with a real soak run
    /// (`--dictation-soak`) that `kill -9`'d the live worker mid-run and confirmed a
    /// single subsequent `start()` call failed before this fix, succeeded
    /// after it.
    public func start() throws {
        if let existing = process {
            guard !existing.isRunning else { throw IpcClientError.alreadyStarted }
            // Stale handle — clean it up (closes pipes, drops the drain
            // task) before spawning a replacement, all within this one call.
            stop()
        }

        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: config.pythonPath)
        proc.arguments = config.arguments
        proc.currentDirectoryURL = URL(fileURLWithPath: config.workingDirectory)

        proc.environment = Self.subprocessEnvironment(overrides: config.environment)

        let inPipe = Pipe()
        let outPipe = Pipe()
        let errPipe = Pipe()
        proc.standardInput = inPipe
        proc.standardOutput = outPipe
        proc.standardError = errPipe

        try proc.run()

        #if canImport(Darwin)
        // A worker that dies between `writeAndRead`'s `isRunning` check and
        // the write would otherwise deliver SIGPIPE, whose default action
        // kills this whole app. With the flag the write fails with EPIPE,
        // which callers already classify as a dead worker.
        _ = fcntl(inPipe.fileHandleForWriting.fileDescriptor, F_SETNOSIGPIPE, 1)
        #endif

        child.set(proc)
        self.process = proc
        self.stdinPipe = inPipe
        self.stdoutHandle = outPipe.fileHandleForReading

        // Drain stderr off the request/response path so it never blocks.
        let errHandle = errPipe.fileHandleForReading
        let sink = stderrSink
        stderrDrainTask = Task.detached {
            while true {
                let chunk = errHandle.availableData
                if chunk.isEmpty { break }
                if let sink, let text = String(data: chunk, encoding: .utf8) {
                    sink(text)
                }
            }
        }
    }

    /// Terminate the subprocess and clean up. Idempotent.
    public func stop() {
        stderrDrainTask?.cancel()
        stderrDrainTask = nil
        if let proc = process, proc.isRunning {
            proc.terminate()
        }
        try? stdinPipe?.fileHandleForWriting.close()
        try? stdoutHandle?.close()
        child.set(nil)
        process = nil
        stdinPipe = nil
        stdoutHandle = nil
    }

    // MARK: - High-level ops

    /// Health check. Returns `engine_loaded` from the reply.
    @discardableResult
    public func ping() async throws -> Bool {
        let resp = try await send(op: "ping", body: .object([:]))
        try checkError(resp)
        return resp["engine_loaded"]?.boolValue ?? false
    }

    /// Ask the engine to shut down cleanly, then tear down the process.
    @discardableResult
    public func shutdown() async throws -> IpcResponse {
        let resp = try await send(op: "shutdown", body: .object([:]))
        stop()
        return resp
    }

    // MARK: - Transport

    /// Send a request frame and synchronously read the single matching response
    /// frame. Serialized by actor isolation.
    /// Send a FLAT frame: `{"op": ..., <fields merged at top level>}`.
    ///
    /// Two wire shapes coexist in this repo. The detection engine
    /// (`src/inference/ipc_server.py`) reads a nested `body` object, which
    /// `send(op:body:)` produces. The meeting-notes ASR worker
    /// (`transcription_worker/asr_server.py`, shared verbatim with the
    /// Windows branch) reads its fields at the TOP level — `req["pcm_b64"]`.
    /// Sending the nested shape to it made `init` look healthy (every field
    /// there is optional with a default) while EVERY transcribe failed with
    /// `KeyError: 'pcm_b64'` — the 2026-08-05 "notes produce nothing" bug.
    /// Keep the two shapes explicit rather than guessing per-op.
    public func sendFlat(op: String, fields: [String: JSONValue]) async throws -> IpcResponse {
        var merged = fields
        merged["op"] = .string(op)
        return try await sendRaw(.object(merged))
    }

    public func send(op: String, body: JSONValue) async throws -> IpcResponse {
        guard let stdin = stdinPipe?.fileHandleForWriting,
              let stdout = stdoutHandle,
              let proc = process, proc.isRunning
        else {
            throw IpcClientError.notStarted
        }

        let request = IpcRequest(op: op, body: body)
        guard let payload = try? JSONEncoder().encode(request) else {
            throw IpcClientError.encodingFailed
        }
        return try await writeAndRead(payload)
    }

    /// Encode an arbitrary JSON value as the whole request frame.
    public func sendRaw(_ value: JSONValue) async throws -> IpcResponse {
        guard let payload = try? JSONEncoder().encode(value) else {
            throw IpcClientError.encodingFailed
        }
        return try await writeAndRead(payload)
    }

    private func writeAndRead(_ payload: Data) async throws -> IpcResponse {
        guard let stdin = stdinPipe?.fileHandleForWriting,
              let stdout = stdoutHandle,
              let proc = process, proc.isRunning
        else {
            throw IpcClientError.notStarted
        }
        // Iteration 41 (verification lane): stage-by-stage transport tracing,
        // gated behind DICTATION_IPC_TRACE=1 so it costs nothing (a few cached
        // ProcessInfo lookups + DispatchTime.now() calls, no allocation) on
        // every other run. Added while reconciling the Iteration 17/20/23/39
        // Python-vs-Swift ASR latency discrepancy; Iteration 41 used it to
        // find the cause.
        let tracing = Self.tracingEnabled
        let tEnter = tracing ? DispatchTime.now() : nil

        let frame = IpcFraming.encode(payload)
        let tFramed = tracing ? DispatchTime.now() : nil
        try stdin.write(contentsOf: frame)
        let tWritten = tracing ? DispatchTime.now() : nil

        // Read exactly one framed response.
        let header = try readExactly(4, from: stdout)
        let tHeader = tracing ? DispatchTime.now() : nil
        let length: Int
        do {
            length = try IpcFraming.decodeResponseLength(header)
        } catch {
            // Out of step with the worker: nothing it writes from here on can
            // be read as a reply. Kill it, so the caller's I/O failure takes
            // the restart path and the next start spawns a fresh worker
            // instead of finding this one still running.
            child.sigkill()
            stop()
            throw error
        }
        let responsePayload = length == 0 ? Data() : try readExactly(length, from: stdout)
        let tPayload = tracing ? DispatchTime.now() : nil

        let decoder = JSONDecoder()
        do {
            let resp = try decoder.decode(IpcResponse.self, from: responsePayload)
            if tracing, let tEnter, let tFramed, let tWritten, let tHeader, let tPayload {
                let tDecoded = DispatchTime.now()
                let traceFields = [
                    "IPC_TRACE frame_encode_ms=\(Self.ms(tEnter, tFramed))",
                    "write_ms=\(Self.ms(tFramed, tWritten))",
                    "await_response_ms=\(Self.ms(tWritten, tHeader))",
                    "read_payload_ms=\(Self.ms(tHeader, tPayload))",
                    "json_decode_ms=\(Self.ms(tPayload, tDecoded))",
                    "total_ms=\(Self.ms(tEnter, tDecoded))",
                    "payload_bytes=\(payload.count)",
                    "response_bytes=\(responsePayload.count)",
                ]
                let traceLine = traceFields.joined(separator: " ") + "\n"
                FileHandle.standardError.write(Data(traceLine.utf8))
            }
            return resp
        } catch {
            throw IpcClientError.decodingFailed(underlying: String(describing: error))
        }
    }

    /// Cached once per process — `ProcessInfo.environment` is not free to
    /// re-read on every call, and this is on the hot IPC path even when
    /// tracing is off.
    private static let tracingEnabled: Bool = {
        ProcessInfo.processInfo.environment["DICTATION_IPC_TRACE"] == "1"
    }()

    private static func ms(_ a: DispatchTime, _ b: DispatchTime) -> String {
        String(format: "%.3f", Double(b.uptimeNanoseconds - a.uptimeNanoseconds) / 1_000_000)
    }

    /// Read exactly `count` bytes from the handle, handling partial reads.
    /// Throws `eof` if the stream closes before `count` bytes arrive.
    private func readExactly(_ count: Int, from handle: FileHandle) throws -> Data {
        var buffer = Data()
        buffer.reserveCapacity(count)
        while buffer.count < count {
            let remaining = count - buffer.count
            let chunk = handle.readData(ofLength: remaining)
            if chunk.isEmpty {
                throw IpcClientError.eof
            }
            buffer.append(chunk)
        }
        return buffer
    }

    private func checkError(_ resp: IpcResponse) throws {
        if let err = resp.error {
            throw IpcClientError.engineError(err)
        }
        if !resp.ok {
            throw IpcClientError.engineError("engine returned ok=false")
        }
    }
}

/// Flat ASR wire seam used by the dictation transport. Scripted clients can
/// exercise retries and cancellation without spawning a worker process.
///
/// `PythonEngineClient`'s own `start()`/`stop()` are not declared `async`
/// (see their doc comments), but as ACTOR-isolated methods they already
/// require `await` from any external caller, which is exactly what
/// satisfying an `async` protocol requirement means — no signature change
/// needed on the concrete type, only the conformance below.
public protocol AsrWireClient: Sendable {
    func start() async throws
    func sendFlat(op: String, fields: [String: JSONValue]) async throws -> IpcResponse
    func stop() async
}

extension PythonEngineClient: AsrWireClient {}
