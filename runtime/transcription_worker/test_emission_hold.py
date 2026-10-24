# test_emission_hold.py
from transcription_worker.emission_hold import EmissionHold, Line

LBL = lambda s, e: (7, e - s)
TXT = lambda s, e: f"[{s:.1f}-{e:.1f}]"

def test_young_turn_is_held_then_released():
    h = EmissionHold(hold_s=5.0)
    turns = [(0.0, 4.0, 7)]
    assert h.offer(turns, now_s=6.0, label_fn=LBL, text_fn=TXT) == []   # age 2 < 5
    out = h.offer(turns, now_s=9.5, label_fn=LBL, text_fn=TXT)          # age 5.5
    assert out == [Line(0.0, 4.0, 7, "[0.0-4.0]")]

def test_exactly_once():
    h = EmissionHold(hold_s=1.0)
    turns = [(0.0, 2.0, 3)]
    assert len(h.offer(turns, 10.0, LBL, TXT)) == 1
    assert h.offer(turns, 11.0, LBL, TXT) == []                          # no re-emit

def test_turn_may_grow_before_release():
    # same start, end extends while young -> released ONCE with final extent
    h = EmissionHold(hold_s=5.0)
    h.offer([(0.0, 2.0, 1)], 3.0, LBL, TXT)
    out = h.offer([(0.0, 3.5, 1)], 9.0, LBL, TXT)
    assert out == [Line(0.0, 3.5, 1, "[0.0-3.5]")]

def test_label_fn_overrides_turn_speaker():
    h = EmissionHold(hold_s=0.0)
    out = h.offer([(0.0, 2.0, 99)], 5.0, label_fn=lambda s, e: (4, 1.5), text_fn=TXT)
    assert out[0].speaker == 4

def test_no_label_no_release_until_flush():
    h = EmissionHold(hold_s=0.0)
    none_lbl = lambda s, e: None
    assert h.offer([(0.0, 2.0, 5)], 9.0, none_lbl, TXT) == []
    out = h.flush(none_lbl, TXT)                                         # falls back to turn spk
    assert out == [Line(0.0, 2.0, 5, "[0.0-2.0]")]

def test_empty_text_line_is_skipped():
    h = EmissionHold(hold_s=0.0)
    out = h.offer([(0.0, 2.0, 1)], 9.0, LBL, text_fn=lambda s, e: "  ")
    assert out == [] and h.flush(LBL, lambda s, e: "") == []

def test_overlapping_second_turn_clipped_by_watermark():
    # a later turn starting before the previous release's end is clipped to
    # the _released_until watermark: emitted spans never overlap, order by start
    h = EmissionHold(hold_s=0.0)
    out = h.offer([(0.0, 2.0, 1), (1.5, 4.0, 2)], 10.0, LBL, TXT)
    assert out == [Line(0.0, 2.0, 7, "[0.0-2.0]"),
                   Line(2.0, 4.0, 7, "[2.0-4.0]")]


# ---- turn-driven span decode protocol (span_text_fn, 2026-09-01) ----------

def test_span_text_pending_holds_within_grace_then_releases():
    """A matured turn WAITS for its in-flight span decode (span_text_fn ->
    None) within text_grace_s, then releases with the decoded text — born
    1:1 with the turn, no carrier join."""
    h = EmissionHold(hold_s=1.0, text_grace_s=5.0)
    turns = [(0.0, 2.0, 1)]
    # matured (now-end=4 >= hold 1) but decode still pending -> held
    assert h.offer(turns, 6.0, LBL, TXT, span_text_fn=lambda s, e: None) == []
    assert h._pending                      # kept for the next tick
    out = h.offer(turns, 6.5, LBL, TXT,
                  span_text_fn=lambda s, e: "decoded words")
    assert out == [Line(0.0, 2.0, 7, "decoded words")]


def test_span_text_deadline_expiry_uses_nonempty_carrier():
    """Past hold+deadline with the decode still wedged, a turn that DOES have
    a non-empty carrier releases with it (real text, fine to emit). The empty
    carrier case (drop, never empty) is covered separately."""
    h = EmissionHold(hold_s=1.0, text_deadline_s=2.0)
    out = h.offer([(0.0, 2.0, 1)], 9.0, LBL, TXT,
                  span_text_fn=lambda s, e: None)
    assert out == [Line(0.0, 2.0, 7, "[0.0-2.0]")]


