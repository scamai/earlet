using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Services.Learning;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>Why a correction watch was ended from outside, before the
/// observer reached a terminal of its own.</summary>
public enum CorrectionWatchEnd
{
    /// <summary>The next dictation was released to transcription. A press
    /// alone is not one: it may yet be a shortcut.</summary>
    NextDictation,

    /// <summary>Dictation was paused.</summary>
    Paused,

    /// <summary>The window closed, or the app quit.</summary>
    Closed,

    /// <summary>"Learn from my corrections" was switched off.</summary>
    SwitchedOff,

    /// <summary>The user cleared the Vocabulary section's notices: Dismiss on
    /// the learning receipt, Add, Remove or Forget all. The Mac learns
    /// nothing more from that watch either, so no fix is saved that no
    /// receipt would report.</summary>
    NoticesCleared,
}

/// <summary>
/// After a delivered dictation, watches the text that was pasted for the
/// user's fixes and hands a settled fix to <see cref="DictationLearning"/>
/// (LEARNING.md §7). One watch at a time, for at most 90 seconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The observer is the imported one.</b> <see cref="SpanObserver"/>'s state
/// machine anchors the span, tracks it, and ends on its own terminals: the
/// window closing, the field losing focus, the span cleared, a secure or
/// unknown field, a terminal, this app's own window. It decides which edit
/// counts — after its autocorrect window, and only with keyboard input in
/// that field — and <see cref="SpanObservationResult.EditedSpan"/> is the
/// span's final text when one did. The learning policy then reduces that to
/// the corrected words; nothing else about the field is kept.
/// </para>
/// <para>
/// <b>Off the UI thread.</b> Resolving the field and every read are
/// cross-process UI Automation calls, bounded by <c>t_uia</c> but not free,
/// so the whole watch runs on the thread pool.
/// </para>
/// <para>
/// <b>Ending it.</b> <see cref="End"/> cancels the watch's token, and
/// <see cref="SpanObserver.RunAsync"/> ends the observation with its own
/// cancelled terminal on the thread that drives it. A watch that was ended
/// never learns, even when its edit settles in the same instant: the ending is
/// checked again, under a lock, after the observer settles.
/// </para>
/// <para>
/// <b>Counts, never words.</b> The log says how each watch ended and how long
/// it ran; <see cref="DictationLearning"/> logs what it learned as counts. No
/// text, application name or field content reaches either.
/// </para>
/// <para>
/// <b>What it tells.</b> <see cref="Started"/>, <see cref="Observed"/> for
/// each settled revision and <see cref="Ended"/>, with the same counts and the
/// observer's terminal: what the Vocabulary section's notice is made of
/// (<see cref="CorrectionNotices"/>). A listener that throws is logged and
/// costs neither the watch nor the other listeners.
/// </para>
/// </remarks>
public sealed class CorrectionWatch : ICorrectionWatchEvents
{
    /// <summary>The terminal of a watch ended from outside
    /// (<see cref="End"/>): the next dictation, pause, close, the switch, the
    /// user clearing the Vocabulary section.</summary>
    public const string TerminalAbandoned = "abandoned";

    /// <summary>The field that has focus belongs to another process than the
    /// one the paste went into. Nothing is read.</summary>
    public const string ReasonOtherApplication = "other_application";

    /// <summary>The process the paste went into is not known, so nothing can
    /// show that the field is still the one pasted into. Nothing is read or
    /// even resolved: the macOS app's <c>target_application_unknown</c>.</summary>
    public const string ReasonTargetUnknown = "target_unknown";

    /// <summary>The span observer's own knobs, with the 90-second window
    /// LEARNING.md §7 sets.</summary>
    public static SpanObserverOptions DefaultObserverOptions { get; } = new() { TWindowMs = 90_000 };

    private readonly DictationLearning _learning;
    private readonly Func<SpanObserverKeyboardSignal, SpanObserverOptions, ISpanObserverTarget> _resolve;
    private readonly SpanObserverOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly ILogger _log;
    private readonly Lock _sync = new();
    private Watch? _current;

