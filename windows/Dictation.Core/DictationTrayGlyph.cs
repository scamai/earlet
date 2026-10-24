using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>
/// The states the notification-area icon and the window's header can show,
/// one per picture and line: the Mac's <c>DictationMenuGlyph</c>
/// (macos/Sources/DictationAppCore/DictationMenuGlyph.swift), with Windows'
/// states.
/// </summary>
public enum DictationGlyph
{
    /// <summary>Paused, never enabled, or an Enable that failed.</summary>
    Off,

    /// <summary>An Enable is loading the speech model.</summary>
    Starting,

    /// <summary>On and at rest: holding the key dictates.</summary>
    Ready,

    /// <summary>A hold, held or hands-free, is listening.</summary>
    Listening,

    /// <summary>Released; the words are being transcribed.</summary>
    Transcribing,

    /// <summary>The words are being typed into the field: a Windows-only
    /// state, which the Mac counts as transcribing.</summary>
    Typing,

    /// <summary>Windows blocks the microphone, as this run last learned: the
    /// Mac's <c>needsPermission</c>.</summary>
    MicrophoneBlocked,

    /// <summary>On, and Windows removed the hold-key hook and would not let
    /// it be put back (<see cref="HookHealth.Unheard"/>): the warning, as a
    /// blocked microphone draws it. The Mac's lost key listener is
    /// <c>needsPermission</c> too.</summary>
    HoldKeyUnheard,

    /// <summary>On, with a window running as administrator in front, where
    /// the hook hears nothing: the ready picture, saying why the key does
    /// nothing there. A limit of Windows, not a fault to fix; Windows only.</summary>
    AdminWindowInFront,

    /// <summary>On, with a hook put back after Windows removed it that has
    /// not heard a key yet (<see cref="HookHealth.Reconnected"/>): the ready
    /// picture, asking for the key again. Windows only.</summary>
    HoldKeyReconnected,
}

/// <summary>How the window's header tints its glyph, after the Mac header's
/// system colours (<c>DictationStatusHeader.tint</c>).</summary>
public enum DictationGlyphTint
{
    /// <summary>Off or starting: the secondary text colour.</summary>
    Secondary,

    /// <summary>On and at rest: green.</summary>
    Positive,

    /// <summary>The microphone is open: red.</summary>
    Recording,

    /// <summary>Transcribing or typing: the accent colour.</summary>
    Accent,

    /// <summary>Something the user must fix: orange.</summary>
    Attention,
}

