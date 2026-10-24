// The Vocabulary section's model: teaching, the refusal sentences, removing
// one entry, Forget all, and the exact copy both platforms show. The learning
// actor and its file are real, in a temporary directory.

#if canImport(Testing)
import DictationLearning
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("The Vocabulary section")
struct VocabularyTests {

    private func vocabulary() -> (Harness, DictationVocabularyModel) {
        let harness = Harness()
        return (harness, harness.model().vocabulary)
    }

    @Test("the copy is the shared wording, word for word")
    func copyIsTheSharedWording() {
        typealias Copy = DictationVocabularyCopy
        #expect(Copy.heading == "Vocabulary")
        #expect(Copy.learnSwitch == "Learn from my corrections")
        #expect(Copy.disclosure == "In supported text fields, Earlet checks your corrections for up to 90 seconds. Useful words are remembered on this computer for future dictations; learning keeps words, not whole sentences.")
        #expect(Copy.wordPlaceholder == "Word or name")
        #expect(Copy.soundsLikePlaceholder == "Sounds like (optional)")
        #expect(Copy.add == "Add")
        #expect(Copy.empty == "No words yet. Fix a word Earlet got wrong, or add one above.")
        #expect(Copy.learnedFromFix == "Learned from your fix")
        #expect(Copy.addedByYou == "Added by you")
        #expect(Copy.remove == "Remove")
        #expect(Copy.forgetAll == "Forget all")
        #expect(Copy.forgetAllQuestion == "Forget every learned and added word? Earlet will stop using them.")
        #expect(Copy.cancel == "Cancel")
        #expect(Copy.unsaved == "Your vocabulary isn't saved yet. Earlet keeps using it and will try again.")
        #expect(Copy.alias(heard: "trovio", meant: "Trovioo") == "trovio → Trovioo")
    }

    @Test("every refusal has its sentence, and a word that was taught has none")
    func refusalSentences() {
        #expect(DictationVocabularyCopy.refusal(.ok) == nil)
        #expect(DictationVocabularyCopy.refusal(.empty) == "Type a word first.")
        #expect(DictationVocabularyCopy.refusal(.tooLong) == "That is too long. Use up to 60 characters.")
        #expect(DictationVocabularyCopy.refusal(.selfContaining) == "“Sounds like” cannot be part of the word itself.")
        #expect(DictationVocabularyCopy.refusal(.full) == "The list is full. Remove a word first.")
    }

    @Test("an empty vocabulary lists nothing")
    func emptyList() async {
        let (harness, vocabulary) = vocabulary()
        await vocabulary.refresh()
        #expect(vocabulary.rows.isEmpty)
        #expect(vocabulary.teachMessage == nil)
        #expect(!vocabulary.unsaved)
        #expect(vocabulary.unsavedNotice == nil)
        #expect(harness.learningFile == nil, "reading the list writes nothing")
    }

    @Test("a word whose origin a newer version wrote is listed without a claim about where it came from")
    func unknownOriginHasNoDetail() async throws {
        let harness = Harness()
        try LearningStore(url: harness.learningURL).save(LearningState(words: [
            LearnedWord(text: "Kubectl", origin: .other("imported"), count: 0, firstSeen: 1, lastSeen: 1),
            LearnedWord(text: "Trovioo", origin: .correction, count: 1, firstSeen: 1, lastSeen: 1),
        ]))
        let vocabulary = harness.model().vocabulary
        await vocabulary.refresh()
        #expect(vocabulary.rows.map(\.title) == ["Kubectl", "Trovioo"])
        #expect(vocabulary.rows.map(\.detail) == [nil, "Learned from your fix"])
    }

