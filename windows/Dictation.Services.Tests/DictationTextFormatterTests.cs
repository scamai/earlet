using Dictation.Services;

namespace Dictation.Services.Tests;

/// <summary>
/// The two shipped text rules, pinned against the macOS implementation they
/// were ported from.
///
/// <para>
/// The cases below are not invented. Each one pins a specific decision recorded
/// in <c>DictationFormatter.swift</c>'s doc comments and in the measurement
/// behind them — the decimal-point guard, the three-dot minimum, the
/// all-dots refusal, the preserved trailing whitespace. A port that "works on
/// the happy path" and drops one of those guards is a port that starts eating
/// sentence periods and decimals in real prose.
/// </para>
///
/// <para>
/// It also pins what the formatter must NOT do. Five rules exist on macOS and
/// are deliberately unshipped because per-utterance measurement showed each of
/// them making good output worse; the "leaves alone" cases below are what stops
/// somebody adding one back on the strength of a plausible-sounding argument.
/// </para>
/// </summary>
public class DictationTextFormatterTests
{
    // ── The default is no change ──────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("Already a fine sentence.")]
    [InlineData("Two sentences. And the second one.")]
    [InlineData("Git commit -m fix the thing")]
    public void Text_that_needs_nothing_comes_back_identical(string input)
    {
        // 38.8% of real dictations are byte-identical after formatting. That is
        // the cheapest win available and it is the default here: neither rule's
        // precondition is met, so nothing runs.
        Assert.Equal(input, DictationTextFormatter.Format(input));
        Assert.False(DictationTextFormatter.WouldChange(input));
    }

    [Fact]
    public void An_unchanged_string_is_the_very_same_instance()
    {
        // Not a correctness requirement, an allocation one — and asserting it
        // is what keeps the no-op path a no-op. A formatter that rebuilt every
        // string would still pass every other test in this file.
        var input = "Nothing to do here.";

        Assert.Same(input, DictationTextFormatter.Format(input));
    }

    // ── Capitalisation ────────────────────────────────────────────────────

    [Fact]
    public void The_first_letter_of_the_utterance_is_capitalised()
    {
        Assert.Equal(
            "The quarterly numbers are ready",
            DictationTextFormatter.Format("the quarterly numbers are ready"));
    }

    [Fact]
    public void A_letter_after_a_sentence_mark_is_capitalised()
    {
        Assert.Equal(
            "One thing. Then another! And a third? Yes.",
            DictationTextFormatter.Format("one thing. then another! and a third? yes."));
    }

    [Fact]
    public void Leading_whitespace_and_punctuation_do_not_consume_the_capital()
    {
        // capitalizeNext survives every non-letter, so the first LETTER gets it
        // however much punctuation or space precedes it.
        Assert.Equal("  \"Hello\"", DictationTextFormatter.Format("  \"hello\""));
    }

    [Fact]
    public void A_decimal_point_is_not_a_sentence_boundary()
    {
        // The whitespace-after requirement exists for exactly this. A '.'
        // between two digits is never followed by whitespace, so "3.14 percent"
        // cannot be misread as two sentences.
        Assert.Equal(
            "Revenue is 3.14 percent of the total",
            DictationTextFormatter.Format("revenue is 3.14 percent of the total"));
    }

    [Fact]
    public void An_abbreviation_is_a_known_and_accepted_limitation()
    {
        // Carried over from macOS verbatim, WITH its measurement: not
        // special-casing "e.g." cost 3 regressions in 5,590 reference no-op
        // utterances. Pinned rather than left implicit so that a future reader
        // who notices it recognises a decision instead of a bug.
        Assert.Equal(
            "See e.g. The manual",
            DictationTextFormatter.Format("see e.g. the manual"));
    }

    [Fact]
    public void Capitalisation_is_culture_invariant()
    {
        // char.ToUpperInvariant, never the current culture. On a machine with a
        // Turkish locale a culture-sensitive uppercase turns a dictated "in"
        // into "İn" — right almost everywhere and silently wrong for some
        // users. Runs under the ambient culture, so this asserts the OUTPUT
        // rather than trying to install a locale.
        Assert.Equal("Istanbul is a city", DictationTextFormatter.Format("istanbul is a city"));
    }

