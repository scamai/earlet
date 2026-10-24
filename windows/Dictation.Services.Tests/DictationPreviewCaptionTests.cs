using System.Linq;
using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The windowing contract for the live dictation caption: what the pill draws
/// out of the accumulator's (possibly paragraph-length) text.
/// </summary>
public sealed class DictationPreviewCaptionTests
{
    [Fact]
    public void Null_and_empty_input_return_empty()
    {
        Assert.Equal(string.Empty, DictationPreviewCaption.Window(null));
        Assert.Equal(string.Empty, DictationPreviewCaption.Window(string.Empty));
        Assert.Equal(string.Empty, DictationPreviewCaption.Window("   \t\n  "));
    }

    [Theory]
    [InlineData("You")]
    [InlineData(" you ")]
    [InlineData("YOU.")]
    public void Standalone_you_is_held_until_it_becomes_a_phrase(string partial)
    {
        Assert.Equal(string.Empty, DictationPreviewCaption.Window(partial));
    }

    [Theory]
    [InlineData("you can")]
    [InlineData("You are")]
    [InlineData("truly you")]
    public void You_inside_a_real_phrase_is_preserved(string partial)
    {
        Assert.Equal(partial, DictationPreviewCaption.Window(partial));
    }

    [Fact]
    public void Latin_text_at_or_under_the_word_window_is_unchanged()
    {
        Assert.Equal("one two three",
            DictationPreviewCaption.Window("one two three"));
    }

    [Fact]
    public void Latin_text_over_the_word_window_keeps_only_the_trailing_words()
    {
        var text = "the quick brown fox jumps over the lazy dog";

        // Default window is 7: enough cadence context for the compact ticker.
        Assert.Equal("brown fox jumps over the lazy dog", DictationPreviewCaption.Window(text));
    }

    [Fact]
    public void Word_window_is_configurable()
    {
        var text = "the quick brown fox jumps over the lazy dog";

        Assert.Equal("lazy dog", DictationPreviewCaption.Window(text, wordWindow: 2));
        Assert.Equal("dog", DictationPreviewCaption.Window(text, wordWindow: 1));
    }

    [Fact]
    public void Punctuation_stays_attached_to_its_word_rather_than_being_stripped()
    {
        // A word is a whitespace-delimited run, not a stripped token — "world!"
        // is one word, matching how a person would count these five words.
        Assert.Equal("world! good",
            DictationPreviewCaption.Window("hello, world! good", wordWindow: 2));
    }

    [Fact]
    public void Non_positive_word_window_falls_back_to_the_default_rather_than_producing_nothing()
    {
        var text = "the quick brown fox jumps over the lazy dog";

        Assert.Equal(DictationPreviewCaption.Window(text), DictationPreviewCaption.Window(text, wordWindow: 0));
        Assert.Equal(DictationPreviewCaption.Window(text), DictationPreviewCaption.Window(text, wordWindow: -3));
    }

    [Fact]
    public void Cjk_text_at_or_under_the_character_budget_is_unchanged()
    {
        // "I think it's fine" — no spaces, six characters, comfortably under
        // the default 16-character budget.
        Assert.Equal("我觉得没问题", DictationPreviewCaption.Window("我觉得没问题"));
    }

    [Fact]
    public void Cjk_text_over_the_character_budget_keeps_only_the_trailing_characters()
    {
        // 20 distinct, sequential Han ideographs so the trailing slice is
        // unambiguous to assert on.
        var run = string.Concat(Enumerable.Range(0, 20).Select(i => (char)(0x4E00 + i)));
        var expectedTail = run[^16..];

        Assert.Equal(expectedTail, DictationPreviewCaption.Window(run));
    }

    [Fact]
    public void Cjk_character_budget_is_configurable()
    {
        var run = string.Concat(Enumerable.Range(0, 20).Select(i => (char)(0x4E00 + i)));

        Assert.Equal(run[^3..], DictationPreviewCaption.Window(run, cjkCharacterBudget: 3));
    }

    [Fact]
    public void Non_positive_cjk_budget_falls_back_to_the_default()
    {
        var run = string.Concat(Enumerable.Range(0, 20).Select(i => (char)(0x4E00 + i)));

        Assert.Equal(
            DictationPreviewCaption.Window(run),
            DictationPreviewCaption.Window(run, cjkCharacterBudget: 0));
    }

