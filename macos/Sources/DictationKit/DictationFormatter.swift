import Foundation

/// M4: a pure, on-device, no-model post-ASR text formatter.
///
/// The formatter is deliberately conservative: `format(_:)` leaves input
/// unchanged unless a rule's explicit precondition is met. Its rules cover
/// small, local transformations such as sentence-initial disfluency handling
/// and selected orthographic repairs.
/// Agreement repair needs sentence-level syntax and is not attempted here;
/// ambiguous rewrites are safer as no-ops. The same conservative policy
/// applies to disfluency and number transformations whose preconditions are
/// not explicit enough.
///
/// ## The confidence gate
///
/// There is no scored/probabilistic gate here: the gate is the rule set.
/// Rules are explicit and closed rather than learned, so there is no threshold
/// to tune and ambiguous input remains unchanged.
public enum DictationFormatter {

    /// Version tag for the formatter's OBSERVABLE behavior — what
    /// `format(_:)` / `format(_:forApp:)` actually do to text. Bump this on
    /// ANY behavior change (a rule added to or removed from the default
    /// pipeline, a per-app profile change, a rule implementation change),
    /// even a "small" one: the dictation shadow harness folds this string
    /// into `config_hash`, and every stored shadow-eval row is keyed by that
    /// hash — a behavior change without a version bump silently pollutes
    /// the harness's per-config series with mixed results.
    ///
    /// v2 ("m4-caps-punct-v2"): added `stripTrailingEllipsis` to the
    /// default pipeline, selected by an offline rule review — see that rule's
    /// doc comment for its behavior. Other candidate transformations remain
    /// (trailing-period add/remove by length, unicode apostrophes/quotes,
    /// comma-before-conjunction, spelled<->digit numbers, percent/currency
    /// signs, double spaces, standalone "i") FAILED the >=50-sample /
    /// >=80%-consistency bar and stays out.
    ///
    /// v3 ("m4-caps-punct-v3"): `addTerminalPunctuationIfMissing` now ends a
    /// Han/kana utterance with a full-width stop instead of `.`. `format(_:)`
    /// and every shipped `AppProfile` are byte-identical to v2 — that rule is
    /// in none of them — but this tag governs OBSERVABLE behavior, and now
    /// that the style surface reaches the live path (`DictationSession`'s
    /// `formattingRules`) a user CAN select that rule, so a harness row
    /// recorded under v2 and one recorded under v3 are no longer answering
    /// the same question.
    ///
    /// v4 ("m4-caps-punct-v4"): `capitalizeSentences` leaves a word alone
    /// when it already has an upper-case letter after its first ("iPhone",
    /// "macOS", "eBay" no longer become "IPhone", "MacOS", "EBay"), and
    /// Warp, kitty, Alacritty, WezTerm and Hyper join the terminals that get
    /// no rules at all. Windows' `DictationTextFormatter` makes the same
    /// capitalisation change.
    ///
    /// v5 ("m4-caps-punct-v5"): `cjkPunctuation` joins the DEFAULT pipeline
    /// (`.standard`, so `format(_:)`, `format(_:forApp:)`'s fallback and the
    /// Dictation product's insertions all get it; terminals still get
    /// nothing). Two Han-script repairs, both no-ops on Latin text, measured
    /// against the 107 Chinese-bearing transcripts in this machine's own
    /// history (2026-10-04: 71% carried NO punctuation at all, none a
    /// full-width comma — the vocabulary `initial_prompt` conditions the
    /// decoder toward Latin style; see `whisper_mac.MLX_ZH_PROMPT_HEAD`):
    /// a half-width `,`/`?` with Han/kana on both sides becomes `，`/`？`,
    /// and a Han utterance with no terminal mark ends in `。`. English text
    /// is byte-identical to v4, so the terminal-punctuation regression that
    /// kept `addTerminalPunctuationIfMissing` out of the default pipeline
    /// does not apply: this rule can never put a `.` on a chat message.
    /// `capitalizeSentences` also treats `。`/`！`/`？` as sentence
    /// terminals, which matters only once such marks exist — i.e. now.
    public static let version = "m4-caps-punct-v5"

    // MARK: - Public entry point

