"""Context carry (DICTATION_NOTES_CTX_CARRY): the previous committed line's tail
rides in the greedy decode's initial_prompt as whisper-style rolling
context, after the vocabulary prompt. Whisper was built to condition on
the preceding transcript; the streaming worker decodes every window
context-free, which loses cross-boundary continuity (measured on the AMI
replay: deletions cluster at commit-span edges).

Contract:
- default (env unset) => decodes see exactly the old initial_prompt.
- DICTATION_NOTES_CTX_CARRY=1 => prompt = vocab prompt + previous committed
  text tail (vocab first: it must stay inside the device prompt-token
  budget's front prefix; the tail is capped at CTX_CARRY_MAX_CHARS).
- a line the hallucination gate would drop never enters the tail (a
  carried "Thank you." would seed the exact loop the gates exist for).
- beam decodes are untouched (bias trie owns beam biasing).
"""

import numpy as np

from transcription_worker import ops_stream_whisper as osw


class PromptSpyEngine:
    def __init__(self):
        self.prompts = []

    def transcribe(self, clip, language=None, beam_size=1,
                   bias_phrases=None, initial_prompt=None, **kw):
        self.prompts.append(initial_prompt)
        return {"text": "decoded words", "stats": {"language": "en"}}


def make_stream(engine, monkeypatch=None, prompt=None):
    log = osw._CommitLog()
    ch = osw._ChannelStream(engine, log, label_fn=lambda *a: "You",
                            fixed_label="You", channel_tag="mic",
                            initial_prompt=prompt)
    return ch


def speech(seconds=1.0):
    rng = np.random.default_rng(0)
    return (rng.standard_normal(int(osw.SAMPLE_RATE * seconds))
            .astype(np.float32) * 0.1)


def test_default_off_prompt_unchanged(monkeypatch):
    monkeypatch.delenv("DICTATION_NOTES_CTX_CARRY", raising=False)
    eng = PromptSpyEngine()
    ch = make_stream(eng, prompt="vocab one, vocab two")
    ch._ctx_tail = "should never appear"
    ch._transcribe2(speech())
    assert eng.prompts == ["vocab one, vocab two"]


def test_carry_appends_tail_after_vocab(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_CTX_CARRY", "1")
    eng = PromptSpyEngine()
    ch = make_stream(eng, prompt="vocab one")
    ch._ctx_tail = "the previous line text"
    ch._transcribe2(speech())
    assert eng.prompts[-1] == "vocab one\nthe previous line text"


def test_carry_without_vocab_is_tail_only(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_CTX_CARRY", "1")
    eng = PromptSpyEngine()
    ch = make_stream(eng, prompt=None)
    ch._ctx_tail = "previous words"
    ch._transcribe2(speech())
    assert eng.prompts[-1] == "previous words"


def test_carry_empty_tail_falls_back(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_CTX_CARRY", "1")
    eng = PromptSpyEngine()
    ch = make_stream(eng, prompt="vocab one")
    ch._transcribe2(speech())
    assert eng.prompts[-1] == "vocab one"


def test_commit_updates_tail_and_caps_length(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_CTX_CARRY", "1")
    eng = PromptSpyEngine()
    ch = make_stream(eng)
    long_text = "word " * 100
    ch._commit_guarded("You", speech(), long_text.strip(), "en",
                       span=(0.0, 2.0))
    assert ch._ctx_tail
    assert len(ch._ctx_tail) <= osw.CTX_CARRY_MAX_CHARS
    # the tail keeps the END of the line (most recent context wins)
    assert ch._ctx_tail.endswith("word")


def test_junk_line_never_enters_tail(monkeypatch):
    monkeypatch.setenv("DICTATION_NOTES_CTX_CARRY", "1")
    eng = PromptSpyEngine()
    ch = make_stream(eng)
    ch._commit_guarded("You", speech(), "real sentence here", "en",
                       span=(0.0, 2.0))
    ch._commit_guarded("You", speech(), "Thank you.", "en",
                       span=(2.0, 3.0))
    assert ch._ctx_tail == "real sentence here"


def test_tail_updates_even_when_env_off(monkeypatch):
    """The tail is tracked unconditionally (cheap) so flipping the env
    mid-session works; only the PROMPT composition is gated."""
    monkeypatch.delenv("DICTATION_NOTES_CTX_CARRY", raising=False)
    eng = PromptSpyEngine()
    ch = make_stream(eng, prompt="vocab")
    ch._commit_guarded("You", speech(), "committed line", "en",
                       span=(0.0, 2.0))
    assert ch._ctx_tail == "committed line"
    ch._transcribe2(speech())
    assert eng.prompts[-1] == "vocab"
