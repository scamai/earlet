import XCTest
@testable import DictationLearning
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// The actor the app calls: loads on first use, writes only when the file
/// would change, reports counts (never words), never overwrites a file it
/// could not read, and replays what it learned meanwhile once it can.
final class DictationLearningFacadeTests: XCTestCase {

    private var dir: URL!
    private var url: URL!
    private let lexicon = WordListLexicon(words: ["we", "use", "the"])
    private let t0 = Date(timeIntervalSince1970: 1_790_000_000)

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dictation-facade-\(UUID().uuidString)", isDirectory: true)
        url = dir.appendingPathComponent("dictation/learning.json")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.deletingLastPathComponent().path)
        try? FileManager.default.removeItem(at: dir)
    }

    private func makeLearning(_ lexicon: (any LearningLexicon)? = nil) -> DictationLearning {
        DictationLearning(store: LearningStore(url: url), lexicon: lexicon ?? self.lexicon)
    }

    private func onDisk() -> LearningState? {
        FileManager.default.contents(atPath: url.path).flatMap(LearningStore.decode)
    }

    func testObserveLearnsReportsCountsAndWrites() async throws {
        let learning = makeLearning()
        let first = await learning.observe(dictationID: "d1", dictated: "we use trovio", edited: "we use Trovioo", at: t0)
        XCTAssertNotNil(first.undoToken)
        XCTAssertEqual(first, .init(candidates: 1, rejections: 0, wordsLearned: 1, aliasesLearned: 0,
                                    save: .saved, undoToken: first.undoToken, pendingCorrections: 1))
        XCTAssertEqual(onDisk()?.words.map(\.text), ["Trovioo"])

        let second = await learning.observe(dictationID: "d2", dictated: "we use trovio", edited: "we use Trovioo",
                                            at: t0.addingTimeInterval(1))
        XCTAssertNotNil(second.undoToken)
        XCTAssertNotEqual(second.undoToken, first.undoToken)
        XCTAssertEqual(second, .init(candidates: 1, rejections: 0, wordsLearned: 0, aliasesLearned: 1,
                                     save: .saved, undoToken: second.undoToken))
        XCTAssertEqual(onDisk()?.aliases.map { [$0.heard, $0.meant] }, [["trovio", "Trovioo"]])
        XCTAssertEqual(onDisk()?.aliases.first?.lastSeen, 1_790_000_001_000)

        let snapshot = await learning.snapshot()
        XCTAssertEqual(snapshot.words.map(\.text), ["Trovioo"])
        XCTAssertEqual(snapshot.aliases.map(\.heard), ["trovio"])
        XCTAssertEqual(snapshot.storage, .ok)
        XCTAssertTrue(snapshot.lexiconAvailable)
    }

    /// The outcome is what the app logs; it must carry no dictated text.
    func testOutOfOrderRevisionsCannotRestoreRetractedEvidence() async {
        let learning = makeLearning()
        _ = await learning.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use trovio", at: t0, revision: 2)
        let stale = await learning.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use Trovioo", at: t0, revision: 1)
        XCTAssertEqual(stale.save, .unchanged)
        XCTAssertNil(onDisk())
        _ = await learning.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use Trovioo", at: t0, revision: 3)
        _ = await learning.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use trovio", at: t0, revision: 2)
        XCTAssertEqual(onDisk()?.words.first?.count, 1)
        let reloaded = makeLearning()
        _ = await reloaded.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use Trovioo", at: t0)
        XCTAssertEqual(onDisk()?.words.first?.count, 1)
        XCTAssertTrue(onDisk()?.aliases.isEmpty == true)
    }

    func testCancelledCallerCannotWriteQueuedEvidence() async {
        let learning = makeLearning(), date = t0
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return await learning.observe(dictationID: "draft", dictated: "we use trovio", edited: "we use Trovioo", at: date)
        }
        let outcome = await task.value
        XCTAssertEqual(outcome.save, .unchanged)
        XCTAssertNil(onDisk())
    }

    func testTheOutcomeCarriesNoText() async {
        let outcome = await makeLearning().observe(dictationID: "d1", dictated: "we use trovio",
                                               edited: "we use Trovioo", at: t0)
        let described = String(describing: outcome)
        XCTAssertFalse(described.contains("rovio"), described)
    }

    func testNothingToLearnWritesNothing() async {
        let learning = makeLearning()
        let outcome = await learning.observe(dictationID: "d1", dictated: "we use it", edited: "we use it", at: t0)
        XCTAssertEqual(outcome.save, .unchanged)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path), "no file until there is something in it")
        let rejected = await learning.teach(written: "   ", soundsLike: nil, at: t0)
        XCTAssertEqual(rejected, .empty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }

    func testTeachRemoveAndClear() async throws {
        let learning = makeLearning()
        let taught = await learning.teach(written: "SkyDock", soundsLike: "sky dock", at: t0)
        XCTAssertEqual(taught, .ok)
        XCTAssertEqual(onDisk()?.aliases.map { [$0.heard, $0.meant, $0.origin.rawValue] }, [["sky dock", "SkyDock", "taught"]])

        let refused = await learning.teach(written: "Fable Notes", soundsLike: "notes", at: t0)
        XCTAssertEqual(refused, .selfContaining)

        let aliasRemoved = await learning.removeAlias(heard: "Sky  Dock", meant: "skydock", at: t0)
        XCTAssertEqual(aliasRemoved, .saved)
        XCTAssertEqual(onDisk()?.aliases, [])
        XCTAssertEqual(onDisk()?.words.map(\.text), ["SkyDock"])

        let wordRemoved = await learning.removeWord("skydock", at: t0)
        XCTAssertEqual(wordRemoved, .saved)
        XCTAssertEqual(onDisk()?.words, [])
        XCTAssertEqual(onDisk()?.removed.words.map(\.key), ["skydock"])

        let cleared = await learning.clear()
        XCTAssertEqual(cleared, .saved)
        XCTAssertEqual(onDisk(), .empty)
        let clearedAgain = await learning.clear()
        XCTAssertEqual(clearedAgain, .unchanged)
    }

    func testTheFileIsReadOnFirstUse() async throws {
        var state = LearningState.empty
        XCTAssertEqual(state.teach(written: "Trovioo", soundsLike: "trovio", at: 1), .ok)
        try LearningStore(url: url).save(state)

        let snapshot = await makeLearning().snapshot()
        XCTAssertEqual(snapshot.loadOutcome, .loaded)
        XCTAssertEqual(snapshot.words.map(\.text), ["Trovioo"])
        XCTAssertEqual(snapshot.aliases.map(\.heard), ["trovio"])
    }

    func testAnUndecodableFileIsKeptAsideAndLearningStartsEmpty() async throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("garbage".utf8).write(to: url)
        let learning = makeLearning()
        let snapshot = await learning.snapshot()
        XCTAssertEqual(snapshot.loadOutcome, .quarantined)
        XCTAssertEqual(snapshot.words, [])
        let taught = await learning.teach(written: "Trovioo", soundsLike: nil, at: t0)
        XCTAssertEqual(taught, .ok)
        XCTAssertEqual(onDisk()?.words.map(\.text), ["Trovioo"])
        XCTAssertEqual(try String(contentsOf: url.appendingPathExtension("bad"), encoding: .utf8), "garbage")
    }

    /// An undecodable file that cannot be moved aside is kept, and learning
    /// goes on in memory; once it can be moved aside, the changes made
    /// meanwhile are saved.
    func testAFileThatCannotBeMovedAsideIsLeftAloneUntilItCanBe() async throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
        let folder = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data("garbage".utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder.path)
        let learning = makeLearning()
        let snapshot = await learning.snapshot()
        XCTAssertEqual(snapshot.storage, .keepingUnreadableFile)
        XCTAssertEqual(snapshot.loadOutcome, .quarantineFailed)
        XCTAssertTrue(snapshot.isUnsaved)
        let taught = await learning.teach(written: "Trovioo", soundsLike: nil, at: t0)
        XCTAssertEqual(taught, .ok, "learning still works for this session")
        let removed = await learning.removeWord("Trovioo", at: t0)
        XCTAssertEqual(removed, .refused)
        let words = await learning.snapshot().words
        XCTAssertEqual(words, [])
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "garbage")

        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder.path)
        let later = await learning.teach(written: "Beta", soundsLike: nil, at: t0)
        XCTAssertEqual(later, .ok)
        XCTAssertEqual(try String(contentsOf: url.appendingPathExtension("bad"), encoding: .utf8), "garbage")
        XCTAssertEqual(onDisk()?.words.map(\.text), ["Beta"])
        XCTAssertEqual(onDisk()?.removed.words.map(\.key), ["trovioo"], "the removal made meanwhile was saved too")
        let recovered = await learning.snapshot()
        XCTAssertEqual(recovered.storage, .ok)
        XCTAssertFalse(recovered.isUnsaved)
    }

    // ─── A file that could not be read (LEARNING.md §5) ───

    private func plantReadable(_ build: (inout LearningState) -> Void) throws -> LearningState {
        var state = LearningState.empty
        build(&state)
        try LearningStore(url: url).save(state)
        return state
    }

    private func setMode(_ mode: Int) throws {
        try FileManager.default.setAttributes([.posixPermissions: mode], ofItemAtPath: url.path)
    }

    private func identity() throws -> [String] {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        return [attributes[.systemFileNumber], attributes[.size], attributes[.modificationDate]].map { "\($0 ?? "nil")" }
    }

    /// The file is never touched while it cannot be read; learning goes on in
    /// memory; the first change after the read succeeds replays everything
    /// made meanwhile onto the file's contents. The removal of a word that is
    /// only in the file proves it is a replay, not a merge of two lists.
    func testAnUnreadableFileIsNeverOverwrittenAndTheChangesMadeMeanwhileAreReplayed() async throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
        _ = try plantReadable { state in
            XCTAssertEqual(state.teach(written: "Kubectl", soundsLike: nil, at: 1), .ok)
            XCTAssertEqual(state.teach(written: "Jarvis", soundsLike: "javis", at: 2), .ok)
        }
        try setMode(0o000)
        defer { try? setMode(0o600) }
        let before = try identity()

        let learning = makeLearning()
        let first = await learning.snapshot()
        XCTAssertEqual(first.loadOutcome, .unreadable)
        XCTAssertEqual(first.storage, .keepingUnreadableFile)
        XCTAssertTrue(first.isUnsaved)
        XCTAssertEqual(first.words, [])

        let taught = await learning.teach(written: "Zupong", soundsLike: nil, at: t0)
        XCTAssertEqual(taught, .ok)
        let observed = await learning.observe(dictationID: "d1", dictated: "we use trovio", edited: "we use Trovioo",
                                              at: t0.addingTimeInterval(1))
        XCTAssertEqual(observed, .init(candidates: 1, rejections: 0, wordsLearned: 1, aliasesLearned: 0,
                                      save: .refused, pendingCorrections: 1))
        let removed = await learning.removeWord("Kubectl", at: t0.addingTimeInterval(2))
        XCTAssertEqual(removed, .refused)
        let meanwhile = await learning.snapshot()
        XCTAssertEqual(Set(meanwhile.words.map(\.text)), ["Zupong", "Trovioo"], "learning goes on in memory")
        XCTAssertEqual(meanwhile.storage, .keepingUnreadableFile)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.appendingPathExtension("bad").path))
        XCTAssertEqual(try identity(), before, "the unread file is the same file, untouched")

        try setMode(0o600)
        let later = await learning.teach(written: "Alpha", soundsLike: nil, at: t0.addingTimeInterval(3))
        XCTAssertEqual(later, .ok)
        let saved = try XCTUnwrap(onDisk())
        XCTAssertEqual(Set(saved.words.map(\.text)), ["Jarvis", "Zupong", "Trovioo", "Alpha"])
        XCTAssertEqual(saved.aliases.map { [$0.heard, $0.meant] }, [["javis", "Jarvis"]], "the file's alias is kept")
        XCTAssertEqual(saved.removed.words.map(\.key), ["kubectl"], "the removal was replayed onto the file")
        XCTAssertEqual(saved.pending.map(\.heard), ["trovio"])

        let after = await learning.snapshot()
        XCTAssertEqual(after.loadOutcome, .loaded)
        XCTAssertEqual(after.storage, .ok)
        XCTAssertEqual(Set(after.words.map(\.text)), ["Jarvis", "Zupong", "Trovioo", "Alpha"])
    }

    /// The window opening is enough: a snapshot retries the read too, and
    /// saves what it replayed.
    func testASnapshotRetriesTheReadAndSavesTheReplay() async throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
        _ = try plantReadable { XCTAssertEqual($0.teach(written: "Kubectl", soundsLike: nil, at: 1), .ok) }
        try setMode(0o000)
        defer { try? setMode(0o600) }
        let learning = makeLearning()
        _ = await learning.teach(written: "Zupong", soundsLike: nil, at: t0)

        try setMode(0o600)
        let snapshot = await learning.snapshot()
        XCTAssertEqual(Set(snapshot.words.map(\.text)), ["Kubectl", "Zupong"])
        XCTAssertEqual(snapshot.storage, .ok)
        XCTAssertEqual(Set(onDisk()?.words.map(\.text) ?? []), ["Kubectl", "Zupong"])
    }

    // ─── Quitting ───

    /// `flush()` is the last save at quit: it retries the read of a file that
    /// could not be read, and saves the replay.
    func testFlushSavesWhatWaitsForAnUnreadableFile() async throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
        _ = try plantReadable { XCTAssertEqual($0.teach(written: "Kubectl", soundsLike: nil, at: 1), .ok) }
        try setMode(0o000)
        defer { try? setMode(0o600) }
        let learning = makeLearning()
        _ = await learning.teach(written: "Zupong", soundsLike: nil, at: t0)
        let stillUnread = await learning.flush()
        XCTAssertEqual(stillUnread, .refused)

        try setMode(0o600)
        let flushed = await learning.flush()
        XCTAssertEqual(flushed, .saved)
        XCTAssertEqual(Set(onDisk()?.words.map(\.text) ?? []), ["Kubectl", "Zupong"])
        let again = await learning.flush()
        XCTAssertEqual(again, .unchanged)
    }

    /// `flush()` also retries a write the disk refused.
    func testFlushRetriesAFailedWrite() async throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
        let learning = makeLearning()
        _ = await learning.teach(written: "Alpha", soundsLike: nil, at: t0)
        let folder = url.deletingLastPathComponent().path
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder)
        let failed = await learning.teach(written: "Beta", soundsLike: nil, at: t0)
        XCTAssertEqual(failed, .ok)
        let unsaved = await learning.snapshot()
        XCTAssertEqual(unsaved.storage, .unsaved)
        XCTAssertTrue(unsaved.isUnsaved)

        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder)
        let flushed = await learning.flush()
        XCTAssertEqual(flushed, .saved)
        XCTAssertEqual(Set(onDisk()?.words.map(\.text) ?? []), ["Alpha", "Beta"])
        let storage = await learning.snapshot().storage
        XCTAssertEqual(storage, .ok)
    }

    /// Learning never used this run: quitting reads nothing, so it cannot
    /// move a file aside or create one.
    func testFlushWithoutUseReadsNothing() async throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("garbage".utf8).write(to: url)
        let flushed = await makeLearning().flush()
        XCTAssertEqual(flushed, .unchanged)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "garbage")
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.appendingPathExtension("bad").path))
    }

    /// Memory back in step with the disk after a failed write is saved, not
    /// "unsaved": a word added while the disk refused, then everything
    /// forgotten, leaves nothing to save.
    func testUndoingAnUnsavedChangeIsNoLongerUnsaved() async throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
        let parent = url.deletingLastPathComponent().deletingLastPathComponent()
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: parent.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: parent.path) }
        let learning = makeLearning()
        _ = await learning.teach(written: "Alpha", soundsLike: nil, at: t0)
        let failed = await learning.snapshot().storage
        XCTAssertEqual(failed, .unsaved)

        let cleared = await learning.clear()
        XCTAssertEqual(cleared, .unchanged)
        let storage = await learning.snapshot().storage
        XCTAssertEqual(storage, .ok)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path))
    }

    func testAFailedWriteIsReportedAndCarriedByTheNextOne() async throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
        let learning = makeLearning()
        _ = await learning.teach(written: "Alpha", soundsLike: nil, at: t0)
        let folder = url.deletingLastPathComponent().path
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder)
        let failed = await learning.removeWord("Alpha", at: t0)
        XCTAssertEqual(failed, .failed)
        let storage = await learning.snapshot().storage
        XCTAssertEqual(storage, .unsaved)
        XCTAssertEqual(onDisk()?.words.map(\.text), ["Alpha"], "the old file is whole")

        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder)
        _ = await learning.teach(written: "Beta", soundsLike: nil, at: t0)
        XCTAssertEqual(onDisk()?.words.map(\.text), ["Beta"], "the removal reached the file with the next write")
        let recovered = await learning.snapshot().storage
        XCTAssertEqual(recovered, .ok)
    }

    func testWithoutAWordListOnlyTeachingWorks() async {
        let learning = makeLearning(WordListLexicon(words: []))
        let outcome = await learning.observe(dictationID: "d1", dictated: "we use trovio", edited: "we use Trovioo", at: t0)
        XCTAssertEqual(outcome.wordsLearned, 0)
        let taught = await learning.teach(written: "Trovioo", soundsLike: "trovio", at: t0)
        XCTAssertEqual(taught, .ok)
        let snapshot = await learning.snapshot()
        XCTAssertFalse(snapshot.lexiconAvailable)
        XCTAssertEqual(snapshot.words.map(\.origin), [.taught])
    }

    func testTimesAreUnixMilliseconds() {
        XCTAssertEqual(DictationLearning.milliseconds(Date(timeIntervalSince1970: 1_790_000_000.5)), 1_790_000_000_500)
        XCTAssertEqual(DictationLearning.milliseconds(Date(timeIntervalSince1970: 0)), 0)
    }
}
