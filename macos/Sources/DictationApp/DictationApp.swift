import AppKit
import Combine
import DictationAppCore
import SwiftUI

@main
struct DictationApp: App {
    @NSApplicationDelegateAdaptor(DictationAppDelegate.self) private var delegate

    var body: some Scene {
        // One `Window`, not a `WindowGroup`: a group put File → New Window
        // (⌘N) in the menu and restored every window that was open at quit,
        // and each copy ran its own permission poll. The app now uses a single
        // `Window` for the same duplicates. It stays the first scene, so
        // SwiftUI opens it at launch; the delegate decides whether it stays.
        Window("Earlet", id: DictationMainWindow.id) {
            DictationWindow(model: delegate.model, updater: delegate.updater)
        }
        // Wide enough for the rail beside a pane at the 520 points the
        // single column used to open at, and tall enough for Home's header,
        // the hold key and setup without scrolling.
        .defaultSize(width: 720, height: 600)
        // The window draws its own top: the rail runs up under the traffic
        // lights and the pages leave room for them (DictationWindowView).
        .windowStyle(.hiddenTitleBar)
        .commands {
            // Belt and braces: a `Window` makes no second copy, but the
            // item would still be there doing nothing.
            CommandGroup(replacing: .newItem) {}
            // The macOS home of preferences. The window is where they live,
            // so ⌘, opens or brings it forward, on its Settings pane.
            CommandGroup(replacing: .appSettings) {
                DictationSettingsCommand()
            }
            // Where a Mac app keeps it: the app menu, under About.
            CommandGroup(after: .appInfo) {
                if let updater = delegate.updater, updater.isAvailable {
                    DictationCheckForUpdatesCommand(updater: updater)
                }
            }
        }
        MenuBarExtra {
            DictationMenu(model: delegate.model, hud: delegate.hud)
        } label: {
            DictationMenuIcon(model: delegate.model, result: delegate.model.recentResult, delegate: delegate)
        }
    }
}

/// Owns the model for the life of the process, so launch and quit can
/// reach it.
@MainActor
final class DictationAppDelegate: NSObject, NSApplicationDelegate {
    let model: DictationModel
    /// In-place updates; `nil` in the offscreen render runs, which must never
    /// reach the network.
    let updater: DictationUpdater?
    /// The floating indicator; created lazily so it exists only in a real
    /// GUI session.
    lazy var hud = DictationHUDController(model: model)
    /// Whether the window must come forward on its own (`DictationWindowAttention`).
    private var attention = DictationWindowAttention()
    private var attentionWatch: AnyCancellable?
    /// A request to show the window that the menu bar label has not served
    /// yet: only a SwiftUI view can call `openWindow`, and at launch the
    /// label may not exist when the request is made.
    private var windowRequested = false
    private var permissionWatch: Task<Void, Never>?

    override init() {
        // The log opens before the model is built: `.live()` reports a
        // learning store that would not open through it, and a sink that
        // has not started drops the line, which left a missing Vocabulary
        // section with no trace. The offscreen render runs are not
        // the app and stay out of the user's log.
        if !Self.isRenderRun {
            DictationLogSink.shared.start()
            let installation = DictationInstallation.current
            DictationLogSink.shared.log("launch \(installation.versionLabel) path=\(installation.path)")
        }
        model = DictationModel(environment: .live())
        updater = Self.isRenderRun ? nil : DictationUpdater()
        super.init()
    }

