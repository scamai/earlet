"""The worker's offline and provider guards, and its use of the host's learned
vocabulary, driven by the frames the hosts send.

Each scenario launches the staged worker the way AsrWorkerHost (Windows) and
PythonAsrTransport (macOS) do, with the init and transcribe frames each
actually builds, and reports whether the guard held. The same scenario is then
rerun against a copy of the worker with that guard deleted, which must turn it
red: a guard test that cannot fail is not evidence. The offline guards'
mutations are below; the learned-vocabulary ones are WORKER_ROWS in
test_guards_can_fire.py. Fake providers record what reached them.
"""
import base64
import json
import re
import sys

import pytest

from harness import (FAKE_FASTER, FAKE_MLX, FAKE_OPENAI, MISSING, ROOT, ct2_model, mac_init, mac_transcribe,
                     mlx_model, run_worker, staged_runtime, user_data, windows_init, windows_transcribe,
                     write_providers)

# ─── The frames are the hosts' frames ────────────────────────────────────────


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


def test_the_windows_init_frame_is_the_one_AsrWorkerHost_sends(tmp_path):
    protocol = read("windows/Dictation.Speech/AsrWorkerProtocol.cs")
    record = re.search(r"public sealed record AsrInitRequest\b(.*?)\n}\n", protocol, re.S).group(1)
    keys = set(re.findall(r'JsonPropertyName\("([^"]+)"\)', record))
    assert keys == set(windows_init(tmp_path)), "AsrInitRequest changed: update harness.windows_init"
    plan = read("windows/Dictation.Core/DictationWorkerPlan.cs")
    assert '["DICTATION_ASR_ENGINE"] = "faster"' in plan
    options = re.search(r"public TranscriberOptions Transcriber => new\((.*?)\);", plan, re.S).group(1)
    for field in ("ModelDirectory: ModelDirectory", "HuggingFaceId: ModelDirectory", "UseQnn: false",
                  "Backend: TranscriberBackends.Portable", "AllowModelDownload: false"):
        assert field in options, field
    assert 'public const string Portable = "mac";' in read("windows/Dictation.Speech/Transcription.cs")


def test_the_macos_init_frame_is_the_one_PythonAsrTransport_sends():
    entry = read("macos/Sources/DictationAppCore/DictationEnvironment.swift")
    assert re.search(r"modelDir: resources.model, hfId: resources.model, allowModelDownload: false", entry)
    assert '"DICTATION_ASR_ENGINE": "mlx"' in entry
    transport = read("macos/Sources/DictationKit/DictationAsrTransport.swift")
    for field in ('"backend": .string("mac")', '"hf_id": .string(modelId)', 'fields["model_dir"]',
                  'fields["allow_model_download"] = .bool(allowModelDownload)'):
        assert field in transport, field


# ─── Scenarios: (guard held?, what happened) ─────────────────────────────────

def macos_model_without_weights(runtime, tmp_path):
    """allow_model_download=false and no weights: refuse before any provider."""
    providers = write_providers(tmp_path / "providers", mlx_whisper=FAKE_MLX)
    model = mlx_model(tmp_path / "model", ["config.json"])
    replies, events, stderr = run_worker(runtime, [mac_init(model), {"op": "shutdown"}], "mlx",
                                         providers, tmp_path)
    init = replies[0] if replies else {}
    held = init.get("ok") is False and "no active speech-model" in init.get("error", "") and events == []
    return held, (init, events, stderr[-400:])


def macos_mlx_missing(runtime, tmp_path):
    """The pinned MLX provider is absent: fail, never fall back to another engine."""
    providers = write_providers(tmp_path / "providers", mlx_whisper=MISSING,
                                faster_whisper=FAKE_FASTER, whisper=FAKE_OPENAI)
    model = mlx_model(tmp_path / "model")
    replies, events, stderr = run_worker(runtime, [mac_init(model), {"op": "shutdown"}], "mlx",
                                         providers, tmp_path)
    init = replies[0] if replies else {}
    held = init.get("ok") is False and "ImportError" in init.get("error", "") and events == []
    return held, (init, events, stderr[-400:])


