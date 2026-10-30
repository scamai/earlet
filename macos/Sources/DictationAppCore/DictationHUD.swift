import CoreGraphics
import Foundation
import DictationKit
import SwiftUI

/// The floating indicator's colours, from the window's palette
/// (`DictationPalette`) in its dark appearance, since the bezel is always
/// dark: the warm dark ground, the mark's gold for the level bars and the
/// hairline, and the dark inks for its words. The app icon
/// (`macos/tools/generate-icon.swift`) is drawn in the same gold.
public enum DictationBrand {
    private static let dark = DictationAppearance(dark: true)
    /// The bezel: the dark ground, nearly opaque, so the indicator reads on
    /// any wallpaper.
    public static let bezel = DictationPalette.rgb(.ground, in: dark).color.opacity(0.96)
    /// The title line and the quieter hint under it.
    public static let title = DictationPalette.rgb(.ink, in: dark).color
    public static let hint = DictationPalette.rgb(.ink2, in: dark).color
    /// The mark's gold, and its deep and bright ends.
    public static let wheat = DictationPalette.rgb(.wheat, in: dark).color
    public static let wheatDeep = DictationRGB(0xE09A14).color
    public static let wheatBright = DictationRGB(0xF7CF5A).color
    /// The level bars: gold from a deep foot to a bright tip, which at 3
    /// points wide on the dark bezel still reads as gold.
    public static let waveformGradient = LinearGradient(
        colors: [wheatDeep, wheatBright], startPoint: .bottom, endPoint: .top)
    /// The capsule's hairline: the gold at a whisper, instead of a flat
    /// white border.
    public static let strokeGradient = LinearGradient(
        colors: [wheat.opacity(0.45), wheatBright.opacity(0.2)],
        startPoint: .leading, endPoint: .trailing)
}

/// What the floating dictation HUD shows, resolved from the model's
/// published state by `DictationHUDContent.resolve` — a pure function, so
/// every state and transition is testable without AppKit. The panel
/// plumbing lives in the app target (`DictationHUDController`).
public enum DictationHUDState: Equatable, Sendable {
    /// Nothing to say: the HUD is hidden.
    case hidden
    /// A hold is recording. `latched` changes only the hint line (how to
    /// stop), never the title.
    case recording(latched: Bool)
    /// Released and the words are on their way.
    case transcribing
    /// The in-hold watchdog rebound the input mid-press. Ephemeral: the
    /// model clears the notice after a few seconds, which hides this.
    case micChanged(String)
    /// How the last dictation ended, for a moment after it (`DictationModel
    /// .hudOutcome`). Ephemeral like the mic-changed notice: the model
    /// clears it after its dwell, or when the next hold is admitted.
    case outcome(DictationHUDOutcome)
}

/// How a dictation ended, in the HUD's words: a short title and an optional
/// line under it. Separate from the status line's full sentences
/// (`DictationCopy`), which say the same thing at length in the window.
/// Never carries the dictated words.
public struct DictationHUDOutcome: Equatable, Sendable {
    public enum Tone: Equatable, Sendable {
        /// A normal ending, or one the user chose.
        case neutral
        /// Something the user should know or act on. Amber, never red.
        case caution
    }

