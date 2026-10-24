using System.Text.RegularExpressions;
using Dictation.Core.Learning;
using Dictation.Core.Tests.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The live preview through the real controller, session, worker slot, host
/// and framing, over the fakes: the Mac's <c>LivePreviewTests</c> (the model's
/// wiring) ported scenario for scenario, plus what only Windows has — the
/// switch that is off by default, the press-time password refusal, a worker
/// that is not ready, and the logs.
/// </summary>
/// <remarks>
/// "D15 discard stops the preview and the rebind starts a fresh run" is in
/// <c>MicrophoneChangeTests</c>, adapted: Windows has no rebind, so a
/// microphone change ends the hold and its run, and the next press's run is
/// the fresh one.
/// </remarks>
public sealed class LivePreviewTests
{
    /// <summary>What the fake worker's stream answers, windowed: the last
    /// line, its speaker tag removed.</summary>
    internal const string Caption = "hello world from preview";

    /// <summary>A final transcript no preview line contains.</summary>
    internal const string Words = "my bank pin is four four two one";

    /// <summary>The environment with the preview switched on.</summary>
    internal static string? On(string name) => name == DictationEnvironment.Preview ? "1" : null;

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static bool Cancelled(DictationViewState v) => v.Status == "Cancelled. Nothing was inserted.";

    private static int Count(Rig rig, string op) => rig.Platform.Worker.Ops().Count(o => o == op);

    internal static Rig PreviewRig(
        Func<DictationControllerOptions, DictationControllerOptions>? options = null,
        Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
    {
        var rig = new Rig(environment: On, options: options, logs: logs);
        rig.Platform.Worker.Transcript = Words;
        return rig;
    }

    /// <summary>The Mac's <c>pressAndPreview</c>: press, the stream opens once
    /// capture is up, speech arrives, and the caption reaches the indicator.</summary>
    internal static void PressAndPreview(Rig rig, int streams = 1)
    {
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.DrainUntil(() => Count(rig, "stream_start") == streams);
        rig.Platform.Microphone.Speak();
        rig.DrainUntil(() => rig.Hud.Current?.Caption == Caption);
    }

    // ─── The Mac's scenarios (LivePreviewTests.swift) ───────────────────

    [Fact]
    public async Task A_hold_streams_a_windowed_caption_to_the_HUD_cleared_at_the_end()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();
        PressAndPreview(rig);
        Assert.True(Count(rig, "stream_push") >= 1);

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Null(rig.Hud.Current?.Caption);
        // The preview is never inserted; the authoritative decode is.
        var pasted = rig.Platform.Clipboard.Writes[0];
        Assert.Contains("bank pin", pasted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preview", pasted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_preview_stream_is_cancelled_before_the_authoritative_decode()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();
        PressAndPreview(rig);

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        // The worker pipe is one request, one reply: the cancel lands before
        // the final transcribe on the same worker, and nothing finishes the
        // stream, which would hand back the streaming engine's own text.
        var ops = rig.Platform.Worker.Ops().Where(o => o is "stream_cancel" or "transcribe" or "stream_finish").ToArray();
        Assert.Equal(["stream_cancel", "transcribe"], ops);
    }

    [Fact]
    public async Task Escape_stops_the_preview_and_clears_the_caption()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();
        PressAndPreview(rig);

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        // At once, not when the cancel's repaint reaches the UI thread.
        Assert.True(SpinWait.SpinUntil(() => Count(rig, "stream_cancel") == 1, 5_000));
        rig.DrainUntil(() => Cancelled(rig.View.Last));

        Assert.Null(rig.Hud.Current?.Caption);
        Assert.Equal(1, Count(rig, "stream_cancel"));
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Equal(0, Count(rig, "transcribe"));
        rig.Release();
    }

    [Fact]
    public async Task A_caption_that_arrives_after_the_release_is_never_drawn()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();

