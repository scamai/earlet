"""tools/run_tests.py: verdicts, result files, the summary, the ledger, and the
CI workflow that calls it.

No test here runs the real suites. Stand-in suites copy synthetic result files
from tests/fixtures/run_tests/ (in the shapes pytest, dotnet test and SwiftPM
write) into the run directory, the way a real suite does, and every run goes
through run_tests.main. The last test deletes each guard from a copy of
run_tests.py and expects the test that claims the guard to fail.
"""
from __future__ import annotations

import inspect
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import time

import pytest

import harness

FIXTURES = harness.ROOT / "tests/fixtures/run_tests"
WORKFLOW = harness.ROOT.parents[1] / ".github/workflows/dictation-ci.yml"
HERE = {"Linux": "linux", "Darwin": "macos", "Windows": "windows"}[platform.system()]
ELSEWHERE = next(name for name in ("linux", "macos", "windows") if name != HERE)
CAPS = {"OPENBLAS_NUM_THREADS": "1", "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1", "DOTNET_PROCESSOR_COUNT": "4"}

# A stand-in suite, run from the root as every suite is. Arguments after the
# run directory are flag/value pairs, applied in order.
FAKE_SUITE = '''import os, shutil, signal, subprocess, sys, time
run, args, code = sys.argv[1], sys.argv[2:], 0
while args:
    flag, value, args = args[0], args[1], args[2:]
    if flag == "--copy":
        source, target = value.split(":")
        shutil.copy(source, os.path.join(run, target))
    elif flag == "--empty":
        open(os.path.join(run, value), "w").close()
    elif flag == "--say":
        print(value, flush=True)
    elif flag == "--env":
        with open(value, "w") as stream:
            stream.write("\\n".join(name + "=" + os.environ.get(name, "") for name in
                                   ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS",
                                    "DOTNET_PROCESSOR_COUNT")))
    elif flag == "--touch":
        open(value, "w").close()
    elif flag == "--ignore-term":
        signal.signal(signal.SIGTERM, signal.SIG_IGN)  # inherited by a --hang child
        open(value, "w").close()
    elif flag == "--hang":
        subprocess.Popen([sys.executable, "-c", "import time; time.sleep(" + value + ")"])
        time.sleep(float(value))
    elif flag == "--exit":
        code = int(value)
sys.exit(code)
'''

ROW = re.compile(r"\| (\d{4}-\d\d-\d\d \d\d:\d\d) \| ((?:[0-9a-f]{7}|unknown)"
                 r"(?: \+dirty \(\d+ files?\)| \(unverified copy\))?)"
                 r" \| (linux|macos|windows)/([\w-]+) \| ([A-Za-z0-9][A-Za-z0-9_-]*) \| ([^|]+) \| (none|\d+/\d+/\d+)"
                 r" \| (OK|FAILED|INCOMPLETE) \| ([^|]+) \|")


# ─── Helpers ─────────────────────────────────────────────────────────────────

def make_root(tmp_path: Path) -> Path:
    """A stand-in apps/dictation: the fixtures, the stand-in suite, and a copy of
    the committed ledger (so the runner writes into the real header)."""
    root = tmp_path / "dictation"
    shutil.copytree(FIXTURES, root / "fixtures")
    (root / "fake_suite.py").write_text(FAKE_SUITE, encoding="utf-8")
    shutil.copy(harness.ROOT / "TEST-LOG.md", root / "TEST-LOG.md")
    return root


def fake(rt, suite_id: str, *args: str, platforms=(HERE,), **fields):
    return rt.Suite(suite_id, (("{python}", "fake_suite.py", "{run}", *args),), platforms, **fields)


def junit(rt, suite_id: str, fixture: str, *args: str, **fields):
    return fake(rt, suite_id, "--copy", f"fixtures/{fixture}:{suite_id}.junit.xml", *args, results="junit",
                required=(f"{suite_id}.junit.xml",), **fields)


def run(rt, root: Path, *argv: str, registry, commit: str | None = "abcdef1") -> int:
    prefix = ["--host", "testhost"] + (["--commit", commit] if commit else [])
    return rt.main([*prefix, *argv], registry=registry, root=root)


def runs(root: Path) -> list[Path]:
    folder = root / "evidence/test-runs"
    return sorted(folder.iterdir(), key=lambda path: path.stat().st_mtime_ns) if folder.is_dir() else []


def last_summary(root: Path) -> dict:
    return json.loads((runs(root)[-1] / "summary.json").read_text(encoding="utf-8"))


def by_id(summary: dict) -> dict:
    return {record["id"]: record for record in summary["suites"]}


def ledger_rows(text: str) -> list[str]:
    lines = text.splitlines()
    header = next(index for index, line in enumerate(lines) if line.startswith("| Date (UTC) |"))
    return [line for line in lines[header + 2:] if line.startswith("| ")]


def builder_path():
    return next(pattern for name, pattern, _ in harness.tool("verify").LEAKS if name == "builder path")


# ─── Verdicts ────────────────────────────────────────────────────────────────

