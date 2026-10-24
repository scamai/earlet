"""Acceptance tool boundaries; real installed MLX is tested by `acceptance.py auto`."""
from __future__ import annotations

import json
import os
from pathlib import Path
import re
import struct
import sys
import time
from types import SimpleNamespace

import pytest

import harness


@pytest.fixture
def tool():
    return harness.tool("acceptance")


def write_store(path, *, identity="private-draft-id", scene="terminal", count=1):
    state = {"schema": "dictation.learning/1",
             "words": [{"text": "Miles", "count": count, "opposition": 0},
                       {"text": "DoNotDisclose", "count": 9}],
             "aliases": [{"heard": "private misspelling", "meant": "Miles"}], "pending": [],
             "evidence": {"version": 1, "observations": [
                 {"id": identity, "scene": scene,
                  "corrections": [{"heard": "private source", "meant": "Miles"}]}]}}
    path.write_text(json.dumps(state))
    return state


@pytest.mark.parametrize("data", [b"\x00", struct.pack(">I", 0), struct.pack(">I", 9_000_000),
                                   struct.pack(">I", 4) + b"{}", struct.pack(">I", 2) + b"[]",
                                   struct.pack(">I", 2) + b"xx"])
def test_frames_fail_closed(tool, data):
    with pytest.raises(ValueError):
        tool.parse_frames(data)


def test_multiple_frames_and_trailing_garbage(tool):
    data = tool.frame({"ok": True}) + tool.frame({"ok": False, "error": "bad rate"})
    assert tool.parse_frames(data) == [{"ok": True}, {"ok": False, "error": "bad rate"}]
    with pytest.raises(ValueError):
        tool.parse_frames(data + b"x")


@pytest.mark.skipif(os.name == "nt", reason="POSIX process groups; auto is macOS-only")
def test_timeout_kills_child_that_inherits_output_pipe(tool, tmp_path):
    marker = tmp_path / "orphan-was-alive"
    # If only the parent is killed, communicate waits for the child's pipe,
    # then the orphan writes this marker. Both symptoms must be absent.
    child = f"import time,pathlib;time.sleep(3);pathlib.Path({str(marker)!r}).touch()"
    parent = f"import subprocess,sys,time;subprocess.Popen([sys.executable,'-c',{child!r}]);time.sleep(30)"
    started = time.monotonic()
    with pytest.raises(tool.Incomplete, match="timed out"):
        tool.run_process([sys.executable, "-c", parent], timeout=.5)
    assert time.monotonic() - started < 2.5
    assert not marker.exists()


def test_snapshot_redacts_unwatched_words_counterparts_and_ids(tool, tmp_path):
    path = tmp_path / "learning.json"
    write_store(path)
    result = tool.word_snapshot(path, ["Miles"])
    serialized = json.dumps(result)
    assert all(private not in serialized for private in ("DoNotDisclose", "private source", "private misspelling", "private-draft-id"))
    assert result["words"]["Miles"]["learned"] is True
    assert result["words"]["Miles"]["support"] == 1
    assert result["words"]["Miles"]["confirmed_aliases"] == 1
    assert result["words"]["Miles"]["observations"][0]["scene"] == "terminal"


def test_snapshot_distinguishes_revision_from_independent_dictation(tool, tmp_path):
    path = tmp_path / "learning.json"
    state = write_store(path)
    first = tool.word_snapshot(path, ["Miles"])
    state["words"][0]["count"] = 2
    path.write_text(json.dumps(state))
    revision = tool.word_snapshot(path, ["Miles"])
    assert revision != first
    assert revision["words"]["Miles"]["observations"] == first["words"]["Miles"]["observations"]
    state["evidence"]["observations"].append({"id": "second", "scene": "wechat",
                                            "corrections": [{"heard": "mies", "meant": "Miles"}]})
    path.write_text(json.dumps(state))
    independent = tool.word_snapshot(path, ["Miles"])["words"]["Miles"]["observations"]
    assert len(independent) == 2 and len({vote["id_hash"] for vote in independent}) == 2
    assert {vote["scene"] for vote in independent} == {"terminal", "wechat"}


def test_first_run_malformed_and_atomic_replacement(tool, tmp_path):
    path = tmp_path / "learning.json"
    assert tool.word_snapshot(path, ["Miles"])["store"] == "missing"
    path.write_text("broken private draft")
    with pytest.raises(ValueError):
        tool.word_snapshot(path, ["Miles"])
    next_path = tmp_path / "next"
    write_store(next_path)
    next_path.replace(path)
    assert tool.word_snapshot(path, ["Miles"])["store"] == "readable"


@pytest.mark.parametrize("change", [lambda data: data.update(words=None),
                                   lambda data: data.update(evidence=[]),
                                   lambda data: data["evidence"].update(observations=[None]),
                                   lambda data: data.update(schema="future")])
def test_bad_schema_is_not_a_successful_empty_snapshot(tool, tmp_path, change):
    path = tmp_path / "learning.json"
    state = write_store(path)
    change(state)
    path.write_text(json.dumps(state))
    with pytest.raises(ValueError):
        tool.word_snapshot(path, ["Miles"])


@pytest.mark.skipif(os.name == "nt", reason="POSIX symlinks and FIFOs")
def test_store_symlinks_and_fifos_fail_without_reading(tool, tmp_path):
    actual = tmp_path / "actual"
    write_store(actual)
    link = tmp_path / "link"
    link.symlink_to(actual)
    with pytest.raises(OSError):
        tool.read_bounded(link)
    fifo = tmp_path / "fifo"
    os.mkfifo(fifo)
    with pytest.raises(ValueError, match="regular"):
        tool.read_bounded(fifo)


