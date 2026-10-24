using System.Xml.Linq;
using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

// The indicator's busy pills and the endings that close them, ported from the
// Mac one scenario for one where the Mac has it, names kept in C# case:
// macos/Tests/DictationAppCoreTests/DictationHUDTests.swift (the mapping, the
// time and the ending suites), MaximumHoldTests.swift, HUDSequenceTests.swift
// and DictationHUDPanelTests.swift (one width for every busy state). The
// hands-free clock's own table and the m:ss clock are LATCH's, in
// HandsFreeSurfaceTests. Windows draws the Mac's title and hint as one line,
// "title — hint", which the window splits in two (DictationHudLines).

/// <summary>Mac suite "The HUD shows exactly the live dictation state", and
/// the hold's life on the pill through the controller the app runs.</summary>
public sealed class IndicatorHintTests
{
    /// <summary>The product's hold policy: a five-minute limit and a
    /// minute's countdown before it.</summary>
    private static readonly DictationHoldPolicy Limit = DictationHoldPolicy.Windows;

    /// <summary>A rig whose holds wait long with nobody speaking, for tests
    /// of the clock and the limit: without silence stops, which would end a
    /// held hold at a minute and a hands-free one at 15 s (HoldPolicyTests).</summary>
    private static Rig Quiet() => new(options: o => o with { HoldPolicy = o.HoldPolicy.WithoutSilenceStops() });

    /// <summary>A stop at the product's limit.</summary>
    private static readonly DictationHoldEnding AtTheLimit = new(DictationHoldStop.Ceiling, DictationHoldPolicy.Windows.Ceiling);

    private static DictationHudLines Lines(string text) => DictationHudLines.For(text);

    private static string Hint(bool latched, double elapsed, string key = "Right Ctrl") =>
        Lines(DictationCopy.HudListening(key, latched, TimeSpan.FromSeconds(elapsed), Limit)).Detail!;

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private static string Shown(Rig rig) => rig.Hud.Current?.Text ?? string.Empty;

    /// <summary>Time passes with the key as it is, polled once.</summary>
    private static void Wait(Rig rig, int milliseconds)
    {
        rig.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
    }

    // ─── The mapping (DictationHUDMappingTests) ─────────────────────────

    [Fact]
    public async Task A_hold_shows_Listening_with_the_release_hint()
    {
        Assert.Equal("Listening", Lines(DictationCopy.HudHeld).Title);
        Assert.Equal("Release to insert · Esc to cancel", Lines(DictationCopy.HudHeld).Detail);

        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        Assert.Equal(DictationCopy.HudHeld, Shown(rig));
        Assert.Null(rig.Hud.Current!.Dwell);
        Assert.False(rig.Hud.Announced[^1]);
        Assert.Equal("Listening", rig.Hud.Current!.UnannouncedName);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
    }

    [Theory]
    [InlineData("Right Ctrl")]
    [InlineData("Right Shift")]
    [InlineData("F13")]
    public void A_latched_hold_names_the_stop_with_the_chosen_key(string key)
    {
        var line = DictationCopy.HudListening(key, latched: true, TimeSpan.Zero, Limit);

        Assert.Equal("Listening", Lines(line).Title);
        Assert.Equal($"0:00 · Press {key}, Space or Enter to stop", Lines(line).Detail);
    }

    /// <summary>The hint is true: Escape during the transcription cancels
    /// it, and nothing is typed.</summary>
    [Fact]
    public async Task A_release_shows_Transcribing_and_Escape_still_cancels_it()
    {
        Assert.Equal("Transcribing…", Lines(DictationCopy.HudTranscribing).Title);
        Assert.Equal("Esc to cancel", Lines(DictationCopy.HudTranscribing).Detail);

        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Clock.Advance(DictationHudPacer.VisibleDwell);
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.Equal(DictationCopy.HudTranscribing, Shown(rig));

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Controller.Tick();
        decode.Set();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Cancelled", StringComparison.Ordinal));

        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Equal("Cancelled", Shown(rig));
    }

    /// <summary>HUDSequenceTests' "press, release and insert": held,
    /// transcribing, Windows' typing, the receipt, then nothing. Each busy
    /// state stands the pacer's 375 ms, as a hold and a decode a person
    /// can see do (<see cref="DictationHudPacer"/>).</summary>
    [Fact]
    public async Task Hold_transcribing_and_hidden_is_the_whole_lifecycle()
    {
        using var rig = new Rig();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        await rig.EnableAsync();

        rig.Press();
        rig.Dispatcher.Drain();
        rig.Clock.Advance(DictationHudPacer.VisibleDwell);
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        rig.Clock.Advance(DictationHudPacer.VisibleDwell);
        decode.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        var receipt = rig.Hud.Current!;
        rig.Clock.Advance(receipt.Dwell!.Value);
        rig.Controller.Tick();

        Assert.Equal(
            [DictationCopy.HudHeld, DictationCopy.HudTranscribing, DictationCopy.HudTyping, "Typed"],
            rig.Hud.Shows.Select(m => m.Text).ToArray());
        Assert.Null(rig.Hud.Current);
    }

