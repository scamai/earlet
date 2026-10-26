// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "Dictation",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "Dictation", targets: ["DictationApp"])],
    dependencies: [
        // In-place updates from the signed feed on GitHub Releases
        // (docs/design-macos.md#updates). Only the executable links it, so
        // `swift test` and the Linux learning build never load the framework.
        .package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.9.6"),
    ],
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
                          dependencies: ["DictationAppCore", "DictationHistory", "DictationLearning", "DictationKit",
                                         .product(name: "Sparkle", package: "Sparkle")]),
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
