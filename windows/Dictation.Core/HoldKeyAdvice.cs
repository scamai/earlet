using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>The chosen hold key's effects and configuration source.</summary>
public static class HoldKeyAdvice
{
    /// <summary>The notice for <paramref name="key"/>, or null for nothing to say.</summary>
    /// <param name="key">The hold key in effect.</param>
    /// <param name="fromEnvironment">Whether the key came from the
    /// <c>DICTATION_HOLD_KEY</c> override rather than the picker.</param>
    public static string? For(HotkeyBinding key, bool fromEnvironment)
    {
        ArgumentNullException.ThrowIfNull(key);
        var lines = new List<string>();

        if (fromEnvironment)
        {
            lines.Add($"{DictationEnvironment.HoldKey} sets the key; clear it to choose here.");
        }

        if (DictationHotkeys.CautionFor(key.VirtualKey) is { } caution)
        {
            lines.Add(caution);
        }

        return lines.Count == 0 ? null : string.Join(" ", lines);
    }
}
