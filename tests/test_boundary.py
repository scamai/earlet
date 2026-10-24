"""tools/verify.py must report each way the standalone boundary has been, or
could be, broken.

Every case edits one file in a copy of the tree the way an import or a
careless change would, and the check must name the problem; the untouched copy
must pass. A case whose anchor no longer matches fails instead of passing
quietly: fix the anchor, do not delete the case.
"""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys

import pytest

import harness
from harness import ROOT, tool

# A standalone checkout has its own .git directory at ROOT. Copy source only:
# a fixture that inherits Git metadata no longer exercises missing history.
BUILD_DIRS = ("bin", "obj", ".build", "dist", "__pycache__", ".pytest_cache",
              ".git", ".venv", "evidence")
APPEND = None  # the case appends its text instead of replacing an anchor

CSPROJ = "windows/Dictation.App/Dictation.App.csproj"
TEST_CSPROJ = "windows/Dictation.Services.Tests/Dictation.Services.Tests.csproj"
SOME_CS = "windows/Dictation.Core/DictationEnvironment.cs"
SOME_SWIFT = "macos/Sources/DictationIPC/PythonEngineClient.swift"
SHIPPED_PY = "runtime/transcription_worker/spoken_aliases.py"
HOLD_KEY_SWIFT = "macos/Sources/DictationAppCore/DictationHoldKey.swift"


def project_item(item):
    return (CSPROJ, "</Project>", f"  <ItemGroup>\n    {item}\n  </ItemGroup>\n</Project>")


CASES = {'ProjectReference into other product': ('windows/Dictation.App/Dictation.App.csproj',
                                         '../Dictation.Infrastructure/Dictation.Infrastructure.csproj',
                                         '../../../other-product/native/OtherProduct/OtherProduct.Infrastructure.Dictation/OtherProduct.Infrastructure.Dictation.csproj',
                                         'Unclosed dependency'),
 'Content into other product': ('windows/Dictation.App/Dictation.App.csproj',
                                '</Project>',
                                '  <ItemGroup>\n'
                                '    <Content Include="../../../other-product/native/models/catalog.json" />\n'
                                '  </ItemGroup>\n'
                                '</Project>',
                                'Unclosed dependency'),
 'None into other product': ('windows/Dictation.App/Dictation.App.csproj',
                             '</Project>',
                             '  <ItemGroup>\n'
                             '    <None Include="..\\..\\..\\other-product\\native\\README.md" />\n'
                             '  </ItemGroup>\n'
                             '</Project>',
                             'Unclosed dependency'),
 'Import of other product props': ('windows/Dictation.App/Dictation.App.csproj',
                                   '</Project>',
                                   '  <ItemGroup>\n'
                                   '    <Import '
                                   'Project="../../../other-product/native/OtherProduct/Directory.Build.props" '
                                   '/>\n'
                                   '  </ItemGroup>\n'
                                   '</Project>',
                                   'Unclosed dependency'),
 'props Import via MSBuildThisFileDirectory': ('windows/Directory.Build.props',
                                               '<Project>',
                                               '<Project>\n'
                                               '  <Import '
                                               'Project="$(MSBuildThisFileDirectory)../../other-product/native/OtherProduct/Directory.Build.props" '
                                               '/>',
                                               'Unclosed dependency'),
 'OutputPath outside': ('windows/Dictation.App/Dictation.App.csproj',
                        '</Project>',
                        '  <ItemGroup>\n'
                        '    '
                        '</ItemGroup><PropertyGroup><OutputPath>..\\..\\..\\other-product\\bin</OutputPath></PropertyGroup><ItemGroup>\n'
                        '  </ItemGroup>\n'
                        '</Project>',
                        'Unclosed dependency'),
 'test project fixture glob into other product': ('windows/Dictation.Services.Tests/Dictation.Services.Tests.csproj',
                                                  '</Project>',
                                                  '  <ItemGroup>\n'
                                                  '    <None '
                                                  'Include="..\\..\\..\\other-product\\fixtures\\**" />\n'
                                                  '  </ItemGroup>\n'
                                                  '</Project>',
                                                  'Unclosed dependency'),
 'Swift target path into other product': ('macos/Package.swift',
                                          '.target(name: "DictationIPC")',
                                          '.target(name: "DictationIPC", path: '
                                          '"../../other-product/native/macos/app/Sources/DictationIPC")',
                                          'Unclosed dependency'),
 'Swift package dependency': ('macos/Package.swift',
                              'platforms: [.macOS(.v14)],',
                              'platforms: [.macOS(.v14)],\n'
                              '    dependencies: [.package(path: "../../other-product/native/macos/app")],',
                              'external package'),
 'worker parents above the app': ('runtime/transcription_worker/spoken_aliases.py',
                                  None,
                                  'sys.path.insert(0, str(Path(__file__).resolve().parents[3] / '
                                  '"other-product"))\n',
                                  'Unclosed dependency'),
 'worker sys.path into other product': ('runtime/transcription_worker/spoken_aliases.py',
                                        None,
                                        'sys.path.insert(0, "../../other-product/native")\n',
                                        'Unclosed dependency'),
 'Linux home path': ('runtime/transcription_worker/spoken_aliases.py',
                     None,
                     'MODEL = "/home/builder/hf_downloads/whisper"\n',
                     'builder path'),
 'macOS home path': ('macos/Sources/DictationIPC/PythonEngineClient.swift',
                     None,
                     'let model = "/Users/someone/models/whisper"\n',
                     'builder path'),
 'Windows home path': ('windows/Dictation.Core/DictationEnvironment.cs',
                       None,
                       'static class M { const string P = @"C:\\Users\\someone\\Downloads"; }\n',
                       'builder path'),
 'worker append outside': ('runtime/transcription_worker/spoken_aliases.py',
                           None,
                           'sys.path.append("../../other-product/native")\n',
                           'Unclosed dependency'),
 'worker extend outside': ('runtime/transcription_worker/spoken_aliases.py',
                           None,
                           'sys.path.extend(["../../other-product/native"])\n',
                           'Unclosed dependency'),
 'worker absolute import outside': ('runtime/transcription_worker/spoken_aliases.py',
                                    None,
                                    'sys.path.insert(0, "/opt/other-product")\n',
                                    'Unclosed dependency')}

