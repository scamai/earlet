using System.Runtime.InteropServices;

namespace Dictation.Platform.Insert;

/// <summary>
/// workflows P-07 native interop. The clipboard and the synthetic keystroke,
/// and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Internal, and confined to this class, per the house rule that P/Invoke does
/// not spread: service code consumes
/// <see cref="Speech.IClipboard"/> and
/// <see cref="Speech.IPasteKeystroke"/> instead.
/// </para>
/// <para>
/// The raw clipboard API is used rather than WPF's <c>System.Windows.Clipboard</c>
/// for two reasons: this project deliberately does not <c>UseWPF</c>, and the
/// OLE wrapper requires an STA thread while these entry points do not — the
/// paste is reached from an async continuation after a focus poll, which is on
/// no thread in particular.
/// </para>
/// </remarks>
internal static class Win32InsertInterop
{
    /// <summary>Unicode text — the only clipboard format this feature writes
    /// content in. The other formats a write carries are the markers in
    /// <see cref="Win32Clipboard.Plan"/>, which say what NOT to do with it.</summary>
    public const uint CF_UNICODETEXT = 13;

    /// <summary>Movable memory, which is what <c>SetClipboardData</c> requires:
    /// the system takes ownership of the handle and moves it as it likes.</summary>
    public const uint GMEM_MOVEABLE = 0x0002;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// <c>KEYEVENTF_EXTENDEDKEY</c> — the prefixed half of the scan-code space:
    /// the arrow cluster, Home/End/PageUp/PageDown, Insert/Delete, and the
    /// right-hand modifiers. Without it those keys arrive as their numpad twins,
    /// so a script that says <c>down</c> types a <c>2</c> when Num Lock is off.
    /// </summary>
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_V = 0x56;

    /// <summary>Enter. In Slack's compose box this is Send, which is the whole
    /// reason it is named separately from every other key here.</summary>
    public const ushort VK_RETURN = 0x0D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    /// <summary>
    /// The number a named clipboard format has in this session. The same name
    /// always answers the same number, so calling it on every write is a
    /// lookup, not a registration that accumulates. Zero means failure.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// The union <c>INPUT</c> carries. <see cref="mi"/> is declared only because
    /// it is the largest member and therefore what fixes the struct's size —
    /// <c>SendInput</c> rejects a <c>cbSize</c> that does not match.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    /// <summary>One key event, up or down.</summary>
    /// <param name="extended">
    /// Whether this virtual key needs the extended prefix — see
    /// <see cref="KEYEVENTF_EXTENDEDKEY"/>. Defaults to false, which is right
    /// for every key P-07 and B-04 send (V, Ctrl, Enter) and wrong for the
    /// arrow cluster, which only the key scripts reach.
    /// </param>
    public static INPUT Key(ushort virtualKey, bool down, bool extended = false) =>
        new()
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = (down ? 0 : KEYEVENTF_KEYUP)
                        | (extended ? KEYEVENTF_EXTENDEDKEY : 0),
                },
            },
        };

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary><c>MAPVK_VK_TO_VSC</c> — a virtual key to its hardware scan code.</summary>
    public const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);
}
