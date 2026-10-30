import Foundation

/// Dictation's own preferences, in the `ai.scam.dictation` defaults domain.
public final class DictationSettings: @unchecked Sendable {
    public static let domain = "ai.scam.dictation"
    static let holdKeyKey = "dictation.holdKey"
    static let mouseButtonKey = "dictation.mouseButton"
    static let enabledKey = "dictation.enabled"
    static let languageKey = "dictation.language"
    static let learningEnabledKey = "dictation.learning.enabled"
    static let resumeEnableKey = "dictation.resumeEnableAfterRelaunch"
    static let completedFirstDictationKey = "dictation.completedFirstDictation"
    static let historyEnabledKey = "dictation.history.enabled"
    static let historyKeepKey = "dictation.history.keep"
    static let recordingKeepKey = "dictation.history.recordings"

    public var silenceSetting: DictationSilenceSetting {
        get { defaults.string(forKey: "dictation.silenceStop").flatMap(DictationSilenceSetting.init(rawValue:)) ?? .thirtySeconds }
        set { defaults.set(newValue.rawValue, forKey: "dictation.silenceStop") }
    }

    let defaults: UserDefaults

    public init(defaults: UserDefaults) {
        self.defaults = defaults
    }

    /// The `ai.scam.dictation` domain, however the app was launched.
    ///
    /// Inside the bundle that domain IS the app's standard defaults, and
    /// `UserDefaults(suiteName:)` refuses the app's own bundle identifier as
    /// a suite name. From `swift run` or a test there is no such bundle, so
    /// the domain is opened as a suite.
    public static func live(bundleIdentifier: String? = Bundle.main.bundleIdentifier) -> DictationSettings {
        DictationSettings(defaults: defaultsForDomain(bundleIdentifier: bundleIdentifier))
    }

    static func defaultsForDomain(bundleIdentifier: String?) -> UserDefaults {
        if bundleIdentifier == domain { return .standard }
        return UserDefaults(suiteName: domain) ?? .standard
    }

    /// The hold key; an unset or unreadable value is the shipped default.
    public var holdKey: DictationHoldKey {
        get {
            defaults.string(forKey: Self.holdKeyKey).flatMap(DictationHoldKey.init(rawValue:))
                ?? .standard
        }
        set { defaults.set(newValue.rawValue, forKey: Self.holdKeyKey) }
    }

    /// The mouse button that also starts a dictation, or `nil` for none. An
    /// unset or unreadable value is none: a hand-edited or downgraded
    /// setting must never take over a button the user did not choose.
    public var mouseButton: DictationMouseSideButton? {
        get {
            defaults.string(forKey: Self.mouseButtonKey).flatMap(DictationMouseSideButton.init(rawValue:))
        }
        set {
            if let newValue {
                defaults.set(newValue.rawValue, forKey: Self.mouseButtonKey)
            } else {
                defaults.removeObject(forKey: Self.mouseButtonKey)
            }
        }
    }

    /// Whether the user left dictation on. Cleared only by Pause, not by
    /// quitting, so a relaunch (which macOS often asks for after an Input
    /// Monitoring grant) comes back the way the user left it.
    public var enabled: Bool {
        get { defaults.bool(forKey: Self.enabledKey) }
        set { defaults.set(newValue, forKey: Self.enabledKey) }
    }

    /// Whether dictation has ever been on with these preferences. Only a
    /// successful enable and Pause write `enabled`, so the key being there
    /// at all says so, for installs from before this property too. Guards
    /// the first-run enable (G16): a user who paused is never turned back
    /// on.
    public var hasBeenEnabledBefore: Bool {
        defaults.object(forKey: Self.enabledKey) != nil
    }

    /// The language the user dictates in. Unset or unreadable is Automatic,
    /// which lets the recognizer detect it per dictation.
    public var language: DictationLanguage {
        get {
            defaults.string(forKey: Self.languageKey).flatMap(DictationLanguage.init(rawValue:))
                ?? .automatic
        }
        set { defaults.set(newValue.rawValue, forKey: Self.languageKey) }
    }

