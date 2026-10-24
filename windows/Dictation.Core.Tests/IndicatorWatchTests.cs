using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The indicator's pacing, heartbeat, wrap and busy width, and its one
/// spoken sentence, with the endings the other watches added: a password
/// field at the paste, a microphone change that ends a take, and a hold-key hook
/// the hook watch repairs. Through the controller the app runs, with the
/// screen reader's side of the window mirrored by <see cref="FakeHud.Spoken"/>.
/// </summary>
public sealed class IndicatorWatchTests
{
    private const string AirPods = "AirPods Pro";

    /// <summary>The ending of a take whose microphone went away after the
    /// rig's half second of speech.</summary>
    private static readonly string MicStopped =
        DictationCopy.HudTakeStoppedInserted(DictationTakeStop.MicrophoneRemoved, TimeSpan.FromSeconds(0.5));

    private static readonly HotkeyBinding Key = DictationHotkeys.PushToTalk;

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    /// <summary>The controller's tick after <paramref name="after"/>.</summary>
    private static void Tick(Rig rig, TimeSpan after)
    {
        rig.Clock.Advance(after);
        rig.Controller.Tick();
        rig.Dispatcher.Drain();
    }

    private static void ChangeMicrophone(Rig rig)
    {
        rig.Platform.Microphone.Change(AudioCaptureEndpointChangeKind.Removed, AirPods, null);
        rig.Dispatcher.Drain();
    }

    /// <summary>An ending's own pill: sized for the hold's busy lines, its
    /// second line wrapped, a caution.</summary>
    private static void AssertEnding(DictationHudModel pill, string text)
    {
        Assert.Equal(text, pill.Text);
        Assert.Equal(HudTone.Caution, pill.Tone);
        Assert.Equal(DictationCopy.HudBusyLines(FakeHud.Hold.KeyName, FakeHud.Hold.Policy), pill.SizedFor);
        Assert.True(DictationHudGeometry.DetailWraps(pill), $"\"{text}\" does not wrap");
        Assert.NotNull(DictationHudLines.For(text).Detail);
    }

    // ─── A microphone change (MIC, kept by LIMIT-WIN-A) ─────────────────