    /// Applies only the transform this module's OWN measurement against the
    /// real corpus proved safe, and returns the result. Returns `input`
    /// completely unchanged (byte-identical) when even that one
    /// precondition is not met.
    ///
    /// ## Why this is one rule, not five, despite the doc comment above
    /// describing several
    ///
    /// Every rule below is IMPLEMENTED and self-checked against the narrow,
    /// hand-reasoned preconditions its doc comment describes — but wiring
    /// each of them into `format(_:)` and re-running
    /// the offline format evaluation against the full 14,412-
    /// utterance corpus (not just the aggregate word-substitution counts
    /// the first analysis ranked) surfaced that the
    /// "data-supported" framing was measuring the wrong thing: those counts
    /// are word-level diff alignments over the WHOLE corpus, not a
    /// per-utterance precision number. When re-measured directly —
    /// "of the utterances where this rule's precondition fires, how often
    /// does the reference text (the corpus's `formattedText` side) agree it
    /// should fire at all" — every content-changing rule failed the
    /// no-op-bias bar this task set:
    ///
    /// - **Leading and/so**: 1,173 utterances start with "and"/"so" in this
    ///   corpus. The reference leaves **408 of them (35%) completely
    ///   untouched**, and even among the 765 it DOES change, only ~40 look
    ///   like a literal strip of the leading word — the rest are unrelated
    ///   edits elsewhere in the same utterance. The 587-count "and -> deleted" statistic in
    ///   the analysis doc is a whole-corpus word alignment; most of those
    ///   587 deletions are NOT the utterance-initial case this rule targets.
    /// - **Terminal punctuation**: the single largest regression source
    ///   measured (1,709 of 2,084 total regressions). This corpus is 83%
    ///   terminal/iTerm2 — a large share of
    ///   dictations are shell commands, code, or commit-message-style
    ///   fragments that conventionally do NOT take a trailing period, and
    ///   the reference leaves them without one.
    /// - **Spelled numbers -> digits**: of 171 utterances matching the
    ///   "number immediately before a unit word" pattern, the 33 the
    ///   reference left untouched were regressed by this rule **100% of the
    ///   time** — the reference converts far less often than the aggregate
    ///   68-occurrence count suggested.
    /// - **Verbal "like"**: only 5 matching occurrences in the whole corpus
    ///   (too small to trust either way), but 2 of those 5 were left
    ///   unchanged in the reference — the same failure shape as the rules
    ///   above, just at negligible volume.
    ///
    /// Capitalization was the one exception: measured on the SAME corpus,
    /// it regressed only 3 of 5,590 utterances the reference left unchanged
    /// (0.05%). That is the only transform this method applies. The other four rules stay
    /// implemented and self-checked below (`stripLeadingDisfluency`,
    /// `stripVerbalLike`, `convertUnambiguousNumbers`,
    /// `addTerminalPunctuationIfMissing` are all `static`, not `private`) —
    /// available for an explicit, narrower future use (e.g. once a
    /// per-app or per-context signal exists to gate them), but deliberately
    /// NOT part of the default pipeline given what direct measurement
    /// showed.
    ///
    /// ## v2: capitalization + trailing-ellipsis strip
    ///
    /// The formatter's rule review re-opened the question at per-utterance
    /// granularity. Ambiguous candidate transformations remain disabled;
    /// `stripTrailingEllipsis`, see
    /// its doc comment — and it is the only addition v2 makes.
    ///
    /// ## v5: the Han-script repairs join the default
    ///
    /// `cjkPunctuation` (its own doc comment has the measurement) converts
    /// half-width separators sitting between Han/kana characters and ends a
    /// Chinese sentence with `。`. It cannot regress the corpus above —
    /// Latin text passes through it byte-identically — which is the bar
    /// every earlier candidate failed. Implemented as
    /// `apply(input, rules: AppProfile.standard.rules)` so this method and
    /// the `.standard` profile cannot drift apart again.
    public static func format(_ input: String) -> String {
        guard !input.isEmpty else { return input }
        return apply(input, rules: AppProfile.standard.rules)
    }

    /// Whether `format(_:)` would change `input` at all — the metric the
    /// eval harness reports as "did we touch an utterance", independent of
    /// whether the change agreed with the reference.
    public static func wouldChange(_ input: String) -> Bool {
        format(input) != input
    }

    // MARK: - Per-app profiles (Iteration 34)
    //
    // Iteration 33's diagnosis: the rules above are not intrinsically
    // wrong, they were measured against a corpus that is 83% iTerm2 — a
    // shell prompt does not want a forced capital or a trailing period.
    // The target app is a signal this app already has at insertion time
    // (its bundle ID) and had never spent. This
    // section gates each rule on that signal instead of applying (or
    // withholding) it globally.
    //
    // Profile assignment is NOT a guess — it is read off a SECOND direct
    // per-utterance measurement, segmented by app, using the same method
    // Iteration 32/33 used corpus-wide: for every utterance where a rule's
    // precondition fires, does the reference text agree it should fire?
    // Iteration 34 recorded the full per-app regression/coverage table this
    // mapping is built from.

