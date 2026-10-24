"""Vocabulary beam decoding on the existing MLX/Metal model, without Torch.

mlx-whisper 0.4.3 leaves beam decoding unimplemented. Upstream tracking:
https://github.com/ml-explore/mlx-examples/issues/1391
https://github.com/ml-explore/mlx-examples/pull/1454

The adapter retains upstream transcription/windowing/temperature fallback.
It uses an isolated function namespace to supply a model adapter: neither
the installed package, its globals, nor the shared model are monkey-patched.
Remove that compatibility seam when upstream accepts a model/decode adapter.
BiasTrie owns rollback and repeat suppression, shared with the old decoder.
Search scores select candidates; acoustic scores alone reach speech gates.
"""
from __future__ import annotations

from dataclasses import replace
import math
from types import FunctionType

import numpy as np

from .beam_decode import BiasTrie, BiasTrieCache

DEFAULT_BEAM_SIZE = 2
DEFAULT_DELTA = 0.5  # Combined with a prompt; not the old CPU-only delta=2.5.
MAX_BEAM_SIZE = 4
MAX_TERMS = 32


def _length_penalty(length, alpha):
    return max(1, length) if alpha is None else ((5 + length) / 6) ** alpha


class BeamCandidates:
    """Small CPU-side bookkeeping; model, logits and top-k stay on Metal.

    One audio item, as in Dictation's upstream transcribe loop. Candidates
    arrive as (token, acoustic log probability, adjusted log probability).
    Prefixes deduplicate, and every surviving prefix carries its own trie
    state and parent index for self-attention cache reordering.
    """

    def __init__(self, width, eot, trie=None, *, max_tokens=224, length_penalty=None):
        if isinstance(width, bool) or not isinstance(width, int) or not 1 <= width <= MAX_BEAM_SIZE:
            raise ValueError("beam width must be an integer from 1 to 4")
        self.width, self.eot, self.trie = width, eot, trie
        self.max_tokens, self.length_penalty = max_tokens, length_penalty
        self.phrase_ids, self.max_depth = set(), 0
        if trie is not None:
            nodes = [trie.root]
            while nodes:
                node = nodes.pop()
                self.phrase_ids.update(node.phrase_ids)
                self.max_depth = max(self.max_depth, node.depth)
                nodes.extend(node.children.values())
        self.max_delta = max(0.0, trie.delta, *trie.phrase_deltas.values()) if trie else 0.0
        self.max_bonus = max(0.0, trie.completion_bonus) if trie else 0.0
        self.reset()

    def reset(self):
        self.prefixes = None
        self.states = None
        self.scores = [0.0] * self.width
        self.acoustic = [0.0] * self.width
        self.finished = {}
        self.exhausted = False

    def start(self, prefixes):
        if len(prefixes) != self.width:
            raise ValueError("one audio item with exactly beam-width rows required")
        if self.prefixes is None:
            self.sample_begin = len(prefixes[0])
            self.prefixes = [tuple(p) for p in prefixes]
            self.states = [self.trie.initial_state() if self.trie else None for _ in prefixes]

    def _rank(self, sequence, score):
        length = len(sequence) - self.sample_begin - 1  # Exclude prompt and EOT.
        return score / _length_penalty(length, self.length_penalty)

    def _finish(self, sequence, score, acoustic):
        self.finished[sequence] = (score, acoustic)
        # A later complete sentence may outrank an early, truncated ending.
        best = sorted(self.finished, key=lambda s: (-self._rank(s, self.finished[s][0]), s))
        self.finished = {s: self.finished[s] for s in best[:self.width]}

    def _can_stop(self):
        if not self.finished:
            return False
        if self.width == 1:
            return True  # Retain the greedy decoder's EOT contract.
        best = max(self._rank(s, score) for s, (score, _) in self.finished.items())
        for seq, score, state in zip(self.prefixes, self.scores, self.states):
            generated = len(seq) - self.sample_begin
            # Acoustic log probabilities cannot add positive score. Trie
            # credit can: each fresh completion retains at most max_depth*D
            # and resets pending credit. Repeats and EOT refund pending.
            # Counting all remaining phrase IDs also bounds overlapping terms.
            remaining = len(self.phrase_ids - state.completed) if state else 0
            reward = remaining * (self.max_depth * self.max_delta + self.max_bonus)
            upper = max(
                (score + min(reward, (length - generated) * (self.max_delta + self.max_bonus)))
                / _length_penalty(length, self.length_penalty)
                for length in range(generated, max(generated, self.max_tokens) + 1))
            if upper > best:
                return False
        return True

    def advance(self, candidates):
        if self.prefixes is None or len(candidates) != self.width:
            raise ValueError("beam candidates do not match initialized rows")
        choices = {}
        for parent, row in enumerate(candidates):
            for token, acoustic, adjusted in row:
                if not math.isfinite(acoustic) or not math.isfinite(adjusted):
                    continue
                sequence = self.prefixes[parent] + (int(token),)
                state = self.states[parent]
                next_state = state.advance(int(token))[0] if state else None
                candidate = (self.scores[parent] + adjusted, parent,
                             self.acoustic[parent] + acoustic, next_state)
                if sequence not in choices or candidate[0] > choices[sequence][0]:
                    choices[sequence] = candidate
        active, parents, scores, acoustic, states = [], [], [], [], []
        for sequence, (score, parent, raw, state) in sorted(
                choices.items(), key=lambda item: (-item[1][0], item[0])):
            if sequence[-1] == self.eot:
                self._finish(sequence, score, raw)
            else:
                active.append(sequence)
                parents.append(parent)
                scores.append(score)
                acoustic.append(raw)
                states.append(state)
                if len(active) == self.width:
                    break
        exhausted = not active
        self.exhausted = exhausted
        if exhausted:
            if not self.finished:
                raise ValueError("beam has no finite candidates")
            # All surviving candidates ended. Keep correctly shaped rows for
            # upstream's reshape; finalize selects only completed sequences.
            active, parents = self.prefixes, list(range(self.width))
            scores, acoustic, states = self.scores, self.acoustic, self.states
        while len(active) < self.width:
            active.append(active[-1]); parents.append(parents[-1])
            scores.append(scores[-1]); acoustic.append(acoustic[-1]); states.append(states[-1])
        self.prefixes, self.scores, self.acoustic, self.states = active, scores, acoustic, states
        return active, parents, scores, exhausted or self._can_stop()

    def finalize(self):
        if not self.prefixes:
            raise ValueError("beam was never started")
        if self.exhausted or (self.width == 1 and self.finished):
            return self.finished
        # Length-limited beams also lose unearned partial-word credit. EOT is
        # synthetic here, so no acoustic likelihood is invented for it.
        for i in sorted(range(self.width), key=lambda i: self.scores[i], reverse=True):
            state = self.states[i]
            refund = state.advance(self.eot)[1] if state else 0.0
            self._finish(self.prefixes[i] + (self.eot,),
                         self.scores[i] + refund, self.acoustic[i])
        return self.finished


