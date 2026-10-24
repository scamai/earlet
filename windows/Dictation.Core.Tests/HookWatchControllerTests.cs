using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The controller acting on the hook watch, through the real session and the
/// hook's real state machine (<see cref="FakeTrigger.UseGesture"/>), with
/// only Windows' key state and the hook's life faked
/// (<see cref="FakeTrigger.HookAlive"/>, <see cref="FakeTrigger.WindowsKeyDown"/>).
/// The Mac scenarios ported are its <c>WindowAttentionTests</c>
/// ("a degraded tap bumps the attention the window observes") and
/// <c>AppWindowAttentionTests</c>; the rest are what Windows adds: a release
/// the dead hook missed, a hook Windows will not put back, and a window
/// running as administrator.
/// </summary>
public sealed class HookWatchControllerTests
{
    private static readonly HotkeyBinding Key = DictationHotkeys.PushToTalk;

    /// <summary>A rig with the hook's real state machine, polled as the app
    /// polls it. Also HookAndMicrophoneTests', which add the microphone watch.</summary>
    internal sealed class Hook : IDisposable
    {
        public Hook(
            Microsoft.Extensions.Logging.ILoggerFactory? logs = null,
            Func<DictationControllerOptions, DictationControllerOptions>? options = null) =>
            Rig = new Rig(options: options, logs: logs, beforeStart: rig => rig.Platform.Gestures = true);

        public Rig Rig { get; }

        public FakeTrigger Trigger => Rig.Platform.Trigger;

        public FakeForeground Foreground => Rig.Platform.Foreground;

        public DictationViewState Shown => Rig.View.Last;

        public string Status => Shown.Status;

        public (TrayIcon, string) Tray => (Shown.Tray.Icon, Shown.Tray.ToolTip);

        public int Reveals => Rig.View.RevealRequests;

        public int Pastes => Rig.Platform.Paste.Count;

        public int Transcribes => Rig.Platform.Worker.Ops().Count(op => op == "transcribe");

        public bool Listening => Rig.Platform.Microphone.Open;

        public Task EnableAsync() => Rig.EnableAsync();

        /// <summary>The app's 30 ms poll, for <paramref name="milliseconds"/>,
        /// with the hook's clock and the controller's moving together.</summary>
        public void Poll(int milliseconds)
        {
            for (var elapsed = 0; elapsed < milliseconds; elapsed += 30)
            {
                PollAfter(30);
            }
        }

        /// <summary>One poll, <paramref name="milliseconds"/> after the last:
        /// 30 as the timer runs, more when the UI thread was busy.</summary>
        public void PollAfter(int milliseconds)
        {
            Trigger.GestureTime += milliseconds;
            Rig.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            Rig.Controller.Tick();
            Rig.Dispatcher.Drain();
            if (Listening)
            {
                Rig.Platform.Microphone.Speak(TimeSpan.FromMilliseconds(milliseconds));
            }
        }

        /// <summary>The user presses the hold key; the hook hears it only
        /// while Windows has not removed it.</summary>
        public void Hold() => Key(down: true);

        public void LetGo() => Key(down: false);

        private void Key(bool down)
        {
            Trigger.UserHoldKey(down);
            Rig.Controller.Tick();
            Rig.Dispatcher.Drain();
        }

        /// <summary>A whole dictation with a live hook: hold, speak, let go,
        /// and the words go in.</summary>
        public void Dictate()
        {
            var pastes = Pastes;
            Hold();
            Poll(600);
            Assert.True(Listening, Status);
            LetGo();
            Rig.DrainUntil(() => Pastes == pastes + 1 && Status.StartsWith("Text inserted", StringComparison.Ordinal));
            Poll(60);
        }

        public void Dispose() => Rig.Dispose();
    }

    // ─── A hook Windows removed ─────────────────────────────────────────

