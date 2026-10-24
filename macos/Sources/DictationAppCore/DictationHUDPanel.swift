import AppKit
import Combine
import Foundation

// The floating HUD's window, as data and small pure gates the tests can
// read: what the panel must be so it never takes focus, how big the capsule
// is and where it goes and how it moves, how silence looks, what marks an
// ending, what VoiceOver hears, which model states reach it (and when the
// menu preview holds them back), when a fade may take it off screen, the
// heartbeat that heals a panel the model and the window disagree on, and
// what the offscreen render must prove. The AppKit plumbing that applies them
// is `DictationHUDController` in the app target, which has no test target,
// so every decision it makes that can be pulled out lives here instead.

// ─── The panel ───

/// The exact AppKit configuration of the HUD panel. Production builds the
/// panel through `makePanel()`, and the tests assert both these values and a
/// panel built that way, so a refactor cannot change one without the other
/// failing.
public enum DictationHUDPanelSpec {
    /// `.borderless`: no title bar, nothing for the user to manage.
    /// `.nonactivatingPanel` is the flag that matters: without it, ordering
    /// the HUD front activates Dictation, keyboard focus leaves the field
    /// being dictated into, and the paste lands nowhere.
    public static let styleMask: NSWindow.StyleMask = [.borderless, .nonactivatingPanel]

    /// `.screenSaver` (1000), not `.statusBar` (25). At `.statusBar` an
    /// open menu (`.popUpMenu`, 101) or a menu-bar popover covers the HUD,
    /// and the moment the user most needs to see that dictation is
    /// listening is often in the middle of some other UI.
    ///
    /// The cost: at this level the HUD also draws over notification
    /// banners. That is acceptable only because the panel is small, sits at
    /// the bottom centre, never takes focus and lets every click through
    /// (`ignoresMouseEvents`). If it ever takes clicks, this comes down.
    public static let level: NSWindow.Level = .screenSaver

    /// `.canJoinAllSpaces`: the HUD follows the user to whichever Space they
    /// dictate in. `.fullScreenAuxiliary`: it shows over a full-screen app
    /// without a Space switch, a primary place to dictate. `.stationary`:
    /// Mission Control does not move it around with the windows.
    public static let collectionBehavior: NSWindow.CollectionBehavior =
        [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]

    /// Click-through: the panel never swallows a click meant for the app
    /// underneath, including in the transparent margin around the capsule.
    public static let ignoresMouseEvents = true

    /// The HUD is up while ANOTHER app is active; the panel default (hide
    /// when this app deactivates) is exactly backwards here.
    public static let hidesOnDeactivate = false

    /// Apply the whole spec to a panel. `makePanel()` is the only caller in
    /// production; the tests call it too, so they check what ships.
    @MainActor
    public static func apply(to panel: NSPanel) {
        panel.styleMask = styleMask
        panel.isFloatingPanel = true
        panel.level = level
        panel.collectionBehavior = collectionBehavior
        panel.ignoresMouseEvents = ignoresMouseEvents
        panel.hidesOnDeactivate = hidesOnDeactivate
        // Transparent: the SwiftUI capsule draws its own surface and
        // shadow, and a window shadow on top would outline the margin.
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isReleasedWhenClosed = false
        panel.isMovableByWindowBackground = false
        // Not a document window: out of the Window menu and ⌘` cycling.
        panel.isExcludedFromWindowsMenu = true
        // The controller animates the panel itself; AppKit's own window
        // animation would add a second, unasked-for one.
        panel.animationBehavior = .none
    }

    /// The HUD panel, built and configured. Called once, when the
    /// controller is made: the press path only orders an existing window
    /// front, so the HUD appears within a frame of the key going down.
    @MainActor
    public static func makePanel() -> DictationHUDPanel {
        let panel = DictationHUDPanel(
            contentRect: NSRect(x: 0, y: 0, width: 10, height: 10),
            styleMask: styleMask, backing: .buffered, defer: false)
        apply(to: panel)
        return panel
    }
}

