import AppKit
import DictationAppCore
import DictationHistory
import SwiftUI

// Home, the main window's first pane: what dictation is doing, how to use
// it, setup while a grant is missing, and anything waiting for the user. The
// window's shell is in DictationWindowView.swift; the result, recording and
// learning cards are in DictationActivityView.swift; the components are in
// DictationDesign.swift.

/// The status as the page's headline, then the hold key drawn as a key
/// once setup is done, the permission steps while a grant is missing, the
/// cards that appear only while something waits for the user, the last
/// seven days' counts once there are any, and the practice field.
struct DictationHomePane: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var history: DictationHistoryModel
    @ObservedObject var navigation: DictationWindowNavigation
    let permissionRecoveryExpanded: Bool

    var body: some View {
        DictationPage {
            DictationStatusHeader(model: model)
            if model.permissionStep == .ready {
                DictationHoldKeyCard(model: model)
            }
            if !model.allPermissionsGranted || model.reopenNeeded {
                DictationPermissionChecklist(model: model, recoveryExpanded: permissionRecoveryExpanded)
            }
            DictationRecentResultSection(model: model, result: model.recentResult)
            DictationSavedTakeSection(model: model, result: model.recentResult)
            DictationLearningNoticeSection(vocabulary: model.vocabulary)
            if history.state == .ready, !history.entries.isEmpty {
                DictationWeekSection(stats: history.lastSevenDays, navigation: navigation)
            }
            if model.enabled && model.permissionStep == .ready {
                DictationPracticeSection(model: model)
            }
        }
    }
}

/// The last seven days, measured from the history: words, dictations,
/// speaking time and pace, as the page's figures. Nothing estimated: no
/// "time saved", which would need a typing speed nobody measured.
private struct DictationWeekSection: View {
    let stats: DictationHistoryStats
    @ObservedObject var navigation: DictationWindowNavigation

    var body: some View {
        DictationSection(title: "Last 7 days", accessory: {
            Button("Show History") { navigation.pane = .history }
                .buttonStyle(.dictation(.quiet))
        }) {
            // Four across when they fit; two by two in a narrow window.
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .top, spacing: 20) { figures }
                Grid(alignment: .topLeading, horizontalSpacing: 20, verticalSpacing: 14) {
                    GridRow { words; dictations }
                    GridRow { speaking; pace }
                }
            }
            DictationFootnote(DictationHistoryCopy.countsFootnote)
        }
    }

    @ViewBuilder private var figures: some View { words; dictations; speaking; pace }

    private var words: some View {
        figure(stats.words.formatted(), stats.words == 1 ? "word" : "words")
    }
    private var dictations: some View {
        figure(stats.dictations.formatted(), stats.dictations == 1 ? "dictation" : "dictations")
    }
    private var speaking: some View {
        figure(DictationHistoryCopy.speakingTime(stats.seconds), "speaking")
    }
    private var pace: some View {
        figure(stats.wordsPerMinute.map { $0.formatted() } ?? "–", "words a minute",
               spoken: stats.wordsPerMinute.map { "\($0.formatted()) words per minute" }
                   ?? "Words per minute: not enough speech yet")
    }

    private func figure(_ value: String, _ label: String, spoken: String? = nil) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value)
                .font(DictationType.figure)
                .monospacedDigit()
                .foregroundStyle(.brandInk)
                .lineLimit(1)
                .minimumScaleFactor(0.7)
            Text(label)
                .font(DictationType.callout)
                .foregroundStyle(.brandInk2)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(minWidth: 72, maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(spoken ?? "\(value) \(label)")
    }
}

/// The top of Home: the headline, the status sentence beside a dot in the
/// state's colour (the page's one glow, while dictation is on or
/// listening), and the control that changes the state, with Cancel beside
/// it while there is something to cancel. The control is the gold primary
/// only when it is the next step (a setup action, or turning dictation
/// on), under the sentence; Pause is a secondary button at the trailing
/// edge, so while dictation is on nothing outranks the hold key below.
private struct DictationStatusHeader: View {
    @ObservedObject var model: DictationModel