def test_flush_uses_ready_span_text_else_carrier():
    """flush() cannot wait: a finished decode's text is used, a pending one
    falls back to the carrier join."""
    h = EmissionHold(hold_s=100.0)
    h.offer([(0.0, 2.0, 1), (3.0, 5.0, 2)], 6.0, LBL, TXT)   # both young/held
    ready = {(0.0, 2.0): "decoded words"}
    out = h.flush(LBL, TXT,
                  span_text_fn=lambda s, e: ready.get((round(s, 2),
                                                      round(e, 2))))
    assert out == [Line(0.0, 2.0, 7, "decoded words"),
                   Line(3.0, 5.0, 7, "[3.0-5.0]")]


def test_accuracy_first_waits_for_text_then_emits_complete():
    """Accuracy-first release (2026-09-02): a matured turn with no text yet is
    HELD (not released empty, not placeholder) until the real text arrives —
    then it emits complete. Late-but-complete beats empty-on-time."""
    from transcription_worker.emission_hold import EmissionHold
    h = EmissionHold(hold_s=1.0, text_deadline_s=12.0)
    turns = [(0.0, 3.0, 0)]
    # matured (now past hold) but decode still running -> held, nothing emitted
    out = h.offer(turns, now_s=5.0, label_fn=lambda s, e: [0],
                  text_fn=lambda s, e: "", span_text_fn=lambda s, e: None)
    assert out == [], "must wait for text, not emit empty"
    # text arrives a few seconds later -> emits the COMPLETE line
    out = h.offer(turns, now_s=8.0, label_fn=lambda s, e: [0],
                  text_fn=lambda s, e: "",
                  span_text_fn=lambda s, e: "the real words")
    assert len(out) == 1 and out[0].text == "the real words"
    assert out[0].speaker == 0 and out[0].end == 3.0


def test_wedged_decode_drops_line_never_empty():
    """Deadlock guard: a genuinely wedged decode (past text_deadline_s) DROPS
    the line so later lines aren't stalled forever — but never emits an empty
    line. A dropped span is backfilled by the final accurate pass."""
    from transcription_worker.emission_hold import EmissionHold
    h = EmissionHold(hold_s=1.0, text_deadline_s=12.0)
    turns = [(0.0, 3.0, 0)]
    # past hold+deadline, still no text, empty carrier -> dropped, not empty
    out = h.offer(turns, now_s=20.0, label_fn=lambda s, e: [0],
                  text_fn=lambda s, e: "", span_text_fn=lambda s, e: None)
    assert out == [], "wedged decode: drop, never emit empty"
    # and the stall is cleared — a LATER turn can now release normally
    out = h.offer([(3.0, 6.0, 1)], now_s=25.0, label_fn=lambda s, e: [1],
                  text_fn=lambda s, e: "", span_text_fn=lambda s, e: "next line")
    assert len(out) == 1 and out[0].text == "next line", "later line not stalled"


def test_turn_grows_while_waiting_for_text_emits_once():
    """Edge (2026-09-02 exhaustive pass): a turn held for its text can GROW
    (speaker still talking) before the text arrives. When text for the NEW
    extent arrives it emits once with the final extent; never re-emits."""
    from transcription_worker.emission_hold import EmissionHold
    h = EmissionHold(hold_s=1.0, text_deadline_s=12.0)
    texts = {}
    span = lambda s, e: texts.get((round(s, 2), round(e, 2)))
    assert h.offer([(0.0, 3.0, 0)], 5.0, lambda s, e: [0],
                   lambda s, e: "", span_text_fn=span) == []
    texts[(0.0, 4.0)] = "grown line"
    out = h.offer([(0.0, 4.0, 0)], 6.0, lambda s, e: [0],
                  lambda s, e: "", span_text_fn=span)
    assert out == [Line(0.0, 4.0, 0, "grown line")]
    assert h.offer([(0.0, 4.0, 0)], 7.0, lambda s, e: [0],
                   lambda s, e: "", span_text_fn=span) == []


