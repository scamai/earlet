import AVFoundation
import CoreAudio
import Foundation

/// Permission state for microphone capture, local to this module.
public enum MicPermissionStatus: Sendable, Equatable {
    case authorized
    case denied
    case notDetermined
}

/// Errors surfaced by `MicCapture`.
public enum MicCaptureError: Error, CustomStringConvertible, Sendable {
    case permissionDenied
    case engineStartFailed(String)
    case converterCreationFailed
    case conversionFailed(String)
    /// The engine's input format could not be reconciled with the bound
    /// device's own rate. Its own case because it is the ONE failure that
    /// used to be invisible: a tap installed across this gap delivers zero
    /// buffers forever, with no error, which the whole stack then reported
    /// as "the microphone heard nothing". See `reconcileInputFormat`.
    case inputFormatMismatch(device: String, deviceRate: Double, engineRate: Double)
    /// CoreAudio refused to point the unit at an input device, and no other
    /// device could be bound either.
    ///
    /// Its own case because the alternative — what this code did until
    /// 2026-08-20 — was to log the refusal and carry on. The engine then
    /// started "warm" while bound to nothing, the tap delivered whatever the
    /// HAL had been left on (0.30 s at peak 0.0029, below the audible floor),
    /// and the user was told `nothingHeard`. They had spoken; the microphone
    /// had never opened. Observed on this machine after several AirPods ⇄
    /// built-in switches: status 'nope'
    /// (`kAudioHardwareIllegalOperationError`), then every subsequent press
    /// reported `bound=(unbound)` and the rebuild-on-next-trigger path
    /// re-ran the same failing bind forever.
    case inputDeviceBindFailed(device: String, status: OSStatus)

    public var description: String {
        switch self {
        case .permissionDenied: return "Microphone access was not granted."
        case .engineStartFailed(let m): return "AVAudioEngine failed to start: \(m)"
        case .converterCreationFailed: return "Could not build a converter to mono 16 kHz Float32."
        case .conversionFailed(let m): return "Audio conversion failed: \(m)"
        case .inputDeviceBindFailed(let device, let status):
            return "The microphone never opened: macOS refused to bind \(device) "
                + "(status \(status)). Nothing was recorded, so this is not a "
                + "question of speaking louder."
        case .inputFormatMismatch(let device, let deviceRate, let engineRate):
            return "The audio engine is still configured for \(Int(engineRate)) Hz "
                + "while \(device) runs at \(Int(deviceRate)) Hz. Capturing across "
                + "that gap records nothing at all, so this start was refused."
        }
    }
}

/// Microphone capture via `AVAudioEngine`'s input tap, resampled to mono
/// 16 kHz Float32 — the format `UtteranceSegmenter` and
/// `transcription_worker` both expect. One `MicCapture` instance == one
/// `AudioChannel.mic` stream.
///
/// TCC: reading the mic requires `NSMicrophoneUsageDescription` in the
/// bundled `.app`'s Info.plist (added when the P1 capture UI lands) and a
/// granted `AVAudioApplication` permission. `requestPermission()` drives
/// the system prompt; this class does not fabricate audio if permission is
/// denied — `start()` throws instead.
public final class MicCapture: @unchecked Sendable {
    private let engine = AVAudioEngine()
    /// The `AVAudioEngine` path as a backend (D13). Lazy, not a `let`, only
    /// because `engine` cannot be read during this class's initialization
    /// phase 1 — there is exactly one of these per `MicCapture`, for the same
    /// reason there is exactly one engine: the
    /// `.AVAudioEngineConfigurationChange` observer is registered against
    /// that instance in `init`.
    private lazy var engineBackend = EngineBackend(engine: engine)
    /// The backend the RUNNING capture is using, or nil while stopped. Set in
    /// `startEngineLocked`, read by `stop()`; guarded by `lock` like every
    /// other piece of start/stop state.
    private var activeBackend: MicInputBackend?
    /// `activeBackend`'s name, kept AFTER `stop()` clears the backend —
    /// `audio-capture-probe --route-sequence` prints which backend a phase
    /// ran on once the phase is over, and a caller asking "what did that
    /// press use" must not get "(none)" back just because the capture has
    /// since been torn down.
    private var lastBackendName = "(none)"
    /// D14 (minimum): the property-listener registrations this instance
    /// installed on the BOUND device for the HAL backend.
    ///
    /// The C listener API (`AudioObjectAddPropertyListener`), not the block
    /// one: the block API's listener parameter is a thick Swift function, so
    /// Swift re-bridges a FRESH block at each call site and a remove can never
    /// match its add — silently, because remove returns `noErr` for a block it
    /// never knew. See `BoundDeviceListenerBox`. A `@convention(c)` proc plus
    /// a `clientData` pointer has identity by construction.
    ///
    /// Cleared in `stop()`, inside `runWithTimeout` —
    /// `AudioObjectRemovePropertyListener` is a HAL call and no HAL call in
    /// this class may run under `lock` outside that bound.
    private var boundDeviceListenerAddresses: [AudioObjectPropertyAddress] = []
    private var boundDeviceListenerBox: BoundDeviceListenerBox?
    /// The exact `clientData` pointer the registration carries — the `+1` from
    /// `Unmanaged.passRetained`. Removal must pass this same pointer (identity),
    /// and must release it only after `BoundDeviceListenerBox.releaseGrace`.
    private var boundDeviceListenerClientData: UnsafeMutableRawPointer?
    private var boundDeviceListenerDevice: AudioDeviceID?
    /// The sample rate THIS capture's client format is set to, as opposed to
    /// the device's own nominal rate (`boundDeviceNominalRateAtBind`). The two
    /// are equal at bind and DIVERGE on Bluetooth, because the unit opening the
    /// device provokes the A2DP→HFP flip: the client format keeps the pre-flip
    /// rate (the AUHAL converts on its input bus) while the device settles on
    /// the post-flip one.
    ///
    /// **Diagnostics only.** It used to key the no-op rate filter, and that was
    /// a blind spot on the one transition that matters (fix round 2, N2): with a
    /// 44.1 kHz client format over a 16 kHz HFP device, an HFP→A2DP return
    /// posting 44 100 was dropped as "a rate we are already on" while
    /// `WarmEngineMonitor` would have called it a change against its 16 000
    /// baseline — so the classic stale-format death reported itself healthy. The
    /// filter is keyed to `boundDeviceNominalRateAtBind` now, the same baseline
    /// the verdict machinery uses. This value survives because the log line that
    /// explains a dropped or credited event is unreadable without both numbers.
    /// Cleared in `stop()` with the rest of the per-capture state.
    private var halClientFormatRate: Double = 0
    /// Serializes HAL starts and lets an abandoned one discover it was
    /// superseded, so a start the caller gave up on cannot leave a second live
    /// AUHAL unit holding the microphone. See `HALStartGate`.
    private let halStartGate = HALStartGate()
    /// Where the bound-device property listeners fire. A serial queue of our
    /// own rather than nil (which would run them on an arbitrary HAL thread)
    /// so the listener body — which takes `lock` — can never run two at once
    /// and can never run on the IO thread.
    private let deviceListenerQueue = DispatchQueue(
        label: "ai.scam.dictation.mic.device-listeners", qos: .utility)
    private let targetFormat: AVAudioFormat
    private var converter: AVAudioConverter?
    private var isRunning = false
    private let lock = NSLock()
    /// The device the running engine's input unit was bound to at start.
    /// Nil while stopped, and nil when the explicit bind FAILED — in which
    /// case the engine is latched to whatever CoreAudio left it on, which
    /// is exactly the state `inputBindingIsStale()` must report.
    private var boundDeviceID: AudioDeviceID?
    /// The UID of `boundDeviceID`, resolved ONCE at bind time inside
    /// `startEngineLocked` — i.e. inside `start()`'s `runWithTimeout` bound.
    ///
    /// `Self.deviceUID` is a Core Audio property read, and the whole point of
    /// `runWithTimeout` is that no HAL call in this class may run unbounded
    /// while `lock` is held: a HAL that stalls (the 6 s Bluetooth contention
    /// `start()` documents) would otherwise hang the caller with the lock
    /// held and nothing to time it out. `stop()` and the ownership-shim
    /// bookkeeping both need this UID while holding `lock` and OUTSIDE any
    /// bound, so they read this stashed value instead of asking the HAL
    /// again. Cleared with `boundDeviceID`.
    private var boundDeviceUIDAtBind: String?
    /// Set by the `.AVAudioEngineConfigurationChange` observer. CoreAudio
    /// posts it when the bound device disappears (headphones unplugged) or
    /// its format changes under the engine — after which the running tap
    /// can deliver silence forever. A capture that saw this must be rebuilt,
    /// not trusted.
    private var configurationChanged = false
    private var configObserver: NSObjectProtocol?
    /// The device's own nominal sample rate at the moment THIS engine last
    /// bound it — captured in `startEngineLocked` next to `deviceRate`.
    /// `WarmEngineMonitor` compares a fresh read of the same HAL property
    /// against this to answer "format changed" without touching
    /// `AVAudioEngine` off the audio thread (see that class's doc comment).
    private var boundDeviceNominalRateAtBind: Double = 0
    /// Re-validates a same-device `.AVAudioEngineConfigurationChange`
    /// instead of trusting the flag unconditionally — see `WarmEngineMonitor`
    /// and spec v3 §14. One per `MicCapture` instance, same lifetime as the
    /// engine it watches.
    private let warmEngineMonitor = WarmEngineMonitor()
    /// 0.002 linear amplitude — the same floor `SegmenterConstants.silenceRMS`
    /// / `AsrGatePolicy` use elsewhere in this codebase (pinned equal by
    /// `PipelineParitySelfCheck`). `WarmEngineMonitor` reuses it rather than
    /// inventing a second "is this audible" threshold.
    static let floorAmplitude: Float = 0.002

    /// What the last `start()` measured about this device's wake-up, all in
    /// milliseconds and all guarded by `lock` like the rest of the state.
    /// Three separate numbers because they are three separate diagnoses: a
    /// device that never delivers a buffer is broken, a device that delivers
    /// buffers full of zeros is asleep, and a `start()` that takes seconds
    /// while both are fast is HAL contention (`runWithTimeout`). Collapsing
    /// them into one "how long did the mic take" is what let the WH-1000XM5's
    /// silent wake-up hide behind a punctual first buffer for weeks.
    private var firstBufferMs: Double?
    private var firstAudibleMs: Double?
    private var engineStartMs: Double?

    /// Input gain for the CURRENTLY BOUND device, as a linear multiplier.
    /// Loaded from `MicGainStore` at bind time and replaced live by
    /// `setGainDB(_:)`; read once per tap callback. Never `lock` — see
    /// `InputGainBox` for why the tap gets its own.
    private let gainBox = InputGainBox()

    /// The 16 kHz mono Float32 stream, one buffer per tap callback. Consume
    /// via `UtteranceStream(channel: .mic).push(_:)`.
    public let stream: AsyncStream<[Float]>
    private let continuation: AsyncStream<[Float]>.Continuation

    /// Whether THIS capture may enable VoiceProcessingIO (AEC + the noise
    /// gate/AGC bundled with it). A per-path policy, not a global: AEC
    /// exists for the MEETING path, where other participants leaking from
    /// the speakers into the mic corrupts speaker attribution at the source
    /// (see the echo-cancellation comment in `startEngineLocked`). The
    /// DICTATION path is push-to-talk, single near-field speaker, nothing
    /// to echo — and on low-gain built-in mics the voice processor's noise
    /// gate EATS normal-volume speech, handing Whisper near-silence that it
    /// hallucinates into YouTube-outro phrases (field report from a
    /// teammate's MacBook, 2026-08-09: real speech never transcribed until
    /// `dictation.echoCancellation` was forced off; this machine's hotter mic
    /// never showed it). Dictation therefore constructs its capture with
    /// this false. The `dictation.echoCancellation` master switch still gates
    /// the paths that leave it true.
    private let voiceProcessingAllowed: Bool

