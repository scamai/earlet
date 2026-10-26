import AppKit
import ApplicationServices
import Foundation

/// The correction observer. After Dictation pastes, watches the span it just
/// pasted to see what the user changes there, and reports that span's final
/// text (LEARNING.md §7). What is learned from it is decided elsewhere
/// (`DictationLearning` in the app); this type only watches and reports.
///
/// This observer is a native implementation with four deliberate safeguards:
/// - **The anchor.** The span is proved by the same field's before/after
///   values, or an exact caret-derived span when no snapshot is available
///   (`DictationSpanAnchor`). The caret-only path is used only when no field
///   snapshot is available.
/// - **Admission.** At the start, Secure Input must be off and the focused
///   element must be an editable, non-password field of the app the paste
///   went to (`DictationObservationAdmission`).
/// - **What leaves.** The result carries the span's final text and nothing
///   else from the field, and only for an end that may be learned from. A
///   continuous watch's end carries no text at all, only whether the span
///   was still located at the end and whether it ever changed.
/// - **Abandon.** `abandon()` ends an observation without reporting a span,
///   for pause, quit, the next dictation and the learning switch.
///
/// PRIVACY: this observes only a span Dictation itself just inserted, never
/// arbitrary focused text; everything stays in-process.
///
/// Design §4's cross-process risk drives every structural decision below:
/// `AXObserver` delivery is synchronous IPC INTO this app from the target
/// app's Accessibility server — a slow callback here stalls the OBSERVED
/// app, not just this one. So: a dedicated background thread owns the run
/// loop (never the main thread), the callback does the minimum possible
/// AX work and hands off to a debounced serial queue for everything else,
/// and `AXUIElementSetMessagingTimeout` bounds the reverse risk (a hung
/// target app blocking this one).
public final class DictationCorrectionObserver: @unchecked Sendable {

    public struct Config: Sendable {
        /// Condition 5's wall-clock cap (design §3: "90 seconds is the
        /// proposed default").
        public var observationWindowSeconds: TimeInterval
        /// Condition 7's threshold (design §3: "e.g. 0.6 normalized edit
        /// distance").
        public var editDistanceThreshold: Double
        /// `AXUIElementSetMessagingTimeout` bound (design §4.3: "proposed:
        /// 300ms").
        public var messagingTimeoutSeconds: Float
        /// Debounce window for coalescing a burst of
        /// `kAXValueChangedNotification` (design §4.4: "at most once per
        /// 150ms").
        public var debounceSeconds: TimeInterval
        /// OUR addition (`DictationTerminalCondition.editSettled` — see its
        /// doc comment for the full diagnosis): how long the field must go
        /// quiet, after the user has diverged from the dictated text,
        /// before that quiet period is treated as "done editing."
        ///
        /// **4 seconds, chosen deliberately** (an anchor that is a plain text
        /// search breaks on the first edit and never needs a settle period,
        /// so there is no prior value to borrow). The two failure modes bound
        /// it from both sides: too short and an ordinary mid-word thinking pause
        /// (a beat between finishing one correction and starting the next,
        /// or just pausing to read what was typed) gets cut off and mined
        /// as if the user were done; too long and the fix stops mattering
        /// — at the 90s backstop's scale, a few extra seconds is noise. 4s
        /// sits comfortably above normal inter-keystroke/inter-word gaps
        /// (a few hundred ms) and normal "let me re-read this" pauses,
        /// while still landing this near-instantly relative to the 90s
        /// backstop it's closing the gap in front of — the same order of
        /// magnitude as this project's own debounce-adjacent constants
        /// (`idleCooldownSeconds` in `DictateCLI` waits 12s before
        /// declaring the whole SESSION idle; this is a much narrower,
        /// per-edit signal, so it should be — and is — a fraction of that).
        public var settleSeconds: TimeInterval
        /// How many times the span is looked for after a paste, and how long
        /// apart. The paste is confirmed when the target app asks for the
        /// pasteboard data, which can be a moment before its field's value
        /// includes the text; the anchor is retried rather than declared
        /// missing on the first read. Proposed values, not measured.
        public var anchorAttempts: Int
        public var anchorRetrySeconds: TimeInterval
        /// How long the edited span must have been still when the whole
        /// field empties for it to count as what was sent
        /// (`DictationSentDraft`). Shorter than `settleSeconds` because a
        /// send is itself the signal that editing ended; long enough that a
        /// word still being typed when Return was pressed, which the 0.6 s
        /// terminal poll may have caught half-way, is not learned.
        public var sendSettleSeconds: TimeInterval

        public init(
            observationWindowSeconds: TimeInterval = 90,
            editDistanceThreshold: Double = 0.6,
            messagingTimeoutSeconds: Float = 0.3,
            debounceSeconds: TimeInterval = 0.15,
            settleSeconds: TimeInterval = 4.0,
            anchorAttempts: Int = 6,
            anchorRetrySeconds: TimeInterval = 0.1,
            sendSettleSeconds: TimeInterval = 1.0
        ) {
            self.observationWindowSeconds = observationWindowSeconds
            self.editDistanceThreshold = editDistanceThreshold
            self.messagingTimeoutSeconds = messagingTimeoutSeconds
            self.debounceSeconds = debounceSeconds
            self.settleSeconds = settleSeconds
            self.anchorAttempts = anchorAttempts
            self.anchorRetrySeconds = anchorRetrySeconds
            self.sendSettleSeconds = sendSettleSeconds
        }
    }

