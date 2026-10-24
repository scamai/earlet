// The HUD panel's window and the gates in front of it, checked without
// showing it: the flags that keep it from ever taking focus from the field
// being dictated into (read both from the spec and from a panel built the
// way production builds it), the capsule's one busy size and where it
// sits, how it moves under Reduce Motion, how silence looks, the mark
// beside an ending, what VoiceOver hears, which model states reach it and
// when the menu preview holds them back, when a fade may take it off
// screen, how the heartbeat heals a disagreement between the model and the
// window, and what the offscreen render must prove.

#if canImport(Testing)
import AppKit
import Combine
import Foundation
import Testing
@testable import DictationAppCore

/// Every state a publisher delivered, in order.
@MainActor
private final class DeliveredStates {
    private(set) var states: [DictationHUDState] = []
    private var subscription: AnyCancellable?

    init(_ publisher: AnyPublisher<DictationHUDState, Never>) {
        subscription = publisher.sink { [weak self] in self?.states.append($0) }
    }
}

/// Comes back once everything already queued on the main queue has run.
/// The settled HUD states are delivered there and the queue runs in order,
/// so every delivery the test's sends queued has landed by then, however
/// busy the main actor is under `--parallel`; a fixed sleep could be
/// outrun.
@MainActor
private func mainQueueCaughtUp() async {
    await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
        DispatchQueue.main.async { continuation.resume() }
    }
}

/// The mode AppKit runs the main run loop in while a menu is open.
private let eventTracking = CFRunLoopMode(RunLoop.Mode.eventTracking.rawValue as CFString)

/// Gives this test process the event-tracking mode an app has: one of the
/// main run loop's common modes, so the main queue is serviced in it, and
/// with something in it, because running a mode nothing was ever added to
/// returns at once without running anything. The timer never fires.
@MainActor
private func withEventTrackingAsInAnApp<T>(_ body: () async -> T) async -> T {
    CFRunLoopAddCommonMode(CFRunLoopGetMain(), eventTracking)
    let parked = CFRunLoopTimerCreateWithHandler(nil, .greatestFiniteMagnitude, 0, 0, 0) { _ in }
    CFRunLoopAddTimer(CFRunLoopGetMain(), parked, eventTracking)
    defer { CFRunLoopRemoveTimer(CFRunLoopGetMain(), parked, eventTracking) }
    return await body()
}

/// Runs `body` on the main thread from a run-loop callout rather than a
/// main-queue job, the way AppKit starts tracking a menu from a mouse-down:
/// a run loop nested inside a main-queue job never services the main
/// queue, one nested in a callout does.
@MainActor
private func fromARunLoopCallout(_ body: @escaping @MainActor () -> Bool) async -> Bool {
    await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
        CFRunLoopPerformBlock(CFRunLoopGetMain(), CFRunLoopMode.defaultMode.rawValue) {
            continuation.resume(returning: MainActor.assumeIsolated { body() })
        }
        CFRunLoopWakeUp(CFRunLoopGetMain())
    }
}

/// One filterable name for every suite in this file:
/// `swift test --filter DictationHUDPanelTests`.
@Suite("The HUD panel and the gates in front of it")
struct DictationHUDPanelTests {}

extension DictationHUDPanelTests {
    @MainActor
    @Suite("The HUD panel never takes focus and floats above menus")
    struct DictationHUDPanelSpecTests {
        typealias Spec = DictationHUDPanelSpec

        @Test("the spec is non-activating, click-through, stationary and above menus")
        func specFlags() {
            #expect(Spec.styleMask.contains(.nonactivatingPanel))
            #expect(Spec.styleMask.contains(.borderless))
            #expect(!Spec.styleMask.contains(.titled))
            #expect(Spec.ignoresMouseEvents)
            #expect(!Spec.hidesOnDeactivate)
            #expect(Spec.collectionBehavior.contains(.stationary))
            #expect(Spec.collectionBehavior.contains(.canJoinAllSpaces))
            #expect(Spec.collectionBehavior.contains(.fullScreenAuxiliary))
            #expect(Spec.level.rawValue >= NSWindow.Level.screenSaver.rawValue)
            #expect(Spec.level.rawValue > NSWindow.Level.popUpMenu.rawValue, "an open menu must not cover the HUD")
        }

