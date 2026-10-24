using System.Xml.Linq;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// LIMIT-WIN-A's surfaces: the notification-area menu's Recover last
/// dictation, on the model the tray draws; then the Win32 and WPF halves
/// pinned on their source — pins, not evidence. WASAPI, the hook and WPF
/// cannot run here; each pin fails if the thing it names is deleted or moved.
/// </summary>
public sealed class TakeSurfaceTests
{
    private const DictationTrayItem Recover = DictationTrayItem.RecoverLastDictation;
    private const DictationTrayItem Sep = DictationTrayItem.Separator;

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static DictationTrayState Tray(DictationSavedTakeMenu? saved, DictationRecentMenu? recent = null) =>
        new(TrayIcon.Ready, DictationCopy.TrayListening, DictationCopy.PauseLabel, CanToggle: true, recent, SavedTake: saved);

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine([DictationOverridesTests.WindowsRoot(), .. path])).ReplaceLineEndings("\n");

    private static XElement Named(XDocument xaml, string name) =>
        xaml.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    // ─── The menu ───────────────────────────────────────────────────────

    [Fact]
    public void Recover_last_dictation_leads_the_menu_in_a_group_of_its_own_while_a_take_is_kept()
    {
        var saved = new DictationSavedTakeMenu("Recover last dictation · 3:40 PM", CanRecover: true);
        var recent = new DictationRecentMenu("Copy last text · 3:41 PM", CanCopy: true, NeedsRecovery: false);

        var items = DictationTrayMenu.Layout(DictationTrayMenu.Items(Tray(saved, recent), learningUndoable: false));

        Assert.Equal([Recover, Sep, DictationTrayItem.CopyLastText, Sep], items.Take(4));
        Assert.DoesNotContain(Recover, DictationTrayMenu.Items(Tray(null, recent), learningUndoable: false));
        var entry = Assert.Single(DictationTrayMenu.Entries(Tray(saved), false), e => e.Item == Recover);
        Assert.Equal(new DictationTrayEntry(Recover, "Recover last dictation · 3:40 PM", Enabled: true), entry);
        var waiting = Assert.Single(
            DictationTrayMenu.Entries(Tray(saved with { CanRecover = false }), false), e => e.Item == Recover);
        Assert.False(waiting.Enabled);
    }

    [Fact]
    public void The_menu_s_slots_hold_the_new_line_and_place_it()
    {
        Assert.Equal(Recover, DictationTrayMenu.Slots[0]);
        var placed = DictationTrayMenu.Place(DictationTrayMenu.Entries(
            Tray(new DictationSavedTakeMenu(DictationCopy.RecoveringLastDictation, CanRecover: false)), false));
        Assert.Equal(DictationCopy.RecoveringLastDictation, placed[0]!.Label);
    }

    [Fact]
    public void The_lines_name_the_time_and_never_a_word()
    {
        Assert.Equal("Recover last dictation · 3:40 PM", DictationCopy.RecoverLastDictationAt("3:40 PM"));
        Assert.Equal("Saved recording", DictationCopy.SavedTakeTitle);
        Assert.Equal("Couldn't transcribe it. 1:12 long.", DictationCopy.SavedTakeDetail(TimeSpan.FromSeconds(72)));
        Assert.Equal(
            "Earlet closed before it was transcribed. 1:12 long.",
            DictationCopy.SavedTakeDetailInterrupted(TimeSpan.FromSeconds(72)));
        // The design's Windows column (LIMIT-DESIGN §5, Copy).
        Assert.Equal(
            "Couldn't transcribe — the recording is saved. Use Recover last dictation to try again",
            DictationCopy.HudTakeSaved);
        Assert.Equal(
            "Microphone stopped at 4:12 — your words up to then went in",
            DictationCopy.HudTakeStoppedInserted(DictationTakeStop.MicrophoneFailed, TimeSpan.FromSeconds(252)));
    }

    // ─── The Win32 half ─────────────────────────────────────────────────

    [Fact]
    public void The_WASAPI_device_hears_a_stream_that_stops_by_itself_and_reports_it_once_on_the_pool()
    {
        var device = Source("Dictation.Platform", "Audio", "WasapiMicrophoneCaptureDevice.cs");
        var fault = Source("Dictation.Platform", "Audio", "WasapiMicrophoneCaptureDevice.Fault.cs");

        Assert.Contains("IAudioCaptureFaultWatch", device, StringComparison.Ordinal);
        var start = Member(device, "public bool Start()");
        Assert.Contains("capture.RecordingStopped += OnRecordingStopped;", start, StringComparison.Ordinal);
        var stop = Member(device, "public void Stop()");
        var asked = stop.IndexOf("_stopping = capture;", StringComparison.Ordinal);
        var stopping = stop.IndexOf("capture.StopRecording();", StringComparison.Ordinal);
        Assert.True(asked >= 0 && stopping > asked, "a stop it asked for is marked before it is asked");
        var release = Member(device, "private void Release()");
        Assert.Contains("_capture.RecordingStopped -= OnRecordingStopped;", release, StringComparison.Ordinal);

        var handler = Member(fault, "private void OnRecordingStopped(");
        Assert.Contains("e.Exception is not { } error", handler, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(sender, _stopping)", handler, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(sender, _faulted)", handler, StringComparison.Ordinal);
        Assert.Contains("ThreadPool.QueueUserWorkItem(_ => CaptureFailed?.Invoke(this, fault));", handler, StringComparison.Ordinal);
        // The type and the HRESULT, never the driver's message.
        Assert.DoesNotContain(".Message", fault, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hook_s_asking_Escape_reaches_the_controller_as_its_own_cancel()
    {
        var platform = Source("Dictation.App", "WindowsDictationPlatform.cs");
        Assert.Contains("inner.ConsumeCancelRequest(out var byChord, out var asked)", platform, StringComparison.Ordinal);
        Assert.Contains("asked ? TriggerCancel.EscapeAsked", platform, StringComparison.Ordinal);
        Assert.Contains("set => inner.EscapeAsksFirst = value;", platform, StringComparison.Ordinal);
    }

    // ─── The window ─────────────────────────────────────────────────────

    [Fact]
    public void The_Saved_recording_section_sits_first_under_the_header_hidden_until_a_take_is_kept()
    {
        var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App", "MainWindow.xaml"));
        var section = Named(xaml, "SavedTakeSection");
        var column = section.Parent!.Elements().ToList();

        Assert.Equal("Collapsed", (string?)section.Attribute("Visibility"));
        Assert.Equal(column.IndexOf(Named(xaml, "HeaderSection")) + 1, column.IndexOf(section));
        Assert.Equal("RecoverClick", (string?)Named(xaml, "RecoverButton").Attribute("Click"));
        Assert.Equal("DiscardSavedTakeClick", (string?)Named(xaml, "DiscardSavedTakeButton").Attribute("Click"));
        // No live region: the status line says what happened to it.
        Assert.DoesNotContain(section.DescendantsAndSelf().Attributes(), a => a.Name.LocalName == "AutomationProperties.LiveSetting");

        var code = Source("Dictation.App", "MainWindow.SavedTake.cs");
        Assert.Contains("=> controller.RecoverLastDictation();", code, StringComparison.Ordinal);
        Assert.Contains("=> controller.DiscardSavedTake();", code, StringComparison.Ordinal);
        var render = Member(code, "private void RenderSavedTake(");
        Assert.Contains("SavedTakeSection.Visibility = Visibility.Collapsed;", render, StringComparison.Ordinal);
        Assert.Contains("RecoverButton.IsEnabled = saved.CanRecover || saved.Recovering;", render, StringComparison.Ordinal);
        var window = Source("Dictation.App", "MainWindow.xaml.cs");
        Assert.Contains("RenderSavedTake(state.SavedTake);", window, StringComparison.Ordinal);
        Assert.Contains("ShowSavedTakeLabels();", window, StringComparison.Ordinal);
    }

    [Fact]
    public void A_render_never_reads_or_writes_the_user_s_recordings()
    {
        using var parts = DictationRenderComposition.For(DictationWindowRenderScenario.Starting);
        Assert.StartsWith(parts.Root, parts.Options.Takes.Directory, StringComparison.Ordinal);
    }
}
