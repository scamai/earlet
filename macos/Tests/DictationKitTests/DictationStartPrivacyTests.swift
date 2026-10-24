#if canImport(Testing)
import Testing
@testable import DictationKit

@Suite("Dictation press-time privacy gate")
struct DictationStartPrivacyTests {
    @Test("Secure Input and AX password fields block before capture")
    func secureSignalsBlock() {
        #expect(TextInserter.dictationStartPrivacyDecision(
            secureInputFailure: .secureInputActive(heldByOtherProcess: true),
            focus: .editable
        ) == .secureInputActive(heldByOtherProcess: true))
        #expect(TextInserter.dictationStartPrivacyDecision(
            secureInputFailure: nil,
            focus: .secureField
        ) == .secureFieldFocused)
    }

    @Test("Incomplete AX metadata does not block ordinary paste targets")
    func incompleteAccessibilityIsNotARecordingBlock() {
        for result in [
            FocusCheckResult.editable,
            .notEditable,
            .noFocusedElement,
            .permissionMissing,
        ] {
            #expect(TextInserter.dictationStartPrivacyDecision(
                secureInputFailure: nil,
                focus: result
            ) == nil)
        }
    }
}
#endif
