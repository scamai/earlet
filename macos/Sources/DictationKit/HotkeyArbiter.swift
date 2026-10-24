import CoreGraphics
import Foundation

/// The two hold gestures that ride one event tap.
///
/// An enum rather than two booleans, and that is the entire non-collision
/// argument: ``HotkeyArbiter/owner(for:)`` returns *one* of these or none,
/// so "the workflow key and the dictation key both fired on one gesture" is
/// not a state this type can produce. On Windows the same property is
/// bought by the two features using two different Win32 mechanisms —
/// `RegisterHotKey` needs a non-modifier key, a `WH_KEYBOARD_LL` hold does
/// not — so neither can be reached by the other's gesture, *"by construction
/// rather than by inspection"*. macOS has no such split available: both
/// gestures are modifier holds, both must be read off `flagsChanged`, and
/// `CGEventFlags.contains(_:)` is a **superset** test — so holding ⌘⌥ also
/// satisfies a binding of ⌥, and two independent detectors would genuinely
/// both fire. The construction has to be built rather than inherited, and
/// this is it: one owner, chosen once per gesture.
public enum HotkeyGesture: String, Sendable, Equatable, CaseIterable {
    /// Dictation's push-to-talk. The shipped default is hold ⌥.
    case dictation
    /// Voice workflows' push-to-talk. The shipped default is hold ⌘⌥.
    case workflows
}

extension HotkeyBinding {
    /// The shipped voice-workflows trigger: **hold ⌘⌥**.
    ///
    /// Chosen, not inherited. Windows pairs its Hold-Right-Alt dictation
    /// with Ctrl+Alt+Space for workflows; the macOS-shaped translation of
    /// "the modifier pair nobody's fingers rest on" is ⌘⌥, and the three
    /// candidates were weighed rather than assumed:
    ///
    ///  - **⌃⌥ was the closer literal translation and is wrong here.**
    ///    Dictation's *toggle* is double-tap ⌃ (``defaultDoubleTap``), and
    ///    that detector reads the raw flags with no arbiter in front of it —
    ///    two quick ⌃⌥ presses would toggle dictation on. That is a real
    ///    collision, it is asserted against in ``HotkeyArbiter/conflict(hold:doubleTap:workflows:)``,
    ///    and it is the reason this is not the obvious chord.
    ///  - **⇧⌥ types special characters.** Holding it is something people do
    ///    on purpose, mid-word.
    ///  - **⌘⌥ is held only in combination with a letter**, and only for as
    ///    long as that shortcut takes. Held alone for the length of a
    ///    sentence it means nothing to macOS or to any common app.
    ///
    /// It is a strict superset of ``defaultHold`` (⌥) on purpose rather than
    /// by accident: the arbiter resolves a superset to the more specific
    /// binding, so ⌘⌥ is a workflow and ⌥ is dictation, and there is no
    /// third key to learn. The cost is stated where the user can see it —
    /// see ``workflowCaution``.
    public static let defaultWorkflows = HotkeyBinding(
        modifiers: [.command, .option], activation: .hold)

    /// What a user should know before this binding is armed, or `nil` when
    /// there is nothing to say. Same job as ``caution``, which only speaks
    /// about single-modifier bindings.
    public var workflowCaution: String? {
        guard modifiers == HotkeyBinding.defaultWorkflows.modifiers else { return nil }
        return "⌘⌥ is half of shortcuts like ⌘⌥D and ⌘⌥Esc. The app watches the keys, it does "
            + "not swallow them, so those still work — but each one is also a very short "
            + "workflow hold, which comes back as \u{201C}I didn't catch that\u{201D}."
    }
}

/// Decides which of the registered hold gestures owns the keyboard right
/// now — and grants it to **at most one**.
///
/// ## The two rules
///
/// **Most specific wins.** A binding of ⌘⌥ and a binding of ⌥ are both
/// satisfied while ⌘⌥ is down, because `CGEventFlags.contains(_:)` is a
/// superset test. Handing it to the one with more modifiers is the only
/// answer that leaves both reachable: the alternative — first registered
/// wins — makes the longer binding unreachable, and does it silently, which
/// is the same shape of bug `TriggerMatcher`'s longest-phrase rule exists to
/// stop one layer up.
///
/// **The owner is sticky until it is released.** Once a gesture owns the
/// keyboard it keeps it until the flags stop satisfying *its* mask, even if
/// a more specific binding becomes satisfied in the meantime. A user who
/// starts dictating on ⌥ and then happens to press ⌘ mid-sentence must not
/// have their dictation handed to workflows halfway through the sentence —
/// the audio is already being captured for one of them, and there is no
/// sensible way to split it.
///
/// Both rules are pure arithmetic on option sets: no tap, no TCC, no clock.
/// That is deliberate, and it is what lets the non-collision be *proved* by
/// `runWorkflowGestureSelfCheck()` on any Mac rather than asserted in a
/// comment.
public struct HotkeyArbiter: Sendable, Equatable {

