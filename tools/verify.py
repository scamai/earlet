#!/usr/bin/env python3
"""Verify the standalone source boundary, optionally the exact import snapshot.

The boundary check reads every non-test source and project file under windows/
and macos/, the worker files tools/package.py ships, and product.json. It fails
on a reference that climbs out of the product directory or a builder's absolute path,
unless EXEMPTIONS names that exact site with a reason. Comments are not scanned:
imported code cites its origin there.

Every imported file in import-manifest.json must exist, unless the manifest's
removed-ledger names it: a file deleted on purpose keeps its provenance entry,
and its ledger entry says why it went and which commit holds its last copy. A
ledgered file that is back fails, so a deletion cannot be undone by accident.

--snapshot also compares every imported file with import-manifest.json: its
destination hash, and its source hash at the pinned commit read through git;
and it reads each removed file's last copy at the commit its entry names.
It fails when the history cannot be read, rather than skip that half.
tests/test_boundary.py reads the real removed files in the gate as well,
because --snapshot never gates.

An exported copy of the app (tools/oss-export/dictation at the monorepo root)
carries no import-manifest.json. There the boundary is checked in full, the
provenance part (every import present or ledgered as removed, and --snapshot)
is skipped and the summary says so, and --snapshot fails rather than pass
without its manifest. A tree that still holds a monorepo-only file the export
leaves out (MONOREPO_ONLY) is not an exported copy: there a missing manifest is
an error.
"""
from __future__ import annotations

import argparse
import ast
from collections import Counter
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import tokenize
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = "import-manifest.json"
# Tracked in the monorepo and never exported (tools/oss-export/dictation EXCLUDED): a tree holding one of
# these is the monorepo's, so a missing manifest there is lost provenance, not an exported copy.
MONOREPO_ONLY = ("VERIFY.md", ".Codex")


def exported_copy(root: Path) -> bool:
    """The public export: no import-manifest.json and none of the monorepo-only files."""
    return not (root / MANIFEST).is_file() and not any((root / name).exists() for name in MONOREPO_ONLY)

# ─── Rules ───────────────────────────────────────────────────────────────────

CODE = {".cs", ".swift", ".py"}
PROJECT = {".csproj", ".props", ".targets"}
XML = PROJECT | {".xaml", ".resx", ".plist", ".xml", ".manifest", ".config"}
BINARY = {".gz", ".zip", ".png", ".ico", ".icns", ".jpg", ".bin", ".onnx", ".dll", ".exe", ".pdb"}
BUILD_DIRS = {"bin", "obj", ".build", "dist", "__pycache__", ".pytest_cache"}

LEAKS = (
    ("builder path", re.compile(r"/home/[\w.-]+|/Users/[\w.-]+|\b[A-Za-z]:\\{1,2}Users\\{1,2}", re.I), None),
)


# Reviewed exceptions are counted exactly: both a new site and a removed site
# fail until this list is reviewed. No production exception remains.
EXEMPTIONS = ()


def _package():
    spec = importlib.util.spec_from_file_location("dictation_package", Path(__file__).with_name("package.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# ─── What is scanned ─────────────────────────────────────────────────────────

def is_test(relative: str) -> bool:
    parts = relative.split("/")
    return (parts[0] == "macos" and parts[1:2] == ["Tests"]) or (
        parts[0] == "windows" and len(parts) > 1 and (parts[1].endswith(".Tests") or parts[1] == "TestSupport"))


def tree_files(root: Path, top: str) -> list[Path]:
    files = []
    for folder, directories, names in os.walk(root / top):
        directories[:] = sorted(name for name in directories if name not in BUILD_DIRS)
        files += [Path(folder) / name for name in sorted(names)]
    return files


def scanned_files(root: Path) -> list[Path]:
    files = [path for top in ("windows", "macos") for path in tree_files(root, top)
             if not is_test(path.relative_to(root).as_posix()) and path.suffix not in BINARY]
    return files + _package().worker_files(root) + [root / "product.json"]


def code_lines(path: Path, text: str) -> list[str]:
    """The file's lines with comments (and Python docstrings) blanked in place."""
    if path.suffix in XML:
        text = re.sub(r"<!--.*?-->", lambda match: "\n" * match.group(0).count("\n"), text, flags=re.S)
    lines = text.splitlines()
    if path.suffix == ".py":
        try:
            for node in ast.walk(ast.parse(text)):
                body = getattr(node, "body", None)
                if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)) and body \
                        and isinstance(body[0], ast.Expr) and isinstance(body[0].value, ast.Constant) \
                        and isinstance(body[0].value.value, str):
                    lines[body[0].lineno - 1:body[0].end_lineno] = [""] * (body[0].end_lineno - body[0].lineno + 1)
            for token in tokenize.generate_tokens(io.StringIO(text).readline):
                if token.type == tokenize.COMMENT:
                    row, column = token.start
                    lines[row - 1] = lines[row - 1][:column]
        except (SyntaxError, tokenize.TokenError):
            pass  # scan an unparsable file whole
    elif path.suffix in (".cs", ".swift"):
        in_block = False
        for index, line in enumerate(lines):
            stripped = line.strip()
            if in_block or stripped.startswith(("//", "/*")):
                in_block = (in_block or stripped.startswith("/*")) and "*/" not in stripped
                lines[index] = ""
    return lines


