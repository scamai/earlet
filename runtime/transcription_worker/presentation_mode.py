"""PRESENTATION MODE — env-gated fallback for scripted stage demos.

Worst case for the Pokee AI on-stage demo: their agent consumes our live
committed-transcript feed, and when the demo content is SCRIPTED we can snap
committed lines to the known script instead of hoping live ASR is perfect.

Enable with::

    DICTATION_NOTES_PRESENTATION_SCRIPT=/path/to/script.txt   # UTF-8 text
    DICTATION_NOTES_PRESENTATION_SNAP_T=0.4                   # optional threshold

Unset / empty / file missing  =>  ZERO behavior change (a missing file logs
once through the worker's ``_dbg`` and is otherwise ignored — it must never
crash the worker).

When set, two things happen:

(a) BIAS FEEDING — at session start, vocabulary/phrases are extracted from
    the script (proper-noun runs, distinctive terms; deduped, first-
    appearance order) and APPENDED after the caller's ``bias_phrases``.
    Order is deliberate: the greedy path's ``_vocab_prompt`` keeps a
    front-prefix under a 320-char budget, so caller-provided vocab (meeting
    attendee names etc.) keeps its prompt priority and can never be evicted
    by script terms; script phrases that overflow the prompt budget still
    reach the beam bias trie and the phonetic-recovery layers in full —
    neither has a token budget.

    CRITICAL interaction: the commit log's regurgitation guards
    (``is_vocab_echo`` — drops a line whose word pieces are >=90% covered
    by the vocab blob — and ``hallucination.strip_vocab_run``) treat vocab
    words in committed text as prompt echo. In presentation mode the user
    SPEAKS the script, so committed lines legitimately consist of script
    vocabulary — feeding script phrases into those guards makes them eat
    the very lines we want (verified by test: a spoken script line IS
    dropped when the guards see the merged list). Therefore
    ``PresentationSession.attach`` restores the guard sets
    (``vocab_norm`` / ``vocab_phrases``) to the CALLER phrases only;
    script phrases bypass the guards entirely.

(b) SCRIPT-ALIGNMENT SNAP — each committed line (after the existing
    cleanup passes in ``_CommitLog.add``) is fuzzy-matched against the
    script with a word-level LOCAL alignment (lowercase, punctuation
    stripped): a semi-global DP finds the best-matching contiguous window
    of script words for the line (the line may cover part of a sentence or
    span two — the alignment is free to start/end anywhere in the script).
    If the normalized edit distance over that window is <= the threshold,
    the line's text is REPLACED with the window's EXACT original wording
    (casing + punctuation from the script file). Above the threshold the
    ASR text is left completely untouched — free-form Q&A still works.

    Degenerate-snap guards: lines shorter than ``MIN_SNAP_WORDS`` words
    ("okay", "yeah") never snap — they would false-match anywhere.
    The matcher is deliberately STATELESS (no monotonic cursor): a
    presenter usually moves forward, but Q&A jumps around, and a stateless
    best-match handles both; the threshold + min-length guard prevent one
    script sentence from being snapped to repeatedly by unrelated lines
    (an unrelated line has distance > threshold by construction, and short
    interjections are excluded outright). Snapped-vs-untouched counts are
    tracked per session and logged through ``_dbg`` so replays can report
    them.

(c) CONSECUTIVE-REPEAT SUPPRESSION — because (a) deliberately blinds the
    regurgitation guards to script phrases, a low-signal/silence decode
    loop that keeps hallucinating the SAME script line used to commit it
    4+ times (stage-visible, on-device 2026-08-27). ``suppress_repeat``
    drops a commit that is a near-duplicate (exact normalized match or
    SequenceMatcher ratio >= 0.9) of the SAME channel's previous commit
    within ``DICTATION_NOTES_PRESENTATION_REPEAT_GAP_S`` seconds (default 10) —
    but ONLY when the line is script-like (>= ``MIN_SNAP_WORDS`` words and
    best-window distance within the snap threshold): short interjections
    ("okay", "yeah") and off-script Q&A always commit, even repeated. A
    suppressed echo refreshes the timer, so a sustained loop stays fully
    suppressed until a genuinely different line intervenes or the loop
    pauses longer than the gap. Presentation mode off => the hook is never
    reached => zero behavior change.

Hook surface in ``ops_stream_whisper`` (kept tiny on purpose):
``start()`` builds the session object from the env and merges bias;
``_CommitLog`` carries a ``presentation`` attribute (default ``None``) and
``add()`` calls ``presentation.snap_line`` right before appending, then
drops the line when ``presentation.suppress_repeat(channel, text)`` says
it is a consecutive script-line echo.
"""

