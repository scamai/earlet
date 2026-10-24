import Foundation

/// D-3's closing deliverable: turn one `--dictate` session into a
/// machine-readable report instead of scrollback the human has to transcribe
/// by hand. Every type here is pure/`Sendable` and covered by
/// `DictationSessionReportSelfCheck` without any TCC/AX/pasteboard access —
/// the only TCC-gated piece is `DictateCLI.swift`'s call sites that feed
/// records in and write the two files out.
///
/// ## The bug this file exists to not repeat
///
/// An earlier pass in this project's own history let an EMPTY percentile
/// input silently produce `0`, which then printed as a bogus "MET, 0 ms"
/// verdict — a stage that was never measured looked like it blew past the
/// bar. Every percentile/stat type below returns `nil` on empty input, and
/// every renderer downstream is required to print "not measured" (with a
/// reason) rather than treat `nil` as `0`.

// MARK: - Percentile (pure)

/// Nearest-rank percentile, `p` in `[0, 1]`. `nil` on empty input — see this
/// file's header comment for why that must never collapse to `0`.
public func dictationPercentile(_ values: [Double], _ p: Double) -> Double? {
    guard !values.isEmpty else { return nil }
    let sorted = values.sorted()
    let idx = min(sorted.count - 1, max(0, Int((Double(sorted.count) * p).rounded(.up)) - 1))
    return sorted[idx]
}

/// p50/p90 + sample count for one latency stage. `p50`/`p90` are `nil` iff
/// `n == 0` — the ONLY condition that produces `nil` here, so a caller can
/// treat `n == 0` as the single source of truth for "not measured."
public struct DictationStageStat: Sendable, Equatable, Codable {
    public let n: Int
    public let p50Ms: Double?
    public let p90Ms: Double?

    public init(values: [Double]) {
        self.n = values.count
        self.p50Ms = dictationPercentile(values, 0.50)
        self.p90Ms = dictationPercentile(values, 0.90)
    }
}

// MARK: - One recorded attempt

/// One dictation attempt as the CLI observed it — captured around every
/// `triggerUp()` call. Distinct from `DictationSession.Outcome`: this also
/// carries the resolved target app (the report's per-app axis) and a
/// wall-clock timestamp, neither of which `Outcome` tracks.
public struct DictationAttemptRecord: Sendable {
    public let timestamp: Date
    public let gesture: String
    /// Best-effort app bundle id **as of trigger-down**, resolved via
    /// `FrontmostApplicationResolver` in `DictationController.begin()`. `nil`
    /// if resolution genuinely failed (no permission, no focused element, no
    /// bundle id) — never fabricated as a placeholder string.
    ///
    /// Named for the moment it samples, because the moment is the whole of
    /// its meaning. Arm time is the right moment for what it was built for —
    /// the per-app formatting exception, which has to be decided before the
    /// session is built — and `begin()`'s own comment explains why resolving
    /// later would name whatever happened to be frontmost seconds after.
    ///
    /// It is the WRONG moment for attributing an insertion failure, and this
    /// field used to be called `targetApp` and rolled up into a
    /// `perAppInsertion` table that read as exactly that. The user can hold
    /// the hotkey, speak, and release with focus somewhere else; and the AX
    /// gate that rejects 20 of 31 failures never contributed to this value at
    /// all, because `TextInserter` resolved its own frontmost app only after
    /// the gate had already returned. A report reading "Slack: 7 attempted, 0
    /// inserted" therefore means "7 dictations were ARMED while Slack was
    /// frontmost" and says nothing about what the gate looked at — an
    /// inference that was drawn from it and that nothing supported.
    ///
    /// For the insertion target, read `InsertTiming.gateRejectedApp`, which
    /// samples at rejection.
    public let armedApp: String?
    public let outcome: DictationSession.Outcome

    public init(timestamp: Date = Date(), gesture: String, armedApp: String?, outcome: DictationSession.Outcome) {
        self.timestamp = timestamp
        self.gesture = gesture
        self.armedApp = armedApp
        self.outcome = outcome
    }

