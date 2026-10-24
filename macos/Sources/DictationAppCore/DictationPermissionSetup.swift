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
        case .microphone: "Allow microphone access"
        case .accessibility: "Allow Earlet to type for you"
        case .inputMonitoring: "Allow your dictation shortcut"
        case .restart: "Restart to finish setup"
        case .ready: "Permissions are ready"
        }
    }

    public var detail: String {
        switch self {
        case .microphone:
            "Earlet needs your microphone to turn speech into text on this Mac. Allow access when macOS asks, or enable Earlet in Microphone settings if you denied it earlier."
        case .accessibility:
            "In System Settings → \(Self.accessibilitySettingsName), enable Earlet, then return here. This lets Earlet insert text in the app you are using."
        case .inputMonitoring:
            "In System Settings → Input Monitoring, enable Earlet, then return here. This lets your shortcut work in other apps. If macOS offers Quit & Reopen, choose it."
        case .restart:
            "macOS has allowed access, but the shortcut listener could not start. Restart this copy of Earlet to apply the permissions."
        case .ready:
            "All three permissions are available to this copy of Earlet. Enable dictation, then try a sentence below."
        }
    }

    public var actionTitle: String {
        switch self {
        case .microphone: "Allow Microphone…"
        case .accessibility: "Open System Settings…"
        case .inputMonitoring: "Open Input Monitoring Settings…"
        case .restart: "Restart Earlet"
        case .ready: "Enable dictation"
        }
    }

    public var checkMessage: String {
        switch self {
        case .ready:
            "All permissions are available. Try dictation to check the microphone and shortcut."
        case .restart:
            "Access is allowed. Restart Earlet to reconnect the shortcut."
        default:
            "This copy still cannot use all permissions. If the switches are already on, restart Earlet. If that does not help, follow the recovery steps below."
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
