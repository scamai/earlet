import Foundation

/// Energy-gated utterance chunker.
///
/// **These constants MUST stay bit-identical to the Python reference** in
    /// The caption-evaluation harness (`FRAME_S` / `SILENCE_RMS` /
/// `MIN_UTT_S` / `MAX_UTT_S` / `TRAIL_SILENCE_S`) and to the eventual C#
/// `UtteranceSegmenter` on the Windows branch this ports from. The two
/// implementations classify the SAME captured audio into utterances that
/// both feed the SAME `transcription_worker` wire protocol — a drift here
/// silently changes chunk boundaries (and therefore ASR accuracy /
/// latency) on only one platform. If you change one, change both, in the
/// same commit, and re-run both test suites.
public struct SegmenterConstants: Sendable, Equatable {
    /// Input sample rate. `MicCapture` resamples to this before handing
    /// frames to the segmenter.
    public var sampleRate: Int = 16_000
    /// Analysis frame length, in seconds.
    public var frameSeconds: Double = 0.03
    /// A frame counts as silence when its RMS is at or below this.
    public var silenceRMS: Float = 0.004
    /// Utterances shorter than this are dropped (mirrors the Python
    /// reference's `MIN_UTT_S` filter on the trailing-silence-close path).
    public var minUtteranceSeconds: Double = 1.0
    /// Hard cap — stays under Whisper's 30 s decode window.
    public var maxUtteranceSeconds: Double = 28.0
    /// This much continuous trailing silence closes an open utterance.
    ///
    /// **2026-08-06 field report: "The meeting live transcript stops when
    /// it breaks down one full sentence into two sentences."** At 0.6s this
    /// closed an utterance on an ordinary mid-sentence breath or
    /// word-finding pause — normal speech regularly has pauses in the
    /// 0.4-0.9s range that are NOT sentence boundaries (a 2024 conversational-
    /// pause study puts the median within-turn pause around 0.5-0.8s, with a
    /// long tail well past 1s), so 0.6s was chopping sentences mid-way far
    /// more often than it was catching real ends. Raised to 1.0s: still
    /// short enough that end-of-turn silence (which tends to run
    /// noticeably longer, >1.5-2s, once a speaker actually finishes) closes
    /// promptly, but long enough to ride out a typical thinking pause
    /// without splitting the sentence. Chosen from the middle of the
    /// 0.9-1.2s range this fix targets rather than the top: `UtteranceStream`
    /// buffers UNCLOSED audio the whole time an utterance stays open, and
    /// `NotesSessionController` doesn't emit anything to the live UI until
    /// a chunk closes — 1.0s keeps live-transcript latency from growing too
    /// far behind real speech, while `TranscriptSegmentMerger` (see
    /// A captioning layer sits on top of this to stitch back together the
    /// mid-sentence splits this threshold alone still won't catch (a
    /// deliberate pause longer than the threshold, mid-sentence).
    ///
    /// **MUST also update the Python reference's `TRAIL_SILENCE_S` in
    /// to `1.0`** (out of scope for
    /// this change per the macOS-only task boundary — do NOT edit anything
    /// under `eval/` here) so both platforms keep chunking the same
    /// captured audio into the same utterance boundaries, per the class
    /// doc comment above.
    public var trailingSilenceSeconds: Double = 1.0

    public init(
        sampleRate: Int = 16_000,
        frameSeconds: Double = 0.03,
        silenceRMS: Float = 0.004,
        minUtteranceSeconds: Double = 1.0,
        maxUtteranceSeconds: Double = 28.0,
        trailingSilenceSeconds: Double = 1.0
    ) {
        self.sampleRate = sampleRate
        self.frameSeconds = frameSeconds
        self.silenceRMS = silenceRMS
        self.minUtteranceSeconds = minUtteranceSeconds
        self.maxUtteranceSeconds = maxUtteranceSeconds
        self.trailingSilenceSeconds = trailingSilenceSeconds
    }

    /// Samples per analysis frame, rounded like the Python reference's
    /// `int(SR * FRAME_S)`.
    public var frameLength: Int {
        Int(Double(sampleRate) * frameSeconds)
    }
}

/// Splits a continuous stream of mono Float32 samples into energy-gated
/// utterances. Stateless and pure over a fixed buffer — see `UtteranceStream`
/// for the incremental/live wrapper used by real capture.
public struct UtteranceSegmenter: Sendable {
    public let constants: SegmenterConstants

    public init(constants: SegmenterConstants = SegmenterConstants()) {
        self.constants = constants
    }

    /// One closed (or, if `includeTrailingPartial`, still-open-at-buffer-end)
    /// span, in frame indices — the unit `coreSegment` reasons in before
    /// slicing actual sample data.
    struct FrameSpan: Equatable {
        var startFrame: Int
        var endFrame: Int   // exclusive
        var closed: Bool    // false only for the final, still-open partial
    }

