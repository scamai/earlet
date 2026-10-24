using System.Globalization;
using System.Text;

namespace Dictation.Core.Learning;

/// <summary>
/// The text primitives the learning rules are written in, with the answers the
/// reference (<c>tests/learning_reference.py</c>) gets from Python's
/// <c>str</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Code points, not UTF-16 units.</b> Every length, index and character test
/// in LEARNING.md counts code points, which is what Python's <c>str</c> and
/// Swift's <c>unicodeScalars</c> give. A CJK Extension B ideograph is one
/// character here, not the two a <see cref="string"/> holds. A lone surrogate
/// is kept as the one code point Python would keep, never replaced.
/// </para>
/// <para>
/// <b>Why not <see cref="string.ToLowerInvariant"/>.</b> Python's
/// <c>lower()</c> is the full Unicode mapping: U+0130 becomes <c>i</c> plus a
/// combining dot, and a capital sigma at the end of a word becomes the final
/// form <c>ς</c>. .NET maps one unit to one unit and never does either, so a
/// tombstone written by the reference for <c>ΛΟΓΟΣ</c> would not match the key
/// this port computes. Everything else agrees: compared over all 1,112,064 code
/// points on 2026-09-28 (Python 3.12, Unicode 15.0; .NET 9), the general
/// categories are identical and the simple lowercase mapping differs only at
/// U+0130. <see cref="Lower"/> then matched <c>str.lower()</c> for every code
/// point, alone and on either side of a capital sigma.
/// </para>
/// </remarks>
public static class LearningText
{
    /// <summary>Unicode's Other_Lowercase: lowercase without being <c>Ll</c>
    /// (ordinal indicators, modifier letters, small roman numerals, circled
    /// letters). Python's <c>islower()</c> counts them.</summary>
    private static readonly (int First, int Last)[] OtherLowercase =
    [
        (0x00AA, 0x00AA), (0x00BA, 0x00BA), (0x02B0, 0x02B8), (0x02C0, 0x02C1),
        (0x02E0, 0x02E4), (0x0345, 0x0345), (0x037A, 0x037A), (0x10FC, 0x10FC),
        (0x1D2C, 0x1D6A), (0x1D78, 0x1D78), (0x1D9B, 0x1DBF), (0x2071, 0x2071),
        (0x207F, 0x207F), (0x2090, 0x209C), (0x2170, 0x217F), (0x24D0, 0x24E9),
        (0x2C7C, 0x2C7D), (0xA69C, 0xA69D), (0xA770, 0xA770), (0xA7F2, 0xA7F4),
        (0xA7F8, 0xA7F9), (0xAB5C, 0xAB5F), (0xAB69, 0xAB69), (0x10780, 0x10780),
        (0x10783, 0x10785), (0x10787, 0x107B0), (0x107B2, 0x107BA), (0x1E030, 0x1E06D),
    ];

    /// <summary>Unicode's Other_Uppercase: capital roman numerals and circled
    /// or squared capitals.</summary>
    private static readonly (int First, int Last)[] OtherUppercase =
    [
        (0x2160, 0x216F), (0x24B6, 0x24CF), (0x1F130, 0x1F149), (0x1F150, 0x1F169), (0x1F170, 0x1F189),
    ];

    /// <summary>Word_Break MidLetter, MidNumLet and Single_Quote: the
    /// punctuation Unicode lets sit inside a word without breaking the final
    /// sigma rule.</summary>
    private static readonly int[] CaseIgnorablePunctuation =
    [
        0x0027, 0x002E, 0x003A, 0x00B7, 0x0387, 0x055F, 0x05F4, 0x2018, 0x2019,
        0x2024, 0x2027, 0xFE13, 0xFE52, 0xFE55, 0xFF07, 0xFF0E, 0xFF1A,
    ];

