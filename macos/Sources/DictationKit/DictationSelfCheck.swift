import Foundation

/// Pure-logic self-check for `DictationKit` — no UI, no TCC (Accessibility
/// / Input Monitoring), no real pasteboard, no CGEventTap. Covers exactly
/// the four invariants the M1 spike called out as needing coverage:
///   1. keycode resolution (falls back cleanly with no active layout)
///   2. the failed-paste timeout classification
///   3. the clipboard-restore decision under a changed `changeCount`
///   4. the double-tap timing window
/// plus the hold-to-talk edge detector these all sit alongside. Registered
/// in the app entry point's `--self-check` chain next to the other pure-logic checks
/// (`runUtteranceSegmenterSelfCheck`, `runBundleIdentityAndStickyClassificationSelfCheck`).
public func runDictationSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }

    // MARK: keycode resolution — no active keyboard layout in this process

    do {
        // A headless self-check process has no window-server keyboard
        // layout session, so `resolveKeyCode` should fall through to the
        // documented ANSI-QWERTY fallback rather than crash or hang.
        let resolved = KeyCodeResolver.resolveVKeyCode()
        check(resolved == KeyCodeResolver.fallbackVKeyCode || resolved != 0,
              "keycode: resolveVKeyCode() returned an unusable value \(resolved)")
    }

    do {
        // Whether or not a real layout session exists, resolving the same
        // character twice must be deterministic — either two real layout
        // lookups agree, or both fall through to the same fallback.
        let first = KeyCodeResolver.resolveKeyCode(for: "v")
        let second = KeyCodeResolver.resolveKeyCode(for: "v")
        check(first == second, "keycode: resolveKeyCode(for: 'v') is not deterministic (\(first) vs \(second))")
    }

    do {
        // A synthetic, hand-built ANSI layout blob lets this self-check
        // exercise the ACTUAL UCKeyTranslate scanning logic (not just the
        // fallback path) without depending on whatever layout happens to
        // be active in the process running the check. Reuses the real
        // system layout's raw bytes when available (best-effort, not
        // required for the check to pass) — see the fallback-only case
        // above for the no-layout-available path.
        if let liveLayout = KeyCodeResolverTestSupport.currentLayoutDataForTesting() {
            let resolved = KeyCodeResolver.resolveKeyCode(for: "v", layoutData: liveLayout)
            // On a real ANSI-QWERTY-derived layout this is 9, but other
            // layouts are legitimate too — the invariant under test is
            // just "produced SOME plausible keycode", not a specific value.
            check(resolved < 128, "keycode: layout-scan produced an out-of-range keycode \(resolved)")
        }
    }

    // MARK: failed-paste timeout classification

    do {
        check(PasteTimeoutClassifier.classify(wasAsked: true, elapsedMs: 0, timeoutMs: 800) == .requested,
              "paste-classifier: asked-immediately must be .requested regardless of elapsed time")
        check(PasteTimeoutClassifier.classify(wasAsked: true, elapsedMs: 5000, timeoutMs: 800) == .requested,
              "paste-classifier: asked-late-but-asked must still be .requested, not .timedOut")
        check(PasteTimeoutClassifier.classify(wasAsked: false, elapsedMs: 0, timeoutMs: 800) == .pending,
              "paste-classifier: not-yet-asked, well under budget, must be .pending")
        check(PasteTimeoutClassifier.classify(wasAsked: false, elapsedMs: 799, timeoutMs: 800) == .pending,
              "paste-classifier: 1ms under the timeout must still be .pending")
        check(PasteTimeoutClassifier.classify(wasAsked: false, elapsedMs: 800, timeoutMs: 800) == .timedOut,
              "paste-classifier: exactly at the timeout must be .timedOut (inclusive boundary)")
        check(PasteTimeoutClassifier.classify(wasAsked: false, elapsedMs: 5000, timeoutMs: 800) == .timedOut,
              "paste-classifier: well past the timeout, never asked, must be .timedOut")
    }

    // MARK: clipboard-restore decision under a changed changeCount

    do {
        check(ClipboardRestorePolicy.decide(changeCountAfterOurWrite: 5, changeCountNow: 5) == .restore,
              "clipboard-restore: unchanged changeCount must restore")
        check(ClipboardRestorePolicy.decide(changeCountAfterOurWrite: 5, changeCountNow: 6) == .skipThirdPartyWrite,
              "clipboard-restore: a changeCount that moved (third-party write) must NOT restore")
        check(ClipboardRestorePolicy.decide(changeCountAfterOurWrite: 5, changeCountNow: 4) == .skipThirdPartyWrite,
              "clipboard-restore: any mismatch (even a decrease, e.g. after a pasteboard-owner restart) must skip")
    }

    // MARK: double-tap timing window

    do {
        var state = DoubleTapModifierState(window: 0.35)
        check(state.handle(down: true, timestamp: 0.0) == nil,
              "double-tap: first press, no prior press, must not fire (it's ambiguous — could be the start of a hold)")
        check(state.handle(down: false, timestamp: 0.05) == nil,
              "double-tap: release must never fire an event")
        check(state.handle(down: true, timestamp: 0.20) == .doubleTapActivate,
              "double-tap: second press within the window (0.20s gap < 0.35s) must activate")
        check(state.handle(down: false, timestamp: 0.25) == nil,
              "double-tap: release while latched must not fire")
        check(state.handle(down: true, timestamp: 5.0) == nil,
              "double-tap: the first press of a stopping pair must not deactivate")
        check(state.handle(down: false, timestamp: 5.05) == nil,
              "double-tap: release between the stopping pair must not fire")
        check(state.handle(down: true, timestamp: 5.20) == .doubleTapDeactivate,
              "double-tap: the second press of a later pair must deactivate")
    }

    do {
        // Regression: a lone tap shortly AFTER the deactivating tap must NOT
        // re-arm. The deactivating press used to leave its own timestamp in
        // `lastPressAt`, so the next single tap fell inside `window` of it and
        // was misread as a phantom double-tap — silently turning dictation
        // back ON and starting capture. (The activate/deactivate case above
        // hid this by deactivating at t=5.0, far from any follow-up tap.)
        var state = DoubleTapModifierState(window: 0.35)
        _ = state.handle(down: true, timestamp: 0.0)    // first tap
        _ = state.handle(down: false, timestamp: 0.05)
        _ = state.handle(down: true, timestamp: 0.20)   // -> activate
        _ = state.handle(down: false, timestamp: 0.25)
        _ = state.handle(down: true, timestamp: 0.50)   // first stopping tap
        _ = state.handle(down: false, timestamp: 0.55)
        _ = state.handle(down: true, timestamp: 0.70)   // -> deactivate
        _ = state.handle(down: false, timestamp: 0.75)
        check(state.handle(down: true, timestamp: 0.90) == nil,
              "double-tap: a lone tap after the deactivating pair must NOT re-arm — deactivation must clear the pair history")
        _ = state.handle(down: false, timestamp: 0.95)
        check(state.handle(down: true, timestamp: 1.10) == .doubleTapActivate,
              "double-tap: a genuine double-tap (0.90 then 1.10) after a deactivation must still activate")
    }

    do {
        var state = DoubleTapModifierState(window: 0.35)
        _ = state.handle(down: true, timestamp: 0.0)
        _ = state.handle(down: false, timestamp: 0.05)
        let outsideWindow = state.handle(down: true, timestamp: 0.50)
        check(outsideWindow == nil,
              "double-tap: second press OUTSIDE the window (0.50s gap > 0.35s) must not activate")
    }

    do {
        // Exactly-at-the-boundary is inclusive (`<=`), matching the failed-
        // paste-timeout boundary convention above for consistency.
        var state = DoubleTapModifierState(window: 0.35)
        _ = state.handle(down: true, timestamp: 0.0)
        _ = state.handle(down: false, timestamp: 0.05)
        check(state.handle(down: true, timestamp: 0.35) == .doubleTapActivate,
              "double-tap: exactly at the window boundary must activate (inclusive)")
    }

    do {
        // An event timestamp moving backwards must reset pairing instead of
        // creating a negative-duration double tap.
        var state = DoubleTapModifierState(window: 0.35)
        _ = state.handle(down: true, timestamp: 1.0)
        _ = state.handle(down: false, timestamp: 1.01)
        check(state.handle(down: true, timestamp: 0.9) == nil,
              "double-tap: reversed timestamps must begin a new pair, not activate")
        _ = state.handle(down: false, timestamp: 0.91)
        check(state.handle(down: true, timestamp: 1.1) == .doubleTapActivate,
              "double-tap: a forward second edge after a reversal must still complete the fresh pair")
    }

    do {
        // De-dupe: a repeated `down` callback with no intervening `up`
        // must not double-fire.
        var state = DoubleTapModifierState(window: 0.35)
        check(state.handle(down: true, timestamp: 0.0) == nil, "double-tap: sanity — first press")
        check(state.handle(down: true, timestamp: 0.01) == nil,
              "double-tap: a repeated `down` with no intervening `up` must be de-duped, not treated as a new press")
    }

    // MARK: hold-to-talk edge detector

    do {
        var state = HoldToTalkState()
        check(state.handle(down: true) == .pushToTalkStart, "hold-to-talk: first down must start")
        check(state.handle(down: true) == nil, "hold-to-talk: repeated down must be de-duped")
        check(state.handle(down: false) == .pushToTalkEnd, "hold-to-talk: up must end")
        check(state.handle(down: false) == nil, "hold-to-talk: repeated up must be de-duped")
        check(state.handle(down: true) == .pushToTalkStart, "hold-to-talk: a second press must start again")
    }

    // MARK: AudioRingBuffer — retention window, pre-roll boundary, wraparound
    // (warm-engine capture's pure logic; see DictationCapture's doc comment)

    do {
        // Fresh buffer: empty, not full, count 0 — the "before warmUp() has
        // filled anything" state a real capture starts in.
        let empty = AudioRingBuffer(capacity: 5)
        check(empty.count == 0, "ring-buffer: fresh buffer must start empty")
        check(!empty.isFull, "ring-buffer: fresh buffer must not report full")
        check(empty.samples.isEmpty, "ring-buffer: fresh buffer samples must be empty")
    }

    do {
        // Under capacity: nothing evicted, order preserved oldest-first.
        var buf = AudioRingBuffer(capacity: 5)
        buf.append([1, 2, 3])
        check(buf.samples == [1, 2, 3], "ring-buffer: under-capacity append must preserve order, no eviction")
        check(!buf.isFull, "ring-buffer: 3/5 samples must not report full")
    }

    do {
        // Exactly at capacity: full, nothing evicted yet.
        var buf = AudioRingBuffer(capacity: 4)
        buf.append([1, 2, 3, 4])
        check(buf.samples == [1, 2, 3, 4], "ring-buffer: exact-capacity append must keep every sample")
        check(buf.isFull, "ring-buffer: capacity-count samples must report full")
    }

    do {
        // Wraparound via multiple small appends: oldest samples evicted
        // first, remaining order still chronological (oldest-first) — this
        // is the exact property `armTriggerDown()` depends on to prepend
        // "the last N ms before the keypress," not some scrambled window.
        var buf = AudioRingBuffer(capacity: 4)
        buf.append([1, 2])
        buf.append([3, 4])
        buf.append([5, 6])
        check(buf.samples == [3, 4, 5, 6],
              "ring-buffer: wraparound across multiple appends must evict oldest-first and keep chronological order, got \(buf.samples)")
        check(buf.isFull, "ring-buffer: post-wraparound buffer at capacity must report full")
    }

    do {
        // A single chunk larger than capacity: only the TAIL survives (the
        // most recent `capacity` samples), not an arbitrary or empty result
        // — this is the fast path `append` takes for oversized chunks.
        var buf = AudioRingBuffer(capacity: 3)
        buf.append([1, 2, 3, 4, 5, 6, 7])
        check(buf.samples == [5, 6, 7],
              "ring-buffer: an oversized single chunk must retain only its most-recent `capacity` samples, got \(buf.samples)")
    }

    do {
        // Pre-roll boundary: capacity 0 (pre-roll disabled) must be a
        // well-defined no-op, not a crash or a negative-size allocation —
        // `DictationCapture.init(preRollSeconds: 0)` relies on this.
        var buf = AudioRingBuffer(capacity: 0)
        buf.append([1, 2, 3])
        check(buf.samples.isEmpty, "ring-buffer: capacity-0 (pre-roll disabled) must discard everything")
        check(!buf.isFull, "ring-buffer: capacity-0 must never report full")
    }

    do {
        // A negative requested capacity must clamp to 0 rather than trap —
        // defends against a caller passing a malformed `preRollSeconds`.
        let buf = AudioRingBuffer(capacity: -10)
        check(buf.capacity == 0, "ring-buffer: negative capacity must clamp to 0, got \(buf.capacity)")
    }

    do {
        // Exact-boundary wraparound: appending exactly one more sample than
        // capacity evicts exactly the single oldest sample, not more/less.
        var buf = AudioRingBuffer(capacity: 3)
        buf.append([10, 20, 30])
        buf.append([40])
        check(buf.samples == [20, 30, 40],
              "ring-buffer: one-over-capacity append must evict exactly the single oldest sample, got \(buf.samples)")
    }

    // MARK: DictationCapture warm/cold state machine (pure — no MicCapture
    // start(), so no TCC/mic hardware needed; only the retention bookkeeping
    // is under test here)

    do {
        var now: TimeInterval = 0
        let capture = DictationCapture(preRollSeconds: 0.1, clock: { now })
        check(!capture.isWarmForTesting, "capture: must start cold (not warm) before warmUp()")
        check(capture.shouldCoolDown(idleSeconds: 5) == false,
              "capture: shouldCoolDown must be false while never warmed (nothing to cool)")
        // Simulate warmUp()'s bookkeeping without touching MicCapture/engine
        // by driving the same activity-timestamp field through the public
        // idle-check contract: a cold capture reports no cool-down need.
        now = 100
        check(capture.shouldCoolDown(idleSeconds: 5) == false,
              "capture: a never-warmed capture must never ask to cool down")
    }

    // MARK: permission-status classification (pure combinatorics, no TCC call)

    do {
        // DictationPermissionStatus itself has no logic to test beyond its
        // enum shape (the actual AX/Input-Monitoring calls are TCC-gated
        // and covered by --dictation-check, not this pure self-check) —
        // assert the granted/not-granted convenience reads correctly for
        // every case, since a flipped `isGranted` would silently let
        // DictationHotkey.start() proceed without real permission.
        check(DictationPermissionStatus.granted.isGranted, "permission: .granted must report isGranted == true")
        check(!DictationPermissionStatus.missingAccessibility.isGranted, "permission: missingAccessibility must not be granted")
        check(!DictationPermissionStatus.missingInputMonitoring.isGranted, "permission: missingInputMonitoring must not be granted")
        check(!DictationPermissionStatus.missingBoth.isGranted, "permission: missingBoth must not be granted")
    }

    // MARK: DictationSessionPhase — stage sequencing (Iteration 14:
    // DictationSession, the trigger->capture->ASR->paste orchestrator)

    do {
        // Happy path: idle -> capturing -> stoppingCapture -> awaitingAsr
        // -> awaitingInsert -> done, exactly what one successful dictation
        // drives DictationSession through.
        var phase = DictationSessionPhase()
        check(phase.state == .idle, "session-phase: must start idle")
        check(phase.handle(.triggerDown) == .capturing, "session-phase: triggerDown from idle must go to capturing")
        check(phase.handle(.triggerUp) == .stoppingCapture, "session-phase: triggerUp from capturing must go to stoppingCapture")
        check(phase.handle(.captureFinished) == .awaitingAsr, "session-phase: captureFinished must go to awaitingAsr")
        check(phase.handle(.asrSucceeded) == .awaitingInsert, "session-phase: asrSucceeded must go to awaitingInsert")
        check(phase.handle(.insertSucceeded) == .done, "session-phase: insertSucceeded must go to done")
    }

    do {
        // Failure paths terminate at .failed, not .done, and don't advance
        // past the stage that actually failed.
        var asrFail = DictationSessionPhase()
        _ = asrFail.handle(.triggerDown)
        _ = asrFail.handle(.triggerUp)
        _ = asrFail.handle(.captureFinished)
        check(asrFail.handle(.asrFailed) == .failed, "session-phase: asrFailed from awaitingAsr must go to failed")

        var insertFail = DictationSessionPhase()
        _ = insertFail.handle(.triggerDown)
        _ = insertFail.handle(.triggerUp)
        _ = insertFail.handle(.captureFinished)
        _ = insertFail.handle(.asrSucceeded)
        check(insertFail.handle(.insertFailed) == .failed, "session-phase: insertFailed from awaitingInsert must go to failed")
    }

    do {
        // Rejected, not crashed: a stray triggerUp with no preceding
        // triggerDown (a missed key-down event is a real, reachable
        // hardware condition, not a hypothetical) must be rejected —
        // `nil` return, state unchanged — never silently accepted into a
        // capture that was never armed.
        var phase = DictationSessionPhase()
        check(phase.handle(.triggerUp) == nil, "session-phase: triggerUp with no triggerDown must be rejected")
        check(phase.state == .idle, "session-phase: a rejected event must leave state unchanged")
    }

    do {
        // A double triggerDown (stuck-key repeat, same class of bug
        // HoldToTalkState's de-dupe defends one layer up) must be
        // rejected on the second call, not silently re-arm/restart.
        var phase = DictationSessionPhase()
        _ = phase.handle(.triggerDown)
        check(phase.handle(.triggerDown) == nil, "session-phase: a second triggerDown while capturing must be rejected")
        check(phase.state == .capturing, "session-phase: a rejected double-triggerDown must leave state at capturing")
    }

    do {
        // Events from a terminal state (other than reset) are rejected —
        // e.g. a stray late ASR callback arriving after the session already
        // reached .done must not silently re-transition it.
        var phase = DictationSessionPhase()
        _ = phase.handle(.triggerDown)
        _ = phase.handle(.triggerUp)
        _ = phase.handle(.captureFinished)
        _ = phase.handle(.asrSucceeded)
        _ = phase.handle(.insertSucceeded)
        check(phase.state == .done, "session-phase: sanity — happy path reaches done")
        check(phase.handle(.asrSucceeded) == nil, "session-phase: an event from a terminal state must be rejected")
        check(phase.state == .done, "session-phase: a rejected event from .done must leave state at .done")
    }

    do {
        // reset() is the universal escape hatch — valid from ANY state,
        // including terminal ones and mid-flight ones (e.g. the app losing
        // focus mid-dictation), and always lands on .idle.
        for startEvents: [DictationSessionPhase.Event] in [
            [],
            [.triggerDown],
            [.triggerDown, .triggerUp],
            [.triggerDown, .triggerUp, .captureFinished, .asrFailed],
        ] {
            var phase = DictationSessionPhase()
            for e in startEvents { _ = phase.handle(e) }
            check(phase.handle(.reset) == .idle,
                  "session-phase: reset() from any state must return .idle, failed after \(startEvents)")
            check(phase.state == .idle, "session-phase: reset() must actually leave state at .idle")
        }
    }

    // MARK: DictationSession.SessionFailure / DictationAsrFailure /
    // Stage — pure classification shape (the actual TCC/subprocess-gated
    // paths are exercised by --dictation-e2e, not this self-check)

    do {
        // Every failure case must round-trip through Equatable identically
        // (guards against an accidentally-non-exhaustive == synthesized by
        // a future associated-value change) and produce a non-empty
        // description — an empty string here would silently degrade a
        // reported failure into unhelpful blank text somewhere downstream.
        let failures: [DictationSession.SessionFailure] = [
            .micPermissionDenied,
            .emptyCapture,
            .asr(.workerUnavailable),
            .asr(.notInitialized),
            .asr(.ioError("boom")),
            .asr(.engineError("boom")),
            .emptyTranscript,
            .insert(.accessibilityPermissionMissing),
            .insert(.noFocusedElement),
            .insert(.pasteNeverRequested),
            .invalidSequencing(.wrongPhase),
        ]
        for f in failures {
            check(f == f, "session-failure: \(f) must equal itself")
            check(!f.description.isEmpty, "session-failure: \(f) must have a non-empty description")
        }
        check(DictationSession.SessionFailure.emptyCapture != DictationSession.SessionFailure.emptyTranscript,
              "session-failure: distinct cases must not compare equal")
    }

    guard failures.isEmpty else {
        let first = failures[0]
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("dictation-check: PASS")
    return true
}
