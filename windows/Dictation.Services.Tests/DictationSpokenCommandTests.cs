using Dictation.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dictation.Services.Tests;

/// <summary>
/// The session's side of the routing seam: a hold's words go to an
/// <see cref="ITranscriptRouter"/>, when one is composed, before the text rules
/// and before the cursor, and the router decides whether they are typed.
///
/// <para>
/// Dictation composes no router, so every hold is typed. What stays is the
/// branch in <c>DictationSession</c>, and these tests pin it with a scripted
/// router. Half of them assert the SAFE direction: a router that fails, hands
/// the words back, or is overtaken by a cancel must cost nothing but the
/// command, never the sentence, and never type into the wrong window.
/// </para>
/// </summary>
public sealed class DictationSpokenCommandTests
{
    // ─── The smallest graph that can reach the branch ───────────────

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

    /// <summary>Records rather than throws — several tests need the insert to happen.</summary>
    private sealed class Inserter : IFocusedTextInserter
    {
        public List<string> Inserted { get; } = new();

        public Task<FocusedInsertionResult> InsertAsync(string text, CancellationToken ct = default)
        {
            Inserted.Add(text);
            return Task.FromResult(FocusedInsertionResult.Succeeded(
                FocusedApplicationResult.Resolved("notepad", 1234, "Untitled - Notepad")));
        }
    }

    /// <summary>
    /// A scripted <see cref="ITranscriptRouter"/>: it records every transcript
    /// it is asked about and answers with <see cref="Answer"/>.
    /// </summary>
    /// <remarks>
    /// This was a <c>Func&lt;string, Task&lt;bool&gt;&gt;</c> sink until the
    /// 2026-08-21 merge: the same feature had been built twice, an hour apart,
    /// and the router survived because it can say one thing the boolean cannot
    /// — addressed, and nothing matched (<see cref="TranscriptRouting.NotMatched"/>).
    /// </remarks>
    private sealed class Sink : ITranscriptRouter
    {
        public List<string> Saw { get; } = new();

        /// <summary>What the router decides; by default, type the words as heard.</summary>
        public Func<string, TranscriptRouting> Answer { get; set; } = TranscriptRouting.Dictate;

        public Exception? Throws { get; set; }

        /// <summary>
        /// When set, the router answers with this task instead of
        /// <see cref="Answer"/>, so a test can act while it is still deciding.
        /// </summary>
        public Task<TranscriptRouting>? Pending { get; set; }

        /// <summary>Completes the moment the session asks.</summary>
        public TaskCompletionSource Asked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TranscriptRouting> RouteAsync(
            string transcript, CancellationToken cancellationToken = default)
        {
            Saw.Add(transcript);
            Asked.TrySetResult();

            if (Throws is not null)
            {
                throw Throws;
            }

            return Pending ?? Task.FromResult(Answer(transcript));
        }
    }

    private static (DictationSession Session, Inserter Typed, Sink Commands) Rig(
        string heard, bool withSink = true)
    {
        var typed = new Inserter();
        var sink = new Sink();
        var session = new DictationSession(
            new Hotkey(),
            new Tap(),
            new Transcriber { Result = new TranscriptionResult(TranscriptionOutcome.Transcribed, heard) },
            typed,
            NullLogger<DictationSession>.Instance,
            router: withSink ? sink : null);
        return (session, typed, sink);
    }

    private static MicrophoneTapResult Captured(double seconds = 4.0) =>
        new(MicrophoneTapOutcome.Captured,
            new MicrophoneCapture(
                new short[(int)(16000 * seconds)], 16000, 1, TimeSpan.FromSeconds(seconds)));

    // ─── A hold the router takes ────────────────────────────────────

