// The dictation history's promises, observed where they are kept: on the
// disk. Every store here lives in a scratch folder with its key in memory; no
// test reaches the login keychain or the user's history.

#if canImport(Testing)
import CryptoKit
import Foundation
import Testing
@testable import DictationHistory

/// A scratch folder that removes itself.
final class ScratchFolder {
    let url: URL
    init() {
        url = FileManager.default.temporaryDirectory
            .appendingPathComponent("dictation-history-\(UUID().uuidString)", isDirectory: true)
    }
    deinit { try? FileManager.default.removeItem(at: url) }

    /// Every regular file under the folder, with its bytes.
    func files() -> [(URL, Data)] {
        guard let walk = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.isRegularFileKey]) else {
            return []
        }
        var found: [(URL, Data)] = []
        for case let file as URL in walk {
            let regular = (try? file.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) ?? false
            if regular, let data = try? Data(contentsOf: file) { found.append((file, data)) }
        }
        return found
    }
}

private func entry(_ text: String, at date: Date = Date(), seconds: Double = 4, id: UUID = UUID()) -> DictationHistoryEntry {
    DictationHistoryEntry(id: id, recordedAt: date, text: text, audioSeconds: seconds,
                          language: "en", appBundleID: "com.apple.TextEdit", delivery: .inserted)
}

/// PCM16 that spells a marker, so a test can look for it on the disk.
private func pcm(_ marker: String) -> Data {
    Data(repeating: 0x11, count: 512) + Data(marker.utf8) + Data(repeating: 0x22, count: 512)
}

private func permissions(_ url: URL) -> Int {
    ((try? FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions]) as? NSNumber)?.intValue ?? -1
}

private extension Data {
    func contains(_ other: Data) -> Bool { range(of: other) != nil }
}

@Suite("The sealed file format")
struct HistorySealTests {
    let key = SymmetricKey(size: .bits256)
    let id = UUID()

    @Test("a sealed file opens back to its plaintext, and never carries it in the clear")
    func roundTrip() throws {
        let plain = Data("We meet at the north office on Friday.".utf8)
        let sealed = try DictationHistorySeal.seal(plain, kind: .entry, id: id, key: key)
        #expect(!sealed.contains(plain))
        #expect(sealed.prefix(8) == Data("DICTHIST".utf8))
        #expect(try DictationHistorySeal.open(sealed, kind: .entry, id: id, key: key) == plain)
    }

    @Test("two seals of the same words differ: every seal has its own nonce")
    func freshNonce() throws {
        let plain = Data("same words".utf8)
        let first = try DictationHistorySeal.seal(plain, kind: .entry, id: id, key: key)
        let second = try DictationHistorySeal.seal(plain, kind: .entry, id: id, key: key)
        #expect(first != second)
        #expect(first.subdata(in: 26..<38) != second.subdata(in: 26..<38))
    }