    /// The feature's own diagnostics — task step 3: "RECORD which fired".
    public let tally = DictationTerminalConditionTally()

    private let config: Config
    private let diagnostic: @Sendable (DictationAnchorSearch.Diagnostic) -> Void
    private let lock = NSLock()
    // Guarded by `lock`.
    private var activeSession: ObservationSession?
    /// Moves on with every start and every abandon. A start still looking for
    /// its anchor gives up when this is no longer the value it began with, so
    /// an abandon that lands during the anchor retries still wins.
    private var generation = 0
    /// Why `generation` last moved: what a superseded start reports.
    private var supersededBy = DictationTerminalCondition.nextTake
    private var backgroundThread: Thread?
    private var backgroundRunLoop: CFRunLoop?

    /// Admission and anchoring: AX reads and short retries, never on the
    /// caller's thread (the app calls in from the main actor).
    private let setupQueue = DispatchQueue(label: "ai.scam.dictation.observer.setup", qos: .utility)

    public init(config: Config = Config(),
                diagnostic: @escaping @Sendable (DictationAnchorSearch.Diagnostic) -> Void = { _ in }) {
        self.config = config
        self.diagnostic = diagnostic
    }

    // ─── Starting and stopping ───

    /// An in-memory snapshot of the admitted field immediately before a
    /// paste. The host can pass it back, but cannot read any field content.
    /// AX references are immutable; all actual reads stay on `setupQueue`.
    public struct PasteSnapshot: @unchecked Sendable {
        let element: AXUIElement
        let pid: pid_t
        let bundleID: String
        let field: DictationAnchorSearch.FieldRead

        func matches(element: AXUIElement, pid: pid_t, bundleID: String) -> Bool {
            self.pid == pid && self.bundleID == bundleID && CFEqual(self.element, element)
        }
    }

    /// Called only for a dictation about to paste with learning enabled.
    /// Admission precedes every value read, including this before snapshot.
    public func preparePaste(expectedBundleID: String) async -> PasteSnapshot? {
        await withCheckedContinuation { continuation in
            setupQueue.async { [self] in
                guard case .admitted(let target) = focusedTarget(expectedBundleID: expectedBundleID) else {
                    continuation.resume(returning: nil)
                    return
                }
                AXUIElementSetMessagingTimeout(target.element, config.messagingTimeoutSeconds)
                let field = Self.readField(target.element, bundleID: target.bundleID)
                let snapshot = PasteSnapshot(element: target.element, pid: target.pid,
                                             bundleID: target.bundleID, field: field)
                // A focus/security change during an AX round trip invalidates
                // this snapshot. Nothing is persisted or sent to a worker.
                guard field.value != nil,
                      case .admitted(let current) = focusedTarget(expectedBundleID: expectedBundleID),
                      snapshot.matches(element: current.element, pid: current.pid, bundleID: current.bundleID) else {
                    continuation.resume(returning: nil)
                    return
                }
                continuation.resume(returning: snapshot)
            }
        }
    }

    /// Watches the field that has focus now for `pasted`, the text just
    /// pasted into the app `expectedBundleID`. `onFinished` fires exactly
    /// once, on a background queue.
    ///
    /// Returns at once. Admission and the anchor are settled on a background
    /// queue: Secure Input off, an editable non-password field, the expected
    /// app, and an exact proof of where `pasted` went. Any
    /// refusal ends the observation with that condition and reports no span.
    ///
    /// Design §3 condition 2 (`next_take`): starting a new
    /// observation ends any earlier one first — only one is live at a time.
    public func beginObservingFocusedField(
        pasted: String,
        expectedBundleID: String,
        beforePaste: PasteSnapshot? = nil,
        onRevision: (@Sendable (String) -> Void)? = nil,
        onFinished: @escaping @Sendable (DictationObservationResult) -> Void
    ) {
        let generation = supersede(with: .nextTake)
        setupQueue.async { [self] in
            let target: Target
            switch focusedTarget(expectedBundleID: expectedBundleID) {
            case .refused(let refusal):
                finishImmediately(refusal, dictatedText: pasted, pastedText: pasted,
                                  observedApp: expectedBundleID, onFinished: onFinished)
                return
            case .admitted(let admitted): target = admitted
            }
            if let beforePaste, !beforePaste.matches(
                element: target.element, pid: target.pid, bundleID: target.bundleID) {
                finishImmediately(.spanContextLost, dictatedText: pasted, pastedText: pasted,
                                  observedApp: expectedBundleID, onFinished: onFinished)
                return
            }
            establish(target, dictatedText: pasted, pastedText: pasted,
                      generation: generation, beforePaste: beforePaste?.field, onRevision: onRevision, onFinished: onFinished)
        }
    }

