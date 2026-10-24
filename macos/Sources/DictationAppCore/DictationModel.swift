import Combine
import CoreGraphics
import DictationLearning
import Foundation
import DictationAudioCapture
import DictationKit

/// The standalone app's state and behaviour: enable/pause, one hold at a
/// time, and the status line. Views only render it; everything it touches
/// outside itself comes from `DictationEnvironment`, so tests drive it with
/// fakes.
@MainActor
public final class DictationModel: ObservableObject {
    @Published public internal(set) var status = DictationCopy.readyToEnable
    @Published public private(set) var enabled = false
    @Published public internal(set) var busy = false
    @Published public private(set) var recording = false
    /// The hold key was double-tapped: dictation stays on until one press of
    /// that key, Return, Escape or Cancel — a hold whose end arrives later.
    @Published public private(set) var latched = false
    /// Released and not yet inserted: the words are waiting for the engine or
    /// being transcribed. Escape and Cancel still work until the paste begins.
    /// Raised a moment before `recording` drops, so the two overlap for the
    /// instant of a release and are never both false in between.
    @Published public internal(set) var transcribing = false
    /// The key held to dictate. Change it with `selectHoldKey(_:)`.
    @Published public private(set) var holdKey: DictationHoldKey
    /// Shown under the key picker when the key collides with something.
    @Published public private(set) var caution: String?
    /// The language dictation listens for. Change it with `selectLanguage(_:)`.
    @Published public private(set) var language: DictationLanguage
    /// Whether macOS opens Dictation at login. Off until the user turns it on.
    @Published public private(set) var launchAtLogin = false
    /// Shown under the launch-at-login toggle when macOS did not simply do
    /// what was asked.
    @Published public private(set) var loginItemNote: String?
    /// "Learn from my corrections". Change it with `setLearningEnabled(_:)`.
    @Published public private(set) var learningEnabled: Bool
    @Published public internal(set) var hasCompletedFirstDictation: Bool
    /// Live permission state, re-read by `refreshPermissions()`. The
    /// checklist shows until all three are granted.
    @Published public private(set) var microphoneGranted = false
    @Published public private(set) var accessibilityGranted = false
    @Published public private(set) var inputMonitoringGranted = false
    /// The shortcut listener was refused, even if the permission probes say
    /// access is allowed. A stale grant can belong to an older signed copy;
    /// this process needs a restart before retrying the listener.
    @Published public private(set) var reopenNeeded = false
    @Published public private(set) var permissionRequestInProgress = false
    @Published public private(set) var permissionCheckMessage: String?
    /// The speech engine failed to load or start. Kept separate from the
    /// status sentence so the menu glyph never depends on string matching.
    @Published public internal(set) var engineFailed = false
    /// Bumped when the key listener reports a missing grant, which the window
    /// must show while it may be closed or behind. The app delegate watches it
    /// (`DictationWindowAttention`, with the grants themselves) and brings the
    /// window forward on each change.
    @Published public internal(set) var windowAttention = 0
    /// The mic-changed sentence after an interrupted hold (D15), for the
    /// floating HUD. Ephemeral: cleared after `micChangedNoticeSeconds`,
    /// or when the next press begins.
    @Published public private(set) var micChangedNotice: String?
    /// The smoothed live input level (0…1) while a hold records, for the
    /// HUD's waveform. 0 outside a hold.
    @Published public private(set) var inputLevel: Float = 0
    /// The live preview's windowed caption while a hold records (display
    /// only, never inserted). Cleared when the hold ends.
    @Published public private(set) var livePreviewText = ""
    /// How the last dictation ended, for the floating HUD: set beside the
    /// status line from the same decision, so the two never disagree.
    /// Ephemeral: cleared after the outcome's dwell, or when the next hold
    /// is admitted. Never holds the dictated words.
    @Published public private(set) var hudOutcome: DictationHUDOutcome?
    /// When the recording hold's ceiling clock started (capture up), for
    /// the HUD's elapsed time and its countdown to `maximumHoldSeconds`.
    /// `nil` outside a recording.
    @Published public private(set) var holdStartedAt: Date?
    /// The Vocabulary section: the list, teaching, removing.
    public let vocabulary: DictationVocabularyModel
    public let recentResult: DictationRecentResult
    public let keyCheck: DictationKeyCheckModel
    /// The dictation history: every delivered dictation, sealed on disk
    /// (`DictationHistoryModel`).
    public let history: DictationHistoryModel

    /// The take kept for Recover Last Dictation, or nil (rule zero: a take
    /// that could not be transcribed keeps its recording; a quit or crash
    /// mid-take is offered at the next launch).
    @Published public internal(set) var savedTake: DictationKeptTake?
    /// A recovery is reading and transcribing the saved take.
    @Published public internal(set) var recoveringSavedTake = false
    @Published public internal(set) var pendingRecordingCleanup = false

    /// What the floating HUD shows, re-resolved on every change of a value
    /// it reads. One pipeline for the HUD controller and the tests that pin
    /// the sequence a dictation produces.
    public var hudStates: AnyPublisher<DictationHUDState, Never> {
        Publishers.CombineLatest(
            Publishers.CombineLatest4($recording, $transcribing, $latched, $micChangedNotice),
            $hudOutcome)
            .map { live, outcome in
                DictationHUDContent.resolve(
                    recording: live.0, transcribing: live.1, latched: live.2,
                    micChangedNotice: live.3, outcome: outcome)
            }
            .eraseToAnyPublisher()
    }

    /// Every grant dictation needs; the window hides its checklist then.
    public var allPermissionsGranted: Bool {
        microphoneGranted && accessibilityGranted && inputMonitoringGranted
    }

    /// The longest hold; at this many seconds after `holdStartedAt` the
    /// recording finishes on its own.
    public var maximumHoldSeconds: TimeInterval { env.timing.maximumHoldSeconds }

    /// Whether Cancel has anything to cancel: a hold that is recording, or
    /// one whose words are still on their way.
    public var canCancel: Bool { recording || transcribing }

    var isQuitting = false
    var changingSavedTake = false
    @Published public internal(set) var silenceSetting: DictationSilenceSetting
    @Published public internal(set) var holdNotice: String?
    @Published public internal(set) var confirmingDiscard = false
    var holdMonitor: Task<Void, Never>?
    var holdHealth: Task<Void, Never>?
    var recoveryExpiry: Task<Void, Never>?
    let env: DictationEnvironment
    private var trigger: (any DictationHoldTrigger)?
    private var maximumHold: Task<Void, Never>?
    private var housekeeping: Task<Void, Never>?

