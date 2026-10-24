#if canImport(Testing)
import Foundation
import DictationIPC
import Testing
@testable import DictationKit

private final class PreviewTextCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [String] = []
    func append(_ value: String) { lock.lock(); values.append(value); lock.unlock() }
    var snapshot: [String] { lock.lock(); defer { lock.unlock() }; return values }
}

private final class PreviewAudioCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var chunks: [[Float]] = []
    func append(_ value: [Float]) { lock.lock(); chunks.append(value); lock.unlock() }
    var snapshot: [[Float]] { lock.lock(); defer { lock.unlock() }; return chunks }
}

private actor PreviewTransportSpy: DictationPreviewTransport {
    private var starts: [[String]] = []
    private var pushes = 0
    private var cancels: [String] = []
    private let reply: DictationPreviewHypothesis?

    init(reply: DictationPreviewHypothesis? = DictationPreviewHypothesis(
        text: "[You] one two three four five six seven eight", isEndpoint: false
    )) {
        self.reply = reply
    }

    func startPreview(languages: [String]) async -> String? {
        starts.append(languages)
        return "preview-1"
    }

    func pushPreview(
        streamID: String, samples: [Float], sampleRate: Int
    ) async -> DictationPreviewHypothesis? {
        pushes += 1
        return reply
    }

    func cancelPreview(streamID: String) async { cancels.append(streamID) }

    func snapshot() -> (starts: [[String]], pushes: Int, cancels: [String]) {
        (starts, pushes, cancels)
    }
}

private actor DelayedPreviewOpen: DictationPreviewTransport {
    private var continuation: CheckedContinuation<String?, Never>?
    private var cancels: [String] = []

    func startPreview(languages: [String]) async -> String? {
        await withCheckedContinuation { continuation = $0 }
    }
    func pushPreview(
        streamID: String, samples: [Float], sampleRate: Int
    ) async -> DictationPreviewHypothesis? { nil }
    func cancelPreview(streamID: String) async { cancels.append(streamID) }

    func isWaiting() -> Bool { continuation != nil }
    func complete(_ streamID: String?) {
        let pending = continuation
        continuation = nil
        pending?.resume(returning: streamID)
    }
    func cancelledIDs() -> [String] { cancels }
}

private actor PreviewWireSpy: AsrWireClient {
    private var calls: [(String, [String: JSONValue])] = []

    func start() async throws {}
    func stop() async {}
    func sendFlat(op: String, fields: [String: JSONValue]) async throws -> IpcResponse {
        calls.append((op, fields))
        switch op {
        case "init":
            return Self.response([
                "ok": .bool(true), "engine": .string("mlx"),
            ])
        case "stream_start":
            return Self.response([
                "ok": .bool(true), "stream_id": .string("wire-preview"),
                "engine": .string("whisper"),
            ])
        case "stream_push":
            return Self.response([
                "ok": .bool(true),
                "text": .string("[You] settled\n[You] newest words"),
                "is_endpoint": .bool(false),
            ])
        case "stream_cancel", "shutdown":
            return Self.response(["ok": .bool(true)])
        default:
            return Self.response(["ok": .bool(false), "error": .string("unexpected")])
        }
    }

    func snapshot() -> [(String, [String: JSONValue])] { calls }

    private static func response(_ fields: [String: JSONValue]) -> IpcResponse {
        let data = try! JSONEncoder().encode(JSONValue.object(fields))
        return try! JSONDecoder().decode(IpcResponse.self, from: data)
    }
}

private func eventually(
    attempts: Int = 200,
    _ condition: @escaping @Sendable () async -> Bool
) async -> Bool {
    for _ in 0..<attempts {
        if await condition() { return true }
        try? await Task.sleep(for: .milliseconds(5))
    }
    return false
}

