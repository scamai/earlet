"""What tools/package.py puts in a package, observed in the staged files.

Synthetic Python distributions, models and providers test layout, routing and
refusals. They are not recognition, installation or device evidence.
"""
import ast
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys

import pytest

from harness import (CT2_MODEL, FAKE_FASTER, FAKE_MLX, ROOT, ct2_model, mac_init, mlx_model, run_worker,
                     staged_runtime, tool, transcribe_frame, windows_init, write_providers)

WORKER = ROOT / "runtime/transcription_worker"


# ─── The shipped worker ──────────────────────────────────────────────────────

def sibling_imports(path, siblings):
    """Every transcription_worker module ``path`` imports, however spelled."""
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.ImportFrom):
            parts = (node.module or "").split(".")
            if node.level == 1:
                yield from ([parts[0]] if node.module else [alias.name for alias in node.names])
            elif parts[0] == "transcription_worker":
                yield from ([parts[1]] if len(parts) > 1 else [alias.name for alias in node.names])
            elif parts[0] in siblings:
                yield parts[0]
        elif isinstance(node, ast.Import):
            for alias in node.names:
                parts = alias.name.split(".")
                if parts[0] == "transcription_worker" and len(parts) > 1:
                    yield parts[1]
                elif parts[0] in siblings:
                    yield parts[0]


def test_the_package_ships_exactly_the_declared_worker(tmp_path):
    package = tool("package")
    staged = staged_runtime(tmp_path) / "transcription_worker"
    expected = {f"{name}.py" for name in package.WORKER_MODULES} | set(package.WORKER_DATA)
    assert {path.name for path in staged.iterdir()} == expected
    # The spoken-alias table ships beside its reader on both platforms, and so
    # does the reader of the host's learning.json, which spoken_aliases and
    # asr_server import on every transcribe.
    assert (staged / "default_aliases.json").is_file()
    assert (staged / "learned_vocabulary.py").is_file()
    assert (staged / "context_vocabulary.py").is_file()
    assert len(package.WORKER_MODULES) == 19
    # Each must exist in the source, or its absence from the package proves
    # nothing: when a pruning step deletes one, drop it here.
    for dormant in ("stream_helpers.py", "corpus.py"):
        assert (WORKER / dormant).is_file(), f"{dormant} is gone from the source: drop it from this list"
        assert not (staged / dormant).exists(), dormant


def test_every_import_in_a_shipped_module_is_shipped_or_declared_optional():
    package = tool("package")
    siblings = {path.stem for path in WORKER.glob("*.py")}
    allowed = set(package.WORKER_MODULES) | set(package.WORKER_OPTIONAL)
    undeclared = {(name, imported) for name in package.WORKER_MODULES
                  for imported in sibling_imports(WORKER / f"{name}.py", siblings)
                  if imported not in allowed}
    assert undeclared == set()
    # A declaration must name a real, unshipped module, or it hides nothing.
    assert set(package.WORKER_OPTIONAL) <= siblings - set(package.WORKER_MODULES)


def test_the_staged_worker_serves_the_windows_dictation_path(tmp_path):
    runtime = staged_runtime(tmp_path)
    model = ct2_model(tmp_path / "model")
    providers = write_providers(tmp_path / "providers", faster_whisper=FAKE_FASTER)
    replies, events, stderr = run_worker(
        runtime, [{"op": "ping"}, windows_init(model), transcribe_frame(), {"op": "shutdown"}],
        "faster", providers, tmp_path)
    assert [reply.get("ok") for reply in replies] == [True, True, True, True], stderr
    assert replies[2]["text"] == "hello from faster"
    assert ["load", "faster_whisper", str(model), {"device": "cpu", "compute_type": "int8"}] in events


def test_the_staged_worker_loads_the_macos_engine(tmp_path):
    runtime = staged_runtime(tmp_path)
    model = mlx_model(tmp_path / "model")
    providers = write_providers(tmp_path / "providers", mlx_whisper=FAKE_MLX)
    replies, events, stderr = run_worker(
        runtime, [{"op": "ping"}, mac_init(model), {"op": "shutdown"}], "mlx", providers, tmp_path)
    assert [reply.get("ok") for reply in replies] == [True, True, True], stderr
    assert ["import", "mlx_whisper"] in events


PREVIEW_SAID = "We ship the build on Friday"


