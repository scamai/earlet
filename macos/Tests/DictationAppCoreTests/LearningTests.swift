// Automatic vocabulary learning in the app (LEARNING.md): a typed paste is
// watched while "Learn from my corrections" is on, the watch's final span goes
// to `DictationLearning`, and pause, quit, the next dictation's release and
// the switch abandon a watch without learning from it. A press that is not a
// dictation (a shortcut on the hold key, Escape, a refused press) leaves it
// running. The watch is a fake; the learning actor and its file are real, in a
// temporary directory.

#if canImport(Testing)
import DictationLearning
import Foundation
import Testing
@testable import DictationKit
import ApplicationServices
@testable import DictationAppCore

@MainActor
@Suite("Learning from corrections")
struct LearningTests {

    private static let textEdit = "com.apple.TextEdit"

    /// A harness whose pastes go to TextEdit, with its model enabled.
    private func enabled(_ configure: (inout Harness) -> Void = { _ in }) async -> (Harness, DictationModel) {
        var harness = Harness()
        harness.targetApplication.mutate { $0 = Self.textEdit }
        configure(&harness)
        let model = harness.model()
        await model.toggle()
        #expect(model.enabled)
        return (harness, model)
    }

    /// One hold whose words are `reply`, through to its outcome.
    private func dictate(_ reply: String, _ harness: Harness, _ model: DictationModel) async {
        harness.workers.made.last?.state.mutate { $0.replies = [.text(reply)] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording && !model.transcribing })
    }

    private func logged(_ harness: Harness, _ text: String) -> Bool {
        harness.logLines.get.contains { $0.contains(text) }
    }

    private func observationsEnded(_ harness: Harness) -> Int {
        harness.logLines.get.filter { $0.contains("learning: observation ended") }.count
    }

    /// The latest watch ends with `span`, and the model has handled it before
    /// the test goes on, as it has long before a user's next dictation. (A
    /// dictation released first abandons the watch, and its result is then
    /// dropped.)
    private func fix(_ span: String, _ harness: Harness) async {
        let before = observationsEnded(harness)
        harness.correctionWatch.finish(span: span)
        #expect(await eventually { observationsEnded(harness) == before + 1 })
    }

    // ─── Learning ───

    @Test("one draft replaces and retracts its learning through repeated stable callbacks")
    func aDraftCanChangeItsMind() async throws {
        let (harness, model) = await enabled {
            $0.lexicon = WordListLexicon(words: ["we", "use", "my", "today"])
        }
        await dictate("we use Mies today", harness, model)
        let original = try #require(harness.inserts.texts.get.last)
        harness.correctionWatch.settle("We use Miles today")
        #expect(await eventually { harness.learningFile?.words.map(\.text) == ["Miles"] })
        harness.correctionWatch.settle("We use Mils today")
        #expect(await eventually { harness.learningFile?.words.map(\.text) == ["Mils"] })
        #expect(harness.learningFile?.words.first?.count == 1)
        harness.correctionWatch.settle(original)
        #expect(await eventually { harness.learningFile?.words.isEmpty == true })
        harness.correctionWatch.settle("We use Miles today")
        #expect(await eventually { harness.learningFile?.words.map(\.text) == ["Miles"] })
        #expect(harness.learningFile?.words.first?.count == 1)
        harness.correctionWatch.settle("We use Miles today")
        harness.correctionWatch.finish(span: nil, reason: "watch_expired")
        #expect(await eventually { observationsEnded(harness) == 1 })
        harness.correctionWatch.settle("We use Mils today")
        await Task.yield()
        #expect(harness.learningFile?.words.map(\.text) == ["Miles"])
        #expect(harness.learningFile?.words.first?.count == 1)
        #expect(!harness.learningFileText.contains("We use"))
    }

    @Test("switching learning off invalidates an already queued revision")
    func queuedRevisionCannotTeachAfterAbandon() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.settle("We use Trovioo")
        model.setLearningEnabled(false)
        harness.correctionWatch.settle("We use TrovioAI")
        await Task.yield()
        #expect(harness.learningFile == nil)
    }

    @Test("a Witty composer correction persists a real Webster name without terminal output")
    func wittyNameCorrectionPersists() async throws {
        let (harness, model) = await enabled {
            $0.targetApplication.mutate { $0 = DictationWittyInput.bundleID }
            $0.lexicon = WebsterLexicon.system()
        }
        #expect(harness.lexicon.isAvailable)
        await dictate("we use Mies", harness, model)
        let pasted = try #require(harness.inserts.texts.get.last)
        let before = try #require(DictationWittyInput.parse(
            "PRIVATE OUTPUT\n\n› Ask Codex to do anything\n\n  Main [default] · ← for agents"))
        let initial = try #require(DictationWittyInput.parse(
            "CHANGED OUTPUT\n\n› \(pasted)\n\n  49% context left    ⚠ 1 warning · f2 to view"))
        let anchor = try #require(DictationSpanAnchor.establish(
            beforePaste: .init(value: before.value, selectionLocation: nil, selectionLength: nil),
            fieldValue: initial.value, pasted: DictationWittyInput.pasted(pasted)))
        let edited = try #require(DictationWittyInput.parse(
            "UNRELATED OUTPUT\n\n› We use Miles\n\n  49% context left"))
        let fieldValue = try #require(edited.value(matching: initial))
        let span = try #require(anchor.span(in: fieldValue))
        await fix(span, harness)
        #expect(harness.learningFile?.words.map(\.text) == ["Miles"])
        #expect(harness.learningFile?.words.first?.origin == .correction)
        #expect(harness.learningFile?.aliases.isEmpty == true)
        #expect(harness.learningFile?.pending.isEmpty == true)
        #expect(!harness.learningFileText.contains("OUTPUT"))
        let relaunched = DictationLearning(store: LearningStore(url: harness.learningURL), lexicon: harness.lexicon)
        #expect(await relaunched.snapshot().words.map(\.text) == ["Miles"])
        #expect(await eventually { model.vocabulary.rows.first?.detail == "Learned from one fix, not yet confirmed" })
    }

    @Test("a Claude Code composer correction in Witty teaches the meant word and keeps the terminal out")
    func claudeCodeCorrectionPersists() async throws {
        let (harness, model) = await enabled {
            $0.targetApplication.mutate { $0 = DictationWittyInput.bundleID }
            $0.lexicon = WordListLexicon(words: ["code"])
        }
        await dictate("我们用扣的写代码", harness, model)
        let pasted = try #require(harness.inserts.texts.get.last)
        let rule = String(repeating: "─", count: 40)
        func screen(_ draft: String, output: String) -> String {
            [output, "", rule, "❯\u{00A0}" + draft, rule, "  project git:(main)", "  ⏵⏵ bypass permissions on"]
                .joined(separator: "\n")
        }
        let before = try #require(DictationWittyInput.parse(screen("Try \"fix lint errors\"", output: "PRIVATE OUTPUT")))
        let initial = try #require(DictationWittyInput.parse(screen(pasted, output: "CHANGED OUTPUT")))
        let anchor = try #require(DictationSpanAnchor.establish(
            beforePaste: .init(value: before.value, selectionLocation: nil, selectionLength: nil),
            fieldValue: initial.value, pasted: DictationWittyInput.pasted(pasted)))
        let fixed = pasted.replacingOccurrences(of: "扣的", with: "Claude Code")
        #expect(fixed != pasted)
        let edited = try #require(DictationWittyInput.parse(screen(fixed, output: "UNRELATED OUTPUT")))
        let fieldValue = try #require(edited.value(matching: initial))
        let span = try #require(anchor.span(in: fieldValue))
        await fix(span, harness)
        #expect(harness.learningFile?.words.map(\.text) == ["Claude Code"])
        #expect(harness.learningFile?.words.first?.origin == .correction)
        // A transliteration becomes a rewrite only on a second dictation.
        #expect(harness.learningFile?.aliases.isEmpty == true)
        #expect(harness.learningFile?.pending.count == 1)
        #expect(!harness.learningFileText.contains("OUTPUT"))
        #expect(!harness.learningFileText.contains("bypass"))
        await model.prepareForQuit()
    }

    @Test("ordinary-word technical corrections reach the file and survive relaunch without a rewrite")
    func repoCorrectionPersists() async throws {
        let (harness, model) = await enabled {
            $0.lexicon = WordListLexicon(words: ["we", "use", "the", "wrapper"])
        }
        await dictate("we use the wrapper", harness, model)
        #expect(model.vocabulary.observationNotice == "Checking this field for corrections…")
        await fix("We use the repo", harness)
        #expect(harness.learningFile?.words.map(\.text) == ["repo"])
        #expect(harness.learningFile?.words.first?.origin == .correction)
        #expect(harness.learningFile?.pending.isEmpty == true)
        #expect(harness.learningFile?.aliases.isEmpty == true)
        #expect(model.vocabulary.learningNotice != nil)
        #expect(model.vocabulary.observationNotice == nil)
        let relaunched = DictationLearning(store: LearningStore(url: harness.learningURL), lexicon: harness.lexicon)
        #expect(await relaunched.snapshot().words.map(\.text) == ["repo"])
        await dictate("we use the wrapper", harness, model)
        await fix("We use the repo", harness)
        #expect(harness.learningFile?.words.first?.count == 2)
        #expect(harness.learningFile?.aliases.isEmpty == true)
        #expect(harness.learningFile?.pending.isEmpty == true)
        #expect(model.vocabulary.observationNotice?.contains("already") == true)
        #expect(!harness.learningFileText.contains("We use"))
    }

    @Test("a fix is learned as a word at once, and the same fix on a second dictation as an alias")
    func learnsAWordThenAnAlias() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        #expect(harness.inserts.texts.get == ["We use trovio"])
        #expect(harness.correctionWatch.begins.get == [.init(pasted: "We use trovio", targetApp: Self.textEdit)])

        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually { harness.learningFile?.words.map(\.text) == ["Trovioo"] })
        #expect(harness.learningFile?.words.first?.origin == .correction)
        #expect(harness.learningFile?.aliases.isEmpty == true, "one dictation never makes an alias")
        #expect(await eventually { model.vocabulary.rows.map(\.title) == ["Trovioo"] })
        #expect(model.vocabulary.rows.first?.detail == "Learned from one fix, not yet confirmed")

        await dictate("we use trovio", harness, model)
        #expect(harness.correctionWatch.begins.get.count == 2)
        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually {
            harness.learningFile?.aliases.map { [$0.heard, $0.meant] } == [["trovio", "Trovioo"]]
        })
        #expect(await eventually { model.vocabulary.rows.map(\.title) == ["Trovioo", "trovio → Trovioo"] })
    }

    @Test("the log says how each observation ended, in counts, never in words")
    func theLogHoldsCountsOnly() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually { logged(harness, "learning: observation ended (edit_settled); 1 corrections") })
        await dictate("we use it", harness, model)
        harness.correctionWatch.finish(span: nil, reason: "revision_too_large")
        #expect(await eventually {
            logged(harness, "learning: observation ended (revision_too_large); nothing to learn")
        })
        for line in harness.logLines.get {
            for word in ["rovio", "We use", "we use", "use it"] {
                #expect(!line.contains(word), "\(line)")
            }
        }
    }

    @Test("an observation that ends with no span learns nothing")
    func noSpanNoLearning() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: nil, reason: "span_context_lost")
        #expect(await eventually { logged(harness, "(span_context_lost); nothing to learn") })
        #expect(harness.learningFile == nil)
        #expect(model.vocabulary.observationNotice == nil)
    }

    @Test("an untouched dictation whose field lost focus ends quietly")
    func untouchedFocusLeftNotice() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: nil, reason: "focus_left", spanObservedToEnd: true)
        #expect(await eventually { model.vocabulary.observationNotice == nil })
        #expect(harness.learningFile == nil)
    }

    @Test("an untouched dictation whose ninety seconds ran out ends quietly")
    func untouchedTimeoutNotice() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: nil, reason: "watch_expired", spanObservedToEnd: true)
        #expect(await eventually { model.vocabulary.observationNotice == nil })
        #expect(harness.learningFile == nil)
    }

    @Test("a settled revision's notice survives the watch ending with its span held to the end")
    func revisionNoticeSurvivesWatchEnd() async {
        let (harness, model) = await enabled()
        await dictate("the wifi password is sunshine", harness, model)
        harness.correctionWatch.settle("The wifi password is Sunshine42x!")
        #expect(await eventually { model.vocabulary.observationNotice?.contains("Another matching correction") == true })
        harness.correctionWatch.finish(span: nil, reason: "focus_left", spanObservedToEnd: true, sawChange: true)
        #expect(await eventually { logged(harness, "(focus_left); nothing to learn") })
        #expect(model.vocabulary.observationNotice?.contains("Another matching correction") == true)
        #expect(harness.learningFile?.pending.map { [$0.heard, $0.meant] } == [["sunshine", "Sunshine42x"]])
    }

    // ─── When a paste is watched ───

    @Test("the snapshot is captured before insertion and passed to the successful paste's watch")
    func snapshotSurvivesThePasteBoundary() async throws {
        let (harness, model) = await enabled()
        let snapshot = DictationCorrectionObserver.PasteSnapshot(
            element: AXUIElementCreateSystemWide(), pid: 1, bundleID: Self.textEdit,
            field: .init(value: "PRIVATE-BEFORE-9K", selectionLocation: 0, selectionLength: 0))
        let inserts = harness.inserts
        harness.correctionWatch.prepareHook.mutate { hook in
            hook = {
                #expect(inserts.texts.get.isEmpty, "snapshot must precede the paste")
                return snapshot
            }
        }
        await dictate("we use wrapper", harness, model)
        #expect(harness.correctionWatch.preparations.get == [Self.textEdit])
        let received = try #require(harness.correctionWatch.snapshots.get.first ?? nil)
        #expect(received.field == snapshot.field)
        #expect(received.matches(element: snapshot.element, pid: snapshot.pid, bundleID: snapshot.bundleID))
        #expect(!harness.logLines.get.joined().contains("PRIVATE-BEFORE-9K"))
    }

    @Test("quitting during correction preparation prevents the pending paste")
    func quitDuringPreparationDoesNotPaste() async {
        let (harness, model) = await enabled()
        harness.correctionWatch.prepareHook.mutate { hook in
            hook = {
                await model.prepareForQuit()
                return nil
            }
        }
        await dictate("words that must not be pasted after quit", harness, model)
        #expect(harness.inserts.texts.get.isEmpty)
        #expect(harness.clipboard.get.isEmpty)
        #expect(harness.correctionWatch.begins.get.isEmpty)
    }

    @Test("turning learning off during preparation invalidates that paste even if turned on again")
    func disablingLearningDuringPreparation() async {
        let (harness, model) = await enabled()
        harness.correctionWatch.prepareHook.mutate { hook in
            hook = {
                await MainActor.run {
                    model.setLearningEnabled(false)
                    model.setLearningEnabled(true)
                }
                return nil
            }
        }
        await dictate("we use wrapper", harness, model)
        #expect(harness.inserts.texts.get == ["We use wrapper"])
        #expect(harness.correctionWatch.begins.get.isEmpty)
        #expect(model.vocabulary.observationNotice == nil)
        harness.correctionWatch.prepareHook.mutate { $0 = nil }
        await dictate("we use trovio", harness, model)
        #expect(harness.correctionWatch.begins.get.count == 1)
    }

    @Test("learning is on by default and the switch is kept across launches")
    func switchDefaultsOnAndPersists() async {
        let harness = Harness()
        #expect(harness.settings.learningEnabled)
        let model = harness.model()
        #expect(model.learningEnabled)
        model.setLearningEnabled(false)
        #expect(!model.learningEnabled)
        #expect(!harness.settings.learningEnabled)
        // The key both the app and a support engineer look for.
        #expect(harness.scratch.defaults.object(forKey: "dictation.learning.enabled") as? Bool == false)
        #expect(!harness.model().learningEnabled, "a relaunch keeps it off")
        model.setLearningEnabled(true)
        #expect(harness.model().learningEnabled)
    }

    @Test("with the switch off, a paste is never watched")
    func switchOffNeverWatches() async {
        let (harness, model) = await enabled()
        model.setLearningEnabled(false)
        await dictate("we use trovio", harness, model)
        #expect(harness.inserts.texts.get == ["We use trovio"])
        #expect(harness.correctionWatch.begins.get.isEmpty)
        #expect(harness.correctionWatch.preparations.get.isEmpty)
    }

    @Test("a refused paste, and the clipboard fallback after one, are never watched")
    func refusedPastesAreNeverWatched() async {
        let refusals: [DictationInsertFailure] = [
            .secureFieldFocused, .secureInputActive(heldByOtherProcess: true), .focusedElementNotEditable,
            .noFocusedElement, .pasteNeverRequested, .accessibilityPermissionMissing,
        ]
        for refusal in refusals {
            let (harness, model) = await enabled()
            harness.inserts.outcome.mutate { $0 = .failed(refusal) }
            await dictate("we use trovio", harness, model)
            #expect(harness.inserts.texts.get == ["We use trovio"], "\(refusal)")
            #expect(harness.correctionWatch.begins.get.isEmpty, "\(refusal)")
        }
        // The fallback really ran for a field that takes no text.
        let (harness, model) = await enabled()
        harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        await dictate("we use trovio", harness, model)
        #expect(harness.clipboard.get == ["We use trovio"])
        #expect(harness.correctionWatch.begins.get.isEmpty)
    }

    @Test("a paste whose target app is unknown is not watched")
    func unknownTargetIsNotWatched() async {
        let (harness, model) = await enabled { $0.targetApplication.mutate { $0 = nil } }
        await dictate("we use trovio", harness, model)
        #expect(harness.inserts.texts.get == ["We use trovio"])
        #expect(harness.correctionWatch.begins.get.isEmpty)
        #expect(harness.correctionWatch.preparations.get.isEmpty)
        #expect(logged(harness, "learning: not observed (the target app is unknown)"))
        #expect(model.vocabulary.observationNotice == nil)
    }

    @Test("without a place to keep the vocabulary, nothing is watched and the section is hidden")
    func noStoreNoWatch() async {
        let (harness, model) = await enabled { $0.learningAvailable = false }
        #expect(!model.vocabulary.isAvailable)
        await dictate("we use trovio", harness, model)
        #expect(harness.correctionWatch.begins.get.isEmpty)
        #expect(harness.correctionWatch.preparations.get.isEmpty)
    }

    /// The worker reads learning.json itself; the host never switches on the
    /// transport's bias flag, so it never sends `bias_phrases`.
    @Test("the host never sends the vocabulary to the worker")
    func biasFlagStaysOff() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually { harness.learningFile?.words.isEmpty == false })
        #expect(!PythonAsrTransport.biasFlagEnabled)
    }

    // ─── Abandoning ───

    /// The fake delivers a span even after the abandon, as a real result can
    /// still be on its way; the model must drop it.
    private func expectAbandonedAndIgnored(_ harness: Harness, _ model: DictationModel, why: String) async {
        #expect(harness.correctionWatch.abandons.get == 1, "\(why)")
        #expect(logged(harness, "learning: observation abandoned (\(why))"))
        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually { logged(harness, "learning: ignored the result of an abandoned observation") },
                "\(why)")
        #expect(harness.learningFile == nil, "\(why)")
        #expect(model.vocabulary.observationNotice == nil, "\(why)")
        await model.vocabulary.refresh()
        #expect(model.vocabulary.rows.isEmpty, "\(why)")
    }

    @Test("pause abandons the watch; nothing is learned from it")
    func pauseAbandons() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        await model.toggle()
        #expect(!model.enabled)
        await expectAbandonedAndIgnored(harness, model, why: "pause")
    }

    @Test("quit abandons the watch; nothing is learned from it")
    func quitAbandons() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        await model.prepareForQuit()
        await expectAbandonedAndIgnored(harness, model, why: "quit")
    }

    @Test("the next dictation abandons the watch on the last paste when it is released, before its words land",
          .timeLimit(.minutes(1)))
    func nextDictationAbandons() async {
        // Long enough that the transcription waits for the test.
        let (harness, model) = await enabled { $0.timing.transcribeSeconds = 30 }
        await dictate("we use trovio", harness, model)
        let worker = harness.workers.made[0]
        worker.state.mutate { $0.replies = [.hang] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        #expect(harness.correctionWatch.abandons.get == 0, "a press alone is not the next dictation")
        harness.trigger.release()
        #expect(await eventually { worker.state.get.transcribes == 2 })
        #expect(harness.inserts.texts.get == ["We use trovio"], "the new words have not landed yet")
        await expectAbandonedAndIgnored(harness, model, why: "next dictation")
        worker.finishTranscribe("hello world")
        #expect(await eventually { !model.busy && !model.recording && !model.transcribing })
        #expect(harness.correctionWatch.begins.get.map(\.pasted) == ["We use trovio", "Hello world"])
    }

    /// Presses that end without a dictation.
    enum NotADictation: String, CaseIterable, Sendable {
        /// The hold key with a letter: Right Shift + T typing the capital a
        /// fix needs, Right Option + Delete, Right Command + Z.
        case keyShortcut
        /// The hold key with another modifier.
        case modifierShortcut
        case escape
        /// A password field has focus: nothing is recorded.
        case privacyRefusal
    }

    @Test("a press that is not a dictation leaves the watch running, and the fix is still learned",
          arguments: NotADictation.allCases)
    func pressThatIsNotADictationKeepsTheWatch(_ press: NotADictation) async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        switch press {
        case .keyShortcut:
            harness.trigger.press()
            harness.keyWatch.type(17) // kVK_ANSI_T
            #expect(await eventually { !model.recording && model.status.contains("shortcut") })
        case .modifierShortcut:
            harness.trigger.press()
            harness.trigger.modifiers([.maskCommand, .maskShift])
            #expect(await eventually { !model.recording && model.status.contains("shortcut") })
        case .escape:
            harness.trigger.press()
            harness.keyWatch.type(HoldChordGuard.escapeKeyCode)
            #expect(await eventually { !model.recording && model.status.contains("cancelled") })
        case .privacyRefusal:
            harness.privacy.mutate { $0 = .secureFieldFocused }
            harness.trigger.press()
            #expect(await eventually { !model.recording && model.status.contains("password fields") })
        }
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording && !model.transcribing })
        #expect(harness.correctionWatch.abandons.get == 0, "\(press)")
        #expect(!logged(harness, "observation abandoned"), "\(press)")

        await fix("We use Trovioo", harness)
        #expect(harness.learningFile?.words.map(\.text) == ["Trovioo"], "\(press)")
        #expect(harness.inserts.texts.get == ["We use trovio"], "\(press): nothing else was inserted")
    }

    @Test("switching learning off abandons the watch")
    func switchingOffAbandons() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        model.setLearningEnabled(false)
        await expectAbandonedAndIgnored(harness, model, why: "learning switched off")
    }

    @Test("nothing is abandoned when nothing is being watched")
    func abandonOnlyWhatIsWatched() async {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: nil, reason: "edit_settled")
        #expect(await eventually { logged(harness, "nothing to learn") })
        await model.toggle()
        #expect(harness.correctionWatch.abandons.get == 0)
    }

    // ─── Removing ───

    @Test("a removed word is not learned again from the same fix")
    func removedWordIsNotRelearned() async throws {
        let (harness, model) = await enabled()
        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually { model.vocabulary.rows.map(\.title) == ["Trovioo"] })

        let row = try #require(model.vocabulary.rows.first)
        await model.vocabulary.remove(row)
        #expect(model.vocabulary.rows.isEmpty)
        #expect(harness.learningFile?.removed.words.map(\.key) == ["trovioo"])

        await dictate("we use trovio", harness, model)
        harness.correctionWatch.finish(span: "We use Trovioo")
        #expect(await eventually {
            harness.logLines.get.filter { $0.contains("learning: observation ended (edit_settled)") }.count == 2
        })
        #expect(harness.learningFile?.words.isEmpty == true)
        await model.vocabulary.refresh()
        #expect(model.vocabulary.rows.isEmpty)
    }

    @Test("a removed alias is not learned again, and its word stays")
    func removedAliasIsNotRelearned() async throws {
        let (harness, model) = await enabled()
        for _ in 0..<2 {
            await dictate("we use trovio", harness, model)
            await fix("We use Trovioo", harness)
        }
        #expect(await eventually { model.vocabulary.rows.map(\.title) == ["Trovioo", "trovio → Trovioo"] })

        let alias = try #require(model.vocabulary.rows.last)
        await model.vocabulary.remove(alias)
        #expect(model.vocabulary.rows.map(\.title) == ["Trovioo"])

        for _ in 0..<2 {
            await dictate("we use trovio", harness, model)
            await fix("We use Trovioo", harness)
        }
        #expect(observationsEnded(harness) == 4)
        #expect(harness.learningFile?.aliases.isEmpty == true)
        #expect(harness.learningFile?.words.map(\.text) == ["Trovioo"])
    }
}

