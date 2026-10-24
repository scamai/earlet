using Dictation.Core.Learning;
using Dictation.Services.Learning;

namespace Dictation.Core;

/// <summary>
/// Which correction notice the Vocabulary section shows for a watch's events:
/// the macOS sentences (docs/design-macos.md, Correction notices), reached from the
/// Windows span observer's terminals instead of the Mac's Accessibility ones.
/// Pure, so every mapping is tested on Linux.
/// </summary>
/// <remarks>
/// <para>
/// docs/design-macos.md's list is the contract. The Mac maps its own end reasons
/// (<c>DictationVocabularyModel.endObservationFeedback</c>); these are the
/// Windows equivalents, landing on the same sentences:
/// </para>
/// <list type="bullet">
/// <item>ended from outside (the next dictation, pause, close, the switch,
/// the user clearing the section):
/// no notice, as the Mac's <c>abandoned</c>;</item>
/// <item>no focused field, no text pattern, the pasted text not found once in
/// the field: "The field could not be read." (<c>no_focused_field</c>,
/// <c>span_missing</c>);</item>
/// <item>the span lost its anchor: "The dictated text moved…"
/// (<c>span_context_lost</c>);</item>
/// <item>a password field or one that could not be shown not to be, another
/// application, a console (never watched), this app's own window: "This
/// field was not checked." (<c>secure_field</c>, <c>other_application</c>);</item>
/// <item>everything else that stopped the observer — a paste target nobody
/// could name, a document too long to read, a UI Automation timeout or
/// failure: "This field could not be checked.";</item>
/// <item>an observation that ran its course (settled, navigated, cleared or
/// timed out) without a revision for learning: "No correction detected." when
/// the span never changed and "No vocabulary change was learned." when it
/// changed but nothing settled into a correction — docs/design-macos.md's "no edit" and
/// "an edit that taught nothing".</item>
/// </list>
/// <para>
/// After a revision went to learning, its own notice stands when the
/// observation runs its course, and goes when the observer stopped for any
/// other reason.
/// </para>
/// <para>
/// Those last two follow docs/design-macos.md and depart from the Mac's running code. Its
/// continuous observer never hands a span to the end of a watch, so an
/// untouched watch that ran its course falls to "This field could not be
/// checked.", and every end after a revision clears that revision's notice
/// (a Windows-side decision of 2026-10-09).
/// </para>
/// </remarks>
public static class CorrectionNotices
{
    /// <summary>While a watch runs.</summary>
    public static string Checking => DictationCopy.CorrectionChecking;

    /// <summary>The notice for a watch that ended.</summary>
    /// <param name="notice">The sentence to show, or null for none; only
    /// meaningful when this returns true.</param>
    /// <returns>False when the notice already showing stands: the watch ran
    /// its course after a revision whose own notice is the news.</returns>
    public static bool TryForEnding(CorrectionWatchEnded ended, out string? notice)
    {
        ArgumentNullException.ThrowIfNull(ended);
        notice = null;
        if (ended.EndedBy is not null)
        {
            return true;
        }

        var ranItsCourse = ended.Terminal is SpanObservation.TerminalSettled or SpanObservation.TerminalNavigated
            or SpanObservation.TerminalCleared or SpanObservation.TerminalTimeout;
        if (ended.Revisions > 0)
        {
            return !ranItsCourse;
        }

        notice = ranItsCourse
            ? ended.SawChange ? DictationCopy.CorrectionNothingLearned : DictationCopy.CorrectionNotDetected
            : ForReason(ended.Reason);
        return true;
    }

    /// <summary>The notice for one revision's learning outcome, or null for
    /// none: a newly learned word or rewrite that is saved (the learning
    /// receipt says so, <c>VocabularyModel.Receipt.cs</c>), or a change that
    /// is not saved (the unsaved notice already says so).</summary>
    public static string? ForOutcome(CorrectionWatchObserved observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        var outcome = observed.Outcome;
        if (outcome.Write == LearningWrite.Saved && outcome.NewWords + outcome.NewAliases > 0)
        {
            return null;
        }

        if (outcome.Write != LearningWrite.Unchanged)
        {
            return outcome.Write == LearningWrite.NotSaved ? null
                : outcome.PendingCorrections > 0 ? DictationCopy.CorrectionPending
                : outcome.Candidates > 0 ? DictationCopy.CorrectionAlreadyKnown
                : DictationCopy.CorrectionNothingLearned;
        }

        return outcome.Rejections > 0 || observed.Edited
            ? DictationCopy.CorrectionNothingLearned
            : DictationCopy.CorrectionNotDetected;
    }

    private static string ForReason(string? reason) => reason switch
    {
        SpanObservation.ReasonNoFocus
            or SpanObservation.ReasonNoTextPattern
            or SpanObservation.ReasonAnchorAmbiguous => DictationCopy.CorrectionFieldUnread,
        SpanObservation.ReasonAnchorLost => DictationCopy.CorrectionTextMoved,
        SpanObservation.ReasonSecure
            or SpanObservation.ReasonSecureUnknown
            or SpanObservation.ReasonSelf
            or SpanObservation.ReasonTerminal
            or CorrectionWatch.ReasonOtherApplication => DictationCopy.CorrectionFieldNotChecked,
        _ => DictationCopy.CorrectionFieldUncheckable,
    };
}