/// A panel that refuses key and main status outright. `.nonactivatingPanel`
/// stops the app from activating, but a panel can still become key when
/// its content wants text input, and SwiftUI content can change that. A
/// hard `false` makes the guarantee independent of what the view contains.
public final class DictationHUDPanel: NSPanel {
    public override var canBecomeKey: Bool { false }
    public override var canBecomeMain: Bool { false }
}

// ─── Size and place ───

/// The capsule's geometry. Every busy state (recording, latched,
/// transcribing) is ONE width and ONE height, so the capsule never resizes
/// or re-centres between them: it used to go 266 → 325 → 151 pt across a
/// double-tap and a release, and peripheral vision reads every edge moving
/// as an event. An outcome or a mic change is at least that wide and grows
/// for its sentence, up to `maximumCapsuleWidth`. Around the capsule sits a
/// transparent margin for its shadow.
public enum DictationHUDMetrics {
    /// Room around the capsule for its shadow, so the shadow is drawn
    /// whole: in a window exactly the capsule's size it was cut at the
    /// window's edge into a grey box behind the capsule's ends. A blur of
    /// radius r fades out about 2r from the edge (measured in the render: 0
    /// alpha 15 pt beside the capsule at r = 8), plus the offset on the side
    /// it falls to. The margin is as deep above as below because the
    /// offscreen render draws the offset upward (Core Graphics applies a
    /// shadow's offset in device space, which a flipped view does not flip),
    /// so one margin holds the shadow on screen and in the render alike.
    /// The panel lets clicks through, so the margin takes none.
    public static let shadowMargin = NSEdgeInsets(top: 20, left: 16, bottom: 20, right: 16)
    /// The capsule's one soft shadow. Radius 12 at 5 pt down needed a
    /// margin of about 30 pt to fade out whole; this reads the same at the
    /// capsule's size inside a smaller window.
    public static let shadowRadius: CGFloat = 8
    public static let shadowOffsetY: CGFloat = 3
    /// The visible capsule's bottom edge sits this far above the bottom of
    /// the screen's visible frame, which already excludes the Dock.
    public static let bottomOffset: CGFloat = 120
    /// Every state's height; two lines of state fit inside it.
    public static let capsuleHeight: CGFloat = 52
    /// The widest an outcome or a mic change may make the capsule; longer
    /// sentences wrap.
    public static let maximumCapsuleWidth: CGFloat = 420
    public static let horizontalPadding: CGFloat = 18
    public static let verticalPadding: CGFloat = 10
    /// The icon's slot: a waveform, a spinner or a symbol all centre in it.
    public static let iconWidth: CGFloat = 18
    public static let iconSpacing: CGFloat = 8
    public static let lineSpacing: CGFloat = 2
    public static let titleSize: CGFloat = 13
    public static let hintSize: CGFloat = 11
    /// The title line's font.
    public static var titleFont: NSFont { .systemFont(ofSize: titleSize, weight: .semibold) }
    /// The hint line's font: tabular digits, so a ticking clock keeps its
    /// width.
    public static var hintFont: NSFont { .monospacedDigitSystemFont(ofSize: hintSize, weight: .regular) }

    /// `text`'s width in `font`, rounded up to a whole point.
    public static func width(of text: String, font: NSFont) -> CGFloat {
        ceil((text as NSString).size(withAttributes: [.font: font]).width)
    }

    /// The text column every busy state shares: the widest line any of
    /// them can show. That is the live preview's caption budget, the
    /// Transcribing title, and every hint — held and hands-free, with a
    /// hands-free hold's clock and the countdown at the most digits a hold
    /// of `maximumHoldSeconds` reaches. Two points spare, so SwiftUI's own
    /// measurement never truncates a line this one measured as fitting.
    public static func textColumnWidth(holdKeyName: String, maximumHoldSeconds: TimeInterval) -> CGFloat {
        let titles = [
            DictationHUDContent.recordingTitleReferenceCJK,
            DictationHUDContent.recordingTitleReferenceWords,
            DictationHUDContent.title(for: .transcribing) ?? "",
        ]
        let limit = maximumHoldSeconds
        let countdown = DictationHUDContent.countdownSeconds
        // Just before the countdown is the longest clock a hands-free hold
        // shows; at the countdown's start, the longest "Stops in".
        let clocks: [TimeInterval?] = [nil, 0, max(0, limit - countdown - 1), max(0, limit - countdown)]
        var hints = [DictationHUDContent.hint(for: .transcribing, holdKeyName: holdKeyName)]
        for latched in [false, true] {
            for elapsed in clocks {
                hints.append(DictationHUDContent.hint(
                    for: .recording(latched: latched), holdKeyName: holdKeyName,
                    elapsed: elapsed, limit: limit))
            }
        }
        let widest = max(
            titles.map { width(of: $0, font: titleFont) }.max() ?? 0,
            hints.compactMap { $0 }.map { width(of: $0, font: hintFont) }.max() ?? 0)
        return widest + 2
    }

