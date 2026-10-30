using System.Xml.Linq;
using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;

namespace Dictation.Core.Tests;

internal sealed class RecordingVocabularyView : IVocabularyView
{
    public List<VocabularyViewState> States { get; } = [];

    public VocabularyViewState Last => States[^1];

    public void Render(VocabularyViewState state) => States.Add(state);
}

/// <summary>
/// The Vocabulary section's behaviour, as the window renders it: the list and
/// its lines, adding a word and every refusal sentence, removing a row, and
/// Forget all only after its confirmation.
/// </summary>
public sealed class VocabularyModelTests
{
    private static readonly DateTimeOffset T = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    private sealed class Section : IDisposable
    {
        public Section(string? directory = null)
        {
            Store = new LearningStore(directory ?? LearningGoldenFileTests.TempDirectory());
            Learning = new DictationLearning(Store, new WordListLexicon(LearningGoldenTests.SharedLexicon));
            Model = new VocabularyModel(Learning, View, Dispatcher.Post);
            Model.Refresh();
        }

        public LearningStore Store { get; }
        public DictationLearning Learning { get; }
        public RecordingVocabularyView View { get; } = new();
        public DeferredDispatcher Dispatcher { get; } = new();
        public VocabularyModel Model { get; }

        public LearningState OnDisk() => new LearningStore(Store.Directory).Load().State;

        /// <summary>A file where the store's folder should be: every save is
        /// refused until <see cref="Unblock"/>.</summary>
        public void Block()
        {
            if (Directory.Exists(Store.Directory))
            {
                Directory.Delete(Store.Directory, recursive: true);
            }

            File.WriteAllText(Store.Directory, "a file where the folder should be");
        }

        public void Unblock() => File.Delete(Store.Directory);

        public void Dispose() => Model.Dispose();
    }

    // ─── The list ───────────────────────────────────────────────────────

    [Fact]
    public void An_empty_list_says_how_words_get_there_and_offers_nothing_to_forget()
    {
        using var s = new Section();

        Assert.Empty(s.View.Last.Rows);
        Assert.Equal("No words yet. Fix a word Earlet got wrong, or add one above.", s.View.Last.EmptyText);
        Assert.False(s.View.Last.CanForgetAll);
        Assert.Null(s.View.Last.TeachMessage);
    }

    [Fact]
    public void Words_say_where_they_came_from_and_aliases_read_heard_to_meant()
    {
        using var s = new Section();
        s.Learning.Observe("d1", "we use trovio", "we use Trovioo", T);
        s.Learning.Observe("d2", "we use trovio", "we use Trovioo", T.AddSeconds(1));
        s.Model.Teach("SkyDock", null);
        s.Dispatcher.Drain();

        var rows = s.View.Last.Rows;
        Assert.Equal(
            [
                new VocabularyRow(VocabularyRowKind.Word, "SkyDock", "Added by you", "SkyDock", null),
                new VocabularyRow(VocabularyRowKind.Word, "Trovioo", "Confirmed by 2 or 3 of your fixes", "Trovioo", null),
                new VocabularyRow(VocabularyRowKind.Alias, "trovio → Trovioo", null, "Trovioo", "trovio"),
            ],
            rows);
        Assert.Null(s.View.Last.EmptyText);
        Assert.True(s.View.Last.CanForgetAll);
    }

    [Fact]
    public void Confidence_follows_independent_support_and_later_rejections_in_the_view()
    {
        using var s = new Section();
        foreach (var (id, label) in new[] { ("one", "Learned from one fix, not yet confirmed"), ("two", "Confirmed by 2 or 3 of your fixes"),
                     ("three", "Confirmed by 2 or 3 of your fixes"), ("four", "Confirmed by 4 or more of your fixes") })
        {
            s.Learning.Observe(id, "we use trovio", "we use Trovioo", T);
            s.Dispatcher.Drain();
            Assert.Equal(label, s.View.Last.Rows.Single(r => r.Kind == VocabularyRowKind.Word && r.Text == "Trovioo").Detail);
        }
        s.Learning.Observe("rejected", "we use Trovioo", "we use Trovio", T.AddSeconds(1));
        s.Dispatcher.Drain();
        Assert.Equal("Confirmed by 2 or 3 of your fixes", s.View.Last.Rows.Single(r => r.Kind == VocabularyRowKind.Word && r.Text == "Trovioo").Detail);
    }

