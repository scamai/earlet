using System.Buffers.Binary;

namespace Dictation.Core;

/// <summary>
/// The notification-area icon, drawn in code as a Windows <c>.ico</c>: a
/// microphone, in the taskbar's own ink when dictation is ready, in red while
/// the microphone is open, and faded while dictation is paused (under High
/// Contrast, in the theme's colours and the Mac's three microphone shapes); a waveform,
/// in the taskbar's ink, while the words are transcribed and typed; and a
/// warning triangle, in the taskbar's ink, while Windows blocks the
/// microphone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why drawn rather than shipped.</b> Five states times two taskbar themes
/// times four sizes is forty bitmaps to keep in step by hand. Drawn from
/// one outline they cannot drift apart, the bytes are the same on every
/// machine, and — the reason it lives in this project — the shapes, colours
/// and file layout are checked on a machine with no Windows in it.
/// Dictation.App only hands the bytes to <c>System.Drawing.Icon</c>.
/// </para>
/// <para>
/// <b>High Contrast.</b> Under a contrast theme the inks are the theme's
/// own (<see cref="TrayInks.HighContrast"/>): its text colour, its selection
/// colour while the microphone is open, and its disabled-text colour for the
/// pause, at full strength. The Mac's menu bar glyph is a template image the
/// system tints; this is the nearest Windows has. Those colours are the
/// user's, and may coincide, so the three microphone states also take the
/// Mac's three shapes there (<see cref="TrayInks.ByShape"/>): an outlined
/// capsule when ready (<c>mic</c>), the filled one while the microphone is
/// open (<c>mic.fill</c>) and the outline struck through while off
/// (<c>mic.slash</c>). Every state is then a shape of its own whatever the
/// theme's colours are; the taskbar's designed pictures are unchanged.
/// </para>
/// <para>
/// <b>Format.</b> One <c>ICONDIR</c>, then one 32-bit BGRA DIB per size with an
/// all-zero AND mask, so the alpha channel alone decides what shows. Sizes
/// 16, 20, 24 and 32 cover the small-icon size at 100–200 % scaling; Windows
/// picks the closest.
/// </para>
/// </remarks>
public static class TrayIconArt
{
    /// <summary>The pixel sizes in every icon, smallest first.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 20, 24, 32];

    /// <summary>Ink on a light taskbar.</summary>
    public const uint DarkInk = 0xFF1B1B1B;

    /// <summary>Ink on a dark taskbar.</summary>
    public const uint LightInk = 0xFFFFFFFF;

    /// <summary>The microphone while it is open, on either taskbar.</summary>
    public const uint RecordingInk = 0xFFE81123;

    /// <summary>How opaque the paused icon is, out of 255.</summary>
    public const byte PausedAlpha = 110;

    /// <summary>The icon for <paramref name="state"/>, as <c>.ico</c> bytes.</summary>
    /// <param name="state">Which picture.</param>
    /// <param name="lightTaskbar">Whether the taskbar is light (Windows'
    /// <c>SystemUsesLightTheme</c>), which decides the ink.</param>
    public static byte[] Render(TrayIcon state, bool lightTaskbar) => Render(state, TrayInks.For(lightTaskbar));

