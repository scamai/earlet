import Foundation
import DictationAudioCapture

/// Everything measurable about ONE dictation press, kept so the next failure
/// explains itself instead of being re-derived from a user's memory.
///
/// Buffer arrival and signal readiness are different: a wireless device can
/// deliver exact zeros while waking up. Record zero lead-in, all-zero status
/// and chunk peaks for successful and failed presses alike, so diagnostics
/// can distinguish no input from an audio gate rejecting quiet input.
///
/// A user-triggered hold is treated as an attempt to speak. An empty capture
/// is reported as a capture fault; this is a diagnostic policy, not a measured
/// claim about how often users speak.
///
/// ## What must never be in here
///
/// Numbers, durations, and device names only. **No transcript text and no
/// audio content, ever** — this record is written to disk on every press and
/// shown in the mic-test panel behind a copy button, so anything in it is
/// something the user will paste into a bug report. Peaks and RMS are
/// aggregates and carry no intelligible speech; a sample array would.
public struct DictationCaptureForensics: Sendable, Codable, Equatable {
    /// Wall-clock instant of trigger-down. Wall clock, not `systemUptime`,
    /// specifically so a record can be lined up against the shipped log's
    /// timestamps — that alignment ("09:10:08 nothingHeard" next to
    /// "09:10:08 leadingZero 1.039 s") is how the headset defect was read
    /// out of 50 clips in the first place.
    public var startedAt: Date

    // MARK: - Device
    //
    // Named, because the whole finding is device-shaped: every zero lead-in
    // measured was the headset and none was the built-in microphone. A
    // record that says "0.56 s of digital silence" without saying WHICH
    // input produced it cannot distinguish "this device wakes slowly" from
    // "this Mac is broken".

    public var deviceName: String
    public var deviceUID: String
    /// `"bluetooth"` | `"built-in"` | `"usb"` | `"virtual"` | `"unknown"`.
    ///
    /// A free string rather than an enum on purpose: this is a Codable
    /// forensic record that outlives the build that wrote it, and a decoder
    /// meeting a transport it has never heard of must not fail to read the
    /// zero lead-in sitting next to it.
    ///
    /// `"bluetooth"` is the one value that changes the reading of every
    /// other field here — it is the transport on which a leading zero run is
    /// EXPECTED (the HFP wake-up), so a cause derived from these numbers may
    /// only call the wake-up normal when this says so.
    public var transport: String
    /// How many times the capture engine saw an `AVAudioEngineConfigurationChange`
    /// between press start and stop. A hold whose engine was restarted by
    /// an audio ROUTE change (a Bluetooth headset flipping A2DP⇄HFP while
    /// another input was open, 2026-09-14) hears nothing for a reason that
    /// is not the microphone's — the audio-source adapter must not count
    /// such a hold as dead evidence against the device.
    public var engineRestartsDuringPress: Int
    /// Sample rate of the samples actually retained — i.e. of the buffer the
    /// zero-run below was counted in, and of `CaptureResult.sampleRate`.
    public var engineFormatRate: Double
    /// The bound device's CoreAudio nominal rate at stop.
    ///
    /// Kept beside `engineFormatRate` because a Bluetooth headset's nominal
    /// rate COLLAPSES when the mic opens (A2DP's wideband rate drops to the
    /// HFP voice rate). That collapse is the same event as the silent
    /// wake-up, seen from the device side rather than from the samples, and
    /// two independent witnesses to one event are what let a reader rule out
    /// a bug in the counting.
    public var deviceNominalRate: Double
    /// False on every dictation press today — `DictationCapture.init` builds
    /// its `MicCapture` with `voiceProcessingAllowed: false`, because the
    /// voice processor's noise gate ate normal-volume speech on a low-gain
    /// built-in mic (2026-08-09 field report). Recorded anyway, and recorded
    /// as measured rather than assumed: "the AEC gate clamped the mic" and
    /// "the headset had not woken up" produce the same near-zero peak, and
    /// the only cheap way to keep telling them apart is to have written down
    /// which one was switched on.
    public var echoCancellationActive: Bool
    /// Input gain applied to every buffer, in dB.
    ///
    /// Hypothesis 2 of the diagnosis — "the new input-gain work caused it" —
    /// was REJECTED because the gain was 0/+1 dB (x1.12) on the failures,
    /// and it could only be rejected because the value had been written
    /// down. It stays written down.
    public var gainDB: Float