    /// "Learn from my corrections". On unless the user turned it off
    /// (LEARNING.md §1): an unset or unreadable value is on. Turning it off stops the
    /// observer and unlearns nothing.
    public var learningEnabled: Bool {
        get { defaults.object(forKey: Self.learningEnabledKey) as? Bool ?? true }
        set { defaults.set(newValue, forKey: Self.learningEnabledKey) }
    }

    /// Set before a quit-and-reopen that should finish an enable on the far
    /// side: macOS applies an Input Monitoring grant only to a fresh process,
    /// and the in-memory pending enable dies with this one. One-shot —
    /// `DictationModel.launch()` clears it when read, whether it can enable
    /// or not.
    public var resumeEnableAfterRelaunch: Bool {
        get { defaults.bool(forKey: Self.resumeEnableKey) }
        set { defaults.set(newValue, forKey: Self.resumeEnableKey) }
    }

    /// A setup milestone only, never text or a dictation count. Once a real
    /// insertion succeeds, the practice guide can start collapsed.
    public var hasCompletedFirstDictation: Bool {
        get { defaults.bool(forKey: Self.completedFirstDictationKey) }
        set { defaults.set(newValue, forKey: Self.completedFirstDictationKey) }
    }

    /// "Save dictations": on unless the user turned it off; an unset or
    /// unreadable value is on, because the history is sealed on this Mac and
    /// learning needs past dictations. Off saves nothing new and deletes
    /// nothing.
    public var historyEnabled: Bool {
        get { defaults.object(forKey: Self.historyEnabledKey) as? Bool ?? true }
        set { defaults.set(newValue, forKey: Self.historyEnabledKey) }
    }

    /// How long dictations are kept; unset or unreadable is Forever.
    public var historyKeep: DictationHistoryKeep {
        get { defaults.string(forKey: Self.historyKeepKey).flatMap(DictationHistoryKeep.init(rawValue:)) ?? .forever }
        set { defaults.set(newValue.rawValue, forKey: Self.historyKeepKey) }
    }

    /// How long recordings are kept; unset or unreadable is 30 days.
    public var recordingKeep: DictationRecordingKeep {
        get { defaults.string(forKey: Self.recordingKeepKey).flatMap(DictationRecordingKeep.init(rawValue:)) ?? .month }
        set { defaults.set(newValue.rawValue, forKey: Self.recordingKeepKey) }
    }
}

/// The languages the picker offers.
///
/// Automatic sends no language, so Whisper detects it from each clip. Any
/// other choice pins the decode: the code is the `language` field of the
/// worker's transcribe request (`PythonAsrTransport.setLanguageCode`).
/// Automatic is the default because a pin is only as good as the user's
/// choice: Whisper given the wrong language does not fail, it translates
/// (see the decode-language note in the worker's `asr_server.py`).
public enum DictationLanguage: String, CaseIterable, Identifiable, Sendable {
    case automatic = "auto"
    case english = "en"
    case chinese = "zh"
    case japanese = "ja"
    case korean = "ko"
    case spanish = "es"
    case french = "fr"
    case german = "de"
    case portuguese = "pt"
    case italian = "it"
    case russian = "ru"

    public var id: String { rawValue }

    /// Whisper's language code, or `nil` to detect it.
    public var code: String? { self == .automatic ? nil : rawValue }

    public var label: String {
        switch self {
        case .automatic: return "Automatic"
        case .english: return "English"
        case .chinese: return "Chinese"
        case .japanese: return "Japanese"
        case .korean: return "Korean"
        case .spanish: return "Spanish"
        case .french: return "French"
        case .german: return "German"
        case .portuguese: return "Portuguese"
        case .italian: return "Italian"
        case .russian: return "Russian"
        }
    }
}
