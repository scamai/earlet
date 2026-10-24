using System.Collections.Frozen;
using Dictation.Services;
using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Services.Tests;

/// <summary>
/// The dictation state machine, exercised on the failure paths.
///
/// <para>
/// The insertion half is driven through the <em>real</em>
/// <see cref="FocusedTextInsertion"/> rather than a stubbed
/// <see cref="IFocusedTextInserter"/>. A stub would let this file assert
/// whatever outcome it liked, including combinations the real inserter cannot
/// produce; composing the real one with fake Win32 seams means every outcome
/// asserted here is one the shipping code actually emits.
/// </para>
/// </summary>
public sealed class DictationSessionTests
{
    // ─── Fakes ──────────────────────────────────────────────────────

    private sealed class InMemoryPushToTalkHotkey : IPushToTalkHotkey
    {
        public event EventHandler? Pressed;
        public event EventHandler? Released;

        public bool IsHeld { get; private set; }
        public bool IsRegistered { get; private set; }
        public bool StartSucceeds { get; set; } = true;

        public bool Start() => IsRegistered = StartSucceeds;

        public void NotifyHotkeyMessage()
        {
            IsHeld = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        public void PollForRelease()
        {
            if (!IsHeld)
            {
                return;
            }
            IsHeld = false;
            Released?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { }
    }

    private sealed class InMemoryMicrophoneTap : IMicrophoneTap
    {
        public event EventHandler<MicrophoneTapResult>? CaptureCompleted;
        public bool IsCapturing => false;
        public void Complete(MicrophoneTapResult r) => CaptureCompleted?.Invoke(this, r);
        public void Dispose() { }
    }

    private sealed class InMemoryTranscriber : ITranscriber
    {
        /// <summary>
        /// Capitalised deliberately. Since <c>DictationSession</c> runs the two
        /// shipped text rules before inserting, a lower-case default would make
        /// the formatter a variable in every unrelated test in this file — the
        /// insertion tests, the cancel tests, the timing tests — and each would
        /// then be asserting a formatted string for no reason of its own.
        /// Already-formatted text makes the formatter a no-op here, so those
        /// tests keep measuring exactly what they were written to measure. The
        /// formatting tests at the bottom of this file supply their own
        /// unformatted text.
        /// </summary>
        public TranscriptionResult Result { get; set; } =
            new(TranscriptionOutcome.Transcribed, "Hello world");

        /// <summary>Set to hold the decode open so a cancel can race it.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public int Calls { get; private set; }

        public async Task<TranscriptionResult> TranscribeAsync(
            MicrophoneCapture capture, CancellationToken ct)
        {
            Calls++;
            if (Gate is not null)
            {
                await Gate.Task.ConfigureAwait(false);
            }
            return Result;
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryFocusedApplicationResolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Next { get; set; } =
            FocusedApplicationResult.Resolved("notepad", 1234, "Untitled");

        public FocusedApplicationResult Resolve() => Next;
    }

    /// <summary>
    /// Implements both seams, because <see cref="FocusedTextInsertion"/> asks
    /// its <see cref="IClipboard"/> whether it is also readable and cannot
    /// restore anything when it is not.
    /// </summary>
    private sealed class InMemoryMicrophoneAccessProbe : IMicrophoneAccessProbe
    {
        /// <summary>
        /// Unknown by default, which is what a build with no reader answers and
        /// what an unreadable consent store answers — so every pre-existing
        /// test in this file keeps measuring exactly what it was written to
        /// measure.
        /// </summary>
        public MicrophoneAccess Next { get; set; } = MicrophoneAccess.Unknown;

        public bool Throws { get; set; }

        public int Calls { get; private set; }

        public MicrophoneAccess Probe()
        {
            Calls++;
            if (Throws)
            {
                throw new InvalidOperationException("consent store unreadable");
            }

            return Next;
        }
    }

    private sealed class InMemorySecureFieldProbe : ISecureFieldProbe
    {
        public SecureFieldState Next { get; set; } = SecureFieldState.NotSecure;

        /// <summary>Set to make Probe throw, which must not fail a dictation.</summary>
        public bool Throws { get; set; }

        public int Calls { get; private set; }

        public SecureFieldState Probe()
        {
            Calls++;
            if (Throws)
            {
                throw new InvalidOperationException("UIA is having a day");
            }

            return Next;
        }
    }

    private sealed class InMemoryClipboard : IClipboard, IClipboardTextSnapshot
    {
        public string? Held { get; set; } = "the user's own clipboard";
        public bool CanRead { get; set; } = true;
        public bool SetSucceeds { get; set; } = true;

        /// <summary>
        /// Every successful write, in order. <see cref="Held"/> alone cannot
        /// answer "what did the paste deliver": the inserter writes the text,
        /// sends Ctrl+V, and then RESTORES the user's clipboard, so by the time
        /// a test looks, Held is the original again. Asserting the session's
        /// returned Text instead would pass on a session that formatted its own
        /// return value while inserting the raw string — precisely the bug
        /// worth catching.
        /// </summary>
        public List<string> Writes { get; } = new();

        /// <summary>Fails only the restore, not the initial write.</summary>
        public bool FailRestore { get; set; }

        private int _sets;

        public bool TrySet(string text)
        {
            _sets++;
            if (!SetSucceeds)
            {
                return false;
            }
            if (FailRestore && _sets > 1)
            {
                return false;
            }
            Held = text;
            Writes.Add(text);
            return true;
        }

        public bool TryRead(out string? text)
        {
            text = Held;
            return CanRead;
        }
    }

    private sealed class InMemoryPasteKeystroke : IPasteKeystroke
    {
        public bool Succeeds { get; set; } = true;
        public int Sends { get; private set; }

        public bool TrySend()
        {
            Sends++;
            return Succeeds;
        }
    }

    // ─── Harness ────────────────────────────────────────────────────

    private sealed class ScopeLogger : ILogger<DictationSession>
    {
        public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = new();
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                Scopes.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private sealed class Harness : IDisposable
    {
        public InMemoryPushToTalkHotkey Hotkey { get; } = new();
        public InMemoryMicrophoneTap Tap { get; } = new();
        public InMemoryTranscriber Transcriber { get; } = new();
        public InMemoryFocusedApplicationResolver Resolver { get; } = new();
        public InMemoryClipboard Clipboard { get; } = new();
        public InMemoryPasteKeystroke Paste { get; } = new();
        public InMemorySecureFieldProbe SecureField { get; } = new();
        public InMemoryMicrophoneAccessProbe MicrophoneAccess { get; } = new();
        public DictationSession Session { get; }
        public List<DictationSessionResult> Completions { get; } = new();

        /// <summary>
        /// The user's per-application exceptions, as the session reads them.
        /// Empty by default, which is what a build with no list has.
        /// </summary>
        public HashSet<string> FormattingExceptions { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Harness(
            Func<bool>? formattingEnabled = null,
            bool withTargetResolver = true,
            bool withSecureFieldProbe = true,
            ILogger<DictationSession>? logger = null)
        {
            var inserter = new FocusedTextInsertion(
                Resolver, Clipboard, Paste, NullLogger<FocusedTextInsertion>.Instance,
                // Zero, so the suite does not sleep the shipped 500 ms paste
                // settle per insertion. The delay's own ordering contract has
                // a dedicated test in FocusedTextInsertionSettleTests.
                pasteSettleDelay: TimeSpan.Zero);

            Session = new DictationSession(
                Hotkey, Tap, Transcriber, inserter,
                logger ?? NullLogger<DictationSession>.Instance,
                // Null by default, which is the SHIPPED setting (formatting on)
                // rather than a quiet test-only path. Every pre-existing test in
                // this file therefore runs against the same pipeline a user has.
                formattingEnabled,
                // The SAME resolver the inserter reads, so a test that points
                // the foreground at a terminal points BOTH halves of the
                // pipeline at it. Two resolvers here would let a test assert a
                // formatting decision made about a window the paste never went
                // to, which is the one thing this seam must not allow.
                withTargetResolver ? Resolver : null,
                // Answers NotSecure by default, which is the ordinary case and
                // leaves every pre-existing test in this file measuring exactly
                // what it was written to measure.
                withSecureFieldProbe ? SecureField : null,
                // Answers Unknown by default, so the only behaviour it changes
                // is the one a test asks for. It is consulted on ONE path — the
                // way to MicrophoneUnavailable — and nowhere else.
                MicrophoneAccess,
                // A Func, not a value, exactly as the shipped composition
                // passes it — so a test can add an application mid-session and
                // the next dictation honours it, which is the property the
                // delegate exists for.
                //
                // A FRESH set per call, deliberately, because that is what the
                // shipped seam does: the pane's FormattingExceptionSet is
                // REPLACED with a new frozen set on every edit. Handing out one
                // long-lived mutable HashSet instead would make a
                // capture-the-value-at-construction bug invisible here — the
                // session would hold the same object and see the mutation
                // anyway — which is exactly what the first version of this did.
                () => FormattingExceptions.ToFrozenSet(StringComparer.OrdinalIgnoreCase));

            Session.Completed += (_, r) => Completions.Add(r);
        }

        public static MicrophoneTapResult Captured() =>
            new(MicrophoneTapOutcome.Captured,
                new MicrophoneCapture(new short[16000], 16000, 1, TimeSpan.FromSeconds(1)));

        public void Dispose() => Session.Dispose();
    }

    // ─── Arming ─────────────────────────────────────────────────────

    [Fact]
    public void Arm_claims_the_hotkey_and_becomes_armed()
    {
        using var h = new Harness();
        Assert.True(h.Session.Arm());
        Assert.Equal(DictationState.Armed, h.Session.State);
    }

    [Fact]
    public void Arm_returns_false_and_stays_idle_when_the_hotkey_is_taken()
    {
        using var h = new Harness();
        h.Hotkey.StartSucceeds = false;

        Assert.False(h.Session.Arm());
        Assert.Equal(DictationState.Idle, h.Session.State);
    }

    [Fact]
    public void A_press_before_arming_does_nothing()
    {
        using var h = new Harness();
        h.Hotkey.NotifyHotkeyMessage();

        // Not Listening: an unarmed session must not open a dictation, or the
        // feature would be live before the user enabled it.
        Assert.Equal(DictationState.Idle, h.Session.State);
    }

    [Fact]
    public void A_press_after_arming_starts_listening()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();

        Assert.Equal(DictationState.Listening, h.Session.State);
    }

    // ─── Capture failures ───────────────────────────────────────────

    [Fact]
    public async Task The_pipeline_scope_uses_the_capture_operation_id()
    {
        var logger = new ScopeLogger();
        using var h = new Harness(logger: logger);

        await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.NoAudio, null, "hold-abc"),
            default);

        var scope = Assert.Single(logger.Scopes);
        Assert.Equal("hold-abc", scope["OperationId"]);
        Assert.Equal("dictation", scope["OperationKind"]);
    }

    [Fact]
    public async Task Every_successful_hold_logs_one_terminal_privacy_safe_record()
    {
        var logger = new ScopeLogger();
        using var h = new Harness(logger: logger);

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(
                MicrophoneTapOutcome.Captured,
                new MicrophoneCapture(
                    new short[16000], 16000, 1, TimeSpan.FromSeconds(1)),
                "hold-success"),
            default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        var line = Assert.Single(logger.Lines, line =>
            line.Contains("Dictation completed:", StringComparison.Ordinal));
        Assert.Contains("operation=hold-success", line, StringComparison.Ordinal);
        Assert.Contains("outcome=Inserted", line, StringComparison.Ordinal);
        Assert.Contains("target=notepad", line, StringComparison.Ordinal);
        Assert.Contains("asr_invoked=True", line, StringComparison.Ordinal);
        Assert.Contains("chars=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("chars=0", line, StringComparison.Ordinal);
        Assert.Contains("elapsed_ms=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello world", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MicrophoneTapOutcome.TooShort, DictationOutcome.HoldTooShort)]
    [InlineData(MicrophoneTapOutcome.DeviceUnavailable, DictationOutcome.MicrophoneUnavailable)]
    [InlineData(MicrophoneTapOutcome.NoAudio, DictationOutcome.NoAudio)]
    public async Task A_hold_that_produced_no_audio_never_reaches_the_recognizer(
        MicrophoneTapOutcome captured, DictationOutcome expected)
    {
        using var h = new Harness();
        h.Session.Arm();

        var result = await h.Session.RunAsync(new MicrophoneTapResult(captured, null), default);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Text);
        Assert.False(result.TextWasDelivered);
        Assert.Equal(0, h.Transcriber.Calls);
        Assert.Equal(0, h.Paste.Sends);
    }

    // ─── The quiet room ─────────────────────────────────────────────

    /// <summary>
    /// A capture whose loudest 30 ms frame is under the audible floor —
    /// hold_0500ms.wav's own reading, 0.00227 against 0.0040.
    /// </summary>
    private static MicrophoneTapResult QuietRoom() =>
        new(MicrophoneTapOutcome.Captured,
            new MicrophoneCapture(new short[8000], 16000, 1, TimeSpan.FromSeconds(0.5))
            {
                FrameRmsPeak = 0.00227f,
                VoicedSeconds = 0f,
            });

    [Fact]
    public async Task A_hold_with_no_speech_energy_never_reaches_the_recognizer()
    {
        // Measured on the device 2026-08-20 at beam 4, through the shipped IPC
        // path, on exactly this clip: `whisper-base-qnn` typed `날씨였습니다.`
        // and `whisper-large-v3-turbo-qnn` typed `Thank you.` Neither model's
        // own gate fired, and turbo's structurally cannot — it puts ~1e-11 on
        // its `<|nospeech|>` token on every input including speech, so no
        // threshold on that number exists.
        //
        // Asking is the defect. `Calls == 0` is the assertion.
        using var h = new Harness();
        h.Session.Arm();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "Thank you.");

        var result = await h.Session.RunAsync(QuietRoom(), default);

        Assert.Equal(0, h.Transcriber.Calls);
        Assert.Equal(DictationOutcome.NoSpeech, result.Outcome);
        Assert.False(result.TextWasDelivered);
        Assert.Equal(0, h.Paste.Sends);
    }

