using Dictation.Core.Learning;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// Tokens, lowercase and the other string answers the rules are built on. The
/// expected values are what <c>tests/learning_reference.py</c> (Python 3.12)
/// returns for the same input.
/// </summary>
public sealed class LearningTextTests
{
    // ─── Tokens (LEARNING.md §3.1) ───

    [Fact]
    public void A_CJK_extension_B_character_is_one_token_of_one_code_point()
    {
        // U+20000 is a surrogate pair in UTF-16: two chars, one code point.
        var text = "\U00020000abc 𝐀𝐁c";
        Assert.Equal(2, "\U00020000".Length);

        var tokens = LearningTokens.Tokenize(text);

        Assert.Equal(
            [("\U00020000", 0, 1, true), ("abc", 1, 4, false), ("𝐀𝐁c", 5, 8, false)],
            tokens.Select(t => (t.Text, t.Start, t.End, t.IsCjk)));
        Assert.Equal("\U00020000", text[tokens[0].Utf16Start..tokens[0].Utf16End]);
        Assert.Equal("𝐀𝐁c", text[tokens[2].Utf16Start..tokens[2].Utf16End]);
    }

    [Fact]
    public void A_token_continues_across_one_joiner_followed_by_a_word_character()
    {
        Assert.Equal(
            ["don't", "Node.js", "a", "b", "x", "y"],
            LearningTokens.Tokenize("don't Node.js a--b x_ y'").Select(t => t.Text));
    }

    [Fact]
    public void Every_CJK_code_point_is_a_token_by_itself()
    {
        Assert.Equal(
            ["日", "本", "語", "テ", "キ", "ス", "ト", "한", "국", "어"],
            LearningTokens.Tokenize("日本語テキスト한국어").Select(t => t.Text));
        Assert.All(LearningTokens.Tokenize("我们用Miles服务器").Where(t => t.Text != "Miles"), t => Assert.True(t.IsCjk));
    }

    [Fact]
    public void A_lone_surrogate_separates_tokens_and_is_kept_in_a_slice()
    {
        var text = "ab\uD800cd";
        var tokens = LearningTokens.Tokenize(text);
        Assert.Equal(["ab", "cd"], tokens.Select(t => t.Text));
        Assert.Equal((3, 5), (tokens[1].Start, tokens[1].End));
        Assert.Equal(5, LearningText.Length(text));
    }

    [Fact]
    public void Extract_cuts_the_original_substring_so_spacing_inside_a_span_is_kept()
    {
        var result = LearningPolicy.Extract("we met at sky  deck today", "we met at SkyDock today",
            new WordListLexicon(LearningGoldenTests.SharedLexicon));
        Assert.Equal("sky  deck", Assert.Single(result.Candidates).Heard);
    }

    // ─── Python's str ───

    [Theory]
    [InlineData("ΛΟΓΟΣ Σ", "λογος σ")]
    [InlineData("ΣΑ", "σα")]
    [InlineData("aΣ.", "aς.")]
    [InlineData("aΣ\u0301b", "aσ\u0301b")]
    [InlineData("İstanbul", "i\u0307stanbul")]
    [InlineData("xªY", "xªy")]
    [InlineData("TROVIOO", "trovioo")]
    [InlineData("𐐀", "𐐨")]
    public void Lower_is_python_lower(string text, string expected)
    {
        Assert.Equal(expected, LearningText.Lower(text));
    }

    [Fact]
    public void White_space_is_python_white_space()
    {
        // U+001C..U+001F are white space to Python and not to .NET.
        Assert.Equal("sky dock x", LearningTokens.HeardKey("  Sky\tDock\u001c x "));
        Assert.Equal("a\u001fb", LearningText.Strip("\u001c a\u001fb \u3000"));
        Assert.Equal(["a", "b"], LearningText.SplitOnSpace("a\u001eb"));
    }

    [Fact]
    public void Strings_sort_in_code_point_order()
    {
        // Ordinal UTF-16 order would put U+FFFF after U+10000.
        var sorted = new[] { "\U00010000", "\uFFFF", "a", "B" }.OrderBy(s => s, LearningText.CodePointOrder);
        Assert.Equal(["B", "a", "\uFFFF", "\U00010000"], sorted);
        Assert.True(string.CompareOrdinal("\U00010000", "\uFFFF") < 0);
    }

    [Fact]
    public void Levenshtein_counts_code_points()
    {
        Assert.Equal(1, LearningTokens.Levenshtein("\U00020000a", "a"));
        Assert.Equal(3, LearningTokens.Levenshtein("kitten", "sitting"));
        Assert.Equal(0, LearningTokens.Levenshtein("", ""));
    }

    [Fact]
    public void The_squashed_and_spaced_forms_drop_joiners_and_case()
    {
        Assert.Equal("nodejs", LearningTokens.Squash("Node.js"));
        Assert.Equal("email x", LearningTokens.NormSpace("e-mail X"));
    }

    [Fact]
    public void Contains_tokens_matches_whole_tokens_ignoring_case()
    {
        Assert.True(LearningTokens.ContainsTokens("Zupong Ai", "zupong"));
        Assert.False(LearningTokens.ContainsTokens("Fable Flow", "flo"));
        Assert.False(LearningTokens.ContainsTokens("a", ""));
    }

    // ─── Distinctive (LEARNING.md §2) ───

    [Theory]
    [InlineData("SkyDock", true)]
    [InlineData("iPhone", true)]
    [InlineData("GPT4", true)]
    [InlineData("Trovioo", true)]
    [InlineData("the server", false)]
    [InlineData("Miles", false)]
    [InlineData("阿里斯", false)]
    [InlineData("ªB", true)] // ª is lowercase to Python (Other_Lowercase), so B is an internal capital
    [InlineData("ªb", false)]
    public void Distinctive_follows_the_contract(string text, bool distinctive)
    {
        var lexicon = new WordListLexicon(["the", "server", "miles", "b", "ªb", "ª"]);
        Assert.Equal(distinctive, LearningPolicy.IsDistinctive(text, lexicon));
    }
}
