"""What tools/release_macos.py signs, submits and refuses, observed in the commands it issues.

A fake Mac stands in for codesign, notarytool, stapler, hdiutil and spctl, so
these tests run on any platform. They prove ordering, entitlements and
refusals; they are not evidence that Apple accepts a build or that the
released app records, recognises or inserts anything.
"""
import base64
import hashlib
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
# Ed25519 keys and signatures from RFC 8032 §7.1 (TEST 1, 2 and 3): the
# fake Mac signs the feed with the first; the second is another key.
RFC8032 = [
    ("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
     "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
     "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
    ("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
     "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
     "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
    ("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
     "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
     "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"),
]
FEED_SEED, FEED_PUBLIC = bytes.fromhex(RFC8032[0][0]), bytes.fromhex(RFC8032[0][1])
OTHER_SEED = bytes.fromhex(RFC8032[1][0])
# Loaded once, before any mutation test replaces the tool: the fake Mac's
# signer must not change with the tool it checks.
ED = tool("release_macos")


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


def complete_app(root, mode="complete-preview", sparkle=False, signed_feed=True):
    """An Earlet.app laid out as tools/package.py lays out a complete package;
    with ``sparkle``, also the updater framework the packager embeds and the
    feed key, which the app requires the feed to be signed with unless
    ``signed_feed`` is false."""
    app = root / "input/Earlet.app"
    (app / "Contents/MacOS").mkdir(parents=True)
    info = {"CFBundleExecutable": "Dictation", "CFBundleName": "Earlet",
            "CFBundleShortVersionString": "0.1.0", "DictationBuild": "20261024224346"}
    if sparkle:
        info |= {"SUPublicEDKey": base64.b64encode(FEED_PUBLIC).decode(), "SURequireSignedFeed": signed_feed}
    with (app / "Contents/Info.plist").open("wb") as stream:
        plistlib.dump(info, stream)
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
    if sparkle:
        version = app / "Contents/Frameworks/Sparkle.framework/Versions/B"
        (version / "Updater.app/Contents/MacOS").mkdir(parents=True)
        (version / "Sparkle").write_bytes(thin(MH_DYLIB))
        (version / "Autoupdate").write_bytes(thin(MH_EXECUTE))
        (version / "Updater.app/Contents/MacOS/Updater").write_bytes(thin(MH_EXECUTE))
        (version.parent / "Current").symlink_to("B")
        (version.parent.parent / "Sparkle").symlink_to("Versions/Current/Sparkle")
    return app


class FakeMac:
    """Records every command; acts out the few whose files later steps read."""

    def __init__(self, status="Accepted", hardened=True, unsigned_feed=False, feed_seed=FEED_SEED,
                 altered_feed=False, honours_critical=True):
        self.commands, self.status, self.hardened = [], status, hardened
        self.unsigned_feed, self.no_key, self.appcast_input = unsigned_feed, False, None
        # The feed's own signature: made with ``feed_seed`` (none when None),
        # over the feed, or over a feed changed afterwards.
        self.feed_seed, self.altered_feed, self.honours_critical = feed_seed, altered_feed, honours_critical

    def __call__(self, command, **kwargs):
        self.commands.append(command)
        stdout = stderr = ""
        if command[0] == "cp" and Path(command[-2]).is_dir():
            shutil.copytree(command[-2], command[-1], symlinks=True)
        elif command[0] == "cp":
            shutil.copy2(command[-2], command[-1])
        elif Path(command[0]).name == "generate_appcast":
            self.generate_appcast(command)
            self.appcast_input = kwargs.get("input")
        elif Path(command[0]).name == "generate_keys":
            Path(command[-1]).write_text("" if self.no_key else "c2VjcmV0LXNlZWQ=\n")
        elif command[0] in ("ditto", "hdiutil"):
            Path(command[-1]).write_bytes(command[0].encode())
        elif command[:3] == ["xcrun", "notarytool", "submit"]:
            stdout = json.dumps({"id": "submission-1", "status": self.status})
        elif command[:3] == ["xcrun", "notarytool", "log"]:
            Path(command[-1]).write_text("{}")
        elif command[:2] == ["codesign", "--display"]:
            stderr = "flags=0x10000(runtime)\nTimestamp=25 Oct 2026" if self.hardened else "flags=0x0(none)"
        return self.finish(command, 0, stdout, stderr, kwargs)

    def generate_appcast(self, command):
        """Writes what Sparkle's tool writes: a delta from each older image and a feed naming them."""
        updates = Path(command[-1])
        prefix = command[command.index("--download-url-prefix") + 1]
        images = sorted(updates.glob("*.dmg"))
        newest, older = images[-1], images[:-1]
        deltas = [updates / f"Earlet2-{old.stem}.delta" for old in older]
        for delta in deltas:
            delta.write_bytes(b"delta")
        signature = "" if self.unsigned_feed else ' sparkle:edSignature="c2lnbmF0dXJl"'
        items = "".join(f'<enclosure url="{prefix}{path.name}"{signature}/>' for path in [newest, *deltas])
        if "--critical-update-version" in command and self.honours_critical:
            items += "<sparkle:criticalUpdate/>"
        content = f"<rss><channel><item>{items}</item></channel></rss>\n".encode()
        feed = content
        if self.feed_seed is not None:
            # As generate_appcast signs a feed the app requires signed.
            edSignature = base64.b64encode(ed25519_sign(self.feed_seed, content)).decode()
            if self.altered_feed:
                content = content.replace(b"<item>", b"<item><title>changed</title>")
            feed = content + f"<!-- sparkle-signatures:\nedSignature: {edSignature}\nlength: {len(content)}\n-->\n".encode()
        Path(command[command.index("-o") + 1]).write_bytes(feed)

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


def ed25519_sign(seed: bytes, message: bytes) -> bytes:
    """RFC 8032 §5.1.6, for the fake Mac: the release tool only verifies."""
    def encode(point):
        inverse = pow(point[2], ED.ED_P - 2, ED.ED_P)
        x, y = point[0] * inverse % ED.ED_P, point[1] * inverse % ED.ED_P
        return (y | ((x & 1) << 255)).to_bytes(32, "little")
    expanded = hashlib.sha512(seed).digest()
    secret = (int.from_bytes(expanded[:32], "little") & ((1 << 254) - 8)) | (1 << 254)
    public = encode(ED.ed_mul(secret, ED.ED_BASE))
    nonce = int.from_bytes(hashlib.sha512(expanded[32:] + message).digest(), "little") % ED.ED_L
    commitment = encode(ED.ed_mul(nonce, ED.ED_BASE))
    challenge = int.from_bytes(hashlib.sha512(commitment + public + message).digest(), "little") % ED.ED_L
    return commitment + ((nonce + challenge * secret) % ED.ED_L).to_bytes(32, "little")


def arguments(tmp_path, app, **overrides):
    values = {"app": app, "output": tmp_path / "release", "identity": IDENTITY,
              "notary_profile": "example-profile", "no_notarize": False, "appcast": False, "previous_dmg": [],
              "critical_update": False}
    values.update(overrides)
    return SimpleNamespace(**values)


def released(tmp_path, mac=None, sparkle=False, module=None, signed_feed=True, **overrides):
    mac = mac or FakeMac()
    app = complete_app(tmp_path, sparkle=sparkle, signed_feed=signed_feed)
    dmg = (module or tool("release_macos")).release(arguments(tmp_path, app, **overrides), mac)
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


# ─── The updater framework ───────────────────────────────────────────────────

def test_sparkle_is_sealed_inside_out_before_the_app_with_no_worker_entitlements(tmp_path):
    mac, _app, _dmg = released(tmp_path, sparkle=True)
    signed = [(path.split("dmg-root/Earlet.app/")[-1], entitlements) for path, entitlements in mac.signed()]
    sparkle = [(path, entitlements) for path, entitlements in signed if "Sparkle.framework" in path]
    assert sparkle == [
        ("Contents/Frameworks/Sparkle.framework/Versions/B/Updater.app/Contents/MacOS/Updater", None),
        ("Contents/Frameworks/Sparkle.framework/Versions/B/Autoupdate", None),
        ("Contents/Frameworks/Sparkle.framework/Versions/B/Sparkle", None),
        ("Contents/Frameworks/Sparkle.framework/Versions/B/Updater.app", None),
        ("Contents/Frameworks/Sparkle.framework", None),
    ]  # never again through Versions/Current
    order = [path for path, _entitlements in signed]
    assert order.index("Contents/Frameworks/Sparkle.framework") < order.index(order[-1])
    assert order[-1].endswith("Earlet.app")
    receipt = json.loads((tmp_path / "release/release-receipt.json").read_text())
    assert receipt["nested_bundles"] == ["Contents/Frameworks/Sparkle.framework/Versions/B/Updater.app",
                                         "Contents/Frameworks/Sparkle.framework"]


def appcast_module(tmp_path):
    """The release tool, its Sparkle tools replaced by a stand-in the fake Mac acts out."""
    bin_dir = tmp_path / "sparkle-bin"
    bin_dir.mkdir()
    (bin_dir / "generate_appcast").write_text("#!/bin/sh\n")
    module = tool("release_macos")
    module.SPARKLE_BIN = bin_dir
    return module


@pytest.fixture
def appcast_tool(tmp_path):
    return appcast_module(tmp_path)


def test_the_feed_names_only_this_release_and_its_deltas(tmp_path, appcast_tool):
    old = tmp_path / "Earlet-0.0.9-macos-arm64.dmg"
    old.write_bytes(b"previous image")
    mac, _app, dmg = released(tmp_path, sparkle=True, module=appcast_tool, appcast=True, previous_dmg=[old])
    command = next(c for c in mac.commands if Path(c[0]).name == "generate_appcast")
    assert command[0] == str(appcast_tool.SPARKLE_BIN / "generate_appcast")
    option = lambda name: command[command.index(name) + 1]
    keys = next(c for c in mac.commands if Path(c[0]).name == "generate_keys")
    assert keys[1:3] == ["--account", json.loads((ROOT / "product.json").read_text())["updates"]["keychain_account"]]
    # The key reaches generate_appcast on standard input, never as an argument
    # and never left in a file.
    assert option("--ed-key-file") == "-" and mac.appcast_input == "c2VjcmV0LXNlZWQ="
    assert not Path(keys[-1]).parent.exists()
    assert "c2VjcmV0LXNlZWQ=" not in " ".join(" ".join(c) for c in mac.commands)
    assert option("--download-url-prefix") == "https://github.com/scamai/earlet/releases/download/v0.1.0/"
    assert option("--maximum-versions") == "1"
    assert "--critical-update-version" not in command
    receipt = json.loads((tmp_path / "release/release-receipt.json").read_text())["appcast"]
    assert receipt["critical"] is False
    names = [asset["name"] for asset in receipt["assets"]]
    assert names == sorted(["appcast.xml", dmg.name, f"Earlet2-{old.stem}.delta"])
    assert old.name not in names and receipt["deltas_from"] == [old.name]
    assert receipt["publish"][:4] == ["gh", "release", "create", "v0.1.0"]
    assert "--repo" in receipt["publish"] and "github.com/scamai/earlet" in receipt["publish"]


def test_an_unsigned_feed_stops_the_release(tmp_path, appcast_tool):
    with pytest.raises(RuntimeError, match="unsigned"):
        released(tmp_path, FakeMac(unsigned_feed=True), sparkle=True, module=appcast_tool, appcast=True)


def test_a_feed_without_sparkles_tools_or_with_a_bad_previous_image_is_refused(tmp_path, appcast_tool):
    with pytest.raises(ValueError, match="not a previous release image"):
        released(tmp_path, sparkle=True, module=appcast_tool, appcast=True, previous_dmg=[tmp_path / "missing.dmg"])
    appcast_tool.SPARKLE_BIN = tmp_path / "nowhere"
    with pytest.raises(ValueError, match="resolve the Swift package"):
        released(tmp_path / "again", sparkle=True, module=appcast_tool, appcast=True)


def test_a_keychain_without_the_signing_key_stops_the_release(tmp_path, appcast_tool):
    mac = FakeMac()
    mac.no_key = True
    with pytest.raises(RuntimeError, match="no update signing key"):
        released(tmp_path, mac, sparkle=True, module=appcast_tool, appcast=True)


# ─── The feed's own signature ────────────────────────────────────────────────

@pytest.mark.parametrize("seed, public, message, signature", RFC8032)
def test_the_feed_check_verifies_ed25519_as_rfc_8032_specifies(seed, public, message, signature):
    public, message, signature = bytes.fromhex(public), bytes.fromhex(message), bytes.fromhex(signature)
    module = tool("release_macos")
    assert module.ed25519_verify(public, message, signature)
    # The fake Mac's signer is the reference algorithm too: deterministic, so
    # it reproduces the RFC's signature exactly.
    assert ed25519_sign(bytes.fromhex(seed), message) == signature
    changed = bytes([signature[0] ^ 1]) + signature[1:]
    malleable = signature[:32] + (int.from_bytes(signature[32:], "little") + module.ED_L).to_bytes(32, "little")
    assert not module.ed25519_verify(public, message + b"x", signature)
    assert not module.ed25519_verify(public, message, changed)
    assert not module.ed25519_verify(public, message, malleable)
    assert not module.ed25519_verify(bytes.fromhex(RFC8032[2][1]) if public == FEED_PUBLIC else FEED_PUBLIC,
                                     message, signature)
    assert not module.ed25519_verify(public, message, signature[:63])


def test_the_published_feed_is_signed_with_the_key_the_app_carries(tmp_path):
    module = appcast_module(tmp_path)
    released(tmp_path, sparkle=True, module=module, appcast=True)
    feed = (tmp_path / "release/updates/appcast.xml").read_bytes()
    assert module.feed_signature_valid(feed, FEED_PUBLIC)
    assert feed.rstrip().endswith(b"-->")


@pytest.mark.parametrize("replacement", [b" edSignature:", b"edSignature :", b"\xff\nedSignature:",
                                           b"\x1cedSignature:", b"edSignature:\x1c"],
                         ids=["indented-key", "space-before-colon", "invalid-utf8", "control-line", "control-value"])
def test_feed_metadata_sparkle_cannot_parse_is_rejected(tmp_path, replacement):
    module = appcast_module(tmp_path)
    released(tmp_path, sparkle=True, module=module, appcast=True)
    feed = (tmp_path / "release/updates/appcast.xml").read_bytes()
    assert module.feed_signature_valid(feed, FEED_PUBLIC)
    # Only unsigned metadata changes: the signed content and signature stay valid.
    altered = feed.replace(b"\nedSignature:", b"\n" + replacement)
    assert altered != feed
    assert not module.feed_signature_valid(altered, FEED_PUBLIC)


@pytest.mark.parametrize("separator", [b"\r", b"\xc2\x85", b"\xe2\x80\xa8", b"\xe2\x80\xa9"])
def test_a_hidden_last_signature_cannot_bypass_sparkles_line_parser(tmp_path, separator):
    module = appcast_module(tmp_path)
    released(tmp_path, sparkle=True, module=module, appcast=True)
    feed = (tmp_path / "release/updates/appcast.xml").read_bytes()
    start, end = feed.rsplit(b"-->", 1)
    altered = start + b"other:" + separator + b"edSignature:invalid\n-->" + end
    assert module.feed_signature_valid(feed, FEED_PUBLIC)
    assert not module.feed_signature_valid(altered, FEED_PUBLIC)


@pytest.mark.parametrize("mac_options", [{"feed_seed": None}, {"feed_seed": OTHER_SEED}, {"altered_feed": True}],
                         ids=["no feed signature", "another key", "changed after signing"])
def test_a_feed_the_app_would_not_read_stops_the_release(tmp_path, mac_options):
    with pytest.raises(RuntimeError, match="own signature"):
        released(tmp_path, FakeMac(**mac_options), sparkle=True, module=appcast_module(tmp_path), appcast=True)


def test_an_app_that_would_read_an_unsigned_feed_is_refused_before_anything_is_signed(tmp_path):
    mac = FakeMac()
    with pytest.raises(ValueError, match="SURequireSignedFeed"):
        released(tmp_path, mac, sparkle=True, module=appcast_module(tmp_path), signed_feed=False, appcast=True)
    assert mac.commands == []


def test_an_app_without_a_whole_feed_key_is_refused_before_anything_is_signed(tmp_path):
    module = tool("release_macos")
    for key in ("", "short", "A" * 43 + "=" * 2):
        with pytest.raises(ValueError, match="SUPublicEDKey"):
            module.feed_public_key({"SURequireSignedFeed": True, "SUPublicEDKey": key})
    assert module.feed_public_key({"SURequireSignedFeed": True,
                                   "SUPublicEDKey": base64.b64encode(FEED_PUBLIC).decode()}) == FEED_PUBLIC


# ─── Security releases ───────────────────────────────────────────────────────

def test_a_security_release_is_marked_critical_for_every_older_copy(tmp_path):
    module = appcast_module(tmp_path)
    mac, _app, _dmg = released(tmp_path, sparkle=True, module=module, appcast=True, critical_update=True)
    command = next(c for c in mac.commands if Path(c[0]).name == "generate_appcast")
    # An empty version: critical for a copy of any older version.
    assert command[command.index("--critical-update-version") + 1] == ""
    assert b"<sparkle:criticalUpdate/>" in (tmp_path / "release/updates/appcast.xml").read_bytes()
    assert json.loads((tmp_path / "release/release-receipt.json").read_text())["appcast"]["critical"] is True


def test_a_security_release_whose_feed_is_not_marked_critical_stops(tmp_path):
    with pytest.raises(RuntimeError, match="critical"):
        released(tmp_path, FakeMac(honours_critical=False), sparkle=True, module=appcast_module(tmp_path),
                 appcast=True, critical_update=True)


def test_a_critical_mark_without_a_feed_is_refused(tmp_path):
    mac = FakeMac()
    with pytest.raises(ValueError, match="--appcast"):
        released(tmp_path, mac, sparkle=True, critical_update=True)
    assert mac.commands == []