    /// Every ending the HUD reports.
    public enum Kind: Equatable, Sendable {
        case inserted
        /// The ceiling (`seconds` long) finished the recording; its words
        /// went in, and anything said after it was not recorded.
        case stoppedAtLimit(seconds: TimeInterval)
        /// The paste was refused; the words are on the clipboard.
        case keptOnClipboard
        /// The paste was refused and the clipboard could not take the words.
        case notInserted
        /// Typing needs Accessibility (named as this Mac's System Settings
        /// names it), and the words could not be kept.
        case accessibilityNeeded
        /// A password field, at the press or at the paste.
        case passwordField
        /// Secure Input is on, at the press or at the paste.
        case secureInput
        /// The silence gate refused the clip: nothing audible was said.
        /// `speakUp` when the hold was long enough for speech and the input
        /// sent a signal (`DictationCopy.asksToSpeakUp`): the speech was too
        /// quiet. Otherwise a tap, told to hold the key.
        case nothingHeard(speakUp: Bool)
        /// Audible audio decoded to no words, and nothing explains it
        /// (`DictationNoWordsReason.unexplained`).
        case noWords
        /// The worker found the take too quiet to decode.
        case tooQuiet
        /// The language gate dropped the take; it is kept for recovery.
        case languageUnclear
        /// A loud take the worker emptied; it is kept for recovery.
        case wordsUnclear
        /// The microphone delivered no audio, or none above
        /// `DictationSession.silentInputPeak` for a whole hold. `deviceName`
        /// is the input's name as the capture reported it, nil when unknown.
        case microphoneSilent(deviceName: String?)
        /// Microphone access is off.
        case microphoneBlocked
        /// Escape or Cancel.
        case cancelled
        /// The speech engine died or stopped answering; it is restarting.
        case engineRestarting
        /// The speech engine could not start for this dictation.
        case engineUnavailable
        /// The engine ran and could not transcribe the clip.
        case engineError
        /// A press landed while the last dictation was still finishing, so
        /// it was not recorded.
        case stillFinishing
        /// A spoken command ran instead of inserting text.
        case commandDone
        case textRecoverable
        case resultCopied
        case resultCopyFailed
        case vocabularyLearned
    }

    public var title: String
    public var detail: String?
    public var tone: Tone
    /// How long the HUD shows it before it hides on its own.
    public var dwell: TimeInterval

    public init(title: String, detail: String?, tone: Tone, dwell: TimeInterval) {
        self.title = title
        self.detail = detail
        self.tone = tone
        self.dwell = dwell
    }
}

/// Why audible audio came back with no words, from the worker's reason
/// (`DictationAsrDropReason`) and the take's peak, and so whether the take
/// is kept for Recover Last Dictation (DESIGN.md, rule zero). A take a
/// recognition gate emptied is kept, and its recovery skips those gates; a
/// quiet one, or one nothing explains, is not.
public enum DictationNoWordsReason: Equatable, Sendable {
    /// The worker's silence pre-check: nothing loud enough to decode.
    case tooQuiet
    /// The language-probability gate. Kept: with the language chosen in
    /// Settings, the recovery's decode runs no language gate at all.
    case languageUnclear
    /// The junk filter or another filter emptied a take at least as loud
    /// as `loudPeak`. Kept.
    case wordsUnclear
    /// A quieter take the junk filter or another filter emptied, or no
    /// reason given (an older worker). Not kept.
    case unexplained

    /// The take's peak from which an emptied take is kept as speech rather
    /// than noise. Ordinary speech's chunk peaks run about 0.05–0.09 (the
    /// waveform's note below), so a take that never reaches this was quiet
    /// throughout.
    public static let loudPeak: Float = 0.1

    public init(dropReason: DictationAsrDropReason?, capturePeak: Float?) {
        switch dropReason {
        case .quiet: self = .tooQuiet
        case .language: self = .languageUnclear
        case .junk, .other: self = (capturePeak ?? 0) >= Self.loudPeak ? .wordsUnclear : .unexplained
        case nil: self = .unexplained
        }
    }

    /// Whether the take is kept for Recover Last Dictation.
    public var keepsTake: Bool { self == .languageUnclear || self == .wordsUnclear }
}

/// The state → content mapping. No timers, no AppKit: the mic-changed
/// notice arrives already bounded (the model owns its expiry), so the
/// mapping itself is a function of the four published values.
public enum DictationHUDContent {
    /// Priority: an interruption outranks everything (a hold was just
    /// discarded), then the live hold, then an outcome, then the
    /// transcription that follows a hold. `recording` and `transcribing` are
    /// both true only for the instant of a release
    /// (`DictationModel.transcribing`'s doc): recording wins there, so the
    /// capsule goes straight from Listening to Transcribing and never
    /// through hidden.
    ///
    /// An outcome outranks transcribing because the only one that can be
    /// set while a release is still under way is a refused press: the user
    /// is holding the key and speaking, and must see that it is not
    /// recording. Every other outcome is set in the same turn that ends the
    /// release, and the next admitted hold clears it.
    public static func resolve(
        recording: Bool, transcribing: Bool, latched: Bool, micChangedNotice: String?,
        outcome: DictationHUDOutcome? = nil
    ) -> DictationHUDState {
        if let micChangedNotice { return .micChanged(micChangedNotice) }
        if recording { return .recording(latched: latched) }
        if let outcome { return .outcome(outcome) }
        if transcribing { return .transcribing }
        return .hidden
    }

