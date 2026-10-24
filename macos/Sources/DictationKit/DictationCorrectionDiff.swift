import Foundation

/// One mined `phrase -> replacement` candidate — explicitly NOT a promoted
/// dictionary entry. Task step 4: "Do NOT promote to a dictionary yet —
/// emit candidates plus the terminal reason. Promotion is gated on the
/// phonetic+repetition rule from Iteration 4, which is not this module's
/// to move."
public struct DictationCorrectionCandidate: Sendable, Equatable {
    public let phrase: String
    public let replacement: String

    public init(phrase: String, replacement: String) {
        self.phrase = phrase
        self.replacement = replacement
    }
}

/// Pairs one `DictationObservation` with whatever candidates were mined
/// from it — `[]` whenever `terminalCondition.isEligibleForDiffMining` is
/// `false` (design §3 condition 7's note: an edit-distance-exceeded
/// observation must not feed correction-mining).
public struct DictationObservationResult: Sendable, Equatable {
    public let observation: DictationObservation
    public let candidates: [DictationCorrectionCandidate]

    public init(observation: DictationObservation, candidates: [DictationCorrectionCandidate]) {
        self.observation = observation
        self.candidates = candidates
    }
}

/// Word-level diff between dictated text and what the field held at
/// observation termination — pure, no AX/IPC. Matches Iteration 1's naive-
/// diff methodology, which Iteration 4's phonetic+repetition filter is
/// calibrated against — this module produces exactly that filter's input
/// shape, deliberately not a fancier diff it wasn't tuned for.
public enum DictationCorrectionDiffExtractor {
    private enum Op: Equatable {
        case equal(String)
        case delete(String)
        case insert(String)
    }

    /// Extracts candidate `phrase -> replacement` pairs. Only
    /// delete-then-insert (or insert-then-delete) runs become candidates —
    /// a pure insertion or pure deletion isn't a "phrase -> replacement"
    /// correction in the sense Iteration 4's phonetic filter operates on,
    /// it's an addition/removal with nothing to phonetically compare.
    public static func candidates(dictated: String, edited: String) -> [DictationCorrectionCandidate] {
        let a = dictated.split(separator: " ").map(String.init)
        let b = edited.split(separator: " ").map(String.init)
        guard a != b else { return [] }
        return collapse(diff(a, b))
    }

    /// Classic O(n*m) LCS-based word diff. Dictations average ~21 words
    /// (measured), so this is cheap; no need for Myers' O(ND) here.
    private static func diff(_ a: [String], _ b: [String]) -> [Op] {
        let n = a.count
        let m = b.count
        guard n > 0 || m > 0 else { return [] }
        var lcs = [[Int]](repeating: [Int](repeating: 0, count: m + 1), count: n + 1)
        if n > 0 && m > 0 {
            for i in stride(from: n - 1, through: 0, by: -1) {
                for j in stride(from: m - 1, through: 0, by: -1) {
                    lcs[i][j] = a[i] == b[j] ? lcs[i + 1][j + 1] + 1 : max(lcs[i + 1][j], lcs[i][j + 1])
                }
            }
        }
        var ops: [Op] = []
        var i = 0
        var j = 0
        while i < n && j < m {
            if a[i] == b[j] {
                ops.append(.equal(a[i])); i += 1; j += 1
            } else if lcs[i + 1][j] >= lcs[i][j + 1] {
                ops.append(.delete(a[i])); i += 1
            } else {
                ops.append(.insert(b[j])); j += 1
            }
        }
        while i < n { ops.append(.delete(a[i])); i += 1 }
        while j < m { ops.append(.insert(b[j])); j += 1 }
        return ops
    }

    /// Collapses adjacent delete/insert runs (in either order) into one
    /// candidate pair; equal runs flush the accumulator, and an orphan
    /// delete-only or insert-only run (nothing on the other side to pair
    /// with) produces nothing.
    private static func collapse(_ ops: [Op]) -> [DictationCorrectionCandidate] {
        var candidates: [DictationCorrectionCandidate] = []
        var deletes: [String] = []
        var inserts: [String] = []
        func flush() {
            if !deletes.isEmpty && !inserts.isEmpty {
                candidates.append(
                    DictationCorrectionCandidate(
                        phrase: deletes.joined(separator: " "),
                        replacement: inserts.joined(separator: " ")))
            }
            deletes.removeAll()
            inserts.removeAll()
        }
        for op in ops {
            switch op {
            case .equal:
                flush()
            case .delete(let w):
                deletes.append(w)
            case .insert(let w):
                inserts.append(w)
            }
        }
        flush()
        return candidates
    }
}
