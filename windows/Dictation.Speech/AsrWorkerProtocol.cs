using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictation.Speech;

/// <summary>
/// The speech worker as a process with two pipes. Implemented for real by
/// <c>Dictation.Platform</c>'s <c>PythonAsrWorkerProcess</c>, and by
/// a fake in the test project.
/// </summary>
/// <remarks>
/// Two pipes and a liveness flag is the whole abstraction; everything above it is arithmetic and JSON, which is why
/// <see cref="AsrWorkerTranscriber"/> can be proven on a machine with no
/// Python at all.
/// </remarks>
public interface IAsrWorkerProcess : IAsyncDisposable, IDisposable
{
    // IDisposable is not decoration. A host that disposes synchronously (a
    // dependency-injection container at exit, a using block) calls Dispose,
    // and an implementation with only IAsyncDisposable makes that call throw
    // InvalidOperationException, which crashes the app on exit. Requiring
    // both here makes the next implementation unable to introduce that.

    /// <summary>True once <see cref="StartAsync"/> has spawned a worker and it
    /// has not exited.</summary>
    bool IsAlive { get; }

    /// <summary>The worker's stdin — request frames go here.</summary>
    Stream StandardIn { get; }

    /// <summary>The worker's stdout — reply frames come from here.</summary>
    Stream StandardOut { get; }

    /// <summary>Spawn the worker. Idempotent while one is alive.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Stop the worker, force-killing it if it outlives
    /// <paramref name="grace"/>.</summary>
    Task StopAsync(TimeSpan grace, CancellationToken ct);
}