    /// One formatting decision, independently toggleable per app because
    /// the measured safety of each rule turned out to depend on the target,
    /// not just the utterance.
    public enum Rule: CaseIterable, Sendable {
        case leadingDisfluency
        case verbalLike
        case numberConversion
        case capitalization
        case terminalPunctuation
        case trailingEllipsis
        /// The Han-script repairs of v5: half-width separators between
        /// Han/kana characters become full-width, and a Han utterance with
        /// no terminal mark ends in `。`. A no-op on Latin text, which is
        /// why — unlike `terminalPunctuation` — it is in the DEFAULT set.
        case cjkPunctuation
    }

    /// The rule set enabled for one target application.
    public struct AppProfile: Sendable, Equatable {
        public let rules: Set<Rule>
        public init(_ rules: Set<Rule>) { self.rules = rules }

        /// No rule ever fires — the invariant the terminal-never-formatted
        /// self-check exists to protect.
        public static let none = AppProfile([])
        /// The v1 default pipeline, kept as a named configuration for
        /// comparison runs (this was `format(_:)`'s whole behavior under
        /// "m4-capitalization-only-v1").
        public static let capitalizationOnly = AppProfile([.capitalization])
        /// The v5 default pipeline — exactly what `format(_:)` applies:
        /// capitalization (v1), the shadow-mined trailing-ellipsis strip
        /// (v2) and the Han-script punctuation repairs (v5).
        public static let standard = AppProfile([.capitalization, .trailingEllipsis, .cjkPunctuation])
    }

    /// Terminal emulators: measured regression on this slice is why
    /// `format(_:)` shipped capitalization-only in the first place (41 of
    /// the residual 41 regressions were single technical words/fragments —
    /// branch names, command fragments — that the reference keeps in lower
    /// case). Terminals get NO rules at all, not just a narrowed set —
    /// the strongest, and per this iteration's data the CORRECT, form of
    /// "gate on target app": see the doc comment on `profile(forApp:)` for
    /// why even capitalization is withheld here despite measuring safe.
    ///
    /// The first three are the ones the measurement saw. The rest are the
    /// other common macOS terminals, added in v4 on the same reasoning, not on
    /// a measurement of their own: before, "git status" in Warp became "Git
    /// status".
    private static let terminalBundleIDs: Set<String> = [
        "com.googlecode.iterm2",
        "com.mitchellh.ghostty",
        "com.apple.Terminal",
        "dev.warp.Warp-Stable",
        "net.kovidgoyal.kitty",
        "org.alacritty",
        "com.github.wez.wezterm",
        "co.zeit.hyper",
    ]

    /// Looks up the profile for a target application by its bundle
    /// identifier.
    ///
    /// ## What Iteration 34's per-app measurement actually found
    ///
    /// The hypothesis under test was "the four rules that regressed
    /// corpus-wide are wrong specifically FOR TERMINALS, and clear the bar
    /// on prose targets once gated by app." Segmenting the offline format
    /// evaluation's regression/coverage numbers by app
    /// (Iteration 34 recorded the full table)
    /// rejects the specific mechanism, though not the general idea:
    ///
    /// - **Terminal punctuation** — the dominant regression source
    ///   corpus-wide — regresses 28-37% of clean utterances on Slack,
    ///   Chrome, AND Claude desktop, not meaningfully better than iTerm2's
    ///   32%. The reference leaves short conversational messages
    ///   (chat/prompt text) without a trailing period in roughly the same
    ///   proportion as shell commands. This
    ///   rule is unsafe on every app measured, not just terminals.
    /// - **Leading and/so, verbal like, number conversion**: low-to-
    ///   moderate regression on prose apps (2-8%, worse than
    ///   capitalization's <2% everywhere) and, critically, ~0% coverage
    ///   on prose apps (0-1 exact hits across hundreds of utterances the
    ///   reference changed, per app) — even where "safe enough," they fix nothing
    ///   measurable. Not worth the risk surface at any gate.
    /// - **Capitalization** is the only rule that clears a low regression
    ///   bar on EVERY app with enough volume to measure (iTerm2 0.78%,
    ///   Slack 0.69%, Chrome 0%, Claude desktop 1.98%) and its coverage on
    ///   prose apps (1.47-2.84%) is modestly, consistently above the
    ///   corpus-wide 1.29% figure.
    ///
    /// So the per-app profile below is NOT "prose apps get more rules than
    /// terminals" — the data does not support giving any app more than
    /// capitalization. It is "terminals get LESS than the conservative
    /// global default, because per-app segmentation is the only way to
    /// identify that terminals are the one place where excluding even the
    /// cheap, low-regression rule removes real risk for zero measured
    /// coverage loss (capitalizing a shell command was never a
    /// meaningful edit to begin with)." `nil`/unrecognized/low-volume
    /// bundle IDs fall back to `.standard`, matching the safe global
    /// default (`format(_:)`'s own pipeline), never to the ungated rule
    /// set. (v2: the default profile gained `.trailingEllipsis` alongside
    /// capitalization — mined from the shadow store, see that rule's doc
    /// comment; terminals still get NOTHING.)
    public static func profile(forApp bundleID: String?) -> AppProfile {
        guard let bundleID, !bundleID.isEmpty else { return .standard }
        if terminalBundleIDs.contains(bundleID) { return .none }
        return .standard
    }

