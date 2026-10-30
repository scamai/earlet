// Test doubles for every seam `DictationModel` has. None of them opens a
// microphone, spawns a process, installs an event tap or posts a key event.

import Combine
import CoreGraphics
import DictationHistory
import DictationLearning
import Foundation
@testable import DictationKit
@testable import DictationAppCore

/// A value behind a lock, for fakes touched from the main actor and from the
/// detached tasks the model starts.
final class Locked<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value
    init(_ value: Value) { self.value = value }
    var get: Value { lock.lock(); defer { lock.unlock() }; return value }
    func mutate<T>(_ body: (inout Value) -> T) -> T {
        lock.lock(); defer { lock.unlock() }
        return body(&value)
    }
}

// ─── Capture ───

final class FakeCapture: DictationCaptureDevice, @unchecked Sendable {
    struct State {
        var warm = false
        var retaining = false
        var starts = 0
        var warmUps = 0
        var arms = 0
        var stops = 0
        var coolDowns = 0
        var startedOnMainThread: [Bool] = []
        var idleLongEnough = false
        /// How long the warm engine has been idle, against the grace the
        /// model asks `shouldCoolDown` about.
        var idleFor: Double = 0
        /// The bound input is a Bluetooth microphone.
        var bluetooth = false
        /// Every grace the model asked `shouldCoolDown` about.
        var gracesAsked: [Double] = []
        /// Settable answers for the trigger-time checks.
        var stale = false
        var warmUpError: Error?
        var interruption: DictationCaptureInterruption?
        /// The bound input's name, as `boundInputName` reports it.
        var inputName = "Test Microphone"
        /// The forensic record the next `stop` carries, as the real capture
        /// measures one: the hold's length and the input's name.
        var forensics: DictationCaptureForensics?
        /// While set, a cold warm-up blocks until `finishWarmUp()`: the
        /// microphone comes up at a moment the test chooses.
        var holdWarmUp = false
        /// A warm-up is blocked in `holdWarmUp` right now.
        var warmUpWaiting = false
    }
    let state = Locked(State())
    /// Audible by default: a steady 0.2 peak passes the 0.004 silence gate.
    let samples = Locked([Float](repeating: 0.2, count: 16_000))

    var isCurrentlyWarm: Bool { state.get.warm }
    var isInputStaleWhileWarm: Bool { let s = state.get; return s.warm && s.stale }
    var lastCaptureInterruption: DictationCaptureInterruption? { state.get.interruption }
    var boundInputName: String { state.get.inputName }

    private let levelHandler = Locked<(@Sendable (Float) -> Void)?>(nil)
    var onAudioLevel: (@Sendable (Float) -> Void)? {
        get { levelHandler.get }
        set { levelHandler.mutate { $0 = newValue } }
    }

    /// One chunk's peak arrives, as from the live collector.
    func emitLevel(_ level: Float) { levelHandler.get?(level) }

    private let retainedHandler = Locked<(@Sendable ([Float], Int) -> Void)?>(nil)
    var onRetainedAudio: (@Sendable ([Float], Int) -> Void)? {
        get { retainedHandler.get }
        set { retainedHandler.mutate { $0 = newValue } }
    }

    /// A retained chunk arrives, as the real collector would route it.
    func emitRetainedAudio(_ samples: [Float], sampleRate: Int = 16_000) {
        retainedHandler.get?(samples, sampleRate)
    }

    private let discardHandler = Locked<(@Sendable () -> Void)?>(nil)
    var onInterruptionDiscardedAudio: (@Sendable () -> Void)? {
        get { discardHandler.get }
        set { discardHandler.mutate { $0 = newValue } }
    }

    private let reboundHandler = Locked<(@Sendable () -> Void)?>(nil)
    var onInterruptionRebound: (@Sendable () -> Void)? {
        get { reboundHandler.get }
        set { reboundHandler.mutate { $0 = newValue } }
    }

    /// D15 callbacks, fired the way the real watchdog would.
    func emitInterruptionDiscardedAudio() { discardHandler.get?() }
    func emitInterruptionRebound() { reboundHandler.get?() }

    /// How long a cold start blocks its caller, like `AVAudioEngine.start()`.
    let startDelay = Locked(0.0)

    func start() throws {
        let onMain = Thread.isMainThread
        let delay = startDelay.get
        if delay > 0 { usleep(useconds_t(delay * 1_000_000)) }
        state.mutate {
            $0.startedOnMainThread.append(onMain)
            $0.starts += 1; $0.warm = true; $0.retaining = true
        }
    }

