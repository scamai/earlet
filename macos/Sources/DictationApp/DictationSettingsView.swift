import AppKit
import DictationAppCore
import SwiftUI

// The main window's Settings pane, in the window's own components
// (DictationDesign.swift). The shell is DictationWindowView.swift.

/// The preferences, as headed groups of rows on the page rather than
/// System Settings' grey boxes: how to dictate, the history, general, and
/// Troubleshooting as a closed disclosure at the end.
struct DictationSettingsPane: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var history: DictationHistoryModel
    @State private var troubleshootingExpanded: Bool

    init(model: DictationModel, history: DictationHistoryModel, troubleshootingExpanded: Bool = false) {
        self.model = model
        self.history = history
        _troubleshootingExpanded = State(initialValue: troubleshootingExpanded)
    }

    var body: some View {
        DictationPage {
            DictationPageHeader(title: "Settings")
            DictationSection("Dictating") {
                VStack(alignment: .leading, spacing: 0) {
                    DictationRow(title: "Hold to talk",
                                 detail: "Hold \(model.holdKey.name) to talk and release to insert. Double-tap it to keep listening.") {
                        DictationChoice(title: "Hold to talk", current: model.holdKey.label, selection: Binding(
                            get: { model.holdKey },
                            set: { model.selectHoldKey($0) }
                        )) {
                            ForEach(DictationHoldKey.allCases) { key in
                                Text(key.label).tag(key)
                            }
                        }
                        .disabled(model.busy || model.recording)
                    }
                    if let caution = model.caution {
                        DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution, text: caution)
                            .padding(.vertical, 12)
                        DictationDivider()
                    }
                    DictationShortcutCheck(model: model, check: model.keyCheck)
                    DictationRow(title: "Language") {
                        DictationChoice(title: "Language", current: model.language.label, selection: Binding(
                            get: { model.language },
                            set: { model.selectLanguage($0) }
                        )) {
                            ForEach(DictationLanguage.allCases) { language in
                                Text(language.label).tag(language)
                            }
                        }
                    }
                    DictationRow(title: "Stop hands-free after silence",
                                 detail: "Up to ten minutes per recording. If the microphone or speech engine fails, the words captured so far are kept.",
                                 showsDivider: false) {
                        DictationChoice(title: "Stop hands-free after silence", current: model.silenceSetting.label,
                                        selection: Binding(get: { model.silenceSetting },
                                                           set: { model.selectSilenceSetting($0) })) {
                            ForEach(DictationSilenceSetting.allCases, id: \.self) { option in
                                Text(option.label).tag(option)
                            }
                        }
                        .disabled(model.recording || model.busy)
                    }
                }
            }
            if history.isAvailable {
                DictationHistorySettings(history: history)
            }
            DictationSection("General") {
                VStack(alignment: .leading, spacing: 0) {
                    DictationRow(title: "Open Earlet at login", detail: model.loginItemNote, showsDivider: false) {
                        Toggle("Open Earlet at login", isOn: Binding(
                            get: { model.launchAtLogin },
                            set: { model.setLaunchAtLogin($0) }
                        ))
                        .toggleStyle(.dictation)
                        .labelsHidden()
                    }
                }
            }
            // One click away here and in the menu bar menu, but no longer
            // level with the preferences.
            DisclosureGroup("Troubleshooting", isExpanded: $troubleshootingExpanded) {
                VStack(alignment: .leading, spacing: 14) {
                    DictationInstallationDetails()
                    HStack(spacing: 8) {
                        Button("Check permissions again") { model.checkPermissionsAgain() }
                        Button("Restart Earlet") { model.quitAndReopen() }
                            .disabled(model.busy || model.recording)
                        Button("Open Log Folder") { model.openLogFolder() }
                    }
                    .buttonStyle(.dictation(.secondary))
                    if let message = model.permissionCheckMessage, model.permissionStep == .ready {
                        DictationFootnote(message)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .disclosureGroupStyle(.dictation)
            DictationPrivacyFooter()
        }
    }
}

/// What the app keeps, said where the preferences end.
private struct DictationPrivacyFooter: View {
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Image(systemName: "lock.fill")
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(.brandWheatStrong)
                .accessibilityHidden(true)
            Text("Your audio stays on this device. No account is required. Your history is encrypted on this Mac and kept as long as you choose above; a recording that could not be transcribed is kept for you to recover.")
                .font(DictationType.callout)
                .foregroundStyle(.brandInk2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.top, 4)
    }
}

/// This copy's version, build and path, for Troubleshooting and the
/// permission recovery disclosure.
struct DictationInstallationDetails: View {
    private let installation = DictationInstallation.current

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("This copy of Earlet")
                .font(DictationType.bodyEmphasis)
                .foregroundStyle(.brandInk)
            Text(installation.versionLabel)
                .font(DictationType.callout)
                .foregroundStyle(.brandInk2)
            Text(installation.path)
                .font(DictationType.callout.monospaced())
                .foregroundStyle(.brandInk2)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
            if !installation.isInApplications {
                DictationFootnote("This copy is outside Applications. Keep one copy in Applications and open that copy before granting access.",
                                  role: .caution)
            }
            Button("Show This Copy in Finder") {
                NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: installation.path)])
            }
            .buttonStyle(.dictation(.quiet))
            .padding(.leading, -8)
        }
    }
}

