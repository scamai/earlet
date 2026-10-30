import AppKit
import ApplicationServices
import Foundation
import DictationAudioCapture

/// The orchestrator: assembles every M1 piece that until now has only been
/// measured SEPARATELY (Iteration 13) —
/// `DictationHotkey`'s trigger events -> `DictationCapture`'s warm capture ->
/// `DictationAsrTransport`'s ASR round trip -> `TextInserter`'s paste path —
/// into one real end-to-end pipeline.
///
/// `DictationSession` itself does NOT touch `DictationHotkey` — callers wire
/// `onPushToTalkStart`/`onPushToTalkEnd` (or the double-tap pair) to
/// `triggerDown()`/`triggerUp()` themselves, exactly as `DictationHotkeyEvent`
/// already documents (the app controller will start/stop
/// capture off them"). Keeping the hotkey out of this type is deliberate:
/// `DictationHotkey` needs Accessibility+Input Monitoring TCC and a real
/// `CGEventTap`, neither of which this type or its self-check should need.
///
/// ## Degrade honestly, every stage
///
/// The task brief calls out a repeated failure mode in this project: silent
/// failure. Every stage here returns a classified failure rather than an
/// empty string or a swallowed error — see `DictationSession.SessionFailure`.
public final class DictationSession: @unchecked Sendable {

    // MARK: - Timings

    /// Per-stage wall-clock milliseconds for ONE dictation. Every field is
    /// `nil` until that stage actually ran — a session that fails at ASR
    /// never has `pasteTiming` populated, and the report must say so rather
    /// than print a misleading 0.
    public struct StageTimings: Sendable, Equatable {
        /// Public all-nil init: the controller self-check builds
        /// synthetic stage timings to drive the report wiring; the implicit
        /// memberwise init is module-internal.
        public init() {}

        /// `triggerDown()`: lock + array-copy cost of `armTriggerDown()`
        /// (warm path) or `AVAudioEngine.start()` (cold path, see
        /// `usedWarmCapture`).
        public var armMs: Double?
        /// `triggerUp()`'s capture half: from the call into
        /// `DictationCapture.stop()` to the utterance buffer being ready.
        public var captureStopMs: Double?
        /// Buffer-ready -> ASR text received, real deployed worker.
        public var asrMs: Double?
        /// The worker's own account of `asrMs`: engine wait, decode, windows,
        /// retries and warmth. Counts only.
        public var decodeStats: DictationDecodeStats?
        /// Counts at the worker-response and post-formatting boundaries, not
        /// transcript content or proof of what the destination field displayed.
        public var recognizedTextShape: DictationTextShape?
        public var insertionTextShape: DictationTextShape?
        public var formattingChanged: Bool?
        /// Why the worker returned no words, when it said
        /// (`DictationAsrDetail.dropReason`).
        public var dropReason: DictationAsrDropReason?
        /// The clip's peak amplitude (0…1), measured only when audible
        /// audio came back with no words: how loud the take was decides
        /// whether it is kept for Recover Last Dictation.
        public var capturePeak: Float?
        /// `TextInserter.insertMeasured(_:)`'s full breakdown — see
        /// `TextInserter.InsertTiming`'s field docs. `waitForAskMs` inside
        /// this is the specific number the task calls "the one unmeasured
        /// term in the whole budget."
        public var pasteTiming: TextInserter.InsertTiming?
        /// Whether `armTriggerDown()` (warm) or `start()` (cold) was used
        /// this session — changes what `armMs` actually measures.
        public var usedWarmCapture: Bool = false

        /// `captureStopMs + asrMs` — the "release-to-text"
        /// exit metric (text available to insert, not yet inserted). `nil`
        /// if either half didn't complete.
        public var releaseToTextMs: Double? {
            guard let captureStopMs, let asrMs else { return nil }
            return captureStopMs + asrMs
        }
        /// `releaseToTextMs + pasteTiming.totalMs` — release to the text
        /// actually landing (or a classified paste failure) in the target.
        public var releaseToInsertedMs: Double? {
            guard let releaseToTextMs, let pasteTiming else { return nil }
            return releaseToTextMs + pasteTiming.totalMs
        }
    }

    // MARK: - Failure classification

    /// Which stage a failure happened in — lets a caller (or this task's
    /// measurement harness) report separate ASR and paste failure counts
    /// instead of one undifferentiated failure count.
    public enum Stage: Sendable, Equatable {
        case capture
        case asr
        case paste
    }

    public enum SessionFailure: Error, Sendable, Equatable, CustomStringConvertible {
        case micPermissionDenied
        /// No samples, only exact digital zeros from the input device, or a
        /// hold of `silentInputMinimumHoldSeconds` or more whose audio never
        /// reached `silentInputPeak` (`isSilentInput`).
        case emptyCapture
        case asr(DictationAsrFailure)
        case emptyTranscript
        case insert(DictationInsertFailure)
        /// The caller drove the state machine out of order (e.g. `triggerUp()`
        /// with no preceding `triggerDown()`) — a programmer error in the
        /// wiring, not a runtime condition, but still reported rather than
        /// crashing or silently no-op'ing.
        case invalidSequencing(DictationSessionPhase.RejectionReason)

        public var description: String {
            switch self {
            case .micPermissionDenied:
                return "microphone permission not granted"
            case .emptyCapture:
                return "captured no usable audio"
            case .asr(let f):
                return "ASR failed: \(f)"
            case .emptyTranscript:
                return "ASR returned an empty transcript (nothing to insert)"
            case .insert(let f):
                return "paste failed: \(f)"
            case .invalidSequencing(let reason):
                return "invalid trigger sequencing: \(reason)"
            }
        }
    }

    /// A spoken (or gestured) instruction to the app itself, rather than text
    /// to type. Exactly one member today; it is an enum so the seam does not
    /// have to be re-cut when a second command earns its place.
    public enum VoiceCommand: Sendable, Equatable {
        /// "Scratch that" — remove the line the app just typed.
        case deleteLastInsertion
    }

    /// How the command was recognized. Carried into the outcome so the
    /// surface (and any later measurement) can tell a spoken trigger from a
    /// gesture without inferring it.
    public enum VoiceCommandTrigger: Sendable, Equatable {
        /// The utterance transcribed to a phrase in
        /// `DictationDeleteCommand.phrases`.
        case phrase
        /// The recorded non-speech gesture matched, BEFORE ASR ran.
        case gesture
    }

