#!/usr/bin/env bash
# Builds and tests the platform-free DictationLearning target (LEARNING.md) on
# any host with a Swift toolchain, Linux included.
#
#   macos/tools/test-learning-on-linux.sh [RUN_DIR]
#   macos/tools/test-learning-on-linux.sh --mutations [RUN_DIR]
#   macos/tools/test-learning-on-linux.sh --self-test
#
# `swift test` on macos/Package.swift cannot build on Linux: every other target
# needs AppKit. So this generates a scratch package in a temporary directory
# that holds ONLY DictationLearning and DictationLearningTests (symlinks to the
# real directories), points DICTATION_LEARNING_GOLDEN at the shared golden
# cases, and runs `swift test` there. It exits with swift test's status and
# removes the temporary directory. With RUN_DIR, the XCTest results are written
# to RUN_DIR/swift-learning.xml (xunit) and the output to swift-learning.log.
#
# SwiftPM writes that xunit file only from its parallel runner, which records
# a test that called XCTSkip as passed. So a serial `swift test --skip-build`
# follows, and every test it reports skipped is marked <skipped/> in the file.
# The file must exist whenever the tests ran, passed or failed: a run that
# leaves none exits non-zero, since the counts would otherwise be lost.
#
# When the Windows word list is present it is unpacked for the run and passed
# as DICTATION_LEARNING_WEB2, so the curated 235,642-line Windows lexicon is exercised on
# hosts without /usr/share/dict/web2.
#
# --mutations breaks each guard below in a COPY of the sources, one at a time,
# and requires the tests to go red for every one. A guard whose removal leaves
# the suite green is claimed, not tested. An anchor that no longer matches, or
# a mutation that does not compile, fails the run too. The real sources are
# never modified.
#
# --self-test runs the xunit handling above against a stand-in for swift that
# writes a fixture; it needs no Swift toolchain.
#
# Swift: $DICTATION_SWIFT, then `swift` on PATH, then
# ~/.swift-toolchain/*/usr/bin/swift (the newest by name).
set -euo pipefail

MODE="test"
if [[ "${1:-}" == "--mutations" ]]; then
    MODE=mutations
    shift
elif [[ "${1:-}" == "--self-test" ]]; then
    MODE=self-test
    shift
fi
if [[ "${1:-}" == -* ]]; then
    awk 'NR > 1 && /^#/ { sub(/^# ?/, ""); print; next } NR > 1 { exit }' "$0" >&2
    exit 2
fi
RUN_DIR="${1:-}"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MACOS="$(cd "$HERE/.." && pwd)"
APP="$(cd "$MACOS/.." && pwd)"
GOLDEN="$APP/tests/fixtures/learning-golden.json"
WEB2_GZ="$APP/windows/Dictation.Services/Resources/web2-words.txt.gz"
SOURCES="$MACOS/Sources/DictationLearning"
TESTS="$MACOS/Tests/DictationLearningTests"
JOBS=4
XCTEST_XML="swift-learning.xml"

# ─── XCTest results ───

# One "Class.method" per line for each test a serial XCTest run reports
# skipped, in Linux ("Test Case 'Class.method' skipped") or Darwin
# ("Test Case '-[Module.Class method]' skipped") form.
skipped_names() { # LOG
    sed -n -E \
        -e "s/^Test Case '([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)' skipped.*/\1.\2/p" \
        -e "s/^Test Case '-\[[A-Za-z0-9_]+\.([A-Za-z0-9_]+) ([A-Za-z0-9_]+)\]' skipped.*/\1.\2/p" \
        "$1" | sort -u
}