    [Fact]
    public void A_change_made_on_another_thread_repaints_on_the_ui_thread()
    {
        using var s = new Section();
        var renders = s.View.States.Count;

        Task.Run(() => s.Learning.Observe("d1", "we use trovio", "we use Trovioo", T)).Wait();

        Assert.Equal(renders, s.View.States.Count);
        s.Dispatcher.Drain();
        Assert.Equal("Trovioo", Assert.Single(s.View.Last.Rows).Text);
    }

    [Fact]
    public void A_closed_section_is_not_repainted()
    {
        using var s = new Section();
        s.Learning.Teach("SkyDock");
        Assert.Equal(1, s.Dispatcher.Pending);
        var renders = s.View.States.Count;

        s.Model.Dispose();
        s.Learning.Teach("Trovioo");

        // No longer listening, and the repaint queued before the close does nothing.
        Assert.Equal(1, s.Dispatcher.Pending);
        s.Dispatcher.Drain();
        Assert.Equal(renders, s.View.States.Count);
    }

    // ─── Adding a word ──────────────────────────────────────────────────

    [Fact]
    public async Task An_added_word_and_its_sounds_like_form_are_listed_and_saved()
    {
        using var s = new Section();

        Assert.True(await s.Model.Teach("Trovioo", "true leo"));

        Assert.Equal("Saved to your vocabulary on this PC.", s.View.Last.TeachMessage);
        Assert.Equal(["Trovioo", "true leo → Trovioo"], s.View.Last.Rows.Select(r => r.Text));
        var disk = s.OnDisk();
        Assert.Equal(LearningOrigins.Taught, Assert.Single(disk.Words).Origin);
        Assert.Equal(("true leo", LearningOrigins.Taught), (Assert.Single(disk.Aliases).Heard, disk.Aliases[0].Origin));
    }

    [Fact]
    public async Task A_blank_sounds_like_field_adds_the_word_alone()
    {
        using var s = new Section();

        Assert.True(await s.Model.Teach("Trovioo", "   "));

        Assert.Equal(VocabularyRowKind.Word, Assert.Single(s.View.Last.Rows).Kind);
    }

