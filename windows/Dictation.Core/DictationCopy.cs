using Dictation.Core.Learning;
using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core;

/// <summary>A status line, and whether it comes with the button that opens
/// Windows' microphone privacy page.</summary>
public sealed record DictationStatus(string Text, bool OffersMicrophoneSettings = false)
{
    /// <summary>
    /// The hook watch's sentence about the hold key, carried after a
    /// blocked-microphone <see cref="Text"/> while both are current, so the
    /// window says both on one status line and neither replaces the other. The
    /// microphone comes first, with its buttons: the Mac's setup steps put the
    /// microphone before the shortcut (<c>DictationPermissionStep.resolve</c>),
    /// as the icon's tooltip does here (<see cref="DictationTrayGlyph"/>).
    /// Null on every other line; see <see cref="DictationCopy.WithHoldKeySentence"/>.
    /// </summary>
    public string? HoldKeySentence { get; init; }

    /// <summary>What the window's status line shows.</summary>
    public string Line => HoldKeySentence is null ? Text : Text + " " + HoldKeySentence;
}

/// <summary>
/// Every sentence the Windows window, its notification-area icon and its
/// notices can show. Fixed text only: no enum member, exception message,
/// worker error, expanded path or dictated word reaches the user — those go
/// to the log, which the failure sentences point at through the Open log
/// folder button. The one folder named, the log's, is written with
/// <c>%LOCALAPPDATA%</c>, never with the user's name.
/// </summary>
public static partial class DictationCopy
{
    public const string Ready = "Ready to enable. Your audio stays on this device.";
    public const string Preparing = "Preparing speech recognition…";
    public const string Paused = "Paused. The microphone is off.";
    public const string Transcribing = "Transcribing on this device…";
    public const string Inserting = "Inserting…";
    public const string FinishingBeforeClose = "Finishing the insertion, then closing…";

    /// <summary>The label of the button that opens the log folder. Failure
    /// sentences name it, so it is written once, here.</summary>
    public const string OpenLogFolder = "Open log folder";

    /// <summary>The window's closed section that holds
    /// <see cref="OpenLogFolder"/>, this copy's identity and Show this copy
    /// in File Explorer: the Mac's Troubleshooting disclosure.</summary>
    public const string TroubleshootingHeading = "Troubleshooting";

    /// <summary>The pointer every failure sentence ends with. The button is
    /// in the window's closed Troubleshooting section, so the sentence says
    /// where; the notification-area menu has the item too.</summary>
    public const string LogDetails = "For details, choose " + OpenLogFolder + " under " + TroubleshootingHeading + ".";

    /// <summary>The window's footer: the Mac's sentence, then Windows' own
    /// about closing, which never quits (the notification area keeps the app,
    /// on or off; Quit is in its menu).</summary>
    public const string Footer =
        "Your audio stays on this device. No account is required. "
        + "No transcript history is saved, and a recording only until its words are in, or for you to recover. "
        + "Closing this window keeps Earlet in the notification area; choose " + QuitDictation + " there to exit.";

    // ─── The window's header (DictationTrayGlyph.Headline) ──────────────
    //
    // One short line over the status sentence, which carries the detail and
    // the advice: the Mac's DictationMenuGlyph.headline.

    /// <summary>Off, not paused, as the Mac words it: only a pause is the
    /// user's choice.</summary>
    public const string HeadlineOff = "Earlet is off";
    public const string HeadlineStarting = "Starting dictation…";
    public const string HeadlineReady = "Earlet is on";
    public const string HeadlineListening = "Listening…";
    public const string HeadlineTranscribing = "Transcribing…";

    /// <summary>The words are going into the field: Windows' own state, which
    /// the tray calls "typing" too.</summary>
    public const string HeadlineTyping = "Typing…";

    /// <summary>The Mac's "Needs permission", for Windows' one grant.</summary>
    public const string HeadlineMicrophoneBlocked = "Microphone blocked";

    // The hook watch's three states (DictationHookWatch), each the headline of
    // the icon's tooltip for it. Keyless, as the Mac's headlines are: the
    // status line and the shortcut line under it name the key.

    /// <summary>A hook Windows would not put back: <see cref="TrayHoldKeyUnheard"/>,
    /// the Mac's "Needs permission" for its lost key listener.</summary>
    public const string HeadlineHoldKeyUnheard = "Can't hear the hold key";

    /// <summary>A window running as administrator in front:
    /// <see cref="TrayAdminWindowInFront"/>.</summary>
    public const string HeadlineAdminWindowInFront = "Can't hear keys in administrator windows";

    /// <summary>A reconnected hook that has not heard a key yet:
    /// <see cref="TrayHoldKeyReconnected"/>.</summary>
    public const string HeadlineHoldKeyReconnected = "Reconnected — hold the key again";

    // ─── Troubleshooting ────────────────────────────────────────────────

    /// <summary>Over this copy's version, build and path: the Mac's words.</summary>
    public const string ThisCopyHeading = "This copy of Earlet";