    /// Begins observing `observedElement`, a field the caller resolved itself
    /// (the `DictationSession` path). The element must not be a password
    /// field and Secure Input must be off; the anchor is found as in
    /// `beginObservingFocusedField`.
    public func beginObserving(
        dictatedText: String,
        pastedText: String,
        observedElement: AXUIElement,
        applicationElement: AXUIElement,
        pid: pid_t,
        observedApp: String,
        onRevision: (@Sendable (String) -> Void)? = nil,
        onFinished: @escaping @Sendable (DictationObservationResult) -> Void
    ) {
        let generation = supersede(with: .nextTake)
        let target = Target(element: observedElement, applicationElement: applicationElement,
                            pid: pid, bundleID: observedApp)
        setupQueue.async { [self] in
            let secureInput = TextInserter.secureInputGate() != nil
            let focus = secureInput ? nil : FocusEditabilityChecker.check(element: target.element)
            if let refusal = DictationObservationAdmission.refusal(
                secureInput: secureInput, focus: focus, bundleID: observedApp, expectedBundleID: observedApp) {
                finishImmediately(refusal, dictatedText: dictatedText, pastedText: pastedText,
                                  observedApp: observedApp, onFinished: onFinished)
                return
            }
            establish(target, dictatedText: dictatedText, pastedText: pastedText,
                      generation: generation, onRevision: onRevision, onFinished: onFinished)
        }
    }

    /// Ends the current observation, or a start still settling its anchor,
    /// WITHOUT reporting a span: it finishes as `.abandoned` whatever the
    /// field holds, even when a recompute had already decided otherwise.
    /// For pause, quit, the next dictation and the learning switch.
    public func abandon() {
        supersede(with: .abandoned)
    }

    /// An explicit stop, which ends the observation as if its window had
    /// elapsed and so still reports the span. Dictation uses `abandon()`.
    public func cancelActiveObservation() {
        lock.lock()
        let session = activeSession
        activeSession = nil
        lock.unlock()
        session?.terminateIfActive(.watchExpired)
    }

    /// Moves `generation` on and ends the live observation with `condition`,
    /// overriding whatever a recompute in flight would have reported.
    @discardableResult
    private func supersede(with condition: DictationTerminalCondition) -> Int {
        lock.lock()
        generation += 1
        supersededBy = condition
        let current = generation
        let session = activeSession
        activeSession = nil
        lock.unlock()
        session?.interrupt(condition)
        return current
    }

    private func clearActiveSession(_ session: ObservationSession) {
        lock.lock()
        if activeSession === session { activeSession = nil }
        lock.unlock()
    }

    // ─── The anchor ───

    /// The field and its app. `@unchecked Sendable`: AX elements are
    /// immutable references to another process's UI, used from the setup
    /// queue only.
    private struct Target: @unchecked Sendable {
        let element: AXUIElement
        let applicationElement: AXUIElement
        let pid: pid_t
        let bundleID: String
    }

    private enum TargetRead {
        case admitted(Target)
        case refused(DictationTerminalCondition)
    }

    private func focusedTarget(expectedBundleID: String) -> TargetRead {
        let secureInput = TextInserter.secureInputGate() != nil
        let element = secureInput ? nil : FocusEditabilityChecker.focusedElement()
        let focus = element.map { FocusEditabilityChecker.check(element: $0) }
        if secureInput || focus == .secureField { return .refused(.secureField) }
        var pid: pid_t = 0
        var bundleID: String?
        if let element, AXUIElementGetPid(element, &pid) == .success, pid > 0 {
            bundleID = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier
        }
        if let refusal = DictationObservationAdmission.refusal(
            secureInput: secureInput, focus: focus, bundleID: bundleID, expectedBundleID: expectedBundleID) {
            return .refused(refusal)
        }
        guard let element, let bundleID else { return .refused(.noFocusedField) }
        return .admitted(Target(element: element, applicationElement: AXUIElementCreateApplication(pid),
                                pid: pid, bundleID: bundleID))
    }

    private static func readField(_ element: AXUIElement, bundleID: String) -> DictationAnchorSearch.FieldRead {
        let value = readValue(element)
        let selection = DictationWittyInput.supports(bundleID: bundleID) ? nil : readSelectedRange(element)
        return fieldSnapshot(value: value, selectedRange: selection, bundleID: bundleID)
    }

    /// Route the AX snapshot before it can enter the pasted-span search.
    static func fieldSnapshot(value: String?, selectedRange: CFRange?,
                              bundleID: String) -> DictationAnchorSearch.FieldRead {
        if DictationWittyInput.supports(bundleID: bundleID) {
            // A terminal screen is never a fallback editable field.
            let input = value.flatMap(DictationWittyInput.parse)
            return .init(value: input?.value, selectionLocation: nil, selectionLength: nil)
        }
        return .init(value: value, selectionLocation: selectedRange?.location,
                     selectionLength: selectedRange?.length)
    }

