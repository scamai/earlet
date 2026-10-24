import XCTest
@testable import DictationAudioCapture
@testable import DictationKit

/// Spec v4 **D15** — "In-hold watchdog and 'say that again'".
///
/// The regression this defends against: when input stops delivering audio in
/// the middle of a hold, the capture layer must notice and report the correct
/// recovery path instead of silently returning an empty utterance.
///
/// These tests enter through the PRODUCT's own entry points: `armTriggerDown()`
/// runs the real `armWatchdog()` (its kill-switch read, its refusal to arm an
/// unbound capture, its grace selection, its timer task), `appendChunkForTesting`
/// routes through the same `appendLocked` the collector task calls,
/// `watchdogTick()` is the body the timer runs, and `stop()` is the shipped
/// trigger-up. Four things are injected, because they need hardware or state
/// this machine cannot give a test beside a running GUI that owns the
/// microphones: the clock, the kill switch's defaults, the bound device, the
/// device roster, and the engine half of the rebind (cool-down + `warmUp`). The
/// decision, the discard, the preview reset, the one-rebind rule, the
/// supersession token and the forensics are all the product's.
final class DictationInHoldWatchdogTests: XCTestCase {

    /// A clock a test can move by hand. The watchdog is a timing rule, and a
    /// rule tested against wall time is a rule tested against this machine's
    /// load.
    private final class TestClock: @unchecked Sendable {
        private let lock = NSLock()
        private var value: TimeInterval = 0
        var now: TimeInterval {
            lock.lock(); defer { lock.unlock() }
            return value
        }
        func advance(_ seconds: TimeInterval) {
            lock.lock(); value += seconds; lock.unlock()
        }
    }

    /// A throwaway defaults suite for the kill switch, removed on deinit — the
    /// user's real `dictation.capture.watchdog` is never touched.
    private final class TestDefaults {
        let name = "ai.scam.dictation.test.d15." + UUID().uuidString
        let defaults: UserDefaults
        init(watchdogEnabled: Bool) {
            defaults = UserDefaults(suiteName: name)!
            defaults.set(watchdogEnabled, forKey: DictationCapture.watchdogFlagKey)
        }
        deinit { UserDefaults().removePersistentDomain(forName: name) }
    }

    /// An ordered, thread-safe event log. The ORDER is the F2 guarantee: the
    /// preview run must be ended before any post-rebind audio can reach one.
    private final class EventLog: @unchecked Sendable {
        private let lock = NSLock()
        private var events: [String] = []
        func append(_ event: String) {
            lock.lock(); events.append(event); lock.unlock()
        }
        var all: [String] {
            lock.lock(); defer { lock.unlock() }
            return events
        }
        func count(of event: String) -> Int {
            lock.lock(); defer { lock.unlock() }
            return events.filter { $0 == event }.count
        }
    }

    private let oldUID = "TEST-OLD-DEVICE"
    private let newUID = "TEST-NEW-DEVICE"

    private func loudChunk(_ count: Int = 1600) -> [Float] {
        [Float](repeating: 0.5, count: count)
    }

    /// One held press, armed through the REAL `armTriggerDown()` path.
    private struct Hold {
        let capture: DictationCapture
        let clock: TestClock
        let events: EventLog
        let defaults: TestDefaults
        var rebinds: Int { events.count(of: "rebind-target") }
        var restarts: Int { events.count(of: "restart") }
    }

    private func makeHold(
        transport: String = "built-in",
        boundUID: String? = nil,
        target: String? = nil,
        rebindSucceeds: Bool = true,
        roster: [String]? = nil,
        watchdogEnabled: Bool = true,
        arm: Bool = true,
        onRestart: (@Sendable (DictationCapture) -> Void)? = nil
    ) -> Hold {
        let clock = TestClock()
        let events = EventLog()
        let testDefaults = TestDefaults(watchdogEnabled: watchdogEnabled)
        let capture = DictationCapture(
            preRollSeconds: 0.1, clock: { clock.now }, defaults: testDefaults.defaults)
        let bound = boundUID ?? oldUID
        let devices = roster ?? [oldUID]
        let answer = target ?? newUID
        capture.watchdogBoundDeviceOverrideForTesting = { (uid: bound, transport: transport) }
        capture.watchdogDeviceRosterOverrideForTesting = { devices }
        capture.onInterruptedRebindTarget = { [events] in
            events.append("rebind-target")
            return answer
        }
        capture.interruptionRestartOverrideForTesting = { [events, weak capture] _ in
            events.append("restart")
            if let capture { onRestart?(capture) }
            return rebindSucceeds
        }
        capture.onRetainedAudio = { [events] _, _ in events.append("retained-audio") }
        capture.onInterruptionDiscardedAudio = { [events] in events.append("preview-reset") }
        capture.onInterruptionRebound = { [events] in events.append("preview-restart") }
        capture.markWarmForTesting()
        if arm { capture.armTriggerDown() }
        return Hold(capture: capture, clock: clock, events: events, defaults: testDefaults)
    }