/// The span the watch reports is the pasted span only: text around it in the
/// field (a sentinel on each side) reaches neither `learning.json` nor the log.
/// The field is simulated; the anchor is the real `DictationSpanAnchor`.
@MainActor
@Suite("Text outside the pasted span")
struct SpanPrivacyTests {

    /// Pastes into a simulated field between two sentinels, applies `edit`
    /// to the whole field, and reports the anchored span as the real watch
    /// would.
    final class SimulatedFieldWatch: DictationCorrectionWatch, @unchecked Sendable {
        static let before = "SENTINEL-BEFORE-9K Dear team, "
        static let after = " Thanks. SENTINEL-AFTER-9K"
        let edit: @Sendable (String) -> String

        init(edit: @escaping @Sendable (String) -> String) { self.edit = edit }

        func prepare(targetApp: String) async -> DictationCorrectionObserver.PasteSnapshot? {
            .init(element: AXUIElementCreateSystemWide(), pid: 1, bundleID: targetApp,
                  field: .init(value: Self.before + Self.after, selectionLocation: 0, selectionLength: 0))
        }

        func begin(pasted: String, targetApp: String, beforePaste: DictationCorrectionObserver.PasteSnapshot?,
                   onRevision: (@Sendable (String) -> Void)?,
                   onEnd: @escaping @Sendable (DictationCorrectionWatchEnd) -> Void) {
            let field = Self.before + pasted + Self.after
            // Zero-caret field: only the snapshot carried through the real
            // model's paste boundary can make the correction reach the file.
            guard let beforePaste, let anchor = DictationSpanAnchor.establish(
                beforePaste: beforePaste.field, fieldValue: field, pasted: pasted) else {
                onEnd(DictationCorrectionWatchEnd(reason: "span_missing", editedSpan: nil))
                return
            }
            let span = anchor.span(in: edit(field))
            onEnd(DictationCorrectionWatchEnd(
                reason: span == nil ? "span_context_lost" : "edit_settled",
                editedSpan: DictationTerminalCondition.editSettled.reportedSpan(span)))
        }

