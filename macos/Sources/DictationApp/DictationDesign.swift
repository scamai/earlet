import AppKit
import DictationAppCore
import SwiftUI

// The window's design system: type, spacing and radii, and the components
// every pane is built from: the mark, the page and its header, sections,
// cards, buttons, choices, fields, notices and empty states. Colours come
// from `DictationPalette` (DictationAppCore), so light, dark and Increase
// Contrast follow the environment. The controls stay native underneath
// (Button, Toggle, Menu, TextField, DisclosureGroup), so focus, the
// keyboard, VoiceOver and the disabled state keep working; only their
// drawing is the product's own.

// ─── Tokens ───

/// The type ramp. Headings and figures are SF Pro Rounded, the friendly
/// half of the identity; reading text stays SF Pro at the system's 13
/// points; times are monospaced so a column of them lines up.
enum DictationType {
    static let hero = Font.system(size: 26, weight: .bold, design: .rounded)
    static let pageTitle = Font.system(size: 24, weight: .bold, design: .rounded)
    static let sectionTitle = Font.system(size: 15, weight: .semibold, design: .rounded)
    static let figure = Font.system(size: 28, weight: .semibold, design: .rounded)
    static let wordmark = Font.system(size: 15, weight: .bold, design: .rounded)
    static let body = Font.system(size: 13)
    static let bodyEmphasis = Font.system(size: 13, weight: .semibold)
    static let callout = Font.system(size: 12)
    static let caption = Font.system(size: 11)
    static let time = Font.system(size: 12).monospacedDigit()
}

/// Spacing is on a 4-point grid; one radius per kind of thing, and none of
/// them a capsule.
enum DictationMetrics {
    static let railWidth: CGFloat = 188
    static let pageMaxWidth: CGFloat = 680
    static let pageHorizontalPadding: CGFloat = 32
    /// Clear of the traffic lights, which sit over the window's top edge.
    static let pageTopPadding: CGFloat = 44
    static let sectionSpacing: CGFloat = 28
    static let cardRadius: CGFloat = 14
    static let controlRadius: CGFloat = 8
    static let chipRadius: CGFloat = 6
}

enum DictationIdentity {
    /// Display identity; persistence and executable identifiers remain stable.
    static let name = "Earlet"
}

// ─── The mark ───

struct DictationMark: View {
    var size: CGFloat = 22

    var body: some View {
        EarletMark()
            .foregroundStyle(.brandWheat)
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}

/// The mark beside the name, at the top of the rail.
struct DictationLockup: View {
    var body: some View {
        HStack(spacing: 8) {
            DictationMark(size: 24)
            Text(DictationIdentity.name)
                .font(DictationType.wordmark)
                .foregroundStyle(.brandInk)
        }
        // The window's title already names the app for VoiceOver.
        .accessibilityHidden(true)
    }
}

// ─── Page structure ───

/// A pane's scrolling page: one column, at most 680 points wide and
/// centred in a wide window, on the window's ground.
struct DictationPage<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: DictationMetrics.sectionSpacing) {
                content
            }
            .measuringNarrow()
            .frame(maxWidth: DictationMetrics.pageMaxWidth, alignment: .leading)
            .padding(.horizontal, DictationMetrics.pageHorizontalPadding)
            .padding(.top, DictationMetrics.pageTopPadding)
            .padding(.bottom, 32)
            .frame(maxWidth: .infinity)
        }
        .scrollIndicators(.automatic)
    }
}

/// A destination's opening: its title, a quiet line under it, and room
/// for one control at the trailing edge (a search field, a count).
struct DictationPageHeader<Trailing: View>: View {
    let title: String
    var subtitle: String? = nil
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text(title)
                    .font(DictationType.pageTitle)
                    .foregroundStyle(.brandInk)
                    .accessibilityAddTraits(.isHeader)
                if let subtitle {
                    Text(subtitle)
                        .font(DictationType.body)
                        .foregroundStyle(.brandInk2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 0)
            trailing
        }
    }
}

extension DictationPageHeader where Trailing == EmptyView {
    init(title: String, subtitle: String? = nil) {
        self.init(title: title, subtitle: subtitle) { EmptyView() }
    }
}