    /// <summary>What a screen reader calls the box with this copy's path.</summary>
    public const string ThisCopyPathName = "Path of this copy";

    /// <summary>The Mac's "Show This Copy in Finder", in Windows' words.</summary>
    public const string ShowThisCopy = "Show this copy in File Explorer";

    /// <summary>File Explorer did not open on this copy. The path is shown
    /// right above the button, so the sentence points there rather than
    /// repeating a path that holds the user's name.</summary>
    public const string ThisCopyNotShown =
        "File Explorer could not show this copy. Its path is under " + TroubleshootingHeading + ".";

    public const string EnableLabel = "Enable dictation";
    public const string PauseLabel = "Pause dictation";

    /// <summary>The notification-area menu's Cancel, shown while a dictation
    /// is listening or transcribing: the Mac menu's words.</summary>
    public const string CancelLabel = "Cancel dictation";

    public const string HoldKeyNotSaved =
        "The new key works now, but it could not be saved and will reset when Earlet restarts.";

    public const string LanguageNotSaved =
        "The new language works now, but it could not be saved and will reset when Earlet restarts.";

    /// <summary>The learning switch changed for this run only: off turns back
    /// on at the next launch, so a silent revert of a privacy switch is never
    /// the user's only clue.</summary>
    public const string LearnFromCorrectionsNotSaved =
        "The “Learn from my corrections” change works now, but it could not be saved and will reset when Earlet restarts.";

    public const string ResourcesMissing =
        "Speech resources are missing. Install the complete Earlet package and try again.";

    public const string ModelDidNotLoad =
        "The speech model could not load. Check the Earlet installation and try again. " + LogDetails;

    public const string ShortcutRefused =
        "The shortcut could not be enabled. Try enabling dictation again.";

    public const string EnableFailed =
        "Earlet could not be enabled. Try again. " + LogDetails;

    /// <summary>Open log folder failed: the window or the menu. Names the
    /// folder, as the Mac does, so the user can find it by hand. Written with
    /// the variable, which Explorer's address bar expands, never the
    /// expanded path with the user's name in it.</summary>
    public const string LogFolderNotOpened =
        @"The log folder could not be opened. It is %LOCALAPPDATA%\" + DictationPaths.ProductDirectoryName + @"\"
        + DictationPaths.LogDirectoryName + ".";

    // The notes under "Start Dictation when I sign in", as the Mac shows one
    // under "Open Dictation at login".

    /// <summary>Checking the box could not write the sign-in entry: the Mac's
    /// refused register.</summary>
    public const string LaunchAtLoginNotTurnedOn =
        "Windows did not let Earlet start when you sign in. Try again. " + LogDetails;

    /// <summary>Clearing the box could not remove the entry: the Mac's
    /// refused unregister, which points at where to remove it by hand.</summary>
    public const string LaunchAtLoginNotTurnedOff =
        "Windows did not remove Earlet from the apps that start when you sign in. "
        + "Turn it off in Settings → Apps → Startup.";

    /// <summary>The entry names this copy, and Settings or Task Manager
    /// switched it off: the box reads unchecked, and checking it turns it back
    /// on. The Mac's "Allow Dictation in … Login Items" note, for the switch
    /// Windows keeps.</summary>
    public const string LaunchAtLoginSwitchedOff =
        "Turned off in Settings → Apps → Startup. Check the box to turn it back on.";

    /// <summary>Windows' microphone privacy page, where the switch is.</summary>
    public const string MicrophoneSettingsUri = MicrophoneAccessCopy.PrivacySettingsUri;

    public const string MicrophoneBlocked =
        "Windows is blocking the microphone for Earlet. Turn on microphone access "
        + "for desktop apps in Privacy & security, then hold the key again.";

    public const string MicrophoneBlockedByPolicy =
        "A policy on this computer blocks microphone access for desktop apps. "
        + "An administrator has to change it before Earlet can hear anything.";

    /// <summary>The label of the button beside the privacy-page link that
    /// asks Windows again.</summary>
    public const string CheckMicrophoneAgain = "Check again";

    /// <summary>Check again found the switch still off.</summary>
    public const string MicrophoneStillBlocked =
        "Windows is still blocking the microphone for Earlet. Turn on microphone access "
        + "for desktop apps in Privacy & security, then choose " + CheckMicrophoneAgain + ".";

    /// <summary>Paused while this run last learned that Windows blocks the
    /// microphone: the window says so, as the notification-area icon's
    /// warning does, and offers the privacy page and Check again, as the
    /// Mac's header shows its setup step while dictation is off.</summary>
    public const string PausedMicrophoneBlocked =
        "Paused. Windows is also blocking the microphone for Earlet. Turn on microphone access "
        + "for desktop apps in Privacy & security before you enable dictation.";

