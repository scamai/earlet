import Foundation

/// Events a trigger detector can emit. `DictationHotkey` fans these out to
/// closures; the app controller starts/stops capture off
/// them. Kept as one shared enum so hold-to-talk and double-tap-toggle
/// consumers can share dispatch code.
public enum DictationHotkeyEvent: Sendable, Equatable {
    case pushToTalkStart
    case pushToTalkEnd
    case doubleTapActivate
    case doubleTapDeactivate
}

/// Pure edge detector for ONE modifier key's hold-to-talk behavior: down
/// starts, up ends. No timing, no TCC, no CGEventTap — this is the
/// deterministic core `DictationHotkey` drives from real `flagsChanged`
/// events, and the self-check exercises directly.
///
/// De-dupes repeat callbacks: `flagsChanged` can fire more than once for the
/// same logical key state (e.g. a stuck-key repeat quirk some keyboards
/// exhibit), and firing `pushToTalkStart` twice without an intervening `End`
/// would double-open the ASR capture session downstream.
public struct HoldToTalkState: Sendable, Equatable {
    private var isDown = false

    public init() {}

    public mutating func handle(down: Bool) -> DictationHotkeyEvent? {
        guard down != isDown else { return nil }
        isDown = down
        return down ? .pushToTalkStart : .pushToTalkEnd
    }
}

/// Pure edge detector for the double-tap-a-modifier toggle. The window is
/// measured between the timestamps of consecutive `flagsChanged` events, not
/// through a Carbon hotkey, and the detector is independent of
/// `HoldToTalkState`, with its own configured modifier mask, because the
/// toggle and the hold need not be bound to the same key.
///
/// Only the PRESS edge matters for timing (press to press); releases are
/// ignored entirely. A pair of
/// presses within `window` seconds toggles dictation on, and a later pair
/// toggles it back off. This deliberately mirrors Windows' double-tap
/// interaction: a lone press never changes a continuous dictation session.
public struct DoubleTapModifierState: Sendable, Equatable {
    public var window: TimeInterval
    private var isDown = false
    private var lastPressAt: TimeInterval?
    private var latched = false

    public init(window: TimeInterval = 0.35) {
        self.window = window
    }

    public mutating func handle(down: Bool, timestamp: TimeInterval) -> DictationHotkeyEvent? {
        guard down != isDown else { return nil }
        isDown = down
        guard down else { return nil }

        // A reversed timestamp can occur in synthetic inputs or after a
        // clock source reset. It must not count as a negative-duration
        // double-tap; treat this edge as a fresh first press instead.
        let isDoubleTap = lastPressAt.map {
            let interval = timestamp - $0
            return interval >= 0 && interval <= window
        } ?? false
        guard isDoubleTap else {
            // First tap of a potential pair — remember it so a tap within
            // `window` completes the double-tap.
            lastPressAt = timestamp
            return nil
        }

        // The pair completed; clear its first edge so the second edge cannot
        // become the first half of a phantom pair. This is especially
        // important when this pair stops dictation: the next tap must begin
        // a wholly new pair before it can restart capture.
        lastPressAt = nil
        latched.toggle()
        return latched ? .doubleTapActivate : .doubleTapDeactivate
    }
}