def windows_incomplete_model(runtime, tmp_path):
    """Windows' own frame and a CTranslate2 directory missing its tokenizer:
    refuse before the provider sees it."""
    providers = write_providers(tmp_path / "providers", faster_whisper=FAKE_FASTER)
    model = ct2_model(tmp_path / "model", ["model.bin", "config.json"])
    replies, events, stderr = run_worker(runtime, [windows_init(model), {"op": "shutdown"}], "faster",
                                         providers, tmp_path)
    init = replies[0] if replies else {}
    reached = [event for event in events if event[0] == "load"]
    return init.get("ok") is False and reached == [], (init, events, stderr[-400:])


def windows_turbo_without_feature_config(runtime, tmp_path):
    """A 128-mel export (Large v3, Turbo) whose folder lost
    preprocessor_config.json: faster-whisper would build 80-bin features and
    fail every decode, so init refuses it by name."""
    providers = write_providers(tmp_path / "providers", faster_whisper=FAKE_FASTER)
    model = ct2_model(tmp_path / "model", ["model.bin", "config.json", "tokenizer.json"])
    replies, events, stderr = run_worker(runtime, [windows_init(model), {"op": "shutdown"}], "faster",
                                         providers, tmp_path, env={"FAKE_PROVIDER_MEL_BINS": "128"})
    init = replies[0] if replies else {}
    held = init.get("ok") is False and init.get("error", "").startswith("MelBinMismatch: ")
    return held, (init, events, stderr[-400:])


@pytest.mark.parametrize("scenario", [macos_model_without_weights, macos_mlx_missing,
                                      windows_turbo_without_feature_config])
def test_the_guard_holds_for_the_frame_the_host_sends(tmp_path, scenario):
    held, detail = scenario(staged_runtime(tmp_path), tmp_path)
    assert held, detail


def test_the_windows_frame_refuses_an_incomplete_model_before_the_provider(tmp_path):
    held, detail = windows_incomplete_model(staged_runtime(tmp_path), tmp_path)
    assert held, detail


@pytest.mark.parametrize("model_bins, names", [
    ("128", ("model.bin", "config.json", "tokenizer.json", "preprocessor_config.json")),
    ("80", ("model.bin", "config.json", "tokenizer.json"))], ids=["turbo with its config", "80-mel without one"])
def test_a_model_whose_features_match_starts(tmp_path, model_bins, names):
    """The mirror of the mel check: a Turbo folder with its feature config,
    and an 80-mel model whose default features are right, both start."""
    providers = write_providers(tmp_path / "providers", faster_whisper=FAKE_FASTER)
    model = ct2_model(tmp_path / "model", names)
    replies, _, stderr = run_worker(staged_runtime(tmp_path), [windows_init(model), {"op": "shutdown"}],
                                    "faster", providers, tmp_path, env={"FAKE_PROVIDER_MEL_BINS": model_bins})
    assert [reply.get("ok") for reply in replies] == [True, True], (replies, stderr[-400:])


# ─── Deleting a guard turns its scenario red ─────────────────────────────────

GUARDS = {
    "offline guard": ("        if not self.allow_model_download and not has_local_model:",
                      "        if False:"),
    "mlx no-fallback": ("                raise  # An explicit provider never falls back to another engine.",
                        "                pass"),
    "mel bins checked": ("                check_faster_mel_bins(self._fw_model)\n", ""),
}


@pytest.mark.parametrize("guard, scenario", [("offline guard", macos_model_without_weights),
                                             ("mlx no-fallback", macos_mlx_missing),
                                             ("mel bins checked", windows_turbo_without_feature_config)])
def test_deleting_the_guard_is_detected(tmp_path, guard, scenario):
    runtime = staged_runtime(tmp_path)
    source = runtime / "transcription_worker/whisper_mac.py"
    anchor, replacement = GUARDS[guard]
    text = source.read_text(encoding="utf-8")
    assert text.count(anchor) == 1, f"the {guard} moved; update GUARDS so this test still fires"
    source.write_text(text.replace(anchor, replacement), encoding="utf-8")
    held, _ = scenario(runtime, tmp_path)
    assert not held