    /// <summary><see cref="PausedMicrophoneBlocked"/> for a block by policy,
    /// which offers neither button: clicking would change nothing.</summary>
    public const string PausedMicrophoneBlockedByPolicy =
        "Paused. A policy on this computer also blocks microphone access for desktop apps. "
        + "An administrator has to change it before Earlet can hear anything.";

    /// <summary>The press landed in a password field, and the microphone
    /// never opened.</summary>
    public const string SecureFieldAtPress =
        "Earlet does not work in password fields. Nothing was recorded.";

    /// <summary>The focus moved into a password field during the hold: the
    /// audio was dropped before the recogniser saw it.</summary>
    public const string SecureFieldAtRelease =
        "Earlet does not work in password fields. Nothing was typed or kept.";

    /// <summary>The field became a password field while the words were
    /// transcribed: they were not typed, put on the clipboard or kept. The
    /// macOS app's sentence for its refusal at the paste.</summary>
    public const string SecureFieldAtPaste =
        "Earlet does not type into password fields. Nothing was kept.";

    /// <summary>The paste was refused and the words are on the clipboard
    /// instead — the macOS app's fallback, in Windows' key.</summary>
    public const string KeptOnClipboard =
        "Couldn't type into that app. The text is on the clipboard — paste it with Ctrl+V.";

    /// <summary>No window had focus, and the words are on the clipboard.</summary>
    public const string KeptOnClipboardNoWindow =
        "No window had focus. The text is on the clipboard — paste it with Ctrl+V.";

    public const string WorkerStopped =
        "Speech recognition stopped, so nothing was inserted. It is restarting…";

    public const string WorkerStoppedWhileIdle = "Speech recognition stopped. It is restarting…";

    public const string WorkerRestartFailed =
        "Speech recognition could not restart. Pause and enable dictation to try again. " + LogDetails;

    /// <summary>A second launch that could not reach the running copy's
    /// window says this instead of bringing it forward.</summary>
    public const string AlreadyOpen = "Earlet is already open.";

    /// <summary>Startup failed before the window existed, so there is no
    /// Open log folder button to point at; the process exits after this.</summary>
    public const string CouldNotStart =
        "Earlet could not start. Open it again, and if that fails too, reinstall Earlet.";

    /// <summary>The one-time notice when the window first closes to the
    /// notification area, with dictation on.</summary>
    public const string StillRunningInTray =
        "Earlet is still on. It runs from the notification area; choose Quit there to exit.";

    /// <summary><see cref="StillRunningInTray"/> with dictation off: closing
    /// never quits, so a paused app keeps what it holds (Copy last text, the
    /// learning receipt) in the notification area too.</summary>
    public const string StillInTrayWhileOff =
        "Earlet is off. It stays in the notification area; choose Quit there to exit.";

    // The icon's tooltip, which carries the state the Mac menu's headline
    // does (DictationMenuGlyph): the menu has no headline on Windows.

    /// <summary>Off: paused, never enabled, or an Enable that failed. "Off",
    /// not "paused", as the Mac words it, because only a pause is the
    /// user's choice.</summary>
    public const string TrayOff = "Earlet — off";

    /// <summary>An Enable is loading the speech model: the Mac's "Starting
    /// dictation…". A sign-in launch keeps the window hidden for all of it.</summary>
    public const string TrayStarting = "Earlet — starting…";

    /// <summary>No dictation under way, while this run last learned that
    /// Windows blocks the microphone, whether dictation is on, starting or
    /// off: the Mac's "Needs permission", over the warning picture
    /// (<see cref="DictationTrayGlyph"/>), in the words of
    /// <see cref="HudMicrophoneBlocked"/>.</summary>
    public const string TrayMicrophoneBlocked = "Earlet — Windows is blocking the microphone";

    public const string TrayListening = "Earlet — listening";
    public const string TrayTranscribing = "Earlet — transcribing";
    public const string TrayTyping = "Earlet — typing";

    public static string TrayReady(HotkeyBinding key) => $"Earlet — hold {key.KeyName} to dictate";

    // ─── When the hold key cannot be heard (DictationHookWatch) ─────────
    //
    // Windows removes a hold-key hook that overran its time limit, and keys
    // sent to a window running as administrator never reach the hook. The
    // Mac reports a lost key listener with a status line and its window; so
    // does Windows, in Windows' words.

    /// <summary>The hook was found removed and a new one installed. True
    /// whether it missed a press (that hold recorded nothing) or a release
    /// (that hold ended late, and its words went in).</summary>
    public static string HoldKeyReconnected(HotkeyBinding key) =>
        $"Windows stopped passing {key.KeyName} to Earlet, so Earlet reconnected it. "
        + $"If a hold did nothing, click where you were typing and hold {key.KeyName} again.";

    /// <summary>The hook was found removed and Windows would not install a
    /// new one: the next press tries again; a Pause and Enable arms afresh.</summary>
    public static string HoldKeyUnheard(HotkeyBinding key) =>
        $"Windows stopped passing {key.KeyName} to Earlet and would not let it reconnect. "
        + "Pause and enable dictation again, or quit and reopen Earlet. " + LogDetails;