    /// <summary>The icon for <paramref name="state"/> in
    /// <paramref name="inks"/>: the taskbar's designed inks, or a contrast
    /// theme's (<see cref="TrayInks.HighContrast"/>). The shapes are the same
    /// either way but for ready and off, which a contrast theme's inks draw
    /// in the Mac's <c>mic</c> and <c>mic.slash</c> (<see cref="TrayInks.ByShape"/>).</summary>
    public static byte[] Render(TrayIcon state, TrayInks inks)
    {
        var ink = InkOf(state) switch
        {
            TrayInk.Recording => inks.RecordingInk,
            TrayInk.Paused => inks.PausedInk,
            _ => inks.Ink,
        };
        var opacity = state == TrayIcon.Off ? inks.PausedAlpha / 255.0 : 1.0;
        var images = Sizes.Select(size => Dib(size, state, inks.ByShape, ink, opacity)).ToArray();

        const int directory = 6;
        const int entry = 16;
        var offset = directory + entry * images.Length;
        var ico = new byte[offset + images.Sum(i => i.Length)];
        var span = ico.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)images.Length);
        for (var i = 0; i < images.Length; i++)
        {
            var at = span[(directory + entry * i)..];
            at[0] = (byte)Sizes[i];
            at[1] = (byte)Sizes[i];
            BinaryPrimitives.WriteUInt16LittleEndian(at[4..], 1); // planes
            BinaryPrimitives.WriteUInt16LittleEndian(at[6..], 32); // bits per pixel
            BinaryPrimitives.WriteUInt32LittleEndian(at[8..], (uint)images[i].Length);
            BinaryPrimitives.WriteUInt32LittleEndian(at[12..], (uint)offset);
            images[i].CopyTo(ico, offset);
            offset += images[i].Length;
        }

        return ico;
    }

    /// <summary>Which of an icon's inks <paramref name="state"/> is drawn
    /// in: the open microphone's, the paused one's, or the ink every other
    /// picture shares. The window's header asks too, so under High Contrast
    /// its glyph takes the system colour the icon's picture does
    /// (<see cref="TrayInks.HighContrast"/>).</summary>
    public static TrayInk InkOf(TrayIcon state) => state switch
    {
        TrayIcon.Listening => TrayInk.Recording,
        TrayIcon.Off => TrayInk.Paused,
        _ => TrayInk.Ink,
    };

    /// <summary>
    /// The picture for <paramref name="state"/> as coverage alone, one byte a
    /// pixel (0 to 255), top row first: what the window's header draws its
    /// glyph through (an opacity mask over the tint), so the window shows the
    /// icon's own picture. Fading and ink are the caller's.
    /// </summary>
    /// <param name="state">Which picture.</param>
    /// <param name="size">Its width and height in pixels.</param>
    /// <param name="byShape">The microphone states in the Mac's three shapes,
    /// as the icon draws them under High Contrast (<see cref="TrayInks.ByShape"/>).</param>
    public static byte[] Mask(TrayIcon state, int size, bool byShape = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var mask = new byte[size * size];
        var pixel = 1.0 / size;
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                mask[row * size + column] =
                    (byte)Math.Round(255 * Coverage(state, (column + 0.5) * pixel, (row + 0.5) * pixel, pixel, byShape));
            }
        }

        return mask;
    }

    /// <summary>How much of the pixel centred at (<paramref name="x"/>,
    /// <paramref name="y"/>) in a unit square the picture for
    /// <paramref name="state"/> covers, 0 to 1.</summary>
    /// <param name="byShape">The microphone states in the Mac's three
    /// shapes, as under High Contrast (<see cref="TrayInks.ByShape"/>).</param>
    public static double Coverage(TrayIcon state, double x, double y, double pixel, bool byShape = false)
    {
        Func<double, double, bool> shape = (state, byShape) switch
        {
            (TrayIcon.Transcribing, _) => IsWave,
            (TrayIcon.MicrophoneBlocked, _) => IsWarning,
            (TrayIcon.Ready, true) => IsOutline,
            (TrayIcon.Off, true) => IsSlashed,
            _ => IsInk,
        };

        // 4 x 4 samples: enough for a smooth edge at 16 px, and exact enough
        // that a test can find the capsule's centre fully inked.
        const int n = 4;
        var inside = 0;
        for (var sy = 0; sy < n; sy++)
        {
            for (var sx = 0; sx < n; sx++)
            {
                if (shape(x + ((sx + 0.5) / n - 0.5) * pixel, y + ((sy + 0.5) / n - 0.5) * pixel))
                {
                    inside++;
                }
            }
        }

        return inside / (double)(n * n);
    }

    /// <summary>The waveform, in the same unit square: five upright bars with
    /// round ends, tallest in the middle. The macOS menu-bar item draws the
    /// same idea (the <c>waveform</c> symbol) while it transcribes; a shape,
    /// not only a colour, so the state reads on either taskbar and to anyone
    /// who cannot tell the inks apart.</summary>
    private static bool IsWave(double x, double y)
    {
        const double centre = 0.5;
        const double halfWidth = 0.045;
        ReadOnlySpan<double> columns = [0.18, 0.34, 0.5, 0.66, 0.82];
        ReadOnlySpan<double> halfHeights = [0.10, 0.24, 0.36, 0.24, 0.10];
        for (var i = 0; i < columns.Length; i++)
        {
            // A vertical segment grown by its half-width, like the capsule.
            var half = halfHeights[i];
            if (Distance(x, y, columns[i], Math.Clamp(y, centre - half, centre + half)) <= halfWidth)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The warning, in the same unit square: a triangle's outline
    /// standing on its base, with an exclamation mark inside, a bar with round
    /// ends over a dot. The macOS menu-bar item draws the same symbol
    /// (<c>exclamationmark.triangle</c>) when a grant is missing, in the menu
    /// bar's ink; a shape, not a colour, so it is told from ready on either
    /// taskbar and by anyone who cannot tell inks apart.</summary>
    private static bool IsWarning(double x, double y)
    {
        // The base sits on a 16-pixel row's centre, and the mark is two
        // 16-pixel columns wide on the centre line, so the smallest size,
        // the one 100 % scaling picks, has its bottom edge and its mark crisp.
        const double stroke = 0.085;
        const double apexY = 0.08;
        const double baseY = 0.90625;
        const double halfBase = 0.44;
        const double markHalfWidth = 0.0625;

        // The outline: within half a stroke of an edge, so the corners come
        // out round, as the symbol's are.
        if (Segment(x, y, 0.5, apexY, 0.5 - halfBase, baseY) <= stroke / 2
            || Segment(x, y, 0.5, apexY, 0.5 + halfBase, baseY) <= stroke / 2
            || Segment(x, y, 0.5 - halfBase, baseY, 0.5 + halfBase, baseY) <= stroke / 2)
        {
            return true;
        }

        // The mark: a bar with round ends, a gap, then a dot.
        return Distance(x, y, 0.5, Math.Clamp(y, 0.40, 0.58)) <= markHalfWidth
            || Distance(x, y, 0.5, 0.76) <= markHalfWidth;
    }

    /// <summary>How far (<paramref name="x"/>, <paramref name="y"/>) is from
    /// the segment between the two points.</summary>
    private static double Segment(double x, double y, double x0, double y0, double x1, double y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var t = Math.Clamp(((x - x0) * dx + (y - y0) * dy) / (dx * dx + dy * dy), 0, 1);
        return Distance(x, y, x0 + t * dx, y0 + t * dy);
    }

    /// <summary>The microphone, in a unit square with y pointing down: a
    /// capsule, the cradle under it, a stem and a base.</summary>
    private static bool IsInk(double x, double y) => IsMicrophone(x, y, hollow: false);

    /// <summary>The microphone with its capsule outlined, not filled: the
    /// Mac's <c>mic</c>, beside <see cref="IsInk"/>'s <c>mic.fill</c>.</summary>
    private static bool IsOutline(double x, double y) => IsMicrophone(x, y, hollow: true);

    /// <summary>The outlined microphone struck through from top left to
    /// bottom right, with a clear gap either side of the stroke so it reads
    /// as a stroke at 16 pixels: the Mac's <c>mic.slash</c>.</summary>
    private static bool IsSlashed(double x, double y)
    {
        const double stroke = 0.085;
        const double gap = 0.065;
        var distance = Segment(x, y, 0.16, 0.08, 0.84, 0.92);
        if (distance <= stroke / 2)
        {
            return true;
        }

        return distance > stroke / 2 + gap && IsOutline(x, y);
    }

    private static bool IsMicrophone(double x, double y, bool hollow)
    {
        const double centre = 0.5;
        const double stroke = 0.075;

        // Capsule: a vertical segment grown by its radius; hollow, only the
        // band one stroke wide inside its edge.
        const double capsuleRadius = 0.16;
        var fromAxis = Distance(x, y, centre, Math.Clamp(y, 0.26, 0.46));
        if (fromAxis <= capsuleRadius && (!hollow || fromAxis >= capsuleRadius - stroke))
        {
            return true;
        }

        // Cradle: the lower half of a ring around the capsule, with short arms.
        const double cradleY = 0.46;
        const double cradleRadius = 0.28;
        if (y >= cradleY && Math.Abs(Distance(x, y, centre, cradleY) - cradleRadius) <= stroke / 2)
        {
            return true;
        }

        if (y is >= 0.36 and < cradleY && Math.Abs(Math.Abs(x - centre) - cradleRadius) <= stroke / 2)
        {
            return true;
        }

        // Stem, from the bottom of the cradle down to the base.
        if (Math.Abs(x - centre) <= stroke / 2 && y is >= 0.72 and <= 0.88)
        {
            return true;
        }

        // Base: a short bar with round ends.
        return Distance(x, y, Math.Clamp(x, 0.34, 0.66), 0.88) <= stroke / 2;
    }

    private static double Distance(double x0, double y0, double x1, double y1) =>
        Math.Sqrt((x0 - x1) * (x0 - x1) + (y0 - y1) * (y0 - y1));

    /// <summary>One size: <c>BITMAPINFOHEADER</c>, bottom-up BGRA pixels, and
    /// an all-zero AND mask.</summary>
    private static byte[] Dib(int size, TrayIcon state, bool byShape, uint ink, double opacity)
    {
        const int header = 40;
        var pixels = size * size * 4;
        var maskRow = (size + 31) / 32 * 4;
        var dib = new byte[header + pixels + maskRow * size];
        var span = dib.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, header);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], size * 2); // XOR + AND
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(pixels + maskRow * size));

        var pixel = 1.0 / size;
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                var coverage = Coverage(state, (column + 0.5) * pixel, (row + 0.5) * pixel, pixel, byShape);
                var alpha = (byte)Math.Round((ink >> 24) * coverage * opacity);
                // Bottom-up: the first stored row is the image's last.
                var at = header + ((size - 1 - row) * size + column) * 4;
                // Premultiplied is not required for icons; straight BGRA.
                dib[at] = (byte)ink;
                dib[at + 1] = (byte)(ink >> 8);
                dib[at + 2] = (byte)(ink >> 16);
                dib[at + 3] = alpha;
            }
        }

        return dib;
    }
}