    @Test("teaching a word and how it sounds lists both, marked as added by you")
    func teachWordAndAlias() async {
        let (harness, vocabulary) = vocabulary()
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: "sky dock"))
        #expect(vocabulary.teachMessage == "Saved to your vocabulary on this Mac.")
        #expect(vocabulary.rows.map(\.title) == ["SkyDock", "sky dock → SkyDock"])
        #expect(vocabulary.rows.map(\.detail) == ["Added by you", nil])
        #expect(harness.learningFile?.words.map(\.origin) == [.taught])
        #expect(harness.learningFile?.aliases.map(\.origin) == [.taught])
    }

    @Test("a blank sounds-like field means no sounds-like, not one that is too short")
    func blankSoundsLike() async {
        let (harness, vocabulary) = vocabulary()
        #expect(await vocabulary.teach(written: "Trovioo", soundsLike: "   "))
        #expect(vocabulary.rows.map(\.title) == ["Trovioo"])
        #expect(harness.learningFile?.aliases.isEmpty == true)
    }

    @Test("each refusal is explained, keeps the list as it was, and a later success clears it")
    func refusalsAreExplained() async {
        let (_, vocabulary) = vocabulary()
        let cases: [(String, String, String)] = [
            ("   ", "", "Type a word first."),
            (String(repeating: "x", count: 61), "", "That is too long. Use up to 60 characters."),
            ("Fable Notes", "notes", "“Sounds like” cannot be part of the word itself."),
        ]
        for (written, soundsLike, sentence) in cases {
            #expect(await vocabulary.teach(written: written, soundsLike: soundsLike) == false, "\(written)")
            #expect(vocabulary.teachMessage == sentence)
            #expect(vocabulary.rows.isEmpty)
        }
        #expect(await vocabulary.teach(written: "Trovioo", soundsLike: ""))
        #expect(vocabulary.teachMessage == "Saved to your vocabulary on this Mac.")
    }

    @Test("a full list says so")
    func fullList() async throws {
        let harness = Harness()
        // 500 taught words already on disk: the cap.
        let words = (0..<500).map {
            LearnedWord(text: "Word\($0)x", origin: .taught, count: 0, firstSeen: 1, lastSeen: 1)
        }
        try LearningStore(url: harness.learningURL).save(LearningState(words: words))
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "Trovioo", soundsLike: "") == false)
        #expect(vocabulary.teachMessage == "The list is full. Remove a word first.")
        #expect(vocabulary.rows.count == 500)
    }

    @Test("removing a word removes the aliases that write it")
    func removeWord() async throws {
        let (harness, vocabulary) = vocabulary()
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: "sky dock"))
        let word = try #require(vocabulary.rows.first)
        await vocabulary.remove(word)
        #expect(vocabulary.rows.isEmpty)
        #expect(harness.learningFile?.words.isEmpty == true)
        #expect(harness.learningFile?.aliases.isEmpty == true)
    }

    @Test("removing an alias keeps its word")
    func removeAlias() async throws {
        let (harness, vocabulary) = vocabulary()
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: "sky dock"))
        let alias = try #require(vocabulary.rows.last)
        await vocabulary.remove(alias)
        #expect(vocabulary.rows.map(\.title) == ["SkyDock"])
        #expect(harness.learningFile?.aliases.isEmpty == true)
        #expect(harness.learningFile?.removed.aliases.map(\.heard) == ["sky dock"])
    }

    @Test("Forget all empties the list and the file, removed entries included")
    func forgetAll() async {
        let (harness, vocabulary) = vocabulary()
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: "sky dock"))
        #expect(await vocabulary.teach(written: "Trovioo", soundsLike: ""))
        await vocabulary.remove(vocabulary.rows[0])
        #expect(harness.learningFile?.removed.words.isEmpty == false)
        await vocabulary.forgetAll()
        #expect(vocabulary.rows.isEmpty)
        #expect(harness.learningFile == LearningState.empty)
    }

    // ─── Not saved yet ───

    /// The case the line exists for: Forget all shown as done while the disk
    /// refused it. Without the line and the last save, the next launch would
    /// bring every word back.
    @Test("Forget all that could not be saved says so, and the last save at quit makes it stick",
          .enabled(if: geteuid() != 0, "permission checks do not bind root"))
    func unsavedForgetAllIsShownAndSavedAtQuit() async throws {
        let harness = Harness()
        let model = harness.model()
        let vocabulary = model.vocabulary
        #expect(await vocabulary.teach(written: "Trovioo", soundsLike: "trovio"))
        #expect(vocabulary.unsavedNotice == nil)
        let folder = harness.learningURL.deletingLastPathComponent().path
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder) }

        await vocabulary.forgetAll()
        #expect(vocabulary.rows.isEmpty)
        #expect(vocabulary.unsaved)
        #expect(vocabulary.unsavedNotice
                == "Your vocabulary isn't saved yet. Earlet keeps using it and will try again.")
        #expect(harness.learningFile?.words.map(\.text) == ["Trovioo"], "the file still holds it")

        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder)
        await model.prepareForQuit()
        #expect(harness.learningFile == LearningState.empty, "the last save at quit wrote the removal")
        #expect(harness.logLines.get.contains("learning: last save at quit (saved)"))
        let relaunched = DictationLearning(store: LearningStore(url: harness.learningURL), lexicon: testLexicon)
        #expect(await relaunched.snapshot().words.isEmpty, "a relaunch does not bring the word back")
        await vocabulary.refresh()
        #expect(!vocabulary.unsaved)
        #expect(vocabulary.unsavedNotice == nil)
    }

    @Test("a learning.json that cannot be read shows the line until it can, and is never overwritten",
          .enabled(if: geteuid() != 0, "permission checks do not bind root"))
    func unreadableFileIsShownUnsaved() async throws {
        let harness = Harness()
        let path = harness.learningURL.path
        try LearningStore(url: harness.learningURL).save(LearningState(words: [
            LearnedWord(text: "Kubectl", origin: .taught, count: 0, firstSeen: 1, lastSeen: 1),
        ]))
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path) }

        let vocabulary = harness.model().vocabulary
        await vocabulary.refresh()
        #expect(vocabulary.unsavedNotice == DictationVocabularyCopy.unsaved)
        #expect(await vocabulary.teach(written: "Zupong", soundsLike: ""))
        #expect(vocabulary.rows.map(\.title) == ["Zupong"], "learning goes on in memory")
        #expect(vocabulary.unsaved)

        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: path)
        await vocabulary.refresh()
        #expect(!vocabulary.unsaved)
        #expect(Set(vocabulary.rows.map(\.title)) == ["Kubectl", "Zupong"])
        #expect(Set(harness.learningFile?.words.map(\.text) ?? []) == ["Kubectl", "Zupong"])
    }

    @Test("teaching, removing and forgetting log outcomes, never words")
    func logsHoldNoWords() async {
        let (harness, vocabulary) = vocabulary()
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: "sky dock"))
        #expect(await vocabulary.teach(written: "Fable Notes", soundsLike: "notes") == false)
        await vocabulary.remove(vocabulary.rows[0])
        await vocabulary.forgetAll()
        let lines = harness.logLines.get
        #expect(lines.contains("learning: teach ok"))
        #expect(lines.contains("learning: teach self_containing"))
        #expect(lines.contains { $0.hasPrefix("learning: removed a word") })
        #expect(lines.contains { $0.hasPrefix("learning: forgot everything") })
        for line in lines {
            for word in ["SkyDock", "sky dock", "Fable Notes", "notes"] {
                #expect(!line.contains(word), "\(line)")
            }
        }
    }
}
#endif