class MetalBeamDecoder:
    def __init__(self, width, eot, inference, trie=None, *, max_tokens=224, length_penalty=None):
        import mlx.core as mx
        self.mx, self.inference = mx, inference
        self.search = BeamCandidates(width, eot, trie, max_tokens=max_tokens, length_penalty=length_penalty)

    def reset(self):
        self.search.reset()

    def update(self, tokens, logits, sum_logprobs):
        mx, search = self.mx, self.search
        if search.prefixes is None:
            search.start(tokens.tolist())
        raw = logits.astype(mx.float32)
        raw = raw - mx.logsumexp(raw, axis=-1, keepdims=True)
        adjusted = raw
        if search.trie is not None:
            # A nonmatching token refunds all unfinished credit. Matching
            # edges include their exact rollback BEFORE top-k, so a dead
            # partial cannot crowd a better acoustic candidate out of search.
            adjustments = np.zeros(raw.shape, dtype=np.float32)
            for row, state in enumerate(search.states):
                adjustments[row, :] = -state.pending
                for token in state.candidate_tokens():
                    if 0 <= token < search.eot:
                        _, refund = state.advance(token)
                        adjustments[row, token] = state.trie.delta_for_token(state.nodes, token) + refund
            adjusted = raw + mx.array(adjustments)
        k = min(search.width + 1, raw.shape[-1])
        ids = mx.argpartition(-adjusted, kth=k - 1, axis=-1)[:, :k]
        selected_raw = mx.take_along_axis(raw, ids, axis=-1)
        selected_adjusted = mx.take_along_axis(adjusted, ids, axis=-1)
        mx.eval(ids, selected_raw, selected_adjusted)
        candidates = [list(zip(i, r, a)) for i, r, a in
                      zip(ids.tolist(), selected_raw.tolist(), selected_adjusted.tolist())]
        prefixes, parents, scores, done = search.advance(candidates)
        self.inference.rearrange_kv_cache(parents)
        return mx.array(prefixes, dtype=tokens.dtype), mx.array(done), mx.array(scores)

    def finalize(self, tokens, sum_logprobs):
        finished = self.search.finalize()
        length = max(map(len, finished))
        sequences = [list(s) + [self.search.eot] * (length - len(s)) for s in finished]
        return self.mx.array([sequences]), self.mx.array([[v[0] for v in finished.values()]])


