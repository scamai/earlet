using System.Collections.Concurrent;
using Dictation.Services;
using static Dictation.Core.Tests.DictationAttemptLogTests;

namespace Dictation.Core.Tests;

// The watch on Windows' microphone switch (DictationMicrophoneWatch and
// DictationController.Microphone.cs), ported from the Mac one scenario for
// one where the Mac has it, names kept in C# case:
// macos/Tests/DictationAppCoreTests/MenuGlyphTests.swift, suites "The app's
// permission poll" (cadence), "The window comes forward when something needs
// the user" (revokedGrant, firstRunIsNotALoss) and the glyph's revoked grant.
// The Mac watches three grants; Windows asks about one, the microphone.

/// <summary>Mac suite "The app's permission poll": the cadence, as a table.</summary>
public sealed class MicrophoneWatchCadenceTests
{
    [Fact]
    public void Once_a_second_while_blocked_every_five_seconds_while_on_with_access()
    {
        var fast = DictationMicrophoneWatch.WhileBlocked;
        var slow = DictationMicrophoneWatch.Steady;

        Assert.Equal(TimeSpan.FromSeconds(1), fast);
        Assert.Equal(TimeSpan.FromSeconds(5), slow);
        Assert.Equal(fast, DictationMicrophoneWatch.Interval(enabled: true, blockedKnown: true));
        Assert.Equal(fast, DictationMicrophoneWatch.Interval(enabled: false, blockedKnown: true));
        Assert.Equal(slow, DictationMicrophoneWatch.Interval(enabled: true, blockedKnown: false));
        Assert.True(slow > fast);
    }

    /// <summary>Windows only: off with no block known, nothing is watched.
    /// The hold key does nothing then and Enable asks first; the Mac polls
    /// while off for its permission checklist, which Windows has none of.</summary>
    [Fact]
    public void Off_with_no_block_known_nothing_is_watched() =>
        Assert.Null(DictationMicrophoneWatch.Interval(enabled: false, blockedKnown: false));

    [Theory]
    [InlineData(false, MicrophoneAccess.Denied, MicrophoneWatchVerdict.Lost)]
    [InlineData(false, MicrophoneAccess.DeniedByPolicy, MicrophoneWatchVerdict.Lost)]
    [InlineData(false, MicrophoneAccess.Allowed, MicrophoneWatchVerdict.Unchanged)]
    [InlineData(false, MicrophoneAccess.Busy, MicrophoneWatchVerdict.Unchanged)]
    [InlineData(false, MicrophoneAccess.Unknown, MicrophoneWatchVerdict.Unchanged)]
    [InlineData(true, MicrophoneAccess.Allowed, MicrophoneWatchVerdict.Regained)]
    [InlineData(true, MicrophoneAccess.Busy, MicrophoneWatchVerdict.Regained)]
    [InlineData(true, MicrophoneAccess.Denied, MicrophoneWatchVerdict.Unchanged)]
    [InlineData(true, MicrophoneAccess.DeniedByPolicy, MicrophoneWatchVerdict.Unchanged)]
    [InlineData(true, MicrophoneAccess.Unknown, MicrophoneWatchVerdict.Unchanged)]
    public void A_reading_changes_only_what_the_run_did_not_know(bool blockedKnown, MicrophoneAccess access, MicrophoneWatchVerdict verdict) =>
        Assert.Equal(verdict, DictationMicrophoneWatch.Judge(blockedKnown, access));
}

/// <summary>The watch through the controller the app runs.</summary>
public sealed class MicrophoneWatchTests
{
    private static readonly TimeSpan Steady = DictationMicrophoneWatch.Steady;
    private static readonly TimeSpan WhileBlocked = DictationMicrophoneWatch.WhileBlocked;

    /// <summary>A tick now, then <paramref name="after"/> later another, and
    /// whatever the readings posted.</summary>
    private static void Watch(Rig rig, TimeSpan after)
    {
        rig.Controller.Tick();
        rig.Clock.Advance(after);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
    }

