import Combine
import DictationKeyCheck
import Foundation

public struct DictationKeySnapshot: Sendable {
    public var time: TimeInterval
    public var recorders: DictationOtherRecorderSnapshot
    public var foreground: Int32?
    public var windows: Set<Int32>
    public var names: [Int32: String]
    public init(time: TimeInterval, recorders: DictationOtherRecorderSnapshot,
                foreground: Int32? = nil, windows: Set<Int32> = [], names: [Int32: String] = [:]) {
        self.time = time; self.recorders = recorders; self.foreground = foreground
        self.windows = windows; self.names = names
    }
}

/// Key tests never open the microphone or load the recognition model. Runtime
/// checks share the classifier and only publish a notice after the hold ends.
@MainActor
public final class DictationKeyCheckModel: ObservableObject {
    @Published public private(set) var isTesting = false
    @Published public private(set) var message: String?
    @Published public private(set) var hasConflict = false
    public var onRuntimeNotice: ((String) -> Void)?
    private let settings: DictationSettings
    private let makeTrigger: (DictationHoldKey) -> any DictationHoldTrigger
    private let snapshot: @MainActor () async -> DictationKeySnapshot
    private let isFrontmost: @MainActor () -> Bool
    private let ownPID: Int32
    private let clock: () -> TimeInterval
    /// What System Settings calls the Accessibility pane on this Mac.
    private let accessibilitySettingsName: String
    private var key = DictationHoldKey.standard
    private var testTrigger: (any DictationHoldTrigger)?
    private var idleTask: Task<Void, Never>?
    private var testTimeout: Task<Void, Never>?
    private var reactionTask: Task<Void, Never>?
    private var finishTask: Task<Void, Never>?
    private var snapshots: [DictationKeySnapshot] = []
    private var memories: [DictationHoldKey: KeyConflictMemory] = [:]
    private var active: Press?
    private var testedHold = false
    private var reconnected = false
    private var pendingNotice: String?
    private var finding: KeyConflictFinding?
    private var testFinding: KeyConflictVerdict?
    private var testRecorderCoverage = true
    private var armed = false
    private var generation = UUID()

    private struct Press {
        var id = UUID()
        var started: TimeInterval
        var ended: TimeInterval?
        var doubleTap = false
        var test: Bool
        var samples: [DictationKeySnapshot]
    }

    public init(settings: DictationSettings, makeTrigger: @escaping (DictationHoldKey) -> any DictationHoldTrigger,
                snapshot: @escaping @MainActor () async -> DictationKeySnapshot,
                isFrontmost: @escaping @MainActor () -> Bool,
                ownPID: Int32 = ProcessInfo.processInfo.processIdentifier,
                clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
                accessibilitySettingsName: String = DictationPermissionStep.accessibilitySettingsName) {
        self.settings = settings; self.makeTrigger = makeTrigger; self.snapshot = snapshot
        self.isFrontmost = isFrontmost; self.ownPID = ownPID; self.clock = clock
        self.accessibilitySettingsName = accessibilitySettingsName
        select(settings.holdKey)
    }

    public func select(_ key: DictationHoldKey) {
        stopTest()
        self.key = key
        let saved = settings.defaults.string(forKey: "dictation.keyCheck.notice.\(key.rawValue)")
        message = saved
        hasConflict = saved != nil
    }

    public var suggestedKey: DictationHoldKey {
        let order: [DictationHoldKey] = [.rightCommand, .rightOption, .leftControl, .rightControl, .function, .rightShift, .leftOption]
        let choices = order.filter { $0 != key && settings.defaults.string(forKey: "dictation.keyCheck.notice.\($0.rawValue)") == nil }
        return choices.first(where: { settings.defaults.bool(forKey: "dictation.keyCheck.clean.\($0.rawValue)") })
            ?? choices.first ?? .rightCommand
    }

    public func keepKey() {
        if let finding {
            settings.defaults.set(finding.identity ?? finding.kind.rawValue,
                                  forKey: "dictation.keyCheck.accepted.\(key.rawValue)")
        }
        pendingNotice = nil
        message = "Keeping \(key.name). You can test it again at any time."
        hasConflict = false
    }

    public func setArmed(_ value: Bool) {
        armed = value
        idleTask?.cancel(); idleTask = nil
        if value { startPolling() }
    }