    // MARK: - Timing (milliseconds; nil means NOT MEASURED, never zero)
    //
    // Nil and 0.0 are different findings and must not be confused: 0 ms
    // would mean the engine came up instantly, nil means nobody looked.

    /// `AVAudioEngine.start()` duration. Only the cold path pays it.
    public var engineStartMs: Double?
    /// Wait for the first buffer to ARRIVE. Measured XM5 median 305 ms, max
    /// 628 ms vs built-in 106 ms — roughly 3x, and paid twice as often
    /// because half the XM5 binds re-bound to the device they were already
    /// bound to.
    public var firstBufferMs: Double?
    /// Wait for the first buffer with SIGNAL in it. This is the number the
    /// arrival latch could not see; the gap between it and `firstBufferMs`
    /// IS the silent wake-up.
    public var firstAudibleMs: Double?
    /// Trigger-down to trigger-up, as the user experienced it. Hypothesis 3
    /// — "the user releases the key too quickly" — was rejected against this
    /// field: retained holds ran 1.4-4.1 s while the engine delivered only
    /// 0.51 s of audio. A short CAPTURE under a long HOLD is a capture-side
    /// fault and this pair is what shows it.
    public var holdSeconds: Double
    /// True when the press ran the warm path (`armTriggerDown()` on an
    /// already-running engine), false when it paid a cold `start()`. The
    /// wake-up is supposed to be paid at warm-up; a warm press that still
    /// shows a zero lead-in means it was not.
    public var wasWarm: Bool

    // MARK: - Audio shape — the load-bearing part

    /// Chunks appended to the UTTERANCE during the hold. Excludes the
    /// pre-roll prefix, which was collected before trigger-down and is
    /// counted by `preRollSamples`.
    public var chunkCount: Int
    /// Samples in the final retained buffer, pre-roll prefix included.
    public var sampleCount: Int
    public var durationSeconds: Double
    /// Samples the pre-roll ring contributed at `armTriggerDown()`.
    ///
    /// Load-bearing for reading `leadingZeroSamples`: 4800 samples is the
    /// pre-roll capacity (0.3 s x 16 kHz) EXACTLY, and a capture whose whole
    /// content is 4800 samples is a replayed ring, not an utterance — the
    /// separate defect `renewMicIfSpent()` documents.
    public var preRollSamples: Int
    /// Length of the run of EXACT `0` samples at the head of the retained
    /// buffer.
    ///
    /// Exact zero, not "below a floor": a quiet room measures small non-zero
    /// numbers, and a device that has not woken up measures literal `0`.
    /// That distinction is the entire diagnosis, so this is counted with
    /// `== 0` and on the real retained buffer — never estimated from chunk
    /// peaks, which round a near-silent chunk and a dead one together.
    public var leadingZeroSamples: Int
    public var leadingZeroSeconds: Double
    /// Every sample exactly `0` — a dead stream, not a quiet room.
    ///
    /// Under the capture-fault policy this is never a
    /// quiet user and always a fault, and it is the one condition that can
    /// be asserted from the numbers alone with no threshold to argue about.
    public var allZero: Bool
    public var peak: Float
    public var rms: Float
    /// Chunks whose peak was at or below the audible floor
    /// (`AsrGatePolicy.dictation.audibleThreshold`, 0.004 — the same number
    /// the segmenter and the gate use, so this cannot disagree with the
    /// verdict the user was given).
    public var silentChunkCount: Int
    /// Per-chunk peaks, capped at `chunkPeakCap` — **the FIRST N, not the
    /// last**.
    ///
    /// The wake-up is at the HEAD of the capture: the measured ramps run
    /// 0.56-1.23 s, i.e. the first ~7-15 chunks at ~85 ms per tap callback.
    /// Keeping the head preserves the shape that identifies the defect;
    /// keeping the tail would preserve the part every clip agrees on. 200
    /// chunks is ~17 s of audio, comfortably past the 28 s cap's realistic
    /// use and far past anything the wake-up can reach, while bounding a
    /// record that is written to disk on every single press.
    public var chunkPeaks: [Float]

