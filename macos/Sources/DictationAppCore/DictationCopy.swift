import Foundation
import DictationKit

/// The status line's sentences for a dictation that did not insert.
///
/// Every `SessionFailure` maps to a fixed sentence here, so no enum name,
/// worker message or path reaches the user (DESIGN.md: no protocol field or
/// internal detail in normal UI copy). The raw classification goes to the
/// log instead, where it can be diagnosed.
public enum DictationCopy {

    public static func failure(
        _ failure: DictationSession.SessionFailure,
        stage: DictationSession.Stage,
        holdKey: DictationHoldKey
    ) -> String {
        switch failure {
        case .micPermissionDenied:
            return "Earlet cannot use the microphone. Allow it in System Settings → Privacy & Security → Microphone."
        case .emptyCapture:
            return "The microphone delivered no audio. Check the input device in System Settings → Sound, then try again."
        case .emptyTranscript:
            // At capture the silence gate refused the clip; at ASR audible
            // audio decoded to nothing. Different causes, different advice.
            return stage == .capture
                ? "Nothing was heard. Hold \(holdKey.name) while you speak, then release."
                : "No words were recognized. Try again, or say a shorter phrase."
        case .asr(let asrFailure):
            switch asrFailure {
            case .engineError:
                return "The speech engine could not transcribe that. Nothing was inserted; try again."
            case .ioError, .notInitialized, .workerUnavailable:
                return workerStopped
            }
        case .insert(let insertFailure):
            return insert(insertFailure, keptOnClipboard: false)
        case .invalidSequencing:
            return "That press landed while the last dictation was still finishing. Nothing was lost; hold \(holdKey.name) again."
        }
    }

    /// The idle status before dictation is enabled, once every grant is in.
    public static let readyToEnable = "Ready to enable."

    /// The idle status before dictation is enabled while a grant is still
    /// missing: what to do, not a "ready" that is not true yet. Says no
    /// "above" or "below", because the same line is the menu's first item.
    public static let allowPermissionsFirst = "Allow the permissions Earlet needs, then enable it."

    /// A press refused because the last dictation was still being
    /// transcribed or typed.
    public static func stillFinishing(holdKey: DictationHoldKey) -> String {
        "Still finishing the last dictation, so that press was not recorded. Hold \(holdKey.name) again."
    }

    /// The status line when a hold reaches the ceiling
    /// (`DictationTiming.maximumHoldSeconds`): Windows'
    /// `DictationCopy.StoppedTranscribing`, which names its own five minutes.
    public static let maximumHoldReached =
        "The recording reached its safety limit. Transcribing your captured words…"

    /// The status line after a dictation the ceiling finished: Windows'
    /// `DictationCopy.InsertedAfterStop`.
    public static func insertedAfterMaximumHold(holdKey: DictationHoldKey) -> String {
        "Text inserted. Hold \(holdKey.name) again to continue."
    }

    public static let workerStopped =
        "The speech engine stopped unexpectedly, so nothing was inserted. It is restarting; try again in a moment."

    /// A refused paste, and whether the text was left on the clipboard.
    public static func insert(_ failure: DictationInsertFailure, keptOnClipboard: Bool) -> String {
        switch failure {
        case .secureFieldFocused:
            return "Earlet does not type into password fields. Nothing was kept."
        case .secureInputActive:
            return "Secure input is on, usually because a password field has focus. Nothing was typed or kept."
        case .accessibilityPermissionMissing:
            return keptOnClipboard
                ? "Earlet needs Accessibility permission to type. Your text is on the clipboard; press ⌘V to paste it."
                : "Earlet needs Accessibility permission to type. Allow it in System Settings → Privacy & Security → Accessibility."
        case .focusedElementNotEditable, .noFocusedElement, .pasteNeverRequested, .keyEventPostFailed:
            return keptOnClipboard
                ? "The text could not be typed here. It is on the clipboard; press ⌘V to paste it."
                : "The text could not be typed here or put on the clipboard. Try again."
        }
    }

