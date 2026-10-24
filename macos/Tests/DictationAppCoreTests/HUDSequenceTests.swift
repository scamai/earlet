// What the floating HUD is sent across a whole dictation, read from the
// model's own publisher rather than from hand-written resolve() calls: the
// HUD resolves every change it sees, so an intermediate combination the
// model passes through is something the user sees. Every ending also has to
// reach the HUD, not only the status line in a window that is usually
// closed.

#if canImport(Testing)
import Combine
import Foundation
import Testing
import DictationAudioCapture
import DictationKit
@testable import DictationAppCore

/// Every HUD state the model published after the recorder was made.
@MainActor
final class HUDStateRecorder {
    private var states: [DictationHUDState] = []
    private var subscription: AnyCancellable?

    init(_ model: DictationModel) {
        subscription = model.hudStates.sink { [weak self] state in self?.states.append(state) }
    }

    /// What the screen went through: the value current at subscription is
    /// dropped and consecutive repeats are collapsed (a repeat is no change
    /// on screen).
    var changes: [DictationHUDState] {
        var collapsed: [DictationHUDState] = []
        for state in states where collapsed.last != state { collapsed.append(state) }
        return Array(collapsed.dropFirst())
    }

    var last: DictationHUDState? { states.last }

    /// Every outcome shown, in order.
    var outcomes: [DictationHUDOutcome] {
        changes.compactMap { if case .outcome(let outcome) = $0 { return outcome } else { return nil } }
    }

    var everRecorded: Bool {
        states.contains { if case .recording = $0 { return true } else { return false } }
    }
}

@MainActor
@Suite("The HUD goes from hold to result without passing through hidden")
struct HUDSequenceTests {

    /// Enabled, with outcomes that expire quickly so a sequence ends hidden.
    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        model.hudOutcomeSecondsOverride = 0.05
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func outcome(_ kind: DictationHUDOutcome.Kind) -> DictationHUDState {
        .outcome(DictationHUDContent.outcome(for: kind, holdKeyName: DictationHoldKey.standard.name))
    }

