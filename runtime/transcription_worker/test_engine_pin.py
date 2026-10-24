"""DICTATION_ASR_ENGINE must actually pin the engine.

Without this the trie path is untestable on the hardware it ships on: mlx
imports successfully on every Apple-silicon machine, so the resolution order
would always pick it, and `BiasTrie` needs a beam search mlx does not have.
A pin that silently does nothing would look exactly like a working one.
"""
import os
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

#: Stand-ins for both providers, installed before whisper_mac is imported: mlx
#: becomes importable on every machine, and openai-whisper's load_model records
#: the size it was asked for instead of reading ~/.cache/whisper or downloading
#: 145 MB. Engine SELECTION is what these tests are about, not weights.
_STUB_PROVIDERS = (
    "import types\n"
    "sys.modules['mlx_whisper'] = types.ModuleType('mlx_whisper')\n"
    "_openai = types.ModuleType('whisper')\n"
    "_openai.load_model = lambda size: print('LOADED:' + size) or object()\n"
    "sys.modules['whisper'] = _openai\n"
)


def _run(engine: str | None, stub_providers: bool = False) -> list[str]:
    """whisper_mac's engine choice, in a clean subprocess, as output lines."""
    env = dict(os.environ)
    env.pop("DICTATION_ASR_ENGINE", None)
    if engine is not None:
        env["DICTATION_ASR_ENGINE"] = engine
    code = (
        "import sys; sys.path.insert(0, %r);\n"
        + (_STUB_PROVIDERS if stub_providers else "")
        + "import whisper_mac\n"
        "try:\n"
        "    e = whisper_mac.WhisperMac(hf_id='openai/whisper-base')\n"
        "    print('ENGINE:' + str(e._engine_name))\n"
        "except Exception as exc:\n"
        "    print('ERROR:' + type(exc).__name__ + ':' + str(exc))\n"
    ) % str(HERE)
    out = subprocess.run([sys.executable, "-c", code], env=env,
                         capture_output=True, text=True, timeout=600)
    lines = [line for line in out.stdout.splitlines()
             if line.startswith(("ENGINE:", "ERROR:", "LOADED:"))]
    return lines or ["ERROR:no-output:" + out.stderr[-300:]]


def _resolve(engine: str | None, stub_providers: bool = False) -> str:
    """Report which engine whisper_mac would choose, in a clean subprocess."""
    return next(line for line in _run(engine, stub_providers)
                if not line.startswith("LOADED:"))


def test_pin_rejects_an_unknown_engine():
    assert _resolve("gpt5").startswith("ERROR:ValueError")


def test_pin_selects_openai_even_when_mlx_is_importable():
    """The case the trie depends on.

    Hermetic: both providers are stubbed, so this runs the same on a machine
    with no cached weights and no network as on the Mac, and mlx really is
    importable here -- which the unpinned control proves, or the pin would be
    winning against nothing.
    """
    assert _resolve(None, stub_providers=True) == "ENGINE:mlx"

    lines = _run("openai", stub_providers=True)
    assert lines == ["LOADED:base", "ENGINE:openai"], lines


def test_dictation_mlx_checkpoint_maps_to_the_openai_model_name():
    """The shipped dictation id must not degrade to the bogus name `4bit`."""
    from transcription_worker import whisper_mac

    assert whisper_mac._classic_model_name(
        "mlx-community/whisper-large-v3-turbo-4bit"
    ) == "large-v3-turbo"


def test_downloaded_quantized_snapshot_is_recognized_as_local_mlx(
        tmp_path, monkeypatch):
    """The signed 4-bit pack publishes model.safetensors, not weights.*."""
    from transcription_worker import whisper_mac

    model = tmp_path / "model"
    model.mkdir()
    monkeypatch.setattr(whisper_mac, "_MLX_SHIM_ROOT", tmp_path / "shims")
    (model / "config.json").write_text("{}", encoding="utf-8")
    (model / "model.safetensors").write_bytes(b"weights")

    assert whisper_mac._is_mlx_whisper_dir(str(model))
    resolved = whisper_mac._resolve_mlx_repo(str(model), allow_download=False)
    assert Path(resolved, "weights.safetensors").resolve() \
        == (model / "model.safetensors").resolve()


def test_unset_behaves_exactly_as_before():
    """On a machine with mlx installed the default must still be mlx."""
    try:
        import mlx_whisper  # noqa: F401
    except ImportError:
        return  # nothing to assert off Apple silicon
    assert _resolve(None) == "ENGINE:mlx"
