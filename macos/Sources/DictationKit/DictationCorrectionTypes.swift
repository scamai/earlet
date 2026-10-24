import Foundation

/// How an observation of a dictated span ended. The first eight are the
/// observer's original set; `rawValue` is snake_case so a printed or logged
/// tally reads the same from every platform.
///
/// `editSettled` came next, diagnosed 2026-08-06. An anchor that is a plain
/// text search breaks the instant the user edits anything nearby, so with
/// one `span_missing` would end most observations early. This
/// observer's anchor (marker range / selected range) keeps tracking
/// through the edit, which paradoxically means that condition almost never
/// fires here. A normal single-word
/// correction (small edit distance, anchor intact, no trailing newline,
/// field non-empty) then crosses none of the eight ported conditions, and
/// nothing terminates it early: it sits until the 90s backstop
/// (`watchExpired`), which reads as a hang to the user and
/// delays every candidate correction by up to a minute and a half.
/// `editSettled` is the fix — see `DictationTerminalConditionEvaluator`'s
/// doc comment for exactly where it sits in the priority order.
///
/// Dictation adds five more (the last group below) for its learning contract
/// (LEARNING.md §7): the observation also ends when the field loses focus,
/// the caller can abandon it without learning, and a start is refused
/// outright for a password field or another app's field.
public enum DictationTerminalCondition: String, Sendable, Equatable, CaseIterable, CustomStringConvertible {
    case spanMissing = "span_missing"
    case nextTake = "next_take"
    case lineSubmitted = "line_submitted"
    case contextChangedSpanIntact = "context_changed_span_intact"
    case watchExpired = "watch_expired"
    case spanContextLost = "span_context_lost"
    case revisionTooLarge = "revision_too_large"
    case fieldCleared = "field_cleared"
    /// Added after the original eight. See the enum doc comment
    /// above and `DictationTerminalConditionEvaluator` for the full
    /// reasoning and where this sits in evaluation priority.
    case editSettled = "edit_settled"

    // ─── Dictation's additions (LEARNING.md §7) ───

    /// The observed field lost focus inside its app. A clean end: the span
    /// is read one last time and may be learned from.
    case focusLeft = "focus_left"
    /// The caller gave up on the observation (pause, quit, the next
    /// dictation, learning switched off). Never learned from, whatever the
    /// span held: see `DictationCorrectionObserver.abandon()`.
    case abandoned = "abandoned"
    /// Refused before it began: Secure Input is on, or the focused element
    /// is a password field. Nothing is read.
    case secureField = "secure_field"
    /// Refused before it began: the focused field belongs to another app
    /// than the one the paste went to.
    case otherApplication = "other_application"
    /// Refused before it began: no focused element that takes text.
    case noFocusedField = "no_focused_field"

    public var description: String { rawValue }

    /// Whether an observation that ended THIS way is trustworthy enough to
    /// feed `DictationCorrectionDiffExtractor` (design §3 condition 7's
    /// note: an edit-distance-exceeded observation "should NOT feed the
    /// correction-mining pipeline, only the aggregate rejection-rate
    /// metric"). `spanMissing`/`spanContextLost`/
    /// `nextTake` are also excluded — there is no reliably
    /// located edited text to diff against in any of those three.
    /// `editSettled` IS eligible: by construction it only fires when the
    /// anchor is intact, the span is non-empty, and the edit distance is
    /// in-budget — exactly as clean and comparable as `lineSubmitted`.
    /// So is `focusLeft`. `abandoned` and the three refusals are not.
    ///
    /// Eligibility alone never makes text learnable: the span must also
    /// still be anchored (`reportedSpan(_:)`), so an eligible condition
    /// reached with a broken anchor reports nothing.
    public var isEligibleForDiffMining: Bool {
        switch self {
        case .lineSubmitted, .contextChangedSpanIntact, .watchExpired, .fieldCleared,
             .editSettled, .focusLeft:
            return true
        case .spanMissing, .nextTake, .spanContextLost, .revisionTooLarge,
             .abandoned, .secureField, .otherApplication, .noFocusedField:
            return false
        }
    }

