"""Private helper: run installed MLX code with a marked scratch vocabulary.

Launched by acceptance.py using the app's own Python. The sole path hook moves
the worker's vocabulary read to scratch. It does not launch the native host.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys
import time

from acceptance import MARKER, pcm_base64, read_bounded, run_process


def require(condition, reason):
    if not condition:
        raise AssertionError(reason)


def validate_scratch(folder: Path):
    require(folder.is_absolute() and folder.resolve() == folder and folder.name.startswith("dictation-acceptance-"),
            "expected a canonical dictation-acceptance-* scratch directory")
    require(read_bounded(folder / ".acceptance-scratch").decode() == MARKER, "invalid scratch marker")
    for name in ("learning.json", "bias_aliases.json"):
        require(not (folder / name).is_symlink(), "scratch vocabulary must not be a symlink")


def pipeline(args, server, learned_vocabulary, mlx_beam, whisper_mac):
    store = args.scratch / "learning.json"
    now = int(time.time() * 1000)
    # More legacy global words than the beam budget, so scene ranking is observable.
    store.write_text(json.dumps({"schema": "dictation.learning/1", "words": [
        {"text": f"ContextProject{i}", "origin": "correction", "count": 4,
         "first_seen": now, "last_seen": now} for i in range(40)],
        "aliases": [], "pending": [], "removed": {"words": [], "aliases": []}}), encoding="utf-8")
    trace = []
    prompts = []
    original = mlx_beam.VocabularyModel.build_trie
    make_prompt = whisper_mac._vocabulary_prompt

    def prompted(*values, **options):
        prompt = make_prompt(*values, **options)
        prompts.append(prompt)
        return prompt

    def traced(model, tokenizer):
        trace.append({phrase: model.phrase_deltas.get(index, model.delta) for index, phrase in enumerate(model.phrases)})
        return original(model, tokenizer)

    mlx_beam.VocabularyModel.build_trie = traced
    whisper_mac._vocabulary_prompt = prompted
    init = server.op_init({"backend": "mac", "model_dir": str(args.resources / "model"), "allow_model_download": False})
    require(init.get("ok") is True, "installed MLX initialization failed")
    pcm = pcm_base64(args.audio)
    rows = []

    def correct(identity, meant, scene):
        output = run_process([args.probe, store, identity, "we use Mies", "we use " + meant, scene], timeout=30)
        require(output.returncode == 0, "Swift learning probe failed: " + output.stderr.decode(errors="replace"))
        return json.loads(output.stdout)

    def run(stage, scene, selected, delta=None):
        learned = learned_vocabulary.load(store, scene=scene)
        trace.clear()
        prompts.clear()
        begin = time.monotonic()
        response = server.op_transcribe({"client": "dictation", "vocabulary_scene": scene,
                                        "language": "en", "pcm_b64": pcm, "text_passes": True})
        require(response.get("ok") and response.get("text"), stage + ": recognition failed")
        search = response.get("stats", {}).get("vocabulary_search", {})
        require(search.get("beam_windows", 0) > 0 and search.get("fallback") is None,
                stage + ": vocabulary beam did not run cleanly")
        require(trace and all(("Miles" in trie) == selected for trie in trace), stage + ": wrong scene selection")
        require(any(isinstance(prompt, str) and prompt for prompt in prompts), stage + ": no vocabulary prompt built")
        if selected:
            require(all(abs(trie["Miles"] - delta) < 1e-8 for trie in trace), stage + ": wrong confidence delta")
            require(re.search(r"\bMiles\b", response["text"], re.IGNORECASE),
                    stage + ": synthetic target word Miles was not recognized")
        require(not learned.aliases, "ordinary Mies was incorrectly admitted as a forced alias")
        observations = json.loads(read_bounded(store))["evidence"]["observations"]
        votes = [{"id": row["id"], "scene": row.get("scene")} for row in observations
                 if row["id"] in ("terminal-draft", "chat-draft")]
        rows.append({"stage": stage, "scene": scene, "miles_selected": selected, "delta": delta,
                     "votes": votes, "text": response["text"], "search": search, "prompt_built": True,
                     "elapsed_ms": round((time.monotonic() - begin) * 1000, 2)})
        print(f"PASS {stage}", flush=True)

    first = correct("terminal-draft", "Miles", "terminal")
    require(first["save"] == "saved" and first["wordsLearned"] == 1, "first correction was not learned")
    run("first-local-correction", "terminal", True, .2)
    run("cross-app-global-fallback", "wechat", False)
    repeated = correct("terminal-draft", "Miles", "wechat")
    require(repeated["save"] == "unchanged", "same draft was counted a second time")
    run("same-draft-retains-original-scene", "wechat", False)
    correct("chat-draft", "Miles", "wechat")
    run("independent-chat-confirmation", "wechat", True, .35)
    correct("chat-draft", "Mies", "wechat")
    run("chat-retraction", "wechat", False)
    run("terminal-contribution-remains", "terminal", True, .2)
    correct("terminal-draft", "Mies", "terminal")
    run("all-retracted", "terminal", False)
    require(not any(row.get("text") == "Miles" for row in json.loads(read_bounded(store))["words"]),
            "Miles survived complete retraction")
    # Exercise the third stage separately with a distinctive, eligible alias.
    # This is a deterministic text-pass probe, not an audio accuracy claim.
    def alias_correction(identity):
        output = run_process([args.probe, store, identity, "use trovio", "use Trovioo", "terminal"], timeout=30)
        require(output.returncode == 0, "alias learning probe failed")

    alias_correction("alias-first")
    require("trovio" not in learned_vocabulary.load(store).aliases, "alias learned too early")
    alias_correction("alias-first")
    require("trovio" not in learned_vocabulary.load(store).aliases, "same draft advanced alias threshold")
    alias_correction("alias-second")
    require(learned_vocabulary.load(store).aliases.get("trovio") == "Trovioo", "independent confirmation did not admit alias")
    rewritten = {"text": "use trovio", "stats": {}}
    server._apply_text_passes(rewritten, [])
    require(rewritten["text"] == "use Trovioo", "installed postprocessing did not apply learned alias")
    print("PASS confirmed-alias-postprocessing", flush=True)
    (args.scratch / "pipeline.json").write_text(json.dumps({"stages": rows, "source_policy_to_installed_worker": True,
                                                           "confirmed_alias_postprocessing": True,
                                                           "native_edit_capture": "NOT_RUN"}, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("resources", "scratch", "audio", "probe"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--wire", action="store_true")
    args = parser.parse_args()
    validate_scratch(args.scratch)
    sys.path.insert(0, str(args.resources / "runtime"))
    from transcription_worker import asr_server, learned_vocabulary, mlx_beam, spoken_aliases, whisper_mac
    spoken_aliases.default_path = lambda: args.scratch / "bias_aliases.json"
    if args.wire:
        # This is the real installed server loop and framing, isolated by the same path hook.
        asr_server.main()
    else:
        pipeline(args, asr_server, learned_vocabulary, mlx_beam, whisper_mac)


if __name__ == "__main__":
    main()
