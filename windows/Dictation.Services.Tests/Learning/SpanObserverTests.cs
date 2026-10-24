using System.Diagnostics;
using Dictation.Services.Learning;

namespace Dictation.Services.Tests.Learning;

/// <summary>
/// span-observer.md §10, against a fake target and a fake clock. Every fixture
/// string here is synthetic; the "document" a test reads is filler around a
/// sentinel, never a user's text.
/// </summary>
public sealed class SpanObserverTests
{
    private const long HoldTs = 1_755_600_000_000;
    private const long T0 = HoldTs + 1_900;          // InsertAsync returned (after the 500 ms settle)
    private const string Pasted = "call Javis at nine";
    private const string Sentinel = "SENTINEL_7f3a9c_FILLER";
    private const int Pid = 4242;
    private const int SelfPid = 99;

    private static readonly SpanObserverOptions Opts = new();

    // ── fake target ─────────────────────────────────────────────────────

    private sealed class FakeTarget : ISpanObserverTarget
    {
        public string Text = string.Empty;
        public int Caret;
        public SecureFieldState Secure = SecureFieldState.NotSecure;
        public string? SecureFailure;
        public bool SelectionAvailable = true;
        public bool TextPatternAvailable = true;
        public bool SubscriptionAvailable = true;
        public string? ReadFailure;
        public int Reads;
        public int WholeFieldReads;
        public Action<SpanObserverSignal>? Handler;
        public bool Unsubscribed;

        public SpanObserverTargetIdentity Identity { get; set; } = new("notepad", Pid, "Notepad", 0x1234, false);

        /// <summary>When set, the production shape: the adapter answers from the hook's signal.</summary>
        public SpanObserverKeyboardSignal? Signal;
        private SpanObserverKeyboardInput? _lastKeyboardInput;
        public SpanObserverKeyboardInput? LastKeyboardInput
        {
            get => Signal is not null ? Signal.Last : _lastKeyboardInput;
            set => _lastKeyboardInput = value;
        }

        public SpanObserverResult<SecureFieldState> ProbeSecureField() =>
            SecureFailure is null ? SpanObserverResult<SecureFieldState>.Success(Secure) : SpanObserverResult<SecureFieldState>.Failed(SecureFailure);

        public SpanObserverResult<SpanObserverSelection> GetSelection() =>
            SelectionAvailable
                ? SpanObserverResult<SpanObserverSelection>.Success(new SpanObserverSelection(Caret, 0))
                : SpanObserverResult<SpanObserverSelection>.Failed(SpanObserverFailures.NoSelection);

        public SpanObserverResult<string> ReadRange(int start, int length)
        {
            Reads++;
            if (ReadFailure is not null)
            {
                return SpanObserverResult<string>.Failed(ReadFailure);
            }
            var s = Math.Clamp(start, 0, Text.Length);
            var e = Math.Clamp(start + length, s, Text.Length);
            return SpanObserverResult<string>.Success(Text[s..e]);
        }

        public SpanObserverResult<string> ReadText(int maxChars)
        {
            Reads++;
            WholeFieldReads++;
            if (!TextPatternAvailable)
            {
                return SpanObserverResult<string>.Failed(SpanObserverFailures.NoTextPattern);
            }
            return SpanObserverResult<string>.Success(Text.Length <= maxChars ? Text : Text[..maxChars]);
        }

        public IDisposable? Subscribe(Action<SpanObserverSignal> handler)
        {
            if (!SubscriptionAvailable)
            {
                return null;
            }
            Handler = handler;
            return new Unsub(this);
        }

        private sealed class Unsub(FakeTarget t) : IDisposable
        {
            public void Dispose() { t.Unsubscribed = true; t.Handler = null; }
        }

        /// <summary>The user (or something else) changes the field.</summary>
        public void Edit(string newText)
        {
            Text = newText;
            Handler?.Invoke(SpanObserverSignal.TextChanged);
        }

        public void KeyPressed(long utcMs, int pid = Pid) => LastKeyboardInput = new SpanObserverKeyboardInput(utcMs, pid);
    }