def test_stale_decode_key_after_growth_falls_to_deadline_drop_not_empty():
    """Edge: text exists only for the OLD extent after the turn grew (stale
    key never matches). It waits out the deadline, then DROPS (empty carrier)
    — never emits empty, never stalls forever."""
    from transcription_worker.emission_hold import EmissionHold
    h = EmissionHold(hold_s=1.0, text_deadline_s=3.0)
    texts = {(0.0, 3.0): "old extent text"}
    span = lambda s, e: texts.get((round(s, 2), round(e, 2)))
    h.offer([(0.0, 3.0, 0)], 3.0, lambda s, e: [0], lambda s, e: "",
            span_text_fn=span)
    assert h.offer([(0.0, 4.0, 0)], 4.0, lambda s, e: [0], lambda s, e: "",
                   span_text_fn=span) == []            # within deadline: wait
    assert h.offer([(0.0, 4.0, 0)], 20.0, lambda s, e: [0], lambda s, e: "",
                   span_text_fn=span) == []            # past deadline: drop, not empty


def test_in_order_release_no_overtaking_no_watermark_kill():
    """The 74.7s whole-turn-hole bug (2026-09-02 device forensics): turn B's
    text arrived before turn A's; B released first, advancing the watermark
    past A, whose eventual release was silently skipped. In-order release:
    B queues behind A; when A's text lands, BOTH emit, chronologically."""
    from transcription_worker.emission_hold import EmissionHold
    h = EmissionHold(hold_s=1.0, text_deadline_s=12.0)
    texts = {}
    span = lambda s, e: texts.get((round(s, 2), round(e, 2)))
    turns = [(0.0, 3.0, 0), (3.0, 6.0, 1)]
    texts[(3.0, 6.0)] = "B text ready first"
    out = h.offer(turns, 10.0, lambda s, e: [0], lambda s, e: "",
                  span_text_fn=span)
    assert out == [], "B must queue behind waiting A, not overtake"
    texts[(0.0, 3.0)] = "A text arrives"
    out = h.offer(turns, 11.0, lambda s, e: [0], lambda s, e: "",
                  span_text_fn=span)
    assert [(l.start, l.text) for l in out] == [
        (0.0, "A text arrives"), (3.0, "B text ready first")], \
        "both emit, chronological order, no hole"


def test_deadline_drop_unblocks_queued_turns():
    """A genuinely wedged earlier turn drops at the deadline and the queue
    behind it flows — one bad decode cannot freeze the stream."""
    from transcription_worker.emission_hold import EmissionHold
    h = EmissionHold(hold_s=1.0, text_deadline_s=5.0)
    texts = {(3.0, 6.0): "B fine"}
    span = lambda s, e: texts.get((round(s, 2), round(e, 2)))
    turns = [(0.0, 3.0, 0), (3.0, 6.0, 1)]
    assert h.offer(turns, 8.0, lambda s, e: [0], lambda s, e: "",
                   span_text_fn=span) == []       # A within deadline: both wait
    out = h.offer(turns, 12.0, lambda s, e: [0], lambda s, e: "",
                  span_text_fn=span)              # A past deadline: drop; B flows
    assert [(l.start, l.text) for l in out] == [(3.0, "B fine")]



def _span_texts(d):
    return lambda s, e: d.get((round(s, 1), round(e, 1)))

# ---- confidence-based release (2026-09-02: label deadline + margin fast
# path — the shipped fix for the 25-min live freeze; every gate bounded,
# strict order preserved) ----

def test_label_deadline_releases_with_turn_speaker():
    h = EmissionHold(hold_s=5.0, label_deadline_s=12.0)
    none_lbl = lambda s, e: None
    turns = [(0.0, 2.0, 5)]
    # matured but label missing, within deadline -> waits
    assert h.offer(turns, 15.0, none_lbl, TXT) == []
    # past hold+deadline (2+5+12=19) -> releases with the TURN's speaker
    out = h.offer(turns, 19.5, none_lbl, TXT)
    assert out == [Line(0.0, 2.0, 5, "[0.0-2.0]")]

def test_label_missing_unbounded_without_deadline():
    h = EmissionHold(hold_s=5.0)                  # legacy: no deadline
    none_lbl = lambda s, e: None
    turns = [(0.0, 2.0, 5)]
    assert h.offer(turns, 100.0, none_lbl, TXT) == []   # waits forever

def test_margin_fast_path_releases_before_hold():
    h = EmissionHold(hold_s=9.6, margin_hi=3.0, margin_min_age_s=3.0)
    hi_lbl = lambda s, e: (7, 2.0, 8.0)          # margin 8 >= 3
    texts = {(0.0, 2.0): "early words"}
    turns = [(0.0, 2.0, 1)]
    # age 2.5 < min_age 3 -> still young
    assert h.offer(turns, 4.5, hi_lbl, TXT,
                   span_text_fn=lambda s, e: texts.get((s, e))) == []
    # age 4.0 >= min_age, margin high, text ready -> released (before 9.6
    # hold). Speaker 1 (not label 7): held at 4.5 -> pinned identity rule.
    out = h.offer(turns, 6.0, hi_lbl, TXT,
                  span_text_fn=lambda s, e: texts.get((s, e)))
    assert out == [Line(0.0, 2.0, 1, "early words")]