    // MARK: - The rule itself, as a pure function

    /// Every call names the whole conjunction, because W1's defect was exactly
    /// that one input was trusted alone.
    private func verdict(
        now: TimeInterval,
        lastChunkAt: TimeInterval,
        graceSeconds: Double = 2.5,
        staleFlagSince: TimeInterval? = nil,
        boundDevicePresent: Bool = true
    ) -> DictationCapture.WatchdogVerdict {
        DictationCapture.watchdogVerdict(
            now: now, holdStartedAt: 0, lastChunkAt: lastChunkAt,
            graceSeconds: graceSeconds, gapSeconds: 0.3,
            staleFlagSince: staleFlagSince,
            staleFlagHoldSeconds: DictationCapture.staleFlagHoldSeconds,
            boundDevicePresent: boundDevicePresent)
    }

    func testTheGraceGatesEveryTriggerAndTheGapIsMeasuredFromTheLastChunk() {
        // Inside the grace nothing is a fault, not even all three at once —
        // a headset's wake-up produces no chunks, can flag a format change,
        // and can drop off the device list while it renegotiates.
        XCTAssertEqual(
            verdict(now: 2.0, lastChunkAt: 0, staleFlagSince: 0, boundDevicePresent: false),
            .keepGoing)
        // Past the grace, each trigger on its own is enough.
        XCTAssertEqual(
            verdict(now: 3.0, lastChunkAt: 2.4, staleFlagSince: 2.5),
            .interrupt(reason: "stream-stale"))
        XCTAssertEqual(
            verdict(now: 3.0, lastChunkAt: 2.95, boundDevicePresent: false),
            .interrupt(reason: "device-gone"))
        XCTAssertEqual(
            verdict(now: 3.0, lastChunkAt: 2.6),
            .interrupt(reason: "no-chunks"))
        // A chunk 0.1 s ago is a live device, however long the hold has run.
        XCTAssertEqual(
            verdict(now: 9.0, lastChunkAt: 8.9, graceSeconds: 0.8),
            .keepGoing)
    }

    // MARK: - W1: the configuration-changed flag is not a verdict

    func testAConfigurationFlipThatKeepsDeliveringNeverInterrupts() {
        // THE W1 DEFECT. `MicCapture`'s `configurationChanged` is "a reason to
        // LOOK" — 13 of 26 measured XM5 binds set it on a healthy capture — and
        // on the D13 AUHAL backend a mid-hold A2DP/HFP flip keeps delivering
        // (6/6 phases in the route-flip experiments). Interrupting on the flag
        // alone discarded a good hold and cost the user a press.
        //
        // Flag up at t=1.0 and a chunk every 100 ms after it: never a fault,
        // however long the flag stands.
        for step in 0..<40 {
            let now = 1.0 + Double(step) * 0.1
            XCTAssertEqual(
                verdict(now: now + 0.05, lastChunkAt: now, graceSeconds: 0.8, staleFlagSince: 1.0),
                .keepGoing,
                "a flip that keeps delivering must never interrupt (t=\(now))")
        }
    }

    func testTheFlagInterruptsOnlyAfterItHasStoodThroughSilence() {
        let hold = DictationCapture.staleFlagHoldSeconds
        // Flag up at 1.0, last chunk at 1.0, and nothing since.
        // Not yet: the flag has not stood long enough for the 300 ms
        // re-validation window to have answered.
        XCTAssertEqual(
            verdict(now: 1.0 + hold - 0.05, lastChunkAt: 1.0, graceSeconds: 0.8,
                    staleFlagSince: 1.0),
            .interrupt(reason: "no-chunks"),
            "the GAP is ground truth and fires on its own timetable — but not as stream-stale")
        // Past the hold, with no buffer since the flag rose: now it is evidence,
        // and the reason names the flip as the proximate cause.
        XCTAssertEqual(
            verdict(now: 1.0 + hold + 0.01, lastChunkAt: 1.0, graceSeconds: 0.8,
                    staleFlagSince: 1.0),
            .interrupt(reason: "stream-stale"))
        // A buffer that arrived AFTER the flag rose disqualifies it, even long
        // after: the stream is alive, whatever the flag says.
        XCTAssertEqual(
            verdict(now: 5.0, lastChunkAt: 4.9, graceSeconds: 0.8, staleFlagSince: 1.0),
            .keepGoing)
    }

