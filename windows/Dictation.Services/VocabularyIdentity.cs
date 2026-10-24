using System.Text;

namespace Dictation.Services;

/// <summary>
/// The one place that decides what makes two spellings of a word the SAME
/// word.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the extractor and the store disagreed about it, and
/// the disagreement resurrected a word the user had struck out.</b>
/// <see cref="VocabularyTermExtractor.NormaliseCorpus(string)"/> composes the
/// corpus to <see cref="NormalizationForm.FormC"/> before it classifies a
/// character, so every offered term is composed. The store did not: its
/// identity is <see cref="StringComparer.OrdinalIgnoreCase"/> over whatever
/// bytes the file happened to hold, and a <c>bias_phrases.txt</c> carrying
/// <c>#- Zürich</c> with a DECOMPOSED <c>ü</c> (<c>U+0075 U+0308</c> — the
/// shape a name pasted out of Word, Outlook or any macOS-authored document
/// arrives in, which <c>Save</c> is documented to keep verbatim) does not
/// ordinal-match the composed <c>U+00FC</c> the scan produces.
/// </para>
/// <para>
/// The consequence was end-to-end, not theoretical: the offer's struck set
/// missed the word, so it was offered NOT excluded and selected by default,
/// and the merge missed it in the same way and APPENDED it, leaving the file
/// holding two visually identical lines —
/// one struck, one live — with the struck word biasing the decoder again and
/// the card reporting "Added 1 word."
/// </para>
/// <para>
/// <b>Composing on the store side rather than at each import-side comparison
/// is the fix that cannot be forgotten.</b>
/// <see cref="DictationVocabulary.ParseLines"/> is the single door every read
/// and every write goes through (<c>Load</c> parses, <c>Save</c> parses what it
/// is handed), so composing there makes the store agree with the extractor by
/// construction instead of by three call sites staying in step.
/// </para>
/// </remarks>
public static class VocabularyIdentity
{
    /// <summary>
    /// Compose to <see cref="NormalizationForm.FormC"/>, or return the text
    /// unchanged when it cannot be.
    /// </summary>
    /// <remarks>
    /// <see cref="string.IsNormalized(NormalizationForm)"/> first, because the
    /// overwhelmingly common input is ASCII and that check is a scan with no
    /// allocation. The catch is not defensive padding: both methods throw
    /// <see cref="ArgumentException"/> on an unpaired surrogate, which a
    /// truncated 512 KB read can genuinely produce at its tail, and text that
    /// cannot be composed is still perfectly usable in the form it arrived in.
    /// </remarks>
    public static string Compose(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            return text.IsNormalized(NormalizationForm.FormC)
                ? text
                : text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
