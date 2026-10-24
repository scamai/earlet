// The HUD's state mapping is pure: every state and every transition is
// pinned here, plus the model side of the mic-changed notice (set on an
// interrupted hold, cleared on expiry and on the next press).

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("The HUD shows exactly the live dictation state")
struct DictationHUDMappingTests {
    typealias Resolve = DictationHUDContent

    @Test("idle is hidden")
    func idleIsHidden() {
        #expect(Resolve.resolve(recording: false, transcribing: false, latched: false,
                                micChangedNotice: nil) == .hidden)
        #expect(Resolve.title(for: .hidden) == nil)
        #expect(Resolve.hint(for: .hidden, holdKeyName: "Right Command") == nil)
    }

    @Test("a hold shows Listening with the release hint")
    func holdShowsListening() {
        let state = Resolve.resolve(recording: true, transcribing: false, latched: false,
                                    micChangedNotice: nil)
        #expect(state == .recording(latched: false))
        #expect(Resolve.title(for: state) == "Listening")
        #expect(Resolve.hint(for: state, holdKeyName: "Right Command")
            == "Release to insert · Esc to cancel")
    }

    @Test("a latched hold names the double-tap stop with the chosen key")
    func latchedShowsTheDoubleTapHint() {
        let state = Resolve.resolve(recording: true, transcribing: false, latched: true,
                                    micChangedNotice: nil)
        #expect(state == .recording(latched: true))
        #expect(Resolve.title(for: state) == "Listening")
        #expect(Resolve.hint(for: state, holdKeyName: "Right Command")
            == "Press Right Command, Space or Return to stop")
    }

    @Test("a release shows Transcribing, and Escape still cancels it")
    func transcribingShowsSpinner() {
        let state = Resolve.resolve(recording: false, transcribing: true, latched: false,
                                    micChangedNotice: nil)
        #expect(state == .transcribing)
        #expect(Resolve.title(for: state) == "Transcribing…")
        #expect(Resolve.hint(for: state, holdKeyName: "Right Command") == "Esc to cancel")
    }

    @Test("hold → transcribing → hidden is the whole lifecycle")
    func lifecycle() {
        let states = [
            Resolve.resolve(recording: false, transcribing: false, latched: false, micChangedNotice: nil),
            Resolve.resolve(recording: true, transcribing: false, latched: false, micChangedNotice: nil),
            Resolve.resolve(recording: false, transcribing: true, latched: false, micChangedNotice: nil),
            Resolve.resolve(recording: false, transcribing: false, latched: false, micChangedNotice: nil),
        ]
        #expect(states == [.hidden, .recording(latched: false), .transcribing, .hidden])
    }

    @Test("a mic-changed notice outranks every other state")
    func micChangedOutranks() {
        let notice = "Mic changed to AirPods Pro — could you say that again?"
        #expect(Resolve.resolve(recording: true, transcribing: false, latched: true,
                                micChangedNotice: notice) == .micChanged(notice))
        #expect(Resolve.resolve(recording: false, transcribing: true, latched: false,
                                micChangedNotice: notice) == .micChanged(notice))
        #expect(Resolve.hint(for: .micChanged(notice), holdKeyName: "Right Command") == nil)
    }

    @Test("while recording, live preview text replaces the Listening title")
    func liveTextReplacesTheTitle() {
        let state = DictationHUDState.recording(latched: false)
        #expect(Resolve.title(for: state, livePreviewText: "this is my voice") == "this is my voice")
        #expect(Resolve.title(for: state, livePreviewText: "") == "Listening")
        #expect(Resolve.title(for: state) == "Listening", "no preview wired is the old behaviour")
        // Only recording: other states never show the preview.
        #expect(Resolve.title(for: .transcribing, livePreviewText: "this is my voice") == "Transcribing…")
    }
}

@MainActor
@Suite("Every ending has short HUD words of its own")
struct DictationHUDOutcomeTests {
    typealias Content = DictationHUDContent
    typealias Kind = DictationHUDOutcome.Kind

