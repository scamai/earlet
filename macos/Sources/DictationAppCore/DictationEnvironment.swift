import AppKit
import AVFoundation
import DictationHistory
import DictationLearning
import Foundation
import DictationKit
import DictationIPC
import ServiceManagement

// ─── Resources ───

/// Where the speech resources live: the relocatable Python, the worker
/// runtime and the MLX model.
///
/// `DICTATION_PYTHON`, `DICTATION_RUNTIME` and `DICTATION_MODEL_DIR` override
/// the bundled locations for development (docs/development.md, "Environment
/// overrides"). A package missing any of the three is reported before a
/// worker is spawned.
public struct DictationResources: Sendable, Equatable {
    public let python: String
    public let runtime: String
    public let model: String

    public init(python: String, runtime: String, model: String) {
        self.python = python
        self.runtime = runtime
        self.model = model
    }

    /// Whether a `DICTATION_*` override may point outside the bundle: in a
    /// debug build only.
    ///
    /// A shipped app honouring them would spawn whatever executable a
    /// `launchctl setenv DICTATION_PYTHON` named, as a child of a process
    /// holding Microphone, Accessibility and Input Monitoring. The packager
    /// builds release, so only `swift run` and the tests see `true`.
    #if DEBUG
    public static let overridesMayLeaveTheBundle = true
    #else
    public static let overridesMayLeaveTheBundle = false
    #endif

    /// `overridesMayLeaveTheBundle` is a parameter so a test, which always
    /// builds debug, can resolve the way a shipped app does. An override it
    /// refuses falls back to the bundled location, never to nothing.
    public static func resolve(
        environment: [String: String],
        resourceRoot: URL,
        overridesMayLeaveTheBundle: Bool = DictationResources.overridesMayLeaveTheBundle,
        fileManager: FileManager = .default
    ) -> DictationResources? {
        func location(_ variable: String, bundled: String) -> String {
            guard let override = environment[variable] else { return bundled }
            if overridesMayLeaveTheBundle || isInside(override, root: resourceRoot) {
                return override
            }
            return bundled
        }
        let python = location("DICTATION_PYTHON",
                              bundled: resourceRoot.appendingPathComponent("python/bin/python3").path)
        let runtime = location("DICTATION_RUNTIME",
                               bundled: resourceRoot.appendingPathComponent("runtime").path)
        let model = location("DICTATION_MODEL_DIR",
                             bundled: resourceRoot.appendingPathComponent("model").path)
        guard fileManager.isExecutableFile(atPath: python),
              fileManager.fileExists(atPath: runtime + "/transcription_worker/asr_server.py"),
              fileManager.fileExists(atPath: model + "/config.json"),
              fileManager.fileExists(atPath: model + "/weights.safetensors")
                || fileManager.fileExists(atPath: model + "/model.safetensors")
        else { return nil }
        return DictationResources(python: python, runtime: runtime, model: model)
    }

    /// Whether `path` names something that exists below `root` once `..` and
    /// symbolic links are resolved, so neither `Resources/../../elsewhere` nor
    /// a link inside the bundle that points out of it counts as inside.
    ///
    /// `realpath(3)` rather than `URL.resolvingSymlinksInPath()`, which drops
    /// a leading `/private` only when the shorter path exists and so can
    /// resolve the root and the path differently.
    static func isInside(_ path: String, root: URL) -> Bool {
        guard let resolvedRoot = realPath(root.path), let resolved = realPath(path) else { return false }
        let rootParts = URL(fileURLWithPath: resolvedRoot).pathComponents
        let parts = URL(fileURLWithPath: resolved).pathComponents
        return parts.count > rootParts.count && Array(parts.prefix(rootParts.count)) == rootParts
    }

    private static func realPath(_ path: String) -> String? {
        guard let resolved = realpath(path, nil) else { return nil }
        defer { free(resolved) }
        return String(cString: resolved)
    }
}

// ─── Seams ───

/// A hold interrupted mid-press by the D15 in-hold watchdog: the bound
/// input died, the partial hold was discarded, and the user is asked to say
/// it again. Product-facing shape of `DictationCapture.Interruption`.
public struct DictationCaptureInterruption: Sendable, Equatable {
    /// Log-safe classification: "no-chunks", "stream-stale" or "device-gone".
    public var reason: String
    /// The input the rebind landed on; empty when nothing was reopened.
    public var deviceName: String

    public init(reason: String, deviceName: String) {
        self.reason = reason
        self.deviceName = deviceName
    }
}

/// The capture the model drives: the session's audio source plus the idle
/// cool-down it owns.
public protocol DictationCaptureDevice: DictationAudioSource {
    /// Starts the engine ahead of the trigger and begins filling the
    /// pre-roll ring — the original press-time precursor (`DictationController.
    /// begin`): a warm press loses no first syllable to a cold start.
    func warmUp() throws
    func coolDown() async
    func shouldCoolDown(idleSeconds: Double) -> Bool
    /// The bound input is a Bluetooth microphone, whose cold start is slow
    /// enough to keep it open longer between dictations.
    var usesBluetoothInput: Bool { get }
    /// Warm but bound to an input a fresh start would not choose: the
    /// trigger-time stale-binding check. Only meaningful while warm.
    var isInputStaleWhileWarm: Bool { get }
    /// The just-finished press's D15 interruption, or nil.
    var lastCaptureInterruption: DictationCaptureInterruption? { get }
    /// Per-chunk peak amplitude (0…1) while the engine runs, about 12 Hz.
    /// Set when a hold starts recording, cleared when it ends.
    var onAudioLevel: (@Sendable (Float) -> Void)? { get set }
    /// Retained PCM per chunk, for the live preview session. Set once the
    /// preview is composed; fires only while a hold retains.
    var onRetainedAudio: (@Sendable ([Float], Int) -> Void)? { get set }
    /// D15: the press's audio (and with it the preview run) was discarded
    /// before an interruption rebind.
    var onInterruptionDiscardedAudio: (@Sendable () -> Void)? { get set }
    /// D15: the rebind reopened a capture; a fresh preview run may start.
    var onInterruptionRebound: (@Sendable () -> Void)? { get set }
}

