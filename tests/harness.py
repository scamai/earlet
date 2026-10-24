"""Shared helpers for the Dictation product tests.

The fake providers below stand in for faster-whisper and MLX so the worker can
be driven the way both hosts drive it, with no model and no network. That is
routing, packaging and protocol evidence, never recognition accuracy.
"""
from __future__ import annotations

import base64
import importlib.util
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


#: Mutated tool sources by name; tests/test_guards_can_fire.py swaps one in to
#: prove that the test claiming a guard fails once the guard is deleted.
OVERRIDES: dict[str, str] = {}

#: The same for worker modules, by file name ("asr_server.py"): written over
#: the staged copy by `staged_runtime`, so a scenario that launches the staged
#: worker runs the mutant.
WORKER_OVERRIDES: dict[str, str] = {}


def tool(name: str, source: str | None = None):
    """Load tools/<name>.py, or ``source`` in its place (a mutated copy)."""
    source = OVERRIDES.get(name) if source is None else source
    path = ROOT / "tools" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    if source is None:
        spec.loader.exec_module(module)
    else:
        exec(compile(source, str(path), "exec"), module.__dict__)
    return module


# ─── Model and runtime fixtures ──────────────────────────────────────────────

def model_dir(path: Path, names) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    for name in names:
        (path / name).write_bytes(b"synthetic-model-for-routing-test")
    return path


#: A complete CTranslate2 Whisper folder, as tools/package.py and
#: DictationWorkerPlan.RequiredModelFiles define it.
CT2_MODEL = ("model.bin", "config.json", "tokenizer.json", "preprocessor_config.json")


def ct2_model(path: Path, names=CT2_MODEL) -> Path:
    """A CTranslate2 folder. Its preprocessor_config.json is Large v3 /
    Turbo's (128 mel bins), real JSON because FAKE_FASTER reads it the way
    faster-whisper does."""
    model = model_dir(path, [name for name in names if name != "preprocessor_config.json"])
    if "preprocessor_config.json" in names:
        (model / "preprocessor_config.json").write_text('{"feature_size": 128}', encoding="utf-8")
    return model


def mlx_model(path: Path, names=("config.json", "weights.safetensors")) -> Path:
    return model_dir(path, names)


def staged_runtime(tmp_path: Path, package=None) -> Path:
    """The worker exactly as an app-only package stages it (with any
    WORKER_OVERRIDES written over the staged files)."""
    resources = tmp_path / "package/resources"
    (package or tool("package")).stage_resources(resources, "windows", None, None, True)
    for name, source in WORKER_OVERRIDES.items():
        staged = resources / "runtime/transcription_worker" / name
        assert staged.is_file(), f"{name} is not a staged worker file"
        staged.write_text(source, encoding="utf-8")
    return resources / "runtime"


# ─── Fake providers ──────────────────────────────────────────────────────────
# Each records what reached it in FAKE_PROVIDER_LOG, one JSON event per line.

_RECORD = '''import json, os
def _record(*event):
    with open(os.environ["FAKE_PROVIDER_LOG"], "a", encoding="utf-8") as stream:
        stream.write(json.dumps(event) + "\\n")
'''

# The decoded text is FAKE_PROVIDER_TEXT when set, so a scenario can hand the
# worker's text passes the words it needs; the options each transcribe call
# received are recorded, which is how a test sees the prompt reach the library.
# With FAKE_PROVIDER_ECHO_PROMPT set, the decoder then continues the prompt it
# was given: everything after the script seed (the prompt's first space), in
# the order the worker wrote it. That is the regurgitation the worker's guard
# exists for, taken from the prompt itself so a test cannot type it in the
# wrong order.
_DECODED = '''
def _decoded(default, prompt):
    text = os.environ.get("FAKE_PROVIDER_TEXT", default)
    if os.environ.get("FAKE_PROVIDER_ECHO_PROMPT") and prompt:
        text += " " + prompt.split(" ", 1)[1]
    return text
'''

