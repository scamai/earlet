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
}