extension DictationCapture: DictationCaptureDevice {
    /// The plain warm-up the model calls: no explicit device, the standing
    /// first-audible budget.
    public func warmUp() throws {
        try warmUp(bindDeviceUID: nil)
    }

    public var usesBluetoothInput: Bool { boundInputTransport == "bluetooth" }

    public var lastCaptureInterruption: DictationCaptureInterruption? {
        lastInterruption().map {
            DictationCaptureInterruption(reason: $0.reason, deviceName: $0.deviceName)
        }
    }
}

/// The ASR worker as the app drives it: the transport, plus a readiness probe
/// and a kill switch that both work while a request is blocked inside it.
public protocol DictationWorker: AnyObject, DictationAsrTransport {
    /// Started and still alive. Never waits on an in-flight request.
    var isReady: Bool { get }
    /// Kills the worker process from any thread without waiting on it.
    func forceTerminate()
    func ping() async -> Bool
}

extension DictationWorker {
    public func ping() async -> Bool { isReady }
}

extension PythonAsrTransport: DictationWorker {}

/// The sessions' ASR: forwards to whichever worker is current.
///
/// A worker can be replaced (killed at a deadline, or after a crash) while a
/// hold is in progress. The hold's session must stay the one that started
/// its capture, so the session talks to this slot rather than to a worker.
///
/// The slot also owns the language: every worker it is given decodes in the
/// language last set here, so a replacement (after a crash, a deadline or an
/// idle release) does not quietly fall back to detection.
final class WorkerSlot: DictationAsrTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var current: (any DictationWorker)?
    private var language: String?
    private var vocabularyScene: String?

    var worker: (any DictationWorker)? {
        get { lock.lock(); defer { lock.unlock() }; return current }
        set {
            lock.lock(); defer { lock.unlock() }
            current = newValue
            newValue?.setLanguageCode(language)
            newValue?.setVocabularyScene(vocabularyScene)
        }
    }

    /// The code every request carries from now on; `nil` detects it.
    var languageCode: String? {
        lock.lock(); defer { lock.unlock() }
        return language
    }

    func start() async -> Bool {
        await worker?.start() ?? false
    }

    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        guard let worker else { return .failed(.workerUnavailable) }
        return await worker.transcribe(samples: samples, sampleRate: sampleRate)
    }

    func transcribeDetailed(samples: [Float], sampleRate: Int,
                            compareBaseline: Bool) async -> DictationAsrDetailedOutcome {
        guard let worker else { return .failed(.workerUnavailable) }
        return await worker.transcribeDetailed(
            samples: samples, sampleRate: sampleRate, compareBaseline: compareBaseline)
    }

    func setVocabularyScene(_ scene: String?) {
        lock.lock(); defer { lock.unlock() }
        vocabularyScene = scene
        current?.setVocabularyScene(scene)
    }

    func setLanguageCode(_ code: String?) {
        lock.lock(); defer { lock.unlock() }
        language = code
        current?.setLanguageCode(code)
    }

    func shutdown() async {
        await worker?.shutdown()
    }
}

/// The live preview rides the same slot as dictation: whichever worker is
/// current gets the stream, and a worker that does not implement the
/// preview surface (a test fake that never grew it) simply opens nothing.
extension WorkerSlot: DictationPreviewTransport {
    func startPreview(languages: [String]) async -> String? {
        guard let preview = worker as? any DictationPreviewTransport else { return nil }
        return await preview.startPreview(languages: languages)
    }

    func pushPreview(
        streamID: String, samples: [Float], sampleRate: Int
    ) async -> DictationPreviewHypothesis? {
        guard let preview = worker as? any DictationPreviewTransport else { return nil }
        return await preview.pushPreview(streamID: streamID, samples: samples, sampleRate: sampleRate)
    }

    func cancelPreview(streamID: String) async {
        await (worker as? any DictationPreviewTransport)?.cancelPreview(streamID: streamID)
    }
}

/// The global hold gesture. Callbacks arrive on the main actor, in the
/// order the events happened.
@MainActor
public protocol DictationHoldTrigger: AnyObject {
    var onHoldStart: (() -> Void)? { get set }
    var onHoldEnd: (() -> Void)? { get set }
    /// Modifier flags of every modifier change, before the hold edge the
    /// same change may produce.
    var onModifierFlags: ((CGEventFlags) -> Void)? { get set }
    /// The listener could not run because Accessibility, Input Monitoring
    /// or both are missing. A refused `start()` reports it here too, with
    /// which grant is missing.
    var onDegraded: ((DictationPermissionStatus) -> Void)? { get set }
    /// The hold key was tapped twice quickly: dictation latches on. The
    /// second press's ordinary hold edges fire too — the model reconciles.
    var onLatchStart: (() -> Void)? { get set }
    /// Another double-tap while latched: finish and transcribe.
    var onLatchEnd: (() -> Void)? { get set }
    /// Installs the global listener; `false` when macOS refused it.
    func start() -> Bool
    func stop()
    /// Listens for a different hold key from now on.
    func update(_ key: DictationHoldKey)
}

