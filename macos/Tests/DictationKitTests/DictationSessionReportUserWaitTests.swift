import XCTest
@testable import DictationKit

/// The session report is the artifact somebody reads days later to decide
/// where dictation's time goes — and until this test it reported the
/// CONFIGURED clipboard-restore window as if the user had waited for it.
///
/// `TextInserter.InsertTiming.totalMs` deliberately kept summing the fixed
/// 500 ms `restoreDelayFixedMs` after the restore moved off the critical
/// path (2026-08-19), so history stays comparable. The renderer then printed
/// that sum as `paste total`, next to sub-stages that summed to 13 ms, and
/// two later documents read the 500 ms as a constant the user pays on every
/// dictation. `blockingMs` — the caller's real wait — existed and reached no
/// report. This pins the report showing both, labelled.
final class DictationSessionReportUserWaitTests: XCTestCase {

    private func paste(detached: Bool) -> TextInserter.InsertTiming {
        var t = TextInserter.InsertTiming()
        t.gateCheckMs = 1
        t.synthesizeMs = 1
        t.waitForAskMs = 11
        t.restoreDelayFixedMs = 500
        t.restoreMs = 0
        t.restoreDetached = detached
        return t
    }

    private func success(detached: Bool) -> DictationAttemptRecord {
        var s = DictationSession.StageTimings()
        s.captureStopMs = 0.2
        s.asrMs = 900
        s.pasteTiming = paste(detached: detached)
        return DictationAttemptRecord(
            gesture: "hold", armedApp: "Witty",
            outcome: .success(text: "hi", clipboardByteIdentical: true,
                              detectedLanguage: "en", timings: s))
    }

    private func report(_ records: [DictationAttemptRecord]) -> DictationSessionReport {
        DictationSessionReportBuilder.buildReport(
            from: records, m2TerminalConditionCounts: nil, generatedAt: Date())
    }

    // MARK: - The defect

    /// A detached restore protects the clipboard for 500 ms; the user waited
    /// 13 ms. The report must carry both and must not pass one off as the other.
    func testADetachedRestoreWindowIsNotReportedAsTheUserWait() {
        let r = report([success(detached: true)])

        // Unchanged arithmetic — history comparability is the reason it exists.
        XCTAssertEqual(r.pasteTotalMs.p50Ms ?? -1, 513, accuracy: 0.001)
        // The number the user feels.
        XCTAssertEqual(r.pasteUserWaitMs.p50Ms ?? -1, 13, accuracy: 0.001)
        XCTAssertEqual(r.pasteUserWaitMs.n, 1)
        // And the same split at the release-to-inserted level.
        XCTAssertEqual(r.releaseToInsertedMs.p50Ms ?? -1, 1413.2, accuracy: 0.001)
        XCTAssertEqual(r.releaseToInsertedUserWaitMs.p50Ms ?? -1, 913.2, accuracy: 0.001)
    }

    /// Inline restore (the checkers' configuration): the user really did wait
    /// for the window, and the two numbers agree.
    func testAnInlineRestoreIsTheUserWait() {
        let r = report([success(detached: false)])
        XCTAssertEqual(r.pasteTotalMs.p50Ms ?? -1, 513, accuracy: 0.001)
        XCTAssertEqual(r.pasteUserWaitMs.p50Ms ?? -1, 513, accuracy: 0.001)
        XCTAssertEqual(r.releaseToInsertedUserWaitMs.p50Ms ?? -1, 1413.2, accuracy: 0.001)
    }

    // MARK: - The far side: what a person reads

    func testTheRenderedReportShowsBothNumbersAndSaysWhichIsWhich() {
        let text = DictationSessionReportRenderer.renderHumanReadable(report([success(detached: true)]))

        let userWaitLine = text.split(separator: "\n").first { $0.contains("paste user wait") }
        XCTAssertNotNil(userWaitLine, "no 'paste user wait' line in:\n\(text)")
        XCTAssertTrue(userWaitLine?.contains("p50=13.00ms") == true, "user wait line: \(userWaitLine ?? "")")

        let totalLine = text.split(separator: "\n").first { $0.contains("paste total") && !$0.contains("keeps") }
        XCTAssertTrue(totalLine?.contains("p50=513.00ms") == true, "total line: \(totalLine ?? "")")

        XCTAssertTrue(text.contains("paste total keeps the configured 500ms clipboard window"),
                      "the report must say which number is which:\n\(text)")
        XCTAssertTrue(text.contains("release->inserted as the user waited"),
                      "the release-to-inserted bar must have a user-wait row:\n\(text)")
    }

    /// Nothing measured must never print as 0 — this file's oldest rule.
    func testAnEmptySessionDoesNotFabricateAUserWait() {
        let r = report([])
        XCTAssertEqual(r.pasteUserWaitMs.n, 0)
        XCTAssertNil(r.pasteUserWaitMs.p50Ms)
        XCTAssertEqual(r.releaseToInsertedUserWaitMs.n, 0)
        XCTAssertNil(r.releaseToInsertedUserWaitMs.p90Ms)
        let text = DictationSessionReportRenderer.renderHumanReadable(r)
        XCTAssertFalse(text.contains("MET") || text.contains("MISSED"))
    }

    /// A gate rejection never entered the paste path: it has a pasteTiming
    /// (the gate ran) but no synthesize/wait, so its user wait is the gate
    /// alone — and it still must not inherit a phantom 500 ms.
    func testAGateRejectionCarriesOnlyTheGateAsUserWait() {
        var t = TextInserter.InsertTiming()
        t.gateCheckMs = 0.75
        var s = DictationSession.StageTimings()
        s.captureStopMs = 0.1
        s.asrMs = 1500
        s.pasteTiming = t
        let rejected = DictationAttemptRecord(
            gesture: "hold", armedApp: "Slack",
            outcome: .failed(stage: .paste, failure: .insert(.noFocusedElement), timings: s))
        let r = report([rejected])
        XCTAssertEqual(r.pasteUserWaitMs.p50Ms ?? -1, 0.75, accuracy: 0.001)
        XCTAssertEqual(r.pasteTotalMs.p50Ms ?? -1, 0.75, accuracy: 0.001)
    }
}
