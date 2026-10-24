# turn_book.py
"""Persistent turn objects for the 2026-09-02 turn architecture.

The Geometry Layer cuts turns from LOCAL segmentation activity
(neural_turns.turns_from_activity — state.bins only, never the global
clustering), and this book makes them OBJECTS: minted once when their local
geometry proves stable, allowed to grow only at the open frontier, then
FROZEN — immutable forever after. Downstream (decode keys, the emission
hold) therefore sees a stable geometry stream instead of a per-tick
re-derivation that global recluster churn could reshape (the measured
15-56s split/merge tail, 20-40s late materialization).

Identity is deliberately absent here: every turn carries speaker -1.
Identity is late-bound at emission by the confidence ladder
(margin fast path / bounded hold / label deadline).
"""
from __future__ import annotations

#: two consecutive consistent sightings mint a turn (matches the wire's
#: existing settle intuition, now applied to LOCAL cuts that cannot be
#: reshaped by the global clusterer)
MINT_SIGHTINGS = 2
#: both edges must repeat within this to count as "the same cut"
EDGE_TOL_S = 0.3
#: a cut whose end is at least this far behind the covered frontier is
#: INTERIOR — nothing to its right can extend it, freeze immediately on mint
#: (same constant family as the wire's FRONTIER_GUARD)
INTERIOR_GUARD_S = 2.5


class Turn:
    __slots__ = ("tid", "start", "end", "frozen", "born_s")

    def __init__(self, tid: int, start: float, end: float,
                 frozen: bool, born_s: float):
        self.tid = tid
        self.start = start
        self.end = end
        self.frozen = frozen
        self.born_s = born_s

    def as_tuple(self):
        return (self.start, self.end, -1)   # identity is late-bound


class TurnBook:
    def __init__(self):
        self._turns: list[Turn] = []        # sorted by start
        self._cand: dict = {}               # start_key -> [start, end, count]
        self._next_id = 1

    # -- helpers ---------------------------------------------------------
    @staticmethod
    def _same_cut(a_s, a_e, b_s, b_e) -> bool:
        return (abs(a_s - b_s) <= EDGE_TOL_S
                and abs(a_e - b_e) <= EDGE_TOL_S)

    @staticmethod
    def _overlap(a_s, a_e, b_s, b_e) -> float:
        return max(0.0, min(a_e, b_e) - max(a_s, b_s))

    # -- the per-tick step ----------------------------------------------
    MAX_TURN_S = 12.0   # same ceiling as turns_from_* — a grown frontier
    #                       turn splits at this length instead of ballooning
    #                       (field bug: 34.9s monster spans, 2026-09-02)

    def observe(self, cuts, frontier_s: float, now_s: float):
        """Feed one tick's local-geometry cuts [(s, e, _)]. frontier_s =
        latest audio time the segmentation has covered. Returns the current
        turn list [(start, end, -1)] — frozen geometry plus the still-open
        frontier turn, ready for decode + the emission hold."""
        cuts = sorted((float(s), float(e)) for s, e, *_ in cuts)
        # 1. grow/refresh OPEN turns from the cut that continues them
        for t in self._turns:
            if t.frozen:
                continue
            best, best_ov = None, 0.0
            for s, e in cuts:
                ov = self._overlap(t.start, t.end, s, e)
                if ov > best_ov:
                    best, best_ov = (s, e), ov
            if best is not None and best_ov > 0.0:
                # start is already minted (immutable); only the end may move,
                # and only FORWARD — geometry never shrinks or re-cuts
                if best[1] > t.end:
                    t.end = min(best[1], t.start + self.MAX_TURN_S)
                    if t.end >= t.start + self.MAX_TURN_S:
                        # ceiling reached: freeze this span; the remainder
                        # re-enters as a fresh candidate cut next tick
                        t.frozen = True
            # freeze once the end has fallen safely behind the frontier:
            # nothing to the right can extend it any more
            if t.end <= frontier_s - INTERIOR_GUARD_S:
                t.frozen = True
        # 2. candidate tracking for NOT-yet-minted regions
        minted_until = max((t.end for t in self._turns), default=-1.0)
        seen_keys = set()
        for s, e in cuts:
            if e <= minted_until + 1e-6:
                continue                    # already represented in the book
            if any(not t.frozen and self._overlap(t.start, t.end, s, e) > 0
                   for t in self._turns):
                continue                    # extends an open turn (handled)
            key = round(s, 1)
            seen_keys.add(key)
            c = self._cand.get(key)
            if c is not None and self._same_cut(c[0], c[1], s, e):
                c[2] += 1
            elif c is not None and abs(c[0] - s) <= EDGE_TOL_S and e > c[1]:
                # same start, end still growing: a live frontier cut —
                # keep counting sightings of the START edge, track the end
                c[1] = e
                c[2] += 1
            else:
                self._cand[key] = [s, e, 1]
                continue
            if c[2] >= MINT_SIGHTINGS:
                interior = e <= frontier_s - INTERIOR_GUARD_S
                self._turns.append(Turn(self._next_id, c[0], c[1],
                                        frozen=interior, born_s=now_s))
                self._next_id += 1
                del self._cand[key]
        # candidates not seen this tick were transient — drop them
        for key in [k for k in self._cand if k not in seen_keys]:
            del self._cand[key]
        self._turns.sort(key=lambda t: t.start)
        return [t.as_tuple() for t in self._turns]

    def prune(self, released_until: float) -> None:
        """Forget frozen turns fully behind the emission watermark —
        bounded memory over long meetings."""
        self._turns = [t for t in self._turns
                       if not (t.frozen and t.end <= released_until + 1e-6)]