    /// The press-time precursor: starts the engine ahead of the arm. A
    /// thrown `warmUpError` is the contended/dead-device case.
    func warmUp() throws {
        let onMain = Thread.isMainThread
        let delay = startDelay.get
        if delay > 0 { usleep(useconds_t(delay * 1_000_000)) }
        if state.get.holdWarmUp {
            state.mutate { $0.warmUpWaiting = true }
            warmUpGate.wait()
            state.mutate { $0.warmUpWaiting = false }
        }
        if let error = state.get.warmUpError { throw error }
        state.mutate {
            $0.startedOnMainThread.append(onMain)
            $0.warmUps += 1; $0.warm = true
        }
    }

    private let warmUpGate = DispatchSemaphore(value: 0)

    /// The blocked warm-up goes on, and later ones no longer block.
    func finishWarmUp() {
        state.mutate { $0.holdWarmUp = false }
        warmUpGate.signal()
    }

    func armTriggerDown() {
        state.mutate { $0.arms += 1; $0.retaining = true }
    }

    func stop(keepWarm: Bool) async -> DictationCapture.CaptureResult {
        state.mutate {
            $0.stops += 1; $0.retaining = false
            if !keepWarm { $0.warm = false }
        }
        // An interrupted press discards its audio, exactly like the real
        // D15 path: the partial hold is never transcribed.
        let interrupted = state.get.interruption != nil
        return DictationCapture.CaptureResult(
            samples: interrupted ? [] : samples.get, sampleRate: 16_000, forensics: state.get.forensics)
    }

    func coolDown() async {
        state.mutate { $0.coolDowns += 1; $0.warm = false; $0.retaining = false }
    }

    func shouldCoolDown(idleSeconds: Double) -> Bool {
        let s = state.mutate { $0.gracesAsked.append(idleSeconds); return $0 }
        return s.warm && !s.retaining && (s.idleLongEnough || s.idleFor >= idleSeconds)
    }

    var usesBluetoothInput: Bool { state.get.bluetooth }
}

// ─── Worker ───

final class FakeWorker: DictationWorker, @unchecked Sendable {
    enum StartReply { case succeed, fail, hang }
    /// `delayed` answers after `seconds`, as a slow decode of a long clip;
    /// `dropped` answers no words, with the worker's reason for them.
    enum Reply {
        case text(String), failure(DictationAsrFailure), hang, delayed(String, seconds: Double)
        case dropped(DictationAsrDropReason)
    }

    struct State {
        var startReply = StartReply.succeed
        var replies: [Reply] = []
        /// `shutdown()` blocks like a request queued behind a wedged one,
        /// until the worker is killed.
        var shutdownHangs = false
        var parkedShutdowns: [CheckedContinuation<Void, Never>] = []
        var initialized = false
        var alive = false
        var starts = 0
        var transcribes = 0
        /// The sample count of each transcription request, in order.
        var transcribeSampleCounts: [Int] = []
        var shutdowns = 0
        var kills = 0
        /// Requests parked in a "hang", released by a kill — exactly what a
        /// killed worker does to a blocked pipe read.
        var parkedStarts: [CheckedContinuation<Bool, Never>] = []
        var parkedTranscribes: [CheckedContinuation<DictationAsrOutcome, Never>] = []
        /// The language code the worker was last given (`nil` detects).
        var language: String?
        /// The language each transcription was sent with, in order.
        var transcribeLanguages: [String?] = []
        /// Whether each transcription was a Recover Last Dictation decode
        /// (`transcribeForRecovery`), in order.
        var recoveries: [Bool] = []
        /// Live-preview traffic, plus an ordered event log shared with
        /// transcription, so a test can assert "preview cancelled before the
        /// final decode".
        var previewStarts = 0
        var previewPushes = 0
        var previewCancels = 0
        var previewReply = "hello world from preview"
        /// `cancelPreview` blocks like a cancel queued behind a decode the
        /// worker never finishes, until the worker is killed.
        var previewCancelHangs = false
        var parkedPreviewCancels: [CheckedContinuation<Void, Never>] = []
        var events: [String] = []
        /// What the worker reports about its decode, attached to every
        /// detailed transcription (`nil`: a worker that reports nothing).
        var decodeStats: DictationDecodeStats?
    }
    let state = Locked(State())
    let name: String

    init(name: String = "worker") { self.name = name }

    var isReady: Bool { let s = state.get; return s.initialized && s.alive }