    /// Whether `state` is a busy state, drawn at exactly the one fixed
    /// size; an outcome or a mic change only starts from it.
    public static func isBusy(_ state: DictationHUDState) -> Bool {
        switch state {
        case .recording, .transcribing: return true
        case .hidden, .micChanged, .outcome: return false
        }
    }

    /// The busy states' one capsule width: the text column plus the icon
    /// and the padding.
    public static func capsuleWidth(holdKeyName: String, maximumHoldSeconds: TimeInterval) -> CGFloat {
        horizontalPadding * 2 + iconWidth + iconSpacing
            + textColumnWidth(holdKeyName: holdKeyName, maximumHoldSeconds: maximumHoldSeconds)
    }

    /// The panel's frame for content of `size` (the capsule plus its shadow
    /// margin): centred on the visible frame, with the CAPSULE's bottom
    /// edge, not the margin's, `bottomOffset` above the visible frame.
    public static func panelFrame(size: CGSize, visibleFrame: CGRect) -> CGRect {
        CGRect(
            x: visibleFrame.midX - size.width / 2,
            y: visibleFrame.minY + bottomOffset - shadowMargin.bottom,
            width: size.width, height: size.height)
    }
}

// ─── Motion ───

/// How the panel moves into place. Reduce Motion keeps the fade (a change
/// of opacity is not motion) and drops the rise into place and the ease
/// of the frame between sizes, which it used to run regardless.
public struct DictationHUDMotion: Equatable, Sendable {
    /// How far below its place a showing panel starts.
    public var entranceRise: CGFloat
    /// Whether a change of frame eases instead of snapping.
    public var easesFrame: Bool

    public init(reduceMotion: Bool) {
        entranceRise = reduceMotion ? 0 : 8
        easesFrame = !reduceMotion
    }

    /// Reduce Motion, read from both places. The SwiftUI environment value
    /// has been unreliable inside a non-activating panel (the app reads the
    /// system setting directly for that reason), and the controller, an
    /// AppKit object, has only the system setting.
    public static func isReduced(environment: Bool, system: Bool) -> Bool {
        environment || system
    }
}

// ─── The waveform ───

/// What the recording waveform draws for a smoothed peak level. Silence is
/// one flat line the bars' width: three bars at their floor are three
/// square dots, the universal "loading…" glyph, which made a quiet room
/// read as busy. Anything audible is the bars (`DictationHUDContent
/// .barHeights`).
public struct DictationHUDWaveformShape: Equatable, Sendable {
    public var isFlatLine: Bool
    public var barHeights: [CGFloat]

    public init(peak: Float) {
        let level = DictationHUDContent.normalizedLevel(peak: peak)
        isFlatLine = level == 0
        barHeights = DictationHUDContent.barHeights(level: level)
    }

    public static let barWidth: CGFloat = 3
    public static let barSpacing: CGFloat = 3
    public static let flatLineHeight: CGFloat = 2
    /// The bars' full width, which the flat line also spans.
    public static var width: CGFloat {
        let count = CGFloat(DictationHUDContent.waveformBarCount)
        return barWidth * count + barSpacing * (count - 1)
    }

    /// How long the bars glide from one level sample to the next: one
    /// sample of the ~12 Hz level cadence, so each glide ends as the next
    /// sample lands instead of the bars jumping at every sample. Reduce
    /// Motion snaps instead.
    public static let glideSeconds: TimeInterval = 0.085
}

