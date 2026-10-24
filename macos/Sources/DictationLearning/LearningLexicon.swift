import Foundation

// ─── Lexicon (LEARNING.md §2) ───

/// The ordinary-word test the policy asks of a token.
///
/// An unavailable lexicon FAILS CLOSED: every token is ordinary, so nothing is
/// distinctive and no alias is admitted. Taught entries still work. The
/// alternative — failing open — would make every
/// word distinctive when the list is missing, and a single edit of "a" to "the"
/// would start learning.
public protocol LearningLexicon: Sendable {
    /// False when the word list could not be read.
    var isAvailable: Bool { get }
    func isOrdinary(_ token: String) -> Bool
}

/// The word set, the three plural rules and the -ed/-ing endings. Shared by
/// every lexicon so the rules exist once.
struct OrdinaryWords: Sendable {
    private let words: Set<ExactText>
    private let excluded: Set<ExactText>

    /// Each entry is stripped and lowercased; blank entries are skipped.
    init<S: Sequence>(_ entries: S, excluding: Set<ExactText> = []) where S.Element == String {
        var words = Set<ExactText>()
        for entry in entries {
            let trimmed = LearningText.strip(entry)
            if !trimmed.isEmpty { words.insert(ExactText(LearningText.lower(trimmed))) }
        }
        self.words = words
        self.excluded = excluding
    }

    /// One entry per whitespace-separated word, which is how the Windows
    /// resource and `/usr/share/dict/web2` are read alike.
    init(splitting text: String) {
        self.init(LearningText.split(Array(text.unicodeScalars)).map { LearningText.string($0) })
    }

    var count: Int { words.count }
    var isAvailable: Bool { !words.isEmpty }

    private func has(_ word: Scalars) -> Bool {
        words.contains(ExactText(LearningText.string(word)))
    }

    /// Directly, or after `-ies`→`-y`, dropping `-es`, or dropping `-s`.
    private func base(_ word: Scalars) -> Bool {
        // An excluded proper name wins over plural folding: Miles otherwise
        // matches the ordinary base "mile" in a synthetic regression case.
        if excluded.contains(ExactText(LearningText.string(word))) { return false }
        if has(word) { return true }
        if word.count > 3, word.suffix(3).elementsEqual("ies".unicodeScalars),
           has(Array(word.dropLast(3)) + ["y"]) { return true }
        if word.count > 2, word.suffix(2).elementsEqual("es".unicodeScalars),
           has(Array(word.dropLast(2))) { return true }
        return word.count > 1 && word.last == "s" && has(Array(word.dropLast(1)))
    }

    /// `changed`, `running`, `tried`: a verb ending dropped. The base is the
    /// stem, the stem plus `e` (`chang|ed`), the stem less one of a doubled
    /// final consonant (`runn|ing`) or, before -ed, the stem with its final
    /// `i` turned back into `y` (`tri|ed`). It is looked up with the plural
    /// rules and must have at least 3 code points, so `Ying` and `Xing` are
    /// not read as a letter plus -ing (LEARNING.md §2). An excluded name is
    /// checked first, as in `base`: `Downing` must not fold to `down`.
    private func verbBase(_ word: Scalars) -> Bool {
        guard !excluded.contains(ExactText(LearningText.string(word))) else { return false }
        for suffix in ["ed", "ing"] {
            let ending = Array(suffix.unicodeScalars)
            guard word.count >= ending.count, Array(word.suffix(ending.count)) == ending else { continue }
            let stem = Array(word.dropLast(ending.count))
            var bases = [stem, stem + ["e"]]
            if stem.count >= 2, stem[stem.count - 1] == stem[stem.count - 2],
               Self.doubledConsonants.contains(stem[stem.count - 1]) {
                bases.append(Array(stem.dropLast()))
            }
            if suffix == "ed", stem.last == "i" {
                bases.append(Array(stem.dropLast()) + ["y"])
            }
            if bases.contains(where: { $0.count >= LearningLimits.minVerbBase && base($0) }) {
                return true
            }
        }
        return false
    }

    /// A doubled final consonant before -ed or -ing (`stopped`, `running`).
    private static let doubledConsonants = Set("bcdfghjklmnpqrstvwxyz".unicodeScalars)

