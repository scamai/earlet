import Foundation

/// A single named assertion failure. Small local duplicate of
/// `DictationIPC.CheckFailure` — this module is dependency-free by design (see
/// `AudioChannel.swift`), so it does not import `DictationIPC` just for this
/// shape.
public struct CheckFailure: Error, CustomStringConvertible, Sendable {
    public let message: String
    public var description: String { message }
}

/// Synthetic-waveform helpers shared by `runSegmenterChecks()` and the
/// swift-testing suite in `Tests/DictationAudioCaptureTests` — tone bursts
/// separated by silence, so the segmenter can be exercised with **no audio
/// hardware**.
public enum SyntheticAudio {
    /// A pure sine tone at `frequency` Hz, `amplitude` in [0, 1], `seconds`
    /// long at `sampleRate`. Amplitude well above `SegmenterConstants.silenceRMS`
    /// (default 0.004) reads as "voiced"; well below reads as "silence" —
    /// tests pick amplitude relative to the RMS gate under test.
    public static func tone(
        frequency: Double = 440,
        amplitude: Float = 0.5,
        seconds: Double,
        sampleRate: Int = 16_000
    ) -> [Float] {
        let count = Int(Double(sampleRate) * seconds)
        return (0..<count).map { i in
            amplitude * Float(sin(2 * Double.pi * frequency * Double(i) / Double(sampleRate)))
        }
    }

    /// `seconds` of digital silence (exact zeros — well under any sane RMS gate).
    public static func silence(seconds: Double, sampleRate: Int = 16_000) -> [Float] {
        Array(repeating: Float(0), count: Int(Double(sampleRate) * seconds))
    }
}