    func start() async -> Bool {
        if isReady { return true }
        let reply = state.mutate { s -> StartReply in s.starts += 1; return s.startReply }
        switch reply {
        case .succeed:
            state.mutate { $0.initialized = true; $0.alive = true }
            return true
        case .fail:
            return false
        case .hang:
            state.mutate { $0.alive = true }
            return await withCheckedContinuation { c in state.mutate { $0.parkedStarts.append(c) } }
        }
    }

    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        await answer(samples, recovery: false).outcome
    }

    func transcribeForRecovery(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        await answer(samples, recovery: true).outcome
    }

    func transcribeDetailed(samples: [Float], sampleRate: Int,
                            compareBaseline: Bool) async -> DictationAsrDetailedOutcome {
        let answered = await answer(samples, recovery: false)
        switch answered.outcome {
        case .text(let text):
            return .result(DictationAsrDetail(text: text, avgLogprob: nil, decodeStats: state.get.decodeStats,
                                              dropReason: answered.dropped))
        case .failed(let failure):
            return .failed(failure)
        }
    }

    /// The next scripted reply, and the drop reason a `dropped` one carries.
    private func answer(_ samples: [Float], recovery: Bool)
    async -> (outcome: DictationAsrOutcome, dropped: DictationAsrDropReason?) {
        guard isReady else { return (.failed(.notInitialized), nil) }
        let reply = state.mutate { s -> Reply in
            s.transcribes += 1
            s.transcribeSampleCounts.append(samples.count)
            s.transcribeLanguages.append(s.language)
            s.recoveries.append(recovery)
            s.events.append("transcribe")
            // Lower case on purpose: the app formats it, so every insertion
            // test also shows the formatter is on ("Hello world").
            return s.replies.isEmpty ? .text("hello world") : s.replies.removeFirst()
        }
        switch reply {
        case .text(let text): return (.text(text), nil)
        case .dropped(let reason): return (.text(""), reason)
        case .failure(let failure):
            if case .ioError = failure { state.mutate { $0.initialized = false; $0.alive = false } }
            return (.failed(failure), nil)
        case .hang:
            let parked: DictationAsrOutcome = await withCheckedContinuation { c in
                state.mutate { $0.parkedTranscribes.append(c) }
            }
            return (parked, nil)
        case .delayed(let text, let seconds):
            try? await Task.sleep(for: .seconds(seconds))
            return (.text(text), nil)
        }
    }

    func shutdown() async {
        let hangs = state.mutate { s -> Bool in s.shutdowns += 1; return s.shutdownHangs }
        if hangs {
            await withCheckedContinuation { c in state.mutate { $0.parkedShutdowns.append(c) } }
        }
        state.mutate { $0.initialized = false; $0.alive = false }
    }

    func setLanguageCode(_ code: String?) {
        state.mutate { $0.language = code }
    }

    /// Transcriptions parked in a "hang" answer after all, with `text`.
    func finishTranscribe(_ text: String) {
        let parked = state.mutate { s -> [CheckedContinuation<DictationAsrOutcome, Never>] in
            defer { s.parkedTranscribes = [] }
            return s.parkedTranscribes
        }
        parked.forEach { $0.resume(returning: .text(text)) }
    }

    /// A start that was hanging completes on its own.
    func finishStart(_ succeeded: Bool) {
        let parked = state.mutate { s -> [CheckedContinuation<Bool, Never>] in
            if succeeded { s.initialized = true; s.alive = true }
            defer { s.parkedStarts = [] }
            return s.parkedStarts
        }
        parked.forEach { $0.resume(returning: succeeded) }
    }

    func forceTerminate() {
        let parked = state.mutate { s -> ([CheckedContinuation<Bool, Never>],
                                          [CheckedContinuation<DictationAsrOutcome, Never>],
                                          [CheckedContinuation<Void, Never>]) in
            s.kills += 1; s.initialized = false; s.alive = false
            defer {
                s.parkedStarts = []; s.parkedTranscribes = []; s.parkedShutdowns = []
                s.parkedPreviewCancels = []
            }
            return (s.parkedStarts, s.parkedTranscribes, s.parkedShutdowns + s.parkedPreviewCancels)
        }
        parked.0.forEach { $0.resume(returning: false) }
        parked.1.forEach { $0.resume(returning: .failed(.ioError("eof"))) }
        parked.2.forEach { $0.resume() }
    }

    /// The worker process dies on its own.
    func crash() { state.mutate { $0.alive = false } }
}

/// The preview surface, answering from the same scripted state.
extension FakeWorker: DictationPreviewTransport {
    func startPreview(languages: [String]) async -> String? {
        guard isReady else { return nil }
        state.mutate { $0.previewStarts += 1 }
        return "preview-stream"
    }

    func pushPreview(
        streamID: String, samples: [Float], sampleRate: Int
    ) async -> DictationPreviewHypothesis? {
        guard isReady else { return nil }
        let reply = state.mutate { s -> String in s.previewPushes += 1; return s.previewReply }
        return DictationPreviewHypothesis(text: reply, isEndpoint: false)
    }

