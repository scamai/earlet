using System.Globalization;
using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>How the controller ends an attempt itself: the endings that are
/// not a session result.</summary>
public enum DictationAttemptEnding
{
    /// <summary>Escape or the window's Cancel, or a pause or quit while the
    /// press or the hold was still live.</summary>
    Cancelled,

    /// <summary>The hold key was part of a shortcut, such as Right Ctrl+C.</summary>
    Shortcut,

    /// <summary>A password field at the press: the microphone never opened.</summary>
    PrivacyRefused,

    /// <summary>A press while the last dictation was still being transcribed
    /// or typed. Windows only: the Mac logs that press without an attempt.</summary>
    StillFinishing,

    /// <summary>A press while Copy last text was still writing the
    /// clipboard. Windows only: the Mac's copy holds the same busy flag as a
    /// dictation, and the Mac logs that press without an attempt.</summary>
    StillCopying,

    /// <summary>The key came up before the press-time password check
    /// answered, so the microphone never opened. Windows only: the Mac has no
    /// check before its microphone.</summary>
    ReleasedBeforeCapture,

    /// <summary>A bug path, never seen in a healthy log: an outcome
    /// <see cref="DictationAttemptReceipt"/> does not name, or an admitted hold
    /// the session went back to rest from without answering (its pipeline
    /// threw).</summary>
    Unclassified,
}

/// <summary>
/// The fixed words an attempt's ending, recovery and copy lines may carry.
/// Apart from its id and its numbers, this table is the only text a receipt
/// logs: no transcript, no undelivered words, no target process name or window
/// title, no error message.
/// </summary>
/// <remarks>
/// The Mac's names where the meaning is the same
/// (<c>macos/Sources/DictationAppCore/DictationAttemptDiagnostics.swift</c>);
/// Windows-only reasons where Windows tells apart what the Mac does not, or
/// fails where the Mac cannot. Windows has no <c>timed_out</c>: a
/// transcription past its deadline comes back as a failed recognition.
/// </remarks>
public static class DictationAttemptReceipt
{
    /// <summary>The ending of <see cref="DictationAttemptEnding.Unclassified"/>.</summary>
    public const string Unclassified = "result=failed stage=unknown reason=unclassified";

    /// <summary>An ending the controller decided.</summary>
    public static string For(DictationAttemptEnding ending) => ending switch
    {
        DictationAttemptEnding.Cancelled => "result=cancelled",
        DictationAttemptEnding.Shortcut => "result=shortcut",
        DictationAttemptEnding.PrivacyRefused => "result=privacy_refused",
        DictationAttemptEnding.StillFinishing => "result=still_finishing",
        DictationAttemptEnding.StillCopying => "result=still_copying",
        DictationAttemptEnding.ReleasedBeforeCapture => Failed("capture", "released_before_capture"),
        _ => Unclassified,
    };

