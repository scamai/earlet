using Dictation.Core.Learning;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// The limits and boundaries of LEARNING.md §3 and §4.2 that the golden cases
/// do not pin. Expected answers are the reference's.
/// </summary>
public sealed class LearningPolicyTests
{
    private static readonly WordListLexicon Lexicon = new(LearningGoldenTests.SharedLexicon);

    private static CorrectionVerdict Classify(string heard, string meant) =>
        LearningPolicy.Classify(heard, meant, Lexicon);

    // ─── Classifying (§3.3) ───

    [Fact]
    public void A_distance_of_exactly_half_is_still_a_spelling()
    {
        Assert.Equal(CorrectionKind.Spelling, Classify("abcd", "abxy").Candidate?.Kind);
        Assert.Equal(RejectionReason.Rewording, Classify("abcd", "axyz").Rejection?.Reason);
    }

    [Fact]
    public void Four_non_CJK_tokens_and_eight_CJK_characters_are_the_longest_correction()
    {
        Assert.NotNull(Classify("a b c d", "abcd x").Candidate);
        Assert.Equal(RejectionReason.TooLong, Classify("a b c d e", "x").Rejection?.Reason);
        Assert.Equal(RejectionReason.TooLong, Classify("x", "a b c d e").Rejection?.Reason);
        Assert.Equal(CorrectionKind.Transliteration, Classify("一二三四五六七八", "X").Candidate?.Kind);
        Assert.Equal(RejectionReason.TooLong, Classify("一二三四五六七八九", "x").Rejection?.Reason);
    }

    [Fact]
    public void The_inflection_rule_needs_the_shorter_side_to_be_ordinary()
    {
        Assert.Equal(RejectionReason.Inflection, Classify("file", "files").Rejection?.Reason);
        Assert.Equal(RejectionReason.Inflection, Classify("Files", "file").Rejection?.Reason);
        Assert.Equal(CorrectionKind.Spelling, Classify("postgres", "postgress").Candidate?.Kind);
    }

    [Theory]
    [InlineData("阿里斯", "alisi")]
    [InlineData("阿里斯", "ali si")]
    [InlineData("阿里斯", "a'li si")]
    public void Lowercase_ASCII_for_CJK_is_uncommitted_input_method_text(string heard, string meant)
    {
        Assert.Equal(RejectionReason.Composition, Classify(heard, meant).Rejection?.Reason);
    }

    [Theory]
    [InlineData("阿里斯", "Miles")]      // a capital: the user typed it
    [InlineData("阿里斯", "alisi’")] // only the ASCII apostrophe is composition
    [InlineData("阿里斯", "4471")]       // no letter: a number, not a reading
    [InlineData("alisi", "阿里斯")]       // CJK on the meant side is a commit
    [InlineData("阿里斯", "' '")]        // no letter at all is not a reading either
    public void Anything_else_across_scripts_is_still_a_transliteration(string heard, string meant)
    {
        Assert.Equal(CorrectionKind.Transliteration, Classify(heard, meant).Candidate?.Kind);
    }

    // ─── Extracting (§3.2) ───

    [Theory]
    [InlineData("Call trovio", "Call Trovioo now", "trovio", "Trovioo now")]
    [InlineData("trovio is great", "Hey Trovioo is great", "trovio", "Hey Trovioo")]
    [InlineData("trovio", "Trovioo now", "trovio", "Trovioo now")]
    [InlineData("call a", "call b c d e f g", "a", "b c d e f g")] // before the too-long test
    public void A_hunk_that_grows_at_an_end_of_both_texts_is_a_boundary(
        string dictated, string edited, string heard, string meant)
    {
        var result = LearningPolicy.Extract(dictated, edited, Lexicon);
        Assert.Empty(result.Candidates);
        Assert.Equal(new CorrectionRejection(heard, meant, RejectionReason.Boundary), Assert.Single(result.Rejections));
    }