def test_the_staged_worker_serves_the_dictation_preview_stream(tmp_path):
    """The HUD live preview's wire on the staged copy: stream_start, then
    speech, a pause and more speech pushed as the Mac host pushes them, then
    stream_cancel.

    The pause is what matters: the preview commits a line at every pause,
    through _commit_guarded, the commit gate, spoken aliases and zh_norm, so
    one push and a cancel (all this test used to send) never reached that
    path, and a module missing from the package there shipped green. The
    commit lands on a decode thread, so short silences are pushed until a
    reply carries it. The worker ISSUES the stream id (the router ignores a
    caller-chosen one), so this drives the worker interactively.
    """
    import base64
    import struct
    import time

    from harness import _frame, worker_env

    def ask(process, frame):
        process.stdin.write(_frame(frame))
        process.stdin.flush()
        (size,) = struct.unpack(">I", process.stdout.read(4))
        return json.loads(process.stdout.read(size).decode("utf-8"))

    def push(seconds=None, silence=None):
        pcm = (transcribe_frame(seconds)["pcm_b64"] if seconds
               else base64.b64encode(bytes(int(32000 * silence))).decode("ascii"))
        reply = ask(process, {"op": "stream_push", "stream_id": stream_id, "pcm_b64": pcm,
                              "channel": "mic"})
        assert reply["ok"], reply
        return reply

    runtime = staged_runtime(tmp_path)
    model = ct2_model(tmp_path / "model")
    providers = write_providers(tmp_path / "providers", faster_whisper=FAKE_FASTER)
    process = subprocess.Popen(
        [sys.executable, "-m", "transcription_worker.asr_server"],
        cwd=runtime, env=worker_env(runtime, "faster", providers, tmp_path,
                                    {"FAKE_PROVIDER_TEXT": PREVIEW_SAID}),
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    commits, shown = [], ""
    try:
        assert ask(process, {"op": "ping"})["ok"]
        assert ask(process, windows_init(model))["ok"]
        started = ask(process, {"op": "stream_start", "client": "dictation",
                                "languages": ["en"]})
        assert started["ok"], started
        stream_id = started["stream_id"]
        replies = [push(seconds=1.0), push(silence=1.0), push(seconds=1.0)]
        deadline = time.monotonic() + 10.0
        while not any(c.get("speaker") == "You" for r in replies for c in r["commits"]) \
                and time.monotonic() < deadline:
            time.sleep(0.05)
            replies.append(push(silence=0.1))
        commits = [c for r in replies for c in r["commits"]]
        shown = replies[-1]["text"]
        assert ask(process, {"op": "stream_cancel", "stream_id": stream_id})["ok"]
        assert ask(process, {"op": "shutdown"})["ok"]
    finally:
        process.kill()
        process.wait()
    stderr = process.stderr.read().decode("utf-8", "replace")
    assert "Traceback" not in stderr, "a missing optional module must degrade, not crash"
    assert {"speaker": "You", "text": PREVIEW_SAID} in [
        {"speaker": c["speaker"], "text": c["text"]} for c in commits], (commits, stderr[-800:])
    assert f"[You] {PREVIEW_SAID}" in shown.splitlines(), shown


def test_the_staged_stream_module_joins_text_without_optional_helpers(tmp_path):
    """The staged stream uses the shipped CJK-aware join directly. Replacing
    that shared function must change the stream's join too; optional source
    utilities must not be needed by the package."""
    from harness import worker_env

    runtime = staged_runtime(tmp_path)
    assert not (runtime / "transcription_worker/stream_helpers.py").exists()
    probe = ("from transcription_worker import ops_stream_whisper as stream, whisper_mac\n"
             "print(stream._join_text('你好', '世界'))\n"
             "print(stream._join_text('see you', 'on Friday'))\n"
             "whisper_mac._join_span_texts = lambda left, right: left + '|' + right\n"
             "print(stream._join_text('one rule', 'one copy'))\n")
    completed = subprocess.run([sys.executable, "-c", probe], cwd=runtime, capture_output=True,
                               env=worker_env(runtime, "faster", tmp_path, tmp_path), timeout=60)
    assert completed.returncode == 0, completed.stderr.decode("utf-8", "replace")[-800:]
    assert completed.stdout.decode("utf-8").splitlines() == ["你好世界", "see you on Friday",
                                                             "one rule|one copy"]


# ─── The staged Python ───────────────────────────────────────────────────────

EMBEDDABLE_PTH = "python311.zip\r\n.\r\n\r\n# Uncomment to run site.main() automatically\r\n#import site\r\n"


def windows_python(folder, pth=EMBEDDABLE_PTH):
    """The python.org embeddable layout: python.exe and python311._pth at the root."""
    folder.mkdir(parents=True)
    (folder / "python.exe").write_bytes(b"synthetic-interpreter")
    if pth is not None:
        (folder / "python311._pth").write_bytes(pth.encode("utf-8"))
    return folder


def test_an_embeddable_python_is_given_the_worker_path(tmp_path):
    python = windows_python(tmp_path / "embeddable")
    resources = tmp_path / "Dictation/resources"
    facts = tool("package").stage_resources(resources, "windows", python, ct2_model(tmp_path / "model"),
                                            False, run_python=False)
    staged = (resources / "python/python311._pth").read_bytes().decode("utf-8")
    assert staged.split("\r\n") == ["python311.zip", ".", "", "# Uncomment to run site.main() automatically",
                                    "import site", "..\\runtime", ""]
    assert facts["python_path_files"] == ["python311._pth"]
    assert facts["import_smoke"].startswith("not run")
    # The builder's own distribution is left as it was.
    assert (python / "python311._pth").read_bytes().decode("utf-8") == EMBEDDABLE_PTH


def test_the_distributions_own_bytecode_is_kept(tmp_path):
    python = windows_python(tmp_path / "embeddable", pth=None)
    (python / "Lib/__pycache__").mkdir(parents=True)
    (python / "Lib/__pycache__/os.cpython-311.pyc").write_bytes(b"synthetic-bytecode")
    resources = tmp_path / "Dictation/resources"
    facts = tool("package").stage_resources(resources, "windows", python, ct2_model(tmp_path / "model"),
                                            False, run_python=False)
    assert (resources / "python/Lib/__pycache__/os.cpython-311.pyc").is_file()
    assert facts["python_path_files"] == []


def test_a_cross_build_says_the_import_smoke_did_not_run(tmp_path, monkeypatch):
    package = tool("package")
    monkeypatch.setattr(package.platform, "system", lambda: "Linux")
    facts = package.stage_resources(tmp_path / "Dictation/resources", "windows",
                                    windows_python(tmp_path / "embeddable"), ct2_model(tmp_path / "model"), False)
    assert facts["import_smoke"] == "not run: this Linux host cannot run the windows interpreter"


def test_a_staged_python_that_cannot_import_the_worker_fails_the_package(tmp_path, monkeypatch):
    if os.name == "nt":
        pytest.skip("the stand-in interpreter is a POSIX shell script")
    python = tmp_path / "broken-python"
    (python / "bin").mkdir(parents=True)
    (python / "bin/python3").write_text("#!/bin/sh\necho 'No module named transcription_worker' >&2\nexit 1\n")
    (python / "bin/python3").chmod(0o755)
    package = tool("package")
    # A Mac builds the Mac package, so the smoke runs without being asked.
    monkeypatch.setattr(package.platform, "system", lambda: "Darwin")
    with pytest.raises(ValueError, match="cannot import the worker"):
        package.stage_resources(tmp_path / "Dictation.app/Contents/Resources", "macos", python,
                                mlx_model(tmp_path / "model"), False)


def relocatable_python(folder):
    """A copy of this interpreter whose sys.path comes only from a ._pth file,
    the way an embeddable distribution's does. Its site-packages holds a stand-in
    MLX provider; the standard library and numpy are this machine's."""
    import numpy
    import sysconfig
    (folder / "bin").mkdir(parents=True)
    shutil.copy2(os.path.realpath(sys.executable), folder / "bin/python3")
    site = folder / "lib/site-packages/mlx_whisper"
    site.mkdir(parents=True)
    (site / "__init__.py").write_text("")
    entries = [sysconfig.get_paths()["stdlib"], sysconfig.get_paths()["platstdlib"],
               *(entry for entry in sys.path if entry.endswith("lib-dynload")),
               str(Path(numpy.__file__).parents[1]), "../lib/site-packages"]
    (folder / "bin/python3._pth").write_text("\n".join(dict.fromkeys(entries)) + "\n")
    probe = subprocess.run([str(folder / "bin/python3"), "-c", "import sys; print(sys.flags.isolated)"],
                           capture_output=True, text=True)
    if probe.returncode != 0 or probe.stdout.strip() != "1":
        pytest.skip(f"this interpreter cannot run from a copy under a ._pth: {probe.stderr.strip()[-200:]}")
    return folder


def test_a_path_file_python_imports_the_staged_worker(tmp_path):
    python = relocatable_python(tmp_path / "input-python")
    resources = tmp_path / "Dictation.app/Contents/Resources"
    facts = tool("package").stage_resources(resources, "macos", python, mlx_model(tmp_path / "model"),
                                            False, run_python=True, jobs=2)
    assert facts == {"python_path_files": ["bin/python3._pth"], "import_smoke": "passed",
                     "pruned": {"distributions": [], "paths": [], "bytes": 0}}
    bytecode = list((resources / "runtime/transcription_worker/__pycache__").glob("*.pyc"))
    assert len(bytecode) == len(tool("package").WORKER_MODULES) == 19
    assert any(path.name.startswith("learned_vocabulary.") for path in bytecode)
    assert any(path.name.startswith("context_vocabulary.") for path in bytecode)
    assert not [path for path in bytecode if str(tmp_path).encode() in path.read_bytes()]


# ─── The pruned Mac Python ───────────────────────────────────────────────────

def distribution(site, name, version, files, requires=(), metadata_name=None, record_self=True, rows=()):
    """A .dist-info as pip leaves it: METADATA, RECORD listing every file
    (paths relative to site-packages, ../../../bin for scripts) and itself.
    ``rows`` adds RECORD rows for files the distribution does not write."""
    info = site / f"{name}-{version}.dist-info"
    info.mkdir(parents=True)
    headers = [f"Name: {metadata_name or name}", f"Version: {version}",
               *(f"Requires-Dist: {requirement}" for requirement in requires)]
    # The description follows a blank line and is not metadata: read as a
    # header, it would make every kept distribution require jinja2.
    (info / "METADATA").write_text("\n".join(headers) + "\n\nRequires-Dist: jinja2\n")
    for relative, content in files.items():
        path = Path(os.path.normpath(site / relative))
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)
    listed = [*files, *rows, *([f"{info.name}/METADATA", f"{info.name}/RECORD"] if record_self else [])]
    (info / "RECORD").write_text("".join(f"{row},,\n" for row in listed))
    return info