/// A titled group on the page. A heading and the rows under it, with no
/// box around them: the page is not a stack of grey frames.
struct DictationSection<Content: View, Accessory: View>: View {
    let title: String
    @ViewBuilder var accessory: Accessory
    @ViewBuilder var content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(title)
                    .font(DictationType.sectionTitle)
                    .foregroundStyle(.brandInk)
                    .accessibilityAddTraits(.isHeader)
                Spacer(minLength: 8)
                accessory
            }
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

extension DictationSection where Accessory == EmptyView {
    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.init(title: title, accessory: { EmptyView() }, content: content)
    }
}

/// A card: white (or the dark surface) on the ground, a hairline edge, one
/// radius. Depth comes from the edge, not a shadow.
struct DictationCard<Content: View>: View {
    var padding: CGFloat = 16
    var spacing: CGFloat = 12
    @ViewBuilder var content: Content

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: DictationMetrics.cardRadius, style: .continuous)
        VStack(alignment: .leading, spacing: spacing) {
            content
        }
        .padding(padding)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.brandSurface, in: shape)
        .overlay(shape.strokeBorder(.brandHairline, lineWidth: 1))
    }
}

/// A row of a settings group: what it is and what it does on the leading
/// side, its control on the trailing side, a hairline between rows.
struct DictationRow<Control: View>: View {
    let title: String
    var detail: String? = nil
    var showsDivider = true
    @ViewBuilder var control: Control
    @Environment(\.dictationNarrow) private var narrow

    var body: some View {
        VStack(spacing: 0) {
            // A narrow window puts the control under its words, where the
            // words keep the whole width.
            if narrow {
                VStack(alignment: .leading, spacing: 8) {
                    labels
                    control
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 12)
            } else {
                HStack(alignment: .center, spacing: 16) {
                    labels
                    Spacer(minLength: 8)
                    control
                }
                .padding(.vertical, 12)
            }
            if showsDivider { DictationDivider() }
        }
    }

    private var labels: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(DictationType.body)
                .foregroundStyle(.brandInk)
                .fixedSize(horizontal: false, vertical: true)
            if let detail {
                Text(detail)
                    .font(DictationType.callout)
                    .foregroundStyle(.brandInk2)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

// ─── Width ───

/// Whether the page's column is narrow (the window near its 600-point
/// minimum), so rows stack their parts instead of squeezing them.
private struct DictationNarrowKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var dictationNarrow: Bool {
        get { self[DictationNarrowKey.self] }
        set { self[DictationNarrowKey.self] = newValue }
    }
}

/// Measures the view's own width and tells what is inside whether it is
/// narrow. The width a reader uses is the column's, after the page's
/// padding: 355 points at the window's minimum, 475 at its default.
struct DictationNarrowReader: ViewModifier {
    static let threshold: CGFloat = 420
    @State private var narrow = false

    func body(content: Content) -> some View {
        content
            .environment(\.dictationNarrow, narrow)
            .background {
                GeometryReader { proxy in
                    Color.clear
                        .onAppear { narrow = proxy.size.width < Self.threshold }
                        .onChange(of: proxy.size.width) { _, width in narrow = width < Self.threshold }
                }
            }
    }
}

extension View {
    func measuringNarrow() -> some View { modifier(DictationNarrowReader()) }
}

struct DictationDivider: View {
    var body: some View {
        Rectangle().fill(.brandHairline).frame(height: 1).accessibilityHidden(true)
    }
}

/// A line of secondary text under a control: a footnote, an advisory.
struct DictationFootnote: View {
    let text: String
    var role: DictationColorRole = .ink2

    init(_ text: String, role: DictationColorRole = .ink2) {
        self.text = text
        self.role = role
    }