def test_a_clean_run_is_ok_and_its_counts_come_from_the_result_file(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = junit(rt, "unit", "junit-pass.xml", "--say", "999 passed, 0 failed")
    assert run(rt, root, registry=(suite,)) == 0
    summary = last_summary(root)
    record = by_id(summary)["unit"]
    # The console said 999; the result file holds 3 passed and 1 skipped.
    assert (record["outcome"], record["passed"], record["failed"], record["skipped"]) == ("PASS", 3, 0, 1)
    assert summary["learning"] == {"passed": 2, "failed": 0, "skipped": 0}
    assert summary["verdict"] == "OK"
    assert "999 passed" in (runs(root)[-1] / "unit.log").read_text(encoding="utf-8")


VERDICTS = {
    "failure in the result file": (("--copy", "fixtures/junit-failure.xml:unit.junit.xml"), 1, "FAIL",
                                   "2 failed in the result file", (1, 2, 0)),
    "missing result file": ((), 1, "FAIL", "no result file unit.junit.xml", None),
    "empty result file": (("--empty", "unit.junit.xml"), 1, "FAIL", "unit.junit.xml is empty", None),
    "unreadable result file": (("--copy", "fake_suite.py:unit.junit.xml"), 1, "FAIL", "not readable XML", None),
    "non-zero exit with a clean file": (("--copy", "fixtures/junit-pass.xml:unit.junit.xml", "--exit", "3"), 1, "FAIL",
                                        "exit 3 with no failure in the result file", (3, 0, 1)),
    "zero tests": (("--copy", "fixtures/junit-zero.xml:unit.junit.xml"), 2, "SKIP", "ran no tests (0 skipped)",
                   (0, 0, 0)),
    "every test skipped": (("--copy", "fixtures/junit-all-skipped.xml:unit.junit.xml"), 2, "SKIP",
                           "ran no tests (2 skipped)", (0, 0, 2)),
}


@pytest.mark.parametrize("case", VERDICTS)
def test_the_verdict_follows_the_result_file(case, tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    args, code, outcome, reason, counts = VERDICTS[case]
    suite = fake(rt, "unit", *args, results="junit", required=("unit.junit.xml",))
    assert run(rt, root, registry=(suite,)) == code
    summary = last_summary(root)
    record = by_id(summary)["unit"]
    assert record["outcome"] == outcome
    assert reason in (record["failure"] or "; ".join(record["skip_reasons"]))
    if counts is not None:
        assert (record["passed"], record["failed"], record["skipped"]) == counts
    assert summary["verdict"] == {0: "OK", 1: "FAILED", 2: "INCOMPLETE"}[code]
    if case == "failure in the result file":
        assert summary["learning"] == {"passed": 0, "failed": 1, "skipped": 0}


def test_every_suite_skipped_is_incomplete(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    elsewhere = fake(rt, "elsewhere", "--touch", "ran", platforms=(ELSEWHERE,))
    waiting = fake(rt, "waiting", "--touch", "ran", precondition=lambda _: "its script is not in this tree")
    assert run(rt, root, "--suite", "elsewhere", "--suite", "waiting", registry=(elsewhere, waiting)) == 2
    summary = last_summary(root)
    assert summary["verdict"] == "INCOMPLETE"
    assert {record["id"]: record["skip_reasons"] for record in summary["suites"]} == {
        "elsewhere": [f"runs on {ELSEWHERE} only"], "waiting": ["its script is not in this tree"]}
    assert not (root / "ran").exists()


def test_a_suite_that_never_gates_cannot_fail_the_run(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    informational = fake(rt, "info", "--exit", "1", gates=False)
    assert run(rt, root, registry=(junit(rt, "unit", "junit-pass.xml"), informational)) == 0
    summary = last_summary(root)
    assert (by_id(summary)["info"]["outcome"], by_id(summary)["info"]["gates"]) == ("FAIL", False)
    assert summary["verdict"] == "OK"


def test_a_failing_step_fails_its_suite_and_ends_it(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = rt.Suite("build", (("{python}", "fake_suite.py", "{run}", "--exit", "1"),
                               ("{python}", "fake_suite.py", "{run}", "--touch", "second-step-ran")), (HERE,))
    assert run(rt, root, registry=(suite,)) == 1
    record = by_id(last_summary(root))["build"]
    assert (record["outcome"], record["exit"], record["failure"]) == ("FAIL", 1, "exit 1")
    assert record["passed"] is None  # a suite without a result file has no counts to report
    assert not (root / "second-step-ran").exists()


def test_an_opt_in_suite_runs_only_when_asked(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    registry = (junit(rt, "unit", "junit-pass.xml"), junit(rt, "full", "junit-pass.xml", opt_in="--full-worker"))
    assert run(rt, root, registry=registry) == 0
    assert by_id(last_summary(root))["full"]["skip_reasons"] == ["opt-in: --full-worker"]
    assert run(rt, root, "--full-worker", registry=registry) == 0
    assert by_id(last_summary(root))["full"]["outcome"] == "PASS"
    assert run(rt, root, "--suite", "full", registry=registry) == 0
    summary = by_id(last_summary(root))
    assert (summary["full"]["outcome"], summary["unit"]["skip_reasons"]) == ("PASS", ["not selected with --suite"])


def test_a_hung_suite_is_killed_with_everything_it_started(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = fake(rt, "hang", "--hang", "5", timeout=1)  # the stand-in and its child each sleep 5 s
    started = time.monotonic()
    assert run(rt, root, registry=(suite,)) == 1
    assert time.monotonic() - started < 4
    assert by_id(last_summary(root))["hang"]["failure"] == "timed out after 1 s"


@pytest.mark.skipif(os.name == "nt" or shutil.which("bash") is None, reason="a bash EXIT trap and POSIX signals")
def test_a_stopped_step_gets_sigterm_first_so_its_exit_trap_cleans_up(tmp_path):
    """The Linux learning script removes its scratch package from an EXIT trap,
    which bash runs on SIGTERM and cannot run after SIGKILL."""
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    (root / "trap.sh").write_text("mkdir scratch\ntrap 'rm -rf scratch' EXIT\n: > ready\nsleep 30\n", encoding="utf-8")
    suite = rt.Suite("trap", (("{bash}", "trap.sh", "{run}"),), (HERE,), needs=("bash",), timeout=2)
    started = time.monotonic()
    assert run(rt, root, registry=(suite,)) == 1
    assert time.monotonic() - started < 8
    assert by_id(last_summary(root))["trap"]["failure"] == "timed out after 2 s"
    assert (root / "ready").exists(), "the trap was never set, so this proves nothing"
    assert not (root / "scratch").exists()


@pytest.mark.skipif(os.name == "nt", reason="POSIX signals")
def test_a_step_that_ignores_sigterm_is_killed_when_the_grace_runs_out(tmp_path, monkeypatch):
    rt = harness.tool("run_tests")
    monkeypatch.setattr(rt, "STOP_GRACE", 1)
    root = make_root(tmp_path)
    # The stand-in and its child both ignore SIGTERM and would sleep 8 s.
    suite = fake(rt, "deaf", "--ignore-term", "ready", "--hang", "8", timeout=2)
    started = time.monotonic()
    assert run(rt, root, registry=(suite,)) == 1
    assert time.monotonic() - started < 6  # 2 s timeout + 1 s grace, not the 8 s sleep
    assert (root / "ready").exists(), "SIGTERM came before the stand-in ignored it, so this proves nothing"
    assert by_id(last_summary(root))["deaf"]["failure"] == "timed out after 2 s"


@pytest.mark.skipif(os.name == "nt", reason="POSIX signals")
def test_a_second_ctrl_c_during_the_grace_still_kills_the_step(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    process = subprocess.Popen([sys.executable, "fake_suite.py", str(tmp_path), "--ignore-term", "ready",
                                "--hang", "30"], cwd=root, start_new_session=True)
    real_wait = process.wait

    def interrupted_wait(timeout=None):
        if timeout is not None:
            raise KeyboardInterrupt  # the second Ctrl-C, while stop() waits out the grace
        return real_wait()

    try:
        deadline = time.monotonic() + 10
        while not (root / "ready").exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        assert (root / "ready").exists(), "the stand-in never started ignoring SIGTERM"
        process.wait = interrupted_wait
        with pytest.raises(KeyboardInterrupt):
            rt.stop(process)
        assert process.returncode == -signal.SIGKILL
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            real_wait()


def test_a_missing_required_tool_fails_and_a_missing_optional_one_skips(tmp_path, monkeypatch):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    (tmp_path / "empty").mkdir()
    monkeypatch.setenv("PATH", str(tmp_path / "empty"))
    monkeypatch.delenv("DICTATION_DOTNET", raising=False)
    required = fake(rt, "needs-dotnet", "--touch", "ran", needs=("dotnet",))
    optional = fake(rt, "maybe-dotnet", "--touch", "ran", needs=("dotnet",), platforms=(ELSEWHERE,),
                    optional_on=(HERE,))
    assert run(rt, root, registry=(required, optional)) == 1
    records = by_id(last_summary(root))
    assert (records["needs-dotnet"]["outcome"], records["maybe-dotnet"]["outcome"]) == ("FAIL", "SKIP")
    assert records["needs-dotnet"]["failure"] == rt.MISSING["dotnet"]
    assert records["maybe-dotnet"]["skip_reasons"] == [rt.MISSING["dotnet"]]
    assert not (root / "ran").exists()
    monkeypatch.setenv("DICTATION_DOTNET", str(tmp_path / "no-such-dotnet"))
    assert run(rt, root, registry=(required,)) == 1
    assert by_id(last_summary(root))["needs-dotnet"]["failure"] == "DICTATION_DOTNET does not name a file"


@pytest.mark.skipif(os.name == "nt", reason="the stand-in interpreter is a shell script")
def test_every_suite_gets_the_thread_caps_and_this_interpreter(tmp_path, monkeypatch):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    for name in CAPS:
        monkeypatch.delenv(name, raising=False)  # the gate itself runs with them set
    marker = tmp_path / "wrapper-used"
    wrapper = tmp_path / "python-wrapper"
    wrapper.write_text(f'#!/bin/sh\n: > "{marker}"\nexec "{sys.executable}" "$@"\n', encoding="utf-8")
    wrapper.chmod(0o755)
    monkeypatch.setattr(sys, "executable", str(wrapper))
    assert run(rt, root, registry=(junit(rt, "env", "junit-pass.xml", "--env", "env.txt"),)) == 0
    assert marker.exists()
    lines = (root / "env.txt").read_text(encoding="utf-8").splitlines()
    assert dict(line.split("=", 1) for line in lines) == CAPS
    assert last_summary(root)["env_caps"] == CAPS


# ─── Result files ────────────────────────────────────────────────────────────

def test_trx_outcomes_names_and_learning_come_from_the_file(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = fake(rt, "dotnet", "--copy", "fixtures/trx-mixed.trx:dotnet.trx", results="trx", required=("dotnet.trx",))
    assert run(rt, root, registry=(suite,)) == 1
    summary = last_summary(root)
    record = by_id(summary)["dotnet"]
    # Passed x3 (the data-driven parent once), Failed + Timeout, NotExecuted.
    assert (record["passed"], record["failed"], record["skipped"]) == (3, 2, 1)
    # Learning: one by its test name, one only through its TestMethod className; one failed.
    assert summary["learning"] == {"passed": 2, "failed": 1, "skipped": 0}


def test_swift_testing_results_are_counted_beside_xctest(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = fake(rt, "swift", "--copy", "fixtures/xunit-xctest.xml:swift.xml",
                 "--copy", "fixtures/xunit-swift-testing.xml:swift-swift-testing.xml",
                 results="xunit", required=("swift.xml", "swift-swift-testing.xml"))
    assert run(rt, root, registry=(suite,)) == 0
    summary = last_summary(root)
    record = by_id(summary)["swift"]
    assert (record["outcome"], record["passed"], record["failed"], record["skipped"]) == ("PASS", 5, 0, 1)
    assert summary["learning"] == {"passed": 2, "failed": 0, "skipped": 0}


# What XCTest prints for a skipped test when it runs serially: the macOS form,
# then swift-corelibs-xctest's on Linux. The parallel run prints only progress.
MAC_SKIP = ("Test Case '-[DictationLearningTests.LearningLexiconTests testTheWindowsCopyOfTheWordList]' skipped "
            "(0.001 seconds).")
LINUX_SKIP = "Test Case 'LearningLexiconTests.testTheSystemWordList' skipped (0.0 seconds)"
PROGRESS = "[1/4] Testing DictationLearningTests.LearningLexiconTests/testTheWindowsCopyOfTheWordList"
XCTEST_SKIPS = {
    # fixture, with the Swift Testing file, serial lines, exit, outcome, (passed, failed, skipped), learning
    "macOS and Linux skip lines": ("xunit-xctest-skipped.xml", True, (MAC_SKIP, LINUX_SKIP), 0, "PASS", (4, 0, 3),
                                   {"passed": 2, "failed": 0, "skipped": 2}),
    "the parallel file alone": ("xunit-xctest-skipped.xml", True, (), 0, "PASS", (6, 0, 1),
                                {"passed": 4, "failed": 0, "skipped": 0}),
    "every XCTest skipped": ("xunit-xctest-skipped.xml", False,
                             (MAC_SKIP, LINUX_SKIP, "Test Case 'LearningLexiconTests.testThreePluralRules' skipped",
                              "Test Case '-[DictationIPCTests.FramingTests testLengthPrefixIsBigEndian]' skipped"),
                             2, "SKIP", (0, 0, 4), {"passed": 0, "failed": 0, "skipped": 3}),
    "a failure stays a failure": ("xunit-xctest-failure.xml", False,
                                  ("Test Case '-[DictationLearningTests.LearningStoreTests testTheFileIsPrivate]' "
                                   "skipped",),
                                  1, "FAIL", (1, 1, 0), {"passed": 0, "failed": 1, "skipped": 0}),
}


@pytest.mark.parametrize("case", XCTEST_SKIPS)
def test_an_xctest_skip_is_a_skip_although_the_parallel_file_writes_a_pass(case, tmp_path):
    """The swift suite's shape: a parallel run writes the xunit files, then a
    serial rerun of the same build prints XCTest's own skip lines."""
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    fixture, with_swift_testing, lines, code, outcome, counts, learning = XCTEST_SKIPS[case]
    parallel = ("--copy", f"fixtures/{fixture}:swift.xml", "--say", PROGRESS)
    if with_swift_testing:
        parallel += ("--copy", "fixtures/xunit-swift-testing.xml:swift-swift-testing.xml")
    serial = tuple(argument for line in lines for argument in ("--say", line))
    suite = rt.Suite("swift", (("{python}", "fake_suite.py", "{run}", *parallel),
                               ("{python}", "fake_suite.py", "{run}", *serial)), (HERE,), results="xunit",
                     required=("swift.xml",) + (("swift-swift-testing.xml",) if with_swift_testing else ()))
    assert run(rt, root, registry=(suite,)) == code
    summary = last_summary(root)
    record = by_id(summary)["swift"]
    assert (record["outcome"], (record["passed"], record["failed"], record["skipped"])) == (outcome, counts)
    assert summary["learning"] == learning


def test_a_missing_required_result_file_fails_even_beside_another(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = fake(rt, "swift", "--copy", "fixtures/xunit-xctest.xml:swift.xml",
                 results="xunit", required=("swift.xml", "swift-swift-testing.xml"))
    assert run(rt, root, registry=(suite,)) == 1
    assert by_id(last_summary(root))["swift"]["failure"] == "exit 0 and no result file swift-swift-testing.xml"


def test_optional_result_files_need_at_least_one_present(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    files = {"results": "xunit", "optional": ("learn.xml", "learn-swift-testing.xml")}
    assert run(rt, root, registry=(fake(rt, "learn", **files),)) == 1
    assert by_id(last_summary(root))["learn"]["failure"] == (
        "exit 0 and no result file learn.xml or learn-swift-testing.xml")
    only_sibling = fake(rt, "learn", "--copy", "fixtures/xunit-swift-testing.xml:learn-swift-testing.xml", **files)
    assert run(rt, root, registry=(only_sibling,)) == 0
    record = by_id(last_summary(root))["learn"]
    assert (record["passed"], record["failed"], record["skipped"]) == (2, 0, 1)


# ─── Summary ─────────────────────────────────────────────────────────────────

def test_the_summary_json_has_the_schema(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    assert run(rt, root, registry=(junit(rt, "unit", "junit-pass.xml"), fake(rt, "later", "--exit", "0"))) == 0
    run_dir = runs(root)[-1]
    assert re.fullmatch(rf"\d{{8}}T\d{{6}}Z-{HERE}-testhost-abcdef1", run_dir.name)
    summary = last_summary(root)
    assert list(summary) == ["schema", "started_utc", "duration_s", "commit", "dirty", "platform", "arch", "os",
                             "host", "toolchains", "env_caps", "suites", "learning", "verdict"]
    assert summary["schema"] == "dictation.test-run/1"
    assert re.fullmatch(r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", summary["started_utc"])
    assert (summary["commit"], summary["dirty"], summary["platform"], summary["host"]) == (
        "abcdef1", None, HERE, "testhost")
    assert summary["arch"] in {"x64", "arm64", "x86"} and summary["os"]
    assert list(summary["toolchains"]) == ["python", "dotnet", "swift"]
    assert summary["toolchains"]["python"] == platform.python_version()
    for record in summary["suites"]:
        assert {"id", "command", "outcome", "exit", "passed", "failed", "skipped", "skip_reasons", "duration_s",
                "log"} <= set(record)
        assert record["outcome"] in {"PASS", "FAIL", "SKIP"}
        assert (run_dir / record["log"]).is_file()
    assert by_id(summary)["unit"]["command"] == "python fake_suite.py <run> --copy " \
                                                "fixtures/junit-pass.xml:unit.junit.xml"
    assert (run_dir / "summary.md").read_text(encoding="utf-8").startswith("# Dictation tests: OK\n")


def test_no_local_path_reaches_the_summary_or_the_ledger(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    # {run}, {tmp} and {python} all stand for absolute paths on this machine.
    suite = junit(rt, "unit", "junit-pass.xml", "--say", "{tmp}/scratch")
    assert run(rt, root, "--record", "--allow-dirty", registry=(suite,)) == 0
    run_dir = runs(root)[-1]
    texts = [(run_dir / "summary.json").read_text(encoding="utf-8"),
             (run_dir / "summary.md").read_text(encoding="utf-8"),
             ledger_rows((root / "TEST-LOG.md").read_text(encoding="utf-8"))[0]]
    assert "<tmp>/scratch" in texts[0]
    for text in texts:
        for value in (str(tmp_path), sys.executable, str(harness.ROOT), str(Path.home())):
            assert value not in text
        assert not builder_path().search(text)


def test_a_summary_that_would_name_a_local_path_is_refused(tmp_path, capsys):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    before = (root / "TEST-LOG.md").read_text(encoding="utf-8")
    leaky = fake(rt, "leaky", precondition=lambda where: f"missing {where}/thing")
    assert run(rt, root, "--record", "--allow-dirty", registry=(junit(rt, "unit", "junit-pass.xml"), leaky)) == 1
    assert "names a local path" in capsys.readouterr().err
    assert not (runs(root)[-1] / "summary.json").exists()
    assert (root / "TEST-LOG.md").read_text(encoding="utf-8") == before


def test_ci_appends_the_summary_to_the_step_summary(tmp_path, monkeypatch):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    step = tmp_path / "step-summary.md"
    step.write_text("earlier step\n", encoding="utf-8")
    monkeypatch.setenv("GITHUB_STEP_SUMMARY", str(step))
    registry = (junit(rt, "unit", "junit-pass.xml"),)
    assert run(rt, root, registry=registry) == 0
    assert step.read_text(encoding="utf-8") == "earlier step\n"
    assert run(rt, root, "--ci", registry=registry) == 0
    assert step.read_text(encoding="utf-8") == "earlier step\n" + (runs(root)[-1] / "summary.md").read_text(
        encoding="utf-8")


def test_json_prints_only_the_summary_on_stdout(tmp_path, capsys):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = junit(rt, "unit", "junit-pass.xml", "--say", "hello from the suite")
    assert run(rt, root, "--json", registry=(suite,)) == 0
    out, err = capsys.readouterr()
    assert json.loads(out)["verdict"] == "OK"
    assert "hello from the suite" in err


# ─── Refusals ────────────────────────────────────────────────────────────────

def test_a_run_for_another_platform_is_refused_before_anything_runs(tmp_path, capsys):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    suite = fake(rt, "unit", "--touch", "ran", platforms=(ELSEWHERE,))
    assert run(rt, root, "--platform", ELSEWHERE, registry=(suite,)) == 2
    assert f"this is a {HERE} machine" in capsys.readouterr().err
    assert not (root / "ran").exists() and not runs(root)


@pytest.mark.parametrize("host", ["../build-box", "/home/someone", "C:\\Users\\someone", "two words", "", "x" * 33])
def test_a_host_must_be_a_short_alias(host, tmp_path, capsys):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    assert run(rt, root, "--host", host, registry=(junit(rt, "unit", "junit-pass.xml"),)) == 2
    assert "--host must be a short machine alias" in capsys.readouterr().err
    assert not runs(root)


@pytest.mark.parametrize("host", ["unnamed", "Unnamed", "UNNAMED"])
def test_the_label_of_a_run_without_a_host_is_not_a_host(host, tmp_path, capsys):
    # Otherwise --record --host unnamed writes a row that looks like an anonymous run's.
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    ledger = (root / "TEST-LOG.md").read_text(encoding="utf-8")
    suite = fake(rt, "unit", "--touch", "ran")
    assert rt.main(["--commit", "abcdef1", "--record", "--allow-dirty", "--host", host], registry=(suite,),
                   root=root) == 2
    assert "is the label of a run without --host" in capsys.readouterr().err
    assert not (root / "ran").exists() and not runs(root)
    assert (root / "TEST-LOG.md").read_text(encoding="utf-8") == ledger


def test_a_run_without_a_host_is_unnamed_and_never_reads_the_hostname(tmp_path, monkeypatch):
    # A hostname can name a person or a team machine, and it would reach the
    # evidence folder, the summary and (with --record) a committed row.
    import socket
    monkeypatch.setattr(socket, "gethostname", lambda: "Someones-Laptop.corp.example")
    monkeypatch.setattr(socket, "getfqdn", lambda *_: "Someones-Laptop.corp.example")
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    assert rt.main(["--commit", "abcdef1"], registry=(junit(rt, "unit", "junit-pass.xml"),), root=root) == 0
    run_dir = runs(root)[-1]
    assert re.fullmatch(rf"\d{{8}}T\d{{6}}Z-{HERE}-unnamed-abcdef1", run_dir.name)
    summary = (run_dir / "summary.json").read_text(encoding="utf-8")
    assert json.loads(summary)["host"] == "unnamed"
    assert "Someones-Laptop" not in summary + (run_dir / "summary.md").read_text(encoding="utf-8")


def test_record_refuses_a_run_without_a_host_before_anything_runs(tmp_path, capsys):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    ledger = (root / "TEST-LOG.md").read_text(encoding="utf-8")
    suite = fake(rt, "unit", "--touch", "ran")
    assert rt.main(["--commit", "abcdef1", "--record", "--allow-dirty"], registry=(suite,), root=root) == 2
    assert "--record needs --host" in capsys.readouterr().err
    assert not (root / "ran").exists() and not runs(root)
    assert (root / "TEST-LOG.md").read_text(encoding="utf-8") == ledger


# ─── Ledger ──────────────────────────────────────────────────────────────────

def test_rows_are_inserted_newest_first_in_the_ledger_format(tmp_path):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    # make_root copies the committed ledger, which may already hold real rows:
    # new rows go on top of them and leave them as they were.
    earlier = ledger_rows((root / "TEST-LOG.md").read_text(encoding="utf-8"))
    skipped = fake(rt, "later", precondition=lambda _: "its script is not in this tree")
    assert run(rt, root, "--record", "--allow-dirty", "--host", "first",
               registry=(junit(rt, "unit", "junit-pass.xml"), skipped)) == 0
    assert run(rt, root, "--record", "--allow-dirty", "--host", "second",
               registry=(junit(rt, "unit", "junit-failure.xml"),)) == 1
    everything = ledger_rows((root / "TEST-LOG.md").read_text(encoding="utf-8"))
    assert everything[2:] == earlier
    rows = [ROW.fullmatch(row) for row in everything[:2]]
    assert all(rows) and [row.group(5) for row in rows] == ["second", "first"]
    second, first = rows
    assert first.group(2) == "abcdef1 (unverified copy)"
    assert (first.group(3), first.group(6), first.group(7), first.group(8)) == (HERE, "unit 3/0/1", "2/0/0", "OK")
    assert first.group(9) == "later (its script is not in this tree)"
    assert (second.group(6), second.group(7), second.group(8), second.group(9)) == (
        "unit 1/2/0 FAIL", "0/1/0", "FAILED", "–")


def test_a_slow_earlier_run_records_below_a_newer_run_without_losing_rows(tmp_path):
    rt = harness.tool("run_tests")
    ledger = tmp_path / "TEST-LOG.md"
    ledger.write_text(rt.LEDGER_HEADER + "\n|---|---|---|---|---|---|---|---|\n\nFooter\n")
    def row(at, host):
        return f"| {at} | abcdef1 | macos/arm64 | {host} | unit 3/0/0 | none | OK | – |"
    newer, older = row("2026-10-07 23:32", "newer"), row("2026-10-07 23:31", "older")
    rt.insert_row(ledger, newer)
    rt.insert_row(ledger, older)
    text = ledger.read_text()
    assert ledger_rows(text) == [newer, older]
    assert text.endswith("\nFooter\n")


def test_an_unterminated_final_ledger_row_does_not_merge_with_a_late_completion(tmp_path):
    rt = harness.tool("run_tests")
    ledger = tmp_path / "TEST-LOG.md"
    newer = "| 2026-10-07 23:32 | abcdef1 | macos/arm64 | newer | unit 3/0/0 | none | OK | – |"
    older = newer.replace("23:32", "23:31").replace("newer", "older")
    ledger.write_text(rt.LEDGER_HEADER + "\n|---|---|---|---|---|---|---|---|\n" + newer)
    rt.insert_row(ledger, older)
    assert ledger_rows(ledger.read_text()) == [newer, older]


def test_the_committed_ledger_has_its_header_and_only_well_formed_rows_newest_first():
    rt = harness.tool("run_tests")
    text = (harness.ROOT / "TEST-LOG.md").read_text(encoding="utf-8")
    assert rt.ledger_table(text) is not None
    rows = ledger_rows(text)
    for row in rows:
        assert ROW.fullmatch(row), row
        assert not builder_path().search(row) and not rt.LOCAL_PATH.search(row), row
    dates = [ROW.fullmatch(row).group(1) for row in rows]
    assert dates == sorted(dates, reverse=True)


def test_record_refuses_a_tree_it_cannot_check(tmp_path, capsys):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    before = (root / "TEST-LOG.md").read_text(encoding="utf-8")
    assert run(rt, root, "--record", registry=(junit(rt, "unit", "junit-pass.xml"),)) == 2
    assert "unverified copy" in capsys.readouterr().err
    assert (root / "TEST-LOG.md").read_text(encoding="utf-8") == before and not runs(root)


def commit_all(repository: Path) -> str:
    """git init + commit everything under ``repository``, isolated from the
    user's git configuration. Returns HEAD."""
    environment = dict(os.environ, GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_NOSYSTEM="1")

    def git(*args):
        return subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                               "-c", "commit.gpgsign=false", *args], cwd=repository, env=environment,
                              capture_output=True, text=True, check=True).stdout

    git("init", "-q")
    git("add", "-A")
    git("commit", "-q", "-m", "base")
    return git("rev-parse", "HEAD").strip()


@pytest.mark.skipif(shutil.which("git") is None, reason="needs git")
def test_record_refuses_uncommitted_changes_unless_allowed(tmp_path, capsys):
    rt = harness.tool("run_tests")
    root = make_root(tmp_path)
    (root / ".gitignore").write_text("evidence/\n", encoding="utf-8")
    head = commit_all(tmp_path)
    registry = (junit(rt, "unit", "junit-pass.xml"),)
    assert run(rt, root, "--record", registry=registry, commit=None) == 0
    assert last_summary(root)["dirty"] == []
    assert ROW.fullmatch(ledger_rows((root / "TEST-LOG.md").read_text(encoding="utf-8"))[0]).group(2) == head[:7]

    (root / "new_tool.py").write_text("print()\n", encoding="utf-8")
    before = (root / "TEST-LOG.md").read_text(encoding="utf-8")
    count = len(runs(root))
    assert run(rt, root, "--record", registry=registry, commit=None) == 2
    assert "1 uncommitted paths under apps/dictation (new_tool.py)" in capsys.readouterr().err
    assert (root / "TEST-LOG.md").read_text(encoding="utf-8") == before and len(runs(root)) == count

    # The ledger row just recorded is not dirt; the new file is.
    assert run(rt, root, "--record", "--allow-dirty", registry=registry, commit=None) == 0
    assert last_summary(root)["dirty"] == ["new_tool.py"]
    row = ROW.fullmatch(ledger_rows((root / "TEST-LOG.md").read_text(encoding="utf-8"))[0])
    assert row.group(2) == f"{head[:7]} +dirty (1 file)"


@pytest.mark.skipif(shutil.which("git") is None, reason="needs git")
def test_a_copy_inside_another_repository_is_an_unverified_copy(tmp_path):
    rt = harness.tool("run_tests")
    (tmp_path / "unrelated.txt").write_text("another project\n", encoding="utf-8")
    commit_all(tmp_path)
    root = make_root(tmp_path)  # untracked there: that repository's HEAD is not this tree's commit
    assert run(rt, root, "--record", "--allow-dirty", registry=(junit(rt, "unit", "junit-pass.xml"),)) == 0
    summary = last_summary(root)
    assert (summary["commit"], summary["dirty"]) == ("abcdef1", None)
    row = ROW.fullmatch(ledger_rows((root / "TEST-LOG.md").read_text(encoding="utf-8"))[0])
    assert row.group(2) == "abcdef1 (unverified copy)"


# ─── Registry and listing ────────────────────────────────────────────────────

def test_the_registry_runs_each_suite_with_structured_results():
    rt = harness.tool("run_tests")
    suites = {suite.id: suite for suite in rt.SUITES}
    assert list(suites) == ["boundary", "python-gate", "worker-full", "dotnet-services", "dotnet-infra", "dotnet-core",
                            "win-app-build", "win-package", "swift", "swift-learning-linux", "mac-package", "snapshot"]
    assert rt.THREAD_CAPS == CAPS
    # VERIFY.md's Python gate; tests/ carries test_learning_golden.py.
    assert rt.PYTHON_GATE == ("tests", "runtime/transcription_worker/test_asr_server.py",
                              "runtime/transcription_worker/test_engine_pin.py",
                              "runtime/transcription_worker/test_junk_filter_mac.py",
                              "runtime/transcription_worker/test_learned_vocabulary.py",
                              "runtime/transcription_worker/test_context_vocabulary.py",
                              "runtime/transcription_worker/test_mlx_beam.py",
                              "runtime/transcription_worker/test_mlx_beam_native.py",
                              "runtime/transcription_worker/test_ops_stream_whisper.py",
                              "runtime/transcription_worker/test_mac_preview_budget.py",
                              "runtime/transcription_worker/test_partial_gate.py",
                              "runtime/transcription_worker/test_spoken_aliases.py",
                              "runtime/transcription_worker/test_whisper_mac.py",
                              "runtime/transcription_worker/test_user_data_isolation.py")
    assert (harness.ROOT / "tests/test_learning_golden.py").is_file()
    gate = suites["python-gate"].steps[0]
    assert gate[:3] == ("{python}", "-m", "pytest") and gate[3:3 + len(rt.PYTHON_GATE)] == rt.PYTHON_GATE
    assert {"-q", "no:cacheprovider", "--junitxml={run}/python-gate.junit.xml"} <= set(gate)
    assert suites["worker-full"].opt_in == "--full-worker"
    for suite_id in ("dotnet-services", "dotnet-infra", "dotnet-core"):
        suite = suites[suite_id]
        step = suite.steps[0]
        assert step[:2] == ("{dotnet}", "test") and {"-c", "Release", "-m:4"} <= set(step)
        assert step[step.index("--results-directory") + 1] == "{run}"
        assert "trx;LogFileName={id}.trx" in step and suite.required == (f"{suite_id}.trx",)
        assert (suite.platforms, suite.optional_on, suite.results) == (("linux", "windows"), ("macos",), "trx")
    swift = suites["swift"]
    assert swift.steps[0][:4] == ("{swift}", "test", "--package-path", "macos") and "--parallel" in swift.steps[0]
    assert swift.steps[0][-2:] == ("--xunit-output", "{run}/swift.xml")
    for step in swift.steps:
        assert "--build-system" in step
        assert step[step.index("--build-system") + 1] == "native"
    # The serial rerun that prints XCTest's skips, from the build just made.
    assert swift.steps[1:] == (("{swift}", "test", "--package-path", "macos", "--build-system", "native", "--skip-build"),)
    assert swift.required == ("swift.xml", "swift-swift-testing.xml") and swift.platforms == ("macos",)
    learning = suites["swift-learning-linux"]
    assert (learning.platforms, learning.missing_tool_skips) == (("linux",), True)
    assert learning.steps == (("{bash}", "macos/tools/test-learning-on-linux.sh", "{run}"),)
    # Every learning test is an XCTest: losing that file is a failure, not a quiet SKIP.
    assert (learning.required, learning.optional) == (("swift-learning.xml",), ("swift-learning-swift-testing.xml",))
    assert (learning.precondition(harness.ROOT) is None) == (harness.ROOT / rt.LEARNING_SCRIPT).is_file()
    assert suites["mac-package"].steps[1] == ("{codesign}", "--verify", "--deep", "--strict",
                                             f"{{tmp}}/package/{json.loads((harness.ROOT / 'product.json').read_text())['name']}.app")
    tools_version = re.match(r"// swift-tools-version: (\d+)\.(\d+)\n",
                             (harness.ROOT / "macos/Package.swift").read_text(encoding="utf-8"))
    declared = tuple(int(part) for part in tools_version.groups())
    assert {suite.id: suite.min_swift for suite in rt.SUITES if "swift" in suite.needs} == dict.fromkeys(
        ("swift", "swift-learning-linux", "mac-package"), declared)
    assert suites["win-package"].platforms == suites["win-app-build"].platforms == ("linux", "windows")
    assert suites["snapshot"].gates is False and all(suite.gates for suite in rt.SUITES if suite.id != "snapshot")
    for suite in rt.SUITES:
        for step in suite.steps:
            assert step[0].startswith("{") and "python3" not in step


@pytest.mark.parametrize("target, absent", [("linux", {"swift", "mac-package"}),
                                            ("macos", {"win-app-build", "win-package", "swift-learning-linux"}),
                                            ("windows", {"swift", "swift-learning-linux", "mac-package"})])
def test_list_names_every_suite_and_what_happens_to_it_there(target, absent, capsys):
    rt = harness.tool("run_tests")
    assert rt.main(["--list", "--platform", target, "--json"]) == 0
    rows = {row["id"]: row for row in json.loads(capsys.readouterr().out)}
    assert list(rows) == [suite.id for suite in rt.SUITES]
    assert {suite_id for suite_id, row in rows.items() if row["here"] == f"not a {target} suite"} == absent
    assert rows["worker-full"]["here"] == "SKIP: opt-in: --full-worker"
    assert rows["snapshot"]["gates"] is False
    assert all(not builder_path().search(row["command"]) for row in rows.values())


@pytest.mark.skipif(HERE != "linux" or shutil.which("bash") is None or shutil.which("cp") is None,
                    reason="the Linux Swift learning suite runs on Linux")
def test_the_linux_swift_learning_suite_skips_until_it_can_run_then_reads_its_file(tmp_path, monkeypatch):
    rt = harness.tool("run_tests")
    suite = next(suite for suite in rt.SUITES if suite.id == "swift-learning-linux")
    root = make_root(tmp_path)
    home, tools = tmp_path / "home", tmp_path / "bin"
    home.mkdir()
    tools.mkdir()
    for name in ("bash", "cp"):
        (tools / name).symlink_to(shutil.which(name))
    monkeypatch.setenv("HOME", str(home))
    monkeypatch.setenv("PATH", str(tools))
    monkeypatch.delenv("DICTATION_SWIFT", raising=False)

    assert run(rt, root, registry=(suite,)) == 2
    assert by_id(last_summary(root))["swift-learning-linux"]["skip_reasons"] == [
        "macos/tools/test-learning-on-linux.sh is not in this tree"]

    script = root / rt.LEARNING_SCRIPT
    script.parent.mkdir(parents=True)
    script.write_text('set -e\ntest -x "$DICTATION_SWIFT"\ncp fixtures/xunit-xctest.xml "$1/swift-learning.xml"\n'
                      'cp fixtures/xunit-swift-testing.xml "$1/swift-learning-swift-testing.xml"\n', encoding="utf-8")
    assert run(rt, root, registry=(suite,)) == 2
    assert by_id(last_summary(root))["swift-learning-linux"]["skip_reasons"] == [rt.MISSING["swift"]]

    swift = tmp_path / "swift"
    swift.write_text("#!/bin/sh\necho 'Swift version 9.9 (stand-in)'\n", encoding="utf-8")
    swift.chmod(0o755)
    monkeypatch.setenv("DICTATION_SWIFT", str(swift))
    assert run(rt, root, registry=(suite,)) == 0
    summary = last_summary(root)
    record = by_id(summary)["swift-learning-linux"]
    # Both files are read: 3 XCTest passes, then 2 passes and 1 skip from Swift Testing.
    assert (record["outcome"], record["passed"], record["failed"], record["skipped"]) == ("PASS", 5, 0, 1)
    assert summary["toolchains"]["swift"] == "9.9 (stand-in)"

    monkeypatch.delenv("DICTATION_SWIFT")
    unpacked = home / ".swift-toolchain/swift-9.9-RELEASE/usr/bin"
    unpacked.mkdir(parents=True)
    shutil.copy(swift, unpacked / "swift")
    assert run(rt, root, registry=(suite,)) == 0

    # The XCTest file lost (no --parallel, a SwiftPM that moved it): the empty
    # Swift Testing file alone would read as "ran no tests", a quiet SKIP.
    script.write_text('cp fixtures/xunit-swift-testing.xml "$1/swift-learning-swift-testing.xml"\n', encoding="utf-8")
    assert run(rt, root, registry=(suite,)) == 1
    record = by_id(last_summary(root))["swift-learning-linux"]
    assert (record["outcome"], record["failure"]) == ("FAIL", "exit 0 and no result file swift-learning.xml")


@pytest.mark.skipif(os.name == "nt", reason="the stand-in swift is a shell script")
@pytest.mark.parametrize("suite_id", ["swift", "mac-package"])
def test_an_older_swift_skips_the_macos_swift_suites_with_a_reason(suite_id, tmp_path):
    """A hosted image's default Xcode can carry a Swift older than
    macos/Package.swift allows; SwiftPM then exits 1 before writing a result
    file, which would read as a failed test rather than a missing tool."""
    rt = harness.tool("run_tests")
    suite = next(suite for suite in rt.SUITES if suite.id == suite_id)
    tools = {name: (str(tmp_path / name), None) for name in rt.TOOLS}  # every tool present
    swift = tmp_path / "swift"
    for printed, decision in [("Apple Swift version 6.1.2 (swiftlang-6.1.2.1.2 clang-1700.0.13.5)",
                               ("skip", "Swift 6.1 is older than 6.2")),
                              ("Apple Swift version 6.2 (swiftlang-6.2.0.19.9 clang-1700.3.19.1)", ("run", None))]:
        swift.write_text(f"#!/bin/sh\necho '{printed}'\n", encoding="utf-8")
        swift.chmod(0o755)
        assert rt.plan(suite, "macos", None, False, harness.ROOT, tools) == decision


# ─── CI workflow ─────────────────────────────────────────────────────────────
# ci-local.py runs a job only when it has no matrix and no step-level if: or
# continue-on-error, and a job-level if: only with --all. So each job is one
# dependency step and one runner step, identical locally and hosted.

DISPATCH = "github.event_name == 'workflow_dispatch'"
# macos-26: macos-15's default Xcode 16.4 carries Swift 6.1, older than macos/Package.swift allows.
JOBS = {"linux": ("ubuntu-latest", None), "macos": ("macos-26", DISPATCH), "windows": ("windows-latest", DISPATCH)}


# The Linux job also runs the open-source export from the monorepo's tools/ (outside this app, never
# shipped): its tests, then a strict dry-run of the committed export.
EXPORT_STEPS = {"linux": (
    "python -m pip install -r requirements.txt && python -m pytest -q -p no:cacheprovider -rs",
    "python export.py --dry-run --ci")}


def workflow_problems(text: str) -> list[str]:
    problems = []
    head, _, body = text.partition("\njobs:\n")
    if not re.search(r"^  workflow_dispatch:", head, re.M):
        problems.append("no workflow_dispatch trigger")
    jobs = dict(re.findall(r"^  ([a-z][\w-]*):\n((?:(?:    .*)?\n)*)", body + "\n", re.M))
    for job, (runner, condition) in JOBS.items():
        steps = jobs.get(job)
        if steps is None:
            problems.append(f"no {job} job")
            continue
        if not re.search(rf"^    runs-on: {runner}$", steps, re.M):
            problems.append(f"{job} does not run on {runner}")
        found = re.search(r"^    if: (.+)$", steps, re.M)
        if (found.group(1).strip() if found else None) != condition:
            problems.append(f"{job} job-level if: is {found.group(1) if found else None}, expected {condition}")
        if re.search(r"^        (if|continue-on-error):|^    strategy:", steps, re.M):
            problems.append(f"{job} has a step-level condition or a matrix; ci-local would skip it")
        commands = re.findall(r"^        run: (.+)$", steps, re.M)
        expected = ["python -m pip install -r tests/requirements.txt",
                    f"python tools/run_tests.py --platform {job} --ci", *EXPORT_STEPS.get(job, ())]
        if commands != expected:
            problems.append(f"{job} runs {commands}, expected {expected}")
        if steps.count("        working-directory: apps/dictation\n") != 2 or steps.count(
                "        working-directory: tools/oss-export/dictation\n") != len(EXPORT_STEPS.get(job, ())):
            problems.append(f"{job} runs a step outside apps/dictation and the export tool")
    return problems


def test_the_ci_workflow_runs_the_runner_on_each_platform():
    if not WORKFLOW.is_file():
        pytest.skip("a copy of apps/dictation without the repository's workflows")
    assert workflow_problems(WORKFLOW.read_text(encoding="utf-8")) == []


@pytest.mark.parametrize("anchor, replacement", [
    ("python tools/run_tests.py --platform linux --ci", "python tools/verify.py"),
    ("  workflow_dispatch:\n", ""),
    (f"    if: {DISPATCH}\n    runs-on: macos-26", "    runs-on: macos-26"),
    ("    runs-on: macos-26\n", "    runs-on: macos-15\n"),
    ("        run: python tools/run_tests.py --platform windows --ci\n",
     "        run: python tools/run_tests.py --platform windows --ci\n        continue-on-error: true\n"),
    ("    runs-on: ubuntu-latest\n", "    runs-on: ubuntu-latest\n    strategy:\n      matrix: {}\n"),
    ("python -m pytest -q -p no:cacheprovider -rs", "true"),
    ("python -m pytest -q -p no:cacheprovider -rs", "python -m pytest test_export.py -q -p no:cacheprovider -rs"),
    ("python export.py --dry-run --ci", "python export.py --dry-run --report-only --ci"),
], ids=["linux gate replaced", "no dispatch", "macos bills on every PR", "macos on a Swift 6.1 image",
        "step-level escape", "matrix", "export tests dropped", "export tests narrowed to one file",
        "export dry-run ignores findings"])
def test_the_workflow_check_catches_a_drifted_workflow(anchor, replacement):
    if not WORKFLOW.is_file():
        pytest.skip("a copy of apps/dictation without the repository's workflows")
    text = WORKFLOW.read_text(encoding="utf-8")
    assert text.count(anchor) == 1, "the workflow moved: update this case"
    assert workflow_problems(text.replace(anchor, replacement))


def test_the_snapshot_suite_skips_with_its_reason_in_an_exported_copy_without_the_manifest(tmp_path):
    """The public export leaves import-manifest.json out, so the snapshot has nothing to compare with:
    a named skip, not a failure that only looks like the monorepo's by-design one."""
    rt = harness.tool("run_tests")
    suite = next(suite for suite in rt.SUITES if suite.id == "snapshot")
    root = make_root(tmp_path)
    assert run(rt, root, registry=(suite,)) == 2
    assert by_id(last_summary(root))["snapshot"]["skip_reasons"] == [
        "import-manifest.json is not in this tree (an exported copy)"]


@pytest.mark.parametrize("marker", ["VERIFY.md", ".Codex/decisions.md"])
def test_the_snapshot_suite_runs_in_a_monorepo_tree_that_lost_its_manifest(tmp_path, marker):
    """Only an exported copy skips: a tree that still holds a monorepo-only file runs the suite, so a lost
    manifest is reported as a failure, not as an exported copy's skip."""
    rt = harness.tool("run_tests")
    suite = next(suite for suite in rt.SUITES if suite.id == "snapshot")
    root = make_root(tmp_path)
    (root / marker).parent.mkdir(parents=True, exist_ok=True)
    (root / marker).write_text("monorepo only\n", encoding="utf-8")
    run(rt, root, registry=(suite,))
    record = by_id(last_summary(root))["snapshot"]
    assert record["outcome"] != "SKIP" and not record.get("skip_reasons"), record


# ─── Guards can fire ─────────────────────────────────────────────────────────
# Each row deletes one guard from a copy of run_tests.py and reruns the test
# that claims it. A row whose anchor no longer matches fails: update the row,
# do not delete it.

def row(guard, anchor, replacement, claim, **parameters):
    return pytest.param(anchor, replacement, claim, parameters, id=guard)


VERDICT = test_the_verdict_follows_the_result_file
XCTEST_SKIP = test_an_xctest_skip_is_a_skip_although_the_parallel_file_writes_a_pass
MUTATIONS = [
    row("snapshot waits for the manifest", "gates=False,\n          precondition=manifest_present),",
        "gates=False),", test_the_snapshot_suite_skips_with_its_reason_in_an_exported_copy_without_the_manifest),
    row("only an exported copy skips the snapshot", "if verify_tool().exported_copy(root)",
        'if not (root / "import-manifest.json").is_file()',
        test_the_snapshot_suite_runs_in_a_monorepo_tree_that_lost_its_manifest, marker="VERIFY.md"),
    row("result-file failure fails", '    if counts["failed"]:\n', "    if False:\n", VERDICT,
        case="failure in the result file"),
    row("junit error is a failure", 'tags & {"failure", "error"}', 'tags & {"failure"}', VERDICT,
        case="failure in the result file"),
    row("missing result file fails", "    if absent or not present:\n", "    if False:\n", VERDICT,
        case="missing result file"),
    row("empty result file named", "        if path.stat().st_size == 0:\n", "        if False:\n", VERDICT,
        case="empty result file"),
    row("unreadable XML fails",
        '        except ET.ParseError:\n            return None, f"{path.name} is not readable XML"',
        "        except KeyError:\n            raise", VERDICT, case="unreadable result file"),
    row("non-zero exit fails", "    if exit_code != 0:\n        return \"FAIL\", f\"exit {exit_code} with no failure",
        "    if False:\n        return \"FAIL\", f\"exit {exit_code} with no failure", VERDICT,
        case="non-zero exit with a clean file"),
    row("no tests is no pass", '    if counts["passed"] == 0:\n', "    if False:\n", VERDICT, case="zero tests"),
    row("nothing ran is incomplete",
        '    return "OK" if any(record["outcome"] == "PASS" for record in gating) else "INCOMPLETE"',
        '    return "OK"', test_every_suite_skipped_is_incomplete),
    row("non-gating never gates", '    gating = [record for record in records if record["gates"]]',
        "    gating = records", test_a_suite_that_never_gates_cannot_fail_the_run),
    row("exit-only suite fails on exit",
        '        return ("PASS", None) if exit_code == 0 else ("FAIL", f"exit {exit_code}")',
        '        return ("PASS", None)', test_a_failing_step_fails_its_suite_and_ends_it),
    row("failing step ends the suite", "                if failure or exit_code != 0:\n                    break\n",
        "", test_a_failing_step_fails_its_suite_and_ends_it),
    row("opt-in stays out", "    if selected is None and suite.opt_in and not full_worker:\n", "    if False:\n",
        test_an_opt_in_suite_runs_only_when_asked),
    row("hung suite times out", "        code = process.wait(timeout=timeout)\n", "        code = process.wait()\n",
        test_a_hung_suite_is_killed_with_everything_it_started),
    row("missing required tool fails",
        "            skips = suite.missing_tool_skips or target not in suite.platforms\n",
        "            skips = True\n", test_a_missing_required_tool_fails_and_a_missing_optional_one_skips),
    row("missing optional tool skips",
        "            skips = suite.missing_tool_skips or target not in suite.platforms\n",
        "            skips = False\n", test_a_missing_required_tool_fails_and_a_missing_optional_one_skips),
    row("junit skips", '"skipped" if "skipped" in tags else "passed"', '"passed"',
        test_a_clean_run_is_ok_and_its_counts_come_from_the_result_file),
    row("learning markers", 'LEARNING = ("Learning", "learning_golden", ', 'LEARNING = ("Learning", ',
        test_a_clean_run_is_ok_and_its_counts_come_from_the_result_file),
    row("trx not-run states skip", 'TRX_SKIPPED = {"NotExecuted", ', "TRX_SKIPPED = {",
        test_trx_outcomes_names_and_learning_come_from_the_file),
    row("unknown trx outcome fails", 'if outcome in TRX_SKIPPED else "failed"))',
        'if outcome in TRX_SKIPPED else "passed"))', test_trx_outcomes_names_and_learning_come_from_the_file),
    row("trx class name joined", '            name = f"{class_name}.{name}"\n', "            pass\n",
        test_trx_outcomes_names_and_learning_come_from_the_file),
    row("trx direct results only", 'results.findall(f"{ns}UnitTestResult")', 'results.iter(f"{ns}UnitTestResult")',
        test_trx_outcomes_names_and_learning_come_from_the_file),
    row("required file checked", "    absent = [name for name in suite.required if not (run_dir / name).is_file()]",
        "    absent = []", test_a_missing_required_result_file_fails_even_beside_another),
    row("XCTest skips read back", '    if suite.results == "xunit":\n        cases = xctest_skips(',
        "    if False:\n        cases = xctest_skips(", XCTEST_SKIP, case="macOS and Linux skip lines"),
    row("macOS skip line", r"-\[(?:\w+\.)*(\w+) (\w+)\]|", r"-\[(?:\w+\.)*(\w+) (\w+)\]NEVER|", XCTEST_SKIP,
        case="macOS and Linux skip lines"),
    row("Linux skip line", r"(?:\w+\.)*(\w+)\.(\w+))' skipped", r"(?:\w+\.)*(\w+)\.(\w+)NEVER)' skipped",
        XCTEST_SKIP, case="macOS and Linux skip lines"),
    row("a failure stays a failure", 'if status == "passed" and tuple(', "if tuple(", XCTEST_SKIP,
        case="a failure stays a failure"),
    row("swift suite reruns serially",
        '\n                    ("{swift}", "test", "--package-path", "macos", "--build-system", "native", "--skip-build")),', "),",
        test_the_registry_runs_each_suite_with_structured_results),
    row("Swift XML covers every target", '"--build-system", "native", "--jobs"', '"--jobs"',
        test_the_registry_runs_each_suite_with_structured_results),
    row("swift requires both files", 'required=("swift.xml", "swift-swift-testing.xml")', 'required=("swift.xml",)',
        test_the_registry_runs_each_suite_with_structured_results),
    row("XCTest needs the parallel runner", '"--jobs", "4", "--parallel", "--num-workers", "4",', '"--jobs", "4",',
        test_the_registry_runs_each_suite_with_structured_results),
    row("snapshot never gates", '(("{python}", "tools/verify.py", "--snapshot"),), PLATFORMS, gates=False,',
        '(("{python}", "tools/verify.py", "--snapshot"),), PLATFORMS,',
        test_the_registry_runs_each_suite_with_structured_results),
    row("commands show placeholders", "    templates = display_steps(suite)\n",
        '    templates = [" ".join(expand(token, values) for token in step) for step in suite.steps]\n',
        test_no_local_path_reaches_the_summary_or_the_ledger),
    row("summary path guard", "    if hits:\n        raise ValueError", "    if False:\n        raise ValueError",
        test_a_summary_that_would_name_a_local_path_is_refused),
    row("step summary written",
        '        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:\n'
        "            stream.write(markdown)\n", "        pass\n", test_ci_appends_the_summary_to_the_step_summary),
    row("json keeps stdout clean", "    echo = sys.stderr if args.json else sys.stdout", "    echo = sys.stdout",
        test_json_prints_only_the_summary_on_stdout),
    row("platform is this machine's", "    if target != here:\n", "    if False:\n",
        test_a_run_for_another_platform_is_refused_before_anything_runs),
    row("host is an alias", "    if not HOST.fullmatch(given):\n", "    if False:\n",
        test_a_host_must_be_a_short_alias, host="two words"),
    row("hostname never read", "    if given is None:\n        return UNNAMED\n",
        '    if given is None:\n        import socket\n        return socket.gethostname().split(".")[0]\n',
        test_a_run_without_a_host_is_unnamed_and_never_reads_the_hostname),
    row("unnamed is not a host", "    if given.lower() == UNNAMED:\n", "    if False:\n",
        test_the_label_of_a_run_without_a_host_is_not_a_host, host="Unnamed"),
    row("recording needs a host", '    if host is None:\n        return "--record needs --host',
        '    if False:\n        return "--record needs --host',
        test_record_refuses_a_run_without_a_host_before_anything_runs),
    row("unverifiable tree refused", "    if dirty is None and not allow_dirty:\n", "    if False:\n",
        test_record_refuses_a_tree_it_cannot_check),
    row("dirty tree refused", "    if dirty and not allow_dirty:\n", "    if False:\n",
        test_record_refuses_uncommitted_changes_unless_allowed),
    row("dirty row marked",
        "    return f\"{commit} +dirty ({count} file{'' if count == 1 else 's'})\" if count else commit",
        "    return commit", test_record_refuses_uncommitted_changes_unless_allowed),
    row("ledger is not dirt", "        if path != LEDGER:", "        if True:",
        test_record_refuses_uncommitted_changes_unless_allowed),
    row("another repository's HEAD ignored",
        "    if head and not (git(root, \"ls-files\", \"--\", \".\") or \"\").strip():\n", "    if False:\n",
        test_a_copy_inside_another_repository_is_an_unverified_copy),
    row("copy row marked", '        return f"{commit} (unverified copy)"', "        return commit",
        test_rows_are_inserted_newest_first_in_the_ledger_format),
    row("newest first", "    lines[start:end] = sorted(", "    lines[end:end] = sorted(",
        test_rows_are_inserted_newest_first_in_the_ledger_format),
    row("run-start order", 'key=lambda line: line.split("|", 2)[1].strip(), reverse=True)',
        'key=lambda line: line.split("|", 2)[1].strip(), reverse=False)',
        test_a_slow_earlier_run_records_below_a_newer_run_without_losing_rows),
]
if os.name != "nt":
    MUTATIONS += [
        row("whole group stopped",
            "        os.killpg(process.pid, signal.SIGTERM)\n        process.wait(timeout=STOP_GRACE)\n"
            "    except (OSError, subprocess.TimeoutExpired):\n        pass\n    finally:\n        try:\n"
            "            os.killpg(process.pid, signal.SIGKILL)",
            "        process.terminate()\n        process.wait(timeout=STOP_GRACE)\n"
            "    except (OSError, subprocess.TimeoutExpired):\n        pass\n    finally:\n        try:\n"
            "            process.kill()", test_a_hung_suite_is_killed_with_everything_it_started),
        row("macOS swift suite needs the package's Swift",
            'required=("swift.xml", "swift-swift-testing.xml"),\n          min_swift=(6, 2), timeout=3600),',
            'required=("swift.xml", "swift-swift-testing.xml"),\n          timeout=3600),',
            test_an_older_swift_skips_the_macos_swift_suites_with_a_reason, suite_id="swift"),
        row("mac package needs the package's Swift", 'needs=("swift", "codesign"), min_swift=(6, 2)),',
            'needs=("swift", "codesign")),', test_an_older_swift_skips_the_macos_swift_suites_with_a_reason,
            suite_id="mac-package"),
        row("SIGTERM before SIGKILL", "        os.killpg(process.pid, signal.SIGTERM)\n",
            "        os.killpg(process.pid, signal.SIGKILL)\n",
            test_a_stopped_step_gets_sigterm_first_so_its_exit_trap_cleans_up),
        row("SIGKILL after the grace", "            os.killpg(process.pid, signal.SIGKILL)  # a child",
            "            pass  # a child", test_a_step_that_ignores_sigterm_is_killed_when_the_grace_runs_out),
        row("a second Ctrl-C still kills",
            "    finally:\n        try:\n            os.killpg(process.pid, signal.SIGKILL)",
            "    if True:\n        try:\n            os.killpg(process.pid, signal.SIGKILL)",
            test_a_second_ctrl_c_during_the_grace_still_kills_the_step),
        row("thread caps", "    environment.update(THREAD_CAPS)\n", "",
            test_every_suite_gets_the_thread_caps_and_this_interpreter),
        row("this interpreter", "        return sys.executable, None",
            '        return shutil.which("python3") or "python3", None',
            test_every_suite_gets_the_thread_caps_and_this_interpreter),
    ]
if HERE == "linux":
    MUTATIONS += [
        row("learning suite waits for its script", "missing_tool_skips=True, precondition=learning_script_present,",
            "missing_tool_skips=True,", test_the_linux_swift_learning_suite_skips_until_it_can_run_then_reads_its_file),
        row("learning suite skips without Swift", "missing_tool_skips=True, precondition=learning_script_present,",
            "precondition=learning_script_present,",
            test_the_linux_swift_learning_suite_skips_until_it_can_run_then_reads_its_file),
        row("learning suite needs its XCTest file",
            'required=("swift-learning.xml",), optional=("swift-learning-swift-testing.xml",),',
            'optional=("swift-learning.xml", "swift-learning-swift-testing.xml"),',
            test_the_linux_swift_learning_suite_skips_until_it_can_run_then_reads_its_file),
        row("unpacked toolchain found",
            '        toolchains = sorted(Path.home().glob(".swift-toolchain/*/usr/bin/swift"))',
            "        toolchains = []", test_the_linux_swift_learning_suite_skips_until_it_can_run_then_reads_its_file),
    ]


@pytest.mark.parametrize("anchor, replacement, claim, parameters", MUTATIONS)
def test_deleting_a_runner_guard_fails_its_test(anchor, replacement, claim, parameters, tmp_path, monkeypatch,
                                               capsys):
    source = (harness.ROOT / "tools/run_tests.py").read_text(encoding="utf-8")
    assert source.count(anchor) == 1, "the guard moved: update this row"
    monkeypatch.setitem(harness.OVERRIDES, "run_tests", source.replace(anchor, replacement))
    harness.tool("run_tests")  # the mutant itself must load, or any failure below proves nothing
    fixtures = {"tmp_path": tmp_path, "monkeypatch": monkeypatch, "capsys": capsys}
    arguments = {name: fixtures[name] if name in fixtures else parameters[name]
                 for name in inspect.signature(claim).parameters}
    with pytest.raises((Exception, pytest.fail.Exception)):
        claim(**arguments)


@pytest.mark.skipif(HERE != "linux" or shutil.which("bash") is None or shutil.which("cp") is None,
                    reason="the Linux Swift learning suite runs on Linux")
@pytest.mark.parametrize(("printed", "expected"), [
    ("Swift version 6.1.2 (swift-6.1.2-RELEASE)", "Swift 6.1 is older than 6.2"),
    ("swift-driver version: 1.0 Apple Swift version 5.10", "Swift 5.10 is older than 6.2"),
    ("no version here", "Swift (unknown version) is older than 6.2"),
])
def test_an_older_swift_skips_the_linux_learning_suite_instead_of_failing_it(tmp_path, monkeypatch, printed,
                                                                             expected):
    """A hosted image's toolchain can be older than the package's tools
    version; that is a missing tool, not a red Linux gate."""
    rt = harness.tool("run_tests")
    suite = next(suite for suite in rt.SUITES if suite.id == "swift-learning-linux")
    root = make_root(tmp_path)
    script = root / rt.LEARNING_SCRIPT
    script.parent.mkdir(parents=True)
    script.write_text('cp fixtures/xunit-xctest.xml "$1/swift-learning.xml"\n', encoding="utf-8")
    swift = tmp_path / "swift"
    swift.write_text(f"#!/bin/sh\necho '{printed}'\n", encoding="utf-8")
    swift.chmod(0o755)
    monkeypatch.setenv("DICTATION_SWIFT", str(swift))
    assert run(rt, root, registry=(suite,)) == 2
    assert by_id(last_summary(root))["swift-learning-linux"]["skip_reasons"] == [expected]
    assert rt.swift_release(str(swift)) != (6, 2)
