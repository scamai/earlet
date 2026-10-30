using System.Xml.Linq;
using Dictation.Services;
using Dictation.Services.Learning;

namespace Dictation.Core.Tests;

/// <summary>
/// The correction notice end to end, as the app composes it: the controller's
/// real correction watch over the fake field, its events, the Vocabulary
/// section on the same dispatcher, and the window's wiring read from source.
/// </summary>
public sealed class CorrectionNoticeFlowTests
{
    private const string Heard = "trovio";
    private const string Meant = "Trovioo";

    /// <summary>The controller and the Vocabulary section, wired as
    /// MainWindow wires them: one dispatcher, the controller's watch, and the
    /// switch passed on from each render.</summary>
    private sealed class App : IDisposable
    {
        public App(Action<Rig>? setUp = null)
        {
            Rig = new Rig(learning: true);
            Rig.Platform.Worker.Transcript = "we use trovio for the checks";
            Rig.Platform.Field.FixAfterEachPaste(Heard, Meant);
            setUp?.Invoke(Rig);
            Vocabulary = new VocabularyModel(Rig.Learning!, View, Rig.Dispatcher.Post, Rig.Controller.Corrections);
            Rig.View.OnRender = state => Vocabulary.ApplyLearningSwitch(state.LearnFromCorrections);
            Vocabulary.Refresh();
        }

        public Rig Rig { get; }
        public RecordingVocabularyView View { get; } = new();
        public VocabularyModel Vocabulary { get; }

        public string? Notice => View.Last.CorrectionNotice;

        public IEnumerable<string?> Notices => View.States.Select(s => s.CorrectionNotice);

        /// <summary>One dictation, until its words are reported inserted and
        /// its watch, if any, has begun and been heard.</summary>
        public void DictateOnce()
        {
            var inserted = Rig.View.States.Count(Inserted);
            Rig.Press();
            Rig.Release();
            Rig.DrainUntil(() => Rig.View.States.Count(Inserted) > inserted);
            Rig.Dispatcher.Drain();
        }

        /// <summary>One dictation, its watch run to the end, and everything
        /// it said heard.</summary>
        public void DictateAndWatch()
        {
            DictateOnce();
            Rig.WaitForWatch();
            Rig.Dispatcher.Drain();
        }

        /// <summary>One dictation whose watch is parked with the fix in hand.</summary>
        public void DictateAndPark()
        {
            Rig.Time.Limit = Rig.Time.NowMs + 3_500;
            DictateOnce();
            Rig.Time.WaitUntilParked();
        }

        public void Dispose()
        {
            Vocabulary.Dispose();
            Rig.Dispose();
        }
    }

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    // ─── What the section shows ─────────────────────────────────────────

    [Fact]
    public async Task A_learned_correction_shows_checking_then_clears_and_the_word_is_listed()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndPark();

        Assert.Equal(DictationCopy.CorrectionChecking, app.Notice);
        app.Rig.Time.Release();
        app.Rig.WaitForWatch();
        app.Rig.Dispatcher.Drain();