    /// <summary>
    /// The session's result. A cancel the hook latched for a chord is the
    /// shortcut ending; every other cancel is a cancel. Windows has no
    /// <c>microphone_changed</c> since 2026-10-10: a microphone that goes away
    /// ends the take and its words go in, so it ends as they do.
    /// </summary>
    /// <remarks>
    /// Windows restores the clipboard before the session leaves Inserting, so
    /// a restore is never detached; an inserted dictation whose clipboard did
    /// not come back is <see cref="DictationOutcome.ClipboardRestoreFailed"/>.
    /// A password field found at the release ends before recognition, so its
    /// stage is <c>capture</c>; one found at the paste is the Mac's
    /// <c>paste</c> (see <see cref="For(DictationSessionResult, TriggerCancel)"/>). This app
    /// composes no workflow router and no voice delete, so it never reaches
    /// those outcomes, and names them as the Mac names a spoken command.
    /// </remarks>
    public static string For(DictationOutcome outcome, TriggerCancel cancelledBy = TriggerCancel.None) => outcome switch
    {
        DictationOutcome.Inserted => "result=inserted clipboard_unchanged=true restore_detached=false",
        DictationOutcome.ClipboardRestoreFailed => "result=inserted clipboard_unchanged=false restore_detached=false",
        DictationOutcome.Cancelled => For(Ending(cancelledBy)),
        // The shared session's outcome. This app's tap has no minimum hold
        // (DictationHoldPolicy.TapOptions), so it never reports one.
        DictationOutcome.HoldTooShort => Failed("capture", "hold_too_short"),
        DictationOutcome.MicrophoneBlocked => Failed("capture", "microphone_permission"),
        DictationOutcome.MicrophoneUnavailable => Failed("capture", "microphone_unavailable"),
        DictationOutcome.NoAudio => Failed("capture", "empty_capture"),
        DictationOutcome.SecureField => Failed("capture", "secure_field"),
        DictationOutcome.NoSpeech => Failed("asr", "no_text"),
        DictationOutcome.RecognizerUnavailable => "result=engine_unavailable",
        DictationOutcome.RecognizerFailed => Failed("asr", "recognition_failed"),
        DictationOutcome.NoFocusedWindow => Failed("paste", "no_field"),
        DictationOutcome.InsertionRefused => Failed("paste", "paste_refused"),
        DictationOutcome.RanWorkflow or DictationOutcome.WorkflowNotMatched or DictationOutcome.VoiceDeleted
            or DictationOutcome.VoiceDeleteNothingToDelete or DictationOutcome.VoiceDeleteRefused =>
            "result=command_completed",
        _ => Unclassified,
    };

    /// <summary>
    /// The session's result, with the stage a password field was found at:
    /// at the paste it is the Mac's <c>result=failed stage=paste
    /// reason=secure_field</c>, word for word; everything else is
    /// <see cref="For(DictationOutcome, TriggerCancel)"/>.
    /// </summary>
    public static string For(DictationSessionResult result, TriggerCancel cancelledBy = TriggerCancel.None)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result is { Outcome: DictationOutcome.SecureField, SecureFieldAtPaste: true }
            ? Failed("paste", "secure_field")
            : For(result.Outcome, cancelledBy);
    }

    /// <summary>
    /// After a refused paste: whether the words reached the clipboard, and
    /// whether Dictation still holds them for Copy last text. The Mac's
    /// <c>recovery(clipboard:retained:)</c> fields, word for word.
    /// </summary>
    public static string Recovery(bool clipboard, bool availableInApp) =>
        $"clipboard={Flag(clipboard)} available_in_app={Flag(availableInApp)}";

    /// <summary>
    /// How Copy last text went for the words this attempt left: the Mac's
    /// "recent text copy: copied" and "failed; available for retry", as
    /// fields. A failed copy always keeps the words for another try.
    /// </summary>
    public static string Copy(bool copied) => copied ? "result=copied" : "result=failed available_in_app=true";

    /// <summary>
    /// The fault that ended a take before the user did, as the field the
    /// ending carries after its timings: <c>stop=microphone_stalled</c>,
    /// <c>microphone_failed</c>, <c>microphone_removed</c> or
    /// <c>worker_lost</c>. A take a fault ended still ends as its words did
    /// (<c>result=inserted</c>, say), so without it the receipt could not
    /// tell a cut-short take from a release. Null for no fault.
    /// </summary>
    public static string? Stop(DictationTakeStop stop) => stop switch
    {
        DictationTakeStop.MicrophoneStalled => "stop=microphone_stalled",
        DictationTakeStop.MicrophoneFailed => "stop=microphone_failed",
        DictationTakeStop.MicrophoneRemoved => "stop=microphone_removed",
        DictationTakeStop.WorkerLost => "stop=worker_lost",
        _ => null,
    };

    /// <summary>
    /// The stop the hold policy made (<see cref="DictationHoldPolicy"/>), as
    /// the same field: <c>stop=recording_limit</c> (the memory cap behind it
    /// too), <c>silence</c>, <c>nothing_heard</c> or <c>stuck_key</c>. Such a
    /// take ends as a release does, its words inserted or kept for Recover
    /// last dictation, so it too would otherwise read as a release.
    /// </summary>
    public static string Stop(DictationHoldStop stop) => stop switch
    {
        DictationHoldStop.Silence => "stop=silence",
        DictationHoldStop.NothingHeard => "stop=nothing_heard",
        DictationHoldStop.StuckKey => "stop=stuck_key",
        _ => "stop=recording_limit",
    };

    /// <summary>The controller's ending for a cancel, by its reason.</summary>
    public static DictationAttemptEnding Ending(TriggerCancel cancelledBy) => cancelledBy switch
    {
        TriggerCancel.Chord => DictationAttemptEnding.Shortcut,
        _ => DictationAttemptEnding.Cancelled,
    };

    private static string Failed(string stage, string reason) => $"result=failed stage={stage} reason={reason}";

    /// <summary>The Mac's <c>true</c> and <c>false</c>, never .NET's
    /// capitalised <c>True</c>.</summary>
    private static string Flag(bool value) => value ? "true" : "false";
}