class TurnBookV2:
    """ADOPTED 2026-09-03 (sim-validated: DER 28.9->21.7,
    conf 20.7->8.4 vs V1 on MTG_30860; miss 8.2->13.3 is the open tuning
    item in the Experiment Matrix). Mint criterion = BOUNDARY-POINT stability
    (tolerance-matched cut-point sightings), immune to the span-edge drift
    that starved V1 minting at fine cut granularity."""
    """Boundary-stability minting: cut POINTS (not spans) accumulate
    sightings across ticks (tolerance-matched); spans between consecutive
    stable boundaries mint frozen. Robust to per-tick span reshuffle.

    COVERAGE FIX (2026-09-03, defect B — sim miss 13.3 vs 8.2 for the v1
    book, device full-8 miss 25.3). Three leaks closed, invariants kept
    (mint-once immutable geometry, minted_until grows forward only, 12 s
    ceiling, stable boundaries as the mint skeleton):
    1. minting is ANCHORED at minted_until instead of pairing raw stable
       boundaries — a boundary that stabilized late, BEHIND minted_until,
       used to poison its pair (`b1 < minted_until -> continue`) and stall
       minted_until forever; and the region before the FIRST stable
       boundary (early meeting) was never mintable at all.
    2. a decided region mints its voiced SUBSPANS (intersection with this
       tick's merged cut intervals) instead of the all-or-nothing >=50%
       voiced() gate, which silently discarded true speech whenever a long
       silence sat inside a region (silence-gap interiors never become
       boundaries, so gaps absorbed their neighbors' speech).
    3. frontier coverage is UNCONDITIONAL (was gated on `stable` being
       non-empty — early meeting / post-gap audio had no offer) and
       chunked at MAXT (was a single span capped at minted_until+MAXT, so
       any mint stall left everything past 12 s uncovered)."""
    TOL = 0.4; STABLE = 2; GUARD = 2.5; MAXT = 12.0; MINT = 0.4
    def __init__(self):
        self.bnd = []          # [pos, sightings]
        self.minted_until = 0.0
        self.turns = []        # frozen (s,e,-1)
    def observe(self, cuts, frontier, now):
        # Minted geometry is immutable. Full-history activity snapshots must
        # not re-match or recreate boundaries that can no longer affect it.
        cuts = [cut for cut in cuts if float(cut[1]) > self.minted_until + 1e-6]
        oldest_edge = self.minted_until - self.TOL
        self.bnd = [b for b in self.bnd if b[0] >= oldest_edge]
        edges = set()
        for s, e, *_ in cuts:
            edges.update(round(float(p), 2) for p in (s, e) if float(p) >= oldest_edge)
        for p in sorted(edges):
            hit = None
            for b in self.bnd:
                if abs(b[0] - p) <= self.TOL:
                    hit = b; break
            if hit is not None:
                hit[0] = 0.5 * (hit[0] + p); hit[1] += 1
            else:
                self.bnd.append([p, 1])
        self.bnd.sort()
        stable = [b[0] for b in self.bnd if b[1] >= self.STABLE
                  and b[0] <= frontier - self.GUARD]
        # merged voiced intervals from this tick's cuts (defect-B fix 2)
        iv = []
        for s, e in sorted((float(s), float(e)) for s, e, *_ in cuts):
            if iv and s <= iv[-1][1] + 1e-6:
                iv[-1][1] = max(iv[-1][1], e)
            else:
                iv.append([s, e])
        # mint every region decided by a stable boundary, anchored at
        # minted_until (defect-B fix 1): [minted_until, b_next) is decided
        # the moment b_next stabilizes; only its voiced subspans mint.
        anchors = [self.minted_until] + [b for b in stable
                                         if b > self.minted_until + 1e-6]
        for b1, b2 in zip(anchors, anchors[1:]):
            a = max(b1, self.minted_until)
            if b2 <= a + 1e-6:
                continue
            for s, e in iv:
                lo, hi = max(a, s), min(b2, e)
                if hi - lo < self.MINT:
                    continue
                while hi - lo > self.MAXT:
                    self.turns.append((lo, lo + self.MAXT, -1))
                    lo += self.MAXT
                self.turns.append((lo, hi, -1))
            self.minted_until = b2
        out = list(self.turns)
        # open frontier coverage (for hold offer; not frozen; defect-B fix 3)
        if iv:
            last_end = iv[-1][1]
            a = self.minted_until
            while last_end > a + self.MINT:
                out.append((a, min(last_end, a + self.MAXT), -1))
                a += self.MAXT
        return out
    def prune(self, until):
        self.turns = [t for t in self.turns if t[1] > until + 1e-6]
        self.bnd = [b for b in self.bnd if b[0] >= self.minted_until - self.TOL]
