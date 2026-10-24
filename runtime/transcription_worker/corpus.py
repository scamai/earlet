"""P_you corpus: build a personal n-gram LM from the app's own transcript
history (the language-model contract sec 6.1's lower-friction source -- no Slack/email connector
needed). Reads the transcript database (DB_PATH, or the path a request names)
read-only.
"""

from __future__ import annotations

import math
import os
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

from transcription_worker.ngram_lm import NgramLM

DB_PATH = Path(
    os.environ.get(
        "DICTATION_DB",
        Path(os.environ.get("LOCALAPPDATA", ".")) / "Dictation" / "dictation.db",
    )
)


def _connect(db_path: str | Path) -> sqlite3.Connection:
    db_path = Path(db_path)
    if not db_path.exists():
        raise RuntimeError(f"Transcript database not found at {db_path}.")
    conn = sqlite3.connect(f"file:{db_path.as_posix()}?mode=ro", uri=True, timeout=5.0)
    conn.row_factory = sqlite3.Row
    return conn


def load_user_segments(db_path: str | Path = DB_PATH) -> list[tuple[str, str]]:
    """All transcript segments, chronological, as (text, utc_start) pairs.

    Table name is `transcript_segments`, not the generic `segments`: the
    transcript_* prefix keeps clear of any other table a shared database holds
    and says which feature owns the table.
    """
    with _connect(db_path) as conn:
        rows = conn.execute(
            "SELECT text, utc_start FROM transcript_segments ORDER BY utc_start, id"
        ).fetchall()
    return [(r["text"], r["utc_start"]) for r in rows]


def _parse_utc(ts: str) -> datetime:
    return datetime.fromisoformat(ts.replace("Z", "+00:00"))


def recency_weight(utc_start: str, tau_days: float, now: datetime | None = None) -> float:
    """exp(-age_days/tau) -- spec sec 6.2's decay idea, simplified from the
    full w(e)=log(1+freq)*e^(-age/tau)*topic*conf to just the age term (no
    per-entity frequency/topic/confidence signal exists at the segment
    granularity this reads at)."""
    now = now or datetime.now(timezone.utc)
    age_days = max((now - _parse_utc(utc_start)).total_seconds() / 86400.0, 0.0)
    return math.exp(-age_days / tau_days)


def build_p_you(
    segments: list[tuple[str, str]],
    tokenizer,
    order: int = 4,
    tau_days: float = 30.0,
    now: datetime | None = None,
) -> NgramLM:
    """Tokenize + recency-weight + fit_hard. `segments` is (text, utc_start)
    pairs, e.g. from load_user_segments() or a chronological training slice
    from split_chronological()."""
    lm = NgramLM(order=order)
    sequences: list[list[int]] = []
    weights: list[float] = []
    for text, utc_start in segments:
        text = text.strip()
        if not text:
            continue
        ids = tokenizer.encode(text, add_special_tokens=False)
        if not ids:
            continue
        sequences.append(ids)
        weights.append(recency_weight(utc_start, tau_days, now))
    if sequences:
        lm.fit_hard(sequences, weights=weights)
    return lm


def split_chronological(
    segments: list[tuple[str, str]], holdout_frac: float = 0.15
) -> tuple[list[tuple[str, str]], list[tuple[str, str]]]:
    """Chronological train/held-out split: oldest -> train, most recent ->
    held out. Never random -- a random split leaks future vocabulary into
    the profile and overstates every downstream eval number (research memo,
    2026-07-18)."""
    if not 0.0 < holdout_frac < 1.0:
        raise ValueError("holdout_frac must be in (0, 1)")
    ordered = sorted(segments, key=lambda s: s[1])
    split_at = max(1, int(len(ordered) * (1.0 - holdout_frac))) if ordered else 0
    return ordered[:split_at], ordered[split_at:]
