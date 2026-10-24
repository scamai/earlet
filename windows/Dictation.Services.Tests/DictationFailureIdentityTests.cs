using Dictation.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Services.Tests;

/// <summary>
/// A dictation that failed still says which application it failed in, and how
/// long it took to fail.
///
/// <para>
/// Every failure was ANONYMOUS before this. The success path resolves a target
/// for the formatter and the inserter resolves one again for the paste, so an
/// inserted dictation names its app — but a microphone or recogniser failure
/// carried <c>null</c>, and the shell's outcome line printed "(target null)".
/// So "dictation never works in Slack" could not be checked against the log,
/// because the failures in Slack were exactly the ones with no app on them.
/// </para>
/// </summary>
public sealed class DictationFailureIdentityTests
{
    // ─── The smallest graph that can fail ───────────────────────────

    private sealed class Hotkey : IPushToTalkHotkey
    {
        public event EventHandler? Pressed;
        public event EventHandler? Released;

        public bool IsHeld => false;
        public bool IsRegistered { get; private set; }

        public bool Start() => IsRegistered = true;

        public void NotifyHotkeyMessage() => Pressed?.Invoke(this, EventArgs.Empty);

        public void PollForRelease() => Released?.Invoke(this, EventArgs.Empty);

        public void Dispose() { }
    }

    private sealed class Tap : IMicrophoneTap
    {
        public event EventHandler<MicrophoneTapResult>? CaptureCompleted;

        public bool IsCapturing => false;

        public void Raise(MicrophoneTapResult r) => CaptureCompleted?.Invoke(this, r);

        public void Dispose() { }
    }

    private sealed class Transcriber : ITranscriber
    {
        public TranscriptionResult Result { get; set; } =
            new(TranscriptionOutcome.Transcribed, "hello");

        public bool? RunsOnNpu => null;

        public Task<TranscriptionResult> TranscribeAsync(
            MicrophoneCapture capture, CancellationToken ct) => Task.FromResult(Result);

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Inserter : IFocusedTextInserter
    {
        public Task<FocusedInsertionResult> InsertAsync(string text, CancellationToken ct = default) =>
            throw new InvalidOperationException(
                "no failure test in this file may reach the inserter");
    }

    private sealed class Resolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Next { get; set; } =
            FocusedApplicationResult.Resolved("slack", 4242, "Slack | general");

        public bool Throws { get; set; }

        public FocusedApplicationResult Resolve() =>
            Throws ? throw new InvalidOperationException("UIA is not answering") : Next;
    }