    /// On `setupQueue`: finds the pasted span (`DictationAnchorSearch`, which
    /// retries briefly while the field catches up with the paste), then hands
    /// over to a live session.
    private func establish(
        _ target: Target, dictatedText: String, pastedText: String, generation: Int,
        beforePaste: DictationAnchorSearch.FieldRead? = nil,
        onRevision: (@Sendable (String) -> Void)? = nil,
        onFinished: @escaping @Sendable (DictationObservationResult) -> Void
    ) {
        let isWitty = DictationWittyInput.supports(bundleID: target.bundleID)
        let dictatedText = isWitty ? DictationWittyInput.pasted(dictatedText) : dictatedText
        let pastedText = isWitty ? DictationWittyInput.pasted(pastedText) : pastedText
        AXUIElementSetMessagingTimeout(target.element, config.messagingTimeoutSeconds)
        AXUIElementSetMessagingTimeout(target.applicationElement, config.messagingTimeoutSeconds)
        DictationAnchorSearch.run(
            pasted: pastedText, beforePaste: beforePaste,
            attempts: config.anchorAttempts, retrySeconds: config.anchorRetrySeconds,
            queue: setupQueue,
            read: { Self.readField(target.element, bundleID: target.bundleID) },
            isCurrent: { [weak self] in
                // An observer that is gone has nothing current.
                guard let self else { return false }
                return supersededCondition(since: generation) == nil
            },
            diagnostic: diagnostic,
            completion: { [weak self] outcome in
                guard let self else { return }
                switch outcome {
                case .anchored(let anchor, let value):
                    startSession(target, anchor: anchor, fieldValue: value, dictatedText: dictatedText,
                                 pastedText: pastedText, generation: generation, onRevision: onRevision, onFinished: onFinished)
                case .notFound:
                    // Neither an exact change nor a usable caret proves a span.
                    finishImmediately(.spanMissing, dictatedText: dictatedText, pastedText: pastedText,
                                      observedApp: target.bundleID, onFinished: onFinished)
                case .superseded:
                    finishImmediately(supersededCondition(since: generation) ?? .abandoned,
                                      dictatedText: dictatedText, pastedText: pastedText,
                                      observedApp: target.bundleID, onFinished: onFinished)
                }
            })
    }

    private func startSession(
        _ target: Target, anchor: DictationSpanAnchor, fieldValue: String, dictatedText: String,
        pastedText: String, generation: Int,
        onRevision: (@Sendable (String) -> Void)? = nil,
        onFinished: @escaping @Sendable (DictationObservationResult) -> Void
    ) {
        var terminalInput: DictationWittyInput?
        if DictationWittyInput.supports(bundleID: target.bundleID) {
            terminalInput = Self.readValue(target.element).flatMap(DictationWittyInput.parse)
            guard terminalInput?.value == fieldValue else {
                finishImmediately(.spanContextLost, dictatedText: dictatedText, pastedText: pastedText,
                                  observedApp: target.bundleID, onFinished: onFinished)
                return
            }
        }
        let runLoop = ensureBackgroundRunLoop()
        let session = ObservationSession(
            dictatedText: dictatedText, pastedText: pastedText, observedApp: target.bundleID,
            element: target.element, applicationElement: target.applicationElement, pid: target.pid,
            anchor: anchor, startedAt: Date(), initialFieldValue: fieldValue,
            config: config, tally: tally, terminalInput: terminalInput)
        session.onRevision = onRevision
        session.onFinished = { [weak self] result in
            self?.clearActiveSession(session)
            onFinished(result)
        }

        // The search checked `generation` before its last read; an abandon
        // can still land between that check and here.
        lock.lock()
        guard generation == self.generation else {
            let superseded = supersededBy
            lock.unlock()
            finishImmediately(superseded, dictatedText: dictatedText, pastedText: pastedText,
                              observedApp: target.bundleID, onFinished: onFinished)
            return
        }
        activeSession = session
        lock.unlock()
        session.startObserving(on: runLoop)
    }

    /// The condition a start begun at `generation` must end with, or `nil`
    /// while it is still the latest.
    private func supersededCondition(since generation: Int) -> DictationTerminalCondition? {
        lock.lock(); defer { lock.unlock() }
        return generation == self.generation ? nil : supersededBy
    }

    /// A dedicated background thread with its own run loop (design §4.2)
    /// — never the main thread, and never competing with AppKit/SwiftUI's
    /// main-thread event loop for scheduling. Created lazily, reused
    /// across observations for the process's lifetime.
    private func ensureBackgroundRunLoop() -> CFRunLoop? {
        lock.lock(); defer { lock.unlock() }
        if let backgroundRunLoop { return backgroundRunLoop }

        let ready = DispatchSemaphore(value: 0)
        let box = RunLoopBox()
        let thread = Thread {
            box.runLoop = CFRunLoopGetCurrent()
            // A run loop with no sources/timers returns immediately from
            // `run()` — a never-firing port keeps this thread's run loop
            // alive for the process's lifetime, the standard pattern for
            // an AXObserver run loop that must outlive any one
            // observation.
            let keepAlivePort = Port()
            RunLoop.current.add(keepAlivePort, forMode: .default)
            ready.signal()
            RunLoop.current.run()
        }
        thread.name = "ai.scam.dictation.observer"
        thread.qualityOfService = .utility
        thread.start()
        ready.wait()

        backgroundThread = thread
        backgroundRunLoop = box.runLoop
        return backgroundRunLoop
    }

