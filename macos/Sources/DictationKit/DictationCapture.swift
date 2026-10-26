import Foundation
import DictationAudioCapture

/// Records ONE dictation utterance. This is the M1 capture half of the
/// release-to-text budget: capture was the unmeasured half after Iteration 3
/// pinned audio-ready -> text.
///
/// REUSE, not reinvention: this wraps a single `DictationAudioCapture.MicCapture`
/// instance rather than opening a second `AVAudioEngine` against the same
/// device. `MeetingRecorder.appendAudio`'s doc comment records the exact
/// failure this avoids — two engines (or two `SCStream`s) on one input
/// device is real device contention, not a style nit: it is the root cause
/// behind "the app hears nothing unless another app is holding the mic," and
/// it silently desyncs two capture pipelines that believe they own the same
/// hardware.
///
/// ## Two capture modes, one engine
///
/// Iteration 9 measured `AVAudioEngine.start()` at
/// ~800ms on the dev machine — restarting the engine per dictation risks
/// losing the first ~800ms of speech every time the hotkey is pressed after
/// any idle period. This type now supports both:
///
/// - **Cold path** (`start()`/`stop()`, unchanged since Iteration 9): starts
///   the engine fresh on trigger-down, stops it on trigger-up. Kept as-is
///   because `--dictation-latency` still needs a genuine cold baseline to
///   compare warm against — see the "warm-vs-cold" table it now prints.
/// - **Warm path** (`warmUp()` / `armTriggerDown()` / `stop(keepWarm:)` /
///   `coolDown()`): the engine is started ONCE, ahead of the actual trigger,
///   and left running while "armed." Trigger-down (`armTriggerDown()`) does
///   no engine I/O at all — it only flips which buffer new audio lands in —
///   so its cost is the lock acquisition plus an array copy, not
///   `engine.start()`. See `DictationLatencyCheck.swift` for the measured
///   number.
///
/// The warm path also buys PRE-ROLL for free: while armed-but-not-triggered,
/// incoming audio fills an `AudioRingBuffer` instead of being discarded;
/// `armTriggerDown()` prepends its contents to the utterance. That fixes a
/// second, independent problem from the engine-start cost: a user who starts
/// talking a beat before the key physically registers (a few tens of ms of
/// human/hardware latency is normal, not a bug) would otherwise lose that
/// lead syllable even with an already-running engine, because "trigger-down"
/// and "speech-down" are never perfectly synchronized.
///
/// Sharing note: `DictationCapture`
/// does NOT share a `MicCapture` *instance* with `NotesSessionController` —
/// each owns its own. The product coordinates this before arming through
/// microphone ownership checks, and refuses instead of double-starting
/// capture while meeting notes owns the microphone. True instance-sharing
/// (one `MicCapture`, two consumers fanning out) remains a separate design
/// task: `MicCapture.stream` is a plain `AsyncStream`, not a broadcast.
///
/// Mic-indicator note (the OTHER thing whoever wires this in must read):
/// any time this engine is running — cold OR warm — macOS shows the orange
/// mic-in-use dot. A permanently-warm engine (started once at app launch,
/// never stopped) means that dot is effectively always on whenever the app
/// runs, which is a real, user-visible privacy signal this project should
/// not spend carelessly. This type therefore does NOT warm itself
/// automatically or run standing timers — the CALLER decides when to
/// `warmUp()` (e.g. on a cheap precursor signal) and when to `coolDown()`
/// (e.g. after `shouldCoolDown(idleSeconds:)` says an idle window has
/// elapsed). Keeping the engine permanently warm was decided against, in
/// favour of this precursor-plus-idle-timeout design.
public final class DictationCapture: @unchecked Sendable {
    /// VAR, not LET — and this is the whole of W-21's symptom 2.
    ///
    /// `MicCapture.stream` is a `let AsyncStream` built once in
    /// `MicCapture.init`, and `MicCapture.stop()` calls
    /// `continuation.finish()` on it. A finished `AsyncStream` is finished
    /// FOREVER: the next `for await chunk in mic.stream` returns
    /// immediately, so the collector task ends the instant it starts and no
    /// audio is ever appended again. `MicCapture` has no restart path and is
    /// not built to have one.
    ///
    /// This class used to hold ONE `MicCapture` for its entire lifetime, and
    /// `ProductDictationPipeline` holds ONE `DictationCapture` for the
    /// lifetime of the process. So the FIRST `coolDown()` — which is not an
    /// error path but the routine 180-second idle unload the controller runs
    /// on purpose to give back ~3.6 GB — permanently killed capture until
    /// the app was relaunched. Everything downstream still worked: the tap
    /// fired, the HUD said "Listening", the engine transcribed an empty
    /// buffer, and W-2's silence gate correctly reported `nothingHeard`.
    ///
    /// It is in the shipped log, 14 seconds apart:
    ///     22:22:59  releasing the transcriber after 180s idle
    ///     22:23:13  dictation failed: nothingHeard (after 6450 ms)
    /// and `--audio-contention-probe` reproduces it in isolation: cool down
    /// a warm capture, warm it back up, and the meter reads
    /// `chunks=0 audible=0/0` for as long as you care to wait.
    ///
    /// Rebuilding the `MicCapture` on the next warm-up is the fix. It also
    /// happens to be the whole of the DEVICE rebind — `MicCapture.start()`
    /// resolves the preferred/default input at the moment it is called, so a
    /// fresh instance binds to whatever the default is NOW rather than to
    /// whatever it was when the engine first started.
    private var mic: MicCapture
    /// True once `mic.stop()` has been called on the current instance, i.e.
    /// its stream is finished and it must be replaced before use.
    private var micIsSpent = false
    private let constants: SegmenterConstants
    private let lock = NSLock()
    private var buffer: [Float] = []
    private var preRoll: AudioRingBuffer
    private var isRetaining = false
    private var isWarm = false
    private var lastActivity: TimeInterval = 0
    private var collectTask: Task<Void, Never>?
    private let clock: () -> TimeInterval
    /// Where the D15 kill switch is read from. Injected so a test can arm the
    /// watchdog against a throwaway suite instead of the user's real defaults.
    private let defaults: UserDefaults

    // MARK: - Per-press forensics accumulation
    //
    // Every dictation press saves its full state, whether it succeeded or
    // not. The rule that makes this cheap to justify is the product policy: most
    // holds contain a voice, so a hold that contains NO audio is a fault
    // and must be visible immediately — not reconstructed later from a bare
    // `nothingHeard` line, which is what the 2026-09-03 Bluetooth
    // silent-wake-up investigation had to do across 50 retained clips.
    //
    // All of it lives under the same `lock` as `buffer`/`preRoll`, because
    // it is measured on the collector task and read on the caller's, and a
    // forensic record that raced its own audio would be worse than none.

    /// Device identity snapshotted at TRIGGER-DOWN, not at stop: the cold
    /// path's `stop()` has already stopped the engine by the time it builds
    /// the record, and the device that matters is the one that captured the
    /// audio, not whatever is default a second later.
    private var pressDeviceLocked = DictationCaptureForensics()
    /// Wall clock and monotonic clock at trigger-down. Both, because they
    /// answer different questions: `startedAt` lines a record up against the
    /// shipped log's timestamps, `holdStart` measures the hold without
    /// caring whether the wall clock moved.
    private var pressHoldStartLocked: TimeInterval = 0
    /// `WarmEngineMonitor` generation at press start / at warm-up, so a
    /// completed hold (and a bounded retest open) can say how many
    /// configuration changes restarted the engine underneath it.
    private var pressEngineGenerationLocked = 0
    private var warmUpEngineGenerationLocked = 0
    /// Chunks appended to the UTTERANCE this press (pre-roll excluded).
    private var pressChunkCountLocked = 0
    /// Chunks whose peak was at or below the audible floor.
    private var pressSilentChunkCountLocked = 0
    /// The FIRST `chunkPeakCap` peaks — the wake-up is at the head. See
    /// `DictationCaptureForensics.chunkPeaks`.
    private var pressChunkPeaksLocked: [Float] = []
    /// Monotonic chunk index across the whole press, PRE-ROLL INCLUDED, so
    /// the `DICTATION_CAPTURE_TRACE=1` timeline shows the wake-up even when it
    /// lands in the ring buffer before the trigger.
    private var pressTraceIndexLocked = 0
    private var lastForensicsLocked: DictationCaptureForensics?

    // MARK: - D15 in-hold watchdog state
    //
    // All of it under the same `lock` as `buffer`/`preRoll`, and all of it
    // read/written on this class's own collector task, timer task and caller
    // thread — NEVER on CoreAudio's render thread, and never while holding
    // `MicCapture`'s lock. The watchdog's HAL reads
    // (`inputStreamFlaggedStale()`, `inputDevices()`) happen with this lock
    // released; see `watchdogTick()`.

    /// True between `armTriggerDown()` and `stop()` — the only window D15
    /// arms in.
    private var holdWatchActiveLocked = false
    /// `clock()` at the most recent chunk of THIS press (pre-roll chunks
    /// included, since arrival is arrival), or 0 before the first one.
    private var lastChunkAtLocked: TimeInterval = 0
    /// The class startup grace for the device this press is bound to, in
    /// seconds — bluetooth gets the long one.
    private var holdGraceSecondsLocked: Double = 0
    /// `clock()` at the moment the current grace window opened — the arm, or
    /// the last rebind. NOT `pressHoldStartLocked`: the device the rebind
    /// landed on is entitled to its own wake-up window, and measuring its
    /// grace from a press that started seconds earlier would give it none.
    private var holdGraceStartedAtLocked: TimeInterval = 0
    /// The device UID the press started on. The registry must hear about
    /// THIS one as `.interrupted`, not the one we rebound to.
    private var holdBoundUIDLocked: String?
    /// Interruptions already served on this press. D15 allows exactly one
    /// rebind; the second interruption ends the hold.
    private var interruptionsThisPressLocked = 0
    private var interruptionLocked: Interruption?
    private var watchdogTask: Task<Void, Never>?
    /// This press's token. Bumped on every arm AND on every `stop()`, so a
    /// rebind that is inside `warmUp()` when the user lets go can tell that the
    /// hold it belongs to is over — see `finishInterruption`.
    private var holdGenerationLocked = 0
    /// When the HAL device roster was last enumerated, and what it said. The
    /// enumeration is throttled to `deviceRosterPollSeconds`; see
    /// `watchdogTick()` for why 20 Hz was wrong.
    private var lastRosterCheckAtLocked: TimeInterval = 0
    private var lastRosterPresentLocked = true
    /// `clock()` at the first tick on which `MicCapture`'s configuration-changed
    /// flag was seen set, or nil while it is clear. W1: the flag alone is "a
    /// reason to look"; only the flag PLUS silence for
    /// `staleFlagHoldSeconds` is evidence.
    private var staleFlagSinceLocked: TimeInterval?
    /// How many collector loops are attached to a stream right now. A COUNT,
    /// not a flag: a rebind starts a second collector while the first may still
    /// be draining its last buffers, and a boolean would let the old loop's
    /// teardown clear the new one's. `stop()` uses it to avoid awaiting a
    /// collector that will never finish.
    private var collectorsRunningLocked = 0

