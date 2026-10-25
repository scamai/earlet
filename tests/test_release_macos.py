"""What tools/release_macos.py signs, submits and refuses, observed in the commands it issues.

A fake Mac stands in for codesign, notarytool, stapler, hdiutil and spctl, so
these tests run on any platform. They prove ordering, entitlements and
refusals; they are not evidence that Apple accepts a build or that the
released app records, recognises or inserts anything.
"""
import json
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
from types import SimpleNamespace

import pytest

from harness import ROOT, tool

IDENTITY = "Developer ID Application: example team (ABCDE12345)"
MH_EXECUTE, MH_DYLIB, MH_BUNDLE = 2, 6, 8
ARM64, X86_64 = 0x0100000C, 0x01000007


# ─── Fixtures ────────────────────────────────────────────────────────────────

def thin(filetype, cpu=ARM64):
    return b"\xcf\xfa\xed\xfe" + struct.pack("<iII", cpu, 0, filetype) + bytes(16)


def fat(*slices):
    """A fat file whose slices start at 4 KiB boundaries."""
    header = b"\xca\xfe\xba\xbe" + struct.pack(">I", len(slices))
    for index, data in enumerate(slices):
        header += struct.pack(">iiIII", 0, 0, 4096 * (index + 1), len(data), 12)
    image = bytearray(header.ljust(4096, b"\0"))
    for index, data in enumerate(slices):
        image[4096 * (index + 1):] = data.ljust(4096, b"\0")
    return bytes(image)


def complete_app(root, mode="complete-preview"):
    """An Earlet.app laid out as tools/package.py lays out a complete package."""
    app = root / "input/Earlet.app"
    (app / "Contents/MacOS").mkdir(parents=True)
    with (app / "Contents/Info.plist").open("wb") as stream:
        plistlib.dump({"CFBundleExecutable": "Dictation", "CFBundleName": "Earlet",
                       "CFBundleShortVersionString": "0.1.0", "DictationBuild": "20261024224346"}, stream)
    (app / "Contents/MacOS/Dictation").write_bytes(thin(MH_EXECUTE))
    resources = app / "Contents/Resources"
    (resources / "python/bin").mkdir(parents=True)
    (resources / "python/bin/python3.11").write_bytes(thin(MH_EXECUTE))
    (resources / "python/bin/python3").symlink_to("python3.11")
    (resources / "python/bin/pip").write_text("#!/usr/bin/env python3\n")
    package = resources / "python/lib/python3.11/site-packages/pkg"
    package.mkdir(parents=True)
    (package / "_native.cpython-311-darwin.so").write_bytes(thin(MH_BUNDLE))
    (package / "Thing.class").write_bytes(b"\xca\xfe\xba\xbe\x00\x00\x00\x41" + bytes(64))
    (resources / "python/lib/libpython3.11.dylib").write_bytes(thin(MH_DYLIB))
    (resources / "package-mode.json").write_text(json.dumps({"mode": mode}))
    return app


class FakeMac:
    """Records every command; acts out the few whose files later steps read."""

    def __init__(self, status="Accepted", hardened=True):
        self.commands, self.status, self.hardened = [], status, hardened

    def __call__(self, command, **kwargs):
        self.commands.append(command)
        stdout = stderr = ""
        if command[0] == "cp":
            shutil.copytree(command[-2], command[-1], symlinks=True)
        elif command[0] in ("ditto", "hdiutil"):
            Path(command[-1]).write_bytes(command[0].encode())
        elif command[:3] == ["xcrun", "notarytool", "submit"]:
            stdout = json.dumps({"id": "submission-1", "status": self.status})
        elif command[:3] == ["xcrun", "notarytool", "log"]:
            Path(command[-1]).write_text("{}")
        elif command[:2] == ["codesign", "--display"]:
            stderr = "flags=0x10000(runtime)\nTimestamp=25 Oct 2026" if self.hardened else "flags=0x0(none)"
        return self.finish(command, 0, stdout, stderr, kwargs)

    @staticmethod
    def finish(command, returncode, stdout, stderr, kwargs):
        # As subprocess.run with check=True, which is the tool's default.
        if returncode and kwargs.get("check", True):
            raise subprocess.CalledProcessError(returncode, command, stdout, stderr)
        return subprocess.CompletedProcess(command, returncode, stdout, stderr)

    def signed(self):
        """(path, entitlements or None) for each signing command, in order."""
        signings = []
        for command in self.commands:
            if command[0] == "codesign" and "--sign" in command and "--options" in command:
                entitlements = command[command.index("--entitlements") + 1] if "--entitlements" in command else None
                signings.append((command[-1], entitlements))
        return signings

    def index(self, *prefix, target=None):
        for position, command in enumerate(self.commands):
            if tuple(command[:len(prefix)]) == prefix and (target is None or any(
                    part.endswith(target) for part in command[len(prefix):])):
                return position
        raise AssertionError(f"never ran {prefix} {target or ''}")


