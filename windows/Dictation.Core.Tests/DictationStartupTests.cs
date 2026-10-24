using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Core.Tests;

/// <summary>
/// One Dictation per session, as the app starts it: the second launch is
/// shown the running window, even while that window is still being built, and
/// a start that fails lets the next launch in instead of locking it out. The
/// named mutex and event are a <see cref="Session"/> here; the order the app
/// uses them in is the real class's.
/// </summary>
public sealed class DictationStartupTests
{
    /// <summary>The user session's named objects, shared by every launch in it.</summary>
    private sealed class Session
    {
        public bool MutexHeld { get; set; }

        /// <summary>The event's waiter, while one copy listens on it.</summary>
        public Action? Listener { get; set; }

        public bool SetThrows { get; set; }

        public bool ListenThrows { get; set; }

        public Launch Start(ILogger? log = null) => new(this, log);
    }

    /// <summary>One process: its mutex handle, its event and its UI thread.</summary>
    private sealed class Launch : IDisposable
    {
        public Launch(Session session, ILogger? log) =>
            Startup = new SingleInstanceStartup(
                new SessionMutex(session), new SessionEvent(session), Dispatcher.Post, log ?? NullLogger.Instance);

        public SingleInstanceStartup Startup { get; }

        /// <summary>The UI thread's queue: a request runs after what it is doing now.</summary>
        public DeferredDispatcher Dispatcher { get; } = new();

        public int Shown { get; private set; }

        public void ShowWindow() => Shown++;

        /// <summary>Build a window that comes up, as <c>App.OnStartup</c> does.</summary>
        public int? OpenWindow(Action? whileBuilding = null) => Startup.Open(
            () =>
            {
                whileBuilding?.Invoke();
                return ShowWindow;
            },
            sentence => Assert.Fail($"A start that worked told the user: {sentence}"));

        public void Dispose() => Startup.Dispose();
    }

    private sealed class SessionMutex(Session session) : IInstanceMutex
    {
        private bool _holds;

        public bool TryTake()
        {
            if (session.MutexHeld)
            {
                return false;
            }

            session.MutexHeld = _holds = true;
            return true;
        }

        public void Dispose()
        {
            if (_holds)
            {
                _holds = false;
                session.MutexHeld = false;
            }
        }
    }

    private sealed class SessionEvent(Session session) : IShowEvent
    {
        public IDisposable Listen(Action onSet)
        {
            if (session.ListenThrows)
            {
                throw new UnauthorizedAccessException("the event refused");
            }

            session.Listener = onSet;
            return new Closing(() => session.Listener = null);
        }

        /// <summary>The waiter fires at once, as the thread pool would.</summary>
        public bool TrySet()
        {
            if (session.SetThrows)
            {
                throw new UnauthorizedAccessException("the event refused");
            }

            if (session.Listener is not { } listener)
            {
                return false;
            }

            listener();
            return true;
        }

        private sealed class Closing(Action close) : IDisposable
        {
            public void Dispose() => close();
        }
    }

    [Fact]
    public void A_second_launch_before_the_window_exists_is_shown_it_once_it_does_not_told_already_open()
    {
        // The double-click race: the second process starts while the first is
        // still building its window, which takes as long as loading the
        // vocabulary and the settings and starting the worker.
        var session = new Session();
        using var first = session.Start();
        Assert.Equal(InstanceClaim.First, first.Startup.Claim());

        using var second = session.Start();
        Assert.Equal(InstanceClaim.BroughtForward, second.Startup.Claim());

        first.Dispatcher.Drain();
        Assert.Equal(0, first.Shown);

        Assert.Null(first.OpenWindow());
        Assert.Equal(1, first.Shown);

        first.Dispatcher.Drain();
        Assert.Equal(1, first.Shown);
    }

    [Fact]
    public void A_request_posted_while_the_window_is_built_shows_it_once_after()
    {
        // The same race, ordered as the dispatcher orders it: the request is
        // posted during the build and runs after startup returns.
        var session = new Session();
        using var first = session.Start();
        first.Startup.Claim();
        using var second = session.Start();

        first.OpenWindow(whileBuilding: () =>
            Assert.Equal(InstanceClaim.BroughtForward, second.Startup.Claim()));
        Assert.Equal(0, first.Shown);

        first.Dispatcher.Drain();
        Assert.Equal(1, first.Shown);
    }

    [Fact]
    public void A_second_launch_after_the_window_exists_brings_it_forward_each_time()
    {
        var session = new Session();
        using var first = session.Start();
        first.Startup.Claim();
        first.OpenWindow();

        for (var i = 1; i <= 2; i++)
        {
            using var again = session.Start();
            Assert.Equal(InstanceClaim.BroughtForward, again.Startup.Claim());
            first.Dispatcher.Drain();
            Assert.Equal(i, first.Shown);
        }
    }

    [Fact]
    public void The_window_counts_as_built_only_between_registering_and_letting_go()
    {
        // What App's dispatcher handler reads: until this is true, an
        // exception that escapes to it is fatal.
        var session = new Session();
        var launch = session.Start();
        launch.Startup.Claim();
        Assert.False(launch.Startup.HasWindow);

        launch.OpenWindow(whileBuilding: () => Assert.False(launch.Startup.HasWindow));
        Assert.True(launch.Startup.HasWindow);

        launch.Dispose();
        Assert.False(launch.Startup.HasWindow);

        using var failed = session.Start();
        failed.Startup.Claim();
        failed.Startup.Open(() => throw new InvalidOperationException("no window"), _ => { });
        Assert.False(failed.Startup.HasWindow);
    }

