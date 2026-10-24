import Foundation

/// Pure-logic self-check for the delete-gesture matcher. No microphone, no
/// recorded template, no disk — every signal below is synthesized in-process.
///
/// The properties pinned here are the ones a regression would turn into
/// deleted user text:
///   1. the same gesture, performed again (louder, slightly faster, with
///      noise on top) still matches;
///   2. a DIFFERENT gesture of similar length does not;
///   3. speech-shaped audio does not match, no matter what the template is —
///      asserted through the SPARSITY gate, which is structural rather than
///      threshold-tuned, so this direction cannot be broken by fiddling with
///      the correlation constants;
///   4. the template builder REFUSES an inconsistent recording session
///      instead of saving a template that will misfire.
public func runDictationGestureSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }

    let rate = 16_000.0

    /// Deterministic pseudo-noise — a fixed LCG, not `Double.random`, so a
    /// failure here is reproducible rather than a coin flip in CI.
    struct Noise {
        var state: UInt64 = 0x2545F491_4F6CDD1D
        mutating func next() -> Float {
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Float(Int32(truncatingIfNeeded: state >> 33)) / Float(Int32.max)
        }
    }

    /// A burst-shaped clip: `bursts` gives (start seconds, length seconds,
    /// amplitude). Everything else is near-silence. Bandwidth is controlled
    /// by `lowpass` (0 = broadband noise, higher = smoother/darker), which
    /// is what makes two gestures spectrally different.
    func makeClip(
        seconds: Double, bursts: [(Double, Double, Float)], lowpass: Int, seed: UInt64
    ) -> [Float] {
        var noise = Noise(state: seed)
        var out = [Float](repeating: 0, count: Int(seconds * rate))
        var previous: Float = 0
        for i in out.indices {
            var sample = noise.next()
            // A crude moving-average lowpass: more taps = darker spectrum.
            for _ in 0..<lowpass {
                sample = (sample + previous) * 0.5
                previous = sample
            }
            // Room floor, so nothing is digitally silent (which would make
            // the sparsity measure degenerate rather than realistic).
            var amplitude: Float = 0.0005
            for (start, length, level) in bursts {
                let t = Double(i) / rate
                if t >= start && t < start + length {
                    // Raised-cosine envelope per burst — a rectangular gate
                    // would put a click at each edge and dominate the
                    // spectrum with the edge, not the burst.
                    let phase = (t - start) / length
                    amplitude = level * Float(0.5 - 0.5 * cos(2 * Double.pi * phase))
                }
            }
            out[i] = sample * amplitude
        }
        return out
    }

    /// Two short broadband bursts ~180 ms apart — the "cough twice" shape.
    func doubleBurst(seed: UInt64, amplitude: Float = 0.6, stretch: Double = 1.0) -> [Float] {
        makeClip(
            seconds: 1.0 * stretch,
            bursts: [(0.12 * stretch, 0.10 * stretch, amplitude),
                     (0.42 * stretch, 0.10 * stretch, amplitude * 0.9)],
            lowpass: 2, seed: seed)
    }

    /// One burst, same total length — a genuinely different gesture.
    func singleBurst(seed: UInt64) -> [Float] {
        makeClip(seconds: 1.0, bursts: [(0.25, 0.18, 0.6)], lowpass: 2, seed: seed)
    }

    /// Speech-shaped: continuously active for most of its length.
    func speechLike(seed: UInt64) -> [Float] {
        var bursts: [(Double, Double, Float)] = []
        var t = 0.05
        var i = 0
        while t < 1.75 {
            bursts.append((t, 0.16, 0.4 + Float(i % 3) * 0.08))
            t += 0.18
            i += 1
        }
        return makeClip(seconds: 1.9, bursts: bursts, lowpass: 4, seed: seed)
    }

    func featuresOf(_ samples: [Float], _ label: String) -> DictationGestureDSP.Features? {
        guard let f = DictationGestureDSP.features(samples: samples, sampleRate: rate) else {
            failures.append("features() returned nil for \(label)")
            return nil
        }
        return f
    }

    // MARK: Vector math

    check(DictationGestureDSP.resample([0, 1, 2, 3], to: 4) == [0, 1, 2, 3],
          "resample to the same length must be identity")
    check(DictationGestureDSP.resample([0, 10], to: 3) == [0, 5, 10],
          "resample must interpolate linearly")
    check(DictationGestureDSP.normalizeZ([5, 5, 5]).allSatisfy { $0 == 0 },
          "a constant vector must z-normalize to zeros, not NaN")
    check(abs(DictationGestureDSP.correlation([1, 2, 3, 4], [2, 4, 6, 8]) - 1) < 1e-9,
          "a scaled copy must correlate at 1")
    check(abs(DictationGestureDSP.correlation([1, 2, 3, 4], [4, 3, 2, 1]) + 1) < 1e-9,
          "a reversed ramp must correlate at -1")
    check(DictationGestureDSP.correlation([5, 5, 5], [1, 2, 3]) == 0,
          "a flat vector must correlate at 0 with everything — never 1")

    // MARK: Template building

    let takes = [doubleBurst(seed: 11), doubleBurst(seed: 22, amplitude: 0.45),
                 doubleBurst(seed: 33, amplitude: 0.75)]
        .compactMap { featuresOf($0, "template take") }

    guard takes.count == 3 else {
        print("dictation-gesture-check: FAIL: could not build template takes")
        return false
    }

    guard case .success(let template) = DictationGestureTemplate.build(from: takes) else {
        let result = DictationGestureTemplate.build(from: takes)
        print("dictation-gesture-check: FAIL: three takes of the same synthetic gesture were "
            + "rejected as inconsistent (\(result))")
        return false
    }
    check(template.cohesion >= DictationGestureMatcher.minimumCohesion,
          "template cohesion \(template.cohesion) below the minimum it was accepted at")

    // Refusal directions.
    if case .failure(.notEnoughTakes) = DictationGestureTemplate.build(from: [takes[0]]) {} else {
        failures.append("a single take must not build a template")
    }
    let mixed = [takes[0], featuresOf(singleBurst(seed: 44), "single")].compactMap { $0 }
    if case .failure(.inconsistent) = DictationGestureTemplate.build(from: mixed) {} else {
        failures.append("two takes of DIFFERENT gestures must be refused as inconsistent — "
            + "saving that template would produce a matcher that fires on both")
    }

    // MARK: Matching

    func scoreOf(_ samples: [Float], _ label: String) -> DictationGestureMatcher.Score? {
        guard let f = featuresOf(samples, label) else { return nil }
        return DictationGestureMatcher.score(f, against: template)
    }

    // 1. The same gesture again — a fresh noise seed, a different loudness,
    //    and slightly slower.
    if let same = scoreOf(doubleBurst(seed: 99, amplitude: 0.5, stretch: 1.15), "repeat") {
        check(same.matched,
              "the same gesture performed again must match "
                  + "(env=\(same.envelope), bands=\(same.bands), rejectedBy=\(same.rejectedBy ?? "-"))")
    }

    // 2. A different gesture of the same length.
    if let different = scoreOf(singleBurst(seed: 55), "single burst") {
        check(!different.matched,
              "a single burst must NOT match a double-burst template "
                  + "(env=\(different.envelope), bands=\(different.bands))")
    }

    // 3. Speech. The gate that must hold structurally.
    if let speech = scoreOf(speechLike(seed: 66), "speech-like") {
        check(!speech.matched,
              "speech-shaped audio must NEVER match (env=\(speech.envelope), "
                  + "active=\(speech.activeFraction), rejectedBy=\(speech.rejectedBy ?? "-"))")
    }
    // …and specifically via sparsity/length, not by luck of correlation: a
    // speech clip built to have the template's OWN envelope shape must still
    // be rejected.
    var adversarial = takes[0]
    adversarial.activeFraction = 0.95
    adversarial.durationSeconds = template.meanDurationSeconds
    let adversarialScore = DictationGestureMatcher.score(adversarial, against: template)
    check(!adversarialScore.matched,
          "a clip with a PERFECT envelope match but speech-level density must still be refused "
              + "— the sparsity gate is what makes normal dictation structurally unmatchable")
    check(adversarialScore.rejectedBy == "sounds like speech",
          "the density refusal must be attributed to the speech gate, got "
              + "\(adversarialScore.rejectedBy ?? "nil")")

    // 4. Duration gates.
    var tooLong = takes[0]
    tooLong.durationSeconds = DictationGestureMatcher.maxClipSeconds + 0.1
    check(!DictationGestureMatcher.score(tooLong, against: template).matched,
          "a clip over the length ceiling must be refused however well it correlates")
    var wrongLength = takes[0]
    wrongLength.durationSeconds = template.meanDurationSeconds * 1.9
    check(!DictationGestureMatcher.score(wrongLength, against: template).matched,
          "a clip well outside the duration tolerance must be refused")

    // 5. Silence and near-nothing.
    check(!DictationGestureMatcher.matches(
            samples: [Float](repeating: 0, count: 16_000), sampleRate: rate, template: template),
          "digital silence must never match")
    check(!DictationGestureMatcher.matches(samples: [0, 0, 0], sampleRate: rate, template: template),
          "a clip too short to describe must be a no-match, not a match")

    guard failures.isEmpty else {
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-gesture-check: FAIL: \(failures[0])\(suffix)")
        return false
    }
    print("dictation-gesture-check: PASS "
        + "(template cohesion \(String(format: "%.3f", template.cohesion)); "
        + "same-shape matches, different-shape and speech-shaped rejected)")
    return true
}
