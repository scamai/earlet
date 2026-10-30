import DictationAppCore
import SwiftUI

/// Useful on first setup, one disclosure row in daily use. Keep a practice
/// field open while it owns focus so an insertion never hides its own result.
struct DictationPracticeSection: View {
    @ObservedObject var model: DictationModel
    @State private var expanded: Bool
    @State private var text = ""
    @FocusState private var focused: Bool

    init(model: DictationModel) {
        self.model = model
        _expanded = State(initialValue: !model.hasCompletedFirstDictation)
    }

    var body: some View {
        DisclosureGroup("Try dictation", isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 8) {
                Text("Click in the field, hold your dictation key and say a sentence, then let go. Your words appear here.")
                    .font(DictationType.callout)
                    .foregroundStyle(.brandInk2)
                    .fixedSize(horizontal: false, vertical: true)
                TextField("Your practice sentence", text: $text, axis: .vertical)
                    .labelsHidden()
                    .lineLimit(3...5)
                    .dictationField(focused: focused)
                    .accessibilityLabel("Earlet practice text")
                    .focused($focused)
                Text("This practice text is not saved.")
                    .font(DictationType.caption)
                    .foregroundStyle(.brandInk2)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .disclosureGroupStyle(.dictation)
        .onChange(of: model.hasCompletedFirstDictation) { _, completed in
            if completed && !focused { expanded = false }
        }
    }
}

/// Daily-use actions before the preferences, as a card. Text stays
/// collapsed until the user asks to see it, including when a failed paste
/// needs recovery.
struct DictationRecentResultSection: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var result: DictationRecentResult