    /// Ends an observation that never started watching: no span, no reads.
    private func finishImmediately(
        _ condition: DictationTerminalCondition, dictatedText: String, pastedText: String,
        observedApp: String, onFinished: @escaping @Sendable (DictationObservationResult) -> Void
    ) {
        tally.record(condition)
        let observation = DictationObservation(
            dictatedText: dictatedText, formattedText: nil, pastedText: pastedText, editedText: nil,
            terminalCondition: condition, observedApp: observedApp,
            startedAt: Date(), terminatedAt: Date())
        onFinished(DictationObservationResult(observation: observation, candidates: []))
    }

    // ─── AX reads (static, side-effect-free beyond the AX round trip) ───

    static func readValue(_ element: AXUIElement) -> String? {
        var value: CFTypeRef?
        let err = AXUIElementCopyAttributeValue(element, kAXValueAttribute as CFString, &value)
        guard err == .success else { return nil }
        return value as? String
    }

    /// The selection as UTF-16 offsets. Right after a paste it is the caret
    /// the paste left, which is what `DictationSpanAnchor` starts from.
    static func readSelectedRange(_ element: AXUIElement) -> CFRange? {
        var value: CFTypeRef?
        let err = AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &value)
        guard err == .success, let value else { return nil }
        guard CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        // Safe: the CFGetTypeID check above guarantees this is an AXValue.
        let axValue = value as! AXValue // swiftlint:disable:this force_cast
        guard AXValueGetType(axValue) == .cfRange else { return nil }
        var range = CFRange(location: 0, length: 0)
        guard AXValueGetValue(axValue, .cfRange, &range) else { return nil }
        return range
    }

    /// Whether `element` is still its application's focused element. An
    /// unreadable answer counts as focus having left.
    static func isFocused(_ element: AXUIElement, in application: AXUIElement) -> Bool {
        var focused: CFTypeRef?
        let err = AXUIElementCopyAttributeValue(application, kAXFocusedUIElementAttribute as CFString, &focused)
        guard err == .success, let focused else { return false }
        return CFEqual(focused, element)
    }
}

/// Plain box so a background `Thread`'s closure can hand its
/// `CFRunLoopGetCurrent()` back to the caller across the readiness
/// semaphore without capturing `self`.
private final class RunLoopBox: @unchecked Sendable {
    var runLoop: CFRunLoop?
}

/// Per-observation state — from the anchor through the debounced recompute
/// loop to teardown. One instance per live observation;
/// `DictationCorrectionObserver` owns at most one at a time. Everything
/// after `init` runs on `queue`.
private final class ObservationSession: @unchecked Sendable {
    let dictatedText: String
    let pastedText: String
    let observedApp: String
    let element: AXUIElement
    let applicationElement: AXUIElement
    let pid: pid_t
    let anchor: DictationSpanAnchor
    let startedAt: Date
    let config: DictationCorrectionObserver.Config
    let tally: DictationTerminalConditionTally
    let terminalInput: DictationWittyInput?

    /// Set by the session right after `init`, before `startObserving`. Defaults
    /// to a no-op so a session that never gets wired up can't crash on a nil
    /// call.
    var onRevision: (@Sendable (String) -> Void)?
    private var revisions = DictationStableRevisions()
    var onFinished: @Sendable (DictationObservationResult) -> Void = { _ in }

    private let lock = NSLock()
    private var finished = false
    /// Set by `interrupt`: the condition this session ends with, whatever a
    /// recompute already on the queue decides.
    private var forced: DictationTerminalCondition?
    private var lastFieldValue: String
    /// Sticky: the span was seen to differ from `dictatedText` at least one
    /// recompute. Never resets — a change that was reverted still means the
    /// end is not "No correction detected."
    private var sawChange = false
    /// OUR addition's clock: wall-clock time the field's content last
    /// actually changed. Starts at `startedAt` (the paste itself is the
    /// first "content" this observation ever saw) and only advances when a
    /// recompute observes `fullValue != previous` — a spurious notification
    /// with no real content change (e.g. a bare selection move) must NOT
    /// reset it, or the settle timer would never converge on a user who's
    /// just clicking around without editing.
    private var lastChangeAt: Date
    private var runLoopRef: CFRunLoop?
    private var axObserver: AXObserver?
    private var retainedSelf: Unmanaged<ObservationSession>?
    private var debounceWorkItem: DispatchWorkItem?
    /// A focus notification arrived since the last recompute; the next one
    /// checks whether the field still has focus.
    private var focusCheckPending = false
    /// OUR addition: the "field's been quiet long enough" wakeup, rescheduled
    /// after every recompute that didn't already terminate the session and
    /// left it diverged from `dictatedText`. Distinct from `debounceWorkItem`
    /// (which coalesces a BURST of notifications into one recompute) — this
    /// fires in the ABSENCE of further notifications, which is exactly the
    /// case debounce alone can never catch: no more notifications ever
    /// arrive once the user stops editing, so nothing re-invokes `recompute`
    /// without a timer dedicated to that silence.
    private var settleWorkItem: DispatchWorkItem?
    private var terminalPollWorkItem: DispatchWorkItem?

    /// Serial — every start, recompute and terminate happens here, off the
    /// AXObserver callback thread (design §4.1).
    private let queue = DispatchQueue(label: "ai.scam.dictation.observer.session", qos: .utility)