    func testTheStartupGraceIsTwoAndAHalfSecondsForBluetoothAndShortOtherwise() {
        // Read from the bundled policy when there is one, with D15's literals
        // as the fallback — either way bluetooth gets the long window, and
        // "unknown" gets the short one (an unknown transport is not a reason
        // to let a dead microphone run for 2.5 s).
        XCTAssertEqual(
            DictationCapture.startupGraceSeconds(transport: "bluetooth"),
            Double(DictationCapture.bluetoothStartupGraceMsFallback) / 1000)
        XCTAssertEqual(
            DictationCapture.startupGraceSeconds(transport: "usb"),
            Double(DictationCapture.defaultStartupGraceMsFallback) / 1000)
        XCTAssertEqual(
            DictationCapture.startupGraceSeconds(transport: "unknown"),
            DictationCapture.startupGraceSeconds(transport: "built-in"))
    }

    // MARK: - Arming: the kill switch and the refusal

    func testTheKillSwitchIsTheRegistryFlagAndOffMeansNoWatchdogAtAll() async {
        XCTAssertEqual(DictationCapture.watchdogFlagKey, "dictation.capture.watchdog",
                       "D15 rides its own flag in Dictation: the registry it shared "
                        + "a key from the shared legacy branch is not ported")
        // Dictation's default is ON; an absent key must still arm.
        let hold = makeHold(watchdogEnabled: false)
        XCTAssertFalse(hold.capture.watchdogIsArmedForTesting)

        // Everything a live hold would trip on, and nothing happens.
        hold.clock.advance(9.0)
        let finished = await hold.capture.watchdogTick()
        XCTAssertTrue(finished)
        XCTAssertEqual(hold.rebinds, 0)
        XCTAssertNil(hold.capture.lastInterruption())
        let result = await hold.capture.stop(keepWarm: true)
        XCTAssertFalse(result.interrupted)
    }

    func testArmingIsRefusedWhenNothingIsBound() async {
        // A capture with no bound device is already handled a level up
        // (`inputBindingIsStale()` treats nil as stale and the press path
        // rebuilds); a watchdog firing here would "recover" a hold that never
        // had an input by opening a microphone nobody asked for.
        let hold = makeHold(boundUID: "")
        XCTAssertFalse(hold.capture.watchdogIsArmedForTesting)
        hold.clock.advance(9.0)
        let ticked = await hold.capture.watchdogTick()
        XCTAssertTrue(ticked)
        XCTAssertEqual(hold.rebinds, 0)
    }

    func testTheRealArmingPathSelectsTheDeviceClassGraceAndStartsTheTimer() async {
        // Bluetooth, through the real arm: 2 s of silence is not yet a fault.
        let hold = makeHold(transport: "bluetooth")
        XCTAssertTrue(hold.capture.watchdogIsArmedForTesting)
        hold.clock.advance(2.0)
        let interrupted = await hold.capture.watchdogTick()
        XCTAssertFalse(interrupted)
        XCTAssertEqual(hold.rebinds, 0)
        // And `stop()` disarms the timer the arm started.
        _ = await hold.capture.stop(keepWarm: true)
        XCTAssertFalse(hold.capture.watchdogIsArmedForTesting)
    }

    // MARK: - Chunks flowing: nothing happens

    func testAHoldWhoseChunksKeepArrivingIsNeverInterrupted() async {
        let hold = makeHold()

        // Five seconds of 100 ms callbacks — six times the startup grace, and
        // every tick in between must stay silent.
        for _ in 0..<50 {
            hold.clock.advance(0.1)
            hold.capture.appendChunkForTesting(loudChunk(160))
            await hold.capture.watchdogTick()
        }

        XCTAssertEqual(hold.rebinds, 0, "a live hold must never rebind")
        XCTAssertNil(hold.capture.lastInterruption())
        XCTAssertEqual(hold.events.count(of: "preview-reset"), 0)
        let result = await hold.capture.stop(keepWarm: true)
        XCTAssertFalse(result.interrupted)
        XCTAssertEqual(result.samples.count, 50 * 160, "every chunk must reach the utterance")
        XCTAssertNil(result.forensics?.interruptedAtMs)
    }

