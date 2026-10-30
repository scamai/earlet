# Getting help

Start with the setup checks below, or choose the matching reporting channel.
Earlet is a 0.x preview: a notarized download for Apple Silicon Macs, and
source for everything else. Support is
best-effort, with no support contract or guaranteed response time. Windows
source has not been exercised on Windows hardware.

## Common setup problems

| What you see | What to check first |
|---|---|
| You are looking for an installer | Download the Mac disk image from [Releases](https://github.com/scamai/earlet/releases/latest) (Apple Silicon, macOS 14 or later), open it and drag Earlet to Applications. There is no Windows or Intel Mac download; [building from source](README.md#quick-start) needs the toolchain, Python dependencies and a local model. |
| The app opens but cannot recognize speech | An app-only package contains no speech runtime or model. Check the [complete-package requirements](docs/development.md#packaging) and [model format](docs/development.md#model-formats). |
| Holding the key does nothing on the Mac | Check Microphone, Accessibility (Device Control and Data Access on macOS 27 and later) and Input Monitoring in System Settings. Quit and reopen the app after granting Input Monitoring. Check the hold key shown in Home. |
| Text is recognized but does not appear in the other app | Focus an editable text field and check Accessibility (Device Control and Data Access on macOS 27 and later). Use **Copy last text** to paste manually. Password fields and Secure Input are intentionally protected. |
| A rebuilt Mac app stopped responding to the key | Rebuilding with ad-hoc signing can invalidate permission grants. See [Signing](docs/development.md#packaging); note the build and the permissions shown when reporting it. |

If the check does not help, use the channels below. A report should say which
step you tried and what happened, without including private dictated text.

## Where to go

| You have | Go to |
|---|---|
| A question about using or building the apps | [Discussions](https://github.com/scamai/earlet/discussions) |
| An idea or a feature request | [Discussions](https://github.com/scamai/earlet/discussions) |
| Something broken | [A bug report](https://github.com/scamai/earlet/issues/new?template=bug_report.yml) |
| Words recognised wrongly | [A recognition report](https://github.com/scamai/earlet/issues/new?template=recognition_report.yml) |
| A security problem | Never a public issue: [SECURITY.md](SECURITY.md) |
| A Code of Conduct concern | [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md#enforcement) |

Search the existing issues and discussions first; adding what you saw to an
open issue helps more than a duplicate.

## What to include

- **The version and build.** The app's window shows both under
  Troubleshooting, with the path of the copy that is running.
- **Your computer:** macOS or Windows version, and the Mac or PC model.
- **The hold key** you use, **the app you dictated into**, and **your
  microphone** and how it is connected (built-in, USB or Bluetooth).
- **What you did, what you expected and what happened instead.**
- **A log excerpt** from around the time it happened. Open the log folder
  from the app (Open Log Folder on macOS, Open log folder on Windows), or
  find it here:
  - macOS: `~/Library/Logs/Dictation/dictation.log`
  - Windows: `%LOCALAPPDATA%\Dictation\logs\dictation.log`

  The logs record what happened and how long it took, never the words you
  dictated. On macOS they include text lengths and longest repeated-character
  runs before and after formatting, to help trace intermittent garbled output.
  These counts do not verify the text displayed by the receiving app.
  They can name the app you dictated into (on Windows by its
  program name, such as WINWORD), and on Windows the title of a window whose
  app could not be identified, which may name a document. Read the excerpt
  before you paste it, and remove anything you would rather not share.

For intermittent repeated characters, note the time and the build shown in
Troubleshooting. Before the next dictation replaces it, compare Show text in
the app with the text in your destination. The Mac log's `asr_repeat` and
`candidate_repeat` counts help locate where the repetition first appeared;
`clipboard_unchanged` describes restoration of the previous clipboard, not
verification of the pasted words. You do not need to share the full text.

For a slow dictation, the same line's `asr_wait_ms`, `asr_decode_ms`,
`asr_windows`, `asr_retries` and `asr_warm` say whether the transcription
waited for the live preview, re-decoded hard-to-hear audio, or ran on a speech
engine that had just loaded.

Do not attach your vocabulary file (`learning.json`): it holds the words you
taught the app or corrected. Never attach a recording of someone else's
voice.
