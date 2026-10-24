import XCTest
@testable import DictationLearning

/// The admission rules and caps of LEARNING.md §3.3-§4.5 at their boundaries,
/// beyond what the golden sequences reach.
final class LearningRulesTests: XCTestCase {

    private let lexicon = WordListLexicon(words: ["we", "use", "the", "node", "sky", "deck"])
    private let t0: Int64 = 1_790_000_000_000
    private let day: Int64 = 24 * 60 * 60 * 1000

    func testMalformedOptionalEvidencePreservesReadableVocabulary() throws {
        for json in ["null", "[]", #"{"version":true,"observations":[]}"#,
                     #"{"version":1,"observations":[null]}"#,
                     #"{"version":1,"observations":[{"id":"bad","at":1,"weight":true,"corrections":[]}]}"#] {
            var state = LearningState.empty
            _ = state.teach(written: "Miles", soundsLike: nil, at: t0)
            state.extra["evidence"] = try JSONDecoder().decode(LearningJSON.self, from: Data(json.utf8))
            let before = state
            state.observe(dictationID: "one", dictated: "we use Mies", edited: "we use Miles", at: t0 + 1, lexicon: lexicon)
            XCTAssertEqual(state, before, json)
        }
    }

    func testMigrationDoesNotCountAWordAndItsPendingPairTwice() throws {
        var state = LearningState.empty
        state.words = [LearnedWord(text: "Trovioo", origin: .correction, count: 1, firstSeen: t0 - 20, lastSeen: t0 - 10)]
        state.pending = [PendingAlias(heard: "trovio", meant: "Trovioo", count: 1, dictations: ["old"],
                                      firstSeen: t0 - 20, lastSeen: t0 - 10)]
        state.observe(dictationID: "new", dictated: "we use trovio", edited: "we use Trovioo", at: t0, lexicon: lexicon)
        XCTAssertEqual(state.words.first?.count, 2)
        XCTAssertEqual(state.words.first?.firstSeen, t0 - 20)
        XCTAssertEqual(state.aliases.first?.meant, "Trovioo")
        state = try JSONDecoder().decode(LearningState.self, from: JSONEncoder().encode(state))
        state.observe(dictationID: "new", dictated: "we use trovio", edited: "we use Trovioo", at: t0 + 1, lexicon: lexicon)
        XCTAssertEqual(state.words.first?.count, 2)
    }

    func testMigrationKeepsAnAlreadyAdmittedLegacyWordThatLooksSecretShaped() {
        var state = LearningState.empty
        state.words = [LearnedWord(text: "Qword000", origin: .correction, count: 1, firstSeen: t0 - 20, lastSeen: t0 - 10)]
        state.observe(dictationID: "new", dictated: "we use trovio", edited: "we use Trovioo", at: t0, lexicon: lexicon)
        XCTAssertEqual(state.words.first { $0.text == "Qword000" }?.count, 1)
    }

    private func classify(_ heard: String, _ meant: String) -> LearningPolicy.Classification {
        LearningPolicy.classify(heard: heard, meant: meant, lexicon: lexicon)
    }

    // ─── Distance ratio ───

    /// Exactly 0.5 is still a spelling fix; above it is a rewording.
    func testTheDistanceRatioIsStrictlyAboveOneHalf() {
        XCTAssertEqual(classify("abcd", "abxy"), .candidate(LearningCandidate(heard: "abcd", meant: "abxy", kind: .spelling)))
        XCTAssertEqual(classify("abcde", "abxyz"),
                       .rejection(LearningRejection(heard: "abcde", meant: "abxyz", reason: .rewording)))
    }

    // ─── Numbers (LEARNING.md §2, §4.2) ───