    /// The tier-1 line, or nil when hidden. While recording, a non-empty
    /// live preview replaces "Listening" — the app's copy policy: the user's
    /// own words are the strongest possible "it is working" signal. The
    /// preview is display-only; it is never inserted.
    public static func title(for state: DictationHUDState, livePreviewText: String = "") -> String? {
        switch state {
        case .hidden: return nil
        case .recording: return livePreviewText.isEmpty ? "Listening" : livePreviewText
        case .transcribing: return "Transcribing…"
        case .micChanged(let message): return message
        case .outcome(let outcome): return outcome.title
        }
    }

    /// The quieter tier-2 hint, or nil for states that have none.
    public static func hint(for state: DictationHUDState, holdKeyName: String) -> String? {
        switch state {
        case .hidden, .micChanged: return nil
        // Escape still cancels until the paste begins.
        case .transcribing: return "Esc to cancel"
        case .recording(latched: false): return "Release to insert · Esc to cancel"
        case .recording(latched: true): return "Press \(holdKeyName), Space or Return to stop"
        case .outcome(let outcome): return outcome.detail
        }
    }

    // ─── Time in a long hold ───

    /// How long before the ceiling the hint starts counting down.
    public static let countdownSeconds = DictationHoldPolicy.stage1.countdownSeconds

    /// The recording hint with time in it. A latched hold shows how long it
    /// has run (hands-free, nothing else tells the user); any hold counts
    /// down in its last `countdownSeconds`, so a long dictation never just
    /// stops. Other states, and a hold with no clock yet, read as
    /// `hint(for:holdKeyName:)`.
    public static func hint(
        for state: DictationHUDState, holdKeyName: String,
        elapsed: TimeInterval?, limit: TimeInterval
    ) -> String? {
        guard case .recording(let latched) = state, let elapsed else {
            return hint(for: state, holdKeyName: holdKeyName)
        }
        let remaining = limit - elapsed
        if remaining <= countdownSeconds {
            let stop = latched ? "Press \(holdKeyName), Space or Return to stop" : "Release to insert"
            return "Stops in \(clock(remaining.rounded(.up))) · \(stop)"
        }
        guard latched else { return hint(for: state, holdKeyName: holdKeyName) }
        return "\(clock(elapsed)) · Press \(holdKeyName), Space or Return to stop"
    }

    /// Whole seconds as m:ss, floored ("1:05"); never negative.
    public static func clock(_ seconds: TimeInterval) -> String {
        let whole = max(0, Int(seconds.rounded(.down)))
        return "\(whole / 60):" + (whole % 60 < 10 ? "0" : "") + "\(whole % 60)"
    }

    // ─── Escape's question ───

    /// The title while Escape's question stands on a long take: how much
    /// of the hold a second Escape throws away, on the hold's own clock.
    public static func discardQuestion(seconds: TimeInterval) -> String {
        "Discard \(clock(seconds)) of speech?"
    }

    /// The question's hint: what each answer is.
    public static let discardQuestionHint = "Press Esc again to discard. Keep talking to continue."

    // ─── Outcomes ───

