// Launch at login: off until the user turns it on, read back from macOS
// rather than remembered, and honest when macOS refuses (an unsigned build
// is the case the copy names).

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Launch at login")
struct LaunchAtLoginTests {

    @Test("off by default: launching and enabling never register Dictation")
    func offByDefault() async {
        let harness = Harness()
        harness.settings.enabled = true
        let model = harness.model()
        #expect(!model.launchAtLogin)
        await model.launch()
        #expect(model.enabled)
        await model.toggle()
        await model.toggle()
        #expect(harness.loginItem.calls.isEmpty)
        #expect(!model.launchAtLogin)
        #expect(model.loginItemNote == nil)
    }

    @Test("the state is read from macOS, which the user can change in System Settings")
    func stateComesFromMacOS() {
        let harness = Harness()
        harness.loginItem.status = .on
        let model = harness.model()
        #expect(model.launchAtLogin)
        harness.loginItem.status = .off
        model.refreshLaunchAtLogin()
        #expect(!model.launchAtLogin)
    }

    @Test("turning it on registers, and turning it off unregisters")
    func toggleRegisters() {
        let harness = Harness()
        let model = harness.model()
        model.setLaunchAtLogin(true)
        #expect(model.launchAtLogin)
        #expect(model.loginItemNote == nil)
        model.setLaunchAtLogin(false)
        #expect(!model.launchAtLogin)
        #expect(harness.loginItem.calls == ["register", "unregister"])
    }

    @Test("when macOS refuses, the toggle stays off and says why")
    func refusedRegisterIsExplained() {
        let lines = Locked([String]())
        let harness = Harness()
        harness.loginItem.registerFails = true
        var env = harness.environment()
        env.log = { line in lines.mutate { $0.append(line) } }
        let model = DictationModel(environment: env)

        model.setLaunchAtLogin(true)
        #expect(!model.launchAtLogin)
        #expect(model.loginItemNote?.contains("not signed") == true)
        #expect(lines.get.contains { $0.contains("launch at login") && $0.contains("refused") })
    }

    @Test("when macOS waits for approval, the toggle is on and points at Login Items")
    func approvalIsRequested() {
        let harness = Harness()
        harness.loginItem.statusAfterRegister = .needsApproval
        let model = harness.model()
        model.setLaunchAtLogin(true)
        #expect(model.launchAtLogin)
        #expect(model.loginItemNote?.contains("Login Items") == true)
        #expect(harness.loginItem.calls == ["register", "settings"])
    }

    @Test("a refused unregister leaves it on and tells the user where to remove it")
    func refusedUnregisterIsExplained() {
        let harness = Harness()
        harness.loginItem.status = .on
        harness.loginItem.unregisterFails = true
        let model = harness.model()
        model.setLaunchAtLogin(false)
        #expect(model.launchAtLogin)
        #expect(model.loginItemNote?.contains("System Settings") == true)
    }

    @Test("a copy macOS cannot find reads as off, with nothing to explain until the user asks")
    func unavailableReadsAsOff() {
        let harness = Harness()
        harness.loginItem.status = .unavailable
        let model = harness.model()
        #expect(!model.launchAtLogin)
        #expect(model.loginItemNote == nil)
    }
}
#endif