    /// Where to rebind when a hold is interrupted, injected by the
    /// controller: the registry adapter's current bind-plan primary UID,
    /// falling back to the resolved default input. Nil (the default, and
    /// what every CLI path passes) falls back to
    /// `MicCapture.resolvedTargetInputDeviceID()`'s UID here instead, so a
    /// caller that never wires the registry still rebinds somewhere real.
    ///
    /// Called on the watchdog's own task, off the press path. Must not block.
    public var onInterruptedRebindTarget: (@Sendable () -> String?)?

    /// Test seam for the ENGINE half of a rebind only (the cool-down plus
    /// `warmUp(bindDeviceUID:)`), which needs a real microphone. The decision,
    /// the discard, the forensics and the one-rebind-per-press rule are the
    /// product's and are exercised for real. Returns whether capture reopened.
    var interruptionRestartOverrideForTesting: (@Sendable (String?) -> Bool)?

    /// The preview run that fed on the discarded audio must be ENDED before the
    /// rebind (F2 of the step-2 review): `onRetainedAudio` has already streamed
    /// every retained chunk to the ASR preview transport, so clearing the
    /// buffer is only half a discard. The controller wires this to the same
    /// reset it uses when a hold is cancelled.
    ///
    /// Called on the watchdog's task. Must not block.
    public var onInterruptionDiscardedAudio: (@Sendable () -> Void)?

    /// Start a FRESH preview run — called only after a rebind actually reopened
    /// a capture, so no hypothesis can span the interruption.
    public var onInterruptionRebound: (@Sendable () -> Void)?

    /// Test seam: the bound device the watchdog arms against, so a test can
    /// drive the REAL `armTriggerDown()` -> `armWatchdog()` path (its flag
    /// read, its nil-UID refusal, its grace selection and its timer task)
    /// without opening a microphone. Nil — production — reads `MicCapture`.
    var watchdogBoundDeviceOverrideForTesting: (@Sendable () -> (uid: String?, transport: String))?

    /// Test seam for the DEVICE ROSTER only — the UIDs
    /// `MicCapture.inputDevices()` would report. A test needs to say "the
    /// bound device is still there" / "it is gone" without unplugging
    /// hardware; the predicate that reads it is the product's.
    var watchdogDeviceRosterOverrideForTesting: (@Sendable () -> [String])?

    /// The watchdog's kill switch, in Dictation's own defaults namespace.
    ///
    /// The source registry carries the dictation stream (the
    /// registry's flag, default OFF), because the rebind target was the
    /// registry's bind plan. The registry is NOT ported to Dictation — the
    /// rebind target here is `MicCapture.resolvedTargetInputDeviceID()` — so
    /// the watchdog gets its own key, and its own default: ON, because the
    /// in-hold rebind is the product's behaviour, with an explicitly stored
    /// `false` as the kill switch (same `object(forKey:)` idiom as
    /// `MicCapture.halInputBackendEnabled`).
    static let watchdogFlagKey = "dictation.capture.watchdog"

    /// The watchdog's arming decision, read in `armWatchdog` and nowhere else.
    static func watchdogEnabled(defaults: UserDefaults) -> Bool {
        defaults.object(forKey: watchdogFlagKey) as? Bool ?? true
    }

    /// How often the watchdog may enumerate the HAL device roster. 500 ms
    /// against a 50 ms tick: `MicCapture.inputDevices()` is a CoreAudio
    /// enumeration plus a UID read per device, and a device mid-renegotiation
    /// is exactly when that is slow — the tick that must notice a 300 ms gap
    /// cannot afford to wait behind it 20 times a second.
    static let deviceRosterPollSeconds: Double = 0.5

    /// Longest `stop()` will wait for the capture collector to drain. The
    /// normal case finishes in microseconds (the stream is already finished);
    /// the bound exists because a rebind can have attached the collector to a
    /// LIVE engine, whose stream nothing is going to finish, and a trigger-up
    /// that never returns is the worst failure this class can produce.
    static let collectorDrainBoundSeconds: Double = 1.0

    /// How long `MicCapture`'s configuration-changed flag must stand — with
    /// NO buffer arriving in that window — before the watchdog will treat it
    /// as evidence. Longer than the flag's own 300 ms re-validation window, so
    /// a `WarmEngineMonitor` verdict that is going to clear the flag has
    /// already had its chance.
    static let staleFlagHoldSeconds: Double = 0.4

    /// No chunk for this long — after the startup grace — is an interrupted
    /// hold (D15). 300 ms is ~3.5 tap callbacks at `MicCapture`'s 85 ms
    /// cadence: long enough that a single late callback cannot trip it,
    /// short enough that the user has not finished their sentence into a
    /// dead microphone.
    public static let interruptionGapSeconds: Double = 0.3

    /// Watchdog poll interval. Well under `interruptionGapSeconds` so the
    /// detection latency the user feels is the gap, not the poll.
    static let watchdogTickSeconds: Double = 0.05

    /// The class startup graces. These values come from
    /// `AudioSourcePolicy.bundled()` with these two values as the fallback;
    /// the policy file lives in the unported `AudioSourceRegistry`, so
    /// Dictation uses the fallback values directly (they were the shipped
    /// values on the branch too). The device wakes up before the watchdog is
    /// allowed to have an opinion — the XM5's own HFP wake-up is ~1.2 s of
    /// exact zeros and is a device property, not a fault (v4 §Context 4).
    public static let bluetoothStartupGraceMsFallback = 2500
    public static let defaultStartupGraceMsFallback = 800

    private static let startupGraceMs: (bluetooth: Int, other: Int) =
        (bluetoothStartupGraceMsFallback, defaultStartupGraceMsFallback)

    /// The grace for a transport string as `MicCapture.boundDeviceTransport()`
    /// reports it. Anything that is not literally `"bluetooth"` — including
    /// `"unknown"` — gets the short grace: an unknown transport is not a
    /// reason to let a dead microphone run for 2.5 s.
    static func startupGraceSeconds(transport: String) -> Double {
        Double(transport == "bluetooth" ? startupGraceMs.bluetooth : startupGraceMs.other) / 1000
    }

    /// The watchdog's verdict, as a PURE function of what it observed — so
    /// the rules are testable without a clock, a device, or a task.
    enum WatchdogVerdict: Equatable {
        case keepGoing
        case interrupt(reason: String)
    }

    /// D15's three triggers. **Ground truth is buffer flow**, not a flag.
    ///
    /// The grace gates ALL of them, deliberately: a Bluetooth headset's
    /// wake-up produces no chunks, can flag a format change, and can briefly
    /// vanish from the device list while it renegotiates. Every one of those
    /// is normal inside the grace and a fault outside it.
    ///
    /// ## Why `configurationChanged` is not, on its own, a reason to interrupt
    ///
    /// `MicCapture.inputStreamFlaggedStale()` is `isRunning &&
    /// configurationChanged`, and that flag's own contract (see
    /// `MicCapture.inputBindingIsStale`'s comment) is that it is **"a reason
    /// to LOOK"**, not a verdict: 13 of 26 measured XM5 binds set it on a
    /// perfectly healthy capture, so it is set first and re-validated over a
    /// 300 ms `WarmEngineMonitor` window, and only a non-`.clear` verdict
    /// leaves it standing. This function used to interrupt on the first read
    /// of it — at a 50 ms tick that is roughly six reads before the
    /// observation can possibly answer.
    ///
    /// Post-D13 that is not merely premature, it is backwards: on the
    /// input-only AUHAL backend a mid-hold A2DP⇄HFP flip **keeps delivering**
    /// (6/6 phases, 435-458 batches per 8 s, `stale=false` in the route-flip
    /// experiments) — which is the whole reason D13 exists — so interrupting
    /// on the flip discards a good hold and costs the user a press instead of
    /// saving one.
    ///
    /// So the flag is only admissible in CONJUNCTION with silence: it must
    /// have stood for `staleFlagHoldSeconds` AND no buffer may have arrived
    /// since it went up. A flip that keeps delivering never interrupts. (The
    /// ideal input is the `WarmEngineMonitor`'s resolved verdict, which
    /// `MicCapture` does not expose publicly — see this round's report.)
    static func watchdogVerdict(
        now: TimeInterval,
        holdStartedAt: TimeInterval,
        lastChunkAt: TimeInterval,
        graceSeconds: Double,
        gapSeconds: Double,
        staleFlagSince: TimeInterval?,
        staleFlagHoldSeconds: Double,
        boundDevicePresent: Bool
    ) -> WatchdogVerdict {
        guard now - holdStartedAt >= graceSeconds else { return .keepGoing }
        // No chunk yet this press ⟹ measure the gap from the press itself.
        // The grace above has already elapsed, so this is a device that never
        // delivered, not one still waking up.
        let since = max(lastChunkAt, holdStartedAt)
        let silentFor = now - since
        // The flag AND silence, which is the honest "the stream died"
        // conjunction: the flag must have stood for `staleFlagHoldSeconds` AND
        // no buffer may have arrived in that window (measured from the later of
        // the flag's rise and the last chunk). Reported first only so the REASON
        // names the proximate cause when a flip is what killed the audio; since
        // the hold is longer than the gap, the `no-chunks` branch below would
        // otherwise catch exactly the same presses.
        if let staleFlagSince, now - staleFlagSince >= staleFlagHoldSeconds,
           now - max(lastChunkAt, staleFlagSince) >= staleFlagHoldSeconds {
            return .interrupt(reason: "stream-stale")
        }
        if !boundDevicePresent { return .interrupt(reason: "device-gone") }
        if silentFor >= gapSeconds { return .interrupt(reason: "no-chunks") }
        return .keepGoing
    }

    /// Per-chunk peak amplitude (0...1, un-smoothed), fired for EVERY chunk
    /// the mic delivers — warm/pre-roll chunks included, since "is the mic
    /// hearing me right now" is exactly as interesting before the trigger
    /// as after it.
    ///
    /// Fires on the capture collector's own task, NOT the main actor: a
    /// consumer that touches UI state must coalesce and hop itself.
    /// `DictationController` does that with the same 12 Hz drain +
    /// asymmetric smoothing `AudioLevelModel` uses for the notes meters, so
    /// the dictation HUD and the notes meters cannot disagree about what
    /// "audible" means.
    ///
    /// Nil (the default) means the peak is not even computed — the CLI
    /// paths pay nothing for a meter they do not render.
    public var onAudioLevel: (@Sendable (Float) -> Void)?

    /// Raw post-trigger PCM for a best-effort live preview. Unlike
    /// `onAudioLevel`, this fires only while `isRetaining` is true: warm
    /// pre-roll is kept for the authoritative final clip but is never replayed
    /// into a stream opened at trigger-down. The sink must return immediately;
    /// `DictationLivePreviewSession.append` only performs a bounded copy.
    public var onRetainedAudio: (@Sendable ([Float], Int) -> Void)?