# ─── Checks ──────────────────────────────────────────────────────────────────

def leak_errors(root: Path) -> list[str]:
    errors, covered = [], Counter()
    for path in scanned_files(root):
        relative = path.relative_to(root).as_posix()
        data = path.read_bytes()
        if b"\0" in data[:8192]:
            continue
        for number, line in enumerate(code_lines(path, data.decode("utf-8", "replace")), 1):
            lowered = line.lower()
            if "/home/" not in lowered and "users" not in lowered:
                continue  # every rule needs one of these; skipping is only speed
            for name, pattern, suffixes in LEAKS:
                if (suffixes is None or path.suffix in suffixes) and pattern.search(line):
                    exemption = next((e for e in EXEMPTIONS if e[0] == relative and e[1] in line), None)
                    if exemption:
                        covered[exemption] += 1
                    else:
                        errors.append(f"{name}: {relative}:{number}: {line.strip()[:120]}")
                    break
    for exemption in EXEMPTIONS:
        if covered[exemption] != exemption[2]:
            errors.append(f"Exemption drift: {exemption[0]} has {covered[exemption]} matching lines with "
                          f"{exemption[1]!r}, reviewed {exemption[2]}; review the site, then update EXEMPTIONS")
    return errors


def escapes(root: Path, base: Path, value: str) -> bool:
    """Whether a relative path, up to its first wildcard, leaves apps/dictation."""
    value = value.split("*")[0]
    return ".." in re.split(r"[\\/]", value) and not (base / value.replace("\\", "/")).resolve().is_relative_to(root)


def project_errors(root: Path) -> list[str]:
    errors = []
    names = ("Include", "Update", "Project")
    not_paths = {"PackageReference", "PackageVersion", "Using", "InternalsVisibleTo", "FrameworkReference"}
    for project in (path for path in tree_files(root, "windows") if path.suffix in PROJECT):
        relative = project.relative_to(root).as_posix()
        for element in ET.parse(project).iter():
            tag = element.tag.rsplit("}", 1)[-1]
            values = [(name, element.get(name)) for name in names if element.get(name)]
            if element.text and element.text.strip():
                values.append(("text", element.text.strip()))
            for name, raw in values:
                if tag in not_paths and name != "text":
                    continue
                value = re.sub(r"\$\((MSBuildThisFileDirectory|MSBuildProjectDirectory)\)[\\/]?", "", raw)
                if "$" in value:
                    continue
                if escapes(root, project.parent, value):
                    errors.append(f"Unclosed dependency: {relative} -> {raw}")
                elif tag in ("ProjectReference", "Compile", "EmbeddedResource", "Import") and "*" not in value \
                        and not (project.parent / value.replace("\\", "/")).exists():
                    errors.append(f"Unclosed dependency: {relative} -> {raw}")
    return errors


#: The only packages macos/Package.swift may name: public, third-party and
#: pinned to one exact release, which Package.resolved must record too. No
#: product's own code arrives this way, and a `path:` package is refused like
#: any other path. Sparkle installs the Mac app's updates
#: (docs/design-macos.md#updates).
ALLOWED_PACKAGES = {"https://github.com/sparkle-project/Sparkle": "2.9.6"}


