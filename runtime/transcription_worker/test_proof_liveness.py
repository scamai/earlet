# test_proof_liveness.py
"""Proof program B — liveness stress obligations B-S1..B-S7 (component spec
@ tip 8a5b9f67da, proof card B) as targeted EmissionHold property tests, plus
a B1 bounded-terminal grid. Companion instrument: the per-turn ledger sim
(kept outside this repository) proves B1/A1..A7 on real meetings; this
module pins the stress-case semantics structurally.

Two of these tests were DELIBERATE strict-xfail pins for real defects found
during the proof run; both defects are FIXED (2026-09-03) and the pins now
assert the spec'd behavior:

  DEFECT-1 (S3 residue, FIXED in emission_hold.py offer()): an
    authoritative-EMPTY turn whose label never reaches the 0.4s evidence
    floor used to squat hold+label_deadline = 21.6s at the head of the
    in-order queue, because the label-missing gate ran BEFORE the span-text
    lookup. Now the empty check precedes the label wait: a READY-empty span
    consumes at hold maturity (no line is ever emitted, so no label is
    needed) — named + countable (EMPTY_RELEASE).

  DEFECT-2 (A5 residue, FIXED in ops_stream_whisper._decode_when_settled):
    decode-once deduped by 0.5s START-BUCKET while settle tolerance admits
    +/-0.25s jitter — a start jittering ACROSS a bucket edge (43.23 ->
    43.43) re-decoded the same turn. Now the neighbor buckets are checked
    for an already-decoded start within the tolerance before a bucket is
    treated as new; deliberate re-decodes (empty-carry superset on the
    seg_base window path, hold-forced first decodes, genuinely regrown
    starts > 0.25s) still fire.
"""
import random

import pytest

from transcription_worker.emission_hold import EmissionHold, Line

LBL = lambda s, e: (7, e - s)               # evidence-backed label, spk 7
TXT = lambda s, e: f"[{s:.1f}-{e:.1f}]"
EMPTY = lambda s, e: ""


# ---------------------------------------------------------------------------
# B-S1 — late diar: a turn materializing beyond safe_lag is DELEGATED to
# finalize (named + counted), and the pipeline never blocks on it.
# ---------------------------------------------------------------------------

def test_s1_late_diar_delegates_and_never_blocks():
    logs = []
    h = EmissionHold(hold_s=1.0, safe_lag_s=30.0, debug_cb=logs.append)
    # steady stream: two lines emitted, watermark capped at frontier-30
    assert len(h.offer([(30.0, 40.0, 1)], 41.5, LBL, TXT, frontier_s=41.0)) == 1
    assert len(h.offer([(50.0, 60.0, 1)], 61.5, LBL, TXT, frontier_s=61.0)) == 1
    assert h._released_until == 31.0                   # capped: 61 - 30
    logs.clear()
    # turn (18,20) materializes at now=65.5 — appear_lag 45.5s > SAFE_LAG:
    # end 20 <= wm 31 -> must DELEGATE (late_lost log), and a normal fresh
    # turn offered in the SAME tick must release — no blocking, ever.
    out = h.offer([(18.0, 20.0, 2), (62.0, 64.0, 3)], 65.5, LBL, TXT,
                  frontier_s=65.0)
    assert [(l.start, l.end) for l in out] == [(62.0, 64.0)], \
        "fresh turn must flow past the delegated late turn"
    assert any(l.startswith("late_lost_to_finalize: 18.0-20.0")
               for l in logs), "delegation must be named + countable"


# ---------------------------------------------------------------------------
# B-S2 — late ASR: span text at deadline-1s emits complete; at deadline+1s
# the line drops (never empty) and the queue behind it flows.
# ---------------------------------------------------------------------------

def test_s2_late_asr_deadline_minus_1_emits_complete():
    h = EmissionHold(hold_s=1.0, text_deadline_s=12.0)   # deadline age = 13
    turns = [(0.0, 3.0, 0)]
    assert h.offer(turns, 10.0, LBL, EMPTY,
                   span_text_fn=lambda s, e: None) == []      # in flight: wait
    out = h.offer(turns, 15.9, LBL, EMPTY,                    # age 12.9 < 13
                  span_text_fn=lambda s, e: "arrived T-1s")
    assert out == [Line(0.0, 3.0, 7, "arrived T-1s")]


