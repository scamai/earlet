// The dictation history through the real release path: what a dictation
// leaves on the disk once its words are delivered, read back by a fresh store
// the way the next launch reads it, and what it never leaves there. The store
// lives in the harness's scratch folder with its key in memory; no test
// reaches the login keychain or the user's history.

#if canImport(Testing)
import Foundation
import Testing
import DictationHistory
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Dictation history")
struct HistoryTests {
    private func harness() -> Harness {
        var harness = Harness()
        harness.historyAvailable = true
        harness.targetApplication.mutate { $0 = "com.apple.Notes" }
        return harness
    }

    private func enabled(_ harness: Harness) async -> DictationModel {
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return model
    }

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        // One second of audio through the retained-audio tap: the spool's,
        // and so the recording's, contents.
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    /// The history on disk, once it holds `count` dictations. Every save the
    /// model handed over has finished first: a dictation's entry file is
    /// written before its recording's, so a reader that only counted
    /// entries could catch one without its recording, under load.
    private func saved(_ harness: Harness, _ model: DictationModel, count: Int) async throws -> DictationHistoryStore.Snapshot {
        await model.history.flush()
        let deadline = Date().addingTimeInterval(5)
        var snapshot = try await harness.reopenedHistory()
        while snapshot.entries.count < count, Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
            snapshot = try await harness.reopenedHistory()
        }
        return snapshot
    }

    /// Every byte the history folder holds.
    private func historyBytes(_ harness: Harness) -> [Data] {
        let walk = FileManager.default.enumerator(at: harness.historyFiles.url, includingPropertiesForKeys: nil)
        var found: [Data] = []
        while let file = walk?.nextObject() as? URL {
            if let data = try? Data(contentsOf: file) { found.append(data) }
        }
        return found
    }

    @Test("a dictation that went in is saved with where it went and its recording, sealed")
    func savesInserted() async throws {
        let harness = harness()
        let model = await enabled(harness)
        await dictate(harness, model)

        let snapshot = try await saved(harness, model, count: 1)
        let entry = try #require(snapshot.entries.first)
        #expect(entry.text == harness.inserts.texts.get.last)
        #expect(entry.text == "Hello world")
        #expect(entry.delivery == .inserted)
        #expect(entry.appBundleID == "com.apple.Notes")
        #expect(entry.audioSeconds == 1)
        #expect(snapshot.recordings == [entry.id])
        let store = DictationHistoryStore(directory: harness.historyFiles.url, keys: harness.historyKeys)
        #expect(try await store.recording(entry.id)?.count == 32_000, "the take's one second of PCM16")
        for data in historyBytes(harness) {
            #expect(data.range(of: Data("Hello world".utf8)) == nil, "a history file holds the words in the clear")
            #expect(data.range(of: Data("com.apple.Notes".utf8)) == nil, "a history file names the app in the clear")
        }
        #expect(harness.takeSpoolFiles().isEmpty, "the take's spool still goes once its words are in")
        #expect(await eventually { model.history.entries.map(\.id) == [entry.id] })
        #expect(model.recentResult.entry?.inHistory == true, "the last text says the history keeps a copy")
        #expect(!harness.logLines.get.joined(separator: "\n").contains("Hello world"), "the log never holds the words")
    }

    @Test("a dictation finished just before Quit is on disk when quitting returns")
    func savedBeforeQuitReturns() async throws {
        let harness = harness()
        // The first save reads and makes the key: 0.6 s, as a keychain that asks.
        harness.historyKeys.slowDown(0.3)
        let model = await enabled(harness)
        await dictate(harness, model)
        await model.prepareForQuit()
        harness.historyKeys.slowDown(0)
        #expect(try await harness.reopenedHistory().entries.count == 1, "quit waited for the save")
    }

    @Test("with the history off nothing new is saved, and what was saved stays")
    func offSavesNothing() async throws {
        let harness = harness()
        let model = await enabled(harness)
        await dictate(harness, model)
        _ = try await saved(harness, model, count: 1)
        model.history.setEnabled(false)
        #expect(!harness.settings.historyEnabled)
        await dictate(harness, model)
        try await Task.sleep(for: .milliseconds(200))
        #expect(try await harness.reopenedHistory().entries.count == 1)
        model.history.setEnabled(true)
        await dictate(harness, model)
        #expect(try await saved(harness, model, count: 2).entries.count == 2, "on again, the next one is saved")
    }

    @Test("a dictation into Dictation's own window is not saved, and the last text says so")
    func ownWindowNotSaved() async throws {
        let harness = harness()
        // The practice field: its caption promises its text is not saved.
        harness.targetApplication.mutate { $0 = "ai.scam.dictation" }
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(harness.inserts.texts.get.last == "Hello world", "the words still went in")
        try await Task.sleep(for: .milliseconds(200))
        #expect(try await harness.reopenedHistory().entries.isEmpty)
        let entry = try #require(model.recentResult.entry)
        #expect(!entry.inHistory)
        #expect(DictationCopy.recentResultFootnote(inHistory: entry.inHistory).hasSuffix("It isn’t saved to disk."))

        harness.targetApplication.mutate { $0 = "com.apple.Notes" }
        await dictate(harness, model)
        let saved = try await saved(harness, model, count: 1)
        #expect(saved.entries.map(\.appBundleID) == ["com.apple.Notes"], "only the other app's dictation")
        #expect(model.recentResult.entry?.inHistory == true)
        #expect(DictationCopy.recentResultFootnote(inHistory: true).hasSuffix("Your history keeps a copy, encrypted on this Mac."))
    }

    @Test("a dictation finished while the keychain asks, which then refuses, does not ask again")
    func refusedOnceAsksOnce() async throws {
        let harness = harness()
        // The launch's read waits a second on the keychain, which then refuses:
        // the dictation's save queues behind it.
        harness.historyKeys.slowDown(1)
        harness.historyKeys.refuse(.unavailable(-128))
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(harness.inserts.texts.get.last == "Hello world", "the words still went in")
        #expect(await eventually { model.history.state == .locked })
        await model.history.flush()
        #expect(harness.historyKeys.calls == 1, "the queued save asked the keychain again after it refused")
        harness.historyKeys.slowDown(0)
    }

    @Test("with recordings not kept the words are saved and the audio is not")
    func recordingsOff() async throws {
        let harness = harness()
        harness.settings.recordingKeep = .off
        let model = await enabled(harness)
        #expect(!model.history.keepsRecordings)
        await dictate(harness, model)
        let snapshot = try await saved(harness, model, count: 1)
        #expect(snapshot.entries.count == 1)
        #expect(snapshot.recordings.isEmpty)
        #expect(snapshot.recordingBytes == 0)
    }

    @Test("a password field or Secure Input at the paste is never saved", arguments: [
        DictationInsertFailure.secureFieldFocused, .secureInputActive(heldByOtherProcess: true),
    ])
    func secureNeverSaved(_ failure: DictationInsertFailure) async throws {
        let harness = harness()
        let model = await enabled(harness)
        harness.inserts.outcome.mutate { $0 = .failed(failure) }
        await dictate(harness, model)
        // The control: the next ordinary dictation is saved, and it is the
        // only one.
        harness.inserts.outcome.mutate { $0 = .success(clipboardByteIdentical: true) }
        await dictate(harness, model)
        let snapshot = try await saved(harness, model, count: 1)
        try await Task.sleep(for: .milliseconds(200))
        #expect(try await harness.reopenedHistory().entries.count == 1)
        #expect(snapshot.entries.first?.delivery == .inserted)
        #expect(snapshot.recordings.count == 1, "only the delivered dictation's recording")
    }

    @Test("a refused paste is saved as on the clipboard, or as not inserted when the clipboard failed too")
    func refusedPaste() async throws {
        let harness = harness()
        let model = await enabled(harness)
        harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        await dictate(harness, model)
        _ = try await saved(harness, model, count: 1)
        harness.clipboardWorks.mutate { $0 = false }
        await dictate(harness, model)
        let snapshot = try await saved(harness, model, count: 2)
        #expect(snapshot.entries.map(\.delivery).sorted { $0.rawValue < $1.rawValue } == [.clipboard, .notInserted])
    }

    @Test("a cancelled hold is not saved")
    func cancelledNotSaved() async throws {
        let harness = harness()
        let model = await enabled(harness)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.capture.emitRetainedAudio([Float](repeating: 0.2, count: 16_000))
        harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
        #expect(await eventually { !model.recording && !model.busy })
        harness.trigger.release()
        await dictate(harness, model)
        let snapshot = try await saved(harness, model, count: 1)
        try await Task.sleep(for: .milliseconds(200))
        #expect(try await harness.reopenedHistory().entries.count == 1)
        #expect(snapshot.entries.count == 1)
    }

    @Test("a refused keychain: the words still go in, nothing is written, and History says locked")
    func lockedKeychain() async throws {
        let harness = harness()
        harness.historyKeys.refuse(.unavailable(-25293))
        let model = await enabled(harness)
        await dictate(harness, model)
        #expect(harness.inserts.texts.get == ["Hello world"])
        #expect(await eventually { model.history.state == .locked })
        #expect(historyBytes(harness).isEmpty)
        #expect(model.history.notice?.contains("Keychain") == true)
        // Locked stays locked: the next dictation does not ask the keychain
        // again (its prompt would come up after every dictation).
        let asked = harness.historyKeys.calls
        await dictate(harness, model)
        try await Task.sleep(for: .milliseconds(200))
        #expect(harness.historyKeys.calls == asked)
        #expect(harness.inserts.texts.get.count == 2)
        // Try Again once the keychain answers: the next dictation is saved.
        harness.historyKeys.refuse(nil)
        await model.history.retry()
        #expect(model.history.state == .ready)
        await dictate(harness, model)
        #expect(try await saved(harness, model, count: 1).entries.count == 1)
    }

    @Test("Delete All History destroys the key and leaves nothing to read")
    func deleteAll() async throws {
        let harness = harness()
        let model = await enabled(harness)
        await dictate(harness, model)
        _ = try await saved(harness, model, count: 1)
        #expect(await eventually { model.history.entries.count == 1 })
        await model.history.deleteAll()
        #expect(harness.historyKeys.current == nil)
        #expect(model.history.entries.isEmpty)
        #expect(historyBytes(harness).isEmpty)
        #expect(try await harness.reopenedHistory().entries.isEmpty)
    }

    @Test("a shorter keep deletes the older dictations at once, recordings with them")
    func shorterKeep() async throws {
        let harness = harness()
        let store = try #require(harness.historyStore)
        let day: TimeInterval = 24 * 60 * 60
        let old = DictationHistoryEntry(recordedAt: Date().addingTimeInterval(-20 * day), text: "old words",
                                        audioSeconds: 3, language: nil, appBundleID: nil, delivery: .inserted)
        let new = DictationHistoryEntry(recordedAt: Date().addingTimeInterval(-60), text: "new words",
                                        audioSeconds: 3, language: nil, appBundleID: nil, delivery: .inserted)
        try await store.append(old, recording: Data(repeating: 1, count: 64))
        try await store.append(new, recording: Data(repeating: 2, count: 64))
        let model = harness.model()
        await model.history.refresh()
        #expect(model.history.entries.count == 2)
        // Recordings are kept 30 days by default, so the old one is still there.
        #expect(model.history.recordings == [old.id, new.id])
        #expect(model.history.dictationsDeleted(by: .week) == 1)
        #expect(model.history.recordingsDeleted(by: .week) == 1)
        await model.history.setKeep(.week)
        #expect(harness.settings.historyKeep == .week)
        #expect(model.history.entries.map(\.text) == ["new words"])
        #expect(model.history.recordings == [new.id])
        await model.history.setRecordingKeep(.off)
        #expect(model.history.recordings.isEmpty)
        #expect(try await harness.reopenedHistory().entries.map(\.text) == ["new words"])
    }

    @Test("recovered words are saved as recovered, with the saved recording's audio")
    func recovered() async throws {
        let harness = harness()
        let model = await enabled(harness)
        harness.workers.made[0].state.mutate { $0.replies = [.failure(.engineError("bad audio")), .text("recovered words")] }
        await dictate(harness, model)
        #expect(model.savedTake != nil)
        try await Task.sleep(for: .milliseconds(100))
        #expect(try await harness.reopenedHistory().entries.isEmpty, "a take that could not be transcribed has no words to save")
        await model.recoverLastDictation()
        let snapshot = try await saved(harness, model, count: 1)
        let entry = try #require(snapshot.entries.first)
        #expect(entry.delivery == .recovered)
        #expect(entry.text == "Recovered words")
        #expect(snapshot.recordings == [entry.id])
    }

    @Test("Home's counts are the last seven days of the history")
    func lastSevenDays() async throws {
        let harness = harness()
        let store = try #require(harness.historyStore)
        let day: TimeInterval = 24 * 60 * 60
        for (age, text) in [(10 * day, "too old to count"), (3 * day, "three words here"), (60, "two words")] {
            try await store.append(DictationHistoryEntry(recordedAt: Date().addingTimeInterval(-age), text: text,
                                                         audioSeconds: 6, language: nil, appBundleID: nil,
                                                         delivery: .inserted), recording: nil)
        }
        let model = harness.model()
        await model.history.refresh()
        let week = model.history.lastSevenDays
        #expect(week.dictations == 2)
        #expect(week.words == 5)
        #expect(week.seconds == 12)
        #expect(week.wordsPerMinute == 25)
        #expect(model.history.stats().dictations == 3)
    }
}
#endif
