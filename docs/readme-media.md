<!-- English only -->

[Back to Earlet README](../README.md)

# README media guide

## Contents

- [Purpose](#purpose)
- [Reproduce the fixture](#reproduce-the-fixture)
- [Limits](#limits)

## Purpose

The README image is a native macOS SwiftUI interface detail, rendered from the
committed source with the inert window-render harness. It shows the Home
practice flow; it does not claim that a microphone or speech transcription was
run.

## Reproduce the fixture

From the repository root on macOS, run:

```bash
python3 tools/render_readme_media.py
```

The helper archives the committed `macos/` tree into a temporary directory,
replaces the scratch app entry point with a render-only bootstrap, narrows the
render harness, and captures the top 460-point region of a
900-point native view at 600 points wide. It writes the light and dark PNGs to
`docs/assets/` at the screen’s native 1× or 2× backing scale. The scratch copy
is removed when the command exits. The normal app delegate, window scene, and
live model are never constructed by this bootstrap.

The run uses only inert fixture state and does not read or change user stores,
recordings, permissions, credentials, third-party source copies, or live
microphone input. The render harness is
[`DictationWindowRender.swift`](../macos/Sources/DictationApp/DictationWindowRender.swift).

## Limits

This fixture verifies layout and appearance only. It is not a live capture,
recognition, insertion, accessibility, permission, performance, or end-to-end
proof. Native microphone and transcription behavior requires the checks in the
[acceptance guide](acceptance.md).