        Assert.Null(app.Notice);
        Assert.Contains(app.View.Last.Rows, r => r.Text == Meant);
    }

    [Fact]
    public async Task The_same_fix_a_third_time_is_already_in_the_vocabulary()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        app.DictateAndWatch();
        Assert.Null(app.Notice);

        app.DictateAndWatch();

        Assert.Equal("This correction is already in your vocabulary.", app.Notice);
    }

    [Fact]
    public async Task A_secret_shaped_fix_says_another_matching_correction_is_needed()
    {
        using var app = new App(rig =>
        {
            rig.Platform.Worker.Transcript = "the wifi password is sunshine";
            rig.Platform.Field.FixAfterEachPaste("sunshine", "Sunshine42x!");
        });
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        Assert.Equal(DictationCopy.CorrectionPending, app.Notice);
        Assert.Empty(app.View.Last.Rows);
    }

    [Fact]
    public async Task A_change_nobody_typed_says_no_vocabulary_change_was_learned()
    {
        using var app = new App(rig => rig.Platform.Field.FixAfterEachPaste(Heard, Meant, typed: false));
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        Assert.Equal(DictationCopy.CorrectionNothingLearned, app.Notice);
    }

    [Fact]
    public async Task Text_left_as_it_was_says_nothing()
    {
        using var app = new App(rig => rig.Platform.Field.Fix = null);
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        Assert.Null(app.Notice);
    }

    /// <remarks>A password field the press can see is refused before
    /// anything is recorded, so it never gets a watch; the observer meets one
    /// only when the press's own probe could not tell (Unknown).</remarks>
    public static TheoryData<string> Unwatchable => new()
    {
        "another application",
        "a password field the press could not classify",
        "no focused field",
    };

    [Theory]
    [MemberData(nameof(Unwatchable))]
    public async Task A_field_that_cannot_be_watched_ends_quietly(string field)
    {
        using var app = new App(rig =>
        {
            switch (field)
            {
                case "another application":
                    rig.Platform.Field.Identity = rig.Platform.Field.Identity with { ProcessId = 4_243 };
                    break;
                case "a password field the press could not classify":
                    rig.Platform.SecureField.Next = SecureFieldState.Unknown;
                    rig.Platform.Field.Secure = SecureFieldState.Secure;
                    break;
                default:
                    rig.Platform.Field.Identity = SpanObserverTargetIdentity.Unresolved;
                    break;
            }
        });
        await app.Rig.EnableAsync();

        app.DictateAndWatch();

        Assert.Null(app.Notice);
        Assert.False(File.Exists(app.Rig.LearningFile));
    }

    [Fact]
    public async Task The_next_dictation_clears_the_notice_and_its_own_watch_then_shows_checking()
    {
        using var app = new App();
        using var paste = new ManualResetEventSlim();
        await app.Rig.EnableAsync();
        app.DictateAndPark();
        var watch = app.Rig.Controller.Corrections!.Completion;
        Assert.Equal(DictationCopy.CorrectionChecking, app.Notice);

        // Released, with the paste held: the old watch is over and heard
        // before the new one can begin.
        app.Rig.Press();
        app.Rig.Platform.Paste.Hold = paste;
        app.Rig.ReleaseToHeldPaste();
        Assert.True(watch.Wait(TimeSpan.FromSeconds(10)));
        app.Rig.Dispatcher.Drain();
        Assert.Null(app.Notice);

        var inserted = app.Rig.View.States.Count(Inserted);
        app.Rig.Time.Limit = app.Rig.Time.NowMs + 3_500;
        paste.Set();
        app.Rig.DrainUntil(() => app.Rig.View.States.Count(Inserted) > inserted);
        app.Rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.CorrectionChecking, app.Notice);
        app.Rig.Time.Release();
    }

    /// <summary>
    /// The order a busy thread pool gives: the last watch's end, ended by the
    /// next dictation's release, is heard only after the next watch has begun.
    /// It must not take the next watch's "Checking…" away; only the next
    /// watch's own end replaces it.
    /// </summary>
    [Fact]
    public async Task A_late_end_from_the_last_watch_cannot_replace_the_next_ones_checking()
    {
        var entered = new ManualResetEventSlim();
        var held = new ManualResetEventSlim();
        var holding = 0;
        // Ahead of the section's listener, on the last watch's own thread.
        using var app = new App(rig => rig.Controller.Corrections!.Ended += (_, _) =>
        {
            if (Interlocked.Exchange(ref holding, 1) == 0)
            {
                entered.Set();
                held.Wait(TimeSpan.FromSeconds(10));
            }
        });
        using var paste = new ManualResetEventSlim();
        try
        {
            await app.Rig.EnableAsync();
            app.DictateAndPark();
            var last = app.Rig.Controller.Corrections!.Completion;

            app.Rig.Press();
            app.Rig.Platform.Paste.Hold = paste;
            app.Rig.ReleaseToHeldPaste();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the last watch never ended");

            // The next dictation leaves its text as it was, and its watch parks.
            app.Rig.Platform.Field.Fix = null;
            app.Rig.Time.Limit = app.Rig.Time.NowMs + 3_500;
            var inserted = app.Rig.View.States.Count(Inserted);
            paste.Set();
            app.Rig.DrainUntil(() => app.Rig.View.States.Count(Inserted) > inserted);
            app.Rig.Dispatcher.Drain();
            Assert.Equal(DictationCopy.CorrectionChecking, app.Notice);

            held.Set();
            Assert.True(last.Wait(TimeSpan.FromSeconds(10)));
            app.Rig.Dispatcher.Drain();
            Assert.Equal(DictationCopy.CorrectionChecking, app.Notice);

            app.Rig.Time.Release();
            app.Rig.WaitForWatch();
            app.Rig.Dispatcher.Drain();
            Assert.Null(app.Notice);
        }
        finally
        {
            held.Set();
            paste.Set();
            app.Rig.Time.Release();
        }
    }

    [Fact]
    public async Task Pausing_mid_watch_clears_the_notice()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndPark();

        app.Rig.Controller.Pause();
        app.Rig.Time.Release();
        app.Rig.WaitForWatch();
        app.Rig.Dispatcher.Drain();

        Assert.Null(app.Notice);
    }

    [Fact]
    public async Task A_finished_watchs_notice_stays_through_pause_until_the_next_watch_replaces_it()
    {
        using var app = new App(rig => rig.Platform.Field.FixAfterEachPaste(Heard, Meant, typed: false));
        await app.Rig.EnableAsync();
        app.DictateAndWatch();
        Assert.Equal(DictationCopy.CorrectionNothingLearned, app.Notice);

        app.Rig.Controller.Pause();
        app.Rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.CorrectionNothingLearned, app.Notice);

        await app.Rig.EnableAsync();
        app.Rig.Time.Limit = app.Rig.Time.NowMs + 3_500;
        app.DictateOnce();
        Assert.Equal(DictationCopy.CorrectionChecking, app.Notice);
        app.Rig.Time.Release();
    }

    [Fact]
    public async Task Switching_learning_off_clears_the_notice_and_the_watch_it_ends_says_nothing_more()
    {
        using var app = new App();
        await app.Rig.EnableAsync();
        app.DictateAndPark();
        Assert.Equal(DictationCopy.CorrectionChecking, app.Notice);

        app.Rig.Controller.SetLearnFromCorrections(false);

        Assert.Null(app.Notice);
        app.Rig.Time.Release();
        app.Rig.WaitForWatch();
        app.Rig.Dispatcher.Drain();
        Assert.Null(app.Notice);
        Assert.False(File.Exists(app.Rig.LearningFile));
    }

    [Fact]
    public async Task No_notice_names_a_word_or_anything_around_it()
    {
        using var app = new App();
        await app.Rig.EnableAsync();

        app.DictateAndWatch();
        app.DictateAndWatch();
        app.DictateAndWatch();

        var shown = app.Notices.Where(n => n is not null).Distinct().ToArray();
        Assert.Contains(DictationCopy.CorrectionChecking, shown);
        Assert.Contains(DictationCopy.CorrectionAlreadyKnown, shown);
        foreach (var notice in shown)
        {
            foreach (var secret in new[] { Heard, Meant, "checks", FakeField.SentinelBefore, FakeField.SentinelAfter, "notepad" })
            {
                Assert.DoesNotContain(secret, notice!, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ─── The window ─────────────────────────────────────────────────────
    //
    // Pins on the source, not evidence that the window behaves: WPF cannot
    // run here, and no one has seen this section on Windows.

    private static string AppFile(string name) =>
        Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", name);

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void The_notice_sits_above_the_teach_row_in_the_secondary_style_as_a_polite_live_region()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var notice = xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "CorrectionNotice");
        var siblings = notice.Parent!.Elements().ToList();
        int At(string name) => siblings.FindIndex(e => (string?)e.Attribute(X + "Name") == name);
        var teachRow = siblings.FindIndex(e => e.Descendants().Any(d => (string?)d.Attribute(X + "Name") == "TeachWord"));

        Assert.Equal("VocabularySection", (string?)notice.Parent.Attribute(X + "Name"));
        Assert.Equal(At("LearnDisclosure") + 1, At("CorrectionNotice"));
        Assert.Equal(At("CorrectionNotice") + 1, teachRow);
        Assert.Equal("Polite", (string?)notice.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.LiveSetting"));
        Assert.Equal("Collapsed", (string?)notice.Attribute("Visibility"));
        Assert.Equal(
            (string?)siblings[At("LearnDisclosure")].Attribute("Foreground"), (string?)notice.Attribute("Foreground"));
    }

    [Fact]
    public void The_window_hands_the_watch_to_the_section_renders_the_notice_and_passes_the_switch_on()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

        Assert.Contains(
            "vocabulary = new VocabularyModel(\n                learning, this, action => Dispatcher.InvokeAsync(action), parts.Corrections(controller.Corrections),",
            code, StringComparison.Ordinal);
        // The app's parts hand the section the controller's watch itself; only
        // the render mode's put a relay in between (DictationRenderModeSurfaceTests).
        Assert.Contains(
            "Corrections: watch => watch,",
            Body(File.ReadAllText(AppFile("DictationWindowParts.cs")).ReplaceLineEndings("\n"), "internal static DictationWindowParts ForUser("),
            StringComparison.Ordinal);
        var renderVocabulary = Body(code, "public void Render(VocabularyViewState state)");
        Assert.Contains("SetLiveText(CorrectionNotice, state.CorrectionNotice ?? string.Empty);", renderVocabulary, StringComparison.Ordinal);
        Assert.Contains("CorrectionNotice.Visibility = state.CorrectionNotice is null", renderVocabulary, StringComparison.Ordinal);
        Assert.Contains(
            "vocabulary?.ApplyLearningSwitch(state.LearnFromCorrections);",
            Body(code, "public void Render(DictationViewState state)"), StringComparison.Ordinal);
    }

    /// <summary>A notice changes two or three times a dictation; rebuilding
    /// the rows each time would take keyboard focus off a Remove button.</summary>
    [Fact]
    public void A_render_that_changes_only_the_notice_keeps_the_rows_it_built()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

        Assert.Contains(
            "if (listedRows is null || !listedRows.SequenceEqual(state.Rows))\n        {\n            listedRows = state.Rows;\n            VocabularyList.Children.Clear();",
            Body(code, "public void Render(VocabularyViewState state)"), StringComparison.Ordinal);
        Assert.Equal(1, Count(code, "VocabularyList.Children.Clear();"));
    }

    private static int Count(string code, string text) =>
        (code.Length - code.Replace(text, string.Empty, StringComparison.Ordinal).Length) / text.Length;

    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{signature}'");
        var end = code.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return code[start..end];
    }
}
