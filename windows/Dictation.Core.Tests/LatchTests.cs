using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

// The Mac's macos/Tests/DictationAppCoreTests/LatchTests.swift, ported one
// scenario for one, method names kept (in C# case). The two pure ones
// (pairsLatchAndUnlatch, loneTapAndOtherSide) and the stop-key table
// (stopKeysAreConsumed) are in Dictation.Services.Tests'
// HoldKeyHandsFreeTests, where the gesture lives.
//
// The real chain runs: the hook's HoldKeyState (FakeTrigger.UseGesture) fed
// native-shaped key events, the controller, the push-to-talk controller, the
// microphone tap, the session, the worker over its real framing, and the
// paste. Only the Win32 edges are fake.
//
// Where the Mac waits for "capture is up" (its microphone warms up after the
// press), Windows waits for the press-time password-field check: the
// microphone opens only once it answers. Those scenarios hold the check open
// with HandsFree.Checks instead of holding a warm-up.

/// <summary>The rig for a hands-free test: every trigger runs the real
/// gesture, and the hook's clock moves with the controller's.</summary>
internal sealed class HandsFree : IDisposable
{
    public const int Trigger = (int)DictationHotkeys.VkRightControl;
    public const int Down = HoldKeyState.WmKeyDown;
    public const int Up = HoldKeyState.WmKeyUp;

    public HandsFree(
        Func<DictationControllerOptions, DictationControllerOptions>? options = null,
        bool checks = false,
        Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
    {
        Rig = new Rig(
            options: o =>
            {
                o = options?.Invoke(o) ?? o;
                return checks ? o with { RunFieldCheck = Checks.Run } : o;
            },
            logs: logs,
            beforeStart: rig => rig.Platform.Gestures = true);
    }

    public Rig Rig { get; }

    public FieldChecks Checks { get; } = new();

    public FakeTrigger Key => Rig.Platform.Trigger;

    public int Transcribes => Rig.Platform.Worker.Ops().Count(op => op == "transcribe");

    public int Pastes => Rig.Platform.Paste.Count;

    public bool Listening => Rig.Platform.Microphone.Open;

    public bool Latched => Key.IsLatched;

    public string Status => Rig.View.Last.Status;

    public bool Inserted => Status.StartsWith("Text inserted", StringComparison.Ordinal);

    public async Task EnableAsync() => await Rig.EnableAsync();

    /// <summary>Time passes for the hook and the controller alike.</summary>
    public void Advance(int milliseconds)
    {
        Key.GestureTime += milliseconds;
        Rig.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
    }

    /// <summary>One key event through the hook, then the next poll.</summary>
    public HoldKeyTransition Event(int virtualKey, int message)
    {
        var transition = Key.KeyEvent(virtualKey, message);
        Rig.Controller.Tick();
        Rig.Dispatcher.Drain();
        return transition;
    }

    public void Press() => Event(Trigger, Down);

    public void Release() => Event(Trigger, Up);

    /// <summary>A key typed: down, then up.</summary>
    public void Type(int virtualKey)
    {
        Event(virtualKey, Down);
        Event(virtualKey, Up);
    }

    /// <summary>A quick tap of the hold key, with speech while the
    /// microphone is open.</summary>
    public void Tap()
    {
        Press();
        Speak();
        Advance(60);
        Release();
    }

    /// <summary>Two taps 160 ms apart, press to press.</summary>
    public void DoubleTap()
    {
        Tap();
        Advance(100);
        Tap();
    }

    public void Speak()
    {
        if (Listening)
        {
            Rig.Platform.Microphone.Speak();
        }
    }

    /// <summary>Time passes with nothing pressed, polled as the app does.</summary>
    public void Wait(int milliseconds)
    {
        Advance(milliseconds);
        Rig.Controller.Tick();
        Rig.Dispatcher.Drain();
    }

    public void DrainUntil(Func<bool> done) => Rig.DrainUntil(done);

    /// <summary>Drain until the dictation is over, however it ended: the
    /// window no longer offers Cancel, and nothing is being typed.</summary>
    public void DrainUntilEnded() =>
        Rig.DrainUntil(() => !Rig.View.Last.CanCancel && Status != DictationCopy.Inserting);

    /// <summary>The Mac's <c>latchesAndFinishes</c>: a double-tap latches,
    /// nothing is transcribed for it past the window, and the habitual
    /// double-tap stop inserts exactly one more dictation.</summary>
    public void LatchesAndFinishes(int transcribedBefore)
    {
        var pastes = Pastes;
        DoubleTap();
        Assert.True(Latched && Listening, $"the double-tap latched ({Status})");
        Wait(500);
        Assert.True(Latched && Listening, "still dictating past the window");
        Assert.Equal(transcribedBefore, Transcribes);

        DoubleTap();
        DrainUntil(() => Inserted && Pastes == pastes + 1);
        Assert.False(Latched);
        Assert.Equal(transcribedBefore + 1, Transcribes);
    }

    public void Dispose() => Rig.Dispose();
}

/// <summary>The press-time password-field check, answered when the test
/// says: the Windows counterpart of a microphone still warming up.</summary>
internal sealed class FieldChecks
{
    private readonly List<TaskCompletionSource<SecureFieldState>> _pending = [];