    /// <summary>A window running as administrator is in front, where the hook
    /// hears nothing (UIPI): after the shortcut line while it is, and the
    /// status line after a press made there.</summary>
    public static string AdminWindowInFront(HotkeyBinding key) =>
        $"Earlet can't hear {key.KeyName} while an administrator window is in front.";

    /// <summary>The shortcut line while a window running as administrator is
    /// in front.</summary>
    public static string InstructionsWithAdminWindow(HotkeyBinding key) =>
        Instructions(key) + " " + AdminWindowInFront(key);

    /// <summary>The hook watch's sentence on <paramref name="status"/>, alone
    /// or after a blocked microphone's, or null when it says none.</summary>
    public static string? HoldKeySentenceIn(DictationStatus status, HotkeyBinding key) =>
        status.HoldKeySentence
        ?? (status.Text == HoldKeyReconnected(key) || status.Text == HoldKeyUnheard(key)
            || status.Text == AdminWindowInFront(key)
            ? status.Text
            : null);

    /// <summary>
    /// One status line for a blocked microphone and the hook watch's sentence
    /// when both are current, whichever was learned first: the microphone's
    /// sentence and buttons, then the hold key's. Every path that puts either
    /// on the status line while the other is there goes through here.
    /// </summary>
    /// <param name="microphone">A blocked-microphone line
    /// (<see cref="IsMicrophoneBlocked"/>).</param>
    /// <param name="holdKey">The hook watch's sentence, or null for none.</param>
    public static DictationStatus WithHoldKeySentence(DictationStatus microphone, string? holdKey) =>
        microphone with { HoldKeySentence = holdKey };

    /// <summary>The icon while a reconnected hook has not heard a key yet.</summary>
    public static string TrayHoldKeyReconnected(HotkeyBinding key) =>
        $"Earlet — reconnected {key.KeyName}, hold it again";

    /// <summary>The icon, over the warning, while no hook could be put back.</summary>
    public static string TrayHoldKeyUnheard(HotkeyBinding key) =>
        $"Earlet — can't hear {key.KeyName}, open Earlet";

    /// <summary>The icon while a window running as administrator is in front.</summary>
    public static string TrayAdminWindowInFront(HotkeyBinding key) =>
        $"Earlet — can't hear {key.KeyName} while an administrator window is in front";

    // The notification-area menu's own items (DictationTrayMenu): the Mac
    // menu's words in Windows' sentence case. Its other items reuse the
    // window's labels: Enable/Pause, Cancel, Copy last text, Open log folder.

    /// <summary>The default item, in bold, and what a left click does.</summary>
    public const string ShowDictation = "Show Earlet";
    public const string QuitDictation = "Quit Earlet";

    /// <summary>An inactive line over Review vocabulary… and Undo last
    /// learning while a learning receipt offers Undo. Names no word.</summary>
    public const string TrayCorrectionRemembered = "Remembered your correction";
    public const string ReviewVocabulary = "Review vocabulary…";
    public const string UndoLastLearning = "Undo last learning";

    // ─── The floating indicator ─────────────────────────────────────────
    //
    // Short lines for the pill, where the window's sentence would not fit.
    // Only where Dictation knows more than the shared model's own lines, or
    // where those name the other app: everything else is DictationHudModel's.
    // Never a dictated word: the pill is seen by whoever is behind the user
    // and by anything sharing the screen.

    /// <summary>A press refused because the last dictation was still going in.</summary>
    public const string HudStillTyping = "Still typing the last one — hold again";

    public const string HudPasswordFieldAtPress = "Password field — nothing was recorded";

    /// <summary>A password field found at the release or at the paste: in
    /// both, nothing was typed or kept.</summary>
    public const string HudPasswordFieldAtRelease = "Password field — nothing was typed or kept";

    /// <summary>The paste was refused and the words are on the clipboard.</summary>
    public const string HudKeptOnClipboard = "Couldn't type here — it's on the clipboard, press Ctrl+V";

    public const string HudMicrophoneBlocked = "Windows is blocking the microphone — open Earlet to see why";

    public const string HudCancelledByShortcut = "Cancelled — that was a shortcut";

    // The shared model's lines that said what happened and stopped there,
    // each given the "— what to do" half every other line has: the Mac's
    // words where the Mac has the ending, the window's where it does not.

    /// <summary>The paste was refused and nothing kept the words: the Mac's
    /// "Couldn't type or copy".</summary>
    public const string HudCouldNotTypeOrCopy = "Couldn't type or copy — click a text field and try again";

    /// <summary>No window had focus and nothing kept the words.</summary>
    public const string HudNoWindow = "No window had focus — click a text field and try again";

    /// <summary>The microphone opened and sent no sound: the Mac's "No sound
    /// from the microphone", pointing at Windows' sound settings.</summary>
    public const string HudNoSound = "No sound from the microphone — check the input device in Windows sound settings";