    init(
        dictatedText: String, pastedText: String, observedApp: String, element: AXUIElement,
        applicationElement: AXUIElement, pid: pid_t, anchor: DictationSpanAnchor, startedAt: Date,
        initialFieldValue: String, config: DictationCorrectionObserver.Config,
        tally: DictationTerminalConditionTally, terminalInput: DictationWittyInput? = nil
    ) {
        self.dictatedText = dictatedText
        self.pastedText = pastedText
        self.observedApp = observedApp
        self.element = element
        self.applicationElement = applicationElement
        self.pid = pid
        self.anchor = anchor
        self.startedAt = startedAt
        self.lastFieldValue = initialFieldValue
        self.lastChangeAt = startedAt
        self.config = config
        self.tally = tally
        self.terminalInput = terminalInput
    }

    /// On `queue`, so an `interrupt` that raced the hand-over is ordered
    /// before or after it, never beside it: a session already finished
    /// registers nothing.
    func startObserving(on runLoop: CFRunLoop?) {
        runLoopRef = runLoop
        queue.async { [self] in
            guard !isFinished() else { return }
            guard let runLoop = runLoopRef else {
                terminate(.watchExpired, span: nil)
                return
            }

            var observer: AXObserver?
            let createErr = AXObserverCreate(pid, Self.axCallback, &observer)
            if createErr == .success, let observer {
                self.axObserver = observer
                let retained = Unmanaged.passRetained(self)
                retainedSelf = retained
                let refcon = retained.toOpaque()
                _ = AXObserverAddNotification(observer, element, kAXValueChangedNotification as CFString, refcon)
                _ = AXObserverAddNotification(observer, element, kAXSelectedTextChangedNotification as CFString, refcon)
                _ = AXObserverAddNotification(
                    observer, applicationElement, kAXFocusedUIElementChangedNotification as CFString, refcon)
                _ = AXObserverAddNotification(observer, element, kAXUIElementDestroyedNotification as CFString, refcon)
                CFRunLoopAddSource(runLoop, AXObserverGetRunLoopSource(observer), .defaultMode)
            } else if terminalInput == nil {
                // No observation mechanism at all — the anchor itself was
                // fine, but with nothing to watch it, this degrades to
                // span_context_lost rather than pretending to observe.
                terminate(.spanContextLost, span: nil)
                return
            }
            // Witty emits selection changes, but no value-change events for
            // edits. Poll only this proven composer, for this observation's
            // existing lifetime; focus/security/layout changes end it.
            scheduleTerminalPoll()

            // Condition 5's hard cap — the backstop that guarantees teardown
            // even if the target app never sends a clean signal (design §4.5).
            queue.asyncAfter(deadline: .now() + config.observationWindowSeconds) { [weak self] in
                self?.terminateIfActive(.watchExpired)
            }
        }
    }

    private static let axCallback: AXObserverCallback = { _, _, notification, refcon in
        guard let refcon else { return }
        // Minimal work on the callback thread (design §4.1): identify the
        // session and hand off immediately. No further AX calls here —
        // the observed app's Accessibility server is synchronously
        // blocked on this callback returning.
        let session = Unmanaged<ObservationSession>.fromOpaque(refcon).takeUnretainedValue()
        let focusChanged = CFEqual(notification, kAXFocusedUIElementChangedNotification as CFString)
        session.scheduleDebouncedRecompute(focusChanged: focusChanged)
    }

    private func scheduleDebouncedRecompute(focusChanged: Bool) {
        queue.async { [weak self] in
            guard let self else { return }
            if focusChanged { self.focusCheckPending = true }
            self.debounceWorkItem?.cancel()
            let item = DispatchWorkItem { [weak self] in self?.recompute() }
            self.debounceWorkItem = item
            self.queue.asyncAfter(deadline: .now() + self.config.debounceSeconds, execute: item)
        }
    }