    /// - Parameter voiceProcessingAllowed: **deliberately has no default.**
    ///   It used to default to `true`, and that default is how the meeting
    ///   path silently kept AEC while dictation opted out — nobody chose it
    ///   for notes, it was simply what omitting the argument did. A per-path
    ///   policy this consequential (the AEC noise gate can clamp a low-gain
    ///   mic after onset and feed Whisper near-silence it hallucinates on)
    ///   must be STATED at every construction site, so a new capture path
    ///   cannot inherit an answer by accident.
    public init(
        constants: SegmenterConstants = SegmenterConstants(),
        voiceProcessingAllowed: Bool
    ) {
        self.voiceProcessingAllowed = voiceProcessingAllowed
        guard let format = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: Double(constants.sampleRate),
            channels: 1,
            interleaved: false
        ) else {
            fatalError("AVAudioFormat(mono, \(constants.sampleRate) Hz, Float32) is always constructible")
        }
        self.targetFormat = format
        var escapedContinuation: AsyncStream<[Float]>.Continuation!
        self.stream = AsyncStream { continuation in escapedContinuation = continuation }
        self.continuation = escapedContinuation
        // Scoped to THIS engine instance — a notes capture and a dictation
        // capture each observe only their own engine. Fires on an arbitrary
        // queue; the flag is read under `lock` like all other state. Starting
        // AVAudioEngine itself posts this notification before `isRunning` is
        // set. That is expected setup churn, not evidence that the brand-new
        // binding is stale; only invalidate a binding that was already live.
        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine, queue: nil
        ) { [weak self] _ in
            guard let self else { return }
            self.lock.lock()
            let shouldInvalidate = Self.shouldInvalidateBindingForConfigurationChange(
                isRunning: self.isRunning
            )
            if shouldInvalidate {
                self.configurationChanged = true
            }
            let boundDevice = self.boundDeviceID
            let recordedRate = self.boundDeviceNominalRateAtBind
            self.lock.unlock()
            if shouldInvalidate {
                Self.trace("engine configuration changed (input device likely moved)")
                // The 300 ms re-validation happens HERE, outside `lock` —
                // never inside the observer's lock scope, which callers
                // (including `start()`/`stop()`) can hold for seconds under
                // HAL contention (`runWithTimeout`). See `WarmEngineMonitor`.
                self.warmEngineMonitor.beginObservation(
                    boundDevice: boundDevice, recordedRate: recordedRate
                ) { [weak self] generation, verdict in
                    self?.applyWarmEngineVerdict(generation: generation, verdict: verdict)
                }
            } else {
                Self.trace("ignored expected engine configuration change during startup")
            }
        }
    }

    deinit {
        if let configObserver {
            NotificationCenter.default.removeObserver(configObserver)
        }
    }

    /// Name of the device the engine is currently bound to (diagnostics).
    nonisolated(unsafe) public static var boundDeviceName: String = "(unbound)"

    /// The device a `start()` issued RIGHT NOW would bind: the user's
    /// preferred device (Settings) when set and present, else the live
    /// system default. The single definition of "the current mic" — the
    /// staleness check below and `startEngineLocked` both use it, so they
    /// cannot drift.
    public static func resolvedTargetInputDeviceID() -> AudioDeviceID {
        preferredInputDeviceID() ?? defaultInputDeviceID()
    }

    /// An explicit device this ENGINE is bound to, independent of the user's
    /// pin (spec v3 §5.5/§5.5.1, fix round 1).
    ///
    /// Automation must never write `preferredDeviceKey` — that key is the
    /// user's pin (`userPreferredSource`, §5.5: "automation never writes
    /// it"), and the registry reads it back as such. Before this seam the
    /// dictation adapter had no other way to say "open THIS device for this
    /// start", so it set the pin transiently and restored it afterwards —
    /// which left the user's pin overwritten on every early-return path
    /// that skipped the restore. The override lives here instead: it is a
    /// property of the engine, not of the user's preferences, and nothing
    /// outside this process can observe it.
    ///
    /// `nil` (the default, and the only value any pre-existing caller ever
    /// produces) means "resolve exactly as before": pin, then system
    /// default.
    private var bindDeviceUIDOverride: String?
    /// Per-start bound on `awaitFirstBuffer`'s FIRST-AUDIBLE wait, in
    /// seconds. `nil` = `firstBufferTimeout` (2.5 s), today's behaviour.
    /// §5.5.1's self-heal retest needs a much shorter bound
    /// (`startup_grace_ms + 300 ms`) so a dead overridden source costs the
    /// press a fraction of a second rather than the full headset budget.
    private var firstAudibleBoundOverride: TimeInterval?

    /// Point the NEXT `start()` at a specific device, and optionally bound
    /// how long it waits for that device to deliver audible audio.
    ///
    /// Sticky, not one-shot: `inputBindingIsStale()` compares the bound
    /// device against the target a fresh start would pick, so an override
    /// that evaporated after one start would make every subsequent
    /// staleness check declare the engine stale and tear down a perfectly
    /// good capture. Pass `deviceUID: nil` to go back to pin/default.
    public func bindNextStart(deviceUID: String?, firstAudibleBoundSeconds: TimeInterval? = nil) {
        lock.lock(); defer { lock.unlock() }
        bindDeviceUIDOverride = (deviceUID?.isEmpty ?? true) ? nil : deviceUID
        firstAudibleBoundOverride = firstAudibleBoundSeconds
    }

    /// The device THIS engine would bind right now: its explicit override
    /// when one is set and the device is present, else the process-wide
    /// pin/default resolution. Must be called with `lock` held (every call
    /// site is already inside a locked region).
    private func resolvedTargetForThisEngineLocked() -> AudioDeviceID {
        Self.resolvedTarget(overrideUID: bindDeviceUIDOverride)
    }

    /// Pure resolution, taking the override as a parameter so callers that
    /// must NOT hold `lock` across a Core Audio device enumeration (see
    /// `inputBindingIsStale`, which is on the press path) can read the
    /// override under the lock and resolve outside it.
    public static func resolvedTarget(overrideUID: String?) -> AudioDeviceID {
        guard let uid = overrideUID, !uid.isEmpty else { return resolvedTargetInputDeviceID() }
        for (id, deviceUID) in inputDevices() where deviceUID == uid { return id }
        // Absent device: fall back rather than fail. The adapter's own
        // evidence path (a hold with no audio) is what reacts to a source
        // that has gone away; refusing to open anything here would turn a
        // recoverable press into a dead one.
        trace("bind override \(uid) not present — using pin/default")
        return resolvedTargetInputDeviceID()
    }

    /// True when this engine is running but no longer capturing from the
    /// device a fresh start would choose — the system default moved (the
    /// user unplugged their earphones), the bound device disappeared, or
    /// the explicit bind failed at start. A stale capture LOOKS alive (the
    /// tap keeps firing) while delivering silence; the only fix is a
    /// rebuild.
    ///
    /// `DictationCapture.renewMicIfSpent()` rebuilds between push-to-talk
    /// presses, after cooling this instance down. A stopped engine is not
    /// stale: it holds no input device. Only a running capture is checked.
    ///
    /// A configuration notification can be caused by opening the microphone
    /// itself, including a Bluetooth profile transition. Rebinding solely
    /// because of the notification would restart a healthy device and repeat
    /// its wake-up delay.
    ///
    /// So the flag now triggers RE-VALIDATION: it is cleared once the
    /// resolved target still matches the bound device. Every genuinely stale
    /// case survives, because each has its own evidence that does not depend
    /// on the flag — an unbound engine (`bound == nil`, the failed bind), a
    /// stopped engine (`isRunning == false`), and a real device move, which
    /// by definition changes what `resolvedTargetInputDeviceID()` answers.
    /// The one case this no longer catches on the flag alone is a format
    /// change on the SAME device — see `bindingIsStale`, which treats it as
    /// stale precisely because a Bluetooth profile transition kills the
    /// stream while leaving the binding intact.
    public func inputBindingIsStale() -> Bool {
        lock.lock()
        let running = isRunning
        let changed = configurationChanged
        let bound = boundDeviceID
        let overrideUID = bindDeviceUIDOverride
        lock.unlock()
        let target = Self.resolvedTarget(overrideUID: overrideUID)
        let stale = Self.bindingIsStale(
            isRunning: running, configurationChanged: changed,
            bound: bound, target: target)
        // NOT cleared here on a "same device" re-validation. That clearing
        // existed for one evening and is exactly what let a dead stream
        // survive: the flag IS the only signal that the device reconfigured
        // under a warm engine, and a profile transition that kills the audio
        // leaves every other observable — bound id, target id, isRunning —
        // completely unchanged. The flag is cleared where it is answered:
        // in `startEngineLocked`, once a fresh engine is actually bound.
        if changed, stale {
            Self.trace("configuration changed under a running engine "
                       + "(bound \(String(describing: bound)), target \(target)) "
                       + "— rebuilding rather than trusting the stream")
        }
        return stale
    }

    /// The pure decision behind `inputBindingIsStale()`, separated so the
    /// self-check can drive every branch without opening a device.
    ///
    /// `configurationChanged` is still a parameter, and still load-bearing,
    /// but only as a reason to LOOK: with it set and the bound device gone
    /// or moved the answer is stale exactly as before. With it set and the
    /// same device still resolving, the honest answer is "not stale" — see
    /// `inputBindingIsStale()` for the 13-of-26 measurement that made the
    /// unconditional `if configurationChanged { return true }` a defect.
    public static func bindingIsStale(
        isRunning: Bool, configurationChanged: Bool,
        bound: AudioDeviceID?, target: AudioDeviceID
    ) -> Bool {
        guard isRunning else { return false }
        guard let bound else { return true }
        if bound != target { return true }
        // A configuration change on the SAME device is still stale, and this
        // clause was removed on 2026-09-03 and restored the same evening,
        // because the removal cost a real dictation within minutes.
        //
        // The reasoning for removing it was that 13 of 26 WH-1000XM5 binds
        // logged "stale at trigger" and rebound to the device they were
        // already on — apparently pure waste, each costing a wake-up. That
        // read the measurement correctly and its purpose exactly backwards.
        // Those rebinds were the RECOVERY. A Bluetooth profile transition
        // leaves the engine bound to a device whose stream has died, and the
        // tap then delivers buffers of exact zeros forever. Captured with the
        // flag removed, two consecutive presses on one warm engine:
        //
        //     press 1  peak=0.3628  outcome=inserted
        //     press 2  samples=37568 allZero=1 zeroLead=2.348s
        //              engineStart=1441ms firstBuffer=309ms   <- IDENTICAL
        //
        // Identical start timings prove the engine was never rebuilt: the
        // device went silent underneath a warm engine and nothing noticed.
        // `awaitFirstBuffer`'s first-audible timeout cannot cover this — it
        // runs at `start()`, and the whole point of a warm engine is that
        // `start()` is not reached.
        //
        // So the flag stays load-bearing, and the cost it was accused of is
        // now much smaller anyway: warm-up waits for SIGNAL rather than
        // arrival, so a rebuild it triggers no longer hands the user a
        // half-second of digital silence.
        return configurationChanged
    }

    /// Whether an AVAudioEngine configuration notification invalidates the
    /// current device binding. AVAudioEngine emits one while a new engine is
    /// being wired; treating that startup notification as a device change
    /// made every healthy capture immediately stale. A real mid-capture
    /// device or format change arrives after the engine is running.
    public static func shouldInvalidateBindingForConfigurationChange(
        isRunning: Bool
    ) -> Bool {
        isRunning
    }

    /// Name of the device THIS instance is bound to (the static
    /// `boundDeviceName` is last-writer-wins across instances).
    public func boundInputDeviceName() -> String {
        lock.lock()
        let bound = boundDeviceID
        lock.unlock()
        guard let bound else { return "(unbound)" }
        return Self.deviceName(bound)
    }

    /// Milliseconds from `engine.start()` to the FIRST buffer of the most
    /// recent start, or nil before any start. "The device answered."
    ///
    public func lastFirstBufferMs() -> Double? {
        lock.lock(); defer { lock.unlock() }
        return firstBufferMs
    }

    /// Milliseconds from `engine.start()` to the first buffer containing a
    /// NON-ZERO sample, or nil when none arrived before
    /// `firstBufferTimeout`. "The device woke up."
    ///
    /// This is the number the `nothingHeard` reports needed and nobody had.
    /// Every retained WH-1000XM5 clip begins with exact digital zeros —
    /// median 0.68 s, max 1.23 s across 50 clips — while the built-in mic
    /// is always 0.000 s. A nil here alongside a small
    /// `lastFirstBufferMs()` is the signature of a device that accepts a tap
    /// and hands back silence; the two must never be read as one fact.
    public func lastFirstAudibleMs() -> Double? {
        lock.lock(); defer { lock.unlock() }
        return firstAudibleMs
    }

    /// Milliseconds the whole of the most recent `start()` took, or nil
    /// before any start.
    ///
    /// Includes the two waits above, deliberately: the three numbers
    /// decompose one warm-up, and the remainder (start minus first-audible)
    /// is the bind-and-configure cost that `runWithTimeout` exists to bound
    /// — 27-400 ms normally, 6.0-6.1 s under the Bluetooth-default HAL stall
    /// documented on `start()`.
    public func lastEngineStartMs() -> Double? {
        lock.lock(); defer { lock.unlock() }
        return engineStartMs
    }

    /// How the currently bound device is attached: `"bluetooth"`,
    /// `"built-in"`, `"usb"`, `"virtual"`, or `"unknown"`.
    ///
    /// Read straight from `kAudioDevicePropertyTransportType` rather than
    /// guessed from the device NAME, because names are user-editable and
    /// localised while the transport is the thing that actually predicts the
    /// failure: only a Bluetooth device pays an A2DP⇄HFP profile transition,
    /// and only that transition produces the silent wake-up window. The
    /// per-press forensics and any "your headset was still waking up"
    /// message must branch on this, never on `"AirPods"` appearing in a
    /// string.
    ///
    /// `"unknown"` covers three real states that must not be dressed up as a
    /// transport: nothing bound, a device that does not answer the property
    /// (aggregates frequently do not), and a transport this build has no
    /// name for. Same rule as `nominalSampleRate`'s zero and
    /// `inputVolume`'s nil — say we do not know.
    public func boundDeviceTransport() -> String {
        lock.lock()
        let bound = boundDeviceID
        lock.unlock()
        guard let bound else { return "unknown" }
        return Self.transportName(bound)
    }

    /// The UID of the device this engine ACTUALLY bound at its last
    /// `start()` — nil while unbound. Stashed at bind time, so no HAL read.
    ///
    /// Distinct from `resolvedTargetInputDeviceID()` (the device a fresh
    /// start would bind RIGHT NOW): with an explicit `bindNextStart` the
    /// two differ by design, and on 2026-09-14 a hold captured on the
    /// EMEET USB mic was recorded — and credited in the source-evidence
    /// store — under `BuiltInMicrophoneDevice`, the system default, because
    /// the forensics record could only reach the resolved target.
    public func boundDeviceUID() -> String? {
        lock.lock()
        defer { lock.unlock() }
        return boundDeviceUIDAtBind
    }

    /// Just the configuration-change half of `inputBindingIsStale()`: true
    /// while a same-device configuration change (a Bluetooth A2DP⇄HFP
    /// flip, a format change) has been seen and no `WarmEngineMonitor`
    /// verdict has cleared it. Under an explicit `bindNextStart` the
    /// bound-vs-resolved-target half of `inputBindingIsStale()` is wrong by
    /// design (the bound device is SUPPOSED to differ from the default),
    /// so the registry-driven press path asks only this.
    public func inputStreamFlaggedStale() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return isRunning && configurationChanged
    }

    /// Set the input gain applied to every buffer this capture yields,
    /// clamped to `InputGain.minDB ... InputGain.maxDB`.
    ///
    /// Takes effect on the NEXT tap callback, with no engine restart. That
    /// property is the whole reason the gain lives in a box rather than in
    /// the state `lock` guards: the calibration UI drags this slider while
    /// the engine is warm and the user is speaking into it, and rebuilding
    /// the engine per slider tick would re-pay the Bluetooth A2DP→HFP
    /// renegotiation (`firstBufferTimeout`, up to 2.5 s of silence) on every
    /// pixel of travel.
    ///
    /// Does NOT persist. `MicGainStore` is written by the settings layer that
    /// owns the device identity; this class only ever READS the store, at
    /// bind time, so a capture cannot save a gain against a device it merely
    /// fell back to.
    public func setGainDB(_ dB: Float) {
        gainBox.setDB(InputGain.clampDB(dB))
    }

    /// The gain currently applied, in dB. 0 while nothing has been loaded or
    /// set, which is also the value at which `InputGain.apply` is a no-op.
    public func currentGainDB() -> Float { gainBox.dB() }

    /// Test seam: simulates what the `.AVAudioEngineConfigurationChange`
    /// observer does, so a check can exercise the staleness path without
    /// unplugging hardware.
    public func markConfigurationChangedForTesting() {
        lock.lock(); configurationChanged = true; lock.unlock()
    }

    /// Test seam: marks this instance as though `start()` had already
    /// succeeded, WITHOUT opening a device — so a check can exercise
    /// `WarmEngineMonitor`'s interaction with `isRunning` (fix-round-1:
    /// `applyWarmEngineVerdict` must ignore a `.clear` verdict once the
    /// engine has stopped) without touching real hardware. `feedLivenessForTesting`
    /// and `awaitVerdictForTesting` never start a real engine, so without
    /// this seam `isRunning` stays false and every `.clear` verdict would be
    /// silently ignored by the very code this pins.
    ///
    /// MUST NEVER be called on an engine that might really be running — this
    /// only lies about `isRunning`; it does not open a device, install a
    /// tap, or do anything `stop()` could safely undo, so calling it on a
    /// genuinely live capture would desynchronize this flag from reality for
    /// good. `#if DEBUG` was considered (fix-round-2) and rejected: every
    /// other `*ForTesting` seam in this file (`markConfigurationChangedForTesting`,
    /// `feedLivenessForTesting`, `awaitVerdictForTesting`, `configurationChangedForTesting`)
    /// is a plain always-compiled `public func` — `--self-check` runs against
    /// the actually-built app, release configuration included, so gating
    /// this one alone behind `#if DEBUG` would silently break it in a
    /// release self-check run while every sibling kept working. A
    /// precondition trap is the seam this file already uses for "this must
    /// not happen" (`fatalError` two lines into `init`), so misuse fails
    /// loudly in every configuration instead of being caught only by
    /// whichever one happens to define `DEBUG`.
    public func markRunningForTesting() {
        lock.lock()
        precondition(!isRunning, "markRunningForTesting() must only be called on a MicCapture "
            + "that has never had start() succeed — see this method's doc comment")
        isRunning = true
        lock.unlock()
    }

    /// Applies one `WarmEngineMonitor` verdict: clears `configurationChanged`
    /// ONLY for `.clear`, and only if BOTH — `generation` is still the
    /// monitor's current observation (a verdict from an observation a later
    /// config-change event has since superseded must never clear a flag
    /// that event just set), AND the engine is still running. A `.clear`
    /// that arrives after `stop()` has already torn this engine down would
    /// otherwise clear a flag on a binding that no longer exists — there is
    /// nothing left to have been re-validated. Every other verdict
    /// (`.stale`, `.inconclusive`) leaves the flag exactly as it was:
    /// today's behaviour.
    private func applyWarmEngineVerdict(generation: Int, verdict: WarmEngineVerdict) {
        guard verdict == .clear else { return }
        lock.lock()
        if isRunning, warmEngineMonitor.isCurrent(generation: generation) {
            configurationChanged = false
        }
        lock.unlock()
    }

    /// The `WarmEngineMonitor`'s current observation generation — spec v3
    /// §14's failure-log line asks for "the engine generation"; this is the
    /// one such counter `MicCapture` maintains. NOT a per-`start()` attempt
    /// counter (this instance's engine is re-bound in place across presses,
    /// never re-constructed), so a caller that wants "which attempt was
    /// this" needs its own counter — see `DictationController`'s fix-round-1
    /// report for why it keeps one locally instead of reaching in here.
    public func warmEngineGeneration() -> Int {
        warmEngineMonitor.currentGeneration
    }

    /// Test seam: drives `WarmEngineMonitor` through a full 300 ms
    /// observation window with FIXED liveness numbers instead of real tap
    /// callbacks — see `DictationInputFollowSelfCheck` for the §14
    /// acceptance matrix this exists to pin. Also sets
    /// `configurationChanged`, exactly as the real
    /// `.AVAudioEngineConfigurationChange` observer does before starting its
    /// own observation, so a check can assert the whole round trip: flag
    /// set → observation → flag cleared (or not).
    public func feedLivenessForTesting(callbacks: Int, anyActive: Bool, formatSame: Bool) {
        lock.lock(); configurationChanged = true; lock.unlock()
        warmEngineMonitor.beginObservationForTesting(
            callbacks: callbacks, anyActive: anyActive, formatSame: formatSame
        ) { [weak self] generation, verdict in
            self?.applyWarmEngineVerdict(generation: generation, verdict: verdict)
        }
    }

    /// Test seam: blocks up to `timeout` for the verdict of the most
    /// recently begun observation (real or `feedLivenessForTesting`).
    /// Returns nil while the window is still open — "pending at trigger
    /// stays stale" (spec v3 §14) reads this as "flag unchanged", exactly
    /// like every other non-`.clear` verdict.
    public func awaitVerdictForTesting(timeout: TimeInterval = 1.0) -> String? {
        warmEngineMonitor.awaitVerdict(timeout: timeout)?.rawValue
    }

    /// Test seam: pins the two rates a real bind would have recorded, so a
    /// check can drive `applyBoundDeviceEvent` over the baseline/client-format
    /// split without a device. Internal, not `public`: `swift test` reaches it
    /// through `@testable import` and nothing in the shipped app may call it.
    func markBoundDeviceRatesForTesting(baselineRate: Double, clientFormatRate: Double) {
        lock.lock()
        boundDeviceNominalRateAtBind = baselineRate
        halClientFormatRate = clientFormatRate
        lock.unlock()
    }

    /// Test seam: the configuration-changed flag as it stands right now —
    /// what a check reads to confirm a verdict actually cleared it (not
    /// merely that the verdict computed `.clear`).
    public func configurationChangedForTesting() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return configurationChanged
    }

    static func trace(_ s: String) { AudioTrace.log("mic", s) }

    /// The system default input device, or `kAudioObjectUnknown`.
    public static func defaultInputDeviceID() -> AudioDeviceID {
        var deviceID = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultInputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject),
                                   &address, 0, nil, &size, &deviceID)
        return deviceID
    }

    /// Human-readable name of the default input, for diagnostics.
    public static func defaultInputDeviceName() -> String {
        var deviceID = defaultInputDeviceID()
        guard deviceID != kAudioObjectUnknown else { return "(none)" }
        var name: CFString = "" as CFString
        var size = UInt32(MemoryLayout<CFString>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioObjectPropertyName,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        let status = withUnsafeMutablePointer(to: &name) { ptr in
            AudioObjectGetPropertyData(deviceID, &address, 0, nil, &size, ptr)
        }
        _ = deviceID
        return status == noErr ? (name as String) : "(unnamed)"
    }

    /// Do NOT reintroduce name-based guessing for input selection. A
    /// previous version treated a device named "External Microphone" as an
    /// empty 3.5mm jack and substituted the built-in mic; a measured sweep
    /// proved the opposite on the reporting machine (jack headset 0.0286 =
    /// working, built-in 0.0000 = silent), so the heuristic turned a working
    /// microphone into none at all. Device names do not tell you which input
    /// has signal; only measuring does (`--mic-sweep`).

    /// Pure decision: run echo cancellation only when the user allows it AND
    /// audio is leaving speakers that the mic can hear. Extracted so the
    /// rule is testable without audio hardware.
    public static func shouldEnableEchoCancellation(
        outputIsHeadphones: Bool, userEnabled: Bool, pathAllowed: Bool = true
    ) -> Bool {
        pathAllowed && userEnabled && !outputIsHeadphones
    }

    /// Resolve the persisted voice-processing preference. An absent value is
    /// intentionally OFF: Apple's VoiceProcessingIO also enables microphone
    /// automatic gain control by default and applies ducking to other audio,
    /// so enabling it implicitly changes both sides of the user's audio path.
    /// Keep AEC available as an explicit opt-in for speaker echo, but plain
    /// capture must be transparent.
    public static func resolveEchoCancellationPreference(_ storedValue: Bool?) -> Bool {
        storedValue ?? false
    }

    /// D13 kill switch for the input-only AUHAL capture backend, default
    /// **ON**. `object(forKey:) as? Bool ?? true` rather than `bool(forKey:)`
    /// so that an ABSENT key means on and an explicitly stored `false` means
    /// off — `bool(forKey:)` cannot tell those apart.
    ///
    /// The backend is only reachable when echo cancellation is off for this
    /// capture; see `startEngineLocked`.
    static var halInputBackendEnabled: Bool {
        UserDefaults.standard.object(forKey: "dictation.micCapture.halInput") as? Bool ?? true
    }

    /// Pure: which backend a start selects from the two answers that decide
    /// it, and the single line it logs. Its own function so the D13 selection
    /// matrix is testable without opening a device.
    static func backendSelection(halFlagEnabled: Bool, wantAEC: Bool)
        -> (useHAL: Bool, backendName: String, logLine: String) {
        if halFlagEnabled, !wantAEC {
            return (true, "hal-input", "[mic] backend=hal-input\n")
        }
        // `aec on` wins the reason when both apply: echo cancellation is the
        // reason a capture CANNOT be on the HAL backend, while the flag is
        // merely a choice.
        let reason = wantAEC ? "aec on" : "flag off"
        return (false, "avaudioengine", "[mic] backend=avaudioengine (reason: \(reason))\n")
    }

    /// Pure: true when a `kAudioDevicePropertyNominalSampleRate` event reports
    /// the rate this capture's BASELINE is already on — i.e. the value
    /// `boundDeviceNominalRateAtBind` holds, which is what
    /// `WarmEngineMonitor.formatMatches` compares every later read against.
    ///
    /// The device posts this property on transitions that land back where they
    /// started, and treating one of those as a change sets
    /// `configurationChanged` on a healthy capture — the same false-stale class
    /// this file has already fought twice (the 13-of-26 needless XM5 rebinds,
    /// the output-route holds).
    ///
    /// **Keyed to the baseline, deliberately, and not to the client format
    /// rate** (fix round 2, N2). One source of truth is the whole point: any
    /// rate this filter drops must be a rate the verdict machinery would also
    /// have called unchanged. Keyed to the client format instead, a 44.1 kHz
    /// client format over a settled 16 kHz HFP device swallowed the HFP→A2DP
    /// return — the classic stale-format death — as a no-op, and the capture
    /// reported itself healthy while delivering nothing. A filter that can
    /// produce a false HEALTHY is worse than no filter at all.
    static func nominalRateChangeIsNoOp(newRate: Double, baselineRate: Double) -> Bool {
        newRate > 0 && baselineRate > 0
            && abs(newRate - baselineRate) < Self.rateTolerance
    }

    /// `"hal-input"`, `"avaudioengine"`, or `"(none)"` before any start —
    /// which capture backend the most recent `start()` selected. Survives
    /// `stop()` on purpose (see `lastBackendName`).
    public func activeBackendName() -> String {
        lock.lock(); defer { lock.unlock() }
        return lastBackendName
    }

    /// Master switch for echo cancellation (default OFF).
    public static let echoCancellationKey = "dictation.echoCancellation"

    public static var echoCancellationEnabled: Bool {
        echoCancellationPreference(defaults: .standard)
    }

    static func echoCancellationPreference(defaults: UserDefaults) -> Bool {
        resolveEchoCancellationPreference(defaults.object(forKey: echoCancellationKey) as? Bool)
    }

    /// True when audio is going somewhere that cannot leak into the mic:
    /// headphones, AirPods, or any Bluetooth/USB headset. Determined from
    /// the default OUTPUT device's transport type and data source rather
    /// than its name, so it works for third-party hardware too.
    public static func outputIsHeadphones() -> Bool {
        var deviceID = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject),
                                         &address, 0, nil, &size, &deviceID) == noErr,
              deviceID != kAudioObjectUnknown else { return false }

        var transport: UInt32 = 0
        var tSize = UInt32(MemoryLayout<UInt32>.size)
        var tAddress = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyTransportType,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        if AudioObjectGetPropertyData(deviceID, &tAddress, 0, nil, &tSize, &transport) == noErr {
            if transport == kAudioDeviceTransportTypeBluetooth
                || transport == kAudioDeviceTransportTypeBluetoothLE
                || transport == kAudioDeviceTransportTypeUSB {
                return true
            }
        }
        var source: UInt32 = 0
        var sSize = UInt32(MemoryLayout<UInt32>.size)
        var sAddress = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDataSource,
            mScope: kAudioDevicePropertyScopeOutput,
            mElement: kAudioObjectPropertyElementMain)
        if AudioObjectGetPropertyData(deviceID, &sAddress, 0, nil, &sSize, &source) == noErr {
            let headphone: UInt32 = 0x6864_706E   // 'hdpn'
            return source == headphone
        }
        return false
    }

    /// UserDefaults key holding the user's chosen input device UID.
    /// Absent/empty = follow the system default.
    public static let preferredDeviceKey = "dictation.micDeviceUID"

    /// Resolve the preferred device UID to a live device id, or nil when
    /// unset / no longer present (unplugged headset) — in which case the
    /// caller falls back to the system default rather than failing.
    static func preferredInputDeviceUID(defaults: UserDefaults) -> String? {
        guard let uid = defaults.string(forKey: preferredDeviceKey), !uid.isEmpty else { return nil }
        return uid
    }

    public static func preferredInputDeviceID() -> AudioDeviceID? {
        guard let uid = preferredInputDeviceUID(defaults: .standard) else { return nil }
        for (id, deviceUID) in inputDevices() where deviceUID == uid { return id }
        trace("preferred input device \(uid) not present — using system default")
        return nil
    }

    /// (deviceID, uid, name, channels) for every input-capable device.
    public static func inputDevices() -> [(AudioDeviceID, String)] {
        var size: UInt32 = 0
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr
        else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr
        else { return [] }
        var out: [(AudioDeviceID, String)] = []
        for id in ids where inputChannelCount(id) > 0 {
            var uid: CFString = "" as CFString
            var uidSize = UInt32(MemoryLayout<CFString>.size)
            var uidAddress = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyDeviceUID,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            let ok = withUnsafeMutablePointer(to: &uid) { ptr in
                AudioObjectGetPropertyData(id, &uidAddress, 0, nil, &uidSize, ptr)
            } == noErr
            out.append((id, ok ? (uid as String) : ""))
        }
        return out
    }

    public static func inputChannelCount(_ id: AudioDeviceID) -> Int {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreamConfiguration,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr, size > 0
        else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(
            byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, raw) == noErr else { return 0 }
        let abl = UnsafeMutableAudioBufferListPointer(
            raw.assumingMemoryBound(to: AudioBufferList.self))
        return abl.reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    /// The device's own sample rate, straight from the HAL.
    ///
    /// The engine's idea of the input rate is NOT a substitute: see
    /// `reconcileInputFormat` for the measured reason they disagree.
    /// Returns 0 when the device does not answer, which every caller must
    /// read as "unknown", never as "zero rate".
    public static func nominalSampleRate(_ id: AudioDeviceID) -> Double {
        var rate: Double = 0
        var size = UInt32(MemoryLayout<Double>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyNominalSampleRate,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &rate) == noErr
        else { return 0 }
        return rate
    }

    /// Name for a specific device id.
    public static func deviceName(_ id: AudioDeviceID) -> String {
        var name: CFString = "" as CFString
        var size = UInt32(MemoryLayout<CFString>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioObjectPropertyName,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        let ok = withUnsafeMutablePointer(to: &name) { ptr in
            AudioObjectGetPropertyData(id, &address, 0, nil, &size, ptr)
        } == noErr
        return ok ? (name as String) : "device \(id)"
    }

    /// Persistent device UID for a specific device id (e.g. `"BuiltInMicrophoneDevice"`)
    /// rather than the numeric, session-scoped `AudioDeviceID`.
    public static func deviceUID(_ id: AudioDeviceID) -> String? {
        var uid: CFString = "" as CFString
        var size = UInt32(MemoryLayout<CFString>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDeviceUID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        let ok = withUnsafeMutablePointer(to: &uid) { ptr in
            AudioObjectGetPropertyData(id, &address, 0, nil, &size, ptr)
        } == noErr
        return ok ? (uid as String) : nil
    }

    /// The device's macOS INPUT volume — the slider in System Settings →
    /// Sound → Input — as 0…1, or nil when the device does not expose one.
    ///
    /// Hardware attenuation happens before software gain. Reading the
    /// device slider helps distinguish quiet input from a low software gain.
    /// Aggregate and some virtual devices have no volume control; nil means
    /// unavailable, never a guessed level.
    public static func inputVolume(_ id: AudioDeviceID) -> Float? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeScalar,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectHasProperty(id, &address) else { return nil }
        var value: Float32 = 0
        var size = UInt32(MemoryLayout<Float32>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr
        else { return nil }
        return value
    }

    /// Write the device's macOS input volume (0…1, clamped).
    ///
    /// - Returns: false when the device has no such property or refuses to
    ///   let us set it. Both are ordinary states — a device can report a
    ///   volume it will not accept a write for — and the caller must treat a
    ///   false as "the offer to raise it does not apply here", never as an
    ///   error to surface or a change to claim happened.
    ///
    /// This is a SYSTEM setting shared with every other app on that mic, so
    /// it is only ever written from an explicit user action ("Raise to 75%").
    /// Nothing on the capture path may call it automatically: silently moving
    /// a slider the user set in System Settings is exactly the kind of
    /// invisible change this file's other comments exist to prevent.
    @discardableResult
    public static func setInputVolume(_ id: AudioDeviceID, _ value: Float) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyVolumeScalar,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectHasProperty(id, &address) else { return false }
        var settable: DarwinBoolean = false
        guard AudioObjectIsPropertySettable(id, &address, &settable) == noErr,
              settable.boolValue else { return false }
        var clamped = Float32(min(1, max(0, value)))
        return AudioObjectSetPropertyData(
            id, &address, 0, nil,
            UInt32(MemoryLayout<Float32>.size), &clamped) == noErr
    }

    /// A device's transport type (`bltn`, `usb `, `virt`, `ccwl`, …), or nil
    /// when Core Audio will not say.
    /// Transport of ANY device by id, as the short token the forensics record
    /// and the mic-test panel both speak.
    ///
    /// Static as well as instance-scoped because the two callers ask different
    /// questions: `boundDeviceTransport()` asks about the engine we are
    /// holding, while the mic-test panel asks about the device dictation WOULD
    /// bind — before any engine exists. Same mapping either way; a second copy
    /// of the switch is how "bluetooth" and "Bluetooth" end up in one log.
    public static func transportName(_ id: AudioDeviceID) -> String {
        guard let transport = transportType(id) else { return "unknown" }
        switch transport {
        case kAudioDeviceTransportTypeBluetooth,
             kAudioDeviceTransportTypeBluetoothLE: return "bluetooth"
        case kAudioDeviceTransportTypeBuiltIn: return "built-in"
        case kAudioDeviceTransportTypeUSB: return "usb"
        case kAudioDeviceTransportTypeVirtual: return "virtual"
        default: return "unknown"
        }
    }

    static func transportType(_ id: AudioDeviceID) -> UInt32? {
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyTransportType,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectHasProperty(id, &address),
              AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr
        else { return nil }
        return value
    }

    /// A device's INPUT data source (`imic` internal mic, `emic` external
    /// mic, …), or nil when unavailable.
    static func inputDataSource(_ id: AudioDeviceID) -> UInt32? {
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyDataSource,
            mScope: kAudioDevicePropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectHasProperty(id, &address),
              AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr
        else { return nil }
        return value
    }

    /// Every input device, classified for `MicAutoSelect` — WITHOUT opening
    /// any of them.
    ///
    /// That is the whole point. Classification comes from Core Audio
    /// metadata, so recovering from a dead input costs no device bind, and
    /// therefore cannot provoke the HAL contention that hangs two engines on
    /// one device (`IOBindingChanged` -> `__psynch_mutexwait`). `ambientFloor`
    /// is left nil here; nothing on the shipping path measures it.
    ///
    /// `isBuiltIn` requires transport `bltn` AND data source `imic`. Both
    /// halves matter: on the 2026-08-14 field machine the DEAF headphone-jack
    /// input (`External Microphone`, the failing default) is also transport
    /// `bltn`, differing only as data source `emic`. Matching on transport
    /// alone would classify the device that caused the bug as the built-in
    /// microphone and "fix" it by choosing it again.
    public static func autoSelectCandidates() -> [MicAutoSelect.MicCandidate] {
        let builtInTransport = kAudioDeviceTransportTypeBuiltIn
        let virtualTransport = kAudioDeviceTransportTypeVirtual
        let internalMicSource: UInt32 = 0x696D_6963   // 'imic'

        return inputDevices().map { deviceID, uid in
            let transport = transportType(deviceID)
            let source = inputDataSource(deviceID)
            return MicAutoSelect.MicCandidate(
                uid: uid,
                name: deviceName(deviceID),
                isBuiltIn: transport == builtInTransport && source == internalMicSource,
                isVirtual: transport == virtualTransport,
                ambientFloor: nil)
        }
    }

    /// Every input-capable device, for diagnostics ("is the app even listening
    /// to the microphone you are speaking into?").
    public static func inputDeviceNames() -> [String] {
        var size: UInt32 = 0
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyDataSize(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr
        else { return [] }
        let count = Int(size) / MemoryLayout<AudioDeviceID>.size
        var ids = [AudioDeviceID](repeating: 0, count: count)
        guard AudioObjectGetPropertyData(
            AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr
        else { return [] }

        let defaultID = defaultInputDeviceID()
        var out: [String] = []
        for id in ids {
            // Input-capable only: a non-zero input stream configuration.
            var cfgAddress = AudioObjectPropertyAddress(
                mSelector: kAudioDevicePropertyStreamConfiguration,
                mScope: kAudioDevicePropertyScopeInput,
                mElement: kAudioObjectPropertyElementMain)
            var cfgSize: UInt32 = 0
            guard AudioObjectGetPropertyDataSize(id, &cfgAddress, 0, nil, &cfgSize) == noErr,
                  cfgSize > 0 else { continue }
            let raw = UnsafeMutableRawPointer.allocate(
                byteCount: Int(cfgSize), alignment: MemoryLayout<AudioBufferList>.alignment)
            defer { raw.deallocate() }
            guard AudioObjectGetPropertyData(id, &cfgAddress, 0, nil, &cfgSize, raw) == noErr
            else { continue }
            let abl = UnsafeMutableAudioBufferListPointer(
                raw.assumingMemoryBound(to: AudioBufferList.self))
            let channels = abl.reduce(0) { $0 + Int($1.mNumberChannels) }
            guard channels > 0 else { continue }

            var name: CFString = "" as CFString
            var nameSize = UInt32(MemoryLayout<CFString>.size)
            var nameAddress = AudioObjectPropertyAddress(
                mSelector: kAudioObjectPropertyName,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            let ok = withUnsafeMutablePointer(to: &name) { ptr in
                AudioObjectGetPropertyData(id, &nameAddress, 0, nil, &nameSize, ptr)
            } == noErr
            let label = ok ? (name as String) : "device \(id)"
            out.append((id == defaultID ? "* " : "  ") + label + " (\(channels)ch)")
        }
        return out
    }

    /// Current permission state without prompting.
    public static func permissionStatus() -> MicPermissionStatus {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return .authorized
        case .denied, .restricted: return .denied
        case .notDetermined: return .notDetermined
        @unknown default: return .notDetermined
        }
    }

    /// Drives the system TCC prompt if not yet determined.
    public static func requestPermission() async -> MicPermissionStatus {
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            AVCaptureDevice.requestAccess(for: .audio) { _ in cont.resume() }
        }
        return permissionStatus()
    }

    /// Starts the input tap. Throws `MicCaptureError.permissionDenied` if
    /// TCC has not granted mic access — callers should gate this behind
    /// `requestPermission()` in the onboarding/permissions flow.
    public func start() throws {
        lock.lock(); defer { lock.unlock() }
        guard !isRunning else { return }
        guard Self.permissionStatus() == .authorized else {
            throw MicCaptureError.permissionDenied
        }
        // 12 s, not 5. Measured on this project's own logs, 2026-08-21: a
        // normal start on an M-series Mac is 27-400 ms, but with Bluetooth
        // AirPods as the SYSTEM DEFAULT input while the app binds the built-in
        // mic, the HAL stalls for a remarkably tight 6.040-6.114 s — six
        // samples, 74 ms of spread. The 5 s bound sat right underneath that
        // cluster, so every one of those starts was declared a failure about
        // a second before it succeeded.
        //
        // ALL SEVEN TIMEOUTS THAT DAY WERE FALSE. Each
        // "MicCapture.start did not complete within 5s" is followed 1.03-1.11 s
        // later by this same instance printing "[mic] bound device=" and then
        // delivering audio. The caller had already been told the microphone
        // was unavailable; the abandoned thread came up anyway and took the
        // device. Both halves of that are bad: notes logs "mic capture
        // unavailable" for a channel that then works, and dictation reports
        // `noAudio (capture did not start)` for a hold whose capture started
        // one second later.
        //
        // Raised on the START only. `stop()` keeps its 5 s, because a stop
        // that overruns leaves a channel the caller can still treat as
        // stopped, whereas a start that overruns leaves a live engine holding
        // the device with nobody who thinks they own it.
        //
        // Not fixed by cancelling the abandoned body, which is the more
        // obvious repair: a blocked mutex wait cannot be cancelled, and
        // NotesSessionController's mic transcription currently WORKS ONLY
        // BECAUSE that abandoned engine comes up — it installs its PCM
        // consumer before calling start and only logs on the throw, so
        // tearing the engine down on timeout would turn today's noisy success
        // into a silent failure.
        //
        // Timed on BOTH paths — the throw included. A start that fails at
        // 12 s and a start that succeeds at 6 s are the same HAL contention
        // seen from two sides, and the failure is the one worth having a
        // number for; `defer` is what guarantees the caller can still read
        // it after the throw.
        let startedAt = DispatchTime.now()
        defer {
            engineStartMs = Double(
                DispatchTime.now().uptimeNanoseconds - startedAt.uptimeNanoseconds
            ) / 1_000_000
        }
        // Ownership shim (spec v3 §7.3/§14 #2) — a bookkeeping-only mirror,
        // changes no behaviour of this method. Owner is per-PATH, not
        // per-instance-config: dictation always constructs with
        // `voiceProcessingAllowed: false`, notes always with `true` — see
        // that property's doc comment — so it is the one signal that tells
        // the two paths apart without a device-specific branch (spec D8).
        let ownershipOwner = voiceProcessingAllowed ? "notes" : "dictation"
        // BOTH ownership UIDs are resolved INSIDE the bounded closure.
        //
        // `Self.deviceUID` is a Core Audio property read, and this method
        // holds `lock` for its whole body. Fix-round-2 resolved the TARGET's
        // UID here, on the caller thread, calling it "cheap" — but cheap is
        // not the invariant this class relies on. `runWithTimeout` exists
        // precisely because HAL calls that are normally sub-millisecond
        // stall for SECONDS under contention (the 6.0-6.1 s Bluetooth
        // cluster documented above), and `resolvedTargetForThisEngineLocked`
        // does a full device ENUMERATION on top of the UID read. Run under
        // `lock` and outside the bound, a stalled HAL hangs the caller with
        // the lock held and nothing to time it out — the same failure the
        // 12 s bound was introduced to make impossible. No HAL call in this
        // method may sit outside the bound.
        //
        // Both values therefore cross back through lock-guarded boxes rather
        // than plain captured `var`s (fix-round-2's reasoning still holds: a
        // var written on the detached thread and read here is a data race no
        // matter how the interleaving happens to fall).
        //
        // `opening` is registered on the detached thread, before
        // `startEngineLocked` touches the device — which is where the shim's
        // `opening` state is meant to be set anyway. On the throw path the
        // box's OUTER nil is meaningful: the closure never got as far as
        // registering, so there is no key to transition and nothing to
        // un-stick.
        let ownershipOpeningUIDBox = SyncStringBox()
        let ownershipBoundUIDBox = SyncStringBox()
        do {
            try Self.runWithTimeout(seconds: 12.0, label: "MicCapture.start") { [self] in
                let openingUID = Self.deviceUID(resolvedTargetForThisEngineLocked())
                ownershipOpeningUIDBox.set(openingUID)
                CaptureOwnershipShim.register(owner: ownershipOwner, deviceUID: openingUID)
                try startEngineLocked()
                // The device ACTUALLY bound can differ from the target:
                // `startEngineLocked`'s jack-avoidance fallback can bind
                // something else. It stashed that device's UID at bind time,
                // so this is a plain property read, not another HAL call.
                ownershipBoundUIDBox.set(boundDeviceUIDAtBind)
            }
        } catch {
            // `quarantined` only for the specific throw `runWithTimeout`
            // itself manufactures on timeout — the case where the detached
            // thread is abandoned and this device's real state is unknown
            // (see that function's doc comment). Every other throw
            // (`startEngineLocked` failing fast — a refused bind, a bad
            // format) is a thread that ran to completion and never took the
            // device: `free`, not `quarantined`.
            let state = Self.isAbandonedStartTimeout(error) ? "quarantined" : "free"
            if let openingUID = ownershipOpeningUIDBox.get() {
                CaptureOwnershipShim.transition(deviceUID: openingUID, to: state)
            } else {
                // Outer nil: the closure stalled before it could resolve and
                // register a key, so there is no shim entry to move. Worth a
                // line — it means the HAL hung on the enumeration itself,
                // which is a different failure from a hung engine start.
                FileHandle.standardError.write(
                    Data("[mic] start failed before ownership key resolved\n".utf8))
            }
            throw error
        }
        let ownershipOpeningUID = ownershipOpeningUIDBox.get() ?? nil
        // Past the `catch`: `runWithTimeout` did not throw, so the closure
        // ran to completion and `set` above always executed — `boxResult`
        // being the OUTER nil is therefore unreachable in practice. Handled
        // rather than asserted, because "unreachable" in a HAL-adjacent path
        // has been wrong before in this file; logged so a real occurrence is
        // visible rather than silently misreporting the opening UID as the
        // bound one.
        guard let ownershipBoundUID = ownershipBoundUIDBox.get() else {
            FileHandle.standardError.write(
                Data("[mic] ownership key unresolved at timeout\n".utf8))
            CaptureOwnershipShim.transition(deviceUID: ownershipOpeningUID, to: "capturing")
            return
        }
        if ownershipBoundUID != ownershipOpeningUID {
            // The fallback bound a device other than the one "opening" was
            // registered under — never leave that key stuck there.
            CaptureOwnershipShim.transition(deviceUID: ownershipOpeningUID, to: "free")
        }
        CaptureOwnershipShim.transition(
            deviceUID: ownershipBoundUID ?? ownershipOpeningUID, to: "capturing")
    }

    /// Why a start threw because a later one had taken over. One string, two
    /// throw sites (before and after the first-buffer wait).
    static let supersededStartMessage =
        "this microphone start was superseded by a later one while its caller had "
        + "already abandoned it — disposing its input-only HAL unit rather than "
        + "leaving it holding the device"

    /// A tap-handler call slower than this is worth a loud line rather than a
    /// trace: at a 512-frame buffer and 48 kHz the HAL's deadline is ~10.6 ms, so
    /// 5 ms inside the handler is half the budget spent on one buffer.
    static let handlerBudgetNanos: UInt64 = 5_000_000

    /// True when `error` is `runWithTimeout`'s own manufactured timeout (its
    /// message contains "did not complete within") rather than a real error
    /// `startEngineLocked` threw before the deadline. Mirrors the same
    /// classification `DictationController`'s swallowed-error logging makes
    /// at its own two catch sites — duplicated rather than shared because
    /// `MicCaptureError` lives in this module and `the app target` cannot be a
    /// dependency of it.
    private static func isAbandonedStartTimeout(_ error: Error) -> Bool {
        if case MicCaptureError.engineStartFailed(let message) = error {
            return message.contains("did not complete within")
        }
        return false
    }

    /// Time-bounds `body`, which is expected to run entirely synchronously
    /// and potentially block on a system call that has no timeout of its
    /// own. Found via `sample` on two `DictationApp --self-check` processes
    /// racing to configure `AVAudioEngine` against the SAME physical
    /// microphone device concurrently (verification-lane, 2026-08-06): one
    /// process's calling thread parked forever inside
    /// `AVAudioEngineImpl::IOBindingChanged` -> `std::recursive_mutex::lock()`
    /// -> `__psynch_mutexwait` — a real, 100%-reproducible OS-level hang
    /// with **zero** error and **zero** timeout, caused by Core Audio's HAL
    /// serializing device reconfiguration across processes. `start()` is
    /// reachable from multiple pre-single-instance-lock CLI entry points
    /// (`--audio-probe`, `--mic-sweep`, `--dictation-*`, `--self-check`
    /// itself via the meeting-notes self-checks) run from a terminal WHILE
    /// the GUI app is already recording — a real, not merely theoretical,
    /// way for two processes to contend the same mic device — so this
    /// class must bound the hang itself rather than trust its callers to
    /// avoid the race. On timeout, the background thread is abandoned (a
    /// blocked mutex wait cannot be cancelled) but the CALLER regains
    /// control with a clear thrown error instead of hanging forever with no
    /// error, matching this project's "never block a session, degrade"
    /// invariant for capture failures (see `NotesSessionController.start()`'s
    /// mic-channel `do`/`catch`, which logs and continues).
    private static func runWithTimeout(
        seconds: TimeInterval, label: String, _ body: @escaping () throws -> Void
    ) throws {
        let sem = DispatchSemaphore(value: 0)
        let box = ThrownErrorBox()
        Thread.detachNewThread {
            do { try body() } catch { box.set(error) }
            sem.signal()
        }
        if sem.wait(timeout: .now() + seconds) == .timedOut {
            throw MicCaptureError.engineStartFailed(
                "\(label) did not complete within \(Int(seconds))s — the microphone "
                + "hardware is likely contended by another process configuring the "
                + "same device; degrading this channel instead of hanging")
        }
        if let error = box.get() { throw error }
    }

    /// Two rates are the same device rate if they differ by less than this.
    /// Rates are whole numbers in practice (44100, 48000); the tolerance
    /// only absorbs float round-tripping.
    private static let rateTolerance: Double = 1.0

    /// Makes `AVAudioEngine`'s input format agree with the device we just
    /// bound — the fix for "the app records 0 samples from the microphone you
    /// chose".
    ///
    /// **The bug.** `AVAudioEngine` caches its input node's format. Setting
    /// `kAudioOutputUnitProperty_CurrentDevice` re-points the underlying
    /// AUHAL — the set succeeds, and reading the property back returns the
    /// new device — but the node keeps reporting the format of whatever
    /// device it latched first, which is the SYSTEM DEFAULT. Install a tap
    /// with that stale format and the engine delivers **zero buffers**, with
    /// no error and no callback, forever.
    ///
    /// A non-default device can have a different rate from the engine's
    /// cached input format. This reconciles the client-side format after
    /// binding. Voice-processing units are excluded because that path owns
    /// a different format and device configuration.
    ///
    /// Static, and public, so that every path which binds an input device
    /// reconciles it the SAME way. `--mic-sweep` had its own copy of the
    /// bind-then-read-format sequence and therefore reproduced this bug
    /// faithfully: the diagnostic built to find a microphone that hears
    /// nothing reported a rate-mismatched device as silent, which is the
    /// answer it was supposed to rule out.
    ///
    /// - Returns: the device's own rate when it is known, else 0.
    @discardableResult
    public static func reconcileInputFormat(
        _ input: AVAudioInputNode, device: AudioDeviceID, voiceProcessingActive: Bool
    ) -> Double {
        reconcileInputFormat(
            EngineNodeFormatTarget(input: input),
            device: device, voiceProcessingActive: voiceProcessingActive)
    }

    /// The reconciliation itself, over anything that can report and rewrite
    /// the input bus format — `EngineNodeFormatTarget` for the public entry
    /// point above, the active `MicInputBackend` for `startEngineLocked`.
    /// ONE implementation on purpose: see `MicInputFormatTarget`'s doc
    /// comment for what happened the last time a second copy of this
    /// sequence existed.
    @discardableResult
    static func reconcileInputFormat(
        _ target: MicInputFormatTarget, device: AudioDeviceID, voiceProcessingActive: Bool
    ) -> Double {
        let deviceRate = Self.nominalSampleRate(device)
        guard deviceRate > 0, !voiceProcessingActive else { return deviceRate }
        guard let currentFormat = target.inputFormat(),
              abs(currentFormat.sampleRate - deviceRate) >= Self.rateTolerance
        else { return deviceRate }

        // Captured BEFORE the set. Reading it afterwards reports the value
        // we just wrote, which turns the one line that explains the bug into
        // "engine 44100Hz vs device 44100Hz" — a contradiction that reads
        // like a logging defect rather than the stale format it documents.
        let staleRate = currentFormat.sampleRate
        let channels = UInt32(max(1, Self.inputChannelCount(device)))
        // A nil status is "there is no audio unit to write to" — the old
        // `guard let unit = input.audioUnit else { return deviceRate }`, and
        // it must return WITHOUT logging, because nothing was attempted.
        guard let status = target.setStreamFormat(rate: deviceRate, channels: channels)
        else { return deviceRate }
        // NOT trace-gated, and only ever emitted when the formats actually
        // disagreed: this line is the difference between a log that explains
        // a silent recording and one that does not.
        let line = "[mic] input format was stale for \(Self.deviceName(device)) "
            + "(engine \(Int(staleRate))Hz vs device \(Int(deviceRate))Hz) — "
            + "re-set the unit stream format (status \(status), \(channels)ch, "
            + "now \(Int(target.inputFormat()?.sampleRate ?? 0))Hz)\n"
        FileHandle.standardError.write(Data(line.utf8))
        return deviceRate
    }

    private func startEngineLocked() throws {
        // A fresh start observes the config from here on; anything the
        // observer recorded belonged to the previous run of this engine.
        configurationChanged = false
        boundDeviceID = nil
        boundDeviceUIDAtBind = nil
        boundDeviceNominalRateAtBind = 0

        // ---- Backend selection (D13) -----------------------------------
        // The echo-cancellation decision is taken HERE, before anything is
        // bound, because it is what chooses the backend: AEC lives in Core
        // Audio's VoiceProcessingIO unit and needs an output reference, so a
        // capture that wants AEC must stay on `AVAudioEngine`. Everything
        // else — dictation always, and any notes capture on headphones —
        // gets the input-only AUHAL unit, which never touches an output node
        // and therefore never provokes the Bluetooth A2DP⇄HFP switch that
        // can invalidate an engine configuration.
        //
        // `outputIsHeadphones()` is read one step earlier than it used to be
        // — it was read just below, after the device bind — because the
        // selection needs its answer before there is a backend to bind with.
        // It reads the default OUTPUT device, which pointing an input unit at
        // a microphone cannot change, so this is a reordering of two
        // independent HAL reads and not a change of decision.
        let usingHeadphones = Self.outputIsHeadphones()
        let wantAEC = Self.shouldEnableEchoCancellation(
            outputIsHeadphones: usingHeadphones,
            userEnabled: Self.echoCancellationEnabled,
            pathAllowed: voiceProcessingAllowed)
        let selection = Self.backendSelection(
            halFlagEnabled: Self.halInputBackendEnabled, wantAEC: wantAEC)
        let useHALBackend = selection.useHAL

        // Claimed BEFORE the unit is created. `start()` bounds this body at
        // 12 s and abandons the thread on timeout while leaving `isRunning`
        // false, so a second `start()` can run concurrently with an abandoned
        // first one. Pre-D13 both reconfigured the one `AVAudioEngine`; a fresh
        // AUHAL per start would instead leave two live units on one microphone,
        // the abandoned one reachable from nothing and disposed by nobody.
        // The gate refuses the second start while the first is still inside its
        // caller's bound, and tells a superseded body to tear itself down.
        var halGeneration: Int?
        if useHALBackend {
            guard let claimed = halStartGate.begin() else {
                throw MicCaptureError.engineStartFailed(
                    "a previous microphone start is still in flight on the input-only "
                    + "HAL backend — refusing to open a second unit on the same device")
            }
            halGeneration = claimed
        }

        let backend: MicInputBackend = useHALBackend ? HALInputBackend() : engineBackend
        activeBackend = backend
        lastBackendName = selection.backendName
        // NOT trace-gated: which backend a press ran on is the first thing
        // any future report about "dictation died again" has to establish,
        // and the kill switch exists precisely so that the two can be
        // compared from a log.
        FileHandle.standardError.write(Data(selection.logLine.utf8))

        // Every throw below leaves the HAL unit created-but-not-running, and
        // this class has no other teardown for that state: `stop()` returns
        // early on `!isRunning`, so nothing would dispose it until the next
        // `start()` replaced the backend and ARC got round to it. The engine
        // backend is deliberately excluded — its engine is instance-scoped
        // and calling `stop()` on it after a failed start is not what this
        // method did before D13.
        var reachedRunning = false
        defer {
            // Release the gate first: `end` reports whether this body is still
            // the current start. A superseded body has already thrown above
            // (`reachedRunning` false), so both cases land in the same teardown.
            if let halGeneration { halStartGate.end(halGeneration) }
            if !reachedRunning, useHALBackend {
                // Documented order: stop, THEN detach the tap (`removeTap`
                // frees the render context, and the HAL's IO thread must be
                // quiet before that memory goes away).
                backend.stop()
                backend.removeTap()
                // Identity-checked. A later start may already own
                // `activeBackend`; a superseded body must not clear it, and
                // must not touch the listeners that later start installed.
                //
                // Releasing here can run `HALInputBackend.deinit`, which makes
                // no HAL call because `backend.stop()` above already nil'd the
                // unit — same reasoning as the release in `stop()`.
                if activeBackend === backend { activeBackend = nil }
            }
        }

        // Bind explicitly to the CURRENT system default input.
        // `AVAudioEngine`'s input node latches a device when the engine's
        // audio unit is first configured; if the default input changed since
        // (or the node latched a virtual/idle device), the tap still fires at
        // the right rate but carries near-silence — the 2026-08-05 report
        // "it hears nothing unless another app is holding the mic" (with
        // another dictation app open it worked, without it RMS sat at
        // ~0.0003).
        // Setting kAudioOutputUnitProperty_CurrentDevice re-points the unit
        // at the real default before the format is read.
        if backend.canBind {
            // Preferred device (Settings > Notes) wins over the system
            // default. This exists because the system default is often NOT
            // the mic the user speaks into: on 2026-08-05 the default was
            // "External Microphone" (the empty 3.5mm jack) while the user
            // spoke into the built-in mic, so the app faithfully recorded
            // silence and transcription only worked while another
            // dictation app had grabbed a real device.
            var deviceID = resolvedTargetForThisEngineLocked()
            if deviceID == kAudioObjectUnknown {
                // Nothing resolved — device enumeration is transiently empty or
                // the preferred device is mid-removal, i.e. the exact instant a
                // headset connects or disconnects. Try the system default
                // explicitly, with the same fallback log line the refused-bind
                // path below uses, before giving up on naming a device.
                let fallback = Self.defaultInputDeviceID()
                if fallback != kAudioObjectUnknown {
                    deviceID = fallback
                    let ok = "[mic] fell back to \(Self.deviceName(fallback)) "
                        + "and bound it — dictation continues on the system "
                        + "default instead of the preferred device\n"
                    FileHandle.standardError.write(Data(ok.utf8))
                }
            }
            Self.boundDeviceName = Self.deviceName(deviceID)
            if deviceID == kAudioObjectUnknown, backend.requiresExplicitBind {
                // Still nothing. The engine path captured anyway here, because
                // its input node exists and stays latched to whatever CoreAudio
                // left it on; an input-only AUHAL has no unit at all until
                // `bind` runs, so skipping the bind would throw "no usable input
                // device" on a press the engine would have served. Create the
                // unit and let the HAL's own current device stand.
                let status = backend.bind(device: AudioDeviceID(kAudioObjectUnknown))
                if status == noErr, let actual = backend.currentDevice() {
                    boundDeviceID = actual
                    boundDeviceUIDAtBind = Self.deviceUID(actual)
                    Self.boundDeviceName = Self.deviceName(actual)
                    let line = "[mic] no input device resolved — opened the HAL's own "
                        + "current device \(Self.boundDeviceName) rather than refusing "
                        + "the start\n"
                    FileHandle.standardError.write(Data(line.utf8))
                } else if status != noErr {
                    throw MicCaptureError.inputDeviceBindFailed(
                        device: "(no input device resolved)", status: status)
                }
                // `noErr` with no readable current device falls through both
                // branches on purpose: the start proceeds with `boundDeviceID`
                // nil and the unit's own default client format, which is
                // EXACTLY what the engine path did in this situation (an input
                // node latched to a device it would not name). Parity, not a
                // gap — `inputBindingIsStale()` already treats a nil bound
                // device as stale, so the layer above still rebuilds.
            }
            if deviceID != kAudioObjectUnknown {
                let status = backend.bind(device: deviceID)
                if status == noErr {
                    boundDeviceID = deviceID
                    // Stashed here, inside the bounded closure, so every
                    // later reader gets the UID without a fresh HAL read.
                    boundDeviceUIDAtBind = Self.deviceUID(deviceID)
                } else {
                    // NOT trace-gated: a failed bind means the engine is
                    // latched to whatever device CoreAudio left it on — the
                    // exact silent-capture state the 2026-08-09 field report
                    // ("unplugged the earphone, nothing is heard") described,
                    // and it was invisible because this line needed
                    // DICTATION_AUDIO_TRACE=1.
                    let line = "[mic] could not bind input device "
                        + "\(Self.deviceName(deviceID)) (status \(status))\n"
                    FileHandle.standardError.write(Data(line.utf8))

                    // Fall back to the system default before giving up. There
                    // was no fallback at all until 2026-08-20: a refused
                    // PREFERRED device left dictation dead until the app was
                    // restarted, while a perfectly good default input sat
                    // unused and the log said "mic auto-select: no better
                    // input to switch to".
                    let fallback = Self.defaultInputDeviceID()
                    var recovered = false
                    if fallback != kAudioObjectUnknown, fallback != deviceID {
                        let retry = backend.bind(device: fallback)
                        if retry == noErr {
                            boundDeviceID = fallback
                            boundDeviceUIDAtBind = Self.deviceUID(fallback)
                            Self.boundDeviceName = Self.deviceName(fallback)
                            recovered = true
                            let ok = "[mic] fell back to \(Self.deviceName(fallback)) "
                                + "and bound it — dictation continues on the system "
                                + "default instead of the preferred device\n"
                            FileHandle.standardError.write(Data(ok.utf8))
                        }
                    }

                    // Refuse the start rather than run unbound. Carrying on
                    // here is what turned "the microphone did not open" into
                    // "we heard nothing" for the user, and it made the
                    // rebuild-on-next-trigger path loop forever because the
                    // rebuild re-ran this same bind and swallowed it again.
                    if !recovered {
                        throw MicCaptureError.inputDeviceBindFailed(
                            device: Self.deviceName(deviceID), status: status)
                    }
                }
            }
        }

        // ---- Acoustic echo cancellation -------------------------------
        // On speakers, the mic records the OTHER participants coming out of
        // those speakers. Without cancellation the "me" channel contains
        // their voices, so the same sentence is transcribed on both channels
        // and speaker attribution is corrupted at the source — no diarizer
        // can undo it. macOS's VoiceProcessingIO does AEC (plus noise
        // suppression and AGC) inside Core Audio, using the system render
        // signal as the echo reference.
        //
        // Skipped on headphones, following the same rule Granola's own
        // capture uses (`_disableEchoCancellationOnHeadphones`): with no
        // acoustic path from speaker to mic there is no echo to remove, and
        // voice processing costs quality (it band-limits and gates).
        //
        // `wantAEC` was computed at the top of this method (backend
        // selection) rather than here; the call and its logging are
        // unchanged. On the HAL backend `wantAEC` is false by construction —
        // that is the selection rule — so this only ever asks that backend
        // to turn voice processing OFF, which it has nothing to do for.
        var voiceProcessingActive = false
        if #available(macOS 14.0, *) {
            do {
                try backend.setVoiceProcessingEnabled(wantAEC)
                voiceProcessingActive = wantAEC
                Self.trace("echo cancellation \(wantAEC ? "ON" : "off") "
                           + "(headphones=\(usingHeadphones))")
            } catch {
                // Not fatal: capture without AEC beats no capture.
                Self.trace("voice processing unavailable: \(error)")
            }
        }

        // Runs AFTER voice processing, because enabling it rebuilds the
        // underlying unit and would discard a format written before it.
        var deviceRate: Double = 0
        if let boundDeviceID {
            deviceRate = Self.reconcileInputFormat(
                backend, device: boundDeviceID, voiceProcessingActive: voiceProcessingActive)
        }
        // What `WarmEngineMonitor` compares a later HAL read against to
        // decide "format changed" on a same-device config event. 0 when the
        // device did not answer, same as `nominalSampleRate`'s own contract —
        // a monitor reading 0 back later will see it as "same" only if the
        // device still does not answer, never as a false "changed".
        boundDeviceNominalRateAtBind = deviceRate

        // Optional for the AUHAL backend only (no unit ⇒ no format). The
        // engine's `outputFormat(forBus: 0)` is never nil, so every log line
        // and error string below is byte-identical on that path.
        let resolvedFormat = backend.inputFormat()
        // Report the device we actually BOUND, not the system default —
        // logging the default here was misleading the moment the
        // jack-avoidance fallback started overriding it. NOT trace-gated:
        // one line per engine start, and it is the single fact the
        // 2026-08-09 unplugged-earphone investigation needed and did not
        // have ("which device was capture actually reading when the user
        // pressed the hotkey and heard nothing?").
        // `deviceRate` is carried here so the log answers the question this
        // line previously could not: the bound device's OWN rate, next to
        // the engine's. When they differ, that difference IS the bug.
        let boundLine = "[mic] bound device=\(Self.boundDeviceName) "
            + "(system default=\(Self.defaultInputDeviceName())) "
            + "format=\(resolvedFormat?.sampleRate ?? 0)Hz "
            + "ch=\(resolvedFormat?.channelCount ?? 0) "
            + "deviceRate=\(deviceRate > 0 ? String(Int(deviceRate)) : "?")Hz\n"
        FileHandle.standardError.write(Data(boundLine.utf8))

        // Input gain belongs to the device we ACTUALLY bound, resolved here
        // rather than from the preferred-device setting, because the two
        // disagree exactly when it matters: the fallback above can bind the
        // system default after the preferred device is refused, and the
        // headset's +16 dB applied to the built-in mic — already hotter and
        // at 0.50 system volume — clips it. A device we could not name gets
        // 0 dB, which is the untouched path.
        var loadedGainDB: Float = 0
        if boundDeviceID != nil, let uid = boundDeviceUIDAtBind {
            loadedGainDB = InputGain.clampDB(MicGainStore().gainDB(deviceUID: uid))
        }
        gainBox.setDB(loadedGainDB)
        // Only when non-zero. 0 dB is the default and the no-op, so a line
        // for it would print on every warm-up and say nothing; a non-zero
        // gain is a fact that changes what every downstream consumer sees
        // and belongs next to the device it was loaded for.
        if loadedGainDB != 0 {
            let gainLine = "[mic] input gain +\(String(format: "%.1f", loadedGainDB))dB "
                + "for \(Self.boundDeviceName) (per-device, applied before the "
                + "level meter, the silence gate and ASR)\n"
            FileHandle.standardError.write(Data(gainLine.utf8))
        }

        guard let inputFormat = resolvedFormat,
              inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else {
            // A zero format means the device is not actually available; a tap
            // installed on it delivers silence forever.
            throw MicCaptureError.engineStartFailed(
                "input format unavailable (\(resolvedFormat?.sampleRate ?? 0)Hz, "
                + "\(resolvedFormat?.channelCount ?? 0)ch) — no usable input device")
        }
        // The reconciliation above failed. Starting anyway is the old
        // behaviour: a tap that fires zero times while every layer above
        // reports a working microphone, and the user is told their mic is
        // silent. Refusing here converts an invisible silent recording into
        // a named, actionable failure — and `DictationCapture` rebuilds the
        // capture on the next trigger rather than trusting this engine.
        if !voiceProcessingActive, deviceRate > 0,
           abs(inputFormat.sampleRate - deviceRate) >= Self.rateTolerance {
            throw MicCaptureError.inputFormatMismatch(
                device: Self.boundDeviceName,
                deviceRate: deviceRate,
                engineRate: inputFormat.sampleRate)
        }
        guard let converter = AVAudioConverter(from: inputFormat, to: targetFormat) else {
            throw MicCaptureError.converterCreationFailed
        }
        self.converter = converter

        // Tap at the hardware's native format/buffer size; convert down to
        // mono 16 kHz Float32 per callback. Native buffer sizes are
        // typically small (≈ 10-20 ms at 44.1/48 kHz), well under one
        // segmenter analysis frame (30 ms) — `UtteranceStream` accumulates
        // across pushes, so partial frames are not a correctness problem.
        // Signalled by the FIRST tap callback, and again by the first
        // callback that carries a non-zero sample. See `awaitFirstBuffer`.
        let firstBuffer = FirstBufferLatch()
        // Nil on the engine path, so that path's closure is byte-identical.
        let tapGeneration = halGeneration
        backend.installTap(bufferSize: 4096, format: inputFormat) { [weak self] buffer in
            // Past the gate's staleness bound two AUHAL units can legitimately be
            // live at once — an abandoned start's and its successor's — and both
            // taps push into the SAME `continuation`. A consumer would then
            // receive two captures' samples interleaved: roughly doubled
            // duration, duplicated and out-of-order audio, which ASR transcribes
            // as a stutter. A superseded unit's audio must never be delivered, so
            // the generation is checked here, first, before the latch and before
            // the converter.
            //
            // `publishedGeneration` is a lock-free read: this is the HAL's IO
            // thread, where an `NSLock` is not acceptable. See `HALStartGate`.
            if let tapGeneration, let self,
               tapGeneration != self.halStartGate.publishedGeneration { return }
            firstBuffer.note(buffer)
            guard let self else { return }
            self.handleInputBuffer(buffer, inputFormat: inputFormat, converter: converter)
        }

        do {
            try backend.start()
        } catch {
            if !useHALBackend {
                // Pre-D13 behaviour verbatim: `input.removeTap(onBus: 0)`, and
                // nothing else — the engine is deliberately not stopped here.
                backend.removeTap()
            }
            // The HAL branch is owned by the `defer` above, which does it in
            // the order `MicInputBackend.removeTap` documents (stop, THEN
            // detach the tap). Doing it here would free the render context
            // before the unit was stopped.
            // A `MicCaptureError` is already a named failure with its own
            // message (the HAL backend's `AudioUnitInitialize` /
            // `AudioOutputUnitStart` statuses); re-wrapping it would nest
            // "engineStartFailed(engineStartFailed(…))" in the user-visible
            // string. `AVAudioEngine` throws plain `NSError`s, so the engine
            // path takes the second branch exactly as before.
            if let known = error as? MicCaptureError { throw known }
            throw MicCaptureError.engineStartFailed(String(describing: error))
        }
        // Checked HERE, the instant the unit is live and BEFORE the up-to-2.5 s
        // `awaitFirstBuffer` wait, so a superseded body stops holding a second
        // live unit on the device almost immediately rather than for the whole
        // warm-up budget. It also keeps the abandoned body out of
        // `awaitFirstBuffer` entirely, so it cannot overwrite the live capture's
        // `firstBufferMs`/`firstAudibleMs` on its way out.
        if let halGeneration, !halStartGate.isCurrent(halGeneration) {
            throw MicCaptureError.engineStartFailed(Self.supersededStartMessage)
        }

        awaitFirstBuffer(firstBuffer, device: Self.boundDeviceName)

        // Belt and braces for a supersession that landed DURING the wait above.
        // A body whose caller already gave up on it must never publish itself:
        // doing so would overwrite the live capture's `isRunning` and listeners
        // with its own and leave two units on the device. The `defer` disposes
        // this one.
        if let halGeneration, !halStartGate.isCurrent(halGeneration) {
            throw MicCaptureError.engineStartFailed(Self.supersededStartMessage)
        }

        isRunning = true
        reachedRunning = true

        // D14 (minimum for D13's step): the HAL backend has no
        // `.AVAudioEngineConfigurationChange` notification to observe, so the
        // bound device's own HAL properties stand in for it.
        //
        // Installed AFTER the unit is running, and after the recorded rate is
        // re-read, and both halves matter. Starting an AUHAL on a Bluetooth
        // headset is what provokes the A2DP⇄HFP switch, so the bound device's
        // nominal rate changes DURING `backend.start()`. Listeners installed
        // before that would fire into a body holding `lock`, run the moment
        // `isRunning` became true — which is the whole of
        // `shouldInvalidateBindingForConfigurationChange` — and latch
        // `configurationChanged` against the PRE-flip rate recorded at bind, so
        // `WarmEngineMonitor` could never return `.clear` and every single
        // Bluetooth press would report itself stale from the instant it
        // started. The backend introduced to stop Bluetooth churn would have
        // guaranteed it.
        if useHALBackend, let boundDeviceID {
            // The rate the device SETTLED on, not the one it was on before the
            // unit opened it. `WarmEngineMonitor` compares later reads against
            // this, so a stale pre-flip value means every later event resolves
            // `.stale` on a perfectly healthy capture.
            let settledRate = Self.nominalSampleRate(boundDeviceID)
            if settledRate > 0, abs(settledRate - boundDeviceNominalRateAtBind) >= Self.rateTolerance {
                let line = "[mic] \(Self.boundDeviceName) settled at \(Int(settledRate))Hz "
                    + "after the unit opened it (was \(Int(boundDeviceNominalRateAtBind))Hz at "
                    + "bind) — recording the settled rate as this capture's baseline\n"
                FileHandle.standardError.write(Data(line.utf8))
                boundDeviceNominalRateAtBind = settledRate
            }
            halClientFormatRate = inputFormat.sampleRate
            installBoundDeviceListenersLocked(device: boundDeviceID)
        }
    }

    /// D14 (minimum): stand in for `.AVAudioEngineConfigurationChange` on the
    /// HAL backend by listening on the BOUND device itself.
    ///
    /// Two properties, both on the device we hold:
    /// `kAudioDevicePropertyNominalSampleRate` (the A2DP⇄HFP flip's visible
    /// signature — 44.1 kHz becoming 16 kHz under a live stream) and
    /// `kAudioDevicePropertyDeviceIsAlive` (the headset walking away). Either
    /// one sets the SAME `configurationChanged` flag and starts the SAME
    /// `WarmEngineMonitor` observation, through the same
    /// `shouldInvalidateBindingForConfigurationChange` gate, as the engine
    /// observer in `init` — so everything above `MicCapture` reads a HAL-backed
    /// capture's staleness exactly as it reads an engine-backed one.
    ///
    /// The C listener API, not the block one, and one `BoundDeviceListenerBox`
    /// as `clientData` for both registrations — see that type for why the block
    /// API cannot be unregistered from Swift at all.
    ///
    /// Must be called with `lock` held, from inside `start()`'s
    /// `runWithTimeout` bound: `AudioObjectAddPropertyListener` is a HAL call,
    /// and no HAL call in this class may run under `lock` outside a bound (see
    /// `boundDeviceUIDAtBind`).
    private func installBoundDeviceListenersLocked(device: AudioDeviceID) {
        removeBoundDeviceListenersLocked()
        let box = BoundDeviceListenerBox(queue: deviceListenerQueue) { [weak self] selector in
            self?.handleBoundDeviceConfigurationChange(selector: selector)
        }
        // RETAINED, not unretained. Core Audio holds nothing but this raw
        // pointer, and `AudioObjectRemovePropertyListener` does not synchronise
        // with a notification already in flight, so a box whose last Swift
        // reference is dropped at removal can be FREED under the HAL's
        // notification thread — and the event that fires it is the Bluetooth
        // teardown this backend exists to survive. The `+1` is released on a
        // timer well after removal; see `BoundDeviceListenerBox`.
        let clientData = Unmanaged.passRetained(box).toOpaque()
        let selectors: [AudioObjectPropertySelector] = [
            kAudioDevicePropertyNominalSampleRate,
            kAudioDevicePropertyDeviceIsAlive,
        ]
        var installed: [AudioObjectPropertyAddress] = []
        for selector in selectors {
            var address = AudioObjectPropertyAddress(
                mSelector: selector,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            let status = AudioObjectAddPropertyListener(
                device, &address, boundDeviceListenerProc, clientData)
            if status == noErr {
                installed.append(address)
            } else {
                Self.trace("could not listen for device property "
                           + "\(selector) on \(Self.deviceName(device)) (status \(status))")
            }
        }
        guard !installed.isEmpty else {
            // Nothing registered, so nothing can be in flight: the `+1` can go
            // straight back rather than being parked.
            box.invalidate()
            Unmanaged<BoundDeviceListenerBox>.fromOpaque(clientData).release()
            return
        }
        boundDeviceListenerBox = box
        boundDeviceListenerClientData = clientData
        boundDeviceListenerAddresses = installed
        boundDeviceListenerDevice = device
    }

    /// Remove whatever `installBoundDeviceListenersLocked` installed.
    ///
    /// Same rule as installing: `lock` held, and inside a `runWithTimeout`
    /// bound (`stop()`'s 5 s one), because `AudioObjectRemovePropertyListener`
    /// is a HAL call. Idempotent — `stop()` and the next `start()` both call it.
    ///
    /// The box is invalidated as well as unregistered. The remove is exact (same
    /// proc, same `clientData`), but a notification the HAL had ALREADY
    /// dispatched cannot be recalled, and invalidating is what guarantees such
    /// an event finds no handler instead of reaching a capture being torn down.
    /// It is also the only property a test can assert: a removal STATUS cannot
    /// distinguish a real unregistration from a no-op.
    private func removeBoundDeviceListenersLocked() {
        defer {
            boundDeviceListenerBox?.invalidate()
            // Park the `+1` rather than releasing it here. There is NO API to
            // drain a Core Audio notification that is already running, so the
            // box's memory has to outlive the removal; `isLive` is false from the
            // `invalidate()` above, which is what makes the parked box inert
            // rather than merely alive. Scheduled on a global queue — never the
            // main queue and never `deviceListenerQueue` — so nothing this
            // teardown holds is ever waited on.
            if let parked = boundDeviceListenerClientData {
                DispatchQueue.global(qos: .utility).asyncAfter(
                    deadline: .now() + BoundDeviceListenerBox.releaseGrace
                ) {
                    Unmanaged<BoundDeviceListenerBox>.fromOpaque(parked).release()
                }
            }
            boundDeviceListenerBox = nil
            boundDeviceListenerClientData = nil
            boundDeviceListenerAddresses.removeAll()
            boundDeviceListenerDevice = nil
        }
        guard let device = boundDeviceListenerDevice,
              let clientData = boundDeviceListenerClientData else { return }
        for address in boundDeviceListenerAddresses {
            var mutableAddress = address
            let status = AudioObjectRemovePropertyListener(
                device, &mutableAddress, boundDeviceListenerProc, clientData)
            if status != noErr {
                Self.trace("could not remove the device property listener for "
                           + "\(mutableAddress.mSelector) on \(Self.deviceName(device)) "
                           + "(status \(status))")
            }
        }
    }

    /// The bound-device listener body — deliberately a line-for-line twin of
    /// the `.AVAudioEngineConfigurationChange` observer in `init`, including
    /// the "ignore the startup churn" gate and the rule that the 300 ms
    /// re-validation is begun OUTSIDE `lock` (callers, `start()` and `stop()`
    /// included, can hold that lock for seconds under HAL contention).
    ///
    /// Runs on `deviceListenerQueue` (the box's hop off the HAL's notification
    /// thread), never on the IO thread, and the one HAL read it makes happens
    /// with the lock RELEASED.
    private func handleBoundDeviceConfigurationChange(selector: AudioObjectPropertySelector) {
        lock.lock()
        let device = boundDeviceID
        lock.unlock()
        // The ONE HAL read this body makes, and it happens with `lock` released.
        let observedRate = selector == kAudioDevicePropertyNominalSampleRate
            ? (device.map { Self.nominalSampleRate($0) } ?? 0)
            : 0
        applyBoundDeviceEvent(selector: selector, observedRate: observedRate)
    }

    /// The decision half of a bound-device event, with the device's rate already
    /// read. Split out so the no-op filter and the flag it guards can be driven
    /// in a test — the HAL read is the only part that needs a device, and the
    /// part worth pinning is which rates are credited against which baseline.
    func applyBoundDeviceEvent(selector: AudioObjectPropertySelector, observedRate: Double) {
        lock.lock()
        let baselineRate = boundDeviceNominalRateAtBind
        let clientRate = halClientFormatRate
        lock.unlock()
        // A rate event that reports the rate the BASELINE is already on is an
        // event, not a change: the device posts this property on transitions that
        // land back where they started, and crediting those would set
        // `configurationChanged` on a healthy capture — the false-stale class
        // this file has fought twice. Keyed to the baseline rather than to the
        // client format so that this filter and `WarmEngineMonitor`'s verdict can
        // never disagree about what counts as unchanged; see
        // `nominalRateChangeIsNoOp` for the false-HEALTHY that keying it to the
        // client format produced.
        if selector == kAudioDevicePropertyNominalSampleRate,
           Self.nominalRateChangeIsNoOp(newRate: observedRate, baselineRate: baselineRate) {
            Self.trace("bound device posted \(Int(observedRate))Hz, the rate this capture's "
                       + "baseline is already on (client format \(Int(clientRate))Hz) "
                       + "— not a change")
            return
        }

        lock.lock()
        let shouldInvalidate = Self.shouldInvalidateBindingForConfigurationChange(
            isRunning: isRunning
        )
        if shouldInvalidate {
            configurationChanged = true
        }
        let boundDevice = boundDeviceID
        let recordedRate = boundDeviceNominalRateAtBind
        lock.unlock()
        if shouldInvalidate {
            Self.trace("bound device property changed (rate or liveness) — "
                       + "treating it as a configuration change")
            self.warmEngineMonitor.beginObservation(
                boundDevice: boundDevice, recordedRate: recordedRate
            ) { [weak self] generation, verdict in
                self?.applyWarmEngineVerdict(generation: generation, verdict: verdict)
            }
        } else {
            Self.trace("ignored expected device property change during startup")
        }
    }

    /// Longest we will wait for a freshly-opened device to deliver AUDIBLE
    /// audio — not merely a buffer.
    ///
    /// Sized for Bluetooth, which is the only case that needs it: opening the
    /// microphone on AirPods makes macOS renegotiate the link from A2DP to
    /// the hands-free profile, and nothing arrives until that completes.
    /// Wired and built-in inputs deliver in tens of milliseconds and never
    /// approach this.
    ///
    /// **Raised from 2.0 s on 2026-09-03**, when the contract changed from
    /// "a buffer arrived" to "a sample was non-zero". The old bound was
    /// sized for arrival alone, and the two costs STACK on one headset
    /// wake-up: the WH-1000XM5's first buffer landed at up to 628 ms, and
    /// the digital-zero lead-in inside the clip that followed ran to
    /// 1.228 s (median 0.68 s, across 50 retained clips; the built-in mic is
    /// always 0.000 s). Worst measured total is therefore ~1.86 s, which
    /// 2.0 s does not clear with any margin at all. 2.5 s covers it by ~35%
    /// and stays far under the 12 s ceiling `start()` bounds the whole
    /// operation with, so a device that is genuinely dead still fails fast
    /// enough for the caller.
    ///
    /// This is a WARM-UP cost by construction — `start()` is reached from
    /// warm-up, ~800 ms off the critical path — so the extra half second is
    /// paid where nobody is holding a key down, which is the entire point of
    /// waiting here instead of discovering the silence mid-utterance.
    private static let firstBufferTimeout: TimeInterval = 2.5

    /// Block until the newly-started engine has actually delivered audio.
    ///
    /// An engine can return success while a Bluetooth link is still
    /// negotiating, before any input reaches the tap. Warm-up waits for
    /// readiness so that a short hold does not end inside that interval.
    ///
    /// Waiting here rather than at the trigger is deliberate: `start()` is
    /// reached from warm-up, which the capture path already treats as off the
    /// critical path ("released the microphone after 30s idle — the next
    /// dictation reopens it, ~800ms, off the critical path"). By the time the
    /// hotkey is pressed the link is up. The wait ends the instant the first
    /// buffer lands, so a built-in mic pays microseconds for it, not seconds.
    ///
    /// A timeout is NOT an error. The engine may legitimately be silent on a
    /// muted or idle virtual device, and refusing to start there would turn a
    /// recoverable state into a dead one. It is logged, because "the device
    /// accepted a tap and then delivered nothing for two seconds" is the fact
    /// that made this invisible for weeks.
    ///
    /// A different readiness failure delivers buffers containing only exact
    /// zeros. Buffer arrival alone cannot distinguish that state from live
    /// audio, so the latch also tracks the first non-zero sample.
    ///
    /// So the wait is now for SIGNAL: the first buffer containing a non-zero
    /// sample. A predicate that measures arrival cannot detect a device that
    /// arrives empty.
    ///
    /// Both facts are logged, never merged, because they are different
    /// diagnoses and only one of them is fixed by waiting: a device that is
    /// slow to ANSWER (first buffer) versus a device that answers at once
    /// and is slow to WAKE (first audible). "Delivered its first buffer in
    /// 41ms, first audible audio in 903ms" is a headset warming up; the same
    /// 41ms with no audible figure at all is a mute, a dead jack, or an idle
    /// virtual device.
    private func awaitFirstBuffer(_ latch: FirstBufferLatch, device: String) {
        let started = DispatchTime.now()
        // ONE deadline for both waits, not two timeouts in series: the
        // headset's arrival cost and its zero lead-in are two halves of a
        // single wake-up, and budgeting them separately would double the
        // worst case a dead device can cost the warm-up.
        // §5.5.1's bounded retest: when the caller asked for a shorter
        // first-audible bound for this start, it wins. Never LONGER than
        // the standing 2.5 s budget — this seam exists to make a dead
        // device fail fast, not to let one stall the warm-up further.
        let bound = min(firstAudibleBoundOverride ?? Self.firstBufferTimeout, Self.firstBufferTimeout)
        let deadline = started + bound
        func elapsedMs() -> Double {
            Double(DispatchTime.now().uptimeNanoseconds - started.uptimeNanoseconds) / 1_000_000
        }
        let arrived = latch.waitForBuffer(deadline: deadline)
        let bufferMs = arrived ? elapsedMs() : nil
        let audible = arrived && latch.waitForAudio(deadline: deadline)
        let audibleMs = audible ? elapsedMs() : nil

        // Written directly, not under `lock`: this runs on the detached
        // thread `start()` handed to `runWithTimeout`, while the CALLING
        // thread holds `lock` for the whole of `start()`. Same discipline as
        // `boundDeviceID` a few lines above, and the accessors cannot
        // observe a half-written start because they block on that lock.
        firstBufferMs = bufferMs
        firstAudibleMs = audibleMs

        // Only worth a line when the device made us wait. Sub-100ms to first
        // AUDIBLE audio is every ordinary input — built-in mics measure
        // 0.000 s of lead-in — and would be pure noise once per warm-up.
        if audible, let audibleMs, audibleMs < 100 { return }
        let bufferPart = bufferMs.map { "\(Int($0))ms to deliver its first buffer" }
            ?? "delivered NO buffer in \(Int(bound * 1000))ms"
        let line: String
        if let audibleMs {
            line = "[mic] \(device) took \(bufferPart) and \(Int(audibleMs))ms to "
                + "deliver its first NON-ZERO audio (a Bluetooth link "
                + "renegotiating; the wait happens at warm-up, not when you "
                + "press the key)\n"
        } else if arrived {
            line = "[mic] \(device) took \(bufferPart) but every sample in the "
                + "next \(Int(bound * 1000))ms was an exact "
                + "digital ZERO — capture is running and may record silence; a "
                + "hardware mute, an idle virtual device and a headset that "
                + "never finished waking all look like this\n"
        } else {
            line = "[mic] \(device) accepted the tap but \(bufferPart) — capture "
                + "is running and may record silence; a hardware mute or an idle "
                + "virtual device both look like this\n"
        }
        FileHandle.standardError.write(Data(line.utf8))
    }

    public func stop() {
        lock.lock(); defer { lock.unlock() }
        guard isRunning else { return }
        // Ownership shim (spec v3 §7.3/§14 #2) — bookkeeping only. Keyed by
        // the device we actually bound at start, not the resolved target:
        // `stop()` tears down whatever THIS engine is holding.
        //
        // Read from `boundDeviceUIDAtBind`, a plain stored property, NOT by
        // asking the HAL for the UID again. This runs with `lock` held and
        // outside `runWithTimeout`'s bound, and no HAL call may do that (see
        // that property's doc comment): under contention a UID read can
        // stall for seconds, and here there would be no timeout to catch it.
        // The value was resolved at bind time inside `start()`'s bound, so
        // it is also exactly the device this engine took — no re-resolution
        // race, and nothing the bounded closure below has to compute.
        let ownershipUID = boundDeviceUIDAtBind
        CaptureOwnershipShim.transition(deviceUID: ownershipUID, to: "stopping")
        // Bounded for the same reason `start()` is: `engine.stop()` walks
        // the same `AVAudioEngineImpl::IOBindingChanged` path under HAL
        // contention with another process, so it can hang exactly like
        // `start()` did — see `runWithTimeout`'s doc comment. `isRunning`
        // and `continuation.finish()` still run either way, so a caller
        // that leaks a blocked teardown thread at least gets a channel it
        // can treat as stopped rather than a `stop()` that itself never
        // returns.
        do {
            try Self.runWithTimeout(seconds: 5.0, label: "MicCapture.stop") { [self] in
                // ENGINE FIRST, THEN THE TAP. This was the other way round, and
                // that ordering killed the app on 2026-08-21 at 20:12:38:
                //
                //   *** Terminating app due to uncaught exception
                //       'com.apple.coreaudio.avfaudio',
                //       reason: 'required condition is false: isSink || tap != nullptr'
                //   AVAudioEngineGraph::InputAvailable
                //     <- Core Audio input-available callback
                //     <- HALC_ProxyIOContext::IOWorkLoop
                //
                // `removeTap` on a RUNNING engine leaves the graph expecting input
                // for a node that no longer has anywhere to put it, and the next
                // buffer the HAL delivers — from its own IO thread, which this
                // lock does not hold — trips that assertion. NSException on a
                // CoreAudio thread is not catchable from Swift, so it is not a
                // failed teardown, it is the process gone. The crash landed to the
                // second on a notes STOP, which is the only thing that calls this.
                //
                // Stopping first drains the graph: no more InputAvailable
                // callbacks can arrive, so the tap can be removed with nothing
                // left to race. The reverse of the start path, which installs the
                // tap before starting for exactly the mirror-image reason.
                //
                // The HAL backend obeys the same ordering for the same reason
                // (`AudioOutputUnitStop` before the handler is dropped), and
                // its `stop()` is also its teardown: the unit is uninitialized
                // and DISPOSED, so every start gets a fresh one (D13).
                //
                // The D14 listeners come off FIRST, so no property event can
                // start a `WarmEngineMonitor` observation against a binding
                // that is being torn down. This is a HAL call, and this is the
                // only place `stop()` has a bound to make one inside.
                removeBoundDeviceListenersLocked()
                // `?? engineBackend` is not a fallback for a missing backend
                // so much as the pre-D13 behaviour preserved verbatim for the
                // one state that can reach here without a start:
                // `markRunningForTesting()` sets `isRunning` without ever
                // selecting a backend, and this method used to stop the engine
                // and remove a tap from bus 0 unconditionally.
                let backend = activeBackend ?? engineBackend
                backend.stop()
                // Read HERE: `AudioOutputUnitStop` inside `backend.stop()` is a
                // barrier for the IO thread, so these single-writer counters can
                // be read without racing the writer (they used to be read right
                // after `awaitFirstBuffer`, while the unit was running). Before
                // `removeTap()`, which releases the render context.
                let stats = backend.renderStats()
                if stats.failures > 0 {
                    // A render callback cannot log, so this is the only place a
                    // dropped or refused render becomes visible instead of
                    // looking like a quiet room.
                    let line = "[mic] \(stats.failures) render callback(s) FAILED on "
                        + "\(Self.boundDeviceName) (last status \(stats.lastStatus)) — that "
                        + "capture was DROPPING audio, not hearing silence\n"
                    FileHandle.standardError.write(Data(line.utf8))
                }
                if stats.maxHandlerNanos > Self.handlerBudgetNanos {
                    let line = "[mic] the tap handler took up to "
                        + "\(stats.maxHandlerNanos / 1_000)µs inside the HAL's IO deadline "
                        + "— above the \(Self.handlerBudgetNanos / 1_000)µs budget; the "
                        + "converter/allocation on the callback thread needs a lock-free "
                        + "ring before this backend is default-ON for other machines\n"
                    FileHandle.standardError.write(Data(line.utf8))
                } else if stats.maxHandlerNanos > 0 {
                    // The number the "is the tap handler safe on the IO thread"
                    // decision rests on, available to the live probe run
                    // (DICTATION_AUDIO_TRACE=1) without a line on every stop.
                    Self.trace("tap handler worst case \(stats.maxHandlerNanos / 1_000)µs "
                               + "(budget \(Self.handlerBudgetNanos / 1_000)µs)")
                }
                backend.removeTap()
            }
            CaptureOwnershipShim.transition(deviceUID: ownershipUID, to: "free")
        } catch {
            // The only throw `runWithTimeout` can produce here is its own
            // manufactured timeout — the closure above does not throw — so
            // this IS the abandoned-thread case, unconditionally.
            CaptureOwnershipShim.transition(deviceUID: ownershipUID, to: "quarantined")
        }
        isRunning = false
        // The backend is released, but `lastBackendName` is not: see its doc
        // comment — a caller asking which backend the press it just finished
        // used must not be told "(none)".
        //
        // This line runs under `lock` and OUTSIDE the bound above, and it can
        // drop the last reference to a `HALInputBackend`, whose `deinit`
        // disposes its unit — three HAL calls. That is safe ONLY because the
        // bounded closure has already run `backend.stop()`, which nils the
        // unit, so `deinit` finds nothing to do and makes no HAL call at all.
        // If a backend is ever given a deinit that touches the HAL
        // unconditionally, this release has to move inside the bound.
        activeBackend = nil
        // Per-capture, like `activeBackend`: leaving it set would hand the next
        // reader a rate from a capture that no longer exists. (`boundDeviceID`
        // and `boundDeviceUIDAtBind` are deliberately NOT cleared here — the
        // forensics path reads them after a stop.)
        halClientFormatRate = 0
        continuation.finish()
    }

    private func handleInputBuffer(
        _ buffer: AVAudioPCMBuffer,
        inputFormat: AVAudioFormat,
        converter: AVAudioConverter
    ) {
        let ratio = targetFormat.sampleRate / inputFormat.sampleRate
        let outCapacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio) + 16
        guard let outBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: outCapacity) else {
            return
        }

        var error: NSError?
        var delivered = false
        let status = converter.convert(to: outBuffer, error: &error) { _, outStatus in
            if delivered {
                outStatus.pointee = .noDataNow
                return nil
            }
            delivered = true
            outStatus.pointee = .haveData
            return buffer
        }

        guard status != .error, let channelData = outBuffer.floatChannelData else { return }
        let frameCount = Int(outBuffer.frameLength)
        guard frameCount > 0 else { return }
        let samples = Array(UnsafeBufferPointer(start: channelData[0], count: frameCount))

        // ---- Warm-engine liveness tee -----------------------------------
        // Feeds `WarmEngineMonitor`'s liveness box from the SAME converted
        // buffer every other consumer below reads — no second tap (spec v3
        // §14: "verify the observation reads fresh tap samples without
        // competing with the existing stream consumer"). A no-op cost when
        // no observation is in flight: one short lock, an increment, and a
        // scan that already runs once per callback economics identical to
        // `FirstBufferLatch.containsSignal` above it in this file.
        let aboveFloor = samples.contains { abs($0) > MicCapture.floorAmplitude }
        warmEngineMonitor.liveness.feed(aboveFloor: aboveFloor)

        // ---- Input gain -----------------------------------------------
        // Applied HERE, on the converted mono/16 kHz samples, one statement
        // before the yield — the single choke point every the app consumer of
        // microphone audio passes through, dictation and meeting notes
        // alike. That placement is the design, not a convenience:
        //
        //   * the level meter, the silence gate, the retained WAVs and ASR
        //     all read this stream, so gaining it here keeps all four
        //     agreeing about the same audio. Gaining at any later point
        //     would show the user a meter for one signal while the gate
        //     judged another;
        //   * `SegmenterConstants.silenceRMS == AsrGatePolicy.dictation
        //     .audibleThreshold` (both 0.004, pinned by
        //     `PipelineParitySelfCheck`) is left completely untouched. The
        //     fix for a too-quiet headset is to raise the signal to meet the
        //     unchanged floor, never to lower the floor: the floor exists
        //     because Whisper does not return "nothing" for near-silence, it
        //     free-runs and emits training-set artifacts ("Thank you.",
        //     YouTube outros). A whisper peaking at 0.002 fails the gate
        //     today AND would be hallucinated on if simply let through; at
        //     +16 dB it peaks at 0.013 and reaches the decoder at a sane
        //     amplitude.
        //
        // At 0 dB — the default, and what every device without a stored gain
        // gets — `InputGain.apply` early-outs and returns this exact array,
        // so the shipped audio path stays bit-identical.
        let gained = InputGain.apply(samples, linearGain: gainBox.linear())
        continuation.yield(gained)
    }
}

