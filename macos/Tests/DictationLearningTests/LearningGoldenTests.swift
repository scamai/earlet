import XCTest
@testable import DictationLearning
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// The shared golden cases (`tests/fixtures/learning-golden.json`), the same
/// file `tests/learning_reference.py` and the C# port are held to. A
/// disagreement is a bug in one implementation, never a tolerance.
final class LearningGoldenTests: XCTestCase {

    private var golden: Golden!

    override func setUpWithError() throws {
        golden = try Golden.load()
    }

    /// Missing cases must fail, not pass vacuously: an empty fixture would
    /// make every loop below green.
    func testFixtureIsTheExpectedSchemaAndNotEmpty() {
        XCTAssertEqual(golden.schema, "dictation.learning-golden/1")
        XCTAssertGreaterThanOrEqual(golden.extract.count, 15)
        XCTAssertGreaterThanOrEqual(golden.sequences.count, 20)
        XCTAssertFalse(golden.lexicon.isEmpty, "the shared lexicon must not be empty")
    }

    /// `swift test` on the Mac runs without DICTATION_LEARNING_GOLDEN; the
    /// walk up from this file must find the same fixture (through the Linux
    /// script's symlinks too).
    func testTheFixtureIsFoundWithoutTheEnvironmentVariable() throws {
        let name = "DICTATION_LEARNING_GOLDEN"
        let saved = ProcessInfo.processInfo.environment[name]
        unsetenv(name)
        defer { if let saved { setenv(name, saved, 1) } }
        XCTAssertNil(ProcessInfo.processInfo.environment[name])

        let found = try Golden.locate()
        XCTAssertEqual(found.lastPathComponent, "learning-golden.json")
        XCTAssertEqual(found.deletingLastPathComponent().lastPathComponent, "fixtures")
        let bytes = try Data(contentsOf: found)
        XCTAssertEqual(try JSONDecoder().decode(Golden.self, from: bytes).schema, golden.schema)
        if let saved {
            XCTAssertEqual(bytes, try Data(contentsOf: URL(fileURLWithPath: saved)))
        }
    }

    func testEveryExtractCase() {
        let lexicon = WordListLexicon(words: golden.lexicon)
        var checked = 0
        for testCase in golden.extract {
            let result = LearningPolicy.extract(dictated: testCase.dictated, edited: testCase.edited, lexicon: lexicon)
            XCTAssertEqual(result.candidates.map { [$0.heard, $0.meant, $0.kind.rawValue] }, testCase.candidates,
                           "candidates of \(testCase.name)")
            XCTAssertEqual(result.rejections.map { [$0.heard, $0.meant, $0.reason.rawValue] }, testCase.rejections,
                           "rejections of \(testCase.name)")
            checked += 1
        }
        XCTAssertEqual(checked, golden.extract.count)
    }

    func testEverySequence() throws {
        var checked = 0
        for sequence in golden.sequences {
            let (state, results) = try golden.replay(sequence)
            XCTAssertEqual(results, sequence.steps.filter { $0.op == "teach" }.map { $0.result ?? "" },
                           "teach results of \(sequence.name)")
            XCTAssertEqual(state.projection(), sequence.expect.projection, sequence.name)
            if let expected = sequence.expect_journal {
                XCTAssertEqual(state.journalRecords(), expected, "journal of \(sequence.name)")
            }
            if let expected = sequence.expect_word_evidence {
                let actual = Dictionary(uniqueKeysWithValues: state.words.map { word in
                    let opposition: Int
                    if case .integer(let value) = word.extra["opposition"] { opposition = Int(value) } else { opposition = 0 }
                    return (word.text, [word.count, opposition])
                })
                XCTAssertEqual(actual, expected, sequence.name)
            }
            checked += 1
        }
        XCTAssertEqual(checked, golden.sequences.count)
    }
}

// ─── Fixture ───

struct Golden: Decodable {
    struct ExtractCase: Decodable {
        let name: String
        let dictated: String
        let edited: String
        let candidates: [[String]]
        let rejections: [[String]]
    }

    struct Step: Decodable {
        let op: String
        let dictation: String?
        let dictated: String?
        let edited: String?
        let written: String?
        let soundsLike: String?
        let text: String?
        let heard: String?
        let meant: String?
        let result: String?
        let at: Int64?

        enum CodingKeys: String, CodingKey {
            case op, dictation, dictated, edited, written, text, heard, meant, result, at
            case soundsLike = "sounds_like"
        }
    }

    struct Expect: Decodable {
        struct Pending: Decodable {
            let heard: String
            let meant: String
            let count: Int
        }

        let words: [String]
        let aliases: [String: String]
        let pending: [Pending]
        let removedWords: [String]
        let removedAliases: [[String]]

