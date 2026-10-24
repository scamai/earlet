import Foundation
import ObjectiveC

/// Reads and writes the spoken-form alias table.
///
/// Same contract as `DictationVocabularyWriter`, including the guard that
/// stops a test writing the real user's file — see `isUnderTest` there for the
/// data loss that guard exists because of.
///
/// The vocabulary supplies written forms to recognition. An explicit alias
/// maps a user-declared spoken form to its written counterpart after decoding.
public enum DictationAliasWriter {

    public enum Failure: Error, Equatable {
        case noLocation
        case notWritable(String)
        case refusedUnderTest
    }

    public static func defaultURL(fileManager: FileManager = .default) -> URL? {
        guard let base = fileManager.urls(for: .applicationSupportDirectory,
                                          in: .userDomainMask).first else { return nil }
        return base
            .appendingPathComponent("Dictation", isDirectory: true)
            .appendingPathComponent("dictation", isDirectory: true)
            .appendingPathComponent("bias_aliases.json", isDirectory: false)
    }

    /// The schema string the worker's module declares
    /// (`spoken_aliases.SCHEMA_VERSION`), written into a file that has none,
    /// as the Windows writer does. The worker ignores it.
    public static let schemaVersion = "spoken_aliases/0.1.0"

    /// `{spoken: written}`. A missing or malformed file reads as empty — the
    /// failure mode must be "dictation behaves as before", never "dictation
    /// stops working". Same contract as `spoken_aliases.load()`.
    ///
    /// Two shapes, the two `spoken_aliases._read_file` reads: the wrapped
    /// file `{"schema"?, "aliases": {…}, "removed": […]}` that Windows and
    /// the worker's own docs use, and a flat `{spoken: written}` table. This
    /// used to decode the whole file as `[String: [String: String]]`, which a
    /// `removed` list or a `schema` string fails, so a wrapped file read as
    /// EMPTY, and the next `merge` rewrote it holding only the new rows: the
    /// user's aliases and tombstones were gone.
    public static func load(from url: URL? = nil,
                            fileManager: FileManager = .default) -> [String: String] {
        guard let url = url ?? defaultURL(fileManager: fileManager),
              let object = readObject(at: url) else { return [:] }
        return table(in: object)
    }

    /// The file's top-level object; `nil` when it is missing or is not a
    /// JSON object.
    static func readObject(at url: URL) -> [String: Any]? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        return (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    /// The rows: the `aliases` member of a wrapped file, or every member of
    /// a flat one, where a key spelled `removed` is somebody's alias. A row
    /// whose written form is not a string is skipped.
    static func table(in object: [String: Any]) -> [String: String] {
        let rows: [String: Any]
        if let wrapped = object["aliases"] {
            rows = wrapped as? [String: Any] ?? [:]
        } else {
            rows = object
        }
        return rows.compactMapValues { $0 as? String }
    }

    /// Adds aliases the table does not already have. Never replaces an
    /// existing mapping: the user's own corrections outrank anything the demo
    /// seeds, and a demo that silently retargeted one of his aliases would be
    /// changing dictation behaviour he tuned deliberately.
    @discardableResult
    public static func merge(adding aliases: [String: String],
                             at url: URL? = nil,
                             fileManager: FileManager = .default) throws -> [String] {
        if url == nil, DictationVocabularyWriter.isUnderTest { throw Failure.refusedUnderTest }
        guard let url = url ?? defaultURL(fileManager: fileManager) else { throw Failure.noLocation }
        var table = load(from: url, fileManager: fileManager)
        var added: [String] = []
        for (spoken, written) in aliases {
            let key = spoken.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            guard !key.isEmpty, table[key] == nil else { continue }
            table[key] = written
            added.append(key)
        }
        guard !added.isEmpty else { return [] }
        try overwrite(table, at: url, fileManager: fileManager)
        return added.sorted()
    }

    /// Replaces the rows and keeps everything else a wrapped file holds:
    /// `removed` (shipped defaults the user deleted on purpose, which come
    /// back if it is dropped), `schema`, and any member a newer writer added.
    /// A flat file becomes a wrapped one; every key in it was a row.
    public static func overwrite(_ table: [String: String],
                                 at url: URL? = nil,
                                 fileManager: FileManager = .default) throws {
        if url == nil, DictationVocabularyWriter.isUnderTest { throw Failure.refusedUnderTest }
        guard let url = url ?? defaultURL(fileManager: fileManager) else { throw Failure.noLocation }
        let directory = url.deletingLastPathComponent()
        if !fileManager.fileExists(atPath: directory.path) {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        }
        var object: [String: Any] = [:]
        if let existing = readObject(at: url), existing["aliases"] != nil {
            object = existing
        }
        object["aliases"] = table
        if object["schema"] == nil { object["schema"] = schemaVersion }
        guard JSONSerialization.isValidJSONObject(object),
              let data = try? JSONSerialization.data(
                withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        else {
            throw Failure.notWritable("encoding failed")
        }
        // Atomic replace via a sibling temp, for the same reason the
        // vocabulary writer does it: a torn write reads back as an EMPTY
        // table and silently disables every alias with nothing logged.
        let temp = directory.appendingPathComponent(
            ".\(url.lastPathComponent).\(UUID().uuidString).tmp", isDirectory: false)
        do {
            try data.write(to: temp, options: .atomic)
            _ = try fileManager.replaceItemAt(url, withItemAt: temp)
        } catch {
            try? fileManager.removeItem(at: temp)
            throw Failure.notWritable(error.localizedDescription)
        }
    }
}
