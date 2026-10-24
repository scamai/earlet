using Dictation.Core.Learning;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// Undo of the last learned change at the learning facade: who gets a token,
/// what Undo puts back, and every change that expires it. The macOS facade's
/// contract (<c>DictationLearning.undoLearning</c>,
/// <c>DictationLearningFacadeTests</c>), against the real file.
/// </summary>
public sealed class LearningUndoTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private static WordListLexicon Lexicon => new(LearningGoldenTests.SharedLexicon);

    /// <summary>Learning over a fresh folder: <c>Dir</c> is where the file
    /// goes, under a root that can be blocked.</summary>
    private static (DictationLearning Learning, string Dir) Make()
    {
        var dir = Path.Combine(LearningGoldenFileTests.TempDirectory(), "dictation");
        return (new DictationLearning(new LearningStore(dir), Lexicon), dir);
    }

    /// <summary>The file as text, or null when there is none.</summary>
    private static string? OnDisk(string dir)
    {
        var file = Path.Combine(dir, LearningStore.FileName);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    private static LearningState State(string dir)
    {
        var load = new LearningStore(dir).Load();
        Assert.Equal(LearningFileStatus.Loaded, load.Status);
        return load.State;
    }

    /// <summary>A file where the store's folder should be: every save is
    /// refused until it is deleted.</summary>
    private static void Block(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        File.WriteAllText(dir, "a file where the folder should be");
    }

    private static Guid Learn(DictationLearning learning, string id = "one", int second = 0) =>
        learning.Observe(id, "we use trovio", "we use Trovioo", T.AddSeconds(second)).UndoToken
        ?? throw new InvalidOperationException("no token for a saved new word");

    // ─── Who gets a token ───────────────────────────────────────────────

    [Fact]
    public void A_saved_new_word_gets_a_token_and_undo_restores_the_file_as_it_was()
    {
        var (learning, dir) = Make();
        Assert.Equal(TeachResult.Ok, learning.Teach("SkyDock", null, T));
        var before = OnDisk(dir);

        var token = Learn(learning);
        Assert.Contains("Trovioo", OnDisk(dir), StringComparison.Ordinal);

        Assert.Equal(LearningWrite.Saved, learning.UndoLearning(token));
        Assert.Equal(before, OnDisk(dir));
        Assert.Equal(["SkyDock"], learning.Snapshot().Words.Select(w => w.Text));
        Assert.False(learning.Snapshot().Unsaved);
    }

    [Fact]
    public void The_first_word_ever_learned_undoes_to_an_empty_vocabulary()
    {
        var (learning, dir) = Make();

        Assert.Equal(LearningWrite.Saved, learning.UndoLearning(Learn(learning)));

        var state = State(dir);
        Assert.Empty(state.Words);
        Assert.Empty(state.Aliases);
        Assert.Empty(state.Pending);
    }

    [Fact]
    public void Undo_restores_the_previous_alias_and_word_counts_not_an_empty_list()
    {
        var (learning, dir) = Make();
        learning.Teach("SkyDock", "sky dock", T);
        Learn(learning, "one");
        var before = OnDisk(dir);

        var token = Learn(learning, "two", 1);
        Assert.Contains(State(dir).Aliases, a => a.Heard == "trovio");

        Assert.Equal(LearningWrite.Saved, learning.UndoLearning(token));
        Assert.Equal(before, OnDisk(dir));
        Assert.Contains(learning.Snapshot().Words, w => w.Text == "SkyDock");
        Assert.Contains(learning.Snapshot().Aliases, a => a.Heard == "sky dock");
    }

    [Fact]
    public void Undoing_a_newly_winning_alias_restores_its_previous_contested_state()
    {
        var (learning, dir) = Make();
        var seed = new LearningStore(dir);
        seed.Load();
        var now = T.ToUnixTimeMilliseconds();
        var existing = new LearningState();
        existing.Words.Add(new LearnedWord { Text = "Trovion", Origin = LearningOrigins.Correction, Count = 2, FirstSeen = now, LastSeen = now });
        existing.Aliases.Add(new LearnedAlias { Heard = "trovio", Meant = "Trovion", Origin = LearningOrigins.Correction, Count = 2, FirstSeen = now, LastSeen = now });
        Assert.True(seed.Save(existing));

        learning.Observe("first", "we use trovio", "we use Trovioo", T);
        learning.Observe("second", "we use trovio", "we use Trovioo", T);
        learning.Observe("third", "we use trovio", "we use Trovioo", T);
        var before = OnDisk(dir);
        Assert.Empty(State(dir).Aliases); // three against two is still contested

        var fourth = learning.Observe("fourth", "we use trovio", "we use Trovioo", T);
        Assert.Equal("Trovioo", State(dir).Aliases.Single().Meant);
        Assert.NotNull(fourth.UndoToken);

        Assert.Equal(LearningWrite.Saved, learning.UndoLearning(fourth.UndoToken!.Value));
        Assert.Equal(before, OnDisk(dir));
        Assert.Empty(State(dir).Aliases);
    }

    [Fact]
    public void Nothing_learned_a_pending_pair_and_a_repeated_correction_get_no_token()
    {
        var (learning, _) = Make();

        Assert.Null(learning.Observe("none", "we use it", "we use it", T).UndoToken);
        Assert.Null(learning.Observe("rejected", "we have a big meeting", "we have a large meeting", T).UndoToken);
        var pending = learning.Observe("secret", "the wifi password is sunshine", "the wifi password is Sunshine42x!", T);
        Assert.Equal((LearningWrite.Saved, 1, 0), (pending.Write, pending.PendingCorrections, pending.NewWords));
        Assert.Null(pending.UndoToken);

        Learn(learning);
        var repeated = learning.Observe("one", "we use trovio", "we use Trovioo", T.AddSeconds(1));
        Assert.Equal((0, 0), (repeated.NewWords, repeated.NewAliases));
        Assert.Null(repeated.UndoToken);
    }

    [Fact]
    public void A_change_the_disk_refused_gets_no_token()
    {
        var (learning, dir) = Make();
        learning.Teach("SkyDock", null, T);
        Block(dir);

        var refused = learning.Observe("one", "we use trovio", "we use Trovioo", T);

        Assert.Equal((LearningWrite.NotSaved, 1), (refused.Write, refused.NewWords));
        Assert.Null(refused.UndoToken);
    }

    /// <summary>While the file cannot be read nothing is saved, so nothing
    /// is offered; once it reads, the changes made meanwhile are replayed, and
    /// the next learned change undoes to that replayed list, not to the file
    /// as it was before.</summary>
    [UnreadableFileFact]
    public void A_file_that_could_not_be_read_gives_no_token_and_a_later_undo_keeps_what_was_replayed()
    {
        var (learning, dir) = Make();
        learning.Teach("Kubectl", null, T);
        var file = Path.Combine(dir, LearningStore.FileName);
        var reopened = new DictationLearning(new LearningStore(dir), Lexicon);

        using (UnreadableFileFactAttribute.Lock(file))
        {
            var outcome = reopened.Observe("one", "we use trovio", "we use Trovioo", T);
            Assert.Equal(LearningFileStatus.Unreadable, reopened.Snapshot().File);
            Assert.Equal((LearningWrite.NotSaved, 1), (outcome.Write, outcome.NewWords));
            Assert.Null(outcome.UndoToken);
        }

        var replayed = reopened.Observe("two", "we use skidek", "we use SkyDock", T.AddSeconds(1));
        Assert.Equal((LearningWrite.Saved, 1), (replayed.Write, replayed.NewWords));
        Assert.NotNull(replayed.UndoToken);

        Assert.Equal(LearningWrite.Saved, reopened.UndoLearning(replayed.UndoToken!.Value));
        Assert.Equal(["Kubectl", "Trovioo"], State(dir).Words.Select(w => w.Text).Order(StringComparer.Ordinal));
    }

    // ─── What expires it ────────────────────────────────────────────────

    public static TheoryData<string> Changes => new()
    {
        "teach", "remove a word", "remove an alias", "forget all", "a later learned word", "a later pending pair",
    };

    /// <summary>The Mac's "a later manual edit expires the undo token and
    /// cannot be overwritten", for every kind of later change.</summary>
    [Theory]
    [MemberData(nameof(Changes))]
    public void A_later_change_expires_the_token_and_cannot_be_overwritten(string change)
    {
        var (learning, dir) = Make();
        learning.Teach("Kubectl", "cube control", T);
        var token = Learn(learning);

        switch (change)
        {
            case "teach":
                Assert.Equal(TeachResult.Ok, learning.Teach("SkyDock", null, T));
                break;
            case "remove a word":
                learning.RemoveWord("Kubectl", T);
                break;
            case "remove an alias":
                learning.RemoveAlias("cube control", "Kubectl", T);
                break;
            case "forget all":
                learning.Clear();
                break;
            case "a later learned word":
                Assert.NotNull(learning.Observe("two", "we use skidek", "we use SkyDock", T).UndoToken);
                break;
            default:
                var pending = learning.Observe("two", "the wifi password is sunshine", "the wifi password is Sunshine42x!", T);
                Assert.Equal((LearningWrite.Saved, 1), (pending.Write, pending.PendingCorrections));
                break;
        }

        var after = OnDisk(dir);
        Assert.Null(learning.UndoLearning(token));
        Assert.Equal(after, OnDisk(dir));
    }

    [Fact]
    public void An_observation_that_changed_nothing_and_a_flush_keep_the_token()
    {
        var (learning, dir) = Make();
        var token = Learn(learning);

        Assert.Equal(LearningWrite.Unchanged, learning.Observe("same", "we use it", "we use it", T.AddSeconds(1)).Write);
        Assert.Equal(TeachResult.Empty, learning.Teach("   ", null, T.AddSeconds(2)));
        Assert.Equal(LearningWrite.Unchanged, learning.Flush());

        Assert.Equal(LearningWrite.Saved, learning.UndoLearning(token));
        Assert.Empty(State(dir).Words);
    }

    [Fact]
    public void A_token_undoes_once_and_no_other_token_undoes_anything()
    {
        var (learning, dir) = Make();
        var token = Learn(learning);

        Assert.Null(learning.UndoLearning(Guid.NewGuid()));
        Assert.Contains("Trovioo", OnDisk(dir), StringComparison.Ordinal);

        Assert.Equal(LearningWrite.Saved, learning.UndoLearning(token));
        Learn(learning, "two", 1);
        var relearned = OnDisk(dir);
        Assert.Null(learning.UndoLearning(token));
        Assert.Equal(relearned, OnDisk(dir));
    }

    // ─── Saving it ──────────────────────────────────────────────────────

    /// <summary>The Mac's "an undo whose save fails stays unsaved until flush
    /// and never reports saved success", at the facade.</summary>
    [Fact]
    public void An_undo_whose_save_fails_stays_unsaved_in_memory_until_a_flush_saves_it()
    {
        var (learning, dir) = Make();
        var token = Learn(learning);
        var learned = OnDisk(dir);
        var root = Path.GetDirectoryName(dir)!;
        var aside = root + ".aside";
        Directory.Move(root, aside);
        File.WriteAllText(root, "a file where the folder should be");

        Assert.Equal(LearningWrite.NotSaved, learning.UndoLearning(token));
        Assert.Empty(learning.Snapshot().Words);
        Assert.True(learning.Snapshot().Unsaved);

        File.Delete(root);
        Directory.Move(aside, root);
        Assert.Equal(learned, OnDisk(dir));
        Assert.Equal(LearningWrite.Saved, learning.Flush());
        Assert.Empty(State(dir).Words);
        Assert.False(learning.Snapshot().Unsaved);
    }

    [Fact]
    public void An_expired_undo_reads_and_writes_nothing_not_even_a_waiting_retry()
    {
        var (learning, dir) = Make();
        var token = Learn(learning);
        var learned = OnDisk(dir);
        Block(dir);
        Assert.Equal(LearningWrite.NotSaved, learning.RemoveWord("Trovioo", T));
        File.Delete(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, LearningStore.FileName), learned);

        Assert.Null(learning.UndoLearning(token));

        // The removal is still waiting: the refused undo did not save it.
        Assert.Equal(learned, OnDisk(dir));
        Assert.True(learning.Snapshot().Unsaved);
    }

    [Fact]
    public void Undo_tells_the_window_the_list_changed()
    {
        var (learning, _) = Make();
        var token = Learn(learning);
        var changes = 0;
        learning.Changed += (_, _) => changes++;

        learning.UndoLearning(token);
        learning.UndoLearning(token);

        Assert.Equal(1, changes);
    }
}