    /// <summary>Mac <c>degradeRaisesWindowAttention</c>: "a degraded tap bumps
    /// the attention the window observes", with the status line saying so.
    /// Windows learns of it at the next press, which records nothing.</summary>
    [Fact]
    public async Task A_removed_hook_is_found_at_the_next_press_reinstalled_and_the_window_comes_forward_once()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.HookAlive = false;

        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(1, hook.Trigger.ProbesSent);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
        Assert.Equal(1, hook.Reveals);
        // The press the dead hook missed records nothing, not even after the
        // new hook is in: half a sentence pasted would be worse.
        Assert.False(hook.Listening);
        Assert.Equal(0, hook.Transcribes);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayHoldKeyReconnected(Key)), hook.Tray);

        // The new hook hears the release: the icon is ready again, and the
        // next hold dictates.
        hook.LetGo();
        hook.Poll(600);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        hook.Dictate();
        Assert.Equal(1, hook.Pastes);
        Assert.Equal(1, hook.Reveals);
    }

    /// <summary>Mac <c>degradedListener</c>: "one bump, one request".</summary>
    [Fact]
    public async Task One_loss_brings_the_window_forward_once_however_long_it_is_polled()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(5_000);

        Assert.Equal(1, hook.Reveals);
        Assert.Equal(1, hook.Trigger.Reinstalls);
    }

    /// <summary>Mac <c>revokedGrant</c>: "a grant revoked while enabled brings
    /// the window forward once ... and again after a re-grant and a second
    /// loss": here the new hook hearing a key is the re-grant.</summary>
    [Fact]
    public async Task A_hook_lost_again_after_the_new_one_heard_a_key_brings_the_window_forward_again()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        hook.LetGo();
        hook.Poll(600);
        Assert.Equal(1, hook.Reveals);

        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(2, hook.Reveals);
        Assert.Equal(2, hook.Trigger.Reinstalls);
    }

    /// <summary>The microphone used to stay open until the recording limit
    /// when the hook died mid-hold: the release it never saw is replayed, the
    /// words go in, and only then is the window brought forward, so it cannot
    /// take the paste.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_release_the_removed_hook_missed_ends_the_hold_and_the_words_go_in_before_the_window_comes_forward(bool completedBeforeWait)
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Hold();
        hook.Poll(600);
        Assert.True(hook.Listening);
        var revealsAtPaste = -1;
        hook.Rig.Platform.Paste.Landed += () => revealsAtPaste = hook.Reveals;

        hook.Trigger.HookAlive = false;
        hook.LetGo();
        Assert.True(hook.Listening, "the dead hook never saw the release");
        hook.Poll(1_000);
        if (completedBeforeWait)
        {
            // A fast decode can finish inside Poll: a later tick already
            // replaced the insertion outcome with the hook-recovery status.
            hook.Rig.TickUntil(() => hook.Reveals == 1);
        }

        // Observe the completed recovery, retaining the insertion outcome in
        // the render history instead of waiting for a transient current line.
        // The paste increments its count before landing on the pipeline thread.
        hook.Rig.TickUntil(() => hook.Pastes == 1 && Volatile.Read(ref revealsAtPaste) >= 0
            && hook.Reveals == 1);
        Assert.Contains(hook.Rig.View.States,
            state => state.Status.StartsWith("Text inserted", StringComparison.Ordinal));

        Assert.Equal(1, hook.Transcribes);
        Assert.Equal(0, revealsAtPaste);
        Assert.Equal(1, hook.Reveals);
        Assert.Equal(new[] { false }, hook.Trigger.Resyncs);
        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
    }

    /// <summary>A latch whose stopping press the dead hook missed keeps
    /// listening (the press is adopted, not replayed), and the loss is
    /// reported once the next press, heard by the new hook, has stopped it
    /// and its words went in.</summary>
    [Fact]
    public async Task A_loss_found_during_a_latch_is_reported_once_the_dictation_is_over()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.DoubleTap();
        Assert.True(hands.Latched && hands.Listening);
        var key = hands.Key;

        key.HookAlive = false;
        key.WindowsKeyDown = true;
        for (var i = 0; i < 40; i++)
        {
            hands.Wait(30);
            hands.Speak();
        }

        Assert.Equal(1, key.Reinstalls);
        Assert.True(hands.Latched && hands.Listening, "still dictating: a missed press is never replayed");
        Assert.Equal(0, hands.Rig.View.RevealRequests);
        Assert.Equal(DictationCopy.Latched(Key), hands.Status);

        // Let go (the new hook hears it; that press held nothing), then one
        // press stops the latch.
        key.WindowsKeyDown = false;
        hands.Release();
        hands.Wait(400);
        Assert.True(hands.Latched);
        key.WindowsKeyDown = null;
        hands.Press();
        hands.Advance(60);
        hands.Release();
        hands.DrainUntil(() => hands.Pastes == 1 && hands.Inserted);
        Assert.Equal(0, hands.Rig.View.RevealRequests);
        hands.Wait(30);

        Assert.Equal(1, hands.Rig.View.RevealRequests);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hands.Status);
    }

    [Fact]
    public async Task A_hook_Windows_will_not_put_back_shows_the_warning_and_is_tried_again_at_the_next_disagreement()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.ReinstallSucceeds = false;
        hook.Trigger.HookAlive = false;

        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(DictationCopy.HoldKeyUnheard(Key), hook.Status);
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayHoldKeyUnheard(Key)), hook.Tray);
        Assert.Equal(1, hook.Reveals);

        // Let go: the release the dead hook missed is put right, and the hook
        // tried again, without asking again.
        hook.LetGo();
        hook.Poll(1_500);
        Assert.Equal(2, hook.Trigger.Reinstalls);
        Assert.Equal(1, hook.Reveals);

        // Press again, and this time Windows lets it back.
        hook.Trigger.ReinstallSucceeds = true;
        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(3, hook.Trigger.Reinstalls);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayHoldKeyReconnected(Key)), hook.Tray);
        Assert.Equal(1, hook.Reveals);
    }

    /// <summary>A removed hook with the key up disagrees with nothing: Windows
    /// says nothing when it removes one, so it is found at the next press.</summary>
    [Fact]
    public async Task A_removed_hook_is_not_noticed_while_the_key_stays_up()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.HookAlive = false;

        hook.Poll(10_000);

        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Trigger.ProbesSent);
        Assert.Equal(0, hook.Reveals);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
    }

    /// <summary>Mac <c>firstRunIsNotALoss</c>: the hold key already down when
    /// dictation comes on, which the new hook never saw go down. The test key
    /// reaches it, so it is alive: its view is put right and nothing else
    /// happens, and that press records nothing.</summary>
    [Fact]
    public async Task A_key_already_down_when_dictation_comes_on_is_not_a_lost_hook()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        var status = hook.Status;

        hook.Trigger.WindowsKeyDown = true;
        hook.Poll(1_000);

        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(1, hook.Trigger.ProbesSent);
        Assert.Equal(new[] { true }, hook.Trigger.Resyncs);
        Assert.Equal(0, hook.Reveals);
        Assert.Equal(status, hook.Status);
        Assert.False(hook.Listening);

        hook.LetGo();
        hook.Poll(600);
        hook.Dictate();
        Assert.Equal(1, hook.Pastes);
    }

    /// <summary>Right Ctrl held into Ctrl+Alt+Del: the release lands on the
    /// secure desktop, where no hook hears anything and Windows' key state
    /// reads up. Not a lost hook: once a window is back, the test key reaches
    /// it and its view is put right quietly.</summary>
    [Fact]
    public async Task A_release_made_on_a_secure_desktop_is_put_right_quietly_once_a_window_is_back()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Hold();
        hook.Poll(600);
        Assert.True(hook.Listening);

        hook.Foreground.Process = 0;
        hook.Trigger.HookAlive = false;
        hook.Trigger.WindowsKeyDown = false;
        hook.Poll(3_000);
        Assert.Equal(0, hook.Trigger.ProbesSent);
        Assert.Equal(0, hook.Trigger.Reinstalls);

        hook.Foreground.Process = 100;
        hook.Trigger.HookAlive = true;
        // Put right once the key has been quiet past the longest repeat
        // delay: the window is seen within 0.5 s, the release 1.2 s after.
        hook.Poll(2_500);
        hook.Rig.DrainUntil(() => hook.Pastes == 1);
        hook.Poll(100);

        Assert.Equal(1, hook.Trigger.ProbesSent);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(new[] { false }, hook.Trigger.Resyncs);
        Assert.Equal(0, hook.Reveals);
        Assert.NotEqual(DictationCopy.HoldKeyReconnected(Key), hook.Status);
    }

    /// <summary>The secure desktop appears between the reading that starts a
    /// suspicion and the one that would confirm it: the window in front is
    /// looked at on every poll while the two sides disagree, so the stale
    /// "ordinary window" answer of up to half a second ago is never acted on.</summary>
    [Fact]
    public async Task A_secure_desktop_that_appears_during_a_suspicion_is_seen_before_it_is_confirmed()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Hold();
        hook.Poll(600);
        hook.Trigger.HookAlive = false;
        hook.Trigger.WindowsKeyDown = false;
        for (var polls = 0; polls < 100 && hook.Trigger.ProbesSent == 0; polls++)
        {
            hook.Poll(30);
        }

        Assert.Equal(1, hook.Trigger.ProbesSent);

        hook.Foreground.Process = 0;
        hook.Poll(1_000);

        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
    }

    [Fact]
    public async Task A_new_hold_key_arms_a_new_hook_with_a_fresh_watch()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        hook.Trigger.HookAlive = true;
        hook.LetGo();
        Assert.Equal(1, hook.Reveals);

        hook.Rig.Controller.ChooseHoldKey(DictationHotkeys.HoldChoices[1].VirtualKey);
        Assert.Equal(2, hook.Rig.Platform.Triggers.Count);
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(2, hook.Reveals);
        Assert.Equal(DictationCopy.HoldKeyReconnected(DictationHotkeys.HoldChoices[1]), hook.Status);
    }

    [Fact]
    public async Task The_hold_key_is_read_twice_a_second_at_rest()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        var reads = 0;
        hook.Trigger.OnReadHook = () => reads++;
        var lookedAt = hook.Foreground.Reads;

        hook.Poll(5_010);

        Assert.InRange(reads, 9, 11);
        Assert.InRange(hook.Foreground.Reads - lookedAt, 9, 11);
    }

    [Fact]
    public async Task A_hook_that_cannot_be_read_never_takes_the_poll_down()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.OnReadHook = () => throw new InvalidOperationException("no");
        hook.Foreground.Throws = true;
        hook.Trigger.HookAlive = false;

        hook.Hold();
        hook.Poll(2_000);

        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
    }

    // ─── A window running as administrator ──────────────────────────────

    [Fact]
    public async Task An_administrator_window_in_front_is_named_in_the_tooltip_and_the_shortcut_line_while_it_is()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Foreground.Elevated.Add(200);

        hook.Foreground.Process = 200;
        hook.Poll(600);

        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayAdminWindowInFront(Key)), hook.Tray);
        Assert.Equal(DictationCopy.InstructionsWithAdminWindow(Key), hook.Shown.Instructions);

        hook.Foreground.Process = 100;
        hook.Poll(600);

        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        Assert.Equal(DictationCopy.Instructions(Key), hook.Shown.Instructions);
    }

    /// <summary>Windows sees the press, the hook cannot (UIPI): said in the
    /// status line, with no test key (Windows drops it there), no reinstall
    /// and no window brought forward, since nothing is broken.</summary>
    [Fact]
    public async Task A_press_in_an_administrator_window_says_why_nothing_happened_and_repairs_nothing()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Foreground.Elevated.Add(200);
        hook.Foreground.Process = 200;
        hook.Poll(600);

        hook.Trigger.WindowsKeyDown = true;
        hook.Poll(1_000);

        Assert.Equal(DictationCopy.AdminWindowInFront(Key), hook.Status);
        Assert.Equal(0, hook.Trigger.ProbesSent);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
        Assert.False(hook.Listening);

        // Let go there too: put right quietly.
        hook.Trigger.WindowsKeyDown = false;
        hook.Poll(1_000);
        Assert.Equal(new[] { true, false }, hook.Trigger.Resyncs);
        Assert.False(hook.Trigger.HookSawKeyDown);
        Assert.Equal(0, hook.Reveals);
    }

    /// <summary>Pressed in a normal window, released in one running as
    /// administrator: the release is replayed so the microphone does not stay
    /// open, and the hook, alive, is not reinstalled.</summary>
    [Fact]
    public async Task A_release_in_an_administrator_window_ends_the_hold()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Foreground.Elevated.Add(200);
        hook.Hold();
        hook.Poll(600);
        Assert.True(hook.Listening);

        hook.Foreground.Process = 200;
        hook.Trigger.WindowsKeyDown = false;
        hook.Poll(1_000);
        hook.Rig.DrainUntil(() => hook.Pastes == 1);

        Assert.Equal(1, hook.Transcribes);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
    }

    [Fact]
    public async Task The_administrator_hint_is_only_while_dictation_is_on()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Foreground.Elevated.Add(200);
        hook.Foreground.Process = 200;
        hook.Poll(600);
        Assert.Equal(DictationCopy.InstructionsWithAdminWindow(Key), hook.Shown.Instructions);

        Assert.True(hook.Rig.Controller.TryPause());
        hook.Poll(600);

        Assert.Equal(DictationCopy.Instructions(Key), hook.Shown.Instructions);
        Assert.Equal((TrayIcon.Off, DictationCopy.TrayOff), hook.Tray);
    }

    // ─── What the reviewers found ───────────────────────────────────────

    /// <summary>A test key Windows refused (another program blocking input)
    /// used to be thrown away, and the silence then read as a removed hook: a
    /// reinstall, the window over the user's, and the wrong sentence. It
    /// proves nothing: the hook's view is put right and nothing else.</summary>
    [Fact]
    public async Task A_test_key_Windows_refused_proves_nothing_so_nothing_is_reinstalled_or_brought_forward()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        var status = hook.Status;
        hook.Trigger.HookAlive = false;
        hook.Trigger.ProbeAccepted = false;

        hook.Hold();
        hook.Poll(1_000);

        Assert.Equal(1, hook.Trigger.ProbesSent);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(new[] { true }, hook.Trigger.Resyncs);
        Assert.Equal(0, hook.Reveals);
        Assert.Equal(status, hook.Status);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        Assert.False(hook.Listening);

        hook.LetGo();
        hook.Poll(2_000);
        Assert.Equal(new[] { true, false }, hook.Trigger.Resyncs);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
    }

    /// <summary>A window whose process Dictation could not ask about (it
    /// exited meanwhile, or would not answer) used to count as ordinary, so a
    /// press there, cut off from the hook if that process is above Dictation,
    /// read as a removed hook on every press. Nothing can be told there.</summary>
    [Fact]
    public async Task A_press_in_a_window_whose_elevation_cannot_be_told_reinstalls_nothing()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Foreground.Unknown.Add(300);
        hook.Foreground.Process = 300;
        hook.Poll(600);
        hook.Trigger.HookAlive = false;

        hook.Hold();
        hook.Poll(1_000);
        hook.LetGo();
        hook.Poll(1_000);

        Assert.Equal(0, hook.Trigger.ProbesSent);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
    }

    /// <summary>Let go in an administrator window, then click into an
    /// ordinary one within 300 ms: the disagreement began where no test key
    /// can go, and used to be called a loss with none sent. The ordinary
    /// window gets the test key, the live hook counts it, and the release is
    /// put right quietly.</summary>
    [Fact]
    public async Task A_release_in_an_administrator_window_followed_by_an_ordinary_one_is_not_a_lost_hook()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Foreground.Elevated.Add(200);
        hook.Foreground.Process = 200;
        hook.Poll(600);
        hook.Trigger.WindowsKeyDown = true;
        hook.Poll(1_000);
        Assert.Equal(new[] { true }, hook.Trigger.Resyncs);

        var reads = 0;
        hook.Trigger.OnReadHook = () => reads++;
        hook.Trigger.WindowsKeyDown = false;
        for (var polls = 0; polls < 40 && reads == 0; polls++)
        {
            hook.Poll(30);
        }

        Assert.Equal(1, reads);
        Assert.Equal(0, hook.Trigger.ProbesSent);
        hook.Foreground.Process = 100;
        hook.Poll(2_000);

        Assert.Equal(1, hook.Trigger.ProbesSent);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
        Assert.Equal(new[] { true, false }, hook.Trigger.Resyncs);
    }

    /// <summary>A hook removed during a latch: the stop tap is shorter than
    /// 300 ms, and the watch used to drop the test key's answer when the two
    /// sides agreed again, so taps went unheard until the recording limit.
    /// The first tap the watch sees is enough; the next tap, heard by the new
    /// hook, stops the latch, and the loss is said once the words are in.</summary>
    [Fact]
    public async Task A_hook_removed_during_a_latch_is_found_from_a_short_stop_tap()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.DoubleTap();
        hands.Wait(400);
        Assert.True(hands.Latched && hands.Listening);
        var key = hands.Key;
        key.HookAlive = false;

        // 150 ms taps the dead hook never hears, 900 ms apart.
        var taps = 0;
        for (; taps < 10 && key.Reinstalls == 0; taps++)
        {
            key.WindowsKeyDown = true;
            for (var i = 0; i < 5; i++)
            {
                hands.Wait(30);
                hands.Speak();
            }

            key.WindowsKeyDown = false;
            for (var i = 0; i < 25; i++)
            {
                hands.Wait(30);
                hands.Speak();
            }
        }

        Assert.Equal(1, key.Reinstalls);
        Assert.True(taps <= 5, $"found after {taps} taps");
        Assert.True(hands.Latched && hands.Listening, "a missed tap is never replayed");
        Assert.Equal(0, hands.Rig.View.RevealRequests);

        key.WindowsKeyDown = null;
        hands.Tap();
        hands.DrainUntil(() => hands.Pastes == 1 && hands.Inserted);
        Assert.Equal(0, hands.Rig.View.RevealRequests);
        hands.Wait(30);

        Assert.Equal(1, hands.Rig.View.RevealRequests);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hands.Status);
    }

    /// <summary>Another program's hook after Dictation's swallows the hold
    /// key (a remapper started before dictation was enabled): Windows' key
    /// state never sees it go down, Dictation's hook does, and its autorepeats
    /// keep arriving. That used to read as a missed release and cut every hold
    /// at about half a second. The hold now runs to the real release.</summary>
    [Fact]
    public async Task A_hold_kept_from_Windows_by_a_later_hook_runs_to_its_real_release()
    {
        using var hook = new Hook();
        await hook.EnableAsync();

        hook.Hold();
        hook.Trigger.WindowsKeyDown = false;
        for (var i = 0; i < 100; i++)
        {
            if (i >= 17)
            {
                // Autorepeats, after a 510 ms delay.
                hook.Trigger.KeyEvent((int)Key.VirtualKey, HoldKeyState.WmKeyDown);
            }

            hook.Poll(30);
        }

        Assert.True(hook.Listening, hook.Status);
        Assert.Empty(hook.Trigger.Resyncs);
        Assert.Equal(0, hook.Trigger.Reinstalls);
        Assert.Equal(1, hook.Trigger.ProbesSent);

        hook.LetGo();
        hook.Rig.DrainUntil(() => hook.Pastes == 1);
        Assert.Equal(1, hook.Transcribes);
        Assert.Equal(0, hook.Reveals);
    }

    /// <summary>The copy tells the user to pause and enable again when Windows
    /// will not put the hook back. Doing that used to keep the old hook's
    /// warning on a new, working hook until something else repainted.</summary>
    [Fact]
    public async Task Pausing_and_enabling_again_after_a_hook_Windows_would_not_put_back_starts_clean()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.ReinstallSucceeds = false;
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        hook.LetGo();
        hook.Poll(1_500);
        Assert.Equal((TrayIcon.MicrophoneBlocked, DictationCopy.TrayHoldKeyUnheard(Key)), hook.Tray);

        Assert.True(hook.Rig.Controller.TryPause());
        await hook.EnableAsync();

        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        hook.Poll(3_000);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        Assert.Equal(1, hook.Reveals);
    }

    [Fact]
    public async Task Enabling_again_after_the_administrator_window_left_drops_its_hint_at_once()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Foreground.Elevated.Add(200);
        hook.Foreground.Process = 200;
        hook.Poll(600);
        Assert.Equal(DictationCopy.InstructionsWithAdminWindow(Key), hook.Shown.Instructions);

        Assert.True(hook.Rig.Controller.TryPause());
        hook.Foreground.Process = 100;
        await hook.EnableAsync();

        Assert.Equal(DictationCopy.Instructions(Key), hook.Shown.Instructions);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
        hook.Poll(1_000);
        Assert.Equal(DictationCopy.Instructions(Key), hook.Shown.Instructions);

        // And one still in front is named again from the first look.
        Assert.True(hook.Rig.Controller.TryPause());
        hook.Foreground.Process = 200;
        await hook.EnableAsync();
        hook.Poll(60);
        Assert.Equal(DictationCopy.InstructionsWithAdminWindow(Key), hook.Shown.Instructions);
    }

    [Fact]
    public async Task A_new_hold_key_chosen_while_the_old_hook_was_reconnected_shows_the_new_key_ready()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayHoldKeyReconnected(Key)), hook.Tray);

        var next = DictationHotkeys.HoldChoices[1];
        hook.Rig.Controller.ChooseHoldKey(next.VirtualKey);

        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(next)), hook.Tray);
    }

    /// <summary>A loss found while the press's password-field check is out
    /// waits for it: brought forward then, Dictation's window would be where
    /// that press lands.</summary>
    [Fact]
    public async Task A_loss_found_while_the_press_time_field_check_is_out_waits_for_its_answer()
    {
        var checks = new FieldChecks();
        using var hook = new Hook(options: o => o with { RunFieldCheck = checks.Run });
        await hook.EnableAsync();
        var status = hook.Status;

        hook.Hold();
        hook.Poll(60);
        Assert.False(hook.Listening);
        hook.Trigger.HookAlive = false;
        hook.LetGo();
        hook.Poll(1_000);

        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(0, hook.Reveals);
        Assert.Equal(status, hook.Status);

        checks.Answer();
        hook.Poll(60);

        Assert.False(hook.Listening);
        Assert.Equal(1, hook.Reveals);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
    }

    /// <summary>A loss waiting for the dictation to end belongs to the hook
    /// it was found on: dictation paused and enabled again before the report
    /// went out never reports it over the new hook.</summary>
    [Fact]
    public async Task A_loss_still_waiting_when_dictation_is_paused_is_dropped_with_its_hook()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Hold();
        hook.Poll(600);
        Assert.True(hook.Listening);
        hook.Trigger.HookAlive = false;
        hook.LetGo();
        for (var polls = 0; polls < 100 && hook.Trigger.Reinstalls == 0; polls++)
        {
            hook.Poll(30);
        }

        Assert.Equal(1, hook.Trigger.Reinstalls);
        // The words go in with no poll after them: the report still waits.
        hook.Rig.DrainUntil(() => hook.Pastes == 1 && hook.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        Assert.Equal(0, hook.Reveals);

        Assert.True(hook.Rig.Controller.TryPause());
        await hook.EnableAsync();
        hook.Poll(1_000);

        Assert.Equal(0, hook.Reveals);
        Assert.NotEqual(DictationCopy.HoldKeyReconnected(Key), hook.Status);
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), hook.Tray);
    }

    // ─── The Win32 adapter, read from source ────────────────────────────
    //
    // Pins on the source, not evidence the hook behaves: WindowsDictationPlatform
    // cannot run here, and no one has watched a hook be removed on Windows.

    [Fact]
    public void The_Windows_trigger_reads_Windows_the_hook_and_its_count_in_that_order_and_resyncs_on_the_hook_s_clock()
    {
        var code = File.ReadAllText(DictationHudGeometryTests.AppFile("WindowsDictationPlatform.cs"));

        Assert.Contains(
            "public HookReading ReadHook() =>\n"
            + "            new(inner.IsKeyDownNow(), inner.HookSawKeyDown, inner.ObservedKeyEvents, inner.TriggerKeyEvents);",
            code.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "public void Resync(bool keyDown) => inner.Resync(keyDown, Environment.TickCount64);",
            code,
            StringComparison.Ordinal);
        Assert.Contains("public bool Reinstall() => inner.Reinstall();", code, StringComparison.Ordinal);
        Assert.Contains("public bool ProbeHook() => inner.ProbeHook();", code, StringComparison.Ordinal);
        Assert.Contains("new Elevation(new ForegroundElevation(", code, StringComparison.Ordinal);
    }

    // ─── The log ────────────────────────────────────────────────────────

    [Fact]
    public async Task The_log_says_what_was_found_and_done_and_never_the_words()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-hookwatch-log-").FullName;
        try
        {
            using (var logs = FileLoggerFactory.TryCreate(dir, out _)!)
            {
                using var hook = new Hook(logs);
                await hook.EnableAsync();
                hook.Dictate();
                hook.Trigger.HookAlive = false;
                hook.Hold();
                hook.Poll(1_000);
                hook.LetGo();
                hook.Poll(600);
                hook.Foreground.Elevated.Add(200);
                hook.Foreground.Process = 200;
                hook.Poll(600);
            }

            var log = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
            Assert.Matches(
                "The hold-to-talk hook stopped receiving keys: Windows had Right Ctrl down where the hook never saw "
                + "it, and a test key sent to it [0-9]+ ms before never arrived; reinstalled: True",
                log);
            Assert.Contains("The reinstalled hold-to-talk hook is receiving keys", log, StringComparison.Ordinal);
            Assert.Contains(
                "A window running as administrator is in front; Right Ctrl cannot reach Dictation until it is not",
                log,
                StringComparison.Ordinal);
            Assert.DoesNotContain("hello world", log, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
