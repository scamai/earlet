# Earlet UI contract

This is the contract for what the two native Earlet apps show and how they
behave: the macOS SwiftUI window, menu bar and floating indicator, and the
Windows WPF window, notification-area icon and floating indicator. It describes
the current build. When the UI changes, edit the topical section in place; do
not add dated sections.

Device observations and design decisions are kept in the project's development
records. The vocabulary-learning rules and
the learning file are in [LEARNING.md](LEARNING.md); this document covers only
how the apps present them.

## Contents

- [Scope and direction](#scope-and-direction)
- [Behaviour both apps share](#behaviour-both-apps-share)
  - [Hold key, shortcuts and the hands-free latch](#hold-key-shortcuts-and-the-hands-free-latch)
  - [Cancel, refused presses and the recording limit](#cancel-refused-presses-and-the-recording-limit)
  - [Insertion, the clipboard and lost words](#insertion-the-clipboard-and-lost-words)
  - [Long takes, faults and lost recordings](#long-takes-faults-and-lost-recordings)
  - [Formatting and terminals](#formatting-and-terminals)
  - [The Vocabulary section](#the-vocabulary-section)
  - [Failures, UI copy and privacy](#failures-ui-copy-and-privacy)
- [Platform contracts](#platform-contracts)
  - [macOS: window, menu bar, app shell and indicator](docs/design-macos.md)
  - [Windows: window, tray and indicator](docs/design-windows.md)
- [Review status and open decisions](#review-status-and-open-decisions)
  - [What has been checked](#what-has-been-checked)
  - [Not yet verified](#not-yet-verified)
  - [Decided](#decided)
  - [Open, for the owner](#open-for-the-owner)

## Scope and direction

Earlet has two small native entry surfaces for its source preview. Their
rendered review is incomplete
([Review status and open decisions](#review-status-and-open-decisions)). The UI
serves these tasks:

- turn dictation on: grant what the platform needs, and know which installed
  copy is being granted;
- on the Mac, find, copy, replay or delete an earlier dictation, and choose
  how long words and recordings are kept;
- dictate into another app, and see that a dictation is running and how it
  ended;
- pause, or recover from a permission or speech-engine failure;
- finish a dictation without losing its words (on Windows built and tested on
  Linux only, never run on Windows);
- know whether a correction was checked, and what to do when it was not;
- know that a correction was learned, and undo that learning;
- understand and retry a vocabulary save;
- identify a failure, and the installed copy, from the log without the log
  keeping speech.

The two windows no longer share one direction. **The Mac window has its own
look** (2026-10-16): a warm paper ground, ink text and wheat gold, headings and
figures in SF Pro Rounded, the voice-ear mark (seven waveform bars whose tips
draw an ear of wheat), and its own rail, pages, cards, buttons, choices,
fields and switches, all rounded rectangles, over native controls that keep
the platform's focus, keyboard, VoiceOver and disabled behaviour
([Window layout](docs/design-macos.md#window-layout),
[Look and components](docs/design-macos.md#look-and-components)). It replaced
System Settings' sidebar and grouped forms, which made the app read as a
preference pane. The palette and the mark are a proposal for the owner's open
identity decision ([Open, for the owner](#open-for-the-owner)); the product
name in the window is one constant. **The Windows window stays
system-native** and deliberately plain for now: WPF controls, system fonts and
colours, the system appearance, no custom animation, shadow, radius, colour or
font, and status and the shortcut form its hierarchy. The tray icon is drawn in code, in fixed inks chosen
by the taskbar's theme: near-black (#1B1B1B) on a light taskbar, white on a
dark one, red (#E81123) while the microphone is open, and faded while paused;
under High Contrast, in the contrast theme's own colours instead, with the
Mac's outlined, filled and struck-through microphone for ready, listening and
off
([Windows: window and tray](docs/design-windows.md#windows-window-and-tray)).
The Windows window's header draws the same picture in fixed tints, which WPF
has no system colours for: green (#0F7B0F) when on, that red while
the microphone is open, orange (#9D5D00) for a blocked microphone or a hold
key Windows would not reconnect, and system colours otherwise; under High
Contrast it is drawn as the tray icon is, in its shapes and system colours.
The floating
indicator on both platforms has its own capsule, shadow, motion and colours
([macOS](docs/design-macos.md#the-floating-indicator-on-macos),
[Windows](docs/design-windows.md#the-floating-indicator-on-windows)).

Changes evolve each window's direction rather than restart it. On Windows,
the status header, recent result, practice field, learning receipt and
Troubleshooting add no colour (beyond the header glyph's tints above), font,
shadow, radius, animation or component of their own; they reuse WPF's
controls, an `Expander` for disclosures, and its type styles, and keep their
native focus, hover, pressed and disabled behaviour. On the Mac, a new part of
the window is built from the window's components and palette
(`DictationDesign.swift`, `DictationPalette.swift`), not from system form
styles or a colour of its own.

Type and spacing. The Mac window's type ramp is SF Pro Rounded for the
headline (26 points), page titles (24), group titles (15) and figures (28),
and SF Pro at the system's 13 points for reading, 12 for notes and 11 for
captions; times use monospaced digits. Spacing is on a 4-point grid, with 28
points between a page's groups; one radius per kind of thing (cards 14,
controls 8, chips 6, the switch 7, the keycap 9) and no capsules. The Windows
window has no large title: its status header leads with a 28-DIP glyph beside a
20-DIP headline (WPF `FontSize`, about 15 pt); the Recent result title and the
Vocabulary heading are 20, the line that explains the shortcut 16 and the
Recent result's footnote 12; everything else uses the system's default size.
Margins are 4, 8, 16, 24 and 32 DIP.

Benchmarks. All of these are documented flows, not hands-on competitor tests:

- The whole UI. The established hold/release flow and its permission and
  error behaviour are the in-product benchmark. Apple's Human Interface
  Guidelines and the Windows platform adapter supplied the window, menu and
  focus conventions. A browser textarea
  is the counterexample: it cannot establish system-wide input. Native controls
  were chosen over a custom branded window or an embedded web shell because
  input and permission behaviour dominate.
- Mac permission setup (2026-10-06).
  [Apple's privacy guidance](https://developer.apple.com/design/human-interface-guidelines/privacy/)
  and the existing SwiftUI controls supply the skeleton; setup skips granted
  permissions and includes microphone and practice steps.
  [Raycast's setup](https://manual.raycast.com/ai/screen-awareness) resumes
  after an authorization restart. The counterexample was observed in this
  product: an enabled system switch beside an app reporting access missing,
  with Enable still the primary action and no copy identity or recovery.
- Mac window identity (2026-10-16). The design note says the window "is the
  old Mac software layout", should not become "something like Mac settings",
  and needs "our own brand flavour". Three directions were drawn as HTML
  studies of the native window and rendered with WebKit before any code:
  warm paper and wheat gold (chosen), a Swiss black-and-white with one cobalt
  accent, and an industrial recording-hardware panel. The chosen one follows
  the naming idea the owner liked best, a Chinese word that reads both as
  "little mic" and as "wheat", and the owner's stated taste in interfaces:
  no grey boxes, no capsules or rings, navigation states in ink rather than
  tiles, figures before paragraphs. It uses its own visual language rather
  than a corporate dashboard style. The
  counterexample was this window: System Settings' sidebar and grey grouped
  forms.
- Mac window structure (2026-10-16).
  [speakmore](https://github.com/zephyr4123/speakmore), an MIT-licensed
  open-source dictation app for macOS, whose source and screenshots were read
  and not run, separates the daily surface from the settings with a sidebar
  and shows the hotkey as a key. Both were adopted, in native controls. Not
  adopted: cancel and done buttons on its floating bar, which need the bar to
  take clicks (this indicator lets every click through, see
  [Decided](#decided)); its settings drawn as a dialog with a second sidebar
  over the main window, a navigation inside a navigation; and its theme
  gradients and frosted panels, a brand expression whose palette is still the
  owner's decision ([Open, for the owner](#open-for-the-owner)). The
  counterexample was this window itself: one column in which how to dictate
  was a footnote and Pause the only prominent button.
- Mac history (2026-10-16). The same app keeps a searchable history by day
  with Copy, which was adopted; but it stores the words in a plain SQLite
  file and the recordings as plain files, encrypting only its API key. This
  history is sealed instead. Its Home also shows a "time saved" figure
  computed from an assumed typing speed of 40 words a minute, and day
  streaks over an activity heatmap: the first is not a measurement and the
  second counts engagement, not a task, so Home shows only measured counts
  (words, dictations, speaking time and pace) for the last seven days.
- Mac result recovery and learning feedback (2026-10-06). The owner's AlDente
  example motivates a contextual action like
  [Top Up](https://apphousekitchen.com/aldente-overview/features/), and
  [iStat Menus](https://bjango.com/help/istatmenus7/power/) motivates showing
  the information relevant to the current state. The in-product
  counterexamples were a clipboard fallback that is lost after another copy,
  and vocabulary learned without a receipt.

Competitor live-flow captures and brand experiments are deferred to commercial
UX work; no full competitive benchmark is claimed.

## Behaviour both apps share

The two apps implement these rules separately, in Swift and in C#. Where a
platform differs, the difference is stated.

### Hold key, shortcuts and the hands-free latch

Earlet is hold-to-talk: hold the key, speak, release, and the text is typed
at the cursor. The user picks the key from a picker in the window, and the
choice is stored in Earlet's own settings: on macOS in the UserDefaults
suite used by the app (key `dictation.holdKey`), on Windows in
`%LOCALAPPDATA%\Dictation\settings.json`.

- macOS offers seven single modifier keys: Right Command (the default), Right
  Option, Left Option, Right Control, Left Control, Right Shift and fn (Globe).
  Right Command is on Apple keyboards. The picker's last item, "Record a
  Key…", takes the next key pressed over the window instead: a modifier key
  pressed and released on its own (any but Left Command, which is part of
  nearly every shortcut), or a key combination (a key held with Command,
  Option or Control, or F1 to F20 alone; a typing key alone or with Shift
  alone is refused). Escape cancels the recording. A modifier key is heard by
  a listener that subscribes to modifier changes alone. A recorded
  combination is registered with macOS as a hot key, so the app is told about
  that combination and nothing else, and other apps stop receiving it while
  Earlet is on; one macOS will not register (the system or another app holds
  it) is refused when recorded. Other key presses are watched, as key codes and
  never as characters, only from a press until its dictation ends, to tell a
  shortcut, Escape or a latch's stop key apart.
- Windows offers sixteen keys (`DictationHotkeys.HoldChoices`): Right Ctrl
  first and the default, then Right Alt,
  Left Alt, Right Shift, Left Shift, Right Win, Left Ctrl, Caps Lock, Scroll
  Lock and F13 to F19. The `DICTATION_HOLD_KEY` override
  ([docs/development.md](docs/development.md#environment-overrides)) wins over
  the picker, and the window says so.
- Under the picker the window explains keys that have another job (on the
  Mac: Apple keyboards have no Right Control, Option types special characters,
  Right Shift types capitals, and fn can open the emoji picker or macOS
  dictation). Windows derives this advice only from the chosen key and the
  environment override; it does not probe another product's process or mutex.
  The Mac's key check observes recorder and foreground-window reactions to
  identify possible conflicts, without assuming another app's default keys.

If another key goes down while the hold key is held, that recording is
cancelled and nothing is inserted, so Right Command+C or Right Ctrl+C never
dictates; the status line says a shortcut was used. The rule is for modifier
keys, which shortcuts are built on: during the hold of a recorded combination,
as of a mouse button, typing keeps recording and only Escape or Cancel
discards it. The Mac indicator shows no
ending for it (the user meant the shortcut); the Windows indicator says
"Cancelled — that was a shortcut".

On both platforms the same key is also a toggle. A double-tap (two presses
no more than 0.35 s apart, measured press to press) keeps dictation listening
hands-free; the second press takes over the first tap's recording, so no audio
is lost and nothing is transcribed twice. One more press of the hold key, or
Space, Return or keypad Enter, stops and transcribes. While a latch records,
those stop keys are consumed (the press and its release), so they never type
into the focused app or send a half-written chat message. On macOS an event
tap installed only for the latch consumes them; without Input Monitoring the
tap cannot be created, the latch then ends only on the hold key (or Escape),
and Space and Return type normally. Escape, or Cancel in the window or the
menu, discards the recording instead. Once the key is up (a latch, or a short
tap waiting out the double-tap window), typing and shortcuts no longer cancel
anything; only Escape or Cancel does. A hold shorter than the double-tap
window waits it out before transcribing, so the pair's first tap never
inserts on its own, and a lone quick tap is still transcribed: neither app
discards a short hold. Stopping a latch with a double-tap out of habit starts
nothing with the second tap, and the next double-tap still latches after
Escape, the recording limit, a silence stop (Windows) or a refused press.

On Windows the same rules are built into the hold-key hook (`HoldKeyState`,
hands-free mode) and have run only in Linux tests, never on Windows. The stop
keys are Space and Enter (keypad Enter is the same virtual key). The hook
consumes nothing else: it swallows Space or Enter only while a latch records,
with that key's autorepeats, and eats a key-up only if it ate that key's down.
It tracks whether each of the two is physically down, so a Space pressed
before the latch reaches the app whole, autorepeats and release included, and
stops nothing. Every way a hold ends without a key edge (the recording limit,
a silence stop, Cancel, a refused press) also ends the latch, and a latch found with no
dictation behind it is ended at the next 30 ms poll, so a stale latch cannot
keep the microphone open or the stop keys swallowed. The hook times each key
by when it happened (the event's own time stamp), not by when its callback
ran: it runs on the window's thread, which opens the microphone at a press, so
a double-tap made while a slow microphone opens arrives late and is still
measured press to press. A stall longer than Windows' `LowLevelHooksTimeout`
(300 ms by default) is beyond that: Windows passes those keys on without the
hook, or removes the hook, and says nothing. The Mac re-enables its event tap
when macOS disables it; Windows compares its own state of the hold key with the
hook's, finds a removed hook at the next press, puts back a new one and says
so, as the Mac reports a lost key listener
([When the hold key cannot be heard](docs/design-windows.md#when-the-hold-key-cannot-be-heard)).
The hook still runs on the window's thread: one of its own (G20, on
`dictation/gap-wincore-2026-10-01`, not taken) would make removal rarer and
waits for Windows hardware.
Windows needs no permission to consume keys. Its limit is UIPI instead: keys
typed into a window running elevated (as administrator) never reach the hook,
so there neither the hold key nor the stop keys are seen, and a latch running
then ends by Cancel in the notification-area menu or the window, at the
hands-free silence stop, or at the recording limit. While such a window is in front, the notification-area
tooltip and the window's shortcut line say that the hold key cannot be heard.
Cancel in the notification-area menu is the Mac menu's
"Cancel dictation". A Right Alt press on a keyboard layout with AltGr is
typing (Windows sends a Left Ctrl with every AltGr), so it never latches.

Windows differs from the Mac in two ways, both deliberate. It pairs more
strictly: a press used in a shortcut, or one Escape cancelled, is never the
first half of a double-tap. The Mac's detector watches the hold key alone and
forgets a first press only when a pair completes, so by its code (not yet seen
on a Mac) Right Command+C followed within 0.35 s by a Right Command press
latches there. Windows opens the microphone only once the press-time
password-field check answers (usually within tens of milliseconds), so a latch
stopped before that answer recorded nothing and ends silently, where the Mac
transcribes what it caught once capture came up. In both apps Escape is armed
from the press, so Escape before capture is up cancels the press: the Mac
stores the cancel and discards the recording once its capture warm-up ends;
Windows withdraws the press at once, the microphone never opens, and the
window and the indicator say "Cancelled" (built and tested on Linux, never run
on Windows).

A mouse side button can start a dictation too, beside the hold key and never
instead of it. A second picker under the hold key offers no button (the
default), Mouse button 4 (Back) or Mouse button 5 (Forward); left, right and
middle are not offered, because they have everyday jobs a spare side button
does not. On macOS the picker also ends with "Record a Mouse Button…", which
takes the next side or extra button clicked over the window, any from 4 up to
32, for mice with more than two; a middle click is refused with that reason,
and left and right clicks are left alone so the window stays usable. The
choice is saved as `none`, `button4` or `button5` on both platforms (on macOS
under `dictation.mouseButton`, absent for none, and `button6` and up for a
recorded one), and a value either app does not recognise reads as none, so a
hand-edited setting never takes over a button. Hold the button to talk and
release to insert. A click (released within 0.3 s of its press) keeps
dictation listening hands-free like a double-tap of the hold key, and the next
press of the button stops it on the press. On macOS a double-click does the
same as a click: a press within 0.35 s of the click's release is the
double-click's second half and is absorbed, so it never stops what the first
click started; one later press stops. A first try at a hands-free mouse
dictation is often a double-click, as the hold key teaches, and before this
each one started and stopped at once. One click is enough because a spare side button
is pressed for nothing else; the hold key needs two because a modifier is
brushed while typing. The microphone opens on the press either way, so
telling a click from a hold never clips a first word. While a button is
chosen it is consumed: its Back or Forward does not also fire, so a click
cannot navigate away from the field being dictated into. The shortcut rule
is the hold key's alone: typing while the button is held keeps recording, and
only Escape or Cancel discards it.

On macOS an active event tap on the side buttons (`DictationMouseButton`)
does this, installed only while dictation is on and a button is chosen; if
macOS refuses it, Settings says so and the hold key still works. The tap
reports presses and releases, and the app model tells a click from a hold,
so a latch has one record however it began and ended: a mouse latch takes the
same stop keys, Escape, Cancel and recording limit as a key latch, a press of
the hold key stops it too, and after Return ends one the next press of the
button starts a new dictation. Each trigger's release ends only the dictation
it started.

On Windows a `WH_MOUSE_LL` hook does it, installed beside the keyboard hook
only while a button is chosen (`mouse_button` in `settings.json`). It
swallows the button's press and the release of a press it saw; a release
whose press it never saw passes through. The gesture is its own machine
(`MouseHoldOrClickState`), so a mouse latch ends on the next press of the
button, not on Space, Enter or the hold key, which end only the keyboard's
latch; Escape, Cancel, the recording limit and a silence stop end it as they
end any dictation. If Windows refuses the mouse hook, dictation still turns
on with the hold key and the line under the picker says the button does
nothing this run. Built and tested on Linux, never run on Windows.

### Cancel, refused presses and the recording limit

In both apps Escape or Cancel discards a dictation while it is recording or
being transcribed, and nothing is inserted; once the paste has begun it can no
longer be cancelled. In both apps, Escape on a take longer than 30 seconds
asks first, on the indicator ("Discard 3:42 of speech? — press Esc again" on
Windows; "Discard 3:42 of speech?" over "Press Esc again to discard. Keep
talking to continue." on the Mac), and only a second press of Escape within 5
seconds discards (holding the first one down repeats it, which is no second
press); neither app brings its window forward to ask, so the field being
dictated into keeps the keyboard, and the window's and the menu's Cancel never
ask ([Long takes, faults and lost recordings](#long-takes-faults-and-lost-recordings);
[Recording, silence and recovery](docs/design-macos.md#recording-silence-and-recovery)). Pause is disabled while a dictation records, is
transcribed or is typed, in the window and the menu of both apps: Cancel is
what ends one, a slip onto Pause beside it would discard the recording, and
clipboard restoration can finish.

A press while the last dictation is still being transcribed or typed is refused
out loud and records nothing: the status line and the indicator say so, and the
first dictation still lands. Queueing that second hold is not built.

A recording has a limit, and reaching it loses nothing: both apps finish the
recording as if the key had been released, transcribe what was captured and
insert it, and say so with the same sentence pair ("<Length> is the longest
recording, so it stopped there. Transcribing…", then "Text inserted. A
recording stops at <length>; hold <key> again to continue.") and an indicator
ending, "Stopped at m:ss". The limits and silence policy are platform-specific:

- **macOS: 600 seconds.** The indicator counts down the last 60 seconds of any
  hold, held or hands-free, and ends on "Stopped at 10:00". The clock starts
  once capture is running. Hands-free silence can stop after 30 or 60 seconds,
  or be disabled; before speech, hands-free stops after 15 seconds. After
  speech, the selected silence setting warns for 10 seconds before stopping. A
  held key has a 60-second no-speech stuck guard. A microphone fault ends the
  hold and transcribes the captured prefix.
- **Windows: five minutes**, one policy for every number here
  (`DictationHoldPolicy`). The clock starts once the microphone has come up,
  as the Mac's does. A minute before the limit the system's alert sound plays
  once and the indicator counts down the last minute ("Stops in 1:00 · Release
  to insert"), held or hands-free; at 5:00 it ends on "Stopped at 5:00". The
  microphone keeps the hold at 16 kHz as it arrives (32 KB a second, about
  10 MB for five minutes) and stops keeping audio at a memory cap a minute
  past the limit; a hold that reaches the cap, because the window's thread
  stalled past the limit, ends there as at the limit, with everything kept.
  Five minutes, not more, because the worker decodes the whole take only after
  the release, so the wait grows with the length (measured on a test machine,
  small model: 56 s at 5 min); the limit moves to twenty minutes once the worker
  decodes while the user speaks. Five minutes assumes the package ships the
  base or small model. The design keeps 2:00 for turbo, which this build does
  not check, and which model the package ships is not recorded yet; a decode
  that misses its deadline is retried once with a longer one and the take is
  kept if that misses too, so a slow model costs a wait, not the words.

On Windows, silence also ends a hands-free dictation. A 30 ms frame counts as speech when its level is above the larger
of a fixed floor and the quietest tenth of the last 10 s raised by 10 dB, so
the floor follows the room; only a quarter-second run of speech counts, so a
key click or a cough does not keep the microphone open. Silence is timed by
the clock, so a microphone that sends nothing is silence too, though the
capture watchdog ends such a take first, as a fault
([below](#long-takes-faults-and-lost-recordings)). Every stop here ends the
hold as a release does: its words go in, or the take is kept for Recover last
dictation when they cannot be transcribed.

- **Hands-free, after speech:** at 20 s of silence the indicator says "No
  speech for 20 s — stopping in 10 s. Keep talking to continue", counting down
  each second, shown and never spoken, with no sound (the user is probably
  thinking); speaking takes it down. At 30 s the hold is transcribed and
  inserted, the latch ends, and the indicator says "Stopped after 30 s of
  silence — your words went in, double-tap <key> to keep going".
- **Hands-free, nothing heard since the latch:** at 15 s the hold ends as a
  release, as every stop does, so no stop loses words. A quiet room never
  reaches the recogniser (the release's own quiet-room check), and nothing is
  inserted ("Nothing heard in 15 s — stopped, double-tap <key> to try
  again"). Speech too soft for the detector, under 10 dB over a noisy room,
  is transcribed and inserted instead of being lost; the cost is that a noisy
  room nobody spoke into reaches the recogniser, which may type a stock
  phrase such as "Thank you."
- **The choice:** "Silence stops hands-free" in the window, After 30 seconds
  (the default), After 60 seconds (warning at 50 s) or Never, which also turns
  off the 15 s stop; it is remembered, and applies at once.
- **Held:** a held key never stops on silence, because holding it is the user
  meaning to talk. A key held a minute with no speech, most likely stuck or
  weighed down, is let go: its words go in ("Stopped — no speech for a minute
  with <key> held. Your words went in"), and nothing records until the key
  comes up and is held again. The indicator warns over that minute's last
  10 s ("No speech for 50 s — stopping in 10 s. Keep talking to continue"),
  because what is said after the stop is not recorded.

All of the Windows half is built and tested on Linux and has never run on
Windows: the sound has not been heard, and whether the recogniser types
anything for it is not measured. What the microphone hears in the two
seconds after the sound does not restart the silence. Both apps bound the wait for text by the
length of the recording, which the Mac measures as the length of the hold
([Deadlines](docs/architecture.md#worker-protocol-and-lifecycle)).

### Insertion, the clipboard and lost words

Both apps insert through the focused field and the clipboard. Both refuse a
password field at the press, before the microphone opens; the Mac also refuses
while Secure Input is on. Both check again just before the paste: a field that
became a password field while the words were transcribed (on the Mac, Secure
Input too) is not typed into, nothing goes on the clipboard, and the words are
kept nowhere, not even for Copy last text, which drops what it held. Windows
also checks at the release, before recognition, so nothing is transcribed
there. Its check at the paste runs off the thread that watches the hold key,
and Escape still cancels while it runs (built and tested on Linux, never run
on Windows).

When the target app refuses the paste for any other reason, the words go on the
clipboard, and the status line and the indicator say how to paste them. Both
apps also keep the words in Earlet's memory, so if the clipboard cannot take
them either, the status line points to Copy last text (on Home or from
Earlet's menu bar icon on the Mac, which carries a waiting badge while they
are held; the window's section or the notification-area
icon's menu on Windows) and the indicator says "Your words are still here"
([Recent result and Copy last text](docs/design-macos.md#recent-result-and-copy-last-text);
[Windows](docs/design-windows.md#windows-window-and-tray)).

After a paste each app puts back what the clipboard held before, and only if
nobody wrote the clipboard meanwhile (a change count on the Mac, a sequence
number on Windows). Windows restores what a paste uses (Unicode text, pictures
as DIB, file lists, HTML and RTF, but not vector metafiles, Office embedding
formats or private handle formats, nor anything over 256 MiB) and marks
Earlet's own clipboard writes to stay out of Win+V history and cloud sync.
The Mac marks its item as concealed (`org.nspasteboard.ConcealedType`) only;
the transient and current-host-only markers are not set.

### Long takes, faults and lost recordings

The rule both apps are moving to (LIMIT, 2026-10-10): no stop loses words.
Every way a take ends either transcribes and inserts what was captured, or
keeps the recording for Recover last dictation; only Escape and Cancel
discard. Windows has the first stage of it (built and tested on Linux, never
run on Windows), and the Mac has its own stage 1. A microphone fault ends a
Mac hold after retaining its captured prefix, and a failed decode keeps the
take for recovery. On the Mac a decode that comes back empty is the next step
toward the rule: when one of the worker's recognition gates emptied it (its
language-probability gate, or its junk filter or another filter on a take
whose peak reaches 0.1), the ending says so and the take is kept; Recover last
dictation then decodes it without those gates, in the current language, and
its words are copied, never typed. A take too quiet for the worker's silence
check, a quieter one a filter emptied, or one emptied with no reason given
still keeps nothing ([Recording, silence and
recovery](docs/design-macos.md#recording-silence-and-recovery)). Windows still
deletes a take that held nothing worth typing.
The Mac now follows the same stage 1 recovery contract; Windows retains its
platform-specific implementation below. On Windows ([Long takes are never
lost](docs/design-windows.md#long-takes-are-never-lost)):

- **Every take is spooled.** While a take records, its audio is written to
  `%LOCALAPPDATA%\Dictation\recovery` as 16 kHz 16-bit mono (32 KB a
  second), and deleted once its words went in, or once it is cancelled or
  found to hold nothing. A take that could not be transcribed is kept, one at
  a time, for at most 7 days; a take a crash or a quit left is offered at the
  next launch, which brings the window forward even at sign-in. The window's
  Saved recording section and the menu's Recover last dictation transcribe it
  again; its words go to Last available text and the clipboard, never pasted,
  and the recording goes once they are on the clipboard (or dismissed), not
  before: a dictation that finishes first replaces them in Last available
  text. A file another program holds open is never read as damaged.
- **A fault ends the take and keeps it.** A microphone that is unplugged,
  whose stream Windows stops with an error, or that delivers nothing for 0.3
  s (after the Mac's grace: 0.8 s, and 2.5 s for the first buffer, counted
  from when the capture is up) ends the
  take as a release would; what it captured is transcribed and inserted, and
  the ending says "The microphone stopped at 4:12". During a take longer than
  30 seconds the speech worker is asked every 10 seconds whether it is alive
  and started again while the user speaks; when two restarts fail, the take
  ends there, and its release tries the worker once more: its words go in if
  it starts, and the take is kept if it does not.
- **A lost decode is retried once.** A release whose worker dies or misses
  its deadline (30 s plus the length) starts it again and tries once more
  with 30 s plus twice the length; a second failure keeps the take.

On macOS, capture is retained as complete 16 kHz PCM16 in a private 0700
folder with 0600 files (about 32 KB/s). Successful final decode deletes the
file. At most one failed take remains for up to 7 days, is recovered after a
crash, and is exposed only through Recover last dictation and clipboard copy.
A microphone fault transcribes the captured prefix; cleanup and file-write
retries preserve a readable failed take. One transport or deadline failure is
retried exactly once with the same PCM on a fresh worker, and hold health waits
for worker startup before deciding that a hold failed.

The platform recording limit, silence stops and stuck-key stop
([above](#cancel-refused-presses-and-the-recording-limit)) end a take the same
way: as a release, so its words go in or, when they cannot be transcribed, the
take is kept.

### Formatting and terminals

Both apps apply the same light formatting to inserted text: a capital at the
start of each sentence, and a trailing ellipsis removed. A word that already has
a capital after its first letter (iPhone, macOS, eBay), a taught word included,
keeps its own casing. Terminals get no formatting at all, so a shell command is
never capitalised. Each app matches a fixed list: on macOS by bundle identifier
(Terminal, iTerm2, Ghostty, Warp, kitty, Alacritty, WezTerm and Hyper), on
Windows by process name, ignoring case (Windows Terminal and its `wt`
launcher, `powershell` and `pwsh`, `cmd` and `conhost`, mintty, Alacritty,
WezTerm and Hyper). A terminal inside another app, such as VS Code's, is still
formatted. There is no formatting switch and no per-app exception; Windows
carries a dormant exception list that the app does not pass.

The formatter and learning scene classifier maintain separate terminal lists.
A terminal recognized only by the learning classifier still receives formatting
([LEARNING.md §4.7](LEARNING.md#47-scene-relevance)).

### The Vocabulary section

Both windows have a Vocabulary section with the same strings, word for word
(`DictationVocabularyCopy` in Swift, `DictationCopy` in C#; change them on both
platforms or not at all). The rules behind it are in [LEARNING.md](LEARNING.md).
In order:

1. The heading, "Vocabulary".
2. The "Learn from my corrections" switch, on by default, and its two-sentence
   disclosure: in supported fields Earlet checks corrections for up to 90
   seconds and remembers useful words on this computer, and learning keeps
   words, not whole sentences.
3. While the switch is on, the correction notice: how the last correction
   watch went, in the disclosure's secondary style and never with a dictated
   word
   ([Correction notices, save receipts and Save again](docs/design-macos.md#correction-notices-save-receipts-and-save-again)).
4. The teach row: a word, an optional "Often misheard as" and Add. The
   second field takes the text Earlet writes for the word now, which it
   rewrites to the word from then on (a "heard → meant" row), not a
   pronunciation; the line under the row says so in the disclosure's
   secondary style: "Optional: how Earlet writes it now, like “get hub” for
   GitHub. Earlet will write the word instead." A refusal sentence follows
   when the word is empty or too long, the misheard form is part of the word
   itself ("The misheard form can't be part of the word itself."), or the
   list is full.
5. While anything is not on disk, the notice "Your vocabulary isn't saved yet.
   Earlet keeps using it and will try again."
   ([LEARNING.md §5](LEARNING.md#5-the-file-dictationlearning1)), or the
   latest Remove, Forget all or Save again sentence that says so, with Save
   again; once everything is saved, that change's receipt
   ([Correction notices, save receipts and Save again](docs/design-macos.md#correction-notices-save-receipts-and-save-again)).
6. The list: each word with a label, each rewrite as "heard → meant", and a
   Remove button on every row. A word the user added says "Added by you". A
   word learned from corrections says Tentative, Confirmed or Established by its
   net evidence (1, 2 to 3, 4 or more;
   [LEARNING.md §4.6](LEARNING.md#46-reversible-evidence-and-confidence)); an
   older learned row without that evidence says "Learned from your fix". A row
   whose origin a newer version wrote shows no label.
7. Forget all, behind a confirmation. It empties everything, the corrections
   waiting for a second dictation and the memory of removed words included
   ([LEARNING.md §4.4](LEARNING.md#44-removing)). On the Mac it therefore stays
   enabled with an empty list and confirms in a dialog; on Windows it is
   enabled while anything is listed or pending, and confirms inline.

Only these Vocabulary strings, the correction notices, the save receipts and
the learning receipt included, are shared (Windows says "on this PC" where the
Mac says "on this Mac"); the rest of the status and indicator copy is not, and
the two platforms word some endings differently. Both have the save receipts
and Save again
([Correction notices, save receipts and Save again](docs/design-macos.md#correction-notices-save-receipts-and-save-again))
and the learning receipt with Undo. The Mac also hides the section when the
learning store could not be opened; Windows does not. How Windows reaches its
notices, its saves and its learning receipt is in
[Windows: window and tray](docs/design-windows.md#windows-window-and-tray).

### Failures, UI copy and privacy

First use on the Mac goes through the permission checklist
([Permission setup and recovery](docs/design-macos.md#permission-setup-and-recovery)); on Windows
the retained microphone probe tells a user's privacy switch apart from a policy
block ([Windows: window and tray](docs/design-windows.md#windows-window-and-tray)). Missing resources
and recognition errors are visible and retryable.

No protocol field or internal detail in normal UI copy: no status, error or
indicator line shows a source path, protocol field, enum or exception name,
worker error text, model benchmark or deployment internals, or names another
product. The hold-key caution reports observed OS activity generically. Raw
failure detail belongs in the log, and the status line gives a fixed sentence
for each failure. No ending, notice, receipt or menu item shows the dictated
words. They appear only in the indicator's live preview while recording (on
the Mac unless `DICTATION_PREVIEW=0`, on Windows only with
`DICTATION_PREVIEW=1`), in each app's recent-result section, collapsed
until the user opens it, and in the Mac's History pane.

The live preview keeps dictated greetings and sign-offs such as "Thank you."
under the same rule as the final transcription. Meeting-style suppression of
stock phrases does not apply to explicit dictation; decoder-loop and
non-speech-annotation guards still do. Preview words remain temporary until
the full dictation has been transcribed.

The Mac keeps a history of delivered dictations, their words and, for a time
the user chooses, their recordings, sealed on the Mac under a key in the login
keychain ([History](docs/design-macos.md#history)); Windows keeps no
transcript history yet. Each app holds its latest usable result in memory for
Copy last text. The Mac spools complete PCM16 audio privately while it
records and deletes a successful take's spool file once its words are in (a
sealed copy goes to the history first when recordings are kept), keeping only
one failed take. Windows
writes a take's audio to disk while it records and deletes it once its words went in, keeping
only the last take that could not be transcribed, or that a crash or quit
left, for at most 7 days
([Long takes, faults and lost recordings](#long-takes-faults-and-lost-recordings)). Both logs record outcomes and timings,
never words ([macOS](docs/design-macos.md#diagnostics-and-the-log),
[Windows](docs/design-windows.md#diagnostics-and-the-log-on-windows)). What the
learning file keeps is in [LEARNING.md](LEARNING.md#5-the-file-dictationlearning1).

## Platform contracts

Each app's window, menu bar or tray, and floating indicator are specified
in its own file:
[docs/design-macos.md](docs/design-macos.md) and
[docs/design-windows.md](docs/design-windows.md). The rules in
[Behaviour both apps share](#behaviour-both-apps-share) apply to both.

## Review status and open decisions

### What has been checked

Builds, automated tests and bundle layout are covered by the
[documented test commands](docs/development.md#running-the-tests). Of the UI itself:

- **Offscreen renders.** The macOS window render mode draws 29 scenarios (first
  run, each permission step and the recovery disclosure, ready, returning user
  with a history to count, paused, recording, the vocabulary and learning
  states, the recent result and its recovery and copy states, missing
  resources, Settings and Troubleshooting, and History populated, empty, off
  and locked), each on the pane that shows it, at 600, 720 and 960 points, in
  light and dark. The indicator render mode draws every
  state and ending ([Preview and render](docs/design-macos.md#the-floating-indicator-on-macos)).
  Samples of the latest renders were inspected for clipping and overflow.
  Renders establish layout only, not microphone, input or permission
  behaviour. The menu bar menu has never been rendered. Windows has the same
  two render modes, built and tested on Linux on 2026-10-09 (PDT) but never run
  ([Render modes on Windows](docs/design-windows.md#render-modes-on-windows)),
  so no Windows window, notification-area menu or indicator has been rendered;
  only the tray icon bitmaps, drawn from `TrayIconArt` on Linux.
- **Live, on a development Mac**, on installed signed builds since
  2026-10-06 (build details are kept in the development record): the window
  reached the enabled state (then labeled "Dictation is on") with the existing grants and Right Option; the
  permission setup, Check again and recovery screens were used; real holds
  inserted text across several attended runs;
  after the first, the recent-result section appeared and Copy last text
  pasted it; the recent text expanded and the practice disclosure collapsed by
  keyboard; and the launch log named the running build and path. The indicator
  has been seen live only in passing, in the field defect that led to the
  [heartbeat](docs/design-macos.md#the-floating-indicator-on-macos). No ending, Reduce Motion
  behaviour or VoiceOver announcement has been checked live. The default hold
  key, Right Command, has not been used on hardware; only Right Option, the
  selected test key.
- **Windows:** nothing has run on Windows hardware.

### Not yet verified

Rendered and interactive review remains UNVERIFIED for keyboard navigation
(beyond the two disclosures named in
[What has been checked](#what-has-been-checked)), VoiceOver and Narrator (the
Mac announcements are posted but have not been heard), permission recovery
beyond the development Mac, contrast, increased and high contrast, scaling,
small and large windows, light and dark on a live screen, and long error text.
These need an attended native session. The Mac window's palette is held to
contrast by tests (words 4.5:1 and state marks 3:1 on every ground, in light,
dark and Increase Contrast); rendered pixels, the system-drawn menus and
placeholders, and the Windows window have not been measured. No
review has counted the defects that block use or leave the next step unclear;
users' completion times and error rates are unmeasured too. The design lint
(`design-lint source`) reads only web source such as CSS, HTML and JSX, so it
found nothing to check in the SwiftUI sources; that is not a pass. No claim is
made that the UI is free of defects or that a capture, fix and re-capture
review was carried through to the end. A Mac pane and the Windows window are
at least 411 points and 420 DIPs wide (the Mac rail adds 189 points), and
both windows scroll. Mobile layouts are not in scope.

Known design debt: the Windows indicator's violet and orange tone dots have not
been reconciled with the Mac's white dot and amber triangle, and its level
bars and hairline still draw the violet the Mac indicator replaced with the
window's gold on 2026-10-16.

Before commercial UI acceptance: render both surfaces at minimum size and at
their default and wide sizes (the Mac at 600, 720 and 960 points; Windows at
420, 520 and 768 DIPs), walk the tasks in [Scope and direction](#scope-and-direction),
check system appearance and accessibility, and fix every defect that blocks
use or leaves the next step unclear.

### Decided

- The Windows notification-area icon (2026-09-28), and its menu with the Mac
  menu's items and order, Open log folder included (2026-10-09).
- A bottom-centre floating indicator on both platforms: macOS first shipped on
  2026-09-29 and rebuilt on 2026-09-30; Windows built on 2026-09-30, never
  seen. An indicator near the cursor was not built.
- The Mac indicator sits at screen-saver level and lets every click through,
  so there is no click-to-stop; neither indicator nor the Windows main window
  speaks while the microphone is open.
- Lost words are recovered from memory and the native menu (2026-10-06). An
  interactive indicator at screen-saver level was rejected: recovering words
  does not need to intercept clicks meant for the user's document. A
  persistent transcript history was rejected then too; on 2026-10-16 the
  owner reversed that for the Mac, on the condition that it is stored
  securely, and because learning needs past dictations to judge by.
- The Mac keeps an encrypted history (2026-10-16): every delivered dictation's
  words, and its recording for 30 days by default (7 days or none; 1 GiB at
  most), sealed with AES-256-GCM under a 256-bit key kept only in the login
  keychain; words are kept forever by default (7 days, 30 days or a year).
  Hashing was considered and rejected: a salted hash cannot be read back, so
  it can neither show a history nor give learning anything to learn from.
  Password fields, Secure Input, spoken commands, cancelled dictations and
  dictations into Earlet's own window (its practice field) are never
  saved. Windows has no history yet.
- The Mac window stays closed on a ready login launch (2026-09-30).
- The Windows hold-key hook consumes Space and Enter, and nothing else, while
  a hands-free latch records (2026-10-09), so Windows keeps the Mac's stop
  keys.

### Open, for the owner

- The product's identity: its name; whether the Mac window keeps the wheat
  palette and the voice-ear mark proposed on 2026-10-16
  ([Look and components](docs/design-macos.md#look-and-components)), which the
  Mac indicator and the app icon share
  ([Palette](docs/design-macos.md#the-floating-indicator-on-macos)).
- Whether the indicator shows the user's spoken words by default, on a surface
  that also appears on shared screens. Today the Mac live preview is on, and
  only `DICTATION_PREVIEW=0` turns it off; the Windows preview is off, and
  only `DICTATION_PREVIEW=1` turns it on, until a run on Windows hardware
  with the shipped model says what it costs.
  Neither window has a setting for it.
