using Dictation.Core.Learning;
using Dictation.Services.Learning;

namespace Dictation.Core;

/// <summary>A correction watch began (<see cref="ICorrectionWatchEvents.Started"/>).</summary>
/// <param name="Watch">The watch's random id. Its learning uses the same id as
/// the dictation id, so a later outcome can be matched to this begin. Never
/// derived from text.</param>
public sealed record CorrectionWatchStarted(Guid Watch);

/// <summary>
/// One settled revision of the watched span went to learning
/// (<see cref="ICorrectionWatchEvents.Observed"/>). Counts and flags only:
/// neither the pasted text nor the user's edit travels with it.
/// </summary>
/// <param name="Watch">Which watch.</param>
/// <param name="Revision">1 for the first revision the watch handed to
/// learning, then 2, 3… for later ones in the same watch.</param>
/// <param name="Edited">The span's text differed from what was pasted. A
/// revision can change it back, so this is not always true.</param>
/// <param name="Outcome">What learning did with the revision, as
/// <see cref="DictationLearning.Observe"/> returned it: what was accepted,
/// rejected and newly learned, what is left pending, and whether it is on
/// disk.</param>
public sealed record CorrectionWatchObserved(Guid Watch, int Revision, bool Edited, LearningObservation Outcome);

/// <summary>
/// A correction watch is over (<see cref="ICorrectionWatchEvents.Ended"/>):
/// how it ended, in the span observer's own words, and nothing about the field.
/// </summary>
/// <param name="Watch">Which watch.</param>
/// <param name="Terminal">The observer's terminal
/// (<see cref="SpanObservation.TerminalSettled"/>,
/// <see cref="SpanObservation.TerminalNavigated"/>,
/// <see cref="SpanObservation.TerminalCleared"/>,
/// <see cref="SpanObservation.TerminalTimeout"/> or
/// <see cref="SpanObservation.TerminalUnobservable"/>), or
/// <see cref="CorrectionWatch.TerminalAbandoned"/> when it was ended from
/// outside.</param>
/// <param name="Reason">Why, for <c>unobservable</c>: one of
/// <see cref="SpanObservation"/>'s reasons, a <see cref="SpanObserverFailures"/>
/// value, or the watch's own <see cref="CorrectionWatch.ReasonOtherApplication"/>
/// or <see cref="CorrectionWatch.ReasonTargetUnknown"/>. Null otherwise.</param>
/// <param name="EndedBy">Why it was ended from outside, or null when it ended
/// on its own. This, not the terminal, says a watch was abandoned: a watch
/// that failed on its way out of an outside end keeps its failure's terminal.</param>
/// <param name="Revisions">How many revisions it handed to learning.</param>
/// <param name="SawChange">The observer saw the span change at all, whether
/// the change was kept, vetoed or never settled.</param>
public sealed record CorrectionWatchEnded(
    Guid Watch,
    string Terminal,
    string? Reason,
    CorrectionWatchEnd? EndedBy,
    int Revisions,
    bool SawChange)
{
    /// <summary><c>settled</c>, <c>unobservable:anchor_lost</c>, <c>abandoned</c>…</summary>
    public string Label => Reason is null ? Terminal : $"{Terminal}:{Reason}";
}

/// <summary>
/// What a correction watch tells the rest of the app: that it began, what
/// each settled revision taught, and how it ended. <see cref="CorrectionWatch"/>
/// raises these; the Vocabulary section turns them into its notice, and ends
/// the watch when the user clears that notice (<see cref="End"/>).
/// </summary>
/// <remarks>
/// Raised on whatever thread the watch is on — the caller's for
/// <see cref="Started"/>, the thread pool for the other two — and, for one
/// watch, always in the order Started, Observed…, Ended. A listener that
/// touches UI state marshals first. No event carries a word.
/// </remarks>
public interface ICorrectionWatchEvents
{
    event EventHandler<CorrectionWatchStarted>? Started;

    event EventHandler<CorrectionWatchObserved>? Observed;

    event EventHandler<CorrectionWatchEnded>? Ended;

    /// <summary>End the running watch, if any, without learning from it.
    /// Safe from any thread; a watch already over is left as it was.</summary>
    void End(CorrectionWatchEnd why);
}
