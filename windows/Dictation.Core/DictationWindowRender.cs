using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core;

/// <summary>
/// The states of the Dictation window a reviewer needs to see: the Mac's
/// <c>DictationRenderScenario</c> list (<c>DictationWindowRender.swift</c>),
/// adapted to Windows.
/// </summary>
/// <remarks>
/// <para>
/// Windows asks for one permission, the microphone, so the Mac's
/// accessibility, input-monitoring and permission-recovery scenarios become
/// <see cref="MicrophoneBlocked"/>, and its restart (an event tap that did not
/// start) becomes <see cref="ShortcutRefused"/>.
/// </para>
/// <para>
/// Windows adds a scenario for every state of the window's header the Mac's
/// list does not reach (<see cref="DictationGlyph"/>: starting, transcribing,
/// typing, and the hook watch's three), for what FIELDCHECK, MIC and WINDOW
/// added to the window (a password field at the paste, the microphone changed
/// mid-hold, the sign-in notes), and for each disclosure the user opens
/// (<see cref="DictationWindowDisclosure"/>). Tests fail when a header state
/// or a disclosure has no scenario.
/// </para>
/// </remarks>
public enum DictationWindowRenderScenario
{
    /// <summary>First launch: dictation off, nothing learned.</summary>
    FirstRun,

    /// <summary>Enable, with Windows blocking the microphone: the blocked
    /// sentence, the privacy-page link and Check again.</summary>
    MicrophoneBlocked,

    /// <summary>Enable, with the hold key's hook refused.</summary>
    ShortcutRefused,

    /// <summary>Enable while the speech model loads: "Starting dictation…".</summary>
    Starting,

    /// <summary>Dictation on and idle, before any dictation: Try dictation open.</summary>
    Ready,

    /// <summary>On and idle after an earlier first dictation: Try dictation closed.</summary>
    ReturningUser,

    /// <summary>Enabled, then paused by the user.</summary>
    Paused,

    /// <summary>A hold is listening: the status line and Cancel.</summary>
    Recording,

    /// <summary>A released hold's words are being transcribed.</summary>
    Transcribing,

    /// <summary>The words are being typed into the field.</summary>
    Typing,

    /// <summary>Windows removed the hold key's hook and would not put it
    /// back: "Can't hear the hold key".</summary>
    HoldKeyUnheard,

    /// <summary>Windows removed the hook and the watch put it back, waiting
    /// for the key again: "Reconnected — hold the key again".</summary>
    HoldKeyReconnected,

    /// <summary>A window running as administrator is in front: "Can't hear
    /// keys in administrator windows".</summary>
    AdminWindow,

    /// <summary>The microphone in use was unplugged 42 seconds into a hold,
    /// and Windows names the next one with the longest name a sentence
    /// carries: the take ends there and its words go in ("The microphone
    /// stopped at 0:42 … The next hold uses …").</summary>
    MicrophoneChanged,

    /// <summary>The field became a password field while the words were
    /// transcribed, so the check at the paste refused them.</summary>
    PasswordFieldAtPaste,

    /// <summary>The vocabulary holds three taught words.</summary>
    Vocabulary,

    /// <summary>Forget all the disk refused: the unsaved line and Save again.</summary>
    VocabularyUnsaved,

    /// <summary>Then Save again with the disk back: the saved receipt.</summary>
    VocabularySaved,

    /// <summary>A dictation landed: the Recent result section.</summary>
    RecentResult,

    /// <summary>The clipboard refused the words, so neither the paste nor
    /// its fallback could use it: "Your words are still here".</summary>
    Recovery,

    /// <summary>Copy last text after a dictation: its "Copied" line.</summary>
    CopiedResult,

    /// <summary>A dictation the speech engine could not transcribe: the
    /// Saved recording section, with Recover last dictation (LIMIT-WIN-A).</summary>
    SavedRecording,

