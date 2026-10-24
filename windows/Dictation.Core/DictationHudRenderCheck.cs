namespace Dictation.Core;

/// <summary>
/// The two properties the indicator render mode (<c>DICTATION_HUD_RENDER_DIR</c>)
/// checks, so a run that breaks either fails instead of only producing
/// pictures: the shadow is drawn whole, not cut at the window's edge, and
/// every busy pill of one scale and appearance is one size (the capsule never
/// resizes from the press to the ending). Each is checked twice: on the
/// layout, by the geometry that decides it (<see cref="ShadowProblems"/>,
/// INDICATOR's shadow ring; <see cref="BusyWidthProblems"/>, HINTS' busy
/// width), and on the PNGs written (<see cref="Problems"/>: every image
/// transparent on its outermost rows and columns, every busy image of a
/// group one size). The second is the Mac's <c>DictationHUDRenderCheck</c>,
/// with two Windows additions: images are compared within their group (one
/// scale and appearance: a pill at 150% is half as wide again as the same
/// pill at 100%), and an ending narrower than the busy pills of its group
/// fails too, since an ending starts at the busy width
/// (<see cref="DictationHudGeometry.PillWidth"/>).
/// </summary>
/// <remarks>
/// Pure, so every rule runs on Linux, and the layout's pass there for the
/// layout the app has. The reading of real pixels and real font widths is
/// the WPF render mode's, and has never run (WPF needs Windows).
/// </remarks>
public static class DictationHudRenderCheck
{
    /// <summary>One rendered indicator image, as the check reads it.</summary>
    /// <param name="Name">The image's name, which every problem starts with.</param>
    /// <param name="Group">The scale and appearance it was drawn at: sizes are
    /// compared only within one group.</param>
    /// <param name="Width">Its width in pixels.</param>
    /// <param name="Height">Its height in pixels.</param>
    /// <param name="IsBusy">A busy pill (listening, transcribing, typing):
    /// every one of a group must share one size.</param>
    /// <param name="EdgeAlpha">The largest alpha, 0…255, on its outermost rows
    /// and columns (<see cref="EdgeAlpha(int, int, Func{int, int, int})"/>).</param>
    public sealed record Rendered(string Name, string Group, int Width, int Height, bool IsBusy, int EdgeAlpha);

    /// <summary>Every way the render broke a property; empty when all hold.
    /// Too little to compare is a problem too, never a pass.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyList<Rendered> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var problems = new List<string>();
        if (images.Count == 0)
        {
            problems.Add("nothing was rendered");
        }

        foreach (var image in images.Where(i => i.EdgeAlpha > 0))
        {
            problems.Add($"{image.Name}: alpha {image.EdgeAlpha} on its edge, so the shadow is cut there");
        }

        foreach (var group in images.GroupBy(i => i.Group, StringComparer.Ordinal))
        {
            var busy = group.Where(i => i.IsBusy).ToArray();
            if (busy.Length < 2)
            {
                problems.Add($"{group.Key}: {busy.Length} busy state(s) rendered, too few to compare their sizes");
                continue;
            }

            if (busy.Select(i => (i.Width, i.Height)).Distinct().Count() > 1)
            {
                problems.Add($"{group.Key}: busy states differ in size: "
                    + string.Join(", ", busy.Select(i => $"{i.Name} {i.Width}x{i.Height}")));
                continue;
            }

            var busyWidth = busy[0].Width;
            foreach (var ending in group.Where(i => !i.IsBusy && i.Width < busyWidth))
            {
                problems.Add($"{ending.Name}: {ending.Width} pixels wide, narrower than the busy pills' {busyWidth}");
            }
        }

