"""Pure-python unit tests for the P_you corpus builder (no ONNX / NPU).

Run: .venv/Scripts/python.exe -m pytest transcription_worker/test_corpus.py
"""

from __future__ import annotations

import sqlite3
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from transcription_worker.corpus import (  # noqa: E402
    build_p_you,
    load_user_segments,
    recency_weight,
    split_chronological,
)

NOW = datetime(2026, 7, 19, tzinfo=timezone.utc)


class FakeTokenizer:
    """One token id per character, so tests stay legible without a real
    Whisper tokenizer (mirrors test_bias_trie.py's FakeTokenizer)."""

    def encode(self, text, add_special_tokens=False):
        assert add_special_tokens is False
        return [ord(c) for c in text]


def _make_db(rows: list[tuple[str, str]]) -> Path:
    """rows: (text, utc_start) -- builds a minimal `transcript_segments` table matching
    SqliteMeetingStore.cs's schema (only the columns corpus.py reads)."""
    d = Path(tempfile.mkdtemp())
    db_path = d / "transcripts.db"
    conn = sqlite3.connect(db_path)
    conn.execute(
        "CREATE TABLE transcript_segments (id INTEGER PRIMARY KEY AUTOINCREMENT, "
        "session_id TEXT, channel INTEGER, utc_start TEXT, "
        "duration_ms INTEGER, text TEXT)"
    )
    conn.executemany(
        "INSERT INTO transcript_segments (session_id, channel, utc_start, duration_ms, text) "
        "VALUES ('s1', 0, ?, 1000, ?)",
        [(utc, text) for text, utc in rows],
    )
    conn.commit()
    conn.close()
    return db_path


# --- load_user_segments -------------------------------------------------


def test_load_user_segments_returns_chronological_order():
    db_path = _make_db(
        [("second", "2026-07-02T00:00:00Z"), ("first", "2026-07-01T00:00:00Z")]
    )
    segs = load_user_segments(db_path)
    assert [t for t, _ in segs] == ["first", "second"]


def test_load_user_segments_missing_db_raises():
    missing = Path(tempfile.mkdtemp()) / "nope.db"
    try:
        load_user_segments(missing)
        assert False, "expected RuntimeError"
    except RuntimeError:
        pass


# --- recency_weight -------------------------------------------------------


def test_recency_weight_is_one_at_zero_age():
    w = recency_weight(NOW.isoformat().replace("+00:00", "Z"), tau_days=30.0, now=NOW)
    assert abs(w - 1.0) < 1e-9


def test_recency_weight_decays_with_age():
    old = (NOW - timedelta(days=60)).isoformat().replace("+00:00", "Z")
    recent = (NOW - timedelta(days=1)).isoformat().replace("+00:00", "Z")
    w_old = recency_weight(old, tau_days=30.0, now=NOW)
    w_recent = recency_weight(recent, tau_days=30.0, now=NOW)
    assert 0.0 < w_old < w_recent <= 1.0


def test_recency_weight_never_negative_for_future_timestamps():
    future = (NOW + timedelta(days=5)).isoformat().replace("+00:00", "Z")
    assert recency_weight(future, tau_days=30.0, now=NOW) == 1.0


# --- build_p_you ------------------------------------------------------------


def test_build_p_you_skips_empty_segments():
    segs = [("hi", NOW.isoformat().replace("+00:00", "Z")), ("   ", NOW.isoformat())]
    lm = build_p_you(segs, FakeTokenizer(), now=NOW)
    # only "hi" contributes; unigram counts for 'h' and 'i' should be nonzero
    vec = lm.score_vector(())
    assert vec[ord("h")] > vec[ord("z")]


def test_build_p_you_recent_segments_weighted_more_than_old():
    old = (NOW - timedelta(days=90)).isoformat().replace("+00:00", "Z")
    recent = NOW.isoformat().replace("+00:00", "Z")
    segs = [("a", old), ("b", recent)]
    lm = build_p_you(segs, FakeTokenizer(), tau_days=30.0, now=NOW)
    vec = lm.score_vector(())
    assert vec[ord("b")] > vec[ord("a")]


# --- split_chronological ----------------------------------------------------


def test_split_chronological_holds_out_most_recent():
    segs = [
        ("d1", "2026-07-01T00:00:00Z"),
        ("d2", "2026-07-02T00:00:00Z"),
        ("d3", "2026-07-03T00:00:00Z"),
        ("d4", "2026-07-04T00:00:00Z"),
    ]
    train, held_out = split_chronological(segs, holdout_frac=0.25)
    assert [t for t, _ in train] == ["d1", "d2", "d3"]
    assert [t for t, _ in held_out] == ["d4"]


def test_split_chronological_invalid_fraction_raises():
    try:
        split_chronological([("a", "2026-07-01T00:00:00Z")], holdout_frac=1.5)
        assert False, "expected ValueError"
    except ValueError:
        pass


if __name__ == "__main__":
    import pytest

    raise SystemExit(pytest.main([__file__, "-v"]))