    /// <summary>A dictation landed and Show text is open on its words.</summary>
    RecentText,

    /// <summary>A correction learned a new word: the learning receipt.</summary>
    Learned,

    /// <summary>Then Undo learning.</summary>
    LearningUndone,

    /// <summary>A correction watch is running.</summary>
    LearningChecking,

    /// <summary>A correction that needs a second matching one.</summary>
    LearningPending,

    /// <summary>An edit that teaches nothing.</summary>
    LearningRejected,

    /// <summary>A correction that puts a new word in a known one's place
    /// ("the wrapper" fixed to "the repo"): the new word is learned.</summary>
    LearnedRepo,

    /// <summary>Enable with the speech resources missing.</summary>
    MissingResources,

    /// <summary>"Start Dictation when I sign in" checked, and Windows refused
    /// to write the entry: the note under the box.</summary>
    SignInRefused,

    /// <summary>The entry switched off in Settings → Apps → Startup, read
    /// when the window is activated: the note under the box.</summary>
    SignInSwitchedOff,

    /// <summary>Ready, with the Troubleshooting section open, as the Mac's
    /// troubleshooting scenario.</summary>
    Troubleshooting,
}

/// <summary>
/// A section of the window that opens only when the user opens it: no
/// controller state says whether it is open, so a scenario does
/// (<see cref="DictationWindowRenderScenarios.Opens"/>). Try dictation is not
/// one: the controller decides when it opens (<see cref="DictationViewState.PracticeStartsExpanded"/>).
/// </summary>
public enum DictationWindowDisclosure
{
    /// <summary>The Recent result's Show text, with the words in it.</summary>
    RecentText,

    /// <summary>Troubleshooting: this copy's version, build and path.</summary>
    Troubleshooting,
}

/// <summary>The scenario list and the names the PNGs carry.</summary>
public static class DictationWindowRenderScenarios
{
    /// <summary>Every scenario, in the order the images are written.</summary>
    public static IReadOnlyList<DictationWindowRenderScenario> All { get; } =
        Enum.GetValues<DictationWindowRenderScenario>();

    /// <summary><see cref="DictationWindowRenderScenario.FirstRun"/> is
    /// <c>first-run</c>, as on the Mac.</summary>
    public static string Name(DictationWindowRenderScenario scenario) => DictationRender.Kebab(scenario);

    /// <summary>The disclosures <paramref name="scenario"/> draws open; every
    /// other is drawn closed, as the window starts.</summary>
    public static IReadOnlySet<DictationWindowDisclosure> Opens(DictationWindowRenderScenario scenario) => scenario switch
    {
        DictationWindowRenderScenario.RecentText => new HashSet<DictationWindowDisclosure> { DictationWindowDisclosure.RecentText },
        DictationWindowRenderScenario.Troubleshooting => new HashSet<DictationWindowDisclosure> { DictationWindowDisclosure.Troubleshooting },
        _ => new HashSet<DictationWindowDisclosure>(),
    };
}

/// <summary>
/// The inert parts one scenario's window is built over, in a scratch folder
/// of its own that <see cref="Dispose"/> deletes: the
/// <see cref="DictationRenderPlatform"/>, a settings file, a learning store
/// judged against a few words, an installed copy's <c>resources\</c> made of
/// placeholders, and the controller's knobs. Nothing here reads or writes the
/// user's settings, vocabulary or log.
/// </summary>
public sealed class DictationRenderComposition : IDisposable
{
    /// <summary>The words the learning store knows, the Mac render's list:
    /// "brivio" and "Brivioo" are not among them, "wrapper" is.</summary>
    public static IReadOnlyList<string> Words { get; } =
        ["we", "use", "the", "hello", "world", "a", "for", "and", "wrapper"];

    /// <summary>The time a dictation is stamped with, so the Recent result
    /// section shows the same time in every run.</summary>
    public static DateTimeOffset Time { get; } = new(new DateTime(2026, 10, 9, 15, 41, 0, DateTimeKind.Local));

