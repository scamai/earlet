import AppKit
import CoreGraphics
import DictationAppCore
import DictationHistory
import DictationLearning
import DictationKit
import SwiftUI

// Offscreen render of the main window (every build honours it, release
// included; it is not compiled out), the window's twin of
// `DictationHUDRenderMode`: launched with `DICTATION_WINDOW_RENDER_DIR` set,
// the app builds the REAL `DictationWindow` over an inert environment — no
// microphone, worker process, event tap, key event or real settings — drives
// it into each scenario through the model's public API, writes one PNG per
// scenario × width × appearance, and exits. The way to see the window on a
// host reached over SSH, where Screen Recording is not granted. Never
// reachable from the UI.

enum DictationWindowRenderMode {
    /// Widths a reviewer checks: the window's minimum (the rail beside a
    /// pane at its narrowest), the default, and wide.
    static let widths: [CGFloat] = [600, 720, 960]
    /// Tall enough that a pane's scroll view shows the whole pane.
    static let height: CGFloat = 1100

    @MainActor
    static func run(to directory: URL) async {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for scenario in DictationRenderScenario.allCases {
            let rig = DictationRenderRig(scenario: scenario)
            await rig.drive()
            for width in widths {
                for dark in [false, true] {
                    rig.beforeEachImage()
                    let name = "window-\(scenario.rawValue)-\(Int(width))-\(dark ? "dark" : "light").png"
                    let url = directory.appendingPathComponent(name)
                    let written = render(model: rig.model, scenario: scenario, width: width, dark: dark, to: url)
                    FileHandle.standardError.write(Data("[window-render] \(url.path) \(written ? "written" : "FAILED")\n".utf8))
                }
            }
            rig.tearDown()
        }
    }

    /// Renders the window at `width` × `height` points, at the screen's
    /// backing scale, over the window background of that appearance.
    @MainActor
    private static func render(model: DictationModel, scenario: DictationRenderScenario,
                               width: CGFloat, dark: Bool, to url: URL) -> Bool {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let root = DictationWindow(model: model, navigation: DictationWindowNavigation(pane: scenario.pane),
                                   troubleshootingExpanded: scenario == .troubleshooting,
                                   permissionRecoveryExpanded: scenario == .permissionRecovery)
            .environment(\.colorScheme, dark ? .dark : .light)
        let hosting = NSHostingView(rootView: root)
        hosting.appearance = appearance
        let frame = NSRect(x: 0, y: 0, width: width, height: height)
        let window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = appearance
        window.contentView = hosting
        hosting.setFrameSize(frame.size)
        hosting.layoutSubtreeIfNeeded()
        // One run-loop turn so SwiftUI commits the first layout pass.
        RunLoop.main.run(until: Date().addingTimeInterval(0.15))
        guard let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { return false }
        hosting.cacheDisplay(in: hosting.bounds, to: rep)
        guard let png = rep.representation(using: .png, properties: [:]) else { return false }
        do {
            try png.write(to: url)
            return true
        } catch {
            return false
        }
    }
}

/// The states a reviewer needs to see.
enum DictationRenderScenario: String, CaseIterable {
    /// First launch: no grant yet, so the permission checklist leads.
    case firstRun = "first-run"
    case accessibility, inputMonitoring = "input-monitoring", restart
    case permissionRecovery = "permission-recovery"
    /// Every grant in, dictation on and idle.
    case ready
    case returningUser = "returning-user"
    /// Every grant in, enabled and then paused by the user.
    case paused
    /// A hold is recording: status line and Cancel.
    case recording
    /// The vocabulary holds taught and learned entries.
    case vocabulary
    case vocabularyUnsaved = "vocabulary-unsaved", vocabularySaved = "vocabulary-saved"
    case recentResult = "recent-result", recovery, copiedResult = "copied-result"
    case learned, learningUndone = "learning-undone"
    case learningChecking = "learning-checking", learningUnavailable = "learning-unavailable"
    case learningPending = "learning-pending", learningRejected = "learning-rejected", learnedRepo = "learned-repo"
    /// The speech resources are missing: the failure sentence.
    case missingResources = "missing-resources"
    /// Ready, on the Settings pane, with a history to count.
    case settings
    /// Settings with a recorded combination as the hold key, listed after
    /// the presets.
    case settingsShortcut = "settings-shortcut"
    /// Settings while it records a new hold key.
    case settingsRecording = "settings-recording"
    /// Settings with a recorded extra mouse button (button 7) chosen.
    case settingsExtraMouseButton = "settings-extra-mouse-button"
    /// Settings while it records a new mouse button.
    case settingsMouseRecording = "settings-mouse-recording"
    /// The History pane: dictations over three days, some with recordings.
    case history
    case historyEmpty = "history-empty", historyOff = "history-off", historyLocked = "history-locked"
    /// Ready, with the Troubleshooting disclosure open on Open Log Folder.
    case troubleshooting

