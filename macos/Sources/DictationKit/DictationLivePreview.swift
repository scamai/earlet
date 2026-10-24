import Foundation

/// The deliberately narrow wire surface available to live dictation.
///
/// There is no `finish` operation here. A preview can be shown or cancelled,
/// but it can never become the text inserted into the user's document. The
/// authoritative result remains `DictationAsrTransport.transcribeDetailed`
/// after the key is released.
public protocol DictationPreviewTransport: Sendable {
    func startPreview(languages: [String]) async -> String?
    func pushPreview(
        streamID: String, samples: [Float], sampleRate: Int
    ) async -> DictationPreviewHypothesis?
    func cancelPreview(streamID: String) async
}

public struct DictationPreviewHypothesis: Sendable, Equatable {
    public let text: String
    public let isEndpoint: Bool

    public init(text: String, isEndpoint: Bool) {
        self.text = text
        self.isEndpoint = isEndpoint
    }
}

/// Turns the shared meeting/dictation worker's multi-line rendering into the
/// one current single-speaker line a dictation HUD needs.
public enum DictationPreviewText {
    public static func liveTail(_ reply: String?) -> String {
        guard let reply else { return "" }
        for rawLine in reply.split(separator: "\n", omittingEmptySubsequences: false).reversed() {
            var line = String(rawLine).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !line.isEmpty else { continue }
            if line.first == "[", let close = line.firstIndex(of: "]") {
                line = String(line[line.index(after: close)...])
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            }
            if !line.isEmpty { return line }
        }
        return ""
    }
}

/// Privacy- and layout-bounded trailing window for the floating preview.
/// Matches the current Windows product contract: seven whitespace-delimited
/// words and sixteen CJK grapheme clusters, with independent budgets for
/// mixed-language speech.
public enum DictationPreviewCaption {
    public static let defaultWordWindow = 7
    public static let defaultCJKCharacterBudget = 16

    private struct Segment {
        let characters: [Character]
        let isCJK: Bool
        let spaceBefore: Bool
    }

    public static func window(
        _ text: String?,
        wordWindow: Int = defaultWordWindow,
        cjkCharacterBudget: Int = defaultCJKCharacterBudget
    ) -> String {
        guard let text, !text.isEmpty else { return "" }
        if isStandaloneYou(text) { return "" }

        let wordBudget = wordWindow > 0 ? wordWindow : defaultWordWindow
        let cjkBudget = cjkCharacterBudget > 0
            ? cjkCharacterBudget : defaultCJKCharacterBudget
        let segments = buildSegments(text)
        guard !segments.isEmpty else { return "" }

        var selected: [Segment] = []
        var wordsUsed = 0
        var cjkUsed = 0

        for segment in segments.reversed() {
            if segment.isCJK {
                let remaining = cjkBudget - cjkUsed
                guard remaining > 0 else { break }
                if segment.characters.count <= remaining {
                    selected.append(segment)
                    cjkUsed += segment.characters.count
                    continue
                }
                selected.append(Segment(
                    characters: Array(segment.characters.suffix(remaining)),
                    isCJK: true,
                    spaceBefore: segment.spaceBefore))
                break
            }

            guard wordsUsed < wordBudget else { break }
            selected.append(segment)
            wordsUsed += 1
        }

        var result = ""
        for (index, segment) in selected.reversed().enumerated() {
            if index > 0, segment.spaceBefore { result.append(" ") }
            result.append(contentsOf: segment.characters)
        }
        return result
    }

    private static func isStandaloneYou(_ text: String) -> Bool {
        var trim = CharacterSet.whitespacesAndNewlines
        trim.formUnion(.punctuationCharacters)
        return text.trimmingCharacters(in: trim).lowercased() == "you"
    }

    private static func buildSegments(_ text: String) -> [Segment] {
        var result: [Segment] = []
        var current: [Character] = []
        var currentIsCJK = false
        var currentSpaceBefore = false
        var sawWhitespace = false

        func flush() {
            guard !current.isEmpty else { return }
            result.append(Segment(
                characters: current,
                isCJK: currentIsCJK,
                spaceBefore: currentSpaceBefore))
            current.removeAll(keepingCapacity: true)
        }

        for character in text {
            if isWhitespace(character) {
                flush()
                sawWhitespace = true
                continue
            }

            let cjk = isCJK(character)
            if current.isEmpty {
                current = [character]
                currentIsCJK = cjk
                currentSpaceBefore = sawWhitespace
                sawWhitespace = false
            } else if cjk == currentIsCJK {
                current.append(character)
            } else {
                flush()
                current = [character]
                currentIsCJK = cjk
                currentSpaceBefore = false
                sawWhitespace = false
            }
        }
        flush()
        return result
    }