// ─── Endings ───

/// The mark beside an ending or a mic change; the live states carry their
/// own (the waveform, the spinner). Caution is amber, never red: red reads
/// as broken or dangerous, and none of these is — the clipboard fallback
/// even kept the words. A neutral ending gets a quiet dot, not a
/// checkmark, because "Nothing heard" and "Cancelled" are neutral too and
/// are not a success. The indicator draws the same split.
public enum DictationHUDGlyph: Equatable, Sendable {
    case caution
    case neutral

    /// The mark for `state`, or nil for a state that has none of its own.
    public init?(state: DictationHUDState) {
        switch state {
        case .micChanged: self = .caution
        case .outcome(let outcome): self = outcome.tone == .caution ? .caution : .neutral
        case .hidden, .recording, .transcribing: return nil
        }
    }

    public var symbolName: String {
        switch self {
        case .caution: return "exclamationmark.triangle.fill"
        case .neutral: return "circle.fill"
        }
    }

    /// The symbol's point size: a full-size triangle, a small dot.
    public var pointSize: CGFloat {
        switch self {
        case .caution: return 13
        case .neutral: return 6
        }
    }

    /// sRGB, 0…1. Caution is amber-400 (#FBBF24), the amber this indicator
    /// uses; neutral is the hint line's white.
    public var rgb: (red: Double, green: Double, blue: Double) {
        switch self {
        case .caution: return (0xFB / 255, 0xBF / 255, 0x24 / 255)
        case .neutral: return (1, 1, 1)
        }
    }

    public var opacity: Double {
        switch self {
        case .caution: return 1
        case .neutral: return 0.65
        }
    }
}

// ─── What VoiceOver hears ───

/// The HUD's spoken words: one constant line per state, never the live
/// preview, the level or the clock, so VoiceOver does not re-read the HUD
/// with every hypothesis and every second. A window that is never key and
/// is ordered front from the background is not announced on its own, so
/// these are also what `DictationHUDAnnouncer` hands VoiceOver to speak,
/// for the states it may speak in.
public enum DictationHUDAccessibility {
    public static func label(for state: DictationHUDState) -> String? {
        switch state {
        case .hidden: return nil
        case .recording(latched: false): return "Listening"
        case .recording(latched: true): return "Listening, hands-free"
        case .transcribing: return "Transcribing"
        case .micChanged(let message): return message
        case .outcome(let outcome):
            guard let detail = outcome.detail else { return outcome.title }
            return "\(outcome.title). \(detail)"
        }
    }
}

/// What to announce as the HUD changes: a state's label, once, when the
/// spoken words change, and only in a state where the microphone can no
/// longer hear it (`speaksIn`). Hiding resets it, so a repeated ending is
/// heard again; a new level, caption or clock is the same state and says
/// nothing.
public struct DictationHUDAnnouncer: Sendable {
    private var lastSpoken: String?

    public init() {}

    /// Whether VoiceOver may be asked to speak while `state` is on screen.
    /// Dictation records without echo cancellation, so VoiceOver speaking
    /// through the speakers while the clip is open is recorded with the
    /// user's words and typed into their text. Listening, held or
    /// hands-free, is the open microphone itself. Transcribing is not safe
    /// either: the model shows it at the release, but the clip keeps
    /// filling until the transcription stops the capture, which first waits
    /// for the engine to be ready and for the live preview to stop (allowed
    /// two seconds), and the HUD is told nothing when that happens. An
    /// ending and the mic-changed sentence are shown once the clip is
    /// closed or thrown away, so those are spoken; the ending is the news
    /// anyway. What this cannot stop: an ending still being spoken when the
    /// user presses again is heard by that next hold.
    public static func speaksIn(_ state: DictationHUDState) -> Bool {
        switch state {
        case .recording, .transcribing: return false
        case .hidden, .micChanged, .outcome: return true
        }
    }

    public mutating func announcement(for state: DictationHUDState) -> String? {
        let spoken = DictationHUDAccessibility.label(for: state)
        // Remembered even when not spoken: a dictation in between is a
        // change, so its ending is heard even if it matches the last one.
        defer { lastSpoken = spoken }
        guard Self.speaksIn(state), let spoken, spoken != lastSpoken else { return nil }
        return spoken
    }
}

