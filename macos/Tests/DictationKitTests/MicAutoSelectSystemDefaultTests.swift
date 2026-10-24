import XCTest
@testable import DictationAudioCapture

/// The case that left the owner unable to dictate at all on 2026-08-18.
///
/// The app was pinned to `BuiltInMicrophoneDevice` while his system default was
/// AirPods Pro. Every press captured `0 samples, peak 0.0000` and failed in
/// 13 ms, and auto-select answered `.allSilent` — the built-in rule cannot
/// fire when the built-in IS the current device, and the loudness rule refuses
/// to rank an unprobed device, which the AirPods were.
final class MicAutoSelectSystemDefaultTests: XCTestCase {

    private let builtIn = MicAutoSelect.MicCandidate(
        uid: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone",
        isBuiltIn: true, isVirtual: false)
    private let airpods = MicAutoSelect.MicCandidate(
        uid: "AirPodsPro-UID", name: "SXY's AirPods Pro",
        isBuiltIn: false, isVirtual: false)

    func testItSwitchesToTheSystemDefaultWhenPinnedToASilentBuiltIn() {
        let decision = MicAutoSelect.decide(
            candidates: [builtIn, airpods],
            currentUID: builtIn.uid,
            micIsBusy: false,
            systemDefaultUID: airpods.uid)
        XCTAssertEqual(decision, .switchTo(uid: airpods.uid, name: airpods.name))
    }

    /// Without the system default it still gives up — which is the exact
    /// behaviour that stranded him, kept here so the fix is visibly the cause
    /// of the difference rather than an incidental change.
    func testWithoutTheSystemDefaultItStillGivesUp() {
        let decision = MicAutoSelect.decide(
            candidates: [builtIn, airpods],
            currentUID: builtIn.uid,
            micIsBusy: false)
        XCTAssertEqual(decision, .allSilent)
    }

    /// The original field bug must keep its fix: capture empty on a
    /// non-built-in still lands on the built-in.
    func testTheBuiltInRuleStillWinsForABrokenExternal() {
        let decision = MicAutoSelect.decide(
            candidates: [builtIn, airpods],
            currentUID: airpods.uid,
            micIsBusy: false,
            systemDefaultUID: airpods.uid)
        XCTAssertEqual(decision, .switchTo(uid: builtIn.uid, name: builtIn.name))
    }

    /// A measured-working current device is never switched away from, whatever
    /// the system default says — the empty capture had another cause.
    func testAWorkingCurrentDeviceIsLeftAlone() {
        let working = MicAutoSelect.MicCandidate(
            uid: builtIn.uid, name: builtIn.name, isBuiltIn: true,
            isVirtual: false, ambientFloor: 0.02)
        let decision = MicAutoSelect.decide(
            candidates: [working, airpods],
            currentUID: working.uid,
            micIsBusy: false,
            systemDefaultUID: airpods.uid)
        XCTAssertEqual(decision, .keepCurrent)
    }

    /// A virtual device that happens to be the system default (Loopback,
    /// Krisp, a screen-share driver) must never be chosen — that is how a
    /// dictation ends up recording nothing at all.
    func testAVirtualSystemDefaultIsNotChosen() {
        let virtualDefault = MicAutoSelect.MicCandidate(
            uid: "loopback", name: "Loopback Audio", isBuiltIn: false, isVirtual: true)
        let decision = MicAutoSelect.decide(
            candidates: [builtIn, virtualDefault],
            currentUID: builtIn.uid,
            micIsBusy: false,
            systemDefaultUID: virtualDefault.uid)
        XCTAssertNotEqual(decision, .switchTo(uid: "loopback", name: "Loopback Audio"))
    }

    /// A default already tried and found silent must not be offered again, or
    /// the two devices ping-pong on every failed press.
    func testAnAlreadyTriedSystemDefaultIsNotRetried() {
        let decision = MicAutoSelect.decide(
            candidates: [builtIn, airpods],
            currentUID: builtIn.uid,
            micIsBusy: false,
            alreadyTried: [airpods.uid],
            systemDefaultUID: airpods.uid)
        XCTAssertEqual(decision, .allSilent)
    }

    func testABusyMicRefusesBeforeAnythingElse() {
        let decision = MicAutoSelect.decide(
            candidates: [builtIn, airpods],
            currentUID: builtIn.uid,
            micIsBusy: true,
            systemDefaultUID: airpods.uid)
        XCTAssertEqual(decision, .refusedMicBusy)
    }

    /// The default being the device we are already on is not a reason to do
    /// anything.
    func testTheSystemDefaultBeingCurrentIsNotASwitch() {
        let decision = MicAutoSelect.decide(
            candidates: [builtIn],
            currentUID: builtIn.uid,
            micIsBusy: false,
            systemDefaultUID: builtIn.uid)
        XCTAssertEqual(decision, .allSilent)
    }
}
