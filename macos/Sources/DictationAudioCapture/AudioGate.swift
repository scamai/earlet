import Foundation

/// How much audible content a clip must contain before it is worth a Whisper
/// round trip.
///
/// **Why this type exists.** Whisper does not return "nothing" for silence:
/// handed near-silence its decoder free-runs and emits training-set
/// artifacts — `"Thank you."`, `"Thanks for watching"`, YouTube outros, often
/// in another language. That is what the model DOES, not a bug that can be
/// patched downstream, so silence must never be handed to it.
///
/// Dictation learned that in W-2 and grew a gate
/// (`DictationSession.containsAudibleSignal`). **Meeting notes never got
/// one** — `NotesSessionController.handleUtterance` computed the clip's peak
/// for its LOG LINE and then enqueued unconditionally — so the notes
/// transcript filled with `"Thank you."` on every quiet stretch of a real
/// meeting. Two features, one question ("is this clip worth transcribing?"),
/// two answers, one of them absent: that split WAS the bug.
///
/// So there is exactly one implementation, in the module both features
/// already depend on, and the only thing that differs between them is this
/// policy value. A future feature that captures audio inherits the answer by
/// construction instead of re-deriving it (or forgetting to).
public struct AsrGatePolicy: Sendable, Equatable {
    /// Peak/RMS amplitude (0...1) at or below which audio counts as silence.
    /// Strictly `>` to be audible, matching `AudioLevelModel`'s own
    /// `peak > audibleThreshold` meter test, so the pill and the outcome
    /// cannot disagree at the boundary.
    ///
    /// This is `SegmenterConstants.silenceRMS` everywhere — the same number
    /// the segmenter itself uses to decide what a voiced frame is. A gate
    /// with a different idea of "audible" than the segmenter that produced
    /// the clip would be reasoning about a different signal than the one it
    /// was handed.
    public var audibleThreshold: Float

    /// Minimum ABSOLUTE voiced duration, in seconds. Guards the "brief blip
    /// in a long quiet clip" shape — a door closing, a keyboard, or the
    /// residue an AEC noise gate leaves after it clamps the mic.
    public var minVoicedSeconds: Double

    /// Minimum voiced FRACTION of the clip. Guards the same shape from the
    /// other side: a 20 s utterance holding 0.5 s of signal clears any
    /// absolute floor worth setting, and is still 97.5% silence being fed to
    /// a decoder that hallucinates on silence.
    public var minVoicedFraction: Double

    /// Short analysis frames keep a click from borrowing most of a longer
    /// frame's duration. Other pipelines retain the segmenter's 30 ms grid.
    public var frameSeconds: Double

    public init(audibleThreshold: Float, minVoicedSeconds: Double, minVoicedFraction: Double,
                frameSeconds: Double = 0.03) {
        self.audibleThreshold = audibleThreshold
        self.minVoicedSeconds = minVoicedSeconds
        self.minVoicedFraction = minVoicedFraction
        self.frameSeconds = frameSeconds
    }

    /// Push-to-talk rejects a brief transient in otherwise quiet audio.
    ///
    /// A peak alone admits a key click, and the model can confidently decode
    /// that noise as words. Require 90 ms of energy across 10 ms analysis
    /// frames, independent of microphone callback size. This still admits a
    /// 120 ms word. There is no fraction floor: one short word in a long hold
    /// must survive. This is an impulse guard, not a general speech classifier.
    public static let dictation = AsrGatePolicy(
        audibleThreshold: SegmenterConstants().silenceRMS,
        minVoicedSeconds: 0.09,
        minVoicedFraction: 0,
        frameSeconds: 0.01
    )

    /// Meetings: the clip must hold real speech, not a blip.
    ///
    /// The floors are affordable here in a way they are not for dictation,
    /// because the trade-off is inverted. Nobody is watching one utterance
    /// land; the segmenter already refuses to emit anything under
    /// `minUtteranceSeconds` (1.0 s), so a gated clip costs at most one
    /// second of a transcript that has hundreds — while an accepted silent
    /// clip costs a fabricated `"Thank you."` sentence attributed to a real
    /// person in a document they will read later and trust.
    ///
    /// 0.3 s: comfortably under the shortest real conversational turn
    /// ("yeah", "mhm" — 0.4-0.6 s with their surrounding voiced frames) and
    /// comfortably over the AEC residue observed in the field (peak 0.03-0.15
    /// spread thin across multi-second utterances). 0.15: a clip that is
    /// 85% silence is not an utterance.
    public static let meetingNotes = AsrGatePolicy(
        audibleThreshold: SegmenterConstants().silenceRMS,
        minVoicedSeconds: 0.3,
        minVoicedFraction: 0.15
    )
}

