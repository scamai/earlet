using Dictation.Services;
using Dictation.Services.Learning;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>Why the running hold was cancelled, as the hook asked: or that
/// Escape only asked first, on a long take. A microphone that goes away
/// cancels nothing since 2026-10-10: it ends the take and keeps it
/// (DictationController.Takes.cs).</summary>
public enum TriggerCancel
{
    /// <summary>Nothing was asked.</summary>
    None,

    /// <summary>Escape, on a press waiting for its password-field check or
    /// a hold that was listening or transcribing.</summary>
    Escape,

    /// <summary>Another key went down while the hold key was held: a
    /// shortcut such as Right Ctrl+C, not a dictation.</summary>
    Chord,

    /// <summary>Escape on a take long enough to ask first
    /// (<see cref="IDictationTrigger.EscapeAsksFirst"/>): nothing is cancelled
    /// yet, the user is asked, and the next Escape cancels
    /// (DictationController.Takes.cs).</summary>
    EscapeAsked,
}

/// <summary>
/// The hold-key hook as the controller needs it: the push-to-talk registration
/// plus the extras the Windows hook carries — the Escape (and chord) cancel
/// latch, the "a dictation is running" flag that arms it, and the hands-free
/// latch.
/// </summary>
/// <remarks>
/// Dictation.App adapts <c>LowLevelHoldKeyRegistration</c> to this. Tests hand
/// in a fake whose key state they set directly. The hook watch's members
/// (<see cref="IHookWatchTarget"/>) are in DictationHookWatch.cs.
/// </remarks>
public interface IDictationTrigger : IHotkeyRegistration, IHookWatchTarget, IDisposable
{
    /// <summary>The cancel the hook latched since the last call, once, or
    /// <see cref="TriggerCancel.None"/>.</summary>
    TriggerCancel ConsumeCancelRequest();

    /// <summary>Whether Escape means anything to the hook right now: a press
    /// is waiting for its password-field check, or a dictation is listening
    /// or transcribing.</summary>
    bool DictationInFlight { get; set; }

    /// <summary>Whether the next Escape on that dictation only asks
    /// (<see cref="TriggerCancel.EscapeAsked"/>) instead of cancelling: the
    /// take holds more than thirty seconds of speech. The hook lowers it at
    /// that Escape, so the one after it cancels.</summary>
    bool EscapeAsksFirst { get; set; }

    /// <summary>
    /// Called by the hook for every key-down with the time (Unix
    /// milliseconds) and the foreground window's process id — never which
    /// key. Null, the default, costs the hook nothing. The controller sets it
    /// only while a correction watch runs: it is how the span observer tells
    /// the user's own typing from anything else that changes a field.
    /// </summary>
    Action<long, int>? KeyboardInputObserved { get; set; }

    /// <summary>
    /// Whether a double-tap has latched the hold open with the key released:
    /// true from the second press until one more press, Space or Enter,
    /// Escape, or <see cref="EndLatch"/>. While it is true the hook swallows
    /// Space and Enter. Always false for a trigger armed without the latch.
    /// </summary>
    bool IsLatched { get; }

    /// <summary>
    /// End a latched hold, or a short press's hold still waiting out the
    /// double-tap window, from outside the keyboard stream: the recording
    /// limit, a silence stop, Cancel, a refused press, or a latch with no
    /// dictation behind it. The controller and the hook share one latch flag, and a stale one
    /// would keep the microphone open and Space and Enter swallowed, so every
    /// path that ends a hold without a key edge calls this. A press still
    /// physically down holds nothing afterwards.
    /// </summary>
    void EndLatch();

    /// <summary>
    /// Which key ended a latch since the last call, once, or
    /// <see cref="LatchEnd.None"/>. A key ends a latch inside the hook, before
    /// the controller looks, so this is how the log can say what stopped it.
    /// The ends the controller makes itself (<see cref="EndLatch"/>) read
    /// <see cref="LatchEnd.None"/>.
    /// </summary>
    LatchEnd ConsumeLatchEnd();
}

/// <summary>A speech worker process, and the moment its startup ends.</summary>
/// <param name="Process">The worker the host frames requests to.</param>
/// <param name="StartupFinished">
/// Called once the first model load has been attempted. The Windows worker
/// keeps its stderr at a level the log file records until then, and drops it
/// afterwards, so load failures are diagnosable without the log ever holding
/// fragments of what the user said.
/// </param>
public sealed record DictationWorker(IAsrWorkerProcess Process, Action StartupFinished);

/// <summary>The four Win32 edges focused-text insertion runs against.</summary>
public sealed record DictationInsertionEdges(
    IFocusedApplicationResolver Target,
    IClipboard Clipboard,
    IPasteKeystroke Paste,
    ISecureFieldProbe SecureField);

