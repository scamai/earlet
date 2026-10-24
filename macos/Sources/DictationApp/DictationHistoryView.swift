import AppKit
import AVFoundation
import DictationAppCore
import DictationHistory
import SwiftUI

// The main window's History pane: the saved dictations by day, searchable,
// each with Copy, its recording when one is kept, and Delete. What it says
// and how it groups is DictationHistoryCopy (DictationAppCore); the shell is
// DictationWindowView.swift and the components DictationDesign.swift.

/// The saved dictations, newest first, grouped by day. The words are shown
/// in full here (up to four lines a row), the one place outside the
/// indicator's live preview and Copy last text that shows them; the pane is
/// the user's own record, and every row can be deleted.
struct DictationHistoryPane: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var history: DictationHistoryModel
    @StateObject private var player = DictationHistoryPlayer()
    @State private var query = ""
    @State private var deleting: DictationHistoryEntry?

    var body: some View {
        content
            // The microphone may be open: a recording played now would be
            // typed with the user's words.
            .onChange(of: model.recording) { _, recording in if recording { player.stop() } }
            .onDisappear { player.stop() }
            .confirmationDialog(DictationHistoryCopy.deleteOneQuestion, isPresented: Binding(
                get: { deleting != nil }, set: { if !$0 { deleting = nil } }),
                titleVisibility: .visible, presenting: deleting) { entry in
                    Button("Delete", role: .destructive) {
                        if player.playing == entry.id { player.stop() }
                        Task { await history.delete(entry) }
                    }
                    Button("Cancel", role: .cancel) {}
            } message: { _ in
                Text(DictationHistoryCopy.deleteOneDetail)
            }
    }

    @ViewBuilder
    private var content: some View {
        switch history.state {
        case .unavailable:
            DictationEmptyState(symbol: "clock", title: "History is unavailable",
                                detail: "There is no Application Support folder to keep it in.")
        case .locked:
            DictationEmptyState(symbol: "lock.fill", title: DictationHistoryCopy.lockedTitle,
                                detail: DictationHistoryCopy.lockedDetail) {
                Button("Try Again") { Task { await history.retry() } }
                    .buttonStyle(.dictation(.secondary))
            }
        case .loading:
            ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
        case .ready where history.entries.isEmpty && history.unreadable == 0 && history.newer == 0:
            if history.enabled {
                DictationEmptyState(title: DictationHistoryCopy.emptyTitle, detail: DictationHistoryCopy.emptyDetail)
            } else {
                DictationEmptyState(symbol: "pause.fill", title: DictationHistoryCopy.offTitle,
                                    detail: DictationHistoryCopy.offDetail) {
                    Button("Turn On History") { history.setEnabled(true) }
                        .buttonStyle(.dictation(.primary))
                }
            }
        case .ready:
            list
        }
    }

    private var list: some View {
        let days = DictationHistoryCopy.days(history.entries, matching: query)
        return ScrollView {
            // Lazy: a history kept for good runs to thousands of rows.
            LazyVStack(alignment: .leading, spacing: 0) {
                header
                    .padding(.bottom, 20)
                notices
                ForEach(days) { day in
                    Text(day.title)
                        .font(DictationType.sectionTitle)
                        .foregroundStyle(.brandInk)
                        .accessibilityAddTraits(.isHeader)
                        .padding(.top, 16)
                        .padding(.bottom, 4)
                    ForEach(day.entries) { entry in
                        DictationHistoryRow(
                            entry: entry,
                            hasRecording: history.recordings.contains(entry.id),
                            playing: player.playing == entry.id,
                            canPlay: !model.recording,
                            copy: { Task { await history.copy(entry) } },
                            play: { togglePlayback(entry) },
                            delete: { deleting = entry })
                        DictationDivider()
                    }
                }
                if days.isEmpty && !query.isEmpty {
                    Text("No dictation matches “\(query)”.")
                        .font(DictationType.body)
                        .foregroundStyle(.brandInk2)
                        .padding(.top, 16)
                }
            }
            .measuringNarrow()
            .frame(maxWidth: DictationMetrics.pageMaxWidth, alignment: .leading)
            .padding(.horizontal, DictationMetrics.pageHorizontalPadding)
            .padding(.top, DictationMetrics.pageTopPadding)
            .padding(.bottom, 32)
            .frame(maxWidth: .infinity)
        }
    }

    /// The title, how many are kept and where, and the search field beside
    /// it, or under it in a narrow window.
    private var header: some View {
        let subtitle = "\(history.entries.count.formatted()) saved, encrypted on this Mac"
        return ViewThatFits(in: .horizontal) {
            HStack(alignment: .center, spacing: 16) {
                DictationPageHeader(title: "History", subtitle: subtitle)
                DictationSearchField(text: $query, prompt: "Search history").frame(width: 220)
            }
            VStack(alignment: .leading, spacing: 14) {
                DictationPageHeader(title: "History", subtitle: subtitle)
                DictationSearchField(text: $query, prompt: "Search history")
            }
        }
    }

    @ViewBuilder
    private var notices: some View {
        if !history.enabled {
            DictationNotice(symbol: "pause.fill", text: DictationHistoryCopy.offTitle + ". New dictations aren't saved.") {
                Button("Turn On") { history.setEnabled(true) }
                    .buttonStyle(.dictation(.secondary))
            }
            .padding(.bottom, 12)
        }
        if history.unreadable > 0 {
            DictationNotice(symbol: "exclamationmark.triangle.fill", tone: .caution,
                            text: DictationHistoryCopy.unreadable(history.unreadable)) {
                Button("Delete Them") { Task { await history.deleteUnreadable() } }
                    .buttonStyle(.dictation(.destructive))
            }
            .padding(.bottom, 12)
        }
        if history.newer > 0 {
            DictationNotice(symbol: "info.circle.fill", text: DictationHistoryCopy.newer(history.newer))
                .padding(.bottom, 12)
        }
        if let notice = history.notice {
            DictationFootnote(notice)
                .padding(.bottom, 12)
        }
    }

    private func togglePlayback(_ entry: DictationHistoryEntry) {
        if player.playing == entry.id {
            player.stop()
            return
        }
        guard !model.recording else { return }
        Task {
            guard let pcm = await history.recording(for: entry), !model.recording else { return }
            player.play(entry.id, pcm16: pcm)
        }
    }
}

