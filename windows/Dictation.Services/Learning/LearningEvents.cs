namespace Dictation.Services.Learning;

/// <summary>
/// Event schemas for the learning directory (ch 11.5.6), the C# twin of
/// <c>transcription_worker/learning/schemas.py</c>. Every event carries
/// <c>schema</c>, <c>kind</c>, <c>id</c> and <c>ts</c> (unix ms, UTC). JSON
/// members a newer writer appended are kept in <see cref="LearningEvent.Extra"/>
/// and re-emitted, so an older reader never drops what a newer writer recorded.
/// Serialisation is canonical (sorted keys, no whitespace, non-ASCII unescaped,
/// one line): <see cref="LearningEvent.Dumps"/> produces the bytes the Python
/// reference produces, which is what makes the golden fixtures a bit-for-bit contract.
/// </summary>
public abstract class LearningEvent
{
    public const string SchemaVersion = "learning_events/0.1.0";

    public static readonly IReadOnlyList<string> Origins = new[] { "import", "typed", "retro", "edit", "notes", "synthetic" };
    public static readonly IReadOnlyList<string> States = new[] { "catalog", "probation", "live", "parked", "tombstone" };
    public static readonly IReadOnlyList<string> Observed = new[] { "none", "self", "app" };
    public static readonly IReadOnlyList<string> Terminals = new[] { "settled", "navigated", "cleared", "unobservable", "timeout" };
    public static readonly IReadOnlyList<string> ProbeKinds = new[] { "agreement", "false_trigger", "reachability", "smoke", "reprobe" };
    public static readonly IReadOnlyList<string> UserActions = new[] { "tombstone", "strike", "truncate", "unpark", "forget" };
    public static readonly IReadOnlyList<string> LedgerActions = new[] { "promote", "park", "unpark", "decay", "catalog", "budget", "revive", "alias" };
    public static readonly IReadOnlyList<string> Actions = UserActions.Concat(LedgerActions).ToArray();
    public static readonly IReadOnlyList<string> AdapterActions = new[] { "snapshot", "eval", "activate", "rollback" };

    /// <summary>Merge order for events that share a timestamp (schemas.KIND_ORDER).</summary>
    public static readonly IReadOnlyDictionary<string, int> KindOrder = new Dictionary<string, int>
    {
        ["claim"] = 0, ["hold"] = 1, ["edit"] = 2, ["probe"] = 3, ["decision"] = 4, ["adapter"] = 5,
    };

    public abstract string Kind { get; }
    public string Id { get; }
    public long Ts { get; }
    public Dictionary<string, object?> Extra { get; } = new(StringComparer.Ordinal);

    protected LearningEvent(string id, long ts)
    {
        if (string.IsNullOrEmpty(id))
        {
            throw new ArgumentException("event id must be a non-empty string", nameof(id));
        }
        Id = id;
        Ts = ts;
    }

