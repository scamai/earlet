"""Beam search with contextual phrase biasing (L1) for the QNN Whisper decoder.

The decoder session is batch-1, so an N-beam step costs N decoder calls. Each
beam carries its own self-KV cache tensors; decoder outputs are fresh numpy
arrays per run() and are never mutated in place, so forked beams may share a
parent's KV arrays until their next decoder call produces new ones (we still
np.copy on branch for belt-and-braces safety against any future buffer reuse).

Phrase biasing is "boost with rollback": tokens that start or extend a match
against a token-prefix trie get +delta added to their logprob; completing a
full phrase adds a completion bonus; if a partial match dies, the accumulated
(unconfirmed) boost is subtracted from the beam score so partial-match garbage
is not rewarded. Each phrase is confirmed at most once per beam: the first
completion keeps the boost and earns the bonus, repeat completions retire the
boost with no bonus (otherwise repetition loops farm the reward -- observed on
u03). Trie/state logic is pure Python (no ONNX) for unit testing.

L2 density-ratio rescoring (DensityRatio) rides the same prepare() hook as L1:
mu*log P_you - lam*log P_ilm is added across the whole vocab, before top-k
selection, right alongside L1's sparse +delta on trie-candidate tokens. Unlike
BiasState, DensityRatio carries no per-beam state (P_you/P_ilm don't change
during decoding) -- only the context (trailing content tokens) varies per call.

L1b closed-set command prefix (CommandPrefix) rides that same prepare() hook
-- L1b is "option A" in the commit log and design notes; the letters A-G are
defined in docs/speech/README.md, which is the legend for both naming schemes --
from the other direction: instead of lifting tokens the beam already carries,
it pushes DOWN every token that cannot begin one of the catalogue's ~361
trigger/alias phrases, for the first max_tokens generated positions only, then
releases. That is what L1 structurally cannot do -- `note` never entered the
beam at that position, so there was nothing for +delta to lift -- and it costs
no extra decoder pass, because the full distribution `lp0` is computed at that
step already and used only to pick top-k. Per-request and opt-in: absent, this
module behaves exactly as it did before it existed.

SpeechGate is the non-speech verdict, which this decoder shipped WITHOUT: the
greedy path in the accelerator backend has taken Whisper's <|nospeech|> reading at the
SOT step since day one, beam search never did, and dictation ships beam 4 --
so the ungated path was the only one users ever ran. Held the key, said
nothing, got Korean (2026-08-19 field report, reproduced on an X Elite laptop). The gate
lives here rather than in the caller because this is the only function that
sees the SOT-step distribution, and a verdict that a caller has to remember to
apply is exactly the verdict that gets forgotten on the next decode path.
"""

from __future__ import annotations

import numpy as np

from .whisper_profile import (  # noqa: F401  -- re-exported; this was their only home
    NO_SPEECH_TOKEN_NAMES,
    WhisperModelMismatch,
    WhisperProfile,
)
from .whisper_profile import no_speech_token_id as _profile_no_speech_token_id

MEAN_DECODE_LEN = 200
MASK_NEG = -100.0
# EOT and SOT are the only two ids in the whole vocabulary that do NOT move
# between Whisper generations — the layout pins them (measured on base and on
# large-v3-turbo). They stay as constants because they are genuinely
# generation-stable, and they are used ONLY as the fallback for the
# tokenizer-less harness path below; every decode with a real tokenizer takes
# them off `WhisperProfile`.
EOT = 50257  # <|endoftext|>
SOT = 50258  # <|startoftranscript|>
# whisper-BASE's [sot, en, transcribe, notimestamps]. On large-v3-turbo those
# same four ids read [sot, en, TRANSLATE, NOSPEECH] — English-translate mode
# with a no-speech token in the prompt, which is what the A9-qnn measurement's
# 73.76% zh CER actually was.
#
# It is NOT a fallback for a real tokenizer any more. `beam_search` resolves
# the prefix through `WhisperProfile` and RAISES when that fails; this constant
# survives only for `tokenizer=None`, which no shipped caller uses (the platform
# always passes one) and which the decoder unit suites drive against
# ten-token synthetic vocabularies where these ids are unreachable anyway.
FORCED_PREFIX = [50258, 50259, 50359, 50363]


# ---------------------------------------------------------------------------
# Bias trie (pure python, unit-testable without ONNX)
# ---------------------------------------------------------------------------


class _TrieNode:
    __slots__ = ("children", "phrase_ids", "depth")

    def __init__(self, depth: int = 0) -> None:
        self.children: dict[int, _TrieNode] = {}
        # Tokens from the root to this node. Read only by `BiasState.advance`
        # to report HOW LONG a completed match was (`fired[].n`); nothing in
        # the scoring path depends on it.
        self.depth = depth
        # ids of phrases whose (variant) token sequence ends at this node;
        # non-empty means terminal. All variants of a phrase share one id, so
        # a phrase is confirmed once per beam no matter which variant matched.
        self.phrase_ids: set[int] = set()

    @property
    def is_end(self) -> bool:
        return bool(self.phrase_ids)


def phrase_variants(phrase: str) -> list[str]:
    """Casing/spacing variants to tokenize: as-is, leading space, lowercase."""
    out: list[str] = []
    for base in (phrase, phrase.lower()):
        for v in (base, " " + base):
            if v not in out:
                out.append(v)
    return out


class BiasTrie:
    """Token-prefix trie over tokenized phrase variants.

    delta: per-token boost for starting/extending a match.
    completion_bonus: extra score for completing a full phrase.

    Defaults tuned on eval/tts u01-u03 + spike/jfk.wav (NPU, beam 4):
    delta=5 recovered all entities but made end-of-audio phrase hallucination
    net-positive (u03 grew a synthetic entity tail); delta=3 was too
    weak for u01 (a declared name and version string were lost). delta=4 recovers all
    entities with clean endings on every clip.
    """

    def __init__(
        self,
        bias_phrases: list[str],
        tokenizer=None,
        *,
        delta: float,
        completion_bonus: float,
    ) -> None:
        # Required, keyword-only, and deliberately without defaults.
        #
        # These carried `delta=4.0, completion_bonus=3.0` — a research-era
        # signature no shipped caller should ever have accepted — and the
        # silence is what did the damage twice on 2026-08-17:
        #
        #   * the accelerator backend constructed with neither, so the shipped
        #     Snapdragon engine ran at δ=4.0, the value `the decode contract` §2 cites as
        #     an outright criterion-5 breach (1.917 per 10k against 0.017 at
        #     the macOS operating point), and at a completion bonus of 3.0
        #     against a shipped 0.0;
        #   * an internal research probe passed δ and omitted the
        #     bonus, inheriting 3.0, which silently corrupted every runaway
        #     rate and the whole `A8'` size ladder that was then reasoned from.
        #
        # A *correct* default would not have prevented either: the defect is
        # that a caller can inherit an operating point it never chose, without
        # saying so and without anything failing. Required parameters turn both
        # into a `TypeError` at the call site. `from_sequences` carries the same
        # requirement for the same reason — it used to default δ to 5.0, a
        # third disagreeing value, and it satisfies this constructor by
        # forwarding its own.
        self.delta = float(delta)
        self.completion_bonus = float(completion_bonus)
        # Empty until set_phrase_deltas() is called. Every δ lookup falls back
        # to the scalar while these are empty, so the uniform path is untouched.
        self.phrase_deltas: dict[int, float] = {}
        self._node_delta: dict[int, float] = {}
        self.root = _TrieNode()
        if bias_phrases and tokenizer is not None:
            for phrase_id, phrase in enumerate(bias_phrases):
                for variant in phrase_variants(phrase):
                    ids = tokenizer.encode(variant, add_special_tokens=False)
                    self.add_sequence(ids, phrase_id)

    @classmethod
    def from_sequences(
        cls,
        sequences: list[list[int]],
        *,
        delta: float,
        completion_bonus: float,
    ) -> "BiasTrie":
        trie = cls([], None, delta=delta, completion_bonus=completion_bonus)
        for phrase_id, seq in enumerate(sequences):
            trie.add_sequence(seq, phrase_id)
        return trie

    def add_sequence(self, ids: list[int], phrase_id: int) -> None:
        if not ids:
            return
        node = self.root
        for t in ids:
            node = node.children.setdefault(t, _TrieNode(node.depth + 1))
        node.phrase_ids.add(phrase_id)

    def set_phrase_deltas(self, phrase_deltas: dict[int, float]) -> None:
        """Give individual phrases their own δ, overriding the trie's scalar.

        Per-term deltas let an evaluated vocabulary give different entries
        different boosts. This method applies the supplied values; it does
        not establish their accuracy or a safe operating point.

        Call after construction; `phrase_id` is the phrase's index in the list
        passed to `__init__`, which is why that order is documented as stable.
        Phrases absent from the map keep `self.delta`.
        """
        self.phrase_deltas = dict(phrase_deltas)
        self._node_delta = {}
        if not self.phrase_deltas:
            return

        # Cache, per node, the largest δ of any phrase still reachable below it.
        # A token can sit on the path to several phrases at once -- `Cluely` and
        # `CedarCore` share a leading `C` -- so "the δ of this token" is only
        # defined once you say which phrase it leads to. Taking the max keeps
        # the neediest term reachable; resolving δ only at the terminal node
        # would boost every prefix at the minimum and lose the hard terms
        # exactly where they need the help.
        def walk(node: _TrieNode) -> float:
            best = 0.0
            for phrase_id in node.phrase_ids:
                best = max(best, self.phrase_deltas.get(phrase_id, self.delta))
            for child in node.children.values():
                best = max(best, walk(child))
            self._node_delta[id(node)] = best
            return best

        walk(self.root)

    def delta_for_token(self, nodes: tuple, token: int) -> float:
        """δ to add to `token`, given the currently active trie nodes.

        Returns `self.delta` unless `set_phrase_deltas` has been called, so the
        uniform path is unchanged by construction.
        """
        if not getattr(self, "_node_delta", None):
            return self.delta
        best = 0.0
        child = self.root.children.get(token)
        if child is not None:
            best = self._node_delta.get(id(child), self.delta)
        for node in nodes:
            child = node.children.get(token)
            if child is not None:
                best = max(best, self._node_delta.get(id(child), self.delta))
        return best

    def initial_state(self) -> "BiasState":
        return BiasState(self, (), 0.0, frozenset())


