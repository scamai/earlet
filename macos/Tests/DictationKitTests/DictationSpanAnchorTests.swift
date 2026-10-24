import XCTest
import ApplicationServices
@testable import DictationKit

/// The correction observer's anchor, its admission refusals and what an end
/// may report (LEARNING.md §7), without Accessibility: every function here is
/// pure, or takes its field reads as a closure.
final class DictationSpanAnchorTests: XCTestCase {

    private func utf16(_ text: String) -> Int { text.utf16.count }

    // ─── Establishing the anchor from the caret ───

    func testTheSpanIsDerivedFromTheCaretThePasteLeft() throws {
        let field = "Notes: We use trovio"
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: utf16(field), selectionLength: 0, pasted: "We use trovio"))
        XCTAssertEqual(anchor.span(in: field), "We use trovio")
    }

    func testTheCaretCanSitBeforeTextThatFollowsThePaste() throws {
        let field = "Dear team, We use trovio. Thanks"
        let caret = utf16("Dear team, We use trovio")
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: caret, selectionLength: 0, pasted: "We use trovio"))
        XCTAssertEqual(anchor.span(in: field), "We use trovio")
    }

    /// An earlier copy used the caret as the span: an empty string, which ended
    /// every observation as `field_cleared` with nothing to learn.
    func testTheAnchorIsNeverTheEmptyCaret() throws {
        let field = "We use trovio"
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: 13, selectionLength: 0, pasted: "We use trovio"))
        XCTAssertNotEqual(anchor.span(in: field), "")
        XCTAssertEqual(anchor.span(in: field), "We use trovio")
    }

    func testTextThatIsNotThePastedTextBeforeTheCaretIsUnobservable() {
        // The app changed the text on paste (here, its own capitalisation).
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "We use Trovio", selectionLocation: 13, selectionLength: 0, pasted: "We use trovio"))
        // The caret moved one unit before the anchor was read.
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "We use trovio!", selectionLocation: 14, selectionLength: 0, pasted: "We use trovio"))
        // The value has not caught up with the paste yet.
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "", selectionLocation: 0, selectionLength: 0, pasted: "We use trovio"))
    }

    func testOffsetsOutsideTheFieldAreUnobservable() {
        // Caret closer to the start than the pasted text is long.
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "We use trovio", selectionLocation: 5, selectionLength: 0, pasted: "We use trovio"))
        // Caret past the end of the value.
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "We use trovio", selectionLocation: 20, selectionLength: 0, pasted: "We use trovio"))
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "We use trovio", selectionLocation: -1, selectionLength: 0, pasted: "We use trovio"))
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "We use trovio", selectionLocation: Int.max, selectionLength: 13, pasted: "We use trovio"))
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: "We use trovio", selectionLocation: 13, selectionLength: 0, pasted: ""))
    }

    func testASelectionThatIsThePastedTextIsAcceptedAndNoOtherSelectionIs() throws {
        let field = "Hi We use trovio"
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: 3, selectionLength: 13, pasted: "We use trovio"))
        XCTAssertEqual(anchor.span(in: field), "We use trovio")
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: 3, selectionLength: 5, pasted: "We use trovio"))
    }

    /// Accessibility counts UTF-16 units: an emoji before the paste is two.
    func testOffsetsAreUTF16Units() throws {
        let field = "👋 你好 We use trovio"
        XCTAssertNotEqual(field.count, utf16(field), "the fixture must have a surrogate pair")
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: utf16(field), selectionLength: 0, pasted: "We use trovio"))
        XCTAssertEqual(anchor.span(in: field), "We use trovio")
        // Counting characters instead would put the caret two units short.
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: field.count, selectionLength: 0, pasted: "We use trovio"))
    }

    /// Unit-for-unit, not Swift's canonical equivalence: a field that
    /// decomposed "é" on paste did not hold the pasted text.
    func testTheComparisonIsExact() {
        let composed = "caf\u{00E9}"
        let decomposed = "cafe\u{0301}"
        XCTAssertEqual(composed, decomposed, "Swift's String == would accept it")
        XCTAssertNil(DictationSpanAnchor.establish(
            fieldValue: decomposed, selectionLocation: utf16(decomposed), selectionLength: 0, pasted: composed))
    }

    // ─── Following the span ───

    func testAnExactInsertionWithNoReliableCaretIsAnchoredInTheSameField() throws {
        let pasted = "We use wrapper"
        for (before, after) in [("", pasted), ("👋 Notes:  Thanks", "👋 Notes: \(pasted) Thanks"),
                                ("Old: \(pasted)\nNew: ", "Old: \(pasted)\nNew: \(pasted)")] {
            let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
                beforePaste: .init(value: before, selectionLocation: 0, selectionLength: 0),
                fieldValue: after, pasted: pasted))
            XCTAssertEqual(anchor.span(in: after), pasted)
            let edited = (after as NSString).replacingCharacters(
                in: (after as NSString).range(of: pasted, options: .backwards), with: "We use repo")
            XCTAssertEqual(anchor.span(in: edited), "We use repo")
        }
    }

    func testTheBeforeValueMustProveExactlyOneNewInsertion() {
        for (before, after, pasted) in [
            ("We use wrapper", "We use wrapper", "We use wrapper"), // old text, no paste
            ("x", "xx", "x"), // could be on either side
            ("abab", "ababab", "ab"), // overlapping repeated text
            ("Notes: ", "Other: We use wrapper", "We use wrapper"), // unrelated edit
            ("cafe\u{301} ", "café repo", "repo"), // normalization outside the span
            ("", "cafe\u{301}", "café"), // normalization inside the span
            ("Notes: ", "Notes: repo!", "repo"), // extra mutation
            ("Notes: ", "Notes: ", "")
        ] {
            XCTAssertNil(DictationSpanAnchor.establish(
                beforePaste: .init(value: before, selectionLocation: 0, selectionLength: 0),
                fieldValue: after, pasted: pasted), "\(before) → \(after)")
        }
        XCTAssertNil(DictationSpanAnchor.establish(
            beforePaste: .init(value: nil, selectionLocation: nil, selectionLength: nil),
            fieldValue: "repo", pasted: "repo"))
    }

    func testOnlyAnExactReplacementOfThePriorSelectionCanAnchorAReplacement() throws {
        let before = DictationAnchorSearch.FieldRead(value: "👋 old Thanks", selectionLocation: 3, selectionLength: 3)
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            beforePaste: before, fieldValue: "👋 We use wrapper Thanks", pasted: "We use wrapper"))
        XCTAssertEqual(anchor.span(in: "👋 We use repo Thanks"), "We use repo")
        XCTAssertNil(anchor.span(in: "Hello We use repo Thanks"))
        for selection in [(-1, 3), (3, -1), (Int.max, 3), (3, Int.max), (0, 0), (4, 3)] {
            XCTAssertNil(DictationSpanAnchor.establish(
                beforePaste: .init(value: before.value, selectionLocation: selection.0, selectionLength: selection.1),
                fieldValue: "👋 We use wrapper Thanks", pasted: "We use wrapper"))
        }
        XCTAssertNil(DictationSpanAnchor.establish(
            beforePaste: .init(value: "repo", selectionLocation: 0, selectionLength: 4),
            fieldValue: "repo", pasted: "repo"), "an unchanged selection does not prove a paste")
    }

    func testInsertionProofCanDistinguishRotationsOfSurroundingText() throws {
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            beforePaste: .init(value: "a", selectionLocation: nil, selectionLength: nil),
            fieldValue: "aba", pasted: "ba"))
        XCTAssertEqual(anchor.span(in: "arepo"), "repo")
    }

    func testASnapshotCannotTransferToAnotherFieldOrApplication() {
        let element = AXUIElementCreateApplication(1)
        let snapshot = DictationCorrectionObserver.PasteSnapshot(
            element: element, pid: 1, bundleID: "test.editor",
            field: .init(value: "", selectionLocation: 0, selectionLength: 0))
        XCTAssertTrue(snapshot.matches(element: AXUIElementCreateApplication(1), pid: 1, bundleID: "test.editor"))
        XCTAssertFalse(snapshot.matches(element: AXUIElementCreateSystemWide(), pid: 1, bundleID: "test.editor"))
        XCTAssertFalse(snapshot.matches(element: element, pid: 2, bundleID: "test.editor"))
        XCTAssertFalse(snapshot.matches(element: element, pid: 1, bundleID: "other.editor"))
    }

    func testAnEditInsideTheSpanIsFollowedWhateverItDoesToTheLength() throws {
        let before = "Notes: We use trovio today"
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: before, selectionLocation: utf16("Notes: We use trovio"), selectionLength: 0,
            pasted: "We use trovio"))
        XCTAssertEqual(anchor.span(in: "Notes: We use Trovioo today"), "We use Trovioo")
        XCTAssertEqual(anchor.span(in: "Notes: We use T today"), "We use T")
        XCTAssertEqual(anchor.span(in: "Notes:  today"), "", "a span the user deleted is empty, not gone")
    }

    /// Plain offsets would read "e use trovio " here and could hand learning
    /// a correction the user never made.
    func testAnEditOutsideTheSpanBreaksTheAnchor() throws {
        let before = "Notes: We use trovio today"
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: before, selectionLocation: utf16("Notes: We use trovio"), selectionLength: 0,
            pasted: "We use trovio"))
        XCTAssertNil(anchor.span(in: "My notes: We use trovio today"), "an edit before the span")
        XCTAssertNil(anchor.span(in: "Notes: We use trovio tonight"), "an edit after the span")
        XCTAssertNil(anchor.span(in: ""), "the field was cleared")
        XCTAssertNil(anchor.span(in: "Notes"), "shorter than the text around the span")
    }

    /// The prefix and suffix are the field's own text; they are compared,
    /// never returned.
    func testTextOutsideTheSpanIsNeverPartOfIt() throws {
        let sentinelBefore = "SENTINEL-BEFORE-4Z "
        let sentinelAfter = " SENTINEL-AFTER-4Z"
        let field = sentinelBefore + "We use trovio" + sentinelAfter
        let anchor = try XCTUnwrap(DictationSpanAnchor.establish(
            fieldValue: field, selectionLocation: utf16(sentinelBefore + "We use trovio"), selectionLength: 0,
            pasted: "We use trovio"))
        let edited = sentinelBefore + "We use Trovioo" + sentinelAfter
        let span = try XCTUnwrap(anchor.span(in: edited))
        XCTAssertEqual(span, "We use Trovioo")
        XCTAssertFalse(span.contains("SENTINEL"))
        XCTAssertEqual(anchor.span(in: field), "We use trovio")
    }

    // ─── Admission ───

    func testSecureInputAndPasswordFieldsAreRefusedFirst() {
        XCTAssertEqual(DictationObservationAdmission.refusal(
            secureInput: true, focus: .editable, bundleID: "com.apple.TextEdit",
            expectedBundleID: "com.apple.TextEdit"), .secureField)
        XCTAssertEqual(DictationObservationAdmission.refusal(
            secureInput: false, focus: .secureField, bundleID: "com.apple.TextEdit",
            expectedBundleID: "com.apple.TextEdit"), .secureField)
        XCTAssertEqual(DictationObservationAdmission.refusal(
            secureInput: false, focus: .secureField, bundleID: "com.other.App",
            expectedBundleID: "com.apple.TextEdit"), .secureField, "a password field wins over the app check")
    }

    func testAFieldThatTakesNoTextIsRefused() {
        for focus in [FocusCheckResult.notEditable, .noFocusedElement, .permissionMissing] {
            XCTAssertEqual(DictationObservationAdmission.refusal(
                secureInput: false, focus: focus, bundleID: "com.apple.TextEdit",
                expectedBundleID: "com.apple.TextEdit"), .noFocusedField, "\(focus)")
        }
        XCTAssertEqual(DictationObservationAdmission.refusal(
            secureInput: false, focus: nil, bundleID: nil, expectedBundleID: "com.apple.TextEdit"), .noFocusedField)
    }

    func testOnlyTheAppThePasteWentToIsObserved() {
        XCTAssertEqual(DictationObservationAdmission.refusal(
            secureInput: false, focus: .editable, bundleID: "com.tinyspeck.slackmacgap",
            expectedBundleID: "com.apple.TextEdit"), .otherApplication)
        XCTAssertEqual(DictationObservationAdmission.refusal(
            secureInput: false, focus: .editable, bundleID: nil,
            expectedBundleID: "com.apple.TextEdit"), .otherApplication)
        XCTAssertNil(DictationObservationAdmission.refusal(
            secureInput: false, focus: .editable, bundleID: "com.apple.TextEdit",
            expectedBundleID: "com.apple.TextEdit"))
    }

    // ─── What an end reports ───

    func testOnlyAnEligibleEndWithTheAnchorHoldingReportsTheSpan() {
        XCTAssertEqual(DictationTerminalCondition.editSettled.reportedSpan("We use Trovioo"), "We use Trovioo")
        XCTAssertEqual(DictationTerminalCondition.focusLeft.reportedSpan("We use Trovioo"), "We use Trovioo")
        XCTAssertNil(DictationTerminalCondition.editSettled.reportedSpan(nil))
        for condition in [DictationTerminalCondition.abandoned, .nextTake, .secureField,
                          .otherApplication, .noFocusedField, .revisionTooLarge, .spanContextLost,
                          .spanMissing] {
            XCTAssertNil(condition.reportedSpan("We use Trovioo"), "\(condition)")
        }
    }

    // ─── Finding the anchor after a paste ───

    private let queue = DispatchQueue(label: "anchor-search-tests")

    private func search(
        reads: [DictationAnchorSearch.FieldRead?], attempts: Int = 4, current: Locked<Bool> = Locked(true)
    ) -> (DictationAnchorSearch.Outcome?, Int) {
        let remaining = Locked(reads)
        let count = Locked(0)
        let done = expectation(description: "search finished")
        let outcome = Locked<DictationAnchorSearch.Outcome?>(nil)
        DictationAnchorSearch.run(
            pasted: "We use trovio", attempts: attempts, retrySeconds: 0.01, queue: queue,
            read: {
                count.mutate { $0 += 1 }
                return remaining.mutate { $0.isEmpty ? nil : $0.removeFirst() }
            },
            isCurrent: { current.get },
            completion: { result in
                outcome.mutate { $0 = result }
                done.fulfill()
            })
        wait(for: [done], timeout: 5)
        return (outcome.get, count.get)
    }

    private let pastedRead = DictationAnchorSearch.FieldRead(
        value: "We use trovio", selectionLocation: 13, selectionLength: 0)
    private let staleRead = DictationAnchorSearch.FieldRead(value: "", selectionLocation: 0, selectionLength: 0)

    func testTheSearchWaitsForTheFieldToCatchUpWithThePaste() throws {
        let (outcome, reads) = search(reads: [nil, staleRead, pastedRead])
        guard case .anchored(let anchor, let value)? = outcome else {
            return XCTFail("expected an anchor, got \(String(describing: outcome))")
        }
        XCTAssertEqual(value, "We use trovio")
        XCTAssertEqual(anchor.span(in: value), "We use trovio")
        XCTAssertEqual(reads, 3)
    }

    func testTheSearchGivesUpAfterItsAttempts() {
        let (outcome, reads) = search(reads: [staleRead, staleRead, staleRead, staleRead, pastedRead], attempts: 4)
        XCTAssertEqual(outcome, .notFound)
        XCTAssertEqual(reads, 4, "the fifth read, which would have matched, is never made")
    }

    func testAnAbandonDuringTheSearchStopsIt() {
        let current = Locked(true)
        let remaining = Locked(0)
        let done = expectation(description: "search finished")
        let outcome = Locked<DictationAnchorSearch.Outcome?>(nil)
        DictationAnchorSearch.run(
            pasted: "We use trovio", attempts: 6, retrySeconds: 0.01, queue: queue,
            read: { [staleRead] in
                // The caller gives up after the second read.
                if remaining.mutate({ $0 += 1; return $0 }) == 2 { current.mutate { $0 = false } }
                return staleRead
            },
            isCurrent: { current.get },
            completion: { result in
                outcome.mutate { $0 = result }
                done.fulfill()
            })
        wait(for: [done], timeout: 5)
        XCTAssertEqual(outcome.get, .superseded)
        XCTAssertEqual(remaining.get, 2)
    }

    func testASearchSupersededBeforeItsFirstReadReadsNothing() {
        let (outcome, reads) = search(reads: [pastedRead], current: Locked(false))
        XCTAssertEqual(outcome, .superseded)
        XCTAssertEqual(reads, 0)
    }

    func testAnchorDiagnosticsExplainUnreadableFieldsWithoutLoggingTheirContents() {
        let privateText = "PRIVATE-SENTINEL-9K"
        let cases: [(DictationAnchorSearch.FieldRead?, DictationAnchorSearch.Diagnostic.Result)] = [
            (.init(value: nil, selectionLocation: 0, selectionLength: 0), .valueUnavailable),
            (.init(value: privateText, selectionLocation: nil, selectionLength: nil), .selectionUnavailable),
            (.init(value: privateText, selectionLocation: 0, selectionLength: 0), .selectionMismatch),
            (.init(value: "different private field", selectionLocation: 0, selectionLength: 0), .textNotFound),
            (.init(value: privateText, selectionLocation: privateText.utf16.count, selectionLength: 0), .anchored)
        ]
        for (field, expected) in cases {
            let done = expectation(description: "diagnostic \(expected)")
            let reports = Locked<[DictationAnchorSearch.Diagnostic]>([])
            DictationAnchorSearch.run(
                pasted: privateText, attempts: 2, retrySeconds: 0.001, queue: queue,
                read: { field }, isCurrent: { true },
                diagnostic: { report in reports.mutate { $0.append(report) } },
                completion: { _ in done.fulfill() })
            wait(for: [done], timeout: 5)
            XCTAssertEqual(reports.get.count, 1)
            XCTAssertEqual(reports.get.first?.result, expected)
            XCTAssertEqual(reports.get.first?.attempts, expected == .anchored ? 1 : 2)
            let log = reports.get.first?.logSummary ?? ""
            XCTAssertFalse(log.contains(privateText))
            XCTAssertFalse(log.contains("different private field"))
        }
    }

    func testAnAbandonDuringTheMatchingReadCannotReportAnAnchor() {
        let current = Locked(true)
        let done = expectation(description: "abandoned read finished")
        let outcome = Locked<DictationAnchorSearch.Outcome?>(nil)
        let reports = Locked<[DictationAnchorSearch.Diagnostic]>([])
        DictationAnchorSearch.run(
            pasted: "We use trovio", attempts: 1, retrySeconds: 0, queue: queue,
            read: { [pastedRead] in current.mutate { $0 = false }; return pastedRead },
            isCurrent: { current.get }, diagnostic: { report in reports.mutate { $0.append(report) } },
            completion: { result in outcome.mutate { $0 = result }; done.fulfill() })
        wait(for: [done], timeout: 5)
        XCTAssertEqual(outcome.get, .superseded)
        XCTAssertEqual(reports.get.map(\.result), [.superseded])
    }

    func testTheSearchUsesTheSnapshotWhenSelectionIsZeroOrMissingAndWaitsForAnActualChange() {
        let pasted = "We use wrapper"
        let before = "Old: \(pasted)"
        for selection: Int? in [0, nil] {
            let done = expectation(description: "snapshot search")
            let reads = Locked(0), reports = Locked<[DictationAnchorSearch.Diagnostic]>([])
            let outcome = Locked<DictationAnchorSearch.Outcome?>(nil)
            DictationAnchorSearch.run(
                pasted: pasted, beforePaste: .init(value: before, selectionLocation: 0, selectionLength: 0),
                attempts: 3, retrySeconds: 0.001, queue: queue,
                read: {
                    let attempt = reads.mutate { $0 += 1; return $0 }
                    // An old occurrence at a plausible caret must not win.
                    return attempt == 1 ? .init(value: before, selectionLocation: before.utf16.count, selectionLength: 0)
                        : .init(value: pasted + before, selectionLocation: selection, selectionLength: selection)
                }, isCurrent: { true }, diagnostic: { report in reports.mutate { $0.append(report) } },
                completion: { result in outcome.mutate { $0 = result }; done.fulfill() })
            wait(for: [done], timeout: 5)
            guard case .anchored(let anchor, _)? = outcome.get else { XCTFail("missing proven insertion"); continue }
            XCTAssertEqual(anchor.span(in: "We use repo" + before), "We use repo")
            XCTAssertEqual(reads.get, 2)
            XCTAssertEqual(reports.get.map(\.result), [.anchoredFromChange])
            XCTAssertFalse(reports.get.map(\.logSummary).joined().contains("wrapper"))
        }
    }
}

/// A value behind a lock, for closures the search calls on its own queue.
final class Locked<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value
    init(_ value: Value) { self.value = value }
    var get: Value { lock.lock(); defer { lock.unlock() }; return value }
    func mutate<T>(_ body: (inout Value) -> T) -> T {
        lock.lock(); defer { lock.unlock() }
        return body(&value)
    }
}
