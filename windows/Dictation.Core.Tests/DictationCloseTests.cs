namespace Dictation.Core.Tests;

/// <summary>
/// Quitting must not cost the user their clipboard.
/// </summary>
public sealed class DictationCloseTests
{
    /// <summary>The notification-area menu's Quit: true when the app may exit now.</summary>
    private static bool Quit(Rig rig) =>
        rig.Controller.RequestClose(quit: true).Action == DictationCloseAction.Close;

    private static Rig InsertingRig(TimeSpan closeWaitLimit, ManualResetEventSlim pasteHold)
    {
        var rig = new Rig(options: o => o with { CloseWaitLimit = closeWaitLimit });
        rig.Platform.Paste.Hold = pasteHold;
        return rig;
    }

    private static async Task HoldUntilThePasteIsInFlight(Rig rig)
    {
        await rig.EnableAsync();
        rig.Press();
        rig.ReleaseToHeldPaste();
        Assert.Equal("hello world", rig.Platform.Clipboard.Text, ignoreCase: true);
    }

    [Fact]
    public async Task A_quit_during_the_insertion_waits_for_the_clipboard_to_come_back()
    {
        using var pasteHold = new ManualResetEventSlim();
        using var rig = InsertingRig(TimeSpan.FromMinutes(1), pasteHold);
        await HoldUntilThePasteIsInFlight(rig);

        Assert.False(Quit(rig));
        Assert.Equal(DictationCopy.FinishingBeforeClose, rig.View.Last.Status);
        Assert.Equal(0, rig.View.QuitRequests);

        pasteHold.Set();
        rig.DrainUntil(() => rig.View.QuitRequests == 1);

        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
        Assert.True(Quit(rig));
    }

    [Fact]
    public async Task A_quit_at_rest_goes_ahead_at_once()
    {
        using var rig = new Rig();
        await rig.EnableAsync();

        Assert.True(Quit(rig));
        Assert.Equal(0, rig.View.QuitRequests);
    }

    [Fact]
    public async Task A_quit_while_listening_goes_ahead_and_touches_no_clipboard()
    {
        using var rig = new Rig();
        await rig.EnableAsync();
        rig.Press();

        Assert.True(Quit(rig));
        rig.Controller.Dispose();

        Assert.Equal(0, rig.Platform.Paste.Count);
        Assert.Equal("the user's clipboard", rig.Platform.Clipboard.Text);
    }

    [Fact]
    public async Task An_insertion_that_never_finishes_cannot_hold_the_window_open()
    {
        using var pasteHold = new ManualResetEventSlim();
        using var rig = InsertingRig(TimeSpan.FromMilliseconds(100), pasteHold);
        await HoldUntilThePasteIsInFlight(rig);

        Assert.False(Quit(rig));
        rig.DrainUntil(() => rig.View.QuitRequests == 1);

        Assert.True(Quit(rig));
        pasteHold.Set();
    }
}
