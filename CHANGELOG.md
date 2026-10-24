# Changelog

All notable changes to Earlet are recorded here, newest first, for people
who use the apps. The format follows
[Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/), and versions
follow [Semantic Versioning](https://semver.org/). A change a user would
notice adds a line under Unreleased in the same pull request.

## [Unreleased]

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

Nothing has been released yet. This section summarises what the source
builds today. The Windows app has been built and tested on Linux only; none
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
- No signed, notarized or installable release exists yet, and no update
  channel.
- The Windows app has never run on Windows hardware, and Windows on ARM has
  no speech engine yet.