    [Theory]
    [InlineData("ask javis to deploy", "ask Jarvis AI to deploy", "javis", "Jarvis AI")] // grows in the middle
    [InlineData("Call trovio now", "Call Trovioo", "trovio now", "Trovioo")]            // shrinks at the end
    [InlineData("we use trovio", "we use Trovioo", "trovio", "Trovioo")]                // same length at the end
    [InlineData("trovio rocks", "Trovioo rocks", "trovio", "Trovioo")]                  // same length at the start
    public void A_hunk_that_does_not_grow_at_an_end_is_classified(
        string dictated, string edited, string heard, string meant)
    {
        var result = LearningPolicy.Extract(dictated, edited, Lexicon);
        Assert.Empty(result.Rejections);
        Assert.Equal((heard, meant), (Assert.Single(result.Candidates).Heard, result.Candidates[0].Meant));
    }

    [Fact]
    public void A_dictation_that_keeps_exactly_half_its_tokens_is_a_correction_and_less_is_a_rewrite()
    {
        var kept = LearningPolicy.Extract("we use trovio now", "we use Trovioo then", Lexicon);
        Assert.Equal(("trovio now", "Trovioo then"), (kept.Candidates[0].Heard, kept.Candidates[0].Meant));

        Assert.Same(CorrectionExtract.None, LearningPolicy.Extract("we use trovio now", "they use Trovioo then", Lexicon));
    }

    [Fact]
    public void A_short_dictation_is_never_treated_as_a_rewrite()
    {
        var result = LearningPolicy.Extract("a b c", "x y z", Lexicon);
        Assert.Equal(RejectionReason.Rewording, Assert.Single(result.Rejections).Reason);
    }

    [Fact]
    public void Two_hundred_tokens_on_either_side_is_the_most_that_is_diffed()
    {
        var words = string.Join(' ', Enumerable.Repeat("w", 199));
        var at200 = LearningPolicy.Extract(words + " trovio", words + " Trovioo", Lexicon);
        Assert.Equal("Trovioo", Assert.Single(at200.Candidates).Meant);

        Assert.Same(CorrectionExtract.None, LearningPolicy.Extract(words + " w trovio", words + " w Trovioo", Lexicon));
        Assert.Same(CorrectionExtract.None, LearningPolicy.Extract(words + " trovio", words + " Trovioo w", Lexicon));
    }

    [Fact]
    public void The_same_correction_twice_in_one_dictation_is_one_candidate()
    {
        var result = LearningPolicy.Extract("trovio and trovio", "Trovioo and Trovioo", Lexicon);
        Assert.Equal("Trovioo", Assert.Single(result.Candidates).Meant);
    }

    // ─── Aliases (§4.2) ───

    [Theory]
    [InlineData("a i", "AI", CorrectionKind.Spelling, true)]            // a single letter in a phrase
    [InlineData("the team", "TheTeam", CorrectionKind.Spelling, false)] // a phrase of ordinary words
    [InlineData("阿里斯", "Miles", CorrectionKind.Transliteration, true)] // CJK heard: always
    [InlineData("x", "Xylo", CorrectionKind.Spelling, false)]           // heard key under 2 characters
    [InlineData("zupong", "Zupong Ai", CorrectionKind.Spelling, false)] // contains its own trigger
    [InlineData("iphone", "iPhone", CorrectionKind.Casing, false)]      // casing never
    [InlineData("trovio", "Trovioo", CorrectionKind.Spelling, true)]
    [InlineData("四四七一", "4471", CorrectionKind.Transliteration, false)] // a number: never a rewrite target
    [InlineData("a 4", "44", CorrectionKind.Spelling, false)]
    [InlineData("gpt 4", "GPT4", CorrectionKind.Respacing, true)]         // letters and digits: still vocabulary
    public void An_alias_is_admitted_by_the_contract(string heard, string meant, CorrectionKind kind, bool admitted)
    {
        Assert.Equal(admitted, LearningPolicy.LearnsAlias(new CorrectionCandidate(heard, meant, kind), Lexicon));
    }

    [Fact]
    public void A_respacing_alias_needs_a_distinctive_written_form()
    {
        var lexicon = new WordListLexicon(["sky", "deck", "skydock"]);
        Assert.False(LearningPolicy.LearnsAlias(new CorrectionCandidate("sky dock", "skydock", CorrectionKind.Respacing), lexicon));
        Assert.True(LearningPolicy.LearnsAlias(new CorrectionCandidate("sky dock", "SkyDock", CorrectionKind.Respacing), lexicon));
    }
}
