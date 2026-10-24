using Dictation.Core.Learning;
using Dictation.Services;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// The ordinary-word test (LEARNING.md §2), over a small list and over the
/// shipped <c>web2</c> resource. Expected answers are the reference's.
/// </summary>
public sealed class LearningLexiconTests
{
    private static readonly string[] Small =
        ["include", "library", "box", "log", "meeting", "e", "mail", "node", "js"];

    [Theory]
    [InlineData("libraries", true)]  // -ies -> -y
    [InlineData("boxes", true)]      // drop -es
    [InlineData("logs", true)]       // drop -s
    [InlineData("MEETINGS", true)]   // case does not matter
    [InlineData("e-mail", true)]     // every part of a split
    [InlineData("Node.js", true)]
    [InlineData("node_js", true)]
    [InlineData("node's", true)]     // cut at the first apostrophe
    [InlineData("node\u2019s", true)]
    [InlineData("---", true)]        // no parts at all
    [InlineData("includes", true)]
    [InlineData("INCLUDING", true)]  // -ing with the e restored
    [InlineData("included", true)]   // -ed with the e restored
    [InlineData("logged", true)]     // one of a doubled consonant dropped
    [InlineData("logging", true)]
    [InlineData("boxed", true)]      // -ed dropped
    [InlineData("libraried", true)]  // -ied -> -y
    [InlineData("eing", false)]      // "e" is too short a base
    [InlineData("eed", false)]
    [InlineData("Includings", false)] // no plural of an -ing form
    [InlineData("zqx-meeting", false)]
    [InlineData("'node", false)]     // an apostrophe first is not a cut
    [InlineData("4417", true)]       // a part with no letter is a number: ordinary
    [InlineData("1-800", true)]
    [InlineData("4417's", true)]
    [InlineData("\u0664\u0664", true)] // Arabic-Indic digits are digits too
    [InlineData("1-zqx", false)]     // only the lettered part is looked up
    [InlineData("4417s", false)]
    [InlineData("gpt4", false)]
    public void A_word_list_lexicon_applies_the_three_plural_rules_and_the_splits(string token, bool ordinary)
    {
        Assert.Equal(ordinary, new WordListLexicon(Small).IsOrdinary(token));
    }

    [Fact]
    public void An_empty_word_list_fails_closed()
    {
        var lexicon = new WordListLexicon(["", "  ", null]);
        Assert.False(lexicon.IsAvailable);
        Assert.True(lexicon.IsOrdinary("Trovioo"));
        Assert.True(lexicon.IsOrdinary("kubectl"));
        Assert.True(lexicon.IsOrdinary("gpt4"));
        Assert.False(LearningPolicy.IsDistinctive("Trovioo", lexicon));

        // Nothing is learned, not the word and not the alias.
        var book = new LearningBook(lexicon);
        book.Observe("d1", "we use trovio", "we use Trovioo", 1);
        book.Observe("d2", "we use trovio", "we use Trovioo", 2);
        var projection = book.Projection();
        Assert.Empty(projection.Words);
        Assert.Empty(projection.Aliases);
        Assert.Empty(projection.Pending);
    }

    [Fact]
    public void Webster_over_an_unreadable_resource_fails_closed_where_the_import_lexicon_fails_open()
    {
        // VocabularyLexicon.Empty is what Shared becomes when the embedded
        // resource cannot be read: every word UNcommon (fail open).
        Assert.False(VocabularyLexicon.Empty.IsCommonWord("the"));

        var lexicon = new WebsterLexicon(VocabularyLexicon.Empty);
        Assert.False(lexicon.IsAvailable);
        Assert.True(lexicon.IsOrdinary("Trovioo"));
        Assert.False(LearningPolicy.LearnsAlias(
            new CorrectionCandidate("trovio", "Trovioo", CorrectionKind.Spelling), lexicon));
    }

    [Fact]
    public void Webster_asks_about_lowercase_parts_so_the_shouted_word_rules_never_run()
    {
        // The import lexicon folds NONINFRINGEMENT onto infringement and
        // AGREEMENT onto agree; the learning contract has the plural rules and
        // the -ed/-ing endings (which also cover INCLUDING and IMPLIED), and
        // no more.
        string[] list = [.. Small, "imply", "infringement", "agree"];
        var words = VocabularyLexicon.Of(list);
        Assert.True(words.IsCommonWord("NONINFRINGEMENT"));
        Assert.True(words.IsCommonWord("AGREEMENT"));

        var webster = new WebsterLexicon(words);
        var reference = new WordListLexicon(list);
        foreach (var token in new[]
                 {
                     "INCLUDING", "IMPLIED", "NONINFRINGEMENT", "AGREEMENT", "libraries", "BOXES", "e-mail", "Trovioo",
                 })
        {
            Assert.Equal(reference.IsOrdinary(token), webster.IsOrdinary(token));
        }

        Assert.True(webster.IsOrdinary("INCLUDING"));
        Assert.True(webster.IsOrdinary("IMPLIED"));
        Assert.False(webster.IsOrdinary("NONINFRINGEMENT"));
        Assert.False(webster.IsOrdinary("AGREEMENT"));
    }

    [Theory]
    [InlineData("meetings", true)]
    [InlineData("libraries", true)]
    [InlineData("teams", true)]
    [InlineData("Miles", false)] // web2 has only the capitalized proper-name spelling
    [InlineData("jarvis", false)] // same filter, case-insensitive lookup remains
    [InlineData("Mies", true)] // "my" + -s is still an ordinary inflection
    [InlineData("sever", true)]
    [InlineData("don't", true)]
    [InlineData("e-mail", true)]
    [InlineData("x", true)]
    [InlineData("Trovioo", false)]
    [InlineData("kubectl", false)]
    [InlineData("javis", false)]
    [InlineData("postgres", false)]
    [InlineData("SkyDock", false)]
    [InlineData("boxes", false)] // web2 has no "box"
    [InlineData("INCLUDING", true)] // include, -ing with the e restored
    [InlineData("IMPLIED", true)]   // imply, -ied
    [InlineData("ARISING", true)]
    [InlineData("changed", true)]
    [InlineData("running", true)]
    [InlineData("Ewing", true)]     // ewe, -ing with the e restored; not in web2
    [InlineData("Harding", true)]   // hard, -ing; not in web2
    [InlineData("Downing", false)]  // a web2 name: excluded before the endings
    [InlineData("emailed", false)]  // web2 has no "email"
    [InlineData("Ying", false)]     // "y" and "ye" are too short a base
    [InlineData("MERCHANTABILITY", false)]
    [InlineData("'tis", false)]
    [InlineData("4417", true)]
    [InlineData("1-800", true)]
    [InlineData("10x", false)]
    public void The_shipped_web2_resource_answers_as_the_reference_does(string token, bool ordinary)
    {
        // Each answer is learning_reference.Lexicon over the same web2 bytes
        // (md5 f920f969528c6adf9fec6e803470083d), 2026-09-28; the verb
        // endings 2026-10-16.
        Assert.True(WebsterLexicon.Shared.IsAvailable);
        Assert.Equal(ordinary, WebsterLexicon.Shared.IsOrdinary(token));
    }

    [Fact]
    public void A_capitalized_web2_name_can_be_learned_as_a_word_without_an_alias()
    {
        var book = new LearningBook(WebsterLexicon.Shared);

        book.Observe("mies-to-miles", "we saw Mies", "we saw Miles", 1);

        Assert.Equal(["Miles"], book.Projection().Words);
        Assert.Empty(book.Projection().Aliases);
    }
}
