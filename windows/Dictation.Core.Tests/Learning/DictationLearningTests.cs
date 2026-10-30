using System.Collections.Concurrent;
using Dictation.Core.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// The facade the app composes: what it tells the window, what reaches the
/// file, what reaches the log, and what several threads at once do to it.
/// </summary>
public sealed class DictationLearningTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private static WordListLexicon Lexicon => new(LearningGoldenTests.SharedLexicon);

    private static (DictationLearning Learning, LearningStore Store, string Dir) Make(
        TimeProvider? clock = null, ILogger<DictationLearning>? log = null, ILogger<LearningStore>? storeLog = null)
    {
        var dir = LearningGoldenFileTests.TempDirectory();
        var store = new LearningStore(dir, storeLog);
        return (new DictationLearning(store, Lexicon, clock, log), store, dir);
    }

    private static LearningState OnDisk(string dir)
    {
        var load = new LearningStore(dir).Load();
        Assert.Equal(LearningFileStatus.Loaded, load.Status);
        return load.State;
    }

    // ─── Operations ───

    [Fact]
    public void Nothing_is_read_or_written_until_first_use()
    {
        var (learning, store, dir) = Make();
        File.WriteAllText(store.FilePath, "not json");

        Assert.True(File.Exists(store.FilePath));
        Assert.False(File.Exists(store.BadFilePath));

        var snapshot = learning.Snapshot();
        Assert.Equal(LearningFileStatus.KeptAside, snapshot.File);
        Assert.True(File.Exists(store.BadFilePath));
        Assert.Empty(Directory.GetFiles(dir, LearningStore.FileName));
    }

    [Fact]
    public void Observe_reports_counts_and_saves_what_it_learned()
    {
        var (learning, _, dir) = Make();

        // The word at once, and javis → Jarvis waiting for a second dictation.
        var first = learning.Observe("d1", "ask javis to deploy the sever", "ask Jarvis to deploy the server", T);
        Assert.NotNull(first.UndoToken);
        Assert.Equal(
            new LearningObservation(2, 0, 1, 0, LearningWrite.Saved, PendingCorrections: 1, first.UndoToken), first);

        // The second dictation makes it an alias: nothing of d2's is left waiting.
        var second = learning.Observe("d2", "ask javis now", "ask Jarvis now", T.AddSeconds(1));
        Assert.NotNull(second.UndoToken);
        Assert.NotEqual(first.UndoToken, second.UndoToken);
        Assert.Equal(
            new LearningObservation(1, 0, 0, 1, LearningWrite.Saved, PendingCorrections: 0, second.UndoToken), second);

        var none = learning.Observe("d3", "we have a big meeting", "we have a large meeting", T.AddSeconds(2));
        Assert.Equal(new LearningObservation(0, 1, 0, 0, LearningWrite.Unchanged, PendingCorrections: 0), none);

        var alias = Assert.Single(OnDisk(dir).Aliases);
        Assert.Equal(("javis", "Jarvis", LearningOrigins.Correction, 2L), (alias.Heard, alias.Meant, alias.Origin, alias.Count));
    }

    [Fact]
    public void Pending_corrections_count_only_what_this_dictation_left_waiting()
    {
        var (learning, _, _) = Make();
        Assert.Equal(1, learning.Observe("other", "the wifi password is sunshine", "the wifi password is Sunshine42x!", T)
            .PendingCorrections);
        learning.Observe("d1", "we use trovio", "we use Trovioo", T.AddSeconds(1));
        learning.Observe("d2", "we use trovio", "we use Trovioo", T.AddSeconds(2));

        // Known by now: nothing of d3's waits, though another dictation's pair still does.
        var known = learning.Observe("d3", "we use trovio", "we use Trovioo", T.AddSeconds(3));

        Assert.Equal(1, learning.Snapshot().Pending);
        Assert.Equal(0, known.PendingCorrections);
    }

    [Fact]
    public void An_observation_with_nothing_to_learn_creates_no_file()
    {
        var (learning, store, _) = Make();

        var outcome = learning.Observe("d1", "we use trovio", "we use trovio", T);

        Assert.Equal(new LearningObservation(0, 0, 0, 0, LearningWrite.Unchanged, PendingCorrections: 0), outcome);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Teach_remove_and_clear_reach_the_file()
    {
        var (learning, _, dir) = Make();

        Assert.Equal(TeachResult.Ok, learning.Teach("Trovioo", "true leo", T));
        Assert.Equal(["Trovioo"], OnDisk(dir).Words.Select(w => w.Text));
        Assert.Equal(["true leo"], OnDisk(dir).Aliases.Select(a => a.Heard));

        Assert.Equal(TeachResult.SelfContaining, learning.Teach("Fable Flow", "flow", T));

        Assert.Equal(LearningWrite.Saved, learning.RemoveAlias("true leo", "Trovioo", T.AddSeconds(1)));
        Assert.Empty(OnDisk(dir).Aliases);
        Assert.Equal(["Trovioo"], OnDisk(dir).Words.Select(w => w.Text));

        Assert.Equal(LearningWrite.Saved, learning.RemoveWord("Trovioo", T.AddSeconds(2)));
        Assert.Empty(OnDisk(dir).Words);
        Assert.Equal(["trovioo"], OnDisk(dir).Removed.Words.Select(r => r.Key));

        Assert.Equal(LearningWrite.Saved, learning.Clear());
        Assert.Empty(OnDisk(dir).Removed.Words);
        Assert.Equal(LearningWrite.Unchanged, learning.Clear());
    }

    [Fact]
    public void The_snapshot_lists_what_the_window_shows()
    {
        var (learning, _, _) = Make();
        learning.Observe("d1", "we use trovio", "we use Trovioo", T);
        learning.Observe("d2", "we use trovio", "we use Trovioo", T.AddSeconds(1));
        learning.Teach("SkyDock", "sky dock", T.AddSeconds(2));
        learning.Observe("d3", "ask javis", "ask Jarvis", T.AddSeconds(3));

        var snapshot = learning.Snapshot();

        Assert.Equal(
            [
                new LearnedWordEntry("Jarvis", LearningOrigin.Correction, 1, T.AddSeconds(3), "Learned from one fix, not yet confirmed"),
                new LearnedWordEntry("SkyDock", LearningOrigin.Taught, 0, T.AddSeconds(2)),
                new LearnedWordEntry("Trovioo", LearningOrigin.Correction, 2, T.AddSeconds(1), "Confirmed by 2 or 3 of your fixes"),
            ],
            snapshot.Words);
        Assert.Equal(
            [
                new LearnedAliasEntry("sky dock", "SkyDock", LearningOrigin.Taught),
                new LearnedAliasEntry("trovio", "Trovioo", LearningOrigin.Correction),
            ],
            snapshot.Aliases);
        Assert.Equal(1, snapshot.Pending);
        Assert.Equal(LearningFileStatus.Missing, snapshot.File);
        Assert.False(snapshot.Unsaved);
    }

    [Fact]
    public void An_operation_without_a_time_uses_the_clock()
    {
        var clock = new ManualClock(T.AddDays(3));
        var (learning, _, dir) = Make(clock);

        learning.Teach("Trovioo");
        clock.Now = T.AddDays(4);
        learning.RemoveAlias("x y", "Xy");

        Assert.Equal(T.AddDays(3).ToUnixTimeMilliseconds(), OnDisk(dir).Words.Single().LastSeen);
        Assert.Equal(T.AddDays(4).ToUnixTimeMilliseconds(), OnDisk(dir).Removed.Aliases.Single().At);
    }

    [Fact]
    public void Changed_is_raised_for_a_change_and_not_for_a_no_op()
    {
        var (learning, _, _) = Make();
        var raised = 0;
        learning.Changed += (_, _) => raised++;

        learning.Observe("d1", "we use trovio", "we use trovio", T);
        Assert.Equal(0, raised);

        learning.Observe("d1", "we use trovio", "we use Trovioo", T);
        Assert.Equal(1, raised);

        learning.Teach("Fable Flow", "flow", T);
        Assert.Equal(1, raised);

        learning.Clear();
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Changed_is_raised_after_the_lock_is_released()
    {
        // The window's handler marshals to the UI thread and reads the
        // snapshot there. Raised inside the lock, that other thread would wait
        // on the lock while the raising thread waits on it.
        var (learning, _, _) = Make();
        LearningSnapshot? seen = null;
        learning.Changed += (_, _) =>
        {
            var read = Task.Run(learning.Snapshot);
            if (read.Wait(TimeSpan.FromSeconds(5)))
            {
                seen = read.Result;
            }
        };

        learning.Observe("d1", "we use trovio", "we use Trovioo", T);

        Assert.NotNull(seen);
        Assert.Equal("Trovioo", Assert.Single(seen.Words).Text);
    }

    // ─── When the disk says no ───

    [Fact]
    public void A_refused_save_keeps_the_change_says_so_and_is_retried_by_the_next_operation()
    {
        var root = LearningGoldenFileTests.TempDirectory();
        var dir = Path.Combine(root, "dictation");
        File.WriteAllText(dir, "a file where the directory should be");
        var learning = new DictationLearning(new LearningStore(dir), Lexicon);

        var outcome = learning.Observe("d1", "we use trovio", "we use Trovioo", T);

        Assert.Equal(LearningWrite.NotSaved, outcome.Write);
        Assert.Equal(1, outcome.NewWords);
        Assert.True(learning.Snapshot().Unsaved);
        Assert.Equal("Trovioo", learning.Snapshot().Words.Single().Text);

        File.Delete(dir);
        var retry = learning.Observe("d2", "nothing to see", "nothing to see", T.AddSeconds(1));

        Assert.Equal(LearningWrite.Saved, retry.Write);
        Assert.False(learning.Snapshot().Unsaved);
        Assert.Equal(["Trovioo"], OnDisk(dir).Words.Select(w => w.Text));
    }

    /// <summary>A store whose file holds the taught word Kubectl.</summary>
    private static (DictationLearning Learning, LearningStore Store, string Dir) MakeWithKubectl(TimeProvider? clock = null)
    {
        var (learning, store, dir) = Make(clock);
        var seed = new LearningStore(dir);
        seed.Load();
        var existing = new LearningState();
        existing.Words.Add(new LearnedWord { Text = "Kubectl", Origin = LearningOrigins.Taught, LastSeen = 1 });
        Assert.True(seed.Save(existing));
        return (learning, store, dir);
    }

    [UnreadableFileFact]
    public void An_unreadable_file_is_retried_and_never_overwritten()
    {
        var (learning, store, dir) = MakeWithKubectl();
        var bytes = File.ReadAllBytes(store.FilePath);

        using (UnreadableFileFactAttribute.Lock(store.FilePath))
        {
            Assert.Equal(LearningFileStatus.Unreadable, learning.Snapshot().File);
            var outcome = learning.Observe("d1", "we use trovio", "we use Trovioo", T);
            Assert.Equal(LearningWrite.NotSaved, outcome.Write);
            var locked = learning.Snapshot();
            Assert.True(locked.Unsaved);
            Assert.Equal(["Trovioo"], locked.Words.Select(w => w.Text));
            Assert.Equal(1, locked.Pending);
        }

        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));

        // The second dictation's correction is the first's second: the alias
        // forms across the unreadable window.
        var second = learning.Observe("d2", "we use trovio", "we use Trovioo", T.AddSeconds(1));
        Assert.Equal((0, 1, LearningWrite.Saved), (second.NewWords, second.NewAliases, second.Write));
        var snapshot = learning.Snapshot();
        Assert.Equal(LearningFileStatus.Loaded, snapshot.File);
        Assert.False(snapshot.Unsaved);
        Assert.Equal(["Trovioo", "Kubectl"], snapshot.Words.Select(w => w.Text));
        Assert.Equal([new LearnedAliasEntry("trovio", "Trovioo", LearningOrigin.Correction)], snapshot.Aliases);
        Assert.Equal(0, snapshot.Pending);

        var disk = OnDisk(dir);
        Assert.Equal(["Trovioo", "Kubectl"], disk.Words.Select(w => w.Text));
        Assert.Equal(2, disk.Words[0].Count);
        Assert.Equal([("trovio", "Trovioo", 2L)], disk.Aliases.Select(a => (a.Heard, a.Meant, a.Count)));
    }

    [UnreadableFileFact]
    public void A_save_that_succeeds_on_retry_tells_the_window_the_list_is_saved_now()
    {
        var (learning, store, _) = MakeWithKubectl();
        learning.Snapshot();
        var raised = 0;
        learning.Changed += (_, _) => raised++;
        var folder = Path.GetDirectoryName(store.FilePath)!;

        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal(TeachResult.Ok, learning.Teach("Zupong", at: T));
            Assert.True(learning.Snapshot().Unsaved);
            Assert.Equal(1, raised);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Same list, now on disk: the "not saved yet" line must go away.
        Assert.Equal(LearningWrite.Saved, learning.Flush());
        Assert.False(learning.Snapshot().Unsaved);
        Assert.Equal(2, raised);
    }

    [UnreadableFileFact]
    public void A_word_taught_while_the_file_is_unreadable_is_listed_and_saved_once_it_reads()
    {
        var (learning, store, dir) = MakeWithKubectl();
        var raised = 0;
        learning.Changed += (_, _) => raised++;

        using (UnreadableFileFactAttribute.Lock(store.FilePath))
        {
            Assert.Equal(TeachResult.Ok, learning.Teach("Zupong", at: T));
            Assert.Equal(1, raised);
            var locked = learning.Snapshot();
            Assert.Equal(["Zupong"], locked.Words.Select(w => w.Text));
            Assert.Equal((LearningFileStatus.Unreadable, true), (locked.File, locked.Unsaved));

            // Still unreadable: the retry keeps what the session holds.
            Assert.Equal(LearningWrite.NotSaved, learning.Flush());
            Assert.Equal(["Zupong"], learning.Snapshot().Words.Select(w => w.Text));
        }

        // A snapshot shows what the session holds; the read is retried by a
        // change or a flush (LEARNING.md §5).
        Assert.Equal((LearningFileStatus.Unreadable, 1), (learning.Snapshot().File, learning.Snapshot().Words.Count));

        // Nothing changes this time, but the retried read brings Kubectl in.
        Assert.Equal(LearningWrite.Saved, learning.Flush());
        Assert.Equal(2, raised);
        var snapshot = learning.Snapshot();
        Assert.Equal((LearningFileStatus.Loaded, false), (snapshot.File, snapshot.Unsaved));
        Assert.Equal(["Zupong", "Kubectl"], snapshot.Words.Select(w => w.Text));
        Assert.Equal(["Zupong", "Kubectl"], OnDisk(dir).Words.Select(w => w.Text));
        Assert.Equal(LearningWrite.Unchanged, learning.Flush());
    }

    [UnreadableFileFact]
    public void A_file_that_stays_unreadable_still_learns_for_the_session()
    {
        var (learning, store, _) = MakeWithKubectl();
        var bytes = File.ReadAllBytes(store.FilePath);

        using (UnreadableFileFactAttribute.Lock(store.FilePath))
        {
            learning.Observe("d1", "we use trovio", "we use Trovioo", T);
            var second = learning.Observe("d2", "we use trovio", "we use Trovioo", T.AddSeconds(1));
            Assert.Equal((0, 1, LearningWrite.NotSaved), (second.NewWords, second.NewAliases, second.Write));
            Assert.Equal(["trovio"], learning.Snapshot().Aliases.Select(a => a.Heard));
            Assert.Equal(LearningWrite.NotSaved, learning.Flush());
            Assert.Equal(LearningWrite.NotSaved, learning.Flush());
            var snapshot = learning.Snapshot();
            Assert.Equal(["Trovioo"], snapshot.Words.Select(w => w.Text));
            Assert.Equal(["trovio"], snapshot.Aliases.Select(a => a.Heard));
        }

        Assert.Equal(bytes, File.ReadAllBytes(store.FilePath));
    }

    [UnreadableFileFact]
    public void A_replayed_change_keeps_the_time_it_was_made()
    {
        var clock = new ManualClock(T);
        var (learning, store, dir) = MakeWithKubectl(clock);

        using (UnreadableFileFactAttribute.Lock(store.FilePath))
        {
            learning.Teach("Zupong", "zoo pong");
            clock.Now = T.AddSeconds(5);
            learning.RemoveAlias("zoo pong", "Zupong");
            clock.Now = T.AddSeconds(7);
            learning.RemoveWord("Gone");
        }

        clock.Now = T.AddDays(9);
        learning.Flush();

        var disk = OnDisk(dir);
        Assert.Equal(T.ToUnixTimeMilliseconds(), disk.Words.Single(w => w.Text == "Zupong").LastSeen);
        Assert.Equal(T.AddSeconds(5).ToUnixTimeMilliseconds(), disk.Removed.Aliases.Single().At);
        Assert.Equal(("gone", T.AddSeconds(7).ToUnixTimeMilliseconds()), (disk.Removed.Words.Single().Key, disk.Removed.Words.Single().At));
    }

    [UnreadableFileFact]
    public void A_clear_made_while_unreadable_clears_what_the_file_held_once_it_reads()
    {
        var (learning, store, dir) = MakeWithKubectl();

        using (UnreadableFileFactAttribute.Lock(store.FilePath))
        {
            learning.Clear();
            learning.Teach("Zupong", at: T);
        }

        learning.Flush();
        Assert.Equal(["Zupong"], OnDisk(dir).Words.Select(w => w.Text));
    }

    // ─── The last save at quit ───

    [Fact]
    public void Flush_retries_a_refused_save_and_is_a_no_op_once_saved()
    {
        var root = LearningGoldenFileTests.TempDirectory();
        var dir = Path.Combine(root, "dictation");
        File.WriteAllText(dir, "a file where the directory should be");
        var learning = new DictationLearning(new LearningStore(dir), Lexicon);

        Assert.Equal(TeachResult.Ok, learning.Teach("Trovioo", at: T));
        Assert.True(learning.Snapshot().Unsaved);
        Assert.Equal(LearningWrite.NotSaved, learning.Flush());
        Assert.True(learning.Snapshot().Unsaved);

        File.Delete(dir);
        Assert.Equal(LearningWrite.Saved, learning.Flush());
        Assert.False(learning.Snapshot().Unsaved);
        Assert.Equal(["Trovioo"], OnDisk(dir).Words.Select(w => w.Text));
        Assert.Equal(LearningWrite.Unchanged, learning.Flush());
    }

    [Fact]
    public void Flush_before_first_use_reads_and_writes_nothing()
    {
        var (learning, store, dir) = Make();
        File.WriteAllText(store.FilePath, "not json");
        var raised = 0;
        learning.Changed += (_, _) => raised++;

        Assert.Equal(LearningWrite.Unchanged, learning.Flush());

        Assert.Equal(0, raised);
        Assert.Equal("not json", File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(store.BadFilePath));
        Assert.Equal([store.FilePath], Directory.GetFiles(dir));
    }

    // ─── Threads ───

    [Fact]
    public void Concurrent_observations_retain_the_newest_bounded_evidence_without_races()
    {
        const int threads = 8;
        const int each = 40;
        var (learning, _, dir) = Make();
        using var start = new Barrier(threads);

        // One shared correction (its word and alias counts must reach the
        // total) and one distinct pending pair per call (their number must).
        // The distinct pairs are transliterations to an ordinary word, so they
        // add no learned words that could push the shared one out.
        var failures = new ConcurrentQueue<Exception>();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (var i = 0; i < each; i++)
                {
                    var at = T.AddMilliseconds(t * each + i);
                    var heard = char.ConvertFromUtf32(0x4E00 + t * each + i) + "斯";
                    learning.Observe($"t{t}-{i}", "we use trovio", "we use Trovioo", at);
                    learning.Observe($"u{t}-{i}", $"我们用{heard}服务器", "我们用Miles服务器", at);
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
        }) { IsBackground = true }).ToList();
        workers.ForEach(w => w.Start());
        Assert.All(workers, w => Assert.True(w.Join(TimeSpan.FromMinutes(2)), "an observer did not finish"));
        Assert.Empty(failures);

        var disk = OnDisk(dir);
        Assert.Equal(256, Assert.Single(disk.Words).Count);
        Assert.Equal(256, Assert.Single(disk.Aliases).Count);
        Assert.Equal(256, disk.Pending.Count);
        Assert.All(disk.Pending, p => Assert.Equal("Miles", p.Meant));

        var snapshot = learning.Snapshot();
        Assert.Equal(256, snapshot.Pending);
        Assert.Equal(256, snapshot.Words.Single(w => w.Text == "Trovioo").Count);
    }

    // ─── Logs ───

    [Fact]
    public void Nothing_the_user_dictated_or_taught_reaches_the_log()
    {
        var log = new CapturingLogger<DictationLearning>();
        var storeLog = new CapturingLogger<LearningStore>();
        var (learning, store, _) = Make(log: log, storeLog: storeLog);
        File.WriteAllText(store.FilePath, "{\"schema\": \"dictation.learning/1\", \"words\": [{\"text\": \"Sentinelword\"},");

        learning.Snapshot();
        learning.Observe("secret-id", "we use sentinalword today", "we use Sentinelword today", T);
        learning.Observe("secret-id-2", "we use sentinalword today", "we use Sentinelword today", T.AddSeconds(1));
        learning.Teach("Sentinelthing", "sentinel thing", T);
        learning.RemoveAlias("sentinalword", "Sentinelword", T);
        learning.RemoveWord("Sentinelword", T);
        learning.Clear();
        learning.Flush();

        var lines = log.Lines.Concat(storeLog.Lines).ToList();
        Assert.True(lines.Count >= 8, $"only {lines.Count} lines were logged");
        Assert.Contains(lines, l => l.StartsWith("Flushed", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("1 new aliases", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            Assert.DoesNotContain("entinel", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("entinal", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-id", line, StringComparison.Ordinal);
            Assert.DoesNotContain("today", line, StringComparison.Ordinal);
        }
    }

    // ─── Fakes ───

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
        }
    }
}
