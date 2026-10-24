using Dictation.Services;
using Dictation.Speech;

namespace Dictation.Core.Tests;

/// <summary>
/// Every picture the notification-area icon can show, on a light taskbar, on
/// a dark one and under High Contrast: each state drawn, legible and told
/// apart from every other. Under High Contrast the colours are the user's,
/// and Microsoft's guidance for contrast themes is that users can, and do,
/// modify them, so there the states are told apart by shape alone, whatever
/// colours the theme gives, the Mac's <c>mic</c>, <c>mic.fill</c> and
/// <c>mic.slash</c> among them (<see cref="TrayInks.ByShape"/>).
/// </summary>
public sealed class TrayIconContrastTests
{
    private const uint Text = 0x00FF_FF00;
    private const uint Selection = 0xFF1A_EBFF;
    private const uint Disabled = 0x003F_F23F;

    /// <summary>Both taskbars, a contrast theme with three colours, and
    /// contrast themes whose user set two of them, or all three, alike.</summary>
    private static readonly (string Name, TrayInks Inks)[] EveryInks =
    [
        ("light taskbar", TrayInks.For(lightTaskbar: true)),
        ("dark taskbar", TrayInks.For(lightTaskbar: false)),
        ("contrast theme", TrayInks.HighContrast(Text, Selection, Disabled)),
        ("contrast theme, disabled text as text", TrayInks.HighContrast(Text, Selection, Text)),
        ("contrast theme, selection as text", TrayInks.HighContrast(Text, Text, Disabled)),
        ("contrast theme, one colour", TrayInks.HighContrast(Text, Text, Text)),
    ];

    private static readonly TrayIcon[] Every = Enum.GetValues<TrayIcon>();

    /// <summary>The contrast themes, picked by name, not by what their
    /// inks say, so inks that lost their shapes are still checked here.</summary>
    private static (string Name, TrayInks Inks)[] Contrast
    {
        get
        {
            var themes = EveryInks.Where(i => i.Name.StartsWith("contrast", StringComparison.Ordinal)).ToArray();
            Assert.Equal(4, themes.Length);
            return themes;
        }
    }

    /// <summary>Where the alpha channel is not zero, at one size.</summary>
    private static bool[] Mask(TrayIcon state, TrayInks inks, int index)
    {
        var image = DictationDesktopTests.Image(TrayIconArt.Render(state, inks), index);
        return [.. Enumerable.Range(0, image.Size * image.Size).Select(i => image.Pixels[i * 4 + 3] > 0)];
    }

    private static int Alpha(TrayIcon state, TrayInks inks, int index, int x, int y) =>
        DictationDesktopTests.Pixel(DictationDesktopTests.Image(TrayIconArt.Render(state, inks), index), x, y).A;

    /// <summary>Every state in every inks: an icon with every size, drawn in
    /// its own ink and nothing else, with enough of it inked to read at the
    /// 16-pixel size 100 % scaling picks and at 32, and at full strength but
    /// for the designed pause, which is faded on purpose.</summary>
    [Fact]
    public void Every_state_is_drawn_legibly_in_every_inks()
    {
        foreach (var (name, inks) in EveryInks)
        {
            foreach (var state in Every)
            {
                var ico = TrayIconArt.Render(state, inks);
                Assert.Equal(TrayIconArt.Sizes.Count, ico[4]);
                var ink = state switch
                {
                    TrayIcon.Listening => inks.RecordingInk,
                    TrayIcon.Off => inks.PausedInk,
                    _ => inks.Ink,
                };
                var strongest = state == TrayIcon.Off ? inks.PausedAlpha : 255;

                var small = DictationDesktopTests.Image(ico, 0);
                var large = DictationDesktopTests.Image(ico, TrayIconArt.Sizes.Count - 1);
                Assert.Equal(16, small.Size);
                Assert.Equal(32, large.Size);
                foreach (var image in new[] { small, large })
                {
                    var alphas = Enumerable.Range(0, image.Size * image.Size).Select(i => image.Pixels[i * 4 + 3]).ToArray();
                    Assert.Equal(strongest, alphas.Max());
                    for (var i = 0; i < alphas.Length; i++)
                    {
                        if (alphas[i] > 0)
                        {
                            Assert.Equal(
                                ((byte)ink, (byte)(ink >> 8), (byte)(ink >> 16)),
                                (image.Pixels[i * 4], image.Pixels[i * 4 + 1], image.Pixels[i * 4 + 2]));
                        }
                    }
                }

                // At least half strength over a dozen pixels at 16, sixty at 32.
                var half = strongest / 2;
                Assert.True(
                    Enumerable.Range(0, 16 * 16).Count(i => small.Pixels[i * 4 + 3] >= half) >= 12,
                    $"{state} on a {name} is too faint at 16 pixels");
                Assert.True(
                    Enumerable.Range(0, 32 * 32).Count(i => large.Pixels[i * 4 + 3] >= half) >= 60,
                    $"{state} on a {name} is too faint at 32 pixels");
            }
        }
    }

