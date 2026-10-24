import AppKit
import CoreGraphics
import Dispatch

/// Why insertion failed: one case for each reason a person would act on
/// differently, so each can be explained in its own words.
public enum DictationInsertFailure: Sendable, Equatable, CustomStringConvertible {
    case accessibilityPermissionMissing
    case focusedElementNotEditable
    case noFocusedElement
    case secureInputActive(heldByOtherProcess: Bool)
    /// The focused element identified itself as a password field
    /// (`AXSecureTextField`). Distinct from `secureInputActive`: that is a
    /// process-wide assertion some apps decline to make, this is the field's
    /// own statement. Callers MUST treat this like Secure Input — refuse the
    /// paste, keep nothing on the clipboard, and store neither the text nor
    /// the audio.
    case secureFieldFocused
    case pasteNeverRequested
    case keyEventPostFailed

    public var description: String {
        switch self {
        case .accessibilityPermissionMissing:
            return "no Accessibility permission, so the focused field cannot be read"
        case .focusedElementNotEditable:
            return "the focused element does not take text"
        case .noFocusedElement:
            return "nothing has keyboard focus to paste into"
        case .secureInputActive(let heldByOther):
            return heldByOther
                ? "another app turned on Secure Input, usually for a password field, and it blocks the paste"
                : "Secure Input is on"
        case .secureFieldFocused:
            return "the focused field is a password field, and dictation never types into one"
        case .pasteNeverRequested:
            return "the app never read the pasted text, so the paste most likely did not land"
        case .keyEventPostFailed:
            return "the ⌘V keystroke could not be sent"
        }
    }
}

/// The paste path. Insertion is paste-only: setting the field's value
/// through Accessibility first, with a paste as the fallback, was dropped on
/// 2026-08-06.
///
/// 1. AX editability gate (`FocusEditabilityChecker`, read-only).
/// 2. Secure Input gate (`SecureInputMonitor`, best-effort holder ID).
/// 3. Delayed-render pasteboard write, tagged `ConcealedType`
///    (`DelayedClipboardProvider`).
/// 4. Synthesized ⌘V with a layout-resolved 'V' keycode (`KeyCodeResolver`).
/// 5. Wait for the provider to be asked, or time out
///    (`PasteTimeoutClassifier`) — the failed-paste signal.
/// 6. Restore the prior clipboard, guarded by `changeCount`
///    (`ClipboardRestorePolicy`), and report whether it came back
///    byte-identical.
public final class TextInserter: @unchecked Sendable {

    public struct Config: Sendable {
        /// How long the target app has to ask for the pasted data before
        /// the paste counts as failed. 800ms gives real
        /// third-party apps (including a cold/backgrounded one waking up)
        /// comfortable margin above the sub-100ms case while still being
        /// short enough that a user isn't left wondering for a second-plus
        /// whether dictation is broken.
        public var pasteTimeoutMs: Double
        /// How long the dictated text stays on the clipboard before the
        /// user's previous contents go back: room for a slow paste handler
        /// (rich-text reformatting, Electron apps) to read the delayed item
        /// first.
        ///
        /// NOT a candidate for shortening on its own. `clearContents()`
        /// invalidates the promised item and the synthesized ⌘V is only
        /// queued, so a target still reading when the restore lands reads
        /// the RESTORED PRIOR CLIPBOARD — the user's previous copy silently
        /// pasted into their document, while `wasAsked` never fires and
        /// The app tells them to press ⌘V for text that did paste. What
        /// changed is WHO waits (see `detachRestore`), not how long the
        /// clipboard is protected.
        public var restoreDelayMs: Double
        /// Run `restoreDelayMs` + the restore AFTER `insertMeasured`
        /// returns instead of before.
        ///
        /// 61,500 ms of the 66,089 ms of insertion measured across 144 real
        /// sessions was this sleep, all of it spent after the text was
        /// already in the user's document.
        ///
        /// Off puts both back on the critical path, which is the only way
        /// to get the OBSERVED clipboard verdict out of
        /// `InsertOutcome.success(clipboardByteIdentical:)`: detached, that
        /// verdict does not exist yet when the value is due, so the caller
        /// gets the hand-off prediction instead (see `insertMeasured`).
        /// A checker that asserts on it — `--dictation-e2e` requires
        /// byte-identical == successes — wants this off and should pay the
        /// half second.
        public var detachRestore: Bool

        /// 800 → 2000 (W-11, 2026-08-09): the wait EARLY-EXITS the moment
        /// the target asks for the pasteboard item, so a bigger cap costs
        /// the happy path nothing — it only delays how long a genuinely
        /// dead paste takes to report. Under load the old cap produced
        /// false failures: the W-12 investigation reproduced a paste
        /// landing late (a cold ~3.6 GB ASR worker loading beside it) and
        /// being reported as "text never reached the focused field" while
        /// the text then arrived. A wrongly-declared failure is also the
        /// clipboard-restore hazard window; rarer beats shorter.
        public init(pasteTimeoutMs: Double = 2000, restoreDelayMs: Double = 500,
                    detachRestore: Bool = true) {
            self.pasteTimeoutMs = pasteTimeoutMs
            self.restoreDelayMs = restoreDelayMs
            self.detachRestore = detachRestore
        }
    }

