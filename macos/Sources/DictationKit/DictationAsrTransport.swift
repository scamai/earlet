import Foundation
import DictationAudioCapture
import DictationIPC

/// The ASR hop, isolated behind a protocol so `DictationSession`'s
/// stage-sequencing/error-classification logic is unit-testable with a
/// scripted fake (no subprocess, no model, no TCC) — same seam shape as
/// `DictationIPC.AsrWireClient`.
public protocol DictationAsrTransport: Sendable {
    /// Spawn + initialize the worker. Idempotent: a transport that is
    /// already started returns `true` without doing anything.
    func start() async -> Bool
    /// One synchronous utterance -> text round trip. `samples` are Float32
    /// mono PCM in [-1, 1] at `sampleRate` Hz (16000 — the only rate
    /// `asr_server.py`'s `op_transcribe` accepts; DictationCapture's
    /// `CaptureResult.sampleRate` is always 16000 for the same reason
    /// `DictationAudioCapture` standardizes on it).
    func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome
    /// The SAME round trip, additionally carrying the worker's decode
    /// metadata (`DictationAsrDetail`: confidence + detected language).
    /// Defaulted below by wrapping `transcribe`, so a transport (or test
    /// fake) that has no metadata to offer conforms unchanged and its
    /// detail fields honestly read nil — a consumer must treat absence as
    /// "unknown", never as a detection.
    func transcribeDetailed(samples: [Float], sampleRate: Int,
                            compareBaseline: Bool) async -> DictationAsrDetailedOutcome
    /// Pins recognition to a BCP-47-ish language code for subsequent
    /// requests. Nil restores Whisper auto-detection. Implementations without
    /// language control inherit the no-op default below.
    func setLanguageCode(_ code: String?)
    /// Coarse scene captured for the next utterance; nil retains global ordering.
    func setVocabularyScene(_ scene: String?)
    /// Best-effort teardown. Safe to call even if `start()` was never
    /// called or already failed.
    func shutdown() async
}

extension DictationAsrTransport {
    public func setLanguageCode(_ code: String?) {}
    public func setVocabularyScene(_ scene: String?) {}

    /// `compareBaseline` is accepted and ignored here on purpose. A transport
    /// with no metadata to offer has no unassisted pass either, and the result
    /// honestly reports `baselineRan: false` rather than an empty column a
    /// comparison view would render as "the recogniser produced nothing".
    public func transcribeDetailed(
        samples: [Float], sampleRate: Int, compareBaseline: Bool = false
    ) async -> DictationAsrDetailedOutcome {
        switch await transcribe(samples: samples, sampleRate: sampleRate) {
        case .text(let text):
            return .result(DictationAsrDetail(text: text, avgLogprob: nil))
        case .failed(let failure):
            return .failed(failure)
        }
    }
}

/// Mirrors `TextInserter.InsertOutcome`'s success/failed shape so
/// `DictationSession` can pattern-match both stages the same way.
public enum DictationAsrOutcome: Sendable, Equatable {
    case text(String)
    case failed(DictationAsrFailure)
}

/// The transcript PLUS the worker's decode-confidence signal — used by the
/// shadow harness's `--dictate-file` mode, which needs `avg_logprob` in its
/// per-file JSON. `DictationSession`'s live path keeps consuming the plain
/// `DictationAsrOutcome` (it has no use for the stats), but both are
/// produced by the SAME wire round trip — see
/// `PythonAsrTransport.transcribeDetailed`.
public struct DictationAsrDetail: Sendable, Equatable {
    public let text: String
    /// Mean per-segment `avg_logprob` from the worker's `stats.mean_logprob`
    /// (mlx engine only — `nil` on backends that don't expose it, and a
    /// consumer must treat absence as "no confidence signal", never as
    /// "confident"; same contract as `whisper_mac.py`'s stats doc).
    public let avgLogprob: Double?
    /// The language the decode actually ran under, from the worker's
    /// `stats.language` — Whisper's own auto-detection on the dictation
    /// path (which pins nothing), reported as a BCP-47-ish code ("en",
    /// "zh", ...). Metadata only, never transcript content. `nil` on
    /// backends that don't expose it; absence means "unknown", never a
    /// detection.
    public let detectedLanguage: String?

    /// The same audio decoded with NO personalization — no trie, no aliases,
    /// no casing pass. `nil` unless the caller asked for it.
    ///
    /// Only the demo stage asks. It roughly doubles decode time, which is
    /// unacceptable on the dictation path and irrelevant on a stage where the
    /// comparison IS the point.
    public let baselineText: String?

    /// Whether the unassisted pass actually ran.
    ///
    /// `baselineText == nil` alone cannot distinguish "we did not decode a
    /// baseline" from "the baseline was empty", and a comparison view that
    /// cannot tell those apart will confidently show an empty column as though
    /// the unassisted recogniser had failed. On stage that reads as a rigged
    /// demo, which is worse than showing nothing.
    public let baselineRan: Bool