    /// The HUD's words for each ending. Use concise status copy: say
    /// what happened and what to do, in a glance. The clipboard case is the
    /// one that matters most — a user who believes the words are gone
    /// dictates them again, so it says where they are and names the key.
    /// Dwell times are product policy; a receipt is brief, a refusal stays long
    /// enough to read and act on. `accessibilitySettingsName` is what System
    /// Settings calls the Accessibility pane on this Mac.
    public static func outcome(
        for kind: DictationHUDOutcome.Kind, holdKeyName: String,
        accessibilitySettingsName: String = DictationPermissionStep.accessibilitySettingsName
    ) -> DictationHUDOutcome {
        typealias Outcome = DictationHUDOutcome
        switch kind {
        case .inserted:
            return Outcome(title: "Inserted", detail: nil, tone: .neutral, dwell: 0.6)
        case .textRecoverable:
            return Outcome(title: "Your words are still here",
                           detail: "Click Earlet's menu bar icon → Copy last text.", tone: .caution, dwell: 5)
        case .resultCopied:
            return Outcome(title: "Copied", detail: "Press ⌘V in a text field.", tone: .neutral, dwell: 2)
        case .resultCopyFailed:
            return Outcome(title: "Couldn’t copy", detail: "Your text is still in Earlet. Try again.",
                           tone: .caution, dwell: 4)
        case .vocabularyLearned:
            return Outcome(title: "Remembered your correction",
                           detail: "Review or undo from Earlet's menu bar icon.", tone: .neutral, dwell: 3)
        case .stoppedAtLimit(let seconds):
            return Outcome(title: "Stopped at \(clock(seconds))",
                           detail: "Your words went in. Hold \(holdKeyName) again to keep going.",
                           tone: .caution, dwell: 3)
        case .keptOnClipboard:
            return Outcome(title: "Couldn't type here",
                           detail: "Your words are on the clipboard — press ⌘V.",
                           tone: .caution, dwell: 4)
        case .notInserted:
            return Outcome(title: "Couldn't type or copy",
                           detail: "Nothing was inserted. Click a text field and try again.",
                           tone: .caution, dwell: 4)
        case .accessibilityNeeded:
            return Outcome(title: "Earlet can't type yet",
                           detail: "Allow Earlet in Privacy & Security → \(accessibilitySettingsName), then try again.",
                           tone: .caution, dwell: 4.5)
        case .passwordField:
            return Outcome(title: "That's a password field",
                           detail: "Earlet didn't type it, and didn't keep it.",
                           tone: .caution, dwell: 4.5)
        case .secureInput:
            return Outcome(title: "Another app is blocking the keyboard",
                           detail: "A password field usually causes this — click a normal text field.",
                           tone: .caution, dwell: 4.5)
        case .nothingHeard(let speakUp):
            // Not an error: the softest copy, and no caution accent. A tap
            // is told to hold the key; a hold long enough for speech heard
            // none loud enough to pass the gate.
            return Outcome(title: "Nothing heard",
                           detail: speakUp ? "Speak up, or move closer to the microphone."
                               : "Hold \(holdKeyName) while you speak.",
                           tone: .neutral, dwell: 1.6)
        case .noWords:
            return Outcome(title: "No words recognized", detail: "Try again.",
                           tone: .neutral, dwell: 1.6)
        case .tooQuiet:
            return Outcome(title: "Too quiet to make out",
                           detail: "Speak up, or move closer to the microphone.",
                           tone: .neutral, dwell: 2.5)
        case .languageUnclear:
            return Outcome(title: "Couldn't tell the language",
                           detail: "Recording kept. Choose your language in Settings, then recover it.",
                           tone: .caution, dwell: 4.5)
        case .wordsUnclear:
            return Outcome(title: "Couldn't make out the words",
                           detail: "Recording kept. Recover it from Earlet's menu bar icon.",
                           tone: .caution, dwell: 4)
        case .microphoneSilent(let deviceName):
            return Outcome(title: "No sound from \(DictationCopy.inputName(deviceName) ?? "the microphone")",
                           detail: "Check it isn't muted, or pick another input in Sound settings.",
                           tone: .caution, dwell: 3.5)
        case .microphoneBlocked:
            return Outcome(title: "Microphone access is off",
                           detail: "Allow it in System Settings → Privacy & Security.",
                           tone: .caution, dwell: 4.5)
        case .cancelled:
            return Outcome(title: "Cancelled", detail: "Nothing was inserted.",
                           tone: .neutral, dwell: 1)
        case .engineRestarting:
            return Outcome(title: "Speech engine restarting",
                           detail: "Nothing was inserted. Try again in a moment.",
                           tone: .caution, dwell: 3.5)
        case .engineUnavailable:
            return Outcome(title: "Speech engine didn't start",
                           detail: "Nothing was inserted. Try again in a moment.",
                           tone: .caution, dwell: 3.5)
        case .engineError:
            return Outcome(title: "That didn't come through",
                           detail: "Nothing was inserted. Try again.",
                           tone: .caution, dwell: 3.5)
        case .stillFinishing:
            return Outcome(title: "Still finishing the last one",
                           detail: "Hold \(holdKeyName) again to dictate.",
                           tone: .caution, dwell: 2.5)
        case .commandDone:
            return Outcome(title: "Done", detail: nil, tone: .neutral, dwell: 1)
        }
    }

