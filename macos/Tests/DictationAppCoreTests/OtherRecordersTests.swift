import CoreAudio
import Darwin
import XCTest
@testable import DictationAppCore

@available(macOS 14.2, *)
final class OtherRecordersTests: XCTestCase {
    private struct FakeReader: DictationOtherRecorderPropertyReader {
        var objects: [AudioObjectID] = []
        var pids: [AudioObjectID: pid_t?] = [:]
        var inputs: [AudioObjectID: Bool] = [:]
        var failure = false

        func processObjects() throws -> [AudioObjectID] {
            if failure { throw CocoaError(.fileReadUnknown) }
            return objects
        }
        func pid(of process: AudioObjectID) throws -> pid_t? {
            if failure { throw CocoaError(.fileReadUnknown) }
            return pids[process] ?? nil
        }
        func isRunningInput(of process: AudioObjectID) throws -> Bool {
            if failure { throw CocoaError(.fileReadUnknown) }
            return inputs[process] ?? false
        }
    }

    func testActiveOtherInputsReturnPIDsAndExcludeOwnPID() {
        let reader = FakeReader(objects: [1, 2, 3, 4],
                                pids: [1: 100, 2: 200, 3: 300, 4: nil],
                                inputs: [1: true, 2: false, 3: true, 4: true])
        let result = DictationOtherRecorders(reader: reader).snapshot(excluding: 100)
        XCTAssertTrue(result.available)
        XCTAssertEqual(result.pids, [300])
    }

    func testAvailableEmptyIsDistinctFromUnavailable() {
        let empty = DictationOtherRecorders(reader: FakeReader(objects: [])).snapshot(excluding: 100)
        XCTAssertTrue(empty.available)
        XCTAssertTrue(empty.pids.isEmpty)

        let unavailable = DictationOtherRecorders(reader: FakeReader(failure: true)).snapshot(excluding: 100)
        XCTAssertFalse(unavailable.available)
        XCTAssertTrue(unavailable.pids.isEmpty)
    }

    func testPropertyFailureMakesWholeSnapshotUnavailable() {
        let reader = FakeReader(objects: [1, 2], pids: [1: 101, 2: 202], inputs: [1: true, 2: true])
        let result = DictationOtherRecorders(reader: ThrowingAfterFirst(reader: reader)).snapshot(excluding: 100)
        XCTAssertFalse(result.available)
        XCTAssertTrue(result.pids.isEmpty)
    }

    private struct ThrowingAfterFirst: DictationOtherRecorderPropertyReader {
        let reader: FakeReader
        func processObjects() throws -> [AudioObjectID] { reader.objects }
        func pid(of process: AudioObjectID) throws -> pid_t? { reader.pids[process] ?? nil }
        func isRunningInput(of process: AudioObjectID) throws -> Bool {
            if process == 2 { throw CocoaError(.fileReadUnknown) }
            return reader.inputs[process] ?? false
        }
    }
}
