# Changelog

All notable changes to Earlet are recorded here, newest first, for people
who use the apps. The format follows
[Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and versions
follow [Semantic Versioning](https://semver.org/). A change a user would
notice adds a line under Unreleased in the same pull request.

## [Unreleased]

## [0.2.4] - 2026-10-30

A copy of 0.2.0, 0.2.1, 0.2.2 or 0.2.3 updates itself to this one. It is a
signed, notarized disk image on [GitHub Releases](https://github.com/scamai/earlet/releases/tag/v0.2.4).

### Changed

- **macOS: the speech engine stays ready for as long as your own pauses
  suggest.** Instead of unloading after three idle minutes, Earlet learns how
  long you usually pause between dictations and keeps the engine (about 3 GB)
  loaded for that long, between 3 and 30 minutes, so fewer dictations wait
  for it to start. When macOS runs short of memory, it shortens the wait, and
  gives the memory back at once if the shortage is critical. Only the lengths
  of the pauses are kept.

### Fixed

- **macOS: dictation is faster after you stop, and your vocabulary applies
  more often.** When a recording ended in silence, the vocabulary search
  could run on into a made-up, repeating ending that outscored the real
  sentence, so Earlet threw the result away and transcribed the stretch again
  without your vocabulary. It now keeps the real sentence. On the
  dictations measured, the extra pass cost about half a second for every
  30 seconds of speech.

## [0.2.3] - 2026-10-30

A copy of 0.2.0, 0.2.1 or 0.2.2 updates itself to this one. It is a signed,
notarized disk image on [GitHub Releases](https://github.com/scamai/earlet/releases/tag/v0.2.3).

### Changed

- **macOS: the installed app is about 60 MB smaller, and nothing in it names
  the machine that built it.** The bundled Python no longer carries its C headers, its
  packages' own test suites or its unused command-line scripts, and its
  compiled files no longer record the folder they were built in.
- **macOS: a first run turns itself on.** Once you have allowed the
  microphone, Accessibility (Device Control and Data Access on macOS 27) and
  Input Monitoring, Earlet turns dictation on and says "You're set up."
  There is no separate Enable click any more. An Earlet you have paused stays
  paused; one that has the permissions but was never turned on turns on after
  this update.
- **macOS: setup says what Earlet is and reads as next steps, not errors.**
  The first screen now opens with one sentence on what Earlet does and that
  it needs three permissions. Steps you haven't reached read "Not yet" and
  the one to do now reads "Next", in gold, instead of three red "Not
  allowed". Each step says what the permission is for, including that
  between dictations Earlet sees only keys like Shift, Option and Command,
  and that learning rereads the words it just typed for up to 90 seconds.
  A missing permission after Earlet has been on is still shown as a problem.
- **macOS: one click opens one thing.** Allowing Accessibility or Input
  Monitoring used to bring up macOS's dialog and System Settings at the same
  time. Now the dialog comes up alone the first time, and System Settings
  opens when there is no dialog. Once you turn Earlet on in System Settings,
  Earlet comes back to the front instead of staying behind.
- **macOS: clearer dead ends.** A copy outside Applications says so above
  the permissions instead of inside a closed section. A copy without its
  speech files offers Download Earlet… instead of an Enable that fails the
  same way every time, and a failed start offers Try Again. The restart step
  shows only Restart Earlet. While dictation is off, the hold key card says
  so instead of promising the gesture. macOS's microphone prompt now reads
  "Earlet listens while you dictate and turns your speech into text on this
  Mac."
- **macOS: VoiceOver hears setup move on,** and what Check Again found.
- **macOS: one word for each thing.** Earlet is either on or off: the
  buttons are Turn On and Turn Off (Turn Earlet On and Turn Earlet Off in the
  menu bar), replacing Enable, Pause, Enabled and Paused. Double-tapping the
  key is "hands-free" everywhere, and the key card says how to finish:
  press the key again to insert. Settings calls the key the "Dictation key".
- **macOS: Home says things once.** Once you have dictated, the status line
  no longer repeats the key the card below already shows, and the card names
  your mouse button too. Anything waiting for you comes before the card. The
  "Last available text" card appears only when its words need you, not after
  every dictation; Copy last text stays in the menu bar. The status dot no
  longer glows green beside a sentence saying the text could not be typed.
- **Vocabulary: learning promises only what it does.** The line under "Learn
  from my corrections" now says that Earlet may learn a word you fix within 90
  seconds, but only in apps whose text it can read, so rarely, and that adding
  the word yourself always works. After a dictation you didn't correct, or in
  a field Earlet couldn't read, the pane no longer shows a notice; it speaks
  up only when a correction needs a second match, is already known, or taught
  nothing. A learned word's label says how often your fixes confirmed it
  instead of "Tentative", "Confirmed" or "Established". The too-long message
  states the real limit (6 words, or 12 Chinese, Japanese or Korean
  characters, and 60 characters), and Forget all says what it clears and that
  it can't be undone.
- **macOS: Vocabulary counts words and misheard forms apart,** and VoiceOver
  hears its notices, refusals and receipts as they arrive.
- **macOS: the indicator says hands-free, and what to do when a recording was
  kept.** A double-tapped dictation reads "Listening hands-free" with "Right
  Command, Space or Return inserts · Esc cancels", so it no longer looks like
  a held one or hides Escape. When the speech engine fails but the recording
  is saved, the indicator says "Recording kept. Recover it from Earlet's menu
  bar icon." instead of "Try again".
- **macOS: Settings and History explain themselves.** The key test is a row
  that says what it checks; the silence setting says what silence does; the
  Troubleshooting buttons say what each one is for and wrap in a narrow
  window, and Check Permissions Again always answers. A long dictation in
  History has Show All, Copy confirms with a check mark, and a locked History
  says how to unlock it.
- **macOS: Settings' footer says what is kept apart from History:** one
  dictation that couldn't be transcribed stays as audio, not encrypted, for
  up to 7 days so you can recover it.

## [0.2.2] - 2026-10-29

A copy of 0.2.0 or 0.2.1 updates itself to this one. It is a signed, notarized
disk image on [GitHub Releases](https://github.com/scamai/earlet/releases/tag/v0.2.2).

### Fixed

- **The live preview keeps dictated sign-offs.** Phrases such as "Thank you."
  could disappear from the preview even though the final transcription kept
  them. The preview now follows the same dictation rule while still rejecting
  decoder loops and non-speech annotations.

- **macOS 27: every message names Device Control and Data Access.** The
  setup checklist already used the pane's new name, but the floating
  indicator, the status line, the key test and the mouse-button notice still
  said Accessibility, the pane's name before macOS 27. Earlier macOS
  versions still read Accessibility.

- **macOS: messages point to where Copy last text and Recover Last Dictation
  are.** They said "the Earlet menu", which reads as the app menu beside the
  Apple menu; both are in the menu of Earlet's menu bar icon, and on Home.
  While words or a recording wait there, the menu bar icon now carries an
  orange dot.

- **macOS: Escape on a long dictation asks on the indicator and keeps your
  place.** After more than 30 seconds, the first Escape used to bring
  Earlet's window forward with a question, taking the keyboard from the app
  you were dictating into, and pressing Escape in that question kept the
  recording. Now the indicator asks "Discard 0:42 of speech?", a second Escape
  within 5 seconds discards, and otherwise the dictation carries on and its
  words go where you were typing. Cancel in the window or the menu bar
  discards without asking.

- **macOS: a microphone that sends no sound is named.** A hold of a second
  or more that hears nothing from the input, as when AirPods are connected
  but silent, now ends with "No sound from AirPods Pro" and how to check it,
  instead of "Nothing heard — Hold Right Command while you speak", and a hold
  says so after 2.5 s without sound. "Nothing heard" after a longer hold
  that picked up only faint sound asks you to speak up or move closer
  instead of to hold the key.

- **macOS: a dictation that comes back with no words says why, and keeps a
  clearly spoken take.** Instead of "No words recognized" every time, the
  indicator says when the recording was too quiet to make out, when Earlet
  couldn't tell its language, and when it couldn't make out the words of a
  clearly spoken one. The last two keep the recording, and Recover Last
  Dictation now transcribes it without the checks that set it aside, in the
  language chosen in Settings.

### Changed

- **macOS: a smaller download.** The bundled Python no longer carries
  PyTorch, pip or the Tk toolkit, which Earlet never used. The 0.2.2 disk
  image is 572 MB, down from 851 MB for 0.2.1, and the installed app 1.1 GB
  instead of 1.9 GB. An update from 0.2.0 or 0.2.1 downloads about 640 KB.
  Dictation itself is unchanged.
- **Vocabulary: the field after the word now reads "Often misheard as".** It
  used to read "Sounds like", but it never took a pronunciation: it takes the
  text Earlet writes for the word now, and Earlet writes the word instead
  whenever it would write that text. A line under the field now says so.

## [0.2.1] - 2026-10-26

A copy of 0.2.0 updates itself to this one. It is a signed, notarized disk
image on [GitHub Releases](https://github.com/scamai/earlet/releases/tag/v0.2.1).

### Changed

- **macOS: the microphone closes sooner after a dictation.** It stayed open
  for 30–35 seconds so the next dictation would start at once. It now closes
  3 seconds after a dictation with a built-in or wired microphone, which
  starts again in about a tenth of a second, and 10 seconds after one with a
  Bluetooth microphone such as AirPods, which is slower to start and plays in
  its low-quality call mode while the microphone is open.

## [0.2.0] - 2026-10-25

The first Mac release that updates itself: install it once by hand over
0.1.0, and later versions arrive on their own. It is a signed, notarized disk
image on [GitHub Releases](https://github.com/scamai/earlet/releases/tag/v0.2.0).

### Fixed

- **macOS: a short or quiet dictation no longer waits for the live preview.**
  The preview now decodes each stretch of speech once and starts only after
  1.5 s of it. Before, on quiet speech it could retry the same stretch up to
  six times, and the final transcription of a short hold waited for all of
  them: a synthetic quiet 2-second clip took 4.3 s with the preview running,
  and takes 1.8–2.9 s now. The final transcription still retries
  hard-to-hear speech.
- **macOS: the live preview shows a quiet voice.** It used to take the start of
  the hold, already speech, as the background noise level, so a soft-spoken
  user's words seldom appeared while they spoke (a synthetic quiet 26-second
  clip showed its first words after 12.8 s; now after 2.4 s).
- **macOS: History shows each dictation's language.** The recognizer reported
  it, but it never reached the History entry.

### Added

- **macOS: Earlet updates itself.** Once a day it checks GitHub Releases for a
  newer version, downloads it in the background and installs it when you
  quit Earlet. When only part of the app changed, it downloads a small patch
  instead of the whole image. Your settings, words, History and permissions
  stay. Turn it off under Settings → Update Earlet automatically, or check
  now with Earlet → Check for Updates…. A copy of 0.1.0 has to be replaced by
  hand once to get this.
- **macOS: choose your own hold key.** "Record a Key…" at the end of the Hold
  to talk picker takes whatever you press next: a modifier key on its own
  (Left Option, Left Shift…) or a key combination such as ⌥Space, ⌃⌥D or F13.
  Hold it to talk and double-tap it to keep listening, as with the preset
  keys. While Earlet is on, other apps no longer receive a chosen
  combination; one that macOS keeps for itself is refused when you record it.
- **macOS: a mouse side button can start a dictation.** Settings can add
  mouse button 4 (Back) or 5 (Forward) beside the hold key: hold it to talk,
  or click or double-click it to keep listening and press it again to stop.
  "Record a Mouse Button…" takes any extra button your mouse has, up to
  button 32. While chosen, the button no longer does its usual job (Back or
  Forward for the side buttons).
- **Windows: a mouse side button can start a dictation.** The window can add
  mouse button 4 or 5 beside the hold key: hold it to talk, click it once to
  keep dictating, and press it again to stop. While chosen, the button no
  longer goes Back or Forward.
- **macOS: corrections are learned in Claude Code inside Witty.** A word you
  fix in Claude Code's input box, with Claude Code filling the window or in a
  tmux pane, is learned the same way as in Codex. Chinese text that
  wraps onto the next row is read without a space at the wrap.
- **macOS: a fix you send right away is still learned.** Pressing Return in a
  chat box (Claude Code, Codex, a messenger) clears it, and a correction used
  to count only after 4 s without typing. Now the text you sent counts if
  you had stopped typing for 1 s before sending.
- **macOS: the log says where a slow transcription's time went.** Each
  dictation's line adds `asr_wait_ms` (waiting for a preview decode already
  running), `asr_decode_ms`, `asr_windows` (30-second windows decoded),
  `asr_retries` (re-decodes of hard-to-hear audio) and `asr_warm` (whether the
  speech engine had already run since it loaded). These are counts, never
  words.

## [0.1.0] - 2026-10-25

The first release. Apple Silicon Macs can download a signed, notarized disk
image from [GitHub Releases](https://github.com/scamai/earlet/releases/tag/v0.1.0);
the Python runtime and speech model are inside it.

- **Product name.** The source preview is now named Earlet, with a wheat
  microphone icon.

### Added

- **macOS history.** Each dictation whose words reached you is saved in a
  History pane, by day and searchable, with Copy, Delete and its recording to
  play back. Everything is encrypted on the Mac (AES-256-GCM) under a key kept
  in the login keychain; a dictation refused for a password field or Secure
  Input, a spoken command, a cancelled one and one into Earlet's own
  window (its practice field) are never saved. Settings sets
  how long words (7 days, 30 days, 1 year or forever; forever by default) and
  recordings (none, 7 or 30 days; 30 days by default, 1 GiB at most) are kept,
  turns History off, and Delete All History destroys the key. Home shows the
  last seven days' words, dictations, speaking time and pace.

### Fixed

- **macOS correction learning.** Recognize both current and legacy Witty
  releases when observing a Codex composer, while retaining the same strict
  input-only parsing and refusal of unknown terminal layouts.

### Changed

- **Independent preferences.** macOS capture uses product-scoped echo-cancellation
  and input-device keys. Observation diagnostics use the canonical reason names.
- **Windows vocabulary.** The bundled ordinary-word list is a curated subset
  of the public-domain dictionary; macOS continues to use its system dictionary.

- **Source preview scope.** Remove unshipped meeting summaries, model-backed
  speaker diarization, voice enrolment and the Snapdragon worker backend.
  The native dictation and live-preview package keeps the same module set.

- **Community reporting.** Conduct reports now go directly to the public team
  support mailbox; security vulnerabilities retain their private GitHub route.

- **Windows shortcut setup.** Key advice now depends only on your selected key
  and configuration; the app no longer probes another product at startup or
  when enabling dictation.

- **README presentation.** A quieter opening puts the native interface first,
  with a direct source-preview path, shorter feature sections, and expandable
  developer reference.
- Add a native light/dark macOS interface detail and a reproducible guide to the README.
- **Getting started and community help.** The README now separates trying the
  Mac source preview from developer tests and packaging, walks through a
  first dictation, and links setup troubleshooting and first contributions.
- **macOS indicator and icon.** The floating indicator's level bars and edge
  are wheat gold on a warm dark bezel, matching the window, and the app icon
  is a gold tile with the new mark.
- **Learning, both apps.** A fix that only changes a number, such as a PIN, is
  no longer kept in the vocabulary file.
- **Learning, both apps.** Learning never turns a plural or past form into a
  rewrite, such as "app" always becoming "apps".
- **Learning, both apps.** Learning no longer mistakes a past or -ing form of
  an ordinary word, such as "deployed" or "pushed", for a new word to learn.
- **Vocabulary wording, both apps.** The Vocabulary section says learning
  keeps words, not whole sentences; it said whole sentences aren't saved,
  which the Mac history now does.
- **macOS window, with a look of its own.** It no longer looks like System
  Settings: a warm paper ground (a warm near-black in dark mode), ink text and
  wheat gold, rounded headings and figures, and a new mark, a waveform whose
  bars draw an ear of wheat. A rail with the mark separates Home (whether
  dictation is on, the hold key drawn as a key, setup, and anything waiting
  for you), History, Vocabulary and Settings, so a long vocabulary list no
  longer pushes the preferences out of sight; each is a page rather than grey
  grouped boxes, and buttons, choices, switches and fields share one style,
  with no capsules. Settings… (⌘,) opens Settings, ⌘1 to ⌘4 switch pages, ⌘F
  searches History, and Home is marked in the rail while it holds something
  only you can finish, such as a missing permission or words to copy.
  Shortcut, Recording and Language are one Dictating group in Settings.
  Light, dark and Increase Contrast keep readable contrast, and VoiceOver and
  the keyboard work as before.
- Clarify the macOS source-preview setup, platform scope and contribution
  entry points; pin CI actions and disable persisted checkout credentials.

This section summarises what 0.1.0 does. The Windows app has been built and tested on Linux only; none
of its entries has run on Windows hardware.

### Fixed

- **macOS vocabulary ranked by app.** The final recognition now orders your
  learned words by the kind of app you dictate into, as the live preview and
  the Windows app already did; on the Mac it used the general order for
  every dictation.

- **macOS quiet recordings.** Reject brief noise transients before recognition
  and insertion, while retaining short words surrounded by silence.

- **macOS intermittent output diagnostics.** Attempt logs include text lengths
  and repeated-character counts at recognition and insertion preparation,
  without retaining dictated words or changing the text.

- **macOS correction diagnostics.** Use independent condition names while continuing to read earlier labels.

- **macOS terminal corrections.** Recognize the narrow composer when its context and keyboard hints are truncated.

- **macOS Secure Input checks.** Use the supported session-dictionary API,
  validate holder identifiers, and serialize Secure Input reads on the main
  thread. Stale holder metadata cannot keep shortcuts blocked.

- **macOS preview load.** Pace repeated MLX preview decoding according to
  the time each pass takes and combine pending sentence boundaries. The
  final recognition still receives the entire recording.

- **macOS long dictation completeness.** Vocabulary-enhanced recognition
  keeps evaluating a continuing sentence when short completed alternatives
  would otherwise make it stop early and omit speech.

- **macOS shortcut cleanup.** Ending a hands-free recording releases its
  event-tap registration; stopping the hold-key and mouse listeners does too.

- **macOS correction notices.** An unchanged readable field reports that no
  correction was detected. A completed correction notice remains visible
  when you switch to the app to read it.

- **macOS live preview after idle.** Preview waits for the speech engine to
  finish starting during the same recording. Releasing, cancelling or quitting
  closes the preview, and a late startup cannot reopen the ended recording.
- **macOS microphone recovery.** A recording containing only digital zeros
  now reports a microphone input problem and releases the capture so the
  next hold opens it again. Quiet room tone still gets the normal silence hint.
- **macOS recording recovery.** Every take is spooled as PCM16 while it
  records; successful insertion, text fallback and cancellation remove it.
  One failed or interrupted take remains recoverable for up to 7 days, with
  visible retry state when deletion fails.
- **macOS hold policy.** The limit is 600 seconds with a 60-second countdown;
  hands-free silence supports 30 seconds, 60 seconds or Off, with the held
  no-speech guard and first-speech stop preserved.
- **macOS test-key diagnostics.** OS activity is reported with truthful
  availability instead of a hardcoded application warning, and engine
  failure is surfaced as an explicit state.
- **macOS correction capture in the terminal composer.** Recognise the
  two-row model/context footer, including its queue hint after typing,
  while keeping exact paste anchoring and clipped-draft safeguards.

### Changed

- **macOS source cleanup.** Remove unused meeting capture, identity models and
  experimental speech/formatting code from the native package. Rename its
  libraries to `DictationKit`, `DictationIPC` and `DictationAudioCapture`.

### Added

- **macOS app (Apple Silicon, macOS 14 or later).** A window and a menu bar
  icon. Hold a key (Right Command by default; six other modifier keys can be
  chosen), speak and release, and the words are typed at the cursor of the
  app in front. A double-tap keeps dictation listening hands-free until one
  more press, Space, Return or keypad Enter. Escape or Cancel discards a
  dictation. Speech is recognised on the computer with Whisper on Apple's
  MLX. A floating indicator shows listening, transcribing and how each
  dictation ended, with level bars and the words as you speak them. Launch
  at login.
- **Windows app (x64; built and tested on Linux, never run on Windows).** A
  window, a notification-area icon and menu, and a floating indicator with
  level bars, in the Mac app's layout. Hold Right Ctrl by default, with the
  same hands-free double-tap (Space or Enter stops it). Speech is recognised
  on the CPU with faster-whisper (CTranslate2). The words as you speak them
  are off by default. A Try dictation practice field, and start at sign-in.
- **Long dictations are not lost on Windows (built and tested on Linux).**
  Each dictation is written to disk while you speak. One the speech engine
  could not transcribe, or that a quit or crash cut short, is kept for
  Recover last dictation, in the window and the menu, for 7 days at most. A
  microphone that is unplugged or stops ends the dictation and its words are
  typed, and Escape asks before it discards more than 30 seconds of speech.
- **Five-minute dictations, and silence stops, on Windows (built and tested
  on Linux).** A dictation runs up to five minutes, with a sound and a
  countdown over its last minute. Hands-free dictation stops after 30 seconds
  of silence (60 seconds or never, in the window), or after 15 seconds when
  nothing was said; a held key is let go after a minute without speech. Each
  of these stops types what was said, or keeps the recording for Recover last
  dictation when it could not be transcribed.
- **Typing into any app.** The words go into the focused field through the
  clipboard and a paste, and the clipboard you had is put back afterwards.
  When the app refuses the paste, the words stay on the clipboard; Copy last
  text recovers the latest dictation even when the clipboard failed too.
  Password fields are refused, before the microphone opens and again before
  the paste.
- **Vocabulary that learns from your corrections.** After a dictation the app
  watches that text for up to 90 seconds; a distinctive word you fix is
  learned, and a sound-alike rewrite after the same fix on two dictations.
  Learning is reversible, ranked by the kind of app you dictate into, and
  listed in the window, where you can add, remove or undo entries. It can be
  switched off.
- **Spoken aliases, formatting and languages.** Your own spoken forms can map
  to spellings you choose. Sentences start with a capital, except in
  terminals, which get no formatting. Automatic language detection plus ten
  languages to choose from.
- **Privacy by design.** No account. The Mac app keeps its history encrypted
  on the Mac, as above; the Windows app keeps no transcript history, only its
  latest result, in memory. The Mac app removes a successful or cancelled
  take's spool file once its words are in (its recording, when kept, lives on
  only in the encrypted history), and keeps at most one failed or interrupted
  take for 7 days; the Windows app keeps the same one-failed-take policy.
  Logs record outcomes and timings, never dictated words.

### Known limitations

- A dictation stops after 600 seconds on the Mac and five minutes on Windows.
- No update channel: download a newer image from GitHub Releases yourself.
  There is no Windows or Intel Mac download.
- The Windows app has never run on Windows hardware, and Windows on ARM has
  no speech engine yet.
