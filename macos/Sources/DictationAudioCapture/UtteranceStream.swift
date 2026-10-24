import Foundation

/// Incremental wrapper around `UtteranceSegmenter` for a single, continuous
/// audio channel (one instance per mic capture, one per system-audio
/// capture — never shared, since the two streams must never mix).
///
/// Implementation note: rather than hand-write a separate incremental state
/// machine, this re-runs the same tested, pure `UtteranceSegmenter.segment`
/// logic over a small rolling buffer every time new audio arrives, then
/// emits only the utterances that have actually **closed** (silence gap or
/// max-length cap) and drops their samples from the buffer. The buffer is
/// bounded by construction: it can never hold more than one
/// `maxUtteranceSeconds` worth of audio (28 s * 16 kHz ≈ 448 KB of Float32),
/// because anything beyond that is always force-closed by the max-length
/// cap on the very next `push`. Re-scanning that little audio on every push
/// is negligible next to real-time capture (16 kHz vs. whatever cadence the
/// caller batches frames at).
public actor UtteranceStream {
    public let channel: AudioChannel
    private let segmenter: UtteranceSegmenter
    private var pending: [Float] = []
    private var pendingStartOffsetSeconds: Double = 0

    private var continuation: AsyncStream<Utterance>.Continuation?
    public let stream: AsyncStream<Utterance>

    public init(channel: AudioChannel, constants: SegmenterConstants = SegmenterConstants()) {
        self.channel = channel
        self.segmenter = UtteranceSegmenter(constants: constants)
        var escapedContinuation: AsyncStream<Utterance>.Continuation!
        self.stream = AsyncStream { continuation in
            escapedContinuation = continuation
        }
        self.continuation = escapedContinuation
    }

    /// Feed newly captured samples, in stream order. Any utterances that
    /// closed as a result are yielded on `stream` before this call returns
    /// (the `AsyncStream` continuation buffers, so this never blocks on a
    /// slow consumer).
    @discardableResult
    public func push(_ samples: [Float]) -> [Utterance] {
        guard !samples.isEmpty else { return [] }
        pending.append(contentsOf: samples)

        let frame = segmenter.constants.frameLength
        guard frame > 0, pending.count >= frame else { return [] }

        let n = pending.count / frame
        let voiced = UtteranceSegmenter.voicedFrames(
            pending, frame: frame, count: n, threshold: segmenter.constants.silenceRMS
        )
        let spans = segmenter.coreSegment(frameCount: n, voiced: voiced, includeTrailingPartial: false)
        // Use the last CLOSED span, not the last span: after the max-length
        // cap force-closes an utterance mid-voiced-audio, `spans` ends with
        // the still-open trailing span, and checking `spans.last` would
        // suppress emission of the closed spans in front of it forever — a
        // channel that never sees a full trailing-silence gap (e.g. an open
        // mic with ambient noise) would then buffer the entire meeting and
        // dump it as one end-of-session flood (dropping most of it in the
        // bounded ASR queue). Field bug, 2026-08-12.
        guard let last = spans.last(where: \.closed) else {
            // Nothing closed yet (either no spans, or the only span is the
            // still-open trailing one) — keep buffering.
            return []
        }

        var emitted: [Utterance] = []
        for span in spans where span.closed {
            let sampleStart = span.startFrame * frame
            let sampleEnd = min(span.endFrame * frame, pending.count)
            let utterance = Utterance(
                channel: channel,
                startOffsetSeconds: pendingStartOffsetSeconds + Double(span.startFrame) * segmenter.constants.frameSeconds,
                samples: Array(pending[sampleStart..<sampleEnd])
            )
            emitted.append(utterance)
            continuation?.yield(utterance)
        }

        // Drop everything through the end of the last closed span; keep any
        // remaining (possibly still-open) tail for the next push.
        let consumedSamples = last.endFrame * frame
        if consumedSamples > 0 {
            pending.removeFirst(min(consumedSamples, pending.count))
            pendingStartOffsetSeconds += Double(consumedSamples) / Double(segmenter.constants.sampleRate)
        }
        return emitted
    }

    /// Call at session end (meeting-detection lifecycle stop) to force-close
    /// any still-open (or still-unflushed, sub-frame) audio and finish the
    /// stream. Returns everything emitted, in order. Safe to call once; a
    /// second call sees an empty buffer, yields nothing, and finishes the
    /// (already-finished) continuation again, which is a no-op.
    @discardableResult
    public func finish() -> [Utterance] {
        defer {
            pending.removeAll()
            continuation?.finish()
        }
        guard !pending.isEmpty else { return [] }
        // `segment` (offline mode) force-closes any trailing open span at
        // the buffer end — exactly the "session just ended" semantics.
        let closed = segmenter.segment(pending, channel: channel)
        let utterances = closed.map { utt in
            Utterance(
                channel: channel,
                startOffsetSeconds: pendingStartOffsetSeconds + utt.startOffsetSeconds,
                samples: utt.samples
            )
        }
        for utterance in utterances {
            continuation?.yield(utterance)
        }
        return utterances
    }
}
