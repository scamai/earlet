// A dictation started in a password field (or under Secure Input) is refused
// at the press: no microphone, no transcription, nothing kept.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("The press-time privacy gate refuses before recording")
struct PressPrivacyTests {

    private func pressAndRelease(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        try? await Task.sleep(for: .milliseconds(100))
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        try? await Task.sleep(for: .milliseconds(100))
    }

    @Test("a focused password field refuses the press: no capture, no ASR, no insertion")
    func passwordFieldIsRefused() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.privacy.mutate { $0 = .secureFieldFocused }

        await pressAndRelease(harness, model)
        #expect(harness.privacyChecks.get == 1)
        #expect(harness.capture.state.get.starts == 0)
        #expect(harness.workers.made[0].state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(model.status.contains("password fields"))
        #expect(!harness.keyWatch.active)
    }

    @Test("Secure Input refuses the press too")
    func secureInputIsRefused() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.privacy.mutate { $0 = .secureInputActive(heldByOtherProcess: true) }

        await pressAndRelease(harness, model)
        #expect(harness.capture.state.get.starts == 0)
        #expect(model.status.contains("Secure input"))
    }

    @Test("incomplete accessibility metadata does not block a dictation")
    func noFocusedElementStillDictates() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.privacy.mutate { $0 = .noFocusedElement }

        await pressAndRelease(harness, model)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }
}
#endif
