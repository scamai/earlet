using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core;

/// <summary>Why a press was refused before the microphone opened.</summary>
public enum DictationPressRefusal
{
    /// <summary>The last dictation was still being transcribed or typed.</summary>
    StillTyping,

    /// <summary>The focused field is a password field.</summary>
    PasswordField,

    /// <summary>Copy last text was still writing to the clipboard.</summary>
    StillCopying,
}

/// <summary>What the controller knows about a finished dictation that the
/// session's result does not say.</summary>
/// <param name="KeptOnClipboard">The paste was refused and the controller left
/// the words on the clipboard.</param>
/// <param name="CancelledByShortcut">The hold was cancelled by another key
/// pressed with the hold key.</param>
/// <param name="Stopped">The app ended the hold itself (<see cref="DictationHoldPolicy"/>):
/// the recording limit, the hands-free silence stop, nothing heard since the
/// latch, or the held stuck-key stop.</param>
/// <param name="RecognizerRestarting">The recogniser stopped under this
/// dictation and the controller is starting it again.</param>
/// <param name="WordsKeptInApp">The paste was refused, the clipboard would not
/// take the words either, and they are held for Copy last text.</param>
/// <param name="TakeStop">A fault ended the take before the user did
/// (DictationController.Takes.cs): what was captured up to it was
/// transcribed.</param>
/// <param name="TakeStoppedAt">With <paramref name="TakeStop"/>: how much
/// audio the take held when it stopped.</param>
/// <param name="TakeSaved">The take could not be transcribed, or a shortcut
/// ended a long one, and it is kept for Recover last dictation.</param>
public sealed record DictationHudContext(
    bool KeptOnClipboard = false,
    bool CancelledByShortcut = false,
    DictationHoldEnding? Stopped = null,
    bool RecognizerRestarting = false,
    bool WordsKeptInApp = false,
    DictationTakeStop TakeStop = DictationTakeStop.None,
    TimeSpan TakeStoppedAt = default,
    bool TakeSaved = false);

/// <summary>
/// What the floating indicator says during a hold and after it, and when it
/// goes away: the controller's state changes and results, mapped through the
/// shared <see cref="DictationHudModel"/>, with the few lines Dictation knows
/// better than that model put in its place.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The window is normally hidden in the notification
/// area, and every outcome sentence went to its status line. Holding the key
/// changed a 16-pixel tray icon that Windows 11 tucks into the overflow, so a
/// refused paste with the words on the clipboard, a password field, nothing
/// heard and a recogniser restart all looked like success. The shared model
/// was written for exactly that failure (its header records it); this is the
/// half that was missing: something to feed it.
/// </para>
/// <para>
/// <b>Plain <c>net9.0</c>, on the controller's clock.</b> Every rule here is a
/// product rule, so it is asserted on a machine with no WPF. The dwell runs on
/// <see cref="DictationControllerOptions.Clock"/> and comes due in the
/// controller's 30 ms tick, through the shared <see cref="DictationHudDwell"/>,
/// so a test moves the clock instead of sleeping. The window only draws.
/// </para>
/// <para>
/// <b>Never the words, but for the live preview's caption.</b> The pill is
/// visible to whoever is behind the user and to anything sharing the screen.
/// Nothing here reads a result's text; the one field the shared model would
/// show, the stock decode of a comparison run, is removed before it is asked.
/// The one exception is opt-in (<c>DICTATION_PREVIEW=1</c>): while a hold
/// listens, the last few words the preview heard stand in the title
/// (<see cref="Caption"/>), never announced and never in a name, and gone
/// with the listening pill. No ending ever shows them, and a hold whose
/// preview showed words ends exactly as one whose preview did not: the
/// shared model's <c>previewShowedWords</c> quieting is not used.
/// </para>
/// <para>
/// <b>Never the other app's process name.</b> The shared model appends the
/// target's process name ("Typed into WINWORD", "The paste did not land into
/// msedge"). That is an executable's file name, not a name the user knows the
/// app by, and for a UWP app it is the frame that hosts it
/// (ApplicationFrameHost). The window title is the only other name to hand,
/// and it names the document. So the target is removed before the model is
/// asked, and the receipt is the plain "Typed": the words are in front of the
/// user, in the window they were typing in.
/// </para>
/// <para>
/// <b>A refusal outranks progress.</b> A press refused because the last
/// dictation was still going in arrives while that dictation is between
/// "Transcribing" and its receipt. Those two are not news, and replacing the
/// refusal with them would take it down before it could be read, so while it
/// is up they are dropped. A new hold, and anything worth a caution, still
/// replaces it.
/// </para>
/// <para>
/// <b>A HUD that throws never stops dictation.</b> The pill is a courtesy; the
/// paste is the product. A window that fails to draw is logged and skipped.
/// </para>
/// </remarks>
public sealed partial class DictationHudFeed : IDisposable
{
    /// <summary>
    /// How long a progress pill stays once its session is back at rest with no
    /// result to replace it. A result follows at once in every ordinary
    /// dictation, posted just behind the state change, and replaces the pill;
    /// hiding at the state change instead would flash an empty screen between
    /// the two. This only bounds a pill nothing replaced.
    /// </summary>
    public static readonly TimeSpan RestGrace = DictationHudModel.ReceiptDwell;

