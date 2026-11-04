import Foundation

/// Keeps a cookie from tying one update check to the next
/// (docs/design-macos.md#updates). The address and user agent of each check
/// still reach GitHub.
///
/// Sparkle 2.9.6 fetches the feed and the update in this process with an
/// `NSURLSession` on the default configuration (SPUDownloader.m), and that
/// configuration keeps cookies in the app's shared `HTTPCookieStorage`, on
/// disk, and sends them back. github.com answers the feed request with
/// cookies, one of them kept for a year, so without this every daily check
/// would carry the same identifier. Earlet makes no other web request, so
/// whatever the store holds came from update traffic: it is emptied at
/// launch, for what an older copy kept, and before and after each check.
public enum DictationUpdateCookies {
    /// Deletes every cookie in `storage` and returns how many it held.
    @discardableResult
    public static func forget(in storage: HTTPCookieStorage = .shared) -> Int {
        let cookies = storage.cookies ?? []
        for cookie in cookies {
            storage.deleteCookie(cookie)
        }
        return cookies.count
    }
}