    private func startPolling() {
        guard idleTask == nil, armed || isTesting else { return }
        idleTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                await self.collect()
                do { try await Task.sleep(for: .seconds(2)) } catch { return }
            }
        }
    }

    public func startTest() {
        guard isFrontmost() else { return }
        stopTest()
        isTesting = true; testedHold = false; reconnected = false; testFinding = nil
        testRecorderCoverage = true
        message = "Hold \(key.name) for about a second, then let go."
        guard installTestTrigger() else { stopTest(); return }
        startPolling()
        armTestTimeout()
    }

    private func installTestTrigger() -> Bool {
        testTrigger?.stop()
        let trigger = makeTrigger(key)
        trigger.onHoldStart = { [weak self] in self?.pressed(test: true) }
        trigger.onHoldEnd = { [weak self] in self?.released() }
        trigger.onLatchStart = { [weak self] in self?.doubleTapped() }
        trigger.onLatchEnd = { [weak self] in self?.doubleTapped() }
        let pane = accessibilitySettingsName
        trigger.onDegraded = { [weak self] _ in
            self?.message = "In Privacy & Security, allow Earlet under Input Monitoring and under \(pane), then restart Earlet to test this key."
        }
        if trigger.start() { testTrigger = trigger; return true }
        message = "The key listener could not start. Check permissions, then restart Earlet."
        return false
    }

    private func armTestTimeout() {
        testTimeout?.cancel()
        testTimeout = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(15)) } catch { return }
            guard let self, self.isTesting else { return }
            if !self.reconnected && !self.testedHold {
                self.reconnected = true
                guard self.installTestTrigger() else { self.stopTest(); return }
                self.message = "Earlet did not hear \(self.key.name), so it reconnected. Hold it once more."
                self.armTestTimeout()
            } else {
                self.stopTest()
                self.message = "Key test incomplete. Try another key or check Input Monitoring, then test again."
            }
        }
    }

    public func stopTest() {
        testTimeout?.cancel(); testTimeout = nil
        testTrigger?.stop(); testTrigger = nil
        reactionTask?.cancel(); finishTask?.cancel()
        active = nil; generation = UUID(); isTesting = false
        if !armed { idleTask?.cancel(); idleTask = nil }
    }

    public func shutdown() {
        armed = false
        stopTest()
        idleTask?.cancel(); idleTask = nil
    }

    public func pressed(test: Bool = false) {
        guard test == isTesting else { return }
        if test && !isFrontmost() { stopTest(); message = "Key test stopped because Earlet left the front."; return }
        finishTask?.cancel(); reactionTask?.cancel()
        pendingNotice = nil
        active = Press(started: clock(), test: test, samples: snapshots)
        let id = active!.id
        reactionTask = Task { [weak self] in
            // 0.2 s includes foreground/window reactions; the later reads
            // include apps which start their microphone asynchronously.
            for delay in [0.0, 0.2, 0.4, 0.9] {
                if delay > 0 { do { try await Task.sleep(for: .seconds(delay)) } catch { return } }
                guard let self, self.active?.id == id else { return }
                await self.collect()
            }
        }
    }

    public func doubleTapped() {
        guard var press = active else { return }
        press.doubleTap = true
        active = press
        if isTesting { released() }
        else { finishTask?.cancel(); active?.ended = nil }
    }

    public func released() {
        guard active != nil else { return }
        active?.ended = clock()
        let id = active!.id
        finishTask?.cancel()
        finishTask = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(1.5)) } catch { return }
            guard let self, self.active?.id == id else { return }
            await self.collect()
            self.finishPress(id)
        }
    }

    func collect() async {
        let epoch = generation
        let sample = await snapshot()
        guard epoch == generation else { return }
        snapshots.append(sample)
        snapshots = Array(snapshots.suffix(4))
        active?.samples.append(sample)
        if isTesting && !isFrontmost() {
            // Keep the release-time sample: the app which took focus may be
            // the reaction we are testing. A held test still ends promptly.
            if let active {
                if active.ended != nil {
                    finishPress(active.id)
                } else {
                    // Losing focus while the key is still down is itself a
                    // complete conflict signal. Close this press and publish
                    // its finding before tearing down the test listener.
                    var completed = active
                    completed.ended = max(clock(), sample.time)
                    completed.doubleTap = true
                    self.active = completed
                    finishPress(completed.id, allowIncompleteClean: false)
                }
            }
            if isTesting {
                stopTest()
                message = "Key test stopped because Earlet left the front."
            }
        }
    }

    func finishPress(_ id: UUID? = nil, allowIncompleteClean: Bool = true) {
        guard let press = active, id == nil || id == press.id, let end = press.ended else { return }
        active = nil
        let pressEvent = KeyConflictPress(time: press.started, duration: max(0, end - press.started),
                                         test: press.test, doubleTap: press.doubleTap)
        let samples = press.samples.sorted { $0.time < $1.time }
        let pids = samples.reduce(into: Set<Int32>()) { $0.formUnion($1.recorders.pids) }
        let events = samples.filter { $0.recorders.available }.flatMap { sample in
            pids.map { KeyConflictRecorder(time: sample.time, pid: $0, identity: sample.names[$0] ?? "pid:\($0)",
                                          active: sample.recorders.pids.contains($0)) }
        }
        var reactions: [KeyConflictReaction] = []
        if let baseline = samples.last(where: { $0.time < press.started && press.started - $0.time <= 2 }) {
            for sample in samples where sample.time >= press.started && sample.time <= press.started + 0.5 {
                if let pid = sample.foreground, pid != baseline.foreground, pid != ownPID {
                    reactions.append(.init(kind: .foreground, time: sample.time, pid: pid, identity: sample.names[pid] ?? "pid:\(pid)"))
                }
                for pid in sample.windows.subtracting(baseline.windows) where pid != ownPID {
                    reactions.append(.init(kind: .window, time: sample.time, pid: pid, identity: sample.names[pid] ?? "pid:\(pid)"))
                }
            }
        }
        var memory = memories[key] ?? KeyConflictMemory()
        var verdict = KeyConflictClassifier.classify(.init(presses: [pressEvent], recorders: events, reactions: reactions),
                                                     ownPID: ownPID, runtime: !press.test, memory: &memory)
        memories[key] = memory
        if let pid = verdict.finding?.pid {
            verdict.finding?.name = samples.reversed().compactMap { $0.names[pid] }.first
        }
        if press.test {
            let baselineAvailable = samples.contains {
                $0.time < press.started && press.started - $0.time <= 2 && $0.recorders.available
            }
            let reactionAvailable = samples.contains {
                $0.time >= press.started && $0.time <= press.started + 1.5 && $0.recorders.available
            }
            testRecorderCoverage = testRecorderCoverage && baselineAvailable && reactionAvailable
            if verdict.finding != nil { testFinding = verdict }
            if !press.doubleTap {
                guard verdict.qualifyingPress else { message = "Hold \(key.name) for about a second, then let go."; return }
                testedHold = true
                message = "Now press \(key.name) twice quickly."
                armTestTimeout()
                return
            }
            if !testedHold, verdict.finding == nil, allowIncompleteClean {
                message = "Hold \(key.name) for about a second before testing the double tap."
                armTestTimeout()
                return
            }
            let final = testFinding ?? verdict
            stopTest()
            publish(final, microphonesAvailable: testRecorderCoverage, test: true,
                    allowIncompleteClean: allowIncompleteClean)
        } else {
            publish(verdict, microphonesAvailable: samples.contains { $0.recorders.available }, test: false)
        }
    }

    private func publish(_ verdict: KeyConflictVerdict, microphonesAvailable: Bool, test: Bool,
                         allowIncompleteClean: Bool = true) {
        let noticeKey = "dictation.keyCheck.notice.\(key.rawValue)"
        guard let found = verdict.finding, verdict.confirmed else {
            if test, allowIncompleteClean {
                settings.defaults.removeObject(forKey: noticeKey)
                settings.defaults.removeObject(forKey: "dictation.keyCheck.accepted.\(key.rawValue)")
                if microphonesAvailable {
                    settings.defaults.set(true, forKey: "dictation.keyCheck.clean.\(key.rawValue)")
                } else {
                    settings.defaults.removeObject(forKey: "dictation.keyCheck.clean.\(key.rawValue)")
                }
                hasConflict = false
                message = microphonesAvailable
                    ? "Earlet heard \(key.name). No other app started recording or came to the front."
                    : "Earlet heard \(key.name). macOS did not provide recorder activity, so that part could not be checked."
            }
            return
        }
        finding = found
        let app = found.name ?? "Another app"
        let action = found.kind == .recorder ? "started recording" : found.kind == .foreground ? "came to the front" : "opened a window"
        let text = "\(app) also \(action) when you used \(key.name). Try another key, or change the shortcut in that app."
        settings.defaults.set(text, forKey: noticeKey)
        settings.defaults.set(false, forKey: "dictation.keyCheck.clean.\(key.rawValue)")
        if !test, settings.defaults.string(forKey: "dictation.keyCheck.accepted.\(key.rawValue)") == (found.identity ?? found.kind.rawValue) { return }
        message = text; hasConflict = true
        if !test { pendingNotice = text; onRuntimeNotice?(text) }
    }

    func takeRuntimeNotice() -> String? {
        defer { pendingNotice = nil }
        return pendingNotice
    }
}
