using System.Xml.Linq;

namespace Dictation.Core.Tests;

/// <summary>
/// The Recent result section and the notification-area items, pinned on the
/// source: pins, not evidence that the window or the menu behaves. WPF and
/// Windows Forms cannot run here. Each fails if the thing it names is deleted
/// or moved, which is the most a machine with no Windows can say.
/// </summary>
public sealed class RecentResultWindowTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppFile(string name) =>
        Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", name);

    private static string Code(string name) => File.ReadAllText(AppFile(name)).ReplaceLineEndings("\n");

    private static XElement Named(XDocument xaml, string name) =>
        xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    /// <summary>The member that starts with <paramref name="signature"/>, to
    /// its closing brace at four spaces' indent, or to the end of its
    /// expression when it has an arrow body.</summary>
    private static string Member(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no '{signature}'");
        var arrow = code.IndexOf("=>", start, StringComparison.Ordinal);
        var brace = code.IndexOf("\n    {", start, StringComparison.Ordinal);
        var end = arrow >= 0 && (brace < 0 || arrow < brace)
            ? code.IndexOf(";\n", arrow, StringComparison.Ordinal)
            : code.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end > start, $"no end to '{signature}'");
        return code[start..end];
    }

    [Fact]
    public void The_section_sits_after_the_buttons_hidden_until_a_result_is_held()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var section = Named(xaml, "RecentSection");
        var column = section.Parent!.Elements().ToList();

        Assert.Equal("Collapsed", (string?)section.Attribute("Visibility"));
        // The buttons are the status header's, the first thing in the window;
        // then the Saved recording section (LIMIT-WIN-A), then this one.
        var buttons = column.IndexOf(Named(xaml, "CancelButton").Parent!.Parent!.Parent!);
        Assert.Equal(0, buttons);
        Assert.Equal(buttons + 1, column.IndexOf(Named(xaml, "SavedTakeSection")));
        Assert.Equal(buttons + 2, column.IndexOf(section));
        Assert.True(column.IndexOf(Named(xaml, "LaunchAtLoginBox")) > column.IndexOf(section), "before the preferences");
    }

    [Fact]
    public void The_words_stay_collapsed_under_Show_text_in_a_read_only_box_that_scrolls_within_160()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var expander = Named(xaml, "RecentTextExpander");
        var text = Named(xaml, "RecentText");

        Assert.Equal("Expander", expander.Name.LocalName);
        Assert.Equal("False", (string?)expander.Attribute("IsExpanded"));
        Assert.Equal("TextBox", text.Name.LocalName);
        Assert.Equal("True", (string?)text.Attribute("IsReadOnly"));
        // WPF records a Text set from code as an undo unit: with undo on, every
        // result ever shown, dismissed ones too, would stay in memory.
        Assert.Equal("False", (string?)text.Attribute("IsUndoEnabled"));
        var scroller = text.Parent!;
        Assert.Equal("ScrollViewer", scroller.Name.LocalName);
        Assert.Equal("160", (string?)scroller.Attribute("MaxHeight"));
        Assert.Same(expander, scroller.Parent);
    }

    /// <summary>No live region in the section: the indicator announces the
    /// copy ending, and the window's status line the recovery sentence.</summary>
    [Fact]
    public void The_section_adds_no_live_region()
    {
        var section = Named(XDocument.Load(AppFile("MainWindow.xaml")), "RecentSection");

        Assert.DoesNotContain(
            section.DescendantsAndSelf(),
            e => e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.LiveSetting"));
    }

    [Fact]
    public void Show_text_closes_again_for_each_new_result_and_the_buttons_reach_the_controller()
    {
        var code = Code("MainWindow.Recent.cs");
        var render = Member(code, "private void RenderRecent(");

        var changed = render.IndexOf("if (recentShown != recent.Id)", StringComparison.Ordinal);
        var collapsed = render.IndexOf("RecentTextExpander.IsExpanded = false;", changed, StringComparison.Ordinal);
        Assert.True(changed >= 0 && collapsed > changed, "the disclosure closes when the entry changes");
        // The keyboard in the box it closes moves to the section's buttons,
        // once they are enabled for this result.
        var focusWithin = render.IndexOf("refocus = RecentTextExpander.IsKeyboardFocusWithin;", changed, StringComparison.Ordinal);
        Assert.True(focusWithin > changed && focusWithin < collapsed, "focus is read before the box closes");
        var enabled = render.IndexOf("CopyLastTextButton.IsEnabled = ", StringComparison.Ordinal);
        var moved = render.IndexOf(
            "if (refocus)\n        {\n            // Dismiss is live", StringComparison.Ordinal);
        Assert.True(enabled > collapsed && moved > enabled, "focus moves after the buttons are drawn");
        Assert.Contains(
            "(CopyLastTextButton.IsEnabled ? CopyLastTextButton : DismissLastTextButton).Focus();",
            render[moved..],
            StringComparison.Ordinal);
        Assert.Contains("RecentSection.Visibility = Visibility.Collapsed;", render, StringComparison.Ordinal);
        Assert.Contains("RecentText.Clear();", render, StringComparison.Ordinal);
        Assert.Contains("controller.CopyLastText()", Member(code, "private void CopyLastTextClick("), StringComparison.Ordinal);
        Assert.Contains("controller.DismissLastText()", Member(code, "private void DismissLastTextClick("), StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetName(DismissLastTextButton, DictationCopy.DismissLastTextName)",
            Member(code, "private void ShowRecentLabels()"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_draws_the_section_from_every_render_and_wires_the_menu_item()
    {
        var code = Code("MainWindow.xaml.cs");

        Assert.Contains("RenderRecent(state.Recent);", Member(code, "public void Render(DictationViewState state)"), StringComparison.Ordinal);
        var constructor = Member(code, "public MainWindow(");
        Assert.Contains("ShowRecentLabels();", constructor, StringComparison.Ordinal);
        // The menu item reaches controller.CopyLastText through the menu's
        // commands (TrayMenuTests).
        Assert.Contains("tray.Chosen += async (_, item) => await trayCommands.ChooseAsync(item);", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void The_menu_shows_the_items_only_while_a_result_is_held_and_never_the_words()
    {
        var idle = new DictationTrayState(TrayIcon.Ready, DictationCopy.TrayOff, DictationCopy.PauseLabel, true);
        Assert.DoesNotContain(DictationTrayItem.CopyLastText, DictationTrayMenu.Items(idle, learningUndoable: false));

        var held = idle with { Recent = new DictationRecentMenu("Copy last text · 3:41 PM", CanCopy: false, NeedsRecovery: false) };
        var copy = Assert.Single(DictationTrayMenu.Entries(held, learningUndoable: false), e => e.Item == DictationTrayItem.CopyLastText);
        Assert.Equal(("Copy last text · 3:41 PM", false), (copy.Label, copy.Enabled));
        Assert.DoesNotContain(DictationTrayItem.RecentRecovery, DictationTrayMenu.Items(held, learningUndoable: false));

        var lost = held with { Recent = held.Recent! with { NeedsRecovery = true } };
        Assert.Equal(
            [DictationTrayItem.RecentRecovery, DictationTrayItem.CopyLastText],
            DictationTrayMenu.Items(lost, learningUndoable: false).Take(2));
        // The tray draws what the tested state holder placed from these entries.
        Assert.Contains("var placed = showing.Placed;", Member(Code("DictationTray.cs"), "private void Redraw()"), StringComparison.Ordinal);
        // The menu record carries a label, a switch and a flag: no text field to leak.
        Assert.Equal(
            ["CopyLabel", "CanCopy", "NeedsRecovery"],
            typeof(DictationRecentMenu).GetConstructors().Single().GetParameters().Select(p => p.Name!));
    }

    [Fact]
    public void Copy_writes_through_the_same_Win32_writer_as_the_refused_paste_fallback()
    {
        var code = Code("WindowsDictationPlatform.cs");

        Assert.Contains(
            "public IClipboard Clipboard { get; } = new Win32Clipboard(logs.CreateLogger<Win32Clipboard>());",
            code,
            StringComparison.Ordinal);
        Assert.Contains("new Win32Clipboard(logs.CreateLogger<Win32Clipboard>())", Member(code, "public DictationInsertionEdges CreateInsertionEdges()"), StringComparison.Ordinal);
    }
}
