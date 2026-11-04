#if canImport(Testing)
import Darwin
import Foundation
import Testing
@testable import DictationAppCore

/// A loopback HTTP server that answers each request as github.com answers an
/// update check, with a cookie kept for a year, and records the Cookie header
/// each request arrived with ("" for none). One connection per request.
private final class CookieSettingServer: @unchecked Sendable {
    struct Unavailable: Error {}

    let port: UInt16
    private let listener: Int32
    private let lock = NSLock()
    private var received: [String] = []
    private static let headEnd = Data("\r\n\r\n".utf8)

    init() throws {
        let listener = socket(AF_INET, SOCK_STREAM, 0)
        guard listener >= 0 else { throw Unavailable() }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        address.sin_port = 0
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let listening = withUnsafeMutablePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { socketAddress -> Bool in
                guard bind(listener, socketAddress, length) == 0, listen(listener, 8) == 0 else { return false }
                return getsockname(listener, socketAddress, &length) == 0
            }
        }
        guard listening else {
            close(listener)
            throw Unavailable()
        }
        self.listener = listener
        port = UInt16(bigEndian: address.sin_port)
    }

    deinit {
        close(listener)
    }

    /// The Cookie header of each request answered so far, in order.
    var cookieHeaders: [String] {
        lock.withLock { received }
    }

    /// Answers `count` requests on a thread of its own, then stops.
    func serve(_ count: Int) {
        Thread.detachNewThread { [self] in
            for _ in 0..<count {
                let client = accept(listener, nil, nil)
                guard client >= 0 else { return }
                record(Self.head(of: client))
                let reply = Array(("HTTP/1.1 200 OK\r\nContent-Type: application/rss+xml\r\n"
                    + "Content-Length: 7\r\nCache-Control: no-store\r\n"
                    + "Set-Cookie: _octo=GH1.1.1.1; Max-Age=31536000; Path=/\r\n"
                    + "Connection: close\r\n\r\n<rss/>\n").utf8)
                _ = reply.withUnsafeBytes { write(client, $0.baseAddress, $0.count) }
                close(client)
            }
        }
    }

    private static func head(of client: Int32) -> String {
        var head = Data()
        var chunk = [UInt8](repeating: 0, count: 1024)
        while head.range(of: headEnd) == nil {
            let count = chunk.withUnsafeMutableBytes { read(client, $0.baseAddress, $0.count) }
            guard count > 0 else { break }
            head.append(contentsOf: chunk[0..<count])
        }
        return String(decoding: head, as: UTF8.self)
    }

    private func record(_ head: String) {
        let line = head.components(separatedBy: "\r\n").first { $0.lowercased().hasPrefix("cookie:") }
        let cookie = line.map { String($0.dropFirst("cookie:".count)).trimmingCharacters(in: .whitespaces) } ?? ""
        lock.withLock { received.append(cookie) }
    }
}

// Both tests use the process's shared cookie store, which is the one Sparkle
// uses in the app, so they do not run side by side.
@Suite("An update check carries no cookie from an earlier one", .serialized)
struct DictationUpdateCookiesTests {
    @Test("a cookie the feed sets is forgotten before the next check", .timeLimit(.minutes(1)))
    func nextCheck() async throws {
        let server = try CookieSettingServer()
        server.serve(3)
        let feed = try #require(URL(string: "http://127.0.0.1:\(server.port)/appcast.xml"))
        // As Sparkle 2.9.6 asks for the feed: a session on the default
        // configuration, whose cookies are the shared store's (SPUDownloader.m),
        // and a request that skips any cached copy (SPUDownloadDriver.m).
        let session = URLSession(configuration: .default)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: feed)
        request.cachePolicy = .reloadIgnoringLocalCacheData
        DictationUpdateCookies.forget()
        defer { DictationUpdateCookies.forget() }

        _ = try await session.data(for: request)
        _ = try await session.data(for: request)
        DictationUpdateCookies.forget()
        _ = try await session.data(for: request)

        let sent = server.cookieHeaders
        try #require(sent.count == 3)
        #expect(sent[0] == "")
        #expect(sent[1].contains("_octo="),
                "left alone, the default session sends the cookie back: the link that forgetting breaks")
        #expect(sent[2] == "")
    }

    @Test("forgetting empties the store and counts what it held")
    func forget() throws {
        let storage = HTTPCookieStorage.shared
        DictationUpdateCookies.forget(in: storage)
        defer { DictationUpdateCookies.forget(in: storage) }
        let cookies: [[HTTPCookiePropertyKey: Any]] = [
            [.domain: ".github.com", .path: "/", .name: "_octo", .value: "GH1.1.1.1",
             .expires: Date(timeIntervalSinceNow: 365 * 86_400)],
            [.domain: "github.com", .path: "/", .name: "_gh_sess", .value: "session"],
        ]
        for properties in cookies {
            storage.setCookie(try #require(HTTPCookie(properties: properties)))
        }
        #expect((storage.cookies ?? []).count == 2)
        #expect(DictationUpdateCookies.forget(in: storage) == 2)
        #expect((storage.cookies ?? []).isEmpty)
    }
}
#endif
