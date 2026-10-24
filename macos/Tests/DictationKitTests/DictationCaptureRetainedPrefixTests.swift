import XCTest
@testable import DictationKit

private final class RetainedPrefixCollector: @unchecked Sendable {
    private let lock = NSLock()
    private var chunks: [[Float]] = []

    func append(_ chunk: [Float]) {
        lock.lock()
        chunks.append(chunk)
        lock.unlock()
    }

    var snapshot: [[Float]] {
        lock.lock()
        defer { lock.unlock() }
        return chunks
    }
}

/// The retained-audio callback is the common input to preview and the Mac
/// recovery spool. Trigger-down must publish the warm pre-roll exactly once,
/// before it publishes chunks that arrive after the press.
final class DictationCaptureRetainedPrefixTests: XCTestCase {
    func testWarmPreRollIsPublishedOnceBeforePostTriggerAudio() async {
        let capture = DictationCapture(preRollSeconds: 0.1)
        let collector = RetainedPrefixCollector()
        capture.onRetainedAudio = { samples, _ in collector.append(samples) }
        capture.markWarmForTesting()

        let prefix = [Float](repeating: 0.1, count: 1_600)
        let suffix: [Float] = [0.2, 0.3, 0.4]
        capture.appendChunkForTesting(prefix)
        capture.armTriggerDown()
        capture.appendChunkForTesting(suffix)
        let result = await capture.stop(keepWarm: true)

        XCTAssertEqual(collector.snapshot, [prefix, suffix])
        XCTAssertEqual(result.samples, prefix + suffix)
    }
}
