using System.Text;

namespace Dictation.Core.Learning;

/// <summary>One token of a dictation or an edit (LEARNING.md §3.1).</summary>
/// <param name="Text">The token as written.</param>
/// <param name="Start">Its first code point.</param>
/// <param name="End">One past its last code point.</param>
/// <param name="IsCjk">A single CJK code point.</param>
public sealed record LearningToken(string Text, int Start, int End, bool IsCjk)
{
    /// <summary>Where the token starts in the UTF-16 string.</summary>
    public int Utf16Start { get; init; }

    /// <summary>One past where the token ends in the UTF-16 string.</summary>
    public int Utf16End { get; init; }
}

/// <summary>
/// Tokens and the string forms the rules compare (LEARNING.md §3.1, §3.3).
/// </summary>
public static class LearningTokens
{
    /// <summary>
    /// A CJK code point is a token by itself; otherwise a token is a maximal
    /// run of letters, marks and decimal digits, which continues across one
    /// joiner (<c>' ’ - . _</c>) when a word character follows it. Everything
    /// else separates tokens.
    /// </summary>
    public static IReadOnlyList<LearningToken> Tokenize(string text)
    {
        var points = LearningText.CodePoints(text, out var offsets);
        var tokens = new List<LearningToken>();
        var n = points.Length;
        var i = 0;
        while (i < n)
        {
            var cp = points[i];
            if (LearningText.IsCjk(cp))
            {
                tokens.Add(Token(text, offsets, i, i + 1, cjk: true));
                i++;
                continue;
            }

            if (LearningText.IsWordChar(cp))
            {
                var j = i + 1;
                while (j < n)
                {
                    if (LearningText.IsWordChar(points[j]))
                    {
                        j++;
                    }
                    else if (LearningText.IsJoiner(points[j]) && j + 1 < n && LearningText.IsWordChar(points[j + 1]))
                    {
                        j += 2;
                    }
                    else
                    {
                        break;
                    }
                }

                tokens.Add(Token(text, offsets, i, j, cjk: false));
                i = j;
                continue;
            }

            i++;
        }

        return tokens;
    }

    /// <summary>The original text from the start of <paramref name="first"/> to
    /// the end of <paramref name="last"/>, spacing inside the span kept.</summary>
    internal static string Slice(string text, LearningToken first, LearningToken last) =>
        text[first.Utf16Start..last.Utf16End];

    /// <summary>Lowercase tokens with their joiners removed, run together.</summary>
    public static string Squash(string text) =>
        string.Concat(Tokenize(text).Select(t => StripJoiners(LearningText.Lower(t.Text))));

    /// <summary>Lowercase tokens with their joiners removed, one space apart.</summary>
    public static string NormSpace(string text) =>
        string.Join(' ', Tokenize(text).Select(t => StripJoiners(LearningText.Lower(t.Text))));

    /// <summary>The key an alias is stored under: lowercase, white space
    /// collapsed to single spaces.</summary>
    public static string HeardKey(string text) =>
        string.Join(' ', LearningText.SplitOnSpace(LearningText.Lower(text)));

    /// <summary>Edit distance in code points.</summary>
    public static int Levenshtein(string a, string b)
    {
        var x = LearningText.CodePoints(a);
        var y = LearningText.CodePoints(b);
        var previous = new int[y.Length + 1];
        var current = new int[y.Length + 1];
        for (var j = 0; j <= y.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= x.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= y.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[y.Length];
    }

    /// <summary>
    /// Does <paramref name="haystack"/> contain <paramref name="needle"/> as a
    /// run of whole tokens, ignoring case?
    /// </summary>
    public static bool ContainsTokens(string haystack, string needle)
    {
        var h = Tokenize(haystack).Select(t => LearningText.Lower(t.Text)).ToArray();
        var n = Tokenize(needle).Select(t => LearningText.Lower(t.Text)).ToArray();
        if (n.Length == 0 || n.Length > h.Length)
        {
            return false;
        }

        for (var i = 0; i <= h.Length - n.Length; i++)
        {
            if (h.AsSpan(i, n.Length).SequenceEqual(n))
            {
                return true;
            }
        }

        return false;
    }

    internal static int LatinCount(IReadOnlyList<LearningToken> tokens) => tokens.Count(t => !t.IsCjk);

    internal static int CjkCount(IReadOnlyList<LearningToken> tokens) => tokens.Count(t => t.IsCjk);

    private static string StripJoiners(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!LearningText.IsJoiner(c))
            {
                kept.Append(c);
            }
        }

        return kept.ToString();
    }

    private static LearningToken Token(string text, int[] offsets, int start, int end, bool cjk) =>
        new(text[offsets[start]..offsets[end]], start, end, cjk)
        {
            Utf16Start = offsets[start],
            Utf16End = offsets[end],
        };
}
