#if canImport(Testing)
import Testing
@testable import DictationAppCore

/// Sparkle 2.9.6's two settings as SPUUpdaterSettings keeps them, with no
/// `SUAllowsAutomaticUpdates` in Info.plist: automatic downloads read as off
/// while automatic checks are, and a change to them is ignored then.
private struct SparkleSettings {
    var checks: Bool
    var storedDownloads: Bool

    var downloads: Bool {
        get { checks && storedDownloads }
        set { if checks { storedDownloads = newValue } }
    }

    var mode: DictationUpdateMode { DictationUpdateMode(automaticallyChecks: checks, automaticallyDownloads: downloads) }

    /// The order DictationUpdater writes a choice in.
    mutating func select(_ mode: DictationUpdateMode) {
        checks = mode.automaticallyChecks
        downloads = mode.automaticallyDownloads
    }
}

@Suite("The Updates choice")
struct DictationUpdateModeTests {
    @Test("a new install installs automatically, as before")
    func newInstall() {
        // Info.plist's SUEnableAutomaticChecks and SUAutomaticallyUpdate, both true.
        #expect(SparkleSettings(checks: true, storedDownloads: true).mode == .installAutomatically)
    }

    @Test("the 0.2.x switch reads as Install automatically when on and Off when off")
    func migration() {
        // On wrote both settings true. Off wrote checks false and left the
        // stored download setting as it was, which Sparkle then ignores.
        #expect(SparkleSettings(checks: true, storedDownloads: true).mode == .installAutomatically)
        #expect(SparkleSettings(checks: false, storedDownloads: true).mode == .off)
        #expect(SparkleSettings(checks: false, storedDownloads: false).mode == .off)
        #expect(DictationUpdateMode(automaticallyChecks: false, automaticallyDownloads: true) == .off)
    }

    @Test("each choice is read back as itself, from any other",
          arguments: DictationUpdateMode.allCases, DictationUpdateMode.allCases)
    func roundTrip(from: DictationUpdateMode, to: DictationUpdateMode) {
        var settings = SparkleSettings(checks: true, storedDownloads: true)
        settings.select(from)
        #expect(settings.mode == from)
        settings.select(to)
        #expect(settings.mode == to)
    }

    @Test("downloads written before checks would lose Notify only and Install automatically")
    func order() {
        // The order select(_:) avoids: from Off, Sparkle ignores the download
        // setting, then checks come on with whatever was stored before.
        var settings = SparkleSettings(checks: false, storedDownloads: true)
        settings.downloads = DictationUpdateMode.notifyOnly.automaticallyDownloads
        settings.checks = DictationUpdateMode.notifyOnly.automaticallyChecks
        #expect(settings.mode == .installAutomatically)
    }

    @Test("Notify only checks but never downloads on its own; Off does neither")
    func settings() {
        #expect(DictationUpdateMode.off.automaticallyChecks == false)
        #expect(DictationUpdateMode.off.automaticallyDownloads == false)
        #expect(DictationUpdateMode.notifyOnly.automaticallyChecks == true)
        #expect(DictationUpdateMode.notifyOnly.automaticallyDownloads == false)
        #expect(DictationUpdateMode.installAutomatically.automaticallyChecks == true)
        #expect(DictationUpdateMode.installAutomatically.automaticallyDownloads == true)
        #expect(DictationUpdateMode.allCases.map(\.label) == ["Off", "Notify only", "Install automatically"])
    }

    @Test("Off and Notify only say a downloaded update still installs, only while one waits")
    func waitingUpdate() {
        let note = "An update already downloaded still installs when Earlet quits."
        #expect(DictationUpdateMode.waitingUpdateNote == note)
        for mode in DictationUpdateMode.allCases {
            #expect(!mode.detail.contains(note))
            #expect(mode.detail(updateWaiting: false) == mode.detail)
        }
        #expect(DictationUpdateMode.off.detail(updateWaiting: true) == DictationUpdateMode.off.detail + " " + note)
        #expect(DictationUpdateMode.notifyOnly.detail(updateWaiting: true)
            == DictationUpdateMode.notifyOnly.detail + " " + note)
        // Its own text already says the build installs when Earlet next quits.
        #expect(DictationUpdateMode.installAutomatically.detail(updateWaiting: true)
            == DictationUpdateMode.installAutomatically.detail)
    }
}
#endif
