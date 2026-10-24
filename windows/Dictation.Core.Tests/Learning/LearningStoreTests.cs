using System.Text;
using System.Text.Json;
using Dictation.Core.Learning;

namespace Dictation.Core.Tests.Learning;

/// <summary>
/// <c>learning.json</c> on disk (LEARNING.md §1, §5): its shape, the
/// reference's own files, bad files kept aside, and replacement that a reader
/// never sees half of.
/// </summary>
public sealed class LearningStoreTests
{
    /// <summary>
    /// What <c>json.dumps(learning.state)</c> printed after a mixed run of the
    /// reference (2026-09-28): ASCII-escaped CJK, a taught and a learned alias,
    /// pending pairs, both kinds of tombstone.
    /// </summary>
    private const string WrittenByTheReference =
        """{"schema": "dictation.learning/1", "words": [{"text": "kubectl", "origin": "correction", "count": 1, "first_seen": 1790000000007, "last_seen": 1790000000007}, {"text": "SkyDock", "origin": "taught", "count": 0, "first_seen": 1790000000004, "last_seen": 1790000000004}, {"text": "Trovioo", "origin": "correction", "count": 1, "first_seen": 1790000000002, "last_seen": 1790000000002}], "aliases": [{"heard": "sky dock", "meant": "SkyDock", "origin": "taught", "count": 0, "first_seen": 1790000000004, "last_seen": 1790000000004}, {"heard": "阿里斯", "meant": "Miles", "origin": "correction", "count": 2, "first_seen": 1790000000000, "last_seen": 1790000000001}], "pending": [{"heard": "javis", "meant": "JARVIS", "count": 1, "dictations": ["d5"], "first_seen": 1790000000006, "last_seen": 1790000000006}, {"heard": "trovio", "meant": "Trovioo", "count": 1, "dictations": ["d3"], "first_seen": 1790000000002, "last_seen": 1790000000002}], "removed": {"words": [{"key": "jarvis", "at": 1790000000005}], "aliases": [{"heard": "xyz", "meant": "xyzzy", "at": 1790000000008}]}}""";

    /// <summary>The reference's <c>projection()</c> of that state.</summary>
    private const string ItsProjection =
        """{"words":["kubectl","SkyDock","Trovioo"],"aliases":{"sky dock":"SkyDock","阿里斯":"Miles"},"pending":[{"heard":"javis","meant":"JARVIS","count":1},{"heard":"trovio","meant":"Trovioo","count":1}],"removed_words":["jarvis"],"removed_aliases":[["xyz","xyzzy"]]}""";

    private static string Dir() => LearningGoldenFileTests.TempDirectory();

    private static LearningState Populated()
    {
        var state = LearningStore.Parse(Encoding.UTF8.GetBytes(WrittenByTheReference));
        Assert.NotNull(state);
        return state;
    }

    private static string Projection(LearningState state) =>
        LearningGoldenTests.ProjectionJson(
            new LearningBook(new WordListLexicon(LearningGoldenTests.SharedLexicon), state).Projection());