    protected static void Check(string value, IReadOnlyList<string> allowed, string what)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{what} must be one of [{string.Join(", ", allowed)}], got '{value}'");
        }
    }

    /// <summary>The Python <c>to_dict()</c>: own fields, then extra, then schema and kind.</summary>
    public Dictionary<string, object?> ToDict()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        Fill(d);
        foreach (var kv in Extra)
        {
            d[kv.Key] = PyJson.Clone(kv.Value);
        }
        d["schema"] = SchemaVersion;
        d["kind"] = Kind;
        return d;
    }

    protected abstract void Fill(Dictionary<string, object?> d);

    public string Dumps() => PyJson.DumpsCompact(ToDict(), sortKeys: true);

    public static LearningEvent Loads(string line) => FromDict(PyJson.AsDict(PyJson.Parse(line)));

    public static LearningEvent FromDict(Dictionary<string, object?> d)
    {
        var kind = PyJson.AsStringOrNull(d.GetValueOrDefault("kind")) ?? throw new FormatException("unknown event kind");
        if (!d.ContainsKey("id") || !d.ContainsKey("ts"))
        {
            throw new FormatException("event needs id and ts");
        }
        var body = d.Where(kv => kv.Key != "schema" && kv.Key != "kind").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        LearningEvent ev = kind switch
        {
            "hold" => HoldEvent.From(body),
            "edit" => EditEvent.From(body),
            "claim" => ClaimEvent.From(body),
            "probe" => ProbeEvent.From(body),
            "decision" => DecisionEvent.From(body),
            "adapter" => AdapterEvent.From(body),
            _ => throw new FormatException($"unknown event kind '{kind}'"),
        };
        foreach (var kv in body)
        {
            if (!ev.OwnFields.Contains(kv.Key))
            {
                ev.Extra[kv.Key] = kv.Value;
            }
        }
        return ev;
    }

    protected abstract IReadOnlySet<string> OwnFields { get; }

    public static (long ts, int order, string id) SortKey(LearningEvent e) => (e.Ts, KindOrder[e.Kind], e.Id);

    public static int Compare(LearningEvent a, LearningEvent b)
    {
        var c = a.Ts.CompareTo(b.Ts);
        if (c != 0) return c;
        c = KindOrder[a.Kind].CompareTo(KindOrder[b.Kind]);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Id, b.Id);
    }

    protected static long TsOf(Dictionary<string, object?> d) => PyJson.AsLong(d["ts"]);
    protected static string IdOf(Dictionary<string, object?> d) => PyJson.AsString(d["id"]);
    protected static string? Str(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? PyJson.AsStringOrNull(v) : null;
    protected static Num? NumOf(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? PyJson.AsNumOrNull(v) : null;
    protected static object? NumOrNull(Num? n) => n.HasValue ? n.Value : null;
}

/// <summary>A trial: one hold of the dictation key.</summary>
public sealed class HoldEvent : LearningEvent
{
    public override string Kind => "hold";
    public string Text { get; init; } = "";
    public string? Proc { get; init; }
    public string? Lang { get; init; }
    public List<string> NBest { get; init; } = new();
    public Num? AvgLogprob { get; init; }
    public string? VocabRev { get; init; }
    /// <summary>Claim ids on the winning path.</summary>
    public List<string> Fired { get; init; } = new();
    /// <summary>Clip id while retained.</summary>
    public string? Audio { get; init; }
    public string ObservedState { get; init; } = "none";
    /// <summary>
    /// The unassisted decode of the same clip when the hold asked for one
    /// (one clip, two decodes; Dictation never asks). Null, and ABSENT from the line, on
    /// every other hold, so a ledger written without it is byte-for-byte what
    /// it was before.
    /// </summary>
    public string? BaselineText { get; init; }

    public HoldEvent(string id, long ts) : base(id, ts) { }

    private static readonly HashSet<string> Own = new(StringComparer.Ordinal)
        { "id", "ts", "text", "proc", "lang", "nbest", "avg_logprob", "vocab_rev", "fired", "audio", "observed", "baseline_text" };
    protected override IReadOnlySet<string> OwnFields => Own;

    public static HoldEvent From(Dictionary<string, object?> d)
    {
        var ev = new HoldEvent(IdOf(d), TsOf(d))
        {
            Text = Str(d, "text") ?? "",
            Proc = Str(d, "proc"),
            Lang = Str(d, "lang"),
            NBest = PyJson.AsStringList(d.GetValueOrDefault("nbest")).Take(8).ToList(),
            AvgLogprob = NumOf(d, "avg_logprob"),
            VocabRev = Str(d, "vocab_rev"),
            Fired = PyJson.AsStringList(d.GetValueOrDefault("fired")),
            Audio = Str(d, "audio"),
            ObservedState = Str(d, "observed") ?? "none",
            BaselineText = Str(d, "baseline_text"),
        };
        Check(ev.ObservedState, Observed, "observed");
        return ev;
    }

    protected override void Fill(Dictionary<string, object?> d)
    {
        d["id"] = Id; d["ts"] = Num.Int(Ts); d["text"] = Text; d["proc"] = Proc; d["lang"] = Lang;
        d["nbest"] = NBest.Take(8).Cast<object?>().ToList(); d["avg_logprob"] = NumOrNull(AvgLogprob);
        d["vocab_rev"] = VocabRev; d["fired"] = Fired.Cast<object?>().ToList(); d["audio"] = Audio; d["observed"] = ObservedState;
        if (BaselineText is not null) d["baseline_text"] = BaselineText;
    }
}

public sealed record EditSpan(string Orig, string Corr, long Start, long Len)
{
    public Dictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
        { ["orig"] = Orig, ["corr"] = Corr, ["start"] = Num.Int(Start), ["len"] = Num.Int(Len) };

    public static EditSpan From(object? v)
    {
        var d = PyJson.AsDict(v);
        return new EditSpan(PyJson.AsString(d["orig"]), PyJson.AsString(d["corr"]), PyJson.AsLong(d["start"]), PyJson.AsLong(d["len"]));
    }
}

/// <summary>A span diff the observer saw — never field contents.</summary>
public sealed class EditEvent : LearningEvent
{
    public override string Kind => "edit";
    public string Hold { get; init; } = "";
    public List<EditSpan> Spans { get; init; } = new();
    public string Terminal { get; init; } = "settled";
    public string? App { get; init; }
    public bool? KeyboardOrigin { get; init; }

    public EditEvent(string id, long ts) : base(id, ts) { }

    private static readonly HashSet<string> Own = new(StringComparer.Ordinal)
        { "id", "ts", "hold", "spans", "terminal", "app", "keyboard_origin" };
    protected override IReadOnlySet<string> OwnFields => Own;

    public static EditEvent From(Dictionary<string, object?> d)
    {
        var spans = d.GetValueOrDefault("spans") is null ? new List<EditSpan>() : PyJson.AsList(d["spans"]).Select(EditSpan.From).ToList();
        var ev = new EditEvent(IdOf(d), TsOf(d))
        {
            Hold = Str(d, "hold") ?? "",
            Spans = spans,
            Terminal = Str(d, "terminal") ?? "settled",
            App = Str(d, "app"),
            KeyboardOrigin = PyJson.AsBoolOrNull(d.GetValueOrDefault("keyboard_origin")),
        };
        Check(ev.Terminal, Terminals, "terminal");
        return ev;
    }

    protected override void Fill(Dictionary<string, object?> d)
    {
        d["id"] = Id; d["ts"] = Num.Int(Ts); d["hold"] = Hold;
        d["spans"] = Spans.Select(s => (object?)s.ToDict()).ToList();
        d["terminal"] = Terminal; d["app"] = App; d["keyboard_origin"] = KeyboardOrigin;
    }
}

/// <summary>Provenance of a claim (schemas.Sources).</summary>
public sealed class ClaimSources
{
    /// <summary>{"mail": 42, ...}</summary>
    public Dictionary<string, long> Counts { get; } = new(StringComparer.Ordinal);
    /// <summary>authored vs dictated (R9)</summary>
    public bool? Authored { get; set; }
    public string? ScanHash { get; set; }
    public long Count { get; set; }
    /// <summary>unix ms of the newest source</summary>
    public long? LastSeen { get; set; }
    public string? MigratedFrom { get; set; }
    public bool? ShippedDefault { get; set; }
    public Num? MigratedDelta { get; set; }

    public ClaimSources Clone()
    {
        var c = new ClaimSources
        {
            Authored = Authored, ScanHash = ScanHash, Count = Count, LastSeen = LastSeen,
            MigratedFrom = MigratedFrom, ShippedDefault = ShippedDefault, MigratedDelta = MigratedDelta,
        };
        foreach (var kv in Counts)
        {
            c.Counts[kv.Key] = kv.Value;
        }
        return c;
    }

    public Dictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["counts"] = Counts.ToDictionary(kv => kv.Key, kv => (object?)Num.Int(kv.Value), StringComparer.Ordinal),
        ["authored"] = Authored, ["scan_hash"] = ScanHash, ["count"] = Num.Int(Count),
        ["last_seen"] = LastSeen.HasValue ? Num.Int(LastSeen.Value) : null,
        ["migrated_from"] = MigratedFrom, ["shipped_default"] = ShippedDefault,
        ["migrated_delta"] = MigratedDelta.HasValue ? MigratedDelta.Value : null,
    };

    public static ClaimSources From(object? v)
    {
        var s = new ClaimSources();
        if (v is null)
        {
            return s;
        }
        var d = PyJson.AsDict(v);
        if (d.TryGetValue("counts", out var counts) && counts is not null)
        {
            foreach (var kv in PyJson.AsDict(counts))
            {
                s.Counts[kv.Key] = PyJson.AsLong(kv.Value);
            }
        }
        s.Authored = PyJson.AsBoolOrNull(d.GetValueOrDefault("authored"));
        s.ScanHash = PyJson.AsStringOrNull(d.GetValueOrDefault("scan_hash"));
        s.Count = d.TryGetValue("count", out var c) && c is not null ? PyJson.AsLong(c) : 0;
        s.LastSeen = d.TryGetValue("last_seen", out var ls) && ls is not null ? PyJson.AsLong(ls) : null;
        s.MigratedFrom = PyJson.AsStringOrNull(d.GetValueOrDefault("migrated_from"));
        s.ShippedDefault = PyJson.AsBoolOrNull(d.GetValueOrDefault("shipped_default"));
        s.MigratedDelta = PyJson.AsNumOrNull(d.GetValueOrDefault("migrated_delta"));
        return s;
    }
}