    public enum InsertOutcome: Sendable, Equatable {
        case success(clipboardByteIdentical: Bool)
        case failed(DictationInsertFailure)
    }

    /// Wall-clock breakdown of one `insert(_:)` call, in milliseconds.
    /// Added for `DictationSession`'s per-stage measurement — the task's
    /// "paste-latency is the one unmeasured term" finding — without
    /// changing `insert(_:)`'s existing signature/behavior at all: it is a
    /// pure by-product of the same single code path, exposed via
    /// `insertMeasured(_:)` below. `insert(_:)` itself still returns just
    /// `InsertOutcome`, byte-for-byte the same contract every existing
    /// caller (`--dictation-check`) already depends on.
    public struct InsertTiming: Sendable, Equatable {
        /// Public zero-value init: the controller self-check builds
        /// synthetic timings to drive the report wiring; the implicit
        /// memberwise init is module-internal, so without this the check
        /// could not construct one at all.
        public init() {}

        /// Gate checks (Secure Input + AX editability) — expected near-zero,
        /// but a real AX round trip to the window server, so worth keeping
        /// separate from "zero" rather than assumed.
        public var gateCheckMs: Double = 0
        /// The app that was frontmost when a gate REJECTED, or nil when the
        /// gates passed.
        ///
        /// Insertion succeeds 26 times in 57 across the 17 session reports on
        /// this machine, and **20 of the 31 failures die in the gate below**,
        /// before recognition quality is in play at all. It stayed
        /// undiagnosed for over a week because the gate classifies the
        /// rejection precisely into six cases and then records nothing about
        /// what it was pointed at.
        ///
        /// The session report's `perAppInsertion` looks like it covers this
        /// and does not: it attributes every attempt to whatever app was
        /// frontmost when dictation was ARMED
        /// (`DictationController.targetAppName`, set at arm time), which is a
        /// different moment from the one the gate looked at — the user can
        /// hold the hotkey, speak, and release with focus somewhere else. So
        /// a report reading "Slack: 7 attempted, 0 inserted" means only
        /// "7 dictations were armed while Slack was frontmost", and naming
        /// Slack as the app that failed was an inference nothing supported.
        public var gateRejectedApp: String? = nil
        /// Pasteboard write + synthesized ⌘V post — expected near-zero
        /// (no waiting), isolates "did posting the event itself block".
        public var synthesizeMs: Double = 0
        /// THE unmeasured number the task exists to produce: from posting
        /// ⌘V to either the target app's pasteboard-data-provider callback
        /// firing (`provider.wasAsked`) or the timeout. This is the real
        /// paste latency against whatever target is focused.
        public var waitForAskMs: Double = 0
        /// The deliberate, CONFIGURED (not measured) delay before restoring
        /// the clipboard — `config.restoreDelayMs`, default 500ms, and 0 on
        /// `.timedOut` (nothing read the item, so there is nothing to
        /// protect). Reported separately so it is never mistaken for
        /// measured latency: it is a fixed sleep, not something the target
        /// app's speed affects.
        ///
        /// When `restoreDetached` is true this is time the CLIPBOARD is
        /// protected, not time the user waits. It still lands in `totalMs`
        /// on purpose — see there — but it is then the CONFIGURED value
        /// exactly, where the inline version recorded the sleep's real
        /// duration. The few ms of scheduling overshoot that used to be
        /// visible in it are not measurable from the calling side any more,
        /// so a `totalMs` from a detached insert reads a little lower than
        /// one from an inline insert of identical cost.
        public var restoreDelayFixedMs: Double = 0
        /// The restore call itself (write prior pasteboard snapshot back +
        /// verify byte-identical) — expected near-zero.
        ///
        /// **Zero, not "instant", when `restoreDetached` is true**: the
        /// call happens after this struct has already been handed back, so
        /// there is no number to put here. Read `restoreDetached` before
        /// reading this field, and do not average the two populations
        /// together.
        public var restoreMs: Double = 0
        /// Whether the delay + restore ran OFF the critical path.
        ///
        /// Defaults to false so a synthetic `InsertTiming()` built by a
        /// self-check still describes the all-inline shape it was written
        /// against; `insertMeasured` sets it explicitly.
        public var restoreDetached: Bool = false
        /// Time spent waiting for the PREVIOUS insert's detached restore to
        /// finish before this one was allowed to snapshot the clipboard.
        /// See `PendingClipboardRestore`. Zero on any dictation whose ASR
        /// round trip (2774 ms median over the 144 measured sessions)
        /// already outlasted the window — which is the ordinary path.
        public var waitedForPriorRestoreMs: Double = 0
        /// Sum of the same five fields it has always summed — gate,
        /// synthesize, wait-for-ask, the fixed delay, restore.
        ///
        /// It is NOT the caller's wait any more; `blockingMs` is. The
        /// arithmetic is deliberately unchanged so the 144 sessions of
        /// history this number already has keep meaning exactly what they
        /// meant, and so a report that pools old and new records is not
        /// silently comparing two different quantities. `insertMs` in the
        /// session report is this.
        ///
        /// `waitedForPriorRestoreMs` is deliberately NOT added here, for
        /// the same reason: it is a term that did not exist when the
        /// history was recorded. It is counted in `blockingMs`, so
        /// `blockingMs` is not a subset of `totalMs`.
        public var totalMs: Double {
            gateCheckMs + synthesizeMs + waitForAskMs + restoreDelayFixedMs + restoreMs
        }
        /// What the caller ACTUALLY waited for — the number the user feels.
        /// Everything the insert did before returning, including a wait on
        /// a prior restore, and including the delay + restore only when
        /// they were not detached.
        public var blockingMs: Double {
            gateCheckMs + waitedForPriorRestoreMs + synthesizeMs + waitForAskMs
                + restoreMs + (restoreDetached ? 0 : restoreDelayFixedMs)
        }
    }

