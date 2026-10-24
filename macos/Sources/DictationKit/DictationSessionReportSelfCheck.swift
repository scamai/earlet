import Foundation

/// Pure-logic self-check for `DictationSessionReport.swift` — no TCC, no AX,
/// no pasteboard, no CGEventTap, no ASR worker. Builds
/// `DictationAttemptRecord`s by hand and drives `DictationSessionReportBuilder
/// .buildReport(from:...)` / `dictationPercentile` / the renderer directly.
///
/// The single most important case here is the EMPTY-input one: this report
/// exists partly because an earlier percentile helper in this project
/// silently returned `0` for an empty array, which printed as a bogus
/// "MET, 0 ms" verdict for a stage that was never exercised. Every assertion
/// below that touches an empty/zero-`n` stage checks for `nil`/"not
/// measured," never a numeric zero.
public func runDictationSessionReportSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }

    // MARK: dictationPercentile — empty input, single value, and a known distribution

    do {
        check(dictationPercentile([], 0.50) == nil, "percentile: empty input must return nil, not 0")
        check(dictationPercentile([], 0.90) == nil, "percentile: empty input must return nil, not 0, at p90 too")
        check(dictationPercentile([42], 0.50) == 42, "percentile: single-value input must return that value at p50")
        check(dictationPercentile([42], 0.90) == 42, "percentile: single-value input must return that value at p90")
        // 1...10: nearest-rank p50 -> index ceil(0.5*10)-1 = 4 -> value 5;
        // p90 -> index ceil(0.9*10)-1 = 8 -> value 9.
        let values: [Double] = (1...10).map { Double($0) }
        check(dictationPercentile(values, 0.50) == 5, "percentile: 1...10 at p50 must be 5, got \(String(describing: dictationPercentile(values, 0.50)))")
        check(dictationPercentile(values, 0.90) == 9, "percentile: 1...10 at p90 must be 9, got \(String(describing: dictationPercentile(values, 0.90)))")
    }

    // MARK: DictationStageStat — n=0 must produce nil/nil, never 0/0

    do {
        let empty = DictationStageStat(values: [])
        check(empty.n == 0, "stage-stat: empty input must report n=0")
        check(empty.p50Ms == nil, "stage-stat: empty input must report p50Ms == nil, not 0")
        check(empty.p90Ms == nil, "stage-stat: empty input must report p90Ms == nil, not 0")

        let nonEmpty = DictationStageStat(values: [100, 200, 300])
        check(nonEmpty.n == 3, "stage-stat: n must equal the input count")
        check(nonEmpty.p50Ms != nil && nonEmpty.p90Ms != nil, "stage-stat: non-empty input must produce non-nil percentiles")
    }

    // MARK: buildReport(from: []) — the fully-empty session must be safe and honest

    do {
        let report = DictationSessionReportBuilder.buildReport(from: [], m2TerminalConditionCounts: nil, generatedAt: Date())
        check(report.attempted == 0 && report.inserted == 0 && report.failed == 0,
              "empty-report: attempted/inserted/failed must all be 0 for zero attempts")
        check(report.failuresByClassification.isEmpty, "empty-report: no failures to classify")
        for stat in [report.armMs, report.captureMs, report.asrMs, report.pasteGateMs, report.pasteSynthesizeMs,
                     report.pasteWaitForAskMs, report.pasteRestoreMs, report.pasteTotalMs,
                     report.pasteUserWaitMs,
                     report.releaseToTextMs, report.releaseToInsertedMs, report.releaseToInsertedUserWaitMs] {
            check(stat.n == 0 && stat.p50Ms == nil && stat.p90Ms == nil,
                  "empty-report: every stage stat must be n=0/nil/nil on an empty session")
        }
        check(report.perGesture.isEmpty, "empty-report: no trigger rows with zero attempts")
        check(report.perArmedApp.isEmpty, "empty-report: no per-app rows with zero attempts")
        check(report.clipboardChecked == 0 && report.clipboardByteIdentical == 0,
              "empty-report: clipboard counters must both be 0")
        check(report.m2TerminalConditionCounts == nil, "empty-report: m2 tally must stay nil when the caller passed nil")

        // The renderer must not crash and must print "not measured" for
        // every empty stage — not "MET, 0 ms" or "MISSED, 0 ms". This is
        // the exact regression this whole file exists to prevent.
        let rendered = DictationSessionReportRenderer.renderHumanReadable(report)
        check(rendered.contains("not measured"), "empty-report: rendered output must say 'not measured' somewhere for an all-empty session")
        check(!rendered.contains("MET") && !rendered.contains("MISSED"),
              "empty-report: an all-empty session must never print a MET/MISSED verdict for any stage (nothing was measured)")
    }

    // MARK: buildReport — success/failure counting, per-app, failure classification

    do {
        func timings(arm: Double?, capture: Double?, asr: Double?, paste: TextInserter.InsertTiming?) -> DictationSession.StageTimings {
            var t = DictationSession.StageTimings()
            t.armMs = arm
            t.captureStopMs = capture
            t.asrMs = asr
            t.pasteTiming = paste
            return t
        }
        var fullPaste = TextInserter.InsertTiming()
        fullPaste.gateCheckMs = 1
        fullPaste.synthesizeMs = 2
        fullPaste.waitForAskMs = 50
        fullPaste.restoreDelayFixedMs = 500
        fullPaste.restoreMs = 3

        let successIterm = DictationAttemptRecord(
            gesture: "hold-to-talk", armedApp: "com.googlecode.iterm2",
            outcome: .success(text: "hello", clipboardByteIdentical: true,
                               detectedLanguage: nil,
                               timings: timings(arm: 5, capture: 40, asr: 335, paste: fullPaste)))
        let failIterm = DictationAttemptRecord(
            gesture: "hold-to-talk", armedApp: "com.googlecode.iterm2",
            outcome: .failed(stage: .paste, failure: .insert(.pasteNeverRequested),
                              timings: timings(arm: 3, capture: 41, asr: 340, paste: fullPaste)))
        var gateOnlyTiming = TextInserter.InsertTiming()
        gateOnlyTiming.gateCheckMs = 1
        let failSlackNoFocus = DictationAttemptRecord(
            gesture: "hold-to-talk", armedApp: "com.tinyspeck.slackmacgap",
            outcome: .failed(stage: .paste, failure: .insert(.noFocusedElement),
                              timings: timings(arm: 4, capture: 38, asr: 330, paste: gateOnlyTiming)))
        let failAsr = DictationAttemptRecord(
            gesture: "double-tap-toggle", armedApp: "com.google.Chrome",
            outcome: .failed(stage: .asr, failure: .emptyTranscript,
                              timings: timings(arm: 6, capture: 42, asr: nil, paste: nil)))

        let records = [successIterm, failIterm, failSlackNoFocus, failAsr]
        let report = DictationSessionReportBuilder.buildReport(from: records, m2TerminalConditionCounts: [:], generatedAt: Date())

        check(report.attempted == 4, "counting: attempted must be 4")
        check(report.inserted == 1, "counting: inserted must be 1")
        check(report.failed == 3, "counting: failed must be 3")

        check(report.perGesture.count == 2,
              "per-gesture: expected hold and double-tap rows, got \(report.perGesture)")
        if let hold = report.perGesture.first(where: { $0.gesture == "hold-to-talk" }) {
            check(hold.attempted == 3 && hold.inserted == 1,
                  "per-gesture: hold must show 1/3, got \(hold.inserted)/\(hold.attempted)")
        } else {
            failures.append("per-gesture: expected a hold-to-talk row")
        }
        if let toggle = report.perGesture.first(where: { $0.gesture == "double-tap-toggle" }) {
            check(toggle.attempted == 1 && toggle.inserted == 0,
                  "per-gesture: double tap must show 0/1, got \(toggle.inserted)/\(toggle.attempted)")
        } else {
            failures.append("per-gesture: expected a double-tap-toggle row")
        }

        check(report.perArmedApp.count == 3, "per-app: expected 3 distinct apps, got \(report.perArmedApp.count)")
        if let iterm = report.perArmedApp.first(where: { $0.app == "com.googlecode.iterm2" }) {
            check(iterm.attempted == 2 && iterm.inserted == 1, "per-app: iTerm2 must show 1/2, got \(iterm.inserted)/\(iterm.attempted)")
        } else {
            failures.append("per-app: expected an iTerm2 row")
        }

        // ASR-stage failure (failAsr) never reached the paste stage at all
        // (pasteTiming == nil) — must NOT contribute to any paste-substat,
        // including pasteGateMs.
        check(report.pasteGateMs.n == 3, "paste-gate: n must be 3 (the 3 records with a non-nil pasteTiming), got \(report.pasteGateMs.n)")

        // noFocusedElement is a gate-ONLY failure — it must contribute to
        // gate stats but NOT to synthesize/waitForAsk/restore stats (those
        // fields default to 0 in its InsertTiming and were never really
        // measured).
        check(report.pasteSynthesizeMs.n == 2, "paste-synthesize: n must be 2 (success + pasteNeverRequested only), got \(report.pasteSynthesizeMs.n)")
        check(report.pasteWaitForAskMs.n == 2, "paste-waitForAsk: n must be 2 (success + pasteNeverRequested only), got \(report.pasteWaitForAskMs.n)")

        check(report.releaseToTextMs.n == 3, "release-to-text: n must be 3 (every record with both capture+asr present), got \(report.releaseToTextMs.n)")
        // releaseToInsertedMs only needs releaseToText AND *a* pasteTiming
        // to be present (StageTimings.releaseToInsertedMs doesn't care
        // WHICH paste sub-fields inside that timing are real vs
        // default-zero) — failSlackNoFocus reached the gate (pasteTiming
        // != nil) even though it failed there, so it's included too:
        // successIterm, failIterm, failSlackNoFocus = 3; only failAsr
        // (asr == nil, never reached paste at all) is excluded.
        check(report.releaseToInsertedMs.n == 3, "release-to-inserted: n must be 3 (every record with both releaseToText AND a pasteTiming present, "
            + "regardless of which paste sub-stage it reached), got \(report.releaseToInsertedMs.n)")

        check(report.failuresByClassification.count == 3, "failure-breakdown: 3 distinct failure classifications expected, got \(report.failuresByClassification.count)")

        check(report.clipboardChecked == 1 && report.clipboardByteIdentical == 1,
              "clipboard: only the one success carries a clipboard verdict, got checked=\(report.clipboardChecked) identical=\(report.clipboardByteIdentical)")

        // m2TerminalConditionCounts: caller passed an EMPTY (not nil) dict
        // -> "flag was on, zero observations," a different fact from "flag
        // was off" (nil) — the renderer must distinguish them.
        check(report.m2TerminalConditionCounts != nil && report.m2TerminalConditionCounts?.isEmpty == true,
              "m2-tally: an explicit empty dict must round-trip as non-nil-and-empty, not collapse to nil")
        let rendered = DictationSessionReportRenderer.renderHumanReadable(report)
        check(rendered.contains("hold-to-talk: 1/3")
                && rendered.contains("double-tap-toggle: 0/1"),
              "per-gesture: human report omitted the trigger split")
        check(rendered.contains("no observations completed"), "m2-tally: empty-but-present tally must render as 'no observations completed', not 'not measured'")
    }

    // MARK: MET/MISSED verdicts against the committed bars — boundary cases

    do {
        // Comfortably under both bars -> MET on both.
        let underBar = DictationSessionReportBuilder.buildReport(
            from: [
                DictationAttemptRecord(gesture: "g", armedApp: "app", outcome: .success(
                    text: "x", clipboardByteIdentical: true, detectedLanguage: nil,
                    timings: { var t = DictationSession.StageTimings(); t.captureStopMs = 40; t.asrMs = 300; return t }())),
            ],
            m2TerminalConditionCounts: nil, generatedAt: Date())
        let rendered = DictationSessionReportRenderer.renderHumanReadable(underBar)
        check(rendered.contains("[MET]"), "bar: 340ms p50/p90 against 500/1000ms bars must render at least one [MET]")
        check(!rendered.contains("[MISSED]"), "bar: 340ms p50/p90 must never render [MISSED] against the 500/1000ms bars")
    }

    do {
        // Well over both bars -> MISSED on both.
        let overBar = DictationSessionReportBuilder.buildReport(
            from: [
                DictationAttemptRecord(gesture: "g", armedApp: "app", outcome: .success(
                    text: "x", clipboardByteIdentical: true, detectedLanguage: nil,
                    timings: { var t = DictationSession.StageTimings(); t.captureStopMs = 900; t.asrMs = 900; return t }())),
            ],
            m2TerminalConditionCounts: nil, generatedAt: Date())
        let rendered = DictationSessionReportRenderer.renderHumanReadable(overBar)
        check(rendered.contains("[MISSED]"), "bar: 1800ms p50/p90 against 500/1000ms bars must render at least one [MISSED]")
    }

    // MARK: insertion-success bar (>= 99%)

    do {
        let allFail = DictationSessionReportBuilder.buildReport(
            from: [
                DictationAttemptRecord(gesture: "g", armedApp: "app", outcome: .failed(
                    stage: .capture, failure: .emptyCapture, timings: DictationSession.StageTimings())),
            ],
            m2TerminalConditionCounts: nil, generatedAt: Date())
        check(allFail.perArmedApp.first?.successRate == 0, "insertion-bar: 0/1 must report successRate == 0")
        let rendered = DictationSessionReportRenderer.renderHumanReadable(allFail)
        check(rendered.contains("[MISSED]"), "insertion-bar: 0% insertion success must render [MISSED]")
    }

    guard failures.isEmpty else {
        let first = failures[0]
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-session-report-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("dictation-session-report-check: PASS")
    return true
}
