// The live permission checklist: its state mirrors the grants, each row
// grants or opens a pane, the poll finishes an Enable click that was waiting
// on a grant and turns a first run on by itself (G16), and a degraded key
// listener says which access was lost.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Permission setup exposes one useful next action")
struct PermissionSetupTests {
    @Test("permission names match the settings page on both older Macs and macOS 27")
    func settingsNamesMatchTheOS() {
        #expect(DictationPermissionStep.accessibilitySettingsName(majorVersion: 26) == "Accessibility")
        #expect(DictationPermissionStep.accessibilitySettingsName(majorVersion: 27) == "Device Control and Data Access")
    }

    @Test("double-clicking while the microphone prompt is open creates only one request")
    func oneOutstandingRequest() async {
        var harness = Harness()
        harness.microphoneGranted = false
        var environment = harness.environment()
        var continuation: CheckedContinuation<Bool, Never>?
        var requests = 0
        environment.permissions.requestMicrophone = {
            requests += 1
            return await withCheckedContinuation { continuation = $0 }
        }
        let model = DictationModel(environment: environment)
        let first = Task { await model.continuePermissionSetup() }
        #expect(await eventually { continuation != nil })
        await model.continuePermissionSetup()
        #expect(requests == 1)
        #expect(model.permissionRequestInProgress)
        continuation?.resume(returning: false)
        await first.value
        #expect(!model.permissionRequestInProgress)
        #expect(model.permissionStep == .microphone)
    }

    @Test("each click requests only the next missing permission; polling never requests the next")
    func sequentialSetup() async {
        var harness = Harness()
        harness.microphoneGranted = false
        harness.accessibilityGranted = false
        harness.inputMonitoringGranted = false
        let model = harness.model()
        await model.continuePermissionSetup()
        #expect(model.permissionStep == .microphone)
        #expect(!model.permissionRequestInProgress)
        #expect(!harness.permissionCalls.get.contains("ax!"))
        #expect(!harness.permissionCalls.get.contains("im!"))
        harness.microphoneGranted = true
        await model.permissionTick()
        #expect(model.permissionStep == .accessibility)
        #expect(!harness.permissionCalls.get.contains("ax!"))
        await model.continuePermissionSetup()
        #expect(harness.permissionCalls.get.contains("ax-pane"))
        #expect(!harness.permissionCalls.get.contains("im!"))
        harness.accessibilityGranted = true
        model.refreshPermissions()
        #expect(model.permissionStep == .inputMonitoring)
        await model.continuePermissionSetup()
        #expect(harness.permissionCalls.get.contains("im!"))
        harness.inputMonitoringGranted = true
        await model.permissionTick()
        #expect(model.permissionStep == .ready)
        #expect(model.enabled, "the last grant of a first run turns dictation on (G16)")
        #expect(harness.trigger.running)
        #expect(!model.recording, "on is ready for the hold key, never a recording")
    }

    @Test("a refused event tap offers restart even when the permission probes all say yes")
    func staleGrantOffersRecovery() async {
        let harness = Harness()
        harness.trigger.startResult = false
        let model = harness.model()
        await model.toggle()
        model.refreshPermissions()
        #expect(model.allPermissionsGranted)
        #expect(model.permissionStep == .restart)
        #expect(model.windowAttention > 0)
        await model.continuePermissionSetup()
        #expect(harness.relaunches.get == 1)
        #expect(harness.settings.resumeEnableAfterRelaunch)
        #expect(harness.workers.made.count == 1, "restart must not loop through another model load")
    }

    @Test("check again explains a missing grant, never prompts, and clears its stale message after recovery")
    func checkIsReadOnlyAndRefreshes() {
        var harness = Harness()
        harness.accessibilityGranted = false
        let model = harness.model()
        model.checkPermissionsAgain()
        #expect(model.permissionCheckMessage?.contains("can't use every permission") == true)
        #expect(!harness.permissionCalls.get.contains("ax!"))
        harness.accessibilityGranted = true
        model.refreshPermissions()
        #expect(model.permissionCheckMessage == nil)
        model.checkPermissionsAgain()
        #expect(model.permissionCheckMessage?.contains("All three permissions") == true)
    }