    func cancelPreview(streamID: String) async {
        let hangs = state.mutate { s -> Bool in
            s.previewCancels += 1; s.events.append("preview-cancel")
            return s.previewCancelHangs
        }
        if hangs {
            await withCheckedContinuation { c in state.mutate { $0.parkedPreviewCancels.append(c) } }
        }
    }
}

extension FakeWorker.Reply {
    /// The decode failed in the engine (the worker is alive and said no).
    static var engineFailed: FakeWorker.Reply { .failure(.engineError("synthetic")) }
    /// The worker died mid-decode.
    static var workerDied: FakeWorker.Reply { .failure(.ioError("eof")) }
}

extension TextInserter.InsertOutcome {
    /// A paste the target refused, as with nothing focused.
    static var refused: TextInserter.InsertOutcome { .failed(.noFocusedElement) }
}

/// Hands out a fresh `FakeWorker` per request and remembers them all.
@MainActor
final class WorkerFactory {
    private(set) var made: [FakeWorker] = []
    var configure: (FakeWorker) -> Void = { _ in }
    func make(_ resources: DictationResources) -> any DictationWorker {
        let worker = FakeWorker(name: "worker\(made.count + 1)")
        configure(worker)
        made.append(worker)
        return worker
    }
}

// ─── Trigger ───

@MainActor
final class FakeTrigger: DictationHoldTrigger {
    var onHoldStart: (() -> Void)?
    var onHoldEnd: (() -> Void)?
    var onModifierFlags: ((CGEventFlags) -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    var onLatchStart: (() -> Void)?
    var onLatchEnd: (() -> Void)?
    var startResult = true
    /// What a refused start reports, like the real tap's onDegraded.
    var degradeStatus: DictationPermissionStatus?
    private(set) var running = false
    private(set) var stops = 0
    /// The key the trigger was built with, then every `update(_:)`.
    private(set) var keys: [DictationHoldKey] = []

    func start() -> Bool {
        running = startResult
        if !startResult, let degradeStatus { onDegraded?(degradeStatus) }
        return startResult
    }
    func stop() { running = false; stops += 1 }
    func update(_ key: DictationHoldKey) { keys.append(key) }

    /// The hold key goes down, with `flags` as the tap would report them.
    func press(flags: CGEventFlags = .maskCommand) {
        onModifierFlags?(flags)
        onHoldStart?()
    }

    /// Another modifier goes down (or up) while the hold key is held.
    func modifiers(_ flags: CGEventFlags) { onModifierFlags?(flags) }

    func release() {
        onModifierFlags?([])
        onHoldEnd?()
    }

    /// The tap reports a missing grant, as at a refused start.
    func degrade(_ status: DictationPermissionStatus) { onDegraded?(status) }

    /// The key tapped twice quickly, as the engine reports the pair: each
    /// press and release fires the ordinary hold edges, and the second
    /// press adds the latch edge after them.
    func doubleTap() {
        press()
        release()
        press()
        onLatchStart?()
        release()
    }

    /// While latched, the pair that ends the latch: the pair's hold edges
    /// are ignored and the latch edge finishes the dictation.
    func doubleTapAgain() {
        press()
        release()
        press()
        onLatchEnd?()
        release()
    }
}

/// The mouse button's listener: `press()`/`release()` are the bound button's
/// edges as the live tap reports them, after its swallow.
@MainActor
final class FakeMouseTrigger: DictationMouseTrigger {
    var onPress: (() -> Void)?
    var onRelease: (() -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    var startResult = true
    private(set) var running = false
    private(set) var starts = 0
    private(set) var stops = 0
    /// The button the listener was built with, then every `update(_:)`.
    private(set) var buttons: [DictationMouseSideButton] = []

    func start() -> Bool {
        starts += 1
        running = startResult
        return startResult
    }
    func stop() { running = false; stops += 1 }
    func update(_ button: DictationMouseSideButton) { buttons.append(button) }

    func press() { onPress?() }
    func release() { onRelease?() }
}

/// The production trigger's fan-out — `HotkeyHoldTrigger` over a real
/// `DictationHotkey`, so the real hold router and the real
/// `DoubleTapModifierState` (with its own latched flag and its own clock)
/// decide every edge — behind a start that installs no tap. Driven with
/// Right Command's modifier flags, as the tap would be.
@MainActor
final class DetectorTrigger: DictationHoldTrigger {
    let live = HotkeyHoldTrigger(key: .rightCommand)