    var body: some View {
        let glyph = model.menuGlyph
        let setup = model.permissionStep
        let showingSetup = setup != .ready && !model.busy && !model.recording
        let nextStep = showingSetup || !model.enabled
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text(showingSetup ? setup.title : glyph.headline)
                    .font(DictationType.hero)
                    .foregroundStyle(.brandInk)
                    .fixedSize(horizontal: false, vertical: true)
                    .accessibilityAddTraits(.isHeader)
                Spacer(minLength: 8)
                if !nextStep { controls(showingSetup: showingSetup, nextStep: false) }
            }
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                DictationStatusDot(role: Self.tone(glyph), glows: glyph == .ready || glyph == .recording)
                    // On the sentence's first line, not its top edge.
                    .alignmentGuide(.firstTextBaseline) { $0[.bottom] }
                // Room for two lines, so a longer sentence does not move the
                // button under the pointer.
                Text(showingSetup ? setup.detail : model.status)
                    .font(DictationType.body)
                    .foregroundStyle(.brandInk2)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, minHeight: 34, alignment: .topLeading)
                    .accessibilityLabel("Status: \(showingSetup ? setup.detail : model.status)")
            }
            if nextStep { controls(showingSetup: showingSetup, nextStep: true) }
        }
    }

    @ViewBuilder
    private func controls(showingSetup: Bool, nextStep: Bool) -> some View {
        HStack(spacing: 8) {
            Button(showingSetup ? model.permissionStep.actionTitle
                   : model.enabled ? "Pause dictation" : "Enable dictation") {
                Task {
                    if showingSetup { await model.continuePermissionSetup() }
                    else { await model.toggle() }
                }
            }
            .buttonStyle(.dictation(nextStep ? .primary : .secondary, large: nextStep))
            .disabled(model.busy || model.recording || model.permissionRequestInProgress)
            if model.permissionRequestInProgress {
                ProgressView().controlSize(.small)
                    .accessibilityLabel("Waiting for macOS permission")
            }
            if model.canCancel {
                Button("Cancel dictation") { Task { await model.cancel() } }
                    .buttonStyle(.dictation(.secondary, large: nextStep))
            }
        }
    }

    /// The dot's colour: live for on, the recording red while the
    /// microphone is open, gold while the words are on their way, caution
    /// for anything to fix, and a quiet ink when off.
    private static func tone(_ glyph: DictationMenuGlyph) -> DictationColorRole {
        switch glyph {
        case .ready: return .live
        case .recording: return .recording
        case .transcribing: return .wheatStrong
        case .needsPermission, .needsReopen, .engineFailed: return .caution
        case .paused, .starting: return .ink3
        }
    }
}

/// How to dictate: the mark, the hold key drawn as a key, and the one
/// gesture the app asks for, at the size of the thing it is. Shown once
/// setup is done; the caution about the key sits in it, because it is
/// about this gesture.
private struct DictationHoldKeyCard: View {
    @ObservedObject var model: DictationModel

    var body: some View {
        DictationCard(padding: 18, spacing: 14) {
            // The mark leaves first when the window is narrow.
            ViewThatFits(in: .horizontal) {
                row(withMark: true)
                row(withMark: false)
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Hold \(model.holdKey.name) to talk, and release it to insert. Double-tap it to keep listening. Escape cancels.")
            if let caution = model.caution {
                DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution, text: caution)
            }
        }
    }

    private func row(withMark: Bool) -> some View {
        HStack(alignment: .center, spacing: 16) {
            if withMark { DictationMark(size: 40) }
            DictationKeycap(label: model.holdKey.keycapLabel)
            VStack(alignment: .leading, spacing: 3) {
                Text("Hold to talk, release to insert")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(.brandInk)
                    .fixedSize(horizontal: false, vertical: true)
                // One line when it fits; otherwise it breaks at the
                // separator, never inside a phrase.
                ViewThatFits(in: .horizontal) {
                    Text("Double-tap to keep listening · Esc to cancel")
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Double-tap to keep listening")
                        Text("Esc to cancel")
                    }
                }
                .font(DictationType.callout)
                .foregroundStyle(.brandInk2)
            }
            .frame(minWidth: 150, alignment: .leading)
            Spacer(minLength: 0)
        }
    }
}