# Adds <skipped/> to each testcase of XML named in NAMES (one "Class.method"
# per line; the module prefix of classname is ignored) unless it records a
# failure or error, and a skipped count to a lone <testsuite>. Prints how many
# it marked.
mark_skipped() { # XML NAMES
    local marked
    marked="$(awk -v names="$2" -v out="$1.tmp" '
        BEGIN { while ((getline name < names) > 0) if (name != "") skip[name] = 1 }
        function key(line,   c, n) {
            c = line; sub(/.*classname="/, "", c); sub(/".*/, "", c); sub(/.*\./, "", c)
            n = line; sub(/.* name="/, "", n); sub(/".*/, "", n)
            return c "." n
        }
        { lines[NR] = $0 }
        END {
            marked = 0; suites = 0
            for (i = 1; i <= NR; i++) {
                if (lines[i] ~ /<testsuite[ >]/) suites++
                if (lines[i] !~ /<testcase / || !(key(lines[i]) in skip)) continue
                if (lines[i] ~ /\/>[[:space:]]*$/) {
                    sub(/\/>[[:space:]]*$/, "><skipped/></testcase>", lines[i]); marked++; continue
                }
                failed = 0
                for (j = i; j <= NR; j++) {
                    if (lines[j] ~ /<(failure|error)[ >\/]/) failed = 1
                    if (lines[j] ~ /<\/testcase>/) break
                }
                if (failed || j > NR) continue
                sub(/<\/testcase>/, "<skipped/></testcase>", lines[j]); marked++
            }
            for (i = 1; i <= NR; i++) {
                if (suites == 1 && marked > 0 && lines[i] ~ /<testsuite[ >]/ && lines[i] !~ / skipped="/)
                    sub(/<testsuite/, "<testsuite skipped=\"" marked "\"", lines[i])
                print lines[i] > out
            }
            print marked
        }' "$1")" || return 1
    mv "$1.tmp" "$1"
    echo "$marked"
}

# The XCTest xunit file must exist once the tests ran: without it the
# learning counts would silently vanish from the runner's summary.
require_xunit() { # RUN_DIR
    [[ -s "$1/$XCTEST_XML" ]] && return 0
    echo "swift test wrote no XCTest result file ($XCTEST_XML); the counts are lost" >&2
    return 1
}

run_tests() { # [extra swift test args...]
    local args=(test --package-path "$PKG" --jobs "$JOBS" "$@")
    if [[ -n "$RUN_DIR" ]]; then
        # SwiftPM writes XCTest's xunit file only from its parallel runner.
        rm -f "$RUN_DIR/$XCTEST_XML"
        args+=(--parallel --num-workers "$JOBS" --xunit-output "$RUN_DIR/$XCTEST_XML")
        set +e
        "$SWIFT" "${args[@]}" 2>&1 | tee "$RUN_DIR/swift-learning.log"
        local status=${PIPESTATUS[0]}
        set -e
        require_xunit "$RUN_DIR" || return $((status == 0 ? 1 : status))
        mark_serial_skips || { [[ "$status" != 0 ]] || status=1; }
        return "$status"
    fi
    "$SWIFT" "${args[@]}"
}

# The parallel runner's xunit file shows a skipped test as passed; a serial
# run of the same build says which were skipped. Fails when the serial run
# fails, which the parallel one did not show.
mark_serial_skips() {
    local serial="$WORK/serial.log" serial_status names marked
    set +e
    "$SWIFT" test --package-path "$PKG" --jobs "$JOBS" --skip-build >"$serial" 2>&1
    serial_status=$?
    set -e
    skipped_names "$serial" > "$WORK/skipped.txt"
    names="$(wc -l < "$WORK/skipped.txt" | tr -d ' ')"
    marked="$(mark_skipped "$RUN_DIR/$XCTEST_XML" "$WORK/skipped.txt")"
    {
        echo "== serial pass: $names skipped, $marked marked <skipped/> in $XCTEST_XML =="
        sed 's/^/skipped: /' "$WORK/skipped.txt"
        grep -E "Executed [0-9]+ tests?" "$serial" | tail -n 1
    } | tee -a "$RUN_DIR/swift-learning.log"
    if [[ "$names" != "$marked" ]]; then
        echo "some skipped tests were not marked (a failure in one run, or a name missing from the other)" \
            | tee -a "$RUN_DIR/swift-learning.log"
    fi
    if [[ "$serial_status" != 0 ]]; then
        tail -n 20 "$serial"
        echo "the serial pass failed (exit $serial_status)" | tee -a "$RUN_DIR/swift-learning.log" >&2
        return 1
    fi
}

# ─── Self-test ───
#
# Drives run_tests itself, with a stand-in for swift that behaves as SwiftPM
# does here: only a --parallel run writes the xunit file (in which a skipped
# test looks passed), and a serial run prints XCTest's lines.
self_test() {
    local dir failed=0 status out xml want
    dir="$(mktemp -d "${TMPDIR:-/tmp}/dictation-learning-selftest.XXXXXX")"
    cat > "$dir/fixture.xml" <<'XML'
<?xml version="1.0" encoding="UTF-8"?>

<testsuites>
<testsuite name="TestResults" errors="0" tests="5" failures="1" time="0.1">
<testcase classname="DictationLearningTests.LearningLexiconTests" name="testTheSystemWordList" time="0.01">
</testcase>
<testcase classname="DictationLearningTests.LearningLexiconTests" name="testThreePluralRules" time="0.01">
</testcase>
<testcase classname="DictationLearningTests.LearningStoreTests" name="testTheDirectoryIsPrivate" time="0.01">
<failure message="failure"></failure>
</testcase>
<testcase classname="DictationLearningTests.LearningLexiconTests" name="testTheWindowsCopyOfTheWordList" time="0.01"/>
<testcase classname="DictationLearningTests.OtherTests" name="testTheSystemWordList" time="0.01">
</testcase>
</testsuite>
</testsuites>
XML
    cat > "$dir/serial.log" <<'LOG'
Test Case 'LearningLexiconTests.testTheSystemWordList' started at 2026-09-28 17:21:16.846
/x/LearningLexiconTests.swift:132: LearningLexiconTests.testTheSystemWordList : Test skipped - absent
Test Case 'LearningLexiconTests.testTheSystemWordList' skipped (0.001 seconds)
Test Case 'LearningLexiconTests.testThreePluralRules' passed (0.001 seconds)
Test Case 'LearningStoreTests.testTheDirectoryIsPrivate' skipped (0.001 seconds)
Test Case '-[DictationLearningTests.LearningLexiconTests testTheWindowsCopyOfTheWordList]' skipped (0.000 seconds).
	 Executed 4 tests, with 3 tests skipped and 0 failures (0 unexpected) in 0.004 (0.004) seconds
LOG
    cat > "$dir/swift" <<'SWIFT'
#!/usr/bin/env bash
xunit="" parallel=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --xunit-output) xunit="$2"; shift ;;
        --parallel) parallel=1 ;;
    esac
    shift
