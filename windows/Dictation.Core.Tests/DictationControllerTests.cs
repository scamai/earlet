using Dictation.Services;

namespace Dictation.Core.Tests;

public sealed class DictationControllerTests
{
    [Fact]
    public async Task A_hold_is_transcribed_and_pasted_through_the_real_pipeline()
    {
        using var rig = new Rig();

        await rig.EnableAsync();
        Assert.True(rig.View.Last.CanPause);

        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));

        Assert.Equal(1, rig.Platform.Paste.Count);
        // Restored after the paste: the user's clipboard, not the transcript.
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
        Assert.Contains("transcribe", rig.Platform.Worker.Ops());
    }

    // ─── The pause teardown under a dictation in flight ─────────────────
    //
    // The window and the menu can no longer pause a dictation under way
    // (TryPause refuses; TrayMenuTests), so these call the internal Pause
    // directly: they pin the teardown of a live hold, the Stop that quitting
    // and a failure run too, and the detached session's late events.

    [Fact]
    public async Task The_pause_teardown_mid_hold_leaves_the_window_paused()
    {
        // The sequence once reported, before Pause refused under a dictation:
        // hold (the window says Listening), click Pause. Cancelling the session
        // raises Armed and Cancelled, and the dispatcher runs those AFTER
        // Pause has painted "Paused" — so without the guard they repaint
        // "Nothing inserted … try again" and turn the Pause button back on
        // over a hook that no longer exists.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.StartsWith("Listening", rig.View.Last.Status, StringComparison.Ordinal);

        rig.Controller.Pause();
        rig.Dispatcher.Drain();

        Assert.Equal("Paused. The microphone is off.", rig.View.Last.Status);
        Assert.False(rig.View.Last.CanPause);
        Assert.True(rig.View.Last.CanEnable);
        Assert.False(rig.Platform.Microphone.Open);
        Assert.True(rig.Platform.Trigger.Disposed);
    }

    [Fact]
    public async Task An_event_posted_before_Pause_is_dropped_when_it_runs_after()
    {
        // The press is seen and its Listening repaint is queued, but the
        // dispatcher has not run it when the user clicks Pause. Detaching the
        // session in Stop cannot help here — the callback was queued before —
        // so this is the identity check on the sender doing the work.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        Assert.True(rig.Dispatcher.Pending > 0);

        rig.Controller.Pause();
        rig.Dispatcher.Drain();

        Assert.Equal("Paused. The microphone is off.", rig.View.Last.Status);
        Assert.False(rig.View.Last.CanPause);
    }

    [Fact]
    public async Task The_pause_teardown_during_the_decode_inserts_nothing_and_stays_paused()
    {
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Transcribing", StringComparison.Ordinal));

        rig.Controller.Pause();
        hold.Set();
        Thread.Sleep(100);
        rig.Dispatcher.Drain();

        Assert.Equal("Paused. The microphone is off.", rig.View.Last.Status);
        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task Enabling_again_after_a_pause_works()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Controller.Pause();

        await rig.EnableAsync();

        Assert.True(rig.Controller.IsEnabled);
        Assert.True(rig.View.Last.CanPause);
        Assert.Equal(2, rig.Platform.Triggers.Count);
    }

    [Fact]
    public async Task A_refused_hook_says_so_and_leaves_Enable_available()
    {
        using var rig = new Rig();
        rig.Platform.TriggerRegisters = false;

        await rig.EnableAsync();

        Assert.False(rig.Controller.IsEnabled);
        Assert.True(rig.View.Last.CanEnable);
        Assert.Equal(
            "The shortcut could not be enabled. Try enabling dictation again.",
            rig.View.Last.Status);
    }

    [Fact]
    public async Task The_hook_is_asked_to_cancel_on_chords()
    {
        // Right Ctrl is half of every Ctrl shortcut; a chord ends the hold.
        using var rig = new Rig();

        await rig.EnableAsync();

        Assert.True(rig.Platform.Trigger.CancelOnChord);
    }

    [Fact]
    public async Task Escape_from_the_hook_cancels_the_hold()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();
        rig.Dispatcher.Drain();
        Assert.True(rig.Platform.Trigger.DictationInFlight);

        rig.Platform.Trigger.PendingCancel = TriggerCancel.Escape;
        rig.Platform.Trigger.Down = false;
        rig.Controller.Tick();
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.False(rig.Platform.Microphone.Open);
        Assert.False(rig.Platform.Trigger.DictationInFlight);
    }

    // ─── What the window says ───────────────────────────────────────────

    [Fact]
    public async Task A_microphone_Windows_blocks_is_named_with_the_way_to_fix_it()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        rig.Platform.Microphone.StartSucceeds = false;

        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.OffersMicrophoneSettings);

        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.CanPause);
    }

    [Fact]
    public async Task Enabling_with_the_microphone_blocked_says_so_straight_away()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;

        await rig.EnableAsync();

        Assert.True(rig.Controller.IsEnabled);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
    }

    // ─── The notification-area icon ─────────────────────────────────────

    [Fact]
    public async Task The_icon_shows_the_words_going_in_after_the_microphone_closes()
    {
        // With the window hidden the icon is the only sign of a hold. It used
        // to fall back to Ready at the release, so a decode and a dead hotkey
        // looked the same, and so did the moment a press is refused.
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        using var pasteHold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        rig.Platform.Paste.Hold = pasteHold;
        await rig.EnableAsync();
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);

        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        // The menu's Pause is inactive under a dictation, as the Mac's is;
        // its Cancel is what ends one (TrayMenuTests).
        Assert.Equal(new DictationTrayState(TrayIcon.Transcribing, DictationCopy.TrayTranscribing, DictationCopy.PauseLabel, false, CanCancel: true),
            rig.View.Last.Tray);

        hold.Set();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Inserting);
        Assert.Equal(TrayIcon.Transcribing, rig.View.Last.Tray.Icon);
        Assert.Equal(DictationCopy.TrayTyping, rig.View.Last.Tray.ToolTip);

        pasteHold.Set();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public void The_transcribing_picture_is_a_waveform_in_the_taskbar_s_ink()
    {
        // Between the microphone's capsule and its right-hand arm: inked in
        // the microphone, a gap between two bars of the waveform.
        const double pixel = 1.0 / 32;
        Assert.Equal(1.0, TrayIconArt.Coverage(TrayIcon.Ready, 0.42, 0.35, pixel));
        Assert.Equal(0.0, TrayIconArt.Coverage(TrayIcon.Transcribing, 0.42, 0.35, pixel));
        // The microphone's base, which the waveform leaves clear.
        Assert.Equal(1.0, TrayIconArt.Coverage(TrayIcon.Ready, 0.38, 0.88, pixel));
        Assert.Equal(0.0, TrayIconArt.Coverage(TrayIcon.Transcribing, 0.38, 0.88, pixel));
        // The middle bar, and the corner above it.
        Assert.Equal(1.0, TrayIconArt.Coverage(TrayIcon.Transcribing, 0.5, 0.5, pixel));
        Assert.Equal(0.0, TrayIconArt.Coverage(TrayIcon.Transcribing, 0.5, 0.04, pixel));

        // Not red: the microphone is closed. The ink follows the taskbar.
        var transcribing = TrayIconArt.Render(TrayIcon.Transcribing, lightTaskbar: true);
        Assert.NotEqual(TrayIconArt.Render(TrayIcon.Ready, lightTaskbar: true), transcribing);
        Assert.Equal((0x1B, 0x1B, 0x1B, 255), CentrePixel(transcribing));
        Assert.Equal((0xFF, 0xFF, 0xFF, 255), CentrePixel(TrayIconArt.Render(TrayIcon.Transcribing, lightTaskbar: false)));
    }

    /// <summary>BGRA of the centre pixel of the 32-pixel image, the icon's last.</summary>
    private static (int B, int G, int R, int A) CentrePixel(byte[] ico)
    {
        var entry = 6 + 16 * (TrayIconArt.Sizes.Count - 1);
        var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 12));
        // Bottom-up rows after the 40-byte header; row 16 of 32 from the top.
        var at = offset + 40 + ((32 - 1 - 16) * 32 + 16) * 4;
        return (ico[at], ico[at + 1], ico[at + 2], ico[at + 3]);
    }

    // ─── Coming back from the privacy page ──────────────────────────────

    [Fact]
    public async Task Access_turned_on_while_the_window_was_away_clears_the_blocked_sentence()
    {
        // The user follows the link, turns microphone access on and comes
        // back. The window used to keep accusing Windows until the next hold.
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();

        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.False(rig.View.Last.OffersMicrophoneSettings);
    }

    [Fact]
    public async Task Check_again_with_access_still_off_says_so_and_a_return_alone_says_nothing()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();

        var renders = rig.View.States.Count;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(renders, rig.View.States.Count);

        rig.Controller.RecheckMicrophone(checkAgain: true);
        Assert.Equal(DictationCopy.MicrophoneStillBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);

        // And once it is on, the "still" sentence clears like the first one.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
    }

    [Fact]
    public async Task A_policy_lifted_while_the_window_was_away_clears_its_sentence_too()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.DeniedByPolicy;
        await rig.EnableAsync();
        Assert.Equal(DictationCopy.MicrophoneBlockedByPolicy, rig.View.Last.Status);

        rig.Controller.RecheckMicrophone(checkAgain: true);
        Assert.Equal(DictationCopy.MicrophoneBlockedByPolicy, rig.View.Last.Status);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
    }

    [Fact]
    public async Task Rechecking_leaves_every_other_status_line_alone()
    {
        // Activation happens all day; it may only ever retract the one
        // sentence a privacy switch can make untrue.
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(v => v.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        var renders = rig.View.States.Count;
        var probes = rig.Platform.Access.Probes;

        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        rig.Controller.RecheckMicrophone();
        rig.Controller.RecheckMicrophone(checkAgain: true);

        Assert.Equal(renders, rig.View.States.Count);
        Assert.Equal(probes, rig.Platform.Access.Probes);
    }

    [Fact]
    public async Task A_probe_that_throws_on_return_leaves_the_sentence_and_the_window_up()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();

        rig.Platform.Access.Throws = true;
        rig.Controller.RecheckMicrophone(checkAgain: true);

        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
    }

    [Fact]
    public async Task An_unexpected_failure_while_enabling_shows_a_fixed_sentence()
    {
        using var rig = new Rig();
        rig.Platform.WorkerFailure = new InvalidOperationException(@"C:\secret\path exploded");

        await rig.EnableAsync();

        Assert.Equal(DictationCopy.EnableFailed, rig.View.Last.Status);
        Assert.DoesNotContain("exploded", rig.View.Last.Status, StringComparison.Ordinal);
        Assert.True(rig.View.Last.CanEnable);
    }

    [Fact]
    public async Task A_recogniser_that_will_not_load_says_so()
    {
        using var rig = new Rig();
        rig.Platform.Worker.InitSucceeds = false;

        await rig.EnableAsync();

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
    }

    [Fact]
    public async Task Holding_the_key_opens_the_microphone_and_marks_the_hold_in_flight()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        rig.Press();
        rig.Dispatcher.Drain();

        Assert.True(rig.Platform.Microphone.Open);
        Assert.False(rig.View.Last.CanEnable);
        Assert.True(rig.Platform.Trigger.DictationInFlight);
    }
}
