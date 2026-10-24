<!-- English only -->

[← Earlet README](../README.md)

# Earlet UI contract: macOS

The macOS window, menu bar and floating indicator. The scope, the behaviour both
apps share and the review status are in [DESIGN.md](../DESIGN.md); Windows is in
[design-windows.md](design-windows.md).

## Contents

- [macOS: window, menu bar and app shell](#macos-window-menu-bar-and-app-shell)
  - [Window layout](#window-layout)
  - [Look and components](#look-and-components)
  - [Status header and menu bar glyph](#status-header-and-menu-bar-glyph)
  - [Permission setup and recovery](#permission-setup-and-recovery)
  - [Recording, silence and recovery](#recording-silence-and-recovery)
  - [Recent result and Copy last text](#recent-result-and-copy-last-text)
  - [Learning feedback, receipts and Undo](#learning-feedback-receipts-and-undo)
  - [Correction notices, save receipts and Save again](#correction-notices-save-receipts-and-save-again)
  - [Try dictation](#try-dictation)
  - [History](#history)
  - [The menu bar menu](#the-menu-bar-menu)
  - [Launch, login and quit](#launch-login-and-quit)
  - [Diagnostics and the log](#diagnostics-and-the-log)
- [The floating indicator on macOS](#the-floating-indicator-on-macos)

## macOS: window, menu bar and app shell

### Window layout

There is one window (a SwiftUI `Window`, so no New Window and no ⌘N). It opens
at 720 × 600 points with its title bar hidden: the product's rail (the mark
and the name, the four panes, and a line on where the audio stays) runs up
under the traffic lights, beside the selected pane (`DictationWindowPane`).
The rail is 188 points wide and the pane at least 411 points wide and 420
tall, so the window keeps its 600-point minimum. Each pane is a scrolling
page on the window's warm ground, one column at most 680 points wide and
centred in a wider window ([Look and components](#look-and-components)); the
window's title still names the pane, for the Window menu and VoiceOver.
⌘1 to ⌘4 switch panes. Settings… ⌘, in the app menu and Settings… in the
menu bar menu open
or raise the window on Settings, because the preferences live there. Show
Earlet, Finish permission setup… and every time the window comes forward on
its own (a lost grant, a refused key listener, a recording to recover) open it
on Home, and Review vocabulary… on Vocabulary. A Dock click raises it on the
pane it was left on.

Before 2026-10-16 it was System Settings' own pattern: a sidebar list beside
grouped native forms, which made a dictation app read as a preference pane.
It used to be one grouped column, in this order: status, setup, the recent
result, practice, Shortcut, Recording, Language, Vocabulary, General and
Troubleshooting. How to dictate was a footnote under Shortcut, Pause was the
only prominent button while dictation was on, and the vocabulary list (it can
grow to hundreds of rows) pushed General and Troubleshooting out of sight.

**Home**, in this order:

1. The status header
   ([Status header and menu bar glyph](#status-header-and-menu-bar-glyph)).
2. Once setup is done, a card with the mark, the hold key drawn as a key (the
   keycap: "Right ⌘", "Left ⌥", "fn") and "Hold to talk, release to insert"
   over "Double-tap to keep listening · Esc to cancel", with the hold-key
   caution in it. In a narrow window the mark leaves first. VoiceOver reads
   it as one sentence that names the key in words.
3. Permissions, while a grant is missing or a restart is due
   ([Permission setup and recovery](#permission-setup-and-recovery)).
4. The recent result, while one is held: "Your words are still here" after a
   failed paste, otherwise "Last available text"
   ([Recent result and Copy last text](#recent-result-and-copy-last-text)).
5. The saved recording, while one is kept for Recover Last Dictation
   ([Recording, silence and recovery](#recording-silence-and-recovery)).
6. "Learning from you", while a learning receipt is shown
   ([Learning feedback, receipts and Undo](#learning-feedback-receipts-and-undo)).
7. "Last 7 days", once the history holds a dictation: words, dictations,
   speaking time and the pace as the page's figures, the pace labelled "words
   a minute" (shown from ten seconds of speech, a dash before), with Show
   History ([History](#history)); four across, or two by two in a narrow
   window. Each count is worked out when the history changes, not on every
   redraw. The labels wrap rather than clip, and the key's hint breaks at its
   separator.
8. "Try dictation", while dictation is on and every permission is ready
   ([Try dictation](#try-dictation)).

**History** lists the saved dictations ([History](#history)).

**Vocabulary** holds the Vocabulary section
([The Vocabulary section](../DESIGN.md#the-vocabulary-section)) and its
correction notices
([Correction notices, save receipts and Save again](#correction-notices-save-receipts-and-save-again)).
When the vocabulary file could not be opened at launch, the pane says
"Vocabulary is unavailable" and that the log under Troubleshooting says why,
instead of showing an empty list.

**Settings**, in this order, as headed groups of rows (a row's words on the
leading side, its control on the trailing side, or under the words in a
narrow window):

1. Dictating: Hold to talk (the hold-key choice, disabled during a
   dictation, with a how-to line that names the double-tap), the caution,
   Test key, Language (Automatic and ten languages; a change applies at the
   next hold, without restarting the speech engine, and a dictation under
   way keeps its language), and Stop hands-free after silence, with the note
   that a recording runs for up to ten minutes and keeps its captured words
   when the microphone or the speech engine fails. Until 2026-10-16 these
   were three sections, Shortcut, Recording and Language.
2. History: Save dictations, Keep dictations, Keep recordings, what is saved
   ("5 dictations · 96 KB of recordings") beside the group's title, where and
   how it is stored, and Delete All History… ([History](#history)).
3. General: "Open Earlet at login", with a note when macOS refused the
   change or the user must allow it under Login Items.
4. Troubleshooting, a closed disclosure: this copy's version, build and path
   with Show This Copy in Finder, then Check permissions again, Restart
   Earlet and Open Log Folder.
5. The footer, by a lock, explains that no account is needed, that the history
   is encrypted on this Mac and kept as long as the user chooses, and that a
   recording that could not be transcribed is kept for recovery.

The rail marks Home with a dot in the caution colour while it holds something
only the user can finish (`homeNeedsAttention`): a missing grant or a due
restart, whether dictation is on or off; words left after a refused paste; a
recording kept for Recover Last Dictation; or a recording file that could not
be removed. Paused by the user and a learning receipt are not marked: neither
needs anything done. VoiceOver reads the item as "Home, needs your attention".
The confirmation for discarding a long take is asked from the window, on
whichever pane is shown.

### Look and components

The window's look is its own (2026-10-16): the owner found the System
Settings layout old and asked for the product's own brand. Everything below
is drawn by `macos/Sources/DictationApp/DictationDesign.swift` from the
palette in `macos/Sources/DictationAppCore/DictationPalette.swift`, over
native controls, so focus, Full Keyboard Access, VoiceOver and the disabled
state are the platform's.

**Palette** (light / dark). Ground #FBFAF6 / #15140F, a warm paper and a warm
near-black; cards and fields #FFFFFF / #1E1C17 with a hairline #E6E1D6 /
#332F27; ink #1D1A15 / #F3EFE5, secondary #5F594E / #ADA595, hints
#8A8375 / #837C6D; wheat gold #E0A11B / #F0B73A for the mark, the switch's
track and the selected pane's glyph, and #875A06 / #F4C661 for gold words;
green #1E8A4C / #3FCB73 for on, red #D13B2C / #FF5B4D while the microphone is
open, a rust #B4470F / #F2905F for something only the user can finish, and
#B42318 / #FF7B6E for deleting. Increase Contrast deepens the secondary text,
hints, edges and, on the light ground, the gold. Every colour that carries
words reaches 4.5:1 on every ground, and every colour that carries a state
without words 3:1, in all four appearances (`DictationPaletteTests`). The
colours resolve from the view's environment, so a page follows the
appearance with nothing to observe.

**The mark.** A microphone with three pairs of wheat grains cut into its
rounded grille, a cradle and a short foot. `EarletMark` is shared by the
window and the app-icon renderer, so the two assets use the same paths. It
sits beside Earlet at the top of the rail, leads the hold-key card and stands
in for a symbol in empty History. See the [identity guide](brand.md) for the
reproducible icon build.

**Type.** SF Pro Rounded for the status headline (26 points, bold), page
titles (24, bold), group titles (15, semibold) and figures (28, semibold,
monospaced digits); SF Pro for reading at 13, notes at 12 and captions at 11;
times in monospaced digits.

**Shapes and depth.** Rounded rectangles only, one radius per kind: cards 14,
controls 8, chips 6, the switch 7, the keycap 9. No capsule, no ring, no
grey box: a group is a heading and its rows on the page, and a card (white,
or the dark surface, with a hairline) holds only something that stands
alone: the hold key, the permission steps, a result, a saved recording, a
learning receipt. Depth comes from the hairline, not from shadows; the one
glow on a page is the status dot while dictation is on or listening.

**Components.**

- The rail: each pane's glyph and name. Its states are ink, not tiles: the
  selected pane's glyph turns gold and its name full ink and semibold; the
  others are secondary until the pointer is over them.
- Buttons (`DictationButtonStyle`): a gold primary with dark words for the one
  next step, a white secondary with a hairline, a quiet button in gold words,
  and a destructive one in red words. The focus ring follows the shape.
- Row actions in History (`DictationIconButtonStyle`): no frame until the
  pointer is over them; every action is also in the row's context menu.
- Choices (`DictationChoice`): the secondary button with the current value
  and a chevron, opening the native menu, so the keyboard and VoiceOver work
  as for any pop-up.
- Switches (`DictationSwitchStyle`): a gold track when on, a raised one when
  off, and a white knob, drawn so they look the same whatever accent colour
  the user chose and whether the window is active; VoiceOver is given a
  native toggle in their place, and Space flips one.
- Fields: plain native text fields on the surface, with a hairline that turns
  gold while the field has focus; the placeholder keeps the system's colour.
- Disclosures (`DictationDisclosureStyle`): the whole title row is the
  button, its chevron turns, and VoiceOver hears Expanded or Collapsed.
  Reduce Motion drops the turn.
- Notices: a glyph in its tone, the sentence and its action, with no box.
- Empty states: the mark or a gold symbol, a title, what to do, and the
  action, centred in the pane.

**Narrow windows.** Under 420 points of column (the window near its minimum),
a settings row puts its control under its words and a history row moves its
time into the line under the words, so the words keep the width.

### Status header and menu bar glyph

The header is the page's headline: the state's one-line headline, then the
status sentence (with room for two lines, so a longer one does not move the
button under the pointer) beside a dot in the state's colour, and
Enable/Pause, with Cancel dictation beside it while there is something to
cancel. The button is the gold primary, under the sentence, only when it is
the next step: a setup action, or Enable dictation while dictation is off.
Pause dictation is a secondary button at the headline's trailing edge, so
while dictation is on the hold key under the header leads. The dot is green
when on and red while recording, both with the page's one glow; gold while
transcribing; rust for anything to fix; and a quiet ink when off or
starting. The menu bar keeps its own glyph (below). While permission setup is incomplete, and nothing is recording or
busy, the header shows the setup step's title, detail and action instead
([Permission setup and recovery](#permission-setup-and-recovery)).

The menu bar icon has one SF Symbol per state, drawn as a template so the menu
bar tints it, with its state in words for VoiceOver. One pure function resolves
the state for the icon, the menu's first line and the header, so they cannot
disagree. A live dictation outranks everything, then anything the user must
fix, then the quiet states.

| State | Menu bar glyph | Headline |
|---|---|---|
| Off: paused, not yet enabled, or an Enable that failed | `mic.slash` | Earlet is off |
| Starting (Enable is loading the engine) | `mic.slash` | Starting dictation… |
| On and idle | `mic` | Earlet is on (menu: "On — hold Right Command", with the chosen key) |
| Recording, held or hands-free | `mic.fill` | Listening… |
| Transcribing | `waveform` | Transcribing… |
| A grant missing | `exclamationmark.triangle` | Needs permission |
| A restart due | `exclamationmark.triangle` | Reopen Earlet to finish |
| The speech engine failed | `exclamationmark.triangle` | Speech engine unavailable |

The two permission headlines appear in the menu; the window header shows the
setup step instead. The model publishes no engine-failed flag yet, so the last
row is never drawn: a failed Enable shows as off, and the status sentence says
what went wrong.

Windows resolves its notification-area picture and tooltip, and its window
header's glyph and headline, the same way, in the same order, with a warning
triangle for its one grant, the microphone
([Windows: window and tray](design-windows.md#windows-window-and-tray)).

### Permission setup and recovery

The header's action advances one step at a time: Microphone ("Allow
Microphone…"), Accessibility ("Open System Settings…"), Input Monitoring ("Open
Input Monitoring Settings…"), a restart when one is needed ("Restart
Earlet"), then "Enable dictation". Granted steps are skipped. One click
requests one permission, and coming back from System Settings advances to the
next action without another system prompt. While the microphone request is
open the header shows progress, and the button refuses a second click. The
system microphone prompt reads "Earlet records while you hold the shortcut
to type your speech locally." On macOS 27 and later, Accessibility is called
"Device Control and Data Access" in the checklist and the recovery text.

The checklist is read-only progress: a "Permissions · N of 3 allowed" header,
one row per grant (Microphone, Accessibility, Input Monitoring) that says
Allowed or Not allowed, and "Checked again when you come back from System
Settings." with Check again beside it. Check again reports what this running
process can use, without opening a prompt. The poll and app activation also refresh it. The
checklist and the recovery disclosure wrap at the 420-point minimum width.

A restart is offered only after this process's key listener was refused: when
the event tap cannot be created although every probe says the grants are in, or
when Input Monitoring arrives after the refusal. That process cannot use the
grant, so only a fresh one can. Restart Earlet (in the header, the recovery
disclosure and Troubleshooting) cannot interrupt a recording or an insertion,
and an Enable that was waiting for the grants resumes in the new process.

The closed "Already enabled in System Settings?" disclosure explains, in order:
restart first, because a switch can be on before macOS makes it available to
the running app; if still blocked, an older copy may own the permission, so
remove only Earlet with − in Accessibility or Input Monitoring, add the copy
shown with +, enable it and restart; for Microphone, switch Earlet off and
on again. It shows this copy's version, its build (a UTC timestamp the packager
embeds), its path, a warning when the copy is outside Applications, and Show
This Copy in Finder. The app never edits the privacy database (TCC) and never
asserts that another copy exists without detecting one.

The app delegate, not the window, re-reads the grants: every second while
dictation is off, a grant is missing or a restart is due; every 5 s when on with
every grant; and at once when the app becomes active. A closed window therefore
still sees a revoked grant, and the window comes forward on its own, as it does
when the hold key's listener is refused. Each loss asks once, so closing the
window again is respected.

### Recording, silence and recovery

The Mac recording ceiling is 600 seconds. The indicator counts down the last
60 seconds and ends at 10:00. The clock starts when capture is running. A
hands-free hold can stop after 30 or 60 seconds of silence, or never stop on
silence; unless silence stopping is Off, before any speech it stops after 15 seconds, and after speech it
warns for 10 seconds before the selected silence deadline. A held key has a
60-second no-speech stuck guard. Escape after more than 30 seconds asks for
confirmation before discarding the take.

Capture retains complete 16 kHz PCM16 in a private folder (0700 directory,
0600 files), about 32 KB/s. A successful insertion or an available text/clipboard fallback deletes the file. At
most one failed take is retained for up to 7 days, is recovered on the next
launch after a crash, and is available through Recover last dictation and its
clipboard copy path. A microphone fault ends the hold and transcribes the
captured prefix; an unavailable file is retained for another recovery attempt. Failed deletion
is shown separately and retried; cancellation erases the audio before unlinking. One transport or deadline failure is retried exactly once with the same
PCM on a fresh worker. A second failure keeps the take, and hold-health checks
wait for worker startup before deciding that a hold failed.

Capture preferences use `dictation.echoCancellation` (off when unset) and
`dictation.micDeviceUID` (the system input when unset or empty). The capture
layer resolves the selected UID against currently available devices and falls
back to the system input when it is absent. Observation end reasons are the
canonical names in `DictationTerminalCondition`; they are transient feedback
and log classifications, not persisted learning records.

### Recent result and Copy last text

The latest usable final text is held in memory only, so a dictation can be
finished without losing its words. The next usable result replaces it, and
Dismiss or quitting discards it. It is kept both when the text was inserted and
when the paste failed, and can be copied again without another transcription.
A password-field or Secure Input refusal clears it and never exposes its words.
The section title says which case it is: "Last available text" ("Ready to copy
again.") or "Your words are still here" ("Couldn't type into the field."). A
timestamp tells it apart from a newer dictation that was cancelled or failed.
The words stay collapsed under "Show text" until the user opens it, the
disclosure closes again for each new result, and the expanded text scrolls
within 160 points. The section offers Copy last text and Dismiss on Home, and
says where else the words are: "Your history keeps a copy, encrypted on this
Mac." when they were handed to the [history](#history), otherwise "It isn't
saved to disk." (Windows, which keeps no history, always says the second).

The menu bar menu offers "Copy last text · <time>" without showing the words,
with a "Your words are still here" line above it after a failed paste. Copy
uses the same clipboard-safe writer as a refused paste: it waits for any
clipboard restoration to finish, and a press during the copy is refused like a
press during a dictation. The indicator then says "Copied" or "Couldn't copy";
a failed copy says so and keeps the text for another try. Copy writes no
transcript history. Windows keeps the same buffer, in its own window and
notification-area menu, built and tested on Linux only and never run on Windows
([Windows: window and tray](design-windows.md#windows-window-and-tray)).

### Learning feedback, receipts and Undo

The supported terminal composer can have either the original single footer
row or a model/context row followed by navigation or a queue/send hint.
Switching between empty and nonempty drafts must not end observation. A
recognized footer, composer boundary and exact before/after paste proof are
all required; clipped or collapsed drafts remain unreadable for learning.
The terminal accessibility fixture exposes separate text areas for its panes.
The focused Codex pane can use the single-composer adapter, including the narrow
footer fixtures. A shared text area containing multiple panes remains unsupported:
no pane is guessed from its text. These fixtures do not prove attended correction
capture in the currently running terminal.

Automatic learning gets a receipt only when new vocabulary was saved: never for
a word that is only pending, and never when the write failed. The receipt names
no word. The window's "Learning from you" section says "Remembered your
correction for next time." with Undo learning and Dismiss. The menu adds
"Remembered your correction", "Review vocabulary…" (which opens the window) and
"Undo last learning". The indicator shows "Remembered your correction" for 3 s,
but only while dictation is on and nothing is recording or being transcribed, so
learning never interrupts a dictation. The window and menu receipt lasts until
it is dismissed or another vocabulary operation replaces it.

Undo restores the vocabulary exactly as it was before that learning change,
replaced aliases included, and then says "Learning undone. Your previous
vocabulary is restored." Any later change to the vocabulary expires it, so an
undo can never overwrite a newer manual edit; an undo that finds its change
already expired says "Vocabulary has changed. Review it below to remove a
word." A later dictation with no edit is not a vocabulary change and keeps the
receipt and Undo. A failed undo save shows the unsaved notice, and the app
tries a last save when it quits. Windows has the same receipt and Undo in its
window and its notification-area menu, and its indicator says the correction
was remembered
([Windows: window and tray](design-windows.md#windows-window-and-tray)).

### Correction notices, save receipts and Save again

While learning is on, the Vocabulary section reports each correction watch in
its secondary text style, above the teach row, and never with the dictated
words:

- "Checking this field for corrections…" while the watch runs;
- a field that could not be read, text that moved before it could be checked,
  or a field that was not checked (a secure field, another app), each ending
  "You can add the word manually below.";
- a correction that needs a second matching one: "Correction checked. Another
  matching correction is needed before it is used." This comes from that
  dictation's actual pending entries, not from a changed file or a candidate
  count, and it does not claim the word is usable yet;
- a correction already known: "This correction is already in your
  vocabulary.";
- an edit that taught nothing: "No vocabulary change was learned. You can add
  the word manually below.";
- no edit: "No correction detected.";
- a saved new word: the receipt in
  [Learning feedback, receipts and Undo](#learning-feedback-receipts-and-undo).

A stale or abandoned watch cannot replace the notice for a newer dictation.

The Mac observer reports whether the inserted span stayed readable through
its ending and whether it ever changed, without returning surrounding text.
An untouched readable span reports "No correction detected." A change that
was reverted or did not settle reports that nothing was learned. Normal watch
completion preserves the latest revision notice, including when focus moves
to the Earlet window; abandonment clears it. These paths are covered by
automated observer and app-model tests. Attended correction capture remains
a separate device check.

Add, Remove and Forget all confirm a change only when it is cleanly saved:
"Saved to your vocabulary on this Mac." (under the teach row), "Removed from
your vocabulary." or "Your vocabulary is empty.". A Remove or Forget all that
was not saved says "This change isn't saved yet. Earlet keeps using it and
will try again."; an Add that was not saved shows no line of its own. While
anything is unsaved, one warning sits beside Save again: the latest message
from Remove, Forget all or Save again, or else the unsaved notice of
[The Vocabulary section](../DESIGN.md#the-vocabulary-section). Save again reads "Saving…"
and is disabled while it runs. A failed retry says "Your vocabulary still isn't
saved. Try again when the file is available.", a successful one "Changes saved
to your vocabulary on this Mac." Windows says "on this PC" in both. A newer
operation invalidates an older response, and a slow Add response cannot erase
text the user has started typing into the teach row.

A later dictation with no edit leaves these receipts in place. One with an edit
clears a Remove, Forget all or Save again message, and its own correction
notice or learning receipt, if any, is shown instead; the Add line under the
teach row stays until the next Add, Remove or Forget all.

### Try dictation

After Enable, while every permission is ready, a "Try dictation" disclosure
holds a temporary practice field: click it, hold the key, say a sentence,
release, and the words should appear there. It exercises the real shortcut,
recording and insertion path; ordinary typing is never labelled success, and the
practice text is not saved. The disclosure is open until the first successful
insertion, then collapses, but never while the field has focus, so an insertion
never hides its own result. The only setup value that persists is one boolean
(the first dictation completed), not text or a score, and a dictation into the
practice field is not kept in the history either. Windows
has the same field
([Windows: window and tray](design-windows.md#windows-window-and-tray)).

### History

Every dictation whose words reached the user is saved: typed in, put on the
clipboard after a refused paste, kept in Copy last text when the clipboard
failed too, or recovered from a saved recording. With it go its time, its
length, the language the recognizer reported, the app the words went to
(its bundle identifier; the window shows the app's name), how the words were
delivered, and, while recordings are kept, the take's audio. Never saved: a
dictation refused for a password field or Secure Input (its words may be a
password), a spoken command, a cancelled or empty dictation, a dictation into
Earlet's own window (the practice field, or a field in Vocabulary or
History: a test of Earlet, not a delivery; its paste target is the app's
own bundle identifier), and a take that could not be transcribed, until it is
recovered.

**Storage** (`DictationHistoryStore`, `DictationHistorySeal`,
`KeychainHistoryKeys`). `~/Library/Application Support/Dictation/dictation/history`
holds `entries/<id>.sealed` per dictation and `audio/<id>.sealed` per kept
recording (16 kHz mono PCM16, about 1.9 MB a minute); directories 0700,
files 0600, each written whole and renamed into place. Each file is sealed
with AES-256-GCM under one random 256-bit key, with the file's kind, the key's
id and the dictation's id authenticated with it, so a file cannot be passed
off as another dictation's or another kind. The key is made at the first
save and kept only in the login keychain, as a generic password whose
service is the app's defaults domain followed by `.history` (account
`history-key-v1`), not synchronizable. A recording
is read from the take's spool before the spool file is deleted, sealed in
memory and written; it is played back from memory as well, never written out
in the clear.

**Settings.** Save dictations is on by default; off, nothing new is saved and
nothing is deleted. Keep dictations: 7 days, 30 days, 1 year or Forever
(default). Keep recordings: Don't keep, 7 days or 30 days (default), never
longer than the words and 1 GiB at most, the oldest going first. A shorter
choice that would delete anything asks first ("Delete 3 older dictations?").
The retention runs at launch, after a change and at most once an hour while
Earlet runs.

**The pane.** Dictations by day (Today, Yesterday, then the date), newest
first, under the title, how many are saved and the search field (⌘F; Escape
clears it; case and accents ignored). A row shows the
time, the words (up to four lines, selectable), and the app, length,
language and delivery when it was not typed in; then Play (with a
recording), Copy and a menu with Delete…, all also in the row's context menu.
Copy uses Copy last text's clipboard-safe writer. Delete asks first and
deletes the recording with the words. Playback stops when a recording
starts, because speech from the speakers would be typed with the user's
words, and Play is disabled while one runs. Empty, off and locked are each
their own state: "No dictations yet", "History is off" with Turn On History,
and "History is locked" with Try Again.

**Without the key.** When the keychain refuses (locked, or the user denied a
build whose signature changed), the history reads as locked: nothing is
shown, nothing new is saved, and dictation itself carries on. The keychain is
not asked again until Try Again (or the next launch), so its prompt never
follows each dictation, nor each save queued behind a prompt the user
denied. Quitting waits up to 2 s for a save already handed over. When the key
is gone (the keychain item deleted), files sealed under it are counted as
unreadable and offered for deletion, and the next save makes a new key. A
file that cannot be opened just now is neither counted nor deleted, and nor
is a dictation that a newer version of the app saved, which the key opens but
this version cannot read: it is kept with its recording, and the list says
how many there are ("1 dictation was saved by a newer version of Earlet.
It is kept, for that version to show."). Every file is flushed to the disk
before it replaces another, so a crash leaves the old file or the new one.

**Delete All History…** asks, then destroys the key first and deletes the
files, so a copy of them or a block the file system has not reused cannot be
opened; the next save makes a new key. A backup made before (Time Machine, a
local snapshot) holds the login keychain too, so it can still open the history
it holds.

### The menu bar menu

In order:

1. The headline, 40 characters at most, because a menu item does not wrap.
2. While a recent result is held: a separator, "Your words are still here"
   after a failed paste, and "Copy last text · <time>".
3. While a learning receipt is live: a separator, "Remembered your
   correction", "Review vocabulary…" and "Undo last learning".
4. "Enable dictation" or "Pause dictation"; while dictation is off and setup is
   incomplete, "Finish permission setup…", which opens the window.
5. "Cancel dictation", only while there is something to cancel.
6. A separator, "Show Earlet", "Settings… ⌘," and "Open Log Folder".
7. A separator, "Preview Earlet Indicator" and "Quit Earlet ⌘Q".

Windows draws the same items in the same order in its notification-area menu,
without the headline (its icon's tooltip says what the headline says),
Settings…, "Preview Earlet Indicator" or the shortcuts, and with its separators
between groups instead of under the headline
([Windows: window and tray](design-windows.md#windows-window-and-tray)).

### Launch, login and quit

A login launch of an app that was left on, with every grant in, shows no
window, like the Windows tray. The window comes back if that launch's enable
fails, on a Dock click, from the menu or from ⌘,. Every other launch shows it,
because the window is where a missing grant or a first run is explained. A
login launch is recognised from the open-application Apple event; whether an
`SMAppService` login item carries that mark has not been seen on a device, and
if it does not, the window shows as it did before. Packaged builds allow one
running copy ([Components](architecture.md#components)). Quitting
discards the recent result, lets the clipboard settle for up to a second, and
tries a last vocabulary save.

### Diagnostics and the log

An empty recording or one containing only exact digital zeros is an input
fault. The window gives the existing microphone-check guidance and capture
is cooled down once, so the next physical hold opens the input again. The
app does not change the selected device or claim the lost speech was recovered.
Nonzero room tone below the audibility gate keeps the calm "Nothing was heard"
ending. A watchdog that already rebound the microphone owns its recovery and
is not cooled down again by this path.

Before final recognition, the captured PCM must contain at least 90 ms of
energy above the shared 0.004 RMS floor, measured in 10 ms frames. A brief
key click in otherwise quiet audio returns "Nothing was heard" without
recognition or insertion. There is no voiced-fraction floor, so a 120 ms word
inside a long quiet hold survives. Recovery uses the same gate. This blocks
brief transients; it does not classify every kind of background sound as
speech or silence.

The log is `~/Library/Logs/Dictation/dictation.log`, capped in size with two
rotated generations. Open Log Folder is in the menu and in Troubleshooting; if
the folder cannot be opened, the status line names the path. The log records:

- at launch, the version, the build and the path of the running bundle, so the
  installed copy can be told apart from any other;
- for each attempted hold, a start, a release where one was reached, and
  exactly one classified ending from a fixed list (inserted; failed, with its
  stage and reason; command completed; cancelled; shortcut; privacy refused;
  engine unavailable; timed out; microphone changed), with the elapsed,
  release, recognition and paste times in milliseconds, or `n/a` for a stage
  not reached;
- for an ASR response, UTF-16 length and the longest equal-scalar run before
  and after formatting, plus whether formatting changed the text. A second
  run count ignores intervening whitespace. Only counts are retained, never
  characters, words or hashes. A missing response is `n/a`; an empty response
  is zero. The post-format candidate is not a readback of the receiving field,
  and no repetition is automatically removed on the strength of these counts;
- after a refused paste, whether the words reached the clipboard and whether
  they are available in the app;
- after Copy last text, whether the copy succeeded or failed and kept the text
  for another try;
- for an inserted dictation, whether the clipboard came back unchanged (an
  observed result) apart from whether its restore was detached (a prediction);
- learning outcomes, never a word.

The attempt logger accepts no transcript, field content or error payload as
free text.

## The floating indicator on macOS

macOS shows a small capsule at the bottom of the screen while a dictation runs
and for a moment after it ends, whether or not the window is open: the one place
the user sees that dictation is listening and how it ended. It was introduced
during the September development cycle and rebuilt shortly afterward. The
states, endings, panel spec, sizes, glyphs, announcer and gates in this section
are tested data in `macos/Sources/DictationAppCore/` (`DictationHUD.swift`,
`DictationHUDPanel.swift`). The AppKit controller in
`macos/Sources/DictationApp/DictationHUD.swift` applies them and itself holds
the fade timings and the choice of screen; it has no test target.

**States.** Hidden; Listening (held), and Listening hands-free (double-tap
latch), with three level bars; Transcribing… with a spinner; a microphone-change
notice; and an ending. The priority is: mic change, then a live hold, then an
ending, then Transcribing. A visible state stays at least 375 ms before a
different visible state replaces it. Hidden, a mic change and an ending land at
once, and a new hold replaces an ending at once. The hint line under the title
says how to stop: "Release to insert · Esc to cancel" while held; "m:ss · Press
Right Command, Space or Return to stop" while hands-free (keypad Enter also
stops, unnamed); "Stops in 0:NN · …" in the last 60 s before the 600-second
limit; "Esc to cancel" while transcribing. While recording, the title becomes
the live preview of the user's words once there are any (display only, never
inserted, oldest words clipped on one line); `DICTATION_PREVIEW=0` turns the
preview off.

A cold preview waits for the same bounded worker startup used by final
recognition, while keeping only the most recent three seconds of pending
preview audio. It opens during that hold once the engine is ready. Only one
preview job runs at a time; after it finishes, the next waits at least 2 s and
at least three times that job's wall duration. Pending microphone boundaries
coalesce to the latest cut with the earliest start, targeting a preview decoding duty cycle of 25% or less while capture retains complete PCM. Release, cancel and quit
invalidate the run before waiting for startup or shutting down the worker; a
late completion cannot open a stream for an ended hold. Preview cleanup is
bounded, and final recognition keeps its separate full recording and complete
beam.

**Endings.** Every way a dictation ends shows a short title and a one-line
detail that says what to do (`DictationHUDContent.outcome`), then hides itself
after its dwell. An ending never shows the dictated words. A hold cancelled
because the key was part of a shortcut shows no ending: the user meant the
shortcut. The indicator also reports the recovery events and the learning
event (the last rows below) in the same form; Windows has both
([The floating indicator on Windows](design-windows.md#the-floating-indicator-on-windows)).

| Ending | Title | Tone | Dwell (s) |
|---|---|---|---|
| Inserted | Inserted | neutral | 0.6 |
| The 600-second limit ended the hold | Stopped at 10:00 | caution | 3 |
| Paste refused, words kept on the clipboard | Couldn't type here | caution | 4 |
| Paste refused, clipboard failed, words kept in Earlet | Your words are still here | caution | 5 |
| Paste refused, nothing kept anywhere | Couldn't type or copy | caution | 4 |
| Accessibility missing, nothing kept anywhere | Earlet can't type yet | caution | 4.5 |
| Password field | That's a password field | caution | 4.5 |
| Secure Input on | Another app is blocking the keyboard | caution | 4.5 |
| Silence | Nothing heard | neutral | 1.6 |
| Audio without words | No words recognized | neutral | 1.6 |
| No audio from the microphone | No sound from the microphone | caution | 3.5 |
| Microphone access off | Microphone access is off | caution | 4.5 |
| Escape or Cancel | Cancelled | neutral | 1 |
| Engine died, restarting | Speech engine restarting | caution | 3.5 |
| Engine did not start | Speech engine didn't start | caution | 3.5 |
| Engine could not transcribe | That didn't come through | caution | 3.5 |
| Press during the last dictation | Still finishing the last one | caution | 2.5 |
| A spoken command ran | Done | neutral | 1 |
| Copy last text succeeded | Copied | neutral | 2 |
| Copy last text failed | Couldn't copy | caution | 4 |
| Automatic learning saved a new word | Remembered your correction | neutral | 3 |

**Placement.** Horizontally centred on the screen the user is working on, with
the capsule's bottom edge 120 pt above the bottom of that screen's visible frame
(which already excludes the Dock): where the eye already is while text goes into
a document, and clear of the text being typed. The screen is Earlet's own key
window's, else the one under the pointer, else the main screen, recomputed on
every show. The other app's focused window is not read, so on a second display
the indicator follows the pointer, not the caret.

**Panel.** A borderless, non-activating panel that refuses key and main status,
so ordering it front never moves keyboard focus away from the field being
dictated into. It sits at the screen-saver window level, so an open menu or
popover does not cover it; the cost is that it also draws over notification
banners, which is acceptable only because it is small, never takes focus and
lets every click through. It ignores the mouse (no click-to-stop), joins every
Space, shows over full-screen apps, stays put in Mission Control, and is out of
the Window menu. It is built once, with its controller, so a press only orders
an existing panel front. If it ever takes clicks, its level comes down.

**Heartbeat.** Once a second the controller compares the state the model wants
with the panel (`DictationHUDReconcile`). A visible state wanted while the
panel is off screen is offered again; a panel on screen while the model wants
it hidden is ordered out, unless a hide fade younger than a second is still
running (the fade itself takes 0.14 s). Each disagreement is logged, and one
that persists is logged again at most once a minute. A menu preview owns the
panel while it runs and is not checked. The heartbeat was added after a
long-running instance never showed the panel during real holds while a fresh
instance of the same build did; whether it heals that case on a
device has not been seen.

**Size.** Every busy state (held, hands-free, transcribing) is one capsule, 52 pt
high, as wide as the widest line any of them can show: the live-preview budget,
the Transcribing title and every hint, including the hands-free clock and the
countdown at the most digits a 600-second hold reaches. The width is measured
from the fonts at run time, so it depends on the hold key's name.
Nothing moves or resizes between a press, a latch and the release.
An ending or a mic change starts at that width and grows to 420 pt at most,
then wraps.

**Look.** Always dark, whatever the system appearance, so it reads on any
wallpaper: a dark material capsule, a 0.5 pt hairline, one soft shadow (radius
8, 3 pt down, 35% black) inside a transparent 20/16/20/16 pt margin so the
shadow is never cut into a box. Title 13 pt semibold white; hint 11 pt with
tabular digits at 65% white, so a ticking clock keeps its width. The level bars
map the input through a −45 to −8 dB window, so ordinary speech fills half or
more of their height, and silence is a flat line rather than three dots, which
read as "loading". An ending carries an amber triangle (#FBBF24) for a caution
or a small 65% white dot for a neutral ending: never red, and never a
checkmark, because "Nothing heard" and "Cancelled" are not successes.

**Motion.** It enters with a 180 ms fade and an 8 pt rise and leaves with a
140 ms fade. A change of size between states eases over 180 ms, and the bars
glide for 85 ms between level samples (about 12 a second). Reduce Motion keeps
the fades. The system setting drops the rise, the size ease and the glide; the
rise and the size ease are set by the AppKit controller, which reads only that
setting, while the glide also honours SwiftUI's environment value. A fade that
finishes after the indicator was shown again cannot take it down, and states
arrive on the main queue, so the indicator updates while a menu is open.

**VoiceOver.** One constant label per state: "Listening", "Listening,
hands-free", "Transcribing", the mic-change sentence, or an ending's title and
detail; never the live words, the level or the clock. A panel that is never key
is not read on its own, so the app posts the label as an announcement, once per
change, and only for an ending or the mic-change sentence. **Nothing is spoken
while the microphone is open, and that includes Transcribing.** Capture runs
without echo cancellation, and the clip keeps filling after the release until
the transcription stops it (after the engine is ready and the live preview has
stopped, allowed up to 2 s); speech from the speakers in that window would be
typed with the user's words. What this cannot stop: an ending still being spoken
when the user presses again is heard by the next hold.

**Palette.** The indicator takes the window's palette in its dark
appearance ([Look and components](#look-and-components), `DictationBrand`
over `DictationPalette`), because its bezel is always dark: the dark ground
(#15140F) at 96% opacity, so it reads on any wallpaper; level bars in the
mark's gold, deep at the foot (#E09A14) and bright at the tip (#F7CF5A); a
gold hairline at a whisper; the title in the dark ink (#F3EFE5) and the hint
in the dark secondary ink (#ADA595); and a gold spinner while transcribing.
Until 2026-10-16 it used the old app icon's indigo (#25216B) and violet
(#7A4FD4, brightened to #9F7AEA) on the system's frosted material. Amber
(#FBBF24) stays the indicator's only status hue, for a caution ending; an
ending replaces the bars, so the two golds never sit side by side. The app
icon, generated by `macos/tools/generate-icon.swift` and packaged from
`macos/Resources/AppIcon.icns`, is a wheat-gold squircle (#F4C552 to
#DB9213, top to bottom) with the mark in the window's ink (#1D1A15). Both
are part of the owner's open identity decision.

**Preview and render.** Menu → Preview Earlet Indicator cycles six states
(held, hands-free, Transcribing, Inserted, the clipboard ending, a mic change)
for 2 s each, then shows whatever dictation is doing; a real dictation that
starts meanwhile is held back and shown at the end, and only the newest of two
overlapping previews hands the panel back. Any build, release included, run
with `DICTATION_HUD_RENDER_DIR=<dir>` writes every state and every ending to
PNG offscreen, plus a six-frame stability sequence of one recording, and exits,
failing when a shadow is cut or two busy states differ in size.
`DICTATION_WINDOW_RENDER_DIR` writes the window's scenarios the same way. The
render modes are not compiled out of release builds (what a release build
refuses is a `DICTATION_*` resource path outside the bundle).
Device-specific acceptance still needs to cover real microphone input, focused
text insertion and recovery after a failed recognition.