    /// Every kind. The switch below does not compile when a kind is added
    /// and left out of the list's reckoning.
    nonisolated static let kinds: [Kind] = [
        .inserted, .stoppedAtLimit(seconds: 120), .keptOnClipboard, .notInserted, .accessibilityNeeded, .passwordField,
        .secureInput, .nothingHeard, .noWords, .microphoneSilent, .microphoneBlocked,
        .cancelled, .engineRestarting, .engineUnavailable, .engineError, .stillFinishing,
        .commandDone, .textRecoverable, .resultCopied, .resultCopyFailed, .vocabularyLearned,
    ]

    nonisolated static func listed(_ kind: Kind) -> Bool {
        switch kind {
        case .inserted, .stoppedAtLimit, .keptOnClipboard, .notInserted, .accessibilityNeeded, .passwordField,
             .secureInput, .nothingHeard, .noWords, .microphoneSilent, .microphoneBlocked,
             .cancelled, .engineRestarting, .engineUnavailable, .engineError, .stillFinishing,
             .commandDone, .textRecoverable, .resultCopied, .resultCopyFailed, .vocabularyLearned:
            return kinds.contains(kind)
        }
    }

    private func words(_ kind: Kind) -> DictationHUDOutcome {
        Content.outcome(for: kind, holdKeyName: "Right Command")
    }