/// Thread-safe one-shot error box for `MicCapture.runWithTimeout` — the
/// background thread that races the timeout may finish (and set an error)
/// after the timeout has already fired, so this must survive being
/// accessed from two threads.
private final class ThrownErrorBox: @unchecked Sendable {
    private let lock = NSLock()
    private var error: Error?
    func set(_ e: Error) { lock.lock(); error = e; lock.unlock() }
    func get() -> Error? { lock.lock(); defer { lock.unlock() }; return error }
}

/// Thread-safe one-shot optional-string box, modelled on `ThrownErrorBox`
/// immediately above — for a value written on `runWithTimeout`'s detached
/// thread and read on the caller thread only AFTER that call returns
/// (fix-round-2: a plain captured `var` written on one thread and read on
/// another with no lock is a data race regardless of how unlikely the
/// interleaving is in practice).
///
/// Unlike `ThrownErrorBox`, the value itself can legitimately BE `nil`
/// ("resolved — to no device"), so `get()` returns a DOUBLE optional: the
/// outer `nil` means `set` was never called at all; `.some(nil)` means it
/// was called with `nil`. `MicCapture.start()` is the one caller, and only
/// reads this on its SUCCESS path — `set` runs as the last statement of the
/// bounded closure, before that closure returns and `runWithTimeout`'s
/// semaphore signals, so by the time `get()` is read the write has already
/// happened-before it.
final class SyncStringBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: String??
    func set(_ value: String?) {
        lock.lock(); stored = value; lock.unlock()
    }
    func get() -> String?? {
        lock.lock(); defer { lock.unlock() }
        return stored
    }
}