# ─── Learned vocabulary through the frames the hosts send (LEARNING.md §6) ───
#
# The host writes <data>/dictation/learning.json; the worker, launched the way
# each host launches it, must put the learned words in front of the provider
# and rewrite with the learned aliases, and a damaged file must change
# nothing. Fake providers: this is wiring evidence, not recognition accuracy.


def test_the_windows_transcribe_frame_is_the_one_AsrWorkerTranscriber_sends():
    protocol = read("windows/Dictation.Speech/AsrWorkerProtocol.cs")
    record = re.search(r"public sealed record AsrTranscribeRequest\b(.*?)\n}\n", protocol, re.S).group(1)
    keys = set(re.findall(r'JsonPropertyName\("([^"]+)"\)', record))
    assert set(windows_transcribe()) <= keys, "AsrTranscribeRequest changed: update harness.windows_transcribe"
    assert "WhenWritingNull" in protocol
    plan = read("windows/Dictation.Core/DictationWorkerPlan.cs")
    # The language defaults to automatic (null, so absent from the frame); the
    # picker's choice becomes the plan's Language and reaches every request.
    assert "BeamSize: 4," in plan and "Language: Language," in plan
    assert "public string? Language { get; init; }\n" in plan
    transcriber = read("windows/Dictation.Speech/AsrWorkerTranscriber.cs")
    assert "TextPasses = true," in transcriber
    assert "HallucinationGuard" not in transcriber, "the transcriber now sets hallucination_guard"


def test_the_macos_transcribe_frame_is_the_one_DictationAsrTransport_sends():
    transport = read("macos/Sources/DictationKit/DictationAsrTransport.swift")
    # The transcribe request: from its field literal to the send.
    request = re.search(r'var fields: \[String: JSONValue\] = \[\s*"pcm_b64": \.string\(pcmB64\)(.*?)'
                        r'client\.sendFlat\(op: "transcribe"', transport, re.S).group(1)
    for field in ('"sample_rate": .number', '"channel": .string(Self.channel)', '"utc_start": .string',
                  'fields["voiced_fraction"]', 'fields["hallucination_guard"]'):
        assert field in request, field
    assert 'private static let channel = "dictation"' in transport
    assert '"client"' not in request and '"text_passes"' not in request
    # Recover Last Dictation's decode adds `recovery: true` to this frame,
    # and no other decode does (test_the_junk_filter_drops_a_hold_and_its_recovery_keeps_it).
    assert 'if recovery { fields["recovery"] = .bool(true) }' in request
    assert 'compareBaseline: compareBaseline, recovery: false)' in transport
    config = read("macos/Sources/DictationAudioCapture/AsrModelConfig.swift")
    dictation = re.search(r"static let dictation = AsrModelConfig\((.*?)\)", config, re.S)
    assert dictation and "hallucinationGuard: false" in dictation.group(1)


def write_learning(tmp_path, words=(), aliases=(), raw=None, origin="correction"):
    """learning.json in the folder this run's worker reads, in the host's shape."""
    path = user_data(tmp_path) / "learning.json"
    path.write_text(raw if raw is not None else json.dumps({
        "schema": "dictation.learning/1",
        "words": [{"text": w, "origin": origin, "count": 1,
                   "first_seen": 1790000000000, "last_seen": 1790000000000} for w in words],
        "aliases": [{"heard": h, "meant": m, "origin": "correction", "count": 2,
                     "first_seen": 1790000000000, "last_seen": 1790000000000} for h, m in aliases],
        "pending": [], "removed": {"words": [], "aliases": []},
    }), encoding="utf-8")
    return path


def write_user_aliases(tmp_path, aliases, removed=()):
    (user_data(tmp_path) / "bias_aliases.json").write_text(
        json.dumps({"aliases": aliases, "removed": list(removed)}), encoding="utf-8")


