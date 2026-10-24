#!/usr/bin/env python3
"""Small, deterministic experiment for app-aware personal vocabulary ranking.

This is a logic/performance experiment, not an ASR accuracy benchmark. Counts
are explicit simulated confirmations; automatic recognition is never used as
training evidence. The three policies share one bounded recency bonus.
"""
from __future__ import annotations

import argparse
import json
import math
import random
import statistics
import sys
import time
from typing import Any

APPS = ("Terminal", "WeChat", "Other")
POLICIES = ("global", "hard_app", "soft_app")
ALPHA = 20.0
RECENCY_WEIGHT = 0.05


def _entry(word: str, global_count: int = 0, *, apps: dict[str, int] | None = None,
           last_seen: int = 0, negative: dict[str, int] | None = None) -> dict[str, Any]:
    return {"word": word, "global": global_count, "apps": dict(apps or {}),
            "last_seen": last_seen, "negative": dict(negative or {})}


def _recency(last_seen: int, now: int) -> float:
    age = max(0, now - last_seen)
    return RECENCY_WEIGHT * math.exp(-age / 30.0)


def _prepared(entries: list[dict[str, Any]]) -> tuple[list[dict[str, Any]], int, float]:
    """Validate the experiment input and derive a global prior denominator."""
    clean: list[dict[str, Any]] = []
    for row in entries:
        word = row.get("word")
        count = row.get("global", 0)
        if not isinstance(word, str) or not word or isinstance(count, bool) or not isinstance(count, int) or count < 0:
            continue
        apps = {a: n for a, n in row.get("apps", {}).items()
                if a in APPS and isinstance(n, int) and not isinstance(n, bool) and n >= 0}
        negative = {a: n for a, n in row.get("negative", {}).items()
                    if a in APPS and isinstance(n, int) and not isinstance(n, bool) and n >= 0}
        clean.append({"word": word, "global": count, "apps": apps,
                      "last_seen": int(row.get("last_seen", 0) or 0),
                      "negative": negative})
    total = sum(r["global"] for r in clean)
    vocab = max(1, len(clean))
    return clean, total, 1.0 / vocab


def rank(entries: list[dict[str, Any]], app: str, *, policy: str, k: int = 5,
         now: int = 100) -> list[str]:
    """Return unique words in deterministic score/order ranking."""
    if policy not in POLICIES or not isinstance(app, str) or k <= 0:
        return []
    rows, total, uniform = _prepared(entries)
    app_known = app in APPS
    local_rows = [r for r in rows if r["apps"].get(app, 0) or r["negative"].get(app, 0)]
    local_total = sum(max(0, r["apps"].get(app, 0) - r["negative"].get(app, 0)) for r in rows)
    scored: list[tuple[float, int, str]] = []
    for order, row in enumerate(rows):
        word = row["word"]
        global_n = row["global"]
        local_n = row["apps"].get(app, 0)
        local_n = max(0, local_n - row["negative"].get(app, 0))
        recent = _recency(row["last_seen"], now)
        p_global = (global_n + ALPHA * uniform) / (total + ALPHA)
        if policy == "global":
            score = math.log(max(p_global, 1e-300)) + recent
        elif policy == "hard_app":
            # Known app contexts use eligible local rows when any exist. A
            # cold/unknown context falls back to the global catalogue.
            if app_known and local_rows:
                if local_n <= 0:
                    continue
                p_local = (local_n + ALPHA * uniform) / (local_total + ALPHA)
                score = math.log(max(p_local, 1e-300)) + recent
            else:
                score = math.log(max(p_global, 1e-300)) + recent
        else:
            # Hierarchical smoothing: local evidence backs off to a smoothed
            # global prior, so a local correction does not globally ban a word.
            p_app = (local_n + ALPHA * p_global) / (local_total + ALPHA)
            score = math.log(max(p_app, 1e-300)) + recent
        scored.append((score, -order, word))
    scored.sort(key=lambda item: (-item[0], -item[1], item[2].casefold()))
    result: list[str] = []
    seen: set[str] = set()
    for _, _, word in scored:
        key = word.casefold()
        if key in seen:
            continue
        seen.add(key)
        result.append(word)
        if len(result) == k:
            break
    return result


