// How the product launches and talks to its speech worker, through the
// product's own worker type rather than a fake: the live preview reaches the
// type `makeLiveWorker` builds, and the interpreter runs shut off from the
// user's Python and from paths outside a shipped bundle. A scripted wire or a
// shell script stands in for Python, so no interpreter or model is needed.
// Quit-and-reopen's helper, the other process the app launches, is here too,
// and so is a reply stream that stops being framed.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationIPC
@testable import DictationAppCore

/// The worker's side of the wire, answering each op the way `asr_server.py`
/// does and remembering every op it was sent, in order, and each transcribe
/// request's fields.
private actor ScriptedWire: AsrWireClient {
    private var sent: [String] = []
    private var transcribeFields: [[String: JSONValue]] = []
    /// The next transcribe replies' `dropped` words, in order; none answers
    /// with words.
    private var dropped: [String] = []

    func start() async throws {}
    func stop() async {}

    func sendFlat(op: String, fields: [String: JSONValue]) async throws -> IpcResponse {
        sent.append(op)
        switch op {
        case "init":
            return Self.reply(["ok": .bool(true), "engine": .string("mlx")])
        case "stream_start":
            return Self.reply(["ok": .bool(true), "stream_id": .string("wire-preview"),
                               "engine": .string("whisper")])
        case "stream_push":
            return Self.reply(["ok": .bool(true), "text": .string("[You] newest words"),
                               "is_endpoint": .bool(false)])
        case "transcribe":
            transcribeFields.append(fields)
            guard !dropped.isEmpty else {
                return Self.reply(["ok": .bool(true), "text": .string("hello world")])
            }
            return Self.reply(["ok": .bool(true), "text": .string(""), "dropped": .string(dropped.removeFirst())])
        default:
            return Self.reply(["ok": .bool(true)])
        }
    }

    var ops: [String] { sent }
    var transcribes: [[String: JSONValue]] { transcribeFields }
    func answerDropped(_ words: [String]) { dropped = words }

    private static func reply(_ fields: [String: JSONValue]) -> IpcResponse {
        let data = try! JSONEncoder().encode(JSONValue.object(fields))
        return try! JSONDecoder().decode(IpcResponse.self, from: data)
    }
}

private let anyResources = DictationResources(python: "/p", runtime: "/r", model: "/m")

private func scratchDirectory() throws -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("dictation-launch-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func lines(_ url: URL) -> [String] {
    let text = (try? String(contentsOf: url, encoding: .utf8)) ?? ""
    return text.split(separator: "\n", omittingEmptySubsequences: true).map(String.init)
}

/// This file's suites under one name, so `--filter WorkerLaunchTests` runs
/// them all.
enum WorkerLaunchTests {}

extension WorkerLaunchTests {
    @Suite("The product's worker serves the live preview")
    struct LiveWorkerPreviewTests {

        @Test("the slot opens a stream on the worker makeLiveWorker builds")
        func slotOpensAStreamOnTheProductWorker() async throws {
            let wire = ScriptedWire()
            let worker = DictationEnvironment.makeLiveWorker(
                anyResources, stderrSink: { _ in }, wireClient: wire)
            #expect(await worker.start())
            let slot = WorkerSlot()
            slot.worker = worker

            // The cast inside the slot is the one the product makes; a worker
            // that does not adopt the preview protocol answers nil here.
            let stream = await slot.startPreview(languages: ["en"])
            #expect(stream == "wire-preview")
            #expect(await wire.ops == ["init", "stream_start"])
        }