HOSTS = {
    "windows": ("faster", windows_init, windows_transcribe, "faster_whisper", FAKE_FASTER, ct2_model),
    "macos": ("mlx", mac_init, mac_transcribe, "mlx_whisper", FAKE_MLX, mlx_model),
}


def dictate(tmp_path, host, said, echo=False, fields=None):
    """One hold through the staged worker: init and transcribe exactly as
    ``host`` frames them, the provider decoding ``said`` (and then, with
    ``echo``, continuing the prompt it received). ``fields`` replaces
    transcribe fields, for a caller other than the host. Returns the
    transcribe reply, the prompt the provider received, and stderr."""
    engine, init, transcribe, provider, fake, model = HOSTS[host]
    runtime = staged_runtime(tmp_path)
    providers = write_providers(tmp_path / "providers", **{provider: fake})
    env = {"FAKE_PROVIDER_TEXT": said, **({"FAKE_PROVIDER_ECHO_PROMPT": "1"} if echo else {})}
    replies, events, stderr = run_worker(runtime, [init(model(tmp_path / "model")),
                                                   {**transcribe(), **(fields or {})},
                                                   {"op": "shutdown"}],
                                         engine, providers, tmp_path, env=env)
    assert [reply.get("ok") for reply in replies] == [True, True, True], stderr[-800:]
    calls = [event for event in events if event[0] == "transcribe"]
    assert len(calls) == 1, events
    prompt = calls[0][2]["initial_prompt"] if provider == "faster_whisper" else calls[0][3]
    return replies[1], prompt, stderr


# ─── A dictated sign-off is typed ────────────────────────────────────────────
#
# The junk filter's stock-phrase stems ("see you next", "stay tuned") were
# built for meeting windows over near-silence. Both hosts send
# hallucination_guard=false for dictation, and a sign-off dictated as its own
# hold is a message: it used to decode correctly and come back empty with
# ok:true, the host blaming the microphone. Mutations: test_guards_can_fire.

SIGN_OFFS = ("See you next week, Evan.", "Stay tuned for the launch update.")


@pytest.mark.parametrize("host", HOSTS)
@pytest.mark.parametrize("said", SIGN_OFFS)
def test_a_dictated_sign_off_is_typed(tmp_path, host, said):
    reply, _, _ = dictate(tmp_path, host, " " + said)

    assert reply["text"] == said
    assert reply["dropped_reason"] is None


def test_a_caller_that_asks_for_the_guard_still_loses_a_stock_sign_off(tmp_path):
    """The meeting-notes frame (client notes, no text passes, the guard on):
    a short "Thanks for watching" over a quiet window is the model filling a
    gap, and it is still dropped, by the junk filter, not the blocklist."""
    notes = {"client": "notes", "text_passes": False, "hallucination_guard": True}

    reply, _, _ = dictate(tmp_path, "windows", " Thanks for watching!", fields=notes)

    assert reply["text"] == ""
    assert reply["dropped_reason"] == "junk-filter"


# ─── An empty hold says why; its recovery keeps the words ────────────────────
#
# The Mac's ending reads the reply's `dropped` word, keeps a take a gate
# emptied, and recovers it with `recovery: true`, which skips the speech
# checks that emptied it. Mutations: test_guards_can_fire.py.

LOOPED = "the the the the the the the"


def test_the_junk_filter_drops_a_hold_and_its_recovery_keeps_it(tmp_path):
    (tmp_path / "hold").mkdir()
    (tmp_path / "recovery").mkdir()
    held, _, _ = dictate(tmp_path / "hold", "macos", " " + LOOPED)
    recovered, _, _ = dictate(tmp_path / "recovery", "macos", " " + LOOPED, fields={"recovery": True})

    assert held["text"] == "" and held["dropped"] == "junk"
    assert recovered["text"] == LOOPED and "dropped" not in recovered


def test_a_hold_with_words_carries_no_drop_word(tmp_path):
    reply, _, _ = dictate(tmp_path, "macos", " send the report today")

    assert reply["text"] == "send the report today" and "dropped" not in reply