/// A spare mouse button beside the hold key: its press and release, which
/// the model reads as a hold, a click that keeps dictation running, or the
/// press that stops it. The listener swallows the bound button, so its
/// usual Back or Forward never fires underneath a dictation. Callbacks
/// arrive on the main actor, in the order the events happened.
@MainActor
public protocol DictationMouseTrigger: AnyObject {
    var onPress: (() -> Void)? { get set }
    var onRelease: (() -> Void)? { get set }
    /// The listener could not run because Accessibility or Input Monitoring
    /// is missing. A refused `start()` reports it here too.
    var onDegraded: ((DictationPermissionStatus) -> Void)? { get set }
    /// Installs the global listener; `false` when macOS refused it.
    func start() -> Bool
    func stop()
    /// Listens for a different button from now on.
    func update(_ button: DictationMouseSideButton)
}

/// Key-down codes while a hold is in progress: Escape cancels, any other
/// key means the hold key was part of a shortcut.
///
/// Installed only for the length of one hold, and it reports the key code
/// alone, never a character.
@MainActor
public protocol DictationKeyWatch: AnyObject {
    func begin(_ onKeyDown: @escaping @MainActor (UInt16) -> Void)
    func end()
}

/// The permission prompts and probes the model needs.
public struct DictationPermissions {
    /// Asks for the microphone (prompting only on first use).
    public var requestMicrophone: @MainActor () async -> Bool
    /// Whether the microphone is already allowed. Never prompts.
    public var microphoneAuthorized: @MainActor () -> Bool
    /// Accessibility trust; `prompt` shows the system dialog when untrusted.
    public var accessibilityTrusted: @MainActor (_ prompt: Bool) -> Bool
    /// Whether the key listener is allowed (Input Monitoring). Never prompts.
    public var inputMonitoringGranted: @MainActor () -> Bool
    /// Registers Dictation in the Input Monitoring list and shows it. Only on
    /// a deliberate user action: without it the user has to find the app
    /// with "+" in System Settings.
    public var requestInputMonitoring: @MainActor () -> Void
    /// Shows the Microphone pane, for when a request already came back
    /// denied and macOS will not prompt again.
    public var openMicrophoneSettings: @MainActor () -> Void
    /// Shows the Accessibility pane, for the same reason.
    public var openAccessibilitySettings: @MainActor () -> Void

    public init(
        requestMicrophone: @escaping @MainActor () async -> Bool,
        microphoneAuthorized: @escaping @MainActor () -> Bool,
        accessibilityTrusted: @escaping @MainActor (_ prompt: Bool) -> Bool,
        inputMonitoringGranted: @escaping @MainActor () -> Bool,
        requestInputMonitoring: @escaping @MainActor () -> Void,
        openMicrophoneSettings: @escaping @MainActor () -> Void,
        openAccessibilitySettings: @escaping @MainActor () -> Void
    ) {
        self.requestMicrophone = requestMicrophone
        self.microphoneAuthorized = microphoneAuthorized
        self.accessibilityTrusted = accessibilityTrusted
        self.inputMonitoringGranted = inputMonitoringGranted
        self.requestInputMonitoring = requestInputMonitoring
        self.openMicrophoneSettings = openMicrophoneSettings
        self.openAccessibilitySettings = openAccessibilitySettings
    }
}

/// Where "Open Dictation at login" stands, as `SMAppService` reports it.
public enum DictationLoginItemStatus: Sendable, Equatable {
    /// Not registered: Dictation does not open at login. The default.
    case off
    /// Registered and allowed.
    case on
    /// Registered, but macOS waits for the user to allow it in System
    /// Settings → General → Login Items.
    case needsApproval
    /// macOS cannot find this copy of the app to register, as with a build
    /// run outside its bundle.
    case unavailable
}

/// Launch at login, through `SMAppService.mainApp`. Nothing registers
/// Dictation unless the user turns the setting on.
public struct DictationLoginItem {
    public var status: @MainActor () -> DictationLoginItemStatus
    /// Throws when macOS refuses to register this copy. An unsigned build is
    /// the expected case; which errors a real refusal carries is unmeasured.
    public var register: @MainActor () throws -> Void
    public var unregister: @MainActor () throws -> Void
    /// Shows System Settings → General → Login Items.
    public var openSettings: @MainActor () -> Void

    public init(
        status: @escaping @MainActor () -> DictationLoginItemStatus,
        register: @escaping @MainActor () throws -> Void,
        unregister: @escaping @MainActor () throws -> Void,
        openSettings: @escaping @MainActor () -> Void
    ) {
        self.status = status
        self.register = register
        self.unregister = unregister
        self.openSettings = openSettings
    }
}