    /// One registered gesture.
    public struct Candidate: Sendable, Equatable {
        public let gesture: HotkeyGesture
        public let mask: CGEventFlags
        /// How many modifiers the binding names — the specificity used to
        /// break a tie between two satisfied candidates. Read off the mask
        /// rather than stored alongside it, so the two cannot disagree.
        var specificity: Int { mask.rawValue.nonzeroBitCount }

        /// The physical keys this candidate accepts, or empty for any.
        ///
        /// Checked ONLY when ownership is acquired, never while it is held —
        /// see `owner(for:keyCode:)`. A binding that names no side leaves this
        /// empty and behaves exactly as it did before sides existed.
        public let keyCodes: Set<Int64>

        public init(gesture: HotkeyGesture, mask: CGEventFlags, keyCodes: Set<Int64> = []) {
            self.gesture = gesture
            self.mask = mask
            self.keyCodes = keyCodes
        }
    }

    private var candidates: [Candidate]
    private var current: HotkeyGesture?

    /// - Parameter candidates: the gestures to arbitrate between. An empty
    ///   mask is dropped rather than registered: `CGEventFlags()` is the
    ///   empty set and `contains` on it is true for every event, so an unset
    ///   binding would own the keyboard permanently. `HotkeyBinding.rejection`
    ///   blocks that at the UI and this drops it again, because the failure
    ///   is unrecoverable from the user's side.
    public init(candidates: [Candidate] = []) {
        self.candidates = candidates.filter { !$0.mask.isEmpty }
    }

    /// The gestures this arbiter is actually arbitrating between.
    ///
    /// Exposed so a wiring check can assert what was *registered* rather
    /// than what somebody meant to register — with the workflows flag off
    /// there must be exactly one candidate here, and "no object to run one"
    /// is checkable rather than declared.
    public var registered: [HotkeyGesture] { candidates.map(\.gesture) }

    /// Replace the registered set. Resets the owner: a binding changed while
    /// a key is physically held would otherwise leave a gesture believing it
    /// still owns a mask nobody is holding.
    public mutating func setCandidates(_ candidates: [Candidate]) {
        self.candidates = candidates.filter { !$0.mask.isEmpty }
        current = nil
    }

    /// Forget the current owner — used when the tap is torn down, for the
    /// same reason `DictationHotkey.stop()` rebuilds its state machines.
    public mutating func reset() { current = nil }

    /// Who owns the keyboard at these flags, or `nil` for nobody.
    ///
    /// The single return value is the safety property. A caller drives every
    /// registered gesture's state machine from this one answer, so no
    /// sequence of events can put two of them down at once.
    /// - Parameter keyCode: the key that just changed, from the same
    ///   `flagsChanged` event the flags came from. `nil` when the caller has
    ///   none, which makes every side check pass — a caller without a keycode
    ///   gets the pre-sides behaviour rather than a gesture that can never
    ///   fire.
    public mutating func owner(
        for flags: CGEventFlags, keyCode: Int64? = nil
    ) -> HotkeyGesture? {
        // Sticky: the gesture that started still owns it while its own mask
        // is satisfied.
        //
        // NO SIDE CHECK HERE, deliberately. `flagsChanged` names the key that
        // just changed, so on the second event of a hold — the user pressing
        // some other modifier, or a Caps Lock light going on — the keycode is
        // that other key's, not the Option still being held. Re-testing the
        // side would tear down a live recording mid-sentence. Which physical
        // key started the hold is decided once, below, and the mask keeps it.
        if let current, candidates.contains(where: { $0.gesture == current && flags.contains($0.mask) }) {
            return current
        }

        var best: Candidate?
        for candidate in candidates where flags.contains(candidate.mask) {
            // The side, at the only moment it is knowable.
            //
            // A NIL keycode passes. It means the caller had no event to read
            // one from — a self-check driving flags directly, or any path that
            // predates sides — and such a caller must get the pre-sides
            // behaviour. Skipping the candidate instead is what the first cut
            // of this did, and `WorkflowGestureSelfCheck` caught it in one
            // run: with dictation unable to take ownership, the ⌘⌥ workflows
            // chord won a hold that ⌥ alone had already started.
            if !candidate.keyCodes.isEmpty, let keyCode,
               !candidate.keyCodes.contains(keyCode) {
                continue
            }
            guard let incumbent = best else { best = candidate; continue }
            if candidate.specificity > incumbent.specificity { best = candidate }
        }

        current = best?.gesture
        return current
    }

    // ─── the checkable non-collision rule ───