    var body: some View {
        Text(text)
            .font(DictationType.callout)
            .foregroundStyle(DictationColor(role))
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// A notice with no box: a glyph in its tone, the sentence, and the action
/// that answers it.
struct DictationNotice<Actions: View>: View {
    let symbol: String
    var tone: DictationColorRole = .ink2
    let text: String
    @ViewBuilder var actions: Actions

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(DictationColor(tone))
                .accessibilityHidden(true)
            Text(text)
                .font(DictationType.body)
                .foregroundStyle(.brandInk)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            actions
        }
        .accessibilityElement(children: .contain)
    }
}

extension DictationNotice where Actions == EmptyView {
    init(symbol: String, tone: DictationColorRole = .ink2, text: String) {
        self.init(symbol: symbol, tone: tone, text: text) { EmptyView() }
    }
}

/// A pane with nothing to list: the mark (or a symbol), what is going on,
/// and what to do about it, centred in the pane.
struct DictationEmptyState<Actions: View>: View {
    var symbol: String? = nil
    let title: String
    let detail: String
    @ViewBuilder var actions: Actions

    var body: some View {
        VStack(spacing: 12) {
            if let symbol {
                Image(systemName: symbol)
                    .font(.system(size: 26, weight: .medium))
                    .foregroundStyle(.brandWheat)
                    .accessibilityHidden(true)
            } else {
                DictationMark(size: 44)
            }
            Text(title)
                .font(DictationType.sectionTitle)
                .foregroundStyle(.brandInk)
                .accessibilityAddTraits(.isHeader)
            Text(detail)
                .font(DictationType.body)
                .foregroundStyle(.brandInk2)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 360)
            actions.padding(.top, 4)
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

extension DictationEmptyState where Actions == EmptyView {
    init(symbol: String? = nil, title: String, detail: String) {
        self.init(symbol: symbol, title: title, detail: detail) { EmptyView() }
    }
}

/// The dot beside a status. The one glow on a page belongs to the live
/// state; everything else is flat.
struct DictationStatusDot: View {
    let role: DictationColorRole
    var glows = false
    @Environment(\.self) private var environment

    var body: some View {
        Circle()
            .fill(DictationColor(role))
            .frame(width: 8, height: 8)
            .shadow(color: glows ? DictationColor(role).color(in: environment).opacity(0.6) : .clear, radius: 5)
            .accessibilityHidden(true)
    }
}

/// A short label on a gold wash: "Copied", "Learned".
struct DictationChip: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(.brandWheatInk)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(.brandWheatWash, in: RoundedRectangle(cornerRadius: DictationMetrics.chipRadius, style: .continuous))
    }
}

// ─── Controls ───

/// The window's buttons: a gold primary for the one next step, a white
/// secondary, a quiet gold-text button, and a destructive one in red text.
/// Rounded rectangles, never capsules. The focus ring follows the shape.
struct DictationButtonStyle: ButtonStyle {
    enum Kind { case primary, secondary, quiet, destructive }
    var kind: Kind = .secondary
    var large = false

    func makeBody(configuration: Configuration) -> some View {
        DictationButtonBody(configuration: configuration, kind: kind, large: large)
    }
}

private struct DictationButtonBody: View {
    let configuration: ButtonStyle.Configuration
    let kind: DictationButtonStyle.Kind
    let large: Bool
    @Environment(\.isEnabled) private var enabled
    @State private var hovering = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: DictationMetrics.controlRadius, style: .continuous)
        configuration.label
            .font(.system(size: 13, weight: kind == .primary ? .semibold : .medium))
            .lineLimit(1)
            .padding(.horizontal, large ? 16 : (kind == .quiet ? 8 : 12))
            .frame(minHeight: large ? 32 : 28)
            .foregroundStyle(foreground)
            .background(fill, in: shape)
            .overlay(shape.strokeBorder(border, lineWidth: 1))
            .brightness(kind == .primary && configuration.isPressed ? -0.06 : 0)
            .opacity(enabled ? 1 : 0.45)
            .contentShape(shape)
            .contentShape(.focusEffect, shape)
            .onHover { hovering = enabled && $0 }
    }

    private var foreground: AnyShapeStyle {
        switch kind {
        case .primary: return AnyShapeStyle(.brandOnWheat)
        case .secondary: return AnyShapeStyle(.brandInk)
        case .quiet: return AnyShapeStyle(.brandWheatInk)
        case .destructive: return AnyShapeStyle(.brandDanger)
        }
    }

    private var fill: AnyShapeStyle {
        let active = hovering || configuration.isPressed
        switch kind {
        case .primary: return AnyShapeStyle(.brandWheat)
        case .secondary, .destructive:
            return configuration.isPressed ? AnyShapeStyle(.brandRaised) : AnyShapeStyle(.brandSurface)
        case .quiet: return active ? AnyShapeStyle(.brandRaised) : AnyShapeStyle(Color.clear)
        }
    }

    private var border: AnyShapeStyle {
        switch kind {
        case .primary, .quiet: return AnyShapeStyle(Color.clear)
        case .secondary, .destructive: return AnyShapeStyle(.brandHairline)
        }
    }
}

