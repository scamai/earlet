import XCTest
@testable import DictationKit

/// Temporary vocabulary fixtures exercise preservation and destructive
/// failure paths without writing to a user's vocabulary.
final class DictationVocabularyWriterTests: XCTestCase {

    private var dir: URL!
    private var url: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("fable-vocab-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        url = dir.appendingPathComponent("bias_vocabulary.json")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func phrases() -> [String] {
        DictationBiasVocabularyLoader.load(from: url).phrases
    }

    // ─── The user's own terms ───

    func testMergeKeepsEveryTermTheUserAlreadyHad() throws {
        try DictationVocabularyWriter.overwrite(
            DictationBiasVocabulary(phrases: ["ArcFace", "Trovioo", "Knemo"]), at: url)
        let result = try DictationVocabularyWriter.merge(adding: ["PadForge", "QNN"], at: url)

        XCTAssertEqual(phrases(), ["ArcFace", "Trovioo", "Knemo", "PadForge", "QNN"])
        XCTAssertEqual(result.added, ["PadForge", "QNN"])
        XCTAssertEqual(result.total, 5)
    }

    /// `BiasTrie` assigns phrase IDs by position. Preserving the existing
    /// order keeps IDs stable for callers.
    func testExistingTermsKeepTheirPositions() throws {
        try DictationVocabularyWriter.overwrite(
            DictationBiasVocabulary(phrases: ["Zupong", "ArcFace"]), at: url)
        try DictationVocabularyWriter.merge(adding: ["Miles"], at: url)
        XCTAssertEqual(phrases(), ["Zupong", "ArcFace", "Miles"])
    }

    /// Two entries differing only in case would spend two slots against the
    /// 1000-phrase ceiling and hand the trie two ids for one word.
    func testACaseVariantIsNotAddedAsASecondEntry() throws {
        try DictationVocabularyWriter.overwrite(
            DictationBiasVocabulary(phrases: ["ArcFace"]), at: url)
        let result = try DictationVocabularyWriter.merge(adding: ["arcface", "ARCFACE"], at: url)

        XCTAssertEqual(phrases(), ["ArcFace"], "declared form must win")
        XCTAssertEqual(result.added, [])
        XCTAssertFalse(result.wrote, "a no-op must not report work it did not do")
    }

    func testDuplicatesWithinOneMergeCollapse() throws {
        let result = try DictationVocabularyWriter.merge(
            adding: ["Soliton", "soliton", "  Soliton  ", ""], at: url)
        XCTAssertEqual(result.added, ["Soliton"])
        XCTAssertEqual(phrases(), ["Soliton"])
    }

    // ─── The ceiling, which is a false-positive bound, not a perf number ───

    func testItStopsAtTheMeasuredCeilingAndSaysSo() throws {
        let many = (0..<1200).map { "Term\($0)" }
        let result = try DictationVocabularyWriter.merge(adding: many, at: url)

        XCTAssertEqual(result.total, DictationBiasVocabulary.maxPhrases)
        XCTAssertEqual(result.dropped, 200)
        XCTAssertEqual(result.added.count, DictationBiasVocabulary.maxPhrases,
                       "added must report what survived, not what was offered")
    }

    // ─── Snapshot and restore, which is what keeps a rehearsal non-destructive ───

    func testARehearsalCanBeUndoneExactly() throws {
        let original = DictationBiasVocabulary(phrases: ["ArcFace", "Trovioo", "Fable"])
        try DictationVocabularyWriter.overwrite(original, at: url)

        let before = DictationVocabularyWriter.snapshot(at: url)
        try DictationVocabularyWriter.merge(adding: ["SignalStack", "QuestPDF"], at: url)
        XCTAssertEqual(phrases().count, 5)

        try DictationVocabularyWriter.overwrite(before, at: url)
        XCTAssertEqual(phrases(), original.phrases)
    }

