using System.Text.Json.Serialization;

namespace Dictation.Speech;

/// <summary>
/// The three additive loop-v2 worker ops (spec P7-2, <c>the worker contract</c>):
/// <c>fired[]</c> on <c>transcribe</c>, <c>score</c> (forced scoring — the verifier primitive),
/// <c>score_release</c>, and <c>rescore</c> (a second pass on cached encoder output).
/// Everything here is additive: a request without the new fields produces the
/// exact engine call it produced before, and every new reply member is null
/// ("not measured") from a worker that predates it.
/// </summary>
public sealed record AsrFiredPhrase
{
    /// <summary>Host index into THIS request's <c>bias_phrases</c>, or null for an init-time default phrase.</summary>
    [JsonPropertyName("i")]
    public int? Index { get; init; }

    [JsonPropertyName("phrase")]
    public string? Phrase { get; init; }

    /// <summary>Generated-token index of the completing token (0 = first token after the prefix).</summary>
    [JsonPropertyName("pos")]
    public int? Pos { get; init; }

    /// <summary>Tokens matched.</summary>
    [JsonPropertyName("n")]
    public int? N { get; init; }
}

/// <summary>Exactly one of <see cref="PcmBase64"/> or <see cref="EncoderId"/> (the worker refuses both or neither).</summary>
public sealed record AsrScoreClip
{
    [JsonPropertyName("pcm_b64")]
    public string? PcmBase64 { get; init; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; init; }

    [JsonPropertyName("encoder_id")]
    public string? EncoderId { get; init; }

    public static AsrScoreClip FromPcm(string pcmBase64, int sampleRate = WorkflowAudio.SampleRateHz) =>
        new() { PcmBase64 = pcmBase64, SampleRate = sampleRate };

    public static AsrScoreClip FromEncoder(string encoderId) => new() { EncoderId = encoderId };
}

/// <summary>The <c>score</c> op: Δ = Σ_W log p(corr|x) − Σ_W log p(orig|x) (− λ·ILM term), unbiased decoder, no trie.</summary>
public sealed record AsrScoreRequest
{
    [JsonPropertyName("op")]
    public string Op => "score";

    /// <summary>Dictation-only by contract (the worker contract §2 principle 4); the
    /// worker namespaces the encoder cache by this.</summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = AsrClients.Dictation;

    /// <summary>The personal prior, stamped by <see cref="AsrWorkerTranscriber.ProbeAsync"/>
    /// when the caller left it null.</summary>
    [JsonPropertyName("profile")]
    public AsrProfile? Profile { get; init; }

    [JsonPropertyName("clip")]
    public AsrScoreClip Clip { get; init; } = new();

    /// <summary>Required (R11: forced scoring only when hold and claim languages match).</summary>
    [JsonPropertyName("language")]
    public string Language { get; init; } = "en";

    [JsonPropertyName("orig")]
    public string Orig { get; init; } = string.Empty;

    [JsonPropertyName("corr")]
    public string Corr { get; init; } = string.Empty;

    /// <summary>Only 0 is implemented on the QNN path (E1 chose λ = 0); λ &gt; 0 is refused with an explicit error.</summary>
    [JsonPropertyName("lambda")]
    public double Lambda { get; init; }

    /// <summary>≤ 8 strings scored the same way against <see cref="Orig"/>.</summary>
    [JsonPropertyName("distractors")]
    public string[]? Distractors { get; init; }

    [JsonPropertyName("keep_encoder")]
    public bool? KeepEncoder { get; init; }

    /// <summary>Echoed, never logged by the worker.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }
}

public sealed record AsrScoreReleaseRequest
{
    [JsonPropertyName("op")]
    public string Op => "score_release";

    [JsonPropertyName("encoder_id")]
    public string EncoderId { get; init; } = string.Empty;
}

public sealed record AsrScoreWindow
{
    [JsonPropertyName("orig")]
    public int[]? Orig { get; init; }