    /// Applies exactly the rules in `rules`, in a fixed order chosen so
    /// later rules never observe text a not-yet-applied earlier rule would
    /// have removed: piece-level deletions first (leading disfluency, then
    /// verbal `like`), then the text-level rules (number conversion,
    /// capitalization, the Han repairs, terminal punctuation last so it sees
    /// the already-capitalized, already-converted result — and, with the Han
    /// repairs before it, a full-width stop it must not double).
    public static func apply(_ input: String, rules: Set<Rule>) -> String {
        guard !input.isEmpty, !rules.isEmpty else { return input }
        var pieces = tokenize(input)
        if rules.contains(.leadingDisfluency) { pieces = stripLeadingDisfluency(pieces) }
        if rules.contains(.verbalLike) { pieces = stripVerbalLike(pieces) }
        var text = render(pieces)
        if rules.contains(.numberConversion) { text = convertUnambiguousNumbers(text) }
        // Trailing-ellipsis strip runs BEFORE capitalization and terminal
        // punctuation: capitalization is end-insensitive either way, and
        // terminalPunctuation (when explicitly enabled) should see the
        // already-bare end so it can classify it, not the artifact dots.
        if rules.contains(.trailingEllipsis) { text = stripTrailingEllipsis(text) }
        if rules.contains(.capitalization) { text = capitalizeSentences(text) }
        // Before `terminalPunctuation`: when both are on, this rule's
        // full-width stop leaves the end classified, so the later rule's
        // "ends in a letter or digit" precondition no longer holds and it
        // stays out — the stronger, measured-unsafe rule never double-stops
        // a Han sentence this one already ended.
        if rules.contains(.cjkPunctuation) { text = applyCjkPunctuation(text) }
        if rules.contains(.terminalPunctuation) { text = addTerminalPunctuationIfMissing(text) }
        return text
    }

    /// Per-app entry point: `format(_:)` plus the one piece of context this
    /// app has at insertion time and had never used before Iteration 34 —
    /// the target application's bundle ID. Unlike `format(_:)`, this can
    /// apply MORE than capitalization (on measured-safe prose targets) or
    /// LESS (nothing at all, on terminals) depending on the target.
    public static func format(_ input: String, forApp bundleID: String?) -> String {
        guard !input.isEmpty else { return input }
        return apply(input, rules: profile(forApp: bundleID).rules)
    }

    /// All five candidate rules, ungated — the "first pass" configuration
    /// Iteration 32 measured and rejected corpus-wide (37.28% regression).
    /// Exposed here only so the eval harness can re-run it as the explicit
    /// control in Iteration 34's segmented-by-app table; never called from
    /// `DictationSession` or any production call site.
    public static func formatAllRulesUngated(_ input: String) -> String {
        apply(input, rules: Set(Rule.allCases))
    }

    // MARK: - Tokenizer: reversible word / non-word split

    /// A run of contiguous letters (+ apostrophe, so "don't" stays one
    /// token) is a `.word` piece; every other character — whitespace,
    /// punctuation, digits — is grouped into `.other` pieces. Joining every
    /// piece's `text` in order always reconstructs the original string
    /// exactly, which is what lets every rule below remove or replace a
    /// SINGLE piece without hand-managing whitespace bookkeeping anywhere
    /// else.
    enum TokenKind: Equatable { case word, other }

    struct Piece: Equatable {
        var text: String
        let kind: TokenKind
    }

    static func tokenize(_ s: String) -> [Piece] {
        var pieces: [Piece] = []
        var current = ""
        var currentKind: TokenKind?
        for ch in s {
            let kind: TokenKind = (ch.isLetter || ch == "'" || ch == "\u{2019}") ? .word : .other
            if currentKind == kind {
                current.append(ch)
            } else {
                if let k = currentKind { pieces.append(Piece(text: current, kind: k)) }
                current = String(ch)
                currentKind = kind
            }
        }
        if let k = currentKind { pieces.append(Piece(text: current, kind: k)) }
        return pieces
    }

