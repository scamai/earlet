import Foundation

/// Only coarse categories may enter learning storage or recognition requests.
public enum LearningScene: String, Sendable {
    case terminal, wechat, other
    public static func forApplication(_ bundleID: String?) -> LearningScene? {
        guard let id = bundleID?.lowercased(), !id.isEmpty else { return nil }
        if ["com.tencent.xinwechat", "com.tencent.wechat"].contains(id) { return .wechat }
        if ["com.apple.terminal", "com.googlecode.iterm2", "dev.warp.warp-stable",
            "com.mitchellh.ghostty", "net.kovidgoyal.kitty", "org.alacritty",
            "com.github.wez.wezterm", "co.zeit.hyper", "ai.scam.witty", "dev.shenxingy.witty"].contains(id) {
            return .terminal
        }
        return .other
    }
}

// ─── Reversible evidence (tests/adaptive_reference.py, LEARNING.md §4.6) ───

/// A bounded journal of corrected spans, not drafts. A stable insertion ID is
/// one vote, and a later revision replaces that vote, including its opposition.
enum LearningEvidence {
    static let maxObservations = 512
    static let maxCorrections = 32

    struct Pair: Hashable {
        var heard: ExactText
        var meant: ExactText
        init(_ heard: String, _ meant: String) {
            self.heard = ExactText(heard)
            self.meant = ExactText(LearningText.lower(meant))
        }
    }

    struct Correction: Equatable {
        var heard: String
        var meant: String
        var word: Bool
        var alias: Bool
        var secret: Bool
        var extra: [String: LearningJSON] = [:]
        var pair: Pair { Pair(heard, meant) }

        init(_ candidate: LearningCandidate, lexicon: any LearningLexicon) {
            heard = LearningText.heardKey(candidate.heard)
            meant = candidate.meant
            word = LearningPolicy.learnsWord(candidate, lexicon: lexicon)
            alias = LearningPolicy.learnsAlias(candidate, lexicon: lexicon)
            secret = LearningPolicy.isSecretShaped(candidate.meant)
        }

        init(heard: String, meant: String, word: Bool, alias: Bool, secret: Bool) {
            self.heard = heard; self.meant = meant; self.word = word; self.alias = alias; self.secret = secret
        }

        init?(_ value: LearningJSON) {
            guard case .object(let object) = value,
                  case .string(let heard) = object["heard"], case .string(let meant) = object["meant"],
                  case .bool(let word) = object["word"], case .bool(let alias) = object["alias"],
                  case .bool(let secret) = object["secret"],
                  heard.unicodeScalars.count <= 256, !meant.isEmpty, meant.unicodeScalars.count <= 256 else { return nil }
            self.init(heard: heard, meant: meant, word: word, alias: alias, secret: secret)
            extra = object
        }

        var json: LearningJSON {
            var object = extra
            object.merge(["heard": .string(heard), "meant": .string(meant), "word": .bool(word),
                          "alias": .bool(alias), "secret": .bool(secret)]) { _, new in new }
            return .object(object)
        }

        func sameContribution(_ other: Self) -> Bool {
            LearningText.same(heard, other.heard) && LearningText.same(meant, other.meant)
                && word == other.word && alias == other.alias && secret == other.secret
        }
    }

    struct Record {
        var id: String
        var at: Int64
        var firstSeen: Int64?
        var weight: Int
        var corrections: [Correction]
        var extra: [String: LearningJSON] = [:]

        init(id: String, at: Int64, firstSeen: Int64? = nil, weight: Int = 1, corrections: [Correction]) {
            self.id = id; self.at = at; self.firstSeen = firstSeen; self.weight = weight; self.corrections = corrections
        }

