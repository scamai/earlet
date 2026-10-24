using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>What <see cref="SingleInstanceStartup.Claim"/> found.</summary>
public enum InstanceClaim
{
    /// <summary>This process is the one Dictation in the session: start it.</summary>
    First,

    /// <summary>Another copy holds the session and was asked to show its
    /// window: exit without a word.</summary>
    BroughtForward,

    /// <summary>Another copy holds the session but had nothing to signal:
    /// say it is already open, then exit.</summary>
    AlreadyOpen,
}

/// <summary>The session-wide named mutex that makes a copy the running one.</summary>
public interface IInstanceMutex : IDisposable
{
    /// <summary>Create or open the mutex and take it: true when this process
    /// now holds it, false when another copy does. Disposing lets go of it.</summary>
    bool TryTake();
}

/// <summary>The named event a second launch sets to bring the running
/// copy's window forward.</summary>
public interface IShowEvent
{
    /// <summary>Create the event and call <paramref name="onSet"/>, on any
    /// thread, each time another launch sets it. Disposing the result stops
    /// listening and closes the event.</summary>
    IDisposable Listen(Action onSet);

    /// <summary>Set the running copy's event: false when there is none.</summary>
    bool TrySet();
}

/// <summary>
/// One Dictation per user session: the mutex, the event a second launch sets,
/// and a request to show the window that arrives before there is a window.
/// The Win32 objects are the app's; the order they are used in is here, so it
/// runs on a machine with no Windows in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the event comes straight after the mutex.</b> Building the window
/// loads the vocabulary and the settings and can start the speech worker,
/// which is long enough for a double-click, or a sign-in start racing a
/// manual one, to start a second process. That process finds the mutex taken
/// and looks for the event. The event used to be created only once the window
/// existed, so the second launch found none and said "already open" about a
/// window that was about to appear. Now the event exists from the moment the
/// mutex is held, and a request that arrives before the window is kept and
/// delivered when the window registers.
/// </para>
/// <para>
/// <b>Why a failed start lets go.</b> WPF hands an exception out of startup to
/// <c>DispatcherUnhandledException</c>, which marked it handled; with
/// explicit shutdown the process then lived on with no window, holding the
/// mutex and no event, and every later launch said "already open" until the
/// process was ended in Task Manager. A start that fails is now logged, lets
/// go of both objects, tells the user and exits non-zero.
/// </para>
/// <para>
/// <b>Threads.</b> The event's callback comes on a thread-pool thread and is
/// posted to the UI thread, where <see cref="Claim"/>,
/// <see cref="Open"/> and <see cref="Dispose"/> are also called, so the
/// pending request needs no lock.
/// </para>
/// </remarks>
public sealed class SingleInstanceStartup : IDisposable
{
    /// <summary>The exit code of a start that failed after the mutex was taken.</summary>
    public const int StartupFailedExitCode = 1;

    private readonly IInstanceMutex _mutex;
    private readonly IShowEvent _showEvent;
    private readonly Action<Action> _post;
    private readonly ILogger _log;
    private IDisposable? _listening;
    private Action? _show;
    private bool _pendingShow;
    private bool _disposed;

    /// <param name="mutex">The session's mutex.</param>
    /// <param name="showEvent">The event a second launch sets.</param>
    /// <param name="post">Runs an action on the UI thread, after whatever it
    /// is doing now: the dispatcher's <c>InvokeAsync</c>.</param>
    /// <param name="log">Where a start that failed, or a launch that could
    /// not reach the running copy, says why.</param>
    public SingleInstanceStartup(IInstanceMutex mutex, IShowEvent showEvent, Action<Action> post, ILogger log)
    {
        _mutex = mutex ?? throw new ArgumentNullException(nameof(mutex));
        _showEvent = showEvent ?? throw new ArgumentNullException(nameof(showEvent));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Whether the window has been built and registered by
    /// <see cref="Open"/>; false again once this copy lets go.</summary>
    public bool HasWindow => _show is not null;

    /// <summary>
    /// The first step of startup: take the mutex and listen for later
    /// launches, or, when another copy holds it, ask that copy to come forward.
    /// </summary>
    public InstanceClaim Claim()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_mutex.TryTake())
        {
            return AskRunningCopyToComeForward() ? InstanceClaim.BroughtForward : InstanceClaim.AlreadyOpen;
        }

        try
        {
            _listening = _showEvent.Listen(() => _post(ShowRequested));
        }
        catch (Exception error)
        {
            // Costs the second launch its way in, not this launch its start.
            _log.LogWarning(error, "A second launch will not be able to bring this window forward");
        }

        return InstanceClaim.First;
    }

    /// <summary>
    /// The rest of startup, once <see cref="Claim"/> has returned
    /// <see cref="InstanceClaim.First"/>: build the window, then register it
    /// so later launches can bring it forward.
    /// </summary>
    /// <param name="openWindow">Builds the window, shows it or leaves it in
    /// the notification area, and returns what brings it forward.</param>
    /// <param name="tellUser">Shows a sentence to the user and returns when
    /// they have read it: a message box.</param>
    /// <returns>Null while the app runs; the code to exit with when it could
    /// not start.</returns>
    public int? Open(Func<Action> openWindow, Action<string> tellUser)
    {
        ArgumentNullException.ThrowIfNull(openWindow);
        ArgumentNullException.ThrowIfNull(tellUser);
        try
        {
            WindowReady(openWindow());
            return null;
        }
        catch (Exception error) when (UnhandledExceptions.CanContinue(error))
        {
            _log.LogCritical(error, "Dictation could not start");
            // Let go before the message box, which waits for the user: a
            // launch made while it is up starts a fresh copy.
            Dispose();
            tellUser(DictationCopy.CouldNotStart);
            return StartupFailedExitCode;
        }
    }

    /// <summary>The window exists: <paramref name="show"/> brings it forward.
    /// A request that came while it was being built is delivered now.</summary>
    private void WindowReady(Action show)
    {
        ArgumentNullException.ThrowIfNull(show);
        if (_disposed)
        {
            return;
        }

        _show = show;
        if (_pendingShow)
        {
            _pendingShow = false;
            show();
        }
    }

    /// <summary>Stop listening and let go of the mutex.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _show = null;
        _pendingShow = false;
        _listening?.Dispose();
        _listening = null;
        _mutex.Dispose();
    }

    /// <summary>On the UI thread: show the window, or keep the request for it.</summary>
    private void ShowRequested()
    {
        if (_disposed)
        {
            return;
        }

        if (_show is { } show)
        {
            show();
        }
        else
        {
            _pendingShow = true;
        }
    }

    private bool AskRunningCopyToComeForward()
    {
        try
        {
            return _showEvent.TrySet();
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "Could not reach the running copy of Dictation");
            return false;
        }
    }
}