/// <summary>
/// A reply frame that arrived WHOLE but did not fit the shape its op expected.
/// </summary>
/// <remarks>
/// Distinguished from an I/O fault precisely because the pipe is NOT
/// desynchronised: the length header was honoured and the payload was read to
/// the byte, so the next frame begins where it should and the worker is still
/// usable. It costs the REQUEST, never the process — which matters because one
/// worker is shared by dictation, voice workflows and secondary capture, and a
/// contract mismatch in any one of them used to take the other two down.
/// </remarks>
public sealed class AsrReplyShapeException(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// Length-prefixed JSON framing for the speech worker's wire protocol.
///
/// <para>
/// Wire format, both directions, identical to
/// <c>transcription_worker/asr_server.py</c>:
/// <c>[4-byte BIG-ENDIAN uint32 length][UTF-8 JSON payload]</c>.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The framing lives here, in a <c>net9.0</c> project, rather than beside
/// the Windows worker process, so the whole protocol is provable on Linux.
/// </para>
/// <para>
/// <see cref="BinaryPrimitives.WriteUInt32BigEndian"/> and not
/// <c>BitConverter</c>: <c>BitConverter</c> is host byte order, and on
/// little-endian Windows it produces a header that Python's
/// <c>struct.unpack("&gt;I", …)</c> reads as a multi-gigabyte payload.
/// </para>
/// </remarks>
/// <summary>
/// A request frame that carries its PCM OUT OF BAND of the JSON serializer:
/// the raw bytes ride on these properties (marked <c>[JsonIgnore]</c> on the
/// DTO) and <see cref="AsrFraming.WriteFrameAsync"/> splices them into the
/// frame as <c>pcm_b64</c> / <c>pcm_b64_system</c> string members itself,
/// streamed in bounded chunks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>Utf8JsonWriter</c> refuses any single JSON
/// string value longer than ~166 M UTF-16 chars ("The JSON value of length N
/// is too large and not supported"). Base64 of one 16 kHz mono PCM16 channel
/// crosses that at ~125 MB of raw audio — about 65 minutes — so the accurate
/// pass on a longer meeting threw out of <c>WriteFrameAsync</c> the moment the
/// take was serialized, ~instantly, with a warm worker; the catch-all in
/// <c>AsrWorkerHost.ExchangeAsync</c> then also forgot that worker. A 103-min
/// take (2×198 MB channels) reproduced it deterministically. The worker side
/// has no such ceiling (its framing is a plain <c>uint32</c> length +
/// <c>json.loads</c>, verified against the real loop at 550 MB), so the fix
/// belongs entirely on this side of the pipe.
/// </para>
/// <para>
/// Base64 is pure ASCII and never needs JSON escaping, which is what makes the
/// splice value-exact with what the serializer would have written (the spliced
/// members land at the END of the object rather than in declaration order;
/// JSON object order carries no meaning and the worker reads a dict) —
/// asserted by the framing tests. It also removes the multi-hundred-MB
/// intermediate strings: the encode streams straight to the pipe in 3 MB
/// chunks.
/// </para>
/// <para>
/// <b>Contract for implementers:</b> when an out-of-band buffer is non-null,
/// the DTO's corresponding base64 STRING property must be null (so the
/// serializer omits it under <c>WhenWritingNull</c>) — otherwise the frame
/// would carry the key twice.
/// </para>
/// </remarks>
public interface IAsrOutOfBandPcm
{
    /// <summary>Raw PCM for the frame's <c>pcm_b64</c> member, or null when
    /// the string property carries it (or the frame has none).</summary>
    byte[]? OutOfBandPcm { get; }

    /// <summary>Raw PCM for <c>pcm_b64_system</c>, or null.</summary>
    byte[]? OutOfBandSystemPcm { get; }
}

public static class AsrFraming
{
    /// <summary>
    /// Serializer settings for every frame. Property names are pinned by
    /// <see cref="JsonPropertyNameAttribute"/> on the DTOs rather than by a
    /// naming policy, because the wire is snake_case and the C# is not.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Write one length-prefixed JSON frame and flush. A frame whose
    /// <see cref="IAsrOutOfBandPcm"/> buffers are set gets its audio spliced in
    /// as streamed base64 — see the interface remarks for why the serializer
    /// cannot be allowed to see those values.</summary>
    public static async Task WriteFrameAsync<T>(Stream output, T frame, CancellationToken ct)
    {
        if (frame is IAsrOutOfBandPcm oob
            && (oob.OutOfBandPcm is { Length: > 0 } || oob.OutOfBandSystemPcm is { Length: > 0 }))
        {
            await WriteOutOfBandPcmFrameAsync(output, frame, oob, ct).ConfigureAwait(false);
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)json.Length);

        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(json, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Base64 length for <paramref name="byteCount"/> bytes, padding
    /// included — what <c>Convert.ToBase64String</c> would produce.</summary>
    public static long Base64Length(long byteCount) => (byteCount + 2) / 3 * 4;

    private static readonly byte[] PcmKeyPrefix = Encoding.ASCII.GetBytes("\"pcm_b64\":\"");
    private static readonly byte[] SystemPcmKeyPrefix = Encoding.ASCII.GetBytes("\"pcm_b64_system\":\"");

    /// <summary>Raw-audio bytes per encode chunk. A multiple of 3, so every
    /// chunk emits complete base64 quads and only the final one pads.</summary>
    private const int PcmEncodeChunk = 3 * 1024 * 1024;

    private static async Task WriteOutOfBandPcmFrameAsync<T>(
        Stream output, T frame, IAsrOutOfBandPcm oob, CancellationToken ct)
    {
        // The head: every member EXCEPT the audio. The out-of-band properties
        // are [JsonIgnore] and the corresponding string properties must be
        // null (interface contract), so the head never carries pcm_b64 keys.
        var head = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        if (head.Length < 2 || head[0] != (byte)'{' || head[^1] != (byte)'}')
            throw new InvalidOperationException(
                $"an out-of-band PCM frame must serialize to a JSON object; {typeof(T).Name} did not");

        var mic = oob.OutOfBandPcm;
        var sys = oob.OutOfBandSystemPcm;

        long payloadLength = head.Length; // '{' + members + '}' — splices go inside
        var headHasMembers = head.Length > 2;
        var needComma = headHasMembers;
        if (mic is { Length: > 0 })
        {
            payloadLength += (needComma ? 1 : 0) + PcmKeyPrefix.Length + Base64Length(mic.Length) + 1;
            needComma = true;
        }
        if (sys is { Length: > 0 })
        {
            payloadLength += (needComma ? 1 : 0) + SystemPcmKeyPrefix.Length + Base64Length(sys.Length) + 1;
        }

        // The read side (both this class and a hypothetical newer worker)
        // treats anything past Int32.MaxValue as unreadable; refuse loudly
        // here rather than framing something nobody can parse. At 16 kHz
        // PCM16 this is ~4.6 hours per channel of a dual-channel take.
        if (payloadLength > int.MaxValue)
            throw new InvalidOperationException(
                $"audio frame of {payloadLength} bytes exceeds the wire's Int32.MaxValue frame limit");

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payloadLength);
        await output.WriteAsync(header, ct).ConfigureAwait(false);

        // head minus its closing '}' …
        await output.WriteAsync(head.AsMemory(0, head.Length - 1), ct).ConfigureAwait(false);
        needComma = headHasMembers;
        if (mic is { Length: > 0 })
        {
            await WriteSplicedBase64MemberAsync(output, needComma, PcmKeyPrefix, mic, ct)
                .ConfigureAwait(false);
            needComma = true;
        }
        if (sys is { Length: > 0 })
        {
            await WriteSplicedBase64MemberAsync(output, needComma, SystemPcmKeyPrefix, sys, ct)
                .ConfigureAwait(false);
        }
        await output.WriteAsync(new[] { (byte)'}' }, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteSplicedBase64MemberAsync(
        Stream output, bool leadingComma, byte[] keyPrefix, byte[] pcm, CancellationToken ct)
    {
        if (leadingComma)
            await output.WriteAsync(new[] { (byte)',' }, ct).ConfigureAwait(false);
        await output.WriteAsync(keyPrefix, ct).ConfigureAwait(false);

        // Streamed: 3 MB of PCM per chunk → 4 MB of base64, no intermediate
        // whole-take string anywhere. Chunks are multiples of 3 so only the
        // last block emits padding.
        var encoded = new byte[System.Buffers.Text.Base64.GetMaxEncodedToUtf8Length(PcmEncodeChunk)];
        for (var offset = 0; offset < pcm.Length; offset += PcmEncodeChunk)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(PcmEncodeChunk, pcm.Length - offset);
            var status = System.Buffers.Text.Base64.EncodeToUtf8(
                pcm.AsSpan(offset, count), encoded, out var consumed, out var written,
                isFinalBlock: offset + count >= pcm.Length);
            if (status != System.Buffers.OperationStatus.Done || consumed != count)
                throw new InvalidOperationException($"base64 encode failed: {status}");
            await output.WriteAsync(encoded.AsMemory(0, written), ct).ConfigureAwait(false);
        }

        await output.WriteAsync(new[] { (byte)'"' }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read exactly one frame. Returns <c>null</c> at a clean EOF — a worker
    /// that closed stdout without replying, which is what a crash looks like
    /// from this side. Throws on a truncated frame, because a half-read header
    /// means every later frame is misaligned.
    /// </summary>
    public static async Task<T?> ReadFrameAsync<T>(Stream input, CancellationToken ct)
        where T : class
    {
        var header = new byte[4];
        var headerRead = await ReadExactAsync(input, header, ct).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < 4)
        {
            throw new IOException($"truncated length header: got {headerRead} of 4 bytes");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0)
        {
            return null;
        }

        if (length > int.MaxValue)
        {
            throw new IOException($"frame length {length} exceeds Int32.MaxValue");
        }

        var payload = new byte[(int)length];
        var payloadRead = await ReadExactAsync(input, payload, ct).ConfigureAwait(false);
        if (payloadRead < length)
        {
            throw new IOException($"truncated payload: got {payloadRead} of {length} bytes");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            // NOT a broken pipe, and the distinction is the whole point. The
            // header was honoured and the payload was read to the byte above,
            // so the stream is still aligned and the next frame starts exactly
            // where it should — only this reply's SHAPE was wrong.
            //
            // Letting a JsonException out reaches AsrWorkerHost's catch-all,
            // which calls ForgetWorker() and tears down the worker that
            // dictation, voice workflows and secondary capture all SHARE. One odd
            // notes reply took dictation down with it. Named so the host can
            // tell a bad body from a bad pipe and cost the request instead.
            throw new AsrReplyShapeException(
                $"a reply of {typeof(T).Name} did not match its contract", ex);
        }
    }

    /// <summary>
    /// Fill <paramref name="buffer"/> or stop at EOF, returning how many bytes
    /// were filled. Pipes hand back short reads under load, and a caller that
    /// accepted one would misalign the next frame's header.
    /// </summary>
    public static async Task<int> ReadExactAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await input
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Encode one frame to a buffer. Used by the test worker to script
    /// replies without a process — and by the contract tests to assert wire
    /// shape, which is why it must produce the SAME bytes as
    /// <see cref="WriteFrameAsync"/>, out-of-band splicing included.</summary>
    public static byte[] EncodeFrame<T>(T frame)
    {
        if (frame is IAsrOutOfBandPcm oob
            && (oob.OutOfBandPcm is { Length: > 0 } || oob.OutOfBandSystemPcm is { Length: > 0 }))
        {
            using var buffer = new MemoryStream();
            // MemoryStream writes complete synchronously; this cannot deadlock.
            WriteOutOfBandPcmFrameAsync(buffer, frame, oob, CancellationToken.None)
                .GetAwaiter().GetResult();
            return buffer.ToArray();
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        var encoded = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(0, 4), (uint)json.Length);
        Buffer.BlockCopy(json, 0, encoded, 4, json.Length);
        return encoded;
    }

    /// <summary>The JSON payload of one frame, as text — so a test can assert
    /// the wire shape rather than a round trip through the same DTO that wrote
    /// it.</summary>
    public static string DecodeFrameJson(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4)
        {
            throw new ArgumentException("frame shorter than its header", nameof(frame));
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(frame[..4]);
        if (frame.Length - 4 < length)
        {
            throw new ArgumentException("frame truncated against its header", nameof(frame));
        }

        return Encoding.UTF8.GetString(frame.Slice(4, (int)length));
    }
}

/// <summary>
/// The <c>client</c> tag every decode request carries (the worker contract
/// §3): which feature is asking, so the worker scopes adaptation and the
/// encoder cache per client instead of process-wide.
/// </summary>
public static class AsrClients
{
    /// <summary>Push-to-talk dictation and voice workflows: bias phrases,
    /// per-phrase deltas, the personal prior and the text passes all apply.</summary>
    public const string Dictation = "dictation";

    /// <summary>Meeting notes: its own vocabulary, no text passes, no prior.</summary>
    public const string Notes = "notes";
}

/// <summary>
/// The per-request personal prior (<c>profile</c> on <c>transcribe</c> /
/// <c>rescore</c> / <c>score</c>). Absent from a frame = no personal prior.
/// </summary>
/// <param name="You">Whether to apply the user's own language prior
/// (<c>p_you</c>, built and cached by the worker per <paramref name="DbPath"/>).</param>
/// <param name="DbPath">The transcript-history database the prior is built
/// from.</param>
/// <remarks>
/// Replaces the init-time <c>db_path</c> the old client deliberately never
/// sent: a process-global prior would have applied to secondary capture the
/// moment notes shared the process. Here it is dictation's per request, and a
/// notes frame simply omits it.
/// </remarks>
public sealed record AsrProfile(
    [property: JsonPropertyName("you")] bool You,
    [property: JsonPropertyName("db_path")] string? DbPath);

/// <summary>The <c>init</c> op — load a model. Only that: <c>{backend,
/// model_dir, hf_id, use_qnn}</c>, plus <c>allow_model_download</c> when the
/// caller sets it. Vocabulary and the personal prior travel
/// per request (the worker contract §3).</summary>
public sealed record AsrInitRequest
{
    [JsonPropertyName("op")]
    public string Op => "init";

    /// <summary>
    /// Which engine the worker should load. Omit to take its own default,
    /// which is <c>qnn</c> everywhere except macOS.
    /// </summary>
    /// <remarks>
    /// The values are the worker's, not ours, and one of them is misleading
    /// enough to be worth naming here: <c>mac</c> is not Apple-only. It selects
    /// <c>whisper_mac.WhisperMac</c>, which tries MLX, then faster-whisper,
    /// then openai-whisper and keeps the first that imports — so on an x64
    /// Windows box with faster-whisper installed, <c>mac</c> is the CPU
    /// recognizer. <c>qnn</c> imports <c>onnxruntime_qnn</c> at module scope
    /// and is therefore unloadable anywhere but Snapdragon. <c>stub</c> echoes
    /// a fixed sentence with no model at all, which is what the bridge's own
    /// tests drive.
    /// <para>
    /// Renaming these on our side would be a lie the worker cannot read, so
    /// they travel verbatim. <see cref="TranscriberBackends"/> holds the three.
    /// </para>
    /// </remarks>
    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    [JsonPropertyName("model_dir")]
    public string? ModelDir { get; init; }

    [JsonPropertyName("hf_id")]
    public string? HuggingFaceId { get; init; }

    [JsonPropertyName("use_qnn")]
    public bool UseQnn { get; init; }

    /// <summary>
    /// <c>false</c> tells the worker to load only what is on disk and to
    /// refuse, naming the missing files, when the model directory is
    /// incomplete. Null is omitted from the frame, which leaves the worker's
    /// default (downloads allowed). See
    /// <see cref="TranscriberOptions.AllowModelDownload"/>.
    /// </summary>
    [JsonPropertyName("allow_model_download")]
    public bool? AllowModelDownload { get; init; }

    // default_bias_phrases and db_path are deliberately absent. Both used to
    // be process-global in the worker, which is exactly what made sharing one
    // process between dictation and secondary capture unsafe: notes would have
    // inherited dictation's vocabulary and prior. Every request now carries
    // its own (AsrTranscribeRequest.BiasPhrases / .Profile) under a client
    // tag, and a word the user removed stops being sent on the very next
    // frame — an init-time list could never be unsent.
}

/// <summary>The <c>transcribe</c> op — one hold's audio, one utterance.</summary>
public sealed record AsrTranscribeRequest : IAsrOutOfBandPcm
{
    [JsonPropertyName("op")]
    public string Op => "transcribe";

    /// <summary>Base64 of int16 little-endian mono PCM. Null when the audio
    /// rides out of band on <see cref="OutOfBandPcm"/> instead — exactly one
    /// of the two must be set for the frame to carry <c>pcm_b64</c>.</summary>
    [JsonPropertyName("pcm_b64")]
    public string? PcmBase64 { get; init; }

    /// <summary>Raw PCM spliced into the frame as <c>pcm_b64</c> by
    /// <see cref="AsrFraming"/> — the full-take path, where base64 of the
    /// whole take as ONE managed string trips <c>Utf8JsonWriter</c>'s
    /// single-value cap. See <see cref="IAsrOutOfBandPcm"/>.</summary>
    [JsonIgnore]
    public byte[]? OutOfBandPcm { get; init; }

    /// <summary>The plain transcribe op has no system channel.</summary>
    [JsonIgnore]
    public byte[]? OutOfBandSystemPcm => null;

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; } = WorkflowAudio.SampleRateHz;

    /// <summary>Echoed back untouched. <c>mic</c> distinguishes these
    /// utterances from the meeting audio the same worker transcribes
    /// elsewhere.</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "mic";

    /// <summary>Which feature is asking — <see cref="AsrClients"/>. The worker
    /// scopes its encoder cache and adaptation by this.</summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = AsrClients.Dictation;

    /// <summary>Privacy-safe operation/take id for correlating a whole-take
    /// fallback with the meeting lifecycle. Null for ordinary callers.</summary>
    [JsonPropertyName("operation_id")]
    public string? OperationId { get; init; }

    /// <summary>Whether the worker applies its text passes (spoken aliases,
    /// declared casing) to this decode. Dictation: true. Notes: false — a
    /// meeting transcript must not carry the user's dictation shorthand.</summary>
    [JsonPropertyName("text_passes")]
    public bool TextPasses { get; init; } = true;

    /// <summary>Whether the worker's hallucination-phrase blocklist applies to
    /// this decode. Notes: true. Dictation: false — the same split as
    /// <see cref="TextPasses"/>, and for a symmetric reason.
    /// <para>On silence Whisper emits training-data pleasantries ("Thank
    /// you.", subtitle credits, subscribe pleas). Notes is background-queued
    /// and a fabricated sentence in a SAVED meeting document is the worst
    /// outcome, so it arms the blocklist. Dictation is interactive with the
    /// user watching, and eating a real word is the worst outcome, so it does
    /// not. Applicability is the CALLER's decision, which is why this is an
    /// explicit flag rather than inferred from voiced_fraction — inferring it
    /// was measured backwards on both paths (whisper_mac
    /// .blocked_hallucination documents that at length).</para>
    /// <para>Windows shipped without this: nothing set it, and the worker
    /// forwards it only when the engine's transcribe() accepts the parameter
    /// (asr_server.py probes the signature), which whisper_qnn's did not. A
    /// bare "Thank you." on silence therefore reached a live meeting
    /// transcript on 2026-09-08. Engine and caller were fixed together —
    /// either one alone is still silent.</para></summary>
    [JsonPropertyName("hallucination_guard")]
    public bool HallucinationGuard { get; init; }

    /// <summary>The personal prior for this decode, or null (omitted) for none.</summary>
    [JsonPropertyName("profile")]
    public AsrProfile? Profile { get; init; }

    /// <summary>The WHOLE vocabulary for this decode — the worker no longer
    /// prepends process defaults. Null omits the field.</summary>
    [JsonPropertyName("bias_phrases")]
    public string[]? BiasPhrases { get; init; }

    [JsonPropertyName("vocabulary_scene")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VocabularyScene { get; init; }

    /// <summary>
    /// Per-phrase boost, keyed by the phrase's INDEX in <see cref="BiasPhrases"/>
    /// (as a string — JSON object keys are strings and the worker parses them
    /// back). Continual-personal-learning setup 6b: the Boost rung's δ for a
    /// phrase the user corrected, resolved from the text-keyed sidecar
    /// against THIS request's list by the caller. Dictation sends none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null, the default, is omitted from the frame and the worker keeps its
    /// scalar δ for every phrase — today's request byte-for-byte, so an
    /// install with no corrections sends nothing new. The worker
    /// (<c>asr_server.py</c>'s <c>_remap_phrase_deltas</c>) drops an index
    /// past the end and clamps a value outside [-10, 6] rather than failing
    /// the utterance, so a stale map costs a boost, never a transcript.
    /// </para>
    /// <para>
    /// Meaningless without <see cref="BiasPhrases"/>: the indices are into
    /// that list. The transcriber never sets one without the other.
    /// </para>
    /// </remarks>
    [JsonPropertyName("phrase_deltas")]
    public Dictionary<string, double>? PhraseDeltas { get; init; }

    /// <summary>Above 1, beam search — which is what makes
    /// <see cref="BiasPhrases"/> take effect at all. Null omits the field and
    /// takes the worker's greedy default (the notes accurate pass today).</summary>
    [JsonPropertyName("beam_size")]
    public int? BeamSize { get; init; }

    /// <summary>
    /// loop-v2 (spec P7-2 §1): when true the reply and every <c>nbest[i]</c>
    /// carry <c>fired</c> — the bias phrases that completed on that hypothesis
    /// path. Null (omitted) keeps yesterday's engine call and reply byte-identical.
    /// </summary>
    [JsonPropertyName("return_fired")]
    public bool? ReturnFired { get; init; }

    /// <summary>
    /// loop-v2 (spec P7-2 §1.4): retain this clip's encoder output under an id
    /// the reply returns as <c>encoder_id</c>, for a later <c>score</c> /
    /// <c>rescore</c>. Null (omitted) = no caching, no <c>encoder_id</c> key.
    /// </summary>
    [JsonPropertyName("keep_encoder")]
    public bool? KeepEncoder { get; init; }

    /// <summary>
    /// A comparison hold: when true the worker ALSO
    /// runs an unassisted decode (no bias trie, no alias or casing pass) on the
    /// same clip and returns it as <c>baseline_text</c> beside <c>text</c> —
    /// one hold, one clip, two decodes. Null = absent on the
    /// wire, which an older worker and today's default path both read as
    /// "no comparison"; the reply then carries <c>baseline_text: null</c>.
    /// Dictation never sets it.
    /// </summary>
    [JsonPropertyName("compare_baseline")]
    public bool? CompareBaseline { get; init; }

    /// <summary>
    /// Which language to decode as — a Whisper code, <c>en</c> / <c>zh</c> /
    /// <c>ko</c>. <c>null</c> is auto-detect and is the default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>asr_server.py</c> has read <c>req["language"]</c> since it was
    /// written; this record once never wrote it, so every utterance was
    /// auto-detected. whisper-base's language head is weak on
    /// short quiet audio, and on 2026-08-19 that turned 1.0 s of gaussian hiss
    /// into Korean — <c>날씨였습니다.</c> typed into the user's document. The
    /// language is only half of that fix (the other half is the beam path's
    /// missing no-speech gate), but it is the half that decides whether a
    /// weakly-detected utterance comes back as English or as a foreign
    /// sentence.
    /// </para>
    /// <para>
    /// Null and not <c>"en"</c>, and that is a product decision rather than a
    /// placeholder: this user dictates in English AND Chinese, and Whisper does
    /// not fail on a language mismatch — it TRANSLATES. Pinning English would
    /// hand them fluent English they never said, which is the worse bug of the
    /// two. <c>asr_server.py</c>'s own comment records that measurement (WER
    /// 8.75 over a 1,657-utterance corpus).
    /// </para>
    /// <para>
    /// Null is omitted from the frame entirely by
    /// <see cref="AsrFraming.JsonOptions"/>'s
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/>, and the worker's
    /// <c>req.get("language")</c> reads an absent key and an explicit null the
    /// same way. So "auto" costs nothing on the wire.
    /// </para>
    /// </remarks>
    [JsonPropertyName("language")]
    public string? Language { get; init; }
}

/// <summary>The <c>shutdown</c> op.</summary>
public sealed record AsrShutdownRequest
{
    [JsonPropertyName("op")]
    public string Op => "shutdown";
}

/// <summary>Configuration evidence returned by a successful live Whisper
/// stream open. It describes speaker labelling only; the speech recognizer is
/// still the shared model named by the init frame.</summary>
public sealed record AsrLiveDiarizationInfo
{
    [JsonPropertyName("available")]
    public bool? Available { get; init; }

    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    [JsonPropertyName("model_dir")]
    public string? ModelDirectory { get; init; }

    [JsonPropertyName("embedding_model")]
    public string? EmbeddingModel { get; init; }

    [JsonPropertyName("embedding_provider")]
    public string? EmbeddingProvider { get; init; }

    [JsonPropertyName("corrector")]
    public string? Corrector { get; init; }

    [JsonPropertyName("corrector_provider")]
    public string? CorrectorProvider { get; init; }
}

/// <summary>
/// Every reply the worker sends. One record for all three ops — the worker
/// answers on one channel and fields it did not fill arrive null, which is
/// cheaper to read than three near-identical types.
/// </summary>
public sealed record AsrResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Why a decode returned nothing <em>on purpose</em> — silence,
    /// junk filter, hallucination blocklist. Null on an ordinary decode, and
    /// the difference between "they said nothing" and "we dropped it".</summary>
    [JsonPropertyName("dropped_reason")]
    public string? DroppedReason { get; init; }

    /// <summary>The unassisted decode of the same clip when the request sent
    /// <c>compare_baseline</c>; null when it did not run (absent on an older
    /// worker, null on today's worker without the flag). Paired with
    /// <see cref="BaselineRan"/> because an EMPTY baseline and a baseline that
    /// never ran must not read the same on a stage.</summary>
    [JsonPropertyName("baseline_text")]
    public string? BaselineText { get; init; }

    /// <summary>Whether the unbiased pass actually ran (asr_server.py
    /// <c>_transcribe_reply</c>). Null from a worker that predates the key.</summary>
    [JsonPropertyName("baseline_ran")]
    public bool? BaselineRan { get; init; }

    /// <summary>Whether <c>init</c> got the NPU. Logged, not acted on — the
    /// worker falls back by itself.</summary>
    [JsonPropertyName("on_npu")]
    public bool? OnNpu { get; init; }

    /// <summary>What the decode did, as the worker counted it. Null on an
    /// <c>init</c> or <c>shutdown</c> reply, which decode nothing.</summary>
    [JsonPropertyName("stats")]
    public AsrDecodeStats? Stats { get; init; }

    /// <summary>Set by <c>stream_start</c>'s reply: the id every later push
    /// and cancel must carry. Null on every other op. (Re-grafted with the
    /// live-preview stream API during the 2026-08-21 merge — main's parallel
    /// protocol work had evolved without it.)</summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    /// <summary>
    /// Set by <c>stream_start</c>: the engine the worker actually opened.
    /// <c>whisper</c> for the product stream. This is the worker's answer, not
    /// merely the engine the caller requested, so release logs can prove the
    /// live stream really opened without recording speech or transcript text.
    /// </summary>
    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    /// <summary>Actual per-turn Whisper beam selected by the worker. Null for
    /// rolling-stable, dictation-only, older-worker, and failed-open replies.</summary>
    [JsonPropertyName("live_beam")]
    public int? LiveBeam { get; init; }

    /// <summary>Actual live speaker-labelling configuration selected by the
    /// worker. Null from dictation-only/older workers or a failed open.</summary>
    [JsonPropertyName("live_diarization")]
    public AsrLiveDiarizationInfo? LiveDiarization { get; init; }

    /// <summary>
    /// Set by <c>stream_push</c>'s reply: the recogniser closed a segment on
    /// this push. <b>The single most load-bearing bool on this record.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worker answers with the CURRENT SEGMENT ONLY, and when this is true
    /// it has ALREADY called <c>rec.reset(stream)</c> — so
    /// <see cref="Text"/> on this reply is the last and only chance to keep
    /// those words. A host that ignores this field shows the final segment of
    /// a hold and silently discards every segment before it.
    /// </para>
    /// <para>
    /// Measured, the test platform 2026-08-22: identical decodes read
    /// last-reply-only score <b>100% WER with every clip empty</b>, and read
    /// with this field honoured score <b>12–14%</b>. The worker has always
    /// sent <c>is_endpoint</c>; this record simply never declared it, so
    /// <c>System.Text.Json</c> dropped it. See
    /// <see cref="Dictation.Services"/>'s preview accumulator, which is
    /// the type that consumes it.
    /// </para>
    /// <para>
    /// Nullable because every other op's reply omits it, and "the worker did
    /// not say" must not read as "no endpoint" for a caller inspecting a
    /// <c>transcribe</c> reply.
    /// </para>
    /// </remarks>
    [JsonPropertyName("is_endpoint")]
    public bool? IsEndpoint { get; init; }

    /// <summary>
    /// Token-weighted mean raw log-probability of the decode (CPL Setup 2,
    /// <c>asr_server.py</c>). Null from a worker that predates the field, from
    /// a greedy decode that reported none, and for a non-finite value the
    /// worker already folded to null. "Not measured", never zero.
    /// </summary>
    [JsonPropertyName("avg_logprob")]
    public double? AvgLogprob { get; init; }

    /// <summary>
    /// Every hypothesis that survived the beam, best first (CPL Setup 2). Null
    /// from an older worker or a greedy decode (<c>beam_size</c> 1 reports no
    /// alternatives); the Notes client reads the same array through
    /// <c>NotesWorkerClient.ParseTranscribeReply</c>, this is the typed twin
    /// the dictation path uses.
    /// </summary>
    [JsonPropertyName("nbest")]
    public AsrHypothesis[]? NBest { get; init; }

    /// <summary>
    /// loop-v2: the bias phrases that completed on the winning path
    /// (= <c>nbest[0].fired</c>), host-indexed into the request's
    /// <c>bias_phrases</c>. Present only when the request sent
    /// <c>return_fired</c>; null = not measured (greedy path, gate-dropped
    /// decode, no trie, or an engine that cannot report it); empty = a trie was
    /// live and nothing completed.
    /// </summary>
    [JsonPropertyName("fired")]
    public AsrFiredPhrase[]? Fired { get; init; }

    /// <summary>loop-v2: the cached encoder output id when the request sent <c>keep_encoder</c>.</summary>
    [JsonPropertyName("encoder_id")]
    public string? EncoderId { get; init; }

    /// <summary>loop-v2 (<c>rescore</c> only): whether the encoder run was skipped for a cached entry.</summary>
    [JsonPropertyName("encoder_reused")]
    public bool? EncoderReused { get; init; }

    /// <summary>loop-v2 (<c>rescore</c> only): the worker's own timing (spec P7-2 §5.1).</summary>
    [JsonPropertyName("timing_ms")]
    public AsrTiming? TimingMs { get; init; }

    /// <summary>
    /// <c>stream_push</c> on the whisper live engine: the caption lines the
    /// worker committed on this push (speaker-labelled, immutable once
    /// emitted). Null on every other op and on the sherpa engine.
    /// </summary>
    [JsonPropertyName("commits")]
    public AsrCommittedLine[]? Commits { get; init; }

    /// <summary>
    /// <c>stream_push</c> / <c>stream_finish</c> on the whisper live engine:
    /// incremental speaker corrections since the previous drain, same cursor
    /// discipline as <see cref="Commits"/>. Each entry names the commit-log
    /// index being corrected and the label the diarizer now believes.
    /// </summary>
    /// <remarks>
    /// Declared the day the worker started emitting it, because this record's
    /// history is exactly the failure mode an undeclared member produces: the
    /// worker sends the field, <c>System.Text.Json</c> silently drops it, and
    /// the app renders labels the diarizer has already retracted — see
    /// <see cref="IsEndpoint"/>, whose comment documents the measured cost of
    /// that class of drop. Null from an older worker and from every non-stream
    /// op; "the worker did not say", never "no relabels".
    /// </remarks>
    [JsonPropertyName("relabels")]
    public AsrRelabel[]? Relabels { get; init; }

    /// <summary>The rolling beam-search transcript that is safe to keep. It is
    /// deliberately separate from <see cref="Text"/>, which remains the fast,
    /// mutable caption hypothesis.</summary>
    [JsonPropertyName("stable_text")]
    public string? StableText { get; init; }

    /// <summary>Timestamped form of <see cref="StableText"/>, when supplied by
    /// the notes stream. The worker only extends this prefix.</summary>
    [JsonPropertyName("stable_timed_transcript")]
    public string? StableTimedTranscript { get; init; }

    /// <summary>
    /// Structured form of <see cref="StableTimedTranscript"/>: the rolling
    /// stable result as timed, speaker-labelled spans.
    /// </summary>
    /// <remarks>
    /// The worker has been sending this the whole time and this record never
    /// named it, so <c>System.Text.Json</c> threw it away — the silent-drop
    /// failure this record's <see cref="IsEndpoint"/> comment warns about, for
    /// the sixth time at this boundary. It is now pinned by the
    /// <c>wire_contract.json</c> fixture and the WireContract test, which is
    /// the mechanism that makes drop number seven a red build instead of a
    /// missing feature. Null from an older worker and on non-stream ops.
    /// </remarks>
    [JsonPropertyName("stable_spans")]
    public AsrStableSpan[]? StableSpans { get; init; }

    /// <summary>Audio time through which the rolling result is immutable.</summary>
    [JsonPropertyName("stable_through_seconds")]
    public double? StableThroughSeconds { get; init; }
}

/// <summary>
/// One incremental speaker correction off a stream reply: "commit line
/// <see cref="Index"/> is actually <see cref="Speaker"/>".
/// </summary>
/// <remarks>
/// Both members nullable on purpose: a JSON <c>null</c> against a
/// non-nullable member throws out of the frame read, and a merely-odd entry
/// must cost that entry, never the reply. A
/// consumer skips an entry missing either half. Mic lines are pinned "You" and
/// the worker never relabels them — consumers may enforce that defensively.
/// </remarks>
public sealed record AsrRelabel
{
    /// <summary>The worker's commit-log index of the line being corrected —
    /// the same index space as <see cref="AsrCommittedLine.Index"/>.</summary>
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; init; }
}

/// <summary>One timed, speaker-labelled span of the rolling stable transcript
/// (<c>stable_spans[]</c>). All members nullable: an entry the worker could
/// not fully fill must not fail the reply around it.</summary>
public sealed record AsrStableSpan
{
    /// <summary>Audio-time start, seconds from stream start.</summary>
    [JsonPropertyName("start")]
    public double? Start { get; init; }

    /// <summary>Audio-time end, seconds from stream start.</summary>
    [JsonPropertyName("end")]
    public double? End { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; init; }

    /// <summary>The language the span's window decoded as (a Whisper code),
    /// or null when the engine reported none — emitted on every span by the
    /// rolling-stable decoder per the fixture's own note.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }
}

/// <summary>One committed live-caption line from the whisper stream engine.</summary>
public sealed record AsrCommittedLine
{
    [JsonPropertyName("speaker")]
    public string? Speaker { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("channel")]
    public string? Channel { get; init; }

    /// <summary>
    /// Audio-time start of the SPEECH this line transcribes: seconds from
    /// stream start (the worker's per-channel sample clock), not a wall
    /// clock. Null when the worker did not measure a span for this commit
    /// (or is older than the field). Together with <see cref="End"/> this is
    /// the line's second timestamp — when it was SAID, as opposed to
    /// <see cref="CommittedAt"/>, which is when it FINALIZED.
    /// </summary>
    [JsonPropertyName("start")]
    public double? Start { get; init; }

    /// <summary>Audio-time end of the speech span, seconds from stream
    /// start. Null when unmeasured — same rule as <see cref="Start"/>.</summary>
    [JsonPropertyName("end")]
    public double? End { get; init; }

    /// <summary>
    /// When the worker committed this line: unix epoch seconds
    /// (<c>time.time()</c>), fractional.
    /// </summary>
    /// <remarks>
    /// Sent on every commit since the feed existed and dropped here for want
    /// of a declaration — the exact silent-drop failure
    /// <see cref="AsrResponse.IsEndpoint"/>'s comment documents. Nullable
    /// because an older worker's absence must read as "did not say", not as
    /// the epoch. <see cref="CommittedAtUtc"/> is the parsed reading.
    /// </remarks>
    [JsonPropertyName("committed_at")]
    public double? CommittedAt { get; init; }

    /// <summary>
    /// The worker's commit-log index for this line, when it sends one. Today's
    /// worker does not (commits are implicitly indexed by arrival order and
    /// relabels point into that shared log); declared ahead of it so the day
    /// the field appears on the wire it is preferred automatically instead of
    /// silently dropped. Null = number by arrival order.
    /// </summary>
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    /// <summary><see cref="CommittedAt"/> as a timestamp, or null when the
    /// worker did not say or sent something unreadable. The range guard is
    /// year-9999 sanity: a garbage epoch must cost the timestamp, never throw
    /// out of a property read.</summary>
    [JsonIgnore]
    public DateTimeOffset? CommittedAtUtc =>
        CommittedAt is { } s && double.IsFinite(s) && s > 0 && s < 253_402_300_799
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(s * 1000))
            : null;
}