class BeamTimestampRules:
    """Upstream timestamp policy with its two 0.4.3 masking bugs corrected.

    Track timestamp TOKEN VALUES (not their positions in the sequence), and
    compare timestamp mass only AFTER impossible tokens have been masked.
    Minimal reproductions live in test_mlx_beam_native.py; upstream PR #1454
    includes these fixes. Keeping timestamps preserves normal punctuation
    and segment seeking instead of changing the transcription task.
    """

    def __init__(self, original):
        self.tokenizer = original.tokenizer
        self.sample_begin = original.sample_begin
        self.max_initial_timestamp_index = original.max_initial_timestamp_index

    def apply(self, logits, tokens):
        import mlx.core as mx
        tokenizer = self.tokenizer
        mask = np.zeros(logits.shape, np.float32)
        if tokenizer.no_timestamps is not None:
            mask[:, tokenizer.no_timestamps] = -np.inf
        for row, prefix in enumerate(tokens.tolist()):
            seq = prefix[self.sample_begin:]
            last_is_time = bool(seq) and seq[-1] >= tokenizer.timestamp_begin
            previous_is_time = len(seq) < 2 or seq[-2] >= tokenizer.timestamp_begin
            if last_is_time:
                if previous_is_time:
                    mask[row, tokenizer.timestamp_begin:] = -np.inf
                else:
                    mask[row, :tokenizer.eot] = -np.inf
            timestamps = [t for t in seq if t >= tokenizer.timestamp_begin]
            if timestamps:
                last_allowed = timestamps[-1] if last_is_time and not previous_is_time else timestamps[-1] + 1
                mask[row, tokenizer.timestamp_begin:last_allowed] = -np.inf
        if tokens.shape[-1] == self.sample_begin:
            mask[:, :tokenizer.timestamp_begin] = -np.inf
            if self.max_initial_timestamp_index is not None:
                mask[:, tokenizer.timestamp_begin + self.max_initial_timestamp_index + 1:] = -np.inf
        masked = logits + mx.array(mask)
        logprobs = masked - mx.logsumexp(masked, axis=-1, keepdims=True)
        time_mass = mx.logsumexp(logprobs[:, tokenizer.timestamp_begin:], axis=-1, keepdims=True)
        text_max = mx.max(logprobs[:, :tokenizer.timestamp_begin], axis=-1, keepdims=True)
        return mx.concatenate((mx.where(time_mass > text_max, -mx.inf,
                                        masked[:, :tokenizer.timestamp_begin]),
                               masked[:, tokenizer.timestamp_begin:]), axis=-1)