done
if [[ -n "$parallel" ]]; then
    [[ -n "$xunit" && -z "${FAKE_NO_XUNIT:-}" ]] && cp "$FAKE_XUNIT" "$xunit"
    echo "parallel run"
    exit "${FAKE_PARALLEL_STATUS:-0}"
fi
cat "$FAKE_SERIAL"
exit "${FAKE_SERIAL_STATUS:-0}"
SWIFT
    chmod +x "$dir/swift"
    export FAKE_XUNIT="$dir/fixture.xml" FAKE_SERIAL="$dir/serial.log"
    SWIFT="$dir/swift"
    PKG="$dir/pkg"
    WORK="$dir/work"
    RUN_DIR="$dir/run"
    mkdir -p "$WORK" "$RUN_DIR"
    run() { status=0; out="$(run_tests 2>&1)" || status=$?; xml="$(cat "$RUN_DIR/$XCTEST_XML" 2>/dev/null || true)"; }

    # Both formats of XCTest's skip line are read.
    [[ "$(skipped_names "$dir/serial.log" | tr '\n' ' ')" == "LearningLexiconTests.testTheSystemWordList LearningLexiconTests.testTheWindowsCopyOfTheWordList LearningStoreTests.testTheDirectoryIsPrivate " ]] \
        || { echo "FAIL skipped_names read: $(skipped_names "$dir/serial.log" | tr '\n' ' ')"; failed=1; }

    # A passing run: the skipped tests are marked, nothing else is.
    run
    [[ "$status" == 0 ]] || { echo "FAIL a passing run exited $status: $out"; failed=1; }
    for want in \
        $'name="testTheSystemWordList" time="0.01">\n<skipped/></testcase>' \
        'name="testTheWindowsCopyOfTheWordList" time="0.01"><skipped/></testcase>' \
        '<testsuite skipped="2" name="TestResults"' \
        $'<failure message="failure"></failure>\n</testcase>'; do
        [[ "$xml" == *"$want"* ]] || { echo "FAIL the result file lacks: $want"; failed=1; }
    done
    [[ "$(grep -c '<skipped/>' <<<"$xml")" == 2 ]] \
        || { echo "FAIL a passed, failed or other-class test was marked skipped"; failed=1; }
    [[ "$out" == *"3 skipped, 2 marked"* ]] || { echo "FAIL the log does not say what was marked: $out"; failed=1; }

    # A failing run keeps its status, and its file is still written and marked.
    FAKE_PARALLEL_STATUS=1 FAKE_SERIAL_STATUS=1 run
    [[ "$status" == 1 && "$xml" == *"<skipped/>"* ]] \
        || { echo "FAIL a failing run exited $status or lost its marks"; failed=1; }

    # No XCTest file after the run: a failure even when swift test passed.
    FAKE_NO_XUNIT=1 run
    [[ "$status" != 0 && -z "$xml" && "$out" == *"wrote no XCTest result file"* ]] \
        || { echo "FAIL a run without $XCTEST_XML exited $status"; failed=1; }
    : > "$dir/empty.xml"
    FAKE_XUNIT="$dir/empty.xml" run
    [[ "$status" != 0 ]] || { echo "FAIL an empty $XCTEST_XML was accepted"; failed=1; }
    printf 'stale' > "$RUN_DIR/$XCTEST_XML"
    FAKE_NO_XUNIT=1 run
    [[ "$status" != 0 ]] || { echo "FAIL a stale $XCTEST_XML from an earlier run was accepted"; failed=1; }

    # A serial pass that fails where the parallel run passed is a failure.
    FAKE_SERIAL_STATUS=3 run
    [[ "$status" != 0 ]] || { echo "FAIL a failing serial pass was ignored"; failed=1; }

    rm -rf "$dir"
    if [[ "$failed" == 0 ]]; then echo "self-test: xunit handling OK"; fi
    return "$failed"
}