    @Test("restart cannot interrupt a recording")
    func restartWaitsForRecording() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.trigger.onHoldStart?()
        #expect(await eventually { model.recording })
        model.quitAndReopen()
        #expect(harness.relaunches.get == 0)
        await model.cancel()
    }

    @Test("installation identity distinguishes builds and warns about a copy outside Applications")
    func installationIdentity() {
        let installed = DictationInstallation(path: "/Applications/Dictation.app", version: "0.1.0", build: "20261006120000")
        #expect(installed.isInApplications)
        #expect(installed.versionLabel.contains("20261006120000"))
        let old = DictationInstallation(path: "/tmp/Applications/Dictation.app", version: "0.1.0")
        #expect(!old.isInApplications)
        #expect(old.versionLabel.contains("Build not recorded"))
    }
}

@MainActor
@Suite("The permission checklist mirrors the live grants")
struct PermissionChecklistTests {

    @Test("refresh reads every grant and never prompts")
    func refreshReflectsTheGrants() {
        var harness = Harness()
        harness.microphoneGranted = false
        let model = harness.model()
        model.refreshPermissions()
        #expect(!model.microphoneGranted)
        #expect(model.accessibilityGranted && model.inputMonitoringGranted)
        #expect(!model.allPermissionsGranted)
        harness.microphoneGranted = true
        model.refreshPermissions()
        #expect(model.allPermissionsGranted)
        let calls = harness.permissionCalls.get
        #expect(calls.contains("mic?") && calls.contains("ax?") && calls.contains("im?"))
        #expect(!calls.contains("mic") && !calls.contains("ax!") && !calls.contains("im!"),
                "a refresh never prompts: \(calls)")
    }

    @Test("a granted microphone request opens nothing")
    func grantMicrophoneGranted() async {
        let harness = Harness()
        let model = harness.model()
        await model.grantMicrophone()
        let calls = harness.permissionCalls.get
        #expect(calls.contains("mic"))
        #expect(!calls.contains("mic-pane"), "\(calls)")
        #expect(model.microphoneGranted)
    }

    @Test("a denied microphone request ends in the settings pane")
    func grantMicrophoneDeniedOpensThePane() async {
        var harness = Harness()
        harness.microphoneGranted = false
        let model = harness.model()
        await model.grantMicrophone()
        #expect(harness.permissionCalls.get.contains("mic-pane"))
        #expect(!model.microphoneGranted)
    }

    @Test("an accessibility request macOS no longer asks about ends in the settings pane")
    func grantAccessibilityDeniedOpensThePane() async {
        var harness = Harness()
        harness.accessibilityGranted = false
        let model = harness.model()
        await model.grantAccessibility()
        let calls = harness.permissionCalls.get
        #expect(calls.contains("ax!") && calls.contains("ax-pane"), "\(calls)")
        #expect(!model.accessibilityGranted)
    }

    @Test("macOS's own accessibility dialog is the only thing a click opens")
    func accessibilityDialogAlone() async {
        var harness = Harness()
        harness.accessibilityGranted = false
        harness.systemDialogShows.mutate { $0 = true }
        let model = harness.model()
        await model.grantAccessibility()
        let calls = harness.permissionCalls.get
        #expect(calls.contains("ax!"), "\(calls)")
        #expect(!calls.contains("ax-pane"), "the dialog has its own button for the pane: \(calls)")
    }

    @Test("input monitoring registers Earlet and shows its pane when macOS shows no dialog")
    func grantInputMonitoringRequests() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        await model.grantInputMonitoring()
        let calls = harness.permissionCalls.get
        #expect(calls.contains("im!") && calls.contains("im-pane"), "\(calls)")
        #expect(calls.firstIndex(of: "im!")! < calls.firstIndex(of: "im-pane")!, "register first, so the pane lists Earlet")
    }

    @Test("macOS's own input monitoring dialog is the only thing a click opens")
    func inputMonitoringDialogAlone() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        harness.systemDialogShows.mutate { $0 = true }
        let model = harness.model()
        await model.grantInputMonitoring()
        let calls = harness.permissionCalls.get
        #expect(calls.contains("im!"), "\(calls)")
        #expect(!calls.contains("im-pane"), "\(calls)")
    }
}

