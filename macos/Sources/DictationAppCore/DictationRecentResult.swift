import Combine
import Foundation
import DictationKit

/// One recoverable result, in memory only. No audio, history, settings or log
/// writes. The next usable result replaces it; dismiss and quit discard it.
@MainActor
public final class DictationRecentResult: ObservableObject {
    public struct Entry: Identifiable, Equatable, Sendable {
        public let id = UUID()
        public let text: String
        public let date: Date
        public let needsRecovery: Bool
        /// Whether the history was handed these words too, so the section
        /// can say where else they are.
        public internal(set) var inHistory = false
    }

    @Published public private(set) var entry: Entry?
    @Published public private(set) var copyMessage: String?
    @Published public private(set) var copying = false
    private let writeClipboard: @MainActor (String) async -> Bool

    public init(writeClipboard: @escaping @MainActor (String) async -> Bool) {
        self.writeClipboard = writeClipboard
    }

    func remember(_ text: String?, needsRecovery: Bool, date: Date = Date()) {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { clear(); return }
        entry = Entry(text: text, date: date, needsRecovery: needsRecovery)
        copyMessage = nil
    }

    /// Marks the entry `id` (still the shown one) as handed to the history.
    func noteInHistory(_ saved: Bool, for id: UUID?) {
        guard let id, entry?.id == id else { return }
        entry?.inHistory = saved
    }

    /// A secure target is excluded even when recognition already produced
    /// words. No later copy action can recover a password-field result.
    func rememberRefused(_ text: String?, failure: DictationInsertFailure) {
        switch failure {
        case .secureFieldFocused, .secureInputActive: clear()
        default: remember(text, needsRecovery: true)
        }
    }

    public func clear() {
        entry = nil
        copyMessage = nil
    }

    /// `nil` means there is no applicable result (or it changed while a
    /// clipboard restore was finishing). Never label a newer entry copied.
    func copy() async -> Bool? {
        guard !copying, let current = entry else { return nil }
        copying = true
        defer { copying = false }
        let copied = await writeClipboard(current.text)
        guard entry?.id == current.id else { return nil }
        copyMessage = copied
            ? "Copied. Press ⌘V in a text field."
            : "Couldn’t copy. Your text is still here; try again."
        return copied
    }
}
