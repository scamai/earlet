using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Dictation.Core;
using Dictation.Speech;

namespace Dictation.App;

// Part of DictationHudWindow (see DictationHudWindow.xaml.cs): the pill under
// Windows' High Contrast. While a contrast theme is on, every part is a
// resource reference to a system brush (DictationHudContrast decides which);
// off, the markup's designed palette is put back as it was.
public partial class DictationHudWindow
{
    /// <summary>The designed look, kept to put back when High Contrast goes
    /// off: the markup's brushes and shadow.</summary>
    private (Brush Capsule, Brush Border, Brush Title, Brush Detail, Brush Caption, Brush Level, Effect Shadow) designed;

    /// <summary>The contrast theme's inks the pill is drawn in, or null while
    /// it wears the designed palette.</summary>
    private DictationHudContrastInks? contrast;

    /// <summary>The tone of the pill shown, whose dot a change of contrast
    /// paints again.</summary>
    private HudTone tone;

    /// <summary>The indicator render mode's High Contrast for the appearance
    /// it draws (<see cref="DictationRender.HighContrast(DictationRenderAppearance)"/>),
    /// in place of the render machine's own; null in the app, which follows
    /// Windows.</summary>
    internal bool? HighContrastForRender { get; set; }

    /// <summary>Remember the designed look, wear the right one now, and
    /// follow High Contrast going on or off until the window closes. Called
    /// once, from the constructor, once the markup's brushes are in place.</summary>
    private void WatchContrast()
    {
        designed = (Capsule.Background, Capsule.BorderBrush, StatusLine.Foreground, DetailLine.Foreground,
            CaptionLine.Foreground, FlatLine.Fill, Capsule.Effect);
        FollowContrast();
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }

    /// <summary>A show in <paramref name="shown"/>'s tone: High Contrast
    /// read again, then the dot painted for the tone.</summary>
    private void PaintFor(HudTone shown)
    {
        tone = shown;
        FollowContrast();
        PaintMark();
    }

    /// <summary>
    /// The pill in a contrast theme's colours while High Contrast is on, and
    /// in the designed palette while it is off (<see cref="DictationHudContrast"/>).
    /// Read on every show and when WPF says the setting changed; does nothing
    /// when the pill already wears the right one.
    /// </summary>
    private void FollowContrast()
    {
        var wanted = DictationHudContrast.For(HighContrastForRender ?? SystemParameters.HighContrast);
        if (wanted == contrast)
        {
            return;
        }

        contrast = wanted;
        if (wanted is null)
        {
            // Local values again, which replace the resource references.
            Capsule.Background = designed.Capsule;
            Capsule.BorderBrush = designed.Border;
            StatusLine.Foreground = designed.Title;
            DetailLine.Foreground = designed.Detail;
            CaptionLine.Foreground = designed.Caption;
            foreach (var level in bars.Append(FlatLine))
            {
                level.Fill = designed.Level;
            }

            Capsule.Effect = designed.Shadow;
        }
        else
        {
            Paint(Capsule, Border.BackgroundProperty, wanted.Capsule);
            Paint(Capsule, Border.BorderBrushProperty, wanted.Border);
            foreach (var line in new TextBlock[] { StatusLine, DetailLine, CaptionLine })
            {
                Paint(line, TextBlock.ForegroundProperty, wanted.Text);
            }

            foreach (var level in bars.Append(FlatLine))
            {
                Paint(level, Shape.FillProperty, wanted.Level);
            }

            Capsule.Effect = wanted.Shadow ? designed.Shadow : null;
        }

        PaintMark();
    }

    /// <summary>The dot in its tone: violet or orange as designed, or the
    /// contrast theme's text or selection colour.</summary>
    private void PaintMark()
    {
        var caution = tone == HudTone.Caution;
        if (contrast is { } inks)
        {
            Paint(Mark, Shape.FillProperty, caution ? inks.CautionMark : inks.NeutralMark);
        }
        else
        {
            Mark.Fill = caution ? CautionMark : NeutralMark;
        }
    }

    /// <summary>A reference to the system brush for <paramref name="color"/>,
    /// which follows a change of contrast theme by itself.</summary>
    private static void Paint(FrameworkElement element, DependencyProperty property, HudSystemColor color) =>
        element.SetResourceReference(property, color switch
        {
            HudSystemColor.Window => SystemColors.WindowBrushKey,
            HudSystemColor.WindowText => SystemColors.WindowTextBrushKey,
            _ => SystemColors.HighlightBrushKey,
        });

    /// <summary>High Contrast went on or off: WPF's notice, raised from the
    /// <c>WM_SETTINGCHANGE</c> that also raises <c>UserPreferenceChanged</c>.</summary>
    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (closed || e.PropertyName != nameof(SystemParameters.HighContrast))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            FollowContrast();
        }
        else
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!closed)
                {
                    FollowContrast();
                }
            });
        }
    }
}
