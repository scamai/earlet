// The menu bar icon is the only thing on screen with the window closed, so
// every state draws its own glyph: paused, ready and a lost permission used
// to look the same.

#if canImport(Testing)
import AppKit
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("The menu bar glyph")
struct MenuGlyphTests {

    /// Every input false except the ones a case names.
    private func resolve(
        enabled: Bool = false, busy: Bool = false, recording: Bool = false,
        transcribing: Bool = false, allPermissionsGranted: Bool = true,
        reopenNeeded: Bool = false, engineFailed: Bool = false
    ) -> DictationMenuGlyph {
        .resolve(enabled: enabled, busy: busy, recording: recording, transcribing: transcribing,
                 allPermissionsGranted: allPermissionsGranted, reopenNeeded: reopenNeeded,
                 engineFailed: engineFailed)
    }

    @Test("paused is the crossed-out microphone")
    func paused() {
        let glyph = resolve()
        #expect(glyph == .paused)
        #expect(glyph.symbolName == "mic.slash")
        #expect(glyph.accessibilityLabel == "Earlet, off")
        #expect(glyph.headline == "Earlet is off")
    }

    @Test("an Enable still loading the engine is starting, and the microphone stays crossed out")
    func starting() {
        let glyph = resolve(busy: true)
        #expect(glyph == .starting)
        #expect(glyph.symbolName == "mic.slash")
        #expect(glyph.headline == "Starting dictation…")
    }

    @Test("enabled and idle is the outline microphone")
    func ready() {
        let glyph = resolve(enabled: true)
        #expect(glyph == .ready)
        #expect(glyph.symbolName == "mic")
        #expect(glyph.accessibilityLabel == "Earlet, on")
    }

    @Test("a release or a pause in progress while enabled still reads as on")
    func busyWhileEnabledIsReady() {
        #expect(resolve(enabled: true, busy: true) == .ready)
    }

    @Test("recording is the filled microphone")
    func recording() {
        let glyph = resolve(enabled: true, busy: true, recording: true)
        #expect(glyph == .recording)
        #expect(glyph.symbolName == "mic.fill")
    }

    @Test("transcribing is the waveform")
    func transcribing() {
        let glyph = resolve(enabled: true, busy: true, transcribing: true)
        #expect(glyph == .transcribing)
        #expect(glyph.symbolName == "waveform")
    }

    @Test("a missing grant, a reopen and a failed engine all draw the warning triangle")
    func needsAttention() {
        let permission = resolve(enabled: true, allPermissionsGranted: false)
        let reopen = resolve(reopenNeeded: true)
        let engine = resolve(engineFailed: true)
        #expect(permission == .needsPermission)
        #expect(reopen == .needsReopen)
        #expect(engine == .engineFailed)
        for glyph in [permission, reopen, engine] {
            #expect(glyph.symbolName == "exclamationmark.triangle")
            #expect(glyph.needsAttention)
        }
        #expect(permission.headline == "Needs permission")
    }

    @Test("recording beats needs-attention: a live hold proves the grants were there")
    func recordingBeatsAttention() {
        #expect(resolve(enabled: true, recording: true, allPermissionsGranted: false) == .recording)
        #expect(resolve(enabled: true, transcribing: true, reopenNeeded: true) == .transcribing)
        #expect(resolve(enabled: true, recording: true, engineFailed: true) == .recording)
    }

    @Test("needs-attention beats paused and ready, so a broken setup never looks fine")
    func attentionBeatsQuietStates() {
        #expect(resolve(allPermissionsGranted: false) == .needsPermission)
        #expect(resolve(enabled: true, allPermissionsGranted: false) == .needsPermission)
        #expect(resolve(busy: true, allPermissionsGranted: false) == .needsPermission)
        #expect(resolve(enabled: true, reopenNeeded: true) == .needsReopen)
        #expect(resolve(engineFailed: true) == .engineFailed)
    }

    @Test("a missing grant outranks a pending reopen: the checklist row comes first")
    func permissionBeatsReopen() {
        #expect(resolve(allPermissionsGranted: false, reopenNeeded: true) == .needsPermission)
    }

    @Test("paused, ready and needs-attention are three different icons")
    func quietStatesAreDistinct() {
        let symbols = Set([DictationMenuGlyph.paused, .ready, .needsPermission].map(\.symbolName))
        #expect(symbols.count == 3)
        let labels = Set(DictationMenuGlyph.allCases.map(\.accessibilityLabel))
        #expect(labels.count == DictationMenuGlyph.allCases.count, "VoiceOver tells every state apart")
    }

