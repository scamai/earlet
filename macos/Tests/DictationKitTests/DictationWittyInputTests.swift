import XCTest
@testable import DictationKit

final class DictationWittyInputTests: XCTestCase {
    private func screen(_ draft: String, history: String = "PRIVATE OUTPUT", footer: String = "tab to queue message    42% context left") -> String {
        history + "\n \n› " + draft.replacingOccurrences(of: "\n", with: "\n  ") + "\n \n  " + footer
    }

    func testCurrentAndLegacyIdentityRouteOnlyTheComposerIntoTheAnchorSearch() {
        for bundleID in ["ai.scam.witty", "dev.shenxingy.witty"] {
            let field = DictationCorrectionObserver.fieldSnapshot(
                value: screen("We use Trovioo"), selectedRange: CFRange(location: 7, length: 2),
                bundleID: bundleID)
            XCTAssertEqual(field.value, " We use Trovioo ")
            XCTAssertNil(field.selectionLocation)
            XCTAssertNil(field.selectionLength)
            let unknownLayout = DictationCorrectionObserver.fieldSnapshot(
                value: "PRIVATE OUTPUT", selectedRange: CFRange(location: 7, length: 2), bundleID: bundleID)
            XCTAssertNil(unknownLayout.value, "An unrecognized terminal layout must never become an editable field")
        }
    }

    func testOnlyExactReleaseIdentitiesSelectTheTerminalAdapter() {
        for bundleID in ["", "ai.scam.witty.debug", "ai.scam.witty.other", "other.ai.scam.witty", "com.example.editor"] {
            XCTAssertFalse(DictationWittyInput.supports(bundleID: bundleID))
        }
        let field = DictationCorrectionObserver.fieldSnapshot(
            value: "Editable text", selectedRange: CFRange(location: 4, length: 2), bundleID: "com.example.editor")
        XCTAssertEqual(field.value, "Editable text")
        XCTAssertEqual(field.selectionLocation, 4)
        XCTAssertEqual(field.selectionLength, 2)
    }

    func testPlaceholderIsEmptyAndOnlyTheFinalComposerIsReturned() throws {
        let input = try XCTUnwrap(DictationWittyInput.parse(screen("Ask Codex to do anything",
            history: "› Previous user message\n\nAssistant output", footer: "Main [default] · ← for agents    42% context left")))
        XCTAssertEqual(input.value, " ")
        XCTAssertEqual(input.boundaryWords, [])
        XCTAssertEqual(DictationWittyInput.parse(screen("We use Mies"))?.value, " We use Mies ")
    }

