namespace Dictation.Core;

/// <summary>
/// When the window's "Try dictation" section opens and closes by itself. The
/// rest of the time it is the user's: they open and close it, and a render
/// leaves that alone.
/// </summary>
/// <remarks>
/// <para>
/// The macOS section's rule (docs/design-macos.md, Try dictation), kept here so it is
/// tested without WPF:
/// </para>
/// <list type="bullet">
/// <item>Each time the section appears it is open until a dictation has
/// delivered its words, and collapsed after that. On the Mac the section is
/// rebuilt every time it appears; here the expander lives as long as the
/// window, so appearing is an edge of its own.</item>
/// <item>The first delivered dictation collapses it, once, unless the practice
/// field has the focus: the words just typed into the field stay in sight,
/// and a window activated again does not give the keyboard back to a hidden
/// field. A section left open that way stays open until the user closes it
/// or it appears again.</item>
/// </list>
/// </remarks>
public sealed class PracticeDisclosure
{
    private bool _shown;
    private bool _startsExpanded;

    /// <summary>One render of the window.</summary>
    /// <param name="visible"><see cref="DictationViewState.PracticeVisible"/>.</param>
    /// <param name="startsExpanded"><see cref="DictationViewState.PracticeStartsExpanded"/>.</param>
    /// <param name="fieldHasFocus">Whether the practice field has the
    /// keyboard focus right now, or the window's focus while it is inactive.</param>
    /// <returns>Whether to open (true) or close (false) the section, or null
    /// to leave it as it is.</returns>
    public bool? Next(bool visible, bool startsExpanded, bool fieldHasFocus)
    {
        var appeared = visible && !_shown;
        var firstDelivered = _startsExpanded && !startsExpanded;
        _shown = visible;
        _startsExpanded = startsExpanded;

        if (appeared)
        {
            return startsExpanded;
        }

        if (visible && firstDelivered && !fieldHasFocus)
        {
            return false;
        }

        return null;
    }
}
