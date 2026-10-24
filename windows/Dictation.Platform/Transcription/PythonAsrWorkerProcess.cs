using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Dictation.Speech;
using Microsoft.Extensions.Logging;

namespace Dictation.Platform.Transcription;

/// <summary>
/// Where the speech worker lives, as the composition root resolved it.
/// </summary>
/// <remarks>
/// No environment lookups here. Dictation.App resolves its
/// <c>DICTATION_PYTHON</c>, <c>DICTATION_RUNTIME</c> and
/// <c>DICTATION_MODEL_DIR</c> overrides once (Dictation.Core's
/// <c>DictationWorkerPlan</c>) and passes the answer in; a second set of
/// overrides read here would be one nothing documents and the app never
/// consults.
/// </remarks>
/// <param name="PythonExecutable">The bundled interpreter.</param>
/// <param name="WorkingDirectory">
/// The directory that holds the <c>transcription_worker</c> package, and the
/// worker's working directory.
/// </param>
/// <param name="ModuleName">The worker module, run as the script it names
/// under <paramref name="WorkingDirectory"/>. Overridden by tests only.</param>
public sealed record AsrWorkerOptions(
    string PythonExecutable,
    string WorkingDirectory,
    string ModuleName = "transcription_worker.asr_server",
    IReadOnlyDictionary<string, string>? WorkerEnvironment = null)
{
    /// <summary>
    /// The file <see cref="ModuleName"/> names:
    /// <c>transcription_worker.asr_server</c> is
    /// <c>transcription_worker/asr_server.py</c> under the working directory.
    /// </summary>
    public string ScriptPath => Path.Combine(
        WorkingDirectory,
        ModuleName.Replace('.', Path.DirectorySeparatorChar) + ".py");
}

/// <summary>
/// workflows P-04 — the Windows half of the recognizer bridge: one Python child
/// process and a dedicated framed-reply pipe.
///
/// <para>
/// Everything that decides anything lives in
/// <see cref="AsrWorkerTranscriber"/>, where it is provable on any machine.
/// What is here is what genuinely needs a Windows box to be believed: an
/// interpreter path, a spawn, and the diagnostic pumps.
/// </para>
///
/// <para>
/// The pumps are not optional. The pipe buffer is around 4 KB, and a worker
/// loading a Whisper model writes considerably more than that before it
/// answers — a host that does not drain it deadlocks the child mid-log and
/// looks exactly like a model that takes forever to load. Native QNN stdout is
/// diagnostic output, never protocol: replies use a current-user-only named
/// pipe named by <see cref="ProtocolPipeEnvironmentVariable"/>. This is stronger
/// than redirecting Python's <c>sys.stdout</c>; a separately linked native CRT
/// can cache the process stdout handle before Python code runs.
/// </para>
/// </summary>
public sealed class PythonAsrWorkerProcess : IAsrWorkerProcess
{
    internal const string ProtocolPipeEnvironmentVariable =
        "DICTATION_ASR_PROTOCOL_PIPE";

    private readonly AsrWorkerOptions _options;
    private readonly ILogger<PythonAsrWorkerProcess> _logger;
    private readonly object _sync = new();

    private Process? _process;
    private NamedPipeServerStream? _protocolOutput;
    private Task? _protocolConnection;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private CancellationTokenSource? _stderrCts;

    private int _stderrLevel = (int)LogLevel.Debug;

    /// <summary>
    /// The level the worker's stderr lines are logged at. Debug by default.
    /// </summary>
    /// <remarks>
    /// Dictation raises it to Information while the model loads, so an import
    /// error or a refused model reaches the log file with its traceback, and
    /// lowers it again once the load is over: after that the worker's
    /// diagnostics can name words that were just spoken (applied aliases,
    /// restored casing), and the log file does not keep speech.
    /// </remarks>
    public LogLevel StderrLogLevel
    {
        get => (LogLevel)Volatile.Read(ref _stderrLevel);
        set => Volatile.Write(ref _stderrLevel, (int)value);
    }

