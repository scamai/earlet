# Security policy

Earlet is a hold-to-talk dictation app. It listens for a hold key in every
app, records the microphone while the key is held, recognises the speech on
the computer and types the words into the app in front. That takes some of
the most sensitive access a desktop app can have, so security reports are
welcome and handled first.

- [Supported versions](#supported-versions)
- [Reporting a vulnerability](#reporting-a-vulnerability)
- [What happens after you report](#what-happens-after-you-report)
- [Scope](#scope)
- [Out of scope](#out-of-scope)
- [Security model](#security-model)

## Supported versions

Only the latest 0.x release gets security fixes. The latest, 0.2.0, is a
notarized download for Apple Silicon Macs that updates itself from then on;
a copy of 0.1.0 has to be replaced by hand once. The Windows app is source only:
it has been built and tested on Linux and has never run on Windows hardware.
Security fixes land on the default branch first.

## Reporting a vulnerability

During private preparation, use the maintainer contact through which you
received repository access. GitHub private vulnerability reporting is a
public-repository feature; the form below becomes the reporting channel
when it is enabled at public launch. Do not include vulnerability details
in an issue while requesting a contact.

Report it privately through GitHub's private vulnerability reporting:
[Report a vulnerability](https://github.com/scamai/earlet/security/advisories/new),
under this repository's Security tab. Only the maintainers can read it. Do
not open a public issue, discussion or pull request for a vulnerability.

A useful report gives:

- the platform, the OS version, and the app's version and build, which the
  app's window shows under Troubleshooting;
- what the attacker controls and needs: another app in the same user
  session, clipboard contents, a document the user dictates into, a model
  folder or Python runtime, a local account, a network position;
- which permission the attack relies on (on macOS: Microphone,
  Accessibility or Input Monitoring);
- the steps to reproduce it, and what you saw.

Describe recordings and the contents of your vocabulary file rather than
attaching them, and never attach a recording of someone else's voice.

## What happens after you report

A maintainer acknowledges every report within 48 hours. After that we keep
you informed while we investigate and fix it, agree a disclosure date with
you, and credit you in the published advisory unless you prefer not to be
named.

## Scope

Everything in this repository is in scope: the macOS and Windows apps, the
speech worker (`runtime/`) and the packager (`tools/package.py`). These parts
carry the most risk:

- **Key listeners.** On macOS a listen-only event tap watches modifier keys
  for the hold key (this needs Input Monitoring). A key combination the user
  records as the hold key is registered with macOS as a hot key instead, and
  the app is told about that combination only. While Settings records a hold
  key or a mouse button, a monitor local to Earlet's window reads the next key
  code, modifier change or extra mouse button pressed there, and nothing typed
  or clicked in other apps. When the user
  chooses a mouse side button, an active event tap receives the extra mouse
  buttons' presses (never left or right click) and consumes the chosen
  button's. While a dictation runs, a
  key-down monitor reads key codes, never characters, to tell a shortcut,
  Escape or a stop key apart. Only while a hands-free latch records does an
  active event tap consume the stop keys (Space, Return, keypad Enter). On
  Windows a low-level keyboard hook (`WH_KEYBOARD_LL`) is installed while
  dictation is turned on, so it receives every key event in the session, not
  only the hold key. It compares virtual-key codes, and stores and logs none
  of them; it passes every key on, except Space and Enter while a hands-free
  latch records, which it consumes. During the correction watch it also notes
  that a key went down, and in which app, but not which key. A way to make
  either app keep, log, leak or consume keys beyond that is in scope.
- **Paste helpers and the clipboard.** Both apps put the words on the
  clipboard, send the paste keystroke (Command-V, Ctrl+V) to the focused app,
  and then put back what the clipboard held, unless something else wrote to
  the clipboard meanwhile. Windows marks its own clipboard writes to stay out
  of clipboard history and cloud sync. Pasting into an app the user did not
  have in front, losing or leaking the user's clipboard, or a restore that
  overwrites a newer copy are in scope.
- **Password fields.** Both apps refuse to type into a password field: they
  check when the key goes down and again just before the paste, and the Mac
  also refuses while Secure Input is on. A way past that refusal is a
  vulnerability.
- **The correction watch.** After a paste, the app reads the field it typed
  into for up to 90 seconds to learn the words the user corrects (macOS
  Accessibility, Windows UI Automation). Reading another field or app, or a
  password field, is in scope.
- **The worker and its channel.** Speech is recognised by a Python child
  process. Requests and replies are length-prefixed JSON frames over its
  standard input and output; on Windows the replies come back over a named
  pipe that only the current user can open. The worker starts isolated from
  the user's Python environment (`-E -s` with every inherited `PYTHON*`
  variable dropped on macOS, `-I` on Windows). Injecting frames, code or
  modules into it is in scope.
- **Files the apps write.** The vocabulary file (`learning.json`) keeps the
  words the user taught or corrected in plain text, with counts and times,
  never a whole dictated sentence or audio. Settings, the user's spoken
  aliases and the logs sit beside it. The logs record outcomes and timings,
  never dictated words; a dictated word in a log is a bug worth reporting.
  On macOS, every take is spooled as raw PCM16 under
  `~/Library/Application Support/Dictation/dictation/recovery` while it
  records. A successful insert, text fallback or cancellation removes it;
  one failed or interrupted take remains visible for Recover last dictation
  for at most 7 days, and failed deletion is retried and remains visible.
  The Windows app also writes the audio of each dictation, raw and
  unencrypted, to `%LOCALAPPDATA%\Dictation\recovery` while it records, and
  deletes it once the words are in, it held nothing to type, or it was
  discarded. At most one whose words did not go in (the speech engine could
  not transcribe it, a shortcut ended it after 30 seconds of speech, or a
  quit or crash cut it short) stays there for Recover last dictation until it
  is recovered or discarded, and for 7 days at most. A recording kept longer
  or written anywhere else is a bug worth reporting.
- **The macOS history and its key.** Each dictation whose words were delivered
  is saved under `~/Library/Application Support/Dictation/dictation/history`
  (directories 0700, files 0600): one file per dictation and one per kept
  recording, each sealed whole with AES-256-GCM, with the file's kind and the
  dictation's id authenticated with it. The key is 256 random bits, made at
  the first save and kept only in the login keychain as a generic password
  that does not sync. Without the key the history reads as locked and nothing
  is saved; it never falls back to plain text, and a keychain that refused is
  not asked again until Try Again. A dictation saved by a newer version of the
  app, which this one cannot read, is kept as it is rather than offered for
  deletion. A dictation refused for a password field or Secure Input is never
  saved, nor is a spoken command, a cancelled dictation or one into
  Earlet's own window (its practice field, or a field in its Vocabulary or
  History). Settings sets how long the words are kept (7 days, 30 days, 1 year
  or forever, forever by default) and the recordings (none, 7 or 30 days,
  30 days by default, never longer than their words and 1 GiB at most, the
  oldest going first), and can turn the history off. Delete All History destroys the
  key before it deletes the files, so a copy of them, or a block the file
  system has not yet reused, cannot be opened. A backup made before the
  delete, by Time Machine or as a local snapshot, holds the login keychain as
  well as the files, so the history in it can still be opened from that
  backup. Reading the history without the key, making the app write a
  dictation or a recording in the clear, or getting a refused dictation saved
  is in scope. The file names are random ids, but the file system's own times
  and sizes still show when each dictation was saved and roughly how long its
  words and its recording are, though not what was said.
- **The bundled runtime and model.** A complete package carries a Python
  distribution, its packages and a Whisper model. Weaknesses in how they are
  pinned, staged, signed or loaded are in scope.

## Out of scope

- Vulnerabilities in third-party components themselves: report them
  upstream, and tell us if the way this project uses one makes it
  exploitable.
- Attacks that need administrator or root rights, an already compromised
  user account, or physical access to an unlocked computer.
- Local denial of service, such as making the app or the worker quit.
- What another app does with the words after they are pasted into it.
- Scanner output with no demonstrated impact.

## Security model

- Recognition runs on the computer, in the bundled worker. The apps need no
  account. The Windows app keeps no transcript history and holds only its
  latest result, in memory; the macOS app also keeps the encrypted history
  described under The macOS history and its key. The macOS app keeps one
  failed or interrupted recording only as described under Files the apps
  write; the spool files of successful and cancelled takes are removed. The
  Windows app keeps one only as described there.
- The worker is started with the Hugging Face offline switches set and told
  never to download a model; it loads the model folder the package carries.
  The `DICTATION_PYTHON`, `DICTATION_RUNTIME` and `DICTATION_MODEL_DIR`
  environment variables can point the worker at another interpreter, runtime
  or model: on macOS only in a debug build (a release app keeps them inside
  its bundle), on Windows in every build.
- The macOS app checks for updates once a day: one HTTPS request to
  github.com for the release feed, which shows GitHub the address it comes
  from and Earlet's version; Settings → Update Earlet automatically turns it
  off. It installs an update only when the archive's EdDSA signature matches
  the public key built into the app and the new app is signed by the same
  Developer ID team, and it installs it in place, so the app's permissions
  and keychain item carry over
  ([Updates](docs/design-macos.md#updates)). One measured check opened that
  single connection and no other in the twelve seconds around it; that is
  one observation, and the Windows app's traffic has not been measured. An
  outbound connection you did not expect is worth reporting.
- On Windows the keyboard hook does not receive keys typed into a window
  running as administrator (Windows' user interface privilege isolation), so
  the app can neither hear the hold key nor type there. That is a limit of
  the platform, not a vulnerability.