# Things that must stay quiet: prose, tests and code the package does not ship.
CONTROLS = {'comment citing a builder path': ('windows/Dictation.Core/DictationEnvironment.cs',
                                   None,
                                   '// Example build path: /home/builder/models.\n'),
 'Python docstring citing a builder path': ('runtime/transcription_worker/spoken_aliases.py',
                                            None,
                                            'def origin():\n'
                                            '    """Example build path: /home/builder/models."""\n'),
 'test file using a builder path': ('windows/Dictation.Services.Tests/HoldKeyStateTests.cs',
                                    None,
                                    'static class T { const string P = "/home/builder/models"; }\n'),
 'unshipped worker module': ('runtime/transcription_worker/conftest.py',
                             None,
                             'BENCH = "/home/someone/bench"\n'),
 'a contained sibling folder': ('windows/Dictation.App/Dictation.App.csproj',
                                '</Project>',
                                '  <ItemGroup>\n'
                                '    <None Include="../OtherProduct.Services/notes.txt" />\n'
                                '  </ItemGroup>\n'
                                '</Project>'),
 'worker contained import path': ('runtime/transcription_worker/spoken_aliases.py',
                                  None,
                                  'sys.path.append("./helpers")\n')}

@pytest.fixture(scope="module")
def tree(tmp_path_factory):
    copy = tmp_path_factory.mktemp("boundary") / "dictation"
    shutil.copytree(ROOT, copy, ignore=shutil.ignore_patterns(*BUILD_DIRS))
    return copy


