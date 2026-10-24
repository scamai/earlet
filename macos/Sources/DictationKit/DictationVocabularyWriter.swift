import Foundation
// `NSClassFromString` lives in ObjectiveC's runtime shim, re-exported by
// Foundation on Darwin; imported explicitly so the test guard cannot silently
// stop resolving if Foundation's re-export changes.
import ObjectiveC

/// Writes the bias vocabulary back to the file the user owns.
///
/// ## Why a writer exists now
///
/// `DictationBiasVocabularyLoader` has only ever read. The vocabulary was
/// something the user edited by hand, so nothing in the app needed to write it
/// and nothing did. The demo changes that: its whole claim is that terms
/// scanned out of a document become terms the recogniser can hear, and the
/// only thing standing between those two halves is this file.
///
/// The recogniser side is already live — `DictationAsrTransport` re-reads the
/// vocabulary whenever its mtime changes, so a term written here reaches the
/// very next utterance with no restart. This is the other half of that wire.
///
/// ## Two things it must never do
///
/// **It must never lose the user's own terms.** A writer that replaced the
/// file would destroy them silently. So the only additive entry point is
/// `merge`, which is a union; a replacing write exists but is spelled
/// `overwrite` and is used by the demo solely to restore its own snapshot.
///
/// **It must never leave a half-written file.** The reader treats malformed
/// JSON as an empty vocabulary — the right failure mode for a missing file,
/// and a catastrophic one for a torn write, because dictation would silently
/// lose every private term with no error anywhere. Writes therefore go to a
/// sibling temp file and land with an atomic replace.
public enum DictationVocabularyWriter {

    public enum Failure: Error, Equatable {
        case noVocabularyLocation
        case notWritable(String)
        case encodingFailed
        /// A test tried to write the real user's vocabulary. See `isUnderTest`.
        case refusedUnderTest
    }

    /// True when running inside XCTest.
    ///
    /// ## Why this guard exists
    ///
    /// This writer must not replace a user's saved vocabulary with a fixture
    /// during a test run.
    ///
    /// The path was `DemoStageViewModel.restoreOrphanedSnapshot(sidecar:)`. The
    /// test passed a temp-directory sidecar and the function read it — then
    /// wrote the result to the DEFAULT vocabulary URL, because the destination
    /// was not a parameter. Every URL the test controlled was a temp path; the
    /// one it did not know about was the user's.
    ///
    /// A test suite that can reach `~/Library/Application Support` is one
    /// forgotten parameter away from deleting user data, and no amount of
    /// discipline at the call sites fixes that — the snapshot-and-restore
    /// safety this very demo is built around lived *inside* the code under
    /// test, so it protected the demo and not the user.
    ///
    /// So the choke point refuses. Under XCTest, every write must name its
    /// destination explicitly; the default path is unreachable.
    ///
    /// ## Detection, and the version of it that did not work
    ///
    /// The first attempt read `XCTestConfigurationFilePath` from the
    /// environment. **SwiftPM's XCTest runner does not set it on this
    /// toolchain**, so the guard was inert and the very next test run wrote
    /// `["Canary"]` over the restored vocabulary — a second destruction, by
    /// the test written to prove the first could not happen again.
    ///
    /// `NSClassFromString("XCTestCase")` asks a different question: is the
    /// XCTest runtime *loaded in this process*. It cannot be, in the shipped
    /// app, because nothing links XCTest; it always is in a test bundle,
    /// because that is what runs the tests. No environment variable, no
    /// launcher convention, nothing a toolchain can decline to set.
    ///
    /// This is the third guard in one day whose failure mode was "I did not
    /// check it fired". It has its own test, and that test asserts on the real
    /// file's bytes rather than on the guard's return value.
    static var isUnderTest: Bool {
        NSClassFromString("XCTestCase") != nil
    }