    private let config: Config
    /// Resolves "which app am I typing into right now". Injectable ONLY so
    /// the undo gate's self-check can drive both the same-app and
    /// different-app branches without a second running application; product
    /// code always uses the default, which is the same resolver
    /// `DictationSession` already targets formatting with.
    private let frontmostBundleID: @Sendable () -> String?
    /// Whether the focused field will accept synthesized keystrokes at all.
    /// Injectable for the same single reason `frontmostBundleID` is: the
    /// self-check has to drive the BLOCKED branch, and staging a real Secure
    /// Input holder from a test is not something to attempt.
    private let fieldGate: @Sendable () -> UndoFieldGate

    public init(
        config: Config = Config(),
        frontmostBundleID: @escaping @Sendable () -> String? = {
            FrontmostApplicationResolver.resolve().bundleID
        },
        fieldGate: @escaping @Sendable () -> UndoFieldGate = { TextInserter.liveFieldGate() }
    ) {
        self.config = config
        self.frontmostBundleID = frontmostBundleID
        self.fieldGate = fieldGate
    }

    @MainActor
    public func insert(_ text: String) async -> InsertOutcome {
        await insertMeasured(text).outcome
    }

    /// Same pipeline as `insert(_:)` — this IS `insert(_:)`'s implementation,
    /// not a parallel copy — with wall-clock timestamps taken at each stage
    /// boundary. See `InsertTiming`'s field docs for what each number means.
    @MainActor
    public func insertMeasured(_ text: String) async -> (outcome: InsertOutcome, timing: InsertTiming) {
        var timing = InsertTiming()
        let tGateStart = DispatchTime.now()

        // Resolved HERE — before the gates rather than after them, and still
        // before the paste. This is the app that is about to receive the text,
        // and it is the only app the voice-delete undo is ever allowed to
        // backspace into. Resolving it afterwards would name whatever the user
        // had switched to by then. One bounded AX round trip (0.3 s worst
        // case, ~1 ms typical), and its cost lands in `gateCheckMs` where it
        // belongs rather than inflating the paste-latency number this struct
        // exists to measure.
        //
        // It moved AHEAD of the gates so a REJECTION can name its target too.
        // Every early return below used to discard this, so the 20-of-31
        // failures that die in the gate recorded nothing about what they were
        // pointed at, and the only app name reaching the session report came
        // from arm time — a different moment. See `gateRejectedApp`.
        let targetBundleID = frontmostBundleID()

        func rejected(_ failure: DictationInsertFailure) -> (InsertOutcome, InsertTiming) {
            timing.gateCheckMs = millis(tGateStart, DispatchTime.now())
            timing.gateRejectedApp = targetBundleID
            // stderr, matching `DictationAsrTransport`, so it lands in
            // the app log beside `[asr] whisper_mac:` —
            // the only place a failure that never reaches a session report
            // can still be read back afterwards.
            FileHandle.standardError.write(Data((
                "[dictation] insert gate rejected: \(failure) "
                + "target=\(targetBundleID ?? "(unresolved)")\n"
            ).utf8))
            return (.failed(failure), timing)
        }

        if let secureInputFailure = Self.secureInputGate() {
            return rejected(secureInputFailure)
        }
        switch FocusEditabilityChecker.check() {
        case .permissionMissing:
            return rejected(.accessibilityPermissionMissing)
        case .secureField:
            // Refuse, and refuse LOUDLY enough that the caller cannot mistake
            // this for the ordinary not-editable case, which falls through to
            // the clipboard fallback and to history. Neither the text nor the
            // recording may be kept.
            return rejected(.secureFieldFocused)
        case .notEditable:
            // AX gave a DEFINITE answer: an element is focused and it does not
            // take text. Refusing is right — pasting would go somewhere the
            // user did not point at.
            return rejected(.focusedElementNotEditable)
        case .noFocusedElement:
            // AX gave a NON-answer, and that is not the same thing.
            //
            // "I cannot see a focused element" from an app that does not
            // publish one is not evidence that there is nowhere to paste.
            // WeChat (`com.tencent.xinWeChat`) is the measured case: this gate
            // rejected it three times on 2026-08-17, the text fell to the
            // clipboard, and the user then pressed ⌘V by hand into the very
            // field the gate had just called nonexistent. It was the largest
            // single complaint about this product — "in many places I have to
            // press Command-V myself".
            //
            // So do not decide it here. This path already carries a STRONGER,
            // post-hoc detector: the delayed pasteboard provider records
            // whether the target app actually asked for the data, and
            // `.timedOut/.pending` becomes `pasteNeverRequested`. That is an
            // observation of what happened; the AX pre-check is a prediction,
            // and on this class of app it is a wrong one. Try the paste and
            // let the observation arbitrate.
            //
            // Cost when there genuinely is no target: one synthesized ⌘V that
            // does nothing, plus `config.pasteTimeoutMs` before we report the
            // same failure we would have reported instantly. The clipboard
            // fallback downstream is unchanged, so the worst case is slower,
            // not worse. That is the right trade against silently refusing to
            // type into a working text field.
            FileHandle.standardError.write(Data((
                "[dictation] insert gate: no AX focused element on "
                + "\(targetBundleID ?? "(unresolved)") — pasting anyway, the "
                + "provider callback decides\n"
            ).utf8))
        case .editable: break
        }
        timing.gateCheckMs = millis(tGateStart, DispatchTime.now())

        // Wait out any restore the PREVIOUS insert left running before
        // looking at the clipboard. Snapshotting while the app's own dictated
        // text is still sitting there adopts it as "the user's prior
        // clipboard" and writes it back when this insert's window closes;
        // the earlier insert's restore then sees a moved changeCount, skips,
        // and the real prior clipboard is gone for good — replaced by the
        // last thing the user dictated. The ASR round trip keeps ordinary
        // dictations far outside the window, but a double trigger and the
        // dictate/"scratch that"/re-dictate cadence are not bounded by it.
        let tPendingStart = DispatchTime.now()
        await PendingClipboardRestore.shared.settle()
        timing.waitedForPriorRestoreMs = millis(tPendingStart, DispatchTime.now())

        if PendingClipboardRestore.shared.beginCriticalSection() {
            FileHandle.standardError.write(Data((
                "[dictation] insert entered the clipboard-critical section "
                + "while another insert was still in it — the snapshot may "
                + "hold this app's own text, target=\(targetBundleID ?? "(unresolved)")\n"
            ).utf8))
        }

        let pasteboard = NSPasteboard.general
        let savedSnapshot = PasteboardSnapshot.capture(pasteboard)

        let tSynthStart = DispatchTime.now()
        let provider = DelayedClipboardProvider(text: text)
        let item = NSPasteboardItem()
        item.setDataProvider(provider, forTypes: [.string, concealedPasteboardType])
        pasteboard.clearContents()
        pasteboard.writeObjects([item])
        let changeCountAfterOurWrite = pasteboard.changeCount

        guard synthesizeCommandV(keyCode: KeyCodeResolver.resolveVKeyCode()) else {
            timing.synthesizeMs = millis(tSynthStart, DispatchTime.now())
            // Still attempt to restore the clipboard — we already wrote to
            // it even though the keystroke failed to post.
            let tRestoreStart = DispatchTime.now()
            restoreIfSafe(pasteboard, savedSnapshot: savedSnapshot, changeCountAfterOurWrite: changeCountAfterOurWrite)
            timing.restoreMs = millis(tRestoreStart, DispatchTime.now())
            // No delay on this path and none needed: nothing was pasted, so
            // no target is mid-read. Restored inline, so the section closes
            // here rather than in a scheduled task.
            PendingClipboardRestore.shared.endCriticalSection()
            return (.failed(.keyEventPostFailed), timing)
        }
        timing.synthesizeMs = millis(tSynthStart, DispatchTime.now())

        let tWaitStart = DispatchTime.now()
        let outcome = await waitForPasteOutcome(provider: provider, timeoutMs: config.pasteTimeoutMs)
        timing.waitForAskMs = millis(tWaitStart, DispatchTime.now())

        // The restore delay (`Config.restoreDelayMs`) gives the target's
        // paste handler time to finish reading the delayed item before we
        // hand the clipboard back. Reported as `restoreDelayFixedMs`, not
        // folded into `waitForAskMs`, since it is a configured sleep, not
        // something the target app's own speed drives.
        //
        // Skipped entirely on `.timedOut`: the provider was never asked, so
        // there is no half-finished read to protect.
        //
        // BUT ONLY WHEN THE CALLER IS WAITING FOR IT. `wasAsked == false` at
        // 2000 ms does not mean the target will never read — it means the
        // target is SLOWER than 2000 ms, which is exactly the population that
        // needs the grace window most. Zeroing the delay there takes the
        // protection away from the slowest targets and hands them the failure
        // it exists to prevent: the pasteboard is cleared and rewritten within
        // a few ms of the timeout, so a read at t=2100 ms finds the user's
        // PRIOR clipboard and pastes that into their document, while the app
        // reports `.pasteNeverRequested` and tells them to press Cmd-V.
        //
        // Detached, the delay costs the caller nothing — that is the whole
        // point of detaching — so there is nothing to buy by skipping it.
        // Skip it only on the inline path, where the caller really is paying
        // for a window nobody has asked to use.
        //
        // This also compounds with the `_wasAsked` tightening: inserts that a
        // ConcealedType-only sniff used to mis-promote to `.requested` now
        // land in `.timedOut` correctly, and would have lost the window too.
        let delayMs: Double = config.detachRestore
            ? config.restoreDelayMs
            : ((outcome == .requested) ? config.restoreDelayMs : 0)

        let clipboardByteIdentical: Bool
        if config.detachRestore {
            timing.restoreDelayFixedMs = delayMs
            timing.restoreDetached = true
            // A PREDICTION, and the only honest one available: the observed
            // verdict does not exist until the window closes, and
            // `InsertOutcome.success(clipboardByteIdentical:)` is due now.
            // It still catches the case that actually fails — a third-party
            // write during the paste wait, which makes the restore skip.
            // A write landing inside the detached window flips it, and that
            // disagreement is written to the app log by `scheduleRestore`
            // rather than left to differ from the report in silence.
            clipboardByteIdentical = ClipboardRestorePolicy.decide(
                changeCountAfterOurWrite: changeCountAfterOurWrite,
                changeCountNow: pasteboard.changeCount) == .restore
            scheduleRestore(
                savedSnapshot: savedSnapshot,
                changeCountAfterOurWrite: changeCountAfterOurWrite,
                delayMs: delayMs,
                reported: clipboardByteIdentical)
        } else {
            let tDelayStart = DispatchTime.now()
            if delayMs > 0 {
                try? await Task.sleep(nanoseconds: UInt64(delayMs * 1_000_000))
            }
            timing.restoreDelayFixedMs = millis(tDelayStart, DispatchTime.now())

            let tRestoreStart = DispatchTime.now()
            clipboardByteIdentical = restoreIfSafe(
                pasteboard, savedSnapshot: savedSnapshot, changeCountAfterOurWrite: changeCountAfterOurWrite)
            timing.restoreMs = millis(tRestoreStart, DispatchTime.now())
            PendingClipboardRestore.shared.endCriticalSection()
        }

        switch outcome {
        case .requested:
            // The receipt voice-delete undoes against. Recorded ONLY on a
            // confirmed paste (the target app actually asked for our
            // pasteboard data) — a paste that timed out may have landed
            // nothing, and backspacing over text that was never typed is
            // the exact failure this feature must never produce.
            LastInsertionRegistry.shared.record(
                LastInsertion(text: text, bundleID: targetBundleID, at: Date()))
            return (.success(clipboardByteIdentical: clipboardByteIdentical), timing)
        case .timedOut, .pending:
            return (.failed(.pasteNeverRequested), timing)
        }
    }

