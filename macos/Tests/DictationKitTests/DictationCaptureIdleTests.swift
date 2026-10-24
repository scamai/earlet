import XCTest
@testable import DictationKit

/// The idle-release guard, and the outage it caused.
///
/// On 2026-08-17 `idleMicrophoneReleaseSeconds` was lowered from 15 s to 5 s.
/// `shouldCoolDown` checked only "warm" and "time since lastActivity", and
/// `lastActivity` is stamped at trigger-DOWN and not again while audio
/// arrives — so a held trigger was indistinguishable from an idle engine, and
/// the housekeeping tick closed the microphone while the user was still
/// speaking. Reported the same day as "it records for five, six, seven, eight
/// seconds and then it just stops".
final class DictationCaptureIdleTests: XCTestCase {

    func testAMicrophoneThatIsRecordingIsNeverReleased() {
        let capture = DictationCapture()
        capture.armTriggerDownForTesting()
        XCTAssertFalse(capture.shouldCoolDown(idleSeconds: 0),
                       "released the microphone mid-utterance")
    }

    /// The guard must not disable idle release entirely — that would hold a
    /// Bluetooth listener open forever, which is what the 5 s value exists to
    /// prevent.
    func testAnIdleWarmMicrophoneIsStillReleased() {
        let capture = DictationCapture()
        capture.markWarmForTesting()
        XCTAssertTrue(capture.shouldCoolDown(idleSeconds: 0),
                      "idle release stopped working")
    }

    func testAColdMicrophoneIsNeverReleased() {
        XCTAssertFalse(DictationCapture().shouldCoolDown(idleSeconds: 0))
    }
}
