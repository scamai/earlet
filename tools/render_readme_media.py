#!/usr/bin/env python3
"""Render the native Dictation window used by the README preview.

The committed macOS source is archived into a private temporary directory and
the render-only harness is narrowed there.  No product source is modified.
"""
from __future__ import annotations

import argparse
import io
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile


HARNESS = Path("Sources/DictationApp/DictationWindowRender.swift")
BOOTSTRAP = Path("Sources/DictationApp/DictationApp.swift")
RENDER_BOOTSTRAP = """import AppKit

// Scratch-only entry point. Never construct the normal app or its live model.
@main
struct DictationReadmeRender {
    @MainActor
    static func main() async {
        NSApplication.shared.setActivationPolicy(.prohibited)
        guard let directory = ProcessInfo.processInfo.environment["DICTATION_WINDOW_RENDER_DIR"] else {
            fatalError("Missing render output directory")
        }
        await DictationWindowRenderMode.run(to: URL(fileURLWithPath: directory))
    }
}
"""
OUTPUTS = {
    False: "mac-window-light.png",
    True: "mac-window-dark.png",
}


def run(command: list[str], *, cwd: Path, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=cwd, env=env, text=True, capture_output=True)


def repository_source(repo: Path) -> tuple[Path, str]:
    root_result = run(["git", "rev-parse", "--show-toplevel"], cwd=repo)
    if root_result.returncode:
        raise RuntimeError(f"not a git repository: {repo}\n{root_result.stderr.strip()}")
    prefix_result = run(["git", "rev-parse", "--show-prefix"], cwd=repo)
    if prefix_result.returncode:
        raise RuntimeError(f"could not determine repository prefix: {prefix_result.stderr.strip()}")
    root = Path(root_result.stdout.strip()).resolve()
    prefix = prefix_result.stdout.strip().strip("/")
    return root, f"{prefix + '/' if prefix else ''}macos"


