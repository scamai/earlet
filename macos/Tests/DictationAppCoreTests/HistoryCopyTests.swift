// What the History pane groups, finds and says: the days and their titles,
// the search, speaking time and the questions before deleting.

#if canImport(Testing)
import Foundation
import Testing
import DictationHistory
@testable import DictationAppCore

@Suite("History pane copy")
struct HistoryCopyTests {
    /// A fixed calendar and clock, so "Today" means the same thing on every host.
    static let calendar: Calendar = {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Los_Angeles")!
        calendar.locale = Locale(identifier: "en_US")
        return calendar
    }()
    // Friday, 16 October 2026, 10:00 in Los Angeles.
    static let now = calendar.date(from: DateComponents(year: 2026, month: 10, day: 16, hour: 10))!

    private func entry(_ text: String, hoursAgo: Double) -> DictationHistoryEntry {
        DictationHistoryEntry(recordedAt: Self.now.addingTimeInterval(-hoursAgo * 3600), text: text,
                              audioSeconds: 3, language: nil, appBundleID: nil, delivery: .inserted)
    }

    @Test("days are titled Today, Yesterday, the date, and the year when it is not this one")
    func dayTitles() {
        let calendar = Self.calendar, now = Self.now
        #expect(DictationHistoryCopy.dayTitle(now, now: now, calendar: calendar) == "Today")
        #expect(DictationHistoryCopy.dayTitle(now.addingTimeInterval(-11 * 3600), now: now, calendar: calendar) == "Yesterday")
        #expect(DictationHistoryCopy.dayTitle(now.addingTimeInterval(-3 * 86400), now: now, calendar: calendar)
                == "Tuesday, October 13")
        #expect(DictationHistoryCopy.dayTitle(now.addingTimeInterval(-300 * 86400), now: now, calendar: calendar)
                == "Saturday, December 20, 2025")
    }

    @Test("entries group by calendar day, newest first, keeping their order")
    func grouping() {
        let entries = [entry("a", hoursAgo: 1), entry("b", hoursAgo: 9), entry("c", hoursAgo: 11),
                       entry("d", hoursAgo: 40)]
        let days = DictationHistoryCopy.days(entries, now: Self.now, calendar: Self.calendar)
        #expect(days.map(\.title) == ["Today", "Yesterday", "Wednesday, October 14"])
        #expect(days.map { $0.entries.map(\.text) } == [["a", "b"], ["c"], ["d"]])
    }

    @Test("search ignores case and accents, and an empty query keeps everything")
    func search() {
        let entries = [entry("Order a café latte", hoursAgo: 1), entry("Send the slides", hoursAgo: 2),
                       entry("明天下午一起看看第一版", hoursAgo: 3)]
        let found = { (query: String) in
            DictationHistoryCopy.days(entries, matching: query, now: Self.now, calendar: Self.calendar)
                .flatMap(\.entries).map(\.text)
        }
        #expect(found("CAFE") == ["Order a café latte"])
        #expect(found("第一版") == ["明天下午一起看看第一版"])
        #expect(found("  ").count == 3)
        #expect(found("nothing like this").isEmpty)
    }

    @Test("speaking time reads in seconds, minutes, then hours and minutes", arguments: [
        (0.4, "0 s"), (42, "42 s"), (59.6, "1 min"), (540, "9 min"), (3600, "1 h"), (4800, "1 h 20 min"),
    ])
    func speakingTime(seconds: Double, expected: String) {
        #expect(DictationHistoryCopy.speakingTime(seconds) == expected)
    }

    @Test("the questions before a shorter keep count what they delete, in the singular when it is one")
    func keepQuestions() {
        #expect(DictationHistoryCopy.keepQuestion(dictations: 3, recordings: 0) == "Delete 3 older dictations?")
        #expect(DictationHistoryCopy.keepQuestion(dictations: 1, recordings: 0) == "Delete 1 older dictation?")
        #expect(DictationHistoryCopy.keepQuestion(dictations: 0, recordings: 2) == "Delete 2 recordings?")
        #expect(DictationHistoryCopy.keepQuestion(dictations: 0, recordings: 1) == "Delete 1 recording?")
        #expect(DictationHistoryCopy.unreadable(1) == "1 saved dictation can't be opened with this Mac's history key.")
    }

    @Test("a typed-in dictation needs no delivery note; the others say what happened")
    func deliveries() {
        #expect(DictationHistoryCopy.delivery(.inserted) == nil)
        #expect(DictationHistoryCopy.delivery(.clipboard)?.contains("clipboard") == true)
        #expect(DictationHistoryCopy.delivery(.notInserted) != nil)
        #expect(DictationHistoryCopy.delivery(.recovered)?.contains("Recovered") == true)
    }
}
#endif
