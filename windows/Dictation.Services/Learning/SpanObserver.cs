using System.Diagnostics;
using System.Text;

namespace Dictation.Services.Learning;

/// <summary>
/// Knobs of the span-observer contract §2. Names are fixed by the spec; defaults are
/// starting points that the E4 matrix (§8) calibrates, which is why
/// <see cref="ToDict"/> prints them into every probe result file.
/// </summary>
public sealed class SpanObserverOptions
{
    /// <summary>§1: deadline of every UIA call (the probe's DefaultTimeout).</summary>
    public int TUiaMs { get; init; } = 250;
    /// <summary>§2 <c>t_autocorrect</c>: changes landing within this of <c>t0</c> are vetoed.</summary>
    public int TAutocorrectMs { get; init; } = 1_500;
    /// <summary>§2 <c>t_origin</c>: a change is keyboard-origin only if a key arrived within this before it.</summary>
    public int TOriginMs { get; init; } = 2_000;
    /// <summary>§2 <c>t_settle</c>: quiet for this long after the last change ⇒ <c>settled</c>.</summary>
    public int TSettleMs { get; init; } = 3_000;
    /// <summary>§2 <c>t_window</c>: hard end of observation.</summary>
    public int TWindowMs { get; init; } = 60_000;
    /// <summary>§2 <c>t_poll</c>: re-read cadence when no TextChanged event is available.</summary>
    public int TPollMs { get; init; } = 500;
    /// <summary>§2 <c>t_redo</c>: a new hold into the same control within this of <c>t0</c> is a negative.</summary>
    public int TRedoMs { get; init; } = 20_000;
    /// <summary>§2 <c>L_span</c>: max stored replacement span.</summary>
    public int LSpan { get; init; } = 64;
    /// <summary>§2 <c>L_ctx</c>: context read each side of the anchored range.</summary>
    public int LCtx { get; init; } = 32;
    /// <summary>§2 <c>L_read</c>: cap on the one whole-field read.</summary>
    public int LRead { get; init; } = 20_000;
    /// <summary>§2 <c>W_max</c> (<c>max_span_words</c>): widest substitution that is still a candidate.</summary>
    public int MaxSpanWords { get; init; } = 4;
    /// <summary>§7 <c>knobs_rev</c>: bumped whenever a default above changes.</summary>
    public string KnobsRev { get; init; } = "so/1";

    public Dictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
    {
        ["t_uia"] = Num.Int(TUiaMs), ["t_autocorrect"] = Num.Int(TAutocorrectMs), ["t_origin"] = Num.Int(TOriginMs),
        ["t_settle"] = Num.Int(TSettleMs), ["t_window"] = Num.Int(TWindowMs), ["t_poll"] = Num.Int(TPollMs),
        ["t_redo"] = Num.Int(TRedoMs), ["L_span"] = Num.Int(LSpan), ["L_ctx"] = Num.Int(LCtx), ["L_read"] = Num.Int(LRead),
        ["max_span_words"] = Num.Int(MaxSpanWords), ["knobs_rev"] = KnobsRev,
    };
}

/// <summary>The terminal of one observation (§6), with its reason when <c>unobservable</c>.</summary>
public sealed record SpanObservationResult(
    EditEvent Event,
    string Terminal,
    string? Reason,
    string Observed,
    string Anchor,
    IReadOnlyList<EditSpan> Candidates,
    IReadOnlyList<SpanObserverVeto> Vetoes,
    int ReadChars,
    IReadOnlyList<string> Trace)
{
    /// <summary><c>settled</c>, <c>cleared</c>, <c>unobservable:secure_unknown</c>…</summary>
    public string Label => Reason is null ? Terminal : $"{Terminal}:{Reason}";

    /// <summary>§6: a negative trial for every claim in the hold's <c>fired[]</c>.</summary>
    public bool IsNegativeTrial => Terminal == SpanObservation.TerminalCleared;

    /// <summary>
    /// The span's final text, line endings normalised, when the observation
    /// <c>settled</c> on a change the §5 time rules kept (after
    /// <c>t_autocorrect</c>, with keyboard origin) — exactly when
    /// <see cref="Candidates"/> are classified. Null for every other terminal,
    /// for an unedited span and for a last change that was vetoed.
    /// </summary>
    /// <remarks>
    /// Dictation's learning policy classifies this itself (LEARNING.md §3), so
    /// it needs the whole span rather than §5's one widened diff. It is bounded
    /// by the tracked window — the pasted span plus at most <c>L_span</c> + 8
    /// characters — and never includes the context read around it (O1).
    /// </remarks>
    public string? EditedSpan { get; init; }

    /// <summary>
    /// How many times the tracked span was seen to change before this
    /// terminal, kept or vetoed (the event's <c>changes</c>). Zero means the
    /// user left the pasted text as it was; a host that reports how an
    /// observation ended uses it to tell "no edit" from "an edit that taught
    /// nothing" without ever holding the text.
    /// </summary>
    public int Changes { get; init; }
}

