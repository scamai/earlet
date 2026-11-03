"""The README's Download for Mac button and the image it shows.

The button is an outlined SVG (docs/readme-media.md#download-button). Each
check is a function over text, run on the real files and on a planted defect
it has to catch (the `*_fires` tests), so a check that could never fail
shows up here.
"""
from __future__ import annotations

import json
from pathlib import Path
import re
from types import SimpleNamespace
import xml.etree.ElementTree as ET

import pytest

from harness import ROOT, tool

BUTTON = "docs/assets/download-mac.svg"
SVG_NS = "{http://www.w3.org/2000/svg}"
# Drawing elements a self-contained button may use; anything else (text,
# images, scripts, style sheets, foreign HTML, <use> references) is refused.
SVG_ELEMENTS = {"svg", "title", "defs", "linearGradient", "stop", "rect", "path"}


def releases_page(root: Path = ROOT) -> str:
    """The latest-release page whose download feed the Mac app updates from."""
    feed = json.loads((root / "product.json").read_text(encoding="utf-8"))["updates"]["feed"]
    if not feed.endswith("/download/appcast.xml"):
        raise ValueError(f"unexpected update feed {feed}")
    return feed.removesuffix("/download/appcast.xml")


def sections(text: str) -> dict[str, str]:
    """Rendered README text by level-two heading; the hero before the first
    is "". Code fences and HTML comments are left out: they show no button."""
    found: dict[str, list[str]] = {"": []}
    name, fenced = "", False
    for line in re.sub(r"<!--.*?-->", "", text, flags=re.S).splitlines():
        if line.lstrip().startswith("```"):
            fenced = not fenced
            continue
        if fenced:
            continue
        if line.startswith("## "):
            name = line[3:].strip()
            found[name] = []
        found[name].append(line)
    return {key: "\n".join(lines) for key, lines in found.items()}


def svg_size(svg: str) -> tuple[str, str]:
    root = ET.fromstring(svg)
    return root.get("width", ""), root.get("height", "")


# ─── Checks ──────────────────────────────────────────────────────────────────

def readme_button_errors(text: str, page: str, size: tuple[str, str]) -> list[str]:
    """Each button links straight to the latest release, says what it does,
    and is drawn at the SVG's own size; the hero and Install both have one."""
    errors = []
    for name, body in sections(text).items():
        for match in re.finditer(rf'<img\b[^>]*\bsrc="{re.escape(BUTTON)}"[^>]*>', body):
            tag, where = match.group(0), name or "hero"
            if not (body[:match.start()].endswith(f'<a href="{page}">') and body[match.end():].startswith("</a>")):
                errors.append(f"{where}: button is not linked, with nothing in between, to {page}")
            alt = re.search(r'\balt="([^"]*)"', tag)
            if not alt or "Download" not in alt.group(1):
                errors.append(f"{where}: button alt text does not say Download")
            width, height = (re.search(rf'\b{attribute}="([^"]*)"', tag) for attribute in ("width", "height"))
            if (width and width.group(1), height and height.group(1)) != size:
                errors.append(f"{where}: button size is not the SVG's {size[0]}x{size[1]}")
    for required in ("", "Install"):
        if f'src="{BUTTON}"' not in sections(text).get(required, ""):
            errors.append(f"{required or 'hero'}: no download button")
    return errors


def svg_errors(svg: str) -> list[str]:
    """The image is self-contained outlines: no text a missing font would
    change, nothing executable, nothing fetched, and a title to read."""
    errors = []
    if re.search(r"<\?(?!xml\s)|<!DOCTYPE|<!ENTITY", svg, re.I):
        errors.append(f"{BUTTON}: processing instruction or DOCTYPE is not allowed")
    try:
        root = ET.fromstring(svg)
    except ET.ParseError as error:
        return errors + [f"{BUTTON}: not XML ({error})"]
    if root.tag != f"{SVG_NS}svg":
        errors.append(f"{BUTTON}: root is not an SVG element")
    for element in root.iter():
        name = element.tag.removeprefix(SVG_NS)
        if name not in SVG_ELEMENTS:
            errors.append(f"{BUTTON}: <{name}> is not allowed")
        for attribute, value in element.attrib.items():
            if attribute.endswith("href") or attribute == "style" or attribute.startswith("on"):
                errors.append(f"{BUTTON}: attribute {attribute} is not allowed")
            if re.search(r"url\((?!#)", value, re.I):
                errors.append(f"{BUTTON}: {attribute} refers outside the image")
    title = root.find(f"{SVG_NS}title")
    if title is None or not (title.text or "").strip():
        errors.append(f"{BUTTON}: no title")
    width, height = svg_size(svg) if root.tag == f"{SVG_NS}svg" else ("", "")
    if root.get("viewBox") != f"0 0 {width} {height}":
        errors.append(f"{BUTTON}: viewBox does not match its {width}x{height} size")
    return errors


