// The status line speaks in sentences: no enum names, no worker messages,
// no other product's name.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@Suite("Every failure has a user-facing sentence")
struct DictationCopyTests {

    static let insertFailures: [DictationInsertFailure] = [
        .accessibilityPermissionMissing, .focusedElementNotEditable, .noFocusedElement,
        .secureInputActive(heldByOtherProcess: true), .secureInputActive(heldByOtherProcess: false),
        .secureFieldFocused, .pasteNeverRequested, .keyEventPostFailed,
    ]

    static let failures: [DictationSession.SessionFailure] =
        [.micPermissionDenied, .emptyCapture, .emptyTranscript, .invalidSequencing(.wrongPhase),
         .asr(.workerUnavailable), .asr(.notInitialized),
         .asr(.ioError("IpcClientError.eof /Users/someone/runtime")),
         .asr(.engineError("ModuleNotFoundError: No module named 'mlx_whisper'"))]
        + insertFailures.map { .insert($0) }

    /// Words that only make sense inside the code.
    static let internalWords = ["ASR", "IPC", "worker", "Error", "eof", "/Users", "nil"]

    static func looksInternal(_ sentence: String) -> String? {
        if let word = internalWords.first(where: { sentence.contains($0) }) { return word }
        // A camelCase identifier, such as an enum case name.
        if sentence.range(of: #"\b[a-z]+[A-Z][A-Za-z]*\b"#, options: .regularExpression) != nil {
            return "camelCase"
        }
        return nil
    }

    @Test("no failure sentence carries a code identifier or worker detail")
    func sentencesAreUserFacing() {
        for failure in Self.failures {
            for stage in [DictationSession.Stage.capture, .asr, .paste] {
                let sentence = DictationCopy.failure(failure, stage: stage, holdKey: .standard)
                #expect(!sentence.isEmpty)
                #expect(Self.looksInternal(sentence) == nil, "\(failure) → \(sentence)")
                #expect(!sentence.contains(failure.description), "\(failure)")
            }
        }
        for failure in Self.insertFailures {
            for kept in [true, false] {
                let sentence = DictationCopy.insert(failure, keptOnClipboard: kept)
                #expect(Self.looksInternal(sentence) == nil, "\(failure) → \(sentence)")
            }
        }
    }

    @Test("silence before ASR and an empty decode after it read differently")
    func silenceIsCalm() {
        let silence = DictationCopy.failure(.emptyTranscript, stage: .capture, holdKey: .standard)
        let empty = DictationCopy.failure(.emptyTranscript, stage: .asr, holdKey: .standard)
        #expect(silence.contains("Nothing was heard"))
        #expect(silence.contains("Right Command"))
        #expect(empty != silence)
    }

