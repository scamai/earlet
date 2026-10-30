import AppKit
import DictationAppCore
import SwiftUI

// The main window's shell: the rail and the selected pane beside it. The
// scenes, the menu bar and the delegate are in DictationApp.swift; the panes
// are DictationHomeView.swift, DictationHistoryView.swift,
// DictationVocabularyView.swift and DictationSettingsView.swift, all built
// from the components in DictationDesign.swift.

/// The main window: the product's own rail (the mark, the four panes and
/// where the audio stays) beside the selected pane, on the window's warm
/// ground. It used to be System Settings' sidebar beside grouped forms,
/// which made a dictation app read as a preference pane.
///
/// Internal (not private) so `DictationWindowRenderMode` renders this view.
struct DictationWindow: View {
    @ObservedObject var model: DictationModel
    @ObservedObject var navigation: DictationWindowNavigation
    /// Open only when the render mode asks, to draw what is inside.
    private let troubleshootingExpanded: Bool
    private let permissionRecoveryExpanded: Bool
    private let updater: DictationUpdater?

    init(model: DictationModel, navigation: DictationWindowNavigation = .shared, updater: DictationUpdater? = nil,
         troubleshootingExpanded: Bool = false, permissionRecoveryExpanded: Bool = false) {
        self.model = model
        self.navigation = navigation
        self.updater = updater
        self.troubleshootingExpanded = troubleshootingExpanded
        self.permissionRecoveryExpanded = permissionRecoveryExpanded
    }

    var body: some View {
        HStack(spacing: 0) {
            DictationRail(model: model, result: model.recentResult, navigation: navigation)
            Rectangle().fill(.brandHairline).frame(width: 1).accessibilityHidden(true)
            Group {
                switch navigation.pane {
                case .home:
                    DictationHomePane(model: model, history: model.history, navigation: navigation,
                                      permissionRecoveryExpanded: permissionRecoveryExpanded)
                case .history:
                    DictationHistoryPane(model: model, history: model.history)
                case .vocabulary:
                    DictationVocabularyPane(model: model, vocabulary: model.vocabulary)
                case .settings:
                    DictationSettingsPane(model: model, history: model.history, updater: updater,
                                          troubleshootingExpanded: troubleshootingExpanded)
                }
            }
            // The rail and the narrowest pane make the 600-point minimum
            // the window has always had.
            .frame(minWidth: 600 - DictationMetrics.railWidth - 1, maxWidth: .infinity,
                   minHeight: 420, maxHeight: .infinity)
        }
        .background(.brandGround)
        // The title bar is hidden (DictationApp.swift) and the rail and the
        // pages keep clear of the traffic lights themselves, so the
        // offscreen render, which has no title bar, draws the same layout.
        .ignoresSafeArea(.container, edges: .top)
        // Native switches and the text cursor take the brand's gold.
        .tint(DictationColor(.wheatStrong))
        .navigationTitle(navigation.pane.title)
        .onAppear {
            DictationMainWindow.contentAppeared()
            model.refreshCaution()
            model.refreshLaunchAtLogin()
        }
        .task { await model.vocabulary.refresh() }
    }
}

/// The rail: the mark and the name, the panes (⌘1 to ⌘4), and a line on
/// where the audio stays. Home carries a dot while it holds something only
/// the user can finish (`homeNeedsAttention`), so a missing grant or words
/// waiting to be copied are not hidden behind another pane.
private struct DictationRail: View {
    @ObservedObject var model: DictationModel
    /// Observed for the dot: words waiting after a refused paste live here.
    @ObservedObject var result: DictationRecentResult
    @ObservedObject var navigation: DictationWindowNavigation

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            DictationLockup()
                .padding(.horizontal, 10)
                .padding(.bottom, 22)
            ForEach(Array(DictationWindowPane.allCases.enumerated()), id: \.element) { index, pane in
                DictationRailItem(pane: pane, selected: navigation.pane == pane,
                                  marked: pane == .home && model.homeNeedsAttention,
                                  markRole: model.isFirstSetup ? .wheatStrong : .caution) {
                    navigation.pane = pane
                }
                .keyboardShortcut(KeyEquivalent(Character("\(index + 1)")), modifiers: .command)
            }
            Spacer(minLength: 16)
            Text("Your audio stays on this Mac.\nNo account needed.")
                .font(DictationType.caption)
                .foregroundStyle(.brandInk2)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 10)
        }
        .padding(.top, 54)
        .padding(.horizontal, 10)
        .padding(.bottom, 18)
        .frame(width: DictationMetrics.railWidth, alignment: .leading)
        .frame(maxHeight: .infinity, alignment: .top)
    }
}

/// One pane in the rail. Its state is ink, not a tile: the selected pane's
/// glyph turns gold and its name goes to full ink and semibold, the others
/// stay quieter until the pointer is over them.
private struct DictationRailItem: View {
    let pane: DictationWindowPane
    let selected: Bool
    let marked: Bool
    /// The mark's colour: a first run's next step is gold, anything that
    /// broke is the caution colour.
    var markRole: DictationColorRole = .caution
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: pane.symbolName)
                    .font(.system(size: 14, weight: selected ? .semibold : .regular))
                    .foregroundStyle(selected ? AnyShapeStyle(.brandWheat)
                                     : hovering ? AnyShapeStyle(.brandInk) : AnyShapeStyle(.brandInk2))
                    .frame(width: 20)
                Text(pane.title)
                    .font(.system(size: 13, weight: selected ? .semibold : .medium))
                    .foregroundStyle(selected || hovering ? AnyShapeStyle(.brandInk) : AnyShapeStyle(.brandInk2))
                Spacer(minLength: 4)
                if marked {
                    Circle().fill(DictationColor(markRole)).frame(width: 7, height: 7)
                }
            }
            .padding(.horizontal, 10)
            .frame(height: 32)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityLabel(marked ? "\(pane.title), needs your attention" : pane.title)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}
