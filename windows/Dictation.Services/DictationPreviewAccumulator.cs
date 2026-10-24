namespace Dictation.Services;

/// <summary>
/// Assembles a streaming recogniser's per-segment hypotheses into the one
/// string a live preview shows.
/// </summary>
/// <remarks>
/// <para>
/// <b>This class exists because the wire contract makes losing text the
/// default.</b> <c>ops_stream.op_stream_push</c> answers with
/// <c>rec.get_result(stream)</c>, which is the hypothesis for the CURRENT
/// SEGMENT ONLY, and when the recogniser reports an endpoint it immediately
/// calls <c>rec.reset(stream)</c>. The segment's text is gone at that moment
/// unless the caller kept it. The worker's own docstring states the obligation
/// — "the host should append <c>text</c> to what it is showing" — and the
/// obligation is this type.
/// </para>
/// <para>
/// <b>Measured, on this corpus, 2026-08-22 (Snapdragon X2 Elite).</b> A caller
/// that keeps only the last reply reads a WER of <b>100%</b> on ordinary
/// speech, with EVERY clip coming back empty, because a clip that ends in
/// silence fires an endpoint and the final segment is therefore blank. The
/// same decodes, accumulated the way this class accumulates them, read
/// <b>12–14%</b>. The gap is not the model and no model swap closes it:
/// </para>
/// <code>
///   1 sentence,  ~6 s   naive 100.0% (66/66 empty)   accumulated 14.1%
///   3 sentences, ~17 s  naive 100.0% (22/22 empty)   accumulated 13.6%
///   5 sentences, ~28 s  naive 100.0% (13/13 empty)   accumulated 12.2%
/// </code>
/// <para>
/// A recorded "en WER 80.4%" for this recogniser is believed to be that bug
/// rather than the recogniser. That belief is NOT yet settled — it was
/// reproduced on a different corpus from the one that produced 80.4% — so
/// nothing in the tree that quotes 80.4% has been edited. See
/// <c>C:/hw/streaming-preview/handoff.md</c> §5 and §7.
/// </para>
/// <para>
/// <b>Append-only by construction.</b> Committed text is never rewritten, so
/// the preview can only grow while a hold runs. That is what makes it safe to
/// draw: a surface that retracts words the user watched appear reads as the
/// product malfunctioning, which is exactly what
/// <c>stream_repetition.py</c>'s header reports happening once already.
/// </para>
/// <para>
/// Pure and synchronous on purpose — no worker, no audio, no clock. The one
/// rule worth proving is provable on any machine.
/// </para>
/// </remarks>
public sealed class DictationPreviewAccumulator
{
    private readonly List<string> _committed = new();
    private string _partial = string.Empty;

    /// <summary>
    /// Take one <c>stream_push</c> reply and answer the text to display.
    /// </summary>
    /// <param name="text">
    /// The reply's hypothesis for the current segment. <c>null</c> is the
    /// worker's "nothing to report" and is NOT the same as an empty
    /// string — see the endpoint handling below.
    /// </param>
    /// <param name="isEndpoint">
    /// Whether the recogniser reported an endpoint on this reply. When it did,
    /// the worker has ALREADY reset its stream: this is the only moment
    /// <paramref name="text"/> can be banked, and skipping it loses the segment
    /// permanently.
    /// </param>
    /// <returns>The full preview text: everything committed, plus the live tail.</returns>
    public string Observe(string? text, bool isEndpoint)
    {
        var trimmed = text?.Trim() ?? string.Empty;

        if (isEndpoint)
        {
            // Bank it, even though the caller may have shown it already as a
            // partial: the worker's stream is reset, so a later push will
            // never return these words again.
            //
            // An endpoint carrying NO text is normal and must not append an
            // empty entry — silence at the end of a hold fires an endpoint of
            // its own, and a blank entry would render as a double space.
            if (trimmed.Length > 0)
            {
                _committed.Add(trimmed);
            }

            _partial = string.Empty;
            return Text;
        }

        // Not an endpoint: this is the mutable tail. A null reply leaves the
        // tail ALONE rather than clearing it — a dropped or timed-out push
        // must not blank text the user is reading. An empty (non-null) string
        // is a real answer and does clear it.
        if (text is not null)
        {
            _partial = trimmed;
        }

        return Text;
    }

    /// <summary>Everything committed so far, plus the live tail.</summary>
    public string Text =>
        _partial.Length > 0
            ? (_committed.Count > 0 ? string.Join(' ', _committed) + ' ' + _partial : _partial)
            : string.Join(' ', _committed);

    /// <summary>Whether anything at all has been recognised this hold.</summary>
    public bool HasText => _partial.Length > 0 || _committed.Count > 0;

    /// <summary>
    /// Forget everything. Called when a hold ends, so the next hold cannot
    /// inherit the previous one's words.
    /// </summary>
    public void Reset()
    {
        _committed.Clear();
        _partial = string.Empty;
    }
}
