#!/usr/bin/env python3
"""Run Dictation's test suites on this machine and keep a summary of the run.

One registry (SUITES) owns every suite's command, the platforms it runs on and
the result file its counts come from. The CI workflow runs this script rather
than repeating the commands. Counts are read from the suites' JUnit, TRX and
xUnit files, never from console text, with one exception: SwiftPM's parallel
xUnit file writes a skipped XCTest as a pass, so that skip is read from
XCTest's own "Test Case '...' skipped" line in a serial rerun (XCTEST_SKIPPED).

Each run writes evidence/test-runs/<UTC>-<platform>-<host>-<sha7>/, which is
gitignored: <suite>.log with everything the suite printed (also shown on the
console), the result files, summary.json ("dictation.test-run/1") and
summary.md.

Exit 0 = OK: every gating suite that ran passed, and skipped suites are named.
Exit 1 = FAILED: a failure in a result file, a non-zero exit, a required
         toolchain missing, or exit 0 with a missing or empty result file.
Exit 2 = INCOMPLETE: nothing ran (every suite skipped or ran no tests), or the
         run was refused before it started.

--record adds one row to TEST-LOG.md, newest first. It needs --host, the
machine's short alias: the machine's own hostname is never read, so a run
without --host is filed as "unnamed" and cannot record. It refuses a tree with
uncommitted changes under apps/dictation, or one it cannot check (a copy with
no git history), unless --allow-dirty is given, which marks the row. A row
carries counts and short reasons only, never test output or a local path.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
from typing import Callable, NamedTuple
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "dictation.test-run/1"
PLATFORMS = ("linux", "macos", "windows")
LEDGER = "TEST-LOG.md"
LEDGER_HEADER = ("| Date (UTC) | Commit | Platform/arch | Host | Suites (passed/failed/skipped) "
                 "| Learning tests | Verdict | Not covered |")
EXIT = {"OK": 0, "FAILED": 1, "INCOMPLETE": 2}

# ─── Suites ──────────────────────────────────────────────────────────────────
# Placeholders in a step: {python} is this interpreter (sys.executable; on
# Windows `python3` can be the Store stub), {dotnet}, {swift}, {bash} and
# {codesign} the resolved tools, {run} the run directory, {tmp} a scratch
# directory deleted after the suite, and {id} the suite id. Commands in the
# summary show the placeholders' names, never the paths behind them.

# Thread caps: native maths libraries on one thread, .NET on four processors.
# Every suite gets them, because the machines that run it are shared.
THREAD_CAPS = {"OPENBLAS_NUM_THREADS": "1", "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1",
               "DOTNET_PROCESSOR_COUNT": "4"}
# The worker tests that run on tests/requirements.txt (numpy and pytest). The
# stream and partial-gate tests cover the live-preview modules every package
# ships (ops_stream_whisper, partial_gate), so they gate rather than wait in
# worker-full; their five cases that reach the meeting diarizer skip without
# scipy (on a requirements-only venv, measured on Linux and macOS).
# test_junk_filter_mac holds the junk filter, including the dictation opt-out
# from its stock-phrase rule.
PYTHON_GATE = ("tests", "runtime/transcription_worker/test_asr_server.py",
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
LEARNING_SCRIPT = "macos/tools/test-learning-on-linux.sh"
# A test counts toward the learning subtotal when its full name contains one of these.
LEARNING = ("Learning", "learning_golden", "LearnedVocabulary", "learned_vocabulary")


class Suite(NamedTuple):
    id: str
    steps: tuple  # argv templates, run in order; the first failing step ends the suite
    platforms: tuple  # where the suite must run
    optional_on: tuple = ()  # where it runs only when its tools are installed
    needs: tuple = ()  # tools resolved before running (see find_tool)
    results: str | None = None  # "junit", "trx" or "xunit"; None means exit status only
    required: tuple = ()  # result files that must exist in the run directory
    optional: tuple = ()  # result files read when present; at least one file must exist
    gates: bool = True  # False: recorded, never changes the verdict
    opt_in: str | None = None  # the flag that adds it to the default set
    missing_tool_skips: bool = False  # a missing tool is a skip here, not a failure
    precondition: Callable | None = None  # root -> skip reason, or None to run
    min_swift: tuple = ()  # (major, minor) the Swift toolchain must reach, else SKIP
    timeout: int = 1800


def dotnet_test(suite_id: str, project: str) -> Suite:
    # One project per call, so a fixed TRX name inside the run directory is safe.
    return Suite(suite_id, (("{dotnet}", "test", project, "-c", "Release", "-m:4",
                             "--logger", "trx;LogFileName={id}.trx", "--logger", "console;verbosity=normal",
                             "--results-directory", "{run}"),),
                 ("linux", "windows"), optional_on=("macos",), needs=("dotnet",), results="trx",
                 required=(f"{suite_id}.trx",))


def learning_script_present(root: Path) -> str | None:
    return None if (root / LEARNING_SCRIPT).is_file() else f"{LEARNING_SCRIPT} is not in this tree"


def manifest_present(root: Path) -> str | None:
    """The public export leaves import-manifest.json out; there is no snapshot to compare with. A monorepo
    tree without it still runs the suite, which fails (tools/verify.py decides which tree this is)."""
    return "import-manifest.json is not in this tree (an exported copy)" if verify_tool().exported_copy(root) \
        else None


SUITES = (
    Suite("boundary", (("{python}", "tools/verify.py"),), PLATFORMS),
    Suite("python-gate", (("{python}", "-m", "pytest", *PYTHON_GATE, "-q", "-p", "no:cacheprovider", "-rs",
                           "--junitxml={run}/python-gate.junit.xml"),),
          PLATFORMS, results="junit", required=("python-gate.junit.xml",)),
    Suite("worker-full", (("{python}", "-m", "pytest", "runtime/transcription_worker", "-q", "-p",
                           "no:cacheprovider", "-rs", "--junitxml={run}/worker-full.junit.xml"),),
          PLATFORMS, results="junit", required=("worker-full.junit.xml",), opt_in="--full-worker", timeout=3600),
    dotnet_test("dotnet-services", "windows/Dictation.Services.Tests/Dictation.Services.Tests.csproj"),
    dotnet_test("dotnet-infra",
                "windows/Dictation.Infrastructure.Tests/Dictation.Infrastructure.Tests.csproj"),
    dotnet_test("dotnet-core", "windows/Dictation.Core.Tests/Dictation.Core.Tests.csproj"),
    Suite("win-app-build", (("{dotnet}", "build", "windows/Dictation.App/Dictation.App.csproj", "-c", "Release",
                             "-m:4"),), ("linux", "windows"), needs=("dotnet",)),
    Suite("win-package", (("{python}", "tools/package.py", "--platform", "windows", "--app-only", "--rid", "win-x64",
                           "--jobs", "4", "--dotnet", "{dotnet}", "--output", "{tmp}/package"),),
          ("linux", "windows"), needs=("dotnet",)),
    # --parallel because SwiftPM writes XCTest results to --xunit-output only
    # from its parallel runner (measured with SwiftPM 6.2): without it only the
    # Swift Testing file, <name>-swift-testing.xml, appears and the XCTest
    # counts are silently lost. Both files are therefore required. That file
    # also writes a skipped XCTest as a pass, so a serial rerun of the same
    # build follows, and read_results takes the skips it prints (XCTEST_SKIPPED).
    # Swift Build emits one Swift Testing product per target and older bundled
    # versions overwrite the XML with the last product. Use the native backend
    # for this lane until the toolchain includes the upstream aggregation fix:
    # https://github.com/swiftlang/swift-package-manager/pull/10263
    # A two-target repro on Swift 6.4 reports one case by default, both with native.
    # min_swift is macos/Package.swift's swift-tools-version, here and below.
    Suite("swift", (("{swift}", "test", "--package-path", "macos", "--build-system", "native", "--jobs", "4", "--parallel", "--num-workers", "4",
                     "--xunit-output", "{run}/swift.xml"),
                    ("{swift}", "test", "--package-path", "macos", "--build-system", "native", "--skip-build")),
          ("macos",), needs=("swift",), results="xunit", required=("swift.xml", "swift-swift-testing.xml"),
          min_swift=(6, 2), timeout=3600),
    # The platform-free Swift learning targets on a Linux toolchain. The script
    # exits with `swift test`'s status and writes <run>/swift-learning.xml.
    # Every learning test is an XCTest, so that file is required for the same
    # reason as on macOS; the Swift Testing file beside it is read when present.
    Suite("swift-learning-linux", (("{bash}", LEARNING_SCRIPT, "{run}"),), ("linux",), needs=("bash", "swift"),
          results="xunit", required=("swift-learning.xml",), optional=("swift-learning-swift-testing.xml",),
          missing_tool_skips=True, precondition=learning_script_present, min_swift=(6, 2), timeout=3600),
    # package.py builds with `swift` from PATH (it takes no --swift), which is
    # the one find_tool checks unless DICTATION_SWIFT names another.
    Suite("mac-package", (("{python}", "tools/package.py", "--platform", "macos", "--app-only", "--jobs", "4",
                           "--output", "{tmp}/package"),
                          ("{codesign}", "--verify", "--deep", "--strict", f"{{tmp}}/package/{json.loads((ROOT / 'product.json').read_text())['name']}.app")),
          ("macos",), needs=("swift", "codesign"), min_swift=(6, 2)),
    # Compares the tree with the extraction snapshot; fails by design after later fixes.
    Suite("snapshot", (("{python}", "tools/verify.py", "--snapshot"),), PLATFORMS, gates=False,
          precondition=manifest_present),
)

# ─── This machine ────────────────────────────────────────────────────────────

SYSTEMS = {"Linux": "linux", "Darwin": "macos", "Windows": "windows"}
ARCHES = {"x86_64": "x64", "amd64": "x64", "aarch64": "arm64", "arm64": "arm64"}
TOOLS = ("python", "dotnet", "swift", "bash", "codesign")
MISSING = {
    "dotnet": "dotnet not found: put the .NET 9 SDK on PATH or set DICTATION_DOTNET",
    "swift": "no Swift toolchain: set DICTATION_SWIFT, put swift on PATH or unpack one under ~/.swift-toolchain",
    "bash": "bash not found",
    "codesign": "codesign not found (Xcode command line tools)",
}
HOST = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,31}")
UNNAMED = "unnamed"  # the run's label without --host, so never a --host itself: a row needs a real alias
COMMIT = re.compile(r"[0-9a-fA-F]{7,40}")


def this_platform() -> str:
    return SYSTEMS.get(platform.system(), platform.system().lower())


def native_arch() -> str:
    """The machine's architecture, not the interpreter's: an x64 Python under
    emulation on Windows on Arm, or under Rosetta on a Mac, reports x64."""
    machine = platform.machine().lower()
    if os.name == "nt":
        try:
            import ctypes
            kernel32 = ctypes.windll.kernel32
            kernel32.GetCurrentProcess.restype = ctypes.c_void_p
            kernel32.IsWow64Process2.argtypes = (ctypes.c_void_p, ctypes.POINTER(ctypes.c_ushort),
                                                 ctypes.POINTER(ctypes.c_ushort))
            process, native = ctypes.c_ushort(), ctypes.c_ushort()
            if kernel32.IsWow64Process2(kernel32.GetCurrentProcess(), ctypes.byref(process), ctypes.byref(native)):
                machine = {0xAA64: "arm64", 0x8664: "x64", 0x014C: "x86"}.get(native.value, machine)
        except (AttributeError, OSError):
            pass  # before Windows 10 1709: keep the interpreter's view
    elif platform.system() == "Darwin" and machine == "x86_64":
        try:
            if subprocess.run(["sysctl", "-n", "hw.optional.arm64"], capture_output=True, text=True,
                              timeout=10).stdout.strip() == "1":
                machine = "arm64"
        except (OSError, subprocess.SubprocessError):
            pass
    return ARCHES.get(machine, machine)


def find_tool(name: str) -> tuple[str | None, str | None]:
    """(path, None) or (None, why it is missing)."""
    if name == "python":
        return sys.executable, None
    variable = f"DICTATION_{name.upper()}"
    if name in ("dotnet", "swift") and os.environ.get(variable):
        override = os.environ[variable]
        return (override, None) if Path(override).is_file() else (None, f"{variable} does not name a file")
    found = shutil.which(name)
    if not found and name == "swift":
        toolchains = sorted(Path.home().glob(".swift-toolchain/*/usr/bin/swift"))
        found = str(toolchains[-1]) if toolchains else None
    return (found, None) if found else (None, MISSING[name])


def host_alias(given: str | None) -> str:
    """The name a run is filed under: --host, or UNNAMED. Never the machine's hostname, which can name a
    person or a team machine, and a recorded row is committed."""
    if given is None:
        return UNNAMED
    if given.lower() == UNNAMED:
        raise ValueError(f"--host {given!r} is the label of a run without --host; give this machine's short alias")
    if not HOST.fullmatch(given):
        raise ValueError(f"--host must be a short machine alias such as build-1 or my-laptop, not {given!r}")
    return given


def git(root: Path, *args: str) -> str | None:
    try:
        return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True,
                              timeout=120).stdout
    except (OSError, subprocess.SubprocessError):
        return None


def changed_paths(status: str, prefix: str) -> list[str]:
    """Paths from `git status --porcelain -z`, relative to apps/dictation."""
    entries, paths, index = status.split("\0"), set(), 0
    while index < len(entries):
        entry = entries[index]
        index += 1
        if len(entry) < 4:
            continue
        if "R" in entry[:2] or "C" in entry[:2]:
            index += 1  # a rename's original path follows
        path = entry[3:]
        path = path[len(prefix):] if path.startswith(prefix) else path
        if path != LEDGER:  # recording a row must not make the next row dirty
            paths.add(path)
    return sorted(paths)


def source_state(root: Path, given: str | None) -> tuple[str | None, list[str] | None]:
    """(commit, dirty paths). dirty is None when there is no git to ask, as in a
    copy of apps/dictation; the commit then comes from --commit."""
    if given is not None and not COMMIT.fullmatch(given):
        raise ValueError(f"--commit must be a hexadecimal commit id, not {given!r}")
    head = (git(root, "rev-parse", "HEAD") or "").strip() or None
    if head and not (git(root, "ls-files", "--", ".") or "").strip():
        head = None  # a copy inside some other repository, whose HEAD says nothing about this tree
    if head and given and not head.startswith(given.lower()):
        raise ValueError(f"--commit {given} is not this checkout's HEAD {head[:12]}")
    if head is None:
        return (given.lower() if given else None), None
    prefix = (git(root, "rev-parse", "--show-prefix") or "").strip()
    status = git(root, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".")
    return head, None if status is None else changed_paths(status, prefix)


def tool_version(executable: str | None, pattern: str) -> str | None:
    if executable is None:
        return None
    environment = dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    try:
        completed = subprocess.run([executable, "--version"], capture_output=True, text=True, timeout=120,
                                   env=environment, stdin=subprocess.DEVNULL)
    except (OSError, subprocess.SubprocessError):
        return None
    match = re.search(pattern, completed.stdout + completed.stderr, re.M)
    return match.group(1) if match else None


# ─── Result files ────────────────────────────────────────────────────────────
# Each parser returns (full test name, "passed" | "failed" | "skipped") per test.

TRX_SKIPPED = {"NotExecuted", "Inconclusive", "NotRunnable", "Pending"}


def junit_cases(path: Path) -> list[tuple[str, str]]:
    """pytest's --junitxml, and SwiftPM's --xunit-output (XCTest and Swift Testing)."""
    cases = []
    for case in ET.parse(path).getroot().iter("testcase"):
        name = ".".join(part for part in (case.get("classname"), case.get("name")) if part)
        tags = {child.tag for child in case}
        status = "failed" if tags & {"failure", "error"} else "skipped" if "skipped" in tags else "passed"
        cases.append((name, status))
    return cases


def trx_cases(path: Path) -> list[tuple[str, str]]:
    """dotnet test's TRX logger. Only direct children of <Results> count: a
    data-driven parent's <InnerResults> repeat tests it already summarises."""
    root = ET.parse(path).getroot()
    ns = root.tag[:root.tag.index("}") + 1] if root.tag.startswith("{") else ""
    classes = {}
    for test in root.iter(f"{ns}UnitTest"):
        method = test.find(f"{ns}TestMethod")
        if method is not None:
            classes[test.get("id")] = method.get("className", "")
    results = root.find(f"{ns}Results")
    cases = []
    for result in [] if results is None else results.findall(f"{ns}UnitTestResult"):
        name = result.get("testName", "")
        class_name = classes.get(result.get("testId"), "")
        if class_name and not name.startswith(class_name):
            name = f"{class_name}.{name}"
        outcome = result.get("outcome", "")
        # Anything that is neither a pass nor a known not-run state is a failure.
        cases.append((name, "passed" if outcome == "Passed" else "skipped" if outcome in TRX_SKIPPED else "failed"))
    return cases


