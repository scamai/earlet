import Foundation

// ─── Candidates (LEARNING.md §3.2-3.3) ───

/// A correction the policy may learn from. `heard` and `meant` are the original
/// substrings of the dictated and edited text, spacing inside the span kept.
public struct LearningCandidate: Equatable, Sendable {
    public enum Kind: String, Sendable {
        case casing, respacing, spelling, transliteration
    }

    public let heard: String
    public let meant: String
    public let kind: Kind

    public init(heard: String, meant: String, kind: Kind) {
        self.heard = heard
        self.meant = meant
        self.kind = kind
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.kind == rhs.kind && LearningText.same(lhs.heard, rhs.heard) && LearningText.same(lhs.meant, rhs.meant)
    }
}

/// A correction the policy refuses to learn from, and why.
public struct LearningRejection: Equatable, Sendable {
    public enum Reason: String, Sendable {
        case tooLong = "too_long", punctuation, inflection, rewording
        /// Pinyin or romaji an input method has not committed yet.
        case composition
        /// A hunk that reaches an end of both texts and grows there: text typed
        /// after (or before) the span, which cannot be told from a fix.
        case boundary
    }

    public let heard: String
    public let meant: String
    public let reason: Reason

    public init(heard: String, meant: String, reason: Reason) {
        self.heard = heard
        self.meant = meant
        self.reason = reason
    }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.reason == rhs.reason && LearningText.same(lhs.heard, rhs.heard) && LearningText.same(lhs.meant, rhs.meant)
    }
}

extension LearningLimits {
    /// A token this long, with `secretMinDigits` digits, is secret-shaped
    /// (LEARNING.md §4.1).
    public static let secretMinLength = 8
    public static let secretMinDigits = 2
}

public enum LearningPolicy {

    public enum Classification: Equatable, Sendable {
        case candidate(LearningCandidate)
        case rejection(LearningRejection)
    }

    struct Hunk {
        var dels: [Int]
        var ins: [Int]
    }

    /// Longest common subsequence over token texts, compared exactly. On a
    /// mismatch the dictated token is dropped when that keeps the LCS at least
    /// as long as dropping the edited one. Returns the LCS length and the
    /// maximal runs of unmatched tokens between matches.
    static func hunks(_ a: [Scalars], _ b: [Scalars]) -> (matches: Int, hunks: [Hunk]) {
        let n = a.count
        let m = b.count
        let width = m + 1
        var table = [Int](repeating: 0, count: (n + 1) * width)
        if n > 0, m > 0 {
            for i in stride(from: n - 1, through: 0, by: -1) {
                for j in stride(from: m - 1, through: 0, by: -1) {
                    table[i * width + j] = a[i] == b[j]
                        ? table[(i + 1) * width + j + 1] + 1
                        : max(table[(i + 1) * width + j], table[i * width + j + 1])
                }
            }
        }
        var hunks: [Hunk] = []
        var current = Hunk(dels: [], ins: [])
        func flush() {
            if !current.dels.isEmpty || !current.ins.isEmpty {
                hunks.append(current)
                current = Hunk(dels: [], ins: [])
            }
        }
        var i = 0
        var j = 0
        while i < n, j < m {
            if a[i] == b[j] {
                flush()
                i += 1
                j += 1
            } else if table[(i + 1) * width + j] >= table[i * width + j + 1] {
                current.dels.append(i)
                i += 1
            } else {
                current.ins.append(j)
                j += 1
            }
        }
        current.dels.append(contentsOf: i..<n)
        current.ins.append(contentsOf: j..<m)
        flush()
        return (table[0], hunks)
    }

    private static func latinCount(_ tokens: [LearningToken]) -> Int {
        tokens.reduce(0) { $0 + ($1.cjk ? 0 : 1) }
    }

    private static func cjkCount(_ tokens: [LearningToken]) -> Int {
        tokens.reduce(0) { $0 + ($1.cjk ? 1 : 0) }
    }