    public enum Outcome: Sendable, Equatable {
        /// `detectedLanguage` is the worker's `stats.language` for THIS
        /// utterance (a code like "en"/"zh"), or nil when the backend did
        /// not report one — metadata only, carried so the surface can say
        /// what the last dictation was heard as. Absence is "unknown",
        /// never a detection.
        case success(text: String, clipboardByteIdentical: Bool,
                     detectedLanguage: String?, timings: StageTimings)
        case failed(stage: Stage, failure: SessionFailure, timings: StageTimings)
        /// The utterance was an instruction to the app, not text. NOTHING was
        /// inserted — that is the point — and `undo` says exactly what
        /// happened, including every reason the delete was refused.
        ///
        /// A separate case rather than a flavour of `.failed` because it is
        /// not a failure: the user asked for something and got it. Making it
        /// distinct is also what forces every existing consumer's `switch`
        /// to decide what to do with it, instead of silently folding a
        /// deletion into "inserted N words".
        case commandHandled(command: VoiceCommand, trigger: VoiceCommandTrigger,
                            undo: TextInserter.UndoOutcome, timings: StageTimings)
        /// The hold opened with the wake word and a workflow ran. NOTHING was
        /// inserted.
        ///
        /// Its own case rather than a `VoiceCommand` flavour of
        /// ``commandHandled``, whose `undo` is the delete's own receipt and
        /// would be a fiction here — there is nothing to undo about joining a
        /// meeting. `command` is the request with the wake word already off,
        /// carried so a surface can say what ran without re-parsing it.
        ///
        /// Windows draws the same line, as `DictationOutcome.SpokenCommand`.
        case spokenCommand(command: String, timings: StageTimings)
    }

    // MARK: - Dependencies

    private let capture: any DictationAudioSource
    private let asr: DictationAsrTransport
    private let inserter: TextInserter
    private let clock: () -> DispatchTime
    private let formattingEnabled: Bool
    private let targetBundleIDProvider: (() -> String?)?
    private let formattingRules: (@Sendable () -> Set<DictationFormatter.Rule>?)?

    /// M2's correction observer — `nil` unless the CALLER has already
    /// checked `feature.dictation_learning` (default OFF, D-1) and
    /// constructed one. This type never reads `FeatureFlags` itself, same
    /// posture as `formattingEnabled`/`targetBundleIDProvider` above: the
    /// flag lives at the call-site boundary. `nil` here means step 3 of
    /// `triggerUp()`'s insert branch is a complete no-op — nothing is
    /// constructed, nothing is called, matching the self-check's "flag off
    /// -> nothing constructed" requirement.
    /// Task K. When `true`, an utterance that transcribes to nothing but a
    /// `DictationDeleteCommand` phrase is treated as an instruction — the
    /// previous insertion is undone and NOTHING is typed — instead of being
    /// pasted as text.
    ///
    /// Same posture as `formattingEnabled`: the decision lives at the
    /// call-site boundary, this type never reads `FeatureFlags`. Default
    /// `nil`, so every existing caller (both CLIs, every self-check, the
    /// measurement harness) is byte-identical and cannot start eating text
    /// because a user's corpus happened to contain "scratch that".
    ///
    /// A CLOSURE rather than a stored `Bool` on purpose. `formattingEnabled`
    /// is fixed at `init`, which means the product pipeline has to REBUILD
    /// the whole session when its toggle flips — an established, working, but
    /// fiddly path (see `makeSessionIfNeeded`). Reading the setting per
    /// dictation instead means the switch takes effect on the next utterance
    /// with no rebuild and, more importantly, no way for the session's copy
    /// of the setting to drift from the user's actual choice. For a switch
    /// that governs DELETING TEXT, "the engine still thinks it's on" is not
    /// an acceptable failure mode.
    private let voiceDeleteEnabled: (@Sendable () -> Bool)?

    /// Offered every finished transcript. Returns the request it ran when the
    /// utterance was a command, and `nil` when it was not — which is also the
    /// answer for "not addressed to the app at all" and for "addressed, but no
    /// workflow matched". `nil` on a build with no workflow surface, which
    /// keeps the branch out of the session rather than behind a flag it would
    /// have to read.
    ///
    /// IT TAKES THE WHOLE TRANSCRIPT, not a pre-stripped request: the parse
    /// happens on the other side of the closure (Package.swift gives this
    /// target `DictationIPC` and `DictationAudioCapture` and nothing else, deliberately).
    /// Handing over the raw text keeps ONE implementation of "does this open
    /// with the wake word"; a copy of that test inside this module would be a
    /// second place to fix the next time a recognizer invents a separator, and
    /// the copy nobody fixed is the difference between typing a sentence and
    /// running a command. Windows has the same shape: its session hands the
    /// whole transcript to an `ITranscriptRouter`, and the Windows app
    /// composes none.
    private let spokenCommandSink: (@Sendable (String) async -> String?)?

    /// Task K's gesture trigger: given the captured clip (samples, rate),
    /// answers "was that the user's recorded delete gesture?".
    ///
    /// Consulted AFTER the silence gate and BEFORE ASR — a matched gesture
    /// costs no round trip at all — and only when `voiceDeleteEnabled` is
    /// also true, so the experimental matcher can never delete anything the
    /// spoken command could not already delete. `nil` (the default) means
    /// the gesture path does not exist for this session: nothing is
    /// computed, nothing is called.
    private let deleteGestureMatcher: (@Sendable ([Float], Double) -> Bool)?

    private let correctionObserver: DictationCorrectionObserver?
    /// Where a finished observation's diff-only result is reported —
    /// mirrors `NotesSessionController`'s `onLiveSegment` shape: the
    /// session stays free of storage/persistence concerns, a caller
    /// decides what (if anything) to do with the result. Fires on an
    /// unspecified background queue (`DictationCorrectionObserver.
    /// beginObserving`'s own contract) — a caller that touches UI/storage
    /// must hop off it itself.
    private let onCorrectionObserved: (@Sendable (DictationObservationResult) -> Void)?

    /// Fires with the final text the moment ASR (and optional formatting)
    /// produced it — BEFORE insertion is attempted, so a caller still has
    /// the words when the paste is refused.
    ///
    /// Added for the app surface: `Outcome.failed` deliberately carries no
    /// text, and `TextInserter` RESTORES the prior clipboard on failure, so
    /// without this hook a blocked paste really does lose the utterance and
    /// the UI's "your text is still on the clipboard" reassurance would be
    /// a lie. `DictationController` uses it to put the text back on the
    /// clipboard itself and report `pasteBlocked(clipboardHasText: true)`
    /// truthfully. `nil` (the default) is a complete no-op — the CLI paths
    /// are byte-identical.
    ///
    /// PRIVACY: this is the user's own speech. A caller may hold it in
    /// memory or write it to the local dictation history; it must never be
    /// logged or transmitted.
    private let onTranscriptReady: (@Sendable (String) -> Void)?

    /// Performs the insertion in place of `inserter.insertMeasured`.
    ///
    /// The standalone app routes insertion through its own closure so that an
    /// attempt it has already given up on (its transcribe deadline fired) can
    /// never paste late, and so its tests can observe what would have been
    /// typed without posting ⌘V into whatever app is frontmost.
    public typealias TextInsertion = @MainActor @Sendable (String) async
        -> (outcome: TextInserter.InsertOutcome, timing: TextInserter.InsertTiming)

    /// `nil` (the default) is the inserter, byte for byte.
    private let textInsertion: TextInsertion?

