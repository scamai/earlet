#!/usr/bin/env python3
"""Turn a complete macOS package into a notarized disk image people can download.

The input is an Earlet.app that tools/package.py built as a complete package.
It is never modified: the app is copied into a fresh output folder, every
Mach-O file inside it is signed with the hardened runtime and a secure
timestamp, then the app and its disk image are each notarized by Apple and
stapled. The app carries its own ticket so it still opens offline after it is
dragged out of the image.

A release needs a Developer ID Application identity and a notarytool keychain
profile for the same team (`xcrun notarytool store-credentials`). The receipt
records what was signed and what Apple answered; it is not microphone,
insertion or clean-machine acceptance.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
from typing import Callable

ROOT = Path(__file__).resolve().parents[1]
# The app's own executable records the microphone; the bundled interpreter
# loads third-party extension modules and a numerical JIT. Each set is in
# docs/development.md#releasing-a-mac-build with the reason for every key.
APP_ENTITLEMENTS = ROOT / "macos/Earlet.entitlements"
WORKER_ENTITLEMENTS = ROOT / "macos/EarletWorker.entitlements"

Runner = Callable[..., subprocess.CompletedProcess]


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(command, **{"check": True, "text": True, **kwargs})


# ─── Mach-O discovery ────────────────────────────────────────────────────────

THIN_MAGIC = {b"\xcf\xfa\xed\xfe": "<", b"\xce\xfa\xed\xfe": "<",
              b"\xfe\xed\xfa\xcf": ">", b"\xfe\xed\xfa\xce": ">"}
FAT_MAGIC = b"\xca\xfe\xba\xbe"
FAT_MAGIC_64 = b"\xca\xfe\xba\xbf"
MH_EXECUTE = 2
CPU_NAMES = {0x0100000C: "arm64", 0x01000007: "x86_64"}


def macho_slices(path: Path) -> list[tuple[int, int]]:
    """(cputype, filetype) for each architecture in ``path``; empty when it is not Mach-O."""
    try:
        with path.open("rb") as stream:
            head = stream.read(8)
            if head[:4] in (FAT_MAGIC, FAT_MAGIC_64):
                # A Java class file shares 0xcafebabe; read as a fat header it
                # names no slice that starts with a Mach-O header, so it yields none.
                count = struct.unpack(">I", head[4:8])[0]
                wide = head[:4] == FAT_MAGIC_64
                offsets = []
                for _ in range(count):
                    entry = stream.read(32 if wide else 20)
                    offsets.append(struct.unpack(">iiQQII" if wide else ">iiIII", entry)[2])
                slices = []
                for offset in offsets:
                    stream.seek(offset)
                    slices.extend(thin_header(stream.read(16)))
                return slices
            stream.seek(0)
            return thin_header(stream.read(16))
    except (OSError, struct.error):
        return []


def thin_header(head: bytes) -> list[tuple[int, int]]:
    order = THIN_MAGIC.get(head[:4])
    if order is None or len(head) < 16:
        return []
    cputype, _subtype, filetype = struct.unpack(order + "iII", head[4:16])
    return [(cputype & 0xFFFFFFFF, filetype)]


def nested_code(app: Path) -> list[tuple[Path, bool]]:
    """Every Mach-O file under ``app`` except its main executable, as (path, is_executable).

    Ordered deepest first, so anything a later signature seals is already final.
    """
    main = main_executable(app)
    found = []
    for path in app.rglob("*"):
        if path.is_symlink() or not path.is_file() or path == main:
            continue
        slices = macho_slices(path)
        if slices:
            found.append((path, any(filetype == MH_EXECUTE for _cpu, filetype in slices)))
    return sorted(found, key=lambda item: (-len(item[0].parts), str(item[0])))


def main_executable(app: Path) -> Path:
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    return app / "Contents/MacOS" / info["CFBundleExecutable"]


def architecture(app: Path) -> str:
    names = sorted({CPU_NAMES.get(cpu, hex(cpu)) for cpu, _type in macho_slices(main_executable(app))})
    return "universal" if names == ["arm64", "x86_64"] else "-".join(names)


# ─── Inputs ──────────────────────────────────────────────────────────────────

def check_inputs(app: Path, output: Path, identity: str, profile: str | None, notarize: bool) -> dict:
    """Refuse anything that cannot become a notarized release; return the app's Info.plist."""
    if output.exists():
        raise ValueError("Output already exists; select a fresh directory to preserve prior releases")
    if not (app / "Contents/Info.plist").is_file():
        raise ValueError(f"{app} is not an app bundle")
    mode_file = app / "Contents/Resources/package-mode.json"
    mode = json.loads(mode_file.read_text()).get("mode") if mode_file.is_file() else None
    if mode != "complete-preview":
        raise ValueError("Only a complete package (bundled Python and model) can be released; "
                         f"this one is {mode or 'unmarked'}")
    if not identity.startswith("Developer ID Application:"):
        raise ValueError("A downloadable release needs a 'Developer ID Application: ...' identity; "
                         "ad-hoc and Apple Development signatures cannot be notarized")
    if notarize and not profile:
        raise ValueError("Notarization needs --notary-profile or DICTATION_NOTARY_PROFILE")
    for entitlements in (APP_ENTITLEMENTS, WORKER_ENTITLEMENTS):
        plistlib.loads(entitlements.read_bytes())  # malformed entitlements fail here, not in the kernel
    return plistlib.loads((app / "Contents/Info.plist").read_bytes())


