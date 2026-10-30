#!/usr/bin/env python3
"""Build a native Earlet preview from this repository and explicit local inputs.

A complete bundle requires an explicitly supplied, relocatable Python distribution
with provider dependencies installed and a local model. --app-only is a visibly
incomplete developer artifact. All outputs go outside versioned source.
"""
from __future__ import annotations

import argparse
import base64
import csv
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import plistlib
import shutil
import stat
import subprocess

ROOT = Path(__file__).resolve().parents[1]

# ─── Shipped worker ──────────────────────────────────────────────────────────
# The ops each host sends, read from its own source:
#   macOS (DictationAsrTransport): init (backend "mac", engine pinned to mlx),
#     transcribe, and for the HUD live preview stream_start, stream_push and
#     stream_cancel; shutdown when it stops the worker.
#   Windows (AsrWorkerHost, driven by Dictation.Core): init (backend "mac",
#     engine pinned to faster), transcribe, shutdown, and only with
#     DICTATION_PREVIEW=1 (off by default) stream_start, stream_push and
#     stream_cancel for the indicator's live preview; never stream_finish.
# Neither host sends ping; import_smoke below calls op_ping in-process. These
# modules are the import closure of those ops. tests/test_package.py stages
# exactly this set, drives both paths (the preview through a commit at a
# pause) through the staged copy, and fails when a shipped module imports a
# sibling that is neither shipped nor declared optional below.
WORKER_MODULES = ("__init__", "asr_server", "audio_energy", "beam_decode", "declared_casing",
                  "engine_lock", "hallucination", "learned_vocabulary", "context_vocabulary", "live_speakers", "mlx_beam", "mlx_cache",
                  "ops_stream", "ops_stream_whisper", "partial_gate", "spoken_aliases",
                  "whisper_mac", "whisper_profile", "zh_norm")
WORKER_DATA = ("default_aliases.json",)
# Imported somewhere in the shipped modules but missing from the package. Each
# import is caught by asr_server, sits in a branch neither host selects
# (meeting notes, diarization, voice enrolment), or is guarded in place, so
# the dictation path degrades instead of failing. The reason beside each name
# is what a pruning decision should trust, so it says where the import is.
WORKER_OPTIONAL = {'presentation_mode': 'notes presentation mode; guarded at stream_start',
 'attribute': 'meeting speaker attribution; sys-channel turns only',
 'aec': 'meeting echo cancellation; ops_stream_whisper calls it on the mic path at every push and '
        'falls back to the raw audio when the import fails',
 'emission_hold': 'single-emission caption mode; DICTATION_NOTES_SINGLE_EMISSION=1 only',
 'en_phonetic': 'vocabulary phonetics; bias-phrase branch only, and guarded',
 'neural_turns': 'single-emission turn detection; that mode is default OFF',
 'rolling_stable': 'rolling stable captions; rolling_stable=True only, never sent',
 'stream_repetition': 'caption cosmetics; guarded try/except',
 'turn_book': 'single-emission turn book; that mode is default OFF',
 'zh_phonetic': 'vocabulary phonetics; bias-phrase branch only, and guarded',
 'corpus': 'personal prior from a notes database; no host sends profile.db_path and asr_server '
           'skips the prior when the import fails',
 'trie_decode': 'openai-whisper engine only (torch); a pinned engine never falls back to it',
 'stream_helpers': 'source-only language voting and speaker-merge utilities; stream setup/commit '
                   'imports are guarded'}
PROVIDERS = {"windows": "faster_whisper", "macos": "mlx_whisper"}
# A CTranslate2 Whisper folder faster-whisper can load offline and decode with.
# preprocessor_config.json carries the mel-bin count: without it faster-whisper
# builds 80-bin features, and a Large v3 or Turbo export (128 bins) then loads
# and fails every decode. DictationWorkerPlan.RequiredModelFiles names the same four.
WINDOWS_MODEL_FILES = ("model.bin", "config.json", "tokenizer.json", "preprocessor_config.json")


def worker_files(root: Path = ROOT) -> list[Path]:
    """The worker files a package ships, in staging order."""
    worker = root / "runtime/transcription_worker"
    files = [worker / f"{name}.py" for name in WORKER_MODULES] + [worker / name for name in WORKER_DATA]
    missing = [path.name for path in files if not path.is_file()]
    if missing:
        raise ValueError(f"Worker files missing from runtime/transcription_worker: {missing}")
    return files


