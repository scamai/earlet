// Failures leave a trace: the log file under ~/Library/Logs/Dictation (here
// a temporary directory), and the worker's own stderr routed into it.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

private func scratchDirectory() -> URL {
    FileManager.default.temporaryDirectory.appendingPathComponent("dictation-log-\(UUID().uuidString)")
}

private func contents(_ sink: DictationLogSink) -> String {
    guard let url = sink.activeFileURL else { return "" }
    return (try? String(contentsOf: url, encoding: .utf8)) ?? ""
}

@Suite("The Dictation log")
struct DictationLogSinkTests {

    @Test("the default location is Dictation's own log directory")
    func defaultDirectory() {
        let path = DictationLogSink.defaultDirectory().path
        #expect(path.hasSuffix("Library/Logs/Dictation"))
        #expect(path == FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/Dictation", isDirectory: true).path)
    }

    @Test("lines are timestamped per line and the file is private")
    func linesAndPermissions() throws {
        let directory = scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let sink = DictationLogSink()
        #expect(sink.start(directory: directory, teeStandardError: false))
        sink.log("first\nsecond", subsystem: "test")
        let text = contents(sink)
        #expect(text.contains("[test] first\n"))
        #expect(text.contains("[test] second\n"))
        let attributes = try FileManager.default.attributesOfItem(atPath: sink.activeFileURL!.path)
        #expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600)
        sink.stop()
    }

    @Test("the log rotates at its cap and keeps two older generations")
    func rotation() throws {
        let directory = scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let sink = DictationLogSink(maxBytesPerFile: 200)
        #expect(sink.start(directory: directory, teeStandardError: false))
        for i in 0..<40 { sink.log("line \(i) with some padding to fill the file") }
        sink.stop()
        let names = try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted()
        #expect(names == ["dictation.1.log", "dictation.2.log", "dictation.log"])
        for name in names {
            let size = try FileManager.default.attributesOfItem(
                atPath: directory.appendingPathComponent(name).path)[.size] as? NSNumber
            #expect((size?.intValue ?? .max) <= 200, "\(name)")
        }
    }

    @Test("stderr written by the copied code lands in the file while the tee is on",
          .timeLimit(.minutes(1)))
    func standardErrorIsTeed() async {
        let directory = scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let sink = DictationLogSink()
        #expect(sink.start(directory: directory, teeStandardError: true))
        FileHandle.standardError.write(Data("[dictation] insert gate rejected: test\n".utf8))
        var seen = false
        for _ in 0..<100 where !seen {
            try? await Task.sleep(for: .milliseconds(20))
            seen = contents(sink).contains("[stderr] [dictation] insert gate rejected: test")
        }
        sink.stop()
        #expect(seen)
    }
}

@MainActor
@Suite("The live worker writes its stderr to the Dictation log")
struct LiveWorkerDiagnosticsTests {

    @Test("a worker that fails to import is explained in the log", .timeLimit(.minutes(1)))
    func workerStderrReachesTheLog() async throws {
        let directory = scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        // A stand-in interpreter that fails the way a package missing its
        // MLX wheel does, then exits.
        let fakePython = directory.appendingPathComponent("python3")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        FileManager.default.createFile(
            atPath: fakePython.path,
            contents: Data("#!/bin/sh\necho \"ModuleNotFoundError: No module named 'mlx_whisper'\" >&2\nexit 1\n".utf8),
            attributes: [.posixPermissions: 0o755])

        let sink = DictationLogSink()
        #expect(sink.start(directory: directory.appendingPathComponent("logs"), teeStandardError: false))
        let env = DictationEnvironment.live(sink: sink)
        let worker = env.makeWorker(DictationResources(
            python: fakePython.path, runtime: directory.path, model: directory.path))
        #expect(await worker.start() == false)

        var seen = false
        for _ in 0..<100 where !seen {
            try? await Task.sleep(for: .milliseconds(20))
            seen = contents(sink).contains("[asr] ModuleNotFoundError: No module named 'mlx_whisper'")
        }
        #expect(seen, "\(contents(sink))")
        env.log("model line")
        #expect(contents(sink).contains("[dictation] model line"))
        sink.stop()
    }

    /// A stand-in worker that speaks the framed protocol: one stderr line
    /// while it loads, an `init` reply, then, for a transcription, the line
    /// the real worker prints when it rewrites a user's alias.
    private func protocolWorker(in directory: URL) throws -> URL {
        let script = """
            #!/bin/sh
            read_frame() {
              set -- $(dd bs=1 count=4 2>/dev/null | od -An -tu1)
              [ $# -eq 4 ] || exit 0
              dd bs=1 count=$(( ($1 << 24) + ($2 << 16) + ($3 << 8) + $4 )) of=/dev/null 2>/dev/null
            }
            echo "loading the model" >&2
            read_frame
            sleep 0.3
            printf '\\000\\000\\000\\013{"ok":true}'
            read_frame
            echo "aliases applied [a spoken word]" >&2
            printf '\\000\\000\\000\\027{"ok":true,"text":"hi"}'
            exec cat > /dev/null
            """
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let fakePython = directory.appendingPathComponent("python3")
        FileManager.default.createFile(atPath: fakePython.path, contents: Data(script.utf8),
                                       attributes: [.posixPermissions: 0o755])
        return fakePython
    }