    /// Fires with the captured utterance samples the moment they pass the
    /// silence gate — the exact clip that is about to be handed to ASR.
    ///
    /// Added for W-25 (per-line audio replay): the history tab shipped a
    /// full play-back surface — per-row play controls, an audio store with
    /// retention and delete affordances — while the samples themselves died
    /// HERE, consumed by `asr.transcribeDetailed` and never seen again. The
    /// store's only caller was the self-check, so every shipped history
    /// entry carried `audioPath: nil` and every play button rendered
    /// disabled. Same shape as the stage-timings n:0 gap: a capability
    /// measured by the harness and dropped by the product path.
    ///
    /// PRIVACY: this is the user's own voice plus whatever the room said.
    /// The ONLY sanctioned consumer is the app surface feeding
    /// `DictationAudioStore`, whose retention bound and safe-location rule
    /// decide whether anything is actually kept. Never log it, never
    /// transmit it, never write it anywhere else. `nil` (the default) is a
    /// complete no-op and keeps the CLI paths byte-identical.
    private let onUtteranceCaptured: (@Sendable ([Float], Double) -> Void)?

    private let lock = NSLock()
    private var phase = DictationSessionPhase()
    private var armStartedAt: DispatchTime?

    // MARK: - Silence gate

    /// The same energy threshold used by the capture meter and segmenter.
    /// The session also requires the short voiced duration in the dictation
    /// policy, so a meter flicker from a key click cannot trigger insertion.
    public static let defaultAudibleThreshold: Float = AsrGatePolicy.dictation.audibleThreshold

    private let audibleThreshold: Float

    /// Gate the authoritative captured PCM before recognition and insertion.
    /// The shared dictation policy rejects short impulses while allowing a
    /// 120 ms word even inside a long quiet hold. It does not reject by the
    /// decoded words or by language confidence, which can erase real speech.
    public static func containsAudibleSignal(_ samples: [Float], threshold: Float) -> Bool {
        // A custom threshold retains the same short-word protection and
        // impulse guard as the ordinary dictation path.
        var policy = AsrGatePolicy.dictation
        policy.audibleThreshold = threshold
        // DictationCapture delivers 16 kHz PCM; use that rate for the
        // analysis frames and their measured voiced duration.
        if case .transcribe = AudioGate.shouldTranscribe(
            samples: samples, sampleRate: 16_000, policy: policy) {
            return true
        }
        return false
    }

    /// About -80 dBFS: a hold whose loudest sample stays under this heard no
    /// signal at all. A chosen floor, not one calibrated against working
    /// microphones in quiet rooms.
    public static let silentInputPeak: Float = 1e-4
    /// A shorter hold under the floor stays "Nothing heard", told to hold the
    /// key (`DictationCopy.asksToSpeakUp`): a headset can still be waking.
    public static let silentInputMinimumHoldSeconds: Double = 1.0

    /// A hold that the silence gate refused and that never reached
    /// `silentInputPeak`: an input that is connected but sends nothing,
    /// reported as no sound from the microphone rather than as a user who
    /// said nothing. Such a stream need not be exact zeros: headset holds
    /// have logged no zero sample and a peak of 0.0000.
    public static func isSilentInput(_ samples: [Float], heldSeconds: Double) -> Bool {
        heldSeconds >= Self.silentInputMinimumHoldSeconds && !samples.contains { abs($0) >= Self.silentInputPeak }
    }

    /// ASR text -> the text that will actually be inserted.
    ///
    /// Pure and `static` so the decision can be asserted directly. It is also
    /// the ONE place the decision is made — `triggerUp()` calls exactly this,
    /// so "the user's chosen rule set reaches the formatter" is a property of
    /// one call site rather than something that has to be re-verified through
    /// a microphone and an ASR worker.
    ///
    /// Precedence: an explicit `styleRules` (the user picked a behaviour for
    /// this app) beats the built-in per-app profile. `nil` means no choice
    /// was resolved and the v2 behaviour stands.
    public static func insertionText(
        _ text: String,
        formattingEnabled: Bool,
        styleRules: Set<DictationFormatter.Rule>?,
        targetBundleIDProvider: (() -> String?)?
    ) -> String {
        guard formattingEnabled else { return text }
        if let styleRules {
            return DictationFormatter.apply(text, rules: styleRules)
        }
        if let targetBundleIDProvider {
            return DictationFormatter.format(text, forApp: targetBundleIDProvider())
        }
        return DictationFormatter.format(text)
    }