    @Test("a changed byte anywhere in the ciphertext or tag fails the tag")
    func tamperedCiphertext() throws {
        let sealed = try DictationHistorySeal.seal(Data("words".utf8), kind: .entry, id: id, key: key)
        for offset in [26, 38, sealed.count / 2, sealed.count - 1] {
            var changed = sealed
            changed[offset] ^= 0x01
            #expect(throws: DictationHistorySeal.OpenFailure.damaged) {
                try DictationHistorySeal.open(changed, kind: .entry, id: id, key: key)
            }
        }
    }

    @Test("a file cannot be passed off as another dictation's, another kind or another key's")
    func boundToItsDictation() throws {
        let sealed = try DictationHistorySeal.seal(Data("words".utf8), kind: .entry, id: id, key: key)
        #expect(throws: DictationHistorySeal.OpenFailure.damaged) {
            try DictationHistorySeal.open(sealed, kind: .entry, id: UUID(), key: key)
        }
        #expect(throws: DictationHistorySeal.OpenFailure.mismatched) {
            try DictationHistorySeal.open(sealed, kind: .audio, id: id, key: key)
        }
        #expect(throws: DictationHistorySeal.OpenFailure.otherKey) {
            try DictationHistorySeal.open(sealed, kind: .entry, id: id, key: SymmetricKey(size: .bits256))
        }
        // The header is authenticated too: claiming another kind in it does
        // not get the file opened as that kind.
        var relabelled = sealed
        relabelled[9] = DictationHistorySeal.Kind.audio.rawValue
        #expect(throws: DictationHistorySeal.OpenFailure.damaged) {
            try DictationHistorySeal.open(relabelled, kind: .audio, id: id, key: key)
        }
    }

    @Test("bytes that are not a sealed history file are refused as such")
    func notSealed() {
        #expect(throws: DictationHistorySeal.OpenFailure.notSealed) {
            try DictationHistorySeal.open(Data("plain text, not sealed at all".utf8), kind: .entry, id: id, key: key)
        }
        #expect(throws: DictationHistorySeal.OpenFailure.notSealed) {
            try DictationHistorySeal.open(Data(), kind: .entry, id: id, key: key)
        }
    }

    @Test("the key id names the key without containing it")
    func keyID() {
        let identifier = DictationHistorySeal.keyID(of: key)
        #expect(identifier.count == 16)
        #expect(identifier == DictationHistorySeal.keyID(of: key))
        #expect(identifier != DictationHistorySeal.keyID(of: SymmetricKey(size: .bits256)))
        let bytes = key.withUnsafeBytes { Data($0) }
        #expect(!bytes.contains(identifier) && !identifier.contains(bytes.prefix(8)))
    }
}

@Suite("The history store")
struct HistoryStoreTests {
    @Test("a saved dictation and its recording read back in a new store, newest first")
    func persists() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        let older = entry("first words", at: Date(timeIntervalSince1970: 1_800_000_000))
        let newer = entry("second words", at: Date(timeIntervalSince1970: 1_800_000_100))
        try await store.append(older, recording: pcm("FIRST-RECORDING"))
        try await store.append(newer, recording: nil)