    /// 300ms: long enough to cover typical speech-onset-before-keypress
    /// slop (a deliberate hold gesture's own physical latency, plus the
    /// ~50-150ms a person's mouth can beat their finger on a word they were
    /// already forming) without holding multiple seconds of audio at all
    /// times purely on spec. Not derived from a measured distribution of
    /// this user's actual lead time — that would need instrumented real
    /// dictations with a human at the keyboard (see the module README's
    /// "needs a human" list) — so treat this as a reasoned default, tunable
    /// via `init(preRollSeconds:)`, not a calibrated constant.
    public static let defaultPreRollSeconds: Double = 0.3

    public let preRollCapacitySamples: Int

    /// What the in-hold watchdog (spec v4 D15) did to ONE press.
    ///
    /// Present on a `CaptureResult` exactly when the hold was interrupted —
    /// the bound input stopped delivering, went stale, or disappeared — so
    /// the consumer can say "mic changed to X, could you say that again?"
    /// instead of transcribing a partial utterance or blaming the user for a
    /// silent clip.
    ///
    /// `deviceName` is the NEW device (the one the rebind landed on), because
    /// that is the only name the user can act on: the device they were
    /// talking to is gone.
    public struct Interruption: Sendable, Equatable {
        /// Log-safe classification, never prose for the user: one of
        /// `"no-chunks"`, `"stream-stale"`, `"device-gone"`.
        public let reason: String
        /// Milliseconds from trigger-down to the moment the watchdog fired.
        public let atMs: Double
        /// The device the press STARTED on — the one the registry must hear
        /// about as `.interrupted`. Nil when nothing was bound.
        public let previousDeviceUID: String?
        /// The UID the rebind aimed at (`onInterruptedRebindTarget`, or the
        /// resolved default). Nil when nothing could be resolved.
        public let rebindTargetUID: String?
        /// Did the rebind actually open a capture again?
        public let rebindSucceeded: Bool
        /// Display name of the input after the rebind.
        public let deviceName: String
    }

    /// One captured utterance.
    public struct CaptureResult: Sendable {
        public let samples: [Float]
        public let sampleRate: Double
        public var durationSeconds: Double { Double(samples.count) / sampleRate }

        /// Non-nil when this press was interrupted mid-hold (D15). The
        /// samples are then deliberately EMPTY: the partial hold is discarded
        /// rather than transcribed, because half an utterance pasted into the
        /// user's document is worse than asking them to repeat it.
        public let interruption: Interruption?

        /// True when this press was interrupted mid-hold — the flag the
        /// consumer branches on. See `interruption` for the detail.
        public var interrupted: Bool { interruption != nil }

        /// Everything measurable about the press that produced `samples` —
        /// device, transport, timing, and above all the audio's SHAPE (the
        /// exact-zero lead-in, the all-zero flag, the per-chunk peaks). See
        /// `DictationCaptureForensics` for the defect it exists for.
        ///
        /// It rides on the result rather than only on the capture object so
        /// the consumer that decides the OUTCOME holds the numbers that
        /// explain it in the same value — `DictationSession` cannot report
        /// `nothingHeard` while the record of why sits somewhere else,
        /// reachable only by a second call it might forget to make.
        ///
        /// Nil for a scripted clip: a test double did not capture anything,
        /// and a fabricated record would be indistinguishable from a
        /// measured one in the panel that displays it.
        public let forensics: DictationCaptureForensics?

        /// Public only so a scripted `DictationAudioSource` (see that
        /// protocol's doc comment on `DictationSession`) can hand a known
        /// clip to the session without a live `AVAudioEngine`. The
        /// memberwise initializer is internal by default, which would have
        /// confined every such test double to this module.
        ///
        /// `forensics` is DEFAULTED so every existing `init(samples:
        /// sampleRate:)` call site — all of them scripted test doubles —
        /// keeps compiling untouched.
        public init(
            samples: [Float],
            sampleRate: Double,
            forensics: DictationCaptureForensics? = nil,
            interruption: Interruption? = nil
        ) {
            self.samples = samples
            self.sampleRate = sampleRate
            self.forensics = forensics
            self.interruption = interruption
        }
    }

    public init(
        constants: SegmenterConstants = SegmenterConstants(),
        preRollSeconds: Double = DictationCapture.defaultPreRollSeconds,
        clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
        defaults: UserDefaults = .standard
    ) {
        self.defaults = defaults
        self.constants = constants
        // voiceProcessingAllowed: false — dictation is push-to-talk with a
        // single near-field speaker and nothing playing that could echo.
        // The voice processor's noise gate ate normal-volume speech on a
        // low-gain built-in mic (2026-08-09 field report: only Whisper's
        // silence-hallucinations came out), while the meeting path keeps
        // AEC for cross-talk. See MicCapture.voiceProcessingAllowed.
        self.mic = MicCapture(constants: constants, voiceProcessingAllowed: false)
        self.preRollCapacitySamples = max(0, Int((preRollSeconds * Double(constants.sampleRate)).rounded()))
        self.preRoll = AudioRingBuffer(capacity: preRollCapacitySamples)
        self.clock = clock
    }

    // MARK: - Cold path (unchanged behavior — start fresh, stop fully)

    /// Trigger-down, cold path. Blocks until `AVAudioEngine.start()` has
    /// returned. Kept byte-for-byte behaviorally identical to the
    /// pre-warm-engine version of this type so `--dictation-latency`'s cold
    /// baseline stays comparable across iterations.
    public func start() throws {
        // Cold path: no pre-roll (the ring was never filled — this engine
        // did not exist a moment ago), and `wasWarm: false` so a zero
        // lead-in in the record reads as "paid at the trigger", which on
        // this path is expected, rather than as "paid at warm-up and
        // therefore a bug", which it would be on the warm one.
        beginPress(wasWarm: false, preRollSamples: 0)

        lock.lock()
        buffer.removeAll(keepingCapacity: true)
        isRetaining = true
        lock.unlock()

        let engine = renewMicIfSpent()

        // Subscribe before starting the engine so no early buffer is lost
        // to a race between `mic.start()` firing the first tap callback
        // and this loop attaching to the stream.
        collectTask = startCollecting(on: engine)

        // Same unwind as the warm path. This one never left `isWarm` set
        // (it is stamped after the start returns), but it DID leave
        // `isRetaining` true on a dead engine, which is the other half of
        // the same wrong state — see `abandonFailedStart`.
        do {
            try engine.start()
        } catch {
            abandonFailedStart(engine)
            throw error
        }

        lock.lock(); isWarm = true; lock.unlock()
    }

    /// Returns the `MicCapture` to use for this run, replacing the current
    /// one if it has already been stopped. See the `mic` property's comment
    /// for what a spent instance does (nothing, silently, forever).
    ///
    /// Returns the instance rather than relying on the field so the caller
    /// and the collector task can never disagree about WHICH engine they are
    /// talking to — a second `warmUp()` racing a `coolDown()` would
    /// otherwise leave a collector attached to the replaced object.
    private func renewMicIfSpent() -> MicCapture {
        lock.lock()
        if micIsSpent {
            mic = MicCapture(constants: constants, voiceProcessingAllowed: false)
            micIsSpent = false
            // THE PRE-ROLL MUST NOT OUTLIVE THE ENGINE THAT FILLED IT.
            //
            // The ring's entire contract — see `AudioRingBuffer` — is "the
            // most recent N ms of audio". That is only true while ONE engine
            // runs continuously. The moment an engine is replaced there is a
            // gap of unbounded length in front of these samples: a 30-second
            // idle mic release, a 180-second transcriber unload, a device
            // unplugged and replugged, or (2026-08-20) a `MicCapture.start()`
            // that timed out after 5 s. Nothing in this class used to clear
            // the ring at any of those points, so `armTriggerDown()`'s
            // `buffer = preRoll.samples` handed the NEXT utterance 300 ms of
            // audio recorded before the gap.
            //
            // It can occur twice, several seconds apart, after a bind
            // failure took the engine down at the trigger:
            //     09:10:08  capture diagnostics: bound=(unbound), 4800 samples (0.30s), peak 0.0029
            //     09:10:13  capture diagnostics: bound=(unbound), 4800 samples (0.30s), peak 0.0029
            // 4800 samples IS the pre-roll capacity (0.3 s x 16 kHz) exactly,
            // and the identical peak proves it is the same buffer replayed —
            // the same 300 ms of the past, offered as two different
            // utterances the user had just spoken.
            //
            // Here that stale audio was quiet (0.0029, under the 0.0040
            // audible floor) so `DictationSession`'s silence gate refused it
            // and the user got `nothingHeard`. That is luck, not a defence:
            // the gate tests the clip's PEAK, and a ring holding the tail of
            // a real sentence clears 0.0040 easily. Then 300 ms of stale
            // speech goes to Whisper, which is the one input length it
            // hallucinates on (a 0.5 s clip once decoded as Korean), and the
            // result is pasted into whatever the user is typing in now. A
            // silent wrong answer, not a visible failure.
            //
            // Dropping it here rather than in `stop()`/`coolDown()` is
            // deliberate: this is the single point every engine replacement
            // passes through, and it leaves the healthy warm path — one
            // engine, still running, ring still current — completely
            // untouched.
            preRoll = AudioRingBuffer(capacity: preRollCapacitySamples)
        }
        let engine = mic
        lock.unlock()
        return engine
    }

    /// Marks the current engine spent and hands it back so the caller can
    /// stop it outside the lock.
    /// Test seam: mark the engine spent WITHOUT opening or stopping a real
    /// device, so a check can assert the renewal that follows.
    public func markInputSpentForTesting() {
        lock.lock(); micIsSpent = true; lock.unlock()
    }

    private func takeMicForStop() -> MicCapture {
        lock.lock()
        micIsSpent = true
        let engine = mic
        lock.unlock()
        return engine
    }

    // MARK: - Warm path

    /// Starts the engine now (if not already running) and begins filling
    /// the pre-roll ring buffer, WITHOUT retaining a full utterance yet.
    /// Call this on the earliest cheap signal available for the gesture in
    /// use — see the type doc comment for
    /// why "earliest cheap signal" differs between hold-to-talk (no signal
    /// earlier than the trigger itself exists) and double-tap-toggle (the
    /// first tap-down, before the second tap confirms activation, is a
    /// genuine few-hundred-ms head start).
    /// - Parameters:
    ///   - bindDeviceUID: bind THIS start to a specific input device UID
    ///     instead of the user's pin / the system default (spec v3 §5.5 —
    ///     "automation never writes `userPreferredSource`"). `nil`, the
    ///     default and the value every pre-existing caller passes, leaves
    ///     resolution exactly as it was. Sticky on the engine, so the
    ///     staleness check keeps comparing against the same device; it is
    ///     re-applied on every call, including the `nil` that clears it.
    ///   - firstAudibleBoundSeconds: bound `MicCapture`'s wait for the first
    ///     AUDIBLE buffer for this start (§5.5.1's `startup_grace_ms +
    ///     300 ms` self-heal retest). `nil` keeps the standing 2.5 s budget.
    ///     Never lengthens it.
    public func warmUp(
        bindDeviceUID: String? = nil, firstAudibleBoundSeconds: TimeInterval? = nil
    ) throws {
        lock.lock()
        if isWarm { lock.unlock(); return }
        isWarm = true
        lastActivity = clock()
        lock.unlock()

        let engine = renewMicIfSpent()
        lock.lock(); warmUpEngineGenerationLocked = engine.warmEngineGeneration(); lock.unlock()
        // BEFORE `startCollecting`/`start()`: the override is read inside
        // `MicCapture.start()`'s own locked body, so it has to be in place
        // before that body runs. Applied on a freshly-renewed engine too —
        // `renewMicIfSpent` hands back a brand-new `MicCapture` with no
        // override at all, which is precisely when a retest press would
        // otherwise silently fall back to the pin.
        engine.bindNextStart(
            deviceUID: bindDeviceUID, firstAudibleBoundSeconds: firstAudibleBoundSeconds)
        collectTask = startCollecting(on: engine)
        do {
            try engine.start()
        } catch {
            abandonFailedStart(engine)
            throw error
        }
    }

