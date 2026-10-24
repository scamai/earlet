using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Services.Tests;

/// <summary>
/// The password-field check just before the paste (the session's
/// <c>pasteFieldCheck</c>): a field that becomes a password field while the
/// words are transcribed is not typed into, nothing reaches the clipboard and
/// no words are carried out of the session. The macOS app refuses there too
/// (its <c>.secureFieldFocused</c> insert failure).
/// </summary>
/// <remarks>
/// Its own file with its own small fakes: DictationSessionTests is at the
/// repository's file-size cap. The inserter is the real
/// <see cref="FocusedTextInsertion"/>, so every outcome asserted here is one
/// the shipped code produces.
/// </remarks>
public sealed class DictationSessionPasteFieldCheckTests
{
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
        public void Complete(MicrophoneTapResult r) => CaptureCompleted?.Invoke(this, r);
        public void Dispose() { }
    }

    private sealed class Transcriber : ITranscriber
    {
        public int Calls { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(MicrophoneCapture capture, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new TranscriptionResult(
                TranscriptionOutcome.Transcribed, "my pin is four four two one", Latency: TimeSpan.FromMilliseconds(120)));
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Resolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Resolve() => FocusedApplicationResult.Resolved("notepad", 7, "Untitled");
    }

    private sealed class Clipboard : IClipboard, IClipboardTextSnapshot
    {
        public string? Held { get; private set; } = "the user's own clipboard";
        public List<string> Writes { get; } = [];

        public bool TrySet(string text)
        {
            Writes.Add(text);
            Held = text;
            return true;
        }

        public bool TryRead(out string? text)
        {
            text = Held;
            return true;
        }
    }

    private sealed class Paste : IPasteKeystroke
    {
        public int Sends { get; private set; }

        public bool TrySend()
        {
            Sends++;
            return true;
        }
    }

    /// <summary>Answers from <see cref="Answers"/> in turn — the release's
    /// check first, then the paste's — and <see cref="Then"/> after.</summary>
    private sealed class Field : ISecureFieldProbe
    {
        public Queue<SecureFieldState> Answers { get; } = new();
        public SecureFieldState Then { get; set; } = SecureFieldState.NotSecure;
        public int Calls { get; private set; }

        public SecureFieldState Probe()
        {
            Calls++;
            return Answers.Count > 0 ? Answers.Dequeue() : Then;
        }
    }

    private sealed class Rig : IDisposable
    {
        public Transcriber Transcriber { get; } = new();
        public Clipboard Clipboard { get; } = new();
        public Paste Paste { get; } = new();
        public Field Field { get; } = new();
        public DictationSession Session { get; }

        /// <param name="check">The session's paste-time runner; null composes
        /// a session without one.</param>
        public Rig(Func<Func<SecureFieldState>, Task<SecureFieldState>>? check)
        {
            var resolver = new Resolver();
            var inserter = new FocusedTextInsertion(
                resolver, Clipboard, Paste, NullLogger<FocusedTextInsertion>.Instance,
                pasteSettleDelay: TimeSpan.Zero);
            Session = new DictationSession(
                new Hotkey(), new Tap(), Transcriber, inserter, NullLogger<DictationSession>.Instance,
                formattingEnabled: () => false, targetResolver: resolver, secureField: Field,
                pasteFieldCheck: check);
            Session.Arm();
        }

        public Task<DictationSessionResult> RunAsync() =>
            Session.RunAsync(
                new MicrophoneTapResult(
                    MicrophoneTapOutcome.Captured,
                    new MicrophoneCapture(new short[16000], 16000, 1, TimeSpan.FromSeconds(1))),
                default);

        public void Dispose() => Session.Dispose();
    }

    private static Task<SecureFieldState> Inline(Func<SecureFieldState> check) => Task.FromResult(check());

    [Fact]
    public async Task A_field_that_becomes_a_password_field_before_the_paste_is_not_typed()
    {
        using var rig = new Rig(Inline);
        rig.Field.Answers.Enqueue(SecureFieldState.NotSecure);
        rig.Field.Answers.Enqueue(SecureFieldState.Secure);

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.SecureField, result.Outcome);
        Assert.True(result.SecureFieldAtPaste);
        // The recogniser ran; its words go nowhere.
        Assert.Equal(1, rig.Transcriber.Calls);
        Assert.Equal(2, rig.Field.Calls);
        Assert.Equal(0, rig.Paste.Sends);
        Assert.Empty(rig.Clipboard.Writes);
        Assert.Equal("the user's own clipboard", rig.Clipboard.Held);
        Assert.False(result.TextWasDelivered);
        Assert.Null(result.Text);
        Assert.Null(result.UndeliveredText);
        Assert.Null(result.Audio);
        Assert.Null(result.NBest);
        // Timed like any other failure after the recogniser.
        Assert.Equal(TimeSpan.FromMilliseconds(120), result.DecodeDuration);
        Assert.NotNull(result.TotalDuration);
        Assert.NotNull(result.InsertDuration);
    }

    [Fact]
    public async Task A_refusal_at_the_paste_times_the_insertion_stage_as_the_check_that_refused_it()
    {
        // The Mac's paste timing on a refused insertion is its gate check
        // (TextInserter's gateCheckMs), so its receipt carries a paste_ms.
        using var rig = new Rig(async check =>
        {
            await Task.Delay(60);
            return check();
        });
        rig.Field.Answers.Enqueue(SecureFieldState.NotSecure);
        rig.Field.Answers.Enqueue(SecureFieldState.Secure);

        var result = await rig.RunAsync();

        Assert.True(result.SecureFieldAtPaste);
        Assert.NotNull(result.InsertDuration);
        Assert.InRange(result.InsertDuration!.Value, TimeSpan.FromMilliseconds(50), result.TotalDuration!.Value);
    }

    [Fact]
    public async Task An_ordinary_field_is_asked_again_at_the_paste_and_typed()
    {
        using var rig = new Rig(Inline);

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.False(result.SecureFieldAtPaste);
        Assert.Equal(2, rig.Field.Calls);
        Assert.Equal(1, rig.Paste.Sends);
    }

    [Fact]
    public async Task Without_a_paste_check_the_field_is_asked_once_as_before()
    {
        // A session composed without a runner: no second question, and a field
        // that turns secure after the release is typed into, as it always was.
        using var rig = new Rig(check: null);
        rig.Field.Answers.Enqueue(SecureFieldState.NotSecure);
        rig.Field.Then = SecureFieldState.Secure;

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(1, rig.Field.Calls);
    }

    [Fact]
    public async Task A_password_field_at_the_release_is_not_asked_again_or_marked_as_the_paste()
    {
        using var rig = new Rig(Inline);
        rig.Field.Answers.Enqueue(SecureFieldState.Secure);

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.SecureField, result.Outcome);
        Assert.False(result.SecureFieldAtPaste);
        Assert.Equal(1, rig.Field.Calls);
        Assert.Equal(0, rig.Transcriber.Calls);
    }

    public static TheoryData<string> Unanswerable => ["unknown", "faulted", "threw"];

    [Theory]
    [MemberData(nameof(Unanswerable))]
    public async Task A_check_at_the_paste_that_cannot_answer_lets_the_words_through(string how)
    {
        // The direction SecureFieldState.Unknown documents: a guard that
        // failed closed would switch dictation off where UIA is slow.
        Func<Func<SecureFieldState>, Task<SecureFieldState>> check = how switch
        {
            "unknown" => _ => Task.FromResult(SecureFieldState.Unknown),
            "faulted" => _ => Task.FromException<SecureFieldState>(new InvalidOperationException("UIA is having a day")),
            _ => _ => throw new InvalidOperationException("the runner is having a day"),
        };
        using var rig = new Rig(check);

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(1, rig.Paste.Sends);
    }

    [Fact]
    public async Task A_cancel_while_the_paste_check_runs_types_nothing()
    {
        // The check is awaited while the session still reads Transcribing, so
        // Escape still cancels; an answer that lands after it must not paste.
        var answer = new TaskCompletionSource<SecureFieldState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new Rig(_ =>
        {
            asked.TrySetResult();
            return answer.Task;
        });
        var raised = new List<DictationOutcome>();
        rig.Session.Completed += (_, r) => raised.Add(r.Outcome);

        var run = rig.RunAsync();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DictationState.Transcribing, rig.Session.State);
        rig.Session.Cancel();
        answer.SetResult(SecureFieldState.NotSecure);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DictationOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, rig.Paste.Sends);
        Assert.Empty(rig.Clipboard.Writes);
        // One result, the cancel's: the late answer raises nothing.
        Assert.Equal([DictationOutcome.Cancelled], raised);
    }
}