/// The gate's verdict. `.transcribe` carries the measured voiced fraction so
/// the caller can hand it downstream (`voiced_fraction` on the `transcribe`
/// op) — the Python side uses it to arm the hallucination-phrase blocklist
/// only on clips that were thin to begin with, rather than censoring a
/// meeting where someone genuinely said "thank you".
public enum GateDecision: Sendable, Equatable {
    case transcribe(voicedFraction: Double)
    /// A short, stable, log-safe token — `empty`, `silent`,
    /// `insufficient-voiced`. Deliberately not a sentence: it goes into
    /// session logs that get grepped.
    case skip(reason: String)
}

/// The single pre-ASR audibility gate. Pure, allocation-light, no state:
/// the verdict is derived only from THIS clip, so nothing leaks between
/// utterances or between sessions.
public enum AudioGate {
    /// - Parameters:
    ///   - samples: mono Float32 in [-1, 1] at `sampleRate`.
    ///   - sampleRate: Hz. 16 kHz on every The app capture path today; taken as
    ///     a parameter rather than assumed so the voiced-duration floor stays
    ///     a real duration if that ever changes.
    ///   - policy: see `AsrGatePolicy.dictation` / `.meetingNotes`.
    ///
    /// Cost: one pass for the peak, one for the frame RMS — tens of
    /// microseconds against the ~1 s ASR round trip it can skip.
    public static func shouldTranscribe(
        samples: [Float],
        sampleRate: Int,
        policy: AsrGatePolicy
    ) -> GateDecision {
        guard !samples.isEmpty else { return .skip(reason: "empty") }

        // Whole-clip audibility, first and cheapest: if literally nothing
        // crossed the line there is nothing to measure. This is the exact
        // test W-2 shipped for dictation, preserved bit-for-bit.
        var peak: Float = 0
        for sample in samples {
            let magnitude = abs(sample)
            if magnitude > peak { peak = magnitude }
        }
        guard peak > policy.audibleThreshold else { return .skip(reason: "silent") }

        let frames = energyFrames(samples: samples, sampleRate: sampleRate,
                                  threshold: policy.audibleThreshold,
                                  frameSeconds: policy.frameSeconds)
        let fraction = frames.count > 0 ? Double(frames.voiced) / Double(frames.count) : 0

        // A caller with both floors disabled asks for the peak test only.
        // Short-circuiting here keeps a sub-frame clip, whose
        // voiced fraction is definitionally 0, from being rejected by a floor
        // that is not set.
        if policy.minVoicedSeconds <= 0 && policy.minVoicedFraction <= 0 {
            return .transcribe(voicedFraction: fraction)
        }

        // Only analyzed samples contribute duration. Multiplying the
        // fraction by the full clip would count the unexamined tail too.
        let voicedSeconds = Double(frames.voiced * frames.length) / Double(max(1, sampleRate))
        guard voicedSeconds >= policy.minVoicedSeconds,
              fraction >= policy.minVoicedFraction else {
            return .skip(reason: "insufficient-voiced")
        }
        return .transcribe(voicedFraction: fraction)
    }

    /// Share of analysis frames whose RMS clears `threshold`.
    ///
    /// Reuses `UtteranceSegmenter.voicedFrames` — the SAME per-frame RMS the
    /// segmenter used to cut this utterance out of the stream — rather than a
    /// second energy measure. Two different definitions of "voiced" in one
    /// pipeline is how a clip becomes an utterance under one rule and silence
    /// under another.
    ///
    /// Returns 0 for a clip shorter than one frame: there is nothing to
    /// average, and 0 is the honest answer (callers with no voiced floor
    /// short-circuit before this can reject them).
    public static func voicedFraction(samples: [Float], sampleRate: Int, threshold: Float) -> Double {
        let frames = energyFrames(samples: samples, sampleRate: sampleRate,
                                  threshold: threshold, frameSeconds: 0.03)
        return frames.count > 0 ? Double(frames.voiced) / Double(frames.count) : 0
    }

    private static func energyFrames(samples: [Float], sampleRate: Int, threshold: Float,
                                     frameSeconds: Double) -> (voiced: Int, count: Int, length: Int) {
        let constants = SegmenterConstants(sampleRate: sampleRate, frameSeconds: frameSeconds,
                                           silenceRMS: threshold)
        let frame = constants.frameLength
        guard frame > 0, samples.count >= frame else { return (0, 0, 0) }
        let count = samples.count / frame
        let voiced = UtteranceSegmenter.voicedFrames(
            samples, frame: frame, count: count, threshold: threshold)
        return (voiced.filter { $0 }.count, count, frame)
    }
}