def archive_macos(repo: Path, destination: Path) -> str:
    root, tree = repository_source(repo)
    result = subprocess.run(
        ["git", "archive", "--format=tar", "HEAD:" + tree],
        cwd=root,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if result.returncode:
        detail = result.stderr.decode(errors="replace").strip()
        raise RuntimeError(f"could not archive committed macOS source: {detail}")
    with tarfile.open(fileobj=io.BytesIO(result.stdout), mode="r:") as archive:
        destination_resolved = destination.resolve()
        for member in archive.getmembers():
            target = (destination / member.name).resolve()
            if target != destination_resolved and destination_resolved not in target.parents:
                raise RuntimeError(f"archive path escapes scratch directory: {member.name}")
            if member.issym() or member.islnk():
                raise RuntimeError(f"symlink in archived source is not allowed: {member.name}")
        archive.extractall(destination)
    return tree


def replace_once(source: str, needle: str, replacement: str, label: str) -> str:
    count = source.count(needle)
    if count != 1:
        raise RuntimeError(f"expected exactly one {label} declaration, found {count}")
    return source.replace(needle, replacement)


def patch_harness(scratch: Path, width: int, height: int, capture_height: int) -> None:
    bootstrap = scratch / BOOTSTRAP
    if not bootstrap.is_file():
        raise RuntimeError(f"archived bootstrap is missing: {BOOTSTRAP}")
    path = scratch / HARNESS
    if not path.is_file():
        raise RuntimeError(f"archived harness is missing: {HARNESS}")
    source = path.read_text(encoding="utf-8")
    source = replace_once(
        source,
        "static let widths: [CGFloat] = [600, 720, 960]",
        f"static let widths: [CGFloat] = [{width}]",
        "widths",
    ) if "static let widths: [CGFloat] = [600, 720, 960]" in source else replace_once(
        source,
        "static let widths: [CGFloat] = [420, 520, 768]",
        f"static let widths: [CGFloat] = [{width}]",
        "widths",
    )
    source = replace_once(source, "static let height: CGFloat = 1100", f"static let height: CGFloat = {height}", "height") \
        if "static let height: CGFloat = 1100" in source else replace_once(
            source, "static let height: CGFloat = 1500", f"static let height: CGFloat = {height}", "height"
        )
    source = replace_once(
        source,
        "for scenario in DictationRenderScenario.allCases {",
        "for scenario in [DictationRenderScenario.ready] {",
        "scenario loop",
    )
    source = replace_once(
        source,
        "guard let rep = hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds) else { return false }\n        hosting.cacheDisplay(in: hosting.bounds, to: rep)",
        "let captureHeight: CGFloat = " + str(capture_height) + "\n"
        "        let captureRect: NSRect\n"
        "        if hosting.isFlipped {\n"
        "            captureRect = NSRect(x: hosting.bounds.minX, y: hosting.bounds.minY, width: hosting.bounds.width, height: captureHeight)\n"
        "        } else {\n"
        "            captureRect = NSRect(x: hosting.bounds.minX, y: hosting.bounds.maxY - captureHeight, width: hosting.bounds.width, height: captureHeight)\n"
        "        }\n"
        "        guard let rep = hosting.bitmapImageRepForCachingDisplay(in: captureRect) else { return false }\n"
        "        hosting.cacheDisplay(in: captureRect, to: rep)",
        "native capture region",
    )
    path.write_text(source, encoding="utf-8")
    bootstrap_source = replace_once(
        bootstrap.read_text(encoding="utf-8"),
        "@main\nstruct DictationApp: App {",
        "struct DictationApp: App {",
        "app entry point",
    )
    # Keep shared window helpers for compilation, but the normal App is never
    # instantiated. Only the inert rig is reachable from the new entry point.
    bootstrap.write_text(bootstrap_source + "\n" + RENDER_BOOTSTRAP, encoding="utf-8")


def render(repo: Path, output: Path, width: int, height: int, capture_height: int) -> None:
    if width <= 0 or height <= 0 or capture_height <= 0:
        raise ValueError("width, height, and capture height must be positive")
    if capture_height > height:
        raise ValueError("capture height must not exceed render height")
    output = output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="dictation-readme-render-") as temporary:
        scratch = Path(temporary) / "macos"
        scratch.mkdir()
        tree = archive_macos(repo, scratch)
        patch_harness(scratch, width, height, capture_height)
        render_dir = Path(temporary) / "renders"
        env = os.environ.copy()
        env["DICTATION_WINDOW_RENDER_DIR"] = str(render_dir)
        env.pop("DICTATION_HUD_RENDER_DIR", None)
        command = ["swift", "run", "--package-path", str(scratch), "--jobs", "4", "Dictation"]
        completed = run(command, cwd=scratch, env=env)
        log = completed.stdout + completed.stderr
        if completed.returncode:
            raise RuntimeError(f"native render failed for {tree}:\n{log}")
        if "FAILED" in log:
            raise RuntimeError(f"native render reported FAILED:\n{log}")
        expected = {f"window-ready-{width}-light.png", f"window-ready-{width}-dark.png"}
        actual = {path.name for path in render_dir.glob("*.png")}
        if actual != expected:
            raise RuntimeError(f"expected exactly light/dark outputs {sorted(expected)}, found {sorted(actual)}")
        for dark, filename in OUTPUTS.items():
            source = render_dir / f"window-ready-{width}-{'dark' if dark else 'light'}.png"
            png_header = source.read_bytes()
            if png_header[:8] != b"\x89PNG\r\n\x1a\n":
                raise RuntimeError(f"native output is not PNG: {source.name}")
            pixels = struct.unpack(">II", png_header[16:24])
            supported_pixels = {(width * scale, capture_height * scale) for scale in (1, 2)}
            if pixels not in supported_pixels:
                raise RuntimeError(f"unexpected native capture dimensions for {source.name}: {pixels}, expected native 1x or 2x")
            shutil.copyfile(source, output / filename)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path.cwd(), help="repository or app directory (default: current directory)")
    parser.add_argument("--output-dir", type=Path, default=Path("docs/assets"), help="fixture output directory")
    parser.add_argument("--width", type=int, default=600, help="render width in points (default: 600)")
    parser.add_argument("--height", type=int, default=900, help="native render height in points (default: 900)")
    parser.add_argument("--capture-height", type=int, default=460, help="native top-region capture height in points (default: 460)")
    parser.add_argument("--show-prefix", action="store_true", help="print the git path prefix used for the macOS archive")
    args = parser.parse_args()
    try:
        repo = args.repo.resolve()
        _, tree = repository_source(repo)
        if args.show_prefix:
            print(tree.removesuffix("/macos"))
        render(repo, args.output_dir, args.width, args.height, args.capture_height)
    except (OSError, RuntimeError, ValueError) as error:
        print(f"render_readme_media.py: error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