    // ─── Recover Last Dictation ───
    //
    // A take that could not be transcribed keeps its recording, and the
    // status line says where it went; the menu and the window's Saved
    // recording section offer to transcribe it again. The Mac's title-hint
    // pair of the design's copy table (LIMIT 2026-10-10, rule zero).

    /// The menu item and the section's button.
    public static let recoverLastDictation = "Recover Last Dictation"

    /// The button and the menu item while a recovery runs.
    public static let recoveringLastDictation = "Recovering…"

    /// A take's length as m:ss, for the saved recording's detail.
    public static func takeLength(_ seconds: TimeInterval) -> String {
        let total = Int(seconds.rounded())
        return "\(total / 60):\(String(format: "%02d", total % 60))"
    }

    /// Where a kept recording is pointed at, as the end of a sentence.
    public static let takeSavedWhere =
        "The recording is saved: choose Recover Last Dictation from the Earlet menu to try again."

    /// A transcription failure whose recording is kept.
    public static let takeSaved = "Couldn't transcribe that dictation, so nothing was inserted. " + takeSavedWhere

    /// The worker lost the decode and is restarting; the take is kept.
    public static let takeSavedWorkerRestarting =
        "The speech engine stopped unexpectedly, so nothing was inserted. It is restarting. " + takeSavedWhere

    /// The engine never came up for the release; the take is kept.
    public static let takeSavedEngineNotStarted =
        "The speech engine could not start, so nothing was inserted. " + takeSavedWhere

    /// The engine took too long to start for the release; the take is kept.
    public static let takeSavedEngineStartTimedOut =
        "The speech engine took too long to start, so nothing was inserted. " + takeSavedWhere

    /// The decode missed its deadline; the take is kept and the worker restarts.
    public static let takeSavedEngineNotResponding =
        "The speech engine stopped responding, so nothing was inserted. It is restarting. " + takeSavedWhere

    /// While the saved recording is transcribed.
    public static let recoveringSavedTake = "Transcribing your saved recording…"

    /// Transcribed; its words are in the recent result and on their way to
    /// the clipboard through Copy last text (never pasted: the user is in
    /// the window or the menu, not the field they spoke into).
    public static let savedTakeRecovered =
        "Your saved recording is transcribed. Its words are in Last available text below."

    /// The recovery's decode failed; the recording is still kept.
    public static let savedTakeRecoveryFailed =
        "Couldn't transcribe your saved recording. It is still saved; try again."

    /// The speech engine would not start for the recovery; still kept.
    public static let savedTakeEngineUnavailable =
        "The speech engine could not start. Your saved recording is still saved; try again."

    /// The saved recording could not be opened just now; it is not damaged
    /// and stays saved.
    public static let savedTakeNotOpened =
        "Your saved recording could not be opened just now. It is still saved; try again."

    /// The saved recording could not be read back: discarded.
    public static let savedTakeUnreadable =
        "Your saved recording could not be read, so it was discarded."

    /// The saved recording held nothing worth typing: discarded.
    public static let savedTakeNothingHeard =
        "Nothing was heard in your saved recording, so it was discarded."

    /// Discarded by the user.
    public static let savedTakeDiscarded = "The saved recording was discarded."

    /// Under the last available text: where else its words are. Windows,
    /// which keeps no history, says the second form.
    public static func recentResultFootnote(inHistory: Bool) -> String {
        inHistory
            ? "Only the latest available text is kept here, until you dismiss it or quit. Your history keeps a copy, encrypted on this Mac."
            : "Only the latest available text is kept, until you dismiss it or quit. It isn’t saved to disk."
    }

    /// The launch offer for a take a quit or crash left mid-recording.
    public static let savedTakeOfferedAtLaunch =
        "Earlet closed before a dictation was transcribed. The recording is saved: choose Recover Last Dictation from the Earlet menu."
}
