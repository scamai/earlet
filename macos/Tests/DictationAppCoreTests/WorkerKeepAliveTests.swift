// How long the speech engine stays loaded: learned from the pauses between
// dictations, halved by memory pressure, and wired into the model's press,
// housekeeping and the kernel's memory-pressure notifications.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@Suite("The keep-alive window learns from the pauses between dictations")
struct WorkerKeepAlivePolicyTests {

    @Test("the default holds until twenty pauses have been seen")
    func defaultUntilEnoughPauses() {
        #expect(DictationWorkerKeepAlive().seconds == 600)
        #expect(DictationWorkerKeepAlive(pauses: Array(repeating: 300, count: 19)).seconds == 600)
        #expect(Int(DictationWorkerKeepAlive(pauses: Array(repeating: 300, count: 20)).seconds) == 330)
    }

    @Test("the window is the 99th percentile of the pauses plus a tenth")
    func percentileWithMargin() {
        // 10 s to 1000 s: the 99th percentile interpolates to 990.1 s.
        let keepAlive = DictationWorkerKeepAlive(pauses: (1...100).map { Double($0) * 10 })
        #expect(abs(keepAlive.seconds - 990.1 * 1.1) < 0.001)
    }

    @Test("pauses longer than the cap are breaks and teach nothing")
    func breaksAreLeftOut() {
        let pauses = Array(repeating: 300.0, count: 30) + Array(repeating: 7_200, count: 10)
        let keepAlive = DictationWorkerKeepAlive(pauses: pauses)
        #expect(keepAlive.samples == 30)
        #expect(Int(keepAlive.seconds) == 330)
    }

    @Test("the window stays between three and thirty minutes")
    func clampedToFloorAndCap() {
        #expect(DictationWorkerKeepAlive(pauses: Array(repeating: 5, count: 30)).seconds == 180)
        #expect(DictationWorkerKeepAlive(pauses: Array(repeating: 1_700, count: 30)).seconds == 1_800)
    }

    @Test("memory pressure halves the window to a quarter, and each dictation gives back an eighth")
    func pressureBacksOffAndRecovers() {
        var keepAlive = DictationWorkerKeepAlive(pauses: Array(repeating: 1_000, count: 30))
        keepAlive.memoryPressure()
        #expect(Int(keepAlive.seconds) == 550)
        keepAlive.memoryPressure()
        keepAlive.memoryPressure()
        #expect(keepAlive.pressureFactor == 0.25)
        #expect(Int(keepAlive.seconds) == 275)
        keepAlive.record(pause: 1_000)
        #expect(keepAlive.pressureFactor == 0.375)
    }

    @Test("pressure never takes the window under the floor")
    func pressureKeepsTheFloor() {
        var keepAlive = DictationWorkerKeepAlive()
        keepAlive.memoryPressure()
        keepAlive.memoryPressure()
        #expect(keepAlive.seconds == 180)
    }

    @Test("the newest two hundred pauses are kept, and a negative or unknown one is not")
    func historyIsBounded() {
        var keepAlive = DictationWorkerKeepAlive(pauses: (0..<250).map(Double.init) + [-1, .nan, .infinity])
        #expect(keepAlive.pauses.count == 200)
        #expect(keepAlive.pauses.first == 50)
        keepAlive.record(pause: -5)
        keepAlive.record(pause: 3)
        #expect(keepAlive.pauses.count == 200)
        #expect(keepAlive.pauses.last == 3)
    }
}

@MainActor
@Suite("The model counts pauses and gives the engine back under memory pressure")
struct WorkerKeepAliveWiringTests {

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    private func logged(_ harness: Harness, _ prefix: String) -> Bool {
        harness.logLines.get.contains { $0.hasPrefix(prefix) }
    }

    @Test("the first press after a dictation counts the pause by the wall clock and keeps it",
          .timeLimit(.minutes(1)))
    func pressCountsThePause() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()

        // The first press after turning on follows no dictation.
        await dictate(harness, model)
        #expect(harness.settings.workerPauses.isEmpty)

        harness.wallClock.mutate { $0.addTimeInterval(42) }
        await dictate(harness, model)
        #expect(harness.settings.workerPauses == [42])
        #expect(logged(harness, "keep-alive pause_s=42 warm=true window_s=600 samples=1"))
    }

    @Test("a press after the engine was given back is logged as cold", .timeLimit(.minutes(1)))
    func releasedEngineIsCold() async {
        var harness = Harness()
        harness.timing.workerIdleSeconds = 0.2
        harness.timing.housekeepingSeconds = 0.05
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        #expect(await eventually { harness.workers.made.last?.state.get.shutdowns == 1 })
        #expect(logged(harness, "keep-alive released idle_s="))

        harness.wallClock.mutate { $0.addTimeInterval(400) }
        await dictate(harness, model)
        #expect(logged(harness, "keep-alive pause_s=400 warm=false"))
        #expect(harness.inserts.texts.get == ["Hello world", "Hello world"])
    }

    @Test("pauses kept by an earlier run set the window")
    func keptPausesSetTheWindow() {
        let harness = Harness()
        harness.settings.workerPauses = Array(repeating: 1_000, count: 30)
        #expect(Int(harness.model().keepAlive.seconds) == 1_100)
    }

    @Test("without a fixed window the learned one keeps an engine used moments ago")
    func learnedWindowKeepsARecentEngine() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        await model.housekeepingTick()
        #expect(harness.workers.made[0].state.get.shutdowns == 0)
        #expect(!logged(harness, "keep-alive released"))
    }

    @Test("critical memory pressure gives an idle engine back at once; the next press starts another",
          .timeLimit(.minutes(1)))
    func criticalPressureReleasesAnIdleEngine() async throws {
        let harness = Harness()
        let model = harness.model()
        await model.launch()
        await model.toggle()
        let pressure = try #require(harness.memoryPressure.get)

        pressure(.critical)
        #expect(await eventually { harness.workers.made[0].state.get.shutdowns == 1 })
        #expect(model.keepAlive.pressureFactor == 0.5)
        #expect(logged(harness, "keep-alive memory_pressure=critical window_s=300"))
        #expect(logged(harness, "keep-alive released by memory pressure"))

        await dictate(harness, model)
        #expect(harness.workers.made.count == 2)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("a memory warning halves the window and keeps an engine used moments ago")
    func warningOnlyShortensTheWindow() async throws {
        let harness = Harness()
        let model = harness.model()
        await model.launch()
        await model.toggle()
        let pressure = try #require(harness.memoryPressure.get)

        pressure(.warning)
        try await Task.sleep(for: .milliseconds(100))
        #expect(harness.workers.made[0].state.get.shutdowns == 0)
        #expect(model.keepAlive.pressureFactor == 0.5)
        #expect(logged(harness, "keep-alive memory_pressure=warning window_s=300"))
    }

    @Test("critical memory pressure during a hold leaves the engine to finish it", .timeLimit(.minutes(1)))
    func pressureDuringAHoldKeepsTheEngine() async throws {
        let harness = Harness()
        let model = harness.model()
        await model.launch()
        await model.toggle()
        let pressure = try #require(harness.memoryPressure.get)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        pressure(.critical)
        try await Task.sleep(for: .milliseconds(100))
        #expect(harness.workers.made[0].state.get.shutdowns == 0)
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(harness.workers.made.count == 1)
    }
}
#endif