def test_s2_late_asr_deadline_plus_1_drops_never_empty_queue_flows():
    logs = []
    h = EmissionHold(hold_s=1.0, text_deadline_s=12.0, debug_cb=logs.append)
    span = lambda s, e: ("B words" if (s, e) == (3.0, 6.0) else None)
    turns = [(0.0, 3.0, 0), (3.0, 6.0, 1)]
    assert h.offer(turns, 10.0, LBL, EMPTY, span_text_fn=span) == []
    out = h.offer(turns, 16.1, LBL, EMPTY, span_text_fn=span)  # A age 13.1>13
    assert [(l.start, l.text) for l in out] == [(3.0, "B words")], \
        "A drops at deadline (never emitted empty); B flows same tick"
    assert any(l.startswith("hold_drop: DEADLINE 0.0-3.0") for l in logs)
    assert all(l.text for l in out), "no empty line may ever emit"


# ---------------------------------------------------------------------------
# B-S3 — empty turn: authoritative "" consumes (no line), never emits, and
# with a label present it consumes at hold age without blocking the tick.
# ---------------------------------------------------------------------------

def test_s3_empty_decode_consumes_no_line_no_block():
    logs = []
    h = EmissionHold(hold_s=1.0, label_deadline_s=12.0, debug_cb=logs.append)
    span = lambda s, e: ("" if (s, e) == (0.0, 2.0) else "real words")
    out = h.offer([(0.0, 2.0, 4), (3.0, 5.0, 1)], 11.0, LBL, EMPTY,
                  span_text_fn=span)
    assert [(l.start, l.text) for l in out] == [(3.0, "real words")], \
        "empty consumes silently; later line releases in the SAME tick"
    assert any(l.startswith("hold_drop: EMPTY_RELEASE 0.0-2.0") for l in logs)
    # consumed for good: re-offer emits nothing, flush emits nothing
    assert h.offer([(0.0, 2.0, 4)], 12.0, LBL, EMPTY, span_text_fn=span) == []
    assert h.flush(LBL, EMPTY, span_text_fn=span) == []


def test_s3x_labelless_empty_should_consume_at_hold_not_label_deadline():
    h = EmissionHold(hold_s=9.6, label_deadline_s=12.0,
                     margin_hi=3.0, margin_min_age_s=3.0)
    # (0,2) decodes to "" and never reaches the 0.4s label evidence floor
    # (the overlap case). (3,5) is a good, matured, labelable line.
    lbl = lambda s, e: (None if s < 2.5 else (1, 1.5, 9.0))
    span = lambda s, e: ("" if (s, e) == (0.0, 2.0) else "good line")
    # now=15: empty turn age 13 (past hold 9.6) with "" READY — per spec it
    # should consume NOW and let (3,5) (age 10, label+text ready) release.
    out = h.offer([(0.0, 2.0, 4), (3.0, 5.0, 1)], 15.0, lbl, EMPTY,
                  span_text_fn=span)
    assert [l.start for l in out] == [3.0], \
        "spec: authoritative empty consumes at hold age; today it blocks " \
        "until hold+label_deadline (age 21.6)"


def test_s3_labelless_empty_consumes_at_hold_named_and_for_good():
    """Companion to DEFECT-1 (fixed 2026-09-03; pre-fix this pinned the
    bounded-but-slow 21.6s squat): the label-less authoritative empty
    consumes at hold age — named + countable (EMPTY_RELEASE) — the queued
    line flows in the SAME tick, and the consumption is terminal: re-offer
    emits nothing, flush emits nothing."""
    logs = []
    h = EmissionHold(hold_s=9.6, label_deadline_s=12.0, debug_cb=logs.append)
    lbl = lambda s, e: (None if s < 2.5 else (1, 1.5))
    span = lambda s, e: ("" if (s, e) == (0.0, 2.0) else "good line")
    turns = [(0.0, 2.0, 4), (3.0, 5.0, 1)]
    out = h.offer(turns, 15.0, lbl, EMPTY, span_text_fn=span)  # ages 13/10
    assert [(l.start, l.text) for l in out] == [(3.0, "good line")], \
        "empty consumes at hold age; the later line flows in the same tick"
    assert any("EMPTY_RELEASE 0.0-2.0" in l for l in logs)
    # consumed for good: no resurrection on re-offer or flush
    assert h.offer(turns, 16.0, lbl, EMPTY, span_text_fn=span) == []
    assert h.flush(lbl, EMPTY, span_text_fn=span) == []


# ---------------------------------------------------------------------------
# B-S4 — ultra-short turn (< evidence floor): geometry floor drops it; if one
# still reaches the hold, it terminates bounded (fallback release or named
# consume) and never freezes the queue.
# ---------------------------------------------------------------------------