extension ButtonStyle where Self == DictationButtonStyle {
    static func dictation(_ kind: DictationButtonStyle.Kind, large: Bool = false) -> DictationButtonStyle {
        DictationButtonStyle(kind: kind, large: large)
    }
}

/// A row's small icon action (Play, Copy): no frame until the pointer is
/// over it, ink that strengthens on hover.
struct DictationIconButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        DictationIconButtonBody(configuration: configuration)
    }
}

private struct DictationIconButtonBody: View {
    let configuration: ButtonStyle.Configuration
    @Environment(\.isEnabled) private var enabled
    @State private var hovering = false

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: DictationMetrics.controlRadius, style: .continuous)
        configuration.label
            .font(.system(size: 13, weight: .medium))
            .foregroundStyle(hovering ? AnyShapeStyle(.brandInk) : AnyShapeStyle(.brandInk2))
            .frame(width: 28, height: 28)
            .background(hovering || configuration.isPressed ? AnyShapeStyle(.brandRaised) : AnyShapeStyle(Color.clear),
                        in: shape)
            .opacity(enabled ? 1 : 0.4)
            .contentShape(shape)
            .contentShape(.focusEffect, shape)
            .onHover { hovering = enabled && $0 }
    }
}

extension ButtonStyle where Self == DictationIconButtonStyle {
    static var dictationIcon: DictationIconButtonStyle { DictationIconButtonStyle() }
}

/// A choice from a short list, drawn as the window's secondary button with
/// the current value and a chevron; the list itself is the native menu, so
/// the keyboard and VoiceOver work as they do for any pop-up.
struct DictationChoice<Value: Hashable, Options: View, Actions: View>: View {
    let title: String
    let current: String
    @Binding var selection: Value
    @ViewBuilder var options: Options
    /// Menu items under the choices, after a separator: what to do when
    /// none of the choices is the one wanted.
    @ViewBuilder var actions: Actions

    var body: some View {
        Menu {
            Picker(title, selection: $selection) { options }
                .pickerStyle(.inline)
                .labelsHidden()
            if Actions.self != EmptyView.self {
                Divider()
                actions
            }
        } label: {
            HStack(spacing: 6) {
                Text(current)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(.brandInk2)
            }
        }
        .menuStyle(.button)
        .menuIndicator(.hidden)
        .buttonStyle(.dictation(.secondary))
        .fixedSize()
        .accessibilityLabel(title)
        .accessibilityValue(current)
    }
}

extension DictationChoice where Actions == EmptyView {
    init(title: String, current: String, selection: Binding<Value>, @ViewBuilder options: () -> Options) {
        self.init(title: title, current: current, selection: selection, options: options, actions: { EmptyView() })
    }
}

/// A text field drawn on the surface with a hairline that turns gold while
/// it has focus. The field is a plain native one; the caller owns the
/// focus state.
struct DictationFieldFrame: ViewModifier {
    var focused: Bool

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: DictationMetrics.controlRadius, style: .continuous)
        // No foreground style: it would paint the placeholder as if it were
        // typed text. The system's text and placeholder colours already
        // follow the appearance.
        content
            .textFieldStyle(.plain)
            .font(DictationType.body)
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(.brandSurface, in: shape)
            .overlay(shape.strokeBorder(focused ? AnyShapeStyle(.brandWheatStrong) : AnyShapeStyle(.brandHairline),
                                        lineWidth: focused ? 1.5 : 1))
    }
}