/// The live input gain, shared between the calibration UI's slider and the
/// realtime audio callback that reads it once per buffer.
///
/// Holds BOTH the dB figure and its linear multiplier, converted on write.
/// That is the point: `InputGain.linear(fromDB:)` is a `pow` per call, and
/// the tap must not do arithmetic it can do once at set time — the read side
/// is a lock, a `Float` copy, and an unlock, with no allocation, no
/// conversion and no logging. Same realtime constraint `FirstBufferLatch`
/// documents for its own `signal()`, and for the same reason: this runs on
/// the HAL's IO thread, where blocking or allocating means a dropout in the
/// audio, not merely a slow function.
///
/// An `NSLock` rather than the class's main `lock`: that one is held for the
/// whole of `start()` and `stop()`, both of which can block for SECONDS
/// under HAL contention (see `runWithTimeout`). A tap callback waiting on it
/// would stall the IO thread behind a device teardown.
final class InputGainBox: @unchecked Sendable {
    private let boxLock = NSLock()
    private var storedDB: Float = 0
    private var storedLinear: Float = 1

    func setDB(_ dB: Float) {
        let linear = InputGain.linear(fromDB: dB)
        boxLock.lock()
        storedDB = dB
        storedLinear = linear
        boxLock.unlock()
    }

    func dB() -> Float {
        boxLock.lock(); defer { boxLock.unlock() }
        return storedDB
    }

