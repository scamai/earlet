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

    /// The names of this Mac's loopback address, where a local test feed runs.
    static let loopbackHosts: Set<String> = ["127.0.0.1", "localhost", "::1"]

    /// The feed to follow, or `nil` when updates are off for this copy.
    ///
    /// Info.plist's feed must be HTTPS: the archive is checked against the
    /// EdDSA key either way, but a feed fetched in the clear could be held
    /// back. The environment may point only at the loopback, over HTTP or
    /// HTTPS: every package is a release build, so the override cannot be
    /// compiled out, and kept to this Mac it cannot send the daily check to
    /// another server. Any other override turns updates off for that launch.
    public static func url(infoPlistFeed: String?, environment: [String: String]) -> URL? {
        if let override = environment[environmentKey], !override.isEmpty {
            guard let url = URL(string: override), let host = url.host, loopbackHosts.contains(host.lowercased()),
                  let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https" else { return nil }
            return url
        }
        guard let infoPlistFeed, let url = URL(string: infoPlistFeed), let host = url.host, !host.isEmpty,
              url.scheme?.lowercased() == "https" else { return nil }
        return url
    }

    /// The feed for the running copy, from its Info.plist and environment.
    public static var current: URL? {
        url(infoPlistFeed: Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") as? String,
            environment: ProcessInfo.processInfo.environment)
    }
}
