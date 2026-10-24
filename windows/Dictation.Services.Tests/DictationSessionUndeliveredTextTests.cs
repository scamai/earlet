using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Services.Tests;

/// <summary>
/// <see cref="DictationSessionResult.UndeliveredText"/>: the words a refused
/// paste did not deliver, carried so the host can still hand them over — and
/// on no other outcome, so a password field, a recogniser failure or a
/// delivered dictation never leaves text behind in it.
/// </summary>
/// <remarks>
/// Its own file with its own small fakes: DictationSessionTests is at the
/// repository's file-size cap. The inserter is the real
/// <see cref="FocusedTextInsertion"/>, so every outcome asserted here is one the
/// shipped code produces.
/// </remarks>
public sealed class DictationSessionUndeliveredTextTests
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
        public TranscriptionResult Result { get; set; } = new(TranscriptionOutcome.Transcribed, "Meet me at noon.");
        public int Calls { get; private set; }

        public Task<TranscriptionResult> TranscribeAsync(MicrophoneCapture capture, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Resolver : IFocusedApplicationResolver
    {
        public FocusedApplicationResult Next { get; set; } =
            FocusedApplicationResult.Resolved("notepad", 7, "Untitled");

        public FocusedApplicationResult Resolve() => Next;
    }

    private sealed class Clipboard : IClipboard, IClipboardTextSnapshot
    {
        public string? Held { get; private set; } = "the user's own clipboard";
        public bool FailRestore { get; set; }
        private int _sets;

        public bool TrySet(string text)
        {
            _sets++;
            if (FailRestore && _sets > 1)
            {
                return false;
            }

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
        public bool Succeeds { get; set; } = true;
        public bool TrySend() => Succeeds;
    }

    private sealed class SecureField : ISecureFieldProbe
    {
        public SecureFieldState Next { get; set; } = SecureFieldState.NotSecure;
        public SecureFieldState Probe() => Next;
    }

    private sealed class Rig : IDisposable
    {
        public Transcriber Transcriber { get; } = new();
        public Resolver Resolver { get; } = new();
        public Clipboard Clipboard { get; } = new();
        public Paste Paste { get; } = new();
        public SecureField Field { get; } = new();
        public DictationSession Session { get; }

        public Rig()
        {
            var inserter = new FocusedTextInsertion(
                Resolver, Clipboard, Paste, NullLogger<FocusedTextInsertion>.Instance,
                pasteSettleDelay: TimeSpan.Zero);
            Session = new DictationSession(
                new Hotkey(), new Tap(), Transcriber, inserter, NullLogger<DictationSession>.Instance,
                formattingEnabled: () => false, targetResolver: Resolver, secureField: Field);
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

    [Fact]
    public async Task A_refused_paste_carries_the_words_it_did_not_deliver()
    {
        using var rig = new Rig();
        rig.Paste.Succeeds = false;

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.InsertionRefused, result.Outcome);
        Assert.Equal("Meet me at noon.", result.UndeliveredText);
        // Text keeps its meaning: what was inserted, which is nothing.
        Assert.Null(result.Text);
        // And the inserter put the user's clipboard back; offering the words
        // again is the host's decision, not a side effect here.
        Assert.Equal("the user's own clipboard", rig.Clipboard.Held);
    }

    [Fact]
    public async Task A_hold_with_no_window_to_type_into_carries_the_words()
    {
        using var rig = new Rig();
        rig.Resolver.Next = FocusedApplicationResult.NoFocusedWindow;

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.NoFocusedWindow, result.Outcome);
        Assert.Equal("Meet me at noon.", result.UndeliveredText);
    }

    [Fact]
    public async Task An_unidentified_window_is_refused_and_the_words_are_carried()
    {
        // The elevated-editor case: a window takes keystrokes but its process
        // cannot be named, so the inserter refuses to type blind.
        using var rig = new Rig();
        rig.Resolver.Next = FocusedApplicationResult.Unresolvable("Administrator: Editor");

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.InsertionRefused, result.Outcome);
        Assert.Equal("Meet me at noon.", result.UndeliveredText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_delivered_dictation_carries_nothing_undelivered(bool loseTheClipboard)
    {
        using var rig = new Rig();
        rig.Clipboard.FailRestore = loseTheClipboard;

        var result = await rig.RunAsync();

        Assert.True(result.TextWasDelivered);
        Assert.Null(result.UndeliveredText);
    }

    [Fact]
    public async Task A_password_field_carries_no_words_at_all()
    {
        using var rig = new Rig();
        rig.Field.Next = SecureFieldState.Secure;
        rig.Paste.Succeeds = false;

        var result = await rig.RunAsync();

        Assert.Equal(DictationOutcome.SecureField, result.Outcome);
        Assert.Null(result.UndeliveredText);
        Assert.Equal(0, rig.Transcriber.Calls);
    }

    [Theory]
    [InlineData(TranscriptionOutcome.RecognizerUnavailable)]
    [InlineData(TranscriptionOutcome.RecognizerFailed)]
    [InlineData(TranscriptionOutcome.NoSpeech)]
    public async Task A_hold_that_never_reached_the_cursor_carries_nothing(TranscriptionOutcome outcome)
    {
        using var rig = new Rig();
        rig.Transcriber.Result = new TranscriptionResult(outcome, null);

        var result = await rig.RunAsync();

        Assert.Null(result.UndeliveredText);
        Assert.Null(result.Text);
    }
}