    /// A part with no letter is ordinary, so a number is never vocabulary; a
    /// part with a letter still needs the word list.
    func testANumberIsAnOrdinaryWord() {
        for token in ["4471", "10", "1-800", "3.14", "٤٤٧١"] {
            XCTAssertTrue(lexicon.isOrdinary(token), token)
            XCTAssertFalse(LearningPolicy.isDistinctive(token, lexicon: lexicon), token)
        }
        XCTAssertTrue(lexicon.isOrdinary("node-10"), "every part ordinary, the number included")
        XCTAssertFalse(lexicon.isOrdinary("kube-10"))
        XCTAssertFalse(lexicon.isOrdinary("4471s"), "a letter makes it a word again")
        XCTAssertTrue(LearningPolicy.isDistinctive("GPT4", lexicon: lexicon), "letters and digits together still are")
    }

    func testANumberIsNeverARewriteTarget() {
        let words = WordListLexicon(words: ["we"])
        XCTAssertFalse(LearningPolicy.learnsAlias(LearningCandidate(heard: "zork", meant: "4417", kind: .spelling),
                                                  lexicon: words))
        XCTAssertFalse(LearningPolicy.learnsAlias(
            LearningCandidate(heard: "\u{56DB}\u{56DB}\u{4E03}\u{4E00}", meant: "4471", kind: .transliteration),
            lexicon: words))
        XCTAssertTrue(LearningPolicy.learnsAlias(LearningCandidate(heard: "zork", meant: "Zork4", kind: .spelling),
                                                 lexicon: words), "the control: a target with a letter")
        XCTAssertTrue(LearningPolicy.learnsAlias(
            LearningCandidate(heard: "\u{963F}\u{91CC}", meant: "Ali", kind: .transliteration), lexicon: words))
    }

    // ─── Input-method composition (LEARNING.md §3.3) ───

    func testUncommittedPinyinOrRomajiIsComposition() {
        let heard = "\u{963F}\u{91CC}\u{65AF}"
        for meant in ["alisi", "a'li si", "ali"] {
            XCTAssertEqual(classify(heard, meant),
                           .rejection(LearningRejection(heard: heard, meant: meant, reason: .composition)), meant)
        }
        for meant in ["Alisi", "alisi2", "ALISI", "ali-si", "\u{E0}lisi"] {
            XCTAssertEqual(classify(heard, meant),
                           .candidate(LearningCandidate(heard: heard, meant: meant, kind: .transliteration)), meant)
        }
        XCTAssertEqual(classify("alisi", heard),
                       .candidate(LearningCandidate(heard: "alisi", meant: heard, kind: .transliteration)),
                       "only CJK heard as lowercase letters is composition")
    }

    // ─── Continuations (LEARNING.md §3.2) ───

    private func extract(_ dictated: String, _ edited: String)
        -> (candidates: [LearningCandidate], rejections: [LearningRejection]) {
        LearningPolicy.extract(dictated: dictated, edited: edited, lexicon: lexicon)
    }

    func testTextTypedAfterOrBeforeTheSpanIsAContinuation() {
        let after = extract("Call trovio", "Call Trovioo now")
        XCTAssertEqual(after.candidates, [])
        XCTAssertEqual(after.rejections, [LearningRejection(heard: "trovio", meant: "Trovioo now", reason: .boundary)])
        let before = extract("trovio is great", "Hey Trovioo is great")
        XCTAssertEqual(before.rejections, [LearningRejection(heard: "trovio", meant: "Hey Trovioo", reason: .boundary)])
        let whole = extract("trovio", "Trovioo now")
        XCTAssertEqual(whole.rejections, [LearningRejection(heard: "trovio", meant: "Trovioo now", reason: .boundary)])
    }

    /// Only a hunk that reaches an end of both texts AND grows there.
    func testAFixAtAnEndOrGrowingInTheMiddleIsStillACorrection() {
        XCTAssertEqual(extract("Call trovio", "Call Trovioo").candidates,
                       [LearningCandidate(heard: "trovio", meant: "Trovioo", kind: .spelling)])
        XCTAssertEqual(extract("trovio is great", "Trovioo is great").candidates,
                       [LearningCandidate(heard: "trovio", meant: "Trovioo", kind: .spelling)])
        XCTAssertEqual(extract("ask javis to deploy", "ask Jarvis AI to deploy").candidates,
                       [LearningCandidate(heard: "javis", meant: "Jarvis AI", kind: .spelling)])
        XCTAssertEqual(extract("we use sky dock", "we use SkyDock").candidates,
                       [LearningCandidate(heard: "sky dock", meant: "SkyDock", kind: .respacing)],
                       "a hunk at the end that shrinks is a fix")
    }