    // MARK: - Clipboard fallback

    /// Returns once no detached clipboard restore is outstanding, so an app
    /// about to quit can let the user's previous clipboard come back first.
    public static func settlePendingClipboardRestore() async {
        await PendingClipboardRestore.shared.settle()
    }

    /// Leaves `text` on the clipboard so a refused paste costs one ⌘V
    /// instead of the utterance. Returns whether it landed.
    ///
    /// A failed insert RESTORES the user's previous clipboard, so without
    /// this the words are gone. Waits for any detached restore first, so
    /// that restore cannot land on top of the text a moment later.
    ///
    /// Tagged `org.nspasteboard.ConcealedType` like the paste itself: this
    /// item stays until the user copies something else, and untagged it
    /// would be harvested into every clipboard manager's history. The tag is
    /// advisory, which is why a password field or Secure Input must never
    /// come here — the caller refuses those outright.
    ///
    /// Preserves the prior clipboard around a delayed paste.
    @MainActor
    public static func preserveOnClipboard(
        _ text: String, pasteboard: NSPasteboard = .general
    ) async -> Bool {
        guard !text.isEmpty else { return false }
        await PendingClipboardRestore.shared.settle()
        pasteboard.clearContents()
        // Both types declared up front: `setData(forType:)` for an
        // undeclared type is refused without an error.
        pasteboard.declareTypes([.string, concealedPasteboardType], owner: nil)
        let landed = pasteboard.setString(text, forType: .string)
        pasteboard.setData(Data(), forType: concealedPasteboardType)
        return landed
    }

