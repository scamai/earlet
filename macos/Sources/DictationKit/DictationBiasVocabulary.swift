import Foundation

/// The user's own terms, handed to the recognizer as `bias_phrases` on the
/// dictation path.
///
/// ## Why this exists, and how it differs from the path this repo closed
///
/// `DictationDictionaryStore` records — correctly — that seeding mined
/// vocabulary into Whisper's `initial_prompt` made WER **14.7% worse** on the
/// jargon it targeted, and that a loose post-ASR phonetic nearest-neighbour
/// pass was worse still (development iteration 7). Both of those
/// stay dead. This is neither of them.
///
/// `bias_phrases` reaches a different mechanism: `beam_decode.BiasTrie`, a
/// token-prefix trie that adds a bounded score to candidate tokens **inside
/// beam search, before pruning**, with rollback when a partial match dies. The
/// research module measured it on 6,120 decodes: private-term recovery rose
/// 9.5% → 60.3% at delta=1, with zero ordinary-word false triggers across
/// 36,006 words of non-target speech
/// (internal research findings 02, 17 and 18).
///
/// The distinction is not cosmetic. `whisper_mac.py` (`_vocabulary_prompt`,
/// called from the mlx branch of `WhisperMac.transcribe`) degrades
/// `bias_phrases` into an `initial_prompt` string, because `mlx_whisper`
/// exposes no beam state for the trie to hook — the worker's own module
/// docstring says the same: "MLX and faster-whisper remain unsupported
/// because neither exposes the required beam state."
///
/// **That degradation is nonetheless what ships on mlx, and deliberately so.**
/// This paragraph used to end "sending phrases is therefore only safe on an
/// engine that runs the trie", which `DictationBiasPolicy` below has
/// contradicted since 2026-08-18 — it admits `mlx`. The
/// contradiction was the stale half: Iteration 7's +14.7% harm was measured on
/// a 70-term, 186-token pile, and the harm is size-dependent, not categorical.
/// At the shipped size (32 terms plus a prose seed, 143 tokens) the same
/// measurement on 50 retained utterances found 0/50 empty outputs, 0/50 clips
/// shortened by more than 20%, and a median length ratio of 1.000. Read the
/// policy's own comment before quoting the harm number — this header is a
/// signpost, the policy is the decision.
///
/// ## What this deliberately does not do
///
/// It does not learn. Nothing here reads corrections, promotes entries, or
/// touches `DictationDictionaryStore`. The vocabulary is a file the user
/// controls, so the first end-to-end test of the trie on this path carries no
/// learning loop to confound it.
public struct DictationBiasVocabulary: Sendable, Equatable {
    /// Phrases in stable order. Order matters: `BiasTrie` assigns phrase ids by
    /// position, and the research module's provenance hashes the phrase list.
    public let phrases: [String]

