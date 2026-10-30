import DictationAppCore
import SwiftUI

// The main window's Vocabulary pane. The window's shell is in
// DictationWindowView.swift and the components in DictationDesign.swift.

/// The learned and taught words, on their own pane: the list can grow to
/// hundreds of rows, and in the single column it pushed General and
/// Troubleshooting out of sight. When the vocabulary file could not be
/// opened at launch the pane says so instead of showing an empty list.
struct DictationVocabularyPane: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var vocabulary: DictationVocabularyModel

    var body: some View {
        if vocabulary.isAvailable {
            DictationPage {
                DictationPageHeader(title: DictationVocabularyCopy.heading, subtitle: count)
                DictationVocabularySection(model: model, vocabulary: vocabulary)
            }
        } else {
            DictationEmptyState(
                symbol: "character.book.closed", title: "Vocabulary is unavailable",
                detail: "Earlet couldn't open its vocabulary file at launch, so it dictates without learned or added words. The log under Settings → Troubleshooting says why.")
        }
    }

    /// "3 words · 2 misheard forms", or nothing while the list is empty (the
    /// list says so). A misheard form is a rewrite, not a word, so it is not
    /// counted as one.
    private var count: String? {
        var words = 0, forms = 0
        for row in vocabulary.rows {
            if case .alias = row.entry { forms += 1 } else { words += 1 }
        }
        var parts: [String] = []
        if words > 0 { parts.append("\(words.formatted()) \(words == 1 ? "word" : "words")") }
        if forms > 0 { parts.append("\(forms.formatted()) misheard \(forms == 1 ? "form" : "forms")") }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

/// The learned and taught words: the switch, the teach row, the list and
/// Forget all. Copy from `DictationVocabularyCopy`, the same on Windows.
struct DictationVocabularySection: View {
    typealias Copy = DictationVocabularyCopy
    @ObservedObject var model: DictationModel
    @ObservedObject var vocabulary: DictationVocabularyModel
    @State private var written = ""
    @State private var soundsLike = ""
    @State private var confirmingForget = false
    @FocusState private var focus: Field?

    private enum Field { case word, soundsLike }

    var body: some View {
        VStack(alignment: .leading, spacing: DictationMetrics.sectionSpacing) {
            learning
            teaching
            words
        }
    }

    /// The switch and what it does.
    private var learning: some View {
        DictationCard {
            HStack(alignment: .firstTextBaseline, spacing: 16) {
                Text(Copy.learnSwitch)
                    .font(DictationType.bodyEmphasis)
                    .foregroundStyle(.brandInk)
                    // The switch carries the same name; read it once.
                    .accessibilityHidden(true)
                Spacer(minLength: 8)
                Toggle(Copy.learnSwitch, isOn: Binding(
                    get: { model.learningEnabled },
                    set: { model.setLearningEnabled($0) }
                ))
                .toggleStyle(.dictation)
                .labelsHidden()
            }
            DictationFootnote(Copy.disclosure)
            if model.learningEnabled, let notice = vocabulary.observationNotice {
                DictationNotice(symbol: "text.badge.checkmark", tone: .wheatStrong, text: notice)
            }
        }
        // Notices, refusals and receipts change text on a pane VoiceOver is
        // not reading; say them as they arrive.
        .onChange(of: vocabulary.observationNotice) { _, notice in announce(notice) }
        .onChange(of: vocabulary.teachMessage) { _, message in announce(message) }
        .onChange(of: vocabulary.managementNotice) { _, message in announce(message) }
        .onChange(of: vocabulary.learningNotice) { _, receipt in announce(receipt) }
    }

    private func announce(_ text: String?) {
        guard let text, !text.isEmpty else { return }
        AccessibilityNotification.Announcement(text).post()
    }

    /// Teach a word: what it is written as, and what Earlet writes for it now.
    private var teaching: some View {
        DictationSection("Add a word") {
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 8) { wordField; soundsLikeField; addButton }
                VStack(alignment: .leading, spacing: 8) {
                    wordField
                    soundsLikeField
                    addButton
                }
            }
            .onSubmit { teach() }
            DictationFootnote(Copy.soundsLikeHelp)
            if let message = vocabulary.teachMessage {
                DictationFootnote(message)
            }
            if let message = vocabulary.managementNotice, !vocabulary.unsaved {
                DictationFootnote(message)
            }
            if let notice = vocabulary.unsavedNotice {
                DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution,
                                text: vocabulary.managementNotice ?? notice) {
                    Button(vocabulary.retryingSave ? "Saving…" : "Save again") {
                        Task { await vocabulary.retrySaving() }
                    }
                    .buttonStyle(.dictation(.secondary))
                    .disabled(vocabulary.retryingSave)
                }
            }
        }
    }

    // Hidden labels (still what VoiceOver reads), the same words as the
    // prompt inside the field.
    private var wordField: some View {
        TextField(Copy.wordPlaceholder, text: $written, prompt: Text(Copy.wordPlaceholder))
            .labelsHidden()
            .focused($focus, equals: .word)
            .dictationField(focused: focus == .word)
            .frame(minWidth: 140)
    }

    private var soundsLikeField: some View {
        TextField(Copy.soundsLikePlaceholder, text: $soundsLike, prompt: Text(Copy.soundsLikePlaceholder))
            .labelsHidden()
            .accessibilityHint(Text(Copy.soundsLikeHelp))
            .focused($focus, equals: .soundsLike)
            .dictationField(focused: focus == .soundsLike)
            .frame(minWidth: 140)
    }

    private var addButton: some View {
        Button(Copy.add) { teach() }
            .buttonStyle(.dictation(.primary))
    }

    /// The list, then Forget all.
    private var words: some View {
        DictationSection(title: "Your words", accessory: {
            // Enabled even with an empty list: it also clears the pairs still
            // waiting for a second dictation and the removed-word memory.
            Button(Copy.forgetAll) { confirmingForget = true }
                .buttonStyle(.dictation(.destructive))
                .confirmationDialog(Copy.forgetAllQuestion, isPresented: $confirmingForget,
                                    titleVisibility: .visible) {
                    Button(Copy.forgetAll, role: .destructive) { Task { await vocabulary.forgetAll() } }
                    Button(Copy.cancel, role: .cancel) {}
                }
        }) {
            if vocabulary.rows.isEmpty {
                DictationFootnote(Copy.empty)
            } else {
                VStack(spacing: 0) {
                    ForEach(vocabulary.rows) { row in
                        HStack(alignment: .center, spacing: 12) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(row.title)
                                    .font(.system(size: 14, weight: .medium))
                                    .foregroundStyle(.brandInk)
                                if let detail = row.detail {
                                    Text(detail)
                                        .font(DictationType.callout)
                                        .foregroundStyle(.brandInk2)
                                }
                            }
                            Spacer(minLength: 8)
                            Button(Copy.remove) { Task { await vocabulary.remove(row) } }
                                .buttonStyle(.dictation(.quiet))
                                .accessibilityLabel(Copy.remove + " " + row.title)
                        }
                        .padding(.vertical, 10)
                        DictationDivider()
                    }
                }
            }
        }
    }

    private func teach() {
        let word = written, sound = soundsLike
        Task {
            if await vocabulary.teach(written: word, soundsLike: sound),
               written == word, soundsLike == sound {
                written = ""
                soundsLike = ""
            }
        }
    }
}
