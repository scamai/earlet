import Foundation

/// Optional stderr trace for microphone diagnostics.
/// Enable with `DICTATION_AUDIO_TRACE=1` when launching from a terminal.
public enum AudioTrace {
    public static let isEnabled =
        ProcessInfo.processInfo.environment["DICTATION_AUDIO_TRACE"] == "1"

    public static func log(_ tag: String, _ message: String) {
        guard isEnabled else { return }
        FileHandle.standardError.write(Data("[\(tag)] \(message)\n".utf8))
    }
}