    /// One of the offscreen render modes below. Every build honours them,
    /// release included: they are not compiled out.
    private static var isRenderRun: Bool {
        let environment = ProcessInfo.processInfo.environment
        return environment["DICTATION_HUD_RENDER_DIR"] != nil || environment["DICTATION_WINDOW_RENDER_DIR"] != nil
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Offscreen HUD render (any build, release included), for hosts
        // without Screen Recording permission: writes one PNG per state and
        // exits.
        if let directory = ProcessInfo.processInfo.environment["DICTATION_HUD_RENDER_DIR"] {
            DictationHUDRenderMode.run(to: URL(fileURLWithPath: directory))
            exit(0)
        }
        // Offscreen window render, the same way: one PNG per
        // scenario, width and appearance, then exit.
        if let directory = ProcessInfo.processInfo.environment["DICTATION_WINDOW_RENDER_DIR"] {
            Task { @MainActor in
                await DictationWindowRenderMode.run(to: URL(fileURLWithPath: directory))
                exit(0)
            }
            return
        }
        // A write to a worker that has just died must fail with EPIPE, never
        // end the app. The worker pipe sets F_SETNOSIGPIPE too; this covers
        // every other descriptor.
        signal(SIGPIPE, SIG_IGN)
        model.refreshPermissions()
        // A login launch of an app left on, with every grant in, stays in
        // the menu bar. Anything else shows the window, and asks for it
        // rather than trusting SwiftUI's own launch-time open: a missing
        // grant is the one thing the user must see, and the checklist is
        // unmissable with the window forward.
        let quiet = !DictationLaunchWindow.showsWindow(
            loginLaunch: Self.launchedAsLoginItem(),
            enabledLastTime: DictationSettings.live().enabled,
            allPermissionsGranted: model.allPermissionsGranted)
        if quiet {
            DictationLogSink.shared.log("opened at login with dictation on; the window stays closed")
            DictationMainWindow.keepClosedAtLaunch()
        } else {
            requestWindow()
        }
        watchForAttention()
        // The grants are polled here, not in the window, so a closed window
        // still sees a revocation and still finishes a pending Enable.
        permissionWatch = Task { [model] in await model.watchPermissionsForLife() }
        Task { [weak self] in
            guard let self else { return }
            await model.launch()
            if DictationLaunchWindow.revealsAfterLaunch(wasQuiet: quiet, enabled: model.enabled) {
                requestWindow()
            }
        }
    }

    /// Whether macOS opened Dictation as a login item: the open-application
    /// Apple event says so while it is the current event, which it is
    /// during `applicationDidFinishLaunching`. Not yet seen on a device for
    /// an `SMAppService` login item; if the event does not say so, the
    /// launch simply counts as the user's and the window shows, as before.
    private static func launchedAsLoginItem() -> Bool {
        guard let event = NSAppleEventManager.shared().currentAppleEvent else { return false }
        return event.eventID == kAEOpenApplication
            && event.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
    }

    /// Back from System Settings, most often: re-read the grants at once
    /// rather than at the next tick of the poll.
    func applicationDidBecomeActive(_ notification: Notification) {
        model.refreshPermissions()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        Task {
            await model.prepareForQuit()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    /// A Dock or Finder click with no window on screen shows it, reopening
    /// it if it was closed. Answering `false` then tells AppKit it was
    /// handled; with a window on screen, AppKit's usual handling stands.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        guard !flag else { return true }
        requestWindow()
        return false
    }

    // ─── The window, from outside SwiftUI ───

    /// Asks the menu bar label, which lives as long as the process, to
    /// show the window: the label holds `openWindow`, the only way to
    /// reopen a closed one.
    func requestWindow() {
        windowRequested = true
        NotificationCenter.default.post(name: DictationMainWindow.showRequest, object: nil)
    }

    /// The label serves a request once.
    func takeWindowRequest() -> Bool {
        defer { windowRequested = false }
        return windowRequested
    }

    /// Watches the model at the app level, so a revoked grant or a refused
    /// key listener brings the window back even after it was closed; the
    /// observer used to live in the window's own view, which a closed
    /// window does not have.
    private func watchForAttention() {
        attention = DictationWindowAttention(
            windowAttention: model.windowAttention, allPermissionsGranted: model.allPermissionsGranted)
        attentionWatch = Publishers.CombineLatest4(
            model.$windowAttention, model.$microphoneGranted,
            model.$accessibilityGranted, model.$inputMonitoringGranted)
            // @Published emits before the property changes; the values
            // carried are the new ones, and the main queue runs the check
            // after the model's update has finished, menu tracking or not.
            .receive(on: DispatchQueue.main)
            .sink { [weak self] attention, microphone, accessibility, inputMonitoring in
                guard let self else { return }
                let allGranted = microphone && accessibility && inputMonitoring
                if self.attention.observe(windowAttention: attention, allPermissionsGranted: allGranted) {
                    // Every reason it comes forward on its own (a lost grant,
                    // a refused key listener, a recording to recover) is
                    // answered on Home.
                    DictationWindowNavigation.shared.pane = .home
                    self.requestWindow()
                }
            }
    }
}

/// Finding and showing the one main window.
@MainActor
enum DictationMainWindow {
    static let id = "main"
    /// Posted by the delegate for the menu bar label to show the window.
    static let showRequest = Notification.Name("ai.scam.dictation.showMainWindow")

    /// The main window when it is open, visible or minimized. A closed one
    /// may linger in `NSApp.windows`, so neither counts as open.
    static var open: NSWindow? {
        NSApp.windows.first { $0.canBecomeMain && ($0.isVisible || $0.isMiniaturized) }
    }

    /// Set by a quiet login launch until the window SwiftUI opens at launch
    /// has been closed.
    private static var closeWhenItAppears = false