    @Test("every glyph is a symbol this Mac has: an unknown name draws an empty menu bar item")
    func symbolsExist() {
        for glyph in DictationMenuGlyph.allCases {
            #expect(NSImage(systemSymbolName: glyph.symbolName, accessibilityDescription: nil) != nil,
                    "\(glyph): \(glyph.symbolName)")
        }
    }

    @Test("the menu headline fits one menu line for every state and every hold key")
    func menuHeadlineIsShort() {
        for glyph in DictationMenuGlyph.allCases {
            for key in DictationHoldKey.presets {
                let line = glyph.menuHeadline(holdKeyName: key.name)
                #expect(!line.isEmpty)
                #expect(line.count <= DictationMenuGlyph.menuHeadlineLimit, "\(line)")
            }
        }
        #expect(DictationMenuGlyph.ready.menuHeadline(holdKeyName: "Right Command") == "On — hold Right Command")
        #expect(DictationMenuGlyph.paused.menuHeadline(holdKeyName: "Right Command") == "Earlet is off")
    }

    @Test("the waiting badge: words outrank a recording, and VoiceOver hears it after the state")
    func waitingBadge() {
        #expect(DictationMenuWaiting.resolve(wordsWaiting: false, recordingSaved: false) == nil)
        #expect(DictationMenuWaiting.resolve(wordsWaiting: true, recordingSaved: false) == .words)
        #expect(DictationMenuWaiting.resolve(wordsWaiting: false, recordingSaved: true) == .recording)
        #expect(DictationMenuWaiting.resolve(wordsWaiting: true, recordingSaved: true) == .words)
        let quiet = DictationMenuIconState(glyph: .ready, waiting: nil)
        #expect(!quiet.badged)
        #expect(quiet.accessibilityLabel == "Earlet, on")
        let words = DictationMenuIconState(glyph: .ready, waiting: .words)
        #expect(words.badged)
        #expect(words.accessibilityLabel == "Earlet, on, words waiting")
        #expect(DictationMenuIconState(glyph: .needsPermission, waiting: .recording).accessibilityLabel
                == "Earlet, needs permission, recording waiting")
        let phrases = Set(DictationMenuWaiting.allCases.map(\.accessibilityPhrase))
        #expect(phrases.count == DictationMenuWaiting.allCases.count, "VoiceOver tells the two apart")
    }
}

@MainActor
@Suite("The menu bar glyph follows the model")
struct ModelMenuGlyphTests {

    @Test("paused, starting, on, listening, transcribing and back to on, through the model",
          .timeLimit(.minutes(1)))
    func lifecycle() async {
        var harness = Harness()
        harness.timing.startSeconds = 30
        harness.timing.transcribeSeconds = 30
        harness.workers.configure = { $0.state.mutate { $0.startReply = .hang } }
        let model = harness.model()
        model.refreshPermissions()
        #expect(model.menuGlyph == .paused)

        let enabling = Task { await model.toggle() }
        #expect(await eventually { harness.workers.made.first?.state.get.parkedStarts.count == 1 })
        #expect(model.menuGlyph == .starting)
        harness.workers.made[0].finishStart(true)
        await enabling.value
        #expect(model.menuGlyph == .ready)

        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(model.menuGlyph == .recording)
        harness.trigger.release()
        #expect(await eventually { model.transcribing })
        #expect(model.menuGlyph == .transcribing)
        // Parked in the worker before it is released: `transcribing` turns
        // on at the release, a moment before the request reaches the worker.
        #expect(await eventually { worker.state.get.transcribes == 1 })
        worker.finishTranscribe("hello")
        #expect(await eventually { !model.busy && !model.transcribing })
        #expect(model.menuGlyph == .ready)
    }