    static func render(_ pieces: [Piece]) -> String {
        pieces.map(\.text).joined()
    }

    // MARK: - Leading and/so

    /// Sentence-initial `and`/`so` are the single largest deletion class in
    /// the measured data (587 and 220 occurrences respectively) — these are
    /// restart/continuation debris ("And, I think..."), not content.
    /// Deliberately scoped to the VERY START of the utterance (only
    /// whitespace may precede the matched word) — this rule says nothing
    /// about `and`/`so` anywhere else in the text, where they are almost
    /// always real conjunctions.
    static func stripLeadingDisfluency(_ pieces: [Piece]) -> [Piece] {
        var pieces = pieces
        var iterations = 0
        // Bounded loop so "and so" chains ("And, so, I think...") strip
        // cleanly across both words; capped defensively — each successful
        // strip removes at least one whole piece, so this can never loop
        // more than the piece count allows regardless.
        while iterations < 3 {
            guard let firstWordIdx = pieces.firstIndex(where: { $0.kind == .word }) else { break }
            let before = pieces[..<firstWordIdx]
            guard before.allSatisfy({ $0.kind == .other && $0.text.trimmingCharacters(in: .whitespaces).isEmpty }) else {
                break
            }
            let word = pieces[firstWordIdx].text.lowercased()
            guard word == "and" || word == "so" else { break }

            var removeUpTo = firstWordIdx + 1
            if removeUpTo < pieces.count, pieces[removeUpTo].kind == .other {
                removeUpTo += 1
            }
            // Never strip the ENTIRE utterance down to nothing — if there is
            // no word content left afterward, this "and"/"so" is presumably
            // load-bearing (e.g. the whole dictation was literally "And?"),
            // so leave it alone rather than emit an empty string.
            guard pieces[removeUpTo...].contains(where: { $0.kind == .word }) else { break }

            pieces.removeSubrange(0..<removeUpTo)
            iterations += 1
        }
        return pieces
    }

    // MARK: - Verbal "like"

    /// `like` used as a filler between two clauses — the second-largest
    /// deletion class measured (558 occurrences). Genuine uses of "like"
    /// (verb: "I like coffee"; preposition: "looks like a house"; "would
    /// like to") are common enough that a blanket removal would corrupt far
    /// more than it fixes, so this only fires when BOTH sides look like a
    /// clause boundary:
    ///   - the word immediately before `like` is a discourse marker
    ///     (`and`/`so`/`but`/`well`) OR `like` is utterance-initial, AND
    ///   - the word immediately after `like` is a subject pronoun starting
    ///     a new clause (`I`/`you`/`we`/`they`/`he`/`she`/`it`/`that`/
    ///     `this`/`there`).
    /// This excludes "would like to" (preceding word "would" is not a
    /// marker), "looks like a house" (following word "a" is not a
    /// pronoun), and plain "I like it" (preceding word "I" is not a
    /// marker) by construction.
    static let clauseBoundaryMarkers: Set<String> = ["and", "so", "but", "well"]
    static let clauseStartPronouns: Set<String> = [
        "i", "you", "we", "they", "he", "she", "it", "that", "this", "there",
    ]

    static func stripVerbalLike(_ pieces: [Piece]) -> [Piece] {
        var removalStarts: Set<Int> = []

        for i in 0..<pieces.count {
            guard pieces[i].kind == .word, pieces[i].text.lowercased() == "like" else { continue }

            // Is only whitespace (no other word) between the start of the
            // utterance and this "like"?
            let isUtteranceInitial = pieces[..<i].allSatisfy {
                $0.kind == .other && $0.text.trimmingCharacters(in: .whitespaces).isEmpty
            }

            let prevWordIdx = pieces[..<i].lastIndex(where: { $0.kind == .word })
            let precededByMarker = prevWordIdx.map { clauseBoundaryMarkers.contains(pieces[$0].text.lowercased()) } ?? false
            guard isUtteranceInitial || precededByMarker else { continue }

            let nextWordIdx = pieces[(i + 1)...].firstIndex(where: { $0.kind == .word })
            guard let nextWordIdx, clauseStartPronouns.contains(pieces[nextWordIdx].text.lowercased()) else { continue }

            removalStarts.insert(i)
        }

        guard !removalStarts.isEmpty else { return pieces }

        var result: [Piece] = []
        result.reserveCapacity(pieces.count)
        var skipNext = false
        for i in 0..<pieces.count {
            if skipNext {
                skipNext = false
                continue
            }
            if removalStarts.contains(i) {
                // Drop "like" itself, and the single separator piece right
                // after it (if any) — collapses "and, like, I" -> "and, I"
                // and "like, I" (utterance-initial) -> "I" rather than
                // leaving a doubled separator behind.
                if i + 1 < pieces.count, pieces[i + 1].kind == .other {
                    skipNext = true
                }
                continue
            }
            result.append(pieces[i])
        }
        return result
    }