    /// Did the most recent `start()` observe a buffer carrying a NON-ZERO
    /// sample before its first-audible bound expired?
    ///
    /// This is §5.5.1's retest verdict: "if it delivers `active` audio
    /// inside the bound, it wins the hold" — arrival is not enough (the
    /// whole Bluetooth wake-up defect is a device that arrives punctually
    /// and empty), so the answer comes from `lastFirstAudibleMs()`, which
    /// is nil exactly when the bound expired with every sample still zero.
    public var lastStartDeliveredAudibleAudio: Bool {
        lock.lock(); let engine = mic; lock.unlock()
        return engine.lastFirstAudibleMs() != nil
    }

    /// Configuration changes the warm engine has seen since the last
    /// `warmUp` — non-zero means an audio route change restarted the
    /// engine (or killed its stream) after we opened it.
    public func engineRestartsSinceWarmUp() -> Int {
        lock.lock(); let engine = mic; let base = warmUpEngineGenerationLocked; lock.unlock()
        return max(0, engine.warmEngineGeneration() - base)
    }

    /// Only the configuration-change half of `isInputStaleWhileWarm` — the
    /// half that is still meaningful when the registry binds a device
    /// explicitly (see `MicCapture.inputStreamFlaggedStale`).
    public var isInputStreamFlaggedStale: Bool {
        lock.lock()
        let warm = isWarm
        let engine = mic
        lock.unlock()
        return warm && engine.inputStreamFlaggedStale()
    }

    /// Unwinds the optimistic state `start()`/`warmUp()` set BEFORE they knew
    /// whether the engine would open.
    ///
    /// `warmUp()` has to set `isWarm` first — it is the re-entrancy guard that
    /// stops two triggers racing into two `AVAudioEngine.start()` calls on one
    /// device, which is the HAL deadlock this module bounds everywhere else.
    /// What it must not do is LEAVE it set when the start throws, and that is
    /// what shipped.
    ///
    /// The consequence is worse than one lost press, because "warm with a dead
    /// engine" is a state nothing downstream can see:
    ///
    ///   - `DictationController.begin()` rebuilds a stale binding only under
    ///     `if capture.isCurrentlyWarm, capture.isInputStaleWhileWarm`, and
    ///     `MicCapture.bindingIsStale` opens with `guard isRunning else {
    ///     return false }` — an engine that never started is not running, so
    ///     it is never "stale". No rebuild.
    ///   - The very next line, `if !capture.isCurrentlyWarm { try warmUp() }`,
    ///     is skipped too, because we still claim to be warm. No retry.
    ///
    /// So every later press walks straight into `armTriggerDown()` on a dead
    /// engine and "captures" whatever is left in the ring, instantly. The
    /// a diagnostic log, after a bind failure took the device:
    ///
    ///     09:10:03  dictation failed: noAudio (capture did not start)
    ///     09:10:08  capture diagnostics: bound=(unbound), 4800 samples ... peak 0.0029
    ///     09:10:08  dictation failed: nothingHeard (after 7 ms)
    ///     09:10:13  capture diagnostics: bound=(unbound), 4800 samples ... peak 0.0029
    ///     09:10:13  dictation failed: nothingHeard (after 7 ms)
    ///
    /// Seven milliseconds — it never waited for audio, and no rebind was even
    /// attempted. The one honest `noAudio` became a run of silent ones, and
    /// only the 30 s idle release could break out of it.
    ///
    /// Marking the engine spent (rather than just clearing `isWarm`) is what
    /// forces `renewMicIfSpent()` to build a fresh `MicCapture` next time: a
    /// `start()` that timed out inside CoreAudio leaves an engine whose real
    /// state we cannot know, and reusing it is how a bad bind persists.
    private func abandonFailedStart(_ engine: MicCapture) {
        // No-op if it never ran; finishes the stream and ends the collector
        // if it partly did.
        engine.stop()
        // Belt and braces for the case where `stop()` bailed on `!isRunning`
        // and therefore never finished the continuation: without this the
        // collector task waits on a stream that will never yield, forever.
        collectTask?.cancel()
        lock.lock()
        // The watchdog has nothing to watch on a capture that did not open.
        // The TASK is deliberately not cancelled here: this method can be
        // reached from inside the watchdog's own rebind (`warmUp` throwing),
        // and cancelling the task you are running on would abandon the rest
        // of the rebind's bookkeeping.
        holdWatchActiveLocked = false
        isWarm = false
        isRetaining = false
        micIsSpent = true
        buffer.removeAll(keepingCapacity: true)
        preRoll = AudioRingBuffer(capacity: preRollCapacitySamples)
        lock.unlock()
    }

    /// Test seam: runs the REAL failed-start unwind, since a `MicCapture`
    /// cannot be made to throw on demand without a device that is genuinely
    /// broken. Drives the product body rather than restating it — same
    /// discipline as `renewInputForTesting()`.
    func abandonFailedStartForTesting() {
        lock.lock(); let engine = mic; lock.unlock()
        abandonFailedStart(engine)
    }

    /// Test seam: hands a chunk to the REAL `appendLocked` the collector task
    /// calls, so a check can fill the pre-roll ring (or the utterance buffer)
    /// without opening a microphone. The routing decision under test —
    /// retaining vs. pre-roll — is the product's, not the test's.
    func appendChunkForTesting(_ chunk: [Float]) {
        routeChunk(chunk)
    }

    /// Trigger-down under a warm (already-running) engine: zero engine I/O.
    /// Snapshots the current pre-roll contents into the utterance buffer and
    /// flips subsequent audio to accumulate there instead of the ring
    /// buffer. This is the call `--dictation-latency`'s warm measurement
    /// times — the number it reports is lock contention plus an array copy,
    /// not `engine.start()`.
    public func armTriggerDown() {
        retainedDeliveryLock.lock()
        defer { retainedDeliveryLock.unlock() }
        lock.lock()
        buffer = preRoll.samples
        let ring = buffer.count
        let prefix = buffer
        isRetaining = true
        lastActivity = clock()
        lock.unlock()

        // Publish the prefix before the collector can append its next chunk.
        // Recovery and final recognition must see exactly the same beginning.
        if !prefix.isEmpty { onRetainedAudio?(prefix, constants.sampleRate) }

        // Deliberately AFTER the arm, and deliberately doing no engine or
        // CoreAudio I/O of its own — see `beginPress`. What
        // `--dictation-latency` times on this path is lock contention plus
        // an array copy, and forensics must not quietly become a third
        // term in that measurement.
        beginPress(wasWarm: true, preRollSamples: ring)
        // Same placement, same reason: the two property reads the watchdog
        // needs (transport, bound UID) are stashed values on `MicCapture`,
        // not HAL round trips, but they are still after the arm so nothing
        // the user's finger is waiting on grew.
        armWatchdog()
    }

    // MARK: - D15: notice a dead input DURING the hold, rebind once, say so

    /// Arms the watchdog for THIS press. Only the warm path arms it — a cold
    /// `start()` is the latency harness's baseline and the CLI's path, and
    /// D15 scopes the watchdog to `armTriggerDown()`...`stop()`.
    private func armWatchdog() {
        // KILL SWITCH, read here and nowhere else. Default ON (see
        // `watchdogFlagKey`); an explicitly stored `false` makes everything
        // from here on inert and a hold behaves exactly as it did before
        // this step.
        guard Self.watchdogEnabled(defaults: defaults) else { return }

        // Both values are stashed at bind time inside `MicCapture` (no HAL
        // read), and both are read with THIS lock released — the watchdog must
        // never hold `MicCapture`'s lock, and taking ours across its accessors
        // is how that rule gets broken by accident later.
        let bound: (uid: String?, transport: String)
        if let override = watchdogBoundDeviceOverrideForTesting {
            bound = override()
        } else {
            lock.lock(); let engine = mic; lock.unlock()
            bound = (engine.boundDeviceUID(), engine.boundDeviceTransport())
        }
        // NO BOUND DEVICE, NO WATCHDOG. A capture with nothing bound is
        // already handled a level up — `MicCapture.inputBindingIsStale()`
        // treats a nil bound device as stale and the press path rebuilds on
        // it — and a watchdog that fired here would "recover" a hold that
        // never had an input by opening a microphone nobody asked for. It is
        // also what keeps `armTriggerDown()` inert for every caller that
        // drives this class without a device (the CLIs).
        guard let boundUID = bound.uid, !boundUID.isEmpty else { return }

        let grace = Self.startupGraceSeconds(transport: bound.transport)
        let now = clock()

        lock.lock()
        // The press's own token. Everything the rebind writes is conditional on
        // it still matching — see `finishInterruption`.
        holdGenerationLocked += 1
        holdWatchActiveLocked = true
        lastChunkAtLocked = 0
        holdGraceSecondsLocked = grace
        holdGraceStartedAtLocked = now
        holdBoundUIDLocked = boundUID
        lastRosterCheckAtLocked = 0
        lastRosterPresentLocked = true
        staleFlagSinceLocked = nil
        interruptionsThisPressLocked = 0
        interruptionLocked = nil
        let previous = watchdogTask
        watchdogTask = Task { [weak self] in await self?.runWatchdog() }
        lock.unlock()
        previous?.cancel()
    }

    /// Test seam: the FIRST TWO THINGS `stop()` does — disarm the watchdog
    /// (which supersedes any rebind in flight) and stop retaining — so a check
    /// can land a release inside the rebind, which is the race
    /// `finishInterruption` exists for. Deliberately both, and in this order:
    /// a "release" that only disarmed would leave `isRetaining` true and let a
    /// superseded rebind look like a live press.
    func simulateReleaseForTesting() {
        disarmWatchdog()
        endRetainingLocked()
    }

    /// Ends the watch AND supersedes any rebind in flight. Called by `stop()`
    /// before it touches the engine, so a rebind that is mid-`warmUp()` when
    /// the user lets go cannot publish itself afterwards.
    private func disarmWatchdog() {
        lock.lock()
        holdGenerationLocked += 1
        holdWatchActiveLocked = false
        let task = watchdogTask
        watchdogTask = nil
        lock.unlock()
        task?.cancel()
    }