PARSERS = {"junit": junit_cases, "xunit": junit_cases, "trx": trx_cases}

# SwiftPM's parallel runner writes an XCTest that called XCTSkip into the xunit
# file as a plain <testcase>, which reads as a pass. XCTest names the skip only
# when it runs the tests itself, serially: "Test Case '-[Module.Class method]'
# skipped" on macOS, "Test Case 'Class.method' skipped" on Linux. The parallel
# run prints no such line.
XCTEST_SKIPPED = re.compile(r"^Test Case '(?:-\[(?:\w+\.)*(\w+) (\w+)\]|(?:\w+\.)*(\w+)\.(\w+))' skipped", re.M)


def xctest_skips(cases: list[tuple[str, str]], log: Path) -> list[tuple[str, str]]:
    """Each passed case the suite's log names as a skipped XCTest, as skipped.
    Matched on class and method; a failure stays a failure."""
    text = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
    skipped = {(match.group(1) or match.group(3), match.group(2) or match.group(4))
               for match in XCTEST_SKIPPED.finditer(text)}
    return [(name, "skipped" if status == "passed" and tuple(name.split(".")[-2:]) in skipped else status)
            for name, status in cases]


def tally(cases: list[tuple[str, str]]) -> dict:
    counts = {"passed": 0, "failed": 0, "skipped": 0}
    learning = dict(counts)
    for name, status in cases:
        counts[status] += 1
        if any(marker in name for marker in LEARNING):
            learning[status] += 1
    return {**counts, "learning": learning}