/// <summary>
/// Whether Dictation starts when the user signs in to Windows. The Windows
/// half is the <c>Run</c> value under <c>HKEY_CURRENT_USER</c>; see
/// <see cref="LaunchAtLoginEntry"/> for the decisions about it.
/// </summary>
public interface ILaunchAtLogin
{
    /// <summary>Whether this copy of Dictation will start at the next sign-in.</summary>
    bool IsEnabled { get; }

    /// <summary>Whether the entry names this copy but Settings → Apps →
    /// Startup switched it off (<see cref="LaunchAtLoginEntry.IsSwitchedOff"/>):
    /// <see cref="IsEnabled"/> is false then, and the window says why.</summary>
    bool SwitchedOffInSettings { get; }

    /// <summary>Turn it on or off.</summary>
    /// <returns>False when the change could not be written.</returns>
    bool TrySet(bool enabled);
}

/// <summary>
/// Everything in the Windows composition that needs Win32. The controller
/// builds the session, microphone tap, worker host and text insertion itself
/// from these.
/// </summary>
public interface IDictationPlatform
{
    /// <param name="binding">The hold key.</param>
    /// <param name="cancelOnChord">Whether another key pressed during a hold
    /// cancels it (see <c>HoldKeyState</c>). The controller always asks for
    /// it; the parameter makes that policy visible here and testable there.</param>
    /// <param name="handsFree">Whether a double-tap of the hold key latches
    /// the hold open, with the Mac's rules
    /// (<c>HoldKeyTriggerMode.HoldOrDoubleTapHandsFree</c>). The controller
    /// always asks for it, for the same reason.</param>
    IDictationTrigger CreateTrigger(HotkeyBinding binding, bool cancelOnChord, bool handsFree);

    IAudioCaptureDevice CreateMicrophone();

    DictationWorker CreateWorker(DictationWorkerPlan plan);

    DictationInsertionEdges CreateInsertionEdges();

    IMicrophoneAccessProbe MicrophoneAccess { get; }

    ILaunchAtLogin LaunchAtLogin { get; }

    /// <summary>Show a folder in Explorer, creating it first if it is missing.</summary>
    /// <returns>False when it could not be shown.</returns>
    bool OpenFolder(string path);

    /// <summary>Open File Explorer on the folder that holds
    /// <paramref name="file"/>, with the file selected: Troubleshooting's
    /// Show this copy in File Explorer, the Mac's Show This Copy in Finder.</summary>
    /// <returns>False when the file is not there or File Explorer could not
    /// be started. True once it started: Explorer does not report whether it
    /// selected the file.</returns>
    bool ShowInFolder(string file);

    /// <summary>
    /// The focused field, resolved once, now, for the span observer: a
    /// bounded cross-process call, so never on the UI thread. Never throws;
    /// a focus that cannot be resolved answers with
    /// <see cref="SpanObserverTargetIdentity.Unresolved"/>, and every later
    /// failure is a failure value the observer ends on.
    /// </summary>
    /// <param name="keyboard">The hook's keyboard-origin feed, which the
    /// target reports as its last keyboard input.</param>
    /// <param name="options">The observer's knobs; the target uses
    /// <c>t_uia</c> as the deadline of every call.</param>
    ISpanObserverTarget CreateSpanObserverTarget(SpanObserverKeyboardSignal keyboard, SpanObserverOptions options);

    /// <summary>
    /// The clipboard Copy last text writes to: the same writer a refused
    /// paste's fallback uses (on Windows it keeps the words out of clipboard
    /// history and cloud sync), and one that needs no armed session, so a held
    /// result can be copied while dictation is paused.
    /// </summary>
    IClipboard Clipboard { get; }

    /// <summary>What the window in front runs as, for the hint that the hold
    /// key cannot be heard while one running as administrator is in front
    /// (<see cref="AdminWindowWatch"/>).</summary>
    IForegroundElevation ForegroundElevation { get; }

    /// <summary>
    /// Play the system's alert sound once: the countdown's minute before the
    /// recording limit has begun (<see cref="DictationHoldPolicy.Countdown"/>).
    /// The one sound Dictation makes, and it follows the user's sound scheme.
    /// Never awaited; may throw, which the controller logs and ignores.
    /// </summary>
    void PlayAlertSound();
}

/// <summary>Which picture the notification-area icon shows.</summary>
public enum TrayIcon
{
    /// <summary>Dictation is paused: the microphone and the hold key are off.</summary>
    Off,

    /// <summary>Dictation is on and waiting for the hold key.</summary>
    Ready,

    /// <summary>The hold key is down and the microphone is open.</summary>
    Listening,

    /// <summary>The hold is over and its words are being transcribed or
    /// typed. The microphone is closed, and a press now is refused.</summary>
    Transcribing,