class BiasTrieCache:
    """Built tries, reused while their inputs are unchanged.

    **The trie is a pure function of its inputs and is read-only afterwards,
    and both halves of that are load-bearing.** Same ordered phrase list, same
    tokenizer, same scalars produce a structurally identical tree — verified by
    fingerprint, not assumed. And nothing mutates it after construction:
    ``set_phrase_deltas`` is called once by the builder, ``delta_for_token``
    only reads ``_node_delta``, and every per-beam mutation lives in
    ``BiasState``, which is immutable and returns new states. So one built tree
    can serve every decode that asks for the same thing.

    **Without this, it was rebuilt inside the decode loop.** ``trie_decode``
    builds inside ``TrieTranscribeModel.decode``, which upstream Whisper calls
    once per 30-second window, and the platform backend builds inside
    ``transcribe``, once per utterance. Each rebuild re-tokenises every phrase
    variant — four per phrase — and re-inserts every node.

    Measured on the research programme's own scale curve (4,303 clips,
    250 → 5,000 entries): 293 s → 503 s. Fitting the three published points
    against candidate cost terms, the time tracks **trie node count**
    (leave-one-out error 0.4%) rather than per-step candidate-set size (5.9%),
    which is what says the cost is construction and not the decode loop. Node
    counts over the real ``large-v3`` vocabulary: 250 phrases → 2,206 nodes;
    5,000 → 34,526.

    That cost is worth naming precisely because it is **not** a property of a
    large vocabulary — it is the same object built 4,303 times. Reporting it as
    "big lists are slow" argues for capping the list, which is the wrong fix.

    **Bounded, and small on purpose.** The shipped pattern is one vocabulary
    sent once in the init frame, with a call's own ``bias_phrases`` merged on
    top (``asr_server``), so the live key set is "defaults" and
    "defaults + this call's extras". Four entries covers that with headroom and
    cannot grow into a leak; a larger cache would be capacity nobody measured a
    need for.
    """

    #: See the class docstring — sized for the merge pattern, not for a guess.
    CAPACITY = 4

    def __init__(self) -> None:
        self._entries: dict[tuple, tuple] = {}

    @staticmethod
    def key(bias_phrases, *, delta: float, completion_bonus: float,
            phrase_deltas=None) -> tuple:
        """Everything that changes the built tree, in one hashable value.

        ``bias_phrases`` is keyed as an ORDERED tuple rather than a set because
        ``phrase_id`` is the phrase's index in the list: the same phrases in a
        different order build a tree whose leaves carry different ids, and
        ``set_phrase_deltas`` binds a δ to a phrase BY THAT INDEX. Keying on a
        set would serve a tree whose per-term δs are attached to the wrong
        terms — silently, and only when a caller reordered its list.
        """
        deltas = (
            None if not phrase_deltas
            else tuple(sorted((int(k), float(v))
                              for k, v in dict(phrase_deltas).items()))
        )
        return (tuple(bias_phrases), float(delta), float(completion_bonus), deltas)

    def get(self, key: tuple, tokenizer, build):
        """The cached trie for ``key``, or ``build()``'s result, cached.

        The tokenizer is compared by IDENTITY and the entry holds a reference to
        it. Identity because two tokenizers that compare equal can still encode
        differently, and a reference because an ``id()`` alone would be unsound:
        a collected tokenizer's address can be reused, and the entry would then
        serve a tree built for a different vocabulary. Holding it makes that
        unrepresentable. In practice the comparison succeeds — upstream
        Whisper's ``get_tokenizer`` is ``lru_cache``d, so a decode asks for the
        same object every time — and when it does not, the miss is a rebuild,
        never a wrong answer.
        """
        hit = self._entries.get(key)
        if hit is not None and hit[0] is tokenizer:
            return hit[1]

        trie = build()
        if len(self._entries) >= self.CAPACITY:
            # Oldest first: dicts preserve insertion order, and the entry least
            # recently ADDED is the right one to drop for a key set this small.
            self._entries.pop(next(iter(self._entries)))
        self._entries[key] = (tokenizer, trie)
        return trie


class BiasState:
    """Per-beam biasing state: active trie nodes + unconfirmed (pending) boost.

    Immutable: advance() returns a new state, so forked beams stay independent.
    """

    __slots__ = ("trie", "nodes", "pending", "completed", "earned", "fired")

    def __init__(
        self, trie: BiasTrie, nodes: tuple, pending: float, completed: frozenset,
        earned: tuple | None = None, fired: tuple = (),
    ) -> None:
        self.trie = trie
        self.nodes = nodes  # tuple[_TrieNode]: in-progress (non-root) matches
        self.pending = pending  # boost applied to score but not yet confirmed
        self.completed = completed  # phrase ids already confirmed in this beam
        # Boost each in-flight match has itself been charged, positionally
        # aligned with `nodes`. Kept as a parallel tuple rather than folded into
        # `nodes` so that tuple stays a plain sequence of trie nodes, which
        # `delta_for_token` and the tests both depend on.
        self.earned = tuple(earned) if earned is not None else (0.0,) * len(nodes)
        # Every FIRST completion this state's path has seen, in token order,
        # as ``(phrase_id, n_tokens_matched)``. This is ``completed`` with its
        # history kept: ``completed`` is a set (what has been paid for, for
        # the repeat rule), ``fired`` is the record (what the host is told
        # under ``fired[]``). Repeat completions are not re-recorded, for the
        # same reason they earn nothing. Appended only; never consulted by
        # the scoring arithmetic.
        self.fired = tuple(fired)

    def candidate_tokens(self) -> set[int]:
        """Tokens that would start or extend a match (get +delta on logprob)."""
        cands: set[int] = set(self.trie.root.children)
        for node in self.nodes:
            cands.update(node.children)
        return cands

    def advance(self, token: int) -> tuple["BiasState", float]:
        """Consume the chosen token. Returns (new_state, score_adjustment).

        score_adjustment covers the completion bonus and/or the rollback of
        pending boost when all partial matches die. The +delta on a boosted
        token is NOT included here -- it was already applied to the token's
        logprob at selection time; we only track it in `pending`.
        """
        trie = self.trie
        boosted = False
        survivors: list[_TrieNode] = []
        survivor_earned: list[float] = []
        # One charge per step: whatever delta_for_token returns is what the
        # decoder added to this token's logprob, so it is what may later need
        # refunding.
        charged = trie.delta_for_token(self.nodes, token)
        seen: set[int] = set()
        completed_new = False  # phrase completed for the first time in this beam
        completed_repeat = False  # phrase this beam has already been paid for
        newly_done: set[int] = set()
        done_len: dict[int, int] = {}  # phrase id -> longest match that completed it
        for node, parent_earned in (*zip(self.nodes, self.earned), (trie.root, 0.0)):
            child = node.children.get(token)
            if child is None:
                continue
            boosted = True
            if child.is_end:
                fresh = child.phrase_ids - self.completed
                if fresh:
                    completed_new = True
                    newly_done |= fresh
                    for pid in fresh:
                        done_len[pid] = max(done_len.get(pid, 0), child.depth)
                else:
                    completed_repeat = True
            if child.children:
                if id(child) in seen:
                    # Shared node reached by two parents: keep the better-earned
                    # path, never the sum -- the score was charged once.
                    i = survivors.index(child)
                    survivor_earned[i] = max(survivor_earned[i], parent_earned + charged)
                else:
                    seen.add(id(child))
                    survivors.append(child)
                    survivor_earned.append(parent_earned + charged)

        # The accrued `pending` is what gets refunded when a partial match dies,
        # so it MUST equal what the decoder actually added to the logprob. With a
        # per-phrase δ the trie's scalar is the wrong amount: the refund would be
        # smaller than the charge and dead matches would keep score they never
        # earned. Both sides call `delta_for_token` with the same inputs, so they
        # cannot drift.
        raw_pending = self.pending + (charged if boosted else 0.0)
        completed = self.completed
        fired = self.fired
        adjustment = 0.0
        if completed_new:
            # First completion in this beam: confirm the boost (keep it) and pay
            # the completion bonus.
            adjustment += trie.completion_bonus
            pending = 0.0
            survivor_earned = [0.0] * len(survivors)
            completed = completed | newly_done
            fired = fired + tuple((pid, done_len[pid]) for pid in sorted(newly_done))
        elif completed_repeat:
            # Repeat completion: retire the guidance boost, no bonus. Confirming
            # repeats makes phrase spam net-positive and beams farm it in
            # repetition loops (observed on u03: "...Hexagon NPU build both past
            # the Snapdragon..." repeated to the length limit).
            adjustment -= raw_pending
            pending = 0.0
            survivor_earned = [0.0] * len(survivors)
        else:
            # Retain only the boost a LIVE match has been charged; refund the
            # rest. Previously this accrued every boosted step and refunded only
            # when EVERY match died -- but with a real lexicon the root offers a
            # phrase-initial token for almost any word, so `survivors` is nearly
            # never empty and `pending` grew without bound. The beam then carried
            # unbounded unconfirmed credit, EOT (never boosted) stopped being
            # competitive, decode ran to the length cap and emitted a repetition
            # loop that the junk filter blanks. Measured on the Windows/QNN copy
            # of this engine: 26.1% of clips (94/360) returned EMPTY with a
            # 98-phrase lexicon, all 94 of which greedy transcribed fine; after
            # this change blanks went to 0%, term recall 76.9 -> 92.5% and WER
            # 34.86 -> 2.52%, which beats greedy's 8.78%. The research side
            # independently measured 24.5% carrier collapse from the same trie.
            retained = max(survivor_earned, default=0.0)
            if retained < raw_pending:
                adjustment -= raw_pending - retained
            pending = retained
        return (
            BiasState(trie, tuple(survivors), pending, completed,
                      tuple(survivor_earned), fired),
            adjustment,
        )


