#if canImport(Testing)
import Foundation
import Testing
import DictationKit
@testable import DictationAppCore

private final class RetryWorkerBox: @unchecked Sendable {
    var worker: FakeWorker?
}

@MainActor
@Suite("The Mac ASR retry transport")
struct DictationRetryTransportTests {
    private func timing() -> DictationTiming {
        var value = DictationTiming()
        value.transcribeSeconds = 0.02
        value.transcribeSecondsPerAudioSecond = 0
        return value
    }

    private func started(_ worker: FakeWorker, in slot: WorkerSlot) async {
        slot.worker = worker
        #expect(await slot.start())
    }

    private func transport(
        first: FakeWorker,
        replacement: FakeWorker? = nil,
        gate: InsertionGate = InsertionGate(),
        box: RetryWorkerBox = RetryWorkerBox()
    ) async -> (DictationRetryTransport, WorkerSlot, InsertionGate, RetryWorkerBox) {
        let slot = WorkerSlot()
        await started(first, in: slot)
        box.worker = replacement
        let retry = DictationRetryTransport(
            slot: slot,
            gate: gate,
            timing: timing(),
            restart: { old in
                guard let replacement = box.worker else { return false }
                slot.worker = replacement
                return await replacement.start()
            },
            log: { _ in })
        return (retry, slot, gate, box)
    }

    @Test("a worker death retries once with the same audio, language, and scene")
    func workerDeathRetriesWithRequestContext() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.failure(.ioError("eof"))] }
        let replacement = FakeWorker(name: "replacement")
        replacement.state.mutate { $0.replies = [.text("recovered")] }
        let (retry, slot, _, _) = await transport(first: first, replacement: replacement)
        slot.setLanguageCode("zh")
        slot.setVocabularyScene("terminal")

        let samples: [Float] = [0.1, -0.2, 0.3]
        let outcome = await retry.transcribeDetailed(
            samples: samples, sampleRate: 16_000, compareBaseline: false)
        #expect(outcome == .result(DictationAsrDetail(text: "recovered", avgLogprob: nil)))
        #expect(first.state.get.transcribeSampleCounts == [samples.count])
        #expect(replacement.state.get.transcribeSampleCounts == [samples.count])
        #expect(first.state.get.transcribeLanguages == ["zh"])
        #expect(replacement.state.get.transcribeLanguages == ["zh"])
    }

    @Test("a deadline kills the wedged worker and the replacement answers")
    func deadlineKillsAndDrainsBeforeRetry() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.hang] }
        let replacement = FakeWorker(name: "replacement")
        replacement.state.mutate { $0.replies = [.text("after deadline")] }
        let (retry, _, _, _) = await transport(first: first, replacement: replacement)

        let outcome = await retry.transcribe(samples: [0.2, 0.3], sampleRate: 16_000)
        #expect(outcome == .text("after deadline"))
        #expect(first.state.get.kills == 1)
        #expect(replacement.state.get.transcribes == 1)
    }

    @Test("a second worker failure does not start a third attempt")
    func secondFailureStopsAfterOneRetry() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.failure(.ioError("first eof"))] }
        let replacement = FakeWorker(name: "replacement")
        replacement.state.mutate { $0.replies = [.failure(.ioError("second eof"))] }
        let (retry, _, _, _) = await transport(first: first, replacement: replacement)

        let outcome = await retry.transcribe(samples: [0.1], sampleRate: 16_000)
        #expect(outcome == .failed(.ioError("second eof")))
        #expect(first.state.get.transcribes == 1)
        #expect(replacement.state.get.transcribes == 1)
    }

    @Test("an engine error is returned without retrying")
    func engineErrorDoesNotRetry() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.failure(.engineError("bad audio"))] }
        let replacement = FakeWorker(name: "replacement")
        let (retry, _, _, _) = await transport(first: first, replacement: replacement)

        let outcome = await retry.transcribe(samples: [0.1], sampleRate: 16_000)
        #expect(outcome == .failed(.engineError("bad audio")))
        #expect(first.state.get.transcribes == 1)
        #expect(replacement.state.get.transcribes == 0)
    }

    @Test("an already abandoned gate never calls ASR or restart")
    func abandonedBeforeRequestDoesNothing() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.failure(.ioError("eof"))] }
        let replacement = FakeWorker(name: "replacement")
        let gate = InsertionGate()
        #expect(gate.abandon())
        let (retry, _, _, _) = await transport(first: first, replacement: replacement, gate: gate)

        #expect(await retry.transcribe(samples: [0.1], sampleRate: 16_000)
            == .failed(.workerUnavailable))
        #expect(first.state.get.transcribes == 0)
        #expect(replacement.state.get.starts == 0)
    }

    @Test("abandoning during restart never invokes the replacement")
    func abandonedDuringRestartStopsBeforeSecondRequest() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.failure(.ioError("eof"))] }
        let replacement = FakeWorker(name: "replacement")
        replacement.state.mutate { $0.replies = [.text("must not arrive")] }
        let gate = InsertionGate()
        let box = RetryWorkerBox()
        let slot = WorkerSlot()
        await started(first, in: slot)
        box.worker = replacement
        let retry = DictationRetryTransport(
            slot: slot, gate: gate, timing: timing(),
            restart: { _ in
                slot.worker = replacement
                #expect(gate.abandon())
                return await replacement.start()
            }, log: { _ in })

        let outcome = await retry.transcribe(samples: [0.1], sampleRate: 16_000)
        #expect(outcome == .failed(.ioError("eof")))
        #expect(first.state.get.transcribes == 1)
        #expect(replacement.state.get.transcribes == 0)
    }

    @Test("a timed-out old attempt kills only its old worker")
    func oldAttemptCannotKillReplacement() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.hang] }
        let replacement = FakeWorker(name: "replacement")
        replacement.state.mutate { $0.replies = [.text("safe retry")] }
        let (retry, _, _, _) = await transport(first: first, replacement: replacement)

        let outcome = await retry.transcribe(samples: [0.1], sampleRate: 16_000)
        #expect(outcome == .text("safe retry"))
        #expect(first.state.get.kills == 1)
        #expect(replacement.state.get.kills == 0)
        #expect(replacement.state.get.transcribes == 1)
    }

    @Test("a successful request is sent exactly once")
    func successDoesNotRetry() async {
        let first = FakeWorker(name: "first")
        first.state.mutate { $0.replies = [.text("one answer")] }
        let (retry, _, _, _) = await transport(first: first)

        #expect(await retry.transcribe(samples: [0.1, 0.2], sampleRate: 16_000)
            == .text("one answer"))
        #expect(first.state.get.transcribes == 1)
        #expect(first.state.get.kills == 0)
    }

    @Test("insertion gate refuses work after begin or abandon")
    func insertionGateTransitionsAreOneWay() {
        let inserting = InsertionGate()
        #expect(inserting.beginInsertion())
        #expect(!inserting.abandon())
        #expect(!inserting.isAbandoned)

        let abandoned = InsertionGate()
        #expect(abandoned.abandon())
        #expect(!abandoned.beginInsertion())
        #expect(abandoned.isAbandoned)
    }
}
#endif