def check_with(tree, relative, anchor, text):
    path = tree / relative
    original = path.read_text(encoding="utf-8")
    if anchor is APPEND:
        mutated = original + ("" if original.endswith("\n") else "\n") + text
    else:
        assert anchor in original, f"anchor gone from {relative}: update this case"
        mutated = original.replace(anchor, text)
    try:
        path.write_text(mutated, encoding="utf-8")
        return tool("verify").check(tree)
    finally:
        path.write_text(original, encoding="utf-8")


def test_the_untouched_tree_passes(tree):
    assert tool("verify").check(tree) == []


@pytest.mark.parametrize("case", list(CASES))
def test_a_boundary_break_is_reported(tree, case):
    relative, anchor, text, expected = CASES[case]
    errors = check_with(tree, relative, anchor, text)
    assert any(expected in error for error in errors), errors


@pytest.mark.parametrize("copies", [0, 2], ids=["exempt site removed", "new site in an exempt file"])
def test_reviewed_exemptions_detect_drift(tree, copies):
    # Exercise drift with a planted exception: the production tree has none.
    verify = tool("verify")
    line = 'let fixture = "/home/builder/fixture"\n'
    verify.EXEMPTIONS = ((SOME_SWIFT, '/home/builder/fixture', 1, "test fixture"),)
    path = tree / SOME_SWIFT
    original = path.read_text(encoding="utf-8")
    try:
        path.write_text(original + "\n" + line, encoding="utf-8")
        assert verify.check(tree) == []
        path.write_text(original + "\n" + line * copies, encoding="utf-8")
        assert any("Exemption drift" in error for error in verify.check(tree))
    finally:
        path.write_text(original, encoding="utf-8")


@pytest.mark.parametrize("control", list(CONTROLS))
def test_prose_tests_and_unshipped_code_stay_quiet(tree, control):
    assert check_with(tree, *CONTROLS[control]) == []


# ─── An exported copy, and a monorepo tree that lost its manifest ────────────

@pytest.fixture
def set_aside(tree, tmp_path):
    """Move tree paths out of the shared tree for one test, and back afterwards."""
    moved = []

    def move(*names):
        for name in names:
            if (tree / name).exists():
                destination = tmp_path / "aside" / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.move(str(tree / name), str(destination))
                moved.append((destination, tree / name))

    yield move
    for destination, original in reversed(moved):
        shutil.move(str(destination), str(original))


@pytest.fixture
def plant(tree):
    """Create a monorepo-only marker the tree lacks, for one test, and remove it afterwards. An exported
    copy has none of them, and the monorepo's own case has to run there too."""
    made = []

    def make(name):
        path = tree / name
        if not path.exists():
            path.write_text("", encoding="utf-8")  # verify.py asks only whether the name exists
            made.append(path)

    yield make
    for path in reversed(made):
        path.unlink()


def test_an_exported_copy_without_the_manifest_checks_the_boundary_and_says_provenance_was_skipped(tree, set_aside):
    """The public tree carries no import-manifest.json, and none of the monorepo-only files
    (tools/oss-export/dictation at the monorepo root leaves them all out). Its boundary suite still runs
    every boundary rule, names the skipped half, and --snapshot fails instead of passing without its
    manifest."""
    set_aside("import-manifest.json", *tool("verify").MONOREPO_ONLY)
    assert tool("verify").check(tree) == []
    relative, anchor, text, expected = CASES["Linux home path"]
    assert any(expected in error for error in check_with(tree, relative, anchor, text))
    assert tool("verify").check(tree, snapshot=True) == [
        "Provenance not checked: import-manifest.json is not in this tree (an exported copy)"]
    run = subprocess.run([sys.executable, str(tree / "tools/verify.py")], capture_output=True, text=True)
    assert (run.returncode, run.stdout.splitlines()[-1]) == (0, (
        "PASS: standalone source boundary; provenance not checked: no import-manifest.json in this tree "
        "(an exported copy)")), run.stdout + run.stderr