def read_results(suite: Suite, run_dir: Path) -> tuple[dict | None, str | None]:
    """(counts, None), or (None, why the result files cannot be trusted)."""
    absent = [name for name in suite.required if not (run_dir / name).is_file()]
    present = [run_dir / name for name in suite.required + suite.optional if (run_dir / name).is_file()]
    if absent or not present:
        return None, f"no result file {' or '.join(absent or suite.optional)}"
    cases = []
    for path in present:
        if path.stat().st_size == 0:
            return None, f"{path.name} is empty"
        try:
            cases += PARSERS[suite.results](path)
        except ET.ParseError:
            return None, f"{path.name} is not readable XML"
    if suite.results == "xunit":
        cases = xctest_skips(cases, run_dir / f"{suite.id}.log")
    return tally(cases), None


# ─── Running a suite ─────────────────────────────────────────────────────────

def expand(token: str, values: dict) -> str:
    return re.sub(r"\{(\w+)\}", lambda match: values.get(match.group(1), match.group(0)), token)


def display_steps(suite: Suite) -> list[str]:
    """Each step as summary.json and the ledger show it: repo-relative, with
    every machine-specific value replaced by its placeholder's name."""
    names = {name: name for name in TOOLS} | {"run": "<run>", "tmp": "<tmp>", "id": suite.id}
    steps = []
    for step in suite.steps:
        tokens = [expand(token, names) for token in step]
        steps.append(" ".join(f'"{token}"' if re.search(r"[\s;]", token) else token for token in tokens))
    return steps