    /// A quiet login launch: closes the window SwiftUI opens at launch, now
    /// if it exists, otherwise as soon as its content appears. Closed rather
    /// than ordered out, so SwiftUI knows it is closed and every way back
    /// (a Dock click, the menu, ⌘,) reopens it through `openWindow`.
    static func keepClosedAtLaunch() {
        if let window = NSApp.windows.first(where: \.canBecomeMain) {
            window.close()
        } else {
            closeWhenItAppears = true
        }
    }

    /// Called by the window's content when it appears.
    static func contentAppeared() {
        guard closeWhenItAppears else { return }
        closeWhenItAppears = false
        // After this layout pass: closing a window from inside its own
        // content's appearance is not something to do mid-update.
        Task { @MainActor in NSApp.windows.first(where: \.canBecomeMain)?.close() }
    }

    /// Brings the window forward and activates the app, reopening it with
    /// `openWindow` when it was closed, on `pane` when one is named (the
    /// pane it was left on otherwise). A `Window` scene never makes a
    /// second copy, so this is safe to call however it is reached.
    static func show(using openWindow: OpenWindowAction, pane: DictationWindowPane? = nil) {
        // Asked for after a quiet launch began: showing wins.
        closeWhenItAppears = false
        if let pane { DictationWindowNavigation.shared.pane = pane }
        if let window = open {
            if window.isMiniaturized { window.deminiaturize(nil) }
            window.makeKeyAndOrderFront(nil)
        } else {
            openWindow(id: id)
        }
        NSApp.activate()
    }
}

/// The menu bar icon: one glyph per state (`DictationMenuGlyph`), a
/// template symbol the menu bar tints itself, the waiting badge while
/// words or a recording wait in its menu (`DictationMenuIconState`), and
/// its state in words for VoiceOver. It is also the process's
/// `openWindow`: it exists for as long as the app runs, unlike the window,
/// so the delegate's requests to show the window come here.
private struct DictationMenuIcon: View {
    @ObservedObject var model: DictationModel
    /// Observed for the badge: words waiting after a refused paste live here.
    @ObservedObject var result: DictationRecentResult
    let delegate: DictationAppDelegate
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        picture
            .accessibilityLabel(model.menuIcon.accessibilityLabel)
            // A request made before this view existed (at launch).
            .task { serveWindowRequest() }
            .onReceive(NotificationCenter.default.publisher(for: DictationMainWindow.showRequest)) { _ in
                serveWindowRequest()
            }
    }

    /// One `Image` either way: a menu bar label draws an image, not a
    /// composed view, so the badge is drawn into the picture.
    private var picture: Image {
        let icon = model.menuIcon
        if icon.badged, let badged = DictationMenuBadgeImage.image(for: icon.glyph) {
            return Image(nsImage: badged).renderingMode(.original)
        }
        return Image(systemName: icon.glyph.symbolName)
    }

    private func serveWindowRequest() {
        if delegate.takeWindowRequest() { DictationMainWindow.show(using: openWindow) }
    }
}

/// The menu bar icon with the waiting badge: the state's symbol in the menu
/// bar's ink, and a dot in the caution colour at its top trailing corner
/// inside a clear ring that keeps it apart from the symbol. A template image
/// cannot carry a colour, so this one is drawn, and the drawing runs in the
/// appearance the menu bar draws it in: the ink and the dot are chosen for
/// a light or dark menu bar, and Increase Contrast, each time it is drawn.
/// The symbol keeps its default size, the one `Image(systemName:)` draws
/// without the badge, so the icon does not move when the dot comes and goes.
enum DictationMenuBadgeImage {
    /// The dot's diameter and the clear ring around it, in points.
    static let dotDiameter: CGFloat = 6
    static let ringWidth: CGFloat = 1.5

    @MainActor private static var made: [DictationMenuGlyph: NSImage] = [:]

    /// One image per glyph, made once: it redraws itself for each appearance.
    @MainActor
    static func image(for glyph: DictationMenuGlyph) -> NSImage? {
        if let image = made[glyph] { return image }
        let name = glyph.symbolName
        guard let symbol = NSImage(systemSymbolName: name, accessibilityDescription: nil) else { return nil }
        // Sendable, so nonisolated: AppKit may call it wherever the image is drawn.
        let image = NSImage(size: symbol.size, flipped: false) { @Sendable rect in
            DictationMenuBadgeImage.draw(symbolNamed: name, in: rect)
        }
        image.isTemplate = false
        // Drawn every time, never from a copy made in another appearance.
        image.cacheMode = .never
        made[glyph] = image
        return image
    }