    /// The table in LEARNING.md §3.3; the first matching row wins.
    public static func classify(heard: String, meant: String, lexicon: any LearningLexicon) -> Classification {
        let ht = LearningText.tokenize(heard)
        let mt = LearningText.tokenize(meant)
        func reject(_ reason: LearningRejection.Reason) -> Classification {
            .rejection(LearningRejection(heard: heard, meant: meant, reason: reason))
        }
        func accept(_ kind: LearningCandidate.Kind) -> Classification {
            .candidate(LearningCandidate(heard: heard, meant: meant, kind: kind))
        }
        if latinCount(ht) > LearningLimits.maxLatinTokens || latinCount(mt) > LearningLimits.maxLatinTokens
            || cjkCount(ht) > LearningLimits.maxCJKChars || cjkCount(mt) > LearningLimits.maxCJKChars {
            return reject(.tooLong)
        }
        if LearningText.lower(Array(heard.unicodeScalars)) == LearningText.lower(Array(meant.unicodeScalars)) {
            return accept(.casing)
        }
        if LearningText.normSpace(heard) == LearningText.normSpace(meant) {
            return reject(.punctuation)
        }
        let sh = LearningText.squash(heard)
        let sm = LearningText.squash(meant)
        if sh == sm {
            return accept(.respacing)
        }
        let shorter = sh.count <= sm.count ? ht : mt
        let plural: (Scalars, Scalars) -> Bool = { long, short in
            long == short + ["s"] || long == short + ["e", "s"]
        }
        if plural(sm, sh) || plural(sh, sm),
           shorter.filter({ !$0.cjk }).allSatisfy({ lexicon.isOrdinary($0.text) }) {
            return reject(.inflection)
        }
        let heardCJK = ht.contains(where: \.cjk)
        if heardCJK != mt.contains(where: \.cjk) {
            if heardCJK, looksLikeComposition(meant) {
                return reject(.composition)
            }
            return accept(.transliteration)
        }
        let distance = Double(LearningText.levenshtein(sh, sm)) / Double(max(sh.count, sm.count))
        if distance > LearningLimits.maxDistanceRatio {
            // A recognizer can substitute an ordinary word for an unfamiliar
            // name or technical term (wrapper -> repo). Remember that single
            // word even when its spelling is distant. learnsAlias still refuses
            // an ordinary heard token, so this never rewrites every "wrapper".
            if ht.count == 1, mt.count == 1, !ht[0].cjk, !mt[0].cjk,
               lexicon.isOrdinary(ht[0].text), isDistinctive(meant, lexicon: lexicon) {
                return accept(.spelling)
            }
            return reject(.rewording)
        }
        return accept(.spelling)
    }

    /// Lowercase ASCII letters, apostrophes and spaces only, with a letter:
    /// what an input method shows (pinyin, romaji) before the user commits the
    /// characters.
    static func looksLikeComposition(_ text: String) -> Bool {
        LearningText.hasLetter(text) && text.unicodeScalars.allSatisfy { c in
            (c.value >= 0x61 && c.value <= 0x7A) || c.value == 0x27 || c.value == 0x20
        }
    }

    /// Candidates and rejections for one dictation, in text order, each once.
    public static func extract(dictated: String, edited: String, lexicon: any LearningLexicon)
        -> (candidates: [LearningCandidate], rejections: [LearningRejection]) {
        let dcp = Array(dictated.unicodeScalars)
        let ecp = Array(edited.unicodeScalars)
        let a = LearningText.tokenize(dcp)
        let b = LearningText.tokenize(ecp)
        if a.isEmpty || a.count > LearningLimits.maxTextTokens || b.count > LearningLimits.maxTextTokens {
            return ([], [])
        }
        let diff = hunks(a.map(\.scalars), b.map(\.scalars))
        // A dictation the user rewrote rather than corrected teaches nothing.
        if a.count >= LearningLimits.rewriteMinTokens, diff.matches * 2 < a.count {
            return ([], [])
        }
        var candidates: [LearningCandidate] = []
        var rejections: [LearningRejection] = []
        for hunk in diff.hunks {
            guard let firstDel = hunk.dels.first, let lastDel = hunk.dels.last,
                  let firstIns = hunk.ins.first, let lastIns = hunk.ins.last
            else { continue } // pure insertions and deletions are ignored
            let heard = LearningText.string(dcp[a[firstDel].start..<a[lastDel].end])
            let meant = LearningText.string(ecp[b[firstIns].start..<b[lastIns].end])
            // Text typed straight after (or before) the span joins the last
            // (first) hunk: "Call trovio" -> "Call Trovioo now". A hunk that
            // reaches an end of both texts and grows there cannot be told from
            // a continuation, so it is refused rather than learned.
            let grows = hunk.ins.count > hunk.dels.count
            let atEnd = lastDel == a.count - 1 && lastIns == b.count - 1
            let atStart = firstDel == 0 && firstIns == 0
            let result: Classification = grows && (atEnd || atStart)
                ? .rejection(LearningRejection(heard: heard, meant: meant, reason: .boundary))
                : classify(heard: heard, meant: meant, lexicon: lexicon)
            switch result {
            case .candidate(let candidate):
                if !candidates.contains(candidate) { candidates.append(candidate) }
            case .rejection(let rejection):
                if !rejections.contains(rejection) { rejections.append(rejection) }
            }
        }
        return (candidates, rejections)
    }

    // ─── Admission (LEARNING.md §4.1-4.2) ───