/// How a watch over a pasted span ended.
public struct DictationCorrectionWatchEnd: Sendable, Equatable {
    /// How it ended, as a stable snake_case name for the log
    /// (`DictationTerminalCondition`'s raw value). Never text.
    public var reason: String
    /// The final text of the span Dictation pasted, and nothing else from
    /// the field; `nil` unless the watch ended in a way that may be learned
    /// from with the span still located. A watch that reports its revisions
    /// as they settle never carries it: its end's facts are the two flags.
    public var editedSpan: String?
    /// The span was still located, and read, when the watch ended. Only then
    /// can the end tell an untouched dictation ("No correction detected.")
    /// from a field that never yielded its span ("This field could not be
    /// checked.").
    public var spanObservedToEnd: Bool
    /// The span was seen to differ from the pasted text at least once,
    /// whether the change was kept, reverted or never settled into a
    /// revision.
    public var sawChange: Bool

    public init(reason: String, editedSpan: String?, spanObservedToEnd: Bool = false, sawChange: Bool = false) {
        self.reason = reason
        self.editedSpan = editedSpan
        self.spanObservedToEnd = spanObservedToEnd
        self.sawChange = sawChange
    }
}

/// Watches the span Dictation just pasted for the user's corrections
/// (LEARNING.md §7). The app holds one for its whole life.
public protocol DictationCorrectionWatch: AnyObject, Sendable {
    /// Captures an admitted field before the paste; the snapshot is opaque,
    /// belongs to this attempt only, and is never persisted.
    func prepare(targetApp: String) async -> DictationCorrectionObserver.PasteSnapshot?
    /// Starts watching `pasted`, which was just pasted into the app
    /// `targetApp`, and ends any earlier watch. Returns at once; `onEnd`
    /// fires exactly once, on any thread.
    func begin(pasted: String, targetApp: String, beforePaste: DictationCorrectionObserver.PasteSnapshot?,
               onRevision: (@Sendable (String) -> Void)?,
               onEnd: @escaping @Sendable (DictationCorrectionWatchEnd) -> Void)
    /// Ends the current watch without a span: pause, quit, the next
    /// dictation, the learning switch.
    func abandon()
}

/// Everything `DictationModel` touches outside itself. `live()` is the
/// product; tests pass fakes for every field.
public struct DictationEnvironment {
    public var settings: DictationSettings
    public var timing: DictationTiming
    /// Monotonic time for the hold key's trailing-tap suppression window.
    /// Tests advance this independently of transcription and actor scheduling.
    public var holdKeyUptime: @MainActor () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }
    public var capture: any DictationCaptureDevice
    public var permissions: DictationPermissions
    public var resolveResources: @MainActor () -> DictationResources?
    public var makeWorker: @MainActor (DictationResources) -> any DictationWorker
    public var makeTrigger: @MainActor (DictationHoldKey) -> any DictationHoldTrigger
    /// Whether macOS would register a recorded key combination as a hot
    /// key, asked when the user records one.
    public var canReserveShortcut: @MainActor (DictationKeyShortcut) -> Bool = {
        ShortcutHotKeyCenter.shared.canRegister($0)
    }
    /// The mouse button's listener, made only once the user has chosen one.
    public var makeMouseTrigger: @MainActor (DictationMouseSideButton) -> any DictationMouseTrigger
    public var keyWatch: any DictationKeyWatch
    /// The commit-key (Return/Enter) trap, installed only while a latch
    /// records: it consumes the key, so the stop signal never also types a
    /// newline or sends the half-written message.
    public var stopTrap: any DictationLatchStopTrap
    /// Asked at every press, before any audio is captured: Secure Input or a
    /// focused password field refuses the dictation outright.
    public var privacyGate: @MainActor () -> DictationInsertFailure?
    public var insertText: DictationSession.TextInsertion
    /// Puts text the paste could not deliver on the clipboard; returns
    /// whether it landed.
    public var clipboardFallback: @MainActor (String) async -> Bool
    /// Returns once a paste's delayed clipboard restore has landed.
    public var settleClipboard: @Sendable () async -> Void
    /// The bundle identifier of the app about to receive the text, for the
    /// formatter's per-app profile (terminals get no formatting). Asked once
    /// per dictation, off the main actor, after the transcription.
    public var targetApplication: @Sendable () -> String?
    /// Cheap frontmost-app metadata for recognition; insertion still uses AX.
    public var vocabularyApplication: @Sendable () -> String?
    /// Opens `~/Library/Logs/Dictation` in Finder; `false` when it could not.
    public var openLogFolder: @MainActor () -> Bool
    /// Quits and reopens the app: a helper reopens the bundle once this
    /// process has exited (macOS applies an Input Monitoring grant only to a
    /// fresh process).
    public var relaunch: @MainActor () -> Void
    public var loginItem: DictationLoginItem
    /// Watches each paste for corrections while learning is on.
    public var correctionWatch: any DictationCorrectionWatch
    /// The learned vocabulary and `learning.json`, or `nil` when there is
    /// nowhere to keep it (no Application Support folder). The worker reads
    /// the same file on every transcription; the host never sends the words.
    public var learning: DictationLearning?
    /// Diagnostics: classifications and raw failure detail, never dictated
    /// text. The status line carries the user-facing sentence instead.
    public var log: @Sendable (String) -> Void
    /// Long takes are never lost: where each take's audio is spooled while
    /// it records, how long a take that could not be transcribed is kept for
    /// Recover Last Dictation, and how the spool's file work runs
    /// (`DictationTakeSpool`). Tests point it at a scratch directory with
    /// synchronous file work.
    public var keySnapshot: @MainActor () async -> DictationKeySnapshot
    public var isDictationFrontmost: @MainActor () -> Bool
    /// This app's own bundle identifier. A dictation into its own window
    /// (the practice field, whose caption says its text is not saved, or a
    /// field in Vocabulary or History) is not a delivery the history keeps.
    public var ownBundleID: String?
    public var playCaution: @MainActor () -> Void
    public var takes: DictationTakeOptions
    /// The dictation history (`DictationHistoryStore`), or nil to keep none:
    /// tests leave it out unless they test it, and the render mode gives it
    /// a scratch folder with a key in memory.
    public var history: DictationHistoryStore?

    public init(
        settings: DictationSettings,
        capture: any DictationCaptureDevice,
        permissions: DictationPermissions,
        resolveResources: @escaping @MainActor () -> DictationResources?,
        makeWorker: @escaping @MainActor (DictationResources) -> any DictationWorker,
        makeTrigger: @escaping @MainActor (DictationHoldKey) -> any DictationHoldTrigger,
        makeMouseTrigger: @escaping @MainActor (DictationMouseSideButton) -> any DictationMouseTrigger,
        keyWatch: any DictationKeyWatch,
        stopTrap: any DictationLatchStopTrap,
        privacyGate: @escaping @MainActor () -> DictationInsertFailure?,
        insertText: @escaping DictationSession.TextInsertion,
        clipboardFallback: @escaping @MainActor (String) async -> Bool,
        settleClipboard: @escaping @Sendable () async -> Void,
        targetApplication: @escaping @Sendable () -> String?,
        vocabularyApplication: (@Sendable () -> String?)? = nil,
        openLogFolder: @escaping @MainActor () -> Bool,
        relaunch: @escaping @MainActor () -> Void,
        loginItem: DictationLoginItem,
        correctionWatch: any DictationCorrectionWatch,
        learning: DictationLearning?,
        log: @escaping @Sendable (String) -> Void = { _ in },
        timing: DictationTiming = DictationTiming(),
        takes: DictationTakeOptions = DictationTakeOptions(),
        history: DictationHistoryStore? = nil,
        playCaution: @escaping @MainActor () -> Void = {},
        keySnapshot: @escaping @MainActor () async -> DictationKeySnapshot = {
            .init(time: ProcessInfo.processInfo.systemUptime, recorders: .init(available: false))
        },
        isDictationFrontmost: @escaping @MainActor () -> Bool = { false },
        ownBundleID: String? = nil
    ) {
        self.settings = settings
        self.timing = timing
        self.capture = capture
        self.permissions = permissions
        self.resolveResources = resolveResources
        self.makeWorker = makeWorker
        self.makeTrigger = makeTrigger
        self.makeMouseTrigger = makeMouseTrigger
        self.keyWatch = keyWatch
        self.stopTrap = stopTrap
        self.privacyGate = privacyGate
        self.insertText = insertText
        self.clipboardFallback = clipboardFallback
        self.settleClipboard = settleClipboard
        self.targetApplication = targetApplication
        self.vocabularyApplication = vocabularyApplication ?? targetApplication
        self.openLogFolder = openLogFolder
        self.relaunch = relaunch
        self.loginItem = loginItem
        self.correctionWatch = correctionWatch
        self.learning = learning
        self.log = log
        self.takes = takes
        self.history = history
        self.playCaution = playCaution
        self.keySnapshot = keySnapshot
        self.isDictationFrontmost = isDictationFrontmost
        self.ownBundleID = ownBundleID
    }
}

