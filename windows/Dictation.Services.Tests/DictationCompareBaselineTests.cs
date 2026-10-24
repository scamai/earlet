using Dictation.Speech;

namespace Dictation.Services.Tests;

/// <summary>
/// The HUD receipt that shows the stock decode of the same clip beside the
/// biased one, when a session result carries <c>BaselineText</c>. Nothing in
/// the Windows app asks for a baseline (DictationHudFeed), so this receipt is
/// held to its contract here and never shown.
/// </summary>
public sealed class DictationCompareBaselineTests
{
    private static DictationSessionResult Delivered(string? baseline) =>
        new(
            DictationOutcome.Inserted,
            "constrain SCLK to 50 megahertz in the XDC",
            FocusedApplicationResult.Resolved("Code", 4242, "top.xdc"),
            DetectedLanguage: "en",
            Fired: [new AsrFiredPhrase { Index = 2, Phrase = "XDC", Pos = 8 }],
            BaselineText: baseline);

    // ── the HUD ──────────────────────────────────────────────────────────

    [Fact]
    public void The_receipt_shows_the_stock_decode_for_about_four_seconds()
    {
        var model = DictationHudModel.For(Delivered("constrain S clock in the ex dee see"));

        Assert.True(model.IsVisible);
        Assert.StartsWith("Typed into Code", model.Text);
        Assert.Contains(DictationHudModel.CompareLabel, model.Text);
        Assert.Contains("constrain S clock in the ex dee see", model.Text);
        Assert.Equal(HudTone.Neutral, model.Tone);
        Assert.Equal(DictationHudModel.CompareDwell, model.Dwell);
        Assert.InRange(DictationHudModel.CompareDwell.TotalSeconds, 3.5, 4.5);
    }

    [Fact]
    public void The_compare_receipt_is_not_quieted_by_the_live_preview()
    {
        // The preview already showed the words that landed; the stock decode
        // is the one thing it could not show, so the pill still appears.
        var model = DictationHudModel.For(Delivered("stock"), firstEverInsert: false, previewShowedWords: true);

        Assert.True(model.IsVisible);
        Assert.Contains(DictationHudModel.CompareLabel, model.Text);
    }

    [Fact]
    public void An_empty_stock_decode_is_named_not_blank()
    {
        var model = DictationHudModel.For(Delivered(string.Empty));

        Assert.Contains("(nothing)", model.Text);
    }

    [Fact]
    public void Without_a_baseline_the_receipt_is_the_ordinary_one()
    {
        var model = DictationHudModel.For(Delivered(null));

        Assert.Equal("Typed into Code", model.Text);
        Assert.Equal(DictationHudModel.ReceiptDwell, model.Dwell);
    }

    [Fact]
    public void A_failed_hold_keeps_its_own_sentence_even_with_a_baseline()
    {
        var refused = new DictationSessionResult(
            DictationOutcome.InsertionRefused, null, FocusedApplicationResult.Resolved("Code", 1, "x"),
            BaselineText: "stock");

        var model = DictationHudModel.For(refused);

        Assert.DoesNotContain(DictationHudModel.CompareLabel, model.Text);
        Assert.Equal(HudTone.Caution, model.Tone);
    }
}