        let reopened = DictationHistoryStore(directory: folder.url, keys: keys)
        let snapshot = try await reopened.load()
        #expect(snapshot.entries == [newer, older])
        #expect(snapshot.recordings == [older.id])
        #expect(snapshot.recordingBytes > 0)
        #expect(snapshot.unreadable == 0)
        #expect(try await reopened.recording(older.id) == pcm("FIRST-RECORDING"))
        #expect(try await reopened.recording(newer.id) == nil)
        #expect(keys.created == 1, "one key, made at the first save")
    }

    @Test("a dictation reads back exactly as it was saved, to the last bit of its time")
    func exactRoundTrip() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        // Times with every kind of fraction a clock gives; seconds since 1970
        // in the file lost the last bits of some of them.
        let saved = (0..<64).map { index in
            entry("words \(index)", at: Date(timeIntervalSinceReferenceDate: 782_000_000 + Double(index) * 1.000_123_457))
        }
        for item in saved { try await store.append(item, recording: nil) }
        let read = try await DictationHistoryStore(directory: folder.url, keys: keys).load()
        #expect(Set(read.entries.map(\.recordedAt)) == Set(saved.map(\.recordedAt)))
        #expect(read.entries.sorted { $0.recordedAt < $1.recordedAt } == saved)
    }

    @Test("nothing on the disk is the words, the recording or the key, and only its owner may read it")
    func sealedAndPrivateOnDisk() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        let words = "Brivioo quarterly forecast for Saoirse"
        try await store.append(entry(words), recording: pcm("RECORDED-VOICE-MARKER"))

        let files = folder.files()
        #expect(files.count == 2)
        let key = try #require(keys.current).withUnsafeBytes { Data($0) }
        for (file, data) in files {
            #expect(!data.contains(Data(words.utf8)), "\(file.lastPathComponent) holds the words")
            #expect(!data.contains(Data("Brivioo".utf8)), "\(file.lastPathComponent) holds a word")
            #expect(!data.contains(Data("RECORDED-VOICE-MARKER".utf8)), "\(file.lastPathComponent) holds the audio")
            #expect(!data.contains(key), "\(file.lastPathComponent) holds the key")
            #expect(!data.contains(Data("com.apple.TextEdit".utf8)), "\(file.lastPathComponent) names the app")
            #expect(permissions(file) == 0o600, "\(file.lastPathComponent)")
        }
        for folderURL in [folder.url, folder.url.appendingPathComponent("entries"), folder.url.appendingPathComponent("audio")] {
            #expect(permissions(folderURL) == 0o700, "\(folderURL.lastPathComponent)")
        }
    }

    @Test("reading an empty history makes no key; the first save does")
    func keyMadeOnFirstSave() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        #expect(try await store.load() == .init())
        #expect(keys.created == 0)
        #expect(!FileManager.default.fileExists(atPath: folder.url.path))
        try await store.append(entry("words"), recording: nil)
        #expect(keys.created == 1)
    }

    @Test("a refused keychain reads as locked and saves nothing, never in the clear")
    func lockedSavesNothing() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        keys.refuse(.unavailable(-25293))
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        await #expect(throws: DictationHistoryStore.Failure.locked(.unavailable(-25293))) {
            try await store.load()
        }
        await #expect(throws: DictationHistoryStore.Failure.locked(.unavailable(-25293))) {
            try await store.append(entry("secret words"), recording: pcm("VOICE"))
        }
        #expect(folder.files().isEmpty)
        // Try Again, once the keychain answers.
        keys.refuse(nil)
        await store.reload()
        try await store.append(entry("later words"), recording: nil)
        #expect(try await store.load().entries.map(\.text) == ["later words"])
    }

    @Test("a keychain that refused is not asked again until Try Again")
    func refusalIsKept() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        keys.refuse(.unavailable(-128))
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        await #expect(throws: DictationHistoryStore.Failure.locked(.unavailable(-128))) { try await store.load() }
        let asked = keys.calls
        // The saves queued behind the prompt the user denied, and a read.
        for words in ["one", "two", "three"] {
            await #expect(throws: DictationHistoryStore.Failure.locked(.unavailable(-128))) {
                try await store.append(entry(words), recording: nil)
            }
        }
        await #expect(throws: DictationHistoryStore.Failure.locked(.unavailable(-128))) { try await store.load() }
        #expect(keys.calls == asked, "the keychain was asked again after it refused")
        #expect(folder.files().isEmpty)
        keys.refuse(nil)
        await store.reload()
        try await store.append(entry("after Try Again"), recording: nil)
        #expect(keys.calls > asked)
        #expect(try await store.load().entries.map(\.text) == ["after Try Again"])
    }

    @Test("a dictation a newer version wrote is kept with its recording, never offered for deletion")
    func newerVersionKept() async throws {
        let folder = ScratchFolder()
        let key = SymmetricKey(size: .bits256)
        let keys = MemoryHistoryKeys(key: key)
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        try await store.append(entry("today's words"), recording: nil)
        // What a newer version might write: a delivery this one does not know.
        let id = UUID()
        let future = Data(#"{"id":"\#(id.uuidString)","recordedAt":800000000,"text":"later words","audioSeconds":2,"delivery":"spokenToTheFuture"}"#.utf8)
        let entryFile = folder.url.appendingPathComponent("entries/\(id.uuidString).sealed")
        let audioFile = folder.url.appendingPathComponent("audio/\(id.uuidString).sealed")
        try DictationHistorySeal.seal(future, kind: .entry, id: id, key: key).write(to: entryFile)
        try DictationHistorySeal.seal(pcm("NEWER-VOICE"), kind: .audio, id: id, key: key).write(to: audioFile)

        let reopened = DictationHistoryStore(directory: folder.url, keys: keys)
        let snapshot = try await reopened.load()
        #expect(snapshot.entries.map(\.text) == ["today's words"])
        #expect(snapshot.unreadable == 0, "a dictation this key opens is counted as unreadable")
        #expect(snapshot.newer == 1)
        #expect(snapshot.recordings.isEmpty)
        try await reopened.deleteUnreadable()
        #expect(FileManager.default.fileExists(atPath: entryFile.path))
        #expect(FileManager.default.fileExists(atPath: audioFile.path), "its recording was swept as an orphan")
        #expect(try await DictationHistoryStore(directory: folder.url, keys: keys).load().newer == 1)
    }

    @Test("a dictation whose recording cannot be deleted still leaves the list")
    func deleteListsNothingGone() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        let saved = entry("delete me")
        try await store.append(saved, recording: pcm("VOICE"))
        let audio = folder.url.appendingPathComponent("audio")
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: audio.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: audio.path) }
        await #expect(throws: (any Error).self) { try await store.delete(saved.id) }
        #expect(try await store.load().entries.isEmpty, "the list still names a dictation whose file is gone")
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: audio.path)
        let reopened = try await DictationHistoryStore(directory: folder.url, keys: keys).load()
        #expect(reopened.entries.isEmpty)
        #expect(folder.files().allSatisfy { !$0.0.path.contains("/audio/") }, "the recording left behind is swept at the next load")
    }

    @Test("a retention pass that fails part-way lists nothing it deleted")
    func pruneFailureRereads() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        try await store.append(entry("old words", at: Date(timeIntervalSinceNow: -40 * 86_400)), recording: pcm("OLD-VOICE"))
        try await store.append(entry("new words"), recording: nil)
        let audio = folder.url.appendingPathComponent("audio")
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: audio.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: audio.path) }
        await #expect(throws: (any Error).self) {
            try await store.prune(.init(dictations: 30 * 86_400, recordings: nil, recordingBytes: 1 << 30), now: Date())
        }
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: audio.path)
        #expect(try await store.load().entries.map(\.text) == ["new words"],
                "the list still names a dictation whose file the pass deleted")
    }

    @Test("files sealed under a lost key are counted unreadable, never shown, and can be deleted")
    func lostKey() async throws {
        let folder = ScratchFolder()
        let first = DictationHistoryStore(directory: folder.url, keys: MemoryHistoryKeys())
        try await first.append(entry("under the old key"), recording: pcm("OLD"))
        try await first.append(entry("also old"), recording: nil)

        let keys = MemoryHistoryKeys()
        let second = DictationHistoryStore(directory: folder.url, keys: keys)
        let lost = try await second.load()
        #expect(lost.entries.isEmpty)
        #expect(lost.unreadable == 2)
        // A new key for new dictations; the old files stay unreadable.
        try await second.append(entry("under the new key"), recording: nil)
        let mixed = try await second.load()
        #expect(mixed.entries.map(\.text) == ["under the new key"])
        #expect(mixed.unreadable == 2)
        try await second.deleteUnreadable()
        #expect(folder.files().count == 1, "only the new dictation is left; the old recording went with its file")
        #expect(try await DictationHistoryStore(directory: folder.url, keys: keys).load().unreadable == 0)
    }

    @Test("a damaged file costs only itself")
    func damagedFile() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        let kept = entry("kept words")
        let broken = entry("broken words")
        try await store.append(kept, recording: nil)
        try await store.append(broken, recording: nil)
        let file = folder.url.appendingPathComponent("entries/\(broken.id.uuidString).sealed")
        var bytes = try Data(contentsOf: file)
        bytes[bytes.count - 3] ^= 0xFF
        try bytes.write(to: file)

        let snapshot = try await DictationHistoryStore(directory: folder.url, keys: keys).load()
        #expect(snapshot.entries == [kept])
        #expect(snapshot.unreadable == 1)
    }

    @Test("a file that cannot be read just now is neither counted nor deleted")
    func unreadableJustNow() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        let shut = entry("shut words")
        try await store.append(shut, recording: nil)
        let file = folder.url.appendingPathComponent("entries/\(shut.id.uuidString).sealed")
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: file.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path) }

        let reopened = DictationHistoryStore(directory: folder.url, keys: keys)
        let snapshot = try await reopened.load()
        #expect(snapshot.entries.isEmpty)
        #expect(snapshot.unreadable == 0)
        try await reopened.deleteUnreadable()
        #expect(FileManager.default.fileExists(atPath: file.path))
    }

    @Test("deleting a dictation deletes its recording")
    func deleteOne() async throws {
        let folder = ScratchFolder()
        let store = DictationHistoryStore(directory: folder.url, keys: MemoryHistoryKeys())
        let gone = entry("delete me")
        let stays = entry("keep me")
        try await store.append(gone, recording: pcm("GONE"))
        try await store.append(stays, recording: pcm("STAYS"))
        let snapshot = try await store.delete(gone.id)
        #expect(snapshot.entries == [stays])
        #expect(snapshot.recordings == [stays.id])
        #expect(folder.files().count == 2)
        #expect(!FileManager.default.fileExists(atPath: folder.url.appendingPathComponent("audio/\(gone.id.uuidString).sealed").path))
    }

    @Test("Delete All destroys the key first, so a copy of the old files opens nowhere")
    func deleteAllShredsTheKey() async throws {
        let folder = ScratchFolder()
        let copy = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        try await store.append(entry("words before"), recording: pcm("VOICE"))
        try FileManager.default.copyItem(at: folder.url, to: copy.url)

        try await store.deleteAll()
        #expect(keys.current == nil)
        #expect(folder.files().isEmpty)
        #expect(try await store.load() == .init())
        // The copy is all that is left, and no key opens it.
        let restored = try await DictationHistoryStore(directory: copy.url, keys: keys).load()
        #expect(restored.entries.isEmpty)
        #expect(restored.unreadable == 1)
        // The next save makes a new key.
        try await store.append(entry("words after"), recording: nil)
        #expect(keys.created == 2)
    }

    @Test("a Delete All the keychain refuses deletes nothing and says so")
    func deleteAllRefused() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        try await store.append(entry("words"), recording: nil)
        keys.refuse(.unavailable(-128))
        await #expect(throws: DictationHistoryStore.Failure.locked(.unavailable(-128))) {
            try await store.deleteAll()
        }
        keys.refuse(nil)
        #expect(folder.files().count == 1)
    }

    @Test("retention: old dictations go with their recordings, old recordings alone, then the oldest past the size limit")
    func prune() async throws {
        let folder = ScratchFolder()
        let store = DictationHistoryStore(directory: folder.url, keys: MemoryHistoryKeys())
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let day: TimeInterval = 24 * 60 * 60
        let ancient = entry("forty days ago", at: now.addingTimeInterval(-40 * day))
        let old = entry("ten days ago", at: now.addingTimeInterval(-10 * day))
        let recent = entry("two days ago", at: now.addingTimeInterval(-2 * day))
        let today = entry("today", at: now.addingTimeInterval(-60))
        for item in [ancient, old, recent, today] { try await store.append(item, recording: pcm(item.text)) }

        let first = try await store.prune(.init(dictations: 30 * day, recordings: 7 * day, recordingBytes: .max), now: now)
        #expect(first == .init(dictations: 1, recordings: 2))
        var snapshot = try await store.load()
        #expect(snapshot.entries.map(\.text) == ["today", "two days ago", "ten days ago"])
        #expect(snapshot.recordings == [recent.id, today.id])

        // One recording's worth of room: the older of the two goes.
        let size = snapshot.recordingBytes / 2
        let second = try await store.prune(.init(dictations: nil, recordings: nil, recordingBytes: size), now: now)
        #expect(second == .init(dictations: 0, recordings: 1))
        snapshot = try await store.load()
        #expect(snapshot.recordings == [today.id])

        // Recordings kept for zero days: none is kept, every dictation stays.
        try await store.prune(.init(dictations: nil, recordings: 0, recordingBytes: .max), now: now)
        snapshot = try await DictationHistoryStore(directory: folder.url, keys: MemoryHistoryKeys()).load()
        #expect(try await store.load().recordings.isEmpty)
        #expect(try await store.load().entries.count == 3)
        #expect(snapshot.unreadable == 3, "a store with another key sees the three files and opens none")
    }

    @Test("a recording without its dictation, and a write that died before its rename, are cleared at load")
    func leftovers() async throws {
        let folder = ScratchFolder()
        let keys = MemoryHistoryKeys()
        let store = DictationHistoryStore(directory: folder.url, keys: keys)
        try await store.append(entry("words"), recording: nil)
        let orphan = folder.url.appendingPathComponent("audio/\(UUID().uuidString).sealed")
        let unfinished = folder.url.appendingPathComponent("entries/.\(UUID().uuidString).tmp")
        try Data("x".utf8).write(to: orphan)
        try Data("x".utf8).write(to: unfinished)

        let snapshot = try await DictationHistoryStore(directory: folder.url, keys: keys).load()
        #expect(snapshot.entries.count == 1)
        #expect(!FileManager.default.fileExists(atPath: orphan.path))
        #expect(!FileManager.default.fileExists(atPath: unfinished.path))
    }
}

