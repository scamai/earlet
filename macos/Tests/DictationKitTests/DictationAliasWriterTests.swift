import XCTest
@testable import DictationKit

/// `bias_aliases.json` is the user's own file, and the worker reads two
/// shapes of it (`spoken_aliases._read_file`): the wrapped
/// `{"schema", "aliases", "removed"}` that Windows writes, and a flat table.
/// The writer used to read only a wrapped file with nothing but `aliases` in
/// it, so a `removed` list or a `schema` string made the whole file read as
/// empty, and the next merge rewrote it with only the new rows.
final class DictationAliasWriterTests: XCTestCase {

    private var dir: URL!
    private var url: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("dictation-aliases-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        url = dir.appendingPathComponent("bias_aliases.json")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: dir)
    }

    private func write(_ json: String) throws {
        try Data(json.utf8).write(to: url)
    }

    private func object() throws -> [String: Any] {
        let data = try Data(contentsOf: url)
        return try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    /// The wrapped shape produced by the Windows writer
    /// (`DictationSpokenAliases.Write`). Dictation's Windows app writes no
    /// alias file.
    private let windowsFile = """
        {
          "schema": "spoken_aliases/0.1.0",
          "aliases": {
            "jasper": "Jasper",
            "bad forge": "PadForge"
          },
          "removed": [
            "mies"
          ]
        }
        """

    // ─── Reading ───

    func testTheWrappedWorkerFileIsRead() throws {
        try write(windowsFile)
        XCTAssertEqual(DictationAliasWriter.load(from: url),
                       ["jasper": "Jasper", "bad forge": "PadForge"])
    }

    func testAFlatTableIsRead() throws {
        try write(#"{"jasper": "Jasper", "removed": "Removed"}"#)
        // In a flat file every key is a row, even one spelled `removed`,
        // which is also how the worker reads it.
        XCTAssertEqual(DictationAliasWriter.load(from: url),
                       ["jasper": "Jasper", "removed": "Removed"])
    }

    func testMissingOrMalformedReadsAsEmpty() throws {
        XCTAssertEqual(DictationAliasWriter.load(from: url), [:])
        try write("not json")
        XCTAssertEqual(DictationAliasWriter.load(from: url), [:])
        try write(#"["a", "list"]"#)
        XCTAssertEqual(DictationAliasWriter.load(from: url), [:])
        try write(#"{"aliases": ["not", "a", "table"]}"#)
        XCTAssertEqual(DictationAliasWriter.load(from: url), [:])
    }

    // ─── Writing keeps what it does not own ───

    func testMergeKeepsTheUsersRowsTombstonesSchemaAndUnknownKeys() throws {
        let withExtra = windowsFile.replacingOccurrences(
            of: #""removed""#, with: #""future": {"kept": true}, "removed""#)
        try write(withExtra)
        let added = try DictationAliasWriter.merge(adding: ["arch face": "ArcFace", "jasper": "Other"], at: url)

        XCTAssertEqual(added, ["arch face"], "an existing row is never replaced")
        let saved = try object()
        XCTAssertEqual(saved["aliases"] as? [String: String],
                       ["jasper": "Jasper", "bad forge": "PadForge", "arch face": "ArcFace"])
        XCTAssertEqual(saved["removed"] as? [String], ["mies"])
        XCTAssertEqual(saved["schema"] as? String, "spoken_aliases/0.1.0")
        XCTAssertEqual((saved["future"] as? [String: Any])?["kept"] as? Bool, true)
    }

    func testOverwriteRoundTripsTheRemovedList() throws {
        try write(windowsFile)
        var table = DictationAliasWriter.load(from: url)
        table["bad forge"] = nil
        table["fable nodes"] = "Fable Notes"
        try DictationAliasWriter.overwrite(table, at: url)

        XCTAssertEqual(DictationAliasWriter.load(from: url),
                       ["jasper": "Jasper", "fable nodes": "Fable Notes"])
        let saved = try object()
        XCTAssertEqual(saved["removed"] as? [String], ["mies"],
                       "dropping it would bring back a default the user removed")
        XCTAssertEqual(saved["schema"] as? String, "spoken_aliases/0.1.0")
    }

    func testAFlatFileBecomesWrappedWithEveryRowKept() throws {
        try write(#"{"jasper": "Jasper"}"#)
        try DictationAliasWriter.merge(adding: ["mies": "Miles"], at: url)

        let saved = try object()
        XCTAssertEqual(saved["aliases"] as? [String: String], ["jasper": "Jasper", "mies": "Miles"])
        XCTAssertNil(saved["jasper"], "the old rows move under aliases, not beside it")
        XCTAssertNil(saved["removed"], "no tombstones are invented")
        XCTAssertEqual(saved["schema"] as? String, DictationAliasWriter.schemaVersion)
    }

    func testANewFileIsWrappedWithTheWorkersSchema() throws {
        try DictationAliasWriter.merge(adding: ["Jasper ": "Jasper"], at: url)
        let saved = try object()
        XCTAssertEqual(saved["aliases"] as? [String: String], ["jasper": "Jasper"])
        XCTAssertEqual(saved["schema"] as? String, "spoken_aliases/0.1.0")
    }

    func testTheDefaultPathIsRefusedUnderTest() {
        XCTAssertThrowsError(try DictationAliasWriter.merge(adding: ["a b": "AB"])) { error in
            XCTAssertEqual(error as? DictationAliasWriter.Failure, .refusedUnderTest)
        }
        XCTAssertThrowsError(try DictationAliasWriter.overwrite([:])) { error in
            XCTAssertEqual(error as? DictationAliasWriter.Failure, .refusedUnderTest)
        }
    }
}