    /// Why these three bindings cannot ship together, or `nil` when they
    /// can.
    ///
    /// This is the "provable by a test, not by inspection" half. The arbiter
    /// above makes the two *hold* gestures mutually exclusive by
    /// construction; what it cannot cover is the double-tap detector, which
    /// runs off the same `flagsChanged` stream with no arbiter in front of
    /// it (it is a temporal gesture, not a held one, and folding it in would
    /// mean a hold could swallow a toggle). So the rule for the toggle is
    /// stated here and checked instead: **the workflow binding must share no
    /// modifier with it.** Two quick presses of a workflow chord that
    /// contained the toggle's modifier would latch dictation on, and the
    /// user would have performed one gesture and got two features.
    ///
    /// - Parameters:
    ///   - hold: dictation's push-to-talk binding.
    ///   - doubleTap: dictation's toggle binding.
    ///   - workflows: the voice-workflows binding.
    public static func conflict(
        hold: HotkeyBinding, doubleTap: HotkeyBinding, workflows: HotkeyBinding
    ) -> String? {
        if workflows.modifiers.isEmpty {
            return "the workflow binding names no modifiers, which would match every event"
        }
        if workflows.modifiers == hold.modifiers {
            return "the workflow binding (\(workflows.symbolString)) is dictation's hold key; "
                + "one gesture cannot start two features"
        }
        let shared = workflows.modifiers.intersection(doubleTap.modifiers)
        if !shared.isEmpty {
            let names = shared.sorted { $0.sortOrder < $1.sortOrder }.map(\.displayName)
            return "the workflow binding (\(workflows.symbolString)) shares \(names.joined(separator: ", ")) "
                + "with dictation's double-tap toggle (\(doubleTap.symbolString)); two quick "
                + "workflow holds would latch dictation on"
        }
        return nil
    }
}

/// The arbiter plus one edge detector per gesture — the exact fan-out
/// `DictationHotkey` performs for every `flagsChanged` event, lifted out of
/// the tap so it can be driven with synthetic flags.
///
/// It is here rather than inside `DictationHotkey` for one reason: the
/// property that matters ("one gesture never starts two features") is a
/// property of this fan-out, not of the arbiter alone, and a check that
/// exercised only the arbiter would be checking half of it. Everything the
/// tap does with an event after reading `event.flags` is in
/// ``handle(flags:)``, so the check drives the same code the keyboard does.
public struct HotkeyGestureRouter: Sendable {

    /// What one `flagsChanged` produced — at most one event per gesture, and
    /// usually none at all.
    public struct Emission: Sendable, Equatable {
        public let dictation: DictationHotkeyEvent?
        public let workflows: DictationHotkeyEvent?

        public init(dictation: DictationHotkeyEvent?, workflows: DictationHotkeyEvent?) {
            self.dictation = dictation
            self.workflows = workflows
        }

        public static let none = Emission(dictation: nil, workflows: nil)
    }

    private var arbiter: HotkeyArbiter
    private var dictationState = HoldToTalkState()
    private var workflowState = HoldToTalkState()
    /// Which gestures are currently down, derived from the events actually
    /// emitted rather than from the arbiter's own answer.
    ///
    /// A `Set` and not an `Optional`, deliberately: an Optional would encode
    /// the conclusion under test. This can hold two, and a check that drives
    /// every reachable flag combination and never sees it hold two has
    /// *observed* the exclusion instead of restating it.
    public private(set) var held: Set<HotkeyGesture> = []

    public init(arbiter: HotkeyArbiter) { self.arbiter = arbiter }

    public init(config candidates: [HotkeyArbiter.Candidate]) {
        self.arbiter = HotkeyArbiter(candidates: candidates)
    }

    /// The gestures this router can ever emit for. With the workflows flag
    /// off this is `[.dictation]` and nothing else.
    public var registered: [HotkeyGesture] { arbiter.registered }

    public mutating func handle(
        flags: CGEventFlags, keyCode: Int64? = nil
    ) -> Emission {
        let owner = arbiter.owner(for: flags, keyCode: keyCode)
        let dictation = dictationState.handle(down: owner == .dictation)
        let workflows = workflowState.handle(down: owner == .workflows)
        apply(dictation, to: .dictation)
        apply(workflows, to: .workflows)
        return Emission(dictation: dictation, workflows: workflows)
    }

    public mutating func reset() {
        arbiter.reset()
        dictationState = HoldToTalkState()
        workflowState = HoldToTalkState()
        held.removeAll()
    }

    private mutating func apply(_ event: DictationHotkeyEvent?, to gesture: HotkeyGesture) {
        switch event {
        case .pushToTalkStart: held.insert(gesture)
        case .pushToTalkEnd: held.remove(gesture)
        default: break
        }
    }
}
