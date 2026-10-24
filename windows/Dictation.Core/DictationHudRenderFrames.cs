using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>One indicator state the render mode draws.</summary>
/// <param name="Name">Its name in the PNG's file name.</param>
/// <param name="Model">The pill, exactly as <see cref="DictationHudFeed"/>
/// hands it to the window: its text, tone, the busy lines it is sized for, its
/// quiet name and the live preview's caption.</param>
/// <param name="Level">The level the bars draw after the show, or null where
/// the tone dot stands (every pill but a listening one).</param>
public sealed record DictationHudRenderFrame(string Name, DictationHudModel Model, float? Level)
{
    /// <summary>
    /// A busy pill, by HINTS' rule: it lasts as long as its state does (no
    /// dwell) and is sized for the hold's busy lines
    /// (<see cref="DictationCopy.HudBusyLines"/>), so every one of them is
    /// the one busy width (<see cref="DictationHudGeometry.PillWidth"/>), and
    /// keeps its second line's room, so the one height too. Its second line
    /// never wraps (<see cref="DictationHudGeometry.DetailWraps"/>).
    /// </summary>
    public bool IsBusy => Model.Dwell is null && Model.SizedFor is not null;
}

/// <summary>
/// Every pill the Windows indicator can show, for the render mode
/// (<c>DICTATION_HUD_RENDER_DIR</c>): the Mac's list in
/// <c>DictationHUDRenderMode</c> adapted to Windows' states and endings. Each
/// is made by driving a real <see cref="DictationHudFeed"/> into it, so a
/// frame is the model the window would be handed in the app, never a copy of
/// one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is in it.</b> The listening pill held and hands-free, with the
/// hands-free clock, the countdown both ways, a level and the live preview's
/// caption; Transcribing and Windows' Typing; every ending the session can
/// produce for Dictation (<see cref="Results"/>, the password field found at
/// the paste included), plainly and again with each context the controller
/// adds that changes its line (<see cref="Contexts"/>, the microphone change
/// with the longest name a sentence carries included), so every ending whose
/// second line wraps to two lines is drawn; every press refusal and the other
/// notices (<see cref="Notices"/>). Then the Mac's stability sequence: one
/// listening pill with a growing caption and a moving level, which must not
/// change size.
/// </para>
/// <para>
/// <b>One list for the render and the tests.</b> <see cref="Contexts"/>,
/// <see cref="Results"/> and <see cref="Notices"/> are also what the
/// indicator's tests hold every line to (its reading time, its title and
/// detail, its fit at the cap), so a line the tests know of is a line the
/// render draws. The endings and refusals come from their enums, and tests
/// fail when a context field, a feed member or a <c>Hud</c> line of
/// <see cref="DictationCopy"/> arrives undrawn.
/// </para>
/// <para>
/// <b>Not in it.</b> The Mac's accessibility-needed, secure-input and
/// command-done endings (Windows has no such permission, check or command),
/// and the shared model's workflow and voice-delete endings, which Dictation
/// never produces (<see cref="NeverProduced"/>).
/// </para>
/// </remarks>
public static class DictationHudRenderFrames
{
    /// <summary>The hold every frame is drawn for: the default key and the
    /// product's hold policy (a five-minute limit, a minute's countdown).</summary>
    public static DictationHudHold DefaultHold { get; } =
        new(DictationHotkeys.PushToTalk.KeyName, DictationHoldPolicy.Windows);

    /// <summary>The ordinary speech level the Mac's render draws: a 0.08 peak,
    /// about −22 dB, visibly raised bars.</summary>
    public const float SpeechLevel = 0.08f;

    /// <summary>The live preview's caption the Mac's render draws.</summary>
    public const string Caption = "this is a live preview of my voice";

    /// <summary>The hands-free clock's frame: 1:05 into the hold.</summary>
    public static readonly TimeSpan LatchedElapsed = TimeSpan.FromSeconds(65);

    /// <summary>The countdown's frame: 10 s before the limit.</summary>
    public static readonly TimeSpan CountdownElapsed = DictationHoldPolicy.Windows.Ceiling - TimeSpan.FromSeconds(10);

    /// <summary>The silence warning's frame: 23 s of a 30 s stop, 7 s left.</summary>
    public static readonly DictationSilenceWarning SilenceWarning = new(TimeSpan.FromSeconds(23), TimeSpan.FromSeconds(7));

    /// <summary>The shared model's endings Dictation never produces: the
    /// controller composes no workflow router and no voice delete.</summary>
    public static IReadOnlySet<DictationOutcome> NeverProduced { get; } = new HashSet<DictationOutcome>
    {
        DictationOutcome.RanWorkflow,
        DictationOutcome.WorkflowNotMatched,
        DictationOutcome.VoiceDeleted,
        DictationOutcome.VoiceDeleteNothingToDelete,
        DictationOutcome.VoiceDeleteRefused,
    };

