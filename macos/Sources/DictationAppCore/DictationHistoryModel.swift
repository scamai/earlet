import Combine
import DictationHistory
import Foundation

// The window's view of the dictation history: what is saved, how long it is
// kept, and the counts Home shows. The store and its sealed files are the
// DictationHistory target; the model writes into it from the release path
// (DictationModelPipeline.swift).

/// How long a dictation's words are kept.
public enum DictationHistoryKeep: String, CaseIterable, Identifiable, Sendable {
    case week, month, year, forever

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .week: return "7 days"
        case .month: return "30 days"
        case .year: return "1 year"
        case .forever: return "Forever"
        }
    }

    /// Nil keeps forever.
    public var seconds: TimeInterval? {
        switch self {
        case .week: return 7 * DictationHistoryModel.day
        case .month: return 30 * DictationHistoryModel.day
        case .year: return 365 * DictationHistoryModel.day
        case .forever: return nil
        }
    }
}

/// How long a dictation's recording is kept, never longer than its words.
public enum DictationRecordingKeep: String, CaseIterable, Identifiable, Sendable {
    case off, week, month

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .off: return "Don't keep"
        case .week: return "7 days"
        case .month: return "30 days"
        }
    }

    public var seconds: TimeInterval {
        switch self {
        case .off: return 0
        case .week: return 7 * DictationHistoryModel.day
        case .month: return 30 * DictationHistoryModel.day
        }
    }
}

/// The dictation history as the window shows it.
///
/// Saved: each dictation whose words reached the user (typed in, put on the
/// clipboard, or kept in Copy last text), and its recording unless
/// recordings are not kept. Never saved: a dictation refused for a password
/// field or Secure Input, a spoken command, a cancelled or empty one.
/// Everything is sealed by `DictationHistoryStore` under a key in the login
/// keychain; this model holds the opened entries in memory while the app
/// runs, as the window needs them.
@MainActor
public final class DictationHistoryModel: ObservableObject {
    public enum State: Equatable, Sendable {
        /// Not read yet.
        case loading
        case ready
        /// The keychain refused the key: nothing can be shown or saved.
        case locked
        /// No store: there is no Application Support folder to keep it in.
        case unavailable
    }

    public nonisolated static let day: TimeInterval = 24 * 60 * 60
    /// The recordings' total size at most, the oldest going first: a
    /// minute of speech is about 1.9 MB, so this is about nine hours.
    public nonisolated static let recordingByteLimit: Int64 = 1 << 30

    @Published public private(set) var state: State
    /// Newest first.
    @Published public private(set) var entries: [DictationHistoryEntry] = []
    /// The dictations whose recording is kept.
    @Published public private(set) var recordings: Set<UUID> = []
    @Published public private(set) var recordingBytes: Int64 = 0
    /// Files this Mac's key cannot open (see `DictationHistoryStore`).
    @Published public private(set) var unreadable = 0
    /// Dictations a newer version of the app saved, kept as they are.
    @Published public private(set) var newer = 0
    @Published public private(set) var enabled: Bool
    @Published public private(set) var keep: DictationHistoryKeep
    @Published public private(set) var recordingKeep: DictationRecordingKeep
    /// The last thing the history has to say: a failure, or what a change
    /// deleted. Never words.
    @Published public private(set) var notice: String?

    private let store: DictationHistoryStore?
    private let settings: DictationSettings
    private let writeClipboard: @MainActor (String) async -> Bool
    private let log: @Sendable (String) -> Void
    private let now: @Sendable () -> Date
    private var lastPrune: Date?

    public init(store: DictationHistoryStore?, settings: DictationSettings,
                writeClipboard: @escaping @MainActor (String) async -> Bool,
                log: @escaping @Sendable (String) -> Void = { _ in },
                now: @escaping @Sendable () -> Date = Date.init) {
        self.store = store
        self.settings = settings
        self.writeClipboard = writeClipboard
        self.log = log
        self.now = now
        self.state = store == nil ? .unavailable : .loading
        self.enabled = settings.historyEnabled
        self.keep = settings.historyKeep
        self.recordingKeep = settings.recordingKeep
    }

