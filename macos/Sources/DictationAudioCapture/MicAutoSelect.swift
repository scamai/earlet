import Foundation

/// Choosing the microphone that can actually hear the user, so they never
/// have to.
///
/// **The field report.** 2026-08-13: "when I click the voice button it says
/// 'no sound in your mic, choose input device'." The machine's system default
/// input was an empty 3.5 mm jack. The app bound it, faithfully captured
/// nothing, and told the user to go read a Settings pane — asking the user to
/// solve a problem the app is better placed to solve.
///
/// **Why this cannot reuse `--mic-sweep`.** That diagnostic opens with
/// `SPEAK CONTINUOUSLY while this runs` and grades devices on speech peaks
/// (`peak > 0.02`). At the moment dictation fails there is no speech left to
/// measure: the user already spoke, into the wrong device, and the audio is
/// gone. Asking them to perform for a diagnostic is the same failure as
/// asking them to visit Settings.
///
/// **The measurement that changed this design.** The first plan was to grade
/// inputs on their ambient noise floor — "a live mic delivers room tone, a
/// dead input delivers digital zero". Measured on the field machine
/// (2026-08-14, `--mic-sweep 2`), that premise is false:
///
///     BR Microphone            peak 0.9278  rms 0.0504
///     External Microphone      peak 0.0322  rms 0.0043   <- the failing default
///     MacBook Pro Microphone   peak 0.1889  rms 0.0212
///     Microsoft Teams Audio    peak 0.0000  rms 0.0000   <- virtual, the only true zero
///
/// The failing input reads 0.0043, not zero: a jack presents electrical noise
/// whether or not anything can hear through it. Only the VIRTUAL device was
/// digitally silent. Worse, the room was noisy at the time — the favourable
/// case — and the separation was still only ~5x on RMS. In a quiet room the
/// real microphones fall toward their own noise floors while the jack's
/// electrical noise stays put, so an absolute threshold is weakest exactly
/// when it would still be needed.
///
/// **What the machine actually had plugged in.** `External Microphone`
/// (`BuiltInHeadphoneInputDevice`) was PRESENT, so the jack was not empty —
/// it held plain headphones with no microphone. macOS sees a plug, creates an
/// input device for it, and promotes it to system default. That device is
/// deaf by construction. This is the common real-world shape of the bug.
///
/// So the decision leads on **device class, not loudness**: prefer the
/// built-in microphone, which is always present, always aimed at the user,
/// and never a deaf plug. Class comes from CoreAudio metadata that requires
/// no binding at all (`kAudioDevicePropertyTransportType` +
/// `kAudioDevicePropertyDataSource`), so the common case needs NO probing —
/// and therefore carries none of the HAL deadlock risk that binding devices
/// does. Measurement survives only as a tiebreak for the one case it is
/// genuinely needed: the built-in itself came back empty.
///
/// `kAudioDevicePropertyJackIsConnected` would have answered "is anything
/// plugged in" outright, but it is UNSUPPORTED on every device and every
/// scope on this hardware — checked before falling back to this.
///
/// This type holds ONLY the decision. Classifying and measuring are the
/// caller's job, injected as `[MicCandidate]`, so every rule below is
/// testable with no audio hardware and no CoreAudio.
public enum MicAutoSelect {

    /// Ambient RMS at or above which an input is treated as carrying signal.
    ///
    /// Used ONLY as a last-resort tiebreak between non-built-in candidates —
    /// never to declare a device dead, because the measurement above shows a
    /// deaf input sitting comfortably above any threshold that a real
    /// microphone in a quiet room also clears. Set below the measured deaf
    /// jack (0.0043) on purpose: its job is to exclude the digitally-silent
    /// (0.0000) case, not to adjudicate live-ness.
    public static let signalFloorThreshold: Double = 0.0002

    /// One input device, classified from CoreAudio metadata. `ambientFloor`
    /// is optional because the common path does NOT probe: class alone
    /// decides, and probing is what carries deadlock risk.
    public struct MicCandidate: Equatable, Sendable {
        public let uid: String
        public let name: String
        /// The real built-in microphone: transport `bltn` AND data source
        /// `imic`. Both halves are required — the deaf headphone-jack input
        /// on the field machine is ALSO transport `bltn`, and differs only in
        /// data source (`emic`). Matching on transport alone would classify
        /// the very device that caused this bug as the built-in mic.
        public let isBuiltIn: Bool
        /// Transport `virt` — a loopback/aggregate device (the field machine's
        /// "Microsoft Teams Audio", BlackHole, a screen-recorder tap). Never
        /// auto-select one: whatever it carries is SYSTEM audio, so the app would
        /// transcribe playback as though it were the user.
        public let isVirtual: Bool
        /// Ambient RMS over a probe window, when one was measured. `nil` on
        /// the unprobed path. Only ever a tiebreak — see `signalFloor
        /// Threshold` for why it cannot be used to declare a device dead.
        public let ambientFloor: Double?

        public init(uid: String, name: String, isBuiltIn: Bool, isVirtual: Bool,
                    ambientFloor: Double? = nil) {
            self.uid = uid
            self.name = name
            self.isBuiltIn = isBuiltIn
            self.isVirtual = isVirtual
            self.ambientFloor = ambientFloor
        }

