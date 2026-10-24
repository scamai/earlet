using System.Runtime.InteropServices;
using System.Windows;
using Dictation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.App;

/// <summary>
/// One Dictation per user session, living in the notification area. Closing
/// the window never ends it, dictation on or off, as on the Mac; the tray's
/// Quit does (<c>ShutdownMode</c> is explicit), as does signing out.
/// </summary>
public partial class App : Application
{
    /// <summary>Held for the process's lifetime: the one running copy.</summary>
    private const string MutexName = @"Local\ScamAI.Dictation";

    /// <summary>A second launch sets this to bring the running window forward.</summary>
    private const string ShowEventName = @"Local\ScamAI.Dictation.Show";

    /// <summary><c>ASFW_ANY</c>: any process may take the foreground next.</summary>
    private const int AnyProcess = -1;

    private SingleInstanceStartup? instance;
    private FileLoggerFactory? logs;
    private MainWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The offscreen render modes (DICTATION_HUD_RENDER_DIR,
        // DICTATION_WINDOW_RENDER_DIR) draw PNGs and exit: pictures, not the
        // app. Ahead of the log, so a render stays out of the user's
        // dictation.log, and of the single-instance claim, so it runs beside
        // a running copy.
        if (DictationRender.Requested(DictationEnvironment.Process) is { } render)
        {
            DictationRenderMode.Start(this, render);
            return;
        }

        // Logging first, and the last-resort handlers straight after it, so a
        // failure anywhere below leaves a line in
        // %LOCALAPPDATA%\Dictation\logs\dictation.log. A log directory that
        // cannot be created costs the log, not the app.
        logs = FileLoggerFactory.TryCreate(DictationPaths.ForCurrentUser().LogDirectory, out _);
        ILoggerFactory factory = logs is null ? NullLoggerFactory.Instance : logs;
        var log = factory.CreateLogger<App>();
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            UnhandledExceptions.OnAppDomain(log, ev.ExceptionObject, ev.IsTerminating);
        // Fatal until the window exists: a process with no window must end,
        // not live on holding the single-instance mutex.
        DispatcherUnhandledException += (_, ev) =>
            ev.Handled = UnhandledExceptions.OnDispatcher(log, ev.Exception, instance?.HasWindow == true);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            UnhandledExceptions.OnUnobservedTask(log, ev.Exception);
            ev.SetObserved();
        };
        // Which copy is running, before anything else can fail: the version,
        // the packager's build stamp and this executable's path tell the
        // installed copy apart from any other, as the Mac's launch line does.
        log.LogInformation(
            "{Launch}", DictationInstallation.Of(typeof(App).Assembly, Environment.ProcessPath).LaunchLine);

        // The show event is created with the mutex, before the window, so a
        // second launch during the slow part below is shown the window once
        // it exists instead of being told it is already open.
        instance = new SingleInstanceStartup(
            new NamedMutex(MutexName),
            new NamedShowEvent(ShowEventName),
            action => Dispatcher.InvokeAsync(action),
            log);
        switch (instance.Claim())
        {
            case InstanceClaim.BroughtForward:
                log.LogInformation("Dictation is already running; asked it to show its window");
                Shutdown();
                return;
            case InstanceClaim.AlreadyOpen:
                MessageBox.Show(DictationCopy.AlreadyOpen, "Earlet");
                Shutdown();
                return;
        }

        // Everything after the mutex fails as one: logged, both objects let
        // go, a sentence for the user, then exit code 1. Left to the dispatcher
        // handler, a failure here kept a windowless process holding the mutex.
        var failed = instance.Open(
            () =>
            {
                window = new MainWindow(factory, LaunchAtLoginEntry.IsLoginLaunch(e.Args));
                SessionEnding += (_, _) => window?.PrepareForSessionEnd();
                if (window.Startup.ShowWindow)
                {
                    window.Show();
                }
                else
                {
                    log.LogInformation(
                        "Started at sign-in with dictation on; the window stays in the notification area");
                }

                return window.ShowFromOutside;
            },
            sentence => MessageBox.Show(sentence, "Earlet", MessageBoxButton.OK, MessageBoxImage.Error));
        if (failed is { } code)
        {
            Shutdown(code);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        logs?.CreateLogger<App>().LogInformation("Dictation exiting (code {Code})", e.ApplicationExitCode);
        logs?.Dispose();
        base.OnExit(e);
    }

    // ─── Win32 objects behind SingleInstanceStartup ─────────────────────

    /// <summary>The session's named mutex.</summary>
    private sealed class NamedMutex(string name) : IInstanceMutex
    {
        private Mutex? mutex;
        private bool held;

        public bool TryTake()
        {
            mutex = new Mutex(initiallyOwned: true, name, out held);
            return held;
        }

        public void Dispose()
        {
            if (held)
            {
                held = false;
                try
                {
                    mutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Called off the thread that took it. Closing the last
                    // handle below frees the name all the same.
                }
            }

            mutex?.Dispose();
            mutex = null;
        }
    }

    /// <summary>The auto-reset event a second launch sets.</summary>
    private sealed class NamedShowEvent(string name) : IShowEvent
    {
        public IDisposable Listen(Action onSet)
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            try
            {
                var wait = ThreadPool.RegisterWaitForSingleObject(
                    signal, (_, _) => onSet(), null, Timeout.Infinite, executeOnlyOnce: false);
                return new Listening(signal, wait);
            }
            catch
            {
                signal.Dispose();
                throw;
            }
        }

        /// <summary>This launch came from the user, so it may pass its right
        /// to the foreground on first.</summary>
        public bool TrySet()
        {
            AllowSetForegroundWindow(AnyProcess);
            if (!EventWaitHandle.TryOpenExisting(name, out var signal))
            {
                return false;
            }

            using (signal)
            {
                return signal.Set();
            }
        }

        private sealed class Listening(EventWaitHandle signal, RegisteredWaitHandle wait) : IDisposable
        {
            public void Dispose()
            {
                wait.Unregister(null);
                signal.Dispose();
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