    /// The timer half. Its own task on the cooperative pool — never the
    /// CoreAudio render thread, which may not allocate, lock or log (see
    /// `appendLocked`'s comment for what a violation there costs).
    private func runWatchdog() async {
        while !Task.isCancelled {
            try? await Task.sleep(nanoseconds: UInt64(Self.watchdogTickSeconds * 1_000_000_000))
            if Task.isCancelled { return }
            if await watchdogTick() { return }
        }
    }

    /// ONE evaluation. Returns true when the watchdog is finished with this
    /// press. Internal rather than private so a test can drive the REAL body
    /// off an injected clock instead of waiting on wall time.
    @discardableResult
    func watchdogTick() async -> Bool {
        // Every critical section in this async body goes through a
        // synchronous helper — Swift 6 forbids raw `NSLock` calls inside an
        // `async` function. Same discipline as `appendLocked`.
        guard let snapshot = watchdogSnapshotLocked() else { return true }
        let engine = snapshot.engine
        let now = clock()

        // Both reads happen with our lock RELEASED.
        //
        // ORDER MATTERS, and so does the throttle. `MicCapture.inputDevices()`
        // is a CoreAudio enumeration plus a UID read per device, and a device
        // mid-renegotiation is exactly when that is slow — i.e. running it at
        // the 50 ms tick rate would delay the very tick meant to notice the
        // 300 ms gap. The cheap stashed flag and the gap are evaluated first,
        // and the roster is consulted at most every
        // `deviceRosterPollSeconds`, only after the grace, with the previous
        // answer standing in between.
        // The flag is TIMED, not trusted: this records when it first went up
        // (and forgets it the moment it clears), so the verdict can require it
        // to have stood through `staleFlagHoldSeconds` of silence.
        let staleFlagSince = noteStaleFlagLocked(
            engine.inputStreamFlaggedStale(), since: snapshot.staleFlagSince, at: now)
        let pastGrace = now - snapshot.graceStartedAt >= snapshot.graceSeconds
        var present = snapshot.lastRosterPresent
        if pastGrace, let boundUID = snapshot.boundUID, !boundUID.isEmpty,
           now - snapshot.lastRosterCheckAt >= Self.deviceRosterPollSeconds {
            let roster = watchdogDeviceRosterOverrideForTesting?()
                ?? MicCapture.inputDevices().map { $0.1 }
            present = roster.contains(boundUID)
            recordRosterCheckLocked(present: present, at: now)
        }

        let verdict = Self.watchdogVerdict(
            now: now,
            holdStartedAt: snapshot.graceStartedAt,
            lastChunkAt: snapshot.lastChunkAt,
            graceSeconds: snapshot.graceSeconds,
            gapSeconds: Self.interruptionGapSeconds,
            staleFlagSince: staleFlagSince,
            staleFlagHoldSeconds: Self.staleFlagHoldSeconds,
            boundDevicePresent: present)
        guard case .interrupt(let reason) = verdict else { return false }

        // D15: exactly ONE rebind per press. A second interruption ends the
        // hold where it stands — the user is already going to be asked to
        // repeat themselves, and a device that dies twice inside one press is
        // not going to be fixed by opening it a third time.
        if snapshot.interruptionsServed >= 1 {
            FileHandle.standardError.write(Data(
                ("[dictation-capture] hold interrupted a second time (\(reason)) — "
                 + "ending the hold, no second rebind\n").utf8))
            endHoldOnSecondInterruptionLocked()
            return true
        }
        await performInterruptionRebind(reason: reason, generation: snapshot.generation)
        return false
    }

    /// The one rebind D15 allows: discard the partial hold AND the preview run
    /// it fed, resolve the registry's current best source, reopen there, and
    /// record enough for the press forensics to explain it afterwards.
    ///
    /// `generation` is the press token this rebind belongs to. Every write at
    /// the end is conditional on it: a release lands as `disarmWatchdog()`,
    /// which bumps the token, and a rebind that discovers it has been
    /// superseded must publish NOTHING and give the device it just opened
    /// straight back.
    private func performInterruptionRebind(reason: String, generation: Int) async {
        let opening = beginInterruptionLocked(reason: reason)
        let previousUID = opening.previousUID
        let atMs = opening.atMs

        // F2: the preview stream is the OTHER path pre-interruption audio takes
        // out of this class. `onRetainedAudio` has already pushed every
        // retained chunk to the ASR preview transport, so clearing `buffer` is
        // only half a discard: without this the caption the user reads while
        // the HUD prepares "could you say that again?" is a hypothesis stitched
        // from a dead-device fragment and a new-device one. Ended BEFORE the
        // rebind, and a fresh run is started (by the same sink) only once the
        // rebind has actually reopened something.
        onInterruptionDiscardedAudio?()

        // The controller's closure resolves the registry's bind plan; the
        // fallback here is for every caller that has no registry at all.
        let target = onInterruptedRebindTarget?()
            ?? MicCapture.deviceUID(MicCapture.resolvedTargetInputDeviceID())
        FileHandle.standardError.write(Data(
            ("[dictation-capture] hold interrupted (\(reason)) after "
             + String(format: "%.0f", atMs) + "ms on \(previousUID ?? "(unbound)")"
             + " — discarding the partial hold and rebinding to \(target ?? "(unresolved)")\n").utf8))

        // First of the two token checks: nothing has been opened yet, so a
        // release here costs only a log line. The interruption record was
        // already written by `beginInterruptionLocked`, so the press is still
        // reported honestly as `mic-changed` either way.
        guard isCurrentGenerationLocked(generation) else {
            FileHandle.standardError.write(Data(
                ("[dictation-capture] the hold ended before the rebind opened anything — "
                 + "nothing reopened\n").utf8))
            return
        }

        // Deliberately rebinds even when the target IS the device that just
        // died: a fresh open is what recovers a headset that flipped profile,
        // and refusing to reopen "the same" device is how a recoverable flip
        // became a permanently dead press.
        let reopened: Bool
        if let override = interruptionRestartOverrideForTesting {
            reopened = override(target)
        } else {
            reopened = await restartCaptureForInterruption(targetUID: target)
        }

        let engine = currentMic()
        let record = Interruption(
            reason: reason,
            atMs: atMs,
            previousDeviceUID: previousUID,
            rebindTargetUID: target,
            rebindSucceeded: reopened,
            // W5: name the device the rebind ACTUALLY LANDED ON, and name
            // nothing when it landed nowhere. `MicCapture.stop()` deliberately
            // does not clear `boundDeviceID` (the forensics path reads it after
            // a stop), so on a failed rebind `boundInputDeviceName()` still
            // answers with the device that just died — which is how the card
            // came to say "Mic changed to WH-1000XM5 / Could you say that
            // again?" when the XM5 was gone and nothing was listening. Both
            // halves of that claim were false. An empty name is the honest
            // answer and the HUD renders it as "Mic changed" with no device.
            //
            // On a SUCCESSFUL rebind this is the right name by construction:
            // `warmUp()` bound it inside `start()`, including the case where
            // `MicCapture`'s own fallback bound the system default instead of
            // the requested target.
            deviceName: reopened ? engine.boundInputDeviceName() : "")
        let published = finishInterruption(
            record,
            reopened: reopened,
            generation: generation,
            graceSeconds: Self.startupGraceSeconds(transport: engine.boundDeviceTransport()),
            boundUID: engine.boundDeviceUID(),
            at: clock())

        guard published else {
            // Second token check, and the one that matters: the user released
            // while `warmUp()` was running. Nothing was published — no
            // retention, no watch, not even the rebind's own result — and the
            // engine it just opened is handed straight back, bounded by
            // `MicCapture.stop()`'s own timeout. Without this, `isRetaining`
            // came back true after the hold ended, which makes
            // `shouldCoolDown(idleSeconds:)` refuse forever ("NEVER release a
            // microphone that is currently recording") and leaves the orange
            // recording indicator lit until the next press.
            guard reopened else { return }
            // ...unless a NEW press has already armed on it. That press called
            // `armTriggerDown()` after the release, so the engine now in hand is
            // ITS engine: cooling it down here would close the microphone under
            // a user who is currently holding the key — trading the bug this
            // guard fixes for a worse one.
            guard shouldCoolSupersededEngineLocked() else {
                FileHandle.standardError.write(Data(
                    ("[dictation-capture] the hold ended while rebinding, but a new press has "
                     + "already taken the input — leaving it open for that press\n").utf8))
                return
            }
            FileHandle.standardError.write(Data(
                ("[dictation-capture] the hold ended while rebinding — cooling down the "
                 + "input the rebind had just opened\n").utf8))
            await coolDown()
            return
        }
        // Only now may audio reach a preview run again: a new run can carry no
        // fragment of the discarded one.
        if reopened { onInterruptionRebound?() }
    }

    // MARK: - The watchdog's critical sections, as synchronous helpers

    private struct WatchdogSnapshot {
        let engine: MicCapture
        let generation: Int
        let graceStartedAt: TimeInterval
        let lastChunkAt: TimeInterval
        let graceSeconds: Double
        let boundUID: String?
        let interruptionsServed: Int
        let lastRosterCheckAt: TimeInterval
        let lastRosterPresent: Bool
        let staleFlagSince: TimeInterval?
    }

    /// Nil when the watchdog is no longer armed for this press.
    private func watchdogSnapshotLocked() -> WatchdogSnapshot? {
        lock.lock(); defer { lock.unlock() }
        guard holdWatchActiveLocked else { return nil }
        return WatchdogSnapshot(
            engine: mic,
            generation: holdGenerationLocked,
            graceStartedAt: holdGraceStartedAtLocked,
            lastChunkAt: lastChunkAtLocked,
            graceSeconds: holdGraceSecondsLocked,
            boundUID: holdBoundUIDLocked,
            interruptionsServed: interruptionsThisPressLocked,
            lastRosterCheckAt: lastRosterCheckAtLocked,
            lastRosterPresent: lastRosterPresentLocked,
            staleFlagSince: staleFlagSinceLocked)
    }

    /// Stamps (or clears) the moment the configuration-changed flag went up.
    /// Returns the timestamp the verdict should use.
    private func noteStaleFlagLocked(
        _ flagged: Bool, since: TimeInterval?, at now: TimeInterval
    ) -> TimeInterval? {
        guard flagged else {
            // The flag cleared — a `WarmEngineMonitor` verdict re-validated the
            // binding. Forget the window entirely; the next rise starts over.
            if since != nil { lock.lock(); staleFlagSinceLocked = nil; lock.unlock() }
            return nil
        }
        if let since { return since }
        lock.lock(); staleFlagSinceLocked = now; lock.unlock()
        return now
    }

    private func recordRosterCheckLocked(present: Bool, at now: TimeInterval) {
        lock.lock()
        lastRosterPresentLocked = present
        lastRosterCheckAtLocked = now
        lock.unlock()
    }