    // MARK: - Voice delete (undo the last insertion)

    /// What the app last typed, where, and when — the entire state voice-delete
    /// operates on. Text only lives here in memory; it is never persisted by
    /// this type (dictation history is the persisted copy, and it is the
    /// one with the privacy rules).
    public struct LastInsertion: Sendable, Equatable {
        public let text: String
        /// The app that received it. `nil` when the resolver could not name
        /// one — which the gate below treats as "refuse", not "allow".
        public let bundleID: String?
        public let at: Date

        public init(text: String, bundleID: String?, at: Date) {
            self.text = text
            self.bundleID = bundleID
            self.at = at
        }
    }

    /// Process-wide, deliberately.
    ///
    /// "The last thing the app typed" is a fact about the process, not about a
    /// particular `TextInserter` instance — and the product path DOES build
    /// more than one instance over a run (`ProductDictationPipeline`
    /// rebuilds its `DictationSession`, and with it a fresh inserter, every
    /// time the formatting setting resolves differently). Hanging the
    /// record off the instance would make "scratch that" silently do nothing
    /// after an unrelated settings change, which is precisely the class of
    /// quiet, unexplainable no-op this surface exists to eliminate.
    public final class LastInsertionRegistry: @unchecked Sendable {
        public static let shared = LastInsertionRegistry()

        private let lock = NSLock()
        private var value: LastInsertion?

        public func record(_ insertion: LastInsertion) {
            lock.lock(); value = insertion; lock.unlock()
        }

        public func current() -> LastInsertion? {
            lock.lock(); defer { lock.unlock() }
            return value
        }

        /// Called after a successful undo, and by `clear()` in the
        /// self-check. THE guardrail behind "never delete more than the last
        /// insertion": once consumed, a second "scratch that" has nothing to
        /// act on and says so, instead of walking backwards through the
        /// user's earlier text.
        public func clear() {
            lock.lock(); value = nil; lock.unlock()
        }
    }