def package_errors(root: Path) -> list[str]:
    errors = []
    package = (root / "macos/Package.swift").read_text(encoding="utf-8")
    named = {}
    for match in re.finditer(r"\.package\(([^)]*)\)", package):
        arguments = " ".join(match.group(1).split())
        url = re.search(r'\burl:\s*"([^"]+)"', arguments)
        exact = re.search(r'\bexact:\s*"([^"]+)"', arguments)
        if not url or not exact or ALLOWED_PACKAGES.get(url.group(1)) != exact.group(1):
            errors.append(f"macOS executable depends on an external package that is not allowed and "
                          f"pinned exactly: {arguments}")
        else:
            named[url.group(1)] = exact.group(1)
    if named:
        resolved = root / "macos/Package.resolved"
        pins = {}
        if resolved.is_file():
            for pin in json.loads(resolved.read_text(encoding="utf-8")).get("pins", []):
                pins[pin.get("location")] = (pin.get("state") or {}).get("version")
        for url, version in named.items():
            if pins.get(url) != version:
                errors.append(f"macos/Package.resolved does not pin {url} at {version}")
    for literal in re.findall(r'"([^"\n]*)"', package):
        if escapes(root, root / "macos", literal):
            errors.append(f"Unclosed dependency: macos/Package.swift -> {literal}")
    return errors


def worker_path_errors(root: Path) -> list[str]:
    """File-relative parents and literal import paths that leave the product."""
    errors = []
    pattern = re.compile(r"Path\(__file__\)(?:\.resolve\(\))?(?:\.parents\[(\d+)\]|((?:\.parent\b)+))")
    for path in _package().worker_files(root):
        text = path.read_text(encoding="utf-8", errors="replace")
        for node in ast.walk(ast.parse(text)):
            if not isinstance(node, ast.Call) or ast.unparse(node.func) not in (
                    "sys.path.insert", "sys.path.append", "sys.path.extend"):
                continue
            for argument in node.args:
                values = argument.elts if isinstance(argument, (ast.List, ast.Tuple)) else [argument]
                for value in values:
                    if not isinstance(value, ast.Constant) or not isinstance(value.value, str):
                        continue
                    literal = value.value.replace("\\", "/")
                    target = (path.parent / literal).resolve()
                    # Literal sys.path entries resolve from the launch working directory,
                    # not this source file. A parent traversal is never closed statically.
                    if ".." in literal.split("/") or not target.is_relative_to(root):
                        errors.append(f"Unclosed dependency: {path.relative_to(root).as_posix()} -> {value.value}")
        for match in pattern.finditer(text):
            depth = int(match.group(1)) if match.group(1) else match.group(2).count(".parent") - 1
            parents = path.resolve().parents
            if depth >= len(parents) or not parents[depth].is_relative_to(root):
                errors.append(f"Unclosed dependency: {path.relative_to(root).as_posix()} -> {match.group(0)}")
    return errors


def source_errors(root: Path, manifest: dict) -> list[str]:
    """Hash every manifest source at the pinned commit, read through git."""
    commit = manifest["source_commit"]
    try:
        subprocess.run(["git", "-C", str(root), "cat-file", "-e", f"{commit}^{{commit}}"],
                       capture_output=True, check=True)
        requests = "".join(f"{commit}:{entry['source']}\n" for entry in manifest["files"]).encode()
        output = subprocess.run(["git", "-C", str(root), "cat-file", "--batch"], input=requests,
                                capture_output=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError):
        return [f"Source hashes not checked: no git history containing {commit[:12]} at {root}"]
    errors, offset = [], 0
    for entry in manifest["files"]:
        end = output.index(b"\n", offset)
        header = output[offset:end].split()
        if header[-1] == b"missing":
            errors.append(f"Missing at {commit[:12]}: {entry['source']}")
            offset = end + 1
            continue
        size = int(header[2])
        blob = output[end + 1:end + 1 + size]
        offset = end + 1 + size + 1
        if hashlib.sha256(blob).hexdigest() != entry["source_sha256"]:
            errors.append(f"Source differs from the manifest at {commit[:12]}: {entry['source']}")
    return errors


# ─── Imported files ──────────────────────────────────────────────────────────

COMMIT = re.compile(r"[0-9a-f]{40}")