    /// True when NO new press owns the input — i.e. the engine a superseded
    /// rebind opened is nobody's, and giving it back is safe.
    private func shouldCoolSupersededEngineLocked() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return !holdWatchActiveLocked && !isRetaining
    }

    private func isCurrentGenerationLocked(_ generation: Int) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return holdWatchActiveLocked && holdGenerationLocked == generation
    }

    private func endHoldOnSecondInterruptionLocked() {
        lock.lock()
        holdWatchActiveLocked = false
        isRetaining = false
        buffer.removeAll(keepingCapacity: true)
        lock.unlock()
    }

    /// Opens the interruption: counts it, discards the partial utterance, and
    /// writes a PROVISIONAL record immediately.
    ///
    /// Provisional-and-immediate because `stop()` reads
    /// `lastInterruption()` to decide the press's outcome, and a release that
    /// lands mid-rebind must still be reported as `mic-changed` rather than as
    /// the `noAudio` an empty buffer would otherwise produce. The rebind's
    /// result (target, success, device name) is filled in later by
    /// `finishInterruption`, and only if this press is still the current one.
    private func beginInterruptionLocked(reason: String) -> (atMs: Double, previousUID: String?) {
        lock.lock(); defer { lock.unlock() }
        interruptionsThisPressLocked += 1
        // The partial hold is gone. Nothing captured before an interruption
        // may be transcribed: half an utterance pasted into the user's
        // document is worse than asking them to say it again.
        buffer.removeAll(keepingCapacity: true)
        let atMs = max(0, clock() - pressHoldStartLocked) * 1000
        interruptionLocked = Interruption(
            reason: reason,
            atMs: atMs,
            previousDeviceUID: holdBoundUIDLocked,
            rebindTargetUID: nil,
            rebindSucceeded: false,
            deviceName: "")
        return (atMs, holdBoundUIDLocked)
    }

    /// Publishes the rebind — or refuses to, when the press it belongs to is
    /// over. Returns whether anything was written.
    private func finishInterruption(
        _ record: Interruption, reopened: Bool, generation: Int, graceSeconds: Double,
        boundUID: String?, at now: TimeInterval
    ) -> Bool {
        lock.lock(); defer { lock.unlock() }
        // A release bumps `holdGenerationLocked`, so a superseded rebind sets
        // NOTHING: not `isRetaining` (which would wedge the idle mic release),
        // not `holdWatchActiveLocked`, and not `interruptionLocked` (which
        // `beginPress` may already have cleared for the NEXT press — writing it
        // here would report a healthy hold as `mic-changed`).
        guard holdGenerationLocked == generation else { return false }
        // The hold is still physically held, so keep retaining — `coolDown()`
        // inside the restart cleared the flag. What reaches the buffer from
        // here is still discarded at `stop()` (see the `interruption != nil`
        // branch there): this press is spent either way, and retaining keeps
        // the watchdog able to notice a SECOND death.
        if reopened { isRetaining = true }
        holdWatchActiveLocked = reopened
        lastChunkAtLocked = 0
        holdGraceSecondsLocked = graceSeconds
        holdGraceStartedAtLocked = now
        holdBoundUIDLocked = boundUID
        lastRosterCheckAtLocked = now
        lastRosterPresentLocked = true
        staleFlagSinceLocked = nil
        interruptionLocked = record
        return true
    }

    /// The ENGINE half of a rebind: give the device back, then open again on
    /// the new target. Separated so the test seam replaces only the part that
    /// needs real hardware.
    private func restartCaptureForInterruption(targetUID: String?) async -> Bool {
        await coolDown()
        do {
            try warmUp(bindDeviceUID: targetUID)
            return true
        } catch {
            FileHandle.standardError.write(Data(
                ("[dictation-capture] interruption rebind to \(targetUID ?? "(unresolved)") "
                 + "failed: \(error)\n").utf8))
            return false
        }
    }

    /// True when the rebind put a real, named input in place — i.e. when it is
    /// honest to tell the user "mic changed to X". Nothing bound (a failed
    /// rebind, or an engine that cannot name its device) is the case W5 exists
    /// for, and the caller must then say "Mic changed" with no name at all.
    public static func boundNameIsUsable(_ name: String) -> Bool {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        return !trimmed.isEmpty && trimmed != "(unbound)"
    }

    /// The interruption record of the CURRENT/last press, or nil when the
    /// hold ran to completion on one device. Read by the controller to raise
    /// `DictationFailure.micChanged`.
    public func lastInterruption() -> Interruption? {
        lock.lock(); defer { lock.unlock() }
        return interruptionLocked
    }

    /// Opens a forensic record for ONE press: resets the per-chunk
    /// accumulators and stamps the two clocks.
    ///
    /// Cheap ON PURPOSE — no engine calls, no CoreAudio property reads. The
    /// device identity, gain, transport and rates are gathered at `stop()`
    /// instead, where a few property reads cost nothing anyone is timing.
    /// They survive the trip: `MicCapture.stop()` finishes the stream and
    /// tears down the graph but does NOT clear `boundDeviceID`, so the
    /// engine can still name the device it captured from after it has been
    /// stopped — which is the device the record must name, not whatever is
    /// default a second later.
    private func beginPress(wasWarm: Bool, preRollSamples: Int) {
        lock.lock()
        pressDeviceLocked = DictationCaptureForensics(
            startedAt: Date(),
            wasWarm: wasWarm,
            preRollSamples: preRollSamples)
        pressHoldStartLocked = clock()
        pressEngineGenerationLocked = mic.warmEngineGeneration()
        pressChunkCountLocked = 0
        pressSilentChunkCountLocked = 0
        pressChunkPeaksLocked = []
        pressTraceIndexLocked = 0
        // A press starts uninterrupted. Cleared HERE rather than only in
        // `armWatchdog()` so the cold `start()` path — which does not arm the
        // watchdog — cannot inherit the previous press's verdict and report a
        // fresh hold as `mic-changed`.
        interruptionLocked = nil
        lock.unlock()
    }

    /// Trigger-up. Always stops RETAINING (returns the utterance). Whether
    /// the ENGINE itself stops is the caller's call:
    /// - `keepWarm: true` (the warm path) — engine keeps running, buffer
    ///   resets to filling the pre-roll ring for the next arm. Caller is
    ///   responsible for eventually calling `coolDown()` once idle.
    /// - `keepWarm: false` (the cold path, and the warm path's own eventual
    ///   idle-timeout) — engine stops, matching the original `stop()`
    ///   contract exactly.
    public func stop(keepWarm: Bool = false) async -> CaptureResult {
        // FIRST: the watchdog's window is `armTriggerDown()`...`stop()`, and a
        // tick that fired mid-teardown would rebind an input nobody is
        // holding any more.
        disarmWatchdog()
        endRetainingLocked()

        if !keepWarm {
            takeMicForStop().stop()
            // `mic.stream`'s continuation is finished inside `mic.stop()`,
            // but `AsyncStream` is unbounded-buffered by default: any chunk
            // already yielded before `finish()` is still delivered, so
            // awaiting the collector task's completion (rather than racing
            // it) is what makes this capture the FULL utterance, not "most
            // of it."
            //
            // BOUNDED, since D15. A rebind in flight can replace `collectTask`
            // with one attached to an engine this `stop()` never stopped —
            // whose stream nothing will ever finish — and an unbounded await
            // there is a trigger-up that never returns. The bound is a full
            // second, i.e. thousands of times the normal drain, and it says so
            // in the log when it expires rather than failing silently.
            await drainCollector(reason: "stop")
            markColdLocked()
        }

        let interruption = lastInterruption()
        // An interrupted press has NO utterance, by design. Whatever the new
        // device delivered after the rebind is a fragment of a sentence the
        // user is about to be asked to repeat; handing it to ASR would paste
        // half a thought into their document. The forensics record measures
        // the same empty clip, so the record and the result cannot disagree —
        // `interruptedAtMs`/`rebindTargetUID`/`rebindSucceeded` (and the
        // per-chunk counts, which DID accumulate) are what explain it.
        if interruption != nil { discardUtteranceLocked() }
        let samples = snapshotLocked()
        recordCaptureSummaryLocked(samples)
        // Built AFTER the collector task has been awaited on the cold path,
        // for the same reason that await exists: `AsyncStream` is
        // unbounded-buffered, so chunks yielded before `finish()` are still
        // in flight. A record assembled before the drain would under-count
        // the tail of the very utterance it is describing.
        //
        // `mic` still names the device it captured from here — `MicCapture.
        // stop()` finishes the stream and tears the graph down without
        // clearing `boundDeviceID`.
        let forensics = recordForensics(samples: samples, engine: currentMic())
        return CaptureResult(
            samples: samples, sampleRate: 16_000, forensics: forensics,
            interruption: interruption)
    }

    private func discardUtteranceLocked() {
        lock.lock(); buffer.removeAll(keepingCapacity: true); lock.unlock()
    }

    /// The `MicCapture` currently in hand, spent or not. Separate synchronous
    /// helper for the same Swift 6 reason as every other one in this file:
    /// raw `NSLock` calls are forbidden inside an `async` body.
    private func currentMic() -> MicCapture {
        lock.lock(); defer { lock.unlock() }
        return mic
    }

    /// Closes the forensic record for this press, stores it, and logs ONE
    /// line for it — unconditionally, on every press, success or failure.
    ///
    /// Unconditional is the requirement, not a default: the finding that
    /// cracked the 2026-09-03 Bluetooth defect was that the exact-zero
    /// lead-in is present on the SUCCESSFUL headset clips too (10 of 50
    /// carry >0.3 s, median 0.68 s), which is what proved the wake-up is
    /// the device's normal behaviour and the failure is a hold that landed
    /// inside it. Logging only failures would have hidden exactly that, and
    /// left "nothing heard" looking like a user who did not speak.
    ///
    /// One line, log-safe, no transcript text and no audio content — see
    /// `DictationCaptureForensics`. It is a per-press line at human key-press
    /// rates, not a per-chunk one; the per-chunk firehose is behind
    /// `DICTATION_CAPTURE_TRACE=1`.
    @discardableResult
    private func recordForensics(samples: [Float], engine: MicCapture) -> DictationCaptureForensics {
        // ---- The measurement that identified the defect ----------------
        //
        // Counted on the REAL retained buffer — the same array handed to
        // ASR — and with `== 0`, not "below a floor". A quiet room measures
        // small non-zero numbers; a device that has not woken up measures
        // literal `0`. Estimating this from chunk peaks would round the two
        // together and lose the entire distinction, and a run that spans a
        // chunk boundary (0.56-1.23 s is 7-15 chunks) would be invisible to
        // any per-chunk sum.
        var leadingZeros = 0
        var stillLeading = true
        var peak: Float = 0
        var sumSquares: Double = 0
        for sample in samples {
            if stillLeading {
                if sample == 0 { leadingZeros += 1 } else { stillLeading = false }
            }
            let magnitude = abs(sample)
            if magnitude > peak { peak = magnitude }
            sumSquares += Double(sample) * Double(sample)
        }
        // `stillLeading` never cleared means the run reached the end: every
        // sample was exactly zero. A dead stream, not a quiet room — and
        // per the product policy that most holds contain a voice, a fault that
        // must be detectable immediately, with no threshold to argue about.
        let allZero = !samples.isEmpty && stillLeading
        let rate = Double(constants.sampleRate)
        let duration = rate > 0 ? Double(samples.count) / rate : 0
        let rms = samples.isEmpty ? Float(0) : Float((sumSquares / Double(samples.count)).squareRoot())

        // ---- Device identity, resolved once, at stop -------------------
        //
        // `deviceUID`/`deviceNominalRate` come from the device a fresh
        // `start()` would bind RIGHT NOW, which is the only device id this
        // module can reach (`MicCapture.boundDeviceID` is private). For a
        // healthy binding that is the bound device; when it is not, that
        // divergence is itself the report — `isInputStaleWhileWarm` names
        // it, and 13 of 26 XM5 binds logged "stale at trigger", each
        // rebinding to the device it was already bound to.
        let target = MicCapture.resolvedTargetInputDeviceID()

        lock.lock()
        var record = pressDeviceLocked
        record.engineRestartsDuringPress = max(0, engine.warmEngineGeneration() - pressEngineGenerationLocked)
        record.chunkCount = pressChunkCountLocked
        record.silentChunkCount = pressSilentChunkCountLocked
        record.chunkPeaks = pressChunkPeaksLocked
        record.holdSeconds = max(0, clock() - pressHoldStartLocked)
        // D15: the watchdog's verdict travels with the numbers it explains.
        if let interruption = interruptionLocked {
            record.interruptedAtMs = interruption.atMs
            record.rebindTargetUID = interruption.rebindTargetUID
            record.rebindSucceeded = interruption.rebindSucceeded
            record.interruptedFromDeviceUID = interruption.previousDeviceUID
        }
        lock.unlock()

        record.deviceName = engine.boundInputDeviceName()
        // The BOUND device's UID first (`engine.boundDeviceUID()`), because
        // under an explicit `bindNextStart` the bound device and the
        // resolved target legitimately differ — and this UID keys the
        // audio-source evidence store ("which mic last heard a voice").
        // The resolved target remains the fallback for an unbound engine.
        // ON AN INTERRUPTED PRESS THIS IS THE **NEW** DEVICE. It is read at
        // stop, and by then a D15 rebind has moved the binding — so a consumer
        // that wants "which microphone is this press evidence about" must read
        // `interruptedFromDeviceUID` first, as
        // `DictationController.reportHoldToSourceAdapter` does. Kept as-is
        // rather than rewritten to the old device because this field's contract
        // is "the device the capture was bound to when it stopped", which other
        // readers (the mic-test panel, the Recent detail) depend on.
        record.deviceUID = engine.boundDeviceUID() ?? MicCapture.deviceUID(target) ?? "(unknown)"
        record.transport = engine.boundDeviceTransport()
        // The rate the retained samples are actually in — i.e. the rate
        // `leadingZeroSeconds` below was divided by, and the rate
        // `CaptureResult.sampleRate` reports.
        record.engineFormatRate = rate
        record.deviceNominalRate = MicCapture.nominalSampleRate(target)
        // Always false on this path, and recorded as measured rather than
        // assumed: `init` builds its `MicCapture` with
        // `voiceProcessingAllowed: false` (the voice processor's noise gate
        // ate normal-volume speech, 2026-08-09). "The AEC gate clamped the
        // mic" and "the headset had not woken up" produce the same near-zero
        // peak; the cheap way to keep telling them apart is to have written
        // down which one was switched on.
        record.echoCancellationActive = false
        record.gainDB = engine.currentGainDB()
        record.engineStartMs = engine.lastEngineStartMs()
        record.firstBufferMs = engine.lastFirstBufferMs()
        // The number the arrival latch could not see. `awaitFirstBuffer`
        // waits for the first BUFFER, and on the XM5 buffers arrived
        // punctually and full of zeros; the gap between this and
        // `firstBufferMs` IS the silent wake-up.
        record.firstAudibleMs = engine.lastFirstAudibleMs()
        record.sampleCount = samples.count
        record.durationSeconds = duration
        record.leadingZeroSamples = leadingZeros
        record.leadingZeroSeconds = rate > 0 ? Double(leadingZeros) / rate : 0
        record.allZero = allZero
        record.peak = peak
        record.rms = rms
        // `outcome`/`gateDecision` stay empty here by design: capture does
        // not know them. The caller that runs the gate fills them in on the
        // copy it holds — which is why the record rides on `CaptureResult`.

        lock.lock(); lastForensicsLocked = record; lock.unlock()

        FileHandle.standardError.write(
            Data(("[dictation-capture] " + record.summaryLine() + "\n").utf8))
        return record
    }

    /// The forensic record of the LAST completed `stop()`, or nil before the
    /// first one.
    ///
    /// A method rather than a property to match `boundInputDeviceName()` and
    /// the rest of the diagnostics surface, and because it is a snapshot
    /// under a lock, not a stored value a caller should read repeatedly in a
    /// loop.
    public func lastForensics() -> DictationCaptureForensics? {
        lock.lock(); defer { lock.unlock() }
        return lastForensicsLocked
    }

    /// O(n) over one utterance (a few seconds of 16 kHz mono) — trivial,
    /// and only paid once per dictation, at stop.
    private func recordCaptureSummaryLocked(_ samples: [Float]) {
        var peak: Float = 0
        for sample in samples {
            let magnitude = abs(sample)
            if magnitude > peak { peak = magnitude }
        }
        let seconds = Double(samples.count) / 16_000
        let summary = "\(samples.count) samples ("
            + String(format: "%.2f", seconds) + "s), peak "
            + String(format: "%.4f", peak)
        lock.lock()
        lastCaptureSummaryLocked = summary
        lastCapturePeakLocked = peak
        lastCaptureSampleCountLocked = samples.count
        lock.unlock()
    }

    /// Explicit engine stop for a warm-but-idle capture. Never called
    /// automatically by this type — see the type doc comment's mic-indicator
    /// note. A caller wiring this in should poll (or timer-drive)
    /// `shouldCoolDown(idleSeconds:)` and call this when it returns true.
    public func coolDown() async {
        guard endWarmLocked() else { return }
        takeMicForStop().stop()
        await drainCollector(reason: "coolDown")
    }

    /// Waits for the capture collector to finish, for at most
    /// `collectorDrainBoundSeconds` of REAL time (never the injected clock —
    /// this is a bound on a thread, not a rule about a hold).
    ///
    /// Polls a flag rather than awaiting `Task.value` because awaiting a
    /// non-throwing task cannot be given a deadline: `withTaskGroup` would
    /// still wait for the child at the end of its scope, which is the very
    /// stall this exists to bound.
    private func drainCollector(reason: String) async {
        guard collectorIsRunningLocked() else { return }
        let deadline = Date().addingTimeInterval(Self.collectorDrainBoundSeconds)
        while collectorIsRunningLocked() {
            if Date() >= deadline {
                FileHandle.standardError.write(Data(
                    ("[dictation-capture] \(reason): the capture collector did not drain within "
                     + String(format: "%.1f", Self.collectorDrainBoundSeconds)
                     + "s — continuing without it (a stream was left live by a rebind)\n").utf8))
                return
            }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
    }

    private func collectorIsRunningLocked() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return collectorsRunningLocked > 0
    }

    // See `appendLocked`'s comment: Swift 6 forbids raw NSLock calls
    // directly inside `async` function bodies, so every critical section
    // touched from an `async` method is wrapped in a synchronous helper.
    private func endRetainingLocked() {
        lock.lock(); isRetaining = false; lastActivity = clock(); lock.unlock()
    }

    private func markColdLocked() {
        lock.lock(); isWarm = false; lock.unlock()
    }

    private func endWarmLocked() -> Bool {
        lock.lock()
        let wasWarm = isWarm
        isWarm = false
        isRetaining = false
        lock.unlock()
        return wasWarm
    }

    /// Pure decision, no clock/timer wiring of its own: has it been at
    /// least `idleSeconds` since the last `warmUp()`/`armTriggerDown()`/
    /// `stop()` activity? Exposed so a caller's timer policy can decide when
    /// to `coolDown()` without this type running a background timer of its own.
    /// Test seams for the idle-release guard. Setting the real state needs a
    /// live `AVAudioEngine`; the guard's logic does not, and it is the logic
    /// that shipped an outage.
    func markWarmForTesting() {
        lock.lock(); isWarm = true; lastActivity = 0; lock.unlock()
    }

    func armTriggerDownForTesting() {
        lock.lock(); isWarm = true; isRetaining = true; lastActivity = 0; lock.unlock()
    }

    public func shouldCoolDown(idleSeconds: Double) -> Bool {
        lock.lock()
        let warm = isWarm
        let last = lastActivity
        let capturing = isRetaining
        lock.unlock()
        guard warm else { return false }
        // NEVER release a microphone that is currently recording.
        //
        // `lastActivity` is stamped on warmUp, armTriggerDown and stop — and
        // at no point while audio is actually arriving. So a held trigger
        // looks exactly like an idle engine after `idleSeconds`, and the
        // housekeeping tick closed the microphone MID-UTTERANCE.
        //
        // Latent until 2026-08-17, when `idleMicrophoneReleaseSeconds` went
        // from 15 s to 5 s: at 15 s it took an unusually long dictation to
        // hit, at 5 s it hits almost every real one. This behavior is
        // the same day — "it records for five, six, seven, eight seconds and
        // then it just stops, even while I'm holding it" — with the level
        // meter visibly frozen while he was still speaking, and it is the
        // true cause of the "second half gets eaten" truncation that three
        // separate decoder investigations chased.
        //
        // `isRetaining` is the capture's own in-progress flag, held under
        // the same lock as the rest of this state.
        guard !capturing else { return false }
        return (clock() - last) >= idleSeconds
    }

    /// The bound input's transport as `MicCapture.boundDeviceTransport()`
    /// names it (`bluetooth`, `built-in`, `usb`, …; `unknown` when nothing
    /// is bound).
    public var boundInputTransport: String {
        lock.lock()
        let engine = mic
        lock.unlock()
        return engine.boundDeviceTransport()
    }

    /// True when the engine is warm but its input binding went stale — the
    /// system default moved (the user unplugged their earphones), the bound
    /// device disappeared out from under the engine, or the explicit bind
    /// failed at start. A stale warm capture is the worst state this type
    /// has: everything LOOKS alive while the tap delivers silence. The
    /// trigger path checks this and rebuilds BEFORE capturing — the
    /// guarantee the housekeeping tick's once-a-second polled rebind cannot
    /// make on its own (the press can land inside the poll gap, or the tick
    /// can miss a change the CoreAudio listener never surfaced).
    public var isInputStaleWhileWarm: Bool {
        lock.lock()
        let warm = isWarm
        let engine = mic
        lock.unlock()
        return warm && engine.inputBindingIsStale()
    }

    /// Name of the device the current engine is bound to (diagnostics).
    public var boundInputName: String {
        lock.lock(); let engine = mic; lock.unlock()
        return engine.boundInputDeviceName()
    }

    /// One-line summary of the LAST completed `stop()` — sample count,
    /// duration, and peak amplitude. Diagnostics only; carries no dictated
    /// content. This is what turns a bare `nothingHeard` log line into a
    /// diagnosable one ("0.0002 peak on MacBook Pro Microphone" is a dead
    /// binding; "0.0000 samples" is a dead stream).
    public var lastCaptureSummary: String {
        lock.lock(); defer { lock.unlock() }
        return lastCaptureSummaryLocked
    }
    private var lastCaptureSummaryLocked = "(no capture yet)"

    /// Peak amplitude of the LAST completed `stop()`, structured rather than
    /// formatted.
    ///
    /// `lastCaptureSummary` already carries this, but only as text — and a
    /// caller that needs to BRANCH on it (a zero peak is a dead input, not a
    /// quiet room) would have to parse a human-readable string, which breaks
    /// the first time the format is prettied. Same value, same lock, no
    /// parsing.
    public var lastCapturePeak: Float {
        lock.lock(); defer { lock.unlock() }
        return lastCapturePeakLocked
    }
    private var lastCapturePeakLocked: Float = 0

    /// Sample count of the LAST completed `stop()`. Distinguishes "the stream
    /// was dead" (no samples) from "the stream ran and delivered silence"
    /// (samples, zero peak) — different causes with different fixes.
    public var lastCaptureSampleCount: Int {
        lock.lock(); defer { lock.unlock() }
        return lastCaptureSampleCountLocked
    }
    private var lastCaptureSampleCountLocked = 0

    /// Test seam: makes the current `MicCapture` report `isRunning` WITHOUT
    /// opening a device, so a check can exercise
    /// `inputStreamFlaggedStale()` (which is `isRunning && configurationChanged`)
    /// and therefore the watchdog's W1 flag-plus-silence conjunction. Pairs
    /// with `markInputConfigurationChangedForTesting()`; see
    /// `MicCapture.markRunningForTesting`'s doc comment for why it is safe only
    /// on a capture that has never really started.
    func markInputRunningForTesting() {
        lock.lock(); let engine = mic; lock.unlock()
        engine.markRunningForTesting()
    }

    /// Test seam: simulates the device disappearing under the warm engine.
    public func markInputConfigurationChangedForTesting() {
        lock.lock(); let engine = mic; lock.unlock()
        engine.markConfigurationChangedForTesting()
    }

    /// Identity of the `MicCapture` currently in hand.
    ///
    /// Exposed because "did the engine get REBUILT after being stopped?" is
    /// the whole of the silent-capture fix, and it is otherwise only
    /// observable by opening the microphone for real — which the self-checks
    /// deliberately refuse to do beside a live GUI instance.
    public var micIdentityForTesting: ObjectIdentifier {
        lock.lock(); defer { lock.unlock() }
        return ObjectIdentifier(mic)
    }

    /// Runs the REAL renewal decision `warmUp()`/`start()` run, without
    /// starting an engine. Returns true when a replacement was needed and
    /// made. Drives the product body rather than restating it, so a check
    /// cannot pass against logic the product does not run.
    @discardableResult
    public func renewInputForTesting() -> Bool {
        let before = micIdentityForTesting
        _ = renewMicIfSpent()
        return micIdentityForTesting != before
    }

    public var isWarmForTesting: Bool {
        lock.lock(); defer { lock.unlock() }
        return isWarm
    }

    /// Same read as `isWarmForTesting` — a plain, side-effect-free getter,
    /// not a testing-only shim — under a name that reads correctly at a
    /// production call site. `DictationSession.triggerDown()` uses this to
    /// decide warm (`armTriggerDown()`) vs. cold (`start()`) path; kept as
    /// a separate property rather than renaming `isWarmForTesting` itself
    /// so the existing self-check assertions don't need touching.
    public var isCurrentlyWarm: Bool { isWarmForTesting }

    /// Test seams for the hold lifecycle the D15 rebind writes to. `isRetaining`
    /// is the flag whose wrong value wedges the idle microphone release
    /// forever, and `holdWatchActive` is what says whether the watchdog owns
    /// this press — both are invisible from outside and both are what a
    /// superseded rebind must NOT set.
    var isRetainingForTesting: Bool {
        lock.lock(); defer { lock.unlock() }
        return isRetaining
    }

    var watchdogIsArmedForTesting: Bool {
        lock.lock(); defer { lock.unlock() }
        return holdWatchActiveLocked
    }

    /// Test seam: how much audio is retained for the press IN FLIGHT, before
    /// `stop()` snapshots it. The D15 discard is only provable against this —
    /// "the result is empty" is equally true of a buffer that was never
    /// filled.
    var retainedSampleCountForTesting: Int {
        lock.lock(); defer { lock.unlock() }
        return buffer.count
    }

    public var preRollSampleCountForTesting: Int {
        lock.lock(); defer { lock.unlock() }
        return preRoll.count
    }

    /// TESTING/MEASUREMENT ONLY — replaces the retained buffer's contents
    /// wholesale, so the NEXT `stop()` call returns exactly `samples`
    /// regardless of what the live mic actually captured. Never called from
    /// any production code path (nothing wires the hotkey to this).
    ///
    /// Why this exists: `--dictation-e2e`'s whole point is measuring the
    /// ASR + paste stages against real, content-bearing speech, but an
    /// unattended agent run has no human physically talking into the mic —
    /// a genuine live capture in that environment is near-silence, which
    /// would defeat the measurement (empty/near-empty transcripts) without
    /// proving anything false about the pipeline itself. This lets the
    /// harness drive `DictationCapture.start()`/`armTriggerDown()` and
    /// `stop()` FOR REAL (so their own timing is genuinely measured) while
    /// substituting the audio source with deterministic, known-content
    /// audio (macOS `say`-synthesized PCM) — the same substitution the task
    /// brief explicitly names as acceptable ("synthesize with macOS `say`
    /// into the capture path if you cannot get live mic input
    /// deterministically").
    public func injectSamplesForTesting(_ samples: [Float]) {
        lock.lock(); buffer = samples; lock.unlock()
    }

    // Swift 6's async-context checker forbids raw `NSLock.lock()/unlock()`
    // calls directly inside an `async` function body (region-isolation
    // analysis can't prove the lock is released on every suspension path)
    // — wrap each critical section in an ordinary synchronous method
    // instead, matching the `NSLock` + helper-method style used throughout
    // `DictationAudioCapture`.
    @discardableResult
    private func appendLocked(_ chunk: [Float]) -> Bool {
        // ---- Per-chunk forensics ---------------------------------------
        //
        // Measured HERE — on the collector task, one hop downstream of the
        // tap — and never inside `MicCapture.handleInputBuffer`, which runs
        // on CoreAudio's realtime IO thread. That thread must not allocate,
        // lock, or write to stderr; the 2026-08-21 crash in `MicCapture.
        // stop()`'s comment is what a realtime-thread violation costs (an
        // uncatchable NSException on a CoreAudio thread = the process gone).
        // This loop is a scan of ~85 ms of 16 kHz mono per callback, off
        // the realtime thread, and it is the whole per-chunk cost.
        //
        // The leading-zero count is per-chunk here so the trace timeline can
        // show a device waking up chunk by chunk. It is NOT the number that
        // goes in the record — see `recordCaptureSummaryLocked`, which
        // counts the run on the real retained buffer, because a run that
        // spans a chunk boundary is invisible to any per-chunk sum.
        var chunkPeak: Float = 0
        var chunkLeadingZeros = 0
        var stillLeading = true
        for sample in chunk {
            if stillLeading {
                if sample == 0 { chunkLeadingZeros += 1 } else { stillLeading = false }
            }
            let magnitude = abs(sample)
            if magnitude > chunkPeak { chunkPeak = magnitude }
        }

        lock.lock()
        // D15's liveness stamp. Every chunk, retained or pre-roll: the
        // watchdog asks "is audio still ARRIVING", which is a question about
        // the device, not about which buffer the audio lands in.
        lastChunkAtLocked = clock()
        let retained = isRetaining
        if retained {
            buffer.append(contentsOf: chunk)
            pressChunkCountLocked += 1
            if chunkPeak <= DictationCaptureForensics.audibleThreshold {
                pressSilentChunkCountLocked += 1
            }
            // FIRST N, not last N. The wake-up is at the head of the
            // capture (0.56-1.23 s measured), so the head is the part that
            // identifies the defect; the tail is the part every clip agrees
            // on. See `DictationCaptureForensics.chunkPeaks`.
            if pressChunkPeaksLocked.count < DictationCaptureForensics.chunkPeakCap {
                pressChunkPeaksLocked.append(chunkPeak)
            }
        } else {
            preRoll.append(chunk)
        }
        let traceIndex = pressTraceIndexLocked
        pressTraceIndexLocked += 1
        lock.unlock()

        // Excessive-logging mode, off by default: `DICTATION_CAPTURE_TRACE=1`.
        // ~12-20 lines a second for every dictation anyone ever does, which
        // is why it is gated — and which is also exactly what is needed to
        // read a wake-up ("chunk 0..6 peak 0.0000 zeros=1360, chunk 7 peak
        // 0.1120") instead of guessing at it from one summary line.
        // Written outside the lock: stderr is slow enough that holding the
        // audio lock across it would make the trace change the timing it is
        // there to observe.
        // `if`, not `guard ... else { return }`: this function now returns
        // whether the chunk was retained (upstream's `routeChunk` needs it),
        // so an early return here would silently report "not retained" for
        // every chunk whenever tracing was off.
        if DictationCaptureForensics.traceEnabled {
            let line = "[dictation-capture] chunk \(traceIndex) "
                + (retained ? "retained" : "pre-roll")
                + " samples=\(chunk.count)"
                + " peak=" + String(format: "%.4f", chunkPeak)
                + " zeros=\(chunkLeadingZeros)"
                + (chunkLeadingZeros == chunk.count ? " ALL-ZERO" : "")
                + "\n"
            FileHandle.standardError.write(Data(line.utf8))
        }
        return retained
    }

    // Orders the pre-roll callback with collector callbacks without holding
    // the capture state lock across a consumer's bounded copy.
    private let retainedDeliveryLock = NSLock()

    private func routeChunk(_ chunk: [Float]) {
        retainedDeliveryLock.lock()
        defer { retainedDeliveryLock.unlock() }
        if appendLocked(chunk) {
            onRetainedAudio?(chunk, constants.sampleRate)
        }
    }

    private func markCollectorRunningLocked(_ running: Bool) {
        lock.lock()
        collectorsRunningLocked = max(0, collectorsRunningLocked + (running ? 1 : -1))
        lock.unlock()
    }

    private func snapshotLocked() -> [Float] {
        lock.lock(); defer { lock.unlock() }
        return buffer
    }

    private func startCollecting(on mic: MicCapture) -> Task<Void, Never> {
        markCollectorRunningLocked(true)
        return Task { [weak self, mic] in
            guard let self else { return }
            // The flag, not the task handle, is what `drainCollector` waits on:
            // `collectTask` can be REPLACED by a rebind, and the old handle is
            // then unreachable while its loop is still running.
            defer { self.markCollectorRunningLocked(false) }
            for await chunk in mic.stream {
                self.routeChunk(chunk)
                // Peak is computed ONLY when someone is listening for it —
                // see `onAudioLevel`'s doc comment.
                if let sink = self.onAudioLevel {
                    var peak: Float = 0
                    for sample in chunk {
                        let magnitude = abs(sample)
                        if magnitude > peak { peak = magnitude }
                    }
                    sink(peak)
                }
            }
        }
    }
}
