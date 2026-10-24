namespace Dictation.Speech;

/// <summary>What a hold turned out to be.</summary>
public enum RoutingOutcome
{
    /// <summary>
    /// Ordinary speech. Type it — the behaviour dictation has always had, and
    /// the one every hold gets when no router is registered.
    /// </summary>
    Dictate = 0,

    /// <summary>
    /// A command was asked for and it ran. <b>Nothing is typed.</b>
    /// </summary>
    RanWorkflow = 1,

    /// <summary>
    /// The words were addressed to the router and no command answered.
    /// <b>Nothing is typed.</b>
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="Dictate"/>, and the difference is the whole
    /// reason this value exists. Somebody who misspoke a command was giving an
    /// instruction, not writing; typing that sentence into their document is a
    /// worse failure than doing nothing and saying so. The indicator carries
    /// the apology.
    /// </remarks>
    NotMatched = 2,
}

/// <summary>
/// What to do with one hold's words.
/// </summary>
/// <param name="TextToInsert">
/// What to type, for <see cref="RoutingOutcome.Dictate"/> only. Null for every
/// other outcome, because those outcomes are exactly the ones where nothing is
/// typed and a text that survived them would be a text somebody eventually
/// pastes.
/// </param>
public sealed record TranscriptRouting(RoutingOutcome Outcome, string? TextToInsert = null)
{
    /// <summary>Type it, unchanged.</summary>
    public static TranscriptRouting Dictate(string text) =>
        new(RoutingOutcome.Dictate, text);

    /// <summary>A command ran. Type nothing.</summary>
    public static readonly TranscriptRouting RanWorkflow = new(RoutingOutcome.RanWorkflow);

    /// <summary>Addressed, and no command answered. Type nothing.</summary>
    public static readonly TranscriptRouting NotMatched = new(RoutingOutcome.NotMatched);

    /// <summary>Whether the words are still going to the cursor.</summary>
    public bool ShouldInsert => Outcome == RoutingOutcome.Dictate;
}

/// <summary>
/// Decides whether a hold was something to type or something to do.
///
/// <para>
/// <b>One key does both.</b> When commands share the dictation key, one hold
/// produces one transcript and something has to read it, and this is that
/// something.
/// </para>
///
/// <para>
/// It sits between the recogniser and the cursor, which is the only place it can
/// sit: before the recogniser there are no words to route on, and after the
/// insert the words are already in somebody's document.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>It lives in the workflows layer, and dictation calls down into it.</b>
/// That is the direction the module graph already runs —
/// <c>Dictation.Services</c> references <c>Dictation.Speech</c>,
/// not the reverse — so the seam belongs on the lower side and the caller is
/// the upper one. Declaring it in dictation would have inverted the reference
/// and closed a cycle.
/// </para>
/// <para>
/// <b>Absent means absent.</b> A session with no router types everything, so
/// a build without commands is a build that never constructed one, not a build
/// with a disabled branch in it. Dictation is such a build: it composes none.
/// </para>
/// </remarks>
public interface ITranscriptRouter
{
    /// <summary>
    /// What to do with <paramref name="transcript"/>.
    /// </summary>
    /// <remarks>
    /// Never throws for an ordinary failure. A router that cannot decide must
    /// return <see cref="TranscriptRouting.Dictate"/> — losing a command costs
    /// a repeat, and losing a sentence somebody dictated costs their words.
    /// </remarks>
    Task<TranscriptRouting> RouteAsync(string transcript, CancellationToken cancellationToken = default);
}