    [Fact]
    public async Task The_three_ways_a_hold_produces_nothing_stay_three_different_sentences()
    {
        // A quiet room, a dead microphone and a brushed key are fixed by three
        // completely different actions, and a user told to speak up when their
        // microphone is dead is a lie this app has shipped once. The pill text
        // is asserted, not just the enum, because the enum is not what the
        // user reads.
        using var h = new Harness();
        h.Session.Arm();

        var quiet = await h.Session.RunAsync(QuietRoom(), default);
        h.Session.Arm();
        var dead = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.NoAudio, null), default);
        h.Session.Arm();
        var brushed = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.TooShort, null), default);

        Assert.Equal(DictationOutcome.NoSpeech, quiet.Outcome);
        Assert.Equal(DictationOutcome.NoAudio, dead.Outcome);
        Assert.Equal(DictationOutcome.HoldTooShort, brushed.Outcome);

        var sentences = new[] { quiet, dead, brushed }
            .Select(r => DictationHudModel.For(r).Text)
            .ToArray();

        Assert.Equal(3, sentences.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("heard", sentences[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("delivered nothing", sentences[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("too short", sentences[2], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_hold_that_carries_speech_energy_still_reaches_the_recognizer()
    {
        // The other half. A gate that rejects everything passes the test above
        // and deletes the product.
        using var h = new Harness();
        h.Session.Arm();

        var spoken = new MicrophoneTapResult(
            MicrophoneTapOutcome.Captured,
            new MicrophoneCapture(new short[16000], 16000, 1, TimeSpan.FromSeconds(1))
            {
                FrameRmsPeak = 0.16944f,   // the quietest real hold in the corpus
                VoicedSeconds = 1.23f,
            });

        var result = await h.Session.RunAsync(spoken, default);

        Assert.Equal(1, h.Transcriber.Calls);
        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
    }

    [Fact]
    public async Task A_capture_nobody_measured_is_not_treated_as_a_quiet_room()
    {
        // Absence of evidence carries on. Every capture built by something
        // that does not measure — a test rig, a second tap added later —
        // reaches the recogniser, the same direction SecureFieldState.Unknown
        // chose and for the same reason.
        using var h = new Harness();
        h.Session.Arm();

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Null(Harness.Captured().Audio!.FrameRmsPeak);
        Assert.Equal(1, h.Transcriber.Calls);
        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
    }

    // ─── Recognizer failures ────────────────────────────────────────

    [Theory]
    [InlineData(TranscriptionOutcome.NoSpeech, DictationOutcome.NoSpeech)]
    [InlineData(TranscriptionOutcome.RecognizerUnavailable, DictationOutcome.RecognizerUnavailable)]
    [InlineData(TranscriptionOutcome.RecognizerFailed, DictationOutcome.RecognizerFailed)]
    public async Task A_recognizer_that_returned_no_words_inserts_nothing(
        TranscriptionOutcome transcribed, DictationOutcome expected)
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Transcriber.Result = new TranscriptionResult(transcribed, null);

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(expected, result.Outcome);
        Assert.False(result.TextWasDelivered);
        Assert.Equal(0, h.Paste.Sends);
    }

    [Fact]
    public async Task A_whitespace_only_transcript_is_treated_as_nothing_said()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Transcriber.Result = new TranscriptionResult(TranscriptionOutcome.Transcribed, "   ");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        // Pasting whitespace would replace a selection with nothing visible,
        // which is destructive and looks like a bug in the editor.
        Assert.Equal(DictationOutcome.NoSpeech, result.Outcome);
        Assert.Equal(0, h.Paste.Sends);
    }

    // ─── Insertion ──────────────────────────────────────────────────

    [Fact]
    public async Task Words_reach_the_focused_window_and_the_clipboard_comes_back()
    {
        using var h = new Harness();
        h.Session.Arm();

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal("Hello world", result.Text);
        Assert.True(result.TextWasDelivered);
        Assert.Equal(1, h.Paste.Sends);
        Assert.Equal("the user's own clipboard", h.Clipboard.Held);
    }

    [Fact]
    public async Task Nothing_is_typed_when_nothing_has_focus()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Resolver.Next = FocusedApplicationResult.NoFocusedWindow;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.NoFocusedWindow, result.Outcome);
        Assert.False(result.TextWasDelivered);
        Assert.Null(result.Text);
        Assert.Equal(0, h.Paste.Sends);
    }

    [Fact]
    public async Task A_refused_paste_is_not_reported_as_delivered()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Paste.Succeeds = false;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.InsertionRefused, result.Outcome);
        Assert.False(result.TextWasDelivered);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task A_lost_clipboard_is_still_a_delivery()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Clipboard.FailRestore = true;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        // Both halves matter: the user's document did change, and something
        // they owned was lost. One outcome that said only one of those would
        // be wrong either way round.
        Assert.Equal(DictationOutcome.ClipboardRestoreFailed, result.Outcome);
        Assert.True(result.TextWasDelivered);
        Assert.Equal("Hello world", result.Text);
    }

    [Fact]
    public async Task An_unreadable_clipboard_does_not_stop_the_dictation()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Clipboard.CanRead = false;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        // Nothing could be stashed, so nothing can be restored — but the words
        // still belong in the document.
        Assert.True(result.TextWasDelivered);
    }

    // ─── Cancellation ───────────────────────────────────────────────

    [Fact]
    public void Cancel_from_idle_is_a_no_op()
    {
        using var h = new Harness();
        h.Session.Cancel();

        Assert.Equal(DictationState.Idle, h.Session.State);
        Assert.Empty(h.Completions);
    }

    [Fact]
    public void Cancel_from_armed_is_a_no_op()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Session.Cancel();

        Assert.Equal(DictationState.Armed, h.Session.State);
        Assert.Empty(h.Completions);
    }

    [Fact]
    public void Cancel_from_listening_returns_to_armed()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();

        h.Session.Cancel();

        Assert.Equal(DictationState.Armed, h.Session.State);
        Assert.Equal(DictationOutcome.Cancelled, Assert.Single(h.Completions).Outcome);
    }

    [Fact]
    public async Task Cancel_during_transcription_discards_the_transcript()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Transcriber.Gate = new TaskCompletionSource();

        var run = h.Session.RunAsync(Harness.Captured(), default);
        Assert.Equal(DictationState.Transcribing, h.Session.State);

        h.Session.Cancel();
        h.Transcriber.Gate.SetResult();   // the decode finishes *after* the cancel

        var result = await run;

        // The recognizer did return words. They are dropped because the window
        // they were meant for is no longer necessarily the one in front of the
        // user — this is the guarantee the generation counter exists for, and
        // it must hold even though the fake transcriber never observed the
        // cancellation token.
        Assert.Equal(DictationOutcome.Cancelled, result.Outcome);
        Assert.False(result.TextWasDelivered);
        Assert.Equal(0, h.Paste.Sends);
    }

    [Fact]
    public async Task A_new_press_during_transcription_supersedes_the_old_dictation()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Transcriber.Gate = new TaskCompletionSource();

        var run = h.Session.RunAsync(Harness.Captured(), default);
        h.Hotkey.NotifyHotkeyMessage();          // user starts a second hold
        h.Transcriber.Gate.SetResult();

        var result = await run;

        Assert.Equal(DictationOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, h.Paste.Sends);
        Assert.Equal(DictationState.Listening, h.Session.State);
    }

    // ─── Lifecycle ──────────────────────────────────────────────────

    [Fact]
    public async Task State_returns_to_armed_after_a_completed_dictation()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();

        await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationState.Armed, h.Session.State);
    }

    [Fact]
    public void Dispose_unsubscribes_so_a_late_event_cannot_revive_the_session()
    {
        var h = new Harness();
        h.Session.Arm();
        h.Session.Dispose();

        h.Hotkey.NotifyHotkeyMessage();
        h.Tap.Complete(Harness.Captured());

        Assert.Equal(DictationState.Armed, h.Session.State);
        Assert.Equal(0, h.Transcriber.Calls);
    }

    [Fact]
    public void Constructor_rejects_a_missing_collaborator()
    {
        Assert.Throws<ArgumentNullException>(() => new DictationSession(
            null!, new InMemoryMicrophoneTap(), new InMemoryTranscriber(),
            new FocusedTextInsertion(new InMemoryFocusedApplicationResolver(), new InMemoryClipboard(), new InMemoryPasteKeystroke(),
                NullLogger<FocusedTextInsertion>.Instance),
            NullLogger<DictationSession>.Instance));
    }

    // ── Stage timings reach the result ───────────────────────────────────

    [Fact]
    public async Task ASuccessfulDictationCarriesTheDecodeTimeItWasGiven()
    {
        // The recognizer already measures its own decode and the session used
        // to drop it on the floor, so every history row was written with a null
        // decode and the Recent pane's "typical wait" could only ever be an em
        // dash. This asserts the PRODUCTION path — a test that hands Record a
        // hand-made TimeSpan proves the store, not the wiring.
        var h = new Harness();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "hello world",
            Latency: TimeSpan.FromMilliseconds(1234));

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), result.DecodeDuration);
    }

    [Fact]
    public async Task ASuccessfulDictationTimesItsOwnInsertion()
    {
        // Not asserted against a threshold — a wall-clock assertion in a test
        // is a flake waiting for a loaded CI box. What matters is that the
        // stage was measured at all rather than left null, because null is what
        // "we never got here" means and insertion plainly happened.
        var h = new Harness();

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.NotNull(result.InsertDuration);
        Assert.True(result.InsertDuration >= TimeSpan.Zero);
    }

    // ── The captured audio reaches the result — on deliveries only ───────

    [Fact]
    public async Task ADeliveredDictationCarriesItsOwnCapturedAudio()
    {
        // Same-reference, not just non-null: a completion handler that keeps
        // the clip must get this hold's audio, and a copy or a re-capture would
        // be a different clip than the hold that produced the words.
        var h = new Harness();
        var captured = Harness.Captured();

        var result = await h.Session.RunAsync(captured, default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Same(captured.Audio, result.Audio);
    }

    [Fact]
    public async Task ALostClipboardStillDeliversTheAudio()
    {
        // ClipboardRestoreFailed is the one outcome that is both a delivery
        // and a failure, and it keeps its Text — the audio follows the same
        // TextWasDelivered line, not the success/failure line.
        var h = new Harness();
        h.Clipboard.FailRestore = true;
        var captured = Harness.Captured();

        var result = await h.Session.RunAsync(captured, default);

        Assert.Equal(DictationOutcome.ClipboardRestoreFailed, result.Outcome);
        Assert.Same(captured.Audio, result.Audio);
    }

    [Fact]
    public async Task AnUndeliveredDictationCarriesNoAudio()
    {
        // The audio existed — the hold was real and the recognizer heard
        // words — but nothing landed, so no audio of the failed hold may
        // linger for a store to pick up. Same discipline Text keeps, and the
        // reason a refusal can never grow a play button.
        var h = new Harness();
        h.Paste.Succeeds = false;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.InsertionRefused, result.Outcome);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task ARecognizerFailureCarriesNoAudio()
    {
        var h = new Harness();
        h.Transcriber.Result = new TranscriptionResult(TranscriptionOutcome.NoSpeech, null);

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.NoSpeech, result.Outcome);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task ACancelledDictationCarriesNoAudio()
    {
        var h = new Harness();
        h.Session.Arm();
        h.Transcriber.Gate = new TaskCompletionSource();

        var run = h.Session.RunAsync(Harness.Captured(), default);
        h.Session.Cancel();
        h.Transcriber.Gate.SetResult();

        var result = await run;

        Assert.Equal(DictationOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Audio);
    }

    [Fact]
    public async Task AFailureBeforeTheRecognizerReportsNoDecodeTime()
    {
        // Absent, not zero. A history row that recorded 0 ms for a dictation
        // that never reached the recognizer would put a measurement on screen
        // that nobody made, and would drag any average toward it.
        var h = new Harness();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.RecognizerUnavailable, null);

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.RecognizerUnavailable, result.Outcome);
        Assert.Null(result.DecodeDuration);
        Assert.Null(result.InsertDuration);
    }

    // ─── Formatting ─────────────────────────────────────────────────
    //
    // The two shipped text rules run between the recogniser and the cursor.
    // Before this, Windows handed transcript.Text straight to the inserter, so
    // a Windows dictation arrived uncapitalised and with the recogniser's
    // trailing ellipsis intact while the same sentence on macOS did not — the
    // single most visible difference in the text that lands under the cursor.

    [Fact]
    public async Task The_shipped_default_formats_what_it_inserts()
    {
        // No delegate at all, which is what a composition with no settings graph
        // gets and what every other test in this file uses. It must format:
        // macOS ships DictationFormattingStore.shippedDefault = true, and an
        // absent preference means the shipped one, never "off".
        using var h = new Harness();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "the quarterly numbers are ready...");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        // The FIRST clipboard write is what the paste actually delivered.
        // Reading Held would find the user's own clipboard, because the
        // inserter restores it afterwards.
        Assert.Equal("The quarterly numbers are ready", h.Clipboard.Writes[0]);
    }

    [Fact]
    public async Task The_result_reports_the_text_that_actually_landed()
    {
        // The character count on the Recent pane and in the history file both
        // derive from this string. If it were the raw transcript while the
        // formatted text went to the cursor, every row would report a length
        // nobody typed.
        using var h = new Harness();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "hello there...");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal("Hello there", result.Text);
    }

    [Fact]
    public async Task Formatting_switched_off_inserts_the_recogniser_text_verbatim()
    {
        using var h = new Harness(() => false);
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "the quarterly numbers are ready...");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal("the quarterly numbers are ready...", h.Clipboard.Writes[0]);
        Assert.Equal("the quarterly numbers are ready...", result.Text);
    }

    [Fact]
    public async Task The_preference_is_read_per_dictation_not_captured_once()
    {
        // The whole reason the seam is a Func. A value snapshotted at
        // construction would leave a user who flips the switch dictating under
        // the old setting until the next arm toggle rebuilt the session — and
        // nothing on the pane says a restart is needed, because none is.
        var enabled = false;
        using var h = new Harness(() => enabled);
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "first one...");

        await h.Session.RunAsync(Harness.Captured(), default);
        Assert.Equal("first one...", h.Clipboard.Writes[0]);

        enabled = true;
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "second one...");

        await h.Session.RunAsync(Harness.Captured(), default);
        // Writes[2]: each dictation writes twice (the text, then the restore).
        Assert.Equal("Second one", h.Clipboard.Writes[2]);
    }

    [Fact]
    public async Task Formatting_never_reaches_a_dictation_that_produced_no_words()
    {
        // The formatter is applied AFTER the empty/whitespace guard, so a
        // recogniser that returned nothing still ends as NoSpeech rather than
        // being formatted into an empty insert.
        using var h = new Harness();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "   ");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.NoSpeech, result.Outcome);
        Assert.Equal(0, h.Paste.Sends);
    }

    [Fact]
    public async Task Formatting_does_not_touch_the_recognisers_own_timing()
    {
        // Two string scans over a sentence are not a stage the user waits
        // through, and folding them into the decode figure would put a cost in
        // a bucket that has to keep meaning "the worker".
        using var h = new Harness();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "some words...",
            Latency: TimeSpan.FromMilliseconds(777));

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(TimeSpan.FromMilliseconds(777), result.DecodeDuration);
    }

    // ─── Formatting, per target application ─────────────────────────
    //
    // macOS's DictationFormatter.profile(forApp:) returns .none for iTerm2,
    // Ghostty and Terminal — no rules at all, not even the capitalisation that
    // measured safe there, because capitalising a shell command was never a
    // useful edit. Windows had no per-app branch: `git status` dictated into
    // Windows Terminal arrived as `Git status`, and the only escape was turning
    // tidy-up off everywhere.

    [Fact]
    public async Task A_terminal_gets_the_transcript_byte_for_byte()
    {
        using var h = new Harness();
        h.Resolver.Next = FocusedApplicationResult.Resolved("WindowsTerminal", 42, "pwsh");
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "git status");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        // Not "Git status". The first clipboard write is what the paste
        // delivered — reading Held would find the user's own clipboard back.
        Assert.Equal("git status", h.Clipboard.Writes[0]);
        Assert.Equal("git status", result.Text);
    }

    [Fact]
    public async Task A_terminal_keeps_a_trailing_ellipsis_too()
    {
        // .none is the WHOLE rule set withheld, not just the capitaliser. A
        // half-ported gate that dropped only capitalisation would still edit a
        // heredoc's trailing dots out of a shell line.
        using var h = new Harness();
        h.Resolver.Next = FocusedApplicationResult.Resolved("pwsh", 42, "pwsh");
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "echo hello...");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal("echo hello...", h.Clipboard.Writes[0]);
    }

    [Fact]
    public async Task An_ordinary_application_still_gets_the_shipped_rules()
    {
        // The gate must be a gate and not an off switch. Same transcript, same
        // session, one field different.
        using var h = new Harness();
        h.Resolver.Next = FocusedApplicationResult.Resolved("notepad", 42, "Untitled");
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "git status");

        await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal("Git status", h.Clipboard.Writes[0]);
    }

    [Fact]
    public async Task A_window_whose_process_cannot_be_named_gets_the_default_rules()
    {
        // Unresolvable is not a terminal. macOS falls back to .standard for a
        // nil bundle id — never to the ungated set, and never to silence.
        //
        // The insertion itself is refused on this path (the inserter will not
        // type into a window it cannot identify), so what is asserted is the
        // outcome, not a clipboard write: the point is that "unknown" did not
        // take the terminal branch.
        using var h = new Harness();
        h.Resolver.Next = FocusedApplicationResult.Unresolvable("Some window");
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "git status");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.InsertionRefused, result.Outcome);
    }

    [Fact]
    public async Task A_session_built_without_a_resolver_formats_as_it_always_did()
    {
        // The optional parameter's whole contract: an older composition, or a
        // test that never passed one, keeps the conservative global default
        // rather than silently losing formatting.
        using var h = new Harness(withTargetResolver: false);
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "git status");

        await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal("Git status", h.Clipboard.Writes[0]);
    }

    [Fact]
    public async Task The_global_tidy_up_switch_still_wins_over_the_per_app_gate()
    {
        // Off everywhere means off in a terminal too — the two controls compose
        // rather than one overriding the other, and a user who turned tidy-up
        // off must not find it back on because they happened to be in Notepad.
        using var h = new Harness(() => false);
        h.Resolver.Next = FocusedApplicationResult.Resolved("notepad", 42, "Untitled");
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "git status...");

        await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal("git status...", h.Clipboard.Writes[0]);
    }

    // ─── The password-field guard ───────────────────────────────────────
    //
    // macOS refuses at the door: Secure Input disables event taps, so dictation
    // physically cannot run in a password box and SecureInputMonitor's only job
    // is to produce the right sentence. Windows has no such mode — a
    // WH_KEYBOARD_LL hook is not disabled by anything — so without this guard
    // the whole pipeline completed: a spoken password was transcribed, written
    // to dictation-history.jsonl, recorded as a wav, put on the clipboard and
    // pasted into the login box.

    [Fact]
    public async Task A_password_field_stops_the_dictation_before_the_recogniser()
    {
        using var h = new Harness();
        h.SecureField.Next = SecureFieldState.Secure;
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "hunter two");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.SecureField, result.Outcome);
        // The recogniser never ran. This is the assertion that matters most:
        // everything downstream is gated on a transcript, so a guard that let
        // the decode happen and then discarded it would still have put the
        // spoken password through the worker.
        Assert.Equal(0, h.Transcriber.Calls);
    }

    [Fact]
    public async Task A_password_field_leaves_nothing_on_the_clipboard_and_types_nothing()
    {
        using var h = new Harness();
        h.SecureField.Next = SecureFieldState.Secure;

        await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Empty(h.Clipboard.Writes);
        Assert.Equal(0, h.Paste.Sends);
        // And the user's own clipboard was never even read, let alone borrowed.
        Assert.Equal("the user's own clipboard", h.Clipboard.Held);
    }

    [Fact]
    public async Task A_password_field_carries_no_text_and_no_audio_to_the_stores()
    {
        // Structural rather than a rule at the call site: the recorder writes a
        // wav only for a delivered outcome and a history line only carries text
        // for one, so these two nulls ARE "no transcript on disk, no recording
        // of the password on disk".
        using var h = new Harness();
        h.SecureField.Next = SecureFieldState.Secure;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Null(result.Text);
        Assert.Null(result.Audio);
        Assert.False(result.TextWasDelivered);
    }

    [Fact]
    public async Task An_unanswerable_probe_lets_the_dictation_through()
    {
        // The direction that matters. A guard that failed closed would switch
        // dictation off, silently, on every machine where UIA is slow or
        // restricted — and "dictation is broken" is the reading this feature
        // exists to prevent.
        using var h = new Harness();
        h.SecureField.Next = SecureFieldState.Unknown;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
    }

    [Fact]
    public async Task A_probe_that_throws_lets_the_dictation_through()
    {
        // ISecureFieldProbe is documented never to throw. The session catches
        // anyway, because the cost of being wrong is a crash raised from inside
        // the guard whose whole purpose is to fail politely.
        using var h = new Harness();
        h.SecureField.Throws = true;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
    }

    [Fact]
    public async Task A_session_with_no_probe_behaves_exactly_as_it_did_before()
    {
        using var h = new Harness(withSecureFieldProbe: false);

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
    }

    [Fact]
    public async Task An_ordinary_field_is_asked_exactly_once_per_dictation()
    {
        // Not per binding read and not per stage: it is a cross-process call.
        using var h = new Harness();

        await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(1, h.SecureField.Calls);
    }

    [Fact]
    public async Task A_hold_that_never_reached_the_microphone_does_not_ask_at_all()
    {
        // The guard sits after the capture check. There is nothing to protect
        // on a hold that produced no audio, and asking UIA about it would be a
        // cross-process call on a path that is already an error.
        using var h = new Harness();

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.TooShort, null), default);

        Assert.Equal(DictationOutcome.HoldTooShort, result.Outcome);
        Assert.Equal(0, h.SecureField.Calls);
    }

    [Fact]
    public void The_password_outcome_reads_as_a_password_field_and_not_as_a_failure()
    {
        // The whole point of giving this its own enum member. "The paste did
        // not land" reads as a broken app; this reads as an app that noticed.
        var pill = DictationHudModel.For(new DictationSessionResult(
            DictationOutcome.SecureField, null, null));

        Assert.Contains("password field", pill.Text, StringComparison.OrdinalIgnoreCase);
        // And it does NOT name the application. The pill is visible to whoever
        // is behind the user and to whatever is screen-sharing, and naming the
        // thing somebody is signing into is a fact worth withholding there.
        Assert.DoesNotContain(" into ", pill.Text, StringComparison.Ordinal);
    }

    // ─── The whole wait ─────────────────────────────────────────────
    //
    // releaseToDone has been started, stopped and logged on every dictation
    // since RunAsync was written, and reached nothing a user could read. The
    // Insights pane therefore reported DecodeMs + InsertMs under macOS's
    // heading for this span, which is systematically the smaller number.

    [Fact]
    public async Task A_finished_dictation_carries_the_whole_release_to_done_wait()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.NotNull(result.TotalDuration);
        // Never less than the stages inside it. The two stages are timed by
        // stopwatches that start and stop INSIDE the one this asserts about, so
        // this relationship is structural — if it ever inverts, the wrong
        // stopwatch is being read.
        Assert.True(
            result.TotalDuration >= (result.DecodeDuration ?? TimeSpan.Zero)
                + (result.InsertDuration ?? TimeSpan.Zero),
            $"total {result.TotalDuration} < decode {result.DecodeDuration} + insert {result.InsertDuration}");
    }

    [Fact]
    public async Task A_hold_that_never_reached_the_recogniser_has_no_total()
    {
        // Absent, not zero. A refusal before the stopwatch starts has no wait
        // to report, and a zero here would drag every median it landed in.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.TooShort, null), default);

        Assert.Equal(DictationOutcome.HoldTooShort, result.Outcome);
        Assert.Null(result.TotalDuration);
    }

    [Fact]
    public async Task A_dictation_that_typed_nothing_still_reports_how_long_it_took()
    {
        // NOT gated on delivery, unlike Text and Audio. How long a failure took
        // to fail is exactly as measured as how long a success took to succeed,
        // and a pane that only counted successes would report a machine that is
        // faster than the one the user has.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.Resolver.Next = FocusedApplicationResult.NoFocusedWindow;

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.False(result.TextWasDelivered);
        Assert.NotNull(result.TotalDuration);
    }

    // ─── Windows refusing the microphone ────────────────────────────
    //
    // Every microphone problem used to be one pill, "The microphone would not
    // open", which is what a broken feature looks like. Windows 11's per-app
    // microphone switch is the likeliest first-run failure on a managed
    // machine, and nothing said a permission existed.

    [Fact]
    public async Task A_denied_microphone_is_reported_as_blocked_rather_than_broken()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.MicrophoneAccess.Next = Dictation.Services.MicrophoneAccess.Denied;

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.DeviceUnavailable, null), default);

        Assert.Equal(DictationOutcome.MicrophoneBlocked, result.Outcome);
    }

    [Fact]
    public async Task A_policy_denial_is_reported_as_blocked_too()
    {
        // Same outcome, different sentence — the copy layer draws that line, so
        // the session does not have to carry two members for one fact.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.MicrophoneAccess.Next = Dictation.Services.MicrophoneAccess.DeniedByPolicy;

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.DeviceUnavailable, null), default);

        Assert.Equal(DictationOutcome.MicrophoneBlocked, result.Outcome);
    }

    [Theory]
    [InlineData(Dictation.Services.MicrophoneAccess.Unknown)]
    [InlineData(Dictation.Services.MicrophoneAccess.Allowed)]
    [InlineData(Dictation.Services.MicrophoneAccess.Busy)]
    public async Task Anything_short_of_certainty_leaves_the_plain_device_failure(
        Dictation.Services.MicrophoneAccess access)
    {
        // Unknown must never become an accusation. Busy is not a denial either:
        // Windows shares capture endpoints, so it is a fact worth naming on the
        // card and not a reason to relabel the failure.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.MicrophoneAccess.Next = access;

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.DeviceUnavailable, null), default);

        Assert.Equal(DictationOutcome.MicrophoneUnavailable, result.Outcome);
    }

    [Fact]
    public async Task A_probe_that_throws_costs_the_distinction_and_not_the_outcome()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.MicrophoneAccess.Throws = true;

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.DeviceUnavailable, null), default);

        Assert.Equal(DictationOutcome.MicrophoneUnavailable, result.Outcome);
    }

    [Fact]
    public async Task The_consent_store_is_not_read_on_a_dictation_that_worked()
    {
        // A registry walk on every successful hold is work bought with nothing
        // — and a permission that was fine a moment ago is not evidence about a
        // hold that succeeded.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();

        await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(0, h.MicrophoneAccess.Calls);
    }

    [Fact]
    public async Task A_short_hold_is_still_a_short_hold_even_under_a_denial()
    {
        // Only DeviceUnavailable asks. TooShort and NoAudio mean what they say,
        // and relabelling them would throw away the distinction the outcome
        // enum exists to preserve.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.MicrophoneAccess.Next = Dictation.Services.MicrophoneAccess.Denied;

        var result = await h.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.TooShort, null), default);

        Assert.Equal(DictationOutcome.HoldTooShort, result.Outcome);
        Assert.Equal(0, h.MicrophoneAccess.Calls);
    }

    // ─── Per-application formatting exceptions ──────────────────────

    [Fact]
    public async Task An_excepted_application_receives_the_transcript_byte_for_byte()
    {
        // The end of the wiring the store's own tests start. "hello world..."
        // is exactly what the two shipped rules change, so a session that never
        // consulted the list would fail here rather than pass on text it would
        // not have touched anyway.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "hello world...");
        h.Resolver.Next = FocusedApplicationResult.Resolved("Code", 42, "main.cs");
        h.FormattingExceptions.Add("Code");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal("hello world...", result.Text);
    }

    [Fact]
    public async Task The_same_application_is_formatted_once_the_exception_is_gone()
    {
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "hello world...");
        h.Resolver.Next = FocusedApplicationResult.Resolved("Code", 42, "main.cs");

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal("Hello world", result.Text);
    }

    [Fact]
    public async Task The_list_is_read_per_dictation_so_a_change_is_live_on_the_next_hold()
    {
        // The reason it is a delegate. A list captured at construction would
        // leave a user who just added an application still being formatted in
        // it until the next arm toggle rebuilt the session, with nothing on
        // screen saying so.
        using var h = new Harness();
        h.Session.Arm();
        h.Hotkey.NotifyHotkeyMessage();
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "hello world...");
        h.Resolver.Next = FocusedApplicationResult.Resolved("Code", 42, "main.cs");

        Assert.Equal("Hello world", (await h.Session.RunAsync(Harness.Captured(), default)).Text);

        h.FormattingExceptions.Add("Code");
        h.Hotkey.NotifyHotkeyMessage();

        Assert.Equal("hello world...", (await h.Session.RunAsync(Harness.Captured(), default)).Text);
    }

    // ─── N-best and log-probability (CPL Setup 6a-3) ───────────────

    [Fact]
    public async Task A_delivered_dictation_carries_the_transcribers_nbest_and_logprob()
    {
        using var h = new Harness();
        var hypotheses = new[] { "hello world", "hallo world" };
        h.Transcriber.Result = new TranscriptionResult(
            TranscriptionOutcome.Transcribed, "Hello world", NBest: hypotheses, AvgLogprob: -0.21);
        h.Session.Arm();

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(hypotheses, result.NBest);
        Assert.Equal(-0.21, result.AvgLogprob);
    }

    [Fact]
    public async Task A_transcriber_that_reports_neither_leaves_both_null()
    {
        using var h = new Harness();
        h.Session.Arm();

        var result = await h.Session.RunAsync(Harness.Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Null(result.NBest);
        Assert.Null(result.AvgLogprob);
    }
}
