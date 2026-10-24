#if canImport(Testing)
import Foundation
import DictationKit
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Recent text recovery")
struct RecentResultTests {
    private func dictate(_ harness: Harness, _ model: DictationModel) async {
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { !model.busy && !model.recording })
    }

    @Test("the final formatted text can be copied again without another transcription")
    func copyInsertedText() async {
        let harness = Harness()
        let model = harness.model()
        #expect(model.recentResult.entry == nil)
        await model.copyRecentResult()
        #expect(harness.clipboard.get.isEmpty)
        await model.toggle()
        await dictate(harness, model)
        #expect(model.recentResult.entry?.text == harness.inserts.texts.get.last)
        #expect(model.recentResult.entry?.needsRecovery == false)
        await model.copyRecentResult()
        #expect(harness.clipboard.get == ["Hello world"])
        #expect(harness.workers.made[0].state.get.transcribes == 1)
        #expect(model.hudOutcome?.title == "Copied")
        #expect(model.recentResult.copyMessage?.contains("⌘V") == true)
        #expect(!harness.logLines.get.joined().contains("Hello world"))
        await model.prepareForQuit()
        #expect(model.recentResult.entry == nil)
        #expect(harness.model().recentResult.entry == nil, "no result survives a relaunch")
    }

    @Test("a refused clipboard does not lose the transcript, and a later copy recovers it")
    func clipboardRetry() async {
        let harness = Harness()
        harness.clipboardWorks.mutate { $0 = false }
        harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        #expect(model.recentResult.entry?.text == "Hello world")
        #expect(model.recentResult.entry?.needsRecovery == true)
        #expect(model.hudOutcome?.title == "Your words are still here")
        await model.copyRecentResult()
        #expect(model.hudOutcome?.title == "Couldn’t copy")
        #expect(model.recentResult.copyMessage?.contains("⌘V") == false)
        harness.clipboardWorks.mutate { $0 = true }
        await model.copyRecentResult()
        #expect(model.hudOutcome?.title == "Copied")
        #expect(harness.clipboard.get.last == "Hello world")
    }

    @Test("secure targets discard the current recovery buffer", arguments: [
        DictationInsertFailure.secureFieldFocused,
        .secureInputActive(heldByOtherProcess: true),
        .secureInputActive(heldByOtherProcess: false),
    ])
    func secureTargets(_ failure: DictationInsertFailure) async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        #expect(model.recentResult.entry != nil)
        harness.inserts.outcome.mutate { $0 = .failed(failure) }
        await dictate(harness, model)
        #expect(model.recentResult.entry == nil)
        await model.copyRecentResult()
        #expect(harness.clipboard.get.isEmpty)
    }

    @Test("only one result is kept and dismiss clears it without changing the clipboard")
    func replaceAndDismiss() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        let first = model.recentResult.entry?.id
        harness.workers.made[0].state.mutate { $0.replies = [.text("another sentence")] }
        await dictate(harness, model)
        #expect(model.recentResult.entry?.id != first)
        #expect(model.recentResult.entry?.text == "Another sentence")
        model.recentResult.clear()
        await model.copyRecentResult()
        #expect(harness.clipboard.get.isEmpty)
    }

    @Test("a late copy cannot label a replaced result as copied, or submit twice")
    func copyRace() async {
        var pending: CheckedContinuation<Bool, Never>?
        var calls = 0
        let result = DictationRecentResult { _ in
            calls += 1
            return await withCheckedContinuation { pending = $0 }
        }
        result.remember("first", needsRecovery: true)
        let copy = Task { await result.copy() }
        #expect(await eventually { pending != nil })
        #expect(await result.copy() == nil)
        result.remember("second", needsRecovery: false)
        pending?.resume(returning: true)
        #expect(await copy.value == nil)
        #expect(result.copyMessage == nil)
        #expect(result.entry?.text == "second")
        #expect(calls == 1)
    }

    @Test("copy cannot interfere with an active recording")
    func noCopyWhileRecording() async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        await dictate(harness, model)
        harness.trigger.press()
        #expect(await eventually { model.recording })
        await model.copyRecentResult()
        #expect(harness.clipboard.get.isEmpty)
        await model.cancel()
    }
}
#endif