    var onHoldStart: (() -> Void)? {
        get { live.onHoldStart } set { live.onHoldStart = newValue }
    }
    var onHoldEnd: (() -> Void)? {
        get { live.onHoldEnd } set { live.onHoldEnd = newValue }
    }
    var onModifierFlags: ((CGEventFlags) -> Void)? {
        get { live.onModifierFlags } set { live.onModifierFlags = newValue }
    }
    var onDegraded: ((DictationPermissionStatus) -> Void)? {
        get { live.onDegraded } set { live.onDegraded = newValue }
    }
    var onLatchStart: (() -> Void)? {
        get { live.onLatchStart } set { live.onLatchStart = newValue }
    }
    var onLatchEnd: (() -> Void)? {
        get { live.onLatchEnd } set { live.onLatchEnd = newValue }
    }

    func start() -> Bool { true }
    func stop() {}
    func update(_ key: DictationHoldKey) { live.update(key) }

    /// `kVK_RightCommand`.
    static let rightCommand: Int64 = 54

    func down() { live.hotkey.process(flags: .maskCommand, keyCode: Self.rightCommand) }
    func up() { live.hotkey.process(flags: [], keyCode: Self.rightCommand) }
    func tap() { down(); up() }
    func doubleTap() { tap(); tap() }

    /// Another modifier changes with the hold key up, as `flagsChanged`
    /// reports it: Left Shift (56) down, then up.
    func shiftTap() {
        live.hotkey.process(flags: .maskShift, keyCode: 56)
        live.hotkey.process(flags: [], keyCode: 56)
    }
}

@MainActor
final class FakeKeyWatch: DictationKeyWatch {
    private var handler: (@MainActor (UInt16) -> Void)?
    private(set) var begins = 0
    var active: Bool { handler != nil }

    func begin(_ onKeyDown: @escaping @MainActor (UInt16) -> Void) {
        begins += 1
        handler = onKeyDown
    }

    func end() { handler = nil }

    /// A key goes down anywhere; only seen while a watch is active.
    func type(_ keyCode: UInt16) { handler?(keyCode) }
}

/// The latch's commit-key trap. `commit()` is a Return/Enter down the live
/// tap would have swallowed; the fake only records begin/end and forwards.
@MainActor
final class FakeStopTrap: DictationLatchStopTrap {
    private var handler: (@MainActor () -> Void)?
    private(set) var begins = 0
    private(set) var ends = 0
    var active: Bool { handler != nil }
    /// What `begin` reports, as the live tap's creation result.
    var installed = true

    @discardableResult
    func begin(_ onStopKey: @escaping @MainActor () -> Void) -> Bool {
        begins += 1
        handler = onStopKey
        return installed
    }

    func end() {
        if handler != nil { ends += 1 }
        handler = nil
    }

    /// A stop key went down and was swallowed; only seen while installed.
    func stopKey() { handler?() }
}

// ─── Launch at login ───

struct LoginItemRefused: Error {}

/// `SMAppService.mainApp` as the model sees it. Starts unregistered, the way
/// a fresh install is.
@MainActor
final class FakeLoginItem {
    var status = DictationLoginItemStatus.off
    /// What a successful register leaves: `.on`, or `.needsApproval` when
    /// macOS waits for the user.
    var statusAfterRegister = DictationLoginItemStatus.on
    var registerFails = false
    var unregisterFails = false
    /// "register", "unregister" and "settings", in order.
    private(set) var calls: [String] = []

    var seam: DictationLoginItem {
        DictationLoginItem(
            status: { self.status },
            register: {
                self.calls.append("register")
                if self.registerFails { throw LoginItemRefused() }
                self.status = self.statusAfterRegister
            },
            unregister: {
                self.calls.append("unregister")
                if self.unregisterFails { throw LoginItemRefused() }
                self.status = .off
            },
            openSettings: { self.calls.append("settings") })
    }
}

// ─── Insertion ───

final class InsertRecorder: @unchecked Sendable {
    let texts = Locked([String]())
    let outcome = Locked(TextInserter.InsertOutcome.success(clipboardByteIdentical: true))
    /// How long a paste takes, measured from when it begins.
    let duration = Locked(0.0)

    var insert: DictationSession.TextInsertion {
        { [self] text in
            texts.mutate { $0.append(text) }
            let seconds = duration.get
            if seconds > 0 { try? await Task.sleep(for: .seconds(seconds)) }
            return (outcome.get, TextInserter.InsertTiming())
        }
    }
}

// ─── Correction watch ───

/// `DictationCorrectionWatch` without Accessibility: records what the model
/// asked for, and ends a watch when the test says so. `finish` delivers to
/// the latest watch even after an abandon, the way a real result can still
/// be on its way when the model gives up on it.
final class FakeCorrectionWatch: DictationCorrectionWatch, @unchecked Sendable {
    struct Begin: Equatable {
        var pasted: String
        var targetApp: String
    }
    let begins = Locked([Begin]())
    let abandons = Locked(0)
    private let revision = Locked<(@Sendable (String) -> Void)?>(nil)
    private let pending = Locked<(@Sendable (DictationCorrectionWatchEnd) -> Void)?>(nil)
    let preparations = Locked([String]())
    let prepareHook = Locked<(@Sendable () async -> DictationCorrectionObserver.PasteSnapshot?)?>(nil)
    let snapshots = Locked([DictationCorrectionObserver.PasteSnapshot?]())