    /// <summary>How long "Remembered your correction" stays: the Mac's 3 s.
    /// The reading rule every line here is held to counts its eight words and
    /// the dash as nine and asks 2.65 s, more than Windows' 2.5 s notice
    /// class.</summary>
    public static readonly TimeSpan RememberedDwell = TimeSpan.FromSeconds(3);

    private readonly IDictationHud _hud;
    private readonly Func<DictationHudHold> _hold;
    private readonly ILogger _log;
    private readonly ClockDelay _delay;
    private readonly DictationHudDwell _dwell;
    private bool _refusalShowing;
    private bool _disposed;

    /// <summary>The pill on screen is a listening one, held or hands-free,
    /// so the level bars stand in its mark (<see cref="Level"/>).</summary>
    private bool _metering;

    /// <summary>A level draw threw during this hold: none is tried again
    /// until the next hold listens. Per hold rather than per pill because
    /// the hands-free pill is shown again every second.</summary>
    private bool _levelFailed;

    /// <summary>The last level the controller reported.</summary>
    private float _level;

    /// <param name="hud">The window that draws.</param>
    /// <param name="clock">The controller's clock, which the dwells run on.</param>
    /// <param name="hold">The hold key's name and the longest recording, read
    /// at each pill: the hints, the busy width and some endings name them.</param>
    /// <param name="log">Where a window that cannot draw is reported.</param>
    public DictationHudFeed(
        IDictationHud hud, Func<TimeSpan> clock, Func<DictationHudHold> hold, ILogger<DictationHudFeed>? log = null)
    {
        _hud = hud ?? throw new ArgumentNullException(nameof(hud));
        _hold = hold ?? throw new ArgumentNullException(nameof(hold));
        _log = log ?? (ILogger)NullLogger.Instance;
        _delay = new ClockDelay(clock ?? throw new ArgumentNullException(nameof(clock)));
        _dwell = new DictationHudDwell(_delay, HideNow);
    }

    /// <summary>What is on screen now; <see cref="DictationHudModel.Hidden"/>
    /// when nothing is.</summary>
    public DictationHudModel Shown { get; private set; } = DictationHudModel.Hidden;

    /// <summary>The session moved to <paramref name="state"/>.</summary>
    public void State(DictationState state)
    {
        switch (state)
        {
            case DictationState.Listening:
                // Shown, never spoken: the microphone is open. The held hint
                // until the controller's clock says otherwise (Listening). The
                // level bars stand in the mark; the title stays "Listening" at
                // every level, as on the Mac, until the live preview (when it
                // is on) has words for it. A new hold tries the bars afresh,
                // and starts with no caption.
                _levelFailed = false;
                _caption = null;
                _silenceWarningShown = false;
                Show(ListeningPill(DictationCopy.HudHeld, latched: false), announce: false, meter: true);
                return;

            case DictationState.Transcribing or DictationState.Inserting:
                if (_refusalShowing)
                {
                    return;
                }

                // After a stop the app made too (the limit, a silence), as on
                // the Mac: the countdown or the warning said it was coming,
                // and the ending says what happened. Each is a state of its
                // own to the pacer.
                Show(
                    Busy(state == DictationState.Transcribing ? DictationCopy.HudTranscribing : DictationCopy.HudTyping),
                    busy: state == DictationState.Transcribing ? DictationHudPace.Transcribing : DictationHudPace.Typing);
                return;

            default:
                // Back at rest: the result that follows says how it ended.
                // A progress pill nothing replaces must still go, and one
                // the pacer held back describes a state that is over.
                DropDeferred();
                if (Shown.IsVisible && Shown.Dwell is null)
                {
                    _dwell.Showing(Shown with { Dwell = RestGrace });
                }

                return;
        }
    }

