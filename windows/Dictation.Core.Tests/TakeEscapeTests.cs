using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

// LIMIT-WIN-A, Escape on a long take (LIMIT 2026-10-10: "Escape discards; on
// a take longer than 30 s it asks to confirm first"; LIMIT-DESIGN §3.1, as
// Superwhisper asks): the first Escape asks "Discard 3:42 of speech? — press
// Esc again" and the take carries on; a second Escape within the
// question's five seconds discards. Through the hook's own state machine
// (HoldKeyState, FakeTrigger.UseGesture) and the controller the app runs.

/// <summary>Escape's question.</summary>
public sealed class TakeEscapeTests
{
    private const int Escape = HoldKeyState.VkEscape;

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(31);

    /// <summary>The question for a take of the press's half second plus
    /// <see cref="Long"/>.</summary>
    private static readonly string Asked = DictationCopy.HudAskToDiscard(TimeSpan.FromSeconds(31.5));

    private static void SpeakLong(HandsFree hf)
    {
        hf.Rig.Platform.Microphone.Speak(Long);
        // The next poll raises the hook's flag for a take this long; past the
        // double-tap window, so a held key's release is its release.
        hf.Wait(400);
    }

    /// <summary>A held take with more than thirty seconds of speech.</summary>
    private static async Task<HandsFree> HeldLongTake()
    {
        var hf = new HandsFree();
        await hf.EnableAsync();
        hf.Press();
        hf.Speak();
        SpeakLong(hf);
        Assert.True(hf.Key.EscapeAsksFirst);
        return hf;
    }

    [Fact]
    public async Task Escape_on_a_long_take_asks_first_and_the_take_carries_on()
    {
        using var hf = await HeldLongTake();

        hf.Type(Escape);

        Assert.Equal(Asked, hf.Rig.Hud.Current!.Text);
        Assert.Equal(HudTone.Caution, hf.Rig.Hud.Current.Tone);
        // Never spoken: the microphone is open.
        Assert.False(hf.Rig.Hud.Announced[^1]);
        Assert.True(hf.Listening);
        Assert.Equal(TrayIcon.Listening, hf.Rig.View.Last.Tray.Icon);
        Assert.Equal(0, hf.Transcribes);
        // The listening clock does not replace the question.
        hf.Wait(1_000);
        Assert.Equal(Asked, hf.Rig.Hud.Current!.Text);

        hf.Release();
        hf.DrainUntil(() => hf.Inserted);
        Assert.Equal(1, hf.Pastes);
    }

    [Fact]
    public async Task A_second_Escape_within_the_question_discards_the_take()
    {
        using var hf = await HeldLongTake();

        hf.Type(Escape);
        hf.Wait(2_000);
        hf.Type(Escape);
        hf.DrainUntilEnded();

        Assert.Equal("Cancelled. Nothing was inserted.", hf.Status);
        Assert.Equal(0, hf.Transcribes);
        Assert.Equal(0, hf.Pastes);
        Assert.Null(hf.Rig.View.Last.SavedTake);
        Assert.Empty(Directory.GetFiles(hf.Rig.RecoveryDirectory));
    }

    [Fact]
    public async Task Once_the_question_s_time_is_up_the_take_carries_on_and_the_next_Escape_asks_again()
    {
        using var hf = await HeldLongTake();
        hf.Type(Escape);
        Assert.Equal(Asked, hf.Rig.Hud.Current!.Text);

        hf.Wait(5_000);
        Assert.NotEqual(Asked, hf.Rig.Hud.Current?.Text);
        Assert.StartsWith("Listening", hf.Rig.Hud.Current!.Text, StringComparison.Ordinal);
        Assert.True(hf.Key.EscapeAsksFirst);

        hf.Type(Escape);
        Assert.StartsWith("Discard 0:3", hf.Rig.Hud.Current!.Text, StringComparison.Ordinal);
        Assert.True(hf.Listening);
        Assert.Equal(0, hf.Transcribes);
    }

    [Fact]
    public async Task A_quick_second_Escape_discards_even_when_the_first_landed_inside_a_poll()
    {
        // The first Escape reaches the hook after this poll has read the
        // cancel latch but before it syncs the question's flag, and the
        // second comes before the next poll. The poll must not raise the
        // flag again under the first, or the second would only ask again.
        using var hf = await HeldLongTake();
        hf.Key.AfterConsume = () => hf.Key.KeyEvent(Escape, HandsFree.Down);
        hf.Rig.Controller.Tick();
        Assert.False(hf.Key.EscapeAsksFirst);

        hf.Key.KeyEvent(Escape, HandsFree.Up);
        hf.Key.KeyEvent(Escape, HandsFree.Down);
        hf.Rig.Controller.Tick();
        hf.Rig.Dispatcher.Drain();
        hf.DrainUntilEnded();

        Assert.Equal("Cancelled. Nothing was inserted.", hf.Status);
        Assert.Equal(0, hf.Transcribes);
        Assert.Equal(0, hf.Pastes);
    }

    [Fact]
    public async Task Escape_on_a_short_take_cancels_at_once()
    {
        using var hf = new HandsFree();
        await hf.EnableAsync();
        hf.Press();
        hf.Speak();
        hf.Wait(30);
        Assert.False(hf.Key.EscapeAsksFirst);

        hf.Type(Escape);
        hf.DrainUntilEnded();

        Assert.Equal("Cancelled. Nothing was inserted.", hf.Status);
        Assert.DoesNotContain(hf.Rig.Hud.Shows, m => m.Text.StartsWith("Discard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Escape_on_a_long_hands_free_take_asks_and_keeps_the_latch()
    {
        using var hf = new HandsFree();
        await hf.EnableAsync();
        hf.DoubleTap();
        Assert.True(hf.Latched && hf.Listening);
        SpeakLong(hf);

        hf.Type(Escape);

        Assert.True(hf.Latched);
        Assert.True(hf.Listening);
        Assert.StartsWith("Discard 0:3", hf.Rig.Hud.Current!.Text, StringComparison.Ordinal);

        hf.Type(Escape);
        hf.DrainUntilEnded();
        Assert.False(hf.Latched);
        Assert.Equal("Cancelled. Nothing was inserted.", hf.Status);
    }

    [Fact]
    public async Task Escape_while_a_long_take_is_transcribed_asks_first_and_says_so_aloud()
    {
        using var hf = await HeldLongTake();
        using var decode = new ManualResetEventSlim();
        hf.Rig.Platform.Worker.HoldTranscribe = decode;
        try
        {
            hf.Release();
            hf.DrainUntil(() => hf.Rig.View.Last.Tray.Icon == TrayIcon.Transcribing);
            hf.Wait(30);

            hf.Type(Escape);
            Assert.Equal(Asked, hf.Rig.Hud.Current!.Text);
            Assert.True(hf.Rig.Hud.Announced[^1]);

            hf.Type(Escape);
        }
        finally
        {
            decode.Set();
        }

        hf.DrainUntilEnded();
        Assert.Equal("Cancelled. Nothing was inserted.", hf.Status);
        Assert.Equal(0, hf.Pastes);
    }

    [Fact]
    public async Task The_window_s_Cancel_never_asks()
    {
        // A button is a choice; only a stray key asks.
        using var hf = await HeldLongTake();

        hf.Rig.Controller.Cancel();
        hf.DrainUntilEnded();

        Assert.Equal("Cancelled. Nothing was inserted.", hf.Status);
        Assert.False(hf.Key.EscapeAsksFirst);
    }
}
