using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationController (see DictationController.cs): the hook watch.
// Windows removes a low-level keyboard hook that overruns LowLevelHooksTimeout
// and tells nobody, and keys sent to a window running as administrator never
// reach the hook at all. Both used to look like a hold key that silently does
// nothing while the icon says "hold Right Ctrl to dictate". The decisions are
// DictationHookWatch and AdminWindowWatch (pure); this is where the poll
// feeds them and acts. The shared partials only call in: Tick (Hold.cs),
// StopTrigger (Session.cs, which forgets the watch with the hook it belongs
// to) and the tooltip and shortcut line in Render (Session.cs).
//
// The hook stays on the UI thread. Moving it to a thread of its own would make
// removal rarer; that change waits for Windows hardware to measure it on.
public sealed partial class DictationController
{
    /// <summary>The watch of the hook armed now. A new one with every hook:
    /// <see cref="ForgetHookWatch"/>, from StopTrigger, which runs before every
    /// Arm.</summary>
    private DictationHookWatch _hookWatch = new();

    /// <summary>The window in front, made at the first poll after an Arm.</summary>
    private AdminWindowWatch? _adminWindow;

    /// <summary>A loss to report once the dictation under way, if any, is
    /// over: the sentence, and whether the window comes forward with it.</summary>
    private (DictationStatus Status, bool Reveal)? _hookReport;

    /// <summary>A window running as administrator is in front while dictation
    /// is on, so the hold key cannot be heard.</summary>
    private bool AdminWindowInFront => _session is not null && _adminWindow?.InFront == true;

    /// <summary>The window's shortcut line, with the administrator-window
    /// hint after it while one is in front.</summary>
    private string ShortcutLine => AdminWindowInFront
        ? DictationCopy.InstructionsWithAdminWindow(_holdKey)
        : DictationCopy.Instructions(_holdKey);

    /// <summary>
    /// One turn of the watch, from the poll: look at the window in front and
    /// at the hold key on both sides when each is due, act on what they say,
    /// and repaint only when something the window or the icon shows changed.
    /// </summary>
    private void WatchHook()
    {
        if (_trigger is not { } hook)
        {
            return;
        }

        _adminWindow ??= new AdminWindowWatch(_platform.ForegroundElevation);
        var now = _options.Clock();
        var repaint = LookAtTheWindowInFront(now);
        if (_hookWatch.Due(now))
        {
            repaint |= LookAtTheHook(hook, now);
        }

        if (ReportHookLoss())
        {
            return;
        }

        if (repaint)
        {
            Render();
        }
    }

    /// <summary>
    /// Forget everything the watch knew about the hook going away, so the
    /// next one starts clean: the Render at the next Arm (Enable, a new hold
    /// key) must not show the old hook's "can't hear" picture or name the new
    /// key in the old one's sentence, and a loss still waiting to be reported
    /// belongs to a hook that no longer exists.
    /// </summary>
    private void ForgetHookWatch()
    {
        _hookWatch = new DictationHookWatch();
        _adminWindow = null;
        _hookReport = null;
    }

    /// <returns>Whether the administrator-window hint came or went.</returns>
    private bool LookAtTheWindowInFront(TimeSpan now)
    {
        // Every poll while the two sides disagree, so the reading that
        // confirms it never acts on a window that has gone since.
        if (_adminWindow is not { } watch || !(watch.Due(now) || _hookWatch.Suspecting))
        {
            return false;
        }

        bool changed;
        try
        {
            changed = watch.Check(now);
        }
        catch (Exception error)
        {
            // Never the process's name or its window's title: the type only.
            _log.LogDebug("The window in front could not be checked for elevation ({Error})", error.GetType().Name);
            return false;
        }

        if (changed)
        {
            if (watch.InFront)
            {
                _log.LogInformation(
                    "A window running as administrator is in front; {Key} cannot reach Dictation until it is not",
                    _holdKey.KeyName);
            }
            else
            {
                _log.LogDebug("No window running as administrator is in front any more");
            }
        }

        return changed;
    }