    /// The multiplier to hand `InputGain.apply`. Exactly 1 at 0 dB, which is
    /// what makes the default path an early-out rather than a `× 1.0` walk
    /// over the samples.
    func linear() -> Float {
        boxLock.lock(); defer { boxLock.unlock() }
        return storedLinear
    }
}

/// Two one-shot signals from the tap: "a buffer arrived" and "a buffer
/// contained a non-zero sample".
///
/// They are separate because a WH-1000XM5 satisfies the first 0.56-1.23 s
/// before the second, and the whole of a short dictation hold fits in that
/// gap — see `MicCapture.awaitFirstBuffer` for the measurement and the
/// failure it produced.
///
/// `DispatchSemaphore`s rather than polled flags so the common case — a
/// built-in mic delivering audible audio in ~10ms — returns immediately
/// instead of sleeping to the next poll tick. `note(_:)` runs on the
/// realtime audio callback, so it must never allocate, lock for long, or
/// log; incrementing each semaphore at most once satisfies all three.
final class FirstBufferLatch: @unchecked Sendable {
    private let arrivalSemaphore = DispatchSemaphore(value: 0)
    private let audibleSemaphore = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var arrived = false
    private var audible = false

    /// Record one tap callback. Called for EVERY buffer, on the HAL's IO
    /// thread.
    ///
    /// Cost: two short lock/unlock pairs plus, until the first non-zero
    /// sample is seen, one linear scan of the buffer that early-exits on
    /// that sample. Worst case is a fully silent 4096-frame mono buffer —
    /// 4096 float compares over 16 KB already resident in cache, tens of
    /// microseconds against the ~85 ms of wall time that buffer represents
    /// at 48 kHz. Once `audible` is set the scan is skipped entirely, so the
    /// steady-state cost for the rest of the capture is the flag read alone.
    /// No allocation, no conversion, no logging on any path.
    func note(_ buffer: AVAudioPCMBuffer) {
        lock.lock()
        let firstArrival = !arrived
        arrived = true
        let stillSearching = !audible
        lock.unlock()
        if firstArrival { arrivalSemaphore.signal() }
        guard stillSearching, Self.containsSignal(buffer) else { return }
        lock.lock()
        let firstAudible = !audible
        audible = true
        lock.unlock()
        if firstAudible { audibleSemaphore.signal() }
    }

