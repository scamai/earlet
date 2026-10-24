<!-- English only -->

[← Earlet README](../README.md)

# Earlet UI contract: Windows

The Windows window, tray and floating indicator. The scope, the behaviour both
apps share and the review status are in [DESIGN.md](../DESIGN.md); macOS is in
[design-macos.md](design-macos.md).

## Contents

- [Windows: window and tray](#windows-window-and-tray)
  - [Password fields and Escape before the check](#password-fields-and-escape-before-the-check)
  - [Long takes are never lost](#long-takes-are-never-lost)
  - [When the hold key cannot be heard](#when-the-hold-key-cannot-be-heard)
  - [Diagnostics and the log on Windows](#diagnostics-and-the-log-on-windows)
  - [Render modes on Windows](#render-modes-on-windows)
  - [How long a hold listens on Windows](#how-long-a-hold-listens-on-windows)
- [The floating indicator on Windows](#the-floating-indicator-on-windows)

## Windows: window and tray

Windows uses one native content column, 520 × 600 DIP at first and at least
420 × 360, which scrolls when the window is short. Its sections follow the
Mac's order ([Window layout](design-macos.md#window-layout)), though the rows
of the shortcut part do not: the Mac has the hold-key picker, its caution, the
how-to line, then Language as a section of its own, where Windows has the
how-to line, the pickers each beside its label (hold key, language, and the
hands-free silence stop the Mac has not got), then the caution. In order: the
status header (below);
the Saved recording section, while a take is kept
([Long takes are never lost](#long-takes-are-never-lost));
the Recent result section, while one is held (below); "Learning from you",
while a learning receipt is shown; "Try dictation", while dictation is on and
Windows is not blocking the microphone; a line that explains the shortcut
("… Hold Right Ctrl, speak, then release to insert. Double-tap it to keep
listening.", the Mac's words, with "Earlet can't hear Right Ctrl while an
administrator window is in front." after it while one is;
[When the hold key cannot be heard](#when-the-hold-key-cannot-be-heard)); the
hold-key and language pickers
(language: Automatic first, then the Mac's ten, each also named in its own
language; a change applies at the next hold without restarting the speech
engine, as on the Mac. Unlike the Mac, whose picker stays live and fixes the
language when a hold starts, the Windows picker is disabled during a dictation
and while the speech engine restarts after a crash: Windows reads the language
when a hold's decode is sent, so the disabled picker is what keeps a dictation
on the language it began with); "Silence stops hands-free", a third picker
under them (After 30 seconds, After 60 seconds, Never; live at any time,
since the hold policy reads it every tick;
[How long a hold listens](#how-long-a-hold-listens-on-windows)), which the Mac
has not got; the hold-key notice, which joins whichever apply
of: that `DICTATION_HOLD_KEY` sets the key and the key's own caution; the Vocabulary section
([The Vocabulary section](../DESIGN.md#the-vocabulary-section)); "Start
Earlet when I sign in" with its note (below); Troubleshooting, closed
(below); and the footer, last. Buttons keep the OS focus, pressed and disabled
states. The order and the header were built on 2026-10-09 (PDT) and are tested on
Linux only; no one has seen this window on Windows.

**The status header** is the Mac's
([Status header and menu bar glyph](design-macos.md#status-header-and-menu-bar-glyph)):
the notification-area icon's picture as a 28-DIP glyph beside a one-line
headline, then the status line (room for two lines), the microphone actions
while Windows blocks the microphone, and one Enable/Pause button with Cancel
dictation beside it only while a dictation listens or transcribes. The title
bar says "Earlet", so the window no longer repeats it as a large title.
The glyph and the headline come from the same `DictationTrayGlyph.State` that
the icon's picture and tooltip are drawn from in the same render, with the
hook watch's and the window in front's state as required inputs, so the
window, the icon and the menu cannot disagree, in the icon's precedence
(below), each headline saying what that state's tooltip says: "Listening…",
"Transcribing…" and Windows' "Typing…" while a dictation is under way;
"Microphone blocked" (the Mac's "Needs permission", for Windows' one grant)
whenever none is and this run last learned that Windows blocks the
microphone, on or off; while on, "Can't hear the hold key" when Windows
would not let the hook be put back, "Can't hear keys in administrator
windows" while one is in front, and "Reconnected — hold the key again" while
a put-back hook waits for its first key
([When the hold key cannot be heard](#when-the-hold-key-cannot-be-heard));
then "Earlet is on", "Starting dictation…" and "Earlet is off" (paused,
never enabled, or an Enable that failed, whose status line says why). The
glyph is the icon's own picture (`TrayIconArt.Mask`: the microphone, the
waveform or the warning triangle) drawn through a tint, the Mac header's:
green (#0F7B0F) when on, an administrator window and a reconnected hook
included, as their picture is the ready one; the icon's red (#E81123) while
the microphone is open; the system highlight colour while the words are
transcribed or typed; orange (#9D5D00) for a blocked microphone or a hook
Windows would not put back; and the grey text colour when off or starting.
In High Contrast it is drawn as the icon is there: the Mac's three
microphone shapes (`mic.slash`, `mic`, `mic.fill`), since the theme's colours
may coincide, in the system colour the icon's picture takes
(`TrayIconArt.InkOf`: the text colour, the selection colour while the
microphone is open, the disabled-text colour while off or starting), as a
resource reference that follows another contrast theme at once, and redrawn
when High Contrast is turned on or off. Against the white window the three
fixed tints compute to at least 4.6:1; nothing has been
measured on a screen. The glyph is decorative: the headline says the state in
words, and is not a live region, because the status line under it is the
window's voice and both would be read for every change. The button reads
"Enable dictation" or "Pause dictation" (access key E or P) and does what it
said when it was drawn, never the other: Enable turns dictation on, and Pause
goes through the menu's refusal under a dictation (`TryPause`). It is inactive
while an Enable loads the model and while a dictation listens, transcribes or
types, with the rule of the menu's Enable/Pause item, decided once for both
(`DictationWindowHeader`).

The status line under the headline never says the opposite of it. A line
that invites a hold is not drawn under a headline that says a hold would
fail: a new hold key chosen, or the speech engine restarted, while this run
knows Windows blocks the microphone keeps the blocked sentence with its
buttons instead of "Enabled. Hold …" or "Speech recognition restarted. Hold
…", as the Mac's header shows its setup step whenever a grant is missing and
nothing is recording. The microphone's sentence and the hook watch's share
one line, the microphone first, under the headline of whichever outranks
(tested on Linux, `WindowHeaderIntegrationTests`).

A choice made in the window that could not be saved (the hold key, the
language, the hands-free silence stop, "Learn from my corrections") still
applies for this run, and the
status line says it will reset when Earlet restarts. A `settings.json` that
cannot be read or does not parse is never overwritten: the run uses the
defaults and saves nothing until a later launch reads the file, so turning
learning off in such a run lasts only until Earlet restarts. An empty file
counts as missing. Nothing in the window names the file yet.

While the user's own privacy switch is blocking the microphone, the status line
offers "Open microphone settings" and "Check again", which asks Windows once
more (a block by policy offers neither, since clicking would change nothing),
with dictation on or paused (below). A
Check again that gets the same answer hides the status line for a moment and
announces it again, so the press visibly did something. The window also asks
again each time it is activated, and the microphone watch (below) every second
while the block is known, so a fixed switch clears the sentence (and brings
back "Try dictation") without a press.

When a control goes with the keyboard on it (Check again's buttons, Cancel
dictation as its dictation ends, the Recent result section, "Learning from
you", "Try dictation"), the keyboard moves to the window's resting button:
Enable/Pause, when it is live; during a dictation, when it is not, Cancel
dictation beside it; and while the words are typed, when Cancel has gone too,
the Troubleshooting section at the bottom of the window, which is always there
and changes nothing if a key lands on it. It is never handed to an inactive or
collapsed button, which would take nothing. Where WPF puts the keyboard inside
the Troubleshooting Expander, and where it goes when the button it is on turns
inactive (Pause, as a hold starts with the window in front), are WPF's own
choices, as after a click on Enable. None of this has been seen on Windows.

"Try dictation" is the Mac's practice field
([Try dictation](design-macos.md#try-dictation)) in a WPF `Expander` (access key T) after the
buttons and the Recent result section, as on the Mac, with the Mac's words
and the Windows key name: "Click below. Hold Right Ctrl, say a sentence, then
release. Your words should appear here.", a three-to-five-line field with the placeholder "Your practice sentence" (named
"Earlet practice text" for a screen reader) and "This practice text is not
saved." under it. It shows while dictation is on and Windows is not blocking
the microphone, the Windows counterpart of the Mac's "every permission is
ready", and stays up through the practice dictation itself. "Blocking" is what
the app last learned, not what the status line says now: a blocked answer
hides the field until a check finds access allowed (Enable, activating the
window, Check again) or a dictation delivers its words, so a later sentence (a
hold's "Listening…", a new hold key or language) never brings it back while
the microphone is still blocked. Each time it appears it is open until a
dictation has delivered its words (typed, or typed with the clipboard not put
back), and collapsed after that; the first delivery collapses it at once
unless the practice field has the focus (the keyboard, or, while the window is
inactive, the window's own focus that activation gives back), so the words
just typed stay in sight and the keyboard never returns to a hidden field. A
refused paste kept on the clipboard, a cancel, a password field or ordinary
typing never count, and a quit asked for during the first paste still saves
the flag before the window closes. The only value kept is
`completed_first_dictation` in `settings.json`, under the same rule as every
other setting: a file the launch could not read is not replaced, and the flag
then holds for that run only. The practice text is never read beyond its
length (for the placeholder), saved or logged, and goes when the section hides
(Pause, a blocked microphone), as the Mac's section discards its own; closing
the window to the notification area keeps it. When the section hides with the
keyboard in it, the keyboard moves to the window's resting button (above). A fix typed into it is
not learned, because the correction observer refuses Earlet's own process
before it reads the field. While Earlet's own window is in front, the
password-field probe answers "not a password field" without asking UI
Automation (`OwnWindowSecureFieldProbe`): the window has none, and the probe at
release waits on the UI thread for up to 250 ms while WPF would answer UI
Automation for its own window through that same thread, so a practice hold
would most likely stall for the whole deadline (reasoned from the code, never
measured). All of this is built and tested on Linux (`DictationPracticeTests`,
with the WPF half pinned on its source only); no one has seen it on Windows,
and pasting into Earlet's own window, with the keyboard hook on the same UI
thread, has never run.

The window's polite live regions (status, the hold-key notice, the learning
receipt, the correction notice, the teach message, the save receipt, the
unsaved-vocabulary notice; the learning receipt's line is the only voice for
"Remembered your correction" while the window is in front, the indicator then
showing its line unspoken) are announced only
while the window is active, so during a hold into another app only the
indicator speaks, and never while the hold is listening: a hold into the
window's own practice field or teach box keeps the window active while the
microphone is open, and Narrator through the speakers would be dictated.
The text still changes (the status line's "Listening…" and hands-free
"Dictating…" are shown, not spoken); the release announces what follows. No line of "Try
dictation" is a live region. The Forget all question is an assertive region.
While the window itself is active (dictating into its practice field or teach
box, say), both announce the ending, each in its own words. Narrator has not
run on any of it.

**Recent result and Copy last text** (built 2026-10-09, tested on Linux only;
no one has seen it on Windows). The Mac's contract
([Recent result and Copy last text](design-macos.md#recent-result-and-copy-last-text)) with
Windows' keys. One entry is held in memory only (`DictationRecentResult`). The
next inserted or refused result replaces it, held or hands-free, a dictation
stopped at the recording limit or by a silence stop included; Dismiss, quitting and a password field found at the release
(before recognition) or at the paste clear it: a field that became a password
field while the words were transcribed is not typed into, and its words are
held nowhere, as on the Mac ([Password fields](#password-fields-and-escape-before-the-check)). A password field refused at the press, a
cancel, nothing heard and a failure leave it, and its time tells them apart.
Pause keeps it, and nothing expires it. A refused paste's words are held even
when they also reached the clipboard; when the clipboard refused them too, the
status line says "Couldn't type into that app. Your words are still here:
choose Copy last text below or from the Earlet icon's menu." (or "No window
had focus. …") instead of "Nothing was inserted.", and the indicator says
"Your words are still here — open Earlet and choose Copy last text".

The section, after the status header: its title ("Your words are still
here" after a failed paste, otherwise "Last available text"), "Couldn't type
into the field." or "Ready to copy again." with the time in the culture's
short-time format; Copy last text and Dismiss (read as "Discard the last text
from Earlet"); after a copy, "Copied. Press Ctrl+V in a text field." or
"Couldn't copy. Your text is still here; try again."; the words in a read-only
box under a collapsed "Show text", which closes again for each new result
(moving the keyboard, if it was in the box, to Copy last text) and scrolls
within 160 DIP, and which keeps no undo history, so a replaced or dismissed
result does not linger in it; and the footnote "Only the latest available text is
kept, until you dismiss it or quit. It isn't saved to disk." The section adds
no live region: the indicator announces the copy's ending, and a second polite
region would read it twice.

Copy last text writes through the same Win32 writer as the refused-paste
fallback, which keeps its text out of clipboard history and cloud sync, on the
thread pool. It is refused while a hold is listening (a hands-free latch
included), waiting on its password-field check, transcribing or typing, and while dictation is being
enabled; it works while paused. While the write is out the button reads
"Copying…" and keeps the keyboard focus, a second click does nothing, Dismiss
is disabled, and a press of the hold key is refused out loud ("Still copying
your last text, so this hold was not recorded. …"; on the indicator "Still
copying your last text — hold again"), so the copy and a paste never share the
clipboard. A double-tap then is two refused presses, and the refusal ends the
gesture as every refusal does, so no latch is left behind; when the copy ends, the status line goes back to what it said before
the refused press, unless something newer (Pause, say) replaced it. A copy the
clipboard has not answered within 5 seconds counts as failed ("Couldn't copy"),
so a hung clipboard owner cannot keep the hold key refused; its late answer is
ignored. The indicator then says "Copied — press Ctrl+V in a text field"
(2.5 s) or "Couldn't copy — your text is still in Earlet, try again" (5 s),
and a failed copy keeps the text. The log says where a refused paste's words
can still be had and how each copy went, as lines of that dictation's attempt
receipt ([Diagnostics and the log on Windows](#diagnostics-and-the-log-on-windows)),
and a write that throws with the exception's type only, never a word.

The notification-area icon has a picture for each state, the Mac menu bar
glyph's, and a tooltip that carries the state, as Windows icons do, so the
menu has no headline; the tooltip says what the Mac's headline says. One pure
function picks both, and the window header's glyph and headline (above)
(`DictationTrayGlyph`, the Mac's `DictationMenuGlyph.resolve`), in the Mac's
precedence, which lives only there: a dictation under way, then a blocked
microphone, then a hook Windows would not put back (both are the Mac's
needs-permission, whose setup steps put the microphone before the shortcut),
then a window running as administrator in front, then a reconnected hook not
yet heard, then ready, starting and off. The hook watch and the window in
front only report their state, and every pair is tested (`TrayGlyphTests`).

- "Earlet — listening" over the red microphone, and "— transcribing" and
  "— typing" over a waveform. With the live preview on, a hold whose caption
  shows words reads the same: the caption is the indicator's alone, and
  neither the tooltip nor any line of the menu ever names a word.
- "Earlet — Windows is blocking the microphone" over a warning triangle,
  the Mac's needs-permission glyph, whenever no dictation is under way and
  this run last learned that Windows blocks the microphone: dictation on,
  starting or off. The Mac checks its grants before it looks at whether
  dictation is on, so a pause does not hide a microphone that will not open.
- "Earlet — can't hear Right Ctrl, open Earlet" over the same warning,
  while dictation is on, no dictation is under way, the microphone is not
  blocked and Windows would not put back a keyboard hook it removed: the
  Mac's lost key listener, which its glyph shows as needs-permission.
- "Earlet — hold Right Ctrl to dictate", with the hold key named, over the
  microphone in the taskbar's ink; over the same picture, "Earlet — can't
  hear Right Ctrl while an administrator window is in front" while one is,
  and "Earlet — reconnected Right Ctrl, hold it again" from a hook's
  reinstall until the new hook receives its first key (the administrator
  window's while both hold: it explains every hold until it leaves).
- "Earlet — starting…" while an Enable loads the speech model, over the off
  picture: the microphone is not on yet.
- "Earlet — off" over the faded microphone: off, not paused, because as on
  the Mac only a pause is the user's choice.

Under High Contrast (Settings → Accessibility → Contrast themes) the pictures
are drawn in the contrast theme's own colours in place of the taskbar's inks:
its text colour, its selection colour for the open microphone, and its
disabled-text colour, at full strength rather than faded, for off
(`TrayInks.HighContrast`; the Mac's glyph is a template image the system
tints). Those colours are the user's to change and may coincide, and off has
no fade there, so the microphone also takes the Mac's three shapes: an
outlined capsule when ready (`mic`), the filled one while listening
(`mic.fill`) and the outline struck through for off and starting
(`mic.slash`). Every picture is then a shape of its own, the warning, which
a hook Windows would not put back shares, and the waveform included
(`TrayIconContrastTests`). The icon is redrawn when High Contrast goes on or off or the theme
changes. The window header's glyph is drawn the same way, in the same roles
(above). Built and tested on Linux; never seen on a Windows taskbar, and
whether a contrast theme's taskbar is drawn on its window colour, which the
text colour is chosen against, is not checked.

Windows learns that the microphone is blocked from the probe at Enable, a hold
it kept the microphone from, Check again, the recheck when the window is
activated (a left click on the icon does that), and the microphone watch, the
Mac's grant poll (`DictationMicrophoneWatch`, built 2026-10-10 and tested on
Linux only; it has never run on Windows). Windows tells an app nothing when its
microphone switch changes, so the controller reads the consent store again
every 5 seconds while dictation is on, and every second while this run knows
Windows blocks the microphone, on or off (the Mac's 5 s and 1 s); never while
a press is being decided or a dictation listens, transcribes or types, whose
own ending reports a block, and always off the UI thread, which also runs the
keyboard hook. A reading that comes back after the run has moved past it (a
press began, dictation was turned on or off, Check again or the recheck on
activation answered first, or an Enable is loading) is dropped, and the next
one asks again. A switch turned off while dictation is on then puts the warning
on the icon, the blocked sentence and its buttons on the status line (a
hold-key sentence already there stays after it:
[When the hold key cannot be heard](#when-the-hold-key-cannot-be-heard)), and
brings the window forward once: the Mac's edge-not-level rule
(`DictationWindowAttention`). A block that stays never asks again, so a window
the user closes stays closed; access found allowed resets it, so the next loss
asks again; and a block the run already knew is not a loss, whoever learned it
(Enable's own probe, a hold, Check again: each said so in its own way, as the
Mac's first run without grants is not a loss). The switch turned back on takes
the warning away within a second and puts a blocked status line back to what
dictation is doing (a newer line, a language change say, stays); a consent
store that cannot be read changes nothing either way. The warning also goes
when a dictation delivers its words. Unlike the Mac, which polls while off and
shows the warning at a first launch, Windows reads nothing while dictation is
off with no block known: the hold key does nothing then, and Enable asks
first, so a launch with dictation off shows off.
The window says what the icon warns of: a Pause while the block is known says "Paused. Windows
is also blocking the microphone for Earlet. Turn on microphone access for
desktop apps in Privacy & security before you enable dictation." with the
privacy page and Check again under it (a policy block says so, with neither),
as the Mac's header shows its setup step and action while dictation is off and
a grant is missing; access found allowed puts it back to "Paused. The
microphone is off.". An Enable the user cannot see, from the menu, at sign-in,
or one whose window was closed while it loaded, that comes on with the
microphone still blocked brings the window forward with the blocked sentence
and its buttons, as a failed one does: the Mac's menu offers "Finish permission
setup…" there, which opens its window, and its sign-in launch shows the window
while a grant is missing. Windows keeps "Enable dictation" in the menu, because
its Enable is what asks Windows again. Built and tested on Linux only
(`TrayGlyphTests`, `TrayMenuTests`, the pictures' pixels in
`DictationDesktopTests`); no one has seen the warning picture on a Windows
taskbar or the window it brings forward.

**A microphone change during a hold** (built 2026-10-10, tested on Linux only;
it has never run on Windows) ends that hold when the microphone the hold
opened goes away, the Mac's device-gone trigger, and since LIMIT-WIN-A
(2026-10-10) keeps what it captured: the take ends as a release would, its
words up to the change are transcribed and inserted, and the window says "The
microphone stopped at 0:42, so the recording ended there. Your words up to then
went in. The next hold uses <microphone>." (the last sentence only when Windows
names another, active microphone, one line, at most 40 characters), the
indicator "Microphone stopped at 0:42 — your words up to then went in", a
caution for 5 s ([Long takes are never lost](#long-takes-are-never-lost)). The WASAPI capture registers once for Windows' endpoint
notifications (`IMMNotificationClient`, through NAudio's
`MMDeviceNotificationClient`) when the controller arms it, never inside a
press on the UI thread that also runs the keyboard hook; the notifications
are handled on the audio system's thread and the thread pool, never the UI
thread, and each capture only opens and closes the watch. It reports once
per capture when the microphone the capture opened is unplugged, disabled or
removed. A default that moves mid-hold changes nothing, as on the Mac: a
headset plugged in, or "Set as Default Communication Device" (or "Set as
Default Device") on another microphone in Sound settings, leaves the hold
recording from the microphone it opened, the words are typed, and the next
hold opens the new default; the device does not subscribe to Windows'
default-device notification at all. Which notifications concern the capture
is a pure rule (`AudioCaptureEndpointChangeRule`). The listening hold, held or
hands-free, then ends within one poll, a hands-free latch ends, and a key
still held starts nothing until it is pressed again. The take is released like
any other, so it meets the release's password-field check, and its receipt
ends as its words do (`inserted`, say); Windows has no `microphone_changed`
any more. A change once the hold is transcribing concerns nothing (the audio
is captured), and one that reaches the UI thread after its capture closed, or
while the next press is still being decided, is dropped
(`MicrophoneChangeTests`, `IndicatorWatchTests`).

Where this differs from the Mac: the Mac's watchdog (D15) binds a device at
the press and fires only on a dead input (the bound device gone, no audio for
300 ms after a startup grace, a stale stream that also went silent), then
rebinds, records on to the release and discards the whole hold with "Mic
changed to … — could you say that again?". Windows shares the device-gone and
no-audio triggers (the second in [Long takes are never lost](#long-takes-are-never-lost)) and, like the Mac,
never ends a hold because the default moved. It has no rebind, so it stops at
once, and it keeps what was captured instead of discarding it, as the Mac will
in its own stage 1.

The menu is the Mac's
([The menu bar menu](design-macos.md#the-menu-bar-menu)), in the Mac's order, drawn from one
ordered model (`DictationTrayMenu`), with a separator between two groups where
the Mac puts one under its headline:

1. While a take is kept: "Recover last dictation · <time>", which shows no
   words, reads "Recovering…" during a recovery and is inactive while one may
   not start ([Long takes are never lost](#long-takes-are-never-lost)).
   Windows only for now.
2. While a recent result is held: "Your words are still here" (an inactive
   line) after a failed paste, and "Copy last text · <time>", which shows no
   words and is inactive while a copy may not start.
3. While a learning receipt offers Undo: "Remembered your correction" (an
   inactive line, naming no word), "Review vocabulary…", which brings the
   window forward with its Vocabulary section scrolled into view and the
   keyboard in its add-a-word box (the Mac's only opens the window), and
   "Undo last learning", the receipt's Undo learning. Undo, Dismiss, Add,
   Remove, Forget all and switching learning off take the three away; a
   dictation left as it was does not.
4. "Enable dictation" or "Pause dictation", inactive while an Enable loads and
   while a dictation listens, transcribes or types, or Recover last dictation
   transcribes, as the Mac's is while it records or is busy. The window's Enable/Pause button follows the same rule,
   decided once for both (`DictationViewState.CanPause`, `DictationWindowHeader`). Then "Cancel dictation",
   while a dictation is listening or transcribing.
5. "Show Earlet", in bold, which a left click on the icon also does and
   which is the Mac's "Settings…" too (Windows has one window), and "Open log
   folder".
6. "Quit Earlet".

There is no "Preview Earlet Indicator" item (the Mac's, which cycles the
pill through its states; the indicator's
[render mode](#render-modes-on-windows) draws every state instead), no
permission-setup label and no keyboard shortcut in it. Each item makes the same call as the window's own control.
Pause, from either, refuses to discard a dictation that started after it was
drawn (`TryPause`), as the Mac's toggle does while any attempt exists: from the
key-down, while the password-field check is still out (the Mac's privacy gate,
during which its button is live too), and before the window has repainted a
hold the session already admitted. The menu is redrawn in place on
every change, so one left open while a dictation ends or a receipt arrives
changes under the pointer; how WinForms draws that has never been seen.

Closing the window never quits, as closing the Mac's never does: on or off, it
hides to the notification area, so a paused Earlet keeps the recent result
for Copy last text and the learning receipt's Undo, which used to go when a
close while paused quit the app.
The first close says so once per installation, from the icon: "Earlet is
still on. It runs from the notification area; choose Quit there to exit.", or,
while off, "Earlet is off. It stays in the notification area; choose Quit
there to exit." Off, nothing needs the window's 30 ms poll (the hold key,
the level bars, a copy's deadline, a pill on the indicator), so it slows to
once a second until an Enable, a Copy last text or a pill needs it again: a
paused Earlet hidden for days does not wake 33 times a second, while the
indicator's 375 ms, its dwell and its heartbeat and the microphone watch's
reading keep their time (tested on Linux, `WindowPollTests`,
`WindowHeaderIntegrationTests`; the WPF timer has never run on Windows, and
the power saved is not measured). A closed window is watched as an open one:
the hook watch and the microphone watch run on, and their one-time reveals
show a window closed to the notification area, not only a minimized one. Quit
Earlet, in the menu, and Windows signing out exit,
through the same clipboard-safe close. A sign-in launch that turns
dictation back on keeps the window hidden, and shows it only if that enable
fails. One copy runs per user session: a second launch brings the running
window forward, and a start that fails says so, exits and lets go of the
single-instance claim, so the next launch works. The app declares per-monitor
DPI awareness (PerMonitorV2) and long paths in its own manifest. The log is
`%LOCALAPPDATA%\Dictation\logs\dictation.log`; Open log folder, in the window's
Troubleshooting section and the menu, opens it, and when it cannot, the status line names the folder
("The log folder could not be opened. It is %LOCALAPPDATA%\Earlet\logs.")
as the Mac's does, with the variable rather than a path holding the user's
name; from the menu, the window also comes forward to show that line, as it
does for an Enable from the menu that fails. The menu is built and tested on
Linux only (`TrayMenuTests`, the WinForms half pinned on its source); no one
has opened it on Windows.

The Vocabulary section shows the Mac's correction notices
([Correction notices, save receipts and Save again](design-macos.md#correction-notices-save-receipts-and-save-again))
above the teach row, in the disclosure's grey, while "Learn from my
corrections" is on; turning it off clears the notice. The correction watch
reports its start, each settled revision's learning outcome (counts only) and
its end, and `CorrectionNotices` maps the span observer's endings onto the
Mac's sentences:

- no focused field, no text pattern, or the pasted text not found exactly once:
  "The field could not be read.";
- the span's anchor lost: "The dictated text moved before it could be
  checked.";
- a password field, or one that could not be shown not to be; another
  application; a console, which is never watched; Earlet's own window:
  "This field was not checked." A password field that the press itself
  detects is refused before anything is recorded, so it gets no watch and
  the notice stays as it was; the observer meets one only when the press's
  own check could not tell;
- a paste into a process nobody could name (the watch does not even look),
  a document too long to read, or a UI Automation timeout or failure: "This
  field could not be checked.";
- an observation that ran its course (settled, focus moved on, span cleared or
  still changing at 90 s) without a revision for learning: "No correction
  detected." when the text never changed, "No vocabulary change was learned."
  when it changed but nothing settled into a correction.

After a revision, that revision's notice stays when the observation runs its
course and goes when the observer stopped for any other reason. These last two
rules follow the list above and depart from the Mac's running code, which says
"This field could not be checked." for an untouched watch that ran its course
and clears a revision's notice at every end.
A watch still running when the next dictation is released, Pause is chosen or the window is
closed or hidden ends with no notice; a finished watch's notice stays until
the next watch replaces it. Switching learning off, Add, Remove and Forget all
clear any notice, and an event already queued before one of those cannot bring
it back. Add, Remove and Forget all, and the learning receipt's Dismiss, also
end a watch still running, so it learns nothing more: the Mac's model ignores
such a watch's later revisions, and on Windows, where the watch learns on its
own thread, a fix it settled afterwards would otherwise be saved with no
receipt to undo it. A newly learned word clears the notice and shows the learning receipt
instead (below). The list is rebuilt only when its rows change, so a notice
changing does not take keyboard focus off a Remove button. All of this is built
and tested on Linux; it has never been seen on Windows.

The learning receipt is the Mac's
([Learning feedback, receipts and Undo](design-macos.md#learning-feedback-receipts-and-undo)),
word for word, in a "Learning from you" section after the Recent result
section and before "Try dictation", the Mac's order. It appears when a watch reports a
revision that saved a new word or rewrite while the list is all on disk:
"Remembered your correction for next time.", with Undo learning (access key U)
and Dismiss (named "Dismiss the learning receipt" for a screen reader). As on
the Mac, it takes down a Remove, Forget all or Save again line, and an answer
still in flight, an Undo's included, says nothing over it. A
pending pair, a known correction and a refused save get no receipt. The
learning store keeps the vocabulary as it was just before that change (one
earlier state, never a transcript), and any later change expires it: Add,
Remove, Forget all, another learned correction, a waiting pair pruned when it
ages out. A dictation left as it was does not. Undo puts that state back,
replaced aliases and counts included, and says "Learning undone. Your previous
vocabulary is restored." once it is saved; a save the disk refused leaves only
the unsaved-vocabulary notice, with Save again, and the last save at quit
tries again; an undo that finds the vocabulary changed says "Vocabulary has
changed. Review it below to remove a word." and changes nothing. Undo writes
through the same work runner as Add and Remove (below): Undo learning goes at
the click, the line stays until the write answers, and there is no "Undoing…"
state. A later outcome that learned,
saved or failed to save anything, or an edit that taught nothing, takes the
receipt down with any save line, as the Mac's does, and from any watch: one
already past its check still learns on its own thread after the next one
starts, and its refused write must not leave "Remembered…" beside the
unsaved notice. Dismiss, Add, Remove, Forget all and switching learning off clear
it with the notice, and an event queued before them cannot bring it back. A
watch ending, or the next one starting, leaves it; Undo learning does not
end the watch, as on the Mac. Undo about to disappear hands the focus to
Dismiss, and the section to the window's resting button, judged on the window's logical
focus, so a receipt that changes while the user is in another app does not
leave the keyboard on a hidden button when the window is next activated. When
a receipt appears while dictation is on and at rest (nothing held, no press
waiting on its password check, nothing transcribing or typing, no caution
still on screen), the indicator says "Remembered your correction — review or
undo in Earlet"
([The floating indicator on Windows](#the-floating-indicator-on-windows)).
Narrator hears it once: from the indicator while the window is behind, and,
while the window is in front, from the section's polite line, with the
indicator's line shown but not spoken. The notification-area menu offers the
receipt's three lines while Undo is offered (above). The log says whether a
correction can be undone and what an undo did, never a word. Built and tested
on Linux (`LearningUndoTests`, `LearningReceiptTests`,
`LearningReceiptFlowTests`, the window pinned on its source only); never seen
on Windows.

The section's save outcomes are the Mac's
([Correction notices, save receipts and Save again](design-macos.md#correction-notices-save-receipts-and-save-again)),
with "on this PC": the Add line under the teach row; then the Remove, Forget
all or Save again receipt while everything is on disk; otherwise the one
unsaved warning with Save again under it (access key v), which reads
"Saving…" and is disabled until its write answers. Add, Remove, Forget all,
Save again and Undo learning write `learning.json` off the UI thread, where the
keyboard hook runs, one at a time in the order they were clicked, and the
section answers when the write is done:

- Every success line needs the snapshot read after its write to hold
  nothing unsaved. The Remove, Forget all or Save again receipt is then shown
  only while that stays true, and a warning only while something is unsaved,
  so a correction that fails to save a moment later turns "Removed from your
  vocabulary." into the plain unsaved notice rather than leaving it beside
  Save again. The Add line stays until the next Add, Remove or Forget all, as
  on the Mac, even beside a later warning.
- A response says nothing when a newer operation started after its own: Add,
  Remove, Forget all, Save again, Undo learning, the learning receipt's
  Dismiss, switching learning off, a new learning receipt, or a correction
  outcome that changed the file, was rejected or edited the text. That
  outcome counts from any watch raised after the click, because on Windows a
  watch that began before an Add, Remove or Forget all still learns on its
  own thread (the Mac's stops at the click); the correction notice still
  follows only its own watch, and the learning receipt goes with the line. A
  correction watch starting or ending, or a dictation with no edit, changes no
  vocabulary and does not cancel a pending response, just as it leaves a
  shown receipt in place; the Mac's running code also drops a pending one at
  a watch's start or end, and its Undo learning drops none.
- While a change of the section's own is unanswered, a list change does not
  repaint the section; the answer does. So a refused Remove shows "This
  change isn't saved yet…" once, not the plain unsaved notice a moment
  before it, and Narrator reads one line.
- The Add empties the teach row only when the row still holds what was
  added, so text typed while the word was being saved stays, and the keyboard
  goes back to the word field only from the row or the Add button. When Save
  again had the keyboard focus, the save's answer puts it back on the button,
  or on the word field once the button has gone, unless the keyboard has
  moved to another control meanwhile.
- The write itself never runs on the UI thread, but the UI thread can still
  wait on one: each repaint reads the list under the learning lock, which a
  write holds through its flush to disk, so a second click or a repaint that
  lands during a slow write waits for it, as one during a correction watch's
  write always did.
- Quitting waits up to 2 s for a write still running, so a change clicked
  just before Quit is not lost with the process. The quit's last save runs
  first (the window lets the controller go before the section), so when that
  change is still not on disk afterwards, quitting tries the save once more;
  a write still running after the 2 s gets no extra try. Closing the window
  only hides it (above), and waits for nothing.

The Save again outcome is logged as `Save again: file Saved|NotSaved|Unchanged`
and a change that throws as its exception type only; no line names a word.
Built and tested on Linux (`VocabularySaveTests`, with the WPF half pinned on
its source only, and `LearningReceiptTests` for the save lines beside the
learning receipt and its Undo); no one has seen it on Windows.

**Start Earlet when I sign in**, after the Vocabulary section as the Mac's
General section is, is read from Windows at launch and again each time the
window is activated (the Mac re-reads its login item only when its window
appears, which is less often): the user can switch it off in Settings → Apps
→ Startup or Task Manager while Earlet runs. A note under it, in the
secondary grey and a polite live region, says what the box cannot: "Turned
off in Settings → Apps →
Startup. Check the box to turn it back on." while the entry names this copy
and Settings switched it off (checking the box removes that switch); "Windows
did not let Earlet start when you sign in. Try again. For details, choose
Open log folder under Troubleshooting." when checking it was refused; and
"Windows did not remove Earlet from the apps that start when you sign in.
Turn it off in Settings → Apps → Startup." when clearing it was refused. A
change Windows reported written but does not show counts as refused. The box
always shows what Windows has, and the status line keeps what it said (a
refusal used to replace it). A refusal's note stays while a later read finds
the box where the refusal left it, so coming back from the log folder it
points at does not erase it (the Mac's read clears it, but comes only when the
window appears); it goes when the entry changes, and gives way to the Settings
note when Settings has switched the entry off. Built and tested on Linux only
(`LaunchAtLoginTests`); this code has never read the registry on Windows.

**Troubleshooting** is the Mac's closed disclosure, a WPF `Expander` (access
key b) before the footer: "This copy of Earlet", the version and build
("Version 0.1.0 · Build 20261009120000", or "Build not recorded" for a build
that was not packaged) and the executable's path, from the same
`DictationInstallation` the log's launch line writes, the path in a read-only
box that can be selected and copied; then Show this copy in File Explorer
(access key x), which opens Explorer with the file selected and is inactive
when Windows did not report the path, and Open log folder (access key f),
which used to stand alone at the bottom of the window. Explorer is started
by its full path with `/select,"<path>"`, the path in quotes as Explorer
documents it (a path with a space or a comma is one argument).
When the file is no longer at that path, or File Explorer cannot be started,
the status line says "File Explorer could not show this copy. Its path is
under Troubleshooting." and the log says which, naming an exception by its
type only. Once
Explorer has started nothing comes back: a path it cannot select opens a
folder of its own choosing, and the window does not know. The failure
sentences that point at the log say "For details, choose Open log folder
under Troubleshooting." Nothing in it is a live region. It has
no Check permissions again (the status area's Check again is Windows' one
check), no Restart Earlet (Pause, then Enable, starts the speech engine
again) and no warning about a copy outside a folder, which only the Mac's
grants depend on. Built and tested on Linux only (`TroubleshootingTests`);
never seen on Windows.

The footer shares the Mac's account and recording-recovery disclosure.
Both apps spool takes and retain a failed take for recovery
([Long takes are never lost](#long-takes-are-never-lost)), so neither says
that recordings are never saved to disk. Windows then explains its own
close behaviour: "Your audio stays on this device. No account is required.
No transcript history is saved, and a recording only until its words are in,
or for you to recover. Closing this window keeps Earlet in the
notification area; choose Quit Earlet there to exit."

The window has no permission checklist (Windows asks only about the
microphone). Of the Mac's daily-use additions it has the status header, the
recent result with Copy last text, the practice field, the correction
notices, the learning receipt with Undo, the save receipts with Save again,
the sign-in note and Troubleshooting, and in the log the launch line and the
per-attempt receipt
([Diagnostics and the log on Windows](#diagnostics-and-the-log-on-windows)).

### Password fields and Escape before the check

Built on 2026-10-09 (PDT) and tested on Linux only (`FieldCheckTests`,
`DictationSessionPasteFieldCheckTests`); no one has seen it on Windows.
Each hold asks UI Automation whether the focused element is a password field
up to three times, each ask bounded at 250 ms; a timeout or any failure reads
as an ordinary field, so a slow or restricted target never switches dictation
off.

- **At the press**, before the microphone opens, on the thread pool. A
  password field refuses the press: "Earlet does not work in password
  fields. Nothing was recorded." (`privacy_refused`).
- **At the release**, before the recogniser, on the thread that runs the
  keyboard hook, which waits for the answer: "Earlet does not work in
  password fields. Nothing was typed or kept." (`secure_field` at stage
  `capture`). The Mac has no check here.
- **Just before the paste**, the Mac's refusal at the paste. The session asks
  once the words are formatted, while the dictation still reads
  Transcribing, so Escape and Cancel still discard it and a press is still
  refused; the ask runs on the thread pool and the session awaits it, so the
  thread that runs the keyboard hook never waits on UI Automation here. A
  password field is not typed into, the clipboard fallback does not run, the
  held recent result is discarded, and the words are kept nowhere. The status
  line says the Mac's "Earlet does not type into password fields. Nothing
  was kept.", the indicator "Password field — nothing was typed or kept", and
  the receipt ends with the Mac's `result=failed stage=paste
  reason=secure_field`, with the check's time as `paste_ms` (the Mac's is its
  gate check's), then `event=recovery clipboard=false
  available_in_app=false`. An answer that lands after a cancel types nothing.

Escape is armed from the press: the hook's Escape cancel, which ignores Escape
at rest, is live from the key-down whose password-field check goes out, not
only once the dictation listens. With the key still held the hook already
heard Escape, as a key pressed with the hold key, and withdrew the press
silently; the arm is what lets it hear Escape once the key is up while the
hold is still open, after a quick tap waiting out the 0.35 s pair window or a
double-tap that latched while the first press's check was out. Escape before
the check answers withdraws the press: the microphone never opens, the check's late answer is ignored, the
status line says "Cancelled. Nothing was inserted.", the indicator
"Cancelled", and the receipt `result=cancelled` with no `event=released`, as
the Mac's discard during its capture warm-up. The Mac stores that cancel and
honours it once its warm-up ends, because the warm-up cannot be stopped. On
Windows the hook's cancel latch holds it, and whichever reads it first
withdraws the press: the next 30 ms poll, or the check's answer, which reads
the latch before it acts (the answer is posted at a higher priority than the
poll, and Escape has already taken the key up in the hook, so an answer that
ignored the latch would end the press as a key released before capture).
Nothing waits for capture to come up. A
shortcut made with the hold key before the answer withdraws the press too and
says nothing, as before. The arm comes down with a press that is not admitted
(a password field, the key up first, a cancel), so Escape at rest stays inert,
and a state the last dictation posted late does not take it from the press.
### Long takes are never lost

Built 2026-10-10 (LIMIT-WIN-A, the first half of Windows stage 1 of
[the cap's replacement](../DESIGN.md#long-takes-faults-and-lost-recordings)),
tested on Linux only: the spool's writes, WASAPI's own stop, the worker's
answer to a ping and the new section have never run on Windows. No stop loses
words; only Escape and Cancel discard:

| How the take ended | What happens to its words |
|---|---|
| Released, a stop key, or a stop of the hold policy: the five-minute limit, a silence stop, nothing heard, the stuck-key stop ([How long a hold listens](#how-long-a-hold-listens-on-windows)) | Transcribed and inserted, as before; the rows below apply to its decode as to any release's |
| Microphone unplugged, its stream stopped with an error, or no audio for 0.3 s | Transcribed and inserted up to there: "The microphone stopped at m:ss" |
| Worker stopped during a long take, two restarts failed | The take ends there; its release tries the worker once more: the words go in if it starts ("Speech recognition stopped at m:ss"), otherwise the take is kept |
| Worker died under the decode or missed its deadline | Started again, retried once with the longer deadline; kept if that fails too |
| Worker refused the decode | Kept (alive and said no: not retried) |
| A shortcut after more than 30 s of speech | Kept; a shorter one is a shortcut, as before |
| Escape or Cancel | Discarded; Escape on a take over 30 s asks first |
| Earlet quit, paused or crashed under it | Left on disk, offered at the next launch |

**The spool** (`DictationTakeSpool`, `DictationTakeConverter`). From the
press's admission, before the microphone opens, each buffer is downmixed and
resampled to 16 kHz as it arrives (the tap's arithmetic, carried across buffer
edges) and appended as 16-bit mono to
`%LOCALAPPDATA%\Dictation\recovery\take-<id>.pcm` behind a 24-byte header
(`DICTTAKE`, format 1, the rate, the start), flushed with every buffer: 32 KB
a second. The capture thread only converts; the file work runs in order on the
thread pool, and a write that fails marks the take (never offered) while
dictation carries on. The file goes once the words went in, the take was
cancelled or held nothing worth typing; a kept take becomes `kept.pcm`,
replacing any older one: one is kept at a time, and the section then adds "It
replaced an earlier saved recording." When `kept.pcm` cannot be replaced just
now (another program holds it), the take stays the kept one under its own
name, still recoverable. A file that cannot be opened just now is never read
as damaged or deleted, at launch or by Recover. No words, app or window are
written.

**Recover last dictation.** While a take is kept, the Saved recording section,
first under the header, says "Saved recording", "Couldn't transcribe it. 1:12
long." (or "Earlet closed before it was transcribed. 1:12 long."), when it
was recorded, Recover last dictation and Discard (no access key: it deletes),
and "Kept on this PC and never sent anywhere, until you recover or discard it,
for 7 days at most."; the menu leads with "Recover last dictation · <time>".
Recover transcribes the recording again (with the decode's one retry); its
words go to Last available text and, when nothing else is under way, onto the
clipboard through Copy last text, never pasted: the user is in this window or
menu, not the field they spoke into. The recording goes once its words are on
the clipboard, or the user dismisses them; until then the section adds "Its
words are in Last available text; it stays until they are copied.", since a
dictation that finishes first replaces them there (a recovery that ends while
one listens says "Your saved recording is transcribed: choose Copy last text
below or from the Earlet icon's menu. The recording stays saved until its
words are copied."). It goes at once when it proves to hold nothing or to be
damaged; one that cannot be opened just now stays ("Your saved recording could
not be opened just now. It is still saved; try again."), as does one whose
recovery fails, and a recovery never deletes a newer kept take that replaced
its own. It is offered only while dictation is on and at rest ("Turn on
dictation to recover it." while off); Pause is inactive and the idle unload
waits while it runs. At launch, a take a crash or quit left becomes the kept
take if it is the newest (every other spool file goes, and the section says it
replaced an earlier one when one was still offered) and brings the window
forward even at sign-in. A kept take is deleted unoffered once it is 7 days
old: at launch, and while Earlet runs, paused or not, since it starts at
sign-in and can run for weeks. The status line on a kept
take: "Couldn't transcribe that dictation, so nothing was inserted. The
recording is saved: choose Recover last dictation below or from the Earlet
icon's menu." (worker restarting: "Speech recognition stopped, so nothing was
inserted. The recording is saved for Recover last dictation. It is
restarting…"). A take is offered at once and its file is kept off the UI
thread; when that file turns out to have failed while it was written, the
offer is withdrawn ("Couldn't transcribe that dictation, and its recording
could not be saved.") and an earlier saved recording it replaced, which the
spool never touched, is offered again.

**Faults keep the take.** Each ends the listening take as a release would,
marking the hold ended early as the hold policy's stops do
(`HoldGate.EndedEarly`): what was captured is transcribed and inserted, a
hands-free latch ends, and a key still held starts nothing until pressed
again. The window says "The microphone stopped at 0:42, so the recording ended
there. Transcribing what was recorded…", then "… Your words up to then went
in." (or "… before anything was heard. Check the microphone, then hold Right
Ctrl again."), naming how much audio the take holds. The faults: the
endpoint removed ([above](#windows-window-and-tray)); NAudio's
`RecordingStopped` with an exception (`AUDCLNT_E_DEVICE_INVALIDATED`, say),
which nothing heard before, reported once per open capture by type and
HRESULT, never by message, on the thread pool, and not for a stop the device
asked for (`IAudioCaptureFaultWatch`); and the in-hold watchdog with the Mac's
thresholds (`DictationCaptureWatchdog`, its verdict tests ported): no buffer
for 0.3 s once past 0.8 s, and, because Windows cannot tell it the transport,
the Mac's Bluetooth 2.5 s for the first buffer. The graces count from the
first poll that sees the capture up, not from the press: Windows opens the
device at the press, on the UI thread, and a headset switching to its call
profile can take seconds to return, where the Mac counts from a microphone
already open. `DICTATION_CAPTURE_WATCHDOG=0` turns the watchdog off in the
field, as the Mac's `dictation.capture.watchdog` flag does
([development](development.md)). Whether 0.3 s is headroom
enough for WASAPI's 10–50 ms buffers on a loaded laptop is unmeasured.

**The speech worker.** Once a take has run 30 s, the worker is pinged every
10 s (its `ping` op, 5 s to answer, off the UI thread); a dead one is started
again by the check itself while the user speaks, a worker still loading for
this hold counts as alive, a failed check is followed at once by another (the
restart), and a check answered after a newer press counts for nothing. When
two restarts fail the take ends there ("Speech recognition stopped at 4:12");
its release's decode tries to start the worker once more, as every exchange on
a dead worker does, so its words go in if it starts and the take is kept if it
does not. A release's decode
whose worker dies under it or misses its deadline (30 s plus one second per
second of audio) is retried once with 30 s plus two (`RetryTimeoutFor`), the
window saying "Speech recognition stopped, so it is starting again to
transcribe this dictation…"; a refusal, or a decode that found no worker at
all, is not retried.

**Escape's question.** While the take holds more than 30 s of speech the
controller raises the hook's `EscapeAsksFirst` (`HoldKeyState`): the first
Escape abandons no held key and ends no latch, and the indicator asks
"Discard 3:42 of speech? — press Esc again" (the design's Windows words, split
at the indicator's " — " into a headline and what to do) for 5 s (never spoken
while the hold listens); the hook has lowered the flag, so a second Escape
within them discards even before the controller reads the first, and after
them the take carries on and the next Escape asks again. The second Escape is
a new press: Escape held down repeats with no key-up between, and those
autorepeats neither discard nor ask again. The controller raises the flag once
per question, never again under an Escape the hook took between its cancel
read and its sync in the same poll. The window's and the menu's Cancel never
ask, and Escape still reaches the app in front.

The hold policy's stops (LIMIT-WIN-B, [How long a hold listens](#how-long-a-hold-listens-on-windows))
end a take the same way, as a release, so each settles here as a release's
take does: its words go in and the file goes, or its decode fails and it is
kept, the memory cap's included, whose spool holds what the tap no longer kept
(`HoldStopTakeTests`).

### When the hold key cannot be heard

Two things keep the low-level keyboard hook from hearing the hold key, and
both used to look like a key that silently does nothing while the icon said
"hold Right Ctrl to dictate". Windows removes a hook whose callback overruns
`LowLevelHooksTimeout` (300 ms by default) and tells nobody; the hook runs on
the window's thread, which also opens the microphone at a press. And UIPI keeps
it from every key sent to a window running as administrator. The hook watch
handles both from the 30 ms poll, never from the hook's callback; its decisions
are pure code (`DictationHookWatch`, `AdminWindowWatch`) and the Win32 half is
`LowLevelHoldKeyRegistration.Watch.cs` and `ForegroundElevation`.

**A removed hook.** Every 0.5 s while dictation is on, and every 0.1 s while
they disagree or a test key is out, the poll compares Windows' own state of the
hold key (`GetAsyncKeyState`, that key only) with the hook's view of it. When
they first disagree with an ordinary window in front it sends the hook a test
key, the unassigned `VK 0xE8` that a bare-Alt hold already injects. A test key
that has not reached the hook 300 ms later, while the hook received no other
key either, means Windows removed it. That answer stands even when the press
is over by then, so one short tap is enough, such as the tap that stops a
hands-free latch. The watch then:

- puts the hook's view right: a release it never saw ends the hold as a
  release would, so the microphone does not stay open until the recording
  limit and the words go in; a press it never saw is adopted as down and
  holding nothing (its repeats open nothing, its release ends nothing), so
  nothing starts a second into the sentence;
- unhooks, in case Windows has not, and installs a new hook on the same
  thread; the hook still runs on the window's thread, and a thread of its own
  (G20) waits for Windows hardware;
- says so once nothing is under way, never before a paste: the status line
  reads "Windows stopped passing Right Ctrl to Earlet, so Earlet
  reconnected it. If a hold did nothing, click where you were typing and hold
  Right Ctrl again." and the window comes forward. Once per loss, the Mac's
  edge-not-level rule (`DictationWindowAttention`): a hook found gone again
  before the new one received a key does not bring it forward again, and once
  it has received one a later loss does;
- when Windows will not install a new hook, says "Windows stopped passing
  Right Ctrl to Earlet and would not let it reconnect. Pause and enable
  dictation again, or quit and reopen Earlet. For details, choose Open log
  folder." with the warning in the notification area (above), and tries again
  at the next disagreement, never on a timer.

None of it reaches the floating indicator: a press the dead hook missed
draws and says nothing while the watch repairs it, heartbeats included, and
a release it missed leaves the listening pill up and unspoken until the
replayed release ends the hold (`IndicatorWatchTests`).

A hook that receives anything is alive and only missed one edge (a key
already held when it was installed): its view is put right and nothing else
happens. A release a live hook missed is replayed only once the hold key itself
has sent the hook nothing for 1.2 s, longer than Windows' longest keyboard
repeat delay, because a key still held sends autorepeats. A hook that keeps
receiving them while Windows' state reads up sits in front of another program's
hook that swallows the key (a key remapper started before dictation was
enabled): the hold runs on to the real release, and the log says so once per
hold. A test key Windows refuses (another program blocking input) proves
nothing: the hook's view is put right, nothing is reinstalled and nothing is
said. With no window in front at all (a secure desktop: Ctrl+Alt+Del, a UAC
prompt, the lock screen), Windows' key state reads up and no hook hears
anything, so the watch waits and starts again, test key included, once a
window is back: a release made on the Ctrl+Alt+Del screen is put right quietly,
not called a lost hook. A window whose process cannot be asked what it runs as
(it exited meanwhile, or would not answer) is treated the same way, since it
may be above Earlet. A disagreement that began under an administrator window
gets its test key once an ordinary window is in front. While the two sides
disagree or a test key is out, the window in front is looked at on every poll,
so the reading that decides never acts on a window that has gone since. A
removed hook with the key up disagrees with nothing, so it is found at the next
press of the hold key, which records nothing; the Mac's tap is told at once. A
press shorter than half a second can fall between two looks and go unnoticed.
Pausing and enabling again, or choosing another hold key, starts a new watch:
the old hook's picture, its administrator hint and a loss not yet reported go
with it.

**A window running as administrator.** Every 0.5 s while dictation is on, the
poll asks whether the window in front belongs to an elevated process
(`OpenProcess` and the token's `TokenElevation`, cached for the process in
front; a process Earlet may not open at all, such as a program a standard
user elevated with an administrator's name and password, or whose token it may
not read counts as elevated; never asked when Earlet itself runs elevated). While one is in front, the tooltip
says so (above) and the shortcut line ends with "Earlet can't hear Right
Ctrl while an administrator window is in front.". A press made there, if
Windows reports it, records nothing and puts the same sentence on the status
line; a release made there ends the hold. Nothing is reinstalled, no test key
is sent (Windows drops it there), and the window does not come forward: it is a
limit of Windows, not a fault. With Earlet's own window in front the hint
is gone, so the shortcut line carries it only while another window is in front,
such as with the two side by side.

**With the microphone watch.** The two watches run from the same poll and can
both find something wrong at once (`HookAndMicrophoneTests`):

- The icon shows one, in the order above, which lives only in
  `DictationTrayGlyph`: a blocked microphone, then a hook Windows would not put
  back, then an administrator window, then a reconnected hook. The window's
  header shows the same one, with a headline for each
  ([Windows: window and tray](#windows-window-and-tray)).
- The status line says both on one line, the microphone's sentence and its
  buttons first, as the Mac's setup steps put the microphone before the
  shortcut, then this section's sentence, whichever was found first. Check
  again changes only the microphone's half; access coming back leaves this
  section's sentence on the line alone.
- Each brings the window forward once for its own cause: a hook lost while the
  microphone is blocked asks again, as a microphone lost after a hook does;
  neither asks twice for one cause, and neither keeps the other from asking.
- Neither waits on the other. The microphone's reading runs on the thread
  pool, one at a time, and one that never comes back does not hold up the
  hook's looks. The hook's look is a few Win32 calls that do not wait
  (`GetAsyncKeyState`; `GetForegroundWindow`, `OpenProcess` and the token,
  cached for the process in front; `SendInput` for the test key). Each keeps
  its own clock (5 s, or 1 s while a block is known; 0.5 s, or 0.1 s while the
  two sides disagree), and on a poll where both fall due the microphone is
  asked first.

The log says what was found, with the key's name and no app's: "The
hold-to-talk hook stopped receiving keys: Windows had Right Ctrl down where
the hook never saw it, and a test key sent to it <n> ms before never arrived;
reinstalled: True", "Hold-to-talk hook reinstalled on …", "The reinstalled
hold-to-talk hook is receiving keys", "The hook missed Right Ctrl going down
but still receives keys; its view was corrected", "The hook missed Right Ctrl
going down and Windows refused the test key, so whether the hook still receives
keys cannot be told; its view was corrected", "Windows shows Right Ctrl up while
the hook keeps receiving it, so another program's keyboard hook may be holding
it back; the hold carries on", "A window running as administrator is in front;
Right Ctrl cannot reach Earlet until it is not" and "Right Ctrl went down in
a window running as administrator, where the hook cannot hear it". Neither the
test key nor the bare-Alt phantom is passed to the correction watch's keyboard
feed, which would read it as the user typing. Built and tested on Linux only
(`HookWatchTests`, `HookWatchControllerTests`, `HoldKeyResyncTests`,
`LowLevelHoldKeyWatchTests`, with the Win32 half pinned on its source); no hook
has been seen removed or reinstalled on Windows, and none of these is measured
there: how often Windows removes the hook, whether `GetAsyncKeyState` reports a
key pressed in an elevated window, which processes refuse their handle or
their token, and whether a key remapper's hook holds the key back as
described.

### Diagnostics and the log on Windows

Built on 2026-10-09 and tested on Linux only: no Windows machine has written
this log. It keeps the Mac's receipt
([Diagnostics and the log](design-macos.md#diagnostics-and-the-log)) in the Windows logger's
line format.

- **At launch**, before the single-instance claim:
  `launch Version <version> · Build <stamp> path=<path of Dictation.exe>`. The
  stamp is the UTC `yyyyMMddHHmmss` id that `tools/package.py` passes as
  `-p:DictationBuild` and also writes into the package's `build-receipt.json`;
  a build that was not packaged says `Build not recorded`.
- **For each attempted hold**: an attempt id and `event=started` at the press,
  a refused press included; `event=released` where the hold is let go to
  transcription, as on the Mac: the key coming up on a held hold, a quick
  tap's 0.35 s pair window running out (not its key-up), the press, Space or
  Enter that stops a hands-free latch, or the app ending either (the
  recording limit, the memory cap behind it, a silence stop, nothing heard
  since the latch, or the stuck-key stop);
  and exactly one `event=finished` line with
  the ending, then `asr_ms`, `paste_ms`, `elapsed_ms` (from the press) and
  `release_ms` (from the release), each `n/a` for a stage not reached. A take
  a fault ended still ends as its words did (`result=inserted`, say) and adds
  `stop=microphone_stalled`, `microphone_failed`, `microphone_removed` or
  `worker_lost` after the timings, and one the hold policy ended adds
  `stop=recording_limit` (the memory cap's too), `silence`, `nothing_heard` or
  `stuck_key`, so a cut-short take is told from a release
  ([Long takes are never lost](#long-takes-are-never-lost)). A
  session result is classified where the session raises it, on the UI thread
  and before the controller posts it, so a result that lands as the app quits
  keeps its own ending. A quit or failure ends a live hold as `cancelled`;
  Pause cannot, since it refuses from the press on, a password-field check
  still out included.
- **A hands-free latch** is one attempt from its first press: the second
  press takes the first press's hold over and is no new press. Escape or
  Cancel during a latch ends it `cancelled` with no `event=released`, as the
  Mac's discard does. A latch stopped before the press-time password check
  answered recorded nothing: `released` at the stop, then
  `released_before_capture`. Each press the controller refuses is its own
  attempt, so a double-tap made while the last dictation goes in is two
  `still_finishing` attempts, and one made while Copy last text writes is two
  `still_copying` attempts; one whose second press lands while the first
  press's password check is still out is one attempt, refused once
  (`privacy_refused`) in a password field.
- **The endings.** The Mac's `inserted`, with `clipboard_unchanged`
  (`restore_detached` is always false: Windows restores the clipboard before
  the session ends); `failed` with a stage (`capture`, `asr` or `paste`) and a
  reason; `cancelled`; `shortcut`; `privacy_refused` (a password field at the
  press); `engine_unavailable` (no engine started for the hold; a worker that
  dies mid-decode is `recognition_failed`, as on the Mac); and
  `command_completed` (workflow and voice-delete outcomes, which this
  app never composes). The reasons keep the Mac's names where Windows can
  reach them: `microphone_permission`, `empty_capture`, `no_text`,
  `recognition_failed`, `no_field` and `secure_field` at stage `paste` (a
  password field found just before the paste). Windows
  adds `still_finishing`, for a press refused while the last dictation is
  still going in, and `still_copying`, for a press refused while Copy last
  text is still writing the clipboard (the Mac logs both presses without an
  attempt), and the reasons `microphone_unavailable`, `paste_refused`,
  `released_before_capture` (the key came up, or a latch was stopped, before
  the press-time password check answered) and `secure_field` at stage
  `capture` (a password field found at the release, before recognition,
  where the Mac does not look). A latch that heard nothing for 15 s is
  released as any other (`no_text` at stage `asr` in a quiet room).
  `hold_too_short` stays in the table for the shared session's outcome, but
  this app never reaches it: since the hands-free latch its tap has no
  minimum hold, and a lone quick tap is transcribed.
  Windows has no `timed_out`: the transcriber tells a missed deadline apart
  (it logs "Speech worker did not answer") and then folds it into a failed
  recognition before the session sees it.
- **The live preview**, when it is on (`DICTATION_PREVIEW=1`), adds three
  lines a hold, numbers and ids only: `Earlet live preview started:
  stream=<id> engine=whisper model=<model directory> on_npu=False` when the
  stream opens; when it stops, `Earlet preview profile: operation
  <attempt id>; session … ms; stream-open … ms; first-audio … ms; first-push
  … ms; first-partial … ms; pushes …; partials …; changed …; changed-gap
  p50/p95 …/… ms; backpressured buffers …; dropped samples …; preview length
  …; caption length …`, `(null)` meaning not reached; and after the result,
  `Earlet preview/final: attempt=<id> first_partial_ms=… changed=…
  changed_gap_p50_ms=… changed_gap_p95_ms=… preview_length=… final_length=…
  agreement=…`, the agreement between the whole text the preview last heard
  and the final words (0 to 1, or `n/a`). The first caption and the changes
  are counted from the caption drawn, as the Mac counts them: an answer that
  draws nothing (no words, or a lone "you") is no caption, and the same
  caption again is no change; `preview length` is the whole text heard and
  `caption length` the last caption drawn (the Mac's `caption_characters`).
  Never either text. These are the numbers a run
  on Windows hardware needs before the preview can be on by default.
- **The bug path.** `result=failed stage=unknown reason=unclassified` should
  never appear in a healthy log. It is written for a hold the session went
  back to rest from without a result (its pipeline threw), when the next hold
  is admitted or dictation stops, and for an outcome missing from the table.
- **After the ending**, under the same attempt id. A refused paste (`no_field`
  or `paste_refused`) adds the Mac's recovery line,
  `event=recovery clipboard=<true|false> available_in_app=<true|false>`:
  whether the words reached the clipboard, and whether Earlet holds them
  for Copy last text. Each Copy last text of a dictation's words adds
  `event=copy result=copied`, or `event=copy result=failed
  available_in_app=true` (a failed copy keeps the words), under the id of the
  dictation that left them. The Mac logs its copy as `recent text copy: …`
  without an id; Windows ties it to the words' dictation, so one id follows
  them from the press to their last copy. A copy the clipboard has not
  answered within 5 seconds is logged as failed, after a warning of its own
  that names the deadline, and a write that throws is logged as failed after a
  warning that names only the exception's type.

The receipt takes no text: its endings and its recovery and copy fields come
from a fixed table (`DictationAttemptReceipt`), never from the transcript, the
refused words, the target's process name or window title, or an error
message. The surrounding implementation outside
the receipt still names the target's process in the log: the session's
`Dictation completed: … target=<process name>` and `Earlet ended at the
recogniser: … from <process name>` lines, and the inserter's warnings when it
does not type or cannot restore the clipboard ("not typing into …", "Did not
type into …", "Typed into … but the clipboard was not restored"). Its
lines that also name the window title are at Debug, below the log's
Information level, except one warning: a foreground window whose process
it cannot identify is named by its title ("Foreground window … could not be
identified; not inserting."). Whether those lines keep the process name or
that title is a separate privacy decision, not taken here.

### Render modes on Windows

Built on 2026-10-09 (PDT) and tested on Linux only. WPF cannot run there and no
Windows machine has run either mode, so no Windows window or indicator has
been rendered yet. As on the Mac
([Preview and render](design-macos.md#the-floating-indicator-on-macos)), two
variables make `Dictation.exe` draw PNGs and exit instead of running
(`DictationRenderMode.cs`; every build, release included):

- **`DICTATION_HUD_RENDER_DIR=<dir>`**: every indicator pill, 65 frames
  (`DictationHudRenderFrames`): the listening pill held and hands-free, with
  the hands-free clock, the countdown both ways, the silence warning, a level
  and the live preview's caption; Transcribing and Typing; every ending Earlet can produce, the
  password field found at the paste included, plainly and with each context
  the controller adds that changes its line (the limit's stop, the silence
  stop after 30 s and after 60 s, the stuck-key stop, nothing heard, a shortcut,
  a recogniser restart, the words on the clipboard or kept for Copy last
  text, a take a microphone or the speech worker stopped at 4:12, a take kept
  for recovery, alone and after a shortcut), so every ending whose second line
  wraps to two lines is drawn; every press refusal; Copied and Couldn't copy;
  the remembered correction; the recogniser that could not restart; a
  recovery that failed; Escape's question on a take of 3:42; and the Mac's six-frame
  stability sequence of one recording whose caption grows and level moves.
  The contexts, endings and notices are the lists the indicator's tests hold
  every line to, and tests fail when a context field, a feed member or a
  `Hud` line of `DictationCopy` has no frame. Each frame is made by driving
  the real feed (`DictationHudFeed`, paced as in the app) and drawn by the
  real indicator window through `IDictationHud.Show` and `Level`, which sizes
  it as the app does and then neither shows, places nor announces it. At 100,
  150 and 200% in light, dark and high contrast: 585 images,
  `hud-<frame>-<percent>-<appearance>.png`, each exactly the window's size,
  shadow ring included, transparent around the capsule.
- **`DICTATION_WINDOW_RENDER_DIR=<dir>`**: the window in 34 scenarios, the
  Mac's list adapted (`DictationWindowRenderScenario`): first run, microphone
  blocked, shortcut refused, starting, ready, returning user, paused,
  recording, transcribing, typing, the hold key's hook not put back,
  reconnected, and unheard in front of an administrator window, the
  microphone changed 42 seconds into a hold, naming a device with a long name,
  a password field at the paste, the vocabulary with three words, unsaved and
  saved again, the recent result, its recovery, its copy and its Show text
  open, a take kept for Recover last dictation (the Saved recording section), the learning
  receipt and its undo, the correction notices (checking, field unread,
  pending, nothing learned), a learned word, missing resources, the two
  sign-in notes (a change Windows refused, the entry off in Settings), and
  Troubleshooting open. Every state of the header (`DictationGlyph`) is the
  end of some scenario, Troubleshooting and Show text are drawn open by one
  and closed by the rest, and Try dictation open (ready) and closed
  (returning user); tests fail when a header state or a section the user
  opens arrives without a scenario. The Mac's accessibility,
  input-monitoring and permission-recovery steps have no Windows
  counterpart beyond the microphone. At 420, 520 and 768 DIPs of content
  width (the whole column, without the title bar and frame), at the same
  scales and appearances: 918 images,
  `window-<scenario>-<width>-<percent>-<appearance>.png`. Then the
  notification-area icon, every picture at 16, 20, 24 and 32 pixels in each
  appearance's inks (a light taskbar, a dark one, High Contrast's): 60 images,
  `tray-<picture>-<pixels>-<appearance>.png`, read back from the `.ico` the
  tray hands Windows (`TrayIconArt.Render`). Each scenario builds the real
  `MainWindow`, controller and Vocabulary section over inert parts in a
  scratch folder (`DictationRenderComposition`: no microphone, keyboard hook,
  worker process, clipboard, registry or Explorer; a settings file and a
  vocabulary of its own; dictations stamped 3:41 PM local time; a
  controller clock that moves only when a scenario polls) and drives them
  through their public members (`DictationWindowRenderScript`); a scenario
  that holds the model's load, a transcript or the paste's settle for its
  picture lets it finish before the window lets go. A scenario that has not
  reached its state within 10 s fails the run rather than write a picture of
  the wrong state. The scripts and parts run on Linux
  (`DictationWindowRenderTests`).
- **Each scale is its own layout.** Every surface is given the DPI of the
  scale it is drawn at (`VisualTreeHelper.SetRootDpi`) and laid out again, so
  layout rounding snaps it to that scale's pixel grid, as on a monitor at that
  scale, rather than to the render machine's; the pill's lines are measured
  at that DPI too. A 150% image is meant to match the same window or pill on
  a 150% monitor pixel for pixel. Nobody has compared them.
- **Not the app.** A render opens no log, reads no settings and claims no
  single instance, so it runs beside a running Earlet; nothing is shown
  and the notification-area icon is never added. One line per image, and the
  verdict, go to stderr and to `render-report.txt` in the directory as each
  is written (a GUI executable started from Explorer has no console), so a
  run that stops part-way, or fails outright, still leaves its lines there. With both variables set,
  both run, the indicator first; the Mac runs only the indicator's.
- **The exit code.** 0 only when every image was written and the indicator's
  passed its check (`DictationHudRenderCheck`). On the layout, which passes
  on Linux for today's: the shadow ring holds the whole shadow (blur 24, 5
  DIP down, so 29 DIP below, 24 at the sides, 19 above, inside a 29-DIP
  ring), and every busy pill is HINTS' one busy width, measured in the
  window's own fonts at each scale. On the pixels, the Mac's: no image with
  any alpha on its outermost rows and columns (a shadow cut at the window's
  edge), every busy pill of one scale and appearance the same size, and, on
  Windows only, no ending narrower than them. 1 otherwise, or when a window
  scenario failed; 2 when the run itself failed. The Mac's window render
  exits 0 whatever it wrote.
- **Appearances are a stand-in.** The window draws with WPF's classic system
  colours and the indicator is always dark, so neither follows Windows' dark
  app mode: the dark images are the light ones, which is what a dark-mode user
  sees. Each appearance puts its colours in place of the system colours
  through WPF's `SystemColors` keys: Windows' default colours for light and
  dark, High Contrast Black's for high contrast (white on black, green
  disabled text, cyan selection, yellow links), which also takes WPF's AeroLite
  control theme. Run it on a machine that is not in High Contrast, whose
  control theme WPF picks from the system. The pill and the header's glyph
  read `SystemParameters.HighContrast` in the app; in a render each is told
  the appearance's setting instead (`DictationRender.HighContrast`), so the
  high-contrast images show the pill in the theme's colours with no shadow
  and the glyph in the Mac's three microphone shapes, whatever the render
  machine's setting, and a test fails when a drawn surface reads the setting
  with no such input. The icon follows the taskbar, not the app: its dark
  images are drawn for a dark taskbar, and its high-contrast ones in the
  same palette's text, selection and disabled-text colours. Nothing here has
  been compared with a real High Contrast screen.

### How long a hold listens on Windows

Built on 2026-10-10 (PDT) and tested on Linux only; never run on Windows. One
object holds every number (`DictationHoldPolicy.Windows`), and the controller
judges a listening hold against it once per 30 ms poll
(`DictationController.HoldPolicy.cs`). It replaced the two-minute
`MicrophoneTapOptions.Dictation`, the tap's latched-only bound, the
controller's `HandsFreeTap` and the indicator's 15-second countdown. The
shared rules, and how the Mac differs, are in
[DESIGN.md](../DESIGN.md#cancel-refused-presses-and-the-recording-limit).

| Rule | Windows |
|---|---|
| Recording limit, held or hands-free | 5:00, then transcribed and inserted (assumes the base or small model; see below) |
| Countdown before it | the last minute, with one alert sound as it starts |
| Memory cap behind the limit | 6:00, its own number |
| Hands-free silence stop (the window's choice) | 30 s (default), 60 s, or Never |
| Its warning | 10 s before the stop: at 20 s, or 50 s |
| Hands-free, nothing heard since the latch | 15 s, then let go as a release: a quiet room inserts nothing (off under Never) |
| Held, no speech (a stuck key) | 60 s, warned at 50 s, then transcribed and inserted |

- **The clock** starts when the press has opened the microphone (or failed
  to), not at the admission before it, as the Mac's starts once capture is
  running: a device that takes two seconds to open costs none of the limit.
- **The limit comes first**, whatever the silence says. A hold at it is let go
  as a release would be: the window says "Five minutes is the longest
  recording, so it stopped there. Transcribing…", then "Text inserted. A
  recording stops at five minutes; hold Right Ctrl again to continue.", and a
  key still held records nothing until it comes up. The countdown's sound is
  Windows' Asterisk sound (`SystemSounds`), so it follows the user's sound
  scheme and is silent under "No sounds"; it plays once per hold, while the
  microphone is open. Nobody has heard it on Windows, and whether the
  recogniser types anything for it is not measured. The microphone hears it
  too, so what the silence tracker hears in the 2 s after it restarts no
  silence (the sound's length on Windows is not measured).
- **Five minutes assumes the base or small model**; the design keeps 2:00 for
  turbo, which took 209 s at 5 min on a test machine (4 threads, English) against the
  330 s deadline. This build does not check the model. A missed deadline
  costs a wait, not the take: the decode is retried once with 630 s, and the
  take is kept for Recover last dictation if that misses too
  ([Long takes are never lost](#long-takes-are-never-lost)).
- **The memory.** The microphone tap resamples each buffer to 16 kHz PCM16 as
  it arrives (`TapRecording`), sample for sample what the release-time
  conversion made from the whole hold, and keeps it in one-second blocks:
  32 KB a second whatever the device's rate, about 10 MB at the limit, 38 MB
  for twenty minutes, where it kept 48 kHz floats before (230 MB for twenty
  minutes). At the memory cap it stops keeping audio and raises
  `BufferLimitReached` (once `LatchedCapReached`, which nothing subscribed
  to), naming its capture; the controller ends that hold there as at the
  limit, with everything kept, and ignores a capture already over. Only a
  poll that did not run in time lets a hold reach the cap.
- **Silence** is read from the same microphone buffers as the level bars, by
  `DictationSilenceTracker`: a 30 ms frame is speech when its RMS is above
  the larger of 0.004 and the 10th percentile of the last 10 s of frames
  raised by 10 dB, and only a run of nine such frames (270 ms; the rule is
  250 ms) counts, so clicks and a cough do not keep the microphone open and a
  fan or a café is the room, not speech. A steady sound becomes the floor; a
  second talker or music keeps the hold open until the limit, so the rule
  fails toward not stopping. Its constants are its own, not the four copies
  of the audible floor that decide what the recogniser is sent. Silence is
  timed on the controller's clock from the last speech the tracker heard (or
  from the start), so a microphone that sends nothing is silence too; with
  the capture watchdog on, as shipped, such a microphone ends the take first,
  as a stalled one ([Long takes are never lost](#long-takes-are-never-lost)),
  and the silence stop is the backstop under `DICTATION_CAPTURE_WATCHDOG=0`. The
  numbers come from synthetic probes; nothing has been calibrated on a real
  voice.
- **Hands-free, after speech.** The warning is the indicator's only, never
  spoken and never sounded ([The floating indicator](#the-floating-indicator-on-windows));
  speaking takes it down. At the stop the hold is transcribed and inserted
  and the latch ends at once (Space and Enter type again): the window says
  "No speech for 30 seconds, so hands-free dictation stopped. Transcribing…",
  then "Text inserted. Hands-free dictation stopped after 30 seconds without
  speech; double-tap Right Ctrl to keep going."
- **Hands-free, nothing heard.** Let go as a release, as every stop is: the
  tracker misses a voice less than about 10 dB over a steady noisy room, and
  the session's quiet-room check decides, as at any release. A quiet room
  never reaches the recogniser: "Nothing was heard for 15 seconds, so
  hands-free dictation stopped. Nothing was inserted. Double-tap Right Ctrl
  to try again." Missed speech goes in with the silence stop's lines. The
  cost: a noisy room nobody spoke into reaches the recogniser, which may type
  a stock phrase ("Thank you.").
- **Held.** No silence stop. After a minute with no speech the hold is let
  go and its words inserted, warned over the last 10 s as hands-free is,
  since what is said after the stop is not recorded: "No speech for a minute
  with Right Ctrl held, so it stopped. Transcribing…", then "Text inserted.
  Recording stopped after a minute without speech; release Right Ctrl, then
  hold it again to keep going."
- **The choice** "Silence stops hands-free" is remembered in `settings.json`
  as `hands_free_silence_stop_seconds` (30, 60, or 0 for Never; any other
  value reads as 30), and applies at once, to a hold that is listening too.
- **The log** says which stop ended a hold ("The hold reached the 300 s
  limit", "The hold ended after 30 s without speech", "Nothing was heard in
  15 s of hands-free dictation; it ends as a release", "Hands-free dictation
  ended: 30 s of silence"), the countdown's start, and a memory cap reached,
  never a word; the attempt's `event=finished` line ends with the stop
  ([Diagnostics](#diagnostics-and-the-log-on-windows)).
- **Every stop settles its take as a release does**
  ([Long takes are never lost](#long-takes-are-never-lost)): the spool is
  deleted once the words went in, a quiet room's never-spoke stop included,
  which holds nothing worth keeping; a decode that fails even on its retry
  keeps the take for Recover last dictation, the memory cap's with everything
  its spool heard.

Not in this stage: the release's progress hint ("Transcribing 6:40 of speech —
about 40 s") waits for the shipped model's speed on Windows (step 0);
decoding during the hold and the twenty-minute limit are stage 2; the Mac's
stage 1 is its own wave.

## The floating indicator on Windows

Windows has an indicator with the same job, written on 2026-09-30. **No one has
seen it on Windows hardware.** Everything in this section is what the code is
written to do, checked only by Linux-run tests of its logic
(`DictationHudFeed`, `DictationHudGeometry`, `DictationHudLevel`,
`DictationHudPacer`, `DictationHudContrast`) and by tests that read the
window's source text; how WPF and Windows actually treat the window is
unknown.

- **What it is.** `DictationHudWindow`: a WPF window with no chrome, transparent,
  topmost, not in the taskbar or Alt+Tab, not hit-testable, never activated on
  show, with the no-activate, click-through and tool-window extended styles set
  before its first show and set again on every show, and logged with the value
  Windows reports back. It must never take the focus: Earlet pastes into
  whatever window has it.
- **Where.** Bottom-centre of the monitor the foreground window is on, the
  capsule's bottom 120 DIP above that monitor's work area, worked out in
  physical pixels at that monitor's own scale. It is placed while hidden, shown
  transparent, placed again, then revealed once laid out.
- **What it says.** The Mac's titles and hints (`DictationHUDContent`), each
  drawn as a bold first line and a quieter second, with Windows' key names
  and Enter for Return. While the key is held, "Listening" over "Release to
  insert · Esc to cancel". While a double-tap has latched the hold
  hands-free, "Listening" over "m:ss · Press Right Ctrl, Space or Enter to
  stop", and the window's status line says "Dictating. Press Right Ctrl,
  Space or Enter to stop." In the last minute before the five-minute limit,
  held or hands-free, the hint becomes "Stops in 1:00 · Release to insert"
  or "Stops in 1:00 · Press Right Ctrl, Space or Enter to stop", counting
  down, and the system's alert sound plays once as it starts. Hands-free,
  20 s into a silence after speech, the pill says "No speech for 20 s" over
  "Stopping in 10 s. Keep talking to continue", the two numbers moving each
  second, with no sound and no live-preview caption over it, until speech
  takes it down or the silence stop ends the hold; held, the same warning
  comes at 50 s without speech, before the stuck-key stop; whichever stop
  comes first, the limit's or the silence's, is the one shown
  ([How long a hold listens](#how-long-a-hold-listens-on-windows)). The time
  runs from the moment the microphone came up, as the Mac's does and as
  Windows' limit counts, so the countdown ends where the limit does. The
  clock is floored and the time left rounded up, so the countdown runs down
  to 0:01 and the limit ends the hold as it would reach 0:00. The pill is
  redrawn only when its words change, so at most once a second, with tabular
  digits. Then "Transcribing…" over "Esc to cancel" (Escape still cancels
  until the paste begins), the same after a stop the app made, as on the
  Mac; "Typing" while the paste goes in, a Windows-only state with no hint;
  then a receipt or a caution. Every ending has its second line, what to do
  or what became of the words, except the receipts for what the user can see
  or did: "Typed", never the target's process name or window title, and
  "Cancelled". The limit's ending is the Mac's: "Stopped at 5:00" over "Your
  words went in, hold Right Ctrl again to keep going", a caution; the stops
  the Mac has not got are cautions too: "Stopped after 30 s of silence" over
  "Your words went in, double-tap Right Ctrl to keep going" ("after 15 s"
  for words the tracker missed), "Stopped" over "No speech for a minute with
  Right Ctrl held. Your words went in", and "Nothing heard in 15 s" over
  "Stopped, double-tap Right Ctrl to try again". The lines that said only
  what happened carry the Mac's what-to-do
  where the Mac has the ending and the window's words where it does not:
  "Couldn't type or copy — click a text field and try again" (the paste and
  the clipboard both failed and nothing kept the words), "No window had focus
  — click a text field and try again", "Nothing heard — hold Right Ctrl and
  speak" (the Mac's "while you speak", shortened to be read in 2.5 s), "No
  sound from the microphone — check the input device in Windows sound
  settings", "The microphone would not open — check that one is connected
  and not in use", and "Too short — hold Right Ctrl longer" (never shown:
  this app's tap has no minimum hold); each keeps the shared model's tone and
  dwell. Other lines cover the press refusals ("Still typing the last one —
  hold again", "Password field — nothing was recorded", "Still copying your
  last text — hold again"), a password field found at the release or just
  before the paste ("Password field — nothing was typed or kept"), a take a
  fault ended ("Microphone stopped at 4:12 — your words up to then went in",
  or "… — nothing was heard before it", and "Speech recognition stopped at
  4:12 — …" when the speech worker could not be brought back), a take kept
  for recovery ("Couldn't transcribe — the recording is saved. Use Recover
  last dictation to try again", and "Cancelled, that was a shortcut — the
  recording is saved for Recover last dictation"), a recovery that failed
  ("Couldn't transcribe the saved recording — it is still saved, try
  again"), Escape's question on a long take ("Discard 3:42 of speech? —
  press Esc again", shown, never spoken, while the hold listens,
  and up for the question's 5 s; the listening clock does not replace it),
  each a caution for the failure tier's 5 s
  ([Long takes are never lost](#long-takes-are-never-lost)), the clipboard fallback, words kept only in memory
  ("Your words are still here — open Earlet and choose Copy last text"),
  Copy last text's "Copied" and "Couldn't copy", a shortcut cancel, the
  recogniser restarting or failing to restart, and, at rest, a learned
  correction ("Remembered your correction — review or undo in Earlet",
  neutral, which does not replace a caution still up, and is not spoken while
  the Earlet window is in front, whose own line is). A listening hold
  outranks an ending, as on the Mac: a recogniser that gives up restarting
  while the key is held leaves the listening pill up and says nothing, and the
  release, which tries the recogniser once more, ends on the give-up if that
  fails too. Receipts stay 0.6 s, notices 2.5 s, failures 5 s; the limit's
  and the silence stops' endings are failure-tier 5 s, not the Mac's 3 s,
  because Windows holds every line to a reading floor (0.4 s plus four words
  a second) that their thirteen to seventeen words exceed;
  the learned correction keeps the Mac's 3 s (its eight words and the dash
  count as nine and ask 2.65 s).
  No ending, notice or receipt shows the dictated words; only the live
  preview below does, while the hold listens, and only when it is on.
- **Live preview, off unless `DICTATION_PREVIEW=1`.** Built on 2026-10-09 and
  tested on Linux only: never seen on Windows, and what it costs on Windows
  hardware is not measured, which is why it is off.
  With it on, while the hold listens, the last seven words or sixteen CJK
  characters the worker's live stream has heard stand in place of
  "Listening", over the same hint, as on the Mac
  ([States](design-macos.md#the-floating-indicator-on-macos)): display only,
  never inserted, gone at the release. A caption is windowed as the Mac's
  is (`DictationPreviewCaption.Window`: the last line of the reply, its
  speaker tag removed, and a lone "you" shown as nothing), and an answer
  with no words keeps the caption already shown. The pill keeps its one busy
  width, whose lines already include the caption's budget; a caption wider
  than that loses its oldest words behind an ellipsis
  (`DictationHudCaption`), as the Mac's title line cuts its head. It is
  drawn in a line of its own, out of the automation tree; the title line
  stays under it, transparent, still named "Listening" or "Listening,
  hands-free", so Narrator is never given the words. A caption that arrives
  after the release, or for an earlier hold, is dropped. No ending changes
  because a preview showed words: quieting of "Transcribing…" and of
  the receipt after a preview is not used. The stream opens once the press
  is admitted and the microphone is open, as on the Mac (a password field at
  the press is refused before the microphone opens, and a microphone that
  would not open gets no stream), only on a worker that is loaded
  and ready (a hold during a load, such as the first after an idle unload,
  has no preview, as on the Mac), and never starts one. It sends the Mac's
  `stream_start`: engine `whisper`, client `dictation`, 16 kHz,
  `rolling_stable` false, the app's vocabulary scene, the bias list and the
  languages, the picked one or Chinese and English for Automatic. Pushes
  carry 160 ms or more of 16 kHz audio, one in flight, skipped while the
  worker is busy; at most the newest 3 s waits for a push, and older audio
  is dropped and counted, as on the Mac. A stream belongs to the worker that
  opened it and dies with it: after an unload or restart no push or cancel
  reaches the new worker, and a worker that dies mid-hold is sent no cancel
  for its stream, so only the controller's restart policy starts another. A
  caption names the run it was heard for, and is drawn only while that run
  is the one listening, however late it reaches the UI thread. At the release the preview is detached, the open
  and the push in flight are awaited and the stream is cancelled, never
  finished, for at most 2 s (the Mac's bound), before the final decode goes
  to the worker. The worker reads one frame at a time, so a frame already
  on the wire still holds the decode until it answers or the host's 2 s
  limit on a stream op ends that worker; the decode then starts a fresh one.
  Escape, Cancel (the window's or the menu's), quitting and every other
  ending stop the preview without waiting; Pause cannot end a hold (it is
  refused under a dictation, `TryPause`), and a refused Pause leaves the
  preview running, and the stop's waiting and cancel run on the thread pool, never on
  the UI thread. Nothing runs without the indicator. Where the Mac's
  microphone rebind (D15) ends a run and starts a fresh one, a Windows
  microphone that goes away ends the take (its words still go in), and its
  run with it ([A microphone change during a hold](#windows-window-and-tray)).
- **Level bars.** While the hold listens, three bars stand where the tone dot
  is, with the Mac's numbers and the Mac's tests ported
  ([Look and Motion](design-macos.md#the-floating-indicator-on-macos); `DictationHudLevel`).
  Only while the session is Listening, and only when the indicator exists,
  the controller attaches a meter to the microphone. On the capture thread,
  without a lock, it keeps the loudest sample of each WASAPI buffer as the
  recogniser hears it: after the tap's downmix to mono (each frame's channels
  averaged), as the Mac reads the mono stream its recogniser is fed, but at
  the device's rate, before the resample to 16 kHz. The 30 ms tick takes that
  peak and resets it on the first tick at least 75 ms after the last sample,
  which is every third tick (90 ms) although the controller's clock,
  `Environment.TickCount64`, moves in 15.6 ms steps, and smooths it: a louder
  peak at once, a quieter one at 0.55 of the old level plus 0.45 of the new.
  The window eases toward each sample by 0.7 and glides the bars to it over
  the Mac's 85 ms (`DictationHudBars` holds the level shown; the window only
  applies its frames), then maps it through the −45 to −8 dB window to three
  bars 3 DIP wide with 3 DIP gaps, 3 to 13 DIP tall in a fixed 1, 0.62, 0.85
  silhouette; silence is one flat line 2 DIP high across the bars' 15 DIP.
  With Windows' animation effects off (Settings → Accessibility → Visual
  effects, read as `SystemParameters.ClientAreaAnimation`) the bars snap to
  each sample. The title stays "Listening" at every level; the shared model's
  "Hearing you" is not used. A level is never announced and is never in the
  name Narrator is given. When the hold stops listening (release, the
  recording limit, a silence stop, Escape, Cancel; Pause is inactive while it
  listens) the
  meter lets go of the microphone and leaves the level at zero. The bars are
  not seen falling: they give way to the next pill's dot ("Transcribing…",
  "Cancelled"), as the Mac's waveform is replaced
  when a hold ends, and the next hold's bars start from the flat line. A
  hands-free latch is listening too, so its bars keep moving in the slot
  beside the clock: the pill is shown again each second as the clock
  changes, the window puts the dot back on every show, and the feed draws
  the bars again in the same turn, at the current level without that one
  sample's ease (whether the bars are seen to jump once a second is
  unmeasured). A window that cannot draw a level is tried once per hold, not
  once per redraw. Two differences from the Mac: a sample with no buffers in it counts as
  silence, so a microphone that stops delivering mid-hold falls to the flat
  line, where the Mac, which updates only when a chunk arrives, keeps its
  last level; and the dot and the bars share one slot the bars' 15 DIP wide
  on every pill, where the Mac's icon slot is 18 pt. That slot is 7 DIP wider
  than the 8 DIP dot alone, so the pill's chrome is 63 DIP (2 × (18 + 1) +
  15 + 10): every pill is 7 DIP wider than the dot alone would make it. The
  dB window comes from Mac capture (speech peaks of −26 to
  −21 dB); WASAPI shared-mode gain and automatic gain control differ, so
  whether ordinary speech fills half the bars on Windows is unmeasured.
- **Size and look.** As on the Mac, every busy pill (held, hands-free,
  Transcribing and Windows' Typing) is one width for the hold key, so the
  pill neither moves nor resizes from the press, through a latch and the
  release, until the ending: as wide as the widest line any of them can show
  (`DictationCopy.HudBusyLines`), which covers each title and hint, the
  hands-free clock at a fixed "00:00" (so the width does not depend on the
  limit: it holds any clock to 99:59, the twenty minutes stage 2 moves to
  included), the countdown at its start, and the live-preview caption budget
  (seven words, sixteen CJK characters), plus 2 DIP spare; every line of the
  silence warning is narrower than the countdown's for every hold key (the
  width tests check it), so it needs no room of its own. The window measures those lines with
  `FormattedText`, which does not apply the second line's tabular digits, so
  each clock line is measured with every digit as 0, then as 1, and so on to
  9, whichever the font draws widest. A busy pill with no second line
  (Typing) keeps that line's room, so the busy pills are one height too. An
  ending starts at that width and takes what its longer line needs, up to a
  536 DIP cap; there its second line wraps to at most two lines of 16 DIP, as
  the Mac's outcome wraps at its 420 pt cap, and only what two lines cannot
  hold ends in an ellipsis (`DictationHudGeometry.DetailWraps`). A busy pill's
  lines never wrap, and no title is long enough to need it; every line the
  indicator can show fits that way in the stand-in face the tests measure
  with. Every pill the feed shows
  is sized this way (`DictationHudModel.SizedFor`); the arithmetic is
  `DictationHudGeometry`, tested on Linux with a stand-in font, and how wide
  the real fonts make it is not measured. An always-dark capsule (#F21C1C1F) with the indigo to
  violet hairline the Mac drew until 2026-10-16, a drop shadow (blur 24, 5 DIP down) inside a 29-DIP
  transparent ring, the blur plus the depth, so the window's edge no longer
  cuts it (the ring was 16 DIP; that WPF's blur reaches no further than its
  radius is not checked on a device, and the
  [indicator render mode](#render-modes-on-windows) fails an image it
  reaches the edge of), and a tone dot: bright violet, or
  orange (#FF9F0A) for a caution, replaced while the hold listens by level
  bars in the violet gradient the Mac drew until 2026-10-16 (#7A4FD4 at the
  foot to #9F7AEA at the top). Only the bars move: the pill itself does not
  fade or rise. The Mac indicator now draws the window's gold on a warm dark
  bezel; neither that, nor the Mac's amber caution and white neutral dot, has
  been reconciled with these.
- **High Contrast.** While a contrast theme is on
  (`SystemParameters.HighContrast`), the pill is drawn in the theme's own
  colours instead (`DictationHudContrast`): its text, both lines and the
  caption, on its background, a hairline in the text colour, the text colour
  for an ordinary dot and the selection colour for a caution dot and the
  level bars, and no shadow. Each is a reference to the system brush, so a
  change of contrast theme repaints a pill on screen, and turning High
  Contrast on or off is followed while the app runs. Off, the designed
  palette above comes back unchanged.
- **Narrator.** One polite live region whose name is the whole sentence,
  and with it a UI Automation notification event carrying the same sentence
  (`AutomationPeer.RaiseNotificationEvent`, one activity, most recent first),
  which a reader speaks wherever its focus is, as the Mac's announcement is:
  the window is never in front, and whether Narrator follows a live region in
  a window that is not has not been seen. Both are raised only when the
  sentence changes, and neither while the hold listens. Like the Mac rule
  ([VoiceOver](design-macos.md#the-floating-indicator-on-macos)), nothing is announced while
  the hold is listening, held or hands-free (the clock included), because the
  microphone is open and Narrator through the speakers would be dictated:
  not the hint, not the clock, not the countdown, and never the live
  preview's words, which are in no name at all. "Transcribing… — Esc to
  cancel" and the ending are announced once the capture has stopped. The
  listening pill is named for its state alone, "Listening" or "Listening,
  hands-free" (the Mac's VoiceOver label), with no live-region event, so a
  reader who reaches the pill mid-hold hears the state, never the clock or
  the previous dictation's ending. Any other pill shown unannounced (the
  learned correction while the Earlet window is in front) is named by its
  whole line, its "what to do" half included, as an announced one is. The
  feed decides (`DictationHudFeed`, `IDictationHud.Show(model, announce)`,
  `DictationHudModel.QuietName`),
  so the rule is tested on Linux; whether Narrator honours it on a device,
  whether a name set on a live region is itself ever spoken, and whether a
  sentence raised both ways is then said once or twice, has not been heard.
- **Pacing and the heartbeat.** The Mac's (`DictationHudPacer`,
  `DictationHudReconcile`, its tests ported one for one). A visible state
  stands at least 375 ms before a different one replaces it, so a quick
  release goes from "Listening" to its ending without "Transcribing…" and
  "Typing" flashing for a frame each; "Typing", a Windows state, waits for
  "Transcribing…" the same way. Hiding, an ending (a take a fault stopped and a
  password field at the paste included), a new hold after an ending and a
  latch within the hold are immediate, and the listening pill
  redrawn with its clock or caption is the same state. A pill held back
  waits on the controller's clock and is drawn, and announced, on the tick
  its wait ends; a newer pill, the session returning to rest or a pause
  drops it. Once a second the controller's tick compares the pill the feed
  drew with the window's own account (`IDictationHud.IsShowing`: the
  window shown and revealed, whatever pill it was last given, so a hide that
  did not take still counts as showing): a pill the window lost is drawn again, not
  announced again, with its bars on a listening pill, and one it kept after
  the feed took it down is hidden. Each heal is one log line, "Earlet
  indicator heartbeat: Reoffer, wanted Ending, window showing: False" or
  "Hide, wanted Hidden, window showing: True", repeated at most once a
  minute while the same disagreement lasts; a window that throws on every
  draw is warned about by the draw that failed, and the heartbeat's retries
  each second log below the log's level. No such wedge has been seen on Windows: the window has never
  run.
- **Not built.** Dragging, docking, a resting island, click-to-stop, and the
  Mac's fades, rise and size ease, which wait for a device run (they would
  honour Windows' animation effects switch); neither app has a microphone
  picker.
