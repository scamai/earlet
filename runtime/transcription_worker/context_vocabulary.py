"""Pure, bounded ranking of an already-admitted vocabulary by app context."""
from __future__ import annotations

import math
import time
from collections import defaultdict
from typing import Any

MAX_OBSERVATIONS = 512
MAX_CORRECTIONS = 32
MAX_TEXT = 256
MAX_TIMESTAMP = 2**63 - 1
SCENES = frozenset(("terminal", "wechat", "other"))
ALPHA = 20.0


def rank_indices(
    entries: list[dict],
    evidence: object,
    scene: object,
    *,
    now_ms: int | None = None,
) -> list[int]:
    """Return entry indices ordered by bounded, scene-local correction evidence.

    Eligibility and recognition strength belong to the caller.  This function
    only permutes the supplied rows and never mutates them or creates rows.
    """
    original = list(range(len(entries))) if isinstance(entries, list) else []
    if not isinstance(entries, list) or len(entries) > 600 or not isinstance(scene, str) or scene not in SCENES:
        return original
    if now_ms is None:
        now = int(time.time() * 1000)
    elif _timestamp(now_ms):
        now = now_ms
    else:
        return original
    records = _validate(evidence)
    if records is None:
        return original

    keys: list[str | None] = []
    for row in entries:
        if not isinstance(row, dict):
            keys.append(None)
            continue
        value = row.get("text", row.get("word"))
        keys.append(value.lower() if isinstance(value, str) and value.strip() else None)
    usable = {key for key in keys if key is not None}
    if not usable:
        return original
    local = [record for record in records if record.get("scene") == scene]
    if not local:
        return original

    # A record can vote only once for a word.  Positive support wins over a
    # negative vote from the same observation.
    support: dict[str, dict[str, int]] = defaultdict(dict)
    oppose: dict[str, dict[str, int]] = defaultdict(dict)
    latest: dict[str, int] = {}
    for record in local:
        _collect(record, usable, support, oppose, latest)
    if not any(support.values()) and not any(oppose.values()):
        return original

    # Stored entry counts form the cross-scene prior.  Keep the first row for
    # duplicate lowercased keys, matching the vocabulary loader's policy.
    prior_counts: dict[str, int] = {}
    for row, key in zip(entries, keys):
        if key is None or key in prior_counts:
            continue
        count = _bounded_number(row.get("count")) if isinstance(row, dict) else None
        opposition = _bounded_number(row.get("opposition")) if isinstance(row, dict) else None
        prior_counts[key] = max(1, (count if count is not None else 1) - (opposition or 0))
    vocabulary_size = len(prior_counts) or 1
    prior_total = sum(prior_counts.values()) or 1

    local_net: dict[str, float] = {}
    total_local = 0.0
    for key in usable:
        net = max(0, sum(support[key].values()) - sum(oppose[key].values()))
        local_net[key] = float(net)
        total_local += net
    if total_local == 0:
        return original

    scores: dict[int, tuple[int, float, int]] = {}
    for index, (row, key) in enumerate(zip(entries, keys)):
        if key is None:
            scores[index] = (0, float("-inf"), index)
            continue
        global_probability = (prior_counts.get(key, 1) + ALPHA / vocabulary_size) / (prior_total + ALPHA)
        probability = (local_net[key] + ALPHA * global_probability) / (total_local + ALPHA)
        stored_seen = row.get("last_seen") if isinstance(row, dict) else None
        seen_at = stored_seen if _timestamp(stored_seen) else latest.get(key, now)
        age_days = max(0.0, (now - seen_at) / 86_400_000.0)
        score = math.log(probability) + 0.05 * math.exp(-age_days / 30.0)
        tier = 1 if isinstance(row, dict) and row.get("origin") == "taught" else 0
        scores[index] = (tier, score, index)
    return sorted(original, key=lambda index: (-scores[index][0], -scores[index][1], scores[index][2]))


def _collect(record: dict[str, Any], usable: set[str], positive, negative, latest) -> None:
    record_id = record["id"]
    weight = record["weight"]
    winners: set[str] = set()
    rows = record["corrections"]
    for correction in rows:
        if correction["word"]:
            target = correction["meant"].lower()
            if target in usable:
                positive[target][record_id] = weight
                winners.add(target)
                latest[target] = max(latest.get(target, 0), record["at"])
    for correction in rows:
        heard = correction["heard"].lower()
        if heard in usable and heard not in winners and correction["meant"].lower() != heard:
            negative[heard][record_id] = weight


def _validate(evidence: object) -> list[dict[str, Any]] | None:
    if not isinstance(evidence, dict) or type(evidence.get("version")) is not int or evidence["version"] != 1:
        return None
    records = evidence.get("observations")
    if not isinstance(records, list) or len(records) > MAX_OBSERVATIONS:
        return None
    seen: set[str] = set()
    for record in records:
        if not isinstance(record, dict):
            return None
        identity = record.get("id")
        if not isinstance(identity, str) or not 0 < len(identity) <= 320 or identity in seen:
            return None
        if not _timestamp(record.get("at")) or type(record.get("weight")) is not int or not 1 <= record["weight"] <= 4:
            return None
        record_scene = record.get("scene")
        if record_scene is not None and (not isinstance(record_scene, str) or record_scene not in SCENES):
            return None
        corrections = record.get("corrections")
        if not isinstance(corrections, list) or len(corrections) > MAX_CORRECTIONS:
            return None
        for correction in corrections:
            if not isinstance(correction, dict):
                return None
            heard, meant = correction.get("heard"), correction.get("meant")
            if not isinstance(heard, str) or len(heard) > MAX_TEXT or not isinstance(meant, str) or not 0 < len(meant) <= MAX_TEXT:
                return None
            if any(type(correction.get(field)) is not bool for field in ("word", "alias", "secret")):
                return None
        seen.add(identity)
    return records


def _timestamp(value: object) -> bool:
    return type(value) is int and 0 <= value <= MAX_TIMESTAMP


def _bounded_number(value: object) -> int | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if value < 0 or value > 1_000_000:
        return None
    return int(value)
