#if canImport(Testing)
import Testing
import CoreAudio
@testable import DictationAudioCapture

/// A refused device bind used to be logged and then ignored.
///
/// The engine started "warm" while bound to nothing, the tap delivered
/// whatever the HAL had been left on — measured on this machine as 0.30 s at
/// peak 0.0029, under the 0.0040 audible floor — and the user was told
/// `nothingHeard`. They had spoken. The microphone had never opened. Because
/// the rebuild-on-next-trigger path re-ran the same bind and swallowed it
/// again, every subsequent press reported `bound=(unbound)` and dictation
/// stayed dead until the app was restarted.
@Suite("Microphone bind failure")
struct MicBindFailureTests {

    /// 'nope' — `kAudioHardwareIllegalOperationError`, the status observed in
    /// the field after several AirPods ⇄ built-in switches.
    private static let nope: OSStatus = 1_852_797_029

    @Test("a refused bind is its own failure, not a mismatched format or a dead engine")
    func bindFailureIsDistinct() {
        let bind = MicCaptureError.inputDeviceBindFailed(
            device: "MacBook Pro Microphone", status: Self.nope)

        if case .inputDeviceBindFailed = bind {
            // the shape the caller switches on
        } else {
            Issue.record("inputDeviceBindFailed did not match its own case")
        }
    }

    @Test("the message says the microphone never opened, and does not blame the speaker")
    func bindFailureCopyIsTruthful() {
        let text = MicCaptureError.inputDeviceBindFailed(
            device: "MacBook Pro Microphone", status: Self.nope).description

        // The whole point: the user must not be sent away thinking they were
        // too quiet, which is what `nothingHeard` told them.
        #expect(text.contains("never opened"))
        #expect(text.contains("MacBook Pro Microphone"))
        #expect(text.contains("\(Self.nope)"))

        // NOT `!contains("louder")`. That was the first version of this check
        // and it failed against copy that is exactly right — "this is not a
        // question of speaking louder" preempts the wrong conclusion, and a
        // substring test cannot tell an instruction from its negation. What
        // must be true is that the message RULES the user's volume OUT.
        #expect(text.contains("not a question of speaking louder"))

        // And it must never restate the diagnosis it replaced.
        #expect(!text.lowercased().contains("nothing was heard"))
        #expect(!text.lowercased().contains("try again"))
    }

    @Test("the status is carried, because which refusal it was decides the fix")
    func bindFailureCarriesTheStatus() {
        // 'nope' is a refusal to re-point a unit that is already going;
        // a missing device is a different problem with a different answer,
        // and a message that dropped the code would flatten the two.
        guard case .inputDeviceBindFailed(_, let status) =
                MicCaptureError.inputDeviceBindFailed(device: "x", status: Self.nope)
        else {
            Issue.record("could not read the status back")
            return
        }
        #expect(status == Self.nope)
    }
}
#endif
