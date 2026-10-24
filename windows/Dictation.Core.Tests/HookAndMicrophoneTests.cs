using System.Diagnostics;
using Dictation.Services;
using Dictation.Speech;
using Hook = Dictation.Core.Tests.HookWatchControllerTests.Hook;

namespace Dictation.Core.Tests;

/// <summary>
/// The hook watch (DictationController.HookWatch.cs) and the microphone watch
/// (DictationController.Microphone.cs) on one controller: both can find
/// something wrong at once, both bring the window forward and write the status
/// line, and both run from the same 30 ms poll. The Mac's rules they keep
/// together: one status line with the microphone first
/// (<c>DictationPermissionStep.resolve</c> puts the microphone before the
/// shortcut), and <c>DictationWindowAttention</c>'s edges, not levels: one
/// bump per distinct cause, never two for one, and neither cause swallowing
/// the other's. The icon's precedence across the three inputs is
/// TrayGlyphTests'.
/// </summary>
public sealed class HookAndMicrophoneTests
{
    private static readonly HotkeyBinding Key = DictationHotkeys.PushToTalk;

    private static int Probes(Hook hook) => Volatile.Read(ref hook.Rig.Platform.Access.Probes);

    private static string Both(string microphone, string holdKey) => microphone + " " + holdKey;

    // ─── One status line, the microphone first ──────────────────────────

