using Dictation.Speech;

namespace Dictation.Core;

/// <summary>
/// Where the speech worker's interpreter, code and model are, and what the
/// Windows app asks it to load.
/// </summary>
/// <param name="PythonExecutable">The bundled (or overridden) interpreter.</param>
/// <param name="RuntimeDirectory">The directory holding <c>transcription_worker/</c>.</param>
/// <param name="ModelDirectory">The CTranslate2 Whisper model directory.</param>
public sealed record DictationWorkerPlan(
    string PythonExecutable,
    string RuntimeDirectory,
    string ModelDirectory)
{
    /// <summary>
    /// The variables the worker process gets on top of its inherited
    /// environment: the portable engine, no Hugging Face network access, and
    /// thread caps sized for a laptop that is also running the user's editor.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment { get; } =
        new Dictionary<string, string>
        {
            ["DICTATION_ASR_ENGINE"] = "faster",
            ["HF_HUB_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["OMP_NUM_THREADS"] = "4",
            ["OPENBLAS_NUM_THREADS"] = "1",
            ["MKL_NUM_THREADS"] = "1",
        };

    /// <summary>
    /// The recognition language sent with every transcription, as a Whisper
    /// code, or null to let the recogniser detect it (the default). See
    /// <see cref="SpeechLanguages"/>.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// The fixed part of a decode's deadline: a hold's decode may take this
    /// plus one second per second of audio, and the one retry after a worker
    /// that died or missed it this plus two
    /// (<see cref="AsrWorkerTranscriber.RetryTimeoutFor"/>). Thirty seconds,
    /// the shipped value; <see cref="DictationTakeOptions.DecodeDeadline"/>.
    /// </summary>
    public TimeSpan TranscribeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The init and decode knobs: the portable Whisper engine (the worker calls
    /// it <c>mac</c>; on Windows it is faster-whisper on the CPU), the local
    /// model directory for both weights and tokenizer, the chosen
    /// <see cref="Language"/> (automatic unless the user picked one), and no
    /// model downloads.
    /// </summary>
    /// <remarks>
    /// <c>AllowModelDownload: false</c> is what turns on the worker's own
    /// offline check. Without it the worker defaults to allowing downloads,
    /// and the only thing keeping a model load off the network is the
    /// <c>HF_HUB_OFFLINE</c> variable above.
    /// </remarks>
    public TranscriberOptions Transcriber => new(
        InitTimeout: TimeSpan.FromSeconds(90),
        TranscribeTimeout: TranscribeTimeout,
        BeamSize: 4,
        ModelDirectory: ModelDirectory,
        HuggingFaceId: ModelDirectory,
        UseQnn: false,
        Language: Language,
        Backend: TranscriberBackends.Portable,
        AllowModelDownload: false);

    /// <summary>
    /// The files a CTranslate2 Whisper directory must hold — the same four
    /// <c>tools/package.py</c> refuses to package without. A directory with
    /// the weights and no tokenizer is not a model faster-whisper can load
    /// offline, so it is reported as missing here rather than discovered by
    /// the worker.
    /// </summary>
    /// <remarks>
    /// <c>preprocessor_config.json</c> carries the mel-bin count. Without it
    /// faster-whisper builds 80-bin features, so a Large v3 or Turbo export
    /// (128 bins) loads and then fails every hold. The worker also refuses
    /// that mismatch at init; this check names the missing file first.
    /// </remarks>
    public static IReadOnlyList<string> RequiredModelFiles { get; } =
        ["model.bin", "config.json", "tokenizer.json", "preprocessor_config.json"];

    /// <summary>
    /// The plan for this machine: <c>resources\</c> beside the executable,
    /// unless an override names another location.
    /// </summary>
    /// <remarks>
    /// Every path is made absolute here, once, against
    /// <paramref name="appDirectory"/>. A relative override used to be
    /// checked against the app's current directory and then handed to a
    /// worker whose working directory is the runtime directory, so the check
    /// and the load looked in two different places.
    /// </remarks>
    public static DictationWorkerPlan Resolve(string appDirectory, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentNullException.ThrowIfNull(environment);

        var root = Path.Combine(appDirectory, "resources");
        string Pick(string name, string bundled)
        {
            var value = environment(name);
            return Path.GetFullPath(
                string.IsNullOrWhiteSpace(value) ? bundled : value.Trim(),
                appDirectory);
        }

        return new DictationWorkerPlan(
            Pick(DictationEnvironment.Python, Path.Combine(root, "python", "python.exe")),
            Pick(DictationEnvironment.Runtime, Path.Combine(root, "runtime")),
            Pick(DictationEnvironment.ModelDirectory, Path.Combine(root, "model")));
    }

    /// <summary>The worker entry script inside <see cref="RuntimeDirectory"/>.</summary>
    public string WorkerScript => Path.Combine(RuntimeDirectory, "transcription_worker", "asr_server.py");

    /// <summary>Every required file that is not where the plan says it is.</summary>
    public IReadOnlyList<string> MissingFiles()
    {
        var required = new[] { PythonExecutable, WorkerScript }
            .Concat(RequiredModelFiles.Select(file => Path.Combine(ModelDirectory, file)));
        return required.Where(path => !File.Exists(path)).ToArray();
    }
}