def test_margin_fast_path_first_sight_takes_label():
    h = EmissionHold(hold_s=9.6, margin_hi=3.0, margin_min_age_s=3.0)
    hi_lbl = lambda s, e: (7, 2.0, 8.0)
    texts = {(0.0, 2.0): "early words"}
    # FIRST offer already fast-eligible -> label_fn speaker used
    out = h.offer([(0.0, 2.0, 1)], 6.0, hi_lbl, TXT,
                  span_text_fn=lambda s, e: texts.get((s, e)))
    assert out == [Line(0.0, 2.0, 7, "early words")]

def test_low_margin_waits_full_hold():
    h = EmissionHold(hold_s=9.6, margin_hi=3.0, margin_min_age_s=3.0)
    lo_lbl = lambda s, e: (7, 2.0, 1.2)          # photo finish: 1.2 < 3
    texts = {(0.0, 2.0): "slow words"}
    turns = [(0.0, 2.0, 1)]
    assert h.offer(turns, 6.0, lo_lbl, TXT,
                   span_text_fn=lambda s, e: texts.get((s, e))) == []   # no fast
    out = h.offer(turns, 11.7, lo_lbl, TXT,      # age 9.7 >= hold
                  span_text_fn=lambda s, e: texts.get((s, e)))
    assert out == [Line(0.0, 2.0, 1, "slow words")]   # pinned (held at 6.0)

def test_two_tuple_label_fn_still_works():
    # legacy label_fn returning (spk, ev) — margin None -> no fast path,
    # normal hold behavior intact
    h = EmissionHold(hold_s=5.0, margin_hi=3.0)
    turns = [(0.0, 2.0, 1)]
    assert h.offer(turns, 4.0, LBL, TXT) == []             # young
    out = h.offer(turns, 7.5, LBL, TXT)
    assert out == [Line(0.0, 2.0, 1, "[0.0-2.0]")]         # pinned identity

def test_every_gate_bounded_no_permanent_freeze():
    """The freeze regression test: frontier with NO label ever + text ready
    for later turns; with label_deadline set, the queue must fully drain
    (worst case hold+label_deadline per line) instead of freezing to flush."""
    h = EmissionHold(hold_s=5.0, label_deadline_s=12.0)
    none_lbl = lambda s, e: None
    turns = [(0.0, 2.0, 5), (3.0, 4.0, 6), (5.0, 6.0, 7)]
    released = []
    for now in range(6, 30):
        released += h.offer(turns, float(now), none_lbl, TXT)
    assert [l.speaker for l in released] == [5, 6, 7]      # all out, in order
    assert h.flush(none_lbl, TXT) == []                    # nothing left over


# ---- speaker-less local-geometry turns (2026-09-02 turn architecture:
# geometry carries spk=-1, identity late-bound) ----

def test_negative_pin_never_beats_label():
    h = EmissionHold(hold_s=5.0)
    turns = [(0.0, 2.0, -1)]                 # local-geometry turn
    assert h.offer(turns, 4.0, LBL, TXT) == []       # young, pinned -1
    out = h.offer(turns, 7.5, LBL, TXT)
    assert out == [Line(0.0, 2.0, 7, "[0.0-2.0]")]   # label wins over -1 pin

def test_label_deadline_drops_speakerless_turn():
    h = EmissionHold(hold_s=5.0, label_deadline_s=12.0)
    none_lbl = lambda s, e: None
    turns = [(0.0, 2.0, -1)]
    assert h.offer(turns, 15.0, none_lbl, TXT) == []
    out = h.offer(turns, 19.5, none_lbl, TXT)        # past deadline
    assert out == []                                 # dropped, not "Speaker -1"
    # and the span is consumed: later turns are not blocked
    out = h.offer([(0.0, 2.0, -1), (3.0, 4.0, -1)], 20.0, LBL, TXT)
    assert [l.speaker for l in out] == [7]           # next turn flows w/ label