/// <summary>One N-best hypothesis on the wire. Only <see cref="Text"/> is carried past the bridge; the scores are here so the frame round-trips whole.</summary>
public sealed record AsrHypothesis
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("score")]
    public double? Score { get; init; }

    [JsonPropertyName("avg_logprob")]
    public double? AvgLogprob { get; init; }

    /// <summary>loop-v2: the phrases that completed on this hypothesis path (null = not measured).</summary>
    [JsonPropertyName("fired")]
    public AsrFiredPhrase[]? Fired { get; init; }
}

/// <summary>
/// The <c>stream_start</c> op — open a live-preview stream.
/// </summary>
/// <remarks>
/// <para>
/// <c>engine</c> selects which recogniser draws the preview. The product sends
/// <see cref="WhisperEngine"/> and re-decodes the selected shared Whisper
/// session periodically. A development harness may still request another
/// engine explicitly, but a failed Whisper start is returned as a failure —
/// the worker never silently changes models.
/// </para>
/// <para>
/// <b>Dictation uses the shared Whisper engine first.</b> A Snapdragon X Elite measurement
/// on 2026-08-23 found first visible words at about 0.65 s through the existing
/// QNN Whisper stream, while the bilingual sherpa transducer reached 1.76 s
/// p50 on retained holds and disagreed substantially more often with the final
/// result. This does not load another model: dictation, workflows, and notes
/// share one worker and one selected Whisper model. The worker's priority lock
/// admits a released dictation hold ahead of queued caption decodes. If that
/// model cannot open a live stream, the caller gets an explicit unavailable
/// state instead of output from an undeclared recogniser.
/// </para>
/// <para>
/// <b>On the quality figure this comment used to lead with.</b> It read:
/// "measured on this device (FLEURS-100, 2026-08-18) the bilingual zipformer
/// scores en WER 80.4% / zh CER 50.2% against the accurate engine's 6.3% /
/// 11.4%." That number is <b>retained, not endorsed</b>. Re-measuring on the
/// the test platform (2026-08-22, the synthetic clip corpus of
/// <c>the platform measurements</c> §9.5) put the same checkpoint at
/// <b>21.1% overall and 14.6% on clips carrying no specialized term</b>, and
/// showed that reading the stream without honouring
/// <c>AsrResponse.IsEndpoint</c> — which is what this protocol did until the
/// field was added — scores <b>100% with every clip empty</b> regardless of
/// model. The readout policy dominates the model by a wide margin, so 80.4%
/// cannot be attributed to the recogniser. It is left in place because it was
/// taken on a different corpus (FLEURS, real speech) and re-running FLEURS
/// with the accumulating readout is the measurement that would actually
/// retire it.
/// </para>
/// <para>
/// <b>The preview is never the transcript.</b> <c>ops_stream</c>'s own header
/// says so — "Do not let a caller treat the return value as final text" — and
/// nothing here does: the words that reach the user's document come from a
/// separate <c>transcribe</c> after the key is released.
/// </para>
/// </remarks>
public sealed record AsrStreamStartRequest
{
    /// <summary>The periodically re-decoded QNN whisper session.</summary>
    public const string WhisperEngine = "whisper";

