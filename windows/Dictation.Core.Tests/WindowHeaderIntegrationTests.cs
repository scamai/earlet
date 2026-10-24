using System.Buffers.Binary;
using Dictation.Services;
using Dictation.Speech;
using static Dictation.Core.Tests.DictationHudGeometryTests;
using Hook = Dictation.Core.Tests.HookWatchControllerTests.Hook;

namespace Dictation.Core.Tests;

/// <summary>
/// The window's status header with what landed beside it: the hook watch's
/// three states and the microphone watch in the one state the icon is drawn
/// from, in every render; the status line under the headline never saying
/// the opposite (the Mac header, design-macos.md "Status header and menu bar
/// glyph"); the watches, their one-time reveals and the indicator's timers
/// with the window closed to the notification area; and the glyph under High
/// Contrast, drawn as the icon is there.
/// </summary>
public sealed class WindowHeaderIntegrationTests
{
    private static readonly HotkeyBinding Key = DictationHotkeys.PushToTalk;

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    // ─── One state for the header and the icon, every state ────────────

    /// <summary>Every state, as a reader would check it: the icon's picture
    /// and tooltip beside the header's headline and tint.</summary>
    private static readonly (DictationGlyph Glyph, TrayIcon Picture, string ToolTip, string Headline, DictationGlyphTint Tint)[] Every =
    [
        (DictationGlyph.Off, TrayIcon.Off, "Earlet — off", "Earlet is off", DictationGlyphTint.Secondary),
        (DictationGlyph.Starting, TrayIcon.Off, "Earlet — starting…", "Starting dictation…", DictationGlyphTint.Secondary),
        (DictationGlyph.Ready, TrayIcon.Ready, "Earlet — hold Right Ctrl to dictate", "Earlet is on", DictationGlyphTint.Positive),
        (DictationGlyph.Listening, TrayIcon.Listening, "Earlet — listening", "Listening…", DictationGlyphTint.Recording),
        (DictationGlyph.Transcribing, TrayIcon.Transcribing, "Earlet — transcribing", "Transcribing…", DictationGlyphTint.Accent),
        (DictationGlyph.Typing, TrayIcon.Transcribing, "Earlet — typing", "Typing…", DictationGlyphTint.Accent),
        (DictationGlyph.MicrophoneBlocked, TrayIcon.MicrophoneBlocked, "Earlet — Windows is blocking the microphone",
            "Microphone blocked", DictationGlyphTint.Attention),
        (DictationGlyph.HoldKeyUnheard, TrayIcon.MicrophoneBlocked, "Earlet — can't hear Right Ctrl, open Earlet",
            "Can't hear the hold key", DictationGlyphTint.Attention),
        (DictationGlyph.AdminWindowInFront, TrayIcon.Ready,
            "Earlet — can't hear Right Ctrl while an administrator window is in front",
            "Can't hear keys in administrator windows", DictationGlyphTint.Positive),
        (DictationGlyph.HoldKeyReconnected, TrayIcon.Ready, "Earlet — reconnected Right Ctrl, hold it again",
            "Reconnected — hold the key again", DictationGlyphTint.Positive),
    ];

    [Fact]
    public void Every_state_the_icon_can_show_has_a_headline_that_says_what_its_tooltip_says()
    {
        // Every state is in the table, the hook watch's three included, so a
        // state added to the icon without a headline of its own fails here.
        Assert.Equal(Enum.GetValues<DictationGlyph>().Order(), Every.Select(row => row.Glyph).Order());
        foreach (var (glyph, picture, toolTip, headline, tint) in Every)
        {
            Assert.Equal((picture, toolTip), DictationTrayGlyph.Picture(glyph, Key));
            Assert.Equal(headline, DictationTrayGlyph.Headline(glyph));
            Assert.Equal(tint, DictationTrayGlyph.Tint(glyph));
            Assert.InRange(headline.Length, 1, 40);
        }

        // Narrator tells every state apart by its headline, as by its tooltip.
        Assert.Equal(Every.Length, Every.Select(row => row.Headline).Distinct().Count());
        Assert.Equal(Every.Length, Every.Select(row => row.ToolTip).Distinct().Count());
    }

