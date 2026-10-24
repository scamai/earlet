#!/usr/bin/env python3
"""Repeatable installed-Mac smoke and a read-only, word-filtered learning watch.

Automatic PASS covers synthetic audio and source learning policy, not native
microphone/Accessibility capture, human accuracy, or optimal search parameters.
Only the Python standard library is needed to launch this tool.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import signal
import stat
import struct
import subprocess
import sys
import tempfile
import time
import wave

ROOT = Path(__file__).resolve().parents[1]
MAX_BYTES = 8 * 1024 * 1024
SCENES = {"terminal", "wechat", "other"}
MARKER = "dictation.acceptance/1"


# ─── Bounded I/O and process lifecycle ────────────────────────────────────────

class Incomplete(RuntimeError):
    """A required lane could not be measured; never counted as PASS."""


def run_process(argv, *, cwd=None, env=None, data=None, timeout=180):
    process = subprocess.Popen(
        [str(arg) for arg in argv], cwd=cwd, env=env, stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=os.name != "nt",
    )
    try:
        stdout, stderr = process.communicate(data, timeout=timeout)
    except subprocess.TimeoutExpired:
        if os.name != "nt":
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        else:
            process.kill()
        process.communicate()  # Drain and reap, including inherited child pipes.
        raise Incomplete(f"{Path(str(argv[0])).name} timed out after {timeout}s") from None
    return subprocess.CompletedProcess(argv, process.returncode, stdout, stderr)


def read_bounded(path: Path) -> bytes:
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0))
    with os.fdopen(descriptor, "rb") as stream:
        before = os.fstat(stream.fileno())
        if not stat.S_ISREG(before.st_mode) or before.st_size > MAX_BYTES:
            raise ValueError("expected a regular file no larger than 8 MiB")
        data = stream.read(MAX_BYTES + 1)
        after = os.fstat(stream.fileno())
    if len(data) > MAX_BYTES or (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise ValueError("file changed while reading or exceeded 8 MiB")
    return data


def fingerprint(path: Path):
    try:
        return hashlib.sha256(read_bounded(path)).hexdigest()
    except FileNotFoundError:
        return None


def file_hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def frame(payload: dict) -> bytes:
    data = json.dumps(payload).encode()
    if not data or len(data) > MAX_BYTES:
        raise ValueError("invalid frame size")
    return struct.pack(">I", len(data)) + data


def parse_frames(data: bytes) -> list[dict]:
    replies = []
    while data:
        if len(data) < 4:
            raise ValueError("truncated frame header")
        size = struct.unpack(">I", data[:4])[0]
        if not 0 < size <= MAX_BYTES or len(data) < size + 4:
            raise ValueError("invalid or truncated frame body")
        value = json.loads(data[4:4 + size])
        if not isinstance(value, dict):
            raise ValueError("reply must be an object")
        replies.append(value)
        data = data[4 + size:]
    return replies


def pcm_base64(path: Path) -> str:
    import base64
    with wave.open(str(path)) as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getcomptype()) != (1, 2, 16000, "NONE"):
            raise ValueError("fixture must be mono 16 kHz PCM16 WAV")
        return base64.b64encode(audio.readframes(audio.getnframes())).decode()


def owner_store() -> Path:
    if os.name == "nt":
        base = os.environ.get("LOCALAPPDATA")
        if not base:
            raise Incomplete("LOCALAPPDATA is unavailable")
        return Path(base) / "Dictation/dictation/learning.json"
    return Path.home() / "Library/Application Support/Dictation/dictation/learning.json"


def new_run() -> Path:
    parent = ROOT / "evidence/acceptance"
    parent.mkdir(parents=True, exist_ok=True)
    folder = Path(tempfile.mkdtemp(prefix=f"dictation-acceptance-{time.strftime('%Y%m%d-%H%M%S')}-", dir=parent)).resolve()
    folder.chmod(0o700)
    (folder / ".acceptance-scratch").write_text(MARKER, encoding="utf-8")
    return folder


def isolated_env() -> dict:
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(("PYTHON", "DICTATION_"))}
    env.update(DICTATION_ASR_ENGINE="mlx", PYTHONNOUSERSITE="1",
               HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", OMP_NUM_THREADS="1")
    return env


# ─── Installed runtime acceptance ────────────────────────────────────────────

def preflight(app: Path) -> dict:
    if sys.platform != "darwin":
        raise Incomplete("auto requires macOS Apple Silicon; Windows device acceptance is separate")
    resources = app / "Contents/Resources"
    required = [app / "Contents/Info.plist", app / "Contents/MacOS/Dictation",
                resources / "python/bin/python3", resources / "package-mode.json",
                resources / "runtime/transcription_worker/asr_server.py", resources / "model/config.json"]
    if not all(path.is_file() for path in required):
        raise Incomplete("installed complete app, Python, worker or model files are missing")
    if not any((resources / "model" / name).is_file() for name in ("weights.safetensors", "model.safetensors")):
        raise Incomplete("installed MLX model weights are missing")
    with required[0].open("rb") as stream:
        info = plistlib.load(stream)
    mode = json.loads(read_bounded(resources / "package-mode.json"))
    if info.get("CFBundleIdentifier") != "ai.scam.dictation" or mode.get("provider") != "mlx":
        raise Incomplete("expected the independent Dictation bundle with the MLX provider")
    signed = run_process(["/usr/bin/codesign", "--verify", "--deep", "--strict", app], timeout=60)
    if signed.returncode:
        raise AssertionError("installed app signature verification failed")
    return {"build": info.get("DictationBuild", "unknown"), "version": info.get("CFBundleShortVersionString"),
            "package_mode": mode.get("mode"), "signature": "PASS", "app": str(app),
            "executable_sha256": file_hash(required[1]),
            "worker_sha256": {path.name: file_hash(path) for path in sorted((resources / "runtime/transcription_worker").glob("*.py"))}}


def auto(args) -> int:
    folder = new_run()
    result = {"schema": MARKER, "automatic_status": "INCOMPLETE", "human_status": "NOT_RUN",
              "synthetic_audio": True, "learning_host": "source Swift probe, not installed native UI",
              "limits": ["microphone, hotkey, insertion and edit observation need the manual script",
                         "synthetic samples do not establish human accuracy or strongest algorithm"],
              "checks": [], "evidence": str(folder)}
    before = None
    owner_read = False
    started = time.monotonic()

    def execute(label, argv, *, data=None, timeout=180):
        output = run_process(argv, cwd=ROOT, env=isolated_env(), data=data, timeout=timeout)
        (folder / f"{label}.stdout").write_bytes(output.stdout)
        (folder / f"{label}.stderr").write_bytes(output.stderr)
        if output.returncode:
            raise AssertionError(f"{label} exited {output.returncode}; inspect {label}.stderr in the evidence folder")
        print(f"Ran {label} (exit 0)", flush=True)
        return output

    try:
        before = fingerprint(owner_store())
        owner_read = True
        app = args.app.expanduser().resolve()
        result["installed"] = preflight(app)
        result["checks"].append("installed-bundle")
        print(f"PASS installed-bundle · build {result['installed']['build']}", flush=True)
        resources = app / "Contents/Resources"
        compiler = run_process(["/usr/bin/xcrun", "--find", "swiftc"], timeout=30)
        if compiler.returncode:
            raise Incomplete("Swift compiler unavailable; install/configure Xcode command line tools")
        voices = run_process(["/usr/bin/say", "-v", "?"], timeout=30)
        if voices.returncode or not any(line.startswith(args.voice + " ") for line in voices.stdout.decode(errors="replace").splitlines()):
            raise Incomplete("requested macOS voice is unavailable; select an installed voice with --voice")
        audio = folder / "miles.wav"
        execute("synthetic-audio", ["/usr/bin/say", "-v", args.voice, "-o", audio,
                                   "--file-format=WAVE", "--data-format=LEI16@16000",
                                   "We have a server called Miles."], timeout=60)
        pcm = pcm_base64(audio)
        result["checks"].append("synthetic-audio")
        result["audio"] = {"voice": args.voice, "sha256": file_hash(audio), "reference": "We have a server called Miles."}
        sources = sorted((ROOT / "macos/Sources/DictationLearning").glob("*.swift"))
        result["learning_source_sha256"] = {path.name: file_hash(path) for path in sources}
        probe = folder / "learning-probe"
        execute("swift-probe-build", ["/usr/bin/xcrun", "swiftc", "-O", "-parse-as-library", *sources,
                                      ROOT / "tools/AcceptanceLearningProbe.swift", "-o", probe])
        result["checks"].append("swift-probe-build")
        command = [resources / "python/bin/python3", "-E", "-s", "-B", "-u",
                   ROOT / "tools/acceptance_runtime.py", "--resources", resources,
                   "--scratch", folder, "--audio", audio, "--probe", probe]
        execute("learning-pipeline", command, timeout=300)
        result["pipeline"] = json.loads(read_bounded(folder / "pipeline.json"))
        result["checks"].append("learning-pipeline")
        print("PASS learning-pipeline", flush=True)
        request = {"op": "transcribe", "client": "dictation", "language": "en", "vocabulary_scene": "terminal",
                   "pcm_b64": pcm, "text_passes": True}
        wire = b"".join(frame(row) for row in [
            {"op": "init", "backend": "mac", "model_dir": str(resources / "model"), "allow_model_download": False},
            request, {**request, "language": None, "vocabulary_scene": "wechat"},
            {**request, "sample_rate": 8000}, {"op": "shutdown"}])
        output = execute("worker-ipc", [*command, "--wire"], data=wire, timeout=180)
        replies = parse_frames(output.stdout)
        if len(replies) != 5 or not all(replies[i].get("ok") is True for i in (0, 1, 2, 4)):
            raise AssertionError("worker did not complete init, two transcribes and shutdown")
        if replies[3].get("ok") is not False:
            raise AssertionError("worker accepted invalid 8 kHz input")
        for reply in replies[1:3]:
            search = reply.get("stats", {}).get("vocabulary_search", {})
            if not reply.get("text") or not search.get("beam_windows", 0) or search.get("fallback"):
                raise AssertionError("framed transcribe did not execute the vocabulary beam")
        result["ipc"] = {"responses": len(replies), "invalid_rate_rejected": True,
                         "search": [reply["stats"]["vocabulary_search"] for reply in replies[1:3]]}
        result["checks"].append("worker-ipc")
        print("PASS worker-ipc", flush=True)
        result["automatic_status"] = "PASS"
    except Incomplete as error:
        result["reason"] = str(error)
    except (FileNotFoundError, PermissionError) as error:
        result["reason"] = f"required tool/file unavailable: {error}"
    except (AssertionError, ValueError, OSError, KeyError) as error:
        result.update(automatic_status="FAIL", reason=str(error))
    finally:
        if owner_read:
            try:
                unchanged = before == fingerprint(owner_store())
                result["owner_store_unchanged"] = unchanged
                if not unchanged:
                    if result["automatic_status"] == "PASS":
                        result["automatic_status"] = "INCOMPLETE"
                    result["owner_store_note"] = "owner store changed during the run; may be a concurrent user correction; never reverted"
            except (OSError, ValueError) as error:
                result["owner_store_unchanged"] = None
                if result["automatic_status"] == "PASS":
                    result["automatic_status"] = "INCOMPLETE"
                result["owner_store_note"] = f"could not compare owner store: {error}"
        result["elapsed_seconds"] = round(time.monotonic() - started, 2)
        (folder / "result.json").write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Automatic acceptance {result['automatic_status']}; human acceptance NOT_RUN. Report: {folder / 'result.json'}")
    if result.get("reason"):
        print(result["reason"])
    return {"PASS": 0, "FAIL": 1, "INCOMPLETE": 2}[result["automatic_status"]]


# ─── Read-only observation, only explicitly requested words ───────────────────

def number(value) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def word_snapshot(path: Path, words: list[str]) -> dict:
    try:
        data = json.loads(read_bounded(path))
    except FileNotFoundError:
        return {"store": "missing", "words": {}}
    if not isinstance(data, dict) or data.get("schema") != "dictation.learning/1":
        raise ValueError("unsupported learning schema")
    entries = {}
    for name in ("words", "aliases", "pending"):
        entries[name] = data.get(name, [])
        if not isinstance(entries[name], list) or any(not isinstance(row, dict) for row in entries[name]):
            raise ValueError("malformed learning entries")
    journal = data.get("evidence", {})
    if not isinstance(journal, dict) or not isinstance(journal.get("observations", []), list):
        raise ValueError("malformed learning journal")
    observations = journal.get("observations", [])
    result = {}
    for word in words:
        key = word.lower()
        match = lambda value: isinstance(value, str) and value.lower() == key
        learned = next((row for row in entries["words"] if match(row.get("text"))), {})
        votes = []
        for row in observations:
            if not isinstance(row, dict) or not isinstance(row.get("corrections"), list) or not isinstance(row.get("id"), str):
                raise ValueError("malformed observation")
            if any(isinstance(fix, dict) and (match(fix.get("meant")) or match(fix.get("heard"))) for fix in row["corrections"]):
                votes.append({"id_hash": hashlib.sha256(row["id"].encode()).hexdigest()[:16],
                              "scene": row.get("scene") if isinstance(row.get("scene"), str) and row["scene"] in SCENES else "unknown"})
        result[word] = {"learned": bool(learned), "support": number(learned.get("count")),
                        "opposition": number(learned.get("opposition", 0)) if learned else None,
                        "confirmed_aliases": sum(match(row.get("meant")) for row in entries["aliases"]),
                        "pending_aliases": sum(match(row.get("meant")) for row in entries["pending"]),
                        "observations": sorted(votes, key=lambda row: row["id_hash"])}
    return {"store": "readable", "words": result}


def watch(args) -> int:
    if any(not word.strip() or len(word) > 128 or not word.isprintable() for word in args.word):
        raise ValueError("--word must be 1–128 printable characters")
    folder = new_run()
    path = owner_store()
    print("Read-only watch: edit in the original field within 90 seconds and do not send. "
          "Only the requested words and counts are printed.", flush=True)
    print(f"Watch log: {folder / 'watch.jsonl'}", flush=True)
    deadline = time.monotonic() + args.seconds
    previous = None
    changes = errors = 0
    try:
        with (folder / "watch.jsonl").open("w", encoding="utf-8") as output:
            while time.monotonic() < deadline:
                try:
                    current = word_snapshot(path, args.word)
                except (OSError, ValueError) as error:
                    # Do not include malformed file content or unrelated vocabulary.
                    current = {"store": "unreadable", "error_type": type(error).__name__}
                if current != previous:
                    event = {"at": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "snapshot": current,
                             "event": "baseline" if previous is None else "change"}
                    if current["store"] == "unreadable":
                        errors += 1
                    elif previous is not None and current["store"] == "readable":
                        if previous.get("store") == "readable" or any(
                            row["learned"] or row["pending_aliases"] or row["observations"]
                            for row in current["words"].values()
                        ):
                            changes += 1
                    line = json.dumps(event, ensure_ascii=False)
                    print(line, flush=True)
                    output.write(line + "\n")
                    output.flush()
                    previous = current
                time.sleep(min(.5, max(0, deadline - time.monotonic())))
    except KeyboardInterrupt:
        pass
    summary = {"target_changes_observed": changes, "read_errors": errors, "human_status": "NOT_EVALUATED",
               "note": "a saved learning change does not prove the next recognition chose the word"}
    (folder / "watch-result.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"Watch ended: {changes} target record changes, {errors} read errors. "
          "Check the next recognition result by hand, as the guide describes.")
    return 0 if changes and not errors else 2


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    automatic = commands.add_parser("auto", help="installed Mac MLX + isolated learning + framed IPC")
    automatic.add_argument("--app", type=Path, default=Path("/Applications/Earlet.app"))
    automatic.add_argument("--voice", default="Samantha", help="an already installed English macOS voice")
    watching = commands.add_parser("watch", help="read-only, allowlisted observation of the live learning store")
    watching.add_argument("--word", action="append", required=True)
    watching.add_argument("--seconds", type=float, default=600)
    args = parser.parse_args(argv)
    if args.command == "watch" and not 0 < args.seconds <= 3600:
        parser.error("--seconds must be greater than 0 and at most 3600")
    try:
        return auto(args) if args.command == "auto" else watch(args)
    except (Incomplete, OSError, ValueError) as error:
        print(f"INCOMPLETE: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
