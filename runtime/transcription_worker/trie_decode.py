"""Beam decoding with the personal-phrase trie, on the openai-whisper engine.

This is the mechanism `beam_decode.BiasTrie` was written for and the only place
on macOS that can currently run it: `mlx_whisper` raises
`NotImplementedError("Beam search decoder is not yet implemented")` and
faster-whisper's beam search lives inside CTranslate2 with no hook. The
openai-whisper engine is slower than both — and it is the one that works.

It is NOT `initial_prompt` seeding. `whisper_mac.py:555` degrades bias phrases
into a prompt string for the mlx engine, which this repo measured at +14.7% WER
(development iteration 7). Here the trie adds a bounded score
to candidate tokens *inside the beam loop, before top-k*, and rolls the score
back when a partial match dies.

Provenance: the decoder below is the one an internal research harness used for
finding 02 and re-verified byte-for-byte in finding 14 — 6,120 decodes, 17 variants, zero
mismatches. Recovery 9.5% -> 60.3% at delta=1 with zero false triggers over
36,006 words of ordinary speech (Findings 17, 18). Keeping the two
implementations identical is the point; a "cleaned up" rewrite here would
silently void that evidence.
"""
from __future__ import annotations

import torch
import torch.nn.functional as F
try:
    from .beam_decode import BiasTrieCache as _BiasTrieCache
except ImportError:  # flat-directory import, same fallback build_trie uses
    from beam_decode import BiasTrieCache as _BiasTrieCache

from whisper.decoding import (
    BeamSearchDecoder,
    DecodingTask,
    MaximumLikelihoodRanker,
)

#: Operating point, re-measured on the model this engine actually loads.
#:
#: The previous value of 1.0 was selected on Whisper `base` (39M). This path
#: resolves `openai/whisper-large-v3-turbo` to the openai-whisper name `turbo`
#: (809M) via `whisper_mac.py`'s `hf_id.rsplit("-", 1)[-1]`, and the two models
#: do not agree about delta. On `turbo`, 360 clips, 21-term arm, beam 4:
#:
#:     delta   recovery   U-WER    (anchor U-WER 5.49%)
#:       0       20.6%    5.49%
#:       1       66.7%    5.07%     <- the old value
#:       2       90.5%    5.04%
#:       2.5     92.1%    5.09%
#:       3       92.1%    9.69%     <- ordinary words break here
#:
#: delta=2 was rejected for `base` on real evidence (U-WER 6.11% -> 7.58%); on
#: `turbo` that cost does not appear, because a larger model needs less
#: persuasion before its own acoustic evidence stops being overridden.
#:
#: 2.5 rather than 2.0 because it wins on every axis measured and costs nothing
#: detectable: across eight synthetic voices the mean is 89.3% against 85.5%,
#: and inside a Mandarin carrier 71.4% against 65.1%, with U-WER flat at ~5.1%.
#:
#: False triggers, on 1,200,007 words of ordinary English (internal research
#: finding 23), with the padding filtered against the corpus:
#:
#:     21 real terms, no padding, delta=2   0 events   0.0000 per 10k
#:     250 entries, clean padding, delta=2  5 events   0.0420 per 10k
#:     250 entries, clean padding, delta=0  1 event    0.0080 per 10k  (anchor)
#:
#: against a criterion of 1 per 10k. Earlier figures (1.111, 0.158) were
#: dominated by padding names that collide with words the corpus genuinely
#: speaks -- `Lorenzo` heard as a padded `lorenz` produced 12 of 19 events by
#: itself -- and are upper bounds, not estimates.
#:
#: The ceiling is 3, and both instruments agree on it independently: U-WER
#: breaks there, and it is also where false triggers first accumulate with the
#: real vocabulary.
#:
#: ## Windows ships 1.0. That is not drift, and it is not this file's to fix.
#:
#: `whisper_qnn.SHIPPED_DELTA` is 1.0 -- one global scalar applied to whatever
#: checkpoint the Snapdragon host loaded. A reader who finds only one of the
#: two constants will conclude the worker has one operating point; it has two, and
#: THREE independent things separate them. None is reconcilable by editing a
#: number:
#:
#:   * **Different decoder.** 2.5 was measured on `BiasBeamSearchDecoder`
#:     below, which charges the trie inside openai-whisper's own beam loop
#:     before top-k. Windows decodes through `beam_decode.beam_search` with
#:     `final_score` -- a separate implementation of the same idea, not a port
#:     of this one. Internal research finding 20 says in terms that an
#:     operating point does not transfer across DECODERS, so 2.5 is not
#:     evidence about that path and 1.0 is not evidence about this one.
#:   * **Different checkpoint at derivation time.** 1.0 was derived on `base`
#:     (39M); every row in the table above is `turbo` (809M). `whisper_qnn`'s
#:     own comment records that turbo is what actually loads there, so Windows
#:     is knowingly running turbo at base's number, and says so.
#:   * **Different measurement.** The rows above are 360 clips against an
#:     anchor U-WER of 5.49%; the QNN control run
#:     (an internal operating-point evaluation) anchors at
#:     3.05% on an X Elite device. Two anchors that far apart are two
#:     different corpora, so the U-WER columns are not comparable cell for
#:     cell and "their delta=2 beats our delta=2.5" is a sentence neither
#:     table can support.
#:
#: The QNN comment also records that recovery there was still climbing at
#: delta=2 (68.25% -> 88.89%) and that ITS optimum has not been located. So
#: the pair is: two decoders, two separately measured points, one of them
#: explicitly provisional. Making them equal is a MEASUREMENT -- a sweep on
#: whichever decoder's constant is moving -- and never a tidy-up. `_selftest`
#: reads `whisper_qnn.py` as source and asserts the two stay UNEQUAL, so a
#: future "restore parity" pass has to argue with a red check before it can
#: copy either number over the other.
DEFAULT_DELTA = 2.5