SITE = "lib/python3.11/site-packages"


def mac_runtime(folder):
    """The layout of the 0.2.1 runtime, shrunk: what the worker uses and what it never imports."""
    site = folder / SITE
    for name, content in {"bin/python3": b"interpreter", "bin/python3.11": b"interpreter",
                          "bin/python": b"interpreter", "bin/python3.11-config": b"#!config",
                          "bin/python3-config": b"#!config", "bin/idle3": b"#!idle", "bin/idle3.11": b"#!idle",
                          "lib/libpython3.11.dylib": b"libpython", "lib/libtcl9.0.dylib": b"tcl",
                          "lib/libtcl9tk9.0.dylib": b"tk", "lib/tcl9.0/init.tcl": b"tcl-script",
                          "lib/tcl9/tcl9.0.tm": b"tcl-module", "lib/tk9.0/tk.tcl": b"tk-script",
                          # Older python-build-standalone releases ship Tk 8.6 as its own library.
                          "lib/libtk8.6.dylib": b"tk8",
                          "lib/itcl4.3.8/itcl.tcl": b"itcl", "lib/thread3.0.6/pkgIndex.tcl": b"thread",
                          "share/man/man1/python3.11.1": b"python-man",
                          "lib/python3.11/os.py": b"# os", "lib/python3.11/turtle.py": b"# turtle",
                          "lib/python3.11/__pycache__/turtle.cpython-311.pyc": b"turtle-bytecode",
                          "lib/python3.11/__pycache__/os.cpython-311.pyc": b"os-bytecode",
                          "lib/python3.11/tkinter/__init__.py": b"# tkinter",
                          "lib/python3.11/idlelib/__init__.py": b"# idle",
                          "lib/python3.11/turtledemo/__init__.py": b"# turtledemo",
                          "lib/python3.11/ensurepip/__init__.py": b"# ensurepip",
                          "lib/python3.11/lib-dynload/_tkinter.cpython-311-darwin.so": b"tkinter-ext",
                          "lib/python3.11/lib-dynload/_ssl.cpython-311-darwin.so": b"ssl-ext"}.items():
        (folder / name).parent.mkdir(parents=True, exist_ok=True)
        (folder / name).write_bytes(content)
    distribution(site, "torch", "2.14.0", {"torch/__init__.py": b"torch", "torch/lib/libtorch.dylib": b"x" * 4096,
                                           "functorch/__init__.py": b"functorch", "torchgen/__init__.py": b"torchgen",
                                           "../../../bin/torchrun": b"#!torchrun",
                                           # The only file in its folder, which then goes too.
                                           "../../../share/torch/notes.txt": b"notes"},
                 requires=["sympy>=1.13.3", "jinja2", "filelock",
                           'triton~=3.8.0; platform_system == "Linux"'])
    # Bytecode compiled after install is not in RECORD; the package directory goes whole.
    (site / "torch/__pycache__").mkdir()
    (site / "torch/__pycache__/__init__.cpython-311.pyc").write_bytes(b"torch-bytecode")
    distribution(site, "sympy", "1.14.0", {"sympy/__init__.py": b"sympy", "isympy.py": b"isympy",
                                           "__pycache__/isympy.cpython-311.pyc": b"isympy-bytecode",
                                           "../../../bin/isympy": b"#!isympy",
                                           "../../../share/man/man1/isympy.1": b"isympy-man"},
                 requires=["mpmath<1.4,>=1.1.0"])
    # A RECORD that does not list its own .dist-info still loses it.
    distribution(site, "mpmath", "1.3.0", {"mpmath/__init__.py": b"mpmath"}, record_self=False)
    distribution(site, "networkx", "3.6.1", {"networkx/__init__.py": b"networkx"})
    distribution(site, "jinja2", "3.1.6", {"jinja2/__init__.py": b"jinja2"}, requires=["MarkupSafe>=2.0"])
    distribution(site, "markupsafe", "3.0.3", {"markupsafe/__init__.py": b"markupsafe"}, metadata_name="MarkupSafe")
    # setuptools also drops a file into a directory a kept distribution owns,
    # and its RECORD names a file that a kept distribution owns as well.
    distribution(site, "setuptools", "84.0.0", {"setuptools/__init__.py": b"setuptools",
                                                "_distutils_hack/__init__.py": b"hack",
                                                "distutils-precedence.pth": b"import _distutils_hack",
                                                "mlx/_setuptools_plugin.py": b"plugin"}, rows=["mlx/core.py"])
    distribution(site, "pip", "26.2.1", {"pip/__init__.py": b"pip", "../../../bin/pip": b"#!pip",
                                         "../../../bin/pip3.11": b"#!pip"})
    distribution(site, "mlx", "0.32.2", {"mlx/core.py": b"mlx", "mlx/lib/libmlx.dylib": b"libmlx"})
    distribution(site, "mlx_whisper", "0.4.3", {"mlx_whisper/__init__.py": b"mlx-whisper",
                                                "mlx_whisper/torch_whisper.py": b"import torch",
                                                "../../../bin/mlx_whisper": b"#!mlx_whisper"},
                 requires=["mlx>=0.11", "numba", "numpy", "torch", "tqdm", "huggingface_hub", "scipy"],
                 metadata_name="mlx-whisper")
    distribution(site, "huggingface_hub", "2.0.0", {"huggingface_hub/__init__.py": b"hub"},
                 requires=["filelock", 'Jinja2; extra == "templates"'], metadata_name="huggingface-hub")
    distribution(site, "filelock", "4.0.5", {"filelock/__init__.py": b"filelock"})
    # A kept module compiled after install shares site-packages/__pycache__.
    distribution(site, "six", "1.17.0", {"six.py": b"six"})
    (site / "__pycache__/six.cpython-311.pyc").write_bytes(b"six-bytecode")
    return folder


