#if canImport(Testing)
import CoreGraphics
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Dictation key conflict checks")
struct DictationKeyCheckModelTests {
    private final class TestTrigger: DictationHoldTrigger {
        var onHoldStart: (() -> Void)?
        var onHoldEnd: (() -> Void)?
        var onModifierFlags: ((CGEventFlags) -> Void)?
        var onDegraded: ((DictationPermissionStatus) -> Void)?
        var onLatchStart: (() -> Void)?
        var onLatchEnd: (() -> Void)?
        var starts = 0
        var stops = 0
        var updates = 0
        var startSucceeds = true

        func start() -> Bool { starts += 1; return startSucceeds }
        func stop() { stops += 1 }
        func update(_ key: DictationHoldKey) { updates += 1 }
    }

    private final class SnapshotFixture {
        var values: [DictationKeySnapshot]
        var index = 0
        var override: DictationKeySnapshot?

        init(_ values: [DictationKeySnapshot]) { self.values = values }

        func next() -> DictationKeySnapshot {
            if let override {
                index += 1
                return override
            }
            guard !values.isEmpty else {
                return .init(time: 0, recorders: .init(available: false))
            }
            defer { index += 1 }
            return values[min(index, values.count - 1)]
        }
    }

    private final class TestClock: @unchecked Sendable {
        var value: TimeInterval = 0
    }

    private final class FrontmostBox: @unchecked Sendable {
        var value = true
    }

    private func recorderSnapshot(_ time: TimeInterval, _ active: Bool,
                                  available: Bool = true) -> DictationKeySnapshot {
        .init(time: time,
              recorders: .init(available: available, pids: active ? [42] : []),
              names: active ? [42: "Other Recorder"] : [:])
    }

    private func foregroundSnapshot(_ time: TimeInterval, _ pid: Int32?) -> DictationKeySnapshot {
        .init(time: time, recorders: .init(available: true), foreground: pid,
              names: pid.map { [$0: "Front App"] } ?? [:])
    }

    private func makeModel(_ values: [DictationKeySnapshot], defaults: UserDefaults? = nil,
                           frontmost: @escaping @Sendable () -> Bool = { true })
    -> (DictationKeyCheckModel, SnapshotFixture, TestTrigger, TestClock, UserDefaults) {
        let ownedDefaults = defaults ?? UserDefaults(suiteName: "ai.scam.dictation.keycheck.\(UUID())")!
        let fixture = SnapshotFixture(values)
        let trigger = TestTrigger()
        let clock = TestClock()
        let model = DictationKeyCheckModel(
            settings: DictationSettings(defaults: ownedDefaults),
            makeTrigger: { _ in trigger },
            snapshot: { fixture.next() },
            isFrontmost: frontmost,
            ownPID: 10,
            clock: { clock.value })
        return (model, fixture, trigger, clock, ownedDefaults)
    }

    private func finish(_ model: DictationKeyCheckModel, _ fixture: SnapshotFixture, _ clock: TestClock,
                        start: TimeInterval, end: TimeInterval, test: Bool,
                        active: DictationKeySnapshot, inactive: DictationKeySnapshot,
                        doubleTap: Bool = false) async {
        await model.collect()
        clock.value = start
        model.pressed(test: test)
        fixture.override = active
        await model.collect()
        clock.value = end
        if doubleTap { model.doubleTapped() } else { model.released() }
        fixture.override = inactive
        await model.collect()
        fixture.override = nil
        model.finishPress()
    }

    @Test("a test hold and double tap classify one recorder without capture or worker")
    func testHoldThenDoubleTapClassifiesOneApp() async {
        let (model, fixture, trigger, clock, _) = makeModel([
            recorderSnapshot(0, false), recorderSnapshot(1, true), recorderSnapshot(3, true)
        ])

        model.startTest()
        await finish(model, fixture, clock, start: 1, end: 2, test: true,
                     active: recorderSnapshot(1.2, true), inactive: recorderSnapshot(2, false))
        #expect(model.message == "Now press Right Command twice quickly.")
        await finish(model, fixture, clock, start: 3, end: 4, test: true,
                     active: recorderSnapshot(3.2, true), inactive: recorderSnapshot(4, false), doubleTap: true)

        #expect(!model.isTesting)
        #expect(model.hasConflict)
        #expect(model.message?.contains("Other Recorder") == true)
        #expect(trigger.starts == 1)
    }

