using System.Text.Json;
using System.Text.Json.Nodes;
using Dictation.Core.Learning;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// The shared cases in <c>tests/fixtures/learning-golden.json</c>, the file the
/// Python reference and the Swift port are held to, driven through this port.
/// A disagreement is a bug in one of the three, never a tolerance.
/// </summary>
public sealed class LearningGoldenTests
{
    // ─── The fixture ───

    internal static readonly JsonObject Golden = LoadGolden();

    private static JsonObject LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "learning-golden.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    internal static IReadOnlyList<string> SharedLexicon =>
        Golden["lexicon"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();

    internal static JsonObject Case(string section, string name) =>
        Golden[section]!.AsArray().Select(c => c!.AsObject()).Single(c => c["name"]!.GetValue<string>() == name);

    public static TheoryData<string> ExtractCases() => Names("extract");

    public static TheoryData<string> Sequences() => Names("sequences");

    private static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (var node in Golden[section]!.AsArray())
        {
            data.Add(node!["name"]!.GetValue<string>());
        }

        return data;
    }

    internal static IReadOnlyList<string> LexiconOf(JsonObject sequence) =>
        sequence["lexicon"] is JsonArray own ? own.Select(w => w!.GetValue<string>()).ToList() : SharedLexicon;

    // ─── The cases ───

    [Fact]
    public void The_fixture_is_the_expected_schema_and_not_empty()
    {
        Assert.Equal("dictation.learning-golden/1", Golden["schema"]!.GetValue<string>());
        Assert.True(Golden["extract"]!.AsArray().Count >= 15);
        Assert.True(Golden["sequences"]!.AsArray().Count >= 20);
        Assert.NotEmpty(SharedLexicon);
    }

    [Theory]
    [MemberData(nameof(ExtractCases))]
    public void Extract_matches_the_golden_case(string name)
    {
        var golden = Case("extract", name);
        var result = LearningPolicy.Extract(
            golden["dictated"]!.GetValue<string>(),
            golden["edited"]!.GetValue<string>(),
            new WordListLexicon(SharedLexicon));

        Assert.Equal(
            golden["candidates"]!.ToJsonString(),
            Json(result.Candidates.Select(c => new[] { c.Heard, c.Meant, LearningWireNames.Of(c.Kind) })));
        Assert.Equal(
            golden["rejections"]!.ToJsonString(),
            Json(result.Rejections.Select(r => new[] { r.Heard, r.Meant, LearningWireNames.Of(r.Reason) })));
    }

    [Theory]
    [MemberData(nameof(Sequences))]
    public void A_sequence_matches_the_golden_case(string name)
    {
        var golden = Case("sequences", name);
        var book = new LearningBook(new WordListLexicon(LexiconOf(golden)));
        var results = Run(golden, step => Apply(book, step));

        Assert.Equal(ExpectedTeachResults(golden), results);
        Assert.Equal(golden["expect"]!.ToJsonString(), ProjectionJson(book.Projection()));
        if (golden["expect_journal"] is JsonArray expectedJournal)
        {
            Assert.Equal(expectedJournal.ToJsonString(), JournalJson(book.State));
        }

        if (golden["expect_word_evidence"] is JsonObject evidence)
        {
            foreach (var (text, expected) in evidence)
            {
                var values = expected!.AsArray();
                var word = book.State.Words.SingleOrDefault(w =>
                    string.Equals(w.Text, text, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(word);
                Assert.Equal(values[0]!.GetValue<long>(), word!.Count);
                Assert.NotNull(word.Extra);
                Assert.True(word.Extra!.TryGetValue("opposition", out var opposition));
                Assert.Equal(values[1]!.GetValue<long>(), opposition.GetInt64());
            }
        }
    }

    /// <summary>The evidence journal in the fixture's <c>expect_journal</c> shape; empty when none was written.</summary>
    internal static string JournalJson(LearningState state)
    {
        var records = new JsonArray();
        if (state.Extra?.TryGetValue("evidence", out var journal) == true)
        {
            foreach (var record in journal.GetProperty("observations").EnumerateArray())
            {
                var corrections = new JsonArray();
                foreach (var c in record.GetProperty("corrections").EnumerateArray())
                {
                    corrections.Add(new JsonArray(c.GetProperty("heard").GetString(), c.GetProperty("meant").GetString()));
                }

                records.Add(new JsonObject { ["id"] = record.GetProperty("id").GetString(), ["corrections"] = corrections });
            }
        }

        return records.ToJsonString();
    }

    // ─── Driving a sequence ───

    internal static List<string> Run(JsonObject golden, Func<JsonObject, string?> apply)
    {
        var results = new List<string>();
        foreach (var step in golden["steps"]!.AsArray())
        {
            if (apply(step!.AsObject()) is { } result)
            {
                results.Add(result);
            }
        }

        return results;
    }

    internal static List<string> ExpectedTeachResults(JsonObject golden) =>
        golden["steps"]!.AsArray()
            .Where(s => s!["op"]!.GetValue<string>() == "teach")
            .Select(s => s!["result"]!.GetValue<string>())
            .ToList();

    internal static string? Apply(LearningBook book, JsonObject step)
    {
        switch (Op(step))
        {
            case "observe":
                book.Observe(Str(step, "dictation"), Str(step, "dictated"), Str(step, "edited"), At(step));
                return null;
            case "teach":
                return Wire(book.Teach(Str(step, "written"), step["sounds_like"]?.GetValue<string>(), At(step)));
            case "remove_word":
                book.RemoveWord(Str(step, "text"), At(step));
                return null;
            case "remove_alias":
                book.RemoveAlias(Str(step, "heard"), Str(step, "meant"), At(step));
                return null;
            case "clear":
                book.Clear();
                return null;
            default:
                throw new InvalidOperationException("unknown op " + Op(step));
        }
    }

    internal static string Op(JsonObject step) => Str(step, "op");

    internal static string Str(JsonObject step, string key) => step[key]!.GetValue<string>();

    internal static long At(JsonObject step) => step["at"]!.GetValue<long>();

    // ─── Wire forms ───

    internal static string Wire(TeachResult result) => result switch
    {
        TeachResult.Ok => "ok",
        TeachResult.Empty => "empty",
        TeachResult.TooLong => "too_long",
        TeachResult.SelfContaining => "self_containing",
        TeachResult.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    /// <summary>The projection in the golden file's shape and key order.</summary>
    internal static string ProjectionJson(LearningProjection projection)
    {
        var aliases = new JsonObject();
        foreach (var (heard, meant) in projection.Aliases)
        {
            aliases[heard] = meant;
        }

        return new JsonObject
        {
            ["words"] = new JsonArray(projection.Words.Select(w => (JsonNode?)w).ToArray()),
            ["aliases"] = aliases,
            ["pending"] = new JsonArray(projection.Pending
                .Select(p => (JsonNode?)new JsonObject { ["heard"] = p.Heard, ["meant"] = p.Meant, ["count"] = p.Count })
                .ToArray()),
            ["removed_words"] = new JsonArray(projection.RemovedWords.Select(w => (JsonNode?)w).ToArray()),
            ["removed_aliases"] = new JsonArray(projection.RemovedAliases
                .Select(r => (JsonNode?)new JsonArray(r.Heard, r.Meant))
                .ToArray()),
        }.ToJsonString();
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

}
