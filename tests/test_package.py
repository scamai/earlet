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
    assert facts == {"python_path_files": ["bin/python3._pth"], "import_smoke": "passed"}
    bytecode = list((resources / "runtime/transcription_worker/__pycache__").glob("*.pyc"))
    assert len(bytecode) == len(tool("package").WORKER_MODULES) == 19
    assert any(path.name.startswith("learned_vocabulary.") for path in bytecode)
    assert any(path.name.startswith("context_vocabulary.") for path in bytecode)
    assert not [path for path in bytecode if str(tmp_path).encode() in path.read_bytes()]


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
    """Stands in for dotnet, swift, strip and codesign and records each call.
    Anything else (git) runs for real."""

    def __init__(self, tmp_path, assembly=b"clean-assembly"):
        self.commands, self.assembly, self.bin = [], assembly, tmp_path / "swift-bin"
        self.real = subprocess.run

    def __call__(self, command, **options):
        command = [str(part) for part in command]
        name = Path(command[0]).name
        if name not in ("fake-dotnet", "swift", "strip", "codesign"):
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
        return subprocess.CompletedProcess(command, 0, stdout="")


def fake_build(tmp_path, monkeypatch, assembly=b"clean-assembly", **overrides):
    package = tool("package")
    toolchain = FakeToolchain(tmp_path, assembly)
    monkeypatch.setattr(package.subprocess, "run", toolchain)
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