    /// <summary>LEARNING.md §3.1: each of these code points is a token by itself.</summary>
    private static readonly (int First, int Last)[] CjkRanges =
    [
        (0x3040, 0x30FF),   // Hiragana, Katakana
        (0x3400, 0x4DBF),   // CJK Extension A
        (0x4E00, 0x9FFF),   // CJK Unified Ideographs
        (0xAC00, 0xD7AF),   // Hangul syllables
        (0xF900, 0xFAFF),   // CJK Compatibility Ideographs
        (0x20000, 0x2FFFF), // CJK Extensions B-F
    ];

    /// <summary>Strings in code point order, which is how Python sorts them.
    /// Ordinal comparison of UTF-16 puts U+E000..U+FFFF after every
    /// supplementary character; this does not.</summary>
    public static IComparer<string> CodePointOrder { get; } = new CodePointComparer();

    // ─── Code points ───

    /// <summary>The code points of <paramref name="text"/>. A lone surrogate is
    /// its own code point, as in Python.</summary>
    public static int[] CodePoints(string text) => CodePoints(text, out _);

    /// <summary>The code points of <paramref name="text"/> and, for each, where
    /// it starts in the string; the last offset is the string's length.</summary>
    public static int[] CodePoints(string text, out int[] utf16Offsets)
    {
        ArgumentNullException.ThrowIfNull(text);
        var points = new List<int>(text.Length);
        var offsets = new List<int>(text.Length + 1);
        for (var i = 0; i < text.Length;)
        {
            offsets.Add(i);
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                points.Add(char.ConvertToUtf32(c, text[i + 1]));
                i += 2;
            }
            else
            {
                points.Add(c);
                i++;
            }
        }

