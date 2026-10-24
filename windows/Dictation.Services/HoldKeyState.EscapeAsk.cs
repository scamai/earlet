namespace Dictation.Services;

// Part of HoldKeyState (see HoldKeyState.cs): the Escape that asks before it
// throws a long take away. The Dictation app raises the flag while the take
// it is recording or transcribing holds more than thirty seconds of speech;
// the first Escape then only asks (HoldKeyTransition.EscapeAsked) and lowers
// the flag, so the next Escape cancels as every Escape did before. The next
// Escape is a new press: while the one that asked stays down, Windows'
// autorepeat sends it again with no key-up between, and that is still the
// same slip, not an answer.
public sealed partial class HoldKeyState
{
    private volatile bool _escapeAsksFirst;

    /// <summary>The Escape that asked is still down: its autorepeats are not
    /// a second Escape. Its key-up clears it.</summary>
    private volatile bool _askingEscapeDown;

    /// <summary>
    /// Whether the next Escape on a running dictation asks instead of
    /// cancelling: it abandons no held key and ends no latch, and answers
    /// <see cref="HoldKeyTransition.EscapeAsked"/>. The first such Escape
    /// lowers it here, in the hook, so a second Escape cancels even before
    /// the host has read the first. Pushed in by the host, which alone knows
    /// how long the take is, as <see cref="DictationInFlight"/> is.
    /// </summary>
    /// <remarks>
    /// Default false: a caller that never sets it keeps the one-Escape cancel
    /// exactly. Only Escape asks; a chord still cancels at once (Right Ctrl+C
    /// is a copy), and so does the host's own Cancel.
    /// </remarks>
    public bool EscapeAsksFirst
    {
        get => _escapeAsksFirst;
        set => _escapeAsksFirst = value;
    }

    /// <summary>The flag, read and lowered in one step: true once per
    /// raising. The Escape it answers is down from here until its key-up.</summary>
    private bool TakeEscapeAsk()
    {
        if (!_escapeAsksFirst)
        {
            return false;
        }

        _escapeAsksFirst = false;
        _askingEscapeDown = true;
        return true;
    }

    /// <summary>
    /// An Escape event, before the arm reads it: its key-up ends the asking
    /// Escape's press, and a key-down while that press lasts is its
    /// autorepeat, which must neither cancel nor ask again.
    /// </summary>
    /// <returns>Whether this is the asking Escape's autorepeat, which means
    /// nothing.</returns>
    private bool IsAskingEscapeRepeat(int message)
    {
        if (message is WmKeyUp or WmSysKeyUp)
        {
            _askingEscapeDown = false;
            return false;
        }

        return _askingEscapeDown && message is WmKeyDown or WmSysKeyDown;
    }
}
