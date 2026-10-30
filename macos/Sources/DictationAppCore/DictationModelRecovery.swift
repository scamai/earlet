import Foundation
import DictationHistory
import DictationKit

extension DictationModel {
    func startRecoveryExpiry() {
        guard recoveryExpiry == nil else { return }
        let interval = max(0.05, env.timing.housekeepingSeconds)
        recoveryExpiry = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .seconds(interval)) } catch { return }
                guard let self, !self.isQuitting else { return }
                await self.expireSavedTake()
                await self.retryRecordingCleanup()
            }
        }
    }

    // ─── The take spool and Recover Last Dictation ───

    /// Made on first use, on the main actor, before any take.
    func takeSpool() -> DictationTakeSpool {
        let log = env.log
        return takeTap.makeSpoolIfNeeded {
            DictationTakeSpool(
                directory: env.takes.directory, runFileWork: env.takes.runFileWork,
                log: { line in log("take spool: \(line)") })
        }
    }

    /// Admit's, before the microphone opens: the take's file, so its first
    /// buffer is spooled too. A take the last hold never settled (its
    /// pipeline never answered) is kept, not lost.
    func beginTake() {
        if let unanswered = takeTap.settle() {
            env.log("a take the last hold never settled is kept for recovery")
            Task { await keepTake(unanswered) }
        }
        installRetainedAudioTapIfNeeded()
        let spool = takeSpool()
        var policy = env.timing.holdPolicy
        policy.silenceSetting = silenceSetting
        _ = takeTap.begin(spool: { spool }, startedAt: Date(), policy: policy)
    }

    /// What becomes of the take, by its ending (rule zero): a take whose
    /// words could not be transcribed is kept for Recover Last Dictation;
    /// every other ending deletes its spool — the words went in, or are on
    /// the clipboard or in Copy last text, or nothing was heard, or the user
    /// cancelled. Never a take that failed half-written or holds no audio.
    /// Returns whether the take is being kept.
    @discardableResult
    func settleTake(worthKeeping: Bool) async -> Bool {
        guard let take = takeTap.settle() else { return false }
        guard worthKeeping, !take.failed, take.captured > 0 else {
            if await !takeSpool().discard(take) { pendingRecordingCleanup = true }
            return false
        }
        return await keepTake(take)
    }

    public func retryRecordingCleanup() async {
        guard pendingRecordingCleanup else { return }
        pendingRecordingCleanup = await !takeSpool().retryDiscards()
    }

    /// Keeps the take for Recover Last Dictation, replacing any earlier one
    /// (one is kept at a time). Returns false when the spool could not keep
    /// it after all.
    func keepTake(_ take: DictationTake) async -> Bool {
        changingSavedTake = true
        defer { changingSavedTake = false }
        let replaced = savedTake != nil
        guard await takeSpool().keep(take) else {
            env.log("the take could not be kept for recovery after all")
            return false
        }
        env.log("the take (\(take.captured) s) is kept for Recover Last Dictation")
        if replaced {
            env.log("it replaces the earlier saved recording: one is kept at a time")
        }
        savedTake = DictationKeptTake(
            recordedAt: take.startedAt, length: take.captured,
            interrupted: false, replacedEarlier: replaced)
        return true
    }

    /// `prepareForQuit`'s: Dictation is closing under a take. Its file
    /// stays, and the next launch offers it (rule zero: a quit mid-take
    /// loses nothing).
    func leaveTake() async {
        guard let take = takeTap.settle() else { return }
        env.log("Dictation closed during a take; its recording is left for the next launch to offer")
        await takeSpool().leave(take)
    }

    /// `launch`'s: what the last run left. A take a crash or a quit left
    /// behind is offered, and the window comes forward with it, even at a
    /// login launch; a kept take older than `takes.keptFor` was deleted
    /// unheard by the spool.
    func offerSavedTakeAtLaunch() {
        guard let found = takeSpool().atLaunch(now: Date(), keptFor: env.takes.keptFor) else { return }
        savedTake = found
        env.log("a saved recording of \(found.length) s is offered for recovery: "
            + (found.interrupted ? "Dictation closed before it was transcribed" : "it could not be transcribed"))
        guard found.interrupted else { return }
        windowAttention += 1
        status = DictationCopy.savedTakeOfferedAtLaunch
    }

    /// Housekeeping's: a kept take older than `takes.keptFor` is deleted
    /// unheard while Dictation runs, as the next launch would; the app
    /// starts at login and can run for weeks. Not under a recovery: the
    /// first tick after it ends does it, if the recording is still kept.
    func expireSavedTake() async {
        guard let kept = savedTake, !recoveringSavedTake, !changingSavedTake, !busy, !recording,
              Date().timeIntervalSince(kept.recordedAt) >= env.takes.keptFor else { return }
        if await forgetSavedTake(same: true) {
            env.log("the saved recording expired and was deleted unheard")
        }
    }

    /// Whether Recover Last Dictation can start: a take is kept, dictation
    /// is on with its speech engine, and nothing is recording, transcribing,
    /// copying or recovering.
    public var canRecoverSavedTake: Bool {
        savedTake != nil && !recoveringSavedTake && !changingSavedTake && !isQuitting && enabled && !busy && !recording
            && attempt == nil && !recentResult.copying
    }

    /// Whether the saved recording is waiting for its recovered words to be
    /// copied out of the recent result.
    public var savedTakeAwaitsCopy: Bool {
        guard let recovered, savedTake == recovered.take else { return false }
        return recentResult.entry?.id == recovered.entryID
    }

    /// Recover Last Dictation, from the menu or the window's Saved recording
    /// section: transcribe the saved take again. Its words go to the recent
    /// result and, thence, onto the clipboard through Copy last text (never
    /// pasted: the user is in the menu or the window, not the field they
    /// spoke into). The recording goes once its words are on the clipboard
    /// (or are dismissed); a failure, or a file that cannot be opened just
    /// now, keeps it for another try.
    public func recoverLastDictation() async {
        guard canRecoverSavedTake, let kept = savedTake else { return }
        recoveringSavedTake = true
        defer { recoveringSavedTake = false }
        markWorkerUsed()
        env.log("recovering the saved recording (\(kept.length) s)")
        status = DictationCopy.recoveringSavedTake
        // Not read is not damaged: a failure before the read answered keeps it.
        let audio = await takeSpool().readKept()
        guard !isQuitting else { return }
        guard !audio.unavailable else {
            env.log("the saved recording could not be opened just now; it is still kept")
            status = DictationCopy.savedTakeNotOpened
            return
        }
        guard let samples = audio.samples else {
            env.log("the saved recording could not be read; it is discarded")
            if await forgetSavedTake(same: savedTake == kept) { status = DictationCopy.savedTakeUnreadable }
            return
        }
        guard DictationSession.containsAudibleSignal(samples, threshold: DictationSession.defaultAudibleThreshold) else {
            if await forgetSavedTake(same: savedTake == kept) { status = DictationCopy.savedTakeNothingHeard }
            return
        }
        guard await ensureWorkerStarted() == .started, !isQuitting, let worker else {
            status = DictationCopy.savedTakeEngineUnavailable
            return
        }
        // The current Language setting, and none of the checks that judge
        // whether the audio was speech: one of them may be why the take was
        // kept, and these words are copied, never typed.
        slot.setLanguageCode(language.code)
        let seconds = env.timing.transcribeDeadline(audioSeconds: kept.length)
        let outcome = await DictationDeadline.run(
            seconds: seconds,
            work: { [slot] in
                await slot.transcribeForRecovery(samples: samples, sampleRate: DictationTakeSpool.sampleRate)
            },
            onAbandon: { worker.forceTerminate() })
        guard !isQuitting else { return }
        markWorkerUsed()
        guard let outcome else {
            env.log("recovering the saved recording exceeded \(seconds) s; the worker was killed")
            if self.worker === worker { self.worker = nil }
            status = DictationCopy.savedTakeRecoveryFailed
            return
        }
        switch outcome {
        case .failed(let failure):
            env.log("the saved recording could not be transcribed (\(failure)); it is still kept")
            if Self.workerDied(failure) { Task { await restartWorker() } }
            status = DictationCopy.savedTakeRecoveryFailed
        case .text(let text):
            let formatted = DictationSession.insertionText(
                text, formattingEnabled: true, styleRules: nil,
                targetBundleIDProvider: env.targetApplication)
            guard !formatted.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                env.log("the saved recording held nothing to type; it is discarded")
                if await forgetSavedTake(same: savedTake == kept) { status = DictationCopy.savedTakeNothingHeard }
                return
            }
            // A count, never the words.
            env.log("the saved recording was recovered: \(formatted.count) characters")
            recentResult.remember(formatted, needsRecovery: false)
            let inHistory = saveToHistory(formatted, take: DictationHistoryTake(
                    startedAt: kept.recordedAt, seconds: kept.length,
                    audio: history.keepsRecordings ? Self.pcm16(samples) : nil),
                heldSeconds: kept.length, app: nil, delivery: .recovered, language: nil)
            recentResult.noteInHistory(inHistory, for: recentResult.entry?.id)
            // Still the take this recovery read: a newer failure may have
            // replaced it, and that one must not be deleted for this one.
            let same = savedTake == kept
            if same, let entry = recentResult.entry {
                // The recording stays until its words are on the clipboard
                // (recoveredWordsCopied): until then a dictation that
                // finishes first would replace them in the recent result,
                // their only other copy.
                recovered = (entryID: entry.id, take: kept)
            }
            status = DictationCopy.savedTakeRecovered
            await copyRecentResult()
        }
    }

    /// Discard, in the Saved recording section: the kept take is deleted
    /// unheard. Not during a recovery.
    public func discardSavedTake() async {
        guard !recoveringSavedTake, !changingSavedTake, !busy, !recording, savedTake != nil else { return }
        busy = true
        defer { busy = false }
        guard await takeSpool().discardKept() else {
            status = "The saved recording could not be deleted. It is still available; try again."
            return
        }
        savedTake = nil
        recovered = nil
        env.log("the saved recording was discarded")
        status = DictationCopy.savedTakeDiscarded
    }

    /// The kept take is done with: forgotten, and its file deleted when it
    /// is still the one kept.
    @discardableResult
    func forgetSavedTake(same: Bool) async -> Bool {
        guard same else { return false }
        guard await takeSpool().discardKept() else {
            status = "The saved recording could not be deleted. It is still available; try again."
            return false
        }
        savedTake = nil
        recovered = nil
        return true
    }

    /// `copyRecentResult`'s, for a copy that reached the clipboard: when it
    /// carried a recovery's words, the recording they came from goes.
    func recoveredWordsCopied() async {
        guard let recovered, recentResult.entry?.id == recovered.entryID else { return }
        if await forgetSavedTake(same: savedTake == recovered.take) {
            env.log("the recovered words were copied and their recording was deleted")
        }
    }

    /// The recent result's Dismiss: dismissing a recovery's words is done
    /// with them, and the recording they came from goes too.
    public func dismissRecentResult() async {
        guard !busy, !recording, !recoveringSavedTake, !changingSavedTake else { return }
        busy = true
        defer { busy = false }
        if let recovered, recentResult.entry?.id == recovered.entryID {
            guard await forgetSavedTake(same: savedTake == recovered.take) else { return }
            env.log("the recovered words and their recording were discarded")
        }
        recentResult.clear()
    }

}