    public Task<SecureFieldState> Run(Func<SecureFieldState> check)
    {
        var answer = new TaskCompletionSource<SecureFieldState>();
        lock (_pending)
        {
            _pending.Add(answer);
        }

        return answer.Task;
    }

    /// <summary>Answer every check still out.</summary>
    public void Answer(SecureFieldState state = SecureFieldState.NotSecure)
    {
        TaskCompletionSource<SecureFieldState>[] pending;
        lock (_pending)
        {
            pending = [.. _pending];
            _pending.Clear();
        }

        foreach (var answer in pending)
        {
            answer.TrySetResult(state);
        }
    }
}

/// <summary>Mac suite "Double-tap latches dictation on the hold key".</summary>
public sealed class LatchTests
{
    /// <summary>Mac <c>latchThenUnlatch</c>: "a double-tap latches, and the
    /// next double-tap stops and transcribes".</summary>
    [Fact]
    public async Task LatchThenUnlatch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        Assert.True(hands.Latched && hands.Listening);
        Assert.Equal("Dictating. Press Right Ctrl, Space or Enter to stop.", hands.Status);
        Assert.Equal(1, hands.Rig.Platform.Microphone.Starts);

        hands.DoubleTap();
        hands.DrainUntil(() => hands.Inserted);
        Assert.False(hands.Latched);
        Assert.Equal(1, hands.Pastes);
        Assert.Equal(1, hands.Transcribes);   // the pair's first tap never transcribed on its own
        Assert.Equal("the user's clipboard", hands.Rig.Platform.Clipboard.Text);
    }

    /// <summary>Mac <c>latchHitsTheCeiling</c>: "a latch is bounded by the
    /// maximum hold".</summary>
    [Fact]
    public async Task LatchHitsTheCeiling()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        Assert.True(hands.Latched);
        hands.Wait(300_000);
        hands.DrainUntil(() => hands.Inserted);

        Assert.False(hands.Latched);
        Assert.Equal("Text inserted. A recording stops at five minutes; hold Right Ctrl again to continue.", hands.Status);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>Mac <c>escapeCancelsALatch</c>: "Escape during a latch aborts
    /// without inserting".</summary>
    [Fact]
    public async Task EscapeCancelsALatch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        Assert.True(hands.Latched);
        hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
        hands.DrainUntil(() => hands.Status.Contains("Cancelled", StringComparison.Ordinal));