    private static func isWhitespace(_ character: Character) -> Bool {
        !character.unicodeScalars.isEmpty
            && character.unicodeScalars.allSatisfy {
                CharacterSet.whitespacesAndNewlines.contains($0)
            }
    }

    private static func isCJK(_ character: Character) -> Bool {
        guard let scalar = character.unicodeScalars.first else { return false }
        let value = scalar.value
        return (0x2E80...0x2EFF).contains(value)
            || (0x2F00...0x2FDF).contains(value)
            || (0x3000...0x303F).contains(value)
            || (0x3040...0x309F).contains(value)
            || (0x30A0...0x30FF).contains(value)
            || (0x3100...0x312F).contains(value)
            || (0x3200...0x33FF).contains(value)
            || (0x3400...0x4DBF).contains(value)
            || (0x4E00...0x9FFF).contains(value)
            || (0xAC00...0xD7A3).contains(value)
            || (0xF900...0xFAFF).contains(value)
            || (0xFF00...0xFFEF).contains(value)
            || (0x20000...0x3FFFF).contains(value)
    }
}

/// Numeric-only evidence for one preview run. No hypothesis text or audio is
/// retained here, so this value is safe for the rolling diagnostic log.
public struct DictationLivePreviewMetrics: Sendable, Equatable {
    public let operationID: UUID
    public let sessionMs: Int
    public let streamOpenMs: Int?
    public let firstAudioMs: Int?
    public let firstPushMs: Int?
    public let firstPartialMs: Int?
    public let pushCount: Int
    public let partialCount: Int
    public let changedPartialCount: Int
    public let backpressuredAppendCount: Int
    public let droppedSampleCount: Int
    public let changedGapP50Ms: Int?
    public let changedGapP95Ms: Int?
    public let lastCaptionCharacterCount: Int
}

/// One press-to-release live preview.
///
/// Audio ingress is synchronous and lock-bounded: it only copies into a
/// three-second buffer and launches at most one wire push. Model work always
/// happens in a `Task`, never on the capture callback. A stop invalidates the
/// run before awaiting in-flight work, so a late response cannot put text back
/// on screen; it then cancels the stream before the accurate final pass uses
/// the same serialized worker.
public final class DictationLivePreviewSession: @unchecked Sendable {
    public static let chunkSeconds = 0.16
    public static let maxPendingSeconds = 3.0

    private let transport: any DictationPreviewTransport
    private let previewSink: @Sendable (String) -> Void
    private let log: @Sendable (String) -> Void
    private let waitUntilReady: @Sendable () async -> Bool
    private let lock = NSLock()

    private var runID: UUID?
    private var operationID = UUID()
    private var startedAt: UInt64 = 0
    private var streamOpenedAt: UInt64?
    private var firstAudioAt: UInt64?
    private var firstPushAt: UInt64?
    private var firstPartialAt: UInt64?
    private var lastChangedAt: UInt64?
    private var changedGapsMs: [Int] = []
    private var streamID: String?
    private var sourceSampleRate: Int?
    private var pending: [Float] = []
    private var pushInFlight = false
    private var openTask: Task<Void, Never>?
    private var pushTask: Task<Void, Never>?
    private var pushCount = 0
    private var partialCount = 0
    private var changedPartialCount = 0
    private var backpressuredAppendCount = 0
    private var droppedSampleCount = 0
    /// Bounded caption only; cleared at stop. The full hypothesis is never a
    /// field on this object.
    private var lastCaption = ""
    private var lastMetricsValue: DictationLivePreviewMetrics?

    public init(
        transport: any DictationPreviewTransport,
        previewSink: @escaping @Sendable (String) -> Void,
        log: @escaping @Sendable (String) -> Void = { _ in },
        readiness: @escaping @Sendable () async -> Bool = { true }
    ) {
        self.transport = transport
        self.previewSink = previewSink
        self.log = log
        self.waitUntilReady = readiness
    }

