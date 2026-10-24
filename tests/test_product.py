"""Observe the extraction boundary in packaged files and real provider selection.

Synthetic provider objects test offline routing, not recognition accuracy.
Native microphone, focus and insertion acceptance remain device gates.
"""
import json
import shutil
import sys
import types

import pytest

from harness import ROOT, ct2_model, tool

sys.path.insert(0, str(ROOT / "runtime"))


@pytest.mark.parametrize("target", ["windows", "macos"])
def test_packaged_files_survive_removal_of_asset_sources(tmp_path, target):
    python = tmp_path / "input-python"
    executable = python / ("python.exe" if target == "windows" else "bin/python3")
    executable.parent.mkdir(parents=True)
    executable.write_bytes(b"synthetic-runtime-for-layout-test")
    model = tmp_path / "input-model"
    model.mkdir()
    names = (("config.json", "model.bin", "tokenizer.json", "preprocessor_config.json") if target == "windows"
             else ("config.json", "weights.safetensors"))
    for name in names:
        (model / name).write_bytes(b"synthetic-model-for-layout-test")
    destination = tmp_path / "package/resources"
    # The interpreter is synthetic bytes: this test is about the layout, so the
    # import smoke (which the host of the same platform would otherwise run on
    # it, and fail with "Permission denied") is not asked for.
    tool("package").stage_resources(destination, target, python, model, False, run_python=False)
    shutil.rmtree(python)
    shutil.rmtree(model)
    assert (destination / "python" / executable.relative_to(python)).is_file()
    assert all((destination / "model" / name).is_file() for name in names)
    assert (destination / "runtime/transcription_worker/asr_server.py").is_file()
    assert not list(destination.rglob("test_*.py"))
    mode = json.loads((destination / "package-mode.json").read_text())
    assert mode["mode"] == "complete-preview"
    # Both keep a failed take; only the Mac keeps recordings in its history.
    assert "one failed take for 7 days" in mode["audio_retention"]
    assert ("encrypted history" in mode["audio_retention"]) == (target == "macos")


def test_packaging_refuses_a_virtualenv_before_copying(tmp_path):
    python = tmp_path / "venv"
    python.mkdir()
    (python / "python.exe").touch()
    (python / "pyvenv.cfg").write_text("home=/builder/python")
    with pytest.raises(ValueError, match="not a virtualenv"):
        tool("package").stage_resources(tmp_path / "out", "windows", python, tmp_path, False)
    assert not (tmp_path / "out").exists()


def test_complete_package_cannot_silently_omit_models(tmp_path):
    with pytest.raises(ValueError, match="require"):
        tool("package").stage_resources(tmp_path / "out", "windows", None, None, False)
    tool("package").stage_resources(tmp_path / "preview", "windows", None, None, True)
    assert json.loads((tmp_path / "preview/package-mode.json").read_text())["mode"] == "app-only"


# The boundary check's own cases, including references outside this product, live in
# tests/test_boundary.py.


def test_local_windows_model_reaches_provider_without_download_permission(tmp_path, monkeypatch):
    from transcription_worker import whisper_mac
    observed = []
    provider = types.ModuleType("faster_whisper")
    provider.WhisperModel = lambda path, **options: observed.append((path, options))
    monkeypatch.setitem(sys.modules, "faster_whisper", provider)
    monkeypatch.setenv("DICTATION_ASR_ENGINE", "faster")
    model = ct2_model(tmp_path)
    engine = whisper_mac.WhisperMac(model_dir=str(model), allow_model_download=False)
    assert engine._engine_name == "faster"
    assert observed == [(str(model), {"device": "cpu", "compute_type": "int8"})]


def test_missing_pinned_provider_cannot_fall_back(tmp_path, monkeypatch):
    from transcription_worker import whisper_mac
    monkeypatch.setenv("DICTATION_ASR_ENGINE", "faster")
    monkeypatch.setitem(sys.modules, "faster_whisper", None)
    fallback = types.ModuleType("whisper")
    fallback.load_model = lambda _: pytest.fail("A missing pinned engine fell back")
    monkeypatch.setitem(sys.modules, "whisper", fallback)
    with pytest.raises(ImportError):
        whisper_mac.WhisperMac(model_dir=str(ct2_model(tmp_path)), allow_model_download=False)


@pytest.mark.parametrize("system", ["win32", "darwin"])
def test_worker_personalization_paths_stay_in_the_product_directory(monkeypatch, tmp_path, system):
    from transcription_worker import spoken_aliases
    monkeypatch.setattr(sys, "platform", system)
    monkeypatch.setenv("HOME", str(tmp_path))
    if system == "win32":
        monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    else:
        # A Mac has no LOCALAPPDATA; setting it here hid the macOS branch.
        monkeypatch.delenv("LOCALAPPDATA", raising=False)
    base = tmp_path if system == "win32" else tmp_path / "Library/Application Support"
    expected = base / "Dictation" / "dictation"
    assert spoken_aliases.default_path() == expected / "bias_aliases.json"
    assert spoken_aliases.learning_path() == expected / "learning.json"