    public PythonAsrWorkerProcess(
        AsrWorkerOptions options, ILogger<PythonAsrWorkerProcess> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsAlive
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public Stream StandardIn
    {
        get
        {
            lock (_sync)
            {
                return _process?.StandardInput.BaseStream
                    ?? throw new InvalidOperationException("speech worker not started");
            }
        }
    }

    public Stream StandardOut
    {
        get
        {
            lock (_sync)
            {
                return _protocolOutput
                    ?? throw new InvalidOperationException("speech worker not started");
            }
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                return _protocolConnection ?? Task.CompletedTask;
            }

            DisposeProcessUnderLock();

            if (!File.Exists(_options.PythonExecutable))
            {
                // Naming the path here is the difference between "speech is off
                // on this machine" and an hour spent on a machine that never
                // had an interpreter to begin with.
                throw new FileNotFoundException(
                    "No Python interpreter for the speech worker.",
                    _options.PythonExecutable);
            }

            var pipeName = NewProtocolPipeName();
            var protocolOutput = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                transmissionMode: PipeTransmissionMode.Byte,
                options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var startInfo = CreateStartInfo(_options, $@"\\.\pipe\{pipeName}");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.Exited += (_, _) =>
                _logger.LogWarning("Speech worker exited (code {Code})", SafeExitCode(process));

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Speech worker would not start: {Exe} -I -u {Script} (cwd={Cwd})",
                    _options.PythonExecutable,
                    _options.ScriptPath,
                    _options.WorkingDirectory);

                protocolOutput.Dispose();
                process.Dispose();
                throw;
            }

            _logger.LogInformation(
                "Speech worker started: PID {TargetPid} ({Exe} -I -u {Script})",
                process.Id,
                _options.PythonExecutable,
                _options.ScriptPath);

            _process = process;
            _protocolOutput = protocolOutput;
            _stderrCts = new CancellationTokenSource();
            _stdoutPump = Task.Run(
                () => DrainStdoutAsync(process, _stderrCts.Token),
                _stderrCts.Token);
            _stderrPump = Task.Run(() => DrainStderrAsync(process, _stderrCts.Token), _stderrCts.Token);
            _protocolConnection = WaitForProtocolConnectionAsync(
                protocolOutput, process, ct);