    func testNewInputCanAnchorWhileTheWholeScreenChanges() throws {
        let before = try XCTUnwrap(DictationWittyInput.parse(screen("Ask Codex to do anything")))
        let after = try XCTUnwrap(DictationWittyInput.parse(screen("We use Mies", history: "OTHER OUTPUT")))
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            beforePaste: .init(value: before.value, selectionLocation: nil, selectionLength: nil),
            fieldValue: after.value, pasted: DictationWittyInput.pasted("We use Mies")))
        let edited = try XCTUnwrap(DictationWittyInput.parse(screen("We use Miles", history: "STREAMING OUTPUT")))
        XCTAssertEqual(anchor.span(in: try XCTUnwrap(edited.value(matching: after))), "We use Miles ")
        XCTAssertFalse(edited.value.contains("OUTPUT"))
    }

    func testIdleComposerKeepsItsAnchorWhenTheLeftFooterHintDisappears() throws {
        let empty = try XCTUnwrap(DictationWittyInput.parse(screen("Ask Codex to do anything",
            footer: "Main [default] · ← for agents    49% context left    ⚠ 1 warning · f2 to view")))
        for footer in ["49% context left", "100% context left    ⚠ 3 warnings · f2 to view"] {
            let initial = try XCTUnwrap(DictationWittyInput.parse(screen("We use Mies", footer: footer)))
            let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
                beforePaste: .init(value: empty.value, selectionLocation: nil, selectionLength: nil),
                fieldValue: initial.value, pasted: DictationWittyInput.pasted("We use Mies")))
            let edited = try XCTUnwrap(DictationWittyInput.parse(screen("We use Miles", footer: footer)))
            XCTAssertEqual(anchor.span(in: try XCTUnwrap(edited.value(matching: initial))), "We use Miles ")
            let running = try XCTUnwrap(DictationWittyInput.parse(screen("We use Miles")))
            XCTAssertEqual(running.value(matching: initial), edited.value)
        }
        for footer in ["101% context left", "-1% context left", "49% context left extra text",
                       "49% context left    ⚠ read private output", "context left", "0%"] {
            XCTAssertNil(DictationWittyInput.parse(screen("We use Mies", footer: footer)), footer)
        }
    }

    func testWordWrappingAndWhitespaceDoNotAlterThePasteProof() throws {
        let before = try XCTUnwrap(DictationWittyInput.parse(screen("The repo works.")))
        let pasted = "We have a server called Mies and use it to run our tests."
        let after = try XCTUnwrap(DictationWittyInput.parse(screen("The repo works. We have a server called Mies\nand use it to run our tests.")))
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            beforePaste: .init(value: before.value, selectionLocation: nil, selectionLength: nil),
            fieldValue: after.value, pasted: DictationWittyInput.pasted(pasted)))
        // A name at the row boundary is deliberately not accepted as a fix.
        let changedBoundary = try XCTUnwrap(DictationWittyInput.parse(screen("The repo works. We have a server called Miles\nand use it to run our tests.")))
        XCTAssertNil(changedBoundary.value(matching: after))
        let initial = try XCTUnwrap(DictationWittyInput.parse(screen("The repo works. We have a server called Mies and\nuse it to run our tests.")))
        let edited = try XCTUnwrap(DictationWittyInput.parse(screen("The repo works. We have a server called Miles and\nuse it to run our tests.")))
        XCTAssertEqual(anchor.span(in: try XCTUnwrap(edited.value(matching: initial))),
                       "We have a server called Miles and use it to run our tests. ")
        XCTAssertNil(anchor.span(in: " OTHER We have a server called Miles and use it to run our tests. "))
    }

    func testTwoRowContextStatusCanAnchorAndObserveAnEdit() throws {
        // The narrow live terminal has a model/context row followed by navigation.
        // These are the exact two rows observed through AX20 while a draft was live.
        let narrow = "GPT-6-Astra max · Context 74% l…\n  ← for agents · ? for   ⚠ 1 · f2"
        let wide = "GPT-6-Astra max · Context 54% left · workspace\n  ← for agents · ? for shortcuts    ⚠ 1 warning · f2 to view"
        let queued = "GPT-6-Astra max · Context 55% left · we…\n  tab to queue message"
        let before = try XCTUnwrap(DictationWittyInput.parse(screen("Ask Codex to do anything", footer: narrow)))
        let after = try XCTUnwrap(DictationWittyInput.parse(screen("We use Mies", footer: queued)))
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            beforePaste: .init(value: before.value, selectionLocation: nil, selectionLength: nil),
            fieldValue: after.value, pasted: DictationWittyInput.pasted("We use Mies")))
        let edited = try XCTUnwrap(DictationWittyInput.parse(screen("We use Miles", history: "CHANGED OUTPUT", footer: queued)))
        XCTAssertEqual(anchor.span(in: try XCTUnwrap(edited.value(matching: after))), "We use Miles ")
        XCTAssertFalse(edited.value.contains("OUTPUT"))
        XCTAssertFalse(edited.value.contains("Context"))
        XCTAssertEqual(DictationWittyInput.parse(screen("We use Miles", footer: wide))?.value, edited.value)
    }

    func testObservedTwoRowFooterHandlesEmptyAndMultilineDrafts() throws {
        let footer = "GPT-6-Astra max · Context 74% l…\n  ← for agents · ? for   ⚠ 1 · f2"
        let empty = try XCTUnwrap(DictationWittyInput.parse(screen(
            "Ask Codex to do anything", footer: footer)))
        XCTAssertEqual(empty.value, " ")
        let multiline = try XCTUnwrap(DictationWittyInput.parse(screen(
            "first line\nsecond line", footer: footer)))
        XCTAssertEqual(multiline.value, " first line second line ")
    }

    func testOriginalPairedNavigationWithoutWarningOrF2StillParses() throws {
        let footer = "GPT-6-Astra max · Context 55% left\n  ← for agents · ? for shortcuts"
        let parsed = try XCTUnwrap(DictationWittyInput.parse(screen("We use Mies", footer: footer)))
        XCTAssertEqual(parsed.value, " We use Mies ")
    }

    func testTwoRowStatusStillRequiresItsCompleteFooterAndComposerBoundary() {
        let status = "GPT-6-Astra max · Context 55% left · workspace"
        let navigation = "← for agents · ? for shortcuts"
        for footer in [status, navigation, status + "\n  unrecognized status",
                       "unrecognized status\n  " + navigation,
                       status.replacingOccurrences(of: "55%", with: "101%") + "\n  " + navigation,
                       status + "\n  " + navigation + "\n  OTHER OUTPUT"] {
            XCTAssertNil(DictationWittyInput.parse(screen("We use Mies", footer: footer)), footer)
        }
        let footer = "  " + status + "\n  " + navigation
        XCTAssertNil(DictationWittyInput.parse("OUTPUT\n› We use Mies\n\n" + footer))
        XCTAssertNil(DictationWittyInput.parse("› Old message\n\nAssistant output\n\n" + footer))
        XCTAssertNil(DictationWittyInput.parse(screen("[Pasted Content 2000 chars]", footer: status + "\n  " + navigation)))
        XCTAssertNil(DictationWittyInput.parse(screen(
            "draft\n" + status + "\n" + navigation,
            footer: "GPT-6-Astra max · Context 54% left\n  ← for agents · ? for shortcuts")))
    }

    func testSubmissionOrAChangedLayoutCannotMineTheTranscript() throws {
        let initial = try XCTUnwrap(DictationWittyInput.parse(screen("We use Mies")))
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            beforePaste: .init(value: " ", selectionLocation: nil, selectionLength: nil),
            fieldValue: initial.value, pasted: DictationWittyInput.pasted("We use Mies")))
        let submitted = try XCTUnwrap(DictationWittyInput.parse(screen("Ask Codex to do anything", history: "› We use Miles")))
        XCTAssertEqual(anchor.span(in: submitted.value), "")
        for bad in [
            "› We use Mies\nassistant output", // no footer
            screen("[Pasted Content 2000 chars]"),
            screen("[Image #1]"), screen("clipped text ↓"),
            "› Old message\n\nAssistant response\n\n  tab to queue message",
            screen("We use Mies", footer: "unrecognized status"),
            "OUTPUT\n› We use Mies\n\n  tab to queue message", // no composer boundary
            screen(String(repeating: "x", count: 8_193)),
            screen((0..<33).map { "row\($0)" }.joined(separator: "\n"))
        ] { XCTAssertNil(DictationWittyInput.parse(bad)) }
    }

    func testMidwordWrapCannotBecomeAFalseNewVocabularyWord() throws {
        let split = try XCTUnwrap(DictationWittyInput.parse(screen("We use Mi\nles")))
        XCTAssertNil(DictationSpanAnchor.establish(
            beforePaste: .init(value: " ", selectionLocation: nil, selectionLength: nil),
            fieldValue: split.value, pasted: DictationWittyInput.pasted("We use Miles")))
        let original = try XCTUnwrap(DictationWittyInput.parse(screen("We use Miles")))
        XCTAssertNil(split.value(matching: original))
        XCTAssertEqual(DictationWittyInput.pasted("  "), "")
        XCTAssertEqual(DictationWittyInput.pasted("我们用 Miles。"), "我们用 Miles。 ")
    }

    // ─── Claude Code ───

    /// A Claude Code screen as Witty exposes it: transcript, the composer
    /// between two rules as wide as the terminal, and indented status rows.
    /// Rows are the composer's own display rows, without its gutter.
    private func claudeScreen(_ rows: [String], columns: Int = 40, label: String? = nil,
                              status: [String] = ["project git:(main) +5% (6d)",
                                                  "⏵⏵ bypass permissions on (shift+tab to cycle)"],
                              history: String = "⏺ PRIVATE OUTPUT") -> String {
        let rule = String(repeating: "─", count: columns)
        let top = label.map { String(repeating: "─", count: columns - $0.count - 3) + " " + $0 + " ─" } ?? rule
        let composer = rows.enumerated().map { $0.offset == 0 ? "❯\u{00A0}" + $0.element : "  " + $0.element }
        return ([history, "", top] + composer + [rule] + status.map { "  " + $0 }).joined(separator: "\n")
    }

    private func claudeScreen50(_ rows: [String], history: String = "⏺ PRIVATE OUTPUT") -> String {
        claudeScreen(rows, columns: 50, history: history)
    }

    private func anchor(_ before: DictationWittyInput, _ after: DictationWittyInput, pasted: String) -> DictationSpanAnchor? {
        DictationSpanAnchor.establish(
            beforePaste: .init(value: before.value, selectionLocation: nil, selectionLength: nil),
            fieldValue: after.value, pasted: DictationWittyInput.pasted(pasted))
    }

    func testClaudeCodeEmptyComposerAndItsPlaceholdersReadAsEmpty() throws {
        for rows in [[""], ["Try \"write a test for main.swift\""], ["Try \"how does README.md work?…"],
                     ["Press up to edit queued messages"],
                     ["Press up to select a queued message to edit, or Enter to send…"]] {
            for label: String? in [nil, "ultracode"] {
                let input = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(rows, label: label)), "\(rows)")
                XCTAssertEqual(input.value, " ")
                XCTAssertEqual(input.boundaryWords, [])
            }
        }
        // The prompt glyph alone, once the terminal trimmed the no-break space.
        let bare = claudeScreen([""]).replacingOccurrences(of: "❯\u{00A0}", with: "❯")
        XCTAssertEqual(DictationWittyInput.parse(bare)?.value, " ")
    }

    func testClaudeCodeUpperRuleMayBeMostlyItsLabel() throws {
        // A narrow terminal: the truncated label fills the rule's left side.
        let rule = String(repeating: "─", count: 35)
        let upper = " feature-branch-with-a-long-name… ─"
        XCTAssertEqual(DictationWittyInput.displayWidth(upper), 35)
        let screen = ["⏺ PRIVATE OUTPUT", "", upper, "❯\u{00A0}We use Mies", rule, "  status"].joined(separator: "\n")
        XCTAssertEqual(DictationWittyInput.parse(screen)?.value, " We use Mies ")
        // Any other width, or a label that does not end one dash from the
        // right, is not this composer.
        for bad in [" feature-branch-with-a-long-name ─", " feature-branch-with-a-long-name…──",
                    "xfeature-branch-with-a-long-name… ─"] {
            XCTAssertNil(DictationWittyInput.parse(screen.replacingOccurrences(of: upper, with: bad)), bad)
        }
    }

    func testClaudeCodeDraftAnchorsAndAnEditIsObservedWhileOutputChanges() throws {
        let before = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(["Try \"fix lint errors\""])))
        let after = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(["We use Mies"])))
        let span = try XCTUnwrap(anchor(before, after, pasted: "We use Mies"))
        let edited = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(
            ["We use Miles"], history: "⏺ STREAMING OUTPUT\n✶ Thinking… (12s)")))
        XCTAssertEqual(span.span(in: try XCTUnwrap(edited.value(matching: after))), "We use Miles ")
        XCTAssertFalse(edited.value.contains("OUTPUT"))
        XCTAssertFalse(edited.value.contains("bypass"))
        let routed = DictationCorrectionObserver.fieldSnapshot(
            value: claudeScreen(["We use Mies"]), selectedRange: CFRange(location: 3, length: 0),
            bundleID: "ai.scam.witty")
        XCTAssertEqual(routed.value, " We use Mies ")
        XCTAssertNil(routed.selectionLocation)
    }

    func testClaudeCodeJoinsHardWrappedChineseWithoutASpace() throws {
        // 40 columns: Chinese fills a row and continues on the next with no
        // space; Latin words wrap at a space the wrap consumed.
        let pasted = "我们最近这个应用的转写速度不太稳定有时候很快有时候又要等好几秒。Please check the Earlet worker logs"
        let rows = ["我们最近这个应用的转写速度不太稳定有时", "候很快有时候又要等好几秒。Please", "check the Earlet worker logs"]
        let after = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(rows)))
        XCTAssertEqual(after.value, " " + pasted + " ")
        XCTAssertEqual(after.boundaryWords, [])
        XCTAssertNotNil(anchor(try XCTUnwrap(DictationWittyInput.parse(claudeScreen([""]))), after, pasted: pasted))
    }

    func testClaudeCodeEditThatReflowsChineseKeepsItsAnchor() throws {
        let pasted = "我们用扣的写代码然后在微信里面发给同事看一下这个结果怎么样"
        let initial = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(
            ["我们用扣的写代码然后在微信里面发给同事", "看一下这个结果怎么样"])))
        let span = try XCTUnwrap(anchor(try XCTUnwrap(DictationWittyInput.parse(claudeScreen([""]))),
                                        initial, pasted: pasted))
        // The user replaces 扣的 with Claude Code: every later character moves.
        let edited = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(
            ["我们用Claude Code写代码然后在微信里面", "发给同事看一下这个结果怎么样"])))
        XCTAssertEqual(span.span(in: try XCTUnwrap(edited.value(matching: initial))),
                       "我们用Claude Code写代码然后在微信里面发给同事看一下这个结果怎么样 ")
    }

    func testClaudeCodeShortRowBreakIsTheSpaceTheWrapConsumed() throws {
        let latin = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(
            ["aaaa bbbb cccc dddd eeee ffff gggg", "hhh iiii"])))
        XCTAssertEqual(latin.value, " aaaa bbbb cccc dddd eeee ffff gggg hhh iiii ")
        XCTAssertEqual(latin.boundaryWords, [])
        let mixed = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(["我们用", "Miles 来测试"])))
        XCTAssertEqual(mixed.value, " 我们用 Miles 来测试 ")
        let blank = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(["first line", "", "second line"])))
        XCTAssertEqual(blank.value, " first line second line ")
    }

    func testClaudeCodeSplitLongWordCannotBecomeAFalseCorrection() throws {
        // A word longer than a row is cut at the row's end: the reading puts a
        // space there, so a paste holding the whole word does not anchor, and
        // a boundary that changes while the span is watched ends the watch.
        let rows = ["see", "https://example.com/a/very/long/path", "/that/never/ends/index.html then"]
        let split = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(rows)))
        XCTAssertEqual(split.boundaryWords, ["https://example.com/a/very/long/path\n/that/never/ends/index.html"])
        XCTAssertNil(anchor(try XCTUnwrap(DictationWittyInput.parse(claudeScreen([""]))), split,
                            pasted: "see https://example.com/a/very/long/path/that/never/ends/index.html then"))
        let reflowed = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(
            ["see", "https://example.com/a/very/long/pat", "h/that/never/ends/index.html then"])))
        XCTAssertNil(reflowed.value(matching: split))
        XCTAssertNotNil(split.value(matching: split))
    }

    func testClaudeCodeWideTerminalRows() throws {
        // 80 columns, as rendered by Claude Code 2.1: rows end four columns
        // short of the terminal, and a Latin row that fills it is ambiguous.
        let rows = ["我们最近这个应用的转写速度不太稳定有时候很快有时候又要等好几秒你能不能看一下", "日志判断一下是不是预览的解码和最终的解码在抢同一个模型和计算资源呢吧。Please",
                    "check the Witty composer and the Earlet worker logs carefully, then tell me", "what you find."]
        let input = try XCTUnwrap(DictationWittyInput.parse(claudeScreen(rows, columns: 80, label: "ultracode")))
        XCTAssertEqual(input.value, " " + rows[0] + rows[1] + " " + rows[2] + " " + rows[3] + " ")
        XCTAssertEqual(input.boundaryWords, ["日志判断一下是不是预览的解码和最终的解码在抢同一个模型和计算资源呢吧。Please\ncheck", "me\nwhat"])
    }

    func testClaudeCodeRefusesEveryOtherLayout() {
        let rule = String(repeating: "─", count: 40)
        let good = claudeScreen(["We use Mies"])
        XCTAssertNotNil(DictationWittyInput.parse(good))
        for bad in [
            claudeScreen(["[Pasted text #1 +12 lines]"]),
            claudeScreen(["See [Image #1]"]),
            claudeScreen(["We use Mies"]) + "\nunindented status",
            claudeScreen(["We use Mies"], status: (0..<9).map { "status row \($0)" }),
            claudeScreen(["We use Mies"]).replacingOccurrences(of: "❯\u{00A0}", with: "> "),
            "⏺ OUTPUT\n" + rule + "\n❯\u{00A0}We use Mies\nnot indented\n" + rule + "\n  status",
            "⏺ OUTPUT\n" + String(repeating: "─", count: 39) + "\n❯\u{00A0}We use Mies\n" + rule + "\n  status",
            "⏺ OUTPUT\n❯\u{00A0}We use Mies\n" + rule + "\n  status",
            rule + "\n" + rule + "\n  status",
            claudeScreen((0..<41).map { "row \($0)" }),
            claudeScreen([String(repeating: "x", count: 8_193)]),
            // A tmux split: the composer shares its rows with another pane,
            // and tmux's own status line ends the screen.
            [String(repeating: "─", count: 20) + "│" + String(repeating: "─", count: 20),
             "❯\u{00A0}We use Mies         │❯\u{00A0}",
             String(repeating: "─", count: 20) + "│" + String(repeating: "─", count: 20),
             "  status            │  status", "main 1:claude 2:zsh"].joined(separator: "\n"),
        ] {
            XCTAssertNil(DictationWittyInput.parse(bad), bad)
        }
    }

    // ─── tmux panes ───

    /// Panes side by side, each padded to `width` columns, joined by tmux's
    /// border, with tmux's status line last.
    private func tmux(_ panes: [String], width: Int = 50, status: String = " main   1:claude   2:zsh") -> String {
        let split = panes.map { $0.components(separatedBy: "\n") }
        let height = split.map(\.count).max() ?? 0
        let padded = split.map { Array(repeating: "", count: height - $0.count) + $0 }
        let rows = (0..<height).map { row in
            padded.map { pane in
                pane[row] + String(repeating: " ", count: width - DictationWittyInput.displayWidth(pane[row]))
            }.joined(separator: "│")
        }
        return (rows + [status]).joined(separator: "\n")
    }

    func testTmuxPanesAnchorOnlyInThePaneThePasteWentTo() throws {
        let before = try XCTUnwrap(DictationWittyInput.parse(tmux([claudeScreen50([""]), claudeScreen50(["Try \"fix lint errors\""])])))
        XCTAssertEqual(before.value, " \u{1E} ")
        let after = try XCTUnwrap(DictationWittyInput.parse(tmux([claudeScreen50([""]), claudeScreen50(["We use Mies"])])))
        let span = try XCTUnwrap(anchor(before, after, pasted: "We use Mies"))
        let edited = try XCTUnwrap(DictationWittyInput.parse(tmux([
            claudeScreen50([""], history: "⏺ OTHER PANE OUTPUT"), claudeScreen50(["We use Miles"])])))
        XCTAssertEqual(span.span(in: try XCTUnwrap(edited.value(matching: after))), "We use Miles ")
        // Typing in the other pane is outside the span: the anchor no longer holds.
        let other = try XCTUnwrap(DictationWittyInput.parse(tmux([claudeScreen50(["hi"]), claudeScreen50(["We use Miles"])])))
        XCTAssertNil(span.span(in: other.value))
    }

    func testTmuxPaneWithoutAComposerCountsAsEmptyText() throws {
        let shell = "➜  project git:(main) ls\nREADME.md\n➜  project git:(main)"
        let input = try XCTUnwrap(DictationWittyInput.parse(tmux([shell, claudeScreen50(["我们用 Miles 来测试"])])))
        XCTAssertEqual(input.value, "\u{1E} 我们用 Miles 来测试 ")
        XCTAssertNil(DictationWittyInput.parse(tmux([shell, shell])), "no pane has a composer")
    }

    func testTmuxStackedPanesSplitAtTheBorderJunction() throws {
        // The right column holds two panes, one above the other.
        let upper = claudeScreen50(["upper draft"]).components(separatedBy: "\n")
        let lower = claudeScreen50(["lower draft"]).components(separatedBy: "\n")
        let left = claudeScreen50([""]).components(separatedBy: "\n")
        let height = upper.count + 1 + lower.count
        let leftRows = Array(repeating: "", count: height - left.count) + left
        var rows: [String] = []
        for index in 0..<height {
            let pad = { (text: String) in text + String(repeating: " ", count: 50 - DictationWittyInput.displayWidth(text)) }
            if index == upper.count {
                rows.append(pad(leftRows[index]) + "├" + String(repeating: "─", count: 50))
            } else {
                let right = index < upper.count ? upper[index] : lower[index - upper.count - 1]
                rows.append(pad(leftRows[index]) + "│" + right)
            }
        }
        let input = try XCTUnwrap(DictationWittyInput.parse((rows + [" main   1:claude"]).joined(separator: "\n")))
        XCTAssertEqual(input.value, " \u{1E} upper draft \u{1E} lower draft ")
    }

    func testTmuxRefusesBordersThatDoNotHoldTheirColumn() {
        let good = tmux([claudeScreen50([""]), claudeScreen50(["We use Mies"])])
        XCTAssertNotNil(DictationWittyInput.parse(good))
        var rows = good.components(separatedBy: "\n")
        // One row's border moves a column: these are not panes.
        rows[3] = " " + rows[3]
        XCTAssertNil(DictationWittyInput.parse(rows.joined(separator: "\n")))
        // A line without the borders between pane rows is not a tmux window,
        // even when it sits in the output above the composers.
        let tall = tmux([claudeScreen50([""], history: "⏺ ONE\nTWO\nTHREE"),
                         claudeScreen50(["We use Mies"], history: "⏺ ONE\nTWO\nTHREE")])
        XCTAssertNotNil(DictationWittyInput.parse(tall))
        var middle = tall.components(separatedBy: "\n")
        middle[1] = " main   1:claude"
        XCTAssertNil(DictationWittyInput.parse(middle.joined(separator: "\n")))
        // Stacked panes alone: a horizontal border is no different from a
        // composer's rule, so this fails closed.
        XCTAssertNil(DictationWittyInput.parse(claudeScreen50(["upper"]) + "\n" + String(repeating: "─", count: 50)
                                               + "\n" + claudeScreen50(["lower"]) + "\n main   1:claude"))
    }
}