@MainActor
@Suite("A grant made in System Settings brings Earlet back during setup")
struct GrantLandsTests {
    @Test("a grant that lands with Earlet behind asks for the window once")
    func grantBehindBringsTheWindow() async {
        var harness = Harness()
        harness.accessibilityGranted = false
        harness.inputMonitoringGranted = false
        let model = harness.model()
        model.refreshPermissions()
        let before = model.windowAttention
        harness.accessibilityGranted = true
        model.refreshPermissions()
        #expect(model.windowAttention == before + 1)
        model.refreshPermissions()
        #expect(model.windowAttention == before + 1, "a grant that stays in asks once")
        #expect(harness.logLines.get.contains { $0.contains("a grant landed while Earlet was behind") })
    }

    @Test("a grant made with Earlet in front, as the microphone's dialog is, leaves the window alone")
    func grantInFrontDoesNothing() async {
        var harness = Harness()
        harness.microphoneGranted = false
        harness.dictationFrontmost.mutate { $0 = true }
        let model = harness.model()
        model.refreshPermissions()
        let before = model.windowAttention
        harness.microphoneGranted = true
        model.refreshPermissions()
        #expect(model.windowAttention == before)
    }

    @Test("the first read of grants already in place is not a grant landing")
    func firstReadIsNotAGrant() {
        let harness = Harness()
        let model = harness.model()
        model.refreshPermissions()
        #expect(model.windowAttention == 0)
    }

    @Test("a grant restored while dictation is on is the fix, not a reason to come forward again")
    func grantWhileOnDoesNothing() async {
        var harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        harness.accessibilityGranted = false
        model.refreshPermissions()
        let before = model.windowAttention
        harness.accessibilityGranted = true
        model.refreshPermissions()
        #expect(model.windowAttention == before)
    }
}

@MainActor
@Suite("Setup reads as a first run's next steps, and says what Earlet is")
struct FirstSetupCopyTests {
    @Test("a first run's setup is a first setup; a paused app missing a grant is not")
    func firstSetupOnlyBeforeTheFirstEnable() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        model.refreshPermissions()
        #expect(model.isFirstSetup)
        harness.inputMonitoringGranted = true
        await model.permissionTick()
        #expect(model.enabled)
        #expect(!model.isFirstSetup)
        await model.toggle()
        #expect(!model.enabled)
        harness.inputMonitoringGranted = false
        model.refreshPermissions()
        #expect(!model.isFirstSetup, "it has been on: a missing grant is something that broke")
    }

    @Test("the setup sentences use no engineering words")
    func plainWords() {
        let steps: [DictationPermissionStep] = [.microphone, .accessibility, .inputMonitoring, .restart, .ready]
        let sentences = [DictationPermissionStep.intro] + steps.flatMap { [$0.title, $0.detail, $0.checkMessage, $0.arrivalAnnouncement] }
        for sentence in sentences {
            for word in ["listener", "this copy", "shortcut", "Reopen Earlet"] {
                #expect(!sentence.contains(word), "\(word) in: \(sentence)")
            }
        }
    }

    @Test("the intro says what Earlet does, that it is local, and that three permissions follow")
    func introSaysWhatItIs() {
        let intro = DictationPermissionStep.intro
        #expect(intro.contains("hold a key"))
        #expect(intro.contains("on this Mac"))
        #expect(intro.contains("three permissions"))
    }

    @Test("a copy with no speech files offers a download, not an Enable that fails again")
    func missingResourcesSaySo() async {
        var harness = Harness()
        harness.resources = nil
        let model = harness.model()
        await model.toggle()
        #expect(model.engineFailed)
        #expect(model.speechResourcesMissing)
        #expect(model.status == DictationCopy.speechResourcesMissing)
    }
}

@MainActor
@Suite("An Enable click waiting on a permission resumes when the grant lands")
struct PendingEnableTests {

    @Test("one poll tick after the grant finishes the pending enable")
    func tickResumesAPendingEnable() async {
        var harness = Harness()
        // Not Input Monitoring: a grant made mid-session there applies only
        // after a reopen, which ReopenNeededTests covers.
        harness.accessibilityGranted = false
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        #expect(model.status.contains("Accessibility"))
        harness.accessibilityGranted = true
        await model.permissionTick()
        #expect(model.enabled)
        #expect(harness.workers.made.count == 1)
        #expect(model.status.hasSuffix("Hold \(model.holdKey.name) wherever you type."))
    }