# ─── Runtime pruning (macOS) ─────────────────────────────────────────────────
# The supplied Python is a whole interpreter with the provider installed, and
# it carries more than the shipped worker imports. The packager removes these
# from its staged copy, never from the input. The reason beside each name is
# why nothing on the dictation path loads it. On the 0.2.1 runtime they were
# most of its 1.38 GB: torch alone was 601 MB.
MAC_PRUNED_DISTRIBUTIONS = {
    "torch": "declared by mlx-whisper 0.4.3, which imports it only in torch_whisper.py, its "
             "checkpoint converter; the worker and mlx_whisper.transcribe never load that module",
    "sympy": "torch only", "mpmath": "sympy only", "networkx": "torch only",
    "jinja2": "torch only", "markupsafe": "jinja2 only",
    "setuptools": "torch only; no shipped module imports setuptools or pkg_resources",
    "pip": "installs packages; the app never installs anything"}
#: A kept distribution may still declare a pruned one only where the reason
#: above says why the import never runs.
MAC_PRUNE_DECLARED = {("mlx-whisper", "torch")}
#: Interpreter parts no shipped module imports, relative to the Python root:
#: Tcl/Tk and what is built on it (tkinter, IDLE, turtle), the pip
#: bootstrapper, the C headers and man pages, every
#: package's own test suite (`tests`, never `testing`, which numpy and scipy
#: import at run time), and a setuptools build tree of mlx-whisper that the
#: 0.2.x runtime carried inside site-packages. Both hosts start bin/python3
#: only, and MAC_KEPT_BIN keeps nothing else in bin.
MAC_PRUNED_PATHS = ("include", "share",
                    "lib/python3.*/site-packages/**/tests", "lib/python3.*/site-packages/build/lib",
                    # Tcl/Tk 9 ships libtcl9tk9.0; older builds ship libtk8.6.
                    "lib/libtcl*", "lib/libtk*", "lib/tcl[0-9]*", "lib/tk[0-9]*", "lib/itcl[0-9]*",
                    "lib/thread[0-9]*", "lib/python3.*/tkinter", "lib/python3.*/idlelib",
                    "lib/python3.*/turtledemo", "lib/python3.*/turtle.py",
                    "lib/python3.*/__pycache__/turtle.*", "lib/python3.*/lib-dynload/_tkinter.*",
                    "lib/python3.*/ensurepip")
#: The only files bin keeps, with any `._pth` path file beside the interpreter,
#: which extend_path_files appends the runtime to. The others are the extra
#: copies of the interpreter that copy_tree materializes from bin's links, and
#: console scripts nothing runs, whose #! line names the machine that installed
#: them.
MAC_KEPT_BIN = ("python3",)
#: Every top-level name the pruning removes; the import smoke asserts the
#: staged interpreter finds none of them.
MAC_PRUNED_IMPORTS = ("torch", "functorch", "torchgen", "sympy", "isympy", "mpmath", "networkx", "jinja2",
                      "markupsafe", "setuptools", "pkg_resources", "_distutils_hack", "pip", "tkinter",
                      "_tkinter", "idlelib", "turtle", "turtledemo", "ensurepip")