/// The history's search: a plain field on the surface, a gold edge while
/// it has focus, ⌘F to reach it and Escape to clear it.
private struct DictationSearchField: View {
    @Binding var text: String
    let prompt: String
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(.brandInk2)
                .accessibilityHidden(true)
            TextField(prompt, text: $text, prompt: Text(prompt))
                .textFieldStyle(.plain)
                .font(DictationType.body)
                .focused($focused)
                .onExitCommand { text = "" }
            if !text.isEmpty {
                Button { text = "" } label: {
                    Image(systemName: "xmark.circle.fill").foregroundStyle(.brandInk3)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 10)
        .frame(height: 30)
        .background(.brandSurface, in: RoundedRectangle(cornerRadius: DictationMetrics.controlRadius, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: DictationMetrics.controlRadius, style: .continuous)
            .strokeBorder(focused ? AnyShapeStyle(.brandWheatStrong) : AnyShapeStyle(.brandHairline),
                          lineWidth: focused ? 1.5 : 1))
        // ⌘F, as in any searchable list: a button with no size, out of
        // VoiceOver's way.
        .background {
            Button("Search history") { focused = true }
                .keyboardShortcut("f")
                .opacity(0)
                .frame(width: 0, height: 0)
                .accessibilityHidden(true)
        }
    }
}

/// One dictation: its time, its words, where they went and how long it was,
/// then Play (with a recording), Copy and a menu with Delete. Every action is
/// a visible control and also in the row's context menu; none waits for
/// hover.
private struct DictationHistoryRow: View {
    let entry: DictationHistoryEntry
    let hasRecording: Bool
    let playing: Bool
    let canPlay: Bool
    let copy: () -> Void
    let play: () -> Void
    let delete: () -> Void

