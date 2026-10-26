using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Infrastructure.Tests;

/// <summary>
/// The hook path in the hands-free mode: what the registration tells the
/// controller, which events <c>HookProc</c> consumes, and that teardown and
/// <see cref="LowLevelHoldKeyRegistration.EndLatch"/> leave nothing latched
/// or swallowing. <c>HookProc</c> itself only runs inside Windows' input path,
/// so its one new branch is pinned on its source, as the window's wiring is.
/// </summary>
public sealed class LowLevelHoldKeyHandsFreeTests
{
    private const int Trigger = (int)DictationHotkeys.VkRightControl;
    private const int Down = HoldKeyState.WmKeyDown;
    private const int Up = HoldKeyState.WmKeyUp;

    private static LowLevelHoldKeyRegistration Build() =>
        new(
            new HotkeyBinding(0, DictationHotkeys.VkRightControl, "Hold Right Ctrl"),
            NullLogger<LowLevelHoldKeyRegistration>.Instance,
            triggerMode: HoldKeyTriggerMode.HoldOrDoubleTapHandsFree,
            cancelOnChord: true);

    private static void Latch(LowLevelHoldKeyRegistration registration, long at)
    {
        registration.ObserveKeyboard(Trigger, Down, at);
        registration.ObserveKeyboard(Trigger, Up, at + 60);
        registration.ObserveKeyboard(Trigger, Down, at + 160);
        registration.ObserveKeyboard(Trigger, Up, at + 220);
        Assert.True(registration.IsLatched);
    }

    [Fact]
    public void Only_the_swallow_flag_makes_the_hook_consume_an_event()
    {
        Assert.True(LowLevelHoldKeyRegistration.Consumes(HoldKeyTransition.Swallow));
        Assert.True(LowLevelHoldKeyRegistration.Consumes(HoldKeyTransition.Swallow | HoldKeyTransition.HoldEnded));

        foreach (var other in new[]
        {
            HoldKeyTransition.None, HoldKeyTransition.HoldStarted, HoldKeyTransition.HoldEnded,
            HoldKeyTransition.SuppressMenu, HoldKeyTransition.CancelRequested,
            HoldKeyTransition.HoldEnded | HoldKeyTransition.SuppressMenu,
        })
        {
            Assert.False(LowLevelHoldKeyRegistration.Consumes(other), other.ToString());
        }
    }

    [Fact]
    public void Space_and_Enter_are_consumed_only_while_a_latch_records()
    {
        using var registration = Build();

        // At rest, and during a parked tap: typed.
        Assert.False(LowLevelHoldKeyRegistration.Consumes(registration.ObserveKeyboard(HoldKeyState.VkSpace, Down, 500)));
        Assert.False(LowLevelHoldKeyRegistration.Consumes(registration.ObserveKeyboard(HoldKeyState.VkSpace, Up, 520)));

        Latch(registration, 1_000);
        Assert.True(LowLevelHoldKeyRegistration.Consumes(registration.ObserveKeyboard(HoldKeyState.VkReturn, Down, 2_000)));
        Assert.False(registration.IsLatched);
        Assert.False(registration.IsKeyDown());
        Assert.True(LowLevelHoldKeyRegistration.Consumes(registration.ObserveKeyboard(HoldKeyState.VkReturn, Up, 2_050)));

        // Over: Enter types again.
        Assert.False(LowLevelHoldKeyRegistration.Consumes(registration.ObserveKeyboard(HoldKeyState.VkReturn, Down, 3_000)));
    }

    [Fact]
    public void Ending_the_latch_from_outside_reads_the_key_up_and_stops_the_swallowing()
    {
        using var registration = Build();
        Latch(registration, Environment.TickCount64 - 10_000);
        Assert.True(registration.IsKeyDown());

        registration.EndLatch();

        Assert.False(registration.IsLatched);
        Assert.False(registration.IsKeyDown());
        Assert.False(LowLevelHoldKeyRegistration.Consumes(
            registration.ObserveKeyboard(HoldKeyState.VkSpace, Down, Environment.TickCount64)));
    }

    [Fact]
    public void Teardown_ends_a_latch_so_no_key_is_swallowed_after_it()
    {
        using var registration = Build();
        Latch(registration, Environment.TickCount64 - 10_000);

        registration.Unregister();

        Assert.False(registration.IsLatched);
        Assert.False(registration.IsKeyDown());
        Assert.False(LowLevelHoldKeyRegistration.Consumes(
            registration.ObserveKeyboard(HoldKeyState.VkSpace, Down, Environment.TickCount64)));
    }

    // ─── Event time ─────────────────────────────────────────────────────

    /// <summary>
    /// The first press opens the microphone on the hook's own thread; a slow
    /// device holds it, and the first tap's release and the second press
    /// arrive late, together. Stamped with when they happened they still
    /// latch; stamped on arrival (the old rule, shown last) they did not.
    /// </summary>
    [Fact]
    public void A_double_tap_delivered_late_by_a_busy_thread_latches_on_the_events_own_times()
    {
        const long stall = 400;
        using var registration = Build();

        registration.ObserveKeyboardEvent(Trigger, Down, Stamp(10_000), 10_000);
        registration.ObserveKeyboardEvent(Trigger, Up, Stamp(10_060), 10_000 + stall);
        Assert.Equal(
            HoldKeyTransition.None,
            registration.ObserveKeyboardEvent(Trigger, Down, Stamp(10_160), 10_000 + stall + 1));
        Assert.True(registration.IsLatched);

        using var onArrival = Build();
        onArrival.ObserveKeyboard(Trigger, Down, 10_000);
        onArrival.ObserveKeyboard(Trigger, Up, 10_000 + stall);
        onArrival.ObserveKeyboard(Trigger, Down, 10_000 + stall + 1);
        Assert.False(onArrival.IsLatched);
    }

