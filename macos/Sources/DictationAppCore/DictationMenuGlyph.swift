import Foundation

/// What the menu bar menu offers, in order. Decided by the model so a test
/// can see it; the scene only renders it.
public enum DictationMenuItem: Hashable, Sendable {
    /// Enable or pause.
    case toggle
    /// Only while there is something to cancel (`DictationModel.canCancel`).
    case cancel
    /// Brings the window forward, reopening it if it was closed.
    case showWindow
    case openLogFolder
    case quit
}


extension DictationModel {
    public var menuItems: [DictationMenuItem] {
        var items: [DictationMenuItem] = [.toggle]
        if canCancel { items.append(.cancel) }
        items += [.showWindow, .openLogFolder, .quit]
        return items
    }
}

// ─── State glyph ───

/// What the menu bar icon, the menu's first line and the window's status
/// header say about dictation, resolved from the model's published state by
/// one pure function so the three surfaces cannot disagree.
///
/// With the window closed the menu bar icon is the only thing on screen.
/// It used to change only while recording, so paused, ready and a lost
/// permission all drew the same outline microphone, and a hold that did
/// nothing was the first sign of trouble. A prior implementation hit the same problem (four
/// states drawn with identical pixels hid a live alert) and gave every state
/// its own mark; this is that fix, with SF Symbols.
public enum DictationMenuGlyph: Equatable, Sendable, CaseIterable {
    /// Off: the user paused it, has not enabled it yet, or an Enable
    /// failed (the status sentence says which). Its words say "off", not
    /// "paused", because only the first is the user's choice.
    case paused
    /// Enable was clicked and the speech engine is loading.
    case starting
    /// On and idle: holding the key dictates.
    case ready
    /// A hold, or a double-tap, is recording.
    case recording
    /// Released; the words are on their way.
    case transcribing
    /// A grant is missing, so the hold key cannot work.
    case needsPermission
    /// Input Monitoring arrived after this process was refused it; only a
    /// relaunch can use it.
    case needsReopen
    /// The speech engine could not start.
    case engineFailed

    /// Precedence: a live dictation outranks everything, because it proves
    /// the grants it needed were there when it started and the user is
    /// looking at the HUD anyway. Then anything the user must fix, then the
    /// quiet states. `busy` while not enabled is an Enable still loading
    /// the engine; while enabled it is a release or a pause, which the
    /// other flags already describe.
    public static func resolve(
        enabled: Bool,
        busy: Bool,
        recording: Bool,
        transcribing: Bool,
        allPermissionsGranted: Bool,
        reopenNeeded: Bool,
        engineFailed: Bool
    ) -> DictationMenuGlyph {
        if recording { return .recording }
        if transcribing { return .transcribing }
        if !allPermissionsGranted { return .needsPermission }
        if reopenNeeded { return .needsReopen }
        if engineFailed { return .engineFailed }
        if enabled { return .ready }
        if busy { return .starting }
        return .paused
    }

    /// Whether the user has something to fix.
    public var needsAttention: Bool {
        switch self {
        case .needsPermission, .needsReopen, .engineFailed: return true
        case .paused, .starting, .ready, .recording, .transcribing: return false
        }
    }

    /// An SF Symbol present since macOS 11, drawn as a template image so
    /// the menu bar tints it for light, dark and a highlighted item.
    /// Starting keeps the paused mark: the microphone is not on yet.
    public var symbolName: String {
        switch self {
        case .paused, .starting: return "mic.slash"
        case .ready: return "mic"
        case .recording: return "mic.fill"
        case .transcribing: return "waveform"
        case .needsPermission, .needsReopen, .engineFailed: return "exclamationmark.triangle"
        }
    }

    /// What VoiceOver says for the menu bar item: the icon is its only
    /// content, and a shape is not an accessible channel on its own.
    public var accessibilityLabel: String {
        switch self {
        case .paused: return "Earlet, off"
        case .starting: return "Earlet, starting"
        case .ready: return "Earlet, on"
        case .recording: return "Earlet, listening"
        case .transcribing: return "Earlet, transcribing"
        case .needsPermission: return "Earlet, needs permission"
        case .needsReopen: return "Earlet, needs to reopen"
        case .engineFailed: return "Earlet, speech engine unavailable"
        }
    }

    /// The window's status headline: one short line over the status
    /// sentence, which carries the detail and the advice.
    public var headline: String {
        switch self {
        case .paused: return "Earlet is off"
        case .starting: return "Starting dictation…"
        case .ready: return "Earlet is on"
        case .recording: return "Listening…"
        case .transcribing: return "Transcribing…"
        case .needsPermission: return "Needs permission"
        case .needsReopen: return "Reopen Earlet to finish"
        case .engineFailed: return "Speech engine unavailable"
        }
    }

    /// The menu's first line. A menu item does not wrap, and the status
    /// sentence runs to 148 characters, so the menu gets this instead (40
    /// characters at most) and the window keeps the full sentence. Ready
    /// names the key, since the menu is where a user looks to recall it.
    public func menuHeadline(holdKeyName: String) -> String {
        self == .ready ? "On — hold \(holdKeyName)" : headline
    }

    /// The longest a menu headline may be.
    public static let menuHeadlineLimit = 40
}