from __future__ import annotations

import os
import re
import time

ENV_SCRIPT = "DICTATION_NOTES_PRESENTATION_SCRIPT"
ENV_SNAP_T = "DICTATION_NOTES_PRESENTATION_SNAP_T"
ENV_REPEAT_GAP = "DICTATION_NOTES_PRESENTATION_REPEAT_GAP_S"
DEFAULT_SNAP_T = 0.4
#: A near-duplicate of the SAME channel's previous commit within this many
#: seconds is a decode-loop echo, not a re-utterance (suppression refreshes
#: the clock, so the gap is measured from the LAST echo, not the commit).
DEFAULT_REPEAT_GAP_S = 10.0
#: Normalized-text similarity at or above this = the same line (echoes
#: wobble by a word; unrelated lines land far below).
REPEAT_SIM = 0.9

#: Lines with fewer normalized words than this never snap ("okay", "yeah",
#: "thank you" would false-match somewhere in almost any script).
MIN_SNAP_WORDS = 3

#: Upper bound on phrases extracted from a script — the beam trie and the
#: phonetic layers scale fine, but an unbounded list is never useful and
#: the prompt budget only ever sees a front-prefix anyway.
MAX_SCRIPT_PHRASES = 48

_WORD_RE = re.compile(r"[a-z0-9']+")

#: Sentence-initial words like these are capitalized without being names —
#: never extract them as "proper nouns" on their own.
_STOPWORDS = frozenset("""
a an and are as at be but by for from has have i if in into is it its of on
or our so that the their then there these they this to was we were what when
which who will with you your not now
""".split())


def _dbg(msg: str) -> None:
    """Route through the worker's debug trail (DICTATION_NOTES_DIAG-gated).
    Lazy import: ops_stream_whisper imports THIS module, so a module-level
    import back would be circular. Never raises."""
    try:
        from . import ops_stream_whisper
        ops_stream_whisper._dbg(msg)
    except Exception:  # noqa: BLE001 — logging must never hurt the worker
        pass


def _norm_tokens(text: str) -> list[str]:
    """Word-level normalization: lowercase, punctuation stripped."""
    return _WORD_RE.findall(text.lower())