/// Executes the segmenter assertions and returns the list of failures
/// (empty == all passed) — runs today on a Command-Line-Tools-only
/// toolchain via the app's `--self-check` command
/// (`runUtteranceSegmenterSelfCheck()`); mirrors the swift-testing suite in
/// `Tests/DictationAudioCaptureTests/UtteranceSegmenterTests.swift` for
/// environments where `swift test` works.
public func runSegmenterChecks() -> [CheckFailure] {
    var failures: [CheckFailure] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(CheckFailure(message: message())) }
    }

    let constants = SegmenterConstants()
    let segmenter = UtteranceSegmenter(constants: constants)
    let sr = constants.sampleRate

    // MARK: single utterance, silence padded on both sides

    do {
        let lead = SyntheticAudio.silence(seconds: 0.5, sampleRate: sr)
        let burst = SyntheticAudio.tone(seconds: 2.0, sampleRate: sr)
        // 1.2s, not 1.0s: frame-quantized at 0.03s/frame, exactly 1.0s of
        // silence rounds down to 33 frames (0.99s), just under the 1.0s
        // trailingSilenceSeconds threshold, so it would never trip the
        // silence-close branch and this utterance would instead run to the
        // buffer end (duration ~3.0, not ~2.0). Give real margin above the
        // threshold.
        let trail = SyntheticAudio.silence(seconds: 1.2, sampleRate: sr)
        let audio = lead + burst + trail
        let utts = segmenter.segment(audio, channel: .mic)

        check(utts.count == 1, "single-utterance: expected 1 utterance, got \(utts.count)")
        if let u = utts.first {
            // Loose tolerance: frame quantization (480-sample analysis
            // frames) means the tone/silence boundary rarely lands exactly
            // on a frame edge — the point of this check is "found roughly
            // where the tone starts", not sample-exact alignment.
            check(abs(u.startOffsetSeconds - 0.5) < 0.1,
                  "single-utterance: start offset \(u.startOffsetSeconds), want ~0.5")
            let durationSeconds = Double(u.samples.count) / Double(sr)
            check(abs(durationSeconds - 2.0) < 0.1,
                  "single-utterance: duration \(durationSeconds), want ~2.0")
            check(u.channel == .mic, "single-utterance: channel not preserved")
        }
    }

    // MARK: two utterances split by a silence gap ≥ trailingSilenceSeconds

    do {
        let a = SyntheticAudio.tone(seconds: 1.5, sampleRate: sr)
        let gap = SyntheticAudio.silence(seconds: 1.3, sampleRate: sr) // > 1.0s TRAIL_SILENCE_S
        let b = SyntheticAudio.tone(frequency: 660, seconds: 1.5, sampleRate: sr)
        let audio = a + gap + b
        let utts = segmenter.segment(audio, channel: .system)

        check(utts.count == 2, "two-utterance: expected 2 utterances, got \(utts.count)")
        if utts.count == 2 {
            check(utts[0].startOffsetSeconds < utts[1].startOffsetSeconds,
                  "two-utterance: not in chronological order")
            check(utts.allSatisfy { $0.channel == .system },
                  "two-utterance: channel not preserved on all utterances")
        }
    }

    // MARK: a gap SHORTER than trailingSilenceSeconds does NOT split the utterance

    do {
        let a = SyntheticAudio.tone(seconds: 1.0, sampleRate: sr)
        let gap = SyntheticAudio.silence(seconds: 0.2, sampleRate: sr) // < 1.0s
        let b = SyntheticAudio.tone(seconds: 1.0, sampleRate: sr)
        let audio = a + gap + b + SyntheticAudio.silence(seconds: 1.0, sampleRate: sr)
        let utts = segmenter.segment(audio, channel: .mic)
        check(utts.count == 1, "short-gap: expected the gap to NOT split (1 utterance), got \(utts.count)")
    }

    // MARK: below the minimum-utterance-length filter is dropped when closed via silence

    do {
        // A short blip (well under MIN_UTT_S) closed by a real trailing
        // silence gap must be dropped by the minimum-length filter — but
        // only reachable via the "closed by silence" branch; see the next
        // block for why a trailing-at-buffer-end blip is NOT filtered.
        let blip = SyntheticAudio.tone(seconds: 0.2, sampleRate: sr)
        let audio = SyntheticAudio.silence(seconds: 0.5, sampleRate: sr)
            + blip
            // 1.2s of margin above the 1.0s threshold (see the
            // single-utterance test above) — this MUST close the blip via
            // the silence branch, not the buffer end, or it merges with the
            // following tone instead of being droppable on its own.
            + SyntheticAudio.silence(seconds: 1.2, sampleRate: sr)
            // A second, legitimately long utterance so the buffer doesn't
            // end mid-silence right after the blip (which would leave
            // `start == nil` and no trailing span to force-close anyway —
            // this keeps the test unambiguous either way).
            + SyntheticAudio.tone(frequency: 660, seconds: 1.5, sampleRate: sr)
            + SyntheticAudio.silence(seconds: 0.5, sampleRate: sr)
        let utts = segmenter.segment(audio, channel: .mic)
        check(utts.count == 1, "min-length filter: expected the 0.2s blip dropped (1 utterance left), got \(utts.count)")
    }

    // MARK: max-utterance cap splits a very long continuous voiced signal

    do {
        let audio = SyntheticAudio.tone(seconds: 65.0, sampleRate: sr) // > 2x MAX_UTT_S (28s)
        let utts = segmenter.segment(audio, channel: .mic)
        check(utts.count >= 3, "max-length cap: expected >= 3 chunks from 65s, got \(utts.count)")
        for u in utts.dropLast() {
            let durationSeconds = Double(u.samples.count) / Double(sr)
            check(durationSeconds <= constants.maxUtteranceSeconds + constants.frameSeconds,
                  "max-length cap: chunk duration \(durationSeconds) exceeds \(constants.maxUtteranceSeconds)")
        }
    }

    // MARK: reference-parity — constants match the caption-evaluation harness

    do {
        check(constants.sampleRate == 16_000, "constants: sampleRate drifted from reference")
        check(constants.frameSeconds == 0.03, "constants: frameSeconds drifted from reference")
        check(constants.silenceRMS == 0.004, "constants: silenceRMS drifted from reference")
        check(constants.minUtteranceSeconds == 1.0, "constants: minUtteranceSeconds drifted from reference")
        check(constants.maxUtteranceSeconds == 28.0, "constants: maxUtteranceSeconds drifted from reference")
        check(constants.trailingSilenceSeconds == 1.0, "constants: trailingSilenceSeconds drifted from reference")
    }

    // MARK: UtteranceStream — incremental pushes emit the same utterances as batch segment()

    do {
        let a = SyntheticAudio.tone(seconds: 1.5, sampleRate: sr)
        let gap = SyntheticAudio.silence(seconds: 1.3, sampleRate: sr)
        let b = SyntheticAudio.tone(frequency: 660, seconds: 1.5, sampleRate: sr)
        let trailingSilence = SyntheticAudio.silence(seconds: 1.0, sampleRate: sr)
        let whole = a + gap + b + trailingSilence

        let emitted: [Utterance] = ChecksRunLoop.run {
            let stream = UtteranceStream(channel: .mic, constants: constants)
            // Feed in small, hardware-realistic chunks (≈ 10 ms at 16kHz)
            // rather than one giant push, to exercise the sub-frame
            // buffering path.
            let chunkSize = 160
            var i = 0
            var result: [Utterance] = []
            while i < whole.count {
                let end = min(i + chunkSize, whole.count)
                let pushed = await stream.push(Array(whole[i..<end]))
                result.append(contentsOf: pushed)
                i = end
            }
            let flushed = await stream.finish()
            result.append(contentsOf: flushed)
            return result
        }

        check(emitted.count == 2, "UtteranceStream: expected 2 incremental utterances, got \(emitted.count)")
        if emitted.count == 2 {
            check(emitted[0].startOffsetSeconds < emitted[1].startOffsetSeconds,
                  "UtteranceStream: not chronological")
            check(emitted.allSatisfy { $0.channel == .mic }, "UtteranceStream: channel not preserved")
            let dur0 = Double(emitted[0].samples.count) / Double(sr)
            check(abs(dur0 - 1.5) < 0.1, "UtteranceStream: first utterance duration \(dur0), want ~1.5")
        }
    }

    // MARK: UtteranceStream — max-length-capped utterances emit INCREMENTALLY (no silence required)

    do {
        // Regression for the 2026-08-12 field bug: a channel that never
        // sees a full trailing-silence gap (e.g. an open mic with ambient
        // noise) closes utterances ONLY via the 28s max-length cap — and
        // each capped span is immediately followed by a still-open
        // trailing span. The emission guard looked at `spans.last` (that
        // open span) instead of the last CLOSED span, so nothing was ever
        // emitted until finish(): the whole meeting buffered in memory,
        // then flooded the capacity-8 ASR queue at session end and mostly
        // dropped. The guard now uses `spans.last(where: \.closed)`.
        let tone = SyntheticAudio.tone(seconds: 65.0, sampleRate: sr) // > 2x MAX_UTT_S, zero silence
        let (emittedDuringPush, emittedAtFinish) = ChecksRunLoop.run {
            let stream = UtteranceStream(channel: .mic, constants: constants)
            var during: [Utterance] = []
            let chunkSize = sr // 1s per push
            var i = 0
            while i < tone.count {
                let end = min(i + chunkSize, tone.count)
                during.append(contentsOf: await stream.push(Array(tone[i..<end])))
                i = end
            }
            let flushed = await stream.finish()
            return (during, flushed)
        }
        check(emittedDuringPush.count >= 2,
              "UtteranceStream no-silence: expected >= 2 capped utterances emitted DURING pushes, got \(emittedDuringPush.count)")
        check(emittedDuringPush.count + emittedAtFinish.count >= 3,
              "UtteranceStream no-silence: expected >= 3 utterances total, got \(emittedDuringPush.count + emittedAtFinish.count)")
    }

    return failures
}

