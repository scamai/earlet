using System.Collections.Concurrent;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

// The indicator's pacing and heartbeat, ported from the Mac one scenario for
// one, names kept in C# case: macos/Tests/DictationAppCoreTests/
// DictationHUDTests.swift ("Visible HUD states dwell before they change;
// hidden and mic-changed pre-empt") and DictationHUDPanelTests.swift ("The
// reconcile heartbeat heals a panel the model and the window disagree on").
// Then the Windows halves: the feed that runs them, and the controller's
// tick that drives both.

/// <summary>Mac suite "Visible HUD states dwell before they change; hidden
/// and mic-changed pre-empt".</summary>
public sealed class DictationHudPacerTests
{
    private static readonly TimeSpan Dwell = DictationHudPacer.VisibleDwell;

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    private static readonly DictationHudPaceState Held = new(DictationHudPace.Listening, DictationCopy.HudListeningName(latched: false));

    private static readonly DictationHudPaceState Latched = new(DictationHudPace.Listening, DictationCopy.HudListeningName(latched: true));

    private static readonly DictationHudPaceState Transcribing = new(DictationHudPace.Transcribing, DictationCopy.HudTranscribing);

    private static readonly DictationHudPaceState Typing = new(DictationHudPace.Typing, DictationCopy.HudTyping);

    private static readonly DictationHudPaceState Receipt = new(DictationHudPace.Ending, "Typed");

    [Fact]
    public void The_dwell_is_the_Macs_375_ms() => Assert.Equal(TimeSpan.FromMilliseconds(375), Dwell);

    [Fact]
    public void A_visible_state_cannot_be_replaced_inside_its_dwell()
    {
        var pacer = new DictationHudPacer();
        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Held, TimeSpan.Zero));

        var decision = pacer.Offer(Transcribing, Dwell - Seconds(0.1));

        Assert.False(decision.Show);
        Assert.Equal(Seconds(0.1), decision.RetryAfter);
        Assert.Equal(Held, pacer.Current);
        // After the dwell, the deferred state lands.
        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Transcribing, Dwell + Seconds(0.01)));
        Assert.Equal(Transcribing, pacer.Current);
    }

    [Fact]
    public void Hidden_is_always_immediate()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Held, TimeSpan.Zero);

        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(DictationHudPaceState.Hidden, Seconds(0.01)));
    }

    /// <summary>The Mac's "mic-changed pre-empts any dwell". Windows says a
    /// microphone change as an ending, which pre-empts as every ending does.</summary>
    [Fact]
    public void Mic_changed_pre_empts_any_dwell()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Held, TimeSpan.Zero);
        var warning = new DictationHudPaceState(DictationHudPace.Ending, "Mic changed — could you say that again?");

        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(warning, Seconds(0.01)));
    }

    [Fact]
    public void A_latch_inside_the_same_hold_is_immediate_not_a_state_change()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Held, TimeSpan.Zero);

        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Latched, Seconds(0.01)));
    }

    [Fact]
    public void An_outcome_pre_empts_any_dwell()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Transcribing, TimeSpan.Zero);

        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Receipt, Seconds(0.01)));
    }

    [Fact]
    public void A_new_hold_replaces_an_outcome_at_once()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Receipt, TimeSpan.Zero);

        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Held, Seconds(0.01)));
    }

    [Fact]
    public void A_fresh_state_after_a_hidden_interval_never_dwells()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Held, TimeSpan.Zero);
        pacer.Offer(DictationHudPaceState.Hidden, Seconds(0.1));

        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Transcribing, Seconds(0.2)));
    }

    // ─── Windows' own ───────────────────────────────────────────────────

    /// <summary>Windows' "Typing" is a state of its own: "Transcribing…"
    /// stands its dwell before "Typing" replaces it.</summary>
    [Fact]
    public void Typing_is_a_state_of_its_own_and_waits_for_Transcribing()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Transcribing, TimeSpan.Zero);

        var decision = pacer.Offer(Typing, Seconds(0.1));

        Assert.False(decision.Show);
        Assert.Equal(Dwell - Seconds(0.1), decision.RetryAfter);
        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Typing, Dwell));
    }

    /// <summary>The listening pill redrawn with each second's clock, or with
    /// a caption, is the same state: it neither waits nor restarts the dwell
    /// the next state is measured from.</summary>
    [Fact]
    public void The_same_state_offered_again_does_not_restart_the_dwell()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Held, TimeSpan.Zero);
        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Held, Seconds(0.3)));

        Assert.Equal(DictationHudPaceDecision.Now, pacer.Offer(Transcribing, Dwell));
    }

    /// <summary>An ending on screen waits its dwell before a busy pill that
    /// is not a new hold replaces it; only a new hold replaces it at once.</summary>
    [Fact]
    public void An_ending_stands_its_dwell_before_a_busy_pill_that_is_not_a_hold()
    {
        var pacer = new DictationHudPacer();
        pacer.Offer(Receipt, TimeSpan.Zero);

        Assert.False(pacer.Offer(Transcribing, Seconds(0.1)).Show);
        Assert.Equal(Receipt, pacer.Current);
    }
}