def arguments(tmp_path, app, **overrides):
    values = {"app": app, "output": tmp_path / "release", "identity": IDENTITY,
              "notary_profile": "example-profile", "no_notarize": False}
    values.update(overrides)
    return SimpleNamespace(**values)


def released(tmp_path, mac=None, **overrides):
    mac = mac or FakeMac()
    app = complete_app(tmp_path)
    dmg = tool("release_macos").release(arguments(tmp_path, app, **overrides), mac)
    return mac, app, dmg


# ─── Finding the code ────────────────────────────────────────────────────────

def test_mach_o_files_are_recognised_by_their_header_not_their_name(tmp_path):
    release = tool("release_macos")
    cases = {"exe": thin(MH_EXECUTE), "lib.so": thin(MH_DYLIB), "both": fat(thin(MH_EXECUTE), thin(MH_EXECUTE, X86_64)),
             "Thing.class": b"\xca\xfe\xba\xbe\x00\x00\x00\x41" + bytes(64), "script": b"#!/bin/sh\n", "empty": b""}
    for name, data in cases.items():
        (tmp_path / name).write_bytes(data)
    assert release.macho_slices(tmp_path / "exe") == [(ARM64, MH_EXECUTE)]
    assert release.macho_slices(tmp_path / "lib.so") == [(ARM64, MH_DYLIB)]
    assert release.macho_slices(tmp_path / "both") == [(ARM64, MH_EXECUTE), (X86_64, MH_EXECUTE)]
    for name in ("Thing.class", "script", "empty"):
        assert release.macho_slices(tmp_path / name) == [], name


def test_every_nested_mach_o_is_found_deepest_first_and_the_main_executable_is_left_for_the_bundle(tmp_path):
    app = complete_app(tmp_path)
    found = [(str(path.relative_to(app)), executable) for path, executable in tool("release_macos").nested_code(app)]
    assert found == [
        ("Contents/Resources/python/lib/python3.11/site-packages/pkg/_native.cpython-311-darwin.so", False),
        ("Contents/Resources/python/bin/python3.11", True),
        ("Contents/Resources/python/lib/libpython3.11.dylib", False),
    ]  # not the python3 symlink, the script, the class file or Contents/MacOS/Dictation


# ─── Signing ─────────────────────────────────────────────────────────────────

def test_executables_get_the_worker_entitlements_and_libraries_get_none(tmp_path):
    mac, _app, _dmg = released(tmp_path)
    worker = str(tool("release_macos").WORKER_ENTITLEMENTS)
    nested = {Path(path).name: entitlements for path, entitlements in mac.signed()[:-1]}
    assert nested == {"_native.cpython-311-darwin.so": None, "python3.11": worker, "libpython3.11.dylib": None}


def test_the_app_is_signed_last_with_its_own_entitlements_and_never_deep(tmp_path):
    mac, _app, _dmg = released(tmp_path)
    path, entitlements = mac.signed()[-1]
    assert path.endswith("release/dmg-root/Earlet.app")
    assert entitlements == str(tool("release_macos").APP_ENTITLEMENTS)
    # --deep would re-sign every nested file with the app's entitlements.
    assert not [command for command in mac.commands if "--sign" in command and "--deep" in command]


def test_every_signature_carries_the_hardened_runtime_and_a_timestamp(tmp_path):
    mac, _app, _dmg = released(tmp_path)
    signings = [command for command in mac.commands if command[0] == "codesign" and "--sign" in command]
    assert len(signings) == 5  # three nested files, the app, the image
    for command in signings:
        assert "--timestamp" in command, command
        if not command[-1].endswith(".dmg"):
            assert "--options" in command and command[command.index("--options") + 1] == "runtime", command


def test_a_file_without_the_hardened_runtime_stops_the_release(tmp_path):
    with pytest.raises(RuntimeError, match="lack the hardened runtime"):
        released(tmp_path, FakeMac(hardened=False))


def test_the_input_app_is_never_modified(tmp_path):
    mac, app, _dmg = released(tmp_path)
    touched = [command for command in mac.commands if command[0] != "cp" and str(app) in " ".join(command)]
    assert touched == []


# ─── Refusals ────────────────────────────────────────────────────────────────

@pytest.mark.parametrize("case, overrides, message", [
    ("output exists", {}, "Output already exists"),
    ("app-only", {"mode": "app-only"}, "Only a complete package"),
    ("ad hoc", {"identity": "-"}, "Developer ID Application"),
    ("development", {"identity": "Apple Development: someone (ABCDE12345)"}, "Developer ID Application"),
    ("no profile", {"notary_profile": None}, "--notary-profile"),
])
def test_what_cannot_become_a_notarized_release_is_refused(tmp_path, case, overrides, message):
    overrides = dict(overrides)
    app = complete_app(tmp_path, mode=overrides.pop("mode", "complete-preview"))
    if case == "output exists":
        (tmp_path / "release").mkdir()
    mac = FakeMac()
    with pytest.raises(ValueError, match=message):
        tool("release_macos").release(arguments(tmp_path, app, **overrides), mac)
    assert mac.commands == []