    private sealed class CapturingLogger : ILogger<DictationSession>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));

        public string Find(string fragment) =>
            Lines.FirstOrDefault(l => l.Message.Contains(fragment, StringComparison.Ordinal)).Message
            ?? throw new Xunit.Sdk.XunitException(
                $"No line contained \"{fragment}\". Lines:\n  "
                + string.Join("\n  ", Lines.Select(l => $"[{l.Level}] {l.Message}")));
    }

    private sealed class Rig : IDisposable
    {
        public Transcriber Recognizer { get; } = new();
        public Resolver Windows { get; } = new();
        public CapturingLogger Log { get; } = new();
        public DictationSession Session { get; }

        public Rig(bool withResolver = true)
        {
            Session = new DictationSession(
                new Hotkey(), new Tap(), Recognizer, new Inserter(), Log,
                formattingEnabled: null,
                targetResolver: withResolver ? Windows : null);
            Session.Arm();
        }

        public void Dispose() => Session.Dispose();

        public static MicrophoneTapResult Captured(double seconds = 4.0) =>
            new(MicrophoneTapOutcome.Captured,
                new MicrophoneCapture(
                    new short[(int)(16000 * seconds)], 16000, 1, TimeSpan.FromSeconds(seconds)));
    }

    // ─── The failures name the window ───────────────────────────────

    [Theory]
    [InlineData(MicrophoneTapOutcome.NoAudio, DictationOutcome.NoAudio)]
    [InlineData(MicrophoneTapOutcome.DeviceUnavailable, DictationOutcome.MicrophoneUnavailable)]
    [InlineData(MicrophoneTapOutcome.TooShort, DictationOutcome.HoldTooShort)]
    public async Task A_microphone_failure_still_names_the_application_it_happened_in(
        MicrophoneTapOutcome capture, DictationOutcome expected)
    {
        using var rig = new Rig();

        var result = await rig.Session.RunAsync(new MicrophoneTapResult(capture, null), default);

        Assert.Equal(expected, result.Outcome);

        // The whole point: the shell's outcome line reads result.Target, and
        // printed "(target null)" for every one of these.
        Assert.NotNull(result.Target);
        Assert.Equal("slack", result.Target!.Value.ProcessName);
    }

    [Theory]
    [InlineData(TranscriptionOutcome.NoSpeech, DictationOutcome.NoSpeech)]
    [InlineData(TranscriptionOutcome.RecognizerUnavailable, DictationOutcome.RecognizerUnavailable)]
    [InlineData(TranscriptionOutcome.RecognizerFailed, DictationOutcome.RecognizerFailed)]
    public async Task A_recogniser_failure_still_names_the_application_it_happened_in(
        TranscriptionOutcome transcription, DictationOutcome expected)
    {
        using var rig = new Rig();
        rig.Recognizer.Result = new TranscriptionResult(transcription, null);

        var result = await rig.Session.RunAsync(Rig.Captured(), default);

        Assert.Equal(expected, result.Outcome);
        Assert.NotNull(result.Target);
        Assert.Equal("slack", result.Target!.Value.ProcessName);
    }

    [Fact]
    public async Task A_hold_that_died_at_the_recogniser_reports_how_long_it_took_to_die()
    {
        // "The recogniser took 15 seconds to time out" and "it refused in 4 ms"
        // arrived from the shell as the same one line. The successful twin of
        // this stage line has been printing since it was written; this path
        // printed nothing at all.
        using var rig = new Rig();
        rig.Recognizer.Result = new TranscriptionResult(
            TranscriptionOutcome.NoSpeech, null, TimeSpan.FromMilliseconds(437));

        await rig.Session.RunAsync(Rig.Captured(seconds: 4.0), default);

        var line = rig.Log.Find("Dictation ended at the recogniser");
        Assert.Contains("NoSpeech", line, StringComparison.Ordinal);
        Assert.Contains("audio 4.0s", line, StringComparison.Ordinal);
        Assert.Contains("from slack", line, StringComparison.Ordinal);
        Assert.Contains("recognizer 437 ms", line, StringComparison.Ordinal);
        Assert.Contains("release-to-done", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resolver_that_throws_costs_the_name_and_not_the_dictation()
    {
        // Same posture as the two probes beside it: a seam that cannot answer
        // must never change the outcome the user is told.
        using var rig = new Rig();
        rig.Windows.Throws = true;
        rig.Recognizer.Result = new TranscriptionResult(TranscriptionOutcome.NoSpeech, null);

        var result = await rig.Session.RunAsync(Rig.Captured(), default);

        Assert.Equal(DictationOutcome.NoSpeech, result.Outcome);
        Assert.Null(result.Target);
        Assert.Contains(
            "an unidentified window",
            rig.Log.Find("Dictation ended at the recogniser"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_with_no_resolver_reports_the_failure_without_inventing_a_window()
    {
        using var rig = new Rig(withResolver: false);

        var result = await rig.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.NoAudio, null), default);

        Assert.Equal(DictationOutcome.NoAudio, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public async Task A_pre_recognizer_failure_still_has_a_terminal_operation_record()
    {
        using var rig = new Rig();

        var result = await rig.Session.RunAsync(
            new MicrophoneTapResult(
                MicrophoneTapOutcome.NoAudio,
                Audio: null,
                OperationId: "hold-no-audio"),
            default);

        Assert.Equal(DictationOutcome.NoAudio, result.Outcome);
        var line = rig.Log.Find("Dictation completed:");
        Assert.Contains("operation=hold-no-audio", line, StringComparison.Ordinal);
        Assert.Contains("outcome=NoAudio", line, StringComparison.Ordinal);
        Assert.Contains("target=slack", line, StringComparison.Ordinal);
        Assert.Contains("asr_invoked=False", line, StringComparison.Ordinal);
        Assert.Contains("chars=0", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_desktop_with_no_foreground_window_is_reported_as_none_rather_than_as_one()
    {
        // NoFocusedWindow is a real answer, not a resolved application, and
        // carrying it as a Target would put an empty process name on the
        // outcome line as though a window had been identified.
        using var rig = new Rig();
        rig.Windows.Next = FocusedApplicationResult.NoFocusedWindow;

        var result = await rig.Session.RunAsync(
            new MicrophoneTapResult(MicrophoneTapOutcome.NoAudio, null), default);

        Assert.Null(result.Target);
    }
}
