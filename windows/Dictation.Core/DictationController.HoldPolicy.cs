using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): how long a hold
// may listen (DictationHoldPolicy) — the recording limit and the countdown and
// sound before it, the memory cap behind it, the in-hold silence tracker and
// the stops it drives — and the window's hands-free silence choice. The
// shared partials only call in here: the tick (Hold.cs), the press's admission
// and its cancel (Hold.cs), arming, stopping and the session's events
// (Session.cs), and the listening pill's clock (Hints.cs).
public sealed partial class DictationController
{
    // ─── Per hold ───────────────────────────────────────────────────────

    /// <summary>The stop the app made on the hold being transcribed, or null
    /// for a hold the user released. Read when its result arrives.</summary>
    private DictationHoldEnding? _holdEnding;

    /// <summary>The silence tracker of the hold that is listening, and the
    /// microphone it is attached to; null at every other time.</summary>
    private (DictationSilenceTracker Tracker, IAudioCaptureDevice Microphone)? _silence;

    /// <summary>The tracker's speech count at the last tick.</summary>
    private long _speechSeen;

    /// <summary>When this hold last heard speech, on the controller's clock;
    /// null until it does.</summary>
    private TimeSpan? _lastSpeechAt;

    /// <summary>The countdown's sound has played for this hold.</summary>
    private bool _countdownSounded;

    /// <summary>Until when the tracker's speech is the countdown's own sound
    /// heard back, not the user; null before the first sound. The clock only
    /// moves on, so a value left from an earlier hold is in the past and
    /// changes nothing.</summary>
    private TimeSpan? _soundHeardUntil;

    /// <summary>
    /// How long after the countdown's sound what the tracker hears does not
    /// restart the silence: 2 s. The sound plays through the speakers while
    /// the microphone is open, and a chime of 250 ms or more over the room is
    /// speech to the tracker, which would give a user who has fallen silent
    /// one more silence stop's wait. Two seconds is meant to cover Windows'
    /// Asterisk sound and the capture's buffering; neither has been measured
    /// on Windows. Words said only inside it restart nothing, and a stop that
    /// comes because of that still inserts them.
    /// </summary>
    private static readonly TimeSpan SoundHeardBack = TimeSpan.FromSeconds(2);

    private DictationHoldPolicy Policy => _options.HoldPolicy;

    // ─── The hands-free silence choice ──────────────────────────────────

    /// <summary>The choices the window's "Silence stops hands-free" picker
    /// offers, in order.</summary>
    public IReadOnlyList<DictationSilenceStopChoice> SilenceStopChoices => DictationSilenceStopChoice.All;

    /// <summary>
    /// End a hands-free dictation after 30 s or 60 s of silence, or never,
    /// from now on, and remember it. It applies at once, to a hold that is
    /// listening too: the policy reads it every tick.
    /// </summary>
    public void ChooseSilenceStop(DictationSilenceStop choice)
    {
        if (_disposed || !Enum.IsDefined(choice) || choice == _settings.SilenceStop)
        {
            return;
        }

        _settings = _settings with { SilenceStop = choice };
        var saved = _settingsStore.Save(_settings);
        _log.LogInformation("Hands-free silence stop set to {Choice} (saved: {Saved})", choice, saved);
        Render(saved ? null : new DictationStatus(DictationCopy.SilenceStopNotSaved));
    }

    // ─── The hold's clock ───────────────────────────────────────────────

    /// <summary>
    /// The press's microphone has just come up, or failed to: the hold's
    /// clock starts here, as the Mac's does once capture is running, not at
    /// the admission before it. A device that takes a second to open costs
    /// the user none of the limit.
    /// </summary>
    private void StartHoldClock()
    {
        _holdStartedAt = _options.Clock();
        _holdEnding = null;
        _lastSpeechAt = null;
        _countdownSounded = false;
    }