// ─── The take spool's plumbing ───

/// The take the audio tap is spooling, behind a lock: the capture's
/// collector task reads it off the main actor, and the model begins and
/// settles takes on the main actor. The spool itself takes the concurrency
/// from there (conversion on the caller, file work on its runner).
final class DictationTakeTap: @unchecked Sendable {
    private let lock = NSLock()
    private var take: DictationTake?
    private var spool: DictationTakeSpool?
    private var tracker = DictationHoldTracker()

    /// What a retained chunk appends to, or nil outside a take.
    func live() -> (take: DictationTake, spool: DictationTakeSpool, tracker: DictationHoldTracker)? {
        lock.lock(); defer { lock.unlock() }
        guard let take, let spool else { return nil }
        return (take, spool, tracker)
    }

    /// The spool, made on first use.
    func makeSpoolIfNeeded(_ make: () -> DictationTakeSpool) -> DictationTakeSpool {
        lock.lock(); defer { lock.unlock() }
        if let spool { return spool }
        let made = make()
        spool = made
        return made
    }

    /// The take's start, on the main actor: the spool is made on first use,
    /// before the take, so its file work is queued from the first buffer.
    func begin(spool makeSpool: () -> DictationTakeSpool, startedAt: Date, policy: DictationHoldPolicy = .stage1) -> DictationTake {
        lock.lock(); defer { lock.unlock() }
        let spool = spool ?? makeSpool()
        self.spool = spool
        tracker = DictationHoldTracker(policy: policy)
        let take = spool.begin(startedAt: startedAt)
        self.take = take
        return take
    }