/// <summary>
/// One attempted hold's private receipt in the log: an id, a start, a release
/// where one was reached, and exactly one classified ending with the elapsed,
/// release, recognition and paste times in milliseconds, or <c>n/a</c> for a
/// stage not reached. After the ending, a refused paste adds one recovery
/// line, and each Copy last text of the words the attempt left adds a copy
/// line, under the same id. The Windows port of the Mac's
/// <c>DictationAttemptDiagnostics</c>.
/// </summary>
/// <remarks>
/// It takes no text. An ending is a <see cref="DictationAttemptEnding"/> or a
/// session result, and only <see cref="DictationAttemptReceipt"/>'s words
/// reach the line. Locked, though in the app every call comes from the UI
/// thread: the session resumes its pipeline there (every await in it is
/// <c>ConfigureAwait(true)</c>). The tests' rig has no UI thread and runs the
/// pipeline on pool threads.
/// </remarks>
public sealed class DictationAttemptLog
{
    private readonly object _sync = new();
    private readonly ILogger _log;
    private readonly Func<TimeSpan> _clock;
    private readonly TimeSpan _started;
    private TimeSpan? _released;
    private bool _finished;
    private string? _stop;

    /// <summary>Starts the attempt and logs <c>event=started</c>.</summary>
    /// <param name="clock">A monotonic clock, the controller's.</param>
    public DictationAttemptLog(ILogger log, Func<TimeSpan> clock)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _started = clock();
        _log.LogInformation("dictation attempt={Attempt} event=started", Id);
    }

    /// <summary>What ties the attempt's lines together.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The hold was let go to transcription: the key came up, a
    /// quick tap's pair window ran out, a hands-free latch was stopped, or the
    /// app ended the hold itself (the recording limit, a silence stop). Only
    /// the first counts, and none after the ending.</summary>
    public void MarkReleased()
    {
        lock (_sync)
        {
            if (_released is not null || _finished)
            {
                return;
            }

            _released = _clock();
            _log.LogInformation("dictation attempt={Attempt} event=released", Id);
        }
    }

    /// <summary>A fault ended the take before the user did: its ending
    /// carries <see cref="DictationAttemptReceipt.Stop(DictationTakeStop)"/>.
    /// The first stop counts, a fault's or the policy's, and none after the
    /// ending.</summary>
    public void MarkStopped(DictationTakeStop stop) => MarkStopped(DictationAttemptReceipt.Stop(stop));

    /// <summary>The hold policy ended the take: its ending carries
    /// <see cref="DictationAttemptReceipt.Stop(DictationHoldStop)"/>. The
    /// first stop counts, and none after the ending.</summary>
    public void MarkStopped(DictationHoldStop stop) => MarkStopped(DictationAttemptReceipt.Stop(stop));

    private void MarkStopped(string? field)
    {
        lock (_sync)
        {
            if (!_finished && _stop is null)
            {
                _stop = field;
            }
        }
    }

    /// <summary>Ends the attempt the controller's way. Only the first ending
    /// is logged.</summary>
    public void Finish(DictationAttemptEnding ending) => Finish(DictationAttemptReceipt.For(ending), null, null);

    /// <summary>Ends the attempt with the session's result. Only the first
    /// ending is logged.</summary>
    public void Finish(DictationSessionResult result, TriggerCancel cancelledBy = TriggerCancel.None)
    {
        ArgumentNullException.ThrowIfNull(result);
        Finish(DictationAttemptReceipt.For(result, cancelledBy), result.DecodeDuration, result.InsertDuration);
    }

    /// <summary>
    /// After a refused paste, where the words can still be had: on the
    /// clipboard, in Dictation for Copy last text, both or neither. The
    /// Mac's <c>event=recovery</c>, logged after the ending as on the Mac.
    /// </summary>
    public void Recovery(bool clipboard, bool availableInApp) =>
        _log.LogInformation(
            "dictation attempt={Attempt} event=recovery {Recovery}",
            Id,
            DictationAttemptReceipt.Recovery(clipboard, availableInApp));

    /// <summary>
    /// Copy last text answered for the words this attempt left. The Mac logs
    /// its copy without an attempt id; Windows ties it to the dictation whose
    /// words were copied, so one id follows those words from the press to
    /// their last copy.
    /// </summary>
    public void Copied(bool copied) =>
        _log.LogInformation(
            "dictation attempt={Attempt} event=copy {Copy}", Id, DictationAttemptReceipt.Copy(copied));

    private void Finish(string ending, TimeSpan? recognition, TimeSpan? paste)
    {
        lock (_sync)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            var now = _clock();
            var stop = _stop is { } field ? " " + field : string.Empty;
            _log.LogInformation(
                "dictation attempt={Attempt} event=finished {Ending} asr_ms={AsrMs} paste_ms={PasteMs} "
                + "elapsed_ms={ElapsedMs} release_ms={ReleaseMs}{Stop}",
                Id,
                ending,
                Milliseconds(recognition),
                Milliseconds(paste),
                Milliseconds(now - _started),
                Milliseconds(now - _released),
                stop);
        }
    }

    /// <summary>Whole milliseconds, or <c>n/a</c> for a stage not reached:
    /// absent is not zero.</summary>
    private static string Milliseconds(TimeSpan? value) => value is { } span
        ? Math.Max(0, Math.Round(span.TotalMilliseconds)).ToString("0", CultureInfo.InvariantCulture)
        : "n/a";
}

