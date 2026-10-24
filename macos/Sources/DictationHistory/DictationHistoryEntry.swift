import Foundation

// One dictation as the history keeps it, and the counts the window shows from
// it. The sealed files are DictationHistorySeal.swift, the folder and its
// rules DictationHistoryStore.swift.

/// One dictation: its words as they were delivered, where they went, when
/// and how long it was. Sealed on disk with the history key, never written
/// in the clear; its recording, when one is kept, is a separate sealed file
/// with the same id.
public struct DictationHistoryEntry: Codable, Equatable, Identifiable, Sendable {
    /// How the words reached the user.
    public enum Delivery: String, Codable, Sendable {
        /// Typed into the field the user was in.
        case inserted
        /// The paste was refused; the words went onto the clipboard.
        case clipboard
        /// The paste and the clipboard both failed. The words waited in Copy
        /// last text, when it could hold them; the history is the copy that
        /// outlives it.
        case notInserted
        /// Transcribed again from a saved recording (Recover Last
        /// Dictation) and handed to Copy last text.
        case recovered
    }

    public let id: UUID
    /// When the hold began, on the wall clock.
    public var recordedAt: Date
    /// The words, formatted as they were delivered.
    public var text: String
    /// The recording's length; the hold's length when the recording failed.
    public var audioSeconds: Double
    /// The language the recognizer reported for this dictation ("en",
    /// "zh"), or nil when it reported none. Never a guess.
    public var language: String?
    /// The bundle identifier of the app the words went to, when known.
    public var appBundleID: String?
    public var delivery: Delivery

    public init(id: UUID = UUID(), recordedAt: Date, text: String, audioSeconds: Double,
                language: String?, appBundleID: String?, delivery: Delivery) {
        self.id = id
        self.recordedAt = recordedAt
        self.text = text
        self.audioSeconds = audioSeconds
        self.language = language
        self.appBundleID = appBundleID
        self.delivery = delivery
    }

    /// Words, counted the way the window counts them (`DictationWordCount`).
    public var words: Int { DictationWordCount.count(text) }
}

/// How many words a dictation holds. A run of letters or digits is one word
/// (an apostrophe or a point inside it does not split it: "don't", "3.6");
/// each Han, Hiragana, Katakana or Hangul character is one, because those
/// scripts do not put spaces between words. Punctuation counts for nothing.
public enum DictationWordCount {
    public static func count(_ text: String) -> Int {
        var words = 0
        var inWord = false
        var pendingJoiner = false
        for scalar in text.unicodeScalars {
            if isCharacterWord(scalar) {
                words += 1
                inWord = false
                pendingJoiner = false
            } else if isWordScalar(scalar) {
                if !inWord { words += 1 }
                inWord = true
                pendingJoiner = false
            } else if inWord, !pendingJoiner, isJoiner(scalar) {
                // "don't": the apostrophe keeps the word open for one scalar.
                pendingJoiner = true
            } else {
                inWord = false
                pendingJoiner = false
            }
        }
        return words
    }

    /// Letters, digits and the marks that ride on them.
    static func isWordScalar(_ scalar: Unicode.Scalar) -> Bool {
        let properties = scalar.properties
        return properties.isAlphabetic || properties.numericType != nil
            || properties.generalCategory == .nonspacingMark || properties.generalCategory == .spacingMark
    }

    static func isJoiner(_ scalar: Unicode.Scalar) -> Bool {
        scalar == "'" || scalar == "\u{2019}" || scalar == "."
    }

    /// One character, one word: CJK ideographs (the unified blocks, their
    /// extensions and the compatibility block), Hiragana, Katakana and
    /// Hangul.
    static func isCharacterWord(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3040...0x309F, 0x30A0...0x30FF, 0x31F0...0x31FF,    // Hiragana, Katakana
             0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF,    // CJK ideographs
             0x20000...0x323AF,                                    // CJK extensions B–H
             0x1100...0x11FF, 0x3130...0x318F, 0xAC00...0xD7AF:    // Hangul
            return true
        default:
            return false
        }
    }
}

/// What a set of dictations adds up to: how many, how many words, how long
/// the recordings were, and the pace those give. Measured from the history
/// alone; nothing here is estimated from an assumed typing speed.
public struct DictationHistoryStats: Equatable, Sendable {
    public var dictations: Int
    public var words: Int
    public var seconds: Double

    public init(dictations: Int = 0, words: Int = 0, seconds: Double = 0) {
        self.dictations = dictations
        self.words = words
        self.seconds = seconds
    }

    public init(_ entries: some Sequence<DictationHistoryEntry>) {
        self.init()
        for entry in entries {
            dictations += 1
            words += entry.words
            seconds += max(0, entry.audioSeconds)
        }
    }

    /// Words per minute of recording, rounded; nil below ten seconds of
    /// speech, where one short dictation would read as a rate.
    public var wordsPerMinute: Int? {
        guard seconds >= 10 else { return nil }
        return Int((Double(words) / (seconds / 60)).rounded())
    }
}
