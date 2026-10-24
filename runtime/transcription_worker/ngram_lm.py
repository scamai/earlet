"""Token-level Stupid Backoff n-gram LM (L2 density-ratio building block).

Used for both `P_you` (a user's personal transcript history, hard-counted)
and `P_ILM` (Whisper's own audio-independent internal LM, soft-counted from
its own output distribution by an offline, Snapdragon-only distiller that is
no longer in this tree). Both feed the same
density-ratio term in beam_decode.py's prepare() hook:

    mu * P_you.score_vector(ctx) - lam * P_ilm.score_vector(ctx)

Stupid Backoff (Brants et al. 2007) rather than a normalized/discounted
model (Kneser-Ney, KenLM) deliberately: its scores are *not* true
probabilities, so no normalization over the 51865-token vocab is needed at
lookup time -- context recursion plus a couple of dict lookups per order.
That's what keeps this cheap enough to call every beam, every decode step.
No native/compiled dependency (KenLM needs a C++/Boost build, painful to
guarantee on ARM64 Windows), just numpy + stdlib.

score_vector() returns *log*-scale scores (natural log), since callers add
them directly into beam_decode's log-probability array.
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np

# Whisper-BASE's multilingual vocabulary width. large-v3 and large-v3-turbo
# are 51866 (they added <|yue|>), so this is a per-model quantity standing in a
# module constant -- deliberately, and it is the ONE place in this package
# where that is defensible:
#
#   * beam_decode's prepare() only ever reads `score_vector(ctx)[:sot]`, i.e.
#     the first 50258 entries, and text token ids 0..50256 are IDENTICAL across
#     every whisper generation. A one-token shortfall at the far end is never
#     indexed, so it produces neither a shape error nor a wrong value.
#   * The real hazard is downstream: save/load persist vocab_size inside the
#     artifact, so the shipped p_ilm.json.gz is stamped at whatever width it was
#     distilled at, and any future code that reads `lm.vocab_size` as "the
#     model's vocabulary" inherits base's number. DensityRatio now refuses two
#     LMs that disagree about it, which is the place a swap would show up.
#
# Callers that know the model should pass `vocab_size=profile.vocab_size`
# (whisper_profile.WhisperProfile derives it from the loaded tokenizer); this
# default is for the ones that do not.
WHISPER_VOCAB_SIZE = 51865

_LAPLACE_ALPHA = 1.0  # unigram floor smoothing; keeps every token finite


class NgramLM:
    """Order-`order` token n-gram, Stupid Backoff smoothed.

    `order=4` matches the spec's "KenLM 4-gram" (the language-model contract sec 6.2) -- i.e.
    context length up to order-1 = 3 preceding tokens.
    """

    def __init__(
        self,
        order: int = 4,
        backoff: float = 0.4,
        vocab_size: int = WHISPER_VOCAB_SIZE,
    ) -> None:
        if order < 1:
            raise ValueError("order must be >= 1")
        self.order = order
        self.backoff = float(backoff)
        self.vocab_size = int(vocab_size)
        self._log_backoff = float(np.log(self.backoff))
        # counts[k][context_tuple][token_id] = weight, k = 1..order,
        # context_tuple has length k-1 (empty tuple for k=1, unigram).
        self._counts: dict[int, dict[tuple[int, ...], dict[int, float]]] = {}
        self._unigram_log: np.ndarray = np.zeros(0)
        self._rebuild_unigram()

    # --- fitting -------------------------------------------------------

    def fit_hard(
        self,
        token_id_sequences: list[list[int]],
        weights: list[float] | None = None,
    ) -> None:
        """Hard-count n-grams from real token sequences (used for P_you)."""
        if weights is not None and len(weights) != len(token_id_sequences):
            raise ValueError("weights must be same length as token_id_sequences")
        for i, seq in enumerate(token_id_sequences):
            weight = 1.0 if weights is None else float(weights[i])
            history: tuple[int, ...] = ()
            for token in seq:
                self._observe(history, token, weight)
                history = (history + (token,))[-(self.order - 1) :]
        self._rebuild_unigram()

    def fit_soft(
        self,
        context: tuple[int, ...],
        prob_vector: np.ndarray,
        top_k: int = 32,
        weight: float = 1.0,
    ) -> None:
        """Weighted-count from a model's own output distribution.

        Used to distill P_ILM from the zeroed-cross-attention decoder: rather
        than hard-counting a single sampled/argmax token per context, spread
        `weight * prob` across the top_k most likely continuations. Only the
        top_k are kept (not the full 51865-wide distribution) so the count
        table stays sparse -- the long tail contributes negligible mass and
        would otherwise blow up storage for no real gain in fidelity.
        """
        history = tuple(context)[-(self.order - 1) :]
        k = min(top_k, prob_vector.shape[0])
        top_ids = np.argpartition(-prob_vector, k - 1)[:k]
        for token in top_ids:
            p = float(prob_vector[int(token)])
            if p <= 0.0:
                continue
            self._observe(history, int(token), weight * p)
        self._rebuild_unigram()

    def _observe(self, history: tuple[int, ...], token: int, weight: float) -> None:
        for k in range(1, self.order + 1):
            ctx_len = k - 1
            if len(history) < ctx_len:
                continue
            ctx = history[-ctx_len:] if ctx_len else ()
            bucket = self._counts.setdefault(k, {}).setdefault(ctx, {})
            bucket[token] = bucket.get(token, 0.0) + weight

    def _rebuild_unigram(self) -> None:
        counts = self._counts.get(1, {}).get((), {})
        total = sum(counts.values())
        denom = total + _LAPLACE_ALPHA * self.vocab_size
        vec = np.full(self.vocab_size, _LAPLACE_ALPHA, dtype=np.float64)
        if counts:
            ids = np.fromiter(counts.keys(), dtype=np.int64)
            vals = np.fromiter(counts.values(), dtype=np.float64)
            vec[ids] += vals
        self._unigram_log = np.log(vec / denom)

    # --- scoring ---------------------------------------------------------

    def score_vector(self, context: tuple[int, ...]) -> np.ndarray:
        """Dense log-score array, shape (vocab_size,), for the next token
        given `context` (only the trailing order-1 tokens are used)."""
        ctx = tuple(context)[-(self.order - 1) :]
        return self._order_vector(ctx)

    def _order_vector(self, ctx: tuple[int, ...]) -> np.ndarray:
        if not ctx:
            return self._unigram_log
        base = self._order_vector(ctx[1:]) + self._log_backoff
        counts = self._counts.get(len(ctx) + 1, {}).get(ctx)
        if not counts:
            return base
        total = sum(counts.values())
        vec = base.copy()
        ids = np.fromiter(counts.keys(), dtype=np.int64)
        vals = np.fromiter(counts.values(), dtype=np.float64)
        vec[ids] = np.log(vals / total)
        return vec

    # --- persistence -------------------------------------------------------

    def save(self, path: str | Path) -> None:
        """Gzips automatically when `path` ends in .gz -- distilled corpora
        (ILM in particular, soft-counted across many sparse high-order
        contexts) are large as raw JSON; gzip is a ~2.5x win for free with
        no format complexity, and this is a load-once-at-startup artifact
        where decompression cost is a non-issue."""
        payload = {
            "order": self.order,
            "backoff": self.backoff,
            "vocab_size": self.vocab_size,
            "counts": {
                str(k): {
                    ",".join(map(str, ctx)): {str(tok): w for tok, w in bucket.items()}
                    for ctx, bucket in ctx_map.items()
                }
                for k, ctx_map in self._counts.items()
            },
        }
        raw = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        path = Path(path)
        if path.suffix == ".gz":
            import gzip

            raw = gzip.compress(raw, compresslevel=6)
        path.write_bytes(raw)

    @classmethod
    def load(cls, path: str | Path) -> "NgramLM":
        path = Path(path)
        raw = path.read_bytes()
        if path.suffix == ".gz":
            import gzip

            raw = gzip.decompress(raw)
        payload = json.loads(raw.decode("utf-8"))
        lm = cls(
            order=payload["order"],
            backoff=payload["backoff"],
            vocab_size=payload["vocab_size"],
        )
        counts: dict[int, dict[tuple[int, ...], dict[int, float]]] = {}
        for k_str, ctx_map in payload["counts"].items():
            k = int(k_str)
            decoded_ctx_map: dict[tuple[int, ...], dict[int, float]] = {}
            for ctx_str, bucket in ctx_map.items():
                ctx = tuple(int(t) for t in ctx_str.split(",")) if ctx_str else ()
                decoded_ctx_map[ctx] = {int(tok): w for tok, w in bucket.items()}
            counts[k] = decoded_ctx_map
        lm._counts = counts
        lm._rebuild_unigram()
        return lm
