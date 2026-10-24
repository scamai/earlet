using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// The notification-area icon's picture and tooltip from one pure function
/// (<see cref="DictationTrayGlyph"/>): the macOS MenuGlyphTests ("The menu bar
/// glyph"), scenario for scenario where Windows has the state, by the Mac's
/// names. The pictures' pixels are in DictationDesktopTests; the controller
/// reaching each state is in TrayMenuTests.
/// </summary>
public sealed class TrayGlyphTests
{
    private static readonly HotkeyBinding Key = new(0, DictationHotkeys.VkRightControl, "Hold Right Ctrl");

    /// <summary>Every input off except the ones a case names: the Mac's
    /// helper, with dictation off, no Enable loading, the microphone allowed.
    /// The two calls the controller's render makes, in its order.</summary>
    private static (TrayIcon Icon, string ToolTip) Resolve(
        bool armed = false,
        bool enabling = false,
        DictationState shown = DictationState.Idle,
        bool microphoneBlocked = false,
        HookHealth hook = HookHealth.Healthy,
        bool adminWindowInFront = false) =>
        DictationTrayGlyph.Picture(
            DictationTrayGlyph.State(armed, enabling, shown, microphoneBlocked, hook, adminWindowInFront), Key);

    [Fact]
    public void Paused_is_the_faded_microphone_and_says_off()
    {
        Assert.Equal((TrayIcon.Off, DictationCopy.TrayOff), Resolve());
        Assert.Equal("Earlet — off", DictationCopy.TrayOff);
    }

    [Fact]
    public void An_enable_still_loading_the_engine_is_starting_and_the_microphone_stays_off()
    {
        Assert.Equal((TrayIcon.Off, DictationCopy.TrayStarting), Resolve(enabling: true));
    }

    [Fact]
    public void Enabled_and_idle_is_the_microphone_and_names_the_hold_key()
    {
        Assert.Equal((TrayIcon.Ready, "Earlet — hold Right Ctrl to dictate"), Resolve(armed: true, shown: DictationState.Armed));
        // The state shown before the arm's first render is Idle: still on.
        Assert.Equal((TrayIcon.Ready, DictationCopy.TrayReady(Key)), Resolve(armed: true));
    }

    [Fact]
    public void Recording_is_the_red_microphone_and_transcribing_and_typing_the_waveform()
    {
        Assert.Equal((TrayIcon.Listening, DictationCopy.TrayListening), Resolve(armed: true, shown: DictationState.Listening));
        Assert.Equal(
            (TrayIcon.Transcribing, DictationCopy.TrayTranscribing), Resolve(armed: true, shown: DictationState.Transcribing));
        Assert.Equal((TrayIcon.Transcribing, DictationCopy.TrayTyping), Resolve(armed: true, shown: DictationState.Inserting));
    }

    [Fact]
    public void A_missing_grant_draws_the_warning_triangle()
    {
        // Windows' one grant is the microphone; its tooltip names it.
        Assert.Equal(
            (TrayIcon.MicrophoneBlocked, "Earlet — Windows is blocking the microphone"),
            Resolve(armed: true, shown: DictationState.Armed, microphoneBlocked: true));
    }

    [Fact]
    public void Recording_beats_needs_attention_a_live_hold_proves_the_grants_were_there()
    {
        Assert.Equal(TrayIcon.Listening, Resolve(armed: true, shown: DictationState.Listening, microphoneBlocked: true).Icon);
        Assert.Equal(TrayIcon.Transcribing, Resolve(armed: true, shown: DictationState.Transcribing, microphoneBlocked: true).Icon);
        Assert.Equal(DictationCopy.TrayTyping, Resolve(armed: true, shown: DictationState.Inserting, microphoneBlocked: true).ToolTip);
    }

    [Fact]
    public void Needs_attention_beats_paused_and_ready_so_a_broken_setup_never_looks_fine()
    {
        // The Mac checks the grants before it looks at whether dictation is
        // enabled (DictationMenuGlyph.resolve), so the warning shows while off,
        // while an Enable loads, and while on and at rest.
        var blocked = (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked);
        Assert.Equal(blocked, Resolve(microphoneBlocked: true));
        Assert.Equal(blocked, Resolve(enabling: true, microphoneBlocked: true));
        Assert.Equal(blocked, Resolve(armed: true, microphoneBlocked: true));
        Assert.Equal(blocked, Resolve(armed: true, shown: DictationState.Armed, microphoneBlocked: true));
    }

    [Fact]
    public void A_dictation_state_left_behind_by_a_pause_is_not_read_while_off()
    {
        // Only an armed session's state is a dictation under way.
        Assert.Equal((TrayIcon.Off, DictationCopy.TrayOff), Resolve(shown: DictationState.Listening));
        Assert.Equal(
            (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked),
            Resolve(shown: DictationState.Transcribing, microphoneBlocked: true));
    }

    [Fact]
    public void Paused_ready_and_needs_attention_are_three_different_icons_and_Narrator_tells_every_state_apart()
    {
        var quiet = new[] { Resolve().Icon, Resolve(armed: true).Icon, Resolve(microphoneBlocked: true).Icon };
        Assert.Equal(3, quiet.Distinct().Count());

        // The tooltip is the icon's accessible name: one per state.
        var every = new[]
        {
            Resolve(), Resolve(enabling: true), Resolve(armed: true), Resolve(microphoneBlocked: true),
            Resolve(armed: true, shown: DictationState.Listening),
            Resolve(armed: true, shown: DictationState.Transcribing),
            Resolve(armed: true, shown: DictationState.Inserting),
            Resolve(armed: true, hook: HookHealth.Unheard),
            Resolve(armed: true, hook: HookHealth.Reconnected),
            Resolve(armed: true, adminWindowInFront: true),
        };
        Assert.Equal(every.Length, every.Select(g => g.ToolTip).Distinct().Count());
        // And every picture is reached by some state.
        Assert.Equal(Enum.GetValues<TrayIcon>().Order(), every.Select(g => g.Icon).Distinct().Order());
    }