    func prepare(targetApp: String) async -> DictationCorrectionObserver.PasteSnapshot? {
        preparations.mutate { $0.append(targetApp) }
        return await prepareHook.get?()
    }

    func begin(pasted: String, targetApp: String, beforePaste: DictationCorrectionObserver.PasteSnapshot?,
               onRevision: (@Sendable (String) -> Void)?,
               onEnd: @escaping @Sendable (DictationCorrectionWatchEnd) -> Void) {
        begins.mutate { $0.append(Begin(pasted: pasted, targetApp: targetApp)) }
        snapshots.mutate { $0.append(beforePaste) }
        pending.mutate { $0 = onEnd }
        revision.mutate { $0 = onRevision }
    }

    func abandon() { abandons.mutate { $0 += 1 } }

    func settle(_ span: String) { revision.get?(span) }

    /// The latest watch ends, reporting `span` as the pasted span's final text.
    /// The flags say what a continuous observer's end would: the span was
    /// still located and read at the end, and whether it ever changed.
    func finish(span: String?, reason: String = "edit_settled",
                spanObservedToEnd: Bool = false, sawChange: Bool = false) {
        let onEnd = pending.mutate { p -> (@Sendable (DictationCorrectionWatchEnd) -> Void)? in
            defer { p = nil }
            return p
        }
        onEnd?(DictationCorrectionWatchEnd(reason: reason, editedSpan: span,
                                           spanObservedToEnd: spanObservedToEnd, sawChange: sawChange))
    }
}

/// A directory removed when the test that made it ends.
final class ScratchDirectory: @unchecked Sendable {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("dictation-appcore-\(UUID().uuidString)", isDirectory: true)
    deinit { try? FileManager.default.removeItem(at: url) }
}

/// The ordinary words the learning tests' sentences use. "trovio" and
/// "Trovioo" are deliberately missing, so both are distinctive.
let testLexicon = WordListLexicon(words: [
    "we", "use", "the", "hello", "world", "a", "for", "and", "is", "it", "to", "our", "meeting",
    "with", "today", "call", "at", "noon",
])

/// A word list whose `isAvailable` can be made to block, which holds the
/// learning actor busy (every snapshot asks it), the way a slow disk would.
final class GatedLexicon: LearningLexicon, @unchecked Sendable {
    private let gate = DispatchSemaphore(value: 0)
    let blocking = Locked(false)
    /// Set once a call is blocked.
    let entered = Locked(false)

    var isAvailable: Bool {
        if blocking.get {
            entered.mutate { $0 = true }
            gate.wait()
        }
        return true
    }

    func isOrdinary(_ token: String) -> Bool { testLexicon.isOrdinary(token) }