        func abandon() {}
    }

    private func run(reply: String = "we use trovio", edit: @escaping @Sendable (String) -> String) async -> Harness {
        let harness = Harness()
        harness.targetApplication.mutate { $0 = "com.apple.TextEdit" }
        var env = harness.environment()
        env.correctionWatch = SimulatedFieldWatch(edit: edit)
        let model = DictationModel(environment: env)
        await model.toggle()
        harness.workers.made[0].state.mutate { $0.replies = [.text(reply)] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { harness.logLines.get.contains { $0.contains("learning: observation ended") } })
        return harness
    }

    private func expectNoSentinel(_ harness: Harness) {
        #expect(!harness.learningFileText.contains("SENTINEL"))
        #expect(!harness.learningFileText.contains("Dear team"))
        for line in harness.logLines.get {
            #expect(!line.contains("SENTINEL"), "\(line)")
            #expect(!line.contains("Dear team"), "\(line)")
            #expect(!line.contains("rovio"), "\(line)")
        }
    }

    @Test("a fix inside the span is learned; the text around it is not")
    func fixInsideTheSpan() async {
        let harness = await run { $0.replacingOccurrences(of: "trovio", with: "Trovioo") }
        #expect(await eventually { harness.learningFile?.words.map(\.text) == ["Trovioo"] })
        expectNoSentinel(harness)
    }