    var timings: DictationSession.StageTimings {
        switch outcome {
        case .success(_, _, _, let t): return t
        case .failed(_, _, let t): return t
        // A voice command still has real capture/ASR stage timings; the
        // paste half is genuinely absent (nothing was pasted), which the
        // all-nil `pasteTiming` already says honestly.
        case .commandHandled(_, _, _, let t): return t
        // Same again for a spoken workflow command: the hold really was held
        // and really was decoded, so those stages are measurements. Nothing
        // was pasted, and the all-nil `pasteTiming` says so.
        case .spokenCommand(_, let t): return t
        }
    }

    var succeeded: Bool {
        if case .success = outcome { return true }
        return false
    }

    var clipboardByteIdentical: Bool? {
        if case .success(_, let identical, _, _) = outcome { return identical }
        return nil
    }

    var failureClassification: String? {
        guard case .failed(let stage, let failure, _) = outcome else { return nil }
        // The same failure enum can describe capture gating or an empty
        // decoder result. A capture rejection happened before ASR, so its
        // description must identify the gate rather than blame the decoder.
        if stage == .capture, failure == .emptyTranscript {
            return "capture: the clip was silent — the gate refused it and ASR never ran"
        }
        return "\(stage): \(failure)"
    }
}

// MARK: - The report itself (Codable — the JSON sidecar)

// `Sendable` (all-value-type payload) so the PRODUCT path can build and
// persist a report off the main actor — `DictationController` records
// attempts on the dictation path (a lock + struct append, microseconds) and
// defers the build/serialize/write to a detached flush, which requires the
// report to cross an isolation boundary.
public struct DictationSessionReport: Codable, Equatable, Sendable {
    public struct GestureOutcome: Codable, Equatable, Sendable {
        public let gesture: String
        public let attempted: Int
        public let inserted: Int
        public var successRate: Double? {
            attempted > 0 ? Double(inserted) / Double(attempted) : nil
        }
    }

    public struct AppInsertion: Codable, Equatable, Sendable {
        public let app: String
        public let attempted: Int
        public let inserted: Int
        /// `nil` iff `attempted == 0` — cannot happen for an entry that
        /// exists (an app only appears here because at least one attempt
        /// targeted it), kept optional anyway so the type doesn't lie about
        /// a division that can't occur rather than defending against it.
        public var successRate: Double? { attempted > 0 ? Double(inserted) / Double(attempted) : nil }
    }
    public struct FailureBreakdown: Codable, Equatable, Sendable {
        public let classification: String
        public let count: Int
    }

    public let generatedAt: Date
    public let attempted: Int
    public let inserted: Int
    public let failed: Int
    public let failuresByClassification: [FailureBreakdown]

    public let armMs: DictationStageStat
    public let captureMs: DictationStageStat
    public let asrMs: DictationStageStat
    public let pasteGateMs: DictationStageStat
    public let pasteSynthesizeMs: DictationStageStat
    public let pasteWaitForAskMs: DictationStageStat
    public let pasteRestoreMs: DictationStageStat
    public let pasteTotalMs: DictationStageStat
    /// `InsertTiming.blockingMs` — what the caller actually waited for.
    ///
    /// `pasteTotalMs` includes the configured clipboard-restore delay even
    /// when it runs detached. This statistic counts the blocking work only;
    /// it equals the total when restoration runs inline.
    public let pasteUserWaitMs: DictationStageStat
    /// `captureMs + asrMs` — the committed "release-to-text"
    /// bar (p50 <= 500ms / p90 <= 1000ms, `DictationLatencyCheck.swift`'s
    /// same bar).
    public let releaseToTextMs: DictationStageStat
    /// `releaseToTextMs + pasteTotalMs` — the full release-to-inserted
    /// number this human session exists to produce. Shown against the same
    /// 500/1000ms numbers for reference, but see the renderer: only
    /// `releaseToTextMs` is the actually-committed bar, this one is
    /// informational.
    public let releaseToInsertedMs: DictationStageStat
    /// `releaseToTextMs + pasteUserWaitMs` — release to the text landing,
    /// as the user experienced it. Same population as
    /// `releaseToInsertedMs`; differs from it by exactly the detached
    /// restore window.
    public let releaseToInsertedUserWaitMs: DictationStageStat

