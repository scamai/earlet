using System.Text;
using Dictation.Services;
using Xunit;

namespace Dictation.Services.Tests;

/// <summary>
/// The user vocabulary store's rules, one file per test in an isolated
/// temp directory so filesystem state never leaks between tests.
/// </summary>
public sealed class DictationVocabularyTests : IDisposable
{
    private readonly string _dir;

    public DictationVocabularyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "dictation-vocab-tests-" + Guid.NewGuid().ToString("N"));

        // Created here, not per-test. Every test that writes a file into this
        // directory needs it to exist, and computing the path without creating
        // it made twelve of them fail with DirectoryNotFoundException from the
        // test's own arrange step rather than from anything under test.
        // Load_MissingDirectory is unaffected: it deliberately points at a
        // subpath of this one that is never created.
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // ── Missing file is empty, not an error ─────────────────────────────

    [Fact]
    public void Load_MissingFile_ReturnsEmptyWithNoReason()
    {
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Empty(result.Phrases);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Load_MissingDirectory_ReturnsEmptyWithNoReason()
    {
        // The directory itself may not exist yet on a fresh install — Load
        // must not throw just because nothing has ever been saved here.
        var neverCreated = Path.Combine(_dir, "does-not-exist-yet");
        var vocab = new DictationVocabulary(neverCreated);

        var result = vocab.Load();

        Assert.Empty(result.Phrases);
        Assert.Null(result.Reason);
    }

    // ── Round trip ───────────────────────────────────────────────────────

    [Fact]
    public void SaveThenLoad_RoundTripsThePhrases()
    {
        var vocab = new DictationVocabulary(_dir);
        var phrases = new[] { "Yurin Zhang", "Zupong", "Trovioo" };

        vocab.Save(phrases);
        var result = vocab.Load();

        Assert.Equal(phrases, result.Phrases);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void SaveThenLoad_UnicodePhrasesRoundTrip()
    {
        // This product's users have Chinese names — the file is UTF-8 and
        // must carry them exactly, not mangle or drop them.
        var vocab = new DictationVocabulary(_dir);
        var phrases = new[] { "张雨欣", "李明", "北京字节跳动" };

        vocab.Save(phrases);
        var result = vocab.Load();

        Assert.Equal(phrases, result.Phrases);
    }

    // ── Blank lines and comments ────────────────────────────────────────

    [Fact]
    public void Load_SkipsBlankLinesAndWhitespaceOnlyLines()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "Zupong\n\n   \nYurin Zhang\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong", "Yurin Zhang" }, result.Phrases);
    }

    [Fact]
    public void Load_SkipsCommentLinesStartingWithHash()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "# teammates\nZupong\n# not a real phrase\nYurin Zhang\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong", "Yurin Zhang" }, result.Phrases);
    }

    [Fact]
    public void Load_TrimsSurroundingWhitespaceFromEachPhrase()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "  Zupong  \n\tYurin Zhang\t\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong", "Yurin Zhang" }, result.Phrases);
    }

    // ── Dedupe ───────────────────────────────────────────────────────────

    [Fact]
    public void Load_DedupesCaseInsensitivelyKeepingFirstSeenCasing()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "Zupong\nZUPONG\nzupong\nZupong\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong" }, result.Phrases);
    }

    [Fact]
    public void Load_DedupePreservesOriginalOrderOfFirstOccurrence()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "Trovioo\nZupong\ntrovioo\nYurin Zhang\nZUPONG\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        // First-seen order and first-seen casing, not a re-sort.
        Assert.Equal(new[] { "Trovioo", "Zupong", "Yurin Zhang" }, result.Phrases);
    }

    [Fact]
    public void Load_KeepsStylisedCasingLikeAProperNoun()
    {
        // "iPhone" is not "Zupong" upper/title-cased — casing that is itself
        // part of what the user typed must survive normalisation untouched.
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "iPhone\neBay\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "iPhone", "eBay" }, result.Phrases);
    }

    // ── Ordering is stable across repeated loads ────────────────────────

    [Fact]
    public void Load_OrderingIsStableAcrossRepeatedCalls()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "Charlie\nAlpha\nBravo\n");
        var vocab = new DictationVocabulary(_dir);

        var first = vocab.Load().Phrases;
        var second = vocab.Load().Phrases;

        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" }, first);

        // Compared as sequences, not as two ImmutableArray values. Assert.Equal
        // with both sides typed ImmutableArray<string> binds to the generic
        // Assert.Equal<T>(T, T), whose default comparer for that struct is
        // reference equality on the underlying array — so two separate Loads
        // with identical contents fail with "Collections differ" printing two
        // identical collections.
        Assert.Equal<IEnumerable<string>>(first, second);
    }

    // ── Cap enforcement ──────────────────────────────────────────────────

    [Fact]
    public void Load_DropsAnOverLongPhraseEntirely_RatherThanTruncatingIt()
    {
        var tooLong = new string('a', DictationVocabulary.MaxPhraseLength + 1);
        var justRight = new string('b', DictationVocabulary.MaxPhraseLength);
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            $"{tooLong}\n{justRight}\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { justRight }, result.Phrases);
    }

    [Fact]
    public void Load_CapsPhraseCountAtMaxPhraseCount()
    {
        var lines = Enumerable.Range(0, DictationVocabulary.MaxPhraseCount + 20)
            .Select(i => $"phrase-{i:D3}");
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            string.Join('\n', lines));
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(DictationVocabulary.MaxPhraseCount, result.Phrases.Length);
        // The kept phrases are the first ones in the file, not an arbitrary
        // subset — a user editing near the top of their list should see
        // predictable behaviour.
        Assert.Equal("phrase-000", result.Phrases[0]);
        Assert.Equal($"phrase-{DictationVocabulary.MaxPhraseCount - 1:D3}", result.Phrases[^1]);
    }

    [Fact]
    public void Load_BlankAndCommentLinesDoNotCountAgainstTheCap()
    {
        var lines = new List<string>();
        for (var i = 0; i < DictationVocabulary.MaxPhraseCount; i++)
        {
            lines.Add("# comment " + i);
            lines.Add("");
            lines.Add($"phrase-{i:D3}");
        }
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            string.Join('\n', lines));
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(DictationVocabulary.MaxPhraseCount, result.Phrases.Length);
    }

    [Fact]
    public void Save_EnforcesTheSameCapsAsLoad()
    {
        var phrases = Enumerable.Range(0, DictationVocabulary.MaxPhraseCount + 5)
            .Select(i => $"phrase-{i:D3}");
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(phrases);
        var result = vocab.Load();

        Assert.Equal(DictationVocabulary.MaxPhraseCount, result.Phrases.Length);
    }

    // ── Unreadable / corrupt file degrades to empty, never throws ──────

    [Fact]
    public void Load_PathThatIsADirectoryNotAFile_DegradesToEmptyWithAReason()
    {
        // Simulates a corrupt/unreadable vocabulary file without relying on
        // OS-specific permission bits: a directory sitting where the file
        // should be can never be parsed as one, on any platform.
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, DictationVocabulary.DefaultFileName));
        var vocab = new DictationVocabulary(_dir);

        var exception = Record.Exception(() => vocab.Load());

        Assert.Null(exception);
        var result = vocab.Load();
        Assert.Empty(result.Phrases);
        Assert.NotNull(result.Reason);
        Assert.NotEmpty(result.Reason!);
    }

    // ── Atomicity ────────────────────────────────────────────────────────

    [Fact]
    public void Save_LeavesNoTempFileBehindOnSuccess()
    {
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(new[] { "Zupong" });

        Assert.False(File.Exists(vocab.FilePath + ".tmp"));
    }

    [Fact]
    public void Load_IgnoresALeftoverTempFile_EvenIfItLooksLikeAVocabulary()
    {
        // A crash between "stage the temp file" and "swap it in" must never
        // leave something Load() will pick up as if it were the real file.
        Directory.CreateDirectory(_dir);
        var vocab = new DictationVocabulary(_dir);
        File.WriteAllText(vocab.FilePath + ".tmp", "Should Not Load\n");

        var result = vocab.Load();

        Assert.Empty(result.Phrases);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Save_OverwritesAStalePartialFileAtomically()
    {
        // A half-written file from a hypothetical crashed save must never be
        // what a subsequent successful Save leaves in place — the final
        // Load must see only the complete new content.
        var vocab = new DictationVocabulary(_dir);
        vocab.Save(new[] { "Old Phrase One", "Old Phrase Two" });

        vocab.Save(new[] { "New Phrase" });
        var result = vocab.Load();

        Assert.Equal(new[] { "New Phrase" }, result.Phrases);
    }

    [Fact]
    public void Save_ProducesAPlainUtf8FileWithNoByteOrderMark()
    {
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(new[] { "Zupong" });

        var bytes = File.ReadAllBytes(vocab.FilePath);
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };
        Assert.False(bytes.Length >= 3 && bytes[0] == utf8Bom[0] && bytes[1] == utf8Bom[1] && bytes[2] == utf8Bom[2]);
    }

    // ── Empty input ──────────────────────────────────────────────────────

    [Fact]
    public void Save_EmptyList_ProducesAFileThatLoadsAsEmpty()
    {
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(Array.Empty<string>());
        var result = vocab.Load();

        Assert.Empty(result.Phrases);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Save_NullPhrases_Throws()
    {
        var vocab = new DictationVocabulary(_dir);

        Assert.Throws<ArgumentNullException>(() => vocab.Save((IEnumerable<string>)null!));
    }

    // ── Constructor validation ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsNullOrWhitespaceDirectory(string? directory)
    {
        // ThrowsAny, not Throws. ArgumentException.ThrowIfNullOrWhiteSpace
        // raises ArgumentNullException for null and ArgumentException for
        // whitespace, and Assert.Throws<T> demands the EXACT type — so the
        // null case failed on a store that behaved correctly.
        Assert.ThrowsAny<ArgumentException>(() => new DictationVocabulary(directory!));
    }

    [Fact]
    public void FilePath_CombinesDirectoryAndDefaultFileName()
    {
        var vocab = new DictationVocabulary(_dir);

        Assert.Equal(Path.Combine(_dir, "bias_phrases.txt"), vocab.FilePath);
    }

    [Fact]
    public void Constructor_HonoursACustomFileName()
    {
        var vocab = new DictationVocabulary(_dir, "custom_vocab.txt");

        Assert.Equal(Path.Combine(_dir, "custom_vocab.txt"), vocab.FilePath);
    }

    // ── Exclusion is not deletion ────────────────────────────────────────

    [Fact]
    public void ExcludedPhrase_SurvivesASaveLoadRoundTrip()
    {
        // The whole point of the feature: a word the user left out is still
        // there tomorrow, so "put it back" is a click rather than a memory
        // test.
        var vocab = new DictationVocabulary(_dir);
        var entries = new[]
        {
            DictationVocabularyEntry.Included("Zupong"),
            DictationVocabularyEntry.Excluded("Trovioo"),
            DictationVocabularyEntry.Included("Yurin Zhang"),
        };

        vocab.Save(entries);
        var result = vocab.Load();

        Assert.Equal<IEnumerable<DictationVocabularyEntry>>(entries, result.Entries);
    }

    [Fact]
    public void ExcludedPhrase_NeverReachesTheDecoder()
    {
        // Phrases is what is handed to bias_phrases. A struck-through word
        // appearing there is the one failure this feature cannot have.
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(new[]
        {
            DictationVocabularyEntry.Included("Zupong"),
            DictationVocabularyEntry.Excluded("Trovioo"),
        });
        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong" }, result.Phrases);
        Assert.Equal(2, result.Entries.Length);
    }

    [Fact]
    public void ExcludedPhrase_KeepsItsPositionBecausePositionIsRank()
    {
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(new[]
        {
            DictationVocabularyEntry.Included("first"),
            DictationVocabularyEntry.Excluded("second"),
            DictationVocabularyEntry.Included("third"),
        });
        var result = vocab.Load();

        Assert.Equal(new[] { "first", "second", "third" }, result.Entries.Select(e => e.Text));
        Assert.Equal(new[] { false, true, false }, result.Entries.Select(e => e.IsExcluded));
    }

    [Fact]
    public void ExcludedPhrase_IsWrittenAsACommentSoAnyReaderIgnoresIt()
    {
        // The format choice, asserted: an excluded word is inert to every
        // reader that has never heard of exclusion, a person's eyes included.
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(new[] { DictationVocabularyEntry.Excluded("Trovioo") });

        var text = File.ReadAllText(vocab.FilePath);
        Assert.Contains("#- Trovioo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReadsAnExclusionMarkerTypedByHand()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "Zupong\n#- Trovioo\nYurin Zhang\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong", "Trovioo", "Yurin Zhang" }, result.Entries.Select(e => e.Text));
        Assert.Equal(new[] { "Zupong", "Yurin Zhang" }, result.Phrases);
    }

    [Fact]
    public void Load_AProseCommentIsStillJustAComment()
    {
        // "# teammates" must not become an excluded phrase called
        // "teammates" — the annotation convention predates exclusion and
        // people are already using it.
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "# teammates\nZupong\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Load_AnAsciiRuleLineIsNotAnExcludedPhrase()
    {
        // "#--------" is a separator a person draws by hand. Without the
        // whitespace requirement after the marker it reads as an excluded
        // phrase named "-------" and draws struck through in their cloud.
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "#--------\nZupong\n#-\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Load_ABarePhraseBeatsAnExclusionMarkerForTheSameWord()
    {
        // Only reachable by hand-editing, and the bare line is the deliberate
        // one: a word that vanishes on save looks like the file is broken,
        // whereas re-excluding it is one click.
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "#- Trovioo\nZupong\nTrovioo\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        // Position comes from whichever line came first, because position is
        // rank; the ACTIVE state comes from the bare line.
        Assert.Equal(new[] { "Trovioo", "Zupong" }, result.Entries.Select(e => e.Text));
        Assert.All(result.Entries, e => Assert.False(e.IsExcluded));
    }

    [Fact]
    public void Load_ExcludedPhrasesDoNotSpendActivePhraseSlots()
    {
        // They never reach the decoder, so the measured lexicon-size hazard
        // does not apply to them — and sharing the budget would mean striking
        // a word out silently pushed a real one off the end.
        var lines = Enumerable.Range(0, DictationVocabulary.MaxPhraseCount)
            .Select(i => $"phrase-{i:D3}")
            .Concat(Enumerable.Range(0, 5).Select(i => $"#- left-out-{i}"))
            .ToArray();
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            string.Join('\n', lines));
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(DictationVocabulary.MaxPhraseCount, result.Phrases.Length);
        Assert.Equal(DictationVocabulary.MaxPhraseCount + 5, result.Entries.Length);
    }

    [Fact]
    public void Load_CapsExcludedPhrasesToo()
    {
        // Every excluded word holds a slot in the render cap, so an unbounded
        // list of them quietly pushes the user's real vocabulary out of its
        // own picture.
        var lines = Enumerable.Range(0, DictationVocabulary.MaxExcludedCount + 10)
            .Select(i => $"#- left-out-{i:D3}");
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            string.Join('\n', lines));
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Equal(DictationVocabulary.MaxExcludedCount, result.Entries.Length);
        Assert.Empty(result.Phrases);
    }

    [Fact]
    public void Load_DedupesAcrossIncludedAndExcluded()
    {
        File.WriteAllText(Path.Combine(_dir, DictationVocabulary.DefaultFileName),
            "#- Trovioo\n#- trovioo\n#- TROVIOO\n");
        var vocab = new DictationVocabulary(_dir);

        var result = vocab.Load();

        Assert.Single(result.Entries);
        Assert.Equal("Trovioo", result.Entries[0].Text);
        Assert.True(result.Entries[0].IsExcluded);
    }

    [Fact]
    public void Save_RawLinesPreserveExclusionMarkers()
    {
        // The vocabulary text box saves its own text verbatim, so a round trip
        // through the string overload must not quietly re-include everything
        // the user struck out.
        var vocab = new DictationVocabulary(_dir);

        vocab.Save(new[] { "Zupong", "#- Trovioo", "# a note", "" });
        var result = vocab.Load();

        Assert.Equal(new[] { "Zupong", "Trovioo" }, result.Entries.Select(e => e.Text));
        Assert.Equal(new[] { "Zupong" }, result.Phrases);
    }

    [Fact]
    public void ToLinesAndParseLines_AreTheSameFormat()
    {
        var entries = new[]
        {
            DictationVocabularyEntry.Included("Zupong"),
            DictationVocabularyEntry.Excluded("Trovioo"),
        };

        var reparsed = DictationVocabulary.ParseLines(DictationVocabulary.ToLines(entries));

        Assert.Equal(entries, reparsed);
    }

    // ── Rename keeps rank ────────────────────────────────────────────────

    [Fact]
    public void Rename_KeepsThePositionBecausePositionIsRank()
    {
        // Removing and re-appending would send a corrected word to the bottom
        // of the cloud and shrink it — a punishment for fixing a typo.
        var entries = DictationVocabulary.ParseLines(["first", "Trovio", "third"]);

        var result = DictationVocabulary.Rename(entries, "Trovio", "Trovioo");

        Assert.True(result.Changed);
        Assert.Null(result.Refusal);
        Assert.Equal(new[] { "first", "Trovioo", "third" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Rename_CarriesTheExcludedFlagToTheNewSpelling()
    {
        // Correcting a spelling is not a decision about whether the word is in
        // the vocabulary.
        var entries = DictationVocabulary.ParseLines(["Zupong", "#- Trovio"]);

        var result = DictationVocabulary.Rename(entries, "Trovio", "Trovioo");

        Assert.True(result.Changed);
        Assert.Equal("Trovioo", result.Entries[1].Text);
        Assert.True(result.Entries[1].IsExcluded);
    }

    [Fact]
    public void Rename_MatchesTheOldSpellingCaseInsensitively()
    {
        var entries = DictationVocabulary.ParseLines(["Zupong"]);

        var result = DictationVocabulary.Rename(entries, "  zupong ", "Zupong Li");

        Assert.True(result.Changed);
        Assert.Equal(new[] { "Zupong Li" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Rename_CanFixCasingOnTheSameWord()
    {
        // The commonest reason to rename at all — and the reason the duplicate
        // check has to exclude the term being renamed.
        var entries = DictationVocabulary.ParseLines(["zupong"]);

        var result = DictationVocabulary.Rename(entries, "zupong", "Zupong");

        Assert.True(result.Changed);
        Assert.Null(result.Refusal);
        Assert.Equal(new[] { "Zupong" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Rename_RefusesToCollideWithAnotherTerm()
    {
        // Two slots spent on one word, and a cloud with the same word drawn
        // twice at two sizes.
        var entries = DictationVocabulary.ParseLines(["Zupong", "Trovioo"]);

        var result = DictationVocabulary.Rename(entries, "Trovioo", "zupong");

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.Equal(new[] { "Zupong", "Trovioo" }, result.Entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_RefusesABlankReplacement(string replacement)
    {
        var entries = DictationVocabulary.ParseLines(["Zupong"]);

        var result = DictationVocabulary.Rename(entries, "Zupong", replacement);

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.Equal(new[] { "Zupong" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Rename_RefusesAnOverLongReplacementRatherThanLosingItOnTheNextSave()
    {
        // ParseLines drops an over-length line outright. Accepting it here
        // would look like the edit worked and the word disappeared later.
        var entries = DictationVocabulary.ParseLines(["Zupong"]);
        var tooLong = new string('a', DictationVocabulary.MaxPhraseLength + 1);

        var result = DictationVocabulary.Rename(entries, "Zupong", tooLong);

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.Equal(new[] { "Zupong" }, result.Entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData("#growth")]
    [InlineData("#")]
    [InlineData("#- Zupong")]
    public void Rename_RefusesAReplacementTheFileFormatWouldSwallow(string replacement)
    {
        // ToLines writes an active entry bare, so a '#' spelling comes back
        // from ParseLines as either nothing (prose comment -- the word is
        // gone) or as an exclusion marker for whatever follows the '#-'. Both
        // are indistinguishable from data loss to the person who just fixed a
        // spelling.
        var entries = DictationVocabulary.ParseLines(["Zupong", "Trovioo"]);

        var result = DictationVocabulary.Rename(entries, "Zupong", replacement);

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.Equal(new[] { "Zupong", "Trovioo" }, result.Entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData("Zu\nkong")]
    [InlineData("Zu\rkong")]
    [InlineData("Zu\tkong")]
    public void Rename_RefusesAReplacementCarryingAControlCharacter(string replacement)
    {
        // WriteAtomically emits one line per entry, so an embedded break
        // splits one phrase into two on the next Load.
        var entries = DictationVocabulary.ParseLines(["Zupong"]);

        var result = DictationVocabulary.Rename(entries, "Zupong", replacement);

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.Equal(new[] { "Zupong" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void EveryRenameTheStoreAcceptsSurvivesTheRoundTripThroughTheFileFormat()
    {
        // The property behind the two refusals above: whatever Rename lets
        // through, ToLines can write and ParseLines can read back unchanged.
        // Guarding the two known spellings is only worth anything if nothing
        // else can reach the same hole.
        string[] candidates =
        [
            "Zupong", "#growth", "#", "#- Trovioo", "##", "Zu\nkong", "Zu\rkong",
            "  Zupong  ", "zupong", "Cove.ai", "-dash", "e\u0301clair", "\u4e2d\u6587",
            "", "   ", new string('a', DictationVocabulary.MaxPhraseLength + 1),
        ];

        foreach (var candidate in candidates)
        {
            var entries = DictationVocabulary.ParseLines(["Zupong", "#- Trovioo"]);
            var result = DictationVocabulary.Rename(entries, "Zupong", candidate);

            if (!result.Changed)
            {
                continue;
            }

            var reread = DictationVocabulary.ParseLines(DictationVocabulary.ToLines(result.Entries));

            Assert.Equal(
                result.Entries.Select(e => (e.Text, e.IsExcluded)),
                reread.Select(e => (e.Text, e.IsExcluded)));
        }
    }

    [Fact]
    public void Rename_RefusesAWordThatIsNotThere()
    {
        var entries = DictationVocabulary.ParseLines(["Zupong"]);

        var result = DictationVocabulary.Rename(entries, "Missing", "Anything");

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.Equal(new[] { "Zupong" }, result.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Rename_SurvivesTheRoundTripToDisk()
    {
        var vocab = new DictationVocabulary(_dir);
        vocab.Save(new[] { "first", "Trovio", "third" });

        var renamed = DictationVocabulary.Rename(vocab.Load().Entries, "Trovio", "Trovioo");
        vocab.Save(renamed.Entries);

        var result = vocab.Load();
        Assert.Equal(new[] { "first", "Trovioo", "third" }, result.Phrases);
    }

    // ── Excluding and putting back ───────────────────────────────────────

    [Fact]
    public void SetExcluded_LeavesTheWordExactlyWhereItWas()
    {
        var entries = DictationVocabulary.ParseLines(["first", "second", "third"]);

        var result = DictationVocabulary.SetExcluded(entries, "second", true);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "first", "second", "third" }, result.Entries.Select(e => e.Text));
        Assert.Equal(new[] { false, true, false }, result.Entries.Select(e => e.IsExcluded));
    }

    [Fact]
    public void ToggleExcluded_IsTheOneControlThatBothRemovesAndRestores()
    {
        var entries = DictationVocabulary.ParseLines(["Zupong"]);

        var out1 = DictationVocabulary.ToggleExcluded(entries, "Zupong");
        Assert.True(out1.Entries[0].IsExcluded);

        var back = DictationVocabulary.ToggleExcluded(out1.Entries, "Zupong");
        Assert.False(back.Entries[0].IsExcluded);
        // Compared as sequences: with BOTH sides typed ImmutableArray<T> this binds
        // to Assert.Equal<T>(T, T), whose default comparer for that struct is
        // reference equality on the underlying array — so two identical lists
        // fail with "Collections differ" printing two identical collections.
        Assert.Equal<IEnumerable<DictationVocabularyEntry>>(entries, back.Entries);
    }

    [Fact]
    public void SetExcluded_IsANoOpWhenNothingWouldChange()
    {
        var entries = DictationVocabulary.ParseLines(["Zupong"]);

        var result = DictationVocabulary.SetExcluded(entries, "Zupong", false);

        Assert.False(result.Changed);
        Assert.Null(result.Refusal);
        Assert.Equal<IEnumerable<DictationVocabularyEntry>>(entries, result.Entries);
    }

    [Fact]
    public void SetExcluded_RefusesAWordThatIsNotThere()
    {
        var entries = DictationVocabulary.ParseLines(["Zupong"]);

        var result = DictationVocabulary.SetExcluded(entries, "Missing", true);

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.Equal<IEnumerable<DictationVocabularyEntry>>(entries, result.Entries);
    }

    [Fact]
    public void SetExcluded_RefusesRatherThanLettingTheNextSaveDropTheWord()
    {
        // A word that disappears one save after you struck it through is
        // indistinguishable from a bug, so the bound is enforced where the
        // user can still see it.
        var entries = DictationVocabulary.ParseLines(
            Enumerable.Range(0, DictationVocabulary.MaxExcludedCount)
                .Select(i => $"#- left-out-{i:D3}")
                .Append("Zupong"));

        var result = DictationVocabulary.SetExcluded(entries, "Zupong", true);

        Assert.False(result.Changed);
        Assert.NotNull(result.Refusal);
        Assert.False(result.Entries.Single(e => e.Text == "Zupong").IsExcluded);
    }

    [Fact]
    public void ExclusionEditsRoundTripThroughTheFile()
    {
        var vocab = new DictationVocabulary(_dir);
        vocab.Save(new[] { "Zupong", "Trovioo" });

        var edited = DictationVocabulary.SetExcluded(vocab.Load().Entries, "Trovioo", true);
        vocab.Save(edited.Entries);

        var reloaded = vocab.Load();
        Assert.Equal(new[] { "Zupong" }, reloaded.Phrases);
        Assert.Equal(new[] { "Zupong", "Trovioo" }, reloaded.Entries.Select(e => e.Text));
        Assert.True(reloaded.Entries[1].IsExcluded);
    }

    [Fact]
    public void Save_NullEntries_Throws()
    {
        var vocab = new DictationVocabulary(_dir);

        Assert.Throws<ArgumentNullException>(
            () => vocab.Save((IEnumerable<DictationVocabularyEntry>)null!));
    }
}
