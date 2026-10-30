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
    /// What each recorder last said: what to press, which control it chose,
    /// or why a press was refused.
    @State private var holdKeyNote: DictationRecorderNote?
    @State private var mouseButtonNote: DictationRecorderNote?
    /// Owned by the pane, not the row: the row's control is rebuilt when a
    /// resize moves it under its words, and a recording must outlive that.
    @StateObject private var recordingSession = DictationRecordingSession()
    private let updater: DictationUpdater?

    init(model: DictationModel, history: DictationHistoryModel, updater: DictationUpdater? = nil,
         troubleshootingExpanded: Bool = false) {
        self.model = model
        self.history = history
        self.updater = updater
        _troubleshootingExpanded = State(initialValue: troubleshootingExpanded)
    }

    var body: some View {
        DictationPage {
            DictationPageHeader(title: "Settings")
            DictationSection("Dictating") {
                VStack(alignment: .leading, spacing: 0) {
                    DictationRow(title: "Hold to talk",
                                 detail: "Hold \(model.holdKey.name) to talk and release to insert. Double-tap it to keep listening.") {
                        if model.recordingHoldKey {
                            DictationRecordingPrompt(prompt: "Press a key…",
                                                     spoken: "Recording a hold key. Press a key or a key combination. Escape cancels.") {
                                finishRecording(nil)
                            }
                        } else {
                            DictationChoice(title: "Hold to talk", current: model.holdKey.label, selection: Binding(
                                get: { model.holdKey },
                                set: { model.selectHoldKey($0) }
                            )) {
                                ForEach(holdKeyChoices) { key in
                                    Text(key.label).tag(key)
                                }
                            } actions: {
                                Button("Record a Key…") { startRecording() }
                            }
                            .disabled(model.busy || model.recording || model.recordingMouseButton)
                        }
                    }
                    if let holdKeyNote {
                        DictationFootnote(holdKeyNote.text, role: holdKeyNote.refused ? .caution : .ink2)
                            .padding(.vertical, 12)
                        DictationDivider()
                    }
                    if let caution = model.caution {
                        DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution, text: caution)
                            .padding(.vertical, 12)
                        DictationDivider()
                    }
                    DictationShortcutCheck(model: model, check: model.keyCheck)
                    DictationRow(title: "Mouse button",
                                 detail: "Optional. Hold a side button to talk, or click or double-click it to keep listening and click again to stop.") {
                        if model.recordingMouseButton {
                            DictationRecordingPrompt(prompt: "Click a mouse button…",
                                                     spoken: "Recording a mouse button. Click a side or extra mouse button over this window. Escape cancels.") {
                                finishMouseRecording(nil)
                            }
                        } else {
                            DictationChoice(title: "Mouse button", current: model.mouseButton?.label ?? "None",
                                            selection: Binding(
                                                get: { model.mouseButton },
                                                set: { model.selectMouseButton($0) }
                                            )) {
                                Text("None").tag(DictationMouseSideButton?.none)
                                ForEach(mouseButtonChoices) { button in
                                    Text(button.label).tag(Optional(button))
                                }
                            } actions: {
                                Button("Record a Mouse Button…") { startMouseRecording() }
                            }
                            .disabled(model.busy || model.recording || model.recordingHoldKey)
                        }
                    }
                    if let mouseButtonNote {
                        DictationFootnote(mouseButtonNote.text, role: mouseButtonNote.refused ? .caution : .ink2)
                            .padding(.vertical, 12)
                        DictationDivider()
                    }
                    if model.mouseButtonRefused, let button = model.mouseButton {
                        DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution,
                                        text: "macOS did not let Earlet listen to \(button.name). In Privacy & Security, allow Earlet under Input Monitoring and under \(DictationPermissionStep.accessibilitySettingsName), then restart Earlet. \(model.holdKey.name) still works.")
                            .padding(.vertical, 12)
                        DictationDivider()
                    } else if let caution = model.mouseButtonCaution {
                        DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution, text: caution)
                            .padding(.vertical, 12)
                        DictationDivider()
                    }
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
                    DictationRow(title: "Open Earlet at login", detail: model.loginItemNote,
                                 showsDivider: updater?.isAvailable == true) {
                        Toggle("Open Earlet at login", isOn: Binding(
                            get: { model.launchAtLogin },
                            set: { model.setLaunchAtLogin($0) }
                        ))
                        .toggleStyle(.dictation)
                        .labelsHidden()
                    }
                    if let updater, updater.isAvailable {
                        DictationUpdateSettings(updater: updater)
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
        .onDisappear {
            // Leaving Settings, or closing the window, ends a recording.
            if model.recordingHoldKey { finishRecording(nil) }
            if model.recordingMouseButton { finishMouseRecording(nil) }
        }
    }
}

