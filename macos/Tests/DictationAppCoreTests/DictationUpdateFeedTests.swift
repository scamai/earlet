#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@Suite("Which update feed a copy follows")
struct DictationUpdateFeedTests {
    private let release = "https://github.com/scamai/earlet/releases/latest/download/appcast.xml"

    @Test("a packaged copy follows the feed in its Info.plist")
    func packaged() {
        #expect(DictationUpdateFeed.url(infoPlistFeed: release, environment: [:])?.absoluteString == release)
    }

    @Test("a copy without a feed never checks")
    func development() {
        #expect(DictationUpdateFeed.url(infoPlistFeed: nil, environment: [:]) == nil)
        #expect(DictationUpdateFeed.url(infoPlistFeed: "", environment: [:]) == nil)
    }

    @Test("the environment replaces the feed for one launch; an empty value does not")
    func override() {
        let local = "http://127.0.0.1:8765/appcast.xml"
        #expect(DictationUpdateFeed.url(infoPlistFeed: release, environment: [DictationUpdateFeed.environmentKey: local])?
            .absoluteString == local)
        #expect(DictationUpdateFeed.url(infoPlistFeed: release, environment: [DictationUpdateFeed.environmentKey: ""])?
            .absoluteString == release)
    }

    @Test("plain HTTP is refused except to this Mac", arguments: [
        "http://example.com/appcast.xml", "http://192.168.1.2/appcast.xml", "ftp://example.com/appcast.xml",
        "file:///tmp/appcast.xml", "appcast.xml", "https:///appcast.xml",
    ])
    func refused(_ feed: String) {
        #expect(DictationUpdateFeed.url(infoPlistFeed: feed, environment: [:]) == nil)
        #expect(DictationUpdateFeed.url(infoPlistFeed: release, environment: [DictationUpdateFeed.environmentKey: feed]) == nil)
    }

    @Test("the loopback may serve a local test feed over HTTP",
          arguments: ["http://127.0.0.1:8765/appcast.xml", "http://localhost:8765/appcast.xml"])
    func loopback(_ feed: String) {
        #expect(DictationUpdateFeed.url(infoPlistFeed: nil, environment: [DictationUpdateFeed.environmentKey: feed]) != nil)
    }
}
#endif
