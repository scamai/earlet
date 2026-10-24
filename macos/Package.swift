// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "Dictation",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "Dictation", targets: ["DictationApp"])],
    targets: [
        .target(name: "DictationIPC"),
        .target(name: "DictationAudioCapture"),
        .target(name: "DictationKit", dependencies: ["DictationIPC", "DictationAudioCapture"]),
        // The learning policy and learning.json (LEARNING.md). Foundation only,
        // so it builds and tests on Linux as well: tools/test-learning-on-linux.sh.
        .target(name: "DictationLearning"),
        .target(name: "DictationKeyCheck"),
        // The dictation history: sealed files under a key in the login
        // keychain (CryptoKit, Security). No dependency on the rest.
        .target(name: "DictationHistory"),
        // The app's behaviour, as a library so `swift test` reaches it. The
        // executable target keeps only the SwiftUI scenes.
        .target(name: "DictationAppCore",
                dependencies: ["DictationHistory", "DictationKeyCheck", "DictationLearning", "DictationKit", "DictationIPC",
                               "DictationAudioCapture"]),
        .executableTarget(name: "DictationApp",
                          dependencies: ["DictationAppCore", "DictationHistory", "DictationLearning", "DictationKit"]),
        .testTarget(name: "DictationKitTests", dependencies: ["DictationKit", "DictationIPC"]),
        .testTarget(name: "DictationIPCTests", dependencies: ["DictationIPC"]),
        .testTarget(name: "DictationAudioCaptureTests", dependencies: ["DictationAudioCapture"]),
        .testTarget(name: "DictationLearningTests", dependencies: ["DictationLearning"]),
        .testTarget(name: "DictationKeyCheckTests", dependencies: ["DictationKeyCheck"]),
        .testTarget(name: "DictationHistoryTests", dependencies: ["DictationHistory"]),
        .testTarget(name: "DictationAppCoreTests",
                    dependencies: ["DictationAppCore", "DictationHistory", "DictationLearning", "DictationKit", "DictationIPC"]),
    ]
)