    private static FakeTarget DocumentWith(string pasted = Pasted, bool selection = true)
    {
        var before = Sentinel + " lorem ipsum dolor sit amet\n";
        var after = "\nconsectetur " + Sentinel;
        return new FakeTarget
        {
            Text = before + pasted + after,
            Caret = before.Length + pasted.Length,
            SelectionAvailable = selection,
        };
    }

    private static SpanObservation Observation(FakeTarget t, SpanObserverOptions? opts = null, IVocabularyLexicon? lexicon = null, Func<string, bool>? tombstoned = null) =>
        new(t, Pasted, "h_1", HoldTs, T0, opts ?? Opts, lexicon, tombstoned, SelfPid);

    /// <summary>Tick until the observation ends or the fake clock passes <paramref name="untilMs"/>.</summary>
    private static SpanObservationResult? Drive(SpanObservation o, ref long now, long untilMs, int stepMs = 100)
    {
        while (now <= untilMs)
        {
            var r = o.Tick(now);
            if (r is not null)
            {
                return r;
            }
            now += stepMs;
        }
        return null;
    }

    private static string ReplaceWord(string text, string from, string to) => text.Replace(from, to, StringComparison.Ordinal);

    [Fact]
    public void Continuous_revisions_include_reverts_without_duplicate_votes_or_a_longer_deadline()
    {
        var target = DocumentWith();
        var observation = Observation(target);
        var revisions = new List<string>();
        Assert.Null(observation.Start());
        var now = T0 + 4_000;
        foreach (var spelling in new[] { "Jarvis", "Jervis", "Javis" })
        {
            target.KeyPressed(now - 10);
            target.Edit(DocumentWith().Text.Replace("Javis", spelling, StringComparison.Ordinal));
            Assert.Null(observation.Tick(now, revisions.Add));
            now += Opts.TSettleMs;
            Assert.Null(observation.Tick(now, revisions.Add));
            Assert.Null(observation.Tick(now + 1, revisions.Add));
            now += 1_000;
        }
        Assert.Equal(new[] { "call Jarvis at nine", "call Jervis at nine", Pasted }, revisions);
        var result = observation.Tick(T0 + Opts.TWindowMs, revisions.Add)!;
        Assert.Equal("settled", result.Label);
        Assert.Null(result.EditedSpan);
        Assert.True(target.Unsubscribed);
        Assert.Equal(3, revisions.Count);
    }

    [Theory]
    [InlineData(SecureFieldState.Secure, "secure")]
    [InlineData(SecureFieldState.Unknown, "secure_unknown")]
    public void A_security_change_without_a_text_event_prevents_a_settled_revision(SecureFieldState secure, string reason)
    {
        var target = DocumentWith();
        var observation = Observation(target);
        var revisions = new List<string>();
        Assert.Null(observation.Start());
        var now = T0 + 4_000;
        target.KeyPressed(now - 10);
        target.Edit(ReplaceWord(target.Text, "Javis", "Jarvis"));
        Assert.Null(observation.Tick(now, revisions.Add));
        var reads = target.Reads;
        target.Secure = secure;
        var result = observation.Tick(now + Opts.TSettleMs, revisions.Add)!;
        Assert.Equal("unobservable:" + reason, result.Label);
        Assert.Equal(reads, target.Reads);
        Assert.Empty(revisions);
        Assert.True(target.Unsubscribed);
    }

    // ── §10.1 ────────────────────────────────────────────────────────────

    [Fact]
    public void SpanObserver_never_stores_field_text()
    {
        // Step B is the path that reads the whole field, so the selection is withheld to force it.
        var t = DocumentWith(selection: false);
        var o = Observation(t);
        Assert.Null(o.Start());
        Assert.Contains("B_ok", o.Trace);
        Assert.Equal(1, t.WholeFieldReads);

        var fieldChars = t.Text.Length;
        var now = T0 + 4_000;
        t.KeyPressed(now - 50);
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;

        Assert.Equal("settled", result.Label);
        var span = Assert.Single(result.Candidates);
        Assert.Equal("Javis", span.Orig);
        Assert.Equal("Jarvis", span.Corr);
        Assert.Equal(5, span.Start);

        var line = result.Event.Dumps();
        Assert.DoesNotContain(Sentinel, line);
        Assert.DoesNotContain("lorem", line);
        Assert.DoesNotContain("consectetur", line);
        Assert.DoesNotContain(Sentinel, string.Join("|", result.Trace));
        Assert.Contains("\"read_chars\":" + fieldChars, line);
        Assert.Contains("\"anchor\":\"substring\"", line);
        Assert.Contains("\"observed\":\"app\"", line);
        Assert.Equal("app", result.Observed);
        Assert.True(t.Unsubscribed);

        // the line round-trips through the schema and keeps the §7 members
        var back = (EditEvent)LearningEvent.Loads(line);
        Assert.Equal("settled", back.Terminal);
        Assert.Equal("Jarvis", back.Spans[0].Corr);
        Assert.Equal(line, back.Dumps());
    }

