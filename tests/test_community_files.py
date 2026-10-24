"""The licensing and community files this directory is published with.

This directory is published as a repository of its own, and these files are
its front door: LICENSE, NOTICE and REUSE.toml say on what terms the code is
offered; CONTRIBUTING, CODE_OF_CONDUCT, SECURITY, SUPPORT, CHANGELOG and
ROADMAP are what a visitor reads first; .github/ holds the issue forms, the
pull request template, CODEOWNERS and dependabot.yml, which do nothing here
and become the public repository's own .github/ once published.

Each check is a function over text or a small tree, run twice: on the real
files, and on a planted defect it has to catch (the `*_fires` tests), so a
check that could never fail shows up here. YAML is read by line, because the
gate installs no YAML parser; the shapes it reads are the ones these files
use.
"""
from __future__ import annotations

import hashlib
from pathlib import Path
import re
import shutil
import tomllib
import xml.etree.ElementTree as ET

import pytest

from harness import ROOT

COMMUNITY = ("LICENSE", "LICENSES/Apache-2.0.txt", "LICENSES/LicenseRef-Public-Domain.txt", "NOTICE",
             "REUSE.toml", "CONTRIBUTING.md", "CODE_OF_CONDUCT.md", "SECURITY.md", "SUPPORT.md", "CHANGELOG.md",
             "ROADMAP.md", ".github/ISSUE_TEMPLATE/bug_report.yml", ".github/ISSUE_TEMPLATE/recognition_report.yml",
             ".github/ISSUE_TEMPLATE/config.yml", ".github/PULL_REQUEST_TEMPLATE.md", ".github/CODEOWNERS",
             ".github/dependabot.yml")
TEXTS = tuple(name for name in COMMUNITY if not name.startswith(("LICENSE", "NOTICE")))

# https://www.apache.org/licenses/LICENSE-2.0.txt, fetched 2026-10-10 (the same
# bytes as Debian's /usr/share/common-licenses/Apache-2.0).
APACHE_SHA256 = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30"
# Contributor Covenant 2.1: content/version/2/1/code_of_conduct.md on the
# release branch of github.com/EthicalSource/contributor_covenant, from its
# first heading on (the Hugo front matter dropped), fetched 2026-10-10.
COVENANT_SHA256 = "369bf7301883368fc19203bd0f1233fed2b83f0378ad19c4d0708bf61925339b"
COVENANT_PLACEHOLDER = "[INSERT CONTACT METHOD]"
COPYRIGHT = "2026 Reality Inc."
BSD_DICT_QUOTE = "The 1934 copyright has lapsed, according to the supplier."

# Paths the publication leaves out (open-source plan, step 5). A public file
# that links into one of them has a dead link once published.
NOT_PUBLISHED = ("VERIFY.md", "BRAINSTORM.md", "TODO.md", "docs/history.md", "docs/internal/", ".Codex/",
                 "import-manifest.json", "evidence/")
# Reviewed public team mailbox for conduct reports; never a personal address.
ROLE_ADDRESSES: tuple[str, ...] = ("support@scam.ai",)
HTML_TAGS = {"details", "summary", "br", "kbd", "picture", "source", "img", "sub", "sup", "p", "a"}
# Network claims are withheld until traffic is measured: the usual ways of
# saying that nothing goes over the network, leaves the computer, reaches a
# cloud or needs the internet, or that the apps are offline or local only.
_DEVICE = r"(?:your|the|this) (?:device|computer|mac|pc|machine|laptop)\b"
NETWORK_CLAIMS = re.compile(
    r"\bno network\b|\bwithout (?:an? |any )?(?:network|internet)\b|\bno internet\b"
    r"|(?:\bnever|\bnot|n't) (?:ever )?(?:connects?|contacts?|talks? to|reach(?:es)?|phones? home|goes? online)\b"
    rf"|(?:\bnever|\bnot|n't) (?:ever )?leaves? {_DEVICE}|\bnothing (?:ever )?leaves\b"
    r"|\bnothing (?:ever )?is (?:sent|uploaded|shared|transmitted)\b"
    r"|(?:\bnever|\bnot|\bno \w+ (?:is|are)) (?:\w+ )?(?:sent|uploaded|transmitted)\b"
    rf"|\bstays? (?:on|in|inside) {_DEVICE}|\bstays? local\b"
    r"|\b(?:to|in|into|via|through) the cloud\b|\bno cloud\b|\bcloud[- ]free\b"
    r"|(?:\b(?:fully|completely|entirely|purely|strictly|only)|(?<![\w%])100%) (?:local(?:ly)?|on[- ]device|offline)\b"
    r"|\b(?:local(?:ly)?|on[- ]device|offline)[- ]only\b|\bworks offline\b", re.I)


def read(name: str, root: Path = ROOT) -> str:
    return (root / name).read_text(encoding="utf-8")


# ─── Checks ──────────────────────────────────────────────────────────────────

def slug(heading: str) -> str:
    """GitHub's anchor for a Markdown heading."""
    heading = re.sub(r"`([^`]*)`", r"\1", heading)
    heading = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", heading)
    return re.sub(r"[^\w\- ]", "", heading.strip().lower()).replace(" ", "-")


def anchors(text: str) -> set[str]:
    found, seen, fenced = set(), {}, False
    for line in text.splitlines():
        if line.lstrip().startswith("```"):
            fenced = not fenced
        match = None if fenced else re.match(r"^#{1,6}\s+(.*?)\s*#*\s*$", line)
        if match:
            base = slug(match.group(1))
            found.add(base if base not in seen else f"{base}-{seen[base]}")
            seen[base] = seen.get(base, 0) + 1
    return found


def link_errors(relative: str, text: str, root: Path = ROOT) -> list[str]:
    """Relative Markdown links that do not resolve, or that point at a path
    the publication leaves out."""
    errors, document = [], root / relative
    prose = re.sub(r"```.*?```", "", text, flags=re.S)
    prose = re.sub(r"`[^`\n]*`", "", prose)
    targets = re.findall(r"(?<!!)\[[^\]]*\]\(([^)\s]+)\)", prose)
    targets += re.findall(r"^ {0,3}\[[^\]]+\]:\s*<?([^>\s]+)", prose, flags=re.M)  # [label]: target
    for target in targets:
        if re.match(r"^[a-z][a-z+.-]*:", target):
            continue
        path, _, fragment = target.partition("#")
        destination = (document.parent / path).resolve() if path else document
        inside = destination.relative_to(root.resolve()).as_posix() if destination.is_relative_to(
            root.resolve()) else None
        if inside is None or not destination.exists():
            errors.append(f"{relative}: {target} does not resolve")
        elif any(inside == p.rstrip("/") or inside.startswith(p) for p in NOT_PUBLISHED):
            errors.append(f"{relative}: {target} points at {inside}, which is not published")
        elif fragment and destination.suffix == ".md" and fragment not in anchors(destination.read_text("utf-8")):
            errors.append(f"{relative}: {target} has no such heading")
    return errors


def placeholder_errors(relative: str, text: str) -> list[str]:
    """Earlet is named: reject leftover product and template placeholders."""
    tokens = [f"<{tag}>" for tag in re.findall(r"<([A-Za-z][A-Za-z0-9 _-]*)>", text)
              if tag.lower() not in HTML_TAGS]
    tokens += re.findall(r"[\[{(]\s*brand[^\]})\n]*[\]})]", text, flags=re.I)
    tokens += re.findall(r"\[INSERT [^\]]*\]", text)
    return [f"{relative}: unfilled template placeholder {token}" for token in tokens]


