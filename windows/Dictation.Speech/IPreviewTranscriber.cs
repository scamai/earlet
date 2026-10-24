using System;
using System.Threading;
using System.Threading.Tasks;

namespace Dictation.Speech;

/// <summary>
/// The three operations a live preview needs from a recogniser: open a stream,
/// push the audio captured since the last push, and abandon the stream.
/// </summary>
/// <remarks>
/// <para>
/// <b>A seam, so the gate in front of it is testable and the worker is not.</b>
/// <see cref="AsrWorkerTranscriber"/> is the one implementation and it is
/// sealed over a Python process, a pipe and a model load — so a test that
/// wanted to assert "no stream was opened" had nothing to assert against, and
/// the one assertion that matters about the preview is exactly that one: a
/// password spoken into a password box must not reach a recogniser. Same rule
/// <c>ISecureFieldProbe</c> states for the platform below it.
/// </para>
/// <para>
/// Deliberately NOT folded into <see cref="ITranscriber"/>. That interface is
/// "audio in, words out" and every caller of it wants the accurate pass;
/// <see cref="NullTranscriber"/> implements it and has no stream to offer. A
/// build whose shared <c>ITranscriber</c> is not a streaming worker simply
/// shows the pill it showed before the preview existed, and the null check
/// that produces that behaviour is a cast rather than a new implementation
/// nobody would maintain.
/// </para>
/// <para>
/// Every method here takes the worker's own wire gate, so the preview can never
/// desync the pipe the transcript rides on. <b>Finishing a stream is not on
/// this interface at all</b>: <c>stream_finish</c> would return the streaming
/// engine's own text, and the preview's host must never be able to insert that
/// — the words that reach the document come from a separate accurate decode on
/// the released audio. The only way to end a preview is to cancel it.
/// </para>
/// </remarks>
public interface IPreviewTranscriber
{
    /// <summary>
    /// Open a live-preview stream, or return <c>null</c> when this build
    /// cannot. Never throws: a preview that could not start costs a hypothesis,
    /// not a dictation.
    /// </summary>
    Task<string?> StreamStartAsync(CancellationToken ct = default);

    /// <summary>
    /// Hand over the audio captured since the last push and take whatever
    /// hypothesis is ready.
    /// </summary>
    /// <returns>
    /// The reply, or <c>null</c> when the push did not complete (the worker is
    /// busy, the wire gate was held, the call threw). A null is "no answer",
    /// never "no words" — a caller must leave what it is showing alone.
    /// </returns>
    Task<PreviewHypothesis?> StreamPushAsync(
        string streamId, ReadOnlyMemory<short> pcm, CancellationToken ct = default);

    /// <summary>Abandon a preview stream and release the worker's engine lock.</summary>
    Task StreamCancelAsync(string streamId, CancellationToken ct = default);
}

/// <summary>
/// One <c>stream_push</c> reply: the hypothesis, and whether it is the last
/// word on the segment it belongs to.
/// </summary>
/// <param name="Text">
/// The recogniser's text for the CURRENT SEGMENT — not the hold. May be empty,
/// which is a real answer ("nothing here"), distinct from a <c>null</c> reply.
/// </param>
/// <param name="IsEndpoint">
/// The recogniser closed the segment on this push and has already reset its
/// stream. <b><paramref name="Text"/> is the only chance to keep these
/// words</b>; the next push answers for a fresh segment.
/// </param>
/// <remarks>
/// This is a record rather than a bare <c>string?</c> because the bare string
/// is what the interface returned before, and it made the correct use of this
/// API unexpressible: a caller could not tell a segment boundary from a
/// mid-segment revision, so it could only show the newest hypothesis and drop
/// everything else. That defect measured 100% WER with every clip empty.
/// <c>Dictation.Services</c>'s preview accumulator is the consumer that
/// gets it right, and it exists to be tested in isolation.
/// </remarks>
public readonly record struct PreviewHypothesis(string? Text, bool IsEndpoint);
