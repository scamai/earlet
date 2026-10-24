namespace Dictation.Services;

/// <summary>
/// Pressing Backspace a counted number of times.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dictation does not delete text on its own initiative.</b> There are
/// two strong arguments against a dictation tool pressing Backspace, both
/// correct, so this seam is written to SATISFY them rather than to soften
/// them. The two claims, and what answers each:
/// </para>
/// <para>
/// <b>1. "A backspace is destructive and untargeted."</b> True, and unchanged.
/// <c>SendInput</c> injects at the keyboard, so these keystrokes delete
/// whatever is to the left of whatever caret exists when they land. Nothing in
/// an implementation of this interface can make that safe. What makes it safe
/// is who is allowed to call it: exactly one caller
/// (<see cref="FocusedTextInsertion"/>'s undo), only on a
/// <see cref="DictationUndoDecision"/> that came back
/// <see cref="DictationUndoDecision.Allowed"/>, and that decision has already
/// refused a moved window, a password field, a stale record and an
/// over-long one.
/// </para>
/// <para>
/// <b>2. "The check that would authorise a delete does not exist."</b> Also
/// true — for retraction of text the recogniser has changed its mind about,
/// mid-utterance, while the user keeps talking. There is no user intent there to appeal to, and the caret
/// can have moved for a dozen invisible reasons. This is the other case: the
/// user has SAID "scratch that". That utterance is the authorisation, it is
/// bounded to one insertion, and it is bounded in time by
/// <see cref="DictationUndoPolicy.MaxAge"/>. Dictation has no streaming
/// typer at all, so nothing streaming can reach this interface.
/// </para>
/// <para>
/// <b>A count, not a string.</b> The caller does not hand over the text to be
/// removed, because an implementation that re-derived the keystroke count from
/// a string would be a second place that decides how many keystrokes leave
/// the app — and <see cref="DictationDeleteCommand.BackspaceCount"/> is
/// deliberately the only one. Grapheme clusters, UTF-16 code units and "the
/// length of the string" are three different numbers, and two of them delete
/// into text the user wrote by hand.
/// </para>
/// <para>
/// <b>Async, unlike <c>IPasteKeystroke</c>.</b> One paste is one batch of key
/// events with nothing to wait for. A delete is up to
/// <see cref="DictationDeleteCommand.MaxBackspaces"/> of them, and a
/// tight loop that hands two thousand key events to the target's message loop
/// with no pacing is how an application drops the tail of them — which is
/// worse than not deleting at all, because it leaves a fragment of the
/// sentence on screen and reports a clean delete. The pacing is the
/// implementation's, and the wait it needs is the reason this returns a
/// <see cref="Task{TResult}"/>.
/// </para>
/// <para>
/// A seam in the policy project, for the same reason
/// <see cref="ISecureFieldProbe"/> is one: the branch that matters most — "the
/// decision refused, so send nothing" — has to be assertable without a live
/// desktop to press keys against. There is no Win32 implementation: the app
/// passes none, so its undo reports that it has nothing to press with
/// (<see cref="FocusedTextInsertion"/>).
/// </para>
/// </remarks>
public interface IBackspaceKeystroke
{
    /// <summary>
    /// Presses Backspace <paramref name="count"/> times and answers how many
    /// presses actually went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Never throws for an ordinary failure</b>, the same rule the rest of
    /// this feature keeps: a keyboard that would not take the input answers
    /// <c>0</c>, and a partial run answers what it managed. A caller comparing
    /// the answer to what it asked for is the only way to notice that half a
    /// sentence is still on screen.
    /// </para>
    /// <para>
    /// An implementation MUST clamp <paramref name="count"/> to
    /// <see cref="DictationDeleteCommand.MaxBackspaces"/> regardless of what
    /// it is passed. The ceiling is not the caller's to raise: the policy
    /// already refuses a record longer than it, so a request above it means
    /// the bookkeeping is wrong somewhere, and "the bookkeeping is wrong" must
    /// not resolve to "so delete more".
    /// </para>
    /// <para>
    /// <paramref name="ct"/> is honoured only BEFORE the first keystroke.
    /// Once one has gone the run finishes: cancelling half way through leaves
    /// the user with the front of a sentence they did not ask to keep, and no
    /// record of it left to try again with.
    /// </para>
    /// </remarks>
    Task<int> SendAsync(int count, CancellationToken ct = default);
}