MAC_PRUNED = {"bin/python", "bin/python3.11", "bin/python3.11-config", "bin/idle3", "bin/idle3.11", "bin/torchrun",
              "bin/isympy", "bin/pip", "bin/pip3.11", "share/torch/notes.txt", "share/man/man1/isympy.1",
              "lib/libtcl9.0.dylib", "lib/libtcl9tk9.0.dylib", "lib/libtk8.6.dylib", "lib/tcl9.0", "lib/tcl9",
              "lib/tk9.0", "lib/itcl4.3.8", "lib/thread3.0.6", "lib/python3.11/turtle.py",
              "lib/python3.11/__pycache__/turtle.cpython-311.pyc", "lib/python3.11/tkinter",
              "lib/python3.11/idlelib", "lib/python3.11/turtledemo", "lib/python3.11/ensurepip",
              "lib/python3.11/lib-dynload/_tkinter.cpython-311-darwin.so",
              *(f"{SITE}/{name}" for name in (
                  "torch", "functorch", "torchgen", "torch-2.14.0.dist-info", "sympy", "isympy.py",
                  "__pycache__/isympy.cpython-311.pyc", "sympy-1.14.0.dist-info", "mpmath",
                  "mpmath-1.3.0.dist-info", "networkx", "networkx-3.6.1.dist-info", "jinja2",
                  "jinja2-3.1.6.dist-info", "markupsafe", "markupsafe-3.0.3.dist-info", "setuptools",
                  "_distutils_hack", "distutils-precedence.pth", "mlx/_setuptools_plugin.py",
                  "setuptools-84.0.0.dist-info", "pip", "pip-26.2.1.dist-info"))}


def tree_bytes(folder):
    return {str(path.relative_to(folder)): path.stat().st_size for path in folder.rglob("*") if path.is_file()}


def stage_mac(tmp_path, python):
    return tool("package").stage_resources(tmp_path / "Earlet.app/Contents/Resources", "macos", python,
                                           mlx_model(tmp_path / "model"), False, run_python=False)


def staged_python(tmp_path):
    return tmp_path / "Earlet.app/Contents/Resources/python"


def test_staging_a_mac_python_prunes_what_the_worker_never_imports(tmp_path):
    python = mac_runtime(tmp_path / "input-python")
    before = tree_bytes(python)
    facts = stage_mac(tmp_path, python)
    staged = staged_python(tmp_path)
    after = tree_bytes(staged)
    gone = set(before) - set(after)
    # Every pruned path is gone, with everything under it, and nothing else is.
    assert {name for name in gone if not any(name.startswith(f"{p}/") for p in MAC_PRUNED)} == MAC_PRUNED & set(before)
    assert not any((staged / name).exists() for name in MAC_PRUNED)
    assert set(after) <= set(before)
    for kept in ("bin/python3", "bin/python3-config", "bin/mlx_whisper", "lib/libpython3.11.dylib",
                 "lib/python3.11/os.py", "lib/python3.11/__pycache__/os.cpython-311.pyc",
                 "lib/python3.11/lib-dynload/_ssl.cpython-311-darwin.so", "share/man/man1/python3.11.1",
                 f"{SITE}/mlx/core.py", f"{SITE}/mlx/lib/libmlx.dylib", f"{SITE}/mlx_whisper/torch_whisper.py",
                 f"{SITE}/huggingface_hub/__init__.py", f"{SITE}/__pycache__/six.cpython-311.pyc",
                 f"{SITE}/filelock-4.0.5.dist-info/RECORD"):
        assert kept in after, kept
    # A folder its last pruned file leaves empty goes; one still holding a kept file stays.
    assert not (staged / "share/torch").exists() and (staged / "share/man/man1").is_dir()
    assert facts["pruned"] == {
        "distributions": ["jinja2", "markupsafe", "mpmath", "networkx", "pip", "setuptools", "sympy", "torch"],
        "paths": sorted(MAC_PRUNED), "bytes": sum(size for name, size in before.items() if name in gone)}
    # The builder's own Python is left as it was.
    assert tree_bytes(python) == before


def test_a_windows_python_is_not_pruned(tmp_path):
    python = windows_python(tmp_path / "embeddable")
    # Lower-case paths, which the Mac pruning's globs would match on any filesystem.
    distribution(python / "lib/site-packages", "torch", "2.14.0", {"torch/__init__.py": b"torch"})
    (python / "lib/python3.11/ensurepip").mkdir(parents=True)
    (python / "lib/python3.11/ensurepip/__init__.py").write_bytes(b"ensurepip")
    before = tree_bytes(python)
    facts = tool("package").stage_resources(tmp_path / "Earlet/resources", "windows", python,
                                            ct2_model(tmp_path / "model"), False, run_python=False)
    assert "pruned" not in facts
    # python311._pth is rewritten on purpose, so names are compared, not sizes.
    assert set(before) <= set(tree_bytes(tmp_path / "Earlet/resources/python"))


@pytest.mark.parametrize("requirement, refused", [
    ("Jinja2>=3", True), ('jinja2; platform_system == "Linux"', True),
    ('jinja2; sys_platform == "darwin" or extra == "templates"', True),
    ('jinja2; extra != "templates"', True), ('jinja2; sys_platform == "extra"', True),
    ('jinja2; extra == ""', True),
    ('jinja2; (extra == "templates" or sys_platform == "darwin")', True),
    ('jinja2; extra == "templates" and python_version >= "3.11"', False),
    ('jinja2; extra == "templates"', False), ('jinja2; extra == "a" or extra == "b"', False)])