# ─── Signing ─────────────────────────────────────────────────────────────────

def codesign(identity: str, path: Path, entitlements: Path | None = None) -> list[str]:
    command = ["codesign", "--force", "--options", "runtime", "--timestamp", "--sign", identity]
    if entitlements is not None:
        command += ["--entitlements", str(entitlements)]
    return command + [str(path)]


def sign_app(app: Path, identity: str, runner: Runner = run) -> dict:
    """Sign nested code inside-out, then the app; return counts for the receipt."""
    nested = nested_code(app)
    for path, executable in nested:
        # Entitlements belong to processes: the interpreter and the few helper
        # executables get the worker set, libraries get none.
        runner(codesign(identity, path, WORKER_ENTITLEMENTS if executable else None))
    runner(codesign(identity, app, APP_ENTITLEMENTS))
    runner(["codesign", "--verify", "--deep", "--strict", "--verbose=2", str(app)])
    return {"nested_executables": sum(1 for _p, executable in nested if executable),
            "nested_libraries": sum(1 for _p, executable in nested if not executable)}


def check_signed(app: Path, runner: Runner = run) -> None:
    """Every Mach-O must carry the hardened runtime and a secure timestamp, as the notary requires."""
    unsigned = []
    for path in [main_executable(app)] + [path for path, _executable in nested_code(app)]:
        details = runner(["codesign", "--display", "--verbose=4", str(path)], capture_output=True).stderr
        if "(runtime)" not in details or "Timestamp=" not in details:
            unsigned.append(str(path.relative_to(app)))
    if unsigned:
        raise RuntimeError(f"{len(unsigned)} files lack the hardened runtime or a timestamp: {unsigned[:5]}")


# ─── Notarization ────────────────────────────────────────────────────────────

def notarize(path: Path, profile: str, logs: Path, runner: Runner = run) -> dict:
    """Submit ``path`` and wait; keep Apple's log beside the receipt; fail unless Accepted."""
    # Not check=True: a rejected submission must still reach the log download below.
    done = runner(["xcrun", "notarytool", "submit", str(path), "--keychain-profile", profile,
                   "--wait", "--output-format", "json"], capture_output=True, check=False)
    try:
        answer = json.loads(done.stdout)
    except (TypeError, ValueError):
        raise RuntimeError(f"notarytool gave no answer for {path.name}: {(done.stderr or '').strip()}") from None
    submission = {"id": answer.get("id"), "status": answer.get("status")}
    if submission["id"]:
        log = logs / f"notary-{path.name}.json"
        # The log is evidence, not the verdict. The keychain can refuse the
        # profile for a moment ("No Keychain password item found"), which once
        # stopped an accepted release here; retry, then record the failure.
        for _attempt in range(3):
            fetched = runner(["xcrun", "notarytool", "log", submission["id"], "--keychain-profile", profile,
                              str(log)], capture_output=True, check=False)
            if fetched.returncode == 0:
                submission["log"] = log.name
                break
        else:
            submission["log_error"] = (fetched.stderr or "").strip()[:200]
    if submission["status"] != "Accepted":
        raise RuntimeError(f"Apple did not accept {path.name}: {submission}")
    return submission


def staple(path: Path, runner: Runner = run) -> None:
    runner(["xcrun", "stapler", "staple", str(path)], capture_output=True)
    runner(["xcrun", "stapler", "validate", str(path)], capture_output=True)


# ─── Disk image ──────────────────────────────────────────────────────────────

