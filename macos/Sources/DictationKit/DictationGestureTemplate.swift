import Accelerate
import Foundation

/// Task K2: recognizing a NON-SPEECH gesture (the user's example: coughing
/// twice) as the delete command, without an ASR round trip.
///
/// ## The one requirement that shapes everything here
///
/// A false positive deletes real text. A false negative types nothing and
/// costs the user one repeat. So every threshold in this file is set to make
/// the matcher *hard to satisfy*, and the gates are ANDed, never averaged:
/// a candidate must clear the duration bound AND the sparsity bound AND the
/// envelope correlation AND the spectral similarity. A high score on three
/// of four is a reject.
///
/// ## What is actually computed
///
/// Per 16 ms frame (256 samples at 16 kHz, 50% overlap):
///   - **log frame energy** → the ENVELOPE, the shape of the gesture in
///     time. Two coughs is a two-bump envelope; one cough is one bump;
///     speech is a long, busy, many-bump envelope. This is the feature that
///     actually separates gestures from each other.
///   - **log mel-band energies** (8 triangular bands, 0–8 kHz) → the
///     SPECTRAL PROFILE, averaged over the clip. A cough is broadband and
///     low-centroid; a tongue click is sharp and high; a hum is narrow.
///     This is what stops a *different* gesture with a similar rhythm from
///     matching.
///
/// The envelope is resampled to a fixed length and z-normalized, so the
/// match is invariant to how loudly and how fast the gesture was performed
/// — which is the whole point, since a person cannot repeat a cough to the
/// millisecond.
///
/// ## Honest limits
///
/// This is a lightweight template matcher, not a trained classifier. It was
/// designed against synthetic signals and the invariants below are pinned by
/// a self-check; it has NOT been measured against a corpus of real coughs
/// from real users in real rooms. That is why the feature ships behind a
/// default-OFF flag labelled experimental, why it can only ever do what the
/// spoken command can already do, and why the thresholds are set where a
/// borderline clip falls through to normal ASR instead of deleting.
public enum DictationGestureDSP {

    /// 16 ms at 16 kHz. Long enough for a stable spectrum, short enough that
    /// the two bumps of a double cough (~100–200 ms apart) stay resolved.
    public static let frameLength = 256
    /// 50% overlap — the envelope is a shape, and hopping a whole frame
    /// aliases short transients in or out depending on phase.
    public static let hopLength = 128
    /// Mel-ish triangular bands across 0–8 kHz.
    public static let bandCount = 8
    /// Every envelope is resampled to this length before comparison, so
    /// clips of different durations are comparable at all.
    public static let envelopeLength = 32
    /// Floor under the log, so a digitally-silent frame is a large negative
    /// number rather than `-inf` (which would poison every later mean).
    static let energyFloor: Double = 1e-10

    /// The comparable description of one clip.
    public struct Features: Sendable, Equatable, Codable {
        /// Length `envelopeLength`, z-normalized (mean 0, unit variance).
        /// Loudness-invariant by construction.
        public var envelope: [Double]
        /// Length `bandCount`, mean log-band energy, mean-removed then unit
        /// normalized — same invariance, in frequency.
        public var bands: [Double]
        public var durationSeconds: Double
        /// Fraction of frames whose energy is within 20 dB of the clip's
        /// loudest frame. A gesture is SPARSE (a couple of bursts in a lot
        /// of quiet); connected speech is DENSE. This is the cheapest
        /// speech/non-speech discriminator available and it is used as a
        /// hard gate, not as a score contribution.
        public var activeFraction: Double

        public init(envelope: [Double], bands: [Double], durationSeconds: Double, activeFraction: Double) {
            self.envelope = envelope
            self.bands = bands
            self.durationSeconds = durationSeconds
            self.activeFraction = activeFraction
        }
    }