    /// What the focused field says about accepting keystrokes RIGHT NOW.
    ///
    /// This exists because `undoLastInsertion` shipped without it for one
    /// review cycle, and the hole was exact and reachable: dictate into a
    /// normal field, click into a PASSWORD FIELD IN THE SAME APP within two
    /// minutes, say "scratch that" — the bundle-id guard passes (same app),
    /// the age guard passes, and the app posts N backspaces into the password
    /// field while the HUD reports a clean delete. `insert(_:)` has always
    /// gated on Secure Input and AX editability before writing; the undo
    /// path now runs the SAME two gates, because pressing ⌫ in a field is
    /// no less a write than pasting into it.
    public enum UndoFieldGate: Sendable, Equatable {
        case ok
        /// Reuses `DictationInsertFailure` rather than inventing a parallel
        /// vocabulary, so "why can't the app type here" has one answer set for
        /// both directions.
        case blocked(DictationInsertFailure)
    }

    /// The product gate: Secure Input first (cheap, and the case that
    /// matters), then the same AX editability check the paste path runs.
    /// Both are the identical calls `insertMeasured` makes — see
    /// `secureInputGate()` and `FocusEditabilityChecker`.
    public static func liveFieldGate() -> UndoFieldGate {
        if let secureInputFailure = secureInputGate() { return .blocked(secureInputFailure) }
        switch FocusEditabilityChecker.check() {
        case .permissionMissing: return .blocked(.accessibilityPermissionMissing)
        case .noFocusedElement: return .blocked(.noFocusedElement)
        case .notEditable: return .blocked(.focusedElementNotEditable)
        case .secureField: return .blocked(.secureFieldFocused)
        case .editable: return .ok
        }
    }

    /// Privacy gate for the START of a dictation, before capture or a live
    /// preview sees any audio.
    ///
    /// This is intentionally narrower than ``liveFieldGate()``. Several real
    /// paste targets (notably WeChat) do not expose a focused AX element even
    /// though Command-V works, so `noFocusedElement`, `notEditable`, and a
    /// missing Accessibility grant are not evidence that recording speech is
    /// unsafe. Secure Input and an `AXSecureTextField` are: the product refuses
    /// those at press time, rather than recording/transcribing first and only
    /// discovering the password field at insertion time.
    public static func dictationStartPrivacyGate() -> DictationInsertFailure? {
        dictationStartPrivacyDecision(
            secureInputFailure: secureInputGate(),
            focus: FocusEditabilityChecker.check())
    }

    /// Pure half of ``dictationStartPrivacyGate()``. Public so a TCC-free
    /// test can pin both sides of the boundary: secure fields stop before
    /// capture, while incomplete AX metadata must not break ordinary apps.
    public static func dictationStartPrivacyDecision(
        secureInputFailure: DictationInsertFailure?,
        focus: FocusCheckResult
    ) -> DictationInsertFailure? {
        if let secureInputFailure { return secureInputFailure }
        if case .secureField = focus { return .secureFieldFocused }
        return nil
    }

    /// Why an undo was refused, or what it removed.
    public enum UndoOutcome: Sendable, Equatable {
        /// `text` is what was removed — handed back so the caller can flag
        /// the matching history entry rather than guessing which one it was.
        case deleted(text: String, backspaces: Int)
        /// Nothing has been inserted this run (or the last one was already
        /// undone).
        case nothingRecorded
        /// The insertion is older than `maxUndoAge`. See that constant.
        case expired(ageSeconds: Double)
        /// The user has moved to another app. NEVER backspace here.
        case differentApp(recorded: String?, current: String?)
        /// The focused field will not take keystrokes — Secure Input is
        /// active (a password field), the element is not editable, nothing is
        /// focused, or Accessibility permission is gone. See `UndoFieldGate`.
        case blocked(DictationInsertFailure)
        /// The record is longer than `DictationUndoPlan.maxBackspaces`.
        case tooLong(characters: Int)
        /// `CGEvent` could not be constructed/posted — same class of failure
        /// as `keyEventPostFailed` on the insert path.
        case keyEventPostFailed
    }

    /// How stale a last-insertion record may be and still be undoable.
    ///
    /// Two minutes is long enough to cover "read what it typed, decide it's
    /// wrong, say scratch that" including a phone-call-length pause, and
    /// short enough that a record left over from before lunch cannot eat a
    /// paragraph the user has since written by hand in the same field.
    public static let maxUndoAge: TimeInterval = 120

    /// The whole gate, as a pure function — no AX, no events, no clock of
    /// its own. Everything that decides whether the app is allowed to press
    /// backspace lives here so the self-check can exercise every refusal
    /// branch, including the ones that are hard to stage live (a two-minute
    /// wait, a second app).
    public static func decideUndo(
        record: LastInsertion?, now: Date, currentBundleID: String?,
        fieldGate: UndoFieldGate = .ok
    ) -> UndoOutcome {
        guard let record else { return .nothingRecorded }
        // Ordered AFTER the record check so a fresh session says "nothing to
        // delete" rather than blaming the field, and BEFORE everything else
        // because it is the safety gate: no later branch may reach
        // `sendBackspaces` without having passed it.
        if case .blocked(let failure) = fieldGate { return .blocked(failure) }
        let age = now.timeIntervalSince(record.at)
        if age > maxUndoAge || age < 0 { return .expired(ageSeconds: age) }
        // An unknown app on EITHER side is a refusal, not a pass. "We could
        // not tell which app this is" must never resolve to "so go ahead and
        // delete in it."
        guard let recorded = record.bundleID, let current = currentBundleID, recorded == current else {
            return .differentApp(recorded: record.bundleID, current: currentBundleID)
        }
        let backspaces = DictationUndoPlan.backspaceCount(for: record.text)
        guard DictationUndoPlan.isWithinBound(backspaces) else {
            return .tooLong(characters: backspaces)
        }
        return .deleted(text: record.text, backspaces: backspaces)
    }