# With FAKE_PROVIDER_MEL_BINS set, the model also exposes its mel bins the way
# faster-whisper 1.2.1 does: the CTranslate2 model's own (`model.n_mels`) and
# the feature extractor's, from preprocessor_config.json's feature_size or 80
# when the folder has none (`feature_extractor.mel_filters` rows).
FAKE_FASTER = _RECORD + _DECODED + '''_record("import", "faster_whisper")
class _Info:
    language = "en"
    language_probability = 0.99
class WhisperModel:
    def __init__(self, path, **options):
        _record("load", "faster_whisper", path, options)
        if os.environ.get("FAKE_PROVIDER_MEL_BINS"):
            config = os.path.join(path, "preprocessor_config.json")
            feature_bins = 80
            if os.path.isfile(config):
                with open(config, encoding="utf-8") as stream:
                    feature_bins = json.load(stream).get("feature_size", 80)
            self.model = type("_Whisper", (), {"n_mels": int(os.environ["FAKE_PROVIDER_MEL_BINS"])})()
            filters = type("_Filters", (), {"shape": (feature_bins, 201)})()
            self.feature_extractor = type("_Features", (), {"mel_filters": filters})()
    def transcribe(self, audio, **options):
        _record("transcribe", "faster_whisper", options)
        segment = type("_Segment", (), {"text": _decoded(" hello from faster", options.get("initial_prompt"))})
        return iter([segment()]), _Info()
'''

FAKE_MLX = _RECORD + _DECODED + '''_record("import", "mlx_whisper")
def transcribe(audio, **options):
    _record("transcribe", "mlx_whisper", options.get("path_or_hf_repo"), options.get("initial_prompt"))
    text = _decoded("hello from mlx", options.get("initial_prompt"))
    return {"text": text, "segments": [{"text": text, "start": 0.0, "end": 1.0}], "language": "en"}
'''

FAKE_OPENAI = _RECORD + '''_record("import", "whisper")
def load_model(name):
    _record("load", "whisper", name)
'''

MISSING = 'raise ImportError("provider not installed in this runtime")\n'


def write_providers(folder: Path, **modules: str) -> Path:
    """Write fake provider packages, e.g. faster_whisper=FAKE_FASTER."""
    for name, source in modules.items():
        package = folder / name
        package.mkdir(parents=True, exist_ok=True)
        (package / "__init__.py").write_text(source)
    return folder


# ─── Frames the hosts send ───────────────────────────────────────────────────

def windows_init(model: Path) -> dict:
    """AsrWorkerHost's init: exactly {backend, model_dir, hf_id, use_qnn, allow_model_download}.

    DictationWorkerPlan passes the model directory as both model_dir and hf_id,
    backend "mac", UseQnn false and AllowModelDownload false. tests/test_worker_frames.py pins the key
    set to AsrInitRequest so this copy cannot drift from the C# record.
    """
    return {"op": "init", "backend": "mac", "model_dir": str(model), "hf_id": str(model),
            "use_qnn": False, "allow_model_download": False}


def mac_init(model: Path) -> dict:
    """PythonAsrTransport.start() as DictationApp configures it."""
    return {"op": "init", "backend": "mac", "hf_id": str(model), "model_dir": str(model),
            "allow_model_download": False}


def _tone(seconds: float) -> str:
    samples = int(16000 * seconds)
    pcm = b"".join(struct.pack("<h", int(9000 * math.sin(2 * math.pi * 220 * n / 16000)))
                   for n in range(samples))
    return base64.b64encode(pcm).decode("ascii")


def transcribe_frame(seconds: float = 1.0) -> dict:
    return {"op": "transcribe", "pcm_b64": _tone(seconds),
            "sample_rate": 16000, "channel": "mic", "client": "dictation", "text_passes": True}


def windows_transcribe(seconds: float = 1.0) -> dict:
    """AsrWorkerTranscriber's frame as Dictation.Core configures it: beam 4,
    no language (auto-detect, the default; a picked language adds `language`),
    no vocabulary, profile or learning-loop fields.
    AsrWorkerProtocol serialises with WhenWritingNull, so the null fields are
    absent rather than null; hallucination_guard is a plain bool and always
    present. tests/test_worker_frames.py pins these against the C# sources."""
    return {"op": "transcribe", "pcm_b64": _tone(seconds), "sample_rate": 16000, "channel": "mic",
            "client": "dictation", "text_passes": True, "hallucination_guard": False, "beam_size": 4}


