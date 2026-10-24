import Foundation

/// Pure-logic self-check for the M2 correction observer
/// (`DictationCorrectionObserver`) — no UI, no TCC, no real `AXUIElement`,
/// no `AXObserver`. Covers the invariants that need no live app:
///   1. terminal-condition ordering/selection (all eight conditions, plus
///      the ordering notes design §3 calls out explicitly)
///   2. which ends may report a span at all
///   3. diff extraction (candidate `phrase -> replacement` mining)
/// Registered in the app entry point's `--self-check` chain next to
/// `runDictationSelfCheck` (M1).
public func runDictationCorrectionObserverSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }

    // The anchor itself (`DictationSpanAnchor`) and the admission refusals
    // are pinned by DictationKitTests/DictationSpanAnchorTests.swift.

    // MARK: tail text search — anchored to the END of the value

    do {
        let haystack = "hello world hello world"
        let range = DictationTailTextSearch.find("hello world", in: haystack)
        check(range != nil, "tail-search: must find a substring present in the haystack")
        if let range {
            check(
                haystack.distance(from: haystack.startIndex, to: range.lowerBound) == 12,
                "tail-search: must match the LAST occurrence (tail-anchored), not the first")
        }
        check(
            DictationTailTextSearch.find("missing", in: haystack) == nil,
            "tail-search: must return nil when the needle is absent")
        check(
            DictationTailTextSearch.find("", in: haystack) == nil,
            "tail-search: an empty needle must never match")
    }

    // MARK: trailing-newline detection — exactly one trailing \n or \r

    do {
        check(
            DictationTrailingNewline.isTrailingNewlineOnly(previous: "hello", current: "hello\n"),
            "trailing-newline: a single trailing \\n must be detected")
        check(
            DictationTrailingNewline.isTrailingNewlineOnly(previous: "hello", current: "hello\r"),
            "trailing-newline: a single trailing \\r must also count")
        check(
            !DictationTrailingNewline.isTrailingNewlineOnly(previous: "hello", current: "hello world"),
            "trailing-newline: a non-newline single-char-longer suffix must not count")
        check(
            !DictationTrailingNewline.isTrailingNewlineOnly(previous: "hello", current: "hello\n\n"),
            "trailing-newline: two trailing newlines must not count (exactly one only)")
        check(
            !DictationTrailingNewline.isTrailingNewlineOnly(previous: "hello", current: "xhello\n"),
            "trailing-newline: current must have `previous` as an exact PREFIX, not just contain it")
        check(
            !DictationTrailingNewline.isTrailingNewlineOnly(previous: "hello", current: "hello"),
            "trailing-newline: unchanged value must not count")
    }

    // MARK: edit distance — normalized Levenshtein

    do {
        check(DictationEditDistance.normalized("hello", "hello") == 0, "edit-distance: identical strings must be 0")
        check(DictationEditDistance.normalized("", "") == 0, "edit-distance: two empty strings must be 0 (no div-by-zero)")
        check(DictationEditDistance.normalized("abc", "xyz") == 1, "edit-distance: fully disjoint equal-length strings must be 1")
        let d = DictationEditDistance.normalized("kitten", "sitting")
        check(d > 0 && d < 1, "edit-distance: a partial edit must land strictly between 0 and 1, got \(d)")
    }

    // MARK: terminal-condition evaluator — all eight conditions + ordering

    func tick(
        anchorValid: Bool, anchoredSpanText: String?, fullValue: String, previousValue: String,
        dictated: String = "hello world", elapsed: TimeInterval = 1, window: TimeInterval = 90,
        threshold: Double = 0.6,
        // OUR addition's params default to "never settle" (0 seconds
        // quiet, an effectively-infinite threshold) so every EXISTING test
        // below is unaffected unless it opts in explicitly.
        secondsSinceLastChange: TimeInterval = 0, settleSeconds: TimeInterval = .greatestFiniteMagnitude
    ) -> DictationObservationTick {
        DictationObservationTick(
            anchorStillValid: anchorValid, anchoredSpanText: anchoredSpanText, fullFieldValue: fullValue,
            previousFieldValue: previousValue, dictatedText: dictated, elapsedSeconds: elapsed,
            windowSeconds: window, editDistanceThreshold: threshold,
            secondsSinceLastChange: secondsSinceLastChange, settleSeconds: settleSeconds)
    }

    do {
        // Condition 5: the hard wall-clock backstop, independent of
        // everything else.
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello world", fullValue: "hello world",
            previousValue: "hello world", elapsed: 90, window: 90)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .watchExpired,
            "terminal: elapsed >= window must fire watchExpired regardless of anything else")
    }

    do {
        // Condition 3 must win over condition 7 (design's explicit
        // ordering note) — a trailing newline alone would inflate edit
        // distance if checked in the wrong order.
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello world\n", fullValue: "hello world\n",
            previousValue: "hello world", dictated: "hello world")
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .lineSubmitted,
            "terminal: trailing-newline-added must be checked BEFORE edit-distance, not misclassified as revision_too_large")
    }

    do {
        // Condition 8 (field-level): anchor broken AND the field is
        // empty must be field_cleared, NOT span_context_lost — design's
        // explicit worked example.
        let t = tick(anchorValid: false, anchoredSpanText: nil, fullValue: "", previousValue: "hello world")
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .fieldCleared,
            "terminal: anchor-broken + field empty must be field_cleared (the more specific condition), not span_context_lost")
    }

    do {
        // Condition 4: anchor broken, but the dictated text is still
        // found verbatim elsewhere in the field.
        let t = tick(
            anchorValid: false, anchoredSpanText: nil, fullValue: "prefix hello world suffix",
            previousValue: "prefix suffix", dictated: "hello world")
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .contextChangedSpanIntact,
            "terminal: anchor broken but text still found verbatim must be context_changed_span_intact")
    }

    do {
        // Condition 6: anchor broken AND the text search also fails —
        // both the pointer and the text are gone.
        let t = tick(
            anchorValid: false, anchoredSpanText: nil, fullValue: "something completely different",
            previousValue: "hello world", dictated: "hello world")
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .spanContextLost,
            "terminal: anchor broken AND text not found must be span_context_lost")
    }

    do {
        // Condition 8 (span-level): anchor still VALID but the anchored
        // span itself shrank to empty (e.g. select-all-delete within a
        // larger document that otherwise still has content).
        let t = tick(
            anchorValid: true, anchoredSpanText: "", fullValue: "surrounding text still here",
            previousValue: "surrounding hello world text still here", dictated: "hello world")
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .fieldCleared,
            "terminal: anchor valid but the anchored span emptied must be field_cleared")
    }

    do {
        // Condition 7: anchor valid, edit distance clearly over
        // threshold — "the user rewrote this into something unrelated."
        let t = tick(
            anchorValid: true, anchoredSpanText: "something totally unrelated now",
            fullValue: "something totally unrelated now", previousValue: "hello world",
            dictated: "hello world", threshold: 0.6)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .revisionTooLarge,
            "terminal: anchor valid + edit distance over threshold must be revision_too_large")
    }

    do {
        // Still observing: anchor valid, a small correction well under
        // the edit-distance threshold, no newline, window not elapsed —
        // must return nil (keep watching), not any terminal condition.
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello world!", fullValue: "hello world!",
            previousValue: "hello world", dictated: "hello world", threshold: 0.6)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == nil,
            "terminal: a small in-budget edit must NOT terminate — observation should continue")
    }

    do {
        // Boundary: edit distance exactly AT the threshold must NOT fire
        // (design says "exceeds", a strict inequality).
        // "ab" vs "xy" at threshold 1.0 -> normalized distance == 1.0 exactly.
        let t = tick(
            anchorValid: true, anchoredSpanText: "xy", fullValue: "xy", previousValue: "ab",
            dictated: "ab", threshold: 1.0)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == nil,
            "terminal: edit distance exactly AT threshold must not fire (exceeds is strict >)")
    }

    // MARK: OUR addition — the settle/quiescence condition (task step 5's
    // required coverage: fires after quiescence, does NOT fire while edits
    // continue, does not preempt higher-priority conditions). Pure logic
    // only, exactly like every other block in this file — no live AX.

    do {
        // Fires after quiescence: anchor valid, a small in-budget edit
        // (diverged from dictated but well under the distance threshold),
        // and the field has been quiet for >= settleSeconds.
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello there", fullValue: "hello there",
            previousValue: "hello world", dictated: "hello world", threshold: 0.6,
            secondsSinceLastChange: 4, settleSeconds: 4)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .editSettled,
            "settle: a diverged, in-budget edit quiet for >= settleSeconds must fire editSettled")
    }

    do {
        // Does NOT fire while edits continue: same diverged edit, but the
        // field only went quiet for LESS than settleSeconds — still
        // mid-edit, or at least not yet confidently done.
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello there", fullValue: "hello there",
            previousValue: "hello world", dictated: "hello world", threshold: 0.6,
            secondsSinceLastChange: 1.5, settleSeconds: 4)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == nil,
            "settle: a diverged edit quiet for LESS than settleSeconds must NOT fire — the user may still be editing")
    }

    do {
        // Does NOT fire with no divergence at all: span still equals
        // dictatedText verbatim (untouched dictation) — nothing to settle
        // from, no matter how long the field has been quiet. Guards
        // against a naive "just check the clock" implementation that
        // would otherwise fire on every untouched dictation once
        // settleSeconds elapses, well before the 90s backstop.
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello world", fullValue: "hello world",
            previousValue: "hello world", dictated: "hello world", threshold: 0.6,
            secondsSinceLastChange: 999, settleSeconds: 4)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == nil,
            "settle: an UNTOUCHED span (still equal to dictatedText) must never fire editSettled, regardless of quiet time")
    }

    do {
        // Does not preempt higher-priority condition 5 (the hard
        // backstop): elapsed >= window wins even though this tick would
        // also satisfy the settle condition.
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello there", fullValue: "hello there",
            previousValue: "hello world", dictated: "hello world", elapsed: 90, window: 90,
            threshold: 0.6, secondsSinceLastChange: 10, settleSeconds: 4)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .watchExpired,
            "settle: must not preempt the higher-priority watchExpired backstop")
    }

    do {
        // Does not preempt condition 3 (trailing newline): a bare trailing
        // newline, quiet long enough to also satisfy settle — newline
        // classification must still win (design's explicit ordering note,
        // now doubly true with our addition checked even later).
        let t = tick(
            anchorValid: true, anchoredSpanText: "hello world\n", fullValue: "hello world\n",
            previousValue: "hello world", dictated: "hello world",
            secondsSinceLastChange: 10, settleSeconds: 4)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .lineSubmitted,
            "settle: must not preempt lineSubmitted even when also quiet long enough to settle")
    }

    do {
        // Does not preempt condition 7 (edit distance exceeded): a
        // divergence far over threshold, quiet long enough to also
        // satisfy settle — the "user rewrote this into something
        // unrelated" classification must still win, since editSettled is
        // eligible for diff mining and revision_too_large explicitly
        // must not be (design §3's caveat) — misclassifying this case
        // would leak a rejected observation into the mining pipeline.
        let t = tick(
            anchorValid: true, anchoredSpanText: "something totally unrelated now",
            fullValue: "something totally unrelated now", previousValue: "hello world",
            dictated: "hello world", threshold: 0.6, secondsSinceLastChange: 10, settleSeconds: 4)
        check(
            DictationTerminalConditionEvaluator.evaluate(t) == .revisionTooLarge,
            "settle: must not preempt revisionTooLarge even when also quiet long enough to settle")
    }

    do {
        check(
            DictationTerminalCondition.editSettled.isEligibleForDiffMining,
            "eligibility: editSettled is a clean, comparable termination — anchor intact, in-budget edit")
    }

    // MARK: diff extraction — candidate phrase -> replacement mining

    do {
        let candidates = DictationCorrectionDiffExtractor.candidates(dictated: "the cloud service", edited: "the Claude service")
        check(
            candidates == [DictationCorrectionCandidate(phrase: "cloud", replacement: "Claude")],
            "diff: a single-word substitution must produce exactly one candidate pair, got \(candidates)")
    }

    do {
        // Pure insertion (nothing deleted) must NOT produce a candidate —
        // it's an addition, not a phrase -> replacement correction.
        let candidates = DictationCorrectionDiffExtractor.candidates(dictated: "hello world", edited: "hello there world")
        check(candidates.isEmpty, "diff: a pure insertion must produce no candidates, got \(candidates)")
    }

    do {
        // Pure deletion (nothing inserted) must NOT produce a candidate.
        let candidates = DictationCorrectionDiffExtractor.candidates(dictated: "hello there world", edited: "hello world")
        check(candidates.isEmpty, "diff: a pure deletion must produce no candidates, got \(candidates)")
    }

    do {
        // Identical strings: no diff at all.
        let candidates = DictationCorrectionDiffExtractor.candidates(dictated: "hello world", edited: "hello world")
        check(candidates.isEmpty, "diff: identical dictated/edited text must produce no candidates")
    }

    do {
        // Multi-word phrase substitution.
        let candidates = DictationCorrectionDiffExtractor.candidates(
            dictated: "meet me at the coffee shop", edited: "meet me at the tea house")
        check(
            candidates == [DictationCorrectionCandidate(phrase: "coffee shop", replacement: "tea house")],
            "diff: a multi-word phrase substitution must collapse into one candidate, got \(candidates)")
    }

    // MARK: terminal-condition tally — the feature's own diagnostics

    do {
        let tally = DictationTerminalConditionTally()
        check(tally.snapshot().isEmpty, "tally: a fresh tally must start empty")
        tally.record(.spanMissing)
        tally.record(.spanMissing)
        tally.record(.fieldCleared)
        let snapshot = tally.snapshot()
        check(snapshot[.spanMissing] == 2, "tally: repeated conditions must accumulate, got \(String(describing: snapshot[.spanMissing]))")
        check(snapshot[.fieldCleared] == 1, "tally: distinct conditions must be tracked separately")
    }

    // MARK: eligibility for diff mining — condition 7 must never feed mining

    do {
        check(
            !DictationTerminalCondition.revisionTooLarge.isEligibleForDiffMining,
            "eligibility: revision_too_large must never feed correction-mining (design §3's explicit caveat)")
        check(
            !DictationTerminalCondition.spanMissing.isEligibleForDiffMining,
            "eligibility: span_missing has no located text to diff against")
        check(
            !DictationTerminalCondition.spanContextLost.isEligibleForDiffMining,
            "eligibility: span_context_lost has no located text to diff against")
        check(
            !DictationTerminalCondition.nextTake.isEligibleForDiffMining,
            "eligibility: next_take is an interrupt, not a completed comparison")
        check(
            DictationTerminalCondition.lineSubmitted.isEligibleForDiffMining,
            "eligibility: line_submitted is a clean, comparable termination")
        check(
            DictationTerminalCondition.fieldCleared.isEligibleForDiffMining,
            "eligibility: field_cleared is a clean, comparable termination (edited text is confirmed empty)")
        check(
            DictationTerminalCondition.focusLeft.isEligibleForDiffMining,
            "eligibility: focus_left ends a clean observation; the span is read one last time")
        for refused in [DictationTerminalCondition.abandoned, .secureField, .otherApplication, .noFocusedField] {
            check(!refused.isEligibleForDiffMining, "eligibility: \(refused) must never feed learning")
        }
    }

    // MARK: what an end reports — only an eligible end, only an anchored span

    do {
        check(
            DictationTerminalCondition.editSettled.reportedSpan("fixed span") == "fixed span",
            "report: an eligible end with the anchor holding reports the span")
        check(
            DictationTerminalCondition.editSettled.reportedSpan(nil) == nil,
            "report: an eligible end with a broken anchor reports nothing, not the field")
        check(
            DictationTerminalCondition.abandoned.reportedSpan("fixed span") == nil,
            "report: an abandoned observation reports nothing whatever the span held")
        check(
            DictationTerminalCondition.nextTake.reportedSpan("fixed span") == nil,
            "report: an observation cut off by the next dictation reports nothing")
    }

    // MARK: every terminal condition round-trips through its rawValue

    do {
        for condition in DictationTerminalCondition.allCases {
            check(
                DictationTerminalCondition(rawValue: condition.rawValue) == condition,
                "terminal-condition: \(condition) must round-trip through its rawValue")
            check(!condition.description.isEmpty, "terminal-condition: \(condition) must have a non-empty description")
        }
        check(
            DictationTerminalCondition.allCases.count == 14,
            "terminal-condition: the eight original conditions, PLUS editSettled, "
                + "PLUS Dictation's five (focus_left, abandoned and three refusals), got "
                + "\(DictationTerminalCondition.allCases.count)")
    }

    guard failures.isEmpty else {
        let first = failures[0]
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-correction-observer-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("dictation-correction-observer-check: PASS")
    return true
}
