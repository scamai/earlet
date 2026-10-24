import Foundation

/// What the mouse trigger asks the session layer to do. Deliberately its own
/// tiny enum, not DictationHotkeyEvent: the mouse gesture has no
/// hold-vs-toggle distinction at the consumer — both collapse to start/stop.
public enum DictationMouseEvent: Sendable, Equatable {
    case start
    case stop
}

/// Pure edge/timing detector for the unified mouse hold-or-click dictation
/// gesture (button 4/5): hold it down = push-to-talk (release ends
/// dictation), a quick press-release within `clickWindow` latches dictation
/// on instead of ending it, and any subsequent press while latched stops it.
/// One button drives both gestures with zero added start latency — down
/// always emits `.start` immediately, the ambiguity is only resolved on
/// release. No CGEventTap, no timing source: this is the
/// deterministic core the tap drives from real events, and the self-check
/// exercises directly.
public struct MouseHoldOrClickState: Sendable, Equatable {

    private enum Phase: Sendable, Equatable {
        case idle
        case pressed(since: TimeInterval)
        case latched
        // The press that stops a latched session must swallow its own
        // up-event silently: that up-event has no dictation meaning (the
        // stop already fired on the down-event), but the CGEventTap still
        // needs somewhere to route it besides re-triggering the state
        // machine. Routing it through this phase lets `handle` return nil
        // for it and land back in `.idle`, instead of letting a stray
        // `default` case guess.
        case stoppingPress
    }

    public var clickWindow: TimeInterval
    private var phase: Phase = .idle
    private var trackedDown = false

    public init(clickWindow: TimeInterval = 0.3) {
        self.clickWindow = clickWindow
    }

    /// True while the machine's last processed edge was a down with no
    /// matching up yet — i.e. it is currently tracking a physical press.
    /// Exposed so a caller with independent knowledge that an event just
    /// arrived (the tap callback, deciding whether to swallow an UP) can
    /// gate on "did we actually see the matching down" without duplicating
    /// this machine's own bookkeeping. An up arriving while this is false
    /// belongs to a press this machine never tracked (e.g. one that
    /// predates the current binding) and must not be treated as this
    /// gesture's own release.
    public var isDown: Bool { trackedDown }

    /// True while a dictation session this machine started is still
    /// logically open — mid-press (push-to-talk not yet released) or
    /// latched (toggled on). Exposed so a caller that must reset the
    /// machine out from under a live press/latch (e.g.
    /// `DictationMouseButton.updateBinding` mid-hold) can tell whether the
    /// reset needs to synthesize a matching `.stop` first — resetting
    /// silently here would leave a running push-to-talk/latched session
    /// with no stop ever delivered. `.stoppingPress` is deliberately NOT
    /// engaged: its `.stop` already fired on the down that reached it: the
    /// only thing left is a release with no dictation meaning.
    public var isEngaged: Bool {
        switch phase {
        case .pressed, .latched: return true
        case .idle, .stoppingPress: return false
        }
    }

    public mutating func handle(down: Bool, timestamp: TimeInterval) -> DictationMouseEvent? {
        guard down != isDown else { return nil }   // de-dupe repeats
        trackedDown = down

        switch (phase, down) {
        case (.idle, true):
            phase = .pressed(since: timestamp)
            return .start
        case (.pressed(let since), false):
            if timestamp - since <= clickWindow {
                phase = .latched          // quick click: stay on
                return nil
            }
            phase = .idle                 // hold release: push-to-talk end
            return .stop
        case (.latched, true):
            phase = .stoppingPress
            return .stop
        case (.stoppingPress, false):
            phase = .idle
            return nil
        default:
            // Unreachable given the de-dupe guard, but a stray combination
            // must fail safe: reset rather than latch dictation open.
            phase = .idle
            return nil
        }
    }
}
