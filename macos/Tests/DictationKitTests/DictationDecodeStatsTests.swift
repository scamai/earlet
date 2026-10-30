#if canImport(Testing)
import DictationIPC
import Testing
@testable import DictationKit

@Suite("The worker's decode counts are read without its text")
struct DictationDecodeStatsTests {
    @Test("a vocabulary-search decode reports its wait, time, windows, retries and warmth")
    func searchDecode() {
        let stats: JSONValue = .object([
            "waited_ms": .number(220.92), "decode_s": .number(3.102), "engine_warm": .bool(true),
            "language": .string("zh"),
            "vocabulary_search": .object(["beam_windows": .number(2), "sampling_fallbacks": .number(1),
                                          "matched_terms": .number(1), "loops_avoided": .number(1)]),
        ])
        #expect(DictationDecodeStats(stats: stats)
                == DictationDecodeStats(waitedMs: 220.92, decodeMs: 3102, windows: 2, retries: 1,
                                        loopsAvoided: 1, warm: true))
    }

    @Test("a greedy decode has no window or retry count")
    func greedyDecode() {
        let parsed = DictationDecodeStats(stats: .object(["waited_ms": .number(0), "decode_s": .number(0.9),
                                                          "engine_warm": .bool(false)]))
        #expect(parsed?.windows == nil && parsed?.retries == nil)
        #expect(parsed?.warm == false && parsed?.decodeMs == 900)
    }

    @Test("a reply without any of the counts reports nothing")
    func nothing() {
        #expect(DictationDecodeStats(stats: nil) == nil)
        #expect(DictationDecodeStats(stats: .object(["language": .string("en")])) == nil)
    }
}
#endif