/// <summary>A change the observer saw and refused (§5); spans are ≤ <c>L_span</c>.</summary>
public sealed record SpanObserverVeto(string Kind, string Orig, string Corr)
{
    public Dictionary<string, object?> ToDict() => new(StringComparer.Ordinal)
        { ["kind"] = Kind, ["orig"] = Orig, ["corr"] = Corr };
}

/// <summary>
/// One hold's observation (the span-observer contract §3–§6): the pure state machine.
/// Driven by <see cref="Start"/> once and then <see cref="Tick"/> with a
/// caller-supplied clock until it returns a result. No threads, no timers, no
/// platform — the adapter behind <see cref="ISpanObserverTarget"/> is the only
/// thing that touches UI Automation, and <see cref="SpanObserver.RunAsync"/> is
/// the loop that drives this on the pool.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant O1.</b> The only strings that leave this class inside an event
/// are the pasted span, the ≤ <c>L_span</c> characters the user put in its
/// place, a process name and a window class. The whole-field read of step B
/// and the context each side of the anchor are compared and dropped; only
/// <c>read_chars</c> (a count) records that the read happened.
/// </para>
/// <para>
/// <b>Invariant O2.</b> Every adapter failure is <c>unobservable</c>; the
/// secure probe's <c>Unknown</c> stops the observation before any read.
/// </para>
/// </remarks>
public sealed class SpanObservation
{
    public const string TerminalSettled = "settled";
    public const string TerminalNavigated = "navigated";
    public const string TerminalCleared = "cleared";
    public const string TerminalUnobservable = "unobservable";
    public const string TerminalTimeout = "timeout";

    public const string ObservedNone = "none";
    /// <summary>The edit was observed inside this app's own process (an in-process editor, no UIA).</summary>
    public const string ObservedSelf = "self";
    public const string ObservedApp = "app";

    public const string AnchorSelection = "selection";
    public const string AnchorSubstring = "substring";
    public const string AnchorInProcess = "in_process";

    // §3/§4 unobservable reasons
    public const string ReasonSelf = "self";
    public const string ReasonTerminal = "terminal";
    public const string ReasonNoFocus = "no_focus";
    public const string ReasonSecure = "secure";
    public const string ReasonSecureUnknown = "secure_unknown";
    public const string ReasonNoTextPattern = "no_textpattern";
    public const string ReasonDocTooLong = "doc_too_long";
    public const string ReasonAnchorAmbiguous = "anchor_ambiguous";
    public const string ReasonAnchorLost = "anchor_lost";
    public const string ReasonRedo = "redo";

    // §5 veto kinds
    public const string VetoAutocorrectWindow = "autocorrect_window";
    public const string VetoNoKeyboardOrigin = "no_keyboard_origin";
    public const string VetoFormattingOnly = "formatting_only";
    public const string VetoRewordingShape = "rewording_shape";
    public const string VetoTombstoned = "tombstoned";
    public const string VetoNotDistinctive = "not_distinctive";

