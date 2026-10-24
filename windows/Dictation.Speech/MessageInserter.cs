namespace Dictation.Speech;

/// <summary>
/// The Windows clipboard, as the one thing a paste does with it.
/// </summary>
/// <remarks>
/// Implemented for real by <c>Dictation.Platform</c>'s
/// <c>Win32Clipboard</c>, and by fakes in the test projects. Deliberately
/// write-only: reading and restoring what the user had is the dictation
/// inserter's business, through its own snapshot seams
/// (<c>IClipboardTextSnapshot</c>, <c>IClipboardSnapshot</c>), so a fake that
/// only needs to accept a paste carries no read it never answers.
/// </remarks>
public interface IClipboard
{
    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard. <c>false</c> when it could
    /// not be — another process holds the clipboard lock more often than is
    /// comfortable — which is a condition to report rather than throw, because
    /// the caller's response is the same either way: do not type.
    /// </summary>
    bool TrySet(string text);
}

/// <summary>
/// Sending the paste keystroke to whatever has focus.
/// </summary>
/// <remarks>
/// Named for the intent rather than the keys, because "what paste is bound to"
/// is the implementation's business and the caller's only question is whether
/// it went. There is no target parameter: this synthesises input at the
/// keyboard, so it lands wherever focus is — which is exactly why every caller
/// has to have confirmed focus first.
/// </remarks>
public interface IPasteKeystroke
{
    /// <summary>Sends the paste keystroke. <c>false</c> if the input could not
    /// be synthesised at all.</summary>
    bool TrySend();
}