    /// <summary>
    /// Once a tick, before the release poll: end the hold the policy says to
    /// end, and sound the countdown once when it starts.
    /// </summary>
    /// <remarks>
    /// The clock decides, not the audio: a microphone that stops delivering
    /// is silence too. A stop that inserts ends the hold as a release would:
    /// the hold reads released until the key really comes up, so one press
    /// is one dictation, and a hands-free latch ends.
    /// </remarks>
    private void EnforceHoldPolicy()
    {
        if (_holdStartedAt is not { } started || _hotkey is not { IsHeld: true } || _gate is not { EndedEarly: false })
        {
            return;
        }

        var now = _options.Clock();
        NoteSpeech(now);
        var verdict = Policy.Judge(Moment(now, started), _settings.SilenceStop);
        if (verdict.Ending is { } ending)
        {
            EndHold(ending);
            return;
        }

        if (verdict.CountingDown && !_countdownSounded)
        {
            _countdownSounded = true;
            _log.LogInformation(
                "The hold is {Left:0} s from the {Limit:0} s limit; counting down", (Policy.Ceiling - (now - started)).TotalSeconds,
                Policy.Ceiling.TotalSeconds);
            _soundHeardUntil = now + SoundHeardBack;
            PlayAlertSound();
        }
    }

    /// <summary>The hold now, as the policy judges it.</summary>
    private DictationHoldMoment Moment(TimeSpan now, TimeSpan started) => new(
        now - started,
        _trigger?.IsLatched == true,
        _lastSpeechAt is not null,
        now - (_lastSpeechAt ?? started));

    /// <summary>What the listening pill shows besides the clock: the silence
    /// stop's warning, when the policy gives one now.</summary>
    private DictationSilenceWarning? SilenceWarningNow(TimeSpan now, TimeSpan started) =>
        Policy.Judge(Moment(now, started), _settings.SilenceStop).Warning;

    /// <summary>
    /// End the hold as a release would, whichever stop it is: what was
    /// captured goes on to the session, which transcribes and inserts it.
    /// </summary>
    /// <remarks>
    /// Nothing heard too, though the tracker heard no speech: it can miss
    /// soft speech in a noisy room (a frame must be 10 dB over the room), and
    /// a stop that threw the take away would lose those words. The session's
    /// own quiet-room check still keeps a silent room from the recogniser,
    /// and an empty transcript inserts nothing.
    /// </remarks>
    private void EndHold(DictationHoldEnding ending)
    {
        var why = ending.Stop switch
        {
            DictationHoldStop.Silence => $"{ending.After.TotalSeconds:0} s of silence",
            DictationHoldStop.NothingHeard => $"nothing was heard in {ending.After.TotalSeconds:0} s",
            DictationHoldStop.StuckKey => $"no speech for {ending.After.TotalSeconds:0} s with the key held",
            _ => "the recording limit",
        };
        _log.LogInformation(
            ending.Stop switch
            {
                DictationHoldStop.Ceiling => "The hold reached the {Limit:0} s limit; transcribing what was captured",
                DictationHoldStop.NothingHeard =>
                    "Nothing was heard in {Limit:0} s of hands-free dictation; it ends as a release, and the quiet-room check decides what is transcribed",
                _ => "The hold ended after {Limit:0} s without speech; transcribing what was captured",
            },
            ending.After.TotalSeconds);
        // The receipt's ending says the app ended the take, and by which stop;
        // before the release, which a hands-free latch makes at once.
        Attempts.Stopped(ending.Stop);
        _holdStartedAt = null;
        _holdEnding = ending;
        _gate!.EndedEarly = true;
        // A hands-free hold ends here as if the key had been released.
        EndHandsFree(why);
    }

