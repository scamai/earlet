#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Quiet holds never insert words from a transient")
struct DictationSilenceInsertionTests {
    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    private func clip(voiced: Double, total: Double, offset: Double = 1.5) -> [Float] {
        let rate = 16_000.0
        var samples = [Float](repeating: 0.0015, count: Int(total * rate))
        let start = Int(offset * rate)
        for i in 0..<Int(voiced * rate) where start + i < samples.count {
            samples[start + i] += 0.024 * Float(sin(2 * Double.pi * 220 * Double(i) / rate))
        }
        return samples
    }

    @Test("a click in room tone never reaches recognition or insertion", arguments: [0.006, 0.020, 0.045], [1.500, 1.525])
    func transientDoesNotInsert(seconds: Double, offset: Double) async {
        let harness = Harness()
        harness.capture.samples.mutate { $0 = clip(voiced: seconds, total: 5.92, offset: offset) }
        let model = harness.model()
        await model.toggle()
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.text("Thank you.")] }

        await dictate(harness, model)

        #expect(worker.state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(model.status.contains("Nothing was heard"))
        #expect(model.enabled)
        #expect(!model.recording && !model.transcribing)

        // A rejected clip must reset the session, and must not eat a short
        // word on the next hold even when it is surrounded by a long pause.
        harness.capture.samples.mutate { $0 = clip(voiced: 0.120, total: 20, offset: 0.017) }
        worker.state.mutate { $0.replies = [.text("yes")] }
        await dictate(harness, model)
        #expect(worker.state.get.transcribes == 1)
        #expect(harness.inserts.texts.get.count == 1)
        #expect(harness.inserts.texts.get.first?.lowercased().contains("yes") == true)
        #expect(!model.recording && !model.transcribing)
    }
}
#endif