    private static string Canonical(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement,
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // ─── Reading and writing ───

    [Fact]
    public void No_file_is_empty_and_the_first_save_creates_it()
    {
        var dir = Path.Combine(Dir(), "not-yet");
        var store = new LearningStore(dir);

        var load = store.Load();

        Assert.Equal(LearningFileStatus.Missing, load.Status);
        Assert.Empty(load.State.Words);
        Assert.False(File.Exists(store.FilePath));
        Assert.True(store.Save(Populated()));
        Assert.Equal(Path.Combine(dir, "learning.json"), store.FilePath);
        Assert.Equal(Canonical(ItsProjection), Canonical(Projection(new LearningStore(dir).Load().State)));
    }

    [Fact]
    public void The_app_file_sits_beside_the_worker_alias_table()
    {
        var root = Dir();
        var store = LearningStore.ForUser(new DictationPaths(root));
        Assert.Equal(Path.Combine(root, "dictation", "learning.json"), store.FilePath);
        Assert.Equal(Path.Combine(root, "dictation", "learning.json.bad"), store.BadFilePath);
    }

    [Fact]
    public void A_file_the_reference_wrote_is_read_as_the_reference_reads_it()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, LearningStore.FileName), WrittenByTheReference);

        var load = new LearningStore(dir).Load();

        Assert.Equal(LearningFileStatus.Loaded, load.Status);
        Assert.Equal(Canonical(ItsProjection), Canonical(Projection(load.State)));
        var pending = load.State.Pending.Single(p => p.Heard == "javis");
        Assert.Equal(["d5"], pending.Dictations);
        Assert.Equal(1790000000006L, pending.FirstSeen);
    }

    [Fact]
    public void The_file_has_the_documented_keys_in_the_documented_order()
    {
        var dir = Dir();
        var store = new LearningStore(dir);
        store.Load();
        store.Save(Populated());

        using var file = JsonDocument.Parse(File.ReadAllBytes(store.FilePath));
        var root = file.RootElement;
        Assert.Equal(["schema", "words", "aliases", "pending", "removed"], Keys(root));
        Assert.Equal("dictation.learning/1", root.GetProperty("schema").GetString());
        Assert.Equal(["text", "origin", "count", "first_seen", "last_seen"], Keys(root.GetProperty("words")[0]));
        Assert.Equal(["heard", "meant", "origin", "count", "first_seen", "last_seen"], Keys(root.GetProperty("aliases")[0]));
        Assert.Equal(["heard", "meant", "count", "dictations", "first_seen", "last_seen"], Keys(root.GetProperty("pending")[0]));
        Assert.Equal(["words", "aliases"], Keys(root.GetProperty("removed")));
        Assert.Equal(["key", "at"], Keys(root.GetProperty("removed").GetProperty("words")[0]));
        Assert.Equal(["heard", "meant", "at"], Keys(root.GetProperty("removed").GetProperty("aliases")[0]));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("words")[0].GetProperty("last_seen").ValueKind);
        // Written as UTF-8 with no byte order mark, which Python's json reads.
        Assert.NotEqual(0xEF, File.ReadAllBytes(store.FilePath)[0]);
    }

    [Fact]
    public void Keys_this_version_does_not_know_are_written_back()
    {
        var dir = Dir();
        var withExtras = WrittenByTheReference
            .Replace("\"schema\": \"dictation.learning/1\",", "\"schema\": \"dictation.learning/1\", \"note\": {\"n\": 1},")
            .Replace("\"text\": \"kubectl\",", "\"text\": \"kubectl\", \"source\": \"x\",");
        File.WriteAllText(Path.Combine(dir, LearningStore.FileName), withExtras);
        var store = new LearningStore(dir);

        Assert.True(store.Save(store.Load().State));

        var text = File.ReadAllText(store.FilePath);
        Assert.Contains("\"note\": {", text);
        Assert.Contains("\"source\": \"x\"", text);
    }

    [Fact]
    public void Missing_lists_read_as_empty()
    {
        var state = LearningStore.Parse("""
            {"schema": "dictation.learning/1",
             "words": [{"text": "Trovioo", "origin": "taught", "count": 0, "first_seen": 1, "last_seen": 1}]}
            """u8);

        Assert.NotNull(state);
        Assert.Equal("Trovioo", Assert.Single(state.Words).Text);
        Assert.Empty(state.Aliases);
        Assert.Empty(state.Pending);
        Assert.Empty(state.Removed.Words);
    }

    [Fact]
    public void A_store_that_has_not_read_the_file_does_not_replace_it()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, LearningStore.FileName), WrittenByTheReference);

        Assert.False(new LearningStore(dir).Save(new LearningState()));

        Assert.Equal(WrittenByTheReference, File.ReadAllText(Path.Combine(dir, LearningStore.FileName)));
    }

    // ─── Bad files ───

    [Theory]
    [InlineData("{\"schema\": \"dictation.learning/1\", \"words\": [")]            // truncated
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schema\": \"dictation.learning/2\"}")]                          // another schema
    [InlineData("{\"words\": []}")]                                                 // no schema
    [InlineData("{\"schema\": \"dictation.learning/1\", \"words\": 5}")]            // wrong type
    [InlineData("{\"schema\": \"dictation.learning/1\", \"words\": [null]}")]      // a hole
    [InlineData("{\"schema\": \"dictation.learning/1\", \"words\": [{\"origin\": \"taught\"}]}")] // no text
    [InlineData("{\"schema\": \"dictation.learning/1\", \"words\": [{\"text\": \"a\", \"origin\": \"taught\", \"count\": 0, \"first_seen\": 1}]}")] // no last_seen
    [InlineData("{\"schema\": \"dictation.learning/1\", \"pending\": [{\"heard\": \"a\", \"meant\": \"b\", \"count\": 1, \"dictations\": [null], \"first_seen\": 1, \"last_seen\": 1}]}")]
    [InlineData("{\"schema\": \"dictation.learning/1\", \"words\": [{\"text\": \"a\", \"origin\": \"taught\", \"count\": 1.5, \"first_seen\": 1, \"last_seen\": 1}]}")]
    [InlineData("{\"schema\": \"dictation.learning/1\", \"words\": [{\"text\": null, \"origin\": \"taught\", \"count\": 1, \"first_seen\": 1, \"last_seen\": 1}]}")]
    [InlineData("{\"schema\": null}")]
    [InlineData("{\"schema\": \"dictation.learning/1\",}")]                         // Python rejects trailing commas
    public void A_bad_file_is_kept_aside_and_the_store_starts_empty(string content)
    {
        var dir = Dir();
        var path = Path.Combine(dir, LearningStore.FileName);
        File.WriteAllText(path, content);
        var store = new LearningStore(dir);

        var load = store.Load();

        Assert.Equal(LearningFileStatus.KeptAside, load.Status);
        Assert.Empty(load.State.Words);
        Assert.False(File.Exists(path));
        Assert.Equal(content, File.ReadAllText(store.BadFilePath));

        // And the store now writes a good file without touching the bad one.
        Assert.True(store.Save(Populated()));
        Assert.Equal(LearningFileStatus.Loaded, new LearningStore(dir).Load().Status);
        Assert.Equal(content, File.ReadAllText(store.BadFilePath));
    }

    [Fact]
    public void Bytes_that_are_not_UTF8_or_start_with_a_byte_order_mark_are_a_bad_file()
    {
        Assert.Null(LearningStore.Parse([.. "{\"schema\": \"dictation.learning/1\", \"words\": [{\"text\": \""u8, 0xFF, .. "\"}]}"u8]));
        Assert.Null(LearningStore.Parse([0xEF, 0xBB, 0xBF, .. "{\"schema\": \"dictation.learning/1\"}"u8]));
        Assert.NotNull(LearningStore.Parse("{\"schema\": \"dictation.learning/1\"}"u8));
    }

    [Fact]
    public void A_newer_bad_file_replaces_an_older_one()
    {
        var dir = Dir();
        var store = new LearningStore(dir);
        File.WriteAllText(store.BadFilePath, "the older bad file");
        File.WriteAllText(store.FilePath, "the newer bad file");

        Assert.Equal(LearningFileStatus.KeptAside, store.Load().Status);
        Assert.Equal("the newer bad file", File.ReadAllText(store.BadFilePath));
    }

    [Fact]
    public void A_bad_file_that_cannot_be_moved_aside_is_left_alone_and_never_overwritten()
    {
        var dir = Dir();
        var store = new LearningStore(dir);
        File.WriteAllText(store.FilePath, "garbage the user may want back");
        Directory.CreateDirectory(store.BadFilePath);
        File.WriteAllText(Path.Combine(store.BadFilePath, "occupied"), "x");

        var load = store.Load();

        Assert.Equal(LearningFileStatus.Unreadable, load.Status);
        Assert.Empty(load.State.Words);
        Assert.False(store.Save(Populated()));
        Assert.Equal("garbage the user may want back", File.ReadAllText(store.FilePath));
    }

    [UnreadableFileFact]
    public void A_file_that_cannot_be_read_is_not_overwritten_until_it_can_be()
    {
        var dir = Dir();
        var store = new LearningStore(dir);
        File.WriteAllText(store.FilePath, WrittenByTheReference);

        using (UnreadableFileFactAttribute.Lock(store.FilePath))
        {
            Assert.Equal(LearningFileStatus.Unreadable, store.Load().Status);
            Assert.False(store.Save(new LearningState()));
        }

        Assert.Equal(WrittenByTheReference, File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(store.BadFilePath));
        Assert.Equal(LearningFileStatus.Loaded, store.Load().Status);
        Assert.True(store.Save(new LearningState()));
    }

    // ─── Replacement ───

    [Fact]
    public void A_save_the_disk_refuses_returns_false_and_throws_nothing()
    {
        var parent = Dir();
        var blocked = Path.Combine(parent, "dictation");
        File.WriteAllText(blocked, "a file where the directory should be");
        var store = new LearningStore(blocked);
        store.Load();

        Assert.False(store.Save(Populated()));
    }

    [Fact]
    public void Saves_leave_no_temporary_files()
    {
        var dir = Dir();
        var store = new LearningStore(dir);
        store.Load();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(store.Save(Populated()));
        }

        Assert.Equal([LearningStore.FileName], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void A_reader_never_sees_part_of_a_file()
    {
        // The worker re-reads learning.json whenever its size or mtime moves.
        // Two states of different sizes, saved back and forth while a reader
        // parses the file as fast as it can: every read must be a whole file.
        var dir = Dir();
        var store = new LearningStore(dir);
        store.Load();
        var small = Populated();
        var large = Populated();
        for (var i = 0; i < 400; i++)
        {
            large.Pending.Add(new PendingAlias
            {
                Heard = $"heard {i}", Meant = $"Meant{i}", Count = 1, Dictations = [$"id-{i}"], FirstSeen = i, LastSeen = i,
            });
        }

        Assert.True(store.Save(small));
        var stop = false;
        var reads = 0;
        var torn = 0;
        using var reading = new ManualResetEventSlim();
        // Its own thread: on a busy thread pool a task could start after the
        // writes are over and prove nothing.
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    using var stream = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    if (LearningStore.Parse(buffer.ToArray()) is null)
                    {
                        Interlocked.Increment(ref torn);
                    }

                    Interlocked.Increment(ref reads);
                    reading.Set();
                }
                catch (IOException)
                {
                    // Windows: the reader lost a race with the replace. A retry
                    // reads the new file, which is what the worker does too.
                }
            }
        }) { IsBackground = true };
        reader.Start();
        Assert.True(reading.Wait(TimeSpan.FromSeconds(10)), "the reader never read the file");

        var readsBefore = Volatile.Read(ref reads);
        var saves = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (saves < 150 || (Volatile.Read(ref reads) - readsBefore < 200 && clock.Elapsed < TimeSpan.FromSeconds(20)))
        {
            Assert.True(store.Save(saves++ % 2 == 0 ? large : small));
        }

        var readsDuring = Volatile.Read(ref reads) - readsBefore;
        Volatile.Write(ref stop, true);
        reader.Join();
        Assert.True(readsDuring >= 200, $"the reader only managed {readsDuring} reads during {saves} saves");
        Assert.Equal(0, torn);
    }

    [Dictation.TestSupport.WindowsOnlyFact("replacing a file another handle holds open without delete sharing fails only on Windows")]
    public void A_save_waits_briefly_for_a_reader_that_holds_the_file_open()
    {
        // FileShare.ReadWrite without Delete is how Python's open() shares the
        // file when the worker reads it.
        var dir = Dir();
        var store = new LearningStore(dir);
        store.Load();
        Assert.True(store.Save(Populated()));
        var reader = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(() =>
        {
            Thread.Sleep(15);
            reader.Dispose();
        });

        Assert.True(store.Save(new LearningState()));
        release.Wait();
        Assert.Empty(new LearningStore(dir).Load().State.Words);
    }

    [Dictation.TestSupport.WindowsOnlyFact("replacing a file another handle holds open without delete sharing fails only on Windows")]
    public void A_reader_that_never_lets_go_costs_the_save_not_an_exception()
    {
        var dir = Dir();
        var store = new LearningStore(dir);
        store.Load();
        Assert.True(store.Save(Populated()));

        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.False(store.Save(new LearningState()));
        }

        Assert.Equal(3, new LearningStore(dir).Load().State.Words.Count);
        Assert.Equal([LearningStore.FileName], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    private static List<string> Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToList();
}

/// <summary>
/// A fact that needs a file this process cannot read: a permission on POSIX, a
/// sharing lock on Windows. Skipped as root, which reads regardless.
/// </summary>
public sealed class UnreadableFileFactAttribute : FactAttribute
{
    public UnreadableFileFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
        {
            Skip = "root reads a file whatever its permissions";
        }
    }

    /// <summary>Make <paramref name="path"/> unreadable until disposed.</summary>
    public static IDisposable Lock(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new Restore(() => File.SetUnixFileMode(path, mode));
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