    /// <summary>§4.2 hard deny-list: process names (case-insensitive, no <c>.exe</c>).</summary>
    public static readonly IReadOnlySet<string> DeniedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "wt", "conhost", "cmd", "powershell", "pwsh", "OpenConsole",
        "mintty", "alacritty", "wezterm-gui", "kitty", "putty", "ubuntu", "bash",
    };

    /// <summary>§4.2 hard deny-list: window classes.</summary>
    public static readonly IReadOnlySet<string> DeniedWindowClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty",
    };

    private readonly ISpanObserverTarget _target;
    private readonly SpanObserverOptions _opts;
    private readonly IVocabularyLexicon _lexicon;
    private readonly Func<string, bool> _isTombstoned;
    private readonly int _selfProcessId;
    private readonly string _pasted;          // raw, as inserted
    private readonly string _pastedNorm;      // line endings normalised
    private readonly List<string> _trace = new();
    private readonly List<EditSpan> _candidates = new();
    private readonly List<SpanObserverVeto> _vetoes = new();

    private string _anchor = AnchorSelection;
    private int _anchorStart;       // offset of the span in the adapter's text model
    private int _anchorLen;         // raw length of the span at anchor time
    private string _prefixCtx = string.Empty;
    private string _suffixCtx = string.Empty;
    private string _lastRegion;     // the region text as last seen (raw)
    private int _readChars;
    private int _changes;
    private long _firstChangeMs = -1;
    private long _lastChangeMs = -1;
    private long _lastPollMs = -1;
    private bool _lastChangeVetoed;
    private bool? _lastKeyboardOrigin;
    private IDisposable? _subscription;
    private int _textChangedPending;
    private int _focusLostPending;
    private SpanObservationResult? _result;
    private string? _lastPublishedSpan;
    private bool _continuous;

    public SpanObservation(
        ISpanObserverTarget target,
        string pasted,
        string holdId,
        long holdTs,
        long t0Ms,
        SpanObserverOptions? options = null,
        IVocabularyLexicon? lexicon = null,
        Func<string, bool>? isTombstoned = null,
        int? selfProcessId = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _pasted = pasted ?? string.Empty;
        _pastedNorm = NormalizeLineEndings(_pasted);
        _lastRegion = _pasted;
        HoldId = holdId ?? throw new ArgumentNullException(nameof(holdId));
        HoldTs = holdTs;
        T0Ms = t0Ms;
        _opts = options ?? new SpanObserverOptions();
        _lexicon = lexicon ?? VocabularyLexicon.Empty;
        _isTombstoned = isTombstoned ?? (_ => false);
        _selfProcessId = selfProcessId ?? Environment.ProcessId;
    }

    public string HoldId { get; }
    public long HoldTs { get; }
    /// <summary>§1: the instant <c>InsertAsync</c> returned.</summary>
    public long T0Ms { get; }
    public SpanObserverTargetIdentity Identity => _target.Identity;
    public bool IsOpen => _result is null && _started;
    public SpanObservationResult? Result => _result;
    /// <summary>§8: <c>A_ok|A_fail:&lt;why&gt;|B_ok|B_fail:&lt;why&gt;</c> plus per-call adapter ms.</summary>
    public IReadOnlyList<string> Trace => _trace;
    /// <summary>True when the adapter gave no subscription and the observer re-reads every <c>t_poll</c>.</summary>
    public bool Polling => _started && _subscription is null;
    /// <summary>§2 <c>t_poll</c>, exposed for the driving loop.</summary>
    public int PollIntervalMs => _opts.TPollMs;

    private bool _started;

    /// <summary>§4 gates, §3.1 secure probe, §3.2 anchoring. Returns the terminal when the observation ends here, else <c>null</c>.</summary>
    public SpanObservationResult? Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("an observation starts once");
        }
        _started = true;

        var id = _target.Identity;
        if (!id.InProcess)
        {
            if (!id.IsResolved)
            {
                return Finish(TerminalUnobservable, ReasonNoFocus, T0Ms);
            }
            if (id.ProcessId == _selfProcessId)
            {
                return Finish(TerminalUnobservable, ReasonSelf, T0Ms);
            }
            if (IsDeniedTarget(id.ProcessName, id.WindowClass))
            {
                return Finish(TerminalUnobservable, ReasonTerminal, T0Ms);
            }

            var secure = _target.ProbeSecureField();
            Note("secure", secure);
            if (!secure.Ok)
            {
                return Finish(TerminalUnobservable, secure.Failure!, T0Ms);
            }
            switch (secure.Value)
            {
                case SecureFieldState.Secure:
                    return Finish(TerminalUnobservable, ReasonSecure, T0Ms);
                case SecureFieldState.NotSecure:
                    break;
                default:
                    return Finish(TerminalUnobservable, ReasonSecureUnknown, T0Ms);
            }
        }

        if (_pasted.Length == 0)
        {
            return Finish(TerminalUnobservable, ReasonAnchorAmbiguous, T0Ms);
        }

        string? failure = null;
        var anchored = AnchorBySelection() ?? AnchorBySubstring(out failure);
        if (!anchored)
        {
            return Finish(TerminalUnobservable, failure ?? ReasonAnchorAmbiguous, T0Ms);
        }
        if (id.InProcess)
        {
            _anchor = AnchorInProcess;
        }

        try
        {
            _subscription = _target.Subscribe(OnSignal);
        }
        catch (Exception)
        {
            _subscription = null;
        }
        _lastPollMs = T0Ms;
        return null;
    }

    /// <summary>
    /// One step of the loop at <paramref name="nowMs"/>: drains signals,
    /// re-reads the range when a change was signalled (or every <c>t_poll</c>
    /// when polling), classifies, and checks the terminal clocks. Returns the
    /// result once, then keeps returning it.
    /// </summary>
    public SpanObservationResult? Tick(long nowMs, Action<string>? onRevision = null)
    {
        if (_result is not null)
        {
            return _result;
        }
        if (!_started)
        {
            throw new InvalidOperationException("Start() first");
        }

        if (Interlocked.Exchange(ref _focusLostPending, 0) != 0)
        {
            return Finish(TerminalNavigated, null, nowMs);
        }

        _continuous = onRevision is not null;
        var changed = Interlocked.Exchange(ref _textChangedPending, 0) != 0;
        var poll = _subscription is null && nowMs - _lastPollMs >= _opts.TPollMs;
        if (changed || poll)
        {
            _lastPollMs = nowMs;
            var r = Observe(nowMs);
            if (r is not null)
            {
                return r;
            }
        }

        if (nowMs - T0Ms >= _opts.TWindowMs)
        {
            if (_changes == 0 || nowMs - _lastChangeMs >= _opts.TSettleMs)
            {
                return Settle(nowMs);
            }
            return Finish(TerminalTimeout, null, nowMs);
        }
        if (_changes > 0 && nowMs - _lastChangeMs >= _opts.TSettleMs)
        {
            if (onRevision is null) return Settle(nowMs);
            var span = NormalizeLineEndings(_lastRegion);
            if (!_lastChangeVetoed && !string.Equals(span, _lastPublishedSpan ?? _pastedNorm, StringComparison.Ordinal))
            {
                // A password-state change need not emit a text notification.
                // Probe again at publication, even if this tick did no read.
                if (CheckContinuousSecurity(nowMs) is { } refusal) return refusal;
                _lastPublishedSpan = span;
                onRevision(span);
            }
        }
        return null;
    }

    /// <summary>Ends an open observation from outside — §6 <c>t_redo</c> (terminal <c>cleared</c>, reason <c>redo</c>) or shutdown.</summary>
    public SpanObservationResult Cancel(string terminal, string? reason, long nowMs) =>
        _result ?? Finish(terminal, reason, nowMs);

    /// <summary>§4.2 and §4.3: terminals, consoles, and nothing else is denied by name.</summary>
    public static bool IsDeniedTarget(string processName, string windowClass)
    {
        var name = processName ?? string.Empty;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        return DeniedProcesses.Contains(name) || DeniedWindowClasses.Contains(windowClass ?? string.Empty);
    }

    // ── anchoring ────────────────────────────────────────────────────────

    /// <summary>Step A. <c>true</c> anchored, <c>null</c> fall through to step B.</summary>
    private bool? AnchorBySelection()
    {
        var sel = _target.GetSelection();
        Note("selection", sel);
        if (!sel.Ok)
        {
            _trace.Add($"A_fail:{sel.Failure}");
            return null;
        }
        var caret = sel.Value.Start;
        foreach (var rawLen in RawLengthCandidates())
        {
            if (caret - rawLen < 0)
            {
                continue;
            }
            var read = _target.ReadRange(caret - rawLen, rawLen);
            Note("readA", read);
            if (!read.Ok)
            {
                _trace.Add($"A_fail:{read.Failure}");
                return null;
            }
            if (NormalizeLineEndings(read.Value ?? string.Empty) == _pastedNorm)
            {
                _anchorStart = caret - rawLen;
                _anchorLen = rawLen;
                _anchor = AnchorSelection;
                if (!CaptureContext(out var why))
                {
                    _trace.Add($"A_fail:{why}");
                    return null;
                }
                _trace.Add("A_ok");
                return true;
            }
        }
        _trace.Add("A_fail:mismatch");
        return null;
    }

    /// <summary>Step B: one whole-field read, unique substring, then the field text is dropped.</summary>
    private bool AnchorBySubstring(out string? failure)
    {
        failure = null;
        var read = _target.ReadText(_opts.LRead + 1);
        Note("readB", read);
        if (!read.Ok)
        {
            failure = read.Failure == SpanObserverFailures.NoTextPattern ? ReasonNoTextPattern : read.Failure;
            _trace.Add($"B_fail:{failure}");
            return false;
        }
        var doc = read.Value ?? string.Empty;
        _readChars = doc.Length;
        if (doc.Length > _opts.LRead)
        {
            failure = ReasonDocTooLong;
            _trace.Add($"B_fail:{failure}");
            return false;
        }

        foreach (var needle in SubstringNeedles())
        {
            var first = doc.IndexOf(needle, StringComparison.Ordinal);
            if (first < 0)
            {
                continue;
            }
            var second = doc.IndexOf(needle, first + 1, StringComparison.Ordinal);
            if (second >= 0)
            {
                failure = ReasonAnchorAmbiguous;
                _trace.Add($"B_fail:{failure}");
                return false;
            }
            _anchorStart = first;
            _anchorLen = needle.Length;
            _anchor = AnchorSubstring;
            var pre = Math.Max(0, first - _opts.LCtx);
            _prefixCtx = doc[pre..first];
            var end = first + needle.Length;
            _suffixCtx = doc[end..Math.Min(doc.Length, end + _opts.LCtx)];
            _lastRegion = needle;
            _trace.Add("B_ok");
            return true;
        }
        failure = ReasonAnchorAmbiguous;
        _trace.Add($"B_fail:{failure}");
        return false;
    }

    /// <summary>§3.3: the context each side of the anchored span, read once and kept only in memory.</summary>
    private bool CaptureContext(out string? why)
    {
        why = null;
        var pre = Math.Max(0, _anchorStart - _opts.LCtx);
        var preLen = _anchorStart - pre;
        var read = _target.ReadRange(pre, preLen + _anchorLen + _opts.LCtx);
        Note("readCtx", read);
        if (!read.Ok)
        {
            why = read.Failure;
            return false;
        }
        var cur = read.Value ?? string.Empty;
        if (cur.Length < preLen + _anchorLen)
        {
            why = ReasonAnchorLost;
            return false;
        }
        _prefixCtx = cur[..preLen];
        _lastRegion = cur.Substring(preLen, _anchorLen);
        _suffixCtx = cur[(preLen + _anchorLen)..];
        return true;
    }

    private IEnumerable<int> RawLengthCandidates()
    {
        yield return _pasted.Length;
        var lf = _pastedNorm.Count(c => c == '\n');
        if (lf > 0)
        {
            yield return _pastedNorm.Length;
            yield return _pastedNorm.Length + lf;
        }
    }

    private IEnumerable<string> SubstringNeedles()
    {
        yield return _pasted;
        if (_pastedNorm.Contains('\n'))
        {
            yield return _pastedNorm;
            yield return _pastedNorm.Replace("\n", "\r\n");
        }
    }

    // ── tracking and classification ──────────────────────────────────────

    private void OnSignal(SpanObserverSignal signal)
    {
        if (signal == SpanObserverSignal.FocusLost)
        {
            Interlocked.Exchange(ref _focusLostPending, 1);
        }
        else
        {
            Interlocked.Exchange(ref _textChangedPending, 1);
        }
    }

    /// <summary>Re-read the tracked window and re-locate the span by its unchanged context (§3.3), then classify (§5).</summary>
    private SpanObservationResult? Observe(long nowMs)
    {
        if (CheckContinuousSecurity(nowMs) is { } refusal) return refusal;
        return ReadObservedSpan(nowMs);
    }

    private SpanObservationResult? CheckContinuousSecurity(long nowMs)
    {
        if (_continuous && !_target.Identity.InProcess)
        {
            var secure = _target.ProbeSecureField();
            if (!secure.Ok) return Finish(TerminalUnobservable, secure.Failure!, nowMs);
            if (secure.Value != SecureFieldState.NotSecure)
                return Finish(TerminalUnobservable, secure.Value == SecureFieldState.Secure ? ReasonSecure : ReasonSecureUnknown, nowMs);
        }
        return null;
    }

    private SpanObservationResult? ReadObservedSpan(long nowMs)
    {
        var start = Math.Max(0, _anchorStart - _prefixCtx.Length);
        var length = _prefixCtx.Length + _anchorLen + _opts.LSpan + _suffixCtx.Length + 8;
        var read = _target.ReadRange(start, length);
        Note("read", read);
        if (!read.Ok)
        {
            return Finish(TerminalUnobservable, read.Failure!, nowMs);
        }
        var cur = read.Value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cur))
        {
            return Change(nowMs, string.Empty);
        }

        int regionStart;
        if (_prefixCtx.Length == 0)
        {
            regionStart = 0;
        }
        else
        {
            regionStart = UniqueIndexOf(cur, _prefixCtx);
            if (regionStart < 0)
            {
                return Finish(TerminalUnobservable, ReasonAnchorLost, nowMs);
            }
            regionStart += _prefixCtx.Length;
        }

        int regionEnd;
        if (_suffixCtx.Length == 0)
        {
            regionEnd = cur.Length;
        }
        else
        {
            regionEnd = cur.LastIndexOf(_suffixCtx, StringComparison.Ordinal);
            if (regionEnd < regionStart)
            {
                return Finish(TerminalUnobservable, ReasonAnchorLost, nowMs);
            }
        }

        return Change(nowMs, cur[regionStart..regionEnd]);
    }

    private SpanObservationResult? Change(long nowMs, string region)
    {
        if (string.Equals(region, _lastRegion, StringComparison.Ordinal))
        {
            return null;
        }
        _lastRegion = region;
        _changes++;
        if (_firstChangeMs < 0)
        {
            _firstChangeMs = nowMs;
        }
        _lastChangeMs = nowMs;

        // §5 rule 4 — cleared
        if (string.IsNullOrWhiteSpace(region))
        {
            return Finish(TerminalCleared, null, nowMs);
        }

        var diff = SpanDiff.Of(_pastedNorm, NormalizeLineEndings(region));

        // §5 rule 1 — inside the autocorrect window
        if (nowMs - T0Ms < _opts.TAutocorrectMs)
        {
            Veto(VetoAutocorrectWindow, diff);
            _lastChangeVetoed = true;
            return null;
        }

        // §5 rule 2 — keyboard origin
        var keyboard = IsKeyboardOrigin(nowMs);
        _lastKeyboardOrigin = keyboard;
        if (!keyboard)
        {
            Veto(VetoNoKeyboardOrigin, diff);
            _lastChangeVetoed = true;
            return null;
        }
        _lastChangeVetoed = false;
        return null;
    }

    private bool IsKeyboardOrigin(long nowMs)
    {
        var last = _target.LastKeyboardInput;
        if (last is null)
        {
            return false;
        }
        var pid = _target.Identity.ProcessId;
        return last.Value.ProcessId == pid && nowMs - last.Value.UtcMs <= _opts.TOriginMs && last.Value.UtcMs <= nowMs;
    }

    /// <summary>§6 <c>settled</c>: the final diff decides the candidates (rules 5–7); time-based vetoes were recorded as they happened.</summary>
    private SpanObservationResult Settle(long nowMs)
    {
        if (_changes > 0 && !_lastChangeVetoed)
        {
            var diff = SpanDiff.Of(_pastedNorm, NormalizeLineEndings(_lastRegion));
            Classify(diff);
        }
        return Finish(TerminalSettled, null, nowMs);
    }

    /// <summary>§5 rules 5–7 on one diff.</summary>
    private void Classify(SpanDiff diff)
    {
        if (diff.IsNoChange)
        {
            return;
        }
        if (diff.IsFormattingOnly)
        {
            Veto(VetoFormattingOnly, diff);
            return;
        }
        if (diff.IsPureInsertOrDelete
            || diff.Corr.Length > _opts.LSpan
            || diff.OrigWords > _opts.MaxSpanWords
            || diff.CorrWords > _opts.MaxSpanWords)
        {
            Veto(VetoRewordingShape, diff);
            return;
        }
        if (_isTombstoned(diff.Corr))
        {
            Veto(VetoTombstoned, diff);
            return;
        }
        if (!SpanDiff.IsDistinctive(diff.Corr, _lexicon))
        {
            Veto(VetoNotDistinctive, diff);
            return;
        }
        _candidates.Add(new EditSpan(diff.Orig, diff.Corr, diff.Start, diff.Orig.Length));
    }

    private void Veto(string kind, SpanDiff diff) =>
        _vetoes.Add(new SpanObserverVeto(kind, Clip(diff.Orig), Clip(diff.Corr)));

    private string Clip(string s) => s.Length <= _opts.LSpan ? s : s[.._opts.LSpan];

    // ── terminal ─────────────────────────────────────────────────────────

    private SpanObservationResult Finish(string terminal, string? reason, long nowMs)
    {
        if (_result is not null)
        {
            return _result;
        }
        try
        {
            _subscription?.Dispose();
        }
        catch (Exception)
        {
            // unsubscribing a dead provider is not a reason to lose the terminal
        }
        _subscription = null;

        var id = _target.Identity;
        var observed = terminal switch
        {
            TerminalSettled or TerminalCleared => id.InProcess ? ObservedSelf : ObservedApp,
            _ => ObservedNone,
        };

        var ev = new EditEvent("e_" + HoldId, nowMs)
        {
            Hold = HoldId,
            Spans = _candidates.ToList(),
            Terminal = terminal,
            App = id.IsResolved ? id.ProcessName : null,
            KeyboardOrigin = _lastKeyboardOrigin,
        };
        // §7 members beyond the schema's own fields travel in Extra and are re-emitted verbatim.
        ev.Extra["hold_ts"] = Num.Int(HoldTs);
        ev.Extra["control_class"] = id.WindowClass;
        ev.Extra["anchor"] = _anchor;
        ev.Extra["observed"] = observed;
        if (reason is not null)
        {
            ev.Extra["reason"] = reason;
        }
        if (terminal == TerminalSettled && _changes == 0)
        {
            ev.Extra["unedited"] = true;
        }
        ev.Extra["t_first_change_ms"] = _firstChangeMs < 0 ? null : Num.Int(_firstChangeMs - T0Ms);
        ev.Extra["t_settle_ms"] = Num.Int(nowMs - T0Ms);
        ev.Extra["changes"] = Num.Int(_changes);
        ev.Extra["candidates"] = _candidates.Select(c => (object?)c.ToDict()).ToList();
        ev.Extra["vetoes"] = _vetoes.Select(v => (object?)v.ToDict()).ToList();
        ev.Extra["read_chars"] = Num.Int(_readChars);
        ev.Extra["knobs_rev"] = _opts.KnobsRev;

        _result = new SpanObservationResult(ev, terminal, reason, observed, _anchor, _candidates.ToList(), _vetoes.ToList(), _readChars, _trace.ToList())
        {
            // The condition Settle classifies under, so the host learns from
            // exactly the edits §5 lets through.
            EditedSpan = !_continuous && terminal == TerminalSettled && _changes > 0 && !_lastChangeVetoed
                ? NormalizeLineEndings(_lastRegion)
                : null,
            Changes = _changes,
        };
        return _result;
    }

    private void Note<T>(string call, SpanObserverResult<T> r) =>
        _trace.Add($"{call}:{(r.Ok ? "ok" : r.Failure)}:{r.ElapsedMs:0}ms");

    private static int UniqueIndexOf(string hay, string needle)
    {
        var first = hay.IndexOf(needle, StringComparison.Ordinal);
        if (first < 0)
        {
            return -1;
        }
        return hay.IndexOf(needle, first + 1, StringComparison.Ordinal) >= 0 ? -1 : first;
    }

    public static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");
}

