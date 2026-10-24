import Foundation

/// Pure-logic self-check for `DictationFormatter` — no model, no I/O, no
/// corpus access (the corpus itself is personal data and never touched by
/// anything in `--self-check`; the real measurement against it is an offline
/// evaluation, run separately). Registered in the app entry point's `--self-check` chain
/// next to `runDictationSelfCheck()`.
///
/// Two layers of coverage, matching the two layers `DictationFormatter`
/// itself ended up with once measured against the real corpus:
///   1. `DictationFormatter.format(_:)` — the actual production entry
///      point, which after measurement applies ONLY the measured-safe rules
///      (capitalization, the trailing-ellipsis strip, and — since v5 — the
///      Han-script punctuation repairs). Its
///      no-op cases get equal billing with the cases that DO change,
///      because "does nothing when it should do nothing" is the property
///      this whole module exists to protect.
///   2. The individual rule functions (`stripLeadingDisfluency`,
///      `stripVerbalLike`, `convertUnambiguousNumbers`,
///      `addTerminalPunctuationIfMissing`) — implemented, self-checked, and
///      documented, but deliberately NOT wired into `format(_:)`'s default
///      pipeline; see that method's doc comment for why. Testing them
///      directly here keeps them honest for whatever narrower future use
///      might call them explicitly.
public func runDictationFormatterSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }
    func checkFormat(_ input: String, _ expected: String, _ label: String) {
        let got = DictationFormatter.format(input)
        check(got == expected, "\(label): format(\(input.debugDescription)) -> \(got.debugDescription), expected \(expected.debugDescription)")
    }
    /// Applies a single piece-level rule (tokenize -> rule -> render) in
    /// isolation, exactly as a future explicit caller would.
    func checkPieceRule(
        _ rule: ([DictationFormatter.Piece]) -> [DictationFormatter.Piece],
        _ input: String, _ expected: String, _ label: String
    ) {
        let got = DictationFormatter.render(rule(DictationFormatter.tokenize(input)))
        check(got == expected, "\(label): \(input.debugDescription) -> \(got.debugDescription), expected \(expected.debugDescription)")
    }
    func checkStringRule(
        _ rule: (String) -> String,
        _ input: String, _ expected: String, _ label: String
    ) {
        let got = rule(input)
        check(got == expected, "\(label): \(input.debugDescription) -> \(got.debugDescription), expected \(expected.debugDescription)")
    }

    // MARK: format(_:) — the production entry point (capitalization only)

    check(DictationFormatter.format("") == "", "format/empty: must stay empty")
    checkFormat("Hello world.", "Hello world.", "format/no-op: already capitalized text is untouched")
    checkFormat("Is this the right approach?", "Is this the right approach?", "format/no-op: already-terminal question untouched")
    checkFormat("and I think this is good", "And I think this is good", "format/leading-and-not-stripped: format() only capitalizes — leading and/so is NOT stripped (measured too imprecise, see doc comment)")
    checkFormat("wait five minutes", "Wait five minutes", "format/numbers-not-converted: format() only capitalizes — spelled numbers are NOT converted (measured too imprecise)")
    checkFormat("already ends with a period.", "Already ends with a period.", "format/cap-only: capitalizes but does not touch existing terminal punctuation")
    checkFormat("hello world", "Hello world", "format/cap-only: capitalizes the first letter but does NOT add a missing terminal period")
    checkFormat("hi. how are you", "Hi. How are you", "format/cap-after-period: capitalizes after a real sentence boundary")
    checkFormat("the score is 3.14 percent accurate", "The score is 3.14 percent accurate", "format/cap-decimal-safety: a decimal point must NOT be read as a sentence boundary")
    check(!DictationFormatter.wouldChange("Hello world."), "wouldChange: must report false for already-clean text")
    check(DictationFormatter.wouldChange("hello world"), "wouldChange: must report true when capitalization actually changes something")

    // v2: trailing-ellipsis strip through the production entry point
    // (mined: 197/197 refs drop the ellipsis; 165/176 end bare, not '.').
    checkFormat("let me think about that...", "Let me think about that", "format/v2-trailing-ellipsis: strips the artifact dots and leaves the end BARE (no period added)")
    checkFormat("maybe we could try ...", "Maybe we could try", "format/v2-trailing-ellipsis-spaced: whitespace before the dot run is stripped with it")
    // Note the expected casing: capitalization-after-'. '-boundaries (which
    // includes an interior '... ') is PRE-EXISTING v1 behavior; the v2 rule
    // asserts only that the interior DOTS survive.
    checkFormat("one... two... three", "One... Two... Three", "format/v2-mid-ellipsis-not-fired: interior ellipses had only n=8 mined support and the dots must stay untouched")
    checkFormat("that seems fine.", "That seems fine.", "format/v2-single-period-not-fired: a real sentence period is not an ellipsis and must survive")
    checkFormat("see fig. 2..", "See fig. 2..", "format/v2-two-dots-not-fired: a run of only two dots is ambiguous and must stay untouched")
    checkFormat("...", "...", "format/v2-only-dots-not-fired: never strip an utterance down to nothing")
    checkFormat("wait, hmm…", "Wait, hmm…", "format/v2-unicode-ellipsis-not-fired: unicode … is preserved by this rule")

    // Iteration 7/10's finding, restated as a permanent invariant: never
    // attempt subject-verb agreement repair — `is`->`are` and `are`->`is`
    // occur with roughly equal frequency in the real data, so there is no
    // safe direction to guess.
    checkFormat("he are happy", "He are happy", "format/agreement-repair-not-attempted: must leave 'are' exactly as dictated")
    checkFormat("I need a apple", "I need a apple", "format/agreement-repair-not-attempted: must leave 'a'/'an' exactly as dictated")

    // MARK: idempotency of format(_:)

    for input in ["hello world", "Hello world.", "he are happy", "", "let me think about that...", "one... two... three", "iPhone. eBay"] {
        let once = DictationFormatter.format(input)
        let twice = DictationFormatter.format(once)
        check(once == twice, "format/idempotency: format(format(\(input.debugDescription))) must equal format(\(input.debugDescription)) once")
    }

    // MARK: stripLeadingDisfluency — implemented, self-checked, NOT wired into format()

    checkPieceRule(DictationFormatter.stripLeadingDisfluency, "and I think this is good", "I think this is good", "leading-and: strips 'and '")
    checkPieceRule(DictationFormatter.stripLeadingDisfluency, "so we should ship it", "we should ship it", "leading-so: strips 'so '")
    checkPieceRule(DictationFormatter.stripLeadingDisfluency, "and so I think we should go", "I think we should go", "leading-and-so-chain: strips both in one pass")
    checkPieceRule(DictationFormatter.stripLeadingDisfluency, "And, I think this is good", "I think this is good", "leading-and-comma: strips the comma-separator piece too")
    checkPieceRule(DictationFormatter.stripLeadingDisfluency, "I like cats and dogs.", "I like cats and dogs.", "leading-mid-sentence-and: real conjunction mid-sentence must survive untouched")
    checkPieceRule(DictationFormatter.stripLeadingDisfluency, "This is so good.", "This is so good.", "leading-mid-sentence-so: real intensifier mid-sentence must survive untouched")
    checkPieceRule(DictationFormatter.stripLeadingDisfluency, "And?", "And?", "leading-and-alone: nothing left afterward, must not strip to empty")

    // MARK: stripVerbalLike — implemented, self-checked, NOT wired into format()

    checkPieceRule(DictationFormatter.stripVerbalLike, "and like I think we should go", "and I think we should go", "verbal-like-after-and: strips only the 'like'")
    checkPieceRule(DictationFormatter.stripVerbalLike, "like I was saying we are done", "I was saying we are done", "verbal-like-utterance-initial: strips a leading filler 'like'")
    checkPieceRule(DictationFormatter.stripVerbalLike, "well, like, that seems right", "well, that seems right", "verbal-like-comma-flanked: strips only the filler 'like,'")
    checkPieceRule(DictationFormatter.stripVerbalLike, "I like pizza.", "I like pizza.", "verbal-like-verb: 'I like X' must survive untouched")
    checkPieceRule(DictationFormatter.stripVerbalLike, "It looks like a house.", "It looks like a house.", "verbal-like-preposition: 'looks like a X' must survive untouched")
    checkPieceRule(DictationFormatter.stripVerbalLike, "I would like to see it.", "I would like to see it.", "verbal-like-would: 'would like to' must survive untouched")
    checkPieceRule(DictationFormatter.stripVerbalLike, "It felt like a trap.", "It felt like a trap.", "verbal-like-felt: 'felt like a X' must survive untouched")

    // MARK: convertUnambiguousNumbers — implemented, self-checked, NOT wired into format()

    checkStringRule(DictationFormatter.convertUnambiguousNumbers, "wait five minutes", "wait 5 minutes", "number-unit: unambiguous single number before a unit converts")
    checkStringRule(DictationFormatter.convertUnambiguousNumbers, "I waited three hours for it", "I waited 3 hours for it", "number-unit-mid-sentence: still converts")
    checkStringRule(DictationFormatter.convertUnambiguousNumbers, "twenty five dollars", "twenty five dollars", "number-compound-guard: compound numbers are declined, not half-converted")
    checkStringRule(DictationFormatter.convertUnambiguousNumbers, "nine lives", "nine lives", "number-no-unit: 'lives' is not a recognized unit, must not convert")

    // MARK: addTerminalPunctuationIfMissing — implemented, self-checked, NOT wired into format()

    checkStringRule(DictationFormatter.addTerminalPunctuationIfMissing, "hello world", "hello world.", "terminal-add: appends a missing period")
    checkStringRule(DictationFormatter.addTerminalPunctuationIfMissing, "already ends with a period.", "already ends with a period.", "terminal-no-double: must not add a second period")
    checkStringRule(DictationFormatter.addTerminalPunctuationIfMissing, "already a question?", "already a question?", "terminal-no-double-question: must not append '.' after '?'")
    checkStringRule(DictationFormatter.addTerminalPunctuationIfMissing, "trailing comma,", "trailing comma,", "terminal-comma-left-alone: cannot classify a trailing comma safely, must not touch it")

    // MARK: applyCjkPunctuation — the v5 default-pipeline Han repairs

    // The terminal stop: Han-dominant text ends in 。, everything else is bare.
    checkStringRule(DictationFormatter.applyCjkPunctuation, "我们下午三点开会", "我们下午三点开会。", "cjk-stop-add: a Mandarin sentence gets the full-width stop")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "请帮我打开一下Slack", "请帮我打开一下Slack。", "cjk-stop-latin-tail: a Han-dominant sentence ending on a product name still gets 。")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "我买了3", "我买了3。", "cjk-stop-digit-end: a trailing digit keeps the sentence's script")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "おはようございます", "おはようございます。", "cjk-stop-kana: Japanese takes the same stop")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "안녕하세요", "안녕하세요", "cjk-stop-hangul-left-alone: Korean takes an ASCII stop, which this rule never adds")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "hello world", "hello world", "cjk-stop-english-left-alone: never puts a stop of either script on English")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "测试一下 we also ship on Tuesday and we are comparing builds against Friday", "测试一下 we also ship on Tuesday and we are comparing builds against Friday", "cjk-stop-latin-closing-sentence: an English closing sentence takes no full-width stop")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "已经好了。", "已经好了。", "cjk-stop-no-double: an existing 。 is terminal punctuation already")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "真的吗？", "真的吗？", "cjk-stop-no-double-question: an existing ？ is terminal punctuation already")
    checkStringRule(DictationFormatter.applyCjkPunctuation, " 会议 ", " 会议。 ", "cjk-stop-keeps-outer-whitespace: the stop lands after the last Han character, whitespace preserved")

    // The separator conversion: half-width between Han/kana on BOTH sides.
    checkStringRule(DictationFormatter.applyCjkPunctuation, "现在我这网络状态不是很好,豆包在那里罢工", "现在我这网络状态不是很好，豆包在那里罢工。", "cjk-comma: a Han-bracketed half-width comma goes full-width, and the sentence ends in 。")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "好的,你现在已经知道我的要求,你可以开始了", "好的，你现在已经知道我的要求，你可以开始了。", "cjk-comma-twice: every Han-bracketed comma converts")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "你知道吗?", "你知道吗？", "cjk-question: a Han-bracketed half-width question mark goes full-width")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "hello, world", "hello, world", "cjk-comma-latin-left-alone: a Latin-bracketed comma stays half-width")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "用 Slack, Teams 开会", "用 Slack, Teams 开会。", "cjk-comma-latin-side-kept: a comma with Latin on one side stays half-width; the Han sentence still ends in 。")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "我买了3,000个", "我买了3,000个。", "cjk-comma-number-grouping: a digit-grouping comma is not between Han and must survive")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "你好,", "你好,", "cjk-comma-trailing: a comma with no Han after it is left alone (and a trailing comma takes no stop)")
    checkStringRule(DictationFormatter.applyCjkPunctuation, "我们明天发布然后再看", "我们明天发布然后再看。", "cjk-stop-joined-seams: span seams joined without a mark still end in 。")

    // v5: the full-width terminal marks are sentence boundaries for
    // capitalization too — an English sentence after a Chinese one gets its
    // capital.
    checkStringRule(DictationFormatter.capitalizeSentences, "我们已经跑通了。 next step is the demo", "我们已经跑通了。 Next step is the demo", "cap-after-full-width-stop: 。 is a sentence boundary")
    checkStringRule(DictationFormatter.capitalizeSentences, "太好吃了！ really", "太好吃了！ Really", "cap-after-full-width-exclamation: ！ is a sentence boundary")
    checkStringRule(DictationFormatter.capitalizeSentences, "3.14 是圆周率。 pi is a constant", "3.14 是圆周率。 Pi is a constant", "cap-after-full-width-stop-digit-decimal: the decimal still does not count, the 。 does")

    // v5: the repairs reach the production entry point, and stay off in
    // terminals.
    checkFormat("我们下午三点开会", "我们下午三点开会。", "format/v5-cjk-stop: the default pipeline ends a Chinese sentence with 。")
    checkFormat("好的,你现在已经知道我的要求", "好的，你现在已经知道我的要求。", "format/v5-cjk-comma: the default pipeline converts a Han-bracketed comma")
    checkFormat("wait, what do you think", "Wait, what do you think", "format/v5-latin-untouched: English keeps its half-width comma and gets NO terminal mark")
    check(DictationFormatter.format("我们下午三点开会", forApp: "com.googlecode.iterm2") == "我们下午三点开会",
          "format/v5-terminal-exempt: a terminal gets the Han text byte-identical")
    for input in ["我们下午三点开会", "好的,你现在已经知道我的要求", "已经好了。"] {
        let once = DictationFormatter.format(input)
        check(DictationFormatter.format(once) == once, "format/v5-idempotency: format(format(\(input.debugDescription))) must equal format once")
    }

    // MARK: stripTrailingEllipsis — the v2 shadow-mined rule, exercised directly

    checkStringRule(DictationFormatter.stripTrailingEllipsis, "trailing off here...", "trailing off here", "ellipsis-strip: removes the trailing three-dot run")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "kept thinking....", "kept thinking", "ellipsis-strip-long-run: a longer dot run is the same artifact, removed whole")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "the answer is 42...", "the answer is 42", "ellipsis-strip-after-digit: strips only the artifact run, keeps the number")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "hold on...\n", "hold on\n", "ellipsis-strip-preserves-trailing-whitespace: whitespace after the run survives byte-for-byte")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "done.", "done.", "ellipsis-guard-single-period: must NOT fire on a real sentence period")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "version 2..", "version 2..", "ellipsis-guard-two-dots: must NOT fire on a two-dot run")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "first... then stop", "first... then stop", "ellipsis-guard-mid-text: must NOT fire on an interior ellipsis (n=8 mined, below support)")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "......", "......", "ellipsis-guard-only-dots: must NOT strip when nothing alphanumeric would remain")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "and then…", "and then…", "ellipsis-guard-unicode: unicode … is out of the mined scope, must NOT fire")
    checkStringRule(DictationFormatter.stripTrailingEllipsis, "", "", "ellipsis-guard-empty: empty stays empty")

    // MARK: capitalizeSentences — the v1 rule format() applies

    checkStringRule(DictationFormatter.capitalizeSentences, "hello world", "Hello world", "cap-first: capitalizes first letter")
    checkStringRule(DictationFormatter.capitalizeSentences, "hi. how are you", "Hi. How are you", "cap-after-period: capitalizes after a real sentence boundary")
    checkStringRule(DictationFormatter.capitalizeSentences, "the score is 3.14 percent accurate", "The score is 3.14 percent accurate", "cap-decimal-safety: a decimal point must NOT be read as a sentence boundary")
    // v4: a word with a capital after its first letter keeps its casing.
    checkStringRule(DictationFormatter.capitalizeSentences, "iPhone battery died", "iPhone battery died", "cap-inner-capital: a word like iPhone is a name, not a missed sentence start")
    checkStringRule(DictationFormatter.capitalizeSentences, "macOS update. iOS too", "macOS update. iOS too", "cap-inner-capital-after-period: the rule holds at every sentence start")
    checkStringRule(DictationFormatter.capitalizeSentences, "iphone battery died", "Iphone battery died", "cap-inner-capital-only: the same word in lower case is capitalized as usual")
    checkStringRule(DictationFormatter.capitalizeSentences, "the iPhone battery died", "The iPhone battery died", "cap-inner-capital-own-word: a capital in a LATER word does not stop the first")
    checkStringRule(DictationFormatter.capitalizeSentences, "e-Commerce is growing", "E-Commerce is growing", "cap-inner-capital-letters-only: a hyphen ends the word")

    // MARK: profile(forApp:) — Iteration 34's per-app gating

    check(
        DictationFormatter.profile(forApp: "com.googlecode.iterm2") == .none,
        "profile-iterm2: terminal must get NO rules at all, not a narrowed set"
    )
    check(
        DictationFormatter.profile(forApp: "com.mitchellh.ghostty") == .none,
        "profile-ghostty: terminal must get NO rules at all"
    )
    check(
        DictationFormatter.profile(forApp: "com.apple.Terminal") == .none,
        "profile-apple-terminal: terminal must get NO rules at all"
    )
    for terminal in ["dev.warp.Warp-Stable", "net.kovidgoyal.kitty", "org.alacritty", "com.github.wez.wezterm", "co.zeit.hyper"] {
        check(
            DictationFormatter.profile(forApp: terminal) == .none,
            "profile-\(terminal): the other common macOS terminals (v4) must get NO rules at all"
        )
    }
    check(
        DictationFormatter.profile(forApp: "com.tinyspeck.slackmacgap") == .standard,
        "profile-slack: prose app gets the standard default (capitalization + shadow-mined trailing-ellipsis strip), nothing richer"
    )
    check(
        DictationFormatter.profile(forApp: "com.google.Chrome") == .standard,
        "profile-chrome: prose app gets the standard default"
    )
    check(
        DictationFormatter.profile(forApp: "com.anthropic.claudefordesktop") == .standard,
        "profile-claude-desktop: prose app gets the standard default"
    )
    check(
        DictationFormatter.profile(forApp: "com.unknown.some-random-app") == .standard,
        "profile-unrecognized: an app with no per-app measurement falls back to the conservative global default, never to the ungated rule set"
    )
    check(
        DictationFormatter.profile(forApp: nil) == .standard,
        "profile-nil: no target-app signal at all falls back to the same conservative default"
    )
    check(
        DictationFormatter.profile(forApp: "") == .standard,
        "profile-empty-string: an empty bundle ID is treated the same as nil, not as an unrecognized-but-real app"
    )
    check(
        DictationFormatter.AppProfile.standard == DictationFormatter.AppProfile([.capitalization, .trailingEllipsis, .cjkPunctuation]),
        "profile-standard-composition: v5's default is exactly capitalization + trailingEllipsis + cjkPunctuation — a rule added here needs its own mined support first"
    )

    // The terminal-never-formatted invariant, exercised through the real
    // per-app entry point end to end, not just the profile lookup: no
    // terminal bundle ID may ever produce a DIFFERENT string than its
    // input, for ANY of the individual rules' trigger patterns, including
    // ones that fire safely elsewhere (capitalization).
    for terminalApp in [
        "com.googlecode.iterm2", "com.mitchellh.ghostty", "com.apple.Terminal",
        "dev.warp.Warp-Stable", "net.kovidgoyal.kitty", "org.alacritty", "com.github.wez.wezterm", "co.zeit.hyper",
    ] {
        for input in [
            "hello world",                    // would capitalize elsewhere
            "and so I think we should go",    // would strip leading disfluency elsewhere
            "wait five minutes",              // would convert a number elsewhere
            "git commit -m fix",               // realistic terminal fragment
            "make clean...",                  // would strip the trailing ellipsis elsewhere (v2)
        ] {
            let got = DictationFormatter.format(input, forApp: terminalApp)
            check(got == input, "terminal-never-formatted/\(terminalApp): format(\(input.debugDescription), forApp:) must return the input unchanged, got \(got.debugDescription)")
        }
    }

    // A prose target DOES still get capitalization through the per-app
    // entry point (not a no-op-everywhere regression from wiring the gate).
    check(
        DictationFormatter.format("hello world", forApp: "com.tinyspeck.slackmacgap") == "Hello world",
        "prose-still-formats: a measured-safe prose app must still get capitalization through format(_:forApp:)"
    )
    check(
        DictationFormatter.format("thinking about it...", forApp: "com.tinyspeck.slackmacgap") == "Thinking about it",
        "prose-strips-ellipsis: the v2 trailing-ellipsis strip must also apply through the per-app entry point on prose targets"
    )

    // format(_:forApp:) with no bundle ID must agree exactly with format(_:)
    // — the no-provider-wired default must be behaviorally invisible.
    for input in ["hello world", "already ends with a period.", "and so I think", "trailing off now..."] {
        let viaApp = DictationFormatter.format(input, forApp: nil)
        let viaDefault = DictationFormatter.format(input)
        check(viaApp == viaDefault, "format-forApp-nil-matches-default: format(\(input.debugDescription), forApp: nil) must equal format(_:), got \(viaApp.debugDescription) vs \(viaDefault.debugDescription)")
    }

    // formatAllRulesUngated — the control configuration re-exposed for the
    // segmented eval, sanity-checked here so a future edit to `apply(_:rules:)`'s
    // rule set can't silently narrow the control without a self-check catching it.
    check(
        DictationFormatter.formatAllRulesUngated("and so wait five minutes") != "and so wait five minutes",
        "all-rules-ungated-changes-something: the control configuration must actually apply more than format(_:) on an input matching multiple rules"
    )

    // MARK: tokenizer round-trip — the invariant every piece-based rule depends on

    for input in [
        "Hello, world! It's fine.",
        "twenty-five",
        "don't stop",
        "3.14 percent",
        "",
        "   leading and trailing whitespace   ",
    ] {
        let pieces = DictationFormatter.tokenize(input)
        let rendered = DictationFormatter.render(pieces)
        check(rendered == input, "tokenizer-round-trip: render(tokenize(\(input.debugDescription))) must reproduce the input exactly, got \(rendered.debugDescription)")
    }

    guard failures.isEmpty else {
        let first = failures[0]
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-formatter-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("dictation-formatter-check: PASS")
    return true
}