    [JsonPropertyName("op")]
    public string Op => "stream_start";

    /// <summary>
    /// Which recogniser draws the preview. <b>Defaults to
    /// <see cref="WhisperEngine"/>, which is what this request has always
    /// sent</b>. Dictation and notes both use this shared-model path; a failed
    /// Whisper start remains a Whisper failure.
    /// </summary>
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = WhisperEngine;

    [JsonPropertyName("sample_rate")]
    public int SampleRate { get; init; } = 16000;

    [JsonPropertyName("model_dir")]
    public string? ModelDirectory { get; init; }

    /// <summary>Privacy-safe meeting take id for correlating stream-open and
    /// final-pass evidence. Null for dictation and older callers.</summary>
    [JsonPropertyName("operation_id")]
    public string? OperationId { get; init; }

    /// <summary>Which feature owns this stream — <see cref="AsrClients"/>. The
    /// worker keeps a registry keyed by the returned <c>stream_id</c>, so
    /// dictation preview and notes caption sessions coexist independently.</summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = AsrClients.Dictation;

    /// <summary>The whisper live engine conditions on these via its initial
    /// prompt; the sherpa engine ignores them. Null omits the field.</summary>
    [JsonPropertyName("bias_phrases")]
    public string[]? BiasPhrases { get; init; }