    [JsonPropertyName("corr")]
    public int[]? Corr { get; init; }

    [JsonPropertyName("len")]
    public int? Len { get; init; }
}

public sealed record AsrScoreDistractor
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("delta")]
    public double? Delta { get; init; }

    [JsonPropertyName("acoustic")]
    public double? Acoustic { get; init; }

    [JsonPropertyName("ilm")]
    public double? Ilm { get; init; }

    [JsonPropertyName("window_len")]
    public int? WindowLen { get; init; }
}

public sealed record AsrTiming
{
    [JsonPropertyName("encoder")]
    public double? Encoder { get; init; }

    [JsonPropertyName("encoder_cached")]
    public bool? EncoderCached { get; init; }

    [JsonPropertyName("decoder")]
    public double? Decoder { get; init; }

    [JsonPropertyName("strings")]
    public int? Strings { get; init; }

    [JsonPropertyName("passes")]
    public int? Passes { get; init; }

    [JsonPropertyName("tokens")]
    public int? Tokens { get; init; }
}

/// <summary>Reply to <c>score</c>. Every number is finite or null; verdicts are host-side knobs from E1 — the worker never returns one.</summary>
public sealed record AsrScoreResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("encoder_id")]
    public string? EncoderId { get; init; }

    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("delta")]
    public double? Delta { get; init; }

    [JsonPropertyName("acoustic")]
    public double? Acoustic { get; init; }

    [JsonPropertyName("ilm")]
    public double? Ilm { get; init; }

    [JsonPropertyName("window")]
    public AsrScoreWindow? Window { get; init; }

    [JsonPropertyName("distractors")]
    public AsrScoreDistractor[]? Distractors { get; init; }

    /// <summary>delta − max(distractor delta); null without distractors.</summary>
    [JsonPropertyName("margin")]
    public double? Margin { get; init; }

    [JsonPropertyName("timing_ms")]
    public AsrTiming? TimingMs { get; init; }

    [JsonPropertyName("tag")]
    public string? Tag { get; init; }
}

/// <summary>The <c>rescore</c> op: a full beam decode with ONLY the caller's small phrase set, on cached encoder output when available.</summary>
public sealed record AsrRescoreRequest
{
    [JsonPropertyName("op")]
    public string Op => "rescore";

    /// <summary>Dictation-only by contract; see <see cref="AsrScoreRequest.Client"/>.</summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = AsrClients.Dictation;

    /// <summary>Null = "the transcriber's default" (true for dictation), stamped
    /// by <see cref="AsrWorkerTranscriber.ProbeAsync"/>.</summary>
    [JsonPropertyName("text_passes")]
    public bool? TextPasses { get; init; }

    [JsonPropertyName("profile")]
    public AsrProfile? Profile { get; init; }

    [JsonPropertyName("encoder_id")]
    public string? EncoderId { get; init; }

    [JsonPropertyName("pcm_b64")]
    public string? PcmBase64 { get; init; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; init; }

    [JsonPropertyName("language")]
    public string Language { get; init; } = "en";

    /// <summary>1..8 phrases — the retrieved catalog neighbours, not the live set.</summary>
    [JsonPropertyName("bias_phrases")]
    public string[] BiasPhrases { get; init; } = Array.Empty<string>();

    [JsonPropertyName("phrase_deltas")]
    public Dictionary<string, double>? PhraseDeltas { get; init; }

    [JsonPropertyName("beam_size")]
    public int BeamSize { get; init; } = 4;

    [JsonPropertyName("completion_bonus")]
    public double? CompletionBonus { get; init; }

    /// <summary>The trie scalar; the host never sends above δ_max = 2.</summary>
    [JsonPropertyName("delta")]
    public double? Delta { get; init; }

    [JsonPropertyName("keep_encoder")]
    public bool? KeepEncoder { get; init; }

    [JsonPropertyName("tag")]
    public string? Tag { get; init; }
}