    [Fact]
    public async Task A_hook_lost_while_the_microphone_is_blocked_joins_its_line_after_the_microphone_and_outlives_the_block()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        var access = hook.Rig.Platform.Access;
        access.Access = MicrophoneAccess.Denied;
        hook.Poll(5_100);
        Assert.Equal(DictationCopy.MicrophoneBlocked, hook.Status);
        Assert.Equal(1, hook.Reveals);

        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(Both(DictationCopy.MicrophoneBlocked, DictationCopy.HoldKeyReconnected(Key)), hook.Status);
        // The microphone's fix stays on offer, and the icon keeps its warning.
        Assert.True(hook.Shown.OffersMicrophoneSettings);
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked), hook.Tray);
        // A second, distinct cause: the window comes forward for it too.
        Assert.Equal(2, hook.Reveals);

        // The new hook hears the release; the block stays and is read every
        // second: nothing asks again.
        hook.LetGo();
        hook.Poll(3_000);
        Assert.Equal(2, hook.Reveals);
        Assert.Equal(Both(DictationCopy.MicrophoneBlocked, DictationCopy.HoldKeyReconnected(Key)), hook.Status);

        // Access back: only the microphone's half was made untrue.
        access.Access = MicrophoneAccess.Allowed;
        hook.Poll(1_100);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
        Assert.False(hook.Shown.OffersMicrophoneSettings);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        Assert.Equal(2, hook.Reveals);
    }

    [Fact]
    public async Task A_microphone_lost_after_a_hook_Windows_would_not_put_back_goes_first_on_the_line_and_Check_again_keeps_both()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.ReinstallSucceeds = false;
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        Assert.Equal(DictationCopy.HoldKeyUnheard(Key), hook.Status);
        Assert.Equal(1, hook.Reveals);

        // Let go: found again and tried again, and not asked again.
        hook.LetGo();
        hook.Poll(1_500);
        Assert.Equal(2, hook.Trigger.Reinstalls);
        Assert.Equal(1, hook.Reveals);

        var access = hook.Rig.Platform.Access;
        access.Access = MicrophoneAccess.Denied;
        hook.Poll(5_100);

        var both = Both(DictationCopy.MicrophoneBlocked, DictationCopy.HoldKeyUnheard(Key));
        Assert.Equal(both, hook.Status);
        Assert.True(hook.Shown.OffersMicrophoneSettings);
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked), hook.Tray);
        Assert.Equal(2, hook.Reveals);

        // Check again with the switch still off: the microphone's half
        // changes, the hold key's stays.
        hook.Rig.Controller.RecheckMicrophone(checkAgain: true);
        Assert.Equal(Both(DictationCopy.MicrophoneStillBlocked, DictationCopy.HoldKeyUnheard(Key)), hook.Status);

        access.Access = MicrophoneAccess.Allowed;
        hook.Poll(1_100);
        Assert.Equal(DictationCopy.HoldKeyUnheard(Key), hook.Status);
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayHoldKeyUnheard(Key)), hook.Tray);
        Assert.Equal(2, hook.Reveals);
    }

    [Fact]
    public async Task A_press_in_an_administrator_window_while_the_microphone_is_blocked_keeps_the_microphone_first()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Rig.Platform.Access.Access = MicrophoneAccess.Denied;
        hook.Poll(5_100);
        hook.Foreground.Elevated.Add(200);
        hook.Foreground.Process = 200;
        hook.Poll(600);

        // Windows reports the press there; the hook never hears it.
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(Both(DictationCopy.MicrophoneBlocked, DictationCopy.AdminWindowInFront(Key)), hook.Status);
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked), hook.Tray);
        // A limit of Windows, not a fault: only the microphone asked.
        Assert.Equal(1, hook.Reveals);
        Assert.Equal(0, hook.Trigger.Reinstalls);
    }

    // ─── Window reveals: one per cause ──────────────────────────────────

    [Fact]
    public async Task Both_found_together_bring_the_window_forward_once_each_and_each_asks_again_only_after_its_own_recovery()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        var access = hook.Rig.Platform.Access;
        access.Access = MicrophoneAccess.Denied;
        hook.Trigger.HookAlive = false;
        // The press lands just before the watch's 5 s reading, so the hook's
        // test key is out when the microphone is found blocked.
        hook.Poll(4_800);
        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(2, hook.Reveals);
        Assert.Equal(Both(DictationCopy.MicrophoneBlocked, DictationCopy.HoldKeyReconnected(Key)), hook.Status);

        // Levels, not edges, would ask again here: the block stays, and the
        // new hook is removed again before it heard a key.
        hook.Trigger.HookAlive = false;
        hook.LetGo();
        hook.Poll(3_000);
        Assert.Equal(2, hook.Trigger.Reinstalls);
        Assert.Equal(2, hook.Reveals);

        // Each cause recovers (access back; the new hook hears the user type
        // a letter) and is lost again: one more each, no more.
        access.Access = MicrophoneAccess.Allowed;
        hook.Poll(1_100);
        hook.Trigger.KeyEvent('A', HoldKeyState.WmKeyDown);
        hook.Trigger.KeyEvent('A', HoldKeyState.WmKeyUp);
        hook.Poll(600);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        access.Access = MicrophoneAccess.Denied;
        hook.Poll(5_100);
        Assert.Equal(3, hook.Reveals);
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        Assert.Equal(4, hook.Reveals);
        hook.LetGo();
        hook.Poll(3_000);
        Assert.Equal(4, hook.Reveals);
    }

    // ─── One poll, two throttles ────────────────────────────────────────

    [Fact]
    public async Task On_the_one_poll_each_watch_keeps_its_own_cadence()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        var reads = 0;
        hook.Trigger.OnReadHook = () => reads++;
        var probes = Probes(hook);
        var looks = hook.Foreground.Reads;

        hook.Poll(10_500);

        // The microphone every 5 s; the hook and the window in front every 0.5 s.
        Assert.Equal(2, Probes(hook) - probes);
        Assert.InRange(reads, 20, 22);
        Assert.InRange(hook.Foreground.Reads - looks, 20, 22);

        // A block known: the microphone every second, the hook unchanged.
        hook.Rig.Platform.Access.Access = MicrophoneAccess.Denied;
        hook.Poll(5_100);
        reads = 0;
        probes = Probes(hook);
        hook.Poll(10_500);
        Assert.InRange(Probes(hook) - probes, 10, 11);
        Assert.InRange(reads, 20, 22);
    }

    [Fact]
    public async Task A_hook_watch_rechecking_every_tenth_of_a_second_does_not_delay_the_microphone_reading()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        // A press the hook missed, with no window in front (a secure desktop):
        // the disagreement waits for an ordinary window, looked at on every
        // poll and rechecked every 0.1 s, for as long as it lasts.
        hook.Foreground.Process = 0;
        hook.Trigger.HookAlive = false;
        hook.Hold();
        var reads = 0;
        hook.Trigger.OnReadHook = () => reads++;
        var probes = Probes(hook);

        hook.Poll(10_500);

        // Every 0.1 s falls on every fourth 30 ms poll: one look per 120 ms.
        Assert.InRange(reads, 85, 90);
        Assert.Equal(2, Probes(hook) - probes);
        Assert.Equal(0, hook.Trigger.Reinstalls);
    }

    /// <summary>On a steady 30 ms poll the two schedules never meet: the 5 s
    /// reading falls between two looks at the hook. A late poll (the UI thread
    /// busy, which is when Windows removes a hook) can make both due at once,
    /// with the hook's test key still out: the reading is asked on that poll
    /// all the same, before the hook's answer is read.</summary>
    [Fact]
    public async Task A_microphone_reading_due_on_the_poll_that_answers_the_hook_s_test_key_is_asked_on_that_poll()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        var start = hook.Rig.Clock.Now;
        var probes = Probes(hook);
        hook.Poll(4_500);
        hook.Trigger.HookAlive = false;
        hook.Hold();
        while (hook.Trigger.ProbesSent == 0)
        {
            hook.PollAfter(30);
        }

        var sentAt = hook.Rig.Clock.Now;
        // The watch began at the first poll after Enable.
        var readingDue = start + TimeSpan.FromMilliseconds(30) + DictationMicrophoneWatch.Steady;
        Assert.True(sentAt < readingDue, $"the test key went out at {sentAt}, after the reading was due at {readingDue}");
        Assert.Equal(probes, Probes(hook));
        Assert.Equal(0, hook.Trigger.Reinstalls);

        var answerDue = sentAt + DictationHookWatch.ConfirmAfter;
        var late = (readingDue > answerDue ? readingDue : answerDue) - hook.Rig.Clock.Now;
        hook.PollAfter((int)Math.Ceiling(late.TotalMilliseconds));

        Assert.Equal(probes + 1, Probes(hook));
        Assert.Equal(1, hook.Trigger.Reinstalls);
    }

    [Fact]
    public async Task A_microphone_reading_that_never_comes_back_does_not_hold_up_the_hook_watch_and_is_not_asked_again_on_top_of_itself()
    {
        var stuck = new TaskCompletionSource<MicrophoneAccess>();
        var holdReadings = false;
        var asks = 0;
        using var hook = new Hook(options: o => o with
        {
            RunMicrophoneProbe = read =>
            {
                if (!holdReadings)
                {
                    return Task.FromResult(read());
                }

                asks++;
                return stuck.Task;
            },
        });
        await hook.EnableAsync();
        holdReadings = true;
        hook.Poll(5_100);
        Assert.Equal(1, asks);

        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
        Assert.Equal(1, hook.Reveals);
        hook.LetGo();
        hook.Poll(10_000);
        Assert.Equal(1, asks);
    }

    [Fact]
    public async Task Through_the_product_s_runner_a_slow_registry_never_holds_the_poll_and_the_hook_watch_carries_on()
    {
        using var gate = new ManualResetEventSlim();
        using var hook = new Hook(options: o => o with
        {
            RunMicrophoneProbe = new DictationControllerOptions().RunMicrophoneProbe,
        });
        await hook.EnableAsync();
        var probes = Probes(hook);
        hook.Rig.Platform.Access.Gate = gate;
        var reads = 0;
        hook.Trigger.OnReadHook = () => reads++;

        var clock = Stopwatch.StartNew();
        hook.Poll(6_000);
        clock.Stop();

        // The 5 s reading began, and is still waiting on the registry: had a
        // tick waited for it, this would have taken 10 s.
        Assert.True(SpinWait.SpinUntil(() => Probes(hook) > probes, TimeSpan.FromSeconds(10)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the poll took {clock.Elapsed}");
        Assert.InRange(reads, 12, 13);
        gate.Set();
    }
}