    /// <summary>No two states share a picture, in any inks: a pause, a
    /// ready key and an open microphone never look alike, however the user
    /// set the contrast theme's colours.</summary>
    [Fact]
    public void Every_state_is_its_own_picture_in_every_inks()
    {
        foreach (var (name, inks) in EveryInks)
        {
            var pictures = Every.Select(state => TrayIconArt.Render(state, inks)).ToArray();
            for (var a = 0; a < pictures.Length; a++)
            {
                for (var b = a + 1; b < pictures.Length; b++)
                {
                    Assert.False(pictures[a].AsSpan().SequenceEqual(pictures[b]), $"{Every[a]} and {Every[b]} look the same on a {name}");
                }
            }
        }
    }

    /// <summary>Under High Contrast the states differ in shape at every
    /// size, not only in colour, so a theme whose colours coincide, or a
    /// reader who cannot tell them apart, still sees five pictures. On the
    /// taskbars the microphone's three states keep one shape, told apart by
    /// red and by the fade, as designed.</summary>
    [Fact]
    public void Under_High_Contrast_every_state_is_its_own_shape_whatever_the_themes_colours()
    {
        foreach (var (name, inks) in Contrast)
        {
            for (var index = 0; index < TrayIconArt.Sizes.Count; index++)
            {
                var masks = Every.Select(state => Mask(state, inks, index)).ToArray();
                for (var a = 0; a < masks.Length; a++)
                {
                    for (var b = a + 1; b < masks.Length; b++)
                    {
                        Assert.False(
                            masks[a].SequenceEqual(masks[b]),
                            $"{Every[a]} and {Every[b]} are one shape at {TrayIconArt.Sizes[index]} px in a {name}");
                    }
                }
            }
        }

        var dark = TrayInks.For(lightTaskbar: false);
        Assert.Equal(Mask(TrayIcon.Ready, dark, 3), Mask(TrayIcon.Listening, dark, 3));
        Assert.Equal(Mask(TrayIcon.Ready, dark, 3), Mask(TrayIcon.Off, dark, 3));
    }

    /// <summary>The Mac's shapes, at 32 and at 16 pixels: the ready
    /// capsule is hollow where the open one is filled, and only the pause is
    /// struck through. The waveform, the warning and the open microphone are
    /// the designed shapes.</summary>
    [Fact]
    public void Under_High_Contrast_the_microphone_takes_the_Macs_three_shapes()
    {
        var contrast = TrayInks.HighContrast(Text, Selection, Disabled);
        var dark = TrayInks.For(lightTaskbar: false);

        // The capsule's centre: 32 px (16, 11), 16 px (8, 5).
        Assert.Equal(0, Alpha(TrayIcon.Ready, contrast, 3, 16, 11));
        Assert.Equal(255, Alpha(TrayIcon.Listening, contrast, 3, 16, 11));
        Assert.Equal(0, Alpha(TrayIcon.Ready, contrast, 0, 8, 5));
        Assert.Equal(255, Alpha(TrayIcon.Listening, contrast, 0, 8, 5));
        // The capsule's edge is drawn in both.
        Assert.Equal(255, Alpha(TrayIcon.Ready, contrast, 3, 11, 9));

        // The slash, near the top left corner, where no other picture draws.
        Assert.Equal(255, Alpha(TrayIcon.Off, contrast, 3, 5, 3));
        foreach (var state in Every.Where(s => s != TrayIcon.Off))
        {
            Assert.Equal(0, Alpha(state, contrast, 3, 5, 3));
        }

        foreach (var state in new[] { TrayIcon.Listening, TrayIcon.Transcribing, TrayIcon.MicrophoneBlocked })
        {
            for (var index = 0; index < TrayIconArt.Sizes.Count; index++)
            {
                Assert.Equal(Mask(state, dark, index), Mask(state, contrast, index));
            }
        }
    }

