# neural_turns.py
"""Turn boundaries from the bank's matured neural segmentation (single-
emission architecture, 2026-09-01): per-frame dominant OUTPUT speaker by
overlap-vote across banked windows, then cut turns on speaker change or
silence. Replaces the energy TurnTracker as the LINE splitter (the energy
path's only advantage was sub-second latency, which the emission hold
removed the need for)."""
from __future__ import annotations
import numpy as np


def dominant_frames(state, sr: int = 16000):
    """(grid, step_s): grid[i] = dominant output-speaker id of absolute frame
    i (id from state.assign's output space, -1 = silence/none), step_s =
    state.frame_dur. Votes: every banked window's active (frame, track) with
    assign >= 0 adds 1 to (abs_frame, speaker); majority wins per frame."""
    if not state.bins:
        return np.zeros(0, dtype=np.int32), float(state.frame_dur or 0.02)
    step = float(state.frame_dur)
    # Size the vote grid from every window's actual extent: robust to
    # unsorted starts and to a window whose bin has != num_frames rows.
    total = 0
    for st, b in zip(state.starts, state.bins):
        f0 = int(round(st / sr / step))
        total = max(total, f0 + b.shape[0])
    n_out = int(getattr(state, "n_out", 0) or 0)
    votes = np.zeros((total, max(n_out, 1)), dtype=np.int32)
    for w, (st, b) in enumerate(zip(state.starts, state.bins)):
        f0 = int(round(st / sr / step))
        amap = state.assign[w]
        for s in range(b.shape[1]):
            out = int(amap[s])
            if out < 0 or out >= votes.shape[1]:
                continue
            idx = np.nonzero(b[:, s])[0]
            votes[f0 + idx, out] += 1
    grid = np.where(votes.sum(axis=1) > 0,
                    votes.argmax(axis=1), -1).astype(np.int32)
    return grid, step


def turns_from_state(state, sr: int = 16000, min_gap_s: float = 0.6,
                     min_turn_s: float = 0.4, max_turn_s: float = 12.0):
    """[(start_s, end_s, speaker_idx)] — maximal same-speaker runs, closed by
    a speaker CHANGE or a silence run >= min_gap_s; runs longer than
    max_turn_s are hard-split; runs shorter than min_turn_s are dropped.
    Turns are sorted, non-overlapping, speaker-pure; trailing silence belongs
    to nobody (a run always ends right after its last speech frame)."""
    grid, step = dominant_frames(state, sr)
    gap_frames = max(1, int(round(min_gap_s / step)))
    raw = []
    i, n = 0, len(grid)
    while i < n:
        if grid[i] < 0:
            i += 1
            continue
        spk = int(grid[i])
        j = i
        sil = 0
        last_speech = i
        while j < n:
            if grid[j] == spk:
                sil = 0
                last_speech = j
            elif grid[j] < 0:
                sil += 1
                if sil >= gap_frames:
                    break
            else:
                break
            j += 1
        end = last_speech + 1          # exclusive; never claims silence
        s_s, e_s = i * step, end * step
        # keep short runs too for now; the post-pass below merges the ones
        # that are a same-speaker turn briefly interrupted, and only THEN
        # drops the genuinely-spurious sub-min_turn flickers.
        raw.append((s_s, e_s, spk))
        i = j if j > i else i + 1
    return _stitch_and_filter(raw, min_gap_s, min_turn_s, max_turn_s)


def _stitch_and_filter(raw, min_gap_s, min_turn_s, max_turn_s):
    """Post-pass (2026-09-02): stitch a same-speaker turn that was split by a
    brief interruption, THEN drop genuine sub-min_turn flickers, THEN hard-
    split over-long runs. Dropping short runs BEFORE stitching punched holes
    into continuous speech — one speaker's run interrupted by a 0.6 s
    backchannel (or a few overlap-garbled frames) reads as [longA][gap][shortA]
    and the shortA was discarded, leaving [gap][hole] uncovered (device
    forensics: 30862 Ernie 216-219, 30865 Olivia 6 s; clean-meeting solo-miss
    8-9). Merge across a gap only when the SAME speaker resumes within
    min_gap_s and nobody else's kept turn occupies the gap."""
    if not raw:
        return []
    raw.sort()
    # Bridge to the most-recent SAME-speaker turn (not just the immediate
    # predecessor): an interruption sits [A_long][B_short][A_short] in start
    # order, so A_short's neighbor is B — we must reach back past B to the A
    # it continues. Only bridge if the gap to that A is < min_gap_s AND the
    # interposed other-speaker turn is itself short (a backchannel), so we
    # never swallow a real exchange.
    # Bridge a same-speaker turn split by a brief interruption. Guard tightly
    # so rapid A/B alternation (all pieces short) is NOT stitched — only a
    # SUBSTANTIAL turn (>= min_turn) that resumes after a short interposed
    # backchannel qualifies. Conditions to bridge current run onto prior tgt:
    #   - same speaker, gap <= min_gap_s
    #   - the target turn is already substantial (>= min_turn_s): it's a real
    #     turn being continued, not a flicker
    #   - everything interposed since tgt is short (< min_turn_s): a
    #     backchannel, not a real exchange turn
    #   - AND the combined span stays continuous (we only extend the end)
    merged = []
    for _ri, (s_s, e_s, spk) in enumerate(raw):
        tgt = None
        for k in range(len(merged) - 1, -1, -1):
            if merged[k][2] == spk:
                tgt = k
                break
            if merged[k][1] < s_s - min_gap_s:     # nothing recent enough left
                break
        tgt_substantial = (tgt is not None
                           and merged[tgt][1] - merged[tgt][0] >= min_turn_s)
        interposed_all_short = tgt is not None and all(
            merged[m][1] - merged[m][0] <= min_turn_s + 1e-6
            for m in range(tgt + 1, len(merged)))
        # SINGLE-interruption guard (2026-09-02, half-B verify): on heavy-
        # overlap meetings dominant-frame flicker strings MULTIPLE short
        # other-speaker pieces between two same-speaker runs — those pieces
        # are the other speaker's REAL speech, and bridging across them
        # relabels it (30880: miss -5.9 but conf +7.8). An isolated
        # backchannel (the clean-meeting hole case) is exactly ONE piece.
        # Bridge only across a single interposed piece.
        interposed_single = tgt is not None and (len(merged) - (tgt + 1)) <= 1
        # look-ahead: if the resumed piece is itself immediately followed by
        # ANOTHER short other-speaker piece, the flicker chain is continuing
        # — the first hop of a chain looks like an isolated backchannel at
        # processing time, so peek one piece ahead before bridging.
        _nxt = raw[_ri + 1] if _ri + 1 < len(raw) else None
        chain_continuing = (_nxt is not None and _nxt[2] != spk
                            and _nxt[0] - e_s <= min_gap_s + 1e-6
                            and _nxt[1] - _nxt[0] <= min_turn_s + 1e-6)
        if (tgt is not None and s_s - merged[tgt][1] <= min_gap_s + 1e-6
                and tgt_substantial and interposed_all_short
                and interposed_single and not chain_continuing):
            merged[tgt][1] = max(merged[tgt][1], e_s)   # bridge same speaker
        else:
            merged.append([s_s, e_s, spk])
    merged.sort()
    out = []
    for s_s, e_s, spk in merged:
        if e_s - s_s < min_turn_s:
            continue                              # genuine flicker: drop
        while e_s - s_s > max_turn_s:
            out.append((s_s, s_s + max_turn_s, spk))
            s_s += max_turn_s
        out.append((s_s, e_s, spk))
    return out


