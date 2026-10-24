"""Deleting a guard must turn the test that claims it red.

Each row removes one guard from a copy of tools/package.py, reruns the test
that claims the guard against that copy, and expects it to fail. WORKER_ROWS
do the same to the worker's guards (its learned-vocabulary handling,
LEARNING.md §1, §5, §6; the sign-off junk filter; the live preview's optional
imports and commit path), through a mutated staged worker the claim launches
as a host would. The
boundary check's mutations are the cases in test_boundary.py; the worker's
offline guards are in test_worker_frames.py. A row whose anchor no longer
matches fails: update the row, do not delete it.
"""
import inspect
from pathlib import Path

import pytest

import harness
import test_package as claims
import test_product
import test_worker_frames as worker_claims

WINDOWS_MODEL = harness.CT2_MODEL


def row(guard, anchor, replacement, claim, **parameters):
    return pytest.param(anchor, replacement, claim, parameters, id=guard)


ROWS = [
    row("worker allowlist", "    for source in worker_files():\n",
        "    for source in sorted((ROOT / 'runtime/transcription_worker').glob('*.py')):\n",
        claims.test_the_package_ships_exactly_the_declared_worker),
    row("alias table ships", 'WORKER_DATA = ("default_aliases.json",)', "WORKER_DATA = ()",
        claims.test_the_package_ships_exactly_the_declared_worker),
    row("closure declared", '"engine_lock", "hallucination", ', '"engine_lock", ',
        claims.test_every_import_in_a_shipped_module_is_shipped_or_declared_optional),
    row("learned vocabulary ships", '"hallucination", "learned_vocabulary", ', '"hallucination", ',
        claims.test_every_import_in_a_shipped_module_is_shipped_or_declared_optional),
    row("path file extended",
        'facts["python_path_files"] = extend_path_files(resources / "python", runtime, target)',
        'facts["python_path_files"] = []', claims.test_an_embeddable_python_is_given_the_worker_path),
    row("site enabled", '                lines[stripped.index("#import site")] = "import site"', "                pass",
        claims.test_an_embeddable_python_is_given_the_worker_path),
    row("distribution bytecode kept", 'ignore=shutil.ignore_patterns(".pytest_cache", ".DS_Store")',
        'ignore=shutil.ignore_patterns("__pycache__", ".pytest_cache", ".DS_Store")',
        claims.test_the_distributions_own_bytecode_is_kept),
    row("smoke only where it can run", "            run_python = host_runs(target)", "            run_python = True",
        claims.test_a_cross_build_says_the_import_smoke_did_not_run),
    row("smoke runs on the Mac", "            run_python = host_runs(target)", "            run_python = False",
        claims.test_a_staged_python_that_cannot_import_the_worker_fails_the_package),
    row("import smoke", "            import_smoke(executable, runtime, target)\n", "",
        claims.test_a_staged_python_that_cannot_import_the_worker_fails_the_package),
    row("worker bytecode", "            compile_worker(executable, runtime, jobs)\n", "",
        claims.test_a_path_file_python_imports_the_staged_worker),
    row("tokenizer required", '"config.json", "tokenizer.json", "preprocessor_config.json")',
        '"config.json", "preprocessor_config.json")', claims.test_a_complete_package_refuses_an_incomplete_model,
        target="windows", missing="tokenizer.json"),
    row("mel configuration required", '"config.json", "tokenizer.json", "preprocessor_config.json")',
        '"config.json", "tokenizer.json")', claims.test_a_complete_package_refuses_an_incomplete_model,
        target="windows", missing="preprocessor_config.json"),
    row("weights required", '    if target == "macos" and not any(', "    if False and not any(",
        claims.test_a_complete_package_refuses_an_incomplete_model, target="macos", missing="weights.safetensors"),
    row("interpreter required",
        'if not python_executable(python, target).is_file() or (python / "pyvenv.cfg").exists():',
        'if (python / "pyvenv.cfg").exists():', claims.test_a_complete_package_refuses_a_python_without_its_interpreter,
        target="windows"),
    row("virtualenv refused",
        'if not python_executable(python, target).is_file() or (python / "pyvenv.cfg").exists():',
        "if not python_executable(python, target).is_file():",
        test_product.test_packaging_refuses_a_virtualenv_before_copying),
    row("links materialized", "shutil.copytree(source, target, symlinks=False,",
        "shutil.copytree(source, target, symlinks=True,", claims.test_links_in_the_supplied_inputs_are_copied_as_files),
    row("no ARM64 provider", '    if target == "windows" and rid == "win-arm64":', "    if False:",
        claims.test_a_refused_complete_package_builds_nothing, rid="win-arm64", model_names=WINDOWS_MODEL,
        message="ARM64"),
    row("inputs checked before building",
        "    check_inputs(args.platform, args.python_runtime, args.model_dir, args.app_only, args.rid)\n", "",
        claims.test_a_refused_complete_package_builds_nothing, rid="win-x64", model_names=WINDOWS_MODEL[:2],
        message="model files"),
    row("symbols stripped", '    facts["stripped_symbols"] = strip_symbols(bundle)\n',
        '    facts["stripped_symbols"] = []\n', claims.test_a_windows_package_ships_no_symbols),
    row("no symbols built", '"-p:DebugType=none", "-p:DebugSymbols=false",', "",
        claims.test_a_windows_package_ships_no_symbols),
    row("builder audit", "    audit_bundle(bundle, resources)\n", "",
        claims.test_a_package_naming_its_builder_is_refused, marker="source map"),
    row("UTF-16 markers", 'markers += [text.encode("utf-8"), text.encode("utf-16-le")]',
        'markers += [text.encode("utf-8")]', claims.test_a_package_naming_its_builder_is_refused,
        marker="checkout path, UTF-16"),
    row("private source map marker", 'texts = {str(ROOT), "raw.githubusercontent.com/scamai"}', "texts = {str(ROOT)}",
        claims.test_a_package_naming_its_builder_is_refused, marker="source map"),
    row("supplied inputs only warn", "    built = builder_path_hits(bundle, skip=inputs)",
        "    built = builder_path_hits(bundle)", claims.test_supplied_inputs_naming_the_builder_warn_without_failing),
    row("Mac binary stripped",
        '        subprocess.run(["strip", "-S", "-x", str(contents / "MacOS/Dictation")], check=True)\n', "",
        claims.test_a_macos_binary_is_stripped_before_signing),
    row("single instance", '                          "LSMultipleInstancesProhibited": True,\n', "",
        claims.test_the_macos_bundle_is_its_own_single_instance_app),
    row("Windows build stamped", ', f"-p:DictationBuild={build_id}"', "",
        claims.test_a_windows_package_stamps_its_build_for_the_launch_line),
    row("receipt names the build", '"platform": args.platform, "build": build_id,', '"platform": args.platform,',
        claims.test_a_windows_package_stamps_its_build_for_the_launch_line),
]
if len(Path.home().parts) >= 3:
    ROWS.append(row("home marker", "        texts.add(str(home))", "        pass",
                    claims.test_a_package_naming_its_builder_is_refused, marker="home directory"))
