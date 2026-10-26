import Foundation

/// Where this copy looks for updates (docs/design-macos.md#updates).
///
/// A packaged build names its feed in Info.plist (`SUFeedURL`, the appcast on
/// the project's GitHub Releases). `DICTATION_UPDATE_FEED_URL` replaces it for
/// one launch, which is how an update is tried against a local feed before
/// anything is published. A `swift run` build has neither, and then nothing
/// checks for updates at all.
public enum DictationUpdateFeed {
    public static let environmentKey = "DICTATION_UPDATE_FEED_URL"

    /// The feed to follow, or `nil` when updates are off for this copy. Only
    /// HTTPS is accepted, or plain HTTP to this Mac's loopback address for a
    /// local test: the archive is checked against the EdDSA key either way,
    /// but a feed fetched in the clear could still be held back.
    public static func url(infoPlistFeed: String?, environment: [String: String]) -> URL? {
        let candidate = environment[environmentKey].flatMap { $0.isEmpty ? nil : $0 } ?? infoPlistFeed
        guard let candidate, let url = URL(string: candidate), let host = url.host, !host.isEmpty else { return nil }
        switch url.scheme?.lowercased() {
        case "https": return url
        case "http": return ["127.0.0.1", "localhost", "::1"].contains(host) ? url : nil
        default: return nil
        }
    }

    /// The feed for the running copy, from its Info.plist and environment.
    public static var current: URL? {
        url(infoPlistFeed: Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") as? String,
            environment: ProcessInfo.processInfo.environment)
    }
}
