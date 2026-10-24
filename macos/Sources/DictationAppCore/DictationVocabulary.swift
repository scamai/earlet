import DictationLearning
import Foundation
import DictationKit

/// The Vocabulary section's sentences. Windows shows the same ones, word for
/// word; change them on both platforms or not at all.
public enum DictationVocabularyCopy {
    public static let heading = "Vocabulary"
    public static let learnSwitch = "Learn from my corrections"
    public static let disclosure = "In supported text fields, Earlet checks your corrections for up to 90 seconds. Useful words are remembered on this computer for future dictations; learning keeps words, not whole sentences."
    public static let wordPlaceholder = "Word or name"
    public static let soundsLikePlaceholder = "Sounds like (optional)"
    public static let add = "Add"
    public static let empty = "No words yet. Fix a word Earlet got wrong, or add one above."
    public static let learnedFromFix = "Learned from your fix"
    public static let addedByYou = "Added by you"
    public static let remove = "Remove"
    public static let forgetAll = "Forget all"
    public static let forgetAllQuestion = "Forget every learned and added word? Earlet will stop using them."
    public static let cancel = "Cancel"
    /// Shown while the list is not all in `learning.json`: a save failed, or
    /// the file could not be read (LEARNING.md §5).
    public static let unsaved = "Your vocabulary isn't saved yet. Earlet keeps using it and will try again."

    /// The sentence for a refused teach, or `nil` when the word was taught.
    public static func refusal(_ result: TeachResult) -> String? {
        switch result {
        case .ok: return nil
        case .empty: return "Type a word first."
        case .tooLong: return "That is too long. Use up to 60 characters."
        case .selfContaining: return "“Sounds like” cannot be part of the word itself."
        case .full: return "The list is full. Remove a word first."
        }
    }

    /// An alias row: what the recognizer hears, then what it writes.
    public static func alias(heard: String, meant: String) -> String {
        "\(heard) → \(meant)"
    }
}

/// What the Vocabulary section lists and does, over `DictationLearning`.
///
/// Holds no copy of the vocabulary of its own: every change goes to the
/// learning actor, and `rows` is re-read from its snapshot afterwards, so the
/// list is always what `learning.json` holds, or will hold once a failed
/// write or an unreadable file is behind it; `unsaved` says so meanwhile. The
/// log gets outcomes only, never a word.
@MainActor
public final class DictationVocabularyModel: ObservableObject {

    /// One listed entry.
    public struct Row: Identifiable, Equatable, Sendable {
        public enum Entry: Equatable, Sendable {
            case word(String)
            case alias(heard: String, meant: String)
        }

        public let entry: Entry
        /// The word, or "heard → meant".
        public let title: String
        /// Where a word came from; `nil` for an alias.
        public let detail: String?

        public var id: String {
            switch entry {
            case .word(let text): return "word\u{0}\(text)"
            case .alias(let heard, let meant): return "alias\u{0}\(heard)\u{0}\(meant)"
            }
        }
    }

    /// Words (most recently seen first), then aliases.
    @Published public private(set) var rows: [Row] = []
    /// Why the last teach was refused, or `nil`.
    @Published public private(set) var teachMessage: String?
    /// The list is not all on disk yet: a save failed, or `learning.json`
    /// could not be read and is being kept. Dictation keeps using the list,
    /// and the next change (and the last save at quit) tries again, so a
    /// removal shown as done is not silently undone at the next launch.
    @Published public private(set) var unsaved = false
    /// A quiet receipt, only after a successful save; contains no words.
    @Published public private(set) var learningNotice: String?
    /// A quiet receipt for removing or clearing entries; contains no words.
    @Published public private(set) var managementNotice: String?
    /// Privacy-safe feedback about the latest correction observation.
    @Published public private(set) var observationNotice: String?
    /// Whether the one outstanding save retry is in flight.
    @Published public private(set) var retryingSave = false
    @Published public private(set) var undoToken: UUID?
    @Published public private(set) var undoing = false
    private var noticeGeneration = UUID()
    private var refreshGeneration = UUID()
    private var currentObservationID: UUID?

    /// The line the section shows while `unsaved`, or `nil`.
    public var unsavedNotice: String? { unsaved ? DictationVocabularyCopy.unsaved : nil }

    /// `nil` when there is nowhere to keep a vocabulary; the section is then
    /// not shown.
    let learning: DictationLearning?
    private let log: @Sendable (String) -> Void

    public init(learning: DictationLearning?, log: @escaping @Sendable (String) -> Void) {
        self.learning = learning
        self.log = log
    }

    public var isAvailable: Bool { learning != nil }

    /// Re-reads the list from the learning actor.
    public func refresh() async {
        guard let learning else { return }
        let generation = UUID()
        refreshGeneration = generation
        let snapshot = await learning.snapshot()
        guard refreshGeneration == generation else { return }
        rows = Self.rows(snapshot)
        unsaved = snapshot.isUnsaved
    }

