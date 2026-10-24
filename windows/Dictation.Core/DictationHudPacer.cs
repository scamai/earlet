namespace Dictation.Core;

/// <summary>Which kind of pill the indicator shows, as the pacer sees it.</summary>
public enum DictationHudPace
{
    /// <summary>Nothing on screen.</summary>
    Hidden,

    /// <summary>The hold listens, held or hands-free: the Mac's
    /// <c>recording</c>.</summary>
    Listening,

    /// <summary>"Transcribing…": the Mac's <c>transcribing</c>.</summary>
    Transcribing,

    /// <summary>"Typing", while the paste goes in: Windows only.</summary>
    Typing,

    /// <summary>How a dictation ended, a refused press, Copy last text's
    /// answer, a learned correction or the recogniser giving up: the Mac's
    /// <c>outcome</c>. The Mac's <c>micChanged</c> pre-empts exactly as an
    /// outcome does, and Windows says a microphone change as an ending, so a
    /// new hold replaces it at once, where the Mac's next hold waits out the
    /// notice's 375 ms (<c>IndicatorWatchTests</c>).</summary>
    Ending,
}

/// <summary>
/// One state the indicator can show, as the pacer compares them: its kind,
/// and the name that tells two states of one kind apart.
/// </summary>
/// <param name="Kind">The kind of pill.</param>
/// <param name="Name">What makes it a different state of that kind: the
/// listening pill's constant name ("Listening" or "Listening, hands-free"),
/// so the clock and the caption redrawn each second are the same state, as
/// the Mac's <c>recording(latched:)</c> is; an ending's whole line.</param>
public readonly record struct DictationHudPaceState(DictationHudPace Kind, string Name = "")
{
    /// <summary>Nothing on screen.</summary>
    public static readonly DictationHudPaceState Hidden = new(DictationHudPace.Hidden);
}

/// <summary>The pacer's answer to an offered state.</summary>
/// <param name="Show">Show the offered state now.</param>
/// <param name="RetryAfter">When <paramref name="Show"/> is false: how long
/// the state on screen still has to stand, after which the offer is made
/// again.</param>
public readonly record struct DictationHudPaceDecision(bool Show, TimeSpan RetryAfter)
{
    /// <summary>Show the offered state now.</summary>
    public static readonly DictationHudPaceDecision Now = new(true, TimeSpan.Zero);

    /// <summary>Keep the state on screen; offer again after <paramref name="retryAfter"/>.</summary>
    public static DictationHudPaceDecision Hold(TimeSpan retryAfter) => new(false, retryAfter);
}

/// <summary>
/// Anti-flap pacing for the indicator's visible states: a port of the Mac's
/// <c>DictationHUDPacer</c> (<c>macos/Sources/DictationAppCore/DictationHUD.swift</c>),
/// with its rules and its 375 ms.
/// </summary>
/// <remarks>
/// <para>
/// A visible state must stand for <see cref="VisibleDwell"/> before a
/// DIFFERENT visible state may replace it, so a very short hold no longer
/// flashes "Listening" for a frame on its way to "Transcribing…". Hidden is
/// always allowed at once; an ending always pre-empts (it is what the user
/// must see next, and has its own dwell); a new hold replaces an ending at
/// once (the user has moved on); a change within the listening pill (a hold
/// becoming hands-free) is immediate; and a state shown from hidden never
/// waits.
/// </para>
/// <para>
/// <b>Windows' Typing is a state of its own,</b> so "Transcribing…" stands
/// 375 ms before "Typing" replaces it, and a paste that follows a quick
/// decode goes from "Transcribing…" straight to its ending.
/// </para>
/// <para>
/// Pure and clock-injected, so the whole policy is checked on Linux; the
/// feed (<see cref="DictationHudFeed"/>) owns the retry, on the controller's
/// clock.
/// </para>
/// </remarks>
public sealed class DictationHudPacer
{
    /// <summary>How long a visible state stands before a different visible
    /// state may replace it: the Mac's <c>visibleDwellSeconds</c>.</summary>
    public static readonly TimeSpan VisibleDwell = TimeSpan.FromMilliseconds(375);

    private TimeSpan _since;

    /// <summary>The state on screen, as far as the pacer knows.</summary>
    public DictationHudPaceState Current { get; private set; } = DictationHudPaceState.Hidden;

    /// <summary>Whether <paramref name="desired"/> may replace the state on
    /// screen at <paramref name="now"/>. A state shown is remembered as the
    /// one on screen from then on; a held one is not.</summary>
    public DictationHudPaceDecision Offer(DictationHudPaceState desired, TimeSpan now)
    {
        var current = Current.Kind;
        if (desired.Kind is DictationHudPace.Hidden or DictationHudPace.Ending
            || desired == Current
            || current == DictationHudPace.Hidden
            || (current == DictationHudPace.Listening && desired.Kind == DictationHudPace.Listening)
            || (current == DictationHudPace.Ending && desired.Kind == DictationHudPace.Listening))
        {
            return Accept(desired, now);
        }

        var elapsed = now - _since;
        return elapsed >= VisibleDwell ? Accept(desired, now) : DictationHudPaceDecision.Hold(VisibleDwell - elapsed);
    }

    private DictationHudPaceDecision Accept(DictationHudPaceState state, TimeSpan now)
    {
        if (state != Current)
        {
            _since = now;
        }

        Current = state;
        return DictationHudPaceDecision.Now;
    }
}

/// <summary>
/// The panel's truth against the feed's, read once a second: a port of the
/// Mac's <c>DictationHUDReconcile</c>
/// (<c>macos/Sources/DictationAppCore/DictationHUDPanel.swift</c>).
/// </summary>
/// <remarks>
/// On the Mac one instance ran 34 hours with the press path alive while its
/// panel was never ordered front. Whatever drops a state between the feed and
/// the window, this heartbeat is the floor under it: a disagreement heals
/// within a second instead of lasting until the app restarts, and each heal
/// leaves one log line, so the mechanism is named the next time it happens.
/// No such wedge has been seen on Windows; the window has never run. Windows
/// has no fades, so the Mac's grace for a hide still fading has nothing to
/// guard here.
/// </remarks>
public static class DictationHudReconcile
{
    /// <summary>What the feed does about a disagreement.</summary>
    public enum Action
    {
        /// <summary>The feed wants a pill and the panel is not showing one:
        /// draw it again.</summary>
        Reoffer,

        /// <summary>The feed wants nothing and the panel is showing a pill:
        /// take it away. The Mac's <c>orderOut</c>.</summary>
        Hide,
    }

    /// <summary>The heartbeat's period: a dropped state stays in the "did it
    /// just flicker?" range rather than "the indicator is gone".</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>How often a disagreement that persists is logged again: once
    /// a minute, not once a heartbeat.</summary>
    public static readonly TimeSpan ReportAgainAfter = TimeSpan.FromSeconds(60);

    /// <summary>The action for one heartbeat's reading, or null when the
    /// panel and the feed agree.</summary>
    public static Action? For(bool desiredHidden, bool panelVisible) => (desiredHidden, panelVisible) switch
    {
        (false, false) => Action.Reoffer,
        (true, true) => Action.Hide,
        _ => null,
    };
}