    @Test("the enable waits while any grant is still missing")
    func tickWaitsForEveryGrant() async {
        var harness = Harness()
        harness.microphoneGranted = false
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        await model.permissionTick()
        #expect(!model.enabled, "the microphone is still missing")
        harness.microphoneGranted = true
        await model.permissionTick()
        #expect(model.enabled)
    }

    @Test("a tick does not enable an app that has been on before and was paused")
    func tickWithoutAClickDoesNothing() async {
        let harness = Harness()
        harness.settings.enabled = false
        let model = harness.model()
        await model.permissionTick()
        #expect(!model.enabled)
        #expect(harness.workers.made.isEmpty)
    }

    @Test("a retried enable that fails does not retry again on every tick")
    func failedRetryStaysFailed() async {
        var harness = Harness()
        harness.accessibilityGranted = false
        harness.workers.configure = { $0.state.mutate { $0.startReply = .fail } }
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        harness.accessibilityGranted = true
        await model.permissionTick()
        #expect(!model.enabled)
        #expect(harness.workers.made.count == 1)
        await model.permissionTick()
        #expect(harness.workers.made.count == 1, "the failure was not a permission, so nothing is pending")
    }
}

@MainActor
@Suite("A first run turns itself on once every grant is in (G16)")
struct FirstRunEnableTests {

    @Test("the tick that sees the last grant enables a first run without a click")
    func lastGrantEnables() async {
        var harness = Harness()
        harness.accessibilityGranted = false
        let model = harness.model()
        await model.permissionTick()
        #expect(!model.enabled, "a grant is still missing")
        #expect(harness.workers.made.isEmpty)
        harness.accessibilityGranted = true
        await model.permissionTick()
        #expect(model.enabled)
        #expect(harness.trigger.running)
        #expect(harness.workers.made.count == 1)
        #expect(harness.settings.enabled, "remembered, so a relaunch comes back on")
        #expect(harness.settings.hasBeenEnabledBefore)
        #expect(model.status == "You're set up. Hold \(model.holdKey.name) wherever you type.")
        #expect(harness.logLines.get.contains { $0.contains("first run: every permission is in place") })
    }

    @Test("a first run whose grants are already in turns on at the first tick")
    func grantsAlreadyInEnable() async {
        let harness = Harness()
        let model = harness.model()
        await model.permissionTick()
        #expect(model.enabled)
        #expect(harness.workers.made.count == 1)
    }

    @Test("after a first-run enable and Pause, no later tick or process turns it back on")
    func pauseIsFinal() async {
        let harness = Harness()
        let model = harness.model()
        await model.permissionTick()
        #expect(model.enabled)
        await model.toggle()
        #expect(!model.enabled)
        await model.permissionTick()
        #expect(!model.enabled)
        let relaunched = harness.model()
        await relaunched.permissionTick()
        #expect(!relaunched.enabled, "the paused choice is in the preferences, not only in memory")
        #expect(harness.workers.made.count == 1)
    }

    @Test("a failed first-run enable is reported once, not retried on every tick")
    func failureIsNotRetried() async {
        let harness = Harness()
        harness.workers.configure = { $0.state.mutate { $0.startReply = .fail } }
        let model = harness.model()
        await model.permissionTick()
        #expect(!model.enabled)
        #expect(model.engineFailed)
        #expect(harness.workers.made.count == 1)
        await model.permissionTick()
        await model.permissionTick()
        #expect(harness.workers.made.count == 1)
        #expect(!harness.settings.hasBeenEnabledBefore, "it never was on")
    }

    @Test("a failed Enable click is not retried by the first-run enable either")
    func failedClickIsNotRetried() async {
        let harness = Harness()
        harness.workers.configure = { $0.state.mutate { $0.startReply = .fail } }
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        await model.permissionTick()
        #expect(harness.workers.made.count == 1)
    }