    /// What an observation that ended this way reports as the edited text:
    /// the span's final text when this condition may be learned from, and
    /// `nil` otherwise. `anchoredSpan` is itself `nil` whenever the anchor no
    /// longer holds, so the rest of the field can never stand in for it.
    public func reportedSpan(_ anchoredSpan: String?) -> String? {
        isEligibleForDiffMining ? anchoredSpan : nil
    }
}

/// One record per dictation, once observation terminates: the text at each
/// stage after recognition (formatted, pasted, then what the user left in the
/// field). The raw recognizer output is not repeated here, because
/// `DictationCapture`'s caller already has it before `TextInserter` is ever
/// called.
public struct DictationObservation: Sendable, Equatable {
    /// What `TextInserter` pasted (already known at insert time).
    public var dictatedText: String
    /// Post-formatting stage — M4 territory, `nil` until M4 exists.
    public var formattedText: String?
    /// What was actually placed on the pasteboard and pasted.
    public var pastedText: String
    /// The final text of the span Dictation pasted, and nothing else from
    /// the field (`DictationSpanAnchor`). `nil` unless the observation ended
    /// in a way that may be learned from with the anchor still holding
    /// (`DictationTerminalCondition.reportedSpan(_:)`).
    ///
    /// There is deliberately no whole-field value here: An earlier implementation carried
    /// one (`axTextAfter`), which in a terminal or a long document is
    /// everything the user has on screen. The observer reads the field to
    /// find the span and keeps that read to itself.
    public var editedText: String?
    public var terminalCondition: DictationTerminalCondition
    /// Bundle identifier of the focused app at insert time.
    public var observedApp: String
    public var startedAt: Date
    public var terminatedAt: Date
    /// The span was still located, and read, when the observation ended —
    /// the end of a continuous watch can then tell an untouched dictation
    /// ("No correction detected.") from a field that never yielded its span
    /// ("This field could not be checked."), without the span's text leaving
    /// the observer. `false` for every refusal and every interrupt.
    public var spanObservedToEnd: Bool
    /// The span was seen to differ from the pasted text at least once,
    /// whether the change was kept, reverted or never settled into a
    /// revision — what separates "No correction detected." from "No
    /// vocabulary change was learned." at an end that held its span.
    public var sawChange: Bool

    public init(
        dictatedText: String, formattedText: String?, pastedText: String, editedText: String?,
        terminalCondition: DictationTerminalCondition, observedApp: String,
        startedAt: Date, terminatedAt: Date,
        spanObservedToEnd: Bool = false, sawChange: Bool = false
    ) {
        self.dictatedText = dictatedText
        self.formattedText = formattedText
        self.pastedText = pastedText
        self.editedText = editedText
        self.terminalCondition = terminalCondition
        self.observedApp = observedApp
        self.startedAt = startedAt
        self.terminatedAt = terminatedAt
        self.spanObservedToEnd = spanObservedToEnd
        self.sawChange = sawChange
    }
}

/// Locates `needle` in `haystack` anchored to the END of the value — "the
/// just-pasted text is the most recent addition, so searching from the tail
/// handles the common case of appending to existing content." An earlier implementation used it
/// as a third anchor tier; here it only tells `spanContextLost` from
/// `contextChangedSpanIntact` once the anchor is gone, and never
/// locates a span to learn from (it cannot see an edit).
public enum DictationTailTextSearch {
    public static func find(_ needle: String, in haystack: String) -> Range<String.Index>? {
        guard !needle.isEmpty else { return nil }
        return haystack.range(of: needle, options: .backwards)
    }
}

/// Condition 3 (design §3): the field grew by EXACTLY one trailing
/// `\n`/`\r` relative to the last-observed snapshot, with no other change.
public enum DictationTrailingNewline {
    public static func isTrailingNewlineOnly(previous: String, current: String) -> Bool {
        guard current.count == previous.count + 1, current.hasPrefix(previous) else { return false }
        guard let last = current.last else { return false }
        return last == "\n" || last == "\r"
    }
}