    public init(text: String, avgLogprob: Double?, detectedLanguage: String? = nil,
                baselineText: String? = nil, baselineRan: Bool = false) {
        self.text = text
        self.avgLogprob = avgLogprob
        self.detectedLanguage = detectedLanguage
        self.baselineText = baselineText
        self.baselineRan = baselineRan
    }
}

/// `DictationAsrOutcome`'s shape, carrying `DictationAsrDetail` instead of
/// a bare string.
public enum DictationAsrDetailedOutcome: Sendable, Equatable {
    case result(DictationAsrDetail)
    case failed(DictationAsrFailure)
}

/// Why the ASR hop failed, classified precisely enough to surface a
/// distinct message — mirrors `DictationInsertFailure`'s precision, per the
/// task's "degrade honestly at every stage" requirement.
public enum DictationAsrFailure: Sendable, Equatable, CustomStringConvertible {
    case workerUnavailable
    case notInitialized
    case ioError(String)
    case engineError(String)

    public var description: String {
        switch self {
        case .workerUnavailable:
            return "the ASR worker paths didn't resolve or the subprocess failed to spawn"
        case .notInitialized:
            return "transcribe called before a successful start()"
        case .ioError(let detail):
            return "ASR worker IPC error (worker may have died): \(detail)"
        case .engineError(let detail):
            return "ASR worker returned an error: \(detail)"
        }
    }
}