// ─── Live implementations ───

extension DictationEnvironment {
    /// The product wiring. `sink` receives the app's lines and the worker's
    /// stderr while it starts (`WorkerStartupLog`); the app starts it at
    /// launch.
    @MainActor
    public static func live(sink: DictationLogSink = .shared) -> DictationEnvironment {
        let inserter = TextInserter()
        let learning: DictationLearning?
        do {
            learning = try DictationLearning.live()
        } catch {
            learning = nil
            sink.log("learning unavailable: \(error)", subsystem: "dictation")
        }
        return DictationEnvironment(
            settings: .live(),
            capture: DictationCapture(),
            permissions: .live,
            resolveResources: {
                DictationResources.resolve(
                    environment: ProcessInfo.processInfo.environment,
                    resourceRoot: Bundle.main.resourceURL ?? Bundle.main.bundleURL)
            },
            makeWorker: { resources in
                DictationEnvironment.makeLiveWorker(resources, stderrSink: { sink.log($0, subsystem: "asr") })
            },
            makeTrigger: { key in LiveHoldTrigger(key: key) },
            makeMouseTrigger: { button in MouseButtonTrigger(button: button) },
            keyWatch: NSEventKeyWatch(),
            stopTrap: CGEventLatchStopTrap(),
            privacyGate: { TextInserter.dictationStartPrivacyGate() },
            insertText: { text in await inserter.insertMeasured(text) },
            clipboardFallback: { text in await TextInserter.preserveOnClipboard(text) },
            settleClipboard: { await TextInserter.settlePendingClipboardRestore() },
            targetApplication: { FrontmostApplicationResolver.resolve().bundleID },
            vocabularyApplication: { NSWorkspace.shared.frontmostApplication?.bundleIdentifier },
            openLogFolder: {
                let folder = sink.activeFileURL?.deletingLastPathComponent()
                    ?? DictationLogSink.defaultDirectory()
                try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                return NSWorkspace.shared.open(folder)
            },
            relaunch: { RelaunchHelper.relaunch(log: { sink.log($0, subsystem: "dictation") }) },
            loginItem: .live,
            correctionWatch: AccessibilityCorrectionWatch(log: { sink.log($0, subsystem: "dictation") }),
            learning: learning,
            log: { sink.log($0, subsystem: "dictation") },
            // The keychain service is the app's defaults domain and
            // `.history`; the history folder sits beside the recovery one.
            history: DictationHistoryStore(
                directory: DictationHistoryStore.defaultDirectory(),
                keys: KeychainHistoryKeys(service: DictationSettings.domain + ".history"),
                log: { sink.log($0, subsystem: "dictation") }),
            playCaution: { NSSound(named: "Tink")?.play() },
            keySnapshot: { await DictationKeySnapshot.live() },
            isDictationFrontmost: { NSWorkspace.shared.frontmostApplication?.processIdentifier == ProcessInfo.processInfo.processIdentifier },
            ownBundleID: Bundle.main.bundleIdentifier)
    }