    /// Lets a blocked call go on, and stops blocking.
    func release() {
        blocking.mutate { $0 = false }
        gate.signal()
    }
}

// ─── Environment ───

/// A private defaults domain per harness, removed when the test ends.
final class ScratchDefaults: @unchecked Sendable {
    let name = "ai.scam.dictation.tests.\(UUID().uuidString)"
    let defaults: UserDefaults
    init() { defaults = UserDefaults(suiteName: name)! }
    deinit { defaults.removePersistentDomain(forName: name) }
}

@MainActor
struct Harness {
    let scratch = ScratchDefaults()
    let capture = FakeCapture()
    let workers = WorkerFactory()
    let trigger = FakeTrigger()
    /// The real detector, used instead of `trigger` when `usesDetector`.
    let detector = DetectorTrigger()
    var usesDetector = false
    let mouse = FakeMouseTrigger()
    let keyWatch = FakeKeyWatch()
    let stopTrap = FakeStopTrap()
    /// What macOS answers when a recorded combination is probed.
    var reservesShortcuts = true
    let inserts = InsertRecorder()
    var resources: DictationResources? = DictationResources(python: "/p", runtime: "/r", model: "/m")
    /// The three grants. Behind a lock so a test can flip one after the
    /// model is built: the poll then sees the new answer, as after a TCC
    /// change in System Settings.
    struct Grants {
        var microphone = true
        var accessibility = true
        var inputMonitoring = true
    }
    let grants = Locked(Grants())
    var microphoneGranted: Bool {
        get { grants.get.microphone }
        set { grants.mutate { $0.microphone = newValue } }
    }
    var accessibilityGranted: Bool {
        get { grants.get.accessibility }
        set { grants.mutate { $0.accessibility = newValue } }
    }
    var inputMonitoringGranted: Bool {
        get { grants.get.inputMonitoring }
        set { grants.mutate { $0.inputMonitoring = newValue } }
    }
    /// Every permission call, in order: "mic?", "mic", "mic-pane", "ax?",
    /// "ax!", "ax-pane", "im?", "im!".
    let permissionCalls = Locked([String]())
    /// What System Settings calls the Accessibility pane: macOS 26's name
    /// unless a test sets another, so no sentence depends on the test Mac.
    var accessibilitySettingsName = DictationPermissionStep.accessibilitySettingsName(majorVersion: 26)
    let clipboardSettles = Locked(0)
    /// Short bounds, so a deadline test takes a fraction of a second.
    var timing: DictationTiming = {
        var t = DictationTiming()
        t.startSeconds = 0.3
        t.transcribeSeconds = 0.3
        t.shutdownSeconds = 0.3
        return t
    }()
    /// What the press-time privacy gate answers, and how often it was asked.
    let privacy = Locked<DictationInsertFailure?>(nil)
    let privacyChecks = Locked(0)
    /// Text the clipboard fallback was handed, and whether it lands.
    let clipboard = Locked([String]())
    let clipboardWorks = Locked(true)
    /// The bundle id the formatter is told the text is going to.
    let targetApplication = Locked<String?>(nil)
    /// How often the log folder was opened, and whether opening works.
    let logFolderOpens = Locked(0)
    let logFolderOpenWorks = Locked(true)
    /// How often a quit-and-reopen was asked for.
    let relaunches = Locked(0)
    let loginItem = FakeLoginItem()
    let correctionWatch = FakeCorrectionWatch()
    /// Where this harness's `learning.json` lives; removed with the harness.
    let learningFiles = ScratchDirectory()
    /// Every line the model logged.
    let logLines = Locked([String]())
    /// Built lazily so every model a harness makes shares one learning actor,
    /// as the app does.
    private let learningBox = Locked<DictationLearning?>(nil)
    /// `false` gives the model no learning store at all.
    var learningAvailable = true
    /// The word list learning uses; set before the first model is made.
    var lexicon: any LearningLexicon = testLexicon

    /// Where this harness's take spool lives; removed with the harness.
    let spoolFiles = ScratchDirectory()
    /// Where this harness's dictation history lives, its key in memory;
    /// `historyAvailable = false` gives the model no history, as the
    /// existing tests had.
    let historyFiles = ScratchDirectory()
    let historyKeys = MemoryHistoryKeys()
    var historyAvailable = false
    /// The app's own bundle id, as the installed app has it: a paste into
    /// its own window is not kept in the history.
    var ownBundleID: String? = "ai.scam.dictation"
    /// One store per harness, as the app has one per process.
    private let historyBox = Locked<DictationHistoryStore?>(nil)
    var historyStore: DictationHistoryStore? {
        guard historyAvailable else { return nil }
        let url = historyFiles.url, keys = historyKeys
        return historyBox.mutate { box in
            if let box { return box }
            let made = DictationHistoryStore(directory: url, keys: keys)
            box = made
            return made
        }
    }

    /// A fresh store over the same folder and key: what the next launch
    /// would read from the disk.
    func reopenedHistory() async throws -> DictationHistoryStore.Snapshot {
        try await DictationHistoryStore(directory: historyFiles.url, keys: historyKeys).load()
    }
    /// How long a kept take is offered; a test shortens it rather than
    /// waiting a week.
    var keptFor: TimeInterval = 7 * 24 * 60 * 60

    /// The spool directory's file names right now, sorted.
    func takeSpoolFiles() -> [String] {
        (try? FileManager.default.contentsOfDirectory(atPath: spoolFiles.url.path))?.sorted() ?? []
    }

    var settings: DictationSettings { DictationSettings(defaults: scratch.defaults) }

    var learningURL: URL { learningFiles.url.appendingPathComponent("dictation/learning.json") }

    var learning: DictationLearning? {
        guard learningAvailable else { return nil }
        let url = learningURL, lexicon = lexicon
        return learningBox.mutate { box in
            if let box { return box }
            let made = DictationLearning(store: LearningStore(url: url), lexicon: lexicon)
            box = made
            return made
        }
    }

    /// `learning.json` as it is on disk now, or `nil` when there is none.
    var learningFile: LearningState? {
        FileManager.default.contents(atPath: learningURL.path).flatMap(LearningStore.decode)
    }

    /// The raw bytes of `learning.json`, for "never contains" checks.
    var learningFileText: String {
        FileManager.default.contents(atPath: learningURL.path).map { String(decoding: $0, as: UTF8.self) } ?? ""
    }