    private func occurrences(_ needle: String, in sink: DictationLogSink) -> Int {
        contents(sink).components(separatedBy: needle).count - 1
    }

    @Test("after the worker has started, what it prints about a dictation never reaches the log",
          .timeLimit(.minutes(1)))
    func workerStderrAfterStartupIsNotLogged() async throws {
        let directory = scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let fakePython = try protocolWorker(in: directory)

        let sink = DictationLogSink()
        #expect(sink.start(directory: directory.appendingPathComponent("logs"), teeStandardError: false))
        defer { sink.stop() }
        let env = DictationEnvironment.live(sink: sink)
        let worker = env.makeWorker(DictationResources(
            python: fakePython.path, runtime: directory.path, model: directory.path))
        defer { worker.forceTerminate() }
        let gate = try #require((worker as? LiveWorker)?.startupLog)

        #expect(await worker.start())
        #expect(contents(sink).contains("[asr] loading the model"), "a start stays diagnosable")
        #expect(contents(sink).contains("[asr] worker started; its later output is not logged"))

        #expect(await worker.transcribe(samples: [0.1, 0.2], sampleRate: 16_000) == .text("hi"))
        // The line reached the gate and was dropped there: a positive signal,
        // not a wait that could pass by being too short.
        var withheld = false
        for _ in 0..<250 where !withheld {
            try? await Task.sleep(for: .milliseconds(20))
            withheld = gate.withheldChunks >= 1
        }
        #expect(withheld, "the worker's line never reached the gate")
        #expect(!contents(sink).contains("a spoken word"), "\(contents(sink))")
        #expect(!contents(sink).contains("aliases applied"))
    }

    @Test("a worker started again after it died is logged again while it starts, and only then",
          .timeLimit(.minutes(1)))
    func restartedWorkerIsLoggedAgain() async throws {
        let directory = scratchDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let fakePython = try protocolWorker(in: directory)

        let sink = DictationLogSink()
        #expect(sink.start(directory: directory.appendingPathComponent("logs"), teeStandardError: false))
        defer { sink.stop() }
        let env = DictationEnvironment.live(sink: sink)
        let worker = env.makeWorker(DictationResources(
            python: fakePython.path, runtime: directory.path, model: directory.path))
        defer { worker.forceTerminate() }

        #expect(await worker.start())
        // A start on a worker that is already running spawns nothing and
        // must not open the log in the middle of a dictation.
        #expect(await worker.start())
        #expect(occurrences("its later output is not logged", in: sink) == 1)

        worker.forceTerminate()
        // Let the killed process be reaped, as it is long before a user's
        // next press.
        try? await Task.sleep(for: .milliseconds(500))
        #expect(await worker.start())
        #expect(occurrences("[asr] loading the model", in: sink) == 2, "\(contents(sink))")
        #expect(occurrences("its later output is not logged", in: sink) == 2)
    }

    @Test("the startup gate logs each start, drops what follows, and says so once per start")
    func startupGateUnit() {
        let lines = Locked([String]())
        let gate = WorkerStartupLog(sink: { line in lines.mutate { $0.append(line) } })
        gate.forward("first load\n")
        gate.close()
        gate.forward("a spoken word\n")
        gate.reopen()
        gate.forward("second load\n")
        gate.close()
        gate.close()
        let joined = lines.get.joined()
        #expect(joined.contains("first load") && joined.contains("second load"))
        #expect(!joined.contains("a spoken word"))
        #expect(gate.withheldChunks == 1)
        #expect(lines.get.filter { $0.contains("not logged") }.count == 2, "said once per start")
    }
}

@MainActor
@Suite("The model logs the failure detail the status line leaves out")
struct ModelDiagnosticsTests {

    @Test("a failed dictation is logged with its stage and classification")
    func failureIsLogged() async {
        let lines = Locked([String]())
        let harness = Harness()
        var env = harness.environment()
        env.log = { line in lines.mutate { $0.append(line) } }
        let model = DictationModel(environment: env)
        await model.toggle()
        harness.workers.made[0].state.mutate { $0.replies = [.failure(.engineError("RuntimeError: bad"))] }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(lines.get.contains { $0.contains("dictation failed at asr") && $0.contains("RuntimeError: bad") })
    }
}
#endif
