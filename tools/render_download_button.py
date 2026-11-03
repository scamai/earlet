#!/usr/bin/env python3
"""Render the README's "Download for Mac" button as a self-contained SVG.

The label is converted to outlines, so the button looks the same whatever
fonts a reader's browser has: GitHub serves README images in a sandbox that
cannot load web fonts, and a <text> element falls back to each system's own
sans-serif.  Colours are the app icon's gold gradient and ink
(tools/render_brand_assets.swift).  The output has no text, script, style
sheet or external reference.

fontTools is imported only to draw, so the tests can rebuild the document
around the committed label without it.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import shutil
import subprocess
import sys
from typing import Any


LABEL = "Download for Mac"
TITLE = "Download Earlet for Mac"
# Inter is licensed under the SIL Open Font License 1.1; the SVG carries the
# outlines of these sixteen glyphs, not the font.  The committed button is
# drawn from Inter 4's static SemiBold OTF; 4.0 and 4.1 give the same paths,
# while the TTF, the variable font, Inter.ttc and Inter 3 do not.
FONT_QUERY = "Inter:semibold"
FONT_FILE = "Inter-SemiBold.otf"
FONT_DIRS = (Path("/usr/share/fonts"), Path("/usr/local/share/fonts"), Path.home() / ".fonts",
             Path.home() / ".local/share/fonts", Path.home() / "Library/Fonts", Path("/Library/Fonts"))
FONT_SOURCE = ("use Inter 4's static Inter-SemiBold.otf: extras/otf/ in the release zip from "
               "github.com/rsms/inter/releases, or a fonts-inter 4 package")

HEIGHT = 52
RADIUS = 12
FONT_SIZE = 18
PADDING = 24
ICON = 20
GAP = 10
GOLD_TOP = "#F7C954"     # Color(red: 0.97, green: 0.79, blue: 0.33)
GOLD_BOTTOM = "#DB9614"  # Color(red: 0.86, green: 0.59, blue: 0.08)
INK = "#1F1B14"          # Color(red: 0.12, green: 0.105, blue: 0.08)


# ─── Font ───

class FontError(ValueError):
    """No font, or not the build the committed button was drawn from."""


def build_problem(font: Any) -> str | None:
    """Why a font cannot redraw the committed button, or None."""
    family = font["name"].getDebugName(16) or font["name"].getDebugName(1)
    version = font["name"].getDebugName(5) or ""
    if "fvar" in font:
        return "is a variable font"
    if "CFF " not in font:
        return "has TrueType outlines, not the OTF's"
    if family != "Inter":
        return f"is {family}, not Inter"
    if font["OS/2"].usWeightClass != 600:
        return f"has weight {font['OS/2'].usWeightClass}, not SemiBold (600)"
    if not version.startswith("Version 4."):
        return f"is {version}, not Inter 4"
    return None


def candidates(explicit: Path | None) -> list[Path]:
    if explicit is not None:
        return [explicit]
    found: list[Path] = []
    if shutil.which("fc-match"):
        match = subprocess.run(["fc-match", "-f", "%{file}", FONT_QUERY], capture_output=True, text=True).stdout
        if Path(match).name == FONT_FILE:
            found.append(Path(match))
    for directory in FONT_DIRS:
        if directory.is_dir():
            found += sorted(directory.rglob(FONT_FILE))
    return found


def load_font(explicit: Path | None = None) -> Any:
    """The first usable Inter SemiBold: `explicit`, else fc-match's, else the
    first in the usual font folders.  Raises FontError naming what was wrong."""
    try:
        from fontTools.ttLib import TTFont, TTLibError  # type: ignore[import-untyped]
    except ImportError as error:
        raise FontError("needs fontTools (python3 -m pip install fonttools)") from error
    problems = []
    for path in candidates(explicit):
        try:
            font = TTFont(path)
        except (OSError, TTLibError) as error:
            problems.append(f"{path}: {error}")
            continue
        problem = build_problem(font)
        if problem is None:
            return font
        problems.append(f"{path} {problem}")
    reason = "; ".join(problems) if problems else f"{FONT_FILE} not found"
    raise FontError(f"{reason}; {FONT_SOURCE}, with --font if it is not installed")


def kern_lookups(font: Any) -> list:
    if "GPOS" not in font:
        return []
    table = font["GPOS"].table
    indices = {index for record in table.FeatureList.FeatureRecord if record.FeatureTag == "kern"
               for index in record.Feature.LookupListIndex}
    return [table.LookupList.Lookup[index] for index in sorted(indices)]


def pair_adjustment(lookups: list, first: str, second: str) -> int:
    """The horizontal kern between two glyphs, from GPOS pair positioning.

    Within a lookup the first subtable that applies wins, as in a shaper: a
    format 1 subtable applies only when it lists the pair, a format 2
    subtable whenever it covers the first glyph.  Lookups add up.
    """
    total = 0
    for lookup in lookups:
        for subtable in lookup.SubTable:
            if lookup.LookupType == 9:
                subtable = subtable.ExtSubTable
            if subtable.LookupType != 2 or first not in subtable.Coverage.glyphs:
                continue
            if subtable.Format == 1:
                pairs = subtable.PairSet[subtable.Coverage.glyphs.index(first)].PairValueRecord
                record = next((pair for pair in pairs if pair.SecondGlyph == second), None)
                if record is None:
                    continue
            else:
                row = subtable.Class1Record[subtable.ClassDef1.classDefs.get(first, 0)]
                record = row.Class2Record[subtable.ClassDef2.classDefs.get(second, 0)]
            total += getattr(record.Value1, "XAdvance", 0) or 0
            break
    return total


def outline(font: Any, text: str, size: float) -> tuple[str, tuple[float, float, float, float], float]:
    """Return the label's SVG path at the origin baseline, its ink bounds and cap height."""
    from fontTools.pens.boundsPen import BoundsPen  # type: ignore[import-untyped]
    from fontTools.pens.svgPathPen import SVGPathPen  # type: ignore[import-untyped]
    from fontTools.pens.transformPen import TransformPen  # type: ignore[import-untyped]

    scale = size / font["head"].unitsPerEm
    cmap = font.getBestCmap()
    names = [cmap[ord(character)] for character in text]
    lookups = kern_lookups(font)
    glyph_set = font.getGlyphSet()
    path = SVGPathPen(glyph_set, ntos=lambda n: f"{n:.2f}".rstrip("0").rstrip(".") or "0")
    bounds = BoundsPen(glyph_set)
    x = 0
    for position, name in enumerate(names):
        transform = (scale, 0, 0, -scale, x * scale, 0)
        glyph_set[name].draw(TransformPen(path, transform))
        glyph_set[name].draw(TransformPen(bounds, transform))
        x += glyph_set[name].width
        if position + 1 < len(names):
            x += pair_adjustment(lookups, name, names[position + 1])
    if bounds.bounds is None:
        raise ValueError("label has no outlines")
    return path.getCommands(), bounds.bounds, font["OS/2"].sCapHeight * scale


