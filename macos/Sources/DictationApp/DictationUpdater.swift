import AppKit
import Combine
import DictationAppCore
import Sparkle

/// In-place updates (docs/design-macos.md#updates): Sparkle reads the signed
/// appcast once a day, downloads a newer build in the background, checks it
/// against the EdDSA key in Info.plist and Apple's code signature, and
/// installs it when Earlet quits, at the same path and under the same
/// Developer ID, so the microphone, Accessibility and Input Monitoring grants
/// carry over. A copy without a feed (`DictationUpdateFeed`) never starts it.
@MainActor
final class DictationUpdater: NSObject, ObservableObject {
    /// Whether this copy follows a feed at all.
    let isAvailable: Bool
    @Published private(set) var canCheck = false
    @Published private(set) var automaticallyChecks = false
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
            .sink { [weak self] in self?.automaticallyChecks = $0 })
    }

    func checkForUpdates() {
        controller?.checkForUpdates(nil)
    }

    func setAutomaticallyChecks(_ on: Bool) {
        guard let updater = controller?.updater else { return }
        updater.automaticallyChecksForUpdates = on
        // Checking and installing go together: a found update is applied the
        // next time Earlet quits, without a prompt.
        updater.automaticallyDownloadsUpdates = on
        DictationLogSink.shared.log("updates: automatic checks \(on ? "on" : "off")")
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
