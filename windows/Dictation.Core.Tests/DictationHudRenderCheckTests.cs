namespace Dictation.Core.Tests;

/// <summary>
/// The indicator render's check (<see cref="DictationHudRenderCheck"/>): on
/// the layout, INDICATOR's shadow ring and HINTS' busy width, each passing for
/// the layout the app has and failing for one that breaks it; then on the
/// pixels, the Mac's <c>DictationHUDRenderCheckTests</c> one for one and the
/// two Windows additions, sizes compared per scale and appearance and an
/// ending no narrower than the busy pills.
/// </summary>
public sealed class DictationHudRenderCheckTests
{
    // ─── The layout: the shadow ring ────────────────────────────────────

    /// <summary>
    /// INDICATOR's ring holds the whole shadow, on every side, by the
    /// geometry the window is built from: the check passes for it with no
    /// arguments, the ones the render mode passes. A ring that shrinks below
    /// the shadow's reach fails it (mutation: <c>ShadowMargin</c> back to 16,
    /// or to the blur alone).
    /// </summary>
    [Fact]
    public void The_indicators_ring_holds_its_whole_shadow()
    {
        Assert.Empty(DictationHudRenderCheck.ShadowProblems());
        Assert.Empty(DictationHudRenderCheck.ShadowProblems(
            DictationHudGeometry.ShadowMargin, DictationHudGeometry.ShadowBlur, DictationHudGeometry.ShadowDepth));
        var reach = DictationHudRenderCheck.ShadowReach(DictationHudGeometry.ShadowBlur, DictationHudGeometry.ShadowDepth);
        Assert.Equal(DictationHudGeometry.ShadowMargin, Math.Max(Math.Max(reach.Left, reach.Right), Math.Max(reach.Top, reach.Bottom)));
    }

    /// <summary>The shadow falls straight down: it reaches the blur on the
    /// sides, the blur less the depth above and the blur plus the depth below,
    /// so a ring of the blur alone cuts it at the bottom only, and the old
    /// 16-DIP ring on every side.</summary>
    [Fact]
    public void A_ring_narrower_than_the_shadows_reach_fails_on_each_side_it_cuts()
    {
        Assert.Equal((24.0, 19.0, 24.0, 29.0), DictationHudRenderCheck.ShadowReach(24, 5));
        Assert.Equal(0, DictationHudRenderCheck.ShadowReach(4, 5).Top);

        var bottom = Assert.Single(DictationHudRenderCheck.ShadowProblems(margin: 24, blur: 24, depth: 5));
        Assert.Equal("shadow: the 24 DIP ring cuts the shadow on the bottom, which reaches 29 DIP", bottom);
        Assert.Equal(4, DictationHudRenderCheck.ShadowProblems(margin: 16, blur: 24, depth: 5).Count);
        Assert.Single(DictationHudRenderCheck.ShadowProblems(margin: 28.9, blur: 24, depth: 5));
        Assert.Empty(DictationHudRenderCheck.ShadowProblems(margin: 29, blur: 24, depth: 5));
    }

    // ─── The layout: HINTS' one busy width ──────────────────────────────

    private static IReadOnlyList<DictationHudRenderFrame> Frames => DictationHudRenderFrames.All(DictationHudRenderFrames.DefaultHold);

    /// <summary>
    /// Every busy pill the render draws is HINTS' one busy width and no other
    /// pill is narrower, in the stand-in face the busy-width tests use and in
    /// the one whose digits are wider than anything else. The render mode
    /// runs the same check with the real fonts at each scale.
    /// </summary>
    [Fact]
    public void Every_busy_frame_is_one_width_by_HINTS_rules()
    {
        foreach (var face in new Func<char, double>[] { IndicatorHintTests.Glyph, IndicatorHintTests.DigitHeavy })
        {
            Assert.Empty(DictationHudRenderCheck.BusyWidthProblems(
                Frames, title => 1.2 * title.Sum(face), detail => detail.Sum(face), "stand-in"));
        }

        Assert.True(Frames.Count(f => f.IsBusy) >= 9, "too few busy frames to compare");
    }

    /// <summary>A busy pill laid out at another width, an ending narrower
    /// than the busy pills, and too few busy pills each fail.</summary>
    [Fact]
    public void A_busy_pill_of_another_width_or_a_narrower_ending_fails()
    {
        static double Title(string text) => 1.2 * text.Sum(IndicatorHintTests.Glyph);
        static double Detail(string text) => text.Sum(IndicatorHintTests.Glyph);
        var frames = Frames;

        // A pill not sized for the hold's busy lines is as wide as its own text.
        var typing = frames.Single(f => f.Name == "typing");
        var unsized = frames.Select(f => f == typing ? f with { Model = f.Model with { SizedFor = ["Typing"], Dwell = null } } : f).ToArray();
        var problem = Assert.Single(DictationHudRenderCheck.BusyWidthProblems(unsized, Title, Detail, "100%"));
        Assert.StartsWith("100%: busy pills laid out at different widths", problem, StringComparison.Ordinal);

        var inserted = frames.Single(f => f.Name == "outcome-inserted");
        var narrow = frames.Select(f => f == inserted ? f with { Model = f.Model with { SizedFor = null } } : f).ToArray();
        Assert.StartsWith(
            "100%: outcome-inserted laid out",
            Assert.Single(DictationHudRenderCheck.BusyWidthProblems(narrow, Title, Detail, "100%")),
            StringComparison.Ordinal);

        Assert.NotEmpty(DictationHudRenderCheck.BusyWidthProblems([typing], Title, Detail, "100%"));
        Assert.NotEmpty(DictationHudRenderCheck.BusyWidthProblems([], Title, Detail, "100%"));
    }