    @Test("a refused key listener on a first-run enable offers the restart, and the fresh process finishes it")
    func refusedTapResumesAfterRestart() async {
        let harness = Harness()
        harness.trigger.startResult = false
        let model = harness.model()
        await model.permissionTick()
        #expect(!model.enabled)
        #expect(model.permissionStep == .restart)
        model.quitAndReopen()
        #expect(harness.relaunches.get == 1)
        #expect(harness.settings.resumeEnableAfterRelaunch)
        harness.trigger.startResult = true
        let reopened = harness.model()
        await reopened.launch()
        #expect(reopened.enabled)
    }

    @Test("no first-run enable while a permission request is open")
    func waitsForAnOpenRequest() async {
        var harness = Harness()
        harness.microphoneGranted = false
        var environment = harness.environment()
        var continuation: CheckedContinuation<Bool, Never>?
        var requests = 0
        // The setup click's request stays open; the enable's own, later,
        // answers at once.
        environment.permissions.requestMicrophone = {
            requests += 1
            if requests > 1 { return true }
            return await withCheckedContinuation { continuation = $0 }
        }
        let model = DictationModel(environment: environment)
        let request = Task { await model.continuePermissionSetup() }
        #expect(await eventually { continuation != nil })
        harness.microphoneGranted = true
        await model.permissionTick()
        #expect(!model.enabled, "the prompt is still open")
        #expect(harness.workers.made.isEmpty)
        continuation?.resume(returning: true)
        await request.value
        await model.permissionTick()
        #expect(model.enabled)
    }

    @Test("no first-run enable while the app quits or recovers a saved take", arguments: [true, false])
    func waitsWhileQuittingOrRecovering(quitting: Bool) async {
        let harness = Harness()
        let model = harness.model()
        if quitting { model.isQuitting = true } else { model.recoveringSavedTake = true }
        await model.permissionTick()
        #expect(!model.enabled)
        #expect(harness.workers.made.isEmpty)
    }

    @Test("the set-up line survives the relaunch macOS asks for after Input Monitoring")
    func setUpLineAfterRelaunch() async {
        let harness = Harness()
        let model = harness.model()
        await model.permissionTick()
        #expect(model.enabled)
        // Quit & Reopen once the enable had already finished: the fresh
        // process comes back on from the preference, not the first-run path.
        let reopened = harness.model()
        await reopened.launch()
        #expect(reopened.enabled)
        #expect(reopened.status == "You're set up. Hold \(reopened.holdKey.name) wherever you type.")
    }

    @Test("once a dictation has been inserted, an enable says Enabled.")
    func returningUserLine() async {
        let harness = Harness()
        harness.settings.hasCompletedFirstDictation = true
        harness.settings.enabled = false
        let model = harness.model()
        await model.toggle()
        #expect(model.status == "On. Dictate wherever you can type.")
    }

    @Test("no first-run enable while Settings records a key or a mouse button", arguments: [true, false])
    func waitsWhileRecording(holdKey: Bool) async {
        let harness = Harness()
        let model = harness.model()
        #expect(holdKey ? model.beginRecordingHoldKey() : model.beginRecordingMouseButton())
        await model.permissionTick()
        #expect(!model.enabled, "the enable would start the listener the recorder stopped")
        #expect(harness.workers.made.isEmpty)
        if holdKey { model.endRecordingHoldKey(choosing: nil) } else { model.endRecordingMouseButton(choosing: nil) }
        await model.permissionTick()
        #expect(model.enabled)
    }

    @Test("an install counts as on before once enable or Pause wrote the setting, older builds included")
    func hasBeenEnabledBeforeReadsTheKey() {
        let harness = Harness()
        #expect(!harness.settings.hasBeenEnabledBefore)
        harness.settings.enabled = false
        #expect(harness.settings.hasBeenEnabledBefore, "a pause written by any build")
        harness.settings.enabled = true
        #expect(harness.settings.hasBeenEnabledBefore)
    }
}

@MainActor
@Suite("A degraded key listener says which access was lost")
struct DegradedTriggerTests {

    @Test("the status names the lost access and the grants are re-read")
    func degradeSetsTheStatus() async {
        var harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        harness.inputMonitoringGranted = false
        harness.trigger.degrade(.missingInputMonitoring)
        #expect(model.status.contains("Accessibility or Input Monitoring"))
        #expect(!model.inputMonitoringGranted)
        #expect(harness.logLines.get.contains { $0.contains("key listener degraded") })
    }

