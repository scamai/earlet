import Foundation

/// Pure-logic self-check for voice delete: the command normalizer, the
/// backspace count, and the undo gate. No AX, no key events, no audio — every
/// property asserted here is one a wrong answer would turn into deleted user
/// text in somebody else's application, so it is all pinned in the one place
/// that runs on every `--self-check`.
public func runDictationVoiceDeleteSelfCheck() -> Bool {
    var failures: [String] = []
    func check(_ condition: Bool, _ message: @autoclosure () -> String) {
        if !condition { failures.append(message()) }
    }

    // MARK: Normalization + matching

    for phrase in ["scratch that", "Scratch that.", "  SCRATCH THAT!  ", "Scratch, that"] {
        check(DictationDeleteCommand.isDeleteCommand(phrase),
              "expected a delete command: \(phrase.debugDescription)")
    }
    for phrase in ["删掉", "删掉。", " 删除 ", "删掉那句"] {
        check(DictationDeleteCommand.isDeleteCommand(phrase),
              "expected a delete command (zh): \(phrase.debugDescription)")
    }

    // The far more important direction. Every one of these is something a
    // person genuinely says as CONTENT; matching any of them would silently
    // eat the previous line instead of typing this one.
    for phrase in [
        "scratch that whole paragraph",
        "let's scratch that idea",
        "delete that file from the repo",
        "undo",
        "no no no",
        "oops",
        "cancel",
        "that",
        "",
        "   ",
        "scratch",
        "删掉这个文件",
        "把它删掉",
    ] {
        check(!DictationDeleteCommand.isDeleteCommand(phrase),
              "must NOT be a delete command: \(phrase.debugDescription)")
    }

    check(DictationDeleteCommand.normalize("Hello, World! 123") == "helloworld123",
          "normalize dropped the wrong characters: "
              + DictationDeleteCommand.normalize("Hello, World! 123").debugDescription)

    // The Settings list and the matcher must describe the same feature.
    check(DictationDeleteCommand.displayPhrases.count == DictationDeleteCommand.phrases.count,
          "displayPhrases (\(DictationDeleteCommand.displayPhrases.count)) and phrases "
              + "(\(DictationDeleteCommand.phrases.count)) have drifted apart")
    for shown in DictationDeleteCommand.displayPhrases {
        check(DictationDeleteCommand.isDeleteCommand(shown),
              "Settings shows \(shown.debugDescription) but the matcher would not accept it")
    }

    // MARK: Backspace count — grapheme clusters, not bytes or UTF-16 units

    func checkCount(_ text: String, _ expected: Int, _ label: String) {
        let got = DictationUndoPlan.backspaceCount(for: text)
        check(got == expected, "\(label): backspaceCount(\(text.debugDescription)) = \(got), expected \(expected)")
    }
    checkCount("hello", 5, "ascii")
    checkCount("你好世界", 4, "cjk")                     // 12 UTF-8 bytes, 4 keystrokes
    checkCount("🙂", 1, "emoji")                        // 4 UTF-8 bytes, 2 UTF-16 units
    checkCount("👩‍👩‍👧", 1, "zwj emoji cluster")           // app-dependent live; the COUNT is 1
    checkCount("café", 4, "precomposed accent")
    checkCount("cafe\u{0301}", 4, "decomposed accent")  // 5 scalars, 4 clusters
    checkCount("a\nb", 3, "newline counts")
    checkCount("", 0, "empty")

    check(!DictationUndoPlan.isWithinBound(0), "a zero-length undo must be refused")
    check(DictationUndoPlan.isWithinBound(1), "a one-character undo is legitimate")
    check(DictationUndoPlan.isWithinBound(DictationUndoPlan.maxBackspaces),
          "the bound itself must be allowed")
    check(!DictationUndoPlan.isWithinBound(DictationUndoPlan.maxBackspaces + 1),
          "one past the bound must be refused")

    // MARK: The undo gate

    let now = Date()
    func record(_ text: String, _ bundleID: String?, ageSeconds: TimeInterval) -> TextInserter.LastInsertion {
        TextInserter.LastInsertion(text: text, bundleID: bundleID, at: now.addingTimeInterval(-ageSeconds))
    }

    check(TextInserter.decideUndo(record: nil, now: now, currentBundleID: "com.apple.Terminal")
            == .nothingRecorded,
          "no record must report nothingRecorded")

    check(TextInserter.decideUndo(
            record: record("hello", "com.apple.Terminal", ageSeconds: 1),
            now: now, currentBundleID: "com.apple.Terminal")
            == .deleted(text: "hello", backspaces: 5),
          "the happy path (same app, fresh) must delete")

    // The two guardrails that matter most.
    if case .differentApp = TextInserter.decideUndo(
        record: record("hello", "com.apple.Terminal", ageSeconds: 1),
        now: now, currentBundleID: "com.google.Chrome") {} else {
        failures.append("a different frontmost app MUST refuse the undo")
    }
    if case .expired = TextInserter.decideUndo(
        record: record("hello", "com.apple.Terminal", ageSeconds: TextInserter.maxUndoAge + 1),
        now: now, currentBundleID: "com.apple.Terminal") {} else {
        failures.append("an insertion older than maxUndoAge MUST refuse the undo")
    }
    check(TextInserter.decideUndo(
            record: record("hello", "com.apple.Terminal", ageSeconds: TextInserter.maxUndoAge - 1),
            now: now, currentBundleID: "com.apple.Terminal")
            == .deleted(text: "hello", backspaces: 5),
          "just inside maxUndoAge must still be undoable")

    // "We could not tell which app" is a refusal on BOTH sides.
    if case .differentApp = TextInserter.decideUndo(
        record: record("hello", nil, ageSeconds: 1),
        now: now, currentBundleID: "com.apple.Terminal") {} else {
        failures.append("an unattributed insertion MUST refuse the undo")
    }
    if case .differentApp = TextInserter.decideUndo(
        record: record("hello", "com.apple.Terminal", ageSeconds: 1),
        now: now, currentBundleID: nil) {} else {
        failures.append("an unresolvable frontmost app MUST refuse the undo")
    }

    // THE gate that stops a backspace burst landing in a password field.
    // Every branch of it, and — the load-bearing assertion — it must refuse
    // even when EVERYTHING ELSE about the undo is perfectly valid: same app,
    // one second old, a sane length. This is exactly the case that shipped
    // broken for one review cycle: dictate into a normal field, click into a
    // password field IN THE SAME APP, say "scratch that".
    for blocker in [
        DictationInsertFailure.secureInputActive(heldByOtherProcess: true),
        .secureInputActive(heldByOtherProcess: false),
        .focusedElementNotEditable,
        .noFocusedElement,
        .accessibilityPermissionMissing,
    ] {
        let outcome = TextInserter.decideUndo(
            record: record("hello", "com.apple.Terminal", ageSeconds: 1),
            now: now, currentBundleID: "com.apple.Terminal",
            fieldGate: .blocked(blocker))
        check(outcome == .blocked(blocker),
              "a blocked field MUST refuse the undo even when the record, the app and the age "
                  + "are all valid — got \(outcome) for \(blocker)")
    }

    // …and the gate must not turn a would-be-refusal into a delete.
    check(TextInserter.decideUndo(record: nil, now: now, currentBundleID: "com.apple.Terminal",
                                  fieldGate: .ok) == .nothingRecorded,
          "an OK field must not manufacture a record")
    check(TextInserter.decideUndo(record: nil, now: now, currentBundleID: "com.apple.Terminal",
                                  fieldGate: .blocked(.noFocusedElement)) == .nothingRecorded,
          "with no record at all, the honest answer is 'nothing to delete', not 'wrong field' — "
              + "the field is not the user's problem when there is nothing to remove")

    // A clock that went backwards (NTP step, sleep/wake) is not a licence.
    if case .expired = TextInserter.decideUndo(
        record: record("hello", "com.apple.Terminal", ageSeconds: -5),
        now: now, currentBundleID: "com.apple.Terminal") {} else {
        failures.append("a record dated in the future MUST refuse the undo")
    }

    if case .tooLong = TextInserter.decideUndo(
        record: record(String(repeating: "x", count: DictationUndoPlan.maxBackspaces + 1),
                       "com.apple.Terminal", ageSeconds: 1),
        now: now, currentBundleID: "com.apple.Terminal") {} else {
        failures.append("an absurdly long record MUST refuse the undo")
    }

    // Empty text is `tooLong`'s sibling: nothing to delete, so refuse rather
    // than "succeed" with zero keystrokes.
    if case .tooLong = TextInserter.decideUndo(
        record: record("", "com.apple.Terminal", ageSeconds: 1),
        now: now, currentBundleID: "com.apple.Terminal") {} else {
        failures.append("an empty record MUST refuse the undo")
    }

    // MARK: Registry take-once

    let registry = TextInserter.LastInsertionRegistry.shared
    let saved = registry.current()
    defer { if let saved { registry.record(saved) } else { registry.clear() } }
    registry.clear()
    check(registry.current() == nil, "a cleared registry must hold nothing")
    registry.record(TextInserter.LastInsertion(text: "abc", bundleID: "x", at: now))
    check(registry.current()?.text == "abc", "the registry must hold what was recorded")
    registry.clear()
    check(registry.current() == nil, "clear() must consume the record")

    guard failures.isEmpty else {
        let suffix = failures.count > 1 ? " (+\(failures.count - 1) more)" : ""
        print("dictation-voice-delete-check: FAIL: \(failures[0])\(suffix)")
        return false
    }
    print("dictation-voice-delete-check: PASS "
        + "(\(DictationDeleteCommand.phrases.count) phrases, "
        + "undo bound \(TextInserter.maxUndoAge)s / \(DictationUndoPlan.maxBackspaces) chars, "
        + "secure-input/editability gate enforced)")
    return true
}