/// <summary>
/// The character diff of §5: longest common prefix/suffix widened to word
/// boundaries, with the shape questions the rules ask.
/// </summary>
public sealed record SpanDiff(string Orig, string Corr, int Start)
{
    public bool IsNoChange => Orig.Length == 0 && Corr.Length == 0;

    /// <summary>
    /// §5 rule 6: no substituted word. At word level, not character level —
    /// <c>Javis→Jarvis</c> inserts one character but substitutes a word, while
    /// <c>nine→nine please</c> keeps every original word in place.
    /// </summary>
    public bool IsPureInsertOrDelete =>
        Orig.Length == 0 || Corr.Length == 0 || IsWordRun(Tokens(Orig), Tokens(Corr)) || IsWordRun(Tokens(Corr), Tokens(Orig));
    public int OrigWords => Words(Orig);
    public int CorrWords => Words(Corr);

    /// <summary>Casing, whitespace or punctuation only — the formatter's business, not a word the recogniser missed.</summary>
    public bool IsFormattingOnly => !IsNoChange && Letters(Orig) == Letters(Corr) && Letters(Orig).Length > 0;

    public static SpanDiff Of(string original, string corrected)
    {
        original ??= string.Empty;
        corrected ??= string.Empty;
        var max = Math.Min(original.Length, corrected.Length);
        var prefix = 0;
        while (prefix < max && original[prefix] == corrected[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < max - prefix
               && original[original.Length - 1 - suffix] == corrected[corrected.Length - 1 - suffix])
        {
            suffix++;
        }
        if (prefix == original.Length && prefix == corrected.Length)
        {
            return new SpanDiff(string.Empty, string.Empty, prefix);
        }

        // widen to word boundaries within the shared prefix / suffix
        while (prefix > 0 && !char.IsWhiteSpace(original[prefix - 1]))
        {
            prefix--;
        }
        while (suffix > 0 && !char.IsWhiteSpace(original[original.Length - suffix]))
        {
            suffix--;
        }
        var origSpan = original.Substring(prefix, original.Length - prefix - suffix).Trim();
        var corrSpan = corrected.Substring(prefix, corrected.Length - prefix - suffix).Trim();
        return new SpanDiff(origSpan, corrSpan, prefix);
    }

    /// <summary>The distinctiveness gate (<see cref="VocabularyDistinctiveness"/>) over the tokens of the replacement: any distinctive token qualifies.</summary>
    public static bool IsDistinctive(string corr, IVocabularyLexicon lexicon)
    {
        foreach (var raw in corr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']');
            if (token.Length == 0)
            {
                continue;
            }
            if (VocabularyDistinctiveness.IsDistinctive(KindOf(token), token, lexicon))
            {
                return true;
            }
        }
        return false;
    }