    /// <summary>How long the Typing scenario's paste settles before the
    /// clipboard is restored: long enough for the script to see "Typing…"
    /// and the picture to be taken, after which the render waits it out
    /// (<see cref="DictationWindowRenderScript.FinishAsync"/>). The settle is
    /// the only wait while the words are typed.</summary>
    public static readonly TimeSpan TypingSettle = TimeSpan.FromSeconds(2);

    /// <summary>The controller's clock in a render: still, until a scenario
    /// moves it (<see cref="AdvanceClock"/>), so nothing that runs on it — the
    /// hook watch, the microphone watch, the copy deadline, the model's idle
    /// unload — acts on its own between two steps. One hour in, as the
    /// app's clock is the time since Windows started.</summary>
    private TimeSpan _clock = TimeSpan.FromHours(1);

    private DictationRenderCorrections? _corrections;

    private DictationRenderComposition(DictationWindowRenderScenario scenario, string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Settings = new DictationSettingsStore(
            Path.Combine(root, "data", "settings.json"), NullLogger<DictationSettingsStore>.Instance);
        if (scenario == DictationWindowRenderScenario.ReturningUser)
        {
            Settings.Save(new DictationSettings(CompletedFirstDictation: true));
        }

        LearningDirectory = Path.Combine(root, "data", LearningStore.DirectoryName);
        Learning = new DictationLearning(new LearningStore(LearningDirectory), new WordListLexicon(Words));
        AppDirectory = Path.Combine(root, "app");
        Directory.CreateDirectory(AppDirectory);
        if (scenario != DictationWindowRenderScenario.MissingResources)
        {
            InstallPlaceholders(AppDirectory);
        }

        Options = new DictationControllerOptions
        {
            // A scripted press and release record what was spoken, however
            // short the hold (the product's policy has no minimum hold), and
            // the paste needs no settle: nothing reads this clipboard back.
            PasteSettleDelay = scenario == DictationWindowRenderScenario.Typing ? TypingSettle : TimeSpan.Zero,
            RunFieldCheck = check => Task.FromResult(check()),
            RunClipboardCopy = write => Task.FromResult(write()),
            LogDirectory = Path.Combine(root, "logs"),
            Now = () => Time,
            Clock = () => _clock,
            // A render never reads or writes the user's own recordings.
            Takes = new DictationTakeOptions { Directory = Path.Combine(root, "data", "recovery") },
        };
    }

    /// <summary>Move the controller's clock on by <paramref name="by"/>.</summary>
    public void AdvanceClock(TimeSpan by) => _clock += by;

    /// <summary>A composition in a new folder under the temporary directory.</summary>
    public static DictationRenderComposition For(DictationWindowRenderScenario scenario) =>
        new(scenario, Path.Combine(Path.GetTempPath(), "dictation-render-" + Guid.NewGuid().ToString("N")));

    /// <summary>The scratch folder everything below lives in.</summary>
    public string Root { get; }

    public DictationRenderPlatform Platform { get; } = new();

    public DictationSettingsStore Settings { get; }

    public DictationLearning Learning { get; }

    /// <summary>Where <c>learning.json</c> is written.</summary>
    public string LearningDirectory { get; }

    /// <summary>The app's directory, with a <c>resources\</c> of placeholder
    /// files (none for <see cref="DictationWindowRenderScenario.MissingResources"/>).</summary>
    public string AppDirectory { get; }

    public DictationControllerOptions Options { get; }

    /// <summary>No override is set.</summary>
    public Func<string, string?> Environment { get; } = _ => null;

    /// <summary>The correction events the Vocabulary section listens to,
    /// once <see cref="Relay"/> has made them.</summary>
    public DictationRenderCorrections Corrections =>
        _corrections ?? throw new InvalidOperationException("the window has not asked for its correction events yet");

