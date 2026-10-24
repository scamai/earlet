// AudioGate unit tests — the SHARED pre-ASR gate both dictation and
// meeting-notes run their captured audio through before spending a Whisper
// round trip on it.
//
// Why this module and not one of the feature modules: the gate exists
// because the two features had DIFFERENT answers to "is this clip worth
// transcribing" — dictation gated (W-2), notes did not, and notes therefore
// shipped `"Thank you."` hallucinations onto silence. One implementation in
// the module both features already depend on is what makes that class of
// drift impossible to re-introduce silently.
//
// Environment note: same `#if canImport(Testing)` guard as every other suite
// here (CLT-only toolchains ship neither XCTest nor swift-testing). The
// equivalent assertions also run through `DictationApp --self-check`.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAudioCapture

@Suite("ASR audio gate")
struct AudioGateTests {
    let sr = 16_000

    /// A speech-shaped clip: `voiced` seconds of tone at `amplitude`,
    /// padded with digital silence to `total` seconds.
    private func clip(voiced: Double, total: Double, amplitude: Float = 0.05) -> [Float] {
        let voicedCount = Int(voiced * Double(sr))
        let padCount = max(0, Int(total * Double(sr)) - voicedCount)
        let tone = (0..<voicedCount).map { i in
            amplitude * Float(sin(2 * Double.pi * 220 * Double(i) / Double(sr)))
        }
        return tone + [Float](repeating: 0, count: padCount)
    }

    // MARK: - Policies

    @Test("both presets use the app-wide silence RMS threshold")
    func presetsShareTheThreshold() {
        #expect(AsrGatePolicy.dictation.audibleThreshold == SegmenterConstants().silenceRMS)
        #expect(AsrGatePolicy.meetingNotes.audibleThreshold == SegmenterConstants().silenceRMS)
    }

    @Test("dictation has a short impulse floor and no voiced fraction floor")
    func presetFloors() {
        #expect(AsrGatePolicy.meetingNotes.minVoicedSeconds == 0.3)
        #expect(AsrGatePolicy.meetingNotes.minVoicedFraction == 0.15)
        // Nine short analysis frames stop clicks; a 120 ms word still survives.
        #expect(AsrGatePolicy.dictation.minVoicedSeconds == 0.09)
        #expect(AsrGatePolicy.dictation.minVoicedFraction == 0)
        #expect(AsrGatePolicy.dictation.frameSeconds == 0.01)
        #expect(AsrGatePolicy.meetingNotes.frameSeconds == 0.03)
    }

    // MARK: - Silence

    @Test("digital silence is skipped under every policy")
    func silenceIsSkipped() {
        let silent = [Float](repeating: 0, count: sr)
        for policy in [AsrGatePolicy.dictation, .meetingNotes] {
            let decision = AudioGate.shouldTranscribe(samples: silent, sampleRate: sr, policy: policy)
            #expect(decision.isSkip)
            #expect(decision.skipReason == "silent")
        }
    }

    @Test("an empty buffer is skipped, not crashed on")
    func emptyIsSkipped() {
        let decision = AudioGate.shouldTranscribe(samples: [], sampleRate: sr, policy: .meetingNotes)
        #expect(decision.skipReason == "empty")
    }

    @Test("a clip sitting exactly AT the threshold reads as silence")
    func boundaryIsSilence() {
        let t = AsrGatePolicy.dictation.audibleThreshold
        let decision = AudioGate.shouldTranscribe(samples: [t, -t, t], sampleRate: sr, policy: .dictation)
        #expect(decision.isSkip)
    }

    // MARK: - The notes bug

    @Test("a brief blip under the notes voiced floor is skipped")
    func briefBlipIsSkipped() {
        // 0.15s of real signal inside a 3s utterance — the AEC-clamped
        // residue shape that decoded to `"Thank you."` in the field.
        let decision = AudioGate.shouldTranscribe(
            samples: clip(voiced: 0.15, total: 3.0), sampleRate: sr, policy: .meetingNotes)
        #expect(decision.isSkip)
        #expect(decision.skipReason == "insufficient-voiced")
    }

    @Test("a low voiced FRACTION is skipped even when the absolute duration clears the floor")
    func lowFractionIsSkipped() {
        // 0.5s voiced (over minVoicedSeconds) inside 20s = 2.5% voiced.
        let decision = AudioGate.shouldTranscribe(
            samples: clip(voiced: 0.5, total: 20.0), sampleRate: sr, policy: .meetingNotes)
        #expect(decision.isSkip)
    }

    @Test("real speech transcribes, with the measured voiced fraction")
    func speechTranscribes() {
        let decision = AudioGate.shouldTranscribe(
            samples: clip(voiced: 2.0, total: 4.0), sampleRate: sr, policy: .meetingNotes)
        guard case .transcribe(let fraction) = decision else {
            Issue.record("real speech was gated: \(decision)")
            return
        }
        // 2s of 4s voiced, frame-quantized at 0.03s — half, within a frame.
        #expect(abs(fraction - 0.5) < 0.02)
    }

    @Test("a 120ms word still passes the DICTATION policy")
    func shortWordPassesDictation() {
        let decision = AudioGate.shouldTranscribe(
            samples: clip(voiced: 0.12, total: 0.12), sampleRate: sr, policy: .dictation)
        #expect(!decision.isSkip)
    }

    @Test("click rejection and short words do not depend on frame alignment", arguments: Array(0..<30))
    func frameAlignment(offsetMilliseconds: Int) {
        let offset = sr + offsetMilliseconds * sr / 1000
        for (seconds, shouldSkip) in [(0.045, true), (0.12, false)] {
            var samples = [Float](repeating: 0.0015, count: sr * 6)
            let word = clip(voiced: seconds, total: seconds, amplitude: 0.008)
            samples.replaceSubrange(offset..<(offset + word.count), with: word)
            #expect(AudioGate.shouldTranscribe(samples: samples, sampleRate: sr,
                                               policy: .dictation).isSkip == shouldSkip)
        }
    }

    @Test("unanalysed trailing samples cannot inflate voiced duration")
    func partialTailCannotClearFloor() {
        let quietTail = [Float](repeating: 0, count: 159)
        let eightFrames = [Float](repeating: 0.02, count: 160 * 8) + quietTail
        let nineFrames = [Float](repeating: 0.02, count: 160 * 9) + quietTail
        #expect(AudioGate.shouldTranscribe(samples: eightFrames, sampleRate: sr, policy: .dictation).isSkip)
        #expect(!AudioGate.shouldTranscribe(samples: nineFrames, sampleRate: sr, policy: .dictation).isSkip)
    }

    @Test("...and the same 120ms word is gated by the NOTES policy")
    func shortWordGatedByNotes() {
        let decision = AudioGate.shouldTranscribe(
            samples: clip(voiced: 0.12, total: 0.12), sampleRate: sr, policy: .meetingNotes)
        #expect(decision.isSkip)
    }

    @Test("a clip shorter than one analysis frame never divides by zero")
    func subFrameClip() {
        let t = AsrGatePolicy.dictation.audibleThreshold
        let tiny: [Float] = [0, 0, t.nextUp, 0]
        #expect(AudioGate.shouldTranscribe(samples: tiny, sampleRate: sr, policy: .dictation).isSkip)
        #expect(AudioGate.shouldTranscribe(samples: tiny, sampleRate: sr, policy: .meetingNotes).isSkip)
    }
}

private extension GateDecision {
    var isSkip: Bool {
        if case .skip = self { return true }
        return false
    }

    var skipReason: String? {
        if case .skip(let reason) = self { return reason }
        return nil
    }
}
#endif
