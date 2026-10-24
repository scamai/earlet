namespace Dictation.Services.Tests;

/// <summary>
/// The island's width per state — the arithmetic behind "it must not move
/// while you speak, and an outcome is its sentence".
/// </summary>
public sealed class DictationHudWidthTests
{
    private const double Chrome = 52;   // 240 compact minus the 188 caption slot

    [Fact]
    public void AShortOutcomeStaysCompact()
    {
        // Growing for "Copied" would be the bouncing the compact canvas exists
        // to stop, in a state that does not need the room.
        Assert.Equal(DictationHudWidth.Compact, DictationHudWidth.ForSentence(60, Chrome));
    }

    [Fact]
    public void ASentenceThatDoesNotFitGetsTheRoomItNeeds()
    {
        // 416 is the measured width of the longest failure copy; the pill has
        // to be wider than the text by its chrome.
        Assert.Equal(468, DictationHudWidth.ForSentence(416, Chrome));
    }

    [Fact]
    public void ThePillNeverBecomesAParagraph()
    {
        Assert.Equal(DictationHudWidth.Max, DictationHudWidth.ForSentence(5000, Chrome));
    }

    [Fact]
    public void TheCapIsTheWidthThisIslandUsedToHave()
    {
        // 536 was the every-state width, sized so the longest copy fit. Kept
        // as the ceiling so this change cannot make the island bigger than it
        // has already been.
        Assert.Equal(536, DictationHudWidth.Max);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-10)]
    public void AnUnmeasuredSentenceFallsBackToCompact(double textWidth)
    {
        Assert.Equal(DictationHudWidth.Compact, DictationHudWidth.ForSentence(textWidth, Chrome));
    }

    [Fact]
    public void NegativeChromeCannotShrinkThePill()
    {
        Assert.Equal(DictationHudWidth.Compact, DictationHudWidth.ForSentence(100, -500));
    }
}