    // MARK: - The gap

    func testAGapAfterTheGraceDiscardsThePartialHoldRebindsOnceAndFlagsTheResult() async {
        let hold = makeHold()

        // 0.5 s of real speech arrives, then the device stops.
        for _ in 0..<5 {
            hold.clock.advance(0.1)
            hold.capture.appendChunkForTesting(loudChunk())
            await hold.capture.watchdogTick()
        }
        XCTAssertEqual(hold.capture.retainedSampleCountForTesting, 5 * 1600,
                       "the pre-gap audio really was captured — the discard below is a discard, "
                        + "not an empty buffer that was never filled")
        XCTAssertEqual(hold.rebinds, 0, "still inside the grace plus a live stream")

        // Past the 800 ms grace, and 300 ms with no chunk.
        hold.clock.advance(0.5)
        await hold.capture.watchdogTick()

        XCTAssertEqual(hold.rebinds, 1, "exactly one rebind, and it asked the injected closure")
        let interruption = hold.capture.lastInterruption()
        XCTAssertEqual(interruption?.reason, "no-chunks")
        XCTAssertEqual(interruption?.previousDeviceUID, oldUID)
        XCTAssertEqual(interruption?.rebindTargetUID, newUID)
        XCTAssertEqual(interruption?.rebindSucceeded, true)
        XCTAssertEqual(interruption?.atMs ?? 0, 1000, accuracy: 1,
                       "the interruption is stamped from trigger-down, in ms")
        XCTAssertEqual(hold.capture.retainedSampleCountForTesting, 0,
                       "everything captured before the interruption is discarded")

        let result = await hold.capture.stop(keepWarm: true)
        XCTAssertTrue(result.interrupted)
        XCTAssertEqual(result.samples.count, 0, "a partial hold is never transcribed")
        XCTAssertEqual(result.interruption?.rebindTargetUID, newUID)
        // The forensics record carries D15's three fields, plus the old device
        // so the registry can be told about the right microphone.
        XCTAssertEqual(result.forensics?.interruptedAtMs ?? 0, 1000, accuracy: 1)
        XCTAssertEqual(result.forensics?.rebindTargetUID, newUID)
        XCTAssertEqual(result.forensics?.rebindSucceeded, true)
        XCTAssertEqual(result.forensics?.interruptedFromDeviceUID, oldUID)
        XCTAssertTrue(result.forensics?.summaryLine().contains("rebindTo=\(newUID)") ?? false)
    }

    /// F2: the OTHER path pre-interruption audio takes out of this class.
    func testThePreviewRunIsEndedBeforeTheRebindAndRestartedOnlyAfterIt() async {
        let hold = makeHold()

        hold.clock.advance(0.1)
        hold.capture.appendChunkForTesting(loudChunk())
        await hold.capture.watchdogTick()

        hold.clock.advance(0.9)
        await hold.capture.watchdogTick()

        hold.clock.advance(0.1)
        hold.capture.appendChunkForTesting(loudChunk())

        XCTAssertEqual(hold.events.count(of: "preview-reset"), 1,
                       "the preview run is reset exactly once per interruption")
        XCTAssertEqual(hold.events.count(of: "preview-restart"), 1,
                       "and restarted exactly once, after the rebind")
        // The ORDER is the guarantee: every chunk that fed the discarded run is
        // before the reset, the restart is after the rebind, and only then can
        // audio reach a run again.
        XCTAssertEqual(hold.events.all, [
            "retained-audio",   // the 1.0 s the user spoke into the dying device
            "preview-reset",    // ended BEFORE anything is reopened
            "rebind-target",
            "restart",
            "preview-restart",  // a fresh run, which no fragment can span
            "retained-audio",   // post-rebind audio, in the new run
        ])
    }