# ---------------------------------------------------------------------------
# Closed-set command prefix (L1b): the post-wake-word position
# ---------------------------------------------------------------------------


class CommandPrefix:
    """Closed-set constraint on the first few GENERATED content positions.

    Voice workflows are spoken as ``<wake word> <trigger> <payload>``. Once the wake
    word has matched, the next one to three tokens are drawn from a closed set
    of ~361 catalogue trigger/alias phrases -- and nothing in the pipeline told
    the decoder that, so Whisper was choosing between our trigger and all of
    English. It loses that contest on short words: `note ThinkShield ...`
    decodes as `No ThinkShield ...` (four arms, two repeats each, all eight
    byte-identical on the NPU, in an internal trigger-decode evaluation).

    **Why L1 cannot fix this and this can.** `BiasTrie` re-ranks tokens the
    beam already carries: it adds +delta to trie candidates and the beam then
    picks top-k. When `note` is not in the top-k of the open distribution at
    all, there is nothing to lift -- measured, `fired[]` never contains it,
    while the control `tell` in the same list at the same position does. This
    layer works on the other side of the same top-k: it pushes the ~50k tokens
    that CANNOT begin a catalogue phrase down, so the ones that can are
    shortlisted whether or not the open distribution wanted them.

    **Cost: zero extra decoder passes.** `beam_search` already computes the
    full log-probability vector at every position and uses it only to pick
    top-k. This rides the same `prepare()` hook as L1 and L2 and adds nothing
    but a vector add.

    **The trie is `BiasTrie`, deliberately.** Same tokenizer variants
    (`phrase_variants`: as-is / leading-space / lowercase, which is what makes
    the leading-space token Whisper actually emits reachable), same
    `BiasTrieCache`. Its `delta` / `completion_bonus` are constructed at 0.0
    and never read: this layer never boosts, so there is no pending balance to
    roll back and no completion to pay for. What it needs from the trie is the
    tree, and having one implementation of the tree is the point.

    mode:
      ``"soft"`` (default) -- off-path tokens get ``-delta``. Strong acoustic
      evidence can still win, which is the graceful degradation callers
      asked for: a hold addressed to the wake word that asks for nothing in the
      catalogue still decodes to what was said, merely at a handicap.
      ``"hard"`` -- off-path tokens get ``MASK_NEG`` (-100) added. Finite on
      purpose, and the same constant the attention mask uses: a decode whose
      whole catalogue is acoustically absurd still returns text rather than
      being forced into a trigger nobody said. ``-inf`` would make the
      constraint unrepresentably strong -- `beam_search` skips non-finite
      candidates outright, so an -inf mask could empty the beam.

    Both modes are one additive vector, so this composes with L1's sparse
    +delta and L2's dense ratio in any order without either being disturbed.

    **This is a live trie PATH, not a per-position token filter, and the
    difference is the whole mechanism.** Measured on the device (lp0 probe,
    clip S9 "note ThinkShield has no media defence pillar",
    whisper-large-v3-turbo-qnn), the catalogue's 361 phrases reduce to 338
    distinct opening tokens, and restricting position 0 to them moves the
    spoken ` note` from **rank 17 of 51,866** to **rank 3 of 338** -- four
    orders of magnitude of competition deleted, which is what puts it in a
    beam of 4 at all. But the two tokens ahead of it are not noise and are not
    paths to `note`; they are legitimate openings of OTHER catalogue
    workflows:

        ` no`   logprob -2.228   opens "no notifications"
        ` not`  logprob -6.201   opens "notepad"
        ` note` logprob -7.322   opens "note"          <- spoken

    A per-position argmax over the restricted set therefore routes to the
    WRONG WORKFLOW, and the 5.1-nat gap says no `delta` small enough to be
    safe will flip position 0 either (turning delta up to force it is exactly
    the over-boosting that produced `NoteThinkShield`). The discrimination
    lives in the tokens AFTER the first one: `no notifications` and `notepad`
    have to keep paying for `notifications` / `pad`, which the speaker never
    said, while `note` completes immediately and is free. So the beam is left
    to settle it, over a path, on the length-normalised score `beam_search`
    already ranks by (`final_score` -> `_normalized`) -- raw sums favour short
    phrases and raw per-token means favour long ones, and neither tracks
    acoustic fit.

    Releases -- and this is the correctness property that matters most, since
    the boundary after a short trigger is exactly where the bugs live -- as
    soon as any of:
      * a phrase completes (a terminal node was reached),
      * the chosen token leaves every live path (soft mode: the off-path token
        won anyway, so this beam is not decoding a catalogue phrase and there
        is no closed set left to state about it),
      * the content position reaches ``max_tokens``.
    After release the payload decodes completely unconstrained.

    **How the wrong workflow dies, given that release.** In HARD mode a beam
    that took ` no` cannot leave the path: its only affordable continuations
    are `notifications`' tokens, which it pays for at their real (terrible)
    acoustic cost -- decisive, and exactly the whole-phrase discrimination the
    device probe measured. In SOFT mode it can leave, once, for ``-delta``.
    So soft mode resolves the measured case with a margin of
    ``delta - 5.1`` nats and no more: at the briefed ``delta = 6.0`` that is
    **0.9 nats**, which works and is thin. That number is a property of the
    catalogue and the clip, it is stated here rather than buried, and it is
    what the device arms should be sized against.

    Charging ``-delta`` ONCE, at the moment the beam leaves, rather than at
    every remaining constrained position, is deliberate. Per-position charging
    would give soft mode ``delta * max_tokens`` of pull (18 nats at the
    defaults) and comfortably win the S9 case -- but "is this utterance a
    catalogue command" is ONE binary fact, and a prior on it must not cost
    three times as much because ``max_tokens`` was set to 3. The pull toward
    the catalogue would then scale with a window length nobody chose for that
    reason, and a mis-armed constraint over ordinary prose -- the wake word is
    only ~50% reliable on Windows, which the brief calls the risk that decides
    whether this ships -- would have 18 nats with which to insert a trigger
    nobody said. One decision, one charge.

    **Why a penalty on off-path tokens rather than L1's boost on on-path
    tokens, and why that means there is nothing to roll back.** The two are
    complements: the boost form credits a beam at every position it is ON the
    closed set, the penalty form debits it at the position it goes OFF. The
    boost form therefore accrues credit a beam has not yet earned -- which is
    precisely why `BiasState` carries `pending` and refunds it when a partial
    match dies, and why an unbounded `pending` once collapsed 26.1% of clips
    to empty. The penalty form accrues only debit, and only for a disagreement
    that has already happened, so no unconfirmed balance is ever created here
    and none can be left unsettled, double-charged, or refunded to the wrong
    beam. It also means `BiasState`'s `pending`/`earned`/`fired` accounting is
    untouched when both layers are live: this one never writes to it, which is
    asserted rather than assumed (`test_command_prefix.py`).
    """

    HARD = "hard"
    SOFT = "soft"
    MODES = (SOFT, HARD)
    #: 100 catalogue triggers, the longest 5 words; 3 covers every one- and
    #: two-word trigger's tokenization with headroom.
    DEFAULT_MAX_TOKENS = 3
    DEFAULT_DELTA = 6.0

    __slots__ = ("trie", "max_tokens", "mode", "delta", "n_phrases")

    def __init__(
        self,
        trie: BiasTrie,
        *,
        max_tokens: int = DEFAULT_MAX_TOKENS,
        mode: str = SOFT,
        delta: float = DEFAULT_DELTA,
        n_phrases: int = 0,
    ) -> None:
        if mode not in self.MODES:
            raise ValueError(
                f"command_prefix mode must be one of {self.MODES}, got {mode!r}")
        max_tokens = int(max_tokens)
        if max_tokens < 1:
            raise ValueError(
                f"command_prefix max_tokens must be >= 1, got {max_tokens}")
        delta = float(delta)
        if not delta > 0.0:
            # A non-positive delta is a constraint that does nothing while
            # LOOKING armed -- the request said "restrict this position" and
            # the decode would not. Refuse rather than no-op silently; the way
            # to ask for no constraint is to omit the field.
            raise ValueError(f"command_prefix delta must be > 0, got {delta}")
        self.trie = trie
        self.max_tokens = max_tokens
        self.mode = mode
        self.delta = delta
        self.n_phrases = int(n_phrases)

    @property
    def penalty(self) -> float:
        """What an off-path token has ADDED to its logprob (always negative)."""
        return MASK_NEG if self.mode == self.HARD else -self.delta

    @classmethod
    def from_phrases(cls, phrases, tokenizer, **kw) -> "CommandPrefix":
        phrases = [p for p in (phrases or []) if p]
        return cls(
            BiasTrie(phrases, tokenizer, delta=0.0, completion_bonus=0.0),
            n_phrases=len(phrases), **kw)

    @classmethod
    def from_sequences(cls, sequences, **kw) -> "CommandPrefix":
        """Token-id phrases directly -- the tokenizer-less unit harnesses."""
        sequences = [list(s) for s in (sequences or []) if s]
        return cls(
            BiasTrie.from_sequences(sequences, delta=0.0, completion_bonus=0.0),
            n_phrases=len(sequences), **kw)

    def initial_state(self) -> "CommandPrefixState":
        return CommandPrefixState(self, (self.trie.root,), False)

    def describe(self) -> dict:
        return {
            "phrases": self.n_phrases,
            "max_tokens": self.max_tokens,
            "mode": self.mode,
            "delta": self.delta,
        }


