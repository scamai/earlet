import DictationHistory
import Foundation

// What the History pane and Home's counts say, and how the list is grouped
// and searched: decided here, where tests reach, and drawn by the views in
// DictationHistoryView.swift and DictationHomeView.swift.

public enum DictationHistoryCopy {
    /// One day of the list, newest first.
    public struct Day: Identifiable, Equatable, Sendable {
        public var id: Date { start }
        public var start: Date
        public var title: String
        public var entries: [DictationHistoryEntry]
    }

    /// The entries that match `query`, grouped by the calendar day they were
    /// recorded on, newest first. Case and accents are ignored, so "cafe"
    /// finds "café"; an empty query keeps them all.
    public static func days(_ entries: [DictationHistoryEntry], matching query: String = "",
                            now: Date = Date(), calendar: Calendar = .current) -> [Day] {
        let wanted = query.trimmingCharacters(in: .whitespacesAndNewlines)
        var days: [Day] = []
        for entry in entries where wanted.isEmpty || entry.text.localizedStandardContains(wanted) {
            let start = calendar.startOfDay(for: entry.recordedAt)
            if days.last?.start == start {
                days[days.count - 1].entries.append(entry)
            } else {
                days.append(Day(start: start, title: dayTitle(start, now: now, calendar: calendar), entries: [entry]))
            }
        }
        return days
    }

    /// "Today", "Yesterday", "Monday, October 12", and the year too when it
    /// is not this year's.
    public static func dayTitle(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        if calendar.isDate(date, inSameDayAs: now) { return "Today" }
        if let yesterday = calendar.date(byAdding: .day, value: -1, to: now),
           calendar.isDate(date, inSameDayAs: yesterday) { return "Yesterday" }
        var style = Date.FormatStyle(date: .omitted, time: .omitted, locale: calendar.locale ?? .current,
                                     calendar: calendar, timeZone: calendar.timeZone)
            .weekday(.wide).month(.wide).day()
        if calendar.component(.year, from: date) != calendar.component(.year, from: now) {
            style = style.year()
        }
        return date.formatted(style)
    }

    /// How a dictation's words reached the user, for its row; nil for the
    /// ordinary case, typed in.
    public static func delivery(_ delivery: DictationHistoryEntry.Delivery) -> String? {
        switch delivery {
        case .inserted: return nil
        case .clipboard: return "Couldn't type it; copied to the clipboard"
        case .notInserted: return "Couldn't type or copy it"
        case .recovered: return "Recovered from a saved recording"
        }
    }

    /// Speaking time: "42 s", "9 min", "1 h 20 min".
    public static func speakingTime(_ seconds: Double) -> String {
        let total = Int(seconds.rounded())
        if total < 60 { return "\(total) s" }
        let minutes = total / 60
        if minutes < 60 { return "\(minutes) min" }
        let rest = minutes % 60
        return rest == 0 ? "\(minutes / 60) h" : "\(minutes / 60) h \(rest) min"
    }

    public static let emptyTitle = "No dictations yet"
    public static let emptyDetail = "Each dictation you make is saved here, encrypted on this Mac."
    public static let offTitle = "History is off"
    public static let offDetail = "New dictations aren't saved. The ones already saved stay until you delete them or their time is up."
    public static let lockedTitle = "History is locked"
    public static let lockedDetail = "Earlet couldn't open its key in your login keychain, so saved dictations can't be shown and new ones aren't saved. If the keychain is locked, unlock it in Keychain Access, then click Try Again. Dictation itself still works."
    public static let storage = "Saved encrypted on this Mac, under a key kept in your login keychain. A password field, Secure Input or a spoken command is never saved."
    public static let deleteAllQuestion = "Delete all history?"
    public static let deleteAllDetail = "Every saved dictation and recording is deleted from this Mac, and the key that opens them is destroyed. This can't be undone."
    public static let deleteOneQuestion = "Delete this dictation?"
    public static let deleteOneDetail = "Its words and its recording are deleted from this Mac."
    public static let countsFootnote = "Counted from your history on this Mac. Each Chinese, Japanese or Korean character counts as one word."

    /// Dictations a newer version saved: kept, and shown by that version.
    public static func newer(_ count: Int) -> String {
        count == 1
            ? "1 dictation was saved by a newer version of Earlet. It is kept, for that version to show."
            : "\(count) dictations were saved by a newer version of Earlet. They are kept, for that version to show."
    }

    public static func unreadable(_ count: Int) -> String {
        count == 1
            ? "1 saved dictation can't be opened with this Mac's history key."
            : "\(count) saved dictations can't be opened with this Mac's history key."
    }

    /// The question before a shorter keep deletes what it would.
    public static func keepQuestion(dictations: Int, recordings: Int) -> String {
        switch (dictations, recordings) {
        case (0, let recordings): return "Delete \(recordings) \(recordings == 1 ? "recording" : "recordings")?"
        case (let dictations, _): return "Delete \(dictations) older \(dictations == 1 ? "dictation" : "dictations")?"
        }
    }
}