    public var isAvailable: Bool { store != nil }

    /// Whether the next dictation's recording is kept with it.
    public var keepsRecordings: Bool { enabled && recordingKeep != .off && state != .locked && store != nil }

    /// The counts of the dictations made since `date`, or of all of them.
    public func stats(since date: Date? = nil) -> DictationHistoryStats {
        guard let date else { return DictationHistoryStats(entries) }
        return DictationHistoryStats(entries.lazy.filter { $0.recordedAt >= date })
    }

    /// The last seven days' counts, for Home: worked out when the history
    /// changes (and at each refresh and prune), not on every redraw, since
    /// counting words reads every saved dictation.
    @Published public private(set) var lastSevenDays = DictationHistoryStats()

    // ─── Reading ───

    /// Reads the history (once per launch, then from memory) and applies
    /// the retention.
    public func refresh() async {
        guard let store else { return }
        do {
            show(try await store.load())
            state = .ready
        } catch {
            fail(error, doing: "read")
        }
        await prune()
    }

    /// Try Again after the keychain refused: reads the disk and the
    /// keychain afresh.
    public func retry() async {
        guard let store else { return }
        await store.reload()
        state = .loading
        notice = nil
        await refresh()
    }

    /// A kept recording, as 16 kHz mono PCM16.
    public func recording(for entry: DictationHistoryEntry) async -> Data? {
        guard let store, recordings.contains(entry.id) else { return nil }
        do {
            return try await store.recording(entry.id)
        } catch {
            fail(error, doing: "open a recording")
            return nil
        }
    }

    // ─── Writing ───

    /// The saves handed over and not yet finished, so quitting can wait.
    private var inFlight: [UUID: Task<Void, Never>] = [:]

    /// Saves without holding the caller: the release path hands a dictation
    /// over and moves on to the next one.
    func enqueue(_ entry: DictationHistoryEntry, recording: Data?) {
        let token = UUID()
        inFlight[token] = Task { [weak self] in
            await self?.record(entry, recording: recording)
            self?.inFlight[token] = nil
        }
    }

    /// Waits for every save already handed over: quitting calls it, so a
    /// dictation finished just before Quit is on disk when the app exits.
    public func flush() async {
        for task in Array(inFlight.values) { await task.value }
    }

    /// Saves one dictation, unless the history is off. The recording is
    /// dropped when recordings are not kept.
    func record(_ entry: DictationHistoryEntry, recording: Data?) async {
        guard enabled, let store else { return }
        // A locked history stays locked until Try Again: the store keeps the
        // keychain's refusal and does not ask again, so its prompt never
        // follows each dictation (DictationHistoryStore).
        do {
            show(try await store.append(entry, recording: keepsRecordings ? recording : nil))
            state = .ready
            log("history: saved a dictation (\(entry.text.count) characters, recording \(keepsRecordings && recording != nil ? "kept" : "not kept"))")
        } catch {
            fail(error, doing: "save a dictation")
        }
    }

    public func delete(_ entry: DictationHistoryEntry) async {
        guard let store else { return }
        do {
            show(try await store.delete(entry.id))
            notice = nil
        } catch {
            fail(error, doing: "delete a dictation")
        }
    }

    /// Deletes every dictation and recording, and destroys the key.
    public func deleteAll() async {
        guard let store else { return }
        do {
            try await store.deleteAll()
            show(DictationHistoryStore.Snapshot())
            state = .ready
            notice = "History deleted. Its key was destroyed, so nothing left on this Mac can be read."
            log("history: deleted everything and destroyed the key")
        } catch {
            fail(error, doing: "delete the history")
        }
    }

