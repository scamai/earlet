using System.Collections.Concurrent;
using System.Diagnostics;
using Dictation.Services;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationAttemptLogTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The real controller over the fakes keeps one receipt per attempted hold:
/// the Mac's <c>AttemptDiagnosticsTests.modelWiring</c> scenarios ported, plus
/// the paths only Windows has (a press refused while the last dictation
/// finishes or while Copy last text writes, a key up before the press-time
/// password check answers), and the recovery and copy lines a hold's words
/// leave under its id.
/// </summary>
public sealed class DictationAttemptWiringTests
{
    internal const string Words = "my bank pin is four four two one";

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    /// <summary>A rig whose transcript and target are distinctive enough to
    /// be caught if either reaches a receipt.</summary>
    private static (Rig Rig, MemoryLog Log) Rig(Func<DictationControllerOptions, DictationControllerOptions>? options = null)
    {
        var log = new MemoryLog();
        var rig = new Rig(logs: log, options: options);
        rig.Platform.Worker.Transcript = Words;
        rig.Platform.Target.Next = FocusedApplicationResult.Resolved("secretwriter", 7, "Secret Plans - Editor");
        return (rig, log);
    }

    /// <summary>Wait until the log holds <paramref name="count"/> endings: a
    /// receipt ends on the session's thread, not the dispatcher's.</summary>
    internal static void AwaitEndings(Rig rig, MemoryLog log, int count = 1)
    {
        var deadline = Stopwatch.StartNew();
        while (Endings(log).Count < count)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), $"{Endings(log).Count} of {count} endings");
            rig.Dispatcher.Drain();
            Thread.Sleep(5);
        }
    }

    internal static List<string> Endings(MemoryLog log) => log.Receipts.Where(l => Event(l) == "finished").ToList();

    /// <summary>Each attempt: started first, at most one release before its
    /// one ending, and after it nothing but at most one recovery line and the
    /// copies of its words; and no receipt names the words, the target's
    /// process or its window title.</summary>
    internal static void AssertWellFormed(MemoryLog log)
    {
        foreach (var attempt in log.Receipts.GroupBy(Attempt))
        {
            var events = attempt.Select(Event).ToList();
            Assert.Equal("started", events[0]);
            Assert.Single(events, e => e == "started");
            Assert.Single(events, e => e == "finished");
            Assert.True(events.Count(e => e == "released") <= 1, string.Join(",", events));
            Assert.True(events.Count(e => e == "recovery") <= 1, string.Join(",", events));
            var after = attempt.Skip(events.IndexOf("finished") + 1).ToList();
            Assert.All(after, l => Assert.Matches(AllowedAfterEnding, l));
        }

        foreach (var ending in Endings(log))
        {
            Assert.Matches(AllowedEnding, ending[(ending.IndexOf("result=", StringComparison.Ordinal))..ending.IndexOf(" asr_ms=", StringComparison.Ordinal)]);
        }

        var receipts = string.Join("\n", log.Receipts);
        Assert.DoesNotContain("secretwriter", receipts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret Plans", receipts, StringComparison.OrdinalIgnoreCase);
        // The words reach no line at all, receipt or not.
        Assert.DoesNotContain("bank pin", string.Join("\n", log.Lines), StringComparison.OrdinalIgnoreCase);
    }

    // ─── The Mac's scenarios (modelWiring) ──────────────────────────────

    [Theory]
    [InlineData("inserted", "result=inserted clipboard_unchanged=true restore_detached=false")]
    [InlineData("paste_failed", "result=failed stage=paste reason=paste_refused")]
    [InlineData("no_text", "result=failed stage=asr reason=no_text")]
    [InlineData("privacy_refused", "result=privacy_refused")]
    [InlineData("microphone_permission", "result=failed stage=capture reason=microphone_permission")]
    [InlineData("cancelled", "result=cancelled")]
    public async Task The_real_controller_finishes_each_attempt_once(string scenario, string ending)
    {
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        switch (scenario)
        {
            case "paste_failed":
                rig.Platform.Paste.Succeeds = false;
                break;
            case "no_text":
                rig.Platform.Worker.Transcript = string.Empty;
                break;
            case "privacy_refused":
                rig.Platform.SecureField.Next = SecureFieldState.Secure;
                break;
            case "microphone_permission":
                rig.Platform.Access.Access = MicrophoneAccess.Denied;
                rig.Platform.Microphone.StartSucceeds = false;
                break;
        }

        rig.Press();
        rig.Dispatcher.Drain();
        if (scenario == "cancelled")
        {
            rig.Controller.Cancel();
        }

        rig.Release();
        AwaitEndings(rig, log);
        Thread.Sleep(50);
        rig.Dispatcher.Drain();

        var endings = Endings(log);
        Assert.Single(endings);
        Assert.Contains($"event=finished {ending} asr_ms=", endings[0]);
        AssertWellFormed(log);
        var released = log.Receipts.Any(l => Event(l) == "released");
        // A release is logged where the hold reached one: not for a press
        // refused at once, nor a hold cancelled while the key was down.
        Assert.Equal(scenario is not ("privacy_refused" or "cancelled"), released);
        // modelWiring's paste_failed: the words went to the clipboard and are
        // kept in Dictation. Only a refused paste has a recovery line.
        var recovery = log.Receipts.Where(l => Event(l) == "recovery").ToList();
        if (scenario == "paste_failed")
        {
            Assert.Equal(
                $"dictation attempt={Attempt(endings[0])} event=recovery clipboard=true available_in_app=true",
                Assert.Single(recovery));
        }
        else
        {
            Assert.Empty(recovery);
        }
        if (scenario == "privacy_refused")
        {
            Assert.Equal(0, rig.Platform.Microphone.Starts);
        }
    }

    // ─── Windows' own paths ─────────────────────────────────────────────

    [Fact]
    public async Task A_shortcut_on_the_hold_key_is_its_own_ending()
    {
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Controller.Tick();
        rig.Release();
        AwaitEndings(rig, log);

        Assert.Contains("event=finished result=shortcut", Endings(log).Single());
        AssertWellFormed(log);
    }

    [Fact]
    public async Task A_press_refused_while_the_last_dictation_finishes_is_its_own_attempt_and_the_first_still_ends_inserted()
    {
        var (rig, log) = Rig();
        using var _ = rig;
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        rig.Release();
        AwaitEndings(rig, log, 2);

        var endings = Endings(log);
        Assert.Equal(2, endings.Count);
        Assert.Contains("result=still_finishing", endings[0]);
        Assert.Contains("result=inserted", endings[1]);
        Assert.Equal(2, log.Receipts.Select(Attempt).Distinct().Count());
        AssertWellFormed(log);
    }

    [Fact]
    public async Task A_hold_whose_microphone_delivered_nothing_logs_its_release_before_its_ending()
    {
        // The tap ends a hold that captured nothing inside its own release
        // handler, so the ending is raised before the release event reaches
        // anyone after it: the receipt's release must be taken first. (This
        // was a too-short hold until the hands-free latch: the app's tap has
        // no minimum hold now, DictationHoldPolicy.TapOptions, so it never
        // reports one.)
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        rig.Platform.Trigger.Down = true;   // pressed, and nothing said
        rig.Controller.Tick();
        rig.Release();
        AwaitEndings(rig, log);

        Assert.Equal(["started", "released", "finished"], log.Receipts.Select(Event));
        Assert.Contains("result=failed stage=capture reason=empty_capture", Endings(log).Single());
        Assert.DoesNotContain("release_ms=n/a", Endings(log).Single(), StringComparison.Ordinal);
        AssertWellFormed(log);
    }

    [Fact]
    public async Task A_key_up_before_the_password_check_answers_ends_without_recording()
    {
        var (rig, log) = Rig(o => o with { RunFieldCheck = check => Task.Run(check) });
        using var _ = rig;
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        gate.Set();
        AwaitEndings(rig, log);

        Assert.Equal(0, rig.Platform.Microphone.Starts);
        Assert.Equal(["started", "released", "finished"], log.Receipts.Select(Event));
        Assert.Contains("result=failed stage=capture reason=released_before_capture", Endings(log).Single());
        AssertWellFormed(log);
    }

    [Fact]
    public async Task A_key_up_seen_first_by_the_password_check_s_answer_is_still_the_press_s_release()
    {
        // The check's answer is posted as soon as it lands, and can find the
        // key up before the next poll does.
        var (rig, log) = Rig(o => o with { RunFieldCheck = check => Task.Run(check) });
        using var _ = rig;
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Trigger.Down = false;
        gate.Set();
        // Drained, never ticked: only the posted answer can end the press.
        AwaitEndings(rig, log);

        Assert.Equal(0, rig.Platform.Microphone.Starts);
        Assert.Equal(["started", "released", "finished"], log.Receipts.Select(Event));
        Assert.Contains("result=failed stage=capture reason=released_before_capture", Endings(log).Single());
        Assert.DoesNotContain("release_ms=n/a", Endings(log).Single(), StringComparison.Ordinal);
        AssertWellFormed(log);
    }

    [Fact]
    public async Task A_shortcut_while_the_password_check_is_out_ends_the_press_as_a_shortcut()
    {
        var (rig, log) = Rig(o => o with { RunFieldCheck = check => Task.Run(check) });
        using var _ = rig;
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Chord;
        rig.Controller.Tick();
        gate.Set();
        rig.TickUntil(() => rig.Platform.SecureField.Calls == 1 && rig.Dispatcher.Pending == 0);
        Thread.Sleep(50);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Contains("event=finished result=shortcut", Endings(log).Single());
        AssertWellFormed(log);
    }

    /// <summary>The internal Pause, called directly here and below: the
    /// window and the menu refuse a pause under a dictation (TryPause), so
    /// these pin the ending the teardown of a live hold writes, which
    /// quitting and a failure share.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_pause_teardown_mid_hold_or_mid_decode_ends_the_attempt_as_cancelled(bool released)
    {
        var (rig, log) = Rig();
        using var _ = rig;
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        if (released)
        {
            rig.Release();
            rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        }

        rig.Controller.Pause();
        hold.Set();
        Thread.Sleep(100);
        rig.Dispatcher.Drain();

        var ending = Endings(log).Single();
        Assert.Contains("result=cancelled", ending);
        Assert.Equal(released, !ending.EndsWith("release_ms=n/a", StringComparison.Ordinal));
        AssertWellFormed(log);
    }

    [Fact]
    public async Task The_pause_teardown_while_the_password_check_is_out_ends_the_press_as_cancelled()
    {
        // The session never had this press, so it raises nothing for it.
        var (rig, log) = Rig(o => o with { RunFieldCheck = check => Task.Run(check) });
        using var _ = rig;
        using var gate = new ManualResetEventSlim();
        rig.Platform.SecureField.Hold = gate;
        await rig.EnableAsync();
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();

        rig.Controller.Pause();
        gate.Set();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();

        Assert.Contains("event=finished result=cancelled", Endings(log).Single());
        Assert.Equal(0, rig.Platform.Microphone.Starts);
        AssertWellFormed(log);
    }

    [Fact]
    public async Task The_recording_limit_is_the_hold_s_release()
    {
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Clock.Advance(TimeSpan.FromSeconds(300));
        rig.Controller.Tick();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        AwaitEndings(rig, log);

        Assert.Equal(["started", "released", "finished"], log.Receipts.Select(Event));
        Assert.Contains("result=inserted", Endings(log).Single());
        Assert.Contains("elapsed_ms=300000 release_ms=0", Endings(log).Single());
        AssertWellFormed(log);
        rig.Release();
    }

    [Fact]
    public async Task A_result_that_arrives_as_the_app_quits_keeps_its_own_ending()
    {
        // The paste lands, and the quit that waited for it tears the
        // controller down before the posted result runs; that post is then
        // dropped as stale. Its receipt was already written where the session
        // raised it, so it does not read "cancelled".
        var (rig, log) = Rig();
        using var _ = rig;
        using var paste = new ManualResetEventSlim();
        rig.Platform.Paste.Hold = paste;
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        paste.Set();
        // Until the session has raised the result, not until a receipt is
        // there: a receipt taken only when the posted result runs must show
        // up below as the wrong ending, not as a timeout here.
        log.AwaitRaised();
        Assert.True(rig.Dispatcher.Pending > 0, "the result's post already ran");

        rig.Controller.Dispose();
        rig.Dispatcher.Drain();

        Assert.Contains("result=inserted", Endings(log).Single());
        AssertWellFormed(log);
    }

    [Fact]
    public async Task Every_hold_of_a_session_gets_its_own_attempt()
    {
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        var before = rig.View.States.Count;
        rig.Dictate(_ => rig.View.States.Skip(before).Any(Inserted));
        AwaitEndings(rig, log, 2);

        Assert.Equal(2, log.Receipts.Select(Attempt).Distinct().Count());
        Assert.All(Endings(log), e => Assert.Contains("result=inserted", e));
        AssertWellFormed(log);
    }

    [Fact]
    public async Task A_press_that_arrives_before_the_last_result_s_post_starts_its_own_attempt()
    {
        // As in the app, on one UI thread: the session resumes its pipeline
        // where it began (every await in it is ConfigureAwait(true)), so it
        // goes back to rest and raises the result in one go, and the next
        // press can arrive only after both — but before the controller's
        // posts of that result have run. The receipt is already written by
        // then, so the press cannot take it.
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        var previous = SynchronizationContext.Current;
        var ui = new UiThread();
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            rig.Press();
            rig.Dispatcher.Drain();
            rig.Release();
            ui.PumpUntil(() => log.Raised == 1, "the first hold was never answered");
            Assert.True(rig.Dispatcher.Pending > 0, "the result's posts already ran");
            Assert.Contains("result=inserted", Assert.Single(Endings(log)));

            rig.Press();
            rig.Dispatcher.Drain();
            rig.Release();
            ui.PumpUntil(() => log.Raised == 2, "the second hold was never answered");
            rig.Dispatcher.Drain();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var endings = Endings(log);
        Assert.Equal(2, endings.Count);
        Assert.All(endings, e => Assert.Contains("result=inserted", e));
        Assert.All(endings, e => Assert.DoesNotContain("asr_ms=n/a", e, StringComparison.Ordinal));
        Assert.Equal(2, endings.Select(Attempt).Distinct().Count());
        AssertWellFormed(log);
    }

    /// <summary>
    /// The endings whose posted handler returns early or takes its own branch
    /// (the worker restart, the clipboard fallback, the inserted line): the
    /// receipt is taken before the post, so each still ends once, as itself.
    /// A worker that dies mid-decode is a failed recognition, as on the Mac,
    /// whose <c>engine_unavailable</c> is an engine that never started for
    /// the hold (Windows' <c>RecognizerUnavailable</c>).
    /// </summary>
    [Theory]
    [InlineData("worker_died", "result=failed stage=asr reason=recognition_failed")]
    [InlineData("no_window", "result=failed stage=paste reason=no_field")]
    [InlineData("clipboard_not_restored", "result=inserted clipboard_unchanged=false restore_detached=false")]
    public async Task Endings_the_window_handles_on_its_own_path_are_still_one_receipt(string scenario, string ending)
    {
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        Func<bool> settled;
        switch (scenario)
        {
            case "worker_died":
                rig.Platform.Worker.DieOnTranscribe = true;
                // Its retry dies too, and the take is kept (LIMIT-WIN-A).
                settled = () => rig.View.States.Any(s => s.Status == DictationCopy.TakeSavedWorkerRestarting);
                break;
            case "no_window":
                rig.Platform.Target.Next = FocusedApplicationResult.NoFocusedWindow;
                settled = () => rig.View.Last.Status == DictationCopy.KeptOnClipboardNoWindow;
                break;
            default:
                var landed = rig.Platform.Paste.Landed;
                rig.Platform.Paste.Landed = () =>
                {
                    landed?.Invoke();
                    rig.Platform.Clipboard.Accepts = false;
                };
                settled = () => rig.View.Last.Status == DictationCopy.ForResult(
                    new DictationSessionResult(DictationOutcome.ClipboardRestoreFailed, null, null), rig.Controller.HoldKey).Text;
                break;
        }

        rig.Press();
        rig.Release();
        rig.DrainUntil(settled);
        AwaitEndings(rig, log);
        if (scenario == "worker_died")
        {
            rig.Platform.Worker.DieOnTranscribe = false;
            rig.TickUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));
        }

        rig.Dispatcher.Drain();
        var receipt = Assert.Single(Endings(log));
        Assert.True(receipt.Contains($"event=finished {ending} asr_ms=", StringComparison.Ordinal), receipt);
        // No window had focus: the words went to the clipboard and are kept.
        Assert.Equal(
            scenario == "no_window"
                ? [$"dictation attempt={Attempt(receipt)} event=recovery clipboard=true available_in_app=true"]
                : Array.Empty<string>(),
            log.Receipts.Where(l => Event(l) == "recovery"));
        AssertWellFormed(log);
    }

    // ─── Recent result and Copy last text ───────────────────────────────

    /// <summary>
    /// The Mac's recovery line, and its Copy last text line, on the receipt of
    /// the hold whose words they are about: a paste refused while the
    /// clipboard refuses too, a copy that fails, one that works, and the next
    /// dictation's words copied under that dictation's own id.
    /// </summary>
    [Fact]
    public async Task A_refused_paste_logs_its_recovery_and_each_copy_of_its_words_under_its_own_attempt()
    {
        var (rig, log) = Rig();
        using var _ = rig;
        await rig.EnableAsync();
        rig.Platform.Paste.Succeeds = false;
        rig.Platform.Clipboard.Accepts = false;
        rig.Dictate(v => v.Status == DictationCopy.WordsStillHere);
        AwaitEndings(rig, log);
        var refused = Attempt(Endings(log).Single());

        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        rig.Platform.Clipboard.Accepts = true;
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();

        rig.Platform.Paste.Succeeds = true;
        var before = rig.View.States.Count;
        rig.Dictate(_ => rig.View.States.Skip(before).Any(Inserted));
        AwaitEndings(rig, log, 2);
        var inserted = Attempt(Endings(log)[1]);
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();

        Assert.Equal(
        [
            "started", "released", "finished", "recovery", "copy", "copy",
        ], log.Receipts.Where(l => Attempt(l) == refused).Select(Event));
        Assert.Equal(
        [
            $"dictation attempt={refused} event=recovery clipboard=false available_in_app=true",
            $"dictation attempt={refused} event=copy result=failed available_in_app=true",
            $"dictation attempt={refused} event=copy result=copied",
        ], log.Receipts.Where(l => Attempt(l) == refused).Skip(3));
        Assert.Equal(
            $"dictation attempt={inserted} event=copy result=copied",
            log.Receipts.Where(l => Attempt(l) == inserted).Last());
        Assert.DoesNotContain(log.Receipts, l => Attempt(l) == inserted && Event(l) == "recovery");
        AssertWellFormed(log);
    }

    /// <summary>A press while Copy last text writes is an attempt like any
    /// refused press, with an ending of its own, and the copy still lands
    /// under the dictation that left the words.</summary>
    [Fact]
    public async Task A_press_refused_during_a_copy_is_its_own_attempt_ending_still_copying()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (rig, log) = Rig(o => o with { RunClipboardCopy = _ => answer.Task });
        using var _ = rig;
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        AwaitEndings(rig, log);
        var dictated = Attempt(Endings(log).Single());
        var starts = rig.Platform.Microphone.Starts;

        rig.Controller.CopyLastText();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Release();
        Assert.Equal(DictationCopy.StillCopyingLastText(rig.Controller.HoldKey), rig.View.Last.Status);

        var endings = Endings(log);
        Assert.Equal(2, endings.Count);
        Assert.Contains("event=finished result=still_copying asr_ms=n/a paste_ms=n/a", endings[1]);
        var press = Attempt(endings[1]);
        Assert.NotEqual(dictated, press);
        Assert.Equal(["started", "finished"], log.Receipts.Where(l => Attempt(l) == press).Select(Event));
        Assert.Equal(starts, rig.Platform.Microphone.Starts);

        answer.SetResult(true);
        rig.DrainUntil(() => rig.View.Last.Recent is { Copying: false });
        Assert.Equal($"dictation attempt={dictated} event=copy result=copied", log.Receipts[^1]);
        AssertWellFormed(log);
    }

    /// <summary>The WPF dispatcher as the session's awaits see it: what they
    /// resume is queued and runs on the test thread when pumped.</summary>
    private sealed class UiThread : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("Nothing here waits on the UI thread.");

        public override SynchronizationContext CreateCopy() => this;

        public void PumpUntil(Func<bool> done, string because)
        {
            var deadline = Stopwatch.StartNew();
            while (!done())
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), because);
                if (_queue.TryDequeue(out var next))
                {
                    next.Callback(next.State);
                }
                else
                {
                    Thread.Sleep(2);
                }
            }
        }
    }

    // ─── The launch line and the build stamp (source pins) ──────────────
    //
    // WPF cannot run here: these pin the source that writes the launch line
    // and the project item that carries the packager's stamp.

    [Fact]
    public void The_app_logs_its_installation_before_it_claims_the_single_instance()
    {
        var app = File.ReadAllText(Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", "App.xaml.cs"));
        var launch = app.IndexOf(
            "DictationInstallation.Of(typeof(App).Assembly, Environment.ProcessPath).LaunchLine",
            StringComparison.Ordinal);
        Assert.True(launch > 0, "no launch line");
        Assert.True(launch < app.IndexOf("instance.Claim()", StringComparison.Ordinal), "logged after the claim");
        Assert.True(launch < app.IndexOf("new MainWindow(", StringComparison.Ordinal), "logged after the window");
        Assert.DoesNotContain("starting\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_project_carries_the_packager_s_build_stamp_only_when_given_one()
    {
        var project = System.Xml.Linq.XDocument.Load(
            Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", "Dictation.App.csproj"));
        var item = project.Descendants("AssemblyMetadata").Single();

        Assert.Equal(DictationInstallation.BuildMetadataKey, (string?)item.Attribute("Include"));
        Assert.Equal("$(DictationBuild)", (string?)item.Attribute("Value"));
        Assert.Equal("'$(DictationBuild)' != ''", (string?)item.Parent!.Attribute("Condition"));
    }
}