        init?(_ value: LearningJSON) {
            guard case .object(let object) = value,
                  case .string(let id) = object["id"], !id.isEmpty, id.unicodeScalars.count <= 320,
                  case .integer(let at) = object["at"], case .integer(let weight) = object["weight"],
                  (1...4).contains(weight), case .array(let values) = object["corrections"],
                  values.count <= maxCorrections else { return nil }
            let corrections = values.compactMap(Correction.init)
            guard corrections.count == values.count else { return nil }
            self.init(id: id, at: at, weight: Int(weight), corrections: corrections)
            if let value = object["first_seen"], value != .null {
                guard case .integer(let firstSeen) = value else { return nil }
                self.firstSeen = firstSeen
            }
            extra = object
        }

        var json: LearningJSON {
            var object = extra
            object.merge(["id": .string(id), "at": .integer(at), "weight": .integer(Int64(weight)),
                          "corrections": .array(corrections.map(\.json))]) { _, new in new }
            if let firstSeen { object["first_seen"] = .integer(firstSeen) }
            return .object(object)
        }

        static func before(_ a: Self, _ b: Self) -> Bool {
            a.at != b.at ? a.at < b.at : LearningText.precedes(a.id, b.id)
        }
    }

    struct Support {
        var record: Record
        var correction: Correction
    }
    typealias Votes = [ExactText: Support]

    static func seed(_ state: LearningState, lexicon: any LearningLexicon) -> [Record] {
        var records: [Record] = []
        for word in state.words where word.origin == .correction {
            records.append(Record(id: "legacy:word:" + LearningText.lower(word.text), at: word.lastSeen,
                                  firstSeen: word.firstSeen, weight: max(1, min(4, word.count)),
                                  corrections: [Correction(heard: "", meant: word.text, word: true, alias: false,
                                                           secret: false)]))
        }
        for alias in state.aliases where alias.origin == .correction {
            records.append(Record(id: "legacy:alias:" + alias.heard, at: alias.lastSeen, firstSeen: alias.firstSeen,
                                  weight: max(1, min(4, alias.count)),
                                  corrections: [Correction(heard: alias.heard, meant: alias.meant, word: false, alias: true,
                                                           secret: LearningPolicy.isSecretShaped(alias.meant))]))
        }
        // Tolerant schema-1 files may contain duplicate automatic rows. Match
        // the projection's last-row-wins rule before assigning stable seed IDs.
        var seen: Set<ExactText> = []
        records = records.reversed().filter { seen.insert(ExactText($0.id)).inserted }.reversed()
        for pending in state.pending {
            var correction = Correction(LearningCandidate(heard: pending.heard, meant: pending.meant, kind: .spelling),
                                        lexicon: lexicon)
            correction.word = correction.word && !state.words.contains {
                LearningText.same(LearningText.lower($0.text), LearningText.lower(pending.meant))
            }
            for id in pending.dictations {
                if let i = records.firstIndex(where: { LearningText.same($0.id, id) }) {
                    if !records[i].corrections.contains(where: { $0.sameContribution(correction) }) {
                        records[i].corrections.append(correction)
                    }
                } else {
                    records.append(Record(id: id, at: pending.lastSeen, corrections: [correction]))
                }
            }
        }
        return records
    }

