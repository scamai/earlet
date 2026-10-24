import XCTest
@testable import DictationKit

final class DictationTerminalConditionNamesTests: XCTestCase {
    func testOriginalEightConditionsUseCanonicalNames() {
        let pairs: [(String, DictationTerminalCondition)] = [
            ("span_missing", .spanMissing),
            ("next_take", .nextTake),
            ("line_submitted", .lineSubmitted),
            ("context_changed_span_intact", .contextChangedSpanIntact),
            ("watch_expired", .watchExpired),
            ("span_context_lost", .spanContextLost),
            ("revision_too_large", .revisionTooLarge),
            ("field_cleared", .fieldCleared),
        ]
        let canonical = [
            "span_missing", "next_take", "line_submitted", "context_changed_span_intact",
            "watch_expired", "span_context_lost", "revision_too_large", "field_cleared",
        ]
        for ((name, condition), expected) in zip(pairs, canonical) {
            XCTAssertEqual(DictationTerminalCondition(rawValue: name), condition)
            XCTAssertEqual(DictationTerminalCondition(rawValue: expected), condition)
            XCTAssertEqual(condition.rawValue, expected)
            XCTAssertEqual(condition.description, expected)
        }
    }

    func testUnknownRawValuesRemainRejected() {
        for raw in ["", "unknown", "dictated_text_missing", "span_missing_extra", "SPAN_MISSING"] {
            XCTAssertNil(DictationTerminalCondition(rawValue: raw), raw)
        }
    }

    func testAdditionalConditionsRoundTrip() {
        let original: Set<DictationTerminalCondition> = [
            .spanMissing, .nextTake, .lineSubmitted,
            .contextChangedSpanIntact, .watchExpired, .spanContextLost,
            .revisionTooLarge, .fieldCleared,
        ]
        for condition in DictationTerminalCondition.allCases where !original.contains(condition) {
            XCTAssertEqual(DictationTerminalCondition(rawValue: condition.rawValue), condition)
        }
    }

    func testEligibilityAndReportedSpanSemanticsRemainUnchanged() {
        for condition in [DictationTerminalCondition.lineSubmitted,
                          .contextChangedSpanIntact, .watchExpired,
                          .fieldCleared, .editSettled, .focusLeft] {
            XCTAssertEqual(condition.reportedSpan("edited"), "edited")
        }
        for condition in [DictationTerminalCondition.spanMissing,
                          .nextTake, .spanContextLost, .revisionTooLarge,
                          .abandoned, .secureField, .otherApplication, .noFocusedField] {
            XCTAssertNil(condition.reportedSpan("edited"))
        }
    }
}