    /// The pane each scenario is drawn on: the one that shows its state.
    /// The correction notices sit under the learning switch, on Vocabulary;
    /// the learning receipt and everything else waiting for the user, on
    /// Home.
    var pane: DictationWindowPane {
        switch self {
        case .vocabulary, .vocabularyUnsaved, .vocabularySaved,
             .learningChecking, .learningUnavailable, .learningPending, .learningRejected, .learnedRepo:
            return .vocabulary
        case .settings, .troubleshooting, .settingsShortcut, .settingsRecording, .settingsExtraMouseButton,
             .settingsMouseRecording:
            return .settings
        case .history, .historyEmpty, .historyOff, .historyLocked:
            return .history
        default:
            return .home
        }
    }
}

/// An inert `DictationEnvironment` plus the scenario's script.
@MainActor
final class DictationRenderRig {
    let scenario: DictationRenderScenario
    let model: DictationModel
    private let trigger = RenderTrigger()
    /// The history's key, in memory: a render never reaches the keychain.
    private let historyKeys = MemoryHistoryKeys()
    private let historyStore: DictationHistoryStore
    private let suiteName = "ai.scam.dictation.render.\(UUID().uuidString)"
    private let scratch = FileManager.default.temporaryDirectory
        .appendingPathComponent("dictation-render-\(UUID().uuidString)", isDirectory: true)

    init(scenario: DictationRenderScenario) {
        self.scenario = scenario
        let defaults = UserDefaults(suiteName: suiteName)!
        if scenario == .returningUser {
            DictationSettings(defaults: defaults).hasCompletedFirstDictation = true
        }
        if scenario == .historyOff {
            DictationSettings(defaults: defaults).historyEnabled = false
        }
        // Settings shows a chosen mouse button with its caution;
        // Troubleshooting, on the same pane, shows none.
        if scenario == .settings {
            DictationSettings(defaults: defaults).mouseButton = .back
        }
        if scenario == .settingsExtraMouseButton {
            DictationSettings(defaults: defaults).mouseButton = DictationMouseSideButton(buttonNumber: 6)
        }
        if scenario == .historyLocked {
            historyKeys.refuse(.unavailable(-25293))
        }
        historyStore = DictationHistoryStore(
            directory: scratch.appendingPathComponent("history", isDirectory: true), keys: historyKeys)
        let microphone = scenario != .firstRun
        let accessibility = microphone && scenario != .accessibility && scenario != .permissionRecovery
        let inputMonitoring = accessibility && scenario != .inputMonitoring
        trigger.startResult = scenario != .restart
        let resources: DictationResources? = scenario == .missingResources
            ? nil : DictationResources(python: "/usr/bin/true", runtime: "/tmp", model: "/tmp")
        try? FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        let learning = DictationLearning(
            store: LearningStore(url: scratch.appendingPathComponent("learning.json")),
            lexicon: WordListLexicon(words: ["we", "use", "the", "hello", "world", "a", "for", "and", "wrapper"]))
        let trigger = self.trigger
        let environment = DictationEnvironment(
            settings: DictationSettings(defaults: defaults),
            capture: RenderCapture(),
            permissions: DictationPermissions(
                requestMicrophone: { microphone },
                microphoneAuthorized: { microphone },
                accessibilityTrusted: { _ in accessibility },
                inputMonitoringGranted: { inputMonitoring },
                requestInputMonitoring: {},
                openMicrophoneSettings: {},
                openAccessibilitySettings: {}),
            resolveResources: { resources },
            makeWorker: { _ in RenderWorker() },
            makeTrigger: { _ in trigger },
            makeMouseTrigger: { _ in RenderMouseTrigger() },
            keyWatch: RenderKeyWatch(),
            stopTrap: RenderStopTrap(),
            privacyGate: { nil },
            insertText: { _ in
                (scenario == .recovery ? .failed(.focusedElementNotEditable)
                    : .success(clipboardByteIdentical: true), TextInserter.InsertTiming())
            },
            clipboardFallback: { _ in scenario != .recovery },
            settleClipboard: {},
            targetApplication: { nil },
            openLogFolder: { true },
            relaunch: {},
            loginItem: DictationLoginItem(
                status: { .off }, register: {}, unregister: {}, openSettings: {}),
            correctionWatch: RenderCorrectionWatch(),
            learning: learning,
            // A render run dictates for real; its takes must never reach the
            // user's data folder.
            takes: DictationTakeOptions(directory: scratch.appendingPathComponent("recovery", isDirectory: true)),
            history: historyStore)
        model = DictationModel(environment: environment)
    }