    func testSnapshottingAnAbsentFileRestoresToAbsentBehaviour() throws {
        let before = DictationVocabularyWriter.snapshot(at: url)
        XCTAssertTrue(before.isEmpty)
        try DictationVocabularyWriter.merge(adding: ["Knemo"], at: url)
        try DictationVocabularyWriter.overwrite(before, at: url)
        // An empty file and a missing file read identically, which is what the
        // recogniser actually consumes.
        XCTAssertEqual(phrases(), [])
    }

    // ─── Durability, because the reader's failure mode is silent ───

    /// A torn write reads back as an EMPTY vocabulary, not as an error — every
    /// private term gone with nothing logged. So the write must land whole or
    /// not at all, and the previous content must survive a failed attempt.
    func testAWriteLeavesNoPartialFileBehind() throws {
        try DictationVocabularyWriter.merge(adding: ["ArcFace", "Trovioo"], at: url)
        let strays = try FileManager.default.contentsOfDirectory(atPath: dir.path)
            .filter { $0.hasSuffix(".tmp") }
        XCTAssertEqual(strays, [], "temp file survived the replace")
    }

    func testTheFileItWritesIsTheFileTheLoaderReads() throws {
        try DictationVocabularyWriter.merge(adding: ["Cove.ai", "Terminal"], at: url)
        // Round-trips through the real loader, not through a private decoder —
        // a writer whose format only its own tests can read is not wired to
        // anything.
        XCTAssertEqual(phrases(), ["Cove.ai", "Terminal"])
    }

    func testItCreatesTheDirectoryOnFirstRun() throws {
        let nested = dir.appendingPathComponent("Earlet/dictation/bias_vocabulary.json")
        try DictationVocabularyWriter.merge(adding: ["Jasper"], at: nested)
        XCTAssertEqual(DictationBiasVocabularyLoader.load(from: nested).phrases, ["Jasper"])
    }

    func testMergingIntoAMalformedFileDoesNotThrowAway() throws {
        // The loader reports malformed as empty, so a merge here can only add.
        // The point of the test is that it does not crash and does not leave
        // the file malformed for the recogniser.
        try Data("{ not json".utf8).write(to: url)
        try DictationVocabularyWriter.merge(adding: ["Wavelet"], at: url)
        XCTAssertEqual(phrases(), ["Wavelet"])
    }
}

// ─── The guard that would have prevented a real data loss ───

extension DictationVocabularyWriterTests {

    /// An implicit destination can bypass a test's temporary source paths
    /// and overwrite a user's vocabulary. The writer must refuse that route.
    func testAWriteWithNoExplicitDestinationIsRefusedUnderTest() {
        XCTAssertThrowsError(try DictationVocabularyWriter.overwrite(
            DictationBiasVocabulary(phrases: ["ShouldNeverBeWritten"]))) { error in
            XCTAssertEqual(error as? DictationVocabularyWriter.Failure, .refusedUnderTest)
        }
        XCTAssertThrowsError(try DictationVocabularyWriter.merge(
            adding: ["ShouldNeverBeWritten"])) { error in
            XCTAssertEqual(error as? DictationVocabularyWriter.Failure, .refusedUnderTest)
        }
    }

    /// The guard must not break the legitimate case, or it will be removed.
    func testAnExplicitDestinationStillWorksUnderTest() throws {
        try DictationVocabularyWriter.merge(adding: ["Trovioo"], at: url)
        XCTAssertEqual(phrases(), ["Trovioo"])
    }

    /// Proof the user's real file is untouched by a full run of this suite.
    func testTheUsersRealVocabularyIsNeverTheTargetOfThisSuite() throws {
        let real = try XCTUnwrap(DictationBiasVocabularyLoader.defaultURL())
        let before = try? Data(contentsOf: real)
        _ = try? DictationVocabularyWriter.merge(adding: ["Canary"])
        _ = try? DictationVocabularyWriter.overwrite(DictationBiasVocabulary(phrases: ["Canary"]))
        let after = try? Data(contentsOf: real)
        XCTAssertEqual(before, after, "this suite wrote the user's real vocabulary")
    }
}