    /// <summary>The countdown's one sound. A sound that fails never stops a
    /// dictation.</summary>
    private void PlayAlertSound()
    {
        try
        {
            _platform.PlayAlertSound();
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "The countdown's sound could not be played");
        }
    }

    // ─── The memory cap ─────────────────────────────────────────────────

    /// <summary>The tap's buffer reached the memory cap: raised on a pool
    /// thread, handled on the controller's.</summary>
    private void OnBufferLimitReached(object? sender, MicrophoneTapLimit limit) =>
        _post(() => BufferLimitReached(limit.OperationId));

    /// <summary>
    /// The tap stopped keeping audio at <see cref="DictationHoldPolicy.MemoryCap"/>
    /// before the limit ended the hold, so the tick did not run in time (the
    /// UI thread stalled past the limit). The hold ends now as a limit stop,
    /// with everything the tap kept, instead of recording on into audio it
    /// already drops.
    /// </summary>
    /// <remarks>
    /// Only for the capture that is listening now, named by its operation
    /// id: a report that reaches this thread after its hold was released or
    /// cancelled and the next hold began, or from a tap a re-arm replaced,
    /// names a capture that is over, and the hold listening now is not at
    /// any cap.
    /// </remarks>
    /// <param name="capture">The operation id of the capture at the cap.</param>
    private void BufferLimitReached(string? capture)
    {
        if (capture is null || capture != _tap?.CurrentOperationId || _holdStartedAt is null
            || _hotkey is not { IsHeld: true } || _gate is not { EndedEarly: false })
        {
            _log.LogDebug("The microphone's memory cap was reached by a capture no hold is waiting on");
            return;
        }

        _log.LogWarning(
            "The recording reached the {Cap:0} s memory cap before the {Limit:0} s limit ended it",
            Policy.MemoryCap.TotalSeconds, Policy.Ceiling.TotalSeconds);
        EndHold(new DictationHoldEnding(DictationHoldStop.Ceiling, Policy.Ceiling));
    }

    // ─── The silence tracker ────────────────────────────────────────────

    /// <summary>
    /// Follow the session into and out of Listening, as the level meter does:
    /// a tracker per hold, reading this arm's microphone while the hold
    /// listens and at no other time. Started at the posted repaint, so a
    /// state the last dictation posted late never stops this hold's tracker.
    /// </summary>
    private void FollowSilence(DictationState state)
    {
        if (state == DictationState.Listening)
        {
            StartSilence();
        }
        else
        {
            StopSilence();
        }
    }

    private void StartSilence()
    {
        if (_microphone is not { } microphone || _silence is not null)
        {
            return;
        }

        var tracker = new DictationSilenceTracker();
        microphone.BufferCaptured += tracker.Offer;
        _silence = (tracker, microphone);
        _speechSeen = 0;
    }

    private void StopSilence()
    {
        if (_silence is not { } silence)
        {
            return;
        }

        _silence = null;
        silence.Microphone.BufferCaptured -= silence.Tracker.Offer;
    }

    /// <summary>Speech heard since the last tick restarts the silence,
    /// unless it is the countdown's sound heard back.</summary>
    private void NoteSpeech(TimeSpan now)
    {
        if (_silence is { } silence && silence.Tracker.SpeechFrames is var frames && frames != _speechSeen)
        {
            _speechSeen = frames;
            if (_soundHeardUntil is not { } heardBack || now >= heardBack)
            {
                _lastSpeechAt = now;
            }
        }
    }

    // ─── What the window and the indicator say ──────────────────────────

    /// <summary>The window's line while a hold the app ended is transcribed;
    /// <see cref="StatusFor"/> for any other.</summary>
    private string TranscribingStatus(DictationState state) =>
        state == DictationState.Transcribing && _holdEnding is { } ending
            ? DictationCopy.StoppedTranscribing(ending, _holdKey)
            : StatusFor(state);

    /// <summary>The window's line for a result the app's own stop shaped,
    /// or null for the ordinary one: the words went in after a stop, or
    /// nothing heard found nothing to send.</summary>
    private DictationStatus? StatusAfterStop(DictationSessionResult result, DictationHoldEnding? stop) =>
        (result.Outcome, stop) switch
        {
            (DictationOutcome.Inserted, { } inserted) =>
                new DictationStatus(DictationCopy.InsertedAfterStop(inserted, _holdKey)),
            (DictationOutcome.NoSpeech, { Stop: DictationHoldStop.NothingHeard } waited) =>
                new DictationStatus(DictationCopy.NothingHeardStopped(waited.After, _holdKey)),
            _ => null,
        };
}