class CommandPrefixState:
    """Per-beam walk of the command-prefix trie. Immutable, like `BiasState`.

    Carries no unconfirmed score: unlike L1 nothing is charged to the beam
    ahead of being earned, so there is nothing to confirm and nothing to
    refund -- which is exactly why this does not reuse `BiasState` and cannot
    disturb its `pending` / `earned` / `fired` accounting.

    `nodes` are the live prefixes (the root alone at content position 0).
    `done` is the release latch; `completed` says WHY it latched -- a phrase
    finished, or the beam walked off the closed set. The two release the
    constraint identically and are only distinguishable in the reporting,
    where the difference is the whole answer to "did this hold ask for
    anything in the catalogue".
    """

    __slots__ = ("cp", "nodes", "done", "completed")

    def __init__(self, cp: CommandPrefix, nodes: tuple, done: bool = False,
                 completed: bool = False) -> None:
        self.cp = cp
        self.nodes = tuple(nodes)
        self.done = bool(done)
        self.completed = bool(completed)

    def active(self, pos: int) -> bool:
        """Is the constraint live at generated-content position `pos`?"""
        return (not self.done) and pos < self.cp.max_tokens and bool(self.nodes)

    def allowed_tokens(self) -> set[int]:
        """Tokens that continue a live path. NOT re-seeded from the root:
        position k>0 must extend the prefix chosen at k-1, or `look up` would
        license every trigger-initial token at the second position too."""
        out: set[int] = set()
        for node in self.nodes:
            out.update(node.children)
        return out

    def apply(self, lp: np.ndarray, pos: int, sot_id: int, eot_id: int) -> np.ndarray:
        """Add the off-path penalty to `lp` IN PLACE (prepare() already copied).

        Purely additive -- the penalty goes on the whole text range and comes
        back off the allowed set -- so this commutes with L1 and L2 and cannot
        clobber what they added.

        EOT is exempt. "Stop here" is always a legal thing for the decoder to
        want, and a constraint that can only be satisfied by emitting a trigger
        is a hallucination generator on exactly the quiet-hold case the energy
        gate and `SpeechGate` exist for. Exempting it can only WEAKEN this
        layer; it can never make it invent a command. The exemption is dropped
        when the allowed set is empty (an empty catalogue -- `asr_server`
        refuses one, this is the belt): with nothing else spared, an exempt EOT
        would be the one token NOT penalised, and this layer would start
        truncating a payload it has no business touching.
        """
        if not self.active(pos):
            return lp
        penalty = self.cp.penalty
        # Bounded by the ACTUAL vector, not just by `sot_id`: the unit
        # harnesses drive `beam_search` with ten-token synthetic vocabularies
        # while `sot_id`/`eot_id` keep their real five-figure values, so an
        # unbounded fancy-index on `eot_id` would be an IndexError there and
        # nowhere else.
        limit = min(int(sot_id), int(lp.shape[0]))
        lp[:limit] += penalty
        exempt = {t for t in self.allowed_tokens() if 0 <= t < limit}
        if exempt and 0 <= eot_id < limit:
            exempt.add(eot_id)
        if exempt:
            idx = np.fromiter(sorted(exempt), dtype=np.int64, count=len(exempt))
            lp[idx] -= penalty
        return lp

    def advance(self, token: int, pos: int) -> "CommandPrefixState":
        """Consume the chosen token at content position `pos`."""
        if self.done or pos >= self.cp.max_tokens:
            # Already inert, or past the window -- `active` is False from here
            # on either way, so there is no walk left to do.
            return self
        survivors = []
        completed = False
        for node in self.nodes:
            child = node.children.get(token)
            if child is None:
                continue
            if child.is_end:
                # A phrase ended here. Release even when a LONGER phrase is
                # still alive through this node (`note` under `notepad`, if
                # they share a leading token): releasing early can only
                # under-constrain the payload, and under-constraining the
                # payload is the safe direction. Nothing is lost by it either
                # -- the longer phrase's next token is then free to win on
                # pure acoustics, which is what should decide it.
                completed = True
            if child.children:
                survivors.append(child)
        if completed:
            return self._released(completed=True)
        if not survivors:
            # Walked off every path (soft mode; the off-path token won anyway).
            # One decision, one charge -- it has already been paid, on this
            # token, and the beam is let go. See the class docstring for why
            # this is not charged again at every remaining position.
            return self._released(completed=False)
        return CommandPrefixState(self.cp, tuple(survivors), False)

    def _released(self, completed: bool) -> "CommandPrefixState":
        return CommandPrefixState(self.cp, (), True, completed)


class DensityRatio:
    """L2: mu*log P_you(t|ctx) - lam*log P_ilm(t|ctx), added across the full
    vocab in prepare(), before top-k selection -- density ratio needs to be
    able to promote a token that acoustic-only scoring wouldn't have shortlisted,
    so (unlike L1's sparse trie-candidate boost) this touches every token.

    p_you / p_ilm: NgramLM instances (worker/ngram_lm.py). No per-beam state;
    stateless given a context, so one instance is shared across all beams.

    lam/mu default to 0.1: tuned on eval/l1_eval.py's 4-clip set (NPU,
    beam-4). lam=mu=1.0 (a "reasonable"-looking default) wrecked every clip
    into repetition/hallucination loops -- unlike L1's delta, which only
    touches a handful of trie-candidate tokens per step, this term is DENSE
    (added across the whole vocab every step), so it needs to be a much
    smaller nudge to stay a tiebreaker rather than override acoustics
    outright. lam=mu=0.3 still broke the jfk fluency guard; 0.1 held clean.
    """

    def __init__(self, p_you, p_ilm, lam: float = 0.1, mu: float = 0.1) -> None:
        # Both LMs must be built over the SAME vocabulary width, or the two
        # score vectors do not line up and `prepare()` adds token i of one to
        # token i of the other. NgramLM.save/load persist vocab_size INSIDE the
        # artifact, so the shipped p_ilm.json.gz is stamped with whatever width
        # it was distilled at; a P_you built fresh at another model's width
        # would silently mis-add against it. Currently always equal (both come
        # from ngram_lm's default), which is why this raises rather than
        # coerces -- it is a tripwire for a swap, not a live code path.
        you, ilm = getattr(p_you, "vocab_size", None), getattr(p_ilm, "vocab_size", None)
        if you is not None and ilm is not None and you != ilm:
            raise ValueError(
                f"density-ratio LMs disagree about the vocabulary: P_you is "
                f"{you} wide, P_ilm is {ilm}. They index the same logits "
                f"vector, so one of them was built for a different model."
            )
        self.p_you = p_you
        self.p_ilm = p_ilm
        self.lam = float(lam)
        self.mu = float(mu)

    def score_vector(self, context: tuple[int, ...]) -> np.ndarray:
        return self.mu * self.p_you.score_vector(context) - self.lam * self.p_ilm.score_vector(
            context
        )


# ---------------------------------------------------------------------------
# Non-speech gate (pure python, unit-testable without ONNX)
# ---------------------------------------------------------------------------


# Whisper's "this audio is not speech" special. whisper-base's tokenizer spells
# it <|nocaptions|>; large-v3/turbo spell the same slot <|nospeech|>. Resolved
# by NAME through the tokenizer, in this order, and never by id -- the id moved
# between generations exactly like the FORCED_PREFIX ids did (see beam_search).
#
# The resolution itself now lives in `whisper_profile`, alongside every other
# id this package needs, because two modules resolving the same concept is what
# produced the defect: `_speech_gate` resolved it correctly by name three lines
# after handing `_no_speech_prob` a hardcoded 50362.


def no_speech_token_id(tokenizer) -> int | None:
    """Id of the <|nospeech|>/<|nocaptions|> token, or None if absent.

    Measured on the shipped whisper-base tokenizer (X Elite laptop, 2026-08-19):
    `convert_tokens_to_ids("<|nospeech|>")` returns **50257**, which is
    `unk_token_id` AND `<|endoftext|>` -- i.e. asking for the wrong spelling
    does not fail, it silently hands back EOT. A gate reading P(EOT) at the
    SOT step would have fired on ordinary speech, so `whisper_profile`'s unk
    check is load-bearing, not defensive decoration. The right name on base is
    `<|nocaptions|>` (50362).

    Kept as a thin alias: this name is what the decoder unit suites and
    the backend's speech-gate callers already ask for, and the None-on-absent contract
    (the gate goes inert rather than gating on a wrong id) belongs to the GATE.
    `WhisperProfile` is the strict layer — it raises instead, at load, because
    an engine whose gate can never fire is an engine that types Korean.
    """
    return _profile_no_speech_token_id(tokenizer)