    /// <summary>Windows blocks the microphone, so a hold cannot record: a
    /// warning triangle, the Mac's <c>exclamationmark.triangle</c> for
    /// <c>needsPermission</c>. Shown whenever no dictation is under way,
    /// dictation on or off (<see cref="DictationTrayGlyph"/>). The same
    /// warning, with its own tooltip, when the hold-key hook was removed and
    /// Windows would not put it back (<see cref="HookHealth.Unheard"/>).</summary>
    MicrophoneBlocked,
}

/// <summary>The notification-area icon and the controller's half of its
/// menu; <see cref="DictationTrayMenu"/> lays the menu out, with the
/// Vocabulary section's learning lines.</summary>
/// <param name="Icon">The picture.</param>
/// <param name="ToolTip">The hover text, one of <see cref="DictationCopy"/>'s.</param>
/// <param name="ToggleLabel">The Enable/Pause item's label.</param>
/// <param name="CanToggle">Whether that item is live: Enable as the window's
/// Enable button is, Pause as its Pause button is
/// (<see cref="DictationViewState.CanPause"/>).</param>
/// <param name="Recent">Copy last text and its recovery line, while a recent
/// result is held; null hides them.</param>
/// <param name="CanCancel">Whether the menu shows Cancel dictation: a
/// dictation is listening or transcribing. With the key up in a hands-free
/// latch, this and Escape are what discard it while the window is hidden.</param>
/// <param name="SavedTake">Recover last dictation, while a take that could
/// not be transcribed is kept; null hides it.</param>
public sealed record DictationTrayState(
    TrayIcon Icon,
    string ToolTip,
    string ToggleLabel,
    bool CanToggle,
    DictationRecentMenu? Recent = null,
    bool CanCancel = false,
    DictationSavedTakeMenu? SavedTake = null);

/// <summary>What the window shows. One immutable snapshot per change.</summary>
/// <param name="Status">The status line: always one of <see cref="DictationCopy"/>'s sentences.</param>
/// <param name="OffersMicrophoneSettings">Show the button that opens
/// <see cref="DictationCopy.MicrophoneSettingsUri"/>.</param>
/// <param name="HoldKey">The hold key in effect, selected in the picker.</param>
/// <param name="CanChooseHoldKey">The picker is live.</param>
/// <param name="HoldKeyNotice">The caution under the picker, or null.</param>
/// <param name="Instructions">The how-to line, naming the hold key.</param>
/// <param name="CanCancel">Cancel dictation shows beside Enable/Pause: a
/// dictation is listening or transcribing, and cancelling it inserts nothing.
/// The window collapses it otherwise, as the Mac's header shows it only while
/// there is something to cancel.</param>
/// <param name="Language">The recognition language, selected in its picker.</param>
/// <param name="CanChooseLanguage">The language picker is live: never while
/// a dictation runs, whose decode reads the language when it is sent, nor
/// while the recogniser restarts after a crash.</param>
/// <param name="LaunchAtLogin">Whether the start-at-sign-in box is checked.</param>
/// <param name="Tray">The notification-area icon.</param>
/// <param name="LearnFromCorrections">Whether the "Learn from my corrections"
/// switch is on.</param>
/// <param name="PracticeVisible">Show "Try dictation": dictation is on and
/// Windows is not blocking the microphone, so a hold into the practice field
/// can work. It stays up through the practice dictation itself.</param>
/// <param name="PracticeStartsExpanded">"Try dictation" opens when it appears:
/// no dictation has delivered its words yet. Its fall, while the field does
/// not have the keyboard, collapses the section (<see cref="PracticeDisclosure"/>).</param>
/// <param name="MicrophoneOpen">The hold is listening, held or hands-free.
/// The window's live regions say nothing then: Narrator through the speakers
/// would be dictated, and dictating into the window's own practice field keeps
/// it active (the indicator keeps the same silence).</param>
/// <param name="Recent">The Recent result section, or null when nothing is held.</param>
/// <param name="Header">The window's status header: glyph, headline and the
/// one Enable/Pause button (<see cref="DictationWindowHeader"/>).</param>
/// <param name="LaunchAtLoginNote">The note under "Start Dictation when I
/// sign in": a change Windows refused, or the entry switched off in Settings
/// → Apps → Startup; null for none.</param>
/// <param name="SavedTake">The Saved recording section, with Recover last
/// dictation, or null when no take is kept.</param>
/// <param name="SilenceStop">The hands-free silence stop, selected in the
/// "Silence stops hands-free" picker; it can be changed at any time.</param>
public sealed record DictationViewState(
    string Status,
    bool OffersMicrophoneSettings,
    HotkeyBinding HoldKey,
    bool CanChooseHoldKey,
    string? HoldKeyNotice,
    string Instructions,
    bool CanCancel,
    SpeechLanguage Language,
    bool CanChooseLanguage,
    bool LaunchAtLogin,
    DictationTrayState Tray,
    bool LearnFromCorrections,
    bool PracticeVisible,
    bool PracticeStartsExpanded,
    bool MicrophoneOpen,
    DictationRecentView? Recent,
    DictationWindowHeader Header,
    string? LaunchAtLoginNote,
    DictationSavedTakeView? SavedTake = null,
    DictationSilenceStop SilenceStop = DictationSilenceStop.After30Seconds)
{
    /// <summary>Enable is live: the window's one Enable/Pause button offers
    /// Enable and is live (<see cref="DictationWindowHeader.CanToggle"/>), as
    /// the menu's item is. Read from <see cref="Header"/>, which the window
    /// and the menu draw, so a test of it tests what they show.</summary>
    public bool CanEnable => Header.ToggleEnables && Header.CanToggle;

    /// <summary>Pause is live, in the window's one Enable/Pause button and
    /// the menu's item alike: dictation is on and no dictation is listening,
    /// transcribing or typing, as the Mac's toggle is disabled while it
    /// records or is busy. Cancel is what ends a dictation, and no Pause cuts
    /// into the clipboard restore. Read from <see cref="Header"/>, as
    /// <see cref="CanEnable"/> is.</summary>
    public bool CanPause => !Header.ToggleEnables && Header.CanToggle;
}