def test_a_kept_distribution_that_requires_a_pruned_one_stops_the_package(tmp_path, requirement, refused):
    python = mac_runtime(tmp_path / "input-python")
    distribution(python / SITE, "needs_jinja", "1.0", {"needs_jinja.py": b"import jinja2"},
                 requires=[requirement], metadata_name="needs-jinja")
    if refused:
        with pytest.raises(ValueError, match=r"needs-jinja requires jinja2"):
            stage_mac(tmp_path, python)
        assert (staged_python(tmp_path) / SITE / "jinja2/__init__.py").is_file()
    else:
        assert "jinja2" in stage_mac(tmp_path, python)["pruned"]["distributions"]


def test_only_mlx_whisper_may_declare_torch(tmp_path):
    python = mac_runtime(tmp_path / "input-python")
    distribution(python / SITE, "needs_torch", "1.0", {"needs_torch.py": b"import torch"},
                 requires=["torch"], metadata_name="needs-torch")
    with pytest.raises(ValueError, match=r"\['needs-torch requires torch'\]"):
        stage_mac(tmp_path, python)


def test_a_requirement_on_a_pruned_distribution_that_is_absent_is_not_refused(tmp_path):
    python = mac_runtime(tmp_path / "input-python")
    site = python / SITE
    for name in ("setuptools", "_distutils_hack", "setuptools-84.0.0.dist-info"):
        shutil.rmtree(site / name)
    (site / "distutils-precedence.pth").unlink()
    (site / "mlx/_setuptools_plugin.py").unlink()
    distribution(site, "needs_setuptools", "1.0", {"needs_setuptools.py": b""}, requires=["setuptools"],
                 metadata_name="needs-setuptools")
    assert "setuptools" not in stage_mac(tmp_path, python)["pruned"]["distributions"]


def test_a_record_naming_a_file_outside_the_python_is_refused(tmp_path):
    python = mac_runtime(tmp_path / "input-python")
    outside = tmp_path / "Earlet.app/Contents/Resources/model/config.json"
    record = python / SITE / "pip-26.2.1.dist-info/RECORD"
    record.write_text(record.read_text() + "../../../../model/config.json,,\n")
    with pytest.raises(ValueError, match="outside the Python"):
        stage_mac(tmp_path, python)
    assert outside.is_file()
    # Every RECORD is checked first: jinja2 sorts before pip and is still there.
    assert (staged_python(tmp_path) / SITE / "jinja2").is_dir()


@pytest.mark.parametrize("row", ["mlx/", "../../../bin", "__pycache__", ".", "../"])
def test_a_record_naming_a_directory_is_refused(tmp_path, row):
    # Only files may be listed: a directory row would take a kept package,
    # bin/, the shared bytecode or site-packages itself with it.
    python = mac_runtime(tmp_path / "input-python")
    record = python / SITE / "pip-26.2.1.dist-info/RECORD"
    record.write_text(record.read_text() + f"{row},,\n")
    before = tree_bytes(python)
    with pytest.raises(ValueError, match="names a directory"):
        stage_mac(tmp_path, python)
    assert set(tree_bytes(staged_python(tmp_path))) == set(before)


@pytest.mark.parametrize("owner", ["mlx-0.32.2", "torch-2.14.0"])
def test_a_distribution_without_a_record_is_refused(tmp_path, owner):
    # Without RECORD the files a distribution owns are unknown: a kept one
    # could lose its namespace directory, a pruned one would stay importable.
    python = mac_runtime(tmp_path / "input-python")
    (python / SITE / f"{owner}.dist-info/RECORD").unlink()
    before = tree_bytes(python)
    with pytest.raises(ValueError, match=f"{owner}.dist-info has no RECORD"):
        stage_mac(tmp_path, python)
    assert set(tree_bytes(staged_python(tmp_path))) == set(before)


def test_a_read_only_input_python_is_still_pruned(tmp_path):
    if os.name == "nt":
        pytest.skip("POSIX permissions")
    python = mac_runtime(tmp_path / "input-python")
    folders = [python, *(path for path in python.rglob("*") if path.is_dir())]
    for path in python.rglob("*"):
        if path.is_file():
            path.chmod(0o444)
    for folder in reversed(folders):
        folder.chmod(0o555)
    try:
        facts = stage_mac(tmp_path, python)
    finally:
        for folder in folders:
            folder.chmod(0o755)
    staged = staged_python(tmp_path)
    assert "torch" in facts["pruned"]["distributions"]
    assert not (staged / SITE / "torch").exists() and not (staged / "lib/tcl9").exists()
    assert (staged / "bin/python3").is_file()


def test_the_mac_requirements_leave_out_what_the_package_prunes():
    package = tool("package")
    pinned = {package.canonical_name(line.split("==")[0])
              for line in (ROOT / "runtime/requirements-macos.txt").read_text().splitlines()
              if line and not line.startswith((" ", "#"))}
    assert {"mlx-whisper", "numpy", "numba", "scipy", "huggingface-hub"} <= pinned
    assert pinned & set(package.MAC_PRUNED_DISTRIBUTIONS) == set()


def test_the_import_smoke_looks_for_every_top_level_name_the_pruning_removes(tmp_path):
    facts = stage_mac(tmp_path, mac_runtime(tmp_path / "input-python"))
    names = set()
    for path in facts["pruned"]["paths"]:
        parts = Path(path).parts
        nested = len(parts) == 4 and parts[2] in ("site-packages", "lib-dynload")
        if parts[:2] == ("lib", "python3.11") and (nested or len(parts) == 3):
            name = parts[-1]
            if name != "__pycache__" and not name.endswith((".dist-info", ".pth")):
                names.add(name.split(".")[0])
    assert names == {"torch", "functorch", "torchgen", "sympy", "isympy", "mpmath", "networkx", "jinja2",
                     "markupsafe", "setuptools", "_distutils_hack", "pip", "tkinter", "_tkinter", "idlelib",
                     "turtle", "turtledemo", "ensurepip"}
    assert names <= set(tool("package").MAC_PRUNED_IMPORTS)


def first_on_path(python, entry):
    pth = python / "bin/python3._pth"
    lines = [line for line in pth.read_text().splitlines() if line != entry]
    pth.write_text("\n".join([entry, *lines]) + "\n")