def _scenarios() -> list[dict[str, Any]]:
    """Hand-labelled logic fixtures; labels are not derived from rankings."""
    return [
        {"name": "terminal-jargon", "app": "Terminal", "target": "repository",
         "entries": [_entry("repository", 8, apps={"Terminal": 6}), _entry("repetition", 8)]},
        {"name": "wechat-ordinary", "app": "WeChat", "target": "meeting",
         "entries": [_entry("meeting", 6), _entry("Meetin", 1, apps={"WeChat": 1})]},
        {"name": "wechat-recent-technical", "app": "WeChat", "target": "Miles",
         "entries": [_entry("Miles", 4, apps={"WeChat": 3}, last_seen=99), _entry("areas", 9)]},
        {"name": "cold-start-unknown-app", "app": "Other", "target": "repo",
         "entries": [_entry("repo", 2), _entry("report", 4, apps={"Terminal": 4})]},
        {"name": "app-switch", "app": "Terminal", "target": "kubectl",
         "entries": [_entry("kubectl", 6, apps={"Terminal": 5}), _entry("cupcake", 8, apps={"WeChat": 4})]},
        {"name": "negative-local-only", "app": "WeChat", "target": "Miles",
         "entries": [_entry("Miles", 5, apps={"WeChat": 1}, negative={"WeChat": 2}),
                     _entry("areas", 4, apps={"WeChat": 1})]},
        {"name": "stable-tie", "app": "Terminal", "target": "alpha",
         "entries": [_entry("alpha", 2), _entry("beta", 2), _entry("alpha", 1)]},
        {"name": "empty-data", "app": "Other", "target": None, "entries": []},
        {"name": "wechat-private-name", "app": "WeChat", "target": "Repoo",
         "entries": [_entry("Repoo", 2, apps={"WeChat": 2}), _entry("repo", 5)]},
        {"name": "wechat-first-technical-mention", "app": "WeChat", "target": "Miles",
         "entries": [_entry("Miles", 12, apps={"Terminal": 11}, last_seen=99)]
                    + [_entry(f"contact-{i}", 2, apps={"WeChat": 1}) for i in range(10)]},
        {"name": "other-fallback", "app": "Other", "target": "ssh",
         "entries": [_entry("ssh", 5, apps={"Terminal": 4}), _entry("cash", 4, apps={"WeChat": 3})]},
    ]


def _budget_cases() -> list[dict[str, Any]]:
    terminal = [_entry(f"terminal-noise-{i:02d}", 10 + i, apps={"Terminal": 1}) for i in range(40)]
    terminal.insert(7, _entry("repository", 8, apps={"Terminal": 6}, last_seen=99))
    wechat = [_entry(f"wechat-noise-{i:02d}", 8 + i, apps={"WeChat": 1}) for i in range(40)]
    wechat.insert(29, _entry("Miles", 4, apps={"WeChat": 3}, last_seen=98))
    return [
        {"name": "budget-terminal-41-entries", "app": "Terminal", "target": "repository", "entries": terminal},
        {"name": "budget-wechat-41-entries", "app": "WeChat", "target": "Miles", "entries": wechat},
    ]


def _scenario_report() -> dict[str, Any]:
    cases = []
    hits = {p: {1: 0, 3: 0, 5: 0, 32: 0} for p in POLICIES}
    all_cases = _scenarios() + _budget_cases()
    target_cases = [case for case in all_cases if isinstance(case["target"], str)]
    for case in all_cases:
        result = {"name": case["name"], "app": case["app"], "target": case["target"], "ranks": {}}
        for policy in POLICIES:
            ranked = rank(case["entries"], case["app"], policy=policy,
                          k=max(32, len(case["entries"])))
            target = case["target"]
            position = (next((i + 1 for i, word in enumerate(ranked) if word.casefold() == target.casefold()), None)
                        if isinstance(target, str) else None)
            result["ranks"][policy] = {"top5": ranked[:5], "rank": position,
                                      "selected_at_32": position is not None and position <= 32}
            for n in hits[policy]:
                hits[policy][n] += int(position is not None and position <= n)
        cases.append(result)
    denominator = len(target_cases)
    return {"cases": cases, "fixture_note": "small hand-labelled cases are illustrative logic tests, not ASR accuracy evidence",
            "evaluated_targets": denominator,
            "hit_at_k": {p: {f"{k}": (v / denominator if denominator else 0.0) for k, v in row.items()} for p, row in hits.items()}}


