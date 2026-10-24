using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The hook watch's decisions, with no controller: when a disagreement
/// between Windows and the hook is acted on and how (<see cref="DictationHookWatch"/>),
/// the Mac's edge-not-level rule for bringing the window forward
/// (<c>DictationWindowAttention</c>, macos/Tests/DictationAppCoreTests/MenuGlyphTests.swift
/// "The window comes forward when something needs the user"), the
/// administrator-window check and its cache (<see cref="AdminWindowWatch"/>),
/// and what the icon says (<see cref="DictationTrayGlyph"/>). The controller
/// acting on them is <see cref="HookWatchControllerTests"/>.
/// </summary>
public sealed class HookWatchTests
{
    private static readonly HotkeyBinding Key = DictationHotkeys.PushToTalk;

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>The key down for Windows and up for the hook: a press the
    /// hook never saw.</summary>
    private static HookReading MissedPress(long events = 0) => new(KeyDown: true, HookSawKeyDown: false, events);

    private static HookReading Agree(bool down = false, long events = 0) => new(down, down, events);

    /// <summary>Observe a disagreement from <paramref name="at"/> until it is
    /// acted on, at the recheck cadence; returns the acting verdict.</summary>
    private static HookVerdict Confirm(
        DictationHookWatch watch, ref int at, HookReading reading, WindowInFront window = WindowInFront.Ordinary)
    {
        var first = watch.Observe(Ms(at), reading, window);
        Assert.Equal(HookFinding.Suspected, first.Finding);
        while (true)
        {
            at += 100;
            var verdict = watch.Observe(Ms(at), reading, window);
            if (verdict.Finding != HookFinding.None)
            {
                return verdict;
            }
        }
    }

    // ─── Sampling ───────────────────────────────────────────────────────

    [Fact]
    public void Agreement_is_sampled_every_half_second_and_a_disagreement_every_tenth()
    {
        var watch = new DictationHookWatch();
        Assert.True(watch.Due(Ms(0)));

        Assert.Equal(HookFinding.None, watch.Observe(Ms(0), Agree(), WindowInFront.Ordinary).Finding);
        Assert.False(watch.Due(Ms(499)));
        Assert.True(watch.Due(Ms(500)));

        Assert.Equal(HookFinding.Suspected, watch.Observe(Ms(500), MissedPress(), WindowInFront.Ordinary).Finding);
        Assert.False(watch.Due(Ms(599)));
        Assert.True(watch.Due(Ms(600)));
    }

    [Fact]
    public void Agreement_up_or_down_is_never_acted_on()
    {
        var watch = new DictationHookWatch();
        for (var at = 0; at < 10_000; at += 500)
        {
            Assert.Equal(HookFinding.None, watch.Observe(Ms(at), Agree(down: at % 1_000 == 0), WindowInFront.Ordinary).Finding);
        }

        Assert.Equal(HookHealth.Healthy, watch.Health);
    }

    // ─── What a lasting disagreement means ──────────────────────────────