class BeamRanker:
    """Upstream length penalty, also defined for an empty EOT candidate.

    MLX 0.4.3 MaximumLikelihoodRanker divides by zero for length zero.
    Unlike the greedy decoder, a beam can retain an early empty candidate
    alongside nonempty candidates. Treat its length as one (its EOT).
    The native test drives this through the unmodified DecodingTask.run.
    """

    def __init__(self, length_penalty):
        self.length_penalty = length_penalty

    def rank(self, tokens, sum_logprobs):
        result = []
        for group, scores in zip(tokens, sum_logprobs):
            normalized = [score / _length_penalty(len(seq), self.length_penalty)
                          for seq, score in zip(group, scores)]
            result.append(int(np.argmax(normalized)))
        return result


def _task_type():
    import mlx.core as mx
    from mlx_whisper.decoding import ApplyTimestampRules, DecodingTask, Inference

    class BeamInference(Inference):
        def rearrange_kv_cache(self, indices):
            if self.kv_cache is None or indices == list(range(len(indices))):
                return
            ids = mx.array(indices)
            # Cross-attention represents the SAME audio for every beam. Do
            # not copy it at every token. Only text/self-attention changes.
            self.kv_cache = [((kv[0][ids], kv[1][ids]), cross)
                             for kv, cross in self.kv_cache]

    class BeamTask(DecodingTask):
        def __init__(self, model, options, trie_builder):
            if options.temperature != 0 or options.best_of is not None:
                raise ValueError("vocabulary beam requires deterministic decoding")
            super().__init__(model, replace(options, beam_size=None, patience=None))
            self.options = options
            self.n_group = options.beam_size
            self.sequence_ranker = BeamRanker(options.length_penalty)
            self.logit_filters = [BeamTimestampRules(f) if isinstance(f, ApplyTimestampRules) else f
                                  for f in self.logit_filters]
            self.inference = BeamInference(model)
            self.decoder = MetalBeamDecoder(self.n_group, self.tokenizer.eot,
                                            self.inference, trie_builder(self.tokenizer),
                                            max_tokens=min(self.sample_len, self.n_ctx - self.sample_begin + 1),
                                            length_penalty=options.length_penalty)

        def _main_loop(self, audio_features, tokens):
            total = mx.zeros(tokens.shape[0])
            no_speech = mx.full(tokens.shape[0], mx.nan)
            for step in range(self.sample_len):
                if tokens.shape[-1] > self.n_ctx:
                    break
                logits = self.inference.logits(tokens if step == 0 else tokens[:, -1:], audio_features)
                if step == 0 and self.tokenizer.no_speech is not None:
                    no_speech = mx.softmax(logits[:, self.sot_index], axis=-1)[:, self.tokenizer.no_speech]
                logits = logits[:, -1]
                for logit_filter in self.logit_filters:
                    logits = logit_filter.apply(logits, tokens)
                tokens, completed, total = self.decoder.update(tokens, logits, total)
                mx.eval(tokens, completed, total, no_speech)
                if completed.item():
                    break
            return tokens, total, no_speech

    return BeamTask


