import Foundation

/// The next useful action, shared by the window and the menu. A system
/// toggle is not proof of a working listener: a refused tap needs recovery.
public enum DictationPermissionStep: Equatable, Sendable {
    case microphone, accessibility, inputMonitoring, restart, ready

    public static var accessibilitySettingsName: String {
        accessibilitySettingsName(majorVersion: ProcessInfo.processInfo.operatingSystemVersion.majorVersion)
    }

    public static func accessibilitySettingsName(majorVersion: Int) -> String {
        majorVersion >= 27 ? "Device Control and Data Access" : "Accessibility"
    }

    public static func resolve(microphone: Bool, accessibility: Bool,
                               inputMonitoring: Bool, restart: Bool) -> Self {
        if !microphone { return .microphone }
        if !accessibility { return .accessibility }
        if !inputMonitoring { return .inputMonitoring }
        return restart ? .restart : .ready
    }

    public var title: String {
        switch self {
        case .microphone: "Allow the microphone"
        case .accessibility: "Let Earlet type for you"
        case .inputMonitoring: "Let your dictation key work everywhere"
        case .restart: "Restart Earlet to finish setup"
        case .ready: "Permissions are ready"
        }
    }

    /// What the app is and what setup asks for, before the first grant:
    /// someone who has just opened it should not meet a permission request
    /// with no idea what it is for.
    public static let intro =
        "Earlet types what you say into any app: hold a key, speak, and let go. Your speech is turned into text on this Mac, with no account. macOS asks for three permissions first, one at a time."

    /// The step's place in the checklist, for the setup steps.
    public var number: Int? {
        switch self {
        case .microphone: 1
        case .accessibility: 2
        case .inputMonitoring: 3
        case .restart, .ready: nil
        }
    }

    public var detail: String {
        switch self {
        case .microphone:
            "Earlet hears your speech through the microphone and turns it into text on this Mac. Click Allow Microphone…, then choose Allow when macOS asks."
        case .accessibility:
            "In System Settings → Privacy & Security → \(Self.accessibilitySettingsName), turn on Earlet; Earlet comes back here when you do. With learning on, Earlet also rereads the words it just typed for up to 90 seconds to learn from your corrections, and keeps words, not sentences."
        case .inputMonitoring:
            "Between dictations Earlet sees only keys like Shift, Option and Command, and keeps nothing you type. In System Settings → Privacy & Security → Input Monitoring, turn on Earlet; if macOS offers Quit & Reopen, choose it."
        case .restart:
            "macOS has given Earlet access, but Earlet can use it only after a restart. Your settings stay as they are."
        case .ready:
            "All three permissions are in place. Turn Earlet on, then try a sentence below."
        }
    }

    public var actionTitle: String {
        switch self {
        case .microphone: "Allow Microphone…"
        case .accessibility: "Open System Settings…"
        case .inputMonitoring: "Open Input Monitoring…"
        case .restart: "Restart Earlet"
        case .ready: "Turn On"
        }
    }

    public var checkMessage: String {
        switch self {
        case .ready:
            "All three permissions are in place."
        case .restart:
            "Access is in place. Restart Earlet to use it."
        default:
            "Earlet still can't use every permission. If its switch is already on in System Settings, restart Earlet. If that doesn't help, open the steps below."
        }
    }

    /// What VoiceOver hears when setup moves on, since nothing else on the
    /// page announces it.
    public var arrivalAnnouncement: String {
        switch self {
        case .microphone, .accessibility, .inputMonitoring: "Next: \(title)."
        case .restart: "All three permissions are allowed. Restart Earlet to finish setup."
        case .ready: "Permissions are ready."
        }
    }
}

/// Identifies the actual running bundle, not whichever copy Launch Services
/// happens to resolve by name. No disk scan or guessed duplicate detection.
public struct DictationInstallation: Equatable, Sendable {
    public let path: String
    public let version: String
    public let build: String?

    public init(path: String, version: String, build: String? = nil) {
        self.path = path
        self.version = version
        self.build = build
    }

    public var versionLabel: String {
        if let build, !build.isEmpty { return "Version \(version) · Build \(build)" }
        return "Version \(version) · Build not recorded"
    }

    public var isInApplications: Bool {
        let parent = URL(fileURLWithPath: path).deletingLastPathComponent().standardizedFileURL.path
        return parent == "/Applications"
            || parent == FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications").path
    }

    public static var current: Self {
        let bundle = Bundle.main
        return Self(path: bundle.bundleURL.path,
                    version: bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "Development",
                    build: bundle.object(forInfoDictionaryKey: "DictationBuild") as? String)
    }
}
