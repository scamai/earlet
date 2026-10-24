namespace Dictation.Speech;

/// <summary>
/// Turns the speech worker's stream reply into the one live line a
/// single-speaker caller asked for.
/// </summary>
/// <remarks>
/// <para>
/// The dictation live preview and meeting notes share one worker op. On the
/// whisper engine, <c>stream_push</c> answers with
/// <c>ops_stream_whisper._Session.display()</c> — the NOTES rendering: every
/// committed line plus the live tail, each prefixed with its speaker, joined
/// by newlines:
/// </para>
/// <code>
/// [You] so what I was saying about the migration
/// [You] and the rollback plan is
/// </code>
/// <para>
/// Dictation is single-speaker by definition — it is your voice going to your
/// own cursor — so the tag names the only person who could have said it, and
/// it costs about six of the roughly twenty-nine characters the island has
/// room for. The committed lines are wrong here too: <c>DictationPreview
/// Accumulator</c> already accumulates the hold on this side, so returning
/// history as well counts it twice.
/// </para>
/// <para>
/// Somebody met the symptom before and patched it there:
/// <c>DictationPreviewCaption.IsStandaloneYou</c> suppresses a caption that is
/// nothing but the word "You", which is what this produces from a slice with
/// no speech yet. That guard stays — it is cheap and it is about a different
/// moment — but the tag is removed at the boundary that knows there is only
/// one speaker.
/// </para>
/// </remarks>
public static class PreviewText
{
    /// <summary>
    /// The live tail of <paramref name="reply"/>, without its speaker tag.
    /// </summary>
    /// <remarks>
    /// The LAST line, because that is the one still being spoken; earlier
    /// lines are settled text the caller is already holding.
    /// </remarks>
    public static string LiveTail(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

        var lines = reply.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = StripSpeaker(lines[i]).Trim();
            if (line.Length > 0) return line;
        }

        return string.Empty;
    }

    /// <summary>
    /// Drops a leading <c>[Name] </c> tag. Only at the start, and only a
    /// complete bracket: a transcript may legitimately contain brackets later
    /// in the sentence, and half a tag is not a tag.
    /// </summary>
    private static string StripSpeaker(string line)
    {
        var text = line.TrimStart();
        if (text.Length == 0 || text[0] != '[') return line;

        var close = text.IndexOf(']');
        if (close < 0) return line;

        return text[(close + 1)..].TrimStart();
    }
}