# ─── Drawing ───

def number(value: float) -> str:
    return f"{value:.2f}".rstrip("0").rstrip(".")


def render(font: Any) -> str:
    label, (left, _, right, _), cap_height = outline(font, LABEL, FONT_SIZE)
    width = round(PADDING + ICON + GAP + (right - left) + PADDING)
    return document(label, PADDING + ICON + GAP - left, (HEIGHT + cap_height) / 2, width)


def document(label: str, text_x: float, baseline: float, width: int) -> str:
    """The button around a label path whose baseline starts at (text_x, baseline)."""
    icon_x = PADDING
    icon_y = (HEIGHT - ICON) / 2
    # A down arrow into a tray, drawn on a 20-point grid.
    arrow = "M10 3.5v9.5M5.5 8.5l4.5 4.5 4.5-4.5M3.5 13.5v1.5a1.5 1.5 0 0 0 1.5 1.5h10a1.5 1.5 0 0 0 1.5-1.5v-1.5"
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{HEIGHT}" viewBox="0 0 {width} {HEIGHT}" role="img" aria-label="{TITLE}">
  <title>{TITLE}</title>
  <defs>
    <linearGradient id="fill" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{GOLD_TOP}"/>
      <stop offset="1" stop-color="{GOLD_BOTTOM}"/>
    </linearGradient>
    <linearGradient id="shine" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.55"/>
      <stop offset="0.5" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>
  </defs>
  <rect x="0.5" y="0.5" width="{width - 1}" height="{HEIGHT - 1}" rx="{RADIUS}" fill="url(#fill)" stroke="{INK}" stroke-opacity="0.28"/>
  <rect x="1.5" y="1.5" width="{width - 3}" height="{HEIGHT - 3}" rx="{RADIUS - 1}" fill="none" stroke="url(#shine)"/>
  <path transform="translate({number(icon_x)} {number(icon_y)})" d="{arrow}" fill="none" stroke="{INK}" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/>
  <path transform="translate({number(text_x)} {number(baseline)})" fill="{INK}" d="{label}"/>
</svg>
"""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--font", type=Path, help=f"{FONT_FILE} from Inter 4 (default: found with fc-match or in the usual font folders)")
    parser.add_argument("--output", type=Path, default=Path("docs/assets/download-mac.svg"), help="SVG to write")
    args = parser.parse_args()
    try:
        svg = render(load_font(args.font))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(svg, encoding="utf-8")
    except (OSError, ValueError) as error:
        print(f"render_download_button.py: error: {error}", file=sys.stderr)
        return 1
    print(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