    /// <summary>
    /// Every state the icon resolves to (<see cref="DictationTrayGlyph"/>),
    /// the hook watch's and the microphone watch's included, draws under High
    /// Contrast the picture it shares on the taskbar: a hook Windows would not
    /// put back the warning, as a blocked microphone; an administrator window
    /// and a reconnected hook the ready microphone, their own tooltips saying
    /// why. And the five kinds stay five shapes there.
    /// </summary>
    /// <summary>The icon for the controller's state: the two calls its
    /// render makes, <see cref="DictationTrayGlyph.State"/> then
    /// <see cref="DictationTrayGlyph.Picture"/>.</summary>
    private static (TrayIcon Icon, string ToolTip) Resolved(
        bool armed,
        bool enabling,
        DictationState shown,
        bool microphoneBlocked,
        HotkeyBinding key,
        HookHealth hook = HookHealth.Healthy,
        bool adminWindowInFront = false) =>
        DictationTrayGlyph.Picture(
            DictationTrayGlyph.State(armed, enabling, shown, microphoneBlocked, hook, adminWindowInFront), key);

    [Fact]
    public void Every_state_the_icon_resolves_to_has_its_picture_under_High_Contrast()
    {
        var key = DictationHotkeys.PushToTalk;
        (string Name, TrayIcon Expected, (TrayIcon Icon, string ToolTip) Resolved)[] states =
        [
            ("listening", TrayIcon.Listening, Resolved(true, false, DictationState.Listening, false, key)),
            ("transcribing", TrayIcon.Transcribing, Resolved(true, false, DictationState.Transcribing, false, key)),
            ("typing", TrayIcon.Transcribing, Resolved(true, false, DictationState.Inserting, false, key)),
            ("blocked, on", TrayIcon.MicrophoneBlocked, Resolved(true, false, DictationState.Armed, true, key)),
            ("blocked, off", TrayIcon.MicrophoneBlocked, Resolved(false, false, DictationState.Idle, true, key)),
            ("hook not put back", TrayIcon.MicrophoneBlocked,
                Resolved(true, false, DictationState.Armed, false, key, HookHealth.Unheard)),
            ("administrator window", TrayIcon.Ready,
                Resolved(true, false, DictationState.Armed, false, key, adminWindowInFront: true)),
            ("hook reconnected", TrayIcon.Ready,
                Resolved(true, false, DictationState.Armed, false, key, HookHealth.Reconnected)),
            ("ready", TrayIcon.Ready, Resolved(true, false, DictationState.Armed, false, key)),
            ("starting", TrayIcon.Off, Resolved(false, true, DictationState.Idle, false, key)),
            ("off", TrayIcon.Off, Resolved(false, false, DictationState.Idle, false, key)),
        ];

        foreach (var (_, inks) in Contrast)
        {
            foreach (var (name, expected, resolved) in states)
            {
                Assert.True(expected == resolved.Icon, $"{name} resolved to {resolved.Icon}");
                Assert.Equal(TrayIconArt.Render(expected, inks), TrayIconArt.Render(resolved.Icon, inks));
            }

            var kinds = states.Select(s => s.Resolved.Icon).Distinct().Select(icon => Mask(icon, inks, 0)).ToArray();
            Assert.Equal(Every.Length, kinds.Length);
            Assert.Equal(kinds.Length, kinds.Select(m => string.Concat(m.Select(b => b ? '1' : '0'))).Distinct().Count());
        }
    }
}