    [Fact]
    public void A_disagreement_is_suspected_first_with_a_test_key_and_acted_on_after_300_ms()
    {
        var watch = new DictationHookWatch();
        var suspected = watch.Observe(Ms(1_000), MissedPress(), WindowInFront.Ordinary);
        Assert.Equal(new HookVerdict(HookFinding.Suspected, KeyDown: true, Probe: true), suspected);

        Assert.Equal(HookFinding.None, watch.Observe(Ms(1_100), MissedPress(), WindowInFront.Ordinary).Finding);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(1_299), MissedPress(), WindowInFront.Ordinary).Finding);
        var lost = watch.Observe(Ms(1_300), MissedPress(), WindowInFront.Ordinary);

        Assert.Equal(HookFinding.Lost, lost.Finding);
        Assert.True(lost.KeyDown);
        Assert.Equal(Ms(300), lost.Silent);
        Assert.True(lost.Reveal);
    }

    [Fact]
    public void A_hook_that_received_the_test_key_missed_one_edge_and_is_not_lost()
    {
        var watch = new DictationHookWatch();
        var at = 0;
        var events = 10L;
        Assert.Equal(HookFinding.Suspected, watch.Observe(Ms(at), MissedPress(events), WindowInFront.Ordinary).Finding);

        // The test key's down and up arrived.
        events += 2;
        at += 300;
        var verdict = watch.Observe(Ms(at), MissedPress(events), WindowInFront.Ordinary);

        Assert.Equal(HookFinding.EdgeMissed, verdict.Finding);
        Assert.False(verdict.Reveal);
        Assert.Equal(HookHealth.Healthy, watch.Health);
    }

    [Fact]
    public void A_missed_release_is_acted_on_the_same_way_with_the_key_up()
    {
        var watch = new DictationHookWatch();
        var at = 0;
        var verdict = Confirm(watch, ref at, new HookReading(KeyDown: false, HookSawKeyDown: true, 4));

        Assert.Equal(HookFinding.Lost, verdict.Finding);
        Assert.False(verdict.KeyDown);
    }

    /// <summary>A short press with a live hook: the test key arrives, the
    /// two sides agree again, nothing is done, and the watch goes back to its
    /// resting cadence.</summary>
    [Fact]
    public void A_disagreement_that_ends_before_300_ms_with_the_test_key_received_is_not_acted_on()
    {
        var watch = new DictationHookWatch();
        Assert.Equal(HookFinding.Suspected, watch.Observe(Ms(0), MissedPress(events: 4), WindowInFront.Ordinary).Finding);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(200), Agree(down: true, events: 7), WindowInFront.Ordinary).Finding);
        Assert.True(watch.Suspecting, "the test key is still out");
        Assert.True(watch.Due(Ms(300)));

        Assert.Equal(new HookVerdict(HookFinding.None, KeyDown: true), watch.Observe(Ms(300), Agree(down: true, events: 7), WindowInFront.Ordinary));
        Assert.False(watch.Suspecting);
        Assert.False(watch.Due(Ms(799)));
        Assert.Equal(HookHealth.Healthy, watch.Health);
    }

    /// <summary>Reviewer finding: the stop tap of a latch is a press shorter
    /// than 300 ms, and a dead hook used to go unnoticed because the two sides
    /// agreed again before the answer was read. The test key's answer is read
    /// whatever the key does meanwhile.</summary>
    [Fact]
    public void A_test_key_that_never_arrives_is_a_loss_even_when_the_press_was_over_first()
    {
        var watch = new DictationHookWatch();
        Assert.True(watch.Observe(Ms(0), MissedPress(events: 4), WindowInFront.Ordinary).Probe);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(150), Agree(events: 4), WindowInFront.Ordinary).Finding);

        var lost = watch.Observe(Ms(300), Agree(events: 4), WindowInFront.Ordinary);

        Assert.Equal(HookFinding.Lost, lost.Finding);
        Assert.False(lost.KeyDown);
        Assert.True(lost.MissedPress);
        Assert.True(lost.Reveal);
        Assert.Equal(Ms(300), lost.Silent);
    }

    [Fact]
    public void A_disagreement_that_turns_the_other_way_starts_its_clock_again()
    {
        var watch = new DictationHookWatch();
        var released = new HookReading(KeyDown: false, HookSawKeyDown: true, 0);
        Assert.True(watch.Observe(Ms(0), released, WindowInFront.Ordinary).Probe);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(100), released with { HookEvents = 2 }, WindowInFront.Ordinary).Finding);

        // The key goes down: a new disagreement, no second test key.
        var flipped = watch.Observe(Ms(250), MissedPress(events: 2), WindowInFront.Ordinary);
        Assert.Equal(new HookVerdict(HookFinding.Suspected, KeyDown: true), flipped);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(500), MissedPress(events: 2), WindowInFront.Ordinary).Finding);
        Assert.Equal(HookFinding.EdgeMissed, watch.Observe(Ms(550), MissedPress(events: 2), WindowInFront.Ordinary).Finding);
    }

    /// <summary>Reviewer finding: a disagreement that began under an
    /// administrator window sent no test key, and an ordinary window in front
    /// before the confirming reading made the silence read as a loss. The
    /// ordinary window gets its test key, and only that key's answer
    /// decides.</summary>
    [Fact]
    public void A_disagreement_begun_under_an_administrator_window_gets_its_test_key_from_the_next_ordinary_one()
    {
        var released = new HookReading(KeyDown: false, HookSawKeyDown: true, 3);
        var watch = new DictationHookWatch();
        Assert.Equal(
            new HookVerdict(HookFinding.Suspected, KeyDown: false),
            watch.Observe(Ms(0), released, WindowInFront.Administrator));

        Assert.Equal(
            new HookVerdict(HookFinding.Suspected, KeyDown: false, Probe: true),
            watch.Observe(Ms(100), released, WindowInFront.Ordinary));
        Assert.Equal(HookFinding.None, watch.Observe(Ms(200), released, WindowInFront.Ordinary).Finding);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(300), released, WindowInFront.Ordinary).Finding);

        // The live hook counted it: a missed release, put right once the key
        // has been quiet long enough; never a loss.
        var answered = released with { HookEvents = 5 };
        for (var at = 400; at < 1_200; at += 100)
        {
            Assert.Equal(HookFinding.None, watch.Observe(Ms(at), answered, WindowInFront.Ordinary).Finding);
        }

        Assert.Equal(HookFinding.EdgeMissed, watch.Observe(Ms(1_200), answered, WindowInFront.Ordinary).Finding);

        // And a dead one is lost only once the test key it never counted is due.
        var dead = new DictationHookWatch();
        dead.Observe(Ms(0), released, WindowInFront.Administrator);
        Assert.True(dead.Observe(Ms(100), released, WindowInFront.Ordinary).Probe);
        Assert.Equal(HookFinding.None, dead.Observe(Ms(300), released, WindowInFront.Ordinary).Finding);
        Assert.Equal(HookFinding.Lost, dead.Observe(Ms(400), released, WindowInFront.Ordinary).Finding);
    }

    /// <summary>Reviewer finding: <c>ProbeHook</c> returning false (Windows
    /// refused the injection) was thrown away, and the silence then read as a
    /// loss. A refused test key proves nothing: the view is put right, nothing
    /// is reinstalled, and no second key is asked for in that
    /// disagreement.</summary>
    [Fact]
    public void A_refused_test_key_proves_nothing_and_ends_in_a_quiet_correction()
    {
        var watch = new DictationHookWatch();
        Assert.True(watch.Observe(Ms(0), MissedPress(), WindowInFront.Ordinary).Probe);
        watch.TestKeyRefused();

        Assert.Equal(new HookVerdict(HookFinding.None, KeyDown: true), watch.Observe(Ms(100), MissedPress(), WindowInFront.Ordinary));
        Assert.Equal(HookFinding.None, watch.Observe(Ms(200), MissedPress(), WindowInFront.Ordinary).Finding);
        var verdict = watch.Observe(Ms(300), MissedPress(), WindowInFront.Ordinary);

        Assert.Equal(HookFinding.Unproven, verdict.Finding);
        Assert.True(verdict.KeyDown);
        Assert.False(verdict.Reveal);
        Assert.Equal(HookHealth.Healthy, watch.Health);

        // The next disagreement asks again, and an answered one is a loss.
        var at = 1_000;
        var next = Confirm(watch, ref at, MissedPress());
        Assert.Equal(HookFinding.Lost, next.Finding);
        Assert.True(next.Reveal);
    }

    /// <summary>A missed release replayed while the key was still held cut a
    /// hold short. With another program's hook after this one that swallows
    /// the hold key, Windows' key state never sees it go down while this hook
    /// does, and its autorepeats keep arriving: held, not released.</summary>
    [Fact]
    public void A_key_whose_autorepeats_keep_arriving_while_Windows_says_up_is_held_back_not_released()
    {
        var watch = new DictationHookWatch();
        var held = new HookReading(KeyDown: false, HookSawKeyDown: true, HookEvents: 10, TriggerEvents: 1);
        Assert.True(watch.Observe(Ms(0), held, WindowInFront.Ordinary).Probe);
        // The test key arrives; the repeat delay (here the slowest, 1 s) has not run out.
        held = held with { HookEvents = 12 };
        for (var at = 100; at < 1_000; at += 100)
        {
            Assert.Equal(HookFinding.None, watch.Observe(Ms(at), held, WindowInFront.Ordinary).Finding);
        }

        // The first autorepeat: said once.
        held = held with { HookEvents = 13, TriggerEvents = 2 };
        Assert.Equal(HookFinding.HeldBack, watch.Observe(Ms(1_000), held, WindowInFront.Ordinary).Finding);

        // Held for ten seconds more, repeating: nothing.
        for (var at = 1_100; at < 11_000; at += 100)
        {
            held = held with { HookEvents = held.HookEvents + 3, TriggerEvents = held.TriggerEvents + 3 };
            Assert.Equal(HookFinding.None, watch.Observe(Ms(at), held, WindowInFront.Ordinary).Finding);
        }

        // Let go: the hook sees it, and the two sides agree.
        Assert.Equal(HookFinding.None, watch.Observe(Ms(11_000), Agree(events: 999), WindowInFront.Ordinary).Finding);
        Assert.False(watch.Suspecting);
    }

    [Fact]
    public void A_missed_release_with_the_key_quiet_is_replayed_after_1200_ms_of_quiet()
    {
        var watch = new DictationHookWatch();
        var released = new HookReading(KeyDown: false, HookSawKeyDown: true, HookEvents: 10, TriggerEvents: 4);
        Assert.True(watch.Observe(Ms(0), released, WindowInFront.Ordinary).Probe);
        released = released with { HookEvents = 12 };
        for (var at = 100; at < 1_200; at += 100)
        {
            Assert.Equal(HookFinding.None, watch.Observe(Ms(at), released, WindowInFront.Ordinary).Finding);
        }

        var verdict = watch.Observe(Ms(1_200), released, WindowInFront.Ordinary);
        Assert.Equal(HookFinding.EdgeMissed, verdict.Finding);
        Assert.False(verdict.KeyDown);
        Assert.Equal(Ms(1_200), verdict.Silent);
    }

    [Fact]
    public void With_an_administrator_window_in_front_there_is_no_test_key_and_no_loss()
    {
        var watch = new DictationHookWatch();
        var suspected = watch.Observe(Ms(0), MissedPress(), WindowInFront.Administrator);
        Assert.False(suspected.Probe);

        var verdict = watch.Observe(Ms(300), MissedPress(), WindowInFront.Administrator);

        Assert.Equal(HookFinding.AdminWindow, verdict.Finding);
        Assert.True(verdict.KeyDown);
        Assert.False(verdict.Reveal);
        // Not a loss, so the next real one still brings the window forward.
        var at = 1_000;
        Assert.True(Confirm(watch, ref at, MissedPress()).Reveal);
    }

    /// <summary>A secure desktop (Ctrl+Alt+Del, a UAC prompt, the lock
    /// screen): Windows' key state reads up and no key reaches the hook, the
    /// test key included, so a release made there would read as a lost hook.
    /// The watch waits for a window, then starts again with a test key.</summary>
    [Fact]
    public void With_no_window_in_front_a_disagreement_waits_for_one_and_is_never_a_loss()
    {
        var watch = new DictationHookWatch();
        var released = new HookReading(KeyDown: false, HookSawKeyDown: true, 3);

        Assert.Equal(new HookVerdict(HookFinding.None, KeyDown: false), watch.Observe(Ms(0), released, WindowInFront.None));
        for (var at = 100; at <= 5_000; at += 100)
        {
            Assert.Equal(HookFinding.None, watch.Observe(Ms(at), released, WindowInFront.None).Finding);
            Assert.False(watch.Suspecting);
        }

        var suspected = watch.Observe(Ms(5_100), released, WindowInFront.Ordinary);
        Assert.Equal(HookFinding.Suspected, suspected.Finding);
        Assert.True(suspected.Probe);
        Assert.True(watch.Suspecting);
        // The live hook received the test key: a missed release, replayed
        // once the key has been quiet past the longest repeat delay.
        Assert.Equal(
            HookFinding.None,
            watch.Observe(Ms(5_400), released with { HookEvents = 5 }, WindowInFront.Ordinary).Finding);
        Assert.Equal(
            HookFinding.EdgeMissed,
            watch.Observe(Ms(6_300), released with { HookEvents = 5 }, WindowInFront.Ordinary).Finding);
        Assert.False(watch.Suspecting);
    }

    [Fact]
    public void A_window_that_goes_during_a_suspicion_ends_it()
    {
        var watch = new DictationHookWatch();
        Assert.Equal(HookFinding.Suspected, watch.Observe(Ms(0), MissedPress(), WindowInFront.Ordinary).Finding);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(300), MissedPress(), WindowInFront.None).Finding);
        Assert.False(watch.Suspecting);
    }

    /// <summary>A window whose process could not be asked might be above
    /// Dictation, where the hook and the test key are both cut off: nothing
    /// can be told there, as with no window.</summary>
    [Fact]
    public void A_window_whose_elevation_cannot_be_told_gets_no_test_key_and_is_never_a_loss()
    {
        var watch = new DictationHookWatch();
        for (var at = 0; at <= 5_000; at += 100)
        {
            var verdict = watch.Observe(Ms(at), MissedPress(), WindowInFront.Unknown);
            Assert.Equal(HookFinding.None, verdict.Finding);
            Assert.False(verdict.Probe);
        }

        // A test key out when such a window comes forward proves nothing either.
        Assert.True(watch.Observe(Ms(5_100), MissedPress(), WindowInFront.Ordinary).Probe);
        Assert.Equal(HookFinding.None, watch.Observe(Ms(5_400), Agree(), WindowInFront.Unknown).Finding);
        Assert.False(watch.Suspecting);
    }

    // ─── Edges, not levels: the Mac's DictationWindowAttention ──────────

    /// <summary>Mac <c>degradedListener</c>: "a refused key listener
    /// (windowAttention) brings the window forward", "one bump, one request".</summary>
    [Fact]
    public void A_lost_hook_brings_the_window_forward_once()
    {
        var watch = new DictationHookWatch();
        var at = 0;
        Assert.True(Confirm(watch, ref at, MissedPress()).Reveal);
        watch.Repaired(reinstalled: false, hookEvents: 0);

        // Still gone, still pressed: acted on again, not asked again.
        at += 500;
        var again = Confirm(watch, ref at, MissedPress());
        Assert.Equal(HookFinding.Lost, again.Finding);
        Assert.False(again.Reveal);
    }

    /// <summary>Mac <c>revokedGrant</c>: "a grant revoked while enabled
    /// brings the window forward once, and again after a re-grant and a
    /// second loss". Here the re-grant is a reinstalled hook receiving a key.</summary>
    [Fact]
    public void A_hook_lost_again_after_the_new_one_heard_a_key_asks_again()
    {
        var watch = new DictationHookWatch();
        var at = 0;
        Assert.True(Confirm(watch, ref at, MissedPress(events: 5)).Reveal);
        watch.Repaired(reinstalled: true, hookEvents: 5);
        Assert.Equal(HookHealth.Reconnected, watch.Health);

        // Lost again before the new hook heard anything: not asked again.
        at += 500;
        Assert.False(Confirm(watch, ref at, MissedPress(events: 5)).Reveal);
        watch.Repaired(reinstalled: true, hookEvents: 5);

        // It hears a key: healed, once.
        at += 500;
        Assert.True(watch.Observe(Ms(at), Agree(events: 7), WindowInFront.Ordinary).Healed);
        Assert.Equal(HookHealth.Healthy, watch.Health);
        at += 500;
        Assert.False(watch.Observe(Ms(at), Agree(events: 9), WindowInFront.Ordinary).Healed);

        // And a second loss asks again.
        at += 500;
        Assert.True(Confirm(watch, ref at, MissedPress(events: 9)).Reveal);
    }

    /// <summary>Mac <c>firstRunIsNotALoss</c>: "a first run that starts
    /// without grants is not a loss". Here: a key already down when the hook
    /// was installed, which the live hook never saw go down, is one missed
    /// edge, not a lost hook.</summary>
    [Fact]
    public void A_key_already_down_when_the_hook_was_installed_is_not_a_loss()
    {
        var watch = new DictationHookWatch();
        Assert.True(watch.Observe(Ms(0), MissedPress(events: 0), WindowInFront.Ordinary).Probe);
        var verdict = watch.Observe(Ms(300), MissedPress(events: 2), WindowInFront.Ordinary);

        Assert.Equal(HookFinding.EdgeMissed, verdict.Finding);
        Assert.False(verdict.Reveal);
    }

    [Fact]
    public void A_hook_Windows_would_not_put_back_cannot_be_heard_until_one_is()
    {
        var watch = new DictationHookWatch();
        var at = 0;
        Confirm(watch, ref at, MissedPress());
        watch.Repaired(reinstalled: false, hookEvents: 0);
        Assert.Equal(HookHealth.Unheard, watch.Health);
        // No hook, no keys: no healing.
        Assert.False(watch.Observe(Ms(at + 500), Agree(), WindowInFront.Ordinary).Healed);

        at += 1_000;
        Confirm(watch, ref at, MissedPress());
        watch.Repaired(reinstalled: true, hookEvents: 0);
        Assert.Equal(HookHealth.Reconnected, watch.Health);
    }

    // ─── The window in front ────────────────────────────────────────────

    [Fact]
    public void An_elevated_window_in_front_is_reported_on_its_edges_only()
    {
        var foreground = new FakeForeground { Process = 100 };
        foreground.Elevated.Add(200);
        var watch = new AdminWindowWatch(foreground);

        Assert.False(watch.Check(Ms(0)));
        Assert.False(watch.InFront);

        foreground.Process = 200;
        Assert.True(watch.Check(Ms(500)));
        Assert.True(watch.InFront);
        Assert.False(watch.Check(Ms(1_000)));

        foreground.Process = 100;
        Assert.True(watch.Check(Ms(1_500)));
        Assert.False(watch.InFront);
    }

    [Fact]
    public void The_window_in_front_is_looked_at_every_half_second_and_each_process_asked_once_while_in_front()
    {
        var foreground = new FakeForeground { Process = 100 };
        var watch = new AdminWindowWatch(foreground);
        Assert.True(watch.Due(Ms(0)));
        watch.Check(Ms(0));
        Assert.False(watch.Due(Ms(499)));
        Assert.True(watch.Due(Ms(500)));

        for (var at = 500; at <= 5_000; at += 500)
        {
            watch.Check(Ms(at));
        }

        Assert.Equal(new[] { 100 }, foreground.Queries);

        // Another process, then the first again: asked again, so a process id
        // reused meanwhile is never answered from the cache.
        foreground.Process = 300;
        watch.Check(Ms(5_500));
        foreground.Process = 100;
        watch.Check(Ms(6_000));
        Assert.Equal(new[] { 100, 300, 100 }, foreground.Queries);
    }

    [Fact]
    public void No_window_in_front_is_no_administrator_window_and_forgets_the_last_answer()
    {
        var foreground = new FakeForeground { Process = 200 };
        foreground.Elevated.Add(200);
        var watch = new AdminWindowWatch(foreground);
        watch.Check(Ms(0));
        Assert.True(watch.InFront);

        foreground.Process = 0;
        Assert.True(watch.Check(Ms(500)));
        Assert.False(watch.InFront);
        Assert.Equal(WindowInFront.None, watch.Kind);
        Assert.Single(foreground.Queries);

        foreground.Process = 200;
        watch.Check(Ms(1_000));
        Assert.True(watch.InFront);
        Assert.Equal(new[] { 200, 200 }, foreground.Queries);
    }

    [Fact]
    public void Dictation_running_elevated_itself_never_reports_one_and_never_asks()
    {
        var foreground = new FakeForeground { Process = 200, ThisProcessElevated = true };
        foreground.Elevated.Add(200);
        var watch = new AdminWindowWatch(foreground);

        Assert.False(watch.Check(Ms(0)));
        Assert.False(watch.InFront);
        Assert.Equal(WindowInFront.Ordinary, watch.Kind);
        Assert.Empty(foreground.Queries);
        foreground.Process = 0;
        watch.Check(Ms(500));
        Assert.Equal(WindowInFront.None, watch.Kind);
    }

    /// <summary>Not named in the hint, which says what is known, and not
    /// called ordinary either, which would make every press there a lost
    /// hook (reviewer finding): <see cref="WindowInFront.Unknown"/>, asked once
    /// while it stays in front.</summary>
    [Fact]
    public void A_process_whose_elevation_cannot_be_told_is_unknown_not_ordinary()
    {
        var foreground = new FakeForeground { Process = 200 };
        foreground.Unknown.Add(200);
        var watch = new AdminWindowWatch(foreground);

        Assert.False(watch.Check(Ms(0)));
        Assert.False(watch.InFront);
        Assert.Equal(WindowInFront.Unknown, watch.Kind);
        watch.Check(Ms(500));
        Assert.Equal(WindowInFront.Unknown, watch.Kind);
        Assert.Equal(new[] { 200 }, foreground.Queries);
    }

    [Fact]
    public void A_probe_that_throws_leaves_the_last_answer_and_is_asked_again()
    {
        var foreground = new FakeForeground { Process = 200 };
        foreground.Elevated.Add(200);
        var watch = new AdminWindowWatch(foreground);
        watch.Check(Ms(0));

        foreground.Throws = true;
        Assert.Throws<InvalidOperationException>(() => watch.Check(Ms(500)));
        Assert.True(watch.InFront);
        Assert.True(watch.Due(Ms(1_000)));
    }

    // ─── The icon ───────────────────────────────────────────────────────
    //
    // Which of the hook's states, a window running as administrator and a
    // blocked microphone the icon shows is DictationTrayGlyph's, tested pair
    // by pair in TrayGlyphTests; here, what each says on its own.

    private static (TrayIcon Icon, string ToolTip) Glyph(HookHealth hook, bool adminWindowInFront = false) =>
        DictationTrayGlyph.Picture(
            DictationTrayGlyph.State(
                armed: true, enabling: false, DictationState.Armed, microphoneBlocked: false, hook, adminWindowInFront),
            Key);

    [Fact]
    public void A_hook_that_cannot_be_put_back_is_the_warning_and_says_so()
    {
        Assert.Equal(
            (TrayIcon.MicrophoneBlocked, "Earlet — can't hear Right Ctrl, open Earlet"),
            Glyph(HookHealth.Unheard));
    }

    [Fact]
    public void An_administrator_window_and_a_reconnected_hook_keep_the_ready_picture_and_say_so()
    {
        Assert.Equal(
            (TrayIcon.Ready, "Earlet — can't hear Right Ctrl while an administrator window is in front"),
            Glyph(HookHealth.Healthy, adminWindowInFront: true));
        Assert.Equal(
            (TrayIcon.Ready, "Earlet — reconnected Right Ctrl, hold it again"),
            Glyph(HookHealth.Reconnected));
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), Glyph(HookHealth.Healthy));
    }

    [Fact]
    public void Every_hold_key_tooltip_fits_what_NotifyIcon_accepts_and_names_the_key()
    {
        foreach (var key in DictationHotkeys.HoldChoices)
        {
            foreach (var tip in new[]
            {
                DictationCopy.TrayHoldKeyUnheard(key),
                DictationCopy.TrayAdminWindowInFront(key),
                DictationCopy.TrayHoldKeyReconnected(key),
            })
            {
                Assert.InRange(tip.Length, 1, 127);
                Assert.Contains(key.KeyName, tip, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_sentences_say_what_happened_and_what_to_do_in_Windows_words()
    {
        Assert.Equal(
            "Windows stopped passing Right Ctrl to Earlet, so Earlet reconnected it. If a hold did nothing, "
            + "click where you were typing and hold Right Ctrl again.",
            DictationCopy.HoldKeyReconnected(Key));
        Assert.Equal(
            "Windows stopped passing Right Ctrl to Earlet and would not let it reconnect. Pause and enable "
            + "dictation again, or quit and reopen Earlet. For details, choose Open log folder under Troubleshooting.",
            DictationCopy.HoldKeyUnheard(Key));
        Assert.Equal(
            "Earlet can't hear Right Ctrl while an administrator window is in front.",
            DictationCopy.AdminWindowInFront(Key));
        Assert.Equal(
            DictationCopy.Instructions(Key) + " Earlet can't hear Right Ctrl while an administrator window is in front.",
            DictationCopy.InstructionsWithAdminWindow(Key));
    }
}
