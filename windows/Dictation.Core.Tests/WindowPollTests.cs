using Dictation.Services;

namespace Dictation.Core.Tests;

/// <summary>
/// How often the window's timer ticks the controller
/// (<see cref="DictationController.PollInterval"/>): every 30 ms while there
/// is work for a tick (the indicator's pill among it), once a second while
/// dictation is off and the indicator shows nothing. Closing the
/// window no longer quits a paused Dictation, so this is what one hidden in
/// the notification area costs. Each render records the interval the window
/// would set then, as <c>MainWindow.Render</c> asks for it; the timer itself
/// is WPF's and has never run on Windows.
/// </summary>
public sealed class WindowPollTests
{
    private static bool Inserted(DictationViewState v) => v.Status.StartsWith("Text inserted", StringComparison.Ordinal);

    /// <summary>The interval each render would set, in render order.</summary>
    private static List<TimeSpan> Paced(Rig rig)
    {
        var paced = new List<TimeSpan>();
        rig.View.OnRender = _ => paced.Add(rig.Controller.PollInterval);
        return paced;
    }

    [Fact]
    public void Off_the_window_polls_once_a_second()
    {
        using var rig = new Rig();

        Assert.False(rig.Controller.IsEnabled);
        Assert.Equal(DictationController.RestingPoll, rig.Controller.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), DictationController.RestingPoll);
        Assert.Equal(TimeSpan.FromMilliseconds(30), DictationController.BusyPoll);
    }

    [Fact]
    public async Task Enable_speeds_the_poll_up_from_the_render_that_says_starting_and_Pause_slows_it_again()
    {
        using var rig = new Rig();
        using var load = new ManualResetEventSlim();
        var paced = Paced(rig);

        rig.Platform.Worker.HoldInit = load;
        var enabling = rig.Controller.EnableAsync();
        // The render that says Starting is the one the window asks at: busy
        // already, so the first hold after the model loads is polled at once.
        Assert.Equal(DictationGlyph.Starting, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationController.BusyPoll, paced[^1]);
        load.Set();
        await enabling;
        rig.Dispatcher.Drain();
        Assert.Equal(DictationGlyph.Ready, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationController.BusyPoll, paced[^1]);

        rig.Dictate(Inserted);
        Assert.Equal(DictationController.BusyPoll, rig.Controller.PollInterval);

        Assert.True(rig.Controller.TryPause());
        Assert.Equal(DictationGlyph.Off, rig.View.Last.Header.Glyph);
        Assert.Equal(DictationController.RestingPoll, paced[^1]);
    }

    [Fact]
    public async Task A_copy_while_paused_is_polled_fast_until_the_clipboard_answers()
    {
        var copy = new RecentResultTests.HeldCopy();
        using var rig = new Rig(options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.True(rig.Controller.TryPause());
        var paced = Paced(rig);

        rig.Controller.CopyLastText();
        // The copy's deadline is read on the tick: at a second a tick, a hung
        // clipboard would hold the copy (and every press) up to a second late.
        Assert.Equal(DictationController.BusyPoll, paced[^1]);
        Assert.Equal(DictationController.BusyPoll, rig.Controller.PollInterval);

        RecentResultTests.Answer(rig, copy);
        Assert.Equal(DictationCopy.Copied, rig.View.Last.Recent?.CopyMessage);
        // The indicator's "Copied" keeps it fast until its dwell is over
        // (DictationHudFeed.Active); then it rests.
        Assert.Equal(DictationController.BusyPoll, paced[^1]);
        RestsOnceTheIndicatorIsDown(rig);
    }

    /// <summary>The pill on screen goes at the end of its dwell, at a tick,
    /// and the poll rests from then.</summary>
    private static void RestsOnceTheIndicatorIsDown(Rig rig)
    {
        var dwell = Assert.IsType<TimeSpan>(Assert.IsType<DictationHudModel>(rig.Hud.Current).Dwell);
        rig.Clock.Advance(dwell);
        rig.Controller.Tick();
        Assert.Null(rig.Hud.Current);
        Assert.Equal(DictationController.RestingPoll, rig.Controller.PollInterval);
    }

    [Fact]
    public async Task A_copy_the_clipboard_never_answers_while_paused_lets_the_poll_rest_at_the_deadline()
    {
        var copy = new RecentResultTests.HeldCopy();
        using var rig = new Rig(options: o => o with { RunClipboardCopy = copy.Run });
        await rig.EnableAsync();
        rig.Dictate(Inserted);
        Assert.True(rig.Controller.TryPause());
        var paced = Paced(rig);

        rig.Controller.CopyLastText();
        rig.Clock.Advance(new DictationControllerOptions().ClipboardCopyDeadline);
        rig.Controller.Tick();

        Assert.Equal(DictationCopy.CopyFailed, rig.View.Last.Recent?.CopyMessage);
        // "Couldn't copy" is on the indicator: fast until it goes, then rest.
        Assert.Equal(DictationController.BusyPoll, paced[^1]);
        RestsOnceTheIndicatorIsDown(rig);
    }

    [Fact]
    public void After_quitting_the_poll_rests()
    {
        using var rig = new Rig();
        rig.Controller.Dispose();

        Assert.Equal(DictationController.RestingPoll, rig.Controller.PollInterval);
    }

    // ─── The WPF half: a pin on the source, not evidence it behaves ─────

    [Fact]
    public void The_window_sets_the_poll_after_every_tick_and_every_render()
    {
        var code = File.ReadAllText(DictationHudGeometryTests.AppFile("MainWindow.xaml.cs")).ReplaceLineEndings("\n");

        Assert.Contains(
            "poll.Tick += (_, _) =>\n            {\n                controller.Tick();\n                PacePoll();\n            };",
            DictationHudGeometryTests.Member(code, "public MainWindow("),
            StringComparison.Ordinal);
        var render = DictationHudGeometryTests.Member(code, "public void Render(DictationViewState state)");
        // Last, after the controls and the menu are drawn.
        Assert.EndsWith("\n        PacePoll();", render.TrimEnd(), StringComparison.Ordinal);
        var pace = DictationHudGeometryTests.Member(code, "private void PacePoll()");
        Assert.Contains("controller?.PollInterval is { } wanted && poll.Interval != wanted", pace, StringComparison.Ordinal);
    }
}