if harness.tool("package").source_revision():
    ROWS.append(row("revision marker", "    if revision:\n        texts.add(revision)\n", "",
                    claims.test_a_package_naming_its_builder_is_refused, marker="source revision"))


@pytest.mark.parametrize("anchor, replacement, claim, parameters", ROWS)
def test_deleting_a_package_guard_fails_its_test(anchor, replacement, claim, parameters, tmp_path, monkeypatch,
                                                 capsys):
    source = (harness.ROOT / "tools/package.py").read_text(encoding="utf-8")
    assert source.count(anchor) == 1, "the guard moved: update this row"
    monkeypatch.setitem(harness.OVERRIDES, "package", source.replace(anchor, replacement))
    harness.tool("package")  # the mutant itself must load, or any failure below proves nothing
    fixtures = {"tmp_path": tmp_path, "monkeypatch": monkeypatch, "capsys": capsys}
    arguments = {name: fixtures[name] if name in fixtures else parameters[name]
                 for name in inspect.signature(claim).parameters}
    # Any way the claim fails is the suite going red.
    with pytest.raises((Exception, pytest.fail.Exception)):
        claim(**arguments)


# ─── Worker guards (learned vocabulary) ──────────────────────────────────────

def worker_row(guard, module, anchor, replacement, claim, **parameters):
    return pytest.param(module, anchor, replacement, claim, parameters, id=guard)


