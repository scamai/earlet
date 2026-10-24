#if canImport(Testing)
import Foundation
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Privacy-safe observation feedback")
struct ObservationFeedbackTests {
    @Test("an unreadable field gives a generic manual-add fallback")
    func unreadableField() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        #expect(vocabulary.observationNotice == "Checking this field for corrections…")
        vocabulary.endObservationFeedback(id: id, reason: "span_missing")
        #expect(vocabulary.observationNotice == "The field could not be read. You can add the word manually below.")
    }

    @Test("a stale completion cannot replace the current observation")
    func staleID() async {
        let vocabulary = Harness().model().vocabulary
        let first = UUID()
        let current = UUID()
        vocabulary.beginObservationFeedback(id: first)
        vocabulary.beginObservationFeedback(id: current)
        vocabulary.endObservationFeedback(id: first, reason: "no focused field")
        #expect(vocabulary.observationNotice == "Checking this field for corrections…")
        vocabulary.endObservationFeedback(id: current, reason: "anchor moved")
        #expect(vocabulary.observationNotice?.contains("manually") == true)
    }

    @Test("an observation result for an older begin cannot replace current checking")
    func staleObserveResult() async {
        let vocabulary = Harness().model().vocabulary
        let old = UUID()
        vocabulary.beginObservationFeedback(id: old)
        vocabulary.beginObservationFeedback(id: UUID())
        _ = await vocabulary.observe(dictationID: "stale", dictated: "we use trovio", edited: "we use Trovioo", observationID: old)
        #expect(vocabulary.observationNotice == "Checking this field for corrections…")
        #expect(vocabulary.rows.isEmpty)
    }

    @Test("abandoning an observation clears its transient feedback")
    func abandonedObservationClearsNotice() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        vocabulary.endObservationFeedback(id: id, reason: "abandoned")
        #expect(vocabulary.observationNotice == nil)
        vocabulary.beginObservationFeedback(id: UUID())
        vocabulary.endObservationFeedback(id: id, reason: "next_take")
        #expect(vocabulary.observationNotice == "Checking this field for corrections…")
    }

    @Test("a real pending pair says another matching correction is needed")
    func pendingPair() async {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        let result = await vocabulary.observe(dictationID: "pending-one", dictated: "the wifi password is sunshine",
                                               edited: "the wifi password is Sunshine42x!", observationID: id)
        #expect(result.wordsLearned == 0)
        #expect(result.pendingCorrections == 1)
        #expect(vocabulary.observationNotice?.contains("Another matching correction") == true)
        #expect(vocabulary.observationNotice?.contains("Sunshine") == false)
        #expect(harness.logLines.get.joined().contains("Sunshine") == false)
    }

    @Test("a rejected edit offers manual add without echoing text")
    func rejectedEdit() async {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        _ = await vocabulary.observe(dictationID: "rejected", dictated: "we have a big meeting",
                                     edited: "we have a large meeting", observationID: id)
        #expect(vocabulary.observationNotice?.contains("No vocabulary change") == true)
        #expect(vocabulary.observationNotice?.contains("big") == false)
        #expect(harness.logLines.get.joined().contains("meeting") == false)
    }

    @Test("an unchanged edit reports no correction and keeps an existing undo receipt")
    func unchangedEditKeepsUndo() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        _ = await vocabulary.observe(dictationID: "learn", dictated: "we use trovio", edited: "we use Trovioo")
        let token = try #require(vocabulary.undoToken)
        let learningNotice = try #require(vocabulary.learningNotice)
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        _ = await vocabulary.observe(dictationID: "same", dictated: "we use it", edited: "we use it", observationID: id)
        #expect(vocabulary.observationNotice == "No correction detected.")
        #expect(vocabulary.undoToken == token)
        #expect(vocabulary.learningNotice == learningNotice)
    }

    @Test("a saved correction uses the existing receipt and clears observation feedback")
    func savedCorrectionClearsObservation() async {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        _ = await vocabulary.observe(dictationID: "saved", dictated: "we use trovio", edited: "we use Trovioo", observationID: id)
        #expect(vocabulary.observationNotice == nil)
        #expect(vocabulary.learningNotice?.contains("Remembered") == true)
        #expect(harness.learningFile?.words.map(\.text) == ["Trovioo"])
    }

    @Test("a failed save leaves the existing unsaved warning")
    func failedSaveLeavesUnsavedWarning() async throws {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        #expect(await vocabulary.teach(written: "SkyDock", soundsLike: ""))
        try FileManager.default.removeItem(at: harness.learningURL)
        try FileManager.default.createDirectory(at: harness.learningURL, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: harness.learningURL) }
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        let result = await vocabulary.observe(dictationID: "failed", dictated: "we use trovio",
                                               edited: "we use Trovioo", observationID: id)
        #expect(result.save == .failed)
        #expect(vocabulary.unsavedNotice != nil)
        #expect(vocabulary.observationNotice == nil)
    }

    @Test("dismiss and manual teach clear stale observation feedback")
    func dismissAndTeachClearObservation() async {
        let harness = Harness()
        let vocabulary = harness.model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        vocabulary.dismissLearningNotice()
        #expect(vocabulary.observationNotice == nil)
        vocabulary.beginObservationFeedback(id: UUID())
        _ = await vocabulary.teach(written: "SkyDock", soundsLike: "")
        #expect(vocabulary.observationNotice == nil)
        _ = harness
    }

    @Test("an already-known correction update is not reported as pending")
    func knownCorrectionIsNotPending() async {
        let vocabulary = Harness().model().vocabulary
        _ = await vocabulary.observe(dictationID: "first", dictated: "we use trovio", edited: "we use Trovioo")
        _ = await vocabulary.observe(dictationID: "second", dictated: "we use trovio", edited: "we use Trovioo")
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        let result = await vocabulary.observe(dictationID: "third", dictated: "we use trovio", edited: "we use Trovioo", observationID: id)
        #expect(result.pendingCorrections == 0)
        #expect(vocabulary.observationNotice?.contains("Another matching correction") == false)
    }

    // ─── The watch's end and the shared list ───

    @Test("an untouched watch whose field lost focus reports no correction detected")
    func untouchedFocusLeftReportsNoCorrection() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        vocabulary.endObservationFeedback(id: id, reason: "focus_left", spanObservedToEnd: true)
        #expect(vocabulary.observationNotice == "No correction detected.")
    }

    @Test("an untouched watch whose ninety seconds ran out reports no correction detected")
    func untouchedTimeoutReportsNoCorrection() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        vocabulary.endObservationFeedback(id: id, reason: "watch_expired", spanObservedToEnd: true)
        #expect(vocabulary.observationNotice == "No correction detected.")
    }

    @Test("a watch that never got its span to the end still says the field could not be checked")
    func unobservedEndStillUncheckable() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        vocabulary.endObservationFeedback(id: id, reason: "watch_expired")
        #expect(vocabulary.observationNotice == "This field could not be checked. You can add the word manually below.")
    }

    @Test("a span that changed but never settled into a revision learned nothing")
    func unsettledChangeLearnedNothing() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        vocabulary.endObservationFeedback(id: id, reason: "focus_left", spanObservedToEnd: true, sawChange: true)
        #expect(vocabulary.observationNotice == "No vocabulary change was learned. You can add the word manually below.")
    }

    @Test("a revision's notice stands when the watch runs its course and goes when the observer stopped")
    func revisionNoticeStandsWhenWatchRunsItsCourse() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        _ = await vocabulary.observe(dictationID: "pending-one", dictated: "the wifi password is sunshine",
                                     edited: "the wifi password is Sunshine42x!", observationID: id, keepObserving: true)
        #expect(vocabulary.observationNotice?.contains("Another matching correction") == true)

        vocabulary.endObservationFeedback(id: id, reason: "focus_left", revised: true,
                                          spanObservedToEnd: true, sawChange: true)
        #expect(vocabulary.observationNotice?.contains("Another matching correction") == true)

        let next = UUID()
        vocabulary.beginObservationFeedback(id: next)
        _ = await vocabulary.observe(dictationID: "pending-two", dictated: "the wifi password is sunshine",
                                     edited: "the wifi password is Sunshine43x!", observationID: next, keepObserving: true)
        #expect(vocabulary.observationNotice?.contains("Another matching correction") == true)
        vocabulary.endObservationFeedback(id: next, reason: "span_context_lost", revised: true)
        #expect(vocabulary.observationNotice == nil)
    }

    @Test("an end from outside clears even a revision's notice")
    func outsideEndClearsRevisionNotice() async {
        let vocabulary = Harness().model().vocabulary
        let id = UUID()
        vocabulary.beginObservationFeedback(id: id)
        _ = await vocabulary.observe(dictationID: "pending-one", dictated: "the wifi password is sunshine",
                                     edited: "the wifi password is Sunshine42x!", observationID: id, keepObserving: true)
        #expect(vocabulary.observationNotice?.contains("Another matching correction") == true)

        vocabulary.endObservationFeedback(id: id, reason: "abandoned", revised: true,
                                          spanObservedToEnd: true, sawChange: true)
        #expect(vocabulary.observationNotice == nil)
    }

    @Test("a stale watch's completed end cannot replace the current observation's checking line")
    func staleCompletedEndCannotReplaceChecking() async {
        let vocabulary = Harness().model().vocabulary
        let first = UUID()
        let current = UUID()
        vocabulary.beginObservationFeedback(id: first)
        vocabulary.beginObservationFeedback(id: current)
        vocabulary.endObservationFeedback(id: first, reason: "watch_expired", spanObservedToEnd: true)
        #expect(vocabulary.observationNotice == "Checking this field for corrections…")
        vocabulary.endObservationFeedback(id: current, reason: "focus_left", spanObservedToEnd: true)
        #expect(vocabulary.observationNotice == "No correction detected.")
    }
}
#endif