    /// <summary>What the window hands the Vocabulary section in place of the
    /// controller's correction watch: the watch's own events and the ones a
    /// scenario raises.</summary>
    public ICorrectionWatchEvents Relay(ICorrectionWatchEvents? watch) =>
        _corrections = new DictationRenderCorrections(watch);

    public void Dispose()
    {
        Platform.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open under a correction watch winding down; the
            // temporary directory is the system's to clear.
        }
    }

    /// <summary>The files <see cref="DictationWorkerPlan"/> looks for, each one
    /// byte: enough to enable, never run.</summary>
    private static void InstallPlaceholders(string appDirectory)
    {
        foreach (var file in new[]
        {
            "resources/python/python.exe",
            "resources/runtime/transcription_worker/asr_server.py",
            "resources/model/model.bin",
            "resources/model/config.json",
            "resources/model/tokenizer.json",
            "resources/model/preprocessor_config.json",
        })
        {
            var path = Path.Combine(appDirectory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }
}

/// <summary>
/// The Vocabulary section's correction events in a render: the controller's
/// real watch, relayed, and the ones a scenario raises itself, as the Mac's
/// render calls <c>beginObservationFeedback</c> and <c>observe</c> directly.
/// </summary>
public sealed class DictationRenderCorrections(ICorrectionWatchEvents? watch) : ICorrectionWatchEvents
{
    private EventHandler<CorrectionWatchStarted>? _started;
    private EventHandler<CorrectionWatchObserved>? _observed;
    private EventHandler<CorrectionWatchEnded>? _ended;

    public event EventHandler<CorrectionWatchStarted>? Started
    {
        add
        {
            _started += value;
            if (watch is not null)
            {
                watch.Started += value;
            }
        }

        remove
        {
            _started -= value;
            if (watch is not null)
            {
                watch.Started -= value;
            }
        }
    }

    public event EventHandler<CorrectionWatchObserved>? Observed
    {
        add
        {
            _observed += value;
            if (watch is not null)
            {
                watch.Observed += value;
            }
        }

        remove
        {
            _observed -= value;
            if (watch is not null)
            {
                watch.Observed -= value;
            }
        }
    }

    public event EventHandler<CorrectionWatchEnded>? Ended
    {
        add
        {
            _ended += value;
            if (watch is not null)
            {
                watch.Ended += value;
            }
        }

        remove
        {
            _ended -= value;
            if (watch is not null)
            {
                watch.Ended -= value;
            }
        }
    }

    public void End(CorrectionWatchEnd why) => watch?.End(why);

    public void Raise(CorrectionWatchStarted started) => _started?.Invoke(this, started);

    public void Raise(CorrectionWatchObserved observed) => _observed?.Invoke(this, observed);

    public void Raise(CorrectionWatchEnded ended) => _ended?.Invoke(this, ended);
}

/// <summary>What a scenario drives: the controller and the Vocabulary section
/// the window built over <paramref name="Parts"/>, and how the host lets the
/// UI thread run between two steps (the WPF dispatcher in the app, a queue
/// in tests).</summary>
public sealed record DictationRenderStage(
    DictationController Controller, VocabularyModel Vocabulary, DictationRenderComposition Parts, Func<Task> Pump)
{
    /// <summary>The longest one step may take to show:
    /// <see cref="DictationWindowRenderScript.Deadline"/> unless a test asks
    /// for less.</summary>
    public TimeSpan Deadline { get; init; } = DictationWindowRenderScript.Deadline;
}

/// <summary>
/// Moves the window into each scenario through the controller's and the
/// Vocabulary section's public members only, as a user would (the Mac's
/// <c>DictationRenderRig.drive</c>), and waits until the window shows it. A
/// step that does not get there within the stage's
/// <see cref="DictationRenderStage.Deadline"/> throws a
/// <see cref="TimeoutException"/> naming what never showed, so a render never
/// writes a picture of the wrong state under the right name.
/// </summary>
public static class DictationWindowRenderScript
{
    /// <summary>The longest one step may take to show.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    public static async Task DriveAsync(DictationWindowRenderScenario scenario, DictationRenderStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var controller = stage.Controller;
        var vocabulary = stage.Vocabulary;
        var platform = stage.Parts.Platform;
        switch (scenario)
        {
            case DictationWindowRenderScenario.FirstRun:
                break;

            case DictationWindowRenderScenario.MicrophoneBlocked:
                platform.Access = MicrophoneAccess.Denied;
                await EnableAsync(stage);
                break;

            case DictationWindowRenderScenario.ShortcutRefused:
                platform.HookRegisters = false;
                await EnableAsync(stage);
                break;

            case DictationWindowRenderScenario.Ready
                or DictationWindowRenderScenario.ReturningUser
                or DictationWindowRenderScenario.MissingResources
                or DictationWindowRenderScenario.Troubleshooting:
                await EnableAsync(stage);
                break;

            case DictationWindowRenderScenario.Starting:
                // The load is held until FinishAsync: the Enable is still out.
                platform.HoldStart = true;
                _ = controller.EnableAsync();
                await UntilGlyphAsync(stage, DictationGlyph.Starting);
                break;

            case DictationWindowRenderScenario.Transcribing:
                platform.HoldTranscription = true;
                await EnableAsync(stage);
                await PressAsync(stage);
                await ReleaseAsync(stage);
                await UntilGlyphAsync(stage, DictationGlyph.Transcribing);
                break;

            case DictationWindowRenderScenario.Typing:
                // The paste settles for TypingSettle: that is the typing.
                await EnableAsync(stage);
                await PressAsync(stage);
                await ReleaseAsync(stage);
                await UntilGlyphAsync(stage, DictationGlyph.Typing);
                break;

            case DictationWindowRenderScenario.HoldKeyUnheard:
                // Windows removed the hook and will not let it back: the
                // user's next press reaches Windows and not the hook.
                await EnableAsync(stage);
                platform.Trigger!.HookAlive = false;
                platform.Trigger.ReinstallSucceeds = false;
                platform.Trigger.UserHoldKey(true);
                await PollUntilAsync(stage, DictationGlyph.HoldKeyUnheard);
                platform.Trigger.UserHoldKey(false);
                await PollAsync(stage, TimeSpan.FromSeconds(1.5));
                await UntilGlyphAsync(stage, DictationGlyph.HoldKeyUnheard);
                break;

            case DictationWindowRenderScenario.HoldKeyReconnected:
                // Removed, and put back at the press the dead hook missed; the
                // new hook has heard nothing yet (the key is still down).
                await EnableAsync(stage);
                platform.Trigger!.HookAlive = false;
                platform.Trigger.UserHoldKey(true);
                await PollUntilAsync(stage, DictationGlyph.HoldKeyReconnected);
                break;

            case DictationWindowRenderScenario.AdminWindow:
                await EnableAsync(stage);
                platform.AdministratorWindowInFront = true;
                await PollUntilAsync(stage, DictationGlyph.AdminWindowInFront);
                break;

            case DictationWindowRenderScenario.MicrophoneChanged:
                await EnableAsync(stage);
                await PressAsync(stage);
                // 42 s of speech in all: the press spoke the first half second.
                for (var spoken = 1; spoken < 84; spoken++)
                {
                    platform.Microphone!.Speak();
                }

                platform.Microphone!.Unplug(DictationHudRenderFrames.LongMicrophoneName);
                await UntilAsync(
                    stage,
                    () => controller.Shown?.Status == MicrophoneStoppedLine,
                    "the microphone-stopped line");
                // The key still held starts nothing until it is pressed again.
                platform.Trigger!.UserHoldKey(false);
                break;

            case DictationWindowRenderScenario.PasswordFieldAtPaste:
                platform.PasswordFieldDuringTranscription = true;
                await EnableAsync(stage);
                await PressAsync(stage);
                await ReleaseAsync(stage);
                await UntilAsync(
                    stage, () => controller.Shown?.Status == DictationCopy.SecureFieldAtPaste, "the refusal at the paste");
                break;

            case DictationWindowRenderScenario.SignInRefused:
                platform.SignIn.Refuses = true;
                controller.SetLaunchAtLogin(true);
                await UntilAsync(
                    stage, () => controller.Shown?.LaunchAtLoginNote == DictationCopy.LaunchAtLoginNotTurnedOn, "the refused sign-in note");
                break;

            case DictationWindowRenderScenario.SignInSwitchedOff:
                // Switched off in Settings while Dictation ran, read again
                // when the window is activated, as MainWindow's Activated does.
                platform.SignIn.SwitchedOffInSettings = true;
                controller.RefreshLaunchAtLogin();
                await UntilAsync(
                    stage, () => controller.Shown?.LaunchAtLoginNote == DictationCopy.LaunchAtLoginSwitchedOff, "the Settings sign-in note");
                break;

            case DictationWindowRenderScenario.Paused:
                await EnableAsync(stage);
                controller.TryPause();
                await UntilAsync(stage, () => !controller.IsEnabled, "paused");
                break;

            case DictationWindowRenderScenario.Recording:
                await EnableAsync(stage);
                await PressAsync(stage);
                break;

            case DictationWindowRenderScenario.Vocabulary:
                await EnableAsync(stage);
                await TeachAsync(stage, "Brivioo", "bree vee oh");
                await TeachAsync(stage, "Kubernetes", string.Empty);
                await TeachAsync(stage, "Saoirse", "seer sha");
                break;

            case DictationWindowRenderScenario.VocabularyUnsaved or DictationWindowRenderScenario.VocabularySaved:
                await EnableAsync(stage);
                await TeachAsync(stage, "Brivioo", "bree vee oh");
                // A folder where learning.json goes: every write fails until it goes.
                var file = Path.Combine(stage.Parts.LearningDirectory, LearningStore.FileName);
                File.Delete(file);
                Directory.CreateDirectory(file);
                vocabulary.AskForgetAll();
                vocabulary.ConfirmForgetAll();
                await UntilAsync(stage, () => vocabulary.Shown?.NotSavedText is not null, "the unsaved line");
                if (scenario == DictationWindowRenderScenario.VocabularySaved)
                {
                    Directory.Delete(file);
                    vocabulary.SaveAgain();
                    await UntilAsync(
                        stage,
                        () => vocabulary.Shown is { NotSavedText: null, ManagementNotice: not null },
                        "the saved receipt");
                }

                break;

            case DictationWindowRenderScenario.RecentResult
                or DictationWindowRenderScenario.Recovery
                or DictationWindowRenderScenario.CopiedResult
                or DictationWindowRenderScenario.RecentText:
                if (scenario == DictationWindowRenderScenario.Recovery)
                {
                    platform.ClipboardAccepts = false;
                }

                await EnableAsync(stage);
                await DictateAsync(stage);
                if (scenario == DictationWindowRenderScenario.CopiedResult)
                {
                    controller.CopyLastText();
                    await UntilAsync(stage, () => controller.Shown?.Recent?.CopyMessage is not null, "the copy line");
                }

                break;

            case DictationWindowRenderScenario.SavedRecording:
                platform.TranscriptionRefused = true;
                await EnableAsync(stage);
                await PressAsync(stage);
                await ReleaseAsync(stage);
                await UntilAsync(stage, () => controller.Shown?.SavedTake is not null, "the saved recording");
                break;

            case DictationWindowRenderScenario.Learned or DictationWindowRenderScenario.LearningUndone:
                await EnableAsync(stage);
                await ObserveAsync(stage, "We use brivio", "We use Brivioo");
                await UntilAsync(stage, () => vocabulary.Shown?.LearningReceipt is not null, "the learning receipt");
                if (scenario == DictationWindowRenderScenario.LearningUndone)
                {
                    vocabulary.UndoLastLearning();
                    await UntilAsync(
                        stage, () => vocabulary.Shown?.LearningReceipt == DictationCopy.LearningUndone, "the undone line");
                }

                break;

            case DictationWindowRenderScenario.LearningChecking:
                await EnableAsync(stage);
                await BeginWatchAsync(stage);
                break;

            case DictationWindowRenderScenario.LearningPending:
                await EnableAsync(stage);
                await ObserveAsync(stage, "we use sunshine", "we use Sunshine42x!");
                break;

            case DictationWindowRenderScenario.LearningRejected:
                await EnableAsync(stage);
                await ObserveAsync(stage, "we use the old version", "we prefer a different approach");
                break;

            case DictationWindowRenderScenario.LearnedRepo:
                await EnableAsync(stage);
                await ObserveAsync(stage, "we use the wrapper", "we use the repo");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "no script for this scenario");
        }

        // Whatever is still posted lands before the picture is taken.
        await SettleAsync(stage);
    }

    /// <summary>
    /// After the pictures, before the window lets go: let a dictation or an
    /// Enable the scenario left under way finish, so nothing it started
    /// lands on a controller that is gone. The held worker answers are
    /// released, and the Typing scenario's paste settles.
    /// </summary>
    public static async Task FinishAsync(DictationWindowRenderScenario scenario, DictationRenderStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        var controller = stage.Controller;
        stage.Parts.Platform.ReleaseHolds();
        switch (scenario)
        {
            case DictationWindowRenderScenario.Starting:
                await UntilAsync(stage, () => controller.IsEnabled, "dictation enabled after the load");
                break;

            case DictationWindowRenderScenario.Transcribing or DictationWindowRenderScenario.Typing:
                await UntilAsync(stage, () => controller.Shown?.Recent is not null, "the words typed");
                break;
        }

        await SettleAsync(stage);
    }

    private static async Task EnableAsync(DictationRenderStage stage)
    {
        await WhilePumpingAsync(stage, stage.Controller.EnableAsync(), "dictation enabled");
        await SettleAsync(stage);
    }

    private static async Task TeachAsync(DictationRenderStage stage, string written, string soundsLike)
    {
        if (!await WhilePumpingAsync(stage, stage.Vocabulary.Teach(written, soundsLike), "a word added"))
        {
            throw new InvalidOperationException("the render's vocabulary refused a word");
        }
    }

    /// <summary>
    /// Wait for <paramref name="task"/> while the UI thread keeps running: an
    /// answer that comes back through the window's post (the Vocabulary
    /// section's, every time) needs the host to pump, and awaiting the task
    /// alone would wait for itself where nothing else pumps.
    /// </summary>
    private static async Task WhilePumpingAsync(DictationRenderStage stage, Task task, string what)
    {
        await UntilAsync(stage, () => task.IsCompleted, what);
        await task;
    }

    private static async Task<T> WhilePumpingAsync<T>(DictationRenderStage stage, Task<T> task, string what)
    {
        await UntilAsync(stage, () => task.IsCompleted, what);
        return await task;
    }

    /// <summary>What the microphone-changed scenario ends on: 42 seconds in,
    /// naming the long microphone as the next hold's.</summary>
    public static string MicrophoneStoppedLine => DictationCopy.TakeStoppedInserted(
        DictationTakeStop.MicrophoneRemoved, TimeSpan.FromSeconds(42), DictationHudRenderFrames.LongMicrophoneName);

    /// <summary>The hold key goes down and the next tick sees it.</summary>
    private static async Task PressAsync(DictationRenderStage stage)
    {
        var trigger = stage.Parts.Platform.Trigger
            ?? throw new InvalidOperationException("dictation armed no hold key");
        trigger.UserHoldKey(true);
        stage.Controller.Tick();
        await UntilAsync(stage, () => stage.Controller.Shown?.MicrophoneOpen == true, "a listening hold");
        stage.Parts.Platform.Microphone?.Speak();
    }

    /// <summary>The hold key comes up and the next tick sees it.</summary>
    private static async Task ReleaseAsync(DictationRenderStage stage)
    {
        stage.Parts.Platform.Trigger!.UserHoldKey(false);
        stage.Controller.Tick();
        await UntilAsync(stage, () => stage.Controller.Shown?.MicrophoneOpen == false, "the hold released");
    }

    /// <summary>A press, half a second of speech, a release, and everything
    /// after it until the Recent result section holds the words.</summary>
    private static async Task DictateAsync(DictationRenderStage stage)
    {
        await PressAsync(stage);
        await ReleaseAsync(stage);
        await UntilAsync(stage, () => stage.Controller.Shown?.Recent is not null, "a recent result");
    }

    /// <summary>Until the window's header shows <paramref name="glyph"/>.</summary>
    private static Task UntilGlyphAsync(DictationRenderStage stage, DictationGlyph glyph) =>
        UntilAsync(stage, () => stage.Controller.Shown?.Header.Glyph == glyph, $"the header's {glyph}");

    /// <summary>The window's 30 ms poll for <paramref name="time"/> on the
    /// render's clock, which moves only here: the hook watch looks at the
    /// hold key every half second and waits 300 ms for its test key.</summary>
    private static async Task PollAsync(DictationRenderStage stage, TimeSpan time)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < time; elapsed += DictationController.BusyPoll)
        {
            stage.Parts.AdvanceClock(DictationController.BusyPoll);
            stage.Controller.Tick();
            await stage.Pump();
        }
    }

    /// <summary>The poll, until the header shows <paramref name="glyph"/>.</summary>
    private static async Task PollUntilAsync(DictationRenderStage stage, DictationGlyph glyph)
    {
        var deadline = DateTime.UtcNow + stage.Deadline;
        while (stage.Controller.Shown?.Header.Glyph != glyph)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"the window never showed the header's {glyph}");
            }

            await PollAsync(stage, DictationController.BusyPoll);
        }
    }

    /// <summary>A correction watch begins: "Checking this field…".</summary>
    private static async Task<Guid> BeginWatchAsync(DictationRenderStage stage)
    {
        var watch = Guid.NewGuid();
        stage.Parts.Corrections.Raise(new CorrectionWatchStarted(watch));
        await UntilAsync(
            stage, () => stage.Vocabulary.Shown?.CorrectionNotice == CorrectionNotices.Checking, "the checking line");
        return watch;
    }

    /// <summary>A watch begins and settles on <paramref name="edited"/>, which
    /// the real learning judges: what the section shows is its answer.</summary>
    private static async Task ObserveAsync(DictationRenderStage stage, string dictated, string edited)
    {
        var watch = await BeginWatchAsync(stage);
        var outcome = stage.Parts.Learning.Observe(
            watch.ToString("N"), dictated, edited, DictationRenderComposition.Time);
        stage.Parts.Corrections.Raise(new CorrectionWatchObserved(
            watch, 1, !string.Equals(dictated, edited, StringComparison.Ordinal), outcome));
        await UntilAsync(
            stage, () => stage.Vocabulary.Shown?.CorrectionNotice != CorrectionNotices.Checking, "the watch's outcome");
    }

    private static async Task UntilAsync(DictationRenderStage stage, Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + stage.Deadline;
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"the window never showed {what}");
            }

            stage.Controller.Tick();
            await stage.Pump();
        }
    }

    /// <summary>A few turns of the UI thread with nothing awaited.</summary>
    private static async Task SettleAsync(DictationRenderStage stage)
    {
        for (var i = 0; i < 5; i++)
        {
            stage.Controller.Tick();
            await stage.Pump();
        }
    }
}
