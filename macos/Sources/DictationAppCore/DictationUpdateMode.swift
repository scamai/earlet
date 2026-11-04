import Foundation

/// The Updates choice in Settings (docs/design-macos.md#updates), kept as
/// Sparkle's own two settings rather than under a key of its own.
///
/// Off never checks. Notify only checks once a day and shows a newer version,
/// which downloads and installs only when the user says so. Install
/// automatically checks once a day, downloads a newer version in the
/// background and installs it when Earlet quits; it is what a new install
/// starts with (`SUEnableAutomaticChecks` and `SUAutomaticallyUpdate` in
/// Info.plist). A build it has already downloaded still installs when Earlet
/// quits after a switch to Off or Notify only (`waitingUpdateNote`).
///
/// Reading the choice back from Sparkle's settings is the migration from the
/// single "Update Earlet automatically" switch of 0.2.x, which set both
/// together: a copy that had it on reads as Install automatically and one that
/// had it off as Off, with nothing rewritten. A copy taken back to 0.2.x reads
/// the same two settings.
public enum DictationUpdateMode: String, CaseIterable, Identifiable, Sendable {
    case off = "off"
    case notifyOnly = "notify-only"
    case installAutomatically = "install-automatically"

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .off: return "Off"
        case .notifyOnly: return "Notify only"
        case .installAutomatically: return "Install automatically"
        }
    }

    /// Under Off and Notify only while a downloaded update waits for Earlet to
    /// quit. Neither choice stops it: Sparkle 2.9.6 keeps that session running
    /// (SPUAutomaticUpdateDriver.m), does nothing with a changed setting while a
    /// session is in progress (SPUUpdater.m) and has no public call that
    /// cancels a staged install.
    public static let waitingUpdateNote = "An update already downloaded still installs when Earlet quits."

    /// What Settings shows under the row: `detail`, and under Off and Notify
    /// only, while `updateWaiting`, `waitingUpdateNote` as well.
    public func detail(updateWaiting: Bool) -> String {
        guard updateWaiting, self != .installAutomatically else { return detail }
        return detail + " " + Self.waitingUpdateNote
    }

    /// What the choice does, under its row in Settings.
    public var detail: String {
        switch self {
        case .off:
            return "Earlet doesn't look for a newer version. Check for Updates… below still asks GitHub when you click it."
        case .notifyOnly:
            return "Once a day Earlet asks GitHub for a newer version and tells you when there is one. It downloads and installs it only if you choose to."
        case .installAutomatically:
            return "Once a day Earlet asks GitHub for a newer version, downloads it and installs it when Earlet next quits. Your settings, words and permissions stay."
        }
    }

    /// The choice Sparkle's `automaticallyChecksForUpdates` and
    /// `automaticallyDownloadsUpdates` amount to. Checks decide first: a copy
    /// that never checks never downloads, and Sparkle reports downloads as off
    /// while checks are off anyway (SPUUpdaterSettings, with no
    /// `SUAllowsAutomaticUpdates` in Info.plist).
    public init(automaticallyChecks: Bool, automaticallyDownloads: Bool) {
        if !automaticallyChecks {
            self = .off
        } else {
            self = automaticallyDownloads ? .installAutomatically : .notifyOnly
        }
    }

    /// Sparkle's `automaticallyChecksForUpdates` for this choice. Write it
    /// before `automaticallyDownloads`: Sparkle ignores a change to automatic
    /// downloads while automatic checks are off.
    public var automaticallyChecks: Bool { self != .off }

    /// Sparkle's `automaticallyDownloadsUpdates` for this choice.
    public var automaticallyDownloads: Bool { self == .installAutomatically }
}
