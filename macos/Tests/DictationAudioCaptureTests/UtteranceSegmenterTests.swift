// UtteranceSegmenter unit tests — synthetic tone bursts separated by
// silence, NO audio hardware required.
//
// Environment note: the machine running this session has only the Command
// Line Tools toolchain, which ships neither `XCTest` nor swift-testing's
// `Testing` module (both come with full Xcode). To keep `swift test` green
// under CLT AND have real assertions run under full Xcode, this suite is
// written with swift-testing and guarded by `#if canImport(Testing)` — same
// pattern as `DictationIPCTests/FramingTests.swift`. The SAME assertions run
// today (CLT-only) via `DictationAudioCapture.runSegmenterChecks()`, wired into
// `DictationApp --self-check` as `segmenter-check`.

#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAudioCapture

@Suite("Utterance segmenter")
struct UtteranceSegmenterTests {
    let constants = SegmenterConstants()
    let sr = 16_000

    @Test("a single tone burst, silence-padded, becomes one utterance")
    func singleUtterance() {
        let segmenter = UtteranceSegmenter(constants: constants)
        // 1.2s trailing silence, not 1.0s: frame-quantized at 0.03s/frame,
        // exactly 1.0s rounds down to 33 frames (0.99s) — just under the
        // 1.0s trailingSilenceSeconds threshold — so give real margin above it.
        let audio = SyntheticAudio.silence(seconds: 0.5, sampleRate: sr)
            + SyntheticAudio.tone(seconds: 2.0, sampleRate: sr)
            + SyntheticAudio.silence(seconds: 1.2, sampleRate: sr)
        let utts = segmenter.segment(audio, channel: .mic)

        #expect(utts.count == 1)
        #expect(abs(utts[0].startOffsetSeconds - 0.5) < 0.1)
        #expect(abs(Double(utts[0].samples.count) / Double(sr) - 2.0) < 0.1)
        #expect(utts[0].channel == .mic)
    }

    @Test("a silence gap >= trailingSilenceSeconds splits two bursts")
    func splitsOnLongGap() {
        let segmenter = UtteranceSegmenter(constants: constants)
        let audio = SyntheticAudio.tone(seconds: 1.5, sampleRate: sr)
            + SyntheticAudio.silence(seconds: 1.3, sampleRate: sr)
            + SyntheticAudio.tone(frequency: 660, seconds: 1.5, sampleRate: sr)
        let utts = segmenter.segment(audio, channel: .system)

        #expect(utts.count == 2)
        #expect(utts[0].startOffsetSeconds < utts[1].startOffsetSeconds)
        #expect(utts.allSatisfy { $0.channel == .system })
    }

    @Test("a silence gap shorter than trailingSilenceSeconds does not split")
    func doesNotSplitOnShortGap() {
        let segmenter = UtteranceSegmenter(constants: constants)
        let audio = SyntheticAudio.tone(seconds: 1.0, sampleRate: sr)
            + SyntheticAudio.silence(seconds: 0.2, sampleRate: sr)
            + SyntheticAudio.tone(seconds: 1.0, sampleRate: sr)
            + SyntheticAudio.silence(seconds: 1.0, sampleRate: sr)
        let utts = segmenter.segment(audio, channel: .mic)
        #expect(utts.count == 1)
    }

    @Test("an utterance shorter than minUtteranceSeconds, closed via silence, is dropped")
    func dropsBelowMinimumLength() {
        let segmenter = UtteranceSegmenter(constants: constants)
        let audio = SyntheticAudio.silence(seconds: 0.5, sampleRate: sr)
            + SyntheticAudio.tone(seconds: 0.2, sampleRate: sr)
            // 1.2s: must close the blip via the silence branch (not the
            // buffer end) or it merges with the following tone.
            + SyntheticAudio.silence(seconds: 1.2, sampleRate: sr)
            + SyntheticAudio.tone(frequency: 660, seconds: 1.5, sampleRate: sr)
            + SyntheticAudio.silence(seconds: 0.5, sampleRate: sr)
        let utts = segmenter.segment(audio, channel: .mic)
        #expect(utts.count == 1)
    }

    @Test("a continuous voiced signal longer than maxUtteranceSeconds is chunked")
    func splitsOnMaxLength() {
        let segmenter = UtteranceSegmenter(constants: constants)
        let audio = SyntheticAudio.tone(seconds: 65.0, sampleRate: sr)
        let utts = segmenter.segment(audio, channel: .mic)
        #expect(utts.count >= 3)
        for u in utts.dropLast() {
            let duration = Double(u.samples.count) / Double(sr)
            #expect(duration <= constants.maxUtteranceSeconds + constants.frameSeconds)
        }
    }

    @Test("constants stay aligned with eval/meeting_notes_eval/run_eval.py")
    func referenceConstants() {
        #expect(constants.sampleRate == 16_000)
        #expect(constants.frameSeconds == 0.03)
        #expect(constants.silenceRMS == 0.004)
        #expect(constants.minUtteranceSeconds == 1.0)
        #expect(constants.maxUtteranceSeconds == 28.0)
        #expect(constants.trailingSilenceSeconds == 1.0)
    }

    @Test("UtteranceStream emits the same utterances as batch segment(), incrementally")
    func streamingMatchesBatch() async {
        let stream = UtteranceStream(channel: .mic, constants: constants)
        let a = SyntheticAudio.tone(seconds: 1.5, sampleRate: sr)
        let gap = SyntheticAudio.silence(seconds: 1.3, sampleRate: sr)
        let b = SyntheticAudio.tone(frequency: 660, seconds: 1.5, sampleRate: sr)
        let trailing = SyntheticAudio.silence(seconds: 1.0, sampleRate: sr)
        let whole = a + gap + b + trailing

        var emitted: [Utterance] = []
        let chunkSize = 160
        var i = 0
        while i < whole.count {
            let end = min(i + chunkSize, whole.count)
            emitted.append(contentsOf: await stream.push(Array(whole[i..<end])))
            i = end
        }
        emitted.append(contentsOf: await stream.finish())

        #expect(emitted.count == 2)
        #expect(emitted[0].startOffsetSeconds < emitted[1].startOffsetSeconds)
        #expect(emitted.allSatisfy { $0.channel == .mic })
    }

    @Test("runSegmenterChecks reports no failures (CLT-runnable self-check parity)")
    func plainFunctionFallbackAgrees() {
        #expect(runSegmenterChecks().isEmpty)
    }

    @Test("UtteranceStream emits max-length-capped utterances incrementally, with zero silence")
    func streamingEmitsCappedUtterancesWithoutSilence() async {
        // Regression for the 2026-08-12 field bug (see SegmenterChecks):
        // continuous voiced audio with no trailing-silence gap must still
        // emit capped utterances DURING pushes, not buffer until finish().
        let stream = UtteranceStream(channel: .mic, constants: constants)
        let tone = SyntheticAudio.tone(seconds: 65.0, sampleRate: sr)
        var during: [Utterance] = []
        var i = 0
        while i < tone.count {
            let end = min(i + sr, tone.count)
            during.append(contentsOf: await stream.push(Array(tone[i..<end])))
            i = end
        }
        #expect(during.count >= 2)
        let flushed = await stream.finish()
        #expect(during.count + flushed.count >= 3)
    }
}
#endif