/// <summary>Creation + provenance of a claim. State and score are DERIVED (ledger).</summary>
public sealed class ClaimEvent : LearningEvent
{
    public override string Kind => "claim";
    public string Text { get; init; } = "";
    public string? WrittenAs { get; init; }
    public string? Lang { get; init; } = "en";
    public string Origin { get; init; } = "import";
    /// <summary>null → knobs.prior_by_origin</summary>
    public Num? Prior { get; init; }
    /// <summary>edit-born: the hold it came from</summary>
    public string? Hold { get; init; }
    public ClaimSources Sources { get; init; } = new();

    public ClaimEvent(string id, long ts) : base(id, ts) { }

    private static readonly HashSet<string> Own = new(StringComparer.Ordinal)
        { "id", "ts", "text", "written_as", "lang", "origin", "prior", "hold", "sources" };
    protected override IReadOnlySet<string> OwnFields => Own;

    public static ClaimEvent From(Dictionary<string, object?> d)
    {
        var ev = new ClaimEvent(IdOf(d), TsOf(d))
        {
            Text = Str(d, "text") ?? "",
            WrittenAs = Str(d, "written_as"),
            Lang = d.ContainsKey("lang") ? Str(d, "lang") : "en",
            Origin = Str(d, "origin") ?? "import",
            Prior = NumOf(d, "prior"),
            Hold = Str(d, "hold"),
            Sources = ClaimSources.From(d.GetValueOrDefault("sources")),
        };
        Check(ev.Origin, Origins, "origin");
        if (string.IsNullOrWhiteSpace(ev.Text))
        {
            throw new FormatException("claim text must be non-empty");
        }
        return ev;
    }