/// The three grants as numbered steps: a filled disc with the step's number
/// until it is allowed, then a filled green disc with a check. Status stays
/// visible while the primary action above advances one step. Recovery is
/// explicit and never treats an enabled system switch as proof.
private struct DictationPermissionChecklist: View {
    @ObservedObject var model: DictationModel
    @State private var recoveryExpanded = false

    init(model: DictationModel, recoveryExpanded: Bool = false) {
        self.model = model
        _recoveryExpanded = State(initialValue: recoveryExpanded)
    }

    var body: some View {
        let count = [model.microphoneGranted, model.accessibilityGranted, model.inputMonitoringGranted]
            .filter { $0 }.count
        DictationSection(title: "Permissions", accessory: {
            Text("\(count) of 3 allowed")
                .font(DictationType.callout)
                .foregroundStyle(.brandInk2)
        }) {
            DictationCard(padding: 0, spacing: 0) {
                PermissionRow(number: 1, title: "Microphone", detail: "Hear your speech.",
                              granted: model.microphoneGranted)
                DictationDivider()
                PermissionRow(number: 2, title: DictationPermissionStep.accessibilitySettingsName,
                              detail: "Insert words where you type.", granted: model.accessibilityGranted)
                DictationDivider()
                PermissionRow(number: 3, title: "Input Monitoring", detail: "Use your shortcut in any app.",
                              granted: model.inputMonitoringGranted)
            }
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                DictationFootnote("Checked again when you come back from System Settings.")
                Button("Check again") { model.checkPermissionsAgain() }
                    .buttonStyle(.dictation(.secondary))
            }
            if let message = model.permissionCheckMessage {
                DictationFootnote(message)
                    .accessibilityLabel("Permission check: \(message)")
            }
            DisclosureGroup("Already enabled in System Settings?", isExpanded: $recoveryExpanded) {
                VStack(alignment: .leading, spacing: 12) {
                    Text("First, restart Earlet. A permission can be switched on before macOS makes it available to the running app.")
                        .font(DictationType.body)
                        .foregroundStyle(.brandInk)
                        .fixedSize(horizontal: false, vertical: true)
                    Button("Restart Earlet") { model.quitAndReopen() }
                        .buttonStyle(.dictation(.secondary))
                        .disabled(model.busy || model.recording)
                    DictationFootnote("Still blocked? An older copy may own the permission. In \(DictationPermissionStep.accessibilitySettingsName) or Input Monitoring, remove only Earlet with −, then use + to add the copy shown below. Enable it, then restart Earlet. For Microphone, switch Earlet off and on again.")
                    DictationInstallationDetails()
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .disclosureGroupStyle(.dictation)
            .padding(.top, 4)
        }
    }

    private struct PermissionRow: View {
        let number: Int
        let title: String
        let detail: String
        let granted: Bool

        var body: some View {
            HStack(alignment: .center, spacing: 12) {
                ZStack {
                    Circle().fill(granted ? AnyShapeStyle(.brandLive) : AnyShapeStyle(.brandRaised))
                    if granted {
                        Image(systemName: "checkmark")
                            .font(.system(size: 10, weight: .heavy))
                            .foregroundStyle(.brandGround)
                    } else {
                        Text("\(number)")
                            .font(.system(size: 11, weight: .bold, design: .rounded))
                            .foregroundStyle(.brandInk2)
                    }
                }
                .frame(width: 22, height: 22)
                .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(DictationType.bodyEmphasis)
                        .foregroundStyle(.brandInk)
                        .fixedSize(horizontal: false, vertical: true)
                    Text(detail)
                        .font(DictationType.callout)
                        .foregroundStyle(.brandInk2)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                Text(granted ? "Allowed" : "Not allowed")
                    .font(DictationType.callout)
                    .foregroundStyle(granted ? AnyShapeStyle(.brandInk2) : AnyShapeStyle(.brandCaution))
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .accessibilityElement(children: .combine)
        }
    }
}
