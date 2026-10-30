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
            if model.isFirstSetup {
                Text(DictationPermissionStep.intro)
                    .font(DictationType.body)
                    .foregroundStyle(.brandInk2)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            DictationStatusHeader(model: model, result: model.recentResult)
            if !model.allPermissionsGranted || model.reopenNeeded {
                DictationPermissionChecklist(model: model, recoveryExpanded: permissionRecoveryExpanded)
            }
            // What waits for the user comes before the how-to card, which
            // never changes.
            DictationRecentResultSection(model: model, result: model.recentResult)
            DictationSavedTakeSection(model: model, result: model.recentResult)
            DictationLearningNoticeSection(vocabulary: model.vocabulary)
            // A copy with no speech files can never dictate, so it shows
            // no key to hold.
            if model.permissionStep == .ready, !model.speechResourcesMissing {
                DictationHoldKeyCard(model: model)
            }
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
    /// Watched for its failed paste: the dot must not glow green beside a
    /// sentence saying the words could not be typed.
    @ObservedObject var result: DictationRecentResult

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
                let failedPaste = glyph == .ready && result.entry?.needsRecovery == true
                DictationStatusDot(role: failedPaste ? .caution : Self.tone(glyph, firstSetup: model.isFirstSetup),
                                   glows: !failedPaste && (glyph == .ready || glyph == .recording))
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
            if !showingSetup, model.engineFailed, model.speechResourcesMissing {
                // Enable would fail the same way every time: this copy has
                // no speech files, and only a complete download brings them.
                Button("Download Earlet…") {
                    if let page = URL(string: DictationCopy.releasesPage) { NSWorkspace.shared.open(page) }
                }
                .buttonStyle(.dictation(.primary, large: true))
            } else {
                Button(showingSetup ? model.permissionStep.actionTitle
                       : model.enabled ? "Turn Off"
                       : model.engineFailed ? "Try Again" : "Turn On") {
                    Task {
                        if showingSetup { await model.continuePermissionSetup() }
                        else { await model.toggle() }
                    }
                }
                .buttonStyle(.dictation(nextStep ? .primary : .secondary, large: nextStep))
                .disabled(model.busy || model.recording || model.permissionRequestInProgress)
            }
            if model.permissionRequestInProgress {
                ProgressView().controlSize(.small)
                    .accessibilityLabel("Waiting for macOS permission")
            }
            if model.canCancel {
                Button("Cancel Dictation") { Task { await model.cancel() } }
                    .buttonStyle(.dictation(.secondary, large: nextStep))
            }
        }
    }

    /// The dot's colour: live for on, the recording red while the
    /// microphone is open, gold while the words are on their way and for a
    /// first run's next setup step, caution for anything to fix, and a
    /// quiet ink when off.
    private static func tone(_ glyph: DictationMenuGlyph, firstSetup: Bool) -> DictationColorRole {
        switch glyph {
        case .ready: return .live
        case .recording: return .recording
        case .transcribing: return .wheatStrong
        case .needsPermission, .needsReopen: return firstSetup ? .wheatStrong : .caution
        case .engineFailed: return .caution
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
            // The mark leaves first when the window is narrow. While
            // dictation is off the gesture does nothing, so the card says so
            // and steps back instead of promising it at full strength.
            ViewThatFits(in: .horizontal) {
                row(withMark: true)
                row(withMark: false)
            }
            .opacity(model.enabled ? 1 : 0.55)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(model.enabled
                ? "Hold \(model.holdKey.name) to talk, and release it to insert. Double-tap it for hands-free, and press it again to insert. Escape cancels."
                : "Dictation is off. Once it is on, hold \(model.holdKey.name) to talk, and release it to insert.")
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
                Text(model.enabled ? "Hold to talk, release to insert" : "Off: turn Earlet on to use this key")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(.brandInk)
                    .fixedSize(horizontal: false, vertical: true)
                if let mouseButton = model.mouseButton {
                    Text("Or hold \(mouseButton.name)")
                        .font(DictationType.callout)
                        .foregroundStyle(.brandInk2)
                }
                // One line when it fits; otherwise it breaks at the
                // separator, never inside a phrase.
                ViewThatFits(in: .horizontal) {
                    Text("Double-tap for hands-free, press again to insert · Esc to cancel")
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Double-tap for hands-free, press again to insert")
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
        let step = model.permissionStep
        let pending: PermissionRow.Pending = model.isFirstSetup ? .notYet : .notAllowed
        DictationSection(title: "Permissions", accessory: {
            Text("\(count) of 3 allowed")
                .font(DictationType.callout)
                .foregroundStyle(.brandInk2)
        }) {
            if !installation.isInApplications {
                // macOS gives each permission to one copy of the app; this
                // used to sit only inside the collapsed disclosure below.
                DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution,
                                text: "This copy of Earlet is not in Applications. Move it there and open it from there before you allow anything: macOS gives each permission to one copy.") {
                    Button("Show in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: installation.path)])
                    }
                    .buttonStyle(.dictation(.secondary))
                }
            }
            DictationCard(padding: 0, spacing: 0) {
                PermissionRow(number: 1, title: "Microphone", detail: "Hears your speech.",
                              granted: model.microphoneGranted, current: step == .microphone, pending: pending)
                DictationDivider()
                PermissionRow(number: 2, title: DictationPermissionStep.accessibilitySettingsName,
                              detail: "Types the words where your cursor is.", granted: model.accessibilityGranted,
                              current: step == .accessibility, pending: pending)
                DictationDivider()
                PermissionRow(number: 3, title: "Input Monitoring", detail: "Lets your dictation key work in any app.",
                              granted: model.inputMonitoringGranted, current: step == .inputMonitoring, pending: pending)
            }
            // Restart is the one thing left once every grant is in: no
            // second Restart button and no check that can only say so again.
            if step != .restart {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    DictationFootnote("Earlet checks again when you come back from System Settings.")
                    Button("Check Again") { model.checkPermissionsAgain() }
                        .buttonStyle(.dictation(.secondary))
                }
                if let message = model.permissionCheckMessage {
                    DictationFootnote(message)
                        .accessibilityLabel("Permission check: \(message)")
                }
                recovery
            }
        }
        // Nothing else on the page tells VoiceOver that a step is done or
        // what Check Again found.
        .onChange(of: step) { _, now in
            AccessibilityNotification.Announcement(now.arrivalAnnouncement).post()
        }
        .onChange(of: model.permissionCheckMessage) { _, message in
            if let message { AccessibilityNotification.Announcement(message).post() }
        }
    }

    private let installation = DictationInstallation.current

    private var recovery: some View {
        DisclosureGroup("Already on in System Settings?", isExpanded: $recoveryExpanded) {
            VStack(alignment: .leading, spacing: 12) {
                Text("First, restart Earlet. macOS can turn a permission on before the running app can use it.")
                    .font(DictationType.body)
                    .foregroundStyle(.brandInk)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Restart Earlet") { model.quitAndReopen() }
                    .buttonStyle(.dictation(.secondary))
                    .disabled(model.busy || model.recording)
                DictationFootnote("Still blocked? An older copy of Earlet may hold the permission. In \(DictationPermissionStep.accessibilitySettingsName) or Input Monitoring, select Earlet and remove it with −, then add the copy shown below with +, turn it on, and restart Earlet. For Microphone, turn Earlet off and on again.")
                DictationInstallationDetails()
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .disclosureGroupStyle(.dictation)
        .padding(.top, 4)
    }

    /// One grant. On a first run a missing one is a step not reached yet
    /// ("Not yet", in ink) and the step the button above asks for is marked
    /// "Next" in gold; once dictation has been on, a missing grant is
    /// something that broke ("Not allowed", in the caution colour).
    private struct PermissionRow: View {
        enum Pending { case notYet, notAllowed }

        let number: Int
        let title: String
        let detail: String
        let granted: Bool
        let current: Bool
        let pending: Pending

        private var stateLabel: String {
            if granted { return "Allowed" }
            if current { return "Next" }
            return pending == .notYet ? "Not yet" : "Not allowed"
        }

        private var stateStyle: AnyShapeStyle {
            if granted { return AnyShapeStyle(.brandInk2) }
            if current { return AnyShapeStyle(.brandWheatInk) }
            return pending == .notYet ? AnyShapeStyle(.brandInk2) : AnyShapeStyle(.brandCaution)
        }

        var body: some View {
            HStack(alignment: .center, spacing: 12) {
                ZStack {
                    Circle().fill(granted ? AnyShapeStyle(.brandLive)
                                  : current ? AnyShapeStyle(.brandWheatWash) : AnyShapeStyle(.brandRaised))
                    if granted {
                        Image(systemName: "checkmark")
                            .font(.system(size: 10, weight: .heavy))
                            .foregroundStyle(.brandGround)
                    } else {
                        Text("\(number)")
                            .font(.system(size: 11, weight: .bold, design: .rounded))
                            .foregroundStyle(current ? AnyShapeStyle(.brandWheatInk) : AnyShapeStyle(.brandInk2))
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
                Text(stateLabel)
                    .font(current ? DictationType.bodyEmphasis : DictationType.callout)
                    .foregroundStyle(stateStyle)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .accessibilityElement(children: .combine)
        }
    }
}