@pytest.mark.parametrize("recovery", [False, True], ids=["hold", "recovery"])
def test_a_silent_hold_is_dropped_as_quiet_before_the_provider(tmp_path, recovery):
    providers = write_providers(tmp_path / "providers", mlx_whisper=FAKE_MLX)
    silence = {**mac_transcribe(), "pcm_b64": base64.b64encode(bytes(32000)).decode("ascii"),
               **({"recovery": True} if recovery else {})}
    replies, events, stderr = run_worker(staged_runtime(tmp_path), [mac_init(mlx_model(tmp_path / "model")),
                                                                    silence, {"op": "shutdown"}],
                                         "mlx", providers, tmp_path)

    assert [reply.get("ok") for reply in replies] == [True, True, True], stderr[-800:]
    assert replies[1]["text"] == "" and replies[1]["dropped"] == "quiet"
    assert not [event for event in events if event[0] == "transcribe"]


VOCABULARY =("SkyDock", "Trovioo", "sky dock", "trovio")


@pytest.mark.parametrize("host", HOSTS)
def test_learned_words_reach_the_provider(tmp_path, host):
    """Stored most recently seen first, written most relevant LAST, where the
    prompt window keeps it."""
    write_learning(tmp_path, words=["SkyDock", "Trovioo"])

    reply, prompt, stderr = dictate(tmp_path, host, " we met at the office")

    assert prompt is not None and prompt.endswith(" Trovioo, SkyDock"), prompt
    assert reply["stats"]["learned"] == {"words": 2, "aliases": 0}
    assert reply["stats"]["personalization"].startswith("L1-prompt")


def write_scene_learning(tmp_path):
    """Two tentative learned words, `Noise` stored first, and one correction
    in the terminal scene that supports `Miles` (LEARNING.md §4.7)."""
    state = {
        "schema": "dictation.learning/1",
        "words": [{"text": w, "origin": "correction", "count": 1, "opposition": 0, "adaptive": True,
                   "first_seen": 1790000000000, "last_seen": 1790000000000} for w in ("Noise", "Miles")],
        "aliases": [], "pending": [], "removed": {"words": [], "aliases": []},
        "evidence": {"version": 1, "observations": [{
            "id": "one", "at": 1790000000000, "weight": 1, "scene": "terminal",
            "corrections": [{"heard": "mies", "meant": "Miles", "word": True, "alias": False, "secret": False}]}]},
    }
    write_learning(tmp_path, raw=json.dumps(state))


@pytest.mark.parametrize("host", HOSTS)
def test_the_press_time_scene_orders_the_final_prompt(tmp_path, host):
    """Each host's final frame carries the scene captured at the press, and
    the Mac's names no client (pinned above; absent means dictation). In the
    terminal the word its scene's correction supports moves to the end the
    prompt window keeps; in WeChat, and with no scene, the stored order
    stands. Until 2026-10-17 the worker read the client field raw, so the
    Mac's final prompt never followed its scene."""
    prompts = {}
    for scene in ("terminal", "wechat", None):
        root = tmp_path / (scene or "none")
        root.mkdir()
        write_scene_learning(root)
        _, prompts[scene], _ = dictate(root, host, " we met at the office",
                                       fields={"vocabulary_scene": scene} if scene else None)

    assert prompts["terminal"].endswith(" Noise, Miles"), prompts["terminal"]
    assert prompts["wechat"].endswith(" Miles, Noise"), prompts["wechat"]
    assert prompts[None].endswith(" Miles, Noise"), prompts[None]


@pytest.mark.parametrize("host", HOSTS)
def test_learned_aliases_rewrite_the_transcript(tmp_path, host):
    write_learning(tmp_path, aliases=[("trovio", "Trovioo"), ("sky dock", "SkyDock")])

    reply, _, stderr = dictate(tmp_path, host, " we met at sky dock with trovio")

    assert reply["text"] == "we met at SkyDock with Trovioo"
    assert reply["stats"]["learned"] == {"words": 0, "aliases": 2}