private extension DictationSettingsPane {
    /// The picker's list: the presets, then a recorded key while it is the
    /// chosen one.
    var holdKeyChoices: [DictationHoldKey] {
        DictationHoldKey.presets.contains(model.holdKey)
            ? DictationHoldKey.presets
            : DictationHoldKey.presets + [model.holdKey]
    }

    /// The picker's list: the side buttons, then a recorded button while it
    /// is the chosen one.
    var mouseButtonChoices: [DictationMouseSideButton] {
        guard let chosen = model.mouseButton, !DictationMouseSideButton.presets.contains(chosen) else {
            return DictationMouseSideButton.presets
        }
        return DictationMouseSideButton.presets + [chosen]
    }

    /// The next key or key combination pressed over the window becomes the
    /// hold key.
    func startRecording() {
        guard model.beginRecordingHoldKey() else { return }
        holdKeyNote = DictationRecorderNote(
            "Press a key on its own (such as Right Option) or a combination (such as ⌥Space or F13). Escape cancels.")
        recordingSession.recordKey { outcome in
            switch outcome {
            case .cancelled: finishRecording(nil)
            case .rejected(let reason): holdKeyNote = DictationRecorderNote(reason, refused: true)
            case .recorded(let key):
                if let refusal = model.refusalForRecordedHoldKey(key) {
                    holdKeyNote = DictationRecorderNote(refusal, refused: true)
                } else {
                    finishRecording(key)
                }
            }
        }
    }

    func finishRecording(_ key: DictationHoldKey?) {
        recordingSession.stop()
        holdKeyNote = key.map { DictationRecorderNote("\($0.name) is your hold key now.") }
        model.endRecordingHoldKey(choosing: key)
    }

    /// The next side or extra mouse button clicked over the window becomes
    /// the mouse button.
    func startMouseRecording() {
        guard model.beginRecordingMouseButton() else { return }
        mouseButtonNote = DictationRecorderNote(
            "Click the button over this window: a side button or any extra one your mouse has. Escape cancels.")
        recordingSession.recordMouseButton { outcome in
            switch outcome {
            case .cancelled: finishMouseRecording(nil)
            case .rejected(let reason): mouseButtonNote = DictationRecorderNote(reason, refused: true)
            case .recorded(let button): finishMouseRecording(button)
            }
        }
    }

    func finishMouseRecording(_ button: DictationMouseSideButton?) {
        recordingSession.stop()
        mouseButtonNote = button.map { DictationRecorderNote("\($0.label) starts a dictation now.") }
        model.endRecordingMouseButton(choosing: button)
    }
}

/// A recorder's line under its row.
private struct DictationRecorderNote {
    var text: String
    /// A refusal, drawn in the caution tone.
    var refused = false

    init(_ text: String, refused: Bool = false) {
        self.text = text
        self.refused = refused
    }
}

/// What a row shows while Settings records: the prompt and Cancel.
private struct DictationRecordingPrompt: View {
    let prompt: String
    /// What VoiceOver reads for the prompt.
    let spoken: String
    let cancel: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            Text(prompt)
                .font(DictationType.callout)
                .foregroundStyle(.brandInk)
                .lineLimit(1)
                .fixedSize()
                .padding(.horizontal, 12)
                .frame(minHeight: 28)
                .background(RoundedRectangle(cornerRadius: DictationMetrics.controlRadius, style: .continuous)
                    .strokeBorder(.brandWheatStrong, lineWidth: 1.5))
                .accessibilityLabel(spoken)
            Button("Cancel", action: cancel)
                .buttonStyle(.dictation(.secondary))
        }
        .fixedSize()
    }
}

/// The recorders' event monitor, for the length of one recording.
///
/// A local monitor, so it sees only events sent to this app's windows, and
/// it consumes what it records: ⌘W records ⌘W instead of closing the window.
/// Left and right clicks pass, so Cancel still works.
@MainActor
final class DictationRecordingSession: ObservableObject {
    private var monitor: Any?
    private var keys = DictationHoldKeyRecorder()

