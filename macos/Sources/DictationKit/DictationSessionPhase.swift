import Foundation

/// Pure state machine for ONE dictation's lifecycle — no I/O, no clock, no
/// TCC. Exists so `DictationSession`'s sequencing logic (which calls are
/// valid in which order) is self-checkable the same way `HoldToTalkState`
/// and `DoubleTapModifierState` are: drive it with synthetic events, assert
/// the transitions, zero hardware needed.
///
/// Guards against exactly the ordering bugs a real hotkey caller could
/// trigger: a stray `triggerUp()` with no `triggerDown()` (e.g. a missed
/// key-down event), a double `triggerDown()` (e.g. a stuck-key repeat, the
/// same class of bug `HoldToTalkState`'s de-dupe already defends
/// `DictationHotkey` against one layer up), or `triggerUp()` racing a
/// still-in-flight prior `triggerUp()`.
public struct DictationSessionPhase: Sendable, Equatable {
    public enum State: Sendable, Equatable {
        case idle
        case capturing
        case stoppingCapture
        case awaitingAsr
        case awaitingInsert
        case done
        case failed
    }

    public enum Event: Sendable, Equatable {
        case triggerDown
        case triggerUp
        case captureFinished
        case asrSucceeded
        case asrFailed
        case insertSucceeded
        case insertFailed
        case reset
    }

    public enum RejectionReason: Sendable, Equatable, CustomStringConvertible {
        case wrongPhase

        public var description: String { "event not valid in the current phase" }
    }

    public private(set) var state: State = .idle

    public init() {}

    /// Returns the NEW state on a valid transition, `nil` (state unchanged)
    /// on an invalid one — mirrors `HoldToTalkState.handle`'s
    /// optional-return de-dupe convention.
    @discardableResult
    public mutating func handle(_ event: Event) -> State? {
        // `.reset` is valid from ANY state, including terminal ones —
        // it's the caller's explicit "abandon and start over" escape
        // hatch (e.g. app losing focus mid-dictation).
        if event == .reset {
            state = .idle
            return state
        }

        let next: State?
        switch (state, event) {
        case (.idle, .triggerDown):
            next = .capturing
        case (.capturing, .triggerUp):
            next = .stoppingCapture
        case (.stoppingCapture, .captureFinished):
            next = .awaitingAsr
        case (.awaitingAsr, .asrSucceeded):
            next = .awaitingInsert
        case (.awaitingAsr, .asrFailed):
            next = .failed
        case (.awaitingInsert, .insertSucceeded):
            next = .done
        case (.awaitingInsert, .insertFailed):
            next = .failed
        default:
            // Everything else (double triggerDown, triggerUp with no
            // triggerDown, an event from .done/.failed other than reset,
            // etc.) is rejected rather than silently accepted.
            next = nil
        }

        guard let next else { return nil }
        state = next
        return next
    }
}