    /// True when any sample in the buffer is not an exact zero.
    ///
    /// Exact `!= 0`, not a level threshold. The measured lead-in is literal
    /// digital silence, so nothing softer is needed to detect it, and a
    /// threshold here would quietly become a SECOND silence gate competing
    /// with `SegmenterConstants.silenceRMS` / `AsrGatePolicy.dictation`
    /// (both 0.004, pinned by `PipelineParitySelfCheck`). This answers "is
    /// the device awake", never "is this loud enough" — that judgement has
    /// one owner and it is not this function.
    ///
    /// A buffer this cannot read (a non-float format, which the tap does not
    /// currently produce) counts as signal: an unreadable buffer is not
    /// evidence of silence, and treating it as such would hold the warm-up
    /// for the full timeout on a working device.
    private static func containsSignal(_ buffer: AVAudioPCMBuffer) -> Bool {
        let frames = Int(buffer.frameLength)
        guard frames > 0 else { return false }
        guard let data = buffer.floatChannelData else { return true }
        if buffer.format.isInterleaved {
            // One block, samples of every channel woven together; `stride`
            // is the channel count, so this walks all of them.
            let count = frames * buffer.stride
            let samples = data[0]
            for i in 0..<count where samples[i] != 0 { return true }
            return false
        }
        for channel in 0..<Int(buffer.format.channelCount) {
            let samples = data[channel]
            for i in 0..<frames where samples[i] != 0 { return true }
        }
        return false
    }