#: Still zero, and now for a different reason than before.
#:
#: Finding 11 measured bonus=3 reaching 74.6% against 60.3% -- but that was at
#: delta=1 on `base`, where the bonus was compensating for a delta set too low.
#: At the corrected operating point it is nearly spent: on `turbo`, delta=2 with
#: bonus=2 reaches 92.1% against delta=2 alone at 90.5%, a 1.6-point gain for a
#: second tuning parameter and a second failure mode. Delta is the control
#: variable; the bonus was its understudy.
#:
#: Windows ships the same 0.0 (`whisper_qnn.SHIPPED_COMPLETION_BONUS`), and
#: here the agreement IS meaningful -- the opposite of delta one constant up.
#: Both are zero for one shared reason, not by coincidence: Finding 11 records
#: that the bonus was frozen at zero and never measured, so no non-zero bonus
#: has been measured on ANY decoder by anyone. `beam_decode.BiasTrie` used to
#: default it to 3.0 and `whisper_qnn` used to inherit that silently; a value
#: reappearing on either side means somebody set a second tuning parameter
#: without a number behind it. `_selftest` pins the equality for that reason,
#: and it is a different assertion from the delta one on purpose.
DEFAULT_COMPLETION_BONUS = 0.0


class _TokenizerAdapter:
    """The Hugging Face-shaped `encode` that BiasTrie expects."""

    def __init__(self, tokenizer) -> None:
        self._tokenizer = tokenizer

    def encode(self, text: str, add_special_tokens: bool = False) -> list[int]:
        if add_special_tokens:
            raise ValueError("the trie never wants special tokens")
        return self._tokenizer.encode(text)