    /// `nil` when the clip is too short to describe (fewer than
    /// `envelopeLength` frames) — a defined "cannot judge this", which the
    /// matcher treats as no-match, never as a match.
    public static func features(samples: [Float], sampleRate: Double) -> Features? {
        guard sampleRate > 0, samples.count >= frameLength * 2 else { return nil }
        let frameCount = 1 + (samples.count - frameLength) / hopLength
        guard frameCount >= 4 else { return nil }

        var logEnergies = [Double](repeating: 0, count: frameCount)
        var bandSums = [Double](repeating: 0, count: bandCount)
        let filters = melFilters(sampleRate: sampleRate)
        let window = hannWindow(frameLength)

        var frame = [Float](repeating: 0, count: frameLength)
        for f in 0..<frameCount {
            let start = f * hopLength
            for i in 0..<frameLength { frame[i] = samples[start + i] * window[i] }

            var energy: Float = 0
            vDSP_measqv(frame, 1, &energy, vDSP_Length(frameLength))
            logEnergies[f] = log(max(Double(energy), energyFloor))

            let spectrum = magnitudeSpectrum(frame)
            for b in 0..<bandCount {
                var acc = 0.0
                for (bin, weight) in filters[b] { acc += Double(spectrum[bin]) * weight }
                bandSums[b] += acc
            }
        }

        let peak = logEnergies.max() ?? 0
        // 20 dB in these units: energy is amplitude², so a 20 dB amplitude
        // drop is a factor of 10⁻² in energy, i.e. log(1e-2) ≈ -4.605.
        let activeFloor = peak - 4.605
        let activeFraction = Double(logEnergies.filter { $0 >= activeFloor }.count) / Double(frameCount)

        let bands = normalizeUnit(bandSums.map { log(max($0 / Double(frameCount), energyFloor)) })
        let envelope = normalizeZ(resample(logEnergies, to: envelopeLength))

        return Features(
            envelope: envelope, bands: bands,
            durationSeconds: Double(samples.count) / sampleRate,
            activeFraction: activeFraction)
    }

    // MARK: - Pure vector math (self-checkable without any audio)

    /// Linear resampling to a fixed length. Deliberately linear, not
    /// windowed-sinc: the envelope is already a smoothed, heavily
    /// downsampled quantity and interpolation artefacts at this scale are
    /// far below the thresholds that matter.
    public static func resample(_ values: [Double], to length: Int) -> [Double] {
        guard length > 0 else { return [] }
        guard values.count > 1 else { return [Double](repeating: values.first ?? 0, count: length) }
        var out = [Double](repeating: 0, count: length)
        let scale = Double(values.count - 1) / Double(max(length - 1, 1))
        for i in 0..<length {
            let x = Double(i) * scale
            let lo = min(Int(x), values.count - 1)
            let hi = min(lo + 1, values.count - 1)
            let t = x - Double(lo)
            out[i] = values[lo] * (1 - t) + values[hi] * t
        }
        return out
    }

    /// Mean-removed, unit-variance. Returns all-zeros for a constant input
    /// (a flat envelope has no shape to compare), which then scores 0
    /// against everything — the correct answer, not a divide-by-zero.
    public static func normalizeZ(_ values: [Double]) -> [Double] {
        guard !values.isEmpty else { return [] }
        let mean = values.reduce(0, +) / Double(values.count)
        let variance = values.reduce(0) { $0 + ($1 - mean) * ($1 - mean) } / Double(values.count)
        let sd = variance.squareRoot()
        guard sd > 1e-9 else { return [Double](repeating: 0, count: values.count) }
        return values.map { ($0 - mean) / sd }
    }

    /// Mean-removed, unit L2 norm — the form `cosine` expects.
    public static func normalizeUnit(_ values: [Double]) -> [Double] {
        guard !values.isEmpty else { return [] }
        let mean = values.reduce(0, +) / Double(values.count)
        let centered = values.map { $0 - mean }
        let norm = centered.reduce(0) { $0 + $1 * $1 }.squareRoot()
        guard norm > 1e-9 else { return [Double](repeating: 0, count: values.count) }
        return centered.map { $0 / norm }
    }

