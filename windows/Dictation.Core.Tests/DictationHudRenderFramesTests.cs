using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The indicator render's frames (<see cref="DictationHudRenderFrames"/>):
/// every pill the Windows indicator can show is drawn, each exactly as the
/// feed hands it to the window, and the busy ones are the ones the size
/// check compares. Then the render's matrix of scales, appearances and file
/// names (<see cref="DictationRender"/>).
/// </summary>
public sealed class DictationHudRenderFramesTests
{
    private static readonly DictationHudHold Hold = DictationHudRenderFrames.DefaultHold;

    private static IReadOnlyList<DictationHudRenderFrame> Frames => DictationHudRenderFrames.All(Hold);

    private static DictationHudRenderFrame Frame(string name) => Frames.Single(f => f.Name == name);

    /// <summary>A new ending in the shared model fails here until it is
    /// either drawn or named as one Dictation never produces; the password
    /// field found at the paste (FIELDCHECK) is a result of its own.</summary>
    [Fact]
    public void Every_ending_Dictation_can_produce_is_drawn()
    {
        var drawn = DictationHudRenderFrames.Results.Select(r => r.Result.Outcome).ToHashSet();
        Assert.True(drawn.Count >= 12, $"only {drawn.Count} outcomes drawn");
        foreach (var outcome in Enum.GetValues<DictationOutcome>())
        {
            Assert.True(
                drawn.Contains(outcome) ^ DictationHudRenderFrames.NeverProduced.Contains(outcome),
                $"{outcome} is {(drawn.Contains(outcome) ? "both drawn and named as never produced" : "neither drawn nor named as never produced")}");
        }

        var names = Frames.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(DictationHudRenderFrames.Results, result => Assert.Contains("outcome-" + result.Name, names));
        Assert.Contains(DictationHudRenderFrames.Results, r => r.Result is { Outcome: DictationOutcome.SecureField, SecureFieldAtPaste: true });
    }

    /// <summary>
    /// Every field of <see cref="DictationHudContext"/> is set by a context
    /// the frames draw, and each such context changes some ending's line, so
    /// it is drawn. A field the controller gains later fails here until a
    /// context in <see cref="DictationHudRenderFrames.Contexts"/> sets it.
    /// </summary>
    [Fact]
    public void Every_field_of_an_endings_context_is_drawn()
    {
        var fields = typeof(DictationHudContext).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .ToArray();
        Assert.True(fields.Length >= 7, $"only {fields.Length} context fields found");
        var plain = new DictationHudContext();
        foreach (var field in fields)
        {
            Assert.True(
                DictationHudRenderFrames.Contexts.Any(c => !Equals(field.GetValue(c.Context), field.GetValue(plain))),
                $"no drawn context sets {field.Name}");
        }

        var endings = DictationHudRenderFrames.Endings(Hold);
        Assert.All(
            DictationHudRenderFrames.Contexts.Where(c => c.Name.Length > 0),
            context => Assert.Contains(endings, e => e.Context == context.Context));
    }