/// <summary>Mac suite "The reconcile heartbeat heals a panel the model and
/// the window disagree on".</summary>
public sealed class DictationHudReconcileTests
{
    [Fact]
    public void A_visible_state_the_panel_is_not_showing_is_offered_again() =>
        Assert.Equal(DictationHudReconcile.Action.Reoffer, DictationHudReconcile.For(desiredHidden: false, panelVisible: false));

    [Fact]
    public void A_panel_on_screen_for_a_hidden_model_is_taken_away() =>
        Assert.Equal(DictationHudReconcile.Action.Hide, DictationHudReconcile.For(desiredHidden: true, panelVisible: true));

    [Fact]
    public void Agreement_is_left_alone()
    {
        Assert.Null(DictationHudReconcile.For(desiredHidden: false, panelVisible: true));
        Assert.Null(DictationHudReconcile.For(desiredHidden: true, panelVisible: false));
    }

    /// <summary>The Mac's "the heartbeat outruns a user noticing". Windows
    /// has no fade, so the Mac's fade grace has no counterpart; a wedge that
    /// persists is logged once a minute, as on the Mac.</summary>
    [Fact]
    public void The_heartbeat_outruns_a_user_noticing()
    {
        Assert.True(DictationHudReconcile.Interval <= TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(1), DictationHudReconcile.Interval);
        Assert.Equal(TimeSpan.FromSeconds(60), DictationHudReconcile.ReportAgainAfter);
    }
}

/// <summary>The feed runs the pacer and the heartbeat: what reaches the
/// window, and when, on the controller's clock.</summary>
public sealed class IndicatorPacingTests
{
    private static readonly TimeSpan Dwell = DictationHudPacer.VisibleDwell;

    private static readonly DictationSessionResult Typed = new(DictationOutcome.Inserted, null, null);

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    private sealed class Feed : IDisposable
    {
        public FakeClock Clock { get; } = new();

        public FakeHud Hud { get; } = new();

        public Lines Log { get; } = new();

        public DictationHudFeed It { get; }

        public Feed() => It = new DictationHudFeed(Hud, () => Clock.Now, () => FakeHud.Hold, Log);

        public string[] Texts => [.. Hud.Shows.Select(m => m.Text)];

        public void Tick(TimeSpan after)
        {
            Clock.Advance(after);
            It.Tick();
        }

        public void Dispose() => It.Dispose();
    }

    /// <summary>The feed's log, kept as level and line.</summary>
    internal sealed class Lines : ILogger<DictationHudFeed>
    {
        public ConcurrentQueue<(LogLevel Level, string Line)> All { get; } = new();

        public string[] At(LogLevel level) => [.. All.Where(l => l.Level == level).Select(l => l.Line)];

        public string[] Heartbeats => [.. All.Select(l => l.Line).Where(l => l.StartsWith("Dictation indicator heartbeat", StringComparison.Ordinal))];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            All.Enqueue((logLevel, formatter(state, exception)));
    }

    // ─── Pacing ─────────────────────────────────────────────────────────

    /// <summary>The anti-flap rule's point: a quick release goes from
    /// "Listening" to its ending, without "Transcribing…" and "Typing"
    /// flashing for a frame each in between.</summary>
    [Fact]
    public void A_quick_release_goes_from_Listening_to_its_ending_with_nothing_flashed_between()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.Clock.Advance(TimeSpan.FromMilliseconds(80));
        feed.It.State(DictationState.Transcribing);
        feed.It.State(DictationState.Inserting);
        feed.It.State(DictationState.Armed);
        feed.It.Result(Typed, new DictationHudContext());
        feed.Tick(Dwell);

