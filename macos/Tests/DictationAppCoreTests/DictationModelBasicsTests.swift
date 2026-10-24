// The app's model, driven end to end through its seams: enable, one hold,
// insertion. The real `DictationSession` runs in between, so the session's
// own stage sequencing is exercised too.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@Suite("Dictation resources resolve only when every part is present")
struct DictationResourcesTests {

    private func makeTree(weights: String?) throws -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("dictation-resources-\(UUID().uuidString)")
        let fm = FileManager.default
        try fm.createDirectory(at: root.appendingPathComponent("python/bin"), withIntermediateDirectories: true)
        try fm.createDirectory(at: root.appendingPathComponent("runtime/transcription_worker"),
                               withIntermediateDirectories: true)
        try fm.createDirectory(at: root.appendingPathComponent("model"), withIntermediateDirectories: true)
        let python = root.appendingPathComponent("python/bin/python3").path
        fm.createFile(atPath: python, contents: Data("#!/bin/sh\n".utf8), attributes: [.posixPermissions: 0o755])
        fm.createFile(atPath: root.appendingPathComponent("runtime/transcription_worker/asr_server.py").path,
                      contents: Data())
        fm.createFile(atPath: root.appendingPathComponent("model/config.json").path, contents: Data("{}".utf8))
        if let weights {
            fm.createFile(atPath: root.appendingPathComponent("model/\(weights)").path, contents: Data())
        }
        return root
    }

    @Test("a complete bundle resolves, with either weights file name")
    func completeBundleResolves() throws {
        for weights in ["weights.safetensors", "model.safetensors"] {
            let root = try makeTree(weights: weights)
            defer { try? FileManager.default.removeItem(at: root) }
            let resolved = DictationResources.resolve(environment: [:], resourceRoot: root)
            #expect(resolved?.model == root.appendingPathComponent("model").path)
        }
    }

    @Test("missing model weights resolve to nothing")
    func missingWeightsDoNotResolve() throws {
        let root = try makeTree(weights: nil)
        defer { try? FileManager.default.removeItem(at: root) }
        #expect(DictationResources.resolve(environment: [:], resourceRoot: root) == nil)
    }

    @Test("DICTATION_* overrides replace the bundled locations")
    func overridesWin() throws {
        let root = try makeTree(weights: "weights.safetensors")
        defer { try? FileManager.default.removeItem(at: root) }
        let elsewhere = FileManager.default.temporaryDirectory.path + "/no-such-python-\(UUID())"
        #expect(DictationResources.resolve(
            environment: ["DICTATION_PYTHON": elsewhere], resourceRoot: root) == nil)
    }
}

@MainActor
@Suite("Dictation model basics")
struct DictationModelBasicsTests {

    @Test("enabling without speech resources builds no worker and says why")
    func missingResourcesStopEnable() async {
        var harness = Harness()
        harness.resources = nil
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        #expect(harness.workers.made.isEmpty)
        #expect(model.status.contains("Speech resources are missing"))
    }

    @Test("one hold types the transcript through the insertion seam")
    func oneHoldInsertsText() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording && model.status.hasPrefix("Text inserted") })
        #expect(harness.inserts.texts.get == ["Hello world"])
    }
}
#endif
