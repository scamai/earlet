using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictation.Core.Learning;

public static class LearningScene
{
    public static string? Normalize(string? scene) => scene is "terminal" or "wechat" or "other" ? scene : null;
    public static string? ForApplication(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Equals("WeChat", StringComparison.OrdinalIgnoreCase) || name.Equals("Weixin", StringComparison.OrdinalIgnoreCase)) return "wechat";
        return Dictation.Services.DictationTextFormatter.IsTerminal(name) ? "terminal" : "other";
    }
}

/// <summary>Optional, bounded correction evidence kept inside schema 1.</summary>
public sealed class LearningEvidence
{
    [JsonRequired]
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonRequired]
    [JsonPropertyName("observations")] public List<LearningObservationEvidence> Observations { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LearningObservationEvidence
{
    [JsonRequired]
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonRequired]
    [JsonPropertyName("at")] public long At { get; set; }
    [JsonRequired]
    [JsonPropertyName("weight")] public long Weight { get; set; } = 1;
    [JsonRequired]
    [JsonPropertyName("corrections")] public List<LearningCorrectionEvidence> Corrections { get; set; } = [];
    [JsonPropertyName("first_seen")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? FirstSeen { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LearningCorrectionEvidence
{
    [JsonRequired]
    [JsonPropertyName("heard")] public string Heard { get; set; } = "";
    [JsonRequired]
    [JsonPropertyName("meant")] public string Meant { get; set; } = "";
    [JsonRequired]
    [JsonPropertyName("word")] public bool Word { get; set; }
    [JsonRequired]
    [JsonPropertyName("alias")] public bool Alias { get; set; }
    [JsonRequired]
    [JsonPropertyName("secret")] public bool Secret { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

internal static class LearningEvidenceEngine
{
    internal static (List<string> Words, List<string> Aliases) Revise(
        LearningState state, string id, IReadOnlyList<CorrectionCandidate> candidates, long at, ILearningLexicon lexicon, string? scene = null)
    {
        var oldWords = state.Words.Select(w => LearningText.Lower(w.Text)).ToHashSet(StringComparer.Ordinal);
        var oldAliases = state.Aliases.Select(a => (a.Heard, a.Meant)).ToHashSet();
        if (string.IsNullOrEmpty(id) || id.EnumerateRunes().Count() > 128 || id.StartsWith("legacy:", StringComparison.Ordinal) || !lexicon.IsAvailable)
            return ([], []);
        // A correction whose meant text has no letter (a PIN) is never journaled (§4.6).
        candidates = candidates.Where(c => LearningText.HasLetter(c.Meant)).ToList();
        LearningEvidence? journal;
        if (state.Extra?.TryGetValue("evidence", out var raw) == true)
        {
            try { journal = raw.Deserialize<LearningEvidence>(); }
            catch (JsonException) { return ([], []); }
            if (journal is null || !Valid(journal)) return ([], []);
        }
        else
        {
            if (candidates.Count == 0) return ([], []);
            journal = Seed(state, lexicon);
        }
        var records = journal.Observations;
        var active = state.Aliases.Select(a => (a.Heard, LearningText.Lower(a.Meant))).ToHashSet();
        var lastPair = new Dictionary<(string, string), long>();
        foreach (var record in records)
            foreach (var correction in record.Corrections)
                if (correction.Alias || (correction.Word && correction.Secret))
                {
                    var key = (correction.Heard, LearningText.Lower(correction.Meant));
                    lastPair[key] = Math.Max(lastPair.GetValueOrDefault(key, record.At), record.At);
                }
        var cutoff = Cutoff(at);
        foreach (var record in records)
            foreach (var correction in record.Corrections)
                if (lastPair.TryGetValue((correction.Heard, LearningText.Lower(correction.Meant)), out var seen)
                    && seen < cutoff && !active.Contains((correction.Heard, LearningText.Lower(correction.Meant))))
                {
                    correction.Alias = false;
                    if (correction.Secret) correction.Word = false;
                }
        var next = candidates.Take(32).Where(c => c.Heard.EnumerateRunes().Count() <= 256 && c.Meant.EnumerateRunes().Count() <= 256)
            .Select(c => Contribution(c, lexicon)).ToList();
        var old = records.FirstOrDefault(r => r.Id == id);
        if (old is null || !Same(old.Corrections, next))
        {
            records.RemoveAll(r => r.Id == id);
            if (next.Count > 0)
            {
                var extra = old?.Extra is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(old.Extra);
                if (old is null && LearningScene.Normalize(scene) is { } validScene)
                    extra["scene"] = JsonSerializer.SerializeToElement(validScene);
                records.Add(new LearningObservationEvidence { Id = id, At = at, Weight = 1, Corrections = next,
                    FirstSeen = old?.FirstSeen, Extra = extra.Count == 0 ? null : extra });
            }
        }
        records.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : LearningText.CodePointOrder.Compare(a.Id, b.Id));
        if (records.Count > 512) records.RemoveRange(0, records.Count - 512);
        state.Extra ??= new Dictionary<string, JsonElement>();
        state.Extra["evidence"] = JsonSerializer.SerializeToElement(journal);
        return Rebuild(state, records, at, oldWords, oldAliases);
    }

    private static LearningEvidence Seed(LearningState state, ILearningLexicon lexicon)
    {
        var result = new LearningEvidence { Version = 1 };
        var byId = new Dictionary<string, LearningObservationEvidence>(StringComparer.Ordinal);
        foreach (var word in state.Words.Where(w => w.Origin == LearningOrigins.Correction))
            byId[$"legacy:word:{LearningText.Lower(word.Text)}"] = new LearningObservationEvidence {
                Id = $"legacy:word:{LearningText.Lower(word.Text)}", At = word.LastSeen, FirstSeen = word.FirstSeen,
                Weight = Math.Max(1, Math.Min(4, word.Count)), Corrections = [new LearningCorrectionEvidence {
                    Heard = "", Meant = word.Text, Word = true, Secret = false }] };
        foreach (var alias in state.Aliases.Where(a => a.Origin == LearningOrigins.Correction))
            byId[$"legacy:alias:{alias.Heard}"] = new LearningObservationEvidence {
                Id = $"legacy:alias:{alias.Heard}", At = alias.LastSeen, FirstSeen = alias.FirstSeen,
                Weight = Math.Max(1, Math.Min(4, alias.Count)), Corrections = [new LearningCorrectionEvidence {
                    Heard = alias.Heard, Meant = alias.Meant, Alias = true, Secret = LearningPolicy.IsSecretShaped(alias.Meant) }] };
        foreach (var pending in state.Pending)
            foreach (var pendingId in pending.Dictations)
            {
                var candidate = new CorrectionCandidate(pending.Heard, pending.Meant, CorrectionKind.Spelling);
                if (!byId.TryGetValue(pendingId, out var record))
                    byId[pendingId] = record = new LearningObservationEvidence { Id = pendingId, At = pending.LastSeen, Weight = 1 };
                var correction = new LearningCorrectionEvidence {
                    Heard = pending.Heard, Meant = pending.Meant,
                    Word = LearningPolicy.LearnsWord(candidate, lexicon)
                        && !state.Words.Any(w => LearningText.Lower(w.Text) == LearningText.Lower(pending.Meant)),
                    Alias = LearningPolicy.LearnsAlias(candidate, lexicon), Secret = LearningPolicy.IsSecretShaped(pending.Meant) };
                if (!record.Corrections.Any(c => c.Heard == correction.Heard && c.Meant == correction.Meant
                    && c.Word == correction.Word && c.Alias == correction.Alias && c.Secret == correction.Secret))
                    record.Corrections.Add(correction);
            }
        result.Observations = byId.Values.ToList();
        return result;
    }

    private static LearningCorrectionEvidence Contribution(CorrectionCandidate candidate, ILearningLexicon lexicon) => new() {
        Heard = LearningTokens.HeardKey(candidate.Heard), Meant = candidate.Meant,
        Word = LearningPolicy.LearnsWord(candidate, lexicon), Alias = LearningPolicy.LearnsAlias(candidate, lexicon),
        Secret = LearningPolicy.IsSecretShaped(candidate.Meant) };

    private static bool Same(IReadOnlyList<LearningCorrectionEvidence> left, IReadOnlyList<LearningCorrectionEvidence> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Heard == pair.Second.Heard
            && pair.First.Meant == pair.Second.Meant && pair.First.Word == pair.Second.Word
            && pair.First.Alias == pair.Second.Alias && pair.First.Secret == pair.Second.Secret);

    private static (List<string> Words, List<string> Aliases) Rebuild(
        LearningState state, List<LearningObservationEvidence> records, long at,
        HashSet<string> previousWords, HashSet<(string, string)> previousAliases)
    {
        var oldWords = state.Words.GroupBy(w => LearningText.Lower(w.Text), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var oldAliases = state.Aliases.GroupBy(a => a.Heard, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var protectedWords = oldWords.Where(p => p.Value.Origin != LearningOrigins.Correction).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var protectedAliases = oldAliases.Where(p => p.Value.Origin != LearningOrigins.Correction).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var removedWords = state.Removed.Words.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        var removedAliases = state.Removed.Aliases.Select(a => (a.Heard, a.Meant)).ToHashSet();
        var words = new Dictionary<string, Dictionary<string, (LearningObservationEvidence Record, LearningCorrectionEvidence Correction)>>(StringComparer.Ordinal);
        var pairs = new Dictionary<(string Heard, string Key), Dictionary<string, (LearningObservationEvidence Record, LearningCorrectionEvidence Correction)>>();
        var replacements = new Dictionary<string, Dictionary<string, LearningObservationEvidence>>(StringComparer.Ordinal);
        foreach (var record in records)
            foreach (var correction in record.Corrections)
            {
                var key = LearningText.Lower(correction.Meant);
                if (!string.IsNullOrEmpty(correction.Heard) && correction.Heard != key)
                    Get(replacements, correction.Heard)[record.Id] = record;
                if (removedWords.Contains(key)) continue;
                if (correction.Word && !protectedWords.ContainsKey(key)
                    && (!correction.Secret || !protectedAliases.ContainsKey(correction.Heard)))
                    Get(words, key)[record.Id] = (record, correction);
                if ((correction.Alias || (correction.Word && correction.Secret && !protectedWords.ContainsKey(key)))
                    && !protectedAliases.ContainsKey(correction.Heard)
                    && !protectedWords.ContainsKey(correction.Heard)
                    && !removedAliases.Contains((correction.Heard, key)))
                    Get(pairs, (correction.Heard, key))[record.Id] = (record, correction);
            }
        var nextWords = protectedWords.Values.ToList();
        foreach (var (key, entries) in words)
        {
            var support = entries.Values.Sum(x => x.Record.Weight);
            var opposition = replacements.GetValueOrDefault(key)?.Where(x => !entries.ContainsKey(x.Key)).Sum(x => x.Value.Weight) ?? 0;
            var latest = Latest(entries.Values);
            if (support - opposition < (latest.Correction.Secret ? 2 : 1)) continue;
            var row = oldWords.GetValueOrDefault(key) ?? new LearnedWord();
            row.Text = latest.Correction.Meant; row.Origin = LearningOrigins.Correction; row.Count = support;
            row.FirstSeen = entries.Values.Min(x => x.Record.FirstSeen ?? x.Record.At); row.LastSeen = latest.Record.At;
            row.Extra ??= new Dictionary<string, JsonElement>(); row.Extra["adaptive"] = Bool(true); row.Extra["opposition"] = Number(opposition);
            nextWords.Add(row);
        }
        var nextAliases = protectedAliases.Values.ToList();
        var pending = new List<PendingAlias>();
        foreach (var ((heard, key), entries) in pairs)
        {
            var negative = new Dictionary<string, LearningObservationEvidence>(replacements.GetValueOrDefault(key) ?? []);
            foreach (var rival in pairs.Where(p => p.Key.Heard == heard && p.Key.Key != key))
                foreach (var item in rival.Value) negative[item.Key] = item.Value.Record;
            var support = entries.Values.Sum(x => x.Record.Weight);
            var opposition = negative.Where(x => !entries.ContainsKey(x.Key)).Sum(x => x.Value.Weight);
            var latest = Latest(entries.Values);
            // Rival pairs share a heard key. Each projection needs its own row;
            // otherwise a later pending rival can mutate the winning alias.
            var oldAlias = oldAliases.GetValueOrDefault(heard);
            var row = new LearnedAlias { Extra = oldAlias?.Extra is null ? null : new Dictionary<string, JsonElement>(oldAlias.Extra) };
            row.Heard = heard; row.Meant = latest.Correction.Meant; row.Origin = LearningOrigins.Correction; row.Count = support;
            row.FirstSeen = entries.Values.Min(x => x.Record.FirstSeen ?? x.Record.At); row.LastSeen = latest.Record.At;
            row.Extra ??= new Dictionary<string, JsonElement>(); row.Extra["adaptive"] = Bool(true); row.Extra["opposition"] = Number(opposition);
            if (latest.Correction.Alias && support - opposition >= LearningLimits.AliasPromotionCount) nextAliases.Add(row);
            else if (row.LastSeen >= Cutoff(at) && (!latest.Correction.Secret || support - opposition < 2))
                pending.Add(new PendingAlias { Heard = heard, Meant = latest.Correction.Meant, Count = support,
                    Dictations = entries.Keys.OrderBy(i => entries[i].Record.At).ThenBy(i => i, LearningText.CodePointOrder).TakeLast(LearningLimits.PendingDictationIds).ToList(),
                    FirstSeen = row.FirstSeen, LastSeen = row.LastSeen });
        }
        state.Words = nextWords; state.Aliases = nextAliases; state.Pending = pending;
        return (state.Words.Where(w => !previousWords.Contains(LearningText.Lower(w.Text))).Select(w => w.Text).ToList(),
            state.Aliases.Where(a => !previousAliases.Contains((a.Heard, a.Meant))).Select(a => a.Heard).ToList());
    }

    private static Dictionary<string, TValue> Get<TKey, TValue>(Dictionary<TKey, Dictionary<string, TValue>> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var result)) map[key] = result = new(StringComparer.Ordinal);
        return result;
    }

    private static (LearningObservationEvidence Record, LearningCorrectionEvidence Correction) Latest(
        IEnumerable<(LearningObservationEvidence Record, LearningCorrectionEvidence Correction)> values) =>
        values.Aggregate((a, b) => a.Record.At > b.Record.At || (a.Record.At == b.Record.At
            && LearningText.CodePointOrder.Compare(a.Record.Id, b.Record.Id) >= 0) ? a : b);

    private static JsonElement Bool(bool value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Number(long value) => JsonSerializer.SerializeToElement(value);

    private static long Cutoff(long at) => at > long.MinValue + LearningLimits.PendingTtlMs
        ? at - LearningLimits.PendingTtlMs : long.MinValue;

    private static bool Valid(LearningEvidence journal) => journal.Version == 1 && journal.Observations is not null
        && journal.Observations.Count <= 4096
        && journal.Observations.Select(r => r?.Id).Distinct(StringComparer.Ordinal).Count() == journal.Observations.Count
        && journal.Observations.All(r => r is not null && !string.IsNullOrEmpty(r.Id)
            && r.Id.EnumerateRunes().Count() <= 320 && r.Weight is >= 1 and <= 4
            && r.Corrections is not null && r.Corrections.Count <= 32
            && r.Corrections.All(c => c is not null && c.Heard is not null && !string.IsNullOrEmpty(c.Meant)
                && c.Heard.EnumerateRunes().Count() <= 256 && c.Meant.EnumerateRunes().Count() <= 256));
}