        Assert.False(hands.Listening);
        Assert.False(hands.Latched);
        Assert.Equal(0, hands.Pastes);
        Assert.Equal(0, hands.Transcribes);
    }

    /// <summary>Mac <c>singleTapDoesNotLatch</c>: "a single quick
    /// press-release never latches, and still transcribes".</summary>
    [Fact]
    public async Task SingleTapDoesNotLatch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.Tap();
        Assert.False(hands.Latched);
        Assert.True(hands.Listening, "the tap waits out the window");

        hands.Wait(HoldKeyState.HandsFreeWindowMs);
        hands.DrainUntil(() => hands.Inserted);
        Assert.False(hands.Latched);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>Mac <c>parkedTapNeverTranscribes</c>: "the pair's first tap
    /// does not transcribe when the second completes the latch".</summary>
    [Fact]
    public async Task ParkedTapNeverTranscribes()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        Assert.True(hands.Latched && hands.Listening);
        hands.Wait(600);
        Assert.True(hands.Latched && hands.Listening, "still dictating past the window");
        Assert.Equal(0, hands.Transcribes);

        hands.DoubleTap();
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(1, hands.Pastes);
    }
}

/// <summary>Mac suite "Typing during a latch or a parked tap keeps the
/// recording": once the hold key is up, of the keys only Escape cancels.</summary>
public sealed class LatchKeysTests
{
    private const int VkC = 0x43;
    private const int VkTab = 0x09;
    private const int VkLeftAlt = 0xA4;
    private const int VkLeftShift = 0xA0;

    /// <summary>Mac <c>keysDuringALatch</c>: "a letter and ⌘-Tab during a
    /// latch keep recording" — Alt+Tab on Windows.</summary>
    [Fact]
    public async Task KeysDuringALatch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Type(VkC);
        hands.Event(VkLeftAlt, HoldKeyState.WmSysKeyDown);
        hands.Type(VkTab);
        hands.Event(VkLeftAlt, HoldKeyState.WmSysKeyUp);

        Assert.True(hands.Latched && hands.Listening, hands.Status);
        Assert.DoesNotContain("shortcut", hands.Status, StringComparison.Ordinal);

        hands.DoubleTap();
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>Mac <c>shiftDuringALatch</c>: "Shift during a latch keeps
    /// recording".</summary>
    [Fact]
    public async Task ShiftDuringALatch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Type(VkLeftShift);
        Assert.True(hands.Latched && hands.Listening, hands.Status);