    @Test("an edit outside the span breaks it, and nothing is learned")
    func editOutsideTheSpan() async {
        let harness = await run {
            $0.replacingOccurrences(of: "trovio", with: "Trovioo")
                .replacingOccurrences(of: "SENTINEL-BEFORE-9K", with: "SENTINEL-EDITED-9K")
        }
        #expect(harness.logLines.get.contains { $0.contains("(span_context_lost); nothing to learn") })
        #expect(harness.learningFile == nil)
        expectNoSentinel(harness)
    }

    /// A span read while the input method still shows pinyin in the field
    /// (the user is retyping 中文 and has not committed it) reaches learning as
    /// the span, and the rules refuse it as input-method composition
    /// (LEARNING.md §3.3), so the pinyin is never learned as a word or an alias.
    /// The committed-looking control shows the same path does learn.
    private static let chinese = "我们今天下午开会讨论一下中文输入法的问题"

    @Test("uncommitted pinyin in the span is refused as input-method composition")
    func uncommittedPinyinIsNotLearned() async {
        let harness = await run(reply: Self.chinese) { $0.replacingOccurrences(of: "中文", with: "zhongwen") }
        #expect(await eventually {
            harness.logLines.get.contains {
                $0.contains("(edit_settled); 0 corrections, 1 rejected, 0 words and 0 aliases learned")
            }
        })
        // Not even a pending pair, so a second such dictation cannot make an
        // alias either.
        #expect(harness.learningFile == nil)
        for line in harness.logLines.get {
            #expect(!line.contains("zhongwen") && !line.contains("中文"), "\(line)")
        }
        expectNoSentinel(harness)
    }

    @Test("the same fix with the word written as a name is learned")
    func committedTransliterationIsLearned() async {
        let harness = await run(reply: Self.chinese) { $0.replacingOccurrences(of: "中文", with: "Zhongwen") }
        #expect(await eventually { harness.learningFile?.words.map(\.text) == ["Zhongwen"] })
        expectNoSentinel(harness)
    }
}