class BiasBeamSearchDecoder(BeamSearchDecoder):
    """Whisper beam search carrying one BiasState per beam.

    The unbiased path deliberately mirrors upstream exactly, so that with
    delta=0 and bonus=0 the output is token-identical to a decode with no trie
    at all. That invariant is what makes any measured difference attributable
    to the trie and nothing else.
    """

    def __init__(self, beam_size, eot, inference, patience=None, bias=None) -> None:
        super().__init__(beam_size, eot, inference, patience)
        self.bias = bias
        self.bias_states = None

    def reset(self) -> None:
        super().reset()
        self.bias_states = None

    def update(self, tokens, logits, sum_logprobs):
        if tokens.shape[0] % self.beam_size != 0:
            raise ValueError(f"{tokens.shape}[0] % {self.beam_size} != 0")
        n_audio = tokens.shape[0] // self.beam_size
        if self.finished_sequences is None:
            self.finished_sequences = [{} for _ in range(n_audio)]
        if self.bias_states is None:
            initial = self.bias.initial_state() if self.bias is not None else None
            self.bias_states = [initial for _ in range(tokens.shape[0])]
        if len(self.bias_states) != tokens.shape[0]:
            raise RuntimeError("bias state count diverged from active beam count")

        logprobs = F.log_softmax(logits.float(), dim=-1)
        next_tokens, source_indices, next_states, finished_sequences = [], [], [], []
        for audio_index in range(n_audio):
            scores, sources, states, finished = {}, {}, {}, {}
            for beam_index in range(self.beam_size):
                index = audio_index * self.beam_size + beam_index
                prefix = tokens[index].tolist()
                state = self.bias_states[index]
                candidate_logprobs = logprobs[index]
                if state is not None:
                    candidate_logprobs = candidate_logprobs.clone()
                    candidates = [
                        token for token in state.candidate_tokens()
                        if 0 <= token < self.eot
                    ]
                    if candidates:
                        if state.trie.phrase_deltas:
                            # Per-phrase δ: each candidate is boosted by the
                            # neediest term it could still be leading toward.
                            # `BiasState.advance` accrues its refund through the
                            # same call, so the charge and the refund cannot
                            # disagree -- which is the invariant that broke when
                            # this was first prototyped, costing 3.2 points and
                            # showing up only because the uniform control stopped
                            # reproducing the shipped decoder.
                            for token in candidates:
                                candidate_logprobs[token] += \
                                    state.trie.delta_for_token(state.nodes, token)
                        else:
                            candidate_logprobs[candidates] += state.trie.delta
                for logprob, token in zip(*candidate_logprobs.topk(self.beam_size + 1)):
                    token_id = token.item()
                    candidate_state = state
                    adjustment = 0.0
                    if state is not None:
                        candidate_state, adjustment = state.advance(token_id)
                    score = (sum_logprobs[index] + logprob).item() + adjustment
                    sequence = tuple(prefix + [token_id])
                    if sequence not in scores or score > scores[sequence]:
                        scores[sequence] = score
                        sources[sequence] = index
                        states[sequence] = candidate_state

            saved = 0
            for sequence in sorted(scores, key=scores.get, reverse=True):
                if sequence[-1] == self.eot:
                    finished[sequence] = scores[sequence]
                else:
                    sum_logprobs[len(next_tokens)] = scores[sequence]
                    next_tokens.append(sequence)
                    source_indices.append(sources[sequence])
                    next_states.append(states[sequence])
                    saved += 1
                    if saved == self.beam_size:
                        break
            finished_sequences.append(finished)

        tokens = torch.tensor(next_tokens, device=tokens.device)
        self.inference.rearrange_kv_cache(source_indices)
        self.bias_states = next_states
        for previous, newly_finished in zip(self.finished_sequences, finished_sequences):
            for sequence in sorted(newly_finished, key=newly_finished.get, reverse=True):
                if len(previous) >= self.max_candidates:
                    break
                previous[sequence] = newly_finished[sequence]
        completed = all(
            len(sequences) >= self.max_candidates
            for sequences in self.finished_sequences
        )
        return tokens, completed


def build_trie(bias_phrases, tokenizer, *, delta=DEFAULT_DELTA,
               completion_bonus=DEFAULT_COMPLETION_BONUS,
               phrase_deltas=None):
    """`BiasTrie` over the phrases, or None when there is nothing to bias.

    ``phrase_deltas`` maps a phrase's index in ``bias_phrases`` to its own δ,
    overriding the scalar for that term only. Absent or empty, this function
    does not call ``set_phrase_deltas`` at all, so ``BiasTrie.phrase_deltas``
    stays ``{}``, ``delta_for_token`` returns ``self.delta`` on its first
    branch, and the decode is the uniform decode with no code path added.
    That identity is the acceptance test for per-term δ, not a recovery
    number: internal research finding 24's first implementation boosted with a
    per-term δ while `BiasState.advance` still accrued the trie's scalar into
    ``pending``, so a dead partial match refunded less than it was charged and
    kept score it had never earned. It cost 3.2 points and was caught only
    because the uniform control stopped reproducing the shipped decoder.
    """
    if not bias_phrases:
        return None
    try:
        from .beam_decode import BiasTrie
    except ImportError:
        from beam_decode import BiasTrie
    trie = BiasTrie(list(bias_phrases), _TokenizerAdapter(tokenizer),
                    delta=delta, completion_bonus=completion_bonus)
    if phrase_deltas:
        trie.set_phrase_deltas({int(k): float(v)
                                for k, v in dict(phrase_deltas).items()})
    return trie