    /// Lowercased, cut at the first apostrophe (unless it leads), split on
    /// `-`, `.` and `_`; ordinary when every part is, directly, as a plural or
    /// with a verb ending (`changed`, `running`). A part with no letter
    /// (`10`, `4417`, `1-800`) is ordinary: a number on its own is never
    /// vocabulary, so a fix with no letter (a PIN, a phone number) is never
    /// stored or prompted. A number beside a distinctive word can be
    /// (`Miles 44171234`, after a second dictation; LEARNING.md §2).
    func isOrdinary(_ token: String) -> Bool {
        guard isAvailable else { return true }
        var word = LearningText.lower(Array(token.unicodeScalars))
        if let cut = word.firstIndex(where: LearningText.isApostrophe), cut > 0 {
            word = Array(word[..<cut])
        }
        let parts = word
            .split(omittingEmptySubsequences: true) { $0 == "-" || $0 == "." || $0 == "_" }
            .map(Array.init)
        guard !parts.isEmpty else { return true }
        return parts.allSatisfy { !LearningText.hasLetter($0) || base($0) || verbBase($0) }
    }
}

/// A lexicon over an explicit list: tests, and the shared golden cases.
public struct WordListLexicon: LearningLexicon {
    private let words: OrdinaryWords

    public init(words: [String]) {
        self.words = OrdinaryWords(words)
    }

    public var isAvailable: Bool { words.isAvailable }
    public func isOrdinary(_ token: String) -> Bool { words.isOrdinary(token) }
}

/// Webster's Second (`web2`), read from a file on first use and
/// never again.
///
/// macOS ships it at `/usr/share/dict/web2`. The Windows app embeds a curated
/// subset of the same public-domain source; the copies are intentionally not
/// byte-identical. A missing,
/// unreadable or non-UTF-8 file leaves the lexicon unavailable — fail closed —
/// for the life of the instance; nothing retries, so one bad read cannot make
/// the answer change halfway through a session.
public final class WebsterLexicon: LearningLexicon, @unchecked Sendable {

    public static let systemURL = URL(fileURLWithPath: "/usr/share/dict/web2")

    // A static let is initialised once, lazily and thread-safely, so every
    // caller of `system()` shares one instance and the file is read once per
    // process.
    private static let shared = WebsterLexicon(url: systemURL)

    /// The process-wide lexicon over `/usr/share/dict/web2`. Nothing is read
    /// until the first question.
    public static func system() -> WebsterLexicon { shared }

    public let url: URL

    private let lock = NSLock()
    // Guarded by `lock`.
    private var loaded: OrdinaryWords?

    public init(url: URL) {
        self.url = url
    }

    public var isAvailable: Bool { words().isAvailable }

    /// How many distinct words were read; 0 means the lexicon failed closed.
    public var wordCount: Int { words().count }

    public func isOrdinary(_ token: String) -> Bool { words().isOrdinary(token) }

    private func words() -> OrdinaryWords {
        lock.lock()
        defer { lock.unlock() }
        if let loaded { return loaded }
        let read = Self.read(url)
        loaded = read
        return read
    }

    private static func read(_ url: URL) -> OrdinaryWords {
        guard let data = try? Data(contentsOf: url),
              let text = String(data: data, encoding: .utf8)
        else { return OrdinaryWords([String]()) }
        // Webster contains names as well as ordinary words. Lowercasing its
        // capitalized-only entries can otherwise be indistinguishable from a
        // common word, so a confirmed Mies → Miles fix must teach the written
        // name. Only this file
        // loader filters names; explicit WordListLexicon lists keep their
        // caller-declared, case-insensitive meaning. A/a and I/i remain present.
        let entries = LearningText.split(Array(text.unicodeScalars)).map { LearningText.string($0) }
        let lowercase = entries.filter { $0 == LearningText.lower($0) }
        let ordinary = Set(lowercase.map(ExactText.init))
        let names = Set(entries.map { ExactText(LearningText.lower($0)) }).subtracting(ordinary)
        return OrdinaryWords(lowercase, excluding: names)
    }
}