    // MARK: - Spelled numbers -> digits

    /// Real but marginal in the data (68/14,412). Scoped to single-word
    /// number tokens immediately followed by a unit word ("five minutes" ->
    /// "5 minutes") — the one context where the numeral reading is
    /// unambiguous. Explicitly declines to touch a number word that is
    /// itself immediately preceded by another number word ("twenty five
    /// dollars") rather than emit a half-converted compound like
    /// "twenty 5 dollars" — no compound-number support is implemented, by
    /// design, given the sample size does not justify the added risk.
    static let numberWords: [String: String] = [
        "zero": "0", "one": "1", "two": "2", "three": "3", "four": "4",
        "five": "5", "six": "6", "seven": "7", "eight": "8", "nine": "9",
        "ten": "10", "eleven": "11", "twelve": "12", "thirteen": "13",
        "fourteen": "14", "fifteen": "15", "sixteen": "16", "seventeen": "17",
        "eighteen": "18", "nineteen": "19", "twenty": "20", "thirty": "30",
        "forty": "40", "fifty": "50", "sixty": "60", "seventy": "70",
        "eighty": "80", "ninety": "90",
    ]

    static let unitWords: Set<String> = [
        "percent", "dollars", "dollar", "cents", "cent",
        "minutes", "minute", "seconds", "second", "hours", "hour",
        "days", "day", "weeks", "week", "months", "month", "years", "year",
        "times", "points", "point", "degrees", "degree",
        "feet", "miles", "mile", "meters", "meter", "pounds", "pound",
    ]

    static func convertUnambiguousNumbers(_ text: String) -> String {
        var pieces = tokenize(text)
        for i in 0..<pieces.count {
            guard pieces[i].kind == .word else { continue }
            let word = pieces[i].text.lowercased()
            guard let digit = numberWords[word] else { continue }

            guard i + 1 < pieces.count, pieces[i + 1].kind == .other else { continue }
            guard i + 2 < pieces.count, pieces[i + 2].kind == .word else { continue }
            guard unitWords.contains(pieces[i + 2].text.lowercased()) else { continue }

            // Compound guard: skip if the piece before this number word is
            // itself another number word.
            if i >= 2, pieces[i - 1].kind == .other, pieces[i - 2].kind == .word,
               numberWords[pieces[i - 2].text.lowercased()] != nil {
                continue
            }

            pieces[i].text = digit
        }
        return render(pieces)
    }

    // MARK: - Trailing ellipsis (v2, shadow-mined)

    /// Strips a trailing run of three-or-more ASCII periods (Whisper's
    /// trailing-ellipsis artifact: the model emits `...` on trailing-off or
    /// truncated speech) plus any whitespace immediately before the run,
    /// leaving the text BARE — deliberately not replaced with a period.
    ///
    /// Only a trailing ASCII run is handled; interior runs and the Unicode
    /// ellipsis are left alone.
    ///
    /// Conservatism guards: requires a run of >=3 dots (never touches a
    /// sentence period or an abbreviation's double dot), requires at least
    /// one letter/digit before the run (never strips an utterance down to
    /// nothing), and preserves any whitespace after the run byte-for-byte.
    static func stripTrailingEllipsis(_ text: String) -> String {
        guard let lastNonWS = text.lastIndex(where: { !$0.isWhitespace }) else { return text }
        guard text[lastNonWS] == "." else { return text }
        let afterBody = text.index(after: lastNonWS)
        let trailingWhitespace = text[afterBody...]
        var body = String(text[..<afterBody])
        var dots = 0
        while body.hasSuffix(".") {
            body.removeLast()
            dots += 1
        }
        guard dots >= 3 else { return text }
        guard body.contains(where: { $0.isLetter || $0.isNumber }) else { return text }
        while let last = body.last, last.isWhitespace { body.removeLast() }
        return body + trailingWhitespace
    }

    // MARK: - Capitalization