class TrieTranscribeModel:
    """Whisper model adapter that changes only zero-temperature beam decode.

    OpenAI Whisper's ``transcribe`` function already owns long-audio windowing,
    prompt carry-over, language detection, timestamp advancement, silence
    handling, and temperature fallback.  Reimplementing that orchestration in
    this module is both unnecessary and what caused the original 30-second truncation.

    This adapter delegates the complete model surface and intercepts only the
    ``model.decode(mel, options)`` seam used by that upstream loop.  A regular
    zero-temperature beam gets the measured ``BiasBeamSearchDecoder``; sampling
    attempts used by Whisper's temperature fallback remain the upstream decode
    because the trie research is beam-only.
    """

    def __init__(self, model, bias_phrases, *, delta=DEFAULT_DELTA,
                 completion_bonus=DEFAULT_COMPLETION_BONUS,
                 phrase_deltas=None):
        self._model = model
        self._bias_phrases = list(bias_phrases or [])
        self._delta = delta
        self._completion_bonus = completion_bonus
        self._phrase_deltas = dict(phrase_deltas) if phrase_deltas else None
        # Upstream's transcribe loop calls decode() once per 30-second window,
        # so a long dictation rebuilt an identical tree several times over.
        # Everything the tree depends on is fixed at construction here, which
        # makes the key constant for this model's lifetime; only the tokenizer
        # arrives per call, and BiasTrieCache compares that by identity.
        self._trie_cache = _BiasTrieCache()
        self._trie_key = _BiasTrieCache.key(
            self._bias_phrases, delta=delta, completion_bonus=completion_bonus,
            phrase_deltas=self._phrase_deltas)

    def __getattr__(self, name):
        return getattr(self._model, name)

    @torch.no_grad()
    def decode(self, mel, options):
        # Whisper removes beam_size when it raises temperature for a fallback.
        # Do not invent a sampling-compatible bias algorithm here: that is a
        # Research decision, while preserving upstream fallback is engineering.
        if (
            not getattr(options, "beam_size", None)
            or getattr(options, "temperature", 0.0) > 0
        ):
            return self._model.decode(mel, options)

        single = mel.ndim == 2
        if single:
            mel = mel.unsqueeze(0)

        task = DecodingTask(self._model, options)
        trie = self._trie_cache.get(
            self._trie_key,
            task.tokenizer,
            lambda: build_trie(
                self._bias_phrases, task.tokenizer,
                delta=self._delta,
                completion_bonus=self._completion_bonus,
                phrase_deltas=self._phrase_deltas,
            ),
        )
        task.decoder = BiasBeamSearchDecoder(
            options.beam_size, task.tokenizer.eot, task.inference,
            options.patience, bias=trie,
        )
        task.sequence_ranker = MaximumLikelihoodRanker(options.length_penalty)
        results = task.run(mel)
        return results[0] if single else results


def transcribe_with_trie(model, audio, *, bias_phrases, language=None,
                         beam_size=4, delta=DEFAULT_DELTA,
                         completion_bonus=DEFAULT_COMPLETION_BONUS,
                         sample_len=128, phrase_deltas=None):
    """Full Whisper transcription with the trie active in every beam window.

    Returns `(text, detected_language, n_phrases, segments)`.  The segments
    stay internal to the product and let the caller detect a decoder that
    returned normally after ending a voiced window far too early.  Raises
    nothing the caller should swallow: if this fails, the caller must fall
    back to the plain decode rather than return a half-biased result.
    """
    import whisper as openai_whisper

    wrapped = TrieTranscribeModel(
        model, bias_phrases, delta=delta,
        completion_bonus=completion_bonus,
        phrase_deltas=phrase_deltas)
    result = openai_whisper.transcribe(
        wrapped,
        audio,
        language=language,
        task="transcribe",
        beam_size=beam_size,
        without_timestamps=True,
        sample_len=sample_len,
        verbose=None,
    )
    return (
        (result.get("text") or "").strip(),
        result.get("language"),
        len(bias_phrases) if bias_phrases else 0,
        result.get("segments") or [],
    )