/// <summary>The window, as the controller drives it.</summary>
public interface IDictationView
{
    void Render(DictationViewState state);

    /// <summary>Exit the app: a quit the controller deferred can now go ahead.</summary>
    void Quit();

    /// <summary>Bring the window forward — a launch at sign-in that started
    /// hidden and then could not turn dictation back on.</summary>
    void Reveal();
}

/// <summary>
/// The floating indicator: a pill near the bottom of the screen the user is
/// working on, which says what a hold is doing and how it ended while the
/// window is hidden. <see cref="DictationHudFeed"/> decides what it says and
/// for how long; this is only the drawing.
/// </summary>
/// <remarks>
/// Called on the controller's thread. The window must never take the focus,
/// or the words would be pasted into it instead of the field the user is in.
/// </remarks>
public interface IDictationHud
{
    /// <summary>Show <paramref name="model"/>, replacing whatever is shown.
    /// Its dwell is the feed's to keep; the window only draws.</summary>
    /// <param name="announce">Whether a screen reader is told. False while
    /// the hold is listening: the microphone is open then, and Narrator
    /// speaking through the speakers would be dictated into the user's text.
    /// The macOS indicator keeps the same silence (DictationHUDAnnouncer).</param>
    void Show(DictationHudModel model, bool announce);

    /// <summary>
    /// Draw the level bars in the mark's place at <paramref name="level"/>, a
    /// smoothed microphone peak (0…1, <see cref="DictationHudLevel"/>). Called
    /// only while the pill says the hold is listening, after its
    /// <see cref="Show"/>; the next <see cref="Show"/> or <see cref="Hide"/>
    /// puts the mark back. Never announced, and never part of what a screen
    /// reader is given.
    /// </summary>
    void Level(float level);

    /// <summary>Take the pill away.</summary>
    void Hide();

    /// <summary>Whether a pill is on screen now, by the window's own account:
    /// the window shown and not still transparent, even after a
    /// <see cref="Hide"/> that did not take. Read once a second by the feed's
    /// heartbeat (<see cref="DictationHudReconcile"/>), which draws a pill
    /// the window lost again and takes away one it kept.</summary>
    bool IsShowing { get; }
}

/// <summary>What closing the window does.</summary>
public enum DictationCloseAction
{
    /// <summary>Close it and exit: Quit, or Windows signing out.</summary>
    Close,

    /// <summary>Hide it; Dictation keeps running from the notification area,
    /// on or off. The window's own close button always gets this.</summary>
    Hide,

    /// <summary>Not yet: an insertion has the user's clipboard, and the
    /// controller calls <see cref="IDictationView.Quit"/> once it is back.</summary>
    Wait,
}

/// <summary>The answer to a close.</summary>
/// <param name="Action">What to do with the window.</param>
/// <param name="Notice">A one-time sentence to show from the notification
/// area when the window hides, or null.</param>
public sealed record DictationCloseDecision(DictationCloseAction Action, string? Notice = null);

/// <summary>How the app starts.</summary>
/// <param name="ShowWindow">False only for a launch at sign-in that is turning
/// dictation back on: the notification-area icon is enough.</param>
/// <param name="Enabling">The automatic enable, when one was started, so a
/// caller (a test) can wait for it; a completed task otherwise.</param>
public sealed record DictationStartup(bool ShowWindow, Task Enabling);