    protected override void Fill(Dictionary<string, object?> d)
    {
        d["id"] = Id; d["ts"] = Num.Int(Ts); d["text"] = Text; d["written_as"] = WrittenAs; d["lang"] = Lang;
        d["origin"] = Origin; d["prior"] = NumOrNull(Prior); d["hold"] = Hold; d["sources"] = Sources.ToDict();
    }
}

/// <summary>A verifier verdict. Span-level strings only.</summary>
public sealed class ProbeEvent : LearningEvent
{
    public override string Kind => "probe";
    public string Claim { get; init; } = "";
    public string? Hold { get; init; }
    public string Test { get; init; } = "agreement";
    public bool Passed { get; init; }
    public Num? Delta { get; init; }
    public Num? Margin { get; init; }
    public string? OrigSpan { get; init; }
    public string? CorrSpan { get; init; }
    public string? Distractor { get; init; }
    public string? Lang { get; init; }

    public ProbeEvent(string id, long ts) : base(id, ts) { }

    private static readonly HashSet<string> Own = new(StringComparer.Ordinal)
        { "id", "ts", "claim", "hold", "test", "passed", "delta", "margin", "orig_span", "corr_span", "distractor", "lang" };
    protected override IReadOnlySet<string> OwnFields => Own;

    public static ProbeEvent From(Dictionary<string, object?> d)
    {
        var ev = new ProbeEvent(IdOf(d), TsOf(d))
        {
            Claim = Str(d, "claim") ?? "",
            Hold = Str(d, "hold"),
            Test = Str(d, "test") ?? "agreement",
            Passed = d.GetValueOrDefault("passed") is bool b && b,
            Delta = NumOf(d, "delta"),
            Margin = NumOf(d, "margin"),
            OrigSpan = Str(d, "orig_span"),
            CorrSpan = Str(d, "corr_span"),
            Distractor = Str(d, "distractor"),
            Lang = Str(d, "lang"),
        };
        Check(ev.Test, ProbeKinds, "probe test");
        return ev;
    }