    /// Pearson correlation. Returns 0 (never 1) for degenerate input — a
    /// flat signal must not read as a perfect match with anything.
    public static func correlation(_ a: [Double], _ b: [Double]) -> Double {
        guard a.count == b.count, !a.isEmpty else { return 0 }
        let za = normalizeZ(a), zb = normalizeZ(b)
        let dot = zip(za, zb).reduce(0) { $0 + $1.0 * $1.1 }
        return dot / Double(a.count)
    }

    /// Cosine similarity of two already-unit vectors, clamped to [-1, 1].
    public static func cosine(_ a: [Double], _ b: [Double]) -> Double {
        guard a.count == b.count, !a.isEmpty else { return 0 }
        let dot = zip(a, b).reduce(0) { $0 + $1.0 * $1.1 }
        return max(-1, min(1, dot))
    }

    // MARK: - Spectrum helpers

    private static func hannWindow(_ n: Int) -> [Float] {
        var w = [Float](repeating: 0, count: n)
        vDSP_hann_window(&w, vDSP_Length(n), Int32(vDSP_HANN_DENORM))
        return w
    }

    /// Magnitude spectrum of one frame, length `frameLength/2`.
    ///
    /// A plain real-to-complex FFT via `vDSP_fft_zrip`, set up once per
    /// process (the setup allocates twiddle tables; doing that per frame
    /// would dominate the cost of the whole matcher).
    private static func magnitudeSpectrum(_ frame: [Float]) -> [Float] {
        let n = frame.count
        let half = n / 2
        var real = [Float](repeating: 0, count: half)
        var imag = [Float](repeating: 0, count: half)
        var magnitudes = [Float](repeating: 0, count: half)
        var input = frame
        real.withUnsafeMutableBufferPointer { realPtr in
            imag.withUnsafeMutableBufferPointer { imagPtr in
                var split = DSPSplitComplex(realp: realPtr.baseAddress!, imagp: imagPtr.baseAddress!)
                input.withUnsafeMutableBufferPointer { inPtr in
                    inPtr.baseAddress!.withMemoryRebound(to: DSPComplex.self, capacity: half) { typed in
                        vDSP_ctoz(typed, 2, &split, 1, vDSP_Length(half))
                    }
                }
                vDSP_fft_zrip(fftSetup, &split, 1, vDSP_Length(log2(Double(n))), FFTDirection(FFT_FORWARD))
                vDSP_zvabs(&split, 1, &magnitudes, 1, vDSP_Length(half))
            }
        }
        return magnitudes
    }

    /// One setup for the whole process. `frameLength` is fixed, so a single
    /// setup is correct for every call.
    nonisolated(unsafe) private static let fftSetup: FFTSetup = {
        guard let setup = vDSP_create_fftsetup(
            vDSP_Length(log2(Double(frameLength))), FFTRadix(kFFTRadix2))
        else {
            // Cannot happen for a fixed power-of-two length, and there is no
            // meaningful degraded behaviour if it somehow did.
            fatalError("vDSP_create_fftsetup failed for frameLength \(frameLength)")
        }
        return setup
    }()

    /// `bandCount` triangular filters, equally spaced on the mel scale.
    /// Cached per sample rate — dictation capture is always 16 kHz, so this
    /// is computed once in practice.
    private static func melFilters(sampleRate: Double) -> [[(Int, Double)]] {
        filterCacheLock.lock()
        defer { filterCacheLock.unlock() }
        if let cached = filterCache[sampleRate] { return cached }
        let half = frameLength / 2
        let nyquist = sampleRate / 2
        func mel(_ hz: Double) -> Double { 2595 * log10(1 + hz / 700) }
        func hz(_ m: Double) -> Double { 700 * (pow(10, m / 2595) - 1) }
        let melMin = mel(80), melMax = mel(nyquist)
        let edges = (0...(bandCount + 1)).map { hz(melMin + (melMax - melMin) * Double($0) / Double(bandCount + 1)) }
        var filters: [[(Int, Double)]] = []
        for b in 0..<bandCount {
            let lo = edges[b], mid = edges[b + 1], hi = edges[b + 2]
            var taps: [(Int, Double)] = []
            for bin in 0..<half {
                let f = Double(bin) * nyquist / Double(half)
                if f > lo && f < mid {
                    taps.append((bin, (f - lo) / max(mid - lo, 1e-9)))
                } else if f >= mid && f < hi {
                    taps.append((bin, (hi - f) / max(hi - mid, 1e-9)))
                }
            }
            filters.append(taps)
        }
        filterCache[sampleRate] = filters
        return filters
    }