/// The test owns a temporary listener; no recording is started by its presses.
private struct DictationShortcutCheck: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var check: DictationKeyCheckModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Button(check.isTesting ? "Stop key test" : "Test key") {
                    if check.isTesting { check.stopTest() } else { check.startTest() }
                }
                .disabled(model.recording || model.busy || model.recoveringSavedTake)
                if check.hasConflict {
                    Button("Try \(check.suggestedKey.name)") { model.selectHoldKey(check.suggestedKey) }
                    Button("Keep \(model.holdKey.name)") { check.keepKey() }
                }
            }
            .buttonStyle(.dictation(.secondary))
            if let message = check.message {
                DictationFootnote(message)
            }
        }
        .padding(.vertical, 12)
        DictationDivider()
    }
}

/// Settings › History: whether dictations are saved, how long words and
/// recordings are kept, what is stored, and Delete All History. A shorter
/// keep that would delete anything asks first.
private struct DictationHistorySettings: View {
    @ObservedObject var history: DictationHistoryModel
    @State private var pendingKeep: DictationHistoryKeep?
    @State private var pendingRecordingKeep: DictationRecordingKeep?
    @State private var confirmingDeleteAll = false

    var body: some View {
        DictationSection(title: "History", accessory: {
            Text(saved)
                .font(DictationType.callout)
                .foregroundStyle(.brandInk2)
                .accessibilityLabel("Saved: \(saved)")
        }) {
            VStack(alignment: .leading, spacing: 0) {
                DictationRow(title: "Save dictations") {
                    Toggle("Save dictations", isOn: Binding(get: { history.enabled }, set: { history.setEnabled($0) }))
                        .toggleStyle(.dictation)
                        .labelsHidden()
                }
                DictationRow(title: "Keep dictations") {
                    DictationChoice(title: "Keep dictations", current: history.keep.label,
                                    selection: Binding(get: { history.keep }, set: { choose($0) })) {
                        ForEach(DictationHistoryKeep.allCases) { Text($0.label).tag($0) }
                    }
                }
                DictationRow(title: "Keep recordings") {
                    DictationChoice(title: "Keep recordings", current: history.recordingKeep.label,
                                    selection: Binding(get: { history.recordingKeep }, set: { chooseRecordings($0) })) {
                        ForEach(DictationRecordingKeep.allCases) { Text($0.label).tag($0) }
                    }
                }
                VStack(alignment: .leading, spacing: 10) {
                    if history.state == .locked {
                        DictationNotice(symbol: "lock.fill", tone: .caution, text: DictationHistoryCopy.lockedTitle) {
                            Button("Try Again") { Task { await history.retry() } }
                                .buttonStyle(.dictation(.secondary))
                        }
                    } else if let notice = history.notice {
                        DictationFootnote(notice)
                    }
                    DictationFootnote(DictationHistoryCopy.storage)
                    Button("Delete All History…", role: .destructive) { confirmingDeleteAll = true }
                        .buttonStyle(.dictation(.destructive))
                        .disabled(history.state != .ready)
                }
                .padding(.top, 12)
            }
        }
        .disabled(history.state == .loading)
        .confirmationDialog(DictationHistoryCopy.deleteAllQuestion, isPresented: $confirmingDeleteAll,
                            titleVisibility: .visible) {
            Button("Delete All History", role: .destructive) { Task { await history.deleteAll() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text(DictationHistoryCopy.deleteAllDetail)
        }
        .confirmationDialog(keepQuestion, isPresented: Binding(
            get: { pendingKeep != nil || pendingRecordingKeep != nil },
            set: { if !$0 { pendingKeep = nil; pendingRecordingKeep = nil } }), titleVisibility: .visible) {
                Button("Delete", role: .destructive) { applyPending() }
                Button("Cancel", role: .cancel) { pendingKeep = nil; pendingRecordingKeep = nil }
        } message: {
            Text("They are deleted from this Mac now. This can't be undone.")
        }
    }

    /// "1,204 dictations · 312 MB of recordings".
    private var saved: String {
        let count = history.entries.count
        let dictations = "\(count.formatted()) \(count == 1 ? "dictation" : "dictations")"
        guard history.recordingBytes > 0 else { return dictations }
        let size = ByteCountFormatter.string(fromByteCount: history.recordingBytes, countStyle: .file)
        return "\(dictations) · \(size) of recordings"
    }

    private var keepQuestion: String {
        if let pendingKeep {
            return DictationHistoryCopy.keepQuestion(
                dictations: history.dictationsDeleted(by: pendingKeep), recordings: 0)
        }
        if let pendingRecordingKeep {
            return DictationHistoryCopy.keepQuestion(
                dictations: 0, recordings: history.recordingsDeleted(by: pendingRecordingKeep))
        }
        return ""
    }

    private func choose(_ keep: DictationHistoryKeep) {
        if history.dictationsDeleted(by: keep) > 0 {
            pendingKeep = keep
        } else {
            Task { await history.setKeep(keep) }
        }
    }

    private func chooseRecordings(_ keep: DictationRecordingKeep) {
        if history.recordingsDeleted(by: keep) > 0 {
            pendingRecordingKeep = keep
        } else {
            Task { await history.setRecordingKeep(keep) }
        }
    }

    private func applyPending() {
        let keep = pendingKeep, recordings = pendingRecordingKeep
        pendingKeep = nil
        pendingRecordingKeep = nil
        Task {
            if let keep { await history.setKeep(keep) }
            if let recordings { await history.setRecordingKeep(recordings) }
        }
    }
}