    // MARK: - Verdict, filled in by the caller

    /// What the user was told (`nothingHeard`, `ok`, `noAudio`, ...).
    public var outcome: String
    /// `AudioGate`'s decision token (`transcribe`, `silent`, `empty`, ...).
    /// Written by the caller because the gate runs downstream of capture —
    /// but stored HERE so the numbers and the verdict they produced can
    /// never be read apart.
    public var gateDecision: String

    // MARK: - D15 — the in-hold watchdog's verdict
    //
    // All four are nil/absent on the overwhelming majority of presses (a hold
    // that ran to completion on one device), and `Optional` rather than a
    // sentinel so a record written by an older build still decodes.

    /// Milliseconds from trigger-down to the moment the watchdog declared the
    /// hold interrupted — nil when it never did. Non-nil is the ONE field
    /// that says "this press was discarded, not transcribed".
    public var interruptedAtMs: Double?
    /// The UID the rebind aimed at (the registry's current best source, or
    /// the resolved default). May legitimately equal
    /// `interruptedFromDeviceUID`: a fresh open often recovers a headset that
    /// flipped profile.
    public var rebindTargetUID: String?
    /// Did the rebind reopen a capture? `false` means the press ended with no
    /// live input at all.
    public var rebindSucceeded: Bool?
    /// The device the press STARTED on — the one an interruption is evidence
    /// ABOUT. `deviceUID` names the device at STOP, which after a rebind is
    /// the new one, so the two must both be recorded or the registry credits
    /// the wrong microphone.
    public var interruptedFromDeviceUID: String?

    /// The head of the capture is what matters; see `chunkPeaks`.
    public static let chunkPeakCap = 200

    /// Excessive-logging switch: `DICTATION_CAPTURE_TRACE=1` logs EVERY chunk
    /// (index, sample count, peak, zero-run) to stderr as it arrives.
    ///
    /// OFF by default and that default is deliberate: `MicCapture` taps at
    /// ~85 ms per callback, so per-chunk logging is ~12-20 lines a second
    /// for every dictation anyone ever does — it would bury the one line
    /// per press that ships unconditionally. ON, it prints the full
    /// timeline, which is the only view in which "chunk 0..6 peak 0.0000,
    /// chunk 7 peak 0.11" reads as a device waking up rather than as a
    /// broken microphone.
    ///
    /// Same shape and same reasoning as `DictationAudioCapture.AudioTrace`
    /// (`DICTATION_AUDIO_TRACE=1`); a separate variable because a dictation
    /// investigation should not have to also turn on the meeting-notes
    /// capture firehose. Read once — an env var cannot change mid-process,
    /// and this is tested on the collector's hot path.
    public static let traceEnabled =
        ProcessInfo.processInfo.environment["DICTATION_CAPTURE_TRACE"] == "1"

    /// The floor everything here calls "audible". Sourced from
    /// `AsrGatePolicy.dictation` rather than restated, so this record and
    /// the gate that produced `gateDecision` cannot drift into two different
    /// ideas of silence.
    public static var audibleThreshold: Float { AsrGatePolicy.dictation.audibleThreshold }

