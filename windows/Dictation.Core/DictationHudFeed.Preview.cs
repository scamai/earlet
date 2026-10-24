namespace Dictation.Core;

// Part of DictationHudFeed (see DictationHudFeed.cs): the live preview's
// caption on the listening pill (DICTATION_PREVIEW=1). Shown in place of
// "Listening", never announced and never in any name a screen reader is
// given; the window fits it to the pill's one busy width, oldest words cut
// (DictationHudCaption).
public sealed partial class DictationHudFeed
{
    /// <summary>The caption of the hold listening now, or null: carried by
    /// every listening pill of the hold (the hands-free clock redraws it each
    /// second), and forgotten when the next hold listens.</summary>
    private string? _caption;

    /// <summary>The pill the feed last offered is a listening one, held or
    /// hands-free: the one on screen, or, while the pacer holds a busy pill
    /// back, not any more.</summary>
    private bool _listening;

    /// <summary>
    /// The live preview heard <paramref name="caption"/> so far: shown in
    /// place of the listening pill's title, unannounced, at the width the
    /// pill already has. Only while a listening pill is up: a caption that
    /// arrives after the hold stopped listening is dropped, and an empty one
    /// keeps the caption already shown, as on the Mac.
    /// </summary>
    public void Caption(string? caption)
    {
        if (!_listening || string.IsNullOrWhiteSpace(caption)
            || string.Equals(caption, _caption, StringComparison.Ordinal))
        {
            return;
        }

        _caption = caption;
        if (_silenceWarningShown)
        {
            // The silence warning is the line to read; the caption comes
            // back with the next listening pill (DictationHudFeed.Listening.cs).
            return;
        }

        // On the listening pill the feed last offered: the one waiting for
        // its retry while a busy pill on screen still stands its 375 ms (a
        // press soon after a dictation went back to rest), else the one on
        // screen. Never on the busy pill, whose title would stand over the
        // words.
        var listening = _deferred is { Meter: true } waiting ? waiting.Model : Shown;
        Show(listening with { Caption = caption }, announce: false, meter: true);
    }

    /// <summary>Every offer and hide says whether the pill is a listening
    /// one. The caption itself is forgotten when the next hold listens
    /// (<see cref="State"/>), and only a listening pill carries one.</summary>
    private void ShowingListening(bool listening) => _listening = listening;
}