    protected override void Fill(Dictionary<string, object?> d)
    {
        d["id"] = Id; d["ts"] = Num.Int(Ts); d["claim"] = Claim; d["hold"] = Hold; d["test"] = Test; d["passed"] = Passed;
        d["delta"] = NumOrNull(Delta); d["margin"] = NumOrNull(Margin); d["orig_span"] = OrigSpan; d["corr_span"] = CorrSpan;
        d["distractor"] = Distractor; d["lang"] = Lang;
    }
}

/// <summary>promote / park / unpark / decay / tombstone / strike / truncate, citing event ids.</summary>
public sealed class DecisionEvent : LearningEvent
{
    public override string Kind => "decision";
    public string Action { get; init; } = "tombstone";
    public string? Claim { get; init; }
    public List<string> Cites { get; init; } = new();
    public string? Reason { get; init; }
    /// <summary>"user" | "ledger" | "migrate"</summary>
    public string By { get; init; } = "ledger";
    /// <summary>truncate only</summary>
    public long? SinceTs { get; init; }

    public DecisionEvent(string id, long ts) : base(id, ts) { }

    private static readonly HashSet<string> Own = new(StringComparer.Ordinal)
        { "id", "ts", "action", "claim", "cites", "reason", "by", "since_ts" };
    protected override IReadOnlySet<string> OwnFields => Own;

    public static DecisionEvent From(Dictionary<string, object?> d)
    {
        var ev = new DecisionEvent(IdOf(d), TsOf(d))
        {
            Action = Str(d, "action") ?? "tombstone",
            Claim = Str(d, "claim"),
            Cites = PyJson.AsStringList(d.GetValueOrDefault("cites")),
            Reason = Str(d, "reason"),
            By = Str(d, "by") ?? "ledger",
            SinceTs = d.TryGetValue("since_ts", out var s) && s is not null ? PyJson.AsLong(s) : null,
        };
        Check(ev.Action, Actions, "action");
        if (ev.Action == "truncate" && ev.SinceTs is null)
        {
            throw new FormatException("truncate needs since_ts");
        }
        return ev;
    }

    protected override void Fill(Dictionary<string, object?> d)
    {
        d["id"] = Id; d["ts"] = Num.Int(Ts); d["action"] = Action; d["claim"] = Claim;
        d["cites"] = Cites.Cast<object?>().ToList(); d["reason"] = Reason; d["by"] = By;
        d["since_ts"] = SinceTs.HasValue ? Num.Int(SinceTs.Value) : null;
    }
}

public sealed class AdapterEvent : LearningEvent
{
    public override string Kind => "adapter";
    public string Action { get; init; } = "snapshot";
    public Dictionary<string, object?> Payload { get; init; } = new(StringComparer.Ordinal);

    public AdapterEvent(string id, long ts) : base(id, ts) { }

    private static readonly HashSet<string> Own = new(StringComparer.Ordinal) { "id", "ts", "action", "payload" };
    protected override IReadOnlySet<string> OwnFields => Own;

    public static AdapterEvent From(Dictionary<string, object?> d)
    {
        var ev = new AdapterEvent(IdOf(d), TsOf(d))
        {
            Action = Str(d, "action") ?? "snapshot",
            Payload = d.GetValueOrDefault("payload") is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : PyJson.AsDict(PyJson.Clone(d["payload"])),
        };
        Check(ev.Action, AdapterActions, "adapter action");
        return ev;
    }

    protected override void Fill(Dictionary<string, object?> d)
    {
        d["id"] = Id; d["ts"] = Num.Int(Ts); d["action"] = Action; d["payload"] = PyJson.Clone(Payload);
    }
}