    /// <summary>
    /// Expected participant count, INCLUDING the local user. Null (omitted) =
    /// unknown, which is byte-for-byte yesterday's frame.
    /// </summary>
    /// <remarks>
    /// The worker has read this key since 2026-08-21 (its known-k diarization
    /// path: <c>remote_k = max(1, n-1)</c>, the local user being on the mic
    /// channel) — only the host never sent it, leaving the worker to fall back
    /// to NME cluster-count estimation and an env-var test hook
    /// (<c>DICTATION_NOTES_NUM_SPK</c>). The count is a hint, not a contract: a
    /// wrong value degrades clustering, it never fails the stream.
    /// </remarks>
    [JsonPropertyName("num_speakers")]
    public int? NumSpeakers { get; init; }

    /// <summary>Languages accepted for this meeting. A singleton lets the
    /// worker pin every short live window instead of trusting Whisper's
    /// unstable per-window auto-detection.</summary>
    [JsonPropertyName("languages")]
    public string[]? Languages { get; init; }

    [JsonPropertyName("rolling_stable")]
    public bool? RollingStable { get; init; }

    /// <summary>The app the dictation is going into, as the learning
    /// layer's scene (<c>terminal</c>, <c>wechat</c> or <c>other</c>): the
    /// worker reads that scene's learned words into a dictation preview's
    /// prompt, as it does for the final <c>transcribe</c>. Null omits the
    /// field; notes never set it.</summary>
    [JsonPropertyName("vocabulary_scene")]
    public string? VocabularyScene { get; init; }