        @Test("a hold's preview session streams through the slot: opened, pushed, cancelled",
              .timeLimit(.minutes(1)))
        func previewSessionStreamsThroughTheSlot() async throws {
            let wire = ScriptedWire()
            let worker = DictationEnvironment.makeLiveWorker(
                anyResources, stderrSink: { _ in }, wireClient: wire)
            #expect(await worker.start())
            let slot = WorkerSlot()
            slot.worker = worker

            // The composition the model makes for a hold, over the same slot.
            let captions = Locked([String]())
            let session = DictationLivePreviewSession(
                transport: slot, previewSink: { text in captions.mutate { $0.append(text) } })
            session.start(languages: ["en"])
            var opened = false
            for _ in 0..<250 where !opened {
                try? await Task.sleep(for: .milliseconds(20))
                opened = await wire.ops.contains("stream_start")
            }
            #expect(opened, "no stream_start reached the worker")
            // Longer than one 0.16 s push at 16 kHz.
            session.append(samples: [Float](repeating: 0.2, count: 4_000), sampleRate: 16_000)
            var captioned = false
            for _ in 0..<250 where !captioned {
                try? await Task.sleep(for: .milliseconds(20))
                captioned = captions.get.contains("newest words")
            }
            #expect(captioned, "\(captions.get)")
            await session.stop()

            // What the device log's profile line reports, as values.
            let metrics = try #require(session.lastMetrics)
            #expect(metrics.streamOpenMs != nil)
            #expect(metrics.pushCount >= 1)
            #expect(await wire.ops == ["init", "stream_start", "stream_push", "stream_cancel"])
        }
    }

    @Suite("The product's worker carries an empty decode's reason and a recovery")
    struct LiveWorkerNoWordsTests {

        @Test("the reply's drop word reaches the detail; an unknown one reads as other")
        func dropWordReachesTheDetail() async throws {
            let wire = ScriptedWire()
            let worker = DictationEnvironment.makeLiveWorker(
                anyResources, stderrSink: { _ in }, wireClient: wire)
            #expect(await worker.start())
            let slot = WorkerSlot()
            slot.worker = worker
            await wire.answerDropped(["language", "louder"])
            let samples = [Float](repeating: 0.2, count: 1_600)

            var reasons: [DictationAsrDropReason?] = []
            for _ in 0..<3 {
                guard case .result(let detail) = await slot.transcribeDetailed(
                    samples: samples, sampleRate: 16_000, compareBaseline: false) else {
                    Issue.record("the decode failed"); return
                }
                reasons.append(detail.dropReason)
            }
            #expect(reasons == [.language, .other, nil])
        }

        @Test("only Recover Last Dictation's decode asks the worker to skip its checks")
        func recoveryReachesTheWire() async throws {
            let wire = ScriptedWire()
            let worker = DictationEnvironment.makeLiveWorker(
                anyResources, stderrSink: { _ in }, wireClient: wire)
            #expect(await worker.start())
            let slot = WorkerSlot()
            slot.worker = worker
            slot.setLanguageCode("ja")
            let samples = [Float](repeating: 0.2, count: 1_600)

            _ = await slot.transcribeDetailed(samples: samples, sampleRate: 16_000, compareBaseline: false)
            #expect(await slot.transcribeForRecovery(samples: samples, sampleRate: 16_000) == .text("hello world"))

            let sent = await wire.transcribes
            #expect(sent.count == 2)
            #expect(sent.first?["recovery"] == nil)
            #expect(sent.last?["recovery"] == .bool(true))
            #expect(sent.last?["language"] == .string("ja"), "a recovery keeps the chosen language")
        }
    }

    @Suite("The worker runs shut off from the user's Python")
    struct WorkerIsolationTests {

        @Test("the launch ignores PYTHON* variables and the user's site-packages")
        func launchArguments() throws {
            let worker = DictationEnvironment.makeLiveWorker(anyResources, stderrSink: { _ in })
            let config = try #require((worker as? LiveWorker)?.transport.launchConfig)
            #expect(config.pythonPath == "/p")
            #expect(config.workingDirectory == "/r", "-m finds the package from the runtime directory")
            #expect(config.arguments == ["-E", "-s", "-B", "-u", "-m", "transcription_worker.asr_server"])
            // -I would also take the working directory off sys.path, and the
            // worker registers stream_start while it loads, before its __main__
            // guard can add the runtime: every live preview would be refused.
            #expect(!config.arguments.contains("-I"))
        }