/// Real transport: reuses `DictationIPC.PythonEngineClient`'s spawn/framing
/// transport wholesale, and speaks the SAME flat wire contract
/// a native worker client does (`op="init"`/`"transcribe"`,
/// `pcm_b64`/`sample_rate`/`channel`/`utc_start` fields, int16 little-endian
/// PCM) — see `transcription_worker/asr_server.py`'s module doc comment for
/// the authoritative wire spec. Deliberately independent of any other client
/// itself; see the `DictationKit` target's `Package.swift` comment for why.
public final class PythonAsrTransport: DictationAsrTransport, DictationPreviewTransport,
    @unchecked Sendable {
    private let config: EngineConfig
    private let stderrSink: (@Sendable (String) -> Void)?
    private let modelDir: String?
    private let dbPath: String?
    private let client: any AsrWireClient
    private let stateLock = NSLock()
    private var initialized = false
    private var preferredLanguageCode: String?

    /// `channel` is echoed back by the worker, not validated against an
    /// enum server-side (see `op_transcribe`'s `req.get("channel")`) — a
    /// distinct literal makes dictation's ASR calls greppable in worker
    /// logs/telemetry apart from meeting-notes' `mic`/`system`.
    private static let channel = "dictation"

    /// Dictation's SHIPPED model choice.
    ///
    /// History: Iteration 20/21 found nobody ever sent `hf_id`, so
    /// `WhisperMac`'s class default always won, silently, and every dictation
    /// transcript actually ran under `asr_server.op_init`'s OLD
    /// `whisper-base` fallback before that fallback was deleted. The fix was
    /// to send this value EXPLICITLY, always. From then until 2026-08-07 the
    /// value was `openai/whisper-small`, chosen when the release-to-text
    /// latency budget (p50<=500ms/p90<=1000ms) was the binding constraint.
    ///
    /// **2026-08-07 (decision D-17): the
    /// human decided latency is acceptable, and the default moved to
    /// `mlx-community/whisper-large-v3-turbo-4bit`.** This is not an
    /// arbitrary pick — it is the cost-adjusted winner of a four-way sweep
    /// measured on the shadow corpus through this exact deployed worker, one
    /// series each for small, 4-bit, 8-bit and fp16:
    ///
    /// - Closes ~35% of the measured gap to the reference app: fair-ground
    ///   gap +0.944 -> +0.611 word-fixes per dictation.
    /// - Keeps 82% of fp16 turbo's accuracy gain over small, at LESS memory
    ///   than the model it replaces: peak worker footprint 3,622 MB vs
    ///   whisper-small's 3,726 MB (and -809 MB vs fp16 turbo's 4,431 MB). The
    ///   accuracy upgrade is memory-FREE, which is why D-6 (RAM) drops out of
    ///   this decision entirely.
    /// - Costs latency: p50 656 ms vs small's 226 ms. That is the whole price,
    ///   and it is the price the human accepted.
    ///
    /// Why not fp16 turbo (+0.458 gap, the best measured): it is better by
    /// only 0.077 edit words per dictation at corpus level, it is NOT
    /// statistically distinguishable from 4-bit on user-authored text
    /// (fair ground n=72, delta -0.153 [-0.450,+0.144]), and it costs +809 MB.
    /// fp16 stays fully reachable — set `DICTATION_ASR_MODEL` (see
    /// `modelOverrideEnvVar`), which now works on the LIVE `--dictate` path,
    /// not just the measurement CLI.
    ///
    /// Changing this value MUST move `DictateFileMode.deployedConfigHash()`
    /// (the shadow store keys its trend series on it) and therefore requires
    /// updating the pins in `DictateFileSelfCheck` and
    /// `AsrModelWiringSelfCheck` in the same commit. Dictation deliberately
    /// still declares its own model rather than inheriting any class default;
    /// notes may declare its own client.
    /// Sourced from `AsrModelConfig.dictation` so dictation and notes declare
    /// their (deliberately different) models through the SAME shape — see
    /// that type for why.
    public static let hfId = AsrModelConfig.dictation.hfId

    /// Env var that swaps the dictation model WITHOUT a code edit.
    ///
    /// This lives HERE, next to `hfId`, on purpose. Until 2026-08-07 the
    /// parsing lived in `DictateFileMode` (the measurement CLI) and the
    /// three product call sites simply omitted the `hfId:` argument — so
    /// `DICTATION_ASR_MODEL` was honored by the harness and silently ignored by
    /// the app. One decision ("which model does dictation run?") had two
    /// implementations that disagreed; that split WAS the bug. There is now
    /// exactly one resolver, `resolvedHfId(environment:)`, and it is also
    /// the initializer's default argument, so a call site that omits `hfId:`
    /// gets the same answer as one that passes it.
    public static let modelOverrideEnvVar = AsrModelConfig.dictation.envOverrideVar

    /// Dictation does NOT ask the worker to drop artifact phrases -- see
    /// `AsrModelConfig.hallucinationGuard`. Pinned against notes' opposite
    /// answer by `PipelineParitySelfCheck`.
    public static let hallucinationGuard = AsrModelConfig.dictation.hallucinationGuard

    /// The requested model override, or `nil` when unset/blank. Blank is
    /// treated as unset on purpose: a blank model override must not
    /// produce a run whose model id is the empty string.
    public static func modelOverride(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> String? {
        AsrModelConfig.dictation.modelOverride(environment: environment)
    }

    /// THE model id dictation runs: the env override if one is set, else the
    /// shipped `hfId`. The single resolution point — every construction site
    /// (live `--dictate`, `--dictate-file`, `--dictation-e2e`,
    /// `--dictation-soak`) resolves through this and nothing else.
    public static func resolvedHfId(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> String {
        AsrModelConfig.dictation.resolvedHfId(environment: environment)
    }

    /// The model id THIS transport instance will send in `op_init` — and
    /// therefore the model that actually transcribed every utterance it
    /// returns. Defaults to `resolvedHfId()` (shipped default, or the
    /// `DICTATION_ASR_MODEL` override).
    ///
    /// This exists so a caller can REPORT the model it used without
    /// re-reading a constant. The repo's most expensive ASR bug (see the
    /// `hfId` doc above, and `asr_server.op_init`'s comment) was exactly a
    /// reported-model/actual-model divergence: 17 iterations of numbers
    /// attributed to a model that never ran. The rule that prevents the
    /// recurrence is "the emitted `model` field comes from the value that
    /// was SENT" — this property is that value.
    public let modelId: String
    /// Immutable catalog revision for remote/cache resolution. nil is reserved
    /// for an explicit developer override.
    public let modelRevision: String?
    /// Product calls keep this false: a transcription request must never turn
    /// into an unreviewed multi-gigabyte model install.
    public let allowModelDownload: Bool

    /// The worker engine explicitly selected at the app boundary, if any.
    /// Exposed as a single non-secret value so the product wiring self-check
    /// can prove its engine decision reached the subprocess configuration.
    public let engineOverride: String?

    /// How the bundled interpreter runs the worker: shut off from the user's
    /// own Python.
    ///
    /// `-E` ignores every `PYTHON*` variable (`PYTHONPATH`, `PYTHONHOME`,
    /// `PYTHONSTARTUP`) and `-s` the user's site-packages. Without them a
    /// `launchctl setenv PYTHONPATH`, or a `pip install --user` of numpy for
    /// the same Python version, sorts ahead of the bundled packages: at best
    /// the engine will not start, at worst code that has none of Dictation's
    /// Microphone and Accessibility grants runs inside a process that has
    /// them. `-B` and `-u` stand in for `PYTHONDONTWRITEBYTECODE` and
    /// `PYTHONUNBUFFERED`, which `-E` turns off.
    ///
    /// Not `-I` with the script's path, which is how Windows launches it.
    /// Isolated mode also keeps the working directory off `sys.path`, and
    /// `asr_server.py` registers its optional ops (the live preview's
    /// `stream_start` among them) by importing `transcription_worker.*`
    /// while the module loads, before its `__main__` guard adds the runtime
    /// directory. Windows gets that directory from the `._pth` file the
    /// packager writes beside its embeddable Python; the Mac's Python has
    /// none, so under `-I` every `stream_start` came back "unknown op"
    /// (measured with the real `asr_server.py` on 3.9 and 3.12). `-m` from
    /// the runtime directory finds the package the way it always has.
    public static let workerArguments = ["-E", "-s", "-B", "-u", "-m", "transcription_worker.asr_server"]

    /// What `start()` spawns: interpreter, arguments, working directory and
    /// the variables laid over the inherited environment.
    public var launchConfig: EngineConfig { config }

    public init(
        pythonPath: String,
        workingDirectory: String,
        modelDir: String? = nil,
        dbPath: String? = nil,
        // Deliberately `resolvedHfId()` and NOT the bare `hfId` constant:
        // omitting this argument must not be able to select a DIFFERENT
        // model than passing it explicitly. That divergence is exactly the
        // 2026-08-07 bug (`DICTATION_ASR_MODEL` honored only by --dictate-file).
        hfId: String = PythonAsrTransport.resolvedHfId(),
        hfRevision: String? = nil,
        allowModelDownload: Bool = false,
        environment: [String: String] = [:],
        wireClient: (any AsrWireClient)? = nil,
        stderrSink: (@Sendable (String) -> Void)? = nil
    ) {
        self.modelId = hfId
        self.modelRevision = hfRevision
        self.allowModelDownload = allowModelDownload
        self.engineOverride = environment["DICTATION_ASR_ENGINE"]
        self.config = EngineConfig(
            pythonPath: pythonPath,
            workingDirectory: workingDirectory,
            arguments: Self.workerArguments,
            environment: environment
        )
        self.modelDir = modelDir
        self.dbPath = dbPath
        self.stderrSink = stderrSink
        self.client = wireClient ?? PythonEngineClient(config: config, stderrSink: stderrSink)
    }

    // Swift 6's async-context checker forbids raw `NSLock.lock()/unlock()`
    // calls directly inside an `async` function body (same rule
    // `DictationCapture` documents on its own locked helpers) — every
    // critical section here is wrapped in an ordinary synchronous method.
    private var preferredVocabularyScene: String?
    public var vocabularyScene: String? {
        stateLock.lock(); defer { stateLock.unlock() }
        return preferredVocabularyScene
    }
    public func setVocabularyScene(_ scene: String?) {
        stateLock.lock(); defer { stateLock.unlock() }
        preferredVocabularyScene = scene.flatMap { ["terminal", "wechat", "other"].contains($0) ? $0 : nil }
    }

    private func readInitialized() -> Bool {
        stateLock.lock(); defer { stateLock.unlock() }
        return initialized
    }

    private func setInitialized(_ value: Bool) {
        stateLock.lock(); initialized = value; stateLock.unlock()
    }

    /// The language the next request will send, exposed so the product wiring
    /// self-check can verify the setting reached the real transport without
    /// spawning Python or loading a model.
    public var languageCode: String? {
        stateLock.lock(); defer { stateLock.unlock() }
        return preferredLanguageCode
    }

    public func setLanguageCode(_ code: String?) {
        let normalized = code?.trimmingCharacters(in: .whitespacesAndNewlines)
        stateLock.lock()
        preferredLanguageCode = normalized?.isEmpty == false ? normalized : nil
        stateLock.unlock()
    }

    /// Process control for the worker, when the wire client has a process.
    /// A scripted wire client has none, and every check below treats that as
    /// "alive", so such a client behaves exactly as it did before.
    private var processControl: (any WorkerProcessControl)? {
        client as? any WorkerProcessControl
    }

    /// Initialized AND the worker process is still alive. Never waits on the
    /// IPC actor, so a caller can ask while a request is in flight.
    public var isReady: Bool {
        readInitialized() && (processControl?.isProcessRunning ?? true)
    }

    /// Kills the worker without going through the IPC actor — the one way to
    /// recover from a worker that stays alive but never answers. The pending
    /// request then fails with an IPC error. Leaves the transport
    /// uninitialized, so a later `start()` spawns a fresh worker.
    public func forceTerminate() {
        setInitialized(false)
        processControl?.forceTerminate()
    }

    public func start() async -> Bool {
        if readInitialized() {
            // `initialized` only learns about a death from the next failed
            // request. A worker that died between dictations (OOM, a crash)
            // would otherwise be reported as started here, and the user's
            // next utterance would be spent discovering it.
            if processControl?.isProcessRunning ?? true { return true }
            stderrSink?("PythonAsrTransport: worker exited while idle; starting a new one\n")
            setInitialized(false)
        }

        do {
            try await client.start()
            var fields: [String: JSONValue] = [
                "backend": .string("mac"),
                // ALWAYS explicit, and always THIS instance's id -- never
                // the type-level constant, so an overridden run cannot
                // silently init the default model while reporting the
                // override (or vice versa).
                "hf_id": .string(modelId),
            ]
            if let modelDir { fields["model_dir"] = .string(modelDir) }
            if let modelRevision { fields["hf_revision"] = .string(modelRevision) }
            fields["allow_model_download"] = .bool(allowModelDownload)
            if let dbPath { fields["db_path"] = .string(dbPath) }
            let resp = try await client.sendFlat(op: "init", fields: fields)
            guard !resp.isError else {
                stderrSink?("PythonAsrTransport: init failed: \(resp.error ?? "unknown error")\n")
                await client.stop()
                return false
            }
            // Record the engine from the INIT handshake, not from the first
            // decode. Without this the first request of a session cannot know
            // whether a vocabulary is safe to send, so it sends none and the
            // user's first utterance is silently un-biased — the regression
            // `asr-model-resolution-check` caught the moment the openai pin
            // was removed. A decode still refreshes it (the worker can fall
            // back between engines), so this is an earlier answer to the same
            // question, never a competing one.
            if let engine = resp["engine"]?.stringValue, !engine.isEmpty {
                Self.biasState.resolvedEngine = engine.lowercased()
            }
            setInitialized(true)
            return true
        } catch {
            stderrSink?("PythonAsrTransport: init threw: \(error)\n")
            await client.stop()
            return false
        }
    }

    // MARK: - Bias phrases

    /// Lock-guarded shared state, matching `DictationTerminalConditionTally`'s
    /// pattern in this module. Swift 6 rejects a bare mutable static, and it is
    /// right to: the engine name is written from the IPC continuation and read
    /// from whichever task builds the next request.
    public final class BiasState: @unchecked Sendable {
        private let lock = NSLock()
        private var loadedVocabulary: DictationBiasVocabulary?
        /// Modification date the cache was built from. `nil` when the file
        /// does not exist — which must still be cached, so a user with no
        /// vocabulary does not stat on every utterance forever.
        private var loadedStamp: Date?
        private var engine: String?
        private var enabled = false

        public init() {}

        /// Re-read whenever the file changes on disk; cached otherwise.
        ///
        /// This was "loaded once, lazily" and never re-read, which meant a word
        /// added to `bias_vocabulary.json` did not reach the recognizer until
        /// the app was relaunched. Nothing surfaced that: dictation kept
        /// working, the new term simply never came out right, and the only way
        /// to find out was to add a word, say it, and watch it fail.
        ///
        /// Keyed on modification date rather than an explicit `invalidate()`
        /// call deliberately. An invalidation hook only fires for writers who
        /// remember it, and the two writers that matter are a user editing the
        /// JSON by hand and a future import flow — neither of which can be
        /// made to call anything. A stat per utterance is free against a
        /// decode measured in seconds.
        ///
        /// The file is small (32 phrases today, capped at `maxPhrases`), so a
        /// re-read costs a parse of a few hundred bytes, not a model load.
        public var vocabulary: DictationBiasVocabulary {
            lock.lock(); defer { lock.unlock() }
            let url = DictationBiasVocabularyLoader.defaultURL()
            let stamp = url.flatMap {
                try? FileManager.default.attributesOfItem(atPath: $0.path)[.modificationDate] as? Date
            } ?? nil
            if let loadedVocabulary, stamp == loadedStamp { return loadedVocabulary }
            let loaded: DictationBiasVocabulary
            if let url {
                loaded = DictationBiasVocabularyLoader.load(from: url).bounded().vocabulary
            } else {
                loaded = .empty
            }
            loadedVocabulary = loaded
            loadedStamp = stamp
            return loaded
        }

        /// Engine the worker reported, lower-cased. Written at the `init`
        /// handshake and refreshed by every decode. `nil` only before the
        /// worker has answered at all, which is why the policy treats a nil
        /// engine as unsafe rather than optimistically sending phrases.
        public var resolvedEngine: String? {
            get { lock.lock(); defer { lock.unlock() }; return engine }
            set { lock.lock(); defer { lock.unlock() }; engine = newValue }
        }

        /// Set by the app layer from `feature.dictation_trie_bias`.
        public var flagEnabled: Bool {
            get { lock.lock(); defer { lock.unlock() }; return enabled }
            set { lock.lock(); defer { lock.unlock() }; enabled = newValue }
        }

        /// Test seam; production never sets the vocabulary directly.
        public func overrideVocabularyForTesting(_ vocabulary: DictationBiasVocabulary) {
            lock.lock(); defer { lock.unlock() }
            loadedVocabulary = vocabulary
        }
    }

    public static let biasState = BiasState()

    /// Injected, never read here. `DictationSession` records the same rule at
    /// :165 -- this module does not read `FeatureFlags` itself; the app layer
    /// reads `feature.dictation_trie_bias` and sets this at the call-site
    /// boundary. Default false, so a build that forgets to wire it sends
    /// nothing rather than silently biasing.
    public static var biasFlagEnabled: Bool {
        get { biasState.flagEnabled }
        set { biasState.flagEnabled = newValue }
    }

    /// Adds `bias_phrases` when the resolved engine can use a vocabulary at
    /// all — the trie on `openai`, `initial_prompt` on `mlx`.
    ///
    /// This comment used to say mlx should be sent silence, citing Iteration
    /// 7's +14.7% WER. That number is now the wrong basis twice over. Iteration
    /// **10** reduced it to +3.2% on the same 70
    /// terms by prose-wrapping alone — while moving damage onto the control
    /// group, +9.5% to +11.7%, so it is a reduction and not an elimination. And
    /// research run A78 on 306 code-mixed clips does not reproduce it at all:
    /// jargon error falls 41–49% and ordinary-word error falls BELOW the
    /// no-vocabulary baseline, with zero false swaps in every arm.
    ///
    /// **A78 is not attributable to the engine yet** — it also carries
    /// vocabulary-last ordering, a script seed, and a matched temperature
    /// fallback, none of which Iteration 7 had. Isolating the engine needs
    /// Iteration 7's exact construction on both engines (A79, running). Treat
    /// this as "the old figure does not describe the current path", not as "the
    /// path is proven safe".
    ///
    /// The whole vocabulary is sent as one list; there is no per-utterance
    /// selection hook here. Every measurement so far says there should be —
    /// small and context-selected beats large and static — see A80.
    static func applyBiasPhrases(to fields: inout [String: JSONValue],
                                 vocabulary: DictationBiasVocabulary,
                                 engine: String?,
                                 flagEnabled: Bool) {
        guard !vocabulary.isEmpty,
              DictationBiasPolicy.shouldSendPhrases(engine: engine, flagEnabled: flagEnabled)
        else { return }
        fields["bias_phrases"] = .array(vocabulary.phrases.map { .string($0) })
    }

    /// Entry point for the self-check that crosses the transport joint.
    public static func requestFieldsForTesting(vocabulary: DictationBiasVocabulary,
                                               engine: String?,
                                               flagEnabled: Bool) -> [String: JSONValue] {
        var fields: [String: JSONValue] = ["pcm_b64": .string("")]
        applyBiasPhrases(to: &fields, vocabulary: vocabulary,
                         engine: engine, flagEnabled: flagEnabled)
        return fields
    }

    /// The engine that is safe to use for bias policy before the first decode.
    /// An explicit process pin is authoritative and the worker validates it at
    /// init; otherwise we wait for the engine reported by a completed decode.
    private var effectiveBiasEngine: String? {
        let pinned = engineOverride?
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        if let pinned, !pinned.isEmpty { return pinned }
        return Self.biasState.resolvedEngine
    }

    /// Regression seam for the first-request wiring. Unlike the static helper,
    /// this crosses the instance's configured-engine boundary exactly as the
    /// live request does.
    public func requestFieldsForTesting(vocabulary: DictationBiasVocabulary,
                                        flagEnabled: Bool) -> [String: JSONValue] {
        Self.requestFieldsForTesting(
            vocabulary: vocabulary,
            engine: effectiveBiasEngine,
            flagEnabled: flagEnabled)
    }

    public func transcribe(samples: [Float], sampleRate: Int) async -> DictationAsrOutcome {
        switch await transcribeDetailed(samples: samples, sampleRate: sampleRate,
                                        compareBaseline: false) {
        case .result(let detail): return .text(detail.text)
        case .failed(let failure): return .failed(failure)
        }
    }

    // MARK: - Bounded live preview

    /// Opens the same Whisper stream Windows dictation uses. It reuses this
    /// transport's already-loaded model; it deliberately does not call
    /// `start()` itself, because product startup owns that single-flight and
    /// two concurrent init frames would corrupt the one-request/one-reply
    /// pipe. The product preview session waits for that shared startup before
    /// opening a stream. Direct callers must likewise ensure readiness; the
    /// authoritative final decode remains independent of preview success.
    public func startPreview(languages: [String]) async -> String? {
        let scene = vocabularyScene
        guard readInitialized() else { return nil }
        var fields: [String: JSONValue] = [
            "sample_rate": .number(16_000),
            "engine": .string("whisper"),
            "client": .string("dictation"),
            // The mutable tail is what dictation wants. Stable-window commits
            // are a meeting presentation policy, not an insertion candidate.
            "rolling_stable": .bool(false),
        ]
        if let scene { fields["vocabulary_scene"] = .string(scene) }
        let normalizedLanguages = languages.reduce(into: [String]()) { result, raw in
            let code = raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            if !code.isEmpty, !result.contains(code) { result.append(code) }
        }
        if !normalizedLanguages.isEmpty {
            fields["languages"] = .array(normalizedLanguages.map(JSONValue.string))
        }
        Self.applyBiasPhrases(
            to: &fields,
            vocabulary: Self.biasState.vocabulary,
            engine: effectiveBiasEngine,
            flagEnabled: Self.biasFlagEnabled)

        do {
            let response = try await client.sendFlat(op: "stream_start", fields: fields)
            guard !response.isError,
                  let streamID = response["stream_id"]?.stringValue,
                  !streamID.isEmpty else {
                stderrSink?("PythonAsrTransport: dictation preview unavailable: "
                    + "\(response.error ?? "missing_stream_id")\n")
                return nil
            }
            stderrSink?("PythonAsrTransport: dictation preview ready engine="
                + "\(response["engine"]?.stringValue ?? "unknown")\n")
            return streamID
        } catch {
            // Preview failure is deliberately isolated from authoritative
            // transcription. The final call owns worker-death classification.
            stderrSink?("PythonAsrTransport: dictation preview start failed: \(error)\n")
            return nil
        }
    }

    public func pushPreview(
        streamID: String, samples: [Float], sampleRate: Int
    ) async -> DictationPreviewHypothesis? {
        guard readInitialized(), !streamID.isEmpty, !samples.isEmpty else { return nil }
        // The stream_start contract is fixed at 16 kHz and stream_push has no
        // rate field. Joining two rates under one id would silently corrupt
        // time and pitch, so refuse rather than pretend to resample here.
        guard sampleRate == 16_000 else {
            stderrSink?("PythonAsrTransport: dictation preview dropped mismatched "
                + "sample_rate=\(sampleRate)\n")
            return nil
        }
        let pcm = Self.floatToInt16LE(samples)
        do {
            let response = try await client.sendFlat(op: "stream_push", fields: [
                "stream_id": .string(streamID),
                "pcm_b64": .string(pcm.base64EncodedString()),
                "channel": .string("mic"),
            ])
            guard !response.isError else {
                stderrSink?("PythonAsrTransport: dictation preview push rejected: "
                    + "\(response.error ?? "unknown")\n")
                return nil
            }
            return DictationPreviewHypothesis(
                text: DictationPreviewText.liveTail(response["text"]?.stringValue),
                isEndpoint: response["is_endpoint"]?.boolValue ?? false)
        } catch {
            stderrSink?("PythonAsrTransport: dictation preview push failed: \(error)\n")
            return nil
        }
    }

    public func cancelPreview(streamID: String) async {
        guard !streamID.isEmpty else { return }
        do {
            let response = try await client.sendFlat(
                op: "stream_cancel", fields: ["stream_id": .string(streamID)])
            if response.isError {
                stderrSink?("PythonAsrTransport: dictation preview cancel rejected: "
                    + "\(response.error ?? "unknown")\n")
            }
        } catch {
            stderrSink?("PythonAsrTransport: dictation preview cancel failed: \(error)\n")
        }
    }

    /// The SAME round trip as `transcribe(samples:sampleRate:)` — identical
    /// op, identical fields, identical worker — additionally surfacing the
    /// worker's `stats.mean_logprob`. `--dictate-file` (the shadow
    /// harness's one-shot file mode) is the intended caller; keeping this
    /// on the transport (rather than a parallel client in the CLI) is what
    /// guarantees the harness exercises the exact deployed ASR hop.
    public func transcribeDetailed(samples: [Float], sampleRate: Int,
                                   compareBaseline: Bool = false) async -> DictationAsrDetailedOutcome {
        let scene = vocabularyScene
        guard readInitialized() else { return .failed(.notInitialized) }

        // Iteration 41 tracing (see PythonEngineClient.writeAndRead for the
        // network-side stages): times the two steps that happen BEFORE the
        // frame is even handed to the transport — int16 conversion and
        // base64 encoding — plus the actor-hop/queueing gap between "caller
        // is about to await sendFlat" and "the IPC actor started running
        // it" (that gap is `await_actor_ms` below; the actor's own internal
        // stages are the `IPC_TRACE ...` line PythonEngineClient prints).
        let tracing = Self.tracingEnabled
        let t0 = tracing ? DispatchTime.now() : nil
        let pcm = Self.floatToInt16LE(samples)
        let tPcm = tracing ? DispatchTime.now() : nil
        let pcmB64 = pcm.base64EncodedString()
        let tB64 = tracing ? DispatchTime.now() : nil
        var fields: [String: JSONValue] = [
            "pcm_b64": .string(pcmB64),
            "sample_rate": .number(Double(sampleRate)),
            "channel": .string(Self.channel),
            "utc_start": .string(Self.isoString(Date())),
        ]
        // Same field notes sends (`AsrWorkerClient.transcribeNow`), from the
        // same shared measurement — `whisper_mac`'s hallucination-phrase
        // blocklist must see the SAME signal on both pipelines or the two
        // features drift apart again in the worker instead of in Swift.
        // Measured here rather than threaded through `DictationAsrTransport`
        // so the number is derived from exactly the samples that are being
        // sent, and every conforming fake stays untouched.
        fields["voiced_fraction"] = .number(AudioGate.voicedFraction(
            samples: samples, sampleRate: sampleRate,
            threshold: AsrGatePolicy.dictation.audibleThreshold))
        // Dictation opts OUT of the worker's hallucination-phrase blocklist.
        // The user is watching the text appear and the worst outcome here is
        // eating a word they actually said -- a real "谢谢" or "thank you very
        // much" inside a long key-hold is ordinary dictation, not an
        // artifact. The silence gate above is what protects this pipeline;
        // it never hands the decoder a clip with nothing in it.
        fields["hallucination_guard"] = .bool(Self.hallucinationGuard)
        // Demo stage only. Roughly doubles decode time, so it is opt-in
        // per request rather than a mode the dictation path can inherit.
        if compareBaseline { fields["compare_baseline"] = .bool(true) }
        if let scene { fields["vocabulary_scene"] = .string(scene) }
        if let languageCode {
            fields["language"] = .string(languageCode)
        }
        // The dictation path has never populated this field. Notes does; the
        // worker merges it (`asr_server.py:198`) and the trie consumes it —
        // only the dictation request omitted it, and no test crossed the joint
        // to notice. `runDictationBiasVocabularySelfCheck` is that test.
        Self.applyBiasPhrases(to: &fields,
                              vocabulary: Self.biasState.vocabulary,
                              engine: effectiveBiasEngine,
                              flagEnabled: Self.biasFlagEnabled)
        if tracing, let t0, let tPcm, let tB64 {
            FileHandle.standardError.write(Data((
                "IPC_TRACE(caller) int16_convert_ms=\(Self.ms(t0, tPcm)) "
                + "base64_encode_ms=\(Self.ms(tPcm, tB64)) "
                + "samples=\(samples.count) pcm_bytes=\(pcm.count) b64_bytes=\(pcmB64.count)\n"
            ).utf8))
        }
        do {
            let resp = try await client.sendFlat(op: "transcribe", fields: fields)
            guard !resp.isError else {
                return .failed(.engineError(resp.error ?? "unknown error"))
            }
            // See AsrWorkerClient's equivalent: an intentionally-emptied
            // decode must say so, or a mis-armed guard eating real speech
            // looks exactly like a bad decode.
            if let reason = resp["dropped_reason"]?.stringValue, !reason.isEmpty {
                FileHandle.standardError.write(Data(
                    "PythonAsrTransport: DROPPED utterance reason=\(reason)\n".utf8))
            }
            // The worker names the engine that actually resolved
            // (`whisper_mac.py` (`WhisperMac.transcribe`, engine-resolution block)). The bias policy needs it, and it is
            // only knowable after a decode -- which is why the policy treats a
            // nil engine as unsafe rather than optimistically sending phrases.
            if let engine = resp["stats"]?["engine"]?.stringValue, !engine.isEmpty {
                Self.biasState.resolvedEngine = engine.lowercased()
            }
            return .result(DictationAsrDetail(
                text: resp["text"]?.stringValue ?? "",
                avgLogprob: resp["stats"]?["mean_logprob"]?.doubleValue,
                detectedLanguage: resp["stats"]?["language"]?.stringValue,
                baselineText: resp["baseline_text"]?.stringValue,
                baselineRan: resp["baseline_ran"]?.boolValue ?? false))
        } catch {
            // A dead pipe means every subsequent call will fail the same
            // way until a fresh `start()` — mark uninitialized so the
            // caller's next attempt gets `.notInitialized` (cheap, local)
            // instead of repeatedly paying a timeout against a dead child.
            setInitialized(false)
            return .failed(.ioError(String(describing: error)))
        }
    }

    public func ping() async -> Bool {
        guard isReady else { return false }
        do { return try await client.sendFlat(op: "ping", fields: [:]).ok }
        catch { return false }
    }

    public func shutdown() async {
        let wasInitialized = readInitialized()
        setInitialized(false)
        guard wasInitialized else { return }
        _ = try? await client.sendFlat(op: "shutdown", fields: [:])
        await client.stop()
    }

    // MARK: - PCM conversion
    //
    // Identical algorithm to the native worker's floatToInt16LE conversion
    // (not `public` there, and — per the dependency note above — this
    // module intentionally keeps the conversion local). ~10 lines;
    // duplicating the algorithm here is cheaper and more honest than adding
    // a cross-module dependency just to share it, and the wire format
    // (int16 LE) is a fixed property of `asr_server.py`'s contract, not a
    // business rule that could drift between the two call sites.
    /// Iteration 41 (verification lane): rewritten from a per-sample
    /// `Data.append(contentsOf:)` loop (one heap-mutation call per sample —
    /// e.g. ~163k calls for a 61-word utterance) to a single pre-sized
    /// buffer written via `withUnsafeMutableBytes`. Same output for every
    /// input (clamp -> scale -> round -> Int16 -> little-endian, byte for
    /// byte identical — covered by `DictationSelfCheck`'s existing
    /// `floatToInt16LE` round-trip assertions, unchanged by this diff), just
    /// without the repeated `Data` mutation overhead. Measured on this
    /// machine (n=19, warm worker, `openai/whisper-small`, real deployed
    /// worker): this conversion alone accounted for a MEAN 37ms and up to
    /// 73ms of the release-to-text ASR stage on the longest (61-word)
    /// utterances — the single largest identified contributor to the
    /// Iteration 17/39 Python-vs-Swift latency gap. Iteration 41's
    /// controlled comparison found it.
    static func floatToInt16LE(_ samples: [Float]) -> Data {
        var data = Data(count: samples.count * 2)
        data.withUnsafeMutableBytes { (raw: UnsafeMutableRawBufferPointer) in
            let out = raw.bindMemory(to: Int16.self)
            for i in 0..<samples.count {
                let clamped = max(-1.0, min(1.0, samples[i]))
                out[i] = Int16(clamping: Int32((clamped * 32767.0).rounded())).littleEndian
            }
        }
        return data
    }

    static func isoString(_ date: Date) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f.string(from: date)
    }

    /// Same gate/caching pattern as `PythonEngineClient.tracingEnabled` —
    /// kept as a separate cached static here rather than importing DictationIPC's
    /// private one, since the two are in different modules.
    static let tracingEnabled: Bool = {
        ProcessInfo.processInfo.environment["DICTATION_IPC_TRACE"] == "1"
    }()

    static func ms(_ a: DispatchTime, _ b: DispatchTime) -> String {
        String(format: "%.3f", Double(b.uptimeNanoseconds - a.uptimeNanoseconds) / 1_000_000)
    }
}