    /// Key presses and modifier changes, for the hold key.
    func recordKey(_ onOutcome: @escaping @MainActor (DictationHoldKeyRecorder.Outcome) -> Void) {
        keys = DictationHoldKeyRecorder()
        install(matching: [.keyDown, .flagsChanged]) { [weak self] event in
            guard let self else { return false }
            let flags = event.flags
            let input: DictationHoldKeyRecorder.Input
            switch event.kind {
            case .keyDown(let keyCode, let isRepeat):
                if isRepeat { return true }
                input = .keyDown(keyCode: keyCode, flags: flags)
            case .flagsChanged(let keyCode):
                input = .flagsChanged(keyCode: keyCode, flags: flags)
            case .otherMouseDown:
                return false
            }
            if let outcome = self.keys.handle(input) { onOutcome(outcome) }
            return true
        }
    }

    /// Clicks of the middle, side and extra buttons, for the mouse button;
    /// Escape cancels.
    func recordMouseButton(_ onOutcome: @escaping @MainActor (DictationRecorded<DictationMouseSideButton>) -> Void) {
        install(matching: [.otherMouseDown, .keyDown]) { event in
            switch event.kind {
            case .otherMouseDown(let button):
                if let outcome = DictationMouseSideButton.recorded(buttonNumber: button) { onOutcome(outcome) }
                return true
            case .keyDown(let keyCode, _):
                guard keyCode == HoldChordGuard.escapeKeyCode else { return false }
                onOutcome(.cancelled)
                return true
            case .flagsChanged:
                return false
            }
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    /// One event, read off the `NSEvent` before it leaves the monitor.
    private struct Event: Sendable {
        enum Kind: Sendable {
            case keyDown(keyCode: UInt16, isRepeat: Bool)
            case flagsChanged(keyCode: UInt16)
            case otherMouseDown(button: Int)
        }
        var kind: Kind
        var flags: CGEventFlags
    }

    /// `handle` answers whether the event was consumed.
    private func install(matching mask: NSEvent.EventTypeMask, _ handle: @escaping @MainActor (Event) -> Bool) {
        stop()
        monitor = NSEvent.addLocalMonitorForEvents(matching: mask) { event in
            guard let read = Self.read(event) else { return event }
            let consumed = MainActor.assumeIsolated { handle(read) }
            return consumed ? nil : event
        }
    }

    nonisolated private static func read(_ event: NSEvent) -> Event? {
        let flags = event.cgEvent?.flags ?? CGEventFlags(rawValue: UInt64(event.modifierFlags.rawValue))
        switch event.type {
        // `isARepeat` is defined for key events only; asked of any other
        // event it raises.
        case .keyDown: return Event(kind: .keyDown(keyCode: event.keyCode, isRepeat: event.isARepeat), flags: flags)
        case .flagsChanged: return Event(kind: .flagsChanged(keyCode: event.keyCode), flags: flags)
        case .otherMouseDown: return Event(kind: .otherMouseDown(button: event.buttonNumber), flags: flags)
        default: return nil
        }
    }
}

/// Automatic updates and the manual check (docs/design-macos.md#updates).
private struct DictationUpdateSettings: View {
    @ObservedObject var updater: DictationUpdater

    var body: some View {
        DictationRow(title: "Update Earlet automatically",
                     detail: "Once a day Earlet asks GitHub for a newer version, downloads it and installs it when Earlet next quits. Your settings, words and permissions stay.") {
            Toggle("Update Earlet automatically", isOn: Binding(
                get: { updater.automaticallyChecks },
                set: { updater.setAutomaticallyChecks($0) }
            ))
            .toggleStyle(.dictation)
            .labelsHidden()
        }
        DictationRow(title: updater.readyToInstall ? "An update is ready" : "Updates",
                     detail: DictationInstallation.current.versionLabel, showsDivider: false) {
            if updater.readyToInstall {
                Button("Restart to Update") { updater.restartToUpdate() }
                    .buttonStyle(.dictation(.secondary))
            } else {
                Button("Check for Updates…") { updater.checkForUpdates() }
                    .buttonStyle(.dictation(.secondary))
                    .disabled(!updater.canCheck)
            }
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