    // ─── An origin this version does not know (LEARNING.md §5) ───

    func testAnUnknownOriginIsNeverReCasedNorEvicted() {
        var state = LearningState.empty
        var imported = LearnedWord(text: "KubeCTL", origin: .other("imported"), count: 1, firstSeen: 1, lastSeen: 1)
        imported.extra = ["source": .string("sync")]
        state.words = [imported]
        state.observe(dictationID: "d1", dictated: "we use kubectl", edited: "we use Kubectl", at: t0, lexicon: lexicon)
        XCTAssertEqual(state.words.map(\.text), ["KubeCTL"], "not re-cased")
        XCTAssertEqual(state.words.first?.origin, .other("imported"))
        XCTAssertEqual(state.words.first?.count, 1, "unknown-origin evidence is not reinterpreted")
        XCTAssertEqual(state.words.first?.extra, ["source": .string("sync")], "unknown keys stay with the entry")

        for i in 0..<(LearningLimits.maxLearnedWords + 1) {
            state.observe(dictationID: "e\(i)", dictated: "we use zorb", edited: "we use Zorb\(i)x",
                          at: t0 + 1 + Int64(i), lexicon: lexicon)
        }
        XCTAssertEqual(state.words.filter { $0.origin == .correction }.count, LearningLimits.maxLearnedWords)
        XCTAssertTrue(state.words.contains { $0.text == "KubeCTL" }, "the least recently seen, but never evicted")
        XCTAssertEqual(state.teach(written: "Kubectl", soundsLike: nil, at: t0 + 500), .ok)
        XCTAssertEqual(state.words.first { $0.text == "Kubectl" }?.origin, .taught, "teaching makes it the user's")
    }

    // ─── Aliases need two distinct dictations ───

    private func observeTrovio(_ state: inout LearningState, _ id: String, at: Int64) {
        state.observe(dictationID: id, dictated: "we use trovio", edited: "we use Trovioo", at: at, lexicon: lexicon)
    }

    func testTheSameDictationCountsOnceTowardsAnAlias() {
        var state = LearningState.empty
        observeTrovio(&state, "d1", at: t0)
        observeTrovio(&state, "d1", at: t0 + 1)
        observeTrovio(&state, "d1", at: t0 + 2)
        XCTAssertTrue(state.aliases.isEmpty)
        XCTAssertEqual(state.pending.map(\.count), [1])
        XCTAssertEqual(state.pending.first?.dictations, ["d1"])

        observeTrovio(&state, "d2", at: t0 + 3)
        XCTAssertEqual(state.aliases.map { [$0.heard, $0.meant] }, [["trovio", "Trovioo"]])
        XCTAssertEqual(state.aliases.first?.count, 2)
        XCTAssertEqual(state.aliases.first?.firstSeen, t0)
        XCTAssertTrue(state.pending.isEmpty)
    }

    func testOneObservationNeverPromotes() {
        var state = LearningState.empty
        observeTrovio(&state, "d1", at: t0)
        XCTAssertTrue(state.aliases.isEmpty)
        XCTAssertEqual(state.words.map(\.text), ["Trovioo"], "the word itself is learned at once")
    }

    // ─── Secret-shaped words wait (LEARNING.md §4.1) ───

