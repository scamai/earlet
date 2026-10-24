# emission_hold.py
"""Single-emission hold buffer (2026-09-01 product decision): a line leaves
exactly once — complete (text + speaker) and immutable — after its turn tail
has aged hold_s (the D=H constant; 99.9%-tier label maturity). No draft
state, no relabels downstream of this buffer, ever."""
from __future__ import annotations
import os
from typing import Callable, NamedTuple, Optional


class Line(NamedTuple):
    start: float
    end: float
    speaker: int
    text: str


class EmissionHold:
    #: minimum uncovered residue worth its own line when the coverage clip
    #: (safe-horizon mode) trims a span against already-emitted intervals.
    #: Boundary churn re-derives an emitted turn a couple hundred ms wider;
    #: releasing that sliver would print the whole turn's text AGAIN
    #: (span text is keyed by the turn extent, not the clipped display span).
    #: Anything below the geometry's own min-turn floor (0.4s) is churn,
    #: not a missed backchannel.
    MIN_UNCOVERED_S = 0.3

    def __init__(self, hold_s: float, text_grace_s: float = 3.0,
                 text_deadline_s: float = 12.0, debug_cb=None,
                 label_deadline_s: Optional[float] = None,
                 margin_hi: Optional[float] = None,
                 margin_min_age_s: float = 3.0,
                 safe_lag_s: Optional[float] = None):
        self.hold_s = float(hold_s)
        #: SAFE-HORIZON WATERMARK (2026-09-03, the coverage fix for the
        #: ladder's late-turn regression). The confidence ladder made every
        #: gate bounded, so lines release fast — and every release used to
        #: advance `_released_until` to its end. But the diarizer MINTS turns
        #: LATE: short backchannels only appear in the sliding-window state
        #: 20-40s after their audio (appear_lag). Those late turns arrived
        #: with end <= _released_until and were silently skipped by offer()'s
        #: loop-top guard — the old unbounded waits had (accidentally) kept
        #: the watermark slow enough to admit them. Device proof: Config A on
        #: MTG_30860 = 75 live lines but miss 37.5 vs strict's 11.2.
        #: Fix: when the caller passes its diarizer frontier to offer(),
        #: `_released_until` advancement is CAPPED at
        #: frontier - safe_lag_s — the watermark never claims audio the
        #: diarizer may still mint turns in. Lines still RELEASE the moment
        #: their gates pass (no new waiting, every gate stays bounded);
        #: spans emitted ahead of the capped watermark are tracked in
        #: `_emitted_iv` so re-derived/grown copies of them can never emit
        #: twice. A late-materializing turn behind an emitted line releases
        #: when it appears — appended late, in release order (single
        #: emission forbids reordering the pane; a late line beats a lost
        #: line). Env: DICTATION_NOTES_SAFE_LAG_S (seconds; 0 restores the
        #: uncapped watermark). Callers that never pass a frontier are
        #: byte-for-byte legacy.
        if safe_lag_s is None:
            try:
                safe_lag_s = float(os.environ.get(
                    "DICTATION_NOTES_SAFE_LAG_S", "30.0"))
            except (TypeError, ValueError):
                safe_lag_s = 30.0
        #: Default 30 (a product call, 2026-09-03): the lag governs
        #: BACKFILL-line staleness, not normal-line latency; sim appear_lag
        #: p90 ~30.6s, so 30 covers ~90% of late materializers, and a line
        #: staler than 30s belongs to the finalize record, not the live
        #: pane. Beyond-lag turns are DELIBERATELY delegated to finalize
        #: (I8) — logged countable (late_lost_to_finalize), never lost
        #: silently.
        self.safe_lag_s = float(safe_lag_s)
        self._late_lost: set = set()     # late_lost_to_finalize log dedupe
        #: CONFIDENCE-BASED RELEASE (2026-09-02, the shipped fix for a
        #: 25-min live freeze). Two changes, both keeping
        #: strict chronological order, that make EVERY gate bounded so the
        #: in-order barrier can never freeze the pane (worst case per line
        #: = hold_s + text_deadline_s):
        #:
        #: margin fast path — label_fn's margin (winner/runner-up evidence
        #: ratio) >= margin_hi AND the turn is at least margin_min_age_s old
        #: => the label is credible NOW; release as soon as the span text
        #: arrives instead of waiting out the full hold. Replaces the pure
        #: time gamble ("9.6s old = mature") with a quality criterion.
        self.margin_hi = None if margin_hi is None else float(margin_hi)
        self.margin_min_age_s = float(margin_min_age_s)
        #: label deadline — the label-missing wait used to be UNBOUNDED
        #: ("wait / flush"), the direct cause of the total freeze: a frontier
        #: whose span never accumulates 0.4s of bank evidence blocked every
        #: later line until flush. Past hold_s + label_deadline_s the line
        #: releases with the turn's own diarizer speaker — exactly flush()'s
        #: existing fallback, moved up from meeting-end to the deadline.
        #: None keeps the legacy unbounded wait.
        self.label_deadline_s = (None if label_deadline_s is None
                                 else float(label_deadline_s))
        #: extra time a MATURED turn may wait for its span decode
        #: (turn-driven 1:1 text, 2026-09-01) before falling back to the
        #: carrier join. Decode of a 1-3 s span takes ~1 s, so the grace is
        #: rarely consumed; it only bounds a wedged decode worker.
        self.text_grace_s = float(text_grace_s)
        #: accuracy-first release (2026-09-02 product decision): a matured turn
        #: waits for its 1:1 text to ARRIVE, not a fixed grace — a complete
        #: line late beats an empty/placeholder line on time. The deadline is
        #: only a deadlock guard: a genuinely wedged decode (worker crashed)
        #: would otherwise stall every later line behind it forever, so past
        #: text_deadline_s the line is DROPPED — never emitted empty. Set to
        #: 12 s: well past any real span decode (~1-3 s, a few s under load)
        #: and ~one max-turn, so a real wedge clears fast instead of freezing
        #: the pane for 30 s. The final accurate pass backfills a dropped span.
        self.text_deadline_s = float(text_deadline_s)
        #: optional str sink for drop forensics (the wire passes _dbg): every
        #: path where a turn fails to become a line gets a named log line.
        self._debug_cb = debug_cb
        self._released_until = -1.0      # everything before this is emitted
        self._emitted_spans: set = set() # (round(start,2), round(end,2)) guard
        self._emitted_iv: list = []      # sorted merged [start,end) intervals
                                         # emitted AHEAD of the capped
                                         # watermark (safe-horizon mode);
                                         # empty in legacy (uncapped) mode
        self._pending: list = []         # held turns [(start, end, spk)],
                                         # latest extent per start; rebuilt
                                         # every offer() from unreleased turns

    def _note_emitted(self, start, end) -> None:
        """Record an emitted interval; keep the list sorted, merged, and
        pruned to intervals still ahead of the watermark (behind it the
        watermark itself is the guard)."""
        iv = self._emitted_iv
        iv.append((start, end))
        iv.sort()
        merged = []
        for a, b in iv:
            if b <= self._released_until + 1e-6:
                continue
            if merged and a <= merged[-1][1] + 1e-6:
                merged[-1] = (merged[-1][0], max(merged[-1][1], b))
            else:
                merged.append((a, b))
        self._emitted_iv = merged

    def _clip_uncovered(self, start, end):
        """First not-yet-emitted sub-span of [start, end): start advanced
        past the watermark and past any emitted interval covering it; end
        truncated at the next emitted interval ahead. (s >= e) = fully
        covered. Legacy mode (no cap ever passed) keeps _emitted_iv empty,
        so this reduces exactly to the old max(start, _released_until)."""
        s = max(start, self._released_until)
        e = end
        for a, b in self._emitted_iv:
            if b <= s + 1e-6:
                continue
            if a <= s + 1e-6:
                s = b               # covered here — resume after the line
                continue
            if a < e - 1e-6:
                e = a               # next emitted line ahead — stop before it
            break
        return s, e

    def _release(self, start, end, speaker, text_fn,
                 cap: Optional[float] = None) -> Optional[Line]:
        """Emit [start, end) as `speaker` exactly once; empty text consumes
        the span without producing a line. `cap` (safe horizon) bounds how
        far the watermark may advance — the emitted interval itself is
        remembered in _emitted_iv either way."""
        key = (round(start, 2), round(end, 2))
        if key in self._emitted_spans or end <= self._released_until + 1e-6:
            return None
        text = (text_fn(start, end) or "").strip()
        self._emitted_spans.add(key)
        wm = end if cap is None else min(end, cap)
        self._released_until = max(self._released_until, wm)
        self._note_emitted(start, end)
        if not text:
            return None
        return Line(round(start, 2), round(end, 2), int(speaker), text)

    def offer(self, turns, now_s: float,
              label_fn: Callable, text_fn: Callable,
              span_text_fn: Optional[Callable] = None,
              frontier_s: Optional[float] = None) -> list:
        # Speakers pinned while a turn was held: once a line waited out its
        # hold, it releases with the identity it was held under (immutable
        # single emission); only first-sight releases take the label_fn
        # override — the tests pin both behaviors.
        #
        # `span_text_fn(start, end)` (turn-driven decode, 2026-09-01): the
        # text born 1:1 with this turn — a str when the span decode is done,
        # None while it is still running. A matured turn waits for its text
        # up to text_grace_s past the hold; only then does it fall back to
        # the legacy carrier join (text_fn). None span_text_fn = legacy.
        # `frontier_s` = latest audio time the diarizer's state covers. The
        # watermark may not advance past frontier - safe_lag_s (see __init__:
        # safe-horizon watermark) so turns minted late still get in. None =
        # legacy uncapped watermark.
        cap = (None if frontier_s is None
               else max(0.0, float(frontier_s) - self.safe_lag_s))
        # a clipped residue smaller than this is boundary churn around an
        # already-emitted line, not a new utterance — skip it (releasing it
        # would re-print the turn's text). Legacy keeps the old epsilon.
        min_resid = 1e-6 if (cap is None and not self._emitted_iv) \
            else self.MIN_UNCOVERED_S
        pinned = {s_key: spk for s_key, (_, _, spk) in
                  ((round(s, 2), (s, e, spk)) for s, e, spk in self._pending)}
        out, still = [], {}

        def _hold(start, end, spk):
            k = round(start, 2)
            prev = still.get(k)
            if prev is None or end > prev[1]:     # latest extent per start
                still[k] = (start, end, spk)

        blocked = False   # in-order release (2026-09-02): once any earlier
        # turn is still waiting (young / label / text), every later turn
        # queues behind it. Without this, a later turn whose text arrived
        # first released ahead and slammed the global watermark past the
        # waiting turn — which was then silently skipped when it matured
        # (device forensics: 74.7s of whole-turn holes, miss ~10). Ordered
        # release also means lines reach the pane chronologically. The
        # barrier is safe ONLY because every frontier gate below is bounded
        # (label deadline + text deadline): the worst case is a slow line,
        # never a frozen pane.
        for start, end, spk in sorted(turns):
            cs, ce = self._clip_uncovered(start, end)
            if ce - cs <= min_resid:
                # already emitted — or a beyond-lag late materialization.
                # The latter is DELIBERATELY delegated to the finalize
                # record (I8: live drops are backfilled, never erased);
                # named + deduped so soak logs can count the delegation
                # rate and tune DICTATION_NOTES_SAFE_LAG_S.
                if (self._debug_cb and cap is not None
                        and end <= self._released_until + 1e-6):
                    k = (round(start, 1), round(end, 1))
                    if (k not in self._late_lost
                            and (round(start, 2), round(end, 2))
                            not in self._emitted_spans):
                        self._late_lost.add(k)
                        self._debug_cb(
                            f"late_lost_to_finalize: {start:.1f}-{end:.1f} "
                            f"wm={self._released_until:.1f}")
                continue
            s_key = round(start, 2)
            if blocked:
                _hold(start, end, spk)        # queued behind an earlier waiter
                continue
            age = now_s - end
            lab = label_fn(start, end) if label_fn else None
            margin = None
            if lab is not None and len(lab) > 2 and lab[2] is not None:
                margin = float(lab[2])
            # confidence fast path (2026-09-02): a decisively-won label
            # (margin >= hi) on a minimally-aged turn is credible NOW —
            # skip the rest of the 9.6s time gamble. Text gates below
            # still apply (never emit without words).
            fast = (self.margin_hi is not None and margin is not None
                    and margin >= self.margin_hi
                    and age >= self.margin_min_age_s)
            if age < self.hold_s and not fast:
                _hold(start, end, spk)        # still young — hold
                blocked = True
                continue
            if label_fn is not None and lab is None:
                if (self.label_deadline_s is None
                        or age < self.hold_s + self.label_deadline_s):
                    # AUTHORITATIVE-EMPTY PROPAGATION (DEFECT-1 fix,
                    # 2026-09-03): the empty check precedes the label wait.
                    # A span whose decode is READY and EMPTY can never emit
                    # a line (I2), so no label is ever needed — waiting out
                    # hold+label_deadline here (21.6s shipped) only squatted
                    # at the head of the in-order queue, blocking every
                    # later line ~12s longer than spec'd (proof pin S3x).
                    # Consume at hold maturity, named + countable, exactly
                    # like the label-present empty release below. Only a
                    # READY-empty label-less turn takes this branch; every
                    # other wait (young / label / text) is byte-identical.
                    if (span_text_fn is not None
                            and span_text_fn(start, end) == ""):
                        if self._debug_cb:
                            self._debug_cb(f"hold_drop: EMPTY_RELEASE "
                                           f"{start:.1f}-{end:.1f}")
                        self._release(cs, ce, 0, lambda _a, _b: "",
                                      cap)               # consume span
                        continue
                    _hold(start, end, spk)    # no evidence yet — bounded wait
                    blocked = True
                    continue
                # LABEL DEADLINE: the bank never accumulated evidence for
                # this span (short overlap turns routinely can't reach the
                # 0.4s floor) — the old unbounded "wait / flush" here was
                # the direct cause of the 25-min total freeze. Release with
                # the turn's own diarizer speaker — flush()'s existing
                # fallback, moved up from meeting-end to the deadline.
                fb = pinned.get(s_key, int(spk))
                if fb < 0:
                    # local-geometry turns carry no speaker (-1) — with no
                    # label AND no fallback identity there is nothing honest
                    # to print. Drop; the finalize backfills the span.
                    if self._debug_cb:
                        self._debug_cb(f"hold_drop: LABEL_DEADLINE "
                                       f"{start:.1f}-{end:.1f} no fallback id")
                    self._release(cs, ce, 0, lambda _a, _b: "",
                                  cap)                    # consume span
                    continue
                if self._debug_cb:
                    self._debug_cb(
                        f"label_deadline: {start:.1f}-{end:.1f} "
                        f"waited={age - self.hold_s:.1f}s -> turn-spk fallback")
                speaker = fb
                lab = (int(fb),)              # fallback identity for release
            else:
                _pin = pinned.get(round(start, 2))
                if _pin is not None and int(_pin) >= 0:
                    speaker = int(_pin)
                else:
                    # negative pin = speaker-less local-geometry turn held
                    # before its label arrived — identity is late-bound, so
                    # the label wins (never print "Speaker -1")
                    speaker = int(lab[0]) if lab else int(spk)
            use_text_fn = text_fn
            if span_text_fn is not None:
                span_text = span_text_fn(start, end)
                if span_text is None:
                    if now_s - end < self.hold_s + self.text_deadline_s:
                        # accuracy-first: WAIT for the real text to arrive
                        # (a complete line late beats an empty line on time),
                        # carrying the MATURED label so identity can't drift
                        # while the text lags. Under load the decode may take
                        # several seconds; we hold the whole time.
                        _hold(start, end, speaker)
                        blocked = True
                        continue
                    # deadlock guard only: decode is genuinely wedged (worker
                    # died) — DROP this line so later lines aren't stalled
                    # behind it forever. Never emit empty. `_release` with the
                    # (usually empty) carrier consumes the span; the final
                    # accurate pass backfills it in the saved record.
                    if self._debug_cb:
                        self._debug_cb(f"hold_drop: DEADLINE {start:.1f}-{end:.1f}"
                                       f" waited={now_s-end:.1f}s")
                else:
                    use_text_fn = (lambda _a, _b, _t=span_text: _t)
            # re-clip: earlier releases in THIS pass may have advanced the
            # watermark / emitted intervals since the loop-top check
            cs, ce = self._clip_uncovered(start, end)
            if ce - cs <= min_resid:
                continue
            ln = self._release(cs, ce, speaker, use_text_fn, cap)
            if ln:
                out.append(ln)
                if self._debug_cb:
                    # margin forensics: every released line records its label
                    # margin + whether the fast path fired, so soak logs can
                    # quantify photo-finish labels and tune margin_hi.
                    self._debug_cb(
                        f"released: {start:.1f}-{end:.1f} spk={speaker} "
                        f"age={age:.1f}s margin="
                        f"{'-' if margin is None else f'{margin:.1f}'}"
                        f"{' FAST' if fast else ''}")
            elif self._debug_cb and end > self._released_until - 1e-6:
                self._debug_cb(f"hold_drop: EMPTY_RELEASE {start:.1f}-{end:.1f}")
        self._pending = sorted(still.values())
        return out

    def flush(self, label_fn: Callable, text_fn: Callable,
              span_text_fn: Optional[Callable] = None) -> list:
        """Stream finish: release everything still pending, label evidence or
        not (turn speaker as fallback) — the meeting is over, nothing will
        mature further. A finished span decode is used when already
        available; a still-pending one cannot be waited for (carrier join)."""
        out = []
        min_resid = 1e-6 if not self._emitted_iv else self.MIN_UNCOVERED_S
        for start, end, spk in sorted(self._pending):
            cs, ce = self._clip_uncovered(start, end)
            if ce - cs <= min_resid:
                continue              # already emitted ahead of the watermark
            lab = label_fn(start, end) if label_fn else None
            speaker = int(lab[0]) if lab else int(spk)
            if speaker < 0:
                # speaker-less geometry with no label even at flush: nothing
                # honest to print; the finalize owns the span
                self._release(cs, ce, 0, lambda _a, _b: "")
                continue
            use_text_fn = text_fn
            if span_text_fn is not None:
                span_text = span_text_fn(start, end)
                if span_text is not None:
                    use_text_fn = (lambda _a, _b, _t=span_text: _t)
            ln = self._release(cs, ce, speaker, use_text_fn)
            if ln:
                out.append(ln)
            elif self._debug_cb:
                self._debug_cb(f"hold_drop: FLUSH_EMPTY {start:.1f}-{end:.1f}"
                               f" span_ready={span_text is not None if span_text_fn else 'n/a'}")
        self._pending = []
        return out