    /// - Parameter formattingEnabled: runs `DictationFormatter` on the ASR
    ///   text before insertion, behind `feature.dictation` at the call site
    ///   (this type has no `FeatureFlags` dependency of its own — same
    ///   reasoning as keeping `DictationHotkey` out, see the type doc
    ///   comment above). **Default `false`.** An offline format evaluation
    ///   found the formatter's coverage of reference edits is small once
    ///   rules that regressed clean utterances were removed. A later pass
    ///   segmented by target app and found per-app gating does not rescue the rejected rules
    ///   on ANY app (terminal punctuation alone still regresses 28-37% of
    ///   Slack/Chrome/Claude-desktop's clean utterances) — only
    ///   capitalization clears the bar anywhere, so this stays off by
    ///   default corpus-wide too; see `DictationFormatter.profile(forApp:)`'s
    ///   doc comment for the full per-app numbers. Wiring stays here, off,
    ///   for a caller that wants it explicitly (e.g. a future settings
    ///   toggle) rather than ripping the capability back out.
    /// - Parameter targetBundleIDProvider: when supplied and
    ///   `formattingEnabled` is `true`, `triggerUp()` calls
    ///   `DictationFormatter.format(_:forApp:)` with this provider's result
    ///   instead of the bundle-ID-blind `format(_:)`. Defaults to
    ///   `FrontmostApplicationResolver.resolve().bundleID` (Iteration 38) —
    ///   the primitive `targetBundleIDProvider` existed for since Iteration
    ///   but had no implementation: it prefers the AX system-wide
    ///   focused element's owning process (more truthful for insertion
    ///   targeting than mere frontmost-ness — see that type's doc comment
    ///   for why they can disagree) and falls back to
    ///   `NSWorkspace.shared.frontmostApplication`. Every case it can hit
    ///   (no permission, no focused element, a helper with no bundle id, a
    ///   hung target app bounded by `AXUIElementSetMessagingTimeout`) is
    ///   defined and returns `nil` rather than crashing or hanging, and
    ///   `nil`/an unrecognized bundle id both resolve to
    ///   `.standard` via `profile(forApp:)` — identical to
    ///   `format(_:)`'s behavior. Because `formattingEnabled` still
    ///   defaults to `false`, wiring a real provider here changes nothing
    ///   about current default behavior; it only means a caller that flips
    ///   `formattingEnabled` on now gets a real per-app answer instead of a
    ///   dead parameter. Pass `nil` explicitly to disable app-targeting
    ///   entirely (falls back to `format(_:)`, byte-identical either way
    ///   since `formattingEnabled` defaults off).
    /// - Parameter formattingRules: the rule set the USER chose for the app
    ///   about to receive the text, or `nil` to keep the built-in per-app
    ///   profile. Read PER DICTATION (a closure, like `voiceDeleteEnabled`
    ///   and for the same reason) rather than fixed at `init`: the choice
    ///   lives in `DictationStyleSettings`, which the Settings surface writes
    ///   through its OWN store instance, so a value captured at construction
    ///   could only be refreshed by rebuilding the session and would silently
    ///   ignore a change the user just made.
    ///
    ///   `nil` — the default, and what every existing caller gets — is the
    ///   v2 path byte for byte: `format(_:forApp:)` when a bundle-ID provider
    ///   is wired, `format(_:)` otherwise. A non-nil EMPTY set is not the
    ///   same thing as `nil`: it means the user picked "exactly as spoken"
    ///   for this app and nothing may be changed.
    ///
    ///   This parameter is why `DictationStyleStore`'s options stopped being
    ///   decoration. Every one of them resolved to a rule set and reached
    ///   nothing: the store's only readers were its own view and its own
    ///   self-check, so a user who picked "Trim, capitalise, and end with a
    ///   full stop" got no change at all.
    /// - Parameter correctionObserver: M2's correction observer, or `nil`
    ///   (the default) to leave dictation-learning completely out of the
    ///   pipeline — see the stored property's doc comment. Pass the SAME
    ///   instance across a session's whole lifetime, never a fresh one per
    ///   dictation: `DictationCorrectionObserver.beginObserving`'s own
    ///   contract (unconditionally terminating any still-active
    ///   observation with `.nextTake` before starting a new
    ///   one) only has anything to terminate if every dictation's call
    ///   lands on the one shared instance.
    /// - Parameter onCorrectionObserved: fires once per finished
    ///   observation with the diff-only result — see the stored property's
    ///   doc comment.
    /// - Parameter audibleThreshold: the silence gate's peak cutoff — see
    ///   `defaultAudibleThreshold`. Overridable only so a self-check can
    ///   drive the boundary; production has exactly one correct value and
    ///   passing anything else makes the HUD's meter and the dictation's
    ///   outcome able to disagree.
    public init(
        capture: any DictationAudioSource,
        asr: DictationAsrTransport,
        inserter: TextInserter = TextInserter(),
        clock: @escaping () -> DispatchTime = { DispatchTime.now() },
        formattingEnabled: Bool = false,
        targetBundleIDProvider: (() -> String?)? = { FrontmostApplicationResolver.resolve().bundleID },
        formattingRules: (@Sendable () -> Set<DictationFormatter.Rule>?)? = nil,
        correctionObserver: DictationCorrectionObserver? = nil,
        onCorrectionObserved: (@Sendable (DictationObservationResult) -> Void)? = nil,
        onTranscriptReady: (@Sendable (String) -> Void)? = nil,
        onUtteranceCaptured: (@Sendable ([Float], Double) -> Void)? = nil,
        audibleThreshold: Float = DictationSession.defaultAudibleThreshold,
        voiceDeleteEnabled: (@Sendable () -> Bool)? = nil,
        deleteGestureMatcher: (@Sendable ([Float], Double) -> Bool)? = nil,
        spokenCommandSink: (@Sendable (String) async -> String?)? = nil,
        textInsertion: TextInsertion? = nil
    ) {
        self.textInsertion = textInsertion
        self.spokenCommandSink = spokenCommandSink
        self.voiceDeleteEnabled = voiceDeleteEnabled
        self.deleteGestureMatcher = deleteGestureMatcher
        self.audibleThreshold = audibleThreshold
        self.onTranscriptReady = onTranscriptReady
        self.onUtteranceCaptured = onUtteranceCaptured
        self.capture = capture
        self.asr = asr
        self.inserter = inserter
        self.clock = clock
        self.formattingEnabled = formattingEnabled
        self.targetBundleIDProvider = targetBundleIDProvider
        self.formattingRules = formattingRules
        self.correctionObserver = correctionObserver
        self.onCorrectionObserved = onCorrectionObserved
    }

    /// Test-only accessor — proves (self-check) that the SAME observer
    /// instance handed in at `init` is what every dictation's completion
    /// reaches, never a freshly-constructed one per call. See
    /// `correctionObserver`'s doc comment for why that identity matters.
    var correctionObserverForTesting: DictationCorrectionObserver? { correctionObserver }

    /// Ensures the ASR worker is spawned and initialized. Callers should do
    /// this ahead of the first `triggerUp()` (e.g. on app launch or on the
    /// hotkey's arm signal) — spawning a Python subprocess on the
    /// trigger-up critical path would dwarf every other stage.
    @discardableResult
    public func prepareAsr() async -> Bool {
        await asr.start()
    }

    /// Trigger-down: arms capture. Uses `DictationCapture`'s warm path
    /// (`armTriggerDown()`) if the caller already warmed it (via
    /// `capture.warmUp()` on whatever precursor signal — see
    /// `DictationCapture`'s doc comment and decision D-4), else falls back to the cold path (`start()`) so this type is
    /// usable even by a caller that never warms.
    @discardableResult
    public func triggerDown() -> Result<Void, SessionFailure> {
        guard transition(.triggerDown) else {
            return .failure(.invalidSequencing(.wrongPhase))
        }
        let t0 = clock()
        if capture.isCurrentlyWarm {
            capture.armTriggerDown()
        } else {
            do {
                try capture.start()
            } catch {
                // Whatever went wrong, the phase machine must not be left
                // stuck in `.capturing` with no way forward.
                _ = transition(.reset)
                // ONLY a permission error may be reported as one.
                //
                // This used to return `.micPermissionDenied` for EVERY
                // throw, on the reasoning that a cold start is "almost
                // always mic-permission related". `MicCapture.start()` also
                // throws `.engineStartFailed`, `.converterCreationFailed`
                // and `.inputFormatMismatch`, and it is bounded by a 5 s
                // timeout that throws on the documented Core Audio HAL hang
                // — none of which are consent problems. Measured on this Mac
                // 2026-08-20: six failures logged
                // `noAudio (capture did not start)` on a machine with 106
                // successful insertions, so the microphone grant was plainly
                // in place and all six were mislabelled.
                //
                // The label is not cosmetic. `micPermissionDenied` is the
                // one classification that means "the user must go and grant
                // something", and spending it on a device error is how a
                // recoverable fault gets filed as a consent problem — the
                // same mistake, in the same direction, as telling a user
                // their microphone is broken when meeting notes is holding
                // it (K-6). `.emptyCapture` is what actually happened: the
                // capture produced no audio. It carries the honest copy and
                // lets the device-recovery path run, which a consent
                // problem correctly declines.
                if case MicCaptureError.permissionDenied = error {
                    return .failure(.micPermissionDenied)
                }
                return .failure(.emptyCapture)
            }
        }
        lock.lock(); armStartedAt = t0; lock.unlock()
        return .success(())
    }