    /// The golden projection holds word texts only. A secret-shaped word is
    /// written when its pending pair is promoted, and the entry takes the pair's
    /// count and first sighting, not the moment of promotion. The repeated d1
    /// moves the pair's last sighting but adds no count.
    func testAWordThatWaitedKeepsTheCountAndFirstSightingOfItsPendingPair() {
        let words = WordListLexicon(words: ["the", "wifi", "password", "is", "sunshine"])
        var state = LearningState.empty
        for (id, at) in [("d1", t0), ("d1", t0 + 1000), ("d2", t0 + 5000)] {
            state.observe(dictationID: id, dictated: "the wifi password is sunshine",
                          edited: "the wifi password is Sunshine42x!", at: at, lexicon: words)
        }
        XCTAssertTrue(state.pending.isEmpty)
        XCTAssertEqual(state.words.map(\.text), ["Sunshine42x"])
        XCTAssertEqual(state.words.first?.origin, .correction)
        XCTAssertEqual(state.words.first?.count, 2)
        XCTAssertEqual(state.words.first?.firstSeen, t0)
        XCTAssertEqual(state.words.first?.lastSeen, t0 + 5000)
    }

    // ─── Casing ───

    /// A casing fix is usually refused twice over: its lowercase tokens equal
    /// the heard ones, so it also "contains its trigger". Greek final sigma
    /// across a middle dot (case-ignorable, not a joiner) is where the two
    /// differ — whole-text lowercase gives σ, the lone token gives ς — and only
    /// the casing rule keeps the alias out. Checked against the reference.
    func testACasingFixNeverBecomesAnAliasEvenWhenItsTokensDiffer() throws {
        let dictated = "we use \u{391}\u{3A3}\u{B7}\u{391}"
        let edited = "we use \u{391}\u{3C3}\u{B7}\u{3B1}"
        let lexicon = WordListLexicon(words: ["we", "use"])
        let candidates = LearningPolicy.extract(dictated: dictated, edited: edited, lexicon: lexicon).candidates
        XCTAssertEqual(candidates.map(\.kind), [.casing])
        let casing = try XCTUnwrap(candidates.first)
        XCTAssertFalse(LearningPolicy.containsTokens(casing.meant, casing.heard))

        var state = LearningState.empty
        state.observe(dictationID: "d1", dictated: dictated, edited: edited, at: t0, lexicon: lexicon)
        state.observe(dictationID: "d2", dictated: dictated, edited: edited, at: t0 + 1, lexicon: lexicon)
        XCTAssertEqual(state.words.map(\.text), ["\u{391}\u{3C3}\u{B7}\u{3B1}"])
        XCTAssertTrue(state.aliases.isEmpty)
        XCTAssertTrue(state.pending.isEmpty)
    }

    // ─── Self-containing rewrites ───

    func testALearnedAliasMayNotContainItsSpokenForm() {
        let candidate = LearningCandidate(heard: "node", meant: "Node js", kind: .spelling)
        XCTAssertFalse(LearningPolicy.learnsAlias(candidate, lexicon: WordListLexicon(words: ["we"])))
        let control = LearningCandidate(heard: "nod", meant: "Node js", kind: .spelling)
        XCTAssertTrue(LearningPolicy.learnsAlias(control, lexicon: WordListLexicon(words: ["we"])))
    }

    func testATaughtAliasMayNotContainItsSpokenForm() {
        var state = LearningState.empty
        XCTAssertEqual(state.teach(written: "Node.js runtime", soundsLike: "node js", at: t0), .ok,
                       "tokens are compared whole: node.js is not node js")
        XCTAssertEqual(state.teach(written: "Fable Notes", soundsLike: "notes", at: t0), .selfContaining)
        XCTAssertEqual(state.teach(written: "SkyDock", soundsLike: "sky dock", at: t0), .ok)
        XCTAssertFalse(state.aliases.contains { $0.heard == "notes" })
    }

    /// The limits the golden cases do not reach. Each expectation is the
    /// reference's answer for the same input.
    func testTeachingValidatesAtEachLimit() {
        let cases: [(String, String?, TeachResult)] = [
            ("!!!", nil, .empty),
            ("Trovioo", String(repeating: "x", count: 61), .tooLong),
            ("Trovioo", "  ", .ok),
            ("Trovioo", " t ", .empty),
            (String(repeating: "\u{963F}", count: 12), nil, .ok),
            (String(repeating: "\u{963F}", count: 13), nil, .tooLong),
            ("a b c d e f", nil, .ok),
            ("a b c d e f g", nil, .tooLong),
            (String(repeating: "x", count: 60), nil, .ok),
        ]
        for (written, soundsLike, expected) in cases {
            var state = LearningState.empty
            XCTAssertEqual(state.teach(written: written, soundsLike: soundsLike, at: t0), expected,
                           "\(written.prefix(8)) / \(soundsLike?.prefix(8) ?? "nil")")
        }
    }