        offsets.Add(text.Length);
        utf16Offsets = [.. offsets];
        return [.. points];
    }

    /// <summary>Python's <c>len()</c>.</summary>
    public static int Length(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    // ─── Character classes ───

    public static UnicodeCategory Category(int codePoint) => CharUnicodeInfo.GetUnicodeCategory(codePoint);

    /// <summary>A CJK code point (LEARNING.md §3.1).</summary>
    public static bool IsCjk(int codePoint) => InRanges(codePoint, CjkRanges);

    /// <summary>A letter, mark or decimal digit that is not CJK.</summary>
    public static bool IsWordChar(int codePoint) => !IsCjk(codePoint) && Category(codePoint) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        or UnicodeCategory.DecimalDigitNumber;

    /// <summary>One of <c>' ’ - . _</c>: a token may continue across it.</summary>
    public static bool IsJoiner(int codePoint) => codePoint is '\'' or '\u2019' or '-' or '.' or '_';

    /// <summary><c>'</c> or <c>’</c>: an ordinary-word test cuts there.</summary>
    public static bool IsApostrophe(int codePoint) => codePoint is '\'' or '\u2019';

    /// <summary>Python's <c>islower()</c> on one code point.</summary>
    public static bool IsLowercase(int codePoint) =>
        Category(codePoint) == UnicodeCategory.LowercaseLetter || InRanges(codePoint, OtherLowercase);

    /// <summary>Python's <c>isupper()</c> on one code point.</summary>
    public static bool IsUppercase(int codePoint) =>
        Category(codePoint) == UnicodeCategory.UppercaseLetter || InRanges(codePoint, OtherUppercase);

    /// <summary>Python's <c>isalpha()</c> on one code point: any <c>L*</c>.</summary>
    public static bool IsLetter(int codePoint) => Category(codePoint) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;

    /// <summary>Does <paramref name="text"/> have a letter (any <c>L*</c>) in it?
    /// The reference's <c>has_letter</c>: a token without one is a number, and
    /// a number is never vocabulary (LEARNING.md §2, §4.2).</summary>
    public static bool HasLetter(string text) => CodePoints(text).Any(IsLetter);

    /// <summary>Python's <c>isdigit()</c> for the code points a token can hold:
    /// a decimal digit (<c>Nd</c>). Superscripts and circled digits are
    /// <c>No</c> and never reach a token.</summary>
    public static bool IsDigit(int codePoint) => Category(codePoint) == UnicodeCategory.DecimalDigitNumber;

    /// <summary>Python's <c>isspace()</c>: .NET's white space plus the four
    /// information separators U+001C..U+001F.</summary>
    public static bool IsSpace(int codePoint) =>
        codePoint is >= 0x1C and <= 0x1F
        || (codePoint <= 0xFFFF && char.IsWhiteSpace((char)codePoint));

    private static bool IsCased(int codePoint) =>
        IsLowercase(codePoint) || IsUppercase(codePoint)
        || Category(codePoint) == UnicodeCategory.TitlecaseLetter;

    private static bool IsCaseIgnorable(int codePoint) =>
        Category(codePoint) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol
        || Array.IndexOf(CaseIgnorablePunctuation, codePoint) >= 0;

    // ─── Python's str methods ───

    /// <summary>Python's <c>str.lower()</c>.</summary>
    public static string Lower(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsAscii(text))
        {
            return text.ToLowerInvariant();
        }

        var points = CodePoints(text);
        var lower = new StringBuilder(text.Length);
        for (var i = 0; i < points.Length; i++)
        {
            var cp = points[i];
            if (cp == 0x0130)
            {
                lower.Append("i\u0307"); // i and a combining dot above
            }
            else if (cp == 0x03A3)
            {
                lower.Append(IsFinalSigma(points, i) ? '\u03C2' : '\u03C3'); // ς : σ
            }
            else if (cp is >= 0xD800 and <= 0xDFFF)
            {
                lower.Append((char)cp);
            }
            else
            {
                var mapped = Rune.ToLowerInvariant(new Rune(cp)).Value;
                if (mapped <= 0xFFFF)
                {
                    lower.Append((char)mapped);
                }
                else
                {
                    lower.Append(char.ConvertFromUtf32(mapped));
                }
            }
        }

        return lower.ToString();
    }

    /// <summary>Python's <c>str.strip()</c> with no argument.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    /// <summary>Python's <c>str.split()</c> with no argument: runs of white
    /// space separate, and none is returned empty.</summary>
    public static List<string> SplitOnSpace(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsSpace(text[i]))
            {
                if (start >= 0)
                {
                    parts.Add(text[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            parts.Add(text[start..]);
        }

        return parts;
    }

    // ─── Helpers ───

    /// <summary>
    /// Unicode's Final_Sigma context, as CPython applies it: a cased letter
    /// before (skipping case-ignorable code points) and no cased letter after.
    /// </summary>
    private static bool IsFinalSigma(int[] points, int index)
    {
        var j = index - 1;
        while (j >= 0 && IsCaseIgnorable(points[j]))
        {
            j--;
        }

        if (j < 0 || !IsCased(points[j]))
        {
            return false;
        }

        j = index + 1;
        while (j < points.Length && IsCaseIgnorable(points[j]))
        {
            j++;
        }

        return j == points.Length || !IsCased(points[j]);
    }

    private static bool IsAscii(string text)
    {
        foreach (var c in text)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static bool InRanges(int codePoint, (int First, int Last)[] ranges)
    {
        foreach (var (first, last) in ranges)
        {
            if (codePoint >= first && codePoint <= last)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CodePointComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var shorter = Math.Min(x.Length, y.Length);
            for (var i = 0; i < shorter; i++)
            {
                int a = x[i], b = y[i];
                if (a == b)
                {
                    continue;
                }

                // Surrogates (D800-DFFF) encode code points above FFFF, so they
                // must sort after E000-FFFF: shift both bands into order.
                if (a >= 0xD800 && b >= 0xD800)
                {
                    a = a >= 0xE000 ? a - 0x800 : a + 0x2000;
                    b = b >= 0xE000 ? b - 0x800 : b + 0x2000;
                }

                return a < b ? -1 : 1;
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