    [Fact]
    public async Task A_hold_the_router_ran_as_a_command_types_nothing()
    {
        var (session, typed, commands) = Rig("join the meeting");
        commands.Answer = _ => TranscriptRouting.RanWorkflow;

        var result = await session.RunAsync(Captured(), default);

        Assert.Equal(DictationOutcome.RanWorkflow, result.Outcome);
        Assert.Empty(typed.Inserted);

        // Not a delivery, so Recent shows no row. Reporting Inserted here
        // would put a character count on a result nobody typed.
        Assert.False(result.TextWasDelivered);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task A_hold_the_router_was_addressed_by_but_could_not_match_is_typed_nowhere()
    {
        // Somebody talking to a command surface is not writing: typing the
        // sentence into their document is a worse failure than doing nothing
        // and saying so, which the indicator's "Heard you, but nothing
        // matched" does.
        var (session, typed, commands) = Rig("join the meting");
        commands.Answer = _ => TranscriptRouting.NotMatched;

        var result = await session.RunAsync(Captured(), default);

        Assert.Equal(DictationOutcome.WorkflowNotMatched, result.Outcome);
        Assert.Empty(typed.Inserted);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task The_router_sees_the_words_as_heard_before_the_text_rules()
    {
        // Routing runs before the formatter: sentence-casing a command would be
        // work on a string nobody reads, and would make the text the router
        // decides on differ from what was said. The words it hands back still
        // get the ordinary text rules on the way to the cursor.
        var (session, typed, commands) = Rig("send this to maya when you get a chance");

        var result = await session.RunAsync(Captured(), default);

        Assert.Equal(new[] { "send this to maya when you get a chance" }, commands.Saw);
        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(new[] { "Send this to maya when you get a chance" }, typed.Inserted);
    }

    [Fact]
    public async Task The_router_decides_which_words_are_typed()
    {
        // TranscriptRouting.TextToInsert is what lands, not the transcript: the
        // router is the seam that may take a leading word out of a sentence
        // it decided was dictation after all.
        var (session, typed, commands) = Rig("hey there send the notes");
        commands.Answer = _ => TranscriptRouting.Dictate("send the notes");

        await session.RunAsync(Captured(), default);

        Assert.Equal(new[] { "Send the notes" }, typed.Inserted);
    }

    // ─── The safe direction ─────────────────────────────────────────

    [Fact]
    public async Task A_hold_the_router_hands_back_is_typed_as_said()
    {
        // If a router that declined ate the utterance, the user would lose a
        // sentence with no way to get it back.
        var (session, typed, commands) = Rig("Remind me to buy milk");

        var result = await session.RunAsync(Captured(), default);

        Assert.Single(commands.Saw);
        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(new[] { "Remind me to buy milk" }, typed.Inserted);
    }

    [Fact]
    public async Task A_command_surface_that_throws_still_lets_the_words_land()
    {
        var (session, typed, commands) = Rig("Join the meeting");
        commands.Throws = new InvalidOperationException("the command surface is down");

        var result = await session.RunAsync(Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(new[] { "Join the meeting" }, typed.Inserted);
    }

    [Fact]
    public async Task A_router_that_is_cancelled_types_nothing()
    {
        // Cancel means nothing is inserted, however far the hold got: the
        // window this was meant for is not necessarily the one in front now.
        var (session, typed, commands) = Rig("Join the meeting");
        commands.Throws = new OperationCanceledException();

        var result = await session.RunAsync(Captured(), default);

        Assert.Equal(DictationOutcome.Cancelled, result.Outcome);
        Assert.Empty(typed.Inserted);
    }

    [Fact]
    public async Task A_cancel_while_the_router_decides_types_nothing_even_if_it_says_type_it()
    {
        // The router need not observe the token — finishing without it is
        // legal, and what a fast router looks like — so it can hand words back
        // for a window the user has since left. The session's generation check
        // after the await is what drops them. Only "type it" is asserted:
        // an answer that types nothing could not show the check missing.
        var (session, typed, commands) = Rig("Join the meeting");
        var decision = new TaskCompletionSource<TranscriptRouting>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Pending = decision.Task;

        var run = session.RunAsync(Captured(), default);
        await commands.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        session.Cancel();
        decision.SetResult(TranscriptRouting.Dictate("Join the meeting"));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DictationOutcome.Cancelled, result.Outcome);
        Assert.Empty(typed.Inserted);
    }

    [Fact]
    public async Task A_build_with_no_command_surface_types_every_transcript()
    {
        // The shipping default: Dictation composes no router, and that keeps
        // the branch inert without the session having to read a flag.
        var (session, typed, commands) = Rig("Join the meeting", withSink: false);

        var result = await session.RunAsync(Captured(), default);

        Assert.Equal(DictationOutcome.Inserted, result.Outcome);
        Assert.Equal(new[] { "Join the meeting" }, typed.Inserted);
        Assert.Empty(commands.Saw);
    }
}