if [[ "$MODE" == self-test ]]; then
    self_test
    exit $?
fi

# ─── Toolchain ───

find_swift() {
    if [[ -n "${DICTATION_SWIFT:-}" ]]; then
        if [[ -x "$DICTATION_SWIFT" ]]; then
            echo "$DICTATION_SWIFT"
            return 0
        fi
        echo "DICTATION_SWIFT is not an executable: $DICTATION_SWIFT" >&2
        return 1
    fi
    if command -v swift >/dev/null 2>&1; then
        command -v swift
        return 0
    fi
    local found=""
    for candidate in "$HOME"/.swift-toolchain/*/usr/bin/swift; do
        [[ -x "$candidate" ]] && found="$candidate"
    done
    [[ -n "$found" ]] && echo "$found" && return 0
    echo "no Swift toolchain: set DICTATION_SWIFT, put swift on PATH, or unpack one under ~/.swift-toolchain" >&2
    return 1
}

SWIFT="$(find_swift)" || exit 2
[[ -f "$GOLDEN" ]] || { echo "golden cases missing: $GOLDEN" >&2; exit 2; }
[[ -d "$SOURCES" && -d "$TESTS" ]] || { echo "DictationLearning sources or tests missing under $MACOS" >&2; exit 2; }
if [[ -n "$RUN_DIR" ]]; then
    mkdir -p "$RUN_DIR"
    RUN_DIR="$(cd "$RUN_DIR" && pwd)"
fi

# ─── Scratch package ───

WORK="$(mktemp -d "${TMPDIR:-/tmp}/dictation-learning.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT
PKG="$WORK/pkg"
mkdir -p "$PKG/Sources" "$PKG/Tests"
cat > "$PKG/Package.swift" <<'EOF'
// swift-tools-version: 6.2
// Generated by macos/tools/test-learning-on-linux.sh: the platform-free
// learning target of macos/Package.swift and its tests, nothing else.
import PackageDescription

let package = Package(
    name: "DictationLearningOnly",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "DictationLearning"),
        .testTarget(name: "DictationLearningTests", dependencies: ["DictationLearning"]),
    ]
)
EOF
if [[ "$MODE" == mutations ]]; then
    cp -R "$SOURCES" "$PKG/Sources/DictationLearning"
else
    ln -s "$SOURCES" "$PKG/Sources/DictationLearning"
fi
ln -s "$TESTS" "$PKG/Tests/DictationLearningTests"

export DICTATION_LEARNING_GOLDEN="$GOLDEN"
if [[ -f "$WEB2_GZ" ]] && command -v gzip >/dev/null 2>&1; then
    gzip -dc "$WEB2_GZ" > "$WORK/web2"
    export DICTATION_LEARNING_WEB2="$WORK/web2"
fi

echo "swift: $SWIFT ($("$SWIFT" --version 2>/dev/null | head -n 1))"
echo "package: $PKG"
if [[ -f /usr/share/dict/web2 ]]; then
    echo "system word list: /usr/share/dict/web2 present"
else
    echo "system word list: /usr/share/dict/web2 absent, testTheSystemWordList is skipped"
fi
echo "Windows copy of the word list: ${DICTATION_LEARNING_WEB2:-absent, testTheWindowsCopyOfTheWordList is skipped}"

if [[ "$MODE" == test ]]; then
    set +e
    run_tests
    status=$?
    set -e
    exit "$status"
fi

# ─── Mutations ───
#
# name | file | exact text | what it becomes. Each anchor must occur on exactly
# one line of its file. The texts are Swift, so `$0` is meant literally.
# shellcheck disable=SC2016
MUTATIONS=(
    'alias promotion count' 'LearningText.swift'
    'static let aliasPromotionCount = 2' 'static let aliasPromotionCount = 3'
    'distance ratio' 'LearningText.swift'
    'static let maxDistanceRatio = 0.5' 'static let maxDistanceRatio = 0.6'
    'distance ratio is strict' 'LearningPolicy.swift'
    'if distance > LearningLimits.maxDistanceRatio {' 'if distance >= LearningLimits.maxDistanceRatio {'
    'fail-closed lexicon' 'LearningLexicon.swift'
    'guard isAvailable else { return true }' 'guard isAvailable else { return false }'
    'unreadable word list fails closed' 'LearningLexicon.swift'
    'else { return OrdinaryWords([String]()) }' 'else { return OrdinaryWords(["zzz"]) }'
    'self-containing refusal (teach)' 'LearningRules.swift'
    'if LearningPolicy.containsTokens(written, soundsLike) {' 'if false, LearningPolicy.containsTokens(written, soundsLike) {'
    'inflection is never an alias' 'LearningPolicy.swift'
    'if isInflectionPair(candidate.heard, candidate.meant) {' 'if false, isInflectionPair(candidate.heard, candidate.meant) {'
    'self-containing refusal (learned alias)' 'LearningPolicy.swift'
    'if containsTokens(candidate.meant, candidate.heard) {' 'if false, containsTokens(candidate.meant, candidate.heard) {'
    'letterless correction is not journaled' 'LearningEvidence.swift'
    'let kept = candidates.filter { LearningText.hasLetter($0.meant) }' 'let kept = candidates'
    'distinct-dictation counting' 'LearningEvidence.swift'
    'records.removeAll { LearningText.same($0.id, id) }' '_ = id'
    'word tombstone check' 'LearningEvidence.swift'
    'if removedWords.contains(key) { continue }' 'if false { continue }'
    'alias tombstone check' 'LearningEvidence.swift'
    '&& !removedAliases.contains(c.pair) {' '&& true {'
    'removed word blocks alias' 'LearningEvidence.swift'
    'if removedWords.contains(key) { continue }' 'if removedWords.contains(key) && !c.alias { continue }'
    'pending TTL boundary' 'LearningRules.swift'
    '.filter { $0.lastSeen >= cutoff }' '.filter { $0.lastSeen > cutoff }'
    'taught-word cap' 'LearningRules.swift'
    'if !alreadyTaught, taught >= LearningLimits.maxTaughtWords {' 'if false, !alreadyTaught, taught >= LearningLimits.maxTaughtWords {'
    'alias cap refusal' 'LearningRules.swift'
    '!aliases.contains(where: { $0.origin == .correction }) {' 'false {'
    'pending cap' 'LearningText.swift'
    'static let maxPending = 500' 'static let maxPending = 501'
    'tombstone cap' 'LearningText.swift'
    'static let maxTombstones = 1000' 'static let maxTombstones = 1001'
    'casing never becomes an alias' 'LearningPolicy.swift'
    'if candidate.kind == .casing || LearningText.heardKey' 'if LearningText.heardKey'
    'an ordinary heard word never becomes an alias' 'LearningPolicy.swift'
    'return !lexicon.isOrdinary(tokens[0].text)' 'return true'
    'a rewritten dictation teaches nothing' 'LearningPolicy.swift'
    'diff.matches * 2 < a.count {' 'false {'
    'learning never replaces a taught alias' 'LearningEvidence.swift'
    'let protectedAliases = oldAliases.filter { $0.value.origin != .correction }' 'let protectedAliases = oldAliases.filter { _ in false }'
    'a secret-shaped word waits' 'LearningEvidence.swift'
    'guard support - opposition >= (newest.correction.secret ? 2 : 1) else { continue }' 'guard support - opposition >= 1 else { continue }'
    'a known word is counted, not parked' 'LearningEvidence.swift'
    'secret: false)]))' 'secret: LearningPolicy.isSecretShaped(word.text))]))'
    'a waiting word enters pending' 'LearningEvidence.swift'
    'if (c.alias || waits) && protectedAliases[heard] == nil && protectedWords[heard] == nil' 'if c.alias && protectedAliases[heard] == nil && protectedWords[heard] == nil'
    'promotion learns the waiting word' 'LearningEvidence.swift'
    'guard support - opposition >= (newest.correction.secret ? 2 : 1) else { continue }' 'guard !newest.correction.secret, support - opposition >= 1 else { continue }'
    'a promoted word keeps the pair count' 'LearningEvidence.swift'
    'row.text = newest.correction.meant; row.origin = .correction; row.count = support' 'row.text = newest.correction.meant; row.origin = .correction; row.count = 1'
    'a promoted word keeps the first sighting' 'LearningEvidence.swift'
    'entries.values.map { $0.record.firstSeen ?? $0.record.at }.min()!' 'entries.values.map { $0.record.at }.max()!'
    'promotion adds an alias only when admitted' 'LearningEvidence.swift'
    'if c.alias && support - opposition >= LearningLimits.aliasPromotionCount {' 'if support - opposition >= LearningLimits.aliasPromotionCount {'
    'secret-shaped length' 'LearningPolicy.swift'
    'public static let secretMinLength = 8' 'public static let secretMinLength = 9'
    'a long number is secret-shaped too' 'LearningPolicy.swift'
    'token.scalars.count >= LearningLimits.secretMinLength' 'token.scalars.count >= LearningLimits.secretMinLength && token.scalars.contains(where: LearningText.isAlpha)'
    'secret-shaped digit count' 'LearningPolicy.swift'
    'public static let secretMinDigits = 2' 'public static let secretMinDigits = 1'
    'words glued by a symbol' 'LearningPolicy.swift'
    'if !gap.contains(where: LearningText.isSpace), gap.contains(where: { !LearningText.isJoiner($0) }) {' 'if false {'
    'a space between words is not glue' 'LearningPolicy.swift'
    'if !gap.contains(where: LearningText.isSpace), gap.contains(where: { !LearningText.isJoiner($0) }) {' 'if gap.contains(where: { !LearningText.isJoiner($0) }) {'
    'a joiner is not a symbol' 'LearningPolicy.swift'
    'if !gap.contains(where: LearningText.isSpace), gap.contains(where: { !LearningText.isJoiner($0) }) {' 'if !gap.contains(where: LearningText.isSpace) {'
    'CJK punctuation is not glue' 'LearningPolicy.swift'
    'for (left, right) in zip(tokens, tokens.dropFirst()) where !left.cjk && !right.cjk {' 'for (left, right) in zip(tokens, tokens.dropFirst()) {'
    'a CJK token on the left is not glue' 'LearningPolicy.swift'
    'for (left, right) in zip(tokens, tokens.dropFirst()) where !left.cjk && !right.cjk {' 'for (left, right) in zip(tokens, tokens.dropFirst()) where !right.cjk {'
    'a CJK token on the right is not glue' 'LearningPolicy.swift'
    'for (left, right) in zip(tokens, tokens.dropFirst()) where !left.cjk && !right.cjk {' 'for (left, right) in zip(tokens, tokens.dropFirst()) where !left.cjk {'
    'learned-word cap' 'LearningText.swift'
    'static let maxLearnedWords = 100' 'static let maxLearnedWords = 101'
    'exact code-point equality' 'LearningText.swift'
    'a.utf8.elementsEqual(b.utf8)' 'a == b'
    'word list read once' 'LearningLexicon.swift'
    'if let loaded { return loaded }' '_ = loaded'
    'write only when the file would change' 'DictationLearning.swift'
    'if data == savedBytes {' 'if false {'
    'memory back in step is not unsaved' 'DictationLearning.swift'
    'lastSaveFailed = false // memory is back in step with the file' '_ = lastSaveFailed'
    'taught CJK limit' 'LearningText.swift'
    'static let maxTaughtCJKChars = 12' 'static let maxTaughtCJKChars = 13'
    'CJK Extension B-F range' 'LearningText.swift'
    '0xAC00...0xD7AF, 0xF900...0xFAFF, 0x20000...0x2FFFF:' '0xAC00...0xD7AF, 0xF900...0xFAFF:'
    'Final_Sigma lowercase' 'LearningText.swift'
    'out.append(isFinalSigma(s, at: i) ? "\u{3C2}" : "\u{3C3}")' 'out.append("\u{3C3}")'
    '.bad quarantine' 'LearningStore.swift'
    'if Self.rename(url, to: quarantineURL) {' 'if true {'
    'schema check' 'LearningStore.swift'
    'state.schema == LearningLimits.schema' 'true'
    'no writes after a failed quarantine' 'LearningStore.swift'
    'if writesRefused { throw Failure.keepingUnreadableFile }' '_ = writesRefused'
    'an unread file is not moved aside' 'LearningStore.swift'
    'return (.empty, .unreadable)' 'data = Data()'
    'an unread file refuses writes' 'LearningStore.swift'
    'writesRefused = result.outcome.keepsFileUnread' 'writesRefused = result.outcome == .quarantineFailed'
    'a later read allows writes again' 'LearningStore.swift'
    'writesRefused = result.outcome.keepsFileUnread' 'writesRefused = writesRefused || result.outcome.keepsFileUnread'
    'a file gone before the read is missing' 'LearningStore.swift'
    'if !fileManager.fileExists(atPath: url.path) { return (.empty, .missing) }' '_ = fileManager'
    'missing lists read as empty' 'LearningState.swift'
    'pending = try container.decodeIfPresent([PendingAlias].self, forKey: .pending) ?? []' 'pending = try container.decode([PendingAlias].self, forKey: .pending)'
    'an unknown origin is kept' 'LearningState.swift'
    'default: self = .other(rawValue)' 'default: self = .correction'
    'unknown keys are written back' 'LearningState.swift'
    'guard !extra.isEmpty else { return }' 'guard extra.isEmpty else { return }'
    'retry the read while the file is unread' 'DictationLearning.swift'
    'if let loadOutcome, !loadOutcome.keepsFileUnread { return false }' 'if loadOutcome != nil { return false }'
    'keep the changes made while unread' 'DictationLearning.swift'
    'unreplayed.append(change)' '_ = change'
    'replay them once the read succeeds' 'DictationLearning.swift'
    'for change in replay {' 'for change in [Change]() {'
    'a snapshot saves the replay' 'DictationLearning.swift'
    'if readIfNeeded() { persist() }' 'readIfNeeded()'
    'flush saves' 'DictationLearning.swift'
    '        return self.persist()' '        return .unchanged'
    'flush reads nothing when learning was unused' 'DictationLearning.swift'
    'guard loadOutcome != nil else { return .unchanged }' 'guard true else { return .unchanged }'
    'numbers are ordinary' 'LearningLexicon.swift'
    'return parts.allSatisfy { !LearningText.hasLetter($0) || base($0) || verbBase($0) }' 'return parts.allSatisfy { base($0) || verbBase($0) }'
    'verb endings are ordinary' 'LearningLexicon.swift'
    'return parts.allSatisfy { !LearningText.hasLetter($0) || base($0) || verbBase($0) }' 'return parts.allSatisfy { !LearningText.hasLetter($0) || base($0) }'
    'the -ed ending' 'LearningLexicon.swift'
    'for suffix in ["ed", "ing"] {' 'for suffix in ["ing"] {'
    'the -ing ending' 'LearningLexicon.swift'
    'for suffix in ["ed", "ing"] {' 'for suffix in ["ed"] {'
    'an ending dropped as it is' 'LearningLexicon.swift'
    'var bases = [stem, stem + ["e"]]' 'var bases = [stem + ["e"]]'
    'an ending dropped with the e put back' 'LearningLexicon.swift'
    'var bases = [stem, stem + ["e"]]' 'var bases = [stem]'
    'a doubled consonant before an ending' 'LearningLexicon.swift'
    'bases.append(Array(stem.dropLast()))' '_ = stem'
    '-ied back to -y' 'LearningLexicon.swift'
    'bases.append(Array(stem.dropLast()) + ["y"])' '_ = stem'
    'an excluded name is not made ordinary by a verb ending' 'LearningLexicon.swift'
    'guard !excluded.contains(ExactText(LearningText.string(word))) else { return false }' 'guard true else { return false }'
    'a verb base of three letters' 'LearningText.swift'
    'public static let minVerbBase = 3' 'public static let minVerbBase = 1'
    'a number is never a rewrite target' 'LearningPolicy.swift'
    'if !LearningText.hasLetter(candidate.meant) {' 'if false, !LearningText.hasLetter(candidate.meant) {'
    'input-method composition' 'LearningPolicy.swift'
    'if heardCJK, looksLikeComposition(meant) {' 'if false, looksLikeComposition(meant) {'
    'composition is lowercase only' 'LearningPolicy.swift'
    '(c.value >= 0x61 && c.value <= 0x7A) || c.value == 0x27 || c.value == 0x20' '(c.value >= 0x41 && c.value <= 0x7A) || c.value == 0x27 || c.value == 0x20'
    'continuation after the span' 'LearningPolicy.swift'
    'let atEnd = lastDel == a.count - 1 && lastIns == b.count - 1' 'let atEnd = false'
    'continuation before the span' 'LearningPolicy.swift'
    'let atStart = firstDel == 0 && firstIns == 0' 'let atStart = false'
    'only a growing hunk is a continuation' 'LearningPolicy.swift'
    'let grows = hunk.ins.count > hunk.dels.count' 'let grows = hunk.ins.count >= hunk.dels.count'
    'file mode 0600' 'LearningStore.swift'
    'attributes: [.posixPermissions: 0o600]) else {' 'attributes: [.posixPermissions: 0o644]) else {'
    'directory mode 0700' 'LearningStore.swift'
    'attributes: [.posixPermissions: 0o700])' 'attributes: [.posixPermissions: 0o755])'
    'refuse the real file under XCTest' 'LearningStore.swift'
    'if isUnderTest { throw Failure.refusedUnderTest }' '_ = isUnderTest'
)

echo "== baseline (must pass) =="
if ! "$SWIFT" test --package-path "$PKG" --jobs "$JOBS" >"$WORK/baseline.log" 2>&1; then
    tail -n 40 "$WORK/baseline.log"
    echo "baseline is red; fix the tests before measuring mutations" >&2
    exit 1
fi

summary="$WORK/mutations.txt"
: > "$summary"
failed=0

apply_mutation() {
    # This is literal source, not a shell substitution pattern. The quoted
    # replacement in ${content/"$anchor"/"$replacement"} writes quote marks
    # into Swift under macOS Bash 3.2, turning valid mutants into syntax errors.
    python3 - "$@" <<'PY'
from pathlib import Path
import sys
source, destination, anchor, replacement = sys.argv[1:]
text = Path(source).read_text(encoding="utf-8")
if text.count(anchor) != 1:
    raise SystemExit("mutation requires exactly one literal anchor")
Path(destination).write_text(text.replace(anchor, replacement, 1), encoding="utf-8")
PY
}

for ((i = 0; i < ${#MUTATIONS[@]}; i += 4)); do
    name="${MUTATIONS[i]}"
    file="$PKG/Sources/DictationLearning/${MUTATIONS[i + 1]}"
    anchor="${MUTATIONS[i + 2]}"
    replacement="${MUTATIONS[i + 3]}"
    original="$SOURCES/${MUTATIONS[i + 1]}"
    hits="$(grep -cF -- "$anchor" "$original" || true)"
    if [[ "$hits" != 1 ]]; then
        echo "BROKEN    $name: anchor matches $hits lines in ${MUTATIONS[i + 1]}" | tee -a "$summary"
        failed=1
        continue
    fi
    apply_mutation "$original" "$file" "$anchor" "$replacement"
    if ! "$SWIFT" build --package-path "$PKG" --jobs "$JOBS" --build-tests >"$WORK/mutant.log" 2>&1; then
        echo "BROKEN    $name: the mutant does not compile" | tee -a "$summary"
        grep -m 3 "error:" "$WORK/mutant.log" || true
        failed=1
    elif "$SWIFT" test --package-path "$PKG" --jobs "$JOBS" --skip-build >"$WORK/mutant.log" 2>&1; then
        echo "SURVIVED  $name" | tee -a "$summary"
        failed=1
    else
        red="$(grep -c "error: -\[\|: error: " "$WORK/mutant.log" || true)"
        echo "KILLED    $name ($red failing assertions)" | tee -a "$summary"
    fi
    cp "$original" "$file"
done

[[ -n "$RUN_DIR" ]] && cp "$summary" "$RUN_DIR/swift-learning-mutations.txt"
killed="$(grep -c '^KILLED' "$summary" || true)"
echo "mutations: $killed of $((${#MUTATIONS[@]} / 4)) killed"
exit "$failed"