    /// <summary>The microphone goes away under a take: the take ends and its
    /// words go in, and the ending, a caution, says where it stopped. Spoken
    /// once, with the microphone already closed; the window losing it brings
    /// it back within a second, not spoken again.</summary>
    [Fact]
    public async Task A_microphone_change_ends_the_take_with_its_words_in_and_is_spoken_once()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Tick(rig, TimeSpan.FromMilliseconds(100));
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);

        ChangeMicrophone(rig);
        rig.TickUntil(() => rig.Hud.Current?.Text == MicStopped);

        AssertEnding(rig.Hud.Current!, MicStopped);
        Assert.Equal(1, rig.Platform.Paste.Count);
        Assert.Single(rig.Hud.Spoken, s => s.Sentence == MicStopped);
        Assert.All(rig.Hud.Spoken, s => Assert.False(s.MicrophoneOpen, s.Sentence));

        rig.Hud.Vanish();
        Tick(rig, DictationHudReconcile.Interval);

        Assert.Equal(MicStopped, rig.Hud.Current!.Text);
        Assert.False(rig.Hud.Announced[^1]);
        Assert.Single(rig.Hud.Spoken, s => s.Sentence == MicStopped);
    }

    /// <summary>The next press takes the ending down at once, and from then on
    /// the heartbeat heals the listening pill, never the ending.</summary>
    [Fact]
    public async Task A_microphone_stopped_ending_the_next_press_took_down_is_never_drawn_again_by_the_heartbeat()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        ChangeMicrophone(rig);
        rig.TickUntil(() => rig.Hud.Current?.Text == MicStopped);
        // The key that was held all along comes up: it starts nothing.
        rig.Release();
        rig.Dispatcher.Drain();
        Assert.Equal(MicStopped, rig.Hud.Current!.Text);

        Tick(rig, TimeSpan.FromMilliseconds(100));
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);

        rig.Hud.Vanish();
        Tick(rig, DictationHudReconcile.Interval);
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);

        rig.Release();
        rig.DrainUntil(() => rig.Platform.Paste.Count == 2 && Inserted(rig.View.Last));
        for (var i = 0; i < 8; i++)
        {
            Tick(rig, DictationHudReconcile.Interval);
        }

        Assert.Single(rig.Hud.Shows, m => m.Text == MicStopped);
        Assert.Single(rig.Hud.Spoken, s => s.Sentence == MicStopped);
        Assert.All(rig.Hud.Spoken, s => Assert.False(s.MicrophoneOpen, s.Sentence));
    }

    /// <summary>An ending that stood its time is gone for good: neither a
    /// window that lost track of it nor one that kept it gets it back.</summary>
    [Fact]
    public async Task A_microphone_stopped_ending_that_timed_out_is_never_drawn_again_by_the_heartbeat()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        ChangeMicrophone(rig);
        rig.TickUntil(() => rig.Hud.Current?.Text == MicStopped);
        var notice = rig.Hud.Current!;
        Tick(rig, DictationHudModel.FailureDwell);
        Assert.Null(rig.Hud.Current);
        var hides = rig.Hud.Hides;

        // The window kept it after all: the heartbeat takes it away.
        rig.Hud.Stray(notice);
        Tick(rig, DictationHudReconcile.Interval);
        Assert.Null(rig.Hud.Current);
        Assert.Equal(hides + 1, rig.Hud.Hides);

        for (var i = 0; i < 5; i++)
        {
            Tick(rig, DictationHudReconcile.Interval);
        }

        Assert.Null(rig.Hud.Current);
        Assert.Single(rig.Hud.Shows, m => m.Text == MicStopped);
        Assert.Single(rig.Hud.Spoken, s => s.Sentence == MicStopped);
    }

    // ─── A password field at the paste (FIELDCHECK) ─────────────────────

    /// <summary>
    /// A quick release leaves "Transcribing…" waiting out the listening
    /// pill's 375 ms; the refusal at the paste ends the dictation before it
    /// may land, so it never does: "Listening", then the ending, spoken once
    /// and sized and wrapped as every ending is. Once its time is up it is
    /// gone, and the heartbeat does not bring it back.
    /// </summary>
    [Fact]
    public async Task A_password_field_at_the_paste_ends_over_a_held_back_busy_pill_and_is_spoken_once()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        using var decode = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = decode;
        rig.Press();
        rig.Dispatcher.Drain();
        Tick(rig, TimeSpan.FromMilliseconds(100));
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.Equal(DictationCopy.HudHeld, rig.Hud.Current!.Text);

        rig.Platform.SecureField.Next = SecureFieldState.Secure;
        decode.Set();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtPaste);
        rig.Platform.Worker.HoldTranscribe = null;
        rig.Platform.SecureField.Next = SecureFieldState.NotSecure;

        var ending = rig.Hud.Current!;
        AssertEnding(ending, DictationCopy.HudPasswordFieldAtRelease);
        Tick(rig, DictationHudPacer.VisibleDwell);
        Assert.Equal(
            [DictationCopy.HudHeld, DictationCopy.HudPasswordFieldAtRelease],
            rig.Hud.Shows.Select(m => m.Text).Distinct());
        Assert.Equal([(DictationCopy.HudPasswordFieldAtRelease, false)], rig.Hud.Spoken);

        Tick(rig, DictationHudModel.FailureDwell);
        Assert.Null(rig.Hud.Current);
        rig.Hud.Stray(ending);
        for (var i = 0; i < 4; i++)
        {
            Tick(rig, DictationHudReconcile.Interval);
        }

        Assert.Null(rig.Hud.Current);
        Assert.Single(rig.Hud.Shows, m => m.Text == DictationCopy.HudPasswordFieldAtRelease);
        Assert.Single(rig.Hud.Spoken);
    }

    // ─── A hook the hook watch repairs (HOOKWATCH) ──────────────────────

    /// <summary>
    /// A press the removed hook missed is adopted, not replayed: nothing
    /// records, so nothing is drawn or spoken while the watch suspects it,
    /// sends its test key and puts a new hook in, through three heartbeats,
    /// and the last dictation's receipt, long gone, stays gone. The hook's
    /// sentence is the status line's, never the pill's.
    /// </summary>
    [Fact]
    public async Task While_the_hook_watch_repairs_a_press_it_missed_the_indicator_shows_nothing()
    {
        using var hook = new HookWatchControllerTests.Hook();
        await hook.EnableAsync();
        hook.Dictate();
        hook.Poll(1_000);
        Assert.Null(hook.Rig.Hud.Current);
        var shows = hook.Rig.Hud.Shows.Count;
        var spoken = hook.Rig.Hud.Spoken.Count;

        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(3_000);

        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
        Assert.False(hook.Listening);
        Assert.Equal(shows, hook.Rig.Hud.Shows.Count);
        Assert.Equal(spoken, hook.Rig.Hud.Spoken.Count);
        Assert.Null(hook.Rig.Hud.Current);

        hook.LetGo();
        hook.Poll(1_000);
        Assert.Equal(shows, hook.Rig.Hud.Shows.Count);

        // The next hold is drawn and spoken as ever.
        hook.Dictate();
        Assert.Equal(2, hook.Rig.Hud.Spoken.Count(s => s.Sentence == "Typed"));
    }

    /// <summary>
    /// A release the removed hook missed leaves the microphone open until
    /// the watch replays it: through that repair the listening pill stands,
    /// is healed if the window loses it, and is never spoken; the ending
    /// after the replayed release is spoken once, and nothing the hook watch
    /// says reaches the pill.
    /// </summary>
    [Fact]
    public async Task A_hold_whose_release_the_hook_missed_stays_unspoken_until_the_replayed_release_ends_it()
    {
        using var hook = new HookWatchControllerTests.Hook();
        await hook.EnableAsync();
        hook.Hold();
        hook.Poll(600);
        Assert.True(hook.Listening);

        hook.Trigger.HookAlive = false;
        hook.LetGo();
        hook.Rig.Hud.Vanish();
        hook.Poll(1_000);
        // The insertion status is transient, but its render history persists.
        // Drain the completion callback before ticking the separate hook
        // recovery: the session can become Armed before completion is posted.
        hook.Rig.DrainUntil(() => hook.Pastes == 1 && hook.Rig.View.States.Any(Inserted));
        hook.Rig.TickUntil(() => hook.Reveals == 1);
        Assert.Contains(hook.Rig.View.States, Inserted);

        var hud = hook.Rig.Hud;
        Assert.Equal(DictationCopy.HoldKeyReconnected(Key), hook.Status);
        Assert.All(hud.Spoken, s => Assert.False(s.MicrophoneOpen, s.Sentence));
        Assert.Single(hud.Spoken, s => s.Sentence == "Typed");
        Assert.All(
            hud.Shows.Zip(hud.Announced).Where(p => p.First.Text == DictationCopy.HudHeld),
            p => Assert.False(p.Second));
        Assert.DoesNotContain(hud.Shows, m =>
            m.Text.Contains("Windows stopped passing", StringComparison.Ordinal)
            || m.Text.Contains("administrator", StringComparison.Ordinal));
    }
}