    /// Which concrete input edge opened each attempt. Without this split, a
    /// broken double-tap detector and a broken hold binding collapse into the
    /// same aggregate even though their fixes live in different state
    /// machines. Values are stable labels, never user input.
    public let perGesture: [GestureOutcome]
    public let perArmedApp: [AppInsertion]

    public let clipboardChecked: Int
    public let clipboardByteIdentical: Int

    /// `nil` iff `feature.dictation_learning` was OFF this session — "not
    /// measured, flag was off," never a fabricated empty/zero tally.
    public let m2TerminalConditionCounts: [String: Int]?
}

// MARK: - Building a report from records (pure)

public final class DictationSessionReportBuilder: @unchecked Sendable {
    private let lock = NSLock()
    private var records: [DictationAttemptRecord] = []

    public init() {}

    public func record(_ r: DictationAttemptRecord) {
        lock.lock(); records.append(r); lock.unlock()
    }

    /// How many attempts are waiting to be flushed. Exists so the flush
    /// caller can skip empty writes WITHOUT keeping its own counter — the
    /// controller used to mirror this in a hand-maintained field whose only
    /// failure mode was silent drift (simplification-audit finding,
    /// 2026-08-09).
    public var pendingCount: Int {
        lock.lock(); defer { lock.unlock() }
        return records.count
    }

    public func build(m2TerminalConditionCounts: [String: Int]?, generatedAt: Date = Date()) -> DictationSessionReport {
        lock.lock(); let snapshot = records; lock.unlock()
        return Self.buildReport(from: snapshot, m2TerminalConditionCounts: m2TerminalConditionCounts, generatedAt: generatedAt)
    }