    /// <summary>A dictation ended.</summary>
    public void Result(DictationSessionResult result, DictationHudContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        var model = For(result, context, _hold());
        if (_refusalShowing && model.Tone == HudTone.Neutral)
        {
            return;
        }

        Show(model);
    }

    /// <summary>A press was refused before the microphone opened.</summary>
    public void PressRefused(DictationPressRefusal why) => Show(
        why switch
        {
            DictationPressRefusal.StillTyping =>
                new DictationHudModel(DictationCopy.HudStillTyping, HudTone.Caution, DictationHudModel.NoticeDwell),
            DictationPressRefusal.StillCopying =>
                new DictationHudModel(DictationCopy.HudStillCopying, HudTone.Caution, DictationHudModel.NoticeDwell),
            _ => new DictationHudModel(DictationCopy.HudPasswordFieldAtPress, HudTone.Caution, DictationHudModel.FailureDwell),
        },
        refusal: true);

    /// <summary>Copy last text finished: "Copied" only when the clipboard
    /// took the words, which is the only time Ctrl+V is promised.</summary>
    public void Copied(bool copied) => Show(copied
        ? new DictationHudModel(DictationCopy.HudCopied, HudTone.Neutral, DictationHudModel.NoticeDwell)
        : new DictationHudModel(DictationCopy.HudCopyFailed, HudTone.Caution, DictationHudModel.FailureDwell));

    /// <summary>
    /// A correction saved a new word or rewrite while dictation is at rest.
    /// Neutral, like the receipt it is, and unrelated to whatever is on the
    /// pill, so a caution still up — a refusal, "Couldn't copy", a recogniser
    /// that could not restart — keeps it: its "what to do" half is the one
    /// that matters, and the window still shows the receipt. The Mac's
    /// replaces whatever is up.
    /// </summary>
    /// <param name="announce">False when the Dictation window is in front,
    /// whose own receipt line Narrator reads instead, so it is heard once.</param>
    public void CorrectionRemembered(bool announce = true)
    {
        // Every refusal is a caution, so this keeps a refusal up too.
        if (Shown.IsVisible && Shown.Tone == HudTone.Caution)
        {
            return;
        }

        Show(new DictationHudModel(DictationCopy.HudCorrectionRemembered, HudTone.Neutral, RememberedDwell), announce: announce);
    }

    /// <summary>The recogniser could not be brought back after it stopped,
    /// at rest or while a release is transcribed. Never while a hold
    /// listens: the controller lets the hold's pill stand, as the Mac's live
    /// hold outranks an ending, and the release's own ending says so.</summary>
    public void RecognizerCouldNotRestart() =>
        Show(new DictationHudModel(DictationCopy.HudRecognizerCouldNotRestart, HudTone.Caution, DictationHudModel.FailureDwell));

    /// <summary>Dictation was paused or stopped: nothing on screen describes
    /// anything any more.</summary>
    public void Clear()
    {
        DropDeferred();
        _dwell.Cancel();
        if (Shown.IsVisible)
        {
            HideNow();
        }
    }

    /// <summary>The controller's tick: take down a pill whose dwell is over,
    /// draw one the pacer held back once it may replace the one on screen,
    /// and, once a second, the heartbeat (<see cref="Heartbeat"/>).</summary>
    public void Tick()
    {
        _delay.RunDue();
        Heartbeat();
    }