    // ─── The take spool ───
    //
    // Every take's audio is spooled to disk as it is recorded
    // (`DictationTakeSpool`), deleted once its words went in, and kept when
    // it could not be transcribed, for Recover Last Dictation.
    /// The live take and its spool as the audio tap sees them: the capture's
    /// collector task reads them off the main actor.
    let takeTap = DictationTakeTap()
    /// A recovery's words, waiting in the recent result to reach the
    /// clipboard, and the kept take they came from, which stays on disk
    /// until then: the recent result is one slot, and the next dictation's
    /// result replaces it, so until the words are copied the recording is
    /// their only other copy (rule zero).
    var recovered: (entryID: UUID, take: DictationKeptTake)?
    /// The preview half of the retained-audio tap: the tap is installed once
    /// and the preview is composed later (or never, when its gate is off).
    private let previewForwarder = DictationPreviewForwarder()

    public init(environment: DictationEnvironment) {
        self.env = environment
        self.silenceSetting = environment.settings.silenceSetting
        self.holdKey = environment.settings.holdKey
        self.language = environment.settings.language
        self.learningEnabled = environment.settings.learningEnabled
        self.hasCompletedFirstDictation = environment.settings.hasCompletedFirstDictation
        self.vocabulary = DictationVocabularyModel(learning: environment.learning, log: environment.log)
        self.recentResult = DictationRecentResult(writeClipboard: environment.clipboardFallback)
        self.history = DictationHistoryModel(store: environment.history, settings: environment.settings,
                                             writeClipboard: environment.clipboardFallback, log: environment.log)
        self.keyCheck = DictationKeyCheckModel(settings: environment.settings,
            makeTrigger: environment.makeTrigger, snapshot: environment.keySnapshot,
            isFrontmost: environment.isDictationFrontmost)
        keyCheck.onRuntimeNotice = { [weak self] _ in
            guard let self, !self.recording, !self.busy,
                  let notice = self.keyCheck.takeRuntimeNotice() else { return }
            self.status += " " + notice
        }
        refreshCaution()
        refreshLaunchAtLogin()
    }

    /// Persists the key and moves a live trigger onto it at once.
    public func selectHoldKey(_ key: DictationHoldKey) {
        guard !recording, !busy, attempt == nil else { return }
        env.settings.holdKey = key
        holdKey = key
        trigger?.update(key)
        keyCheck.select(key)
        if env.isDictationFrontmost() { keyCheck.startTest() }
        refreshCaution()
        if enabled { status = "Enabled. Hold \(key.name) in a text field." }
    }

    /// Refresh the current key-conflict diagnostic for the selected binding.
    public func refreshCaution() {
        caution = holdKey.keyNote
    }

    /// Persists the language. The worker gets it when the next hold starts
    /// (`holdStarted`), so a dictation already under way keeps the language
    /// it began with.
    public func selectLanguage(_ language: DictationLanguage) {
        env.settings.language = language
        self.language = language
    }

    // ─── Around the app ───

    /// Opens the folder holding `dictation.log`, which the failure copy
    /// points at.
    public func openLogFolder() {
        guard env.openLogFolder() else {
            status = "The log folder could not be opened. It is ~/Library/Logs/Dictation."
            return
        }
    }

    /// Re-reads launch at login from macOS: the user can also change it in
    /// System Settings, so it is never cached in the app's own settings.
    public func refreshLaunchAtLogin() {
        showLoginItem(env.loginItem.status())
    }

    /// Registers or unregisters Dictation as a login item. Nothing else in
    /// the app does: the setting is off until the user turns it on here.
    public func setLaunchAtLogin(_ on: Bool) {
        let item = env.loginItem
        do {
            if on { try item.register() } else { try item.unregister() }
        } catch {
            env.log("launch at login: \(on ? "register" : "unregister") refused: \(error)")
            showLoginItem(item.status())
            loginItemNote = on
                ? "macOS did not let Earlet open at login. If this copy is not signed, install the signed Earlet app and try again."
                : "macOS did not remove Earlet from the login items. Remove it in System Settings → General → Login Items."
            return
        }
        let status = item.status()
        showLoginItem(status)
        if on, status == .needsApproval { item.openSettings() }
    }

    private func showLoginItem(_ status: DictationLoginItemStatus) {
        switch status {
        case .on:
            launchAtLogin = true
            loginItemNote = nil
        case .needsApproval:
            launchAtLogin = true
            loginItemNote = "Allow Earlet in System Settings → General → Login Items to finish."
        case .off, .unavailable:
            launchAtLogin = false
            loginItemNote = nil
        }
    }

    // ─── Permissions ───

    public var permissionStep: DictationPermissionStep {
        .resolve(microphone: microphoneGranted, accessibility: accessibilityGranted,
                 inputMonitoring: inputMonitoringGranted, restart: reopenNeeded)
    }

    /// One deliberate click requests one permission. Returning from Settings
    /// advances the next action without starting another system prompt.
    public func continuePermissionSetup() async {
        guard !permissionRequestInProgress, !busy, !recording, attempt == nil else { return }
        permissionRequestInProgress = true
        defer { permissionRequestInProgress = false }
        refreshPermissions()
        permissionCheckMessage = nil
        switch permissionStep {
        case .microphone: await grantMicrophone()
        case .accessibility: grantAccessibility()
        case .inputMonitoring: grantInputMonitoring()
        case .restart: quitAndReopen()
        case .ready: await toggle()
        }
    }

    /// Reports what this process can use, without opening a prompt. Repeating
    /// a denied request is not a check, and can hide stale-version problems.
    public func checkPermissionsAgain() {
        refreshPermissions()
        permissionCheckMessage = permissionStep.checkMessage
    }

    /// Whether a refresh has ever seen Input Monitoring missing. Guards the
    /// false→true transition against the initial `false` defaults.
    private var sawInputMonitoringMissing = false

    /// Whether this process ever tried to create the event tap and was
    /// refused. The distinction that decides what an Input Monitoring grant
    /// landing mid-session means: a tap that has not been created yet is
    /// created AFTER the grant at the next `start()` and simply works
    /// (a lazy-tap pattern — no reopen), while a process whose tap
    /// creation was already refused cannot get listen access retroactively
    /// on some macOS versions, and that process must relaunch.
    private var tapCreationRefused = false

    /// Re-reads the three grants. Never prompts, so it can run on a timer.
    public func refreshPermissions() {
        let permissions = env.permissions
        let previousStep = permissionStep
        let inputMonitoringWas = inputMonitoringGranted
        microphoneGranted = permissions.microphoneAuthorized()
        accessibilityGranted = permissions.accessibilityTrusted(false)
        inputMonitoringGranted = permissions.inputMonitoringGranted()
        // Before the first enable the line says what to do next. Only the
        // two idle sentences are replaced: any other line is newer and says
        // something more specific.
        if !enabled, status == DictationCopy.readyToEnable || status == DictationCopy.allowPermissionsFirst {
            status = allPermissionsGranted ? DictationCopy.readyToEnable : DictationCopy.allowPermissionsFirst
        }
        if !inputMonitoringGranted {
            sawInputMonitoringMissing = true
        } else if sawInputMonitoringMissing, !inputMonitoringWas, tapCreationRefused {
            // The grant arrived after this process's tap creation was
            // refused: only a fresh process can use it.
            reopenNeeded = true
        }
        if permissionStep != previousStep {
            permissionCheckMessage = nil
            if permissionStep == .ready, !enabled, !busy {
                status = DictationCopy.readyToEnable
            }
        }
    }

