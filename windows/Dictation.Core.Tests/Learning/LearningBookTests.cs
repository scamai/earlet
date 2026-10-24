using Dictation.Core.Learning;
using System.Text.Json;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// The caps, expiry and teaching rules of LEARNING.md §4 beyond the golden
/// cases. Every expected value was produced by running the same steps through
/// <c>tests/learning_reference.py</c>.
/// </summary>
public sealed class LearningBookTests
{
    private const long T = 1_790_000_000_000;
    private const long Day = 24L * 60 * 60 * 1000;

    private static LearningBook Book() => new(new WordListLexicon(LearningGoldenTests.SharedLexicon));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":true,\"observations\":[]}")]
    [InlineData("{\"version\":1,\"observations\":[null]}")]
    [InlineData("{\"version\":1,\"observations\":[{\"id\":\"bad\",\"at\":1,\"weight\":true,\"corrections\":[]}]}")]
    public void Malformed_optional_evidence_preserves_the_readable_vocabulary(string journal)
    {
        var book = Book();
        book.Teach("Miles", null, T);
        book.State.Extra = new() { ["evidence"] = JsonSerializer.Deserialize<JsonElement>(journal) };
        var before = JsonSerializer.Serialize(book.State);
        book.Observe("one", "we use Mies", "we use Miles", T + 1);
        Assert.Equal(before, JsonSerializer.Serialize(book.State));
    }

    [Fact]
    public void Migration_preserves_first_seen_without_double_counting_a_word_and_pending_pair()
    {
        var state = new LearningState();
        state.Words.Add(new LearnedWord { Text = "Trovioo", Count = 1, FirstSeen = T - 20, LastSeen = T - 10 });
        state.Pending.Add(new PendingAlias { Heard = "trovio", Meant = "Trovioo", Count = 1,
            Dictations = ["old"], FirstSeen = T - 20, LastSeen = T - 10 });
        var book = new LearningBook(new WordListLexicon(LearningGoldenTests.SharedLexicon), state);
        book.Observe("new", "we use trovio", "we use Trovioo", T);
        Assert.Equal(2, Assert.Single(book.State.Words).Count);
        Assert.Equal(T - 20, book.State.Words[0].FirstSeen);
        Assert.Equal("Trovioo", Assert.Single(book.State.Aliases).Meant);
        var restarted = new LearningBook(new WordListLexicon(LearningGoldenTests.SharedLexicon),
            JsonSerializer.Deserialize<LearningState>(JsonSerializer.Serialize(book.State))!);
        restarted.Observe("new", "we use trovio", "we use Trovioo", T + 1);
        Assert.Equal(2, Assert.Single(restarted.State.Words).Count);
    }

    private static void LearnTrovioAlias(LearningBook book, long at)
    {
        book.Observe("t1", "we use trovio", "we use Trovioo", at);
        book.Observe("t2", "we use trovio", "we use Trovioo", at + 1);
    }

    // ─── Caps (§4.5) ───

    [Fact]
    public void Learned_words_are_capped_at_100_and_never_push_out_a_taught_word()
    {
        var book = Book();
        Assert.Equal(TeachResult.Ok, book.Teach("Kubectl", null, T));
        for (var i = 0; i <= 100; i++)
        {
            book.Observe($"d{i}", $"use zqx{i:000} now", $"use Zqx{i:000} now", T + 1 + i);
        }

        var words = book.Projection().Words;
        Assert.Equal(101, words.Count);
        Assert.Equal("Kubectl", words[^1]);
        Assert.DoesNotContain("Zqx000", words);
        Assert.Contains("Zqx001", words);
    }

    [Fact]
    public void An_origin_this_version_does_not_know_is_kept_never_evicted_and_never_recased()
    {
        // LEARNING.md §5: a newer app's entry is read as it is. Oldest of all,
        // it still outlives the 100-word cap, and a correction to its casing
        // counts without rewriting it.
        var state = new LearningState();
        state.Words.Add(new LearnedWord { Text = "ZetaCo", Origin = "synced", Count = 3, FirstSeen = 1, LastSeen = 1 });
        for (var i = 0; i < LearningLimits.MaxLearnedWords; i++)
        {
            state.Words.Add(new LearnedWord
            {
                Text = $"Qword{i:000}", Origin = LearningOrigins.Correction, Count = 1, FirstSeen = 10 + i, LastSeen = 10 + i,
            });
        }

        state.Aliases.Add(new LearnedAlias { Heard = "zeta co", Meant = "ZetaCo", Origin = "synced", FirstSeen = 1, LastSeen = 1 });
        var book = new LearningBook(new WordListLexicon(LearningGoldenTests.SharedLexicon), state);

        book.Observe("d1", "we use zetaco", "we use ZETACO", 1000);
        book.Observe("d2", "we use trovio", "we use Trovioo", 1001);

        Assert.Equal(101, book.State.Words.Count);
        var kept = Assert.Single(book.State.Words, w => w.Origin == "synced");
        Assert.Equal(("ZetaCo", 3L), (kept.Text, kept.Count));
        Assert.DoesNotContain(book.State.Words, w => w.Text == "Qword000");
        Assert.Contains(book.State.Words, w => w.Text == "Trovioo");
        Assert.Equal([("zeta co", "ZetaCo", "synced")], book.State.Aliases.Select(a => (a.Heard, a.Meant, a.Origin)));
    }

    [Fact]
    public void Five_hundred_taught_words_is_full_but_reteaching_one_is_not()
    {
        var book = Book();
        for (var i = 0; i < LearningLimits.MaxTaughtWords; i++)
        {
            Assert.Equal(TeachResult.Ok, book.Teach($"Qzt{i:000}", null, T + i));
        }

        Assert.Equal(TeachResult.Full, book.Teach("Qzt500", null, T + 600));
        Assert.Equal(TeachResult.Ok, book.Teach("qzt000", null, T + 601));
        Assert.Equal("qzt000", book.Projection().Words[0]);
    }

    [Fact]
    public void Two_hundred_taught_aliases_is_full_for_a_new_sound_but_not_for_a_known_one()
    {
        var book = Book();
        for (var i = 0; i < LearningLimits.MaxAliases; i++)
        {
            Assert.Equal(TeachResult.Ok, book.Teach($"Qzt{i:000}", $"kay {i:000}", T + i));
        }

        Assert.Equal(TeachResult.Full, book.Teach("Newword", "new sound", T + 700));
        Assert.Equal(TeachResult.Ok, book.Teach("Newword", "kay 000", T + 701));
        Assert.Equal(LearningLimits.MaxAliases, book.Projection().Aliases.Count);
    }

    [Fact]
    public void Past_two_hundred_aliases_the_least_recently_seen_learned_one_goes_first()
    {
        var book = Book();
        for (var i = 0; i < LearningLimits.MaxAliases - 1; i++)
        {
            book.Teach($"Qzt{i:000}", $"kay {i:000}", T + i);
        }

        LearnTrovioAlias(book, T + 1000);
        book.Observe("b1", "ask javis", "ask Jarvis", T + 1002);
        book.Observe("b2", "ask javis", "ask Jarvis", T + 1003);

        var aliases = book.Projection().Aliases.Select(a => a.Key).ToList();
        Assert.Equal(LearningLimits.MaxAliases, aliases.Count);
        Assert.DoesNotContain("trovio", aliases);
        Assert.Contains("javis", aliases);
        Assert.Contains("kay 000", aliases);
    }

    [Fact]
    public void Teaching_a_sound_when_the_table_is_full_evicts_a_learned_alias()
    {
        var book = Book();
        for (var i = 0; i < LearningLimits.MaxAliases - 1; i++)
        {
            book.Teach($"Qzt{i:000}", $"kay {i:000}", T + i);
        }

        LearnTrovioAlias(book, T + 1000);
        Assert.Equal(LearningLimits.MaxAliases, book.State.Aliases.Count);

        Assert.Equal(TeachResult.Ok, book.Teach("Newword", "new sound", T + 2000));
        Assert.Equal(LearningLimits.MaxAliases, book.State.Aliases.Count);
        Assert.DoesNotContain(book.Projection().Aliases, a => a.Key == "trovio");
    }

    [Fact]
    public void Pending_pairs_are_capped_at_500_oldest_first_out()
    {
        var book = Book();
        for (var i = 0; i <= LearningLimits.MaxPending; i++)
        {
            book.Observe($"d{i}", $"use zqx{i:000} now", $"use Zqqx{i:000} now", T + i);
        }

        var pending = book.Projection().Pending;
        Assert.Equal(LearningLimits.MaxPending, pending.Count);
        Assert.Equal(new PendingProjection("zqx001", "Zqqx001", 1), pending[0]);
        Assert.DoesNotContain(pending, p => p.Heard == "zqx000");
    }

    [Fact]
    public void A_pending_pair_lives_exactly_thirty_days()
    {
        var book = Book();
        book.Observe("d1", "we use trovio", "we use Trovioo", T);

        book.Observe("x", "nothing", "nothing", T + 30 * Day);
        Assert.Single(book.Projection().Pending);

        book.Observe("x", "nothing", "nothing", T + 30 * Day + 1);
        Assert.Empty(book.Projection().Pending);
    }

    [Fact]
    public void Tombstones_are_capped_at_1000_oldest_first_out()
    {
        var book = Book();
        for (var i = 0; i <= LearningLimits.MaxTombstones; i++)
        {
            book.RemoveWord($"w{i:0000}", T + i);
        }

        var removed = book.Projection().RemovedWords;
        Assert.Equal(LearningLimits.MaxTombstones, removed.Count);
        Assert.Equal("w0001", removed[0]);
        Assert.Equal("w1000", removed[^1]);
    }

    // ─── Words (§4.1) ───

    [Fact]
    public void A_learned_word_takes_the_latest_casing_and_a_taught_word_keeps_the_users()
    {
        var taught = Book();
        taught.Teach("iPhone", null, T);
        taught.Observe("d1", "open the iphone app", "open the IPhone app", T + 1);
        var word = Assert.Single(taught.State.Words);
        Assert.Equal(("iPhone", LearningOrigins.Taught, 0L), (word.Text, word.Origin, word.Count));

        var learned = Book();
        learned.Observe("d1", "we use trovio", "we use Trovioo", T);
        learned.Observe("d2", "we use trovio", "we use TROVIOO", T + 1);
        word = Assert.Single(learned.State.Words);
        Assert.Equal(("TROVIOO", LearningOrigins.Correction, 2L), (word.Text, word.Origin, word.Count));
        // Casing variants support the same target; its newest casing wins.
        Assert.Empty(learned.Projection().Pending);
        Assert.Equal("TROVIOO", Assert.Single(learned.State.Aliases).Meant);
    }

    [Fact]
    public void A_word_that_waited_keeps_the_count_and_first_sighting_of_its_pending_pair()
    {
        // The golden projection holds word texts only. A secret-shaped word is
        // written when its pending pair is promoted, and the entry takes the
        // pair's count and first sighting, not the moment of promotion. The
        // repeated d1 moves the pair's last sighting but adds no count.
        var book = Book();
        foreach (var (dictation, at) in new[] { ("d1", T), ("d1", T + 1000), ("d2", T + 5000) })
        {
            book.Observe(dictation, "the wifi password is sunshine", "the wifi password is Sunshine42x!", at);
        }

        Assert.Empty(book.State.Pending);
        var word = Assert.Single(book.State.Words);
        Assert.Equal(
            ("Sunshine42x", LearningOrigins.Correction, 2L, T, T + 5000),
            (word.Text, word.Origin, word.Count, word.FirstSeen, word.LastSeen));
    }

    // ─── Teaching (§4.3) ───

    [Theory]
    [InlineData("一二三四五六七八九十一二", null, TeachResult.Ok)]
    [InlineData("一二三四五六七八九十一二三", null, TeachResult.TooLong)]
    [InlineData("a b c d e f", null, TeachResult.Ok)]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", null, TeachResult.Ok)]
    [InlineData("Zed", "yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy", TeachResult.TooLong)]
    [InlineData("!!!", null, TeachResult.Empty)]
    [InlineData("Fable Flow", "fable", TeachResult.SelfContaining)]
    [InlineData("Trovioo", "  ", TeachResult.Ok)]
    public void Teaching_validates_its_input(string written, string? soundsLike, TeachResult expected)
    {
        Assert.Equal(expected, Book().Teach(written, soundsLike, T));
    }

    [Fact]
    public void A_refused_teach_adds_nothing()
    {
        var book = Book();
        Assert.Equal(TeachResult.SelfContaining, book.Teach("Fable Flow", "flow", T));
        Assert.Empty(book.State.Words);
        Assert.Empty(book.State.Aliases);
    }

    [Fact]
    public void Teaching_an_alias_clears_its_tombstone()
    {
        var book = Book();
        LearnTrovioAlias(book, T);
        book.RemoveAlias("trovio", "Trovioo", T + 2);
        Assert.Single(book.Projection().RemovedAliases);

        Assert.Equal(TeachResult.Ok, book.Teach("Trovioo", "trovio", T + 3));

        var projection = book.Projection();
        Assert.Empty(projection.RemovedAliases);
        Assert.Equal([new KeyValuePair<string, string>("trovio", "Trovioo")], projection.Aliases);
        Assert.Equal(LearningOrigins.Taught, Assert.Single(book.State.Aliases).Origin);
    }

    [Fact]
    public void Teaching_a_sound_drops_the_pending_corrections_for_it()
    {
        var book = Book();
        book.Observe("d1", "we use trovio", "we use TrovioAI", T);
        Assert.Single(book.Projection().Pending);

        Assert.Equal(TeachResult.Ok, book.Teach("Trovioo", "Trovio", T + 1));

        var projection = book.Projection();
        Assert.Empty(projection.Pending);
        Assert.Equal(["Trovioo", "TrovioAI"], projection.Words);
    }

    // ─── Tombstones (§4.4) ───

    [Fact]
    public void A_removed_word_is_not_learned_again_by_a_correction()
    {
        var book = Book();
        book.Observe("d1", "we use trovio", "we use Trovioo", T);
        book.RemoveWord(" Trovioo ", T + 1);
        book.Observe("d2", "we use trovio", "we use TROVIOO", T + 2);

        Assert.Empty(book.Projection().Words);
        Assert.Equal(["trovioo"], book.Projection().RemovedWords);
    }

    [Fact]
    public void A_removed_alias_is_not_learned_again_in_any_casing_of_its_written_form()
    {
        var book = Book();
        LearnTrovioAlias(book, T);
        book.RemoveAlias("Trovio", "TROVIOO", T + 2);
        Assert.Empty(book.Projection().Aliases);

        book.Observe("d3", "we use trovio", "we use Trovioo", T + 3);
        book.Observe("d4", "we use trovio", "we use Trovioo", T + 4);

        Assert.Empty(book.Projection().Aliases);
        Assert.Empty(book.Projection().Pending);
    }
}