    /// <returns>Whether the icon's state changed.</returns>
    private bool LookAtTheHook(IDictationTrigger hook, TimeSpan now)
    {
        HookVerdict verdict;
        try
        {
            verdict = _hookWatch.Observe(now, hook.ReadHook(), _adminWindow?.Kind ?? WindowInFront.Ordinary);
        }
        catch (Exception error)
        {
            _log.LogDebug("The hold key could not be read for the hook watch ({Error})", error.GetType().Name);
            return false;
        }

        if (verdict.Healed)
        {
            _log.LogInformation("The reinstalled hold-to-talk hook is receiving keys");
        }

        var edge = verdict.KeyDown ? "down" : "up";
        switch (verdict.Finding)
        {
            case HookFinding.Suspected:
                _log.LogDebug("The hook and Windows disagree about {Key}; checking", _holdKey.KeyName);
                if (verdict.Probe)
                {
                    var sent = false;
                    Try(() => sent = hook.ProbeHook(), "send the hook a test key");
                    if (!sent)
                    {
                        // Its silence would prove nothing.
                        _hookWatch.TestKeyRefused();
                        _log.LogDebug("Windows refused the hook's test key");
                    }
                }

                break;

            case HookFinding.HeldBack:
                _log.LogInformation(
                    "Windows shows {Key} up while the hook keeps receiving it, so another program's keyboard hook "
                    + "may be holding it back; the hold carries on",
                    _holdKey.KeyName);
                break;

            case HookFinding.Unproven:
                Try(() => hook.Resync(verdict.KeyDown), "correct the hook's view of the key");
                _log.LogInformation(
                    "The hook missed {Key} going {Edge} and Windows refused the test key, so whether the hook still "
                    + "receives keys cannot be told; its view was corrected",
                    _holdKey.KeyName, edge);
                break;

            case HookFinding.EdgeMissed:
                Try(() => hook.Resync(verdict.KeyDown), "correct the hook's view of the key");
                _log.LogInformation(
                    "The hook missed {Key} going {Edge} but still receives keys; its view was corrected",
                    _holdKey.KeyName, edge);
                break;

            case HookFinding.AdminWindow:
                Try(() => hook.Resync(verdict.KeyDown), "correct the hook's view of the key");
                _log.LogInformation(
                    "{Key} went {Edge} in a window running as administrator, where the hook cannot hear it",
                    _holdKey.KeyName, edge);
                if (verdict.KeyDown)
                {
                    // Pressed there: nothing recorded, and this says why. Not
                    // brought forward: an administrator window is a limit of
                    // Windows, not a fault to fix.
                    _hookReport ??= (new DictationStatus(DictationCopy.AdminWindowInFront(_holdKey)), false);
                }

                break;

            case HookFinding.Lost:
                Try(() => hook.Resync(verdict.KeyDown), "correct the hook's view of the key");
                var reinstalled = false;
                Try(() => reinstalled = hook.Reinstall(), "reinstall the hook");
                long events = 0;
                Try(() => events = hook.ReadHook().HookEvents, "read the hook");
                _hookWatch.Repaired(reinstalled, events);
                _log.LogWarning(
                    "The hold-to-talk hook stopped receiving keys: Windows had {Key} {Edge} where the hook never saw "
                    + "it, and a test key sent to it {Ms:0} ms before never arrived; reinstalled: {Reinstalled}",
                    _holdKey.KeyName, verdict.MissedPress ? "down" : "up", verdict.Silent.TotalMilliseconds, reinstalled);
                var sentence = reinstalled
                    ? DictationCopy.HoldKeyReconnected(_holdKey)
                    : DictationCopy.HoldKeyUnheard(_holdKey);
                _hookReport = (new DictationStatus(sentence), verdict.Reveal || _hookReport?.Reveal == true);
                return true;
        }

        return verdict.Healed;
    }

    /// <summary>
    /// Say what the watch found, once nothing is under way: the status line,
    /// and the window brought forward once per loss. Not during a dictation:
    /// the session's own lines would replace the sentence at once, and the
    /// window coming forward before the paste would take the words.
    /// </summary>
    /// <returns>Whether the window was repainted.</returns>
    private bool ReportHookLoss()
    {
        if (_hookReport is not { } report
            || !AtRest
            || _fieldCheck is not null
            || _shownState is not (DictationState.Armed or DictationState.Idle))
        {
            return false;
        }

        _hookReport = null;
        // A blocked microphone already on the status line stays first, with
        // its buttons, and this follows it: both are current, and the Mac puts
        // the microphone first (DictationCopy.WithHoldKeySentence).
        Render(DictationCopy.IsMicrophoneBlocked(_status)
            ? DictationCopy.WithHoldKeySentence(_status, report.Status.Text)
            : report.Status);
        if (report.Reveal)
        {
            _view.Reveal();
        }

        return true;
    }

    /// <summary>One call to the hook, which must not take the poll down.</summary>
    private void Try(Action call, string what)
    {
        try
        {
            call();
        }
        catch (Exception error)
        {
            _log.LogWarning("The hook watch could not {What} ({Error})", what, error.GetType().Name);
        }
    }
}
