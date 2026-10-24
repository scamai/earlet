using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>What the indicator needs to know about the hold itself.</summary>
/// <param name="KeyName">The hold key's name, which the hints and some
/// endings name ("Press Right Ctrl, Space or Enter to stop", "hold Right Ctrl
/// while you speak").</param>
/// <param name="Policy">How long a hold may listen: the limit the clock and
/// the countdown count to, and the countdown's length.</param>
public sealed record DictationHudHold(string KeyName, DictationHoldPolicy Policy);

// Part of DictationHudFeed (see DictationHudFeed.cs): the busy pills — the
// listening pill, held or hands-free, with its hint, the hands-free clock and
// the countdown, and the one width every busy pill shares.
public sealed partial class DictationHudFeed
{
    /// <summary>The listening pill offered last is the silence warning,
    /// which no live-preview caption covers.</summary>
    private bool _silenceWarningShown;

    /// <summary>The hold the busy lines were last worked out for.</summary>
    private DictationHudHold? _busyFor;

    /// <summary><see cref="DictationCopy.HudBusyLines"/> for
    /// <see cref="_busyFor"/>: one list per hold key, so every pill of a hold
    /// carries the same instance.</summary>
    private IReadOnlyList<string> _busyLines = [];

    /// <summary>
    /// The hold is listening, held or latched hands-free: "Listening", then
    /// the hint (<see cref="DictationCopy.HudListening"/>), counting down in
    /// the policy's last minute before the limit either way, with the
    /// hands-free clock beside it; or, near a silence stop (hands-free, or
    /// held before the stuck-key stop), the silence warning. The controller calls this on every tick while the
    /// hold listens; the pill is redrawn only when its words change, so at
    /// most once a second, and it is never announced: the microphone is open,
    /// and Narrator through the speakers would be dictated. It is a
    /// listening pill: the level bars stay in its mark, drawn again right
    /// after each redraw, beside the clock.
    /// </summary>
    /// <param name="latched">The hold is latched hands-free.</param>
    /// <param name="elapsed">Time since the microphone came up.</param>
    /// <param name="silence">The silence stop's warning, when one is due.</param>
    public void Listening(bool latched, TimeSpan elapsed, DictationSilenceWarning? silence = null)
    {
        if (_askingToDiscard)
        {
            // Escape's question stands until the controller ends it
            // (DictationHudFeed.Takes.cs).
            return;
        }

        var hold = _hold();
        var text = DictationCopy.HudListening(hold.KeyName, latched, elapsed, hold.Policy, silence);
        if (string.Equals(Shown.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        // The warning is the line to read: the live preview's caption, which
        // would stand in its title, gives way to it.
        var warning = silence is not null && text == DictationCopy.HudSilenceWarning(silence.Value);
        _silenceWarningShown = warning;
        var pill = ListeningPill(text, latched);
        Show(warning ? pill with { Caption = null } : pill, announce: false, meter: true);
    }

    /// <summary>A busy pill: it lasts as long as its state does.</summary>
    private static DictationHudModel Busy(string text) => new(text, HudTone.Neutral, null);

    /// <summary>
    /// A listening pill, never announced, whose name a screen reader finds
    /// on it is the state's alone: "Listening" or "Listening, hands-free"
    /// (<see cref="DictationCopy.HudListeningName"/>), never the hint, the
    /// clock, the countdown or the live preview's caption, as the Mac's
    /// <c>DictationHUDAccessibility.label</c>. It carries the hold's current
    /// caption, so the hands-free clock redrawn each second keeps it.
    /// </summary>
    private DictationHudModel ListeningPill(string text, bool latched) =>
        Busy(text) with { UnannouncedName = DictationCopy.HudListeningName(latched), Caption = _caption };

    /// <summary>
    /// <paramref name="model"/> sized for every busy line of the current
    /// hold: a busy pill is then the one busy width, and an ending starts at
    /// it (<see cref="DictationHudGeometry.PillWidth"/>).
    /// </summary>
    private DictationHudModel SizedForTheHold(DictationHudModel model)
    {
        var hold = _hold();
        if (hold != _busyFor)
        {
            _busyLines = DictationCopy.HudBusyLines(hold.KeyName, hold.Policy);
            _busyFor = hold;
        }

        return model with { SizedFor = _busyLines };
    }
}
