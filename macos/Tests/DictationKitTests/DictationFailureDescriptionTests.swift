// Failure descriptions reach Dictation's log and, before the app mapped them
// to its own sentences, its status line. Every supported failure must remain
// available with its specified diagnostic wording.

#if canImport(Testing)
import Testing
@testable import DictationKit

@Suite("Failure descriptions remain user-facing")
struct DictationFailureDescriptionTests {

    static let insertFailures: [DictationInsertFailure] = [
        .accessibilityPermissionMissing, .focusedElementNotEditable, .noFocusedElement,
        .secureInputActive(heldByOtherProcess: true), .secureInputActive(heldByOtherProcess: false),
        .secureFieldFocused, .pasteNeverRequested, .keyEventPostFailed,
    ]

    static let sessionFailures: [DictationSession.SessionFailure] =
        [.micPermissionDenied, .emptyCapture, .emptyTranscript, .invalidSequencing(.wrongPhase),
         .asr(.workerUnavailable), .asr(.notInitialized), .asr(.ioError("eof")),
         .asr(.engineError("boom"))]
        + insertFailures.map { .insert($0) }

    @Test("session and insert failures use the specified diagnostic wording")
    func descriptionsAreSpecified() {
        let expected = [
            "microphone permission not granted",
            "captured no usable audio",
            "ASR returned an empty transcript (nothing to insert)",
            "invalid trigger sequencing: event not valid in the current phase",
            "ASR failed: the ASR worker paths didn't resolve or the subprocess failed to spawn",
            "ASR failed: transcribe called before a successful start()",
            "ASR failed: ASR worker IPC error (worker may have died): eof",
            "ASR failed: ASR worker returned an error: boom",
            "paste failed: no Accessibility permission, so the focused field cannot be read",
            "paste failed: the focused element does not take text",
            "paste failed: nothing has keyboard focus to paste into",
            "paste failed: another app turned on Secure Input, usually for a password field, and it blocks the paste",
            "paste failed: Secure Input is on",
            "paste failed: the focused field is a password field, and dictation never types into one",
            "paste failed: the app never read the pasted text, so the paste most likely did not land",
            "paste failed: the ⌘V keystroke could not be sent",
        ]
        #expect(Self.sessionFailures.map(\.description) == expected)
    }

    @Test("the password-field refusal still says why nothing was typed")
    func passwordRefusalIsExplicit() {
        let text = DictationInsertFailure.secureFieldFocused.description
        #expect(text.contains("password field"))
    }
}
#endif