    @Test("a refused start is reported by enable itself, not overwritten")
    func degradeDuringEnableKeepsTheEnableStatus() async {
        let harness = Harness()
        harness.trigger.startResult = false
        harness.trigger.degradeStatus = .missingInputMonitoring
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        #expect(model.status.contains("The shortcut needs Input Monitoring permission"))
    }
}

@MainActor
@Suite("An Input Monitoring grant made mid-session: lazy tap, reopen only after a refused tap")
struct ReopenNeededTests {

    @Test("the false-to-true transition alone needs no reopen — the tap is created after the grant")
    func plainTransitionNeedsNoReopen() {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        model.refreshPermissions()
        harness.inputMonitoringGranted = true
        model.refreshPermissions()
        #expect(!model.reopenNeeded, "no tap was ever refused, so the next start() benefits")
        #expect(model.allPermissionsGranted)
    }

    @Test("the transition after a refused tap creation raises the affordance")
    func transitionAfterRefusalNeedsReopen() async {
        var harness = Harness()
        harness.trigger.startResult = false
        harness.trigger.degradeStatus = .missingInputMonitoring
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled, "the tap was refused while the grant was missing")
        harness.inputMonitoringGranted = false
        model.refreshPermissions()
        harness.inputMonitoringGranted = true
        model.refreshPermissions()
        #expect(model.reopenNeeded, "this process cannot get listen access retroactively")
    }

    @Test("a pending enable resumes on the poll when the grant lands before any tap was tried")
    func grantBeforeAnyTapAttemptResumes() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled, "bailed at the Input Monitoring guard — no tap was created")
        harness.inputMonitoringGranted = true
        await model.permissionTick()
        #expect(!model.reopenNeeded)
        #expect(model.enabled, "the fresh tap is created after the grant and works")
        #expect(harness.workers.made.count == 1)
    }

    @Test("Input Monitoring granted from the start needs no reopen")
    func noTransitionNoReopen() {
        let harness = Harness()
        let model = harness.model()
        model.refreshPermissions()
        #expect(!model.reopenNeeded)
        model.refreshPermissions()
        #expect(!model.reopenNeeded)
    }

    @Test("a pending enable is persisted for the fresh process")
    func quitAndReopenPersistsTheIntent() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        await model.toggle()
        model.quitAndReopen()
        #expect(harness.settings.resumeEnableAfterRelaunch)
        #expect(harness.relaunches.get == 1)
    }

    @Test("an enabled app is already remembered, nothing more is persisted")
    func quitAndReopenWhileEnabled() async {
        var harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        // The tap dies mid-session (reported degraded), and the grant is
        // toggled off and back on: only then is the reopen offered.
        harness.trigger.degrade(.missingInputMonitoring)
        harness.inputMonitoringGranted = false
        model.refreshPermissions()
        harness.inputMonitoringGranted = true
        model.refreshPermissions()
        #expect(model.reopenNeeded)
        model.quitAndReopen()
        #expect(harness.settings.enabled, "launch() re-enables from the preference")
        #expect(!harness.settings.resumeEnableAfterRelaunch)
        #expect(harness.relaunches.get == 1)
    }
}

@MainActor
@Suite("The relaunch intent enables the fresh process exactly once")
struct ResumeEnableAfterRelaunchTests {

    @Test("launch with the intent and every grant enables on its own")
    func launchConsumesTheIntent() async {
        let harness = Harness()
        harness.settings.resumeEnableAfterRelaunch = true
        let model = harness.model()
        await model.launch()
        #expect(model.enabled)
        #expect(!harness.settings.resumeEnableAfterRelaunch, "one-shot: consumed")
    }

    @Test("launch with the intent but a missing grant stays paused and clears it")
    func launchWithAMissingGrantClearsTheIntent() async {
        var harness = Harness()
        harness.accessibilityGranted = false
        harness.settings.resumeEnableAfterRelaunch = true
        let model = harness.model()
        await model.launch()
        #expect(!model.enabled)
        #expect(!harness.settings.resumeEnableAfterRelaunch)
        let calls = harness.permissionCalls.get
        #expect(!calls.contains("mic") && !calls.contains("ax!") && !calls.contains("im!"),
                "launch never prompts: \(calls)")
        #expect(model.status.contains("Turn it on again"))
    }
}