class SpeechGate:
    """Two INDEPENDENT reasons to call a chunk non-speech, both read off the
    single SOT-step distribution (so a rejected chunk costs zero beam steps).

    Rule 1, `no_speech_threshold` -- P(<|nospeech|>). Whisper's own verdict,
    the same number and the same 0.75 the greedy platform path has
    always used.

    Rule 2, `language_prob_threshold` -- P(the language token the decode
    actually picked). A confident decode commits to a language; a decode over
    noise spreads that mass over 99 of them and then transcribes whichever one
    won the lottery. This is the *observable shape* of the reported bug: not
    "the text was wrong" but "the text was KOREAN". Rule 2 is deliberately a
    separate threshold with a separate method: it catches chunks rule 1 misses
    (measured below) and it is the one to loosen first if a legitimate speaker
    of an under-represented language trips it, without touching rule 1.

    Either threshold may be None, which disables that rule and only that rule.

    Measured on a Snapdragon X Elite laptop (X1E80100), staged worker, whisper-base-qnn
    on the NPU, 2026-08-19 -- SOT-step readings on the beam path's own features:

        audio                              P(nospeech)   P(chosen lang)
        1.0s gaussian hiss (5 amplitudes)  0.925-0.956   0.013-0.036  <|ko|>
        1.5s digital silence               0.932         0.025        <|en|>
        jfk.wav 7.4s                       0.007         0.988        <|en|>
        jfk.wav scaled to peak 0.005       0.148         0.823        <|en|>
        jfk.wav first 0.8s                 0.143         0.749        <|en|>
        real mic capture, English          0.070         0.865        <|en|>
        real mic capture, Chinese          0.179         0.816        <|zh|>
        real mic capture, Chinese (quiet)  0.471         0.501        <|zh|>
        real mic capture, English (messy)  0.307         0.475        <|en|>

    Worst real speech: P(nospeech) 0.471, P(lang) 0.475. Worst noise: 0.925 /
    0.036. 0.75 sits 1.6x above the highest real-speech no-speech reading;
    0.10 sits 4.7x below the lowest real-speech language reading and 2.8x
    above the highest noise one. Both were chosen from THIS table.

    `prompt_conditioned` is the FLEURS interaction and it is not optional. A
    <|startofprev|> prompt whose language differs from the audio INFLATES the
    no-speech logit -- an English vocabulary prompt over Chinese audio once
    zeroed every Chinese decode on FLEURS -- and since rule 2 reads the same
    softmax, an inflated no-speech mass depresses the language probability
    too, so BOTH rules inherit the hazard. When a prompt is in the decoder
    input, neither probability is evidence on its own; the reference rule
    (decoding.py) pairs it with avg_logprob < -1.0, because real speech under
    a mismatched prompt still decodes confidently while noise regurgitating
    the prompt's vocabulary does not. `confirms()` is that second half. The
    naive version -- reject on the probability alone -- is what would kill
    biased Chinese decoding outright.

    beam_search feeds no prompt today, so it constructs the gate with
    prompt_conditioned=False from the fact that its own prefix starts at SOT.
    If it ever learns <|startofprev|> conditioning, that flag must follow, or
    FLEURS zeroes again.
    """

    __slots__ = (
        "no_speech_id",
        "no_speech_threshold",
        "certain_threshold",
        "language_prob_threshold",
        "language_corroboration_threshold",
        "avg_logprob_threshold",
        "prompt_conditioned",
    )

    def __init__(
        self,
        *,
        no_speech_id: int | None,
        no_speech_threshold: float | None,
        language_prob_threshold: float | None,
        certain_threshold: float = 0.90,
        language_corroboration_threshold: float = 0.50,
        avg_logprob_threshold: float,
        prompt_conditioned: bool,
    ) -> None:
        # Required and keyword-only, for the reason BiasTrie.__init__ spells
        # out at length: a caller that inherits an operating point it never
        # chose is how this file shipped delta=4.0 into production twice.
        self.no_speech_id = None if no_speech_id is None else int(no_speech_id)
        self.no_speech_threshold = (
            None if no_speech_threshold is None else float(no_speech_threshold)
        )
        # The bar above which a chunk may be rejected WITHOUT decoding, on the
        # probability alone. Distinct from no_speech_threshold on purpose: that
        # one decides "suspicious enough to check", this one decides "certain
        # enough not to bother checking". 0.90 sits above every real-speech
        # reading in the table above (worst 0.471) and below every noise one
        # (lowest 0.925), so it buys the decode-skip on exactly the cases that
        # caused this defect while leaving the thin middle to confirms().
        self.certain_threshold = float(certain_threshold)
        self.language_prob_threshold = (
            None if language_prob_threshold is None else float(language_prob_threshold)
        )
        # Rule 2 needs a second opinion for the same reason rule 1 does under a
        # prompt: P(lang) is read off the SAME softmax as P(nospeech) and is not
        # renormalised inside the language range, so a chunk can read low on
        # every individual language token merely because the mass is SPLIT --
        # accented or code-switched speech landing between <|en|> and <|de|> is
        # the ordinary case -- while <|nospeech|> reads near zero, i.e. the
        # model actively asserting this IS speech. Discarding that chunk whole
        # deletes confident speech, which is worse than the mislabelling rule 2
        # exists to prevent. This bar is the "no-speech does not contradict it"
        # line: above it the two signals agree the chunk is doubtful.
        self.language_corroboration_threshold = float(language_corroboration_threshold)
        self.avg_logprob_threshold = float(avg_logprob_threshold)
        self.prompt_conditioned = bool(prompt_conditioned)

    def no_speech_prob(self, sot_logprobs: np.ndarray) -> float | None:
        """P(<|nospeech|>) from the SOT-step log-softmax, or None if this
        tokenizer/vocab has no such token (the tiny fake vocabularies the
        decoder unit tests run against do not)."""
        tid = self.no_speech_id
        if tid is None or sot_logprobs is None or tid >= sot_logprobs.shape[0]:
            return None
        return float(np.exp(sot_logprobs[tid]))

    def language_prob(
        self, sot_logprobs: np.ndarray, language_id: int | None
    ) -> float | None:
        """P(the language token this decode chose), over the full vocab --
        NOT renormalised inside the language range, so the mass Whisper puts
        on <|nospeech|> counts against it, which is the point."""
        if language_id is None or sot_logprobs is None:
            return None
        if language_id >= sot_logprobs.shape[0]:
            return None
        return float(np.exp(sot_logprobs[language_id]))

    def suspects(
        self, no_speech_prob: float | None, language_prob: float | None
    ) -> str | None:
        """The SOT-step half of the verdict: a reason string, or None.

        "suspects", not "rejects": under a prompt this is only half the
        evidence and must be confirmed by `confirms()` -- see the class
        docstring for what happens to Chinese if it isn't.
        """
        if (
            self.no_speech_threshold is not None
            and no_speech_prob is not None
            and no_speech_prob > self.no_speech_threshold
        ):
            return "no_speech"
        if (
            self.language_prob_threshold is not None
            and language_prob is not None
            and language_prob < self.language_prob_threshold
            # The conjunction. `no_speech_prob is None` only happens on the
            # tiny fake vocabularies the decoder unit tests build, which carry
            # no <|nospeech|> token at all; there is no second opinion to be
            # had, so the rule stands alone exactly as it used to.
            and (
                no_speech_prob is None
                or no_speech_prob >= self.language_corroboration_threshold
            )
        ):
            return "language"
        return None

    def confirms(self, avg_logprob: float | None) -> bool:
        """Second half of the reference rule, required only when a prompt is
        conditioning the decode. None (nothing was decoded) does not confirm."""
        return avg_logprob is not None and avg_logprob < self.avg_logprob_threshold


# ---------------------------------------------------------------------------
# Beam search over the ONNX decoder session
# ---------------------------------------------------------------------------


class _Beam:
    __slots__ = ("tokens", "score", "kv", "bias_state", "acoustic", "fired",
                 "cmd_state", "cmd_release")

    def __init__(self, tokens, score, kv, bias_state, acoustic=0.0, fired=(),
                 cmd_state=None, cmd_release=None) -> None:
        self.tokens = tokens  # list[int], includes FORCED_PREFIX (and EOT at end)
        self.score = score  # cumulative (un-normalized) log score incl. bias
        self.kv = kv  # dict[str, np.ndarray]: self-KV *_in tensors
        self.bias_state = bias_state  # BiasState | None
        # Sum of the RAW log-softmax of each generated token, i.e. `score`
        # without L1's delta, the completion bonus or L2's density ratio.
        # avg_logprob is a claim about how confident the ACOUSTICS were; if the
        # lexicon could raise it, a big enough bias trie would talk the
        # no-speech gate out of firing on the very noise that regurgitates the
        # lexicon. Kept separately for that reason, not derived from `score`.
        self.acoustic = acoustic
        # ``(phrase_id, pos, n)`` per FIRST phrase completion on this path:
        # `pos` is the generated-token index (0 = first token after the
        # forced prefix) of the completing token, `n` the matched length.
        # Derived from `bias_state.fired` (which carries no positions) as the
        # beam steps; reported under stats["nbest"][k]["fired"].
        self.fired = fired
        # L1b closed-set command prefix. `cmd_state` is None unless the caller
        # armed one, so a decode without `command_prefix` carries exactly the
        # state it carried before this layer existed.
        self.cmd_state = cmd_state
        # How many content positions the constraint covered on this path
        # before a catalogue phrase COMPLETED (None = none completed: the beam
        # walked off the closed set, ran out of max_tokens, or was never
        # armed). Observability only -- nothing in the scoring reads it -- and
        # it is the number a device run needs in order to say whether the
        # payload decoded free.
        self.cmd_release = cmd_release


def _fired_after(parent_fired: tuple, before: "BiasState | None",
                 after: "BiasState | None", pos: int) -> tuple:
    """`parent_fired` extended with whatever `advance` just completed."""
    if after is None or before is None or len(after.fired) == len(before.fired):
        return parent_fired
    return parent_fired + tuple(
        (pid, pos, n) for pid, n in after.fired[len(before.fired):])