/// Runs `runSegmenterChecks()` and prints "segmenter-check: PASS" or
/// "segmenter-check: FAIL: <reason> (+N more)" — same PASS/FAIL-line
/// convention as the app's other self-checks, wired into its
/// `--self-check` sequence.
public func runUtteranceSegmenterSelfCheck() -> Bool {
    let failures = runSegmenterChecks()
    guard failures.isEmpty else {
        let first = failures[0].message
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("segmenter-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("segmenter-check: PASS")
    return true
}

/// Minimal synchronous-to-async bridge so `runSegmenterChecks()` (a plain
/// synchronous function, callable from the self-check chain without
/// restructuring it as async) can drive `UtteranceStream`'s actor-isolated
/// API. Blocks the calling thread on a semaphore while a `Task` runs the
/// async work and reports its result back through a lock-guarded box —
/// same "block a plain thread, let Swift Concurrency's pool do the async
/// work" shape used by the app's synchronous self-check runner for the whole
/// self-check sequence (that one pumps the main run loop instead of a
/// semaphore because it must stay on the main thread for `@MainActor` work;
/// this one has no such constraint).
enum ChecksRunLoop {
    static func run<T: Sendable>(_ work: @escaping @Sendable () async -> T) -> T {
        let box = SyncResultBox<T>()
        let sema = DispatchSemaphore(value: 0)
        Task {
            box.set(await work())
            sema.signal()
        }
        sema.wait()
        return box.value
    }
}

private final class SyncResultBox<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: T?
    var value: T { lock.lock(); defer { lock.unlock() }; return stored! }
    func set(_ v: T) { lock.lock(); stored = v; lock.unlock() }
}