    /// The automatic correction path and its receipt share one outcome.
    /// Never infer successful learning from a list changing in memory.
    public func observe(dictationID: String, dictated: String, edited: String,
                        at date: Date = Date(), observationID: UUID? = nil, revision: Int = 0, keepObserving: Bool = false, scene: LearningScene? = nil) async -> DictationLearning.ObserveOutcome {
        guard observationID == nil || currentObservationID == observationID else {
            return .init(candidates: 0, rejections: 0, wordsLearned: 0, aliasesLearned: 0, save: .unchanged)
        }
        let generation = UUID()
        noticeGeneration = generation
        guard let learning else {
            return .init(candidates: 0, rejections: 0, wordsLearned: 0, aliasesLearned: 0, save: .unchanged)
        }
        let result = await learning.observe(dictationID: dictationID, dictated: dictated, edited: edited, at: date, revision: revision, scene: scene)
        await refresh()
        let observationMatches = observationID == nil || currentObservationID == observationID
        if noticeGeneration == generation && observationMatches {
            if !keepObserving { currentObservationID = nil }
            if let token = result.undoToken, !unsaved {
                managementNotice = nil
                observationNotice = nil
                undoToken = token
                learningNotice = "Remembered your correction for next time."
            } else if result.save != .unchanged {
                dismissLearningNotice(keepingObservation: keepObserving)
                observationNotice = result.save == .failed || result.save == .refused
                    ? nil
                    : (result.pendingCorrections > 0
                        ? "Correction checked. Another matching correction is needed before it is used."
                        : (result.candidates > 0
                            ? "This correction is already in your vocabulary."
                            : "No vocabulary change was learned. You can add the word manually below."))
            } else if result.rejections > 0 || dictated != edited {
                dismissLearningNotice(keepingObservation: keepObserving)
                observationNotice = "No vocabulary change was learned. You can add the word manually below."
            } else {
                observationNotice = "No correction detected."
            }
        }
        return result
    }

    /// Starts privacy-safe feedback for one correction-watch session.
    public func beginObservationFeedback(id: UUID) {
        currentObservationID = id
        noticeGeneration = UUID()
        observationNotice = "Checking this field for corrections…"
    }

    /// Ends feedback for a correction-watch session. The mapping is the
    /// shared one (docs/design-macos.md, "Correction notices, save receipts
    /// and Save again"), the same one Windows' `CorrectionNotices` applies
    /// to its observer's endings:
    /// - ended from outside (`abandoned`, `next_take`): no
    ///   notice, whatever the watch saw or taught;
    /// - the span was still located and read when the watch ended
    ///   (`spanObservedToEnd`): after a revision, that revision's own notice
    ///   is the news and stands; otherwise "No correction detected." when
    ///   the span never changed, or "No vocabulary change was learned…"
    ///   when it changed but nothing settled into a correction (`sawChange`);
    /// - any other end: a revision's notice goes with the watch; without a
    ///   revision the outcome stays generic and suggests manual entry.
    ///
    /// `revised` says the watch handed at least one settled revision to
    /// `observe`. When it holds, the watch's id is kept so a settled
    /// revision still in flight is learned: the observer hands every
    /// revision over ahead of the end, but the task that learns it can
    /// finish afterwards.
    public func endObservationFeedback(id: UUID, reason: String, revised: Bool = false,
                                       spanObservedToEnd: Bool = false, sawChange: Bool = false) {
        guard currentObservationID == id else { return }
        noticeGeneration = UUID()
        let condition = DictationTerminalCondition(rawValue: reason.lowercased())
        switch condition {
        case .abandoned, .nextTake:
            currentObservationID = nil
            observationNotice = nil
        default:
            if spanObservedToEnd {
                if !revised {
                    currentObservationID = nil
                    observationNotice = sawChange
                        ? "No vocabulary change was learned. You can add the word manually below."
                        : "No correction detected."
                }
            } else if !revised {
                currentObservationID = nil
                switch condition {
                case .noFocusedField, .spanMissing:
                    observationNotice = "The field could not be read. You can add the word manually below."
                case .spanContextLost:
                    observationNotice = "The dictated text moved before it could be checked. You can add the word manually below."
                case .secureField, .otherApplication:
                    observationNotice = "This field was not checked. You can add the word manually below."
                default:
                    observationNotice = "This field could not be checked. You can add the word manually below."
                }
            } else {
                observationNotice = nil
            }
        }
    }

    public func dismissLearningNotice(keepingObservation: Bool = false) {
        noticeGeneration = UUID()
        learningNotice = nil
        managementNotice = nil
        observationNotice = nil
        if !keepingObservation { currentObservationID = nil }
        undoToken = nil
    }