        return problems;
    }

    /// <summary>How far a drop shadow of <paramref name="blur"/> cast
    /// <paramref name="depth"/> straight down reaches past the capsule on
    /// each side, in DIPs: the blur on the left and right, the blur less the
    /// depth above, and the blur plus the depth below.</summary>
    public static (double Left, double Top, double Right, double Bottom) ShadowReach(double blur, double depth) =>
        (blur, Math.Max(0, blur - depth), blur, blur + depth);

    /// <summary>
    /// Every side on which a transparent ring of <paramref name="margin"/>
    /// DIPs is too narrow for the shadow (<see cref="ShadowReach"/>), so the
    /// window's edge would cut it; empty when the ring holds it all. With no
    /// arguments, the indicator's own: <see cref="DictationHudGeometry.ShadowMargin"/>
    /// around <see cref="DictationHudGeometry.ShadowBlur"/> and
    /// <see cref="DictationHudGeometry.ShadowDepth"/>, which INDICATOR sized
    /// to pass.
    /// </summary>
    public static IReadOnlyList<string> ShadowProblems(
        double margin = DictationHudGeometry.ShadowMargin,
        double blur = DictationHudGeometry.ShadowBlur,
        double depth = DictationHudGeometry.ShadowDepth)
    {
        var reach = ShadowReach(blur, depth);
        var problems = new List<string>();
        foreach (var (side, needs) in new[] { ("left", reach.Left), ("top", reach.Top), ("right", reach.Right), ("bottom", reach.Bottom) })
        {
            if (margin < needs)
            {
                problems.Add($"shadow: the {margin:0.##} DIP ring cuts the shadow on the {side}, which reaches {needs:0.##} DIP");
            }
        }

        return problems;
    }

    /// <summary>
    /// HINTS' busy-width rule, on the frames' own models: every busy pill
    /// (<see cref="DictationHudRenderFrame.IsBusy"/>) is the same
    /// <see cref="DictationHudGeometry.PillWidth"/>, and no other pill is
    /// narrower. Empty when it holds.
    /// </summary>
    /// <param name="frames">What is drawn.</param>
    /// <param name="title">The width of a text in the title's font.</param>
    /// <param name="detail">The width of a text in the second line's font.</param>
    /// <param name="group">What the problems name: the scale the widths were
    /// measured at.</param>
    public static IReadOnlyList<string> BusyWidthProblems(
        IReadOnlyList<DictationHudRenderFrame> frames, Func<string, double> title, Func<string, double> detail, string group)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(detail);
        var widths = frames
            .Select(frame => (frame, Width: DictationHudGeometry.PillWidth(frame.Model, DictationHudGeometry.WidestLine(frame.Model, title, detail))))
            .ToArray();
        var busy = widths.Where(w => w.frame.IsBusy).ToArray();
        if (busy.Length < 2)
        {
            return [$"{group}: {busy.Length} busy pill(s), too few to compare their widths"];
        }

        var problems = new List<string>();
        var width = busy[0].Width;
        if (busy.Any(w => w.Width != width))
        {
            problems.Add($"{group}: busy pills laid out at different widths: "
                + string.Join(", ", busy.Select(w => $"{w.frame.Name} {w.Width:0.##}")));
            return problems;
        }

        foreach (var (frame, w) in widths.Where(w => !w.frame.IsBusy && w.Width < width))
        {
            problems.Add($"{group}: {frame.Name} laid out {w:0.##} DIP wide, narrower than the busy pills' {width:0.##}");
        }

        return problems;
    }

    /// <summary>The largest alpha on the outermost rows and columns of a
    /// <paramref name="width"/> x <paramref name="height"/> image whose alpha
    /// at (x, y) is <paramref name="alpha"/>(x, y).</summary>
    public static int EdgeAlpha(int width, int height, Func<int, int, int> alpha)
    {
        ArgumentNullException.ThrowIfNull(alpha);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var largest = 0;
        for (var x = 0; x < width; x++)
        {
            largest = Math.Max(largest, Math.Max(alpha(x, 0), alpha(x, height - 1)));
        }

        for (var y = 0; y < height; y++)
        {
            largest = Math.Max(largest, Math.Max(alpha(0, y), alpha(width - 1, y)));
        }

        return largest;
    }
}