    /// True when the tap fired at least once before `deadline`.
    func waitForBuffer(deadline: DispatchTime) -> Bool {
        arrivalSemaphore.wait(timeout: deadline) == .success
    }

    /// True when a buffer carrying a non-zero sample arrived before
    /// `deadline`. The two waits share one deadline; see `awaitFirstBuffer`.
    func waitForAudio(deadline: DispatchTime) -> Bool {
        audibleSemaphore.wait(timeout: deadline) == .success
    }
}

/// One verdict from a `WarmEngineMonitor` observation window — spec v3 §14's
/// table, minus the timing rules (a second event restarting the window, a
/// read arriving before the window closes), which are about WHEN this is
/// computed, not what it decides.
///
/// Three cases, not two, because `.stale` and `.inconclusive` are different
/// DIAGNOSES that happen to have the same EFFECT (the flag stays set): a
/// same-device format change or an all-zero window is a device that is
/// genuinely dead; fewer than two callbacks is a window that closed before
/// the device had a chance to answer at all. Collapsing them would lose the
/// distinction the `verdict=` log line exists to preserve.
enum WarmEngineVerdict: String {
    case stale
    case clear
    case inconclusive
}

/// The counters a `WarmEngineMonitor` observation reads once its 300 ms
/// window closes, updated by the TAP on every callback in between.
///
/// Modeled on `InputGainBox` immediately above: a dedicated lock, never
/// `MicCapture`'s own `lock` (which `start()`/`stop()` can hold for seconds
/// under HAL contention — a tap callback blocking behind a device teardown
/// would stall the IO thread), and nothing here allocates or logs. `feed`
/// runs unconditionally on every buffer — cheap even with no observation in
/// flight (one lock, one increment, an OR) — so the tap never has to know
/// whether a window is currently open; `reset` is what makes an observation
/// start counting from zero, and `snapshot`'s generation check is what makes
/// a superseded window's late read a no-op instead of a wrong answer.
final class WarmEngineLivenessBox: @unchecked Sendable {
    private let boxLock = NSLock()
    private var generation = 0
    private var callbacks = 0
    private var anyAboveFloor = false

