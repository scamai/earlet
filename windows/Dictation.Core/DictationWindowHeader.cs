namespace Dictation.Core;

/// <summary>
/// The top of the window: the state glyph the notification-area icon shows,
/// a one-line headline over the status line, and Enable/Pause as one button,
/// the Mac's status header (DictationHomeView.swift,
/// <c>DictationStatusHeader</c>). Cancel beside it shows while
/// <see cref="DictationViewState.CanCancel"/>.
/// </summary>
/// <remarks>
/// The glyph is the same <see cref="DictationTrayGlyph.State"/> the icon's
/// picture and tooltip are drawn from in the same render, and the button's
/// label and liveness are the menu's Enable/Pause item's
/// (<see cref="DictationTrayState.ToggleLabel"/>,
/// <see cref="DictationTrayState.CanToggle"/>), so the window, the icon and the
/// menu cannot disagree.
/// </remarks>
/// <param name="Glyph">The state: which picture (<see cref="DictationTrayGlyph.Picture"/>)
/// and tint (<see cref="DictationTrayGlyph.Tint"/>) the header draws.</param>
/// <param name="Headline">The line over the status sentence
/// (<see cref="DictationTrayGlyph.Headline"/>).</param>
/// <param name="ToggleLabel"><see cref="DictationCopy.EnableLabel"/> or
/// <see cref="DictationCopy.PauseLabel"/>.</param>
/// <param name="ToggleEnables">A click enables (dictation is off); otherwise
/// it pauses, through <c>TryPause</c>, which refuses under a dictation. The
/// button does what it said when it was drawn, never the other.</param>
/// <param name="CanToggle">The button is live: Enable while no Enable is
/// loading; Pause while no dictation listens, transcribes or types
/// (<see cref="DictationViewState.CanPause"/>).</param>
public sealed record DictationWindowHeader(
    DictationGlyph Glyph,
    string Headline,
    string ToggleLabel,
    bool ToggleEnables,
    bool CanToggle);
