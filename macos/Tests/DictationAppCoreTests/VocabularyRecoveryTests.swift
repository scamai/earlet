#if canImport(Testing)
import DictationLearning
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Vocabulary management recovery")
struct VocabularyRecoveryTests {
    private func replaceFileWithDirectory(_ harness: Harness) throws {
        try? FileManager.default.removeItem(at: harness.learningURL)
        try FileManager.default.createDirectory(at: harness.learningURL, withIntermediateDirectories: false)
    }

    private func restoreFile(_ harness: Harness) throws {
        try FileManager.default.removeItem(at: harness.learningURL)
    }

    @Test("a successful removal gives a generic persisted receipt")
    func removeSuccess() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: ""))
        await vocabulary.remove(try #require(vocabulary.rows.first))
        #expect(vocabulary.managementNotice == "Removed from your vocabulary.")
        #expect(vocabulary.unsavedNotice == nil)
        #expect(!harness.logLines.get.joined(separator: "\n").contains("SkyDock"))
    }

    @Test("a failed removal remains visibly recoverable and retry saves it")
    func removeFailureAndRetry() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: ""))
        try replaceFileWithDirectory(harness)
        await vocabulary.remove(try #require(vocabulary.rows.first))
        #expect(vocabulary.managementNotice?.contains("isn't saved") == true)
        #expect(vocabulary.unsaved)

        await vocabulary.retrySaving()
        #expect(!vocabulary.retryingSave)
        #expect(vocabulary.unsaved)
        #expect(vocabulary.managementNotice?.contains("isn't saved") == true)

        try restoreFile(harness)
        await vocabulary.retrySaving()
        #expect(vocabulary.managementNotice == "Changes saved to your vocabulary on this Mac.")
        #expect(!vocabulary.unsaved)
        #expect(harness.learningFile?.words.isEmpty == true)
        #expect(harness.learningFile?.removed.words.map(\.key) == ["skydock"])
    }

    @Test("a failed Forget all remains visibly recoverable and retry clears the file")
    func clearFailureAndRetry() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: ""))
        try replaceFileWithDirectory(harness)
        await vocabulary.forgetAll()
        #expect(vocabulary.managementNotice?.contains("isn't saved") == true)
        #expect(vocabulary.unsaved)
        #expect(vocabulary.teachMessage == nil)

        await vocabulary.retrySaving()
        #expect(!vocabulary.retryingSave)
        #expect(vocabulary.unsaved)
        #expect(vocabulary.managementNotice?.contains("isn't saved") == true)

        try restoreFile(harness)
        await vocabulary.retrySaving()
        #expect(vocabulary.managementNotice == "Changes saved to your vocabulary on this Mac.")
        #expect(!vocabulary.unsaved)
        #expect(harness.learningFile == .empty)
    }

    @Test("Forget all success has no word content in its receipt or log")
    func clearSuccessIsPrivate() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: ""))
        await vocabulary.forgetAll()
        #expect(vocabulary.managementNotice == "Your vocabulary is empty.")
        let output = ([vocabulary.managementNotice] + harness.logLines.get).compactMap { $0 }.joined(separator: "\n")
        #expect(!output.contains("SkyDock"))
    }

    @Test("a newer observation expires a management receipt while preserving undo")
    func newerObservationExpiresManagementReceipt() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: ""))
        await vocabulary.forgetAll()
        #expect(vocabulary.managementNotice != nil)
        let result = await vocabulary.observe(dictationID: "one", dictated: "We use trovio", edited: "We use Trovioo")
        #expect(result.undoToken != nil)
        #expect(vocabulary.managementNotice == nil)
        #expect(vocabulary.learningNotice != nil)
        await vocabulary.undoLastLearning()
        #expect(vocabulary.rows.isEmpty)
    }

    @Test("an unchanged observation preserves the last management receipt")
    func unchangedObservationPreservesManagementReceipt() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: ""))
        await vocabulary.forgetAll()
        let receipt = try #require(vocabulary.managementNotice)
        let result = await vocabulary.observe(dictationID: "unchanged", dictated: "same", edited: "same")
        #expect(result.wordsLearned == 0 && result.aliasesLearned == 0)
        #expect(vocabulary.managementNotice == receipt)
    }
}
#endif