// ─── Following the model ───

extension Publisher where Output == DictationHUDState, Failure == Never {
    /// Where the HUD state ended up, delivered on the main queue's next
    /// turn. The model publishes every change as it makes it, and several
    /// can land inside one main-actor turn; the panel must show only where
    /// the turn ended, or a state the model merely passed through (a
    /// release that stops recording a moment before it publishes
    /// Transcribing) reaches the screen as a blink. Each delivery reads the
    /// LATEST state rather than the one that scheduled it, so every change
    /// of one turn collapses into its last; repeats are dropped, being no
    /// change on screen. The indicator reads its phase on the next run-loop
    /// tick for the same reason.
    ///
    /// The main queue, not `RunLoop.main`: Combine's run-loop scheduler
    /// performs in the default mode only, and while a menu is open (the
    /// app's own menu-bar menu included) AppKit runs the main run loop in
    /// event tracking, so every change waited for the menu to close and a
    /// hold started with it open showed nothing. The hold key's event tap
    /// and the model's main-actor work both carry on there; the main queue
    /// is serviced in every common mode, which in an app includes event
    /// tracking and modal panels.
    public func settled() -> AnyPublisher<DictationHUDState, Never> {
        let latest = DictationHUDLatestState()
        return handleEvents(receiveOutput: { latest.value = $0 })
            .receive(on: DispatchQueue.main)
            .map { _ in latest.value }
            .removeDuplicates()
            .eraseToAnyPublisher()
    }
}

/// The last state published: written as the model publishes it, read one
/// turn later. In production both happen on the main thread (the model is
/// main-actor isolated and delivery is on the main queue).
private final class DictationHUDLatestState: @unchecked Sendable {
    var value: DictationHUDState = .hidden
}

// ─── Hiding ───

/// When a hide's fade may take the panel off screen. Every presentation,
/// hidden or visible, takes a new generation; a fade's completion may
/// order the panel out only if nothing was presented after it and the HUD
/// is still hidden. Without it, a fade that began on one change and
/// completed after the next state was shown ordered the panel out under
/// that state ("Transcribing…", or the next hold's "Listening"), and
/// nothing showed it again until the state changed.
public struct DictationHUDHideGate: Sendable {
    public private(set) var generation = 0

    public init() {}

    /// A new presentation. Returns its generation, which a hide hands to
    /// `mayOrderOut` when its fade completes.
    public mutating func present() -> Int {
        generation &+= 1
        return generation
    }

    /// Whether the fade of the hide presented at `hideGeneration` may order
    /// the panel out, given the state the HUD is in now.
    public func mayOrderOut(hideGeneration: Int, state: DictationHUDState) -> Bool {
        hideGeneration == generation && state == .hidden
    }
}

// ─── The menu preview ───

/// Who owns the panel while the menu's "Preview Dictation Indicator"
/// cycles its states. While a preview runs, the model's states are kept
/// but not shown, so a real dictation and the preview never interleave on
/// one panel; when it ends, the model's latest state is shown at once,
/// whatever it is. The preview used to end by hiding the panel, which hid a
/// hold that had started during it until the model next changed.
public struct DictationHUDPreviewGate: Sendable {
    public private(set) var isRunning = false
    /// The model's latest state, whether or not it is on screen.
    public private(set) var modelState: DictationHUDState = .hidden

    public init() {}

    /// The model published `state`: the state to offer the panel now, or
    /// nil while a preview owns it.
    public mutating func modelOffer(_ state: DictationHUDState) -> DictationHUDState? {
        modelState = state
        return isRunning ? nil : state
    }

    public mutating func begin() {
        isRunning = true
    }

    /// Ends the preview; returns the model's state, to offer the panel now.
    public mutating func end() -> DictationHUDState {
        isRunning = false
        return modelState
    }
}

// ─── Reconciling ───