        hands.DoubleTap();
        hands.DrainUntil(() => hands.Inserted);
    }

    /// <summary>Mac <c>escapeStillCancels</c>: "Escape still cancels a
    /// latch", and the stop keys type again once it is over.</summary>
    [Fact]
    public async Task EscapeStillCancels()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Type(VkC);
        hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
        hands.DrainUntil(() => hands.Status.Contains("Cancelled", StringComparison.Ordinal));

        Assert.False(hands.Listening);
        Assert.Equal(0, hands.Pastes);
        Assert.Equal(HoldKeyTransition.None, hands.Event(HoldKeyState.VkSpace, HandsFree.Down));
    }

    /// <summary>Escape on a held press, the key still down: the key's
    /// autorepeats that follow open no new dictation, so nothing said before
    /// the release is transcribed or pasted.</summary>
    [Fact]
    public async Task EscapeOnAHeldKeyStaysCancelledThroughItsAutorepeats()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.Press();
        hands.Speak();
        hands.Advance(600);
        hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
        hands.DrainUntil(() => hands.Status.Contains("Cancelled", StringComparison.Ordinal));
        var cancelled = hands.Status;

        for (var repeat = 0; repeat < 5; repeat++)
        {
            hands.Advance(33);
            hands.Press();   // an autorepeat of the key still held
            hands.Speak();
        }

        hands.Advance(150);
        hands.Release();
        hands.Wait(HoldKeyState.HandsFreeWindowMs + 50);
        hands.Rig.Dispatcher.Drain();

        Assert.Equal(1, hands.Rig.Platform.Microphone.Starts);
        Assert.False(hands.Listening);
        Assert.Equal(0, hands.Transcribes);
        Assert.Equal(0, hands.Pastes);
        Assert.Equal(cancelled, hands.Status);
    }

    /// <summary>Mac <c>keysDuringAParkedTap</c>: "typing right after a quick
    /// tap, while it waits out the window, keeps it".</summary>
    [Fact]
    public async Task KeysDuringAParkedTap()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.Tap();
        hands.Type(VkC);
        hands.Type(VkLeftShift);
        hands.Wait(HoldKeyState.HandsFreeWindowMs);
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>Mac <c>keysBeforeCaptureIsUp</c>: "typing after a release
    /// that came before capture was up keeps it" — here, before the
    /// password-field check answered.</summary>
    [Fact]
    public async Task KeysBeforeCaptureIsUp()
    {
        using var hands = new HandsFree(checks: true);
        await hands.EnableAsync();

        hands.Press();
        hands.Advance(60);
        hands.Release();
        hands.Type(VkC);
        Assert.False(hands.Listening, "the check is still out");

        hands.Checks.Answer();
        hands.Wait(30);
        Assert.True(hands.Listening);
        hands.Speak();
        hands.Wait(HoldKeyState.HandsFreeWindowMs);
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>Mac <c>latchMadeByTheSecondPress</c>: "a latch whose second
    /// tap started the hold also stops treating keys as shortcuts". The first
    /// tap is refused while the last dictation finishes; the second, inside
    /// the window, starts a hold and completes the pair.</summary>
    [Fact]
    public async Task LatchMadeByTheSecondPress()
    {
        using var hands = new HandsFree();
        using var decode = new ManualResetEventSlim();
        hands.Rig.Platform.Worker.HoldTranscribe = decode;
        await hands.EnableAsync();

        hands.Press();
        hands.Speak();
        hands.Advance(400);
        hands.Release();
        hands.DrainUntil(() => hands.Status == DictationCopy.Transcribing);
        hands.Advance(400);

        hands.Tap();
        Assert.StartsWith("Still typing the last dictation", hands.Status, StringComparison.Ordinal);
        decode.Set();
        hands.DrainUntil(() => hands.Inserted);
        hands.Rig.Platform.Worker.HoldTranscribe = null;

        hands.Advance(100);
        hands.Tap();
        Assert.True(hands.Latched && hands.Listening, hands.Status);
        hands.Type(VkC);
        Assert.True(hands.Latched && hands.Listening, hands.Status);

        hands.DoubleTap();
        hands.DrainUntil(() => hands.Inserted && hands.Pastes == 2);
        Assert.Equal(2, hands.Transcribes);
    }

    /// <summary>Mac <c>heldKeyStillMakesAShortcut</c>: "while the key is
    /// held, a letter is still a shortcut".</summary>
    [Fact]
    public async Task HeldKeyStillMakesAShortcut()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.Press();
        Assert.True(hands.Listening);
        hands.Event(VkC, HandsFree.Down);
        hands.DrainUntil(() => hands.Status.Contains("shortcut", StringComparison.Ordinal));
        Assert.False(hands.Listening);
        hands.Release();
        Assert.False(hands.Latched);
    }
}

/// <summary>Mac suite "A double-tap through the real detector latches
/// whatever ended the last one". Here the detector is always the real one.</summary>
public sealed class DetectorLatchTests
{
    /// <summary>Mac <c>relatchAfterEscape</c>.</summary>
    [Fact]
    public async Task RelatchAfterEscape()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Event(HoldKeyState.VkEscape, HandsFree.Down);
        hands.DrainUntil(() => hands.Status.Contains("Cancelled", StringComparison.Ordinal));
        hands.Wait(400);

