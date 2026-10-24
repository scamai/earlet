import XCTest
@testable import DictationLearning

/// The tokenizer and the Python string semantics the policy depends on
/// (LEARNING.md §3.1). Each expectation here is what `learning_reference.py`
/// computes for the same input.
final class LearningTextTests: XCTestCase {

    private func texts(_ text: String) -> [String] {
        LearningText.tokenize(text).map(\.text)
    }

    // ─── Tokens ───

    /// U+20000 is one code point but two UTF-16 units: it must be one token,
    /// and indices after it must count it once.
    func testCJKExtensionBScalarIsOneToken() {
        let tokens = LearningText.tokenize("a\u{20000}b")
        XCTAssertEqual(tokens.map(\.text), ["a", "\u{20000}", "b"])
        XCTAssertEqual(tokens.map(\.cjk), [false, true, false])
        XCTAssertEqual(tokens.map(\.start), [0, 1, 2])
        XCTAssertEqual(tokens.map(\.end), [1, 2, 3])
        XCTAssertEqual(texts("\u{20000}\u{2A6D6}\u{2FFFF}"), ["\u{20000}", "\u{2A6D6}", "\u{2FFFF}"])
    }

    func testAnExtensionBCorrectionIsCutOnCodePoints() {
        let result = LearningPolicy.extract(dictated: "我用\u{20000}服务", edited: "我用Miles服务",
                                            lexicon: WordListLexicon(words: ["miles"]))
        XCTAssertEqual(result.candidates, [LearningCandidate(heard: "\u{20000}", meant: "Miles", kind: .transliteration)])
    }

    func testEveryCJKRangeIsOneTokenPerCodePoint() {
        for scalar in ["\u{3041}", "\u{30FF}", "\u{3400}", "\u{4E00}", "\u{9FFF}", "\u{AC00}", "\u{F900}", "\u{2F800}"] {
            XCTAssertEqual(LearningText.tokenize(scalar + scalar).count, 2, "U+\(String(scalar.unicodeScalars.first!.value, radix: 16))")
        }
        // Just outside: Hangul Jamo is an ordinary letter run.
        XCTAssertEqual(texts("\u{1100}\u{1161}"), ["\u{1100}\u{1161}"])
    }

    func testJoinersContinueOnlyBeforeAWordCharacter() {
        XCTAssertEqual(texts("don't use Node.js at snake_case, e-mail. end- x’s"),
                       ["don't", "use", "Node.js", "at", "snake_case", "e-mail", "end", "x’s"])
        XCTAssertEqual(texts("a--b a..b"), ["a", "b", "a", "b"])
    }

    func testMarksAndDecimalDigitsAreWordCharactersOtherNumbersAreNot() {
        XCTAssertEqual(texts("cafe\u{301} GPT4 x\u{B2} \u{0663}\u{0664}"), ["cafe\u{301}", "GPT4", "x", "\u{0663}\u{0664}"])
    }

    func testIndicesAreCodePoints() {
        let tokens = LearningText.tokenize("\u{E9}\u{1F600}ab")
        XCTAssertEqual(tokens.map(\.text), ["\u{E9}", "ab"])
        XCTAssertEqual(tokens.map(\.start), [0, 2])
        XCTAssertEqual(tokens.map(\.end), [1, 4])
    }

    // ─── Python str semantics ───

    func testLowerAppliesFinalSigmaAndFullMappingsLikePython() {
        XCTAssertEqual(LearningText.lower("\u{39F}\u{394}\u{39F}\u{3A3} \u{3A3}\u{391}\u{3A3}"),
                       "\u{3BF}\u{3B4}\u{3BF}\u{3C2} \u{3C3}\u{3B1}\u{3C2}")
        XCTAssertEqual(LearningText.lower("\u{3A3}"), "\u{3C3}", "a lone capital sigma is not final")
        XCTAssertEqual(LearningText.lower("\u{130}"), "i\u{307}")
        XCTAssertEqual(LearningText.lower("SkyDock"), "skydock")
    }

    func testWhitespaceIsPythonsIncludingTheInformationSeparators() {
        XCTAssertEqual(LearningText.strip("\u{1F} a b\u{3000}\u{85}"), "a b")
        XCTAssertEqual(LearningText.heardKey("  Sky\u{1C}\u{A0}DOCK\t"), "sky dock")
        XCTAssertEqual(LearningText.strip("\u{200B}x"), "\u{200B}x", "U+200B is not whitespace in Python")
    }

    func testComparisonsAreExactCodePointsNotCanonicalEquivalence() {
        XCTAssertTrue("\u{E9}" == "e\u{301}", "Swift's own String == is canonical")
        XCTAssertFalse(LearningText.same("\u{E9}", "e\u{301}"))
        XCTAssertNotEqual(ExactText("\u{E9}"), ExactText("e\u{301}"))
        XCTAssertEqual(Set([ExactText("\u{E9}"), ExactText("e\u{301}")]).count, 2)
        XCTAssertTrue(LearningText.precedes("Z", "a"))
        XCTAssertTrue(LearningText.precedes("\u{FFFF}", "\u{10000}"), "code-point order, not UTF-16 order")
    }

    /// The same property through the entry points: precomposed and decomposed
    /// "é" are different tokens to the diff and different words to the store,
    /// as they are to the reference and to C#'s ordinal comparison.
    func testThePolicyComparesCodePointsNotCanonicalForms() {
        let lexicon = WordListLexicon(words: ["we", "use"])
        let result = LearningPolicy.extract(dictated: "we use cafe\u{301} now", edited: "we use caf\u{E9} now",
                                            lexicon: lexicon)
        XCTAssertEqual(result.candidates.map { $0.heard.unicodeScalars.count }, [5])
        XCTAssertEqual(result.candidates.map(\.kind), [.spelling])

        var state = LearningState.empty
        XCTAssertEqual(state.teach(written: "caf\u{E9}", soundsLike: nil, at: 1), .ok)
        XCTAssertEqual(state.teach(written: "cafe\u{301}", soundsLike: nil, at: 2), .ok)
        XCTAssertEqual(state.words.map { Array($0.text.unicodeScalars) },
                       [Array("cafe\u{301}".unicodeScalars), Array("caf\u{E9}".unicodeScalars)])
    }

    func testDerivedForms() {
        XCTAssertEqual(LearningText.string(LearningText.squash("Node.js  runs")), "nodejsruns")
        XCTAssertEqual(LearningText.string(LearningText.normSpace("Node.js  RUNS")), "nodejs runs")
        XCTAssertEqual(LearningText.levenshtein(Array("kitten".unicodeScalars), Array("sitting".unicodeScalars)), 3)
        XCTAssertEqual(LearningText.levenshtein([], Array("ab".unicodeScalars)), 2)
    }
}