    [Fact]
    public void Mixed_cjk_and_latin_text_under_both_budgets_is_unchanged()
    {
        // The example from the spec: a Chinese clause, an English acronym,
        // another Chinese clause — nine CJK characters total (under the
        // 16-character budget) and one word (under the 7-word window), so
        // nothing needs to be dropped from either side.
        Assert.Equal("我觉得 QNN 没问题",
            DictationPreviewCaption.Window("我觉得 QNN 没问题"));
    }

    [Fact]
    public void Mixed_text_spends_each_budget_independently_when_trimming()
    {
        // 12 trailing English words (comfortably over the 7-word window) plus
        // a 12-character trailing CJK run (under the CJK budget, so it
        // survives whole). The window should keep the last 7 WORDS *and* the
        // whole CJK run after them — one budget is not made to eat the other.
        var words = new[]
        {
            "hello", "there", "my", "friend", "this", "is", "a",
            "long", "dictation", "session", "about", "QNN",
        };
        var cjkRun = "中文一二三四五六七八九十"; // 12 characters
        var text = string.Join(' ', words) + " " + cjkRun;

        Assert.Equal("is a long dictation session about QNN " + cjkRun,
            DictationPreviewCaption.Window(text));
    }

    [Fact]
    public void A_cjk_run_that_alone_exceeds_its_budget_wins_the_whole_window()
    {
        // The trailing window must be a CONTIGUOUS suffix of the source text.
        // When the trailing CJK run alone is longer than the CJK budget, the
        // cut necessarily falls INSIDE that run — so nothing earlier
        // (including "QNN" and the English words before it) can be attached
        // without leaving a gap in the middle of the caption. The whole
        // caption becomes just the run's trailing slice.
        var words = new[] { "hello", "there", "about", "QNN" };
        var cjkRun = string.Concat(Enumerable.Range(0, 20).Select(i => (char)(0x4E00 + i))); // 20 chars, over budget
        var text = string.Join(' ', words) + " " + cjkRun;

        Assert.Equal(cjkRun[^16..], DictationPreviewCaption.Window(text));
    }

    [Fact]
    public void The_window_never_skips_a_segment_to_reach_a_cheaper_one_further_back()
    {
        // Six words, window of 5: the window must stop at "two" rather than
        // reaching past it for "one", even though "one" alone would "fit".
        Assert.Equal("two three four five six",
            DictationPreviewCaption.Window("one two three four five six", wordWindow: 5));
    }

    [Fact]
    public void Whitespace_runs_collapse_to_a_single_space()
    {
        Assert.Equal("hello world foo",
            DictationPreviewCaption.Window("hello   world\n\t foo"));
    }

    [Fact]
    public void Output_never_has_leading_or_trailing_whitespace()
    {
        var text = "  the quick brown fox jumps  ";

        var result = DictationPreviewCaption.Window(text);

        Assert.Equal(result.Trim(), result);
        Assert.False(result.StartsWith(' '));
        Assert.False(result.EndsWith(' '));
    }

    [Fact]
    public void Default_policy_includes_the_trailing_fragment_as_it_grows()
    {
        // The recogniser's own growth pattern: tru -> truly -> truly you.
        // IncludeTrailingFragment (the default) shows each one as it arrives.
        Assert.Equal("tru", DictationPreviewCaption.Window("tru"));
        Assert.Equal("truly", DictationPreviewCaption.Window("truly"));
        Assert.Equal("truly you", DictationPreviewCaption.Window("truly you"));
    }

    [Fact]
    public void Hold_policy_withholds_the_trailing_token_until_a_further_one_starts()
    {
        const DictationCaptionPartialWordPolicy hold =
            DictationCaptionPartialWordPolicy.HoldUntilWordBoundary;

        // Only one token exists so far in each of these — held, so nothing
        // shows yet. This is the deliberate cost of the steadier policy: it
        // reads as one token behind, and with only one token spoken, "one
        // behind" is empty.
        Assert.Equal(string.Empty, DictationPreviewCaption.Window("tru", partialWordPolicy: hold));
        Assert.Equal(string.Empty, DictationPreviewCaption.Window("truly", partialWordPolicy: hold));

        // A second token has now started, so "truly" graduates out of the
        // trailing slot and is shown; "you" is the new trailing token and is
        // withheld in its place.
        Assert.Equal("truly", DictationPreviewCaption.Window("truly you", partialWordPolicy: hold));
        Assert.Equal("truly you", DictationPreviewCaption.Window("truly you all", partialWordPolicy: hold));
    }