    /// The one construction point for the product's worker. `stderrSink`
    /// receives the worker's stderr (import errors, tracebacks, a model
    /// that would not load) and the transport's own failure lines while the
    /// worker starts; without it a failed start said only "could not load".
    /// Once the worker has started, nothing more reaches it: see
    /// `WorkerStartupLog`.
    ///
    /// `wireClient` replaces the process with a scripted wire, so a test can
    /// drive the product's own worker type instead of a fake that may have
    /// grown a surface the product type never did.
    public static func makeLiveWorker(
        _ resources: DictationResources, stderrSink: @escaping @Sendable (String) -> Void,
        wireClient: (any AsrWireClient)? = nil
    ) -> any DictationWorker {
        let startupLog = WorkerStartupLog(sink: stderrSink)
        let transport = PythonAsrTransport(
            pythonPath: resources.python, workingDirectory: resources.runtime,
            modelDir: resources.model, hfId: resources.model, allowModelDownload: false,
            environment: ["DICTATION_ASR_ENGINE": "mlx", "HF_HUB_OFFLINE": "1",
                          "TRANSFORMERS_OFFLINE": "1", "OMP_NUM_THREADS": "4",
                          "OPENBLAS_NUM_THREADS": "1", "MKL_NUM_THREADS": "1"],
            wireClient: wireClient,
            stderrSink: { startupLog.forward($0) })
        return LiveWorker(transport: transport, startupLog: startupLog)
    }
}

// ─── Quit and reopen ───

/// Terminates this process and reopens the bundle once it has exited. The
/// detached helper waits on the pid before `open`, so the fresh process
/// starts only after this one is gone — which is the point: macOS applies
/// an Input Monitoring grant accepted mid-session only to a fresh process.
enum RelaunchHelper {
    /// The helper's whole script. The pid, the app's path and the opener
    /// arrive as positional parameters (`$1`, `$2`, `$3`) and are never
    /// pasted into the text, so an app folder whose name holds a quote,
    /// `$(…)` or a backtick is a path for `open`, not shell for `sh` to run.
    static let script = #"while kill -0 "$1" 2>/dev/null; do sleep 0.2; done; exec "$3" -n -a "$2""#

    /// `/bin/sh`'s arguments; `dictation-relaunch` is the helper's `$0`.
    static func arguments(pid: Int32, bundlePath: String, opener: String = "/usr/bin/open") -> [String] {
        ["-c", script, "dictation-relaunch", String(pid), bundlePath, opener]
    }

    /// Starts the helper, and quits only once it is running. A helper that
    /// never started would leave the user with no Dictation and nothing to
    /// bring it back; staying open keeps the reopen row in front of them, and
    /// the log says why the button did nothing. Returns whether it quit.
    @MainActor
    @discardableResult
    static func relaunch(
        pid: Int32 = ProcessInfo.processInfo.processIdentifier,
        bundlePath: String = Bundle.main.bundlePath,
        shell: String = "/bin/sh",
        opener: String = "/usr/bin/open",
        terminate: @MainActor () -> Void = { NSApplication.shared.terminate(nil) },
        log: (String) -> Void
    ) -> Bool {
        let helper = Process()
        helper.executableURL = URL(fileURLWithPath: shell)
        helper.arguments = arguments(pid: pid, bundlePath: bundlePath, opener: opener)
        do {
            try helper.run()
        } catch {
            log("quit and reopen: the helper did not start (\(error)); Dictation stays open")
            return false
        }
        terminate()
        return true
    }
}

// ─── The worker's stderr ───

/// Lets a worker's stderr into the log only while the worker starts.
///
/// A failed start is diagnosed from what the worker printed (an import
/// error, a traceback, a model that would not load), so those lines must
/// reach `~/Library/Logs/Dictation`. Once the model is loaded, the worker's
/// per-dictation diagnostics can quote the user: `spoken_aliases` prints the
/// written forms it applied, which are words from the user's vocabulary, and
/// the log promises never to hold what was said. Windows makes the same cut
/// (`DictationWorker.StartupFinished` in `Dictation.Core`). It makes it after
/// any first load attempt; here only a start that succeeded closes the log,
/// because stderr is drained apart from the reply, and a traceback can land
/// after the failed `init` has already been reported.
final class WorkerStartupLog: @unchecked Sendable {
    private let lock = NSLock()
    private let sink: @Sendable (String) -> Void
    private var open = true
    private var withheld = 0

    init(sink: @escaping @Sendable (String) -> Void) {
        self.sink = sink
    }