def display(suite: Suite) -> str:
    return " && ".join(display_steps(suite))


def suite_environment(tools: dict) -> dict:
    environment = dict(os.environ)
    environment.update(THREAD_CAPS)
    # No MSBuild worker node outlives a suite on a shared host, or holds the
    # output pipe open after the step has exited.
    environment.update(MSBUILDDISABLENODEREUSE="1", DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
    if tools.get("swift"):
        environment["DICTATION_SWIFT"] = tools["swift"]  # the learning script uses the same toolchain
    return environment


STOP_GRACE = 10  # seconds between SIGTERM and SIGKILL when a step is stopped


def stop(process: subprocess.Popen) -> None:
    """Stop a step and everything it started (dotnet test leaves a testhost).
    POSIX: SIGTERM to the whole group first, so a script's EXIT trap can remove
    its scratch directory (the learning script's /tmp package), then SIGKILL
    to whatever is left after STOP_GRACE seconds, or at once when a second
    Ctrl-C cuts the grace short."""
    if os.name == "nt":
        try:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(process.pid)], capture_output=True, timeout=60)
        except (OSError, subprocess.SubprocessError):
            process.kill()
        process.wait()
        return
    try:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=STOP_GRACE)
    except (OSError, subprocess.TimeoutExpired):
        pass
    finally:
        try:
            os.killpg(process.pid, signal.SIGKILL)  # a child that ignored SIGTERM or outlived the step
        except OSError:
            process.kill()  # no group left to signal: the step alone, a no-op once it is reaped
        process.wait()


