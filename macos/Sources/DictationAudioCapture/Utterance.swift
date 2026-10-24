import Foundation

/// One energy-gated utterance: mono 16 kHz Float32 samples, tagged with the
/// channel it came from and its offset (in seconds) from the start of the
/// capture session that produced it.
public struct Utterance: Sendable, Equatable {
    public let channel: AudioChannel
    /// Seconds from session start to the utterance's first sample.
    public let startOffsetSeconds: Double
    /// Mono, 16 kHz, Float32, range [-1, 1] — matches
    /// `UtteranceSegmenter.Constants.sampleRate` and the wire format
    /// `transcription_worker/asr_server.py` expects (see the eval harness'
    /// `Worker.transcribe`, which converts this same shape to 16-bit PCM).
    public let samples: [Float]

    public init(channel: AudioChannel, startOffsetSeconds: Double, samples: [Float]) {
        self.channel = channel
        self.startOffsetSeconds = startOffsetSeconds
        self.samples = samples
    }

    /// Duration of the utterance, in seconds, at `sampleRate`.
    public func durationSeconds(sampleRate: Int) -> Double {
        Double(samples.count) / Double(sampleRate)
    }
}