extension DictationModel {
    /// The glyph for what the model publishes now.
    ///
    /// The model publishes a typed engine failure flag so this glyph does
    /// not depend on the wording of a status sentence.
    public var menuGlyph: DictationMenuGlyph {
        .resolve(
            enabled: enabled,
            busy: busy,
            recording: recording,
            transcribing: transcribing,
            allPermissionsGranted: allPermissionsGranted,
            reopenNeeded: reopenNeeded,
            engineFailed: engineFailed)
    }
}

// ─── Menu order ───

/// One line of the menu bar menu as the scene draws it: the model's items
/// (`DictationModel.menuItems`) plus what only the app shell offers.
public enum DictationMenuEntry: Hashable, Sendable {
    case item(DictationMenuItem)
    /// "Settings…", the same window ⌘, opens.
    case settings
    /// Cycles the floating indicator's states, the way to see it without
    /// dictating.
    case previewIndicator
    case separator
}

/// The menu's order, after the macOS convention: what acts on dictation
/// first, then the windows, then Quit alone at the bottom with the
/// indicator preview just above it. The preview used to sit below Quit.
public enum DictationMenuLayout {
    public static func entries(for items: [DictationMenuItem]) -> [DictationMenuEntry] {
        var entries: [DictationMenuEntry] = []
        for item in items {
            switch item {
            case .showWindow:
                entries += [.separator, .item(.showWindow), .settings]
            case .quit:
                entries += [.separator, .previewIndicator, .item(.quit)]
            case .toggle, .cancel, .openLogFolder:
                entries.append(.item(item))
            }
        }
        return entries
    }
}

// ─── Window attention ───

/// When the main window must come forward on its own: the model bumped
/// `windowAttention` (a refused key listener), or a grant that was in has
/// gone. The second matters because macOS never tells the key listener
/// about a revocation, so the poll seeing the grant go is the only signal,
/// and with the window closed the user would otherwise learn of it from a
/// hold that does nothing.
///
/// Edges, not levels: a grant that stays missing asks once, so closing the
/// window is respected, and a first run that starts without grants does not
/// count as a loss (the launch shows the window for that).
public struct DictationWindowAttention: Sendable {
    private var lastAttention: Int
    private var hadEveryGrant: Bool

    public init(windowAttention: Int = 0, allPermissionsGranted: Bool = false) {
        lastAttention = windowAttention
        hadEveryGrant = allPermissionsGranted
    }

    /// Records the model's values and answers whether the window must come
    /// forward because of them.
    public mutating func observe(windowAttention: Int, allPermissionsGranted: Bool) -> Bool {
        defer {
            lastAttention = windowAttention
            hadEveryGrant = allPermissionsGranted
        }
        if windowAttention != lastAttention { return true }
        return hadEveryGrant && !allPermissionsGranted
    }
}

// ─── Permission watch ───

/// How often the app re-reads the grants. macOS never notifies an app of a
/// grant or a revocation made in System Settings, so a poll is the only way
/// to see either; it used to run in the window's view, so with the window
/// closed nothing noticed.
public enum DictationPermissionWatch {
    /// While dictation is off, a grant is missing or a reopen is due: once a
    /// second, so the checklist flips as the user grants and an Enable that
    /// was waiting for the grants finishes at once.
    public static let settingUp: Duration = .seconds(1)
    /// On with every grant in: only to notice a revocation, so the menu bar
    /// icon and the window can say the hold key stopped working.
    public static let steady: Duration = .seconds(5)

    /// A pending Enable implies dictation is off, so the fast cadence covers
    /// it without reading the model's private flag.
    public static func interval(enabled: Bool, allPermissionsGranted: Bool, reopenNeeded: Bool) -> Duration {
        enabled && allPermissionsGranted && !reopenNeeded ? steady : settingUp
    }
}

extension DictationModel {
    /// The app's permission poll, for the life of the process: a tick
    /// (`permissionTick`, which also finishes a pending Enable), then a
    /// sleep of `DictationPermissionWatch.interval`, until cancelled.
    public func watchPermissionsForLife() async {
        while !Task.isCancelled {
            await permissionTick()
            let interval = DictationPermissionWatch.interval(
                enabled: enabled, allPermissionsGranted: allPermissionsGranted, reopenNeeded: reopenNeeded)
            try? await Task.sleep(for: interval)
        }
    }
}

// ─── Launch window ───

/// Whether the window shows at launch. Menu bar dictation tools start
/// silently at login, and Windows keeps its window in the notification area
/// on a sign-in launch with dictation on (`DictationController.Start`); the
/// Mac opened its window at every login instead. The same rule here: a
/// login launch of an app that was left on, with every grant in, stays in
/// the menu bar. Anything else shows the window, which is where a missing
/// grant or a first run is explained.
public enum DictationLaunchWindow {
    public static func showsWindow(loginLaunch: Bool, enabledLastTime: Bool, allPermissionsGranted: Bool) -> Bool {
        !(loginLaunch && enabledLastTime && allPermissionsGranted)
    }

    /// After a quiet launch has run `launch()`: if dictation did not come
    /// back on (the engine would not start, say), the window comes forward,
    /// so the failure is not left to a menu bar icon alone.
    public static func revealsAfterLaunch(wasQuiet: Bool, enabled: Bool) -> Bool {
        wasQuiet && !enabled
    }
}