def test_signing_only_needs_no_notary_profile_and_says_it_is_not_notarized(tmp_path):
    mac, _app, dmg = released(tmp_path, notary_profile=None, no_notarize=True)
    receipt = json.loads((tmp_path / "release/release-receipt.json").read_text())
    assert receipt["notarized"] is False and "not notarized" in receipt["validation"]
    assert not [command for command in mac.commands if command[:2] == ["xcrun", "notarytool"]]
    assert dmg.is_file()


# ─── Notarization and the image ──────────────────────────────────────────────

def test_a_rejected_submission_fails_and_keeps_apples_log(tmp_path):
    with pytest.raises(RuntimeError, match="did not accept"):
        released(tmp_path, FakeMac(status="Invalid"))
    assert (tmp_path / "release/notary-Earlet-app.zip.json").is_file()


def test_a_log_the_keychain_will_not_fetch_does_not_stop_an_accepted_release(tmp_path):
    class Forgetful(FakeMac):
        def __call__(self, command, **kwargs):
            if command[:3] == ["xcrun", "notarytool", "log"]:
                self.commands.append(command)
                return self.finish(command, 69, "", "Error: No Keychain password item found for profile", kwargs)
            return super().__call__(command, **kwargs)
    mac, _app, dmg = released(tmp_path, Forgetful())
    receipt = json.loads((tmp_path / "release/release-receipt.json").read_text())
    assert receipt["notarized"] is True and dmg.is_file()
    assert "No Keychain password item" in receipt["dmg_notarization"]["log_error"]
    assert len([command for command in mac.commands if command[:3] == ["xcrun", "notarytool", "log"]]) == 6


def test_a_notarytool_failure_without_an_answer_fails(tmp_path):
    class Silent(FakeMac):
        def __call__(self, command, **kwargs):
            done = super().__call__(command, **kwargs)
            if command[:3] == ["xcrun", "notarytool", "submit"]:
                return subprocess.CompletedProcess(command, 1, "", "no keychain profile")
            return done
    with pytest.raises(RuntimeError, match="no keychain profile"):
        released(tmp_path, Silent())


def test_the_app_is_notarized_and_stapled_before_it_goes_into_the_image(tmp_path):
    mac, _app, _dmg = released(tmp_path)
    app_submitted = mac.index("xcrun", "notarytool", "submit", target="Earlet-app.zip")
    app_stapled = mac.index("xcrun", "stapler", "staple", target="Earlet.app")
    imaged = mac.index("hdiutil", "create")
    assert app_submitted < app_stapled < imaged


def test_the_image_is_signed_then_notarized_then_stapled_then_assessed(tmp_path):
    mac, _app, dmg = released(tmp_path)
    order = [mac.index("codesign", "--force", "--timestamp", "--sign", target=".dmg"),
             mac.index("xcrun", "notarytool", "submit", target=".dmg"),
             mac.index("xcrun", "stapler", "staple", target=".dmg"),
             mac.index("spctl", "--assess", "--type", "open", target=".dmg")]
    assert order == sorted(order)
    assert dmg.name == "Earlet-0.1.0-macos-arm64.dmg"


def test_the_receipt_names_the_image_and_what_was_not_checked(tmp_path):
    _mac, _app, dmg = released(tmp_path)
    receipt = json.loads((tmp_path / "release/release-receipt.json").read_text())
    assert receipt["notarized"] is True
    assert receipt["dmg"]["name"] == dmg.name and len(receipt["dmg"]["sha256"]) == 64
    assert receipt["app_notarization"]["status"] == receipt["dmg_notarization"]["status"] == "Accepted"
    assert receipt["build"] == "20261024224346"
    assert "no clean-machine, microphone or insertion acceptance" in receipt["validation"]
    assert not (tmp_path / "release/Earlet-app.zip").exists()


# ─── Entitlements ────────────────────────────────────────────────────────────

def test_only_the_app_may_record_and_only_the_interpreter_relaxes_code_signing():
    app = plistlib.loads((ROOT / "macos/Earlet.entitlements").read_bytes())
    worker = plistlib.loads((ROOT / "macos/EarletWorker.entitlements").read_bytes())
    # Under the hardened runtime the microphone stays silent without this key.
    assert app == {"com.apple.security.device.audio-input": True}
    assert worker == {"com.apple.security.cs.allow-jit": True,
                      "com.apple.security.cs.allow-unsigned-executable-memory": True,
                      "com.apple.security.cs.disable-library-validation": True}
    for text in ((ROOT / "macos/Earlet.entitlements").read_text(), (ROOT / "macos/EarletWorker.entitlements").read_text()):
        assert "<!--" not in text  # the kernel's entitlement parser rejects comments