def execute(argv: list[str], cwd: Path, environment: dict, log, echo, timeout: int) -> tuple[int | None, str | None]:
    """Run one step, copying its output to the log and the console as it
    arrives. Returns (exit status, None) or (None, why it did not finish)."""
    group = ({"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt"
             else {"start_new_session": True})
    try:
        process = subprocess.Popen(argv, cwd=cwd, env=environment, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, **group)
    except OSError as error:
        return None, f"could not start {Path(argv[0]).name}: {error.strerror or type(error).__name__}"

    def pump() -> None:
        try:
            for line in iter(process.stdout.readline, b""):
                log.write(line)
                echo.write(line.decode("utf-8", "replace"))
                echo.flush()
        except (OSError, ValueError):
            pass  # the log closed under a reader that outlived its step

    reader = threading.Thread(target=pump, daemon=True)
    reader.start()
    try:
        code = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        stop(process)
        reader.join(10)
        return None, f"timed out after {timeout} s"
    except KeyboardInterrupt:
        stop(process)
        raise
    reader.join(60)  # a leftover grandchild can hold the pipe; keep the exit status and move on
    return code, None


def judge(suite: Suite, exit_code: int | None, failure: str | None, counts: dict | None,
          problem: str | None) -> tuple[str, str | None]:
    """(PASS | FAIL | SKIP, reason)."""
    if failure:
        return "FAIL", failure
    if suite.results is None:
        return ("PASS", None) if exit_code == 0 else ("FAIL", f"exit {exit_code}")
    if problem:
        return "FAIL", f"exit {exit_code} and {problem}"
    if counts["failed"]:
        return "FAIL", f"{counts['failed']} failed in the result file"
    if exit_code != 0:
        return "FAIL", f"exit {exit_code} with no failure in the result file"
    if counts["passed"] == 0:
        return "SKIP", f"ran no tests ({counts['skipped']} skipped)"
    return "PASS", None


def blank_record(suite: Suite) -> dict:
    return {"id": suite.id, "command": display(suite), "outcome": "SKIP", "exit": None, "passed": None,
            "failed": None, "skipped": None, "skip_reasons": [], "failure": None, "duration_s": 0.0, "log": None,
            "gates": suite.gates, "learning": None}


def run_suite(suite: Suite, root: Path, run_dir: Path, tools: dict, echo) -> dict:
    record = blank_record(suite)
    values = {name: path for name, path in tools.items() if path} | {"run": str(run_dir), "id": suite.id}
    scratch = Path(tempfile.mkdtemp(prefix=f"dictation-{suite.id}-"))
    values["tmp"] = str(scratch)
    # What the log and the summary show: placeholders, never the values behind them.
    templates = display_steps(suite)
    record["command"] = " && ".join(templates)
    environment = suite_environment(tools)
    log_path = run_dir / f"{suite.id}.log"
    echo.write(f"\n─── {suite.id} ───\n")
    started = time.monotonic()
    exit_code, failure = None, None
    try:
        with log_path.open("wb") as log:
            for step, template in zip(suite.steps, templates):
                banner = f"$ {template}\n"
                log.write(banner.encode("utf-8"))
                echo.write(banner)
                exit_code, failure = execute([expand(token, values) for token in step], root, environment, log,
                                             echo, suite.timeout)
                if failure or exit_code != 0:
                    break
    finally:
        shutil.rmtree(scratch, ignore_errors=True)
    counts, problem = read_results(suite, run_dir) if suite.results and not failure else (None, None)
    outcome, reason = judge(suite, exit_code, failure, counts, problem)
    record.update(outcome=outcome, exit=exit_code, duration_s=round(time.monotonic() - started, 1),
                  log=log_path.name)
    if counts:
        record.update(passed=counts["passed"], failed=counts["failed"], skipped=counts["skipped"],
                      learning=counts["learning"])
    if outcome == "SKIP":
        record["skip_reasons"] = [reason]
    else:
        record["failure"] = reason
    echo.write(f"{suite.id}: {outcome}{counts_text(record)}{f' ({reason})' if reason else ''}"
               f" in {record['duration_s']} s\n")
    return record


def plan(suite: Suite, target: str, selected: set | None, full_worker: bool, root: Path,
         tools: dict | None) -> tuple[str, str | None] | None:
    """("run", None), ("skip" | "fail", reason), or None when the suite does not
    belong to this platform and was not asked for. tools None: another machine."""
    if target not in suite.platforms + suite.optional_on:
        if selected is None or suite.id not in selected:
            return None
        return "skip", f"runs on {', '.join(suite.platforms)} only"
    if selected is not None and suite.id not in selected:
        return "skip", "not selected with --suite"
    if selected is None and suite.opt_in and not full_worker:
        return "skip", f"opt-in: {suite.opt_in}"
    reason = suite.precondition(root) if suite.precondition else None
    if reason:
        return "skip", reason
    for name in suite.needs if tools is not None else ():
        if tools[name][0] is None:
            skips = suite.missing_tool_skips or target not in suite.platforms
            return ("skip" if skips else "fail"), tools[name][1]
    if suite.min_swift and tools is not None:
        # macos/Package.swift, and the learning script's scratch package after
        # it, declare a swift-tools-version that an older toolchain (a hosted
        # image's default Xcode, say) refuses before building anything: that is
        # a missing tool, not a failed test.
        found = swift_release(tools["swift"][0])
        if found is None or found < suite.min_swift:
            floor = ".".join(map(str, suite.min_swift))
            return "skip", f"Swift {'.'.join(map(str, found)) if found else '(unknown version)'} is older than {floor}"
    return "run", None


def swift_release(executable: str | None) -> tuple[int, int] | None:
    version = tool_version(executable, r"Swift version (\d+\.\d+)")
    match = re.match(r"(\d+)\.(\d+)", version or "")
    return (int(match.group(1)), int(match.group(2))) if match else None


# ─── Verdict, summary and ledger ─────────────────────────────────────────────

def verdict(records: list[dict]) -> str:
    gating = [record for record in records if record["gates"]]
    if any(record["outcome"] == "FAIL" for record in gating):
        return "FAILED"
    return "OK" if any(record["outcome"] == "PASS" for record in gating) else "INCOMPLETE"


def counts_text(record: dict) -> str:
    if record["passed"] is None:
        return ""
    return f", {record['passed']} passed, {record['failed']} failed, {record['skipped']} skipped"


def learning_total(records: list[dict]) -> dict:
    total = {"passed": 0, "failed": 0, "skipped": 0}
    for record in records:
        for key, value in (record["learning"] or {}).items():
            total[key] += value
    return total


_VERIFY = []


def verify_tool():
    """tools/verify.py, loaded once."""
    if not _VERIFY:
        spec = importlib.util.spec_from_file_location("dictation_verify", Path(__file__).with_name("verify.py"))
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        _VERIFY.append(module)
    return _VERIFY[0]


def builder_path() -> re.Pattern:
    """tools/verify.py's builder-path rule."""
    return next(pattern for name, pattern, _ in verify_tool().LEAKS if name == "builder path")


# The boundary check's builder-path rule, plus absolute paths under the usual
# system and temporary roots and Windows drive paths.
LOCAL_PATH = re.compile(r"(?<![\w.<>~-])(?:/(?:home|Users|tmp|var|private|opt|usr|root|mnt|srv|Volumes)/"
                        r"|[A-Za-z]:[\\/])")


def check_no_paths(text: str, local: tuple[str, ...]) -> None:
    hits = [value for value in local if len(Path(value).parts) >= 3 and value in text]
    hits += [match.group(0) for pattern in (builder_path(), LOCAL_PATH) for match in pattern.finditer(text)]
    if hits:
        raise ValueError(f"refusing to write a summary that names a local path: {sorted(set(hits))[:5]}")


def summary_markdown(summary: dict) -> str:
    records = summary["suites"]
    skips = [f"{record['id']} ({'; '.join(record['skip_reasons'])})"
             for record in records if record["outcome"] == "SKIP"]
    learning = summary["learning"]
    lines = [f"# Dictation tests: {summary['verdict']}", "",
             f"**{summary['verdict']}** on {summary['platform']}/{summary['arch']} ({summary['host']}), "
             f"commit {commit_cell(summary)}, {summary['started_utc']}, {summary['duration_s']} s. "
             f"Learning tests: {learning['passed']} passed, {learning['failed']} failed, "
             f"{learning['skipped']} skipped.",
             "", f"Skipped: {', '.join(skips)}." if skips else "Nothing skipped.", "",
             "| Suite | Outcome | Passed | Failed | Skipped | Time (s) | Command, or why not |",
             "|---|---|---:|---:|---:|---:|---|"]
    for record in records:
        outcome = record["outcome"] if record["gates"] else f"{record['outcome']} (not gating)"
        note = "; ".join(record["skip_reasons"]) or record["failure"] or f"`{record['command']}`"
        numbers = ["–" if record[key] is None else str(record[key]) for key in ("passed", "failed", "skipped")]
        lines.append(f"| {record['id']} | {outcome} | {' | '.join(numbers)} | {record['duration_s']} | "
                     f"{note.replace('|', '/')} |")
    lines += ["", "Counts come from each suite's result file, except an XCTest skip: SwiftPM's parallel file "
              "writes it as a pass, so it is read from XCTest's own line in the serial rerun. Raw logs are in the "
              "run directory, which is not committed."]
    return "\n".join(lines) + "\n"


def commit_cell(summary: dict) -> str:
    commit = (summary["commit"] or "unknown")[:7]
    if summary["dirty"] is None:
        return f"{commit} (unverified copy)"
    count = len(summary["dirty"] or ())
    return f"{commit} +dirty ({count} file{'' if count == 1 else 's'})" if count else commit


def suite_cell(record: dict) -> str:
    if record["passed"] is not None:
        cell = f"{record['id']} {record['passed']}/{record['failed']}/{record['skipped']}"
        cell += " FAIL" if record["outcome"] == "FAIL" else ""
    else:
        cell = f"{record['id']} {'pass' if record['outcome'] == 'PASS' else 'FAIL'}"
        cell += f" ({record['failure']})" if record["failure"] else ""
    return cell if record["gates"] else f"{cell.replace('FAIL', 'fail')}, not gating"


def ledger_row(summary: dict) -> str:
    records = summary["suites"]
    learning = summary["learning"]
    cells = [summary["started_utc"][:16].replace("T", " "), commit_cell(summary),
             f"{summary['platform']}/{summary['arch']}", summary["host"],
             " · ".join(suite_cell(record) for record in records if record["outcome"] != "SKIP") or "none ran",
             f"{learning['passed']}/{learning['failed']}/{learning['skipped']}" if any(learning.values()) else "none",
             summary["verdict"],
             "; ".join(f"{record['id']} ({'; '.join(record['skip_reasons'])})"
                       for record in records if record["outcome"] == "SKIP") or "–"]
    return "| " + " | ".join(cell.replace("|", "/") for cell in cells) + " |"


def ledger_table(text: str) -> int | None:
    """Index of the ledger's table header line, when the separator follows it."""
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if line.strip() == LEDGER_HEADER and index + 1 < len(lines) and lines[index + 1].startswith("|---"):
            return index
    return None


def record_refusal(ledger: Path, dirty: list[str] | None, allow_dirty: bool, host: str | None) -> str | None:
    if host is None:
        return "--record needs --host with this machine's short alias; the hostname is never read"
    if not ledger.is_file() or ledger_table(ledger.read_text(encoding="utf-8")) is None:
        return f"{LEDGER} with its table header is required to record a row"
    if dirty is None and not allow_dirty:
        return ("no git status for this tree, so the row cannot claim its commit; "
                "pass --allow-dirty to record it marked as an unverified copy")
    if dirty and not allow_dirty:
        return (f"{len(dirty)} uncommitted paths under apps/dictation ({', '.join(dirty[:5])}); "
                "commit them, or pass --allow-dirty to record the row marked dirty")
    return None


def insert_row(ledger: Path, row: str) -> None:
    """Newest run first, even when an earlier run finishes after a later one."""
    lines = ledger.read_text(encoding="utf-8").splitlines(keepends=True)
    index = ledger_table("".join(lines))
    ending = "\r\n" if lines[index].endswith("\r\n") else "\n"
    start = end = index + 2
    while end < len(lines) and lines[end].startswith("|"):
        end += 1
    # Stable sorting keeps the latest completion first within the same minute.
    existing = [line.rstrip("\r\n") + ending for line in lines[start:end]]
    lines[start:end] = sorted([row + ending, *existing],
                              key=lambda line: line.split("|", 2)[1].strip(), reverse=True)
    ledger.write_text("".join(lines), encoding="utf-8", newline="")


# ─── Command line ────────────────────────────────────────────────────────────

def make_run_dir(parent: Path, started: datetime, target: str, host: str, commit: str | None) -> Path:
    name = f"{started:%Y%m%dT%H%M%SZ}-{target}-{host}-{(commit or 'nocommit')[:7]}"
    parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(1, 100):
        candidate = parent / (name if attempt == 1 else f"{name}-{attempt}")
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            continue
    raise RuntimeError(f"no free run directory for {name}")


def list_suites(registry, target: str, here: str, args, root: Path, tools: dict, stream) -> int:
    rows = []
    for suite in registry:
        decision = plan(suite, target, set(args.suite) if args.suite else None, args.full_worker, root,
                        tools if target == here else None)
        state = f"not a {target} suite" if decision is None else (
            "runs" if decision[0] == "run" else f"{decision[0].upper()}: {decision[1]}")
        if state == "runs" and target != here and target in suite.optional_on:
            state = f"runs when {' and '.join(suite.needs)} is installed there, else SKIP"
        rows.append({"id": suite.id, "platforms": list(suite.platforms), "optional_on": list(suite.optional_on),
                     "gates": suite.gates, "here": state, "command": display(suite)})
    if args.json:
        print(json.dumps(rows, indent=2), file=stream)
        return 0
    print(f"Suites on {target}" + ("" if target == here else f" (listed from a {here} machine; tools not checked)"),
          file=stream)
    for row in rows:
        print(f"  {row['id']:<22} {row['here']}{'' if row['gates'] else ' (never gates)'}\n"
              f"  {'':<22} {row['command']}", file=stream)
    return 0


def parse_args(argv, registry):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--list", action="store_true", help="show each suite, whether it runs here and why not")
    parser.add_argument("--suite", action="append", choices=[suite.id for suite in registry],
                        help="run only this suite (repeatable)")
    parser.add_argument("--platform", choices=("auto",) + PLATFORMS, default="auto")
    parser.add_argument("--full-worker", action="store_true",
                        help="add the whole worker suite (needs torch, onnxruntime and openai-whisper)")
    parser.add_argument("--host", help="short machine alias for the run and the ledger; required with --record "
                        f"(without it the run is filed as {UNNAMED!r}; the hostname is never read)")
    parser.add_argument("--commit", help="commit id, when running from a copy with no git history")
    parser.add_argument("--json", action="store_true",
                        help="print summary.json on stdout; suite output goes to stderr")
    parser.add_argument("--ci", action="store_true", help="append summary.md to $GITHUB_STEP_SUMMARY when set")
    parser.add_argument("--record", action="store_true", help=f"add this run's row to {LEDGER}")
    parser.add_argument("--allow-dirty", action="store_true",
                        help="with --record: record from an uncommitted or unverifiable tree, marking the row")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None, *, registry=SUITES, root: Path = ROOT) -> int:
    args = parse_args(argv, registry)
    echo = sys.stderr if args.json else sys.stdout
    try:
        echo.reconfigure(errors="replace")  # a Windows console codepage must not end the run
    except (AttributeError, ValueError):
        pass
    here = this_platform()
    target = here if args.platform == "auto" else args.platform
    tools = {name: find_tool(name) for name in TOOLS}
    if args.list:
        return list_suites(registry, target, here, args, root, tools, sys.stdout)
    if target != here:
        print(f"run_tests: this is a {here} machine; run the {target} suites on a {target} machine, "
              "or the summary would claim a platform that did not run", file=sys.stderr)
        return EXIT["INCOMPLETE"]
    try:
        host = host_alias(args.host)
        commit, dirty = source_state(root, args.commit)
    except ValueError as error:
        print(f"run_tests: {error}", file=sys.stderr)
        return EXIT["INCOMPLETE"]
    ledger = root / LEDGER
    refusal = record_refusal(ledger, dirty, args.allow_dirty, args.host) if args.record else None
    if refusal:
        print(f"run_tests: not recording: {refusal}", file=sys.stderr)
        return EXIT["INCOMPLETE"]

    started_at, started, arch = datetime.now(timezone.utc), time.monotonic(), native_arch()
    run_dir = make_run_dir(root / "evidence/test-runs", started_at, target, host, commit)
    echo.write(f"Dictation tests on {target}/{arch} ({host}), commit {(commit or 'unknown')[:7]}; "
               f"evidence in {run_dir.name}\n")
    selected = set(args.suite) if args.suite else None
    records = []
    for suite in registry:
        decision = plan(suite, target, selected, args.full_worker, root, tools)
        if decision is None:
            continue
        if decision[0] == "run":
            records.append(run_suite(suite, root, run_dir, {name: path for name, (path, _) in tools.items()}, echo))
            continue
        record = blank_record(suite)
        if decision[0] == "skip":
            record["skip_reasons"] = [decision[1]]
        else:
            record.update(outcome="FAIL", failure=decision[1])
        echo.write(f"{suite.id}: {record['outcome']} ({decision[1]})\n")
        records.append(record)

    summary = {
        "schema": SCHEMA, "started_utc": f"{started_at:%Y-%m-%dT%H:%M:%SZ}",
        "duration_s": round(time.monotonic() - started, 1), "commit": commit, "dirty": dirty,
        "platform": target, "arch": arch, "os": platform.platform(), "host": host,
        "toolchains": {"python": platform.python_version(),
                       "dotnet": tool_version(tools["dotnet"][0], r"^\s*(\d+\.\d+\.\S+)\s*$"),
                       "swift": tool_version(tools["swift"][0], r"Swift version (\S+(?: \([^)]*\))?)")},
        "env_caps": THREAD_CAPS, "suites": records, "learning": learning_total(records),
        "verdict": verdict(records),
    }
    markdown = summary_markdown(summary)
    row = ledger_row(summary)
    local = (str(root), str(run_dir), str(Path.home()), sys.executable, tempfile.gettempdir())
    try:
        for text in (json.dumps(summary), markdown, row):
            check_no_paths(text, local)
    except ValueError as error:
        print(f"run_tests: {error}", file=sys.stderr)
        return EXIT["FAILED"]
    (run_dir / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    (run_dir / "summary.md").write_text(markdown, encoding="utf-8")
    if args.ci and os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write(markdown)
    if args.record:
        insert_row(ledger, row)

    echo.write(f"\n─── Summary: {summary['verdict']} ───\n")
    for record in records:
        reason = "; ".join(record["skip_reasons"]) or record["failure"] or ""
        echo.write(f"{record['outcome']:<5} {record['id']:<22} {record['duration_s']:>7} s{counts_text(record)}"
                   f"{f' ({reason})' if reason else ''}{'' if record['gates'] else ' [not gating]'}\n")
    learning = summary["learning"]
    passed, failed, skipped = ([record["id"] for record in records if record["gates"] and record["outcome"] == outcome]
                               for outcome in ("PASS", "FAIL", "SKIP"))
    echo.write(f"Learning tests: {learning['passed']} passed, {learning['failed']} failed, "
               f"{learning['skipped']} skipped\n")
    echo.write(f"{summary['verdict']}: {len(passed)} suites passed; failed: {', '.join(failed) or 'none'}; "
               f"skipped: {', '.join(skipped) or 'none'}; {summary['duration_s']} s; evidence/test-runs/{run_dir.name}"
               f"{f'; recorded in {LEDGER}' if args.record else ''}\n")
    echo.flush()
    if args.json:
        print(json.dumps(summary, indent=2))
    return EXIT[summary["verdict"]]


if __name__ == "__main__":
    raise SystemExit(main())