        @Test("an inherited PYTHON* variable never reaches the worker; the user site is off")
        func inheritedPythonVariablesAreDropped() throws {
            let worker = DictationEnvironment.makeLiveWorker(anyResources, stderrSink: { _ in })
            let config = try #require((worker as? LiveWorker)?.transport.launchConfig)
            let environment = PythonEngineClient.subprocessEnvironment(
                inherited: ["PYTHONPATH": "/Users/someone/site", "PYTHONHOME": "/Users/someone/py",
                            "PYTHONSTARTUP": "/Users/someone/start.py", "PYTHONUSERBASE": "/Users/someone",
                            "HOME": "/Users/someone", "PATH": "/usr/bin:/bin"],
                overrides: config.environment)
            #expect(environment.keys.filter { $0.hasPrefix("PYTHON") }.sorted()
                    == ["PYTHONDONTWRITEBYTECODE", "PYTHONNOUSERSITE", "PYTHONUNBUFFERED"])
            #expect(environment["PYTHONNOUSERSITE"] == "1")
            #expect(environment["HOME"] == "/Users/someone", "everything else is still inherited")
            #expect(environment["DICTATION_ASR_ENGINE"] == "mlx", "the worker's own settings still arrive")
        }

        @Test("the spawned process gets those arguments, the runtime directory and that environment",
              .timeLimit(.minutes(1)))
        func spawnedProcessIsIsolated() async throws {
            let directory = try scratchDirectory()
            defer { try? FileManager.default.removeItem(at: directory) }
            // Records what it was started with, then fails the way a worker
            // that cannot import does.
            let python = directory.appendingPathComponent("python3")
            let script = """
                #!/bin/sh
                printf '%s\\n' "$@" > '\(directory.path)/argv'
                pwd -P > '\(directory.path)/cwd'
                env > '\(directory.path)/env'
                exit 1
                """
            FileManager.default.createFile(atPath: python.path, contents: Data(script.utf8),
                                           attributes: [.posixPermissions: 0o755])
            let worker = DictationEnvironment.makeLiveWorker(
                DictationResources(python: python.path, runtime: directory.path, model: directory.path),
                stderrSink: { _ in })
            #expect(await worker.start() == false)

            #expect(lines(directory.appendingPathComponent("argv")) == PythonAsrTransport.workerArguments)
            let runtime = try #require(realpath(directory.path, nil))
            defer { free(runtime) }
            #expect(lines(directory.appendingPathComponent("cwd")) == [String(cString: runtime)])
            let environment = lines(directory.appendingPathComponent("env"))
            #expect(environment.contains("PYTHONNOUSERSITE=1"))
            #expect(environment.contains("PYTHONDONTWRITEBYTECODE=1"))
            #expect(!environment.contains { $0.hasPrefix("PYTHONPATH=") })
        }
    }

    @Suite("A shipped build takes its speech resources from its own bundle")
    struct ShippedResourceTests {

        /// A complete resource tree: Python, runtime and model.
        private func resourceTree(in parent: URL, named name: String) throws -> URL {
            let root = parent.appendingPathComponent(name)
            let fm = FileManager.default
            try fm.createDirectory(at: root.appendingPathComponent("python/bin"), withIntermediateDirectories: true)
            try fm.createDirectory(at: root.appendingPathComponent("runtime/transcription_worker"),
                                   withIntermediateDirectories: true)
            try fm.createDirectory(at: root.appendingPathComponent("model"), withIntermediateDirectories: true)
            for name in ["python3", "python3.11"] {
                fm.createFile(atPath: root.appendingPathComponent("python/bin/\(name)").path,
                              contents: Data("#!/bin/sh\n".utf8), attributes: [.posixPermissions: 0o755])
            }
            fm.createFile(atPath: root.appendingPathComponent("runtime/transcription_worker/asr_server.py").path,
                          contents: Data())
            fm.createFile(atPath: root.appendingPathComponent("model/config.json").path, contents: Data("{}".utf8))
            fm.createFile(atPath: root.appendingPathComponent("model/weights.safetensors").path, contents: Data())
            return root
        }

        @Test("DICTATION_* paths outside the bundle are ignored; a debug build still honours them")
        func outsideOverridesAreIgnored() throws {
            let parent = try scratchDirectory()
            defer { try? FileManager.default.removeItem(at: parent) }
            let bundle = try resourceTree(in: parent, named: "Resources")
            let elsewhere = try resourceTree(in: parent, named: "elsewhere")
            let environment = [
                "DICTATION_PYTHON": elsewhere.appendingPathComponent("python/bin/python3").path,
                "DICTATION_RUNTIME": elsewhere.appendingPathComponent("runtime").path,
                "DICTATION_MODEL_DIR": elsewhere.appendingPathComponent("model").path,
            ]

            let shipped = try #require(DictationResources.resolve(
                environment: environment, resourceRoot: bundle, overridesMayLeaveTheBundle: false))
            #expect(shipped == DictationResources(
                python: bundle.appendingPathComponent("python/bin/python3").path,
                runtime: bundle.appendingPathComponent("runtime").path,
                model: bundle.appendingPathComponent("model").path))

            let development = try #require(DictationResources.resolve(
                environment: environment, resourceRoot: bundle, overridesMayLeaveTheBundle: true))
            #expect(development.python == environment["DICTATION_PYTHON"])
            #expect(development.runtime == environment["DICTATION_RUNTIME"])
            #expect(development.model == environment["DICTATION_MODEL_DIR"])
        }

        @Test("an override inside the bundle is honoured; .. and a link out of it are not")
        func onlyPathsInsideTheBundleCount() throws {
            let parent = try scratchDirectory()
            defer { try? FileManager.default.removeItem(at: parent) }
            let bundle = try resourceTree(in: parent, named: "Resources")
            let elsewhere = try resourceTree(in: parent, named: "elsewhere")
            let bundled = bundle.appendingPathComponent("python/bin/python3").path
            func shippedPython(_ override: String) -> String? {
                DictationResources.resolve(environment: ["DICTATION_PYTHON": override],
                                           resourceRoot: bundle, overridesMayLeaveTheBundle: false)?.python
            }

            let inside = bundle.appendingPathComponent("python/bin/python3.11").path
            #expect(shippedPython(inside) == inside)

            let climbsOut = bundle.path + "/../elsewhere/python/bin/python3"
            #expect(shippedPython(climbsOut) == bundled)

            let link = bundle.appendingPathComponent("python/bin/python-link")
            try FileManager.default.createSymbolicLink(
                at: link, withDestinationURL: elsewhere.appendingPathComponent("python/bin/python3"))
            #expect(shippedPython(link.path) == bundled)
        }

        /// `Resources-evil` begins with the characters of `Resources`, so a
        /// check on the path string would call everything in it inside the
        /// bundle. The sibling is a complete resource tree, so a check that
        /// let it through would resolve to it rather than fail on a missing
        /// file and pass this test by accident.
        @Test("a sibling folder whose name starts with the bundle's name is not inside it")
        func aSiblingThatSharesThePrefixIsOutside() throws {
            let parent = try scratchDirectory()
            defer { try? FileManager.default.removeItem(at: parent) }
            let bundle = try resourceTree(in: parent, named: "Resources")
            let sibling = try resourceTree(in: parent, named: "Resources-evil")
            #expect(sibling.path.hasPrefix(bundle.path))
            let environment = [
                "DICTATION_PYTHON": sibling.appendingPathComponent("python/bin/python3").path,
                "DICTATION_RUNTIME": sibling.appendingPathComponent("runtime").path,
                "DICTATION_MODEL_DIR": sibling.appendingPathComponent("model").path,
            ]

            let shipped = try #require(DictationResources.resolve(
                environment: environment, resourceRoot: bundle, overridesMayLeaveTheBundle: false))
            #expect(shipped == DictationResources(
                python: bundle.appendingPathComponent("python/bin/python3").path,
                runtime: bundle.appendingPathComponent("runtime").path,
                model: bundle.appendingPathComponent("model").path))
        }
    }

    @MainActor
    @Suite("Quit and reopen passes the app's path to its helper as data")
    struct RelaunchHelperTests {

        @Test("the path is one positional argument and never part of the script")
        func pathIsAnArgument() {
            let path = "/tmp/a\"$(touch x).app"
            let arguments = RelaunchHelper.arguments(pid: 4242, bundlePath: path)
            #expect(arguments == ["-c", RelaunchHelper.script, "dictation-relaunch", "4242", path, "/usr/bin/open"])
            #expect(!arguments[1].contains("touch") && !arguments[1].contains("4242"))
        }

        @Test("sh hands a path full of shell syntax to open unchanged, and runs none of it",
              .timeLimit(.minutes(1)))
        func shellRunsNoneOfThePath() async throws {
            let directory = try scratchDirectory()
            defer { try? FileManager.default.removeItem(at: directory) }
            // Stands in for /usr/bin/open: records its arguments, one per line.
            let opener = directory.appendingPathComponent("open")
            FileManager.default.createFile(
                atPath: opener.path,
                contents: Data("#!/bin/sh\nprintf '%s\\n' \"$@\" > '\(directory.path)/opened'\n".utf8),
                attributes: [.posixPermissions: 0o755])
            let pwned = directory.appendingPathComponent("pwned").path
            let paths = [
                "\(directory.path)/a\"$(touch \(pwned)).app",
                "\(directory.path)/b\"$(touch \(pwned))\".app",
                "\(directory.path)/c`touch \(pwned)`.app",
            ]
            for path in paths {
                try? FileManager.default.removeItem(atPath: directory.appendingPathComponent("opened").path)
                var quits = 0
                // A pid no process can have, so the helper's wait ends at once.
                let started = RelaunchHelper.relaunch(
                    pid: Int32.max, bundlePath: path, opener: opener.path,
                    terminate: { quits += 1 }, log: { _ in })
                #expect(started)
                #expect(quits == 1)
                var opened: [String] = []
                for _ in 0..<250 where opened.count < 3 {
                    try? await Task.sleep(for: .milliseconds(20))
                    opened = lines(directory.appendingPathComponent("opened"))
                }
                #expect(opened == ["-n", "-a", path])
                #expect(!FileManager.default.fileExists(atPath: pwned), "sh ran part of \(path)")
            }
        }

        @Test("a helper that does not start leaves Dictation open and says why in the log")
        func noHelperNoQuit() {
            var quits = 0
            var logged: [String] = []
            let started = RelaunchHelper.relaunch(
                pid: Int32.max, bundlePath: "/Applications/Dictation.app",
                shell: "/nonexistent/sh", terminate: { quits += 1 }, log: { logged.append($0) })
            #expect(!started)
            #expect(quits == 0)
            #expect(logged.contains { $0.contains("the helper did not start") })
        }
    }

    @MainActor
    @Suite("A reply header no reply could have fails at once")
    struct ReplyFrameLimitTests {

        @Test("the header 'Star' is refused; lengths up to 64 MiB are read")
        func starIsRefused() throws {
            // "Starting…" from a native library, read as a length: 1,400,136,050.
            let star = Data("Star".utf8)
            #expect(try IpcFraming.decodeLength(star) == 0x5374_6172, "the plain parser is unchanged")
            #expect(throws: IpcFraming.FramingError.oversizedPayload(length: 0x5374_6172, limit: 64 << 20)) {
                try IpcFraming.decodeResponseLength(star)
            }
            #expect(try IpcFraming.decodeResponseLength(Data([0x04, 0x00, 0x00, 0x00])) == 64 << 20)
            #expect(throws: IpcFraming.FramingError.oversizedPayload(length: (64 << 20) + 1, limit: 64 << 20)) {
                try IpcFraming.decodeResponseLength(Data([0x04, 0x00, 0x00, 0x01]))
            }
        }

        /// A stand-in worker that writes a line to stdout where a reply should
        /// be, after `framedReplies` well-formed ones, then stays alive with its
        /// stdout still open, as the real worker does: the host gets the four
        /// bytes `Star` and nothing more. Each process appends its pid to `pids`.
        private func unframedWorker(in directory: URL, framedReplies: Int) -> URL {
            var script = """
                #!/bin/sh
                read_frame() {
                  set -- $(dd bs=1 count=4 2>/dev/null | od -An -tu1)
                  [ $# -eq 4 ] || exit 0
                  dd bs=1 count=$(( ($1 << 24) + ($2 << 16) + ($3 << 8) + $4 )) of=/dev/null 2>/dev/null
                }
                echo $$ >> '\(directory.path)/pids'

                """
            for _ in 0..<framedReplies {
                script += "read_frame\nprintf '\\000\\000\\000\\013{\"ok\":true}'\n"
            }
            script += "read_frame\nprintf 'Starting the accelerator\\n'\nexec sleep 60\n"
            let python = directory.appendingPathComponent("python3")
            FileManager.default.createFile(atPath: python.path, contents: Data(script.utf8),
                                           attributes: [.posixPermissions: 0o755])
            return python
        }

        private func pids(in directory: URL) -> [Int32] {
            lines(directory.appendingPathComponent("pids")).compactMap { Int32($0) }
        }

        /// Whether `pid` has exited and been reaped, within two seconds.
        private func exits(_ pid: Int32) async -> Bool {
            for _ in 0..<100 {
                if kill(pid, 0) != 0 { return true }
                try? await Task.sleep(for: .milliseconds(20))
            }
            return false
        }

        @Test("a worker that prints where its init reply belongs fails the start at once",
              .timeLimit(.minutes(1)))
        func unframedInitReplyFailsTheStart() async throws {
            let directory = try scratchDirectory()
            defer { try? FileManager.default.removeItem(at: directory) }
            let python = unframedWorker(in: directory, framedReplies: 0)
            let logged = Locked([String]())
            let worker = DictationEnvironment.makeLiveWorker(
                DictationResources(python: python.path, runtime: directory.path, model: directory.path),
                stderrSink: { line in logged.mutate { $0.append(line) } })
            defer { worker.forceTerminate() }

            let started = Locked<Bool?>(nil)
            let finished = await finishes(within: 5) {
                let result = await worker.start()
                started.mutate { $0 = result }
            }
            // Without the limit the read is still waiting for 1.4 GB.
            if !finished { worker.forceTerminate() }
            #expect(finished, "the start waited on a length no reply could have")
            #expect(started.get == false)
            #expect(logged.get.joined().contains("oversizedPayload"), "\(logged.get)")
        }

        @Test("an unframed reply to a transcription is an I/O failure, and the next start spawns a fresh worker",
              .timeLimit(.minutes(1)))
        func unframedReplyRestartsTheWorker() async throws {
            let directory = try scratchDirectory()
            defer { try? FileManager.default.removeItem(at: directory) }
            // A good init reply, then "Star…" where the transcription's belongs.
            let python = unframedWorker(in: directory, framedReplies: 1)
            let worker = DictationEnvironment.makeLiveWorker(
                DictationResources(python: python.path, runtime: directory.path, model: directory.path),
                stderrSink: { _ in })
            defer { worker.forceTerminate() }
            #expect(await worker.start())

            let outcome = Locked<DictationAsrOutcome?>(nil)
            let finished = await finishes(within: 5) {
                let result = await worker.transcribe(samples: [0.1, 0.2], sampleRate: 16_000)
                outcome.mutate { $0 = result }
            }
            if !finished { worker.forceTerminate() }
            #expect(finished, "the transcription waited on a length no reply could have")
            guard case .failed(.ioError(let detail))? = outcome.get else {
                Issue.record("expected an I/O failure, got \(String(describing: outcome.get))")
                return
            }
            #expect(detail.contains("oversizedPayload"))
            #expect(DictationModel.workerDied(.ioError(detail)), "the model restarts the worker after it")

            let first = try #require(pids(in: directory).first)
            #expect(await exits(first), "the out-of-step worker is still running")
            // The model's restart: a start on the same worker spawns a new process
            // rather than finding the old one still holding the pipe.
            #expect(await worker.start())
            #expect(pids(in: directory).count == 2)
        }
    }
}
#endif
