import Foundation
import DictationKit

/// One attempt's lifecycle, without transcript, audio, application or field
/// content. Endings are an allow-list; error descriptions never enter it.
@MainActor
final class DictationAttemptDiagnostics {
    enum Ending: String {
        case cancelled, shortcut
        case privacyRefused = "privacy_refused"
        case engineUnavailable = "engine_unavailable"
        case timedOut = "timed_out"
        case microphoneChanged = "microphone_changed"
    }

    private let id = UUID()
    private let log: @Sendable (String) -> Void
    private let clock: () -> TimeInterval
    private let started: TimeInterval
    private var released: TimeInterval?
    private var finished = false

    init(log: @escaping @Sendable (String) -> Void,
         clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.log = log
        self.clock = clock
        started = clock()
        log("dictation attempt=\(id) event=started")
    }

    func markReleased() {
        guard released == nil, !finished else { return }
        released = clock()
        log("dictation attempt=\(id) event=released")
    }

    func finish(_ ending: Ending) { finish(fields: "result=\(ending.rawValue)") }

    func finish(_ result: DictationSession.Outcome) {
        let fields: String
        let timings: DictationSession.StageTimings
        switch result {
        case .success(_, let restored, _, let measured):
            timings = measured
            fields = "result=inserted clipboard_unchanged=\(restored) restore_detached=\(measured.pasteTiming?.restoreDetached == true)"
        case .failed(let stage, let failure, let measured):
            timings = measured
            let reason: String
            switch failure {
            case .micPermissionDenied: reason = "microphone_permission"
            case .emptyCapture: reason = "empty_capture"
            case .emptyTranscript: reason = "no_text"
            case .asr: reason = "recognition_failed"
            case .invalidSequencing: reason = "invalid_sequence"
            case .insert(let insert):
                switch insert {
                case .secureFieldFocused: reason = "secure_field"
                case .secureInputActive: reason = "secure_input"
                case .accessibilityPermissionMissing: reason = "accessibility_permission"
                case .focusedElementNotEditable: reason = "field_not_editable"
                case .noFocusedElement: reason = "no_field"
                case .pasteNeverRequested: reason = "paste_not_requested"
                case .keyEventPostFailed: reason = "key_event_failed"
                }
            }
            fields = "result=failed stage=\(stage) reason=\(reason)"
        case .commandHandled(_, _, _, let measured), .spokenCommand(_, let measured):
            timings = measured
            fields = "result=command_completed"
        }
        let shapes = Self.shapeFields(timings.recognizedTextShape, prefix: "asr")
            + Self.shapeFields(timings.insertionTextShape, prefix: "candidate")
            + " format_changed=\(timings.formattingChanged.map { String($0) } ?? "n/a")"
        finish(fields: fields + " asr_ms=\(Self.ms(timings.asrMs)) paste_ms=\(Self.ms(timings.pasteTiming?.totalMs))" + shapes)
    }

    func recovery(clipboard: Bool, retained: Bool) {
        log("dictation attempt=\(id) event=recovery clipboard=\(clipboard) available_in_app=\(retained)")
    }

    private func finish(fields: String) {
        guard !finished else { return }
        finished = true
        let now = clock()
        let releaseMs = released.map { (now - $0) * 1_000 }
        log("dictation attempt=\(id) event=finished \(fields) elapsed_ms=\(Self.ms((now - started) * 1_000)) release_ms=\(Self.ms(releaseMs))")
    }

    private static func ms(_ value: Double?) -> String {
        guard let value, value.isFinite else { return "n/a" }
        return String(format: "%.0f", max(0, value))
    }

    private static func shapeFields(_ shape: DictationTextShape?, prefix: String) -> String {
        " \(prefix)_utf16=\(shape.map { String($0.utf16Count) } ?? "n/a")"
            + " \(prefix)_repeat=\(shape.map { String($0.longestRun) } ?? "n/a")"
            + " \(prefix)_compact_repeat=\(shape.map { String($0.longestCompactRun) } ?? "n/a")"
    }
}