    /// Trigger-up: the full pipeline from here — stop capture, ASR round
    /// trip, insert. Every stage boundary is timestamped into the returned
    /// `Outcome`'s `StageTimings`.
    public func triggerUp() async -> Outcome {
        var timings = StageTimings()
        let armStart = readArmStartedAt()
        let wasWarm = capture.isCurrentlyWarm
        if let armStart {
            timings.armMs = millis(armStart, clock())
        }
        timings.usedWarmCapture = wasWarm

        guard transition(.triggerUp) else {
            return .failed(stage: .capture, failure: .invalidSequencing(.wrongPhase), timings: timings)
        }

        let tCaptureStart = clock()
        let result = await capture.stop(keepWarm: wasWarm)
        timings.captureStopMs = millis(tCaptureStart, clock())

        // A live capture can return a nonempty PCM buffer whose every sample
        // is exactly zero when the input device produced no signal. Treat it
        // like an empty capture so the caller tears down the dead warm
        // engine; quiet, nonzero room tone still goes through the normal
        // silence gate below, which reports a whole hold under
        // `silentInputPeak` the same way.
        guard result.samples.contains(where: { $0 != 0 }) else {
            _ = transition(.captureFinished)
            _ = transition(.reset)
            return .failed(stage: .capture, failure: .emptyCapture, timings: timings)
        }

        // SILENCE GATE — the clip never reaches the model unless something
        // in it was actually audible.
        //
        // Whisper does not return "nothing" for silence: with no real signal
        // the decoder free-runs and emits training-set artifacts — YouTube
        // outros ("えいえ次の動画でお会いしましょう。" / "thanks for
        // watching"), caption credits, often in Japanese or Korean. That is
        // what the model DOES when handed silence, not a bug we can patch
        // downstream, so silence must never be handed to it. Press the
        // hotkey, say nothing, release, and the app typed that sentence into
        // the user's editor; this guard is why it no longer can.
        //
        // The classification is `.emptyTranscript`, which
        // `DictationController.map` already turns into
        // `DictationFailure.nothingHeard` — "Audio arrived but decoded to
        // nothing. NOT an error to the user." The HUD already renders calm
        // copy for it and the shadow harness already counts it as
        // `asr_empty`, so this reuses the existing honest outcome instead of
        // inventing a new one. Reporting it at `stage: .capture` (not
        // `.asr`) keeps the stage field truthful: ASR never ran. Skipping it
        // also saves the ~1s round trip.
        //
        // No state leaks between dictations: the decision is derived purely
        // from THIS clip's samples, so there is nothing to reset.
        guard Self.containsAudibleSignal(result.samples, threshold: audibleThreshold) else {
            // THROUGH .captureFinished first, then .reset — matching every
            // other terminal path. Jumping straight to .reset from the
            // stopped-capture state is an illegal transition the machine
            // silently refuses (the `_ =` discards the false), leaving the
            // session stuck: every later trigger reports "arrived out of
            // order" until availability collapses and the surface DISARMS
            // ITSELF. Shipped exactly that way for ~1 hour on 2026-08-08;
            // the self-check missed it because it asserted the outcome and
            // the transport call count, not the machine's state afterwards.
            _ = transition(.captureFinished)
            _ = transition(.reset)
            // The capture's own hold (trigger-down to stop) when it measured
            // one; a scripted source has only this session's clock.
            let heldSeconds = result.forensics?.holdSeconds ?? (timings.armMs ?? 0) / 1000
            let failure: SessionFailure = Self.isSilentInput(result.samples, heldSeconds: heldSeconds)
                ? .emptyCapture : .emptyTranscript
            return .failed(stage: .capture, failure: failure, timings: timings)
        }

        _ = transition(.captureFinished)

        // TASK K, GESTURE PATH — before ASR, deliberately.
        //
        // A cough is not speech; sending it to Whisper costs a ~1 s round
        // trip and gets back either "" or a hallucinated caption artifact.
        // Matching here means the gesture costs nothing but the feature
        // computation, and it means the delete happens at the speed of the
        // release rather than a second later.
        //
        // BOTH flags must be on: the matcher is only ever consulted when the
        // spoken command is also enabled, so the experimental DSP path can
        // never delete something the reliable path could not. A `nil`
        // matcher (the default, and what ships when the gesture flag is off)
        // is a complete no-op — no features computed, no call made.
        if voiceDeleteEnabled?() == true, let deleteGestureMatcher,
           deleteGestureMatcher(result.samples, result.sampleRate) {
            _ = transition(.asrSucceeded)
            return await finishVoiceCommand(trigger: .gesture, timings: timings)
        }

        // The clip that is about to reach ASR is the clip the history tab can
        // replay — handed out HERE, after the silence gate, so a silent
        // accidental press is never retained. See `onUtteranceCaptured`.
        onUtteranceCaptured?(result.samples, result.sampleRate)

        let tAsrStart = clock()
        // The detailed variant of the SAME round trip (defaulted for
        // transports without metadata), so the detected language rides the
        // one wire call rather than costing a second decode.
        //
        // `compareBaseline` is spelled out on purpose. Without it the call
        // matches only the protocol extension's defaulted overload, which is
        // statically dispatched and wraps the plain `transcribe`: the real
        // transport's detail (the language for History, the decode counts
        // for the attempt log) was dropped on every dictation.
        let asrOutcome = await asr.transcribeDetailed(
            samples: result.samples, sampleRate: Int(result.sampleRate), compareBaseline: false)
        timings.asrMs = millis(tAsrStart, clock())

        var text: String
        let detectedLanguage: String?
        switch asrOutcome {
        case .failed(let failure):
            _ = transition(.asrFailed)
            _ = transition(.reset)  // terminal — ready for the next dictation
            return .failed(stage: .asr, failure: .asr(failure), timings: timings)
        case .result(let detail):
            text = detail.text
            detectedLanguage = detail.detectedLanguage
            timings.decodeStats = detail.decodeStats
            timings.dropReason = detail.dropReason
        }
        timings.recognizedTextShape = DictationTextShape(text)
        let recognizedText = text

        // Optional and off by default — see `formattingEnabled`'s doc
        // comment on `init`. Runs BEFORE the empty-transcript guard below
        // deliberately: no rule reachable from here can turn non-empty text
        // into empty text (capitalization cannot; the ellipsis strip refuses
        // to strip an utterance that has no letter or digit left; terminal
        // punctuation only appends), so ordering here doesn't change the
        // guard's behavior, but doing formatting first means the guard is
        // always checking exactly what would be inserted, not a
        // pre-formatting intermediate.
        text = Self.insertionText(
            text,
            formattingEnabled: formattingEnabled,
            styleRules: formattingRules?(),
            targetBundleIDProvider: targetBundleIDProvider)
        timings.insertionTextShape = DictationTextShape(text)
        timings.formattingChanged = !text.utf16.elementsEqual(recognizedText.utf16)

        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            timings.capturePeak = result.samples.reduce(Float(0)) { max($0, abs($1)) }
            _ = transition(.asrFailed)
            _ = transition(.reset)
            return .failed(stage: .asr, failure: .emptyTranscript, timings: timings)
        }

