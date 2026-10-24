using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Dictation.Services.Learning;
using Dictation.Speech;

namespace Dictation.Core.Tests;

// LIMIT-WIN-A through the controller the app runs: the spool of every take,
// the take kept when it cannot be transcribed, Recover last dictation in the
// window and the notification-area menu, and the take a closing Dictation
// left offered at the next launch (LIMIT-DESIGN §3.1 and §5 step 3: "the
// spool is written during a take, deleted after insert, kept after a
// failure; an orphaned spool at launch triggers the offer"). The Mac's spool
// comes in its own stage 1, so these are the design's scenarios.

/// <summary>The spool, the kept take and Recover last dictation.</summary>
public sealed class TakeTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static string[] Files(Rig rig) =>
        Directory.Exists(rig.RecoveryDirectory)
            ? [.. Directory.GetFiles(rig.RecoveryDirectory).Select(Path.GetFileName)!]
            : [];

    private static int Transcribes(Rig rig) => rig.Platform.Worker.Ops().Count(op => op == "transcribe");

    /// <summary>A dictation the worker refuses: alive, it says no, so the
    /// decode is not retried and the take is kept.</summary>
    private static async Task<Rig> WithAKeptTake(Func<DictationControllerOptions, DictationControllerOptions>? options = null)
    {
        var rig = new Rig(options: options);
        await rig.EnableAsync();
        rig.Platform.Worker.RefuseTranscribe = true;
        rig.Dictate(v => v.Status == DictationCopy.TakeSaved);
        rig.Platform.Worker.RefuseTranscribe = false;
        return rig;
    }

    // ─── The spool ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_take_is_spooled_while_it_is_recorded_and_deleted_once_its_words_went_in()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Press();

        // On disk while it still records: what a crash now leaves.
        var file = Assert.Single(Directory.GetFiles(rig.RecoveryDirectory));
        Assert.Equal((rig.Now, 8_000L), DictationTakeSpool.ReadHeader(file));

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Empty(Files(rig));
        Assert.Null(rig.View.Last.SavedTake);
        Assert.Null(rig.View.Last.Tray.SavedTake);
    }

    [Fact]
    public async Task Only_the_take_s_own_audio_is_spooled()
    {
        // Audio before the press, and after the release, is no take's.
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        await rig.EnableAsync();
        rig.Platform.Microphone.Speak();
        rig.Platform.Worker.HoldTranscribe = decode;

        rig.Press();
        rig.Release();
        rig.Platform.Microphone.Speak();

        try
        {
            var file = Assert.Single(Directory.GetFiles(rig.RecoveryDirectory));
            Assert.Equal(8_000L, DictationTakeSpool.ReadHeader(file)!.Value.Samples);
        }
        finally
        {
            decode.Set();
        }

        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Fact]
    public async Task A_take_that_could_not_be_transcribed_is_kept_and_offered_in_the_window_and_the_menu()
    {
        using var rig = await WithAKeptTake();

        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        var saved = rig.View.Last.SavedTake;
        Assert.NotNull(saved);
        Assert.Equal(DictationCopy.SavedTakeTitle, saved!.Title);
        Assert.Equal(DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(0.5)), saved.Detail);
        Assert.Equal(DictationCopy.ShortTime(rig.Now), saved.Time);
        Assert.Equal(DictationCopy.RecoverLastDictation, saved.RecoverLabel);
        Assert.True(saved.CanRecover);
        Assert.True(saved.CanDiscard);
        Assert.Equal(DictationCopy.HudTakeSaved, rig.Hud.Current!.Text);
        Assert.Equal(HudTone.Caution, rig.Hud.Current.Tone);

        var menu = rig.View.Last.Tray.SavedTake;
        Assert.Equal(new DictationSavedTakeMenu(DictationCopy.RecoverLastDictationAt(DictationCopy.ShortTime(rig.Now)), true), menu);
        Assert.Equal(DictationTrayItem.RecoverLastDictation, DictationTrayMenu.Items(rig.View.Last.Tray, learningUndoable: false)[0]);
        // Nothing of it in the words the app holds.
        Assert.Null(rig.View.Last.Recent);
    }

    [Fact]
    public async Task A_newer_failure_replaces_the_kept_take()
    {
        using var rig = await WithAKeptTake();
        rig.Now = rig.Now.AddMinutes(3);
        rig.Platform.Worker.RefuseTranscribe = true;

        rig.Press();
        rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(1));
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.SavedTake?.Time == DictationCopy.ShortTime(rig.Now));

        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        // One is kept at a time, and the section says the earlier one went.
        Assert.Equal(
            DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(1.5)) + " " + DictationCopy.SavedTakeReplacedEarlier,
            rig.View.Last.SavedTake!.Detail);
    }

    [Fact]
    public async Task Escape_discards_the_take_and_its_spool()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");

        Assert.Empty(Files(rig));
        Assert.Null(rig.View.Last.SavedTake);
    }

    [Fact]
    public async Task A_shortcut_that_ends_a_long_take_keeps_it_and_a_short_one_is_only_a_shortcut()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.CancelledByShortcut(rig.Controller.HoldKey));
        Assert.Empty(Files(rig));
        rig.Release();

        rig.Press();
        rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(31));
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.CancelledByShortcutTakeSaved(rig.Controller.HoldKey));

        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        Assert.Equal(DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(31.5)), rig.View.Last.SavedTake!.Detail);
        Assert.Equal(DictationCopy.HudCancelledByShortcutTakeSaved, rig.Hud.Current!.Text);
        Assert.Equal(0, rig.Platform.Paste.Count);
    }

    // ─── Recover last dictation ─────────────────────────────────────────

    [Fact]
    public async Task Recover_transcribes_the_kept_take_and_its_words_go_to_the_recent_result_and_the_clipboard()
    {
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.Transcript = "the words that were kept";

        rig.Controller.RecoverLastDictation();
        Assert.Equal(DictationCopy.Recovering, rig.View.Last.Status);
        Assert.Equal(DictationCopy.RecoveringLastDictation, rig.View.Last.SavedTake!.RecoverLabel);
        Assert.False(rig.View.Last.SavedTake.CanDiscard);
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);

        Assert.Equal(DictationCopy.Recovered, rig.View.Last.Status);
        Assert.Equal("the words that were kept", rig.View.Last.Recent?.Text);
        Assert.Equal(DictationCopy.Copied, rig.View.Last.Recent?.CopyMessage);
        Assert.Equal("the words that were kept", rig.Platform.Clipboard.Text);
        Assert.Equal(DictationCopy.HudCopied, rig.Hud.Current!.Text);
        // Never pasted: the user is in Dictation's window or menu.
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Empty(Files(rig));
        // The recording itself went back to the worker: half a second of it.
        var frame = rig.Platform.Worker.FramesOf("transcribe")[^1];
        Assert.Equal(8_000 * 2, Convert.FromBase64String(frame.GetProperty("pcm_b64").GetString()!).Length);
    }

    [Fact]
    public async Task Recover_from_the_menu_is_the_window_s_Recover()
    {
        using var rig = await WithAKeptTake();
        var vocabulary = new VocabularyModel(
            new DictationLearning(
                new LearningStore(rig.LearningDirectory),
                new WordListLexicon(LearningGoldenTests.SharedLexicon)),
            new RecordingVocabularyView(),
            rig.Dispatcher.Post);
        var commands = new DictationTrayCommands(rig.Controller, vocabulary, () => { }, () => { }, () => { });

        await commands.ChooseAsync(DictationTrayItem.RecoverLastDictation);
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);

        Assert.Equal(DictationCopy.Recovered, rig.View.Last.Status);
    }

    [Fact]
    public async Task A_recovery_that_fails_keeps_the_take_and_says_so()
    {
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.RefuseTranscribe = true;

        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.RecoveryFailed);

        Assert.NotNull(rig.View.Last.SavedTake);
        Assert.True(rig.View.Last.SavedTake!.CanRecover);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        Assert.Equal(DictationCopy.HudRecoveryFailed, rig.Hud.Current!.Text);
    }

    [Fact]
    public async Task A_kept_take_that_held_nothing_to_type_is_discarded_when_recovered()
    {
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.Transcript = "   ";

        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.RecoveredNothingHeard);

        Assert.Null(rig.View.Last.SavedTake);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task A_kept_take_that_cannot_be_read_is_discarded_and_said_so()
    {
        using var rig = await WithAKeptTake();
        File.WriteAllText(Path.Combine(rig.RecoveryDirectory, DictationTakeSpool.KeptFileName), "damaged");

        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.RecoveryUnreadable);

        Assert.Null(rig.View.Last.SavedTake);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task Recover_waits_for_dictation_and_for_rest()
    {
        using var rig = await WithAKeptTake();

        // Under a dictation: not offered, and a stray call does nothing.
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.False(rig.View.Last.SavedTake!.CanRecover);
        Assert.False(rig.View.Last.Tray.SavedTake!.CanRecover);
        var before = Transcribes(rig);
        rig.Controller.RecoverLastDictation();
        Assert.Equal(before, Transcribes(rig));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        // Paused: the speech engine is off, and the section says to turn it on.
        Assert.True(rig.Controller.TryPause());
        Assert.False(rig.View.Last.SavedTake!.CanRecover);
        Assert.EndsWith(DictationCopy.SavedTakeNeedsDictation, rig.View.Last.SavedTake.Detail, StringComparison.Ordinal);
        rig.Controller.RecoverLastDictation();
        Assert.NotEqual(DictationCopy.Recovering, rig.View.Last.Status);

        await rig.EnableAsync();
        Assert.True(rig.View.Last.SavedTake!.CanRecover);
    }

    [Fact]
    public async Task Pause_waits_for_a_recovery_and_the_idle_unload_never_stops_one()
    {
        using var decode = new ManualResetEventSlim();
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.HoldTranscribe = decode;
        try
        {
            rig.Controller.RecoverLastDictation();
            Assert.True(SpinWait.SpinUntil(() => Transcribes(rig) == 2, TimeSpan.FromSeconds(10)));
            Assert.False(rig.View.Last.CanPause);
            Assert.False(rig.Controller.TryPause());

            rig.Clock.Advance(TimeSpan.FromMinutes(10));
            rig.Controller.Tick();
            Assert.True(rig.Platform.Worker.IsAlive);
        }
        finally
        {
            decode.Set();
        }

        rig.DrainUntil(() => rig.View.Last.SavedTake is null);
        Assert.Equal(DictationCopy.Recovered, rig.View.Last.Status);
        Assert.Equal(1, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task A_recovery_never_deletes_a_newer_kept_take()
    {
        // A shortcut ends a long take while the last one is recovered: the
        // new one is kept in its place, and the recovery that ends after it
        // leaves it alone.
        using var decode = new ManualResetEventSlim();
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.HoldTranscribe = decode;
        try
        {
            rig.Controller.RecoverLastDictation();
            Assert.True(SpinWait.SpinUntil(() => Transcribes(rig) == 2, TimeSpan.FromSeconds(10)));
            rig.Now = rig.Now.AddMinutes(5);
            rig.Press();
            rig.Platform.Microphone.Speak(TimeSpan.FromSeconds(31));
            rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
            rig.Controller.Tick();
            rig.DrainUntil(() => rig.View.Last.SavedTake?.Time == DictationCopy.ShortTime(rig.Now));
        }
        finally
        {
            decode.Set();
        }

        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Recovered);
        Assert.Equal(DictationCopy.ShortTime(rig.Now), rig.View.Last.SavedTake?.Time);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        // The new take's 31.5 s, not the recovered one's half second.
        Assert.Equal(
            504_000L,
            DictationTakeSpool.ReadHeader(Path.Combine(rig.RecoveryDirectory, DictationTakeSpool.KeptFileName))!.Value.Samples);
    }

    [Fact]
    public async Task Recovered_words_survive_a_dictation_that_finishes_before_they_are_copied()
    {
        // The recovery ends while a dictation listens, so its words cannot go
        // to the clipboard yet and wait in the Recent result, which the
        // dictation's own result then replaces. Its recording stays until its
        // words are copied, so they are never on no disk, no clipboard and in
        // no Recent result at once (rule zero).
        using var decode = new ManualResetEventSlim();
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.Transcript = "recovered words";
        rig.Platform.Worker.HoldTranscribe = decode;
        try
        {
            rig.Controller.RecoverLastDictation();
            Assert.True(SpinWait.SpinUntil(() => Transcribes(rig) == 2, TimeSpan.FromSeconds(10)));
            rig.Press();
        }
        finally
        {
            decode.Set();
        }

        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.RecoveredUseCopy);
        Assert.Equal("recovered words", rig.View.Last.Recent?.Text);
        Assert.NotEqual("recovered words", rig.Platform.Clipboard.Text);
        // Beside the dictation's own spool, still recording.
        Assert.Contains(DictationTakeSpool.KeptFileName, Files(rig));
        Assert.Equal(
            DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(0.5)) + " " + DictationCopy.SavedTakeAwaitsCopy,
            rig.View.Last.SavedTake?.Detail);

        rig.Platform.Worker.Transcript = "new words";
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        // The dictation's words replaced the recovered ones in the Recent
        // result; the recording they came from is still offered.
        Assert.Equal("New words", rig.View.Last.Recent?.Text);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        Assert.Equal(DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(0.5)), rig.View.Last.SavedTake?.Detail);
        Assert.True(rig.View.Last.SavedTake!.CanRecover);

        // Recovered again at rest, its words reach the clipboard and it goes.
        rig.Platform.Worker.Transcript = "recovered words";
        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);
        Assert.Equal("recovered words", rig.Platform.Clipboard.Text);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task A_recovery_s_recording_stays_until_its_words_reach_the_clipboard()
    {
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.Transcript = "the words that were kept";
        rig.Platform.Clipboard.Accepts = false;

        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.Recent?.CopyMessage is not null && !rig.View.Last.Recent.Copying);

        // The clipboard refused them: the recording is still the backup.
        Assert.Equal("the words that were kept", rig.View.Last.Recent!.Text);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        Assert.EndsWith(DictationCopy.SavedTakeAwaitsCopy, rig.View.Last.SavedTake!.Detail, StringComparison.Ordinal);

        rig.Platform.Clipboard.Accepts = true;
        rig.Controller.CopyLastText();
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);
        Assert.Equal("the words that were kept", rig.Platform.Clipboard.Text);
        Assert.Empty(Files(rig));
        Assert.Null(rig.View.Last.Tray.SavedTake);
    }

    [Fact]
    public async Task Dismissing_a_recovery_s_words_deletes_its_recording()
    {
        using var rig = await WithAKeptTake();
        rig.Platform.Clipboard.Accepts = false;
        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.Recent?.CopyMessage is not null && !rig.View.Last.Recent.Copying);
        Assert.NotNull(rig.View.Last.SavedTake);

        rig.Controller.DismissLastText();

        Assert.Null(rig.View.Last.Recent);
        Assert.Null(rig.View.Last.SavedTake);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task Copying_or_dismissing_other_words_leaves_the_recording()
    {
        // The recovery's words wait (the clipboard refused them), and a
        // dictation's words replace them in the Recent result: copying or
        // dismissing those is not done with the recording.
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.Transcript = "recovered words";
        rig.Platform.Clipboard.Accepts = false;
        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.Recent?.CopyMessage is not null && !rig.View.Last.Recent.Copying);
        rig.Platform.Clipboard.Accepts = true;
        rig.Platform.Worker.Transcript = "new words";
        rig.Dictate(Inserted);

        rig.Controller.CopyLastText();
        rig.DrainUntil(() => rig.View.Last.Recent?.CopyMessage == DictationCopy.Copied);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        rig.Controller.DismissLastText();

        Assert.Null(rig.View.Last.Recent);
        Assert.NotNull(rig.View.Last.SavedTake);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kept_take_is_deleted_when_its_week_runs_out_while_Dictation_runs(bool paused)
    {
        // Dictation starts at sign-in and can run for weeks: the section's
        // "7 days at most" holds without a relaunch, paused or not.
        using var rig = await WithAKeptTake();
        if (paused)
        {
            Assert.True(rig.Controller.TryPause());
        }

        var recorded = rig.Now;
        rig.Now = recorded.AddDays(7) - TimeSpan.FromMinutes(1);
        rig.Controller.Tick();
        Assert.NotNull(rig.View.Last.SavedTake);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));

        rig.Now = recorded.AddDays(7);
        rig.Controller.Tick();

        Assert.Null(rig.View.Last.SavedTake);
        Assert.Null(rig.View.Last.Tray.SavedTake);
        Assert.Empty(Files(rig));
    }

    [Fact]
    public async Task A_kept_take_that_cannot_be_opened_just_now_stays_saved()
    {
        // Another program holds it (an antivirus scan, a backup): that is not
        // a damaged recording, and deleting it would lose the words.
        using var rig = await WithAKeptTake();
        var kept = Path.Combine(rig.RecoveryDirectory, DictationTakeSpool.KeptFileName);
        using (new FileStream(kept, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            rig.Controller.RecoverLastDictation();
            rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.RecoveryNotOpened);
        }

        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        Assert.True(rig.View.Last.SavedTake!.CanRecover);
        Assert.Equal(DictationCopy.HudRecoveryFailed, rig.Hud.Current!.Text);

        rig.Controller.RecoverLastDictation();
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);
        Assert.Equal(rig.Platform.Worker.Transcript, rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task A_recovery_paused_under_it_keeps_the_take_and_says_nothing_of_it()
    {
        // Pause is inactive during a recovery; the internal Pause, the
        // teardown quitting and a failure share, is what can still stop the
        // worker under it. The slot the recovery used is gone: its answer
        // changes nothing.
        using var decode = new ManualResetEventSlim();
        using var rig = await WithAKeptTake();
        rig.Platform.Worker.HoldTranscribe = decode;
        try
        {
            rig.Controller.RecoverLastDictation();
            Assert.True(SpinWait.SpinUntil(() => Transcribes(rig) == 2, TimeSpan.FromSeconds(10)));
            rig.Controller.Pause();
        }
        finally
        {
            decode.Set();
        }

        rig.DrainUntil(() => rig.View.Last.SavedTake is { Recovering: false });
        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
        Assert.DoesNotContain(rig.Hud.Shows, m => m.Text == DictationCopy.HudRecoveryFailed);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
    }

    [Fact]
    public async Task Discard_deletes_the_kept_take_unheard()
    {
        using var rig = await WithAKeptTake();
        var before = Transcribes(rig);

        rig.Controller.DiscardSavedTake();

        Assert.Equal(DictationCopy.SavedTakeDiscarded, rig.View.Last.Status);
        Assert.Null(rig.View.Last.SavedTake);
        Assert.Null(rig.View.Last.Tray.SavedTake);
        Assert.Empty(Files(rig));
        Assert.Equal(before, Transcribes(rig));
    }

    // ─── A take whose ending reaches no window ───────────────────────────

    [Fact]
    public async Task A_take_worth_keeping_whose_result_lands_after_a_quit_is_left_for_the_next_launch()
    {
        // The session answered (a refused decode), but Dictation quit before
        // the answer reached the controller's thread: no window to say "kept"
        // in, so the take stays on disk for the next launch to offer.
        var log = new MemoryLog();
        using var first = new Rig(logs: log);
        await first.EnableAsync();
        first.Platform.Worker.RefuseTranscribe = true;
        first.Press();
        first.Release();
        log.AwaitRaised();

        first.Controller.Dispose();
        first.Dispatcher.Drain();

        var left = Assert.Single(Directory.GetFiles(first.RecoveryDirectory));
        Assert.StartsWith(DictationTakeSpool.TakePrefix, Path.GetFileName(left), StringComparison.Ordinal);
        using var next = new Rig(options: o => o with { Takes = o.Takes with { Directory = first.RecoveryDirectory } });
        Assert.StartsWith(
            DictationCopy.SavedTakeDetailInterrupted(TimeSpan.FromSeconds(0.5)),
            next.View.Last.SavedTake?.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_take_whose_words_went_in_and_whose_result_lands_after_a_quit_is_deleted()
    {
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        log.AwaitRaised();

        rig.Controller.Dispose();
        rig.Dispatcher.Drain();

        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Empty(Directory.GetFiles(rig.RecoveryDirectory));
    }

    [Fact]
    public async Task A_take_the_session_never_answered_is_kept_at_the_next_press()
    {
        // The session's pipeline faults (a bug path: it logs "pipeline threw"
        // and goes back to rest with no result), so the take is never
        // settled. The next press keeps it rather than lose it.
        var log = new ThrowOnceLog("Dictation stages:");
        using var rig = new Rig(logs: log);
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => log.Lines.Any(l => l.StartsWith("Dictation pipeline threw", StringComparison.Ordinal)));
        rig.DrainUntil(() => rig.View.Last.Tray.Icon != TrayIcon.Transcribing && !rig.View.Last.CanCancel);
        Assert.Null(rig.View.Last.SavedTake);

        rig.Press();
        rig.Dispatcher.Drain();

        Assert.Contains(log.Lines, l => l == "A take the session never answered is kept for recovery");
        Assert.Equal(DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(0.5)), rig.View.Last.SavedTake?.Detail);
        Assert.Contains(DictationTakeSpool.KeptFileName, Files(rig));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Fact]
    public async Task A_take_whose_recording_failed_to_save_after_it_was_offered_is_withdrawn_and_said_so()
    {
        // The disk answers late: the take is offered at once, then its file
        // work fails (the folder cannot be made) and the keep finds nothing
        // whole to keep. The window stops promising it.
        var files = new DeferredFiles();
        using var rig = new Rig(options: o => o with { Takes = o.Takes with { RunFileWork = files.Run } });
        await rig.EnableAsync();
        rig.Platform.Worker.RefuseTranscribe = true;
        rig.Dictate(v => v.Status == DictationCopy.TakeSaved);
        Assert.NotNull(rig.View.Last.SavedTake);

        File.WriteAllText(rig.RecoveryDirectory, "not a folder");
        files.RunAll();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.TakeNotSaved);

        Assert.Null(rig.View.Last.SavedTake);
        Assert.Null(rig.View.Last.Tray.SavedTake);
    }

    [Fact]
    public async Task A_withdrawn_keep_offers_the_earlier_saved_recording_again()
    {
        // The second failure is offered in place of the first, then its own
        // file work fails (the folder is briefly not a folder). The spool
        // never touched the first recording, so it is offered again rather
        // than left on disk unseen.
        var files = new DeferredFiles();
        using var rig = new Rig(options: o => o with { Takes = o.Takes with { RunFileWork = files.Run } });
        await rig.EnableAsync();
        rig.Platform.Worker.RefuseTranscribe = true;
        rig.Dictate(v => v.Status == DictationCopy.TakeSaved);
        files.RunAll();
        rig.Dispatcher.Drain();
        var first = rig.View.Last.SavedTake;

        rig.Now = rig.Now.AddMinutes(3);
        rig.Dictate(v => v.Status == DictationCopy.TakeSaved && v.SavedTake?.Time == DictationCopy.ShortTime(rig.Now));
        Assert.EndsWith(DictationCopy.SavedTakeReplacedEarlier, rig.View.Last.SavedTake!.Detail, StringComparison.Ordinal);
        var aside = rig.RecoveryDirectory + ".aside";
        Directory.Move(rig.RecoveryDirectory, aside);
        File.WriteAllText(rig.RecoveryDirectory, "not a folder");
        files.RunAll();
        File.Delete(rig.RecoveryDirectory);
        Directory.Move(aside, rig.RecoveryDirectory);
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.TakeNotSaved);

        Assert.Equal(first, rig.View.Last.SavedTake);
        Assert.Equal([DictationTakeSpool.KeptFileName], Files(rig));
        rig.Platform.Worker.RefuseTranscribe = false;
        rig.Controller.RecoverLastDictation();
        files.RunAll();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Recovered);
        files.RunAll();
        rig.DrainUntil(() => rig.View.Last.SavedTake is null);
    }

    // ─── The next launch ────────────────────────────────────────────────

    [Fact]
    public async Task Quitting_mid_take_leaves_it_and_the_next_launch_offers_it_and_shows_the_window()
    {
        using var first = new Rig();
        await first.EnableAsync();
        first.Press();
        first.Platform.Microphone.Speak(TimeSpan.FromSeconds(1));
        first.Controller.Dispose();
        // Whatever the quit's cancel posted runs too: the take stays.
        first.Dispatcher.Drain();
        var left = Assert.Single(Directory.GetFiles(first.RecoveryDirectory));
        Assert.StartsWith(DictationTakeSpool.TakePrefix, Path.GetFileName(left), StringComparison.Ordinal);

        // Started at sign-in with dictation on: the window comes forward for it.
        using var next = new Rig(
            options: o => o with { Takes = o.Takes with { Directory = first.RecoveryDirectory } },
            beforeStart: rig => rig.Settings.Save(new DictationSettings(Enabled: true)),
            atLogin: true);
        await next.Startup.Enabling;
        next.Dispatcher.Drain();

        Assert.True(next.Startup.ShowWindow);
        var saved = next.View.Last.SavedTake;
        Assert.Equal(DictationCopy.SavedTakeDetailInterrupted(TimeSpan.FromSeconds(1.5)), saved!.Detail);
        Assert.Equal([DictationTakeSpool.KeptFileName], Directory.GetFiles(first.RecoveryDirectory).Select(Path.GetFileName));

        next.Controller.RecoverLastDictation();
        next.DrainUntil(() => next.View.Last.SavedTake is null);
        Assert.Equal(next.Platform.Worker.Transcript, next.View.Last.Recent?.Text);
    }

    [Fact]
    public async Task A_launch_with_nothing_left_offers_nothing_and_keeps_the_sign_in_launch_hidden()
    {
        using var rig = new Rig(
            beforeStart: r => r.Settings.Save(new DictationSettings(Enabled: true)),
            atLogin: true);
        await rig.Startup.Enabling;

        Assert.False(rig.Startup.ShowWindow);
        Assert.Null(rig.View.Last.SavedTake);
    }

    [Fact]
    public async Task A_kept_take_from_a_week_ago_is_deleted_at_launch_unoffered()
    {
        using var old = await WithAKeptTake();

        using var next = new Rig(options: o => o with
        {
            Takes = o.Takes with { Directory = old.RecoveryDirectory },
            Now = () => old.Now.AddDays(7),
        });

        Assert.Null(next.View.Last.SavedTake);
        Assert.Empty(Directory.GetFiles(old.RecoveryDirectory));
    }
}