    /// The take is settled: detached and returned, or nil when none was
    /// live. The spool stays.
    @discardableResult
    func settle() -> DictationTake? {
        lock.lock(); defer { lock.unlock() }
        let take = self.take
        self.take = nil
        return take
    }

    /// The spool, once made.
    var currentSpool: DictationTakeSpool? { lock.lock(); defer { lock.unlock() }; return spool }
}


// ─── The history ───

/// What the history keeps of a take besides its words, read before the
/// take's file is deleted: when it began, how long it is, and its audio when
/// recordings are kept.
struct DictationHistoryTake: Sendable {
    var startedAt: Date
    var seconds: TimeInterval
    var audio: Data?
}

extension DictationModel {
    /// The live take, for the history, before `settleTake` deletes its file;
    /// nil when the history is off or there is no take.
    func takeForHistory() async -> DictationHistoryTake? {
        guard history.enabled, history.isAvailable, let live = takeTap.live() else { return nil }
        let take = live.take
        var audio: Data?
        if history.keepsRecordings, !take.failed, take.captured > 0 {
            audio = await live.spool.pcm(take)
        }
        return DictationHistoryTake(startedAt: take.startedAt, seconds: take.captured, audio: audio)
    }

    /// Saves a delivered dictation to the history, off the release path, and
    /// says whether it was handed over. Never for empty words, nor for words
    /// that went into Dictation's own window; the callers never pass a
    /// password-field or Secure Input refusal, a spoken command, or a
    /// cancelled hold.
    @discardableResult
    func saveToHistory(_ text: String?, take: DictationHistoryTake?, heldSeconds: TimeInterval,
                       app: String?, delivery: DictationHistoryEntry.Delivery, language: String?) -> Bool {
        guard history.enabled, let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        // The practice field says its text is not saved, and a word typed
        // into Vocabulary or History is not a dictation to keep.
        if let app, app == env.ownBundleID { return false }
        let seconds = (take?.seconds ?? 0) > 0 ? take!.seconds : heldSeconds
        let entry = DictationHistoryEntry(
            recordedAt: take?.startedAt ?? Date().addingTimeInterval(-heldSeconds),
            text: text, audioSeconds: seconds, language: language, appBundleID: app, delivery: delivery)
        history.enqueue(entry, recording: take?.audio)
        return true
    }

    /// Float samples to the PCM16 the spool writes, for a recovered take.
    static func pcm16(_ samples: [Float]) -> Data {
        var data = Data(capacity: samples.count * 2)
        for sample in samples {
            var value = DictationTakeSpool.quantise(sample).littleEndian
            withUnsafeBytes(of: &value) { data.append(contentsOf: $0) }
        }
        return data
    }
}
