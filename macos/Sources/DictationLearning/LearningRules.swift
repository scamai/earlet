import Foundation

// The state machine of LEARNING.md §4, ported line for line from
// `Learning` in `tests/learning_reference.py`. Every operation but `clear`
// ends in `prune`, which applies the caps and the pending TTL and puts every
// list in canonical order, so two implementations fed the same steps hold the
// same file. `tests/fixtures/learning-golden.json` is the acceptance test.

/// Why teaching was refused, or `ok`. Raw values are the reference's strings.
public enum TeachResult: String, Sendable {
    case ok
    /// Blank, a written form with no word in it ("!!!"), or a misheard form
    /// shorter than 2 characters.
    case empty
    /// More than 60 characters, 6 non-CJK tokens or 12 CJK characters.
    case tooLong = "too_long"
    /// The written form contains the misheard form.
    case selfContaining = "self_containing"
    /// 500 taught words, or 200 aliases none of which is learned.
    case full
}

/// What one observation changed. The words are for the caller's own use (a
/// UI toast, a test); they must never reach a log.
public struct LearningObservation: Equatable, Sendable {
    public var candidates: Int
    public var rejections: Int
    /// Words added for the first time.
    public var learnedWords: [String]
    /// Heard keys promoted to aliases.
    public var learnedAliases: [String]

    public init(candidates: Int, rejections: Int, learnedWords: [String], learnedAliases: [String]) {
        self.candidates = candidates
        self.rejections = rejections
        self.learnedWords = learnedWords
        self.learnedAliases = learnedAliases
    }
}

/// What the worker reads (LEARNING.md §6), in canonical order. The golden
/// cases compare this.
public struct LearningProjection: Equatable, Sendable {
    public struct Pair: Equatable, Sendable {
        public var heard: String
        public var meant: String

        public init(heard: String, meant: String) {
            self.heard = heard
            self.meant = meant
        }
    }

    public struct Pending: Equatable, Sendable {
        public var heard: String
        public var meant: String
        public var count: Int

        public init(heard: String, meant: String, count: Int) {
            self.heard = heard
            self.meant = meant
            self.count = count
        }
    }

    public var words: [String]
    /// One entry per heard key.
    public var aliases: [Pair]
    public var pending: [Pending]
    public var removedWords: [String]
    public var removedAliases: [Pair]

    public init(words: [String], aliases: [Pair], pending: [Pending], removedWords: [String], removedAliases: [Pair]) {
        self.words = words
        self.aliases = aliases
        self.pending = pending
        self.removedWords = removedWords
        self.removedAliases = removedAliases
    }
}

extension LearningState {

    // ─── Lookups ───

    private func wordIndex(_ key: String) -> Int? {
        words.firstIndex { LearningText.same(LearningText.lower($0.text), key) }
    }

    private func aliasIndex(_ heard: String) -> Int? {
        aliases.firstIndex { LearningText.same($0.heard, heard) }
    }

    private func pendingIndex(_ heard: String, _ meant: String) -> Int? {
        pending.firstIndex { LearningText.same($0.heard, heard) && LearningText.same($0.meant, meant) }
    }

    private func wordRemoved(_ key: String) -> Bool {
        removed.words.contains { LearningText.same($0.key, key) }
    }

    private func aliasRemoved(_ heard: String, _ meant: String) -> Bool {
        let meantKey = LearningText.lower(meant)
        return removed.aliases.contains { LearningText.same($0.heard, heard) && LearningText.same($0.meant, meantKey) }
    }

    private mutating func unremoveWord(_ key: String) {
        removed.words.removeAll { LearningText.same($0.key, key) }
    }

    private mutating func unremoveAlias(_ heard: String, _ meant: String) {
        let meantKey = LearningText.lower(meant)
        removed.aliases.removeAll { LearningText.same($0.heard, heard) && LearningText.same($0.meant, meantKey) }
    }

    private mutating func tombstoneWord(_ key: String, at: Int64) {
        if !wordRemoved(key) {
            removed.words.append(RemovedWord(key: key, at: at))
        }
    }

    private mutating func tombstoneAlias(_ heard: String, _ meant: String, at: Int64) {
        if !aliasRemoved(heard, meant) {
            removed.aliases.append(RemovedAlias(heard: heard, meant: LearningText.lower(meant), at: at))
        }
    }

    // ─── Operations ───

    /// Learns from one dictation: `dictated` is what was pasted, `edited` the
    /// same span when the observation ended. `dictationID` is random per
    /// insertion; the same id counts once towards an alias.
    @discardableResult
    public mutating func observe(dictationID: String, dictated: String, edited: String, at: Int64,
                                 lexicon: any LearningLexicon, scene: LearningScene? = nil) -> LearningObservation {
        let (candidates, rejections) = LearningPolicy.extract(dictated: dictated, edited: edited, lexicon: lexicon)
        var result = learn(candidates, dictationID: dictationID, at: at, lexicon: lexicon, scene: scene)
        result.rejections = rejections.count
        return result
    }