        @Test("the panel production builds carries every flag of the spec")
        func builtPanel() {
            let panel = Spec.makePanel()
            defer { panel.close() }
            #expect(panel.styleMask.contains(.nonactivatingPanel))
            #expect(panel.styleMask.contains(.borderless))
            #expect(panel.ignoresMouseEvents)
            #expect(!panel.hidesOnDeactivate)
            #expect(panel.collectionBehavior.contains(.stationary))
            #expect(panel.collectionBehavior.contains(.canJoinAllSpaces))
            #expect(panel.collectionBehavior.contains(.fullScreenAuxiliary))
            // `isFloatingPanel = true` resets the level to `.floating`, so the
            // order inside `apply(to:)` matters; this reads what it ended at.
            #expect(panel.level.rawValue >= NSWindow.Level.screenSaver.rawValue)
            #expect(!panel.canBecomeKey)
            #expect(!panel.canBecomeMain)
            #expect(panel.isExcludedFromWindowsMenu)
            #expect(panel.animationBehavior == .none)
            #expect(!panel.isOpaque)
            #expect(!panel.hasShadow)
            #expect(!panel.isVisible, "building the panel must not show it")
        }
    }

    @MainActor
    @Suite("Every busy state is one capsule size, with room for its shadow")
    struct DictationHUDMetricsTests {
        typealias Metrics = DictationHUDMetrics
        typealias Content = DictationHUDContent

        nonisolated static let holdKeyNames = DictationHoldKey.allCases.map(\.name)

        /// Every hint a recording or transcribing state can show for a hold of
        /// `limit` seconds, second by second, held and hands-free.
        private func everyBusyHint(holdKeyName: String, limit: TimeInterval) -> Set<String> {
            var hints: Set<String> = []
            for latched in [false, true] {
                let state = DictationHUDState.recording(latched: latched)
                if let plain = Content.hint(for: state, holdKeyName: holdKeyName) { hints.insert(plain) }
                for second in 0...Int(limit) {
                    if let hint = Content.hint(
                        for: state, holdKeyName: holdKeyName,
                        elapsed: TimeInterval(second) + 0.5, limit: limit) {
                        hints.insert(hint)
                    }
                }
            }
            if let hint = Content.hint(for: .transcribing, holdKeyName: holdKeyName) { hints.insert(hint) }
            return hints
        }

        @Test("every line a busy state can show fits the one text column, so nothing resizes",
              arguments: holdKeyNames)
        func everyBusyLineFits(holdKeyName: String) {
            for limit: TimeInterval in [120, 900] {
                let column = Metrics.textColumnWidth(holdKeyName: holdKeyName, maximumHoldSeconds: limit)
                for hint in everyBusyHint(holdKeyName: holdKeyName, limit: limit) {
                    #expect(Metrics.width(of: hint, font: Metrics.hintFont) <= column, "\(hint) at a \(limit) s limit")
                }
                let titles = [
                    Content.title(for: .recording(latched: false)),
                    Content.title(for: .transcribing),
                    Content.recordingTitleReferenceCJK,
                    Content.recordingTitleReferenceWords,
                ].compactMap { $0 }
                for title in titles {
                    #expect(Metrics.width(of: title, font: Metrics.titleFont) <= column, "\(title)")
                }
            }
        }

        @Test("held, hands-free and transcribing share one capsule width")
        func oneWidthForEveryBusyState() {
            let width = Metrics.capsuleWidth(holdKeyName: "Right Command", maximumHoldSeconds: 120)
            let latchedHint = Content.hint(for: .recording(latched: true), holdKeyName: "Right Command",
                                           elapsed: 65, limit: 120) ?? ""
            // The widest busy line sets the width for all of them, including
            // the short Transcribing one.
            #expect(width >= Metrics.width(of: latchedHint, font: Metrics.hintFont)
                    + 2 * Metrics.horizontalPadding + Metrics.iconWidth + Metrics.iconSpacing)
            #expect(width <= Metrics.maximumCapsuleWidth)
        }

