namespace Dictation.Speech;

/// <summary>
/// Which of four tone colours a HUD line is painted in.
/// </summary>
/// <remarks>
/// <para>
/// Named for what the colour <em>means</em> rather than for the colour itself,
/// because the meaning is the invariant: green is genuine only, amber is
/// suspicious or degraded, red is a confirmed deepfake and never decoration,
/// grey is unknown or inactive. A <c>HudColor.Red</c> would invite a caller to
/// pick red because a failure felt urgent; <see cref="Deepfake"/> cannot be
/// picked for anything but a deepfake without the call site reading as the
/// bug it is.
/// </para>
/// <para>
/// The dictation indicator (<c>DictationHudModel</c>) is the one user left and
/// paints only <see cref="Neutral"/> and <see cref="Caution"/>;
/// <c>DictationHudTests</c> fails if any state or outcome reaches for a verdict
/// colour, so nothing in this tree asks for <see cref="Genuine"/> or
/// <see cref="Deepfake"/>.
/// </para>
/// </remarks>
public enum HudTone
{
    /// <summary>Grey. Unknown, inactive, pending, or simply not a verdict —
    /// which includes every ordinary success. The default, on purpose.</summary>
    Neutral,

    /// <summary>Green. A genuine verdict, and nothing else ever.</summary>
    Genuine,

    /// <summary>Amber. Suspicious, or degraded — the recognizer struggling, a
    /// target that did not resolve, a paste that did not land.</summary>
    Caution,

    /// <summary>Red. A confirmed deepfake finding, and nothing else ever.
    /// Never a workflow failure, however bad.</summary>
    Deepfake,
}
