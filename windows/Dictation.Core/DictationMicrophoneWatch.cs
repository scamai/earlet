using Dictation.Services;

namespace Dictation.Core;

/// <summary>What one reading of microphone access means for the window and
/// the notification-area icon.</summary>
public enum MicrophoneWatchVerdict
{
    /// <summary>Nothing to change: the reading says what the run already
    /// knew, or it could not be read.</summary>
    Unchanged,

    /// <summary>Windows now blocks the microphone, and this run did not know:
    /// say so in the window and the icon, and bring the window forward once.</summary>
    Lost,

    /// <summary>Windows no longer blocks the microphone this run knew to be
    /// blocked: take the warning away.</summary>
    Regained,
}

/// <summary>
/// The watch on microphone access: how often the controller asks Windows
/// whether it blocks the microphone, and what an answer changes. The
/// Windows port of the Mac's <c>DictationPermissionWatch</c> (the cadence)
/// and of the grant half of <c>DictationWindowAttention</c> (the edge rule),
/// in <c>macos/Sources/DictationAppCore/DictationMenuGlyph.swift</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it polls.</b> Windows says nothing to an app when its microphone
/// switch is turned off in Privacy &amp; security, so before this a switch
/// turned off while dictation was on showed only after a hold failed, the
/// window was activated, or Check again was pressed. The answer is a read of
/// the consent store (<c>WindowsMicrophoneAccessProbe</c>), which the
/// controller runs off the UI thread.
/// </para>
/// <para>
/// <b>When.</b> Every 5 seconds while dictation is on, to notice a switch
/// turned off (the Mac's <c>steady</c>); every second while this run knows
/// Windows blocks the microphone, on or off, so the warning goes as soon as
/// the switch is back (the Mac's <c>settingUp</c>). Not while dictation is
/// off with no block known: the hold key does nothing then, and Enable asks
/// Windows first, as it always did. The Mac polls while off too, because its
/// window carries a permission checklist; Windows has none.
/// </para>
/// <para>
/// <b>Edges, not levels.</b> A loss brings the window forward once: a
/// microphone that stays blocked never asks again, so closing the window is
/// respected, and access found allowed resets it, so the next loss asks
/// again. What the run already knew does not count as a loss, whoever
/// learned it (Enable's own probe, a hold the block kept the microphone
/// from, Check again): each of those already said so in its own way, as the
/// Mac's first run without grants is not a loss.
/// </para>
/// </remarks>
public static class DictationMicrophoneWatch
{
    /// <summary>On, with no block known: only to notice one. The Mac's
    /// <c>DictationPermissionWatch.steady</c>.</summary>
    public static readonly TimeSpan Steady = TimeSpan.FromSeconds(5);

    /// <summary>A block is known: to notice the switch turned back on. The
    /// Mac's <c>DictationPermissionWatch.settingUp</c>.</summary>
    public static readonly TimeSpan WhileBlocked = TimeSpan.FromSeconds(1);

    /// <summary>How long after the last reading the next one is due, or null
    /// when nothing is watched.</summary>
    /// <param name="enabled">Dictation is on.</param>
    /// <param name="blockedKnown">This run last learned that Windows blocks
    /// the microphone.</param>
    public static TimeSpan? Interval(bool enabled, bool blockedKnown) =>
        blockedKnown ? WhileBlocked
        : enabled ? Steady
        : null;

    /// <summary>What a reading changes.</summary>
    /// <param name="blockedKnown">This run already knew Windows blocks the
    /// microphone.</param>
    /// <param name="access">The reading.</param>
    /// <remarks>
    /// <see cref="MicrophoneAccess.Unknown"/> changes nothing either way: a
    /// consent store that could not be read is no evidence of a block (the
    /// probe's own rule) and no evidence that one ended, so an unreadable
    /// moment never makes the warning flicker. <see cref="MicrophoneAccess.Busy"/>
    /// is access allowed, as everywhere else in the app
    /// (<see cref="DictationCopy.ForMicrophone"/>).
    /// </remarks>
    public static MicrophoneWatchVerdict Judge(bool blockedKnown, MicrophoneAccess access)
    {
        if (access == MicrophoneAccess.Unknown)
        {
            return MicrophoneWatchVerdict.Unchanged;
        }

        var blocked = DictationCopy.ForMicrophone(access) is not null;
        return (blockedKnown, blocked) switch
        {
            (false, true) => MicrophoneWatchVerdict.Lost,
            (true, false) => MicrophoneWatchVerdict.Regained,
            _ => MicrophoneWatchVerdict.Unchanged,
        };
    }
}