    /// <summary>Beam size for the LIVE per-turn decodes (each closed voice
    /// turn decoded once, full context, by the whisper engine). 4 = the
    /// notes product config (two-pass decision, 2026-08-30: the streaming
    /// transcript is per-turn whisper-large-v3-turbo decodes; the rolling
    /// windowed pass is off). Null = worker default (1, greedy legacy).</summary>
    [JsonPropertyName("live_beam")]
    public int? LiveBeam { get; init; }

    [JsonPropertyName("stable_beam_size")]
    public int? StableBeamSize { get; init; }

    [JsonPropertyName("stable_window_seconds")]
    public double? StableWindowSeconds { get; init; }

    [JsonPropertyName("stable_hop_seconds")]
    public double? StableHopSeconds { get; init; }

    [JsonPropertyName("stable_lookahead_seconds")]
    public double? StableLookaheadSeconds { get; init; }
}

/// <summary>
/// The <c>stream_push</c> op — hand over the audio captured since the last
/// push and take whatever hypothesis is ready.
/// </summary>
/// <remarks>
/// Partials are PULLED, never pushed: the worker writes exactly one frame per
/// frame read and there is no request id, so an unsolicited frame would desync
/// the pipe permanently and silently. The current hypothesis therefore rides
/// back as the reply to the next push — which is also why a push must be sent
/// on a cadence even when there is little audio to send.
/// </remarks>
public sealed record AsrStreamPushRequest
{
    [JsonPropertyName("op")]
    public string Op => "stream_push";

