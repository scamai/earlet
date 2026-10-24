using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>Knobs tests need to turn; the defaults are the product's.</summary>
public sealed record DictationControllerOptions
{
    /// <summary>How long a hold may listen: the recording limit and its
    /// countdown, the memory cap the microphone tap keeps behind it, and the
    /// silence stops (<see cref="DictationHoldPolicy"/>). Checked when the
    /// controller is made.</summary>
    public DictationHoldPolicy HoldPolicy { get; init; } = DictationHoldPolicy.Windows;

    /// <summary>How long a delivered paste settles before the clipboard is
    /// restored; null keeps <see cref="FocusedTextInsertion"/>'s default.</summary>
    public TimeSpan? PasteSettleDelay { get; init; }

    /// <summary>
    /// The longest a close waits for an insertion to put the user's clipboard
    /// back. An insertion finishes in well under a second (paste plus the
    /// settle delay); this only bounds a clipboard that will not open.
    /// </summary>
    public TimeSpan CloseWaitLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the loaded model may sit unused before it is unloaded. The
    /// macOS app's value: a few minutes of pause keeps
    /// it warm, a lunch break gives the memory back. The next press starts it
    /// again while the user speaks.
    /// </summary>
    public TimeSpan WorkerIdleUnload { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>How many restarts a stopped worker gets before the window
    /// asks the user to pause and enable. Reset by a dictation that reached
    /// the recogniser and came back.</summary>
    public int WorkerRestartAttempts { get; init; } = 3;

    /// <summary>The wait between two restart attempts that both failed.</summary>
    public TimeSpan WorkerRestartDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How the controller waits out <see cref="WorkerRestartDelay"/>
    /// before the next attempt. Tests hold it, to choose the moment a retry
    /// comes due.</summary>
    public Func<TimeSpan, CancellationToken, Task> RestartWait { get; init; } = Task.Delay;

    /// <summary>A monotonic clock: time since some fixed start.</summary>
    public Func<TimeSpan> Clock { get; init; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// How the press-time password-field check runs. The shipped value puts it
    /// on the thread pool: it is a cross-process UI Automation call, bounded
    /// but not free, and the dispatcher that runs the hold key must not wait
    /// for it. Tests pass a synchronous runner to keep a press to one tick.
    /// </summary>
    public Func<Func<SecureFieldState>, Task<SecureFieldState>> RunFieldCheck { get; init; } =
        check => Task.Run(check);

    /// <summary>
    /// How the password-field check just before the paste runs: on the
    /// thread pool too, for the same reason, while the session awaits it
    /// (DictationController.FieldCheck.cs). Its own knob so a test can hold
    /// the press-time check without holding this one.
    /// </summary>
    public Func<Func<SecureFieldState>, Task<SecureFieldState>> RunPasteFieldCheck { get; init; } =
        check => Task.Run(check);

    /// <summary>The folder the Open log folder button shows.</summary>
    public string LogDirectory { get; init; } = DictationPaths.ForCurrentUser().LogDirectory;

    /// <summary>The span observer's knobs for the correction watch: its own,
    /// with the 90-second window of LEARNING.md §7.</summary>
    public SpanObserverOptions Observer { get; init; } = CorrectionWatch.DefaultObserverOptions;

    /// <summary>The wall clock the correction watch observes and learns by.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How the correction watch waits between reads of the field.</summary>
    public Func<int, CancellationToken, Task> ObserverDelay { get; init; } = Task.Delay;

    /// <summary>The wall clock a recent result is stamped with (its time is
    /// shown in the window and the notification-area menu).</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    /// <summary>
    /// How Copy last text runs its clipboard write. The shipped value puts it
    /// on the thread pool, as the insertion's own clipboard calls run off the
    /// dispatcher; the answer comes back through the controller's post, and a
    /// press in between is refused. Tests pass a held or synchronous runner.
    /// </summary>
    public Func<Func<bool>, Task<bool>> RunClipboardCopy { get; init; } = write => Task.Run(write);

    /// <summary>
    /// How long Copy last text waits for the clipboard, on <see cref="Clock"/>,
    /// before it counts the copy as failed and lets the hold key record again.
    /// A write normally answers in milliseconds; five seconds is when Windows
    /// itself calls a window that has not answered hung.
    /// </summary>
    public TimeSpan ClipboardCopyDeadline { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a release waits for the live preview to stop before its
    /// final decode goes to the worker anyway (DICTATION_PREVIEW=1 only): the
    /// Mac's <c>previewStopSeconds</c>, 2 s. In real time, not on
    /// <see cref="Clock"/>: it bounds a wait on another thread.
    /// </summary>
    public TimeSpan PreviewStopLimit { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How the microphone-access watch reads Windows' microphone switch
    /// (<see cref="DictationMicrophoneWatch"/>). The shipped value puts it on
    /// the thread pool: the read walks the consent store in the registry, and
    /// the UI thread it would otherwise run on also runs the keyboard hook.
    /// The answer comes back through the controller's post. Tests pass a
    /// synchronous or held runner.
    /// </summary>
    public Func<Func<MicrophoneAccess>, Task<MicrophoneAccess>> RunMicrophoneProbe { get; init; } = read => Task.Run(read);

    /// <summary>Long takes are never lost: the spool, the faults that keep a
    /// take, the worker's health check and Escape's question
    /// (<see cref="DictationTakeOptions"/>, DictationController.Takes.cs).</summary>
    public DictationTakeOptions Takes { get; init; } = new();
}