def mac_transcribe(seconds: float = 1.0) -> dict:
    """DictationAsrTransport's frame: no client or text_passes (the worker's
    defaults, dictation and true, apply) and no bias_phrases while its flag is
    off, which DictationApp never turns on."""
    return {"op": "transcribe", "pcm_b64": _tone(seconds), "sample_rate": 16000, "channel": "dictation",
            "utc_start": "2026-09-28T00:00:00Z", "voiced_fraction": 1.0, "hallucination_guard": False}


def user_data(tmp_path: Path) -> Path:
    """The folder a `run_worker` launch reads the user's bias_aliases.json and
    learning.json from: LEARNING.md §1's `<data>/dictation`, under the HOME
    and LOCALAPPDATA run_worker points at `tmp_path / "user-home"`."""
    home = tmp_path / "user-home"
    folder = (home / "Dictation" / "dictation" if sys.platform == "win32"
              else home / "Library" / "Application Support" / "Dictation" / "dictation")
    folder.mkdir(parents=True, exist_ok=True)
    return folder


def _frame(message: dict) -> bytes:
    payload = json.dumps(message, separators=(",", ":")).encode("utf-8")
    return struct.pack(">I", len(payload)) + payload


def _read_frames(raw: bytes) -> list[dict]:
    replies, offset = [], 0
    while offset + 4 <= len(raw):
        (size,) = struct.unpack_from(">I", raw, offset)
        replies.append(json.loads(raw[offset + 4:offset + 4 + size].decode("utf-8")))
        offset += 4 + size
    return replies


def worker_env(runtime: Path, engine: str, providers: Path, tmp_path: Path,
               env: dict[str, str] | None = None) -> dict[str, str]:
    """The environment both hosts launch the worker with: PYTHONPATH set to
    the runtime, the engine pinned and the Hub offline. ``env`` adds
    variables (FAKE_PROVIDER_TEXT)."""
    log = tmp_path / "provider-events.jsonl"
    log.unlink(missing_ok=True)
    home = tmp_path / "user-home"
    home.mkdir(exist_ok=True)
    environment = {key: value for key, value in os.environ.items() if not key.startswith("PYTHON")}
    environment.update(
        PYTHONPATH=os.pathsep.join([str(runtime), str(providers)]), PYTHONNOUSERSITE="1",
        PYTHONDONTWRITEBYTECODE="1", DICTATION_ASR_ENGINE=engine, HF_HUB_OFFLINE="1",
        TRANSFORMERS_OFFLINE="1", FAKE_PROVIDER_LOG=str(log), HOME=str(home),
        LOCALAPPDATA=str(home), OPENBLAS_NUM_THREADS="1", OMP_NUM_THREADS="1", MKL_NUM_THREADS="1")
    environment.update(env or {})
    return environment


def run_worker(runtime: Path, frames: list[dict], engine: str, providers: Path, tmp_path: Path,
               python: str = sys.executable, env: dict[str, str] | None = None
               ) -> tuple[list[dict], list[list], str]:
    """Launch the worker as both hosts do: ``-m`` from the runtime directory,
    PYTHONPATH set to it, the engine pinned and the Hub offline. ``env`` adds
    variables (FAKE_PROVIDER_TEXT). Returns the replies, the provider events
    and stderr."""
    environment = worker_env(runtime, engine, providers, tmp_path, env)
    completed = subprocess.run([python, "-m", "transcription_worker.asr_server"], cwd=runtime,
                               env=environment, input=b"".join(_frame(f) for f in frames),
                               capture_output=True, timeout=180)
    log = tmp_path / "provider-events.jsonl"
    events = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
    return _read_frames(completed.stdout), events, completed.stderr.decode("utf-8", "replace")
