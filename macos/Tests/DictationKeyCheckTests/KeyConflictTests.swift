import XCTest
@testable import DictationKeyCheck

final class KeyConflictTests: XCTestCase {
    private let own: Int32 = 10

    func testGoldenFixtureCoversClassifierCases() throws {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let data = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/key-conflict-golden.json"))
        let cases = try JSONDecoder().decode([Golden].self, from: data)
        XCTAssertGreaterThanOrEqual(cases.count, 8)
        for item in cases {
            let verdict = KeyConflictClassifier.classify(item.timeline, ownPID: own)
            XCTAssertEqual(verdict.confirmed, item.confirmed, item.name)
            XCTAssertEqual(verdict.finding?.kind.rawValue, item.kind, item.name)
        }
    }

    func testOwnAndAlwaysOnRecordersAreIgnored() {
        let timeline = KeyConflictTimeline(presses: [.init(time: 1, duration: 1)], recorders: [
            .init(time: 0, pid: 20, identity: "call", active: true),
            .init(time: 1.1, pid: 20, identity: "call", active: true),
            .init(time: 1.2, pid: own, identity: "self", active: true),
        ])
        XCTAssertNil(KeyConflictClassifier.classify(timeline, ownPID: own).finding)
    }

    func testRuntimeNeedsTwoOfThreeAndTestPressNeedsOne() {
        var memory = KeyConflictMemory()
        let make: (TimeInterval, Bool) -> KeyConflictTimeline = { t, test in
            .init(presses: [.init(time: t, duration: 1, test: test)], recorders: [
                .init(time: t - 1, pid: 42, identity: "Recorder", active: false),
                .init(time: t + 0.1, pid: 42, identity: "Recorder", active: true)
            ])
        }
        XCTAssertFalse(KeyConflictClassifier.classify(make(1, false), ownPID: own, memory: &memory).confirmed)
        XCTAssertTrue(KeyConflictClassifier.classify(make(3, false), ownPID: own, memory: &memory).confirmed)
        var fresh = KeyConflictMemory()
        XCTAssertTrue(KeyConflictClassifier.classify(make(1, true), ownPID: own, memory: &fresh).confirmed)
    }

    func testRecorderStopCorrelatesWithOnePress() {
        let timeline = KeyConflictTimeline(presses: [.init(time: 1, duration: 1)], recorders: [
            .init(time: 0, pid: 42, identity: "Recorder", active: false),
            .init(time: 1.1, pid: 42, identity: "Recorder", active: true),
            .init(time: 2.2, pid: 42, identity: "Recorder", active: false),
        ])
        XCTAssertTrue(KeyConflictClassifier.classify(timeline, ownPID: own).confirmed)
    }

    func testTwoCandidatesAreUnnamedAndKindsRemainDistinct() {
        let timeline = KeyConflictTimeline(presses: [.init(time: 1, duration: 1)], reactions: [
            .init(kind: .foreground, time: 1.1, pid: 20, identity: "Front"),
            .init(kind: .window, time: 1.2, pid: 21, identity: "Window"),
        ])
        let finding = try! XCTUnwrap(KeyConflictClassifier.classify(timeline, ownPID: own).finding)
        XCTAssertNil(finding.name)
        XCTAssertEqual(finding.candidateCount, 2)
        XCTAssertEqual(KeyConflictClassifier.classify(timeline, ownPID: own).os, .unknown)
    }

    func testPIDIsRequiredForAttributionAndShortTapRejected() {
        let unnamed = KeyConflictTimeline(presses: [.init(time: 1, duration: 1)], reactions: [
            .init(kind: .foreground, time: 1.1, pid: nil, identity: "Front")
        ])
        XCTAssertNil(KeyConflictClassifier.classify(unnamed, ownPID: own).finding?.name)
        let short = KeyConflictTimeline(presses: [.init(time: 1, duration: 0.2)], reactions: [
            .init(kind: .foreground, time: 1.1, pid: 20, identity: "Front")
        ])
        XCTAssertFalse(KeyConflictClassifier.classify(short, ownPID: own).qualifyingPress)
        XCTAssertNil(KeyConflictClassifier.classify(short, ownPID: own).finding)
    }

    func testRecorderMustStartNearPressRatherThanLateInLongHold() {
        let timeline = KeyConflictTimeline(presses: [.init(time: 1, duration: 10)], recorders: [
            .init(time: 2.6, pid: 42, identity: "Recorder", active: true)
        ])
        XCTAssertNil(KeyConflictClassifier.classify(timeline, ownPID: own).finding)
    }

    func testRecorderWithoutRecentInactiveBaselineIsUnknown() {
        for baseline in [[], [KeyConflictRecorder(time: -2, pid: 42, identity: "Recorder", active: false)]] {
            let timeline = KeyConflictTimeline(presses: [.init(time: 1, duration: 1, test: true)], recorders: baseline + [
                .init(time: 1.1, pid: 42, identity: "Recorder", active: true),
                .init(time: 2.1, pid: 42, identity: "Recorder", active: false)
            ])
            XCTAssertNil(KeyConflictClassifier.classify(timeline, ownPID: own).finding)
        }
    }

    func testQuietQualifyingPressesSeparateRuntimeHits() {
        var memory = KeyConflictMemory()
        let hit = KeyConflictTimeline(presses: [.init(time: 1, duration: 1)], reactions: [
            .init(kind: .foreground, time: 1.1, pid: 42, identity: "Recorder")
        ])
        let quiet = { (time: TimeInterval) in
            KeyConflictTimeline(presses: [.init(time: time, duration: 1)])
        }
        XCTAssertFalse(KeyConflictClassifier.classify(hit, ownPID: own, memory: &memory).confirmed)
        _ = KeyConflictClassifier.classify(quiet(2), ownPID: own, memory: &memory)
        _ = KeyConflictClassifier.classify(quiet(3), ownPID: own, memory: &memory)
        XCTAssertFalse(KeyConflictClassifier.classify(
            KeyConflictTimeline(presses: [.init(time: 4, duration: 1)], reactions: [
                .init(kind: .foreground, time: 4.1, pid: 42, identity: "Recorder")
            ]), ownPID: own, memory: &memory).confirmed)
    }

    func testMemoryDoesNotCrossConfirmSameIdentityAcrossKinds() {
        var memory = KeyConflictMemory()
        let foreground = KeyConflictTimeline(presses: [.init(time: 1, duration: 1)], reactions: [
            .init(kind: .foreground, time: 1.1, pid: 42, identity: "App")
        ])
        let window = KeyConflictTimeline(presses: [.init(time: 2, duration: 1)], reactions: [
            .init(kind: .window, time: 2.1, pid: 42, identity: "App")
        ])
        XCTAssertFalse(KeyConflictClassifier.classify(foreground, ownPID: own, memory: &memory).confirmed)
        XCTAssertFalse(KeyConflictClassifier.classify(window, ownPID: own, memory: &memory).confirmed)
    }

    func testForegroundReactionWinsOverWindowRegardlessOfInputOrder() throws {
        let timeline = KeyConflictTimeline(presses: [.init(time: 1, duration: 1)], reactions: [
            .init(kind: .window, time: 1.1, pid: 20, identity: "Window"),
            .init(kind: .foreground, time: 1.2, pid: 21, identity: "Front")
        ])
        let finding = try XCTUnwrap(KeyConflictClassifier.classify(timeline, ownPID: own).finding)
        XCTAssertEqual(finding.kind, .foreground)
    }

    private struct Golden: Codable {
        var name: String
        var timeline: KeyConflictTimeline
        var confirmed: Bool
        var kind: String?
    }
}