    @Test("clean tests distinguish available recorder data from unavailable data")
    func cleanVsUnavailable() async {
        let clean = makeModel([
            recorderSnapshot(0, false), recorderSnapshot(1, false), recorderSnapshot(3, false)
        ])
        clean.0.startTest()
        await finish(clean.0, clean.1, clean.3, start: 1, end: 2, test: true,
                     active: recorderSnapshot(1, false), inactive: recorderSnapshot(2, false))
        await finish(clean.0, clean.1, clean.3, start: 3, end: 4, test: true,
                     active: recorderSnapshot(3, false), inactive: recorderSnapshot(4, false), doubleTap: true)
        #expect(clean.0.hasConflict == false)
        #expect(clean.0.message?.contains("No other app started recording") == true)

        let unavailable = makeModel([
            recorderSnapshot(0, false, available: false),
            recorderSnapshot(1, false, available: false),
            recorderSnapshot(3, false, available: false)
        ])
        unavailable.0.startTest()
        await finish(unavailable.0, unavailable.1, unavailable.3, start: 1, end: 2, test: true,
                     active: recorderSnapshot(1, false, available: false), inactive: recorderSnapshot(2, false, available: false))
        await finish(unavailable.0, unavailable.1, unavailable.3, start: 3, end: 4, test: true,
                     active: recorderSnapshot(3, false, available: false), inactive: recorderSnapshot(4, false, available: false), doubleTap: true)
        #expect(unavailable.0.hasConflict == false)
        #expect(unavailable.0.message?.contains("could not be checked") == true)
        #expect(unavailable.4.bool(forKey: "dictation.keyCheck.clean.\(DictationHoldKey.standard.rawValue)") == false)
    }

    @Test("losing recorder visibility during either gesture cannot certify a clean key")
    func unavailableReactionCannotCertifyClean() async {
        let (model, fixture, _, clock, defaults) = makeModel([recorderSnapshot(0, false)])
        model.startTest()
        await finish(model, fixture, clock, start: 1, end: 2, test: true,
                     active: recorderSnapshot(1.2, false, available: false),
                     inactive: recorderSnapshot(2, false, available: false))
        fixture.override = recorderSnapshot(2.5, false)
        await finish(model, fixture, clock, start: 3, end: 4, test: true,
                     active: recorderSnapshot(3.2, false), inactive: recorderSnapshot(4, false), doubleTap: true)
        #expect(model.message?.contains("could not be checked") == true)
        #expect(!defaults.bool(forKey: "dictation.keyCheck.clean.\(DictationHoldKey.standard.rawValue)"))
    }

    @Test("runtime confirmation uses two of three and a correlated recorder release")
    func runtimeConfirmationAndRelease() async {
        var notices: [String] = []
        let (model, fixture, _, clock, _) = makeModel([
            recorderSnapshot(0, false), recorderSnapshot(1, true), recorderSnapshot(2, false),
            recorderSnapshot(3, false), recorderSnapshot(4, true), recorderSnapshot(5, false)
        ])
        model.onRuntimeNotice = { notices.append($0) }

        await finish(model, fixture, clock, start: 1, end: 2, test: false,
                     active: recorderSnapshot(1.2, true), inactive: recorderSnapshot(2, true))
        #expect(notices.isEmpty)
        await finish(model, fixture, clock, start: 4, end: 5, test: false,
                     active: recorderSnapshot(4.2, true), inactive: recorderSnapshot(5, false))

        #expect(notices.count == 1)
        #expect(notices[0].contains("Other Recorder") == true)
        #expect(model.hasConflict)
        #expect(model.takeRuntimeNotice() == notices[0])
        #expect(model.takeRuntimeNotice() == nil)
    }

    @Test("keep suppresses the accepted runtime conflict and preserves the notice")
    func keepSuppressionAndPersistence() async {
        let defaults = UserDefaults(suiteName: "ai.scam.dictation.keycheck.\(UUID())")!
        let (model, fixture, _, clock, _) = makeModel([
            recorderSnapshot(0, false), recorderSnapshot(1, true), recorderSnapshot(2, false),
            recorderSnapshot(3, false), recorderSnapshot(4, true), recorderSnapshot(5, false)
        ], defaults: defaults)
        defaults.set(true, forKey: "dictation.keyCheck.clean.\(DictationHoldKey.rightOption.rawValue)")
        model.onRuntimeNotice = { _ in }
        await finish(model, fixture, clock, start: 1, end: 2, test: false,
                     active: recorderSnapshot(1.2, true), inactive: recorderSnapshot(2, false))
        await finish(model, fixture, clock, start: 4, end: 5, test: false,
                     active: recorderSnapshot(4.2, true), inactive: recorderSnapshot(5, false))
        let persisted = model.message
        model.keepKey()
        #expect(model.hasConflict == false)
        #expect(model.message?.contains("Keeping") == true)
        #expect(defaults.string(forKey: "dictation.keyCheck.accepted.\(DictationHoldKey.standard.rawValue)") == "Other Recorder")
        #expect(model.suggestedKey == .rightOption)

        let restored = makeModel([], defaults: defaults).0
        restored.select(.standard)
        #expect(restored.hasConflict)
        #expect(restored.message == persisted)
    }