    /// <summary>
    /// The microphone's smoothed level (<see cref="DictationLevelMeter"/>).
    /// Drawn only while the listening pill is up, and remembered, so a
    /// listening pill shown again draws the current level at once. Never
    /// announced: it changes the bars, not the sentence.
    /// </summary>
    public void Level(float level)
    {
        _level = level;
        if (_metering && !Draw(() => _hud.Level(level)))
        {
            // Once per hold: a window that cannot draw a level would throw
            // and be logged twelve times a second for the rest of the hold,
            // or, hands-free, once for each second's pill.
            _metering = false;
            _levelFailed = true;
        }
    }

    /// <summary>
    /// The pill for <paramref name="result"/>. The shared model's, except
    /// where the controller knows more (the words kept on the clipboard or
    /// only in memory, a shortcut, a take a fault ended or one kept for
    /// recovery, a stop the app made itself, a recogniser restart), where its
    /// line names the other app or a place in its
    /// settings, and where it says what happened and not what to do (those
    /// keep its tone and dwell and gain the second half). The model is asked
    /// without the target window, so no line names the process the words
    /// went to either.
    /// </summary>
    /// <param name="result">How the dictation ended.</param>
    /// <param name="context">What the controller knows besides.</param>
    /// <param name="hold">The hold key, which some lines name, and the limit.</param>
    public static DictationHudModel For(DictationSessionResult result, DictationHudContext context, DictationHudHold hold)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(hold);
        const HudTone caution = HudTone.Caution;
        var shared = DictationHudModel.For(result with { BaselineText = null, Target = null });
        // A kept take and a take a fault ended first (DictationHudFeed.Takes.cs).
        return ForTake(result, context) ?? result.Outcome switch
        {
            DictationOutcome.RecognizerUnavailable or DictationOutcome.RecognizerFailed
                when context.RecognizerRestarting =>
                new(DictationCopy.HudRecognizerRestarting, caution, DictationHudModel.FailureDwell),
            DictationOutcome.RecognizerUnavailable =>
                new(DictationCopy.HudRecognizerNotRunning, caution, DictationHudModel.FailureDwell),
            DictationOutcome.RecognizerFailed =>
                new(DictationCopy.HudRecognizerFailed, caution, DictationHudModel.FailureDwell),
            DictationOutcome.InsertionRefused or DictationOutcome.NoFocusedWindow when context.KeptOnClipboard =>
                new(DictationCopy.HudKeptOnClipboard, caution, DictationHudModel.FailureDwell),
            DictationOutcome.InsertionRefused or DictationOutcome.NoFocusedWindow when context.WordsKeptInApp =>
                new(DictationCopy.HudWordsStillHere, caution, DictationHudModel.FailureDwell),
            DictationOutcome.Cancelled when context.CancelledByShortcut =>
                new(DictationCopy.HudCancelledByShortcut, HudTone.Neutral, DictationHudModel.ReceiptDwell),
            // Hands-free, nothing heard since the latch, and the quiet-room
            // check found nothing to send: said as the stop it was, a
            // caution, not as the held key's "hold Right Ctrl and speak".
            DictationOutcome.NoSpeech when context.Stopped is { Stop: DictationHoldStop.NothingHeard } waited =>
                new(DictationCopy.HudNothingHeardStopped(hold.KeyName, waited.After), caution, DictationHudModel.FailureDwell),
            // The Mac's "Stopped at 2:00", and the silence and stuck-key
            // stops beside it: a caution, so a hold that just stopped is not
            // read as an ordinary "Typed". Windows' failure tier, not the
            // Mac's 3 s: the lines run to seventeen words, and the reading
            // floor every line here is held to (0.4 s, then four words a
            // second) asks for over four seconds.
            DictationOutcome.Inserted when context.Stopped is { Stop: DictationHoldStop.Ceiling } limit =>
                new(DictationCopy.HudStoppedAtLimit(hold.KeyName, limit.After), caution, DictationHudModel.FailureDwell),
            // Nothing heard is a silence too: the tracker heard none of the
            // words the recogniser then found.
            DictationOutcome.Inserted when context.Stopped is { Stop: DictationHoldStop.Silence or DictationHoldStop.NothingHeard } silence =>
                new(DictationCopy.HudStoppedAfterSilence(hold.KeyName, silence.After), caution, DictationHudModel.FailureDwell),
            DictationOutcome.Inserted when context.Stopped is { Stop: DictationHoldStop.StuckKey } stuck =>
                new(DictationCopy.HudStoppedStuckKey(hold.KeyName, stuck.After), caution, DictationHudModel.FailureDwell),
            DictationOutcome.MicrophoneBlocked =>
                new(DictationCopy.HudMicrophoneBlocked, caution, DictationHudModel.FailureDwell),
            DictationOutcome.SecureField =>
                new(DictationCopy.HudPasswordFieldAtRelease, caution, DictationHudModel.FailureDwell),
            DictationOutcome.InsertionRefused => shared with { Text = DictationCopy.HudCouldNotTypeOrCopy },
            DictationOutcome.NoFocusedWindow => shared with { Text = DictationCopy.HudNoWindow },
            DictationOutcome.NoSpeech => shared with { Text = DictationCopy.HudNothingHeard(hold.KeyName) },
            DictationOutcome.NoAudio => shared with { Text = DictationCopy.HudNoSound },
            DictationOutcome.MicrophoneUnavailable => shared with { Text = DictationCopy.HudMicrophoneWouldNotOpen },
            DictationOutcome.HoldTooShort => shared with { Text = DictationCopy.HudTooShort(hold.KeyName) },
            _ => shared,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Clear();
        _dwell.Dispose();
        _disposed = true;
    }

