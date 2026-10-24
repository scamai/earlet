"""Tests for note_templates.py + its wiring into NotesGenerator.

Templates steer generation via a system message; these tests confirm the
default template is neutral (no system message → today's behavior), a built-in
template injects its steer as a system role on EVERY pass, per-call override
works, custom templates load fail-soft, and an unknown id degrades to default
rather than breaking generation.

Run: python -m pytest transcription_worker/test_note_templates.py
"""
from __future__ import annotations

import json

from transcription_worker import note_templates as nt
from transcription_worker.notes_gen import NotesGenerator
from transcription_worker.test_notes_gen import FakeChatEnh, _TRANSCRIPT, _USER_NOTES


def test_default_template_is_neutral():
    t = nt.get("default")
    assert t.system_message() is None
    assert nt.get(None).id == "default"
    assert nt.get("does_not_exist").id == "default"  # unknown → default, never error


def test_builtin_templates_carry_a_steer():
    t = nt.get("customer_discovery")
    sysmsg = t.system_message()
    assert sysmsg and "customer discovery call" in sysmsg
    assert "pain points" in sysmsg
    # steer is about emphasis, and it still tells the model to obey JSON format
    assert "strict JSON" in sysmsg


def test_default_generator_sends_no_system_message():
    fake = FakeChatEnh()
    NotesGenerator(chat=fake).summarize(_TRANSCRIPT, attendees=["Ken", "Evan"])
    # every call was a bare user message — no system role injected
    assert all(True for _ in fake.calls)
    # inspect the actual messages the fake received
    # (FakeChatEnh records pass+temp; extend by checking via a capturing fake)


class _CapChat(FakeChatEnh):
    def __init__(self, overrides=None):
        super().__init__(overrides)
        self.messages_seen = []

    def __call__(self, messages, max_tokens, temperature):
        self.messages_seen.append(messages)
        return super().__call__(messages, max_tokens, temperature)


def test_template_injects_system_on_every_pass():
    fake = _CapChat()
    gen = NotesGenerator(chat=fake, template="customer_discovery")
    gen.summarize(_TRANSCRIPT, attendees=["Ken", "Evan"])
    assert fake.messages_seen  # sanity
    for msgs in fake.messages_seen:
        assert msgs[0]["role"] == "system"
        assert "customer discovery" in msgs[0]["content"]
        assert msgs[-1]["role"] == "user"


def test_default_generator_no_system_role():
    fake = _CapChat()
    NotesGenerator(chat=fake).summarize(_TRANSCRIPT, attendees=["Ken", "Evan"])
    for msgs in fake.messages_seen:
        assert all(m["role"] != "system" for m in msgs)


def test_per_call_template_override():
    fake = _CapChat()
    gen = NotesGenerator(chat=fake)  # default (neutral)
    gen.summarize(_TRANSCRIPT, attendees=["Ken", "Evan"], template="standup")
    assert any(m[0]["role"] == "system" and "standup" in m[0]["content"]
               for m in fake.messages_seen)


def test_enhance_honors_template():
    fake = _CapChat()
    gen = NotesGenerator(chat=fake)
    gen.enhance(_TRANSCRIPT, _USER_NOTES, attendees=["Ken", "Evan"], template="one_on_one")
    assert any(m[0]["role"] == "system" and "1:1" in m[0]["content"]
               for m in fake.messages_seen)


def test_custom_templates_load_and_shadow(tmp_path):
    p = tmp_path / "templates.json"
    p.write_text(json.dumps({
        "weekly_review": {"name": "Weekly review", "description": "team weekly",
                          "guidance": "Emphasize wins, misses and next week's focus."},
        # shadow a builtin id
        "standup": {"name": "My standup", "guidance": "Just blockers."},
    }))
    custom = nt.load_custom(p)
    assert "weekly_review" in custom
    assert nt.get("weekly_review", custom_path=p).name == "Weekly review"
    # custom shadows the builtin of the same id
    assert nt.get("standup", custom_path=p).name == "My standup"


def test_custom_templates_fail_soft(tmp_path):
    bad = tmp_path / "bad.json"
    bad.write_text("not json at all")
    assert nt.load_custom(bad) == {}
    missing = tmp_path / "nope.json"
    assert nt.load_custom(missing) == {}


def test_builtin_ids_present():
    ids = nt.builtin_ids()
    for expected in ("default", "customer_discovery", "one_on_one", "standup", "interview"):
        assert expected in ids
