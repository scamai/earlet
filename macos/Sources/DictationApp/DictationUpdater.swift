import AppKit
import Combine
import DictationAppCore
import Sparkle

/// In-place updates (docs/design-macos.md#updates): Sparkle reads the signed
/// appcast once a day, downloads a newer build in the background, checks it
/// against the EdDSA key in Info.plist and Apple's code signature, and
/// installs it when Earlet quits, at the same path and under the same
/// Developer ID, so the microphone, Accessibility and Input Monitoring grants
/// carry over. The Updates choice can make it only tell, or not check at all
/// (`DictationUpdateMode`). No cookie outlives a check
/// (`DictationUpdateCookies`). A copy without a feed (`DictationUpdateFeed`)
/// never starts it.
@MainActor
final class DictationUpdater: NSObject, ObservableObject {
    /// Whether this copy follows a feed at all.
    let isAvailable: Bool
    @Published private(set) var canCheck = false
    @Published private(set) var mode = DictationUpdateMode.off
    /// Set when a downloaded update is waiting for Earlet to quit.
    @Published private(set) var readyToInstall = false

    private let feed: URL?
    private var controller: SPUStandardUpdaterController?
    private var installNow: (() -> Void)?
    private var watches: [AnyCancellable] = []

    init(feed: URL? = DictationUpdateFeed.current) {
        self.feed = feed
        isAvailable = feed != nil
        super.init()
        guard feed != nil else { return }
        let controller = SPUStandardUpdaterController(startingUpdater: false, updaterDelegate: self,
                                                      userDriverDelegate: nil)
        self.controller = controller
        // What an older copy, or a check cut short by quitting, left behind.
        DictationUpdateCookies.forget()
        do {
            try controller.updater.start()
        } catch {
            // Only the error's kind: its text can carry a path.
            DictationLogSink.shared.log("updates: could not start (\(type(of: error)))")
            return
        }
        let updater = controller.updater
        watches.append(updater.publisher(for: \.canCheckForUpdates).receive(on: RunLoop.main)
            .sink { [weak self] in self?.canCheck = $0 })
        watches.append(updater.publisher(for: \.automaticallyChecksForUpdates).receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.readMode() })
        watches.append(updater.publisher(for: \.automaticallyDownloadsUpdates).receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.readMode() })
        readMode()
    }

    func checkForUpdates() {
        controller?.checkForUpdates(nil)
    }

    func select(_ mode: DictationUpdateMode) {
        guard let updater = controller?.updater else { return }
        // Checks first: Sparkle ignores a change to automatic downloads while
        // automatic checks are off.
        updater.automaticallyChecksForUpdates = mode.automaticallyChecks
        updater.automaticallyDownloadsUpdates = mode.automaticallyDownloads
        DictationLogSink.shared.log("updates: set to \(mode.rawValue)")
    }

    /// The choice as Sparkle's settings now stand, which is also how the
    /// 0.2.x switch is carried over (`DictationUpdateMode`).
    private func readMode() {
        guard let updater = controller?.updater else { return }
        mode = DictationUpdateMode(automaticallyChecks: updater.automaticallyChecksForUpdates,
                                   automaticallyDownloads: updater.automaticallyDownloadsUpdates)
    }

    /// Installs the waiting update now; Sparkle quits and relaunches Earlet.
    func restartToUpdate() {
        installNow?()
    }
}

// Sparkle calls its delegate on the main thread, so the conformance is
// isolated to the main actor like the rest of this type.
extension DictationUpdater: @MainActor SPUUpdaterDelegate {
    func feedURLString(for updater: SPUUpdater) -> String? {
        feed?.absoluteString
    }

    // Every check, scheduled or asked for, starts and ends with an empty
    // cookie store, so no cookie github.com sets ties one check to the next.
    func updater(_ updater: SPUUpdater, mayPerform updateCheck: SPUUpdateCheck) throws {
        DictationUpdateCookies.forget()
    }

    func updater(_ updater: SPUUpdater, didFinishUpdateCycleFor updateCheck: SPUUpdateCheck, error: (any Error)?) {
        DictationUpdateCookies.forget()
    }

    // The log records builds and outcomes, never URLs or error text.
    func updater(_ updater: SPUUpdater, didFindValidUpdate item: SUAppcastItem) {
        DictationLogSink.shared.log("updates: found build \(item.versionString)")
    }

    func updaterDidNotFindUpdate(_ updater: SPUUpdater) {
        DictationLogSink.shared.log("updates: up to date")
    }

    func updater(_ updater: SPUUpdater, didDownloadUpdate item: SUAppcastItem) {
        DictationLogSink.shared.log("updates: downloaded build \(item.versionString)")
    }

    func updater(_ updater: SPUUpdater, failedToDownloadUpdate item: SUAppcastItem, error: any Error) {
        DictationLogSink.shared.log("updates: download failed (code \((error as NSError).code))")
    }

    func updater(_ updater: SPUUpdater, didAbortWithError error: any Error) {
        DictationLogSink.shared.log("updates: check ended (code \((error as NSError).code))")
    }

    func updater(_ updater: SPUUpdater, willInstallUpdateOnQuit item: SUAppcastItem,
                 immediateInstallationBlock: @escaping () -> Void) -> Bool {
        installNow = immediateInstallationBlock
        readyToInstall = true
        DictationLogSink.shared.log("updates: build \(item.versionString) installs when Earlet quits")
        return true
    }
}