    private func recompute() {
        guard !isFinished() else { return }
        let elapsed = Date().timeIntervalSince(startedAt)

        guard TextInserter.secureInputGate() == nil,
              FocusEditabilityChecker.check(element: element) == .editable else {
            terminate(.secureField, span: nil)
            return
        }
        if terminalInput != nil {
            guard DictationCorrectionObserver.isFocused(element, in: applicationElement) else {
                terminate(.focusLeft, span: anchor.span(in: lastFieldValueSnapshot()))
                return
            }
        }

        // Every AX read here must tolerate the messaging-timeout error
        // (design §4.3) and degrade rather than retry-loop — a `nil`
        // return from `readValue` is exactly that degrade path.
        guard let fullValue = currentFieldValue() else {
            terminate(.spanContextLost, span: nil)
            return
        }
        let anchoredSpanText = anchor.span(in: fullValue)

        // The end's "No correction detected." vs "No vocabulary change was
        // learned." split needs to know the span ever differed from what was
        // pasted, even when the change was later reverted or never settled
        // into a revision. Before the focus check below: an edit that lands
        // in the same recompute as the focus leaving still counts.
        if let anchoredSpanText, anchoredSpanText != dictatedText { markSawChange() }

        // The field lost focus inside its app: the observation ends here, with
        // the span as it stands (LEARNING.md §7).
        if focusCheckPending {
            focusCheckPending = false
            if !DictationCorrectionObserver.isFocused(element, in: applicationElement) {
                terminate(.focusLeft, span: anchoredSpanText)
                return
            }
        }

        let previous = lastFieldValueSnapshot()
        // How long the field had been still before this read: a send's
        // last span counts only when it was not still being typed.
        let quietBeforeRead = Date().timeIntervalSince(lastChangeAtSnapshot())
        // Bookkeeping: only a real content change resets the
        // quiet clock — a recompute triggered by e.g. a bare selection
        // change with identical field content must not look like fresh
        // activity, or the settle timer would keep getting pushed out by
        // notifications that carry no actual edit.
        if fullValue != previous {
            setLastChangeAt(Date())
        }
        setLastFieldValue(fullValue)

        let secondsSinceLastChange = Date().timeIntervalSince(lastChangeAtSnapshot())
        let tick = DictationObservationTick(
            anchorStillValid: anchoredSpanText != nil, anchoredSpanText: anchoredSpanText,
            fullFieldValue: fullValue, previousFieldValue: previous, dictatedText: dictatedText,
            elapsedSeconds: elapsed, windowSeconds: config.observationWindowSeconds,
            editDistanceThreshold: config.editDistanceThreshold,
            secondsSinceLastChange: secondsSinceLastChange, settleSeconds: config.settleSeconds)

        if let condition = DictationTerminalConditionEvaluator.evaluate(tick),
           condition != .editSettled || onRevision == nil {
            if onRevision != nil, let sent = DictationSentDraft.revision(
                condition: condition, anchoredSpanText: anchoredSpanText,
                lastSpan: anchor.span(in: previous), dictatedText: dictatedText,
                quietSeconds: quietBeforeRead, sendSettleSeconds: config.sendSettleSeconds,
                revisions: &revisions) {
                lock.lock()
                let allowed = !finished && forced == nil
                lock.unlock()
                if allowed { onRevision?(sent) }
            }
            terminate(condition, span: anchoredSpanText)
            return
        }
        if let onRevision, let span = revisions.takeIfSettled(
            span: anchoredSpanText, original: dictatedText,
            quietSeconds: secondsSinceLastChange, settleSeconds: config.settleSeconds) {
            // An interrupt can race this queue; the host also guards its token.
            lock.lock()
            let allowed = !finished && forced == nil
            lock.unlock()
            if allowed { onRevision(span) }
        }

        // Still observing: if this tick is diverged from `dictatedText`,
        // (re)schedule the settle wakeup for `settleSeconds` after the last
        // real change — the only thing that will re-invoke `recompute` once
        // notifications stop arriving. If not diverged, cancel any pending
        // wakeup — nothing to settle from yet.
        scheduleSettleWakeup(anchoredSpanText: anchoredSpanText)
    }

    private func currentFieldValue() -> String? {
        guard let terminalInput else { return DictationCorrectionObserver.readValue(element) }
        guard TextInserter.secureInputGate() == nil,
              FocusEditabilityChecker.check(element: element) == .editable,
              DictationCorrectionObserver.isFocused(element, in: applicationElement),
              let raw = DictationCorrectionObserver.readValue(element),
              let input = DictationWittyInput.parse(raw) else { return nil }
        return input.value(matching: terminalInput)
    }

    private func scheduleTerminalPoll() {
        guard terminalInput != nil, !isFinished() else { return }
        let item = DispatchWorkItem { [weak self] in
            guard let self, !self.isFinished() else { return }
            self.recompute()
            self.scheduleTerminalPoll()
        }
        terminalPollWorkItem = item
        queue.asyncAfter(deadline: .now() + DictationWittyInput.pollSeconds, execute: item)
    }

    /// OUR addition. No-op (and cancels any pending wakeup) unless this
    /// tick's anchored span has actually diverged from `dictatedText` — an
    /// untouched dictation has nothing to settle from, and scheduling a
    /// wakeup for it would just be a second, redundant path to the same
    /// backstop `watchExpired` already covers.
    private func scheduleSettleWakeup(anchoredSpanText: String?) {
        settleWorkItem?.cancel()
        settleWorkItem = nil
        guard let anchoredSpanText else { return }
        if onRevision != nil {
            guard revisions.needsSettlement(span: anchoredSpanText, original: dictatedText) else { return }
        } else if anchoredSpanText == dictatedText { return }

        let deadline = lastChangeAtSnapshot().addingTimeInterval(config.settleSeconds)
        let delay = max(0, deadline.timeIntervalSinceNow)
        let item = DispatchWorkItem { [weak self] in self?.recompute() }
        settleWorkItem = item
        queue.asyncAfter(deadline: .now() + delay, execute: item)
    }

    /// External terminate on the hard-cap timer (condition 5), reading the
    /// span one last time.
    func terminateIfActive(_ condition: DictationTerminalCondition) {
        queue.async { [weak self] in
            guard let self, !self.isFinished() else { return }
            let span = self.currentFieldValue().flatMap(self.anchor.span(in:))
            self.terminate(condition, span: span)
        }
    }