    public var lastMetrics: DictationLivePreviewMetrics? {
        lock.lock(); defer { lock.unlock() }
        return lastMetricsValue
    }

    /// Starts asynchronously and returns immediately to the hotkey path.
    public func start(languages: [String]) {
        lock.lock()
        guard runID == nil else { lock.unlock(); return }
        let id = UUID()
        runID = id
        operationID = UUID()
        startedAt = Self.now()
        streamOpenedAt = nil
        firstAudioAt = nil
        firstPushAt = nil
        firstPartialAt = nil
        lastChangedAt = nil
        changedGapsMs.removeAll(keepingCapacity: true)
        streamID = nil
        sourceSampleRate = nil
        pending.removeAll(keepingCapacity: true)
        pushInFlight = false
        pushCount = 0
        partialCount = 0
        changedPartialCount = 0
        backpressuredAppendCount = 0
        droppedSampleCount = 0
        lastCaption = ""
        lastMetricsValue = nil
        let task = Task<Void, Never> { [weak self] in
            guard let self else { return }
            await self.openStream(runID: id, languages: languages)
        }
        openTask = task
        lock.unlock()
        previewSink("")
    }

    /// Called from the microphone collector. Never awaits and never performs
    /// PCM conversion or IPC on that thread.
    public func append(samples: [Float], sampleRate: Int) {
        guard !samples.isEmpty, sampleRate > 0 else { return }
        lock.lock()
        guard runID != nil else { lock.unlock(); return }
        if let sourceSampleRate, sourceSampleRate != sampleRate {
            lock.unlock()
            return
        }
        if sourceSampleRate == nil { sourceSampleRate = sampleRate }
        if firstAudioAt == nil { firstAudioAt = Self.now() }
        if pushInFlight { backpressuredAppendCount += 1 }

        pending.append(contentsOf: samples)
        let maximum = max(1, Int(Double(sampleRate) * Self.maxPendingSeconds))
        if pending.count > maximum {
            let overflow = pending.count - maximum
            pending.removeFirst(overflow)
            droppedSampleCount += overflow
        }
        schedulePushLocked()
        lock.unlock()
    }

    /// Invalidates screen updates first, then drains/cancels the shared wire.
    /// Callers await this before submitting the authoritative final decode.
    public func stop() async {
        guard let stopped = detachForStop() else {
            previewSink("")
            return
        }
        previewSink("")
        await stopped.openTask?.value
        await stopped.pushTask?.value
        if let streamID = stopped.streamID {
            await transport.cancelPreview(streamID: streamID)
        }
        let m = stopped.metrics
        log("dictation preview profile: operation=\(m.operationID.uuidString) "
            + "session_ms=\(m.sessionMs) stream_open_ms=\(Self.label(m.streamOpenMs)) "
            + "first_audio_ms=\(Self.label(m.firstAudioMs)) "
            + "first_push_ms=\(Self.label(m.firstPushMs)) "
            + "first_partial_ms=\(Self.label(m.firstPartialMs)) pushes=\(m.pushCount) "
            + "partials=\(m.partialCount) changed=\(m.changedPartialCount) "
            + "changed_gap_p50_ms=\(Self.label(m.changedGapP50Ms)) "
            + "changed_gap_p95_ms=\(Self.label(m.changedGapP95Ms)) "
            + "backpressured_appends=\(m.backpressuredAppendCount) "
            + "dropped_samples=\(m.droppedSampleCount) "
            + "caption_characters=\(m.lastCaptionCharacterCount)")
    }

    private struct StopSnapshot {
        let streamID: String?
        let openTask: Task<Void, Never>?
        let pushTask: Task<Void, Never>?
        let metrics: DictationLivePreviewMetrics
    }