    public func undoLastLearning() async {
        guard !undoing, let token = undoToken, let learning else { return }
        undoing = true
        defer { undoing = false }
        let generation = noticeGeneration
        let saved = await learning.undoLearning(token)
        // A newer operation can have replaced this notice across the await.
        guard undoToken == token else { await refresh(); return }
        undoToken = nil
        await refresh()
        guard noticeGeneration == generation else { return }
        switch saved {
        case .saved?, .unchanged?: learningNotice = "Learning undone. Your previous vocabulary is restored."
        case .failed?, .refused?: learningNotice = nil // unsavedNotice carries the failure
        case nil: learningNotice = "Vocabulary has changed. Review it below to remove a word."
        }
        log("learning: undo \(saved.map { "\($0)" } ?? "expired")")
    }

    /// Teaches `written` and, when `soundsLike` is not blank, how it sounds.
    /// `true` when it was taught; the view then clears its fields. A refusal
    /// is explained in `teachMessage`.
    ///
    /// The sounds-like field is passed as typed: the learning rules strip it
    /// first, so a field left blank means "no sounds-like", never one that is
    /// too short (LEARNING.md §4.3, as `tests/learning_reference.py` does).
    @discardableResult
    public func teach(written: String, soundsLike: String) async -> Bool {
        dismissLearningNotice()
        teachMessage = nil
        let generation = noticeGeneration
        guard let learning else { return false }
        let result = await learning.teach(written: written, soundsLike: soundsLike)
        log("learning: teach \(result.rawValue)")
        await refresh()
        guard noticeGeneration == generation else { return result == .ok }
        teachMessage = DictationVocabularyCopy.refusal(result)
        if result == .ok, !unsaved { teachMessage = "Saved to your vocabulary on this Mac." }
        return result == .ok
    }

    /// Removes one entry. A removed word takes its aliases with it; neither
    /// is learned again by itself (LEARNING.md §4.4).
    public func remove(_ row: Row) async {
        dismissLearningNotice()
        teachMessage = nil
        let generation = noticeGeneration
        guard let learning else { return }
        let save: DictationLearning.SaveOutcome
        switch row.entry {
        case .word(let text):
            save = await learning.removeWord(text)
            log("learning: removed a word (\(save))")
        case .alias(let heard, let meant):
            save = await learning.removeAlias(heard: heard, meant: meant)
            log("learning: removed an alias (\(save))")
        }
        await refresh()
        guard noticeGeneration == generation else { return }
        managementNotice = managementReceipt(for: save, success: "Removed from your vocabulary.")
    }

    /// "Forget all": every word, alias, pending pair and tombstone.
    public func forgetAll() async {
        dismissLearningNotice()
        teachMessage = nil
        let generation = noticeGeneration
        guard let learning else { return }
        let save = await learning.clear()
        log("learning: forgot everything (\(save))")
        await refresh()
        guard noticeGeneration == generation else { return }
        managementNotice = managementReceipt(for: save, success: "Your vocabulary is empty.")
    }

    /// Tries the pending vocabulary write again. A failure remains visible and
    /// retryable; a successful clean snapshot clears the failure receipt.
    public func retrySaving() async {
        guard !retryingSave, let learning else { return }
        let generation = UUID()
        noticeGeneration = generation
        retryingSave = true
        defer { retryingSave = false }
        let save = await learning.flush()
        await refresh()
        guard noticeGeneration == generation else { return }
        guard (save == .saved || save == .unchanged) && !unsaved else {
            log("learning: retry \(save)")
            managementNotice = "Your vocabulary still isn't saved. Try again when the file is available."
            return
        }
        log("learning: retry \(save)")
        managementNotice = "Changes saved to your vocabulary on this Mac."
    }

    private func managementReceipt(for save: DictationLearning.SaveOutcome,
                                   success: String) -> String {
        if (save == .saved || save == .unchanged) && !unsaved {
            return success
        }
        return "This change isn't saved yet. Earlet keeps using it and will try again."
    }

    static func rows(_ snapshot: DictationLearning.Snapshot) -> [Row] {
        let words = snapshot.words.map { word in
            Row(entry: .word(word.text), title: word.text, detail: word.origin == .correction ? word.learningConfidence : detail(word.origin))
        }
        let aliases = snapshot.aliases.map { alias in
            Row(entry: .alias(heard: alias.heard, meant: alias.meant),
                title: DictationVocabularyCopy.alias(heard: alias.heard, meant: alias.meant),
                detail: nil)
        }
        return words + aliases
    }

    /// Where a word came from. An origin a newer version wrote says nothing
    /// rather than guess, as on Windows.
    static func detail(_ origin: LearningOrigin) -> String? {
        switch origin {
        case .correction: return DictationVocabularyCopy.learnedFromFix
        case .taught: return DictationVocabularyCopy.addedByYou
        case .other: return nil
        }
    }
}