    [Fact]
    public void A_copy_with_nothing_to_signal_is_reported_as_already_open()
    {
        // The mutex is held by a copy that never created the event.
        var session = new Session { MutexHeld = true };
        using var launch = session.Start();

        Assert.Equal(InstanceClaim.AlreadyOpen, launch.Startup.Claim());
    }

    [Fact]
    public void An_event_that_cannot_be_reached_is_reported_as_already_open_not_thrown()
    {
        var session = new Session();
        using var first = session.Start();
        first.Startup.Claim();
        session.SetThrows = true;
        using var second = session.Start();

        Assert.Equal(InstanceClaim.AlreadyOpen, second.Startup.Claim());
    }

    [Fact]
    public void An_event_that_cannot_be_created_costs_the_second_launch_not_this_start()
    {
        var session = new Session { ListenThrows = true };
        using var first = session.Start();

        Assert.Equal(InstanceClaim.First, first.Startup.Claim());
        Assert.True(session.MutexHeld);
    }

    [Fact]
    public void Exiting_lets_go_of_the_mutex_and_the_event()
    {
        var session = new Session();
        using (var first = session.Start())
        {
            first.Startup.Claim();
            first.OpenWindow();
        }

        Assert.False(session.MutexHeld);
        Assert.Null(session.Listener);
        using var next = session.Start();
        Assert.Equal(InstanceClaim.First, next.Startup.Claim());
    }

    [Fact]
    public void A_request_that_lands_after_exit_does_nothing()
    {
        var session = new Session();
        var first = session.Start();
        first.Startup.Claim();
        var listener = session.Listener!;
        first.Dispose();

        listener();
        first.Dispatcher.Drain();
        first.OpenWindow();

        Assert.Equal(0, first.Shown);
    }

    // ─── The executable's manifest ──────────────────────────────────────

    [Fact]
    public void The_app_declares_per_monitor_dpi_long_paths_and_no_elevation()
    {
        // A source-tree check: what Windows reads from Dictation.exe cannot be
        // observed here, but the file the build embeds can.
        var app = Path.Combine(DictationOverridesTests.WindowsRoot(), "Dictation.App");
        var project = XDocument.Load(Path.Combine(app, "Dictation.App.csproj"));
        var named = project.Descendants("ApplicationManifest").Select(e => e.Value.Trim()).Single();
        var manifest = XDocument.Load(Path.Combine(app, named));
        string? Setting(string name) =>
            manifest.Descendants().SingleOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

        Assert.Equal("app.manifest", named);
        Assert.StartsWith("PerMonitorV2", Setting("dpiAwareness"), StringComparison.Ordinal);
        Assert.Equal("True/PM", Setting("dpiAware"));
        Assert.Equal("true", Setting("longPathAware"));
        var level = manifest.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel");
        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
    }

    // ─── A start that fails ─────────────────────────────────────────────

    [Fact]
    public void A_start_that_fails_after_the_mutex_lets_go_says_so_and_exits_non_zero()
    {
        var session = new Session();
        var log = new LinesLogger();
        using var first = session.Start(log);
        first.Startup.Claim();
        string? told = null;
        InstanceClaim? relaunch = null;

        var code = first.Startup.Open(
            () => throw new InvalidOperationException("the window would not load"),
            sentence =>
            {
                told = sentence;
                // The user opens Dictation again while the message is up.
                using var again = session.Start();
                relaunch = again.Startup.Claim();
            });

        Assert.Equal(SingleInstanceStartup.StartupFailedExitCode, code);
        Assert.NotEqual(0, code);
        Assert.Equal(DictationCopy.CouldNotStart, told);
        Assert.Equal(InstanceClaim.First, relaunch);
        Assert.False(session.MutexHeld);
        Assert.Null(session.Listener);
        Assert.Contains(log.Lines, l => l.Contains("could not start", StringComparison.Ordinal)
                                        && l.Contains("the window would not load", StringComparison.Ordinal));
    }

    [Fact]
    public void A_process_that_is_no_longer_safe_to_run_is_not_kept_up_for_a_message()
    {
        // Out of memory is left to the dispatcher handler, which ends the
        // process; the system frees the mutex with it.
        var session = new Session();
        using var first = session.Start();
        first.Startup.Claim();

        Assert.Throws<OutOfMemoryException>(() => first.Startup.Open(
            () => throw new OutOfMemoryException(),
            sentence => Assert.Fail($"told the user: {sentence}")));
    }

    [Fact]
    public void The_failure_sentence_names_no_path_or_exception()
    {
        Assert.DoesNotContain("\\", DictationCopy.CouldNotStart, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", DictationCopy.CouldNotStart, StringComparison.Ordinal);
        Assert.DoesNotContain(DictationCopy.OpenLogFolder, DictationCopy.CouldNotStart, StringComparison.Ordinal);
    }

    /// <summary>Each line the class logs, with its exception.</summary>
    private sealed class LinesLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel} {formatter(state, exception)} {exception?.Message}");
    }
}