/// The panel's truth against the model's, read on a slow heartbeat by the
/// controller. Every way the HUD has ever gone silent ends the same way:
/// the model wants a visible state while the panel sits off screen (or,
/// rarer, the reverse). One instance ran 34 hours with the press path
/// provably alive — capture started, sessions transcribed — while the
/// panel was never ordered front; a fresh instance of the same build
/// showed it on the first hold (2026-10-06). Whatever dropped the state
/// between the model and the window, this heartbeat is the floor under all
/// of them: a disagreement heals within a second instead of lasting until
/// the app restarts, and each heal leaves one log line, so the mechanism
/// is named the next time it happens instead of mysterious.
public enum DictationHUDReconcile {
    /// What the controller does about a disagreement.
    public enum Action: Equatable, Sendable {
        /// The model wants a visible state and the panel is off screen:
        /// offer the desired state again.
        case reoffer
        /// The model wants hidden and the panel is on screen: take it
        /// away.
        case orderOut
    }

    /// The heartbeat's period. One second keeps a dropped state inside the
    /// "did it just flicker?" range rather than "the HUD is gone".
    public static let intervalSeconds: TimeInterval = 1

    /// A hide's fade runs 0.14 s. A fade still counting after this long
    /// lost its completion, and the panel is on screen for nothing — the
    /// heartbeat may take it away. Well clear of the fade, so a healthy
    /// hide never reads as stale.
    public static let fadeStaleAfterSeconds: TimeInterval = 1

    /// The action for one heartbeat's reading, or nil when the panel and
    /// the model agree. A menu preview owns the panel while it runs; the
    /// controller skips the check then, so it is not a case here.
    public static func action(desiredHidden: Bool, panelVisible: Bool) -> Action? {
        switch (desiredHidden, panelVisible) {
        case (false, false): return .reoffer
        case (true, true): return .orderOut
        case (false, true), (true, false): return nil
        }
    }
}

// ─── The render check ───

/// What the offscreen render (`DICTATION_HUD_RENDER_DIR`) proves about the
/// PNGs it writes, so a run that breaks either property fails instead of
/// only producing pictures: every image is transparent on its outermost
/// rows and columns (the shadow is drawn whole, not cut at the window's
/// edge), and every busy state is one size (the capsule never resizes
/// between them).
public enum DictationHUDRenderCheck {
    public struct Rendered: Equatable, Sendable {
        public var name: String
        public var width: Int
        public var height: Int
        /// A busy state (`DictationHUDMetrics.isBusy`): must share one size.
        public var isBusy: Bool
        /// The largest alpha, 0…255, on the outermost rows and columns.
        public var edgeAlpha: Int

        public init(name: String, width: Int, height: Int, isBusy: Bool, edgeAlpha: Int) {
            self.name = name
            self.width = width
            self.height = height
            self.isBusy = isBusy
            self.edgeAlpha = edgeAlpha
        }
    }

    /// Every way the render broke a property; empty when both hold. Too
    /// little to compare is a problem too, never a pass.
    public static func problems(_ images: [Rendered]) -> [String] {
        var problems: [String] = []
        if images.isEmpty { problems.append("nothing was rendered") }
        for image in images where image.edgeAlpha > 0 {
            problems.append("\(image.name): alpha \(image.edgeAlpha) on its edge, so the shadow is cut there")
        }
        let busy = images.filter(\.isBusy)
        if busy.count < 2 {
            problems.append("\(busy.count) busy state(s) rendered, too few to compare their sizes")
        } else if Set(busy.map { "\($0.width)x\($0.height)" }).count > 1 {
            problems.append("busy states differ in size: "
                + busy.map { "\($0.name) \($0.width)x\($0.height)" }.joined(separator: ", "))
        }
        return problems
    }

    /// The largest alpha on the outermost rows and columns of a
    /// `width` x `height` image whose alpha at (x, y) is `alpha(x, y)`.
    public static func edgeAlpha(width: Int, height: Int, alpha: (Int, Int) -> Int) -> Int {
        guard width > 0, height > 0 else { return 0 }
        var largest = 0
        for x in 0..<width {
            largest = max(largest, alpha(x, 0), alpha(x, height - 1))
        }
        for y in 0..<height {
            largest = max(largest, alpha(0, y), alpha(width - 1, y))
        }
        return largest
    }
}