        enum CodingKeys: String, CodingKey {
            case words, aliases, pending
            case removedWords = "removed_words"
            case removedAliases = "removed_aliases"
        }

        /// The reference's projection as the Swift type. JSON objects carry no
        /// order, so aliases are put in the canonical (code-point) order.
        var projection: LearningProjection {
            LearningProjection(
                words: words,
                aliases: aliases
                    .map { LearningProjection.Pair(heard: $0.key, meant: $0.value) }
                    .sorted { LearningText.precedes($0.heard, $1.heard) },
                pending: pending.map { LearningProjection.Pending(heard: $0.heard, meant: $0.meant, count: $0.count) },
                removedWords: removedWords,
                removedAliases: removedAliases.map { LearningProjection.Pair(heard: $0[0], meant: $0[1]) })
        }
    }

    struct SequenceCase: Decodable {
        let name: String
        let steps: [Step]
        let expect: Expect
        let lexicon: [String]?
        let expect_word_evidence: [String: [Int]]?
        let expect_journal: [JournalRecord]?
    }

    /// One evidence record as the fixture states it: its id and the
    /// `[heard, meant]` pairs of its corrections, in journal order.
    struct JournalRecord: Decodable, Equatable {
        let id: String
        let corrections: [[String]]
    }

    struct Missing: Error, CustomStringConvertible {
        let description: String
    }

    let schema: String
    let lexicon: [String]
    let extract: [ExtractCase]
    let sequences: [SequenceCase]

    /// `DICTATION_LEARNING_GOLDEN` when set (the Linux script sets it), else
    /// the `tests/fixtures` beside the nearest LEARNING.md above this file.
    static func locate(filePath: String = #filePath) throws -> URL {
        if let path = ProcessInfo.processInfo.environment["DICTATION_LEARNING_GOLDEN"], !path.isEmpty {
            return URL(fileURLWithPath: path)
        }
        var directory = URL(fileURLWithPath: filePath).resolvingSymlinksInPath().deletingLastPathComponent()
        while directory.path != "/" && !directory.path.isEmpty {
            if FileManager.default.fileExists(atPath: directory.appendingPathComponent("LEARNING.md").path) {
                return directory.appendingPathComponent("tests/fixtures/learning-golden.json")
            }
            directory = directory.deletingLastPathComponent()
        }
        throw Missing(description: "no LEARNING.md above \(filePath); set DICTATION_LEARNING_GOLDEN")
    }

    static func load() throws -> Golden {
        let url = try locate()
        guard let data = FileManager.default.contents(atPath: url.path) else {
            throw Missing(description: "golden cases not readable at \(url.path)")
        }
        return try JSONDecoder().decode(Golden.self, from: data)
    }

    /// Runs one sequence the way `tests/test_learning_golden.py` does.
    func replay(_ sequence: SequenceCase) throws -> (LearningState, [String]) {
        let lexicon = WordListLexicon(words: sequence.lexicon ?? self.lexicon)
        var state = LearningState.empty
        var results: [String] = []
        for step in sequence.steps {
            switch step.op {
            case "observe":
                state.observe(dictationID: try need(step.dictation), dictated: try need(step.dictated),
                              edited: try need(step.edited), at: try need(step.at), lexicon: lexicon)
            case "teach":
                results.append(state.teach(written: try need(step.written), soundsLike: step.soundsLike,
                                           at: try need(step.at)).rawValue)
            case "remove_word":
                state.removeWord(try need(step.text), at: try need(step.at))
            case "remove_alias":
                state.removeAlias(heard: try need(step.heard), meant: try need(step.meant), at: try need(step.at))
            case "clear":
                state.clear()
            default:
                throw Missing(description: "unknown op \(step.op) in \(sequence.name)")
            }
        }
        return (state, results)
    }

    private func need<T>(_ value: T?) throws -> T {
        guard let value else { throw Missing(description: "a golden step is missing a field") }
        return value
    }
}

extension LearningState {
    /// The evidence journal's records in the fixture's `expect_journal` shape;
    /// empty when no journal was ever written.
    func journalRecords() -> [Golden.JournalRecord] {
        guard case .object(let journal)? = extra["evidence"], case .array(let records)? = journal["observations"] else { return [] }
        return records.compactMap { value in
            guard case .object(let record) = value, case .string(let id)? = record["id"],
                  case .array(let corrections)? = record["corrections"] else { return nil }
            return Golden.JournalRecord(id: id, corrections: corrections.compactMap { item in
                guard case .object(let c) = item, case .string(let heard)? = c["heard"], case .string(let meant)? = c["meant"] else { return nil }
                return [heard, meant]
            })
        }
    }
}