    public init(
        startedAt: Date = Date(),
        deviceName: String = "(unknown)",
        deviceUID: String = "(unknown)",
        transport: String = "unknown",
        engineFormatRate: Double = 0,
        deviceNominalRate: Double = 0,
        echoCancellationActive: Bool = false,
        gainDB: Float = 0,
        engineStartMs: Double? = nil,
        firstBufferMs: Double? = nil,
        firstAudibleMs: Double? = nil,
        holdSeconds: Double = 0,
        wasWarm: Bool = false,
        chunkCount: Int = 0,
        sampleCount: Int = 0,
        durationSeconds: Double = 0,
        preRollSamples: Int = 0,
        leadingZeroSamples: Int = 0,
        leadingZeroSeconds: Double = 0,
        allZero: Bool = false,
        peak: Float = 0,
        rms: Float = 0,
        silentChunkCount: Int = 0,
        chunkPeaks: [Float] = [],
        outcome: String = "",
        gateDecision: String = "",
        engineRestartsDuringPress: Int = 0,
        interruptedAtMs: Double? = nil,
        rebindTargetUID: String? = nil,
        rebindSucceeded: Bool? = nil,
        interruptedFromDeviceUID: String? = nil
    ) {
        self.interruptedAtMs = interruptedAtMs
        self.rebindTargetUID = rebindTargetUID
        self.rebindSucceeded = rebindSucceeded
        self.interruptedFromDeviceUID = interruptedFromDeviceUID
        self.startedAt = startedAt
        self.deviceName = deviceName
        self.deviceUID = deviceUID
        self.transport = transport
        self.engineFormatRate = engineFormatRate
        self.deviceNominalRate = deviceNominalRate
        self.echoCancellationActive = echoCancellationActive
        self.gainDB = gainDB
        self.engineStartMs = engineStartMs
        self.firstBufferMs = firstBufferMs
        self.firstAudibleMs = firstAudibleMs
        self.holdSeconds = holdSeconds
        self.wasWarm = wasWarm
        self.chunkCount = chunkCount
        self.sampleCount = sampleCount
        self.durationSeconds = durationSeconds
        self.preRollSamples = preRollSamples
        self.leadingZeroSamples = leadingZeroSamples
        self.leadingZeroSeconds = leadingZeroSeconds
        self.allZero = allZero
        self.peak = peak
        self.rms = rms
        self.silentChunkCount = silentChunkCount
        self.chunkPeaks = chunkPeaks
        self.outcome = outcome
        self.gateDecision = gateDecision
        self.engineRestartsDuringPress = engineRestartsDuringPress
    }

    /// One line, log-safe, no transcript text. Used for the shipped
    /// per-press log line and for the mic-test panel.
    ///
    /// Field order is not cosmetic: device and transport first (the finding
    /// is device-shaped), then the audio shape, then the timing, then the
    /// verdict. A reader scanning a column of these should be able to see
    /// "zeros=0.560s" line up under "transport=bluetooth" without parsing
    /// the rest.
    public func summaryLine() -> String {
        func ms(_ value: Double?) -> String {
            guard let value else { return "-" }
            return String(format: "%.0f", value) + "ms"
        }
        var fields: [String] = []
        fields.append("device=\"\(deviceName)\"")
        fields.append("uid=\(deviceUID)")
        fields.append("transport=\(transport)")
        fields.append("warm=\(wasWarm ? 1 : 0)")
        fields.append("hold=" + String(format: "%.2f", holdSeconds) + "s")
        fields.append("chunks=\(chunkCount)")
        fields.append("samples=\(sampleCount)")
        fields.append("dur=" + String(format: "%.2f", durationSeconds) + "s")
        fields.append("preroll=\(preRollSamples)")
        fields.append("zeros=\(leadingZeroSamples)")
        fields.append("zeroLead=" + String(format: "%.3f", leadingZeroSeconds) + "s")
        fields.append("allZero=\(allZero ? 1 : 0)")
        fields.append("peak=" + String(format: "%.4f", peak))
        fields.append("rms=" + String(format: "%.4f", rms))
        fields.append("silentChunks=\(silentChunkCount)/\(chunkCount)")
        fields.append("restarts=\(engineRestartsDuringPress)")
        if let interruptedAtMs {
            // Only on an interrupted press: four extra fields on every healthy
            // line would push the numbers that matter off the reader's screen.
            fields.append("interruptedAt=" + String(format: "%.0f", interruptedAtMs) + "ms")
            fields.append("interruptedFrom=\(interruptedFromDeviceUID ?? "-")")
            fields.append("rebindTo=\(rebindTargetUID ?? "-")")
            fields.append("rebindOk=\((rebindSucceeded ?? false) ? 1 : 0)")
        }
        fields.append("engineStart=" + ms(engineStartMs))
        fields.append("firstBuffer=" + ms(firstBufferMs))
        fields.append("firstAudible=" + ms(firstAudibleMs))
        fields.append("rate=" + String(format: "%.0f", engineFormatRate)
            + "/" + String(format: "%.0f", deviceNominalRate))
        fields.append("aec=\(echoCancellationActive ? 1 : 0)")
        fields.append("gain=" + String(format: "%+.1f", gainDB) + "dB")
        fields.append("outcome=\(outcome.isEmpty ? "-" : outcome)")
        fields.append("gate=\(gateDecision.isEmpty ? "-" : gateDecision)")
        if let cause = likelyCause() {
            fields.append("cause=\"\(cause)\"")
        }
        return fields.joined(separator: " ")
    }