class Script:
    """A parsed presentation script: the original whitespace tokens plus a
    parallel normalized word sequence used by the aligner.

    ``tokens[i]`` is the i-th original whitespace-delimited chunk;
    ``norm[i]`` is its normalized form. Tokens that normalize to nothing
    (stray dashes etc.) are dropped from BOTH sequences so alignment
    indices always map 1:1 back to reconstructable original text.
    """

    def __init__(self, tokens: list[str], norm: list[str], raw: str):
        self.tokens = tokens
        self.norm = norm
        self.raw = raw

    @property
    def n_words(self) -> int:
        return len(self.norm)

    @classmethod
    def parse(cls, text: str) -> "Script | None":
        tokens, norm = [], []
        for chunk in text.split():
            words = _norm_tokens(chunk)
            if not words:
                continue  # pure punctuation — nothing to align against
            tokens.append(chunk)
            # a chunk like "16kHz-mono" normalizes to 2+ words; join them so
            # the two sequences stay index-parallel (alignment is per chunk,
            # close enough at word granularity and always reconstructable)
            norm.append(" ".join(words))
        if not tokens:
            return None
        return cls(tokens, norm, text)

    def window_text(self, start: int, end: int) -> str:
        """EXACT original wording (casing/punctuation) of tokens[start:end]."""
        return " ".join(self.tokens[start:end])

    def extract_phrases(self) -> list[str]:
        """Vocabulary for bias feeding: proper-noun runs (consecutive
        capitalized/digit-bearing tokens, sentence-initial stopwords
        excluded) plus distinctive long content words. Deduped
        (case-insensitive), first-appearance order, bounded."""
        phrases: list[str] = []
        seen: set[str] = set()

        def emit(phrase: str):
            key = phrase.lower()
            if phrase and key not in seen and len(phrases) < MAX_SCRIPT_PHRASES:
                seen.add(key)
                phrases.append(phrase)

        sentence_start = True
        run: list[str] = []
        for tok in self.tokens:
            bare = tok.strip(".,;:!?\"'()[]{}""''—–-")
            is_cap = bool(bare) and (bare[0].isupper() or
                                     any(ch.isdigit() for ch in bare))
            is_stop = bare.lower() in _STOPWORDS
            # sentence-initial capitalized stopwords/common words are just
            # capitalization; only continue a run or start one on a
            # non-stopword capital
            if is_cap and not is_stop and not (sentence_start and not run
                                               and bare.istitle()
                                               and bare.lower() in _STOPWORDS):
                if sentence_start and not run and bare.istitle():
                    # sentence-initial single capital: only trust it if it
                    # recurs capitalized mid-sentence later; cheap proxy —
                    # accept if it also appears capitalized elsewhere
                    if self._recurs_capitalized(bare):
                        run.append(bare)
                    # else: treat as ordinary sentence-case word
                else:
                    run.append(bare)
            else:
                if run:
                    emit(" ".join(run))
                    run = []
            sentence_start = bool(bare) and bare[-1] in ".!?" or (
                tok and tok[-1] in ".!?")
        if run:
            emit(" ".join(run))

        # distinctive content words: long, non-stopword, lowercase terms
        # ("transcript", "latency", "demonstration") — helpful bias, cheap
        for tok, nrm in zip(self.tokens, self.norm):
            for w in nrm.split():
                if len(w) >= 8 and w not in _STOPWORDS:
                    emit(w)
        return phrases

    def _recurs_capitalized(self, word: str) -> bool:
        n = 0
        for t in self.tokens:
            if t.strip(".,;:!?\"'()").startswith(word):
                n += 1
                if n >= 2:
                    return True
        return False