    /// <summary>The microphone would not open at all. The Mac has no such
    /// ending; the words are the window's.</summary>
    public const string HudMicrophoneWouldNotOpen =
        "The microphone would not open — check that one is connected and not in use";

    /// <summary>Nothing worth typing was heard: the Mac's "Nothing heard",
    /// its "Hold Right Command while you speak" shortened to "and speak" so
    /// the line can be read in the notice tier's 2.5 s.</summary>
    public static string HudNothingHeard(string keyName) => $"Nothing heard — hold {keyName} and speak";

    /// <summary>A hold too short to be a sentence. This app's tap has no
    /// minimum (a lone quick tap is transcribed, as on the Mac), so the
    /// shared session never reports one here; the line is complete anyway.</summary>
    public static string HudTooShort(string keyName) => $"Too short — hold {keyName} longer";

    public const string HudRecognizerRestarting = "Speech recognition stopped — restarting it";

    public const string HudRecognizerNotRunning = "Speech recognition is not running — open Earlet";

    /// <summary>Every restart failed; the window says what to do.</summary>
    public const string HudRecognizerCouldNotRestart = "Speech recognition couldn't restart — open Earlet";

    public const string HudRecognizerFailed = "Speech recognition failed — try again";

    // ─── The busy pills: held, hands-free, transcribing, typing ─────────
    //
    // The Mac's DictationHUDContent title and hint, drawn as Windows' two
    // lines (title — hint). Never spoken while the microphone is open, so
    // none of the listening lines reaches Narrator, the clock included.

    /// <summary>The title of every listening pill, held or hands-free, as on
    /// the Mac.</summary>
    private const string ListeningTitle = "Listening";

    /// <summary>
    /// The name a screen reader finds on a listening pill, which is shown and
    /// never announced: the state's alone, "Listening" or "Listening,
    /// hands-free", never the hint, the clock or the countdown. The Mac's
    /// <c>DictationHUDAccessibility.label</c> for <c>.recording(latched:)</c>.
    /// </summary>
    /// <param name="latched">The hold is latched hands-free.</param>
    public static string HudListeningName(bool latched) => latched ? ListeningTitle + ", hands-free" : ListeningTitle;

    /// <summary>A held hold: the Mac's "Listening" over "Release to insert ·
    /// Esc to cancel".</summary>
    public const string HudHeld = ListeningTitle + " — Release to insert · Esc to cancel";

    /// <summary>Released, being transcribed; Escape still cancels until the
    /// paste begins. The Mac's "Transcribing…" over "Esc to cancel".</summary>
    public const string HudTranscribing = "Transcribing… — Esc to cancel";

    /// <summary>The paste is going in and can no longer be cancelled. The Mac
    /// has no such state; it is Windows' own, kept from the shared model.</summary>
    public const string HudTyping = "Typing";

    /// <summary>The live preview's word budget
    /// (<see cref="DictationPreviewCaption.DefaultWordWindow"/>) as a
    /// reference line: the Mac's <c>recordingTitleReferenceWords</c>.</summary>
    public const string CaptionReferenceWords = "one two three four five six seven";

    /// <summary>The live preview's CJK budget as a reference line: the Mac's
    /// <c>recordingTitleReferenceCJK</c>.</summary>
    public static readonly string CaptionReferenceCjk = new('你', DictationPreviewCaption.DefaultCjkCharacterBudget);

    /// <summary>A correction saved a new word or rewrite, at rest: the Mac's
    /// "Remembered your correction", pointing at the window's receipt and the
    /// notification-area menu's lines, both "in Dictation".</summary>
    public const string HudCorrectionRemembered = "Remembered your correction — review or undo in Earlet";

    // ─── A microphone's name in a sentence ──────────────────────────────

    /// <summary>The longest microphone name a sentence carries, in characters.
    /// Windows names some arrays at seventy characters; the line has to show
    /// the rest of the sentence too.</summary>
    public const int MicrophoneNameLimit = 40;

    /// <summary>
    /// A microphone's name as a sentence may carry it: one line, white space
    /// collapsed, a control character (the name is the driver's) a space, an
    /// em dash a hyphen (the indicator splits its two lines at the first
    /// " — "), and at most <see cref="MicrophoneNameLimit"/> characters with
    /// an ellipsis. Null for a name with nothing to show.
    /// </summary>
    public static string? MicrophoneForSentence(string? microphone)
    {
        if (string.IsNullOrWhiteSpace(microphone))
        {
            return null;
        }

        var printable = new string(microphone.Select(c => char.IsControl(c) ? ' ' : c == '—' ? '-' : c).ToArray());
        var name = string.Join(' ', printable.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Length <= MicrophoneNameLimit)
        {
            return name;
        }

        // Never half a surrogate pair at the cut.
        var keep = MicrophoneNameLimit - 1;
        if (char.IsHighSurrogate(name[keep - 1]))
        {
            keep--;
        }

        return name[..keep].TrimEnd() + "…";
    }