    /// Begin counting for a fresh observation `generation`. Called once per
    /// window, from `WarmEngineMonitor.beginObservation`/
    /// `beginObservationForTesting` — never from the tap.
    func reset(generation: Int) {
        boxLock.lock()
        self.generation = generation
        callbacks = 0
        anyAboveFloor = false
        boxLock.unlock()
    }

    /// Record one tap callback against whichever generation is current.
    /// Called for EVERY buffer, on the HAL's IO thread — see this type's
    /// doc comment for the realtime constraints that follow from that.
    func feed(aboveFloor: Bool) {
        boxLock.lock()
        callbacks += 1
        if aboveFloor { anyAboveFloor = true }
        boxLock.unlock()
    }

    /// The counts for `generation`, or nil when a later `reset` has since
    /// moved the box on to a newer one — the window this snapshot would
    /// have described was superseded before it could be read.
    func snapshot(generation matching: Int) -> (callbacks: Int, anyAboveFloor: Bool)? {
        boxLock.lock(); defer { boxLock.unlock() }
        guard generation == matching else { return nil }
        return (callbacks, anyAboveFloor)
    }
}

/// Turns `.AVAudioEngineConfigurationChange` from a flag-only signal into a
/// bounded, asynchronous re-check of the SAME tap's own samples — spec v3
/// §14's `WarmEngineMonitor`, and the fix for the 13-of-26 WH-1000XM5
/// needless-rebind measurement `bindingIsStale`'s doc comment describes
/// (that comment explains why the flag must stay load-bearing; this class
/// is what lets a HEALTHY same-device config event clear it again instead
/// of leaving every one stale until the next full rebuild).
///
/// One instance per `MicCapture`, owned for that engine's whole lifetime —
/// see `MicCapture.warmEngineMonitor`. Every method here is safe to call
/// from any thread; none of them touch `MicCapture.lock`, which is the
/// whole point (spec: "never inside the observer, which holds `lock`").
final class WarmEngineMonitor: @unchecked Sendable {
    /// 300 ms — spec v3 §14. Long enough for at least two tap callbacks at
    /// any device's native buffer size (native buffers run 10-20 ms across
    /// this codebase's measurements), short enough that a config event under
    /// a genuinely dead engine still resolves well inside an ordinary
    /// dictation hold.
    static let observationWindow: TimeInterval = 0.3

    /// Mirrors `MicCapture.rateTolerance`'s value and rationale (absorbing
    /// float round-tripping on whole-number HAL rates) rather than reaching
    /// across to that `private` constant — this class does not otherwise
    /// depend on `MicCapture`'s internals.
    private static let rateTolerance: Double = 1.0

    private let stateLock = NSLock()
    private var generation = 0
    private var verdict: WarmEngineVerdict?
    private let queue = DispatchQueue(
        label: "ai.scam.dictation.mic.warm-engine-monitor", qos: .utility)
    /// The tap's tee target — see `WarmEngineLivenessBox`. `MicCapture`
    /// reaches this directly (`warmEngineMonitor.liveness.feed(...)`) from
    /// its tap callback; every other interaction goes through this class.
    let liveness = WarmEngineLivenessBox()
    private var doneSemaphores: [Int: DispatchSemaphore] = [:]

    /// Begins (or, for a config event that lands mid-window, RESTARTS — spec:
    /// "a second config event during observation → restart observation") a
    /// fresh 300 ms window. Reads the bound device's own HAL rate NOW and
    /// AGAIN after the window closes (`resolve`) to answer "format changed"
    /// — see `MicCapture.boundDeviceNominalRateAtBind`'s doc comment for why
    /// that is a HAL property read (safe from any thread) rather than a
    /// touch of `AVAudioEngine` itself off the audio thread.
    ///
    /// **Both reads must agree with the bind-time rate, not just one.**
    /// A2DP→HFP can post the `.AVAudioEngineConfigurationChange`
    /// notification BEFORE the device's `kAudioDevicePropertyNominalSampleRate`
    /// has actually settled to its new value — a single read taken right at
    /// the notification can still see the OLD (matching) rate even though
    /// the format really did change underneath it. Reading again at close
    /// closes that window: `formatSame` is true only if NEITHER read saw a
    /// disagreement with `recordedRate`.
    ///
    /// - Parameters:
    ///   - boundDevice: the device this engine believes it is bound to,
    ///     read under `MicCapture.lock` by the caller before this call.
    ///   - recordedRate: that device's nominal rate AT BIND TIME.
    ///   - onVerdict: invoked once, off `queue`, when this window closes
    ///     with a verdict for a generation this monitor still considers
    ///     current (a superseded window never calls back at all — the
    ///     restart it lost to already owns the answer).
    func beginObservation(
        boundDevice: AudioDeviceID?,
        recordedRate: Double,
        onVerdict: @escaping @Sendable (Int, WarmEngineVerdict) -> Void
    ) {
        let gen = beginGeneration()
        let formatSameAtStart = Self.formatMatches(boundDevice: boundDevice, recordedRate: recordedRate)
        queue.asyncAfter(deadline: .now() + Self.observationWindow) { [weak self] in
            self?.resolve(
                generation: gen, boundDevice: boundDevice, recordedRate: recordedRate,
                formatSameAtStart: formatSameAtStart, onVerdict: onVerdict)
        }
    }

    /// One HAL read: does `boundDevice`'s CURRENT nominal sample rate still
    /// agree with `recordedRate` (the bind-time reading)? An unresolved
    /// `boundDevice` can never be "the same format" — mirrors
    /// `bindingIsStale`'s own `guard let bound else { return true }`.
    private static func formatMatches(boundDevice: AudioDeviceID?, recordedRate: Double) -> Bool {
        guard let boundDevice else { return false }
        let currentRate = MicCapture.nominalSampleRate(boundDevice)
        return currentRate > 0 && abs(currentRate - recordedRate) < Self.rateTolerance
    }

    /// Test seam: the same generation/restart machinery and the same real
    /// 300 ms window `beginObservation` runs, but with FIXED liveness
    /// numbers fed directly into the box instead of tap callbacks, and a
    /// fixed FINAL `formatSame` — standing in for what `beginObservation`'s
    /// two real HAL reads would have AND'ed together — instead of any HAL
    /// read. No device to read twice in a self-check that never opens one.
    /// See `DictationInputFollowSelfCheck` for the acceptance matrix this
    /// drives.
    func beginObservationForTesting(
        callbacks: Int, anyActive: Bool, formatSame: Bool,
        onVerdict: @escaping @Sendable (Int, WarmEngineVerdict) -> Void
    ) {
        let gen = beginGeneration()
        for _ in 0..<callbacks {
            liveness.feed(aboveFloor: anyActive)
        }
        queue.asyncAfter(deadline: .now() + Self.observationWindow) { [weak self] in
            self?.resolveForTesting(generation: gen, formatSame: formatSame, onVerdict: onVerdict)
        }
    }

    private func beginGeneration() -> Int {
        stateLock.lock()
        generation += 1
        let gen = generation
        verdict = nil
        stateLock.unlock()
        liveness.reset(generation: gen)
        return gen
    }

    private func resolve(
        generation gen: Int, boundDevice: AudioDeviceID?, recordedRate: Double,
        formatSameAtStart: Bool,
        onVerdict: (Int, WarmEngineVerdict) -> Void
    ) {
        stateLock.lock()
        let isCurrent = generation == gen
        stateLock.unlock()
        // Superseded by a later config event's own window, which owns the
        // answer now — this one contributes nothing, not even a log line.
        guard isCurrent else { return }
        guard let snap = liveness.snapshot(generation: gen) else { return }
        // The second of the two required reads — see `beginObservation`'s
        // doc comment for why one alone can miss a format change the A2DP→HFP
        // transition posted before the HAL property had settled.
        let formatSameAtClose = Self.formatMatches(boundDevice: boundDevice, recordedRate: recordedRate)
        let formatSame = formatSameAtStart && formatSameAtClose
        finish(generation: gen, snap: snap, formatSame: formatSame, onVerdict: onVerdict)
    }

    /// Test-seam counterpart to `resolve`: same generation/snapshot check,
    /// but `formatSame` is the fixed value the self-check supplied directly
    /// (standing in for both HAL reads `resolve` would otherwise AND
    /// together) rather than anything read from a device.
    private func resolveForTesting(
        generation gen: Int, formatSame: Bool,
        onVerdict: (Int, WarmEngineVerdict) -> Void
    ) {
        stateLock.lock()
        let isCurrent = generation == gen
        stateLock.unlock()
        guard isCurrent else { return }
        guard let snap = liveness.snapshot(generation: gen) else { return }
        finish(generation: gen, snap: snap, formatSame: formatSame, onVerdict: onVerdict)
    }

    /// Shared tail of `resolve`/`resolveForTesting`: classify, record,
    /// log, wake `awaitVerdict`, and report back.
    private func finish(
        generation gen: Int, snap: (callbacks: Int, anyAboveFloor: Bool), formatSame: Bool,
        onVerdict: (Int, WarmEngineVerdict) -> Void
    ) {
        let result = Self.classify(
            callbacks: snap.callbacks, anyAboveFloor: snap.anyAboveFloor, formatSame: formatSame)
        stateLock.lock(); verdict = result; stateLock.unlock()
        let line = "[mic] config change verdict=\(result.rawValue) "
            + "callbacks=\(snap.callbacks) active=\(snap.anyAboveFloor) "
            + "formatSame=\(formatSame) gen=\(gen)\n"
        FileHandle.standardError.write(Data(line.utf8))
        signalDone(generation: gen)
        onVerdict(gen, result)
    }

    /// Pure classification — the whole of spec §14's verdict table that is
    /// actually a function of the observation's own numbers. First match:
    /// a format change is decisive regardless of how much activity the
    /// window otherwise saw (a device that changed format and is also
    /// making noise is still not the device we started with).
    static func classify(
        callbacks: Int, anyAboveFloor: Bool, formatSame: Bool
    ) -> WarmEngineVerdict {
        guard formatSame else { return .stale }
        guard callbacks >= 2 else { return .inconclusive }
        return anyAboveFloor ? .clear : .stale
    }

    /// True while `generation` is still this monitor's current window —
    /// `MicCapture.applyWarmEngineVerdict` checks this before clearing the
    /// flag, so a verdict for a generation a later event has since
    /// superseded can never win a race against that event's own flag set.
    func isCurrent(generation gen: Int) -> Bool {
        stateLock.lock(); defer { stateLock.unlock() }
        return generation == gen
    }

    /// The monitor's current observation generation — not `private`: spec
    /// v3 §14's failure-log line wants "the engine generation", and this is
    /// the one generation counter this class exposes (see
    /// `MicCapture.warmEngineGeneration()`).
    var currentGeneration: Int {
        stateLock.lock(); defer { stateLock.unlock() }
        return generation
    }

    /// Test seam: blocks up to `timeout` for the CURRENT generation's
    /// verdict, returning nil while it is still pending — spec: "pending at
    /// trigger → stale" reads a nil here as "the flag is unchanged".
    func awaitVerdict(timeout: TimeInterval) -> WarmEngineVerdict? {
        let gen = currentGeneration
        let sem = DispatchSemaphore(value: 0)
        stateLock.lock()
        // Already resolved by the time this seam ran (very short timeout in
        // series with prior calls, or an already-fast test double).
        if verdict != nil, generation == gen {
            let existing = verdict
            stateLock.unlock()
            return existing
        }
        doneSemaphores[gen] = sem
        stateLock.unlock()
        _ = sem.wait(timeout: .now() + timeout)
        stateLock.lock(); defer { stateLock.unlock() }
        return generation == gen ? verdict : nil
    }

    private func signalDone(generation gen: Int) {
        stateLock.lock()
        let sem = doneSemaphores.removeValue(forKey: gen)
        stateLock.unlock()
        sem?.signal()
    }
}
