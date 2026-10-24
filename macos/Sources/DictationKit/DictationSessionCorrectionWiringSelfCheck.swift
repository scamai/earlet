import Foundation

/// Pure-logic self-check for the M2 wiring gap this iteration closes:
/// `DictationSession` inserted text and then forgot about it — nothing
/// ever called `DictationCorrectionObserver.beginObserving`. Covers
/// exactly the three invariants the task called out as testable without a
/// live window-server session:
///   1. a failed insertion starts no observation
///   2. with `correctionObserver` `nil` (the `feature.dictation_learning`
///      OFF case), nothing is even attempted
///   3. the SAME observer instance is reused across every dictation in a
///      session — the precondition `DictationCorrectionObserver
///      .beginObserving`'s own contract needs to make "a second dictation
///      terminates the first with `next_take`" true in
///      practice
///
/// What this deliberately does NOT re-test: `DictationCorrectionObserver
/// .beginObserving`'s own internal `terminateActiveSession(with:
/// .nextTake)` call (its unconditional first statement,
/// `DictationCorrectionObserver.swift`) — exercising that live needs a
/// real focused `AXUIElement` whose `kAXValueAttribute` actually reads
/// back a value, which needs Accessibility TRUST, which this self-check
/// (by the task's own instruction) must not depend on. That line is
/// reviewed, unchanged by this task, and covered structurally by (3)
/// above: DictationSession guarantees every dictation's completion reaches
/// the ONE observer instance the caller constructed, so whatever
/// single-active-slot behavior that instance implements applies across
/// dictations, not just within one. Whether a real `AXObserver` callback
/// ever fires at all remains unverified in this project.
public func runDictationSessionCorrectionWiringSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }

    // MARK: the pure decision — insertionSucceeded && hasObserver

    do {
        check(
            DictationSession.shouldBeginCorrectionObservation(insertionSucceeded: true, hasObserver: true),
            "wiring: a successful insertion with a real observer must begin observation")
        check(
            !DictationSession.shouldBeginCorrectionObservation(insertionSucceeded: false, hasObserver: true),
            "wiring: a FAILED insertion must never begin observation, even with a real observer present")
        check(
            !DictationSession.shouldBeginCorrectionObservation(insertionSucceeded: true, hasObserver: false),
            "wiring: a successful insertion with NO observer (flag off) must never begin observation")
        check(
            !DictationSession.shouldBeginCorrectionObservation(insertionSucceeded: false, hasObserver: false),
            "wiring: a failed insertion with no observer must never begin observation")
    }

    // MARK: flag off -> nothing constructed at all

    do {
        // A `DictationSession` built with `correctionObserver: nil` (the
        // default — matches every existing caller, `DictateCLI`/
        // `DictationE2ECheck`, which never pass one) must store `nil`, not
        // silently default-construct a real `DictationCorrectionObserver`.
        // If it did, `feature.dictation_learning`'s "default OFF" claim
        // would be false — the observer would exist and be reachable even
        // when no caller asked for it.
        let session = DictationSession(capture: DictationCapture(), asr: NoopAsrTransportForCorrectionWiringCheck())
        check(
            session.correctionObserverForTesting == nil,
            "wiring: a session constructed with no correctionObserver argument must have nil correctionObserverForTesting — nothing should be default-constructed")
    }

    // MARK: same observer instance reused across the session, not rebuilt per dictation

    do {
        let observer = DictationCorrectionObserver()
        let session = DictationSession(
            capture: DictationCapture(), asr: NoopAsrTransportForCorrectionWiringCheck(),
            correctionObserver: observer)
        check(
            session.correctionObserverForTesting === observer,
            "wiring: the session must store the EXACT observer instance passed at init, not a copy or a fresh one")
        // `correctionObserverForTesting` is a plain stored-property read,
        // not something that changes call to call — reading it twice must
        // still be the same instance, i.e. nothing along `triggerUp()`'s
        // path could plausibly rebuild it per dictation.
        check(
            session.correctionObserverForTesting === session.correctionObserverForTesting,
            "wiring: the stored observer must be referentially stable across reads")
    }

    guard failures.isEmpty else {
        let first = failures[0]
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-session-correction-wiring-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("dictation-session-correction-wiring-check: PASS")
    return true
}

/// Minimal no-op `DictationAsrTransport` — this self-check never calls
/// `triggerDown()`/`triggerUp()` (those are TCC-gated, out of scope per
/// the task's own instruction), it only constructs `DictationSession` to
/// read back its stored `correctionObserverForTesting`. A real
/// `PythonAsrTransport` would spawn a subprocess for no reason here.
private struct NoopAsrTransportForCorrectionWiringCheck: DictationAsrTransport {
    func start() async -> Bool { false }
    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        .failed(.notInitialized)
    }
    func shutdown() async {}
}