    // ─── Vocabulary: the same strings as the macOS window ───────────────

    public const string VocabularyHeading = "Vocabulary";

    /// <summary>The switch; on unless the user turned it off.</summary>
    public const string LearnFromCorrections = "Learn from my corrections";

    /// <summary>Under the switch: what is watched, for how long, and what is kept.</summary>
    public const string LearnFromCorrectionsDisclosure =
        "In supported text fields, Earlet checks your corrections for up to 90 seconds. "
        + "Useful words are remembered on this computer for future dictations; learning keeps words, not whole sentences.";

    public const string TeachWordPlaceholder = "Word or name";
    public const string TeachSoundsLikePlaceholder = "Sounds like (optional)";
    public const string TeachAdd = "Add";

    public const string TeachEmpty = "Type a word first.";
    public const string TeachTooLong = "That is too long. Use up to 60 characters.";
    public const string TeachSelfContaining = "“Sounds like” cannot be part of the word itself.";
    public const string TeachFull = "The list is full. Remove a word first.";

    public const string VocabularyEmpty = "No words yet. Fix a word Earlet got wrong, or add one above.";
    public const string LearnedFromYourFix = "Learned from your fix";
    public const string AddedByYou = "Added by you";
    public const string RemoveEntry = "Remove";

    /// <summary>While the list shown is not what is on disk: a save failed,
    /// or learning.json could not be read (LEARNING.md §5).</summary>
    public const string VocabularyNotSaved =
        "Your vocabulary isn't saved yet. Earlet keeps using it and will try again.";

    public const string ForgetAll = "Forget all";
    public const string ForgetAllQuestion = "Forget every learned and added word? Earlet will stop using them.";
    public const string ForgetAllCancel = "Cancel";

    // Save receipts and Save again: the Mac's words, with "on this PC" where
    // the Mac says "on this Mac". No line names a word.

    /// <summary>Under the teach row, after an Add that is on disk.</summary>
    public const string TeachSaved = "Saved to your vocabulary on this PC.";

    public const string VocabularyRemoved = "Removed from your vocabulary.";
    public const string VocabularyForgotten = "Your vocabulary is empty.";

    /// <summary>A Remove or Forget all that is in use but not on disk.</summary>
    public const string VocabularyChangeNotSaved =
        "This change isn't saved yet. Earlet keeps using it and will try again.";

    public const string SaveAgain = "Save again";
    public const string SavingAgain = "Saving…";
    public const string SaveAgainFailed = "Your vocabulary still isn't saved. Try again when the file is available.";
    public const string SaveAgainSucceeded = "Changes saved to your vocabulary on this PC.";

    // Correction notices: how the last correction watch went, above the teach
    // row, never naming a word (CorrectionNotices picks one).

    public const string CorrectionChecking = "Checking this field for corrections…";
    public const string CorrectionFieldUnread =
        "The field could not be read. You can add the word manually below.";
    public const string CorrectionTextMoved =
        "The dictated text moved before it could be checked. You can add the word manually below.";
    public const string CorrectionFieldNotChecked =
        "This field was not checked. You can add the word manually below.";
    public const string CorrectionFieldUncheckable =
        "This field could not be checked. You can add the word manually below.";
    public const string CorrectionPending =
        "Correction checked. Another matching correction is needed before it is used.";
    public const string CorrectionAlreadyKnown = "This correction is already in your vocabulary.";
    public const string CorrectionNothingLearned =
        "No vocabulary change was learned. You can add the word manually below.";
    public const string CorrectionNotDetected = "No correction detected.";

    // The learning receipt: the Mac's "Learning from you" section, word for
    // word. Never names what was learned.

    public const string LearningReceiptHeading = "Learning from you";
    public const string LearningRemembered = "Remembered your correction for next time.";
    public const string UndoLearning = "Undo learning";
    public const string DismissLearningReceipt = "Dismiss";

    /// <summary>The Dismiss button's name for a screen reader, which hears it
    /// out of the section it sits in.</summary>
    public const string DismissLearningReceiptName = "Dismiss the learning receipt";
    public const string LearningUndone = "Learning undone. Your previous vocabulary is restored.";
    public const string LearningUndoExpired = "Vocabulary has changed. Review it below to remove a word.";

    /// <summary>An alias row: what the recogniser hears, and what is typed instead.</summary>
    public static string AliasRow(string heard, string meant) => $"{heard} → {meant}";

