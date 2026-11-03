<!-- English only -->

[Back to Earlet README](../README.md)

# README media guide

## Contents

- [Purpose](#purpose)
- [Reproduce the fixture](#reproduce-the-fixture)
- [Download button](#download-button)
- [Limits](#limits)

## Purpose

The README shows two generated images. The window image is a native macOS
SwiftUI interface detail, rendered from the committed source with the inert
window-render harness. It shows the Home practice flow; it does not claim that
a microphone or speech transcription was run. The
[Download for Mac button](#download-button) is drawn by a script.

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

## Download button

The README's **Download for Mac** button,
[`docs/assets/download-mac.svg`](assets/download-mac.svg), is drawn by a
script rather than captured. It needs
[fontTools](https://github.com/fonttools/fonttools) and Inter 4's static
`Inter-SemiBold.otf`: the `extras/otf` folder of a
[release](https://github.com/rsms/inter/releases), or a `fonts-inter` 4
package. Inter's usual install, `Inter.ttc` and `InterVariable.ttf`, draws
different outlines and is refused, as are the TTF and Inter 3. From the
repository root, run:

```bash
python3 tools/render_download_button.py --font path/to/extras/otf/Inter-SemiBold.otf
```

Without `--font`, the script asks `fc-match` and then searches the usual Linux
and macOS font folders. Inter 4.0 and 4.1 draw the same file.

The label is converted to outlines, with the font's own kerning, so the button
looks the same in every browser: GitHub shows README images in a sandbox that
cannot load web fonts. The SVG carries the outlines of its sixteen glyphs, not
the font, which is licensed under the SIL Open Font License 1.1. The gold
gradient and ink are the app icon's colours from
[`render_brand_assets.swift`](../tools/render_brand_assets.swift). The image
has no text, script, style sheet or external reference, and reads well on
GitHub's light and dark backgrounds. If its width changes, update the `width`
of both README buttons; [the button's tests](../tests/test_readme_download_button.py)
compare them, rebuild everything but the glyphs without the font, and redraw
the whole file where the font is installed.

## Limits

This fixture verifies layout and appearance only. It is not a live capture,
recognition, insertion, accessibility, permission, performance, or end-to-end
proof. Native microphone and transcription behavior requires the checks in the
[acceptance guide](acceptance.md).