    /// Restores what tearing down the last image's window undid: leaving
    /// Settings ends a recording.
    func beforeEachImage() {
        if scenario == .settingsRecording { _ = model.beginRecordingHoldKey() }
        if scenario == .settingsMouseRecording { _ = model.beginRecordingMouseButton() }
    }

    /// Moves the model into the scenario through its public API only (and
    /// seeds the scratch history through its store).
    func drive() async {
        model.refreshPermissions()
        if [.returningUser, .settings, .history].contains(scenario) { await seedHistory() }
        await model.history.refresh()
        switch scenario {
        case .firstRun, .accessibility, .inputMonitoring:
            break
        case .permissionRecovery:
            model.checkPermissionsAgain()
        case .ready, .returningUser, .missingResources, .settings, .troubleshooting, .restart,
             .history, .historyEmpty, .historyOff, .historyLocked:
            await model.toggle()
        case .settingsShortcut:
            await model.toggle()
            model.selectHoldKey(.shortcut(DictationKeyShortcut(keyCode: 49, modifiers: .option)))
        case .settingsRecording:
            await model.toggle()
            _ = model.beginRecordingHoldKey()
        case .settingsExtraMouseButton:
            await model.toggle()
        case .settingsMouseRecording:
            await model.toggle()
            _ = model.beginRecordingMouseButton()
        case .paused:
            await model.toggle()
            await model.toggle()
        case .recording:
            await model.toggle()
            trigger.onHoldStart?()
            try? await Task.sleep(for: .milliseconds(300))
        case .vocabulary:
            await model.toggle()
            _ = await model.vocabulary.teach(written: "Brivioo", soundsLike: "bree vee oh")
            _ = await model.vocabulary.teach(written: "Kubernetes", soundsLike: "")
            _ = await model.vocabulary.teach(written: "Saoirse", soundsLike: "seer sha")
            await model.vocabulary.refresh()
        case .vocabularyUnsaved, .vocabularySaved:
            await model.toggle()
            _ = await model.vocabulary.teach(written: "Brivioo", soundsLike: "bree vee oh")
            let url = scratch.appendingPathComponent("learning.json")
            do {
                try FileManager.default.removeItem(at: url)
                try FileManager.default.createDirectory(at: url, withIntermediateDirectories: false)
                await model.vocabulary.forgetAll()
                if scenario == .vocabularySaved {
                    try FileManager.default.removeItem(at: url)
                    await model.vocabulary.retrySaving()
                }
            } catch { preconditionFailure("Could not create the vocabulary save render fixture: \(error)") }
        case .recentResult, .recovery, .copiedResult:
            await model.toggle()
            trigger.onHoldStart?()
            try? await Task.sleep(for: .milliseconds(350))
            trigger.onHoldEnd?()
            let deadline = Date().addingTimeInterval(3)
            while (model.recording || model.busy), Date() < deadline {
                try? await Task.sleep(for: .milliseconds(20))
            }
            if scenario == .copiedResult { await model.copyRecentResult() }
        case .learned, .learningUndone:
            await model.toggle()
            _ = await model.vocabulary.observe(dictationID: "render", dictated: "We use brivio",
                                               edited: "We use Brivioo")
            if scenario == .learningUndone { await model.vocabulary.undoLastLearning() }
        case .learningChecking, .learningUnavailable, .learningPending, .learningRejected, .learnedRepo:
            await model.toggle()
            let id = UUID()
            model.vocabulary.beginObservationFeedback(id: id)
            switch scenario {
            case .learningUnavailable:
                model.vocabulary.endObservationFeedback(id: id, reason: "span_missing")
            case .learningPending:
                _ = await model.vocabulary.observe(dictationID: "render", dictated: "we use sunshine",
                    edited: "we use Sunshine42x!", observationID: id)
            case .learningRejected:
                _ = await model.vocabulary.observe(dictationID: "render", dictated: "we use the old version",
                    edited: "we prefer a different approach", observationID: id)
            case .learnedRepo:
                _ = await model.vocabulary.observe(dictationID: "render", dictated: "we use the wrapper",
                    edited: "we use the repo", observationID: id)
            default: break
            }
        }
    }

