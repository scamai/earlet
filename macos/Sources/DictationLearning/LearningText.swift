import Foundation

// The text primitives of the learning policy (LEARNING.md §3.1), ported from
// `tests/learning_reference.py`. Everything here works on Unicode scalars —
// code points — because that is what the reference indexes and compares, and
// what C# `Rune` enumeration gives: the three implementations must cut the same
// substrings and agree on every comparison.
//
// Two Swift defaults would silently disagree with the reference, so neither is
// used for policy decisions:
//
// - `String ==`, `<` and hashing follow canonical equivalence ("é" precomposed
//   equals "e" + U+0301). The reference compares code points exactly. Use
//   `LearningText.same` / `precedes` / `ExactText` instead.
// - `String.lowercased()` maps each scalar alone. Python's `str.lower()` also
//   applies the Final_Sigma rule ("ΟΔΟΣ" → "οδος", not "οδοσ"). Use
//   `LearningText.lower`.

// ─── Knobs (LEARNING.md §3-§5) ───

/// The numbers LEARNING.md fixes. The same values live in
/// `tests/learning_reference.py` and the C# port; change all three together.
public enum LearningLimits {
    public static let schema = "dictation.learning/1"
    public static let maxTextTokens = 200
    public static let rewriteMinTokens = 4
    public static let maxLatinTokens = 4
    public static let maxCJKChars = 8
    public static let maxDistanceRatio = 0.5
    public static let aliasPromotionCount = 2
    public static let pendingDictationIDs = 4
    public static let pendingTTLMilliseconds: Int64 = 30 * 24 * 60 * 60 * 1000
    public static let maxPending = 500
    public static let maxLearnedWords = 100
    public static let maxTaughtWords = 500
    public static let maxAliases = 200
    public static let maxTombstones = 1000
    public static let maxTaughtChars = 60
    public static let maxTaughtLatinTokens = 6
    public static let maxTaughtCJKChars = 12
    /// The shortest base a verb ending may leave (LEARNING.md §2).
    public static let minVerbBase = 3
}

typealias Scalars = [Unicode.Scalar]

// ─── Tokens (LEARNING.md §3.1) ───

/// One token: a CJK code point on its own, or a maximal run of word
/// characters. `start` and `end` are code-point offsets into the source text.
struct LearningToken: Equatable, Sendable {
    let scalars: Scalars
    let start: Int
    let end: Int
    let cjk: Bool

    var text: String { LearningText.string(scalars) }
}

enum LearningText {

    /// `' ’ - . _` — a token continues across one of these when a word
    /// character follows it (`don't`, `Node.js`).
    static func isJoiner(_ c: Unicode.Scalar) -> Bool {
        switch c.value {
        case 0x27, 0x2019, 0x2D, 0x2E, 0x5F: return true
        default: return false
        }
    }

    static func isApostrophe(_ c: Unicode.Scalar) -> Bool {
        c.value == 0x27 || c.value == 0x2019
    }

    /// Hiragana/Katakana, CJK Extension A, CJK Unified, Hangul syllables,
    /// CJK Compatibility, and Extensions B-F. Checked by range only, as the
    /// reference does: punctuation inside a range is still a CJK token.
    static func isCJK(_ c: Unicode.Scalar) -> Bool {
        switch c.value {
        case 0x3040...0x30FF, 0x3400...0x4DBF, 0x4E00...0x9FFF,
             0xAC00...0xD7AF, 0xF900...0xFAFF, 0x20000...0x2FFFF:
            return true
        default:
            return false
        }
    }