    /// `observe` after extraction, which is pure, so running it first is the
    /// reference's order. `DictationLearning` replays this onto a file it could
    /// not read when the correction was made: it keeps the corrected spans,
    /// never the dictated sentence.
    @discardableResult
    mutating func learn(_ candidates: [LearningCandidate], dictationID: String, at: Int64,
                        lexicon: any LearningLexicon, scene: LearningScene? = nil) -> LearningObservation {
        prune(at: at)
        return LearningEvidence.revise(&self, id: dictationID, candidates: candidates, at: at, lexicon: lexicon, scene: scene)
    }

    /// Teaches a written form and, optionally, what Earlet writes for it now
    /// (the misheard form; LEARNING.md §4.3).
    public mutating func teach(written rawWritten: String, soundsLike rawSoundsLike: String?, at: Int64) -> TeachResult {
        let written = LearningText.strip(rawWritten)
        let soundsLike = LearningText.strip(rawSoundsLike ?? "")
        if written.isEmpty { return .empty }
        let tokens = LearningText.tokenize(written)
        let latin = tokens.reduce(0) { $0 + ($1.cjk ? 0 : 1) }
        // "!!!" has no word in it: nothing to teach, not something too long.
        if tokens.isEmpty { return .empty }
        if written.unicodeScalars.count > LearningLimits.maxTaughtChars
            || latin > LearningLimits.maxTaughtLatinTokens
            || tokens.count - latin > LearningLimits.maxTaughtCJKChars {
            return .tooLong
        }
        prune(at: at)
        let heard: String? = soundsLike.isEmpty ? nil : LearningText.heardKey(soundsLike)
        if let heard {
            let soundsLength = soundsLike.unicodeScalars.count
            if heard.unicodeScalars.count < 2 || soundsLength > LearningLimits.maxTaughtChars {
                return soundsLength > LearningLimits.maxTaughtChars ? .tooLong : .empty
            }
            if LearningPolicy.containsTokens(written, soundsLike) {
                return .selfContaining
            }
        }
        let key = LearningText.lower(written)
        let wordAt = wordIndex(key)
        let taught = words.reduce(0) { $0 + ($1.origin == .taught ? 1 : 0) }
        let alreadyTaught = wordAt.map { words[$0].origin == .taught } ?? false
        if !alreadyTaught, taught >= LearningLimits.maxTaughtWords {
            return .full
        }
        if let heard, aliasIndex(heard) == nil, aliases.count >= LearningLimits.maxAliases,
           !aliases.contains(where: { $0.origin == .correction }) {
            return .full
        }
        unremoveWord(key)
        if let i = wordAt {
            words[i].text = written
            words[i].origin = .taught
            words[i].lastSeen = at
        } else {
            words.append(LearnedWord(text: written, origin: .taught, count: 0, firstSeen: at, lastSeen: at))
        }
        if let heard {
            unremoveAlias(heard, written)
            if let a = aliasIndex(heard) { aliases.remove(at: a) }
            aliases.append(LearnedAlias(heard: heard, meant: written, origin: .taught, count: 0,
                                        firstSeen: at, lastSeen: at))
            pending.removeAll { LearningText.same($0.heard, heard) }
        }
        prune(at: at)
        return .ok
    }

    /// Removes a word, every alias that writes it and every pending pair that
    /// would, and tombstones all of them.
    public mutating func removeWord(_ text: String, at: Int64) {
        let key = LearningText.lower(LearningText.strip(text))
        words.removeAll { LearningText.same(LearningText.lower($0.text), key) }
        tombstoneWord(key, at: at)
        let doomed = aliases.filter { LearningText.same(LearningText.lower($0.meant), key) }
        aliases.removeAll { LearningText.same(LearningText.lower($0.meant), key) }
        for alias in doomed {
            tombstoneAlias(alias.heard, alias.meant, at: at)
        }
        pending.removeAll { LearningText.same(LearningText.lower($0.meant), key) }
        prune(at: at)
    }

    /// Removes one alias and tombstones it; its word stays.
    public mutating func removeAlias(heard rawHeard: String, meant: String, at: Int64) {
        let heard = LearningText.heardKey(rawHeard)
        let meantKey = LearningText.lower(meant)
        func matches(_ entryHeard: String, _ entryMeant: String) -> Bool {
            LearningText.same(entryHeard, heard) && LearningText.same(LearningText.lower(entryMeant), meantKey)
        }
        aliases.removeAll { matches($0.heard, $0.meant) }
        tombstoneAlias(heard, meant, at: at)
        pending.removeAll { matches($0.heard, $0.meant) }
        prune(at: at)
    }