            return _protocolConnection;
        }
    }

    /// <summary>
    /// The name of the pipe the worker writes protocol frames to, unique per
    /// launch: <c>dictation-asr-&lt;process id&gt;-&lt;32 hex digits&gt;</c>. Any
    /// process can list the pipes on a machine, so the name says which app
    /// owns it and nothing else.
    /// </summary>
    internal static string NewProtocolPipeName() =>
        $"dictation-asr-{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <summary>
    /// The launch: the bundled interpreter in isolated mode running the
    /// worker's script by path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Isolated, because the interpreter is ours.</b> <c>-I</c> ignores
    /// every <c>PYTHON*</c> variable (<c>PYTHONPATH</c>, <c>PYTHONHOME</c>,
    /// <c>PYTHONSTARTUP</c>) and the user's site-packages. Without it a
    /// user-level <c>PYTHONPATH</c> — which Windows hands to apps started from
    /// Explorer — sorts ahead of the bundled site-packages, so a stray numpy or
    /// ctranslate2 silently replaced the bundled one and the only symptom was a
    /// model that would not load.
    /// </para>
    /// <para>
    /// <b>By path, not <c>-m</c>.</b> Isolated mode also stops Python putting
    /// the working directory on <c>sys.path</c>, which is what <c>-m</c>
    /// relied on. The script's own <c>__main__</c> guard puts the runtime
    /// directory there instead, and the live module is <c>__main__</c> either
    /// way. <c>-u</c> replaces <c>PYTHONUNBUFFERED</c>, which <c>-I</c>
    /// ignores. The <c>DICTATION_*</c> and offline variables the worker reads
    /// are not <c>PYTHON*</c> variables and still arrive.
    /// </para>
    /// <para>
    /// <b><c>-B</c>, because <c>-I</c> also drops <c>PYTHONDONTWRITEBYTECODE</c>.</b>
    /// A package cross-built on Linux carries no worker bytecode, so without it
    /// the first launch writes <c>__pycache__</c> into the install directory.
    /// </para>
    /// </remarks>
    internal static ProcessStartInfo CreateStartInfo(AsrWorkerOptions options, string protocolPipePath)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.PythonExecutable,
            WorkingDirectory = options.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add("-B");
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(options.ScriptPath);

        ApplyWorkerEnvironment(startInfo, options.WorkerEnvironment);
        startInfo.Environment[ProtocolPipeEnvironmentVariable] = protocolPipePath;
        return startInfo;
    }

    /// <summary>Merge host-resolved worker variables (engine pin, offline
    /// flags, thread caps) into the child's environment without making this
    /// infrastructure layer depend on a specific model catalog.</summary>
    internal static void ApplyWorkerEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (environment is null) return;
        foreach (var pair in environment)
            startInfo.Environment[pair.Key] = pair.Value;
    }

    public async Task StopAsync(TimeSpan grace, CancellationToken ct)
    {
        Process? process;
        NamedPipeServerStream? protocolOutput;
        Task? stdoutPump;
        Task? stderrPump;
        CancellationTokenSource? pumpCts;

        lock (_sync)
        {
            process = _process;
            protocolOutput = _protocolOutput;
            stdoutPump = _stdoutPump;
            stderrPump = _stderrPump;
            pumpCts = _stderrCts;
            _process = null;
            _protocolOutput = null;
            _protocolConnection = null;
            _stdoutPump = null;
            _stderrPump = null;
            _stderrCts = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    // EOF on stdin is the worker's other shutdown signal, and
                    // the one that still works when it never answered ours.
                    process.StandardInput.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Closing speech worker stdin failed");
                }

                try
                {
                    using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    graceCts.CancelAfter(grace);
                    await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Speech worker did not exit within {Grace}; killing it", grace);

                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Killing the speech worker failed");
                    }
                }
            }
        }
        finally
        {
            try { pumpCts?.Cancel(); } catch { /* shutting down */ }

            if (stdoutPump is not null)
            {
                try { await stdoutPump.ConfigureAwait(false); } catch { /* shutting down */ }
            }
            if (stderrPump is not null)
            {
                try { await stderrPump.ConfigureAwait(false); } catch { /* shutting down */ }
            }

            pumpCts?.Dispose();
            try { protocolOutput?.Dispose(); } catch { /* shutting down */ }
            try { process.Dispose(); } catch { /* shutting down */ }
        }
    }

    public async ValueTask DisposeAsync() =>
        await StopAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// The synchronous close, for the container dispose at app exit.
    /// </summary>
    /// <remarks>
    /// No grace period and no await: this runs on the UI thread as the app is
    /// tearing down, and a Python child process that has stopped answering
    /// must not be able to hold the exit open. The graceful stop lives in
    /// <see cref="StopAsync"/> for callers that have somewhere to wait.
    /// </remarks>
    public void Dispose()
    {
        Process? process;
        NamedPipeServerStream? protocolOutput;
        CancellationTokenSource? stderrCts;

        lock (_sync)
        {
            process = _process;
            protocolOutput = _protocolOutput;
            stderrCts = _stderrCts;
            _process = null;
            _protocolOutput = null;
            _protocolConnection = null;
            _stdoutPump = null;
            _stderrPump = null;
            _stderrCts = null;
        }

        try { stderrCts?.Cancel(); } catch { /* shutting down */ }
        try { stderrCts?.Dispose(); } catch { /* shutting down */ }
        try { protocolOutput?.Dispose(); } catch { /* shutting down */ }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech worker did not stop cleanly on exit");
        }

        try { process.Dispose(); } catch { /* shutting down */ }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            using var reader = process.StandardError;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (line.Length > 0)
                {
                    _logger.Log(StderrLogLevel, "[asr] {Line}", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech worker stderr pump ended");
        }
    }

    private async Task WaitForProtocolConnectionAsync(
        NamedPipeServerStream protocolOutput,
        Process process,
        CancellationToken ct)
    {
        try
        {
            var connected = protocolOutput.WaitForConnectionAsync(ct);
            var exited = process.WaitForExitAsync(CancellationToken.None);
            var completed = await Task.WhenAny(connected, exited).ConfigureAwait(false);
            if (ReferenceEquals(completed, exited))
            {
                await exited.ConfigureAwait(false);
                throw new IOException(
                    $"speech worker exited before opening its reply pipe "
                    + $"(code {SafeExitCode(process)})");
            }

            await connected.ConfigureAwait(false);
            _logger.LogDebug("Speech worker dedicated reply pipe connected");
        }
        catch
        {
            try
            {
                await StopAsync(TimeSpan.Zero, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The original connection/start failure is the useful error.
            }
            throw;
        }
    }

    private async Task DrainStdoutAsync(Process process, CancellationToken ct)
    {
        try
        {
            using var reader = process.StandardOutput;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                if (line.Length > 0)
                {
                    _logger.LogDebug("[asr/native-stdout] {Line}", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Speech worker stdout pump ended");
        }
    }

    private void DisposeProcessUnderLock()
    {
        try { _stderrCts?.Cancel(); } catch { /* shutting down */ }
        try { _protocolOutput?.Dispose(); } catch { /* shutting down */ }
        try { _process?.Dispose(); } catch { /* shutting down */ }

        _process = null;
        _protocolOutput = null;
        _protocolConnection = null;
        _stderrCts?.Dispose();
        _stderrCts = null;
        _stdoutPump = null;
        _stderrPump = null;
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; } catch { return int.MinValue; }
    }
}
