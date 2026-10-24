import Foundation

/// Software input gain for captured microphone audio: dB ↔ linear, a soft
/// knee, and the calibration arithmetic behind the "speak normally for three
/// seconds" button.
///
/// **Why gain and not a lower silence threshold.** The reported defect was a
/// Bluetooth headset (WH-1000XM5) sitting at macOS input volume 0.27 — the
/// signal is attenuated at the source, before any the app code sees it. The
/// obvious-looking fix, lowering the level at which the app decides you spoke,
/// is the wrong knob twice over. It would break a deliberately pinned
/// invariant (`AsrGatePolicy.dictation.audibleThreshold ==
/// SegmenterConstants.silenceRMS`, both `0.004` — see `AudioGate`), and it
/// would hand Whisper the same too-quiet audio its decoder free-runs on,
/// emitting `"Thank you."` and other training-set artifacts. Gain fixes both
/// halves: a whisper peaking at `0.002` today fails the gate as
/// `nothingHeard`; at +16 dB it peaks at `0.013`, clears the **unchanged**
/// `0.004` gate, AND reaches the decoder at a sane amplitude. Gain is applied
/// before the gate, never instead of it.
///
/// Pure and stateless, so the meter, the gate, the retained WAVs and ASR all
/// see the same samples — see `MicCapture.handleInputBuffer`, the single
/// choke point every the app consumer of microphone audio passes through.
public enum InputGain {
    /// The shipped default, and the bottom of the range: unity. `apply`
    /// early-outs here (see below), so a user who never touches this feature
    /// gets today's audio path bit for bit.
    public static let minDB: Float = 0

    /// +24 dB ≈ 15.85×. The cap is not a round number chosen for looks: gain
    /// amplifies the room's noise floor along with the voice, and for meeting
    /// notes that means more marginal clips reaching a decoder that
    /// hallucinates on near-silence. `AsrGatePolicy.meetingNotes`'
    /// `minVoicedSeconds` / `minVoicedFraction` floors are the defence, and
    /// this cap is what keeps them defending a signal they still recognise.
    public static let maxDB: Float = 24

    /// What calibration aims the p90 of the user's speech peaks at.
    ///
    /// **0.35, not 0.9.** Two reasons, both about what calibration did NOT
    /// hear: headroom for the loudest syllable of a sentence the user did not
    /// speak during those three seconds, and a refusal to run the room's
    /// noise floor up along with the voice. Targeting near full scale would
    /// make every calibration a clipping calibration for the next loud word.
    public static let calibrationTargetPeak: Float = 0.35

    /// Where the soft knee starts. Below this a gained sample is passed
    /// through untouched — the compression only exists to stop a transient
    /// squaring off at ±1.0, so it must not colour the body of the signal
    /// that Whisper actually reads.
    public static let kneeStart: Float = 0.7

    /// Clamp to the shipped range. Every path that can produce a dB figure —
    /// calibration, the slider, a hand-edited preference — goes through this,
    /// so no unclamped multiplier can reach the audio path.
    public static func clampDB(_ dB: Float) -> Float {
        guard dB.isFinite else { return minDB }
        return min(max(dB, minDB), maxDB)
    }

    /// dB → amplitude multiplier. `20 * log10` (amplitude, not power), so
    /// +6 dB is 2×.
    public static func linear(fromDB dB: Float) -> Float {
        powf(10, dB / 20)
    }

    /// Amplitude multiplier → dB.
    ///
    /// A non-positive multiplier is `-∞` dB, which is not a number any caller
    /// here can use; it collapses to `minDB` (unity) — the safe end of the
    /// range, matching what `clampDB` would do with it anyway.
    public static func dB(fromLinear linear: Float) -> Float {
        guard linear > 0, linear.isFinite else { return minDB }
        return 20 * log10f(linear)
    }

    /// Multiply, then knee, per sample.
    ///
    /// **The `linearGain == 1` early-out is load-bearing, and is not a
    /// `* 1.0` multiply.** 0 dB is the shipped default, so the default audio
    /// path must stay bit-identical to what the app captured before this feature
    /// existed: the same array instance's contents, no float round trip, no
    /// knee applied to samples that were already above `kneeStart` before any
    /// gain existed. The dictation shadow harness compares pre/post clips —
    /// a silent change here would show up as a WER delta nobody could
    /// attribute.
    public static func apply(_ samples: [Float], linearGain: Float) -> [Float] {
        guard linearGain != 1, linearGain.isFinite else { return samples }
        var out = samples
        for i in out.indices {
            out[i] = softKnee(out[i] * linearGain)
        }
        return out
    }

    /// The knee itself, on the magnitude, sign preserved:
    ///
    ///     |y| <= 0.7  →  |y|
    ///     |y| >  0.7  →  0.7 + 0.3 * tanh((|y| - 0.7) / 0.3)
    ///
    /// Continuous at 0.7 (`tanh(0) == 0`) and C¹ there (`tanh'(0) == 1`, and
    /// the `0.3 / 0.3` scaling cancels), monotonic because `tanh` is, and
    /// strictly under 1.0 because `tanh < 1`. That last property is why this
    /// is a knee and not a clamp: hard clipping a gained transient adds
    /// broadband harmonics right where Whisper is listening, whereas this
    /// compresses it.
    public static func softKnee(_ value: Float) -> Float {
        let magnitude = abs(value)
        guard magnitude > kneeStart else { return value }
        let over = magnitude - kneeStart
        let compressed = kneeStart + (1 - kneeStart) * tanhf(over / (1 - kneeStart))
        return value < 0 ? -compressed : compressed
    }

    /// The dB figure the `Calibrate` button offers, from the per-chunk peaks
    /// collected over ~3 s of speech.
    ///
    /// **p90, not max and not mean.** The max is one lip smack or one chair
    /// creak away from setting everyone's gain from a single outlier sample;
    /// the mean is dragged down by the gaps between words, which are most of
    /// any three-second utterance and are not what we are trying to make
    /// audible. The p90 is the loud part of actual speech.
    ///
    /// Returns `nil` when the p90 does not clear `audibleFloor` (pass
    /// `SegmenterConstants.silenceRMS` — the same `0.004` the gate uses).
    /// **Hearing nothing at all is a dead device, not a quiet one**: a muted
    /// headset, a mic another app has clamped, a device that bound but never
    /// delivered. Handing that case a confident "+24 dB" would hide a real
    /// failure behind a number the user would then trust. Calibration refuses
    /// instead, and the caller says so.
    public static func calibratedDB(
        peaks: [Float],
        targetPeak: Float = InputGain.calibrationTargetPeak,
        audibleFloor: Float
    ) -> Float? {
        guard !peaks.isEmpty else { return nil }
        let sorted = peaks.sorted()
        // Nearest-rank p90: for n = 10 this is the 9th value, and for n = 1
        // it is that single value rather than an out-of-bounds read.
        let rank = Int((0.9 * Float(sorted.count)).rounded(.up))
        let p90 = sorted[min(max(rank - 1, 0), sorted.count - 1)]
        guard p90 > audibleFloor else { return nil }
        return clampDB(dB(fromLinear: targetPeak / p90))
    }
}