    [Fact]
    public void The_header_s_tint_is_the_family_of_the_icon_s_picture_in_every_state()
    {
        // The warning is orange, the open microphone red, the waveform the
        // accent, the ready microphone green and the off one grey: a state
        // drawn with the warning cannot be tinted as fine, nor the reverse.
        foreach (var glyph in Enum.GetValues<DictationGlyph>())
        {
            var family = DictationTrayGlyph.IconOf(glyph) switch
            {
                TrayIcon.MicrophoneBlocked => DictationGlyphTint.Attention,
                TrayIcon.Listening => DictationGlyphTint.Recording,
                TrayIcon.Transcribing => DictationGlyphTint.Accent,
                TrayIcon.Ready => DictationGlyphTint.Positive,
                _ => DictationGlyphTint.Secondary,
            };
            Assert.True(family == DictationTrayGlyph.Tint(glyph), $"{glyph}: {DictationTrayGlyph.Tint(glyph)}, picture {family}");
        }
    }

    /// <summary>Every render: the header's glyph is the state the icon was
    /// drawn from, with its headline.</summary>
    private static void AssertHeaderIsTheIcon(Rig rig)
    {
        foreach (var view in rig.View.States)
        {
            Assert.Equal(DictationTrayGlyph.Picture(view.Header.Glyph, rig.Controller.HoldKey), (view.Tray.Icon, view.Tray.ToolTip));
            Assert.Equal(DictationTrayGlyph.Headline(view.Header.Glyph), view.Header.Headline);
        }
    }

    [Fact]
    public async Task Through_an_administrator_window_a_hook_not_put_back_and_one_reconnected_the_header_says_what_the_tooltip_says()
    {
        using var hook = new Hook();
        await hook.EnableAsync();

        hook.Foreground.Elevated.Add(200);
        hook.Foreground.Process = 200;
        hook.Poll(600);
        Assert.Equal(DictationGlyph.AdminWindowInFront, hook.Shown.Header.Glyph);
        Assert.Equal("Can't hear keys in administrator windows", hook.Shown.Header.Headline);
        hook.Foreground.Process = 100;
        hook.Poll(600);
        Assert.Equal(DictationGlyph.Ready, hook.Shown.Header.Glyph);

        hook.Trigger.ReinstallSucceeds = false;
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        Assert.Equal(DictationGlyph.HoldKeyUnheard, hook.Shown.Header.Glyph);
        Assert.Equal(DictationCopy.HoldKeyUnheard(Key), hook.Status);
        hook.LetGo();
        hook.Poll(1_500);

        hook.Trigger.ReinstallSucceeds = true;
        hook.Hold();
        hook.Poll(1_000);
        Assert.Equal(DictationGlyph.HoldKeyReconnected, hook.Shown.Header.Glyph);
        Assert.Equal("Reconnected — hold the key again", hook.Shown.Header.Headline);
        hook.LetGo();
        hook.Poll(600);
        Assert.Equal(DictationGlyph.Ready, hook.Shown.Header.Glyph);

        hook.Dictate();
        Assert.Contains(hook.Rig.View.States, v => v.Header.Glyph == DictationGlyph.Listening);
        AssertHeaderIsTheIcon(hook.Rig);
        AssertLineAgreesWithHeadline(hook.Rig);
    }

    // ─── The status line under the headline ─────────────────────────────

    /// <summary>Every sentence that puts a block on the status line.</summary>
    private static readonly string[] BlockedLines =
    [
        DictationCopy.MicrophoneBlocked, DictationCopy.MicrophoneBlockedByPolicy, DictationCopy.MicrophoneStillBlocked,
        DictationCopy.PausedMicrophoneBlocked, DictationCopy.PausedMicrophoneBlockedByPolicy,
    ];

