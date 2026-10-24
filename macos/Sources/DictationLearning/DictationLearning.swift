import Foundation

/// The app's single entry point to automatic vocabulary learning
/// (LEARNING.md): the policy, the lexicon and `learning.json` behind one actor.
///
/// The app model is `@MainActor`; every call here hops off the main thread,
/// which is where the first use reads the 235,976-word list and the file.
/// Nothing is read at construction.
///
/// Every operation changes the in-memory state first and then writes the file
/// when the encoded bytes changed. A failed write leaves the state in memory
/// (it is still what the UI shows) and the next successful write carries it;
/// `snapshot().storage` says `.unsaved` meanwhile.
///
/// A file that could not be read (LEARNING.md §5) is never overwritten.
/// Learning goes on in memory; every later change, snapshot and `flush()`
/// tries the read again, and once it succeeds the changes made in the
/// meantime are replayed onto the file's contents and saved. Meanwhile
/// `snapshot().storage` says `.keepingUnreadableFile`.
///
/// Results carry counts and an opaque undo token, never a dictated sentence.
public actor DictationLearning {

    /// What one observation did, in counts.
    public struct ObserveOutcome: Equatable, Sendable {
        public var candidates: Int
        public var rejections: Int
        /// Words added for the first time.
        public var wordsLearned: Int
        /// Pairs promoted to aliases.
        public var aliasesLearned: Int
        /// Corrections from this dictation still waiting for confirmation.
        public var pendingCorrections: Int
        public var save: SaveOutcome
        /// Only for a newly learned, successfully saved change. Invalidated
        /// by the next mutation; never permits undoing somebody else's edit.
        public var undoToken: UUID?

        public init(candidates: Int, rejections: Int, wordsLearned: Int, aliasesLearned: Int,
                    save: SaveOutcome, undoToken: UUID? = nil, pendingCorrections: Int = 0) {
            self.candidates = candidates
            self.rejections = rejections
            self.wordsLearned = wordsLearned
            self.aliasesLearned = aliasesLearned
            self.pendingCorrections = pendingCorrections
            self.save = save
            self.undoToken = undoToken
        }
    }

    public enum SaveOutcome: Equatable, Sendable {
        /// Nothing in the file would change; nothing was written.
        case unchanged
        case saved
        case failed
        /// The file on disk has not been read (an I/O error, or an undecodable
        /// file that could not be moved aside). It is kept; the change waits
        /// in memory until a read succeeds.
        case refused
    }

    public enum StorageStatus: Equatable, Sendable {
        case ok
        /// The last write failed; memory is ahead of the file.
        case unsaved
        /// The file on disk could not be read (or, undecodable, could not be
        /// moved aside). It is left alone; learning goes on in memory and
        /// every later change tries the read again.
        case keepingUnreadableFile
    }

    /// What the app window lists.
    public struct Snapshot: Equatable, Sendable {
        /// Most recently seen first.
        public var words: [LearnedWord]
        /// By heard key.
        public var aliases: [LearnedAlias]
        public var pendingCount: Int
        /// False when the word list could not be read (fail closed: only taught
        /// entries can be added).
        public var lexiconAvailable: Bool
        public var storage: StorageStatus
        /// How the file was found by the last read.
        public var loadOutcome: LearningStore.LoadOutcome

        public init(words: [LearnedWord], aliases: [LearnedAlias], pendingCount: Int, lexiconAvailable: Bool,
                    storage: StorageStatus, loadOutcome: LearningStore.LoadOutcome) {
            self.words = words
            self.aliases = aliases
            self.pendingCount = pendingCount
            self.lexiconAvailable = lexiconAvailable
            self.storage = storage
            self.loadOutcome = loadOutcome
        }

        /// The list is not all on disk: the Vocabulary section then says "Your
        /// vocabulary isn't saved yet. Dictation keeps using it and will try
        /// again." (LEARNING.md §5).
        public var isUnsaved: Bool { storage != .ok }
    }

    /// One change, as `apply` makes it. Kept while the file is unread, to be
    /// replayed onto its contents once a read succeeds. Holds corrected spans
    /// and what the user typed into the Vocabulary section, never a dictated
    /// sentence.
    enum Change: Sendable {
        case learn(candidates: [LearningCandidate], dictationID: String, at: Int64, scene: LearningScene? = nil)
        case teach(written: String, soundsLike: String?, at: Int64)
        case removeWord(String, at: Int64)
        case removeAlias(heard: String, meant: String, at: Int64)
        case clear
    }

    /// What `apply` returned.
    enum Applied {
        case learned(LearningObservation)
        case taught(TeachResult)
        case done
    }

    private let store: LearningStore
    private let lexicon: any LearningLexicon
    private var state = LearningState.empty
    private var loadOutcome: LearningStore.LoadOutcome?
    private var savedBytes: Data?
    private var lastSaveFailed = false
    /// The changes made since the file was last found unread, oldest first.
    private var unreplayed: [Change] = []
    /// One previous vocabulary state, not a transcript. Actor ownership makes
    /// validation and restoration atomic with respect to later edits.
    private var observationRevisions: [String: Int] = [:]
    private var observationOrder: [String] = []
    private var lastLearningUndo: (token: UUID, before: LearningState)?

    public init(store: LearningStore, lexicon: any LearningLexicon) {
        self.store = store
        self.lexicon = lexicon
    }

    /// Learning over the user's real file and `/usr/share/dict/web2`. Throws
    /// `LearningStore.Failure.refusedUnderTest` inside XCTest.
    public static func live(lexicon: any LearningLexicon = WebsterLexicon.system()) throws -> DictationLearning {
        DictationLearning(store: try LearningStore.live(), lexicon: lexicon)
    }

    // ─── Operations ───

    /// Learns from one observed correction. `dictated` is the text Dictation
    /// pasted and `edited` the same span when the observation ended.
    @discardableResult
    public func observe(dictationID: String, dictated: String, edited: String,
                        at date: Date = Date(), revision: Int = 0, scene: LearningScene? = nil) -> ObserveOutcome {
        guard !Task.isCancelled else {
            return ObserveOutcome(candidates: 0, rejections: 0, wordsLearned: 0, aliasesLearned: 0, save: .unchanged)
        }
        // Actor scheduling may deliver an older queued callback after a newer
        // one. The sequence fences those callbacks, including after retraction.
        if revision > 0 {
            guard revision > (observationRevisions[dictationID] ?? 0) else {
                return ObserveOutcome(candidates: 0, rejections: 0, wordsLearned: 0, aliasesLearned: 0, save: .unchanged)
            }
            if observationRevisions[dictationID] == nil { observationOrder.append(dictationID) }
            observationRevisions[dictationID] = revision
            if observationOrder.count > 512 { observationRevisions.removeValue(forKey: observationOrder.removeFirst()) }
        }
        let extracted = LearningPolicy.extract(dictated: dictated, edited: edited, lexicon: lexicon)
        let (applied, save, before) = perform(.learn(candidates: extracted.candidates, dictationID: dictationID,
                                             at: Self.milliseconds(date), scene: scene))
        guard case .learned(let result) = applied else {
            return ObserveOutcome(candidates: 0, rejections: 0, wordsLearned: 0, aliasesLearned: 0, save: save)
        }
        let token: UUID? = save == .saved && (!result.learnedWords.isEmpty || !result.learnedAliases.isEmpty)
            ? UUID() : nil
        if let token { lastLearningUndo = (token, before) }
        return ObserveOutcome(candidates: result.candidates, rejections: extracted.rejections.count,
                              wordsLearned: result.learnedWords.count,
                              aliasesLearned: result.learnedAliases.count,
                              save: save, undoToken: token,
                              pendingCorrections: state.pending.filter { $0.dictations.contains(dictationID) }.count)
    }

    /// Restores the exact vocabulary before the most recent learned change,
    /// including an alias it replaced. A later mutation expires the token.
    /// A failed save follows the normal unsaved-state/flush contract.
    public func undoLearning(_ token: UUID) -> SaveOutcome? {
        guard let undo = lastLearningUndo, undo.token == token else { return nil }
        lastLearningUndo = nil
        state = undo.before
        return persist()
    }

    /// Teaches a word and, optionally, how it sounds.
    @discardableResult
    public func teach(written: String, soundsLike: String?, at date: Date = Date()) -> TeachResult {
        // A refusal can still have pruned expired pending pairs, so it is
        // written like any other change.
        let (applied, _, _) = perform(.teach(written: written, soundsLike: soundsLike, at: Self.milliseconds(date)))
        guard case .taught(let result) = applied else { return .empty }
        return result
    }

    @discardableResult
    public func removeWord(_ text: String, at date: Date = Date()) -> SaveOutcome {
        perform(.removeWord(text, at: Self.milliseconds(date))).save
    }

    @discardableResult
    public func removeAlias(heard: String, meant: String, at date: Date = Date()) -> SaveOutcome {
        perform(.removeAlias(heard: heard, meant: meant, at: Self.milliseconds(date))).save
    }

    /// "Forget all": words, aliases, pending pairs and tombstones.
    @discardableResult
    public func clear() -> SaveOutcome {
        perform(.clear).save
    }

    public func snapshot() -> Snapshot {
        if readIfNeeded() { persist() }
        let storage: StorageStatus = loadOutcome?.keepsFileUnread == true
            ? .keepingUnreadableFile
            : (lastSaveFailed ? .unsaved : .ok)
        return Snapshot(words: state.words, aliases: state.aliases, pendingCount: state.pending.count,
                        lexiconAvailable: lexicon.isAvailable, storage: storage,
                        loadOutcome: loadOutcome ?? .missing)
    }

    /// The host calls this when the app quits: a last try to save what the
    /// file does not hold yet, a failed write or changes made while the file
    /// could not be read. Reads nothing when learning was never used.
    @discardableResult
    public func flush() -> SaveOutcome {
        guard loadOutcome != nil else { return .unchanged }
        readIfNeeded()
        return self.persist()
    }

    // ─── Changes ───

    /// Reads the file if it still needs reading, makes the change, keeps it
    /// for a replay while the file is unread, and writes.
    private func perform(_ change: Change) -> (applied: Applied, save: SaveOutcome, before: LearningState) {
        readIfNeeded()
        let before = state
        let applied = Self.apply(change, to: &state, lexicon: lexicon)
        if state != before { lastLearningUndo = nil }
        if loadOutcome?.keepsFileUnread == true {
            unreplayed.append(change)
        }
        return (applied, persist(), before)
    }

    /// The one place a `Change` becomes a state change, so a replay does what
    /// the original call did.
    @discardableResult
    static func apply(_ change: Change, to state: inout LearningState, lexicon: any LearningLexicon) -> Applied {
        switch change {
        case .learn(let candidates, let dictationID, let at, let scene):
            return .learned(state.learn(candidates, dictationID: dictationID, at: at, lexicon: lexicon, scene: scene))
        case .teach(let written, let soundsLike, let at):
            return .taught(state.teach(written: written, soundsLike: soundsLike, at: at))
        case .removeWord(let text, let at):
            state.removeWord(text, at: at)
        case .removeAlias(let heard, let meant, let at):
            state.removeAlias(heard: heard, meant: meant, at: at)
        case .clear:
            state.clear()
        }
        return .done
    }

    // ─── File ───

    /// Reads the file on first use, and again every time while it could not
    /// be read. Once a read succeeds, the changes made meanwhile are replayed
    /// onto what it holds. True when something was replayed, which the caller
    /// then writes.
    @discardableResult
    private func readIfNeeded() -> Bool {
        if let loadOutcome, !loadOutcome.keepsFileUnread { return false }
        let loaded = store.load()
        loadOutcome = loaded.outcome
        if loaded.outcome.keepsFileUnread {
            // Nothing on disk is known, so no state matches it; the state in
            // memory, and the changes kept for the replay, stay as they are.
            savedBytes = nil
            return false
        }
        // What is on disk now, as this build would write it: an unchanged
        // state then skips the write, and a missing file is not created until
        // there is something to put in it.
        savedBytes = try? LearningStore.encode(loaded.state)
        state = loaded.state
        let replay = unreplayed
        unreplayed = []
        for change in replay {
            Self.apply(change, to: &state, lexicon: lexicon)
        }
        return !replay.isEmpty
    }

    /// The store refuses the write while the file is unread (`.refused`); the
    /// change then waits in memory for the replay.
    @discardableResult
    private func persist() -> SaveOutcome {
        guard let data = try? LearningStore.encode(state) else {
            lastSaveFailed = true
            return .failed
        }
        if data == savedBytes {
            lastSaveFailed = false // memory is back in step with the file
            return .unchanged
        }
        do {
            try store.write(data)
            savedBytes = data
            lastSaveFailed = false
            return .saved
        } catch LearningStore.Failure.keepingUnreadableFile {
            return .refused
        } catch {
            lastSaveFailed = true
            return .failed
        }
    }

    static func milliseconds(_ date: Date) -> Int64 {
        Int64((date.timeIntervalSince1970 * 1000).rounded(.down))
    }
}