def test_s4_ultra_short_turn_bounded_terminal():
    from transcription_worker.neural_turns import _stitch_and_filter
    # geometry's own floor: a 0.2s run never becomes a turn
    assert _stitch_and_filter([(0.0, 0.2, 1)], 0.6, 0.4, 12.0) == []
    # defense in depth: a sub-floor turn AT the hold, label always None
    # (below the 0.4s evidence floor) -> label-deadline fallback releases
    # with the turn's own speaker; bounded at hold+label_deadline, queue
    # flows. The substantial later turn DOES have evidence.
    lbl = lambda s, e: (None if (e - s) < 0.4 else (1, 1.5))
    h = EmissionHold(hold_s=1.0, label_deadline_s=5.0)
    turns = [(0.0, 0.2, 3), (1.0, 3.0, 1)]
    assert h.offer(turns, 4.0, lbl, TXT) == []           # within deadline
    out = h.offer(turns, 6.5, lbl, TXT)                  # age 6.3 > 1+5
    assert [(l.start, l.speaker) for l in out] == [(0.0, 3), (1.0, 1)], \
        "ultra-short releases with fallback id; later line flows"
    # speaker-less variant (-1): nothing honest to print -> consumed, no line
    logs = []
    h2 = EmissionHold(hold_s=1.0, label_deadline_s=5.0, debug_cb=logs.append)
    out = h2.offer([(0.0, 0.2, -1), (1.0, 3.0, 1)], 6.5, lbl, TXT)
    assert [(l.start, l.speaker) for l in out] == [(1.0, 1)]
    assert any("LABEL_DEADLINE 0.0-0.2" in l for l in logs)


# ---------------------------------------------------------------------------
# B-S5 — rapid speaker switch: alternating 0.5s turns emit in order, one
# line per turn, no merge in emission.
# ---------------------------------------------------------------------------

def test_s5_rapid_speaker_switch_order_no_merge():
    h = EmissionHold(hold_s=1.0)
    turns = [(round(i * 0.6, 2), round(i * 0.6 + 0.5, 2), i % 2)
             for i in range(10)]
    lbl = lambda s, e: (int(round(s / 0.6)) % 2, 0.5)
    out = h.offer(turns, 20.0, lbl, TXT)
    assert [(l.start, l.end) for l in out] == \
        [(s, e) for s, e, _ in turns], "one line per turn, exact spans, no merge"
    assert [l.speaker for l in out] == [i % 2 for i in range(10)]
    starts = [l.start for l in out]
    assert starts == sorted(starts)


# ---------------------------------------------------------------------------
# B-S6 — queue backlog: 50 ready turns at once all emit in ONE offer,
# ordered, non-overlapping, watermark still safe-horizon bounded.
# ---------------------------------------------------------------------------

def test_s6_backlog_50_ready_turns_all_emit_ordered_bounded():
    h = EmissionHold(hold_s=1.0, safe_lag_s=30.0)
    turns = [(i * 2.0, i * 2.0 + 1.5, i % 3) for i in range(50)]
    now = 50 * 2.0 + 2.0                                  # all matured
    out = h.offer(turns, now, LBL, TXT, frontier_s=now)
    assert len(out) == 50, "backlog fully drains in one pass"
    starts = [l.start for l in out]
    assert starts == sorted(starts), "ordered release"
    for a, b in zip(out, out[1:]):
        assert b.start >= a.end - 1e-6, "no overlapping emissions"
    assert h._released_until == pytest.approx(now - 30.0), \
        "watermark stays safe-horizon capped even under backlog"


# ---------------------------------------------------------------------------
# B-S7 — out-of-order decode readiness: a later turn's text ready first must
# not overtake or skip the earlier turn; the frontier stays bounded.
# ---------------------------------------------------------------------------

def test_s7_out_of_order_decode_in_order_no_skip():
    h = EmissionHold(hold_s=1.0, text_deadline_s=60.0)
    ready = {}
    span = lambda s, e: ready.get((s, e))
    turns = [(0.0, 3.0, 0), (3.0, 6.0, 1), (6.0, 9.0, 2)]
    ready[(6.0, 9.0)] = "C first"
    assert h.offer(turns, 12.0, LBL, EMPTY, span_text_fn=span) == []
    ready[(3.0, 6.0)] = "B second"
    assert h.offer(turns, 13.0, LBL, EMPTY, span_text_fn=span) == []
    ready[(0.0, 3.0)] = "A last"
    out = h.offer(turns, 14.0, LBL, EMPTY, span_text_fn=span)
    assert [(l.start, l.text) for l in out] == [
        (0.0, "A last"), (3.0, "B second"), (6.0, "C first")], \
        "all emit chronologically once the earliest lands; nothing skipped"