    /// Pure — factored out so the self-check can exercise report-building
    /// (including the empty-input/n=0 case) without the lock or any mutable
    /// session state.
    static func buildReport(
        from records: [DictationAttemptRecord],
        m2TerminalConditionCounts: [String: Int]?,
        generatedAt: Date
    ) -> DictationSessionReport {
        let attempted = records.count
        let inserted = records.filter { $0.succeeded }.count
        let failed = attempted - inserted

        var failureCounts: [String: Int] = [:]
        for r in records {
            if let f = r.failureClassification {
                failureCounts[f, default: 0] += 1
            }
        }
        let failuresByClassification = failureCounts
            .map { DictationSessionReport.FailureBreakdown(classification: $0.key, count: $0.value) }
            .sorted { $0.count != $1.count ? $0.count > $1.count : $0.classification < $1.classification }

        let armValues = records.compactMap { $0.timings.armMs }
        let captureValues = records.compactMap { $0.timings.captureStopMs }
        let asrValues = records.compactMap { $0.timings.asrMs }
        let releaseToTextValues = records.compactMap { $0.timings.releaseToTextMs }
        let releaseToInsertedValues = records.compactMap { $0.timings.releaseToInsertedMs }

        // Paste sub-stages: only a sample from an attempt that actually
        // reached that specific sub-stage counts — a gate failure (no
        // focused element, Secure Input, ...) or a failed key-event post
        // never ran `waitForPasteOutcome`/the restore-delay sleep at all,
        // and its `InsertTiming` fields default to `0`, NOT "not run." See
        // this file's header comment: including those 0s would be exactly
        // the fabricated-zero bug this report exists to avoid.
        var gateValues: [Double] = []
        var synthesizeValues: [Double] = []
        var waitForAskValues: [Double] = []
        var restoreValues: [Double] = []
        var pasteTotalValues: [Double] = []
        var pasteUserWaitValues: [Double] = []
        var releaseToInsertedUserWaitValues: [Double] = []
        for r in records {
            guard let timing = r.timings.pasteTiming else { continue }
            gateValues.append(timing.gateCheckMs)  // always a real gate check whenever pasteTiming exists at all
            pasteTotalValues.append(timing.totalMs)
            pasteUserWaitValues.append(timing.blockingMs)
            if let releaseToText = r.timings.releaseToTextMs {
                releaseToInsertedUserWaitValues.append(releaseToText + timing.blockingMs)
            }
            if reachedSynthesizeStage(r.outcome) {
                synthesizeValues.append(timing.synthesizeMs)
                restoreValues.append(timing.restoreMs)
            }
            if reachedWaitForAskStage(r.outcome) {
                waitForAskValues.append(timing.waitForAskMs)
            }
        }

        var perApp: [String: (attempted: Int, inserted: Int)] = [:]
        for r in records {
            let key = r.armedApp ?? "(unresolved)"
            var entry = perApp[key] ?? (0, 0)
            entry.attempted += 1
            if r.succeeded { entry.inserted += 1 }
            perApp[key] = entry
        }
        let perArmedApp = perApp
            .map { DictationSessionReport.AppInsertion(app: $0.key, attempted: $0.value.attempted, inserted: $0.value.inserted) }
            .sorted { $0.app < $1.app }

        var byGesture: [String: (attempted: Int, inserted: Int)] = [:]
        for record in records {
            let key = record.gesture.isEmpty ? "unknown" : record.gesture
            var entry = byGesture[key] ?? (0, 0)
            entry.attempted += 1
            if record.succeeded { entry.inserted += 1 }
            byGesture[key] = entry
        }
        let perGesture = byGesture
            .map {
                DictationSessionReport.GestureOutcome(
                    gesture: $0.key,
                    attempted: $0.value.attempted,
                    inserted: $0.value.inserted)
            }
            .sorted { $0.gesture < $1.gesture }

        let clipboardChecked = records.filter { $0.clipboardByteIdentical != nil }.count
        let clipboardIdentical = records.filter { $0.clipboardByteIdentical == true }.count

        return DictationSessionReport(
            generatedAt: generatedAt,
            attempted: attempted, inserted: inserted, failed: failed,
            failuresByClassification: failuresByClassification,
            armMs: DictationStageStat(values: armValues),
            captureMs: DictationStageStat(values: captureValues),
            asrMs: DictationStageStat(values: asrValues),
            pasteGateMs: DictationStageStat(values: gateValues),
            pasteSynthesizeMs: DictationStageStat(values: synthesizeValues),
            pasteWaitForAskMs: DictationStageStat(values: waitForAskValues),
            pasteRestoreMs: DictationStageStat(values: restoreValues),
            pasteTotalMs: DictationStageStat(values: pasteTotalValues),
            pasteUserWaitMs: DictationStageStat(values: pasteUserWaitValues),
            releaseToTextMs: DictationStageStat(values: releaseToTextValues),
            releaseToInsertedMs: DictationStageStat(values: releaseToInsertedValues),
            releaseToInsertedUserWaitMs: DictationStageStat(values: releaseToInsertedUserWaitValues),
            perGesture: perGesture,
            perArmedApp: perArmedApp,
            clipboardChecked: clipboardChecked, clipboardByteIdentical: clipboardIdentical,
            m2TerminalConditionCounts: m2TerminalConditionCounts
        )
    }

