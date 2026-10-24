#!/usr/bin/env python3
"""Breaks each guard of the dictation history in a copy of the Swift package,
one at a time, and requires the history tests to go red for every one.

    python3 macos/tools/history-mutations.py            # on a Mac with Swift
    python3 macos/tools/history-mutations.py --anchors  # anywhere: anchors only

A guard whose removal leaves `swift test --filter HistoryTests` green is
claimed, not tested. A mutation that does not compile is BROKEN, not killed,
and so is one whose anchor does not occur exactly once: both fail the run.
Several edits in one mutation remove guards that back each other up (the
history switch is checked where a dictation is saved and where its take is
read), so only removing both shows whether a test notices. The real sources
are never modified: the package is copied to a temporary folder without its
`.build`. `tests/test_history_mutation_anchors.py` checks the anchors on every
platform, so an edit that moves one turns the Python gate red.

Swift: $DICTATION_SWIFT, then `swift` on PATH.
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

MACOS = Path(__file__).resolve().parents[1]
FILTER = "HistoryTests"

SEAL = "Sources/DictationHistory/DictationHistorySeal.swift"
STORE = "Sources/DictationHistory/DictationHistoryStore.swift"
MODEL = "Sources/DictationAppCore/DictationHistoryModel.swift"
RECOVERY = "Sources/DictationAppCore/DictationModelRecovery.swift"
PIPELINE = "Sources/DictationAppCore/DictationModelPipeline.swift"
APP_MODEL = "Sources/DictationAppCore/DictationModel.swift"

# (name, [(file, anchor, replacement), ...])
MUTATIONS: list[tuple[str, list[tuple[str, str, str]]]] = [
    ("the dictation's id is not authenticated", [
        (SEAL, "authenticating: header + Data(id.uuidString.utf8))\n        guard let combined",
         "authenticating: header)\n        guard let combined"),
        (SEAL, "authenticating: Data(header) + Data(id.uuidString.utf8))", "authenticating: Data(header))"),
    ]),
    ("the kind is not checked", [
        (SEAL, "        guard header[9] == kind.rawValue else { throw OpenFailure.mismatched }\n", ""),
    ]),
    ("the key id is not checked", [
        (SEAL, "        guard header.subdata(in: 10..<26) == keyID(of: key) else { throw OpenFailure.otherKey }\n", ""),
    ]),
    ("files readable by others", [
        (STORE, "attributes: [.posixPermissions: 0o600]) else {", "attributes: [.posixPermissions: 0o644]) else {"),
    ]),
    ("folders open to others", [
        (STORE, "                try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder.path)\n",
         "                try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: folder.path)\n"),
    ]),
    ("Delete All keeps the key", [
        (STORE, "            try keys.destroy()\n", "            _ = try keys.existing()\n"),
    ]),
    ("words written in the clear", [
        (STORE, "        try write(sealed, to: entryURL(entry.id))\n",
         "        try write((try? encoder.encode(entry)) ?? sealed, to: entryURL(entry.id))\n"),
    ]),
    ("a recording written in the clear", [
        (STORE, "                try write(sealedAudio, to: audioURL(entry.id))\n",
         "                try write(recording, to: audioURL(entry.id))\n"),
    ]),
    ("recordings never age out", [
        (STORE, "        if let age = retention.recordings {", "        if let age = retention.recordings, age < 0 {"),
    ]),
    ("deleting a dictation leaves its recording", [
        (STORE, "        snapshot = current\n        try removeOrThrow(audioURL(id))\n", "        snapshot = current\n"),
    ]),
    ("a delete that fails part-way keeps listing the dictation", [
        (STORE, "        current.entries.removeAll { $0.id == id }\n        snapshot = current\n",
         "        current.entries.removeAll { $0.id == id }\n"),
    ]),
    ("a retention pass that fails part-way keeps its list", [
        (STORE, "            snapshot = nil\n            recordingSizes = [:]\n            throw error\n", "            throw error\n"),
    ]),
    ("a refused keychain reads as an empty history", [
        (STORE, "            key = try keys.existing()\n        } catch {\n            refusal = error\n            throw .locked(error)\n        }",
         "            key = try keys.existing()\n        } catch {\n            return nil\n        }"),
    ]),
    ("a refused keychain is asked again by the next save", [
        (STORE, "        if let refusal { throw .locked(refusal) }\n", ""),
    ]),
    ("a newer version's dictation is offered for deletion", [
        (STORE, "                newer.insert(id)\n", "                unreadable.append(file)\n"),
    ]),
    ("a newer version's recording is swept as an orphan", [
        (STORE, ".union(newer)", ""),
    ]),
    ("a file that cannot be opened just now is counted unreadable", [
        (STORE, "it is left alone\")\n                continue",
         "it is left alone\")\n                unreadable.append(file)\n                continue"),
    ]),
    ("a password-field or Secure Input refusal is saved", [
        (PIPELINE, "            if !secure {\n", "            if true {\n"),
    ]),
    ("the history switch is ignored", [
        (MODEL, "recording: Data?) async {\n        guard enabled, let store else { return }",
         "recording: Data?) async {\n        guard let store else { return }"),
        (RECOVERY, "        guard history.enabled, let text, !text", "        guard let text, !text"),
        (RECOVERY, "        guard history.enabled, history.isAvailable, let live", "        guard history.isAvailable, let live"),
    ]),
    ("a dictation into Dictation's own window is saved", [
        (RECOVERY, "        if let app, app == env.ownBundleID { return false }\n", ""),
    ]),
    ("dates written with seconds since 1970", [
        (STORE, "        let encoder = JSONEncoder()\n",
         "        let encoder = JSONEncoder()\n        encoder.dateEncodingStrategy = .secondsSince1970\n"),
        (STORE, "        let decoder = JSONDecoder()\n",
         "        let decoder = JSONDecoder()\n        decoder.dateDecodingStrategy = .secondsSince1970\n"),
    ]),
    ("quitting does not wait for a save", [
        (APP_MODEL, "work: { await history.flush(); return true }", "work: { _ = history; return true }"),
    ]),
    ("recordings kept although they are off", [
        (MODEL, "recording: keepsRecordings ? recording : nil)", "recording: recording)"),
        (RECOVERY, "        if history.keepsRecordings, !take.failed, take.captured > 0 {",
         "        if !take.failed, take.captured > 0 {"),
    ]),
]


def anchor_problems(root: Path = MACOS) -> list[str]:
    """Each mutation that would be BROKEN before Swift runs: an anchor that
    does not occur exactly once, or a replacement equal to its anchor."""
    problems = []
    names = [name for name, _ in MUTATIONS]
    problems += [f"{name}: named twice" for name in sorted({n for n in names if names.count(n) > 1})]
    for name, edits in MUTATIONS:
        for file, anchor, replacement in edits:
            path = root / file
            if not path.is_file():
                problems.append(f"{name}: no {file}")
                continue
            hits = path.read_text(encoding="utf-8").count(anchor)
            if hits != 1:
                problems.append(f"{name}: the anchor occurs {hits} times in {file}")
            if anchor == replacement:
                problems.append(f"{name}: the replacement is the anchor")
    return problems


def swift() -> str:
    return os.environ.get("DICTATION_SWIFT") or shutil.which("swift") or "swift"


def run(package: Path, *arguments: str) -> subprocess.CompletedProcess[str]:
    env = dict(os.environ, TMPDIR="/tmp")
    return subprocess.run([swift(), *arguments], cwd=package, env=env, capture_output=True, text=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--anchors", action="store_true", help="check the anchors only (no Swift)")
    args = parser.parse_args()
    problems = anchor_problems()
    for problem in problems:
        print(f"BROKEN    {problem}")
    if args.anchors or problems:
        return 1 if problems else 0

    with tempfile.TemporaryDirectory(prefix="dictation-history-mutations-") as scratch:
        package = Path(scratch) / "macos"
        shutil.copytree(MACOS, package, ignore=shutil.ignore_patterns(".build", ".swiftpm"))
        baseline = run(package, "test", "--build-system", "native", "--filter", FILTER)
        if baseline.returncode != 0:
            print("baseline is red; fix the tests before measuring mutations", file=sys.stderr)
            print(baseline.stdout[-2000:] + baseline.stderr[-2000:], file=sys.stderr)
            return 1
        killed = 0
        for name, edits in MUTATIONS:
            originals = {}
            for file, anchor, replacement in edits:
                path = package / file
                originals.setdefault(path, path.read_text(encoding="utf-8"))
                current = path.read_text(encoding="utf-8")
                path.write_text(current.replace(anchor, replacement, 1), encoding="utf-8")
            try:
                build = run(package, "build", "--build-system", "native", "--build-tests")
                if build.returncode != 0:
                    print(f"BROKEN    {name}: the mutation does not compile")
                    continue
                tests = run(package, "test", "--build-system", "native", "--skip-build", "--filter", FILTER)
                if tests.returncode != 0:
                    killed += 1
                    print(f"KILLED    {name}")
                else:
                    print(f"SURVIVED  {name}")
            finally:
                for path, text in originals.items():
                    path.write_text(text, encoding="utf-8")
        print(f"mutations: {killed} of {len(MUTATIONS)} killed")
        return 0 if killed == len(MUTATIONS) else 1


if __name__ == "__main__":
    sys.exit(main())