def test_flush_never_emits_negative_speaker():
    h = EmissionHold(hold_s=5.0)
    none_lbl = lambda s, e: None
    h.offer([(0.0, 2.0, -1)], 4.0, none_lbl, TXT)    # held young
    assert h.flush(none_lbl, TXT) == []              # no label, no -1 line


# ---- safe-horizon watermark (2026-09-03: the ladder's coverage regression —
# fast releases slammed the watermark past turns the diarizer MINTS LATE
# (appear_lag 20-40s), which were then silently skipped at the loop top.
# With the frontier passed, watermark advancement is capped at
# frontier - safe_lag_s; late turns behind emitted lines still emit.) ----

def test_late_materializing_turn_admitted_behind_released_line():
    h = EmissionHold(hold_s=1.0, safe_lag_s=30.0)
    # a line releases at 30-40; frontier 41 -> watermark capped at 11, NOT 40
    out = h.offer([(30.0, 40.0, 1)], 41.5, LBL, TXT, frontier_s=41.0)
    assert [l.start for l in out] == [30.0]
    assert h._released_until == 11.0
    # a backchannel at 12-14 only MATERIALIZES now (appear_lag ~29.5s):
    # end 14 <= the old watermark 40 -> legacy skipped it silently. Fixed:
    # it emits when it appears (appended late; late beats lost).
    out = h.offer([(12.0, 14.0, 2), (30.0, 40.0, 1)], 43.5, LBL, TXT,
                  frontier_s=43.0)
    assert [(l.start, l.end) for l in out] == [(12.0, 14.0)]


def test_legacy_uncapped_watermark_skips_late_turn():
    # pin the PRE-FIX behavior when no frontier is passed (legacy callers):
    # exactly the regression the safe horizon exists to fix
    h = EmissionHold(hold_s=1.0, safe_lag_s=30.0)
    h.offer([(30.0, 40.0, 1)], 41.5, LBL, TXT)       # no frontier
    assert h._released_until == 40.0
    assert h.offer([(12.0, 14.0, 2)], 43.5, LBL, TXT) == []


def test_no_duplicate_reemission_ahead_of_capped_watermark():
    """An emitted line ahead of the capped watermark must never re-emit:
    exact re-derivations, grown copies and churn slivers all stay silent."""
    h = EmissionHold(hold_s=1.0, safe_lag_s=30.0)
    out = h.offer([(30.0, 40.0, 1)], 41.5, LBL, TXT, frontier_s=41.0)
    assert len(out) == 1
    # exact re-derivation
    assert h.offer([(30.0, 40.0, 1)], 43.5, LBL, TXT, frontier_s=43.0) == []
    # churn-widened copy: uncovered residue 0.2s < MIN_UNCOVERED_S -> churn
    assert h.offer([(29.8, 40.1, 1)], 45.5, LBL, TXT, frontier_s=45.0) == []
    # flush must not re-emit it either
    h.offer([(29.8, 40.1, 1)], 45.6, lambda s, e: None, TXT)  # park pending
    assert h.flush(LBL, TXT) == []


def test_safe_lag_zero_restores_uncapped_watermark():
    h = EmissionHold(hold_s=1.0, safe_lag_s=0.0)
    h.offer([(30.0, 40.0, 1)], 41.5, LBL, TXT, frontier_s=41.0)
    assert h._released_until == 40.0                 # min(end, frontier-0)


def test_beyond_lag_turn_logged_as_delegated_to_finalize():
    """Product call (2026-09-03): a turn materializing MORE than safe_lag_s
    late is delegated to finalize (I8), not shown live — but it must be
    COUNTABLE, never silent: one named debug line, deduped per turn."""
    logs = []
    h = EmissionHold(hold_s=1.0, safe_lag_s=30.0, debug_cb=logs.append)
    h.offer([(30.0, 40.0, 1)], 41.5, LBL, TXT, frontier_s=41.0)  # wm 11
    h.offer([(50.0, 60.0, 1)], 61.5, LBL, TXT, frontier_s=61.0)  # wm 31
    logs.clear()
    # ends at 20 but only materializes now (appear_lag 45 > lag 30)
    assert h.offer([(18.0, 20.0, 2)], 65.0, LBL, TXT,
                   frontier_s=65.0) == []
    assert any(l.startswith("late_lost_to_finalize") for l in logs)
    logs.clear()
    h.offer([(18.0, 20.0, 2)], 67.4, LBL, TXT, frontier_s=67.0)
    assert not any(l.startswith("late_lost_to_finalize") for l in logs), \
        "delegation log must be deduped per turn"