    // ─── Three things wrong at once ─────────────────────────────────────
    //
    // A blocked microphone, a hold-key hook Windows removed (put back and not
    // yet heard, or refused) and a window running as administrator in front
    // can all hold together; the icon shows one. Every pair, all three and
    // each alone, against the order DictationTrayGlyph keeps in one place:
    // the microphone, then a hook Windows would not put back (both the Mac's
    // needsPermission, whose setup steps put the microphone first), then the
    // administrator window, then a reconnected hook.

    /// <summary>What the icon says, by name, so a row reads as a sentence.</summary>
    public enum Shows
    {
        Microphone,
        HookUnheard,
        AdminWindow,
        HookReconnected,
        Ready,
    }

    private static (TrayIcon, string) Expected(Shows shows) => shows switch
    {
        Shows.Microphone => (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked),
        Shows.HookUnheard => (TrayIcon.MicrophoneBlocked, DictationCopy.TrayHoldKeyUnheard(Key)),
        Shows.AdminWindow => (TrayIcon.Ready, DictationCopy.TrayAdminWindowInFront(Key)),
        Shows.HookReconnected => (TrayIcon.Ready, DictationCopy.TrayHoldKeyReconnected(Key)),
        _ => (TrayIcon.Ready, DictationCopy.TrayReady(Key)),
    };

    [Theory]
    // Each alone.
    [InlineData(true, HookHealth.Healthy, false, Shows.Microphone)]
    [InlineData(false, HookHealth.Unheard, false, Shows.HookUnheard)]
    [InlineData(false, HookHealth.Reconnected, false, Shows.HookReconnected)]
    [InlineData(false, HookHealth.Healthy, true, Shows.AdminWindow)]
    [InlineData(false, HookHealth.Healthy, false, Shows.Ready)]
    // Every pair.
    [InlineData(true, HookHealth.Unheard, false, Shows.Microphone)]
    [InlineData(true, HookHealth.Reconnected, false, Shows.Microphone)]
    [InlineData(true, HookHealth.Healthy, true, Shows.Microphone)]
    [InlineData(false, HookHealth.Unheard, true, Shows.HookUnheard)]
    [InlineData(false, HookHealth.Reconnected, true, Shows.AdminWindow)]
    // All three.
    [InlineData(true, HookHealth.Unheard, true, Shows.Microphone)]
    [InlineData(true, HookHealth.Reconnected, true, Shows.Microphone)]
    public void On_and_at_rest_the_microphone_outranks_a_lost_hook_which_outranks_an_administrator_window(
        bool microphoneBlocked, HookHealth hook, bool adminWindowInFront, Shows shows)
    {
        Assert.Equal(
            Expected(shows),
            Resolve(armed: true, shown: DictationState.Armed, microphoneBlocked: microphoneBlocked, hook: hook,
                adminWindowInFront: adminWindowInFront));
    }

    /// <summary>Every combination of the three.</summary>
    public static TheoryData<bool, HookHealth, bool> EveryCombination()
    {
        var data = new TheoryData<bool, HookHealth, bool>();
        foreach (var microphone in new[] { false, true })
        {
            foreach (var hook in Enum.GetValues<HookHealth>())
            {
                foreach (var admin in new[] { false, true })
                {
                    data.Add(microphone, hook, admin);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void A_dictation_under_way_outranks_all_three_and_off_or_starting_reads_only_the_microphone(
        bool microphoneBlocked, HookHealth hook, bool adminWindowInFront)
    {
        (TrayIcon, string) With(bool armed = true, bool enabling = false, DictationState shown = DictationState.Armed) =>
            Resolve(armed, enabling, shown, microphoneBlocked, hook, adminWindowInFront);

        // A live dictation proves the microphone opened and the key was heard.
        Assert.Equal((TrayIcon.Listening, DictationCopy.TrayListening), With(shown: DictationState.Listening));
        Assert.Equal((TrayIcon.Transcribing, DictationCopy.TrayTranscribing), With(shown: DictationState.Transcribing));
        Assert.Equal((TrayIcon.Transcribing, DictationCopy.TrayTyping), With(shown: DictationState.Inserting));

        // Off nothing listens for the key, so only the microphone can warn,
        // whatever state a pause left behind.
        var blocked = (TrayIcon.MicrophoneBlocked, DictationCopy.TrayMicrophoneBlocked);
        Assert.Equal(microphoneBlocked ? blocked : (TrayIcon.Off, DictationCopy.TrayOff), With(armed: false, shown: DictationState.Idle));
        Assert.Equal(
            microphoneBlocked ? blocked : (TrayIcon.Off, DictationCopy.TrayOff), With(armed: false, shown: DictationState.Listening));
        Assert.Equal(
            microphoneBlocked ? blocked : (TrayIcon.Off, DictationCopy.TrayStarting),
            With(armed: false, enabling: true, shown: DictationState.Idle));
    }
}
