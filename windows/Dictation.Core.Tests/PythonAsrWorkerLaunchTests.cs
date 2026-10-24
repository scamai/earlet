using System.Collections.Concurrent;
using Dictation.Platform.Transcription;
using Microsoft.Extensions.Logging;

namespace Dictation.Core.Tests;

/// <summary>
/// The speech worker runs on the bundled interpreter, isolated from the
/// user's Python environment.
/// </summary>
/// <remarks>
/// Not parallel with the rest of the suite: the end-to-end case sets
/// <c>PYTHONPATH</c> and <c>PYTHONHOME</c> on this process for the instant
/// the worker is spawned, which is how a user's environment reaches it.
/// </remarks>
[Collection(nameof(ProcessEnvironment))]
public sealed class PythonAsrWorkerLaunchTests
{
    [Fact]
    public void The_worker_runs_isolated_and_unbuffered_by_script_path()
    {
        var runtime = Path.Combine(Path.GetTempPath(), "dictation-runtime");
        var options = new AsrWorkerOptions(
            "python.exe", runtime,
            WorkerEnvironment: new Dictionary<string, string> { ["DICTATION_ASR_ENGINE"] = "faster" });

        var start = PythonAsrWorkerProcess.CreateStartInfo(options, "pipe-path");

        Assert.Equal(
            ["-I", "-B", "-u", Path.Combine(runtime, "transcription_worker", "asr_server.py")],
            start.ArgumentList);
        Assert.Equal(runtime, start.WorkingDirectory);
        Assert.Equal("faster", start.Environment["DICTATION_ASR_ENGINE"]);
        Assert.Equal("pipe-path", start.Environment["DICTATION_ASR_PROTOCOL_PIPE"]);
    }

    [Fact]
    public void The_protocol_pipe_is_named_for_this_app_and_unique_per_launch()
    {
        // Every process on the machine can list pipe names, so the name carries
        // the app's own name, this process and a fresh GUID: nothing else.
        var first = PythonAsrWorkerProcess.NewProtocolPipeName();
        var second = PythonAsrWorkerProcess.NewProtocolPipeName();

        Assert.Matches($"^dictation-asr-{Environment.ProcessId}-[0-9a-f]{{32}}$", first);
        Assert.NotEqual(first, second);
    }

    [PythonFact]
    public async Task The_worker_is_handed_the_pipe_named_for_this_app()
    {
        // Through StartAsync, the path the app takes: the stand-in worker
        // prints the pipe path it was given, which the process's pump logs.
        var root = Directory.CreateTempSubdirectory("dictation-pipe-").FullName;
        var log = new CapturingLogger();
        PythonAsrWorkerProcess? worker = null;
        try
        {
            var runtime = Path.Combine(root, "runtime");
            Directory.CreateDirectory(Path.Combine(runtime, "transcription_worker"));
            File.WriteAllText(
                Path.Combine(runtime, "transcription_worker", "asr_server.py"),
                "import os, sys\n"
                + "print('pipe ' + os.environ['DICTATION_ASR_PROTOCOL_PIPE'], flush=True)\n"
                + "sys.stdin.read()\n");

            worker = new PythonAsrWorkerProcess(new AsrWorkerOptions(PythonFactAttribute.Interpreter!, runtime), log);
            _ = worker.StartAsync(CancellationToken.None);

            var line = await log.WaitForAsync(
                l => l.StartsWith("[asr/native-stdout] pipe ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30));

            Assert.True(line is not null, "the worker never reported its pipe: " + string.Join(" | ", log.Lines));
            Assert.Matches(
                $@"pipe \\\\\.\\pipe\\dictation-asr-{Environment.ProcessId}-[0-9a-f]{{32}}$", line);
        }
        finally
        {
            worker?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [PythonFact]
    public async Task A_user_PYTHONPATH_or_PYTHONHOME_cannot_reach_the_worker()
    {
        // Through StartAsync with a real interpreter. The user's environment
        // carries a module that shadows a standard one and a PYTHONHOME that
        // points nowhere; either would stop the bundled worker if the launch
        // honoured it. The stand-in worker reports its flags on stdout, which
        // the process's own pump logs.
        var root = Directory.CreateTempSubdirectory("dictation-isolation-").FullName;
        var log = new CapturingLogger();
        var previousPath = Environment.GetEnvironmentVariable("PYTHONPATH");
        var previousHome = Environment.GetEnvironmentVariable("PYTHONHOME");
        PythonAsrWorkerProcess? worker = null;
        try
        {
            var runtime = Path.Combine(root, "runtime");
            var shadow = Path.Combine(root, "user-site");
            Directory.CreateDirectory(Path.Combine(runtime, "transcription_worker"));
            Directory.CreateDirectory(shadow);
            File.WriteAllText(Path.Combine(shadow, "json.py"), "raise ImportError('user PYTHONPATH shadowed json')\n");
            File.WriteAllText(
                Path.Combine(runtime, "transcription_worker", "asr_server.py"),
                "import json, sys\n"
                + "print('flags ' + json.dumps({'isolated': sys.flags.isolated, 'unbuffered': sys.stdout.write_through}))\n"
                + "sys.stdin.read()\n");

            worker = new PythonAsrWorkerProcess(
                new AsrWorkerOptions(PythonFactAttribute.Interpreter!, runtime), log);
            Environment.SetEnvironmentVariable("PYTHONPATH", shadow);
            Environment.SetEnvironmentVariable("PYTHONHOME", Path.Combine(root, "no-such-home"));
            _ = worker.StartAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("PYTHONPATH", previousPath);
            Environment.SetEnvironmentVariable("PYTHONHOME", previousHome);

            var line = await log.WaitForAsync(
                l => l.StartsWith("[asr/native-stdout] flags ", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30));

            Assert.True(line is not null, "the worker never reported its flags: " + string.Join(" | ", log.Lines));
            Assert.Contains("\"isolated\": 1", line, StringComparison.Ordinal);
            Assert.Contains("\"unbuffered\": true", line, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYTHONPATH", previousPath);
            Environment.SetEnvironmentVariable("PYTHONHOME", previousHome);
            worker?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class CapturingLogger : ILogger<PythonAsrWorkerProcess>
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Enqueue(formatter(state, exception));

        public async Task<string?> WaitForAsync(Func<string, bool> match, TimeSpan limit)
        {
            var deadline = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < deadline)
            {
                var hit = Lines.FirstOrDefault(match);
                if (hit is not null)
                {
                    return hit;
                }

                await Task.Delay(20);
            }

            return null;
        }
    }
}

/// <summary>Tests that change this process's environment variables.</summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>A fact that needs a Python 3 interpreter on PATH, and says so
/// when there is none instead of passing.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (Interpreter is null)
        {
            Skip = "needs python3 (or python) on PATH";
        }
    }

    public static string? Interpreter { get; } = Find();

    private static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "python.exe" } : ["python3", "python"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