WORKER_ROWS = [
    worker_row("learned words merged", "asr_server.py",
               '    merged_bias = _merge_vocabulary(learned_words, req.get("bias_phrases"))',
               '    merged_bias = _merge_vocabulary([], req.get("bias_phrases"))',
               worker_claims.test_learned_words_reach_the_provider, host="windows"),
    worker_row("alias idempotence", "spoken_aliases.py", "        if already_written(match, written):",
               "        if False:", worker_claims.test_a_self_containing_alias_is_rewritten_once, host="windows"),
    worker_row("user over learned", "spoken_aliases.py",
               "    layers = ((defaults(), True), (learned, True), (declared, False))",
               "    layers = ((defaults(), True), (declared, False), (learned, True))",
               worker_claims.test_the_users_alias_file_outranks_learned_aliases),
    worker_row("tombstone covers learned", "spoken_aliases.py", "(learned, True), (declared, False)",
               "(learned, False), (declared, False)",
               worker_claims.test_the_users_alias_file_outranks_learned_aliases),
    worker_row("other schema is empty", "learned_vocabulary.py",
               '    if not isinstance(document, dict) or document.get("schema") != SCHEMA:',
               "    if not isinstance(document, dict):",
               worker_claims.test_a_malformed_learning_file_changes_nothing, host="windows",
               content="other schema"),
    worker_row("unparsable is empty", "learned_vocabulary.py", "    except (UnicodeDecodeError, ValueError):\n",
               "    except ():\n", worker_claims.test_a_malformed_learning_file_changes_nothing,
               host="windows", content="unparsable"),
    # The order the echo guard expects is the order the prompt is written in.
    worker_row("echo read in prompt order", "whisper_mac.py",
               "    order = {t.lower(): i for i, t in enumerate(_prompt_terms(phrases))}",
               "    order = {t.lower(): i for i, t in enumerate(phrases or []) if t and t.strip()}",
               worker_claims.test_an_echoed_prompt_is_removed, host="macos"),
    worker_row("stored order is speech", "whisper_mac.py",
               "    order = {t.lower(): i for i, t in enumerate(_prompt_terms(phrases))}",
               "    order = {t.lower(): i for i, t in enumerate(phrases or []) if t and t.strip()}",
               worker_claims.test_the_learned_words_spoken_in_stored_order_are_kept, host="windows"),
    # A word learned from one correction restores case only, at both casing passes.
    worker_row("engine told case-only", "asr_server.py", '        kwargs["case_only_phrases"] = case_only',
               "        pass", worker_claims.test_a_word_learned_from_one_correction_only_restores_case,
               host="macos"),
    worker_row("engine pass case-only", "whisper_mac.py", "case_only=case_only_phrases or ())", ")",
               worker_claims.test_a_word_learned_from_one_correction_only_restores_case, host="windows"),
    worker_row("server pass case-only", "asr_server.py",
               "        _apply_text_passes(result, merged_bias, case_only, engine_aliased=engine_aliased)",
               "        _apply_text_passes(result, merged_bias, engine_aliased=engine_aliased)",
               worker_claims.test_a_word_learned_from_one_correction_only_restores_case, host="windows"),
    worker_row("taught keeps the join", "asr_server.py",
               "if origin != learned_vocabulary.TAUGHT and _phrase_key(word) not in declared)",
               "if _phrase_key(word) not in declared)",
               worker_claims.test_a_taught_word_joins_its_spaced_form, host="macos"),
    # The alias table runs once per hold: the engine says it applied it, the server skips it.
    worker_row("engine reports its alias pass", "whisper_mac.py",
               "                alias_stat = spoken_aliases.APPLIED_STAT", "                pass",
               worker_claims.test_the_alias_table_runs_once_per_hold, host="windows"),
    worker_row("server skips a done alias pass", "asr_server.py",
               "        _aliases = {} if engine_aliased else spoken_aliases.load()",
               "        _aliases = spoken_aliases.load()",
               worker_claims.test_the_alias_table_runs_once_per_hold, host="macos"),
    worker_row("alias flag read", "asr_server.py", "    engine_aliased = _engine_aliased(stats)",
               "    engine_aliased = False", worker_claims.test_the_alias_table_runs_once_per_hold,
               host="windows"),
    # The junk filter's stock-phrase stems follow the caller's hallucination_guard.
    worker_row("guard reaches the junk filter", "whisper_mac.py",
               "        if text and looks_like_junk(text, long_form=long_form,\n"
               "                                    hallucination_guard=hallucination_guard):",
               "        if text and looks_like_junk(text, long_form=long_form):",
               worker_claims.test_a_dictated_sign_off_is_typed, host="windows", said="See you next week, Evan."),
    worker_row("stems need the guard", "whisper_mac.py", "    if (hallucination_guard and len(words) <= 12",
               "    if (len(words) <= 12", worker_claims.test_a_dictated_sign_off_is_typed, host="macos",
               said="Stay tuned for the launch update."),
    worker_row("stems still drop for notes", "whisper_mac.py", "    if (hallucination_guard and len(words) <= 12",
               "    if (False and len(words) <= 12",
               worker_claims.test_a_caller_that_asks_for_the_guard_still_loses_a_stock_sign_off),
    # The live preview's meeting-only imports degrade on the staged package,
    # and the staged preview reaches the commit it makes at a pause.
    worker_row("preview language vote optional", "ops_stream_whisper.py",
               "        except ImportError:\n            # Meeting diarization is not shipped on the dictation path.",
               "        except ():\n            # Meeting diarization is not shipped on the dictation path.",
               claims.test_the_staged_worker_serves_the_dictation_preview_stream),
    worker_row("presentation mode optional", "ops_stream_whisper.py",
               "    except ImportError:\n        # A notes-only presentation mode",
               "    except ():\n        # A notes-only presentation mode",
               claims.test_the_staged_worker_serves_the_dictation_preview_stream),
    worker_row("text join uses shared seam rule", "ops_stream_whisper.py",
               "    return whisper_mac._join_span_texts(left, right)\n",
               "    return left + ' ' + right\n",
               claims.test_the_staged_stream_module_joins_text_without_optional_helpers),
    worker_row("preview reaches the commit", "ops_stream_whisper.py", "        vote = self.language_vote\n",
               "        from . import stream_helpers  # an unshipped import on the commit path\n"
               "        vote = self.language_vote\n",
               claims.test_the_staged_worker_serves_the_dictation_preview_stream),
]


@pytest.mark.parametrize("module, anchor, replacement, claim, parameters", WORKER_ROWS)
def test_deleting_a_worker_guard_fails_its_test(module, anchor, replacement, claim, parameters, tmp_path,
                                                monkeypatch):
    source = (harness.ROOT / "runtime/transcription_worker" / module).read_text(encoding="utf-8")
    assert source.count(anchor) == 1, "the guard moved: update this row"
    mutant = source.replace(anchor, replacement)
    compile(mutant, module, "exec")  # the mutant must load, or any failure below proves nothing
    monkeypatch.setitem(harness.WORKER_OVERRIDES, module, mutant)
    arguments = {name: tmp_path if name == "tmp_path" else parameters[name]
                 for name in inspect.signature(claim).parameters}
    # Any way the claim fails is the suite going red.
    with pytest.raises((Exception, pytest.fail.Exception)):
        claim(**arguments)
