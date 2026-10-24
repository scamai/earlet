using System.Text.Json.Nodes;
using Dictation.Core.Learning;
using static Dictation.Core.Tests.Learning.LearningGoldenTests;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// The golden sequences again, through <c>learning.json</c>: the state each one
/// leaves survives the store file, and the app facade leaves the golden
/// projection in the file the worker reads.
/// </summary>
public sealed class LearningGoldenFileTests
{
    public static TheoryData<string> GoldenSequences() => Sequences();

    [Theory]
    [MemberData(nameof(GoldenSequences))]
    public void A_sequence_state_survives_the_store_file(string name)
    {
        var golden = Case("sequences", name);
        var book = new LearningBook(new WordListLexicon(LexiconOf(golden)));
        Run(golden, step => Apply(book, step));
        var expected = ProjectionJson(book.Projection());

        var dir = TempDirectory();
        var writer = new LearningStore(dir);
        writer.Load();
        Assert.True(writer.Save(book.State));

        var load = new LearningStore(dir).Load();
        Assert.Equal(LearningFileStatus.Loaded, load.Status);
        Assert.Equal(LearningLimits.Schema, load.State.Schema);
        var reloaded = new LearningBook(new WordListLexicon(SharedLexicon), load.State);
        Assert.Equal(expected, ProjectionJson(reloaded.Projection()));
        Assert.Equal(golden["expect"]!.ToJsonString(), expected);
    }

    [Theory]
    [MemberData(nameof(GoldenSequences))]
    public void A_sequence_through_the_app_facade_lands_in_the_file(string name)
    {
        // The value the product hands the worker is the FILE, so read it back
        // with a fresh store rather than asking the facade what it holds.
        var golden = Case("sequences", name);
        var dir = TempDirectory();
        var learning = new DictationLearning(new LearningStore(dir), new WordListLexicon(LexiconOf(golden)));
        var results = Run(golden, step => ApplyToFacade(learning, step));

        Assert.Equal(ExpectedTeachResults(golden), results);
        // Missing only when no step changed anything; then the file's absence
        // must itself be the expected (empty) projection.
        var load = new LearningStore(dir).Load();
        Assert.True(load.Status is LearningFileStatus.Loaded or LearningFileStatus.Missing, load.Status.ToString());
        Assert.Equal(
            golden["expect"]!.ToJsonString(),
            ProjectionJson(new LearningBook(new WordListLexicon(SharedLexicon), load.State).Projection()));
    }

    private static string? ApplyToFacade(DictationLearning learning, JsonObject step)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(Op(step) == "clear" ? 0 : At(step));
        switch (Op(step))
        {
            case "observe":
                learning.Observe(Str(step, "dictation"), Str(step, "dictated"), Str(step, "edited"), at);
                return null;
            case "teach":
                return Wire(learning.Teach(Str(step, "written"), step["sounds_like"]?.GetValue<string>(), at));
            case "remove_word":
                learning.RemoveWord(Str(step, "text"), at);
                return null;
            case "remove_alias":
                learning.RemoveAlias(Str(step, "heard"), Str(step, "meant"), at);
                return null;
            case "clear":
                learning.Clear();
                return null;
            default:
                throw new InvalidOperationException("unknown op " + Op(step));
        }
    }

    internal static string TempDirectory() => Directory.CreateTempSubdirectory("dictation-learning-").FullName;
}