    // ─── The pixels ─────────────────────────────────────────────────────

    private const string Group = "100%-light";

    private static DictationHudRenderCheck.Rendered Image(
        string name, int width, int height, bool busy = true, int edge = 0, string group = Group) =>
        new(name, group, width, height, busy, edge);

    // ─── The Mac's scenarios ────────────────────────────────────────────

    [Fact]
    public void Transparent_edges_and_one_busy_size_pass()
    {
        Assert.Empty(DictationHudRenderCheck.Problems(
        [
            Image("recording", 396, 92), Image("transcribing", 396, 92),
            Image("outcome-secure-input", 424, 92, busy: false),
        ]));
    }

    [Fact]
    public void A_shadow_cut_at_any_images_edge_fails_busy_or_not()
    {
        var problems = DictationHudRenderCheck.Problems(
        [
            Image("recording", 396, 92), Image("transcribing", 396, 92),
            Image("mic-changed", 447, 92, busy: false, edge: 39),
        ]);

        var problem = Assert.Single(problems);
        Assert.StartsWith("mic-changed", problem, StringComparison.Ordinal);

        var busyCut = DictationHudRenderCheck.Problems(
            [Image("recording", 396, 92, edge: 1), Image("transcribing", 396, 92)]);
        Assert.StartsWith("recording", Assert.Single(busyCut), StringComparison.Ordinal);
    }

    [Fact]
    public void Busy_states_of_different_sizes_fail()
    {
        Assert.Single(DictationHudRenderCheck.Problems([Image("recording", 266, 52), Image("recording-latched", 325, 52)]));
        Assert.Single(DictationHudRenderCheck.Problems([Image("listening", 396, 92), Image("typing", 396, 70)]));
    }

    [Fact]
    public void Too_little_to_compare_is_never_a_pass()
    {
        Assert.NotEmpty(DictationHudRenderCheck.Problems([]));
        Assert.NotEmpty(DictationHudRenderCheck.Problems([Image("recording", 396, 92)]));
        Assert.NotEmpty(DictationHudRenderCheck.Problems([Image("outcome-inserted", 396, 92, busy: false)]));
    }

    [Fact]
    public void The_edge_reading_covers_all_four_edges_and_nothing_inside()
    {
        // A 5 x 4 image, opaque inside; one edge pixel at a time.
        static int Edge((int X, int Y)? lit) => DictationHudRenderCheck.EdgeAlpha(5, 4, (x, y) =>
        {
            if (lit is { } at && at.X == x && at.Y == y)
            {
                return 7;
            }

            return x is >= 1 and <= 3 && y is >= 1 and <= 2 ? 255 : 0;
        });

        Assert.Equal(0, Edge(null));
        foreach (var lit in new[] { (2, 0), (2, 3), (0, 1), (4, 2), (0, 0), (4, 3) })
        {
            Assert.True(Edge(lit) == 7, $"{lit}");
        }

        Assert.Equal(0, DictationHudRenderCheck.EdgeAlpha(0, 4, (_, _) => 255));
    }

    // ─── Windows' additions ─────────────────────────────────────────────

    /// <summary>A pill at 150% is half as wide again as at 100%: sizes are
    /// compared within a scale and appearance, never across them, and every
    /// group needs two busy pills of its own.</summary>
    [Fact]
    public void Busy_sizes_are_compared_within_one_scale_and_appearance()
    {
        Assert.Empty(DictationHudRenderCheck.Problems(
        [
            Image("listening-100", 396, 92), Image("typing-100", 396, 92),
            Image("listening-150", 594, 138, group: "150%-light"), Image("typing-150", 594, 138, group: "150%-light"),
        ]));

        var mixed = DictationHudRenderCheck.Problems(
        [
            Image("listening-100", 396, 92), Image("typing-100", 396, 92),
            Image("listening-150", 594, 138, group: "150%-light"), Image("typing-150", 595, 138, group: "150%-light"),
        ]);
        Assert.StartsWith("150%-light: busy states differ in size", Assert.Single(mixed), StringComparison.Ordinal);

        var lonely = DictationHudRenderCheck.Problems(
        [
            Image("listening-100", 396, 92), Image("typing-100", 396, 92),
            Image("listening-150", 594, 138, group: "150%-light"),
        ]);
        Assert.StartsWith("150%-light: 1 busy state(s)", Assert.Single(lonely), StringComparison.Ordinal);
    }

    /// <summary>An ending starts at the busy width and grows for its
    /// sentence, so one narrower than its group's busy pills fails; one
    /// shorter is fine (an ending of one line is one line high).</summary>
    [Fact]
    public void An_ending_narrower_than_the_busy_pills_fails()
    {
        var problems = DictationHudRenderCheck.Problems(
        [
            Image("listening", 396, 92), Image("transcribing", 396, 92),
            Image("outcome-inserted", 396, 70, busy: false),
            Image("outcome-cancelled", 395, 70, busy: false),
        ]);

        Assert.Equal("outcome-cancelled: 395 pixels wide, narrower than the busy pills' 396", Assert.Single(problems));
    }
}