        /// True only when a measurement exists AND it is not digital silence.
        /// An unprobed device is NOT "not carrying signal" — absence of a
        /// measurement must never read as evidence of a dead device.
        public var hasMeasuredSignal: Bool {
            guard let ambientFloor else { return false }
            return ambientFloor >= MicAutoSelect.signalFloorThreshold
        }
    }

    /// What the caller should do next.
    public enum Decision: Equatable, Sendable {
        /// Bind this device, persist its UID, retry, and say so.
        case switchTo(uid: String, name: String)
        /// The currently-bound device is live — the capture failed for some
        /// reason other than a dead input. Report the original failure
        /// unchanged rather than churning the user's device selection.
        case keepCurrent
        /// Every usable input is silent. This is NOT a device-choice problem
        /// (hardware mute, revoked permission), and switching devices would
        /// only make the user think the app is thrashing. Keep today's honest
        /// failure.
        case allSilent
        /// Another part of the app holds the microphone. Probing means binding
        /// `AVAudioEngine` against a device already in use, which is the
        /// documented Core Audio deadlock (`IOBindingChanged` ->
        /// `__psynch_mutexwait`). Never probe to prove it.
        case refusedMicBusy
    }

    /// - Parameters:
    ///   - candidates: every input device, already probed. An empty list is
    ///     treated as `.allSilent` — nothing to choose between.
    ///   - currentUID: the device that was bound when capture came back empty.
    ///   - micIsBusy: true when meeting notes (or anything else) currently
    ///     owns the microphone.
    ///   - alreadyTried: UIDs this selector already switched to during the
    ///     current run of failures. Without this, two marginally-live devices
    ///     ping-pong forever, and each bounce costs the user a retry.
    /// `systemDefaultUID` is macOS's own default input — the user's explicit
    /// OS-level choice of microphone. See the rule that consumes it below.
    public static func decide(
        candidates: [MicCandidate],
        currentUID: String?,
        micIsBusy: Bool,
        alreadyTried: Set<String> = [],
        systemDefaultUID: String? = nil
    ) -> Decision {
        // Ordered first on purpose: it is a refusal to ACT, not a conclusion
        // drawn from the measurements. When the mic is busy the caller must
        // not have probed at all, so the candidate list is meaningless here.
        if micIsBusy { return .refusedMicBusy }

        // If we DID probe the current device and it is carrying signal, the
        // empty capture had some other cause and switching would spend the
        // user's chosen microphone on a guess. Guarded on an actual
        // measurement: on the unprobed path this is silent, because "we did
        // not measure it" is not evidence that it works.
        if let currentUID,
           let current = candidates.first(where: { $0.uid == currentUID }),
           current.hasMeasuredSignal {
            return .keepCurrent
        }

        let usable = candidates.filter {
            !$0.isVirtual && $0.uid != currentUID && !alreadyTried.contains($0.uid)
        }
        guard !usable.isEmpty else { return .allSilent }

        // THE SYSTEM DEFAULT, when we are not already on it.
        //
        // This is the INVERSE of the built-in case below, and it is the one
        // that can strand a user: dictation may be pinned to
        // `BuiltInMicrophoneDevice` while the system default is another device.
        // A press can then capture no samples and fail quickly,
        // and this function returned `.allSilent` — because the built-in rule
        // could not fire (the built-in WAS the current device, so it is
        // excluded from `usable`) and the loudness rule refuses to rank an
        // unprobed device, which the AirPods were.
        //
        // The default input is the user's own explicit choice at the OS
        // level. When the device we are on produced nothing and the default
        // is a DIFFERENT usable device, that is at least as strong a signal
        // as "the built-in is always there", and it needs no probe either.
        //
        // Safe against the field bug the built-in rule exists for: a headphone
        // with no microphone element has no input channels, so it never
        // reaches `candidates` at all and can never be chosen here.
        if let systemDefaultUID, systemDefaultUID != currentUID,
           let fallback = usable.first(where: { $0.uid == systemDefaultUID }) {
            return .switchTo(uid: fallback.uid, name: fallback.name)
        }

        // THE COMMON CASE, and the original field bug. Capture came back empty
        // on something that is not the built-in microphone — headphones with
        // no mic element, a stale USB headset, a jack holding a bare cable. The
        // built-in is always physically present and always aimed at the user,
        // so it is the answer without measuring anything. No probe, no device
        // bind, no deadlock risk, no threshold to tune.
        if let builtIn = usable.first(where: { $0.isBuiltIn }) {
            return .switchTo(uid: builtIn.uid, name: builtIn.name)
        }

        // No built-in available (already tried, or this is a Mac mini with
        // nothing internal). Only here does loudness get a vote, and only
        // among devices actually measured — an UNPROBED device must not be
        // ranked as though it were silent.
        let measured = usable.filter(\.hasMeasuredSignal)
        guard let best = measured.max(by: {
            ($0.ambientFloor ?? 0) < ($1.ambientFloor ?? 0)
        }) else {
            // Nothing measurable to choose between. Falling back to "any
            // non-virtual device" would be a coin flip presented to the user
            // as a fix, so say nothing rather than churn their selection.
            return .allSilent
        }
        return .switchTo(uid: best.uid, name: best.name)
    }
}