    /// The checklist's microphone row. A denied request is not asked again
    /// by macOS, so it ends in the settings pane.
    public func grantMicrophone() async {
        let granted = await env.permissions.requestMicrophone()
        if !granted { env.permissions.openMicrophoneSettings() }
        refreshPermissions()
    }

    /// Accessibility: the system prompt answers once, the pane after that.
    public func grantAccessibility() {
        if !env.permissions.accessibilityTrusted(true) {
            env.permissions.openAccessibilitySettings()
        }
        refreshPermissions()
    }

    /// Input Monitoring has no prompt of its own; registering Dictation and
    /// showing the pane is all there is.
    public func grantInputMonitoring() {
        env.permissions.requestInputMonitoring()
        refreshPermissions()
    }

    /// `enable()` bailed on a permission guard: when the poll sees every
    /// grant, it finishes what the click started.
    private var enablePending = false

    /// One pass of the permission poll: re-read the grants, and finish a
    /// pending Enable click once they are all in — unless a reopen is due:
    /// this process can never use an Input Monitoring grant made during it.
    /// The app runs it from `watchPermissionsForLife()`; tests drive it
    /// directly.
    func permissionTick() async {
        refreshPermissions()
        guard !reopenNeeded, !permissionRequestInProgress, enablePending, allPermissionsGranted, !busy else { return }
        enablePending = false
        env.log("permissions complete; finishing the pending enable")
        await enable()
    }

    /// The "Restart Earlet" button. A pending enable cannot
    /// survive the process exit, so a one-shot intent is persisted for the
    /// fresh process to finish it (`launch()`); an enabled app is already
    /// remembered by `settings.enabled`.
    public func quitAndReopen() {
        guard !busy, !recoveringSavedTake, !isQuitting, !recording, attempt == nil else { return }
        if enablePending, !enabled {
            env.settings.resumeEnableAfterRelaunch = true
        }
        env.log("quit and reopen to refresh permission access")
        env.relaunch()
    }

    // ─── Enable and pause ───

    public func toggle() async {
        guard !busy && !recoveringSavedTake && !isQuitting && !recording && attempt == nil else { return }
        if enabled {
            await pause()
        } else {
            await enable()
        }
    }

    /// At launch: comes back on if the user left it on, or if a
    /// quit-and-reopen asked the fresh process to finish an enable — but
    /// only when every permission is already in place, so nothing prompts
    /// at launch. The relaunch intent is one-shot: read, cleared, and not
    /// retried if a grant is still missing.
    public func launch() async {
        startRecoveryExpiry()
        // Before anything else: what the last run left. A take a crash or a
        // quit cut short is offered whether or not dictation comes back on.
        offerSavedTakeAtLaunch()
        // Off the press path: opening the history asks the keychain once,
        // and the retention runs after it.
        Task { await history.refresh() }
        let resume = env.settings.resumeEnableAfterRelaunch
        if resume { env.settings.resumeEnableAfterRelaunch = false }
        guard (env.settings.enabled || resume), !enabled, !busy else { return }
        let permissions = env.permissions
        guard permissions.microphoneAuthorized(), permissions.accessibilityTrusted(false),
              permissions.inputMonitoringGranted() else {
            status = resume
                ? "Earlet reopened to finish enabling. Enable it again once every permission is in place."
                : "Earlet was on when it last ran. Enable it again to finish allowing access."
            return
        }
        await enable()
    }

    private func enable() async {
        busy = true
        defer { busy = false }
        guard await env.permissions.requestMicrophone() else {
            enablePending = true
            refreshPermissions()
            status = "Allow Earlet to use the microphone in System Settings → Privacy & Security → Microphone, then try again."
            return
        }
        guard env.permissions.accessibilityTrusted(true) else {
            enablePending = true
            refreshPermissions()
            status = "Allow Earlet in System Settings → Privacy & Security → Accessibility, then enable dictation again."
            return
        }
        // Before the worker: a multi-gigabyte model load is wasted if the
        // key listener is not allowed anyway.
        guard env.permissions.inputMonitoringGranted() else {
            enablePending = true
            env.permissions.requestInputMonitoring()
            refreshPermissions()
            status = "Allow Earlet in System Settings → Privacy & Security → Input Monitoring, then enable dictation again. macOS may ask you to reopen Earlet."
            return
        }
        // Past the guards, the rest is not a permission problem: a failure
        // there must not retry on every poll tick.
        enablePending = false
        guard let resources = env.resolveResources() else {
            engineFailed = true
            status = "Speech resources are missing. Install the complete Earlet package and try again."
            return
        }
        self.resources = resources
        status = "Preparing speech recognition…"
        let started = await ensureWorkerStarted()
        env.log("enable: worker start \(started)")
        switch started {
        case .started:
            break
        case .failed:
            engineFailed = true
            await stopWorker()
            status = "The speech model could not load. Check the Earlet installation and try again."
            return
        case .timedOut:
            engineFailed = true
            status = "The speech engine took too long to start. Try enabling dictation again."
            return
        }
        refreshCaution()
        let key = env.makeTrigger(holdKey)
        key.onModifierFlags = { [weak self] flags in self?.modifierFlagsChanged(flags) }
        key.onHoldStart = { [weak self] in self?.holdStarted() }
        key.onHoldEnd = { [weak self] in self?.holdEnded() }
        // Both double-tap edges toggle: the model's own `latched` decides
        // which way (`toggleLatch`).
        key.onLatchStart = { [weak self] in self?.toggleLatch() }
        key.onLatchEnd = { [weak self] in self?.toggleLatch() }
        key.onDegraded = { [weak self] degraded in self?.triggerDegraded(degraded) }
        guard key.start() else {
            env.log("enable: the key listener was refused")
            tapCreationRefused = true
            reopenNeeded = true
            enablePending = true
            windowAttention += 1
            await stopWorker()
            status = "The shortcut needs Input Monitoring permission. Check access, then restart Earlet."
            return
        }
        trigger = key
        enabled = true
        keyCheck.setArmed(true)
        env.settings.enabled = true
        markWorkerUsed()
        startHousekeeping()
        refreshPermissions()
        status = "Enabled. Hold \(holdKey.name) in a text field."
    }

    private func pause() async {
        keyCheck.shutdown()
        busy = true
        defer { busy = false }
        abandonObservation("pause")
        enablePending = false
        housekeeping?.cancel(); housekeeping = nil
        trigger?.stop(); trigger = nil
        env.keyWatch.end()
        await env.capture.coolDown()
        await stopWorker()
        enabled = false
        engineFailed = false
        env.settings.enabled = false
        status = "Paused. The microphone is off."
    }

