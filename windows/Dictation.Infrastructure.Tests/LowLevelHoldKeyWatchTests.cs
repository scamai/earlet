using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Infrastructure.Tests;

/// <summary>
/// The hook watch's Win32 half: what the registration reports to the watch
/// and how it puts its view right, through the registration itself; and,
/// pinned on the source because none of it can run here, that the watch's
/// calls (Windows' key state, the test key, the reinstall, the elevation
/// probe) stay out of the hook's callback and that the reinstall keeps the
/// delegate Windows holds.
/// </summary>
public sealed class LowLevelHoldKeyWatchTests
{
    private const int Trigger = (int)DictationHotkeys.VkRightControl;
    private const int Down = HoldKeyState.WmKeyDown;
    private const int Up = HoldKeyState.WmKeyUp;

    private static LowLevelHoldKeyRegistration Build(HoldKeyTriggerMode mode = HoldKeyTriggerMode.HoldOnly) =>
        new(
            new HotkeyBinding(0, DictationHotkeys.VkRightControl, "Hold Right Ctrl"),
            NullLogger<LowLevelHoldKeyRegistration>.Instance,
            triggerMode: mode,
            cancelOnChord: true);

    [Fact]
    public void The_hook_s_view_is_the_physical_key_and_a_missed_release_ends_the_hold()
    {
        using var registration = Build();
        Assert.False(registration.HookSawKeyDown);
        registration.ObserveKeyboard(Trigger, Down, 1_000);
        Assert.True(registration.HookSawKeyDown);
        Assert.True(registration.IsKeyDown());

        registration.Resync(keyDown: false, 5_000);

        Assert.False(registration.HookSawKeyDown);
        Assert.False(registration.IsKeyDown());
    }

    [Fact]
    public void A_missed_press_is_adopted_and_holds_nothing()
    {
        using var registration = Build(HoldKeyTriggerMode.HoldOrDoubleTapHandsFree);

        registration.Resync(keyDown: true, Environment.TickCount64);

        Assert.True(registration.HookSawKeyDown);
        Assert.False(registration.IsKeyDown());
        registration.ObserveKeyboard(Trigger, Up, Environment.TickCount64);
        Assert.False(registration.HookSawKeyDown);
        Assert.False(registration.IsKeyDown());
    }

    /// <summary>The count that tells a held key kept from Windows by a later
    /// hook (its autorepeats keep arriving) from a release this hook missed:
    /// every event of the trigger through the hook's path, its autorepeats
    /// included, and nothing else; a resync is not an event.</summary>
    [Fact]
    public void The_trigger_s_own_events_are_counted_autorepeats_included_and_nothing_else()
    {
        using var registration = Build();
        Assert.Equal(0, registration.TriggerKeyEvents);

        registration.ObserveKeyboard(Trigger, Down, 1_000);
        registration.ObserveKeyboard(Trigger, Down, 1_500);
        registration.ObserveKeyboard(Trigger, Down, 1_533);
        registration.ObserveKeyboard((int)'A', Down, 1_600);
        registration.ObserveKeyboard((int)'A', Up, 1_650);
        registration.ObserveKeyboard(HoldKeyState.VkMenuSuppression, Down, 1_700);
        Assert.Equal(3, registration.TriggerKeyEvents);

        registration.Resync(keyDown: false, 2_000);
        Assert.Equal(3, registration.TriggerKeyEvents);
        registration.ObserveKeyboard(Trigger, Up, 2_100);
        Assert.Equal(4, registration.TriggerKeyEvents);
    }

    [Fact]
    public void The_elevation_probe_answers_nothing_for_no_process_without_asking_Windows()
    {
        var probe = new ForegroundElevation(NullLogger<ForegroundElevation>.Instance);
        Assert.Null(probe.IsElevated(0));
        Assert.Null(probe.IsElevated(-1));
    }