    func testAFailedRebindNeverStartsAFreshPreviewRun() async {
        let hold = makeHold(rebindSucceeds: false)
        hold.clock.advance(1.5)
        await hold.capture.watchdogTick()
        XCTAssertEqual(hold.events.count(of: "preview-reset"), 1)
        XCTAssertEqual(hold.events.count(of: "preview-restart"), 0,
                       "nothing was reopened, so there is nothing to preview")
    }

    func testTheBoundDeviceDisappearingInterruptsTheHold() async {
        // The roster no longer contains the device the hold is bound to: the
        // user unplugged it, or the headset dropped off the bus.
        let hold = makeHold(roster: ["SOME-OTHER-DEVICE"])

        hold.clock.advance(0.1)
        hold.capture.appendChunkForTesting(loudChunk())
        await hold.capture.watchdogTick()
        XCTAssertEqual(hold.rebinds, 0, "inside the grace a missing device is still not a fault")

        hold.clock.advance(0.8)
        await hold.capture.watchdogTick()
        XCTAssertEqual(hold.rebinds, 1)
        XCTAssertEqual(hold.capture.lastInterruption()?.reason, "device-gone")
    }

    /// F4: the roster is enumerated at most every 500 ms, so a device that
    /// vanishes is noticed on the next POLL, not on the next tick.
    func testTheDeviceRosterIsNotEnumeratedOnEveryTick() async {
        let enumerations = EventLog()
        let hold = makeHold(roster: [])
        hold.capture.watchdogDeviceRosterOverrideForTesting = { [enumerations] in
            enumerations.append("enumerate")
            return []
        }

        // 1 s of held key in 50 ms ticks, all of them past the 800 ms grace for
        // the last few. Chunks keep arriving, so only the roster could fire.
        for _ in 0..<40 {
            hold.clock.advance(0.05)
            hold.capture.appendChunkForTesting(loudChunk(160))
            await hold.capture.watchdogTick()
        }
        // 2 s of hold, 40 ticks, grace covers the first 800 ms: at 500 ms
        // spacing that is a handful of enumerations, not one per tick.
        XCTAssertLessThanOrEqual(enumerations.count(of: "enumerate"), 4,
                                 "the HAL roster must not be enumerated 20 times a second")
        XCTAssertGreaterThanOrEqual(enumerations.count(of: "enumerate"), 1,
                                    "but it must be consulted, or a vanished device is invisible")
    }

    /// The same rule through the REAL capture: the flag is set on the live
    /// `MicCapture` (via the two seams that lie about `isRunning` and
    /// `configurationChanged` without opening a device), and buffer flow
    /// decides.
    func testAFlaggedStreamThatKeepsDeliveringIsNotInterruptedButAGapIs() async {
        let hold = makeHold()
        hold.capture.markInputRunningForTesting()
        hold.capture.markInputConfigurationChangedForTesting()

        // 2 s of held key with the flag standing the whole time and chunks
        // arriving every 100 ms.
        for _ in 0..<20 {
            hold.clock.advance(0.1)
            hold.capture.appendChunkForTesting(loudChunk(160))
            await hold.capture.watchdogTick()
        }
        XCTAssertEqual(hold.rebinds, 0,
                       "a flagged-but-delivering stream is the D13 success case, not a fault")
        XCTAssertNil(hold.capture.lastInterruption())

        // Now the audio stops while the flag still stands.
        hold.clock.advance(DictationCapture.staleFlagHoldSeconds + 0.1)
        await hold.capture.watchdogTick()
        XCTAssertEqual(hold.rebinds, 1)
        XCTAssertEqual(hold.capture.lastInterruption()?.reason, "stream-stale",
                       "with the flag standing and the buffers stopped, the flip IS the cause")
    }

    // MARK: - W5: name what the rebind landed on, or name nothing

    func testAFailedRebindNamesNoDeviceRatherThanTheOneThatDied() async {
        // `MicCapture.stop()` deliberately does not clear `boundDeviceID`, so
        // reading the bound name after a failed rebind answers with the device
        // that just died — the card then said "Mic changed to <dead device> /
        // Could you say that again?", false twice over.
        let hold = makeHold(rebindSucceeds: false)
        hold.clock.advance(1.5)
        await hold.capture.watchdogTick()

        let interruption = hold.capture.lastInterruption()
        XCTAssertEqual(interruption?.rebindSucceeded, false)
        XCTAssertEqual(interruption?.deviceName, "",
                       "nothing was bound, so there is no device to name")
        XCTAssertFalse(DictationCapture.boundNameIsUsable(interruption?.deviceName ?? ""))
        // And the target it tried is still recorded, for the log and forensics.
        XCTAssertEqual(interruption?.rebindTargetUID, newUID)
    }

