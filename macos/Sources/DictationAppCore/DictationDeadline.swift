import Foundation
import DictationKit

/// Bounds on everything that waits for the ASR worker.
public struct DictationTiming: Sendable {
    /// Spawn plus model load. A cold MLX load of the shipped model takes
    /// seconds; minutes means the worker is wedged.
    public var startSeconds: Double = 120
    /// Fixed part of the release-to-text deadline.
    public var transcribeSeconds: Double = 30
    /// Added to the bound per second of audio. Decoding grows with the
    /// clip, and a fixed 30 s for holds of up to 120 s killed the worker
    /// mid-decode on a long dictation and lost all of it. Windows adds the
    /// audio's duration once (`AsrWorkerTranscriber.TranscribeTimeoutFor`).
    public var transcribeSecondsPerAudioSecond: Double = 1
    /// A polite shutdown before the worker is killed.
    public var shutdownSeconds: Double = 3
    /// Ending a hold's live preview: its cancel can queue behind a decode
    /// in the worker, and must not hold up a release or a cancel.
    public var previewStopSeconds: Double = 2
    /// The longest hold; what was captured is then transcribed.
    public var workerHealthAfterSeconds: Double = 30
    public var workerHealthIntervalSeconds: Double = 10
    public var workerHealthReplySeconds: Double = 5
    public var holdPolicy = DictationHoldPolicy.stage1
    public var maximumHoldSeconds: Double {
        get { holdPolicy.maximumHoldSeconds }
        set { holdPolicy.maximumHoldSeconds = newValue }
    }
    /// A take longer than this asks at the first Escape, in the HUD, and
    /// discards at the second; a shorter one is cancelled at once. 30 s, as
    /// on Windows.
    public var escapeAsksAfterSeconds: Double = 30
    /// How long Escape's question stands, as on Windows.
    public var escapeQuestionSeconds: Double = 5
    /// Idle time after which the loaded model (about 3 GB) is given back,
    /// fixed. `nil`, the default, uses the window `DictationWorkerKeepAlive`
    /// learns from the user's pauses. The next press starts the worker again
    /// while the user speaks.
    public var workerIdleSeconds: Double?
    /// How often idle resources are checked.
    public var housekeepingSeconds: Double = 5
    /// Idle time after which the warm microphone is closed, counted from the
    /// end of the last dictation. The microphone stays open only where a
    /// cold start costs words. Over 545 real dictations on one Mac
    /// (2026-09-29 to 10-25) a built-in microphone delivered audio 85 ms
    /// after a cold start (median; 162 ms at p90), quicker than anyone
    /// starts speaking after a press, so it closes almost at once.
    public var microphoneIdleSeconds: Double = 3
    /// The same for a Bluetooth microphone, whose first audible sound came
    /// 269 ms after a cold start (median; 862 ms at p90): enough to lose a
    /// first word. It stays open across the short pauses of a working
    /// session (34% of next presses came within 10 s), but no longer: while
    /// it is open, AirPods and other headsets play in their low-quality
    /// call mode.
    public var bluetoothMicrophoneIdleSeconds: Double = 10
    /// How often an idle warm microphone is checked once a dictation ends.
    public var microphoneCheckSeconds: Double = 0.5

    public init() {}

    /// The release-to-text bound for a clip `audioSeconds` long.
    public func transcribeDeadline(audioSeconds: Double) -> Double {
        transcribeSeconds + max(0, audioSeconds) * transcribeSecondsPerAudioSecond
    }
}

/// Waits for async work for at most a fixed time, even when the work cannot
/// be cancelled.
///
/// A task group cannot do this: it waits for every child before returning,
/// and a request blocked in the worker's pipe read ignores cancellation (it is
/// a synchronous read inside the IPC actor). So the work runs in an
/// unstructured task and the caller waits on a continuation that either the
/// work or a timer resumes, whichever comes first. What happens to work that
/// is still running at the deadline is the caller's business: `onAbandon` is
/// where it kills the worker so the blocked read reaches EOF.
public enum DictationDeadline {