@MainActor
@Suite("A revoked permission brings the window back")
struct WindowAttentionTests {

    @Test("a degraded tap bumps the attention the window observes")
    func degradeRaisesWindowAttention() async {
        var harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        let before = model.windowAttention
        harness.accessibilityGranted = false
        harness.trigger.degrade(.missingAccessibility)
        #expect(model.windowAttention == before + 1)
        #expect(!model.accessibilityGranted)
        #expect(model.status.contains("Accessibility or Input Monitoring"))
    }
}

@MainActor
@Suite("Every sentence names the Accessibility pane as this Mac's System Settings does")
struct AccessibilityPaneNameTests {
    // macOS 26 and earlier say Accessibility; macOS 27 says Device Control
    // and Data Access. Each test runs under both names, so a sentence that
    // spells the old name, or reads the test Mac's own, fails on any Mac.

    private func makeHarness(onMacOS macOS: Int) -> Harness {
        var harness = Harness()
        harness.accessibilitySettingsName = DictationPermissionStep.accessibilitySettingsName(majorVersion: macOS)
        return harness
    }

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.inserts.outcome.mutate { $0 = .failed(.accessibilityPermissionMissing) }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("enabling without the grant says where to allow it", arguments: [26, 27])
    func enableNamesThePane(macOS: Int) async {
        var harness = makeHarness(onMacOS: macOS)
        harness.accessibilityGranted = false
        let pane = harness.accessibilitySettingsName
        let model = harness.model()
        await model.toggle()
        #expect(model.status
            == "Allow Earlet in System Settings → Privacy & Security → \(pane), then turn Earlet on again.")
    }

    @Test("a lost grant names the pane", arguments: [26, 27])
    func degradeNamesThePane(macOS: Int) async {
        let harness = makeHarness(onMacOS: macOS)
        let pane = harness.accessibilitySettingsName
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        harness.trigger.degrade(.missingAccessibility)
        #expect(model.status == "Earlet lost its access under \(pane) or Input Monitoring. Grant it again below to continue.")
    }

    @Test("a refused paste kept on the clipboard names the pane", arguments: [26, 27])
    func clipboardFallbackNamesThePane(macOS: Int) async {
        let harness = makeHarness(onMacOS: macOS)
        let pane = harness.accessibilitySettingsName
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        #expect(harness.clipboard.get == ["Hello world"])
        #expect(model.status
            == "Earlet needs \(pane) permission to type. Your text is on the clipboard; press ⌘V to paste it.")
    }

    @Test("a refused paste the clipboard could not take names the pane", arguments: [26, 27])
    func recoverableTextNamesThePane(macOS: Int) async {
        let harness = makeHarness(onMacOS: macOS)
        harness.clipboardWorks.mutate { $0 = false }
        let pane = harness.accessibilitySettingsName
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        #expect(model.status == "Allow \(pane) to type here. "
            + "Your words are kept: use Copy last text on Home or from Earlet's menu bar icon.")
    }

    @Test("the floating indicator is handed the pane's name", arguments: [26, 27])
    func indicatorNamesThePane(macOS: Int) {
        let harness = makeHarness(onMacOS: macOS)
        let pane = harness.accessibilitySettingsName
        let model = harness.model()
        model.showOutcome(.accessibilityNeeded)
        #expect(model.hudOutcome?.title == "Earlet can't type yet")
        #expect(model.hudOutcome?.detail == "Allow Earlet in Privacy & Security → \(pane), then try again.")
    }

    @Test("a key test whose listener loses its grant names the pane", arguments: [26, 27])
    func keyTestNamesThePane(macOS: Int) {
        let harness = makeHarness(onMacOS: macOS)
        let pane = harness.accessibilitySettingsName
        var environment = harness.environment()
        environment.isDictationFrontmost = { true }
        let model = DictationModel(environment: environment)
        model.keyCheck.startTest()
        #expect(model.keyCheck.isTesting)
        harness.trigger.degrade(.missingAccessibility)
        #expect(model.keyCheck.message == "In Privacy & Security, allow Earlet under Input Monitoring and under \(pane), then restart Earlet to test this key.")
        model.keyCheck.stopTest()
    }
}
#endif
