import XCTest
@testable import DictationKit

/// The session report must not blame a stage that never ran. A capture-stage
/// silence gate and a genuine post-ASR empty decode are distinct outcomes and
/// must remain distinct in both the classification and timing sections.
///
/// `DictationSession.SessionFailure.emptyTranscript` is raised at two stages
/// that mean opposite things, and carries one description that names ASR. The
/// silence gate's own comment is explicit that it is not one of them:
/// "Reporting it at `stage: .capture` (not `.asr`) keeps the stage field
/// truthful: ASR never ran."
///
/// Why this is worth a test rather than a shrug: known issue K-3 records a
/// mid-utterance microphone release
/// as "the true cause of the 'second half gets eaten' truncation that three
/// separate DECODER investigations chased". A capture fault wearing an ASR
/// label is how that bill gets run up again.
final class DictationSessionReportBlameTests: XCTestCase {

    private func timings(asrMs: Double?) -> DictationSession.StageTimings {
        var t = DictationSession.StageTimings()
        t.captureStopMs = 0.02
        t.asrMs = asrMs
        return t
    }

    /// A capture-stage failure, rendered for a human.
    private func silenceGateRecord() -> DictationAttemptRecord {
        DictationAttemptRecord(
            gesture: "in-app", armedApp: "Witty",
            // Exactly what the silence gate returns — see
            // `DictationSession`'s "SILENCE GATE" guard.
            outcome: .failed(stage: .capture, failure: .emptyTranscript,
                             timings: timings(asrMs: nil)))
    }

    /// A post-ASR empty decode: audio reached the decoder and nothing came back.
    private func realEmptyDecodeRecord() -> DictationAttemptRecord {
        DictationAttemptRecord(
            gesture: "in-app", armedApp: "Witty",
            outcome: .failed(stage: .asr, failure: .emptyTranscript,
                             timings: timings(asrMs: 5525.61)))
    }

    // MARK: - The defect

    func testASilenceGateFailureDoesNotClaimTheDecoderReturnedAnything() {
        let report = DictationSessionReportBuilder.buildReport(
            from: [silenceGateRecord()],
            m2TerminalConditionCounts: nil, generatedAt: Date())
        let text = DictationSessionReportRenderer.renderHumanReadable(report)

        XCTAssertFalse(
            text.contains("capture: ASR returned an empty transcript"),
            "the report blames ASR for a clip the gate refused before ASR ran:\n\(text)")
    }

    /// The strongest form: the report's own numbers must not disagree with
    /// its own prose. If nothing reached ASR, nothing may be filed under ASR.
    func testTheReportDoesNotBlameASRInProseWhileCountingZeroASRAttempts() {
        let report = DictationSessionReportBuilder.buildReport(
            from: [silenceGateRecord(), silenceGateRecord()],
            m2TerminalConditionCounts: nil, generatedAt: Date())

        // The measurement half: no attempt reached the decoder.
        XCTAssertEqual(report.asrMs.n, 0,
                       "fixture wrong — these records must not carry an ASR timing")

        // The prose half must agree with it.
        for f in report.failuresByClassification {
            XCTAssertFalse(
                f.classification.contains("ASR returned"),
                "\(f.count) failure(s) filed as \"\(f.classification)\" while the "
                + "same report counts \(report.asrMs.n) attempts that reached ASR")
        }
    }

    // MARK: - ...without flattening the distinction

    /// The fix must not be "stop naming ASR". A real empty decode IS an ASR
    /// finding, and losing that would trade one wrong label for another.
    func testARealEmptyDecodeIsStillAttributedToASR() {
        let report = DictationSessionReportBuilder.buildReport(
            from: [realEmptyDecodeRecord()],
            m2TerminalConditionCounts: nil, generatedAt: Date())
        let text = DictationSessionReportRenderer.renderHumanReadable(report)

        XCTAssertTrue(text.contains("asr: ASR returned an empty transcript"),
                      "a genuine post-ASR empty decode lost its attribution:\n\(text)")
    }

    /// And the two must not collapse into one bucket. A session with one of
    /// each is two distinct findings with two distinct fixes — a dead
    /// microphone and a decoder returning nothing are not the same bug, and
    /// counting them together is how the smaller one disappears.
    func testTheTwoEmptyTranscriptStagesAreCountedSeparately() {
        let report = DictationSessionReportBuilder.buildReport(
            from: [silenceGateRecord(), realEmptyDecodeRecord()],
            m2TerminalConditionCounts: nil, generatedAt: Date())

        XCTAssertEqual(report.failuresByClassification.count, 2,
                       "the gate's refusal and a real empty decode were merged into "
                       + "one line: \(report.failuresByClassification.map { $0.classification })")
    }
}
