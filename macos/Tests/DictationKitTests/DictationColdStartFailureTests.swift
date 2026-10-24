// What a cold start is allowed to blame the user for.
//
// `DictationSession.triggerDown()` falls back to `capture.start()` when the
// engine is not already warm, and that call throws for several unrelated
// reasons. It used to answer EVERY one of them with `.micPermissionDenied`,
// on the reasoning that a cold-start failure is "almost always mic-permission
// related".
//
// `micPermissionDenied` is the one classification that means "the user must
// go and grant something". Spending it on a device error files a recoverable
// fault as a consent problem — the same mistake, in the same direction, as
// telling a user their microphone is broken while meeting notes holds it.
// Measured on this Mac 2026-08-20: six failures logged as
// `noAudio (capture did not start)` on a machine with 106 successful
// insertions, so the grant was plainly in place and all six were mislabelled.

#if canImport(Testing)
import Foundation
import Testing
import DictationAudioCapture
@testable import DictationKit

/// A capture whose cold start throws whatever the test asks it to.
private final class ThrowingAudioSource: DictationAudioSource, @unchecked Sendable {
    let error: Error
    private(set) var startAttempts = 0

    init(error: Error) { self.error = error }

    // Always cold, so `triggerDown()` takes the `start()` path under test
    // rather than the warm `armTriggerDown()` one.
    var isCurrentlyWarm: Bool { false }

    func start() throws {
        startAttempts += 1
        throw error
    }

    func armTriggerDown() {}

    func stop(keepWarm: Bool) async -> DictationCapture.CaptureResult {
        DictationCapture.CaptureResult(samples: [], sampleRate: 16_000)
    }
}

/// Never reached by these tests — `triggerDown()` fails before ASR exists.
private struct UnusedAsr: DictationAsrTransport {
    func start() async -> Bool { true }
    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        .failed(.workerUnavailable)
    }
    func shutdown() async {}
}

@Suite("A cold start only blames consent when consent is the problem")
struct DictationColdStartFailureTests {

    private static func session(throwing error: Error)
    -> (DictationSession, ThrowingAudioSource) {
        let capture = ThrowingAudioSource(error: error)
        return (DictationSession(capture: capture, asr: UnusedAsr()), capture)
    }

    @Test("a device error is not reported as a missing permission")
    func deviceErrorIsNotConsent() {
        // The real shape of the six mislabelled failures: the engine refused
        // to start on a machine whose microphone grant was in place.
        let (s, capture) = Self.session(
            throwing: MicCaptureError.engineStartFailed("IOBindingChanged"))

        let result = s.triggerDown()

        #expect(capture.startAttempts == 1)
        guard case .failure(let failure) = result else {
            Issue.record("cold start should have failed"); return
        }
        // The assertion that would have caught the original defect.
        #expect(failure != .micPermissionDenied)
        #expect(failure == .emptyCapture)
    }

    @Test("every non-consent cold-start error keeps its honest classification")
    func allDeviceErrorsAgree() {
        // `MicCapture.start()` throws all of these, and the 5 s bound around
        // it throws on the documented Core Audio HAL hang too. None is a
        // consent problem.
        let errors: [Error] = [
            MicCaptureError.engineStartFailed("boom"),
            MicCaptureError.converterCreationFailed,
            MicCaptureError.inputFormatMismatch(
                device: "AirPods Pro", deviceRate: 24_000, engineRate: 48_000),
        ]
        for error in errors {
            let (s, _) = Self.session(throwing: error)
            guard case .failure(let failure) = s.triggerDown() else {
                Issue.record("\(error) should have failed"); continue
            }
            #expect(failure == .emptyCapture, "\(error) reported \(failure)")
        }
    }

    @Test("a real permission denial is still reported as one")
    func consentStillReportsConsent() {
        // The other half of the guard. Narrowing the catch must not empty the
        // one classification that legitimately sends the user to System
        // Settings — otherwise a genuinely ungranted microphone would look
        // like a device fault and the user would never be told what to do.
        let (s, _) = Self.session(throwing: MicCaptureError.permissionDenied)

        guard case .failure(let failure) = s.triggerDown() else {
            Issue.record("cold start should have failed"); return
        }
        #expect(failure == .micPermissionDenied)
    }

    @Test("a failed cold start leaves the machine ready for the next press")
    func machineIsNotLeftStuck() {
        // The original reason the catch existed at all. If the phase machine
        // stays in `.capturing`, every later trigger reports "arrived out of
        // order" until the surface disarms itself — the 2026-08-08 incident
        // this repo already documents. Narrowing the classification must not
        // cost the reset.
        let (s, _) = Self.session(throwing: MicCaptureError.engineStartFailed("boom"))
        _ = s.triggerDown()

        // A second press must be accepted, not rejected as out of order.
        guard case .failure(let second) = s.triggerDown() else {
            Issue.record("second press should also reach the cold-start path"); return
        }
        #expect(second == .emptyCapture)
        if case .invalidSequencing = second {
            Issue.record("the phase machine was left stuck after a failed cold start")
        }
    }
}
#endif