    func environment() -> DictationEnvironment {
        let workers = workers, trigger = trigger, detector = detector, usesDetector = usesDetector
        let mouse = mouse
        let resources = resources, grants = grants
        let calls = permissionCalls, settles = clipboardSettles, paneName = accessibilitySettingsName
        let privacy = privacy, privacyChecks = privacyChecks
        let clipboard = clipboard, clipboardWorks = clipboardWorks
        let target = targetApplication, opens = logFolderOpens, openWorks = logFolderOpenWorks
        let relaunchCount = relaunches
        let lines = logLines
        let reserves = reservesShortcuts
        var environment = DictationEnvironment(
            settings: settings,
            capture: capture,
            permissions: DictationPermissions(
                requestMicrophone: { calls.mutate { $0.append("mic") }; return grants.get.microphone },
                microphoneAuthorized: { calls.mutate { $0.append("mic?") }; return grants.get.microphone },
                accessibilityTrusted: { prompt in
                    calls.mutate { $0.append(prompt ? "ax!" : "ax?") }
                    return grants.get.accessibility
                },
                inputMonitoringGranted: { calls.mutate { $0.append("im?") }; return grants.get.inputMonitoring },
                requestInputMonitoring: { calls.mutate { $0.append("im!") } },
                openMicrophoneSettings: { calls.mutate { $0.append("mic-pane") } },
                openAccessibilitySettings: { calls.mutate { $0.append("ax-pane") } },
                accessibilitySettingsName: paneName),
            resolveResources: { resources },
            makeWorker: { workers.make($0) },
            makeTrigger: { key -> any DictationHoldTrigger in
                if usesDetector { detector.update(key); return detector }
                trigger.update(key)
                return trigger
            },
            makeMouseTrigger: { button -> any DictationMouseTrigger in
                mouse.update(button)
                return mouse
            },
            keyWatch: keyWatch,
            stopTrap: stopTrap,
            privacyGate: { privacyChecks.mutate { $0 += 1 }; return privacy.get },
            insertText: inserts.insert,
            clipboardFallback: { text in
                clipboard.mutate { $0.append(text) }
                return clipboardWorks.get
            },
            settleClipboard: { settles.mutate { $0 += 1 } },
            targetApplication: { target.get },
            openLogFolder: {
                opens.mutate { $0 += 1 }
                return openWorks.get
            },
            relaunch: { relaunchCount.mutate { $0 += 1 } },
            loginItem: loginItem.seam,
            correctionWatch: correctionWatch,
            learning: learning,
            log: { line in lines.mutate { $0.append(line) } },
            timing: timing,
            // The spool lands in a scratch folder, and its file work runs at
            // once: a file is on disk when the call returns.
            takes: DictationTakeOptions(
                directory: spoolFiles.url, keptFor: keptFor, runFileWork: { work in work() }),
            history: historyStore,
            ownBundleID: ownBundleID)
        environment.canReserveShortcut = { _ in reserves }
        return environment
    }

    func model() -> DictationModel { DictationModel(environment: environment()) }
}

/// Polls `condition` on the main actor until it holds or `seconds` pass.
@MainActor
func eventually(_ seconds: Double = 5, _ condition: @MainActor () -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(seconds)
    while Date() < deadline {
        if condition() { return true }
        try? await Task.sleep(for: .milliseconds(10))
    }
    return condition()
}

/// Every status line the model published, in order, so a test can assert on
/// a message that a faster one replaced before any poll could see it.
@MainActor
final class StatusHistory {
    private(set) var lines: [String] = []
    private var subscription: AnyCancellable?

    init(_ model: DictationModel) {
        subscription = model.$status.sink { [weak self] line in self?.lines.append(line) }
    }

    func contains(_ text: String) -> Bool { lines.contains { $0.contains(text) } }
}

/// Whether `operation` returns within `seconds`. A missing deadline in the
/// code under test then fails the test instead of hanging the whole run: the
/// stuck operation is left behind, parked, and the test carries on.
@MainActor
func finishes(within seconds: Double, _ operation: @escaping @MainActor () async -> Void) async -> Bool {
    final class Once: @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: CheckedContinuation<Bool, Never>?
        init(_ continuation: CheckedContinuation<Bool, Never>) { self.continuation = continuation }
        func resume(_ value: Bool) {
            lock.lock(); let c = continuation; continuation = nil; lock.unlock()
            c?.resume(returning: value)
        }
    }
    return await withCheckedContinuation { continuation in
        let once = Once(continuation)
        Task { @MainActor in await operation(); once.resume(true) }
        Task { try? await Task.sleep(for: .seconds(seconds)); once.resume(false) }
    }
}