    func testASuccessfulRebindNamesWhateverTheEngineActuallyBound() async {
        let hold = makeHold(rebindSucceeds: true)
        hold.clock.advance(1.5)
        await hold.capture.watchdogTick()
        // In this harness no real device is opened, so the engine can only
        // answer "(unbound)" — which must still be treated as "no name", never
        // shown to the user as a device.
        let name = hold.capture.lastInterruption()?.deviceName ?? "MISSING"
        XCTAssertFalse(DictationCapture.boundNameIsUsable(name),
                       "an engine that cannot name its input must not be presented as one")
        XCTAssertTrue(DictationCapture.boundNameIsUsable("Built-in Microphone"))
    }

    // MARK: - Never twice

    func testASecondInterruptionEndsTheHoldWithoutASecondRebind() async {
        let hold = makeHold()

        hold.clock.advance(1.5)
        await hold.capture.watchdogTick()
        XCTAssertEqual(hold.rebinds, 1)
        let first = hold.capture.lastInterruption()

        // The device the rebind landed on dies too. No third open: the user is
        // already being asked to repeat themselves.
        hold.clock.advance(2.0)
        let finished = await hold.capture.watchdogTick()
        XCTAssertTrue(finished, "the watchdog is done with this press")
        XCTAssertEqual(hold.rebinds, 1, "no second rebind on the same press")
        XCTAssertEqual(hold.capture.lastInterruption()?.atMs, first?.atMs,
                       "the press keeps the FIRST interruption's stamp")

        // And the hold still ends as an interrupted one.
        let result = await hold.capture.stop(keepWarm: true)
        XCTAssertTrue(result.interrupted)
        XCTAssertEqual(result.samples.count, 0)
    }

    func testTheWatchdogIsDisarmedByStopSoATickAfterTheHoldDoesNothing() async {
        let hold = makeHold()
        hold.clock.advance(0.1)
        hold.capture.appendChunkForTesting(loudChunk())
        _ = await hold.capture.stop(keepWarm: true)

        hold.clock.advance(5.0)
        let finished = await hold.capture.watchdogTick()
        XCTAssertTrue(finished, "the watchdog's window is armTriggerDown()...stop()")
        XCTAssertNil(hold.capture.lastInterruption(),
                     "a tick after the hold must not invent an interruption")
    }

    // MARK: - The grace

    func testATwoSecondSilenceAtTheStartOfABluetoothHoldIsNotAnInterruption() async {
        // The XM5's own HFP wake-up is ~1.2 s of exact digital zeros before
        // real audio — a device property to wait through, not a fault. The
        // bluetooth class therefore gets 2.5 s before the watchdog may have an
        // opinion, and this is the test that keeps it.
        let hold = makeHold(transport: "bluetooth")

        // Two seconds, not one chunk.
        for _ in 0..<20 {
            hold.clock.advance(0.1)
            await hold.capture.watchdogTick()
        }
        XCTAssertEqual(hold.rebinds, 0, "the headset was still waking up")
        XCTAssertNil(hold.capture.lastInterruption())

        // Past 2.5 s with still nothing, it IS a fault.
        hold.clock.advance(0.6)
        await hold.capture.watchdogTick()
        XCTAssertEqual(hold.rebinds, 1)
        XCTAssertEqual(hold.capture.lastInterruption()?.reason, "no-chunks")
    }

    // MARK: - The release racing the rebind (F1)

    func testAReleaseLandingBeforeTheRestartReopensNothing() async {
        // The window that was already closed: the verdict is in, the target has
        // been resolved, and the key comes up before anything is opened.
        let hold = makeHold(onRestart: nil)
        hold.capture.onInterruptedRebindTarget = { [capture = hold.capture, events = hold.events] in
            events.append("rebind-target")
            capture.simulateReleaseForTesting()   // what `stop()` does first
            return "SOMETHING-ELSE"
        }

        hold.clock.advance(1.5)
        await hold.capture.watchdogTick()

        XCTAssertEqual(hold.restarts, 0, "no engine may be reopened after the key is released")
        XCTAssertFalse(hold.capture.watchdogIsArmedForTesting)
        // Still reported honestly: the press WAS interrupted, so the user is
        // told so rather than being shown "nothing heard".
        XCTAssertEqual(hold.capture.lastInterruption()?.rebindSucceeded, false)
        let result = await hold.capture.stop(keepWarm: true)
        XCTAssertTrue(result.interrupted)
    }

