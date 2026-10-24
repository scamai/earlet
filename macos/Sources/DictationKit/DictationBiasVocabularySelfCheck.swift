import Foundation

/// Self-check for `DictationBiasVocabulary` and the engine policy that gates it.
///
/// The check this file exists for is the last one: **no test anywhere asserted
/// that the dictation transport sends `bias_phrases`**, which is why the field
/// could be absent from every dictation request while the worker, the merge in
/// `asr_server.py:198` and `test_bias_phrases_merge_default_and_per_call_deduped`
/// all passed. A plumbing gap with tests on both ends and none across the joint
/// is invisible by construction.
@discardableResult
public func runDictationBiasVocabularySelfCheck() -> Bool {
    var failures: [String] = []

    func expect(_ condition: Bool, _ message: String) {
        if !condition { failures.append(message) }
    }

    // --- vocabulary hygiene ---
    let messy = DictationBiasVocabulary(phrases: ["  Miles ", "Miles", "", "Knemo", "   "])
    expect(messy.phrases == ["Miles", "Knemo"],
           "trim + dedupe + drop-empty failed: \(messy.phrases)")

    let ordered = DictationBiasVocabulary(phrases: ["Zulu", "Alpha", "Mike"])
    expect(ordered.phrases == ["Zulu", "Alpha", "Mike"],
           "order must be preserved: BiasTrie assigns phrase ids by position")

    expect(DictationBiasVocabulary.empty.isEmpty, "empty vocabulary should be empty")

    // --- the ceiling is reported, never silent ---
    // Derive the overshoot from the current ceiling so the fixture still
    // exercises truncation when the configured limit changes.
    let overshoot = DictationBiasVocabulary.maxPhrases + 150
    let big = DictationBiasVocabulary(phrases: (0..<overshoot).map { "Term\($0)" })
    let (bounded, dropped) = big.bounded()
    expect(bounded.phrases.count == DictationBiasVocabulary.maxPhrases,
           "bounded() must cap at \(DictationBiasVocabulary.maxPhrases), got \(bounded.phrases.count)")
    expect(dropped == overshoot - DictationBiasVocabulary.maxPhrases,
           "bounded() must report what it dropped, got \(dropped)")
    let (small, none) = DictationBiasVocabulary(phrases: ["Miles"]).bounded()
    expect(none == 0 && small.phrases == ["Miles"], "no truncation should report zero dropped")

    // --- the engine policy ---
    // mlx still has no beam search, so phrases there take the initial_prompt
    // path. This check verifies that vocabulary reaches the configured
    // engine. It does not establish recognition accuracy or latency; those
    // require an audio evaluation of the packaged build.
    expect(DictationBiasPolicy.shouldSendPhrases(engine: "mlx", flagEnabled: true),
           "mlx must receive phrases as initial_prompt")
    expect(!DictationBiasPolicy.shouldSendPhrases(engine: "faster_whisper", flagEnabled: true),
           "must NOT send phrases to faster_whisper: its beam search is CTranslate2-internal, no trie hook")
    expect(DictationBiasPolicy.shouldSendPhrases(engine: "openai", flagEnabled: true),
           "openai engine runs the trie and should receive phrases")
    expect(!DictationBiasPolicy.shouldSendPhrases(engine: "openai", flagEnabled: false),
           "flag off must suppress phrases on every engine")
    expect(!DictationBiasPolicy.shouldSendPhrases(engine: nil, flagEnabled: true),
           "unknown engine must be treated as unsafe")

    // --- loading is fail-soft ---
    let tmp = FileManager.default.temporaryDirectory
        .appendingPathComponent("dictation-bias-selfcheck-\(UUID().uuidString)", isDirectory: true)
    try? FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: tmp) }

    let missing = tmp.appendingPathComponent("nope.json")
    expect(DictationBiasVocabularyLoader.load(from: missing).isEmpty,
           "a missing file must yield an empty vocabulary, not a failure")

    let garbage = tmp.appendingPathComponent("garbage.json")
    try? Data("not json at all".utf8).write(to: garbage)
    expect(DictationBiasVocabularyLoader.load(from: garbage).isEmpty,
           "a malformed file must yield an empty vocabulary: dictation must keep working")

    let bare = tmp.appendingPathComponent("bare.json")
    try? Data(#"["Miles","Knemo"]"#.utf8).write(to: bare)
    expect(DictationBiasVocabularyLoader.load(from: bare).phrases == ["Miles", "Knemo"],
           "bare array form should load")

    let wrapped = tmp.appendingPathComponent("wrapped.json")
    try? Data(#"{"phrases":["Miles","Trovioo"]}"#.utf8).write(to: wrapped)
    expect(DictationBiasVocabularyLoader.load(from: wrapped).phrases == ["Miles", "Trovioo"],
           "wrapped object form should load")

    // --- THE GAP THIS FILE EXISTS FOR: the field must cross the transport ---
    let fields = PythonAsrTransport.requestFieldsForTesting(
        vocabulary: DictationBiasVocabulary(phrases: ["Miles", "Knemo"]),
        engine: "openai", flagEnabled: true)
    expect(fields["bias_phrases"] != nil,
           "transport MUST send bias_phrases when the engine runs the trie and the flag is on")
    if case let .array(values)? = fields["bias_phrases"] {
        expect(values.count == 2, "expected 2 phrases on the wire, got \(values.count)")
    } else if fields["bias_phrases"] != nil {
        failures.append("bias_phrases must be a JSON array")
    }

    // The wire is the same field on both engines; what differs is what the
    // WORKER does with it (trie beam bias on openai, initial_prompt on mlx).
    // Keeping one field means the transport does not have to know which
    // mechanism ran, and the engine report stays the single source of truth.
    let onMlx = PythonAsrTransport.requestFieldsForTesting(
        vocabulary: DictationBiasVocabulary(phrases: ["Miles"]),
        engine: "mlx", flagEnabled: true)
    expect(onMlx["bias_phrases"] != nil,
           "transport must send bias_phrases to mlx: the worker turns them into initial_prompt")

    let noTrieNoPrompt = PythonAsrTransport.requestFieldsForTesting(
        vocabulary: DictationBiasVocabulary(phrases: ["Miles"]),
        engine: "faster_whisper", flagEnabled: true)
    expect(noTrieNoPrompt["bias_phrases"] == nil,
           "faster_whisper has neither a trie hook nor a prompt path: send nothing")

    let emptyVocab = PythonAsrTransport.requestFieldsForTesting(
        vocabulary: DictationBiasVocabulary.empty, engine: "openai", flagEnabled: true)
    expect(emptyVocab["bias_phrases"] == nil,
           "an empty vocabulary must send no field at all, not an empty array")

    // The wiring this feature shipped broken twice: the flag is injected, so a
    // build that never sets it sends nothing while looking fully wired. This
    // asserts the default is the SAFE one, and DictationControllerCheck asserts
    // the boundary actually assigns it.
    expect(!PythonAsrTransport.BiasState().flagEnabled,
           "a freshly constructed BiasState must default to OFF, never ON")

    if failures.isEmpty {
        print("DictationBiasVocabularySelfCheck: OK")
        return true
    }
    for failure in failures { print("DictationBiasVocabularySelfCheck: FAIL — \(failure)") }
    return false
}