    [Fact]
    public void Text_that_is_already_capitalised_is_untouched()
    {
        var input = "The quarterly numbers are ready.";

        Assert.Same(input, DictationTextFormatter.Format(input));
    }

    [Theory]
    [InlineData("iOS build is failing", "iOS build is failing")]
    [InlineData("iPhone battery died", "iPhone battery died")]
    [InlineData("macOS update. iOS too", "macOS update. iOS too")]
    [InlineData("eBay order shipped", "eBay order shipped")]
    [InlineData("I bought an iPhone. iPhone is great", "I bought an iPhone. iPhone is great")]
    // A possessive: the capital comes before the apostrophe, straight or curly.
    [InlineData("iPhone's battery died", "iPhone's battery died")]
    [InlineData("so. iPhone\u2019s screen", "So. iPhone\u2019s screen")]
    public void A_word_with_a_capital_after_its_first_letter_keeps_its_casing(string input, string expected)
    {
        // "IPhone" is not a sentence start fixed, it is a name broken. The
        // worker restores a taught word's declared casing (LEARNING.md §6),
        // and this rule must not break it again.
        Assert.Equal(expected, DictationTextFormatter.Format(input));
    }

    [Fact]
    public void A_word_that_keeps_its_casing_costs_no_allocation()
    {
        var input = "iPhone battery died";

        Assert.Same(input, DictationTextFormatter.Format(input));
    }

    [Theory]
    // Only a capital INSIDE the word counts: the same words written all in
    // lower case are capitalised as any other sentence start.
    [InlineData("iphone battery died", "Iphone battery died")]
    [InlineData("ios build. macos too", "Ios build. Macos too")]
    // A capital in a LATER word does not stop the first one.
    [InlineData("the iPhone battery died", "The iPhone battery died")]
    // Nor does one after a word boundary: a hyphen ends the word.
    [InlineData("e-Commerce is growing", "E-Commerce is growing")]
    public void Only_a_capital_inside_the_word_itself_keeps_it_as_it_is(string input, string expected)
    {
        Assert.Equal(expected, DictationTextFormatter.Format(input));
    }

    // ── Trailing ellipsis ─────────────────────────────────────────────────

    [Fact]
    public void A_trailing_ellipsis_and_the_space_before_it_are_removed()
    {
        Assert.Equal("So I was thinking",
            DictationTextFormatter.Format("So I was thinking..."));
        Assert.Equal("So I was thinking",
            DictationTextFormatter.Format("So I was thinking ..."));
    }

    [Fact]
    public void More_than_three_dots_are_all_removed()
    {
        Assert.Equal("Wait", DictationTextFormatter.Format("Wait......"));
    }

    [Fact]
    public void A_shell_command_is_capitalised_and_that_is_the_measured_behaviour()
    {
        // Not a defect and not an oversight. This corpus is 83% terminal, and
        // capitalisation was measured on it: 3 regressions in 5,590 reference
        // no-op utterances (0.05%). The rule that WOULD have hurt here — adding
        // a terminal period to a shell fragment — is one of the five that were
        // measured and rejected, and the theory at the bottom of this file
        // guards it.
        Assert.Equal("Git push origin main",
            DictationTextFormatter.Format("git push origin main"));
    }

    [Fact]
    public void A_sentence_period_survives()
    {
        // The three-dot minimum. This is the guard that stops the rule eating
        // the end of every ordinary sentence.
        var input = "That is all.";

        Assert.Same(input, DictationTextFormatter.Format(input));
    }

    [Fact]
    public void A_double_dot_survives()
    {
        // Two dots is not an ellipsis; it is usually a typo or an abbreviation
        // colliding with a full stop, and neither is ours to correct.
        var input = "That is all..";

        Assert.Same(input, DictationTextFormatter.Format(input));
    }