    /// Letters, marks and decimal digits (`L*`, `M*`, `Nd`) outside the CJK
    /// ranges.
    static func isWordChar(_ c: Unicode.Scalar) -> Bool {
        guard !isCJK(c) else { return false }
        switch c.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter,
             .nonspacingMark, .spacingMark, .enclosingMark, .decimalNumber:
            return true
        default:
            return false
        }
    }

    static func tokenize(_ text: String) -> [LearningToken] {
        tokenize(Array(text.unicodeScalars))
    }

    static func tokenize(_ cps: Scalars) -> [LearningToken] {
        var tokens: [LearningToken] = []
        var i = 0
        let n = cps.count
        while i < n {
            let ch = cps[i]
            if isCJK(ch) {
                tokens.append(LearningToken(scalars: [ch], start: i, end: i + 1, cjk: true))
                i += 1
                continue
            }
            if isWordChar(ch) {
                var j = i + 1
                while j < n {
                    if isWordChar(cps[j]) {
                        j += 1
                    } else if isJoiner(cps[j]), j + 1 < n, isWordChar(cps[j + 1]) {
                        j += 2
                    } else {
                        break
                    }
                }
                tokens.append(LearningToken(scalars: Array(cps[i..<j]), start: i, end: j, cjk: false))
                i = j
                continue
            }
            i += 1
        }
        return tokens
    }

    // ─── Python str predicates on one code point ───

    /// `str.islower()`: the Unicode Lowercase property.
    static func isLower(_ c: Unicode.Scalar) -> Bool { c.properties.isLowercase }

    /// `str.isupper()`: the Unicode Uppercase property.
    static func isUpper(_ c: Unicode.Scalar) -> Bool { c.properties.isUppercase }

    /// `str.isdigit()`: Numeric_Type Decimal or Digit (so `²` counts).
    static func isDigit(_ c: Unicode.Scalar) -> Bool {
        switch c.properties.numericType {
        case .decimal?, .digit?: return true
        default: return false
        }
    }

    /// `str.isalpha()`: general category Lu, Ll, Lt, Lm or Lo.
    static func isAlpha(_ c: Unicode.Scalar) -> Bool {
        switch c.properties.generalCategory {
        case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter, .modifierLetter, .otherLetter:
            return true
        default:
            return false
        }
    }

    /// The reference's `has_letter`: some code point is a letter (`L*`). A
    /// text without one (`10`, `4417`, `1-800`) is a number, never vocabulary
    /// (LEARNING.md §2, §4.2).
    static func hasLetter<S: Sequence>(_ text: S) -> Bool where S.Element == Unicode.Scalar {
        text.contains(where: isAlpha)
    }

    static func hasLetter(_ text: String) -> Bool {
        hasLetter(text.unicodeScalars)
    }

    /// `str.isspace()`: category Zs or bidi class WS, B or S. That is the
    /// White_Space property plus U+001C–U+001F, which Python also splits on.
    static func isSpace(_ c: Unicode.Scalar) -> Bool {
        switch c.value {
        case 0x09...0x0D, 0x1C...0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A,
             0x2028, 0x2029, 0x202F, 0x205F, 0x3000:
            return true
        default:
            return false
        }
    }

    // ─── Python str operations ───

    /// `str.lower()`: the full lowercase mapping of each code point, plus the
    /// Final_Sigma rule for U+03A3 exactly as CPython applies it.
    static func lower(_ s: Scalars) -> Scalars {
        var out = Scalars()
        out.reserveCapacity(s.count)
        for (i, c) in s.enumerated() {
            if c.value < 0x80 {
                out.append(c.value >= 0x41 && c.value <= 0x5A ? Unicode.Scalar(UInt8(c.value + 0x20)) : c)
            } else if c.value == 0x3A3 {
                out.append(isFinalSigma(s, at: i) ? "\u{3C2}" : "\u{3C3}")
            } else {
                out.append(contentsOf: c.properties.lowercaseMapping.unicodeScalars)
            }
        }
        return out
    }

    static func lower(_ s: String) -> String {
        string(lower(Array(s.unicodeScalars)))
    }

    /// `\p{cased}\p{case-ignorable}* Σ !(\p{case-ignorable}* \p{cased})`,
    /// CPython's `handle_capital_sigma`.
    private static func isFinalSigma(_ s: Scalars, at i: Int) -> Bool {
        var j = i - 1
        while j >= 0, s[j].properties.isCaseIgnorable { j -= 1 }
        guard j >= 0, s[j].properties.isCased else { return false }
        var k = i + 1
        while k < s.count, s[k].properties.isCaseIgnorable { k += 1 }
        return k == s.count || !s[k].properties.isCased
    }

    /// `str.strip()` with no argument.
    static func strip(_ s: String) -> String {
        let cps = Array(s.unicodeScalars)
        guard let first = cps.firstIndex(where: { !isSpace($0) }),
              let last = cps.lastIndex(where: { !isSpace($0) })
        else { return "" }
        return string(cps[first...last])
    }

    /// `str.split()` with no argument: runs of whitespace separate, and
    /// leading/trailing whitespace yields no empty parts.
    static func split(_ s: Scalars) -> [Scalars] {
        var parts: [Scalars] = []
        var part = Scalars()
        for c in s {
            if isSpace(c) {
                if !part.isEmpty { parts.append(part); part.removeAll() }
            } else {
                part.append(c)
            }
        }
        if !part.isEmpty { parts.append(part) }
        return parts
    }

    static func string<S: Sequence>(_ scalars: S) -> String where S.Element == Unicode.Scalar {
        var view = String.UnicodeScalarView()
        view.append(contentsOf: scalars)
        return String(view)
    }

    /// Exact code-point equality (UTF-8 is a bijection with code points).
    static func same(_ a: String, _ b: String) -> Bool {
        a.utf8.elementsEqual(b.utf8)
    }

    /// Code-point order, which is Python's `str` order. UTF-8 byte order
    /// preserves it.
    static func precedes(_ a: String, _ b: String) -> Bool {
        a.utf8.lexicographicallyPrecedes(b.utf8)
    }

    // ─── Derived forms (LEARNING.md §3.3, §4.2) ───

    static func stripJoiners(_ s: Scalars) -> Scalars {
        s.filter { !isJoiner($0) }
    }

    /// Lowercased tokens with joiners removed, concatenated.
    static func squash(_ text: String) -> Scalars {
        tokenize(text).flatMap { stripJoiners(lower($0.scalars)) }
    }

    /// Lowercased tokens with joiners removed, joined by single spaces.
    static func normSpace(_ text: String) -> Scalars {
        var out = Scalars()
        for (i, token) in tokenize(text).enumerated() {
            if i > 0 { out.append(" ") }
            out.append(contentsOf: stripJoiners(lower(token.scalars)))
        }
        return out
    }

    /// The key an alias is stored and matched under: lowercase, whitespace
    /// collapsed to single spaces.
    static func heardKey(_ text: String) -> String {
        let parts = split(lower(Array(text.unicodeScalars)))
        var out = Scalars()
        for (i, part) in parts.enumerated() {
            if i > 0 { out.append(" ") }
            out.append(contentsOf: part)
        }
        return string(out)
    }

    static func levenshtein(_ a: Scalars, _ b: Scalars) -> Int {
        var prev = Array(0...b.count)
        for (i, ca) in a.enumerated() {
            var cur = [i + 1]
            cur.reserveCapacity(b.count + 1)
            for (j, cb) in b.enumerated() {
                cur.append(min(prev[j + 1] + 1, cur[j] + 1, prev[j] + (ca == cb ? 0 : 1)))
            }
            prev = cur
        }
        return prev[b.count]
    }
}

/// A `String` compared and hashed by its exact code points, for sets and
/// lookups where Swift's canonical-equivalence `String ==` would disagree with
/// the reference.
struct ExactText: Hashable, Sendable {
    let value: String

    init(_ value: String) { self.value = value }

    static func == (lhs: ExactText, rhs: ExactText) -> Bool {
        LearningText.same(lhs.value, rhs.value)
    }

    func hash(into hasher: inout Hasher) {
        var copy = value
        copy.withUTF8 { hasher.combine(bytes: UnsafeRawBufferPointer($0)) }
    }
}

extension Array {
    /// A sort that keeps equal elements in their current order, as Python's
    /// `list.sort` does. The standard library does not promise stability, and
    /// tie order decides which tombstone a cap drops.
    func stableSorted(by areInIncreasingOrder: (Element, Element) -> Bool) -> [Element] {
        enumerated()
            .sorted { lhs, rhs in
                if areInIncreasingOrder(lhs.element, rhs.element) { return true }
                if areInIncreasingOrder(rhs.element, lhs.element) { return false }
                return lhs.offset < rhs.offset
            }
            .map(\.element)
    }
}
