import XCTest
@testable import DictationKit

final class DictationRevisionTests: XCTestCase {
    func testStableRevisionsIncludeRetractionAndNeverResettleIdenticalText() {
        var revisions = DictationStableRevisions()
        let original = "Use Mies today"
        XCTAssertNil(revisions.takeIfSettled(span: original, original: original, quietSeconds: 8, settleSeconds: 4))
        XCTAssertNil(revisions.takeIfSettled(span: "Use Miles today", original: original, quietSeconds: 3.9, settleSeconds: 4))
        XCTAssertEqual(revisions.takeIfSettled(span: "Use Miles today", original: original, quietSeconds: 4, settleSeconds: 4), "Use Miles today")
        XCTAssertFalse(revisions.needsSettlement(span: "Use Miles today", original: original))
        XCTAssertNil(revisions.takeIfSettled(span: "Use Miles today", original: original, quietSeconds: 8, settleSeconds: 4))
        XCTAssertEqual(revisions.takeIfSettled(span: "Use Mils today", original: original, quietSeconds: 4, settleSeconds: 4), "Use Mils today")
        XCTAssertTrue(revisions.needsSettlement(span: original, original: original))
        XCTAssertEqual(revisions.takeIfSettled(span: original, original: original, quietSeconds: 4, settleSeconds: 4), original)
        XCTAssertNil(revisions.takeIfSettled(span: nil, original: original, quietSeconds: 8, settleSeconds: 4))
        XCTAssertNil(revisions.takeIfSettled(span: original, original: original, quietSeconds: 8, settleSeconds: 4))
    }

    func testASentDraftCountsOnlyWhenTheWholeFieldEmptiedAfterAStillEdit() {
        let original = "We use Mies "
        func sent(_ condition: DictationTerminalCondition = .fieldCleared, anchored: String? = nil,
                  last: String? = "We use Miles ", quiet: TimeInterval = 1.2,
                  revisions: inout DictationStableRevisions) -> String? {
            DictationSentDraft.revision(condition: condition, anchoredSpanText: anchored, lastSpan: last,
                                        dictatedText: original, quietSeconds: quiet, sendSettleSeconds: 1,
                                        revisions: &revisions)
        }
        var revisions = DictationStableRevisions()
        XCTAssertNil(sent(quiet: 0.6, revisions: &revisions), "a word may still have been half typed")
        XCTAssertNil(sent(last: original, revisions: &revisions), "an untouched dictation is no evidence")
        XCTAssertNil(sent(last: nil, revisions: &revisions), "no span was located before the send")
        XCTAssertNil(sent(anchored: "", revisions: &revisions), "the span was deleted inside a field that kept text")
        XCTAssertNil(sent(.spanContextLost, revisions: &revisions))
        XCTAssertNil(sent(.focusLeft, revisions: &revisions))
        XCTAssertEqual(sent(revisions: &revisions), "We use Miles ")
        XCTAssertNil(sent(revisions: &revisions), "the same text is not published twice")
        var settled = DictationStableRevisions()
        XCTAssertEqual(settled.takeIfSettled(span: "We use Miles ", original: original, quietSeconds: 4, settleSeconds: 4),
                       "We use Miles ")
        XCTAssertNil(sent(revisions: &settled), "already published when it settled")
    }

    func testAWittySendEmptiesTheComposerAndEndsAsFieldCleared() throws {
        // A two-row Codex draft with a boundary; Return empties the composer.
        let screen = { (draft: String) in
            "OUTPUT\n \n› " + draft.replacingOccurrences(of: "\n", with: "\n  ") + "\n \n  tab to queue message    42% context left"
        }
        let initial = try XCTUnwrap(DictationWittyInput.parse(screen("We have a server called Mies and\nuse it")))
        XCTAssertFalse(initial.boundaryWords.isEmpty)
        let edited = try XCTUnwrap(DictationWittyInput.parse(screen("We have a server called Miles and\nuse it")))
        let emptied = try XCTUnwrap(DictationWittyInput.parse(screen("Ask Codex to do anything")))
        let empty = try XCTUnwrap(emptied.value(matching: initial), "an empty composer is never a layout change")
        let tick = DictationObservationTick(
            anchorStillValid: false, anchoredSpanText: nil, fullFieldValue: empty,
            previousFieldValue: try XCTUnwrap(edited.value(matching: initial)), dictatedText: "We have a server called Mies and use it ",
            elapsedSeconds: 10, windowSeconds: 90, editDistanceThreshold: 0.6)
        XCTAssertEqual(DictationTerminalConditionEvaluator.evaluate(tick), .fieldCleared)
    }
}

