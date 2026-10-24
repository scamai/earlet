using System.Collections.Frozen;

namespace Dictation.Services;

/// <summary>
/// The post-recognition text rules Windows dictation applies before the
/// words land under the cursor — a direct port of the shipped half of macOS's
/// <c>DictationFormatter</c> (<c>DictationFormatter.swift</c>).
///
/// <para>
/// <b>Why only these.</b> macOS implements eight and applies three. The other
/// five — sentence-initial disfluency deletion, verbal "like" removal,
/// grammatical agreement repair, spelled-number conversion and terminal
/// punctuation — were all measured against 14,412 real
/// <c>asrText → formattedText</c> pairs and then re-measured per-utterance, and
/// every one of them regressed output the reference had left alone. Terminal
/// punctuation alone accounted for 1,709 of 2,084 total regressions; the
/// number conversion regressed 33 of 33 reference no-ops. The finding that
/// governs this file is macOS's own: <em>a rule-based mechanism that is
/// sometimes wrong makes good output worse</em>, which is a worse product
/// outcome than doing nothing. Those five rules are deliberately NOT ported.
/// Porting one later is a measurement, not an edit.
/// </para>
///
/// <para>
/// The three that did clear the bar:
/// <list type="bullet">
/// <item><description>
/// <see cref="CapitalizeSentences"/> — measured on the same corpus, it
/// regressed 3 of 5,590 reference no-op utterances (0.05%).
/// </description></item>
/// <item><description>
/// <see cref="StripTrailingEllipsis"/> — the only structural rule that cleared
/// the ≥50-sample / ≥80%-consistency bar when the shadow harness re-mined every
/// candidate against 1,745 real dictations.
/// </description></item>
/// <item><description>
/// <see cref="ApplyCjkPunctuation"/> — the Han-script repairs (v5). Cannot
/// regress Latin text by construction (see its doc comment), which is the bar
/// the five unshipped rules failed.
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Where this runs.</b> <c>DictationSession</c>, between the transcript and
/// the insert, gated on the user's <c>DictationFormatting</c> setting — which
/// ships ON, exactly as <c>DictationFormattingStore.shippedDefault = true</c>
/// does on macOS. Nothing about it is Windows-specific, so it lives in the
/// platform-neutral project and is covered on any machine.
/// </para>
///
/// <para>
/// <b>Default is no change.</b> 38.8% of real dictations need no formatting at
/// all, and this returns its input byte-identically whenever neither rule's
/// precondition is met. There is no scored or probabilistic gate: the gate IS
/// the rule set, and each rule's own conservatism guards are what keep it from
/// firing on text it should not touch.
/// </para>
/// </summary>
public static class DictationTextFormatter
{
    /// <summary>
    /// Apply the shipped rules to one transcript.
    /// </summary>
    /// <remarks>
    /// The order is the macOS order and it matters:
    /// <see cref="StripTrailingEllipsis"/> runs FIRST, then
    /// <see cref="CapitalizeSentences"/>, then <see cref="ApplyCjkPunctuation"/>.
    /// Reversed, the capitaliser would see
    /// the ellipsis's dots as a sentence boundary and set its
    /// capitalise-the-next-letter flag on text that is about to be deleted —
    /// harmless today because nothing follows a trailing ellipsis, and exactly
    /// the kind of accidental coupling that stops being harmless the first time
    /// a third rule is added between them. The CJK repairs run LAST so they see
    /// the already-capitalised text; they are byte-identical no-ops on Latin
    /// text, which is why they may ship in the default pipeline while the five
    /// measured-unsafe rules may not.
    /// </remarks>
    /// <param name="input">The recogniser's text. Never null.</param>
    /// <returns>
    /// The text to insert — the same string instance when nothing applied.
    /// </returns>
    public static string Format(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0) return input;
        return ApplyCjkPunctuation(CapitalizeSentences(StripTrailingEllipsis(input)));
    }

    /// <summary>
    /// The processes whose windows get <b>no</b> rules at all — not even the
    /// measured-safe capitalisation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Windows half of macOS's <c>terminalBundleIDs</c> (Terminal, iTerm2,
    /// Ghostty, Warp, kitty, Alacritty, WezTerm and Hyper there; here the
    /// built-in shells plus the Windows builds of Alacritty, WezTerm and Hyper,
    /// and Git Bash's mintty). The macOS list is a set of bundle
    /// identifiers because that is what <c>NSRunningApplication</c> answers
    /// with; this one is a set of PROCESS NAMES because that is what
    /// <see cref="FocusedApplicationResult.ProcessName"/> answers with —
    /// <c>Process.GetProcessById(id).ProcessName</c>, which is the executable
    /// name without its extension.
    /// </para>
    /// <para>
    /// <b>Why nothing rather than a narrowed set.</b> macOS's own doc comment
    /// on <c>profile(forApp:)</c> is the measurement: capitalisation clears the
    /// regression bar on every app it measured, iTerm2 included (0.78%), and it
    /// is still withheld from terminals because its coverage there is worthless
    /// — <em>capitalising a shell command was never a meaningful edit to begin
    /// with</em>. `git status` becoming `Git status` is a rule paying nothing
    /// and costing a retyped character every time. So terminals get
    /// <c>.none</c>, and this set is the whole of the gate.
    /// </para>
    /// <para>
    /// <b>Ordinal-ignore-case.</b> Windows process names are compared
    /// case-insensitively everywhere else in the platform and the casing
    /// <c>Process.ProcessName</c> reports is whatever the file system recorded
    /// at install time — <c>WindowsTerminal</c> on one machine and
    /// <c>windowsterminal</c> on another is not a difference the user should
    /// feel. Ordinal and not culture-aware, for the reason
    /// <see cref="CapitalizeSentences"/> uses <c>ToUpperInvariant</c>: a
    /// Turkish locale must not decide whether a terminal is a terminal.
    /// </para>
    /// <para>
    /// <b>Six names for four applications.</b> <c>wt</c> is the launcher and
    /// <c>WindowsTerminal</c> is the process it becomes; <c>conhost</c> is the
    /// console host that owns the window for a legacy <c>cmd</c>. Which of the
    /// pair the foreground resolves to depends on the Windows build and on
    /// whether the user has the modern console turned on, so both members of
    /// each pair are listed rather than guessed at.
    /// </para>
    /// </remarks>
    public static readonly FrozenSet<string> TerminalProcessNames =
        new[]
        {
            "WindowsTerminal",
            "wt",
            "powershell",
            "pwsh",
            "cmd",
            "conhost",
            "mintty",
            "alacritty",
            "wezterm-gui",
            "Hyper",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="processName"/> names a terminal, and so gets no
    /// formatting at all.
    /// </summary>
    /// <remarks>
    /// A null, empty or whitespace name is <b>not</b> a terminal. That is the
    /// safe direction and it is deliberate: an unresolved foreground window
    /// falls back to the conservative global default (the two shipped rules),
    /// exactly as macOS's <c>profile(forApp:)</c> falls back to
    /// <c>.standard</c> for a nil bundle id — never to the ungated set, and
    /// never to silence.
    /// </remarks>
    public static bool IsTerminal(string? processName) => IsTerminal(processName, null);

    /// <summary>
    /// Whether this window gets no formatting: it is a built-in terminal, or
    /// the user has named it.
    /// </summary>
    /// <param name="processName">
    /// The foreground process, as <see cref="IFocusedApplicationResolver"/>
    /// reports it.
    /// </param>
    /// <param name="userExceptions">
    /// Applications the user added. Dictation passes none. Null means none,
    /// which is exactly what this method did before the list existed.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The built-in set is consulted FIRST and the user list only adds.</b>
    /// A terminal is a place where a capitalised first letter changes what a
    /// command does, so the five are a floor rather than a default — the same
    /// rule macOS keeps, where the terminal bundle ids sit under
    /// <c>profile(forApp:)</c> and the Exceptions row sits on top of them.
    /// There is deliberately no way to remove one from here.
    /// </para>
    /// <para>
    /// A null, empty or whitespace name is <b>not</b> a terminal and matches no
    /// exception. That is the safe direction and it is deliberate: an
    /// unresolved foreground window falls back to the conservative global
    /// default (the two shipped rules), exactly as macOS's
    /// <c>profile(forApp:)</c> falls back to <c>.standard</c> for a nil bundle
    /// id — never to the ungated set.
    /// </para>
    /// </remarks>
    public static bool IsTerminal(string? processName, IReadOnlySet<string>? userExceptions)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        // Trimmed, because the resolver's contract is "the process name" and
        // not "a trimmed process name", and a stray space would otherwise
        // silently turn formatting back on in a terminal.
        var name = processName.Trim();

        return TerminalProcessNames.Contains(name)
            || (userExceptions is not null && userExceptions.Contains(name));
    }

    /// <summary>
    /// Apply the shipped rules, unless the window they are aimed at is a
    /// terminal.
    /// </summary>
    /// <param name="input">The recogniser's text. Never null.</param>
    /// <param name="processName">
    /// The process behind the window the text is about to be inserted into, as
    /// <see cref="IFocusedApplicationResolver"/> reports it. Null or empty when
    /// nothing could be resolved, which takes the same path as an ordinary
    /// application: see <see cref="IsTerminal"/>.
    /// </param>
    /// <returns>
    /// <paramref name="input"/> itself — the same string instance, byte for
    /// byte — for a terminal; otherwise whatever <see cref="Format(string)"/>
    /// returns.
    /// </returns>
    public static string Format(string input, string? processName) =>
        Format(input, processName, null);

    /// <summary>
    /// Apply the shipped rules, unless the window they are aimed at is a
    /// terminal or an application the user has excepted.
    /// </summary>
    /// <param name="userExceptions">
    /// See <see cref="IsTerminal(string?, IReadOnlySet{string}?)"/>. Null
    /// behaves exactly as this method did before the list existed.
    /// </param>
    /// <returns>
    /// <paramref name="input"/> itself — the same string instance, byte for
    /// byte — for an exempt window; otherwise whatever
    /// <see cref="Format(string)"/> returns.
    /// </returns>
    public static string Format(
        string input, string? processName, IReadOnlySet<string>? userExceptions)
    {
        ArgumentNullException.ThrowIfNull(input);
        return IsTerminal(processName, userExceptions) ? input : Format(input);
    }

    /// <summary>
    /// Whether <see cref="Format"/> would change <paramref name="input"/> at
    /// all. The metric macOS's eval harness reports as "did we touch an
    /// utterance", independent of whether the change was an improvement.
    /// </summary>
    public static bool WouldChange(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return !string.Equals(Format(input), input, StringComparison.Ordinal);
    }

    /// <summary>
    /// Delete a trailing run of three or more ASCII dots, and the whitespace
    /// immediately before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whisper emits a trailing "..." on a clause it believes was cut off
    /// mid-thought, and the reference formatter removed it consistently enough
    /// to clear the mining bar. Mid-text ellipses are left untouched — only a
    /// run that ends the utterance is considered.
    /// </para>
    /// <para>
    /// Three conservatism guards, all of them load-bearing:
    /// <list type="number">
    /// <item><description>
    /// At least three dots. A sentence-ending period and an abbreviation's
    /// double dot are never touched.
    /// </description></item>
    /// <item><description>
    /// At least one letter or digit must survive. An utterance that is nothing
    /// but dots is left exactly as it is rather than stripped to nothing.
    /// </description></item>
    /// <item><description>
    /// Whitespace AFTER the run is preserved byte-for-byte. A user dictating
    /// into a text field where the trailing space matters does not lose it to a
    /// cosmetic rule.
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Only the ASCII form. Unicode <c>…</c> never occurred in the measured
    /// corpus (n=0), so an <c>…</c> is left alone rather than handled on a
    /// guess.
    /// </para>
    /// </remarks>
    public static string StripTrailingEllipsis(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lastNonWhitespace = -1;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i])) { lastNonWhitespace = i; break; }
        }

        if (lastNonWhitespace < 0) return text;
        if (text[lastNonWhitespace] != '.') return text;

        // Everything after the last non-whitespace character, kept verbatim.
        var trailingWhitespace = text[(lastNonWhitespace + 1)..];

        var bodyEnd = lastNonWhitespace + 1;
        var dots = 0;
        while (bodyEnd > 0 && text[bodyEnd - 1] == '.') { bodyEnd--; dots++; }

        if (dots < 3) return text;

        var hasAlphanumeric = false;
        for (var i = 0; i < bodyEnd; i++)
        {
            if (char.IsLetter(text[i]) || char.IsDigit(text[i])) { hasAlphanumeric = true; break; }
        }

        if (!hasAlphanumeric) return text;

        while (bodyEnd > 0 && char.IsWhiteSpace(text[bodyEnd - 1])) bodyEnd--;

        return string.Concat(text.AsSpan(0, bodyEnd), trailingWhitespace);
    }

    /// <summary>
    /// Upper-case the first letter of the utterance and the first letter after
    /// each sentence-terminal <c>.</c>, <c>!</c> or <c>?</c> — and the
    /// full-width <c>。</c>, <c>！</c> or <c>？</c>, which matter once Chinese
    /// text actually carries them (<see cref="ApplyCjkPunctuation"/> and the
    /// worker's zh-chunk prompt both make that common): an English sentence
    /// after a Chinese one still gets its capital.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terminal mark must itself be followed by whitespace or by the end of
    /// the string. That requirement is the whole reason "3.14 percent" survives:
    /// a decimal point sits between two digits and is never followed by
    /// whitespace, so it is never read as a sentence boundary.
    /// </para>
    /// <para>
    /// Abbreviations ("e.g.", "Mr.") are NOT special-cased. A known and
    /// accepted limitation, carried over from macOS with its measurement: this
    /// is dictated prose, not formal writing, and the corpus that produced the
    /// 0.05% regression rate contains them.
    /// </para>
    /// <para>
    /// <see cref="char.ToUpperInvariant(char)"/>, never the current culture. A
    /// machine with a Turkish locale would otherwise turn a dictated "in" into
    /// "İn" — a culture-sensitive uppercase inside a text transform is the
    /// classic instance of a rule that is right almost everywhere and silently
    /// wrong for some users.
    /// </para>
    /// <para>
    /// <b>A word with a capital after its first letter is left alone</b>:
    /// "iPhone", "macOS", "eBay", "iOS". Its casing is its own, not a sentence
    /// start the recogniser missed, and upper-casing its first letter only
    /// breaks it ("IPhone"). That includes a word the user taught, whose
    /// declared casing the worker restores (LEARNING.md §6). The word is the
    /// run of letters that starts there.
    /// </para>
    /// </remarks>
    public static string CapitalizeSentences(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;

        char[]? chars = null;
        var capitalizeNext = true;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetter(c))
            {
                if (capitalizeNext)
                {
                    var upper = HasCapitalAfterFirstLetter(text, i) ? c : char.ToUpperInvariant(c);
                    // Allocate only when something actually changes, so the
                    // 38.8% of dictations that need no formatting come back as
                    // the very string that went in.
                    if (upper != c)
                    {
                        chars ??= text.ToCharArray();
                        chars[i] = upper;
                    }
                    capitalizeNext = false;
                }
            }
            else if (c is '.' or '!' or '?' or '\u3002' or '\uFF01' or '\uFF1F')
            {
                var nextIsWhitespaceOrEnd =
                    i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]);
                if (nextIsWhitespaceOrEnd) capitalizeNext = true;
            }
        }

        return chars is null ? text : new string(chars);
    }

    /// <summary>Whether the word starting at <paramref name="start"/> — the
    /// run of letters from there — has an upper-case letter after its first.</summary>
    private static bool HasCapitalAfterFirstLetter(string text, int start)
    {
        for (var j = start + 1; j < text.Length && char.IsLetter(text[j]); j++)
        {
            if (char.IsUpper(text[j])) return true;
        }

        return false;
    }

    /// <summary>
    /// The Han-script repairs, ported from macOS's <c>applyCjkPunctuation</c>
    /// (DictationFormatter v5): a half-width <c>,</c> or <c>?</c> whose nearest
    /// non-whitespace neighbours are BOTH Han/kana becomes <c>,</c>/<c>?</c> —
    /// and a <c>?</c> at the very end of the text converts on a Han predecessor
    /// alone ("你知道吗?": the attested shape of a Chinese question; a trailing
    /// comma after Han stays half-width — it marks a cut-off clause, not a
    /// separator) — and a text with no terminal mark whose sentence is Chinese
    /// (<see cref="TerminalMarkFor"/>) ends in <c>。</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this rule may ship while terminal punctuation may not: its effect on
    /// Latin text is byte-identical to doing nothing — the separator conversion
    /// needs Han on BOTH sides, and the stop is only ever the full-width one, so
    /// the measured regression (a '.' appended to chat messages and shell
    /// fragments) is unreachable from here. The need is measured on the owner's
    /// history (2026-10-04): of 107 Chinese-bearing transcripts, 76 carried no
    /// punctuation at all and none a full-width comma, because the vocabulary
    /// prompt conditions the decoder toward Latin style
    /// (<c>whisper_mac.MLX_ZH_PROMPT_HEAD</c>).
    /// </para>
    /// <para>
    /// Separators other than <c>,</c>/<c>?</c> are deliberately not converted:
    /// they never occurred between Han characters in that history, and a rule
    /// with no measured trigger pattern is a guess. A comma with Latin on either
    /// side ("用 Slack, Teams 开会") is left half-width — the safe direction for
    /// code-mixed text.
    /// </para>
    /// </remarks>
    public static string ApplyCjkPunctuation(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return text;

        // The separator conversion. Allocates only on a real edit, the same
        // no-op-is-a-no-op contract as CapitalizeSentences.
        char[]? chars = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not (',' or '?')) continue;

            var before = i - 1;
            while (before >= 0 && char.IsWhiteSpace(text[before])) before--;
            var after = i + 1;
            while (after < text.Length && char.IsWhiteSpace(text[after])) after++;
            if (before < 0 || !IsHanOrKanaAt(text, before)) continue;
            // A run to the end of the text converts a '?' only: "吗?" is a
            // complete Chinese question, while a trailing comma is a cut-off
            // clause and keeps its half-width mark.
            if (after < text.Length)
            {
                if (!IsHanOrKanaAt(text, after)) continue;
            }
            else if (c == ',')
            {
                continue;
            }

            chars ??= text.ToCharArray();
            chars[i] = c == ',' ? '\uFF0C' : '\uFF1F';
        }

        var result = chars is null ? text : new string(chars);

        // The terminal stop: only the full-width one, only when the sentence
        // is Chinese, and only when no terminal mark is present — the macOS
        // guards, character for character.
        var lastNonWhitespace = -1;
        for (var i = result.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(result[i])) { lastNonWhitespace = i; break; }
        }

        if (lastNonWhitespace < 0) return result;
        var last = result[lastNonWhitespace];
        if (!char.IsLetter(last) && !char.IsDigit(last) && !IsHanOrKanaAt(result, lastNonWhitespace))
        {
            return result;
        }

        if (TerminalMarkFor(result) != '\u3002') return result;

        // After the whole last character, not between its surrogate halves.
        var insertAt = lastNonWhitespace + 1;
        if (char.IsHighSurrogate(result[lastNonWhitespace])
            && insertAt < result.Length
            && char.IsLowSurrogate(result[insertAt]))
        {
            insertAt++;
        }

        return result.Insert(insertAt, "\u3002");
    }

    /// <summary>
    /// Whether the character at <paramref name="index"/> is a Han ideograph or
    /// a Japanese kana — the scripts whose sentences end in a full-width stop.
    /// Deliberately NOT "anything non-Latin": Hangul and Cyrillic sentences take
    /// an ASCII full stop. Supplementary-plane ideographs (Extension B and
    /// beyond) arrive as surrogate pairs and are read as one scalar.
    /// </summary>
    private static bool IsHanOrKanaAt(string text, int index)
    {
        var c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return char.ConvertToUtf32(c, text[index + 1]) is >= 0x20000 and <= 0x2FA1F;
        }

        if (char.IsLowSurrogate(c) && index > 0 && char.IsHighSurrogate(text[index - 1]))
        {
            return char.ConvertToUtf32(text[index - 1], c) is >= 0x20000 and <= 0x2FA1F;
        }

        return c is >= '\u3040' and <= '\u30FF'    // Hiragana, Katakana
            or >= '\u3400' and <= '\u4DBF'         // CJK Unified Ideographs Extension A
            or >= '\u4E00' and <= '\u9FFF'         // CJK Unified Ideographs
            or >= '\uF900' and <= '\uFAFF';        // CJK Compatibility Ideographs
    }

    /// <summary>
    /// Which full stop this utterance ends with — a direct port of macOS's
    /// <c>terminalMark(for:)</c>, including both of its conditions, because
    /// real dictation here is bilingual and either condition alone gets a real
    /// slice of it wrong: "ends in Han/kana" catches English-opening sentences
    /// that finish in Chinese, and "more Han/kana than other letters" catches
    /// Chinese sentences that end on a Latin product name.
    /// </summary>
    private static char TerminalMarkFor(string text)
    {
        var hanOrKana = 0;
        var otherLetters = 0;
        var lastLetterIsHanOrKana = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsHanOrKanaAt(text, i))
            {
                hanOrKana++;
                lastLetterIsHanOrKana = true;
                if (char.IsHighSurrogate(text[i])) i++;  // count the pair once
            }
            else if (char.IsLetter(text[i]))
            {
                otherLetters++;
                lastLetterIsHanOrKana = false;
            }
        }

        return lastLetterIsHanOrKana || hanOrKana > otherLetters ? '\u3002' : '.';
    }
}
