import Foundation
import Testing
@testable import DictationAppCore

@Suite("Dictation take spool")
struct DictationTakeSpoolTests {
    private func scratch() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("dictation-spool-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    private func synchronousSpool(_ directory: URL) -> DictationTakeSpool {
        DictationTakeSpool(directory: directory, runFileWork: { work in work() })
    }

    private func audio(_ spool: DictationTakeSpool, _ take: DictationTake,
                       _ samples: [Float], rate: Int = DictationTakeSpool.sampleRate) {
        spool.append(take, samples: samples, sampleRate: rate)
    }

    private func bytes(_ url: URL) throws -> Data {
        try Data(contentsOf: url)
    }

    @Test("first launch with no recovery folder offers nothing")
    func firstLaunchAbsent() throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let missing = root.appendingPathComponent("recovery")
        let spool = synchronousSpool(missing)
        #expect(spool.atLaunch(now: Date(), keptFor: 7 * 24 * 60 * 60) == nil)
        #expect(!FileManager.default.fileExists(atPath: missing.path))
    }

    @Test("PCM round trip handles nonfinite and clamped samples")
    func pcmRoundTrip() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date(timeIntervalSince1970: 1_700_000_000))
        let input: [Float] = [.nan, .infinity, -.infinity, 2, -2, 0.5, -0.5]
        audio(spool, take, input)
        #expect(await spool.keep(take))
        let recovered = await spool.readKept()
        let expected = input.map { Float(DictationTakeSpool.quantise($0)) / 32768 }
        #expect(recovered.unavailable == false)
        #expect(recovered.samples?.count == expected.count)
        if let samples = recovered.samples {
            for (actual, wanted) in zip(samples, expected) {
                #expect(abs(actual - wanted) < 0.00001)
            }
        }
    }

    @Test("discard closes and removes the current take")
    func discardRemovesCurrentFile() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date())
        audio(spool, take, [0.1, 0.2])
        let path = take.path
        await spool.discard(take)
        #expect(!FileManager.default.fileExists(atPath: path.path))
    }

    @Test("failed unlink erases cancelled audio and retries without offering recovery")
    func cancelledTakeCannotReappear() async throws {
        let root = try scratch()
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
            try? FileManager.default.removeItem(at: root)
        }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date())
        audio(spool, take, [0.1, 0.2])
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: root.path)
        #expect(await !spool.discard(take))
        #expect(try Data(contentsOf: take.path).isEmpty)
        // Late audio cannot recreate cancelled contents.
        audio(spool, take, [0.3])
        #expect(try Data(contentsOf: take.path).isEmpty)
        #expect(synchronousSpool(root).atLaunch(now: Date(), keptFor: 60) == nil)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
        #expect(await spool.retryDiscards())
        #expect(!FileManager.default.fileExists(atPath: take.path.path))
    }

    @Test("launch keeps the newest interrupted take and removes the older one")
    func launchChoosesNewestAndMarksReplacement() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let older = spool.begin(startedAt: Date(timeIntervalSince1970: 1_700_000_000))
        audio(spool, older, [0.1])
        await spool.leave(older)
        let newer = spool.begin(startedAt: Date(timeIntervalSince1970: 1_700_000_100))
        audio(spool, newer, [0.2, 0.3])
        await spool.leave(newer)

        let recovered = DictationTakeSpool(directory: root, runFileWork: { work in work() })
            .atLaunch(now: Date(timeIntervalSince1970: 1_700_000_200), keptFor: 7 * 24 * 60 * 60)
        #expect(recovered?.interrupted == true)
        #expect(recovered?.replacedEarlier == true)
        #expect(recovered?.length == 2.0 / Double(DictationTakeSpool.sampleRate))
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent(DictationTakeSpool.keptFileName).path))
        #expect(!FileManager.default.fileExists(atPath: older.path.path))
        #expect(!FileManager.default.fileExists(atPath: newer.path.path))
    }

    @Test("a left take is recoverable by a new spool instance")
    func leaveAndNewInstanceRecover() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date(timeIntervalSince1970: 1_700_001_000))
        audio(spool, take, [0.25, 0.5, 0.75])
        await spool.leave(take)
        let found = synchronousSpool(root).atLaunch(
            now: Date(timeIntervalSince1970: 1_700_001_001), keptFor: 7 * 24 * 60 * 60)
        #expect(found?.interrupted == true)
        #expect(found?.length == 3.0 / Double(DictationTakeSpool.sampleRate))
    }

    @Test("takes older than seven days expire at launch")
    func expiry() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date(timeIntervalSince1970: 1_700_000_000))
        audio(spool, take, [0.1])
        await spool.leave(take)
        let now = Date(timeIntervalSince1970: 1_700_000_000 + 7 * 24 * 60 * 60 + 1)
        #expect(synchronousSpool(root).atLaunch(now: now, keptFor: 7 * 24 * 60 * 60) == nil)
        #expect((try? FileManager.default.contentsOfDirectory(atPath: root.path))?.isEmpty == true)
    }

    @Test("malformed, truncated, and odd-byte files are safe")
    func malformedFilesSafe() throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let malformed = root.appendingPathComponent("take-malformed.pcm")
        try Data([1, 2, 3]).write(to: malformed)
        let truncated = root.appendingPathComponent("take-truncated.pcm")
        try Data(repeating: 0, count: DictationTakeSpool.headerBytes - 1).write(to: truncated)
        let odd = root.appendingPathComponent("take-odd.pcm")
        var oddData = DictationTakeSpool.header(startedAt: Date(timeIntervalSince1970: 1_700_000_000))
        oddData.append(1)
        try oddData.write(to: odd)

        let found = synchronousSpool(root).atLaunch(
            now: Date(timeIntervalSince1970: 1_700_000_001), keptFor: 7 * 24 * 60 * 60)
        #expect(found == nil)
        #expect(!FileManager.default.fileExists(atPath: malformed.path))
        #expect(!FileManager.default.fileExists(atPath: truncated.path))
        #expect(!FileManager.default.fileExists(atPath: odd.path))
    }

    @Test("wrong sample rate marks a take incomplete")
    func wrongRateMarksFailed() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date())
        audio(spool, take, [0.1, 0.2], rate: 8_000)
        #expect(take.failed)
        #expect(take.dropped == 1)
        #expect(await spool.keep(take) == false)
        #expect(!FileManager.default.fileExists(atPath: take.path.path))
    }

    @Test("a failed asynchronous write cannot be offered as complete")
    func asyncWriteFailureCannotOffer() async throws {
        let root = try scratch()
        let unusableDirectory = root.appendingPathComponent("not-a-directory")
        try Data([1]).write(to: unusableDirectory)
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(unusableDirectory)
        let take = spool.begin(startedAt: Date())
        audio(spool, take, [0.1, 0.2])
        #expect(take.failed)
        #expect(await spool.keep(take) == false)
        #expect(!FileManager.default.fileExists(atPath: take.path.path))
    }

    @Test("keeping a second take replaces the older kept audio")
    func keepReplacesOlder() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let first = spool.begin(startedAt: Date())
        audio(spool, first, [0.1])
        #expect(await spool.keep(first))
        let second = spool.begin(startedAt: Date().addingTimeInterval(1))
        audio(spool, second, [0.9, 0.8])
        #expect(await spool.keep(second))
        let recovered = await spool.readKept()
        #expect(recovered.samples?.count == 2)
        #expect(!FileManager.default.fileExists(atPath: first.path.path))
    }

    @Test("spool creates private directory and file permissions")
    func privatePermissions() throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let nested = root.appendingPathComponent("recovery")
        let take = synchronousSpool(nested).begin(startedAt: Date())
        let directoryMode = try FileManager.default.attributesOfItem(atPath: nested.path)[.posixPermissions] as? NSNumber
        let fileMode = try FileManager.default.attributesOfItem(atPath: take.path.path)[.posixPermissions] as? NSNumber
        #expect(directoryMode?.intValue == 0o700)
        #expect(fileMode?.intValue == 0o600)
    }

    @Test("temporarily unavailable kept audio stays present")
    func unavailableReadPreservesFile() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date(timeIntervalSince1970: 1_700_000_000))
        audio(spool, take, [0.1, 0.2])
        #expect(await spool.keep(take))
        let kept = spool.keptPath
        try FileManager.default.removeItem(at: kept)
        try FileManager.default.createDirectory(at: kept, withIntermediateDirectories: false)
        let read = await spool.readKept()
        #expect(read.unavailable)
        #expect(FileManager.default.fileExists(atPath: kept.path))
    }

    @Test("failed replacement leaves the prior valid kept take intact")
    func failedReplacementPreservesPrior() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let first = spool.begin(startedAt: Date())
        audio(spool, first, [0.11, 0.22])
        #expect(await spool.keep(first))
        let prior = try bytes(spool.keptPath)

        let second = spool.begin(startedAt: Date())
        audio(spool, second, [0.99])
        try FileManager.default.removeItem(at: second.path)
        #expect(await spool.keep(second) == false)
        #expect(try bytes(spool.keptPath) == prior)
    }

    @Test("an unreadable existing kept take survives a later keep")
    func unreadableKeptSurvivesReplacement() async throws {
        let root = try scratch()
        defer { try? FileManager.default.removeItem(at: root) }
        let spool = synchronousSpool(root)
        let first = spool.begin(startedAt: Date())
        audio(spool, first, [0.11, 0.22])
        #expect(await spool.keep(first))
        let prior = try bytes(spool.keptPath)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: spool.keptPath.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: spool.keptPath.path) }

        let second = spool.begin(startedAt: Date().addingTimeInterval(1))
        audio(spool, second, [0.99, 0.88])
        #expect(await spool.keep(second))
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: spool.keptPath.path)
        #expect(try bytes(spool.keptPath) == prior)
    }

    @Test("discarding a kept take reports failure when its folder is read-only")
    func discardKeptReadOnlyFolderPreservesFile() async throws {
        let root = try scratch()
        defer {
            try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
            try? FileManager.default.removeItem(at: root)
        }
        let spool = synchronousSpool(root)
        let take = spool.begin(startedAt: Date())
        audio(spool, take, [0.1, 0.2])
        #expect(await spool.keep(take))
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: root.path)

        #expect(await spool.discardKept() == false)
        #expect(FileManager.default.fileExists(atPath: spool.keptPath.path))
    }
}