    private static func hasInternalUpper(_ text: Scalars) -> Bool {
        text.contains(where: LearningText.isLower) && text.dropFirst().contains(where: LearningText.isUpper)
    }

    private static func mixesDigitsAndLetters(_ text: Scalars) -> Bool {
        text.contains(where: LearningText.isDigit) && text.contains(where: LearningText.isAlpha)
    }

    /// At least one non-CJK token has an internal capital next to lowercase
    /// letters (`SkyDock`), mixes letters and digits (`GPT4`), or is not an
    /// ordinary word. CJK-only text never is: there is no lexicon for it.
    public static func isDistinctive(_ text: String, lexicon: any LearningLexicon) -> Bool {
        LearningText.tokenize(text).contains { token in
            !token.cjk && (hasInternalUpper(token.scalars) || mixesDigitsAndLetters(token.scalars)
                || !lexicon.isOrdinary(token.text))
        }
    }

    /// Could `text` be a password or key typed over a dictated word? A token of
    /// at least 8 code points with at least 2 digits (`Sunshine42`, or the
    /// account number in `Miles 44171234`), or two non-CJK tokens glued
    /// together, with no space between them, by anything but `' ’ - . _`
    /// (`p@ss`, `AT&T`). Such a word waits for a second dictation (LEARNING.md
    /// §4.1); `GPT4`, `M4` and `Hunter2` are short enough to be learned at once.
    public static func isSecretShaped(_ text: String) -> Bool {
        let cps = Array(text.unicodeScalars)
        let tokens = LearningText.tokenize(cps)
        let long = tokens.contains { token in
            token.scalars.count >= LearningLimits.secretMinLength
                && token.scalars.filter(LearningText.isDigit).count >= LearningLimits.secretMinDigits
        }
        if long { return true }
        for (left, right) in zip(tokens, tokens.dropFirst()) where !left.cjk && !right.cjk {
            let gap = cps[left.end..<right.start]
            if !gap.contains(where: LearningText.isSpace), gap.contains(where: { !LearningText.isJoiner($0) }) {
                return true
            }
        }
        return false
    }

    /// Whether `needle`'s tokens occur, lowercased, as a contiguous run of
    /// whole tokens in `haystack`.
    public static func containsTokens(_ haystack: String, _ needle: String) -> Bool {
        let h = LearningText.tokenize(haystack).map { LearningText.lower($0.scalars) }
        let n = LearningText.tokenize(needle).map { LearningText.lower($0.scalars) }
        guard !n.isEmpty, n.count <= h.count else { return false }
        for start in 0...(h.count - n.count) where Array(h[start..<(start + n.count)]) == n {
            return true
        }
        return false
    }

    public static func learnsWord(_ candidate: LearningCandidate, lexicon: any LearningLexicon) -> Bool {
        isDistinctive(candidate.meant, lexicon: lexicon)
    }

    /// Do the two texts differ, ignoring case, only by `s`, `es`, `ed` or `ing`
    /// added to the end of one (`ies`/`ied` for a final `y`)? (§4.2)
    static func isInflectionPair(_ a: String, _ b: String) -> Bool {
        let first = LearningText.lower(a), second = LearningText.lower(b)
        let (short, long) = first.unicodeScalars.count <= second.unicodeScalars.count ? (first, second) : (second, first)
        guard !short.isEmpty else { return false }
        if ["s", "es", "ed", "ing"].contains(where: { long == short + $0 }) { return true }
        return short.hasSuffix("y") && ["ies", "ied"].contains { long == String(short.dropLast()) + $0 }
    }

    public static func learnsAlias(_ candidate: LearningCandidate, lexicon: any LearningLexicon) -> Bool {
        if candidate.kind == .casing || LearningText.heardKey(candidate.heard).unicodeScalars.count < 2 {
            return false
        }
        // A plural or verb form is never a rewrite (LEARNING.md §4.2).
        if isInflectionPair(candidate.heard, candidate.meant) {
            return false
        }
        // A rewrite that contains its own trigger would rewrite its output again.
        if containsTokens(candidate.meant, candidate.heard) {
            return false
        }
        // A number is never a rewrite target (LEARNING.md §4.2).
        if !LearningText.hasLetter(candidate.meant) {
            return false
        }
        if candidate.kind == .respacing {
            return isDistinctive(candidate.meant, lexicon: lexicon)
        }
        let tokens = LearningText.tokenize(candidate.heard)
        if tokens.contains(where: \.cjk) {
            return true
        }
        if tokens.count == 1 {
            return !lexicon.isOrdinary(tokens[0].text)
        }
        return tokens.contains { token in
            (token.scalars.count == 1 && LearningText.isAlpha(token.scalars[0])) || !lexicon.isOrdinary(token.text)
        }
    }
}
