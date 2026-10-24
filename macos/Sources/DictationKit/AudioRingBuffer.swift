import Foundation

/// A fixed-capacity FIFO of the most recent audio samples, chronological
/// order preserved (oldest first). Pure value type — no engine, no lock, no
/// TCC — so its retention/eviction/wraparound behavior is fully covered by
/// `runDictationSelfCheck()` without a real microphone.
///
/// This is what buys PRE-ROLL for warm-engine capture: `DictationCapture`
/// keeps one of these filled continuously while armed-but-not-triggered, so
/// `armTriggerDown()` can prepend "the last N ms before the keypress" to the
/// utterance instead of starting the buffer at silence. It fixes a real,
/// separate complaint from the ~800ms engine-start problem: a user who
/// starts talking a beat before the key registers (normal — speech onset
/// and a deliberate keypress are not perfectly synchronized) loses the lead
/// syllable even with an already-running engine, because "trigger-down" is
/// not "speech-down". A few hundred ms of standing pre-roll erases that gap
/// for free once the engine is warm.
public struct AudioRingBuffer: Sendable, Equatable {
    public let capacity: Int
    private var storage: [Float] = []

    public init(capacity: Int) {
        // A negative/zero capacity (e.g. preRollSeconds == 0) is a valid
        // "pre-roll disabled" configuration, not a programmer error —
        // clamp rather than trap so a bad duration degrades to "no
        // pre-roll" instead of crashing capture.
        self.capacity = max(capacity, 0)
    }

    /// Appends a chunk, evicting the oldest samples first if it would
    /// overflow `capacity`. `Array.removeFirst(k)` is O(n) in the removed
    /// count, but at pre-roll scale (hundreds of ms of 16kHz mono Float32 —
    /// a few thousand elements) and callback cadence (~10-100ms bursts,
    /// not per-sample) this is trivial; a true circular index was judged
    /// not worth the extra invariant surface for a buffer this small.
    public mutating func append(_ chunk: [Float]) {
        guard capacity > 0 else { return }
        if chunk.count >= capacity {
            // The chunk alone overflows capacity — only its tail can
            // possibly survive; skip the intermediate append+trim.
            storage = Array(chunk.suffix(capacity))
            return
        }
        storage.append(contentsOf: chunk)
        if storage.count > capacity {
            storage.removeFirst(storage.count - capacity)
        }
    }

    /// Current contents, oldest first. Never longer than `capacity`; shorter
    /// than `capacity` until the buffer has been running for the full
    /// pre-roll duration (e.g. right after `warmUp()`).
    public var samples: [Float] { storage }

    public var count: Int { storage.count }

    /// True once the buffer holds a full `capacity` samples of history —
    /// i.e. pre-roll is at full strength, not still filling from a cold
    /// `warmUp()`.
    public var isFull: Bool { capacity > 0 && storage.count >= capacity }
}