    private func detachForStop() -> StopSnapshot? {
        lock.lock(); defer { lock.unlock() }
        guard runID != nil else { return nil }
        let stoppedAt = Self.now()
        let metrics = DictationLivePreviewMetrics(
            operationID: operationID,
            sessionMs: Self.elapsed(startedAt, stoppedAt),
            streamOpenMs: Self.elapsed(startedAt, streamOpenedAt),
            firstAudioMs: Self.elapsed(startedAt, firstAudioAt),
            firstPushMs: Self.elapsed(startedAt, firstPushAt),
            firstPartialMs: Self.elapsed(startedAt, firstPartialAt),
            pushCount: pushCount,
            partialCount: partialCount,
            changedPartialCount: changedPartialCount,
            backpressuredAppendCount: backpressuredAppendCount,
            droppedSampleCount: droppedSampleCount,
            changedGapP50Ms: Self.percentile(changedGapsMs, fraction: 0.50),
            changedGapP95Ms: Self.percentile(changedGapsMs, fraction: 0.95),
            lastCaptionCharacterCount: lastCaption.count)
        let result = StopSnapshot(
            streamID: streamID,
            openTask: openTask,
            pushTask: pushTask,
            metrics: metrics)
        lastMetricsValue = metrics
        runID = nil
        streamID = nil
        sourceSampleRate = nil
        pending.removeAll(keepingCapacity: true)
        pushInFlight = false
        openTask = nil
        pushTask = nil
        lastCaption = ""
        return result
    }

    private func openStream(runID: UUID, languages: [String]) async {
        // Startup belongs to the host's shared task. Keep buffering while it
        // loads, but never reopen a hold cancelled before or during the wait.
        guard isCurrentRun(runID) else { return }
        guard await waitUntilReady() else {
            _ = completeOpen(runID: runID, streamID: nil)
            return
        }
        guard isCurrentRun(runID) else { return }
        let opened = await transport.startPreview(languages: languages)
        let cancelLate = completeOpen(runID: runID, streamID: opened)
        if cancelLate, let opened {
            await transport.cancelPreview(streamID: opened)
        }
    }

    private func isCurrentRun(_ id: UUID) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return runID == id
    }

    /// Returns true when the stream opened after its hold already stopped.
    private func completeOpen(runID: UUID, streamID opened: String?) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard self.runID == runID else { return opened != nil }
        openTask = nil
        guard let opened, !opened.isEmpty else { return false }
        streamID = opened
        streamOpenedAt = Self.now()
        schedulePushLocked()
        return false
    }

    /// Must be called with `lock` held.
    private func schedulePushLocked() {
        guard let id = runID,
              let streamID,
              let rate = sourceSampleRate,
              !pushInFlight,
              pending.count >= max(1, Int(Double(rate) * Self.chunkSeconds))
        else { return }

        let samples = pending
        pending.removeAll(keepingCapacity: true)
        pushInFlight = true
        pushCount += 1
        if firstPushAt == nil { firstPushAt = Self.now() }
        let task = Task { [weak self] in
            guard let self else { return }
            let hypothesis = await self.transport.pushPreview(
                streamID: streamID, samples: samples, sampleRate: rate)
            self.completePush(runID: id, hypothesis: hypothesis)
        }
        pushTask = task
    }

    private func completePush(runID: UUID, hypothesis: DictationPreviewHypothesis?) {
        var publish: String?
        lock.lock()
        guard self.runID == runID else { lock.unlock(); return }
        pushInFlight = false
        pushTask = nil
        if let hypothesis {
            partialCount += 1
            let tail = DictationPreviewText.liveTail(hypothesis.text)
            let caption = DictationPreviewCaption.window(tail)
            if !caption.isEmpty {
                let now = Self.now()
                if firstPartialAt == nil { firstPartialAt = now }
                if caption != lastCaption {
                    if let lastChangedAt {
                        changedGapsMs.append(Self.elapsed(lastChangedAt, now))
                    }
                    self.lastChangedAt = now
                    changedPartialCount += 1
                    lastCaption = caption
                    publish = caption
                }
            }
        }
        schedulePushLocked()
        lock.unlock()
        if let publish { previewSink(publish) }
    }

    private static func now() -> UInt64 { DispatchTime.now().uptimeNanoseconds }

    private static func elapsed(_ start: UInt64, _ end: UInt64) -> Int {
        guard end >= start else { return 0 }
        return Int((end - start) / 1_000_000)
    }

    private static func elapsed(_ start: UInt64, _ end: UInt64?) -> Int? {
        end.map { elapsed(start, $0) }
    }

    private static func percentile(_ values: [Int], fraction: Double) -> Int? {
        guard !values.isEmpty else { return nil }
        let ordered = values.sorted()
        let raw = Double(ordered.count - 1) * fraction
        let index = min(ordered.count - 1, max(0, Int(raw.rounded())))
        return ordered[index]
    }

    private static func label(_ value: Int?) -> String {
        value.map(String.init) ?? "n/a"
    }
}