def _cmd_release_after(parent_release, before: "CommandPrefixState | None",
                       after: "CommandPrefixState | None", pos: int):
    """Content position count at which a catalogue phrase COMPLETED here.

    `pos` is the index of the token just consumed, so completing on that token
    means the constraint covered `pos + 1` positions and everything after is
    free. Recorded once; None means no phrase completed inside the window --
    the beam either walked off the closed set or ran out of `max_tokens` --
    which is itself the signal that this hold asked for nothing in the
    catalogue. Deliberately NOT `after.done`: walking off releases too, and
    reporting that as a completion would tell the host a workflow was
    recognised when none was."""
    if before is None or after is None or parent_release is not None:
        return parent_release
    return pos + 1 if (after.completed and not before.completed) else None


def _log_softmax(logits_slice: np.ndarray) -> np.ndarray:
    lp = logits_slice.astype(np.float32)
    lp -= lp.max()
    lp -= np.log(np.exp(lp).sum())
    return lp


def _normalized(
    score: float, tokens: list[int], length_penalty: float, prefix_len: int
) -> float:
    """Beam score per GENERATED token.

    `prefix_len` is the length of the prefix this decode actually forced, not
    `len(FORCED_PREFIX)`. Both base and turbo happen to produce a four-token
    prefix, so this is the same arithmetic today — it is passed rather than
    assumed because the file already contemplates a <|startofprev|>-conditioned
    beam path, and a base-derived constant standing in for a per-decode
    quantity is the exact shape of every defect this module just removed."""
    gen = max(len(tokens) - prefix_len, 1)
    return score / (gen**length_penalty)


def _resolve_prefix(profile, tokenizer, *, want_timestamps: bool = False):
    """``(prefix, sot_id, eot_id, lang_slot)`` for one decode.

    Shared by `beam_search` and `forced_logprobs` so the two can never
    disagree about the prefix -- see the comment block in `beam_search` for
    why the prefix is resolved from the MODEL and never from constants.
    """
    lang_slot = None
    if profile is None and tokenizer is not None:
        profile = WhisperProfile.from_tokenizer(
            tokenizer, model="the tokenizer handed to beam_search")
    if profile is not None:
        prefix = profile.forced_prefix
        if want_timestamps:
            # Drop <|notimestamps|> so the model decodes timestamp tokens
            # freely (same as greedy _transcribe_chunk want_timestamps=True).
            # The finalize's transcribe_timed needs these to build spans.
            prefix = [t for t in prefix if t != profile.notimestamps]
        lang_slot = profile.language_range  # inclusive id range
        sot_id, eot_id = profile.sot, profile.eot
    else:
        prefix = list(FORCED_PREFIX)
        sot_id, eot_id = SOT, EOT
    return list(prefix), sot_id, eot_id, lang_slot


def _stepper(decoder_session, cross_kv: dict[str, np.ndarray], max_len: int):
    """The per-step decoder call both decoders share.

    Returns ``(run_step, attention_mask, kv0)``: ``run_step(kv, token, n)``
    gives ``(log_softmax over the vocab, new self-KV)``; the caller opens
    position ``n`` in the right-aligned ``attention_mask`` before each call
    (it depends only on the step index, so it is shared across beams).
    """
    self_shapes = {
        i.name: tuple(i.shape)
        for i in decoder_session.get_inputs()
        if "_cache_self_" in i.name
    }
    n_layers = sum(1 for n in self_shapes if n.startswith("k_cache_self_"))
    out_names = [o.name for o in decoder_session.get_outputs()]
    attention_mask = np.full((1, 1, 1, max_len), MASK_NEG, dtype=np.float16)

    def run_step(kv: dict[str, np.ndarray], token: int, n: int):
        inputs = dict(cross_kv)
        inputs.update(kv)
        inputs["input_ids"] = np.array([[token]], dtype=np.int32)
        inputs["attention_mask"] = attention_mask
        inputs["position_ids"] = np.array([n], dtype=np.int32)
        outs = dict(zip(out_names, decoder_session.run(None, inputs)))
        new_kv = {
            f"{p}_cache_self_{i}_in": outs[f"{p}_cache_self_{i}_out"]
            for i in range(n_layers)
            for p in ("k", "v")
        }
        return _log_softmax(outs["logits"][0, :, 0, 0]), new_kv

    kv0 = {name: np.zeros(shape, dtype=np.float16) for name, shape in self_shapes.items()}
    return run_step, attention_mask, kv0


