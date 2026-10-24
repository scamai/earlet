namespace Dictation.Services;

/// <summary>
/// The one thing Dictation typed most recently, kept so "scratch that" has
/// something to act on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is process-wide static and not a field on the inserter.</b>
/// macOS learned it the hard way (<c>TextInserter.swift:508-517</c>): a record
/// held per instance is empty for the next dictation the moment anything
/// rebuilds the inserter, and the user's experience of that is not "undo is
/// unavailable" — it is "scratch that worked yesterday". On Windows the
/// rebuild is likelier, not less: <see cref="FocusedTextInsertion"/> is a
/// singleton in the shipped graph, but the graph itself is rebuilt when
/// dictation is re-bootstrapped behind a flag, and a HUD-owned inserter has
/// been constructed directly more than once in this tree's history. The record
/// has to outlive whichever object typed it, because the TEXT it describes
/// outlives that object — it is sitting in somebody's document.
/// </para>
/// <para>
/// <b>One slot, deliberately.</b> Not a stack. A stack would let a second
/// "scratch that" walk backwards into text from an earlier insertion, and
/// every guard <see cref="DictationUndoPolicy"/> holds — the two-minute age,
/// the same window, the grapheme count — is stated about ONE insertion. There
/// is no honest way to age or window-check the third entry down, so there is
/// no third entry.
/// </para>
/// <para>
/// <b><see cref="Take"/> rather than a read plus a clear.</b> The undo path
/// consumes the record as its first action, before it has decided anything, so
/// that "the user said scratch that" spends the record whether or not the
/// delete is allowed. A refusal that left the record in place would let the
/// same record be re-offered a minute later against a window the user has
/// since moved back to — which is precisely the stale delete
/// <see cref="DictationUndoRefusal.Expired"/> exists to prevent, arriving
/// through the back door.
/// </para>
/// <para>
/// <b>Thread safety is not theoretical here.</b> The insert runs on whatever
/// thread the transcription continuation lands on and the undo runs on the
/// hotkey host's; a torn read of a two-field record would hand the policy a
/// window handle from one insertion and a timestamp from another. The lock is
/// held across nothing but a field assignment, so it can never be the thing
/// that delays a keystroke.
/// </para>
/// <para>
/// The text lives in memory only. Nothing here is written to disk — the record
/// holds exactly what the user just dictated, which is the most sensitive
/// string this feature ever touches.
/// </para>
/// </remarks>
public static class LastInsertionRegistry
{
    private static readonly object Gate = new();

    private static DictationInsertionRecord? _last;

    /// <summary>
    /// Remembers <paramref name="record"/> as the last thing Dictation typed,
    /// replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// Callers must only reach this on a CONFIRMED insertion — see
    /// <see cref="FocusedTextInsertion.InsertAsync"/>, which writes it on the
    /// single branch where the paste keystroke was reported delivered.
    /// Recording a refusal here would arm a backspace over text that was never
    /// typed, which is the one failure this feature must never produce.
    /// </remarks>
    public static void Record(DictationInsertionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (Gate)
        {
            _last = record;
        }
    }

    /// <summary>
    /// What Dictation last typed, without spending it. For a caller that wants to
    /// TELL the user whether there is anything to undo — the HUD's affordance —
    /// rather than to undo it.
    /// </summary>
    public static DictationInsertionRecord? Peek()
    {
        lock (Gate)
        {
            return _last;
        }
    }

    /// <summary>
    /// The record, removed. Returns <c>null</c> when there is none, which is
    /// what <see cref="DictationUndoPolicy.Decide"/> reads as
    /// <see cref="DictationUndoRefusal.NothingRecorded"/>.
    /// </summary>
    /// <remarks>
    /// Read-and-remove in one lock, so two undos racing cannot both come away
    /// holding the same insertion and send its backspaces twice.
    /// </remarks>
    public static DictationInsertionRecord? Take()
    {
        lock (Gate)
        {
            var taken = _last;
            _last = null;
            return taken;
        }
    }

    /// <summary>
    /// Forgets the record without reading it — for a caller that knows the
    /// text is gone by some other route (the user undid it themselves, the
    /// window closed), and for tests, which share this static.
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            _last = null;
        }
    }
}