def import_errors(root: Path, manifest: dict, snapshot: bool = False) -> list[str]:
    """Every imported file is present, or deleted on purpose and named in the
    removed-ledger with a reason and the commit that holds its last copy."""
    errors = []
    imported = {entry["destination"] for entry in manifest["files"]}
    removed = Counter(entry.get("destination") for entry in manifest.get("removed", []))
    for entry in manifest.get("removed", []):
        destination = entry.get("destination")
        if destination not in imported:
            errors.append(f"Removed entry names no imported file: {destination}")
        if not str(entry.get("reason") or "").strip() or not COMMIT.fullmatch(str(entry.get("commit") or "")):
            errors.append(f"Removed entry needs a reason and the full commit that holds its last copy: {destination}")
    errors += [f"Removed twice: {destination}" for destination, count in removed.items() if count > 1]
    for entry in manifest["files"]:
        path = (root / entry["destination"]).resolve()
        inside = path.is_relative_to(root)
        if entry["destination"] in removed:
            if inside and path.exists():
                errors.append(f"Removed import is back: {entry['destination']} (delete it, or take it off the "
                              f"removed list in import-manifest.json)")
        elif not inside or not path.is_file():
            errors.append(f"Missing import: {entry['destination']}")
        elif snapshot and hashlib.sha256(path.read_bytes()).hexdigest() != entry["destination_sha256"]:
            errors.append(f"Changed since extraction: {entry['destination']}")
    return errors


def removed_errors(root: Path, manifest: dict) -> list[str]:
    """Each removed file's last copy, read through git at the commit its entry
    names. A commit the history does not hold (a shallow clone has only its tip)
    is one line for that commit, not one misleading line per file it holds."""
    removed = manifest.get("removed", [])
    if not removed:
        return []

    def batch_check(lines: list[str]) -> list[str]:
        return subprocess.run(["git", "-C", str(root), "cat-file", "--batch-check"],
                              input="".join(f"{line}\n" for line in lines).encode(),
                              capture_output=True, check=True).stdout.decode().splitlines()

    try:
        prefix = subprocess.run(["git", "-C", str(root), "rev-parse", "--show-prefix"],
                                capture_output=True, text=True, check=True).stdout.strip()
        commits = sorted({str(entry.get("commit")) for entry in removed})
        absent = {commit for commit, answer in zip(commits, batch_check([f"{c}^{{commit}}" for c in commits]))
                  if answer.split()[1:2] != ["commit"]}
        present = [entry for entry in removed if str(entry.get("commit")) not in absent]
        answers = batch_check([f"{entry.get('commit')}:{prefix}{entry.get('destination')}" for entry in present])
    except (OSError, subprocess.CalledProcessError):
        return [f"Removed files not checked: no git history at {root}"]
    errors = [f"Removed files not checked: no commit {commit[:12]} in the git history at {root}"
              for commit in sorted(absent)]
    return errors + [f"Removed file not at {str(entry.get('commit'))[:12]}: {entry.get('destination')}"
                     for entry, answer in zip(present, answers) if answer.split()[1:2] != ["blob"]]


def check(root: Path, snapshot: bool = False) -> list[str]:
    root = root.resolve()
    try:
        errors = project_errors(root) + package_errors(root) + worker_path_errors(root) + leak_errors(root)
    except ValueError as error:  # a shipped worker file is missing
        errors = [str(error)]
    if not (root / MANIFEST).is_file():
        if not exported_copy(root):
            present = ", ".join(name for name in MONOREPO_ONLY if (root / name).exists())
            errors.append(f"Missing {MANIFEST}: this is the monorepo tree ({present} is here), where provenance "
                          "is checked; only an exported copy goes without it")
        elif snapshot:  # an exported copy: provenance is checked in the monorepo
            errors.append(f"Provenance not checked: {MANIFEST} is not in this tree (an exported copy)")
        return errors
    manifest = json.loads((root / MANIFEST).read_text())
    errors += import_errors(root, manifest, snapshot)
    if snapshot:
        errors += source_errors(root, manifest) + removed_errors(root, manifest)
    return errors


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--snapshot", action="store_true",
                        help="also check imported files against the manifest and the pinned source commit")
    args = parser.parse_args()
    failures = check(ROOT, args.snapshot)
    for failure in failures:
        print(failure)
    summary = "standalone source boundary"
    if exported_copy(ROOT):
        summary += f"; provenance not checked: no {MANIFEST} in this tree (an exported copy)"
    elif args.snapshot and (ROOT / MANIFEST).is_file():
        manifest = json.loads((ROOT / MANIFEST).read_text())
        summary += (f" and {len(manifest['files'])} imported files, {len(manifest.get('removed', []))} of them "
                    f"removed on purpose (destination hashes, source hashes at {manifest['source_commit'][:12]}, "
                    f"and each removed file's last copy)")
    print(f"{'FAIL' if failures else 'PASS'}: {summary}")
    raise SystemExit(bool(failures))