def _synthetic(size: int, seed: int) -> list[dict[str, Any]]:
    rng = random.Random(seed)
    rows = []
    for i in range(size):
        counts = {a: rng.randrange(0, 15) for a in APPS}
        rows.append(_entry(f"term{i:05d}", sum(counts.values()) + rng.randrange(0, 80), apps=counts,
                           last_seen=rng.randrange(0, 100)))
    return rows


def _benchmark() -> dict[str, Any]:
    output: dict[str, Any] = {}
    for size in (600, 10_000):
        rows = _synthetic(size, size)
        measurements: dict[str, dict[str, float]] = {}
        for policy in POLICIES:
            rank(rows, "Terminal", policy=policy, k=32)  # warmup
        samples_by_policy = {policy: [] for policy in POLICIES}
        for run in range(100):
            order = POLICIES[run % len(POLICIES):] + POLICIES[:run % len(POLICIES)]
            for policy in order:
                started = time.perf_counter_ns()
                rank(rows, "Terminal", policy=policy, k=32)
                samples_by_policy[policy].append((time.perf_counter_ns() - started) / 1_000_000)
        for policy in POLICIES:
            samples = samples_by_policy[policy]
            ordered = sorted(samples)
            measurements[policy] = {"p50_ms": statistics.median(ordered),
                                    "p95_ms": ordered[94], "runs": 100}
        output[str(size)] = measurements
    return output


def self_check() -> None:
    assert rank([], "Other", policy="soft_app") == []
    rows = [_entry("same", 2), _entry("SAME", 1)]
    assert len(rank(rows, "Other", policy="global", k=10)) == 1
    assert rank(rows, "Other", policy="global") == rank(rows, "Other", policy="global")
    assert rank(rows, "Other", policy="global") == rank(rows, "Other", policy="hard_app")
    unknown = [_entry("fresh", 2, last_seen=0), _entry("recent", 1, last_seen=99)]
    for policy in POLICIES:
        assert rank(unknown, "UnknownApp", policy=policy) == rank(unknown, "UnknownApp", policy="global")
    known_empty = [_entry("fresh", 2, last_seen=0), _entry("recent", 1, last_seen=99)]
    for policy in ("hard_app", "soft_app"):
        assert rank(known_empty, "Other", policy=policy) == rank(known_empty, "Other", policy="global")
    before = [_entry("Miles", 20, apps={"WeChat": 20}), _entry("areas", 18, apps={"WeChat": 1})]
    after = [_entry("Miles", 20, apps={"WeChat": 20}, negative={"WeChat": 20}), _entry("areas", 18, apps={"WeChat": 1})]
    assert rank(before, "WeChat", policy="soft_app")[0] == "Miles"
    assert rank(after, "WeChat", policy="soft_app")[0] == "areas"
    assert rank(before, "Terminal", policy="soft_app") == rank(after, "Terminal", policy="soft_app")
    for case in _scenarios() + _budget_cases():
        assert all(row["global"] >= sum(row["apps"].values()) for row in case["entries"])
    assert all(row["global"] >= sum(row["apps"].values()) for row in _synthetic(64, 64))
    assert rank([_entry("x", 1)], "Other", policy="global") == ["x"]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-check", action="store_true", help="run mechanical invariants")
    parser.add_argument("--output", type=argparse.FileType("w"), help="write JSON evidence here instead of stdout")
    args = parser.parse_args(argv)
    if args.self_check:
        self_check()
    report = {"experiment": "app-aware-context-vocabulary", "alpha": ALPHA,
              "recency_weight": RECENCY_WEIGHT, "policies": list(POLICIES),
              "counts_are": "explicit simulated confirmations only",
              "automatic_recognition_is_training_data": False,
              "scenarios": _scenario_report(), "benchmark": _benchmark(),
              "platform": sys.platform, "python": sys.version.split()[0]}
    stream = args.output or sys.stdout
    json.dump(report, stream, ensure_ascii=False, indent=2)
    stream.write("\n")
    if args.output:
        args.output.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