    /// <summary>A microphone name longer than a sentence carries: Windows
    /// names some arrays at seventy characters, and a sentence cuts them at
    /// <see cref="DictationCopy.MicrophoneNameLimit"/>. The window render's
    /// microphone-changed scenario names it as the next hold's microphone.</summary>
    public const string LongMicrophoneName = "Microphone Array (Intel® Smart Sound Technology for Digital Microphones)";

    /// <summary>Where the faults in <see cref="Contexts"/> stop a take: the
    /// design's 4:12.</summary>
    public static readonly TimeSpan TakeStoppedAt = TimeSpan.FromSeconds(252);

    /// <summary>
    /// Every context the controller can give an ending
    /// (<see cref="DictationHudContext"/>), each with the name its frames
    /// carry after the result's: none, then one per field the controller
    /// sets, and a take a fault ended at 4:12 (the microphone, and the speech
    /// worker) and one kept for recovery, alone and after a shortcut.
    /// </summary>
    public static IReadOnlyList<(string Name, DictationHudContext Context)> Contexts { get; } =
    [
        (string.Empty, new()),
        ("kept-on-clipboard", new(KeptOnClipboard: true)),
        ("by-shortcut", new(CancelledByShortcut: true)),
        ("at-limit", new(Stopped: new(DictationHoldStop.Ceiling, DictationHoldPolicy.Windows.Ceiling))),
        ("after-silence", new(Stopped: new(DictationHoldStop.Silence, TimeSpan.FromSeconds(30)))),
        ("after-a-minute-of-silence", new(Stopped: new(DictationHoldStop.Silence, TimeSpan.FromSeconds(60)))),
        ("stuck-key", new(Stopped: new(DictationHoldStop.StuckKey, DictationHoldPolicy.Windows.StuckKeyStop))),
        ("nothing-heard", new(Stopped: new(DictationHoldStop.NothingHeard, DictationHoldPolicy.Windows.NothingHeardStop))),
        ("restarting", new(RecognizerRestarting: true)),
        ("words-kept", new(WordsKeptInApp: true)),
        ("microphone-stopped", new(TakeStop: DictationTakeStop.MicrophoneRemoved, TakeStoppedAt: TakeStoppedAt)),
        ("worker-lost", new(TakeStop: DictationTakeStop.WorkerLost, TakeStoppedAt: TakeStoppedAt)),
        ("take-saved", new(TakeSaved: true)),
        ("by-shortcut-take-saved", new(CancelledByShortcut: true, TakeSaved: true)),
    ];

    /// <summary>
    /// Every way a dictation can end, as the session hands it to the
    /// controller, each with its name: every outcome but
    /// <see cref="NeverProduced"/>, and the password field found at the paste
    /// (FIELDCHECK), a result of its own whose pill reads as the one found at
    /// the release.
    /// </summary>
    public static IReadOnlyList<(string Name, DictationSessionResult Result)> Results { get; } =
    [
        .. Enum.GetValues<DictationOutcome>()
            .Where(outcome => !NeverProduced.Contains(outcome))
            .Select(outcome => (DictationRender.Kebab(outcome), new DictationSessionResult(outcome, null, null))),
        ("secure-field-at-paste", new DictationSessionResult(DictationOutcome.SecureField, null, null, SecureFieldAtPaste: true)),
    ];

    /// <summary>
    /// The endings drawn for <paramref name="hold"/>: every result plainly,
    /// and again with each context that changes its line, named
    /// <c>&lt;result&gt;-&lt;context&gt;</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, DictationSessionResult Result, DictationHudContext Context)> Endings(DictationHudHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        var endings = new List<(string, DictationSessionResult, DictationHudContext)>();
        foreach (var (result, ended) in Results)
        {
            var plain = DictationHudFeed.For(ended, new DictationHudContext(), hold).Text;
            foreach (var (name, context) in Contexts)
            {
                if (name.Length == 0)
                {
                    endings.Add((result, ended, context));
                }
                else if (DictationHudFeed.For(ended, context, hold).Text != plain)
                {
                    endings.Add(($"{result}-{name}", ended, context));
                }
            }
        }