    /// Ends the session with `condition` and no span. Takes effect at once:
    /// a recompute already running on `queue` finishes with this condition
    /// too, so an abandon can never be overtaken by a span it raced.
    func interrupt(_ condition: DictationTerminalCondition) {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        forced = condition
        lock.unlock()
        queue.async { [weak self] in self?.terminate(condition, span: nil) }
    }

    private func terminate(_ proposed: DictationTerminalCondition, span: String?) {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        finished = true
        let condition = forced ?? proposed
        lock.unlock()

        tally.record(condition)
        teardown()

        // Only the anchored span, and only for an end that may be learned
        // from; an interrupt never carries one. Whether the span was still
        // located at the end rides along either way, so a continuous watch's
        // end can tell an untouched dictation from a field that never yielded
        // its span without the text leaving the observer.
        let reportedSpan = condition.reportedSpan(span)
        let editedText = onRevision == nil ? reportedSpan : nil
        let observation = DictationObservation(
            dictatedText: dictatedText, formattedText: nil, pastedText: pastedText, editedText: editedText,
            terminalCondition: condition, observedApp: observedApp,
            startedAt: startedAt, terminatedAt: Date(),
            spanObservedToEnd: reportedSpan != nil, sawChange: sawChangeSnapshot())
        let candidates = editedText.map {
            DictationCorrectionDiffExtractor.candidates(dictated: dictatedText, edited: $0)
        } ?? []
        let report = onFinished
        // The session closure holds this session; dropping it ends the cycle.
        onFinished = { _ in }
        onRevision = nil
        report(DictationObservationResult(observation: observation, candidates: candidates))
    }

    private func teardown() {
        if let observer = axObserver {
            AXObserverRemoveNotification(observer, element, kAXValueChangedNotification as CFString)
            AXObserverRemoveNotification(observer, element, kAXSelectedTextChangedNotification as CFString)
            AXObserverRemoveNotification(
                observer, applicationElement, kAXFocusedUIElementChangedNotification as CFString)
            AXObserverRemoveNotification(observer, element, kAXUIElementDestroyedNotification as CFString)
            if let runLoopRef {
                CFRunLoopRemoveSource(runLoopRef, AXObserverGetRunLoopSource(observer), .defaultMode)
            }
        }
        axObserver = nil
        retainedSelf?.release()
        retainedSelf = nil
        debounceWorkItem?.cancel()
        debounceWorkItem = nil
        settleWorkItem?.cancel()
        settleWorkItem = nil
        terminalPollWorkItem?.cancel()
        terminalPollWorkItem = nil
    }

    private func isFinished() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return finished
    }

    private func lastFieldValueSnapshot() -> String {
        lock.lock(); defer { lock.unlock() }
        return lastFieldValue
    }

    private func sawChangeSnapshot() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return sawChange
    }

    private func markSawChange() {
        lock.lock(); sawChange = true; lock.unlock()
    }

    private func setLastFieldValue(_ value: String) {
        lock.lock(); lastFieldValue = value; lock.unlock()
    }

    private func lastChangeAtSnapshot() -> Date {
        lock.lock(); defer { lock.unlock() }
        return lastChangeAt
    }

    private func setLastChangeAt(_ value: Date) {
        lock.lock(); lastChangeAt = value; lock.unlock()
    }
}

// ─── Stable draft revisions ───

/// A chat composer (Claude Code, Codex, a messenger) sends on Return and
/// empties itself, usually long before an edit has settled for
/// `settleSeconds`, so the user's fix used to be lost with the send
/// (LEARNING.md §7). When the whole field empties while the span is
/// diverged, the last span read before it is what was sent, and it is
/// published as one more revision of the same insertion if it had been
/// still for `sendSettleSeconds`. Pure, so the rule is testable without
/// Accessibility.
enum DictationSentDraft {
    static func revision(condition: DictationTerminalCondition, anchoredSpanText: String?,
                         lastSpan: String?, dictatedText: String, quietSeconds: TimeInterval,
                         sendSettleSeconds: TimeInterval,
                         revisions: inout DictationStableRevisions) -> String? {
        // The whole field emptied: the anchor is gone because nothing is
        // left to hold it. The span itself emptied inside a field that still
        // has text is a deletion, not a send.
        guard condition == .fieldCleared, anchoredSpanText == nil else { return nil }
        return revisions.takeIfSettled(span: lastSpan, original: dictatedText,
                                       quietSeconds: quietSeconds, settleSeconds: sendSettleSeconds)
    }
}

/// Pure settlement state used by the AX session. Returning to the original
/// span is a revision too; untouched dictation never becomes positive evidence.
struct DictationStableRevisions {
    private(set) var lastPublished: String?

    func needsSettlement(span: String, original: String) -> Bool {
        !span.unicodeScalars.elementsEqual((lastPublished ?? original).unicodeScalars)
    }

    mutating func takeIfSettled(span: String?, original: String, quietSeconds: TimeInterval,
                               settleSeconds: TimeInterval) -> String? {
        guard let span, quietSeconds >= settleSeconds,
              needsSettlement(span: span, original: original) else { return nil }
        lastPublished = span
        return span
    }
}