    public init(phrases: [String]) {
        var seen = Set<String>()
        self.phrases = phrases
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty && seen.insert($0).inserted }
    }

    public static let empty = DictationBiasVocabulary(phrases: [])

    public var isEmpty: Bool { phrases.isEmpty }

    /// Hard ceiling on how many phrases may be active at once.
    ///
    /// Not a performance number — it is where false triggers cross the
    /// <1-per-10k bar.
    ///
    /// The previous value of 250 came from Finding 18, measured on 36,006 words
    /// with a distractor list that had not been filtered against the corpus. The
    /// padding names were colliding with words the corpus genuinely speaks —
    /// `Lorenzo` heard as a padded `lorenz` produced 12 of 19 events on its own —
    /// so the ceiling was set against noise. Re-measured on **1,200,007 words**
    /// with the padding filtered (internal research finding 23), at δ=2.5:
    ///
    ///     entries   per 10k   95% interval      verdict
    ///        21      0.0170   [0.002, 0.060]    pass   (the real vocabulary)
    ///       250      0.2420   [0.162, 0.347]    pass
    ///       500      0.2330   [0.155, 0.337]    pass
    ///      1000      0.6170   [0.484, 0.774]    pass
    ///      2000      1.1830   [0.997, 1.395]    inconclusive
    ///
    /// 1,000 is the largest size measured to pass with the interval clear of the
    /// bar. It is set here rather than 1,250 or 1,500 because those two are still
    /// being measured and an unmeasured ceiling is how the last one got set.
    ///
    /// **This is the ceiling for the TRIE. It is not the capacity of the
    /// `initial_prompt` path, which is where macOS now decodes.** The trie can
    /// genuinely carry 1,000 phrases; a prompt cannot. Whisper keeps only the
    /// final 223 prompt tokens, which measures at **33–74 terms** depending on
    /// term length (`test_whisper_mac.py` pins it against a 1,000-term input).
    /// Everything above that is dropped by `whisper_mac.py::_vocabulary_prompt`.
    ///
    /// The value is deliberately NOT lowered to 74: Windows still decodes
    /// through `beam_decode.BiasTrie` on the NPU, where 1,000 is correct and
    /// measured, and a shared constant must not be re-tuned for whichever
    /// platform was looked at most recently. What was actually wrong is that
    /// the prompt path truncated in SILENCE, so a vocabulary could appear
    /// configured and simply never be sent; it now reports the drop count.
    ///
    /// Two things worth knowing before anyone raises this (a research run, 306
    /// clips): below the budget every extra term **dilutes** — 17 terms scored
    /// 0.8497 against 50 at 0.7941 and 250 at 0.7614 — while above it extra
    /// terms are merely truncated and cost nothing. **Headroom is not free
    /// capacity.** Truncate where relevance runs out, not where the budget
    /// does.
    ///
    /// The rate is flat from 250 to 500 and climbs after, so this is not linear
    /// in entry count and must not be extrapolated. δ matters too — 1,000 entries
    /// at δ=2.0 sits well below the same size at δ=2.5 — so a future change to
    /// `trie_decode.DEFAULT_DELTA` invalidates this number and requires a
    /// re-measurement, not an adjustment.
    public static let maxPhrases = 1000

    /// Truncation is reported, never silent: a caller that ignores the return
    /// value is making an explicit choice.
    public func bounded(to limit: Int = maxPhrases) -> (vocabulary: DictationBiasVocabulary, dropped: Int) {
        guard phrases.count > limit else { return (self, 0) }
        return (DictationBiasVocabulary(phrases: Array(phrases.prefix(limit))),
                phrases.count - limit)
    }
}

/// Whether the resolved engine has a supported vocabulary path.
/// The OpenAI backend uses trie biasing; MLX uses an initial prompt. The
/// worker limits prompt size and applies its script seed. This capability
/// check does not establish recognition accuracy for a particular vocabulary.
public enum DictationBiasPolicy: Sendable {
    /// Engine identifiers as `whisper_mac.py` reports them in `stats["engine"]`.
    ///
    /// Named for what it now means — "can use a vocabulary at all" — rather
    /// than for the trie, which is only one of the two mechanisms. `openai`
    /// takes the trie's beam-search path; `mlx` takes `initial_prompt`.
    public static let vocabularyCapableEngines: Set<String> = ["openai", "whisper", "mlx"]

    /// Kept as the old name for callers outside this module; the trie itself
    /// still runs only where beam search exists.
    public static let trieCapableEngines: Set<String> = ["openai", "whisper"]

    public static func shouldSendPhrases(engine: String?, flagEnabled: Bool) -> Bool {
        guard flagEnabled, let engine else { return false }
        return vocabularyCapableEngines.contains(engine.lowercased())
    }
}

/// Loads the vocabulary from a file the user owns.
public enum DictationBiasVocabularyLoader {
    /// `~/Library/Application Support/Dictation/dictation/bias_vocabulary.json`,
    /// beside the dictionary store and validated by the same predicate.
    public static func defaultURL(fileManager: FileManager = .default) -> URL? {
        guard let base = fileManager.urls(for: .applicationSupportDirectory,
                                          in: .userDomainMask).first else { return nil }
        return base
            .appendingPathComponent("Dictation", isDirectory: true)
            .appendingPathComponent("dictation", isDirectory: true)
            .appendingPathComponent("bias_vocabulary.json", isDirectory: false)
    }

    /// Accepts either a bare array of strings or `{"phrases": [...]}`.
    ///
    /// A malformed or missing file yields an empty vocabulary rather than an
    /// error: the failure mode of this feature must be "dictation behaves
    /// exactly as before", never "dictation stops working".
    public static func load(from url: URL,
                            fileManager: FileManager = .default) -> DictationBiasVocabulary {
        guard fileManager.fileExists(atPath: url.path),
              let data = try? Data(contentsOf: url) else { return .empty }
        if let phrases = try? JSONDecoder().decode([String].self, from: data) {
            return DictationBiasVocabulary(phrases: phrases)
        }
        struct Wrapper: Decodable { let phrases: [String] }
        if let wrapper = try? JSONDecoder().decode(Wrapper.self, from: data) {
            return DictationBiasVocabulary(phrases: wrapper.phrases)
        }
        return .empty
    }
}
