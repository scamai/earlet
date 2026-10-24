// The text inserted is formatted the way Windows formats it: the first
// letter of each sentence capitalised (unless the word already has a capital
// after its first letter, as iPhone does) and a trailing ellipsis dropped, and
// nothing changed in a terminal. The rules are `DictationFormatter`'s
// shipped pair; there is no model involved.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@MainActor
@Suite("Inserted text is formatted like Windows")
struct FormattingTests {

    private func dictate(_ reply: String, into app: String?) async -> Harness {
        let harness = Harness()
        harness.targetApplication.mutate { $0 = app }
        let model = harness.model()
        await model.toggle()
        harness.workers.made[0].state.mutate { $0.replies = [.text(reply)] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        return harness
    }

    @Test("sentences start with a capital and a trailing ellipsis is dropped")
    func sentencesAreFormatted() async {
        let harness = await dictate("hello there. how are you...", into: "com.apple.TextEdit")
        #expect(harness.inserts.texts.get == ["Hello there. How are you"])
    }

    @Test("an unknown target gets the same rules")
    func unknownTargetIsFormatted() async {
        let harness = await dictate("ship it...", into: nil)
        #expect(harness.inserts.texts.get == ["Ship it"])
    }

    @Test("a terminal gets the words exactly as recognized")
    func terminalIsLeftAlone() async {
        let terminals = [
            "com.apple.Terminal", "com.googlecode.iterm2", "com.mitchellh.ghostty",
            // Warp, kitty, Alacritty, WezTerm, Hyper.
            "dev.warp.Warp-Stable", "net.kovidgoyal.kitty", "org.alacritty", "com.github.wez.wezterm",
            "co.zeit.hyper",
        ]
        for terminal in terminals {
            let harness = await dictate("git status...", into: terminal)
            #expect(harness.inserts.texts.get == ["git status..."], "\(terminal)")
        }
    }

    @Test("a word with a capital after its first letter keeps its casing where the text is pasted")
    func innerCapitalIsKept() async {
        let harness = await dictate("iPhone battery died. macOS too...", into: "com.microsoft.VSCode")
        #expect(harness.inserts.texts.get == ["iPhone battery died. macOS too"])
    }

    @Test("a Chinese sentence ends in the full-width stop and its comma goes full-width")
    func chineseSentenceIsPunctuated() async {
        let harness = await dictate("我们下午三点开会,别忘了", into: "com.apple.TextEdit")
        #expect(harness.inserts.texts.get == ["我们下午三点开会，别忘了。"])
    }

    @Test("a terminal gets Chinese text exactly as recognized")
    func terminalLeavesChineseAlone() async {
        let harness = await dictate("我们下午三点开会", into: "com.apple.Terminal")
        #expect(harness.inserts.texts.get == ["我们下午三点开会"])
    }

    @Test("an English chat message still gets no terminal period")
    func englishStaysBare() async {
        let harness = await dictate("wait, what do you think", into: "com.tinyspeck.slackmacgap")
        #expect(harness.inserts.texts.get == ["Wait, what do you think"])
    }

    @Test("the rules give the answers the Windows formatter's tests pin")
    func sameAnswersAsWindows() {
        // Inputs and outputs copied from
        // windows/Dictation.Services.Tests/DictationTextFormatterTests.cs.
        let cases: [(String, String)] = [
            ("one thing. then another! and a third? yes.", "One thing. Then another! And a third? Yes."),
            ("revenue is 3.14 percent of the total", "Revenue is 3.14 percent of the total"),
            ("istanbul is a city", "Istanbul is a city"),
            ("So I was thinking...", "So I was thinking"),
            ("So I was thinking ...", "So I was thinking"),
            ("Wait......", "Wait"),
            ("....", "...."),
            ("So I was thinking\u{2026}", "So I was thinking\u{2026}"),
            ("Nothing to do here.", "Nothing to do here."),
            // A word with a capital after its first letter keeps its casing.
            ("iOS build is failing", "iOS build is failing"),
            ("iPhone battery died", "iPhone battery died"),
            ("macOS update. iOS too", "macOS update. iOS too"),
            ("eBay order shipped", "eBay order shipped"),
            ("I bought an iPhone. iPhone is great", "I bought an iPhone. iPhone is great"),
            ("iPhone's battery died", "iPhone's battery died"),
            ("so. iPhone\u{2019}s screen", "So. iPhone\u{2019}s screen"),
            // Only a capital inside the word itself counts.
            ("iphone battery died", "Iphone battery died"),
            ("ios build. macos too", "Ios build. Macos too"),
            ("the iPhone battery died", "The iPhone battery died"),
            ("e-Commerce is growing", "E-Commerce is growing"),
            // v5: the Han-script repairs (mirrors of the CJK cases in
            // DictationTextFormatterTests.cs). A Chinese sentence ends in 。
            // and its Han-bracketed separators go full-width; English and
            // Korean are byte-identical apart from capitalization.
            ("我们下午三点开会", "我们下午三点开会。"),
            ("请帮我打开一下Slack", "请帮我打开一下Slack。"),
            ("我买了3", "我买了3。"),
            ("已经好了。", "已经好了。"),
            ("真的吗？", "真的吗？"),
            ("现在我这网络状态不是很好,豆包在那里罢工", "现在我这网络状态不是很好，豆包在那里罢工。"),
            ("好的,你现在已经知道我的要求,你可以开始了", "好的，你现在已经知道我的要求，你可以开始了。"),
            ("你知道吗?", "你知道吗？"),
            ("用 Slack, Teams 开会", "用 Slack, Teams 开会。"),
            ("我买了3,000个", "我买了3,000个。"),
            ("안녕하세요", "안녕하세요"),
            ("测试一下 we also work with Asteron and we are comparing ourselves against Fable",
             "测试一下 we also work with Asteron and we are comparing ourselves against Fable"),
            ("wait, what do you think", "Wait, what do you think"),
            ("我们已经跑通了。 next step is the demo", "我们已经跑通了。 Next step is the demo"),
            (" 会议 ", " 会议。 "),
        ]
        for (input, expected) in cases {
            #expect(DictationFormatter.format(input, forApp: nil) == expected, "\(input)")
        }
    }
}
#endif