#: Seconds the repair pass is shifted by. Half a window, so a phrase cut by the
#: main pass's 30 s boundary sits near the middle of a repair window rather than
#: on its edge -- shifting by less would move the seam without clearing it.
SEAM_REPAIR_OFFSET_S = 15.0

#: Below this length there is no seam, so the repair pass is never considered.
SEAM_REPAIR_MIN_AUDIO_S = 30.0


def _terms_present(text: str, bias_phrases) -> set[str]:
    """Which bias phrases appear in `text`, matched loosely on word boundaries."""
    import re

    found = set()
    lowered = text.lower()
    for phrase in bias_phrases or ():
        needle = phrase.lower()
        if re.search(rf"(?<!\w){re.escape(needle)}(?!\w)", lowered):
            found.add(phrase)
    return found


def _repair_from_offset_pass(segments, repair_segments, missing, offset_s):
    """Substitute repair segments that recovered a term the main pass lost.

    Returns `(segments, repaired_terms)`. A repair segment replaces **the single
    main segment it overlaps most**, and only when it contains a term the main
    pass missed -- so every region where nothing was missing comes through
    untouched.

    Replacing every overlapping segment instead of the best-overlapping one is
    the obvious implementation and it is wrong: a repair window offset by 15 s
    straddles two main segments by construction, so it would swallow the
    preceding one and delete speech that was transcribed correctly. The repair
    must be able to fix a term without being able to lose a sentence.
    """
    if not missing or not repair_segments:
        return segments, set()

    repaired: set[str] = set()
    result = list(segments)
    for repair in repair_segments:
        text = (repair.get("text") or "").strip()
        recovered = _terms_present(text, missing)
        if not recovered:
            continue
        start = float(repair.get("start", 0.0)) + offset_s
        end = float(repair.get("end", 0.0)) + offset_s
        def overlap(seg) -> float:
            return max(0.0, min(float(seg.get("end", 0.0)), end)
                       - max(float(seg.get("start", 0.0)), start))

        best = max(range(len(result)), key=lambda i: overlap(result[i]),
                   default=None)
        if best is None or overlap(result[best]) <= 0.0:
            continue
        result[best] = {**result[best], "text": text}
        repaired |= recovered
        missing = missing - recovered
        if not missing:
            break
    return result, repaired


def transcribe_with_trie_repaired(model, audio, *, bias_phrases, language=None,
                                  beam_size=4, delta=DEFAULT_DELTA,
                                  completion_bonus=DEFAULT_COMPLETION_BONUS,
                                  sample_len=128, phrase_deltas=None):
    """`transcribe_with_trie`, plus a second pass that repairs the 30 s seam.

    ## The defect this exists for

    Whisper encodes 30 seconds at a time, and `BiasTrie` state does not persist
    across that boundary: a phrase straddling it is half-matched at the end of
    one window, has its boost rolled back, and restarts from the trie root in
    the next with the term already half-spoken. Measured on 168 utterances of
    58 s and 88 s (internal research finding 21), a term planted on the seam
    recovers 42.9-47.6% against 71.4% for one sitting well inside a window.

    Overlapping the windows by five seconds closes it -- the seam position
    reaches 80.9% -- but the main path deliberately delegates windowing to
    `whisper.transcribe`, whose seek advancement is upstream's business and not
    something to reach into.

    ## What this does instead

    Runs the ordinary decode unchanged, then, **only if** the audio is longer
    than one window **and** some vocabulary term is missing from the result,
    decodes again from a 15-second offset and substitutes only those segments
    that recovered a missing term.

    So the repair cannot alter anything it did not fix:

    * audio <= 30 s -> the second pass never runs, output byte-identical;
    * no term missing -> the second pass never runs, output byte-identical;
    * a term missing but not found by the offset pass -> nothing substituted.

    The cost is a second decode on long audio, paid only when something was
    actually lost. That is the trade: `turbo` decodes at ~4.7 ms/token, and the
    alternative is losing the term entirely.

    Returns `(text, detected_language, n_phrases, segments)` like the plain
    version, so callers need not know which path ran.
    """
    import whisper as openai_whisper

    text, detected, n_phrases, segments = transcribe_with_trie(
        model, audio, bias_phrases=bias_phrases, language=language,
        beam_size=beam_size, delta=delta, completion_bonus=completion_bonus,
        sample_len=sample_len, phrase_deltas=phrase_deltas)

    sample_rate = openai_whisper.audio.SAMPLE_RATE
    if not bias_phrases or len(audio) <= SEAM_REPAIR_MIN_AUDIO_S * sample_rate:
        return text, detected, n_phrases, segments

    missing = set(bias_phrases) - _terms_present(text, bias_phrases)
    if not missing:
        return text, detected, n_phrases, segments

    offset = int(SEAM_REPAIR_OFFSET_S * sample_rate)
    _, _, _, repair_segments = transcribe_with_trie(
        model, audio[offset:], bias_phrases=bias_phrases, language=language,
        beam_size=beam_size, delta=delta, completion_bonus=completion_bonus,
        sample_len=sample_len, phrase_deltas=phrase_deltas)

    segments, repaired = _repair_from_offset_pass(
        segments, repair_segments, missing, SEAM_REPAIR_OFFSET_S)
    if not repaired:
        return text, detected, n_phrases, segments

    print(f"whisper_mac: seam repair recovered {sorted(repaired)}",
          file=__import__("sys").stderr, flush=True)
    merged = " ".join((s.get("text") or "").strip() for s in segments).strip()
    return merged, detected, n_phrases, segments


