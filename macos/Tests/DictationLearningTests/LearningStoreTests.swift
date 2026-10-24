import XCTest
@testable import DictationLearning
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// `learning.json` on disk (LEARNING.md §5): the exact schema, tolerant
/// reading within it, atomic replacement, private permissions, that an
/// undecodable file is kept aside, and that a file that could not be read is
/// left in place and never overwritten.
final class LearningStoreTests: XCTestCase {

    private var dir: URL!
    private var url: URL!
    private var store: LearningStore!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dictation-store-\(UUID().uuidString)", isDirectory: true)
        url = dir.appendingPathComponent("dictation/learning.json")
        store = LearningStore(url: url)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.deletingLastPathComponent().path)
        try? FileManager.default.removeItem(at: dir)
    }

    private let t0: Int64 = 1_790_000_000_000

    private func sample() -> LearningState {
        LearningState(
            words: [LearnedWord(text: "Trovioo", origin: .correction, count: 2, firstSeen: t0, lastSeen: t0 + 1000)],
            aliases: [LearnedAlias(heard: "trovio", meant: "Trovioo", origin: .taught, count: 0,
                                   firstSeen: t0, lastSeen: t0 + 1000)],
            pending: [PendingAlias(heard: "javis", meant: "Jarvis", count: 1, dictations: ["id-1"],
                                   firstSeen: t0, lastSeen: t0)],
            removed: RemovedEntries(words: [RemovedWord(key: "sol", at: t0)],
                                    aliases: [RemovedAlias(heard: "so", meant: "sol", at: t0)]))
    }

    private func mode(_ path: String) throws -> Int {
        let attributes = try FileManager.default.attributesOfItem(atPath: path)
        return (attributes[.posixPermissions] as? NSNumber)?.intValue ?? -1
    }

    private func skipIfRoot() throws {
        try XCTSkipIf(geteuid() == 0, "permission checks do not bind root")
    }

    // ─── Schema ───

    func testTheFileIsTheExactSnakeCaseSchemaWithIntegerMilliseconds() throws {
        try store.save(sample())
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        XCTAssertEqual(Set(json.keys), ["schema", "words", "aliases", "pending", "removed"])
        XCTAssertEqual(json["schema"] as? String, "dictation.learning/1")

        let word = try XCTUnwrap((json["words"] as? [[String: Any]])?.first)
        XCTAssertEqual(Set(word.keys), ["text", "origin", "count", "first_seen", "last_seen"])
        XCTAssertEqual(word["origin"] as? String, "correction")
        XCTAssertEqual((word["last_seen"] as? NSNumber)?.int64Value, t0 + 1000)
        XCTAssertFalse(String(data: try Data(contentsOf: url), encoding: .utf8)!.contains("1790000001000.0"),
                       "times are integers, not floats")

        let alias = try XCTUnwrap((json["aliases"] as? [[String: Any]])?.first)
        XCTAssertEqual(Set(alias.keys), ["heard", "meant", "origin", "count", "first_seen", "last_seen"])
        let pending = try XCTUnwrap((json["pending"] as? [[String: Any]])?.first)
        XCTAssertEqual(Set(pending.keys), ["heard", "meant", "count", "dictations", "first_seen", "last_seen"])
        let removed = try XCTUnwrap(json["removed"] as? [String: Any])
        XCTAssertEqual(Set(removed.keys), ["words", "aliases"])
        XCTAssertEqual(Set(try XCTUnwrap((removed["words"] as? [[String: Any]])?.first).keys), ["key", "at"])
        XCTAssertEqual(Set(try XCTUnwrap((removed["aliases"] as? [[String: Any]])?.first).keys), ["heard", "meant", "at"])
    }

    /// The file LEARNING.md §5 shows, byte for byte as a reader would write it.
    func testTheDocumentedExampleLoads() throws {
        let example = """
        {"schema": "dictation.learning/1",
         "words": [{"text": "Trovioo", "origin": "correction", "count": 2, "first_seen": 1790000000000, "last_seen": 1790000001000}],
         "aliases": [{"heard": "trovio", "meant": "Trovioo", "origin": "correction", "count": 2, "first_seen": 1790000000000, "last_seen": 1790000001000}],
         "pending": [{"heard": "javis", "meant": "Jarvis", "count": 1, "dictations": ["<random id>"], "first_seen": 1790000000000, "last_seen": 1790000000000}],
         "removed": {"words": [{"key": "sol", "at": 1790000000000}], "aliases": [{"heard": "so", "meant": "sol", "at": 1790000000000}]}}
        """
        let state = try XCTUnwrap(LearningStore.decode(Data(example.utf8)))
        XCTAssertEqual(state.words.first?.lastSeen, 1_790_000_001_000)
        XCTAssertEqual(state.pending.first?.dictations, ["<random id>"])
        XCTAssertEqual(state.removed.aliases.first?.meant, "sol")
    }

    /// Every golden end state (tests/fixtures/learning-golden.json) written
    /// through the store and read back is the same state, and still the
    /// golden projection.
    func testEveryGoldenSequenceSurvivesTheStore() throws {
        let golden = try Golden.load()
        XCTAssertGreaterThanOrEqual(golden.sequences.count, 20)
        for (index, sequence) in golden.sequences.enumerated() {
            let (state, _) = try golden.replay(sequence)
            let store = LearningStore(url: dir.appendingPathComponent("golden-\(index)/learning.json"))
            try store.save(state)
            let loaded = store.load()
            XCTAssertEqual(loaded.outcome, .loaded, sequence.name)
            XCTAssertEqual(loaded.state, state, sequence.name)
            XCTAssertEqual(loaded.state.schema, "dictation.learning/1", sequence.name)
            XCTAssertEqual(loaded.state.projection(), state.projection(), sequence.name)
            XCTAssertEqual(loaded.state.projection(), sequence.expect.projection, sequence.name)
        }
    }

    func testRoundTrip() throws {
        try store.save(sample())
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .loaded)
        XCTAssertEqual(loaded.state, sample())
    }

    func testAMissingFileLoadsEmptyAndCreatesNothing() {
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .missing)
        XCTAssertEqual(loaded.state, .empty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.path))
    }

    // ─── Tolerant reading ───

    private func plant(_ bytes: String) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(bytes.utf8).write(to: url)
    }

    private func assertKeptAside(_ bytes: String, file: StaticString = #filePath, line: UInt = #line) throws {
        try plant(bytes)
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .quarantined, file: file, line: line)
        XCTAssertEqual(loaded.state, .empty, file: file, line: line)
        XCTAssertFalse(FileManager.default.fileExists(atPath: url.path), file: file, line: line)
        XCTAssertEqual(try String(contentsOf: store.quarantineURL, encoding: .utf8), bytes, file: file, line: line)
        XCTAssertEqual(store.quarantineURL.lastPathComponent, "learning.json.bad", file: file, line: line)

        // Learning starts again, and the kept file survives the next write.
        try store.save(sample())
        XCTAssertEqual(store.load().state, sample(), file: file, line: line)
        XCTAssertEqual(try String(contentsOf: store.quarantineURL, encoding: .utf8), bytes, file: file, line: line)
    }

    /// A missing list is empty, as the worker and the Windows port read it;
    /// the file is loaded, not set aside.
    func testMissingListsReadAsEmpty() throws {
        try plant(#"{"schema": "dictation.learning/1", "words": [], "removed": {"aliases": []}}"#)
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .loaded)
        XCTAssertEqual(loaded.state, .empty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.quarantineURL.path))

        try plant(#"{"schema": "dictation.learning/1", "pending": [{"heard": "javis", "meant": "Jarvis", "count": 1, "dictations": ["d1"], "first_seen": 1, "last_seen": 1}]}"#)
        let partial = store.load()
        XCTAssertEqual(partial.outcome, .loaded)
        XCTAssertEqual(partial.state.pending.map(\.heard), ["javis"])
        XCTAssertEqual(partial.state.words, [])
        XCTAssertEqual(partial.state.removed, RemovedEntries())
    }

    /// An origin a newer build wrote is kept as it is and written back
    /// verbatim; LearningRulesTests shows it is never evicted nor re-cased.
    func testAnUnknownOriginIsKeptAndWrittenBack() throws {
        try plant(#"{"schema": "dictation.learning/1", "words": [{"text": "x", "origin": "guess", "count": 1, "first_seen": 1, "last_seen": 1}], "aliases": [{"heard": "ex", "meant": "x", "origin": "imported", "count": 1, "first_seen": 1, "last_seen": 1}], "pending": [], "removed": {"words": [], "aliases": []}}"#)
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .loaded)
        XCTAssertEqual(loaded.state.words.map(\.origin), [.other("guess")])
        XCTAssertEqual(loaded.state.aliases.map(\.origin.rawValue), ["imported"])

        try store.save(loaded.state)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        XCTAssertEqual((json["words"] as? [[String: Any]])?.first?["origin"] as? String, "guess")
        XCTAssertEqual((json["aliases"] as? [[String: Any]])?.first?["origin"] as? String, "imported")
        XCTAssertEqual(LearningOrigin(rawValue: "taught"), .taught)
        XCTAssertEqual(LearningOrigin(rawValue: "correction"), .correction)
    }

    /// Keys this version does not know, at every level, survive a load and a
    /// save with their values; integers stay exact integers.
    func testUnknownKeysAreWrittenBackUnchanged() throws {
        try plant(#"""
        {"schema": "dictation.learning/1", "note": {"by": "a newer build", "n": [1, 2.5, true, null]},
         "big": 9007199254740993,
         "words": [{"text": "Trovioo", "origin": "correction", "count": 2, "first_seen": 1, "last_seen": 2, "source": "import"}],
         "aliases": [{"heard": "trovio", "meant": "Trovioo", "origin": "taught", "count": 0, "first_seen": 1, "last_seen": 2, "flag": false}],
         "pending": [{"heard": "javis", "meant": "Jarvis", "count": 1, "dictations": ["d1"], "first_seen": 1, "last_seen": 1, "weight": 0.25}],
         "removed": {"words": [{"key": "sol", "at": 1, "why": null}], "aliases": [{"heard": "so", "meant": "sol", "at": 1, "n": -3}], "since": "2026"}}
        """#)
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .loaded)
        XCTAssertEqual(loaded.state.extra["big"], .integer(9_007_199_254_740_993))
        XCTAssertEqual(loaded.state.words.first?.extra, ["source": .string("import")])

        try store.save(loaded.state)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        let note = try XCTUnwrap(json["note"] as? [String: Any])
        XCTAssertEqual(note["by"] as? String, "a newer build")
        XCTAssertEqual((note["n"] as? [Any])?.count, 4)
        XCTAssertTrue(String(data: try Data(contentsOf: url), encoding: .utf8)!.contains("9007199254740993"),
                      "an integer past 2^53 is written back exactly")
        XCTAssertEqual((json["words"] as? [[String: Any]])?.first?["source"] as? String, "import")
        XCTAssertEqual((json["aliases"] as? [[String: Any]])?.first?["flag"] as? Bool, false)
        XCTAssertEqual(((json["pending"] as? [[String: Any]])?.first?["weight"] as? NSNumber)?.doubleValue, 0.25)
        let removed = try XCTUnwrap(json["removed"] as? [String: Any])
        XCTAssertEqual(removed["since"] as? String, "2026")
        XCTAssertTrue((removed["words"] as? [[String: Any]])?.first?["why"] is NSNull)
        XCTAssertEqual(((removed["aliases"] as? [[String: Any]])?.first?["n"] as? NSNumber)?.intValue, -3)
        XCTAssertEqual(store.load().state, loaded.state, "a second round trip changes nothing")
    }

    // ─── An undecodable file ───

    func testACorruptFileIsKeptAsBadAndLearningStartsEmpty() throws {
        try assertKeptAside("{\"schema\": \"dictation.learning/1\", \"words\": [")
    }

    func testAnotherSchemaIsKeptAsBad() throws {
        try assertKeptAside(#"{"schema": "dictation.learning/2", "words": [], "aliases": [], "pending": [], "removed": {"words": [], "aliases": []}}"#)
    }

    /// Every field of an entry is required: an entry the reference could not
    /// have written makes the file undecodable.
    func testAnEntryMissingAFieldIsKeptAsBad() throws {
        try assertKeptAside(#"{"schema": "dictation.learning/1", "words": [{"text": "x", "origin": "taught"}]}"#)
    }

    func testAFileWithoutASchemaIsKeptAsBad() throws {
        try assertKeptAside(#"{"words": [], "aliases": [], "pending": [], "removed": {"words": [], "aliases": []}}"#)
    }

    /// If the undecodable file cannot even be moved aside, it is the only copy
    /// of the user's entries: no write may replace it until a later load moves
    /// it aside.
    func testAFileThatCannotBeMovedAsideIsNeverOverwritten() throws {
        try skipIfRoot()
        try plant("not json at all")
        let folder = url.deletingLastPathComponent().path
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder)
        let loaded = store.load()
        // Writable again, so only the store's own refusal can stop the write.
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder)

        XCTAssertEqual(loaded.outcome, .quarantineFailed)
        XCTAssertEqual(loaded.state, .empty)
        XCTAssertThrowsError(try store.save(sample())) { error in
            XCTAssertEqual(error as? LearningStore.Failure, .keepingUnreadableFile)
        }
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "not json at all")
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.quarantineURL.path))

        // The next load can move it aside, and writing is allowed again.
        XCTAssertEqual(store.load().outcome, .quarantined)
        try store.save(sample())
        XCTAssertEqual(try String(contentsOf: store.quarantineURL, encoding: .utf8), "not json at all")
    }

    // ─── A file that could not be read ───

    private func identity(_ path: String) throws -> [String] {
        let attributes = try FileManager.default.attributesOfItem(atPath: path)
        return [attributes[.systemFileNumber], attributes[.size], attributes[.modificationDate]].map { "\($0 ?? "nil")" }
    }

    /// A read that fails (here: no permission) is not a bad file. It stays
    /// exactly where it is, is never moved aside, and no write replaces it
    /// until a later load reads it (LEARNING.md §5).
    func testAFileThatCannotBeReadIsLeftInPlaceAndNeverOverwritten() throws {
        try skipIfRoot()
        try store.save(sample())
        let before = try Data(contentsOf: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: url.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path) }
        let seen = try identity(url.path)

        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .unreadable)
        XCTAssertTrue(loaded.outcome.keepsFileUnread)
        XCTAssertEqual(loaded.state, .empty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.quarantineURL.path), "not moved aside")
        XCTAssertThrowsError(try store.save(.empty)) { error in
            XCTAssertEqual(error as? LearningStore.Failure, .keepingUnreadableFile)
        }
        XCTAssertEqual(try identity(url.path), seen, "the same file, untouched")

        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        XCTAssertEqual(try Data(contentsOf: url), before)
        let again = store.load()
        XCTAssertEqual(again.outcome, .loaded)
        XCTAssertEqual(again.state, sample())
        try store.save(.empty)
        XCTAssertEqual(store.load().state, .empty, "writing is allowed once the file was read")
    }

    /// Says a file exists once, then asks the disk: the file vanished between
    /// the check and the read.
    private final class VanishingFileManager: FileManager, @unchecked Sendable {
        private var lied = false

        override func fileExists(atPath path: String) -> Bool {
            if !lied {
                lied = true
                return true
            }
            return super.fileExists(atPath: path)
        }
    }

    /// A file removed between the check and the read is missing, not
    /// unreadable: there is nothing to keep, so writing stays allowed.
    func testAFileThatVanishesBeforeTheReadIsMissing() throws {
        let store = LearningStore(url: url, fileManager: VanishingFileManager())
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .missing)
        try store.save(sample())
        XCTAssertEqual(LearningStore(url: url).load().state, sample())
    }

    /// The same, for any user: a directory where the file should be cannot be
    /// read, and is not moved aside or replaced.
    func testADirectoryInPlaceOfTheFileIsLeftAlone() throws {
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        let loaded = store.load()
        XCTAssertEqual(loaded.outcome, .unreadable)
        XCTAssertThrowsError(try store.save(sample())) { error in
            XCTAssertEqual(error as? LearningStore.Failure, .keepingUnreadableFile)
        }
        var isDirectory: ObjCBool = false
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory))
        XCTAssertTrue(isDirectory.boolValue)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.quarantineURL.path))
    }

    // ─── Writing ───

    private func requirePermissionSupport() throws {
        let probe = FileManager.default.temporaryDirectory.appendingPathComponent("dictation-mode-\(UUID().uuidString)")
        XCTAssertTrue(FileManager.default.createFile(atPath: probe.path, contents: Data()))
        defer { try? FileManager.default.removeItem(at: probe) }
        try FileManager.default.setAttributes([.posixPermissions: 0o640], ofItemAtPath: probe.path)
        try XCTSkipUnless(try mode(probe.path) == 0o640, "this filesystem does not keep POSIX modes")
    }

    func testTheDirectoryIsPrivateAndTheFileIs0600() throws {
        try requirePermissionSupport()
        try store.save(sample())
        XCTAssertEqual(try mode(url.deletingLastPathComponent().path), 0o700)
        XCTAssertEqual(try mode(url.path), 0o600)
    }

    func testAWiderExistingFileBecomes0600() throws {
        try requirePermissionSupport()
        try plant(String(data: try LearningStore.encode(sample()), encoding: .utf8)!)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: url.path)
        try store.save(.empty)
        XCTAssertEqual(try mode(url.path), 0o600)
    }

    func testAWriteLeavesNoTemporaryFile() throws {
        try store.save(sample())
        try store.save(.empty)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: url.deletingLastPathComponent().path),
                       ["learning.json"])
    }

    func testAFailedWriteLeavesTheOldFileWhole() throws {
        try skipIfRoot()
        try store.save(sample())
        let before = try Data(contentsOf: url)
        let folder = url.deletingLastPathComponent().path
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: folder)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder) }
        XCTAssertThrowsError(try store.save(.empty)) { error in
            guard case .notWritable? = error as? LearningStore.Failure else {
                return XCTFail("expected notWritable, got \(error)")
            }
        }
        XCTAssertEqual(try Data(contentsOf: url), before)
    }

    func testEncodingIsDeterministic() throws {
        XCTAssertEqual(try LearningStore.encode(sample()), try LearningStore.encode(sample()))
    }

    // ─── The real file ───

    /// Tests must never reach the user's learning.json. Asserted on the real
    /// path's bytes as well as on the thrown error.
    func testTheRealFileIsRefusedUnderXCTest() throws {
        XCTAssertTrue(LearningStore.isUnderTest)
        let real = try XCTUnwrap(LearningStore.defaultURL())
        let before = FileManager.default.contents(atPath: real.path)

        XCTAssertThrowsError(try LearningStore.live()) { error in
            XCTAssertEqual(error as? LearningStore.Failure, .refusedUnderTest)
        }
        XCTAssertThrowsError(try DictationLearning.live(lexicon: WordListLexicon(words: []))) { error in
            XCTAssertEqual(error as? LearningStore.Failure, .refusedUnderTest)
        }
        XCTAssertEqual(FileManager.default.contents(atPath: real.path), before)
    }

    func testTheRealFileIsBesideTheUsersAliases() throws {
        let real = try XCTUnwrap(LearningStore.defaultURL())
        XCTAssertTrue(real.path.hasSuffix("/Dictation/dictation/learning.json"), real.path)
        #if os(macOS)
        XCTAssertTrue(real.path.hasSuffix("/Library/Application Support/Dictation/dictation/learning.json"), real.path)
        #endif
    }
}