@Suite("Counting words")
struct WordCountTests {
    @Test("words, contractions and numbers in English", arguments: [
        ("Hello world", 2), ("Don't stop.", 2), ("It costs 3.6 dollars", 4), ("well-known", 2),
        ("  , . !  ", 0), ("", 0), ("U.S.A. today", 2), ("café au lait", 3),
    ])
    func latin(text: String, expected: Int) {
        #expect(DictationWordCount.count(text) == expected)
    }

    @Test("each Han, kana or Hangul character is a word; Latin words among them count as words")
    func cjk() {
        #expect(DictationWordCount.count("明天下午") == 4)
        #expect(DictationWordCount.count("明天下午 review 第一版。") == 8)
        #expect(DictationWordCount.count("ひらがなカタカナ") == 8)
        #expect(DictationWordCount.count("안녕하세요") == 5)
    }

    @Test("pace is measured, and withheld below ten seconds of speech")
    func pace() {
        let short = DictationHistoryStats([entry("one two three", seconds: 3)])
        #expect(short.wordsPerMinute == nil)
        let long = DictationHistoryStats([entry(String(repeating: "word ", count: 30), seconds: 12)])
        #expect(long.dictations == 1 && long.words == 30)
        #expect(long.wordsPerMinute == 150)
    }
}
#endif

