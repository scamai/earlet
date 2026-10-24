namespace Dictation.Services;

/// <summary>
/// How wide the dictation island is, per state.
/// </summary>
/// <remarks>
/// <para>
/// Two facts settled this, and they are in the view's own comments.
/// </para>
/// <para>
/// <b>It must not move while you speak.</b> The island once grew with every
/// word the recogniser produced, sliding its centred left edge half that
/// growth each time — the owner's report was that it was "bouncing around,
/// jumping left and right". That is why the speaking state has one fixed
/// canvas and keeps it.
/// </para>
/// <para>
/// <b>An outcome is its sentence.</b> The same view says it plainly: the
/// states with no meter "are the outcomes and the failures — the microphone
/// denial, the paste that did not land — and those are exactly the pills whose
/// whole value is the sentence in them." When every state was pinned to the
/// compact 240, that sentence stopped fitting: measured on 2026-08-24, 12 of
/// the 18 user-facing strings in <c>DictationHudModel</c> were cut mid-word,
/// and the half that was cut was the half carrying the outcome or the fix —
/// "Windows is blocking the mi…" losing "crophone — see Settings →
/// Dictation", "You are in a password field —…" losing "nothing was
/// recorded". One of them inverts: "Typed{into} — your clipboard …" reads as
/// reassurance when the full line is "did not come back".
/// </para>
/// <para>
/// So: the speaking pill never resizes, and a discrete outcome takes the width
/// its sentence needs, up to <see cref="Max"/>. That is the behaviour of the
/// reference the view names — an island that expands for an event and
/// collapses after it — not a reversal of it.
/// </para>
/// </remarks>
public static class DictationHudWidth
{
    /// <summary>
    /// The canvas a HOLD owns: listening and speaking, and the reference width
    /// a dragged pill is clamped against.
    /// </summary>
    public const double Compact = 240;

    /// <summary>
    /// The resting pill, which is a narrower question than the speaking one.
    ///
    /// <para>
    /// Rest used to borrow <see cref="Compact"/>, because the resting face was a
    /// clock stack that needed the room. The clock went on 2026-08-25 — the
    /// taskbar has always shown the time — and what is left is a mic glyph, the
    /// name of one key and a 5px dot: measured off a screenshot of the running
    /// app, about 85 DIP of content in a 240 DIP box. On screen that is a black
    /// slab two thirds empty, hanging permanently over the top of the display.
    /// </para>
    /// <para>
    /// This was held back once, on 2026-08-25, for a reason that no longer
    /// exists. Placement then anchored every width to the RESTING one, so
    /// shrinking rest would have pushed the expanded states further off centre.
    /// Placement now centres on the CURRENT width (frozen only for the duration
    /// of a hold — see <c>DictationHudWindow.HoldInProgress</c>), so rest and
    /// every expansion are centred independently and the trade is gone. The
    /// dragged clamp still measures against <see cref="Compact"/>, so parking
    /// the pill at the right-hand edge is unaffected.
    /// </para>
    /// <para>
    /// 152 = the longest hold-key name this can show ("Right Shift", ~62 DIP at
    /// 12px) plus the glyph, the two gaps, the dot and the pill's 16 DIP padding
    /// on each side, with slack so a longer name does not clip.
    /// </para>
    /// </summary>
    public const double Resting = 152;

    /// <summary>
    /// The ceiling for an outcome. 536 is the width this island used to have
    /// for every state, chosen so the longest failure copy fit; it is kept as
    /// the cap so the pill can never become a paragraph.
    /// </summary>
    public const double Max = 536;

    /// <summary>
    /// Width for a state that is showing a sentence and no meter.
    /// </summary>
    /// <param name="textWidth">Measured width of the rendered sentence.</param>
    /// <param name="chrome">Padding and borders around the text slot.</param>
    public static double ForSentence(double textWidth, double chrome)
    {
        if (double.IsNaN(textWidth) || double.IsInfinity(textWidth) || textWidth <= 0)
        {
            // Nothing measured yet. The compact canvas is the safe answer: it
            // is what the pill was before, and the next layout pass corrects
            // it once the text has been measured.
            return Compact;
        }

        var needed = Math.Ceiling(textWidth + Math.Max(0, chrome));
        return Math.Clamp(needed, Compact, Max);
    }
}