    /// Forgets everything, tombstones included.
    public mutating func clear() {
        self = .empty
    }

    // ─── Housekeeping ───

    /// Pending TTL, then every cap, then canonical order.
    mutating func prune(at: Int64) {
        let ttl = LearningLimits.pendingTTLMilliseconds
        let cutoff = at > Int64.min + ttl ? at - ttl : Int64.min
        pending = pending
            .filter { $0.lastSeen >= cutoff }
            .stableSorted { lhs, rhs in
                if lhs.lastSeen != rhs.lastSeen { return lhs.lastSeen > rhs.lastSeen }
                if !LearningText.same(lhs.heard, rhs.heard) { return LearningText.precedes(lhs.heard, rhs.heard) }
                return LearningText.precedes(lhs.meant, rhs.meant)
            }
        if pending.count > LearningLimits.maxPending {
            pending.removeLast(pending.count - LearningLimits.maxPending)
        }

        // Least recently seen learned words go first; taught words never.
        let learned = words.indices
            .filter { words[$0].origin == .correction }
            .stableSorted { Self.recentFirst(words[$0], words[$1]) }
        if learned.count > LearningLimits.maxLearnedWords {
            let evicted = Set(learned[LearningLimits.maxLearnedWords...])
            words = words.indices.filter { !evicted.contains($0) }.map { words[$0] }
        }

        // Learned aliases go first, least recently seen; taught ones never.
        while aliases.count > LearningLimits.maxAliases {
            var victim: Int?
            for i in aliases.indices where aliases[i].origin == .correction {
                if let v = victim {
                    let lhs = aliases[i]
                    let rhs = aliases[v]
                    if lhs.lastSeen < rhs.lastSeen
                        || (lhs.lastSeen == rhs.lastSeen && LearningText.precedes(lhs.heard, rhs.heard)) {
                        victim = i
                    }
                } else {
                    victim = i
                }
            }
            guard let victim else { break }
            aliases.remove(at: victim)
        }

        removed.words = removed.words.stableSorted { $0.at > $1.at }
        if removed.words.count > LearningLimits.maxTombstones {
            removed.words.removeLast(removed.words.count - LearningLimits.maxTombstones)
        }
        removed.aliases = removed.aliases.stableSorted { $0.at > $1.at }
        if removed.aliases.count > LearningLimits.maxTombstones {
            removed.aliases.removeLast(removed.aliases.count - LearningLimits.maxTombstones)
        }
        canonicalise()
    }

    private static func recentFirst(_ lhs: LearnedWord, _ rhs: LearnedWord) -> Bool {
        if lhs.lastSeen != rhs.lastSeen { return lhs.lastSeen > rhs.lastSeen }
        return LearningText.precedes(LearningText.lower(lhs.text), LearningText.lower(rhs.text))
    }

    mutating func canonicalise() {
        words = words.stableSorted(by: Self.recentFirst)
        aliases = aliases.stableSorted { LearningText.precedes($0.heard, $1.heard) }
        pending = pending.stableSorted { lhs, rhs in
            if !LearningText.same(lhs.heard, rhs.heard) { return LearningText.precedes(lhs.heard, rhs.heard) }
            return LearningText.precedes(lhs.meant, rhs.meant)
        }
        removed.words = removed.words.stableSorted { LearningText.precedes($0.key, $1.key) }
        removed.aliases = removed.aliases.stableSorted { lhs, rhs in
            if !LearningText.same(lhs.heard, rhs.heard) { return LearningText.precedes(lhs.heard, rhs.heard) }
            return LearningText.precedes(lhs.meant, rhs.meant)
        }
    }

    // ─── What the worker reads (LEARNING.md §6) ───

    public func projection() -> LearningProjection {
        var state = self
        state.canonicalise()
        // A later alias for the same heard key replaces the earlier one in
        // place, as the reference's dict does.
        var aliases: [LearningProjection.Pair] = []
        for alias in state.aliases {
            if let i = aliases.firstIndex(where: { LearningText.same($0.heard, alias.heard) }) {
                aliases[i].meant = alias.meant
            } else {
                aliases.append(LearningProjection.Pair(heard: alias.heard, meant: alias.meant))
            }
        }
        return LearningProjection(
            words: state.words.map(\.text),
            aliases: aliases,
            pending: state.pending.map { LearningProjection.Pending(heard: $0.heard, meant: $0.meant, count: $0.count) },
            removedWords: state.removed.words.map(\.key),
            removedAliases: state.removed.aliases.map { LearningProjection.Pair(heard: $0.heard, meant: $0.meant) })
    }
}
