using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Dictation.Core;

namespace Dictation.App;

// Part of MainWindow (see MainWindow.xaml.cs): what the window render mode
// (DictationRenderMode) needs of a window built over its inert parts
// (DictationWindowParts.ForRender). The app itself never calls any of it.
// Built on Linux; never run (WPF needs Windows).
public partial class MainWindow
{
    /// <summary>The controller this window built, for a scenario to drive.</summary>
    internal DictationController RenderController => controller;

    /// <summary>The Vocabulary section this window built, for a scenario to drive.</summary>
    internal VocabularyModel RenderVocabulary => vocabulary;

    /// <summary>
    /// The window's content, taken out of the window, which is never shown,
    /// once the scenario has been driven: stood on its own so it can be laid
    /// out at each width and drawn. Its host carries what the window gave it,
    /// the window's background and text colours, by the same system colour
    /// keys, so a render's palette reaches them.
    /// </summary>
    /// <remarks>
    /// The poll stops first: nothing may change the window under the
    /// pictures. The title bar and the frame are not part of the content and
    /// are not drawn, as the Mac draws its hosting view without them.
    /// </remarks>
    internal UIElement TakeContentForRender()
    {
        poll.Stop();
        var content = (UIElement)Content;
        Content = null;
        return content;
    }

    /// <summary>
    /// Open the sections the scenario draws open
    /// (<see cref="DictationWindowRenderScenarios.Opens"/>) and close every
    /// other, as a user's click would: the window keeps no state that says
    /// whether they are open. Try dictation is the controller's to open.
    /// </summary>
    internal void OpenForRender(IReadOnlySet<DictationWindowDisclosure> open)
    {
        RecentTextExpander.IsExpanded = open.Contains(DictationWindowDisclosure.RecentText);
        TroubleshootingSection.IsExpanded = open.Contains(DictationWindowDisclosure.Troubleshooting);
    }

    /// <summary>Draw the header's glyph with High Contrast on or off, as the
    /// appearance being drawn has it, whatever the render machine's setting.</summary>
    internal void DrawHighContrastForRender(bool on)
    {
        highContrastForRender = on;
        if (header is { } shown)
        {
            RenderGlyph(shown.Glyph);
        }
    }

    /// <summary>A host for <paramref name="content"/> with the window's
    /// background and text colours.</summary>
    internal static Border RenderHost(UIElement content)
    {
        var host = new Border { Child = content, UseLayoutRounding = true };
        host.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
        host.SetResourceReference(TextElement.ForegroundProperty, SystemColors.WindowTextBrushKey);
        return host;
    }

    /// <summary>
    /// Let go of the controller, the Vocabulary section and the (hidden)
    /// icon, without closing the window: closing it ends the process
    /// (<c>Closed</c>), and the render has more scenarios to draw.
    /// </summary>
    internal void ReleaseForRender() => LetGo();
}