    /// <summary>Why a word was not added, or null when it was.</summary>
    public static string? ForTeach(TeachResult result) => result switch
    {
        TeachResult.Ok => null,
        TeachResult.Empty => TeachEmpty,
        TeachResult.TooLong => TeachTooLong,
        TeachResult.SelfContaining => TeachSelfContaining,
        TeachResult.Full => TeachFull,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "no sentence for this refusal"),
    };

    public static string Armed(HotkeyBinding key) => $"Enabled. Hold {key.KeyName} in a text field.";

    public static string Instructions(HotkeyBinding key) =>
        $"Speak wherever you type. Hold {key.KeyName}, speak, then release to insert. Double-tap it to keep listening.";

    /// <summary>A hold latched hands-free by a double-tap: the Mac's status
    /// line, with Windows' Enter.</summary>
    public static string Latched(HotkeyBinding key) =>
        $"Dictating. Press {key.KeyName}, Space or Enter to stop.";

    public static string WorkerRestarted(HotkeyBinding key) =>
        $"Speech recognition restarted. Hold {key.KeyName} to dictate.";

    public static string CancelledByShortcut(HotkeyBinding key) =>
        $"Cancelled because {key.KeyName} was used in a shortcut. Nothing was inserted.";

    /// <summary>A press while the last dictation was still being transcribed
    /// or typed: refused, so that the earlier words still land.</summary>
    public static string StillTypingLastDictation(HotkeyBinding key) =>
        $"Still typing the last dictation, so this hold was not recorded. Hold {key.KeyName} again in a moment.";

    public static string LanguageChanged(SpeechLanguage language) =>
        language.Code is null
            ? "Earlet now detects the language you speak."
            : $"Earlet now expects {language.Name}.";

    // ─── Try dictation: the macOS window's words ────────────────────────

    /// <summary>The practice section's header.</summary>
    public const string PracticeHeading = "Try dictation";

    /// <summary>The practice field's placeholder.</summary>
    public const string PracticePlaceholder = "Your practice sentence";

    /// <summary>The practice field's name for a screen reader.</summary>
    public const string PracticeFieldName = "Earlet practice text";

    /// <summary>Under the practice field.</summary>
    public const string PracticeNotSaved = "This practice text is not saved.";

    /// <summary>Above the practice field, naming the hold key.</summary>
    public static string PracticeInstructions(HotkeyBinding key) =>
        $"Click below. Hold {key.KeyName}, say a sentence, then release. Your words should appear here.";

    /// <summary>What the window says while a session is in this state.</summary>
    public static string ForState(DictationState state, HotkeyBinding key) => state switch
    {
        DictationState.Listening => $"Listening… Release {key.KeyName} to insert.",
        DictationState.Transcribing => Transcribing,
        DictationState.Inserting => Inserting,
        _ => Armed(key),
    };

    /// <summary>What the window says about a blocked microphone, or null
    /// when access is not blocked.</summary>
    public static DictationStatus? ForMicrophone(MicrophoneAccess access) => access switch
    {
        MicrophoneAccess.Denied => new DictationStatus(MicrophoneBlocked, OffersMicrophoneSettings: true),
        MicrophoneAccess.DeniedByPolicy => new DictationStatus(MicrophoneBlockedByPolicy),
        _ => null,
    };

    /// <summary>What Check again says when access is still blocked, or null
    /// when it is not.</summary>
    public static DictationStatus? ForMicrophoneStillBlocked(MicrophoneAccess access) => access switch
    {
        MicrophoneAccess.Denied => new DictationStatus(MicrophoneStillBlocked, OffersMicrophoneSettings: true),
        _ => ForMicrophone(access),
    };

    /// <summary>What Pause says while this run last learned that Windows
    /// blocks the microphone: <paramref name="blocked"/> is the blocked
    /// sentence that taught it, whose button it keeps.</summary>
    public static DictationStatus PausedWhileBlocked(DictationStatus blocked) =>
        blocked.OffersMicrophoneSettings
            ? new DictationStatus(PausedMicrophoneBlocked, OffersMicrophoneSettings: true)
            : new DictationStatus(PausedMicrophoneBlockedByPolicy);

    /// <summary>Whether <paramref name="status"/> is one of the blocked-microphone
    /// sentences, which a later look at the privacy switch may make untrue,
    /// with or without the hold key's sentence after it.</summary>
    public static bool IsMicrophoneBlocked(DictationStatus status) =>
        status.Text is MicrophoneBlocked or MicrophoneBlockedByPolicy or MicrophoneStillBlocked
            or PausedMicrophoneBlocked or PausedMicrophoneBlockedByPolicy;

    /// <summary>What the window says when a dictation ends.</summary>
    /// <param name="result">The session's result.</param>
    /// <param name="key">The hold key, named in the retry hints.</param>
    /// <param name="access">
    /// The microphone access probe, asked only when the session says the
    /// microphone was blocked — so the sentence can tell a switch the user can
    /// turn on from a policy they cannot.
    /// </param>
    /// <remarks>
    /// The controller says more than this for three outcomes: a refused paste
    /// whose words it kept on the clipboard or in memory, a recogniser that stopped and is
    /// being restarted, and a cancel that came from a shortcut. These are the
    /// sentences when none of that applies.
    /// </remarks>
    public static DictationStatus ForResult(
        DictationSessionResult result, HotkeyBinding key, Func<MicrophoneAccess>? access = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            DictationOutcome.Inserted => new($"Text inserted. Hold {key.KeyName} for your next sentence."),
            DictationOutcome.ClipboardRestoreFailed =>
                new("Text inserted, but your clipboard could not be put back."),
            DictationOutcome.Cancelled => new("Cancelled. Nothing was inserted."),
            DictationOutcome.MicrophoneBlocked =>
                ForMicrophone(access?.Invoke() ?? MicrophoneAccess.Denied)
                ?? new DictationStatus(MicrophoneBlocked, OffersMicrophoneSettings: true),
            DictationOutcome.MicrophoneUnavailable =>
                new("The microphone would not open. Check that one is connected and not in use, then try again."),
            DictationOutcome.NoAudio =>
                new("The microphone opened but sent no sound. Check the input device in Windows sound settings."),
            DictationOutcome.NoSpeech => new("Nothing was heard. Speak while you hold the key."),
            DictationOutcome.RecognizerUnavailable =>
                new("Speech recognition is not running, so nothing was inserted. " + LogDetails),
            DictationOutcome.RecognizerFailed =>
                new("Speech recognition failed for that sentence. Try again. " + LogDetails),
            DictationOutcome.NoFocusedWindow =>
                new("Nothing was inserted because no window had focus. Click into a text field and try again."),
            DictationOutcome.InsertionRefused =>
                new("The text could not be pasted into that window. Nothing was inserted."),
            DictationOutcome.SecureField => new(result.SecureFieldAtPaste ? SecureFieldAtPaste : SecureFieldAtRelease),
            // HoldTooShort included: this app's microphone tap has no minimum
            // hold (DictationHoldPolicy.TapOptions; a lone quick tap is
            // transcribed, as on the Mac), so it never reports one.
            _ => new("Nothing was inserted. You can try again."),
        };
    }

    // ─── Recent result and Copy last text: the macOS words, Windows keys ─
    //
    // The section, the menu items and the copy endings of docs/design-macos.md's Recent
    // result and Copy last text. Never the words themselves: those are shown
    // only in the window, collapsed under Show text, and never logged.

    public const string RecentTitle = "Last available text";
    public const string RecentTitleRecovery = "Your words are still here";
    public const string RecentDetail = "Ready to copy again.";
    public const string RecentDetailRecovery = "Couldn't type into the field.";
    public const string CopyLastText = "Copy last text";
    public const string CopyingLastText = "Copying…";
    public const string DismissLastText = "Dismiss";

    /// <summary>Dismiss's accessible name: what it does, not just its label.</summary>
    public const string DismissLastTextName = "Discard the last text from Earlet";

    /// <summary>The disclosure the words stay collapsed under.</summary>
    public const string ShowLastText = "Show text";

    /// <summary>The accessible name of the read-only box that holds the words.</summary>
    public const string LastTextName = "Last text";

    public const string RecentFootnote =
        "Only the latest available text is kept, until you dismiss it or quit. It isn't saved to disk.";

    /// <summary>The window's line after Copy last text worked.</summary>
    public const string Copied = "Copied. Press Ctrl+V in a text field.";

    /// <summary>The window's line after Copy last text failed; the words are kept.</summary>
    public const string CopyFailed = "Couldn't copy. Your text is still here; try again.";

    /// <summary>The paste was refused and the clipboard would not take the
    /// words either: they are still held in memory.</summary>
    public const string WordsStillHere =
        "Couldn't type into that app. Your words are still here: choose Copy last text below "
        + "or from the Earlet icon's menu.";

    /// <summary>No window had focus and the clipboard would not take the
    /// words either: they are still held in memory.</summary>
    public const string WordsStillHereNoWindow =
        "No window had focus. Your words are still here: choose Copy last text below "
        + "or from the Earlet icon's menu.";

    /// <summary>The indicator line when the words are kept only in memory.</summary>
    public const string HudWordsStillHere = "Your words are still here — open Earlet and choose Copy last text";

    public const string HudCopied = "Copied — press Ctrl+V in a text field";

    public const string HudCopyFailed = "Couldn't copy — your text is still in Earlet, try again";

    /// <summary>A press refused because Copy last text was still writing.</summary>
    public const string HudStillCopying = "Still copying your last text — hold again";

    /// <summary>The menu item: the time, never the words.</summary>
    public static string CopyLastTextAt(string time) => $"{CopyLastText} · {time}";

    /// <summary>When a result arrived, as the user's culture writes a short time.</summary>
    public static string ShortTime(DateTimeOffset at) =>
        at.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>A press while Copy last text was still writing: refused, like
    /// a press during a dictation, so the copy and a new paste never share
    /// the clipboard.</summary>
    public static string StillCopyingLastText(HotkeyBinding key) =>
        $"Still copying your last text, so this hold was not recorded. Hold {key.KeyName} again in a moment.";
}