    @Environment(\.dictationNarrow) private var narrow

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            // In a narrow window the time joins the line under the words,
            // which keep the width.
            if !narrow {
                Text(entry.recordedAt, format: .dateTime.hour().minute())
                    .font(DictationType.time)
                    .foregroundStyle(.brandInk2)
                    .frame(width: 64, alignment: .leading)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text(entry.text)
                    .font(.system(size: 14))
                    .foregroundStyle(.brandInk)
                    .lineLimit(4)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                Text(narrow ? entry.recordedAt.formatted(date: .omitted, time: .shortened) + " · " + detail : detail)
                    .font(DictationType.callout)
                    .foregroundStyle(.brandInk2)
            }
            Spacer(minLength: 8)
            HStack(spacing: 2) {
                if hasRecording {
                    Button(action: play) {
                        Image(systemName: playing ? "stop.fill" : "play.fill")
                    }
                    .disabled(!canPlay && !playing)
                    .help(playing ? "Stop" : "Play the recording")
                    .accessibilityLabel(playing ? "Stop the recording" : "Play the recording")
                }
                Button(action: copy) { Image(systemName: "doc.on.doc") }
                    .help("Copy")
                    .accessibilityLabel("Copy")
                Menu {
                    menuItems
                } label: {
                    Image(systemName: "ellipsis")
                }
                .menuStyle(.button)
                .menuIndicator(.hidden)
                .fixedSize()
                .help("More")
                .accessibilityLabel("More")
            }
            .buttonStyle(.dictationIcon)
            .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
        }
        .padding(.vertical, 12)
        .contextMenu { menuItems }
    }

    @ViewBuilder
    private var menuItems: some View {
        Button("Copy", action: copy)
        if hasRecording {
            Button(playing ? "Stop" : "Play Recording", action: play).disabled(!canPlay && !playing)
        }
        Divider()
        Button("Delete…", role: .destructive, action: delete)
    }

    /// "Notes · 0:12", with the language and how the words were delivered
    /// when there is something to say.
    private var detail: String {
        var parts: [String] = []
        if let name = DictationAppName.name(for: entry.appBundleID) { parts.append(name) }
        parts.append(DictationCopy.takeLength(entry.audioSeconds))
        if let language = entry.language, let name = Locale.current.localizedString(forLanguageCode: language) {
            parts.append(name)
        }
        if let delivery = DictationHistoryCopy.delivery(entry.delivery) { parts.append(delivery) }
        return parts.joined(separator: " · ")
    }
}

/// An app's display name from its bundle identifier, as Finder names it,
/// remembered for the life of the process; nil when it is not installed.
@MainActor
enum DictationAppName {
    private static var names: [String: String?] = [:]

    static func name(for bundleID: String?) -> String? {
        guard let bundleID, !bundleID.isEmpty else { return nil }
        if let known = names[bundleID] { return known }
        let found = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)
            .map { FileManager.default.displayName(atPath: $0.path) }
            .map { $0.hasSuffix(".app") ? String($0.dropLast(4)) : $0 }
        names[bundleID] = found
        return found
    }
}

/// Plays one kept recording at a time, from memory: the sealed file is
/// opened into memory and handed to the player as a WAV, never written out
/// in the clear.
@MainActor
final class DictationHistoryPlayer: NSObject, ObservableObject, AVAudioPlayerDelegate {
    @Published private(set) var playing: UUID?
    private var player: AVAudioPlayer?

    func play(_ id: UUID, pcm16: Data) {
        stop()
        guard let player = try? AVAudioPlayer(data: Self.wav(pcm16: pcm16, sampleRate: 16_000)) else { return }
        player.delegate = self
        self.player = player
        if player.play() { playing = id }
    }

    func stop() {
        player?.stop()
        player = nil
        playing = nil
    }

    nonisolated func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        Task { @MainActor in self.stop() }
    }

    /// A 44-byte RIFF header in front of mono 16-bit little-endian PCM.
    static func wav(pcm16: Data, sampleRate: Int) -> Data {
        func le32(_ value: UInt32) -> Data { withUnsafeBytes(of: value.littleEndian) { Data($0) } }
        func le16(_ value: UInt16) -> Data { withUnsafeBytes(of: value.littleEndian) { Data($0) } }
        var header = Data("RIFF".utf8)
        header += le32(UInt32(36 + pcm16.count))
        header += Data("WAVEfmt ".utf8)
        header += le32(16) + le16(1) + le16(1)
        header += le32(UInt32(sampleRate)) + le32(UInt32(sampleRate * 2))
        header += le16(2) + le16(16)
        header += Data("data".utf8) + le32(UInt32(pcm16.count))
        return header + pcm16
    }
}
