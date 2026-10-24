using System.Reflection;
using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The floating indicator's feed: what a hold shows while the window is
/// hidden, driven through the controller the app runs, and how long each pill
/// stays on the controller's clock.
/// </summary>
public sealed class DictationHudFeedTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    /// <summary>The fake worker's transcript: never on the pill.</summary>
    private static readonly string[] TranscriptWords = ["hello", "world"];

    private static DictationHudModel Current(Rig rig) => rig.Hud.Current ?? DictationHudModel.Hidden;

    // ─── A hold, start to end ───────────────────────────────────────────

    [Fact]
    public async Task A_dictation_shows_listening_then_transcribing_then_a_receipt_the_clock_takes_down()
    {
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        Assert.Null(rig.Hud.Current);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, Current(rig).Text);

        // A hold long enough for "Listening" to stand its 375 ms, so
        // "Transcribing…" replaces it at the release (DictationHudPacer).
        rig.Clock.Advance(DictationHudPacer.VisibleDwell);
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.Equal(DictationCopy.HudTranscribing, Current(rig).Text);

        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        var receipt = Current(rig);
        Assert.StartsWith("Typed", receipt.Text, StringComparison.Ordinal);
        Assert.Equal(DictationHudModel.ReceiptDwell, receipt.Dwell);
        Assert.All(TranscriptWords, word => Assert.DoesNotContain(word, receipt.Text, StringComparison.OrdinalIgnoreCase));

        rig.Clock.Advance(DictationHudModel.ReceiptDwell - TimeSpan.FromMilliseconds(1));
        rig.Controller.Tick();
        Assert.Same(receipt, rig.Hud.Current);
        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        rig.Controller.Tick();
        Assert.Null(rig.Hud.Current);
    }

    [Fact]
    public async Task Listening_is_shown_but_never_spoken_while_the_microphone_is_open()
    {
        // Narrator speaking "Listening" through the speakers would be picked up
        // by the open microphone and typed into the user's text; the macOS
        // indicator keeps the same silence. Transcribing and the ending are
        // spoken once the microphone is closed.
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, Current(rig).Text);
        Assert.False(rig.Hud.Announced[^1]);

        rig.Clock.Advance(DictationHudPacer.VisibleDwell);
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.Equal(DictationCopy.HudTranscribing, Current(rig).Text);
        Assert.True(rig.Hud.Announced[^1]);

        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.StartsWith("Typed", Current(rig).Text, StringComparison.Ordinal);
        Assert.True(rig.Hud.Announced[^1]);
        Assert.Equal(rig.Hud.Shows.Count, rig.Hud.Announced.Count);
    }

    /// <summary>Each way a hold can end, run through its own controller, and
    /// the pill it left on screen.</summary>
    private static async Task<DictationHudModel> Ending(string how)
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var before = rig.View.States.Count;
        bool Ended() => rig.View.States.Skip(before).Any(s => !s.CanCancel && s.Status != DictationCopy.Inserting
            && !s.Status.StartsWith("Listening", StringComparison.Ordinal) && s.Status != DictationCopy.Transcribing
            && !s.Status.EndsWith("so it stopped there. Transcribing…", StringComparison.Ordinal));

        switch (how)
        {
            case "nothing heard":
                rig.Platform.Trigger.Down = true;
                rig.Controller.Tick();
                rig.Platform.Microphone.Hush();
                rig.Release();
                break;
            case "no sound at all":
                rig.Platform.Trigger.Down = true;
                rig.Controller.Tick();
                rig.Release();
                break;
            case "a refused paste, kept on the clipboard":
                rig.Platform.Paste.Succeeds = false;
                rig.Press();
                rig.Release();
                break;
            case "a password field at the release":
                rig.Press();
                rig.Platform.SecureField.Next = SecureFieldState.Secure;
                rig.Release();
                break;
            case "a password field at the press":
                rig.Platform.SecureField.Next = SecureFieldState.Secure;
                rig.Press();
                rig.Release();
                break;
            case "escape":
                rig.Press();
                rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
                rig.Controller.Tick();
                break;
            case "a shortcut":
                rig.Press();
                rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
                rig.Controller.Tick();
                break;
            case "the recording limit":
                rig.Press();
                rig.Clock.Advance(DictationHoldPolicy.Windows.Ceiling);
                rig.Controller.Tick();
                break;
            case "a blocked microphone":
                rig.Platform.Access.Access = MicrophoneAccess.Denied;
                rig.Platform.Microphone.StartSucceeds = false;
                rig.Press();
                rig.Release();
                break;
            case "a recogniser that stopped":
                rig.Platform.Worker.DieOnTranscribe = true;
                rig.Press();
                rig.Release();
                break;
            default:
                rig.Press();
                rig.Release();
                break;
        }

        rig.DrainUntil(Ended);
        return Current(rig);
    }

    private static readonly string[] EndingCases =
    [
        "inserted",
        "nothing heard",
        "no sound at all",
        "a refused paste, kept on the clipboard",
        "a password field at the release",
        "a password field at the press",
        "escape",
        "a shortcut",
        "the recording limit",
        "a blocked microphone",
        "a recogniser that stopped",
    ];

    [Fact]
    public async Task Every_ending_leaves_its_own_line_with_no_dictated_word_and_a_dwell()
    {
        // The field failure this exists for: silence covered eleven distinct
        // endings. Each now says which one it was, and then goes away.
        var lines = new Dictionary<string, DictationHudModel>
        {
            ["listening"] = new DictationHudModel(DictationCopy.HudHeld, HudTone.Neutral, null),
            ["transcribing"] = new DictationHudModel(DictationCopy.HudTranscribing, HudTone.Neutral, null),
        };
        foreach (var how in EndingCases)
        {
            lines[how] = await Ending(how);
        }

        Assert.All(lines, line => Assert.False(string.IsNullOrWhiteSpace(line.Value.Text), $"{line.Key} showed nothing"));
        Assert.Equal(lines.Count, lines.Values.Select(m => m.Text).Distinct(StringComparer.Ordinal).Count());
        var notices = HoldKeyNoticeSentences();
        foreach (var (how, model) in lines.Where(l => l.Key is not ("listening" or "transcribing")))
        {
            Assert.True(model.Dwell > TimeSpan.Zero, $"{how}: \"{model.Text}\" never goes away");
            Assert.All(TranscriptWords, word => Assert.DoesNotContain(word, model.Text, StringComparison.OrdinalIgnoreCase));
            Assert.All(notices, notice => Assert.DoesNotContain(notice, model.Text, StringComparison.Ordinal));
            Assert.DoesNotContain("notepad", model.Text, StringComparison.OrdinalIgnoreCase);
        }

        // The shared model's own lines where Dictation has nothing to add.
        string Shared(DictationOutcome outcome) => DictationHudModel.For(new DictationSessionResult(outcome, null, null)).Text;
        // The words are in the user's window; the pill does not name its
        // process (the rig's target is notepad).
        Assert.Equal("Typed", lines["inserted"].Text);
        Assert.Equal(DictationCopy.HudNothingHeard("Right Ctrl"), lines["nothing heard"].Text);
        Assert.Equal(DictationCopy.HudNoSound, lines["no sound at all"].Text);
        Assert.Equal(Shared(DictationOutcome.Cancelled), lines["escape"].Text);

        // The audio was recorded and dropped: only the press can say nothing
        // was recorded.
        Assert.Equal(DictationCopy.HudPasswordFieldAtRelease, lines["a password field at the release"].Text);
        Assert.DoesNotContain("recorded", lines["a password field at the release"].Text, StringComparison.Ordinal);
        Assert.Equal(DictationCopy.HudPasswordFieldAtPress, lines["a password field at the press"].Text);
        Assert.Equal(DictationCopy.HudCancelledByShortcut, lines["a shortcut"].Text);
        Assert.Equal(DictationCopy.HudKeptOnClipboard, lines["a refused paste, kept on the clipboard"].Text);
        Assert.Equal(DictationCopy.HudStoppedAtLimit("Right Ctrl", TimeSpan.FromMinutes(5)), lines["the recording limit"].Text);
        Assert.Equal(DictationCopy.HudMicrophoneBlocked, lines["a blocked microphone"].Text);
        // Its take is kept for Recover last dictation (LIMIT-WIN-A): that,
        // not the restart, is the ending.
        Assert.Equal(DictationCopy.HudTakeSaved, lines["a recogniser that stopped"].Text);
    }

    /// <summary>As on the Mac: the countdown said the stop was coming, the
    /// pill transcribes as any other, and the ending says what happened.</summary>
    [Fact]
    public async Task The_limit_transcribes_like_any_release_and_its_ending_says_so()
    {
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Clock.Advance(TimeSpan.FromSeconds(300));
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Five minutes is the longest recording", StringComparison.Ordinal));

        Assert.Equal(DictationCopy.HudTranscribing, Current(rig).Text);
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(DictationCopy.HudStoppedAtLimit("Right Ctrl", TimeSpan.FromMinutes(5)), Current(rig).Text);
        Assert.Equal("Stopped at 5:00 — your words went in, hold Right Ctrl again to keep going", Current(rig).Text);
    }

    // ─── A recogniser that will not come back ───────────────────────────

    [Fact]
    public async Task A_recogniser_that_cannot_be_restarted_ends_on_a_caution_not_on_restarting()
    {
        // The pill said "restarting it" when the dictation failed. Every
        // restart then failed too, and only the hidden window said so.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Worker.DieOnTranscribe = true;
        rig.Platform.Worker.InitSucceeds = false;

        rig.Press();
        rig.Release();
        // Drained without a tick: the restarts report back through the
        // dispatcher, and a tick before the result could find the dead worker
        // first and report the failure as "try again" instead.
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestartFailed);

        // The dictation's ending is its kept take (LIMIT-WIN-A); the give-up
        // follows it.
        var texts = rig.Hud.Shows.Select(m => m.Text).ToList();
        Assert.Contains(DictationCopy.HudTakeSaved, texts);
        Assert.True(
            texts.IndexOf(DictationCopy.HudTakeSaved) < texts.IndexOf(DictationCopy.HudRecognizerCouldNotRestart),
            string.Join(" | ", texts));
        var last = Current(rig);
        Assert.Equal(DictationCopy.HudRecognizerCouldNotRestart, last.Text);
        Assert.Equal(HudTone.Caution, last.Tone);
        Assert.Equal(DictationHudModel.FailureDwell, last.Dwell);
    }

    [Fact]
    public async Task A_recogniser_lost_between_dictations_says_so_before_the_next_hold_fails()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Worker.InitSucceeds = false;

        rig.Platform.Worker.Crash();
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestartFailed);

        Assert.Equal(DictationCopy.HudRecognizerCouldNotRestart, Current(rig).Text);
    }

    [Fact]
    public async Task A_dictation_after_the_restarts_ran_out_shows_the_give_up_once()
    {
        // The press still tries once. When that fails too there is no restart
        // left to promise: one caution that matches the window, not that
        // caution and then a vaguer one drawn over it.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Worker.InitSucceeds = false;
        rig.Platform.Worker.Crash();
        rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestartFailed);
        var shows = rig.Hud.Shows.Count;
        var states = rig.View.States.Count;

        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.States.Skip(states).Any(s => s.Status == DictationCopy.WorkerRestartFailed));

        var after = rig.Hud.Shows.Skip(shows).Select(m => m.Text).ToList();
        Assert.Equal(DictationCopy.HudRecognizerCouldNotRestart, after.Last());
        Assert.Single(after, t => t == DictationCopy.HudRecognizerCouldNotRestart);
        Assert.DoesNotContain(DictationCopy.HudRecognizerNotRunning, after);
        Assert.DoesNotContain(DictationCopy.HudRecognizerFailed, after);
    }

    // ─── A press refused while the last dictation goes in ───────────────

    [Fact]
    public async Task A_refused_press_stays_up_through_the_progress_and_receipt_it_refused_for()
    {
        // The refusal lands between "Transcribing" and "Typed". Replaced by them,
        // it would be gone before anyone could read it.
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        var refusal = Current(rig);
        Assert.Equal(DictationCopy.HudStillTyping, refusal.Text);
        Assert.Equal(HudTone.Caution, refusal.Tone);

        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Same(refusal, rig.Hud.Current);
        Assert.DoesNotContain(rig.Hud.Shows, m => m.Text is DictationCopy.HudTyping || m.Text.StartsWith("Typed", StringComparison.Ordinal));

        // Its own dwell takes it down, and the next hold is shown as usual.
        rig.Clock.Advance(DictationHudModel.NoticeDwell);
        rig.Controller.Tick();
        Assert.Null(rig.Hud.Current);
        rig.Release();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, Current(rig).Text);
    }

    [Fact]
    public void A_refusal_gives_way_to_a_new_hold_and_to_anything_worth_a_caution()
    {
        var clock = new FakeClock();
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold);

        feed.PressRefused(DictationPressRefusal.StillTyping);
        feed.State(DictationState.Listening);
        Assert.Equal(DictationCopy.HudHeld, hud.Current!.Text);

        feed.PressRefused(DictationPressRefusal.StillTyping);
        feed.Result(new DictationSessionResult(DictationOutcome.InsertionRefused, null, null),
            new DictationHudContext(KeptOnClipboard: true));
        Assert.Equal(DictationCopy.HudKeptOnClipboard, hud.Current!.Text);
    }

    // ─── Going away ─────────────────────────────────────────────────────

    [Fact]
    public void A_progress_pill_no_result_replaces_goes_after_a_grace()
    {
        // A pipeline that faults returns to rest without a result;
        // "Transcribing" must not stand on top of every window for ever.
        var clock = new FakeClock();
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold);

        feed.State(DictationState.Listening);
        clock.Advance(DictationHudPacer.VisibleDwell);
        feed.State(DictationState.Transcribing);
        feed.State(DictationState.Armed);
        feed.Tick();
        Assert.Equal(DictationCopy.HudTranscribing, hud.Current!.Text);

        clock.Advance(DictationHudFeed.RestGrace);
        feed.Tick();
        Assert.Null(hud.Current);
    }

    [Fact]
    public void A_receipt_s_hide_does_not_take_down_the_next_hold()
    {
        var clock = new FakeClock();
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold);

        feed.Result(new DictationSessionResult(DictationOutcome.Cancelled, null, null), new DictationHudContext());
        feed.State(DictationState.Listening);
        clock.Advance(TimeSpan.FromMinutes(1));
        feed.Tick();

        Assert.Equal(DictationCopy.HudHeld, hud.Current!.Text);
    }

    /// <summary>A hold torn down. The internal Pause, called directly: the window and
    /// the menu refuse a pause under a dictation (TryPause), so this pins the
    /// teardown of a live hold that quitting and a failure share.</summary>
    [Fact]
    public async Task The_pause_teardown_mid_hold_takes_the_pill_down_at_once()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.NotNull(rig.Hud.Current);

        rig.Controller.Pause();
        rig.Dispatcher.Drain();

        Assert.Null(rig.Hud.Current);
    }

    // ─── What never reaches the pill ────────────────────────────────────

    [Fact]
    public void The_stock_decode_of_a_comparison_run_never_reaches_the_pill()
    {
        // The shared model shows it, quoted, after "without your vocabulary:".
        var result = new DictationSessionResult(DictationOutcome.Inserted, "said aloud", null, BaselineText: "said allowed");

        var model = DictationHudFeed.For(result, new DictationHudContext(), FakeHud.Hold);

        Assert.DoesNotContain("allowed", model.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(DictationHudModel.CompareLabel, model.Text, StringComparison.Ordinal);
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

    /// <summary>Every outcome, with every piece of context the controller
    /// can add, aimed at a window whose process and title are both known: the
    /// pill names neither. The shared model would say "Typed into WINWORD".</summary>
    [Theory]
    [MemberData(nameof(Outcomes))]
    public void No_ending_names_the_other_app_or_shows_a_word(DictationOutcome outcome)
    {
        var target = FocusedApplicationResult.Resolved("WINWORD", 42, "Budget notes.docx - Word");
        var notices = HoldKeyNoticeSentences();
        foreach (var context in new[]
        {
            new DictationHudContext(),
            new DictationHudContext(
                KeptOnClipboard: true, CancelledByShortcut: true, Stopped: new(DictationHoldStop.Ceiling, TimeSpan.FromMinutes(5))),
            new DictationHudContext(Stopped: new(DictationHoldStop.Silence, TimeSpan.FromSeconds(30))),
            new DictationHudContext(Stopped: new(DictationHoldStop.StuckKey, TimeSpan.FromMinutes(1))),
            new DictationHudContext(Stopped: new(DictationHoldStop.NothingHeard, TimeSpan.FromSeconds(15))),
            new DictationHudContext(RecognizerRestarting: true),
            new DictationHudContext(WordsKeptInApp: true),
        })
        {
            var model = DictationHudFeed.For(
                new DictationSessionResult(outcome, "hello world", target, UndeliveredText: "hello world"), context, FakeHud.Hold);

            Assert.False(string.IsNullOrWhiteSpace(model.Text));
            Assert.All(TranscriptWords, word => Assert.DoesNotContain(word, model.Text, StringComparison.OrdinalIgnoreCase));
            Assert.All(notices, notice => Assert.DoesNotContain(notice, model.Text, StringComparison.Ordinal));
            Assert.DoesNotContain("WINWORD", model.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Budget", model.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" into ", model.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_receipts_that_would_name_the_target_say_what_happened_without_it()
    {
        var target = FocusedApplicationResult.Resolved("msedge", 7, "Inbox - Outlook - Microsoft Edge");
        DictationHudModel Pill(DictationOutcome outcome) =>
            DictationHudFeed.For(new DictationSessionResult(outcome, "hello world", target), new DictationHudContext(), FakeHud.Hold);

        Assert.Equal("Typed", Pill(DictationOutcome.Inserted).Text);
        Assert.Equal(DictationHudModel.ReceiptDwell, Pill(DictationOutcome.Inserted).Dwell);
        Assert.Equal("Typed — your clipboard did not come back", Pill(DictationOutcome.ClipboardRestoreFailed).Text);
        Assert.Equal(DictationCopy.HudCouldNotTypeOrCopy, Pill(DictationOutcome.InsertionRefused).Text);
    }

    /// <summary>Every line this product wrote for the pill.</summary>
    private static IEnumerable<string> HudCopy() =>
        typeof(DictationCopy).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.Name.StartsWith("Hud", StringComparison.Ordinal))
            .Select(f => (string)f.GetValue(null)!);

    /// <summary>Every context the controller can give an ending: the
    /// indicator render's list (<see cref="DictationHudRenderFrames.Contexts"/>),
    /// so every line held to the rules here is a line the render draws. The
    /// longest name a sentence carries is in it (DictationCopy.MicrophoneNameLimit).</summary>
    internal static readonly DictationHudContext[] Contexts = [.. DictationHudRenderFrames.Contexts.Select(c => c.Context)];

    /// <summary>Every ending and notice the pill can show, for every hold key
    /// the window offers.</summary>
    internal static IEnumerable<DictationHudModel> EveryEnding()
    {
        foreach (var key in DictationHotkeys.HoldChoices)
        {
            var hold = FakeHud.Hold with { KeyName = key.KeyName };
            foreach (var outcome in Enum.GetValues<DictationOutcome>())
            {
                foreach (var context in Contexts)
                {
                    yield return DictationHudFeed.For(new DictationSessionResult(outcome, null, null), context, hold);
                }
            }
        }

        foreach (var notice in EveryNotice())
        {
            yield return notice;
        }
    }

    /// <summary>Every pill that ends no dictation: the press refusals, Copy
    /// last text's lines, the recogniser's give-up and the learned
    /// correction, from the indicator render's list
    /// (<see cref="DictationHudRenderFrames.Notices"/>).</summary>
    internal static IEnumerable<DictationHudModel> EveryNotice()
    {
        foreach (var (_, show) in DictationHudRenderFrames.Notices)
        {
            var hud = new FakeHud();
            using var feed = new DictationHudFeed(hud, () => TimeSpan.Zero, () => FakeHud.Hold);
            show(feed);
            yield return hud.Current!;
        }
    }

    [Fact]
    public void Each_line_this_product_wrote_stays_up_long_enough_to_read()
    {
        // The shared model's rule, held against every ending the pill can
        // show, for every hold key: the time to notice a pill that arrived
        // unannounced, plus the words at a glancing 240 a minute. A receipt
        // ("Typed", "Cancelled", the shortcut's) carries no reason to read; a
        // busy pill has no dwell, and lasts as long as its state.
        var read = EveryEnding().Where(m => m.Dwell != DictationHudModel.ReceiptDwell).ToArray();
        Assert.True(read.Length > 100, $"only {read.Length} endings");
        foreach (var model in read)
        {
            var words = model.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var needed = TimeSpan.FromMilliseconds(400) + TimeSpan.FromSeconds(words / 4.0);
            Assert.True(model.Dwell >= needed, $"\"{model.Text}\" is up {model.Dwell} and needs {needed}");
        }

        // Kept in step with the copy: every line this product wrote for the
        // pill is a busy line, the shortcut's receipt, or one checked above.
        var busy = DictationCopy.HudBusyLines("Right Ctrl", FakeHud.Hold.Policy);
        foreach (var line in HudCopy())
        {
            Assert.True(
                busy.Contains(line) || line == DictationCopy.HudCancelledByShortcut || read.Any(m => m.Text == line),
                $"\"{line}\" is in no reading check");
        }
    }

    [Fact]
    public void The_lines_this_product_wrote_are_distinct_and_never_carry_the_hold_key_notice()
    {
        var lines = HudCopy().ToArray();

        Assert.True(lines.Length >= 10, $"only {lines.Length} lines");
        Assert.Equal(lines.Length, lines.Distinct(StringComparer.Ordinal).Count());

        var notices = HoldKeyNoticeSentences();
        foreach (var text in lines.Concat(EveryEnding().Select(m => m.Text)))
        {
            Assert.All(notices, notice => Assert.DoesNotContain(notice, text, StringComparison.Ordinal));
        }
    }

    /// <summary>Settings advice belongs under the key picker, never in the
    /// transient indicator that accompanies each dictation.</summary>
    private static string[] HoldKeyNoticeSentences()
    {
        var sentences = (
            from key in DictationHotkeys.HoldChoices
            from fromEnvironment in new[] { false, true }
            let advice = HoldKeyAdvice.For(key, fromEnvironment)
            where advice is not null
            select advice.Split([": ", ". "], 2, StringSplitOptions.None)[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Keep this privacy check non-vacuous across configuration and key cautions.
        Assert.True(sentences.Length >= 3, $"only {sentences.Length} notice sentences: {string.Join(" | ", sentences)}");
        return sentences;
    }

    // ─── An indicator that fails, or none ───────────────────────────────

    [Fact]
    public async Task A_window_that_cannot_draw_never_stops_a_dictation()
    {
        using var rig = new Rig();
        rig.Hud.Throws = true;
        await rig.EnableAsync();

        rig.Dictate(Inserted);

        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    [Fact]
    public async Task Without_an_indicator_dictation_runs_as_before()
    {
        using var rig = new Rig(hud: false);
        await rig.EnableAsync();

        rig.Dictate(Inserted);

        Assert.Empty(rig.Hud.Shows);
    }
}
