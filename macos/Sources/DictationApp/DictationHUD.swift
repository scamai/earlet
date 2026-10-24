import AppKit
import Combine
import DictationAppCore
import SwiftUI

// The floating dictation indicator: a borderless capsule that floats above
// other apps while a dictation runs and for a moment after it ends, whether
// or not the main window is open, so the user can always see that dictation
// is live and how it ended. AppKit plumbing only — what it shows and when is
// `DictationHUDContent` and `DictationHUDPanel` in DictationAppCore, which
// is where the tests are.
// Colours come from `DictationBrand`: the window's palette, dark appearance.

/// Owns the panel and follows the model. Non-activating and click-through
/// (`DictationHUDPanelSpec`): the HUD never takes key focus from the field
/// being dictated into.
@MainActor
final class DictationHUDController {
    /// Built once, here: the press path orders an existing window front and
    /// swaps its root view, so nothing is constructed while the key is down.
    private let panel: DictationHUDPanel
    private let hosting: NSHostingView<DictationHUDView>
    private var state: DictationHUDState = .hidden
    private var cancellables: Set<AnyCancellable> = []
    /// Set while the menu preview cycles the states; model updates wait in
    /// `previewGate` and the latest is shown when it ends.
    private var previewTask: Task<Void, Never>?
    private var previewGate = DictationHUDPreviewGate()
    /// Which preview owns the gate and the task handle: a cancelled
    /// predecessor must end neither.
    private var previewGeneration = 0