    /// <summary>
    /// Every render, with no dictation under way (whose own lines speak for
    /// it): a status line that says the microphone is blocked is under
    /// "Microphone blocked"; one that says the key cannot be heard is under
    /// that or the microphone, which outranks it; and under either of those
    /// the line never invites a hold, which would meet the same wall. The
    /// Mac's header shows its setup step whenever a grant is missing and
    /// nothing is recording.
    /// </summary>
    private static void AssertLineAgreesWithHeadline(Rig rig)
    {
        foreach (var view in rig.View.States)
        {
            var glyph = view.Header.Glyph;
            if (glyph is DictationGlyph.Listening or DictationGlyph.Transcribing or DictationGlyph.Typing)
            {
                continue;
            }

            var key = view.HoldKey;
            var blocked = BlockedLines.Any(line => view.Status.StartsWith(line, StringComparison.Ordinal));
            var unheard = view.Status.Contains(DictationCopy.HoldKeyUnheard(key), StringComparison.Ordinal);
            Assert.True(!blocked || glyph == DictationGlyph.MicrophoneBlocked, $"\"{view.Status}\" under {glyph}");
            Assert.True(
                !unheard || glyph is DictationGlyph.MicrophoneBlocked or DictationGlyph.HoldKeyUnheard,
                $"\"{view.Status}\" under {glyph}");
            if (glyph is DictationGlyph.MicrophoneBlocked or DictationGlyph.HoldKeyUnheard)
            {
                Assert.DoesNotContain(view.Status, new[] { DictationCopy.Armed(key), DictationCopy.WorkerRestarted(key) });
            }
        }
    }

    [Fact]
    public async Task A_new_hold_key_chosen_while_the_microphone_is_blocked_keeps_the_blocked_sentence_and_its_buttons()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.Equal(DictationGlyph.MicrophoneBlocked, rig.View.Last.Header.Glyph);

        rig.Controller.ChooseHoldKey(DictationHotkeys.VkRightAlt);

        // Not "Enabled. Hold Right Alt in a text field." under "Microphone
        // blocked": a hold on the new key would meet the same block.
        Assert.Equal("Right Alt", rig.Controller.HoldKey.KeyName);
        Assert.Equal(DictationGlyph.MicrophoneBlocked, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);