    /// Removes the text the app last inserted, by synthesizing one ⌫ per
    /// grapheme cluster into the SAME app that received it.
    ///
    /// Gated exactly as `insert(_:)` is — Secure Input, then AX editability
    /// (see `UndoFieldGate`) — because pressing ⌫ in a field is no less a
    /// write than pasting into it.
    ///
    /// See `DictationUndoPlan`'s doc comment for the honest limits of
    /// counting characters (terminal-reliable, rich-editor best-effort).
    /// Nothing is destroyed regardless: the caller keeps the text in
    /// dictation history, flagged and restorable.
    @MainActor
    @discardableResult
    public func undoLastInsertion(now: Date = Date()) async -> UndoOutcome {
        let decision = Self.decideUndo(
            record: LastInsertionRegistry.shared.current(),
            now: now,
            currentBundleID: frontmostBundleID(),
            fieldGate: fieldGate())
        guard case .deleted(let text, let backspaces) = decision else { return decision }
        guard await Self.sendBackspaces(count: backspaces) else { return .keyEventPostFailed }
        // Consumed: a second command has nothing left to delete.
        LastInsertionRegistry.shared.clear()
        return .deleted(text: text, backspaces: backspaces)
    }

    /// `kVK_Delete` — the ⌫ key, not the forward-delete key.
    private static let backspaceKeyCode: CGKeyCode = 51

    /// Posts `count` ⌫ down/up pairs, paced.
    ///
    /// The 0.5 ms gap is not superstition: a burst of synthesized key events
    /// posted with no spacing can outrun a target app's own event handling
    /// (Electron/terminal emulators coalesce or drop), and a DROPPED
    /// backspace leaves residue on screen. At the length of a real utterance
    /// (~50 characters) the whole undo still finishes in well under 50 ms,
    /// and the bound at `DictationUndoPlan.maxBackspaces` caps the worst
    /// case at ~1 s.
    private static func sendBackspaces(count: Int) async -> Bool {
        guard let source = CGEventSource(stateID: .hidSystemState) else { return false }
        for _ in 0..<count {
            guard let down = CGEvent(keyboardEventSource: source, virtualKey: backspaceKeyCode, keyDown: true),
                  let up = CGEvent(keyboardEventSource: source, virtualKey: backspaceKeyCode, keyDown: false)
            else { return false }
            down.post(tap: .cgSessionEventTap)
            up.post(tap: .cgSessionEventTap)
            try? await Task.sleep(nanoseconds: 500_000)
        }
        return true
    }

    /// Runs the protection window and the restore AFTER `insertMeasured`
    /// has already returned — fired, never awaited, the same shape
    /// `DictationSession.beginCorrectionObservationFireAndForget` uses for
    /// the other piece of post-paste work in this module.
    ///
    /// Ordering is not optional here, which is why it goes through
    /// `PendingClipboardRestore` rather than a bare `Task.detached`: two
    /// restores interleaving their `clearContents()` + `writeObjects` can
    /// leave a pasteboard holding neither snapshot.
    private func scheduleRestore(
        savedSnapshot: PasteboardSnapshot,
        changeCountAfterOurWrite: Int,
        delayMs: Double,
        reported: Bool
    ) {
        PendingClipboardRestore.shared.enqueue { [self] in
            if delayMs > 0 {
                try? await Task.sleep(nanoseconds: UInt64(delayMs * 1_000_000))
            }
            // Back on the main actor for the AppKit calls — the whole point
            // was to stop the CALLER waiting, not to start touching
            // `NSPasteboard` from an arbitrary thread.
            let observed = await MainActor.run {
                restoreIfSafe(
                    NSPasteboard.general,
                    savedSnapshot: savedSnapshot,
                    changeCountAfterOurWrite: changeCountAfterOurWrite)
            }
            PendingClipboardRestore.shared.endCriticalSection()
            guard observed != reported else { return }
            // The value already reported to the caller is now known to be
            // wrong. stderr, matching the gate-rejection line above, so
            // The app log carries the correction the session report cannot.
            FileHandle.standardError.write(Data((
                "[dictation] clipboard restore: observed=\(observed) "
                + "reported=\(reported) — the pasteboard changed inside the "
                + "\(Int(delayMs)) ms protection window\n"
            ).utf8))
        }
    }

    private func millis(_ a: DispatchTime, _ b: DispatchTime) -> Double {
        Double(b.uptimeNanoseconds - a.uptimeNanoseconds) / 1_000_000
    }