    @Test("silence after a hold long enough to speak asks for a louder voice, as the HUD does")
    func silenceAfterALongHold() {
        let tap = DictationCopy.failure(.emptyTranscript, stage: .capture, holdKey: .standard, heldSeconds: 0.3)
        let hold = DictationCopy.failure(.emptyTranscript, stage: .capture, holdKey: .standard,
                                         heldSeconds: DictationCopy.speakUpAfterSeconds)
        #expect(tap == "Nothing was heard. Hold Right Command while you speak, then release.")
        #expect(hold == "Nothing was heard. Speak up, or move closer to the microphone.")
        // No signal at all is not a quiet voice: the hold hint stays.
        #expect(DictationCopy.failure(.emptyTranscript, stage: .capture, holdKey: .standard, heldSeconds: 0.8,
                                      peak: DictationSession.silentInputPeak / 5) == tap)
        #expect(DictationCopy.failure(.emptyTranscript, stage: .capture, holdKey: .standard, heldSeconds: 0.8,
                                      peak: 0.001) == hold)
        #expect(Self.looksInternal(hold) == nil, "\(hold)")
        // An empty decode is not about the hold's length.
        #expect(DictationCopy.failure(.emptyTranscript, stage: .asr, holdKey: .standard, heldSeconds: 5)
            == DictationCopy.failure(.emptyTranscript, stage: .asr, holdKey: .standard))
    }

    @Test("an input is named only by a real name, never a placeholder or a raw device id")
    func inputNames() {
        #expect(DictationCopy.inputName("AirPods Pro") == "AirPods Pro")
        #expect(DictationCopy.inputName(" AirPods Pro\n") == "AirPods Pro")
        #expect(DictationCopy.inputName("device 2 mic") == "device 2 mic", "only the bare id form is raw")
        for raw in [nil, "", " ", "device 117", "(unbound)", "(unknown)"] as [String?] {
            #expect(DictationCopy.inputName(raw) == nil, "\(raw ?? "nil")")
        }
        #expect(DictationCopy.silentInputDuringHold(deviceName: "AirPods Pro")
            == "No sound from AirPods Pro — check the microphone.")
        #expect(DictationCopy.silentInputDuringHold(deviceName: "device 117")
            == "No sound from the microphone — check it isn't muted.")
    }

    @Test("an empty decode's sentence says why, and promises a recording only when one was kept")
    func noWordsSentences() {
        let generic = DictationCopy.failure(.emptyTranscript, stage: .asr, holdKey: .standard)
        #expect(generic == "No words were recognized. Try again.")
        let reasons: [DictationNoWordsReason] = [.tooQuiet, .languageUnclear, .wordsUnclear, .unexplained]
        for reason in reasons {
            for kept in [true, false] {
                let sentence = DictationCopy.noWords(reason, kept: kept)
                #expect(Self.looksInternal(sentence) == nil, "\(reason) → \(sentence)")
                #expect(!sentence.contains("shorter phrase"), "\(reason)")
                #expect(sentence.contains(DictationCopy.takeSavedWhere) == (kept && reason.keepsTake),
                        "\(reason) kept=\(kept) → \(sentence)")
            }
        }
        #expect(DictationCopy.noWords(.unexplained, kept: false) == generic)
        #expect(DictationCopy.noWords(.wordsUnclear, kept: false) == generic)
        #expect(DictationCopy.noWords(.tooQuiet, kept: false).contains("move closer to the microphone"))
        #expect(DictationCopy.noWords(.languageUnclear, kept: false)
            .hasSuffix("Choose your language in Settings, then try again."))
    }

    @Test("a press refused while the last dictation finishes says so and what to do")
    func stillFinishingIsUserFacing() {
        let sentence = DictationCopy.stillFinishing(holdKey: .standard)
        #expect(sentence.contains("not recorded"))
        #expect(sentence.contains("Hold Right Command again"))
        #expect(Self.looksInternal(sentence) == nil, "\(sentence)")
    }

    @Test("the idle lines are sentences, and only one says ready")
    func idleLines() {
        for line in [DictationCopy.readyToEnable, DictationCopy.allowPermissionsFirst] {
            #expect(Self.looksInternal(line) == nil, "\(line)")
            #expect(line.hasSuffix("."))
        }
        #expect(!DictationCopy.allowPermissionsFirst.contains("Ready"))
        #expect(!DictationCopy.allowPermissionsFirst.contains("above"),
                "the same line is the menu's first item, where nothing is above it")
    }

    @Test("the clipboard is only promised when the text is on it")
    func clipboardPromiseIsHonest() {
        for failure in Self.insertFailures {
            #expect(!DictationCopy.insert(failure, keptOnClipboard: false).contains("⌘V"), "\(failure)")
        }
    }

    @Test("a missing grant names the pane as System Settings does on macOS 26 and on 27", arguments: [26, 27])
    func missingGrantNamesThePane(macOS: Int) {
        let pane = DictationPermissionStep.accessibilitySettingsName(majorVersion: macOS)
        #expect(DictationCopy.insert(.accessibilityPermissionMissing, keptOnClipboard: true,
                                     accessibilitySettingsName: pane)
            == "Earlet needs \(pane) permission to type. Your text is on the clipboard; press ⌘V to paste it.")
        #expect(DictationCopy.insert(.accessibilityPermissionMissing, keptOnClipboard: false,
                                     accessibilitySettingsName: pane)
            == "Earlet needs \(pane) permission to type. Allow it in System Settings → Privacy & Security → \(pane).")
    }

    @Test("every saved-recording line sends the user to the menu bar icon or Home, where Recover is")
    func savedRecordingLinesPointAtTheMenuBarIcon() {
        let saved = [DictationCopy.takeSaved, DictationCopy.takeSavedWorkerRestarting,
                     DictationCopy.takeSavedEngineNotStarted, DictationCopy.takeSavedEngineStartTimedOut,
                     DictationCopy.takeSavedEngineNotResponding]
        for line in saved {
            #expect(line.hasSuffix("The recording is saved. Recover it from Earlet's menu bar icon or on Home."),
                    "\(line)")
        }
        for line in saved + [DictationCopy.savedTakeOfferedAtLaunch] {
            #expect(!line.contains("Earlet menu"), "the app menu has no Recover Last Dictation: \(line)")
        }
    }
}

@MainActor
@Suite("The model shows sentences, not failure descriptions")
struct DictationModelCopyTests {

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("before enabling, a missing grant makes the idle line say what to do")
    func idleLineAsksForPermissions() {
        var harness = Harness()
        harness.accessibilityGranted = false
        let model = harness.model()
        model.refreshPermissions()
        #expect(model.status == DictationCopy.allowPermissionsFirst)
        #expect(!model.status.contains("Ready"))

        // Every grant in: ready, and the line says so.
        harness.accessibilityGranted = true
        model.refreshPermissions()
        #expect(model.status == DictationCopy.readyToEnable)

        // Revoked again before enabling: back to the instruction.
        harness.microphoneGranted = false
        model.refreshPermissions()
        #expect(model.status == DictationCopy.allowPermissionsFirst)
    }

    @Test("a more specific line is never replaced by the idle one")
    func specificLinesStay() async {
        var harness = Harness()
        harness.microphoneGranted = false
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        let specific = model.status
        #expect(specific.contains("microphone"))
        model.refreshPermissions()
        #expect(model.status == specific)
    }

    @Test("once enabled, a refresh leaves the status alone")
    func enabledStatusStays() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        let enabledLine = model.status
        model.refreshPermissions()
        #expect(model.status == enabledLine)
    }

    @Test("a silent hold says nothing was heard")
    func silentHold() async {
        let harness = Harness()
        // Quiet room tone is nonzero; exact digital zeros mean no input signal.
        harness.capture.samples.mutate { $0 = [Float](repeating: 0.0001, count: 16_000) }
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        #expect(model.status.contains("Nothing was heard"))
    }

    @Test("a worker error is not shown verbatim")
    func engineErrorIsNotVerbatim() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.workers.made[0].state.mutate {
            $0.replies = [.failure(.engineError("ModuleNotFoundError: No module named 'x'"))]
        }
        await dictate(harness, model)
        #expect(!model.status.contains("ModuleNotFoundError"))
        #expect(DictationCopyTests.looksInternal(model.status) == nil, "\(model.status)")
    }
}
#endif