    @Test("a grant revoked while enabled turns the icon into the warning at the next poll")
    func revokedWhileEnabled() async {
        var harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.menuGlyph == .ready)
        harness.inputMonitoringGranted = false
        model.refreshPermissions()
        #expect(model.enabled, "the model stays enabled; only the icon can say it no longer works")
        #expect(model.menuGlyph == .needsPermission)
        harness.inputMonitoringGranted = true
        model.refreshPermissions()
        #expect(model.menuGlyph == .ready)
    }

    @Test("first run, before any grant, is needs-permission rather than paused")
    func firstRun() {
        var harness = Harness()
        harness.microphoneGranted = false
        let model = harness.model()
        model.refreshPermissions()
        #expect(model.menuGlyph == .needsPermission)
        #expect(model.menuGlyph.menuHeadline(holdKeyName: model.holdKey.name) == "Needs permission")
        #expect(model.homeNeedsAttention)
        #expect(model.menuIcon == DictationMenuIconState(glyph: .needsPermission, waiting: nil),
                "a missing grant marks Home but not the badge: the triangle already says it")
    }

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    /// One press and release.
    private func dictate(_ harness: Harness, _ model: DictationModel, audio: Bool = false) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        if audio { harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000)) }
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("words left after a refused paste badge the icon until they are dismissed", .timeLimit(.minutes(1)))
    func wordsWaitingBadgeTheIcon() async {
        let harness = Harness()
        harness.clipboardWorks.mutate { $0 = false }
        harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(model.recentResult.entry?.needsRecovery == true)
        #expect(model.menuIcon == DictationMenuIconState(glyph: .ready, waiting: .words))
        #expect(model.menuIcon.accessibilityLabel == "Earlet, on, words waiting")
        #expect(model.homeNeedsAttention, "the same signal as Home's rail mark")
        await model.dismissRecentResult()
        #expect(model.menuIcon == DictationMenuIconState(glyph: .ready, waiting: nil))
    }

    @Test("a recording kept for Recover Last Dictation badges the icon until it is discarded",
          .timeLimit(.minutes(1)))
    func savedRecordingBadgesTheIcon() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate { $0.replies = [.failure(.engineError("bad audio"))] }
        await dictate(harness, model, audio: true)
        #expect(model.savedTake != nil)
        #expect(model.menuIcon.waiting == .recording)
        #expect(model.menuIcon.accessibilityLabel.hasSuffix(", recording waiting"))
        await model.discardSavedTake()
        #expect(model.savedTake == nil)
        #expect(!model.menuIcon.badged)
    }

    @Test("an inserted dictation, kept to copy again, leaves the icon unbadged", .timeLimit(.minutes(1)))
    func insertedResultLeavesTheIconQuiet() async {
        let harness = Harness()
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(model.recentResult.entry?.needsRecovery == false)
        #expect(model.menuIcon == DictationMenuIconState(glyph: .ready, waiting: nil))
    }
}
#endif

#if canImport(Testing)
@MainActor
@Suite("The menu's order")
struct MenuLayoutTests {

    @Test("idle: toggle, then Show, Settings… and the log, then the preview and Quit last")
    func idleOrder() async {
        let harness = Harness()
        let model = harness.model()
        let entries = DictationMenuLayout.entries(for: model.menuItems)
        #expect(entries == [
            .item(.toggle),
            .separator,
            .item(.showWindow), .settings, .item(.openLogFolder),
            .separator,
            .previewIndicator, .item(.quit),
        ])
    }

    @Test("while recording, Cancel sits with the toggle, above the first separator",
          .timeLimit(.minutes(1)))
    func recordingOrder() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.trigger.press()
        #expect(await eventually { model.recording })
        let entries = DictationMenuLayout.entries(for: model.menuItems)
        #expect(Array(entries.prefix(3)) == [.item(.toggle), .item(.cancel), .separator])
        await model.cancel()
        #expect(await eventually { !model.recording })
        harness.trigger.release()
    }

    @Test("Quit is the last line and nothing follows it")
    func quitIsLast() {
        let entries = DictationMenuLayout.entries(for: [.toggle, .showWindow, .openLogFolder, .quit])
        #expect(entries.last == .item(.quit))
        #expect(entries.firstIndex(of: .previewIndicator) == entries.count - 2)
    }
}
#endif

#if canImport(Testing)
@MainActor
@Suite("The window comes forward when something needs the user")
struct AppWindowAttentionTests {

    private func observe(_ attention: inout DictationWindowAttention, _ model: DictationModel) -> Bool {
        attention.observe(windowAttention: model.windowAttention,
                          allPermissionsGranted: model.allPermissionsGranted)
    }

    @Test("a grant revoked while enabled brings the window forward once, and again after a re-grant and a second loss")
    func revokedGrant() async {
        var harness = Harness()
        let model = harness.model()
        var attention = DictationWindowAttention()
        model.refreshPermissions()
        #expect(!observe(&attention, model), "every grant arriving is not a reason to interrupt")
        await model.toggle()
        #expect(!observe(&attention, model))

        harness.accessibilityGranted = false
        model.refreshPermissions()
        #expect(observe(&attention, model))
        model.refreshPermissions()
        #expect(!observe(&attention, model), "a grant that stays missing asks once; closing the window is respected")

        harness.accessibilityGranted = true
        model.refreshPermissions()
        #expect(!observe(&attention, model))
        harness.inputMonitoringGranted = false
        model.refreshPermissions()
        #expect(observe(&attention, model))
    }

    @Test("a refused key listener (windowAttention) brings the window forward")
    func degradedListener() async {
        let harness = Harness()
        let model = harness.model()
        var attention = DictationWindowAttention()
        await model.toggle()
        #expect(!observe(&attention, model))
        harness.trigger.degrade(.missingInputMonitoring)
        #expect(model.windowAttention == 1)
        #expect(observe(&attention, model))
        #expect(!observe(&attention, model), "one bump, one request")
    }

