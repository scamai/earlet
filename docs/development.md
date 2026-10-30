<!-- English only -->

[← Earlet README](../README.md)

# Development and packaging

How to build, test and package Earlet, and every `DICTATION_*` environment
variable in the tree. Run commands from the repository root (`apps/dictation`
in the development monorepo). The test
commands below cover the repository's automated checks; how the pieces fit
together is in [architecture.md](architecture.md).

- [Prerequisites](#prerequisites)
- [Running the tests](#running-the-tests)
- [Building the Windows entry point](#building-the-windows-entry-point)
- [Packaging](#packaging)
- [Releasing a Mac build](#releasing-a-mac-build)
- [Environment overrides](#environment-overrides)
- [Model formats](#model-formats)

## Prerequisites

- **Windows code:** the .NET 9 SDK. The projects target `net9.0` and
  `net9.0-windows`, and `windows/Directory.Build.props` turns on Windows
  targeting, so Linux and macOS build them too; only Windows can run the WPF
  app.
- **Mac code:** Swift 6.2 or later on macOS 14 or later with Apple Silicon
  (`macos/Package.swift`). Its one external package, Sparkle 2.9.6, is pinned
  to that exact release in `Package.swift` and `Package.resolved`, and
  SwiftPM downloads it from GitHub on the first build; `tools/verify.py`
  refuses any other package, a version range or a path. Only the app's
  executable links it, so `swift test` does not load it. The platform-free
  learning target also builds on a Linux Swift 6.2 toolchain, without it.
- **Test tools:** Python 3.11 or later with `tests/requirements.txt` (numpy
  and pytest, pinned; the numpy pin has no wheel for an older Python) for the
  Python gate. The opt-in full worker suite also needs torch, onnxruntime and
  openai-whisper.
- **A complete voice package** also needs a relocatable Python distribution
  with the speech provider installed, and a matching local Whisper model
  ([Model formats](#model-formats)). The provider inputs are
  `runtime/requirements-windows.txt` (CPython 3.11, win_amd64: numpy and
  faster-whisper) and `runtime/requirements-macos.txt` (CPython 3.11, arm64,
  macOS 14: numpy and mlx-whisper). Each file pins the full resolved closure
  with `==`, and `tests/test_package.py` fails on an unpinned line. They carry
  no hashes, and the Python distribution itself is not pinned. `onnxruntime`
  (Windows) is pinned only as a transitive dependency the worker never
  imports. mlx-whisper declares `torch` but imports it only in its checkpoint
  converter, so the macOS file leaves out torch and the packages only it
  pulls in: install it with `pip install --no-deps -r`, and the packager
  prunes them from any Python it is given anyway
  ([Packaging](#packaging)). Each file's header gives the `uv pip compile`
  command that regenerates it. Windows ARM64 has no provider.

Packaging never downloads a model for the user and does not call any external
model-update service.

## Running the tests

```bash
python3 tools/run_tests.py --list         # what runs here
python3 tools/run_tests.py                # default suites
python3 tools/run_tests.py --suite python-gate --suite dotnet-core
python3 tools/run_tests.py --full-worker  # plus worker-full
```

`--json` prints the run's summary on stdout, and `--ci` appends `summary.md`
to the GitHub step summary. The command output names the suites, exit codes
and evidence folder. On a Mac, use `TMPDIR=/tmp` and set `DICTATION_DOTNET`
when `dotnet` is not on `PATH`. Recording a row with
`--record` (and `--host`, `--allow-dirty` and `--commit`) is described in
[TEST-LOG.md](../TEST-LOG.md).

The Mac `swift` suite explicitly selects `--build-system native`. Some bundled
Swift 6.4 toolchains run each test target separately but overwrite the Swift
Testing XML after each target, undercounting a successful run. A two-target
reproduction reports both cases with the native backend and only the final
case with the default backend. Keep this workaround until the selected
Apple toolchain includes [SwiftPM's aggregation fix](https://github.com/swiftlang/swift-package-manager/pull/10263).
The package build continues to exercise the default build system.

The learning target alone, on any host with Swift (`--mutations` breaks each
guard in a copy of the sources and requires the tests to go red):

```bash
macos/tools/test-learning-on-linux.sh [RUN_DIR]
macos/tools/test-learning-on-linux.sh --mutations [RUN_DIR]
```

## Building the Windows entry point

```bash
dotnet build windows/Dictation.App/Dictation.App.csproj -c Release
```

Linux and macOS can cross-compile it but cannot run WPF. A plain build takes
its name and version from `<AssemblyName>` and `<Version>` in
`Dictation.App.csproj`; the packager passes `product.json`'s version instead. A
Release build maps source paths to `/_/` and turns SourceLink off, so no
builder path or private repository reaches the binaries.

## Packaging

```bash
# Complete preview packages. Inputs must belong to this product: never pass a
# different product's installation or user-data directory.
python3 tools/package.py --platform windows --rid win-x64 \
  --python-runtime /path/to/portable-python --model-dir /path/to/ct2-whisper \
  --output dist/windows-complete
python3 tools/package.py --platform macos \
  --python-runtime /path/to/portable-python --model-dir /path/to/mlx-whisper \
  --output dist/macos-complete

# App-only packages: no Python or model (a developer artifact).
python3 tools/package.py --platform windows --app-only --rid win-arm64 \
  --output dist/windows-arm64
python3 tools/package.py --platform macos --app-only --output dist/macos-app
```

Options: `--rid` is `win-x64` (default) or `win-arm64`; `--jobs` defaults to
the smaller of 4 and the CPU count; `--dotnet` names the .NET executable;
`--sign-identity` is the macOS signing identity. The output directory must not
exist yet. `--app-only` cannot be combined with `--python-runtime` or
`--model-dir`, and a complete package needs both. A complete `win-arm64`
package is refused: faster-whisper needs ctranslate2, which publishes no
win_arm64 wheel. A macOS package must be built and signed on a Mac.
With the Swift 6.4 toolchain of the Xcode 27 beta, the first
`swift build -c release` of a fresh checkout fails with "unable to resolve
module dependency: 'Sparkle'" and leaves Sparkle's xcframework without its
`Info.plist`, so every later build fails too. Delete `macos/.build`, build
once with `swift build --package-path macos --build-system native -c release
--product Dictation` (the `swift` test suite also uses the native build
system), then package (seen on 2026-10-26 on one Mac).

The input Python must be relocatable, not a virtualenv tied to the build
machine's paths (a `pyvenv.cfg` is refused), and the model folder must hold
the files its platform needs. The packager:

- stamps the build with a `DictationBuild` UTC timestamp (the build id the
  launch log line and the verification records cite), also written to the
  receipt as `build`;
- builds the app: `dotnet publish` self-contained for the chosen runtime into
  `Earlet/`, with the stamp as the assembly metadata `DictationBuild`
  (`-p:DictationBuild`), or `swift build -c release` into `Earlet.app` with
  an `Info.plist` built from `product.json` (bundle id, name, version), the
  stamp, macOS 14 as the minimum, one instance at a time and the microphone
  usage sentence;
- stages only the worker modules the two hosts' ops import, plus
  `default_aliases.json`
  ([What ships](architecture.md#what-ships-beyond-the-entry-points)),
  and for a complete package copies the Python and the model, appends the
  runtime to the Python's `._pth` files, and, on a host that can run that
  Python, imports the staged worker and compiles its bytecode;
- on macOS, removes from its copy of the Python what no shipped module
  imports (`MAC_PRUNED_DISTRIBUTIONS` and `MAC_PRUNED_PATHS`): torch and the
  packages only it needs (sympy, mpmath, networkx, jinja2, MarkupSafe,
  setuptools), pip and its bootstrapper, Tcl/Tk with tkinter, IDLE and turtle,
  and the two extra copies of the interpreter. Each distribution goes by its
  `RECORD`. Before deleting anything it refuses a distribution a kept one
  requires outside its extras (mlx-whisper's declared torch is the one
  exception), a missing `RECORD`, and a `RECORD` row naming a directory or a
  file outside the Python. Only flat dependency conditions requiring a
  nonempty extra in every alternative are excluded from that dependency
  check; other conditions are treated conservatively as requirements.
  The import smoke then fails if the staged
  interpreter can still find any top-level name it removed
  (`MAC_PRUNED_IMPORTS`);
- writes `package-mode.json` (mode, provider, no audio retention, no network
  inference), deletes debug symbols, and fails if anything it built names the
  builder's checkout, home directory, commit or private repository;
- on macOS signs the bundle with `codesign --deep` and verifies it.

**Signing.** Ad-hoc signing (`-`) is the default. `--sign-identity`, or
`DICTATION_SIGN_IDENTITY` when the flag is absent, signs with a stable
identity instead, so macOS keeps the Microphone, Accessibility and Input
Monitoring grants across rebuilds; an ad-hoc rebuild changes the code
identity and silently loses them. Local
installs can use your own stable signing identity to retain permission grants.
Developer ID signing is not required to build or test from source.
`tools/package.py` never enables the Hardened Runtime; a downloadable build
goes through [Releasing a Mac build](#releasing-a-mac-build). The Windows output is an
unsigned portable folder. The `mac-package` test suite passes no
`--sign-identity`, so it is ad-hoc only while `DICTATION_SIGN_IDENTITY` is
unset: the runner passes its environment through to every suite. Keep it
unset for verification runs.

**What `build-receipt.json` proves.** The packager writes it beside the
package: the `product.json` fields, platform, the `build` stamp, mode,
`"validation": "build-and-layout-only"`, the `._pth` files it changed,
on macOS what it pruned from the Python (`pruned`: the distributions, the
paths and the bytes), whether the import smoke ran, the symbol files it
deleted and a SHA-256 of every file. It shows that the build ran and how the files are laid out. It
is not recognition, installation, signing or release acceptance. A complete
package still has to be accepted on another machine of the same architecture
with no build caches.

**Installing on a Mac.** For a local complete package:

1. Build from the selected source, using your own stable signing identity if
   available. With an ad-hoc signature, be prepared to grant permissions again.
2. Quit the running app and replace `/Applications/Earlet.app` with the
   new bundle.
3. Compare every SHA-256 in the receipt's `files` (paths relative to the
   bundle, hashed after signing) with the installed files, and run
   `codesign --verify --deep --strict /Applications/Earlet.app`. No tool
   in the tree does the comparison yet.
4. Launch it. `~/Library/Logs/Dictation/dictation.log` gets a `launch` line
   with the version, the `DictationBuild` id and the path: check it names the
   new build in `/Applications`. The window must reach Enabled with the
   existing settings and taught words. A changed signing identity can require
   new permission grants. Installation must preserve the existing vocabulary.
5. Delete the retired bundle and any other copies, keeping their
   `build-receipt.json` files.
6. Keep the receipt and note the tested build, signing method and device.
   Use [the acceptance procedure](acceptance.md) to check the installed copy.

## Releasing a Mac build

`tools/release_macos.py` turns a complete macOS package into the disk image
published on [GitHub Releases](https://github.com/scamai/earlet/releases).
It needs a Mac with Xcode's command-line tools, a `Developer ID Application`
identity in the keychain, and a notarytool profile for the same team, stored
once with `xcrun notarytool store-credentials <profile> --apple-id <id>
--team-id <team>`:

```bash
python3 tools/release_macos.py --app dist/macos-complete/Earlet.app \
  --identity "Developer ID Application: <Name> (<TEAMID>)" \
  --notary-profile <profile> --output dist/release
```

The input app is not changed. The tool:

- refuses an app-only package, an ad-hoc or Apple Development identity and an
  existing output folder;
- copies the app, then signs every Mach-O file inside it, deepest first,
  with the Hardened Runtime and a secure timestamp; then each nested bundle
  (Sparkle's `Updater.app`, then `Sparkle.framework`), deepest first; and the
  app last. It never uses `--deep`, which would give every nested file the
  app's entitlements;
- fails unless every Mach-O file then carries the Hardened Runtime flag and a
  timestamp, which the notary service requires;
- notarizes and staples the app, so it still opens offline after it leaves
  the image; builds `Earlet-<version>-macos-<arch>.dmg` with an Applications
  link, signs, notarizes and staples it; then runs `spctl` on both and
  `syspolicy_check distribution` on the app;
- writes `release-receipt.json`: the identity, how many executables and
  libraries were signed, Apple's submission ids and answers (with each
  notary log beside it), the image's size and SHA-256, and the entitlement
  files' hashes.

`--no-notarize` signs and builds the image without submitting it, to check
a build first; its receipt says it is not notarized.

**The update feed.** `--appcast` also writes, under `<output>/updates`, the
image, a signed `appcast.xml` that lists only this release, and one
`.delta` from each image given with `--previous-dmg` (repeatable). A delta
is what an installed copy of that earlier build downloads instead of the
whole image ([Updates](design-macos.md#updates)), so pass the images of the
releases people are likely to be on, for example the previous one. Only
releases from 0.2.0 on carry the updater; 0.1.0 needs no delta:

```bash
gh release download v0.2.0 --repo github.com/scamai/earlet --pattern '*.dmg' --dir dist/previous
python3 tools/release_macos.py --app dist/macos-complete/Earlet.app \
  --identity "Developer ID Application: <Name> (<TEAMID>)" \
  --notary-profile <profile> --output dist/release \
  --appcast --previous-dmg dist/previous/Earlet-0.2.0-macos-arm64.dmg
```

Before a release, raise `version` in `product.json`; Sparkle orders builds by
`CFBundleVersion`, which the packager sets to the build id. The feed's
download addresses name the tag `v<version>`, so the release must carry that
tag and every file under `updates/` except `old_updates/`; the receipt's
`appcast.publish` is the `gh release create` command that uploads them.
Publishing is a separate, deliberate step: once a release is the latest,
every copy that checks for updates finds it within a day.

**The signing key.** The feed and every archive are signed with an EdDSA key
kept in the login keychain of the releasing Mac, under the account
`product.json` names (`updates.keychain_account`); its public half is
`updates.ed25519_public`, which the packager writes into Info.plist. Create it
once with Sparkle's `generate_keys --account <account>` (from
`macos/.build/artifacts/sparkle/Sparkle/bin` after `swift package resolve`)
and keep a backup with `generate_keys --account <account> -x <file>` in a
store with named, audited access. Without the key no installed copy accepts
another update. The tool reads the key through `generate_keys`, which made
the keychain item, so macOS does not ask, and hands it to `generate_appcast`
on standard input.

**Entitlements.** Each process gets only what it uses:

| File | Signed onto | Key | Why |
|---|---|---|---|
| `macos/Earlet.entitlements` | The app's own executable | `device.audio-input` | Under the Hardened Runtime the microphone delivers nothing without it |
| `macos/EarletWorker.entitlements` | The bundled Python interpreter and the few other executables in its packages | `cs.disable-library-validation` | The interpreter loads extension modules built by others |
| | | `cs.allow-jit`, `cs.allow-unsigned-executable-memory` | Packages in the recognition stack compile code at run time |

Libraries carry no entitlements; the app itself keeps library validation.
The plists hold no comments, which the kernel's entitlement parser rejects.

**What the receipt proves.** Apple accepted the app and the image, the
tickets are stapled, and Gatekeeper on the building Mac accepts both. It is
not microphone, insertion or clean-machine acceptance: install the image on
another Mac and follow [the acceptance procedure](acceptance.md) for that.
Run `tools/acceptance.py auto --app <output>/dmg-root/Earlet.app` before
publishing to check that the signed worker still loads its model.

## Environment overrides

Every `DICTATION_*` name in `windows/`, `macos/`, `runtime/`, `tools/` and
`tests/`. On macOS a debug build (`swift run`, the tests) honours the three
path overrides anywhere; a release or packaged app honours them only when the
path resolves, after `realpath`, inside its own `Contents/Resources`, and
otherwise uses the bundled location. Windows honours them anywhere and
resolves a relative path against the app's directory. Trace lines go to
stderr, which the Mac app also copies into its log
([where the logs are](architecture.md#components)).

**Running the Mac app from source.** A debug build finds no bundled `python/`,
`runtime/` or `model/` beside its executable, so set all three path
overrides:

```bash
DICTATION_PYTHON=/path/to/python3 DICTATION_RUNTIME="$PWD/runtime" \
  DICTATION_MODEL_DIR=/path/to/mlx-whisper swift run --package-path macos Dictation
```

It uses the installed app's existing settings suite and the same
`learning.json` (`DictationSettings.swift`, `LearningStore.swift`). The stable
internal settings and storage identifiers remain unchanged, so an installed
update keeps the existing settings and vocabulary. A development launch reads
and can change that same vocabulary; close an installed preview before running
the development build.

**Read by the apps, the shipped worker and the tools**

| Name | Read by | Effect |
|---|---|---|
| `DICTATION_PYTHON` | Mac `DictationEnvironment.swift`; Windows `DictationEnvironment.cs`, `DictationWorkerPlan.cs` | The interpreter that runs the worker, instead of the bundled `python/` |
| `DICTATION_RUNTIME` | Same | The directory that contains `transcription_worker/`, instead of the bundled `runtime/` |
| `DICTATION_MODEL_DIR` | Same | The model folder, instead of the bundled `model/` |
| `DICTATION_HOLD_KEY` | Windows only: `DictationHotkeys.ResolveHold`, named in `DictationEnvironment.cs` | The hold key as a virtual-key code, decimal or `0x` hex, from the picker's list. It wins over the saved choice; the window says so and locks the picker. An unknown value falls back to Right Ctrl and logs a warning |
| `DICTATION_PREVIEW` | Mac `DictationModel.swift`; Windows `DictationEnvironment.cs` | The live preview of the words on the indicator. Mac: `0` turns it off; any other value, or none, leaves it on. Windows: `1` turns it on; any other value, or none, leaves it off until its cost is measured on Windows hardware with the shipped model. Read once a run |
| `DICTATION_CAPTURE_WATCHDOG` | Windows `DictationEnvironment.cs`, `DictationController.Takes.cs` | The in-hold watchdog that ends a take whose microphone stopped delivering audio (0.3 s with no buffer, after its graces), keeping what was captured: `0` turns it off; any other value, or none, leaves it on. The field switch for a device whose buffering the gap misreads, as the Mac's `dictation.capture.watchdog` defaults flag is ([long takes](design-windows.md#long-takes-are-never-lost)). Read once a run |
| `DICTATION_HUD_RENDER_DIR` | Mac `DictationApp.swift`; Windows `DictationEnvironment.cs`, `DictationRenderMode.cs` | Renders every indicator state to PNGs in that directory (on the Mac also the menu bar icon's waiting badge, `hud-menu-icon-*.png`), then exits, non-zero when a shadow is cut at the image's edge or two busy states differ in size. Honoured by every build, release included. Windows draws each state at 100, 150 and 200% in light, dark and high contrast (the pill in the theme's colours), also fails when the shadow ring or the one busy width fails on the layout, and writes `render-report.txt` beside them; built and tested on Linux, never run ([render modes](design-windows.md#render-modes-on-windows)) |
| `DICTATION_WINDOW_RENDER_DIR` | Mac `DictationApp.swift`, `DictationWindowRender.swift`; Windows `DictationEnvironment.cs`, `DictationRenderMode.cs` | Renders the window to PNGs (each scenario, width and appearance), then exits. Honoured by every build, release included. The Mac exits 0 whatever it wrote; Windows also draws 100, 150 and 200% and high contrast, every header state and the notification-area icon's pictures, and exits non-zero when a scenario or an image failed. With both variables set, the Mac renders only the indicator and Windows renders both |
| `DICTATION_UPDATE_FEED_URL` | Mac `DictationUpdateFeed.swift` | The update feed for one launch instead of Info.plist's `SUFeedURL`: HTTPS, or HTTP to 127.0.0.1 or localhost to try an update against a local feed. Any other value turns updates off for that launch ([Updates](design-macos.md#updates)) |
| `DICTATION_CAPTURE_TRACE` | Mac `DictationCaptureForensics.swift` | `1` logs every audio chunk of each hold |
| `DICTATION_AUDIO_TRACE` | Mac `AudioTrace.swift` (microphone and system-audio capture) | `1` logs the capture layer's trace lines |
| `DICTATION_IPC_TRACE` | Mac `DictationAsrTransport.swift`, `PythonEngineClient.swift` | `1` logs stage-by-stage worker transport timings |
| `DICTATION_ASR_ENGINE` | Worker `whisper_mac.py`; set by both hosts, `tools/acceptance.py` and the Python tests | Pins the engine (`mlx`, `faster` or `openai`) instead of the first that imports; any other value fails `init`. The Mac host sets `mlx` and Windows `faster`, over any inherited value |
| `DICTATION_ASR_PROTOCOL_PIPE` | Worker `asr_server.py`; set by Windows `PythonAsrWorkerProcess.cs` | The named pipe the worker replies on, so native output on stdout cannot corrupt a frame. The Mac sets none and the replies use stdout |
| `DICTATION_MLX_SPEECH_GATE` | Worker `whisper_mac.py` | `enforce` drops MLX chunks the speech gate flags; the default, `shadow`, only logs and counts them |
| `DICTATION_MLX_SCRIPT_SEED` | Worker `whisper_mac.py` | Replaces the carrier sentence that leads a generated MLX vocabulary prompt, the Chinese and the English one alike; empty removes it |
| `DICTATION_SIGN_IDENTITY` | `tools/package.py`, `tools/release_macos.py` | The default for `--sign-identity` (unset means ad-hoc) and for the release's `--identity` |
| `DICTATION_NOTARY_PROFILE` | `tools/release_macos.py` | The default for `--notary-profile` |
| `DICTATION_DOTNET` | `tools/run_tests.py` | The `dotnet` executable when it is not on `PATH` |
| `DICTATION_SWIFT` | `tools/run_tests.py`, `macos/tools/test-learning-on-linux.sh` | The `swift` executable; otherwise `PATH`, then the newest `~/.swift-toolchain/*/usr/bin/swift`. `tools/package.py` always uses `swift` from `PATH` |
| `DICTATION_LEARNING_GOLDEN` | `LearningGoldenTests.swift`; set by the Linux learning script | The shared golden cases; otherwise found beside the nearest `LEARNING.md` |
| `DICTATION_LEARNING_WEB2` | `LearningLexiconTests.swift`; set by the Linux learning script | The Windows app's copy of the word list; unset, that test skips |
| `DICTATION_NOTES_*` | Worker `ops_stream_whisper.py` and `live_speakers.py` (shipped), optional source-only stream utilities | Dormant meeting-notes tuning; see the paragraph below the table |

The worker inherits the host's environment on both platforms (the Mac drops
every `PYTHON*` variable, and Windows' `-I` makes Python ignore them), so the
worker rows of this table apply to a running app too.

**Meeting-notes switches.** `DICTATION_NOTES_*` tune a dormant
meeting-notes caption pipeline. They are read by `ops_stream_whisper.py` and
`live_speakers.py`, which ship because the live preview runs through the
same stream module (the Mac's unless `DICTATION_PREVIEW=0`, the Windows one
only with `DICTATION_PREVIEW=1`), and by remaining optional stream utilities.
Model-backed meeting operations and their model/environment selectors are retired. Neither app sets them. Because the preview's stream module reads
them, one set in the app's environment may change either app's live preview;
that has not been tested, and unset is the only tested state.

<details>
<summary>Remaining names read by shipped stream modules</summary>

These source-level tuning switches are not a supported configuration surface.
Leave them unset for the source preview.

- `DICTATION_NOTES_AEC`
- `DICTATION_NOTES_AGC_MAX_GAIN`
- `DICTATION_NOTES_AGC_SPEECH_RMS`
- `DICTATION_NOTES_AGC_TARGET`
- `DICTATION_NOTES_CARRY_RETRY_S`
- `DICTATION_NOTES_CHEAP_REDIAR`
- `DICTATION_NOTES_COMMIT_BANK_LABEL`
- `DICTATION_NOTES_COMMIT_BEAM_TEXT`
- `DICTATION_NOTES_COMMIT_ON_STABLE`
- `DICTATION_NOTES_COMMIT_SETTLED_LABEL`
- `DICTATION_NOTES_CTX_CARRY`
- `DICTATION_NOTES_DIAG`
- `DICTATION_NOTES_ECHO_EXCISE`
- `DICTATION_NOTES_FRONTIER_GUARD_S`
- `DICTATION_NOTES_GATE_TRACE`
- `DICTATION_NOTES_HOLD_S`
- `DICTATION_NOTES_INPUT_AGC`
- `DICTATION_NOTES_KEEP_EMPTY_WINDOW`
- `DICTATION_NOTES_LABEL_DEADLINE_S`
- `DICTATION_NOTES_LB_ARB`
- `DICTATION_NOTES_LIVE_BEAM`
- `DICTATION_NOTES_LIVE_DECODE_CAP`
- `DICTATION_NOTES_MARGIN_MIN_AGE_S`
- `DICTATION_NOTES_MARGIN_RELEASE`
- `DICTATION_NOTES_MAX_OPEN_S`
- `DICTATION_NOTES_MERGE_CONSEC`
- `DICTATION_NOTES_MERGE_MIN_OBS`
- `DICTATION_NOTES_NUM_SPK`
- `DICTATION_NOTES_REDIAR_CADENCE_S`
- `DICTATION_NOTES_REDIAR_EARLY_S`
- `DICTATION_NOTES_REDIAR_EARLY_WINDOW_S`
- `DICTATION_NOTES_REGISTRY_FLOOR`
- `DICTATION_NOTES_SHOW_PARTIAL`
- `DICTATION_NOTES_SINGLE_EMISSION`
- `DICTATION_NOTES_SPK_SIM`
- `DICTATION_NOTES_SYS_CARRIER`
- `DICTATION_NOTES_SYS_LIVE_MIN_S`
- `DICTATION_NOTES_TICK_S`
- `DICTATION_NOTES_TIMED_MAX_S`
- `DICTATION_NOTES_TURNFIRST`
- `DICTATION_NOTES_TURN_DECODE`
- `DICTATION_NOTES_TURN_GAP_S`
- `DICTATION_NOTES_TURN_GEOMETRY`
- `DICTATION_NOTES_TURN_PEAK`
- `DICTATION_NOTES_TURN_SPEECH_RMS`
- `DICTATION_NOTES_TURN_TEXT_PATIENCE_S`
- `DICTATION_NOTES_TURN_TRACE`
- `DICTATION_NOTES_WORDSPLIT`

</details>

**Named in copied code no entry point reaches**

Setting these changes nothing in either app. They are kept with the code that
was retained from earlier source (see
[What ships](architecture.md#what-ships-beyond-the-entry-points)).

| Names | Where | What they were for in dormant code |
|---|---|---|
| `DICTATION_ASR_MODEL` | Mac `AsrModelConfig.swift`, `DictationAsrTransport.swift` | A Hugging Face model id; the app passes its model folder explicitly instead |
| `DICTATION_DB` | Worker `corpus.py` | The notes database a personal vocabulary prior is read from |

## Model formats

The two formats are not interchangeable.

For a concrete Mac download, the [README quickstart](../README.md#quick-start)
uses [MLX Whisper Large v3 Turbo, 4-bit](https://huggingface.co/mlx-community/whisper-large-v3-turbo-4bit)
at revision `0f058d38170d183f9fdee07908f5b515d91793a8`. The pinned files total
about 464 MB, including `config.json`, `model.safetensors` and the tokenizer.
The `hf download` command is supplied by the `huggingface-hub` dependency in
`runtime/requirements-macos.txt`; it retrieves model files from Hugging Face.
Keep downloads under the ignored `runtime/models/` directory, or use your own
model directory outside the checkout. Do not commit weights.

- **macOS: an MLX Whisper export.** `config.json` plus `weights.safetensors` or
  `model.safetensors`. The app reports a missing file before it spawns a
  worker, and the packager refuses such a folder.
- **Windows: a CTranslate2 Whisper folder.** `model.bin`, `config.json`,
  `tokenizer.json` and `preprocessor_config.json`. The last carries the
  mel-bin count: without it faster-whisper builds 80-bin features, so a
  Large v3 or Turbo export (128 bins) loads and then fails every hold. The
  packager and the app (`DictationWorkerPlan.RequiredModelFiles`) refuse a
  folder without it, and the worker refuses a mismatch at `init`. An 80-bin
  folder that lacks it (Systran's prebuilt small, for one) needs it copied
  from the matching `openai/whisper-*` repository.

The worker loads the model offline
([architecture.md → Worker protocol and lifecycle](architecture.md#worker-protocol-and-lifecycle)).