        // Access back: the line goes back to what dictation is doing, on the new key.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(DictationGlyph.Ready, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        AssertLineAgreesWithHeadline(rig);
        AssertHeaderIsTheIcon(rig);
    }

    [Fact]
    public async Task Speech_recognition_restarted_under_a_blocked_microphone_says_the_block_not_hold_to_dictate()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();

        rig.Platform.Worker.Crash();
        rig.Controller.Tick();
        Assert.Equal(DictationCopy.WorkerStoppedWhileIdle, rig.View.Last.Status);
        rig.TickUntil(() => rig.Platform.CreateWorkerCalls == 2 && rig.View.Last.Status != DictationCopy.WorkerStoppedWhileIdle);

        Assert.Equal(DictationGlyph.MicrophoneBlocked, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationCopy.MicrophoneBlocked, rig.View.Last.Status);
        Assert.True(rig.View.Last.OffersMicrophoneSettings);
        AssertLineAgreesWithHeadline(rig);
    }

    [Fact]
    public async Task The_microphone_and_the_hook_on_one_line_sit_under_the_headline_of_whichever_outranks()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        hook.Trigger.ReinstallSucceeds = false;
        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        hook.LetGo();
        hook.Poll(600);
        Assert.Equal(DictationGlyph.HoldKeyUnheard, hook.Shown.Header.Glyph);

        // The microphone goes too: its sentence first, under its headline.
        hook.Rig.Platform.Access.Access = MicrophoneAccess.Denied;
        hook.Poll(5_100);
        Assert.Equal(DictationCopy.MicrophoneBlocked + " " + DictationCopy.HoldKeyUnheard(Key), hook.Status);
        Assert.Equal(DictationGlyph.MicrophoneBlocked, hook.Shown.Header.Glyph);

        // Check again keeps both, and the microphone's headline.
        hook.Rig.Controller.RecheckMicrophone(checkAgain: true);
        Assert.Equal(DictationGlyph.MicrophoneBlocked, hook.Shown.Header.Glyph);

        // Access back: the key's sentence alone, under the key's headline.
        hook.Rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        hook.Poll(1_100);
        Assert.Equal(DictationCopy.HoldKeyUnheard(Key), hook.Status);
        Assert.Equal(DictationGlyph.HoldKeyUnheard, hook.Shown.Header.Glyph);

        AssertLineAgreesWithHeadline(hook.Rig);
        AssertHeaderIsTheIcon(hook.Rig);
    }

    // ─── Closed to the notification area ────────────────────────────────

    /// <summary>
    /// The window's timer as MainWindow runs it: a tick, then the interval
    /// the controller asks for (<see cref="DictationController.PollInterval"/>,
    /// set by PacePoll after every tick), and so on. How long from the start
    /// until the tick after which <paramref name="done"/> holds.
    /// </summary>
    private static TimeSpan RunTimer(Rig rig, TimeSpan limit, Func<bool> done)
    {
        var elapsed = TimeSpan.Zero;
        while (elapsed <= limit)
        {
            var wait = rig.Controller.PollInterval;
            rig.Clock.Advance(wait);
            elapsed += wait;
            rig.Controller.Tick();
            rig.Dispatcher.Drain();
            if (done())
            {
                return elapsed;
            }
        }

        throw new Xunit.Sdk.XunitException($"not done within {limit}");
    }

    [Fact]
    public async Task Closed_while_on_both_watches_keep_watching_and_each_cause_still_brings_the_window_forward_once()
    {
        using var hook = new Hook();
        await hook.EnableAsync();
        Assert.Equal(DictationCloseAction.Hide, hook.Rig.Controller.RequestClose(quit: false).Action);
        Assert.Equal(DictationController.BusyPoll, hook.Rig.Controller.PollInterval);

        hook.Rig.Platform.Access.Access = MicrophoneAccess.Denied;
        hook.Poll(5_100);
        Assert.Equal(1, hook.Reveals);
        Assert.Equal(DictationGlyph.MicrophoneBlocked, hook.Shown.Header.Glyph);

        hook.Trigger.HookAlive = false;
        hook.Hold();
        hook.Poll(1_000);
        Assert.Equal(1, hook.Trigger.Reinstalls);
        Assert.Equal(2, hook.Reveals);

        // Neither asks again while its cause stays.
        hook.LetGo();
        hook.Poll(3_000);
        Assert.Equal(2, hook.Reveals);
    }

    [Fact]
    public async Task Closed_while_off_the_heartbeat_still_heals_the_indicator_and_the_microphone_watch_still_clears_a_block()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.True(rig.Controller.TryPause());
        Assert.Equal(DictationCloseAction.Hide, rig.Controller.RequestClose(quit: false).Action);
        Assert.Equal(DictationController.RestingPoll, rig.Controller.PollInterval);
        var reveals = rig.View.RevealRequests;

        // A pill the window kept after the feed took it down: the heartbeat,
        // once a second, takes it away at the resting poll too.
        rig.Hud.Stray(new DictationHudModel("Typed", HudTone.Neutral, null));
        Assert.InRange(RunTimer(rig, TimeSpan.FromSeconds(5), () => rig.Hud.Current is null), TimeSpan.Zero, DictationController.RestingPoll);

        // Access back: the warning goes within about a second, without
        // bringing the window forward.
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        Assert.InRange(
            RunTimer(rig, TimeSpan.FromSeconds(5), () => rig.View.Last.Header.Glyph == DictationGlyph.Off),
            TimeSpan.Zero,
            DictationController.RestingPoll);
        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
        Assert.Equal(reveals, rig.View.RevealRequests);

        // Enabled from the menu while closed, into a block again: the window comes forward.
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.Controller.ToggleAsync();
        rig.Dispatcher.Drain();
        Assert.Equal(DictationGlyph.MicrophoneBlocked, rig.View.Last.Header.Glyph);
        Assert.Equal(reveals + 1, rig.View.RevealRequests);
    }

    [Fact]
    public async Task Closed_while_off_a_pill_keeps_the_fast_poll_so_it_goes_at_its_dwell_and_then_the_poll_rests()
    {
        var copy = new RecentResultTests.HeldCopy();
        using var rig = new Rig(options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.True(rig.Controller.TryPause());
        rig.Controller.RequestClose(quit: false);

        rig.Controller.CopyLastText();
        RecentResultTests.Answer(rig, copy);
        var copied = Assert.IsType<DictationHudModel>(rig.Hud.Current);
        var dwell = Assert.IsType<TimeSpan>(copied.Dwell);
        Assert.Equal(DictationController.BusyPoll, rig.Controller.PollInterval);

        // At the resting poll the pill would stand up to a second past its dwell.
        Assert.InRange(
            RunTimer(rig, dwell + TimeSpan.FromSeconds(3), () => rig.Hud.Current is null),
            dwell,
            dwell + DictationController.BusyPoll);
        Assert.Equal(DictationController.RestingPoll, rig.Controller.PollInterval);
    }

    [Fact]
    public void The_window_hides_on_close_without_stopping_its_poll_and_a_reveal_shows_a_hidden_window_not_only_a_minimized_one()
    {
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

        var closing = Member(code, "private void OnClosing(");
        Assert.Contains("case DictationCloseAction.Hide:", closing, StringComparison.Ordinal);
        Assert.Contains("Hide();", closing, StringComparison.Ordinal);
        // The timer that drives both watches and the indicator runs on.
        Assert.DoesNotContain("poll.", closing, StringComparison.Ordinal);
        Assert.DoesNotContain("poll.Stop()", Member(code, "public MainWindow("), StringComparison.Ordinal);

        Assert.Contains("void IDictationView.Reveal() => ShowFromOutside();", code, StringComparison.Ordinal);
        var reveal = Member(code, "public void ShowFromOutside()");
        var show = reveal.IndexOf("Show();", StringComparison.Ordinal);
        var restore = reveal.IndexOf("WindowState = WindowState.Normal;", StringComparison.Ordinal);
        Assert.True(show >= 0 && restore > show, "Show() first, so a window closed to the tray comes back, then un-minimize");
        Assert.Contains("Activate();", reveal, StringComparison.Ordinal);
    }

    // ─── High Contrast ──────────────────────────────────────────────────

    /// <summary>A contrast theme's three colours, all different here so the
    /// ink each picture takes can be read back.</summary>
    private static readonly TrayInks Contrast = TrayInks.HighContrast(0xFF00FF00, 0xFF0000FF, 0xFF808080);

    [Theory]
    [InlineData(TrayIcon.Off)]
    [InlineData(TrayIcon.Ready)]
    [InlineData(TrayIcon.Listening)]
    [InlineData(TrayIcon.Transcribing)]
    [InlineData(TrayIcon.MicrophoneBlocked)]
    public void Under_High_Contrast_the_header_glyph_is_the_icon_s_contrast_picture_in_the_icon_s_ink(TrayIcon picture)
    {
        // The icon's 32-pixel image under High Contrast, which is drawn at full
        // strength: its alpha is the coverage, pixel for pixel the header's mask.
        var ico = TrayIconArt.Render(picture, Contrast);
        var entry = 6 + 16 * (TrayIconArt.Sizes.Count - 1);
        Assert.Equal(32, ico[entry]);
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 12)) + 40;
        var mask = TrayIconArt.Mask(picture, 32, byShape: true);
        var ink = TrayIconArt.InkOf(picture) switch
        {
            TrayInk.Recording => Contrast.RecordingInk,
            TrayInk.Paused => Contrast.PausedInk,
            _ => Contrast.Ink,
        };

        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                var at = offset + ((31 - y) * 32 + x) * 4;
                Assert.Equal(mask[y * 32 + x], ico[at + 3]);
                if (ico[at + 3] > 0)
                {
                    // The colour the header's resource reference names is the
                    // one the icon is drawn in.
                    Assert.Equal((byte)ink, ico[at]);
                    Assert.Equal((byte)(ink >> 8), ico[at + 1]);
                    Assert.Equal((byte)(ink >> 16), ico[at + 2]);
                }
            }
        }
    }

    [Fact]
    public void Under_High_Contrast_off_ready_and_listening_are_three_shapes_in_the_header_whatever_the_theme_s_colours()
    {
        // A contrast theme may give its text, selection and disabled text one
        // colour; the Mac's mic.slash, mic and mic.fill still tell them apart.
        var shapes = new[] { TrayIcon.Off, TrayIcon.Ready, TrayIcon.Listening, TrayIcon.Transcribing, TrayIcon.MicrophoneBlocked }
            .Select(p => Convert.ToBase64String(TrayIconArt.Mask(p, 32, byShape: true)))
            .ToHashSet();
        Assert.Equal(5, shapes.Count);
        Assert.Equal(TrayInk.Paused, TrayIconArt.InkOf(TrayIcon.Off));
        Assert.Equal(TrayInk.Recording, TrayIconArt.InkOf(TrayIcon.Listening));
        Assert.All(new[] { TrayIcon.Ready, TrayIcon.Transcribing, TrayIcon.MicrophoneBlocked },
            p => Assert.Equal(TrayInk.Ink, TrayIconArt.InkOf(p)));
    }

    [Fact]
    public void The_window_draws_the_glyph_by_shape_in_the_icon_s_system_colour_under_High_Contrast_and_repaints_on_a_switch()
    {
        var code = File.ReadAllText(AppFile("MainWindow.Header.cs")).ReplaceLineEndings("\n");
        var glyph = Member(code, "private void RenderGlyph(");

        // Windows' setting, unless the render mode draws an appearance of its own.
        Assert.Contains("var highContrast = highContrastForRender ?? SystemParameters.HighContrast;", glyph, StringComparison.Ordinal);
        Assert.Contains("StateGlyph.OpacityMask = MaskFor(picture, byShape: highContrast);", glyph, StringComparison.Ordinal);
        var contrast = glyph.IndexOf("if (highContrast)", StringComparison.Ordinal);
        var tint = glyph.IndexOf("switch (DictationTrayGlyph.Tint(glyph))", StringComparison.Ordinal);
        Assert.True(contrast >= 0 && tint > contrast, "High Contrast first");
        var roles = glyph[contrast..tint];
        Assert.Contains("TrayIconArt.InkOf(picture) switch", roles, StringComparison.Ordinal);
        Assert.Contains("TrayInk.Recording => SystemColors.HighlightBrushKey,", roles, StringComparison.Ordinal);
        Assert.Contains("TrayInk.Paused => SystemColors.GrayTextBrushKey,", roles, StringComparison.Ordinal);
        Assert.Contains("_ => SystemColors.WindowTextBrushKey,", roles, StringComparison.Ordinal);
        Assert.Contains("TrayIconArt.Mask(picture, GlyphPixels, byShape)", Member(code, "private ImageBrush MaskFor("), StringComparison.Ordinal);

        // The tray picks the same system colours, in the same roles.
        var tray = File.ReadAllText(AppFile("DictationTray.cs")).ReplaceLineEndings("\n");
        Assert.Contains(
            "Argb(System.Drawing.SystemColors.WindowText), Argb(System.Drawing.SystemColors.Highlight),\n"
            + "            Argb(System.Drawing.SystemColors.GrayText)",
            tray,
            StringComparison.Ordinal);
    }
}