    // ─── Pinned on the source ───────────────────────────────────────────

    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(WindowsRoot(), "Dictation.Infrastructure", file)).ReplaceLineEndings("\n");

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{signature}'");
        return code[start..code.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
    }

    /// <summary>
    /// Windows removes a hook whose callback overruns its time limit, so the
    /// watch must never add work to the callback: no key-state read, no test
    /// key, no reinstall, no cross-process elevation call. The poll does all of
    /// it.
    /// </summary>
    [Fact]
    public void Nothing_the_watch_does_runs_inside_the_hook_s_callback()
    {
        var hook = Method(Source("LowLevelHoldKeyRegistration.cs"), "    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)");

        foreach (var call in new[]
        {
            "IsKeyDownNow", "GetAsyncKeyState", "ProbeHook", "Reinstall", "Resync", "SetWindowsHookEx",
            "UnhookWindowsHookEx", "OpenProcess", "IsElevated", "ForegroundElevation",
        })
        {
            Assert.DoesNotContain(call, hook, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The test key, and the bare-Alt phantom before it, are this hook's own
    /// injections of the unassigned <c>VK 0xE8</c>; passed to the correction
    /// watch's keyboard feed they read as the user typing in the field a
    /// dictation just went into (reviewer finding).
    /// </summary>
    [Fact]
    public void The_hook_s_own_unassigned_key_never_reaches_the_keyboard_feed()
    {
        var hook = Method(Source("LowLevelHoldKeyRegistration.cs"), "    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)");

        Assert.Contains(
            "if (_keyboardInputObserved is not null && virtualKey != HoldKeyState.VkMenuSuppression)",
            hook,
            StringComparison.Ordinal);
        Assert.Contains("var virtualKey = Marshal.ReadInt32(lParam);", hook, StringComparison.Ordinal);
        // One read of the key code, the one above.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(hook, @"Marshal\.ReadInt32\(lParam\)"));
    }

    /// <summary>
    /// A process Dictation may not open at all (a program a standard user
    /// elevated with an administrator's name and password, a service) counts
    /// as above it, as one whose token it may not read does; any other failure
    /// is "cannot tell", which the watch never calls a lost hook (reviewer
    /// finding).
    /// </summary>
    [Fact]
    public void A_process_that_refuses_to_be_opened_counts_as_elevated()
    {
        var probe = Method(Source("ForegroundElevation.cs"), "    public bool? IsElevated(int processId)");

        var open = probe.IndexOf("OpenProcess(ProcessQueryLimitedInformation", StringComparison.Ordinal);
        var refused = probe.IndexOf("return error == ErrorAccessDenied ? true : null;", StringComparison.Ordinal);
        Assert.True(open >= 0 && refused > open, probe);
        Assert.Contains(
            "return Marshal.GetLastWin32Error() == ErrorAccessDenied ? true : null;",
            Source("ForegroundElevation.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_reinstall_unhooks_first_and_hands_Windows_the_delegate_held_in_the_field()
    {
        var reinstall = Method(Source("LowLevelHoldKeyRegistration.Watch.cs"), "    public bool Reinstall()");

        var unhook = reinstall.IndexOf("UnhookWindowsHookEx(_hook)", StringComparison.Ordinal);
        var keep = reinstall.IndexOf("_proc ??= HookProc;", StringComparison.Ordinal);
        var hookAgain = reinstall.IndexOf("SetWindowsHookEx(WH_KEYBOARD_LL, _proc,", StringComparison.Ordinal);
        Assert.True(unhook >= 0 && keep > unhook && hookAgain > keep, reinstall);
        // The trigger state is the resync's to put right, not the reinstall's.
        Assert.DoesNotContain("_state.", reinstall, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_key_state_is_read_for_the_hold_key_alone_and_the_down_bit_alone()
    {
        var code = Source("LowLevelHoldKeyRegistration.Watch.cs");
        Assert.Contains(
            "public bool IsKeyDownNow() => (GetAsyncKeyState((int)Binding.VirtualKey) & KeyDownBit) != 0;",
            code,
            StringComparison.Ordinal);
        Assert.Contains("private const int KeyDownBit = 0x8000;", code, StringComparison.Ordinal);
        // One call, the one above; the other match is the declaration.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"GetAsyncKeyState\(\(int\)"));
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