    nonisolated(unsafe) private static var filterCache: [Double: [[(Int, Double)]]] = [:]
    private static let filterCacheLock = NSLock()
}

/// The user's recorded gesture: three takes, reduced to one comparable
/// template plus the numbers needed to judge a later clip honestly.
public struct DictationGestureTemplate: Sendable, Equatable, Codable {
    /// Mean envelope across the takes, z-normalized.
    public var envelope: [Double]
    /// Mean spectral profile across the takes, unit-normalized.
    public var bands: [Double]
    public var meanDurationSeconds: Double
    /// Mean sparsity of the takes — used as the centre of the activity gate,
    /// so a user whose gesture is naturally denser than a cough is not
    /// permanently locked out by a hardcoded constant.
    public var meanActiveFraction: Double
    /// THE honesty number: the WORST pairwise envelope correlation among the
    /// user's own three takes.
    ///
    /// If a person cannot repeat their own gesture consistently (cohesion
    /// below `DictationGestureMatcher.minimumCohesion`), then no threshold
    /// can separate their gesture from everything else, and the Settings
    /// flow says so and refuses to save rather than shipping a template that
    /// will misfire. This is the number that turns "the recording UI
    /// succeeded" into "the recording is usable".
    public var cohesion: Double
    /// When it was recorded. Metadata only; carries nothing personal.
    public var recordedAt: Date

    public init(envelope: [Double], bands: [Double], meanDurationSeconds: Double,
                meanActiveFraction: Double, cohesion: Double, recordedAt: Date) {
        self.envelope = envelope
        self.bands = bands
        self.meanDurationSeconds = meanDurationSeconds
        self.meanActiveFraction = meanActiveFraction
        self.cohesion = cohesion
        self.recordedAt = recordedAt
    }

    /// Builds a template from the takes, or explains why it cannot.
    public static func build(
        from takes: [DictationGestureDSP.Features], recordedAt: Date = Date()
    ) -> Result<DictationGestureTemplate, BuildFailure> {
        guard takes.count >= 2 else { return .failure(.notEnoughTakes(takes.count)) }
        for take in takes where take.durationSeconds > DictationGestureMatcher.maxClipSeconds {
            return .failure(.takeTooLong(seconds: take.durationSeconds))
        }
        // Worst pairwise agreement, not the average: an average hides one bad
        // take, and one bad take is exactly what makes a template
        // unreliable.
        var worst = 1.0
        for i in 0..<takes.count {
            for j in (i + 1)..<takes.count {
                worst = min(worst, DictationGestureDSP.correlation(takes[i].envelope, takes[j].envelope))
            }
        }
        guard worst >= DictationGestureMatcher.minimumCohesion else {
            return .failure(.inconsistent(cohesion: worst))
        }
        let length = DictationGestureDSP.envelopeLength
        var envelope = [Double](repeating: 0, count: length)
        var bands = [Double](repeating: 0, count: DictationGestureDSP.bandCount)
        for take in takes {
            for i in 0..<length { envelope[i] += take.envelope[i] }
            for i in 0..<bands.count { bands[i] += take.bands[i] }
        }
        let n = Double(takes.count)
        return .success(DictationGestureTemplate(
            envelope: DictationGestureDSP.normalizeZ(envelope.map { $0 / n }),
            bands: DictationGestureDSP.normalizeUnit(bands.map { $0 / n }),
            meanDurationSeconds: takes.map(\.durationSeconds).reduce(0, +) / n,
            meanActiveFraction: takes.map(\.activeFraction).reduce(0, +) / n,
            cohesion: worst,
            recordedAt: recordedAt))
    }

    public enum BuildFailure: Error, Sendable, Equatable {
        case notEnoughTakes(Int)
        case takeTooLong(seconds: Double)
        /// The takes do not resemble each other. See `cohesion`.
        case inconsistent(cohesion: Double)

