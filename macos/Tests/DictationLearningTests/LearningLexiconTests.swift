import XCTest
@testable import DictationLearning

/// LEARNING.md §2: the ordinary-word test, and that a lexicon which cannot be
/// read fails CLOSED — every token ordinary, nothing learned automatically.
final class LearningLexiconTests: XCTestCase {

    private var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dictation-lexicon-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    /// The observable consequence of failing closed: a correction that a real
    /// lexicon would learn from teaches nothing.
    private func assertFailsClosed(_ lexicon: any LearningLexicon, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse(lexicon.isAvailable, file: file, line: line)
        XCTAssertTrue(lexicon.isOrdinary("Trovioo"), file: file, line: line)
        XCTAssertTrue(lexicon.isOrdinary("kubectl"), file: file, line: line)
        XCTAssertFalse(LearningPolicy.isDistinctive("Trovioo", lexicon: lexicon), file: file, line: line)
        var state = LearningState.empty
        for (index, at) in [Int64(1_000), 2_000].enumerated() {
            state.observe(dictationID: "d\(index)", dictated: "we use trovio", edited: "we use Trovioo",
                          at: at, lexicon: lexicon)
        }
        XCTAssertEqual(state, LearningState.empty, "a lexicon that cannot be read must learn nothing",
                       file: file, line: line)
    }

    // ─── Fail closed ───

    func testAnEmptyListFailsClosed() {
        assertFailsClosed(WordListLexicon(words: []))
        assertFailsClosed(WordListLexicon(words: ["", "  ", "\n"]))
    }

    func testAMissingWordListFailsClosed() {
        assertFailsClosed(WebsterLexicon(url: dir.appendingPathComponent("absent-web2")))
    }

    func testAWordListThatIsNotUTF8FailsClosed() throws {
        let url = dir.appendingPathComponent("web2")
        try Data([0x6D, 0x65, 0x65, 0x74, 0xFF, 0xFE, 0x0A, 0x61, 0x0A]).write(to: url)
        assertFailsClosed(WebsterLexicon(url: url))
    }

    func testADirectoryInPlaceOfTheWordListFailsClosed() {
        assertFailsClosed(WebsterLexicon(url: dir))
    }

    /// Distinctive tokens still are, with a real list: the control for the
    /// fail-closed cases above.
    func testAnAvailableListStillFindsDistinctiveWords() {
        let lexicon = WordListLexicon(words: ["we", "use"])
        XCTAssertTrue(lexicon.isAvailable)
        XCTAssertFalse(lexicon.isOrdinary("Trovioo"))
        XCTAssertTrue(LearningPolicy.isDistinctive("Trovioo", lexicon: lexicon))
    }

    // ─── The ordinary-word test ───

    func testThreePluralRules() {
        let lexicon = WordListLexicon(words: ["library", "box", "meeting", "bus"])
        for word in ["library", "libraries", "boxes", "meetings", "MEETINGS", "buses", "bus"] {
            XCTAssertTrue(lexicon.isOrdinary(word), word)
        }
        for word in ["librar", "boxer", "meetingz", "ies", "es", "s"] {
            XCTAssertFalse(lexicon.isOrdinary(word), word)
        }
    }

    /// LEARNING.md §2: -ed and -ing dropped, with the e restored, with one of
    /// a doubled consonant dropped, or -ied back to -y; the base needs three
    /// code points. The answers are the reference's.
    func testVerbEndings() {
        let lexicon = WordListLexicon(words: ["play", "change", "stop", "try", "run", "box", "y", "ye", "ex"])
        for word in ["played", "playing", "changed", "changing", "Changed", "stopped", "stopping", "tried",
                     "running", "boxing", "boxed", "box-played"] {
            XCTAssertTrue(lexicon.isOrdinary(word), word)
        }
        // "ying" and "yed" would be "y" or "ye" plus an ending: too short a
        // base. "exed" leaves "ex", "exe" and no word of three letters.
        for word in ["ying", "yed", "Ying", "exed", "ed", "ing", "tryied", "chang", "playful"] {
            XCTAssertFalse(lexicon.isOrdinary(word), word)
        }
    }

    func testCutAtTheFirstApostropheAndSplitOnSeparators() {
        let lexicon = WordListLexicon(words: ["don", "node", "js", "snake", "case"])
        XCTAssertTrue(lexicon.isOrdinary("don't"))
        XCTAssertTrue(lexicon.isOrdinary("don’t"))
        XCTAssertTrue(lexicon.isOrdinary("Node.js"))
        XCTAssertTrue(lexicon.isOrdinary("snake_case"))
        XCTAssertFalse(lexicon.isOrdinary("node-kube"))
        XCTAssertFalse(lexicon.isOrdinary("'node"), "a leading apostrophe does not cut")
        XCTAssertTrue(lexicon.isOrdinary("-._"), "no parts left is ordinary")
    }

    func testEntriesAreStrippedAndLowercased() {
        let lexicon = WordListLexicon(words: ["  Miles\t", "SKY"])
        XCTAssertTrue(lexicon.isOrdinary("miles"))
        XCTAssertTrue(lexicon.isOrdinary("Sky"))
    }