    /// Returns `true` iff the clipboard was restored AND came back
    /// byte-identical to `savedSnapshot`. Returns `false` (without
    /// mutating the pasteboard) when a third-party write was detected —
    /// see `ClipboardRestorePolicy`.
    @discardableResult
    private func restoreIfSafe(
        _ pasteboard: NSPasteboard, savedSnapshot: PasteboardSnapshot, changeCountAfterOurWrite: Int
    ) -> Bool {
        let decision = ClipboardRestorePolicy.decide(
            changeCountAfterOurWrite: changeCountAfterOurWrite,
            changeCountNow: pasteboard.changeCount)
        guard decision == .restore else { return false }
        savedSnapshot.restore(to: pasteboard)
        return PasteboardSnapshot.capture(pasteboard) == savedSnapshot
    }

    private func synthesizeCommandV(keyCode: CGKeyCode) -> Bool {
        guard let source = CGEventSource(stateID: .hidSystemState) else { return false }
        guard let keyDown = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: true),
              let keyUp = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false)
        else { return false }
        keyDown.flags = .maskCommand
        keyUp.flags = .maskCommand
        keyDown.post(tap: .cgSessionEventTap)
        keyUp.post(tap: .cgSessionEventTap)
        return true
    }

    private func waitForPasteOutcome(provider: DelayedClipboardProvider, timeoutMs: Double) async -> PasteOutcome {
        let start = Date()
        while true {
            if provider.wasAsked { return .requested }
            let elapsedMs = Date().timeIntervalSince(start) * 1000
            let classification = PasteTimeoutClassifier.classify(
                wasAsked: provider.wasAsked, elapsedMs: elapsedMs, timeoutMs: timeoutMs)
            if classification == .timedOut { return .timedOut }
            // 10ms poll — fine-grained enough not to blow the timeout
            // budget on the last iteration, cheap enough not to matter.
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
    }

    /// Secure Input never has a "held by self" failure — this app has no
    /// password-field-protection feature of its own, so `.activeHeldBySelf`
    /// would mean OUR OWN process asserted Secure Input somewhere else in
    /// the app, which is still a real block on the paste path and must fail
    /// with an explanation, not silently proceed.
    ///
    /// Module-internal rather than private: `DictationCorrectionObserver`
    /// refuses to start watching a field while Secure Input is on.
    static func secureInputGate() -> DictationInsertFailure? {
        switch SecureInputMonitor.currentStatus() {
        case .inactive: return nil
        case .activeHeldByPID: return .secureInputActive(heldByOtherProcess: true)
        case .activeHeldBySelf: return .secureInputActive(heldByOtherProcess: false)
        case .activeUnknownHolder: return .secureInputActive(heldByOtherProcess: true)
        }
    }
}

/// The one place an in-flight detached clipboard restore is tracked, so the
/// next insert can wait for it instead of snapshotting on top of it.
///
/// Process-wide for the same reason `TextInserter.LastInsertionRegistry`
/// is: `ProductDictationPipeline` rebuilds its `DictationSession`, and with
/// it a fresh inserter, whenever the formatting setting resolves
/// differently — a token hanging off the instance would not see the restore
/// the previous instance left running, which is exactly the case it exists
/// to catch.
final class PendingClipboardRestore: @unchecked Sendable {
    static let shared = PendingClipboardRestore()

    private let lock = NSLock()
    /// The tail of a chain, not a set. Restores must not overlap each other
    /// either, and awaiting the tail awaits everything queued behind it.
    /// The finished handle stays retained until the next insert replaces
    /// it — one `Task`, deliberately, rather than a completion callback
    /// that would have to take this lock again from inside the task.
    private var tail: Task<Void, Never>?
    /// Inserts currently between "snapshot the clipboard" and "restore
    /// finished". Never more than 1 unless something upstream broke — see
    /// `beginCriticalSection`.
    private var inFlightInserts = 0

    /// Returns once nothing is outstanding. Free when nothing is.
    func settle() async {
        await currentTail()?.value
    }

    /// Split out because `NSLock.lock()` is unavailable from an async
    /// context — the read has to finish inside a synchronous frame before
    /// `settle()` suspends on the handle.
    private func currentTail() -> Task<Void, Never>? {
        lock.lock()
        defer { lock.unlock() }
        return tail
    }

    /// Enter the clipboard-critical section — snapshot, write, paste,
    /// restore. Returns `true` if ANOTHER insert was already inside it.
    ///
    /// `settle()` serializes an insert against the previous insert's
    /// RESTORE, which is the case detaching creates. It cannot serialize
    /// against a previous insert that is still mid-paste, because that one
    /// has not scheduled its restore yet — an overlap there would snapshot
    /// the app's own text just the same. `DictationSessionPhase` forbids that
    /// overlap inside a session ("triggerUp() racing a still-in-flight
    /// prior triggerUp()"), and a mutex spanning the whole paste wait would
    /// buy the fix at the price of a way to wedge dictation permanently.
    /// So this counts instead of blocking: if the forbidden overlap ever
    /// does happen, the app log says so and the guess stops being a guess.
    func beginCriticalSection() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        inFlightInserts += 1
        return inFlightInserts > 1
    }

    func endCriticalSection() {
        lock.lock()
        inFlightInserts = max(0, inFlightInserts - 1)
        lock.unlock()
    }

    /// Chains `work` behind anything already queued and returns at once.
    func enqueue(_ work: @escaping @Sendable () async -> Void) {
        lock.lock()
        let previous = tail
        tail = Task.detached {
            await previous?.value
            await work()
        }
        lock.unlock()
    }
}