        Assert.Equal([DictationCopy.HudHeld, "Typed"], feed.Texts);
        Assert.Equal("Typed", feed.Hud.Current!.Text);
    }

    /// <summary>A busy pill held back lands on the tick its wait ends,
    /// announced as it would have been at once.</summary>
    [Fact]
    public void A_held_back_pill_lands_on_the_tick_its_wait_ends_and_is_announced_then()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.Clock.Advance(TimeSpan.FromMilliseconds(100));
        feed.It.State(DictationState.Transcribing);
        Assert.Equal(DictationCopy.HudHeld, feed.Hud.Current!.Text);
        Assert.Equal(DictationHudPace.Listening, feed.It.Pace.Kind);

        feed.Tick(Dwell - TimeSpan.FromMilliseconds(101));
        Assert.Equal(DictationCopy.HudHeld, feed.Hud.Current!.Text);

        feed.Tick(TimeSpan.FromMilliseconds(1));
        Assert.Equal(DictationCopy.HudTranscribing, feed.Hud.Current!.Text);
        Assert.True(feed.Hud.Announced[^1]);
        Assert.Equal(DictationHudPace.Transcribing, feed.It.Pace.Kind);
    }

    /// <summary>Only the newest offer waits: Typing offered while
    /// Transcribing waits replaces it, and lands alone.</summary>
    [Fact]
    public void Only_the_newest_held_back_pill_lands()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.It.State(DictationState.Transcribing);
        feed.It.State(DictationState.Inserting);
        feed.Tick(Dwell);

        Assert.Equal([DictationCopy.HudHeld, DictationCopy.HudTyping], feed.Texts);
    }

    /// <summary>A pipeline that goes back to rest with no result: the busy
    /// pill held back describes a state that is over and never lands, and
    /// the listening pill on screen goes after the grace.</summary>
    [Fact]
    public void A_held_back_pill_is_dropped_at_rest_and_the_one_on_screen_goes_after_the_grace()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.It.State(DictationState.Transcribing);
        feed.It.State(DictationState.Armed);

        feed.Tick(Dwell);
        Assert.Equal([DictationCopy.HudHeld], feed.Texts);
        feed.Tick(DictationHudFeed.RestGrace - Dwell);
        Assert.Null(feed.Hud.Current);
    }

    [Fact]
    public void An_ending_drops_a_held_back_pill()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.It.State(DictationState.Transcribing);
        feed.It.Result(new DictationSessionResult(DictationOutcome.NoSpeech, null, null), new DictationHudContext());

        feed.Tick(Dwell);
        Assert.DoesNotContain(DictationCopy.HudTranscribing, feed.Texts);
        Assert.Equal(DictationCopy.HudNothingHeard("Right Ctrl"), feed.Hud.Current!.Text);
    }

    [Fact]
    public void Pausing_drops_a_held_back_pill()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.It.State(DictationState.Transcribing);
        feed.It.Clear();

        feed.Tick(Dwell);
        Assert.Null(feed.Hud.Current);
        Assert.Equal([DictationCopy.HudHeld], feed.Texts);
    }

    /// <summary>The feed tells the pacer when the pill goes, so whatever
    /// comes up next comes up from hidden and never waits (the Mac's "a fresh
    /// state after a hidden interval never dwells", through the feed).</summary>
    [Fact]
    public void A_pill_after_the_indicator_went_never_waits()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.Clock.Advance(TimeSpan.FromMilliseconds(50));
        feed.It.Clear();
        feed.Clock.Advance(TimeSpan.FromMilliseconds(50));
        feed.It.State(DictationState.Transcribing);

        Assert.Equal(DictationCopy.HudTranscribing, feed.Hud.Current!.Text);
        Assert.Equal(DictationHudPace.Transcribing, feed.It.Pace.Kind);
    }

    /// <summary>The caption is dropped as soon as the hold stops listening,
    /// even while its listening pill still stands its dwell.</summary>
    [Fact]
    public void A_caption_for_a_hold_whose_Transcribing_waits_is_dropped()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.It.Caption("one two");
        Assert.Equal("one two", feed.Hud.Current!.Caption);
        feed.It.State(DictationState.Transcribing);
        Assert.Equal(DictationHudPace.Listening, feed.It.Pace.Kind);
        feed.It.Caption("one two three");

        Assert.DoesNotContain(feed.Hud.Shows, m => m.Caption == "one two three");
        feed.Tick(Dwell);
        Assert.Equal(DictationCopy.HudTranscribing, feed.Hud.Current!.Text);
    }

    /// <summary>The other way round: a press soon after a dictation went
    /// back to rest has its listening pill held behind the "Transcribing…"
    /// still standing its 375 ms. A caption then goes on that waiting
    /// listening pill, never on the busy pill on screen, so no pill ever
    /// shows the words under "Transcribing…".</summary>
    [Fact]
    public void A_caption_for_a_hold_whose_Listening_waits_goes_on_the_listening_pill()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        feed.Tick(Dwell);
        feed.It.State(DictationState.Transcribing);
        Assert.Equal(DictationCopy.HudTranscribing, feed.Hud.Current!.Text);
        feed.It.State(DictationState.Armed);

        feed.Clock.Advance(TimeSpan.FromMilliseconds(100));
        feed.It.State(DictationState.Listening);
        Assert.Equal(DictationCopy.HudTranscribing, feed.Hud.Current!.Text);
        feed.It.Caption("one two");
        feed.Tick(Dwell);

        Assert.Equal(DictationCopy.HudHeld, feed.Hud.Current!.Text);
        Assert.Equal("one two", feed.Hud.Current!.Caption);
        Assert.All(feed.Hud.Shows.Where(m => m.Caption is not null), m => Assert.Equal(DictationCopy.HudHeld, m.Text));
    }

    /// <summary>Through the controller the app runs: a press released at
    /// once shows the held pill and then the receipt, nothing between.</summary>
    [Fact]
    public async Task A_dictation_released_at_once_shows_Listening_then_its_receipt()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Dictate(Inserted);
        rig.Controller.Tick();

        Assert.Equal([DictationCopy.HudHeld, "Typed"], rig.Hud.Shows.Select(m => m.Text).ToArray());
    }

    // ─── The heartbeat ──────────────────────────────────────────────────

    /// <summary>A pill the window lost without the feed knowing comes back
    /// within a second, drawn again without being spoken a second time.</summary>
    [Fact]
    public void A_pill_the_window_lost_is_drawn_again_within_a_second_and_not_spoken_again()
    {
        using var feed = new Feed();
        feed.It.Tick();
        feed.It.RecognizerCouldNotRestart();
        var pill = feed.Hud.Current!;
        feed.Hud.Vanish();

        feed.Tick(TimeSpan.FromMilliseconds(500));
        Assert.Null(feed.Hud.Current);
        feed.Tick(TimeSpan.FromMilliseconds(500));

        Assert.Same(pill, feed.Hud.Current);
        Assert.Equal([true, false], feed.Hud.Announced);
        var line = Assert.Single(feed.Log.Heartbeats);
        Assert.Equal("Dictation indicator heartbeat: Reoffer, wanted Ending, window showing: False", line);
    }

    /// <summary>The listening pill comes back with its bars, at the level
    /// the controller last reported.</summary>
    [Fact]
    public void A_lost_listening_pill_comes_back_with_its_bars()
    {
        using var feed = new Feed();
        feed.It.Tick();
        feed.It.State(DictationState.Listening);
        feed.It.Level(0.5f);
        feed.Hud.Vanish();

        feed.Tick(DictationHudReconcile.Interval);

        Assert.Equal(DictationCopy.HudHeld, feed.Hud.Current!.Text);
        Assert.True(feed.Hud.Bars);
        Assert.Equal(0.5f, feed.Hud.Levels[^1]);
        Assert.All(feed.Hud.Announced, Assert.False);
    }

    /// <summary>A pill the window kept after the feed took it down is taken
    /// away within a second: the Mac's <c>orderOut</c>.</summary>
    [Fact]
    public void A_pill_the_window_kept_after_the_feed_hid_it_is_taken_away()
    {
        using var feed = new Feed();
        feed.It.Result(Typed, new DictationHudContext());
        var receipt = feed.Hud.Current!;
        feed.Tick(DictationHudModel.ReceiptDwell);
        Assert.Null(feed.Hud.Current);
        Assert.Equal(1, feed.Hud.Hides);

        feed.Hud.Stray(receipt);
        feed.Tick(DictationHudReconcile.Interval);

        Assert.Null(feed.Hud.Current);
        Assert.Equal(2, feed.Hud.Hides);
        Assert.Equal("Dictation indicator heartbeat: Hide, wanted Hidden, window showing: True", Assert.Single(feed.Log.Heartbeats));
    }

    /// <summary>The way a window keeps a pill in practice: the hide threw
    /// and the window stayed up (the window reads its own visibility, not
    /// the pill it was last given, so it says so). The heartbeat hides it
    /// again once the window can.</summary>
    [Fact]
    public void A_hide_that_did_not_take_is_tried_again_by_the_heartbeat()
    {
        using var feed = new Feed();
        feed.It.Tick();
        feed.It.Result(Typed, new DictationHudContext());
        feed.Hud.Throws = true;
        feed.Tick(DictationHudModel.ReceiptDwell);
        Assert.False(feed.It.Shown.IsVisible);
        Assert.NotNull(feed.Hud.Current);

        feed.Hud.Throws = false;
        feed.Tick(DictationHudReconcile.Interval);

        Assert.Null(feed.Hud.Current);
        Assert.Equal(1, feed.Hud.Hides);
        Assert.Equal("Dictation indicator heartbeat: Hide, wanted Hidden, window showing: True", Assert.Single(feed.Log.Heartbeats));
    }

    /// <summary>A disposed feed leaves the window alone: no heal, no hide,
    /// not even of a pill the window kept.</summary>
    [Fact]
    public void A_disposed_feed_heals_nothing()
    {
        using var feed = new Feed();
        feed.It.Result(Typed, new DictationHudContext());
        var receipt = feed.Hud.Current!;
        feed.It.Dispose();
        var shows = feed.Hud.Shows.Count;
        var hides = feed.Hud.Hides;

        feed.Hud.Stray(receipt);
        feed.Tick(DictationHudReconcile.Interval);

        Assert.Equal(shows, feed.Hud.Shows.Count);
        Assert.Equal(hides, feed.Hud.Hides);
        Assert.Empty(feed.Log.Heartbeats);
    }

    /// <summary>A heal whose draw fails draws no level on the pill it could
    /// not put up: a failed level draw would end the bars for the rest of
    /// the hold. When the window draws again, the listening pill is back
    /// with its bars.</summary>
    [Fact]
    public void A_heal_that_cannot_draw_draws_no_level_and_the_bars_come_back_with_the_pill()
    {
        using var feed = new Feed();
        feed.It.Tick();
        feed.It.State(DictationState.Listening);
        feed.It.Level(0.5f);
        var attempts = feed.Hud.LevelAttempts;
        var warnings = feed.Log.At(LogLevel.Warning).Length;
        feed.Hud.Vanish();
        feed.Hud.Throws = true;

        feed.Tick(DictationHudReconcile.Interval);
        Assert.Equal(attempts, feed.Hud.LevelAttempts);
        Assert.Equal(warnings, feed.Log.At(LogLevel.Warning).Length);

        feed.Hud.Throws = false;
        feed.Tick(DictationHudReconcile.Interval);
        Assert.Equal(DictationCopy.HudHeld, feed.Hud.Current!.Text);
        Assert.True(feed.Hud.Bars);
        Assert.Equal(0.5f, feed.Hud.Levels[^1]);
    }

    /// <summary>A window that throws when asked whether it is showing never
    /// stops the tick: the heartbeat logs it below the log's level and
    /// leaves the window alone.</summary>
    [Fact]
    public void A_window_that_cannot_say_whether_it_is_showing_never_stops_the_tick()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        var shows = feed.Hud.Shows.Count;
        feed.Hud.IsShowingThrows = true;

        feed.Tick(DictationHudReconcile.Interval);

        Assert.Equal(shows, feed.Hud.Shows.Count);
        Assert.Empty(feed.Log.Heartbeats);
        Assert.Contains("The dictation indicator could not say whether it is showing", feed.Log.At(LogLevel.Debug));
        Assert.Empty(feed.Log.At(LogLevel.Warning));
    }

    /// <summary>The same through the controller: the feed's tick runs
    /// first in the controller's, before the hold key is read, so a window
    /// that cannot answer must not cost the press.</summary>
    [Fact]
    public async Task A_window_that_cannot_say_whether_it_is_showing_never_costs_a_press()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Hud.IsShowingThrows = true;
        rig.Clock.Advance(DictationHudReconcile.Interval);

        rig.Press();
        rig.Dispatcher.Drain();

        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);
    }

    /// <summary>One line per wedge, not one per heartbeat: a window that
    /// keeps losing the pill is logged once a minute.</summary>
    [Fact]
    public void A_wedge_that_persists_is_logged_once_a_minute()
    {
        using var feed = new Feed();
        feed.It.Tick();
        feed.It.State(DictationState.Listening);
        // Healed at 1 s, and again each second to 60 s: one line.
        for (var second = 1; second <= 60; second++)
        {
            feed.Hud.Vanish();
            feed.Tick(DictationHudReconcile.Interval);
            Assert.NotNull(feed.Hud.Current);
        }

        Assert.Single(feed.Log.Heartbeats);
        // A minute after the first line: the second.
        feed.Hud.Vanish();
        feed.Tick(DictationHudReconcile.Interval);
        Assert.Equal(2, feed.Log.Heartbeats.Length);
    }

    /// <summary>Agreement leaves the window alone: no redraw, no line.</summary>
    [Fact]
    public void A_heartbeat_that_finds_agreement_draws_nothing()
    {
        using var feed = new Feed();
        feed.It.State(DictationState.Listening);
        var shows = feed.Hud.Shows.Count;

        for (var second = 0; second < 5; second++)
        {
            feed.Tick(DictationHudReconcile.Interval);
        }

        Assert.Equal(shows, feed.Hud.Shows.Count);
        Assert.Empty(feed.Log.Heartbeats);
    }

    /// <summary>A window that throws on every draw is warned about once, by
    /// the draw that failed; the heartbeat's retries each second stay below
    /// the log's level.</summary>
    [Fact]
    public void A_window_that_cannot_draw_is_warned_about_once_not_once_a_second()
    {
        using var feed = new Feed();
        feed.It.Tick();
        feed.Hud.Throws = true;
        feed.It.State(DictationState.Listening);
        var warned = feed.Log.At(LogLevel.Warning).Length;
        Assert.True(warned > 0);
        for (var second = 0; second < 5; second++)
        {
            feed.Tick(DictationHudReconcile.Interval);
        }

        Assert.Equal(warned, feed.Log.At(LogLevel.Warning).Length);
        Assert.Single(feed.Log.Heartbeats);
        Assert.Equal(5, feed.Log.At(LogLevel.Debug).Length);
    }

    /// <summary>The window's half, pinned on its source (WPF cannot run
    /// here): it is showing exactly when the window is visible and has been
    /// revealed, so a pill whose reveal never ran reads as lost, and a window
    /// still up after a hide that did not take reads as showing, whatever
    /// pill it last had (the hide clears that first); and a show on a window
    /// still transparent reveals it again.</summary>
    [Fact]
    public void The_window_is_showing_only_when_visible_and_revealed_and_a_show_reveals_a_transparent_one()
    {
        var code = File.ReadAllText(DictationHudGeometryTests.AppFile("DictationHudWindow.xaml.cs"));

        Assert.Contains(
            "bool IDictationHud.IsShowing => !closed && IsVisible && Opacity > 0;",
            code, StringComparison.Ordinal);
        var show = DictationHudGeometryTests.Member(code, "void IDictationHud.Show(").ReplaceLineEndings("\n");
        var again = show.IndexOf("else if (Opacity < 1)", StringComparison.Ordinal);
        Assert.True(again >= 0, "a show on a transparent window reveals it");
        Assert.True(show.IndexOf("RevealWhenLaidOut();", again, StringComparison.Ordinal) > again);
    }

    /// <summary>Through the controller the app runs: its tick drives the
    /// heartbeat, so a listening pill the window lost mid-hold is back
    /// within a second.</summary>
    [Fact]
    public async Task The_controllers_tick_heals_a_pill_lost_mid_hold()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);

        rig.Hud.Vanish();
        rig.Clock.Advance(DictationHudReconcile.Interval);
        rig.Controller.Tick();

        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);
    }
}