/// Plain Levenshtein distance, normalized to [0, 1] against the longer
/// string's length: the measure design §3 condition 7 compares with its
/// threshold.
public enum DictationEditDistance {
    public static func levenshtein(_ a: String, _ b: String) -> Int {
        let aChars = Array(a)
        let bChars = Array(b)
        if aChars.isEmpty { return bChars.count }
        if bChars.isEmpty { return aChars.count }
        var prev = Array(0...bChars.count)
        var curr = [Int](repeating: 0, count: bChars.count + 1)
        for i in 1...aChars.count {
            curr[0] = i
            for j in 1...bChars.count {
                if aChars[i - 1] == bChars[j - 1] {
                    curr[j] = prev[j - 1]
                } else {
                    curr[j] = 1 + min(prev[j - 1], prev[j], curr[j - 1])
                }
            }
            swap(&prev, &curr)
        }
        return prev[bChars.count]
    }

    public static func normalized(_ a: String, _ b: String) -> Double {
        let maxLen = max(a.count, b.count)
        guard maxLen > 0 else { return 0 }
        return Double(levenshtein(a, b)) / Double(maxLen)
    }
}

/// The inputs to one debounced "read current value, compare to anchor"
/// step — design §3's ordering note. Pure by construction: every field is
/// a plain value the caller already read via AX, so this struct (and
/// `DictationTerminalConditionEvaluator.evaluate`) is fully exercisable
/// without AXUIElement/TCC.
public struct DictationObservationTick: Sendable, Equatable {
    public var anchorStillValid: Bool
    /// Current text at the anchored span, when `anchorStillValid`. `nil`
    /// when the anchor is broken — the evaluator falls back to
    /// `fullFieldValue` for the anchor-broken checks (conditions 4/6/8).
    public var anchoredSpanText: String?
    public var fullFieldValue: String
    public var previousFieldValue: String
    public var dictatedText: String
    public var elapsedSeconds: TimeInterval
    public var windowSeconds: TimeInterval
    public var editDistanceThreshold: Double
    /// OUR addition (see `DictationTerminalCondition.editSettled`'s doc
    /// comment): wall-clock seconds since the field's content last
    /// actually changed — tracked by the caller (`ObservationSession`),
    /// not derivable from a single tick's before/after snapshot alone,
    /// since "quiet since when" needs history beyond one comparison.
    public var secondsSinceLastChange: TimeInterval
    /// OUR addition's threshold — `Config.settleSeconds`.
    public var settleSeconds: TimeInterval

    public init(
        anchorStillValid: Bool, anchoredSpanText: String?, fullFieldValue: String,
        previousFieldValue: String, dictatedText: String, elapsedSeconds: TimeInterval,
        windowSeconds: TimeInterval, editDistanceThreshold: Double,
        secondsSinceLastChange: TimeInterval = 0, settleSeconds: TimeInterval = .greatestFiniteMagnitude
    ) {
        self.anchorStillValid = anchorStillValid
        self.anchoredSpanText = anchoredSpanText
        self.fullFieldValue = fullFieldValue
        self.previousFieldValue = previousFieldValue
        self.dictatedText = dictatedText
        self.elapsedSeconds = elapsedSeconds
        self.windowSeconds = windowSeconds
        self.editDistanceThreshold = editDistanceThreshold
        self.secondsSinceLastChange = secondsSinceLastChange
        self.settleSeconds = settleSeconds
    }
}

