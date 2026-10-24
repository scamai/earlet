import SwiftUI

// The window's palette: a warm paper ground, ink text and wheat gold, after
// the 小麦 idea (a little mic, and wheat) behind the product's mark. Light,
// dark and Increase Contrast each have their own values, and every text
// colour is held to WCAG contrast against every ground it sits on
// (`DictationPaletteTests`). The components that draw with it are in the app
// target (DictationDesign.swift).

/// One opaque colour, as sRGB components from 0 to 1.
public struct DictationRGB: Equatable, Sendable, CustomStringConvertible {
    public let red: Double
    public let green: Double
    public let blue: Double

    /// From a 0xRRGGBB literal.
    public init(_ hex: UInt32) {
        red = Double((hex >> 16) & 0xFF) / 255
        green = Double((hex >> 8) & 0xFF) / 255
        blue = Double(hex & 0xFF) / 255
    }

    /// WCAG 2.x relative luminance.
    public var luminance: Double {
        func linear(_ channel: Double) -> Double {
            channel <= 0.04045 ? channel / 12.92 : pow((channel + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue)
    }

    /// WCAG 2.x contrast ratio, from 1 to 21.
    public func contrast(with other: DictationRGB) -> Double {
        let (a, b) = (luminance, other.luminance)
        return (max(a, b) + 0.05) / (min(a, b) + 0.05)
    }

    public var color: Color { Color(.sRGB, red: red, green: green, blue: blue) }

    public var description: String {
        let value = [red, green, blue].map { Int(($0 * 255).rounded()) }
            .reduce(0) { $0 << 8 | $1 }
        return "#" + String(value, radix: 16, uppercase: true).leftPadded(to: 6)
    }
}

/// What a colour is for. The tests read these groups: a text role must
/// reach 4.5:1 (7:1 under Increase Contrast) on every ground role, and a
/// mark role, which carries a state but no words, 3:1.
public enum DictationColorRole: String, CaseIterable, Sendable {
    /// The window behind everything.
    case ground
    /// A card or a field.
    case surface
    /// A hover or pressed fill, and the keycap's base.
    case raised
    /// Dividers and card edges.
    case hairline
    /// Text, from the most to the least prominent. `ink3` is for hints and
    /// placeholders, never for something the user must read to proceed.
    case ink, ink2, ink3
    /// The mark, the live bars, a switch's track and the selected pane's
    /// glyph. Never words: it is too light for them on the light ground.
    /// Where it shows a state, something else shows it too (the knob's
    /// side, the pane's name in full ink), and Increase Contrast deepens it.
    case wheat
    /// Gold that carries a state on its own: a field's focus edge, the dot
    /// while the words are on their way.
    case wheatStrong
    /// Gold words: a quiet button, a count.
    case wheatInk
    /// A quiet gold fill behind a short label.
    case wheatWash
    /// Words on a wheat fill (the primary button).
    case onWheat
    /// On and ready, and a granted permission.
    case live
    /// The microphone is open.
    case recording
    /// Something only the user can finish (a missing grant).
    case caution
    /// An action that deletes.
    case danger

    /// Roles that carry words.
    public static let text: [DictationColorRole] = [.ink, .ink2, .wheatInk, .caution, .danger]
    /// Roles that carry a state without words.
    public static let marks: [DictationColorRole] = [.ink3, .wheatStrong, .live, .recording]
    /// Roles that words and marks are drawn on.
    public static let grounds: [DictationColorRole] = [.ground, .surface, .raised, .wheatWash]
}

/// Which palette applies: the appearance and the Increase Contrast setting.
public struct DictationAppearance: Hashable, Sendable {
    public var dark: Bool
    public var increasedContrast: Bool

    public init(dark: Bool, increasedContrast: Bool = false) {
        self.dark = dark
        self.increasedContrast = increasedContrast
    }

    public init(_ environment: EnvironmentValues) {
        self.init(dark: environment.colorScheme == .dark,
                  increasedContrast: environment.colorSchemeContrast == .increased)
    }

    public static let all: [DictationAppearance] = [false, true].flatMap { dark in
        [false, true].map { DictationAppearance(dark: dark, increasedContrast: $0) }
    }
}

public enum DictationPalette {
    public static func rgb(_ role: DictationColorRole, in appearance: DictationAppearance) -> DictationRGB {
        if appearance.increasedContrast,
           let stronger = (appearance.dark ? darkIncreased : lightIncreased)[role] {
            return stronger
        }
        return (appearance.dark ? dark : light)[role]!
    }

    private static let light: [DictationColorRole: DictationRGB] = [
        .ground: DictationRGB(0xFBFAF6), .surface: DictationRGB(0xFFFFFF), .raised: DictationRGB(0xF3F0E8),
        .hairline: DictationRGB(0xE6E1D6),
        .ink: DictationRGB(0x1D1A15), .ink2: DictationRGB(0x5F594E), .ink3: DictationRGB(0x8A8375),
        .wheat: DictationRGB(0xE0A11B), .wheatStrong: DictationRGB(0xB97F0A), .wheatInk: DictationRGB(0x875A06),
        .wheatWash: DictationRGB(0xFAEFD3), .onWheat: DictationRGB(0x1D1A15),
        .live: DictationRGB(0x1E8A4C), .recording: DictationRGB(0xD13B2C),
        .caution: DictationRGB(0xB4470F), .danger: DictationRGB(0xB42318),
    ]

    private static let dark: [DictationColorRole: DictationRGB] = [
        .ground: DictationRGB(0x15140F), .surface: DictationRGB(0x1E1C17), .raised: DictationRGB(0x27241D),
        .hairline: DictationRGB(0x332F27),
        .ink: DictationRGB(0xF3EFE5), .ink2: DictationRGB(0xADA595), .ink3: DictationRGB(0x837C6D),
        .wheat: DictationRGB(0xF0B73A), .wheatStrong: DictationRGB(0xF0B73A), .wheatInk: DictationRGB(0xF4C661),
        .wheatWash: DictationRGB(0x3A2D10), .onWheat: DictationRGB(0x15140F),
        .live: DictationRGB(0x3FCB73), .recording: DictationRGB(0xFF5B4D),
        .caution: DictationRGB(0xF2905F), .danger: DictationRGB(0xFF7B6E),
    ]

    /// Increase Contrast: stronger secondary text, hints and edges, and on
    /// the light ground a deeper gold; the grounds stay.
    private static let lightIncreased: [DictationColorRole: DictationRGB] = [
        .wheat: DictationRGB(0xB97F0A),
        .hairline: DictationRGB(0xACA594), .ink2: DictationRGB(0x4A453C), .ink3: DictationRGB(0x6B6559),
        .wheatStrong: DictationRGB(0x9A6A05), .wheatInk: DictationRGB(0x6E4900),
        .caution: DictationRGB(0x963A0B), .danger: DictationRGB(0x961D13), .live: DictationRGB(0x17703D),
        .recording: DictationRGB(0xB02F22),
    ]

    private static let darkIncreased: [DictationColorRole: DictationRGB] = [
        .hairline: DictationRGB(0x5E584C), .ink2: DictationRGB(0xCFC8B9), .ink3: DictationRGB(0xA39B8B),
        .caution: DictationRGB(0xF7AE88), .danger: DictationRGB(0xFF9D93),
    ]
}

/// A palette role as a SwiftUI style, resolved from the view's environment,
/// so a view follows light, dark and Increase Contrast with nothing to
/// observe: `.foregroundStyle(.brandInk2)`.
public struct DictationColor: ShapeStyle, Sendable {
    public let role: DictationColorRole

    public init(_ role: DictationColorRole) { self.role = role }

    public func resolve(in environment: EnvironmentValues) -> Color {
        DictationPalette.rgb(role, in: DictationAppearance(environment)).color
    }

    /// For the few APIs that take a `Color` rather than a style (a shadow).
    public func color(in environment: EnvironmentValues) -> Color { resolve(in: environment) }
}

extension ShapeStyle where Self == DictationColor {
    public static var brandGround: DictationColor { DictationColor(.ground) }
    public static var brandSurface: DictationColor { DictationColor(.surface) }
    public static var brandRaised: DictationColor { DictationColor(.raised) }
    public static var brandHairline: DictationColor { DictationColor(.hairline) }
    public static var brandInk: DictationColor { DictationColor(.ink) }
    public static var brandInk2: DictationColor { DictationColor(.ink2) }
    public static var brandInk3: DictationColor { DictationColor(.ink3) }
    public static var brandWheat: DictationColor { DictationColor(.wheat) }
    public static var brandWheatStrong: DictationColor { DictationColor(.wheatStrong) }
    public static var brandWheatInk: DictationColor { DictationColor(.wheatInk) }
    public static var brandWheatWash: DictationColor { DictationColor(.wheatWash) }
    public static var brandOnWheat: DictationColor { DictationColor(.onWheat) }
    public static var brandLive: DictationColor { DictationColor(.live) }
    public static var brandRecording: DictationColor { DictationColor(.recording) }
    public static var brandCaution: DictationColor { DictationColor(.caution) }
    public static var brandDanger: DictationColor { DictationColor(.danger) }
}

private extension String {
    func leftPadded(to length: Int) -> String {
        count >= length ? self : String(repeating: "0", count: length - count) + self
    }
}