    @Test("a first run that starts without grants is not a loss")
    func firstRunIsNotALoss() {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        var attention = DictationWindowAttention()
        model.refreshPermissions()
        #expect(!observe(&attention, model))
        model.refreshPermissions()
        #expect(!observe(&attention, model))
    }
}
#endif

#if canImport(Testing)
@MainActor
@Suite("The app's permission poll")
struct PermissionWatchTests {

    @Test("once a second while setting up; every five seconds once on with every grant")
    func cadence() {
        let fast = DictationPermissionWatch.settingUp, slow = DictationPermissionWatch.steady
        #expect(fast == .seconds(1))
        #expect(DictationPermissionWatch.interval(enabled: false, allPermissionsGranted: true, reopenNeeded: false) == fast,
                "off: an Enable may be waiting for the grants")
        #expect(DictationPermissionWatch.interval(enabled: false, allPermissionsGranted: false, reopenNeeded: false) == fast)
        #expect(DictationPermissionWatch.interval(enabled: true, allPermissionsGranted: false, reopenNeeded: false) == fast)
        #expect(DictationPermissionWatch.interval(enabled: true, allPermissionsGranted: true, reopenNeeded: true) == fast)
        #expect(DictationPermissionWatch.interval(enabled: true, allPermissionsGranted: true, reopenNeeded: false) == slow)
        #expect(slow > fast)
    }

    @Test("with no window at all, the poll finishes an Enable once the last grant arrives",
          .timeLimit(.minutes(1)))
    func finishesPendingEnable() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        harness.permissionCalls.mutate { $0 = [] }
        let watch = Task { await model.watchPermissionsForLife() }
        defer { watch.cancel() }
        // Past the first tick, so the grant is seen by a later one.
        #expect(await eventually { harness.permissionCalls.get.contains("im?") })
        harness.inputMonitoringGranted = true
        #expect(await eventually(5) { model.enabled })
    }

    @Test("while enabled, a revoked grant is noticed by the poll without any window",
          .timeLimit(.minutes(1)))
    func noticesRevocation() async {
        var harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.permissionCalls.mutate { $0 = [] }
        let watch = Task { await model.watchPermissionsForLife() }
        defer { watch.cancel() }
        // Past the first tick: the revocation lands during the steady sleep,
        // and the next tick, five seconds on, must still see it.
        #expect(await eventually { harness.permissionCalls.get.contains("im?") })
        #expect(model.menuGlyph == .ready)
        harness.inputMonitoringGranted = false
        #expect(await eventually(8) { model.menuGlyph == .needsPermission })
    }
}
#endif

#if canImport(Testing)
@MainActor
@Suite("A login launch stays in the menu bar when dictation is ready")
struct LaunchWindowTests {

    @Test("only a login launch, left on, with every grant in, starts without the window")
    func onlyReadyLoginLaunchIsQuiet() {
        #expect(!DictationLaunchWindow.showsWindow(loginLaunch: true, enabledLastTime: true, allPermissionsGranted: true))
        #expect(DictationLaunchWindow.showsWindow(loginLaunch: false, enabledLastTime: true, allPermissionsGranted: true),
                "opened by the user: show what they opened")
        #expect(DictationLaunchWindow.showsWindow(loginLaunch: true, enabledLastTime: false, allPermissionsGranted: true),
                "paused at login: the window is the only way to see it is off")
        #expect(DictationLaunchWindow.showsWindow(loginLaunch: true, enabledLastTime: true, allPermissionsGranted: false),
                "a missing grant is the one thing the user must see")
    }

    @Test("a quiet launch whose enable succeeds stays quiet; one that fails brings the window forward",
          .timeLimit(.minutes(1)))
    func revealOnFailure() async {
        let ready = Harness()
        ready.settings.enabled = true
        let model = ready.model()
        model.refreshPermissions()
        let quiet = !DictationLaunchWindow.showsWindow(
            loginLaunch: true, enabledLastTime: ready.settings.enabled,
            allPermissionsGranted: model.allPermissionsGranted)
        #expect(quiet)
        await model.launch()
        #expect(model.enabled)
        #expect(!DictationLaunchWindow.revealsAfterLaunch(wasQuiet: quiet, enabled: model.enabled))

        var broken = Harness()
        broken.resources = nil
        broken.settings.enabled = true
        let failing = broken.model()
        await failing.launch()
        #expect(!failing.enabled)
        #expect(DictationLaunchWindow.revealsAfterLaunch(wasQuiet: true, enabled: failing.enabled))
        #expect(!DictationLaunchWindow.revealsAfterLaunch(wasQuiet: false, enabled: failing.enabled),
                "a window already on screen needs no second request")
    }
}
#endif
