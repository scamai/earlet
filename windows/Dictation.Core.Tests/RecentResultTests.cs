using System.Globalization;
using System.Reflection;
using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// Recent text recovery: the macOS RecentResultTests, scenario for scenario
/// and by the same names, on the Windows controller the app runs; then the
/// Windows edges the Mac has no counterpart for (the copy written off the
/// dispatcher and the press it refuses, Pause, the menu's time, the log).
/// </summary>
public sealed class RecentResultTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static DictationRecentView Section(Rig rig) => Assert.IsType<DictationRecentView>(rig.View.Last.Recent);

    private static DictationRecentMenu Menu(Rig rig) => Assert.IsType<DictationRecentMenu>(rig.View.Last.Tray.Recent);

    private static int Transcribes(Rig rig) => rig.Platform.Worker.Ops().Count(op => op == "transcribe");

    /// <summary>A second dictation after the last one ended.</summary>
    private static void DictateAgain(Rig rig, Func<DictationViewState, bool> done)
    {
        var before = rig.View.States.Count;
        rig.Dictate(_ => rig.View.States.Skip(before).Any(done));
    }

    /// <summary>Copies whose clipboard writes each wait until the test lets
    /// them go.</summary>
    internal sealed class HeldCopy
    {
        private readonly List<(TaskCompletionSource<bool> Answer, Func<bool> Write)> _copies = [];

        public int Calls => _copies.Count;

        public Task<bool> Run(Func<bool> write)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _copies.Add((answer, write));
            return answer.Task;
        }

        /// <summary>The <paramref name="copy"/>th write runs now, and its
        /// answer goes back.</summary>
        public void Finish(int copy = 0) => _copies[copy].Answer.SetResult(_copies[copy].Write());
    }

    /// <summary>Let the <paramref name="copy"/>th write go, and run what its
    /// answer posts back.</summary>
    internal static void Answer(Rig rig, HeldCopy held, int copy = 0)
    {
        var posted = rig.Dispatcher.Posted;
        held.Finish(copy);
        Assert.True(SpinWait.SpinUntil(() => rig.Dispatcher.Posted > posted, TimeSpan.FromSeconds(10)), "the answer was never posted");
        rig.Dispatcher.Drain();
    }

    private static TimeSpan CopyDeadline => new DictationControllerOptions().ClipboardCopyDeadline;

    // ─── The macOS scenarios ────────────────────────────────────────────

    [Fact]
    public async Task The_final_formatted_text_can_be_copied_again_without_another_transcription()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var rig = new Rig(logs: logs);
        Assert.Null(rig.View.Last.Recent);
        Assert.Null(rig.View.Last.Tray.Recent);
        rig.Controller.CopyLastText();
        Assert.Empty(rig.Platform.Clipboard.Writes);

        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var typed = rig.Platform.Clipboard.Writes[0];
        Assert.Equal(typed, Section(rig).Text);
        Assert.Equal(DictationCopy.RecentTitle, Section(rig).Title);
        Assert.Equal(DictationCopy.RecentDetail, Section(rig).Detail);
        Assert.False(Menu(rig).NeedsRecovery);
        Assert.True(Section(rig).CanCopy);

        var writes = rig.Platform.Clipboard.Writes.Count;
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();

        Assert.Equal(writes + 1, rig.Platform.Clipboard.Writes.Count);
        Assert.Equal(typed, rig.Platform.Clipboard.Text);
        Assert.Equal(1, Transcribes(rig));
        Assert.Equal(DictationCopy.HudCopied, rig.Hud.Current?.Text);
        Assert.Contains("Ctrl+V", Section(rig).CopyMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("hello world", File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName)), StringComparison.OrdinalIgnoreCase);

        rig.Controller.Dispose();
        Assert.False(rig.Controller.HoldsRecentText);
        // Nothing reached the disk to survive a relaunch: no file under this
        // copy's folders (settings, data, logs) holds the words. This scan is
        // the guard; a second Rig would get its own folders and prove nothing.
        foreach (var file in Directory.EnumerateFiles(rig.Copy.Root, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("hello world", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_refused_clipboard_does_not_lose_the_transcript_and_a_later_copy_recovers_it()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Clipboard.Accepts = false;
        rig.Platform.Paste.Succeeds = false;

        rig.Dictate(v => v.Status == DictationCopy.WordsStillHere);

        Assert.Equal("Hello world", Section(rig).Text, ignoreCase: true);
        Assert.Equal(DictationCopy.RecentTitleRecovery, Section(rig).Title);
        Assert.Equal(DictationCopy.RecentDetailRecovery, Section(rig).Detail);
        Assert.True(Menu(rig).NeedsRecovery);
        Assert.Equal(DictationCopy.HudWordsStillHere, rig.Hud.Current?.Text);

        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudCopyFailed, rig.Hud.Current?.Text);
        Assert.Equal(DictationCopy.CopyFailed, Section(rig).CopyMessage);
        Assert.DoesNotContain("Ctrl+V", Section(rig).CopyMessage, StringComparison.Ordinal);
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);

        rig.Platform.Clipboard.Accepts = true;
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudCopied, rig.Hud.Current?.Text);
        Assert.Equal("Hello world", rig.Platform.Clipboard.Text, ignoreCase: true);
    }

    /// <summary>The Mac's secure targets are a password field and Secure
    /// Input, met at the paste; Windows has only the first, and finds it at
    /// the release, before recognition, or at the paste (that case is in
    /// FieldCheckTests).</summary>
    [Fact]
    public async Task Secure_targets_discard_the_current_recovery_buffer()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.NotNull(rig.View.Last.Recent);
        var writes = rig.Platform.Clipboard.Writes.Count;

        rig.Press();
        rig.Platform.SecureField.Next = SecureFieldState.Secure;
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtRelease);

        Assert.Null(rig.View.Last.Recent);
        Assert.Null(rig.View.Last.Tray.Recent);
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(writes, rig.Platform.Clipboard.Writes.Count);
    }

    [Fact]
    public async Task Only_one_result_is_kept_and_dismiss_clears_it_without_changing_the_clipboard()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var first = Section(rig).Id;
        rig.Platform.Worker.Transcript = "another sentence";

        DictateAgain(rig, Inserted);

        Assert.NotEqual(first, Section(rig).Id);
        Assert.Equal("Another sentence", Section(rig).Text);
        var clipboard = rig.Platform.Clipboard.Text;
        var writes = rig.Platform.Clipboard.Writes.Count;

        rig.Controller.DismissLastText();

        Assert.Null(rig.View.Last.Recent);
        Assert.Null(rig.View.Last.Tray.Recent);
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(clipboard, rig.Platform.Clipboard.Text);
        Assert.Equal(writes, rig.Platform.Clipboard.Writes.Count);
    }

    [Fact]
    public void A_late_copy_cannot_label_a_replaced_result_as_copied_or_submit_twice()
    {
        var result = new DictationRecentResult();
        result.Remember("first", needsRecovery: true, DateTimeOffset.UnixEpoch);

        var copying = result.BeginCopy();
        Assert.NotNull(copying);
        Assert.True(result.Copying);
        Assert.Null(result.BeginCopy());
        result.Remember("second", needsRecovery: false, DateTimeOffset.UnixEpoch);

        Assert.Null(result.FinishCopy(copying, succeeded: true));
        Assert.Null(result.CopyMessage);
        Assert.False(result.Copying);
        Assert.Equal("second", result.Current?.Text);
    }

    [Fact]
    public async Task Copy_cannot_interfere_with_an_active_recording()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var writes = rig.Platform.Clipboard.Writes.Count;

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.True(rig.View.Last.CanCancel);
        Assert.False(Section(rig).CanCopy);
        Assert.False(Menu(rig).CanCopy);
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();

        Assert.Equal(writes, rig.Platform.Clipboard.Writes.Count);
        rig.Controller.Cancel();
    }

    // ─── The Windows edges ──────────────────────────────────────────────

    [Fact]
    public async Task Copy_waits_for_the_last_dictation_to_finish_typing()
    {
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        rig.Platform.Worker.HoldTranscribe = hold;
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        var writes = rig.Platform.Clipboard.Writes.Count;

        Assert.False(Section(rig).CanCopy);
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(writes, rig.Platform.Clipboard.Writes.Count);

        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.True(Section(rig).CanCopy);
    }

    [Fact]
    public async Task Copy_waits_for_a_press_whose_password_check_is_still_out()
    {
        using var rig = new Rig(options: o => o with { RunFieldCheck = check => Task.Run(check) });
        await rig.EnableAsync();
        rig.Platform.Trigger.Down = true;
        rig.TickUntil(() => rig.Platform.Microphone.Open);
        rig.Platform.Microphone.Speak();
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        var writes = rig.Platform.Clipboard.Writes.Count;

        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(writes, rig.Platform.Clipboard.Writes.Count);

        gate.Set();
        rig.TickUntil(() => rig.Platform.Microphone.Open);
        rig.Controller.Cancel();
        rig.Release();
    }

    [Fact]
    public async Task A_press_during_a_copy_is_refused_like_a_press_during_a_dictation()
    {
        var copy = new HeldCopy();
        using var rig = new Rig(options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var starts = rig.Platform.Microphone.Starts;

        rig.Controller.CopyLastText();
        Assert.True(Section(rig).Copying);
        Assert.Equal(DictationCopy.CopyingLastText, Section(rig).CopyLabel);
        Assert.False(Section(rig).CanCopy);
        Assert.False(Section(rig).CanDismiss);
        Assert.False(Menu(rig).CanCopy);

        // One click is one write, and Dismiss waits for the copy.
        rig.Controller.CopyLastText();
        rig.Controller.DismissLastText();
        Assert.Equal(1, copy.Calls);
        Assert.NotNull(rig.View.Last.Recent);

        var before = rig.View.Last.Status;
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.StillCopyingLastText(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.Equal(DictationCopy.HudStillCopying, rig.Hud.Current?.Text);
        Assert.Equal(starts, rig.Platform.Microphone.Starts);
        Assert.False(rig.View.Last.CanCancel);

        // A second refused press does not count the first refusal as the line to put back.
        rig.Release();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.StillCopyingLastText(rig.Controller.HoldKey), rig.View.Last.Status);

        copy.Finish();
        rig.DrainUntil(() => !Section(rig).Copying);
        Assert.Equal(DictationCopy.Copied, Section(rig).CopyMessage);
        Assert.Equal(DictationCopy.CopyLastText, Section(rig).CopyLabel);
        // No dictation follows to replace the refusal: the line it replaced comes back.
        Assert.Equal(before, rig.View.Last.Status);

        // Letting go of the refused press starts nothing; the next is a dictation.
        rig.Release();
        rig.Dispatcher.Drain();
        Assert.Equal(1, Transcribes(rig));
        DictateAgain(rig, Inserted);
        Assert.Equal(2, Transcribes(rig));
    }

    [Fact]
    public async Task The_held_text_survives_a_pause_never_expires_and_copies_while_paused()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var held = Section(rig).Id;

        rig.Controller.Pause();
        rig.Clock.Advance(TimeSpan.FromDays(2));
        rig.Now += TimeSpan.FromDays(2);
        rig.Controller.Tick();

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(held, Section(rig).Id);
        Assert.True(Section(rig).CanCopy);
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal("Hello world", rig.Platform.Clipboard.Text, ignoreCase: true);
        Assert.Equal(DictationCopy.HudCopied, rig.Hud.Current?.Text);
    }

    [Fact]
    public async Task Cancelled_and_failed_dictations_keep_the_older_text_and_its_time()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var held = Section(rig);
        rig.Now += TimeSpan.FromMinutes(5);

        // Nothing heard.
        var before = rig.View.States.Count;
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Microphone.Hush();
        rig.Release();
        rig.DrainUntil(() => rig.View.States.Skip(before).Any(s => s.Status.StartsWith("Nothing was heard", StringComparison.Ordinal)));
        Assert.Equal(held.Id, Section(rig).Id);
        Assert.Equal(held.Time, Section(rig).Time);

        // Cancelled.
        rig.Press();
        rig.Controller.Cancel();
        rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
        rig.Release();
        Assert.Equal(held.Id, Section(rig).Id);
    }

    /// <summary>The Mac's nuance: a press-time password-field refusal is not
    /// a result, and leaves the held text alone.</summary>
    [Fact]
    public async Task A_password_field_at_the_press_leaves_the_held_text_alone()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var held = Section(rig).Id;

        rig.Platform.SecureField.Next = SecureFieldState.Secure;
        rig.Press();
        rig.Release();
        rig.Dispatcher.Drain();

        Assert.Equal(DictationCopy.SecureFieldAtPress, rig.View.Last.Status);
        Assert.Equal(held, Section(rig).Id);
    }

    [Fact]
    public async Task A_refused_paste_kept_on_the_clipboard_is_still_offered_for_recovery()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Paste.Succeeds = false;

        rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboard);

        Assert.Equal(DictationCopy.RecentTitleRecovery, Section(rig).Title);
        Assert.True(Menu(rig).NeedsRecovery);
        Assert.Equal(DictationCopy.HudKeptOnClipboard, rig.Hud.Current?.Text);
    }

    [Fact]
    public async Task With_no_window_and_a_refusing_clipboard_the_words_are_kept_in_Dictation()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Target.Next = FocusedApplicationResult.NoFocusedWindow;
        rig.Platform.Clipboard.Accepts = false;

        rig.Dictate(v => v.Status == DictationCopy.WordsStillHereNoWindow);

        Assert.Equal(DictationCopy.HudWordsStillHere, rig.Hud.Current?.Text);
        Assert.Equal(DictationCopy.RecentTitleRecovery, Section(rig).Title);
    }

    [Theory]
    [InlineData("de-DE", "15:41")]
    [InlineData("en-US", "3:41")]
    public async Task The_section_and_the_menu_name_the_time_in_the_culture_s_short_time_and_the_menu_never_the_words(
        string culture, string time)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            using var rig = new Rig();
            rig.Now = new DateTimeOffset(new DateTime(2026, 10, 9, 15, 41, 0, DateTimeKind.Local));
            await rig.EnableAsync();
            rig.Dictate(Inserted);

            Assert.StartsWith(time, Section(rig).Time, StringComparison.Ordinal);
            Assert.Equal(culture == "en-US", Section(rig).Time.Contains("PM", StringComparison.Ordinal));
            Assert.Equal($"Copy last text · {Section(rig).Time}", Menu(rig).CopyLabel);
            Assert.DoesNotContain("hello", Menu(rig).CopyLabel, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task The_recovery_and_copy_log_lines_never_hold_the_words()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var rig = new Rig(logs: logs);
        rig.Platform.Worker.Transcript = "my bank pin is four four two one";
        await rig.EnableAsync();
        rig.Platform.Paste.Succeeds = false;
        rig.Platform.Clipboard.Accepts = false;
        rig.Dictate(v => v.Status == DictationCopy.WordsStillHere);

        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        rig.Platform.Clipboard.Throws = true;
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudCopyFailed, rig.Hud.Current?.Text);
        Assert.NotNull(rig.View.Last.Recent);
        rig.Platform.Clipboard.Throws = false;
        rig.Platform.Clipboard.Accepts = true;
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        rig.Controller.DismissLastText();

        var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        // The recovery and copy lines are the refused hold's own receipt
        // lines (DictationAttemptWiringTests has their order).
        var attempt = System.Text.RegularExpressions.Regex.Match(
            log, "dictation attempt=([0-9a-f-]+) event=finished result=failed stage=paste reason=paste_refused").Groups[1].Value;
        Assert.NotEmpty(attempt);
        Assert.Contains($"dictation attempt={attempt} event=recovery clipboard=false available_in_app=true", log, StringComparison.Ordinal);
        Assert.Equal(2, log.Split($"dictation attempt={attempt} event=copy result=failed available_in_app=true").Length - 1);
        Assert.Contains("the clipboard write threw (InvalidOperationException)", log, StringComparison.Ordinal);
        Assert.Contains($"dictation attempt={attempt} event=copy result=copied", log, StringComparison.Ordinal);
        Assert.Contains("The last text was dismissed", log, StringComparison.Ordinal);
        Assert.DoesNotContain("bank pin", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("four four", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_copy_answered_after_quitting_changes_nothing()
    {
        var copy = new HeldCopy();
        using var rig = new Rig(options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        rig.Controller.CopyLastText();

        rig.Controller.Dispose();
        var rendered = rig.View.States.Count;
        var posted = rig.Dispatcher.Posted;
        copy.Finish();
        Assert.True(SpinWait.SpinUntil(() => rig.Dispatcher.Posted > posted, TimeSpan.FromSeconds(10)), "the answer was never posted");
        rig.Dispatcher.Drain();

        Assert.Equal(rendered, rig.View.States.Count);
        Assert.False(rig.Controller.HoldsRecentText);
        Assert.NotEqual(DictationCopy.HudCopied, rig.Hud.Current?.Text);
    }

    /// <summary>The Mac's "remember every success": one ended by the
    /// recording limit is a success too.</summary>
    [Fact]
    public async Task A_dictation_stopped_at_the_limit_is_kept()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Press();
        rig.Clock.Advance(DictationHoldPolicy.Windows.Ceiling);
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.InsertedAfterStop(new(DictationHoldStop.Ceiling, DictationHoldPolicy.Windows.Ceiling), rig.Controller.HoldKey));

        Assert.Equal("Hello world", Section(rig).Text, ignoreCase: true);
        Assert.Equal(DictationCopy.RecentTitle, Section(rig).Title);
        Assert.False(Menu(rig).NeedsRecovery);
        rig.Release();
    }

    /// <summary>A refusal during the copy is replaced by anything newer the
    /// window said meanwhile, and the copy's end leaves that alone.</summary>
    [Fact]
    public async Task A_pause_during_a_copy_keeps_its_own_line_when_the_copy_ends()
    {
        var copy = new HeldCopy();
        using var rig = new Rig(options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);

        rig.Controller.CopyLastText();
        rig.Press();
        rig.Release();
        Assert.Equal(DictationCopy.StillCopyingLastText(rig.Controller.HoldKey), rig.View.Last.Status);
        rig.Controller.Pause();
        Answer(rig, copy);

        Assert.Equal(DictationCopy.Copied, Section(rig).CopyMessage);
        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
    }

    [Fact]
    public async Task A_copy_the_clipboard_never_answers_fails_at_the_deadline_and_frees_the_hold_key()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        var copy = new HeldCopy();
        using var rig = new Rig(logs: logs, options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);

        rig.Controller.CopyLastText();
        rig.Clock.Advance(CopyDeadline - TimeSpan.FromMilliseconds(1));
        rig.Controller.Tick();
        Assert.True(Section(rig).Copying);

        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        rig.Controller.Tick();
        Assert.False(Section(rig).Copying);
        Assert.True(Section(rig).CanCopy);
        Assert.True(Section(rig).CanDismiss);
        Assert.Equal(DictationCopy.CopyFailed, Section(rig).CopyMessage);
        Assert.Equal(DictationCopy.HudCopyFailed, rig.Hud.Current?.Text);

        // The write answers at last, and that answer changes nothing.
        Answer(rig, copy);
        Assert.Equal(DictationCopy.CopyFailed, Section(rig).CopyMessage);
        Assert.Equal(DictationCopy.HudCopyFailed, rig.Hud.Current?.Text);

        // The hold key records again.
        DictateAgain(rig, Inserted);
        Assert.Equal(2, Transcribes(rig));
        Assert.Contains(
            $"Copy last text: the clipboard did not answer within {CopyDeadline.TotalSeconds} s; treated as failed",
            File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_late_answer_of_a_copy_given_up_on_does_not_end_the_next_copy()
    {
        var copy = new HeldCopy();
        using var rig = new Rig(options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var inserted = rig.View.Last.Status;
        rig.Controller.CopyLastText();
        rig.Clock.Advance(CopyDeadline);
        rig.Controller.Tick();

        rig.Controller.CopyLastText();
        rig.Press();
        rig.Release();
        var refused = DictationCopy.StillCopyingLastText(rig.Controller.HoldKey);
        Assert.Equal(refused, rig.View.Last.Status);

        Answer(rig, copy, 0);
        Assert.True(Section(rig).Copying);
        Assert.Equal(refused, rig.View.Last.Status);
        Assert.NotEqual(DictationCopy.HudCopied, rig.Hud.Current?.Text);

        Answer(rig, copy, 1);
        Assert.False(Section(rig).Copying);
        Assert.Equal(DictationCopy.Copied, Section(rig).CopyMessage);
        Assert.Equal(inserted, rig.View.Last.Status);
        Assert.Equal(2, copy.Calls);
    }

    /// <summary>The records the window is drawn from print the length of the
    /// words, never the words: one log line or exception that formats the
    /// view state would otherwise write what was said.</summary>
    [Fact]
    public async Task The_entry_the_section_and_the_view_state_print_without_the_words()
    {
        using var rig = new Rig();
        rig.Platform.Worker.Transcript = "my bank pin is four four two one";
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.Contains("bank pin", Section(rig).Text, StringComparison.OrdinalIgnoreCase);
        var entry = new DictationRecentResult.Entry(Guid.NewGuid(), "my bank pin", DateTimeOffset.UnixEpoch, false);

        foreach (var printed in new[] { entry.ToString(), Section(rig).ToString(), rig.View.Last.ToString() })
        {
            Assert.DoesNotContain("bank pin", printed, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("characters", printed, StringComparison.Ordinal);
        }
    }

    // ─── The buffer itself ──────────────────────────────────────────────

    [Fact]
    public void A_late_answer_from_a_copy_given_up_on_does_not_finish_the_next()
    {
        var result = new DictationRecentResult();
        result.Remember("held", needsRecovery: false, DateTimeOffset.UnixEpoch);
        var first = result.BeginCopy()!;
        Assert.False(result.FinishCopy(first, succeeded: false));
        var second = result.BeginCopy()!;
        Assert.NotSame(first, second);

        Assert.Null(result.FinishCopy(first, succeeded: true));
        Assert.Same(second, result.Pending);
        Assert.Equal(DictationCopy.CopyFailed, result.CopyMessage);

        Assert.True(result.FinishCopy(second, succeeded: true));
        Assert.False(result.Copying);
        Assert.Equal(DictationCopy.Copied, result.CopyMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n\t")]
    public void Blank_text_is_no_usable_result_and_clears_the_held_one(string? blank)
    {
        var result = new DictationRecentResult();
        result.Remember("held", needsRecovery: false, DateTimeOffset.UnixEpoch);

        result.Remember(blank, needsRecovery: true, DateTimeOffset.UnixEpoch);

        Assert.Null(result.Current);
    }

    [Fact]
    public void A_new_result_replaces_the_held_one_and_its_copy_message()
    {
        var result = new DictationRecentResult();
        result.Remember("first", needsRecovery: false, DateTimeOffset.UnixEpoch);
        var copying = result.BeginCopy()!;
        Assert.False(result.FinishCopy(copying, succeeded: false));
        Assert.Equal(DictationCopy.CopyFailed, result.CopyMessage);
        Assert.Equal("first", result.Current?.Text);

        result.Remember("second", needsRecovery: true, DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.Null(result.CopyMessage);
        Assert.Equal(("second", true), (result.Current?.Text, result.Current?.NeedsRecovery == true));
        Assert.NotEqual(copying.Entry.Id, result.Current?.Id);
    }

    public static TheoryData<DictationOutcome> Outcomes()
    {
        var data = new TheoryData<DictationOutcome>();
        foreach (var outcome in Enum.GetValues<DictationOutcome>())
        {
            data.Add(outcome);
        }

        return data;
    }

    /// <summary>Every outcome the session can end with, over an entry already
    /// held: delivered words replace it, refused words replace it for
    /// recovery, a password field clears it, and everything else keeps it.</summary>
    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Each_ending_keeps_replaces_or_clears_the_held_text(DictationOutcome outcome)
    {
        var result = new DictationRecentResult();
        result.Remember("older", needsRecovery: false, DateTimeOffset.UnixEpoch);
        var older = result.Current!;

        result.Remember(
            new DictationSessionResult(outcome, "typed words", null, UndeliveredText: "refused words"),
            DateTimeOffset.UnixEpoch.AddMinutes(1));

        switch (outcome)
        {
            case DictationOutcome.Inserted or DictationOutcome.ClipboardRestoreFailed:
                Assert.Equal(("typed words", false), (result.Current?.Text, result.Current!.NeedsRecovery));
                break;
            case DictationOutcome.InsertionRefused or DictationOutcome.NoFocusedWindow:
                Assert.Equal(("refused words", true), (result.Current?.Text, result.Current!.NeedsRecovery));
                break;
            case DictationOutcome.SecureField:
                Assert.Null(result.Current);
                break;
            default:
                Assert.Same(older, result.Current);
                break;
        }
    }

    // ─── The indicator's copy endings ───────────────────────────────────

    /// <summary>The Mac's "only a successful clipboard write promises ⌘V",
    /// over every line this product wrote for the pill.</summary>
    [Fact]
    public void Only_a_successful_clipboard_write_promises_Ctrl_V()
    {
        var lines = typeof(DictationCopy).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name.StartsWith("Hud", StringComparison.Ordinal))
            .ToDictionary(f => f.Name, f => (string)f.GetValue(null)!);

        foreach (var (name, line) in lines)
        {
            var promises = line.Contains("Ctrl+V", StringComparison.Ordinal);
            Assert.True(
                promises == (name is nameof(DictationCopy.HudKeptOnClipboard) or nameof(DictationCopy.HudCopied)),
                $"{name}: \"{line}\"");
        }

        Assert.Contains(nameof(DictationCopy.HudWordsStillHere), lines.Keys);
        Assert.Contains(nameof(DictationCopy.HudCopyFailed), lines.Keys);
    }

    [Fact]
    public void Copied_is_a_short_receipt_and_a_failed_copy_a_caution_long_enough_to_act_on()
    {
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => TimeSpan.Zero, () => FakeHud.Hold);

        feed.Copied(true);
        Assert.Equal((HudTone.Neutral, DictationHudModel.NoticeDwell), (hud.Current!.Tone, hud.Current.Dwell));
        feed.Copied(false);
        Assert.Equal((HudTone.Caution, DictationHudModel.FailureDwell), (hud.Current!.Tone, hud.Current.Dwell));
        Assert.True(hud.Announced.All(a => a), "a copy ending is spoken: no microphone is open");
    }
}