    [Fact]
    public void An_events_own_time_is_used_across_the_32_bit_wrap()
    {
        const long now = 0x1_0000_0010;   // the 32-bit count wrapped 16 ms ago

        Assert.Equal(now - 32, LowLevelHoldKeyRegistration.EventTimeMs(0xFFFF_FFF0, now, 0));
        Assert.Equal(now, LowLevelHoldKeyRegistration.EventTimeMs(0x10, now, 0));
    }

    [Theory]
    [InlineData(5)]                                                       // ahead of the clock
    [InlineData(-(LowLevelHoldKeyRegistration.MaxEventAgeMs + 1))]        // older than any stall
    public void A_time_stamp_that_cannot_be_a_late_delivery_falls_back_to_the_clock(long offset)
    {
        const long now = 50_000;

        Assert.Equal(now, LowLevelHoldKeyRegistration.EventTimeMs(Stamp(now + offset), now, 0));
        Assert.Equal(
            now - LowLevelHoldKeyRegistration.MaxEventAgeMs,
            LowLevelHoldKeyRegistration.EventTimeMs(Stamp(now - LowLevelHoldKeyRegistration.MaxEventAgeMs), now, 0));
    }

    [Fact]
    public void The_stamp_never_runs_backwards()
    {
        Assert.Equal(49_990, LowLevelHoldKeyRegistration.EventTimeMs(Stamp(49_900), 50_000, 49_990));

        // Through the registration: a second press carrying a stamp older
        // than the first press's reads as no earlier than the last event, a
        // pair 60 ms apart, and not as a reversed clock (a fresh first press).
        using var registration = Build();
        registration.ObserveKeyboardEvent(Trigger, Down, Stamp(1_000_000), 1_000_000);
        registration.ObserveKeyboardEvent(Trigger, Up, Stamp(1_000_060), 1_000_060);
        registration.ObserveKeyboardEvent(Trigger, Down, Stamp(999_500), 1_000_100);
        Assert.True(registration.IsLatched);
    }

    /// <summary>Pins on the source: <c>HookProc</c> hands the hook's own
    /// time stamp to <see cref="LowLevelHoldKeyRegistration.ObserveKeyboardEvent"/>,
    /// read from <c>KBDLLHOOKSTRUCT.time</c> at offset 12.</summary>
    [Fact]
    public void HookProc_stamps_each_event_with_its_own_time()
    {
        var code = File.ReadAllText(Path.Combine(WindowsRoot(), "Dictation.Infrastructure", "LowLevelHoldKeyRegistration.cs"))
            .ReplaceLineEndings("\n");
        var start = code.IndexOf("    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)", StringComparison.Ordinal);
        Assert.True(start >= 0, "no HookProc");
        var hook = code[start..code.IndexOf("\n    }\n", start, StringComparison.Ordinal)];

        Assert.Contains("ObserveKeyboardEvent(", hook, StringComparison.Ordinal);
        Assert.Contains("Marshal.ReadInt32(lParam, KbdLlTimeOffset)", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("ObserveKeyboard(", hook, StringComparison.Ordinal);
        Assert.Contains("private const int KbdLlTimeOffset = 12;", code, StringComparison.Ordinal);
    }

    private static uint Stamp(long ms) => unchecked((uint)ms);

    /// <summary>
    /// Pins on the source, not evidence the hook behaves: <c>HookProc</c>
    /// returns the consumed value for exactly the events
    /// <see cref="LowLevelHoldKeyRegistration.Consumes"/> names, after the menu
    /// phantom and before the keyboard-origin feed and
    /// <c>CallNextHookEx</c>.
    /// </summary>
    [Fact]
    public void HookProc_consumes_what_Consumes_names_and_passes_everything_else_on()
    {
        var code = File.ReadAllText(Path.Combine(WindowsRoot(), "Dictation.Infrastructure", "LowLevelHoldKeyRegistration.cs"))
            .ReplaceLineEndings("\n");
        var start = code.IndexOf("    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)", StringComparison.Ordinal);
        Assert.True(start >= 0, "no HookProc");
        var hook = code[start..code.IndexOf("\n    }\n", start, StringComparison.Ordinal)];

        var phantom = hook.IndexOf("SuppressMenuModeArming();", StringComparison.Ordinal);
        var consume = hook.IndexOf("if (Consumes(transition))", StringComparison.Ordinal);
        var consumed = hook.IndexOf("return ConsumedEvent;", StringComparison.Ordinal);
        var feed = hook.IndexOf("ForwardKeyboardInput(", StringComparison.Ordinal);
        var next = hook.IndexOf("return CallNextHookEx(", StringComparison.Ordinal);

        Assert.True(phantom >= 0 && consume > phantom, "the stop key is consumed after the phantom is injected");
        Assert.True(consumed > consume && feed > consumed, "a consumed key is not reported as the user's typing");
        Assert.True(next > feed, "everything else reaches the next hook");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(hook, "return ConsumedEvent;"));
        Assert.Contains("ConsumedEvent = new(1)", code, StringComparison.Ordinal);
    }

    private static string WindowsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Dictation.Infrastructure", "Dictation.Infrastructure.csproj")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("no windows/ source tree above " + AppContext.BaseDirectory);
    }
}