def beam_search(
    decoder_session,
    cross_kv: dict[str, np.ndarray],
    tokenizer=None,
    beam_size: int = 4,
    bias: BiasTrie | None = None,
    density_ratio: DensityRatio | None = None,
    command_prefix: "CommandPrefix | None" = None,
    max_len: int = MEAN_DECODE_LEN,
    length_penalty: float = 0.6,
    speech_gate: "SpeechGate | None" = None,
    forced_language_id: int | None = None,
    stats: dict | None = None,
    profile: "WhisperProfile | None" = None,
    want_timestamps: bool = False,
    yield_fn=None,
) -> list[int]:
    """Beam-search decode one 30s chunk. Returns best token ids (specials included).

    speech_gate: reject non-speech chunks (see SpeechGate). A rejected chunk
    returns just ``[sot]``, which detokenizes to the empty string -- the same
    shape the greedy path returns from its own gate, so callers that already
    handle greedy's ``[SOT]`` need no new case.

    forced_language_id: pin the language slot to this token instead of letting
    the model pick. None keeps auto-detect (unchanged). Auto-detect is what
    turned 1.0 s of hiss into Korean rather than into English gibberish:
    whisper-base's language head is a lottery on short or quiet audio, and the
    host has never sent a language (see the backend transcribe contract).

    stats: optional dict, filled with the gate's measurements
    (no_speech_prob / language_prob / avg_logprob / no_speech + reason). The
    numbers a field report needs, without a second decode to recover them.
    Also carries ``generated`` (how many tokens the winner produced past the
    forced prefix -- the denominator of ``avg_logprob``, so a caller merging
    several chunks can weight by it) and ``nbest``: every hypothesis that
    SURVIVED the search, best first, as ``{"tokens", "score", "avg_logprob"}``
    -- the winner is ``nbest[0]`` and its tokens are exactly the return value.
    ``score`` is the same length-normalized, bias-inclusive number the winner
    was picked by (``final_score``); ``avg_logprob`` is the per-hypothesis raw
    acoustic mean, bias-free for the reason given on ``_Beam.acoustic``.

    Why the runners-up are worth keeping: with beam 4 the decoder already paid
    for up to four complete hypotheses and then threw three away. The gap
    between ``nbest[0].score`` and ``nbest[1].score`` is the cheapest honest
    confidence signal this decoder has -- a near-tie means the audio did not
    decide, and a per-phrase bias that flips a near-tie is doing its job while
    one that flips a clear win is hallucinating. The continual-personal-
    learning loop reads exactly that. Nothing here is pushed onto the wire by
    default: ``beam_search``'s RETURN value is unchanged, so a caller that
    passes no ``stats`` dict sees byte-identical behaviour.

    command_prefix: L1b closed-set constraint over the first `max_tokens`
    GENERATED content positions (see CommandPrefix). None -- every caller
    before this layer, and every notes request -- makes this function
    byte-identical to what it was: no state is allocated, `prepare` takes the
    same branch it always did, and `stats` grows no key. It is a per-request
    switch by construction; there is no module-level default to inherit.

    profile: the model's derived identity (WhisperProfile). Pass the one the
    engine built at load rather than making this function re-derive it per
    chunk — and so that a model this worker cannot drive fails at LOAD, named,
    instead of at the user's first hold.
    """
    # Resolve the forced prefix from the MODEL, never from constants:
    # FORCED_PREFIX's [50258, 50259, 50359, 50363] is whisper-BASE's
    # [sot, en, transcribe, notimestamps] — but large-v3's vocab shifted, so
    # on turbo those very ids mean [sot, en, TRANSLATE, NOSPEECH]. Every
    # beam decode on the shipped engine ran in English+translate mode with
    # a nospeech token in the prompt — the A9-qnn measurement's 73.76% zh
    # CER in one line of dead constants. The language slot is chosen by the
    # MODEL (argmax over the language-token range at that step), matching
    # greedy's auto-detect.
    #
    # `profile` is normally passed in by the backend (derived once at load).
    # When only a tokenizer is given it is derived here. When NEITHER is given
    # — the tokenizer-less unit harnesses, and nothing else — the base
    # constants stand in, which is safe precisely because those harnesses run
    # ten-token vocabularies where an id in the 50k range is unreachable. What
    # is gone is the middle case: a real tokenizer that fails to resolve one
    # name used to fall THROUGH to base's ids. It now raises.
    prefix, sot_id, eot_id, lang_slot = _resolve_prefix(
        profile, tokenizer, want_timestamps=want_timestamps)
    # Timestamp carve-out for prepare()'s special-token wall. Everything at or
    # above SOT is a special, and prepare() suppresses the whole block — which
    # also made every timestamp token unsamplable, so a want_timestamps beam
    # decode could never emit one and transcribe_timed collapsed each 30 s
    # window into a single edge-clamped span. On the rolling-stable pass that
    # meant no span ever cleared the commit line until the tail flush: the
    # saved meeting transcript was silently its final <=30 s (proven on the
    # staged QNN engine, 2026-08-24 — 0 timestamp tokens on beam AND greedy).
    # With the floor set, [sot, ts_floor) stays walled — including
    # <|notimestamps|>, which sits just below it, so the beam is locked into
    # timestamp mode the same way the fixed greedy path is.
    ts_floor = (profile.timestamp_begin
                if want_timestamps and profile is not None
                and profile.timestamp_begin is not None else None)
    run_step, attention_mask, kv = _stepper(decoder_session, cross_kv, max_len)

    # --- Forced prefix: run it like normal steps, but with the token chosen ---
    logprobs = None
    sot_logprobs = None  # the distribution the no-speech verdict is read from
    language_id = None
    for n in range(len(prefix)):
        token = prefix[n]
        if token == -1:
            # language slot: forced when the caller pinned one, otherwise the
            # model picks, restricted to language tokens
            lo, hi = lang_slot
            if forced_language_id is not None and lo <= forced_language_id <= hi:
                token = int(forced_language_id)
            else:
                token = int(lo + np.argmax(logprobs[lo:hi + 1]))
            prefix[n] = token
            language_id = token
        attention_mask[0, 0, 0, max_len - n - 1] = 0.0
        logprobs, kv = run_step(kv, token, n)
        if n == 0:
            # Step 0 consumed SOT, so this is the SOT-step distribution -- the
            # same one greedy reads _no_speech_prob off. It must be captured
            # here: by the end of the prefix `logprobs` has moved on three
            # steps to the first text position, where <|nospeech|> means
            # nothing at all.
            sot_logprobs = logprobs

    # --- Non-speech verdict, before a single beam step is spent -------------
    suspicion = None
    if speech_gate is not None:
        ns_p = speech_gate.no_speech_prob(sot_logprobs)
        lang_p = measured_lang_p = speech_gate.language_prob(sot_logprobs, language_id)
        if forced_language_id is not None:
            # The language rule asks "did this decode COMMIT to a language, or
            # pick one out of a hat?". When the caller pinned the language
            # there was no choice to be confident about, so the reading is not
            # evidence of anything -- it is just the model disagreeing with
            # the pin. Measured on an X Elite laptop: real Chinese speech with <|en|>
            # forced reads P(en)=0.0023, which the 0.10 rule would reject as
            # noise. The host's eventual fix for this whole bug is to start
            # sending a language; had this line not been here, that fix would
            # have silently swallowed every utterance in any other language.
            # The no-speech rule is untouched -- it never asked about the
            # language slot.
            lang_p = None
        if stats is not None:
            stats["no_speech_prob"] = ns_p
            # Reported even when it is not gated on, so a field report can
            # still show how far the pinned language was from the model's.
            stats["language_prob"] = measured_lang_p
            stats["language_forced"] = forced_language_id is not None
        suspicion = speech_gate.suspects(ns_p, lang_p)
        # certain_threshold belongs to the NO-SPEECH rule alone. Rule 2 fires
        # precisely when P(nospeech) is BELOW the no-speech bar, so demanding
        # 0.90 of it there does not make rule 2 careful -- it deletes rule 2,
        # and with it the measured Korean-repetition case (P(nospeech)=0.650,
        # P(lang)=0.067) that rule 2 was written for. Rule 2 carries its own
        # second opinion inside suspects() instead: the corroboration bar.
        # ns_p is None-guarded because a vocabulary without a <|nospeech|>
        # token yields None here, and rule 2 can reach this line on it.
        if suspicion == "no_speech":
            certain = (
                ns_p is not None and ns_p >= speech_gate.certain_threshold
            )
        else:
            certain = True
        if suspicion is not None and not speech_gate.prompt_conditioned and certain:
            # Rejected before decoding ONLY when the reading is far past
            # anything real speech has been measured at. Everything else falls
            # through to the post-decode check, which pairs the probability
            # with avg_logprob.
            #
            # The bar is deliberately not NO_SPEECH_THRESHOLD. Rejecting on a
            # single softmax reading at 0.75 is exactly what this worker's
            # macOS half refuses to do, and says why at whisper_mac.py's
            # drop_no_speech_segments: "BOTH conditions, deliberately:
            # quiet-but-real speech scores high no_speech_prob with good
            # logprob and must survive". The measured margin here is thinner
            # than it looks -- the worst REAL speech in the device sweep read
            # 0.471, against a 0.75 bar, on two languages and one device. A
            # user whose sentence is silently deleted has a worse bug than the
            # Korean one: that one at least leaves evidence on screen.
            #
            # Above certain_threshold no measured speech comes close, so the
            # early return still buys the ~437 ms of decode
            # (the platform measurements 8.1, quoted) on the cases that
            # actually caused this defect -- device noise read 0.925-0.956.
            if stats is not None:
                stats["no_speech"] = True
                stats["no_speech_reason"] = suspicion
            return [prefix[0]]

    def prepare(
        lp: np.ndarray, bias_state: BiasState | None, context: tuple[int, ...] = (),
        cmd_state: "CommandPrefixState | None" = None, pos: int = 0,
    ) -> np.ndarray:
        lp = lp.copy()
        # Everything from SOT up is a special (language, task, timestamps).
        # `sot_id` is the resolved one, not the module constant: on a
        # checkpoint whose block starts elsewhere, suppressing from 50258
        # would leave real specials samplable and delete real text tokens.
        # When the caller asked for timestamps, the wall stops at ts_floor so
        # timestamp tokens stay samplable (see the ts_floor comment above).
        if ts_floor is None:
            lp[sot_id:] = -np.inf
        else:
            lp[sot_id:ts_floor] = -np.inf
        if bias_state is not None:
            # Charge through `delta_for_token`, never the scalar. This line
            # used to add `trie.delta` directly, which made `set_phrase_deltas`
            # refund-only on THIS decoder: `advance` computed the per-phrase
            # amount to roll back while selection had only ever added the
            # uniform one, so a per-phrase map changed which dead matches got
            # refunded and never which token won -- the macOS decoder
            # (`trie_decode.BiasBeamSearchDecoder`) already charged per token.
            # Found by the setup-2 contract test "a delta on phrase B flips a
            # near-tie toward B", which did not flip. With no map set,
            # `delta_for_token` returns `trie.delta`, so the uniform path is
            # byte-identical by construction.
            trie, nodes = bias_state.trie, bias_state.nodes
            for t in bias_state.candidate_tokens():
                if t < sot_id:
                    # Corroborated independently on main, on hardware: giving
                    # one phrase its own δ and sweeping it 0 -> 25 moved the output
                    # by not one character across 76 clips on an X Elite, with
                    # instrumentation confirming the beam path was taken and
                    # the phrase list reached the engine. Two findings, two
                    # methods, one line.
                    lp[t] += trie.delta_for_token(nodes, t)
        if density_ratio is not None:
            lp[:sot_id] += density_ratio.score_vector(context)[:sot_id]
        # L1b LAST, purely for readability: all three layers are additive
        # vectors, so the arithmetic is order-independent and this cannot
        # overwrite L1's per-token delta or L2's ratio -- it only adds a
        # constant to everything the closed set excludes.
        if cmd_state is not None:
            cmd_state.apply(lp, pos, sot_id, eot_id)
        return lp

    init_bias = bias.initial_state() if bias is not None else None
    init_cmd = command_prefix.initial_state() if command_prefix is not None else None
    beams: list[_Beam] = []
    # Content position 0: the first token after the forced prefix, which is
    # where `note` lost to `No`. `context` is empty here for the same reason
    # `pos` is 0 -- nothing has been generated yet.
    lp0 = prepare(logprobs, init_bias, (), init_cmd, 0)
    for t in np.argpartition(-lp0, beam_size)[:beam_size]:
        t = int(t)
        score = float(lp0[t])
        bs, extra = (init_bias.advance(t) if init_bias is not None else (None, 0.0))
        cs = init_cmd.advance(t, 0) if init_cmd is not None else None
        # All initial beams share the prefix KV (immutable), but copy on branch.
        beams.append(
            _Beam(prefix + [t], score + extra, dict(kv), bs, float(logprobs[t]),
                  _fired_after((), init_bias, bs, 0),
                  cs, _cmd_release_after(None, init_cmd, cs, 0))
        )

    finished: list[_Beam] = []
    active = [b for b in beams if b.tokens[-1] != eot_id]
    finished.extend(b for b in beams if b.tokens[-1] == eot_id)

    for n in range(len(prefix), max_len - 1):
        if not active:
            break
        if yield_fn is not None:
            # Cooperative preemption between beam steps (engine-lock
            # occupancy fix, 2026-08-27): every beam's per-step state (its
            # tokens + KV arrays) lives in Python and the decoder session
            # is stateless across run() calls, so a waiting live-caption /
            # dictation decode can borrow the engine here and this window
            # resumes exactly where it left off. Passed only by the
            # background rolling pass; every other caller is unchanged.
            yield_fn()
        attention_mask[0, 0, 0, max_len - n - 1] = 0.0
        candidates: list[_Beam] = []
        for beam in active:
            raw_lp, new_kv = run_step(beam.kv, beam.tokens[-1], n)
            # Context for L2 is generated content only -- FORCED_PREFIX specials
            # warm up KV but were never counted into P_you/P_ilm's n-grams either
            # (see corpus.py; P_ilm's distiller never shipped and is no longer in
            # this tree), so they must not leak in here.
            content_ctx = tuple(beam.tokens[len(prefix) :])
            # The content position about to be generated == how many content
            # tokens this beam already has. Same quantity `content_ctx` is
            # built from, so L1b and L2 can never disagree about where we are.
            content_pos = len(content_ctx)
            lp = prepare(raw_lp, beam.bias_state, content_ctx,
                         beam.cmd_state, content_pos)
            # 2*beam_size candidates per beam so EOT hypotheses don't crowd
            # out continuations (HF generation convention).
            k = min(2 * beam_size, lp.shape[0] - 1)
            top = np.argpartition(-lp, k)[:k]
            for t in top:
                t = int(t)
                if not np.isfinite(lp[t]):
                    continue
                score = beam.score + float(lp[t])
                if beam.bias_state is not None:
                    bs, extra = beam.bias_state.advance(t)
                    score += extra
                else:
                    bs = None
                cs = (beam.cmd_state.advance(t, content_pos)
                      if beam.cmd_state is not None else None)
                # Siblings share the parent's new_kv dict for now; survivors
                # that actually share get forked (np copy) after pruning.
                candidates.append(
                    _Beam(
                        beam.tokens + [t], score, new_kv, bs,
                        beam.acoustic + float(raw_lp[t]),
                        _fired_after(beam.fired, beam.bias_state, bs, n + 1 - len(prefix)),
                        cs,
                        _cmd_release_after(beam.cmd_release, beam.cmd_state, cs,
                                           content_pos),
                    )
                )
        candidates.sort(key=lambda b: b.score, reverse=True)
        active = []
        for cand in candidates:
            if cand.tokens[-1] == eot_id:
                finished.append(cand)
            elif len(active) < beam_size:
                active.append(cand)
        # Fork = np copy on branch: give surviving siblings their own KV arrays.
        seen_kv: set[int] = set()
        for cand in active:
            if id(cand.kv) in seen_kv:
                cand.kv = {name: arr.copy() for name, arr in cand.kv.items()}
            else:
                seen_kv.add(id(cand.kv))
        # Termination (HF-style, early_stopping=False): keep the best beam_size
        # finished hypotheses; stop when the worst of them already beats the
        # best score any active beam could reach at its current length.
        finished.sort(
            key=lambda b: _normalized(
                b.score, b.tokens, length_penalty, len(prefix)
            ),
            reverse=True,
        )
        del finished[beam_size:]
        if len(finished) >= beam_size and active:
            worst_kept = _normalized(
                finished[-1].score, finished[-1].tokens, length_penalty,
                len(prefix),
            )
            best_active = max(
                _normalized(b.score, b.tokens, length_penalty, len(prefix))
                for b in active
            )
            if worst_kept >= best_active:
                break

    # Beams still active at the length limit compete without an EOT; settle any
    # unconfirmed (pending) bias boost before ranking them.
    def final_score(b: _Beam) -> float:
        score = b.score
        if b.bias_state is not None and b.tokens[-1] != eot_id:
            score -= b.bias_state.pending
        return _normalized(score, b.tokens, length_penalty, len(prefix))

    best = max(finished + active, key=final_score)

    # --- Deferred half of the verdict, prompt-conditioned decodes only ------
    # Unreachable while beam_search feeds no <|startofprev|> prefix, and kept
    # anyway because the day it does, this is the branch that has to already
    # be here: rejecting on the inflated probability alone is what zeroed
    # every Chinese decode on FLEURS. Real speech under a mismatched-language
    # prompt keeps a healthy avg_logprob and survives; noise reciting the
    # prompt's vocabulary is low-confidence and dies.
    generated = max(len(best.tokens) - len(prefix), 0)
    avg_logprob = best.acoustic / generated if generated else None
    if stats is not None:
        stats["avg_logprob"] = avg_logprob
    if suspicion is not None and speech_gate.confirms(avg_logprob):
        if stats is not None:
            stats["no_speech"] = True
            stats["no_speech_reason"] = suspicion
        # No N-best for a rejected chunk: the hypotheses exist, but reporting
        # alternatives for audio the gate just called non-speech would hand
        # the host a confidence signal about text it must not show.
        return [prefix[0]]
    if stats is not None and command_prefix is not None:
        # Present ONLY when the caller armed one, so a request without the
        # field produces the same stats dict it produced yesterday. `release`
        # is how many content positions the constraint covered on the WINNING
        # path before a catalogue phrase completed -- the number that says
        # whether the payload decoded free, which is the property a device run
        # has to be able to check. null = no phrase completed in the window,
        # i.e. this hold asked for nothing in the catalogue.
        stats["command_prefix"] = {
            **command_prefix.describe(),
            "release": best.cmd_release,
        }
    if stats is not None:
        stats["generated"] = generated
        # Every surviving hypothesis, ranked by the SAME number the winner was
        # chosen by. `finished` and `active` are disjoint by construction
        # (a candidate lands in exactly one), so no dedup is needed; ties are
        # left in ranking order, which is deterministic because `max` above
        # and this sort see the same keys.
        ranked = sorted(finished + active, key=final_score, reverse=True)
        nbest = []
        for b in ranked:
            gen_b = max(len(b.tokens) - len(prefix), 0)
            nbest.append({
                "tokens": list(b.tokens),
                "score": float(final_score(b)),
                "avg_logprob": (b.acoustic / gen_b) if gen_b else None,
                # Carried per hypothesis so a multi-chunk caller can weight
                # without re-deriving the prefix length from a constant.
                "generated": gen_b,
                # Phrase completions on THIS path as (phrase_id, pos, n); None
                # when no trie was live (the host reads None as "not
                # measured" and [] as "measured, nothing completed").
                "fired": (list(b.fired) if bias is not None else None),
            })
        stats["nbest"] = nbest
    return best.tokens