/// Design §3's ordering: "conditions 1, 4, 6, 8 are all evaluated from the
/// same 'read current value, compare to anchor' step... Condition 3 must
/// be checked BEFORE the generic edit-distance check... Condition 5 is a
/// timer, checked independently." Condition 2 (`next_take`)
/// is NOT decided here — it is an external interrupt from the capture
/// layer; the caller terminates directly on that signal without going
/// through this evaluator. Condition 1 (`span_missing`) is also
/// not decided here — it is the ANCHOR-ESTABLISHMENT failure path
/// (`DictationSpanAnchor.establish`), evaluated once before observation
/// even begins, not on a recurring tick. Neither are `focus_left`,
/// `abandoned` or the three refusals, which the observer decides itself.
///
/// OUR addition, `editSettled`, is checked LAST — strictly after every
/// ported condition above, including edit-distance (condition 7) — so it
/// can never preempt a higher-priority signal. It only catches the case
/// none of the eight catch: anchor intact, span non-empty, edit distance
/// in-budget, and now quiet for `settleSeconds`. See
/// `DictationTerminalCondition.editSettled`'s doc comment for why the
/// reference distribution implies this is missing, not optional polish.
public enum DictationTerminalConditionEvaluator {
    public static func evaluate(_ tick: DictationObservationTick) -> DictationTerminalCondition? {
        // Condition 5: the hard wall-clock backstop. Checked first — it is
        // the cheapest test, and it is meant to fire whenever nothing more
        // specific caught the observation in time, so a tick that blew the
        // window reports the timeout regardless of what else might also
        // apply this same tick.
        if tick.elapsedSeconds >= tick.windowSeconds {
            return .watchExpired
        }

        // Condition 3 MUST be checked before edit-distance (condition 7) —
        // a bare trailing newline would otherwise inflate edit distance
        // and misclassify as condition 7.
        if DictationTrailingNewline.isTrailingNewlineOnly(
            previous: tick.previousFieldValue, current: tick.fullFieldValue
        ) {
            return .lineSubmitted
        }

        if !tick.anchorStillValid {
            // Conditions 4/6/8 all read off the full field value once the
            // anchor itself is gone. Design's ordering note: "a value
            // that's both empty AND anchor-broken should classify as
            // field_cleared, the more specific condition" — so the
            // empty check wins over the found/not-found split below it.
            let trimmed = tick.fullFieldValue.trimmingCharacters(in: .whitespacesAndNewlines)
            if trimmed.isEmpty {
                return .fieldCleared
            }
            if DictationTailTextSearch.find(tick.dictatedText, in: tick.fullFieldValue) != nil {
                return .contextChangedSpanIntact
            }
            return .spanContextLost
        }

        // Anchor still valid: the anchored span itself shrinking to empty
        // is condition 8's OTHER path — distinct from the field-level
        // check above, since the field can be non-empty while just the
        // anchored span was cleared (e.g. select-all-delete inside a
        // larger document).
        if let spanText = tick.anchoredSpanText, spanText.isEmpty, !tick.dictatedText.isEmpty {
            return .fieldCleared
        }

        if let spanText = tick.anchoredSpanText {
            let distance = DictationEditDistance.normalized(tick.dictatedText, spanText)
            if distance > tick.editDistanceThreshold {
                return .revisionTooLarge
            }
        }

        // OUR addition — checked last, see this type's doc comment. Only
        // fires once the user has actually diverged from what was dictated
        // (a span identical to `dictatedText` has nothing to "settle" from,
        // and would otherwise fire the moment the 90s backstop's window
        // math happened to line up) AND the field has gone quiet for at
        // least `settleSeconds` since its last real content change.
        if let spanText = tick.anchoredSpanText, spanText != tick.dictatedText,
            tick.secondsSinceLastChange >= tick.settleSeconds {
            return .editSettled
        }

        return nil
    }
}

/// The feature's own diagnostics — task step 3: "RECORD which fired —
/// that distribution is the feature's own diagnostics." In-memory only,
/// per process; this is a measurement aid for the M2 exit metric
/// ("observation terminates cleanly >= 95%, no measurable typing lag"),
/// not a persisted analytics pipeline — persistence, if ever needed, is
/// M3's call, same as dictionary promotion itself.
public final class DictationTerminalConditionTally: @unchecked Sendable {
    private let lock = NSLock()
    private var counts: [DictationTerminalCondition: Int] = [:]

    public init() {}

    public func record(_ condition: DictationTerminalCondition) {
        lock.lock(); defer { lock.unlock() }
        counts[condition, default: 0] += 1
    }

    public func snapshot() -> [DictationTerminalCondition: Int] {
        lock.lock(); defer { lock.unlock() }
        return counts
    }
}