@pytest.mark.parametrize("survivor", tool("package").MAC_PRUNED_IMPORTS)
def test_the_import_smoke_fails_when_a_pruned_module_survives(tmp_path, survivor):
    # A module with no .dist-info escapes the distribution pruning; the
    # smoke, run by the staged interpreter, still finds it.
    python = relocatable_python(tmp_path / "input-python")
    (python / "lib/site-packages" / survivor).mkdir()
    (python / "lib/site-packages" / survivor / "__init__.py").write_text("")
    first_on_path(python, "../lib/site-packages")  # ahead of this machine's own copies
    with pytest.raises(ValueError, match=f"pruned modules still import: \\['{survivor}'\\]"):
        tool("package").stage_resources(tmp_path / "Earlet.app/Contents/Resources", "macos", python,
                                        mlx_model(tmp_path / "model"), False, run_python=True)


def test_the_import_smoke_finds_a_surviving_namespace_package(tmp_path):
    # No __init__.py: the spec has no origin, only search locations. A name
    # no machine installs, since a regular package elsewhere would win.
    package = tool("package")
    package.MAC_PRUNED_IMPORTS = ("earlet_pruned_namespace",)
    python = relocatable_python(tmp_path / "input-python")
    (python / "lib/site-packages/earlet_pruned_namespace").mkdir()
    (python / "lib/site-packages/earlet_pruned_namespace/part.py").write_text("")
    with pytest.raises(ValueError, match=r"pruned modules still import: \['earlet_pruned_namespace'\]"):
        package.stage_resources(tmp_path / "Earlet.app/Contents/Resources", "macos", python,
                                mlx_model(tmp_path / "model"), False, run_python=True)


def test_the_import_smoke_ignores_modules_outside_the_staged_python(tmp_path):
    # The test interpreter borrows this machine's standard library; a pruned
    # name found there is not in the package.
    python = relocatable_python(tmp_path / "input-python")
    borrowed = tmp_path / "builder-site"
    (borrowed / "pip").mkdir(parents=True)
    (borrowed / "pip/__init__.py").write_text("")
    first_on_path(python, str(borrowed))
    facts = tool("package").stage_resources(tmp_path / "Earlet.app/Contents/Resources", "macos", python,
                                            mlx_model(tmp_path / "model"), False, run_python=True)
    assert facts["import_smoke"] == "passed"


# ─── Refused and materialized inputs ─────────────────────────────────────────

def mac_python(folder):
    (folder / "bin").mkdir(parents=True)
    (folder / "bin/python3").write_bytes(b"synthetic-interpreter")
    return folder


@pytest.mark.parametrize("target, missing", [
    ("windows", "model.bin"), ("windows", "config.json"), ("windows", "tokenizer.json"),
    ("windows", "preprocessor_config.json"),
    ("macos", "config.json"), ("macos", "weights.safetensors")])
def test_a_complete_package_refuses_an_incomplete_model(tmp_path, target, missing):
    if target == "windows":
        python = windows_python(tmp_path / "python")
        model = ct2_model(tmp_path / "model", [n for n in CT2_MODEL if n != missing])
    else:
        python = mac_python(tmp_path / "python")
        model = mlx_model(tmp_path / "model", [n for n in ("config.json", "weights.safetensors") if n != missing])
    with pytest.raises(ValueError, match="model files|MLX Whisper export"):
        tool("package").stage_resources(tmp_path / "out", target, python, model, False, run_python=False)
    assert not (tmp_path / "out").exists()


def test_a_macos_export_may_name_its_weights_model_safetensors(tmp_path):
    model = mlx_model(tmp_path / "model", ["config.json", "model.safetensors"])
    tool("package").stage_resources(tmp_path / "out", "macos", mac_python(tmp_path / "python"), model,
                                    False, run_python=False)
    assert (tmp_path / "out/model/model.safetensors").is_file()


@pytest.mark.parametrize("target", ["windows", "macos"])
def test_a_complete_package_refuses_a_python_without_its_interpreter(tmp_path, target):
    python = tmp_path / "python"
    (python / "lib").mkdir(parents=True)
    model = ct2_model(tmp_path / "model") if target == "windows" else mlx_model(tmp_path / "model")
    with pytest.raises(ValueError, match="relocatable Python"):
        tool("package").stage_resources(tmp_path / "out", target, python, model, False, run_python=False)
    assert not (tmp_path / "out").exists()


def test_links_in_the_supplied_inputs_are_copied_as_files(tmp_path):
    if os.name == "nt":
        pytest.skip("creating symlinks needs developer mode on Windows")
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "libpython3.11.dylib").write_bytes(b"library-bytes")
    (outside / "weights.safetensors").write_bytes(b"weight-bytes")
    python = tmp_path / "python"
    (python / "bin").mkdir(parents=True)
    (python / "lib").mkdir()
    (python / "bin/python3.11").write_bytes(b"interpreter-bytes")
    os.symlink("python3.11", python / "bin/python3")  # relative, as distributions ship it
    os.symlink(outside / "libpython3.11.dylib", python / "lib/libpython3.11.dylib")  # absolute
    model = mlx_model(tmp_path / "model", ["config.json"])
    os.symlink(outside / "weights.safetensors", model / "weights.safetensors")
    resources = tmp_path / "Dictation.app/Contents/Resources"
    tool("package").stage_resources(resources, "macos", python, model, False, run_python=False)
    for folder in (python, model, outside):
        shutil.rmtree(folder)
    for staged, content in (("python/bin/python3", b"interpreter-bytes"),
                            ("python/lib/libpython3.11.dylib", b"library-bytes"),
                            ("model/weights.safetensors", b"weight-bytes")):
        assert not (resources / staged).is_symlink(), staged
        assert (resources / staged).read_bytes() == content


def build_args(tmp_path, **overrides):
    import argparse
    values = dict(platform="windows", output=tmp_path / "dist", python_runtime=None, model_dir=None,
                  app_only=True, rid="win-x64", jobs=1, dotnet="dotnet")
    values.update(overrides)
    return argparse.Namespace(**values)


@pytest.mark.parametrize("rid, model_names, message", [
    ("win-arm64", CT2_MODEL, "ARM64"),
    ("win-x64", ("model.bin", "config.json"), "model files")])
def test_a_refused_complete_package_builds_nothing(tmp_path, monkeypatch, rid, model_names, message):
    package = tool("package")
    commands = []
    monkeypatch.setattr(package.subprocess, "run", lambda command, **_: commands.append(command))
    args = build_args(tmp_path, app_only=False, rid=rid, python_runtime=windows_python(tmp_path / "python"),
                      model_dir=ct2_model(tmp_path / "model", model_names))
    with pytest.raises(ValueError, match=message):
        package.build(args)
    assert commands == [] and not args.output.exists()


def test_an_arm64_app_only_preview_is_still_allowed():
    tool("package").check_inputs("windows", None, None, True, "win-arm64")