    private static int Probes(Rig rig) => Volatile.Read(ref rig.Platform.Access.Probes);

    /// <summary>Readings the test answers by hand: each ask waits in
    /// <see cref="Asked"/> until the test sets it.</summary>
    private sealed class HeldReadings
    {
        public ConcurrentQueue<(Func<MicrophoneAccess> Read, TaskCompletionSource<MicrophoneAccess> Answer)> Asked { get; } = new();

        public Task<MicrophoneAccess> Run(Func<MicrophoneAccess> read)
        {
            var answer = new TaskCompletionSource<MicrophoneAccess>(TaskCreationOptions.RunContinuationsAsynchronously);
            Asked.Enqueue((read, answer));
            return answer.Task;
        }

        /// <summary>Answer the oldest ask by running its read now, and wait
        /// until its answer is posted.</summary>
        public void AnswerNext(Rig rig)
        {
            Assert.True(Asked.TryDequeue(out var ask), "nothing was asked");
            var posted = rig.Dispatcher.Posted;
            ask.Answer.SetResult(ask.Read());
            SpinWait.SpinUntil(() => rig.Dispatcher.Posted > posted, TimeSpan.FromSeconds(10));
        }
    }

    // ─── The Mac's scenarios ────────────────────────────────────────────

    [Fact]
    public async Task A_switch_turned_off_while_on_brings_the_window_forward_once_and_again_after_a_regain_and_a_second_loss()
    {
        // revokedGrant.
        using var rig = new Rig();
        await rig.EnableAsync();
        Watch(rig, Steady);
        Assert.Equal(0, rig.View.RevealRequests);
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);

        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);
        Assert.Equal(1, rig.View.RevealRequests);
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
        Assert.Equal(DictationCopy.TrayMicrophoneBlocked, rig.View.Last.Tray.ToolTip);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);

        // A block that stays asks once: closing the window is respected.
        Watch(rig, WhileBlocked);
        Watch(rig, WhileBlocked);
        Assert.Equal(1, rig.View.RevealRequests);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Watch(rig, WhileBlocked);
        Assert.Equal(1, rig.View.RevealRequests);
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);

        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);
        Assert.Equal(2, rig.View.RevealRequests);
    }

    [Fact]
    public async Task The_controller_stays_on_and_only_the_icon_says_the_key_no_longer_works()
    {
        // MenuGlyphTests: "the model stays enabled; only the icon can say it
        // no longer works", and the glyph's way back.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);

        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
        Assert.True(rig.View.Last.CanPause);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Watch(rig, WhileBlocked);
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task A_block_Enable_already_found_is_not_a_loss()
    {
        // firstRunIsNotALoss: Enable's own probe says so, in the window it was
        // clicked in; the watch then brings nothing forward.
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);

        Watch(rig, WhileBlocked);
        Watch(rig, WhileBlocked);
        Watch(rig, Steady);

        Assert.Equal(0, rig.View.RevealRequests);
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
    }

    // ─── When it reads ──────────────────────────────────────────────────

    [Fact]
    public async Task On_with_access_it_reads_every_five_seconds_and_no_sooner()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        var afterEnable = Probes(rig);

        Watch(rig, Steady - TimeSpan.FromMilliseconds(30));
        Assert.Equal(afterEnable, Probes(rig));
        Watch(rig, TimeSpan.FromMilliseconds(30));
        Assert.Equal(afterEnable + 1, Probes(rig));
        Watch(rig, Steady - TimeSpan.FromMilliseconds(30));
        Assert.Equal(afterEnable + 1, Probes(rig));
        Watch(rig, TimeSpan.FromMilliseconds(30));
        Assert.Equal(afterEnable + 2, Probes(rig));
    }

    [Fact]
    public async Task While_a_block_is_known_it_reads_every_second()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        var afterEnable = Probes(rig);

        Watch(rig, WhileBlocked - TimeSpan.FromMilliseconds(30));
        Assert.Equal(afterEnable, Probes(rig));
        Watch(rig, TimeSpan.FromMilliseconds(30));
        Assert.Equal(afterEnable + 1, Probes(rig));
    }

    [Fact]
    public void Off_with_no_block_known_Windows_is_not_asked()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        for (var i = 0; i < 12; i++)
        {
            Watch(rig, Steady);
        }

        Assert.Equal(0, Probes(rig));
        Assert.Equal(TrayIcon.Off, rig.View.Last.Tray.Icon);
        Assert.Equal(0, rig.View.RevealRequests);
    }

    [Fact]
    public async Task Paused_while_a_block_is_known_the_switch_turned_back_on_takes_the_warning_away()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.True(rig.Controller.TryPause());
        Assert.Equal(DictationCopy.PausedMicrophoneBlocked, rig.View.Last.Status);
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Watch(rig, WhileBlocked);

        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
        Assert.Equal(TrayIcon.Off, rig.View.Last.Tray.Icon);
        Assert.False(rig.View.Last.OffersMicrophoneSettings);

        // Off and nothing known any more: no further reading.
        var probes = Probes(rig);
        Watch(rig, Steady);
        Assert.Equal(probes, Probes(rig));
    }

    [Fact]
    public async Task Nothing_is_read_while_a_dictation_is_under_way()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Controller.Tick();
        rig.Press();
        rig.Dispatcher.Drain();
        var probes = Probes(rig);
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        for (var i = 0; i < 4; i++)
        {
            Watch(rig, Steady);
        }

        Assert.Equal(probes, Probes(rig));
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Assert.Equal(0, rig.View.RevealRequests);

        // Back at rest, the reading that came due during the hold is taken.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        Assert.Equal(probes + 1, Probes(rig));
    }

    // ─── What a reading changes ─────────────────────────────────────────

    [Fact]
    public async Task A_policy_block_says_so_without_the_privacy_page()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Access.Access = MicrophoneAccess.DeniedByPolicy;
        Watch(rig, Steady);

        Assert.Equal(DictationCopy.MicrophoneBlockedByPolicy, rig.View.Last.Status);
        Assert.False(rig.View.Last.OffersMicrophoneSettings);
        Assert.Equal(1, rig.View.RevealRequests);
    }

    [Fact]
    public async Task A_loss_hides_Try_dictation_and_a_regain_brings_it_back()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        Assert.True(rig.View.Last.PracticeVisible);

        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);
        Assert.False(rig.View.Last.PracticeVisible);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Watch(rig, WhileBlocked);
        Assert.True(rig.View.Last.PracticeVisible);
    }

    [Fact]
    public async Task A_regain_leaves_a_newer_status_line_alone()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);
        rig.Controller.ChooseLanguage("de");
        var newer = rig.View.Last.Status;
        Assert.StartsWith("Earlet now expects", newer, StringComparison.Ordinal);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Watch(rig, WhileBlocked);

        Assert.Equal(newer, rig.View.Last.Status);
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task A_reading_that_cannot_answer_changes_nothing_either_way()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        // On with access: unreadable is no evidence of a block.
        rig.Platform.Access.Throws = true;
        Watch(rig, Steady);
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
        Assert.Equal(0, rig.View.RevealRequests);

        // Blocked: unreadable is no evidence the block ended, so the warning
        // does not flicker.
        rig.Platform.Access.Throws = false;
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);
        rig.Platform.Access.Access = MicrophoneAccess.Unknown;
        Watch(rig, WhileBlocked);
        rig.Platform.Access.Throws = true;
        Watch(rig, WhileBlocked);

        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.Equal(1, rig.View.RevealRequests);
    }

    // ─── Off the UI thread, and late ────────────────────────────────────

    [Fact]
    public async Task The_product_reads_the_switch_off_the_calling_thread()
    {
        using var gate = new ManualResetEventSlim();
        var reading = new DictationControllerOptions().RunMicrophoneProbe(() =>
        {
            gate.Wait(TimeSpan.FromSeconds(2));
            return MicrophoneAccess.Allowed;
        });

        // Back before the read could finish: the caller, the UI thread that
        // also runs the keyboard hook, never waits for the registry.
        Assert.False(reading.IsCompleted);
        gate.Set();
        Assert.Equal(MicrophoneAccess.Allowed, await reading.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task One_reading_at_a_time()
    {
        var held = new HeldReadings();
        using var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        await rig.EnableAsync();

        for (var i = 0; i < 5; i++)
        {
            Watch(rig, Steady);
        }

        Assert.Single(held.Asked);
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();
        Watch(rig, Steady);
        Assert.Single(held.Asked);
    }

    [Fact]
    public async Task A_reading_that_lands_after_a_press_began_is_dropped()
    {
        var held = new HeldReadings();
        using var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        await rig.EnableAsync();
        Watch(rig, Steady);
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        rig.Press();
        rig.Dispatcher.Drain();
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.View.RevealRequests);
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Assert.StartsWith("Listening", rig.View.Last.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reading_that_lands_after_a_pause_is_dropped()
    {
        var held = new HeldReadings();
        using var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        await rig.EnableAsync();
        Watch(rig, Steady);
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        Assert.True(rig.Controller.TryPause());
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.View.RevealRequests);
        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
        Assert.Equal(TrayIcon.Off, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task A_reading_that_lands_after_the_app_closed_is_dropped()
    {
        // Paused with a block known, so dictation was off when it was asked
        // and is off when it lands: only the closing tells them apart.
        var held = new HeldReadings();
        var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.True(rig.Controller.TryPause());
        Watch(rig, WhileBlocked);
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Dispose();
        var renders = rig.View.States.Count;

        held.AnswerNext(rig);
        rig.Dispatcher.Drain();

        Assert.Equal(renders, rig.View.States.Count);
        Assert.Equal(DictationCopy.PausedMicrophoneBlocked, rig.View.Last.Status);
    }

    // ─── While a press is being decided, and after one ──────────────────

    /// <summary>A rig whose press-time password-field check answers when the
    /// test says, and whose readings do too.</summary>
    private static Rig Deciding(FieldChecks checks, HeldReadings? held = null) =>
        new(options: o => o with
        {
            RunFieldCheck = checks.Run,
            RunMicrophoneProbe = held is null ? o.RunMicrophoneProbe : held.Run,
        });

    [Fact]
    public async Task No_reading_is_asked_while_a_press_is_being_decided()
    {
        var checks = new FieldChecks();
        var held = new HeldReadings();
        using var rig = Deciding(checks, held);
        await rig.EnableAsync();
        rig.Controller.Tick();
        rig.Press();
        rig.Dispatcher.Drain();

        // The reading comes due with the field check still out.
        rig.Clock.Advance(Steady);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
        Assert.Empty(held.Asked);

        // Once the press is decided and its dictation done, it is asked.
        checks.Answer();
        rig.Dispatcher.Drain();
        rig.Platform.Microphone.Speak();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        rig.Controller.Tick();
        Assert.Single(held.Asked);
    }

    [Fact]
    public async Task A_reading_that_lands_while_a_press_is_being_decided_is_dropped()
    {
        // Applied, a loss would bring the window forward, and take the
        // keyboard with it, while the press that may yet type is undecided.
        var checks = new FieldChecks();
        var held = new HeldReadings();
        using var rig = Deciding(checks, held);
        await rig.EnableAsync();
        Watch(rig, Steady);
        rig.Press();
        rig.Dispatcher.Drain();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        held.AnswerNext(rig);
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.View.RevealRequests);
        Assert.NotEqual(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.NotEqual(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);

        // The press is then admitted as if nothing had been read.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        checks.Answer();
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task A_reading_from_before_a_hold_that_met_a_block_does_not_clear_it()
    {
        // A reading asked while access was allowed, still out while a hold
        // finds the microphone blocked: applied, it would take the warning
        // away, and the next reading would then call the same block a loss
        // and bring the window forward.
        var held = new HeldReadings();
        using var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        await rig.EnableAsync();
        Watch(rig, Steady);
        Assert.Single(held.Asked);

        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        rig.Platform.Microphone.StartSucceeds = false;
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Tray.Icon == TrayIcon.MicrophoneBlocked);
        var reveals = rig.View.RevealRequests;

        // The held reading saw access as it was when it was asked.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);

        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, WhileBlocked);
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
        Assert.Equal(reveals, rig.View.RevealRequests);
    }

    [Fact]
    public async Task A_reading_from_before_Check_again_found_access_is_dropped()
    {
        // The user turned the switch back on and came back to the window,
        // whose activation found access allowed. A reading asked while it was
        // still off, landing after, is older than that answer: applied, it
        // would put the warning back and bring the window forward again.
        var held = new HeldReadings();
        using var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Watch(rig, WhileBlocked);
        Assert.Single(held.Asked);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);

        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();

        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.Equal(0, rig.View.RevealRequests);
    }

    [Fact]
    public async Task A_reading_asked_while_paused_is_dropped_once_dictation_is_on()
    {
        // Enable asked Windows itself and found the block still there; the
        // reading asked before it, while paused, does not get to overrule it.
        var held = new HeldReadings();
        using var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.True(rig.Controller.TryPause());
        Watch(rig, WhileBlocked);
        Assert.Single(held.Asked);

        await rig.EnableAsync();
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();

        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
    }

    [Fact]
    public async Task A_reading_that_lands_while_Enable_loads_is_left_to_Enable()
    {
        // Enable asks Windows itself once the model is up, and that answer
        // replaces the last; until then the icon keeps the warning it had
        // (TrayMenuTests: "An enable loading while the microphone is known
        // blocked warns rather than starting").
        var held = new HeldReadings();
        using var rig = new Rig(options: o => o with { RunMicrophoneProbe = held.Run });
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.True(rig.Controller.TryPause());
        Watch(rig, WhileBlocked);
        Assert.Single(held.Asked);
        using var load = new ManualResetEventSlim();
        rig.Platform.Worker.HoldInit = load;

        var enabling = rig.Controller.EnableAsync();
        var loading = rig.View.Last.Tray;
        Assert.Equal(TrayIcon.MicrophoneBlocked, loading.Icon);
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        held.AnswerNext(rig);
        rig.Dispatcher.Drain();

        Assert.Equal(loading, rig.View.Last.Tray);
        Assert.Equal(DictationCopy.Preparing, rig.View.Last.Status);

        load.Set();
        await enabling;
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task A_read_that_throws_is_logged_and_read_as_nothing_learned()
    {
        // The probe promises never to throw; one that does must still not
        // fault the reading, which nothing would observe.
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        await rig.EnableAsync();
        rig.Platform.Access.Throws = true;
        Watch(rig, Steady);

        Assert.Contains("Microphone access could not be read by the watch", log.Lines);
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);

        // And the watch goes on: the next reading is taken.
        rig.Platform.Access.Throws = false;
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);
        Assert.Equal(TrayIcon.MicrophoneBlocked, rig.View.Last.Tray.Icon);
    }

    /// <summary>The watch's lines say what changed, never a path or a name.</summary>
    [Fact]
    public async Task A_loss_and_a_regain_are_logged_once_each()
    {
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        await rig.EnableAsync();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        Watch(rig, Steady);
        Watch(rig, WhileBlocked);
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Watch(rig, WhileBlocked);
        Watch(rig, Steady);

        Assert.Single(log.Lines, l => l.StartsWith("Windows now blocks the microphone (Denied)", StringComparison.Ordinal));
        Assert.Single(log.Lines, l => l.StartsWith("Microphone access is no longer blocked (Allowed)", StringComparison.Ordinal));
    }
}