    /// Capitalizes the first letter of the utterance and the first letter
    /// following a sentence-terminal `.`/`!`/`?` — and, since v5, the
    /// full-width `。`/`！`/`？` — that is itself followed by whitespace (or
    /// end of string) — the whitespace requirement is what keeps a decimal
    /// point ("3.14 percent") from being misread as a sentence boundary,
    /// since a `.` between two digits is never followed by whitespace. The
    /// full-width marks matter once Chinese text actually carries them (the
    /// v5 rule and the zh prompt both make that common): an English sentence
    /// after a Chinese one still gets its capital. Does not attempt to
    /// special-case abbreviations ("e.g.", "Mr.") — a known, accepted
    /// limitation given how rarely they appear in this corpus (83%
    /// terminal/dictation prose, not formal writing).
    ///
    /// A word with an upper-case letter after its first is left alone
    /// (v4): "iPhone", "macOS", "eBay", "iOS". Its casing is its own, not a
    /// sentence start the recognizer missed, and upper-casing its first
    /// letter only breaks it ("IPhone"). That includes a word the user taught,
    /// whose declared casing the worker restores (LEARNING.md §6), so a fix of
    /// "IPhone" to "iPhone" can stick. The word is the run of letters that
    /// starts there, as on Windows (`DictationTextFormatter`).
    static func capitalizeSentences(_ text: String) -> String {
        guard !text.isEmpty else { return text }
        var chars = Array(text)
        var capitalizeNext = true
        for i in 0..<chars.count {
            let c = chars[i]
            if c.isLetter {
                if capitalizeNext {
                    if !hasCapitalAfterFirstLetter(chars, from: i) {
                        chars[i] = Character(c.uppercased())
                    }
                    capitalizeNext = false
                }
            } else if c == "." || c == "!" || c == "?" || c == "\u{3002}" || c == "\u{FF01}" || c == "\u{FF1F}" {
                let nextIsWhitespaceOrEnd = (i + 1 >= chars.count) || chars[i + 1].isWhitespace
                if nextIsWhitespaceOrEnd {
                    capitalizeNext = true
                }
            }
        }
        return String(chars)
    }

    /// Whether the word starting at `start` (the run of letters from there)
    /// has an upper-case letter after its first.
    static func hasCapitalAfterFirstLetter(_ chars: [Character], from start: Int) -> Bool {
        var j = start + 1
        while j < chars.count, chars[j].isLetter {
            if chars[j].isUppercase { return true }
            j += 1
        }
        return false
    }

    // MARK: - Terminal punctuation

