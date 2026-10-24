#if canImport(Testing)
import Foundation
import DictationAudioCapture
import DictationKit
import Testing
@testable import DictationAppCore

@MainActor
@Suite("Private attempt receipts and completed setup")
struct AttemptDiagnosticsTests {
    @Test("one final receipt includes measured release time and never dictated content")
    func measuredReceipt() {
        let lines = Locked([String]())
        var now = 10.0
        let receipt = DictationAttemptDiagnostics(log: { line in lines.mutate { $0.append(line) } }, clock: { now })
        now = 12
        receipt.markReleased()
        now = 12.25
        var timings = DictationSession.StageTimings()
        timings.asrMs = 125
        receipt.finish(.success(text: "private spoken words", clipboardByteIdentical: true,
                                detectedLanguage: "private metadata", timings: timings))
        receipt.finish(.timedOut)
        #expect(lines.get.count == 3)
        let ending = lines.get.last ?? ""
        #expect(ending.contains("event=finished result=inserted"))
        #expect(ending.contains("elapsed_ms=2250 release_ms=250"))
        #expect(ending.contains("asr_ms=125 paste_ms=n/a"))
        #expect(!lines.get.joined().contains("private"))
    }

    @Test("failure payloads and unmeasured durations are not logged as content or zero")
    func privateFailure() {
        let lines = Locked([String]())
        let receipt = DictationAttemptDiagnostics(log: { line in lines.mutate { $0.append(line) } })
        receipt.finish(.failed(stage: .asr, failure: .asr(.engineError("private worker payload")), timings: .init()))
        let ending = lines.get.last ?? ""
        #expect(ending.contains("stage=asr reason=recognition_failed"))
        #expect(ending.contains("asr_ms=n/a paste_ms=n/a"))
        #expect(ending.contains("asr_utf16=n/a asr_repeat=n/a asr_compact_repeat=n/a"))
        #expect(ending.contains("candidate_utf16=n/a candidate_repeat=n/a candidate_compact_repeat=n/a"))
        #expect(ending.contains("format_changed=n/a"))
        #expect(ending.contains("release_ms=n/a"))
        #expect(!ending.contains("private"))
    }

    @Test("ASR and formatter counts reach the attempt receipt without modifying the insertion",
          arguments: ["private " + String(repeating: "A", count: 37), "hello there", ""])
    func textShapeWiring(_ raw: String) async {
        let harness = Harness()
        let model = harness.model()
        await model.toggle()
        harness.workers.made[0].state.mutate { $0.replies = [.text(raw)] }
        harness.trigger.press()
        #expect(await eventually { model.recording })
        harness.trigger.release()
        #expect(await eventually { harness.logLines.get.contains { $0.contains("event=finished") } })
        #expect(await eventually { !model.busy && !model.recording })
        let endings = harness.logLines.get.filter { $0.contains("event=finished") }
        #expect(endings.count == 1)
        let receipt = endings.first ?? ""
        #expect(receipt.contains("asr_utf16=\(raw.utf16.count)"))
        if raw.isEmpty {
            #expect(receipt.contains("asr_repeat=0 asr_compact_repeat=0"))
            #expect(receipt.contains("candidate_utf16=0 candidate_repeat=0"))
            #expect(harness.inserts.texts.get.isEmpty)
        } else {
            let inserted = harness.inserts.texts.get.first ?? ""
            #expect(harness.inserts.texts.get.count == 1)
            #expect(receipt.contains("candidate_utf16=\(inserted.utf16.count)"))
            #expect(receipt.contains("format_changed=\(!inserted.utf16.elementsEqual(raw.utf16))"))
            #expect(!harness.logLines.get.joined().contains(raw))
            if raw.contains("AAAA") {
                #expect(inserted.contains(String(repeating: "A", count: 37)))
                #expect(receipt.contains("asr_repeat=37 asr_compact_repeat=37"))
                #expect(receipt.contains("candidate_repeat=37 candidate_compact_repeat=37"))
            }
        }
        await model.prepareForQuit()
    }

    @Test("the real model finishes each attempt and only insertion completes setup",
          arguments: ["inserted", "paste_failed", "no_text", "privacy_refused", "microphone_permission", "cancelled"])
    func modelWiring(_ scenario: String) async {
        let harness = Harness()
        let model = harness.model()
        #expect(!model.hasCompletedFirstDictation)
        await model.toggle()
        switch scenario {
        case "paste_failed": harness.inserts.outcome.mutate { $0 = .failed(.focusedElementNotEditable) }
        case "no_text": harness.workers.made[0].state.mutate { $0.replies = [.text("")] }
        case "privacy_refused": harness.privacy.mutate { $0 = .secureFieldFocused }
        case "microphone_permission": harness.capture.state.mutate { $0.warmUpError = MicCaptureError.permissionDenied }
        default: break
        }
        harness.trigger.press()
        if scenario != "privacy_refused", scenario != "microphone_permission" {
            #expect(await eventually { model.recording })
            if scenario == "cancelled" { await model.cancel() }
            else { harness.trigger.release() }
        }
        #expect(await eventually { harness.logLines.get.contains { $0.contains("event=finished") } })
        #expect(await eventually { !model.busy && !model.recording })
        let endings = harness.logLines.get.filter { $0.contains("event=finished") }
        #expect(endings.count == 1)
        #expect(!harness.logLines.get.joined().contains("Hello world"))
        let completed = scenario == "inserted"
        #expect(model.hasCompletedFirstDictation == completed)
        #expect(harness.settings.hasCompletedFirstDictation == completed)
        model.recentResult.clear()
        #expect(harness.model().hasCompletedFirstDictation == completed,
                "setup progress survives dismissal and relaunch without storing text")
        if scenario == "paste_failed" {
            #expect(endings[0].contains("stage=paste reason=field_not_editable"))
            #expect(harness.logLines.get.contains { $0.contains("event=recovery clipboard=true available_in_app=true") })
        } else if scenario == "privacy_refused" {
            #expect(endings[0].contains("result=privacy_refused"))
            #expect(harness.capture.state.get.starts == 0)
        } else if scenario == "microphone_permission" {
            #expect(endings[0].contains("stage=capture reason=microphone_permission"))
        } else if completed {
            #expect(endings[0].contains("result=inserted"))
        }
        await model.prepareForQuit()
    }
}
#endif