    /// `true` iff `outcome` reached far enough into `TextInserter.insertMeasured`
    /// to run the actual synthesize-⌘V / restore steps (i.e. the gate check
    /// passed) — see `insertMeasured`'s early-return branches.
    static func reachedSynthesizeStage(_ outcome: DictationSession.Outcome) -> Bool {
        switch outcome {
        case .success:
            return true
        // Voice delete never enters `insertMeasured` at all — it posts
        // backspaces instead. Excluding it keeps the paste histograms a
        // description of pastes. A spoken workflow command does not enter it
        // either, and for a stronger reason: it never had text to paste.
        case .commandHandled, .spokenCommand:
            return false
        case .failed(let stage, let failure, _):
            guard stage == .paste else { return false }
            if case .insert(let f) = failure {
                switch f {
                case .accessibilityPermissionMissing, .noFocusedElement, .focusedElementNotEditable,
                     .secureInputActive, .secureFieldFocused:
                    return false  // gate-only failure, never reached synthesize
                case .keyEventPostFailed, .pasteNeverRequested:
                    return true
                }
            }
            return false
        }
    }

    /// `true` iff `outcome` reached `waitForPasteOutcome` (and therefore the
    /// fixed restore-delay sleep right after it) — narrower than
    /// `reachedSynthesizeStage`: `keyEventPostFailed` reaches synthesize but
    /// fails BEFORE the wait.
    static func reachedWaitForAskStage(_ outcome: DictationSession.Outcome) -> Bool {
        switch outcome {
        case .success:
            return true
        case .commandHandled, .spokenCommand:
            return false
        case .failed(let stage, let failure, _):
            guard stage == .paste else { return false }
            if case .insert(.pasteNeverRequested) = failure { return true }
            return false
        }
    }
}

// MARK: - Human-readable rendering, with explicit MET/MISSED against the committed bars

public enum DictationSessionReportRenderer {
    /// The committed release-to-text bar
    /// (`DictationLatencyCheck.swift`'s header doc comment): p50 <= 500ms,
    /// p90 <= 1000ms.
    public static let releaseToTextP50BarMs = 500.0
    public static let releaseToTextP90BarMs = 1000.0
    /// D-3's own exit metric: insertion success >= 99% across the four
    /// target apps.
    public static let insertionSuccessBar = 0.99

