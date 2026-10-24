using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the window's status
// header, "Start Dictation when I sign in" and Troubleshooting, the Mac's
// (DictationHomeView.swift: DictationStatusHeader; DictationSettingsView.swift: the
// General section and the Troubleshooting disclosure), and how often the window polls. Hooks: Start
// (ReadLaunchAtLogin), Render (Header, the note), MainWindow's Activated
// (RefreshLaunchAtLogin), its poll and Render (PollInterval).
public sealed partial class DictationController
{
    /// <summary>The note under the sign-in box, or null: a change Windows
    /// refused, or the entry switched off in Settings → Apps → Startup.</summary>
    private string? _launchAtLoginNote;

    // ─── The status header ──────────────────────────────────────────────

    /// <summary>
    /// The header for this render: the glyph the icon is drawn from, its
    /// headline, and the one Enable/Pause button with the menu item's rule.
    /// </summary>
    /// <param name="glyph">This render's <see cref="DictationTrayGlyph.State"/>,
    /// the same value the icon's picture and tooltip come from.</param>
    /// <param name="armed">Dictation is on.</param>
    /// <param name="canEnable">Enable is live.</param>
    /// <param name="canPause">Pause is live (<see cref="PauseAllowed"/>).</param>
    private static DictationWindowHeader Header(DictationGlyph glyph, bool armed, bool canEnable, bool canPause) =>
        new(
            glyph,
            DictationTrayGlyph.Headline(glyph),
            armed ? DictationCopy.PauseLabel : DictationCopy.EnableLabel,
            ToggleEnables: !armed,
            CanToggle: armed ? canPause : canEnable);

    // ─── Start at sign-in ───────────────────────────────────────────────

    /// <summary>
    /// "Start Dictation when I sign in": write or remove the entry, then show
    /// what Windows has. A change Windows refused leaves a note under the box
    /// that says what to do, as the Mac's toggle does; the status line keeps
    /// what it said.
    /// </summary>
    public void SetLaunchAtLogin(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        bool saved;
        try
        {
            saved = _platform.LaunchAtLogin.TrySet(enabled);
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "Changing the sign-in entry failed");
            saved = false;
        }

        ReadLaunchAtLogin();
        _log.LogInformation(
            "Start at sign-in turned {State} (saved: {Saved})", enabled ? "on" : "off", saved);
        if (!saved || _launchAtLogin != enabled)
        {
            _log.LogWarning(
                "Start at sign-in: turning it {State} was refused; the box shows what Windows has", enabled ? "on" : "off");
            _launchAtLoginNote = enabled ? DictationCopy.LaunchAtLoginNotTurnedOn : DictationCopy.LaunchAtLoginNotTurnedOff;
        }

        Render();
    }

    /// <summary>
    /// Read the sign-in entry again. The user can also change it in Settings
    /// → Apps → Startup or Task Manager while Dictation runs, so the window
    /// asks each time it is activated. The Mac re-reads its login item only
    /// when its window appears, and that read drops a refusal's note; an
    /// activation comes far more often (an Alt+Tab, or back from the log
    /// folder the note itself points at), so here a refusal's note stays
    /// while the box reads back as the refusal left it. It goes when the
    /// entry changes, or gives way to the Settings note when Settings has
    /// switched the entry off. Repaints only when the box or its note changed.
    /// </summary>
    public void RefreshLaunchAtLogin()
    {
        if (_disposed)
        {
            return;
        }

        var (wasOn, wasNote) = (_launchAtLogin, _launchAtLoginNote);
        ReadLaunchAtLogin();
        if (wasOn == _launchAtLogin
            && _launchAtLoginNote is null
            && wasNote is DictationCopy.LaunchAtLoginNotTurnedOn or DictationCopy.LaunchAtLoginNotTurnedOff)
        {
            _launchAtLoginNote = wasNote;
        }

        if (wasOn == _launchAtLogin && wasNote == _launchAtLoginNote)
        {
            return;
        }

        _log.LogInformation(
            "Start at sign-in read again: {State}{Settings}",
            _launchAtLogin ? "on" : "off",
            _launchAtLoginNote == DictationCopy.LaunchAtLoginSwitchedOff ? " (switched off in Settings)" : string.Empty);
        Render();
    }

    /// <summary>The box and its note as Windows has them now. A read that
    /// fails reads as off, with no note: there is nothing the user can act
    /// on, and the log has the reason.</summary>
    private void ReadLaunchAtLogin()
    {
        try
        {
            _launchAtLogin = _platform.LaunchAtLogin.IsEnabled;
            _launchAtLoginNote = !_launchAtLogin && _platform.LaunchAtLogin.SwitchedOffInSettings
                ? DictationCopy.LaunchAtLoginSwitchedOff
                : null;
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "Reading the sign-in entry failed");
            _launchAtLogin = false;
            _launchAtLoginNote = null;
        }
    }

    // ─── The window's poll ──────────────────────────────────────────────

    /// <summary>The poll while there is work for <see cref="Tick"/>: the hold
    /// key, the level bars and the copy deadline. The level meter's sampling
    /// arithmetic assumes it (<c>DictationHudLevelTests</c>).</summary>
    public static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(30);

    /// <summary>The poll while dictation is off, no copy is out and the
    /// indicator shows nothing: about the Mac's once-a-second watch while it
    /// is off. The indicator's heartbeat and the microphone watch's
    /// once-a-second reading of a known block keep their second at it.</summary>
    public static readonly TimeSpan RestingPoll = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often the window's timer should call <see cref="Tick"/> now:
    /// <see cref="BusyPoll"/> while dictation is on or starting, a Copy last
    /// text waits on the clipboard, or the indicator shows a pill or holds
    /// one back (<see cref="DictationHudFeed.Active"/>: its 375 ms, its dwell
    /// and the heartbeat that heals it run on the tick, closed window or
    /// not); <see cref="RestingPoll"/> otherwise. Off, Tick has no hold key
    /// to read, so since a close no longer quits a paused Dictation, this
    /// keeps one hidden in the notification area for days from waking 33
    /// times a second. The window asks after every tick and every render, so
    /// an Enable or a copy speeds it up at once.
    /// </summary>
    public TimeSpan PollInterval =>
        _session is not null || _enabling || _recent.Copying || _hud?.Active == true ? BusyPoll : RestingPoll;

    // ─── Troubleshooting ────────────────────────────────────────────────

    /// <summary>
    /// Show this copy in File Explorer, selected in its folder: the Mac's
    /// Show This Copy in Finder. When File Explorer cannot be started, or
    /// the file is no longer at that path, the status line says so and points
    /// at the path shown above the button. A path Explorer starts but cannot
    /// select is not seen here: Explorer opens a folder of its own choosing.
    /// </summary>
    /// <param name="executable">This copy's path, as Troubleshooting shows it
    /// (<see cref="DictationInstallation.Path"/>).</param>
    /// <returns>Whether File Explorer was asked to show it.</returns>
    public bool ShowThisCopy(string executable)
    {
        if (_disposed)
        {
            return false;
        }

        var shown = false;
        try
        {
            shown = _platform.ShowInFolder(executable);
        }
        catch (Exception error)
        {
            // The type only: the message may hold the path, and the path
            // holds the user's name.
            _log.LogWarning("Showing this copy in File Explorer failed ({Error})", error.GetType().Name);
        }

        if (!shown)
        {
            Render(DictationCopy.ThisCopyNotShown);
        }

        return shown;
    }
}