    /// One chunk of stderr: logged while the worker starts, dropped after.
    func forward(_ text: String) {
        lock.lock(); defer { lock.unlock() }
        if open {
            sink(text)
        } else {
            withheld += 1
        }
    }

    /// A worker is about to be spawned; its start is logged again.
    func reopen() {
        lock.lock(); defer { lock.unlock() }
        open = true
    }

    /// The worker has started. Says so once, so a reader of the log knows
    /// why its output stops there.
    func close() {
        lock.lock(); defer { lock.unlock() }
        guard open else { return }
        open = false
        sink("worker started; its later output is not logged\n")
    }

    /// Chunks dropped since startup: a count, never their text.
    var withheldChunks: Int {
        lock.lock(); defer { lock.unlock() }
        return withheld
    }
}

/// The product's worker: the Python transport, with its stderr logged only
/// while it starts.
final class LiveWorker: DictationWorker, @unchecked Sendable {
    let transport: PythonAsrTransport
    let startupLog: WorkerStartupLog

    init(transport: PythonAsrTransport, startupLog: WorkerStartupLog) {
        self.transport = transport
        self.startupLog = startupLog
    }

    var isReady: Bool { transport.isReady }
    func ping() async -> Bool { await transport.ping() }

    func forceTerminate() { transport.forceTerminate() }

    func start() async -> Bool {
        // Not running means this start spawns a process, and a spawn is what
        // the log is for. A ready worker's start returns at once and must
        // not reopen the log in the middle of a dictation.
        if !transport.isReady { startupLog.reopen() }
        let started = await transport.start()
        if started { startupLog.close() }
        return started
    }

    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        await transport.transcribe(samples: samples, sampleRate: sampleRate)
    }

    func transcribeDetailed(samples: [Float], sampleRate: Int,
                            compareBaseline: Bool) async -> DictationAsrDetailedOutcome {
        await transport.transcribeDetailed(
            samples: samples, sampleRate: sampleRate, compareBaseline: compareBaseline)
    }

    func setLanguageCode(_ code: String?) { transport.setLanguageCode(code) }
    func setVocabularyScene(_ scene: String?) { transport.setVocabularyScene(scene) }

    func shutdown() async { await transport.shutdown() }
}

/// The live preview's stream, forwarded to the transport that speaks it.
///
/// `WorkerSlot` reaches the preview by casting the current worker to
/// `DictationPreviewTransport`, and this worker is the only one the product
/// builds. Before it adopted the protocol the cast failed on every install:
/// no `stream_start` was ever sent and the HUD stayed on "Listening", while
/// the model tests passed because their fake worker adopts it. The app never had
/// the gap because it casts the Python transport itself.
extension LiveWorker: DictationPreviewTransport {
    func startPreview(languages: [String]) async -> String? {
        await transport.startPreview(languages: languages)
    }

    func pushPreview(
        streamID: String, samples: [Float], sampleRate: Int
    ) async -> DictationPreviewHypothesis? {
        await transport.pushPreview(streamID: streamID, samples: samples, sampleRate: sampleRate)
    }

    func cancelPreview(streamID: String) async {
        await transport.cancelPreview(streamID: streamID)
    }
}

/// `DictationCorrectionObserver` behind `DictationCorrectionWatch`: the
/// Accessibility observer, one for the life of the app.
final class AccessibilityCorrectionWatch: DictationCorrectionWatch {
    let observer: DictationCorrectionObserver

    init(log: @escaping @Sendable (String) -> Void = { _ in }) {
        observer = DictationCorrectionObserver(diagnostic: { log("learning: \($0.logSummary)") })
    }

    func prepare(targetApp: String) async -> DictationCorrectionObserver.PasteSnapshot? {
        await observer.preparePaste(expectedBundleID: targetApp)
    }

    func begin(pasted: String, targetApp: String, beforePaste: DictationCorrectionObserver.PasteSnapshot?,
               onRevision: (@Sendable (String) -> Void)?,
               onEnd: @escaping @Sendable (DictationCorrectionWatchEnd) -> Void) {
        observer.beginObservingFocusedField(pasted: pasted, expectedBundleID: targetApp, beforePaste: beforePaste, onRevision: onRevision) { result in
            // How it ended and the span; the rest of the observation stays here.
            onEnd(AccessibilityCorrectionWatch.end(result))
        }
    }

    func abandon() { observer.abandon() }

    static func end(_ result: DictationObservationResult) -> DictationCorrectionWatchEnd {
        DictationCorrectionWatchEnd(reason: result.observation.terminalCondition.rawValue,
                                    editedSpan: result.observation.editedText,
                                    spanObservedToEnd: result.observation.spanObservedToEnd,
                                    sawChange: result.observation.sawChange)
    }
}

extension DictationPermissions {
    public static var live: DictationPermissions { DictationPermissions(
        requestMicrophone: { await AVCaptureDevice.requestAccess(for: .audio) },
        microphoneAuthorized: { AVCaptureDevice.authorizationStatus(for: .audio) == .authorized },
        accessibilityTrusted: { prompt in
            // The documented CFString value of kAXTrustedCheckOptionPrompt;
            // Swift 6 rejects the imported mutable C global.
            let options = ["AXTrustedCheckOptionPrompt": prompt] as CFDictionary
            return AXIsProcessTrustedWithOptions(options)
        },
        inputMonitoringGranted: { CGPreflightListenEventAccess() },
        requestInputMonitoring: {
            _ = CGRequestListenEventAccess()
            if let pane = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent") {
                NSWorkspace.shared.open(pane)
            }
        },
        openMicrophoneSettings: {
            if let pane = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone") {
                NSWorkspace.shared.open(pane)
            }
        },
        openAccessibilitySettings: {
            if let pane = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
                NSWorkspace.shared.open(pane)
            }
        })
    }
}

