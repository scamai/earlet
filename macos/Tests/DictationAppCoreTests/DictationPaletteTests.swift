// The window's palette, held to WCAG contrast in all four appearances
// (light, dark, and each under Increase Contrast): words on every ground at
// 4.5:1, a state drawn without words at 3:1, the primary button's label on
// its gold, and Increase Contrast never weaker than the standard palette.

#if canImport(Testing)
import Testing
@testable import DictationAppCore

@Suite struct DictationPaletteTests {
    @Test func wordsReadOnEveryGround() {
        for appearance in DictationAppearance.all {
            for role in DictationColorRole.text {
                for ground in DictationColorRole.grounds {
                    let ratio = contrast(role, on: ground, appearance)
                    #expect(ratio >= 4.5, "\(role) on \(ground), \(appearance): \(ratio)")
                }
            }
        }
    }

    @Test func statesWithoutWordsStandOut() {
        for appearance in DictationAppearance.all {
            for role in DictationColorRole.marks {
                for ground in DictationColorRole.grounds {
                    let ratio = contrast(role, on: ground, appearance)
                    #expect(ratio >= 3, "\(role) on \(ground), \(appearance): \(ratio)")
                }
            }
        }
    }

    @Test func thePrimaryButtonsLabelReads() {
        for appearance in DictationAppearance.all {
            let ratio = contrast(.onWheat, on: .wheat, appearance)
            #expect(ratio >= 4.5, "\(appearance): \(ratio)")
        }
    }

    @Test func increaseContrastNeverWeakens() {
        for dark in [false, true] {
            let standard = DictationAppearance(dark: dark)
            let increased = DictationAppearance(dark: dark, increasedContrast: true)
            for role in DictationColorRole.text + DictationColorRole.marks {
                for ground in DictationColorRole.grounds {
                    #expect(contrast(role, on: ground, increased) >= contrast(role, on: ground, standard) - 0.001,
                            "\(role) on \(ground), dark: \(dark)")
                }
            }
            // The edges get stronger: they are what Increase Contrast is for.
            #expect(contrast(.hairline, on: .ground, increased) > contrast(.hairline, on: .ground, standard))
        }
    }

    /// The ratio itself, against values worked by hand: black on white is
    /// 21, and a colour against itself is 1.
    @Test func theRatioIsWCAGs() {
        let black = DictationRGB(0x000000), white = DictationRGB(0xFFFFFF)
        #expect(abs(black.contrast(with: white) - 21) < 0.001)
        #expect(abs(white.contrast(with: black) - 21) < 0.001)
        #expect(abs(DictationRGB(0x777777).contrast(with: DictationRGB(0x777777)) - 1) < 0.001)
        // #767676 on white is the classic 4.54:1.
        #expect(abs(DictationRGB(0x767676).contrast(with: white) - 4.54) < 0.01)
        #expect(DictationRGB(0xE0A11B).description == "#E0A11B")
    }

    private func contrast(_ role: DictationColorRole, on ground: DictationColorRole,
                          _ appearance: DictationAppearance) -> Double {
        DictationPalette.rgb(role, in: appearance).contrast(with: DictationPalette.rgb(ground, in: appearance))
    }
}
#endif