        // The hold's caption is posted while it listens, and the key comes
        // up before the UI thread gets to it.
        var posted = rig.Dispatcher.Posted;
        rig.Press();
        Assert.True(SpinWait.SpinUntil(() => Count(rig, "stream_push") == 1 && rig.Dispatcher.Posted >= posted + 2, 5_000));
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.All(rig.Hud.Shows, pill => Assert.Null(pill.Caption));
    }

    [Fact]
    public async Task Escape_while_the_engine_starts_ends_the_preview_run_so_the_next_hold_gets_one()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();
        // Unloaded after idle, and its reload is slow: the hold's preview run
        // opens no stream while the worker loads (and never starts one).
        rig.Clock.Advance(TimeSpan.FromSeconds(180));
        rig.Controller.Tick();
        Assert.False(rig.Platform.Worker.IsAlive);
        using var init = new ManualResetEventSlim();
        rig.Platform.Worker.HoldInit = init;

        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        rig.DrainUntil(() => Cancelled(rig.View.Last));
        Assert.Equal(0, Count(rig, "stream_start"));
        Assert.Equal(2, rig.Platform.CreateWorkerCalls);

        // The load finishes for the next hold, which opens its stream.
        init.Set();
        rig.TickUntil(() => rig.Platform.StartupFinishedCalls == 2);
        // Installed a moment after the load reports in, on the start's thread.
        Thread.Sleep(100);
        PressAndPreview(rig);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(2, rig.Platform.CreateWorkerCalls);
    }

    [Fact]
    public async Task An_engine_that_could_not_start_ends_the_preview_run_too()
    {
        var log = new MemoryLog();
        using var rig = PreviewRig(logs: log);
        await rig.EnableAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(180));
        rig.Controller.Tick();
        rig.Platform.Worker.InitSucceeds = false;

        rig.Press();
        rig.Release();
        // The hold's own load fails; let the restart that follows succeed.
        log.AwaitRaised();
        rig.Platform.Worker.InitSucceeds = true;
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestarted(rig.Controller.HoldKey));
        Assert.Equal(0, Count(rig, "stream_start"));

        PressAndPreview(rig);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Fact]
    public async Task A_preview_cancel_stuck_in_the_worker_holds_up_neither_a_release_nor_Escape()
    {
        using var rig = PreviewRig(options: o => o with { PreviewStopLimit = TimeSpan.FromMilliseconds(200) });
        using var stuck = new ManualResetEventSlim();
        try
        {
            await rig.EnableAsync();
            PressAndPreview(rig);
            rig.Platform.Worker.HoldStreamCancel = stuck;

            // The release waits 200 ms for the preview, then sends its decode.
            // The worker answers one frame at a time, so the decode waits for
            // the host's own 2 s limit on the stuck cancel, which ends that
            // worker; the decode starts a fresh one and the words still land.
            rig.Release();
            rig.DrainUntil(() => Inserted(rig.View.Last));
            Assert.Contains("bank pin", rig.Platform.Clipboard.Writes[0], StringComparison.OrdinalIgnoreCase);
            Assert.Null(rig.Hud.Current?.Caption);

            // The run was detached anyway: the next hold opens its own stream,
            // and Escape is not held up by its stuck cancel.
            PressAndPreview(rig, streams: 2);
            rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
            rig.Controller.Tick();
            var escaped = System.Diagnostics.Stopwatch.StartNew();
            rig.DrainUntil(() => Cancelled(rig.View.Last));
            Assert.True(escaped.Elapsed < TimeSpan.FromSeconds(1), $"Escape took {escaped.Elapsed}");
            rig.Release();
        }
        finally
        {
            stuck.Set();
        }
    }

    /// <summary>
    /// The bound itself. A stream still opening at the release is waited for
    /// no longer than the limit: the decode is queued for the worker then,
    /// and the late stream's cancel goes after it, instead of the words
    /// waiting for a caption nobody will see. (Anything already on the wire
    /// holds the decode until it answers or the host's own limit ends it,
    /// bound or not: the worker reads one frame at a time.)
    /// </summary>
    [Fact]
    public async Task A_stream_slow_to_open_holds_the_decode_up_no_longer_than_the_bound()
    {
        var log = new MemoryLog();
        using var rig = PreviewRig(options: o => o with { PreviewStopLimit = TimeSpan.FromMilliseconds(100) }, logs: log);
        using var open = new ManualResetEventSlim();
        try
        {
            await rig.EnableAsync();
            rig.Platform.Worker.HoldStreamStart = open;
            rig.Press();
            rig.DrainUntil(() => Count(rig, "stream_start") == 1);

            rig.Release();
            // The bound runs out, and the decode queues behind the open on
            // the wire a moment later.
            Assert.True(SpinWait.SpinUntil(
                () => log.Lines.Any(l => l.StartsWith("The live preview had not stopped after", StringComparison.Ordinal)),
                5_000));
            Thread.Sleep(200);
            open.Set();
            rig.DrainUntil(() => Inserted(rig.View.Last) && Count(rig, "stream_cancel") == 1);

            var ops = rig.Platform.Worker.Ops().Where(o => o is "stream_start" or "stream_cancel" or "transcribe").ToArray();
            Assert.Equal(["stream_start", "transcribe", "stream_cancel"], ops);
            Assert.All(rig.Hud.Shows, pill => Assert.Null(pill.Caption));
        }
        finally
        {
            open.Set();
        }
    }

    /// <summary>
    /// What the bound is for: a caption decode already on the wire at the
    /// release. The decode waits for that push to answer and for the
    /// stream's cancel, in that order, as the Mac's stop does, and only then
    /// goes to the worker. A decode that did not wait would queue behind the
    /// push and reach the worker before the cancel.
    /// </summary>
    [Fact]
    public async Task A_push_on_the_wire_at_the_release_is_answered_and_its_stream_cancelled_before_the_decode()
    {
        using var rig = PreviewRig();
        using var push = new ManualResetEventSlim();
        try
        {
            await rig.EnableAsync();
            PressAndPreview(rig);
            var pushes = Count(rig, "stream_push");
            rig.Platform.Worker.HoldStreamPush = push;
            rig.Platform.Microphone.Speak();
            Assert.True(SpinWait.SpinUntil(() => Count(rig, "stream_push") == pushes + 1, 5_000));

            rig.Release();
            // Well inside the 2 s bound, and well under the host's own 2 s
            // limit on a stream op.
            Thread.Sleep(300);
            push.Set();
            rig.DrainUntil(() => Inserted(rig.View.Last));

            var ops = rig.Platform.Worker.Ops().Where(o => o is "stream_push" or "stream_cancel" or "transcribe").ToArray();
            Assert.Equal(["stream_push", "stream_cancel", "transcribe"], ops[^3..]);
            Assert.Contains("bank pin", rig.Platform.Clipboard.Writes[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            push.Set();
            rig.Platform.Worker.HoldStreamPush = null;
        }
    }

    /// <summary>
    /// The other side of <see cref="A_stream_slow_to_open_holds_the_decode_up_no_longer_than_the_bound"/>:
    /// an open that answers inside the bound is waited for, and its stream
    /// is cancelled before the decode goes.
    /// </summary>
    [Fact]
    public async Task A_stream_that_opens_inside_the_bound_is_cancelled_before_the_decode()
    {
        using var rig = PreviewRig();
        using var open = new ManualResetEventSlim();
        try
        {
            await rig.EnableAsync();
            rig.Platform.Worker.HoldStreamStart = open;
            rig.Press();
            rig.DrainUntil(() => Count(rig, "stream_start") == 1);

            rig.Release();
            Thread.Sleep(300);
            open.Set();
            rig.DrainUntil(() => Inserted(rig.View.Last));

            var ops = rig.Platform.Worker.Ops().Where(o => o is "stream_start" or "stream_cancel" or "transcribe").ToArray();
            Assert.Equal(["stream_start", "stream_cancel", "transcribe"], ops);
        }
        finally
        {
            open.Set();
        }
    }

    /// <summary>
    /// A worker that dies mid-hold takes its stream with it. Ending the hold
    /// sends no cancel for it, so nothing but the controller's own restart
    /// policy starts a worker again: once, out loud, and counted.
    /// </summary>
    [Fact]
    public async Task Escape_after_the_worker_died_mid_hold_sends_no_cancel_and_starts_no_worker_of_its_own()
    {
        var log = new MemoryLog();
        using var rig = PreviewRig(logs: log);
        await rig.EnableAsync();
        PressAndPreview(rig);
        var starts = rig.Platform.Worker.Starts;
        rig.Platform.Worker.Crash();

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        var escaped = System.Diagnostics.Stopwatch.StartNew();
        rig.Controller.Tick();
        rig.DrainUntil(() => Cancelled(rig.View.Last));
        Assert.True(escaped.Elapsed < TimeSpan.FromSeconds(1), $"Escape took {escaped.Elapsed}");
        rig.Release();

        rig.TickUntil(() => log.Lines.Contains("Speech recognition restarted"));
        Assert.Contains(log.Lines, l => l.StartsWith("The speech worker stopped between dictations", StringComparison.Ordinal));
        Assert.Equal(starts + 1, rig.Platform.Worker.Starts);
        Assert.Equal(0, Count(rig, "stream_cancel"));
    }

    [Fact]
    public async Task A_microphone_that_would_not_open_opens_no_stream_and_ends_as_without_the_preview()
    {
        // As on the Mac, the stream opens once capture is up.
        string Ending(Rig rig, MemoryLog log)
        {
            rig.Platform.Microphone.StartSucceeds = false;
            rig.Press();
            rig.Release();
            log.AwaitRaised();
            rig.Dispatcher.Drain();
            return rig.View.Last.Status;
        }

        var plainLog = new MemoryLog();
        using var plain = new Rig(logs: plainLog);
        await plain.EnableAsync();
        var previewLog = new MemoryLog();
        using var rig = PreviewRig(logs: previewLog);
        await rig.EnableAsync();

        Assert.Equal(Ending(plain, plainLog), Ending(rig, previewLog));
        Assert.DoesNotContain(rig.Platform.Worker.Ops(), op => op.StartsWith("stream_", StringComparison.Ordinal));
        Assert.All(rig.Hud.Shows, pill => Assert.Null(pill.Caption));
    }

    /// <summary>
    /// A caption and a stop are drawn on the UI thread after they were
    /// posted. One posted for a hold that has ended must not land on the
    /// next: its words would stand on another hold's pill, and its stop
    /// would end that hold's preview. (In the app the dispatcher runs posts
    /// ahead of the tick that admits a press; the rig does not drain until
    /// told, which makes the order reachable here.)
    /// </summary>
    [Fact]
    public async Task A_caption_or_a_stop_posted_for_an_earlier_hold_never_reaches_a_later_one()
    {
        var log = new MemoryLog();
        using var rig = PreviewRig(logs: log);
        await rig.EnableAsync();
        rig.Platform.Worker.PreviewReply = "[You] first hold words";

        // The first hold: its caption is posted, and nothing is drained.
        var posted = rig.Dispatcher.Posted;
        rig.Press();
        Assert.True(SpinWait.SpinUntil(() => Count(rig, "stream_push") == 1 && rig.Dispatcher.Posted >= posted + 2, 5_000));
        rig.Release();
        log.AwaitRaised();

        // The second hold is admitted before any of the first's posts run.
        rig.Platform.Worker.PreviewReply = "[You] second hold words";
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        Assert.True(SpinWait.SpinUntil(() => Count(rig, "stream_start") == 2, 5_000));
        rig.Platform.Microphone.Speak();

        rig.DrainUntil(() => rig.Hud.Current?.Caption == "second hold words");
        Assert.DoesNotContain(rig.Hud.Shows, pill => pill.Caption == "first hold words");

        rig.Release();
        rig.DrainUntil(() => Volatile.Read(ref rig.Platform.Paste.Count) == 2 && Inserted(rig.View.Last));
        Assert.Equal(2, Count(rig, "stream_cancel"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("2")]
    public async Task Without_DICTATION_PREVIEW_1_nothing_is_composed(string? value)
    {
        // Windows' gate is opt-in, where the Mac's is opt-out (=0).
        using var rig = new Rig(environment: name => name == DictationEnvironment.Preview ? value : null);
        rig.Platform.Worker.Transcript = Words;
        await rig.EnableAsync();

        rig.Press();
        var listeners = rig.Platform.Microphone.Listeners;
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.DoesNotContain(rig.Platform.Worker.Ops(), op => op.StartsWith("stream_", StringComparison.Ordinal));
        // The tap's and the take spool's only: the preview never listened.
        Assert.Equal(FakeMicrophone.ArmedListeners, listeners);
        Assert.All(rig.Hud.Shows, pill => Assert.Null(pill.Caption));
        // Dictation itself is untouched by the gate.
        Assert.Contains("bank pin", rig.Platform.Clipboard.Writes[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_an_indicator_to_draw_it_no_preview_runs()
    {
        // The caption is all a preview is for, and its decodes cost the CPU
        // the final pass runs on.
        using var rig = new Rig(environment: On, hud: false);
        await rig.EnableAsync();

        rig.Dictate(Inserted);

        Assert.DoesNotContain(rig.Platform.Worker.Ops(), op => op.StartsWith("stream_", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_1_turns_the_switch_on()
    {
        Assert.True(DictationEnvironment.PreviewIsOn("1"));
        Assert.True(DictationEnvironment.PreviewIsOn(" 1 "));
        foreach (var value in new[] { null, "", "0", "true", "yes", "on", "01", "2" })
        {
            Assert.False(DictationEnvironment.PreviewIsOn(value), value ?? "(unset)");
        }

        Assert.Contains(DictationEnvironment.Preview, DictationEnvironment.Overrides);
    }

    // ─── Windows ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_press_into_a_password_field_opens_no_stream_at_all()
    {
        // The press-time check refuses it before the microphone opens, so no
        // word spoken into a password field reaches a recogniser.
        using var rig = PreviewRig();
        await rig.EnableAsync();
        rig.Platform.SecureField.Next = SecureFieldState.Secure;

        rig.Press();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtPress);
        rig.Release();
        Thread.Sleep(50);
        rig.Dispatcher.Drain();

        Assert.DoesNotContain(rig.Platform.Worker.Ops(), op => op.StartsWith("stream_", StringComparison.Ordinal));
        Assert.All(rig.Hud.Shows, pill => Assert.Null(pill.Caption));
    }

    [Fact]
    public async Task Audio_before_the_press_never_reaches_the_stream()
    {
        // The Mac's "capture publishes post-trigger chunks but never warm
        // pre-roll": here the preview is fed only from the admitted press.
        using var rig = PreviewRig();
        await rig.EnableAsync();
        rig.Platform.Microphone.Speak();
        Assert.Equal(FakeMicrophone.ArmedListeners, rig.Platform.Microphone.Listeners);

        PressAndPreview(rig);
        var pushed = rig.Platform.Worker.FramesOf("stream_push")
            .Sum(f => Convert.FromBase64String(f.GetProperty("pcm_b64").GetString()!).Length / 2);
        // Half a second at 16 kHz: the press's speech, and nothing before it.
        Assert.Equal(8_000, pushed);

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        // The feed lets go of the microphone when the hold stops listening.
        Assert.Equal(FakeMicrophone.ArmedListeners, rig.Platform.Microphone.Listeners);
    }

    [Fact]
    public async Task The_stream_is_opened_for_the_language_and_the_app_of_the_hold()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();
        rig.Platform.Target.Next = FocusedApplicationResult.Resolved("WindowsTerminal", 9, "PowerShell");
        PressAndPreview(rig);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        rig.Controller.ChooseLanguage("en");
        rig.Platform.Target.Next = FocusedApplicationResult.Resolved("notepad", 42, "Untitled - Notepad");
        PressAndPreview(rig, streams: 2);
        rig.Release();
        rig.DrainUntil(() => Volatile.Read(ref rig.Platform.Paste.Count) == 2 && Inserted(rig.View.Last));

        var starts = rig.Platform.Worker.FramesOf("stream_start");
        Assert.Equal(2, starts.Count);
        string[] Languages(System.Text.Json.JsonElement f) =>
            f.GetProperty("languages").EnumerateArray().Select(l => l.GetString()!).ToArray();
        // Automatic is the Mac's detection pair; a chosen language is pinned.
        Assert.Equal(["zh", "en"], Languages(starts[0]));
        Assert.Equal(["en"], Languages(starts[1]));
        Assert.Equal("terminal", starts[0].GetProperty("vocabulary_scene").GetString());
        Assert.Equal("other", starts[1].GetProperty("vocabulary_scene").GetString());
        Assert.All(starts, f => Assert.False(f.GetProperty("rolling_stable").GetBoolean()));
    }

    [Fact]
    public async Task A_caption_is_never_announced_and_never_quiets_the_ending()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();
        PressAndPreview(rig);

        var captioned = rig.Hud.Shows.Select((pill, i) => (pill, i)).Where(p => p.pill.Caption is not null).ToArray();
        Assert.NotEmpty(captioned);
        foreach (var (pill, i) in captioned)
        {
            Assert.False(rig.Hud.Announced[i]);
            Assert.Equal("Listening", pill.QuietName);
            Assert.DoesNotContain("preview", pill.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("preview", pill.AnnouncedText, StringComparison.Ordinal);
        }

        // A hold that stood the pacer's 375 ms, so its release shows
        // "Transcribing…" (DictationHudPacer).
        rig.Clock.Advance(DictationHudPacer.VisibleDwell);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        // The shared model's previewShowedWords quieting is not used: the hold whose
        // preview showed words still says Transcribing and its ending, aloud.
        var after = rig.Hud.Shows.Skip(captioned[^1].i + 1).ToArray();
        Assert.Contains(after, pill => pill.Text == DictationCopy.HudTranscribing);
        Assert.Equal(rig.Hud.Shows[^1], DictationHudFeed.For(
            new DictationSessionResult(DictationOutcome.Inserted, Words, null), new DictationHudContext(), FakeHud.Hold)
            with { SizedFor = rig.Hud.Shows[^1].SizedFor });
        Assert.True(rig.Hud.Announced[^1]);
        Assert.All(after, pill => Assert.Null(pill.Caption));
    }

    [Fact]
    public async Task The_preview_is_logged_in_numbers_only_beside_its_hold()
    {
        var log = new MemoryLog();
        using var rig = PreviewRig(logs: log);
        rig.Platform.Worker.PreviewReply = "[You] my secret plans for tuesday";
        await rig.EnableAsync();
        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Platform.Microphone.Speak();
        rig.DrainUntil(() => rig.Hud.Current?.Caption == "my secret plans for tuesday");
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        var attempt = DictationAttemptLogTests.Attempt(log.Receipts[0]);
        var profile = Assert.Single(log.Lines, l => l.StartsWith("Dictation preview profile:", StringComparison.Ordinal));
        Assert.Contains($"operation {attempt};", profile, StringComparison.Ordinal);
        Assert.Matches(new Regex("pushes [1-9]"), profile);
        var comparison = Assert.Single(log.Lines, l => l.StartsWith("Dictation preview/final:", StringComparison.Ordinal));
        Assert.Contains($"attempt={attempt} ", comparison, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"agreement=[01]\.\d{3}$"), comparison);
        Assert.Matches(new Regex(@"preview_length=27 final_length=\d+ "), comparison);

        var all = string.Join("\n", log.Lines);
        Assert.DoesNotContain("secret plans", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bank pin", all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_worker_that_cannot_open_a_stream_costs_the_caption_not_the_dictation()
    {
        using var rig = PreviewRig();
        rig.Platform.Worker.RefuseStreamStart = true;
        await rig.EnableAsync();

        rig.Press();
        rig.DrainUntil(() => Count(rig, "stream_start") == 1);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Equal(0, Count(rig, "stream_push"));
        Assert.Equal(0, Count(rig, "stream_cancel"));
        Assert.All(rig.Hud.Shows, pill => Assert.Null(pill.Caption));
    }

    /// <summary>
    /// The teardown a live hold gets from quitting and a failure (the same
    /// <c>Stop</c> that <c>Pause</c> runs). The app's own Pause never reaches
    /// a hold: <see cref="DictationController.TryPause"/> refuses it, and
    /// the menu's tests below pin that the refusal leaves the preview alone.
    /// </summary>
    [Fact]
    public async Task Pausing_mid_hold_ends_the_preview_and_lets_go_of_the_microphone()
    {
        using var rig = PreviewRig();
        await rig.EnableAsync();
        PressAndPreview(rig);
        var microphone = rig.Platform.Microphone;
        Assert.True(microphone.Listeners >= 2);

        rig.Controller.Pause();
        rig.Dispatcher.Drain();

        Assert.Equal(0, microphone.Listeners);
        Assert.Null(rig.Hud.Current?.Caption);
    }

    // ─── With the notification-area menu ────────────────────────────────

    /// <summary>Words of the caption and of the final pass that no tray
    /// string contains.</summary>
    private static readonly string[] Spoken = ["hello", "world", "bank"];

    private static (TrayIcon Icon, string ToolTip) TrayOf(DictationViewState state) =>
        (state.Tray.Icon, state.Tray.ToolTip);

    /// <summary>Neither the tooltip nor any line of the menu names a word
    /// the hold's preview or its final pass heard.</summary>
    private static void NamesNoWord(DictationViewState state)
    {
        var lines = DictationTrayMenu.Entries(state.Tray, learningUndoable: false).Select(e => e.Label)
            .Append(state.Tray.ToolTip);
        foreach (var line in lines)
        {
            foreach (var word in Spoken)
            {
                Assert.DoesNotContain(word, line, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>The menu's commands over the rig's controller, as MainWindow
    /// wires them; no learning, so Undo has nothing to do here.</summary>
    private static (DictationTrayCommands Commands, VocabularyModel Vocabulary) Menu(Rig rig)
    {
        var vocabulary = new VocabularyModel(
            new DictationLearning(
                new LearningStore(rig.LearningDirectory),
                new WordListLexicon(LearningGoldenTests.SharedLexicon)),
            new RecordingVocabularyView(),
            rig.Dispatcher.Post);
        return (new DictationTrayCommands(rig.Controller, vocabulary, () => { }, () => { }, () => { }), vocabulary);
    }

    [Fact]
    public async Task During_a_captioned_hold_the_tray_says_listening_over_a_known_block_and_never_the_words()
    {
        // The Mac's glyph precedence (DictationTrayGlyph): a dictation under
        // way outranks a blocked microphone, and the preview changes nothing
        // in it. The caption is the indicator's alone: no repaint of the
        // window while it is up puts a word in the tooltip or the menu.
        using var rig = PreviewRig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked), TrayOf(rig.View.Last));
        var from = rig.View.States.Count;

        // Access turned back on in Settings, and the key held before the
        // window was activated: the block is still what this run knows.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        PressAndPreview(rig);
        Assert.Equal((TrayIcon.Listening, DictationCopy.TrayListening), TrayOf(rig.View.Last));

        // The window activated with the caption up: the block clears, the
        // window repaints, and the hold still wins over ready.
        var renders = rig.View.States.Count;
        rig.Controller.RecheckMicrophone();
        rig.Dispatcher.Drain();
        Assert.Equal(Caption, rig.Hud.Current?.Caption);
        Assert.True(rig.View.States.Count > renders, "the activation repainted the window mid-hold");
        Assert.Equal((TrayIcon.Listening, DictationCopy.TrayListening), TrayOf(rig.View.Last));
        Assert.Equal([DictationTrayItem.Toggle, DictationTrayItem.Cancel], DictationTrayMenu.Items(rig.View.Last.Tray, false).Take(2));

        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(rig.Controller.HoldKey)), TrayOf(rig.View.Last));
        Assert.All(rig.View.States.Skip(from), NamesNoWord);
    }

    [Fact]
    public async Task A_Pause_refused_under_a_dictation_leaves_the_preview_running_and_the_release_still_stops_it_first()
    {
        // TryPause refuses while a dictation listens or transcribes, from the
        // window and the menu alike: the hold goes on, so its caption does,
        // and the release ends the stream before the decode, as ever.
        using var rig = PreviewRig();
        var (commands, vocabulary) = Menu(rig);
        using var _ = vocabulary;
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        await rig.EnableAsync();
        PressAndPreview(rig);
        var listeners = rig.Platform.Microphone.Listeners;

        Assert.False(rig.Controller.TryPause());
        await commands.ChooseAsync(DictationTrayItem.Toggle);
        rig.Dispatcher.Drain();
        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(Caption, rig.Hud.Current?.Caption);
        Assert.Equal(listeners, rig.Platform.Microphone.Listeners);
        Assert.Equal(0, Count(rig, "stream_cancel"));

        // The stream still answers, and its next caption still lands.
        rig.Platform.Worker.PreviewReply = "[You] still heard after the refusal";
        rig.Platform.Microphone.Speak();
        rig.DrainUntil(() => rig.Hud.Current?.Caption == "still heard after the refusal");

        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.False(rig.Controller.TryPause());
        await commands.ChooseAsync(DictationTrayItem.Toggle);
        Assert.True(rig.Controller.IsEnabled);

        decode.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        var ops = rig.Platform.Worker.Ops().Where(o => o is "stream_start" or "stream_cancel" or "transcribe").ToArray();
        Assert.Equal(["stream_start", "stream_cancel", "transcribe"], ops);
        Assert.Null(rig.Hud.Current?.Caption);
    }

    [Fact]
    public async Task Cancel_dictation_from_the_menu_ends_the_preview_at_once_and_the_next_hold_gets_its_own()
    {
        using var rig = PreviewRig();
        var (commands, vocabulary) = Menu(rig);
        using var _ = vocabulary;
        await rig.EnableAsync();
        var atRest = rig.Platform.Microphone.Listeners;
        PressAndPreview(rig);
        Assert.Contains(DictationTrayItem.Cancel, DictationTrayMenu.Items(rig.View.Last.Tray, false));

        await commands.ChooseAsync(DictationTrayItem.Cancel);
        // At once, not when the cancel's repaint reaches the UI thread.
        Assert.True(SpinWait.SpinUntil(() => Count(rig, "stream_cancel") == 1, 5_000));
        rig.DrainUntil(() => Cancelled(rig.View.Last));

        Assert.Null(rig.Hud.Current?.Caption);
        Assert.Equal(atRest, rig.Platform.Microphone.Listeners);
        Assert.Equal(0, Count(rig, "transcribe"));
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.DoesNotContain(DictationTrayItem.Cancel, DictationTrayMenu.Items(rig.View.Last.Tray, false));

        rig.Release();
        PressAndPreview(rig, streams: 2);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(2, Count(rig, "stream_cancel"));
    }

    [Fact]
    public async Task After_an_enable_from_the_menu_lands_blocked_and_shows_the_window_holds_start_and_stop_the_preview_as_ever()
    {
        var log = new MemoryLog();
        using var rig = PreviewRig(logs: log);
        var (commands, vocabulary) = Menu(rig);
        using var _ = vocabulary;
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        await commands.ChooseAsync(DictationTrayItem.Toggle);
        rig.Dispatcher.Drain();
        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(1, rig.View.RevealRequests);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);

        // Still blocked: the hold that cannot open the microphone gets no
        // stream and leaves nothing attached to it.
        var atRest = rig.Platform.Microphone.Listeners;
        rig.Platform.Microphone.StartSucceeds = false;
        rig.Press();
        rig.Release();
        log.AwaitRaised();
        rig.Dispatcher.Drain();
        Assert.DoesNotContain(rig.Platform.Worker.Ops(), op => op.StartsWith("stream_", StringComparison.Ordinal));
        Assert.Equal(atRest, rig.Platform.Microphone.Listeners);
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked), TrayOf(rig.View.Last));

        // Access allowed, and the window it brought forward activated.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Platform.Microphone.StartSucceeds = true;
        rig.Controller.RecheckMicrophone();
        PressAndPreview(rig);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Equal(["stream_start", "stream_cancel", "transcribe"],
            rig.Platform.Worker.Ops().Where(o => o is "stream_start" or "stream_cancel" or "transcribe"));
        Assert.Null(rig.Hud.Current?.Caption);

        // Paused and enabled from the menu at rest: the next hold has a
        // stream of its own, from the new arm's preview.
        await commands.ChooseAsync(DictationTrayItem.Toggle);
        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(0, rig.Platform.Microphone.Listeners);
        await commands.ChooseAsync(DictationTrayItem.Toggle);
        rig.Dispatcher.Drain();
        Assert.Equal(1, rig.View.RevealRequests);
        PressAndPreview(rig, streams: 2);
        rig.Release();
        rig.DrainUntil(() => Volatile.Read(ref rig.Platform.Paste.Count) == 2 && Inserted(rig.View.Last));
        Assert.Equal(2, Count(rig, "stream_cancel"));
    }
}