    /// <summary>Every context the controller adds that changes a line is
    /// drawn with the outcome it changes, and only those: each reads
    /// differently from its outcome alone.</summary>
    [Fact]
    public void Every_context_that_changes_an_ending_is_drawn()
    {
        var endings = DictationHudRenderFrames.Endings(Hold);
        Assert.Contains(endings, e => e.Result.Outcome == DictationOutcome.Inserted && e.Context.Stopped?.Stop == DictationHoldStop.Ceiling);
        Assert.Contains(endings, e => e.Result.Outcome == DictationOutcome.Inserted && e.Context.Stopped?.Stop == DictationHoldStop.Silence);
        Assert.Contains(endings, e => e.Result.Outcome == DictationOutcome.Inserted && e.Context.Stopped?.Stop == DictationHoldStop.StuckKey);
        Assert.Contains(endings, e => e.Result.Outcome == DictationOutcome.Inserted && e.Context.Stopped?.Stop == DictationHoldStop.NothingHeard);
        Assert.Contains(endings, e => e.Result.Outcome == DictationOutcome.NoSpeech && e.Context.Stopped?.Stop == DictationHoldStop.NothingHeard);
        Assert.Contains(endings, e => e.Result.Outcome == DictationOutcome.Cancelled && e.Context.CancelledByShortcut);
        Assert.Contains(endings, e => e.Result.Outcome == DictationOutcome.Cancelled && e.Context is { CancelledByShortcut: true, TakeSaved: true });
        foreach (var outcome in new[] { DictationOutcome.Inserted, DictationOutcome.NoSpeech })
        {
            Assert.Contains(endings, e => e.Result.Outcome == outcome && e.Context.TakeStop == DictationTakeStop.MicrophoneRemoved);
            Assert.Contains(endings, e => e.Result.Outcome == outcome && e.Context.TakeStop == DictationTakeStop.WorkerLost);
        }

        foreach (var outcome in new[] { DictationOutcome.RecognizerUnavailable, DictationOutcome.RecognizerFailed })
        {
            Assert.Contains(endings, e => e.Result.Outcome == outcome && e.Context.TakeSaved);
        }

        foreach (var outcome in new[] { DictationOutcome.RecognizerUnavailable, DictationOutcome.RecognizerFailed })
        {
            Assert.Contains(endings, e => e.Result.Outcome == outcome && e.Context.RecognizerRestarting);
        }

        foreach (var outcome in new[] { DictationOutcome.InsertionRefused, DictationOutcome.NoFocusedWindow })
        {
            Assert.Contains(endings, e => e.Result.Outcome == outcome && e.Context.KeptOnClipboard);
            Assert.Contains(endings, e => e.Result.Outcome == outcome && e.Context.WordsKeptInApp);
        }

        foreach (var ending in endings.Where(e => e.Context != new DictationHudContext()))
        {
            var plain = DictationHudFeed.For(ending.Result, new DictationHudContext(), Hold);
            var withContext = DictationHudFeed.For(ending.Result, ending.Context, Hold);
            Assert.NotEqual(plain.Text, withContext.Text);
        }
    }

    /// <summary>LIMIT-WIN-A's microphone that stopped at 4:12 with the words
    /// in, the kept take, and Escape's question, and FIELDCHECK's refusal at
    /// the paste: each drawn as the feed shows it.</summary>
    [Fact]
    public void A_stopped_take_a_kept_take_Escape_s_question_and_the_refusal_at_the_paste_are_drawn()
    {
        var stopped = Frame("outcome-inserted-microphone-stopped").Model;
        Assert.Equal("Microphone stopped at 4:12 — your words up to then went in", stopped.Text);
        Assert.Equal(HudTone.Caution, stopped.Tone);
        var saved = Frame("outcome-recognizer-failed-take-saved").Model;
        Assert.Equal(DictationCopy.HudTakeSaved, saved.Text);
        Assert.Equal(HudTone.Caution, saved.Tone);
        Assert.Equal("Discard 3:42 of speech? — press Esc again", Frame("ask-to-discard").Model.Text);

        var paste = Frame("outcome-secure-field-at-paste").Model;
        var expected = DictationHudFeed.For(
            new DictationSessionResult(DictationOutcome.SecureField, null, null, SecureFieldAtPaste: true), new DictationHudContext(), Hold);
        Assert.Equal((expected.Text, expected.Tone, expected.Dwell), (paste.Text, paste.Tone, paste.Dwell));
        Assert.Equal(DictationCopy.HudPasswordFieldAtRelease, paste.Text);
    }