    /// The single most likely cause of this press's outcome, **derived from
    /// the numbers in this record and nothing else**.
    ///
    /// This is the point of the whole exercise. "Nothing heard" is advice
    /// that was actively wrong on the measured failures — the user HAD
    /// spoken — and the reason it shipped is that no code path could tell
    /// "the room was quiet" from "the device had not woken up". Every branch
    /// below is a shape those two produce differently, in the numbers.
    ///
    /// **Returning nil is a real answer.** A press with nothing anomalous in
    /// it gets no cause; inventing one would put a confident wrong sentence
    /// in front of a user, which is the exact failure being fixed.
    public func likelyCause() -> String? {
        let floor = Self.audibleThreshold

        // 1. A dead stream. Every sample exactly 0 is not a quiet room at
        //    any gain — it is an input that delivered nothing. Per the
        //    capture-fault policy, this is a fault
        //    and needs no threshold to say so.
        if allZero && sampleCount > 0 {
            return "\(deviceName) delivered a dead stream — all \(sampleCount) samples were "
                + "exactly zero, which is a silent input, not a quiet room. "
                + "Try the built-in microphone or run the mic test."
        }

        // 2. The wake-up swallowed the hold. Measured on the WH-1000XM5:
        //    0.56-1.23 s of exact digital zeros at the head of EVERY
        //    capture, median 0.68 s, while the built-in microphone measured
        //    0.000 s. This is normal for the device and abnormal only
        //    because the hold landed inside it.
        if leadingZeroSamples > 0, durationSeconds > 0,
           leadingZeroSeconds >= durationSeconds * 0.5 {
            let lead = String(format: "%.2f", leadingZeroSeconds)
            let total = String(format: "%.2f", durationSeconds)
            let normal = transport == "bluetooth"
                ? "That wake-up is normal for a Bluetooth headset"
                : "A silent lead-in of that length is not normal for this transport"
            return "\(deviceName) was still waking up: \(lead)s of the \(total)s capture was "
                + "exact digital silence. \(normal) — the problem is that the hold landed "
                + "inside it. Hold a beat longer, or speak after the microphone is already open."
        }

        // 3. The hold was shorter than the wake-up. Signal did arrive, so
        //    the device is fine and the gain is fine; there simply was not
        //    enough of it. The measured failures captured 8192 samples =
        //    0.51 s against a wake-up window of 0.56-1.23 s.
        if peak > floor, durationSeconds > 0, durationSeconds < 1.0 {
            let total = String(format: "%.2f", durationSeconds)
            return "The hold was shorter than the microphone's wake-up: only \(total)s "
                + "(\(sampleCount) samples) of audio reached the capture, though what "
                + "arrived was audible. Hold the key a little longer."
        }

        // 4. Genuinely too quiet. Real duration, no zero lead-in to blame,
        //    and the peak still under the floor — the one shape where the
        //    honest answer is about level, not about the device waking.
        //    Finding 6 of the diagnosis: the headset also simply runs ~8 dB
        //    down (peaks 0.11-0.33 vs 0.35 built-in).
        if peak <= floor, durationSeconds >= 1.0, leadingZeroSamples == 0, sampleCount > 0 {
            let measured = String(format: "%.4f", peak)
            let gain = String(format: "%+.1f", gainDB)
            return "The input was audible but too quiet: peak \(measured) against a floor of "
                + String(format: "%.4f", floor)
                + " across \(String(format: "%.2f", durationSeconds))s, with no silent lead-in. "
                + "Raise the input gain (currently \(gain) dB) or the system input volume "
                + "for \(deviceName)."
        }

        // Nothing anomalous. Say so by saying nothing.
        return nil
    }
}
