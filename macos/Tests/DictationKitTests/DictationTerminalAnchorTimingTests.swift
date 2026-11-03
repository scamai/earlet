import XCTest
@testable import DictationKit

final class DictationTerminalAnchorTimingTests: XCTestCase {
    private final class ReadBox: @unchecked Sendable {
        let lock = NSLock()
        var firstRead: UInt64?
        var count = 0
        var elapsedNanoseconds: UInt64 = 0

        func read(before: DictationAnchorSearch.FieldRead,
                  after: DictationAnchorSearch.FieldRead) -> DictationAnchorSearch.FieldRead {
            lock.lock()
            defer { lock.unlock() }
            count += 1
            let now = DispatchTime.now().uptimeNanoseconds
            if let firstRead {
                elapsedNanoseconds = now - firstRead
            } else {
                self.firstRead = now
            }
            return elapsedNanoseconds >= UInt64(DictationWittyInput.pollSeconds * 1_000_000_000)
                ? after : before
        }
    }

    func testWittyRetriesWaitForItsAXSnapshotCacheBeforeGivingUp() {
        let queue = DispatchQueue(label: "test.terminal-anchor-cache")
        let before = DictationAnchorSearch.FieldRead(value: "draft", selectionLocation: nil,
                                                     selectionLength: nil)
        let after = DictationAnchorSearch.FieldRead(value: "draftnew ", selectionLocation: nil,
                                                    selectionLength: nil)
        let reads = ReadBox()
        let done = expectation(description: "anchor after terminal cache expires")

        DictationCorrectionObserver.searchAnchor(
            pasted: "new ", beforePaste: before, config: .init(),
            bundleID: DictationWittyInput.bundleID, queue: queue,
            read: {
                reads.read(before: before, after: after)
            },
            isAdmitted: { true }, isCurrent: { true },
            diagnostic: { report in
                XCTAssertEqual(report.result, .anchoredFromChange)
                XCTAssertEqual(report.attempts, 2)
            },
            completion: { result in
                guard case .anchored = result else {
                    XCTFail("expected the delayed terminal value to anchor: \(result)")
                    done.fulfill()
                    return
                }
                done.fulfill()
            })

        wait(for: [done], timeout: 5)
        XCTAssertEqual(reads.count, 2)
        XCTAssertGreaterThanOrEqual(
            reads.elapsedNanoseconds,
            UInt64(DictationWittyInput.pollSeconds * 1_000_000_000))
    }

    func testProductionSearchRefusesReadsAfterAdmissionIsLost() {
        let done = expectation(description: "refused terminal anchor")
        DictationCorrectionObserver.searchAnchor(
            pasted: "new ", beforePaste: .init(value: "draft", selectionLocation: nil, selectionLength: nil),
            config: .init(anchorAttempts: 1), bundleID: DictationWittyInput.bundleID,
            queue: DispatchQueue(label: "test.refused-anchor"),
            read: { XCTFail("The search must not read a field after focus/security admission is lost"); return nil },
            isAdmitted: { false }, isCurrent: { true }, diagnostic: { _ in },
            completion: { result in XCTAssertEqual(result, .notFound); done.fulfill() })
        wait(for: [done], timeout: 5)
    }

    func testLostAdmissionDoesNotReadAndMidReadLossDiscardsValue() {
        var reads = 0
        let blocked: String? = DictationAnchorSearch.admittedRead(admit: { false }, read: {
            reads += 1
            return "synthetic field"
        })
        XCTAssertNil(blocked)
        XCTAssertEqual(reads, 0, "A focus or Secure Input refusal must precede AXValue")

        var admitted = true
        let changed: String? = DictationAnchorSearch.admittedRead(admit: { admitted }, read: {
            reads += 1
            admitted = false
            return "synthetic field"
        })
        XCTAssertNil(changed, "A value read across an admission change must not reach the anchor")
        XCTAssertEqual(reads, 1)
        let stable: String? = DictationAnchorSearch.admittedRead(admit: { true }, read: { "synthetic field" })
        XCTAssertEqual(stable, "synthetic field")
    }

    func testOnlyWittyUsesTheTerminalCacheFloorAndLongerOverridesWin() {
        XCTAssertEqual(
            DictationCorrectionObserver.anchorRetrySeconds(
                configured: 0.1, bundleID: DictationWittyInput.bundleID),
            DictationWittyInput.pollSeconds)
        XCTAssertEqual(
            DictationCorrectionObserver.anchorRetrySeconds(
                configured: 0.8, bundleID: DictationWittyInput.bundleID),
            0.8)
        XCTAssertEqual(
            DictationCorrectionObserver.anchorRetrySeconds(
                configured: 0.1, bundleID: "com.example.editor"),
            0.1)
    }
}