/// <summary>
/// What the notification-area icon and the window's header say about
/// dictation, resolved by one pure function so they cannot disagree: the
/// Mac's <c>DictationMenuGlyph.resolve</c>
/// (macos/Sources/DictationAppCore/DictationMenuGlyph.swift), with Windows'
/// states. The icon's picture and tooltip and the header's glyph and
/// headline are all read from the one <see cref="State"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Precedence, the Mac's, in this one function.</b> Three things can be
/// wrong at once (a blocked microphone, a hold-key hook Windows removed, a
/// window running as administrator in front), and the icon shows one. From
/// the top, the first that holds wins:
/// </para>
/// <list type="number">
/// <item>A dictation under way: listening, transcribing, typing. It proves
/// the microphone opened, and the indicator is on screen anyway (the Mac's
/// <c>recording</c> and <c>transcribing</c>).</item>
/// <item>A blocked microphone, on or off: the warning. The Mac's
/// <c>needsPermission</c>, which it checks before it looks at whether
/// dictation is enabled, so a broken setup never looks fine and a pause does
/// not hide it.</item>
/// <item>A hook Windows would not put back, while on: the same warning, with
/// its own tooltip. The Mac's lost key listener is also
/// <c>needsPermission</c>; when both are missing its setup steps put the
/// microphone first (<c>DictationPermissionStep.resolve</c>: microphone,
/// then the shortcut's Input Monitoring), so the microphone's tooltip
/// wins here.</item>
/// <item>A window running as administrator in front, while on: the ready
/// picture, saying why the key does nothing there. It explains every hold
/// until it leaves, so it outranks the line below.</item>
/// <item>A reconnected hook that has not heard a key yet, while on: the ready
/// picture, asking for the key again.</item>
/// <item>Then the quiet states: ready, starting, off.</item>
/// </list>
/// <para>
/// The hook watch reports only the hook (<see cref="HookHealth"/>) and the
/// controller only whether an administrator window is in front; neither
/// ranks anything, so this list is the only place the order lives.
/// </para>
/// <para>
/// <b>What Windows leaves out.</b> The Mac's <c>needsReopen</c> (Windows asks
/// for no grant that needs a relaunch) and <c>engineFailed</c> (the Mac's
/// model never publishes it either; a failed Enable reads as off there too,
/// and the status line says why). Typing is the Mac's transcribing, with its
/// own words: Windows' icon tells the paste from the decode.
/// </para>
/// <para>
/// <b>Where "blocked" comes from.</b> What this run last learned about the
/// microphone (<c>DictationController</c>'s probe at Enable, a hold that
/// Windows kept the microphone from, Check again, the recheck when the window
/// is activated, and the microphone watch, <see cref="DictationMicrophoneWatch"/>,
/// which reads it every 5 s while dictation is on and every second while a
/// block is known). The warning stays until one of those finds access
/// allowed, or a dictation delivers its words.
/// </para>
/// </remarks>
public static class DictationTrayGlyph
{
    /// <summary>The state for the controller's state now: the one decision
    /// the icon and the window's header both draw, with the whole precedence
    /// above. Every input is required, the hook's and the window in front's
    /// too, so no caller can draw one of them from less than the other sees.</summary>
    /// <param name="armed">Dictation is on: the hold key is claimed.</param>
    /// <param name="enabling">An Enable is loading the speech model.</param>
    /// <param name="shown">The dictation state the window shows; read only
    /// while armed.</param>
    /// <param name="microphoneBlocked">Windows blocks the microphone, as this
    /// run last learned.</param>
    /// <param name="hook">The hold-key hook's state (<see cref="DictationHookWatch.Health"/>);
    /// read only while armed.</param>
    /// <param name="adminWindowInFront">A window running as administrator is
    /// in front, where the hook hears nothing; read only while armed.</param>
    public static DictationGlyph State(
        bool armed,
        bool enabling,
        DictationState shown,
        bool microphoneBlocked,
        HookHealth hook,
        bool adminWindowInFront)
    {
        // 1. A dictation under way. Only an armed session's state is one.
        if (armed)
        {
            switch (shown)
            {
                case DictationState.Listening:
                    return DictationGlyph.Listening;
                case DictationState.Transcribing:
                    return DictationGlyph.Transcribing;
                case DictationState.Inserting:
                    return DictationGlyph.Typing;
            }
        }

        // 2. A blocked microphone, on or off.
        if (microphoneBlocked)
        {
            return DictationGlyph.MicrophoneBlocked;
        }

        if (armed)
        {
            // 3. A hook Windows would not put back; 4. a window running as
            // administrator in front; 5. a reconnected hook still waiting for
            // its first key; 6. ready.
            if (hook == HookHealth.Unheard)
            {
                return DictationGlyph.HoldKeyUnheard;
            }

            if (adminWindowInFront)
            {
                return DictationGlyph.AdminWindowInFront;
            }

            return hook == HookHealth.Reconnected ? DictationGlyph.HoldKeyReconnected : DictationGlyph.Ready;
        }

        // 6. The quiet states. Starting keeps the off picture, as the Mac's
        // keeps mic.slash: the microphone is not on yet.
        return enabling ? DictationGlyph.Starting : DictationGlyph.Off;
    }

    /// <summary>The icon's picture and tooltip for <paramref name="glyph"/>.</summary>
    /// <param name="holdKey">The hold key, which the tooltips name.</param>
    public static (TrayIcon Icon, string ToolTip) Picture(DictationGlyph glyph, HotkeyBinding holdKey) =>
        (IconOf(glyph), ToolTipOf(glyph, holdKey));