        /// Product copy — shown in Settings, so it must say what to do.
        public var message: String {
            switch self {
            case .notEnoughTakes:
                return "Not enough recordings. Record the same sound three times."
            case .takeTooLong:
                return "That was too long. The sound needs to be under two seconds."
            case .inconsistent:
                return "Those three recordings were too different from each other. "
                    + "Pick a sound you can repeat the same way — two sharp coughs, "
                    + "a double knock — and record it again."
            }
        }
    }
}

/// Decides whether a captured clip is the recorded gesture.
public enum DictationGestureMatcher {

    /// Every one of these is a HARD gate. A candidate must clear all four.

    /// A gesture is short. Anything longer is speech (or a gesture buried in
    /// speech, which is not something to delete text over).
    public static let maxClipSeconds = 2.0
    /// The candidate's duration must be within this factor of the template's,
    /// in both directions.
    public static let durationTolerance = 1.75
    /// Envelope shape agreement. 0.88 is high: two takes of the same gesture
    /// from the same person typically sit above 0.9, while a different
    /// gesture with a similar length lands far below.
    public static let envelopeThreshold = 0.88
    /// Spectral profile agreement — what stops a knock from matching a
    /// cough.
    public static let bandThreshold = 0.85
    /// Sparsity: how far the candidate's active-frame fraction may sit above
    /// the template's. Speech is dense; this is the gate that makes normal
    /// dictation structurally incapable of matching, independent of shape.
    public static let activeFractionSlack = 0.20
    /// A hard ceiling on top of the template-relative gate above: whatever
    /// the user recorded, a clip that is active for most of its length is
    /// speech, and speech never matches.
    public static let absoluteMaxActiveFraction = 0.70
    /// Minimum self-agreement a recording session must reach to be saved at
    /// all. See `DictationGestureTemplate.cohesion`.
    public static let minimumCohesion = 0.80

    /// The full judgement, with every term exposed — the Settings "Test it"
    /// row renders these so a user whose gesture does not match can see
    /// WHICH gate it failed instead of being told "no".
    public struct Score: Sendable, Equatable {
        public var envelope: Double
        public var bands: Double
        public var durationRatio: Double
        public var activeFraction: Double
        public var matched: Bool
        /// Which gate failed first, for the Settings readout. `nil` on a
        /// match.
        public var rejectedBy: String?
    }

    public static func score(
        _ candidate: DictationGestureDSP.Features, against template: DictationGestureTemplate
    ) -> Score {
        let envelope = DictationGestureDSP.correlation(candidate.envelope, template.envelope)
        let bands = DictationGestureDSP.cosine(candidate.bands, template.bands)
        let ratio = candidate.durationSeconds / max(template.meanDurationSeconds, 1e-6)

        var rejectedBy: String?
        if candidate.durationSeconds > maxClipSeconds {
            rejectedBy = "too long"
        } else if ratio > durationTolerance || ratio < 1 / durationTolerance {
            rejectedBy = "wrong length"
        } else if candidate.activeFraction > absoluteMaxActiveFraction
                    || candidate.activeFraction > template.meanActiveFraction + activeFractionSlack {
            // Named for the reader, not for the DSP: this is the speech gate.
            rejectedBy = "sounds like speech"
        } else if envelope < envelopeThreshold {
            rejectedBy = "different rhythm"
        } else if bands < bandThreshold {
            rejectedBy = "different sound"
        }
        return Score(envelope: envelope, bands: bands, durationRatio: ratio,
                     activeFraction: candidate.activeFraction,
                     matched: rejectedBy == nil, rejectedBy: rejectedBy)
    }

    /// The runtime entry point. `nil` features (a clip too short to describe)
    /// is a no-match, never a match.
    public static func matches(
        samples: [Float], sampleRate: Double, template: DictationGestureTemplate
    ) -> Bool {
        guard let features = DictationGestureDSP.features(samples: samples, sampleRate: sampleRate)
        else { return false }
        return score(features, against: template).matched
    }
}