@pytest.mark.parametrize("marker", ["VERIFY.md", ".Codex"])
def test_a_monorepo_tree_that_lost_its_manifest_fails_rather_than_pass_as_an_export(tree, set_aside, plant, marker):
    """Absence of the manifest means an exported copy only where the monorepo-only files are gone too.
    In the monorepo the boundary suite, which gates, must go red, and so must --snapshot. In an exported
    copy, which has no marker, one is planted, so this case runs in both trees instead of failing there."""
    assert marker in tool("verify").MONOREPO_ONLY
    plant(marker)
    set_aside("import-manifest.json", *(name for name in tool("verify").MONOREPO_ONLY if name != marker))
    expected = (f"Missing import-manifest.json: this is the monorepo tree ({marker} is here), where provenance "
                "is checked; only an exported copy goes without it")
    assert tool("verify").check(tree) == [expected]
    assert tool("verify").check(tree, snapshot=True) == [expected]
    run = subprocess.run([sys.executable, str(tree / "tools/verify.py")], capture_output=True, text=True)
    assert (run.returncode, run.stdout.splitlines()) == (1, [expected, "FAIL: standalone source boundary"]), \
        run.stdout + run.stderr


# ─── The removed-ledger ──────────────────────────────────────────────────────

# An imported file every planned cleanup keeps (docs/architecture.md, "What
# ships beyond the entry points"): the cases delete it from the copy.
KEPT_IMPORT = "windows/Dictation.Services/HoldKeyState.cs"


def ledger(destination=KEPT_IMPORT, **fields):
    return {"destination": destination, "reason": "unused code", "commit": "a" * 40, **fields}


def planted_manifest(tree):
    """A manifest naming KEPT_IMPORT alone, for an exported copy, which carries none (the export leaves it
    out). With it there the ledger cases run in both trees instead of failing in the public one."""
    digest = hashlib.sha256((tree / KEPT_IMPORT).read_bytes()).hexdigest()
    return {"source_commit": "0" * 40, "files": [
        {"source": KEPT_IMPORT, "destination": KEPT_IMPORT, "source_sha256": digest, "destination_sha256": digest}]}


def check_ledger(tree, removed, delete=()):
    """The check on the copy with these removed-ledger entries added and these
    files deleted; both are put back, and a planted manifest is taken away."""
    manifest_path = tree / "import-manifest.json"
    original = manifest_path.read_text(encoding="utf-8") if manifest_path.is_file() else None
    manifest = json.loads(original) if original is not None else planted_manifest(tree)
    assert KEPT_IMPORT in {entry["destination"] for entry in manifest["files"]} and (tree / KEPT_IMPORT).is_file(), \
        "KEPT_IMPORT is no longer a present imported file: update it"
    saved = {relative: (tree / relative).read_bytes() for relative in delete}
    try:
        manifest["removed"] = manifest.get("removed", []) + removed
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        for relative in delete:
            (tree / relative).unlink()
        return tool("verify").check(tree)
    finally:
        if original is None:
            manifest_path.unlink()
        else:
            manifest_path.write_text(original, encoding="utf-8")
        for relative, data in saved.items():
            (tree / relative).write_bytes(data)


def test_a_ledger_case_puts_the_shared_tree_back_as_the_source_has_it(tree):
    """The copy is shared by the module, so after a case its manifest (or, in an exported copy, the lack
    of one) and the deleted file match the source tree, whatever ran before."""
    check_ledger(tree, [ledger()], delete=[KEPT_IMPORT])

    def manifest_text(root):
        path = root / "import-manifest.json"
        return path.read_text(encoding="utf-8") if path.is_file() else None

    assert manifest_text(tree) == manifest_text(ROOT)
    assert (tree / KEPT_IMPORT).read_bytes() == (ROOT / KEPT_IMPORT).read_bytes()


def test_a_deleted_import_the_ledger_does_not_name_is_missing(tree):
    assert check_ledger(tree, [], delete=[KEPT_IMPORT]) == [f"Missing import: {KEPT_IMPORT}"]