# ─── Real files ──────────────────────────────────────────────────────────────

def test_readme_buttons_link_to_the_latest_release_at_the_images_size():
    svg = (ROOT / BUTTON).read_text(encoding="utf-8")
    assert readme_button_errors((ROOT / "README.md").read_text(encoding="utf-8"), releases_page(),
                                svg_size(svg)) == []


def test_the_button_is_self_contained_outlines():
    assert svg_errors((ROOT / BUTTON).read_text(encoding="utf-8")) == []


def committed_label(svg: str) -> tuple[str, float, float, int]:
    """The label path's outline, its baseline origin and the button's width."""
    match = re.search(r'<path transform="translate\(([\d.]+) ([\d.]+)\)" fill="#1F1B14" d="([^"]+)"/>', svg)
    assert match, "no label path in the committed button"
    return match.group(3), float(match.group(1)), float(match.group(2)), int(svg_size(svg)[0])


def test_everything_but_the_glyphs_is_what_the_script_draws():
    """Runs without fontTools or Inter: a hand edit to the colours, frame or
    icon, or a layout the script would not produce, fails here."""
    svg = (ROOT / BUTTON).read_text(encoding="utf-8")
    assert tool("render_download_button").document(*committed_label(svg)) == svg


def test_the_committed_button_is_what_the_script_draws():
    script = tool("render_download_button")
    try:
        font = script.load_font()
    except script.FontError as error:
        pytest.skip(f"cannot redraw the button: {error}")
    assert script.render(font) == (ROOT / BUTTON).read_text(encoding="utf-8")


class StubFont:
    """The few tables `build_problem` reads, without fontTools."""

    def __init__(self, tables=("CFF ",), family="Inter", version="Version 4.000;git-66647c0f0", weight=600):
        self.tables = set(tables)
        self.parts = {"name": SimpleNamespace(getDebugName={1: family, 16: family, 5: version}.get),
                      "OS/2": SimpleNamespace(usWeightClass=weight)}

    def __contains__(self, tag):
        return tag in self.tables

    def __getitem__(self, tag):
        return self.parts[tag]


@pytest.mark.parametrize("font, expected", [
    (StubFont(tables=("CFF ", "fvar")), "is a variable font"),
    (StubFont(tables=("glyf",)), "has TrueType outlines"),
    (StubFont(family="Roboto"), "is Roboto, not Inter"),
    (StubFont(weight=400), "has weight 400"),
    (StubFont(version="Version 3.019;git-0a5106e0b"), "is Version 3.019"),
])
def test_the_script_refuses_fonts_that_draw_another_button(font, expected):
    script = tool("render_download_button")
    assert script.build_problem(StubFont()) is None
    assert (script.build_problem(font) or "").startswith(expected)


def test_an_unreadable_font_is_a_font_error_so_the_redraw_skips(tmp_path):
    pytest.importorskip("fontTools")
    script = tool("render_download_button")
    (tmp_path / "corrupt.otf").write_bytes(b"not a font")
    for path in (tmp_path / "missing.otf", tmp_path / "corrupt.otf"):
        with pytest.raises(script.FontError, match=re.escape(str(path))):
            script.load_font(path)


def test_the_script_reports_a_missing_font_without_a_traceback(tmp_path, capsys, monkeypatch):
    script = tool("render_download_button")
    monkeypatch.setattr("sys.argv", ["render_download_button.py", "--font", str(tmp_path / "missing.otf"),
                                     "--output", str(tmp_path / "out.svg")])
    assert script.main() == 1
    assert "render_download_button.py: error:" in capsys.readouterr().err
    assert not (tmp_path / "out.svg").exists()


# ─── The checks fire ─────────────────────────────────────────────────────────

PAGE = "https://github.com/example/app/releases/latest"
GOOD_TAG = f'<img src="{BUTTON}" alt="Download App for Mac" width="234" height="52">'
GOOD_README = f'<p>\n  <a href="{PAGE}">{GOOD_TAG}</a>\n</p>\n\n## Install\n\n<a href="{PAGE}">{GOOD_TAG}</a>\n'


