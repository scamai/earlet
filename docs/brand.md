<!-- English only -->

[Back to README](../README.md)

# Earlet identity

## Contents

- [Name and mark](#name-and-mark)
- [Reproduce the icon](#reproduce-the-icon)
- [Stable application identifiers](#stable-application-identifiers)

## Name and mark

Use **Earlet** for the product and `earlet` for its repository slug. The mark
is a microphone with three pairs of wheat grains cut into its grille. The
microphone is the primary silhouette; wheat adds character at larger sizes.
Warm gold and a dark ink keep the mark legible against light and dark surfaces.

The design began with an AI-generated concept using the brief “a rounded
microphone, three paired wheat grains as grille openings, a simple cradle and
foot, warm gold, no lettering.” The shipped artwork is a native SwiftUI path,
drawn in [EarletMark.swift](../macos/Sources/DictationAppCore/EarletMark.swift).
The window and icon renderer share that path. The raster concept is not a
runtime asset.

## Reproduce the icon

On macOS, from the repository root, choose a fresh output directory:

```bash
swiftc -parse-as-library macos/Sources/DictationAppCore/EarletMark.swift \
  tools/render_brand_assets.swift -o /tmp/earlet-render-brand
/tmp/earlet-render-brand dist/earlet-brand
iconutil -c icns dist/earlet-brand/Earlet.iconset \
  -o dist/earlet-brand/AppIcon.icns
```

The helper renders the 1024-pixel master and macOS iconset, including 16- and
32-pixel sizes. Inspect those small images before replacing
`macos/Resources/AppIcon-master.png` and `macos/Resources/AppIcon.icns`.
The packager copies the ICNS into the app bundle. For native README imagery,
follow the [media guide](readme-media.md).

## Stable application identifiers

The display name and package filename are Earlet and `Earlet.app`. Native
executables and modules keep their `Dictation` names. The bundle identifier,
settings domains, keychain identity, existing data paths and `DICTATION_*`
environment variables remain unchanged so earlier preview installations keep
their settings and vocabulary. The [development guide](development.md)
documents these paths and commands.