    /// Han ideographs and Japanese kana — the scripts whose sentences end in
    /// a full-width stop rather than `.`.
    ///
    /// Deliberately NOT "anything non-Latin": Hangul and Cyrillic sentences
    /// take an ASCII full stop, so a broad `!isASCII` test would put a
    /// Chinese period on Korean. Anything outside these blocks falls through
    /// to `.` — the safe direction, because that is what this rule appended
    /// to everything before v3.
    static func isHanOrKana(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3040...0x30FF,     // Hiragana, Katakana
             0x3400...0x4DBF,     // CJK Unified Ideographs Extension A
             0x4E00...0x9FFF,     // CJK Unified Ideographs
             0xF900...0xFAFF,     // CJK Compatibility Ideographs
             0x20000...0x2FA1F:   // Extensions B-F, compatibility supplement
            return true
        default:
            return false
        }
    }

    /// Which full stop this utterance ends with.
    ///
    /// Fires on either of two conditions, because real dictation here is
    /// bilingual and each condition ALONE gets a real slice of it wrong.
    /// Counted over the 144 transcripts in this machine's own
    /// `~/Library/Application Support/Dictation/dictation/history.json` — the same
    /// history an earlier usage-defect count measured, whose
    /// 83-bare / 52-CJK split reproduced exactly: 55 transcripts contain
    /// Han/kana, and this pair of conditions marks 52 of them.
    ///
    /// - **Ends in Han/kana.** Catches the 4 transcripts that open in English
    ///   and finish in Chinese; a majority test alone calls those Latin and
    ///   ends a Chinese clause with `.`.
    /// - **More Han/kana than other letters.** Catches the 6 Chinese
    ///   sentences that end on a Latin product name; a last-letter test alone
    ///   calls those English.
    ///
    /// The 3 transcripts the pair still leaves on `.` are Chinese-opening
    /// ones that finish with a whole English sentence, where `.` is right.
    static func terminalMark(for text: String) -> Character {
        var hanOrKana = 0
        var otherLetters = 0
        var lastLetterIsHanOrKana = false
        for scalar in text.unicodeScalars {
            if isHanOrKana(scalar) {
                hanOrKana += 1
                lastLetterIsHanOrKana = true
            } else if Character(scalar).isLetter {
                otherLetters += 1
                lastLetterIsHanOrKana = false
            }
        }
        return (lastLetterIsHanOrKana || hanOrKana > otherLetters) ? "\u{3002}" : "."
    }

    /// Appends a full stop only when the last non-whitespace character is a
    /// letter or digit — i.e. only when there is clearly NO terminal
    /// punctuation yet (never doubles up on an existing mark of either
    /// script, and never touches text ending in a comma, quote, or anything
    /// else it can't classify safely).
    ///
    /// WHICH full stop is `terminalMark(for:)`'s call, and it has to be
    /// asked: Han characters satisfy the `isLetter` guard below, so a
    /// script-blind version of this rule puts a half-width `.` on Mandarin —
    /// a wrong character, not a style preference. For this user that is the
    /// MAJORITY of what enabling the rule would touch: 52 of the 83
    /// transcripts in the shipped history that carry no terminal punctuation
    /// contain Han/kana.
    static func addTerminalPunctuationIfMissing(_ text: String) -> String {
        guard let lastChar = text.reversed().first(where: { !$0.isWhitespace }) else { return text }
        guard lastChar.isLetter || lastChar.isNumber else { return text }
        guard let idx = text.rangeOfCharacter(from: CharacterSet.whitespaces.inverted, options: .backwards) else {
            return text
        }
        var result = text
        result.insert(terminalMark(for: text), at: idx.upperBound)
        return result
    }

    // MARK: - CJK punctuation (v5)

    /// The Han-script repairs in the DEFAULT pipeline (v5): a half-width
    /// `,` or `?` whose nearest non-whitespace neighbours are BOTH Han/kana
    /// becomes `，`/`？` — and a `?` at the very end of the text converts on
    /// a Han predecessor alone ("你知道吗?": the attested shape of a Chinese
    /// question; a trailing comma after Han stays half-width — it marks a
    /// cut-off clause, not a separator) — and a text with no terminal mark
    /// whose sentence is Chinese (`terminalMark(for:)`) ends in `。`.
    ///
    /// Why this rule may ship while `terminalPunctuation` may not: its
    /// effect on Latin text is byte-identical to doing nothing — the
    /// separator conversion needs Han on BOTH sides, and the stop is only
    /// ever the full-width one, so the measured regression (a `.` appended
    /// to chat messages and shell fragments) is unreachable from here. The
    /// need is measured on this machine's own history (2026-10-04): of 107
    /// Chinese-bearing transcripts, 76 carried no punctuation at all and
    /// none a full-width comma, because the vocabulary prompt conditions
    /// the decoder toward Latin style (`whisper_mac.MLX_ZH_PROMPT_HEAD`).
    ///
    /// Separators other than `,`/`?` are deliberately not converted: they
    /// never occurred between Han characters in that history, and a rule
    /// with no measured trigger pattern is a guess. A comma with Latin on
    /// either side ("用 Slack, Teams 开会") is left half-width — the safe
    /// direction for code-mixed text.
    static func applyCjkPunctuation(_ text: String) -> String {
        var chars = Array(text)
        for i in chars.indices where chars[i] == "," || chars[i] == "?" {
            var before = i - 1
            while before >= 0 && chars[before].isWhitespace { before -= 1 }
            var after = i + 1
            while after < chars.count && chars[after].isWhitespace { after += 1 }
            guard before >= 0, isHanOrKanaCharacter(chars[before]) else { continue }
            // A run to the end of the text converts a "?" only: "吗?" is a
            // complete Chinese question, while a trailing comma is a cut-off
            // clause and keeps its half-width mark.
            if after < chars.count {
                guard isHanOrKanaCharacter(chars[after]) else { continue }
            } else if chars[i] == "," {
                continue
            }
            chars[i] = chars[i] == "," ? "\u{FF0C}" : "\u{FF1F}"
        }
        var result = String(chars)
        guard let lastChar = result.reversed().first(where: { !$0.isWhitespace }),
              lastChar.isLetter || lastChar.isNumber,
              terminalMark(for: result) == "\u{3002}",
              let idx = result.rangeOfCharacter(from: CharacterSet.whitespaces.inverted, options: .backwards)
        else { return result }
        result.insert("\u{3002}", at: idx.upperBound)
        return result
    }

    /// A Character is Han/kana when its first scalar is (`isHanOrKana`).
    static func isHanOrKanaCharacter(_ ch: Character) -> Bool {
        guard let scalar = ch.unicodeScalars.first else { return false }
        return isHanOrKana(scalar)
    }
}
