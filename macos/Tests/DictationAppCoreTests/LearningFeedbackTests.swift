#if canImport(Testing)
import DictationLearning
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Saved learning feedback and undo")
struct LearningFeedbackTests {
    private func learn(_ vocabulary: DictationVocabularyModel, id: String = "one") async
        -> DictationLearning.ObserveOutcome {
        await vocabulary.observe(dictationID: id, dictated: "We use trovio", edited: "We use Trovioo")
    }

    @Test("the app's correction observer delivers a saved receipt and a working undo")
    func actualObserverPath() async throws {
        let harness = Harness()
        harness.targetApplication.mutate { $0 = "com.apple.TextEdit" }
        let model = harness.model()
        await model.toggle()
        harness.workers.made[0].state.mutate { $0.replies = [.text("we use trovio")] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually { model.vocabulary.undoToken != nil })
        #expect(harness.learningFile?.words.map(\.text) == ["Trovioo"])
        #expect(model.vocabulary.learningNotice?.contains("Remembered") == true)
        #expect(await eventually { model.hudOutcome?.title == "Remembered your correction" })
        #expect(model.hudOutcome?.detail == "Review or undo from Earlet's menu bar icon.")
        await model.vocabulary.undoLastLearning()
        #expect(harness.learningFile == .empty)
        #expect(model.vocabulary.rows.isEmpty)
        #expect(model.vocabulary.undoToken == nil)
        #expect(model.vocabulary.learningNotice?.contains("undone") == true)
        #expect(!harness.logLines.get.joined().contains("Trovioo"))
    }

    @Test("undo restores the previous alias and word counts, not an empty list")
    func exactUndo() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        _ = await vocabulary.teach(written: "SkyDock", soundsLike: "sky dock")
        _ = await learn(vocabulary)
        let before = try #require(harness.learningFile)
        _ = await learn(vocabulary, id: "two")
        #expect(harness.learningFile?.aliases.contains { $0.heard == "trovio" } == true)
        await vocabulary.undoLastLearning()
        #expect(harness.learningFile == before)
        #expect(vocabulary.rows.contains { $0.title == "SkyDock" })
    }

    @Test("a later manual edit expires the undo token and cannot be overwritten")
    func expiredUndo() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        let outcome = await learn(vocabulary)
        let token = try #require(outcome.undoToken)
        _ = await vocabulary.teach(written: "SkyDock", soundsLike: "")
        #expect(vocabulary.undoToken == nil)
        #expect(vocabulary.learningNotice == nil)
        let learning = try #require(harness.learning)
        #expect(await learning.undoLearning(token) == nil)
        #expect(Set(harness.learningFile?.words.map(\.text) ?? []) == ["Trovioo", "SkyDock"])
    }

    @Test("an unchanged next dictation leaves the saved receipt and undo available")
    func noOpPreservesUndo() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        let first = await learn(vocabulary)
        _ = await vocabulary.observe(dictationID: "unchanged", dictated: "We use it", edited: "We use it")
        #expect(vocabulary.undoToken == first.undoToken)
        #expect(vocabulary.learningNotice != nil)
        await vocabulary.undoLastLearning()
        #expect(harness.learningFile == .empty)
    }

    @Test("undoing a newly winning alias restores its previous contested state")
    func restoreReplacedAlias() async throws {
        let harness = Harness()
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        try LearningStore(url: harness.learningURL).save(LearningState(
            words: [LearnedWord(text: "Trovion", origin: .correction, count: 2, firstSeen: now, lastSeen: now)],
            aliases: [LearnedAlias(heard: "trovio", meant: "Trovion", origin: .correction,
                                  count: 2, firstSeen: now, lastSeen: now)]))
        let vocabulary = harness.model().vocabulary
        _ = await learn(vocabulary)
        _ = await learn(vocabulary, id: "second")
        _ = await learn(vocabulary, id: "third")
        let before = try #require(harness.learningFile)
        #expect(before.aliases.isEmpty, "three vs two is still contested")
        _ = await learn(vocabulary, id: "fourth")
        #expect(harness.learningFile?.aliases.first?.meant == "Trovioo")
        await vocabulary.undoLastLearning()
        #expect(harness.learningFile == before)
        #expect(harness.learningFile?.aliases.isEmpty == true)
    }

    @Test("nothing learned and a repeated pending correction never claim a new saved word")
    func noFalseReceipt() async {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        let none = await vocabulary.observe(dictationID: "none", dictated: "We use it", edited: "We use it")
        #expect(none.undoToken == nil)
        #expect(vocabulary.learningNotice == nil)
        _ = await learn(vocabulary)
        let repeated = await learn(vocabulary)
        #expect(repeated.wordsLearned == 0 && repeated.aliasesLearned == 0)
        #expect(vocabulary.learningNotice == nil)
        #expect(vocabulary.undoToken == nil)
    }

    @Test("failed saves do not produce success feedback or an undo promise",
          .enabled(if: geteuid() != 0, "permissions do not bind root"))
    func failedSave() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        _ = await vocabulary.teach(written: "SkyDock", soundsLike: "")
        let folder = harness.learningURL.deletingLastPathComponent().path
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder) }
        let result = await learn(vocabulary)
        #expect(result.save == .failed)
        #expect(result.undoToken == nil)
        #expect(vocabulary.learningNotice == nil)
        #expect(vocabulary.unsavedNotice != nil)
        #expect(harness.learningFile?.words.map(\.text) == ["SkyDock"])
    }

    @Test("an undo whose save fails stays unsaved until flush and never reports saved success",
          .enabled(if: geteuid() != 0, "permissions do not bind root"))
    func failedUndoSave() async throws {
        let harness = Harness()
        let model = harness.model()
        _ = await learn(model.vocabulary)
        let folder = harness.learningURL.deletingLastPathComponent().path
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder) }
        await model.vocabulary.undoLastLearning()
        #expect(model.vocabulary.learningNotice == nil)
        #expect(model.vocabulary.unsaved)
        #expect(model.vocabulary.rows.isEmpty)
        #expect(harness.learningFile?.words.isEmpty == false)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder)
        await model.prepareForQuit()
        #expect(harness.learningFile == .empty)
    }
}
#endif