    @Test("every ending has a title of its own, a dwell, and no code words", arguments: kinds)
    func everyEndingReads(_ kind: Kind) {
        #expect(Self.listed(kind))
        let outcome = words(kind)
        #expect(!outcome.title.isEmpty)
        #expect(outcome.dwell > 0 && outcome.dwell <= 5)
        let others = Self.kinds.filter { $0 != kind }.map { words($0).title }
        #expect(!others.contains(outcome.title), "\(kind) shares its title")
        for line in [outcome.title, outcome.detail ?? ""] {
            #expect(DictationCopyTests.looksInternal(line) == nil, "\(kind) → \(line)")
        }
        if let detail = outcome.detail { #expect(detail.hasSuffix(".")) }
    }

    @Test("only a successful clipboard write promises ⌘V")
    func onlyTheClipboardPromisesPaste() {
        for kind in Self.kinds {
            let promises = words(kind).detail?.contains("⌘V") == true
            #expect(promises == (kind == .keptOnClipboard || kind == .resultCopied), "\(kind)")
        }
        #expect(words(.keptOnClipboard).tone == .caution)
        #expect(words(.keptOnClipboard).dwell >= 4, "long enough to read and act on")
    }

    @Test("the ceiling's ending names the limit and how to go on")
    func stoppedAtTheLimit() {
        let outcome = words(.stoppedAtLimit(seconds: 120))
        #expect(outcome.title == "Stopped at 2:00")
        #expect(outcome.detail == "Your words went in. Hold Right Command again to keep going.")
        #expect(outcome.tone == .caution)
    }

    @Test("a receipt is brief and calm; nothing heard and cancel are not alarms")
    func calmEndings() {
        #expect(words(.inserted).dwell <= 1)
        for kind: Kind in [.inserted, .nothingHeard, .noWords, .cancelled, .commandDone] {
            #expect(words(kind).tone == .neutral, "\(kind)")
        }
        #expect(words(.nothingHeard).detail == "Hold Right Command while you speak.")
    }

    @Test("every session failure maps to the ending the status line describes")
    func failuresMapToEndings() {
        let expected: [(DictationSession.SessionFailure, DictationSession.Stage, Kind)] = [
            (.micPermissionDenied, .capture, .microphoneBlocked),
            (.emptyCapture, .capture, .microphoneSilent),
            (.emptyTranscript, .capture, .nothingHeard),
            (.emptyTranscript, .asr, .noWords),
            (.asr(.engineError("x")), .asr, .engineError),
            (.asr(.ioError("eof")), .asr, .engineRestarting),
            (.asr(.notInitialized), .asr, .engineRestarting),
            (.asr(.workerUnavailable), .asr, .engineRestarting),
            (.insert(.secureFieldFocused), .paste, .passwordField),
            (.insert(.noFocusedElement), .paste, .notInserted),
            (.invalidSequencing(.wrongPhase), .capture, .stillFinishing),
        ]
        for (failure, stage, kind) in expected {
            #expect(Content.ending(for: failure, stage: stage) == kind, "\(failure)")
        }
    }

    @Test("a refused paste or press maps by field and by whether the words were kept")
    func refusedPastesMap() {
        #expect(Content.ending(forRefusedPaste: .secureFieldFocused, keptOnClipboard: false) == .passwordField)
        #expect(Content.ending(forRefusedPaste: .secureInputActive(heldByOtherProcess: true),
                               keptOnClipboard: false) == .secureInput)
        #expect(Content.ending(forRefusedPaste: .accessibilityPermissionMissing, keptOnClipboard: true)
            == .keptOnClipboard)
        #expect(Content.ending(forRefusedPaste: .accessibilityPermissionMissing, keptOnClipboard: false)
            == .accessibilityNeeded)
        for failure: DictationInsertFailure in [.focusedElementNotEditable, .noFocusedElement,
                                                .pasteNeverRequested, .keyEventPostFailed] {
            #expect(Content.ending(forRefusedPaste: failure, keptOnClipboard: true) == .keptOnClipboard)
            #expect(Content.ending(forRefusedPaste: failure, keptOnClipboard: false) == .notInserted)
        }
    }

    @Test("an outcome ranks under the live hold and the mic notice, over transcribing")
    func outcomePriority() {
        let receipt = words(.inserted)
        #expect(Content.resolve(recording: false, transcribing: false, latched: false,
                                micChangedNotice: nil, outcome: receipt) == .outcome(receipt))
        #expect(Content.resolve(recording: true, transcribing: false, latched: false,
                                micChangedNotice: nil, outcome: receipt) == .recording(latched: false))
        #expect(Content.resolve(recording: false, transcribing: false, latched: false,
                                micChangedNotice: "Mic changed", outcome: receipt) == .micChanged("Mic changed"))
        // A press refused while the last release is still transcribing must
        // be seen by the user who is holding the key.
        let refused = words(.stillFinishing)
        #expect(Content.resolve(recording: false, transcribing: true, latched: false,
                                micChangedNotice: nil, outcome: refused) == .outcome(refused))
        #expect(Content.title(for: .outcome(receipt)) == "Inserted")
        #expect(Content.hint(for: .outcome(words(.cancelled)), holdKeyName: "Right Command")
            == "Nothing was inserted.")
    }
}

@MainActor
@Suite("A long hold shows its time, and counts down to the ceiling")
struct DictationHUDTimeTests {
    typealias Content = DictationHUDContent
    private let key = "Right Command"