@pytest.mark.parametrize("host", HOSTS)
def test_the_workers_log_never_carries_the_learned_vocabulary(tmp_path, host):
    """Both hosts copy worker stderr into their logs (LEARNING.md §6)."""
    write_learning(tmp_path, words=["SkyDock", "Trovioo"], aliases=[("trovio", "Trovioo"), ("sky dock", "SkyDock")])

    reply, _, stderr = dictate(tmp_path, host, " we met at sky dock with trovio")

    assert reply["text"] == "we met at SkyDock with Trovioo"
    assert "aliases applied" in stderr, "the rewrite must still be visible as a count"
    for word in VOCABULARY:
        assert word not in stderr, f"{word!r} reached the worker's stderr"


@pytest.mark.parametrize("host", HOSTS)
def test_a_self_containing_alias_is_rewritten_once(tmp_path, host):
    """`flow -> Fable Flow`: the spoken `flow` is expanded, and the `Flow`
    inside a `Fable Flow` the recognizer already wrote is left alone."""
    write_user_aliases(tmp_path, {"flow": "Fable Flow"})

    reply, _, _ = dictate(tmp_path, host, " I use flow, not Fable Flow")

    assert reply["text"] == "I use Fable Flow, not Fable Flow"


@pytest.mark.parametrize("host", HOSTS)
def test_the_alias_table_runs_once_per_hold(tmp_path, host):
    """Two taught aliases where one's spoken form sits inside the other's
    written form (`gpt` in `GPT-4o`). The engine applies the table and the
    server must not apply it again, or "GPT-4o" becomes "ChatGPT-4o"."""
    write_learning(tmp_path, aliases=[("gpt four oh", "GPT-4o"), ("gpt", "ChatGPT")])

    reply, _, stderr = dictate(tmp_path, host, " ask gpt four oh, or just gpt")

    assert reply["text"] == "ask GPT-4o, or just ChatGPT"
    assert "asr_server: aliases applied" not in stderr
    assert "spoken_aliases_applied" not in reply["stats"]


#: Stored most recently seen first, so the prompt writes them in reverse.
ECHOED = ["Trovioo", "Knemo", "ArcFace", "PadForge"]


@pytest.mark.parametrize("host", HOSTS)
def test_an_echoed_prompt_is_removed(tmp_path, host):
    """The decoder continues its prompt (measured on 1% of code-mixed clips
    on mlx): the learned words come back in the order the prompt WROTE them,
    and the worker removes them before they are pasted."""
    write_learning(tmp_path, words=ECHOED)

    reply, prompt, stderr = dictate(tmp_path, host, " 你能帮我看一下?", echo=True)

    assert prompt.endswith(" PadForge, ArcFace, Knemo, Trovioo"), prompt
    assert reply["text"] == "你能帮我看一下?"
    assert "dropped 4 regurgitated" in stderr, stderr[-800:]


@pytest.mark.parametrize("host", HOSTS)
def test_the_learned_words_spoken_in_stored_order_are_kept(tmp_path, host):
    """The mirror: the same words in the order the host stored them are not
    the prompt coming back, so they are the user's own words."""
    write_learning(tmp_path, words=ECHOED)

    reply, _, stderr = dictate(tmp_path, host, " 我们用了 Trovioo, Knemo, ArcFace, PadForge")

    assert reply["text"] == "我们用了 Trovioo, Knemo, ArcFace, PadForge"
    assert "regurgitated" not in stderr


@pytest.mark.parametrize("host", HOSTS)
def test_a_word_learned_from_one_correction_only_restores_case(tmp_path, host):
    """One fix of "the service now" to "ServiceNow" learns the word and holds
    the spaced form back for a second dictation (LEARNING.md §4.1, §4.2).
    Until then the word restores case, and "service now" in prose stays as
    it was said, through the engine's casing pass and the server's."""
    write_learning(tmp_path, words=["ServiceNow"])

    reply, _, _ = dictate(tmp_path, host, " call the service now, not the servicenow desk")

    assert reply["text"] == "call the service now, not the ServiceNow desk"


