using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dictation.Core.Tests.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using static Dictation.Core.Tests.DictationHudGeometryTests;

namespace Dictation.Core.Tests;

/// <summary>
/// "Try dictation", the macOS window's practice field, on Windows: shown while
/// dictation is on and the microphone is not blocked, open until a dictation
/// has delivered its words, and one remembered boolean. The completed-setup
/// half of the Mac's <c>AttemptDiagnosticsTests.modelWiring</c> and the
/// cancel case of <c>ReleaseCancelTests</c> are ported under their scenario
/// names; the collapse, which the Mac leaves to its view, is
/// <see cref="PracticeDisclosure"/> here and tested below. The WPF half is
/// pinned on its source only: WPF cannot run on Linux.
/// </summary>
public sealed class DictationPracticeTests
{
    private const string Words = "hello world";

    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    // ─── Only a delivery completes setup ────────────────────────────────

    /// <summary>The Mac's six scenarios, then the Windows endings that
    /// decide the same question differently or not at all on the Mac.</summary>
    public static TheoryData<string, bool> Scenarios => new()
    {
        { "inserted", true },
        { "paste_failed", false },
        { "no_text", false },
        { "privacy_refused", false },
        { "microphone_permission", false },
        { "cancelled", false },
        // ReleaseCancelTests: the late words of a cancelled transcription.
        { "cancelled_while_transcribing", false },
        // Typed, then the clipboard did not come back: the words landed.
        { "clipboard_restore_failed", true },
        { "no_window", false },
        // Inserted at the recording limit, as the Mac's success at the ceiling.
        { "stopped_at_the_limit", true },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Only_insertion_completes_setup(string scenario, bool completed)
    {
        var logDirectory = Directory.CreateTempSubdirectory("dictation-log-").FullName;
        using var logs = FileLoggerFactory.TryCreate(logDirectory, out _)!;
        using var rig = new Rig(logs: logs);
        await rig.EnableAsync();
        Assert.True(rig.View.Last.PracticeStartsExpanded);

        DictateEndingIn(rig, scenario);

        Assert.Equal(!completed, rig.View.Last.PracticeStartsExpanded);
        Assert.Equal(completed, rig.Settings.Load().CompletedFirstDictation);
        Assert.DoesNotContain(Words, File.ReadAllText(rig.Settings.Path), StringComparison.Ordinal);
        // The Mac's check too: the words are in neither the settings nor the log.
        var log = ReadShared(Path.Combine(logDirectory, FileLoggerFactory.FileName));
        Assert.Contains("Dictation enabled on", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Words, log, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(completed, log.Contains("First dictation delivered", StringComparison.Ordinal));

        // Setup progress survives a relaunch without storing text.
        using var next = new Rig(beforeStart: r => CopySettings(rig, r));
        await next.Startup.Enabling;
        next.Dispatcher.Drain();
        Assert.Equal(!completed, next.View.Last.PracticeStartsExpanded);
    }

    private static void DictateEndingIn(Rig rig, string scenario)
    {
        switch (scenario)
        {
            case "inserted":
                rig.Dictate(Inserted);
                break;
            case "paste_failed":
                rig.Platform.Paste.Succeeds = false;
                rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboard);
                break;
            case "no_text":
                rig.Platform.Worker.Transcript = string.Empty;
                rig.Dictate(v => v.Status == "Nothing was heard. Speak while you hold the key.");
                break;
            case "privacy_refused":
                rig.Platform.SecureField.Next = SecureFieldState.Secure;
                rig.Press();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.SecureFieldAtPress);
                break;
            case "microphone_permission":
                rig.Platform.Access.Access = MicrophoneAccess.Denied;
                rig.Platform.Microphone.StartSucceeds = false;
                rig.Dictate(v => v.Status == DictationCopy.MicrophoneBlocked);
                break;
            case "cancelled":
                rig.Press();
                rig.Controller.Cancel();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
                break;
            case "cancelled_while_transcribing":
            {
                using var hold = new ManualResetEventSlim();
                rig.Platform.Worker.HoldTranscribe = hold;
                rig.Press();
                rig.Release();
                rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
                rig.Controller.Cancel();
                hold.Set();
                rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
                Thread.Sleep(100);
                rig.Dispatcher.Drain();
                Assert.Equal(0, rig.Platform.Paste.Count);
                break;
            }
            case "clipboard_restore_failed":
            {
                var land = rig.Platform.Paste.Landed;
                rig.Platform.Paste.Landed = () =>
                {
                    land?.Invoke();
                    rig.Platform.Clipboard.Accepts = false;
                };
                rig.Dictate(v => v.Status == "Text inserted, but your clipboard could not be put back.");
                break;
            }
            case "no_window":
                rig.Platform.Target.Next = FocusedApplicationResult.NoFocusedWindow;
                rig.Dictate(v => v.Status == DictationCopy.KeptOnClipboardNoWindow);
                break;
            case "stopped_at_the_limit":
                rig.Press();
                rig.Clock.Advance(DictationHoldPolicy.Windows.Ceiling);
                rig.Controller.Tick();
                rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.InsertedAfterStop(new(DictationHoldStop.Ceiling, DictationHoldPolicy.Windows.Ceiling), rig.Controller.HoldKey));
                rig.Release();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "no such scenario");
        }
    }

    /// <summary>The log as the open logger leaves it, readable while it writes.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(stream).ReadToEnd();
    }

    /// <summary>The next launch reads the file this one left, byte for byte.</summary>
    private static void CopySettings(Rig from, Rig to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to.Settings.Path)!);
        File.Copy(from.Settings.Path, to.Settings.Path);
    }

    [Fact]
    public async Task A_delivered_dictation_saves_the_flag_once_not_after_every_dictation()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.True(rig.Settings.Load().CompletedFirstDictation);

        // Changed outside this run: a second save would write this run's
        // language back over it.
        var file = JsonNode.Parse(File.ReadAllText(rig.Settings.Path))!.AsObject();
        file["language"] = "de";
        File.WriteAllText(rig.Settings.Path, file.ToJsonString());

        var before = rig.View.States.Count;
        rig.Dictate(v => rig.View.States.Count > before && Inserted(v));

        Assert.Equal("de", JsonNode.Parse(File.ReadAllText(rig.Settings.Path))!["language"]!.GetValue<string>());
        Assert.False(rig.View.Last.PracticeStartsExpanded);
    }

    /// <summary>
    /// A quit asked for while the first dictation is pasting waits for the
    /// clipboard, then closes the window, which disposes the controller. The
    /// words landed, so the flag must be saved before that: the quit is posted
    /// behind the dictation's own ending.
    /// </summary>
    [Fact]
    public async Task A_quit_during_the_first_insertion_still_completes_setup()
    {
        using var pasteHold = new ManualResetEventSlim();
        var log = new MemoryLog();
        using var rig = new Rig(logs: log, options: o => o with { CloseWaitLimit = TimeSpan.FromMinutes(1) });
        rig.Platform.Paste.Hold = pasteHold;
        rig.View.OnQuit = rig.Controller.Dispose;
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        Assert.Equal(DictationCloseAction.Wait, rig.Controller.RequestClose(quit: true).Action);

        pasteHold.Set();
        // Both of the session's posts queued, as on the UI thread.
        log.AwaitRaised();
        rig.DrainUntil(() => rig.View.QuitRequests == 1);

        Assert.True(rig.Settings.Load().CompletedFirstDictation);
        Assert.Equal(1, rig.Platform.Paste.Count);
    }

    /// <summary>The deferred quit belongs to the controller that posted it:
    /// one closed in between (Windows ending the session) asks for nothing,
    /// since closing a closed window throws.</summary>
    [Fact]
    public async Task A_deferred_quit_is_not_asked_for_after_the_controller_is_gone()
    {
        using var pasteHold = new ManualResetEventSlim();
        var log = new MemoryLog();
        using var rig = new Rig(logs: log, options: o => o with { CloseWaitLimit = TimeSpan.FromMinutes(1) });
        rig.Platform.Paste.Hold = pasteHold;
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        Assert.Equal(DictationCloseAction.Wait, rig.Controller.RequestClose(quit: true).Action);
        rig.View.OnRender = s =>
        {
            if (Inserted(s))
            {
                rig.Controller.Dispose();
            }
        };

        pasteHold.Set();
        log.AwaitRaised();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        rig.Dispatcher.Drain();

        Assert.Equal(0, rig.View.QuitRequests);
        Assert.True(rig.Settings.Load().CompletedFirstDictation);
    }

    /// <summary>The settings rule every other value keeps: a file the launch
    /// could not read is not replaced, and the flag holds for this run only.</summary>
    [UnreadableFileFact]
    public async Task A_launch_that_could_not_read_its_settings_completes_setup_for_this_run_only()
    {
        const string Chosen = """{ "hold_virtual_key": 124, "language": "de", "learn_from_corrections": false }""";
        IDisposable? locked = null;
        using var rig = new Rig(beforeStart: r =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(r.Settings.Path)!);
            File.WriteAllText(r.Settings.Path, Chosen);
            locked = UnreadableFileFactAttribute.Lock(r.Settings.Path);
        });
        locked!.Dispose();

        await rig.EnableAsync();
        rig.Dictate(Inserted);

        Assert.False(rig.View.Last.PracticeStartsExpanded);
        Assert.Equal(Chosen, File.ReadAllText(rig.Settings.Path));
    }

    /// <summary>
    /// The practice field is in Dictation's own window, and a fix typed there
    /// is practice, not a correction: the span observer refuses its own
    /// process (<see cref="SpanObserver.ReasonSelf"/>) before it reads the
    /// field, so nothing is learned. The dictation still completes setup.
    /// </summary>
    [Fact]
    public async Task A_fix_typed_into_the_practice_field_is_never_learned()
    {
        using var rig = new Rig(learning: true);
        rig.Platform.Worker.Transcript = "we use trovio for the checks";
        rig.Platform.Field.FixAfterEachPaste("trovio", "Trovioo");
        rig.Platform.Target.Next = FocusedApplicationResult.Resolved("Dictation", Environment.ProcessId, "Dictation");
        rig.Platform.Field.Identity = new SpanObserverTargetIdentity(
            "Dictation", Environment.ProcessId, "HwndWrapper[Dictation.exe;;]", 7, false);
        await rig.EnableAsync();

        rig.Dictate(Inserted);
        rig.WaitForWatch();

        Assert.Equal(1, rig.Platform.SpanTargets);
        Assert.Equal(0, rig.Platform.Field.Reads);
        Assert.False(File.Exists(rig.LearningFile));
        Assert.False(rig.View.Last.PracticeStartsExpanded);
    }

    // ─── The setting ────────────────────────────────────────────────────

    [Fact]
    public void The_flag_is_false_when_absent_or_mistyped_and_round_trips_beside_keys_it_does_not_own()
    {
        var dir = Directory.CreateTempSubdirectory("dictation-settings-").FullName;
        var path = Path.Combine(dir, "settings.json");
        var store = new DictationSettingsStore(path, NullLogger<DictationSettingsStore>.Instance);
        Assert.False(store.Load().CompletedFirstDictation);

        // A file from before the flag existed.
        File.WriteAllText(path, """{ "enabled": true, "a_newer_switch": 1 }""");
        Assert.False(store.Load().CompletedFirstDictation);
        File.WriteAllText(path, """{ "completed_first_dictation": "yes" }""");
        Assert.False(store.Load().CompletedFirstDictation);

        File.WriteAllText(path, """{ "enabled": true, "a_newer_switch": 1 }""");
        var loaded = store.Load();
        Assert.True(store.Save(loaded with { CompletedFirstDictation = true }));

        var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(file["completed_first_dictation"]!.GetValue<bool>());
        Assert.Equal(1, file["a_newer_switch"]!.GetValue<int>());
        Assert.Equal(loaded with { CompletedFirstDictation = true }, store.Load());
    }

    // ─── When the window shows it ───────────────────────────────────────

    [Fact]
    public async Task Practice_shows_while_dictation_is_on_and_through_its_own_dictation()
    {
        using var rig = new Rig();
        await rig.Startup.Enabling;
        Assert.False(rig.View.Last.PracticeVisible);

        var enabling = rig.View.States.Count;
        await rig.EnableAsync();
        Assert.Contains(rig.View.States.Skip(enabling), s => s.Status == DictationCopy.Preparing && !s.PracticeVisible);
        Assert.True(rig.View.Last.PracticeVisible);
        Assert.True(rig.View.Last.PracticeStartsExpanded);

        // Every state of the practice hold keeps the field it types into.
        var holding = rig.View.States.Count;
        rig.Dictate(Inserted);
        Assert.All(rig.View.States.Skip(holding), s => Assert.True(s.PracticeVisible, s.Status));
        Assert.Contains(rig.View.States.Skip(holding), s => s.MicrophoneOpen);

        rig.Controller.Pause();
        Assert.False(rig.View.Last.PracticeVisible);
        Assert.False(rig.View.Last.PracticeStartsExpanded);
    }

    [Theory]
    [InlineData(MicrophoneAccess.Denied)]
    [InlineData(MicrophoneAccess.DeniedByPolicy)]
    public async Task A_blocked_microphone_hides_practice_until_Windows_allows_it_again(MicrophoneAccess blocked)
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = blocked;
        await rig.EnableAsync();
        Assert.True(rig.Controller.IsEnabled);
        Assert.False(rig.View.Last.PracticeVisible);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();

        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.True(rig.View.Last.PracticeVisible);
    }

    /// <summary>
    /// The status line moves on — a hold's "Listening…", a new hold key, a new
    /// language — while Windows still blocks the microphone. None of those
    /// sentences brings the practice field back; only a probe that finds
    /// access allowed does, and it leaves the line it finds alone.
    /// </summary>
    [Theory]
    [InlineData(MicrophoneAccess.Denied)]
    [InlineData(MicrophoneAccess.DeniedByPolicy)]
    public async Task Practice_stays_hidden_through_later_sentences_while_the_microphone_is_blocked(MicrophoneAccess blocked)
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = blocked;
        rig.Platform.MicrophoneStarts = false;
        await rig.EnableAsync();
        var since = rig.View.States.Count;

        // The hold listens, the microphone does not open, and it ends blocked.
        rig.Press();
        rig.Release();
        rig.DrainUntil(() => rig.View.States.Count > since + 1
            && DictationCopy.IsMicrophoneBlocked(new DictationStatus(rig.View.Last.Status)));
        Assert.Contains(rig.View.States.Skip(since), s => s.Status.StartsWith("Listening", StringComparison.Ordinal));

        // Re-armed on a new key with a microphone that opens. As far as this
        // run knows Windows still blocks it, so the blocked sentence stays
        // under the header's "Microphone blocked" instead of inviting a hold
        // (WindowHeaderIntegrationTests); a cancelled hold delivered nothing,
        // so it proves nothing about access either.
        rig.Platform.MicrophoneStarts = true;
        rig.Controller.ChooseHoldKey(
            DictationHotkeys.HoldChoices.First(c => c.VirtualKey != rig.Controller.HoldKey.VirtualKey).VirtualKey);
        Assert.True(DictationCopy.IsMicrophoneBlocked(new DictationStatus(rig.View.Last.Status)), rig.View.Last.Status);
        Assert.Equal(DictationGlyph.MicrophoneBlocked, rig.View.Last.Header.Glyph);
        rig.Press();
        rig.Controller.Cancel();
        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
        rig.Controller.ChooseLanguage("de");
        var language = rig.View.Last.Status;
        Assert.Equal(DictationCopy.LanguageChanged(SpeechLanguages.Find("de")), language);
        Assert.All(rig.View.States.Skip(since), s => Assert.False(s.PracticeVisible, s.Status));

        // Still blocked: the recheck on activation changes nothing.
        rig.Controller.RecheckMicrophone();
        Assert.False(rig.View.Last.PracticeVisible);
        Assert.Equal(language, rig.View.Last.Status);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();

        Assert.True(rig.View.Last.PracticeVisible);
        Assert.Equal(language, rig.View.Last.Status);
    }

    /// <summary>Enabling again asks Windows afresh: what an earlier run of
    /// dictation learned does not hide the field.</summary>
    [Fact]
    public async Task Enabling_again_asks_about_the_microphone_afresh()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.False(rig.View.Last.PracticeVisible);
        rig.Controller.Pause();

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        await rig.EnableAsync();

        Assert.Equal(DictationCopy.Armed(rig.Controller.HoldKey), rig.View.Last.Status);
        Assert.True(rig.View.Last.PracticeVisible);
    }

    [Fact]
    public async Task A_recheck_while_paused_keeps_the_paused_line()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.True(rig.Controller.TryPause());
        // Paused, and still saying what the notification-area icon warns of.
        Assert.Equal(DictationCopy.PausedMicrophoneBlocked, rig.View.Last.Status);

        rig.Platform.Access.Access = MicrophoneAccess.Allowed;
        rig.Controller.RecheckMicrophone();
        Assert.Equal(DictationCopy.Paused, rig.View.Last.Status);

        // Enabled again, the field is back: the access was found allowed.
        await rig.EnableAsync();
        Assert.True(rig.View.Last.PracticeVisible);
    }

    /// <summary>
    /// The switch was turned on in Settings and the user dictated into
    /// another app without coming back to this window, so nothing asked
    /// again. A dictation that delivered its words had the microphone open,
    /// so the field comes back with it.
    /// </summary>
    [Fact]
    public async Task A_delivered_dictation_shows_practice_again_without_a_recheck()
    {
        using var rig = new Rig();
        rig.Platform.Access.Access = MicrophoneAccess.Denied;
        await rig.EnableAsync();
        Assert.False(rig.View.Last.PracticeVisible);
        rig.Platform.Access.Access = MicrophoneAccess.Allowed;

        var since = rig.View.States.Count;
        rig.Dictate(Inserted);

        Assert.True(rig.View.Last.PracticeVisible);
        // Not before the words landed: the hold itself proves nothing yet.
        Assert.False(rig.View.States[since].PracticeVisible);
    }

    [Fact]
    public async Task An_enable_that_failed_shows_no_practice()
    {
        using var rig = new Rig(beforeStart: r => r.Copy.Delete("resources/model/model.bin"));
        await rig.EnableAsync();

        Assert.Equal(DictationCopy.ResourcesMissing, rig.View.Last.Status);
        Assert.False(rig.View.Last.PracticeVisible);
    }

    [Fact]
    public async Task The_window_is_told_the_microphone_is_open_only_while_the_hold_listens()
    {
        using var rig = new Rig();
        using var hold = new ManualResetEventSlim();
        rig.Platform.Worker.HoldTranscribe = hold;
        await rig.EnableAsync();
        Assert.False(rig.View.Last.MicrophoneOpen);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.True(rig.View.Last.MicrophoneOpen);
        Assert.StartsWith("Listening", rig.View.Last.Status, StringComparison.Ordinal);

        rig.Release();
        rig.DrainUntil(() => rig.View.Last.Status == DictationCopy.Transcribing);
        Assert.False(rig.View.Last.MicrophoneOpen);
        hold.Set();
        rig.DrainUntil(() => Inserted(rig.View.Last));
        Assert.False(rig.View.Last.MicrophoneOpen);

        rig.Press();
        rig.Dispatcher.Drain();
        Assert.True(rig.View.Last.MicrophoneOpen);
        rig.Controller.Cancel();
        rig.DrainUntil(() => rig.View.Last.Status == "Cancelled. Nothing was inserted.");
        Assert.False(rig.View.Last.MicrophoneOpen);
        rig.Release();
    }

    // ─── Opening and closing (PracticeDisclosure) ───────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Appearing_opens_the_section_only_before_a_dictation_has_landed(bool startsExpanded) =>
        Assert.Equal(startsExpanded, new PracticeDisclosure().Next(true, startsExpanded, fieldHasFocus: false));

    [Fact]
    public void A_hidden_section_is_never_opened_or_closed()
    {
        var disclosure = new PracticeDisclosure();

        Assert.Null(disclosure.Next(false, true, false));
        Assert.Null(disclosure.Next(false, false, false));
        // It appears after the first dictation: collapsed, once.
        Assert.False(disclosure.Next(true, false, false));
        Assert.Null(disclosure.Next(true, false, false));

        // Hidden in the render that brings the first dictation: left alone,
        // and collapsed when it next appears.
        disclosure = new PracticeDisclosure();
        Assert.True(disclosure.Next(true, true, false));
        Assert.Null(disclosure.Next(false, false, false));
        Assert.False(disclosure.Next(true, false, false));
    }

    [Fact]
    public void The_first_delivered_dictation_closes_the_section_once()
    {
        var disclosure = new PracticeDisclosure();
        Assert.True(disclosure.Next(true, true, false));

        Assert.False(disclosure.Next(true, false, false));
        Assert.Null(disclosure.Next(true, false, false));
    }

    [Fact]
    public void A_practice_field_with_the_keyboard_stays_open_and_is_not_closed_later()
    {
        var disclosure = new PracticeDisclosure();
        Assert.True(disclosure.Next(true, true, false));

        Assert.Null(disclosure.Next(true, false, fieldHasFocus: true));
        // The words stay in sight after the keyboard leaves: the edge is gone.
        Assert.Null(disclosure.Next(true, false, fieldHasFocus: false));
    }

    [Fact]
    public void An_ordinary_render_leaves_the_users_choice_alone()
    {
        var disclosure = new PracticeDisclosure();
        Assert.True(disclosure.Next(true, true, false));

        Assert.Null(disclosure.Next(true, true, false));
        Assert.Null(disclosure.Next(true, true, true));
    }

    [Fact]
    public void Appearing_again_after_the_first_dictation_starts_collapsed()
    {
        // Left open by a focused field, then paused and enabled again: the
        // Mac rebuilds its section, collapsed, and so does this.
        var disclosure = new PracticeDisclosure();
        Assert.True(disclosure.Next(true, true, true));
        Assert.Null(disclosure.Next(true, false, true));

        Assert.Null(disclosure.Next(false, false, false));
        Assert.False(disclosure.Next(true, false, false));
    }

    // ─── The password-field probe in Dictation's own window ─────────────

    private sealed class CountingProbe(SecureFieldState answer) : ISecureFieldProbe
    {
        public int Calls;

        public SecureFieldState Probe()
        {
            Calls++;
            return answer;
        }
    }

    [Theory]
    [InlineData(SecureFieldState.Secure)]
    [InlineData(SecureFieldState.Unknown)]
    public void The_probe_asks_UI_Automation_only_outside_Dictations_own_windows(SecureFieldState outsideAnswer)
    {
        var outside = new CountingProbe(outsideAnswer);

        Assert.Equal(SecureFieldState.NotSecure, new OwnWindowSecureFieldProbe(outside, () => true).Probe());
        Assert.Equal(0, outside.Calls);

        Assert.Equal(outsideAnswer, new OwnWindowSecureFieldProbe(outside, () => false).Probe());
        Assert.Equal(1, outside.Calls);

        // A front-window check that fails is no answer: UI Automation decides.
        Assert.Equal(outsideAnswer, new OwnWindowSecureFieldProbe(
            outside, () => throw new InvalidOperationException("no foreground")).Probe());
        Assert.Equal(2, outside.Calls);
    }

    /// <summary>The app's edges wrap the UIA probe, and "NotSecure" for the
    /// own window holds only while no window of the app has a password box.</summary>
    [Fact]
    public void The_app_skips_UI_Automation_for_its_own_windows_which_have_no_password_box()
    {
        var edges = Member(File.ReadAllText(AppFile("WindowsDictationPlatform.cs")), "public DictationInsertionEdges CreateInsertionEdges()");
        Assert.Contains(
            "new OwnWindowSecureFieldProbe(\n                new UiaSecureFieldProbe(logs.CreateLogger<UiaSecureFieldProbe>()),\n                ForegroundWindow.IsThisProcess)",
            edges, StringComparison.Ordinal);
        Assert.Contains("processId == (uint)Environment.ProcessId", File.ReadAllText(AppFile("ForegroundWindow.cs")), StringComparison.Ordinal);

        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(AppFile("MainWindow.xaml"))!, "*.*")
                     .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PasswordBox", text, StringComparison.Ordinal);
            Assert.DoesNotContain("PasswordChar", text, StringComparison.Ordinal);
            Assert.DoesNotContain("UseSystemPasswordChar", text, StringComparison.Ordinal);
        }
    }

    // ─── The words: the Mac's, with the Windows key ─────────────────────

    [Fact]
    public void The_practice_copy_is_the_macOS_windows_words_with_the_Windows_key()
    {
        var key = DictationHotkeys.ResolveHold(_ => { }, 0, null);

        Assert.Equal(
            "Click below. Hold Right Ctrl, say a sentence, then release. Your words should appear here.",
            DictationCopy.PracticeInstructions(key));

        var swift = File.ReadAllText(Path.Combine(
            DictationOverridesTests.WindowsRoot(), "..", "macos", "Sources", "DictationApp", "DictationActivityView.swift"));
        foreach (var words in new[]
        {
            $"DisclosureGroup(\"{DictationCopy.PracticeHeading}\"",
            $"TextField(\"{DictationCopy.PracticePlaceholder}\"",
            $".accessibilityLabel(\"{DictationCopy.PracticeFieldName}\")",
            $"Text(\"{DictationCopy.PracticeNotSaved}\")",
            "Text(\"Click below. Hold \\(model.holdKey.name), say a sentence, then release. Your words should appear here.\")",
        })
        {
            Assert.Contains(words, swift, StringComparison.Ordinal);
        }
    }

    // ─── The window's markup and wiring ─────────────────────────────────
    //
    // Pins on the source, not evidence that the window behaves.

    private static string WindowCode() => File.ReadAllText(AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

    [Fact]
    public void The_practice_section_sits_between_the_header_and_the_shortcut_and_starts_hidden()
    {
        var xaml = XDocument.Load(AppFile("MainWindow.xaml"));
        var section = Named(xaml, "PracticeSection");
        var column = section.Parent!.Elements().ToList();

        Assert.Equal("Expander", section.Name.LocalName);
        Assert.Equal("Collapsed", (string?)section.Attribute("Visibility"));
        // The Mac's order: the status header with its buttons, the recent
        // result, "Learning from you" while shown, Try dictation, then the
        // shortcut's how-to line and pickers; Windows' Saved recording
        // (LIMIT-WIN-A) comes first of them, a dictation not yet in words.
        var header = column.IndexOf(Named(xaml, "HeaderSection"));
        Assert.Equal(0, header);
        Assert.Same(Named(xaml, "HeaderSection"), Named(xaml, "EnablePauseButton").Parent!.Parent!.Parent);
        Assert.Equal(
            ["SavedTakeSection", "RecentSection", "LearningReceiptSection"],
            column.Skip(header + 1).Take(column.IndexOf(section) - header - 1).Select(e => (string?)e.Attribute(X + "Name")));
        Assert.Equal(column.IndexOf(section) + 1, column.IndexOf(Named(xaml, "Instructions")));

        var box = Named(xaml, "PracticeBox");
        Assert.Equal("TextBox", box.Name.LocalName);
        Assert.Equal("True", (string?)box.Attribute("AcceptsReturn"));
        Assert.Equal("Wrap", (string?)box.Attribute("TextWrapping"));
        // The Mac field's three to five lines.
        Assert.Equal("3", (string?)box.Attribute("MinLines"));
        Assert.Equal("5", (string?)box.Attribute("MaxLines"));
        Assert.Equal("PracticeTextChanged", (string?)box.Attribute("TextChanged"));
        var hint = Named(xaml, "PracticeHint");
        Assert.Same(box.Parent, hint.Parent);
        Assert.Equal("False", (string?)hint.Attribute("IsHitTestVisible"));
    }

    /// <summary>A hold into the practice field must hear nothing from the
    /// section it types into.</summary>
    [Fact]
    public void No_line_of_the_practice_section_is_a_live_region()
    {
        var section = Named(XDocument.Load(AppFile("MainWindow.xaml")), "PracticeSection");

        Assert.DoesNotContain(
            section.DescendantsAndSelf().SelectMany(e => e.Attributes()),
            a => a.Name.LocalName == "AutomationProperties.LiveSetting");
    }

    [Fact]
    public void The_practice_text_is_never_read_beyond_its_length()
    {
        var code = WindowCode();
        var reads = code.Split("PracticeBox.Text").Length - 1;

        Assert.Equal(1, reads);
        Assert.Contains("PracticeBox.Text.Length == 0", Member(code, "private void PracticeTextChanged("), StringComparison.Ordinal);
        foreach (var other in Directory.EnumerateFiles(Path.GetDirectoryName(AppFile("MainWindow.xaml"))!, "*.cs")
                     .Where(f => !f.EndsWith("MainWindow.xaml.cs", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("PracticeBox", File.ReadAllText(other), StringComparison.Ordinal);
        }
    }

    private const string PracticeShown =
        "PracticeSection.Visibility = state.PracticeVisible ? Visibility.Visible : Visibility.Collapsed;";

    /// <summary>The field's focus is the keyboard, or the window's own focus
    /// while it is inactive, which activation gives the keyboard back to.</summary>
    [Fact]
    public void The_window_opens_and_closes_practice_only_as_the_disclosure_says_asking_with_the_fields_focus()
    {
        var code = WindowCode();
        Assert.Contains("RenderPractice(state);", Member(code, "public void Render(DictationViewState state)"), StringComparison.Ordinal);

        var render = Member(code, "private void RenderPractice(");
        var keyboard = render.IndexOf("var keyboard = PracticeBox.IsKeyboardFocusWithin;", StringComparison.Ordinal);
        var focused = render.IndexOf(
            "var focused = keyboard || ReferenceEquals(FocusManager.GetFocusedElement(this), PracticeBox);",
            StringComparison.Ordinal);
        var asked = render.IndexOf(
            "practice.Next(state.PracticeVisible, state.PracticeStartsExpanded, focused)",
            StringComparison.Ordinal);
        var shown = render.IndexOf(PracticeShown, StringComparison.Ordinal);
        Assert.True(keyboard >= 0 && focused > keyboard && asked > focused && shown > asked,
            "the focus is read before the section can hide");
        Assert.Contains("PracticeSection.IsExpanded = expanded;", render, StringComparison.Ordinal);
        Assert.Contains("DictationCopy.PracticeInstructions(state.HoldKey)", render, StringComparison.Ordinal);
        // The window's only writer of the expansion.
        Assert.Equal(1, code.Split("PracticeSection.IsExpanded").Length - 1);
    }

    /// <summary>
    /// The section going (Pause, a blocked microphone) takes the practice text
    /// with it, as the Mac's section discards its own, and does not leave the
    /// keyboard on a hidden field: it moves to the window's resting button,
    /// as it does when the microphone actions go.
    /// </summary>
    [Fact]
    public void Hiding_practice_moves_the_keyboard_to_a_button_and_drops_the_practice_text()
    {
        var code = WindowCode();
        var render = Member(code, "private void RenderPractice(");
        var hiding = render.IndexOf(
            "if (!state.PracticeVisible && PracticeSection.Visibility == Visibility.Visible)", StringComparison.Ordinal);
        var rescue = render.IndexOf("FocusRestingButton();", StringComparison.Ordinal);
        var clear = render.IndexOf("PracticeBox.Clear();", StringComparison.Ordinal);
        var shown = render.IndexOf(PracticeShown, StringComparison.Ordinal);

        Assert.True(hiding >= 0 && rescue > hiding && clear > rescue && shown > clear,
            "the keyboard moves and the text goes on the hide edge, before the section hides");
        Assert.Contains("if (keyboard)", render[hiding..rescue], StringComparison.Ordinal);
        // The button's state is this render's: Pause and Enable are set first.
        var whole = Member(code, "public void Render(DictationViewState state)");
        var buttons = whole.IndexOf("RenderHeader(state.Header);", StringComparison.Ordinal);
        Assert.True(buttons >= 0 && buttons < whole.IndexOf("RenderPractice(state);", StringComparison.Ordinal));
        Assert.Equal(1, code.Split("PracticeBox.Clear()").Length - 1);
    }

    [Fact]
    public void The_practice_labels_come_from_the_copy()
    {
        var code = WindowCode();
        var labels = Member(code, "private void ShowPracticeLabels()");

        Assert.Contains("ShowPracticeLabels();", Member(code, "public MainWindow("), StringComparison.Ordinal);
        Assert.Contains("PracticeSection.Header = WithAccessKey(DictationCopy.PracticeHeading, \"T\");", labels, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(PracticeSection, DictationCopy.PracticeHeading);", labels, StringComparison.Ordinal);
        Assert.Contains("PracticeHint.Text = DictationCopy.PracticePlaceholder;", labels, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(PracticeBox, DictationCopy.PracticeFieldName);", labels, StringComparison.Ordinal);
        Assert.Contains("PracticeNotSaved.Text = DictationCopy.PracticeNotSaved;", labels, StringComparison.Ordinal);
    }

    /// <summary>
    /// Dictating into the practice field keeps this window active, so the
    /// status line's "Listening…" would otherwise be announced into the open
    /// microphone. The flag is taken from the state before any live text in
    /// the render changes.
    /// </summary>
    [Fact]
    public void Nothing_in_the_window_is_announced_while_the_microphone_is_open()
    {
        var code = WindowCode();
        var render = Member(code, "public void Render(DictationViewState state)");
        var quiet = render.IndexOf("microphoneOpen = state.MicrophoneOpen;", StringComparison.Ordinal);

        Assert.True(quiet >= 0 && quiet < render.IndexOf("SetLiveText(", StringComparison.Ordinal), "quiet before the first live text");
        Assert.Contains("!microphoneOpen", Member(code, "private void SetLiveText("), StringComparison.Ordinal);
        Assert.Equal(1, code.Split("microphoneOpen = ").Length - 1);
    }
}