def turns_from_activity(state, sr: int = 16000, min_gap_s: float = 0.6,
                        min_turn_s: float = 0.4, max_turn_s: float = 12.0,
                        act_frac: float = 0.5, change_vote: float = None):
    """GEOMETRY-LAYER PROTOTYPE (2026-09-02 turn architecture): turns cut
    from LOCAL segmentation activity ONLY — state.bins, never state.assign —
    so boundaries are immune to global recluster churn (the measured 15-56s
    split/merge tail all came through assign flips).

    Speaker ids in the result are always -1: identity is late-bound at
    emission (margin ladder), never part of geometry.

    Cuts: (a) silence gaps >= min_gap_s on the majority-activity grid;
    (b) speaker-CHANGE points, voted by each covering window from its
    INTERNAL slot-set transitions. Transition positions are slot-permutation
    invariant across windows, so no cross-window identity is needed; votes
    are edge-weighted (a window trusts transitions near its center — its
    edge opinions are the unreliable ones, per the churn forensics).
    gap/min/max post-rules match turns_from_state; the same-speaker stitch
    is skipped (needs identity, which geometry no longer has).
    """
    if change_vote is None:
        import os
        try:
            change_vote = float(os.environ.get("DICTATION_NOTES_CHANGE_VOTE", "1.2"))
        except (TypeError, ValueError):
            change_vote = 1.2
    if not state.bins:
        return []
    step = float(state.frame_dur)
    total = 0
    for st, b in zip(state.starts, state.bins):
        f0 = int(round(st / sr / step))
        total = max(total, f0 + b.shape[0])
    act = np.zeros(total)
    cov = np.zeros(total)
    chg = np.zeros(total)
    for st, b in zip(state.starts, state.bins):
        f0 = int(round(st / sr / step))
        nf = b.shape[0]
        if nf < 2:
            continue
        cov[f0:f0 + nf] += 1.0
        sets = (np.asarray(b) > 0)
        any_act = sets.any(axis=1)
        act[f0:f0 + nf] += any_act
        moved = (sets[1:] != sets[:-1]).any(axis=1) & any_act[1:] & any_act[:-1]
        if moved.any():
            f_idx = np.nonzero(moved)[0] + 1
            w = np.minimum(f_idx, nf - f_idx) / float(nf)   # 0 edge → .5 center
            np.add.at(chg, f0 + f_idx, w)
    speech = np.divide(act, cov, out=np.zeros_like(act), where=cov > 0) >= act_frac
    gap_frames = max(1, int(round(min_gap_s / step)))
    turns = []
    i, n = 0, len(speech)
    while i < n:
        if not speech[i]:
            i += 1
            continue
        j = i
        sil = 0
        last_speech = i
        while j < n:
            if speech[j]:
                sil = 0
                last_speech = j
            else:
                sil += 1
                if sil >= gap_frames:
                    break
            j += 1
        seg_a, seg_b = i, last_speech + 1
        # split the run at strong change points (interior only)
        cuts = [f for f in range(seg_a + 1, seg_b)
                if chg[f] >= change_vote]
        pts = [seg_a] + cuts + [seg_b]
        for a, bnd in zip(pts, pts[1:]):
            s_s, e_s = a * step, bnd * step
            dur = e_s - s_s
            if dur < min_turn_s:
                continue
            while dur > max_turn_s:                 # hard-split long runs
                turns.append((s_s, s_s + max_turn_s, -1))
                s_s += max_turn_s
                dur = e_s - s_s
            if dur >= min_turn_s:
                turns.append((s_s, e_s, -1))
        i = j if j > i else i + 1
    return turns