#if canImport(Testing) && os(macOS)
import Security

/// The product's key store against a keychain file of its own, made and
/// deleted by the test: the real SecItem calls, never the login keychain.
@Suite("The keychain key store")
struct KeychainKeysTests {
    private func bytes(_ key: SymmetricKey?) -> Data? { key?.withUnsafeBytes { Data($0) } }

    @Test("a key is made once, read back, kept on a second create, and destroyed")
    func keychainRoundTrip() throws {
        let folder = ScratchFolder()
        try FileManager.default.createDirectory(at: folder.url, withIntermediateDirectories: true)
        let path = folder.url.appendingPathComponent("history-test.keychain-db").path
        // The throwaway keychain's own lock, not a credential of anyone's.
        let unlock = "throwaway"
        var made: SecKeychain?
        let status = unlock.withCString { pointer in
            SecKeychainCreate(path, UInt32(strlen(pointer)), pointer, false, nil, &made)
        }
        let keychain = try #require(status == errSecSuccess ? made : nil, "SecKeychainCreate: \(status)")
        defer { SecKeychainDelete(keychain) }

        let keys = KeychainHistoryKeys(service: "dictation.history.test.\(UUID().uuidString)",
                                       account: KeychainHistoryKeys.account, keychain: keychain)
        #expect(try keys.existing() == nil)
        let key = try keys.create()
        #expect(bytes(key)?.count == 32)
        #expect(bytes(try keys.existing()) == bytes(key))
        #expect(bytes(try keys.create()) == bytes(key), "a second create returns the key already there")
        try keys.destroy()
        #expect(try keys.existing() == nil)
        try keys.destroy()
    }
}
#endif
