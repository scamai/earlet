// How the ASR transport notices a dead worker and hands out a kill switch.
//
// A scripted wire client stands in for the Python process, with a process
// flag the test can flip, so no interpreter or model is needed.

#if canImport(Testing)
import Foundation
import Testing
import DictationIPC
@testable import DictationKit

private final class ScriptedWorkerWire: AsrWireClient, WorkerProcessControl, @unchecked Sendable {
    private let lock = NSLock()
    private var running = false
    private var starts = 0
    private var kills = 0

    var startCount: Int { lock.lock(); defer { lock.unlock() }; return starts }
    var killCount: Int { lock.lock(); defer { lock.unlock() }; return kills }

    /// The worker dies on its own, between requests.
    func die() { lock.lock(); running = false; lock.unlock() }

    var isProcessRunning: Bool { lock.lock(); defer { lock.unlock() }; return running }

    func forceTerminate() { lock.lock(); kills += 1; running = false; lock.unlock() }

    func start() async throws { markStarted() }

    private func markStarted() { lock.lock(); starts += 1; running = true; lock.unlock() }

    func sendFlat(op: String, fields: [String: JSONValue]) async throws -> IpcResponse {
        let reply = #"{"ok": true, "engine": "stub", "text": "hello"}"#
        return try JSONDecoder().decode(IpcResponse.self, from: Data(reply.utf8))
    }

    func stop() async { die() }
}

@Suite("The ASR transport recovers from a dead or wedged worker")
struct PythonAsrTransportRecoveryTests {

    private static func transport(_ wire: ScriptedWorkerWire) -> PythonAsrTransport {
        PythonAsrTransport(pythonPath: "/nonexistent", workingDirectory: "/nonexistent",
                           hfId: "test-model", wireClient: wire)
    }

    @Test("start() spawns a new worker when the old one died while idle")
    func startRespawnsAfterAnIdleDeath() async {
        let wire = ScriptedWorkerWire()
        let asr = Self.transport(wire)
        #expect(await asr.start())
        #expect(asr.isReady)

        wire.die()
        #expect(!asr.isReady, "a dead process is not ready, whatever the init flag says")
        #expect(await asr.start())
        #expect(wire.startCount == 2, "the second start must spawn, not report the dead worker as started")
    }

    @Test("start() stays a no-op while the worker is alive")
    func startIsIdempotentWhileAlive() async {
        let wire = ScriptedWorkerWire()
        let asr = Self.transport(wire)
        #expect(await asr.start())
        #expect(await asr.start())
        #expect(wire.startCount == 1)
    }

    @Test("forceTerminate kills through the process control and leaves the transport unready")
    func forceTerminateKillsAndUninitializes() async {
        let wire = ScriptedWorkerWire()
        let asr = Self.transport(wire)
        #expect(await asr.start())
        asr.forceTerminate()
        #expect(wire.killCount == 1)
        #expect(!asr.isReady)
        if case .failed(.notInitialized) = await asr.transcribe(samples: [0.5], sampleRate: 16_000) {
        } else {
            Issue.record("a killed transport must refuse to transcribe until started again")
        }
    }
}
#endif