    // ─── Tombstones ───

    func testARemovedWordAndItsAliasAreNeverRelearned() {
        var state = LearningState.empty
        observeTrovio(&state, "d1", at: t0)
        observeTrovio(&state, "d2", at: t0 + 1)
        state.removeWord("trovioo", at: t0 + 2)
        for id in ["d3", "d4", "d5"] { observeTrovio(&state, id, at: t0 + 3) }
        XCTAssertTrue(state.words.isEmpty)
        XCTAssertTrue(state.aliases.isEmpty)
        XCTAssertTrue(state.pending.isEmpty)
        XCTAssertEqual(state.removed.words.map(\.key), ["trovioo"])
        XCTAssertEqual(state.removed.aliases.map { [$0.heard, $0.meant] }, [["trovio", "trovioo"]])
    }

    func testARemovedAliasIsNeverRelearnedButItsWordStays() {
        var state = LearningState.empty
        observeTrovio(&state, "d1", at: t0)
        observeTrovio(&state, "d2", at: t0 + 1)
        state.removeAlias(heard: "Trovio", meant: "TROVIOO", at: t0 + 2)
        for id in ["d3", "d4"] { observeTrovio(&state, id, at: t0 + 3) }
        XCTAssertTrue(state.aliases.isEmpty)
        XCTAssertTrue(state.pending.isEmpty)
        XCTAssertEqual(state.words.map(\.text), ["Trovioo"])
        XCTAssertEqual(state.words.first?.count, 4)
    }

    // ─── Pending TTL ───

    func testPendingPairsExpireAfterExactlyThirtyDays() {
        let quiet: (inout LearningState, Int64) -> Void = { state, at in
            state.observe(dictationID: "q", dictated: "we use", edited: "we use", at: at, lexicon: self.lexicon)
        }
        var state = LearningState.empty
        observeTrovio(&state, "d1", at: t0)
        quiet(&state, t0 + 30 * day)
        XCTAssertEqual(state.pending.count, 1, "still pending at exactly 30 days")
        quiet(&state, t0 + 30 * day + 1)
        XCTAssertTrue(state.pending.isEmpty)
        XCTAssertEqual(state.words.map(\.text), ["Trovioo"], "words do not expire")
    }

    // ─── Caps ───

    func testFiveHundredTaughtWordsThenFull() {
        var state = LearningState.empty
        for i in 0..<500 {
            XCTAssertEqual(state.teach(written: "Brandword\(i)", soundsLike: nil, at: t0 + Int64(i)), .ok)
        }
        XCTAssertEqual(state.teach(written: "Brandword500", soundsLike: nil, at: t0 + 600), .full)
        XCTAssertEqual(state.teach(written: "brandword7", soundsLike: nil, at: t0 + 601), .ok,
                       "re-teaching a taught word is not a new word")
        observeTrovio(&state, "d1", at: t0 + 700)
        XCTAssertEqual(state.words.count, 501, "learned words do not count against the taught cap")
    }

    private func teachAliases(_ state: inout LearningState, _ range: Range<Int>) {
        for i in range {
            XCTAssertEqual(state.teach(written: "Zq\(i)", soundsLike: "zed queue \(i)", at: t0 + Int64(i)), .ok)
        }
    }

