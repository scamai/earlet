using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>The chosen hold key's effects and configuration source, and the
/// chosen mouse button's.</summary>
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

    /// <summary>
    /// The notice under the mouse button picker: how the chosen button is
    /// used, what it stops doing while chosen
    /// (<see cref="DictationMouseButtons.CautionFor"/>), and, when Windows
    /// refused the mouse hook, that it does nothing this run.
    /// </summary>
    /// <param name="button">The button chosen, or none.</param>
    /// <param name="refused">A button is bound and its hook was refused
    /// (<c>LowLevelHoldKeyRegistration.MouseTriggerDegraded</c>).</param>
    public static string ForMouse(DictationMouseButton button, bool refused)
    {
        var lines = new List<string>
        {
            DictationMouseButtons.Choices.First(c => c.Value == button).Detail,
        };

        if (DictationMouseButtons.CautionFor(button) is { } caution)
        {
            lines.Add(caution);
        }

        // Last, so it is the sentence a screen reader ends on: the one that
        // says the choice above is not in effect.
        if (refused && button != DictationMouseButton.None)
        {
            lines.Add(DictationCopy.MouseButtonRefused);
        }

        return string.Join(" ", lines);
    }
}