    @Test("a latched hold shows how long it has run")
    func latchedShowsElapsed() {
        #expect(Content.hint(for: .recording(latched: true), holdKeyName: key, elapsed: 42, limit: 120)
            == "0:42 · Press Right Command, Space or Return to stop")
        #expect(Content.hint(for: .recording(latched: true), holdKeyName: key, elapsed: 65.9, limit: 600)
            == "1:05 · Press Right Command, Space or Return to stop")
    }

    @Test("a held key keeps its plain hint until the countdown")
    func heldHintIsPlain() {
        #expect(Content.hint(for: .recording(latched: false), holdKeyName: key, elapsed: 42, limit: 120)
            == "Release to insert · Esc to cancel")
    }

    @Test("the last sixty seconds count down, held or latched")
    func countdown() {
        #expect(Content.hint(for: .recording(latched: false), holdKeyName: key, elapsed: 59.9, limit: 120)
            == "Release to insert · Esc to cancel", "not yet")
        #expect(Content.hint(for: .recording(latched: false), holdKeyName: key, elapsed: 105, limit: 120)
            == "Stops in 0:15 · Release to insert")
        #expect(Content.hint(for: .recording(latched: false), holdKeyName: key, elapsed: 107.2, limit: 120)
            == "Stops in 0:13 · Release to insert")
        #expect(Content.hint(for: .recording(latched: true), holdKeyName: key, elapsed: 110, limit: 120)
            == "Stops in 0:10 · Press Right Command, Space or Return to stop")
        #expect(Content.hint(for: .recording(latched: true), holdKeyName: key, elapsed: 125, limit: 120)
            == "Stops in 0:00 · Press Right Command, Space or Return to stop", "never negative")
    }

    @Test("without a clock, and outside a recording, the hint is the plain one")
    func noClock() {
        #expect(Content.hint(for: .recording(latched: true), holdKeyName: key, elapsed: nil, limit: 120)
            == Content.hint(for: .recording(latched: true), holdKeyName: key))
        #expect(Content.hint(for: .transcribing, holdKeyName: key, elapsed: 110, limit: 120) == "Esc to cancel")
    }

    @Test("the clock is m:ss, floored, never negative")
    func clock() {
        #expect(Content.clock(0) == "0:00")
        #expect(Content.clock(9.99) == "0:09")
        #expect(Content.clock(125.9) == "2:05")
        #expect(Content.clock(600) == "10:00")
        #expect(Content.clock(-3) == "0:00")
    }
}

@MainActor
@Suite("The mic-changed notice is ephemeral and dies with the next press")
struct MicChangedNoticeTests {

    private func interruptedModel(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        harness.capture.state.mutate {
            $0.interruption = DictationCaptureInterruption(reason: "no-chunks", deviceName: "AirPods Pro")
        }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.micChangedNotice != nil })
        return model
    }

    @Test("an interrupted hold publishes the notice beside the status line")
    func interruptionPublishesTheNotice() async {
        let harness = Harness()
        let model = await interruptedModel(harness)
        #expect(model.micChangedNotice == "Mic changed to AirPods Pro — could you say that again?")
        #expect(model.status == model.micChangedNotice)
    }

    @Test("the notice clears itself after its seconds")
    func noticeExpires() async {
        let harness = Harness()
        let model = await interruptedModel(harness)
        model.micChangedNoticeSeconds = 0.05
        // Re-trigger the timer with the shortened bound: the first notice
        // was already shown with the default four seconds.
        harness.capture.state.mutate {
            $0.interruption = DictationCaptureInterruption(reason: "no-chunks", deviceName: "AirPods Pro")
        }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.micChangedNotice != nil })
        #expect(await eventually { model.micChangedNotice == nil }, "the HUD hides after the notice expires")
    }

    @Test("the next press clears the notice however much time is left")
    func nextPressClearsTheNotice() async {
        let harness = Harness()
        let model = await interruptedModel(harness)
        #expect(model.micChangedNotice != nil)
        harness.capture.state.mutate { $0.interruption = nil }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(model.micChangedNotice == nil)
        harness.trigger.release()
        #expect(await eventually { !model.busy })
        #expect(model.micChangedNotice == nil, "a healthy hold leaves no notice behind")
    }
}
@MainActor
@Suite("The HUD waveform tracks the live input level")
struct DictationHUDLevelTests {
    typealias Content = DictationHUDContent

    @Test("silence is a flat floor — no pulse, no breathing, no randomness")
    func silenceIsAFlatFloor() {
        let heights = Content.barHeights(level: 0)
        #expect(heights == [Content.waveformMinHeight, Content.waveformMinHeight, Content.waveformMinHeight])
        // The floor is a function of the level alone: calling it again, at
        // any "phase", gives the identical still shape.
        #expect(Content.barHeights(level: 0) == heights)
    }