@Suite("Dictation live preview")
struct DictationLivePreviewTests {
    @Test("Python transport speaks the shared Turbo stream wire and never finishes it")
    func pythonWireContract() async throws {
        let wire = PreviewWireSpy()
        let transport = PythonAsrTransport(
            pythonPath: "/unused",
            workingDirectory: "/unused",
            wireClient: wire)
        #expect(await transport.start())

        let streamID = try #require(await transport.startPreview(languages: ["zh", "en", "zh"]))
        let hypothesis = try #require(await transport.pushPreview(
            streamID: streamID,
            samples: [0.25, -0.25],
            sampleRate: 16_000))
        #expect(hypothesis.text == "newest words")
        await transport.cancelPreview(streamID: streamID)

        let calls = await wire.snapshot()
        let start = try #require(calls.first { $0.0 == "stream_start" })
        #expect(start.1["engine"]?.stringValue == "whisper")
        #expect(start.1["client"]?.stringValue == "dictation")
        #expect(start.1["rolling_stable"]?.boolValue == false)
        #expect(start.1["languages"]?.arrayValue?.compactMap(\.stringValue) == ["zh", "en"])
        let push = try #require(calls.first { $0.0 == "stream_push" })
        #expect(push.1["stream_id"]?.stringValue == "wire-preview")
        #expect(push.1["channel"]?.stringValue == "mic")
        let encodedPCM = try #require(push.1["pcm_b64"]?.stringValue)
        #expect(Data(base64Encoded: encodedPCM)?.count == 4)
        #expect(calls.contains { $0.0 == "stream_cancel" })
        #expect(!calls.contains { $0.0 == "stream_finish" })
    }

    @Test("speaker rendering keeps only the newest unlabelled line")
    func liveTail() {
        #expect(DictationPreviewText.liveTail(
            "[You] settled words\n[You] current words") == "current words")
        #expect(DictationPreviewText.liveTail("[unfinished words") == "[unfinished words")
        #expect(DictationPreviewText.liveTail("words [inside] stay") == "words [inside] stay")
        #expect(DictationPreviewText.liveTail("\n  \n") == "")
    }

    @Test("caption uses independent seven-word and sixteen-CJK bounds")
    func captionBounds() {
        #expect(DictationPreviewCaption.window(
            "one two three four five six seven eight")
            == "two three four five six seven eight")
        #expect(DictationPreviewCaption.window("一二三四五六七八九十一二三四五六七八九十")
            == "五六七八九十一二三四五六七八九十")
        #expect(DictationPreviewCaption.window(
            "one two three four five six seven eight 我觉得这个方案没有任何问题")
            == "two three four five six seven eight 我觉得这个方案没有任何问题")
        #expect(DictationPreviewCaption.window("QNN我觉得没问题") == "QNN我觉得没问题")
        #expect(DictationPreviewCaption.window("You…") == "")
    }

    @Test("capture publishes post-trigger chunks but never warm pre-roll")
    func captureRouting() {
        let capture = DictationCapture(preRollSeconds: 0.3)
        let chunks = PreviewAudioCollector()
        capture.onRetainedAudio = { samples, _ in
            chunks.append(samples)
        }
        capture.appendChunkForTesting([0.1])
        #expect(chunks.snapshot.isEmpty)
        capture.armTriggerDownForTesting()
        capture.appendChunkForTesting([0.2, 0.3])
        #expect(chunks.snapshot == [[0.2, 0.3]])
    }

    @Test("one bounded push updates the preview and stop cancels then clears")
    func lifecycle() async {
        let transport = PreviewTransportSpy()
        let text = PreviewTextCollector()
        let logs = PreviewTextCollector()
        let session = DictationLivePreviewSession(
            transport: transport,
            previewSink: { text.append($0) },
            log: { logs.append($0) })

        session.start(languages: ["zh", "en"])
        #expect(await eventually { await transport.snapshot().starts.count == 1 })
        session.append(samples: [Float](repeating: 0.1, count: 2_560), sampleRate: 16_000)
        #expect(await eventually { text.snapshot.contains("two three four five six seven eight") })
        await session.stop()

        let calls = await transport.snapshot()
        #expect(calls.starts == [["zh", "en"]])
        #expect(calls.pushes == 1)
        #expect(calls.cancels == ["preview-1"])
        #expect(text.snapshot.last == "")
        #expect(session.lastMetrics?.pushCount == 1)
        #expect(session.lastMetrics?.lastCaptionCharacterCount
            == "two three four five six seven eight".count)
        #expect(logs.snapshot.count == 1)
        #expect(logs.snapshot[0].contains("pushes=1"))
        #expect(!logs.snapshot[0].contains("two three"))
    }

    @Test("stop racing a late open cancels the orphaned worker stream")
    func lateOpenIsCancelled() async {
        let transport = DelayedPreviewOpen()
        let text = PreviewTextCollector()
        let session = DictationLivePreviewSession(
            transport: transport, previewSink: { text.append($0) })
        session.start(languages: ["en"])
        #expect(await eventually { await transport.isWaiting() })

        let stop = Task { await session.stop() }
        await transport.complete("late-stream")
        await stop.value

        #expect(await transport.cancelledIDs() == ["late-stream"])
        #expect(text.snapshot.last == "")
    }

    @Test("audio waiting on a slow open stays bounded to three seconds")
    func pendingAudioIsBounded() async {
        let transport = DelayedPreviewOpen()
        let session = DictationLivePreviewSession(
            transport: transport, previewSink: { _ in })
        session.start(languages: ["en"])
        #expect(await eventually { await transport.isWaiting() })
        session.append(samples: [Float](repeating: 0.1, count: 80_000), sampleRate: 16_000)
        let stop = Task { await session.stop() }
        await transport.complete(nil)
        await stop.value
        #expect((session.lastMetrics?.droppedSampleCount ?? 0) == 32_000)
        #expect((session.lastMetrics?.pushCount ?? -1) == 0)
    }
}
#endif