@pytest.mark.parametrize("host", HOSTS)
def test_a_taught_word_joins_its_spaced_form(tmp_path, host):
    """Teaching declares the term, which is what the join is priced against."""
    write_learning(tmp_path, words=["ServiceNow"], origin="taught")

    reply, _, _ = dictate(tmp_path, host, " call the service now, not the servicenow desk")

    assert reply["text"] == "call the ServiceNow, not the ServiceNow desk"


def test_the_users_alias_file_outranks_learned_aliases(tmp_path):
    """LEARNING.md §1: the user's rows win over learned ones, and the user's
    `removed` list suppresses a learned alias as it does a shipped one."""
    write_learning(tmp_path, aliases=[("trovio", "Trovioo"), ("sky dock", "SkyDock")])
    write_user_aliases(tmp_path, {"Trovio": "TROVIOO Inc"}, removed=["SKY DOCK"])

    reply, _, _ = dictate(tmp_path, "windows", " we met at sky dock with trovio")

    assert reply["text"] == "we met at sky dock with TROVIOO Inc"


SECRET = "Zqxwvutsrq"
MALFORMED = {
    "unparsable": '{"schema": "dictation.learning/1", "words": [{"text": "' + SECRET + '"}',
    "other schema": json.dumps({"schema": "dictation.learning/2", "words": [{"text": SECRET}],
                                "aliases": [{"heard": "office", "meant": SECRET}]}),
}


@pytest.mark.parametrize("host", HOSTS)
@pytest.mark.parametrize("content", MALFORMED)
def test_a_malformed_learning_file_changes_nothing(tmp_path, host, content):
    """The hold decodes exactly as with no file: no prompt, no rewrite, and
    one note on stderr that quotes nothing -- not a skipped pass."""
    write_learning(tmp_path, raw=MALFORMED[content])

    reply, prompt, stderr = dictate(tmp_path, host, " we met at the office")

    assert prompt is None
    assert reply["text"] == "we met at the office"
    assert reply["stats"]["learned"] == {"words": 0, "aliases": 0}
    assert stderr.count("learned_vocabulary:") == 1, stderr[-800:]
    assert "skipped" not in stderr, stderr[-800:]
    assert SECRET not in stderr


def test_with_no_learning_file_the_provider_call_is_unchanged(tmp_path):
    """The Windows call option for option as it was before learned words:
    no prompt, the same anti-hallucination pair."""
    reply, prompt, _ = dictate(tmp_path, "windows", " hello from faster")
    events = [line for line in (tmp_path / "provider-events.jsonl").read_text().splitlines()
              if '"transcribe"' in line]

    assert prompt is None
    assert json.loads(events[0])[2] == {"language": None, "beam_size": 4, "initial_prompt": None,
                                        "condition_on_previous_text": False,
                                        "no_speech_threshold": 0.6, "log_prob_threshold": -1.0}
    assert reply["stats"]["learned"] == {"words": 0, "aliases": 0}


def test_the_worker_reads_what_the_learning_reference_writes(tmp_path):
    """Every shared golden sequence (tests/fixtures/learning-golden.json),
    written by the reference exactly as a host persists it, read back by the
    shipped reader: the words and aliases §6 says the worker uses."""
    from test_learning_golden import GOLDEN, _run

    sys.path.insert(0, str(ROOT / "runtime"))
    from transcription_worker import learned_vocabulary

    for index, case in enumerate(GOLDEN["sequences"]):
        learning, _ = _run(case.get("lexicon", GOLDEN["lexicon"]), case["steps"])
        path = tmp_path / f"{index}" / "learning.json"
        path.parent.mkdir()
        path.write_text(json.dumps(learning.state), encoding="utf-8")

        read = learned_vocabulary.load(path)

        # The host lists by recency; recognition now prioritizes confidence.
        # Explicit shared expectations cover sequences where those orders differ.
        words = case.get("expect_worker_words", case["expect"]["words"])
        assert (read.words, read.aliases) == (words, case["expect"]["aliases"]), case["name"]
        # The origin decides whether a word may be respaced (asr_server._case_only).
        origins = {word["text"]: word["origin"] for word in learning.state["words"]}
        assert read.origins == [origins[word] for word in words], case["name"]