    @Test("level 0.5 lands the tall bar near the middle of the range")
    func levelHalfIsMidHeight() {
        let heights = Content.barHeights(level: 0.5)
        let mid = Content.waveformMinHeight + (Content.waveformMaxHeight - Content.waveformMinHeight) / 2
        #expect(heights[0] == mid)
        #expect(heights[1] < mid && heights[2] < mid, "a fixed wave silhouette, not one column")
        #expect(heights.allSatisfy { $0 >= Content.waveformMinHeight })
    }

    @Test("level 1 tops the tall bar at the maximum")
    func levelOneIsMaxHeight() {
        let heights = Content.barHeights(level: 1)
        #expect(heights[0] == Content.waveformMaxHeight)
        #expect(heights.allSatisfy { $0 > Content.waveformMinHeight })
        // Out-of-range input is clamped, never taller or inverted.
        #expect(Content.barHeights(level: 2)[0] == Content.waveformMaxHeight)
    }

    @Test("the per-bar easing converges toward the target and never overshoots")
    func easingConverges() {
        var level: Float = 0
        var previous: Float = 0
        for _ in 0..<10 {
            level = Content.easedLevel(current: level, target: 1)
            #expect(level > previous, "every step approaches the target")
            #expect(level <= 1, "and never overshoots it")
            previous = level
        }
        #expect(level > 0.99, "ten chunks at ~12 Hz is under a second to settle")
        // Two chunks ≈ 170 ms covers ~90% of a step — the ~120–160 ms feel.
        var two: Float = 0
        two = Content.easedLevel(current: two, target: 1)
        two = Content.easedLevel(current: two, target: 1)
        #expect(two > 0.85)
    }

    @Test("the frozen title reference is the full caption budget, both scripts")
    func frozenReferenceCoversTheCaptionBudget() {
        #expect(Content.recordingTitleReferenceCJK.count
            == DictationPreviewCaption.defaultCJKCharacterBudget)
        #expect(Content.recordingTitleReferenceWords.split(separator: " ").count
            == DictationPreviewCaption.defaultWordWindow)
    }

    @Test("silence and room tone map to the floor of the dB window")
    func silenceMapsToZero() {
        #expect(Content.normalizedLevel(peak: 0) == 0)
        #expect(Content.normalizedLevel(peak: 0.001) == 0, "-60 dB is under the floor")
        #expect(Content.normalizedLevel(peak: 0.005) == 0, "-46 dB is still room tone")
    }

    @Test("ordinary speech peaks land visibly above half the window")
    func speechIsVisible() {
        for peak in [Float(0.05), 0.08, 0.1] {
            let level = Content.normalizedLevel(peak: peak)
            #expect(level > 0.5, "peak \(peak) should read well above half, got \(level)")
            #expect(level < 0.75, "and leave headroom for a shout, got \(level)")
        }
    }

    @Test("a shout reaches the ceiling and clipping clamps to it")
    func shoutMapsToOne() {
        #expect(Content.normalizedLevel(peak: 0.5) == 1)
        #expect(Content.normalizedLevel(peak: 1) == 1)
    }
}

@MainActor
@Suite("Visible HUD states dwell before they change; hidden and mic-changed pre-empt")
struct DictationHUDPacerTests {
    private let dwell = DictationHUDPacer.visibleDwellSeconds

    @Test("a visible state cannot be replaced inside its dwell")
    func transitionBeforeDwellIsDeferred() {
        var pacer = DictationHUDPacer()
        #expect(pacer.offer(.recording(latched: false), at: 0) == .show(.recording(latched: false)))
        let decision = pacer.offer(.transcribing, at: dwell - 0.1)
        guard case .hold(let retryAfter) = decision else {
            Issue.record("expected a deferred transition, got \(decision)"); return
        }
        #expect(abs(retryAfter - 0.1) < 0.001)
        #expect(pacer.current == .recording(latched: false), "the old state stands meanwhile")
        // After the dwell, the deferred state lands.
        #expect(pacer.offer(.transcribing, at: dwell + 0.01) == .show(.transcribing))
    }

