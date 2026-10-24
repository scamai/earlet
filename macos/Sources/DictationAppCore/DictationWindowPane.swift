import Combine
import Foundation

// The main window's rail: which panes it has, in what order, and which
// one a request to show the window opens. The views are in the app target
// (DictationWindowView.swift); what decides them is here, where tests reach.

/// The main window's panes, in rail order. Home is the daily surface:
/// what dictation is doing, the hold key, setup while a grant is missing,
/// anything waiting for the user (words to copy, a saved recording, a
/// learning receipt) and the last seven days' counts. History lists the saved
/// dictations. Vocabulary holds the learned and taught words, a list that can
/// grow to hundreds of rows, so it no longer pushes the preferences off
/// screen. Settings holds the preferences and Troubleshooting.
public enum DictationWindowPane: String, CaseIterable, Identifiable, Sendable {
    case home
    case history
    case vocabulary
    case settings

    public var id: String { rawValue }

    /// The rail item and the window title while the pane is shown.
    public var title: String {
        switch self {
        case .home: return "Home"
        case .history: return "History"
        case .vocabulary: return "Vocabulary"
        case .settings: return "Settings"
        }
    }

    /// The rail item's SF Symbol.
    public var symbolName: String {
        switch self {
        case .home: return "house"
        case .history: return "clock.arrow.circlepath"
        case .vocabulary: return "character.book.closed"
        case .settings: return "gearshape"
        }
    }
}

/// The pane the one main window shows. One per process, owned by the app
/// and shared by everything that opens the window: Settings… (⌘,) opens
/// Settings, Show Dictation and the window's own attention requests open
/// Home, Review vocabulary… opens Vocabulary. The render mode makes its own.
@MainActor
public final class DictationWindowNavigation: ObservableObject {
    public static let shared = DictationWindowNavigation()

    @Published public var pane: DictationWindowPane

    public init(pane: DictationWindowPane = .home) {
        self.pane = pane
    }
}

extension DictationModel {
    /// Whether Home holds something only the user can finish: a missing
    /// grant or a due restart (on or off: a grant revoked while dictation is
    /// on counts too), words still waiting after a refused paste, a
    /// recording kept for Recover Last Dictation, or a recording file that
    /// could not be removed. The rail marks Home while it is true, so it
    /// is seen from Vocabulary or Settings too. Paused by the user is not on
    /// the list, and neither is a learning receipt: neither needs anything
    /// done.
    public var homeNeedsAttention: Bool {
        permissionStep != .ready
            || savedTake != nil
            || pendingRecordingCleanup
            || recentResult.entry?.needsRecovery == true
    }
}
