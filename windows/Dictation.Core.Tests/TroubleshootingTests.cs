using System.Xml.Linq;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// The window's closed Troubleshooting section (the Mac's disclosure: this
/// copy's version, build and path, Show This Copy in Finder, Open Log Folder)
/// and its footer (the Mac's sentence, then Windows' own about closing). The
/// Mac's <c>installationIdentity</c> scenario is ported by name; the WPF half
/// is pinned on its source only, and has never been seen on Windows.
/// </summary>
public sealed class TroubleshootingTests
{
    private const string Exe = @"C:\Program Files\Dictation\Dictation.exe";

    // ─── Show this copy in File Explorer ────────────────────────────────

    [Fact]
    public void Show_this_copy_selects_it_in_File_Explorer_and_leaves_the_status_alone()
    {
        using var rig = new Rig();
        var (status, renders) = (rig.View.Last.Status, rig.View.States.Count);

        Assert.True(rig.Controller.ShowThisCopy(Exe));

        Assert.Equal([Exe], rig.Platform.ShownFiles);
        Assert.Equal(status, rig.View.Last.Status);
        Assert.Equal(renders, rig.View.States.Count);
    }

    [Fact]
    public void A_copy_File_Explorer_will_not_show_is_pointed_at_in_the_status_line_without_its_path()
    {
        using var rig = new Rig();
        rig.Platform.FileShows = false;

        Assert.False(rig.Controller.ShowThisCopy(Exe));

        Assert.Equal(DictationCopy.ThisCopyNotShown, rig.View.Last.Status);
        Assert.Contains(DictationCopy.TroubleshootingHeading, rig.View.Last.Status, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", rig.View.Last.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void A_File_Explorer_that_throws_is_logged_by_its_type_only()
    {
        var log = new MemoryLog();
        using var rig = new Rig(logs: log);
        rig.Platform.FileShows = null;

        Assert.False(rig.Controller.ShowThisCopy(Exe));

        Assert.Equal(DictationCopy.ThisCopyNotShown, rig.View.Last.Status);
        Assert.Contains(log.Lines, l => l.Contains("Win32Exception", StringComparison.Ordinal));
        // The exception's message holds a path with a user's name in it.
        Assert.DoesNotContain(log.Lines, l => l.Contains("someone", StringComparison.Ordinal));
    }

    [Fact]
    public void After_quitting_nothing_is_shown()
    {
        using var rig = new Rig();
        rig.Controller.Dispose();

        Assert.False(rig.Controller.ShowThisCopy(Exe));
        Assert.Empty(rig.Platform.ShownFiles);
    }

    /// <summary>The Mac's "installation identity distinguishes builds". The
    /// Applications-folder warning is the Mac's alone: Windows has no folder
    /// a grant depends on.</summary>
    [Fact]
    public void Installation_identity_distinguishes_builds_and_knows_when_it_has_no_path()
    {
        var installed = new DictationInstallation("0.1.0", "20261006120000", Exe);
        Assert.Contains("20261006120000", installed.VersionLabel, StringComparison.Ordinal);
        Assert.True(installed.PathKnown);

        var developer = new DictationInstallation("0.1.0", null, Exe);
        Assert.Contains("Build not recorded", developer.VersionLabel, StringComparison.Ordinal);

        var unknown = DictationInstallation.Of(typeof(TroubleshootingTests).Assembly, processPath: null);
        Assert.Equal(DictationInstallation.UnknownPath, unknown.Path);
        Assert.False(unknown.PathKnown);
        Assert.False(new DictationInstallation("0.1.0", null, " ").PathKnown);
    }

    // ─── The footer and the pointers to the log ─────────────────────────

    [Fact]
    public void The_footer_says_what_Windows_keeps_after_the_shared_privacy_line()
    {
        const string opening = "Your audio stays on this device. No account is required.";
        const string recordings = "No transcript history is saved, and a recording only until its words are in, or for you to recover.";
        // The Mac keeps an encrypted history (2026-10-16), and since 2026-10-30
        // its window's rail says that audio stays on the Mac with no account,
        // so its Settings footer says only what Settings decides. Windows has
        // no rail and keeps the privacy line as its footer's opening.
        const string macHistory = "History is encrypted on this Mac and kept as long as you choose above. Apart from it, one dictation that couldn't be transcribed is kept as audio, not encrypted, for up to 7 days so you can recover it, whatever Keep recordings says.";
        const string obsoleteRecordings = "Recordings and transcript history are not saved to disk.";
        var swift = File.ReadAllText(Path.Combine(
            DictationOverridesTests.WindowsRoot(), "..", "macos", "Sources", "DictationApp", "DictationSettingsView.swift"));

        Assert.Contains(macHistory, swift, StringComparison.Ordinal);
        Assert.DoesNotContain(opening, swift, StringComparison.Ordinal);
        Assert.DoesNotContain(recordings, swift, StringComparison.Ordinal);
        Assert.StartsWith(opening + " " + recordings + " ", DictationCopy.Footer, StringComparison.Ordinal);
        // Both platforms spool a take and retain a failed one for recovery.
        // Neither footer may claim that recordings never reach disk.
        Assert.DoesNotContain(obsoleteRecordings, swift, StringComparison.Ordinal);
        Assert.DoesNotContain(obsoleteRecordings, DictationCopy.Footer, StringComparison.Ordinal);
        // Closing never quits: the footer says where the app goes and names
        // the menu item that exits, as the menu labels it.
        Assert.Contains("notification area", DictationCopy.Footer, StringComparison.Ordinal);
        Assert.Contains(DictationCopy.QuitDictation, DictationCopy.Footer, StringComparison.Ordinal);
        Assert.DoesNotContain("History is off", DictationCopy.Footer, StringComparison.Ordinal);
        Assert.DoesNotContain("keeps dictation running", DictationCopy.Footer, StringComparison.Ordinal);
    }

    [Fact]
    public void Failure_sentences_say_where_Open_log_folder_is_now_that_it_is_under_Troubleshooting()
    {
        Assert.Equal("Troubleshooting", DictationCopy.TroubleshootingHeading);
        Assert.Equal("For details, choose Open log folder under Troubleshooting.", DictationCopy.LogDetails);
        Assert.EndsWith(DictationCopy.LogDetails, DictationCopy.ModelDidNotLoad, StringComparison.Ordinal);
        Assert.EndsWith(DictationCopy.LogDetails, DictationCopy.LaunchAtLoginNotTurnedOn, StringComparison.Ordinal);
    }

    // ─── The WPF half: pins on the source, not evidence it behaves ──────

    [Fact]
    public void Troubleshooting_is_a_closed_section_with_this_copy_s_identity_Show_in_File_Explorer_and_Open_log_folder()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var section = Named(xaml, "TroubleshootingSection");
        var column = section.Parent!.Elements().ToList();

        Assert.Equal("Expander", section.Name.LocalName);
        Assert.Equal("False", (string?)section.Attribute("IsExpanded"));
        // After start at sign-in and its note, before the footer, which is last.
        Assert.Equal(column.IndexOf(Named(xaml, "LaunchAtLoginNote")) + 1, column.IndexOf(section));
        Assert.Equal(column.IndexOf(section) + 1, column.IndexOf(Named(xaml, "Footer")));
        Assert.Equal(column.Count - 1, column.IndexOf(Named(xaml, "Footer")));

        var inside = section.Descendants().Select(e => (string?)e.Attribute(X + "Name")).Where(n => n is not null).ToList();
        Assert.Equal(
            ["ThisCopyHeading", "ThisCopyVersion", "ThisCopyPath", "ShowThisCopyButton", "OpenLogFolderButton"],
            inside);
        var path = Named(xaml, "ThisCopyPath");
        Assert.Equal("TextBox", path.Name.LocalName);
        Assert.Equal("True", (string?)path.Attribute("IsReadOnly"));
        Assert.Equal("False", (string?)path.Attribute("IsUndoEnabled"));
        Assert.Equal("ShowThisCopyClick", (string?)Named(xaml, "ShowThisCopyButton").Attribute("Click"));
        Assert.Equal("OpenLogFolderClick", (string?)Named(xaml, "OpenLogFolderButton").Attribute("Click"));
        // Nothing in it is a live region.
        Assert.DoesNotContain(section.DescendantsAndSelf().SelectMany(e => e.Attributes()),
            a => a.Name.LocalName == "AutomationProperties.LiveSetting");
    }

    [Fact]
    public void The_section_shows_the_identity_the_launch_line_logs_and_its_button_reaches_the_controller()
    {
        var code = File.ReadAllText(AppFile("MainWindow.Header.cs")).ReplaceLineEndings("\n");
        var app = File.ReadAllText(AppFile("App.xaml.cs")).ReplaceLineEndings("\n");

        // One identity: the launch line's (App.OnStartup) and the window's.
        const string identity = "DictationInstallation.Of(typeof(App).Assembly, Environment.ProcessPath)";
        Assert.Contains(identity, app, StringComparison.Ordinal);
        Assert.Contains(identity, code, StringComparison.Ordinal);

        var labels = Member(code, "private void ShowHeaderAndTroubleshootingLabels()");
        Assert.Contains("TroubleshootingSection.Header = WithAccessKey(DictationCopy.TroubleshootingHeading, \"b\");", labels, StringComparison.Ordinal);
        Assert.Contains("ThisCopyHeading.Text = DictationCopy.ThisCopyHeading;", labels, StringComparison.Ordinal);
        Assert.Contains("ThisCopyVersion.Text = installation.VersionLabel;", labels, StringComparison.Ordinal);
        Assert.Contains("ThisCopyPath.Text = installation.Path;", labels, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(ThisCopyPath, DictationCopy.ThisCopyPathName);", labels, StringComparison.Ordinal);
        Assert.Contains("ShowThisCopyButton.IsEnabled = installation.PathKnown;", labels, StringComparison.Ordinal);
        Assert.Contains(
            "private void ShowThisCopyClick(object sender, RoutedEventArgs e) => controller.ShowThisCopy(installation.Path);",
            code,
            StringComparison.Ordinal);
        Assert.Contains("Footer.Text = DictationCopy.Footer;", File.ReadAllText(AppFile("MainWindow.xaml.cs")), StringComparison.Ordinal);

        var platform = File.ReadAllText(AppFile("WindowsDictationPlatform.cs")).ReplaceLineEndings("\n");
        var show = Member(platform, "public bool ShowInFolder(string file)");
        Assert.Contains("if (!File.Exists(file))", show, StringComparison.Ordinal);
        Assert.Contains(
            "Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), \"explorer.exe\")", show, StringComparison.Ordinal);
        Assert.Contains(
            "new ProcessStartInfo(explorer, DictationInstallation.SelectInExplorerArguments(file))", show, StringComparison.Ordinal);
        // ArgumentList would quote the whole switch around a path with a space.
        Assert.DoesNotContain("ArgumentList", show, StringComparison.Ordinal);
        Assert.Contains("error.GetType().Name", show, StringComparison.Ordinal);
    }

    /// <summary>The switch, then the path in quotes, whatever the path holds:
    /// a space (Program Files, a profile named "Avrit Raj") or a comma, which
    /// Explorer splits its switches on. Explorer's reading of it has never
    /// been seen here; this is the form it documents.</summary>
    [Theory]
    [InlineData(Exe)]
    [InlineData(@"C:\Users\Smith, John\AppData\Local\Programs\Dictation\Dictation.exe")]
    [InlineData(@"D:\Dictation\Dictation.exe")]
    public void File_Explorer_is_given_the_switch_then_the_whole_path_in_quotes(string path)
    {
        var arguments = DictationInstallation.SelectInExplorerArguments(path);

        Assert.StartsWith("/select,\"", arguments, StringComparison.Ordinal);
        Assert.EndsWith("\"", arguments, StringComparison.Ordinal);
        Assert.Equal(path, arguments["/select,\"".Length..^1]);
    }

    [Fact]
    public void Closing_the_window_hides_it_whatever_the_controller_says_it_is_and_only_Quit_closes_it()
    {
        // The controller decides (Closing_the_window_with_dictation_off_hides_it_too…);
        // the window hides on Hide, waits on Wait, and closes otherwise.
        var code = File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");
        var closing = Member(code, "private void OnClosing(");

        Assert.Contains("var decision = controller.RequestClose(quitting);", closing, StringComparison.Ordinal);
        Assert.Contains("case DictationCloseAction.Hide:\n                e.Cancel = true;\n                Hide();", closing, StringComparison.Ordinal);
        Assert.Contains("void IDictationView.Quit() => Quit();", code, StringComparison.Ordinal);
    }
}