    /// - Parameters:
    ///   - seconds: the bound.
    ///   - work: runs in its own task; cancelled if the deadline wins.
    ///   - canAbandon: asked once when the bound passes first. Return `false`
    ///     to keep waiting instead (the work has reached a stage that must be
    ///     allowed to finish on its own).
    ///   - onAbandon: runs after the deadline has won, so whatever it does
    ///     to the work (a kill makes the work return at once) cannot turn
    ///     the result back into the work's own.
    /// - Returns: the work's value, or `nil` when the deadline won.
    public static func run<T: Sendable>(
        seconds: Double,
        work: @escaping @Sendable () async -> T,
        canAbandon: @escaping @Sendable () -> Bool = { true },
        onAbandon: @escaping @Sendable () -> Void = {}
    ) async -> T? {
        await withCheckedContinuation { (continuation: CheckedContinuation<T?, Never>) in
            let race = Race(continuation)
            race.workTask = Task {
                let value = await work()
                if race.finish(value) { race.timerTask?.cancel() }
            }
            race.timerTask = Task {
                try? await Task.sleep(for: .seconds(seconds))
                guard !Task.isCancelled, !race.isFinished, canAbandon() else { return }
                guard race.finish(nil) else { return }
                race.workTask?.cancel()
                onAbandon()
            }
        }
    }

    /// Resumes the continuation exactly once, whichever side gets there first.
    private final class Race<T: Sendable>: @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: CheckedContinuation<T?, Never>?
        private var _workTask: Task<Void, Never>?
        private var _timerTask: Task<Void, Never>?

        init(_ continuation: CheckedContinuation<T?, Never>) { self.continuation = continuation }

        var workTask: Task<Void, Never>? {
            get { lock.lock(); defer { lock.unlock() }; return _workTask }
            set { lock.lock(); _workTask = newValue; lock.unlock() }
        }

        var timerTask: Task<Void, Never>? {
            get { lock.lock(); defer { lock.unlock() }; return _timerTask }
            set { lock.lock(); _timerTask = newValue; lock.unlock() }
        }

        var isFinished: Bool { lock.lock(); defer { lock.unlock() }; return continuation == nil }

        /// Returns whether this call was the one that resumed.
        func finish(_ value: T?) -> Bool {
            lock.lock()
            let pending = continuation
            continuation = nil
            lock.unlock()
            pending?.resume(returning: value)
            return pending != nil
        }
    }
}

/// Decides, once, whether a transcription that hit its deadline is abandoned
/// or allowed to finish.
///
/// The deadline exists for a worker that stops answering. Once the text is
/// back and the paste has begun, the paste is bounded by its own timeout, and
/// abandoning it would report "nothing inserted" over text that lands a
/// moment later. So whichever of "the paste begins" and "the deadline passes"
/// happens first wins, under one lock, and the loser never acts.
final class InsertionGate: @unchecked Sendable {
    private enum State { case open, inserting, abandoned }
    private let lock = NSLock()
    private var state = State.open

    var isAbandoned: Bool {
        lock.lock(); defer { lock.unlock() }; return state == .abandoned
    }

    /// The paste may start. `false` once the attempt was abandoned.
    func beginInsertion() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard state == .open else { return false }
        state = .inserting
        return true
    }

    /// The deadline passed, or the user cancelled. `false` when the paste
    /// had already begun. Asking again after either says `true`: a
    /// transcription the user cancelled can still hit its deadline, and the
    /// deadline must then be free to kill a wedged worker.
    func abandon() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard state != .inserting else { return false }
        state = .abandoned
        return true
    }
}

/// The wait between a release and its text, which Escape or the Cancel
/// button can end early.
///
/// The work itself cannot be stopped: a transcription blocked in the worker's
/// pipe ignores cancellation (see `DictationDeadline`), and killing the worker
/// would cost the next dictation a model load. So a cancel ends the WAITING:
/// the caller carries on at once, and the hold's `InsertionGate` refuses the
/// paste whenever the abandoned work gets that far. Once the paste has begun
/// the cancel is refused, as the deadline is, because that text is already
/// landing.
final class ReleaseCancel: @unchecked Sendable {
    let gate: InsertionGate
    private let lock = NSLock()
    private var cancelled = false
    private var wake: (@Sendable () -> Void)?