    private static func draw(symbolNamed name: String, in rect: NSRect) -> Bool {
        guard let context = NSGraphicsContext.current?.cgContext,
              let symbol = NSImage(systemSymbolName: name, accessibilityDescription: nil)
        else { return false }
        let look = NSAppearance.currentDrawing().bestMatch(from: [
            .aqua, .darkAqua, .accessibilityHighContrastAqua, .accessibilityHighContrastDarkAqua,
        ])
        let appearance = DictationAppearance(
            dark: look == .darkAqua || look == .accessibilityHighContrastDarkAqua,
            increasedContrast: look == .accessibilityHighContrastAqua || look == .accessibilityHighContrastDarkAqua)
        // A layer of its own, so the tint and the clear ring act on this
        // drawing only, never on the menu bar it is drawn over.
        context.saveGState()
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        // The template symbol, then the menu bar's ink over its pixels only.
        context.setBlendMode(.normal)
        symbol.draw(in: rect)
        context.setBlendMode(.sourceAtop)
        NSColor.labelColor.withAlphaComponent(1).setFill()
        context.fill(rect)
        let dot = CGRect(x: rect.maxX - dotDiameter, y: rect.maxY - dotDiameter,
                         width: dotDiameter, height: dotDiameter)
        context.setBlendMode(.clear)
        context.fillEllipse(in: dot.insetBy(dx: -ringWidth, dy: -ringWidth))
        context.setBlendMode(.normal)
        let caution = DictationPalette.rgb(.caution, in: appearance)
        context.setFillColor(red: caution.red, green: caution.green, blue: caution.blue, alpha: 1)
        context.fillEllipse(in: dot)
        context.endTransparencyLayer()
        context.restoreGState()
        return true
    }
}

/// App menu → Settings… (⌘,). Its own view so it can read `openWindow`;
/// a `.commands` builder cannot hold the property wrapper itself.
private struct DictationCheckForUpdatesCommand: View {
    @ObservedObject var updater: DictationUpdater

    var body: some View {
        if updater.readyToInstall {
            Button("Restart to Update") { updater.restartToUpdate() }
        } else {
            Button("Check for Updates…") { updater.checkForUpdates() }
                .disabled(!updater.canCheck)
        }
    }
}

private struct DictationSettingsCommand: View {
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button("Settings…") { DictationMainWindow.show(using: openWindow, pane: .settings) }
            .keyboardShortcut(",")
    }
}

private struct DictationMenu: View {
    @ObservedObject var model: DictationModel
    let hud: DictationHUDController
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        // A short headline, not the status sentence: a menu item does not
        // wrap, and the longest sentences ran the menu 148 characters wide.
        Text(model.menuGlyph.menuHeadline(holdKeyName: model.holdKey.name))
        DictationRecentResultMenu(model: model, result: model.recentResult)
        DictationSavedTakeMenu(model: model)
        DictationLearningMenu(vocabulary: model.vocabulary)
        // Offsets as identity: the two separators are equal values.
        ForEach(Array(DictationMenuLayout.entries(for: model.menuItems).enumerated()), id: \.offset) { _, entry in
            switch entry {
            case .item(let item):
                itemButton(item)
            case .settings:
                Button("Settings…") { DictationMainWindow.show(using: openWindow, pane: .settings) }
                    .keyboardShortcut(",")
            case .previewIndicator:
                // Cycles every indicator state for a few seconds each — the
                // way to see the floating HUD without dictating.
                Button("Preview Earlet Indicator") { hud.preview(holdKeyName: model.holdKey.name) }
            case .separator:
                Divider()
            }
        }
    }

    @ViewBuilder
    private func itemButton(_ item: DictationMenuItem) -> some View {
        switch item {
        case .toggle:
            if !model.enabled && model.permissionStep != .ready {
                Button("Finish Setup…") { DictationMainWindow.show(using: openWindow, pane: .home) }
            } else {
                Button(model.enabled ? "Turn Earlet Off" : "Turn Earlet On") {
                    Task { await model.toggle() }
                }.disabled(model.busy || model.recording)
            }
        case .cancel:
            Button("Cancel Dictation") { Task { await model.cancel() } }
        case .showWindow:
            Button("Show Earlet") { DictationMainWindow.show(using: openWindow, pane: .home) }
        case .openLogFolder:
            Button("Open Log Folder") { model.openLogFolder() }
        case .quit:
            Button("Quit Earlet") { NSApplication.shared.terminate(nil) }
                .keyboardShortcut("q")
        }
    }
}
