import Foundation

/// WHICH Whisper checkpoint a pipeline runs, and how a developer overrides it.
///
/// **Why this type exists.** Dictation and meeting notes each declared their
/// own model id, their own default, and — until this type — their own answer
/// to "is `DICTATION_ASR_MODEL` honored?" Dictation's was yes (after its own
/// expensive bug: the env var was read by the measurement CLI and silently
/// ignored by the app, so seventeen iterations of numbers were attributed to
/// a model that never ran). Notes' was **no**, with no override at all, which
/// meant "reproduce this on the other model" was a code edit on one pipeline
/// and impossible on the other.
///
/// The shipped model choice is shared: first-run setup verifies and activates
/// one Turbo generation for every speech-to-text path. Pipeline-specific
/// policy can still differ (for example the hallucination guard), but model
/// identity, override shape, and activation source must not drift.
public struct AsrModelConfig: Sendable, Equatable {
    /// The shipped checkpoint id, sent explicitly in `op_init`.
    ///
    /// Never omitted at a call site. `asr_server.op_init` silently defaulted
    /// to `whisper-base` while every doc and class default said
    /// `large-v3-turbo`, because NO caller sent `hf_id` — measured accuracy
    /// belonged to a model nobody was running.
    public var hfId: String

    /// Env var that swaps the model without a code edit, e.g.
    /// `DICTATION_ASR_MODEL`. Both pipelines read the same one: an A/B is
    /// meaningless if only half the app can be moved.
    public var envOverrideVar: String

    /// Whether this pipeline asks the worker to DROP short known-artifact
    /// phrases ("Thank you.", "谢谢", YouTube outros) instead of emitting
    /// them — sent as `hallucination_guard` on the `transcribe` op.
    ///
    /// A per-pipeline product judgement, which is exactly why it is declared
    /// here and sent explicitly rather than inferred in the worker. The first
    /// cut inferred it from the measured `voiced_fraction` and got it
    /// backwards on BOTH pipelines: notes' gate never forwards a fraction
    /// below its own 0.15 floor, so the blocklist could never fire on the one
    /// path it exists for; dictation has no fraction floor, so a real
    /// "谢谢" inside a long key-hold measured thin and was silently deleted.
    /// A threshold shared with a gate constant inverts the moment either side
    /// moves — so applicability is now a declaration, not a derivation.
    ///
    /// Notes: **true**. Background-queued, and the worst outcome is a
    /// fabricated sentence sitting in a saved document under a real person's
    /// name. Dictation: **false**. Interactive, the user is watching the text
    /// appear, and the worst outcome is eating a word they actually said.
    public var hallucinationGuard: Bool

    public init(hfId: String, envOverrideVar: String, hallucinationGuard: Bool) {
        self.hfId = hfId
        self.envOverrideVar = envOverrideVar
        self.hallucinationGuard = hallucinationGuard
    }

    /// The environment variable every Dictation ASR pipeline honors.
    public static let defaultOverrideVar = "DICTATION_ASR_MODEL"

    /// Push-to-talk: 4-bit MLX turbo. Keeps 82% of fp16 turbo's accuracy gain
    /// over `small` at LESS memory than the model it replaced (3,622 MB vs
    /// 3,726 MB), for +430 ms p50. Full measurement rationale lives on
    /// `DictationKit.PythonAsrTransport.hfId`, which reads this value — the
    /// numbers stay next to the feature that measured them.
    public static let dictation = AsrModelConfig(
        hfId: "mlx-community/whisper-large-v3-turbo-4bit",
        envOverrideVar: defaultOverrideVar,
        hallucinationGuard: false
    )

    /// Meetings: the same 4-bit MLX Turbo generation voice typing uses. The
    /// full-precision pack remains an explicit Best quality choice; once it is
    /// activated, both pipelines pick it up through the shared activation.
    public static let meetingNotes = AsrModelConfig(
        hfId: "mlx-community/whisper-large-v3-turbo-4bit",
        envOverrideVar: defaultOverrideVar,
        hallucinationGuard: true
    )

    /// The requested override, or nil when unset/blank.
    ///
    /// Blank is treated as unset deliberately.
    /// must not produce a run whose model id is the empty string.
    public func modelOverride(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> String? {
        guard let raw = environment[envOverrideVar] else { return nil }
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    /// THE model id this pipeline runs: the override if set, else `hfId`.
    /// The single resolution point — every construction site must go through
    /// this and nothing else, so a site that omits the argument cannot select
    /// a different model than one that passes it.
    public func resolvedHfId(
        environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> String {
        modelOverride(environment: environment) ?? hfId
    }
}