        _ = transition(.asrSucceeded)

        // TASK K, SPOKEN PATH. Checked here — after ASR, after formatting,
        // immediately before the text would have been handed onward — so the
        // string tested is exactly the string that would have been typed.
        //
        // `onTranscriptReady` is NOT fired for a command, and neither is
        // `onUtteranceCaptured`'s consumer going to see it as a dictation:
        // "scratch that" is an instruction to the app, not something the user
        // said into their editor, and putting it in history/on the clipboard
        // as a recovered transcript would be a small lie that shows up as a
        // junk row every single time the feature is used.
        if voiceDeleteEnabled?() == true, DictationDeleteCommand.isDeleteCommand(text) {
            return await finishVoiceCommand(trigger: .phrase, timings: timings)
        }

        // THE WAKE WORD. Same seam as the delete above and for the same
        // reason: after ASR, before the text is handed anywhere, so nothing
        // has been typed and nothing has been recorded as a dictation.
        //
        // Windows branches one step EARLIER, before its formatter, because
        // there the string goes to a prefix-anchored trigger matcher and a
        // formatter-added full stop is noise the matcher would have to know to
        // strip. Here formatting has already run — `text` above is the
        // formatted string — so the request carries whatever the rules did to
        // it. That is a behavioral difference and it is written down rather than
        // discovered later: `WorkflowRunner` tokenizes before matching, so a
        // trailing stop does not change the match, and moving this check above
        // the formatter would put it before the silence and delete gates that
        // are correct where they are.
        //
        // FALLING THROUGH IS THE SAFE DIRECTION. The trigger word is an ordinary English
        // word, so the recognizer producing it when nobody said it is routine.
        // A sink that declines gets the ORIGINAL text typed — eating the word
        // out of a sentence the app chose not to act on would be editing what the
        // user said. A bare trigger word with nothing after it is not a command:
        // someone who says only the name has asked for nothing.
        if let sink = spokenCommandSink, let request = await sink(text) {
            _ = transition(.reset)
            return .spokenCommand(command: request, timings: timings)
        }

        // Hand the text to the caller BEFORE attempting insertion — see
        // `onTranscriptReady`'s doc comment.
        onTranscriptReady?(text)

        // `insertMeasured` is `@MainActor` (AppKit pasteboard/AX/event calls
        // require it) — `triggerUp()` itself is not, so hop explicitly
        // rather than requiring every caller of this whole type to already
        // be on the main actor.
        return await finishInsert(text: text, detectedLanguage: detectedLanguage, timings: timings)
    }

    /// The one place a voice command is carried out, shared by the spoken
    /// and gesture triggers so the two can never diverge on what "delete"
    /// means or on which guardrails apply.
    ///
    /// `@MainActor` for the same reason `finishInsert` is: posting CGEvents
    /// and resolving the frontmost app are main-thread AppKit work.
    @MainActor
    private func finishVoiceCommand(
        trigger: VoiceCommandTrigger, timings: StageTimings
    ) async -> Outcome {
        let undo = await inserter.undoLastInsertion()
        switch undo {
        case .deleted:
            _ = transition(.insertSucceeded)
        default:
            // A refusal is not a session failure the user needs an error
            // for, but it IS "nothing reached the target app", and the
            // phase machine should say so rather than claim a completed
            // insertion. Both branches land in `.idle` via `.reset`.
            _ = transition(.insertFailed)
        }
        _ = transition(.reset)
        return .commandHandled(
            command: .deleteLastInsertion, trigger: trigger, undo: undo, timings: timings)
    }

    @MainActor
    private func finishInsert(
        text: String, detectedLanguage: String?, timings: StageTimings
    ) async -> Outcome {
        var timings = timings
        let outcome: TextInserter.InsertOutcome
        let timing: TextInserter.InsertTiming
        if let textInsertion {
            (outcome, timing) = await textInsertion(text)
        } else {
            (outcome, timing) = await inserter.insertMeasured(text)
        }
        timings.pasteTiming = timing
        switch outcome {
        case .success(let byteIdentical):
            _ = transition(.insertSucceeded)
            _ = transition(.reset)  // terminal — ready for the next dictation
            if Self.shouldBeginCorrectionObservation(insertionSucceeded: true, hasObserver: correctionObserver != nil) {
                beginCorrectionObservationFireAndForget(dictatedText: text)
            }
            return .success(text: text, clipboardByteIdentical: byteIdentical,
                            detectedLanguage: detectedLanguage, timings: timings)
        case .failed(let failure):
            _ = transition(.insertFailed)
            _ = transition(.reset)
            // shouldBeginCorrectionObservation(insertionSucceeded: false, ...)
            // is always false — a failed paste means there is nothing the app
            // actually inserted, so there is no span to observe. No call
            // here at all, deliberately, not a call that's then skipped.
            return .failed(stage: .paste, failure: .insert(failure), timings: timings)
        }
    }

    /// Pure decision, no AX/IO — `insertionSucceeded && hasObserver`, spelled
    /// out as its own `static` so the self-check can exercise the full
    /// truth table (both branches x flag on/off) without driving the
    /// TCC-gated capture/ASR/paste pipeline, matching how
    /// `DictationSessionPhase`/`DictationSpanAnchor` are already tested
    /// in this module.
    static func shouldBeginCorrectionObservation(insertionSucceeded: Bool, hasObserver: Bool) -> Bool {
        insertionSucceeded && hasObserver
    }

    /// Step 2 of the task: fire, never await. `beginObserving` itself is
    /// synchronous (it hands the actual up-to-90s watch off to its own
    /// background run loop and reports asynchronously via `onFinished`),
    /// but resolving the AX target still costs one bounded round trip
    /// (`FrontmostApplicationResolver`'s own `axMessagingTimeoutSeconds`
    /// budget) — detaching the whole thing keeps `finishInsert`
    /// (MainActor) and therefore `triggerUp()` returning immediately
    /// regardless, matching the project's three prior fired-not-awaited
    /// regressions this task was briefed to avoid repeating (Iteration 2's
    /// `shutdown()`, Iteration 40's `requestAccess`, Iteration 30's
    /// near-miss).
    private func beginCorrectionObservationFireAndForget(dictatedText: String) {
        guard let correctionObserver else { return }
        let onFinished = onCorrectionObserved
        Task.detached {
            guard let target = Self.resolveObservationTarget() else {
                // No focused element to observe right after paste (focus
                // moved, permission revoked mid-dictation, ...) — degrade
                // to "no observation this time," never a crash or a hang.
                return
            }
            correctionObserver.beginObserving(
                dictatedText: dictatedText,
                pastedText: dictatedText,
                observedElement: target.element,
                applicationElement: target.applicationElement,
                pid: target.pid,
                observedApp: target.bundleID,
                onFinished: { result in onFinished?(result) }
            )
        }
    }

    private struct ObservationTarget {
        let element: AXUIElement
        let applicationElement: AXUIElement
        let pid: pid_t
        let bundleID: String
    }

    /// Re-reads the AX system-wide focused element right after a
    /// successful paste — the exact field `TextInserter` just wrote into.
    /// Mirrors `FocusEditabilityChecker.check()`'s own AX call
    /// (`FocusEditabilityChecker.swift`) and `FrontmostApplicationResolver
    /// .resolveViaAXFocusedElement()`'s permission/timeout discipline, just
    /// returning the element instead of a yes/no or a bundle id — every
    /// case (no permission, no focused element, a helper process with no
    /// bundle id) returns `nil` rather than crashing or hanging.
    private static func resolveObservationTarget() -> ObservationTarget? {
        guard AXIsProcessTrusted() else { return nil }

        let systemWide = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(systemWide, FrontmostApplicationResolver.axMessagingTimeoutSeconds)

        var focusedRef: CFTypeRef?
        let copyErr = AXUIElementCopyAttributeValue(
            systemWide, kAXFocusedUIElementAttribute as CFString, &focusedRef)
        guard copyErr == .success, let focusedRef else { return nil }
        // Safe: AXUIElementCopyAttributeValue for kAXFocusedUIElementAttribute
        // always returns an AXUIElement on `.success` — same guarantee
        // `FocusEditabilityChecker.check()`/`FrontmostApplicationResolver`
        // already rely on.
        let element = focusedRef as! AXUIElement // swiftlint:disable:this force_cast
        AXUIElementSetMessagingTimeout(element, FrontmostApplicationResolver.axMessagingTimeoutSeconds)

        var pid: pid_t = 0
        guard AXUIElementGetPid(element, &pid) == .success, pid > 0 else { return nil }

        // A helper process (or one that already exited between the AX call
        // and here) with no bundle id is a real, expected outcome, not an
        // error — degrade to "no observation" rather than observing with
        // an unattributable app name.
        guard let bundleID = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier else { return nil }

        let applicationElement = AXUIElementCreateApplication(pid)
        return ObservationTarget(element: element, applicationElement: applicationElement, pid: pid, bundleID: bundleID)
    }

    /// Explicit reset, e.g. after a caller-side abandon (trigger-down
    /// followed by e.g. app losing focus rather than a real trigger-up).
    public func reset() {
        _ = transition(.reset)
        lock.lock(); armStartedAt = nil; lock.unlock()
    }

    public var currentPhaseForTesting: DictationSessionPhase.State {
        lock.lock(); defer { lock.unlock() }
        return phase.state
    }

    @discardableResult
    private func transition(_ event: DictationSessionPhase.Event) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return phase.handle(event) != nil
    }

    private func readArmStartedAt() -> DispatchTime? {
        lock.lock(); defer { lock.unlock() }
        return armStartedAt
    }

    private func millis(_ a: DispatchTime, _ b: DispatchTime) -> Double {
        Double(b.uptimeNanoseconds - a.uptimeNanoseconds) / 1_000_000
    }
}

