// A worker that stops answering must not be able to take the app with it.
//
// Every request holds `PythonEngineClient`'s actor while it blocks in a pipe
// read, so a worker that stays alive without replying also blocks `stop()` and
// `shutdown()`. These tests drive real child processes (`/bin/sh`, no Python):
// one that swallows requests and never replies, and one that closes its stdin
// while it keeps running.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationIPC

/// Resumes a continuation exactly once, whichever side gets there first.
private final class ResumeOnce<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<T?, Never>?
    init(_ continuation: CheckedContinuation<T?, Never>) { self.continuation = continuation }
    func resume(_ value: T?) {
        lock.lock(); let c = continuation; continuation = nil; lock.unlock()
        c?.resume(returning: value)
    }
}

/// Waits for `operation` at most `seconds`, without a task group — a group
/// would wait for a child blocked in a synchronous read, which is the very
/// failure under test. `nil` means the bound passed first.
private func within<T: Sendable>(
    _ seconds: Double, _ operation: @escaping @Sendable () async -> T
) async -> T? {
    await withCheckedContinuation { continuation in
        let once = ResumeOnce(continuation)
        Task.detached { once.resume(await operation()) }
        Task.detached {
            try? await Task.sleep(for: .seconds(seconds))
            once.resume(nil)
        }
    }
}

private func shellClient(_ script: String) -> PythonEngineClient {
    PythonEngineClient(config: EngineConfig(
        pythonPath: "/bin/sh", workingDirectory: NSTemporaryDirectory(),
        arguments: ["-c", script]))
}

@Suite("A worker that stops answering can be killed from outside its actor")
struct WorkerProcessControlTests {

    @Test("forceTerminate unblocks a request the actor is stuck reading", .timeLimit(.minutes(1)))
    func killUnblocksAWedgedRequest() async throws {
        // Reads every request and never writes a reply: the shape of a
        // decode that hangs inside the worker.
        let client = shellClient("cat > /dev/null")
        try await client.start()
        #expect(client.isProcessRunning)

        let request = Task.detached { () -> Bool in
            do {
                _ = try await client.sendFlat(op: "transcribe", fields: [:])
                return false
            } catch {
                return true
            }
        }
        // Long enough for the request to be parked in the read.
        try await Task.sleep(for: .milliseconds(300))
        client.forceTerminate()

        let threw = await within(10) { await request.value }
        #expect(threw == true, "the wedged request must fail once the worker is killed")
        #expect(await within(5) { await client.stop(); return true } == true,
                "stop() must be reachable again after the kill")
        #expect(!client.isProcessRunning)
    }

    @Test("a write to a worker that closed its stdin throws instead of raising SIGPIPE",
          .timeLimit(.minutes(1)))
    func writeToAClosedPipeThrows() async throws {
        // Alive, but nobody reads the request pipe any more: the moment a
        // write gets EPIPE — and, without F_SETNOSIGPIPE, SIGPIPE, whose
        // default action ends this whole test process.
        let client = shellClient("exec 0<&-; exec sleep 30")
        try await client.start()
        try await Task.sleep(for: .milliseconds(300))
        #expect(client.isProcessRunning, "the worker must still look alive to reach the write")

        var threw = false
        do {
            _ = try await client.sendFlat(op: "shutdown", fields: [:])
        } catch {
            threw = true
        }
        #expect(threw)
        client.forceTerminate()
        await client.stop()
    }

    @Test("forceTerminate is a no-op before start and after stop")
    func forceTerminateIsIdempotent() async throws {
        let client = shellClient("exec sleep 30")
        client.forceTerminate()
        #expect(!client.isProcessRunning)
        try await client.start()
        await client.stop()
        client.forceTerminate()
        #expect(!client.isProcessRunning)
    }
}
#endif