    public static TheoryData<string?, string?, string> Refusals => new()
    {
        { "", null, "Type a word first." },
        { "   ", "trovio", "Type a word first." },
        { null, null, "Type a word first." },
        { "Trovioo", "t", "Type a word first." },
        { new string('x', 61), null, "That is too long. Use at most 6 words, or 12 Chinese, Japanese or Korean characters, and 60 characters in all." },
        { "one two three four five six seven", null, "That is too long. Use at most 6 words, or 12 Chinese, Japanese or Korean characters, and 60 characters in all." },
        { "Trovioo Cloud", "trovioo", "The misheard form can't be part of the word itself." },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_word_that_cannot_be_added_says_why_and_is_not_saved(string? written, string? soundsLike, string why)
    {
        using var s = new Section();

        Assert.False(await s.Model.Teach(written, soundsLike));

        Assert.Equal(why, s.View.Last.TeachMessage);
        Assert.Empty(s.View.Last.Rows);
        Assert.False(File.Exists(s.Store.FilePath));
    }

    [Fact]
    public async Task A_full_list_says_so()
    {
        var dir = LearningGoldenFileTests.TempDirectory();
        var state = new LearningState();
        for (var i = 0; i < LearningLimits.MaxTaughtWords; i++)
        {
            state.Words.Add(new LearnedWord
            {
                Text = $"Word{i}", Origin = LearningOrigins.Taught, Count = 1, FirstSeen = 1, LastSeen = 1,
            });
        }

        var writer = new LearningStore(dir);
        writer.Load();
        Assert.True(writer.Save(state));
        using var s = new Section(dir);

        Assert.False(await s.Model.Teach("Trovioo", null));

        Assert.Equal("The list is full. Remove a word first.", s.View.Last.TeachMessage);
    }

    [Fact]
    public async Task The_refusal_goes_away_once_a_word_is_added()
    {
        using var s = new Section();
        Assert.False(await s.Model.Teach("", null));
        Assert.Equal(DictationCopy.TeachEmpty, s.View.Last.TeachMessage);

        Assert.True(await s.Model.Teach("Trovioo", null));

        Assert.Equal(DictationCopy.TeachSaved, s.View.Last.TeachMessage);
    }

    [Fact]
    public void Every_refusal_has_its_sentence()
    {
        foreach (var result in Enum.GetValues<TeachResult>())
        {
            Assert.Equal(result == TeachResult.Ok, DictationCopy.ForTeach(result) is null);
        }
    }

    // ─── Removing ───────────────────────────────────────────────────────

    [Fact]
    public void Removing_a_word_takes_its_aliases_and_keeps_it_from_coming_back()
    {
        using var s = new Section();
        s.Learning.Observe("d1", "we use trovio", "we use Trovioo", T);
        s.Learning.Observe("d2", "we use trovio", "we use Trovioo", T.AddSeconds(1));
        s.Model.Refresh();

        s.Model.Remove(s.View.Last.Rows.Single(r => r.Kind == VocabularyRowKind.Word));

        Assert.Empty(s.View.Last.Rows);
        s.Learning.Observe("d3", "we use trovio", "we use Trovioo", T.AddSeconds(2));
        s.Learning.Observe("d4", "we use trovio", "we use Trovioo", T.AddSeconds(3));
        var disk = s.OnDisk();
        Assert.Empty(disk.Words);
        Assert.Empty(disk.Aliases);
    }

    [Fact]
    public void Removing_an_alias_keeps_its_word()
    {
        using var s = new Section();
        s.Model.Teach("Trovioo", "true leo");

        s.Model.Remove(s.View.Last.Rows.Single(r => r.Kind == VocabularyRowKind.Alias));

        var row = Assert.Single(s.View.Last.Rows);
        Assert.Equal((VocabularyRowKind.Word, "Trovioo"), (row.Kind, row.Text));
        Assert.Empty(s.OnDisk().Aliases);
        Assert.Equal("true leo", Assert.Single(s.OnDisk().Removed.Aliases).Heard);
    }

    // ─── Forget all ─────────────────────────────────────────────────────

    [Fact]
    public void Forget_all_asks_first_and_cancel_keeps_everything()
    {
        using var s = new Section();
        s.Model.Teach("Trovioo", null);

        s.Model.AskForgetAll();

        Assert.True(s.View.Last.ConfirmingForgetAll);
        Assert.False(s.View.Last.CanForgetAll);
        Assert.Equal(
            "Forget every word you added and every word Earlet learned, with their misheard forms and the corrections it is still checking? Earlet stops using them, and this can't be undone.", DictationCopy.ForgetAllQuestion);
        Assert.Equal(("Forget all", "Cancel"), (DictationCopy.ForgetAll, DictationCopy.ForgetAllCancel));

        s.Model.CancelForgetAll();

        Assert.False(s.View.Last.ConfirmingForgetAll);
        Assert.True(s.View.Last.CanForgetAll);
        Assert.Single(s.OnDisk().Words);
    }

    [Fact]
    public void Forget_all_without_the_confirmation_does_nothing()
    {
        using var s = new Section();
        s.Model.Teach("Trovioo", null);

        s.Model.ConfirmForgetAll();

        Assert.Single(s.View.Last.Rows);
        Assert.Single(s.OnDisk().Words);
    }

    [Fact]
    public void Confirmed_forget_all_empties_the_list_and_the_file_tombstones_included()
    {
        using var s = new Section();
        s.Model.Teach("Trovioo", "true leo");
        s.Model.Teach("SkyDock", null);
        s.Model.Remove(s.View.Last.Rows.Single(r => r.Text == "SkyDock"));

        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();

        Assert.Empty(s.View.Last.Rows);
        Assert.Equal(DictationCopy.VocabularyEmpty, s.View.Last.EmptyText);
        Assert.False(s.View.Last.ConfirmingForgetAll);
        Assert.False(s.View.Last.CanForgetAll);
        var disk = s.OnDisk();
        Assert.Empty(disk.Words);
        Assert.Empty(disk.Aliases);
        Assert.Empty(disk.Removed.Words);
    }

    // ─── When the disk says no ──────────────────────────────────────────

    [Fact]
    public void A_removal_the_disk_refused_says_the_vocabulary_is_not_saved_until_a_save_works()
    {
        using var s = new Section();
        s.Model.Teach("Trovioo", null);
        Assert.Null(s.View.Last.NotSavedText);

        s.Block();
        s.Model.Remove(s.View.Last.Rows.Single());

        // Gone from the list, as asked — and the list says it is not on disk
        // yet, rather than looking done while the worker still uses it.
        Assert.Empty(s.View.Last.Rows);
        Assert.Equal(DictationCopy.VocabularyChangeNotSaved, s.View.Last.NotSavedText);
        Assert.Null(s.View.Last.ManagementNotice);

        s.Unblock();
        s.Model.Teach("SkyDock", null);

        Assert.Null(s.View.Last.NotSavedText);
        Assert.Equal(["SkyDock"], s.OnDisk().Words.Select(w => w.Text));
    }

    [Fact]
    public void A_forget_all_the_disk_refused_says_so()
    {
        using var s = new Section();
        s.Model.Teach("Trovioo", "true leo");
        s.Block();

        s.Model.AskForgetAll();
        s.Model.ConfirmForgetAll();

        Assert.Empty(s.View.Last.Rows);
        Assert.Equal(DictationCopy.VocabularyEmpty, s.View.Last.EmptyText);
        Assert.Equal(DictationCopy.VocabularyChangeNotSaved, s.View.Last.NotSavedText);
    }

    [Fact]
    public void The_not_saved_sentence_is_the_one_learning_md_names()
    {
        Assert.Equal(
            "Your vocabulary isn't saved yet. Earlet keeps using it and will try again.",
            DictationCopy.VocabularyNotSaved);
        var spec = File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "..", "LEARNING.md"));
        Assert.Contains(
            DictationCopy.VocabularyNotSaved,
            System.Text.RegularExpressions.Regex.Replace(spec, @"\s+", " "),
            StringComparison.Ordinal);
    }

    /// <summary>LEARNING.md §4.3 names the second field as the window does
    /// and never calls it a pronunciation again.</summary>
    [Fact]
    public void Learning_md_names_the_misheard_field_as_the_window_does()
    {
        var spec = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "..", "LEARNING.md")), @"\s+", " ");
        var teaching = spec[spec.IndexOf("### 4.3 Teaching", StringComparison.Ordinal)..spec.IndexOf("### 4.4", StringComparison.Ordinal)];

        Assert.Contains(
            $"\"{DictationCopy.TeachSoundsLikePlaceholder.Replace(" (optional)", "", StringComparison.Ordinal)}\" field",
            teaching, StringComparison.Ordinal);
        Assert.DoesNotContain("how it sounds", teaching, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sounds-like", teaching, StringComparison.OrdinalIgnoreCase);
    }

    // ─── The copy ───────────────────────────────────────────────────────

    [Fact]
    public void The_section_says_exactly_what_the_mac_says()
    {
        Assert.Equal("Vocabulary", DictationCopy.VocabularyHeading);
        Assert.Equal("Learn from my corrections", DictationCopy.LearnFromCorrections);
        Assert.Equal(
            "If you fix a word within 90 seconds of dictating it, Earlet may learn it, but only in apps whose text it can read, "
            + "so that happens rarely. Adding the word below always works. Learning keeps words on this computer, never whole sentences.",
            DictationCopy.LearnFromCorrectionsDisclosure);
        Assert.Equal("Word or name", DictationCopy.TeachWordPlaceholder);
        Assert.Equal("Often misheard as (optional)", DictationCopy.TeachSoundsLikePlaceholder);
        Assert.Equal(
            "Optional: how Earlet writes it now, like “get hub” for GitHub. Earlet will write the word instead.",
            DictationCopy.TeachSoundsLikeHelp);
        Assert.Equal("Add", DictationCopy.TeachAdd);
        Assert.Equal("Remove", DictationCopy.RemoveEntry);
        Assert.Equal("Learned from your fix", DictationCopy.LearnedFromYourFix);
        Assert.Equal("Added by you", DictationCopy.AddedByYou);
        Assert.Equal("trovio → Trovioo", DictationCopy.AliasRow("trovio", "Trovioo"));
    }

    public static TheoryData<string> MisheardFieldSentences => new()
    {
        DictationCopy.TeachSoundsLikePlaceholder,
        DictationCopy.TeachSoundsLikeHelp,
        DictationCopy.TeachSelfContaining,
    };

    /// <summary>The second field is what Earlet writes now, rewritten to the
    /// word ("heard → meant"), not a pronunciation. Its label, the line under
    /// the row and its refusal are word for word the Mac's, read from its
    /// source as the correction notices are.</summary>
    [Theory]
    [MemberData(nameof(MisheardFieldSentences))]
    public void The_misheard_field_is_the_macs_sentence_word_for_word(string sentence) =>
        Assert.Contains(
            $"\"{sentence}\"", File.ReadAllText(MacSource("DictationAppCore", "DictationVocabulary.swift")),
            StringComparison.Ordinal);

    // ─── The teach row's hint, read from source ─────────────────────────

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string MacSource(string folder, string file) =>
        Path.Combine(DictationOverridesTests.WindowsRoot(), "..", "macos", "Sources", folder, file);

    /// <summary>From <paramref name="start"/> to the end of that member: the
    /// first line holding only a closing brace at its indentation.</summary>
    private static string Member(string code, string start)
    {
        var at = code.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no '{start}'");
        var end = code.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        Assert.True(end > at, $"no end to '{start}'");
        return code[at..end];
    }

    [Fact]
    public void The_hint_sits_under_the_teach_row_in_the_secondary_style_on_both_windows()
    {
        var xaml = XDocument.Load(Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", "MainWindow.xaml"));
        var hint = xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "TeachSoundsLikeHelp");
        var siblings = hint.Parent!.Elements().ToList();
        int At(string name) => siblings.FindIndex(e => (string?)e.Attribute(X + "Name") == name);
        var teachRow = siblings.FindIndex(e => e.Descendants().Any(d => (string?)d.Attribute(X + "Name") == "TeachSoundsLike"));

        Assert.Equal("VocabularySection", (string?)hint.Parent.Attribute(X + "Name"));
        Assert.Equal(teachRow + 1, At("TeachSoundsLikeHelp"));
        Assert.Equal(At("TeachSoundsLikeHelp") + 1, At("TeachMessage"));
        Assert.Null(hint.Attribute("Visibility"));
        Assert.Equal("Wrap", (string?)hint.Attribute("TextWrapping"));
        Assert.Equal(
            (string?)siblings[At("LearnDisclosure")].Attribute("Foreground"), (string?)hint.Attribute("Foreground"));

        // Both placeholders end in an ellipsis in a narrow window instead of
        // being cut mid-word.
        foreach (var placeholder in new[] { "TeachWordHint", "TeachSoundsLikeHint" })
        {
            Assert.Equal(
                "CharacterEllipsis",
                (string?)xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == placeholder)
                    .Attribute("TextTrimming"));
        }

        var labels = Member(
            File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", "MainWindow.xaml.cs"))
                .ReplaceLineEndings("\n"),
            "private void ShowVocabularyLabels(");
        Assert.Contains("TeachSoundsLikeHint.Text = DictationCopy.TeachSoundsLikePlaceholder;", labels, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetName(TeachSoundsLike, DictationCopy.TeachSoundsLikePlaceholder);", labels,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetHelpText(TeachSoundsLike, DictationCopy.TeachSoundsLikeHelp);", labels,
            StringComparison.Ordinal);
        Assert.Contains("TeachSoundsLikeHelp.Text = DictationCopy.TeachSoundsLikeHelp;", labels, StringComparison.Ordinal);

        // The Mac's row: the same line as a footnote under the fields and
        // above the refusal, and the field's VoiceOver hint.
        var view = File.ReadAllText(MacSource("DictationApp", "DictationVocabularyView.swift")).ReplaceLineEndings("\n");
        var teaching = Member(view, "private var teaching: some View {");
        var fields = teaching.IndexOf("ViewThatFits(in: .horizontal)", StringComparison.Ordinal);
        var footnote = teaching.IndexOf("DictationFootnote(Copy.soundsLikeHelp)", StringComparison.Ordinal);
        var refusal = teaching.IndexOf("if let message = vocabulary.teachMessage", StringComparison.Ordinal);
        Assert.True(fields >= 0 && fields < footnote && footnote < refusal, $"{fields} < {footnote} < {refusal}");
        Assert.Contains(
            ".accessibilityHint(Text(Copy.soundsLikeHelp))", Member(view, "private var soundsLikeField: some View {"),
            StringComparison.Ordinal);
    }
}