    public func deleteUnreadable() async {
        guard let store else { return }
        do {
            show(try await store.deleteUnreadable())
            notice = nil
        } catch {
            fail(error, doing: "delete the unreadable files")
        }
    }

    /// Puts one dictation's words on the clipboard, the clipboard-safe way
    /// Copy last text does. Returns whether they landed.
    @discardableResult
    public func copy(_ entry: DictationHistoryEntry) async -> Bool {
        let copied = await writeClipboard(entry.text)
        notice = copied ? nil : "Couldn't copy. Try again."
        return copied
    }

    // ─── Settings ───

    /// Off: new dictations are not saved. Saved ones stay until they are
    /// deleted or their time is up.
    public func setEnabled(_ on: Bool) {
        enabled = on
        settings.historyEnabled = on
        log("history: \(on ? "on" : "off")")
    }

    /// How many saved dictations a shorter keep would delete now: the
    /// window asks before it applies one that deletes any.
    public func dictationsDeleted(by choice: DictationHistoryKeep) -> Int {
        guard let seconds = choice.seconds else { return 0 }
        let cutoff = now().addingTimeInterval(-seconds)
        return entries.lazy.filter { $0.recordedAt < cutoff }.count
    }

    /// How many kept recordings a shorter keep would delete now.
    public func recordingsDeleted(by choice: DictationRecordingKeep) -> Int {
        let cutoff = now().addingTimeInterval(-min(choice.seconds, keep.seconds ?? .infinity))
        return entries.lazy.filter { self.recordings.contains($0.id) && $0.recordedAt < cutoff }.count
    }

    public func setKeep(_ choice: DictationHistoryKeep) async {
        keep = choice
        settings.historyKeep = choice
        await prune(force: true)
    }

    public func setRecordingKeep(_ choice: DictationRecordingKeep) async {
        recordingKeep = choice
        settings.recordingKeep = choice
        await prune(force: true)
    }

    /// Applies the retention: at launch, after a setting changes, and at
    /// most once an hour from housekeeping.
    public func prune(force: Bool = false) async {
        guard let store, state == .ready else { return }
        let time = now()
        if !force, let lastPrune, time.timeIntervalSince(lastPrune) < 60 * 60 { return }
        lastPrune = time
        let recordingSeconds = min(recordingKeep.seconds, keep.seconds ?? .infinity)
        let retention = DictationHistoryStore.Retention(
            dictations: keep.seconds, recordings: recordingSeconds, recordingBytes: Self.recordingByteLimit)
        do {
            let pruned = try await store.prune(retention, now: time)
            show(try await store.load())
            if pruned.dictations > 0 || pruned.recordings > 0 {
                log("history: retention deleted \(pruned.dictations) dictation(s) and \(pruned.recordings) recording(s)")
            }
        } catch {
            fail(error, doing: "apply the retention")
        }
    }

    // ─── Inside ───

    private func show(_ snapshot: DictationHistoryStore.Snapshot) {
        entries = snapshot.entries
        recordings = snapshot.recordings
        recordingBytes = snapshot.recordingBytes
        unreadable = snapshot.unreadable
        newer = snapshot.newer
        lastSevenDays = stats(since: now().addingTimeInterval(-7 * Self.day))
    }

    private func fail(_ error: Error, doing what: String) {
        switch error as? DictationHistoryStore.Failure {
        case .locked(let reason)?:
            state = .locked
            notice = "History is locked: Earlet couldn't open its key in your Keychain."
            log("history: locked while trying to \(what) (\(reason))")
        case .disk(let reason)?:
            notice = "History couldn't \(what). Your dictation itself was not affected."
            log("history: could not \(what) (\(reason))")
        case nil:
            notice = "History couldn't \(what)."
            log("history: could not \(what) (\(DictationHistoryModel.describe(error)))")
        }
    }

    private nonisolated static func describe(_ error: Error) -> String {
        let nsError = error as NSError
        return "\(nsError.domain) \(nsError.code)"
    }
}