/// The three calls `DictationSession` makes into capture, extracted as a
/// protocol so its silence gate is testable WITHOUT a live `AVAudioEngine`,
/// a mic-permission grant, or the audio-concurrency guard.
///
/// This exists for exactly one reason: the load-bearing claim of the
/// silence gate is "silence never reaches the model," and the only honest
/// way to assert that is to count calls on a fake ASR transport while
/// feeding the session a known-silent clip. `DictationCapture`'s existing
/// `injectSamplesForTesting(_:)` seam cannot do that on its own — it only
/// works AFTER a real `triggerDown()` has already started the engine, which
/// needs a device, a permission grant, and the concurrency guard, none of
/// which have anything to do with the property under test.
///
/// `DictationCapture` is the only production conformer; nothing about its
/// behavior changes and every existing call site keeps passing it unchanged.
public protocol DictationAudioSource: AnyObject, Sendable {
    var isCurrentlyWarm: Bool { get }
    func start() throws
    func armTriggerDown()
    func stop(keepWarm: Bool) async -> DictationCapture.CaptureResult
}

extension DictationCapture: DictationAudioSource {}

// MARK: - Style-wiring self-check

/// Proves the two halves of "the user's formatting choice actually happens":
/// the chosen rule set reaching the formatter, and the formatter appending
/// the right full stop for the script it is ending.
///
/// Shaped exactly like `runDictationFormatterSelfCheck()` — same module, same
/// return contract. NOT yet in the `--self-check` list, because that list
/// lives in the app entry point and this change does not own that file: adding it
/// is one line beside the formatter check,
/// `ok = runDictationStyleWiringSelfCheck() && ok`. Until then it is run by
/// hand, and it has been: it passes, and it fails (9 assertions) when the
/// rule-set wiring or the script-aware full stop is reverted.
///
/// ## Why these two
///
/// `DictationStyleStore` shipped four options that each resolved to a rule
/// set and reached nothing: its only readers were its own view and its own
/// self-check, so a user who picked "Trim, capitalise, and end with a full
/// stop" got no change at all. `DictationSession.insertionText` is now the
/// one place that decision is made, so asserting it here is asserting the
/// product path, not a parallel one.
///
/// And enabling that option WITHOUT the script fix would have mis-fixed the
/// majority of what it touches on this user's own history: 52 of the 83
/// transcripts with no terminal punctuation contain Han/kana, and Han
/// characters satisfy `addTerminalPunctuationIfMissing`'s `isLetter` guard,
/// so it would have appended a half-width `.` to Mandarin.
public func runDictationStyleWiringSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }
    func checkText(_ got: String, _ expected: String, _ label: String) {
        check(got == expected, "\(label): got \(got.debugDescription), expected \(expected.debugDescription)")
    }

    // The three rule sets `DictationStyleBehavior` declares that change text.
    // Spelled out rather than imported: the app entry point depends on this module, not
    // the other way round. `DictationStyleSelfCheck` asserts the surface's
    // sets equal these.
    let asSpoken: Set<DictationFormatter.Rule> = []
    let tidy: Set<DictationFormatter.Rule> = [.trailingEllipsis, .capitalization]
    let tidyAndFinish: Set<DictationFormatter.Rule> = [.trailingEllipsis, .capitalization, .terminalPunctuation]

    // MARK: 1 — the chosen rule set reaches the formatter

    // The load-bearing case: a terminal's built-in profile applies NOTHING,
    // so a result that differs from the input can only have come from the
    // user's choice. This is the assertion the dead control would fail.
    checkText(
        DictationSession.insertionText(
            "hello world...", formattingEnabled: true,
            styleRules: tidyAndFinish, targetBundleIDProvider: { "com.googlecode.iterm2" }),
        "Hello world.",
        "style-beats-builtin-profile: the chosen rule set did not reach the formatter")

    // The opposite direction, and the reason an empty set may never be
    // collapsed into "no choice": "exactly as spoken" has to beat a built-in
    // profile that WOULD have changed the text.
    checkText(
        DictationSession.insertionText(
            "hello world...", formattingEnabled: true,
            styleRules: asSpoken, targetBundleIDProvider: { "com.apple.Safari" }),
        "hello world...",
        "as-spoken-beats-builtin-profile: an explicit empty rule set was treated as no choice")

    checkText(
        DictationSession.insertionText(
            "hello world...", formattingEnabled: true,
            styleRules: tidy, targetBundleIDProvider: { "com.apple.Safari" }),
        "Hello world",
        "tidy-reaches-formatter: the trim+capitalise choice did not produce its own result")

    // No choice pushed -> byte-identical to the pre-style path, per app and
    // with no provider at all. This is what keeps every CLI, harness and
    // self-check that never pushes a style on exactly the old behaviour.
    for id in ["com.googlecode.iterm2", "com.apple.Safari", "com.tinyspeck.slackmacgap"] {
        for input in ["hello world", "trailing off now...", "already ends with a period."] {
            checkText(
                DictationSession.insertionText(
                    input, formattingEnabled: true,
                    styleRules: nil, targetBundleIDProvider: { id }),
                DictationFormatter.format(input, forApp: id),
                "no-style-matches-v2(\(id)): a session with no style choice changed behaviour")
        }
    }
    checkText(
        DictationSession.insertionText(
            "hello world...", formattingEnabled: true,
            styleRules: nil, targetBundleIDProvider: nil),
        DictationFormatter.format("hello world..."),
        "no-style-no-provider-matches-v2: the bundle-ID-blind path changed behaviour")

    // The global switch still wins: formatting off means nothing is touched,
    // whatever the user picked per context.
    for rules in [asSpoken, tidy, tidyAndFinish] {
        checkText(
            DictationSession.insertionText(
                "hello world...", formattingEnabled: false,
                styleRules: rules, targetBundleIDProvider: { "com.apple.Safari" }),
            "hello world...",
            "global-switch-off-wins: a style choice formatted text while formatting was off")
    }

    // MARK: 2 — the terminal character matches the script

    checkText(DictationFormatter.addTerminalPunctuationIfMissing("hello world"),
              "hello world.", "ascii-period: an English sentence must still end in '.'")
    checkText(DictationFormatter.addTerminalPunctuationIfMissing("\u{6211}\u{4EEC}\u{4E0B}\u{5348}\u{4E09}\u{70B9}\u{5F00}\u{4F1A}"),
              "\u{6211}\u{4EEC}\u{4E0B}\u{5348}\u{4E09}\u{70B9}\u{5F00}\u{4F1A}\u{3002}",
              "han-full-stop: a Mandarin sentence must not end in a half-width '.'")
    // Ends on a Latin product name but is a Chinese sentence — the case a
    // last-character test alone gets wrong (6 such transcripts in the real
    // history).
    checkText(DictationFormatter.addTerminalPunctuationIfMissing("\u{6211}\u{662F}\u{8BF4}\u{6253}\u{5F00}Zoom"),
              "\u{6211}\u{662F}\u{8BF4}\u{6253}\u{5F00}Zoom\u{3002}",
              "han-majority: a Chinese sentence ending on an English word took the wrong mark")
    // Opens in English and finishes in Chinese — the case a majority test
    // alone gets wrong (4 such transcripts).
    checkText(DictationFormatter.addTerminalPunctuationIfMissing("We deployed the build to staging this morning \u{6211}\u{4EEC}\u{5DF2}\u{7ECF}\u{8DD1}\u{901A}\u{4E86}"),
              "We deployed the build to staging this morning \u{6211}\u{4EEC}\u{5DF2}\u{7ECF}\u{8DD1}\u{901A}\u{4E86}\u{3002}",
              "han-tail: an utterance that ends in Chinese took the wrong mark")
    // Opens in Chinese and finishes with a whole English sentence — '.' is
    // right here, and the pair of conditions has to leave it alone.
    checkText(DictationFormatter.addTerminalPunctuationIfMissing("\u{6D4B}\u{8BD5}\u{4E00}\u{4E0B} we also tried the new keyboard and we are comparing it with the old one"),
              "\u{6D4B}\u{8BD5}\u{4E00}\u{4E0B} we also tried the new keyboard and we are comparing it with the old one.",
              "latin-tail: an English closing sentence took a full-width stop")
    // A trailing digit must not lose the script of the sentence it ends.
    checkText(DictationFormatter.addTerminalPunctuationIfMissing("\u{6211}\u{4E70}\u{4E86}3"),
              "\u{6211}\u{4E70}\u{4E86}3\u{3002}",
              "han-digit-end: a Chinese sentence ending in a digit took the wrong mark")
    // Kana is the same convention as Han; Hangul and Cyrillic are NOT.
    checkText(DictationFormatter.addTerminalPunctuationIfMissing("\u{304A}\u{306F}\u{3088}\u{3046}\u{3054}\u{3056}\u{3044}\u{307E}\u{3059}"),
              "\u{304A}\u{306F}\u{3088}\u{3046}\u{3054}\u{3056}\u{3044}\u{307E}\u{3059}\u{3002}",
              "kana-full-stop: Japanese must not end in a half-width '.'")
    checkText(DictationFormatter.addTerminalPunctuationIfMissing("\u{C548}\u{B155}\u{D558}\u{C138}\u{C694}"),
              "\u{C548}\u{B155}\u{D558}\u{C138}\u{C694}.",
              "hangul-ascii-period: Korean takes an ASCII full stop, not a full-width one")

    // Never doubles up, in either script.
    for already in ["already done.", "\u{5DF2}\u{7ECF}\u{597D}\u{4E86}\u{3002}", "\u{771F}\u{7684}\u{5417}\u{FF1F}", "\u{592A}\u{597D}\u{4E86}\u{FF01}", "trailing comma,"] {
        checkText(DictationFormatter.addTerminalPunctuationIfMissing(already), already,
                  "no-double-stop: text that already ends in a mark was touched")
    }

    // MARK: 3 — the two together, through the rule pipeline the user picks

    checkText(
        DictationFormatter.apply("\u{6211}\u{4EEC}\u{8D70}\u{5427}...", rules: tidyAndFinish),
        "\u{6211}\u{4EEC}\u{8D70}\u{5427}\u{3002}",
        "cjk-through-apply: the ellipsis strip then the full-width stop")
    checkText(
        DictationSession.insertionText(
            "\u{6211}\u{4EEC}\u{4E0B}\u{5348}\u{4E09}\u{70B9}\u{5F00}\u{4F1A}", formattingEnabled: true,
            styleRules: tidyAndFinish, targetBundleIDProvider: { "com.apple.Safari" }),
        "\u{6211}\u{4EEC}\u{4E0B}\u{5348}\u{4E09}\u{70B9}\u{5F00}\u{4F1A}\u{3002}",
        "cjk-through-session: the chosen rule set and the script-aware stop did not meet")

    guard failures.isEmpty else {
        let first = failures[0]
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-style-wiring-check: FAIL: \(first)\(suffix)")
        return false
    }
    print("dictation-style-wiring-check: PASS")
    return true
}