    init(model: DictationModel) {
        maximumHoldSeconds = model.maximumHoldSeconds
        panel = DictationHUDPanelSpec.makePanel()
        hosting = NSHostingView(rootView: DictationHUDView(state: .hidden, holdKeyName: model.holdKey.name))
        panel.contentView = hosting
        // Where each main-actor turn of the model ended, on the main
        // queue's next turn, which also comes while a menu is open: a state
        // the model only passed through never reaches the panel.
        model.hudStates
            .settled()
            .sink { [weak self, weak model] state in
                guard let self, let model else { return }
                holdKeyName = model.holdKey.name
                guard let offer = previewGate.modelOffer(state) else { return }
                desiredState = offer
                offerDesired()
            }
            .store(in: &cancellables)
        // The live level changes ~12× a second while a hold records — far
        // too often to re-resolve state; only the waveform reads it.
        model.$inputLevel
            .sink { [weak self] level in
                self?.level = level
                self?.redraw()
            }
            .store(in: &cancellables)
        // The preview caption changes on the worker's cadence; only the
        // title line reads it.
        model.$livePreviewText
            .sink { [weak self] text in
                self?.liveText = text
                self?.redraw()
            }
            .store(in: &cancellables)
        // A long hold's time: the hint shows it latched and counts down
        // near the ceiling.
        model.$holdNotice
            .sink { [weak self] notice in self?.holdNotice = notice; self?.redraw() }
            .store(in: &cancellables)
        model.$holdStartedAt
            .sink { [weak self] start in self?.holdClockChanged(start) }
            .store(in: &cancellables)
        // The floor under every "the HUD went silent" mechanism
        // (`DictationHUDReconcile`): the panel's truth against the model's
        // wanted state, once a second.
        reconcileTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(DictationHUDReconcile.intervalSeconds))
                guard let self, !Task.isCancelled else { return }
                self.reconcile()
            }
        }
    }

    private var level: Float = 0
    private var liveText = ""
    private var holdNotice: String?
    private var holdKeyName = ""
    private var holdStartedAt: Date?
    private let maximumHoldSeconds: TimeInterval
    /// Redraws once a second while a hold's clock runs.
    private var clockTick: Task<Void, Never>?
    /// The state the model wants, and the anti-flap pacer gating it.
    private var desiredState: DictationHUDState = .hidden
    private var pacer = DictationHUDPacer()
    private var dwellRetry: Task<Void, Never>?
    /// Keeps a hide's fade from ordering out a panel shown again since.
    private var hideGate = DictationHUDHideGate()
    private var announcer = DictationHUDAnnouncer()
    /// The in-flight hide fade, if any, by when it started and its hide
    /// generation: a heartbeat may take the panel away only from a fade
    /// whose completion was lost, never from a healthy one mid-fade.
    private var hideFade: (startedAt: TimeInterval, generation: Int)?
    /// The heartbeat comparing the model's wanted state with the panel's.
    private var reconcileTask: Task<Void, Never>?
    /// The last wedge the heartbeat reported, so a persisting one logs once
    /// a minute rather than once a second.
    private var lastReconcileReport: (action: String, at: TimeInterval)?

    /// Routes the desired state through the pacer: hidden and micChanged
    /// land at once, other visible states after their dwell — with one
    /// retry so a deferred state is never lost.
    private func offerDesired() {
        dwellRetry?.cancel()
        dwellRetry = nil
        // A retry scheduled before the preview began must not land in it.
        guard !previewGate.isRunning else { return }
        let decision = pacer.offer(desiredState, at: ProcessInfo.processInfo.systemUptime)
        switch decision {
        case .show(let state):
            present(state, holdKeyName: holdKeyName)
        case .hold(let retryAfter):
            dwellRetry = Task { [weak self] in
                try? await Task.sleep(for: .seconds(retryAfter))
                guard !Task.isCancelled else { return }
                self?.offerDesired()
            }
        }
    }

    /// The heartbeat: the model's wanted state against the panel's truth
    /// (`DictationHUDReconcile`). A preview owns the panel while it runs,
    /// so a disagreement then is none.
    private func reconcile() {
        guard !previewGate.isRunning else { return }
        switch DictationHUDReconcile.action(
            desiredHidden: desiredState == .hidden, panelVisible: panel.isVisible) {
        case .reoffer:
            report(.reoffer)
            offerDesired()
        case .orderOut:
            // A fade still counting past its grace lost its completion;
            // a younger one is healthy and finishes on its own.
            if let fade = hideFade,
               ProcessInfo.processInfo.systemUptime - fade.startedAt
                <= DictationHUDReconcile.fadeStaleAfterSeconds { return }
            hideFade = nil
            report(.orderOut)
            panel.orderOut(nil)
            panel.alphaValue = 1
        case nil:
            break
        }
    }

    /// One log line per wedge, not one per heartbeat: while the same
    /// disagreement persists it repeats at most once a minute.
    private func report(_ action: DictationHUDReconcile.Action) {
        let now = ProcessInfo.processInfo.systemUptime
        let name = action == .reoffer ? "reoffer" : "orderOut"
        if let last = lastReconcileReport, last.action == name, now - last.at < 60 { return }
        lastReconcileReport = (name, now)
        DictationLogSink.shared.log(
            "hud reconcile: \(name) — wanted \(desiredState), panel visible: \(panel.isVisible)")
    }

    private func present(_ newState: DictationHUDState, holdKeyName: String) {
        announce(newState)
        switch newState {
        case .hidden:
            guard state != .hidden else { return }
            state = newState
            let generation = hideGate.present()
            hideFade = (ProcessInfo.processInfo.systemUptime, generation)
            NSAnimationContext.runAnimationGroup({ context in
                // Exit not slower than the 180 ms entrance.
                context.duration = 0.14
                panel.animator().alphaValue = 0
            }, completionHandler: { [weak self] in
                Task { @MainActor in
                    // AppKit also completes a fade that a newer animation
                    // replaced; only the latest hide, still hidden, may
                    // take the panel away.
                    guard let self else { return }
                    if self.hideFade?.generation == generation { self.hideFade = nil }
                    guard self.hideGate.mayOrderOut(hideGeneration: generation, state: self.state)
                    else { return }
                    self.panel.orderOut(nil)
                    self.panel.alphaValue = 1
                }
            })
        default:
            let entering = state == .hidden
            state = newState
            _ = hideGate.present()
            show(newState, holdKeyName: holdKeyName, animateIn: entering)
        }
    }

    /// Speaks a state's constant label when the spoken words change: a
    /// panel that is never key is not announced by VoiceOver on its own.
    /// Never while the microphone can hear it (`DictationHUDAnnouncer
    /// .speaksIn`), or the announcement is typed with the user's words.
    private func announce(_ newState: DictationHUDState) {
        guard let spoken = announcer.announcement(for: newState) else { return }
        NSAccessibility.post(
            element: NSApp as Any, notification: .announcementRequested,
            userInfo: [
                .announcement: spoken,
                .priority: NSAccessibilityPriorityLevel.high.rawValue,
            ])
    }

    /// The capsule for `state` with the latest level, caption and clock.
    private func view(for state: DictationHUDState, holdKeyName: String) -> DictationHUDView {
        DictationHUDView(
            state: state, holdKeyName: holdKeyName, level: level, liveText: liveText, holdNotice: holdNotice,
            elapsed: holdStartedAt.map { Date().timeIntervalSince($0) },
            maximumHoldSeconds: maximumHoldSeconds)
    }

    /// Redraws the state on screen after a level, caption or clock change.
    /// Nothing is drawn while the panel is off screen.
    private func redraw() {
        guard panel.isVisible, state != .hidden else { return }
        hosting.rootView = view(for: state, holdKeyName: holdKeyName)
    }

    private func holdClockChanged(_ start: Date?) {
        holdStartedAt = start
        clockTick?.cancel()
        clockTick = nil
        guard let start else { return }
        clockTick = Task { [weak self] in
            while !Task.isCancelled {
                // Just past the next whole second of the hold, when the
                // m:ss in the hint changes.
                let elapsed = max(0, Date().timeIntervalSince(start))
                let wait = 1 - elapsed.truncatingRemainder(dividingBy: 1) + 0.02
                try? await Task.sleep(for: .seconds(wait))
                guard !Task.isCancelled, let self else { return }
                redraw()
            }
        }
    }

    private func show(_ newState: DictationHUDState, holdKeyName: String, animateIn: Bool) {
        hosting.rootView = view(for: newState, holdKeyName: holdKeyName)

        // Recompute placement on every show: the screen with keyboard
        // focus, horizontally centred, the capsule (not its shadow margin)
        // 120 pt above the bottom of the visible frame. Every busy state
        // has the same size, so between them the frame does not move.
        let size = hosting.fittingSize
        hosting.setFrameSize(size)
        let frame = DictationHUDMetrics.panelFrame(
            size: size, visibleFrame: Self.focusedScreen().visibleFrame)

        // The controller is AppKit, so it reads the system setting; the
        // view also reads its environment (`DictationHUDMotion.isReduced`).
        let motion = DictationHUDMotion(
            reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
        if animateIn {
            panel.alphaValue = 0
            panel.setFrame(frame.offsetBy(dx: 0, dy: -motion.entranceRise), display: false)
            panel.orderFrontRegardless()
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.18
                panel.animator().alphaValue = 1
                if motion.easesFrame { panel.animator().setFrame(frame, display: true) }
            }
        } else {
            // Only an outcome or a mic change can resize the capsule (the
            // busy states share one size); it eases, not snaps, unless
            // Reduce Motion is on.
            panel.orderFrontRegardless()
            if motion.easesFrame {
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = 0.18
                    panel.animator().setFrame(frame, display: true)
                }
            } else {
                panel.setFrame(frame, display: true)
            }
        }
    }

    /// The screen with keyboard focus: our key window's screen when there is
    /// one, else the screen under the pointer (the reliable proxy for which
    /// display the user is working on), else the main screen.
    private static func focusedScreen() -> NSScreen {
        if let key = NSApp.keyWindow?.screen { return key }
        let mouse = NSEvent.mouseLocation
        if let under = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) {
            return under
        }
        return NSScreen.main ?? NSScreen.screens[0]
    }

    /// The menu's "Preview Dictation Indicator": cycles the states for
    /// about two seconds each, then hands the panel back to the model,
    /// showing whatever the model is in now. A second preview restarts the
    /// cycle.
    func preview(holdKeyName: String) {
        previewTask?.cancel()
        previewGeneration &+= 1
        let generation = previewGeneration
        previewGate.begin()
        previewTask = Task { [weak self] in
            guard let self else { return }
            let states: [DictationHUDState] = [
                .recording(latched: false),
                .recording(latched: true),
                .transcribing,
                .outcome(DictationHUDContent.outcome(for: .inserted, holdKeyName: holdKeyName)),
                .outcome(DictationHUDContent.outcome(for: .keptOnClipboard, holdKeyName: holdKeyName)),
                .micChanged("Mic changed to AirPods Pro — could you say that again?"),
            ]
            for state in states {
                if Task.isCancelled { break }
                present(state, holdKeyName: holdKeyName)
                try? await Task.sleep(for: .seconds(2))
            }
            // Only the newest preview hands the panel back: a cancelled one
            // owns nothing — the preview that replaced it holds the gate
            // and the task handle, and ends both when it finishes.
            guard generation == previewGeneration else { return }
            previewTask = nil
            desiredState = previewGate.end()
            offerDesired()
        }
    }
}