    /// The ending a failed dictation reports. Mirrors `DictationCopy.failure`,
    /// which says the same at length. `heldSeconds` is how long the capture
    /// recorded, `peak` its loudest sample, and `deviceName` the input it
    /// recorded from.
    public static func ending(
        for failure: DictationSession.SessionFailure, stage: DictationSession.Stage,
        heldSeconds: TimeInterval = 0, peak: Float? = nil, deviceName: String? = nil
    ) -> DictationHUDOutcome.Kind {
        switch failure {
        case .micPermissionDenied: return .microphoneBlocked
        case .emptyCapture: return .microphoneSilent(deviceName: deviceName)
        case .emptyTranscript:
            return stage == .capture
                ? .nothingHeard(speakUp: DictationCopy.asksToSpeakUp(heldSeconds: heldSeconds, peak: peak))
                : .noWords
        case .asr(.engineError): return .engineError
        case .asr(.ioError), .asr(.notInitialized), .asr(.workerUnavailable): return .engineRestarting
        case .insert(let failure): return ending(forRefusedPaste: failure, keptOnClipboard: false)
        case .invalidSequencing: return .stillFinishing
        }
    }

    /// The ending for audible audio that came back with no words. The two
    /// that promise a kept recording fall back to `noWords` when it could
    /// not be kept.
    public static func ending(forNoWords reason: DictationNoWordsReason, kept: Bool) -> DictationHUDOutcome.Kind {
        switch reason {
        case .tooQuiet: return .tooQuiet
        case .languageUnclear: return kept ? .languageUnclear : .noWords
        case .wordsUnclear: return kept ? .wordsUnclear : .noWords
        case .unexplained: return .noWords
        }
    }

    /// The ending for a refused paste, or a press the privacy gate refused.
    public static func ending(
        forRefusedPaste failure: DictationInsertFailure, keptOnClipboard: Bool
    ) -> DictationHUDOutcome.Kind {
        switch failure {
        case .secureFieldFocused: return .passwordField
        case .secureInputActive: return .secureInput
        case .accessibilityPermissionMissing:
            return keptOnClipboard ? .keptOnClipboard : .accessibilityNeeded
        case .focusedElementNotEditable, .noFocusedElement, .pasteNeverRequested, .keyEventPostFailed:
            return keptOnClipboard ? .keptOnClipboard : .notInserted
        }
    }

    // ─── Waveform geometry ───

    public static let waveformBarCount = 3
    public static let waveformMinHeight: CGFloat = 3
    public static let waveformMaxHeight: CGFloat = 13

    /// The perceptual window for the waveform, in dB. Real speech peaks are
    /// ~0.05–0.09 (capture forensics), i.e. -26…-21 dB; a linear 0…1 map
    /// puts them at ~4 pt against a 3 pt floor — invisible. The dB window
    /// puts ordinary speech at 50–65% of the bar range instead.
    ///
    /// The amplitude→dB convention is the capture stack's own
    /// (`InputGain.dB(fromLinear:)`: 20·log10, amplitude not power).
    public static let levelFloorDB: Float = -45
    public static let levelCeilingDB: Float = -8