extension DictationLoginItem {
    public static var live: DictationLoginItem { DictationLoginItem(
        status: {
            switch SMAppService.mainApp.status {
            case .enabled: return .on
            case .requiresApproval: return .needsApproval
            case .notFound: return .unavailable
            case .notRegistered: return .off
            @unknown default: return .off
            }
        },
        register: { try SMAppService.mainApp.register() },
        unregister: { try SMAppService.mainApp.unregister() },
        openSettings: { SMAppService.openSystemSettingsLoginItems() })
    }
}

/// `DictationHotkey` behind `DictationHoldTrigger`.
@MainActor
final class HotkeyHoldTrigger: DictationHoldTrigger {
    var onHoldStart: (() -> Void)?
    var onHoldEnd: (() -> Void)?
    var onModifierFlags: ((CGEventFlags) -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    var onLatchStart: (() -> Void)?
    var onLatchEnd: (() -> Void)?
    let hotkey: DictationHotkey

    init(key: DictationModifierKey) {
        hotkey = DictationHotkey(config: key.hotkeyConfig)
        hotkey.onModifierFlags = { [weak self] flags in
            Self.deliver { self?.onModifierFlags?(flags) }
        }
        hotkey.onPushToTalkStart = { [weak self] in
            Self.deliver { self?.onHoldStart?() }
        }
        hotkey.onPushToTalkEnd = { [weak self] in
            Self.deliver { self?.onHoldEnd?() }
        }
        hotkey.onDegraded = { [weak self] status in
            Self.deliver { self?.onDegraded?(status) }
        }
        hotkey.onDoubleTapActivate = { [weak self] in
            Self.deliver { self?.onLatchStart?() }
        }
        hotkey.onDoubleTapDeactivate = { [weak self] in
            Self.deliver { self?.onLatchEnd?() }
        }
    }

    /// Synchronously when already on the main thread, which is where the tap
    /// runs (its source is on the main run loop).
    ///
    /// A `Task` hop per callback would let a key-down reach the key watch
    /// before the hold that should be watching for it had started, and a fast
    /// Right Command + C would dictate instead of copying.
    nonisolated private static func deliver(_ body: @escaping @MainActor () -> Void) {
        if Thread.isMainThread {
            MainActor.assumeIsolated { body() }
        } else {
            Task { @MainActor in body() }
        }
    }

    func start() -> Bool { hotkey.start() }
    func stop() { hotkey.stop() }
    /// Another modifier key; a recorded combination is `LiveHoldTrigger`'s
    /// to swap in.
    func update(_ key: DictationHoldKey) {
        guard let modifier = key.modifierKey else { return }
        hotkey.updateConfig(modifier.hotkeyConfig)
    }
}

/// `DictationMouseButton` behind `DictationMouseTrigger`.
@MainActor
final class MouseButtonTrigger: DictationMouseTrigger {
    var onPress: (() -> Void)?
    var onRelease: (() -> Void)?
    var onDegraded: ((DictationPermissionStatus) -> Void)?
    let mouse = DictationMouseButton()

    init(button: DictationMouseSideButton) {
        mouse.updateBinding(button.binding)
        mouse.onPress = { [weak self] in
            Self.deliver { self?.onPress?() }
        }
        mouse.onRelease = { [weak self] in
            Self.deliver { self?.onRelease?() }
        }
        mouse.onDegraded = { [weak self] status in
            Self.deliver { self?.onDegraded?(status) }
        }
    }

    /// Synchronously on the main thread, where the tap runs, for the reason
    /// `HotkeyHoldTrigger.deliver` gives: a press and its release must reach
    /// the model in the order they happened.
    nonisolated private static func deliver(_ body: @escaping @MainActor () -> Void) {
        if Thread.isMainThread {
            MainActor.assumeIsolated { body() }
        } else {
            Task { @MainActor in body() }
        }
    }

    func start() -> Bool { mouse.start() }
    func stop() { mouse.stop() }
    func update(_ button: DictationMouseSideButton) { mouse.updateBinding(button.binding) }
}

/// Global and local `NSEvent` key-down monitors, for the length of a hold.
///
/// Two monitors because a global one never sees this app's own events, and
/// the user may hold the key with Dictation's window focused. Neither
/// consumes the event: Escape and the shortcut still do what they did.
@MainActor
final class NSEventKeyWatch: DictationKeyWatch {
    private var monitors: [Any] = []

    func begin(_ onKeyDown: @escaping @MainActor (UInt16) -> Void) {
        end()
        if let global = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: { event in
            guard !event.isARepeat else { return }
            let code = event.keyCode
            MainActor.assumeIsolated { onKeyDown(code) }
        }) {
            monitors.append(global)
        }
        if let local = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { event in
            guard !event.isARepeat else { return event }
            let code = event.keyCode
            MainActor.assumeIsolated { onKeyDown(code) }
            return event
        }) {
            monitors.append(local)
        }
    }

    func end() {
        for monitor in monitors { NSEvent.removeMonitor(monitor) }
        monitors.removeAll()
    }
}