/// <summary>
/// Which attempt a press, a release, a cancel or a session result belongs to.
/// The controller's hooks call it; it holds at most two attempts: the press
/// still being decided (the password-field check) and the hold the session
/// has, until the session's one result for it.
/// </summary>
/// <remarks>
/// <para>
/// Which hold a result belongs to rests on the session's order, on the UI
/// thread: it goes back to rest and raises its result in one call
/// (<c>DictationSession.Finish</c> and <c>Cancel</c> transition, then raise),
/// so no press can be admitted between the two, and a press is admitted only
/// once the session reads at rest. The session is not thread-safe and does
/// not promise this order to a caller that drives it from two threads at once,
/// so neither does this class.
/// </para>
/// <para>
/// <see cref="Completed"/> is called where the session raises the result,
/// before the controller posts it, so a result the post later drops as stale
/// (a quit that waited for the paste tears the controller down first) still
/// gets its own ending, and a press that reaches the controller before that
/// post cannot take it.
/// </para>
/// </remarks>
public sealed class DictationAttemptTracker
{
    private readonly object _sync = new();
    private readonly ILogger _log;
    private readonly Func<TimeSpan> _clock;
    private DictationAttemptLog? _pending;
    private DictationAttemptLog? _admitted;

    public DictationAttemptTracker(ILogger log, Func<TimeSpan> clock)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>A key-down edge: a new attempt, refused or not.</summary>
    public void Pressed()
    {
        lock (_sync)
        {
            // The last press's field check had not answered when its key
            // came up and went down again; that answer is now ignored.
            _pending?.Finish(DictationAttemptEnding.ReleasedBeforeCapture);
            _pending = new DictationAttemptLog(_log, _clock);
        }
    }