def test_environment_does_not_inherit_provider_or_python_overrides(tool, monkeypatch):
    monkeypatch.setenv("PYTHONPATH", "/wrong/python")
    monkeypatch.setenv("DICTATION_MODEL_DIR", "/other/product")
    monkeypatch.setenv("DICTATION_ASR_ENGINE", "stub")
    env = tool.isolated_env()
    assert "PYTHONPATH" not in env and "DICTATION_MODEL_DIR" not in env
    assert env["DICTATION_ASR_ENGINE"] == "mlx" and env["HF_HUB_OFFLINE"] == "1"


def test_missing_app_is_incomplete_and_preserves_owner_store(tool, tmp_path, monkeypatch):
    monkeypatch.setattr(tool, "ROOT", tmp_path)
    owner = tmp_path / "owner.json"
    write_store(owner)
    before = owner.read_bytes()
    monkeypatch.setattr(tool, "owner_store", lambda: owner)
    code = tool.auto(SimpleNamespace(app=tmp_path / "missing.app", voice="Samantha"))
    result_path, = (tmp_path / "evidence/acceptance").glob("*/result.json")
    result = json.loads(result_path.read_text())
    assert code == 2 and result["automatic_status"] == "INCOMPLETE"
    assert result["human_status"] == "NOT_RUN" and result["owner_store_unchanged"] is True
    assert owner.read_bytes() == before


def test_concurrent_owner_change_is_reported_never_reverted(tool, tmp_path, monkeypatch):
    monkeypatch.setattr(tool, "ROOT", tmp_path)
    owner = tmp_path / "owner.json"
    owner.write_text("before")
    monkeypatch.setattr(tool, "owner_store", lambda: owner)
    def unavailable(app):
        owner.write_text("concurrent user correction")
        raise tool.Incomplete("test dependency unavailable")
    monkeypatch.setattr(tool, "preflight", unavailable)
    assert tool.auto(SimpleNamespace(app=tmp_path, voice="unused")) == 2
    result_path, = (tmp_path / "evidence/acceptance").glob("*/result.json")
    assert json.loads(result_path.read_text())["owner_store_unchanged"] is False
    assert owner.read_text() == "concurrent user correction"


def test_watch_no_change_is_not_pass_and_saves_only_allowlisted_data(tool, tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(tool, "ROOT", tmp_path)
    owner = tmp_path / "learning.json"
    write_store(owner)
    monkeypatch.setattr(tool, "owner_store", lambda: owner)
    assert tool.watch(SimpleNamespace(word=["Miles"], seconds=.01)) == 2
    output = capsys.readouterr().out
    assert all(private not in output for private in ("DoNotDisclose", "private source", "private misspelling", "private-draft-id"))
    saved, = (tmp_path / "evidence/acceptance").glob("*/watch.jsonl")
    assert "DoNotDisclose" not in saved.read_text()
    summary = json.loads((saved.parent / "watch-result.json").read_text())
    assert summary["human_status"] == "NOT_EVALUATED" and summary["target_changes_observed"] == 0


@pytest.mark.parametrize("initially_missing,change_target,expected", [(True, True, 0), (False, True, 0), (False, False, 2)])
def test_watch_detects_first_learning_and_target_changes_only(tool, tmp_path, monkeypatch, initially_missing, change_target, expected):
    monkeypatch.setattr(tool, "ROOT", tmp_path)
    owner = tmp_path / "learning.json"
    if not initially_missing:
        write_store(owner)
    monkeypatch.setattr(tool, "owner_store", lambda: owner)
    clock = [0.0]
    monkeypatch.setattr(tool.time, "monotonic", lambda: clock[0])
    def next_snapshot(seconds):
        state = write_store(owner, count=2 if change_target else 1)
        state["words"][1]["count"] = 99
        owner.write_text(json.dumps(state))
        clock[0] += seconds
    monkeypatch.setattr(tool.time, "sleep", next_snapshot)
    assert tool.watch(SimpleNamespace(word=["Miles"], seconds=1)) == expected
    saved, = (tmp_path / "evidence/acceptance").glob("*/watch-result.json")
    assert json.loads(saved.read_text())["target_changes_observed"] == (1 if change_target else 0)


@pytest.mark.parametrize("args", [["watch", "--word", "Miles", "--seconds", "0"],
                                  ["watch", "--word", "Miles", "--seconds", "nan"],
                                  ["watch", "--seconds", "1"]])
def test_cli_rejects_unbounded_or_unfiltered_watch(tool, args):
    with pytest.raises(SystemExit) as error:
        tool.main(args)
    assert error.value.code == 2


CJK = re.compile(r"[\u3000-\u303f\u4e00-\u9fff\uff00-\uffef]")


def cjk_lines(source: str) -> list[int]:
    return [n for n, line in enumerate(source.splitlines(), 1) if CJK.search(line)]


def test_console_lines_are_english():
    """AGENTS.md: everything in this directory is English except test data.
    The tool's console lines were Chinese until 2026-10-09."""
    lines = cjk_lines((harness.ROOT / "tools" / "acceptance.py").read_text(encoding="utf-8"))
    assert lines == [], f"CJK text in tools/acceptance.py at lines {lines}"


def test_the_english_check_flags_the_old_console_lines():
    old = 'print(f"Ran {label} (exit 0)")\nprint(f"已执行 {label}（进程退出 0）")\n'
    assert cjk_lines(old) == [2]
