import XCTest
@testable import DictationKit

/// These tests cover two related capture invariants: a microphone engine that
/// has been replaced must not contribute its old pre-roll to the next press,
/// and a current engine must retain the short lead-in that arrives before the
/// trigger. The fixtures exercise the shipped capture calls directly without
/// relying on a device log or a recorded user session.
final class DictationCaptureStaleAudioTests: XCTestCase {

    /// 100 ms of pre-roll at 16 kHz, so the numbers below are small and the
    /// arithmetic is checkable by eye.
    private let preRollSeconds = 0.1
    private var preRollCapacity: Int { 1600 }

    private func loudChunk(_ count: Int) -> [Float] {
        [Float](repeating: 0.5, count: count)
    }

    // MARK: - Defect 1 — the pre-roll outlived the engine that filled it

    /// THE defect: audio captured by a dead engine must not be served as the
    /// next utterance.
    ///
    /// Drives the real product calls end to end — `appendChunkForTesting`
    /// routes through the same `appendLocked` the collector task uses,
    /// `renewInputForTesting` runs the same `renewMicIfSpent` that
    /// `warmUp()` runs, and `armTriggerDown()`/`stop()` are the shipped
    /// trigger-down and trigger-up. Nothing here is a restatement of the fix.
    func testAudioCapturedBeforeTheEngineDiedIsNotServedAsTheNextUtterance() async {
        let capture = DictationCapture(preRollSeconds: preRollSeconds)
        capture.markWarmForTesting()

        // A live engine fills the ring with the last 100 ms before the press.
        capture.appendChunkForTesting(loudChunk(preRollCapacity))
        XCTAssertEqual(capture.preRollSampleCountForTesting, preRollCapacity,
                       "the ring did not fill — the rest of this test proves nothing")

        // The device goes away and the engine is rebuilt: the 30 s idle mic
        // release, the 180 s transcriber unload, an unplugged headset, or the
        // bind failure in the log above. All of them land here.
        capture.markInputSpentForTesting()
        XCTAssertTrue(capture.renewInputForTesting(),
                      "the engine was not actually replaced")

        // The next press. On a new engine that has not delivered a single
        // buffer yet, this must produce NOTHING.
        capture.armTriggerDown()
        let result = await capture.stop(keepWarm: true)

        XCTAssertEqual(result.samples.count, 0,
                       "the utterance was \(result.samples.count) samples of audio "
                       + "recorded by an engine that is already gone — the shipped "
                       + "samples from an engine that is already gone")
        XCTAssertEqual(capture.lastCapturePeak, 0,
                       "stale audio reached the silence gate, which tests the "
                       + "clip's peak — loud enough and it is transcribed and pasted")
    }

    /// A stale ring must not be replayed across repeated presses.
    func testTheStaleRingIsNotReplayedPressAfterPress() async {
        let capture = DictationCapture(preRollSeconds: preRollSeconds)
        capture.markWarmForTesting()
        capture.appendChunkForTesting(loudChunk(preRollCapacity))
        capture.markInputSpentForTesting()
        capture.renewInputForTesting()

        for press in 1...3 {
            capture.armTriggerDown()
            let result = await capture.stop(keepWarm: true)
            XCTAssertEqual(result.samples.count, 0,
                           "press \(press) was answered with \(result.samples.count) "
                           + "samples from before the engine died")
        }
    }

    /// The fix must not be "always clear the ring". Pre-roll exists because a
    /// speaker's mouth beats their finger by 50-150 ms, and losing it silently
    /// eats the first syllable of every dictation — a regression nothing else
    /// in this suite would catch.
    func testPreRollFromTheLIVEEngineStillReachesTheUtterance() async {
        let capture = DictationCapture(preRollSeconds: preRollSeconds)
        capture.markWarmForTesting()

        // No engine replacement: one engine, still running, ring current.
        capture.appendChunkForTesting(loudChunk(preRollCapacity))
        capture.armTriggerDown()
        // ...and the speech that arrives after the key registers.
        capture.appendChunkForTesting(loudChunk(800))
        let result = await capture.stop(keepWarm: true)

        XCTAssertEqual(result.samples.count, preRollCapacity + 800,
                       "the lead-in the user spoke before the key registered was dropped")
    }

    // MARK: - Defect 2 — "warm" survived an engine that never started

    /// `warmUp()` sets `isWarm` before it knows the engine will open — it has
    /// to, it is the re-entrancy guard. It used to leave it set when
    /// `MicCapture.start()` threw, and that single stuck flag is what turned
    /// one honest `noAudio` into a run of 7 ms `nothingHeard`s: the
    /// controller's trigger-time rebuild is gated on `isCurrentlyWarm &&
    /// isInputStaleWhileWarm`, and an engine that never started reports "not
    /// stale" (`MicCapture.bindingIsStale` opens with `guard isRunning`), so
    /// neither the rebuild nor the `if !isCurrentlyWarm { warmUp() }` retry
    /// one line below could ever fire again.
    func testAnEngineThatFailedToStartDoesNotLeaveCaptureClaimingToBeWarm() {
        let capture = DictationCapture(preRollSeconds: preRollSeconds)
        capture.markWarmForTesting()   // the optimistic flag warmUp() sets first

        capture.abandonFailedStartForTesting()   // the real unwind, on the real body

        XCTAssertFalse(capture.isCurrentlyWarm,
                       "capture still claims to be warm with no engine running — "
                       + "DictationController.begin() will skip both the rebind and "
                       + "the retry on every press from here on")
    }

    /// Clearing the flag is not enough on its own: a `start()` that timed out
    /// inside CoreAudio (the shipped failure was a 5 s
    /// `MicCapture.runWithTimeout` bound) leaves an engine whose real state
    /// nobody knows. The next press must build a fresh one, not reuse it.
    func testAFailedStartForcesAFreshEngineOnTheNextPress() {
        let capture = DictationCapture(preRollSeconds: preRollSeconds)
        capture.markWarmForTesting()

        capture.abandonFailedStartForTesting()

        XCTAssertTrue(capture.renewInputForTesting(),
                      "the engine whose start() threw was reused — a bad bind "
                      + "persists across every later trigger")
    }

    /// And the two defects meet here: a failed start must not leave the ring
    /// standing either, or the very next press is answered from it instantly.
    func testAFailedStartLeavesNoStaleAudioBehind() async {
        let capture = DictationCapture(preRollSeconds: preRollSeconds)
        capture.markWarmForTesting()
        capture.appendChunkForTesting(loudChunk(preRollCapacity))

        capture.abandonFailedStartForTesting()

        XCTAssertEqual(capture.preRollSampleCountForTesting, 0,
                       "the ring survived the failed start")
        capture.armTriggerDown()
        let result = await capture.stop(keepWarm: true)
        XCTAssertEqual(result.samples.count, 0,
                       "the press after a failed start was answered with "
                       + "\(result.samples.count) stale samples in ~7 ms")
    }
}