@pytest.mark.parametrize("old, new, expected", [
    (f'<a href="{PAGE}">{GOOD_TAG}</a>\n</p>', f"{GOOD_TAG}\n</p>", "hero: button is not linked"),
    (f'<a href="{PAGE}">{GOOD_TAG}</a>\n</p>', f'<a href="{PAGE}/download/app.dmg">{GOOD_TAG}</a>\n</p>',
     "hero: button is not linked"),
    (f'<a href="{PAGE}">{GOOD_TAG}</a>\n</p>', f'<a href="{PAGE}">\n{GOOD_TAG}\n</a>\n</p>',
     "hero: button is not linked"),
    ('alt="Download App for Mac" width="234" height="52"></a>\n</p>', 'alt="" width="234" height="52"></a>\n</p>',
     "hero: button alt text does not say Download"),
    ('width="234" height="52"></a>\n</p>', 'width="200" height="52"></a>\n</p>', "hero: button size"),
    (' height="52"></a>\n</p>', '></a>\n</p>', "hero: button size"),
    (f'  <a href="{PAGE}">{GOOD_TAG}</a>\n</p>', "  <strong>Download for Mac →</strong>\n</p>",
     "hero: no download button"),
    (f'## Install\n\n<a href="{PAGE}">{GOOD_TAG}</a>\n', "## Install\n\nDownload from Releases.\n",
     "Install: no download button"),
    (f'  <a href="{PAGE}">{GOOD_TAG}</a>\n</p>', f'  <!-- <a href="{PAGE}">{GOOD_TAG}</a> -->\n</p>',
     "hero: no download button"),
    (f'## Install\n\n<a href="{PAGE}">{GOOD_TAG}</a>\n', f'## Install\n\n```html\n<a href="{PAGE}">{GOOD_TAG}</a>\n```\n',
     "Install: no download button"),
    (f'## Install\n\n<a href="{PAGE}">{GOOD_TAG}</a>\n', f'## Install\n\n<a href="{PAGE}">{GOOD_TAG}</a>\n{GOOD_TAG}\n',
     "Install: button is not linked"),
])
def test_readme_button_check_fires(old, new, expected):
    assert readme_button_errors(GOOD_README, PAGE, ("234", "52")) == []
    assert GOOD_README.count(old) == 1
    errors = readme_button_errors(GOOD_README.replace(old, new), PAGE, ("234", "52"))
    assert any(error.startswith(expected) for error in errors), errors


def test_a_fenced_example_neither_starts_a_section_nor_counts_as_a_button():
    fenced = GOOD_README + f"\n```html\n## Not a heading\n{GOOD_TAG}\n```\n"
    assert readme_button_errors(fenced, PAGE, ("234", "52")) == []
    assert set(sections(fenced)) == {"", "Install"}


GOOD_SVG = ('<svg xmlns="http://www.w3.org/2000/svg" width="234" height="52" viewBox="0 0 234 52">'
            '<title>Download</title><defs><linearGradient id="fill"><stop offset="0"/></linearGradient></defs>'
            '<rect fill="url(#fill)"/><path d="M0 0h1"/></svg>')


@pytest.mark.parametrize("old, new, expected", [
    ('<path d="M0 0h1"/>', "<text>Download</text>", "<text> is not allowed"),
    ('<path d="M0 0h1"/>', "<script>alert(1)</script>", "<script> is not allowed"),
    ('<path d="M0 0h1"/>', "<style>path{fill:red}</style>", "<style> is not allowed"),
    ('<path d="M0 0h1"/>', '<image href="https://example.com/a.png"/>', "<image> is not allowed"),
    ('<path d="M0 0h1"/>', '<path xmlns:x="http://www.w3.org/1999/xlink" x:href="#a" d="M0 0"/>',
     "attribute {http://www.w3.org/1999/xlink}href is not allowed"),
    ('<path d="M0 0h1"/>', '<path onclick="go()" d="M0 0"/>', "attribute onclick is not allowed"),
    ('<path d="M0 0h1"/>', '<path style="fill:red" d="M0 0"/>', "attribute style is not allowed"),
    ('fill="url(#fill)"', 'fill="url(https://example.com/a.svg#fill)"', "fill refers outside the image"),
    ("<title>Download</title>", "<title> </title>", "no title"),
    ("<title>Download</title>", "", "no title"),
    ('viewBox="0 0 234 52"', 'viewBox="0 0 200 52"', "viewBox does not match"),
    ("</svg>", "", "not XML"),
    ('<svg xmlns', '<?xml-stylesheet href="https://example.com/a.css"?><svg xmlns',
     "processing instruction or DOCTYPE"),
    ('<svg xmlns', '<!DOCTYPE svg [<!ENTITY t "Download">]><svg xmlns', "processing instruction or DOCTYPE"),
    ('fill="url(#fill)"', 'fill="URL(https://example.com/a.svg#fill)"', "fill refers outside the image"),
])
def test_svg_check_fires(old, new, expected):
    assert svg_errors(GOOD_SVG) == []
    assert GOOD_SVG.count(old) == 1
    errors = svg_errors(GOOD_SVG.replace(old, new))
    assert any(error.removeprefix(f"{BUTTON}: ").startswith(expected) for error in errors), errors


def test_releases_page_comes_from_the_update_feed(tmp_path):
    (tmp_path / "product.json").write_text(json.dumps({"updates": {"feed": f"{PAGE}/download/appcast.xml"}}))
    assert releases_page(tmp_path) == PAGE
    (tmp_path / "product.json").write_text(json.dumps({"updates": {"feed": "https://example.com/appcast.xml"}}))
    with pytest.raises(ValueError):
        releases_page(tmp_path)
