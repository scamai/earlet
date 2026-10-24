#if canImport(Testing)
import Foundation
import Testing
import DictationLearning
import DictationKit
import DictationIPC
@testable import DictationAppCore

private actor ContextWire: AsrWireClient {
    var frames: [(String, [String: JSONValue])] = []
    func start() async throws {}
    func stop() async {}
    func sendFlat(op: String, fields: [String: JSONValue]) async throws -> IpcResponse {
        frames.append((op, fields))
        var reply: [String: JSONValue] = ["ok": .bool(true)]
        if op == "init" { reply["engine"] = .string("mlx") }
        if op == "transcribe" { reply["text"] = .string("we use trovio") }
        if op == "stream_start" { reply["stream_id"] = .string("context-stream") }
        let bytes = try JSONEncoder().encode(JSONValue.object(reply))
        return try JSONDecoder().decode(IpcResponse.self, from: bytes)
    }
    func scenes(for op: String) -> [String?] {
        frames.filter { $0.0 == op }.map { $0.1["vocabulary_scene"]?.stringValue }
    }
}

@MainActor
@Suite("Context vocabulary crosses native app and worker boundaries")
struct ContextVocabularyTests {
    private func records(_ url: URL) throws -> [[String: Any]] {
        let document = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        let journal = try #require(document["evidence"] as? [String: Any])
        return try #require(journal["observations"] as? [[String: Any]])
    }

    @Test("only fixed scene tags are derived from application identity")
    func categories() {
        #expect(LearningScene.forApplication("ai.scam.witty") == .terminal)
        #expect(LearningScene.forApplication("dev.shenxingy.witty") == .terminal)
        #expect(LearningScene.forApplication("ai.scam.witty.debug") == .other)
        #expect(LearningScene.forApplication("com.apple.Terminal") == .terminal)
        #expect(LearningScene.forApplication("com.tencent.xinWeChat") == .wechat)
        #expect(LearningScene.forApplication("com.example.editor") == .other)
        #expect(LearningScene.forApplication(nil) == nil)
        #expect(LearningScene.forApplication("") == nil)
    }

    @Test("real product worker carries the scene through restart, preview and final frames")
    func realWorkerFrames() async {
        let resources = DictationResources(python: "/p", runtime: "/r", model: "/m")
        let slot = WorkerSlot()
        for scene in ["terminal", "wechat", nil, "invalid"] as [String?] {
            slot.setVocabularyScene(scene)
            let wire = ContextWire()
            slot.worker = DictationEnvironment.makeLiveWorker(resources, stderrSink: { _ in }, wireClient: wire)
            #expect(await slot.start())
            #expect(await slot.startPreview(languages: ["en"]) == "context-stream")
            let output = await slot.transcribe(samples: [0.2, 0.2], sampleRate: 16_000)
            #expect(output == .text("we use trovio"))
            let expected = scene == "invalid" ? nil : scene
            #expect(await wire.scenes(for: "transcribe") == [expected])
            #expect(await wire.scenes(for: "stream_start") == [expected])
        }
    }

    @Test("recording snapshots scene while correction records the verified paste application",
          .timeLimit(.minutes(1)))
    func appSwitchDuringHold() async throws {
        let harness = Harness()
        harness.targetApplication.mutate { $0 = "ai.scam.witty" }
        let wire = ContextWire()
        var environment = harness.environment()
        environment.makeWorker = { resources in
            DictationEnvironment.makeLiveWorker(resources, stderrSink: { _ in }, wireClient: wire)
        }
        let model = DictationModel(environment: environment)
        await model.toggle()
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.targetApplication.mutate { $0 = "com.tencent.xinWeChat" }
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording && !harness.correctionWatch.begins.get.isEmpty })
        #expect(await wire.scenes(for: "transcribe") == ["terminal"])
        #expect(harness.correctionWatch.begins.get.last?.targetApp == "com.tencent.xinWeChat")
        harness.correctionWatch.settle("We use Trovioo")
        #expect(await eventually { harness.learningFile?.words.first?.text == "Trovioo" })
        let rows = try records(harness.learningURL)
        #expect(rows.count == 1)
        #expect(rows.first?["scene"] as? String == "wechat")
        #expect(!harness.learningFileText.contains("com.tencent"))
        #expect(!harness.learningFileText.contains("dev.shenxingy"))
        await model.toggle()
    }

    @Test("one scene contribution survives restart and revision, and retraction removes it")
    func revisionAndRetraction() async throws {
        let harness = Harness()
        let first = try #require(harness.learning)
        _ = await first.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use Trovioo", scene: .terminal)
        let reloaded = DictationLearning(store: LearningStore(url: harness.learningURL), lexicon: testLexicon)
        _ = await reloaded.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use TrovioAI", scene: .wechat)
        let rows = try records(harness.learningURL)
        #expect(rows.count == 1)
        #expect(rows.first?["scene"] as? String == "terminal")
        _ = await reloaded.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use trovio", scene: .wechat)
        #expect(try records(harness.learningURL).isEmpty)
    }
}
#endif