    /// <summary>The picture for <paramref name="glyph"/>: the icon's, and the
    /// window header's glyph (<see cref="TrayIconArt.Mask"/>). Typing shares
    /// transcribing's waveform, starting shares off's microphone, a hook
    /// Windows would not put back shares the microphone's warning, and an
    /// administrator window or a reconnected hook keeps the ready picture.</summary>
    public static TrayIcon IconOf(DictationGlyph glyph) => glyph switch
    {
        DictationGlyph.Listening => TrayIcon.Listening,
        DictationGlyph.Transcribing or DictationGlyph.Typing => TrayIcon.Transcribing,
        DictationGlyph.MicrophoneBlocked or DictationGlyph.HoldKeyUnheard => TrayIcon.MicrophoneBlocked,
        DictationGlyph.Ready or DictationGlyph.AdminWindowInFront or DictationGlyph.HoldKeyReconnected => TrayIcon.Ready,
        _ => TrayIcon.Off,
    };

    /// <summary>The icon's tooltip for <paramref name="glyph"/>, which
    /// carries the state the Mac menu's headline does.</summary>
    /// <param name="holdKey">The hold key, which the tooltips name.</param>
    public static string ToolTipOf(DictationGlyph glyph, HotkeyBinding holdKey) => glyph switch
    {
        DictationGlyph.Listening => DictationCopy.TrayListening,
        DictationGlyph.Transcribing => DictationCopy.TrayTranscribing,
        DictationGlyph.Typing => DictationCopy.TrayTyping,
        DictationGlyph.MicrophoneBlocked => DictationCopy.TrayMicrophoneBlocked,
        DictationGlyph.HoldKeyUnheard => DictationCopy.TrayHoldKeyUnheard(holdKey),
        DictationGlyph.AdminWindowInFront => DictationCopy.TrayAdminWindowInFront(holdKey),
        DictationGlyph.HoldKeyReconnected => DictationCopy.TrayHoldKeyReconnected(holdKey),
        DictationGlyph.Ready => DictationCopy.TrayReady(holdKey),
        DictationGlyph.Starting => DictationCopy.TrayStarting,
        _ => DictationCopy.TrayOff,
    };

    /// <summary>The window header's one-line headline over the status line:
    /// the Mac's <c>DictationMenuGlyph.headline</c>, with Windows' typing, its
    /// one grant, the microphone, and the hook watch's three states, each
    /// saying what the icon's tooltip says.</summary>
    public static string Headline(DictationGlyph glyph) => glyph switch
    {
        DictationGlyph.Starting => DictationCopy.HeadlineStarting,
        DictationGlyph.Ready => DictationCopy.HeadlineReady,
        DictationGlyph.Listening => DictationCopy.HeadlineListening,
        DictationGlyph.Transcribing => DictationCopy.HeadlineTranscribing,
        DictationGlyph.Typing => DictationCopy.HeadlineTyping,
        DictationGlyph.MicrophoneBlocked => DictationCopy.HeadlineMicrophoneBlocked,
        DictationGlyph.HoldKeyUnheard => DictationCopy.HeadlineHoldKeyUnheard,
        DictationGlyph.AdminWindowInFront => DictationCopy.HeadlineAdminWindowInFront,
        DictationGlyph.HoldKeyReconnected => DictationCopy.HeadlineHoldKeyReconnected,
        _ => DictationCopy.HeadlineOff,
    };

    /// <summary>The header glyph's tint: the Mac header's, green when on, red
    /// while the microphone is open, the accent colour while the words go
    /// in, orange for anything to fix (a blocked microphone, a hook Windows
    /// would not put back), secondary when off or starting. An administrator
    /// window and a reconnected hook are on, and green, as their ready
    /// picture is on the icon.</summary>
    public static DictationGlyphTint Tint(DictationGlyph glyph) => glyph switch
    {
        DictationGlyph.Ready or DictationGlyph.AdminWindowInFront or DictationGlyph.HoldKeyReconnected =>
            DictationGlyphTint.Positive,
        DictationGlyph.Listening => DictationGlyphTint.Recording,
        DictationGlyph.Transcribing or DictationGlyph.Typing => DictationGlyphTint.Accent,
        DictationGlyph.MicrophoneBlocked or DictationGlyph.HoldKeyUnheard => DictationGlyphTint.Attention,
        _ => DictationGlyphTint.Secondary,
    };
}