extension View {
    func dictationField(focused: Bool) -> some View { modifier(DictationFieldFrame(focused: focused)) }
}

/// The window's disclosure: the whole title row is the button, its chevron
/// turns, and the content appears under it. Reduce Motion drops the turn.
struct DictationDisclosureStyle: DisclosureGroupStyle {
    func makeBody(configuration: Configuration) -> some View {
        DictationDisclosureBody(configuration: configuration)
    }
}

private struct DictationDisclosureBody: View {
    let configuration: DisclosureGroupStyle.Configuration
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button {
                if reduceMotion {
                    configuration.isExpanded.toggle()
                } else {
                    withAnimation(.easeOut(duration: 0.15)) { configuration.isExpanded.toggle() }
                }
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .bold))
                        .foregroundStyle(.brandInk2)
                        .rotationEffect(.degrees(configuration.isExpanded ? 90 : 0))
                    configuration.label
                        .font(DictationType.bodyEmphasis)
                        .foregroundStyle(.brandInk)
                    Spacer(minLength: 0)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityValue(configuration.isExpanded ? "Expanded" : "Collapsed")
            if configuration.isExpanded {
                configuration.content
                    .padding(.leading, 18)
            }
        }
    }
}

extension DisclosureGroupStyle where Self == DictationDisclosureStyle {
    static var dictation: DictationDisclosureStyle { DictationDisclosureStyle() }
}

/// The window's switch: a gold track when on, a raised one when off, and
/// a white knob, all rounded rectangles like every other control. Drawn
/// here so it reads the same whatever accent colour the user has chosen
/// and whether or not the window is active; VoiceOver and the keyboard get
/// a native toggle (`accessibilityRepresentation`), and Space flips it.
struct DictationSwitchStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        DictationSwitch(configuration: configuration)
    }
}

private struct DictationSwitch: View {
    let configuration: ToggleStyle.Configuration
    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let track = RoundedRectangle(cornerRadius: 7, style: .continuous)
        Button {
            if reduceMotion {
                configuration.isOn.toggle()
            } else {
                withAnimation(.easeOut(duration: 0.15)) { configuration.isOn.toggle() }
            }
        } label: {
            ZStack(alignment: configuration.isOn ? .trailing : .leading) {
                track.fill(configuration.isOn ? AnyShapeStyle(.brandWheat) : AnyShapeStyle(.brandRaised))
                track.strokeBorder(configuration.isOn ? AnyShapeStyle(Color.clear) : AnyShapeStyle(.brandHairline),
                                   lineWidth: 1)
                RoundedRectangle(cornerRadius: 5, style: .continuous)
                    .fill(Color.white)
                    .shadow(color: .black.opacity(0.18), radius: 1, y: 0.5)
                    .frame(width: 16, height: 16)
                    .padding(2)
            }
            .frame(width: 36, height: 20)
            .opacity(enabled ? 1 : 0.45)
            .contentShape(track)
            .contentShape(.focusEffect, track)
        }
        .buttonStyle(.plain)
        .accessibilityRepresentation {
            Toggle(isOn: configuration.$isOn) { configuration.label }
        }
    }
}

extension ToggleStyle where Self == DictationSwitchStyle {
    static var dictation: DictationSwitchStyle { DictationSwitchStyle() }
}

/// The hold key drawn as a key: a face on the surface, a hairline edge and
/// a 3-point base under it, so it reads as a key and not as a button.
struct DictationKeycap: View {
    let label: String
    var large = true

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 9, style: .continuous)
        Text(label)
            .font(.system(size: large ? 17 : 14, weight: .semibold, design: .rounded))
            .foregroundStyle(.brandInk)
            .lineLimit(1)
            .fixedSize()
            .padding(.horizontal, large ? 16 : 12)
            .frame(minWidth: large ? 92 : 64, minHeight: large ? 46 : 34)
            .background(shape.fill(.brandSurface))
            .overlay(shape.strokeBorder(.brandHairline, lineWidth: 1))
            .background(shape.fill(.brandHairline).offset(y: 3))
            .padding(.bottom, 3)
            .accessibilityHidden(true)
    }
}