def test_a_deletion_the_ledger_names_passes(tree):
    assert check_ledger(tree, [ledger()], delete=[KEPT_IMPORT]) == []


def test_a_removed_import_that_comes_back_fails(tree):
    errors = check_ledger(tree, [ledger()])
    assert len(errors) == 1 and errors[0].startswith(f"Removed import is back: {KEPT_IMPORT} "), errors


LEDGER_FAULTS = {
    "names no imported file": ([ledger("windows/Dictation.Core/DictationPaths.cs")],
                               "Removed entry names no imported file: windows/Dictation.Core/DictationPaths.cs"),
    "no reason": ([ledger(reason=" ")], "Removed entry needs a reason and the full commit"),
    "no commit": ([ledger(commit=None)], "Removed entry needs a reason and the full commit"),
    "abbreviated commit": ([ledger(commit="e218f66dd")], "Removed entry needs a reason and the full commit"),
    "named twice": ([ledger(), ledger()], f"Removed twice: {KEPT_IMPORT}"),
}


@pytest.mark.parametrize("fault", list(LEDGER_FAULTS))
def test_a_ledger_entry_names_an_import_says_why_and_where_its_last_copy_is(tree, fault):
    removed, expected = LEDGER_FAULTS[fault]
    errors = check_ledger(tree, removed, delete=[KEPT_IMPORT])
    assert any(error.startswith(expected) for error in errors), errors


def test_every_real_removed_entry_names_a_commit_that_holds_the_file():
    """The real ledger, read through git in the gate. --snapshot reads the same
    last copies but never gates, and a full hash of the wrong commit is well
    formed, so without this a ledger entry could point nowhere unnoticed. A
    shallow clone (hosted CI checks out one commit) or a copy cannot answer, and
    an exported copy has no ledger to read."""
    if tool("verify").exported_copy(ROOT):
        pytest.skip("an exported copy has no import-manifest.json; the monorepo's gate reads the real ledger")
    manifest = json.loads((ROOT / "import-manifest.json").read_text(encoding="utf-8"))
    assert manifest.get("removed"), "the removed list is empty, so this test checks nothing"
    shallow = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--is-shallow-repository"],
                             capture_output=True, text=True)
    if shallow.returncode or shallow.stdout.strip() != "false":
        pytest.skip("needs the full git history (a shallow clone or a copy has none)")
    assert tool("verify").removed_errors(ROOT, manifest) == []


# ─── --snapshot ──────────────────────────────────────────────────────────────

def test_a_snapshot_without_the_pinned_history_fails_rather_than_skip(tree):
    if tool("verify").exported_copy(ROOT):
        pytest.skip("an exported copy has no import-manifest.json; the case above covers its --snapshot")
    errors = tool("verify").check(tree, snapshot=True)
    assert any("Source hashes not checked" in error for error in errors), errors


