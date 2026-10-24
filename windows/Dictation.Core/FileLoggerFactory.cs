using System.Text;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

/// <summary>
/// The Windows app's diagnostic log: one text file,
/// <c>%LOCALAPPDATA%\Dictation\logs\dictation.log</c>, with one previous
/// generation kept beside it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the app needs one.</b> Every inherited class — the worker host, the
/// worker process, the session, the microphone tap — reports what went wrong
/// through <see cref="ILogger"/>, and the entry point used to hand all of them
/// the null logger. A missing Python package, a model in the wrong format and
/// a worker that crashed then looked identical and left nothing behind.
/// </para>
/// <para>
/// <b>What it keeps out.</b> Information and above only. The inherited classes
/// log lengths, timings and outcomes at those levels, not transcript text; the
/// speech worker's own stderr, which can name words that were spoken, is
/// recorded only while the model loads (see <see cref="DictationWorker"/>).
/// </para>
/// <para>
/// No logging package: the abstractions are all the tree references, and a
/// line-per-event writer behind a lock is all a desktop preview needs.
/// </para>
/// </remarks>
public sealed class FileLoggerFactory : ILoggerFactory
{
    /// <summary>The live log's file name.</summary>
    public const string FileName = "dictation.log";

    /// <summary>The previous generation's file name.</summary>
    public const string PreviousFileName = "dictation.1.log";

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _previousPath;
    private readonly long _maxBytes;
    private readonly LogLevel _minimum;
    private readonly Func<DateTimeOffset> _clock;
    private StreamWriter? _writer;

    private FileLoggerFactory(
        string directory, LogLevel minimum, long maxBytes, Func<DateTimeOffset> clock)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
        _previousPath = Path.Combine(directory, PreviousFileName);
        _minimum = minimum;
        _maxBytes = maxBytes;
        _clock = clock;
        _writer = Open();
    }

    /// <summary>The live log file.</summary>
    public string FilePath => _path;

    /// <summary>
    /// A factory writing under <paramref name="directory"/>, or null when the
    /// directory cannot be created or the file cannot be opened — the app then
    /// runs with no log rather than not at all.
    /// </summary>
    public static FileLoggerFactory? TryCreate(
        string directory,
        out Exception? error,
        LogLevel minimum = LogLevel.Information,
        long maxBytes = 4 * 1024 * 1024,
        Func<DateTimeOffset>? clock = null)
    {
        try
        {
            error = null;
            return new FileLoggerFactory(directory, minimum, maxBytes, clock ?? (() => DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            error = ex;
            return null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>Other providers are not supported; this factory is the sink.</summary>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open()
    {
        if (File.Exists(_path) && new FileInfo(_path).Length >= _maxBytes)
        {
            try
            {
                File.Move(_path, _previousPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The previous generation is held open by another program or
                // is not a file. Keep appending to the live file past the cap
                // rather than lose the log; the next full line tries again.
            }
        }

        var stream = new FileStream(
            _path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    private bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimum;

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(_clock().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(Abbreviation(level))
            .Append(' ')
            .Append(category)
            .Append(": ")
            .Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_sync)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line.ToString());
                if (_writer.BaseStream.Length >= _maxBytes)
                {
                    // Detach before reopening. If Open throws, later calls
                    // must find no writer and return, not a disposed one that
                    // throws ObjectDisposedException out of every ILogger.Log.
                    var full = _writer;
                    _writer = null;
                    full.Dispose();
                    _writer = Open();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk or a file someone locked must not take the app
                // down through its own diagnostics.
            }
        }
    }

    private static string Abbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (owner.IsEnabled(logLevel))
            {
                owner.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}

/// <summary>
/// What the Windows app does with an exception nothing else caught: log it,
/// and keep the window alive unless there is no window yet or the process is
/// no longer safe to run.
/// </summary>
public static class UnhandledExceptions
{
    /// <summary>
    /// Whether the UI may carry on after <paramref name="exception"/>: an
    /// exception out of a button handler is fatal to WPF by default even
    /// though the interaction that raised it is over. The three that mean the
    /// process itself is damaged are not survivable.
    /// </summary>
    public static bool CanContinue(Exception exception) => exception switch
    {
        OutOfMemoryException => false,
        StackOverflowException => false,
        AccessViolationException => false,
        _ => true,
    };

    /// <summary><c>Application.DispatcherUnhandledException</c>: returns the
    /// value for <c>Handled</c>.</summary>
    /// <param name="log">Where the exception is written.</param>
    /// <param name="exception">What nothing else caught.</param>
    /// <param name="windowConstructed">
    /// Whether the window has been built and registered. Until it has, every
    /// exception is fatal: there is no interaction to survive, and a process
    /// kept alive with explicit shutdown and no window would hold the
    /// single-instance mutex, so every later launch would say Dictation is
    /// already open while nothing could be brought forward.
    /// </param>
    public static bool OnDispatcher(ILogger log, Exception exception, bool windowConstructed)
    {
        var carryOn = windowConstructed && CanContinue(exception);
        log.LogCritical(
            exception,
            "Unhandled exception on the UI thread; window={WindowConstructed} continuing={Continuing}",
            windowConstructed,
            carryOn);
        return carryOn;
    }

    /// <summary><c>AppDomain.UnhandledException</c>.</summary>
    public static void OnAppDomain(ILogger log, object exceptionObject, bool terminating) =>
        log.LogCritical(
            exceptionObject as Exception,
            "Unhandled exception; terminating={Terminating} ({Type})",
            terminating,
            exceptionObject?.GetType().FullName ?? "null");

    /// <summary><c>TaskScheduler.UnobservedTaskException</c>.</summary>
    public static void OnUnobservedTask(ILogger log, Exception exception) =>
        log.LogError(exception, "A background task failed and nothing observed it");
}
