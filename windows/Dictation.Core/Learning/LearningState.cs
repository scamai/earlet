using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dictation.Core.Learning;

/// <summary>Where a word or alias came from, as the file spells it.</summary>
public static class LearningOrigins
{
    /// <summary>Learned from a correction.</summary>
    public const string Correction = "correction";

    /// <summary>Typed in by the user.</summary>
    public const string Taught = "taught";
}

/// <summary>
/// <c>learning.json</c>, schema <c>dictation.learning/1</c> (LEARNING.md §5).
/// </summary>
/// <remarks>
/// <para>
/// Mutable on purpose: <see cref="LearningBook"/> edits it in place the way the
/// reference edits its dict. Times are Unix milliseconds (UTC). Keys this
/// version does not know are kept in <c>Extra</c> and written back, as the
/// reference's dict would keep them.
/// </para>
/// <para>
/// <b>Reading.</b> The schema and every field of every entry are required
/// (<see cref="JsonRequiredAttribute"/>): an entry the reference could not have
/// written makes the whole file another schema. A missing top-level list reads
/// as empty, which is unambiguous.
/// </para>
/// </remarks>
public sealed class LearningState
{
    [JsonRequired]
    [JsonPropertyName("schema")]
    public string? Schema { get; set; } = LearningLimits.Schema;

    /// <summary>Most recently seen first; the worker reads that order as relevance.</summary>
    [JsonPropertyName("words")]
    public List<LearnedWord> Words { get; set; } = [];

    [JsonPropertyName("aliases")]
    public List<LearnedAlias> Aliases { get; set; } = [];

    [JsonPropertyName("pending")]
    public List<PendingAlias> Pending { get; set; } = [];

    [JsonPropertyName("removed")]
    public RemovedEntries Removed { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A word in the vocabulary.</summary>
public sealed class LearnedWord
{
    [JsonRequired]
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary><see cref="LearningOrigins.Correction"/> or
    /// <see cref="LearningOrigins.Taught"/>.</summary>
    [JsonRequired]
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = LearningOrigins.Correction;

    [JsonRequired]
    [JsonPropertyName("count")]
    public long Count { get; set; }

    [JsonRequired]
    [JsonPropertyName("first_seen")]
    public long FirstSeen { get; set; }

    [JsonRequired]
    [JsonPropertyName("last_seen")]
    public long LastSeen { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A rewrite: <see cref="Heard"/> (the heard key) becomes <see cref="Meant"/>.</summary>
public sealed class LearnedAlias
{
    [JsonRequired]
    [JsonPropertyName("heard")]
    public string Heard { get; set; } = "";

    [JsonRequired]
    [JsonPropertyName("meant")]
    public string Meant { get; set; } = "";

    [JsonRequired]
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = LearningOrigins.Correction;

    [JsonRequired]
    [JsonPropertyName("count")]
    public long Count { get; set; }

    [JsonRequired]
    [JsonPropertyName("first_seen")]
    public long FirstSeen { get; set; }

    [JsonRequired]
    [JsonPropertyName("last_seen")]
    public long LastSeen { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A pair waiting for its second dictation.</summary>
public sealed class PendingAlias
{
    [JsonRequired]
    [JsonPropertyName("heard")]
    public string Heard { get; set; } = "";

    [JsonRequired]
    [JsonPropertyName("meant")]
    public string Meant { get; set; } = "";

    /// <summary>Distinct dictations that made this correction.</summary>
    [JsonRequired]
    [JsonPropertyName("count")]
    public long Count { get; set; }

    /// <summary>The last few dictation ids (random per insertion, never text).</summary>
    [JsonRequired]
    [JsonPropertyName("dictations")]
    public List<string> Dictations { get; set; } = [];

    [JsonRequired]
    [JsonPropertyName("first_seen")]
    public long FirstSeen { get; set; }

    [JsonRequired]
    [JsonPropertyName("last_seen")]
    public long LastSeen { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>Tombstones: what the user removed and learning must not bring back.</summary>
public sealed class RemovedEntries
{
    [JsonPropertyName("words")]
    public List<RemovedWord> Words { get; set; } = [];

    [JsonPropertyName("aliases")]
    public List<RemovedAlias> Aliases { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RemovedWord
{
    /// <summary>The removed word, lowercase.</summary>
    [JsonRequired]
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonRequired]
    [JsonPropertyName("at")]
    public long At { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class RemovedAlias
{
    /// <summary>The heard key.</summary>
    [JsonRequired]
    [JsonPropertyName("heard")]
    public string Heard { get; set; } = "";

    /// <summary>The meant text, lowercase.</summary>
    [JsonRequired]
    [JsonPropertyName("meant")]
    public string Meant { get; set; } = "";

    [JsonRequired]
    [JsonPropertyName("at")]
    public long At { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