    public static func renderHumanReadable(_ report: DictationSessionReport) -> String {
        var lines: [String] = []
        let df = ISO8601DateFormatter()
        lines.append("=== Earlet session report — \(df.string(from: report.generatedAt)) ===")
        lines.append("")
        lines.append("Dictations: attempted=\(report.attempted)  inserted=\(report.inserted)  failed=\(report.failed)")
        if report.failuresByClassification.isEmpty {
            lines.append("Failures: none")
        } else {
            lines.append("Failures by classification:")
            for f in report.failuresByClassification {
                lines.append("  \(f.count)x  \(f.classification)")
            }
        }
        lines.append("")

        lines.append("--- Per-stage latency (n = attempts that reached that stage) ---")
        lines.append(statLine("arm", report.armMs))
        lines.append(statLine("capture", report.captureMs))
        lines.append(statLine("ASR", report.asrMs))
        lines.append(statLine("paste gate", report.pasteGateMs))
        lines.append(statLine("paste synthesize", report.pasteSynthesizeMs))
        lines.append(statLine("paste waitForAsk", report.pasteWaitForAskMs))
        lines.append(statLine("paste restore", report.pasteRestoreMs))
        lines.append(statLine("paste total", report.pasteTotalMs))
        lines.append(statLine("paste user wait", report.pasteUserWaitMs))
        lines.append("  (paste total keeps the configured 500ms clipboard window; paste user wait is what the caller blocked on)")
        lines.append("")

        lines.append("--- Against the committed bars ---")
        lines.append(barLine(
            "release->text (capture+ASR) — THE committed bar",
            report.releaseToTextMs, p50Bar: releaseToTextP50BarMs, p90Bar: releaseToTextP90BarMs))
        lines.append(barLine(
            "release->inserted (full pipeline, incl. paste) — informational, "
                + "same numbers shown for reference, NOT itself a committed bar",
            report.releaseToInsertedMs, p50Bar: releaseToTextP50BarMs, p90Bar: releaseToTextP90BarMs))
        lines.append(barLine(
            "release->inserted as the user waited (capture+ASR+paste user wait) — informational",
            report.releaseToInsertedUserWaitMs, p50Bar: releaseToTextP50BarMs, p90Bar: releaseToTextP90BarMs))
        lines.append("")

        lines.append("--- Trigger source (inserted / attempted) ---")
        if report.perGesture.isEmpty {
            lines.append("  not measured — no dictations attempted this session")
        } else {
            for gesture in report.perGesture {
                let rate = gesture.successRate.map { String(format: "%.1f%%", $0 * 100) }
                    ?? "not measured"
                lines.append("  \(gesture.gesture): \(gesture.inserted)/\(gesture.attempted) (\(rate))")
            }
        }
        lines.append("")

        lines.append("--- Insertion success per app ARMED IN (not the insertion target — see DictationAttemptRecord.armedApp) (bar: >= \(Int(insertionSuccessBar * 100))%) ---")
        if report.perArmedApp.isEmpty {
            lines.append("  not measured — no dictations attempted this session")
        } else {
            for app in report.perArmedApp {
                guard let rate = app.successRate else {
                    lines.append("  \(app.app): not measured (0 attempts)")
                    continue
                }
                let verdict = rate >= insertionSuccessBar ? "MET" : "MISSED"
                let pct = String(format: "%.1f", rate * 100)
                lines.append("  \(app.app.padding(toLength: max(app.app.count, 30), withPad: " ", startingAt: 0)) "
                    + "\(app.inserted)/\(app.attempted) = \(pct)" + "%  [\(verdict)]")
            }
        }
        lines.append("")

        lines.append("--- Clipboard integrity ---")
        if report.clipboardChecked == 0 {
            lines.append("  not measured — no successful insertion this session to check restore against")
        } else {
            let pct = 100.0 * Double(report.clipboardByteIdentical) / Double(report.clipboardChecked)
            lines.append(String(format: "  %d/%d successful insertions restored the clipboard byte-identical (%.1f%%)",
                report.clipboardByteIdentical, report.clipboardChecked, pct))
        }
        lines.append("")

        lines.append("--- M2 correction-observer terminal conditions ---")
        if let m2 = report.m2TerminalConditionCounts {
            if m2.isEmpty {
                lines.append("  feature.dictation_learning was ON, but no observations completed this session")
            } else {
                for (condition, count) in m2.sorted(by: { $0.key < $1.key }) {
                    lines.append("  \(count)x  \(condition)")
                }
            }
        } else {
            lines.append("  not measured — feature.dictation_learning was OFF this session")
        }

        return lines.joined(separator: "\n")
    }

    private static func statLine(_ name: String, _ stat: DictationStageStat) -> String {
        guard let p50 = stat.p50Ms, let p90 = stat.p90Ms else {
            return "  \(name): not measured (n=0 — no attempt reached this stage)"
        }
        let padded = name.padding(toLength: max(name.count, 18), withPad: " ", startingAt: 0)
        return "  \(padded) p50=\(String(format: "%.2f", p50))ms  p90=\(String(format: "%.2f", p90))ms  (n=\(stat.n))"
    }

    private static func barLine(_ name: String, _ stat: DictationStageStat, p50Bar: Double, p90Bar: Double) -> String {
        guard let p50 = stat.p50Ms, let p90 = stat.p90Ms else {
            return "  \(name): not measured (n=0 — no attempt reached this stage this session)"
        }
        let p50Verdict = p50 <= p50Bar ? "MET" : "MISSED"
        let p90Verdict = p90 <= p90Bar ? "MET" : "MISSED"
        return "  \(name): p50=\(String(format: "%.2f", p50))ms (bar <= \(Int(p50Bar))ms) [\(p50Verdict)]   "
            + "p90=\(String(format: "%.2f", p90))ms (bar <= \(Int(p90Bar))ms) [\(p90Verdict)]  (n=\(stat.n))"
    }
}
