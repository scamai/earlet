using System.Runtime.InteropServices;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using static Dictation.Platform.Insert.Win32InsertInterop;

namespace Dictation.Platform.Insert;

/// <summary>
/// workflows P-07 — <see cref="IPasteKeystroke"/> as a synthetic Ctrl+V.
/// </summary>
/// <remarks>
/// <para>
/// <c>SendInput</c> injects at the keyboard, not at a window: there is no HWND
/// parameter and there cannot be one. The keystroke goes wherever focus is at
/// the instant it is sent, which is the entire reason the dictation inserter
/// (<c>FocusedTextInserter</c>) will not call this until it has resolved the
/// focused application. Nothing in this class can make that safe; it is only
/// safe because of who calls it.
/// </para>
/// <para>
/// Four events in one call rather than four calls — <c>SendInput</c> guarantees
/// a single batch is not interleaved with input from another thread, so the V
/// cannot land outside the Ctrl.
/// </para>
/// <para>
/// <b>Not handled: modifiers the user is physically holding.</b> A held Alt
/// would make this Ctrl+Alt+V, which Slack does not treat as paste. In the real
/// sequence the push-to-talk key (P-02, Ctrl+Alt+Space) was released before the
/// transcript existed, so by here it is long up — but that is reasoning about
/// timing, and it is on the Windows queue rather than asserted here.
/// </para>
/// </remarks>
public sealed class Win32PasteKeystroke : IPasteKeystroke
{
    private readonly ILogger<Win32PasteKeystroke> _logger;

    public Win32PasteKeystroke(ILogger<Win32PasteKeystroke> logger) =>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public bool TrySend()
    {
        var keys = new[]
        {
            Key(VK_CONTROL, down: true),
            Key(VK_V, down: true),
            Key(VK_V, down: false),
            Key(VK_CONTROL, down: false),
        };

        var sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>());

        if (sent == keys.Length)
        {
            return true;
        }

        // A partial send is a failure, not a partial success: a Ctrl left down
        // with no matching up would turn the operator's next keypress into a
        // shortcut. Reported so the caller falls back to a human Ctrl+V, which
        // also clears the state.
        _logger.LogWarning(
            "SendInput accepted {Sent} of {Expected} key events (error {Error}); paste not sent.",
            sent,
            keys.Length,
            Marshal.GetLastWin32Error());
        return false;
    }
}
