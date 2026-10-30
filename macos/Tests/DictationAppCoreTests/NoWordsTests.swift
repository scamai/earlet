// Audible audio that comes back with no words: the ending says why, and a
// take a recognition gate emptied is kept for Recover Last Dictation, whose
// decode asks the worker to skip those gates. Scratch spool and fakes only;
// no microphone, worker or owner store is touched.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

/// One empty decode: the worker's reason, the take's level, and what the
/// user is shown and keeps.
struct NoWordsCase: Sendable {
    let name: String
    let dropped: DictationAsrDropReason?
    let level: Float
    let title: String
    let kept: Bool
}

private let noWordsCases: [NoWordsCase] = [
    NoWordsCase(name: "quiet", dropped: .quiet, level: 0.2, title: "Too quiet to make out", kept: false),
    NoWordsCase(name: "language", dropped: .language, level: 0.2, title: "Couldn't tell the language", kept: true),
    NoWordsCase(name: "loud junk", dropped: .junk, level: 0.2, title: "Couldn't make out the words", kept: true),
    NoWordsCase(name: "loud other", dropped: .other, level: 0.2, title: "Couldn't make out the words", kept: true),
    NoWordsCase(name: "soft junk", dropped: .junk, level: 0.05, title: "No words recognized", kept: false),
    NoWordsCase(name: "no reason", dropped: nil, level: 0.2, title: "No words recognized", kept: false),
]

@MainActor
@Suite("An empty decode says why, and keeps a take a gate emptied")
struct NoWordsTests {
    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        // The ending is read after the release; its dwell must not end first.
        model.hudOutcomeSecondsOverride = 60
        return model
    }

    /// One hold whose capture and spooled audio sit at `level`, answered
    /// with `reply`.
    private func dictate(_ harness: Harness, _ model: DictationModel, level: Float = 0.2,
                         reply: FakeWorker.Reply) async {
        harness.capture.samples.mutate { $0 = [Float](repeating: level, count: 16_000) }
        harness.workers.made[0].state.mutate { $0.replies = [reply] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: level, count: 16_000))
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("each reason has its own ending, and only a gate's keeps the take", arguments: noWordsCases)
    func endingAndTake(_ scenario: NoWordsCase) async {
        let harness = Harness()
        let model = await enabled(harness)
        let reply: FakeWorker.Reply = scenario.dropped.map { FakeWorker.Reply.dropped($0) } ?? .text("")

        await dictate(harness, model, level: scenario.level, reply: reply)

        #expect(harness.inserts.texts.get.isEmpty, "\(scenario.name)")
        #expect(model.hudOutcome?.title == scenario.title, "\(scenario.name)")
        #expect((model.savedTake != nil) == scenario.kept, "\(scenario.name)")
        if scenario.kept {
            #expect(harness.takeSpoolFiles() == [DictationTakeSpool.keptFileName], "\(scenario.name)")
        } else {
            #expect(harness.takeSpoolFiles().isEmpty, "\(scenario.name)")
        }
        #expect(model.status.contains(DictationCopy.takeSavedWhere) == scenario.kept, "\(scenario.name)")
        #expect(!model.status.contains("shorter phrase"), "\(scenario.name)")
    }

    @Test("the status line is the sentence for the reason the worker gave")
    func statusFollowsTheReason() async {
        let harness = Harness()
        let model = await enabled(harness)

        // A prefix: a key-check notice may follow the sentence.
        await dictate(harness, model, reply: .dropped(.quiet))
        #expect(model.status.hasPrefix(DictationCopy.noWords(.tooQuiet, kept: false)))
        await dictate(harness, model, reply: .dropped(.language))
        #expect(model.status.hasPrefix(DictationCopy.noWords(.languageUnclear, kept: true)))
    }

    @Test("recovering a take the language gate emptied skips the gates, in the chosen language")
    func recoverySkipsTheGates() async {
        let harness = Harness()
        let model = await enabled(harness)
        await dictate(harness, model, reply: .dropped(.language))
        #expect(model.savedTake != nil)

        model.selectLanguage(.japanese)
        harness.workers.made[0].state.mutate { $0.replies = [.text("recovered words")] }
        await model.recoverLastDictation()

        let worker = harness.workers.made[0].state.get
        #expect(worker.recoveries == [false, true], "only the recovery asks the worker to skip them")
        #expect(worker.transcribeLanguages.last == "ja")
        #expect(harness.clipboard.get == ["Recovered words"])
        #expect(harness.inserts.texts.get.isEmpty, "a recovery is copied, never typed")
        #expect(model.savedTake == nil)
        #expect(harness.takeSpoolFiles().isEmpty)
    }

    @Test("a hold with words is decoded under the gates and keeps nothing")
    func wordsKeepTheGates() async {
        let harness = Harness()
        let model = await enabled(harness)

        await dictate(harness, model, reply: .text("hello world"))

        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.recoveries == [false])
        #expect(model.savedTake == nil)
    }
}
#endif