/// The capsule itself: always dark, on the window's warm dark ground, one
/// soft shadow, and one line of state — gold level bars while recording
/// (the only ambient motion, and only then), a gold spinner while
/// transcribing, and for an ending or a mic change an amber caution mark
/// or a quiet dot (`DictationHUDGlyph`). All spacing on the 4-px grid.
struct DictationHUDView: View {
    let state: DictationHUDState
    let holdKeyName: String
    var level: Float = 0
    var liveText: String = ""
    var holdNotice: String? = nil
    /// How long the recording hold has run, or nil before its clock starts.
    var elapsed: TimeInterval? = nil
    var maximumHoldSeconds = DictationHoldPolicy.stage1.maximumHoldSeconds
    @Environment(\.accessibilityReduceMotion) private var environmentReduceMotion

    private var reduceMotion: Bool {
        DictationHUDMotion.isReduced(
            environment: environmentReduceMotion,
            system: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
    }

    private typealias Metrics = DictationHUDMetrics

    /// Recording and transcribing: one fixed capsule size for all of them.
    private var isBusy: Bool { Metrics.isBusy(state) }

    var body: some View {
        let margin = Metrics.shadowMargin
        capsule
            // Transparent room for the shadow, inside the window.
            .padding(EdgeInsets(
                top: margin.top, leading: margin.left, bottom: margin.bottom, trailing: margin.right))
            // The bezel is always dark regardless of system appearance — a
            // floating indicator must read on any wallpaper behind it.
            .environment(\.colorScheme, .dark)
    }

    private var capsule: some View {
        let column = Metrics.textColumnWidth(holdKeyName: holdKeyName, maximumHoldSeconds: maximumHoldSeconds)
        let width = Metrics.capsuleWidth(holdKeyName: holdKeyName, maximumHoldSeconds: maximumHoldSeconds)
        return HStack(spacing: Metrics.iconSpacing) {
            icon.frame(width: Metrics.iconWidth)
            VStack(alignment: .leading, spacing: Metrics.lineSpacing) {
                if let title = DictationHUDContent.title(for: state, livePreviewText: liveText) {
                    let line = Text(title)
                        .font(.system(size: Metrics.titleSize, weight: .semibold))
                        .foregroundStyle(DictationBrand.title)
                    if isBusy {
                        // One line, oldest words clipped, inside the FIXED
                        // column: a live preview that changes with every
                        // hypothesis never relayouts the capsule.
                        line.lineLimit(1).truncationMode(.head)
                    } else {
                        line.fixedSize(horizontal: false, vertical: true)
                    }
                }
                if let hint = holdNotice ?? DictationHUDContent.hint(
                    for: state, holdKeyName: holdKeyName,
                    elapsed: elapsed, limit: maximumHoldSeconds) {
                    // Tabular digits: a ticking clock must not jitter.
                    let line = Text(hint)
                        .font(.system(size: Metrics.hintSize).monospacedDigit())
                        .foregroundStyle(DictationBrand.hint)
                    if isBusy {
                        line.lineLimit(1)
                    } else {
                        line.fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .frame(width: isBusy ? column : nil, alignment: .leading)
        }
        .padding(.vertical, Metrics.verticalPadding)
        .padding(.horizontal, Metrics.horizontalPadding)
        // Busy states: exactly one size. An outcome or a mic change: at
        // least that, growing for its sentence up to the maximum.
        .frame(
            minWidth: width, maxWidth: isBusy ? width : Metrics.maximumCapsuleWidth,
            minHeight: Metrics.capsuleHeight, maxHeight: isBusy ? Metrics.capsuleHeight : nil,
            alignment: .leading)
        .background(DictationBrand.bezel, in: Capsule())
        .overlay(Capsule().strokeBorder(DictationBrand.strokeGradient, lineWidth: 0.5))
        .shadow(color: .black.opacity(0.35), radius: Metrics.shadowRadius, y: Metrics.shadowOffsetY)
        // One constant label per state. Combining the children read out
        // the live preview, which changes with every hypothesis.
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(DictationHUDAccessibility.label(for: state) ?? "")
    }

    @ViewBuilder
    private var icon: some View {
        switch state {
        case .hidden:
            EmptyView()
        case .recording:
            DictationHUDWaveform(level: level, animated: !reduceMotion)
        case .transcribing:
            ProgressView()
                .controlSize(.small)
                .tint(DictationBrand.wheat)
        case .micChanged, .outcome:
            if let glyph = DictationHUDGlyph(state: state) {
                let rgb = glyph.rgb
                Image(systemName: glyph.symbolName)
                    .font(.system(size: glyph.pointSize, weight: .semibold))
                    .foregroundStyle(Color(.sRGB, red: rgb.red, green: rgb.green, blue: rgb.blue,
                                           opacity: glyph.opacity))
            }
        }
    }
}

/// Three bars in the mark's gold, tracking the smoothed input level.
/// Silence is one flat line — no pulse, no breathing, nothing moving in a
/// quiet room, and no row of dots that reads as "loading…"
/// (`DictationHUDWaveformShape`). Each level update eases the target
/// (`DictationHUDContent.easedLevel`) and the bars glide to it over one
/// sample, so all motion comes from the voice itself. Reduce Motion skips
/// the easing and the glide and snaps to the current level.
private struct DictationHUDWaveform: View {
    let level: Float
    let animated: Bool
    @State private var smoothed: Float

    init(level: Float, animated: Bool) {
        self.level = level
        self.animated = animated
        _smoothed = State(initialValue: level)
    }

    private typealias Shape = DictationHUDWaveformShape

    var body: some View {
        let shape = Shape(peak: smoothed)
        ZStack {
            Capsule()
                .fill(DictationBrand.waveformGradient)
                .frame(width: Shape.width, height: Shape.flatLineHeight)
                .opacity(shape.isFlatLine ? 1 : 0)
            HStack(alignment: .center, spacing: Shape.barSpacing) {
                ForEach(Array(shape.barHeights.enumerated()), id: \.offset) { _, height in
                    RoundedRectangle(cornerRadius: Shape.barWidth / 2)
                        .fill(DictationBrand.waveformGradient)
                        .frame(width: Shape.barWidth, height: height)
                }
            }
            .opacity(shape.isFlatLine ? 0 : 1)
        }
        .frame(width: Shape.width, height: 14)
        .animation(animated ? .linear(duration: Shape.glideSeconds) : nil, value: smoothed)
        .onChange(of: level) { _, target in
            smoothed = animated
                ? DictationHUDContent.easedLevel(current: smoothed, target: target)
                : target
        }
    }
}

/// Offscreen render, honoured by every build including release (it is not
/// compiled out): launched with `DICTATION_HUD_RENDER_DIR`
/// set, the app writes one PNG per HUD state into that directory and exits
/// — the way to see the capsule on a host without Screen Recording
/// permission. Never reachable from the UI. It also checks what the PNGs
/// must show (`DictationHUDRenderCheck`: transparent edges, one size for
/// every busy state) and exits 1 when they do not.
enum DictationHUDRenderMode {
    @MainActor
    static func run(to directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let states: [(name: String, state: DictationHUDState, level: Float, liveText: String,
                      elapsed: TimeInterval?)] = [
            ("recording", .recording(latched: false), 0, "", nil),
            ("recording-latched", .recording(latched: true), 0, "", nil),
            // A hands-free hold 1:05 in, and a held key 10 s from the ceiling.
            ("recording-latched-elapsed", .recording(latched: true), 0, "", 65),
            ("recording-countdown", .recording(latched: false), 0, "",
             DictationHoldPolicy.stage1.maximumHoldSeconds - 10),
            // 0.08 peak ≈ -22 dB: ordinary speech, visibly raised bars.
            ("recording-level", .recording(latched: false), 0.08, "", nil),
            ("recording-live-text", .recording(latched: false), 0.08, "this is a live preview of my voice", nil),
            ("transcribing", .transcribing, 0, "", nil),
            ("mic-changed", .micChanged("Mic changed to AirPods Pro — could you say that again?"), 0, "", nil),
        ] + outcomes.map { name, kind in
            ("outcome-\(name)", .outcome(DictationHUDContent.outcome(for: kind, holdKeyName: "Right Command")),
             0, "", nil)
        }
        // The stability sequence: one recording with growing live text and
        // a varying level (speech-realistic peaks, ~0.03–0.12). Every frame
        // must be the SAME size — only bars and text change.
        let sequence: [(level: Float, liveText: String)] = [
            (0, ""),
            (0.03, "this"),
            (0.06, "this is a"),
            (0.09, "this is a live"),
            (0.05, "this is a live preview"),
            (0.12, "this is a live preview of my voice"),
        ]
        let frames = states + sequence.enumerated().map { index, frame in
            ("sequence-\(index)", DictationHUDState.recording(latched: false), frame.level, frame.liveText,
             TimeInterval?.none)
        }

        var rendered: [DictationHUDRenderCheck.Rendered] = []
        var problems: [String] = []
        for (name, state, level, liveText, elapsed) in frames {
            let view = DictationHUDView(
                state: state, holdKeyName: "Right Command", level: level, liveText: liveText,
                elapsed: elapsed)
            let url = directory.appendingPathComponent("hud-\(name).png")
            if let image = render(view, to: url, name: name, isBusy: DictationHUDMetrics.isBusy(state)) {
                rendered.append(image)
                report("\(url.path) written \(image.width)x\(image.height)")
            } else {
                problems.append("\(name): not written")
                report("\(url.path) FAILED")
            }
        }
        problems += DictationHUDRenderCheck.problems(rendered)
        guard problems.isEmpty else {
            for problem in problems { report("check FAILED: \(problem)") }
            exit(1)
        }
        let busy = rendered.first(where: \.isBusy).map { "\($0.width)x\($0.height)" } ?? "none"
        report("check passed: \(rendered.count) images, every edge transparent, every busy state \(busy)")
    }

    private static func report(_ line: String) {
        FileHandle.standardError.write(Data("[hud-render] \(line)\n".utf8))
    }

    /// Every ending the HUD can report.
    private static let outcomes: [(name: String, kind: DictationHUDOutcome.Kind)] = [
        ("text-recoverable", .textRecoverable),
        ("result-copied", .resultCopied),
        ("result-copy-failed", .resultCopyFailed),
        ("vocabulary-learned", .vocabularyLearned),
        ("inserted", .inserted),
        ("stopped-at-limit", .stoppedAtLimit(seconds: DictationHoldPolicy.stage1.maximumHoldSeconds)),
        ("kept-on-clipboard", .keptOnClipboard),
        ("not-inserted", .notInserted),
        ("accessibility-needed", .accessibilityNeeded),
        ("password-field", .passwordField),
        ("secure-input", .secureInput),
        ("nothing-heard", .nothingHeard),
        ("no-words", .noWords),
        ("microphone-silent", .microphoneSilent),
        ("microphone-blocked", .microphoneBlocked),
        ("cancelled", .cancelled),
        ("engine-restarting", .engineRestarting),
        ("engine-unavailable", .engineUnavailable),
        ("engine-error", .engineError),
        ("still-finishing", .stillFinishing),
        ("command-done", .commandDone),
    ]

    /// Renders the REAL `DictationHUDView` at its natural fitting size —
    /// the panel's size, shadow margin included — onto a clear bitmap, so
    /// the PNG is exactly what the panel draws, transparency and all.
    /// Returns what the check reads from it, or nil when nothing was
    /// written.
    @MainActor
    private static func render(
        _ view: DictationHUDView, to url: URL, name: String, isBusy: Bool
    ) -> DictationHUDRenderCheck.Rendered? {
        let hosting = NSHostingView(rootView: view)
        let size = hosting.fittingSize
        hosting.setFrameSize(size)
        hosting.layoutSubtreeIfNeeded()
        guard let viewRep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else {
            return nil
        }
        hosting.cacheDisplay(in: hosting.bounds, to: viewRep)
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        viewRep.draw(in: NSRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        guard let png = rep.representation(using: .png, properties: [:]),
              (try? png.write(to: url)) != nil
        else { return nil }
        var pixel = [Int](repeating: 0, count: 4)
        let edgeAlpha = DictationHUDRenderCheck.edgeAlpha(width: rep.pixelsWide, height: rep.pixelsHigh) { x, y in
            rep.getPixel(&pixel, atX: x, y: y)
            return pixel[3]
        }
        return DictationHUDRenderCheck.Rendered(
            name: name, width: rep.pixelsWide, height: rep.pixelsHigh, isBusy: isBusy, edgeAlpha: edgeAlpha)
    }
}