    init(gate: InsertionGate) {
        self.gate = gate
    }

    /// Cancels unless the paste has begun. `true` only for the call that
    /// cancelled.
    @discardableResult
    func cancel() -> Bool {
        guard gate.abandon() else { return false }
        lock.lock()
        let first = !cancelled
        cancelled = true
        let waiting = wake
        wake = nil
        lock.unlock()
        waiting?()
        return first
    }

    /// `work`'s value, or `nil` as soon as the release is cancelled. Work
    /// still running then carries on unobserved.
    func unlessCancelled<T: Sendable>(_ work: @escaping @Sendable () async -> T) async -> T? {
        await withCheckedContinuation { (continuation: CheckedContinuation<T?, Never>) in
            let once = ResumeOnce(continuation)
            guard arm({ once.resume(nil) }) else {
                once.resume(nil)
                return
            }
            Task { once.resume(await work()) }
        }
    }

    /// Records what a cancel wakes; `false` when the cancel came first.
    private func arm(_ wakeUp: @escaping @Sendable () -> Void) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard !cancelled else { return false }
        wake = wakeUp
        return true
    }
}

/// Resumes a continuation exactly once, whichever caller gets there first.
private final class ResumeOnce<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<T?, Never>?

    init(_ continuation: CheckedContinuation<T?, Never>) { self.continuation = continuation }

    func resume(_ value: T?) {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume(returning: value)
    }
}

// ─── Per-hold state guarded by the insertion deadline ───

extension DictationModel {
    /// One hold, from key-down to its outcome.
    ///
    /// Capture starts asynchronously, so a release or a cancel can arrive
    /// before it has; both are recorded here and honoured the moment capture
    /// is up, rather than dropped.
    ///
    /// Each hold gets its own `DictationSession` (a cheap object: the
    /// capture and the worker are shared). A transcription abandoned at its
    /// deadline finishes later on its own session, so it cannot reset the
    /// phase of the hold that follows it.
    final class HoldAttempt {
        let session: DictationSession
        var capture: DictationRecoverableCapture?
        let gate: InsertionGate
        let transcript: TranscriptHolder
        let diagnostics: DictationAttemptDiagnostics
        var captureStarted = false
        var releaseRequested = false
        var cancelReason: CancelReason?
        /// When the hold began, for the double-tap window check at release.
        var startedAt: TimeInterval = 0
        /// The mouse button started this hold, not the hold key: only the
        /// button's release ends it, and the shortcut rule never applies.
        var byMouse = false
        /// A quick release waiting out the double-tap window: a second press
        /// within it turns this hold into a latch instead of a transcription.
        var tapParked = false
        /// The latch's end arrived: finished as soon as capture is up (at
        /// once when it already is), the way `releaseRequested` is.
        var latchEndRequested = false
        /// The ceiling finished this hold, not the user: the status and the
        /// HUD say so.
        var reachedMaximumHold = false
        /// The hint for an input that has sent nothing, worded once per
        /// silent run: naming the input reads Core Audio.
        var silentInputNotice: String?
        /// The app the words were pasted into, once the release asked; the
        /// history names it.
        var target: PasteTarget?

        init(session: DictationSession, gate: InsertionGate, transcript: TranscriptHolder,
             diagnostics: DictationAttemptDiagnostics) {
            self.session = session
            self.gate = gate
            self.transcript = transcript
            self.diagnostics = diagnostics
        }
    }

    /// The text a hold's paste was given, for the clipboard fallback. Held
    /// in memory for that one hold only.
    @MainActor
    final class TranscriptHolder {
        var text: String?
    }

    /// The bundle id of the app a hold's text was about to be pasted into,
    /// as the formatter was told it (off the main actor). Held for that one
    /// hold, for the correction watch's same-app check.
    final class PasteTarget: @unchecked Sendable {
        private let lock = NSLock()
        private var app: String?

        func record(_ app: String?) {
            lock.lock(); defer { lock.unlock() }
            self.app = app
        }

        var value: String? {
            lock.lock(); defer { lock.unlock() }
            return app
        }
    }
}