        hands.LatchesAndFinishes(transcribedBefore: 0);
        Assert.Equal(1, hands.Pastes);
    }

    /// <summary>Mac <c>relatchAfterTheCeiling</c>.</summary>
    [Fact]
    public async Task RelatchAfterTheCeiling()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Wait(300_000);
        hands.DrainUntil(() => hands.Inserted);
        Assert.False(hands.Latched);
        Assert.Equal(1, hands.Transcribes);
        hands.Wait(400);

        hands.LatchesAndFinishes(transcribedBefore: 1);
    }

    /// <summary>Mac <c>relatchAfterARefusal</c>.</summary>
    [Fact]
    public async Task RelatchAfterARefusal()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();
        hands.Rig.Platform.SecureField.Next = SecureFieldState.Secure;

        hands.DoubleTap();
        Assert.Contains("password fields", hands.Status, StringComparison.Ordinal);
        Assert.False(hands.Latched);
        Assert.False(hands.Listening);
        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);

        hands.Rig.Platform.SecureField.Next = SecureFieldState.NotSecure;
        hands.Wait(400);
        hands.LatchesAndFinishes(transcribedBefore: 0);
    }

    /// <summary>As on the Mac, where the detector does not care why the shell
    /// refused a press: a first tap refused in a password field, then a
    /// second inside the window in an ordinary field, starts a hold and
    /// latches it.</summary>
    [Fact]
    public async Task A_password_refusal_of_the_first_tap_lets_the_second_start_and_latch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.Rig.Platform.SecureField.Next = SecureFieldState.Secure;
        hands.Tap();
        Assert.Contains("password fields", hands.Status, StringComparison.Ordinal);
        hands.Rig.Platform.SecureField.Next = SecureFieldState.NotSecure;
        hands.Advance(100);
        hands.Tap();

        Assert.True(hands.Latched && hands.Listening, hands.Status);
    }

    /// <summary>Mac <c>doubleTapWhileTranscribing</c>: "a double-tap made
    /// while the last dictation transcribes leaves the next one working".</summary>
    [Fact]
    public async Task DoubleTapWhileTranscribing()
    {
        using var hands = new HandsFree();
        using var decode = new ManualResetEventSlim();
        hands.Rig.Platform.Worker.HoldTranscribe = decode;
        await hands.EnableAsync();

        hands.Press();
        hands.Speak();
        hands.Advance(400);
        hands.Release();
        hands.DrainUntil(() => hands.Status == DictationCopy.Transcribing);
        hands.Advance(400);

        hands.DoubleTap();
        Assert.False(hands.Latched, "a double-tap while busy does not latch");
        Assert.False(hands.Listening);
        decode.Set();
        hands.DrainUntil(() => hands.Inserted);
        hands.Rig.Platform.Worker.HoldTranscribe = null;
        hands.Wait(400);

        hands.LatchesAndFinishes(transcribedBefore: 1);
        Assert.Equal(2, hands.Pastes);
    }

    /// <summary>Mac <c>captureComesUpBetweenTheTaps</c>: "a cold microphone
    /// that comes up between the taps still latches" — the field check
    /// answering between them.</summary>
    [Fact]
    public async Task CaptureComesUpBetweenTheTaps()
    {
        using var hands = new HandsFree(checks: true);
        await hands.EnableAsync();

        // Tap one: released while the check is still out.
        hands.Press();
        hands.Advance(60);
        hands.Release();
        Assert.False(hands.Listening);
        // The check answers before the second tap: the microphone opens.
        hands.Checks.Answer();
        hands.Wait(10);
        Assert.True(hands.Listening);
        hands.Speak();
        // Tap two, still inside the window.
        hands.Advance(80);
        hands.Tap();

        Assert.True(hands.Latched && hands.Listening);
        hands.Wait(500);
        Assert.True(hands.Latched, "still dictating past the window");
        Assert.Equal(0, hands.Transcribes);
        hands.DoubleTap();
        hands.DrainUntil(() => hands.Inserted);
        Assert.Equal(1, hands.Transcribes);
    }

    /// <summary>Mac <c>loneColdTapTranscribes</c>: "a lone quick tap with a
    /// cold microphone still transcribes after the window".</summary>
    [Fact]
    public async Task LoneColdTapTranscribes()
    {
        using var hands = new HandsFree(checks: true);
        await hands.EnableAsync();

        hands.Press();
        hands.Advance(60);
        hands.Release();
        hands.Checks.Answer();
        hands.Wait(10);
        hands.Speak();
        hands.Wait(HoldKeyState.HandsFreeWindowMs);
        hands.DrainUntil(() => hands.Inserted);

        Assert.False(hands.Latched);
        Assert.Equal(1, hands.Pastes);
    }
}