    /// Adds phrases the file does not already have, preserving existing order.
    ///
    /// Existing entries keep their position because `BiasTrie` assigns phrase
    /// ids by index and the research module's provenance hashes the list —
    /// reordering a shared prefix would invalidate a comparison against
    /// numbers measured on the old order for no benefit.
    ///
    /// Matching is case-insensitive: adding a lower-case form when a stylized
    /// term is
    /// already declared must not produce two entries that differ only in case,
    /// which would spend two slots against `maxPhrases` and hand the trie two
    /// ids for one word. The DECLARED form wins — see `declared_casing.py`,
    /// which exists to put that exact spelling back into the transcript.
    ///
    /// Returns what actually changed, so a caller can tell "nothing to do"
    /// from "wrote 40 terms" — the demo needs that to know whether the word
    /// cloud has anything to ignite.
    @discardableResult
    public static func merge(adding phrases: [String],
                             at url: URL? = nil,
                             limit: Int = DictationBiasVocabulary.maxPhrases,
                             fileManager: FileManager = .default) throws -> MergeResult {
        if url == nil, isUnderTest { throw Failure.refusedUnderTest }
        guard let url = url ?? DictationBiasVocabularyLoader.defaultURL(fileManager: fileManager)
        else { throw Failure.noVocabularyLocation }

        let existing = DictationBiasVocabularyLoader.load(from: url, fileManager: fileManager)
        var seen = Set(existing.phrases.map { $0.lowercased() })
        var merged = existing.phrases
        var added: [String] = []

        for phrase in phrases {
            let trimmed = phrase.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty, seen.insert(trimmed.lowercased()).inserted else { continue }
            merged.append(trimmed)
            added.append(trimmed)
        }

        // Truncation is reported rather than silent, matching `bounded(to:)`.
        // A demo that quietly dropped the term it was about to dictate would
        // fail in the least explicable way possible.
        var dropped = 0
        if merged.count > limit {
            dropped = merged.count - limit
            merged = Array(merged.prefix(limit))
            added = added.filter { phrase in
                merged.contains { $0.caseInsensitiveCompare(phrase) == .orderedSame }
            }
        }

        if added.isEmpty && dropped == 0 {
            return MergeResult(added: [], total: merged.count, dropped: 0, wrote: false)
        }
        try overwrite(DictationBiasVocabulary(phrases: merged), at: url, fileManager: fileManager)
        return MergeResult(added: added, total: merged.count, dropped: dropped, wrote: true)
    }

    public struct MergeResult: Equatable, Sendable {
        public let added: [String]
        public let total: Int
        public let dropped: Int
        /// False when the merge was a no-op. The caller can skip a "vocabulary
        /// updated" message rather than claiming work that did not happen.
        public let wrote: Bool
    }

    /// Replaces the file wholesale. Used to restore a snapshot, not to add.
    ///
    /// Named to be conspicuous at the call site: anything reaching for this
    /// when it means `merge` is about to delete the user's vocabulary, and the
    /// name is the last chance to notice.
    public static func overwrite(_ vocabulary: DictationBiasVocabulary,
                                 at url: URL? = nil,
                                 fileManager: FileManager = .default) throws {
        // A test that did not name a destination is about to write the real
        // user's vocabulary. That has happened once and cost 32 curated terms.
        if url == nil, isUnderTest { throw Failure.refusedUnderTest }
        guard let url = url ?? DictationBiasVocabularyLoader.defaultURL(fileManager: fileManager)
        else { throw Failure.noVocabularyLocation }

        let directory = url.deletingLastPathComponent()
        if !fileManager.fileExists(atPath: directory.path) {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        }

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(Payload(phrases: vocabulary.phrases)) else {
            throw Failure.encodingFailed
        }

        // A sibling temp file, so the replace is a same-volume rename and
        // cannot tear. Writing in place would leave a window in which the
        // reader sees truncated JSON and silently returns an EMPTY vocabulary
        // — every private term gone, with nothing logged anywhere.
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

    /// Reads the current vocabulary so a caller can put it back later.
    ///
    /// The demo takes one of these before it adds anything and restores it on
    /// exit, so a rehearsal never permanently edits the user's vocabulary and
    /// two rehearsals in a row start from the same place. An absent file
    /// snapshots as empty, and restoring that writes an empty file rather than
    /// deleting — the reader treats both identically, and writing is the
    /// operation whose failure mode we have actually tested.
    public static func snapshot(at url: URL? = nil,
                                fileManager: FileManager = .default) -> DictationBiasVocabulary {
        guard let url = url ?? DictationBiasVocabularyLoader.defaultURL(fileManager: fileManager)
        else { return .empty }
        return DictationBiasVocabularyLoader.load(from: url, fileManager: fileManager)
    }

    private struct Payload: Encodable { let phrases: [String] }
}
