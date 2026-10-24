using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Dictation.Core;

namespace Dictation.App;

// Part of MainWindow (see MainWindow.xaml.cs): the status header (the glyph,
// the headline, Enable/Pause as one button, Cancel dictation while there is
// something to cancel) and Troubleshooting, the Mac's DictationStatusHeader and
// Troubleshooting disclosure. Built and pinned on Linux; never seen on Windows.
public partial class MainWindow
{
    /// <summary>The glyph's mask is drawn at this many pixels a side and
    /// scaled to the header's 28 DIP: sharp at 200 %.</summary>
    private const int GlyphPixels = 64;

    /// <summary>Green for on: Windows' own success colour, readable on the
    /// window's light background.</summary>
    private static readonly Brush PositiveInk = Frozen(Color.FromRgb(0x0F, 0x7B, 0x0F));

    /// <summary>Red while the microphone is open: the icon's own red.</summary>
    private static readonly Brush RecordingInk = Frozen(Color.FromArgb(
        unchecked((byte)(TrayIconArt.RecordingInk >> 24)),
        unchecked((byte)(TrayIconArt.RecordingInk >> 16)),
        unchecked((byte)(TrayIconArt.RecordingInk >> 8)),
        unchecked((byte)TrayIconArt.RecordingInk)));

    /// <summary>Orange for something to fix: Windows' caution colour.</summary>
    private static readonly Brush AttentionInk = Frozen(Color.FromRgb(0x9D, 0x5D, 0x00));

    /// <summary>This copy: the identity the launch line logs (App.OnStartup).</summary>
    private readonly DictationInstallation installation =
        DictationInstallation.Of(typeof(App).Assembly, Environment.ProcessPath);

    /// <summary>One frozen mask per picture and per drawing: the designed
    /// shapes, or the Mac's three microphones under High Contrast.</summary>
    private readonly Dictionary<(TrayIcon Picture, bool ByShape), ImageBrush> glyphMasks = [];

    /// <summary>The header as last drawn: what a click on Enable/Pause does,
    /// and what a High Contrast change repaints.</summary>
    private DictationWindowHeader? header;

    /// <summary>The window render mode's High Contrast for the appearance it
    /// draws, in place of the render machine's own
    /// (<see cref="DrawHighContrastForRender"/>); null in the app, which
    /// follows Windows.</summary>
    private bool? highContrastForRender;

    /// <summary>The header's and Troubleshooting's fixed text, from
    /// <see cref="DictationCopy"/>; the access keys are this window's.</summary>
    private void ShowHeaderAndTroubleshootingLabels()
    {
        CancelButton.Content = WithAccessKey(DictationCopy.CancelLabel, "C");
        TroubleshootingSection.Header = WithAccessKey(DictationCopy.TroubleshootingHeading, "b");
        AutomationProperties.SetName(TroubleshootingSection, DictationCopy.TroubleshootingHeading);
        ThisCopyHeading.Text = DictationCopy.ThisCopyHeading;
        ThisCopyVersion.Text = installation.VersionLabel;
        ThisCopyPath.Text = installation.Path;
        AutomationProperties.SetName(ThisCopyPath, DictationCopy.ThisCopyPathName);
        ShowThisCopyButton.Content = WithAccessKey(DictationCopy.ShowThisCopy, "x");
        ShowThisCopyButton.IsEnabled = installation.PathKnown;

        // High Contrast on or off: the glyph takes the new colours and shapes at once,
        // not at the next change of state. The window lives as long as the
        // process, so the static event is never let go.
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast) && header is { } shown)
            {
                RenderGlyph(shown.Glyph);
            }
        };
    }

    /// <summary>The glyph, the headline and the one Enable/Pause button.</summary>
    private void RenderHeader(DictationWindowHeader state)
    {
        header = state;
        RenderGlyph(state.Glyph);
        // Not a live region: the status line under it is the window's voice,
        // and both would be read for every change.
        Headline.Text = state.Headline;
        EnablePauseButton.Content = WithAccessKey(state.ToggleLabel, state.ToggleLabel[..1]);
        EnablePauseButton.IsEnabled = state.CanToggle;
    }

    /// <summary>
    /// The icon's picture (<see cref="TrayIconArt.Mask"/>) as an opacity mask
    /// over the tint: the Mac header's colours; or, in High Contrast, where
    /// only system colours are certain to be seen, the icon's own drawing
    /// there — the Mac's three microphone shapes, since the theme's colours
    /// may coincide, each in the system colour the icon's picture takes
    /// (<see cref="TrayIconArt.InkOf"/>: text, selection while the microphone
    /// is open, disabled text while off). A resource reference, so another
    /// contrast theme's colours apply at once.
    /// </summary>
    private void RenderGlyph(DictationGlyph glyph)
    {
        var picture = DictationTrayGlyph.IconOf(glyph);
        var highContrast = highContrastForRender ?? SystemParameters.HighContrast;
        StateGlyph.OpacityMask = MaskFor(picture, byShape: highContrast);
        if (highContrast)
        {
            StateGlyph.SetResourceReference(Shape.FillProperty, TrayIconArt.InkOf(picture) switch
            {
                TrayInk.Recording => SystemColors.HighlightBrushKey,
                TrayInk.Paused => SystemColors.GrayTextBrushKey,
                _ => SystemColors.WindowTextBrushKey,
            });
            return;
        }

        switch (DictationTrayGlyph.Tint(glyph))
        {
            case DictationGlyphTint.Positive:
                StateGlyph.Fill = PositiveInk;
                break;
            case DictationGlyphTint.Recording:
                StateGlyph.Fill = RecordingInk;
                break;
            case DictationGlyphTint.Accent:
                StateGlyph.SetResourceReference(Shape.FillProperty, SystemColors.HighlightBrushKey);
                break;
            case DictationGlyphTint.Attention:
                StateGlyph.Fill = AttentionInk;
                break;
            default:
                StateGlyph.SetResourceReference(Shape.FillProperty, SystemColors.GrayTextBrushKey);
                break;
        }
    }

    /// <summary>The picture's coverage as premultiplied white: only the alpha
    /// counts in a mask. One frozen brush per picture and drawing.</summary>
    private ImageBrush MaskFor(TrayIcon picture, bool byShape)
    {
        if (!glyphMasks.TryGetValue((picture, byShape), out var brush))
        {
            var coverage = TrayIconArt.Mask(picture, GlyphPixels, byShape);
            var pixels = new byte[coverage.Length * 4];
            for (var i = 0; i < coverage.Length; i++)
            {
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = pixels[i * 4 + 3] = coverage[i];
            }

            var bitmap = BitmapSource.Create(
                GlyphPixels, GlyphPixels, 96, 96, PixelFormats.Pbgra32, null, pixels, GlyphPixels * 4);
            bitmap.Freeze();
            brush = new ImageBrush(bitmap) { Stretch = Stretch.Uniform };
            brush.Freeze();
            glyphMasks[(picture, byShape)] = brush;
        }

        return brush;
    }

    /// <summary>What the button said when it was drawn, never the other: a
    /// click on Pause cannot enable. Pause goes through the menu's refusal
    /// under a dictation (TryPause), one rule.</summary>
    private async void EnablePauseClick(object sender, RoutedEventArgs e)
    {
        switch (header)
        {
            case { ToggleEnables: true }:
                await controller.EnableAsync();
                break;
            case { ToggleEnables: false }:
                controller.TryPause();
                break;
        }
    }

    private void ShowThisCopyClick(object sender, RoutedEventArgs e) => controller.ShowThisCopy(installation.Path);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