@pytest.mark.parametrize("name", ["../Elsewhere", "App/Name", "App\\Name", "", "App ", 7])
def test_a_product_name_cannot_redirect_package_output(tmp_path, monkeypatch, name):
    package = tool("package")
    source = tmp_path / "source"
    source.mkdir()
    (source / "product.json").write_text(json.dumps({"name": name}))
    monkeypatch.setattr(package, "ROOT", source)
    commands = []
    monkeypatch.setattr(package.subprocess, "run", lambda command, **_: commands.append(command))
    args = build_args(tmp_path)
    with pytest.raises(ValueError, match="safe application filename"):
        package.build(args)
    assert commands == [] and not args.output.exists()


@pytest.mark.parametrize("name, provider", [("requirements-windows.txt", "faster-whisper"),
                                            ("requirements-macos.txt", "mlx-whisper")])
def test_provider_requirements_are_pinned(name, provider):
    requirements = [line.split("#")[0].strip() for line in (ROOT / "runtime" / name).read_text().splitlines()]
    requirements = [line for line in requirements if line]
    unpinned = [line for line in requirements if not re.fullmatch(r"[A-Za-z0-9_.-]+==[A-Za-z0-9_.+!-]+", line)]
    assert requirements and unpinned == []
    assert {"numpy", provider} <= {line.split("==")[0] for line in requirements}


# ─── Release hygiene, through build() ────────────────────────────────────────

class FakeToolchain:
    """Stands in for dotnet, swift, strip, install_name_tool and codesign and
    records each call. Anything else (git) runs for real. Its swift build
    leaves a Sparkle.framework beside the executable, as SwiftPM does."""

    def __init__(self, tmp_path, assembly=b"clean-assembly"):
        self.commands, self.assembly, self.bin = [], assembly, tmp_path / "swift-bin"
        self.real = subprocess.run

    def __call__(self, command, **options):
        command = [str(part) for part in command]
        name = Path(command[0]).name
        if name not in ("fake-dotnet", "swift", "strip", "install_name_tool", "codesign"):
            return self.real(command, **options)
        self.commands.append(command)
        if name == "fake-dotnet":
            output = Path(command[command.index("-o") + 1])
            output.mkdir(parents=True)
            (output / "Dictation.exe").write_bytes(b"MZ-synthetic-apphost")
            (output / "Dictation.dll").write_bytes(self.assembly)
            (output / "Dictation.pdb").write_bytes(b"synthetic-portable-pdb")
        elif name == "swift" and "--show-bin-path" in command:
            return subprocess.CompletedProcess(command, 0, stdout=f"{self.bin}\n")
        elif name == "swift":
            self.bin.mkdir(parents=True, exist_ok=True)
            (self.bin / "Dictation").write_bytes(self.assembly)
            framework = self.bin / "Sparkle.framework"
            for part in ("Sparkle", "Autoupdate", "Updater.app/Contents/MacOS/Updater",
                         "XPCServices/Installer.xpc/Contents/MacOS/Installer"):
                (framework / "Versions/B" / part).parent.mkdir(parents=True, exist_ok=True)
                (framework / "Versions/B" / part).write_bytes(b"synthetic-mach-o")
            if not (framework / "Versions/Current").exists():
                (framework / "Versions/Current").symlink_to("B")
                (framework / "Sparkle").symlink_to("Versions/Current/Sparkle")
        return subprocess.CompletedProcess(command, 0, stdout="")


def fake_build(tmp_path, monkeypatch, assembly=b"clean-assembly", **overrides):
    package = tool("package")
    toolchain = FakeToolchain(tmp_path, assembly)
    monkeypatch.setattr(package.subprocess, "run", toolchain)
    license_text = tmp_path / "sparkle-artifact/LICENSE"
    license_text.parent.mkdir(parents=True)
    license_text.write_text("Copyright (c) 2006-2013 Andy Matuschak.\n")
    monkeypatch.setattr(package, "SPARKLE_LICENSE", license_text)
    args = build_args(tmp_path, dotnet="fake-dotnet", **overrides)
    return package, toolchain, args


def test_a_windows_package_ships_no_symbols(tmp_path, monkeypatch):
    package, toolchain, args = fake_build(tmp_path, monkeypatch)
    bundle = package.build(args)
    assert bundle.name == "Earlet"
    assert (bundle / "Dictation.exe").is_file()
    assert not list(bundle.rglob("*.pdb"))
    receipt = json.loads((args.output / "build-receipt.json").read_text())
    assert receipt["stripped_symbols"] == ["Dictation.pdb"]
    assert not [name for name in receipt["files"] if name.endswith(".pdb")]
    assert {"-p:DebugType=none", "-p:DebugSymbols=false"} <= set(toolchain.commands[0])


def test_a_windows_package_stamps_its_build_for_the_launch_line(tmp_path, monkeypatch):
    # The app logs "launch Version … · Build <stamp> path=…"; the stamp reaches
    # it as assembly metadata from this property, and the receipt names it too.
    package, toolchain, args = fake_build(tmp_path, monkeypatch)
    package.build(args)
    build = json.loads((args.output / "build-receipt.json").read_text())["build"]
    assert len(build) == 14 and build.isascii() and build.isdigit()
    assert f"-p:DictationBuild={build}" in toolchain.commands[0]


def marker_cases():
    package = tool("package")
    cases = {"checkout path": str(package.ROOT).encode(),
             "checkout path, UTF-16": str(package.ROOT).encode("utf-16-le"),
             "source map": b'{"documents":{"/_/*":"https://raw.githubusercontent.com/scamai/example/0/*"}}'}
    if len(Path.home().parts) >= 3:
        cases["home directory"] = f"{Path.home()}/build/obj/Dictation.pdb".encode()
    if package.source_revision():
        cases["source revision"] = f"0.1.0+{package.source_revision()}".encode("utf-16-le")
    return cases


@pytest.mark.parametrize("marker", list(marker_cases()))
def test_a_package_naming_its_builder_is_refused(tmp_path, monkeypatch, marker):
    assembly = b"MZ..." + marker_cases()[marker] + b"...rest"
    package, _, args = fake_build(tmp_path, monkeypatch, assembly)
    with pytest.raises(ValueError, match="builder path"):
        package.build(args)


def test_supplied_inputs_naming_the_builder_warn_without_failing(tmp_path, monkeypatch, capsys):
    python = windows_python(tmp_path / "python", pth=None)
    (python / "Scripts").mkdir()
    (python / "Scripts/hf.exe").write_bytes(f"#!{tool('package').ROOT}/python.exe".encode())
    package, _, args = fake_build(tmp_path, monkeypatch, app_only=False, python_runtime=python,
                                  model_dir=ct2_model(tmp_path / "model"))
    monkeypatch.setattr(package.platform, "system", lambda: "Linux")
    package.build(args)
    assert "python/Scripts/hf.exe" in capsys.readouterr().out