    /// A peak amplitude (0…1) mapped into the perceptual 0…1 window,
    /// clamped: digital silence and anything under `levelFloorDB` is 0,
    /// anything over `levelCeilingDB` (a shout, clipping) is 1.
    public static func normalizedLevel(peak: Float) -> Float {
        guard peak > 0 else { return 0 }
        let dB = 20 * log10f(peak)
        return min(1, max(0, (dB - levelFloorDB) / (levelCeilingDB - levelFloorDB)))
    }

    /// The three bar heights, in points, for a PERCEPTUAL level (0…1, see
    /// `normalizedLevel`). Pure, so the level → shape mapping is testable.
    ///
    /// **Silence is a flat floor.** Zero level means every bar at minimum
    /// height with no pulse and no breathing: a quiet room wiggling reads
    /// as a bug, not as life. Above the floor the bars hold a fixed wave
    /// silhouette around the level, which moves only when the level moves.
    public static func barHeights(level: Float) -> [CGFloat] {
        let clamped = CGFloat(min(1, max(0, level)))
        guard clamped > 0 else {
            return Array(repeating: waveformMinHeight, count: waveformBarCount)
        }
        let factors: [CGFloat] = [1.0, 0.62, 0.85]
        let levelHeight = waveformMinHeight + (waveformMaxHeight - waveformMinHeight) * clamped
        return factors.map { waveformMinHeight + (levelHeight - waveformMinHeight) * $0 }
    }

    /// One easing step toward a target level — the per-bar temporal
    /// smoothing. At the level cadence (~12 Hz, a chunk every ~85 ms) a
    /// factor of 0.7 lands within ~9% of a step change in two chunks,
    /// ≈170 ms: bars ease instead of jumping, and all motion still comes
    /// from the level itself.
    public static func easedLevel(current: Float, target: Float) -> Float {
        current + (target - current) * 0.7
    }

    // ─── Frozen layout while recording ───

    /// The caption budget as reference lines. The recording state's title
    /// area is laid out ONCE for a line this wide and then FROZEN for the
    /// whole hold: a live preview that changes length with every hypothesis
    /// head-truncates inside the fixed area and the capsule never relayouts.
    /// Width may change only between distinct states (recording →
    /// transcribing → mic-changed → hidden), with the standard ease.
    public static let recordingTitleReferenceCJK =
        String(repeating: "你", count: DictationPreviewCaption.defaultCJKCharacterBudget)
    public static let recordingTitleReferenceWords = "one two three four five six seven"
}

/// Anti-flap pacing for the HUD's visible states: a visible state must
/// dwell for `visibleDwellSeconds` before a DIFFERENT visible state may
/// replace it. Hidden is always allowed immediately; micChanged and an
/// outcome always pre-empt (both are what the user must see next, and an
/// outcome has its own dwell); a new hold replaces an outcome at once (the
/// user has moved on); and a change within the same case (a hold becoming
/// latched) is immediate. Pure and clock-injected, so the whole policy is
/// unit-testable; the controller owns the retry.
public struct DictationHUDPacer: Sendable {
    public static let visibleDwellSeconds: TimeInterval = 0.375

    public private(set) var current: DictationHUDState = .hidden
    private var currentSince: TimeInterval = 0

    public init() {}

    public enum Decision: Equatable, Sendable {
        /// Show this state now.
        case show(DictationHUDState)
        /// Stay on the current state; offer the desired one again after
        /// this many seconds.
        case hold(retryAfter: TimeInterval)
    }

    public mutating func offer(_ desired: DictationHUDState, at now: TimeInterval) -> Decision {
        func accept(_ state: DictationHUDState) -> Decision {
            if state != current { currentSince = now }
            current = state
            return .show(state)
        }
        if desired == .hidden { return accept(desired) }
        if case .micChanged = desired { return accept(desired) }
        if case .outcome = desired { return accept(desired) }
        if desired == current { return accept(desired) }
        if case .hidden = current { return accept(desired) }
        if case .recording = current, case .recording = desired { return accept(desired) }
        if case .outcome = current, case .recording = desired { return accept(desired) }
        let elapsed = now - currentSince
        guard elapsed >= Self.visibleDwellSeconds else {
            return .hold(retryAfter: Self.visibleDwellSeconds - elapsed)
        }
        return accept(desired)
    }
}
