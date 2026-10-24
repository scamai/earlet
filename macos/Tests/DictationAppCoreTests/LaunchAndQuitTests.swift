// Permissions are asked in an order that wastes nothing; the enabled state
// survives a relaunch; quitting never leaves a worker behind.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Input Monitoring is requested before any worker is spawned")
struct InputMonitoringTests {

    @Test("without Input Monitoring the app is registered for it and no worker starts")
    func missingInputMonitoringIsRequested() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        let model = harness.model()
        await model.toggle()
        #expect(!model.enabled)
        #expect(harness.permissionCalls.get.contains("im!"))
        #expect(harness.workers.made.isEmpty, "no model load for a shortcut that cannot work")
        #expect(model.status.contains("Input Monitoring"))
    }

    @Test("with every permission granted nothing is requested")
    func grantedIsNotRequested() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        #expect(!harness.permissionCalls.get.contains("im!"))
    }
}

@MainActor
@Suite("The enabled state survives a relaunch")
struct PersistedEnabledTests {

    @Test("enable and pause are remembered")
    func toggleIsPersisted() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        #expect(harness.settings.enabled)
        await model.toggle()
        #expect(!harness.settings.enabled)
    }

    @Test("a relaunch comes back on when the user left it on and access is in place")
    func relaunchReEnables() async {
        let harness = Harness()
        harness.settings.enabled = true
        let model = harness.model()
        await model.launch()
        #expect(model.enabled)
    }

    @Test("a relaunch never prompts: missing access leaves it paused with an explanation")
    func relaunchDoesNotPrompt() async {
        var harness = Harness()
        harness.inputMonitoringGranted = false
        harness.settings.enabled = true
        let model = harness.model()
        await model.launch()
        #expect(!model.enabled)
        let calls = harness.permissionCalls.get
        #expect(!calls.contains("mic") && !calls.contains("ax!") && !calls.contains("im!"), "\(calls)")
        #expect(harness.workers.made.isEmpty)
        #expect(model.status.contains("Enable it again"))
    }

    @Test("a relaunch after the user paused stays paused")
    func pausedStaysPaused() async {
        let harness = Harness()
        let model = harness.model()
        await model.launch()
        #expect(!model.enabled)
        #expect(harness.permissionCalls.get.isEmpty)
    }
}

@MainActor
@Suite("Quitting stops the worker and lets the clipboard settle")
struct QuitTests {

    @Test("quit shuts the worker down and keeps the preference")
    func quitStopsTheWorker() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        await model.prepareForQuit()
        #expect(harness.workers.made[0].state.get.shutdowns == 1)
        #expect(harness.clipboardSettles.get == 1)
        #expect(harness.settings.enabled, "quitting is not pausing")
        #expect(!harness.trigger.running)
    }

    @Test("quit during a wedged transcription kills the worker within its bound",
          .timeLimit(.minutes(1)))
    func quitKillsAWedgedWorker() async {
        var harness = Harness()
        // Long enough that only quitting can end the wait.
        harness.timing.transcribeSeconds = 30
        let model = harness.model()
        await model.toggle()
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang]; $0.shutdownHangs = true }

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { worker.state.get.transcribes == 1 })
        #expect(await finishes(within: 10) { await model.prepareForQuit() })
        #expect(worker.state.get.kills >= 1)
    }

    @Test("quit does not wait past its bound for learning's last save", .timeLimit(.minutes(1)))
    func quitBoundsTheLastSave() async {
        let lexicon = GatedLexicon()
        var harness = Harness()
        harness.lexicon = lexicon
        let model = harness.model()
        await model.toggle()
        // The learning actor is held busy, as by a disk that does not answer.
        lexicon.blocking.mutate { $0 = true }
        let refresh = Task { await model.vocabulary.refresh() }
        #expect(await eventually { lexicon.entered.get })
        #expect(await finishes(within: 5) { await model.prepareForQuit() })
        #expect(harness.logLines.get.contains("learning: last save at quit (timed out)"))
        #expect(harness.workers.made[0].state.get.shutdowns == 1, "the rest of the clean-up still ran")
        lexicon.release()
        await refresh.value
    }

    @Test("quit reads nothing from learning.json when learning was not used")
    func quitWithoutLearningReadsNothing() async throws {
        let harness = Harness()
        let url = harness.learningURL
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: url)
        let model = harness.model()
        await model.toggle()
        await model.prepareForQuit()
        #expect(try String(contentsOf: url, encoding: .utf8) == "not json")
        #expect(!FileManager.default.fileExists(atPath: url.appendingPathExtension("bad").path))
        #expect(harness.logLines.get.contains("learning: last save at quit (unchanged)"))
    }
}
#endif