    /// The key listener reports a missing grant. While `enable()` is busy it
    /// writes its own, more specific line for the same refused start.
    func triggerDegraded(_ degraded: DictationPermissionStatus) {
        env.log("key listener degraded: \(degraded)")
        tapCreationRefused = true
        refreshPermissions()
        windowAttention += 1
        guard !busy else { return }
        status = "Earlet lost Accessibility or Input Monitoring access. Re-grant it below to continue."
    }

    /// How long the HUD shows the mic-changed sentence. Internal: a test
    /// shortens it rather than sleeping for the real four seconds.
    var micChangedNoticeSeconds: TimeInterval = 4
    private var micChangedNoticeTask: Task<Void, Never>?

    /// Shows the mic-changed sentence on the HUD for a few seconds. The
    /// status line keeps it until something newer lands; only the HUD's
    /// copy is ephemeral.
    func showMicChangedNotice(_ notice: String) {
        micChangedNoticeTask?.cancel()
        micChangedNotice = notice
        let seconds = micChangedNoticeSeconds
        micChangedNoticeTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled else { return }
            self?.micChangedNotice = nil
        }
    }

    /// The next press makes the notice stale however much time is left.
    private func clearMicChangedNotice() {
        micChangedNoticeTask?.cancel()
        micChangedNoticeTask = nil
        micChangedNotice = nil
    }

    /// Replaces every outcome's own dwell. Internal: a test shortens it
    /// rather than sleeping for seconds.
    var hudOutcomeSecondsOverride: TimeInterval?
    private var hudOutcomeTask: Task<Void, Never>?

    /// Shows how a dictation ended on the HUD until its dwell runs out, the
    /// way the mic-changed notice is shown.
    func showOutcome(_ kind: DictationHUDOutcome.Kind) {
        hudOutcomeTask?.cancel()
        let outcome = DictationHUDContent.outcome(for: kind, holdKeyName: holdKey.name)
        hudOutcome = outcome
        let seconds = hudOutcomeSecondsOverride ?? outcome.dwell
        hudOutcomeTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled else { return }
            self?.hudOutcome = nil
        }
    }

    private func clearOutcome() {
        hudOutcomeTask?.cancel()
        hudOutcomeTask = nil
        if hudOutcome != nil { hudOutcome = nil }
    }

    // ─── Live preview ───

    /// The hold's streaming preview, composed once. `nil` forever when the
    /// gate is off.
    private var livePreview: DictationLivePreviewSession?

    /// `DICTATION_PREVIEW=0` turns the streaming preview off; every other
    /// value (including unset) leaves it on. Model-local so a test can flip
    /// the gate without changing other models or relaunching the process.
    var livePreviewEnabled =
        ProcessInfo.processInfo.environment["DICTATION_PREVIEW"] != "0"

    /// The stream's languages: the pinned one, or the detection pair the app
    /// uses.
    private var previewLanguages: [String] {
        language.code.map { [$0] } ?? ["zh", "en"]
    }

    /// The capture's retained chunks, tapped once for the model's life: the
    /// live take's spool always, and the preview while it is composed. The
    /// sink fires on the capture's collector task, so both halves are
    /// bounded, lock-protected handoffs — never file I/O or a hop to main.
    private var retainedAudioTapInstalled = false
    func installRetainedAudioTapIfNeeded() {
        guard !retainedAudioTapInstalled else { return }
        env.capture.onInterruptionDiscardedAudio = { [weak self] in
            Task { @MainActor [weak self] in
                guard let self, self.recording else { return }
                await self.finishRecording()
            }
        }
        env.capture.onInterruptionRebound = nil
        retainedAudioTapInstalled = true
        let takeTap = takeTap, forwarder = previewForwarder
        env.capture.onRetainedAudio = { samples, sampleRate in
            if let live = takeTap.live() {
                live.spool.append(live.take, samples: samples, sampleRate: sampleRate)
                if sampleRate == DictationHoldTracker.sampleRate { live.tracker.append(samples) }
            }
            forwarder.forward(samples, sampleRate)
        }
    }

    /// Wires the capture's D15 callbacks and the preview half of the
    /// retained-audio tap to a preview session over the shared worker slot.
    /// Idempotent; with the gate off the tap feeds only the take spool.
    private func composeLivePreviewIfNeeded() {
        guard livePreview == nil else { return }
        guard livePreviewEnabled else { return }
        let log = env.log
        let preview = DictationLivePreviewSession(
            transport: slot,
            previewSink: { [weak self] text in
                Task { @MainActor [weak self] in
                    self?.livePreviewText = DictationPreviewCaption.window(text)
                }
            },
            log: { line in log(line) },
            readiness: { [weak self] in
                await self?.preparePreviewWorker() ?? false
            })
        livePreview = preview
        previewForwarder.set { samples, sampleRate in
            preview.append(samples: samples, sampleRate: sampleRate)
        }

    }

    /// Recheck on the main actor: the hold can end while the preview task
    /// hops here. A late callback must not start a worker after cleanup.
    private func preparePreviewWorker() async -> Bool {
        guard enabled, recording, attempt != nil else { return false }
        return await ensureWorkerStarted() == .started
    }

    /// Ends the hold's preview run and clears its caption, waiting at most
    /// `timing.previewStopSeconds`. The run is detached as soon as the stop
    /// begins (`DictationLivePreviewSession.stop`), so even a stop that is
    /// still waiting on the worker leaves the next hold free to start one.
    func stopLivePreview() async {
        livePreviewText = ""
        guard let preview = livePreview else { return }
        _ = await DictationDeadline.run(
            seconds: env.timing.previewStopSeconds,
            work: { await preview.stop(); return true })
    }

    /// Bounded clean-up before the app exits. The worker must not outlive
    /// the app (a wedged one holding its model would otherwise be orphaned),
    /// a paste's pending clipboard restore should land first, and learning
    /// gets a last try at saving what `learning.json` does not hold yet (a
    /// failed write, or changes made while the file could not be read;
    /// LEARNING.md §5). The enabled preference is left alone: quitting is not
    /// pausing.
    public func prepareForQuit() async {
        isQuitting = true
        keyCheck.shutdown()
        recoveryExpiry?.cancel(); recoveryExpiry = nil
        _ = release?.gate.abandon()
        _ = attempt?.gate.abandon()
        recentResult.clear()
        abandonObservation("quit")
        housekeeping?.cancel(); housekeeping = nil
        trigger?.stop(); trigger = nil
        endHold()
        await stopLivePreview()
        // Stop the collector before closing its spool: the final retained
        // chunks must reach the recovery file before the app exits.
        let stoppingCapture = env.capture
        _ = await DictationDeadline.run(seconds: 2, work: {
            await stoppingCapture.stop(keepWarm: false)
        })
        await leaveTake()
        let settle = env.settleClipboard
        _ = await DictationDeadline.run(seconds: 1, work: { await settle(); return true })
        if let learning = env.learning {
            // Reads nothing when learning was not used this run.
            let saved = await DictationDeadline.run(seconds: 1, work: { await learning.flush() })
            env.log("learning: last save at quit (\(saved.map { "\($0)" } ?? "timed out"))")
        }
        // A dictation finished just before Quit is still being sealed.
        let history = self.history
        if await DictationDeadline.run(seconds: 2, work: { await history.flush(); return true }) == nil {
            env.log("history: a save was still running at quit")
        }
        await stopWorker()
        let capture = env.capture
        _ = await DictationDeadline.run(seconds: 2, work: { await capture.coolDown(); return true })
    }

    // ─── The worker ───

    enum WorkerStart: Equatable {
        case started
        case failed
        case timedOut
    }

    private var resources: DictationResources?
    let slot = WorkerSlot()
    var worker: (any DictationWorker)? {
        get { slot.worker }
        set { slot.worker = newValue }
    }
    /// The start in flight, shared by everyone who needs the worker ready.
    var workerStart: Task<WorkerStart, Never>?

    /// The current worker, built (not started) when there is none.
    private func currentWorker() -> (any DictationWorker)? {
        if let worker { return worker }
        guard let resources else { return nil }
        let built = env.makeWorker(resources)
        worker = built
        return built
    }

    /// Starts the worker if it is not ready, within `timing.startSeconds`.
    ///
    /// Single flight: a second caller waits for the start already running
    /// rather than sending a second `init` down the same pipe. A worker that
    /// fails or times out is dropped, so the next call builds a fresh one.
    func ensureWorkerStarted() async -> WorkerStart {
        if let workerStart { return await workerStart.value }
        guard let target = currentWorker() else { return .failed }
        if target.isReady { return .started }
        let seconds = env.timing.startSeconds
        let task = Task { () -> WorkerStart in
            let started = await DictationDeadline.run(
                seconds: seconds,
                work: { await target.start() },
                onAbandon: { target.forceTerminate() })
            switch started {
            case true?: return .started
            case false?: return .failed
            case nil: return .timedOut
            }
        }
        workerStart = task
        let result = await task.value
        workerStart = nil
        engineFailed = result != .started
        if result == .started { engineFailed = false }
        if result == .timedOut { env.log("worker start exceeded \(seconds) s; killed") }
        if result != .started, worker === target { worker = nil }
        return result
    }

    private var lastWorkerUse: TimeInterval = 0

    func markWorkerUsed() {
        lastWorkerUse = ProcessInfo.processInfo.systemUptime
    }

    // ─── Housekeeping ───

    private func startHousekeeping() {
        housekeeping?.cancel()
        let interval = env.timing.housekeepingSeconds
        housekeeping = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(interval))
                guard !Task.isCancelled, let self else { return }
                await self.housekeepingTick()
            }
        }
    }

    /// Gives back what an idle app should not hold: the open microphone
    /// and the loaded model.
    func housekeepingTick() async {
        refreshCaution()
        // A kept take older than the kept-for limit is deleted unheard, as
        // the next launch would; the app starts at login and can run for
        // weeks.
        await expireSavedTake()
        // The same for the history's retention, at most once an hour.
        await history.prune()
        // `shouldCoolDown` refuses while a capture is retaining audio, so a
        // long hold is never cut off here.
        if !busy, attempt == nil, env.capture.shouldCoolDown(idleSeconds: env.timing.microphoneIdleSeconds) {
            await env.capture.coolDown()
        }
        guard enabled, !busy, !recoveringSavedTake, !isQuitting, attempt == nil, workerStart == nil, worker?.isReady == true,
              ProcessInfo.processInfo.systemUptime - lastWorkerUse >= env.timing.workerIdleSeconds
        else { return }
        await stopWorker()
    }

    /// Asks the worker to exit and kills it if it does not within
    /// `timing.shutdownSeconds` — a shutdown queued behind a wedged request
    /// would otherwise wait forever.
    private func stopWorker() async {
        guard let target = worker else { return }
        worker = nil
        if workerStart != nil {
            // Still loading: nothing to be polite to.
            target.forceTerminate()
            return
        }
        _ = await DictationDeadline.run(
            seconds: env.timing.shutdownSeconds,
            work: { await target.shutdown(); return true },
            onAbandon: { target.forceTerminate() })
    }

    // ─── One hold ───

    /// Why a hold ended without inserting anything.
    enum CancelReason: Equatable {
        /// Escape, or the window's Cancel button.
        case user
        /// The hold key went down as part of a shortcut.
        case shortcut
    }

    private func makeAttempt() -> HoldAttempt {
        let gate = InsertionGate()
        let transcript = TranscriptHolder()
        let target = PasteTarget()
        let resolveTarget = env.targetApplication
        // Formatting on, with the shipped rules only: capitalise sentences,
        // drop a trailing ellipsis, and repair Han-script punctuation (a
        // half-width comma between Han characters goes full-width, a bare
        // Chinese sentence ends in 。), nothing in a terminal
        // (`DictationFormatter.format(_:forApp:)`). Windows applies the same
        // rules and also leaves terminals alone (`DictationTextFormatter`),
        // so the same words insert the same text on both. Rule-based and
        // local; no language model.
        //
        // The app the formatter is told about is the app the paste goes to:
        // asked once, after the transcription and right before the paste, off
        // the main actor. The correction watch reuses that answer rather than
        // asking again on the paste path.
        let capture = DictationRecoverableCapture(capture: env.capture, tap: takeTap)
        let session = DictationSession(
            capture: capture,
            asr: DictationRetryTransport(slot: slot, gate: gate, timing: env.timing,
                restart: { [weak self] failed in
                    guard let self, !gate.isAbandoned, self.worker === failed else { return false }
                    failed.forceTerminate()
                    self.worker = nil
                    self.status = "The speech engine stopped. Retrying your recording…"
                    return await self.ensureWorkerStarted() == .started
                }, log: env.log),
            formattingEnabled: true,
            targetBundleIDProvider: {
                let app = resolveTarget()
                target.record(app)
                return app
            },
            textInsertion: { [weak self] text in
                guard let self else { return (.failed(.pasteNeverRequested), TextInserter.InsertTiming()) }
                return await self.insert(text, gate: gate, transcript: transcript, target: target)
            })
        let attempt = HoldAttempt(session: session, gate: gate, transcript: transcript,
                           diagnostics: DictationAttemptDiagnostics(log: env.log))
        attempt.capture = capture
        attempt.target = target
        return attempt
    }

    var attempt: HoldAttempt?
    private var chordGuard = HoldChordGuard()
    /// The flags of the latest modifier change, so a hold can tell whether
    /// another modifier was already down when it began.
    private var lastModifierFlags: CGEventFlags = []
    /// Waits out the double-tap window after a quick release; cancelled when
    /// the second press makes the hold a latch or the hold ends another way.
    private var tapSettle: Task<Void, Never>?
    /// Set when a hold-key press ends a latch: the uptime-plus-window before
    /// which further presses start nothing. Absorbs the trailing tap of the
    /// old double-tap stop habit — both its raw press and the detector's
    /// stray toggle edge — so it cannot open a fresh dictation by accident.
    private var stopTapSuppressUntil: TimeInterval = 0

    func holdStarted() {
        guard !keyCheck.isTesting else { return }
        // A press while latched is the stop signal (the mouse trigger's
        // any-press stop, on the key): finish and transcribe.
        if latched {
            stopTapSuppressUntil = env.holdKeyUptime()
                + holdKey.hotkeyConfig.doubleTapWindow
            env.log("latch ended by a \(holdKey.name) press")
            latchEnded()
            return
        }
        if env.holdKeyUptime() < stopTapSuppressUntil {
            env.log("absorbed the stop pair's trailing tap")
            return
        }
        guard enabled, !recoveringSavedTake, !isQuitting, attempt == nil else { return }
        guard !busy else {
            refuseBusyPress()
            return
        }
        guard chordGuard.begin(holdMask: holdKey.binding.eventFlagMask, flags: lastModifierFlags) else {
            return
        }
        // The watch on the last paste keeps running: this press may yet be a
        // shortcut on the hold key, a refused press or a cancelled one, and
        // none of those is the next dictation (`finishRecording` ends it).
        // The language is fixed when a hold starts, so a change made while
        // this one is under way reaches the next.
        slot.setLanguageCode(language.code)
        keyCheck.pressed()
        let attempt = makeAttempt()
        attempt.startedAt = ProcessInfo.processInfo.systemUptime
        self.attempt = attempt
        // `recording` waits for the privacy gate (`admit`): a refused press
        // never shows Listening. The key watch does not wait, so a shortcut
        // typed at once is still told apart from a dictation.
        env.keyWatch.begin { [weak self] code in self?.keyDown(code) }
        if worker?.isReady != true {
            // Released after idle, or died: load it while the user speaks.
            Task { _ = await ensureWorkerStarted() }
        }
        Task { await beginCapture(attempt) }
    }

    private func beginCapture(_ attempt: HoldAttempt) async {
        guard self.attempt === attempt else { return }
        // Before the microphone: a password must never be recorded, let
        // alone transcribed and only refused at insertion.
        let privacy = env.privacyGate()
        if let refusal = Self.privacyRefusal(privacy), let privacy {
            env.log("press refused by the privacy gate")
            attempt.diagnostics.finish(.privacyRefused)
            endHold()
            status = refusal
            showOutcome(DictationHUDContent.ending(forRefusedPaste: privacy, keptOnClipboard: false))
            return
        }
        slot.setVocabularyScene(LearningScene.forApplication(env.vocabularyApplication())?.rawValue)
        admit()
        composeLivePreviewIfNeeded()
        let session = attempt.session
        let capture = env.capture
        // Rebind stale inputs and warm cold capture before arming it.
        if capture.isCurrentlyWarm, capture.isInputStaleWhileWarm {
            env.log("press: the warm capture's input binding is stale — rebinding")
        }
        // Device startup can take seconds; keep it off the main actor.
        let result = await Task.detached { () -> Result<Void, DictationSession.SessionFailure> in
            if capture.isCurrentlyWarm, capture.isInputStaleWhileWarm {
                await capture.coolDown()
            }
            if !capture.isCurrentlyWarm {
                do {
                    try capture.warmUp()
                } catch {
                    // The same classification `DictationSession.triggerDown`
                    // gives a failed cold start: only a TCC error is a
                    // permission problem.
                    if case MicCaptureError.permissionDenied = error {
                        return .failure(.micPermissionDenied)
                    }
                    return .failure(.emptyCapture)
                }
            }
            return session.triggerDown()
        }.value
        guard self.attempt === attempt else { return }
        switch result {
        case .failure(let reason):
            env.log("capture did not start: \(reason)")
            attempt.diagnostics.finish(.failed(stage: .capture, failure: reason, timings: .init()))
            // Shown before the hold ends, so the HUD goes from Listening
            // straight to the reason.
            showOutcome(DictationHUDContent.ending(for: reason, stage: .capture))
            endHold()
            // The take never captured anything: its spool file goes.
            await settleTake(worthKeeping: false)
            status = DictationCopy.failure(reason, stage: .capture, holdKey: holdKey)
        case .success:
            attempt.captureStarted = true
            // Audio is flowing: open the preview stream. Asynchronous by
            // design — nothing on the press path waits for it.
            livePreview?.start(languages: previewLanguages)
            if let reason = attempt.cancelReason {
                await discard(reason, session: session)
                return
            }
            if attempt.latchEndRequested {
                await finishRecording()
                return
            }
            holdStartedAt = Date()
            startHoldMonitor(attempt)
            if attempt.releaseRequested {
                // The key came up while capture was starting. A cold
                // microphone can come up between the two taps of a
                // double-tap, so this release gets the same double-tap
                // window as one made with capture already running.
                attempt.releaseRequested = false
                releaseCaptured(attempt)
            }
        }
    }

    /// A press while the last dictation is still being transcribed or typed.
    /// Recording it alongside and queueing its transcription is the better
    /// design and is not built; until then it is refused out loud. Ignored
    /// silently, the user spoke a whole sentence into nothing while the HUD
    /// said "Transcribing…".
    private func refuseBusyPress() {
        env.log("press refused: the last dictation is still finishing")
        status = DictationCopy.stillFinishing(holdKey: holdKey)
        showOutcome(.stillFinishing)
    }

    /// The press passed the privacy gate: from here it is a recording, and
    /// whatever the HUD said about the last dictation is stale.
    private func admit() {
        recording = true
        clearMicChangedNotice()
        clearOutcome()
        beginTake()
        env.capture.onAudioLevel = { [weak self] level in
            Task { @MainActor [weak self] in self?.noteInputLevel(level) }
        }
        // A double-tap may already have latched this hold while the gate ran.
        status = latched ? latchedStatus : "Listening… Release \(holdKey.name) to insert."
    }

    private var latchedStatus: String { "Dictating. Press \(holdKey.name), Space or Return to stop." }

    /// What to say when the press-time gate refuses, or `nil` to proceed.
    ///
    /// Only the two privacy signals refuse. Incomplete accessibility metadata
    /// is not evidence of a password field (some apps that accept ⌘V publish
    /// no focused element), so any other answer lets the dictation go ahead.
    static func privacyRefusal(_ failure: DictationInsertFailure?) -> String? {
        switch failure {
        case .secureFieldFocused?:
            return "Earlet is off in password fields. Nothing was recorded."
        case .secureInputActive?:
            return "Secure input is on, usually because a password field has focus, so Earlet is not listening."
        default:
            return nil
        }
    }

    func holdEnded() {
        guard !keyCheck.isTesting else { return }
        if !latched { keyCheck.released() }
        // A latch ends on the next double-tap, Escape or Cancel — the pair's
        // own releases are not releases.
        guard !latched else { return }
        guard let attempt else { return }
        guard attempt.captureStarted else {
            attempt.releaseRequested = true
            watchEscapeOnly()
            return
        }
        releaseCaptured(attempt)
    }

    /// The hold key is up but this hold goes on: parked in the double-tap
    /// window, waiting for capture, or latched. The shortcut rule exists to
    /// tell Right Command + C from a dictation, which only means anything
    /// while the key is held; from here typing, ⌘-Tab or Shift is the user
    /// working, and dropping up to two minutes of speech as "a shortcut"
    /// was the bug. Of the keys, only Escape still cancels; the window's and
    /// the menu's Cancel do too.
    private func watchEscapeOnly() {
        chordGuard.end()
        env.keyWatch.begin { [weak self] code in
            guard code == HoldChordGuard.escapeKeyCode else { return }
            Task { await self?.cancelHold(.user) }
        }
    }

    /// The hold key came up on a hold whose capture is running.
    ///
    /// A release this quick may be the first tap of a double-tap: wait out
    /// the detector's window (measured from the press) before finishing, so
    /// the pair's first tap never transcribes on its own. The capture keeps
    /// running meanwhile, so a latch loses no audio.
    private func releaseCaptured(_ attempt: HoldAttempt) {
        watchEscapeOnly()
        let elapsed = ProcessInfo.processInfo.systemUptime - attempt.startedAt
        let window = holdKey.hotkeyConfig.doubleTapWindow
        guard elapsed < window else {
            Task { await finishRecording() }
            return
        }
        attempt.tapParked = true
        tapSettle = Task { [weak self] in
            try? await Task.sleep(for: .seconds(window - elapsed))
            guard let self, self.attempt === attempt, attempt.tapParked else { return }
            attempt.tapParked = false
            self.tapSettle = nil
            await self.finishRecording()
        }
    }

    /// Where both of the detector's double-tap edges land.
    ///
    /// The detector keeps its own on/off flag, and it drifts from the
    /// model's whenever a latch ends some other way — Escape, Cancel, the
    /// two-minute ceiling, a refused press — or a double-tap lands while the
    /// last dictation is still finishing. Its next "deactivate" then arrived
    /// for a model that was not latched and was dropped, and the pair's first
    /// tap transcribed on its own. So a double-tap means "toggle", and the
    /// model's own `latched` decides which way.
    func toggleLatch() {
        guard !keyCheck.isTesting else { return }
        if !latched { keyCheck.doubleTapped() }
        if latched { latchEnded() } else { latchStarted() }
    }

    /// The double-tap that latches dictation on. Its second press already
    /// fired the ordinary hold edges: the first tap's hold is parked
    /// (or its release still waiting for capture), and that hold becomes
    /// the latch — no audio is lost and nothing transcribes twice.
    func latchStarted() {
        if let attempt {
            if attempt.tapParked {
                attempt.tapParked = false
                tapSettle?.cancel(); tapSettle = nil
            } else if !attempt.captureStarted {
                // Tap one's release arrived before capture was up; the
                // double-tap takes it back.
                attempt.releaseRequested = false
            } else {
                // Unreachable from one physical key (a held key cannot be
                // tapped); never take over a hold that is finishing.
                return
            }
        } else {
            holdStarted()
            guard attempt != nil else { return }
        }
        latched = true
        watchEscapeOnly()
        if !env.stopTrap.begin({ [weak self] in self?.stopKeyPressed() }) {
            env.log("the Space/Return stop needs Input Monitoring; this latch ends by key press or Escape only")
        }
        status = latchedStatus
    }

    /// The trap swallowed a stop key's down (Space, Return or keypad Enter):
    /// end the latch. The keystroke never reaches the focused app, so nothing
    /// types into it or sends the message before the words land.
    private func stopKeyPressed() {
        env.log("latch ended by stop key")
        latchEnded()
    }

    /// A latch's end — one press of the hold key, a stop key, or the
    /// detector's toggle edge: finish as a release would.
    /// `latched` stays up until the finish clears it with the rest of the
    /// hold: dropped here, the HUD showed a plain hold's hint for a moment
    /// before Transcribing, and the pair's own release that follows must
    /// still not count as a release.
    func latchEnded() {
        guard latched, let attempt, !attempt.latchEndRequested else { return }
        attempt.latchEndRequested = true
        guard attempt.captureStarted else { return }
        Task { await finishRecording() }
    }

    func modifierFlagsChanged(_ flags: CGEventFlags) {
        lastModifierFlags = flags
        if chordGuard.modifiersChanged(flags) == .chord {
            Task { await cancelHold(.shortcut) }
        }
    }

    func keyDown(_ keyCode: UInt16) {
        switch chordGuard.keyDown(keyCode) {
        case .escape: Task { await cancelHold(.user) }
        case .chord: Task { await cancelHold(.shortcut) }
        case .none: break
        }
    }

    /// Clears everything that exists only while a key is held.
    func endHold() {
        attempt = nil
        recording = false
        latched = false
        inputLevel = 0
        livePreviewText = ""
        env.capture.onAudioLevel = nil
        chordGuard.end()
        env.keyWatch.end()
        env.stopTrap.end()
        maximumHold?.cancel(); maximumHold = nil
        holdMonitor?.cancel(); holdMonitor = nil
        holdHealth?.cancel(); holdHealth = nil
        holdNotice = nil
        confirmingDiscard = false
        holdStartedAt = nil
        tapSettle?.cancel(); tapSettle = nil
    }

    /// One chunk's peak from the capture, ~12 Hz while a hold records.
    /// Ignored outside a hold: the HUD only shows a live level while
    /// recording.
    func noteInputLevel(_ level: Float) {
        guard recording else { return }
        // Fast attack, slower release, so the bars follow speech instead of
        // strobing per chunk.
        inputLevel = level > inputLevel ? level : inputLevel * 0.55 + level * 0.45
    }

    // ─── Release to text ───

    /// The release whose words are on their way, while there is one.
    var release: ReleaseCancel?

    // ─── Learning from corrections ───

    /// Only this active watch token may teach; stale results are discarded.
    private var observation: UUID?
    private var observationSpan: String?
    private var observationSequence = 0
    private var observationTasks: [UUID: Task<Void, Never>] = [:]
    var learningRevision = 0
    private var observationScene: LearningScene?

    /// Off abandons correction observation; learned vocabulary stays available.
    public func setLearningEnabled(_ on: Bool) {
        env.settings.learningEnabled = on
        learningEnabled = on
        if !on {
            abandonObservation("learning switched off")
            vocabulary.dismissLearningNotice()
        }
    }

    /// Starts the correction watch on text that was just typed into `app`.
    func watchPaste(_ pasted: String, into app: String?, beforePaste: DictationCorrectionObserver.PasteSnapshot?) {
        guard learningEnabled, env.learning != nil else { return }
        let token = UUID()
        vocabulary.beginObservationFeedback(id: token)
        guard let app else {
            // An unknown paste app cannot prove that focus stayed in its field.
            env.log("learning: not observed (the target app is unknown)")
            vocabulary.endObservationFeedback(id: token, reason: "target_application_unknown")
            return
        }
        observation = token
        observationScene = LearningScene.forApplication(app)
        observationSpan = nil
        observationSequence = 0
        env.correctionWatch.begin(pasted: pasted, targetApp: app, beforePaste: beforePaste,
            onRevision: { [weak self] span in
                DispatchQueue.main.async { [weak self] in
                    self?.observationRevised(span, token: token, pasted: pasted, continuing: true)
                }
            }) { [weak self] end in
            DispatchQueue.main.async { [weak self] in
                self?.observationEnded(end, token: token, pasted: pasted)
            }
        }
    }

    private func observationEnded(_ end: DictationCorrectionWatchEnd, token: UUID, pasted: String) {
        guard observation == token else {
            env.log("learning: ignored the result of an abandoned observation (\(end.reason))")
            return
        }
        if let span = end.editedSpan {
            // Legacy/fake final-only observers still deliver their one result.
            observationRevised(span, token: token, pasted: pasted, continuing: false, terminalReason: end.reason)
        } else {
            // The watch's real reason and its two flags reach the notice: an
            // end that still held its span is not "could not be checked",
            // and an end after a revision may not clear that revision's
            // notice.
            vocabulary.endObservationFeedback(id: token, reason: end.reason,
                                              revised: observationSpan != nil,
                                              spanObservedToEnd: end.spanObservedToEnd,
                                              sawChange: end.sawChange)
            env.log("learning: observation ended (\(end.reason)); nothing to learn")
        }
        observation = nil
    }

    private func observationRevised(_ span: String, token: UUID, pasted: String, continuing: Bool, terminalReason: String? = nil) {
        guard observation == token else { return }
        if let previous = observationSpan, previous.unicodeScalars.elementsEqual(span.unicodeScalars) { return }
        observationSpan = span
        observationSequence += 1
        let sequence = observationSequence, generation = learningRevision
        let scene = observationScene
        let log = env.log, vocabulary = vocabulary
        let taskID = UUID()
        observationTasks[taskID] = Task { [weak self] in
            defer { self?.observationTasks.removeValue(forKey: taskID) }
            guard let self, self.learningRevision == generation, self.learningEnabled else { return }
            let outcome = await vocabulary.observe(dictationID: token.uuidString, dictated: pasted, edited: span,
                at: Date(), observationID: token, revision: sequence, keepObserving: continuing, scene: scene)
            if let terminalReason {
                log("learning: observation ended (\(terminalReason)); \(DictationModel.describe(outcome))")
            } else {
                log("learning: stable revision; \(DictationModel.describe(outcome))")
            }
            if self.learningRevision == generation, let undo = outcome.undoToken, vocabulary.undoToken == undo,
               self.enabled, !self.recording, !self.busy {
                self.showOutcome(.vocabularyLearned)
            }
        }
    }

    /// Ends the watch in progress, if any, without learning from it.
    func abandonObservation(_ why: String) {
        learningRevision += 1 // Also invalidates preparation/paste already in flight.
        for task in observationTasks.values { task.cancel() }
        observationTasks.removeAll()
        guard let token = observation else { return }
        observation = nil
        vocabulary.endObservationFeedback(id: token, reason: "abandoned")
        env.correctionWatch.abandon()
        env.log("learning: observation abandoned (\(why))")
    }

    /// Counts only: the log never holds a word.
    static func describe(_ outcome: DictationLearning.ObserveOutcome) -> String {
        "\(outcome.candidates) corrections, \(outcome.rejections) rejected, "
            + "\(outcome.wordsLearned) words and \(outcome.aliasesLearned) aliases learned, file \(outcome.save)"
    }

    // ─── Cancel ───

    /// The Cancel button and menu item: drops a recording, or the words of a
    /// release that are still on their way.
    public func cancel() async {
        if attempt != nil {
            await cancelHold(.user)
        } else {
            cancelRelease()
        }
    }

    func cancelHold(_ reason: CancelReason) async {
        guard let attempt, !busy else { return }
        if reason == .user, let start = holdStartedAt,
           Date().timeIntervalSince(start) > 30, !confirmingDiscard {
            confirmingDiscard = true
            holdNotice = "Discard this recording? Press Escape again to confirm."
            windowAttention += 1
            return
        }
        guard attempt.captureStarted else {
            // Honoured by `beginCapture` once the capture it is starting is up.
            attempt.cancelReason = reason
            chordGuard.end()
            env.keyWatch.end()
            env.stopTrap.end()
            return
        }
        await discard(reason, session: attempt.session)
    }

    private func discard(_ reason: CancelReason, session: DictationSession) async {
        attempt?.diagnostics.finish(reason == .user ? .cancelled : .shortcut)
        // Decided before the hold ends, so the HUD goes from Listening
        // straight to "Cancelled". A shortcut shows nothing: the user meant
        // the shortcut, and a notice after every Right Command + C is noise.
        switch reason {
        case .user:
            status = "Recording cancelled. Nothing inserted."
            showOutcome(.cancelled)
        case .shortcut:
            status = "\(holdKey.name) was used in a shortcut, so nothing was recorded."
        }
        endHold()
        busy = true
        // Escape and Cancel throw the take away, and so does a shortcut:
        // only a transcription failure keeps it.
        await settleTake(worthKeeping: false)
        // A cancelled hold's preview ends with it.
        await stopLivePreview()
        // Stop retaining and drop the audio; the engine stays warm.
        _ = await env.capture.stop(keepWarm: env.capture.isCurrentlyWarm)
        session.reset()
        busy = false
    }
}

/// The preview half of the retained-audio tap, set when the preview session
/// is composed (never, when its gate is off). Fires on the capture's
/// collector task; `DictationLivePreviewSession.append` is a bounded copy.
final class DictationPreviewForwarder: @unchecked Sendable {
    private let lock = NSLock()
    private var sink: (@Sendable ([Float], Int) -> Void)?

    func set(_ sink: (@Sendable ([Float], Int) -> Void)?) {
        lock.lock(); self.sink = sink; lock.unlock()
    }

    func forward(_ samples: [Float], _ sampleRate: Int) {
        lock.lock(); let sink = self.sink; lock.unlock()
        sink?(samples, sampleRate)
    }
}