    private static VocabularyTermKind KindOf(string token)
    {
        var dot = token.IndexOf('.');
        if (dot > 0 && dot < token.Length - 1 && token.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-'))
        {
            return VocabularyTermKind.Domain;
        }
        var upper = 0; var lower = 0; var internalUpper = false;
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (char.IsUpper(c)) { upper++; if (i > 0) { internalUpper = true; } }
            else if (char.IsLower(c)) { lower++; }
        }
        if (internalUpper && lower > 0) { return VocabularyTermKind.Camel; }
        if (upper >= 2 && lower == 0) { return VocabularyTermKind.Acronym; }
        return VocabularyTermKind.Proper;
    }

    private static string[] Tokens(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int Words(string s) => Tokens(s).Length;

    /// <summary>Is <paramref name="needle"/> a contiguous run of <paramref name="hay"/>'s words?</summary>
    private static bool IsWordRun(string[] needle, string[] hay)
    {
        if (needle.Length == 0 || needle.Length > hay.Length)
        {
            return false;
        }
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var all = true;
            for (var j = 0; j < needle.Length && all; j++)
            {
                all = string.Equals(needle[j], hay[i + j], StringComparison.Ordinal);
            }
            if (all)
            {
                return true;
            }
        }
        return false;
    }

    private static string Letters(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Per-process coordinator: starts one <see cref="SpanObservation"/> per hold,
/// applies the §6 <c>t_redo</c> rule against the previous hold, and drives the
/// open observation on the thread pool with a caller-supplied clock.
/// </summary>
public sealed class SpanObserver
{
    private readonly SpanObserverOptions _opts;
    private readonly IVocabularyLexicon _lexicon;
    private readonly Func<string, bool> _isTombstoned;
    private readonly int _selfProcessId;
    private readonly object _gate = new();
    private SpanObservation? _previous;

    public SpanObserver(
        SpanObserverOptions? options = null,
        IVocabularyLexicon? lexicon = null,
        Func<string, bool>? isTombstoned = null,
        int? selfProcessId = null)
    {
        _opts = options ?? new SpanObserverOptions();
        _lexicon = lexicon ?? VocabularyLexicon.Empty;
        _isTombstoned = isTombstoned ?? (_ => false);
        _selfProcessId = selfProcessId ?? Environment.ProcessId;
    }

    public SpanObserverOptions Options => _opts;

    /// <summary>
    /// Begins the observation of one hold. When the previous hold went into the
    /// same control within <c>t_redo</c> of its <c>t0</c> and is still open,
    /// it is ended as <c>cleared</c>/<c>redo</c> and that result is returned in
    /// <paramref name="redoOfPrevious"/> (a negative trial for the previous
    /// hold, §6). A previous hold that already ended <c>cleared</c> carries its
    /// negative already and is not counted twice.
    /// </summary>
    public SpanObservation Begin(
        ISpanObserverTarget target,
        string pasted,
        string holdId,
        long holdTs,
        long t0Ms,
        out SpanObservationResult? redoOfPrevious)
    {
        redoOfPrevious = null;
        var next = new SpanObservation(target, pasted, holdId, holdTs, t0Ms, _opts, _lexicon, _isTombstoned, _selfProcessId);
        lock (_gate)
        {
            var prev = _previous;
            if (prev is not null
                && prev.IsOpen
                && t0Ms - prev.T0Ms <= _opts.TRedoMs
                && prev.Identity.SameControlAs(target.Identity))
            {
                redoOfPrevious = prev.Cancel(SpanObservation.TerminalCleared, SpanObservation.ReasonRedo, t0Ms);
            }
            else if (prev is not null && prev.IsOpen)
            {
                // one observer per process: a new hold elsewhere ends the old one as navigated
                prev.Cancel(SpanObservation.TerminalNavigated, null, t0Ms);
            }
            _previous = next;
        }
        return next;
    }

    /// <summary>
    /// Drives one observation to its terminal: <see cref="SpanObservation.Start"/>,
    /// then <see cref="SpanObservation.Tick"/> every <c>t_poll</c> (or sooner on a
    /// signal — the tick is cheap when nothing was signalled). <paramref name="clock"/>
    /// returns unix ms; <paramref name="delay"/> defaults to <see cref="Task.Delay(int, CancellationToken)"/>.
    /// </summary>
    public static async Task<SpanObservationResult> RunAsync(
        SpanObservation observation,
        Func<long> clock,
        CancellationToken ct,
        Func<int, CancellationToken, Task>? delay = null,
        Action<string>? onRevision = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(clock);
        delay ??= Task.Delay;
        var result = observation.Start();
        while (result is null)
        {
            if (ct.IsCancellationRequested)
            {
                return observation.Cancel(SpanObservation.TerminalUnobservable, "cancelled", clock());
            }
            try
            {
                await delay(observation.Polling ? observation.PollIntervalMs : Math.Min(observation.PollIntervalMs, 100), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return observation.Cancel(SpanObservation.TerminalUnobservable, "cancelled", clock());
            }
            result = observation.Tick(clock(), onRevision);
        }
        return result;
    }

    /// <summary>A bounded call with the shape of <c>UiaSecureFieldProbe.Probe</c>: run on the pool, wait <paramref name="deadline"/>, abandon a late answer.</summary>
    public static SpanObserverResult<T> Bounded<T>(Func<T> call, TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(call);
        var sw = Stopwatch.StartNew();
        var task = Task.Run(call);
        try
        {
            if (!task.Wait(deadline))
            {
                // The task keeps running and its result is discarded; there is
                // no way to cancel a UIA call in flight, and abandoning the
                // WAIT is the whole point.
                return SpanObserverResult<T>.Failed(SpanObserverFailures.UiaTimeout, sw.Elapsed.TotalMilliseconds);
            }
            return SpanObserverResult<T>.Success(task.Result, sw.Elapsed.TotalMilliseconds);
        }
        catch (AggregateException ex) when (ex.InnerException is SpanObserverCallException sce)
        {
            return SpanObserverResult<T>.Failed(sce.Reason, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            return SpanObserverResult<T>.Failed(SpanObserverFailures.Exception, sw.Elapsed.TotalMilliseconds);
        }
    }
}

/// <summary>Thrown inside a <see cref="SpanObserver.Bounded{T}"/> call to name a specific failure reason.</summary>
public sealed class SpanObserverCallException : Exception
{
    public string Reason { get; }

    public SpanObserverCallException(string reason) : base(reason)
    {
        Reason = reason;
    }
}