    /// Press, release, and wait until the HUD has hidden again.
    private func dictate(_ harness: Harness, _ model: DictationModel, _ recorder: HUDStateRecorder) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(await eventually { recorder.last == .hidden })
    }

    @Test("press, release and insert: recording, transcribing, inserted, hidden, with no gap")
    func holdReleaseInsert() async {
        let harness = Harness()
        let model = await enabled(harness)
        let recorder = HUDStateRecorder(model)

        await dictate(harness, model, recorder)

        #expect(model.status.hasPrefix("Text inserted"))
        #expect(recorder.changes == [.recording(latched: false), .transcribing, outcome(.inserted), .hidden])
    }

    @Test("a latched dictation goes from the latch to transcribing directly")
    func latchedRelease() async {
        let harness = Harness()
        let model = await enabled(harness)
        let recorder = HUDStateRecorder(model)

        harness.trigger.doubleTap()
        #expect(await eventually { model.latched && model.recording })
        harness.trigger.doubleTapAgain()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        #expect(await eventually { recorder.last == .hidden })

        #expect(recorder.changes == [.recording(latched: true), .transcribing, outcome(.inserted), .hidden])
    }

    @Test("Escape goes from Listening straight to Cancelled")
    func escapeShowsCancelled() async {
        let harness = Harness()
        let model = await enabled(harness)
        let recorder = HUDStateRecorder(model)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && !model.recording })
        #expect(await eventually { recorder.last == .hidden })
        harness.trigger.release()

        #expect(recorder.changes == [.recording(latched: false), outcome(.cancelled), .hidden])
    }

    @Test("Escape while the words are transcribed shows Cancelled after Transcribing")
    func escapeDuringTranscriptionShowsCancelled() async {
        var harness = Harness()
        harness.timing.transcribeSeconds = 30
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }
        let recorder = HUDStateRecorder(model)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { worker.state.get.transcribes == 1 })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.busy && recorder.last == .hidden })
        worker.finishTranscribe("late words")

        #expect(recorder.changes == [.recording(latched: false), .transcribing, outcome(.cancelled), .hidden])
    }

    @Test("a shortcut on the hold key hides the HUD without a notice")
    func shortcutShowsNothing() async {
        let harness = Harness()
        let model = await enabled(harness)
        let recorder = HUDStateRecorder(model)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.keyWatch.type(8) // kVK_ANSI_C
        #expect(await eventually { !model.busy && !model.recording && model.status.contains("shortcut") })
        harness.trigger.release()

        #expect(recorder.changes == [.recording(latched: false), .hidden])
    }

    @Test("a press in a password field shows the refusal and never Listening")
    func passwordFieldNeverShowsListening() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.privacy.mutate { $0 = .secureFieldFocused }
        let recorder = HUDStateRecorder(model)

        harness.trigger.press()
        #expect(await eventually { model.hudOutcome != nil })
        harness.trigger.release()
        #expect(await eventually { recorder.last == .hidden })

        #expect(!recorder.everRecorded, "a refused press is never shown as recording")
        #expect(!model.status.contains("Listening"))
        #expect(recorder.changes == [outcome(.passwordField), .hidden])
        #expect(harness.capture.state.get.starts + harness.capture.state.get.warmUps == 0)
    }

    @Test("Secure Input refuses with its own outcome")
    func secureInputOutcome() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.privacy.mutate { $0 = .secureInputActive(heldByOtherProcess: true) }
        let recorder = HUDStateRecorder(model)

        harness.trigger.press()
        #expect(await eventually { model.hudOutcome != nil })
        harness.trigger.release()
        #expect(!recorder.everRecorded)
        #expect(recorder.outcomes == [DictationHUDContent.outcome(for: .secureInput, holdKeyName: "Right Command")])
    }

    @Test("a refused paste whose words went to the clipboard says so and names ⌘V")
    func keptOnClipboardOutcome() async {
        let harness = Harness()
        harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        let model = await enabled(harness)
        let recorder = HUDStateRecorder(model)

        await dictate(harness, model, recorder)

        #expect(recorder.changes == [.recording(latched: false), .transcribing, outcome(.keptOnClipboard), .hidden])
        #expect(recorder.outcomes.first?.detail?.contains("⌘V") == true)
        #expect(recorder.outcomes.first?.tone == .caution)
    }

    @Test("a refused paste that could not reach the clipboard does not promise it")
    func notInsertedOutcome() async {
        let harness = Harness()
        harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        harness.clipboardWorks.mutate { $0 = false }
        let model = await enabled(harness)
        let recorder = HUDStateRecorder(model)

        await dictate(harness, model, recorder)

        #expect(recorder.outcomes.map(\.title) == ["Your words are still here"])
        #expect(model.recentResult.entry?.needsRecovery == true)
        #expect(recorder.outcomes.first?.detail?.contains("⌘V") == false)
    }

    @Test("a silent hold shows Nothing heard, calmly")
    func nothingHeardOutcome() async {
        let harness = Harness()
        // Quiet room tone is nonzero; the digital-zero input fault is separate.
        harness.capture.samples.mutate { $0 = [Float](repeating: 0.0001, count: 16_000) }
        let model = await enabled(harness)
        let recorder = HUDStateRecorder(model)

        await dictate(harness, model, recorder)

        #expect(recorder.outcomes == [DictationHUDContent.outcome(for: .nothingHeard, holdKeyName: "Right Command")])
        #expect(recorder.outcomes.first?.tone == .neutral)
    }

    @Test("a worker that died mid-transcription retries and inserts")
    func workerDiedOutcome() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate { $0.replies = [.failure(.ioError("eof"))] }
        let recorder = HUDStateRecorder(model)

        await dictate(harness, model, recorder)

        #expect(recorder.outcomes.map(\.title) == ["Inserted"])
    }

    @Test("a capture that cannot start goes from Listening straight to the reason")
    func captureFailureOutcome() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.capture.state.mutate { $0.warmUpError = MicCaptureError.permissionDenied }
        let recorder = HUDStateRecorder(model)

        harness.trigger.press()
        #expect(await eventually { model.hudOutcome != nil })
        harness.trigger.release()
        #expect(await eventually { recorder.last == .hidden })

        #expect(recorder.changes == [.recording(latched: false), outcome(.microphoneBlocked), .hidden])
    }

    @Test("no outcome ever carries the dictated words")
    func outcomesNeverCarryTheTranscript() async {
        let harness = Harness()
        let model = await enabled(harness)
        let worker = harness.workers.made[0]
        let recorder = HUDStateRecorder(model)

        worker.state.mutate { $0.replies = [.text("zanzibar quokka")] }
        await dictate(harness, model, recorder)
        harness.inserts.outcome.mutate { $0 = .failed(.noFocusedElement) }
        worker.state.mutate { $0.replies = [.text("zanzibar quokka")] }
        await dictate(harness, model, recorder)

        #expect(recorder.outcomes.count == 2)
        for outcome in recorder.outcomes {
            let shown = outcome.title + " " + (outcome.detail ?? "")
            #expect(!shown.lowercased().contains("zanzibar"), "\(shown)")
            #expect(!shown.lowercased().contains("quokka"), "\(shown)")
        }
    }

    @Test("an outcome expires on its own, and the next admitted hold clears it at once")
    func outcomeLifetime() async {
        let harness = Harness()
        let model = harness.model()
        model.hudOutcomeSecondsOverride = 5
        await model.toggle()

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { model.hudOutcome?.title == "Inserted" })

        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(model.hudOutcome == nil, "a new hold makes the last outcome stale")
        model.hudOutcomeSecondsOverride = 0.05
        harness.trigger.release()
        #expect(await eventually { model.hudOutcome != nil })
        #expect(await eventually { model.hudOutcome == nil }, "and it hides after its dwell")
    }
}
#endif