class PresentationSession:
    """Per-session presentation state: the script, the snap threshold, the
    caller's original bias phrases (guard restoration), and counters."""

    def __init__(self, script: Script, snap_t: float):
        self.script = script
        self.snap_t = snap_t
        self.caller_phrases: list | None = None
        self.snapped = 0
        self.untouched = 0
        self.suppressed = 0
        self._script_phrases = script.extract_phrases()
        # Consecutive-repeat guard state: {channel: (norm_text, last_seen)}
        # — last_seen is a monotonic clock, refreshed on every suppression.
        self._last_commit: dict[str, tuple[str, float]] = {}
        # Parsed like ENV_SNAP_T (defensive; garbage falls back), clamped
        # non-negative (0 disables suppression short of a same-instant dup).
        try:
            gap = float(os.environ.get(ENV_REPEAT_GAP, "")
                        or DEFAULT_REPEAT_GAP_S)
        except (TypeError, ValueError):
            gap = DEFAULT_REPEAT_GAP_S
        self.repeat_gap_s = max(gap, 0.0)

    # ------------------------------------------------------------ bias

    def extend_bias(self, bias_phrases) -> list:
        """Caller vocab first, script phrases appended after (dedup).

        Order rationale: ``_vocab_prompt`` keeps a front-prefix of whole
        phrases under its 320-char budget, so putting the caller's vocab
        first guarantees presentation mode can never evict caller-provided
        terms from the greedy initial_prompt. Script phrases that fall off
        the prompt's tail still reach the beam bias trie and both phonetic
        recovery layers, which take the FULL list with no budget.
        """
        caller = [str(p) for p in (bias_phrases or ()) if str(p).strip()]
        self.caller_phrases = list(caller)
        seen = {p.lower().strip() for p in caller}
        merged = list(caller)
        for p in self._script_phrases:
            if p.lower().strip() not in seen:
                seen.add(p.lower().strip())
                merged.append(p)
        return merged

    def attach(self, commits) -> None:
        """Wire this session into a ``_CommitLog``: install the snap hook
        and restore the regurgitation-guard sets to caller-only vocab (see
        module docstring — script phrases MUST bypass ``is_vocab_echo`` /
        ``strip_vocab_run``, because spoken script lines are legitimately
        made of script vocabulary)."""
        commits.presentation = self
        caller = self.caller_phrases or []
        # mirror _Session.__init__'s guard construction, caller-only
        if caller:
            commits.vocab_phrases = list(caller)
            commits.vocab_norm = "".join(
                ch for ch in ", ".join(caller).lower() if ch.isalnum())
        else:
            commits.vocab_phrases = []
            commits.vocab_norm = ""
        _dbg(f"presentation: attached (script {self.script.n_words} words, "
             f"{len(self._script_phrases)} bias phrases, snap_t {self.snap_t}, "
             f"guards restricted to {len(caller)} caller phrases)")

    # ------------------------------------------------------------ snap

    def snap_line(self, text: str) -> str:
        """Snap a committed line to the script's exact wording when a local
        word alignment lands under the threshold; otherwise return the text
        unchanged. Never raises (a presentation-mode bug must not take the
        commit path down)."""
        try:
            return self._snap(text)
        except Exception:  # noqa: BLE001
            _dbg("presentation: snap ERROR (line left untouched)")
            return text

    def _snap(self, text: str) -> str:
        words = _norm_tokens(text)
        if len(words) < MIN_SNAP_WORDS:
            self.untouched += 1
            return text
        dist, start, end = self._best_window(words)
        window_len = end - start
        denom = max(len(words), window_len, 1)
        ndist = dist / denom
        if ndist <= self.snap_t and window_len >= MIN_SNAP_WORDS:
            self.snapped += 1
            snapped = self.script.window_text(start, end)
            _dbg(f"presentation: snap #{self.snapped} ndist={ndist:.2f} "
                 f"win=[{start}:{end}] {text[:48]!r} -> {snapped[:48]!r} "
                 f"(session: {self.snapped} snapped / {self.untouched} untouched)")
            return snapped
        self.untouched += 1
        _dbg(f"presentation: no-snap ndist={ndist:.2f} {text[:48]!r} "
             f"(session: {self.snapped} snapped / {self.untouched} untouched)")
        return text

    def _best_window(self, words: list[str]) -> tuple[int, int, int]:
        """Semi-global (infix) word alignment of ``words`` against the whole
        script: edit distance to the BEST-matching contiguous script window,
        with the window free to start and end anywhere (Smith-Waterman-style
        locality at O(len(line) x len(script)) — the script is a few hundred
        words, so this is trivial per commit).

        Returns (distance, window_start, window_end) over script tokens.
        """
        script = self.script.norm
        n, m = len(words), len(script)
        # prev[j] = (cost, start) of best alignment of words[:i] ending at
        # script position j; row 0 = free start anywhere (cost 0, start j)
        prev = [(0, j) for j in range(m + 1)]
        for i in range(1, n + 1):
            cur = [(i, 0)] * (m + 1)  # column 0: consumed i line words
            wi = words[i - 1]
            for j in range(1, m + 1):
                sub = prev[j - 1][0] + (0 if wi == script[j - 1] else 1)
                dele = prev[j][0] + 1      # drop a line word
                ins = cur[j - 1][0] + 1    # skip a script word inside window
                best = min(sub, dele, ins)
                if best == sub:
                    cur[j] = (sub, prev[j - 1][1])
                elif best == dele:
                    cur[j] = (dele, prev[j][1])
                else:
                    cur[j] = (ins, cur[j - 1][1])
            prev = cur
        best_j, (best_cost, best_start) = 1, prev[1]
        for j in range(1, m + 1):
            if prev[j][0] < best_cost:
                best_cost, best_start = prev[j]
                best_j = j
        return best_cost, best_start, best_j

    # ---------------------------------------------------- repeat guard

    def suppress_repeat(self, channel: str, text: str,
                        now: float | None = None) -> bool:
        """True when this commit is a consecutive near-duplicate of the
        channel's previous commit AND the line is script-like — the
        low-signal decode loop's echo (module docstring, (c)). ``now`` is
        injectable for tests; defaults to time.monotonic(). Never raises
        (same contract as snap_line: a presentation-mode bug must not take
        the commit path down — any error means "commit the line")."""
        try:
            return self._suppress_repeat(channel, text, now)
        except Exception:  # noqa: BLE001
            _dbg("presentation: suppress_repeat ERROR (line committed)")
            return False

    def _suppress_repeat(self, channel: str, text: str,
                         now: float | None) -> bool:
        if now is None:
            now = time.monotonic()
        words = _norm_tokens(text)
        norm = " ".join(words)
        prev = self._last_commit.get(channel)
        if (prev is not None
                and len(words) >= MIN_SNAP_WORDS      # never "okay"/"yeah"
                and now - prev[1] <= self.repeat_gap_s
                and self._near_identical(norm, prev[0])
                and self._script_like(words)):
            dt = now - prev[1]
            # refresh last_seen: a sustained loop stays suppressed until a
            # different line intervenes or the loop pauses > gap
            self._last_commit[channel] = (prev[0], now)
            self.suppressed += 1
            _dbg(f"presentation: repeat-suppress #{self.suppressed} "
                 f"[{channel}] dt={dt:.1f}s {text[:48]!r} "
                 f"(session: {self.suppressed} suppressed)")
            return True
        self._last_commit[channel] = (norm, now)
        return False

    @staticmethod
    def _near_identical(a: str, b: str) -> bool:
        if a == b:
            return True
        import difflib
        return difflib.SequenceMatcher(None, a, b).ratio() >= REPEAT_SIM

    def _script_like(self, words: list[str]) -> bool:
        """The snap gate's ndist math, reused as a classifier: a line this
        close to some script window IS the script (a just-snapped line
        re-checks at ndist 0; an unsnapped-but-similar echo still passes
        via ndist <= snap_t). Off-script Q&A lands above the threshold and
        is never suppressed."""
        if len(words) < MIN_SNAP_WORDS:
            return False
        dist, start, end = self._best_window(words)
        denom = max(len(words), end - start, 1)
        return dist / denom <= self.snap_t

    def stats(self) -> dict:
        return {"snapped": self.snapped, "untouched": self.untouched,
                "suppressed": self.suppressed}


def session_from_env() -> PresentationSession | None:
    """Build a PresentationSession from the environment, or None.

    None (zero behavior change) when: env unset/blank, file missing or
    unreadable, file empty. NEVER raises — this runs on the session-start
    path and a bad env var must not take the worker down."""
    try:
        path = (os.environ.get(ENV_SCRIPT) or "").strip()
        if not path:
            return None
        if not os.path.isfile(path):
            _dbg(f"presentation: script file missing ({path}) — mode OFF")
            return None
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            raw = f.read()
        script = Script.parse(raw)
        if script is None:
            _dbg(f"presentation: script file empty ({path}) — mode OFF")
            return None
        try:
            snap_t = float(os.environ.get(ENV_SNAP_T, "") or DEFAULT_SNAP_T)
        except (TypeError, ValueError):
            snap_t = DEFAULT_SNAP_T
        snap_t = min(max(snap_t, 0.0), 1.0)
        return PresentationSession(script, snap_t)
    except Exception:  # noqa: BLE001 — absolute no-crash guarantee
        _dbg("presentation: session_from_env ERROR — mode OFF")
        return None