/// <summary>Mac suite "A latch ends on one press, a stop key, or the old
/// double-tap habit".</summary>
public sealed class LatchStopTests
{
    /// <summary>Mac <c>pressStopsALatch</c>: "one press of the hold key
    /// stops a latch and transcribes", and its release starts nothing.</summary>
    [Fact]
    public async Task PressStopsALatch()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Wait(2_000);
        hands.Press();
        hands.DrainUntil(() => hands.Inserted);
        Assert.False(hands.Latched);
        Assert.Equal(1, hands.Transcribes);
        Assert.Equal(HoldKeyTransition.None, hands.Event(HoldKeyState.VkSpace, HandsFree.Down));

        hands.Advance(3_000);
        hands.Release();
        hands.Wait(100);
        Assert.False(hands.Listening);
        Assert.Equal(1, hands.Transcribes);
    }

    /// <summary>Mac <c>trailingTapIsAbsorbed</c>: "the trailing tap of a
    /// habitual double-tap stop starts nothing".</summary>
    [Fact]
    public async Task TrailingTapIsAbsorbed()
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        hands.Wait(2_000);
        hands.Tap();
        hands.DrainUntil(() => hands.Inserted);
        hands.Advance(100);
        hands.Tap();
        hands.Wait(500);
        Assert.False(hands.Listening || hands.Latched, hands.Status);
        Assert.Equal(1, hands.Pastes);
        Assert.Equal(1, hands.Transcribes);   // nothing new was recorded or transcribed

        // Past the window the next hold works normally.
        hands.Press();
        Assert.True(hands.Listening);
        hands.Speak();
        hands.Advance(800);
        hands.Release();
        hands.DrainUntil(() => hands.Pastes == 2 && hands.Inserted);
    }

    /// <summary>Mac <c>stopKeyStopsALatch</c>: "a stop key during a latch
    /// stops it and transcribes"; the key never reaches the app, down or up,
    /// and types again once the latch is over.</summary>
    [Theory]
    [InlineData(HoldKeyState.VkSpace)]
    [InlineData(HoldKeyState.VkReturn)]
    public async Task StopKeyStopsALatch(int stopKey)
    {
        using var hands = new HandsFree();
        await hands.EnableAsync();

        hands.DoubleTap();
        Assert.True(LowLevelSwallows(hands.Event(stopKey, HandsFree.Down)));
        hands.DrainUntil(() => hands.Inserted);
        Assert.True(LowLevelSwallows(hands.Event(stopKey, HandsFree.Up)));

        Assert.False(hands.Latched);
        Assert.Equal(1, hands.Pastes);
        Assert.False(LowLevelSwallows(hands.Event(stopKey, HandsFree.Down)), "the trap leaves with the latch");
    }

    /// <summary>Mac <c>pressStopsALatchBeforeCapture</c>: "the stop press
    /// also ends a latch before capture is up". A deliberate difference: the
    /// Mac records from the moment capture comes up and transcribes that
    /// sliver; Windows opens the microphone only once the password-field
    /// check answers, so a hold that was over by then recorded nothing and
    /// says nothing, as a released press always has. What must match is that
    /// the latch is over: nothing stays open or swallowed.</summary>
    [Fact]
    public async Task PressStopsALatchBeforeCapture()
    {
        using var hands = new HandsFree(checks: true);
        await hands.EnableAsync();

        hands.DoubleTap();
        Assert.True(hands.Latched, "the latch waits for the check");
        hands.Advance(200);
        hands.Press();
        Assert.False(hands.Latched);
        hands.Checks.Answer();
        hands.Wait(30);

        Assert.False(hands.Listening);
        Assert.Equal(0, hands.Rig.Platform.Microphone.Starts);
        Assert.False(LowLevelSwallows(hands.Event(HoldKeyState.VkSpace, HandsFree.Down)));
    }

    private static bool LowLevelSwallows(HoldKeyTransition transition) =>
        (transition & HoldKeyTransition.Swallow) != 0;
}
