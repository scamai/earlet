// The main window's rail: its panes and order, the keycap Home draws for
// the hold key, and the mark that keeps Home's unfinished business visible
// from the other panes. Only the scratch spool and the existing fakes are
// used; no window is drawn (the render mode draws it).

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Window panes")
struct WindowPaneTests {
    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func dictate(_ harness: Harness, _ model: DictationModel, audio: Bool = false) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        if audio { harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000)) }
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("the rail is Home, History, Vocabulary, Settings, each with its own title and symbol")
    func panesInOrder() {
        #expect(DictationWindowPane.allCases == [.home, .history, .vocabulary, .settings])
        #expect(DictationWindowPane.allCases.map(\.title) == ["Home", "History", "Vocabulary", "Settings"])
        let symbols = DictationWindowPane.allCases.map(\.symbolName)
        #expect(Set(symbols).count == symbols.count)
        #expect(DictationWindowNavigation().pane == .home, "a fresh window opens on Home")
    }

    @Test("the keycap names the side and the glyph on the key, and fn has no side")
    func keycapLabels() {
        let expected: [DictationHoldKey: String] = [
            .rightCommand: "Right ⌘", .rightOption: "Right ⌥", .leftOption: "Left ⌥",
            .rightControl: "Right ⌃", .leftControl: "Left ⌃", .rightShift: "Right ⇧", .function: "fn",
        ]
        #expect(DictationHoldKey.allCases.count == expected.count)
        for key in DictationHoldKey.allCases {
            #expect(key.keycapLabel == expected[key], "\(key)")
        }
    }

    @Test("Home is marked while a grant is missing, and only then on a first run")
    func missingGrantMarksHome() {
        var harness = Harness()
        harness.microphoneGranted = false
        harness.accessibilityGranted = false
        harness.inputMonitoringGranted = false
        let model = harness.model()
        model.refreshPermissions()
        #expect(model.homeNeedsAttention)
        harness.microphoneGranted = true
        harness.accessibilityGranted = true
        harness.inputMonitoringGranted = true
        model.refreshPermissions()
        #expect(!model.homeNeedsAttention, "every grant in and not yet enabled is nothing to fix")
    }

    @Test("on, or paused by the user, Home is not marked; a grant revoked while on marks it")
    func onAndPausedAreQuiet() async {
        var harness = Harness()
        let model = await enabled(harness)
        #expect(!model.homeNeedsAttention)
        await model.toggle()
        #expect(!model.enabled)
        #expect(!model.homeNeedsAttention, "paused is the user's choice, not something to fix")
        await model.toggle()
        harness.accessibilityGranted = false
        model.refreshPermissions()
        #expect(model.homeNeedsAttention)
    }

    @Test("words left after a refused paste mark Home until they are dismissed")
    func refusedPasteMarksHome() async {
        let harness = Harness()
        harness.clipboardWorks.mutate { $0 = false }
        harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(model.recentResult.entry?.needsRecovery == true)
        #expect(model.homeNeedsAttention)
        await model.dismissRecentResult()
        #expect(!model.homeNeedsAttention)
    }

    @Test("an inserted result is kept to copy again, and does not mark Home")
    func insertedResultIsQuiet() async {
        let harness = Harness()
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(model.recentResult.entry?.needsRecovery == false)
        #expect(!model.homeNeedsAttention)
    }

    @Test("a recording kept for Recover Last Dictation marks Home until it is discarded")
    func savedTakeMarksHome() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate { $0.replies = [.failure(.engineError("bad audio"))] }
        await dictate(harness, model, audio: true)
        #expect(model.savedTake != nil)
        #expect(model.homeNeedsAttention)
        await model.discardSavedTake()
        #expect(model.savedTake == nil)
        #expect(!model.homeNeedsAttention)
    }
}
#endif