# ---------------------------------------------------------------------------
# Forced scoring (the verifier primitive; loop-v2 `score` op)
# ---------------------------------------------------------------------------
#
# Port of loop-v2/verifier/forced_score.py (HF reference) onto the ORT
# decoder: teacher-force `prefix + ids + [EOT]` one token at a time through
# the SAME `run_step` beam search uses, and read log_softmax[target] at each
# step. No trie, no delta, no density ratio, no specials mask (the reference
# reads the raw log-softmax, and so does this). The window arithmetic is
# copied token for token from `ForcedScorer.window` / `ForcedScorer.delta`.


def forced_logprobs(
    decoder_session,
    cross_kv: dict[str, np.ndarray],
    token_ids: list[int],
    *,
    profile: "WhisperProfile | None" = None,
    tokenizer=None,
    forced_language_id: int | None = None,
    max_len: int = MEAN_DECODE_LEN,
) -> np.ndarray:
    """``log p(token_i | prefix, tokens<i, x)`` for every token in ``token_ids``
    plus the closing EOT -- one float per entry of ``token_ids + [EOT]``,
    exactly `ForcedScorer.token_logprobs`' return.

    The language slot of the forced prefix is pinned to ``forced_language_id``
    when given; otherwise the model's argmax at that step (auto-detect), the
    same rule `beam_search` applies. Raises ``ValueError`` when the forced
    sequence does not fit the decoder's context (``max_len``).
    """
    prefix, sot_id, eot_id, lang_slot = _resolve_prefix(profile, tokenizer)
    full = list(prefix) + [int(t) for t in token_ids] + [eot_id]
    if len(full) > max_len:
        raise ValueError(
            f"string exceeds decode context ({len(full)} tokens > {max_len})")
    run_step, attention_mask, kv = _stepper(decoder_session, cross_kv, max_len)
    logprobs = None
    out: list[float] = []
    for n in range(len(full) - 1):
        token = full[n]
        if token == -1:
            lo, hi = lang_slot
            if forced_language_id is not None and lo <= forced_language_id <= hi:
                token = int(forced_language_id)
            else:
                token = int(lo + np.argmax(logprobs[lo:hi + 1]))
            full[n] = token
        attention_mask[0, 0, 0, max_len - n - 1] = 0.0
        logprobs, kv = run_step(kv, token, n)
        target = full[n + 1]
        if target == -1:
            # The language slot is scored under whichever token fills it;
            # resolve it now so the reference alignment (one entry per
            # input position) holds. Only reachable with an unpinned prefix.
            lo, hi = lang_slot
            target = (int(forced_language_id)
                      if forced_language_id is not None and lo <= forced_language_id <= hi
                      else int(lo + np.argmax(logprobs[lo:hi + 1])))
            full[n + 1] = target
        out.append(float(logprobs[target]))
    return np.asarray(out[len(prefix) - 1:], dtype=np.float64)


def forced_window(orig_ids: list[int], corr_ids: list[int], right_ctx: int = 1):
    """Minimal differing token window in both strings, + right context.

    ``((o0, o1), (c0, c1))`` as ``[start, end)`` offsets into
    ``orig_ids + [EOT]`` / ``corr_ids + [EOT]`` -- the ``+1`` on the clamp is
    what lets the right context include EOT when the diff reaches the end.
    Identical strings give two empty windows. Verbatim `ForcedScorer.window`.
    """
    import difflib

    sm = difflib.SequenceMatcher(a=list(orig_ids), b=list(corr_ids), autojunk=False)
    ops = [o for o in sm.get_opcodes() if o[0] != "equal"]
    if not ops:
        return (0, 0), (0, 0)
    o0, o1 = ops[0][1], ops[-1][2]
    c0, c1 = ops[0][3], ops[-1][4]
    o1 = min(o1 + right_ctx, len(orig_ids) + 1)  # +1 may include EOT
    c1 = min(c1 + right_ctx, len(corr_ids) + 1)
    return (o0, o1), (c0, c1)


def forced_delta(lp_orig: np.ndarray, lp_corr: np.ndarray,
                 orig_ids: list[int], corr_ids: list[int]) -> dict:
    """`ForcedScorer.delta` at lambda = 0: unnormalised window sums and their
    difference, plus the whole-string sums for diagnostics."""
    (o0, o1), (c0, c1) = forced_window(orig_ids, corr_ids)
    so = float(np.sum(lp_orig[o0:o1]))
    sc = float(np.sum(lp_corr[c0:c1]))
    return {
        "acoustic": sc - so,
        "window": {"orig": [o0, o1], "corr": [c0, c1],
                   "len": max(o1 - o0, c1 - c0)},
        "sum_orig": so, "sum_corr": sc,
        "full_orig": float(np.sum(lp_orig)), "full_corr": float(np.sum(lp_corr)),
        "tokens": {"orig": len(orig_ids), "corr": len(corr_ids)},
    }