def canonical_name(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def installed_distributions(python: Path) -> list[tuple[str, Path]]:
    """Each .dist-info in the Python's site-packages, with its canonical name."""
    return [(canonical_name(metadata_headers(info).get("Name", [info.name.split("-")[0]])[0]), info)
            for info in sorted([*python.glob("lib/python3*/site-packages/*.dist-info"),
                                *python.glob("lib/site-packages/*.dist-info")])]


def metadata_headers(info: Path) -> dict[str, list[str]]:
    """The METADATA header fields; the description after the first blank line is not read."""
    headers: dict[str, list[str]] = {}
    path = info / "METADATA"
    if not path.is_file():
        return headers
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if not line.strip():
            break
        key, colon, value = line.partition(":")
        if colon and not key.startswith((" ", "\t")):
            headers.setdefault(key.strip(), []).append(value.strip())
    return headers


def declared_requirements(info: Path) -> set[str]:
    """What a distribution requires outside its extras. Only a marker whose
    every flat ``or`` branch requires a nonempty extra is skipped. Nested
    expressions and platform markers count conservatively as requirements."""
    names = set()
    for requirement in metadata_headers(info).get("Requires-Dist", []):
        spec, _, marker = requirement.partition(";")
        match = re.match(r"\s*([A-Za-z0-9][A-Za-z0-9._-]*)", spec)
        branches = re.split(r"\bor\b", marker) if marker.strip() else []
        extras_only = bool(branches) and all(
            not any(char in branch for char in "()") and any(
                re.fullmatch(r'''extra\s*==\s*(['"])[A-Za-z0-9][A-Za-z0-9._-]*\1''', atom.strip())
                for atom in re.split(r"\band\b", branch))
            for branch in branches)
        if match and not extras_only:
            names.add(canonical_name(match.group(1)))
    return names


def recorded_paths(info: Path, python: Path) -> list[Path]:
    """The files a distribution's RECORD lists, refusing any outside the Python."""
    record = info / "RECORD"
    if not record.is_file():
        raise ValueError(f"{info.name} has no RECORD, so the files it owns are unknown")
    root = python.resolve()
    paths = []
    with record.open(encoding="utf-8", newline="") as stream:
        for row in csv.reader(stream):
            if not row or not row[0]:
                continue
            path = Path(os.path.normpath(info.parent.resolve() / row[0]))
            if not path.is_relative_to(root):
                raise ValueError(f"{info.name}/RECORD names a file outside the Python: {row[0]}")
            paths.append(path)
    return paths


def remove(path: Path) -> int:
    """Delete a file or a tree and return the bytes it held."""
    if path.is_dir() and not path.is_symlink():
        size = sum(item.stat().st_size for item in path.rglob("*") if item.is_file() and not item.is_symlink())
        shutil.rmtree(path)
        return size
    size = path.lstat().st_size if path.is_file() or path.is_symlink() else 0
    path.unlink(missing_ok=True)
    return size


def prune_mac_runtime(python: Path) -> dict:
    """Remove from a staged Mac Python what no shipped module imports.

    Refuses, before deleting anything, when a kept distribution requires a
    pruned one (unless MAC_PRUNE_DECLARED says why that import never runs),
    or a RECORD is missing, names a file outside the Python or names a
    directory. A pruned package directory goes whole, with the bytecode
    compiled after install, unless a kept distribution also installs into
    it (a namespace package); then only the pruned distribution's own files
    go.
    """
    root = python.resolve()
    installed = installed_distributions(python)
    pruned = [(name, info) for name, info in installed if name in MAC_PRUNED_DISTRIBUTIONS]
    kept = [(name, info) for name, info in installed if name not in MAC_PRUNED_DISTRIBUTIONS]
    needed = sorted(f"{owner} requires {name}" for owner, info in kept
                    for name in declared_requirements(info)
                    if name in MAC_PRUNED_DISTRIBUTIONS and (owner, name) not in MAC_PRUNE_DECLARED
                    and any(name == present for present, _ in pruned))
    if needed:
        raise ValueError(f"A kept distribution requires one the package prunes: {needed}")
    kept_files = {path for _, info in kept for path in recorded_paths(info, python)}
    sites = {info.parent.resolve() for _, info in kept}
    claimed = {path.relative_to(site).parts[0] for path in kept_files for site in sites
               if path.is_relative_to(site) and path != site}
    removed, removed_bytes = [], 0

    def take(path: Path, name: str) -> None:
        nonlocal removed_bytes
        removed_bytes += remove(path)
        removed.append(name)

    # Every RECORD is read, and checked, before anything is deleted. A row
    # naming a directory is refused, so a directory only ever goes whole
    # below, as an unclaimed package.
    records = [(info, recorded_paths(info, python)) for _, info in pruned]
    for info, paths in records:
        listed = [path for path in paths if path.is_dir() and not path.is_symlink()]
        if listed:
            raise ValueError(f"{info.name}/RECORD names a directory: {listed[0].relative_to(root)}")
    # The staged copy keeps the input's modes; deleting needs write access
    # to each directory.
    for folder in [python, *python.rglob("*")]:
        if folder.is_dir() and not folder.is_symlink() and not folder.stat().st_mode & stat.S_IWUSR:
            folder.chmod(folder.stat().st_mode | stat.S_IWUSR)
    for info, paths in records:
        site = info.parent.resolve()
        for path in paths:
            top = path.relative_to(site).parts[0] if path.is_relative_to(site) else None
            whole = top and top not in claimed and top != "__pycache__" and (site / top).is_dir()
            target = site / top if whole else path
            if target in kept_files or not (target.exists() or target.is_symlink()):
                continue
            take(target, str(target.relative_to(root)))
            parent = target.parent
            while parent != site and parent != root and parent.is_dir() and not any(parent.iterdir()):
                parent.rmdir()
                parent = parent.parent
        if info.exists():
            take(info, str(info.relative_to(python)))
    for pattern in MAC_PRUNED_PATHS:
        for path in sorted(python.glob(pattern)):
            # A test folder inside one already taken is gone with it.
            if path.exists() or path.is_symlink():
                take(path, str(path.relative_to(python)))
    for path in sorted(python.glob("bin/*")):
        if path.name not in MAC_KEPT_BIN and not path.name.endswith("._pth"):
            take(path, str(path.relative_to(python)))
    return {"distributions": sorted({name for name, _ in pruned}), "paths": sorted(removed),
            "bytes": removed_bytes}


# ─── Staging ─────────────────────────────────────────────────────────────────

def digest(path: Path) -> str:
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(chunk)
    return result.hexdigest()


def copy_tree(source: Path, target: Path) -> None:
    # A Python distribution often uses bin/python3 -> python3.x. Materialize
    # links so the resulting package does not depend on the builder's filesystem.
    # The distribution's own __pycache__ stays: stripping it made every launch
    # recompile, or write bytecode into the installed package.
    shutil.copytree(source, target, symlinks=False,
                    ignore=shutil.ignore_patterns(".pytest_cache", ".DS_Store"))


def python_executable(python: Path, target: str) -> Path:
    return python / ("python.exe" if target == "windows" else "bin/python3")


def check_inputs(target: str, python: Path | None, model: Path | None, app_only: bool,
                 rid: str = "win-x64") -> None:
    """Refuse an incomplete complete package before anything is built or copied."""
    if app_only:
        return
    if python is None or model is None:
        raise ValueError("Complete packages require --python-runtime and --model-dir")
    if target == "windows" and rid == "win-arm64":
        # faster-whisper needs ctranslate2, which publishes no win_arm64 wheel.
        raise ValueError("No Windows ARM64 speech provider exists yet; build win-x64 or --app-only")
    if not python_executable(python, target).is_file() or (python / "pyvenv.cfg").exists():
        raise ValueError("Supply a relocatable Python distribution, not a virtualenv")
    expected = WINDOWS_MODEL_FILES if target == "windows" else ("config.json",)
    if not all((model / name).is_file() for name in expected):
        raise ValueError(f"Missing local {target} model files: {expected}")
    if target == "macos" and not any((model / n).is_file() for n in ("weights.safetensors", "model.safetensors")):
        raise ValueError("macOS requires a local MLX Whisper export")


def extend_path_files(python: Path, runtime: Path, target: str) -> list[str]:
    """Put the worker on sys.path of an embeddable Python.

    A ``python*._pth`` beside the interpreter replaces sys.path entirely and
    turns on isolated mode, so PYTHONPATH and the working directory, which is
    how both hosts find ``transcription_worker``, are ignored. Append the
    runtime directory and enable site-packages.
    """
    changed = []
    for pth in sorted([*python.glob("*._pth"), *python.glob("bin/*._pth")]):
        raw = pth.read_bytes().decode("utf-8")
        newline = "\r\n" if "\r\n" in raw else "\n"
        lines = raw.splitlines()
        entry = os.path.relpath(runtime, pth.parent)
        entry = entry.replace("/", "\\") if target == "windows" else entry
        stripped = [line.strip() for line in lines]
        if entry not in stripped:
            lines.append(entry)
        if "import site" not in stripped:
            if "#import site" in stripped:
                lines[stripped.index("#import site")] = "import site"
            else:
                lines.append("import site")
        # The staged copy keeps the input's modes, and a read-only input's
        # path file is read-only too.
        pth.chmod(pth.stat().st_mode | stat.S_IWUSR)
        pth.write_bytes((newline.join(lines) + newline).encode("utf-8"))
        changed.append(str(pth.relative_to(python)))
    return changed


def host_runs(target: str) -> bool:
    """Whether this machine can execute the target's staged interpreter."""
    return platform.system() == {"windows": "Windows", "macos": "Darwin"}[target]


def worker_environment(runtime: Path) -> dict[str, str]:
    """The environment the import smoke runs the staged worker in: the
    builder's PYTHON* variables removed and user site-packages off, the
    isolation the Windows host launches the worker with (-I -B -u by script
    path, PythonAsrWorkerProcess).

    It used to say "the environment both hosts give the worker", which the
    macOS launch did not match (the app's whole environment, -m). Whether the
    Mac isolates its worker is decided, and must be tested, in
    DictationAsrTransport and PythonEngineClient; a passing smoke here is not
    evidence about it."""
    environment = {key: value for key, value in os.environ.items() if not key.startswith("PYTHON")}
    environment.update(PYTHONPATH=str(runtime), PYTHONNOUSERSITE="1", PYTHONDONTWRITEBYTECODE="1",
                       HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1")
    return environment


def import_smoke(executable: Path, runtime: Path, target: str) -> None:
    """Import the worker and its provider the way the hosts launch it, and on
    macOS check that the pruned modules are gone."""
    probe = (f"import transcription_worker.asr_server as server, {PROVIDERS[target]}\n"
             "assert server.op_ping({})['ok']\n")
    if target == "macos":
        # Only a module inside the staged Python counts: a test interpreter
        # borrows the builder's standard library.
        root = os.path.realpath(executable.parents[1]) + os.sep
        probe += ("import importlib.util, os\n"
                  "def staged(spec):\n"
                  "    places = [spec.origin] if spec.origin else list(spec.submodule_search_locations or ())\n"
                  f"    return any(os.path.realpath(place).startswith({root!r}) for place in places if place)\n"
                  f"found = [name for name in {MAC_PRUNED_IMPORTS!r}\n"
                  "         if (spec := importlib.util.find_spec(name)) and staged(spec)]\n"
                  "assert not found, f'pruned modules still import: {found}'\n")
    try:
        subprocess.run([str(executable), "-c", probe], cwd=runtime, env=worker_environment(runtime),
                       capture_output=True, text=True, timeout=300, check=True)
    except (OSError, subprocess.SubprocessError) as error:
        detail = getattr(error, "stderr", None) or str(error)
        raise ValueError(f"The staged Python cannot import the worker: {detail.strip()[-600:]}") from error


def compile_worker(executable: Path, runtime: Path, jobs: int) -> None:
    """Ship worker bytecode so no launch recompiles or writes into the package."""
    # checked-hash survives installers that rewrite mtimes; -d keeps the
    # builder's directory out of every code object.
    subprocess.run([str(executable), "-m", "compileall", "-q", f"-j{jobs}",
                    "--invalidation-mode", "checked-hash", "-d", "transcription_worker",
                    str(runtime / "transcription_worker")],
                   env=worker_environment(runtime), capture_output=True, text=True, check=True)


def recompile_mac_runtime(executable: Path, python: Path, jobs: int) -> None:
    """Rewrite the staged Mac Python's bytecode so no code object names the
    machine that compiled it.

    The supplied runtime's .pyc files carry the absolute path it was installed
    at (a home directory and a checkout) as each code object's file name, which
    tracebacks print. Recompiled with that prefix stripped, they say
    "python/lib/…". unchecked-hash: the signed bundle never changes, so an
    import neither stats nor hashes the source.
    """
    subprocess.run([str(executable), "-m", "compileall", "-q", "-f", f"-j{jobs}",
                    "--invalidation-mode", "unchecked-hash", "-s", str(python), "-p", "python",
                    str(python / "lib")],
                   capture_output=True, text=True, check=True)


def stage_resources(resources: Path, target: str, python: Path | None, model: Path | None,
                    app_only: bool, rid: str = "win-x64", run_python: bool | None = None,
                    jobs: int = 1) -> dict:
    """Stage the worker (and for a complete package, Python and model).

    Returns receipt facts. ``run_python`` defaults to whether this host can run
    the staged interpreter; when it runs, the staged worker must import.
    """
    check_inputs(target, python, model, app_only, rid)
    resources.mkdir(parents=True, exist_ok=True)
    runtime = resources / "runtime"
    (runtime / "transcription_worker").mkdir(parents=True)
    # Only the dictation path ships: no tests, benchmarks, corpora, meeting,
    # diarization, QNN or Genie-client modules belong in an installed app. The
    # live-preview stream ops (ops_stream*, and the modules their dictation
    # path imports) ARE on that path since the HUD preview shipped.
    for source in worker_files():
        shutil.copy2(source, runtime / "transcription_worker" / source.name)
    facts: dict = {}
    if not app_only:
        copy_tree(python, resources / "python")
        copy_tree(model, resources / "model")
        if target == "macos":
            facts["pruned"] = prune_mac_runtime(resources / "python")
        facts["python_path_files"] = extend_path_files(resources / "python", runtime, target)
        if run_python is None:
            run_python = host_runs(target)
        if run_python:
            executable = python_executable(resources / "python", target)
            import_smoke(executable, runtime, target)
            if target == "macos":
                recompile_mac_runtime(executable, resources / "python", jobs)
            compile_worker(executable, runtime, jobs)
            facts["import_smoke"] = "passed"
        else:
            facts["import_smoke"] = f"not run: this {platform.system()} host cannot run the {target} interpreter"
    # What each app keeps of the audio, as SECURITY.md says it: both keep one
    # take that could not be transcribed, for 7 days at most; the Mac also
    # keeps delivered dictations' recordings, sealed, in its history.
    retention = ("one failed take for 7 days" if target == "windows" else
                 "one failed take for 7 days; recordings in the encrypted history, 30 days by default")
    (resources / "package-mode.json").write_text(json.dumps({
        "mode": "app-only" if app_only else "complete-preview",
        "provider": "faster" if target == "windows" else "mlx",
        "audio_retention": retention, "network_inference": False,
    }, indent=2) + "\n")
    return facts


# ─── Release hygiene ─────────────────────────────────────────────────────────

# ─── In-place updates (macOS) ────────────────────────────────────────────────

#: What Sparkle needs from Info.plist (docs/design-macos.md#updates). The feed
#: and the EdDSA public key come from product.json; the rest is policy: check
#: once a day, download and install a found update when Earlet next quits,
#: and check the archive's signature before unpacking it.
UPDATE_POLICY = {"SUEnableAutomaticChecks": True, "SUAutomaticallyUpdate": True,
                 "SUScheduledCheckInterval": 86400, "SUVerifyUpdateBeforeExtraction": True}


def update_keys(product: dict) -> dict:
    """The Info.plist keys that turn updates on, or none when the product names no feed."""
    updates = product.get("updates")
    if not updates:
        return {}
    feed, key = updates.get("feed", ""), updates.get("ed25519_public", "")
    if not feed.startswith("https://") or len(base64.b64decode(key, validate=True)) != 32:
        raise ValueError("product.json updates need an https feed and a 32-byte EdDSA public key")
    return {"SUFeedURL": feed, "SUPublicEDKey": key, **UPDATE_POLICY}


#: Sparkle's MIT license and the notices of the code it bundles, from the
#: archive SwiftPM downloaded; the framework itself carries no copy.
SPARKLE_LICENSE = ROOT / "macos/.build/artifacts/sparkle/Sparkle/LICENSE"


def embed_sparkle(binaries: Path, contents: Path) -> None:
    """Copy the Sparkle framework SwiftPM linked into Contents/Frameworks.

    Its XPC services exist for sandboxed apps, which Earlet is not
    (sparkle-project.org/documentation/sandboxing), so they are left out
    rather than signed. The executable finds the framework beside itself in
    .build; in the bundle it looks in Frameworks.
    """
    framework = binaries / "Sparkle.framework"
    if not framework.is_dir():
        raise ValueError(f"swift build produced no Sparkle.framework in {binaries}")
    if not SPARKLE_LICENSE.is_file():
        raise ValueError(f"Sparkle's license is missing at {SPARKLE_LICENSE}; resolve the Swift package")
    target = contents / "Frameworks/Sparkle.framework"
    shutil.copytree(framework, target, symlinks=True)
    shutil.rmtree(target / "Versions/B/XPCServices", ignore_errors=True)
    (contents / "Resources/Licenses").mkdir(parents=True, exist_ok=True)
    shutil.copy2(SPARKLE_LICENSE, contents / "Resources/Licenses/Sparkle.txt")
    subprocess.run(["install_name_tool", "-add_rpath", "@executable_path/../Frameworks",
                    str(contents / "MacOS/Dictation")], check=True)


def strip_symbols(bundle: Path) -> list[str]:
    """Delete debug symbol files; they carry source paths and SourceLink maps."""
    removed = []
    for path in sorted(bundle.rglob("*.pdb")):
        path.unlink()
        removed.append(str(path.relative_to(bundle)))
    return removed


def source_revision() -> str | None:
    try:
        return subprocess.run(["git", "-C", str(ROOT), "rev-parse", "HEAD"], capture_output=True,
                              text=True, check=True).stdout.strip() or None
    except (OSError, subprocess.CalledProcessError):
        return None


def builder_markers() -> list[bytes]:
    """Byte strings that identify this checkout, its builder or the private repository."""
    texts = {str(ROOT), "raw.githubusercontent.com/scamai"}
    home = Path.home()
    if len(home.parts) >= 3:  # never a bare "/" or "/root"
        texts.add(str(home))
    revision = source_revision()  # SourceLink and +<sha> versions carry it
    if revision:
        texts.add(revision)
    markers = []
    for text in sorted(texts):
        markers += [text.encode("utf-8"), text.encode("utf-16-le")]
    return markers


def builder_path_hits(folder: Path, skip: tuple[Path, ...] = ()) -> list[str]:
    markers = builder_markers()
    hits = []
    for path in sorted(folder.rglob("*")):
        if not path.is_file() or any(path.is_relative_to(prefix) for prefix in skip):
            continue
        data = path.read_bytes()
        if any(marker in data for marker in markers):
            hits.append(str(path.relative_to(folder)))
    return hits


def audit_bundle(bundle: Path, resources: Path) -> None:
    """Fail on a builder path in anything this script built; warn about supplied inputs."""
    inputs = (resources / "python", resources / "model")
    built = builder_path_hits(bundle, skip=inputs)
    if built:
        raise ValueError(f"Package embeds a builder path or private source map: {built[:10]}")
    supplied = [f"{folder.name}/{hit}" for folder in inputs if folder.is_dir()
                for hit in builder_path_hits(folder)]
    if supplied:
        print(f"warning: supplied Python/model files name the builder's paths: {supplied[:10]}")


# ─── Build ───────────────────────────────────────────────────────────────────

def build(args: argparse.Namespace) -> Path:
    system = platform.system()
    if args.platform == "macos" and system != "Darwin":
        raise ValueError("macOS packages must be built and signed on a Mac")
    if args.output.exists():
        raise ValueError("Output already exists; select a fresh directory to preserve prior packages")
    check_inputs(args.platform, args.python_runtime, args.model_dir, args.app_only, args.rid)
    product = json.loads((ROOT / "product.json").read_text())
    name = product["name"]
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9 -]{0,63}", name) or name.endswith(" "):
        raise ValueError("Product name must be a safe application filename")
    # The build id the launch log line and the verification records cite.
    build_id = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    if args.platform == "windows":
        # windows/Directory.Build.props maps source paths and turns SourceLink
        # off for Release; no symbols are produced at all.
        subprocess.run([args.dotnet, "publish", str(ROOT / "windows/Dictation.App/Dictation.App.csproj"),
                        "-c", "Release", "-r", args.rid, "--self-contained", "true",
                        f"-m:{args.jobs}", f"-p:Version={product['version']}", f"-p:DictationBuild={build_id}",
                        "-p:DebugType=none", "-p:DebugSymbols=false",
                        "-o", str(args.output / name)], check=True)
        bundle = args.output / name
        resources = bundle / "resources"
    else:
        subprocess.run(["swift", "build", "--package-path", str(ROOT / "macos"),
                        "-c", "release", "--jobs", str(args.jobs), "--product", "Dictation"], check=True)
        binary_path = subprocess.check_output(["swift", "build", "--package-path", str(ROOT / "macos"),
                        "-c", "release", "--show-bin-path"], text=True).strip()
        bundle = args.output / f"{name}.app"
        contents = bundle / "Contents"
        (contents / "MacOS").mkdir(parents=True)
        shutil.copy2(Path(binary_path) / "Dictation", contents / "MacOS/Dictation")
        # The unstripped binary lists every object file under the checkout's
        # .build/ in its debug map (measured: audit_bundle fails without this).
        subprocess.run(["strip", "-S", "-x", str(contents / "MacOS/Dictation")], check=True)
        embed_sparkle(Path(binary_path), contents)
        with (contents / "Info.plist").open("wb") as stream:
            plistlib.dump({"CFBundleIdentifier": product["bundle_id"], "CFBundleName": product["name"],
                          "CFBundleDisplayName": product["name"], "CFBundleExecutable": "Dictation",
                          # Sparkle orders builds by CFBundleVersion, so it is the
                          # build id, which only grows; the version is for people.
                          "CFBundlePackageType": "APPL", "CFBundleVersion": build_id,
                          "CFBundleShortVersionString": product["version"],
                          "DictationBuild": build_id,
                          "LSMinimumSystemVersion": "14.0", "NSHighResolutionCapable": True,
                          # A second copy would add a second key tap and a second model.
                          "LSMultipleInstancesProhibited": True,
                          "CFBundleIconFile": "AppIcon",
                          "NSMicrophoneUsageDescription": f"{name} listens while you dictate and turns your speech into text on this Mac.",
                          **update_keys(product)}, stream)
        resources = contents / "Resources"
        resources.mkdir(exist_ok=True)
        shutil.copy2(ROOT / "macos/Resources/AppIcon.icns", resources / "AppIcon.icns")
    facts = stage_resources(resources, args.platform, args.python_runtime, args.model_dir,
                            args.app_only, args.rid, jobs=args.jobs)
    facts["stripped_symbols"] = strip_symbols(bundle)
    audit_bundle(bundle, resources)
    if args.platform == "macos":
        identity = getattr(args, "sign_identity", None) or os.environ.get("DICTATION_SIGN_IDENTITY", "-")
        subprocess.run(["codesign", "--force", "--deep", "--sign", identity, str(bundle)], check=True)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True)
    files = {}
    for path in sorted(bundle.rglob("*")):
        if path.is_file():
            files[str(path.relative_to(bundle))] = digest(path)
    # This is a build receipt, not a release/signing/ASR acceptance claim.
    (args.output / "build-receipt.json").write_text(json.dumps({
        "product": product, "platform": args.platform, "build": build_id,
        "mode": "app-only" if args.app_only else "complete-preview",
        "validation": "build-and-layout-only", **facts, "files": files,
    }, indent=2) + "\n")
    return bundle


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=["windows", "macos"], required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--python-runtime", type=Path)
    parser.add_argument("--model-dir", type=Path)
    parser.add_argument("--app-only", action="store_true")
    parser.add_argument("--rid", choices=["win-x64", "win-arm64"], default="win-x64")
    parser.add_argument("--jobs", type=int, default=min(4, os.cpu_count() or 1))
    parser.add_argument("--dotnet", default="dotnet")
    # macOS only: a stable identity keeps TCC grants across rebuilds; "-"
    # stays ad-hoc. DICTATION_SIGN_IDENTITY overrides the default.
    parser.add_argument("--sign-identity", default=os.environ.get("DICTATION_SIGN_IDENTITY", "-"))
    args = parser.parse_args()
    if args.jobs < 1:
        parser.error("--jobs must be positive")
    if args.app_only and (args.python_runtime or args.model_dir):
        parser.error("--app-only cannot be combined with model/runtime inputs")
    if not args.app_only and (not args.python_runtime or not args.model_dir):
        parser.error("Complete packages require --python-runtime and --model-dir")
    args.output = args.output.resolve()
    try:
        print(build(args))
    except (ValueError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Package failed: {error}\n")


if __name__ == "__main__":
    main()
