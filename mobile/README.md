[← Dictation README](../README.md)

# Mobile adapter boundary

Mobile is planned, not implemented. Reuse the request/result semantics and
synthetic fixtures; do not assume the desktop Python process, clipboard,
global hotkeys, AppKit or Win32 input path runs on a phone.

- Android: an `InputMethodService` receives an `InputConnection`, and commits
  recognized text through that connection. Capture needs Android permission and
  lifecycle handling. See [Create an input method](https://developer.android.com/develop/ui/views/touch-and-input/creating-input-method).
- iOS: a keyboard extension and its containing app are separate processes with
  different permissions. Apple's [custom keyboard guide](https://developer.apple.com/library/archive/documentation/General/Conceptual/ExtensibilityPG/CustomKeyboard.html)
  documents that keyboard extensions cannot access the microphone. Prototype
  a containing-app capture journey before promising system-wide dictation;
  recheck current SDK behavior and distribution rules on an actual device.

Each adapter must preserve cancellation, secure-field refusal, explicit
permissions, no persistence by default, and text insertion only into the
intended active input session. Sharing a schema does not establish those
properties. On-device ASR must have a mobile-native provider and measured memory,
latency, battery and interruption behavior; a cloud provider requires an
explicit user choice and data-egress disclosure.

Desktop protocol reference: [docs/architecture.md → Worker protocol and
lifecycle](../docs/architecture.md#worker-protocol-and-lifecycle), and in code
`runtime/transcription_worker/asr_server.py`,
`macos/Sources/DictationKit/DictationAsrTransport.swift`, and
`windows/Dictation.Speech/AsrWorkerHost.cs`. Version or normalize a new
mobile boundary alongside contract fixtures before adding a third adapter.
