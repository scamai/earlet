using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Dictation.Infrastructure;

// Part of LowLevelHoldKeyRegistration (see LowLevelHoldKeyRegistration.cs):
// what the hook watch reads and does. Windows removes a low-level hook whose
// callback overruns LowLevelHooksTimeout and says nothing, so the shell asks,
// from its poll and never from the callback, whether the hook still agrees
// with Windows about the trigger, and puts a new hook in place when it does
// not. The decisions are Dictation.Core's DictationHookWatch; this is the
// Win32 half.
public sealed partial class LowLevelHoldKeyRegistration
{
    // ─── The hook watch ─────────────────────────────────────────────────

    /// <summary>
    /// Whether Windows says the trigger is down right now
    /// (<c>GetAsyncKeyState</c>), whatever this hook has or has not seen.
    /// </summary>
    /// <remarks>
    /// One user32 call with no message to any window, so it cannot block on a
    /// hung application. Only the trigger's own key is read, and only by the
    /// poll: never the other keys, which would make this a keylogger, and
    /// never from the callback. Whether Windows answers for a key pressed in a
    /// window running as administrator is not measured; the shell asks about
    /// that window separately.
    /// </remarks>
    public bool IsKeyDownNow() => (GetAsyncKeyState((int)Binding.VirtualKey) & KeyDownBit) != 0;

    /// <summary>The hook's own view of the trigger: whether it last saw the
    /// key go down and not come up (<see cref="HoldKeyState.IsPhysicallyDown"/>).</summary>
    public bool HookSawKeyDown => _state.IsPhysicallyDown;

    /// <summary>
    /// How many events of the trigger itself this hook has received, its
    /// autorepeats included: a count, as <see cref="ObservedKeyEvents"/> is.
    /// A trigger the hook says is down, still arriving while Windows' key state
    /// reads up, is held and kept from Windows by a hook after this one, not a
    /// release this hook missed. Hook thread only, as the poll that reads it.
    /// </summary>
    public long TriggerKeyEvents => _triggerKeyEvents;

    private long _triggerKeyEvents;

    /// <summary>Count one event of the trigger: called with every keyboard
    /// event the hook folds into the trigger state.</summary>
    private void CountTriggerEvent(int virtualKey)
    {
        if (virtualKey == _state.TriggerVirtualKey)
        {
            _triggerKeyEvents++;
        }
    }

    /// <summary>
    /// Put this hook's view of the trigger right after it missed an edge:
    /// see <see cref="HoldKeyState.Resync"/>. A release it never saw ends the
    /// hold now; a press it never saw holds nothing.
    /// </summary>
    /// <param name="keyDown">Where Windows says the trigger is (<see cref="IsKeyDownNow"/>).</param>
    /// <param name="nowMs">The hook's clock, <see cref="Environment.TickCount64"/>.</param>
    public void Resync(bool keyDown, long nowMs) => _state.Resync(keyDown, nowMs);

    /// <summary>
    /// Send the hook one harmless key, the unassigned <c>VK 0xE8</c> down and
    /// up that a bare Alt hold already gets (<see cref="SuppressMenuModeArming"/>),
    /// so the poll can see whether the hook still receives anything: a live
    /// hook counts it (<see cref="ObservedKeyEvents"/>) once the thread next
    /// pumps, a removed one never does.
    /// </summary>
    /// <returns>False when Windows refused the injection, which proves
    /// nothing either way: the watch then never calls the silence a removed
    /// hook.</returns>
    /// <remarks>
    /// The key reaches the application in front, as the Alt phantom does; no
    /// keyboard produces it and no application binds it, and the hold-key
    /// state ignores it as a chord. It is never passed to the correction
    /// watch's keyboard feed (<c>HookProc</c>). Windows blocks the injection, silently,
    /// when a window running at a higher integrity level is in front (UIPI),
    /// which is why the shell does not probe then.
    /// </remarks>
    public bool ProbeHook()
    {
        var scan = (ushort)MapVirtualKey(VK_UNASSIGNED, MAPVK_VK_TO_VSC);
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki.wVk = VK_UNASSIGNED;
        inputs[0].U.ki.wScan = scan;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki.wVk = VK_UNASSIGNED;
        inputs[1].U.ki.wScan = scan;
        inputs[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>
    /// Take the keyboard hook down, if Windows has not already, and install a
    /// new one, on this thread, which must be the thread that installed the
    /// first: a low-level hook is called on the thread that installed it,
    /// while that thread pumps messages.
    /// </summary>
    /// <returns>Whether a hook is in place afterwards. False is the
    /// machine-policy refusal <see cref="Register"/> also reports; the next
    /// call tries again.</returns>
    /// <remarks>
    /// The trigger state is kept: call <see cref="Resync"/> first, so a hold
    /// the old hook left open is closed and a key already down holds nothing.
    /// The event count is kept too, so the shell can tell the new hook is
    /// receiving keys when it moves. The mouse hook, when a button is bound,
    /// is not touched: the standalone app binds none.
    /// </remarks>
    public bool Reinstall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != IntPtr.Zero)
        {
            // False, and nothing to do, when Windows removed it already: that
            // is the usual reason to be here.
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        // The same delegate, still held in its field, so the pointer Windows
        // is handed is one the collector cannot take.
        _proc ??= HookProc;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
        {
            _logger.LogWarning(
                "The hold-to-talk hook could not be installed again (error {Error})", Marshal.GetLastWin32Error());
            return false;
        }

        _logger.LogInformation("Hold-to-talk hook reinstalled on {Binding}", Binding.Display);
        return true;
    }

    /// <summary>The bit of <c>GetAsyncKeyState</c>'s answer that says the key
    /// is down now. The low bit, "pressed since the last call", is shared with
    /// every other caller in the session and means nothing here.</summary>
    private const int KeyDownBit = 0x8000;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