    /// <summary>The key reads up while the press is still being decided.</summary>
    public void KeyUp()
    {
        lock (_sync)
        {
            _pending?.MarkReleased();
        }
    }

    /// <summary>The press ended before the session had it: refused, or
    /// cancelled while its field check was out. A no-op once admitted.</summary>
    public void PressEnded(DictationAttemptEnding ending)
    {
        lock (_sync)
        {
            _pending?.Finish(ending);
            _pending = null;
        }
    }

    /// <summary>The id of the admitted hold the session has not answered
    /// yet, or null: what the live preview's numeric lines are tagged
    /// with, so they read beside that hold's receipt.</summary>
    public Guid? AdmittedId
    {
        get
        {
            lock (_sync)
            {
                return _admitted?.Id;
            }
        }
    }

    /// <summary>The press goes to the session, which answers it once.</summary>
    public void Admitted()
    {
        lock (_sync)
        {
            if (_pending is null)
            {
                return;
            }

            // A press is admitted only once the session reads at rest, and it
            // raises a hold's result in the same call that brings it there
            // (see the remarks). A hold still unanswered here never will be:
            // its pipeline threw, which the session logs as a bug, or the
            // session never took it. Left in place it would take this hold's
            // result.
            _admitted?.Finish(DictationAttemptEnding.Unclassified);
            _admitted = _pending;
            _pending = null;
        }
    }

    /// <summary>The admitted hold was released: the key came up, a quick
    /// tap's pair window ran out, a latch was stopped, or the app ended it
    /// (the recording limit, a silence stop).</summary>
    public void Released()
    {
        lock (_sync)
        {
            _admitted?.MarkReleased();
        }
    }

    /// <summary>A fault ended the admitted hold's take before the user did
    /// (DictationController.Takes.cs): its ending will say which.</summary>
    public void Stopped(DictationTakeStop stop)
    {
        lock (_sync)
        {
            _admitted?.MarkStopped(stop);
        }
    }

    /// <summary>The hold policy ended the admitted hold
    /// (DictationController.HoldPolicy.cs): its ending will say which stop.</summary>
    public void Stopped(DictationHoldStop stop)
    {
        lock (_sync)
        {
            _admitted?.MarkStopped(stop);
        }
    }

    /// <summary>The session's one result for the admitted hold. Returns that
    /// hold's receipt, for the lines that follow its ending (a refused
    /// paste's recovery, a later Copy last text), or null when no hold was
    /// admitted, which a healthy session never does.</summary>
    public DictationAttemptLog? Completed(DictationSessionResult result, TriggerCancel cancelledBy)
    {
        lock (_sync)
        {
            var attempt = _admitted;
            attempt?.Finish(result, cancelledBy);
            _admitted = null;
            return attempt;
        }
    }

    /// <summary>
    /// Dictation stopped (paused, failed, closed). A press still being decided
    /// ends as cancelled. Called after the session's own cancel, which has
    /// already answered a hold that was live (listening, transcribing or
    /// typing) with its <c>Cancelled</c> result, so an admitted hold still open
    /// here is one the session left without an answer: unclassified, never
    /// reported as a cancel the user made.
    /// </summary>
    public void Abandon()
    {
        lock (_sync)
        {
            _pending?.Finish(DictationAttemptEnding.Cancelled);
            _pending = null;
            _admitted?.Finish(DictationAttemptEnding.Unclassified);
            _admitted = null;
        }
    }
}