    [Fact]
    public void A_mid_text_ellipsis_survives()
    {
        // The rule alone. Format would ALSO capitalise the word after the dots
        // — a mid-text "..." followed by a space is a sentence boundary to the
        // capitaliser, exactly as it is on macOS — and that would obscure what
        // this case is about, which is that the dots themselves stay.
        var input = "I said... and then I stopped.";

        Assert.Same(input, DictationTextFormatter.StripTrailingEllipsis(input));
        Assert.Equal("I said... And then I stopped.", DictationTextFormatter.Format(input));
    }

    [Fact]
    public void An_utterance_that_is_only_dots_is_left_exactly_as_it_is()
    {
        // The "at least one letter or digit survives" guard. Without it this
        // rule strips the whole utterance to nothing, which turns a strange
        // transcript into a dictation that silently typed nothing at all.
        var input = "....";

        Assert.Same(input, DictationTextFormatter.StripTrailingEllipsis(input));
    }

    [Fact]
    public void Whitespace_after_the_ellipsis_is_preserved_byte_for_byte()
    {
        // A user dictating into a field where the trailing space matters does
        // not lose it to a cosmetic rule.
        // The rule alone, not Format — the capitaliser would obscure what is
        // being asserted here, which is only what happens to the whitespace.
        Assert.Equal("hello ", DictationTextFormatter.StripTrailingEllipsis("hello... "));
        Assert.Equal("hello\n", DictationTextFormatter.StripTrailingEllipsis("hello...\n"));
    }

    [Fact]
    public void The_unicode_ellipsis_is_left_alone()
    {
        // n=0 in the measured corpus, so it is not handled on a guess.
        var input = "So I was thinking…";

        Assert.Same(input, DictationTextFormatter.StripTrailingEllipsis(input));
    }

    // ── The two together ──────────────────────────────────────────────────

    [Fact]
    public void Both_rules_apply_to_one_utterance()
    {
        Assert.Equal("So I was thinking",
            DictationTextFormatter.Format("so I was thinking..."));
    }

    [Fact]
    public void The_two_rules_commute_today_and_the_fixed_order_is_what_keeps_them_so()
    {
        // Honest about what this asserts. Applying these two in EITHER order
        // gives the same answer for every input: nothing follows a trailing
        // ellipsis, so the capitalise-next flag the dots would set has no
        // letter left to act on. That is a property of THESE TWO rules, not a
        // property of the pipeline — Format pins strip-then-capitalise anyway,
        // because the first rule inserted between them is where an accidental
        // ordering dependency would otherwise appear silently.
        const string input = "one. then two...";

        var shipped = DictationTextFormatter.Format(input);
        var reversed = DictationTextFormatter.StripTrailingEllipsis(
            DictationTextFormatter.CapitalizeSentences(input));

        Assert.Equal("One. Then two", shipped);
        Assert.Equal(shipped, reversed);
    }

    [Fact]
    public void WouldChange_agrees_with_Format()
    {
        Assert.True(DictationTextFormatter.WouldChange("so I was thinking..."));
        Assert.False(DictationTextFormatter.WouldChange("So I was thinking"));
    }

    // ── The five rules that were deliberately NOT ported ──────────────────

    [Theory]
    // Sentence-initial disfluency. macOS implements deletion of a leading
    // "and"/"so" and does not apply it: most of the corpus's 587 "and →
    // deleted" alignments are not the utterance-initial case the rule targets.
    [InlineData("and then we shipped it", "And then we shipped it")]
    // Verbal "like". Five occurrences in the whole corpus, two of them
    // reference no-ops — too small to trust either way.
    [InlineData("it was like really fast", "It was like really fast")]
    // Grammatical agreement. is→are and are→is occur with roughly EQUAL
    // frequency, so the correct direction depends on the sentence, not on a
    // lookup table.
    [InlineData("the numbers is ready", "The numbers is ready")]
    // Spelled numbers. Of 171 matching utterances, the 33 the reference left
    // untouched were regressed 100% of the time.
    [InlineData("give me five minutes", "Give me five minutes")]
    // Terminal punctuation — the single largest regression source measured,
    // 1,709 of 2,084. This corpus is 83% terminal/iTerm2: shell commands and
    // commit-message fragments conventionally take no trailing period.
    [InlineData("git push origin main", "Git push origin main")]
    public void Rules_that_measurement_rejected_are_not_applied(
        string input, string expected)
    {
        // Every case here changes ONLY by capitalisation. If one of the five
        // unshipped rules is ever added to Format, its case fails — which is
        // the point: "a rule-based mechanism that is sometimes wrong makes good
        // output worse" is the finding this file guards, and it is easier to
        // argue for a rule than to re-run the measurement.
        Assert.Equal(expected, DictationTextFormatter.Format(input));
    }

