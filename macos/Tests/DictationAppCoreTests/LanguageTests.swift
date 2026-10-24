// The language picker: Automatic by default, kept in the ai.scam.dictation
// defaults, and sent to the worker from the next dictation on, including
// to a worker that replaces the one that was running.

#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

@Suite("The languages on offer")
struct DictationLanguageTests {

    @Test("Automatic first, then ten languages, each with Whisper's code")
    func choices() {
        #expect(DictationLanguage.allCases.map(\.label) == [
            "Automatic", "English", "Chinese", "Japanese", "Korean", "Spanish",
            "French", "German", "Portuguese", "Italian", "Russian",
        ])
        #expect(DictationLanguage.allCases.map(\.code) == [
            nil, "en", "zh", "ja", "ko", "es", "fr", "de", "pt", "it", "ru",
        ])
    }

    @Test("an unset or unreadable stored value is Automatic")
    func unreadableIsAutomatic() {
        let scratch = ScratchDefaults()
        let settings = DictationSettings(defaults: scratch.defaults)
        #expect(settings.language == .automatic)
        scratch.defaults.set("klingon", forKey: DictationSettings.languageKey)
        #expect(settings.language == .automatic)
    }

    @Test("the settings key is Dictation's own")
    func key() {
        #expect(DictationSettings.languageKey == "dictation.language")
        #expect(DictationSettings.domain == "ai.scam.dictation")
    }
}

@MainActor
@Suite("The chosen language reaches the worker")
struct LanguageSelectionTests {

    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording && model.status.hasPrefix("Text inserted") })
    }

    @Test("Automatic is the default and sends no language")
    func automaticByDefault() async {
        let harness = Harness()
        let model = harness.model()
        #expect(model.language == .automatic)
        await model.toggle()
        await dictate(harness, model)
        #expect(harness.workers.made[0].state.get.transcribeLanguages == [nil])
    }

    @Test("a choice is saved, sent with the next dictation, and still there after a relaunch")
    func choiceIsPersistedAndSent() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        model.selectLanguage(.japanese)
        #expect(model.language == .japanese)
        #expect(harness.scratch.defaults.string(forKey: "dictation.language") == "ja")

        await dictate(harness, model)
        #expect(harness.workers.made[0].state.get.transcribeLanguages == ["ja"])

        let relaunched = harness.model()
        #expect(relaunched.language == .japanese)
    }

    @Test("going back to Automatic stops sending a language")
    func backToAutomatic() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        model.selectLanguage(.german)
        await dictate(harness, model)
        model.selectLanguage(.automatic)
        await dictate(harness, model)
        #expect(harness.workers.made[0].state.get.transcribeLanguages == ["de", nil])
    }

    @Test("a change made during a hold applies to the next hold, not this one",
          .timeLimit(.minutes(1)))
    func changeDuringAHold() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()

        harness.trigger.press()
        #expect(await eventually { model.recording })
        model.selectLanguage(.french)
        harness.trigger.release()
        #expect(await eventually { !model.busy && model.status.hasPrefix("Text inserted") })
        await dictate(harness, model)
        #expect(harness.workers.made[0].state.get.transcribeLanguages == [nil, "fr"])
    }

    @Test("a worker started after an idle release decodes in the chosen language",
          .timeLimit(.minutes(1)))
    func replacementWorkerKeepsTheLanguage() async {
        var harness = Harness()
        harness.timing.workerIdleSeconds = 0.2
        harness.timing.housekeepingSeconds = 0.05
        let model = harness.model()
        await model.toggle()
        model.selectLanguage(.korean)
        #expect(await eventually { harness.workers.made[0].state.get.shutdowns == 1 })

        // The press starts a fresh worker while the user speaks; it must not
        // fall back to detection.
        await dictate(harness, model)
        #expect(harness.workers.made.count == 2)
        #expect(harness.workers.made[1].state.get.transcribeLanguages == ["ko"])
    }
}
#endif