    var body: some View {
        // Only while it needs the user: after an ordinary dictation the text
        // is where it was typed, and Copy last text stays in the menu bar.
        if let entry = result.entry, entry.needsRecovery || model.savedTakeAwaitsCopy {
            DictationCard {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    if entry.needsRecovery {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .font(.system(size: 13, weight: .semibold))
                            .foregroundStyle(.brandCaution)
                            .accessibilityHidden(true)
                    }
                    Text(entry.needsRecovery ? "Your words are still here" : "Last available text")
                        .font(DictationType.sectionTitle)
                        .foregroundStyle(.brandInk)
                        .accessibilityAddTraits(.isHeader)
                    Spacer(minLength: 8)
                    Text(entry.date, style: .time)
                        .font(DictationType.time)
                        .foregroundStyle(.brandInk2)
                }
                Text(entry.needsRecovery ? "Couldn’t type into the field." : "Ready to copy again.")
                    .font(DictationType.body)
                    .foregroundStyle(.brandInk2)
                HStack(spacing: 8) {
                    Button(result.copying ? "Copying…" : "Copy last text") {
                        Task { await model.copyRecentResult() }
                    }
                    .buttonStyle(.dictation(entry.needsRecovery ? .primary : .secondary))
                    .disabled(model.busy || model.recording || result.copying)
                    Button("Discard") { Task { await model.dismissRecentResult() } }
                        .buttonStyle(.dictation(.quiet))
                        .disabled(result.copying)
                        .accessibilityHint("Removes the last text from Earlet")
                }
                if let message = result.copyMessage {
                    DictationFootnote(message)
                }
                DisclosureGroup("Show text") {
                    ScrollView {
                        Text(entry.text)
                            .font(.system(size: 14))
                            .foregroundStyle(.brandInk)
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .frame(maxHeight: 160)
                }
                .disclosureGroupStyle(.dictation)
                .id(entry.id)
                Text(DictationCopy.recentResultFootnote(inHistory: entry.inHistory))
                    .font(DictationType.caption)
                    .foregroundStyle(.brandInk2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// The saved recording's card, while a take is kept for Recover Last
/// Dictation: what it is and how long it is, Recover and Discard, and how
/// long it is kept. The recording holds the user's voice, so the card shows
/// its time and length, never the words.
struct DictationSavedTakeSection: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var result: DictationRecentResult

    var body: some View {
        if model.pendingRecordingCleanup {
            DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution,
                            text: "A recording file could not be removed. Earlet will retry automatically.") {
                Button("Retry cleanup") { Task { await model.retryRecordingCleanup() } }
                    .buttonStyle(.dictation(.secondary))
            }
        }
        if let take = model.savedTake {
            DictationCard {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Image(systemName: "waveform")
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(.brandWheatStrong)
                        .accessibilityHidden(true)
                    Text("Saved recording")
                        .font(DictationType.sectionTitle)
                        .foregroundStyle(.brandInk)
                        .accessibilityAddTraits(.isHeader)
                    Spacer(minLength: 8)
                    Text(take.recordedAt, style: .time)
                        .font(DictationType.time)
                        .foregroundStyle(.brandInk2)
                }
                Text(take.interrupted
                     ? "Earlet closed before it was transcribed."
                     : "Couldn't transcribe it.")
                    .font(DictationType.body)
                    .foregroundStyle(.brandInk)
                DictationFootnote(detail(for: take))
                HStack(spacing: 8) {
                    Button(model.recoveringSavedTake ? "Recovering…" : "Recover Last Dictation") {
                        Task { await model.recoverLastDictation() }
                    }
                    .buttonStyle(.dictation(.primary))
                    .disabled(!model.canRecoverSavedTake)
                    Button("Discard") { Task { await model.discardSavedTake() } }
                        .buttonStyle(.dictation(.quiet))
                        .disabled(model.recoveringSavedTake)
                        .accessibilityLabel("Discard the saved recording")
                }
                Text("Kept on this Mac until you recover or discard it, for 7 days at most.")
                    .font(DictationType.caption)
                    .foregroundStyle(.brandInk2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func detail(for take: DictationKeptTake) -> String {
        var line = "\(DictationCopy.takeLength(take.length)) long."
        if take.replacedEarlier {
            line += " It replaced an earlier saved recording."
        }
        if model.savedTakeAwaitsCopy {
            line += " Its words are in Last available text; it stays until they are copied."
        }
        return line
    }
}

/// The menu bar menu's Recover Last Dictation, while a take is saved: the
/// time, never the words. Sits with Copy last text.
struct DictationSavedTakeMenu: View {
    @ObservedObject var model: DictationModel

    var body: some View {
        if let take = model.savedTake {
            // The recent result's group draws its own separator when it shows.
            if model.recentResult.entry == nil { Divider() }
            Button(model.recoveringSavedTake
                   ? "Recovering…"
                   : "Recover Last Dictation · \(take.recordedAt.formatted(date: .omitted, time: .shortened))") {
                Task { await model.recoverLastDictation() }
            }
            .disabled(!model.canRecoverSavedTake)
        }
    }
}

struct DictationLearningNoticeSection: View {
    @ObservedObject var vocabulary: DictationVocabularyModel

    var body: some View {
        if let notice = vocabulary.learningNotice {
            DictationCard {
                Text("Learning from you")
                    .font(DictationType.sectionTitle)
                    .foregroundStyle(.brandInk)
                    .accessibilityAddTraits(.isHeader)
                DictationNotice(symbol: "text.badge.checkmark", tone: .wheatStrong, text: notice)
                HStack(spacing: 8) {
                    if vocabulary.undoToken != nil {
                        Button(vocabulary.undoing ? "Undoing…" : "Undo learning") {
                            Task { await vocabulary.undoLastLearning() }
                        }
                        .buttonStyle(.dictation(.secondary))
                        .disabled(vocabulary.undoing)
                    }
                    Button("Dismiss") { vocabulary.dismissLearningNotice() }
                        .buttonStyle(.dictation(.quiet))
                        .disabled(vocabulary.undoing)
                }
            }
        }
    }
}

/// Native menu actions, observed directly so a new result or correction
/// appears even when the main window has been closed for the whole session.
struct DictationRecentResultMenu: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var result: DictationRecentResult

    var body: some View {
        if let entry = result.entry {
            Divider()
            if entry.needsRecovery { Text("Your words are still here") }
            Button("Copy last text · \(entry.date.formatted(date: .omitted, time: .shortened))") {
                Task { await model.copyRecentResult() }
            }.disabled(model.busy || model.recording || result.copying)
        }
    }
}

struct DictationLearningMenu: View {
    @ObservedObject var vocabulary: DictationVocabularyModel
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        if vocabulary.undoToken != nil {
            Divider()
            Text("Remembered your correction")
            Button("Review vocabulary…") { DictationMainWindow.show(using: openWindow, pane: .vocabulary) }
            Button("Undo last learning") { Task { await vocabulary.undoLastLearning() } }
                .disabled(vocabulary.undoing)
        }
    }
}