    @Test("a refused test listener leaves a paused app without snapshot polling")
    func failedListenerStopsTestWork() async {
        let (model, fixture, trigger, _, _) = makeModel([])
        trigger.startSucceeds = false
        model.startTest()
        for _ in 0..<10 { await Task.yield() }
        #expect(!model.isTesting)
        #expect(fixture.index == 0)
        #expect(model.message?.contains("could not start") == true)
    }

    @Test("a double tap alone cannot certify a key before its held test")
    func doubleTapStillNeedsHold() async {
        let (model, fixture, _, clock, defaults) = makeModel([recorderSnapshot(0, false)])
        model.startTest()
        await finish(model, fixture, clock, start: 1, end: 1.2, test: true,
                     active: recorderSnapshot(1.1, false), inactive: recorderSnapshot(1.2, false), doubleTap: true)
        #expect(model.isTesting)
        #expect(model.message?.contains("before testing") == true)
        #expect(!defaults.bool(forKey: "dictation.keyCheck.clean.\(DictationHoldKey.standard.rawValue)"))
        model.stopTest()
    }

    @Test("test mode cancels when the window leaves front and stops its listener")
    func testStopsOnFrontmostLossAndListenerStops() async {
        let frontmost = FrontmostBox()
        let (model, _, trigger, _, _) = makeModel([], frontmost: { frontmost.value })
        model.startTest()
        #expect(model.isTesting)
        frontmost.value = false
        model.pressed(test: true)
        #expect(!model.isTesting)
        #expect(model.message?.contains("left the front") == true)
        model.stopTest()
        #expect(trigger.stops == 1)
    }

    @Test("a release-time focus change is classified before the test stops")
    func focusChangeBeforeTestTeardownIsClassified() async {
        let frontmost = FrontmostBox()
        let (model, fixture, trigger, clock, _) = makeModel([foregroundSnapshot(0, 10)], frontmost: { frontmost.value })
        model.startTest()
        await finish(model, fixture, clock, start: 1, end: 2, test: true,
                     active: foregroundSnapshot(1.2, 10), inactive: foregroundSnapshot(2, 10))

        frontmost.value = true
        clock.value = 3
        model.pressed(test: true)
        fixture.override = foregroundSnapshot(3.2, 77)
        await model.collect()
        clock.value = 4
        model.doubleTapped()
        frontmost.value = false
        fixture.override = foregroundSnapshot(3.2, 77)
        await model.collect()

        #expect(!model.isTesting)
        #expect(model.hasConflict)
        #expect(model.message?.contains("Front App") == true)
        #expect(trigger.stops == 1)
    }

    @Test("focus stolen while held closes and publishes the active conflict")
    func focusChangeDuringHeldPressIsClassified() async {
        let frontmost = FrontmostBox()
        let (model, fixture, trigger, clock, _) = makeModel([foregroundSnapshot(0, 10)], frontmost: { frontmost.value })
        model.startTest()
        await model.collect()
        clock.value = 1
        model.pressed(test: true)
        fixture.override = foregroundSnapshot(1.2, 77)
        frontmost.value = false
        await model.collect()

        #expect(!model.isTesting)
        #expect(model.hasConflict)
        #expect(model.message?.contains("Front App") == true)
        #expect(trigger.stops == 1)
    }

    @Test("an available snapshot without a pre-press baseline cannot claim a clean check")
    func cleanCheckRequiresPrePressBaseline() async {
        let (model, fixture, _, clock, _) = makeModel([])
        model.startTest()
        let available = recorderSnapshot(1.2, false)

        clock.value = 1
        model.pressed(test: true)
        fixture.override = available
        await model.collect()
        clock.value = 2
        model.released()
        await model.collect()
        fixture.override = nil
        model.finishPress()

        clock.value = 1
        model.pressed(test: true)
        fixture.override = available
        await model.collect()
        clock.value = 2
        model.doubleTapped()
        await model.collect()
        fixture.override = nil
        model.finishPress()

        #expect(model.hasConflict == false)
        #expect(model.message?.contains("could not be checked") == true)
        #expect(model.message?.contains("No other app started recording") == false)
    }

    @Test("test mode uses the key listener only and never starts dictation capture")
    func testModePressesNeverRecord() async {
        let (model, fixture, trigger, clock, _) = makeModel([
            recorderSnapshot(0, false), recorderSnapshot(1, false), recorderSnapshot(3, false)
        ])
        model.startTest()
        await finish(model, fixture, clock, start: 1, end: 2, test: true,
                     active: recorderSnapshot(1, false), inactive: recorderSnapshot(2, false))
        await finish(model, fixture, clock, start: 3, end: 4, test: true,
                     active: recorderSnapshot(3, false), inactive: recorderSnapshot(4, false), doubleTap: true)
        #expect(trigger.starts == 1)
        #expect(trigger.updates == 0)
        #expect(model.isTesting == false)
    }
}
#endif