class VocabularyModel:
    def __init__(self, model, phrases, width, delta, metrics, phrase_deltas=None):
        self.model, self.phrases, self.width, self.delta = model, tuple(phrases), width, delta
        self.metrics = metrics
        self.cache = BiasTrieCache()
        self.phrase_deltas = dict(phrase_deltas or {})

    def __getattr__(self, name):
        return getattr(self.model, name)

    def build_trie(self, tokenizer):
        if not self.phrases or (not self.delta and not self.phrase_deltas):
            return None

        class TokenizerAdapter:
            def __init__(self, tokenizer): self.tokenizer = tokenizer
            def encode(self, text, add_special_tokens=False): return self.tokenizer.encode(text)

        key = BiasTrieCache.key(self.phrases, delta=self.delta, completion_bonus=0.0,
                                phrase_deltas=self.phrase_deltas)

        def build():
            trie = BiasTrie(list(self.phrases), TokenizerAdapter(tokenizer),
                            delta=self.delta, completion_bonus=0.0)
            if self.phrase_deltas:
                trie.set_phrase_deltas(self.phrase_deltas)
            return trie
        return self.cache.get(key, tokenizer, build)

    def decode(self, mel, options):
        import mlx.core as mx
        if options.temperature > 0 or options.task == "lang_id":
            self.metrics["sampling_fallbacks"] += int(options.temperature > 0)
            return self.model.decode(mel, replace(options, beam_size=None, patience=None))
        if mel.ndim != 2:
            raise ValueError("vocabulary transcribe adapter expects one audio window")

        options = replace(options, beam_size=self.width, patience=None)
        task = _task_type()(self.model, options, self.build_trie)
        result = task.run(mx.expand_dims(mel, 0))[0]
        self.metrics["beam_windows"] += 1
        for sequence, (_, acoustic) in task.decoder.search.finished.items():
            generated = sequence[task.sample_begin:]
            generated = generated[:generated.index(task.tokenizer.eot)]
            if tuple(result.tokens) == generated:
                state = task.decoder.search.trie.initial_state() if task.decoder.search.trie else None
                if state is not None:
                    for token in generated:
                        state, _ = state.advance(token)
                    self.metrics["matched_terms"] += len(state.completed)
                return replace(result, avg_logprob=acoustic / (len(result.tokens) + 1))
        raise RuntimeError("selected beam lost its acoustic score")


def transcribe(audio, *, bias_phrases=(), beam_size=DEFAULT_BEAM_SIZE, delta=DEFAULT_DELTA,
               phrase_deltas=None, **kwargs):
    """Upstream transcribe with an instance-scoped GPU beam model adapter."""
    import mlx_whisper
    if isinstance(beam_size, bool) or not isinstance(beam_size, int) or not 1 <= beam_size <= MAX_BEAM_SIZE:
        raise ValueError("unsupported vocabulary beam width")
    if not isinstance(delta, (int, float)) or not math.isfinite(delta) or not 0 <= delta <= 2.5:
        raise ValueError("invalid vocabulary beam strength")
    phrases = tuple(bias_phrases or ())
    if len(phrases) > MAX_TERMS or any(not isinstance(p, str) or not p.strip() for p in phrases):
        raise ValueError("invalid vocabulary beam terms")
    deltas = dict(phrase_deltas or {})
    if any(isinstance(i, bool) or not isinstance(i, int) or not 0 <= i < len(phrases)
           or isinstance(value, bool) or not isinstance(value, (int, float))
           or not math.isfinite(value) or not -10.0 <= value <= 6.0 for i, value in deltas.items()):
        raise ValueError("invalid per-term vocabulary strength")
    original = mlx_whisper.transcribe
    holder = original.__globals__["ModelHolder"]
    metrics = {"beam_windows": 0, "sampling_fallbacks": 0, "matched_terms": 0}

    class LocalHolder:
        @staticmethod
        def get_model(path, dtype):
            return VocabularyModel(holder.get_model(path, dtype), phrases, beam_size, delta, metrics, deltas)

    namespace = dict(original.__globals__, ModelHolder=LocalHolder)
    local_transcribe = FunctionType(original.__code__, namespace, original.__name__,
                                    original.__defaults__, original.__closure__)
    local_transcribe.__kwdefaults__ = original.__kwdefaults__
    result = local_transcribe(audio, beam_size=beam_size, **kwargs)
    result["vocabulary_search"] = dict(metrics, engine="mlx", beam_size=beam_size, terms=len(phrases))
    return result