    /// <param name="meter">The level bars replace the mark: a listening
    /// pill, held or hands-free. The window puts the mark back on every show,
    /// so the bars are drawn again right after it, in the same turn, unless a
    /// level draw already failed in this hold.</param>
    /// <param name="busy">Transcribing or Typing, for a busy pill that is
    /// not a listening one; anything else that is not listening is an
    /// ending to the pacer.</param>
    private void Show(
        DictationHudModel model, bool refusal = false, bool announce = true, bool meter = false, DictationHudPace? busy = null)
    {
        if (_disposed)
        {
            return;
        }

        if (!model.IsVisible)
        {
            Clear();
            return;
        }

        model = SizedForTheHold(model);
        Offer(new Pill(model, PaceOf(model, meter, busy), refusal, announce, meter));
    }

    /// <summary>Draw a pill the pacer let through.</summary>
    private void Present(Pill pill)
    {
        var model = pill.Model;
        _refusalShowing = pill.Refusal;
        _metering = pill.Meter && !_levelFailed;
        Shown = model;
        Draw(() => _hud.Show(model, pill.Announce));
        if (_metering)
        {
            Level(_level);
        }

        _dwell.Showing(model);
    }

    private void HideNow()
    {
        PacedHidden();
        _refusalShowing = false;
        ShowingListening(false);
        _metering = false;
        Shown = DictationHudModel.Hidden;
        Draw(_hud.Hide);
    }

    /// <returns>Whether the window drew it.</returns>
    private bool Draw(Action draw)
    {
        try
        {
            draw();
            return true;
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "The dictation indicator could not be drawn");
            return false;
        }
    }

    /// <summary>
    /// <see cref="IDictationHudDelay"/> on the controller's clock: a delay is
    /// due once the clock has passed it, and runs in the next
    /// <see cref="RunDue"/>. Single-threaded, like the controller.
    /// </summary>
    private sealed class ClockDelay(Func<TimeSpan> clock) : IDictationHudDelay
    {
        private readonly List<Pending> _pending = [];

        /// <summary>The controller's clock now.</summary>
        public TimeSpan Now => clock();

        public IDisposable After(TimeSpan delay, Action run)
        {
            var pending = new Pending(this, clock() + delay, run);
            _pending.Add(pending);
            return pending;
        }

        public void RunDue()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var now = clock();
            foreach (var due in _pending.Where(p => p.Due <= now).ToArray())
            {
                _pending.Remove(due);
                due.Run();
            }
        }

        private sealed class Pending(ClockDelay owner, TimeSpan due, Action run) : IDisposable
        {
            public TimeSpan Due { get; } = due;

            public Action Run { get; } = run;

            public void Dispose() => owner._pending.Remove(this);
        }
    }
}