    func testTwoHundredTaughtAliasesThenFull() {
        var state = LearningState.empty
        teachAliases(&state, 0..<200)
        XCTAssertEqual(state.teach(written: "Zq200", soundsLike: "zed queue 200", at: t0 + 300), .full)
        XCTAssertEqual(state.teach(written: "Zq5", soundsLike: "zed queue five", at: t0 + 301), .full)
        XCTAssertEqual(state.teach(written: "Zq9", soundsLike: "zed queue 9", at: t0 + 302), .ok,
                       "replacing an existing sound is not a new alias")
        XCTAssertEqual(state.aliases.count, 200)
    }

    func testALearnedAliasMakesRoomForATaughtOne() {
        var state = LearningState.empty
        observeTrovio(&state, "d1", at: t0)
        observeTrovio(&state, "d2", at: t0 + 1)
        teachAliases(&state, 0..<199)
        XCTAssertEqual(state.aliases.count, 200)
        XCTAssertEqual(state.teach(written: "Zq199", soundsLike: "zed queue 199", at: t0 + 500), .ok)
        XCTAssertEqual(state.aliases.count, 200)
        XCTAssertFalse(state.aliases.contains { $0.origin == .correction }, "the learned alias is evicted first")
        XCTAssertEqual(state.teach(written: "Zq200", soundsLike: "zed queue 200", at: t0 + 501), .full)
    }

    func testFiveHundredPendingPairsLeastRecentOut() {
        var state = LearningState.empty
        state.pending = (0..<500).map { i in
            PendingAlias(heard: "qx\(i)", meant: "Qxz\(i)", count: 1, dictations: ["p\(i)"],
                         firstSeen: t0 + Int64(i), lastSeen: t0 + Int64(i))
        }
        observeTrovio(&state, "d1", at: t0 + 1_000)
        XCTAssertEqual(state.pending.count, 500)
        XCTAssertFalse(state.pending.contains { $0.heard == "qx0" }, "the least recently seen pair goes")
        XCTAssertTrue(state.pending.contains { $0.heard == "trovio" })
        XCTAssertTrue(state.pending.contains { $0.heard == "qx1" })
    }

    func testAThousandTombstonesOfEachKindOldestOut() {
        var state = LearningState.empty
        state.removed.words = (0..<1000).map { RemovedWord(key: "w\($0)", at: t0 + Int64($0)) }
        state.removed.aliases = (0..<1000).map { RemovedAlias(heard: "h\($0)", meant: "m\($0)", at: t0 + Int64($0)) }
        state.removeWord("newest", at: t0 + 5_000)
        state.removeAlias(heard: "fresh", meant: "Fresh", at: t0 + 5_000)
        XCTAssertEqual(state.removed.words.count, 1000)
        XCTAssertEqual(state.removed.aliases.count, 1000)
        XCTAssertFalse(state.removed.words.contains { $0.key == "w0" })
        XCTAssertTrue(state.removed.words.contains { $0.key == "newest" })
        XCTAssertFalse(state.removed.aliases.contains { $0.heard == "h0" })
        XCTAssertTrue(state.removed.aliases.contains { $0.heard == "fresh" && $0.meant == "fresh" })
    }

    // ─── Canonical order ───

    func testWordsAreMostRecentFirstAndListsAreCanonical() {
        var state = LearningState.empty
        XCTAssertEqual(state.teach(written: "Beta", soundsLike: "bee ta", at: t0), .ok)
        XCTAssertEqual(state.teach(written: "Alpha", soundsLike: "al fa", at: t0 + 1), .ok)
        XCTAssertEqual(state.teach(written: "Gamma", soundsLike: nil, at: t0), .ok)
        XCTAssertEqual(state.words.map(\.text), ["Alpha", "Beta", "Gamma"])
        XCTAssertEqual(state.aliases.map(\.heard), ["al fa", "bee ta"])
    }

    func testClearForgetsTombstonesToo() {
        var state = LearningState.empty
        observeTrovio(&state, "d1", at: t0)
        state.removeWord("Trovioo", at: t0 + 1)
        state.clear()
        XCTAssertEqual(state, .empty)
        observeTrovio(&state, "d2", at: t0 + 2)
        XCTAssertEqual(state.words.map(\.text), ["Trovioo"])
    }
}