def git(repository, *arguments):
    return subprocess.run(["git", "-C", str(repository), "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                           *arguments], capture_output=True, text=True, check=True).stdout.strip()


def make_pinned(tmp_path):
    """A repository whose history holds the source, and an app beside it whose
    manifest pins that commit."""
    repository = tmp_path / "repository"
    (repository / "upstream").mkdir(parents=True)
    source = b"print('from upstream')\n"
    (repository / "upstream/worker.py").write_bytes(source)
    git(repository, "init", "-q")
    git(repository, "add", "upstream")
    git(repository, "commit", "-qm", "upstream")
    app = repository / "app"
    app.mkdir()
    (app / "worker.py").write_bytes(source)
    digest = hashlib.sha256(source).hexdigest()
    manifest = {"source_commit": git(repository, "rev-parse", "HEAD"), "files": [
        {"source": "upstream/worker.py", "destination": "worker.py", "source_sha256": digest,
         "destination_sha256": digest}]}
    (app / "import-manifest.json").write_text(json.dumps(manifest))
    return app, manifest


@pytest.fixture
def pinned(tmp_path):
    return make_pinned(tmp_path)


def test_source_hashes_are_read_at_the_pinned_commit(pinned):
    app, manifest = pinned
    assert tool("verify").source_errors(app, manifest) == []
    changed = dict(manifest, files=[dict(manifest["files"][0], source_sha256="0" * 64)])
    assert tool("verify").source_errors(app, changed) == [
        f"Source differs from the manifest at {manifest['source_commit'][:12]}: upstream/worker.py"]
    missing = dict(manifest, files=[dict(manifest["files"][0], source="upstream/gone.py")])
    assert tool("verify").source_errors(app, missing)[0].startswith("Missing at")


def test_the_snapshot_entry_point_runs_the_source_check(pinned, monkeypatch):
    app, manifest = pinned
    verify = tool("verify")
    seen = []
    monkeypatch.setattr(verify, "source_errors", lambda root, data: seen.append(root) or ["sentinel"])
    monkeypatch.setattr(verify, "removed_errors", lambda root, data: ["removed sentinel"])
    for step in ("project_errors", "package_errors", "worker_path_errors", "leak_errors"):
        monkeypatch.setattr(verify, step, lambda root: [])
    assert verify.check(app, snapshot=True) == ["sentinel", "removed sentinel"] and seen == [app.resolve()]
    assert verify.check(app) == []


def test_a_removed_file_is_read_at_the_commit_its_entry_names(pinned):
    app, manifest = pinned
    git(app.parent, "add", "app")
    git(app.parent, "commit", "-qm", "app")
    held = git(app.parent, "rev-parse", "HEAD")
    (app / "worker.py").unlink()
    git(app.parent, "commit", "-qam", "remove the worker")
    gone = git(app.parent, "rev-parse", "HEAD")
    verify = tool("verify")
    entry = {"destination": "worker.py", "reason": "orphan", "commit": held}
    assert verify.import_errors(app.resolve(), dict(manifest, removed=[entry]), snapshot=True) == []
    assert verify.removed_errors(app, dict(manifest, removed=[entry])) == []
    assert verify.removed_errors(app, dict(manifest, removed=[dict(entry, commit=gone)])) == [
        f"Removed file not at {gone[:12]}: worker.py"]


def test_a_removed_file_without_the_history_fails_rather_than_skip(tree):
    assert tool("verify").removed_errors(tree, {"removed": [ledger()]}) == [
        f"Removed files not checked: no git history at {tree}"]


def test_a_ledger_commit_the_history_lacks_is_one_line_not_one_per_file(pinned):
    """A shallow clone holds only its tip: say that once for the commit, and
    still read the entries whose commit is there."""
    app, manifest = pinned
    git(app.parent, "add", "app")
    git(app.parent, "commit", "-qm", "app")
    held, absent = git(app.parent, "rev-parse", "HEAD"), "b" * 40
    entries = [{"destination": "worker.py", "reason": "orphan", "commit": held},
               {"destination": "a.py", "reason": "orphan", "commit": absent},
               {"destination": "b.py", "reason": "orphan", "commit": absent},
               {"destination": "never.py", "reason": "orphan", "commit": held}]
    assert tool("verify").removed_errors(app, dict(manifest, removed=entries)) == [
        f"Removed files not checked: no commit {absent[:12]} in the git history at {app}",
        f"Removed file not at {held[:12]}: never.py"]


def test_the_snapshot_compares_each_present_import_with_its_extracted_hash(pinned, monkeypatch):
    app, manifest = pinned
    verify = tool("verify")
    for step in ("project_errors", "package_errors", "worker_path_errors", "leak_errors"):
        monkeypatch.setattr(verify, step, lambda root: [])
    assert verify.check(app, snapshot=True) == []
    (app / "worker.py").write_text("print('edited after extraction')\n")
    assert verify.check(app) == []
    assert verify.check(app, snapshot=True) == ["Changed since extraction: worker.py"]


# ─── Deleting a ledger guard fails its test ──────────────────────────────────

def ledger_guard(guard, anchor, replacement, claim):
    return pytest.param(anchor, replacement, claim, id=guard)


LEDGER_GUARDS = [
    ledger_guard("ledgered deletion skipped", '        if entry["destination"] in removed:\n',
                 "        if False:\n", lambda tree, tmp_path, mp: test_a_deletion_the_ledger_names_passes(tree)),
    ledger_guard("restored file refused", "            if inside and path.exists():", "            if False:",
                 lambda tree, tmp_path, mp: test_a_removed_import_that_comes_back_fails(tree)),
    ledger_guard("entry names an import", "        if destination not in imported:", "        if False:",
                 lambda tree, tmp_path, mp: test_a_ledger_entry_names_an_import_says_why_and_where_its_last_copy_is(
                     tree, "names no imported file")),
    ledger_guard("entry says why", 'if not str(entry.get("reason") or "").strip() or ', "if ",
                 lambda tree, tmp_path, mp: test_a_ledger_entry_names_an_import_says_why_and_where_its_last_copy_is(
                     tree, "no reason")),
    ledger_guard("entry names a full commit", ' or not COMMIT.fullmatch(str(entry.get("commit") or "")):', ":",
                 lambda tree, tmp_path, mp: test_a_ledger_entry_names_an_import_says_why_and_where_its_last_copy_is(
                     tree, "abbreviated commit")),
    ledger_guard("entry named once", "count > 1]", "count > 2]",
                 lambda tree, tmp_path, mp: test_a_ledger_entry_names_an_import_says_why_and_where_its_last_copy_is(
                     tree, "named twice")),
    ledger_guard("--snapshot reads the last copies", " + removed_errors(root, manifest)", "",
                 lambda tree, tmp_path, mp: test_the_snapshot_entry_point_runs_the_source_check(
                     make_pinned(tmp_path), mp)),
    ledger_guard("a missing last copy fails", 'if answer.split()[1:2] != ["blob"]]', "if False]",
                 lambda tree, tmp_path, mp: test_a_removed_file_is_read_at_the_commit_its_entry_names(
                     make_pinned(tmp_path))),
    ledger_guard("an absent commit is one line", 'if answer.split()[1:2] != ["commit"]}', "if False}",
                 lambda tree, tmp_path, mp: test_a_ledger_commit_the_history_lacks_is_one_line_not_one_per_file(
                     make_pinned(tmp_path))),
    ledger_guard("--snapshot reaches the extracted hashes", "import_errors(root, manifest, snapshot)",
                 "import_errors(root, manifest)",
                 lambda tree, tmp_path, mp: test_the_snapshot_compares_each_present_import_with_its_extracted_hash(
                     make_pinned(tmp_path), mp)),
    ledger_guard("a changed import is reported", "elif snapshot and hashlib", "elif False and hashlib",
                 lambda tree, tmp_path, mp: test_the_snapshot_compares_each_present_import_with_its_extracted_hash(
                     make_pinned(tmp_path), mp)),
]


@pytest.mark.parametrize("anchor, replacement, claim", LEDGER_GUARDS)
def test_deleting_a_ledger_guard_fails_its_test(tree, tmp_path, monkeypatch, anchor, replacement, claim):
    source = (ROOT / "tools/verify.py").read_text(encoding="utf-8")
    assert source.count(anchor) == 1, "the guard moved: update this row"
    claim(tree, tmp_path / "control", monkeypatch)  # the claim passes against the real check ...
    monkeypatch.setitem(harness.OVERRIDES, "verify", source.replace(anchor, replacement))
    tool("verify")  # ... the mutant itself loads, or any failure below proves nothing ...
    with pytest.raises((Exception, pytest.fail.Exception)):  # ... and the claim fails against it
        claim(tree, tmp_path / "mutant", monkeypatch)