    [Fact]
    public void Hold_policy_withholds_a_trailing_cjk_run_the_same_way()
    {
        const DictationCaptionPartialWordPolicy hold =
            DictationCaptionPartialWordPolicy.HoldUntilWordBoundary;

        // A single, still-growing CJK run is the last (and only) segment —
        // held, same as a single growing Latin word would be.
        Assert.Equal(string.Empty, DictationPreviewCaption.Window("我", partialWordPolicy: hold));
        Assert.Equal(string.Empty, DictationPreviewCaption.Window("我觉得", partialWordPolicy: hold));

        // Once English starts after it, the Chinese clause is no longer
        // trailing and is shown; "QNN" becomes the new withheld tail.
        Assert.Equal("我觉得", DictationPreviewCaption.Window("我觉得 QNN", partialWordPolicy: hold));
    }

    [Fact]
    public void Does_not_throw_on_a_lone_surrogate()
    {
        var exception = Record.Exception(() => DictationPreviewCaption.Window("abc\uD800def"));

        Assert.Null(exception);
    }

    [Fact]
    public void Does_not_throw_on_a_bare_lone_surrogate_with_nothing_else()
    {
        var exception = Record.Exception(() => DictationPreviewCaption.Window("\uD800"));

        Assert.Null(exception);
    }

    [Fact]
    public void Does_not_throw_on_a_combining_mark_sequence()
    {
        // "e" + COMBINING ACUTE ACCENT (U+0301), one grapheme cluster.
        var text = "café truly you";

        var exception = Record.Exception(() => DictationPreviewCaption.Window(text));

        Assert.Null(exception);
    }

    [Fact]
    public void Does_not_throw_on_a_very_long_string()
    {
        var text = string.Concat(Enumerable.Repeat("word ", 200_000));

        var exception = Record.Exception(() => DictationPreviewCaption.Window(text));

        Assert.Null(exception);
    }

    [Fact]
    public void Grapheme_clusters_are_never_split_by_a_latin_word_boundary()
    {
        // The combining-mark cluster is the whole last word; whole-word
        // atomicity for non-CJK segments means it is either kept intact or
        // dropped entirely — never sliced in half.
        var text = "hello café"; // "hello cafe" with a combining accent on the final e

        var result = DictationPreviewCaption.Window(text, wordWindow: 1);

        Assert.Equal("café", result);
    }

    [Fact]
    public void A_surrogate_pair_inside_a_truncated_cjk_run_is_kept_whole()
    {
        // Three supplementary-plane CJK ideographs (Extension B, each one a
        // surrogate PAIR — two UTF-16 code units) followed by three
        // ordinary BMP Han characters. Budget of 2 forces a cut that must
        // land on a whole character, not half a surrogate pair.
        var supplementary = char.ConvertFromUtf32(0x20000)
            + char.ConvertFromUtf32(0x20001)
            + char.ConvertFromUtf32(0x20002);
        var text = supplementary + "一二三";

        var result = DictationPreviewCaption.Window(text, cjkCharacterBudget: 2);

        Assert.Equal("二三", result);
        // If a surrogate pair had been split, this would not be valid UTF-16
        // (an unpaired surrogate) and StringInfo would report more than 2
        // text elements for what should be exactly 2 whole characters.
        Assert.Equal(2, new System.Globalization.StringInfo(result).LengthInTextElements);
    }

    [Fact]
    public void A_truncated_cjk_run_never_cuts_a_surrogate_pair_even_when_it_is_the_boundary_character()
    {
        // The boundary character itself (the one right at the cut) is a
        // surrogate pair — this is the case most likely to be sliced in half
        // by a naive char-index-based Substring.
        var text = "一" + char.ConvertFromUtf32(0x20000) + "二";

        var result = DictationPreviewCaption.Window(text, cjkCharacterBudget: 2);

        Assert.Equal(char.ConvertFromUtf32(0x20000) + "二", result);
    }
}
