// Regression coverage for capture paths that contain no usable audio.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Zero input cools the capture before the next hold")
struct ZeroInputTests {

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    @Test("empty or exact-zero PCM is reported as no audio, cooled, and the next hold rewarms", arguments: [false, true])
    func exactZeroPcmCoolsAndNextHoldRewarms(empty: Bool) async {
        let harness = Harness()
        harness.capture.samples.mutate { $0 = [Float](repeating: 0, count: empty ? 0 : 16_000) }
        let model = await enabled(harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })

        var state = harness.capture.state.get
        #expect(model.status.contains("microphone delivered no audio"))
        #expect(state.coolDowns == 1)
        #expect(state.warmUps == 1)
        #expect(harness.workers.made.last?.state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)

        harness.capture.samples.mutate { $0 = [Float](repeating: 0.2, count: 16_000) }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })

        state = harness.capture.state.get
        #expect(state.warmUps == 2)
        #expect(harness.inserts.texts.get == ["Hello world"])
    }

    @Test("ordinary quiet nonzero PCM remains the nothing-heard path and stays warm")
    func quietNonzeroPcmStaysWarm() async {
        let harness = Harness()
        harness.capture.samples.mutate { $0 = [Float](repeating: 0.0001, count: 16_000) }
        let model = await enabled(harness)

        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })

        let state = harness.capture.state.get
        #expect(model.status.contains("Nothing was heard"))
        #expect(state.coolDowns == 0)
        #expect(state.warmUps == 1)
        #expect(harness.workers.made.last?.state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)
    }

    // ─── Near-silent input and how long the hold was ───

    /// The next hold's audio, and the forensic record the real capture
    /// measures with it: the input's name, the hold's length and its peak.
    private func capture(_ harness: Harness, seconds: Double, level: Float, device: String = "AirPods Pro") {
        harness.capture.samples.mutate { $0 = [Float](repeating: level, count: Int(seconds * 16_000)) }
        harness.capture.state.mutate {
            $0.forensics = DictationCaptureForensics(deviceName: device, holdSeconds: seconds, peak: abs(level))
        }
    }

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    private func words(_ kind: DictationHUDOutcome.Kind) -> DictationHUDOutcome {
        DictationHUDContent.outcome(for: kind, holdKeyName: "Right Command")
    }

    @Test("a two-second hold that never reaches the floor names the microphone and cools it, like exact zeros")
    func nearSilentHoldNamesTheMicrophone() async {
        let harness = Harness()
        // No exact zeros, and a peak the log prints as 0.0000.
        capture(harness, seconds: 2, level: 0.00002)
        let model = await enabled(harness)
        model.hudOutcomeSecondsOverride = 60

        await dictate(harness, model)

        #expect(model.hudOutcome == words(.microphoneSilent(deviceName: "AirPods Pro")))
        #expect(model.hudOutcome?.title == "No sound from AirPods Pro")
        #expect(model.status.contains("microphone delivered no audio"))
        #expect(harness.capture.state.get.coolDowns == 1)
        #expect(harness.workers.made.last?.state.get.transcribes == 0)
        #expect(harness.inserts.texts.get.isEmpty)
    }

    @Test("a silent microphone whose name the capture could not read is the microphone")
    func unnamedSilentMicrophone() async {
        let harness = Harness()
        capture(harness, seconds: 2, level: 0, device: "device 117")
        let model = await enabled(harness)
        model.hudOutcomeSecondsOverride = 60

        await dictate(harness, model)

        #expect(model.hudOutcome?.title == "No sound from the microphone")
        #expect(model.hudOutcome?.tone == .caution)
    }

    @Test("a near-silent tap is a tap: nothing heard, hold the key")
    func nearSilentTapAsksForAHold() async {
        let harness = Harness()
        capture(harness, seconds: 0.3, level: 0.00002)
        let model = await enabled(harness)
        model.hudOutcomeSecondsOverride = 60

        await dictate(harness, model)

        #expect(model.hudOutcome == words(.nothingHeard(speakUp: false)))
        #expect(model.hudOutcome?.detail == "Hold Right Command while you speak.")
        #expect(model.status.contains("Hold Right Command while you speak"))
        #expect(harness.capture.state.get.coolDowns == 0)
    }

    @Test("a near-silent hold too short to be a silent input is told to hold the key, not to speak up")
    func shortNearSilentHoldAsksForAHold() async {
        let harness = Harness()
        // Long enough for speech, too short to blame the input: no voice
        // was too quiet, so "speak up" would be wrong advice.
        capture(harness, seconds: 0.8, level: 0.00002)
        let model = await enabled(harness)
        model.hudOutcomeSecondsOverride = 60

        await dictate(harness, model)

        #expect(model.hudOutcome == words(.nothingHeard(speakUp: false)))
        #expect(model.hudOutcome?.detail == "Hold Right Command while you speak.")
        #expect(model.status.contains("Hold Right Command while you speak"))
        #expect(harness.capture.state.get.coolDowns == 0)
    }

    @Test("two seconds of quiet audio under the speech gate asks for a louder voice")
    func quietHoldAsksToSpeakUp() async {
        let harness = Harness()
        // Above the silent-input floor, under the 0.004 gate.
        capture(harness, seconds: 2, level: 0.001)
        let model = await enabled(harness)
        model.hudOutcomeSecondsOverride = 60

        await dictate(harness, model)

        #expect(model.hudOutcome == words(.nothingHeard(speakUp: true)))
        #expect(model.hudOutcome?.detail == "Speak up, or move closer to the microphone.")
        #expect(model.status.hasPrefix("Nothing was heard. Speak up, or move closer to the microphone."))
        #expect(harness.capture.state.get.coolDowns == 0)
        #expect(harness.workers.made.last?.state.get.transcribes == 0)
    }

    @Test("a hold that hears nothing from the input for 2.5 s says so, and audio takes it down")
    func silentInputNoticeDuringTheHold() async throws {
        let harness = Harness()
        harness.capture.state.mutate { $0.inputName = "AirPods Pro" }
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.attempt?.captureStarted == true })
        let attempt = try #require(model.attempt)

        harness.capture.emitRetainedAudio([Float](repeating: 0.00002, count: 32_000))
        await model.checkHoldSilence(attempt)
        #expect(model.holdNotice == nil, "two seconds is a headset still waking")

        harness.capture.emitRetainedAudio([Float](repeating: 0, count: 9_600))
        await model.checkHoldSilence(attempt)
        #expect(model.holdNotice == "No sound from AirPods Pro — check the microphone.")

        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 480))
        await model.checkHoldSilence(attempt)
        #expect(model.holdNotice == nil)

        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(model.holdNotice == nil)
    }

    @Test("an input name too long for the hint's one line gives the unnamed notice, advice whole")
    func longInputNameKeepsTheAdvice() async throws {
        let harness = Harness()
        harness.capture.state.mutate { $0.inputName = "USB Audio Interface Front Panel Microphone Input 1-2" }
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.attempt?.captureStarted == true })
        let attempt = try #require(model.attempt)

        harness.capture.emitRetainedAudio([Float](repeating: 0, count: 41_600))
        await model.checkHoldSilence(attempt)
        #expect(model.holdNotice == DictationCopy.silentInputDuringHold(deviceName: nil))

        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("a watchdog-discarded capture keeps its recovered engine warm")
    func interruptedCaptureDoesNotCoolDownAgain() async {
        let harness = Harness()
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.state.mutate {
            $0.interruption = DictationCaptureInterruption(reason: "no-chunks", deviceName: "Headset")
        }
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
        #expect(model.status.contains("Mic changed to Headset"))
        #expect(harness.capture.state.get.coolDowns == 0)
        #expect(harness.capture.state.get.warm)
        #expect(harness.inserts.texts.get.isEmpty)
    }
}
#endif