def test_s7_seeded_random_readiness_chronological_and_complete():
    rng = random.Randomon 2026-09-03                        # fixed seed
    n = 10
    turns = [(i * 3.0, i * 3.0 + 2.0, i % 4) for i in range(n)]
    ready_at = {t[:2]: 12.0 + 2.0 * j
                for t, j in zip(turns, rng.sample(range(n), n))}
    h = EmissionHold(hold_s=1.0, text_deadline_s=120.0)
    span = lambda s, e: ("w" if ready_at.get((s, e), 1e9) <= now[0] else None)
    now = [0.0]
    emitted = []
    t = 0.5
    while t < 60.0:
        now[0] = t
        for ln in h.offer(turns, t, LBL, EMPTY, span_text_fn=span):
            emitted.append((t, ln))
        t += 0.5
    assert len(emitted) == n, "every turn emits (no skip, no loss)"
    starts = [ln.start for _, ln in emitted]
    assert starts == sorted(starts), "strictly chronological pane"
    # in-order barrier: turn i cannot emit before the latest readiness among
    # turns 0..i (it queues behind them) — and must emit within one tick after
    prefix_ready = 0.0
    for (t_emit, ln), tu in zip(emitted, turns):
        prefix_ready = max(prefix_ready, ready_at[tu[:2]])
        assert t_emit >= prefix_ready - 1e-6
        assert t_emit <= prefix_ready + 0.5 + 1e-6, \
            "frontier bounded: emits within one tick of unblocking"


# ---------------------------------------------------------------------------
# B1 — bounded-terminal grid: label {ready, never} x text {ready, late,
# never} x fallback {real spk, -1}: every combination terminalizes within
# hold + max(label_deadline, text_deadline) + tick of the turn's end.
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("label_ready", [True, False])
@pytest.mark.parametrize("text_mode", ["ready", "late18", "never"])
@pytest.mark.parametrize("spk", [2, -1])
def test_b1_single_turn_terminal_bounded(label_ready, text_mode, spk):
    HOLD, LDL, TDL, TICK = 9.6, 12.0, 12.0, 0.5
    h = EmissionHold(hold_s=HOLD, label_deadline_s=LDL, text_deadline_s=TDL,
                     margin_hi=3.0, margin_min_age_s=3.0)
    lbl = (lambda s, e: (5, 1.0, 2.0)) if label_ready else (lambda s, e: None)
    now = [0.0]

    def span(s, e):
        if text_mode == "ready":
            return "words"
        if text_mode == "late18" and now[0] >= 18.0:
            return "late words"
        return None

    turn = (0.0, 2.0, spk)
    terminal_at = None
    t = 0.5
    while t < 45.0:
        now[0] = t
        out = h.offer([turn], t, lbl, EMPTY, span_text_fn=span)
        if terminal_at is None and (out or not h._pending):
            terminal_at = t                # released or consumed/dropped
        t += TICK
    assert terminal_at is not None, "turn must reach a terminal state"
    bound = turn[1] + HOLD + max(LDL, TDL) + TICK + 1e-6
    assert terminal_at <= bound, (
        f"terminal at {terminal_at} exceeds bound {bound} "
        f"(label_ready={label_ready} text={text_mode} spk={spk})")
    h.flush(lbl, EMPTY, span_text_fn=span)
    assert not h._pending                    # nothing lingers past flush


# ---------------------------------------------------------------------------
# DEFECT-2 pin — decode-once bucket-edge leak (wire, ops_stream_whisper).
# ---------------------------------------------------------------------------

def _decode_probe():
    """Duck-typed _Session decode-state probe: real _decode_when_settled /
    _ensure_turn_decode_state logic, stubbed _request_turn_decode recorder
    (no engine, no threads)."""
    import types
    from transcription_worker import ops_stream_whisper as osw
    S = osw._Session
    p = types.SimpleNamespace()
    p._turn_texts = None
    p._hold = types.SimpleNamespace(hold_s=9.6, _released_until=-1.0)
    p.requests = []
    p._ensure_turn_decode_state = lambda: S._ensure_turn_decode_state(p)
    p._turn_bucket = S._turn_bucket
    p._turn_key = S._turn_key
    p._request_turn_decode = lambda s, e: p.requests.append((s, e))
    p.decode_when_settled = lambda s, e, n: S._decode_when_settled(p, s, e, n)
    return p


def test_decode_once_holds_for_jitter_within_bucket():
    p = _decode_probe()
    p.decode_when_settled(43.23, 44.58, 48.0)   # interior (age 3.4): decode
    p.decode_when_settled(43.20, 44.60, 50.4)   # jitter, same 0.5s bucket
    assert len(p.requests) == 1, "same-bucket jitter never re-decodes"


def test_decode_once_should_hold_across_bucket_edge_jitter():
    p = _decode_probe()
    p.decode_when_settled(43.23, 44.58, 48.0)   # decode #1 (interior)
    p.decode_when_settled(43.43, 44.58, 50.4)   # +0.20s jitter, new bucket
    assert len(p.requests) == 1, \
        "spec A5: a 0.2s start jitter is the SAME turn — one decode"