    /// Dictations over three days, in two languages, delivered three ways,
    /// some with a second of recording.
    private func seedHistory() async {
        let now = Date()
        let hour: TimeInterval = 60 * 60
        let samples: [(TimeInterval, String, Double, String?, String?, DictationHistoryEntry.Delivery, Bool)] = [
            (0.2 * hour, "Let's review the first draft tomorrow and send the slides to the team before Friday.",
             6.4, "en", "com.apple.TextEdit", .inserted, true),
            (1.5 * hour, "明天下午一起看看第一版，确认文字和操作都能自然衔接。", 5.1, "zh", "com.apple.Notes", .inserted, true),
            (3 * hour, "Can we move the call to three o'clock?", 2.6, "en", "com.apple.mail", .clipboard, false),
            (26 * hour, "Thanks for the notes. I'll fold them into the next version and share it this afternoon.",
             7.9, "en", "com.apple.TextEdit", .inserted, false),
            (50 * hour, "Remember to book the meeting room for Tuesday.", 3.2, "en", nil, .recovered, true),
        ]
        for (age, text, seconds, language, app, delivery, recorded) in samples {
            let entry = DictationHistoryEntry(recordedAt: now.addingTimeInterval(-age), text: text,
                                              audioSeconds: seconds, language: language, appBundleID: app,
                                              delivery: delivery)
            _ = try? await historyStore.append(entry, recording: recorded ? Data(count: 32_000) : nil)
        }
    }

    func tearDown() {
        UserDefaults().removePersistentDomain(forName: suiteName)
        try? FileManager.default.removeItem(at: scratch)
    }
}

// ─── Inert seams: nothing below opens a device, a process or a tap ───

private final class RenderCapture: DictationCaptureDevice, @unchecked Sendable {
    var isCurrentlyWarm: Bool { false }
    var isInputStaleWhileWarm: Bool { false }
    var lastCaptureInterruption: DictationCaptureInterruption? { nil }
    var onAudioLevel: (@Sendable (Float) -> Void)?
    var onRetainedAudio: (@Sendable ([Float], Int) -> Void)?
    var onInterruptionDiscardedAudio: (@Sendable () -> Void)?
    var onInterruptionRebound: (@Sendable () -> Void)?
    func start() throws {}
    func warmUp() throws {}
    func armTriggerDown() {}
    func stop(keepWarm: Bool) async -> DictationCapture.CaptureResult {
        DictationCapture.CaptureResult(samples: [Float](repeating: 0.2, count: 16_000), sampleRate: 16_000)
    }
    func coolDown() async {}
    func shouldCoolDown(idleSeconds: Double) -> Bool { false }
    var usesBluetoothInput: Bool { false }
}

private final class RenderWorker: DictationWorker, @unchecked Sendable {
    private var started = false
    var isReady: Bool { started }
    func start() async -> Bool { started = true; return true }
    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        .text("Let's review the first draft tomorrow. 明天下午一起看看第一版，确认文字和操作都能自然衔接。")
    }
    func shutdown() async { started = false }
    func forceTerminate() { started = false }
}

@MainActor
private final class RenderTrigger: DictationHoldTrigger {
    var startResult = true
    var onHoldStart: (() -> Void)?
    var onHoldEnd: (() -> Void)?
    var onModifierFlags: ((CGEventFlags) -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    var onLatchStart: (() -> Void)?
    var onLatchEnd: (() -> Void)?
    func start() -> Bool { startResult }
    func stop() {}
    func update(_ key: DictationHoldKey) {}
}

@MainActor
private final class RenderMouseTrigger: DictationMouseTrigger {
    var onPress: (() -> Void)?
    var onRelease: (() -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    func start() -> Bool { true }
    func stop() {}
    func update(_ button: DictationMouseSideButton) {}
}

@MainActor
private final class RenderKeyWatch: DictationKeyWatch {
    func begin(_ onKeyDown: @escaping @MainActor (UInt16) -> Void) {}
    func end() {}
}

@MainActor
private final class RenderStopTrap: DictationLatchStopTrap {
    func begin(_ onStopKey: @escaping @MainActor () -> Void) -> Bool { true }
    func end() {}
}

private final class RenderCorrectionWatch: DictationCorrectionWatch, @unchecked Sendable {
    func prepare(targetApp: String) async -> DictationCorrectionObserver.PasteSnapshot? { nil }
    func begin(pasted: String, targetApp: String, beforePaste: DictationCorrectionObserver.PasteSnapshot?,
               onRevision: (@Sendable (String) -> Void)?,
               onEnd: @escaping @Sendable (DictationCorrectionWatchEnd) -> Void) {}
    func abandon() {}
}