    /// <summary>
    /// Every line of the indicator's own in <see cref="DictationCopy"/> (its
    /// <c>Hud</c> fields, as the feed's tests read them) is drawn as some frame's text, or is one of the
    /// busy lines every pill is sized for: a pill added to the copy without
    /// a frame fails here.
    /// </summary>
    [Fact]
    public void Every_indicator_line_of_the_copy_is_drawn()
    {
        var lines = typeof(DictationCopy)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.Name.StartsWith("Hud", StringComparison.Ordinal))
            .Select(f => (f.Name, Text: (string)f.GetValue(null)!))
            .ToArray();
        Assert.True(lines.Length >= 15, $"only {lines.Length} Hud lines in DictationCopy");
        var drawn = Frames.Select(f => f.Model.Text).ToHashSet(StringComparer.Ordinal);
        var busy = DictationCopy.HudBusyLines(Hold.KeyName, Hold.Policy);
        foreach (var (name, text) in lines)
        {
            Assert.True(drawn.Contains(text) || busy.Contains(text), $"DictationCopy.{name} (\"{text}\") is in no frame");
        }
    }

    /// <summary>
    /// INDICATOR's two-line endings are drawn: every ending the indicator's
    /// tests know of for this hold key is a frame, each of them one whose
    /// second line may wrap (<see cref="DictationHudGeometry.DetailWraps"/>).
    /// Which of them wraps is the real font's to say: in the stand-in face
    /// the tests measure with, every second line fits one line at the cap,
    /// so the frames carry the ending with the widest second line, the first
    /// to wrap in a wider face.
    /// </summary>
    [Fact]
    public void The_endings_that_wrap_to_two_lines_are_drawn()
    {
        var otherKeys = DictationHotkeys.HoldChoices.Select(k => k.KeyName).Where(k => k != Hold.KeyName).ToArray();
        var neverProduced = DictationHudRenderFrames.NeverProduced
            .SelectMany(outcome => DictationHudFeedTests.Contexts.Select(context =>
                DictationHudFeed.For(new DictationSessionResult(outcome, null, null), context, Hold).Text))
            .ToHashSet(StringComparer.Ordinal);
        var known = DictationHudFeedTests.EveryEnding()
            .Where(m => m.IsVisible && m.Dwell is not null)
            .Select(m => m.Text)
            .Where(text => !otherKeys.Any(k => text.Contains(k, StringComparison.Ordinal)) && !neverProduced.Contains(text))
            .Distinct()
            .ToArray();
        Assert.True(known.Length >= 20, $"only {known.Length} endings known");
        var drawn = Frames.Where(f => !f.IsBusy).GroupBy(f => f.Model.Text, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Model, StringComparer.Ordinal);
        foreach (var text in known)
        {
            Assert.True(drawn.ContainsKey(text), $"\"{text}\" is in no frame");
            Assert.True(DictationHudGeometry.DetailWraps(drawn[text]), $"\"{text}\" may not wrap");
        }

        static double Width(string text) => text.Sum(IndicatorHintTests.Glyph);
        var widest = known.MaxBy(text => DictationHudLines.For(text).Detail is { } detail ? Width(detail) : 0)!;
        Assert.NotNull(DictationHudLines.For(widest).Detail);
        Assert.Contains(Frames, f => f.Model.Text == widest);
    }

    [Fact]
    public void Every_frame_has_its_own_name_and_shows_something()
    {
        var names = Frames.Select(f => f.Name).ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", name));
        Assert.All(Frames, frame => Assert.True(frame.Model.IsVisible, frame.Name));
        foreach (var refusal in new[] { "refused-still-typing", "refused-password-field", "refused-still-copying" })
        {
            Assert.Contains(refusal, names);
        }

        // A refusal added later is drawn too, under its own name.
        foreach (var why in Enum.GetValues<DictationPressRefusal>())
        {
            Assert.Contains("refused-" + DictationRender.Kebab(why), names);
        }

        foreach (var other in new[] { "copied", "copy-failed", "correction-remembered", "recognizer-could-not-restart" })
        {
            Assert.Contains(other, names);
        }

        Assert.Equal(DictationHudRenderFrames.Notices.Select(n => n.Name), names.Intersect(DictationHudRenderFrames.Notices.Select(n => n.Name)));
    }

    /// <summary>
    /// Every public member of the feed that can put a pill up is one the
    /// frames drive. A new one fails here until a frame drives it or it is
    /// named as one that shows nothing. A new ending added as an outcome, or
    /// as a context of one (MIC's microphone change came that way), is caught
    /// by <see cref="Every_ending_Dictation_can_produce_is_drawn"/> and
    /// <see cref="Every_field_of_an_endings_context_is_drawn"/> instead.
    /// </summary>
    [Fact]
    public void Every_way_the_feed_shows_a_pill_is_drawn()
    {
        string[] drawn =
        [
            "AskToDiscard", "Caption", "Copied", "CorrectionRemembered", "Level", "Listening", "PressRefused",
            "RecognizerCouldNotRestart", "RecoveryFailed", "Result", "State",
        ];
        // EndAskToDiscard puts back the transcribing pill, drawn above.
        string[] showsNothing = ["Clear", "Dispose", "EndAskToDiscard", "Tick"];
        var members = typeof(DictationHudFeed)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(drawn.Concat(showsNothing).Order(StringComparer.Ordinal), members);
    }

    /// <summary>Each frame is the feed's own model: sized for the hold's busy
    /// lines, as the window sizes every pill the app shows, so the size check
    /// reads the widths a user would see.</summary>
    [Fact]
    public void Every_frame_is_sized_for_the_holds_busy_lines()
    {
        var busyLines = DictationCopy.HudBusyLines(Hold.KeyName, Hold.Policy);
        Assert.All(Frames, frame => Assert.Equal(busyLines, frame.Model.SizedFor));
    }

    /// <summary>The busy pills are the listening, transcribing and typing
    /// ones and the stability sequence; every other is an ending with a dwell.
    /// The check needs at least two to compare.</summary>
    [Fact]
    public void The_busy_frames_are_the_holds_own_pills()
    {
        var busy = Frames.Where(f => f.IsBusy).Select(f => f.Name).ToArray();

        Assert.All(busy, name => Assert.True(
            name.StartsWith("listening", StringComparison.Ordinal) || name.StartsWith("sequence-", StringComparison.Ordinal)
            || name is "transcribing" or "typing",
            name));
        Assert.Contains("transcribing", busy);
        Assert.Contains("typing", busy);
        Assert.Equal(10 + DictationHudRenderFrames.Sequence.Count, busy.Length);
        Assert.All(Frames.Where(f => !f.IsBusy), frame => Assert.NotNull(frame.Model.Dwell));

        // HINTS' rule, the window's own: a pill with no dwell keeps the busy
        // width and its second line's room only when it is sized for the
        // hold's busy lines, so one that is not is no busy pill to compare.
        var listening = Frame("listening");
        Assert.False((listening with { Model = listening.Model with { SizedFor = null } }).IsBusy);
        Assert.False((listening with { Model = listening.Model with { Dwell = DictationHudModel.NoticeDwell } }).IsBusy);
        Assert.Contains(
            "model.Dwell is null && model.SizedFor is not null ? Visibility.Hidden : Visibility.Collapsed",
            File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs")),
            StringComparison.Ordinal);
    }

    /// <summary>The bars stand in the dot on the listening pills, at the
    /// level asked for; every other pill shows the dot.</summary>
    [Fact]
    public void Listening_frames_draw_the_level_and_no_other_does()
    {
        Assert.Equal(0f, Frame("listening").Level);
        Assert.Equal(DictationHudRenderFrames.SpeechLevel, Frame("listening-level").Level);
        Assert.Equal(DictationHudRenderFrames.SpeechLevel, Frame("listening-caption").Level);
        Assert.All(Frames.Where(f => f.Model.UnannouncedName is not null), frame => Assert.NotNull(frame.Level));
        Assert.All(Frames.Where(f => f.Model.UnannouncedName is null), frame => Assert.Null(frame.Level));
        Assert.Equal(
            DictationHudRenderFrames.Sequence.Select(s => (float?)s.Level),
            Frames.Where(f => f.Name.StartsWith("sequence-", StringComparison.Ordinal)).Select(f => f.Level));
    }

    /// <summary>The listening pills say what the hold is doing, hands-free,
    /// in its last minute and close to a silence stop included.</summary>
    [Fact]
    public void The_listening_frames_show_the_clock_the_countdown_and_the_silence_warning()
    {
        Assert.Equal(DictationCopy.HudHeld, Frame("listening").Model.Text);
        Assert.Equal(DictationCopy.HudListening(Hold.KeyName, true, TimeSpan.Zero, Hold.Policy), Frame("listening-latched").Model.Text);
        Assert.Equal(
            "No speech for 23 s — stopping in 7 s. Keep talking to continue", Frame("listening-silence-warning").Model.Text);
        Assert.Equal(DictationCopy.HudListeningName(latched: true), Frame("listening-silence-warning").Model.UnannouncedName);
        Assert.Contains("1:05", Frame("listening-latched-clock").Model.Text, StringComparison.Ordinal);
        Assert.Contains("Stops in 0:10", Frame("listening-countdown").Model.Text, StringComparison.Ordinal);
        Assert.Contains("Stops in 0:10", Frame("listening-latched-countdown").Model.Text, StringComparison.Ordinal);
        Assert.Equal(DictationCopy.HudListeningName(latched: true), Frame("listening-latched-countdown").Model.UnannouncedName);
        Assert.Equal(DictationCopy.HudTranscribing, Frame("transcribing").Model.Text);
        Assert.Equal(DictationCopy.HudTyping, Frame("typing").Model.Text);
    }

    /// <summary>The live preview's words are only ever a caption: never the
    /// text, the announcement or the name a reader is given.</summary>
    [Fact]
    public void The_caption_frames_carry_the_words_only_as_a_caption()
    {
        var captioned = Frames.Where(f => f.Model.Caption is not null).ToArray();
        Assert.Equal(1 + DictationHudRenderFrames.Sequence.Count(s => s.Caption.Length > 0), captioned.Length);
        Assert.Equal(DictationHudRenderFrames.Caption, Frame("listening-caption").Model.Caption);
        Assert.All(captioned, frame =>
        {
            Assert.DoesNotContain(frame.Model.Caption!, frame.Model.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(frame.Model.Caption!, frame.Model.QuietName, StringComparison.Ordinal);
        });
        Assert.All(Frames.Where(f => f.Model.Dwell is not null), frame => Assert.Null(frame.Model.Caption));
    }

    /// <summary>The endings are the feed's, context and all: the frame is
    /// <see cref="DictationHudFeed.For"/> sized for the hold.</summary>
    [Fact]
    public void The_ending_frames_are_the_feeds_endings()
    {
        foreach (var (name, result, context) in DictationHudRenderFrames.Endings(Hold))
        {
            var frame = Frame("outcome-" + name);
            var expected = DictationHudFeed.For(result, context, Hold);
            Assert.Equal((expected.Text, expected.Tone, expected.Dwell), (frame.Model.Text, frame.Model.Tone, frame.Model.Dwell));
        }

        Assert.Equal(DictationCopy.HudStillTyping, Frame("refused-still-typing").Model.Text);
        Assert.Equal(DictationCopy.HudPasswordFieldAtPress, Frame("refused-password-field").Model.Text);
        Assert.Equal(DictationCopy.HudStillCopying, Frame("refused-still-copying").Model.Text);
        Assert.Equal(DictationCopy.HudCopied, Frame("copied").Model.Text);
        Assert.Equal(DictationCopy.HudCopyFailed, Frame("copy-failed").Model.Text);
        Assert.Equal(DictationCopy.HudCorrectionRemembered, Frame("correction-remembered").Model.Text);
        Assert.Equal(DictationCopy.HudRecognizerCouldNotRestart, Frame("recognizer-could-not-restart").Model.Text);
    }

    /// <summary>The frames are what the app's indicator is handed: a press
    /// and a release through the controller show the listening and the
    /// transcribing frames' pills.</summary>
    [Fact]
    public async Task The_listening_and_transcribing_frames_are_what_a_dictation_shows()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.DrainUntil(() => rig.Hud.Current?.Text == DictationCopy.HudHeld);
        Same(Frame("listening").Model, rig.Hud.Current!);
        Assert.Equal(Frame("listening").Level, rig.Hud.Levels[^1]);

        rig.Platform.Worker.HoldTranscribe = new ManualResetEventSlim();
        try
        {
            // A hold long enough for "Listening" to stand its 375 ms, so
            // "Transcribing…" replaces it at the release (DictationHudPacer).
            rig.Clock.Advance(DictationHudPacer.VisibleDwell);
            rig.Release();
            rig.DrainUntil(() => rig.Hud.Current?.Text == DictationCopy.HudTranscribing);
            Same(Frame("transcribing").Model, rig.Hud.Current!);
        }
        finally
        {
            rig.Platform.Worker.HoldTranscribe.Set();
        }

        static void Same(DictationHudModel frame, DictationHudModel shown)
        {
            Assert.Equal(
                (frame.Text, frame.Tone, frame.Dwell, frame.UnannouncedName, frame.Caption),
                (shown.Text, shown.Tone, shown.Dwell, shown.UnannouncedName, shown.Caption));
            Assert.Equal(frame.SizedFor, shown.SizedFor);
        }
    }

    // ─── The request and the matrix ─────────────────────────────────────

    /// <summary>The app starts as itself unless a render directory is named;
    /// one named with spaces only is not named.</summary>
    [Fact]
    public void A_render_runs_only_when_a_directory_is_named()
    {
        static DictationRenderRequest? Asked(string? hud, string? window) => DictationRender.Requested(name => name switch
        {
            DictationEnvironment.HudRenderDirectory => hud,
            DictationEnvironment.WindowRenderDirectory => window,
            _ => "not this one",
        });

        Assert.Null(Asked(null, null));
        Assert.Null(Asked("", "  "));
        Assert.Equal(new DictationRenderRequest(@"C:\renders\hud", null), Asked(@" C:\renders\hud ", null));
        Assert.Equal(new DictationRenderRequest(null, "out"), Asked(" ", "out"));
        Assert.Equal(new DictationRenderRequest("a", "b"), Asked("a", "b"));
        Assert.Equal("DICTATION_HUD_RENDER_DIR", DictationEnvironment.HudRenderDirectory);
        Assert.Equal("DICTATION_WINDOW_RENDER_DIR", DictationEnvironment.WindowRenderDirectory);
        Assert.Contains(DictationEnvironment.HudRenderDirectory, DictationEnvironment.Overrides);
        Assert.Contains(DictationEnvironment.WindowRenderDirectory, DictationEnvironment.Overrides);
    }

    [Fact]
    public void Every_image_of_the_matrix_has_its_own_file_name()
    {
        var hud = DictationRender.ScalePercents
            .SelectMany(percent => DictationRender.Appearances.SelectMany(appearance =>
                Frames.Select(frame => DictationRender.HudFile(frame.Name, percent, appearance))))
            .ToArray();
        var window = DictationWindowRenderScenarios.All
            .SelectMany(scenario => DictationRender.WindowWidths.SelectMany(width => DictationRender.ScalePercents.SelectMany(percent =>
                DictationRender.Appearances.Select(appearance => DictationRender.WindowFile(scenario, width, percent, appearance)))))
            .ToArray();

        var tray = DictationRender.Appearances
            .SelectMany(appearance => DictationRender.TrayIcons.SelectMany(icon =>
                TrayIconArt.Sizes.Select(size => DictationRender.TrayFile(icon, size, appearance))))
            .ToArray();

        Assert.Equal(Frames.Count * 9, hud.Length);
        Assert.Equal(DictationWindowRenderScenarios.All.Count * 27, window.Length);
        Assert.Equal(Enum.GetValues<TrayIcon>().Length * TrayIconArt.Sizes.Count * 3, tray.Length);
        var all = hud.Concat(window).Concat(tray).Append(DictationRender.ReportFile).ToArray();
        Assert.Equal(all.Length, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, name => Assert.Equal(name, Path.GetFileName(name)));
        Assert.Equal("window-first-run-520-150-high-contrast.png", DictationRender.WindowFile(DictationWindowRenderScenario.FirstRun, 520, 150, DictationRenderAppearance.HighContrast));
        Assert.Equal("hud-typing-200-dark.png", DictationRender.HudFile("typing", 200, DictationRenderAppearance.Dark));
        Assert.Equal("tray-microphone-blocked-16-high-contrast.png", DictationRender.TrayFile(TrayIcon.MicrophoneBlocked, 16, DictationRenderAppearance.HighContrast));
    }

    /// <summary>
    /// The icon is drawn in every picture it can show (a picture added later
    /// is drawn too), in each appearance's inks: a light taskbar's, a dark
    /// taskbar's, and High Contrast's from the same palette the window's
    /// high-contrast images use, in the Mac's three microphone shapes. Each
    /// picture differs from the others in every appearance.
    /// </summary>
    [Fact]
    public void Every_icon_picture_is_drawn_in_every_appearances_inks()
    {
        Assert.Equal(Enum.GetValues<TrayIcon>(), DictationRender.TrayIcons);
        Assert.Equal(TrayInks.For(lightTaskbar: true), DictationRender.TrayInksFor(DictationRenderAppearance.Light));
        Assert.Equal(TrayInks.For(lightTaskbar: false), DictationRender.TrayInksFor(DictationRenderAppearance.Dark));
        var contrast = DictationRender.Palette(DictationRenderAppearance.HighContrast);
        Assert.Equal(
            TrayInks.HighContrast(
                0xFF00_0000 | (uint)contrast[DictationSystemColor.WindowText],
                0xFF00_0000 | (uint)contrast[DictationSystemColor.Highlight],
                0xFF00_0000 | (uint)contrast[DictationSystemColor.GrayText]),
            DictationRender.TrayInksFor(DictationRenderAppearance.HighContrast));
        Assert.True(DictationRender.TrayInksFor(DictationRenderAppearance.HighContrast).ByShape);
        Assert.Equal(
            [false, false, true],
            DictationRender.Appearances.Select(DictationRender.HighContrast));

        foreach (var appearance in DictationRender.Appearances)
        {
            var inks = DictationRender.TrayInksFor(appearance);
            var pictures = DictationRender.TrayIcons.Select(icon => Convert.ToBase64String(TrayIconArt.Render(icon, inks))).ToArray();
            Assert.Equal(pictures.Length, pictures.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void The_scales_are_windows_100_150_and_200_percent_and_each_group_is_one_scale_and_appearance()
    {
        Assert.Equal([100, 150, 200], DictationRender.ScalePercents);
        Assert.Equal([1.0, 1.5, 2.0], DictationRender.ScalePercents.Select(DictationRender.Scale));
        Assert.Equal([420, 520, 768], DictationRender.WindowWidths);
        var groups = DictationRender.ScalePercents
            .SelectMany(p => DictationRender.Appearances.Select(a => DictationRender.Group(p, a)))
            .ToArray();
        Assert.Equal(9, groups.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>An image is never a pixel short of its layout, and never a
    /// pixel larger than the window for floating-point error alone.</summary>
    [Fact]
    public void An_image_rounds_up_to_whole_pixels_but_not_for_floating_point_error()
    {
        Assert.Equal(100, DictationRender.Pixels(100, 100));
        Assert.Equal(11, DictationRender.Pixels(10.5, 100));
        Assert.Equal(151, DictationRender.Pixels(100.4, 150));
        Assert.Equal(201, DictationRender.Pixels(100.4, 200));
        Assert.Equal(0, DictationRender.Pixels(0, 200));

        // Two lengths laid out on the 150% grid make 26 pixels, though their
        // product comes out a hair over 26.
        var laidOut = (1 / 1.5) + (25 / 1.5);
        Assert.True(laidOut * 1.5 > 26);
        Assert.Equal(26, DictationRender.Pixels(laidOut, 150));

        // A real sliver still counts: a hundredth of a pixel over is one more.
        Assert.Equal(27, DictationRender.Pixels(26.01 / 1.5, 150));
    }

    /// <summary>Every appearance sets every system colour the render puts in
    /// place, so none falls back to the render host's own. Dark is the light
    /// palette (neither surface follows Windows' dark mode); high contrast is
    /// light text on black, with disabled text that still reads.</summary>
    [Fact]
    public void Every_appearance_sets_every_system_colour()
    {
        var all = Enum.GetValues<DictationSystemColor>();
        foreach (var appearance in DictationRender.Appearances)
        {
            Assert.Equal(all.Order(), DictationRender.Palette(appearance).Keys.Order());
        }

        Assert.Same(DictationRender.Palette(DictationRenderAppearance.Light), DictationRender.Palette(DictationRenderAppearance.Dark));
        var contrast = DictationRender.Palette(DictationRenderAppearance.HighContrast);
        Assert.Equal(0x000000, contrast[DictationSystemColor.Window]);
        Assert.Equal(0xFFFFFF, contrast[DictationSystemColor.WindowText]);
        Assert.True(Contrast(contrast[DictationSystemColor.GrayText], contrast[DictationSystemColor.Window]) >= 7);
        var light = DictationRender.Palette(DictationRenderAppearance.Light);
        Assert.True(Contrast(light[DictationSystemColor.WindowText], light[DictationSystemColor.Window]) >= 7);
        Assert.True(Contrast(light[DictationSystemColor.GrayText], light[DictationSystemColor.Window]) >= 4.5);
    }

    /// <summary>WCAG's contrast ratio of two 0xRRGGBB colours.</summary>
    private static double Contrast(int a, int b)
    {
        static double Luminance(int rgb)
        {
            static double Channel(int value)
            {
                var c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
        }

        var (lighter, darker) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (lighter + 0.05) / (darker + 0.05);
    }
}
