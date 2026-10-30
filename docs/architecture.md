<!-- English only -->

[← Earlet README](../README.md)

# Architecture and provenance

How Earlet is put together: the two native apps, the recognition worker they
drive, what the packages carry beyond what the apps use, where the code came
from, and what a rename has to touch. Building and packaging are in
[development.md](development.md); the UI contract is in
[DESIGN.md](../DESIGN.md).

- [Components](#components)
- [Worker protocol and lifecycle](#worker-protocol-and-lifecycle)
- [What ships beyond the entry points](#what-ships-beyond-the-entry-points)
- [Source provenance and maintenance](#extraction-provenance)
- [Renaming the product](#renaming-the-product)

## Components

Each desktop app is a native shell around the same Python recognition worker,
which runs as a child process and talks to the app over framed JSON. Neither
app is registered as an input method; both insert through the focused
application.

**Windows** (`windows/`, .NET 9). `Dictation.App` is the WPF entry point and
builds `Dictation.exe`: the window, the notification-area icon (a Windows
Forms `NotifyIcon`, the only Windows Forms use), the floating indicator window,
the UI Automation adapters for the correction watch and the password-field
probe (skipped while Earlet's own window is in front, which has no password
field), sign-in launch, and the single-instance claim (a named mutex plus a
show event, so a second launch brings the first window forward; a start that
fails exits and lets the mutex go). `Dictation.Core` holds the app's logic
with no WPF in it, so it builds and is tested on Linux: the controller, the
worker slot, settings, copy, the indicator's feed, placement and level bars,
the correction watch, the Vocabulary view model with its correction notices,
the notification-area menu's model and commands (`DictationTrayMenu.cs`; the
WinForms icon only draws it), the live preview (its `DICTATION_PREVIEW` switch,
the controller's wiring, the worker slot's streams and the caption's fit on the
pill), the render modes' scenarios, inert parts, check and report
(`DictationWindowRender.cs`, `DictationRenderPlatform.cs`,
`DictationHudRenderFrames.cs`, `DictationHudRenderCheck.cs`,
`DictationRenderReport.cs`; the drawing is
`Dictation.App/DictationRenderMode.cs`), and `Learning/`, the policy and store. Four
more projects supply the rest: `Dictation.Services` (the dictation session,
hold keys, formatter, focused-text inserter, span observer, the live-preview
session and its caption window), `Dictation.Infrastructure` (the low-level
hold-key hook and the clipboard snapshot), `Dictation.Speech` (the worker
host, transcriber and wire types, the microphone tap) and
`Dictation.Platform` (the worker process, WASAPI capture, the Win32
clipboard). `Dictation.App` references `Dictation.Core`,
`Dictation.Infrastructure` and `Dictation.Platform`, and through them
`Dictation.Services` and `Dictation.Speech`; all six assemblies ship.
The Windows assemblies share the same `Dictation*` naming so the native app,
tests, and package can be built independently.

**macOS** (`macos/`, SwiftPM, Swift 6.2, macOS 14). `DictationApp` builds the
`Dictation` executable: the SwiftUI window, the menu bar extra, the
indicator's AppKit controller and the offscreen render modes.
`DictationAppCore` is the app's behaviour as a library, so `swift test`
reaches it with fakes: the model, hold key and latch, live preview, recent
result, vocabulary, permission setup, settings, timing and the log.
`DictationLearning` is the learning policy and store, Foundation only, and is
also tested on Linux. `DictationHistory` is the dictation history's sealed
files, store and keychain key (CryptoKit and Security, nothing else of the
app's); `macos/tools/history-mutations.py` checks that its tests notice each
guard removed. The copied targets are `DictationKit` (capture session,
worker transport, text insertion, correction observer, formatter, Secure Input
monitor), `DictationIPC` (the worker process client) and `DictationAudioCapture`
(microphone capture with automatic device selection, carrying the D13-D15
Bluetooth fixes). Unused meeting capture, identity/timeline models and the
experimental system speech backend have been removed. One instance at a time comes from the
packaged `Info.plist` (`LSMultipleInstancesProhibited`), and the single
SwiftUI `Window` scene keeps one window.

**Worker** (`runtime/transcription_worker/`). `asr_server.py` reads framed
requests and dispatches ops; `whisper_mac.py` decodes with MLX on the Mac and
faster-whisper (CTranslate2) on Windows; `learned_vocabulary.py`,
`context_vocabulary.py`, `spoken_aliases.py` and `mlx_beam.py` apply the
learned vocabulary ([LEARNING.md §6](../LEARNING.md#6-what-the-worker-does-with-it));
`ops_stream.py` and `ops_stream_whisper.py` serve the live preview: the Mac's,
and the Windows one when `DICTATION_PREVIEW=1`.

**Files each app writes**

| What | macOS | Windows |
|---|---|---|
| Settings | macOS UserDefaults domain and `dictation.*` keys (hold key, on/off, language, learning switch, first-dictation and relaunch flags) | `%LOCALAPPDATA%\Dictation\settings.json` (hold key, on/off, language, learning switch, tray notice, first-dictation flag). A file that cannot be read or does not parse is left byte for byte: that run uses the defaults and refuses every save until a later launch reads the file. An empty file counts as missing |
| Learned vocabulary and the user's aliases | `~/Library/Application Support/Dictation/dictation/learning.json` and `bias_aliases.json` | `%LOCALAPPDATA%\Dictation\dictation\learning.json` and `bias_aliases.json` |
| Log | `~/Library/Logs/Dictation/dictation.log` plus two rotated generations, rotated at 5 MiB, mode 0600; the app's stderr is copied into it | `%LOCALAPPDATA%\Dictation\logs\dictation.log` plus one previous generation, `dictation.1.log`, rotated at 4 MiB; Information and above |
| Recordings | `~/Library/Application Support/Dictation/dictation/recovery`: PCM16 while a take records; removed after successful insertion, text fallback or cancellation. One failed or interrupted take is retained for recovery with a seven-day expiry, checked at launch and during runtime housekeeping. The directory is created with mode 0700 and files with mode 0600; audio is not encrypted by the app ([Recovery](design-macos.md#recording-silence-and-recovery)) | `%LOCALAPPDATA%\Dictation\recovery`: `take-<id>.pcm`, 16 kHz 16-bit mono, written while a take records and deleted once its words went in, it was cancelled or it held nothing to type; `kept.pcm`, at most one take whose words did not go in, for Recover last dictation, deleted once its recovered words are copied, on Discard, or at 7 days old ([Long takes are never lost](design-windows.md#long-takes-are-never-lost)) |
| History | `~/Library/Application Support/Dictation/dictation/history`: `entries/<id>.sealed` per delivered dictation and `audio/<id>.sealed` per kept recording, each sealed with AES-256-GCM under a 256-bit key kept only in the login keychain (service: the settings domain followed by `.history`). Directories 0700, files 0600. Words kept forever and recordings 30 days by default, 1 GiB of recordings at most ([History](design-macos.md#history)) | None |
| Worker cache | `~/.cache/dictation/mlx_whisper_repos`: symlinks that let an export with `model.safetensors` load | Not used (the MLX engine only) |
| Login launch | An `SMAppService` login item | The `HKCU` Run value `Dictation` |

Neither log holds dictated words. The worker's own stderr can name words from
the user's vocabulary once the model is loaded, so both apps record it only
while the worker starts. The `learning.json` contract, and what may enter it,
are in [LEARNING.md §5](../LEARNING.md#5-the-file-dictationlearning1) and
[§7](../LEARNING.md#7-observers-and-privacy).

## Worker protocol and lifecycle

**Frames.** Each frame is a 4-byte big-endian length followed by UTF-8 JSON,
in both directions. The Mac writes requests to the worker's stdin and reads
replies on its stdout. Windows writes requests to stdin and reads replies on a
current-user-only named pipe it names in `DICTATION_ASR_PROTOCOL_PIPE`; the
worker's stdout is then only a diagnostics channel, so a native library that
writes to stdout cannot corrupt a reply. The worker answers a malformed frame
or an unknown op with an error and keeps running; `PROTOCOL_VERSION` is 2.

**Ops.** Both hosts send `init` and `transcribe`. The Mac sends `shutdown`
before it stops a loaded worker, and kills it when that takes longer than
3 s; it also sends `stream_start`, `stream_push` and `stream_cancel` for the
live preview. Windows sends no `shutdown`: `DictationWorkerSlot` stops the
worker through the copied host's synchronous `Dispose`, which kills the
process at once, and the host's polite path (`AsrWorkerHost.DisposeAsync`,
a `shutdown` op and then a wait for the exit) is never called. Windows sends
the three stream ops only with `DICTATION_PREVIEW=1`, with the Mac's
`stream_start` fields, and never `stream_finish`. Windows sends `ping`
every 10 s during a take longer than 30 s, with 5 s to answer
([Long takes are never lost](design-windows.md#long-takes-are-never-lost));
the Mac also sends it during long holds, and the packager's import check calls it in-process. The worker also
registers `score`, `score_release` and `rescore` for learning tools, which
neither app wires in.

**`backend: mac`.** Both hosts send `backend: "mac"` in `init`. It is the
protocol's historical name for the portable Whisper backend and says nothing
about the platform or about an NPU. The engine is pinned by
`DICTATION_ASR_ENGINE`: the Mac sets `mlx` (Apple GPU, Metal) and Windows
`faster` (faster-whisper on the CPU). Both send the model folder as the model
and `allow_model_download: false`.

**Launch.** Both hosts add `HF_HUB_OFFLINE=1`, `TRANSFORMERS_OFFLINE=1`,
`OMP_NUM_THREADS=4`, `OPENBLAS_NUM_THREADS=1` and `MKL_NUM_THREADS=1` to the
worker's environment.

- **macOS:** `<python> -E -s -B -u -m transcription_worker.asr_server`, run
  from the runtime directory, with every inherited `PYTHON*` variable dropped
  and `PYTHONNOUSERSITE=1`, `PYTHONUNBUFFERED=1` and
  `PYTHONDONTWRITEBYTECODE=1` forced. `-m` keeps the runtime directory on
  `sys.path`, which is safe in a release build only because a release build
  honours a runtime override only inside its bundle. Using `-I` would also
  remove this module search path, preventing the launcher from finding the
  worker module; the flags above ignore inherited Python settings and user
  site packages while preserving the bundled module path.
- **Windows:** `<python> -I -B -u <runtime>\transcription_worker\asr_server.py`.
  Isolated mode drops the working directory from `sys.path`; the runtime gets
  onto it from the `._pth` file the packager writes beside the embeddable
  Python.

**Language and long holds.** Both apps apply a new language at the next hold
without restarting the worker: the engine takes the language with each
request (on Windows, `DictationWorkerSlot.Language`, read by every transcribe
frame; a later start launches with it too). With the
language left on Automatic, a hold of about 12 s or more is decoded one span
at a time on both engines, so each span gets its own language decision. A span
ends at the first pause of 0.6 s or more that leaves 6 s on each side, or at
24 s of unbroken speech (that cut can fall inside a word); a shorter hold, or
one of at most 24 s with no such pause, is one span. On Windows this is new
and its effect on recognition is not measured.

**Deadlines.**

| Wait | macOS | Windows |
|---|---|---|
| Start (spawn and model load) | 120 s | 90 s for `init` |
| Recognition attempt | 30 s plus the audio length; after a transport/deadline failure, one fresh-worker retry with 30 s plus twice the length | 30 s plus the audio's length; once the worker died or missed it, one retry with 30 s plus twice the length |
| Polite shutdown before a kill | 3 s | None: the worker is killed |
| Ending a live preview | 2 s | 2 s, with `DICTATION_PREVIEW=1` |
| Longest hold, then transcribe | 600 s, with a 60 s countdown | 300 s, with a 360 s memory cap behind it ([hold policy](design-windows.md#how-long-a-hold-listens-on-windows)) |
| Silence that ends a hands-free hold | 30 s (60 s or Off in the window); 15 s before speech unless Off | 30 s (60 s or never in the window); 15 s with nothing heard |

**Restart.** The two apps differ.

- **macOS:** a failed transport or missed recognition deadline restarts the
  worker and retries the same complete recording once; an engine-reported
  decoding error is not blindly retried. A second failure keeps the PCM16
  take for Recover last dictation. During holds longer than 30 seconds,
  a health ping runs every 10 seconds, respecting the initial 120-second load
  deadline; two failed restarts end the take. A microphone fault ends capture
  and transcribes the retained prefix instead of mixing audio across devices.
  The spool is private to this product, deletes successful/cancelled takes,
  and keeps at most one failed/interrupted take for seven days. Recovery
  copies text rather than pasting into a field chosen after the failure.
- **Windows:** a stopped worker gets up to 3 restart attempts, 2 s apart. A
  dictation that reached the recogniser and came back resets the count. When
  the attempts are spent, the worker is unloaded and the window and the
  indicator say that speech recognition could not restart and ask the user to
  pause and enable (the indicator waits for the release when the attempts run
  out while a hold listens); the next press still tries once. A stop caused by a cancel
  restarts quietly; a language change restarts nothing. A release whose
  worker dies under the decode or misses its deadline is not lost: the slot
  starts the worker again and retries the decode once with the longer
  deadline, and a take that still fails is kept for Recover last dictation.
  During a take longer than 30 s the worker is pinged every 10 s and started
  again while the user speaks; two failed restarts end the take
  ([Long takes are never lost](design-windows.md#long-takes-are-never-lost)).

**Idle unload.** Both apps unload the model once it has gone unused for a
while; the next press starts the worker again while the user speaks. Windows
waits 180 s, checked on its 30 ms housekeeping tick. The Mac learns the wait
from the user's own pauses between dictations
(`DictationWorkerKeepAlive`): the 99th percentile of the last 200 pauses of
30 minutes or less, plus 10%, kept between 3 and 30 minutes, and 10 minutes
until 20 pauses have been seen. Only the pause lengths are kept, in the app's
preferences (`dictation.worker.pauses`). A macOS memory-pressure warning
halves the wait, down to a quarter of the learned one and never under 3
minutes, and applies it at once; a critical warning also unloads an idle model
at once. Each later dictation gives back an eighth. The Mac checks every 5 s
and logs each pause, unload and pressure event as a `keep-alive` line of
numbers. The Mac also closes its warm
microphone once a dictation has ended and nothing used it for 3 s with a
built-in, wired or USB microphone, or 10 s with a Bluetooth one, checked every
0.5 s from the end of the dictation.

## What ships beyond the entry points

The source tree is wider than what the two apps use. A compiled assembly can
contain types the entry point never reaches. Check package contents and runtime
reachability separately when changing what ships; the worker's module list and
native project references define the package boundary.

**Still in the Windows assemblies, unreached.**

- The session's routing seam: `DictationSession` asks an `ITranscriptRouter`
  (`TranscriptRouter.cs`) whether a hold's words are typed, and the indicator
  has lines for a hold a router ran or could not match. The app composes no
  router, so every hold is typed. `DictationSpokenCommandTests` pins the
  session's side, including a cancel that lands while the router decides.
- `HudTone`'s two verdict colours, `Genuine` and `Deepfake`, which nothing
  paints; `DictationHudTests` fails if dictation ever paints them.
- `WorkflowHotkeys` in `PushToTalk.cs`: the old Ctrl+Alt+Space chord and its
  Ctrl+Alt+Shift+digit fallbacks. Nothing registers either.
- The learning protocol's `score` and `rescore` wire types
  (`AsrLearningProtocol.cs`) and the worker ops behind them in
  `asr_server.py`. The native apps do not send them.
- The formatting-exception list the dictation session still accepts; the app
  passes none.

These are implementation seams, not user-facing features. Their presence in
the source tree does not imply that a packaged app exposes them.

The Mac contains no meeting identity/timeline module, system-audio capture,
experimental system speech backend, Foundation Models formatter or outcome
ledger. These had no product callers. Tests dedicated to the removed meeting
rebind policy and outcome ledger were retired with them; tests of the live
microphone, recent-result and learning paths remain. Copy last text keeps its
one result in memory on both platforms.

**The worker package.** `tools/package.py` stages only the modules the
ASR server imports on the paths the two hosts drive: transcription, learning,
and the live-preview stream ops (`ops_stream*`), which the Mac sends and
Windows sends only with `DICTATION_PREVIEW=1`. That is nineteen modules
(`WORKER_MODULES`) plus one data file, `default_aliases.json` (`WORKER_DATA`).
The shipped alias table is empty and product-neutral, and the worker is its
only reader on both platforms. `WORKER_OPTIONAL` names each remaining source-only
utility a shipped module imports but the package leaves out, such as optional
caption processing and the openai-whisper trie decoder. The staged dictation
path guards those imports. `tests/test_package.py` fails when a shipped module
gains an import that is neither shipped nor declared optional, and exercises
recognition and a preview commit from the staged package.

Model-backed meeting summaries, speaker diarization, voice enrolment and the
Snapdragon backend have been removed. Both native hosts initialize the `mac`
wire adapter, pinned to MLX or faster-whisper; the legacy adapter name is kept
for compatibility. Pure stream utilities remain source-only and are not app
features.
Tests, benchmarks, corpora, model weights, and user recordings are not bundled
by an app-only package. A complete preview package adds its selected local model
and Python runtime; the public source repository includes the tests. On macOS
the packager first removes from that Python what no shipped module imports,
torch, pip and Tcl/Tk among it
([Packaging](development.md#packaging)).

<a id="extraction-provenance"></a>
## Source provenance and maintenance

The public repository receives verified, independent source snapshots. It
contains the native clients, worker, packaging scripts, tests, and documentation
needed to build them. The contribution and attribution process is described in
[CONTRIBUTING.md](../CONTRIBUTING.md#how-a-pull-request-lands); licensing is in
[NOTICE](../NOTICE) and [REUSE.toml](../REUSE.toml).

When moving or removing code, update imports, package manifests, tests, and the
relevant documentation together. Use `python3 tools/verify.py` to check the
standalone boundary and `python3 tools/run_tests.py --list` to select suites.
The public tree omits internal provenance ledgers, so its boundary summary says
that extraction provenance was not checked. Generated build output, models,
recordings, and user data stay outside version control.

## Renaming the product

Earlet's display name is separate from the identifiers that existing previews
use for settings, vocabulary, history keys and OS permissions.

- `product.json` supplies the visible name, version and existing bundle ID.
  The packager names its output `Earlet.app` on macOS and `Earlet/` on Windows,
  and writes Earlet to the display metadata and microphone prompt.
- Native executable and module names stay `Dictation*`, including the Mac
  executable `Dictation` and Windows `Dictation.exe`. The package build and
  embedded-resource names must continue to match those targets.
- Existing settings domains, Windows single-instance and sign-in identifiers,
  data and log directories, and history keychain identity remain unchanged.
  Changing them requires a separate migration that preserves existing data.
- Window titles, tray and menu text, permission guidance and other visible
  sentences use Earlet. The shared copy lives in `DictationCopy.swift` and
  `DictationCopy.cs`; some native surfaces own their own text.
- Public links, acceptance defaults and package instructions must match the
  selected name. The [identity guide](brand.md) describes the shared native
  mark and reproducible app icon.

The worker's `"dictation"` client tag and `DICTATION_*` environment variables
are technical interfaces. A display-name change does not rename them.
