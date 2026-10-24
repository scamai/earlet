using System.Buffers.Binary;
using System.Reflection;
using System.Xml.Linq;
using Dictation.Services;

namespace Dictation.Core.Tests;

/// <summary>
/// The app as a resident of the desktop: the window closes to the
/// notification area while dictation is on, the icon follows the state,
/// Dictation can start at sign-in, and the log folder is one click away.
/// </summary>
public sealed class DictationDesktopTests
{
    // ─── Closing and the notification area ──────────────────────────────

    [Fact]
    public async Task Closing_the_window_with_dictation_on_hides_it_and_explains_once()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        var first = rig.Controller.RequestClose(quit: false);
        var second = rig.Controller.RequestClose(quit: false);

        Assert.Equal(DictationCloseAction.Hide, first.Action);
        Assert.Equal(DictationCopy.StillRunningInTray, first.Notice);
        Assert.Equal(DictationCloseAction.Hide, second.Action);
        Assert.Null(second.Notice);
        Assert.True(rig.Settings.Load().TrayNoticeShown);

        // Still on, and still dictating.
        Assert.True(rig.Controller.IsEnabled);
        rig.Dictate(v => v.Status.StartsWith("Text inserted", StringComparison.Ordinal));

        // Once per install, not once per launch.
        using var next = new Rig(beforeStart: r => r.Settings.Save(rig.Settings.Load()));
        await next.EnableAsync();
        Assert.Null(next.Controller.RequestClose(quit: false).Notice);
    }

    /// <summary>
    /// Closing the window never quits, on or off, as closing the Mac's never
    /// does: it used to exit while dictation was off, which threw away the
    /// recent result Copy last text still offered and the learning receipt's
    /// Undo. Quit, in the notification-area menu, is how to exit.
    /// </summary>
    [Fact]
    public async Task Closing_the_window_with_dictation_off_hides_it_too_and_explains_once()
    {
        using var rig = new Rig();

        var first = rig.Controller.RequestClose(quit: false);
        var second = rig.Controller.RequestClose(quit: false);

        Assert.Equal(DictationCloseAction.Hide, first.Action);
        Assert.Equal(DictationCopy.StillInTrayWhileOff, first.Notice);
        Assert.Equal(DictationCloseAction.Hide, second.Action);
        Assert.Null(second.Notice);
        Assert.True(rig.Settings.Load().TrayNoticeShown);

        // Still running: the menu's Enable works from the notification area,
        // and closing again with dictation on says nothing more.
        await rig.Controller.ToggleAsync();
        rig.Dispatcher.Drain();
        Assert.True(rig.Controller.IsEnabled);
        Assert.Equal(new DictationCloseDecision(DictationCloseAction.Hide), rig.Controller.RequestClose(quit: false));
        Assert.Equal(DictationCloseAction.Close, rig.Controller.RequestClose(quit: true).Action);
    }

    [Fact]
    public async Task Closing_the_window_while_paused_keeps_the_recent_result_for_Copy_last_text()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(v => v.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        var typed = rig.Platform.Clipboard.Writes[0];
        Assert.True(rig.Controller.TryPause());

        var close = rig.Controller.RequestClose(quit: false);

        Assert.Equal(DictationCloseAction.Hide, close.Action);
        Assert.Equal(DictationCopy.StillInTrayWhileOff, close.Notice);
        Assert.True(rig.Controller.HoldsRecentText);
        Assert.NotNull(rig.View.Last.Recent);
        Assert.NotNull(rig.View.Last.Tray.Recent);

        // The menu's Copy last text, with the window hidden.
        rig.Controller.CopyLastText();
        rig.Dispatcher.Drain();
        Assert.Equal(typed, rig.Platform.Clipboard.Text);

        // Quit still discards it, as on the Mac.
        Assert.Equal(DictationCloseAction.Close, rig.Controller.RequestClose(quit: true).Action);
        rig.Controller.Dispose();
        Assert.False(rig.Controller.HoldsRecentText);
    }

    [Fact]
    public async Task The_notice_is_shown_once_per_install_whether_the_first_close_was_on_or_off()
    {
        using var rig = new Rig();
        Assert.Equal(DictationCopy.StillInTrayWhileOff, rig.Controller.RequestClose(quit: false).Notice);

        using var next = new Rig(beforeStart: r => r.Settings.Save(rig.Settings.Load()));
        await next.EnableAsync();
        Assert.Null(next.Controller.RequestClose(quit: false).Notice);
        Assert.True(next.Controller.TryPause());
        Assert.Null(next.Controller.RequestClose(quit: false).Notice);
    }

    [Fact]
    public void Closing_after_quitting_began_closes()
    {
        // A window closed once the controller has let go (the app is on its
        // way out) is not hidden into a notification area that is gone.
        using var rig = new Rig();
        rig.Controller.Dispose();

        Assert.Equal(DictationCloseAction.Close, rig.Controller.RequestClose(quit: false).Action);
    }

    [Fact]
    public async Task Quit_exits_with_dictation_on()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        Assert.Equal(DictationCloseAction.Close, rig.Controller.RequestClose(quit: true).Action);
    }

    [Fact]
    public async Task The_icon_follows_the_state()
    {
        using var rig = new Rig();
        Assert.Equal(new DictationTrayState(TrayIcon.Off, DictationCopy.TrayOff, DictationCopy.EnableLabel, true),
            rig.View.Last.Tray);

        await rig.EnableAsync();
        Assert.Equal(
            new DictationTrayState(TrayIcon.Ready, "Earlet — hold Right Ctrl to dictate", DictationCopy.PauseLabel, true),
            rig.View.Last.Tray);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.Equal(TrayIcon.Listening, rig.View.Last.Tray.Icon);
        Assert.Equal(DictationCopy.TrayListening, rig.View.Last.Tray.ToolTip);

        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        Assert.Equal(TrayIcon.Ready, rig.View.Last.Tray.Icon);
    }

    [Fact]
    public async Task The_menu_toggle_enables_and_pauses()
    {
        using var rig = new Rig();

        await rig.Controller.ToggleAsync();
        rig.Dispatcher.Drain();
        Assert.True(rig.Controller.IsEnabled);

        await rig.Controller.ToggleAsync();
        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);
        Assert.False(rig.Settings.Load().Enabled);
    }

    [Fact]
    public async Task The_menu_cannot_pause_while_the_text_goes_in()
    {
        using var rig = new Rig();
        using var pasteHold = new ManualResetEventSlim();
        rig.Platform.Paste.Hold = pasteHold;
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Inserting);

        Assert.False(rig.View.Last.Tray.CanToggle);
        await rig.Controller.ToggleAsync();
        Assert.True(rig.Controller.IsEnabled);

        pasteHold.Set();
        rig.DrainUntil(() => rig.View.Last.Status.StartsWith("Text inserted", StringComparison.Ordinal));
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    // ─── A failure the hidden window must show ──────────────────────────

    [Fact]
    public async Task A_window_closed_while_enabling_comes_back_when_the_enable_fails()
    {
        using var rig = new Rig();
        using var load = new ManualResetEventSlim();
        rig.Platform.Worker.HoldInit = load;
        rig.Platform.Worker.InitSucceeds = false;
        var enabling = rig.Controller.EnableAsync();
        Assert.Equal(DictationCopy.Preparing, rig.View.Last.Status);

        var close = rig.Controller.RequestClose(quit: false);
        Assert.Equal(DictationCloseAction.Hide, close.Action);
        Assert.Equal(DictationCopy.StillRunningInTray, close.Notice);
        Assert.Equal(0, rig.View.RevealRequests);

        load.Set();
        await enabling;

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
        Assert.Equal(1, rig.View.RevealRequests);
    }

    [Fact]
    public async Task An_enable_from_the_notification_area_that_fails_brings_the_window_forward()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        Assert.Equal(DictationCloseAction.Hide, rig.Controller.RequestClose(quit: false).Action);
        await rig.Controller.ToggleAsync();
        Assert.False(rig.Controller.IsEnabled);
        rig.Platform.Worker.InitSucceeds = false;

        await rig.Controller.ToggleAsync();

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
        Assert.Equal(1, rig.View.RevealRequests);
    }

    [Fact]
    public async Task An_enable_from_the_notification_area_that_works_pulls_nothing_forward_later()
    {
        using var rig = new Rig();
        await rig.Controller.ToggleAsync();
        Assert.True(rig.Controller.IsEnabled);
        rig.Controller.Pause();
        rig.Platform.Worker.InitSucceeds = false;

        // The window's own button, with the window in front.
        await rig.EnableAsync();

        Assert.Equal(DictationCopy.ModelDidNotLoad, rig.View.Last.Status);
        Assert.Equal(0, rig.View.RevealRequests);
    }

    // ─── The icon's pixels ──────────────────────────────────────────────

    internal static (int Size, byte[] Pixels) Image(byte[] ico, int index)
    {
        var entry = 6 + 16 * index;
        var size = ico[entry] == 0 ? 256 : ico[entry];
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(ico.AsSpan(entry + 12));
        return (size, ico.AsSpan(offset + 40, size * size * 4).ToArray());
    }

    /// <summary>BGRA at (x, y), y down from the top.</summary>
    internal static (int B, int G, int R, int A) Pixel((int Size, byte[] Pixels) image, int x, int y)
    {
        var at = ((image.Size - 1 - y) * image.Size + x) * 4;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2], image.Pixels[at + 3]);
    }

    [Theory]
    [InlineData(TrayIcon.Off)]
    [InlineData(TrayIcon.Ready)]
    [InlineData(TrayIcon.Listening)]
    [InlineData(TrayIcon.Transcribing)]
    [InlineData(TrayIcon.MicrophoneBlocked)]
    public void The_icon_is_a_well_formed_ico_with_every_size(TrayIcon state)
    {
        var ico = TrayIconArt.Render(state, lightTaskbar: false);

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));
        Assert.Equal(TrayIconArt.Sizes.Count, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4)));
        var end = 6 + 16 * TrayIconArt.Sizes.Count;
        for (var i = 0; i < TrayIconArt.Sizes.Count; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i);
            var size = TrayIconArt.Sizes[i];
            Assert.Equal(size, entry[0]);
            Assert.Equal(size, entry[1]);
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            Assert.Equal(end, offset);
            end += length;

            var dib = ico.AsSpan(offset);
            Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(dib));
            Assert.Equal(size, BinaryPrimitives.ReadInt32LittleEndian(dib[4..]));
            Assert.Equal(size * 2, BinaryPrimitives.ReadInt32LittleEndian(dib[8..]));
            Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]));
            Assert.Equal(40 + size * size * 4 + (size + 31) / 32 * 4 * size, length);
        }

        Assert.Equal(ico.Length, end);
    }

    [Fact]
    public void The_microphone_is_drawn_and_the_corners_are_clear()
    {
        var image = Image(TrayIconArt.Render(TrayIcon.Ready, lightTaskbar: false), TrayIconArt.Sizes.Count - 1);

        Assert.Equal(32, image.Size);
        // The capsule's centre, and the stem.
        Assert.Equal(255, Pixel(image, 16, 11).A);
        Assert.Equal(255, Pixel(image, 16, 25).A);
        Assert.Equal(0, Pixel(image, 0, 0).A);
        Assert.Equal(0, Pixel(image, 31, 31).A);
        Assert.Equal(0, Pixel(image, 31, 0).A);
    }

    [Fact]
    public void Recording_is_red_paused_is_faded_and_the_ink_follows_the_taskbar()
    {
        var onDark = Image(TrayIconArt.Render(TrayIcon.Ready, lightTaskbar: false), 3);
        var onLight = Image(TrayIconArt.Render(TrayIcon.Ready, lightTaskbar: true), 3);
        var recording = Image(TrayIconArt.Render(TrayIcon.Listening, lightTaskbar: false), 3);
        var paused = Image(TrayIconArt.Render(TrayIcon.Off, lightTaskbar: false), 3);

        Assert.Equal((255, 255, 255, 255), Pixel(onDark, 16, 11));
        Assert.Equal((0x1B, 0x1B, 0x1B, 255), Pixel(onLight, 16, 11));
        Assert.Equal((0x23, 0x11, 0xE8, 255), Pixel(recording, 16, 11));
        Assert.Equal((int)TrayIconArt.PausedAlpha, Pixel(paused, 16, 11).A);
        Assert.True(paused.Pixels.Where((_, i) => i % 4 == 3).Max() <= TrayIconArt.PausedAlpha);
        Assert.Equal(
            TrayIconArt.Render(TrayIcon.Ready, lightTaskbar: false),
            TrayIconArt.Render(TrayIcon.Ready, lightTaskbar: false));
    }

    /// <summary>The Mac's "a missing grant ... draw the warning triangle"
    /// (MenuGlyphTests): <c>exclamationmark.triangle</c>, drawn here.</summary>
    [Fact]
    public void A_blocked_microphone_draws_the_warning_triangle_in_the_taskbar_s_ink()
    {
        var onDark = Image(TrayIconArt.Render(TrayIcon.MicrophoneBlocked, lightTaskbar: false), 3);
        var onLight = Image(TrayIconArt.Render(TrayIcon.MicrophoneBlocked, lightTaskbar: true), 3);

        // The triangle: its apex and its base; the corners stay clear.
        Assert.Equal(255, Pixel(onDark, 16, 2).A);
        Assert.Equal(255, Pixel(onDark, 4, 28).A);
        Assert.Equal(255, Pixel(onDark, 27, 28).A);
        Assert.Equal(0, Pixel(onDark, 0, 0).A);
        Assert.Equal(0, Pixel(onDark, 31, 0).A);
        Assert.Equal(0, Pixel(onDark, 0, 31).A);
        Assert.Equal(0, Pixel(onDark, 31, 31).A);
        // The exclamation mark: a bar, a gap, a dot, with clear space beside.
        Assert.Equal(255, Pixel(onDark, 16, 14).A);
        Assert.Equal(0, Pixel(onDark, 16, 21).A);
        Assert.Equal(255, Pixel(onDark, 16, 24).A);
        Assert.Equal(0, Pixel(onDark, 12, 18).A);
        Assert.Equal(0, Pixel(onDark, 20, 24).A);

        // The taskbar's ink at full strength: not red (the microphone is not
        // open) and not faded (it is not the user's pause).
        Assert.Equal((255, 255, 255, 255), Pixel(onDark, 16, 14));
        Assert.Equal((0x1B, 0x1B, 0x1B, 255), Pixel(onLight, 16, 14));
    }

    /// <summary>The Mac's "paused, ready and needs-attention are three
    /// different icons": a hold that does nothing must not be the first sign
    /// that Windows blocks the microphone.</summary>
    [Fact]
    public void Off_ready_and_the_warning_are_three_different_pictures_and_every_state_has_its_own()
    {
        foreach (var light in new[] { false, true })
        {
            var pictures = Enum.GetValues<TrayIcon>().Select(state => TrayIconArt.Render(state, light)).ToArray();
            for (var a = 0; a < pictures.Length; a++)
            {
                for (var b = a + 1; b < pictures.Length; b++)
                {
                    Assert.False(pictures[a].AsSpan().SequenceEqual(pictures[b]), $"{(TrayIcon)a} and {(TrayIcon)b} look the same");
                }
            }
        }

        // Not only the bytes: the warning's shape is not the microphone's.
        var ready = Image(TrayIconArt.Render(TrayIcon.Ready, lightTaskbar: false), 3);
        var warning = Image(TrayIconArt.Render(TrayIcon.MicrophoneBlocked, lightTaskbar: false), 3);
        // The microphone's left arm, where the triangle is open; the
        // triangle's base corner, where the microphone has nothing.
        Assert.Equal(255, Pixel(ready, 7, 13).A);
        Assert.Equal(0, Pixel(warning, 7, 13).A);
        Assert.Equal(0, Pixel(ready, 4, 28).A);
        Assert.Equal(255, Pixel(warning, 4, 28).A);
    }

    // ─── Start at sign-in ───────────────────────────────────────────────

    [Fact]
    public void Starting_at_sign_in_is_off_until_the_box_is_checked()
    {
        using var rig = new Rig();
        Assert.False(rig.View.Last.LaunchAtLogin);

        rig.Controller.SetLaunchAtLogin(true);
        Assert.True(rig.Platform.Login.IsEnabled);
        Assert.True(rig.View.Last.LaunchAtLogin);

        rig.Controller.SetLaunchAtLogin(false);
        Assert.False(rig.Platform.Login.IsEnabled);
        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal([true, false], rig.Platform.Login.Requests);
    }

    [Fact]
    public void The_box_shows_the_entry_found_at_launch()
    {
        using var rig = new Rig(beforeStart: r => r.Platform.Login.IsEnabled = true);

        Assert.True(rig.View.Last.LaunchAtLogin);
    }

    [Fact]
    public void A_change_that_cannot_be_written_says_so_and_shows_what_is_there()
    {
        using var rig = new Rig();
        rig.Platform.Login.Accepts = false;

        rig.Controller.SetLaunchAtLogin(true);

        Assert.False(rig.View.Last.LaunchAtLogin);
        Assert.Equal(DictationCopy.LaunchAtLoginNotTurnedOn, rig.View.Last.LaunchAtLoginNote);
    }

    [Fact]
    public void The_entry_names_this_copy_and_marks_the_sign_in_launch()
    {
        const string exe = @"C:\Program Files\Dictation\Dictation.exe";
        var command = LaunchAtLoginEntry.Command(exe);

        Assert.Equal("\"C:\\Program Files\\Dictation\\Dictation.exe\" --login", command);
        Assert.Equal("Dictation", LaunchAtLoginEntry.ValueName);
        Assert.True(LaunchAtLoginEntry.IsOn(command, null, exe));
        Assert.True(LaunchAtLoginEntry.IsOn(command.ToUpperInvariant(), null, exe));
        Assert.False(LaunchAtLoginEntry.IsOn(null, null, exe));
        Assert.False(LaunchAtLoginEntry.IsOn(LaunchAtLoginEntry.Command(@"D:\Old\Dictation.exe"), null, exe));
        Assert.True(LaunchAtLoginEntry.IsLoginLaunch(["--login"]));
        Assert.False(LaunchAtLoginEntry.IsLoginLaunch([]));
        Assert.False(LaunchAtLoginEntry.IsLoginLaunch(null));
    }

    [Fact]
    public void An_entry_switched_off_in_Windows_settings_reads_as_off()
    {
        const string exe = @"C:\Dictation\Dictation.exe";
        var command = LaunchAtLoginEntry.Command(exe);

        Assert.True(LaunchAtLoginEntry.IsOn(command, [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], exe));
        Assert.False(LaunchAtLoginEntry.IsOn(command, [0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8], exe));
        Assert.True(LaunchAtLoginEntry.IsOn(command, [], exe));
    }

    // ─── The log folder ─────────────────────────────────────────────────

    [Fact]
    public void Open_log_folder_shows_the_app_s_log_directory()
    {
        using var rig = new Rig();
        var status = rig.View.Last.Status;

        Assert.True(rig.Controller.OpenLogFolder());

        Assert.Equal([rig.LogDirectory], rig.Platform.OpenedFolders);
        Assert.Equal(status, rig.View.Last.Status);
        Assert.EndsWith(Path.Combine("Dictation", "logs"), new DictationControllerOptions().LogDirectory,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_folder_that_will_not_open_says_so()
    {
        using var rig = new Rig();
        rig.Platform.FolderOpens = false;

        Assert.False(rig.Controller.OpenLogFolder());

        Assert.Equal(DictationCopy.LogFolderNotOpened, rig.View.Last.Status);
        // The window's own button is in front already; only the menu, which
        // is used with the window hidden, brings it forward (TrayMenuTests).
        Assert.Equal(0, rig.View.RevealRequests);
    }

    /// <summary>Every fixed sentence in <see cref="DictationCopy"/>, and a
    /// rendering of every sentence it builds.</summary>
    private static IEnumerable<string> AllCopy()
    {
        var key = DictationHotkeys.HoldChoices[0];
        foreach (var field in typeof(DictationCopy).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is string text)
            {
                yield return text;
            }
        }

        foreach (var outcome in Enum.GetValues<DictationOutcome>())
        {
            yield return DictationCopy.ForResult(new DictationSessionResult(outcome, null, null), key).Text;
        }

        yield return DictationCopy.WorkerRestarted(key);
        yield return DictationCopy.CancelledByShortcut(key);
        foreach (var stop in Enum.GetValues<DictationHoldStop>())
        {
            var ending = new DictationHoldEnding(stop, TimeSpan.FromSeconds(30));
            yield return DictationCopy.StoppedTranscribing(ending, key);
            yield return DictationCopy.InsertedAfterStop(ending, key);
        }

        yield return DictationCopy.NothingHeardStopped(DictationHoldPolicy.Windows.NothingHeardStop, key);
    }

    [Fact]
    public void Every_sentence_that_sends_the_user_to_the_details_names_the_button()
    {
        var mentions = AllCopy()
            .Where(t => t.Contains("details", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(mentions.Length >= 5, $"only {mentions.Length} sentences point at the details");
        Assert.All(mentions, t => Assert.Contains(DictationCopy.OpenLogFolder, t, StringComparison.Ordinal));
        Assert.DoesNotContain(AllCopy(), t => t.Contains("the log has", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_button_is_labelled_the_way_the_sentences_name_it()
    {
        var xaml = XDocument.Load(Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var button = xaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "OpenLogFolderButton");

        Assert.Equal(DictationCopy.OpenLogFolder, ((string?)button.Attribute("Content"))?.Replace("_", string.Empty));
    }

    [Fact]
    public void The_password_field_sentences_say_only_what_is_true()
    {
        Assert.Equal("Earlet does not work in password fields. Nothing was recorded.", DictationCopy.SecureFieldAtPress);
        Assert.DoesNotContain("recorded", DictationCopy.ForResult(
            new DictationSessionResult(DictationOutcome.SecureField, null, null), DictationHotkeys.HoldChoices[0]).Text,
            StringComparison.Ordinal);
    }
}