    /// Direct, frame-indexed port of `run_eval.py`'s `segment()`. Kept
    /// separate from the public `segment(_:channel:)` so the streaming
    /// wrapper can ask "what's still open" (`closed == false`) without
    /// re-deriving frame math from sample offsets.
    ///
    /// - Parameter includeTrailingPartial: when `false`, an utterance still
    ///   open at the end of `audio` (never closed by silence or the max-
    ///   length cap) is NOT returned — used by `UtteranceStream`, which
    ///   will see more audio later and should not emit early. When `true`
    ///   (the default, offline/batch use), a trailing open utterance is
    ///   force-closed at the buffer end if it meets the minimum length —
    ///   mirrors the Python reference's unconditional final `if start is
    ///   not None: utts.append(...)`.
    func coreSegment(frameCount n: Int, voiced: [Bool], includeTrailingPartial: Bool) -> [FrameSpan] {
        let frameSeconds = constants.frameSeconds
        var spans: [FrameSpan] = []
        var start: Int?
        var silence = 0

        var i = 0
        while i < n {
            if voiced[i] {
                if start == nil { start = i }
                silence = 0
            } else if let s = start {
                silence += 1
                let longSilence = Double(silence) * frameSeconds >= constants.trailingSilenceSeconds
                let tooLong = Double(i - s) * frameSeconds >= constants.maxUtteranceSeconds
                if longSilence || tooLong {
                    let end = i - silence + 1
                    if Double(end - s) * frameSeconds >= constants.minUtteranceSeconds {
                        spans.append(FrameSpan(startFrame: s, endFrame: end, closed: true))
                    }
                    start = nil
                    silence = 0
                }
            }
            // Second, independent cap check — reachable only when frame `i`
            // IS voiced this iteration (the `else` branch above already
            // handled the silent case), matching the Python reference's
            // un-elif'd trailing `if` at the bottom of its loop body.
            //
            // Note the slice is `..<i`, EXCLUDING frame `i` itself, exactly
            // like the Python reference's `audio[start*frame:i*frame]` —
            // frame `i` (the one that trips the boundary) is silently
            // dropped and the next utterance starts at `i+1`. This looks
            // like an off-by-one in the reference, but reproducing it
            // exactly (not "fixing" it) is the point: both platforms must
            // chunk the SAME captured audio into the SAME utterance
            // boundaries.
            if let s = start, Double(i - s) * frameSeconds >= constants.maxUtteranceSeconds {
                spans.append(FrameSpan(startFrame: s, endFrame: i, closed: true))
                start = nil
                silence = 0
            }
            i += 1
        }
        if let s = start {
            // The Python reference appends this trailing span
            // unconditionally, with no minimum-length filter — matched
            // here for `includeTrailingPartial == true` (offline/batch).
            let closed = includeTrailingPartial
            spans.append(FrameSpan(startFrame: s, endFrame: n, closed: closed))
        }
        return spans
    }

    /// Segment a complete, offline buffer (mirrors `run_eval.py`'s
    /// `segment(audio)`). The trailing utterance, if any, is force-closed
    /// at the buffer end — appropriate for "segment this whole recording",
    /// not for a live stream still receiving audio (use `UtteranceStream`
    /// for that).
    public func segment(_ audio: [Float], channel: AudioChannel) -> [Utterance] {
        let frame = constants.frameLength
        guard frame > 0, audio.count >= frame else { return [] }
        let n = audio.count / frame
        let voiced = Self.voicedFrames(audio, frame: frame, count: n, threshold: constants.silenceRMS)
        let spans = coreSegment(frameCount: n, voiced: voiced, includeTrailingPartial: true)
        return spans.map { span in
            let sampleStart = span.startFrame * frame
            let sampleEnd = min(span.endFrame * frame, audio.count)
            return Utterance(
                channel: channel,
                startOffsetSeconds: Double(span.startFrame) * constants.frameSeconds,
                samples: Array(audio[sampleStart..<sampleEnd])
            )
        }
    }

    /// Per-frame RMS-above-threshold, i.e. "is this frame voiced".
    static func voicedFrames(_ audio: [Float], frame: Int, count: Int, threshold: Float) -> [Bool] {
        var result = [Bool]()
        result.reserveCapacity(count)
        audio.withUnsafeBufferPointer { buf in
            for f in 0..<count {
                let start = f * frame
                var sumSquares: Float = 0
                for i in start..<(start + frame) {
                    let v = buf[i]
                    sumSquares += v * v
                }
                let rms = (sumSquares / Float(frame)).squareRoot()
                result.append(rms > threshold)
            }
        }
        return result
    }
}