    /// <summary>Required — every stream op names its stream (v2 contract).</summary>
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("pcm_b64")]
    public string? PcmBase64 { get; init; }

    /// <summary><c>mic</c> (the local user) or <c>sys</c> (the loopback, the
    /// remote party) on the whisper live engine; null omits it and the sherpa
    /// preview never sets it.</summary>
    [JsonPropertyName("channel")]
    public string? Channel { get; init; }
}

/// <summary>The <c>stream_finish</c> op — close the stream and take its final
/// text. Meeting notes only; dictation always cancels (see
/// <see cref="AsrStreamCancelRequest"/>).</summary>
public sealed record AsrStreamFinishRequest
{
    [JsonPropertyName("op")]
    public string Op => "stream_finish";

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }
}

/// <summary>
/// The <c>stream_cancel</c> op — abandon the stream.
/// </summary>
/// <remarks>
/// Dictation cancels rather than finishing, always. <c>stream_finish</c>
/// returns the streaming engine's own text, and this host must never be able
/// to insert that: the accurate pass is a separate <c>transcribe</c> on the
/// released audio. Cancelling also releases the worker's engine lock, so the
/// preview cannot still be decoding while the transcript the user waits for
/// queues behind it.
/// </remarks>
public sealed record AsrStreamCancelRequest
{
    [JsonPropertyName("op")]
    public string Op => "stream_cancel";

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }
}

