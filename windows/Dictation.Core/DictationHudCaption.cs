using System.Globalization;

namespace Dictation.Core;

/// <summary>
/// How the live preview's caption fits the listening pill: whole when it fits,
/// otherwise its newest end after an ellipsis — the oldest words are the ones
/// cut, as the Mac's title line does it (<c>.truncationMode(.head)</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The pill does not grow for a caption.</b> Every busy pill of a hold is
/// one width, worked out once from every line it can show, the caption's
/// budget among them (<see cref="DictationCopy.HudBusyLines"/>), so a caption
/// that changes with every hypothesis never resizes or moves the pill. A
/// caption wider than that (seven long words) is cut here, at its start,
/// because its end is what the user just said.
/// </para>
/// <para>
/// WPF's own trimming cuts the end of a line, never the start, so the window
/// asks this instead, with its font's measure. Pure: tested on Linux with a
/// stand-in measure; how wide the real fonts make a caption is not measured.
/// </para>
/// </remarks>
public static class DictationHudCaption
{
    /// <summary>What stands before a caption whose start was cut.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// <paramref name="caption"/> as it fits in <paramref name="room"/>:
    /// unchanged when it fits, otherwise <see cref="Ellipsis"/> and the
    /// longest end of it that fits with one, cut between text elements (never
    /// inside a surrogate pair or a combining sequence) and without the space
    /// the cut leaves. When not even the last element fits with the
    /// ellipsis, that much is returned and the window clips it.
    /// </summary>
    /// <param name="caption">The caption, already windowed to its word budget.</param>
    /// <param name="room">The width the title line has, in the measure's units.</param>
    /// <param name="width">The width of a text in the title's font.</param>
    public static string HeadTrimmed(string? caption, double room, Func<string, double> width)
    {
        ArgumentNullException.ThrowIfNull(width);
        if (string.IsNullOrEmpty(caption))
        {
            return string.Empty;
        }

        if (width(caption) <= room)
        {
            return caption;
        }

        var starts = StringInfo.ParseCombiningCharacters(caption);
        string? last = null;
        for (var i = 1; i < starts.Length; i++)
        {
            var tail = caption[starts[i]..].TrimStart();
            if (tail.Length == 0)
            {
                continue;
            }

            last = Ellipsis + tail;
            if (width(last) <= room)
            {
                return last;
            }
        }

        return last ?? Ellipsis + caption[starts[^1]..];
    }
}