        return endings;
    }

    /// <summary>Every pill that ends no dictation, with its name: each press
    /// refusal (<see cref="DictationPressRefusal"/>), Copied and Couldn't
    /// copy, the remembered correction, the recogniser that could not
    /// restart, a recovery that failed, and the question Escape asks on a
    /// take of 3:42.</summary>
    public static IReadOnlyList<(string Name, Action<DictationHudFeed> Show)> Notices { get; } =
    [
        .. Enum.GetValues<DictationPressRefusal>().Select(why =>
            ("refused-" + DictationRender.Kebab(why), (Action<DictationHudFeed>)(feed => feed.PressRefused(why)))),
        ("copied", feed => feed.Copied(true)),
        ("copy-failed", feed => feed.Copied(false)),
        ("correction-remembered", feed => feed.CorrectionRemembered()),
        ("recognizer-could-not-restart", feed => feed.RecognizerCouldNotRestart()),
        ("recovery-failed", feed => feed.RecoveryFailed()),
        ("ask-to-discard", feed => feed.AskToDiscard(TimeSpan.FromSeconds(222), listening: false)),
    ];

    /// <summary>The Mac's stability sequence: one recording with a growing
    /// caption and a speech-like level (peaks of about 0.03 to 0.12). Every
    /// frame must be the same size: only the bars and the words change.</summary>
    public static IReadOnlyList<(float Level, string Caption)> Sequence { get; } =
    [
        (0f, ""),
        (0.03f, "this"),
        (0.06f, "this is a"),
        (0.09f, "this is a live"),
        (0.05f, "this is a live preview"),
        (0.12f, "this is a live preview of my voice"),
    ];

    /// <summary>Every frame, for <paramref name="hold"/>, in the order the
    /// images are written: the busy pills, the endings, the other pills, then
    /// the stability sequence.</summary>
    public static IReadOnlyList<DictationHudRenderFrame> All(DictationHudHold hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        var frames = new List<DictationHudRenderFrame>
        {
            Through("listening", hold, feed => feed.State(DictationState.Listening)),
            Through("listening-latched", hold, feed => Listening(feed, latched: true, TimeSpan.Zero)),
            Through("listening-latched-clock", hold, feed => Listening(feed, latched: true, LatchedElapsed)),
            Through("listening-countdown", hold, feed => Listening(feed, latched: false, CountdownElapsed)),
            Through("listening-latched-countdown", hold, feed => Listening(feed, latched: true, CountdownElapsed)),
            Through("listening-silence-warning", hold, feed =>
            {
                feed.State(DictationState.Listening);
                feed.Listening(latched: true, LatchedElapsed, SilenceWarning);
            }),
            Through("listening-level", hold, feed =>
            {
                feed.State(DictationState.Listening);
                feed.Level(SpeechLevel);
            }),
            Through("listening-caption", hold, feed =>
            {
                feed.State(DictationState.Listening);
                feed.Level(SpeechLevel);
                feed.Caption(Caption);
            }),
            Through("transcribing", hold, feed => feed.State(DictationState.Transcribing)),
            Through("typing", hold, feed => feed.State(DictationState.Inserting)),
        };

        frames.AddRange(Endings(hold).Select(ending => Through(
            "outcome-" + ending.Name, hold, feed => feed.Result(ending.Result, ending.Context))));
        frames.AddRange(Notices.Select(notice => Through(notice.Name, hold, notice.Show)));

        frames.AddRange(Sequence.Select((frame, index) => Through($"sequence-{index}", hold, feed =>
        {
            feed.State(DictationState.Listening);
            feed.Level(frame.Level);
            if (frame.Caption.Length > 0)
            {
                feed.Caption(frame.Caption);
            }
        })));
        return frames;
    }

    private static void Listening(DictationHudFeed feed, bool latched, TimeSpan elapsed)
    {
        feed.State(DictationState.Listening);
        feed.Listening(latched, elapsed);
    }

    /// <summary>A fresh feed driven by <paramref name="drive"/>, and the last
    /// pill and level it handed the window.</summary>
    /// <remarks>The feed paces its pills (<see cref="DictationHudPacer"/>):
    /// a different busy pill waits until the one on screen has stood
    /// <see cref="DictationHudPacer.VisibleDwell"/>. Every frame is a first
    /// pill from hidden or a change within the listening pill, neither of
    /// which waits; the clock is still moved past the pacing and the feed
    /// ticked after the steps, so a pill a step held back is drawn rather
    /// than lost, and no dwell (the shortest is 600 ms) has run out.</remarks>
    private static DictationHudRenderFrame Through(string name, DictationHudHold hold, Action<DictationHudFeed> drive)
    {
        var window = new Capture();
        var now = TimeSpan.Zero;
        using (var feed = new DictationHudFeed(window, () => now, () => hold))
        {
            drive(feed);
            now += DictationHudPacer.VisibleDwell;
            feed.Tick();
        }

        return new DictationHudRenderFrame(
            name,
            window.Shown ?? throw new InvalidOperationException($"the indicator frame '{name}' showed nothing"),
            window.Level);
    }

    /// <summary>The window, as a record of the last pill shown and the level
    /// drawn on it. A hide leaves both: the feed hides when it is disposed.</summary>
    private sealed class Capture : IDictationHud
    {
        public DictationHudModel? Shown { get; private set; }

        public float? Level { get; private set; }

        /// <summary>The feed's heartbeat reads it: a pill is up from its show
        /// to the next hide.</summary>
        public bool IsShowing { get; private set; }

        public void Show(DictationHudModel model, bool announce)
        {
            Shown = model;
            Level = null;
            IsShowing = true;
        }

        void IDictationHud.Level(float level) => Level = level;

        public void Hide() => IsShowing = false;
    }
}
