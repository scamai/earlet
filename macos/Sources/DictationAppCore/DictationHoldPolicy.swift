import DictationKit
import Foundation

/// The user-facing hands-free silence choices.  The detector itself remains
/// independent of the setting so a held take can use the same audio evidence.
public enum DictationSilenceSetting: String, CaseIterable, Equatable, Sendable {
    case thirtySeconds = "30"
    case sixtySeconds = "60"
    case off

    public var label: String { self == .off ? "Off" : "\(rawValue) seconds" }

    public var stopAfterSeconds: TimeInterval? {
        switch self {
        case .thirtySeconds: return 30
        case .sixtySeconds: return 60
        case .off: return nil
        }
    }
}

public enum DictationHoldMode: Equatable, Sendable {
    case held
    case handsFree
}

public enum DictationHoldStopReason: Equatable, Sendable {
    case handsFreeNeverSpoke
    case handsFreeSilence
    case heldSilenceGuard
}

public enum DictationHoldDecision: Equatable, Sendable {
    case continueListening
    case silenceWarning
    case stop(DictationHoldStopReason)
}

/// Stage-1 hold values.  A single value object gives the model one source for
/// the ceiling and countdown shown by the HUD and for the silence policy.
public struct DictationHoldPolicy: Equatable, Sendable {
    public var maximumHoldSeconds: TimeInterval
    public var countdownSeconds: TimeInterval
    public var silenceSetting: DictationSilenceSetting
    public var neverSpokeSeconds: TimeInterval
    public var afterSpeechWarningSeconds: TimeInterval
    public var heldNoSpeechSeconds: TimeInterval

    public init(
        maximumHoldSeconds: TimeInterval = 10 * 60,
        countdownSeconds: TimeInterval = 60,
        silenceSetting: DictationSilenceSetting = .thirtySeconds,
        neverSpokeSeconds: TimeInterval = 15,
        afterSpeechWarningSeconds: TimeInterval = 10,
        heldNoSpeechSeconds: TimeInterval = 60
    ) {
        self.maximumHoldSeconds = maximumHoldSeconds
        self.countdownSeconds = countdownSeconds
        self.silenceSetting = silenceSetting
        self.neverSpokeSeconds = neverSpokeSeconds
        self.afterSpeechWarningSeconds = afterSpeechWarningSeconds
        self.heldNoSpeechSeconds = heldNoSpeechSeconds
    }

    public static let stage1 = DictationHoldPolicy()
    public var silenceStopSeconds: TimeInterval? { silenceSetting.stopAfterSeconds }
}

public struct DictationHoldSnapshot: Equatable, Sendable {
    public let audioSeconds: TimeInterval
    public let speechDetected: Bool
    public let silenceSeconds: TimeInterval?
    public let decision: DictationHoldDecision
    /// The run of audio, up to now, in which no sample reached
    /// `DictationSession.silentInputPeak`: the input is sending nothing.
    public let silentInputSeconds: TimeInterval

    public var neverSpoke: Bool { !speechDetected }
}

/// Thread-safe, PCM-time silence evidence for one hold.  Input is retained by
/// the model separately; this tracker only keeps a bounded frame-level ring
/// and a partial frame, so checking the policy never sorts per sample.
public final class DictationHoldTracker: @unchecked Sendable {
    public static let sampleRate = 16_000
    public static let frameSamples = 480 // 30 ms at 16 kHz
    /// How long a hold hears nothing from the input before the hint says so:
    /// past a Bluetooth headset's wake-up, which `MicCapture` also waits out
    /// for this long.
    public static let silentInputNoticeSeconds: TimeInterval = 2.5

    private let lock = NSLock()
    private let policy: DictationHoldPolicy
    private var pending: [Float] = []
    private var frameLevels: [Float] = []
    private var audioSeconds: TimeInterval = 0
    private var confirmedSpeech = false
    private var consecutiveSpeechFrames = 0
    private var lastSpeechAudioSeconds: TimeInterval?
    private var silentInputFrames = 0

    public init(policy: DictationHoldPolicy = .stage1) {
        self.policy = policy
        frameLevels.reserveCapacity(334)
    }

    /// Feed retained 16 kHz PCM. Arbitrary buffer boundaries are accepted.
    public func append(_ samples: [Float]) {
        guard !samples.isEmpty else { return }
        lock.lock()
        pending.append(contentsOf: samples)
        while pending.count >= Self.frameSamples {
            let frame = Array(pending.prefix(Self.frameSamples))
            pending.removeFirst(Self.frameSamples)
            process(frame)
        }
        lock.unlock()
    }

    public func reset() {
        lock.lock()
        pending.removeAll(keepingCapacity: true)
        frameLevels.removeAll(keepingCapacity: true)
        audioSeconds = 0
        confirmedSpeech = false
        consecutiveSpeechFrames = 0
        lastSpeechAudioSeconds = nil
        silentInputFrames = 0
        lock.unlock()
    }

    public func snapshot(mode: DictationHoldMode = .handsFree) -> DictationHoldSnapshot {
        lock.lock()
        defer { lock.unlock() }
        let silence = lastSpeechAudioSeconds.map { max(0, audioSeconds - $0) }
        let decision: DictationHoldDecision
        switch mode {
        case .held:
            let heldSilence = silence ?? audioSeconds
            if heldSilence >= policy.heldNoSpeechSeconds {
                decision = .stop(.heldSilenceGuard)
            } else {
                decision = .continueListening
            }
        case .handsFree:
            if !confirmedSpeech {
                if policy.silenceStopSeconds != nil,
                   audioSeconds >= policy.neverSpokeSeconds {
                    decision = .stop(.handsFreeNeverSpoke)
                } else {
                    decision = .continueListening
                }
            } else if let silence,
                      let stop = policy.silenceStopSeconds {
                if silence >= stop {
                    decision = .stop(.handsFreeSilence)
                } else if silence >= max(0, stop - policy.afterSpeechWarningSeconds) {
                    decision = .silenceWarning
                } else {
                    decision = .continueListening
                }
            } else {
                decision = .continueListening
            }
        }
        return DictationHoldSnapshot(audioSeconds: audioSeconds,
                                     speechDetected: confirmedSpeech,
                                     silenceSeconds: silence,
                                     decision: decision,
                                     silentInputSeconds: Double(silentInputFrames) * 0.03)
    }

    private func process(_ frame: [Float]) {
        var sum: Float = 0
        var peak: Float = 0
        for sample in frame {
            let value = sample.isFinite ? sample : 0
            sum += value * value
            peak = max(peak, abs(value))
        }
        silentInputFrames = peak < DictationSession.silentInputPeak ? silentInputFrames + 1 : 0
        let rms = sqrt(sum / Float(frame.count))
        frameLevels.append(rms)
        let maxFrames = Int(10 / 0.03) + 1
        if frameLevels.count > maxFrames {
            frameLevels.removeFirst(frameLevels.count - maxFrames)
        }
        let sorted = frameLevels.sorted()
        let p10 = sorted[max(0, Int(Double(sorted.count - 1) * 0.10))]
        let threshold = max(Float(0.004), p10 * sqrt(10))
        let isSpeech = rms > threshold
        audioSeconds += 0.03
        if isSpeech {
            consecutiveSpeechFrames += 1
        } else {
            consecutiveSpeechFrames = 0
        }
        if consecutiveSpeechFrames >= 9 {
            confirmedSpeech = true
            lastSpeechAudioSeconds = audioSeconds
        }
    }
}
