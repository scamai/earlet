using Dictation.Services;
using Microsoft.Extensions.Logging;

namespace Dictation.Core;

// Part of DictationHudFeed (see DictationHudFeed.cs): when a pill reaches the
// window, as on the Mac. Every show goes through the anti-flap pacer
// (DictationHudPacer), which may hold a busy pill back until the one on
// screen has stood its 375 ms, and a heartbeat once a second compares what
// the feed drew with what the window says it is showing and heals a
// disagreement (DictationHudReconcile), logging one line per wedge.
public sealed partial class DictationHudFeed
{
    private readonly DictationHudPacer _pacer = new();

    /// <summary>A pill the pacer held back, waiting for its retry; null when
    /// none is. Only the newest offer waits: any later show, hide or clear
    /// replaces or drops it, as the Mac's <c>offerDesired</c> cancels its
    /// <c>dwellRetry</c>.</summary>
    private Pill? _deferred;

    /// <summary>The retry of <see cref="_deferred"/>, on the controller's clock.</summary>
    private IDisposable? _retry;

    /// <summary>When the next heartbeat is due, on the controller's clock.</summary>
    private TimeSpan _nextHeartbeat;

    /// <summary>The last disagreement the heartbeat logged and when, so a
    /// wedge that persists is logged once a minute, not once a second.</summary>
    private (DictationHudReconcile.Action Action, TimeSpan At)? _lastReport;

    /// <summary>What the pacer knows is on screen: <see cref="Shown"/>'s
    /// state, or what is waiting to replace it.</summary>
    public DictationHudPaceState Pace => _pacer.Current;

    /// <summary>
    /// A pill is on screen or held back for its retry. Its dwell, the retry
    /// and the heartbeat that heals it all run on <see cref="Tick"/>, so the
    /// window's poll keeps its 30 ms while this is true, dictation off
    /// included (<c>DictationController.PollInterval</c>): at the resting
    /// poll of a second the 375 ms would stretch to a second, and a 2.5 s
    /// ending to 3.5.
    /// </summary>
    public bool Active => !_disposed && (Shown.IsVisible || _deferred is not null);

    /// <summary>One show, as offered: the pill, its state for the pacer, and
    /// how <see cref="Present"/> draws it.</summary>
    private sealed record Pill(DictationHudModel Model, DictationHudPaceState Pace, bool Refusal, bool Announce, bool Meter);

    /// <summary>The state the pacer compares <paramref name="model"/> by. A
    /// listening pill by its constant name, so the clock, the countdown and
    /// the caption redrawn under it are one state and never wait; a busy
    /// pill and an ending by their line.</summary>
    private static DictationHudPaceState PaceOf(DictationHudModel model, bool meter, DictationHudPace? busy) =>
        meter
            ? new DictationHudPaceState(DictationHudPace.Listening, model.QuietName)
            : new DictationHudPaceState(busy ?? DictationHudPace.Ending, model.Text);

    /// <summary>
    /// Offer <paramref name="pill"/> to the pacer: drawn now, or held until
    /// the pill on screen has stood its <see cref="DictationHudPacer.VisibleDwell"/>
    /// and offered again then. Whatever was waiting is dropped first: this
    /// offer is newer.
    /// </summary>
    private void Offer(Pill pill)
    {
        DropDeferred();
        // What the feed now wants is a listening pill or not, even while the
        // one on screen still is: a caption for a hold that has stopped
        // listening is dropped at once.
        ShowingListening(pill.Meter);
        var decision = _pacer.Offer(pill.Pace, _delay.Now);
        if (decision.Show)
        {
            Present(pill);
            return;
        }

        _deferred = pill;
        _retry = _delay.After(decision.RetryAfter, () =>
        {
            _retry = null;
            if (_deferred is { } waiting)
            {
                _deferred = null;
                Offer(waiting);
            }
        });
    }

    /// <summary>Forget the pill waiting for its retry, if any.</summary>
    private void DropDeferred()
    {
        _deferred = null;
        var retry = _retry;
        _retry = null;
        retry?.Dispose();
    }

    /// <summary>The pacer is told the pill went, so the next one, coming up
    /// from hidden, never waits.</summary>
    private void PacedHidden() => _pacer.Offer(DictationHudPaceState.Hidden, _delay.Now);

    /// <summary>
    /// The heartbeat, from the controller's tick, once every
    /// <see cref="DictationHudReconcile.Interval"/>: what the feed drew
    /// (<see cref="Shown"/>) against what the window says it is showing
    /// (<see cref="IDictationHud.IsShowing"/>). A pill the window lost is
    /// drawn again, unannounced (it was announced, or named, when it was
    /// first shown), with its bars when it is the listening one; a pill the
    /// window kept after the feed took it down is taken away. A pill held
    /// back by the pacer is not a disagreement: its retry draws it.
    /// </summary>
    private void Heartbeat()
    {
        var now = _delay.Now;
        if (_disposed || now < _nextHeartbeat)
        {
            return;
        }

        _nextHeartbeat = now + DictationHudReconcile.Interval;
        bool showing;
        try
        {
            showing = _hud.IsShowing;
        }
        catch (Exception error)
        {
            _log.LogDebug(error, "The dictation indicator could not say whether it is showing");
            return;
        }

        switch (DictationHudReconcile.For(desiredHidden: !Shown.IsVisible, panelVisible: showing))
        {
            case DictationHudReconcile.Action.Reoffer:
                Report(DictationHudReconcile.Action.Reoffer, showing, now);
                var shown = Shown;
                if (Quietly(() => _hud.Show(shown, false)) && _metering)
                {
                    Level(_level);
                }

                return;

            case DictationHudReconcile.Action.Hide:
                Report(DictationHudReconcile.Action.Hide, showing, now);
                Quietly(_hud.Hide);
                return;
        }
    }

    /// <summary>One line per wedge, never the words: what the heartbeat did,
    /// the kind of pill wanted and what the window said. Repeated at most
    /// once a minute while the same disagreement persists.</summary>
    private void Report(DictationHudReconcile.Action action, bool showing, TimeSpan now)
    {
        if (_lastReport is { } last && last.Action == action && now - last.At < DictationHudReconcile.ReportAgainAfter)
        {
            return;
        }

        _lastReport = (action, now);
        _log.LogInformation(
            "Dictation indicator heartbeat: {Action}, wanted {Wanted}, window showing: {Showing}",
            action, Shown.IsVisible ? _pacer.Current.Kind : DictationHudPace.Hidden, showing);
    }

    /// <summary>A heal's draw: a window that throws was already reported by
    /// the draw that failed first, so a retry each second logs below the
    /// log's level.</summary>
    private bool Quietly(Action draw)
    {
        try
        {
            draw();
            return true;
        }
        catch (Exception error)
        {
            _log.LogDebug(error, "The dictation indicator could not be drawn again");
            return false;
        }
    }
}