# ─── self-check ─────────────────────────────────────────────────────────────


def _selftest() -> int:
    """Model-free checks for this optional decoder's fixed operating point.

    Requires torch and openai-whisper; no audio or model weights are loaded.
    """
    import ast
    import inspect
    from pathlib import Path

    failures = []

    def check(name: str, cond: bool, detail: str = "") -> None:
        print(f"  {'ok  ' if cond else 'FAIL'} {name}{(' — ' + detail) if detail else ''}")
        if not cond:
            failures.append(name)

    check("completion bonus stays disabled", DEFAULT_COMPLETION_BONUS == 0.0)

    # Held to the macOS measurement itself, not to the other file: these are
    # the two ends of the table in DEFAULT_DELTA's comment (turbo, 360 clips,
    # 21-term arm, beam 4), and they are what makes 2.5 defensible rather
    # than merely different from Windows'.
    check("the operating point stays under the ceiling both instruments found",
          DEFAULT_DELTA < 3.0,
          f"{DEFAULT_DELTA} vs 3.0 — U-WER 5.09% -> 9.69% there, and it is "
          "also where false triggers start accumulating on the real vocabulary")
    check("the operating point stays above the value turbo was under-persuaded at",
          DEFAULT_DELTA > 1.0,
          f"{DEFAULT_DELTA} vs 1.0 — 66.7% recovery against 92.1%; 1.0 is "
          "`base`'s number, and copying whisper_qnn's constant lands here")

    # δ is not tunable at runtime, and every comment above depends on that: a
    # default argument binds at `def` time, so reassigning
    # `trie_decode.DEFAULT_DELTA` after import changes nothing at all
    # (`the engine field report`).
    # What ships is whatever each signature captured, so an entry point that
    # spells a literal instead of the constant ships a private operating
    # point that no comment in this file describes.
    entry_points = {
        "build_trie": build_trie,
        "TrieTranscribeModel.__init__": TrieTranscribeModel.__init__,
        "transcribe_with_trie": transcribe_with_trie,
        "transcribe_with_trie_repaired": transcribe_with_trie_repaired,
    }
    for name, fn in entry_points.items():
        params = inspect.signature(fn).parameters
        for param, constant in (("delta", DEFAULT_DELTA),
                                ("completion_bonus", DEFAULT_COMPLETION_BONUS)):
            found = params[param].default if param in params else None
            check(f"{name} defaults {param} to the module constant",
                  found == constant, f"{found} vs {constant}")

    print(f"trie_decode selftest: {len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    import sys

    if "--selftest" in sys.argv:
        raise SystemExit(_selftest())
    raise SystemExit("usage: python -m transcription_worker.trie_decode --selftest")