    static func revise(_ state: inout LearningState, id: String, candidates: [LearningCandidate], at: Int64,
                       lexicon: any LearningLexicon, scene: LearningScene? = nil) -> LearningObservation {
        let unchanged = LearningObservation(candidates: candidates.count, rejections: 0, learnedWords: [], learnedAliases: [])
        guard !id.isEmpty, id.unicodeScalars.count <= 128, !id.hasPrefix("legacy:"), lexicon.isAvailable else { return unchanged }
        // A correction whose meant text has no letter (a PIN) is never journaled (LEARNING.md §4.6).
        let kept = candidates.filter { LearningText.hasLetter($0.meant) }
        var journal: [String: LearningJSON]
        var records: [Record]
        if let value = state.extra["evidence"] {
            guard case .object(let object) = value, object["version"] == .integer(1),
                  case .array(let values) = object["observations"], values.count <= 4096 else { return unchanged }
            records = values.compactMap(Record.init)
            guard records.count == values.count, Set(records.map { ExactText($0.id) }).count == records.count else { return unchanged }
            journal = object
        } else {
            guard !kept.isEmpty else { return unchanged }
            journal = ["version": .integer(1)]
            records = seed(state, lexicon: lexicon)
        }
        let oldWords = Set(state.words.map { ExactText(LearningText.lower($0.text)) })
        let oldAliases = Set(state.aliases.map { Pair($0.heard, $0.meant) })
        expirePending(&records, active: oldAliases, at: at)
        let corrections = kept.prefix(maxCorrections).filter {
            $0.heard.unicodeScalars.count <= 256 && $0.meant.unicodeScalars.count <= 256
        }.map { Correction($0, lexicon: lexicon) }
        let old = records.first { LearningText.same($0.id, id) }
        let identical = old.map { record in
            record.corrections.count == corrections.count && zip(record.corrections, corrections).allSatisfy {
                $0.sameContribution($1)
            }
        } ?? false
        if !identical {
            records.removeAll { LearningText.same($0.id, id) }
            if !corrections.isEmpty {
                var record = old ?? Record(id: id, at: at, corrections: [])
                record.at = at; record.weight = 1; record.corrections = corrections
                // Once captured, a draft never migrates between scenes on revision.
                if old == nil, let scene { record.extra["scene"] = .string(scene.rawValue) }
                records.append(record)
            }
        }
        records.sort(by: Record.before)
        records = Array(records.suffix(maxObservations))
        journal["observations"] = .array(records.map(\.json))
        state.extra["evidence"] = .object(journal)
        rebuild(&state, records: records, at: at)
        state.prune(at: at)
        return LearningObservation(candidates: candidates.count, rejections: 0,
                                   learnedWords: state.words.filter { !oldWords.contains(ExactText(LearningText.lower($0.text))) }.map(\.text),
                                   learnedAliases: state.aliases.filter { !oldAliases.contains(Pair($0.heard, $0.meant)) }.map(\.heard))
    }

    static func expirePending(_ records: inout [Record], active: Set<Pair>, at: Int64) {
        let ttl = LearningLimits.pendingTTLMilliseconds
        let cutoff = at > Int64.min + ttl ? at - ttl : Int64.min
        var last: [Pair: Int64] = [:]
        for r in records {
            for c in r.corrections where c.alias || (c.word && c.secret) {
                last[c.pair] = max(last[c.pair] ?? r.at, r.at)
            }
        }
        let expired = Set(last.filter { $0.value < cutoff && !active.contains($0.key) }.map(\.key))
        for i in records.indices {
            for j in records[i].corrections.indices where expired.contains(records[i].corrections[j].pair) {
                records[i].corrections[j].alias = false
                if records[i].corrections[j].secret { records[i].corrections[j].word = false }
            }
        }
    }