/// <summary>
/// The <c>stats</c> block of a <c>transcribe</c> reply. Only the fields this
/// host acts on are declared — the worker also sends chunk, token and timing
/// counters, and System.Text.Json drops what is not named here.
/// </summary>
public sealed record AsrDecodeStats
{
    /// <summary>Time this decode waited behind another user of the shared
    /// Whisper engine. This is worker-side contention, distinct from the
    /// caller's full request latency.</summary>
    [JsonPropertyName("waited_ms")]
    public double? WaitedMilliseconds { get; init; }

    /// <summary>
    /// The language the decode actually ran under — detected, since the host
    /// sends none by default. <c>whisper_qnn.py</c> fills it from the language
    /// token that sits right after SOT, so it is what the model DECIDED and not
    /// what anyone asked for.
    /// </summary>
    /// <remarks>
    /// Carried because a transcript nobody can explain is a support call and a
    /// transcript labelled <c>ko</c> is a bug report. This is the field that
    /// lets a surface say "that came back as Korean" about the 2026-08-19
    /// defect instead of showing a sentence from nowhere.
    /// <para>
    /// Null is normal, not an error: the worker omits the key when no language
    /// token was emitted at all — which is what the same silent audio produced
    /// on the greedy path, where the no-speech gate returned before any
    /// language existed.
    /// </para>
    /// </remarks>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary>How many chunks the decode ran at all.</summary>
    [JsonPropertyName("chunks")]
    public int? Chunks { get; init; }

    /// <summary>
    /// How many of those chunks the non-speech gate threw away before a single
    /// beam step was spent.
    /// </summary>
    [JsonPropertyName("no_speech_chunks")]
    public int? NoSpeechChunks { get; init; }

    /// <summary>
    /// Which of the gate's two rules fired first: <c>no_speech</c> (the model
    /// put its mass on the no-captions token) or <c>language</c> (it could not
    /// commit to a language).
    /// </summary>
    [JsonPropertyName("no_speech_reason")]
    public string? NoSpeechReason { get; init; }

    /// <summary>
    /// P(no-speech) on the chunk that fired the gate — the model's own softmax
    /// mass on <c>&lt;|nospeech|&gt;</c> at the SOT step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Declared because the value already existed and was being thrown
    /// away.</b> <c>beam_decode.py</c> computes it, writes it into the chunk's
    /// stats, and the C# side did not name it — so
    /// <c>System.Text.Json</c> dropped it, exactly as this record's own header
    /// comment warns. The result was a log that could say WHICH rule refused an
    /// utterance and never how close the call was.
    /// </para>
    /// <para>
    /// The margin is the whole diagnosis. The measured Korean case that these
    /// thresholds were tuned against read P(no-speech)=0.650 against a 0.75
    /// bar, and the worst REAL speech in the device sweep read 0.471 — a
    /// rejection at 0.48 and a rejection at 0.96 are a bug and a correct call,
    /// and "gate rejected 4 of 4 chunks: no_speech" is the same sentence for
    /// both.
    /// </para>
    /// </remarks>
    [JsonPropertyName("no_speech_prob")]
    public double? NoSpeechProb { get; init; }

    /// <summary>
    /// P(the language token the decode committed to), on the same chunk.
    /// </summary>
    /// <remarks>
    /// The other half of the gate's evidence, and the one that names the
    /// 2026-08-19 defect: a decode that could not commit to a language is the
    /// state that produced Korean text from English speech. Reported by the
    /// worker even when the language was pinned and the rule therefore did not
    /// run — see <see cref="LanguageForced"/>, which says which of those two
    /// readings this is.
    /// </remarks>
    [JsonPropertyName("language_prob")]
    public double? LanguageProb { get; init; }

    /// <summary>
    /// Whether the host pinned the decode's language rather than letting the
    /// model choose.
    /// </summary>
    /// <remarks>
    /// Carried because it changes what <see cref="LanguageProb"/> MEANS. With
    /// a pin, a low reading is the model disagreeing with the pin and is not
    /// evidence of non-speech (real Chinese with <c>&lt;|en|&gt;</c> forced
    /// measures P(en)=0.0023 on this device); without one, the same number is
    /// the gate's second rule firing. One line has to be readable as both.
    /// </remarks>
    [JsonPropertyName("language_forced")]
    public bool? LanguageForced { get; init; }

    /// <summary>
    /// A one-clause explanation of a silent result, or <c>null</c> when the
    /// gate is not the reason there are no words.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "The worker heard nothing" and "the worker decided this was not speech"
    /// look identical from outside and are diagnosed completely differently.
    /// The 2026-08-19 defect was the gate NOT firing; every fix for it can fail
    /// the opposite way, by eating a real sentence, and that failure is
    /// invisible unless the rejection says so. A user reporting "I spoke and
    /// nothing appeared" is answerable from one log line or not at all.
    /// </para>
    /// <para>
    /// Null rather than a cheerful "no chunks rejected": this is only ever
    /// appended to a line that has already established there are no words, so
    /// saying nothing is the right output when the gate is not to blame.
    /// </para>
    /// </remarks>
    public string? GateExplanation()
    {
        if (NoSpeechChunks is not > 0)
        {
            return null;
        }

        var reason = NoSpeechReason is { Length: > 0 } r && r.Trim().Length > 0
            ? r.Trim()
            : "unstated";

        // The denominator matters more than it looks. "1 of 4" is the gate
        // doing its job on a pause; "4 of 4" on a hold the user swears they
        // spoke through is the gate eating the utterance, and only the ratio
        // separates them.
        var ratio = Chunks is > 0
            ? $"gate rejected {NoSpeechChunks} of {Chunks} chunks: {reason}"
            : $"gate rejected {NoSpeechChunks} chunks: {reason}";

        // And the margin, because a category is not a measurement. Both
        // readings are appended when the worker sent them, in the invariant
        // culture so a machine with a comma decimal separator does not print
        // "0,650" into a field somebody greps for.
        var margins = new List<string>(3);
        if (NoSpeechProb is { } ns)
        {
            margins.Add(
                $"P(no-speech) {ns.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (LanguageProb is { } lang)
        {
            margins.Add(
                $"P(language) {lang.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}"
                + (LanguageForced == true ? " against a pinned language" : string.Empty));
        }

        return margins.Count == 0 ? ratio : $"{ratio}, {string.Join(", ", margins)}";
    }
}