@Suite("The Accessibility watch passes on how it ended and the span only")
struct AccessibilityWatchTests {

    private func result(_ condition: DictationTerminalCondition, edited: String?,
                        spanObservedToEnd: Bool = false, sawChange: Bool = false) -> DictationObservationResult {
        DictationObservationResult(
            observation: DictationObservation(
                dictatedText: "We use trovio", formattedText: nil, pastedText: "We use trovio",
                editedText: edited, terminalCondition: condition, observedApp: "com.apple.TextEdit",
                startedAt: Date(), terminatedAt: Date(),
                spanObservedToEnd: spanObservedToEnd, sawChange: sawChange),
            candidates: [])
    }

    @Test("the end names the condition and carries the observer's span")
    func endCarriesReasonAndSpan() {
        #expect(AccessibilityCorrectionWatch.end(result(.editSettled, edited: "We use Trovioo"))
                == DictationCorrectionWatchEnd(reason: "edit_settled", editedSpan: "We use Trovioo"))
        #expect(AccessibilityCorrectionWatch.end(result(.abandoned, edited: nil))
                == DictationCorrectionWatchEnd(reason: "abandoned", editedSpan: nil))
    }

    @Test("the end carries whether the span was observed to its end and whether it changed")
    func endCarriesSpanFlags() {
        #expect(AccessibilityCorrectionWatch.end(result(.focusLeft, edited: nil,
                                                        spanObservedToEnd: true, sawChange: true))
                == DictationCorrectionWatchEnd(reason: "focus_left", editedSpan: nil,
                                               spanObservedToEnd: true, sawChange: true))
        #expect(AccessibilityCorrectionWatch.end(result(.watchExpired, edited: nil,
                                                        spanObservedToEnd: true))
                == DictationCorrectionWatchEnd(reason: "watch_expired", editedSpan: nil,
                                               spanObservedToEnd: true))
    }
}
#endif