    static func rebuild(_ state: inout LearningState, records: [Record], at: Int64) {
        let oldWords = Dictionary(state.words.map { (ExactText(LearningText.lower($0.text)), $0) }, uniquingKeysWith: { _, b in b })
        let oldAliases = Dictionary(state.aliases.map { (ExactText($0.heard), $0) }, uniquingKeysWith: { _, b in b })
        let protectedWords = oldWords.filter { $0.value.origin != .correction }
        let protectedAliases = oldAliases.filter { $0.value.origin != .correction }
        let removedWords = Set(state.removed.words.map { ExactText($0.key) })
        let removedAliases = Set(state.removed.aliases.map { Pair($0.heard, $0.meant) })
        var words: [ExactText: Votes] = [:]
        var pairs: [Pair: Votes] = [:]
        var replacements: [ExactText: [ExactText: Record]] = [:]
        for record in records {
            for c in record.corrections {
                let key = c.pair.meant, heard = c.pair.heard, id = ExactText(record.id)
                if !c.heard.isEmpty && heard != key { replacements[heard, default: [:]][id] = record }
                if removedWords.contains(key) { continue }
                let support = Support(record: record, correction: c)
                if c.word && protectedWords[key] == nil && (!c.secret || protectedAliases[heard] == nil) {
                    words[key, default: [:]][id] = support
                }
                let waits = c.word && c.secret && protectedWords[key] == nil
                if (c.alias || waits) && protectedAliases[heard] == nil && protectedWords[heard] == nil
                    && !removedAliases.contains(c.pair) {
                    pairs[c.pair, default: [:]][id] = support
                }
            }
        }
        state.words = Array(protectedWords.values)
        state.aliases = Array(protectedAliases.values)
        state.pending = []
        for (key, entries) in words {
            let (support, opposition) = totals(entries, negative: replacements[key] ?? [:])
            let newest = latest(entries)
            guard support - opposition >= (newest.correction.secret ? 2 : 1) else { continue }
            var row = oldWords[key] ?? LearnedWord(text: newest.correction.meant, origin: .correction, count: 0, firstSeen: 0, lastSeen: 0)
            row.text = newest.correction.meant; row.origin = .correction; row.count = support
            row.firstSeen = firstSeen(entries); row.lastSeen = newest.record.at
            row.extra["adaptive"] = .bool(true); row.extra["opposition"] = .integer(Int64(opposition))
            state.words.append(row)
        }
        let ttl = LearningLimits.pendingTTLMilliseconds
        let cutoff = at > Int64.min + ttl ? at - ttl : Int64.min
        for (pair, entries) in pairs {
            var negative = replacements[pair.meant] ?? [:]
            for (other, rival) in pairs where other.heard == pair.heard && other.meant != pair.meant {
                for (id, support) in rival { negative[id] = support.record }
            }
            let (support, opposition) = totals(entries, negative: negative)
            let newest = latest(entries), c = newest.correction
            if c.alias && support - opposition >= LearningLimits.aliasPromotionCount {
                var row = oldAliases[pair.heard] ?? LearnedAlias(heard: pair.heard.value, meant: c.meant, origin: .correction,
                                                               count: 0, firstSeen: 0, lastSeen: 0)
                row.heard = pair.heard.value; row.meant = c.meant; row.origin = .correction; row.count = support
                row.firstSeen = firstSeen(entries); row.lastSeen = newest.record.at
                row.extra["adaptive"] = .bool(true); row.extra["opposition"] = .integer(Int64(opposition))
                state.aliases.append(row)
            } else if newest.record.at >= cutoff && (!c.secret || support - opposition < 2) {
                let ids = entries.values.map(\.record).sorted(by: Record.before).suffix(LearningLimits.pendingDictationIDs).map(\.id)
                state.pending.append(PendingAlias(heard: pair.heard.value, meant: c.meant, count: support, dictations: ids,
                                                   firstSeen: firstSeen(entries), lastSeen: newest.record.at))
            }
        }
    }

    static func totals(_ entries: Votes, negative: [ExactText: Record]) -> (Int, Int) {
        (entries.values.reduce(0) { $0 + $1.record.weight },
         negative.filter { entries[$0.key] == nil }.values.reduce(0) { $0 + $1.weight })
    }

    static func latest(_ entries: Votes) -> Support {
        entries.values.max { Record.before($0.record, $1.record) }!
    }

    static func firstSeen(_ entries: Votes) -> Int64 {
        entries.values.map { $0.record.firstSeen ?? $0.record.at }.min()!
    }
}

public extension LearnedWord {
    /// The UI and worker use the same thresholds. Manual vocabulary is fixed.
    var learningConfidence: String {
        guard origin == .correction else { return "Taught" }
        guard extra["adaptive"] == .bool(true) else { return "Learned from your fix" }
        let opposition: Int
        if case .integer(let value) = extra["opposition"] { opposition = Int(clamping: value) } else { opposition = 0 }
        let score = count - max(0, min(count, opposition))
        // Say what the count means; "Tentative", "Confirmed" and
        // "Established" were labels nobody was told how to read.
        return score >= 4 ? "Confirmed by 4 or more of your fixes"
            : score >= 2 ? "Confirmed by 2 or 3 of your fixes" : "Learned from one fix, not yet confirmed"
    }
}