    /// THE F1 race: the release lands while the rebind is inside the restart.
    /// The rebind must publish nothing and give the input it just opened back.
    func testAReleaseLandingINSIDETheRestartPublishesNothingAndCoolsTheEngineBack() async {
        let hold = makeHold(onRestart: { capture in
            // The user lets go while `warmUp()` is running. This is exactly
            // what `stop()` does first, and it bumps the press token.
            capture.simulateReleaseForTesting()
        })

        hold.clock.advance(1.5)
        let started = Date()
        await hold.capture.watchdogTick()

        XCTAssertEqual(hold.restarts, 1, "the restart did run — this is the race, not the guard")
        // Nothing published: not retention (which would make
        // `shouldCoolDown(idleSeconds:)` refuse forever and leave the orange
        // recording indicator lit until the next press) and not the watch.
        XCTAssertFalse(hold.capture.isRetainingForTesting,
                       "a superseded rebind must not turn retention back on")
        XCTAssertFalse(hold.capture.watchdogIsArmedForTesting,
                       "a superseded rebind must not re-arm the watchdog")
        // And the engine it opened was handed straight back, so no second input
        // is alive after the hold.
        XCTAssertFalse(hold.capture.isCurrentlyWarm,
                       "the input the rebind opened must be cooled down again")
        XCTAssertFalse(hold.capture.shouldCoolDown(idleSeconds: 0),
                       "nothing is warm, so there is nothing left to release")

        // The press is still reported honestly, from the provisional record
        // written before the rebind began.
        let result = await hold.capture.stop(keepWarm: false)
        XCTAssertTrue(result.interrupted)
        XCTAssertEqual(result.samples.count, 0)
        XCTAssertLessThan(Date().timeIntervalSince(started),
                          DictationCapture.collectorDrainBoundSeconds + 1.0,
                          "trigger-up must return within the collector drain bound")
    }

    func testASupersededRebindCannotStampTheNextPress() async {
        // The pathological ordering the token exists for: the superseded
        // rebind's write lands AFTER the next press has already begun.
        let hold = makeHold(onRestart: { capture in
            capture.simulateReleaseForTesting()
            // The next press starts while this rebind is still in flight.
            capture.armTriggerDown()
        })

        hold.clock.advance(1.5)
        await hold.capture.watchdogTick()

        // The NEW press is healthy: `beginPress` cleared the old press's record
        // and the superseded rebind must not write it back. Reporting this hold
        // as `mic-changed` would tell a user their microphone changed on a press
        // where nothing happened.
        XCTAssertNil(hold.capture.lastInterruption(),
                     "a superseded rebind must not stamp the press that followed it")
        // The new press's own arm owns its state — `armTriggerDown()` set
        // retention and armed the watch, and the superseded rebind's write (had
        // it landed) would have overwritten the new press's grace window and
        // bound device with the dead press's.
        XCTAssertTrue(hold.capture.isRetainingForTesting)
        XCTAssertTrue(hold.capture.watchdogIsArmedForTesting)
        // And the superseded rebind must NOT cool the input down either: the new
        // press is holding it. Closing the microphone under a user who is
        // mid-hold would trade the wedged-retention bug for a worse one.
        XCTAssertTrue(hold.capture.isCurrentlyWarm,
                      "the input belongs to the new press now — it must stay open")
        let result = await hold.capture.stop(keepWarm: true)
        XCTAssertFalse(result.interrupted)
    }

    // MARK: - A rebind that could not reopen anything

    func testAFailedRebindIsRecordedAsSuchAndStopsTheWatch() async {
        let hold = makeHold(rebindSucceeds: false)

        hold.clock.advance(1.5)
        await hold.capture.watchdogTick()

        XCTAssertEqual(hold.rebinds, 1)
        XCTAssertEqual(hold.capture.lastInterruption()?.rebindSucceeded, false)
        XCTAssertFalse(hold.capture.watchdogIsArmedForTesting)
        let result = await hold.capture.stop(keepWarm: false)
        XCTAssertTrue(result.interrupted, "the user is still told the mic changed")
        XCTAssertEqual(result.forensics?.rebindSucceeded, false)
    }
}