    [Fact]
    public void Null_is_rejected_rather_than_silently_treated_as_empty()
    {
        Assert.Throws<ArgumentNullException>(() => DictationTextFormatter.Format(null!));
    }

    // ── The terminal gate ────────────────────────────────────────────────
    //
    // macOS returns .none for its three terminal bundle ids, so a shell command
    // dictated into iTerm2 lands byte-for-byte as spoken. The Windows list is
    // process names because that is what the foreground resolver answers with.

    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("wt")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("cmd")]
    [InlineData("conhost")]
    public void Every_listed_terminal_is_recognised(string process)
    {
        Assert.True(DictationTextFormatter.IsTerminal(process));
    }

    [Theory]
    // The casing Process.ProcessName reports is whatever the file system
    // recorded, and that differs between machines. A gate that is
    // case-sensitive is a gate that works on the developer's box.
    [InlineData("windowsterminal")]
    [InlineData("WINDOWSTERMINAL")]
    [InlineData("PowerShell")]
    // A stray space would otherwise silently turn formatting back on.
    [InlineData("  pwsh  ")]
    public void The_gate_is_case_and_whitespace_insensitive(string process)
    {
        Assert.True(DictationTextFormatter.IsTerminal(process));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome")]
    [InlineData("slack")]
    [InlineData("devenv")]
    // Not a terminal, despite the name — the gate is an exact-name set and not
    // a substring match, because "Terminal" appears in plenty of window titles
    // and in at least one text editor's process name.
    [InlineData("TerminalNotesApp")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Everything_else_is_not_a_terminal(string? process)
    {
        Assert.False(DictationTextFormatter.IsTerminal(process));
    }

    [Fact]
    public void A_terminal_gets_the_input_back_unchanged()
    {
        const string command = "git status";
        var formatted = DictationTextFormatter.Format(command, "WindowsTerminal");

        Assert.Equal(command, formatted);
        // The SAME instance, not merely an equal string. macOS's .none profile
        // returns its input; anything that rebuilt the string here would mean a
        // rule ran and happened to be a no-op on this particular sentence.
        Assert.Same(command, formatted);
    }

    [Fact]
    public void A_terminal_keeps_its_trailing_ellipsis()
    {
        Assert.Equal("cat file...", DictationTextFormatter.Format("cat file...", "cmd"));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData(null)]
    [InlineData("")]
    public void Anything_that_is_not_a_terminal_gets_the_shipped_rules(string? process)
    {
        // An unresolved window takes the SAME path as an ordinary application —
        // macOS falls back to .standard for a nil bundle id, never to .none.
        Assert.Equal("Git status", DictationTextFormatter.Format("git status", process));
    }

    [Fact]
    public void The_per_app_overload_rejects_null_text_like_the_other_one()
    {
        Assert.Throws<ArgumentNullException>(
            () => DictationTextFormatter.Format(null!, "notepad"));
    }

    [Fact]
    public void The_terminal_set_is_pinned_so_a_second_reader_cannot_drift_from_it()
    {
        // The set is public because a Style surface will need to read the same
        // list rather than keep a second copy of it. Pinned here so adding a
        // name in one place and not the other is a failing test rather than two
        // surfaces that disagree about what a terminal is.
        Assert.Equal(
            new[] { "alacritty", "cmd", "conhost", "hyper", "mintty", "powershell", "pwsh", "wezterm-gui", "windowsterminal", "wt" },
            DictationTextFormatter.TerminalProcessNames
                .Select(n => n.ToLowerInvariant())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray());
    }

    // ── The CJK repairs (macOS DictationFormatter v5) ─────────────────────
    //
    // The same cases the Swift self-check pins, character for character. The
    // need is measured on the owner's history (2026-10-04): 76 of 107
    // Chinese-bearing transcripts carried no punctuation at all.

    [Theory]
    [InlineData("我们下午三点开会", "我们下午三点开会。")]
    [InlineData("请帮我打开一下Slack", "请帮我打开一下Slack。")]       // Han-dominant, ends on a Latin product name, still Chinese
    [InlineData("我买了3", "我买了3。")]                     // a trailing digit keeps the sentence's script
    [InlineData("おはようございます", "おはようございます。")]   // kana takes the same stop
    [InlineData("已经好了。", "已经好了。")]                  // no doubling
    [InlineData("真的吗？", "真的吗？")]
    [InlineData(" 会议 ", " 会议。 ")]  // the stop lands after the last Han character, whitespace preserved
    public void A_Chinese_sentence_ends_in_the_full_width_stop(string input, string expected)
    {
        Assert.Equal(expected, DictationTextFormatter.ApplyCjkPunctuation(input));
    }

    [Theory]
    [InlineData("안녕하세요")]   // Korean takes an ASCII stop, which this rule never adds
    [InlineData("hello world")]  // English is byte-identical to before
    [InlineData("测试一下 we also work with Asteron and we are comparing ourselves against Fable")]
    [InlineData("你好,")]        // a trailing comma is no terminal mark and gets none
    public void Non_Chinese_text_is_never_given_a_stop(string input)
    {
        Assert.Equal(input, DictationTextFormatter.ApplyCjkPunctuation(input));
    }

    [Theory]
    [InlineData("现在我这网络状态不是很好,豆包在那里罢工", "现在我这网络状态不是很好，豆包在那里罢工。")]
    [InlineData("好的,你现在已经知道我的要求,你可以开始了", "好的，你现在已经知道我的要求，你可以开始了。")]
    [InlineData("你知道吗?", "你知道吗？")]
    public void A_Han_bracketed_separator_goes_full_width(string input, string expected)
    {
        Assert.Equal(expected, DictationTextFormatter.ApplyCjkPunctuation(input));
    }

    [Theory]
    [InlineData("hello, world", "hello, world")]                 // Latin neighbours: half-width stays
    [InlineData("用 Slack, Teams 开会", "用 Slack, Teams 开会。")]  // Latin on one side: half-width stays, stop still lands
    [InlineData("我买了3,000个", "我买了3,000个。")]                // digit grouping is not a separator
    public void A_separator_with_Latin_on_either_side_stays_half_width(string input, string expected)
    {
        Assert.Equal(expected, DictationTextFormatter.ApplyCjkPunctuation(input));
    }

    [Fact]
    public void The_full_pipeline_repairs_Chinese_and_leaves_English_bare()
    {
        // Through Format (the shipped pipeline), not just the rule in
        // isolation: the stop and the conversion survive the other rules, and
        // an English chat message still gets NO trailing period — the
        // regression that kept terminal punctuation out of the default set.
        Assert.Equal("好的，你现在已经知道我的要求。", DictationTextFormatter.Format("好的,你现在已经知道我的要求"));
        Assert.Equal("Wait, what do you think", DictationTextFormatter.Format("wait, what do you think"));
    }

    [Fact]
    public void An_English_sentence_after_a_full_width_stop_is_capitalised()
    {
        Assert.Equal(
            "我们已经跑通了。 Next step is the demo",
            DictationTextFormatter.Format("我们已经跑通了。 next step is the demo"));
    }

    [Fact]
    public void A_terminal_keeps_Chinese_text_byte_identical()
    {
        const string input = "我们下午三点开会";
        Assert.Same(input, DictationTextFormatter.Format(input, "cmd"));
    }

    [Fact]
    public void Cjk_punctuation_is_idempotent()
    {
        foreach (var input in new[] { "我们下午三点开会", "好的,你现在已经知道我的要求", "已经好了。" })
        {
            var once = DictationTextFormatter.Format(input);
            Assert.Equal(once, DictationTextFormatter.Format(once));
        }
    }
}