    @Test("hidden is always immediate")
    func hiddenPreempts() {
        var pacer = DictationHUDPacer()
        _ = pacer.offer(.recording(latched: false), at: 0)
        #expect(pacer.offer(.hidden, at: 0.01) == .show(.hidden))
    }

    @Test("mic-changed pre-empts any dwell")
    func micChangedPreempts() {
        var pacer = DictationHUDPacer()
        _ = pacer.offer(.recording(latched: false), at: 0)
        let warning = DictationHUDState.micChanged("Mic changed — could you say that again?")
        #expect(pacer.offer(warning, at: 0.01) == .show(warning))
    }

    @Test("a latch inside the same hold is immediate, not a state change")
    func latchIsNotAStateChange() {
        var pacer = DictationHUDPacer()
        _ = pacer.offer(.recording(latched: false), at: 0)
        #expect(pacer.offer(.recording(latched: true), at: 0.01) == .show(.recording(latched: true)))
    }

    @Test("an outcome pre-empts any dwell")
    func outcomePreempts() {
        var pacer = DictationHUDPacer()
        _ = pacer.offer(.transcribing, at: 0)
        let receipt = DictationHUDState.outcome(
            DictationHUDContent.outcome(for: .inserted, holdKeyName: "Right Command"))
        #expect(pacer.offer(receipt, at: 0.01) == .show(receipt))
    }

    @Test("a new hold replaces an outcome at once")
    func holdReplacesAnOutcome() {
        var pacer = DictationHUDPacer()
        let receipt = DictationHUDState.outcome(
            DictationHUDContent.outcome(for: .inserted, holdKeyName: "Right Command"))
        _ = pacer.offer(receipt, at: 0)
        #expect(pacer.offer(.recording(latched: false), at: 0.01) == .show(.recording(latched: false)))
    }

    @Test("a fresh state after a hidden interval never dwells")
    func showFromHiddenIsImmediate() {
        var pacer = DictationHUDPacer()
        _ = pacer.offer(.recording(latched: false), at: 0)
        _ = pacer.offer(.hidden, at: 0.1)
        #expect(pacer.offer(.transcribing, at: 0.2) == .show(.transcribing))
    }
}

@MainActor
@Suite("The model's input level lives only for the hold")
struct InputLevelTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("a chunk peak raises the level with a fast attack")
    func levelFollowsTheVoice() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitLevel(0.8)
        #expect(await eventually { model.inputLevel > 0.7 })
        // Quiet chunks decay it rather than strobing it off.
        harness.capture.emitLevel(0.1)
        #expect(await eventually { model.inputLevel < 0.7 && model.inputLevel > 0 })
        harness.trigger.release()
        #expect(await eventually { !model.busy })
    }

    @Test("releasing the hold resets the level to zero")
    func releaseResetsTheLevel() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitLevel(0.9)
        #expect(await eventually { model.inputLevel > 0.8 })
        harness.trigger.release()
        #expect(await eventually { model.inputLevel == 0 })
        #expect(harness.capture.onAudioLevel == nil, "the capture stops reporting too")
    }

    @Test("cancelling the hold resets the level to zero")
    func cancelResetsTheLevel() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitLevel(0.9)
        #expect(await eventually { model.inputLevel > 0.8 })
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { model.inputLevel == 0 && !model.recording })
    }

    @Test("a level arriving outside a hold is ignored")
    func levelOutsideAHoldIsIgnored() async {
        let harness = Harness()
        let model = await enabled(harness)
        #expect(harness.capture.onAudioLevel == nil, "nothing listens while idle")
        model.noteInputLevel(1)
        #expect(model.inputLevel == 0)
        #expect(DictationHUDContent.resolve(recording: model.recording, transcribing: model.transcribing,
                                            latched: model.latched, micChangedNotice: model.micChangedNotice)
            == .hidden)
    }
}
#endif