def address_errors(relative: str, text: str) -> list[str]:
    """No personal address: only example domains and reviewed role mailboxes."""
    return [f"{relative}: {address} is not an example or a role address"
            for address in re.findall(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+", text)
            if not re.search(r"@example\.(?:com|org|net)$", address) and address not in ROLE_ADDRESSES]


def network_claim_errors(relative: str, text: str) -> list[str]:
    return [f"{relative}: unmeasured network claim {match.group(0)!r}" for match in NETWORK_CLAIMS.finditer(text)]


def hygiene_errors(relative: str, text: str) -> list[str]:
    """Every text check a community file passes."""
    return placeholder_errors(relative, text) + address_errors(relative, text) + network_claim_errors(relative, text)


def license_errors(root: Path) -> list[str]:
    """Both copies are the Apache-2.0 text, CRLF read as LF: a Windows
    checkout (core.autocrlf, Git for Windows' default) may carry CRLF."""
    return [f"{name} is not the Apache-2.0 text" for name in ("LICENSE", "LICENSES/Apache-2.0.txt")
            if hashlib.sha256((root / name).read_bytes().replace(b"\r\n", b"\n")).hexdigest() != APACHE_SHA256]


def covenant_errors(text: str) -> list[str]:
    """Preserve Contributor Covenant 2.1 and require the reviewed role contact."""
    if "TODO(owner)" in text:
        return ["the conduct contact is still marked TODO(owner)"]
    body = text
    contact = re.search(r"responsible for enforcement at (.+?)\. All complaints", body)
    if not contact:
        return ["the Enforcement sentence that names the contact is missing"]
    address = ROLE_ADDRESSES[0]
    if contact.group(1) != f"[{address}](mailto:{address})":
        return ["the conduct contact is not the reviewed role mailbox"]
    canonical = body[:contact.start(1)] + COVENANT_PLACEHOLDER + body[contact.end(1):]
    if hashlib.sha256(canonical.encode("utf-8")).hexdigest() != COVENANT_SHA256:
        return ["the text differs from the Contributor Covenant 2.1 outside its contact"]
    return []


def web2_resource(root: Path) -> str:
    """The word list a Windows project embeds, as a path from the root."""
    for project in sorted(root.glob("windows/*/*.csproj")):
        for item in ET.parse(project).iter():
            include = item.get("Include") or ""
            if item.tag.rsplit("}", 1)[-1] == "EmbeddedResource" and "web2" in include:
                return (project.parent / include.replace("\\", "/")).relative_to(root).as_posix()
    raise AssertionError("no project embeds the web2 word list")


def reuse_glob(pattern: str) -> re.Pattern:
    """A REUSE.toml path as reuse 6.2.0 reads it: ** crosses /, and swallows
    the / after it; * does not cross /; \\* is a literal star."""
    parts = re.split(r"(\\\*|\*\*/?|\*)", pattern)
    table = {"\\*": r"\*", "*": "[^/]*"}
    return re.compile("".join(".*" if part.startswith("**") else table.get(part, re.escape(part))
                              for part in parts) + r"\Z")


def annotation_for(notes: list[dict], path: str) -> dict | None:
    """The annotation REUSE applies to a path: the LAST one that matches."""
    matching = [note for note in notes
                if any(reuse_glob(glob).match(path)
                       for glob in ([note.get("path")] if isinstance(note.get("path"), str) else note.get("path", [])))]
    return matching[-1] if matching else None


def reuse_errors(root: Path) -> list[str]:
    config = tomllib.loads(read("REUSE.toml", root))
    notes = config.get("annotations", [])
    errors = [] if config.get("version") == 1 else ["REUSE.toml is not version 1"]
    if not notes or (notes[0].get("path"), notes[0].get("SPDX-License-Identifier"),
                     notes[0].get("SPDX-FileCopyrightText")) != ("**", "Apache-2.0", COPYRIGHT):
        errors.append(f"the first annotation must give ** to Apache-2.0, {COPYRIGHT}")
    used = {note.get("SPDX-License-Identifier") for note in notes}
    shipped = {path.stem for path in (root / "LICENSES").glob("*.txt")}
    errors += [f"{name} is used but LICENSES/{name}.txt is missing" for name in sorted(used - shipped)]
    errors += [f"LICENSES/{name}.txt is not used" for name in sorted(shipped - used)]
    for note in notes:
        for path in [note.get("path")] if isinstance(note.get("path"), str) else note.get("path", []):
            if not re.search(r"[*?\[]", path) and not (root / path).exists():
                errors.append(f"REUSE.toml annotates {path}, which does not exist")
    # The last matching annotation decides, so a later glob over windows/
    # would relabel the word list; precedence override keeps a REUSE.toml nested
    # closer to the file from relabelling it (both measured with reuse 6.2.0).
    words = web2_resource(root)
    applied = annotation_for(notes, words) or {}
    if (applied.get("SPDX-License-Identifier"), applied.get("precedence")) != ("LicenseRef-Public-Domain", "override"):
        errors.append(f"{words} must be annotated LicenseRef-Public-Domain with precedence override, "
                      f"by the last annotation that matches it")
    if BSD_DICT_QUOTE not in read("LICENSES/LicenseRef-Public-Domain.txt", root):
        errors.append("LICENSES/LicenseRef-Public-Domain.txt lost the BSD README's provenance statement")
    return errors


def form_items(text: str) -> list[str]:
    return re.split(r"^  - type: ", text, flags=re.M)[1:]


def form_errors(relative: str, text: str) -> list[str]:
    """An issue form asks for the platform first, as a required dropdown."""
    errors = [f"{relative}: missing top-level {key}:" for key in ("name", "description", "body")
              if not re.search(rf"^{key}:", text, flags=re.M)]
    platform = [item for item in form_items(text) if item.startswith("dropdown") and "\n    id: platform\n" in item]
    if not platform:
        return errors + [f"{relative}: no Platform dropdown"]
    options = re.findall(r"^        - (.+)$", platform[0], flags=re.M)
    if not re.search(r"^      required: true$", platform[0], flags=re.M):
        errors.append(f"{relative}: the Platform dropdown is optional")
    if not any("macOS" in option for option in options) or not any("Windows" in option for option in options):
        errors.append(f"{relative}: the Platform dropdown lacks macOS or Windows")
    return errors


def chooser_errors(text: str) -> list[str]:
    """Blank issues off; ideas and questions to Discussions; vulnerabilities
    to private reporting."""
    urls = re.findall(r"^    url: (\S+)$", text, flags=re.M)
    errors = [] if re.search(r"^blank_issues_enabled: false$", text, flags=re.M) else ["blank issues are on"]
    for needle in ("/discussions", "/security/advisories/new"):
        if not any(url.endswith(needle) for url in urls):
            errors.append(f"no contact link to {needle}")
    return errors


def pull_request_errors(text: str) -> list[str]:
    needs = {"the DCO sign-off": "Signed-off-by:", "how to sign off": "git commit -s",
             "the AI-assistance disclosure": "AI assistance:", "the device evidence": "Ran on real hardware"}
    return [f"the pull request template lost {what}" for what, needle in needs.items() if needle not in text]


def codeowners_errors(root: Path, text: str) -> list[str]:
    errors, patterns = [], []
    for line in text.splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        pattern, *owners = line.split()
        patterns.append(pattern)
        if not owners:
            errors.append(f"CODEOWNERS: {pattern} has no owner")
        errors += [f"CODEOWNERS: {owner} is not a team (owners are teams, never people)"
                   for owner in owners if not re.fullmatch(r"@[\w-]+/[\w-]+", owner)]
        if pattern != "*" and not re.search(r"[*?\[]", pattern) and not (root / pattern.strip("/")).exists():
            errors.append(f"CODEOWNERS: {pattern} does not exist")
    if patterns[:1] != ["*"]:
        errors.append("CODEOWNERS: the first rule must give every file an owner (*)")
    return errors


def dependabot_errors(root: Path, text: str) -> list[str]:
    errors = [] if re.search(r"^version: 2$", text, flags=re.M) else ["dependabot.yml is not version 2"]
    blocks = re.split(r'^  - package-ecosystem: "([\w-]+)"$', text, flags=re.M)[1:]
    for ecosystem, block in zip(blocks[::2], blocks[1::2]):
        directories = re.findall(r'^    directory: "([^"]+)"$', block, flags=re.M) + re.findall(
            r'^      - "(/[^"]*)"$', block, flags=re.M)
        if not directories:
            errors.append(f"dependabot.yml: {ecosystem} names no directory")
        for directory in directories:
            matches = [path for path in root.glob(directory.strip("/")) if path.is_dir()] if directory != "/" else [root]
            if not matches:
                errors.append(f"dependabot.yml: {ecosystem} directory {directory} matches nothing")
        # Only the publication writes the public main, so a version-update pull
        # request there could only be imported by hand and closed.
        if not re.search(r"^    open-pull-requests-limit: 0$", block, flags=re.M):
            errors.append(f"dependabot.yml: {ecosystem} {' '.join(directories)} must get security updates only")
    for ecosystem in ("github-actions", "nuget", "pip"):
        if f'"{ecosystem}"' not in text:
            errors.append(f"dependabot.yml: no {ecosystem} entry")
    return errors


def changelog_errors(text: str) -> list[str]:
    errors = [] if re.findall(r"^## (.+)$", text, flags=re.M)[:1] == ["[Unreleased]"] else [
        "CHANGELOG.md must open with ## [Unreleased]"]
    # Pull request numbers of the development repository are private.
    errors += [f"CHANGELOG.md cites {number}" for number in re.findall(r"(?<![\w&])#\d{2,}\b", text)]
    return errors


def security_errors(text: str) -> list[str]:
    needs = {"private vulnerability reporting": "/security/advisories/new",
             "the 48-hour acknowledgement": "within 48 hours", "the key listeners": "**Key listeners.**",
             "the paste helpers": "**Paste helpers and the clipboard.**"}
    return [f"SECURITY.md lost {what}" for what, needle in needs.items() if needle not in text]


# ─── What the files say about the apps ───────────────────────────────────────
# SUPPORT.md, SECURITY.md and the issue forms send people to UI the apps show
# and to files they write, so each fact is read from the source that makes it
# true. The Windows window gained its Troubleshooting section (version, build,
# path) only on 2026-10-09, after these files were first drafted.

APP_SOURCES = {
    "windows copy": "windows/Dictation.Core/DictationCopy.cs",
    "windows identity": "windows/Dictation.Core/DictationInstallation.cs",
    "windows paths": "windows/Dictation.Core/DictationPaths.cs",
    "windows takes": "windows/Dictation.Core/DictationTakeOptions.cs",
    "mac spool": "macos/Sources/DictationAppCore/DictationTakeSpool.swift",
    "mac policy": "macos/Sources/DictationAppCore/DictationHoldPolicy.swift",
    "windows log": "windows/Dictation.Core/FileLoggerFactory.cs",
    "mac window": "macos/Sources/DictationApp/DictationSettingsView.swift",
    "mac identity": "macos/Sources/DictationAppCore/DictationPermissionSetup.swift",
    "mac log": "macos/Sources/DictationAppCore/DictationLogSink.swift",
    "mac history store": "macos/Sources/DictationHistory/DictationHistoryStore.swift",
    "mac settings": "macos/Sources/DictationAppCore/DictationSettings.swift",
    "mac history model": "macos/Sources/DictationAppCore/DictationHistoryModel.swift",
}
POINTERS = ("SECURITY.md", "SUPPORT.md", ".github/ISSUE_TEMPLATE/bug_report.yml",
            ".github/ISSUE_TEMPLATE/recognition_report.yml")
BUG_FORM = ".github/ISSUE_TEMPLATE/bug_report.yml"


def source_text(root: Path, relative: str) -> str:
    path = root / relative
    return path.read_text(encoding="utf-8", errors="replace") if path.is_file() else ""


def flat(text: str) -> str:
    return " ".join(text.split())


def app_facts(root: Path) -> dict[str, str | None]:
    """The labels and log paths, as each app's source spells them; None where
    the source no longer has one."""
    def find(source: str, pattern: str) -> str | None:
        match = re.search(pattern, source_text(root, APP_SOURCES[source]))
        return match.group(1) if match else None
    windows_parts = (find("windows paths", r'ProductDirectoryName = "([^"]+)"'),
                     find("windows paths", r'LogDirectoryName = "([^"]+)"'),
                     find("windows log", r'const string FileName = "([^"]+)"'))
    local = "SpecialFolder.LocalApplicationData" in source_text(root, APP_SOURCES["windows paths"])
    mac_parts = (find("mac log", r'appendingPathComponent\("(Library/Logs/[^"]+)"'),
                 find("mac log", r'activeFileName = "([^"]+)"'))
    return {
        "windows troubleshooting": find("windows copy", r'TroubleshootingHeading = "([^"]+)"'),
        "mac troubleshooting": find("mac window", r'DisclosureGroup\("([^"]+)", isExpanded: \$troubleshooting'),
        "windows version and build": find("windows identity", r'\$"(Version \{Version\} · Build) \{Build\}"'),
        "mac version and build": find("mac identity", r'"(Version \\\(version\) · Build) \\\(build\)"'),
        "windows log folder button": find("windows copy", r'OpenLogFolder = "([^"]+)"'),
        "mac log folder button": find("mac window", r'Button\("([^"]+)"\) \{ model\.openLogFolder\(\) \}'),
        "windows log path": "%LOCALAPPDATA%\\" + "\\".join(windows_parts) if all(windows_parts) and local else None,
        "mac log path": "~/" + "/".join(mac_parts) if all(mac_parts) else None,
    }


def app_fact_errors(root: Path) -> list[str]:
    facts, texts = app_facts(root), {name: flat(read(name, root)) for name in POINTERS}
    errors = []
    pointing = ", ".join(name for name, text in texts.items() if "under Troubleshooting" in text)
    for platform in ("windows", "mac") if pointing else ():
        if facts[f"{platform} troubleshooting"] != "Troubleshooting":
            errors.append(f"{pointing} send people to Troubleshooting, which the {platform} window lacks")
        if not facts[f"{platform} version and build"]:
            errors.append(f"{pointing} send people to Troubleshooting for a version and build the {platform} "
                          "window does not show")
    buttons = f"{facts['mac log folder button']} on macOS, {facts['windows log folder button']} on Windows"
    if buttons not in texts["SUPPORT.md"]:
        errors.append(f"SUPPORT.md does not name the log folder buttons as the apps do: {buttons}")
    for platform in ("mac", "windows"):
        button, path = facts[f"{platform} log folder button"], facts[f"{platform} log path"]
        if not button or button.lower() not in texts[BUG_FORM].lower():
            errors.append(f"{BUG_FORM} does not name the {platform} log folder button {button}")
        errors += [f"{name} does not give the {platform} log path {path}"
                   for name in ("SUPPORT.md", BUG_FORM) if not path or path not in texts[name]]
    return errors


# The Windows file log keeps Information and above (FileLoggerFactory's
# default floor, which App.xaml.cs keeps). A dictation's log lines that can name
# the target come from these projects; the speech projects (Dictation.Speech,
# Dictation.Platform) log the worker and the microphone, never the target.
WINDOWS_DICTATION_PROJECTS = ("Dictation.App", "Dictation.Core", "Dictation.Infrastructure",
                              "Dictation.Services")
KEPT_LOG_CALL = re.compile(r'\.Log(?:Information|Warning|Error|Critical)\(\s*((?:\$?"(?:[^"\\]|\\.)*"\s*\+?\s*)+)')
# What the logs name besides outcomes and timings, and how each file says so.
LOG_DISCLOSURES = {
    "app": {"SUPPORT.md": "name the app you dictated into", BUG_FORM: "name the app you dictated into",
            "CONTRIBUTING.md": "name the app a dictation went into"},
    "title": {"SUPPORT.md": "the title of a window whose app could not be identified",
              BUG_FORM: "the title of a window whose app could not be identified",
              "CONTRIBUTING.md": "the title of a window whose app it cannot identify"},
}


def log_names(root: Path) -> set[str]:
    """"app" when a kept Windows log line names the target's process, or the
    Mac's paste code writes the target's bundle ID to stderr, which the Mac
    log tees in; "title" when a kept Windows line names a window title."""
    names = set()
    for project in WINDOWS_DICTATION_PROJECTS:
        for path in sorted((root / "windows" / project).rglob("*.cs")):
            for call in KEPT_LOG_CALL.finditer(path.read_text(encoding="utf-8", errors="replace")):
                template = "".join(re.findall(r'"((?:[^"\\]|\\.)*)"', call.group(1)))
                if re.search(r"\{Process\}|target=\{Target\}", template):
                    names.add("app")
                if "{Title}" in template:
                    names.add("title")
    for path in sorted((root / "macos/Sources").rglob("*.swift")):
        if re.search(r"target=\\\(\w*[Bb]undle", path.read_text(encoding="utf-8", errors="replace")):
            names.add("app")
    return names


def log_disclosure_errors(root: Path) -> list[str]:
    """The files say what the logs name, and stop saying it once they do not."""
    names, errors = log_names(root), []
    for kind, phrases in LOG_DISCLOSURES.items():
        for name, phrase in phrases.items():
            says = phrase in flat(read(name, root))
            if kind in names and not says:
                errors.append(f"{name} must say the logs can {phrase}")
            elif says and kind not in names:
                errors.append(f"{name} says the logs can {phrase}, which no log line does")
    return errors


# Both apps write every take's audio to a recovery folder while it records and
# keep at most one failed or interrupted take for Recover last dictation. The
# folder, age limit and Mac's 600-second policy are read from their sources so
# public-file claims cannot silently drift.
RECORDING_DENIAL = re.compile(
    r"\b(?:keeps?|kept|keeping) no recordings\b|\bno recordings\b.*?\b(?:is|are) (?:kept|saved|stored)\b", re.I)
RECORDING_TEXTS = (*TEXTS, "README.md")  # the README is published beside them


def windows_recording_facts(root: Path) -> tuple[str | None, int | None]:
    """The folder a Windows take is spooled to and kept in, and its age limit
    in days, as the source spells them; None where the source has none."""
    paths = source_text(root, APP_SOURCES["windows paths"])
    product = re.search(r'ProductDirectoryName = "([^"]+)"', paths)
    folder = re.search(r'RecoveryDirectory => Path\.Combine\(Root, "([^"]+)"\)', paths)
    days = re.search(r"KeptFor \{ get; init; \} = TimeSpan\.FromDays\((\d+)\)",
                     source_text(root, APP_SOURCES["windows takes"]))
    local = "SpecialFolder.LocalApplicationData" in paths
    path = f"%LOCALAPPDATA%\\{product.group(1)}\\{folder.group(1)}" if product and folder and local else None
    return path, int(days.group(1)) if days else None


def mac_recording_facts(root: Path) -> tuple[str | None, int | None, int | None, int | None]:
    """The Mac recovery path, retention age, ceiling and countdown defaults."""
    spool = source_text(root, APP_SOURCES["mac spool"])
    policy = source_text(root, APP_SOURCES["mac policy"])
    directory = ("~/Library/Application Support/Dictation/dictation/recovery"
                 if all(part in spool for part in (
                     'appendingPathComponent("Dictation"',
                     'appendingPathComponent("dictation"',
                     'appendingPathComponent("recovery"')) else None)
    days = 7 if re.search(r"keptFor: TimeInterval = 7 \* 24 \* 60 \* 60", spool) else None
    limit = 600 if re.search(r"maximumHoldSeconds: TimeInterval = 10 \* 60", policy) else None
    countdown = 60 if re.search(r"countdownSeconds: TimeInterval = 60", policy) else None
    return directory, days, limit, countdown


def recording_errors(root: Path) -> list[str]:
    path, days = windows_recording_facts(root)
    mac_path, mac_days, mac_limit, mac_countdown = mac_recording_facts(root)
    errors = []
    for name in RECORDING_TEXTS:
        for sentence in re.split(r"(?<=[.;])\s", flat(read(name, root))):
            if RECORDING_DENIAL.search(sentence):
                windows_fact = f"Windows keeps one in {path}" if path else "the Mac and Windows recovery contract"
                errors.append(f"{name} says the apps keep no recordings; {windows_fact}: "
                              f"{sentence.strip()!r}")
    security = flat(read("SECURITY.md", root))
    if path and (path not in security or f"{days} days at most" not in security):
        errors.append(f"SECURITY.md does not say the Windows app keeps a recording in {path} for {days} days at most")
    elif not path and re.search(r"\\recovery\b", security):
        errors.append("SECURITY.md names a recovery folder the Windows app no longer writes")
    mac_claims = flat(read("SECURITY.md", root)) + " " + flat(read("CHANGELOG.md", root))
    expected_mac_path = "~/Library/Application Support/Dictation/dictation/recovery"
    if (mac_path, mac_days) != (expected_mac_path, 7) or (
            mac_path not in mac_claims or "7 days" not in mac_claims):
        errors.append(f"Mac public files do not say takes remain in {expected_mac_path} for 7 days at most")
    if (mac_limit, mac_countdown) != (600, 60) or (
            "600 seconds" not in mac_claims or "60-second" not in mac_claims):
        errors.append("Mac public files do not say the limit is 600 seconds with a 60-second countdown")
    return errors


MAC_HISTORY = "~/Library/Application Support/Dictation/dictation/history"


def mac_history_facts(root: Path) -> tuple[str | None, str | None, int | None, str | None]:
    """The Mac history's folder, how long words and recordings are kept by
    default, and the recordings' size limit, as the source spells them."""
    store = source_text(root, APP_SOURCES["mac history store"])
    settings = source_text(root, APP_SOURCES["mac settings"])
    model = source_text(root, APP_SOURCES["mac history model"])
    directory = MAC_HISTORY if all(part in store for part in (
        'appendingPathComponent("Dictation"', 'appendingPathComponent("dictation"',
        'appendingPathComponent("history"')) else None
    words = "forever" if re.search(r"DictationHistoryKeep\.init\(rawValue:\)\) \?\? \.forever", settings) else None
    # DictationRecordingKeep's own table: its .month follows its .off and .week.
    recordings = 30 if (re.search(r"DictationRecordingKeep\.init\(rawValue:\)\) \?\? \.month", settings)
                        and re.search(r"case \.off: return 0\s+case \.week: return 7 \* DictationHistoryModel\.day"
                                      r"\s+case \.month: return 30 \* DictationHistoryModel\.day", model)) else None
    limit = "1 GiB" if re.search(r"recordingByteLimit: Int64 = 1 << 30\b", model) else None
    return directory, words, recordings, limit


def history_errors(root: Path) -> list[str]:
    """SECURITY.md says where the Mac keeps its history and for how long by
    default, as the source does."""
    security = flat(read("SECURITY.md", root))
    facts = mac_history_facts(root)
    said = (MAC_HISTORY in security and "forever by default" in security
            and "30 days by default" in security and "1 GiB at most" in security)
    if facts != (MAC_HISTORY, "forever", 30, "1 GiB") or not said:
        return [f"SECURITY.md does not say the Mac history is kept in {MAC_HISTORY}, words forever and "
                "recordings 30 days by default, 1 GiB at most"]
    return []


# ─── The real files ──────────────────────────────────────────────────────────

def test_every_community_file_is_present():
    assert [name for name in COMMUNITY if not (ROOT / name).is_file()] == []


def test_license_is_the_apache_2_text_verbatim_in_both_places():
    assert license_errors(ROOT) == []


def test_notice_names_the_copyright_holder_only():
    assert read("NOTICE") == f"Copyright {COPYRIGHT}\n"


def test_reuse_covers_the_tree_and_marks_the_word_list_public_domain():
    assert reuse_errors(ROOT) == []


def test_code_of_conduct_is_the_contributor_covenant():
    text = read("CODE_OF_CONDUCT.md")
    assert covenant_errors(text) == []
    assert f"mailto:{ROLE_ADDRESSES[0]}" in text
    assert "/security/advisories/new" in read("SECURITY.md")


@pytest.mark.parametrize("name", [name for name in TEXTS if name.endswith(".md")])
def test_links_resolve_inside_the_published_tree(name):
    assert link_errors(name, read(name)) == []


@pytest.mark.parametrize("name", TEXTS)
def test_public_text_hygiene(name):
    assert hygiene_errors(name, read(name)) == []


@pytest.mark.parametrize("name", sorted(path.relative_to(ROOT).as_posix()
                                        for path in (ROOT / ".github/ISSUE_TEMPLATE").glob("*.yml")
                                        if path.name != "config.yml"))
def test_issue_forms_ask_for_the_platform(name):
    assert form_errors(name, read(name)) == []


def test_feature_requests_go_to_discussions_and_vulnerabilities_stay_private():
    assert chooser_errors(read(".github/ISSUE_TEMPLATE/config.yml")) == []
    assert not list((ROOT / ".github/ISSUE_TEMPLATE").glob("feature*"))


def test_pull_request_template_asks_for_sign_off_ai_use_and_device_evidence():
    assert pull_request_errors(read(".github/PULL_REQUEST_TEMPLATE.md")) == []


def test_codeowners_are_teams_on_paths_that_exist():
    assert codeowners_errors(ROOT, read(".github/CODEOWNERS")) == []


def test_dependabot_watches_directories_that_exist():
    assert dependabot_errors(ROOT, read(".github/dependabot.yml")) == []


def test_changelog_opens_with_unreleased_and_cites_no_private_pull_request():
    assert changelog_errors(read("CHANGELOG.md")) == []


def test_security_policy_keeps_its_channel_promise_and_scope():
    assert security_errors(read("SECURITY.md")) == []


def test_files_name_the_sections_buttons_and_log_paths_the_apps_have():
    assert app_fact_errors(ROOT) == []


def test_files_say_what_the_logs_name_beyond_outcomes():
    assert log_disclosure_errors(ROOT) == []


def test_files_say_where_both_apps_keep_failed_recordings():
    assert windows_recording_facts(ROOT) == ("%LOCALAPPDATA%\\Dictation\\recovery", 7)
    assert mac_recording_facts(ROOT) == (
        "~/Library/Application Support/Dictation/dictation/recovery", 7, 600, 60)
    assert recording_errors(ROOT) == []


def test_security_md_says_where_and_how_long_the_mac_history_is_kept():
    assert mac_history_facts(ROOT) == (MAC_HISTORY, "forever", 30, "1 GiB")
    assert history_errors(ROOT) == []


@pytest.mark.parametrize("source,old,new", [
    ("mac history store", 'appendingPathComponent("history"', 'appendingPathComponent("saved"'),
    ("mac settings", "DictationHistoryKeep.init(rawValue:)) ?? .forever", "DictationHistoryKeep.init(rawValue:)) ?? .year"),
    ("mac settings", "DictationRecordingKeep.init(rawValue:)) ?? .month", "DictationRecordingKeep.init(rawValue:)) ?? .week"),
    ("mac history model", "case .week: return 7 * DictationHistoryModel.day\n        case .month: return 30 * DictationHistoryModel.day\n        }\n    }\n}",
     "case .week: return 7 * DictationHistoryModel.day\n        case .month: return 31 * DictationHistoryModel.day\n        }\n    }\n}"),
    ("mac history model", "recordingByteLimit: Int64 = 1 << 30", "recordingByteLimit: Int64 = 1 << 31"),
    ("SECURITY.md", "30 days by default", "a month by default"),
    ("SECURITY.md", "1 GiB at most", "a few gigabytes"),
])
def test_history_check_fires_when_the_source_or_security_md_changes(recording_tree, source, old, new):
    page = recording_tree / APP_SOURCES.get(source, source)
    text = page.read_text(encoding="utf-8")
    assert old in text
    page.write_text(text.replace(old, new, 1), encoding="utf-8")
    assert history_errors(recording_tree) == [
        f"SECURITY.md does not say the Mac history is kept in {MAC_HISTORY}, words forever and "
        "recordings 30 days by default, 1 GiB at most"]


@pytest.mark.parametrize("source,old,new,expected", [
    ("mac spool", 'appendingPathComponent("recovery"', 'appendingPathComponent("kept"',
     "Mac public files do not say takes remain in ~/Library/Application Support/Dictation/dictation/recovery for 7 days at most"),
    ("mac spool", "keptFor: TimeInterval = 7 * 24 * 60 * 60", "keptFor: TimeInterval = 3 * 24 * 60 * 60",
     "Mac public files do not say takes remain in ~/Library/Application Support/Dictation/dictation/recovery for 7 days at most"),
    ("mac policy", "maximumHoldSeconds: TimeInterval = 10 * 60", "maximumHoldSeconds: TimeInterval = 5 * 60",
     "Mac public files do not say the limit is 600 seconds with a 60-second countdown"),
    ("mac policy", "countdownSeconds: TimeInterval = 60", "countdownSeconds: TimeInterval = 30",
     "Mac public files do not say the limit is 600 seconds with a 60-second countdown"),
])
def test_recording_check_fires_when_mac_source_defaults_change(recording_tree, source, old, new, expected):
    page = recording_tree / APP_SOURCES[source]
    text = page.read_text(encoding="utf-8")
    assert old in text
    page.write_text(text.replace(old, new, 1), encoding="utf-8")
    assert recording_errors(recording_tree) == [expected]


# ─── Each check fires on a planted defect ────────────────────────────────────

def test_link_check_fires(tmp_path):
    (tmp_path / "docs").mkdir()
    (tmp_path / "VERIFY.md").write_text("# Status\n")
    (tmp_path / "docs/a.md").write_text("# Top\n")
    page = ("[ok](docs/a.md#top) [code](`x`) `[skip](nowhere.md)` [web](https://example.com/x)\n"
            "[gone](docs/b.md) [anchor](docs/a.md#bottom) [private](VERIFY.md) [up](../outside.md)\n"
            "\n[ref]: https://example.com/ok\n[log]: VERIFY.md#status\n")
    errors = link_errors("page.md", page, tmp_path)
    assert [error.split(": ", 1)[1] for error in errors] == [
        "docs/b.md does not resolve", "docs/a.md#bottom has no such heading",
        "VERIFY.md points at VERIFY.md, which is not published", "../outside.md does not resolve",
        "VERIFY.md#status points at VERIFY.md, which is not published"]


def test_placeholder_address_and_network_checks_fire():
    assert len(placeholder_errors("x", "<BRAND> <Brand> [BRAND] {brand} <PRODUCT> <details> [INSERT NAME]")) == 6
    assert placeholder_errors("x", "Welcome to Earlet. <details><summary>More</summary></details>") == []
    assert len(address_errors("x", "you@example.com, conduct@team.invalid, person@private.invalid")) == 2
    claims = ("It makes no network connections.", "Works offline.", "Nothing ever leaves your Mac.", "Fully offline.",
              "Your voice never leaves your Mac.", "Audio stays on your computer.", "No data is sent to the cloud.",
              "Runs 100% locally.", "No internet connection needed.", "It does not connect to the internet.",
              "Private: nothing is uploaded.", "It doesn't phone home.", "Your words are never uploaded.",
              "Works without an internet connection.", "Recognition is on-device only.", "Cloud-free dictation.",
              "Everything stays local.")
    assert [claim for claim in claims if not network_claim_errors("x", claim)] == []
    quiet = ("Network traffic has not been measured; the offline switches are set. Windows keeps these writes out "
             "of clipboard history and cloud sync. Recognition runs on the computer, never a whole sentence is "
             "logged, and no recordings or transcript history are kept. An outbound connection you did not expect "
             "is worth reporting; the worker is told never to download a model. Do not leave the key held.")
    assert network_claim_errors("x", quiet) == []


@pytest.mark.parametrize("text", ["<BRAND>", "\\<BRAND>", "<Brand>", "[BRAND]", "{brand}"])
def test_the_chosen_brand_leaves_no_placeholder_even_when_escaped(text):
    assert placeholder_errors("p.md", f"Welcome to {text}.")


def test_hygiene_runs_every_text_check():
    """Through the entry point the real files use, one planted defect each."""
    for file in ("p.md", "p.yml"):
        assert hygiene_errors(file, "Welcome to <BRAND>.") == [
            f"{file}: unfilled template placeholder <BRAND>"]
    assert hygiene_errors("p.md", "Write to person@private.invalid.") == [
        "p.md: person@private.invalid is not an example or a role address"]
    assert hygiene_errors("p.md", "Audio stays on your computer.") == [
        "p.md: unmeasured network claim 'stays on your computer'"]


def test_license_check_reads_crlf_as_lf_and_still_fires(tmp_path):
    text = (ROOT / "LICENSE").read_bytes().replace(b"\r\n", b"\n")  # this checkout may be CRLF itself
    (tmp_path / "LICENSES").mkdir()
    (tmp_path / "LICENSE").write_bytes(text.replace(b"\n", b"\r\n"))  # a CRLF checkout
    (tmp_path / "LICENSES/Apache-2.0.txt").write_bytes(text.replace(b"[yyyy]", b"2026"))  # the appendix filled in
    assert license_errors(tmp_path) == ["LICENSES/Apache-2.0.txt is not the Apache-2.0 text"]


def test_covenant_check_fires():
    text = read("CODE_OF_CONDUCT.md")
    assert covenant_errors(text.replace("harassment-free", "harassment free")) == [
        "the text differs from the Contributor Covenant 2.1 outside its contact"]
    assert covenant_errors("<!-- TODO(owner): choose a contact -->\n" + text) == [
        "the conduct contact is still marked TODO(owner)"]
    assert covenant_errors(text.replace("All complaints", "Every complaint")) == [
        "the Enforcement sentence that names the contact is missing"]
    # Both the covenant contract and the generic placeholder guard reject an
    # unconfigured reporting route.
    unfilled = re.sub(r"(enforcement at ).+?(\. All complaints)",
                      lambda match: match.group(1) + COVENANT_PLACEHOLDER + match.group(2), text)
    assert covenant_errors(unfilled) == ["the conduct contact is not the reviewed role mailbox"]
    assert placeholder_errors("x", unfilled)
    assert address_errors("x", ROLE_ADDRESSES[0]) == []
    assert address_errors("x", "person@private.invalid")


@pytest.fixture
def licensed_tree(tmp_path):
    for name in ("REUSE.toml", "LICENSES/Apache-2.0.txt", "LICENSES/LicenseRef-Public-Domain.txt"):
        (tmp_path / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(ROOT / name, tmp_path / name)
    words = web2_resource(ROOT)
    for name in (words, *(path.relative_to(ROOT).as_posix() for path in ROOT.glob("windows/*/*.csproj")
                          if "web2" in path.read_text(encoding="utf-8"))):
        (tmp_path / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(ROOT / name, tmp_path / name)
    assert reuse_errors(tmp_path) == []
    return tmp_path


def test_reuse_check_fires_when_the_word_list_loses_its_annotation(licensed_tree):
    config = licensed_tree / "REUSE.toml"
    config.write_text(config.read_text().split("# The web2 word list")[0])
    assert reuse_errors(licensed_tree) == [
        "LICENSES/LicenseRef-Public-Domain.txt is not used",
        f"{web2_resource(ROOT)} must be annotated LicenseRef-Public-Domain with precedence override, "
        "by the last annotation that matches it"]


@pytest.mark.parametrize("glob,relabels", [
    ("**", True), ("windows/**", True), ("windows/*/Resources/*.gz", True), ("**/Resources/**", True),
    ("windows/*", False), ("runtime/**", False), ("windows/\\\\*", False), (["docs/**", "windows/**"], True)])
def test_reuse_check_fires_when_a_later_annotation_relabels_the_word_list(licensed_tree, glob, relabels):
    """The last matching annotation is the one REUSE applies; the quiet rows
    are globs that do not reach the word list (* stops at /)."""
    config = licensed_tree / "REUSE.toml"
    path = f'"{glob}"' if isinstance(glob, str) else "[" + ", ".join(f'"{g}"' for g in glob) + "]"
    config.write_text(config.read_text() + f'\n[[annotations]]\npath = {path}\n'
                      f'SPDX-FileCopyrightText = "{COPYRIGHT}"\nSPDX-License-Identifier = "Apache-2.0"\n')
    assert reuse_errors(licensed_tree) == ([
        f"{web2_resource(ROOT)} must be annotated LicenseRef-Public-Domain with precedence override, "
        "by the last annotation that matches it"] if relabels else [])


def test_reuse_check_fires_when_the_word_list_loses_precedence_override(licensed_tree):
    config = licensed_tree / "REUSE.toml"
    config.write_text(config.read_text().replace('precedence = "override"\n', ""))
    assert reuse_errors(licensed_tree) == [
        f"{web2_resource(ROOT)} must be annotated LicenseRef-Public-Domain with precedence override, "
        "by the last annotation that matches it"]


def test_reuse_check_fires_when_the_word_list_moves(licensed_tree):
    words = licensed_tree / web2_resource(ROOT)
    words.rename(words.with_name("words.txt.gz"))
    assert reuse_errors(licensed_tree) == [f"REUSE.toml annotates {web2_resource(ROOT)}, which does not exist"]


def test_reuse_check_fires_on_a_missing_license_text_and_a_changed_default(licensed_tree):
    (licensed_tree / "LICENSES/Apache-2.0.txt").unlink()
    config = licensed_tree / "REUSE.toml"
    config.write_text(config.read_text().replace(f'"{COPYRIGHT}"', '"2026 Someone Else"'))
    assert reuse_errors(licensed_tree) == [
        f"the first annotation must give ** to Apache-2.0, {COPYRIGHT}",
        "Apache-2.0 is used but LICENSES/Apache-2.0.txt is missing"]


def test_reuse_check_fires_when_the_provenance_quote_is_lost(licensed_tree):
    note = licensed_tree / "LICENSES/LicenseRef-Public-Domain.txt"
    note.write_text(note.read_text().replace("has lapsed", "lapsed"))
    assert reuse_errors(licensed_tree) == [
        "LICENSES/LicenseRef-Public-Domain.txt lost the BSD README's provenance statement"]


def test_form_and_chooser_checks_fire():
    form = read(".github/ISSUE_TEMPLATE/bug_report.yml")
    platform = re.search(r"  - type: dropdown\n    id: platform\n.*?(?=\n  - type: )", form, flags=re.S).group(0)
    assert form_errors("f", form.replace(platform, "")) == ["f: no Platform dropdown"]
    optional = platform.replace("      required: true", "      required: false")
    assert form_errors("f", form.replace(platform, optional)) == ["f: the Platform dropdown is optional"]
    no_windows = re.sub(r"^        - Windows.*\n", "", form, flags=re.M).replace("(any platform)", "")
    assert no_windows != form and form_errors("f", no_windows) == ["f: the Platform dropdown lacks macOS or Windows"]
    chooser = read(".github/ISSUE_TEMPLATE/config.yml")
    assert chooser_errors(chooser.replace("false", "true")) == ["blank issues are on"]
    assert chooser_errors(chooser.replace("/discussions", "/issues")) == [
        "no contact link to /discussions"]


def test_pull_request_and_security_checks_fire():
    template = read(".github/PULL_REQUEST_TEMPLATE.md")
    assert pull_request_errors(template.replace("Signed-off-by:", "Sign-off:")) == [
        "the pull request template lost the DCO sign-off"]
    assert pull_request_errors(template.replace("AI assistance:", "Tools:")) == [
        "the pull request template lost the AI-assistance disclosure"]
    policy = read("SECURITY.md")
    assert security_errors(policy.replace("within 48 hours", "promptly")) == [
        "SECURITY.md lost the 48-hour acknowledgement"]
    assert security_errors(policy.replace("**Paste helpers and the clipboard.**", "**Clipboard.**")) == [
        "SECURITY.md lost the paste helpers"]


def test_codeowners_and_dependabot_checks_fire():
    owners = read(".github/CODEOWNERS")
    assert codeowners_errors(ROOT, owners + "/tools/ @a-person\n") == [
        "CODEOWNERS: @a-person is not a team (owners are teams, never people)"]
    assert codeowners_errors(ROOT, owners + "/gone.md @example/maintainers\n") == [
        "CODEOWNERS: /gone.md does not exist"]
    assert "\n* " in owners
    assert codeowners_errors(ROOT, owners.replace("\n* ", "\n/docs/ ", 1)) == [
        "CODEOWNERS: the first rule must give every file an owner (*)"]
    config = read(".github/dependabot.yml")
    assert dependabot_errors(ROOT, config.replace('"/tests"', '"/test"')) == [
        "dependabot.yml: pip directory /test matches nothing"]
    nuget = re.search(r'  - package-ecosystem: "nuget"\n.*?(?=\n\n|\Z)', config, flags=re.S).group(0)
    assert dependabot_errors(ROOT, config.replace(nuget, nuget.replace(
        "open-pull-requests-limit: 0", "open-pull-requests-limit: 3"))) == [
        "dependabot.yml: nuget /windows/* must get security updates only"]


def test_changelog_check_fires():
    changelog = read("CHANGELOG.md")
    assert changelog_errors(changelog.replace("## [Unreleased]", "## [0.1.0]")) == [
        "CHANGELOG.md must open with ## [Unreleased]"]
    assert changelog_errors(changelog + "- Fixed the latch (#42).\n") == ["CHANGELOG.md cites #42"]


@pytest.fixture
def app_tree(tmp_path):
    for name in (*POINTERS, *APP_SOURCES.values()):
        (tmp_path / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(ROOT / name, tmp_path / name)
    assert app_fact_errors(tmp_path) == []
    return tmp_path


TROUBLESHOOTING_POINTERS = "SECURITY.md, SUPPORT.md, " + ", ".join(
    f".github/ISSUE_TEMPLATE/{name}_report.yml" for name in ("bug", "recognition"))


@pytest.mark.parametrize("source,old,new,expected", [
    ("windows copy", 'TroubleshootingHeading = "Troubleshooting"', 'TroubleshootingHeading = "Diagnostics"',
     [f"{TROUBLESHOOTING_POINTERS} send people to Troubleshooting, which the windows window lacks"]),
    ("mac window", 'DisclosureGroup("Troubleshooting"', 'DisclosureGroup("Diagnostics"',
     [f"{TROUBLESHOOTING_POINTERS} send people to Troubleshooting, which the mac window lacks"]),
    ("windows identity", '$"Version {Version} · Build {Build}"', '$"Version {Version}"',
     [f"{TROUBLESHOOTING_POINTERS} send people to Troubleshooting for a version and build the windows window "
      "does not show"]),
    ("mac identity", '"Version \\(version) · Build \\(build)"', '"Version \\(version)"',
     [f"{TROUBLESHOOTING_POINTERS} send people to Troubleshooting for a version and build the mac window "
      "does not show"]),
    ("windows paths", 'LogDirectoryName = "logs"', 'LogDirectoryName = "Logs"',
     [f"{name} does not give the windows log path %LOCALAPPDATA%\\Dictation\\Logs\\dictation.log"
      for name in ("SUPPORT.md", BUG_FORM)]),
    ("windows paths", "SpecialFolder.LocalApplicationData", "SpecialFolder.ApplicationData",
     [f"{name} does not give the windows log path None" for name in ("SUPPORT.md", BUG_FORM)]),
    ("windows paths", 'ProductDirectoryName = "Dictation"', 'ProductDirectoryName = "Dictate"',
     [f"{name} does not give the windows log path %LOCALAPPDATA%\\Dictate\\logs\\dictation.log"
      for name in ("SUPPORT.md", BUG_FORM)]),
    ("windows log", 'const string FileName = "dictation.log"', 'const string FileName = "app.log"',
     [f"{name} does not give the windows log path %LOCALAPPDATA%\\Dictation\\logs\\app.log"
      for name in ("SUPPORT.md", BUG_FORM)]),
    ("mac log", 'activeFileName = "dictation.log"', 'activeFileName = "app.log"',
     [f"{name} does not give the mac log path ~/Library/Logs/Dictation/app.log" for name in ("SUPPORT.md", BUG_FORM)]),
    ("mac log", '"Library/Logs/Dictation"', '"Library/Logs/Dictate"',
     [f"{name} does not give the mac log path ~/Library/Logs/Dictate/dictation.log" for name in ("SUPPORT.md", BUG_FORM)]),
    ("windows copy", 'OpenLogFolder = "Open log folder"', 'OpenLogFolder = "Show logs"',
     ["SUPPORT.md does not name the log folder buttons as the apps do: Open Log Folder on macOS, Show logs on Windows",
      f"{BUG_FORM} does not name the windows log folder button Show logs"]),
])
def test_app_fact_check_fires(app_tree, source, old, new, expected):
    path = app_tree / APP_SOURCES[source]
    text = path.read_text(encoding="utf-8")
    assert old in text
    path.write_text(text.replace(old, new), encoding="utf-8")
    assert app_fact_errors(app_tree) == expected


def test_app_fact_check_asks_for_troubleshooting_only_while_a_file_points_there(app_tree):
    path = app_tree / APP_SOURCES["windows copy"]
    path.write_text(path.read_text(encoding="utf-8").replace('"Troubleshooting"', '"Diagnostics"'), encoding="utf-8")
    for name in POINTERS:
        page = app_tree / name
        page.write_text(re.sub(r"under\s+Troubleshooting", "in the window", page.read_text(encoding="utf-8")),
                        encoding="utf-8")
    assert app_fact_errors(app_tree) == []


WINDOWS_LINES = (
    '_log.LogInformation(\n    "Dictation completed: operation={Operation} "\n'
    '    + "target={Target} chars={Characters}",\n    id, process, 0);\n'
    '_logger.LogWarning("Foreground window {Title} could not be identified; not inserting.", title);\n')
MAC_LINE = 'write("[dictation] insert gate rejected: \\(failure) " + "target=\\(targetBundleID ?? "none")\\n")\n'


@pytest.fixture
def logged_tree(tmp_path):
    for name in ("SUPPORT.md", "CONTRIBUTING.md", BUG_FORM):
        (tmp_path / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(ROOT / name, tmp_path / name)
    for name, text in (("windows/Dictation.Services/Session.cs", WINDOWS_LINES),
                       ("macos/Sources/DictationKit/TextInserter.swift", MAC_LINE)):
        (tmp_path / name).parent.mkdir(parents=True, exist_ok=True)
        (tmp_path / name).write_text(text, encoding="utf-8")
    assert log_names(tmp_path) == {"app", "title"} and log_disclosure_errors(tmp_path) == []
    return tmp_path


def test_log_disclosure_check_fires_when_a_file_stops_saying_it(logged_tree):
    support = logged_tree / "SUPPORT.md"
    support.write_text(support.read_text(encoding="utf-8").replace("They can name", "They can list"), encoding="utf-8")
    assert log_disclosure_errors(logged_tree) == ["SUPPORT.md must say the logs can name the app you dictated into"]


def test_log_disclosure_check_fires_when_the_logs_stop_naming_it(logged_tree):
    windows = logged_tree / "windows/Dictation.Services/Session.cs"
    windows.write_text(WINDOWS_LINES.replace("LogWarning", "LogDebug"), encoding="utf-8")  # below the file log's floor
    assert log_disclosure_errors(logged_tree) == [
        f"{name} says the logs can {LOG_DISCLOSURES['title'][name]}, which no log line does"
        for name in ("SUPPORT.md", BUG_FORM, "CONTRIBUTING.md")]
    windows.write_text("", encoding="utf-8")
    assert log_names(logged_tree) == {"app"}  # the Mac's line alone still names the app
    (logged_tree / "macos/Sources/DictationKit/TextInserter.swift").write_text("", encoding="utf-8")
    assert log_disclosure_errors(logged_tree) == [
        f"{name} says the logs can {LOG_DISCLOSURES[kind][name]}, which no log line does"
        for kind in ("app", "title") for name in ("SUPPORT.md", BUG_FORM, "CONTRIBUTING.md")]


def test_log_names_reads_only_the_dictation_projects_and_kept_levels(tmp_path):
    other = tmp_path / "windows/Dictation.Speech/Messages.cs"  # not reached by a dictation
    other.parent.mkdir(parents=True)
    other.write_text(WINDOWS_LINES, encoding="utf-8")
    core = tmp_path / "windows/Dictation.Core/Feed.cs"
    core.parent.mkdir(parents=True)
    core.write_text('_log.LogDebug("Typed into {Process} ({Title}).", p, t);\n', encoding="utf-8")
    assert log_names(tmp_path) == set()
    core.write_text('_log.LogError("Typed into {Process} ({Title}).", p, t);\n', encoding="utf-8")
    assert log_names(tmp_path) == {"app", "title"}
    core.write_text(WINDOWS_LINES.split("_logger")[0], encoding="utf-8")  # the target in a concatenated template
    assert log_names(tmp_path) == {"app"}


@pytest.fixture
def recording_tree(app_tree):
    for name in RECORDING_TEXTS:
        (app_tree / name).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(ROOT / name, app_tree / name)
    assert recording_errors(app_tree) == []
    return app_tree


@pytest.mark.parametrize("name,old,new", [
    # A bare no-recordings claim is now false for either desktop app.
    ("SECURITY.md", "its key. The macOS app keeps one", "its key. The macOS app keeps no recordings;"),
    ("CHANGELOG.md", "latest result, in memory. The Mac app removes", "latest result, in memory. The Mac app keeps no recordings;"),
    ("README.md", "No account is required, and logs", "No recordings are kept, no account is required, and logs"),
])
def test_recording_check_fires_on_an_unqualified_denial(recording_tree, name, old, new):
    page = recording_tree / name
    text = page.read_text(encoding="utf-8")
    assert old in text
    page.write_text(text.replace(old, new, 1), encoding="utf-8")
    errors = recording_errors(recording_tree)
    assert len(errors) == 1 and errors[0].startswith(
        f"{name} says the apps keep no recordings; Windows keeps one in %LOCALAPPDATA%\\Dictation\\recovery")


@pytest.mark.parametrize("source,old,new,expected", [
    ("SECURITY.md", "%LOCALAPPDATA%\\Dictation\\recovery", "the app's folder", "in %LOCALAPPDATA%\\Dictation\\recovery for 7"),
    ("SECURITY.md", "for 7 days at most", "for a while", "in %LOCALAPPDATA%\\Dictation\\recovery for 7"),
    ("windows takes", "TimeSpan.FromDays(7)", "TimeSpan.FromDays(3)", "in %LOCALAPPDATA%\\Dictation\\recovery for 3"),
    ("windows paths", 'Path.Combine(Root, "recovery")', 'Path.Combine(Root, "kept")',
     "in %LOCALAPPDATA%\\Dictation\\kept for 7"),
])
def test_recording_check_fires_when_security_md_and_the_source_disagree(recording_tree, source, old, new, expected):
    page = recording_tree / APP_SOURCES.get(source, source)
    text = page.read_text(encoding="utf-8")
    assert old in text
    page.write_text(text.replace(old, new), encoding="utf-8")
    assert recording_errors(recording_tree) == [
        f"SECURITY.md does not say the Windows app keeps a recording {expected} days at most"]


def test_recording_check_still_requires_windows_source_while_mac_facts_remain_valid(recording_tree):
    paths = recording_tree / APP_SOURCES["windows paths"]
    text = paths.read_text(encoding="utf-8")
    paths.write_text(re.sub(r"\n[^\n]*RecoveryDirectory =>[^\n]*", "", text), encoding="utf-8")
    assert windows_recording_facts(recording_tree)[0] is None
    assert recording_errors(recording_tree) == ["SECURITY.md names a recovery folder the Windows app no longer writes"]
    assert recording_errors(recording_tree) == ["SECURITY.md names a recovery folder the Windows app no longer writes"]