    /// <summary>HUDSequenceTests' "a latched dictation goes from the latch to
    /// transcribing directly": no held pill in between.</summary>
    [Fact]
    public async Task A_latched_dictation_goes_from_the_latch_to_transcribing_directly()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.DoubleTap();
        var latched = hands.Rig.Hud.Shows.Count;
        Assert.StartsWith("Listening — 0:00 · Press Right Ctrl", Shown(hands.Rig), StringComparison.Ordinal);
        Assert.Equal("Listening, hands-free", hands.Rig.Hud.Current!.UnannouncedName);

        hands.Wait(1_000);
        using var decode = new ManualResetEventSlim();
        hands.Rig.Platform.Worker.HoldTranscribe = decode;
        hands.Event(HoldKeyState.VkSpace, HandsFree.Down);
        hands.Event(HoldKeyState.VkSpace, HandsFree.Up);
        hands.DrainUntil(() => hands.Status == DictationCopy.Transcribing);
        // A decode a person can see: "Transcribing…" stands its 375 ms
        // before "Typing" may replace it (DictationHudPacer).
        hands.Rig.Clock.Advance(DictationHudPacer.VisibleDwell);
        decode.Set();
        hands.DrainUntil(() => hands.Inserted);

        var after = hands.Rig.Hud.Shows.Skip(latched).Select(m => m.Text).ToArray();
        Assert.Equal(
            ["Listening — 0:01 · Press Right Ctrl, Space or Enter to stop", DictationCopy.HudTranscribing, DictationCopy.HudTyping, "Typed"],
            after);
    }

    // ─── Time in a long hold (DictationHUDTimeTests) ────────────────────

    [Theory]
    [InlineData(42.0, "0:42 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(65.9, "1:05 · Press Right Ctrl, Space or Enter to stop")]
    public void A_latched_hold_shows_how_long_it_has_run(double elapsed, string hint) =>
        Assert.Equal(hint, Hint(latched: true, elapsed));

    [Fact]
    public void A_held_key_keeps_its_plain_hint_until_the_countdown()
    {
        Assert.Equal("Release to insert · Esc to cancel", Hint(latched: false, 42));
        Assert.Equal(DictationCopy.HudHeld, DictationCopy.HudListening("Right Ctrl", latched: false, TimeSpan.FromSeconds(42), Limit));
    }

    [Theory]
    [InlineData(false, 239.9, "Release to insert · Esc to cancel")]
    [InlineData(false, 240.0, "Stops in 1:00 · Release to insert")]
    [InlineData(false, 242.2, "Stops in 0:58 · Release to insert")]
    [InlineData(false, 299.99, "Stops in 0:01 · Release to insert")]
    [InlineData(true, 290.0, "Stops in 0:10 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(true, 305.0, "Stops in 0:00 · Press Right Ctrl, Space or Enter to stop")]
    [InlineData(false, 305.0, "Stops in 0:00 · Release to insert")]
    public void The_last_minute_counts_down_held_or_latched(bool latched, double elapsed, string hint) =>
        Assert.Equal(hint, Hint(latched, elapsed));

    /// <summary>The Mac's "without a clock, and outside a recording, the hint
    /// is the plain one": the listening pill a hold opens with is the plain
    /// held hint, and the transcription's has no time in it.</summary>
    [Fact]
    public void Without_a_clock_and_outside_a_recording_the_hint_is_the_plain_one()
    {
        var hud = new FakeHud();
        var clock = new FakeClock();
        using var feed = new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold);

        feed.State(DictationState.Listening);
        Assert.Equal(DictationCopy.HudHeld, hud.Current!.Text);
        clock.Advance(DictationHudPacer.VisibleDwell);
        feed.State(DictationState.Transcribing);
        Assert.Equal("Esc to cancel", Lines(hud.Current!.Text).Detail);
        Assert.DoesNotContain(hud.Current!.Text, char.IsAsciiDigit);
    }

    /// <summary>
    /// The held countdown through the controller: the plain hint for four
    /// minutes, then "Stops in 1:00", redrawn when the second changes and not
    /// on every 30 ms poll, and never spoken while the microphone is open.
    /// At the limit the pill transcribes, as on the Mac.
    /// </summary>
    [Fact]
    public async Task A_held_hold_counts_down_its_last_minute_once_a_second_and_never_aloud()
    {
        using var rig = Quiet();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        var shows = rig.Hud.Shows.Count;

        Wait(rig, 239_900);
        Assert.Equal(DictationCopy.HudHeld, Shown(rig));
        Assert.Equal(shows, rig.Hud.Shows.Count);

        Wait(rig, 100);
        Assert.Equal("Listening — Stops in 1:00 · Release to insert", Shown(rig));
        shows = rig.Hud.Shows.Count;
        for (var tick = 0; tick < 30; tick++)
        {
            Wait(rig, 30);
        }

        Assert.Equal(shows, rig.Hud.Shows.Count);
        Wait(rig, 1_200);
        Assert.Equal("Listening — Stops in 0:58 · Release to insert", Shown(rig));
        Wait(rig, 57_200);
        Assert.Equal("Listening — Stops in 0:01 · Release to insert", Shown(rig));
        Assert.True(rig.Hud.Bars, "the level bars stay beside the countdown");

        var listening = rig.Hud.Shows.Select((m, i) => (m, i)).Where(s => s.m.Text.StartsWith("Listening", StringComparison.Ordinal)).ToArray();
        Assert.True(listening.Length >= 4);
        Assert.All(listening, s => Assert.False(rig.Hud.Announced[s.i], $"\"{s.m.Text}\" was spoken"));
        Assert.All(listening, s => Assert.Equal("Listening", s.m.UnannouncedName));

        Wait(rig, 700);
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.Contains(DictationCopy.HudTranscribing, rig.Hud.Shows.Skip(listening[^1].i).Select(m => m.Text));
        // The tick that reaches the limit ends the hold before the clock is
        // drawn again: the last countdown is 0:01, never 0:00.
        Assert.DoesNotContain(rig.Hud.Shows, m => m.Text.Contains("Stops in 0:00", StringComparison.Ordinal));
        rig.Release();
    }

    // ─── The ceiling (MaximumHoldTests) ─────────────────────────────────

    [Fact]
    public async Task The_ceiling_says_so_in_the_status_line_and_on_the_indicator()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();

        Wait(rig, 300_000);
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.Contains(rig.View.States, s => s.Status == "Five minutes is the longest recording, so it stopped there. Transcribing…");
        Assert.Equal("Text inserted. A recording stops at five minutes; hold Right Ctrl again to continue.", rig.View.Last.Status);
        var ending = rig.Hud.Current!;
        Assert.Equal(DictationCopy.HudStoppedAtLimit("Right Ctrl", Limit.Ceiling), ending.Text);
        Assert.Equal(HudTone.Caution, ending.Tone);
        Assert.True(rig.Hud.Announced[^1]);
        rig.Release();
    }

    /// <summary>The Mac's "the hold's clock is published while it records,
    /// and only then": once the hold is released the listening pill is not
    /// drawn again, whatever the clock does.</summary>
    [Fact]
    public async Task The_hold_s_clock_is_shown_while_it_records_and_only_then()
    {
        using var rig = Quiet();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Wait(rig, 286_000);
        Assert.Equal("Listening — Stops in 0:14 · Release to insert", Shown(rig));

        var released = rig.Hud.Shows.Count;
        rig.Release();
        // The seconds go on while the release's repaint is still queued: the
        // session has moved on, so the pill is not drawn again.
        Assert.True(
            SpinWait.SpinUntil(() => rig.Platform.Worker.Ops().Contains("transcribe"), TimeSpan.FromSeconds(10)),
            "the release never reached the recogniser");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Controller.Tick();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Wait(rig, 1_000);
        Wait(rig, 1_000);
        decode.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));

        Assert.DoesNotContain(rig.Hud.Shows.Skip(released), m => m.Text.StartsWith("Listening", StringComparison.Ordinal));
        Assert.Contains(DictationCopy.HudTranscribing, rig.Hud.Shows.Skip(released).Select(m => m.Text));
    }

    [Fact]
    public async Task A_release_before_the_ceiling_ends_the_timer_with_the_hold()
    {
        using var rig = Quiet();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Wait(rig, 290_000);
        rig.Release();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Wait(rig, 20_000);

        Assert.Equal("Text inserted. Hold Right Ctrl for your next sentence.", rig.View.Last.Status);
        Assert.DoesNotContain(rig.Hud.Shows, m => m.Text.StartsWith("Stopped at", StringComparison.Ordinal));
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    // ─── A live hold outranks an ending (DictationHUDContent.resolve) ───

    /// <summary>
    /// The Mac's priority: a live hold, then an ending. The recogniser gives
    /// up restarting while the key is held: the pill keeps listening and
    /// says nothing (the microphone is open, and the next tick would take the
    /// ending down anyway); the release tries the recogniser once more and,
    /// when that fails too, ends on the give-up, announced once the capture
    /// is over.
    /// </summary>
    [Fact]
    public async Task A_recogniser_that_gives_up_while_the_hold_listens_waits_for_the_release()
    {
        var retry = new TaskCompletionSource();
        var waits = 0;
        using var rig = new Rig(options: o => o with
        {
            RestartWait = (_, ct) =>
            {
                Interlocked.Increment(ref waits);
                return retry.Task.WaitAsync(ct);
            },
        });
        await rig.EnableAsync();
        rig.Platform.Worker.InitSucceeds = false;
        rig.Platform.Worker.Crash();
        rig.TickUntil(() => Volatile.Read(ref waits) == 1);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, Shown(rig));
        var pressed = rig.Hud.Shows.Count;

        retry.SetResult();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.WorkerRestartFailed);
        Wait(rig, 30);

        Assert.Equal(DictationCopy.HudHeld, Shown(rig));
        Assert.Equal(pressed, rig.Hud.Shows.Count);

        rig.Release();
        rig.DrainUntil(() => rig.Hud.Current?.Text == DictationCopy.HudRecognizerCouldNotRestart);
        Assert.True(rig.Hud.Announced[^1]);
        Assert.Single(rig.Hud.Shows.Skip(pressed), m => m.Text == DictationCopy.HudRecognizerCouldNotRestart);
        Assert.Equal(0, rig.Platform.Paste.Count);
    }

    // ─── What Narrator finds (DictationHUDAccessibilityTests) ───────────

    /// <summary>The Mac's "the spoken label is the state's alone: no preview,
    /// no level, no clock". The listening pill is never announced; the name a
    /// reader finds on it stays the state's through the hint, the clock, the
    /// countdown and the level, and the transcribing pill, which is
    /// announced, needs none.</summary>
    [Fact]
    public void The_spoken_label_is_the_state_s_alone_no_level_no_clock()
    {
        Assert.Equal("Listening", DictationCopy.HudListeningName(latched: false));
        Assert.Equal("Listening, hands-free", DictationCopy.HudListeningName(latched: true));

        var hud = new FakeHud();
        var clock = new FakeClock();
        using var feed = new DictationHudFeed(hud, () => clock.Now, () => FakeHud.Hold);
        feed.State(DictationState.Listening);
        feed.Level(0.7f);
        Assert.Equal("Listening", hud.Current!.UnannouncedName);
        foreach (var seconds in new[] { 3.0, 59.0, 106.0, 119.5 })
        {
            feed.Listening(latched: false, TimeSpan.FromSeconds(seconds));
            Assert.Equal("Listening", hud.Current!.UnannouncedName);
            feed.Listening(latched: true, TimeSpan.FromSeconds(seconds));
            Assert.Equal("Listening, hands-free", hud.Current!.UnannouncedName);
        }

        Assert.True(hud.Shows.Count >= 8);
        Assert.All(hud.Announced, Assert.False);
        Assert.All(hud.Shows, m => Assert.DoesNotContain(m.UnannouncedName!, char.IsAsciiDigit));

        clock.Advance(DictationHudPacer.VisibleDwell);
        feed.State(DictationState.Transcribing);
        Assert.True(hud.Announced[^1]);
        Assert.Null(hud.Current!.UnannouncedName);
    }

    /// <summary>The window gives an unannounced pill that name, and raises no
    /// live-region event for it; it remembers the name as the last sentence,
    /// so the ending after the hold is news even when it repeats the one
    /// before. Pinned on the source: WPF cannot run here.</summary>
    [Fact]
    public void An_unannounced_pill_is_named_for_its_state_without_an_event()
    {
        var code = File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs"));
        var show = DictationHudGeometryTests.Member(code, "void IDictationHud.Show(");
        var quiet = DictationHudGeometryTests.Member(code, "private void NameQuietly(");

        Assert.Contains("if (!announce)", show, StringComparison.Ordinal);
        Assert.Contains("NameQuietly(model.QuietName);", show, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(StatusLine, name);", quiet, StringComparison.Ordinal);
        Assert.Contains("lastAnnounced = name;", quiet, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveRegion", quiet, StringComparison.Ordinal);
    }

    /// <summary>
    /// RECEIPT's learned correction, shown without a word while the Dictation
    /// window is in front, is named by its whole line, "what to do"
    /// included, as an announced pill is and as the Mac's label is its title
    /// and detail: the second line is out of the automation tree, so a name
    /// from the title alone would drop "review or undo in Dictation". The
    /// listening pill keeps its state's name.
    /// </summary>
    [Fact]
    public void A_quiet_ending_is_named_by_its_whole_line_and_a_listening_pill_by_its_state()
    {
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => TimeSpan.Zero, () => FakeHud.Hold);

        feed.CorrectionRemembered(announce: false);
        Assert.False(hud.Announced[^1]);
        Assert.Equal("Remembered your correction — review or undo in Earlet", hud.Current!.QuietName);
        Assert.Equal(hud.Current.AnnouncedText, hud.Current.QuietName);

        feed.State(DictationState.Listening);
        Assert.Equal("Listening", hud.Current!.QuietName);
        feed.Listening(latched: true, TimeSpan.FromSeconds(110));
        Assert.Equal("Listening, hands-free", hud.Current!.QuietName);
    }

    // ─── Endings (DictationHUDOutcomeTests) ─────────────────────────────

    [Fact]
    public void The_ceiling_s_ending_names_the_limit_and_how_to_go_on()
    {
        var ending = DictationHudFeed.For(
            new DictationSessionResult(DictationOutcome.Inserted, "words", null), new DictationHudContext(Stopped: AtTheLimit), FakeHud.Hold);

        Assert.Equal("Stopped at 5:00", Lines(ending.Text).Title);
        Assert.Equal("Your words went in, hold Right Ctrl again to keep going", Lines(ending.Text).Detail);
        Assert.Equal(HudTone.Caution, ending.Tone);
        Assert.Equal(DictationHudModel.FailureDwell, ending.Dwell);

        // The limit the stop names is the one that ended the hold, whatever
        // the policy says by the time the ending is drawn.
        var longer = DictationHudFeed.For(
            new DictationSessionResult(DictationOutcome.Inserted, "words", null),
            new DictationHudContext(Stopped: new(DictationHoldStop.Ceiling, TimeSpan.FromMinutes(20))),
            new DictationHudHold("Scroll Lock", Limit));
        Assert.Equal("Stopped at 20:00 — your words went in, hold Scroll Lock again to keep going", longer.Text);
    }

    [Fact]
    public void Nothing_heard_says_to_hold_the_key_and_speak()
    {
        var ending = DictationHudFeed.For(
            new DictationSessionResult(DictationOutcome.NoSpeech, null, null), new DictationHudContext(), FakeHud.Hold);

        Assert.Equal("Nothing heard", Lines(ending.Text).Title);
        Assert.Equal("Hold Right Ctrl and speak", Lines(ending.Text).Detail);
    }

    /// <summary>The outcomes this app can reach. The rest are the
    /// workflow and voice-delete endings: Arm passes no router and no voice
    /// delete, so the session never raises them here.</summary>
    public static TheoryData<DictationOutcome> ReachableOutcomes()
    {
        var data = new TheoryData<DictationOutcome>();
        foreach (var outcome in Enum.GetValues<DictationOutcome>().Where(o => o is not (
            DictationOutcome.RanWorkflow or DictationOutcome.WorkflowNotMatched or DictationOutcome.VoiceDeleted
            or DictationOutcome.VoiceDeleteNothingToDelete or DictationOutcome.VoiceDeleteRefused)))
        {
            data.Add(outcome);
        }

        return data;
    }

    /// <summary>
    /// DESIGN's "every line reads what happened — what to do": every ending
    /// this app can show, in every context the controller gives, has its
    /// second half, except the receipts for what the user can see ("Typed")
    /// or did ("Cancelled"). The shared model's lines that stopped at what
    /// happened keep their tone and dwell, and gain only the words.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReachableOutcomes))]
    public void Every_ending_that_is_not_a_receipt_says_what_to_do(DictationOutcome outcome)
    {
        foreach (var context in DictationHudFeedTests.Contexts)
        {
            var ending = DictationHudFeed.For(new DictationSessionResult(outcome, null, null), context, FakeHud.Hold);
            if (ending.Dwell == DictationHudModel.ReceiptDwell && ending.Text is "Typed" or "Cancelled")
            {
                continue;
            }

            Assert.False(Lines(ending.Text).Detail is null, $"{outcome} with {context}: \"{ending.Text}\" says only what happened");
        }

        var shared = DictationHudModel.For(new DictationSessionResult(outcome, null, null));
        var plain = DictationHudFeed.For(new DictationSessionResult(outcome, null, null), new DictationHudContext(), FakeHud.Hold);
        if (outcome is DictationOutcome.InsertionRefused or DictationOutcome.NoFocusedWindow or DictationOutcome.NoSpeech
            or DictationOutcome.NoAudio or DictationOutcome.MicrophoneUnavailable or DictationOutcome.HoldTooShort)
        {
            Assert.Equal((shared.Tone, shared.Dwell), (plain.Tone, plain.Dwell));
            Assert.NotEqual(shared.Text, plain.Text);
        }
    }

    /// <summary>The same rule for the pills that end no dictation: the press
    /// refusals, Copy last text's lines, the recogniser's give-up and
    /// RECEIPT's learned correction each say what to do.</summary>
    [Fact]
    public void Every_notice_says_what_to_do_the_learned_correction_included()
    {
        var notices = DictationHudFeedTests.EveryNotice().ToArray();

        Assert.Contains(notices, m => m.Text == DictationCopy.HudCorrectionRemembered);
        Assert.All(notices, m => Assert.False(Lines(m.Text).Detail is null, $"\"{m.Text}\" says only what happened"));
        Assert.Equal("Review or undo in Earlet", Lines(DictationCopy.HudCorrectionRemembered).Detail);
    }

    [Fact]
    public void The_lines_that_name_the_key_name_the_chosen_one()
    {
        var hold = new DictationHudHold("Right Shift", Limit);
        DictationHudModel Ending(DictationOutcome outcome, DictationHudContext? context = null) =>
            DictationHudFeed.For(new DictationSessionResult(outcome, null, null), context ?? new DictationHudContext(), hold);

        Assert.Equal("Nothing heard — hold Right Shift and speak", Ending(DictationOutcome.NoSpeech).Text);
        Assert.Equal("Too short — hold Right Shift longer", Ending(DictationOutcome.HoldTooShort).Text);
        Assert.Contains("hold Right Shift again", Ending(DictationOutcome.Inserted, new(Stopped: AtTheLimit)).Text, StringComparison.Ordinal);
    }

    /// <summary>The controller reads the key at each pill: one chosen at rest
    /// is the one the next hold's lines name and are sized for.</summary>
    [Fact]
    public async Task The_controller_names_the_key_the_user_chose()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Controller.ChooseHoldKey(DictationHotkeys.VkRightShift);
        rig.Dispatcher.Drain();

        rig.Platform.Trigger.Down = true;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudBusyLines("Right Shift", Limit), rig.Hud.Current!.SizedFor);
        rig.Platform.Microphone.Hush();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Nothing was heard", StringComparison.Ordinal));

        Assert.Equal("Nothing heard — hold Right Shift and speak", Shown(rig));
    }

    // ─── One width (DictationHUDPanelTests) ─────────────────────────────

    /// <summary>A proportional face for the arithmetic, every digit its own
    /// width and '7' the widest, so no digit can stand in for another; the
    /// title's face is larger. The window measures the real ones.</summary>
    internal static double Glyph(char c) => c switch
    {
        >= '0' and <= '9' => 5 + (c - '0') * 7 % 10 / 4.0,
        ' ' or ':' or '·' or ',' => 3,
        '—' => 9,
        '你' => 14,
        >= 'A' and <= 'Z' => 8,
        _ => 6,
    };

    /// <summary>An extreme face: digits far wider than anything else, so a
    /// line with more digits is wider whatever else it says. The busy width
    /// must hold under it too, which only the most digits a hold reaches can
    /// guarantee.</summary>
    internal static double DigitHeavy(char c) => char.IsAsciiDigit(c) ? 40 + (c - '0') : 0.5;

    private static double Detail(string text) => text.Sum(Glyph);

    private static double Title(string text) => 1.2 * text.Sum(Glyph);

    private static double Column(DictationHudModel model) => DictationHudGeometry.WidestLine(model, Title, Detail);

    private static double Column(DictationHudModel model, Func<char, double> face) =>
        DictationHudGeometry.WidestLine(model, t => 1.2 * t.Sum(face), d => d.Sum(face));

    private static DictationHudModel Busy(string text, string key, DictationHoldPolicy limit) =>
        new(text, HudTone.Neutral, null) { SizedFor = DictationCopy.HudBusyLines(key, limit) };

    public static TheoryData<string> HoldKeyNames()
    {
        var data = new TheoryData<string>();
        foreach (var key in DictationHotkeys.HoldChoices)
        {
            data.Add(key.KeyName);
        }

        return data;
    }

    /// <summary>Every line a busy pill can show, at every half second of a
    /// hold held or hands-free, for a five-minute and a twenty-minute limit,
    /// fits the one width in either face: nothing resizes. The silence
    /// warning too, at every half second of a 30 s and a 60 s stop.</summary>
    [Theory]
    [MemberData(nameof(HoldKeyNames))]
    public void Every_busy_line_fits_the_one_text_column_so_nothing_resizes(string key)
    {
        foreach (var face in new Func<char, double>[] { Glyph, DigitHeavy })
        {
            // The stage-1 limit and the twenty minutes stage 2 moves to: the
            // width does not depend on the limit (the clock's reference is
            // "00:00"), as the Mac's DictationHUDPanelTests runs to 1,200 s.
            foreach (var limit in new[] { Limit, Limit with { Ceiling = TimeSpan.FromMinutes(20) } })
            {
                var column = Column(Busy(DictationCopy.HudHeld, key, limit), face);
                for (var half = 0; half <= limit.Ceiling.TotalSeconds * 2 + 4; half++)
                {
                    foreach (var latched in new[] { false, true })
                    {
                        var line = DictationCopy.HudListening(key, latched, TimeSpan.FromSeconds(half / 2.0), limit);
                        var width = Column(new DictationHudModel(line, HudTone.Neutral, null), face);
                        Assert.True(width <= column, $"\"{line}\" ({width}) is wider than the column ({column}) at a {limit} limit");
                    }
                }

                foreach (var stop in new[] { 30, 60 })
                {
                    for (var half = (stop - 10) * 2; half < stop * 2; half++)
                    {
                        var silent = TimeSpan.FromSeconds(half / 2.0);
                        var line = DictationCopy.HudSilenceWarning(new(silent, TimeSpan.FromSeconds(stop) - silent));
                        var width = Column(new DictationHudModel(line, HudTone.Neutral, null), face);
                        Assert.True(width <= column, $"\"{line}\" ({width}) is wider than the column ({column})");
                    }
                }

                foreach (var title in new[] { DictationCopy.HudTranscribing, DictationCopy.HudTyping, DictationCopy.CaptionReferenceWords, DictationCopy.CaptionReferenceCjk })
                {
                    Assert.True(Column(new DictationHudModel(title, HudTone.Neutral, null), face) <= column, title);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(HoldKeyNames))]
    public void Held_hands_free_transcribing_and_typing_share_one_capsule_width(string key)
    {
        var widths = new[]
        {
            DictationCopy.HudHeld,
            DictationCopy.HudListening(key, latched: true, TimeSpan.FromSeconds(65), Limit),
            DictationCopy.HudListening(key, latched: false, TimeSpan.FromSeconds(290), Limit),
            DictationCopy.HudListening(key, latched: true, TimeSpan.FromSeconds(290), Limit),
            DictationCopy.HudSilenceWarning(new(TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(7))),
            DictationCopy.HudTranscribing,
            DictationCopy.HudTyping,
        }.Select(text =>
        {
            var model = Busy(text, key, Limit);
            return DictationHudGeometry.PillWidth(model, Column(model));
        }).Distinct().ToArray();

        var width = Assert.Single(widths);
        var latchedHint = Lines(DictationCopy.HudListening(key, latched: true, TimeSpan.FromSeconds(65), Limit)).Detail!;
        Assert.True(width >= Detail(latchedHint) + DictationHudGeometry.Chrome, "the widest busy line sets the width for all of them");
        Assert.True(width > DictationHudWidth.Compact, "wider than the old compact pill, which cut the hands-free hint");
    }

    /// <summary>The Mac's "recording and transcribing are the fixed-size
    /// states; endings and the mic change grow": an ending starts at the
    /// busy width and takes what its sentence needs, up to the cap.</summary>
    [Fact]
    public void An_ending_starts_at_the_busy_width_and_grows_for_its_sentence()
    {
        var busy = Busy(DictationCopy.HudHeld, "Right Ctrl", Limit);
        var busyWidth = DictationHudGeometry.PillWidth(busy, Column(busy));
        DictationHudModel Ending(string text) =>
            new(text, HudTone.Caution, DictationHudModel.FailureDwell) { SizedFor = busy.SizedFor };

        var typed = Ending("Typed");
        Assert.Equal(busyWidth, DictationHudGeometry.PillWidth(typed, Column(typed)));
        var longer = Ending("Something happened — " + new string('w', 70));
        Assert.True(DictationHudGeometry.PillWidth(longer, Column(longer)) > busyWidth);
        Assert.Equal(DictationHudWidth.Max, DictationHudGeometry.PillWidth(longer, 2_000));
    }

    /// <summary>RECEIPT's learned correction is the one indicator line added
    /// since the busy width (SAVE's lines are the window's): sized for the
    /// hold's busy lines and no wider than the widest of them, for every hold
    /// key, so it starts at the busy width and neither grows the pill after
    /// a dictation nor is cut off.</summary>
    [Theory]
    [MemberData(nameof(HoldKeyNames))]
    public void The_learned_correction_fits_the_one_busy_width(string key)
    {
        var hud = new FakeHud();
        using var feed = new DictationHudFeed(hud, () => TimeSpan.Zero, () => new DictationHudHold(key, Limit));
        feed.CorrectionRemembered();
        var pill = hud.Current!;
        var busy = Busy(DictationCopy.HudHeld, key, Limit);

        Assert.Equal(DictationCopy.HudCorrectionRemembered, pill.Text);
        Assert.Equal(busy.SizedFor, pill.SizedFor);
        var own = Column(new DictationHudModel(pill.Text, pill.Tone, pill.Dwell));
        Assert.True(own <= Column(busy), $"\"{pill.Text}\" ({own}) is wider than the busy column ({Column(busy)}) for {key}");
        Assert.Equal(DictationHudGeometry.PillWidth(busy, Column(busy)), DictationHudGeometry.PillWidth(pill, Column(pill)));
    }

    /// <summary>The feed sizes every pill it shows for the same busy lines,
    /// one list per hold key: the busy pills, the endings, the refusals,
    /// Copy last text's lines and the learned correction.</summary>
    [Fact]
    public void Every_pill_the_feed_shows_is_sized_for_the_hold_s_busy_lines()
    {
        var hud = new FakeHud();
        var hold = FakeHud.Hold;
        using var feed = new DictationHudFeed(hud, () => TimeSpan.Zero, () => hold);

        feed.State(DictationState.Listening);
        feed.Listening(latched: true, TimeSpan.FromSeconds(3));
        feed.State(DictationState.Transcribing);
        feed.State(DictationState.Inserting);
        feed.Result(new DictationSessionResult(DictationOutcome.Inserted, null, null), new DictationHudContext());
        feed.PressRefused(DictationPressRefusal.StillTyping);
        feed.Copied(false);
        feed.RecognizerCouldNotRestart();
        feed.Copied(true);
        feed.CorrectionRemembered();

        Assert.Equal(DictationCopy.HudCorrectionRemembered, hud.Current!.Text);
        var lists = hud.Shows.Select(m => m.SizedFor).Distinct().ToArray();
        var list = Assert.Single(lists);
        Assert.Equal(DictationCopy.HudBusyLines("Right Ctrl", Limit), list);

        hold = new DictationHudHold("Right Shift", Limit);
        feed.State(DictationState.Listening);
        Assert.Equal(DictationCopy.HudBusyLines("Right Shift", Limit), hud.Current!.SizedFor);
    }

    [Fact]
    public void The_widest_line_is_measured_over_both_halves_of_every_line_in_its_own_font()
    {
        var model = new DictationHudModel("Ab — c", HudTone.Neutral, null) { SizedFor = ["Wide title", "x — a much longer detail"] };
        var titles = new List<string>();
        var details = new List<string>();

        var widest = DictationHudGeometry.WidestLine(
            model, t => { titles.Add(t); return t.Length * 10; }, d => { details.Add(d); return d.Length; });

        Assert.Equal(["Ab", "Wide title", "x"], titles);
        Assert.Equal(["C", "A much longer detail"], details);
        Assert.Equal(100, widest);
    }

    [Fact]
    public void The_frozen_reference_is_the_full_caption_budget_both_scripts()
    {
        Assert.Equal(DictationPreviewCaption.DefaultWordWindow, DictationCopy.CaptionReferenceWords.Split(' ').Length);
        Assert.Equal(DictationPreviewCaption.DefaultCjkCharacterBudget, DictationCopy.CaptionReferenceCjk.Length);
        var lines = DictationCopy.HudBusyLines("Right Ctrl", Limit);
        Assert.Contains(DictationCopy.CaptionReferenceWords, lines);
        Assert.Contains(DictationCopy.CaptionReferenceCjk, lines);
    }

    /// <summary>A busy pill whose title has nothing under it (Typing) keeps
    /// the second line's room, so no busy pill is shorter than another; an
    /// ending with one line collapses it. Pinned on the source: WPF cannot
    /// run here.</summary>
    [Fact]
    public void A_busy_pill_keeps_its_second_line_s_room()
    {
        var show = DictationHudGeometryTests.Member(
            File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs")), "void IDictationHud.Show(");

        Assert.Contains(
            ": model.Dwell is null && model.SizedFor is not null ? Visibility.Hidden : Visibility.Collapsed;",
            show, StringComparison.Ordinal);
        var detail = XDocument.Load(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml")).Descendants()
            .Single(e => (string?)e.Attribute(DictationHudGeometryTests.X + "Name") == "DetailLine");
        Assert.Equal("Tabular", (string?)detail.Attributes().Single(a => a.Name.LocalName == "Typography.NumeralAlignment"));
    }
}
