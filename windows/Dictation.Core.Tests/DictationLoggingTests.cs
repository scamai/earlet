using System.Collections.Concurrent;
using Dictation.Platform.Transcription;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

/// <summary>
/// What the Windows app leaves behind when something fails, and what it
/// does not.
/// </summary>
public sealed class DictationLoggingTests
{
    private static string TempDirectory() => Directory.CreateTempSubdirectory("dictation-logs-").FullName;

    [Fact]
    public void Information_and_above_reach_the_file_with_the_exception_debug_does_not()
    {
        var dir = TempDirectory();
        using (var logs = FileLoggerFactory.TryCreate(dir, out _)!)
        {
            var log = logs.CreateLogger("Test.Category");
            log.LogDebug("a debug line");
            log.LogInformation("an information line");
            log.LogError(new InvalidOperationException("the worker said no"), "an error line");
        }

        var text = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.DoesNotContain("a debug line", text, StringComparison.Ordinal);
        Assert.Contains("INF Test.Category: an information line", text, StringComparison.Ordinal);
        Assert.Contains("ERR Test.Category: an error line", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException: the worker said no", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_log_rolls_over_to_one_previous_generation()
    {
        var dir = TempDirectory();
        using (var logs = FileLoggerFactory.TryCreate(dir, out _, maxBytes: 200)!)
        {
            var log = logs.CreateLogger("Roll");
            for (var i = 0; i < 20; i++)
            {
                log.LogInformation("line {Index} with some padding to fill the file", i);
            }
        }

        Assert.True(File.Exists(Path.Combine(dir, FileLoggerFactory.PreviousFileName)));
        Assert.True(new FileInfo(Path.Combine(dir, FileLoggerFactory.FileName)).Length < 400);
        Assert.Contains("line 19", File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_directory_that_cannot_exist_costs_the_log_not_the_app()
    {
        var dir = TempDirectory();
        var file = Path.Combine(dir, "a-file");
        File.WriteAllText(file, "x");

        var logs = FileLoggerFactory.TryCreate(Path.Combine(file, "logs"), out var error);

        Assert.Null(logs);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_previous_generation_that_cannot_be_replaced_keeps_the_live_log_going()
    {
        // dictation.1.log is a directory, so every rollover's move fails, as
        // it does on Windows while another program holds the old generation
        // open. The failed rollover used to leave a disposed writer behind,
        // and every later line threw ObjectDisposedException out of Log.
        var dir = TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir, FileLoggerFactory.PreviousFileName));
        using (var logs = FileLoggerFactory.TryCreate(dir, out _, maxBytes: 64)!)
        {
            var log = logs.CreateLogger("Roll");
            log.LogInformation("first line, long enough on its own to take the log past its cap");
            log.LogInformation("second line");
            log.LogInformation("third line");
        }

        var text = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.Contains("first line", text, StringComparison.Ordinal);
        Assert.Contains("second line", text, StringComparison.Ordinal);
        Assert.Contains("third line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_log_whose_previous_generation_cannot_be_replaced_still_opens_at_launch()
    {
        var dir = TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir, FileLoggerFactory.PreviousFileName));
        var live = Path.Combine(dir, FileLoggerFactory.FileName);
        File.WriteAllText(live, new string('x', 100) + "\n");

        using (var logs = FileLoggerFactory.TryCreate(dir, out var error, maxBytes: 64))
        {
            Assert.Null(error);
            Assert.NotNull(logs);
            logs.CreateLogger("Launch").LogInformation("the next launch");
        }

        Assert.Contains("the next launch", File.ReadAllText(live), StringComparison.Ordinal);
    }

    [WriteProtectionFact]
    public void A_log_that_cannot_be_reopened_after_a_rollover_goes_quiet_instead_of_throwing()
    {
        // Neither the move nor the reopen can succeed: the previous generation
        // is a directory and the live file has turned read-only. The writer
        // already open still works, so the first line lands and starts the
        // rollover; the log is then gone for the session, and the app is not.
        var dir = TempDirectory();
        Directory.CreateDirectory(Path.Combine(dir, FileLoggerFactory.PreviousFileName));
        var live = Path.Combine(dir, FileLoggerFactory.FileName);
        using var logs = FileLoggerFactory.TryCreate(dir, out _, maxBytes: 64)!;
        var log = logs.CreateLogger("Roll");
        File.SetAttributes(live, FileAttributes.ReadOnly);
        try
        {
            // The precondition, checked rather than assumed: a new writer is refused.
            Assert.Throws<UnauthorizedAccessException>(() =>
                new FileStream(live, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)
                    .Dispose());

            log.LogInformation("first line, long enough on its own to take the log past its cap");
            log.LogInformation("second line");
            log.LogError(new InvalidOperationException("still no writer"), "third line");
        }
        finally
        {
            File.SetAttributes(live, FileAttributes.Normal);
        }

        var text = File.ReadAllText(live);
        Assert.Contains("first line", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_escaping_a_button_is_logged_and_survived()
    {
        var dir = TempDirectory();
        bool handled;
        using (var logs = FileLoggerFactory.TryCreate(dir, out _)!)
        {
            handled = UnhandledExceptions.OnDispatcher(
                logs.CreateLogger("App"), new InvalidOperationException("clicked into a wall"), windowConstructed: true);
        }

        Assert.True(handled);
        Assert.Contains("clicked into a wall", File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName)),
            StringComparison.Ordinal);
        Assert.False(UnhandledExceptions.CanContinue(new OutOfMemoryException()));
    }

    [Fact]
    public void An_exception_before_the_window_exists_is_logged_and_fatal()
    {
        // With explicit shutdown, surviving it left a process with no window
        // that held the single-instance mutex: every later launch said
        // "already open" and nothing came forward.
        var dir = TempDirectory();
        bool before;
        bool after;
        using (var logs = FileLoggerFactory.TryCreate(dir, out _)!)
        {
            var log = logs.CreateLogger("App");
            before = UnhandledExceptions.OnDispatcher(
                log, new InvalidOperationException("the window would not load"), windowConstructed: false);
            after = UnhandledExceptions.OnDispatcher(
                log, new InvalidOperationException("clicked into a wall"), windowConstructed: true);
        }

        Assert.False(before);
        Assert.True(after);
        var text = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.Contains("window=False continuing=False", text, StringComparison.Ordinal);
        Assert.Contains("the window would not load", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_will_not_load_leaves_the_worker_s_reason_in_the_log()
    {
        // Through the controller with the real host: the refusal text the
        // worker sends back used to go to a null logger.
        var dir = TempDirectory();
        using var logs = FileLoggerFactory.TryCreate(dir, out _)!;
        using var rig = new Rig(logs: logs);
        rig.Platform.Worker.InitSucceeds = false;

        await rig.EnableAsync();

        var text = File.ReadAllText(Path.Combine(dir, FileLoggerFactory.FileName));
        Assert.Contains("no speech model generation", text, StringComparison.Ordinal);
        Assert.Contains("Enabling dictation: python=", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_startup_capture_ends_once_the_model_load_is_over_whatever_the_result()
    {
        using var loads = new Rig();
        await loads.EnableAsync();
        Assert.Equal(1, loads.Platform.StartupFinishedCalls);

        using var refuses = new Rig();
        refuses.Platform.Worker.InitSucceeds = false;
        await refuses.EnableAsync();
        Assert.Equal(1, refuses.Platform.StartupFinishedCalls);
    }

    [PythonFact]
    public async Task Worker_stderr_is_kept_while_the_model_loads_and_dropped_after()
    {
        // The real process class and a real interpreter. The first stderr line
        // stands for a load failure's traceback; the second for the worker's
        // per-dictation diagnostics, which can name spoken words.
        var root = Directory.CreateTempSubdirectory("dictation-stderr-").FullName;
        var log = new LevelLogger();
        PythonAsrWorkerProcess? worker = null;
        try
        {
            var runtime = Path.Combine(root, "runtime");
            Directory.CreateDirectory(Path.Combine(runtime, "transcription_worker"));
            File.WriteAllText(
                Path.Combine(runtime, "transcription_worker", "asr_server.py"),
                "import sys\n"
                + "print('loading the model', file=sys.stderr)\n"
                + "sys.stdin.readline()\n"
                + "print('aliases applied [a spoken word]', file=sys.stderr)\n"
                + "sys.stdin.read()\n");
            worker = new PythonAsrWorkerProcess(new AsrWorkerOptions(PythonFactAttribute.Interpreter!, runtime), log)
            {
                StderrLogLevel = LogLevel.Information,
            };
            _ = worker.StartAsync(CancellationToken.None);

            var loading = await log.WaitForAsync("[asr] loading the model");
            worker.StderrLogLevel = LogLevel.Debug;
            await worker.StandardIn.WriteAsync("\n"u8.ToArray());
            await worker.StandardIn.FlushAsync();
            var spoken = await log.WaitForAsync("[asr] aliases applied");

            Assert.Equal(LogLevel.Information, loading);
            Assert.Equal(LogLevel.Debug, spoken);
        }
        finally
        {
            worker?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class LevelLogger : ILogger<PythonAsrWorkerProcess>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _lines.Enqueue((logLevel, formatter(state, exception)));

        public async Task<LogLevel?> WaitForAsync(string prefix)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var (level, text) in _lines)
                {
                    if (text.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return level;
                    }
                }

                await Task.Delay(20);
            }

            return null;
        }
    }
}

/// <summary>A fact that needs a read-only file to refuse a new writer. Root on
/// Unix writes through the permission bits, so there it skips and says so
/// instead of passing without testing anything.</summary>
public sealed class WriteProtectionFactAttribute : FactAttribute
{
    public WriteProtectionFactAttribute()
    {
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            Skip = "root writes through a read-only file on Unix";
        }
    }
}