    // ─── Loading ───

    func testWebsterNamesDoNotBecomeOrdinaryWordsByLowercasing() throws {
        let url = dir.appendingPathComponent("web2")
        try "we use my mile a A i I apple Apple Miles Jarvis NASA\n".write(to: url, atomically: true, encoding: .utf8)
        let lexicon = WebsterLexicon(url: url)
        for word in ["A", "I", "Apple", "Mies"] { XCTAssertTrue(lexicon.isOrdinary(word), word) }
        for word in ["Miles", "Miles's", "use-Miles", "Jarvis", "NASA"] {
            XCTAssertFalse(lexicon.isOrdinary(word), word)
        }
        var state = LearningState.empty
        state.observe(dictationID: "name-fix", dictated: "we use Mies", edited: "we use Miles",
                      at: 1_000, lexicon: lexicon)
        XCTAssertEqual(state.words.map(\.text), ["Miles"])
        XCTAssertTrue(state.aliases.isEmpty, "Mies still stems to ordinary my; never force its replacement")
        XCTAssertTrue(state.pending.isEmpty)
    }

    /// An excluded Webster name is checked before the verb endings, as before
    /// the plural rules: `Downing` must not fold to `down` + `-ing`.
    func testWebsterNamesAreNotMadeOrdinaryByAVerbEnding() throws {
        let url = dir.appendingPathComponent("web2")
        try "down play ewe Downing\n".write(to: url, atomically: true, encoding: .utf8)
        let lexicon = WebsterLexicon(url: url)
        for word in ["playing", "downed", "Ewing"] { XCTAssertTrue(lexicon.isOrdinary(word), word) }
        for word in ["Downing", "use-Downing"] { XCTAssertFalse(lexicon.isOrdinary(word), word) }
    }

    /// Nothing is read at construction, and nothing is re-read after the first
    /// question: the answer cannot change halfway through a session.
    func testTheWordListIsReadLazilyAndOnce() throws {
        let url = dir.appendingPathComponent("web2")
        let lexicon = WebsterLexicon(url: url)
        try "meeting\nsky\n".write(to: url, atomically: true, encoding: .utf8)
        XCTAssertTrue(lexicon.isOrdinary("meetings"), "the file written after init must be the one read")
        XCTAssertEqual(lexicon.wordCount, 2)

        try "trovioo\n".write(to: url, atomically: true, encoding: .utf8)
        XCTAssertTrue(lexicon.isOrdinary("meetings"))
        XCTAssertFalse(lexicon.isOrdinary("trovioo"))
        try FileManager.default.removeItem(at: url)
        XCTAssertTrue(lexicon.isAvailable)
        XCTAssertEqual(lexicon.wordCount, 2)
    }

    func testWordsAreSplitOnAnyWhitespace() throws {
        let url = dir.appendingPathComponent("web2")
        try "a\r\nmeeting  sky\n\n".write(to: url, atomically: true, encoding: .utf8)
        let lexicon = WebsterLexicon(url: url)
        XCTAssertEqual(lexicon.wordCount, 3)
        XCTAssertTrue(lexicon.isOrdinary("sky"))
    }

    func testSystemIsOneSharedInstanceOverWeb2() {
        XCTAssertTrue(WebsterLexicon.system() === WebsterLexicon.system())
        XCTAssertEqual(WebsterLexicon.system().url.path, "/usr/share/dict/web2")
    }

    // ─── The real list ───

    /// macOS ships web2; most Linux hosts do not, so this is skipped there and
    /// the next test covers the same list.
    func testTheSystemWordList() throws {
        try XCTSkipUnless(FileManager.default.fileExists(atPath: "/usr/share/dict/web2"),
                          "/usr/share/dict/web2 is not installed on this host")
        assertRealWordList(WebsterLexicon.system())
    }

    /// The curated Windows subset, unpacked by
    /// macos/tools/test-learning-on-linux.sh.
    func testTheWindowsCopyOfTheWordList() throws {
        let path = ProcessInfo.processInfo.environment["DICTATION_LEARNING_WEB2"] ?? ""
        try XCTSkipIf(path.isEmpty, "DICTATION_LEARNING_WEB2 is not set")
        assertRealWordList(WebsterLexicon(url: URL(fileURLWithPath: path)))
    }

    private func assertRealWordList(_ lexicon: WebsterLexicon, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertTrue(lexicon.isAvailable, file: file, line: line)
        XCTAssertGreaterThan(lexicon.wordCount, 200_000, file: file, line: line)
        XCTAssertTrue(lexicon.isOrdinary("meetings"), file: file, line: line)
        XCTAssertTrue(lexicon.isOrdinary("libraries"), file: file, line: line)
        XCTAssertFalse(lexicon.isOrdinary("Trovioo"), file: file, line: line)
        XCTAssertFalse(lexicon.isOrdinary("kubectl"), file: file, line: line)
        XCTAssertFalse(lexicon.isOrdinary("Miles"), file: file, line: line)
        XCTAssertTrue(lexicon.isOrdinary("I"), file: file, line: line)
    }
}