    [Fact]
    public void Step_A_anchors_on_the_selection_without_a_whole_field_read()
    {
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());
        Assert.Contains("A_ok", o.Trace);
        Assert.Equal(0, t.WholeFieldReads);
        Assert.Equal(0, o.Result?.ReadChars ?? 0);
    }

    // ── §10.2 ────────────────────────────────────────────────────────────

    [Fact]
    public void SpanObserver_fails_closed_on_unknown_secure_state()
    {
        var t = DocumentWith();
        t.Secure = SecureFieldState.Unknown;
        var result = Observation(t).Start()!;

        Assert.Equal("unobservable:secure_unknown", result.Label);
        Assert.Equal(0, t.Reads);
        Assert.Equal("none", result.Observed);
        Assert.Empty(result.Candidates);
        Assert.Equal("unobservable", result.Event.Terminal);
    }

    [Fact]
    public void A_secure_field_and_a_probe_timeout_are_both_unobservable_with_zero_reads()
    {
        var t = DocumentWith();
        t.Secure = SecureFieldState.Secure;
        Assert.Equal("unobservable:secure", Observation(t).Start()!.Label);
        Assert.Equal(0, t.Reads);

        var t2 = DocumentWith();
        t2.SecureFailure = SpanObserverFailures.UiaTimeout;
        Assert.Equal("unobservable:uia_timeout", Observation(t2).Start()!.Label);
        Assert.Equal(0, t2.Reads);
    }

    // ── §10.3 ────────────────────────────────────────────────────────────

    [Fact]
    public void SpanObserver_vetoes_changes_inside_t_autocorrect()
    {
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());

        var now = T0 + Opts.TAutocorrectMs - 300;
        t.KeyPressed(now - 10);
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;

        Assert.Equal("settled", result.Label);
        Assert.Empty(result.Candidates);
        var veto = Assert.Single(result.Vetoes);
        Assert.Equal("autocorrect_window", veto.Kind);
        Assert.Equal("Javis", veto.Orig);
        Assert.Equal("Jarvis", veto.Corr);
        Assert.Empty(result.Event.Spans);
    }

    [Fact]
    public void SpanObserver_vetoes_changes_without_keyboard_origin()
    {
        // no keyboard input at all
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());
        var now = T0 + 5_000;
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;
        Assert.Equal("settled", result.Label);
        Assert.Empty(result.Candidates);
        Assert.Equal("no_keyboard_origin", Assert.Single(result.Vetoes).Kind);
        Assert.False(result.Event.KeyboardOrigin);

        // a key in another process (a co-author, Grammarly) does not count
        var t2 = DocumentWith();
        var o2 = Observation(t2);
        Assert.Null(o2.Start());
        now = T0 + 5_000;
        t2.KeyPressed(now - 10, pid: Pid + 1);
        t2.Edit(ReplaceWord(t2.Text, "Javis", "Jarvis"));
        var r2 = Drive(o2, ref now, now + Opts.TSettleMs + 200)!;
        Assert.Equal("no_keyboard_origin", Assert.Single(r2.Vetoes).Kind);

        // a key in the target process too long before the change does not count either
        var t3 = DocumentWith();
        var o3 = Observation(t3);
        Assert.Null(o3.Start());
        now = T0 + 5_000;
        t3.KeyPressed(now - Opts.TOriginMs - 1);
        t3.Edit(ReplaceWord(t3.Text, "Javis", "Jarvis"));
        var r3 = Drive(o3, ref now, now + Opts.TSettleMs + 200)!;
        Assert.Equal("no_keyboard_origin", Assert.Single(r3.Vetoes).Kind);
        Assert.Empty(r3.Candidates);
    }

    // ── §10.4 ────────────────────────────────────────────────────────────

    [Fact]
    public void SpanObserver_cleared_is_a_negative_trial()
    {
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());

        var now = T0 + 5_000;
        t.KeyPressed(now - 10);
        t.Edit(ReplaceWord(t.Text, Pasted, string.Empty));   // select-all inside the span + delete
        var result = o.Tick(now)!;

        Assert.Equal("cleared", result.Label);
        Assert.True(result.IsNegativeTrial);
        Assert.Equal("app", result.Observed);
        Assert.Equal("cleared", result.Event.Terminal);
        Assert.Equal("h_1", result.Event.Hold);
    }

    [Fact]
    public void SpanObserver_redo_within_t_redo_is_a_negative()
    {
        var observer = new SpanObserver(Opts, selfProcessId: SelfPid);
        var t1 = DocumentWith();
        var first = observer.Begin(t1, Pasted, "h_1", HoldTs, T0, out var none);
        Assert.Null(none);
        Assert.Null(first.Start());
        Assert.True(first.IsOpen);

        // the user re-dictates into the same control 5 s later
        var t2 = DocumentWith("call Jarvis at nine");
        t2.Identity = t1.Identity;
        var second = observer.Begin(t2, "call Jarvis at nine", "h_2", HoldTs + 5_000, T0 + 5_000, out var redo);

        Assert.NotNull(redo);
        Assert.Equal("cleared:redo", redo!.Label);
        Assert.Equal("h_1", redo.Event.Hold);
        Assert.True(redo.IsNegativeTrial);
        Assert.False(first.IsOpen);
        Assert.Same(redo, first.Result);
        Assert.Null(second.Start());
    }

    [Fact]
    public void A_new_hold_outside_t_redo_or_into_another_control_is_not_a_redo()
    {
        var observer = new SpanObserver(Opts, selfProcessId: SelfPid);
        var t1 = DocumentWith();
        var first = observer.Begin(t1, Pasted, "h_1", HoldTs, T0, out _);
        Assert.Null(first.Start());

        var t2 = DocumentWith();
        t2.Identity = t1.Identity with { FocusHandle = 0x9999 };
        observer.Begin(t2, Pasted, "h_2", HoldTs + 5_000, T0 + 5_000, out var redo);
        Assert.Null(redo);
        Assert.Equal("navigated", first.Result!.Label);   // one observer per process: the old one ends, not as a negative

        var t3 = DocumentWith();
        var third = observer.Begin(t3, Pasted, "h_3", HoldTs + 10_000, T0 + 10_000, out _);
        Assert.Null(third.Start());
        var t4 = DocumentWith();
        observer.Begin(t4, Pasted, "h_4", HoldTs + 10_000 + Opts.TRedoMs + 1, T0 + 10_000 + Opts.TRedoMs + 1, out var late);
        Assert.Null(late);
    }

    // ── §10.5 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("wt", "")]
    [InlineData("conhost", "ConsoleWindowClass")]
    [InlineData("cmd", "")]
    [InlineData("powershell", "")]
    [InlineData("pwsh", "")]
    [InlineData("OpenConsole", "")]
    [InlineData("mintty", "mintty")]
    [InlineData("alacritty", "")]
    [InlineData("wezterm-gui", "")]
    [InlineData("kitty", "")]
    [InlineData("putty", "")]
    [InlineData("ubuntu", "")]
    [InlineData("bash", "")]
    [InlineData("POWERSHELL.exe", "")]
    [InlineData("someapp", "ConsoleWindowClass")]
    [InlineData("someapp", "CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData("someapp", "mintty")]
    public void SpanObserver_refuses_terminals(string process, string windowClass)
    {
        var t = DocumentWith();
        t.Identity = new SpanObserverTargetIdentity(process, Pid, windowClass, 1, false);
        var result = Observation(t).Start()!;
        Assert.Equal("unobservable:terminal", result.Label);
        Assert.Equal(0, t.Reads);
        Assert.True(SpanObservation.IsDeniedTarget(process, windowClass));
    }

    [Fact]
    public void SpanObserver_refuses_self_and_an_unresolved_focus()
    {
        var t = DocumentWith();
        t.Identity = new SpanObserverTargetIdentity("Dictation", SelfPid, "HwndWrapper[Dictation.exe;;]", 1, false);
        Assert.Equal("unobservable:self", Observation(t).Start()!.Label);
        Assert.Equal(0, t.Reads);

        var t2 = DocumentWith();
        t2.Identity = SpanObserverTargetIdentity.Unresolved;
        Assert.Equal("unobservable:no_focus", Observation(t2).Start()!.Label);
        Assert.Equal(0, t2.Reads);

        Assert.False(SpanObservation.IsDeniedTarget("notepad", "Notepad"));
    }

    [Fact]
    public void The_app_s_own_panes_are_observed_in_process_with_no_probe()
    {
        var t = DocumentWith();
        t.Identity = SpanObserverTargetIdentity.OwnPane("NotesEditor", 7);
        t.Secure = SecureFieldState.Unknown;   // never asked
        var o = new SpanObservation(t, Pasted, "h_1", HoldTs, T0, Opts, null, null, Environment.ProcessId);
        Assert.Null(o.Start());

        var now = T0 + 5_000;
        t.KeyPressed(now - 10, Environment.ProcessId);
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;
        Assert.Equal("settled", result.Label);
        Assert.Equal("self", result.Observed);
        Assert.Equal("in_process", result.Anchor);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void A_hold_event_records_an_in_process_observation_as_self()
    {
        // The hold event's "observed" field takes the observer's own values,
        // so what SpanObservation reports is what the ledger accepts and
        // writes back unchanged.
        Assert.Equal(
            new[] { SpanObservation.ObservedNone, SpanObservation.ObservedSelf, SpanObservation.ObservedApp },
            LearningEvent.Observed);
        Assert.Equal("self", SpanObservation.ObservedSelf);

        var hold = new HoldEvent("h_1", HoldTs) { Text = "call Jarvis", ObservedState = SpanObservation.ObservedSelf };
        var line = hold.Dumps();
        Assert.Contains("\"observed\":\"self\"", line);
        var read = Assert.IsType<HoldEvent>(LearningEvent.Loads(line));
        Assert.Equal("self", read.ObservedState);
        Assert.Throws<ArgumentException>(() => LearningEvent.Loads(line.Replace("\"observed\":\"self\"", "\"observed\":\"other\"")));
    }

    // ── §10.6 ────────────────────────────────────────────────────────────

    [Fact]
    public void SpanObserver_bounded_uia_wait()
    {
        // Same shape as UiaSecureFieldProbeTests.It_gives_up_rather_than_stalling_the_dictation_path:
        // a call that never answers costs the observation the deadline and nothing more.
        var clock = Stopwatch.StartNew();
        var r = SpanObserver.Bounded(() => { Thread.Sleep(5_000); return "late"; }, TimeSpan.FromMilliseconds(1));
        clock.Stop();

        Assert.False(r.Ok);
        Assert.Equal("uia_timeout", r.Failure);
        Assert.True(clock.ElapsedMilliseconds < 2_000, $"bounded call took {clock.ElapsedMilliseconds}ms with a 1ms deadline");

        // and a timed-out read during tracking ends the observation, never a hang
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());
        t.ReadFailure = SpanObserverFailures.UiaTimeout;
        t.Edit(t.Text + " more");
        Assert.Equal("unobservable:uia_timeout", o.Tick(T0 + 3_000)!.Label);
        Assert.True(t.Unsubscribed);

        // These two assert FAILURE CLASSIFICATION, not the deadline — a named
        // call exception must surface its own reason, and any other exception
        // must land on "exception". The deadline is asserted above, on its own
        // and deliberately tight.
        //
        // They used to pass 5 s, and that made them a lottery: `Bounded` runs
        // the delegate through `Task.Run` and then `task.Wait(deadline)`
        // (SpanObserver.cs:967-975), so on a starved thread pool the work is
        // not even SCHEDULED inside the budget, `Wait` returns false, and the
        // result is `uia_timeout` — the classification under test never
        // happens. Measured on an X Elite laptop 2026-08-24: 3/3 green run in isolation,
        // red in every full-solution pass, `Expected: "no_textpattern"`.
        //
        // 60 s is not a timing claim; it is "long enough that scheduling is
        // not the variable". Bounded rather than infinite so a genuine hang
        // still fails the run instead of parking it forever.
        var classifyBudget = TimeSpan.FromSeconds(60);
        var named = SpanObserver.Bounded<int>(() => throw new SpanObserverCallException("no_textpattern"), classifyBudget);
        Assert.Equal("no_textpattern", named.Failure);
        var thrown = SpanObserver.Bounded<int>(() => throw new InvalidOperationException(), classifyBudget);
        Assert.Equal("exception", thrown.Failure);
    }

    // ── the rest of §5/§6 ────────────────────────────────────────────────

    [Fact]
    public void Unedited_window_settles_without_a_positive_trial_and_focus_loss_is_navigated()
    {
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());
        var now = T0;
        var result = Drive(o, ref now, T0 + Opts.TWindowMs + 1_000, stepMs: 1_000)!;
        Assert.Equal("settled", result.Label);
        Assert.Empty(result.Candidates);
        Assert.Contains("\"unedited\":true", result.Event.Dumps());
        Assert.Contains("\"changes\":0", result.Event.Dumps());
        Assert.Equal(0, result.Changes);

        var t2 = DocumentWith();
        var o2 = Observation(t2);
        Assert.Null(o2.Start());
        t2.Handler!(SpanObserverSignal.FocusLost);
        var r2 = o2.Tick(T0 + 2_000)!;
        Assert.Equal("navigated", r2.Label);
        Assert.Equal("none", r2.Observed);
    }

    [Fact]
    public void Changes_still_arriving_at_the_window_end_are_a_timeout()
    {
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());
        var now = T0;
        SpanObservationResult? result = null;
        var i = 0;
        while (result is null)
        {
            now += 1_000;
            t.KeyPressed(now - 5);
            t.Edit(ReplaceWord(t.Text, "Javis", "Javis" + (i++ % 2 == 0 ? "x" : string.Empty)));
            result = o.Tick(now);
        }
        Assert.Equal("timeout", result.Label);
        Assert.Equal("none", result.Observed);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Formatting_only_pure_insertions_and_wide_rewordings_are_vetoed()
    {
        SpanObservationResult Settle(string corrected, SpanObserverOptions? opts = null, IVocabularyLexicon? lexicon = null, Func<string, bool>? tomb = null)
        {
            var t = DocumentWith();
            var o = Observation(t, opts, lexicon, tomb);
            Assert.Null(o.Start());
            var now = T0 + 5_000;
            t.KeyPressed(now - 10);
            t.Edit(ReplaceWord(t.Text, Pasted, corrected));
            return Drive(o, ref now, now + Opts.TSettleMs + 200)!;
        }

        Assert.Equal("formatting_only", Assert.Single(Settle("Call Javis at nine.").Vetoes).Kind);
        Assert.Equal("rewording_shape", Assert.Single(Settle("call Javis at nine please").Vetoes).Kind);
        Assert.Equal("rewording_shape", Assert.Single(Settle("call at nine").Vetoes).Kind);
        Assert.Equal("rewording_shape", Assert.Single(Settle("ring Jarvis up at about nine").Vetoes).Kind);
        Assert.Equal("tombstoned", Assert.Single(Settle("call Jarvis at nine", tomb: s => s == "Jarvis").Vetoes).Kind);
        Assert.Equal("not_distinctive", Assert.Single(Settle("call Travis at nine", lexicon: VocabularyLexicon.Of(new[] { "travis" })).Vetoes).Kind);

        var ok = Settle("call Jarvis at nine");
        Assert.Empty(ok.Vetoes);
        Assert.Equal("Jarvis", Assert.Single(ok.Candidates).Corr);

        var twoWords = Settle("call Jarvis Ng at nine");
        Assert.Equal("Javis", Assert.Single(twoWords.Candidates).Orig);
        Assert.Equal("Jarvis Ng", twoWords.Candidates[0].Corr);
    }

    [Fact]
    public void Polling_mode_reads_every_t_poll_and_line_endings_are_normalised()
    {
        var t = DocumentWith();
        t.SubscriptionAvailable = false;
        var o = Observation(t);
        Assert.Null(o.Start());
        Assert.True(o.Polling);
        var readsAfterStart = t.Reads;
        o.Tick(T0 + 100);
        Assert.Equal(readsAfterStart, t.Reads);
        o.Tick(T0 + Opts.TPollMs);
        Assert.Equal(readsAfterStart + 1, t.Reads);

        // a CRLF field holding an LF paste still anchors
        var crlf = new FakeTarget { Text = "first line\r\nsecond\r\nline tail", Caret = "first line\r\nsecond\r\nline".Length };
        var o2 = new SpanObservation(crlf, "second\nline", "h_9", HoldTs, T0, Opts, null, null, SelfPid);
        Assert.Null(o2.Start());
        Assert.Contains("A_ok", o2.Trace);
    }

    [Fact]
    public void An_ambiguous_or_absent_span_is_unobservable_and_a_long_document_is_not_read_past_L_read()
    {
        var t = DocumentWith(selection: false);
        t.Text = Pasted + " … " + Pasted;
        Assert.Equal("unobservable:anchor_ambiguous", Observation(t).Start()!.Label);

        var t2 = DocumentWith(selection: false);
        t2.Text = "nothing of the sort";
        Assert.Equal("unobservable:anchor_ambiguous", Observation(t2).Start()!.Label);

        var t3 = DocumentWith(selection: false);
        t3.TextPatternAvailable = false;
        Assert.Equal("unobservable:no_textpattern", Observation(t3).Start()!.Label);

        var small = new SpanObserverOptions { LRead = 40 };
        var t4 = DocumentWith(selection: false);
        var r4 = Observation(t4, small).Start()!;
        Assert.Equal("unobservable:doc_too_long", r4.Label);
        Assert.Equal(small.LRead + 1, r4.ReadChars);
    }

    [Fact]
    public async Task RunAsync_drives_an_observation_to_its_terminal_with_an_injected_clock()
    {
        var t = DocumentWith();
        var o = Observation(t);
        var now = T0;
        var result = await SpanObserver.RunAsync(o, () => now, CancellationToken.None, (ms, _) =>
        {
            now += ms;
            if (now >= T0 + 5_000 && t.Text.Contains("Javis", StringComparison.Ordinal))
            {
                t.KeyPressed(now - 10);
                t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
            }
            return Task.CompletedTask;
        });
        Assert.Equal("settled", result.Label);
        Assert.Equal("Jarvis", Assert.Single(result.Candidates).Corr);
    }

    [Fact]
    public void Knobs_are_printed_with_their_spec_names()
    {
        var d = new SpanObserverOptions().ToDict();
        foreach (var k in new[] { "t_uia", "t_autocorrect", "t_origin", "t_settle", "t_window", "t_poll", "t_redo", "L_span", "L_ctx", "L_read", "max_span_words", "knobs_rev" })
        {
            Assert.True(d.ContainsKey(k), k);
        }
    }

    // ── §5.1 keyboard feed as the hook writes it ─────────────────────────

    [Fact]
    public void SpanObserver_keyboard_signal_from_the_hook_gives_a_change_keyboard_origin()
    {
        // The hook's contract is SpanObserverKeyboardSignal.Record(unixMs, foregroundPid);
        // a key-down in the target process just before the edit makes it a candidate.
        var signal = new SpanObserverKeyboardSignal();
        Assert.Null(signal.Last);
        var t = DocumentWith();
        t.Signal = signal;
        var o = Observation(t);
        Assert.Null(o.Start());
        var now = T0 + 5_000;
        signal.Record(now - 10, Pid);
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;
        Assert.Equal("settled", result.Label);
        Assert.True(result.Event.KeyboardOrigin);
        Assert.Empty(result.Vetoes);
        var c = Assert.Single(result.Candidates);
        Assert.Equal("Javis", c.Orig);
        Assert.Equal("Jarvis", c.Corr);

        // the signal keeps only the last (timestamp, pid): a later key in another process replaces it
        signal.Record(now, Pid + 1);
        Assert.Equal(new SpanObserverKeyboardInput(now, Pid + 1), signal.Last);
    }

    // ── EditedSpan: what Dictation's learning policy is handed ──────────

    [Fact]
    public void A_settled_keyboard_edit_hands_over_the_whole_edited_span_and_nothing_around_it()
    {
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());
        var now = T0 + 5_000;
        t.KeyPressed(now - 10);
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis").Replace("nine", "ten", StringComparison.Ordinal));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;

        Assert.Equal("settled", result.Label);
        // The span, with both edits in it: not the one widened diff of §5.
        Assert.Equal("call Jarvis at ten", result.EditedSpan);
        Assert.DoesNotContain(Sentinel, result.EditedSpan, StringComparison.Ordinal);
        Assert.DoesNotContain("lorem", result.EditedSpan, StringComparison.Ordinal);
    }

    [Fact]
    public void A_crlf_span_is_handed_over_with_line_feeds()
    {
        const string pasted = "call Javis\r\nat nine";
        var t = DocumentWith(pasted);
        var o = new SpanObservation(t, pasted, "h_1", HoldTs, T0, Opts, null, null, SelfPid);
        Assert.Null(o.Start());
        var now = T0 + 5_000;
        t.KeyPressed(now - 10);
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;

        Assert.Equal("call Jarvis\nat nine", result.EditedSpan);
    }

    [Fact]
    public void Nothing_is_handed_over_for_an_edit_the_time_rules_vetoed()
    {
        // No keyboard input: somebody else's change.
        var t = DocumentWith();
        var o = Observation(t);
        Assert.Null(o.Start());
        var now = T0 + 5_000;
        t.Edit(ReplaceWord(t.Text, "Javis", "Jarvis"));
        var result = Drive(o, ref now, now + Opts.TSettleMs + 200)!;
        Assert.Equal("settled", result.Label);
        Assert.Null(result.EditedSpan);

        // Inside the autocorrect window.
        var t2 = DocumentWith();
        var o2 = Observation(t2);
        Assert.Null(o2.Start());
        now = T0 + Opts.TAutocorrectMs - 300;
        t2.KeyPressed(now - 10);
        t2.Edit(ReplaceWord(t2.Text, "Javis", "Jarvis"));
        var r2 = Drive(o2, ref now, now + Opts.TSettleMs + 200)!;
        Assert.Equal("settled", r2.Label);
        Assert.Null(r2.EditedSpan);
    }

    [Fact]
    public void Nothing_is_handed_over_unedited_cleared_navigated_or_timed_out()
    {
        var unedited = Observation(DocumentWith());
        Assert.Null(unedited.Start());
        var now = T0;
        var r1 = Drive(unedited, ref now, T0 + Opts.TWindowMs + 200)!;
        Assert.Equal("settled", r1.Label);
        Assert.Null(r1.EditedSpan);

        var t2 = DocumentWith();
        var cleared = Observation(t2);
        Assert.Null(cleared.Start());
        now = T0 + 5_000;
        t2.KeyPressed(now - 10);
        t2.Edit(ReplaceWord(t2.Text, Pasted, string.Empty));
        Assert.Equal("cleared", cleared.Tick(now)!.Label);
        Assert.Null(cleared.Result!.EditedSpan);

        var t3 = DocumentWith();
        var navigated = Observation(t3);
        Assert.Null(navigated.Start());
        now = T0 + 5_000;
        t3.KeyPressed(now - 10);
        t3.Edit(ReplaceWord(t3.Text, "Javis", "Jarvis"));
        Assert.Null(navigated.Tick(now));
        t3.Handler!(SpanObserverSignal.FocusLost);
        Assert.Equal("navigated", navigated.Tick(now + 100)!.Label);
        Assert.Null(navigated.Result!.EditedSpan);
        // Not handed over, and still counted: the host can say an edit was
        // seen without being given it.
        Assert.Equal(1, navigated.Result!.Changes);

        // Still typing when the window closes.
        var t4 = DocumentWith();
        var busy = Observation(t4);
        Assert.Null(busy.Start());
        now = T0 + Opts.TWindowMs - 100;
        t4.KeyPressed(now - 10);
        t4.Edit(ReplaceWord(t4.Text, "Javis", "Jarvis"));
        Assert.Null(busy.Tick(now));
        Assert.Equal("timeout", busy.Tick(T0 + Opts.TWindowMs)!.Label);
        Assert.Null(busy.Result!.EditedSpan);
    }
}