def test_a_macos_binary_is_stripped_before_signing(tmp_path, monkeypatch):
    package, toolchain, args = fake_build(tmp_path, monkeypatch, platform="macos")
    monkeypatch.setattr(package.platform, "system", lambda: "Darwin")
    bundle = package.build(args)
    names = [Path(command[0]).name for command in toolchain.commands]
    strips = [index for index, name in enumerate(names) if name == "strip"]
    # Signing seals the binary; a strip after it would break the signature.
    assert strips and max(strips) < names.index("codesign")
    assert toolchain.commands[strips[0]][-1] == str(bundle / "Contents/MacOS/Dictation")


def test_the_macos_bundle_is_its_own_single_instance_app(tmp_path, monkeypatch):
    import plistlib
    package, _, args = fake_build(tmp_path, monkeypatch, platform="macos")
    monkeypatch.setattr(package.platform, "system", lambda: "Darwin")
    bundle = package.build(args)
    info = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
    assert bundle.name == "Earlet.app"
    assert info["CFBundleName"] == "Earlet"
    assert info["CFBundleDisplayName"] == "Earlet"
    assert info["CFBundleExecutable"] == "Dictation"
    assert info["NSMicrophoneUsageDescription"].startswith("Earlet records")
    assert info["CFBundleIdentifier"] == json.loads((ROOT / "product.json").read_text())["bundle_id"]
    assert info["LSMultipleInstancesProhibited"] is True
    # Multiple previews share the product version; the actual signed bundle
    # must carry a build label the permission recovery UI can display.
    assert len(info["DictationBuild"]) == 14
    assert info["DictationBuild"].isascii() and info["DictationBuild"].isdigit()
    assert json.loads((args.output / "build-receipt.json").read_text())["build"] == info["DictationBuild"]


def macos_build(tmp_path, monkeypatch, **overrides):
    import plistlib
    package, toolchain, args = fake_build(tmp_path, monkeypatch, platform="macos", **overrides)
    monkeypatch.setattr(package.platform, "system", lambda: "Darwin")
    bundle = package.build(args)
    return package, toolchain, args, bundle, plistlib.loads((bundle / "Contents/Info.plist").read_bytes())


def test_sparkle_orders_builds_by_a_version_that_only_grows(tmp_path, monkeypatch):
    # CFBundleVersion is what Sparkle compares: the build id, never the
    # product version, which many builds share.
    *_, info = macos_build(tmp_path, monkeypatch)
    assert info["CFBundleVersion"] == info["DictationBuild"]
    assert info["CFBundleShortVersionString"] == json.loads((ROOT / "product.json").read_text())["version"]


def test_the_macos_bundle_follows_the_product_update_feed(tmp_path, monkeypatch):
    *_, info = macos_build(tmp_path, monkeypatch)
    updates = json.loads((ROOT / "product.json").read_text())["updates"]
    assert info["SUFeedURL"] == updates["feed"] and info["SUFeedURL"].startswith("https://")
    assert info["SUPublicEDKey"] == updates["ed25519_public"]
    assert info["SUEnableAutomaticChecks"] is True and info["SUAutomaticallyUpdate"] is True
    assert info["SUScheduledCheckInterval"] == 86400
    assert info["SUVerifyUpdateBeforeExtraction"] is True


@pytest.mark.parametrize("updates, accepted", [
    (None, True),
    ({"feed": "http://example.com/appcast.xml", "ed25519_public": "A" * 43 + "="}, False),
    ({"feed": "https://example.com/appcast.xml", "ed25519_public": "short"}, False),
    ({"feed": "https://example.com/appcast.xml", "ed25519_public": "A" * 43 + "="}, True),
])
def test_an_update_feed_must_be_https_with_a_whole_key(updates, accepted):
    package = tool("package")
    product = {"name": "Earlet"} | ({"updates": updates} if updates else {})
    if accepted:
        keys = package.update_keys(product)
        assert ("SUFeedURL" in keys) == bool(updates)
    else:
        with pytest.raises(Exception):
            package.update_keys(product)


def test_sparkle_is_embedded_without_its_sandbox_services(tmp_path, monkeypatch):
    _package, toolchain, _args, bundle, _info = macos_build(tmp_path, monkeypatch)
    framework = bundle / "Contents/Frameworks/Sparkle.framework"
    assert (framework / "Versions/B/Sparkle").is_file()
    assert (framework / "Versions/B/Autoupdate").is_file()
    assert (framework / "Versions/B/Updater.app/Contents/MacOS/Updater").is_file()
    assert (framework / "Versions/Current").is_symlink() and (framework / "Sparkle").is_symlink()
    assert not (framework / "Versions/B/XPCServices").exists()
    # The MIT notice goes with the binary.
    assert (bundle / "Contents/Resources/Licenses/Sparkle.txt").read_text().startswith("Copyright (c) 2006")
    names = [Path(command[0]).name for command in toolchain.commands]
    rpath = toolchain.commands[names.index("install_name_tool")]
    assert rpath[1:3] == ["-add_rpath", "@executable_path/../Frameworks"]
    assert rpath[-1] == str(bundle / "Contents/MacOS/Dictation")
    # The rpath goes in before the signature seals the executable.
    assert names.index("install_name_tool") < names.index("codesign")


def test_a_build_without_sparkle_is_refused(tmp_path, monkeypatch):
    package, toolchain, args = fake_build(tmp_path, monkeypatch, platform="macos")
    monkeypatch.setattr(package.platform, "system", lambda: "Darwin")
    real = toolchain.__call__

    def without_sparkle(command, **options):
        done = real(command, **options)
        if Path(str(command[0])).name == "swift" and "--show-bin-path" not in command:
            import shutil
            shutil.rmtree(toolchain.bin / "Sparkle.framework")
        return done
    monkeypatch.setattr(package.subprocess, "run", without_sparkle)
    with pytest.raises(ValueError, match="Sparkle.framework"):
        package.build(args)


def test_a_build_without_sparkles_license_is_refused(tmp_path, monkeypatch):
    package, _toolchain, args = fake_build(tmp_path, monkeypatch, platform="macos")
    monkeypatch.setattr(package.platform, "system", lambda: "Darwin")
    monkeypatch.setattr(package, "SPARKLE_LICENSE", tmp_path / "missing/LICENSE")
    with pytest.raises(ValueError, match="license"):
        package.build(args)
