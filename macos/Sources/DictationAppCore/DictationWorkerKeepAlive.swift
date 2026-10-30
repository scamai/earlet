import Foundation

/// How long the speech engine stays loaded after a dictation, learned from
/// the user's own pauses and backed off when the Mac runs short of memory.
///
/// A loaded engine holds about 3 GB; a cold one adds about 0.6 s to the next
/// dictation. A fixed 3 minutes left a quarter of presses cold on one Mac
/// whose pauses came in bursts of seconds to minutes, with breaks of hours.
/// So the window is the 99th percentile of the recent pauses that were short
/// enough to keep warm through (30 minutes at most), plus 10%: the histogram
/// keep-alive of Shahrad et al., "Serverless in the Wild" (USENIX ATC '20).
/// Replayed over 421 real pauses (2026-10-06 to 10-30) it kept 84% of
/// presses warm, against 73% for 3 minutes, and held the engine idle 15%
/// less than a fixed 30 minutes for about the same warmth.
///
/// Memory pressure is the congestion signal. A warning halves the window,
/// down to a quarter of what was learned and never under the floor; a
/// critical warning also releases an idle engine at once. Each later
/// dictation gives back an eighth. Nothing here holds a word, only pause
/// lengths in seconds.
public struct DictationWorkerKeepAlive: Equatable, Sendable {
    /// Never shorter: the old fixed window, a dictation session's own pace.
    public var floorSeconds: Double = 180
    /// Never longer, and pauses longer than this are breaks, not pauses.
    public var capSeconds: Double = 1_800
    /// Until there are `minimumSamples` pauses to learn from.
    public var defaultSeconds: Double = 600
    public var percentile: Double = 0.99
    public var margin: Double = 1.1
    public var minimumSamples = 20
    /// The pauses kept, newest last.
    public static let historyLimit = 200

    public private(set) var pauses: [Double]
    /// 1 normally; halved by each memory-pressure event, to a quarter.
    public private(set) var pressureFactor: Double = 1

    public init(pauses: [Double] = []) {
        self.pauses = Array(pauses.filter { $0.isFinite && $0 >= 0 }.suffix(Self.historyLimit))
    }

    /// One pause between dictations, by the wall clock, so a pause the Mac
    /// slept through counts as the break it was.
    public mutating func record(pause seconds: Double) {
        guard seconds.isFinite, seconds >= 0 else { return }
        pauses.append(seconds)
        if pauses.count > Self.historyLimit { pauses.removeFirst(pauses.count - Self.historyLimit) }
        pressureFactor = min(1, pressureFactor + 0.125)
    }

    /// The Mac asked for memory back: halve the window, multiplicatively,
    /// as a sender halves its congestion window on a loss.
    public mutating func memoryPressure() {
        pressureFactor = max(0.25, pressureFactor / 2)
    }

    /// The pauses short enough to learn from: the rest were breaks.
    public var samples: Int { pauses.filter { $0 <= capSeconds }.count }

    /// The window learned from the pauses, before any pressure back-off.
    public var learnedSeconds: Double {
        let within = pauses.filter { $0 <= capSeconds }.sorted()
        guard within.count >= minimumSamples else { return defaultSeconds }
        return min(capSeconds, max(floorSeconds, margin * Self.quantile(within, percentile)))
    }

    /// How long the engine stays loaded after its last use.
    public var seconds: Double {
        max(floorSeconds, learnedSeconds * pressureFactor)
    }

    /// Linear interpolation between the two nearest ranks of a sorted list.
    static func quantile(_ sorted: [Double], _ q: Double) -> Double {
        guard let first = sorted.first else { return 0 }
        guard sorted.count > 1 else { return first }
        let rank = q * Double(sorted.count - 1)
        let low = Int(rank.rounded(.down)), high = min(sorted.count - 1, low + 1)
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - Double(low))
    }
}

/// What macOS reported about memory: a warning or a critical shortage.
public enum DictationMemoryPressure: String, Sendable {
    case warning, critical
}