    /// <param name="learning">Where a settled fix goes.</param>
    /// <param name="resolve">Resolves the focused field once, now
    /// (<see cref="IDictationPlatform.CreateSpanObserverTarget"/>).</param>
    /// <param name="log">Outcomes and durations only.</param>
    /// <param name="options">The observer's knobs; <see cref="DefaultObserverOptions"/> when null.</param>
    /// <param name="time">The wall clock the observer and the learned file use.</param>
    /// <param name="delay">How the observer waits between reads.</param>
    public CorrectionWatch(
        DictationLearning learning,
        Func<SpanObserverKeyboardSignal, SpanObserverOptions, ISpanObserverTarget> resolve,
        ILogger log,
        SpanObserverOptions? options = null,
        TimeProvider? time = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options = options ?? DefaultObserverOptions;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// The keyboard-origin feed: the hook records the time and foreground
    /// process of each key-down here while a watch runs (see
    /// <see cref="IDictationTrigger.KeyboardInputObserved"/>).
    /// </summary>
    public SpanObserverKeyboardSignal Keyboard { get; } = new();

    /// <inheritdoc/>
    public event EventHandler<CorrectionWatchStarted>? Started;

    /// <inheritdoc/>
    public event EventHandler<CorrectionWatchObserved>? Observed;

    /// <inheritdoc/>
    public event EventHandler<CorrectionWatchEnded>? Ended;

    /// <summary>Whether a watch is running.</summary>
    public bool IsWatching
    {
        get
        {
            lock (_sync)
            {
                return _current is { Completion.IsCompleted: false };
            }
        }
    }

    /// <summary>The latest watch, finished when this completes; a completed
    /// task when none ever ran. Never faults.</summary>
    public Task Completion
    {
        get
        {
            lock (_sync)
            {
                return _current?.Completion ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>
    /// Watch <paramref name="pasted"/>, which a confirmed paste just put into
    /// <paramref name="pastedInto"/>. A watch still running ends first, as the
    /// next dictation would end it.
    /// </summary>
    public void Begin(string pasted, FocusedApplicationResult pastedInto)
    {
        ArgumentNullException.ThrowIfNull(pasted);
        End(CorrectionWatchEnd.NextDictation);

        var watch = new Watch();
        // Before the watch can run, let alone end: a listener that marshals
        // to one thread gets this ahead of anything the watch says later.
        Raise(Started, new CorrectionWatchStarted(watch.Id));
        lock (_sync)
        {
            _current = watch;
            watch.Completion = Task.Run(() => RunAsync(watch, pasted, pastedInto));
        }
    }

    /// <summary>End the running watch, if any, without learning from it.</summary>
    public void End(CorrectionWatchEnd why)
    {
        Watch? watch;
        lock (_sync)
        {
            watch = _current;
        }

        watch?.End(why);
    }

    // ─── One watch ───

    private async Task RunAsync(Watch watch, string pasted, FocusedApplicationResult pastedInto)
    {
        var started = _time.GetUtcNow();
        CorrectionWatchEnded ended;
        string described;
        try
        {
            ended = await ObserveAsync(watch, pasted, pastedInto).ConfigureAwait(false);
            described = Describe(ended);
        }
        catch (Exception error)
        {
            ended = Ending(watch, SpanObservation.TerminalUnobservable, SpanObserverFailures.Exception, endedBy: watch.EndedBy);
            // The type only: an exception message is not promised to be free
            // of what it was handling.
            described = $"failed ({error.GetType().Name})";
        }

        _log.LogInformation(
            "Correction watch ended: {Ending} after {Seconds:0.0} s",
            described,
            (_time.GetUtcNow() - started).TotalSeconds);
        Raise(Ended, ended);
    }

    /// <returns>How the watch ended.</returns>
    private async Task<CorrectionWatchEnded> ObserveAsync(Watch watch, string pasted, FocusedApplicationResult pastedInto)
    {
        // A paste into a process nobody could name cannot be told apart from
        // a focus that moved: do not even resolve the field.
        if (!pastedInto.IsResolved)
        {
            return Ending(watch, SpanObservation.TerminalUnobservable, ReasonTargetUnknown);
        }

        var target = _resolve(Keyboard, _options);
        if (watch.EndedBy is { } before)
        {
            return Ending(watch, TerminalAbandoned, null, endedBy: before);
        }

        // The field the paste went into, and no other: a focus that moved to
        // another application in between is not the user fixing this text.
        var identity = target.Identity;
        if (identity.IsResolved && identity.ProcessId != pastedInto.ProcessId)
        {
            return Ending(watch, SpanObservation.TerminalUnobservable, ReasonOtherApplication);
        }

        var id = watch.Id.ToString("N");
        var dictated = SpanObservation.NormalizeLineEndings(pasted);
        var t0 = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var observation = new SpanObserver(_options).Begin(target, pasted, "h_" + id, t0, t0, out _);
        var result = await SpanObserver.RunAsync(
            observation, () => _time.GetUtcNow().ToUnixTimeMilliseconds(), watch.Token, _delay,
            edited =>
            {
                LearningObservation? outcome = null;
                watch.PublishIfActive(() =>
                {
                    outcome = _learning.Observe(id, dictated, edited, _time.GetUtcNow(), LearningScene.ForApplication(pastedInto.ProcessName));
                    watch.Revisions++;
                });

                // Outside the watch's lock: a listener runs no code under it.
                if (outcome is not null)
                {
                    Raise(Observed, new CorrectionWatchObserved(
                        watch.Id, watch.Revisions, !string.Equals(edited, dictated, StringComparison.Ordinal), outcome));
                }
            }).ConfigureAwait(false);

        // Asked again after the observer settled: an end that landed on the
        // very tick that settled the fix still wins. One that arrives after
        // this came after the fix had settled, and does not undo it.
        if (watch.EndedBy is { } why)
        {
            return Ending(watch, TerminalAbandoned, null, endedBy: why);
        }

        // Every settled revision already used the same insertion identity.
        // The terminal only closes the watch; it must not count that text again.
        return Ending(watch, result.Terminal, result.Reason, sawChange: result.Changes > 0);
    }

    /// <param name="endedBy">The outside end this ending reports, read once
    /// by the caller that acted on it; null for an ending of its own.</param>
    private static CorrectionWatchEnded Ending(
        Watch watch, string terminal, string? reason, bool sawChange = false, CorrectionWatchEnd? endedBy = null) =>
        new(watch.Id, terminal, reason, endedBy, watch.Revisions, sawChange);

    /// <summary>How an ending reads in the log: words the log may carry.</summary>
    private static string Describe(CorrectionWatchEnded ended) => ended switch
    {
        { EndedBy: { } why } => Describe(why),
        { Reason: ReasonOtherApplication } => "focus moved to another application",
        { Reason: ReasonTargetUnknown } => "the application it was pasted into is unknown",
        { Revisions: > 0 } => "settled on a typed edit",
        _ => ended.Label,
    };

    /// <summary>Tell every listener; one that throws is logged by type and
    /// stops neither the watch nor the listeners after it.</summary>
    private void Raise<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception error)
            {
                _log.LogWarning("A correction watch listener failed ({Type})", error.GetType().Name);
            }
        }
    }

    private static string Describe(CorrectionWatchEnd why) => why switch
    {
        CorrectionWatchEnd.NextDictation => "the next dictation began",
        CorrectionWatchEnd.Paused => "dictation was paused",
        CorrectionWatchEnd.Closed => "the window closed",
        CorrectionWatchEnd.SwitchedOff => "learning was switched off",
        CorrectionWatchEnd.NoticesCleared => "the vocabulary notices were cleared",
        _ => why.ToString(),
    };

    /// <summary>One watch's id, its token, and why it was ended.</summary>
    private sealed class Watch
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _cancel = new();
        private CorrectionWatchEnd? _endedBy;

        /// <summary>Random, and the dictation id its learning uses.</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Revisions handed to learning; touched only by the thread
        /// that runs the watch.</summary>
        public int Revisions { get; set; }

        public Task Completion { get; set; } = Task.CompletedTask;

        public CancellationToken Token => _cancel.Token;

        /// <summary>Why it was ended from outside — the first reason given —
        /// or null.</summary>
        public CorrectionWatchEnd? EndedBy
        {
            get
            {
                lock (_gate)
                {
                    return _endedBy;
                }
            }
        }

        public void PublishIfActive(Action publish)
        {
            lock (_gate)
            {
                if (_endedBy is null) publish();
            }
        }

        public void End(CorrectionWatchEnd why)
        {
            lock (_gate)
            {
                _endedBy ??= why;
            }

            _cancel.Cancel();
        }
    }
}