        @Test("recording and transcribing are the fixed-size states; endings and the mic change grow")
        func busyStates() {
            #expect(Metrics.isBusy(.recording(latched: false)))
            #expect(Metrics.isBusy(.recording(latched: true)))
            #expect(Metrics.isBusy(.transcribing))
            #expect(!Metrics.isBusy(.micChanged("Mic changed")))
            #expect(!Metrics.isBusy(.outcome(Content.outcome(for: .secureInput, holdKeyName: "Right Command"))),
                    "a fixed width would cut the longest ending off")
            #expect(!Metrics.isBusy(.hidden))
        }

        @Test("the capsule, not its shadow margin, sits 120 pt above the Dock, centred")
        func placement() {
            let visible = CGRect(x: 0, y: 80, width: 1512, height: 900)
            let size = CGSize(width: 400, height: 80)
            let frame = Metrics.panelFrame(size: size, visibleFrame: visible)
            #expect(frame.minY + Metrics.shadowMargin.bottom == visible.minY + Metrics.bottomOffset)
            #expect(Metrics.bottomOffset == 120)
            #expect(frame.midX == visible.midX)
            #expect(frame.size == size)
            #expect(Metrics.shadowMargin.left == Metrics.shadowMargin.right,
                    "a lopsided margin would push the capsule off centre")
        }

        @Test("the shadow margin is deeper than the shadow's fade on every side")
        func marginHoldsTheShadow() {
            // A blur fades out about twice its radius from the edge, plus the
            // offset on the side it falls to; above and below alike, since the
            // offscreen render draws the offset the other way. The render
            // mode's edge check is the measured version of this.
            let margin = Metrics.shadowMargin
            let fade = 2 * Metrics.shadowRadius
            #expect(min(margin.top, margin.bottom) >= fade + Metrics.shadowOffsetY)
            #expect(min(margin.left, margin.right) >= fade)
        }
    }

    @Suite("An ending is marked amber for caution, a quiet dot otherwise, and never red")
    struct DictationHUDGlyphTests {
        @Test("the mark follows the ending's tone", arguments: DictationHUDOutcomeTests.kinds)
        func markFollowsTone(_ kind: DictationHUDOutcome.Kind) {
            let outcome = DictationHUDContent.outcome(for: kind, holdKeyName: "Right Command")
            let glyph = DictationHUDGlyph(state: .outcome(outcome))
            #expect(glyph == (outcome.tone == .caution ? .caution : .neutral), "\(kind)")
        }

        @Test("a mic change is a caution; the live states draw no mark of their own")
        func micChangeAndLiveStates() {
            #expect(DictationHUDGlyph(state: .micChanged("Mic changed")) == .caution)
            #expect(DictationHUDGlyph(state: .recording(latched: false)) == nil)
            #expect(DictationHUDGlyph(state: .transcribing) == nil)
            #expect(DictationHUDGlyph(state: .hidden) == nil)
        }

        @Test("caution is amber, not red or orange-red")
        func cautionIsAmber() {
            let amber = DictationHUDGlyph.caution.rgb
            #expect(amber.red > 0.9)
            #expect(amber.green > 0.65, "a red or orange-red has far less green than amber")
            #expect(amber.blue < 0.3)
        }

        @Test("a neutral ending is not drawn as a success")
        func neutralIsNotACheckmark() {
            #expect(!DictationHUDGlyph.neutral.symbolName.contains("checkmark"))
            #expect(DictationHUDGlyph.neutral.symbolName != DictationHUDGlyph.caution.symbolName)
        }
    }

    @Suite("VoiceOver hears each change once, in constant words, never into an open microphone, and Reduce Motion stills the panel")
    struct DictationHUDAccessibilityTests {
        typealias Spoken = DictationHUDAccessibility

        private func outcome(_ kind: DictationHUDOutcome.Kind) -> DictationHUDState {
            .outcome(DictationHUDContent.outcome(for: kind, holdKeyName: "Right Command"))
        }

        @Test("nothing is announced while the microphone can hear it, and each ending once")
        func silentWhileTheMicrophoneHears() {
            // VoiceOver through the speakers is recorded and typed with the
            // user's words: no "Listening" at the press, none at the latch,
            // and no "Transcribing" while the clip is still filling.
            var announcer = DictationHUDAnnouncer()
            let micChanged = "Mic changed to AirPods Pro — could you say that again?"
            let shown: [DictationHUDState] = [
                .hidden,
                .recording(latched: false),
                .recording(latched: true),
                .transcribing,
                outcome(.inserted),
                outcome(.inserted),           // the same ending presented again
                .hidden,
                .recording(latched: false),
                .transcribing,
                .micChanged(micChanged),
                .hidden,
            ]
            let heard = shown.map { announcer.announcement(for: $0) }
            #expect(heard == [nil, nil, nil, nil, "Inserted", nil, nil, nil, nil, micChanged, nil])
        }

        @Test("only the states with the clip closed may speak")
        func speaksOnlyWithTheClipClosed() {
            #expect(!DictationHUDAnnouncer.speaksIn(.recording(latched: false)))
            #expect(!DictationHUDAnnouncer.speaksIn(.recording(latched: true)))
            #expect(!DictationHUDAnnouncer.speaksIn(.transcribing))
            #expect(DictationHUDAnnouncer.speaksIn(.micChanged("Mic changed")))
            #expect(DictationHUDAnnouncer.speaksIn(outcome(.stillFinishing)))
        }

        @Test("the same ending after the next dictation is heard again")
        func nextDictationsEndingIsHeard() {
            // A press inside the ending's dwell goes straight from the
            // ending to Listening, never through hidden.
            var announcer = DictationHUDAnnouncer()
            let shown: [DictationHUDState] = [
                .transcribing, outcome(.inserted),
                .recording(latched: false), .transcribing, outcome(.inserted),
            ]
            let heard = shown.map { announcer.announcement(for: $0) }
            #expect(heard == [nil, "Inserted", nil, nil, "Inserted"])
        }

        @Test("the same words after the HUD hid are announced again")
        func hidingResets() {
            // Two presses while the last dictation is still finishing, the
            // first refusal's dwell having run out between them.
            var announcer = DictationHUDAnnouncer()
            let refused = outcome(.stillFinishing)
            let heard = [refused, .hidden, refused].map { announcer.announcement(for: $0) }
            #expect(heard[0] != nil)
            #expect(heard[1] == nil)
            #expect(heard[2] == heard[0], "the second refusal must be heard too")
        }

        @Test("the spoken label is the state's alone: no preview, no level, no clock")
        func labelsAreConstant() {
            #expect(Spoken.label(for: .recording(latched: false)) == "Listening")
            #expect(Spoken.label(for: .recording(latched: true)) == "Listening, hands-free")
            #expect(Spoken.label(for: .transcribing) == "Transcribing")
            #expect(Spoken.label(for: .hidden) == nil)
        }

        @Test("an ending is spoken with the line under it", arguments: DictationHUDOutcomeTests.kinds)
        func endingsAreSpokenWhole(_ kind: DictationHUDOutcome.Kind) {
            let words = DictationHUDContent.outcome(for: kind, holdKeyName: "Right Command")
            let spoken = Spoken.label(for: .outcome(words)) ?? ""
            #expect(spoken.hasPrefix(words.title))
            if let detail = words.detail { #expect(spoken.hasSuffix(detail)) }
        }

        @Test("the clipboard ending tells a VoiceOver user where the words are")
        func clipboardIsSpoken() {
            #expect(Spoken.label(for: outcome(.keptOnClipboard))
                == "Couldn't type here. Your words are on the clipboard — press ⌘V.")
        }

        @Test("Reduce Motion, from either source, drops the rise and the frame's ease")
        func reduceMotion() {
            let still = DictationHUDMotion(reduceMotion: true)
            #expect(still.entranceRise == 0)
            #expect(!still.easesFrame)
            let moving = DictationHUDMotion(reduceMotion: false)
            #expect(moving.entranceRise > 0)
            #expect(moving.easesFrame)
            #expect(DictationHUDMotion.isReduced(environment: true, system: false))
            #expect(DictationHUDMotion.isReduced(environment: false, system: true))
            #expect(!DictationHUDMotion.isReduced(environment: false, system: false))
        }
    }

    @Suite("Silence is a flat line, not a row of dots; speech is the bars")
    struct DictationHUDWaveformShapeTests {
        typealias Shape = DictationHUDWaveformShape

        @Test("digital silence and room tone under the floor draw one flat line")
        func silenceIsAFlatLine() {
            #expect(Shape(peak: 0).isFlatLine)
            // -60 dB, well under the -45 dB floor.
            #expect(Shape(peak: 0.001).isFlatLine)
        }

        @Test("ordinary speech draws the bars, raised off the floor")
        func speechDrawsBars() {
            let shape = Shape(peak: 0.08)
            #expect(!shape.isFlatLine)
            #expect(shape.barHeights.allSatisfy { $0 > DictationHUDContent.waveformMinHeight })
        }

        @Test("the flat line spans the bars, and the glide is one level sample")
        func lineAndGlide() {
            let count = CGFloat(DictationHUDContent.waveformBarCount)
            #expect(Shape.width == Shape.barWidth * count + Shape.barSpacing * (count - 1))
            #expect(Shape.flatLineHeight < DictationHUDContent.waveformMinHeight,
                    "a line as tall as a floor bar would read as dots again")
            #expect(Shape.glideSeconds > 0 && Shape.glideSeconds <= 0.1)
        }
    }

    @MainActor
    @Suite("The panel shows where a model turn ended, and a stale fade never hides it")
    struct DictationHUDFollowTests {
        private let listening = DictationHUDState.recording(latched: false)

        @Test("a state the model only passed through inside one turn never reaches the panel")
        func settledShowsWhereATurnEnded() async {
            let model = CurrentValueSubject<DictationHUDState, Never>(.hidden)
            let delivered = DeliveredStates(model.settled())
            await mainQueueCaughtUp()
            model.send(listening)
            await mainQueueCaughtUp()
            // One turn: the hold stops recording a moment before it publishes
            // Transcribing.
            model.send(.hidden)
            model.send(.transcribing)
            await mainQueueCaughtUp()
            model.send(.hidden)
            await mainQueueCaughtUp()
            #expect(delivered.states == [.hidden, listening, .transcribing, .hidden])
        }

        @Test("a turn that ends where it started is no change at all")
        func settledDropsANoOpTurn() async {
            let model = CurrentValueSubject<DictationHUDState, Never>(.hidden)
            let delivered = DeliveredStates(model.settled())
            await mainQueueCaughtUp()
            model.send(listening)
            model.send(.hidden)
            await mainQueueCaughtUp()
            #expect(delivered.states == [.hidden])
        }

        @Test("a hold started with a menu open is shown while the menu is still open",
              .timeLimit(.minutes(1)))
        func settledDeliversWhileAMenuIsOpen() async {
            let model = CurrentValueSubject<DictationHUDState, Never>(.hidden)
            let delivered = DeliveredStates(model.settled())
            await mainQueueCaughtUp()
            let shownWithTheMenuOpen = await withEventTrackingAsInAnApp {
                await fromARunLoopCallout {
                    // The hold key goes down while AppKit tracks the menu.
                    model.send(listening)
                    let menuCloses = Date().addingTimeInterval(2)
                    while delivered.states.last != listening, Date() < menuCloses {
                        _ = CFRunLoopRunInMode(eventTracking, 0.05, true)
                    }
                    return delivered.states.last == listening
                }
            }
            #expect(shownWithTheMenuOpen, "the HUD waited for the menu to close")
            await mainQueueCaughtUp()
            #expect(delivered.states == [.hidden, listening])
        }

        @Test("a fade that a newer show overtook never orders the panel out")
        func overtakenFadeKeepsThePanel() {
            var gate = DictationHUDHideGate()
            let fade = gate.present()          // a hide starts its fade…
            _ = gate.present()                 // …and the next state is shown first
            #expect(!gate.mayOrderOut(hideGeneration: fade, state: .transcribing))
            #expect(!gate.mayOrderOut(hideGeneration: fade, state: listening))
        }

        @Test("after hide, show, hide, only the second fade may order the panel out")
        func onlyTheLatestFadeOrdersOut() {
            var gate = DictationHUDHideGate()
            let first = gate.present()
            _ = gate.present()
            let second = gate.present()
            #expect(!gate.mayOrderOut(hideGeneration: first, state: .hidden),
                    "the first fade would cut the second one short")
            #expect(gate.mayOrderOut(hideGeneration: second, state: .hidden))
        }

        @Test("the latest fade orders the panel out only while the HUD is hidden")
        func latestFadeNeedsHidden() {
            var gate = DictationHUDHideGate()
            let fade = gate.present()
            #expect(gate.mayOrderOut(hideGeneration: fade, state: .hidden))
            #expect(!gate.mayOrderOut(hideGeneration: fade, state: listening))
        }
    }

    @Suite("The menu preview holds the model's states back and hands the panel back")
    struct DictationHUDPreviewGateTests {
        private let listening = DictationHUDState.recording(latched: false)

        @Test("outside a preview every model state goes straight to the panel")
        func noPreviewPassesThrough() {
            var gate = DictationHUDPreviewGate()
            #expect(gate.modelOffer(listening) == listening)
            #expect(gate.modelOffer(.transcribing) == .transcribing)
            #expect(!gate.isRunning)
        }

        @Test("model states wait while the preview runs, and the latest is offered when it ends")
        func previewHoldsModelStates() {
            var gate = DictationHUDPreviewGate()
            _ = gate.modelOffer(.hidden)
            gate.begin()
            #expect(gate.modelOffer(listening) == nil)
            #expect(gate.modelOffer(.transcribing) == nil)
            #expect(gate.end() == .transcribing)
            #expect(gate.modelOffer(.hidden) == .hidden, "the model owns the panel again")
        }

        @Test("a hold that started during the preview is shown when it ends, not hidden")
        func holdDuringPreviewSurvivesItsEnd() {
            var gate = DictationHUDPreviewGate()
            gate.begin()
            _ = gate.modelOffer(.recording(latched: true))
            #expect(gate.end() == .recording(latched: true))
        }

        @Test("a preview with nothing dictated during it hands back hidden")
        func idlePreviewEndsHidden() {
            var gate = DictationHUDPreviewGate()
            gate.begin()
            #expect(gate.end() == .hidden)
        }
    }

    @Suite("The reconcile heartbeat heals a panel the model and the window disagree on")
    struct DictationHUDReconcileTests {
        @Test("a visible state the panel is not showing is offered again")
        func reoffer() {
            #expect(DictationHUDReconcile.action(desiredHidden: false, panelVisible: false) == .reoffer)
        }

        @Test("a panel on screen for a hidden model is taken away")
        func orderOut() {
            #expect(DictationHUDReconcile.action(desiredHidden: true, panelVisible: true) == .orderOut)
        }

        @Test("agreement is left alone")
        func agreement() {
            #expect(DictationHUDReconcile.action(desiredHidden: false, panelVisible: true) == nil)
            #expect(DictationHUDReconcile.action(desiredHidden: true, panelVisible: false) == nil)
        }

        @Test("the heartbeat outruns a user noticing, and the fade grace is clear of the 0.14 s fade")
        func timing() {
            #expect(DictationHUDReconcile.intervalSeconds <= 2)
            #expect(DictationHUDReconcile.fadeStaleAfterSeconds > 0.14 * 5)
        }
    }

    @Suite("The offscreen render fails when a shadow is cut or a busy state resizes")
    struct DictationHUDRenderCheckTests {
        typealias Check = DictationHUDRenderCheck
        typealias Rendered = DictationHUDRenderCheck.Rendered

        private func image(_ name: String, _ width: Int, _ height: Int, busy: Bool = true, edge: Int = 0) -> Rendered {
            Rendered(name: name, width: width, height: height, isBusy: busy, edgeAlpha: edge)
        }

        @Test("transparent edges and one busy size pass")
        func passes() {
            #expect(Check.problems([
                image("recording", 396, 92), image("transcribing", 396, 92),
                image("outcome-secure-input", 424, 92, busy: false),
            ]).isEmpty)
        }

        @Test("a shadow cut at any image's edge fails, busy or not")
        func cutShadowFails() {
            let problems = Check.problems([
                image("recording", 396, 92), image("transcribing", 396, 92),
                image("mic-changed", 447, 92, busy: false, edge: 39),
            ])
            #expect(problems.count == 1)
            #expect(problems.first?.hasPrefix("mic-changed") == true)
        }

        @Test("busy states of different sizes fail")
        func resizingFails() {
            let problems = Check.problems([image("recording", 266, 52), image("recording-latched", 325, 52)])
            #expect(problems.count == 1)
        }

        @Test("too little to compare is never a pass")
        func emptyIsNotAPass() {
            #expect(!Check.problems([]).isEmpty)
            #expect(!Check.problems([image("recording", 396, 92)]).isEmpty)
        }

        @Test("the edge reading covers all four edges and nothing inside")
        func edgeReading() {
            // A 5 x 4 image, opaque inside; one edge pixel at a time.
            func edge(lit: (Int, Int)?) -> Int {
                Check.edgeAlpha(width: 5, height: 4) { x, y in
                    if (x, y) == lit ?? (-1, -1) { return 7 }
                    return (1...3).contains(x) && (1...2).contains(y) ? 255 : 0
                }
            }
            #expect(edge(lit: nil) == 0, "the opaque inside is not an edge")
            for lit in [(2, 0), (2, 3), (0, 1), (4, 2), (0, 0), (4, 3)] {
                #expect(edge(lit: lit) == 7, "\(lit)")
            }
        }
    }
}
#endif