def make_dmg(root: Path, dmg: Path, volume: str, identity: str, runner: Runner = run) -> None:
    """Image ``root`` (the app beside an Applications link), compressed, then sign the image."""
    link = root / "Applications"
    if not link.exists():
        link.symlink_to("/Applications")
    # LZMA: the bundled interpreter compresses well and macOS 14, the minimum,
    # reads it. The model weights barely compress whatever the format.
    runner(["hdiutil", "create", "-volname", volume, "-srcfolder", str(root), "-format", "ULMO", str(dmg)],
           capture_output=True)
    # An image is assessed as "open", and an unsigned one fails that even with a ticket.
    runner(["codesign", "--force", "--timestamp", "--sign", identity, str(dmg)])
    runner(["codesign", "--verify", "--strict", "--verbose=2", str(dmg)])


def gatekeeper(app: Path, dmg: Path, runner: Runner = run) -> dict:
    checks = {
        "dmg_open": ["spctl", "--assess", "--type", "open", "--context", "context:primary-signature",
                     "--verbose=2", str(dmg)],
        "app_execute": ["spctl", "--assess", "--type", "execute", "--verbose=2", str(app)],
        "syspolicy_distribution": ["syspolicy_check", "distribution", str(app)],
    }
    results = {}
    for name, command in checks.items():
        done = runner(command, capture_output=True)
        results[name] = (done.stderr or done.stdout).strip().splitlines()[-1:] or ["passed"]
    return results


def digest(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            sha.update(chunk)
    return sha.hexdigest()


# ─── Release ─────────────────────────────────────────────────────────────────

def release(args: argparse.Namespace, runner: Runner = run) -> Path:
    info = check_inputs(args.app, args.output, args.identity, args.notary_profile, not args.no_notarize)
    name, version = info["CFBundleName"], info["CFBundleShortVersionString"]
    root = args.output / "dmg-root"
    root.mkdir(parents=True)
    app = root / args.app.name
    # An APFS clone: only the Mach-O files that signing rewrites take new space.
    runner(["cp", "-c", "-p", "-R", str(args.app), str(app)])
    counts = sign_app(app, args.identity, runner)
    check_signed(app, runner)
    dmg = args.output / f"{name}-{version}-macos-{architecture(app)}.dmg"
    receipt = {"product": name, "version": version, "build": info.get("DictationBuild"),
               "identity": args.identity, **counts,
               "entitlements": {"app": digest(APP_ENTITLEMENTS), "worker": digest(WORKER_ENTITLEMENTS)}}
    if args.no_notarize:
        make_dmg(root, dmg, name, args.identity, runner)
        receipt["notarized"] = False
    else:
        archive = args.output / f"{name}-app.zip"
        runner(["ditto", "-c", "-k", "--keepParent", str(app), str(archive)])
        receipt["app_notarization"] = notarize(archive, args.notary_profile, args.output, runner)
        archive.unlink()
        staple(app, runner)
        make_dmg(root, dmg, name, args.identity, runner)
        receipt["dmg_notarization"] = notarize(dmg, args.notary_profile, args.output, runner)
        staple(dmg, runner)
        receipt["gatekeeper"] = gatekeeper(app, dmg, runner)
        receipt["notarized"] = True
    receipt["dmg"] = {"name": dmg.name, "bytes": dmg.stat().st_size, "sha256": digest(dmg)}
    receipt["validation"] = ("signed, notarized and stapled" if receipt["notarized"] else "signed, not notarized") + \
        "; no clean-machine, microphone or insertion acceptance"
    (args.output / "release-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return dmg


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--app", type=Path, required=True, help="complete Earlet.app from tools/package.py")
    parser.add_argument("--output", type=Path, required=True, help="new folder for the image and receipt")
    parser.add_argument("--identity", default=os.environ.get("DICTATION_SIGN_IDENTITY", ""))
    parser.add_argument("--notary-profile", default=os.environ.get("DICTATION_NOTARY_PROFILE"))
    parser.add_argument("--no-notarize", action="store_true",
                        help="sign and image only, to check a build before submitting it")
    args = parser.parse_args(argv)
    try:
        dmg = release(args)
    except (ValueError, RuntimeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    except subprocess.CalledProcessError as error:
        print(f"error: {' '.join(error.cmd)} exited {error.returncode}\n{error.stderr or ''}", file=sys.stderr)
        return 1
    print(dmg)
    return 0


if __name__ == "__main__":
    sys.exit(main())
