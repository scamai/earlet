using Dictation.Services;

namespace Dictation.Core.Tests;

// ─── The hook watch's half of the fake hook ─────────────────────────────────
//
// Windows' own key state and the hook's view of the hold key are two separate
// things here, as they are on Windows: a test removes the hook (HookAlive =
// false), and the user's key then reaches Windows (WindowsKeyDown) but not
// the hook, whose view goes stale. The event count moves only while the hook
// is alive: every key event through it, and the test key the watch sends.

internal sealed partial class FakeTrigger
{
    /// <summary>Whether the hook still receives keys; false: Windows removed it.</summary>
    public bool HookAlive { get; set; } = true;

    /// <summary>Keyboard events the hook has received.</summary>
    public long HookEvents { get; set; }

    /// <summary>Of those, the hold key's own, its autorepeats included.</summary>
    public long TriggerEvents { get; set; }

    /// <summary>Whether Windows takes the test key; false: refused, as with
    /// another program blocking input.</summary>
    public bool ProbeAccepted { get; set; } = true;

    /// <summary>Windows' own key state (<c>GetAsyncKeyState</c>); null: the
    /// same as the hook's view, so a test that never sets it never disagrees.</summary>
    public bool? WindowsKeyDown { get; set; }

    /// <summary>Whether <see cref="Reinstall"/> gets a new hook from Windows.</summary>
    public bool ReinstallSucceeds { get; set; } = true;

    /// <summary>Runs inside <see cref="ReadHook"/>, to make it throw.</summary>
    public Action? OnReadHook { get; set; }

    public int Reinstalls { get; private set; }

    public int ProbesSent { get; private set; }

    /// <summary>Every <see cref="Resync"/>, with where Windows said the key was.</summary>
    public List<bool> Resyncs { get; } = [];

    /// <summary>Without the real gesture: a press the hook never saw, adopted
    /// as down and holding nothing.</summary>
    private bool _adopted;

    /// <summary>The hook's own view of the key.</summary>
    public bool HookSawKeyDown => Gesture?.IsPhysicallyDown ?? (Down || _adopted);

    public HookReading ReadHook()
    {
        OnReadHook?.Invoke();
        return new HookReading(WindowsKeyDown ?? HookSawKeyDown, HookSawKeyDown, HookEvents, TriggerEvents);
    }

    public bool ProbeHook()
    {
        ProbesSent++;
        if (HookAlive && ProbeAccepted)
        {
            // The injected down and up, both through the hook.
            HookEvents += 2;
        }

        return ProbeAccepted;
    }

    public void Resync(bool keyDown)
    {
        Resyncs.Add(keyDown);
        if (Gesture is not null)
        {
            Gesture.Resync(keyDown, GestureTime);
        }
        else if (keyDown)
        {
            _adopted = true;
        }
        else
        {
            Down = false;
            _adopted = false;
        }
    }

    public bool Reinstall()
    {
        Reinstalls++;
        HookAlive = ReinstallSucceeds;
        return ReinstallSucceeds;
    }

    /// <summary>The user presses or lets go of the hold key: Windows sees it,
    /// and the hook too while it is alive.</summary>
    public void UserHoldKey(bool down)
    {
        WindowsKeyDown = down;
        if (!HookAlive)
        {
            return;
        }

        if (Gesture is not null)
        {
            KeyEvent((int)Binding.VirtualKey, down ? HoldKeyState.WmKeyDown : HoldKeyState.WmKeyUp);
        }
        else
        {
            HookEvents++;
            TriggerEvents++;
            Down = down;
            _adopted &= down;
        }
    }
}

/// <summary>The window in front, as the hook watch asks about it.</summary>
internal sealed class FakeForeground : IForegroundElevation
{
    public bool ThisProcessElevated { get; set; }

    /// <summary>The process in front; 0 for none.</summary>
    public int Process { get; set; } = 100;

    /// <summary>The processes that run as administrator.</summary>
    public HashSet<int> Elevated { get; } = [];

    /// <summary>Processes whose elevation cannot be told.</summary>
    public HashSet<int> Unknown { get; } = [];

    public bool Throws { get; set; }

    public int Reads { get; private set; }

    public List<int> Queries { get; } = [];

    public int ForegroundProcessId()
    {
        Reads++;
        return Throws ? throw new InvalidOperationException("the window manager is having a day") : Process;
    }

    public bool? IsElevated(int processId)
    {
        Queries.Add(processId);
        return Unknown.Contains(processId) ? null : Elevated.Contains(processId);
    }
}
