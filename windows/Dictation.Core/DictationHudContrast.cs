namespace Dictation.Core;

/// <summary>A Windows system colour the indicator draws with under High
/// Contrast: the user's contrast theme decides what each one is.</summary>
public enum HudSystemColor
{
    /// <summary>The theme's background (<c>SystemColors.Window</c>).</summary>
    Window,

    /// <summary>The theme's text on that background (<c>SystemColors.WindowText</c>).</summary>
    WindowText,

    /// <summary>The theme's selection colour (<c>SystemColors.Highlight</c>),
    /// which every contrast theme sets apart from its background and text.</summary>
    Highlight,
}

/// <summary>What each part of the pill is drawn in under High Contrast.</summary>
/// <param name="Capsule">The capsule's fill.</param>
/// <param name="Border">Its hairline, which is all that outlines it then.</param>
/// <param name="Text">Both lines and the live preview's caption, at full strength.</param>
/// <param name="NeutralMark">The dot of an ordinary line.</param>
/// <param name="CautionMark">The dot of a line that needs reading: set apart
/// from the neutral one, as orange is from violet in the designed palette.</param>
/// <param name="Level">The level bars and the flat line.</param>
/// <param name="Shadow">Whether the drop shadow is drawn.</param>
public sealed record DictationHudContrastInks(
    HudSystemColor Capsule,
    HudSystemColor Border,
    HudSystemColor Text,
    HudSystemColor NeutralMark,
    HudSystemColor CautionMark,
    HudSystemColor Level,
    bool Shadow);

/// <summary>
/// The pill under Windows' High Contrast (Settings → Accessibility →
/// Contrast themes, read as <c>SystemParameters.HighContrast</c>): the
/// theme's own colours in place of the designed ones.
/// </summary>
/// <remarks>
/// <para>
/// The designed pill is always dark (#F21C1C1F) with violet and orange marks
/// and a translucent second line, chosen to read on any wallpaper. A contrast
/// theme exists so its user's colours are the ones on screen, and none of
/// those fixed colours is one of them. The Mac's capsule follows the system
/// through its materials; Windows has to be told.
/// </para>
/// <para>
/// Outside High Contrast nothing changes: the palette is the owner's
/// decision and stays as designed (<see cref="For"/> answers null). Pure, so
/// the choice of roles is checked on Linux; the window maps each role to the
/// system brush and follows a change of theme while it runs.
/// </para>
/// </remarks>
public static class DictationHudContrast
{
    /// <summary>The theme's text on the theme's background, a hairline in
    /// the text's colour around it, the selection colour for the caution
    /// mark and the level, and no shadow (a contrast theme draws none).</summary>
    public static readonly DictationHudContrastInks HighContrast = new(
        Capsule: HudSystemColor.Window,
        Border: HudSystemColor.WindowText,
        Text: HudSystemColor.WindowText,
        NeutralMark: HudSystemColor.WindowText,
        CautionMark: HudSystemColor.Highlight,
        Level: HudSystemColor.Highlight,
        Shadow: false);

    /// <summary>The inks for the pill, or null for the designed palette.</summary>
    public static DictationHudContrastInks? For(bool highContrast) => highContrast ? HighContrast : null;
}

/// <summary>Which of <see cref="TrayInks"/> a picture is drawn in
/// (<see cref="TrayIconArt.InkOf"/>).</summary>
public enum TrayInk
{
    /// <summary><see cref="TrayInks.Ink"/>: ready, transcribing and the
    /// warning; under High Contrast the theme's text colour.</summary>
    Ink,

    /// <summary><see cref="TrayInks.RecordingInk"/>: the open microphone;
    /// under High Contrast the theme's selection colour.</summary>
    Recording,

    /// <summary><see cref="TrayInks.PausedInk"/>: off and starting; under
    /// High Contrast the theme's disabled-text colour.</summary>
    Paused,
}

/// <summary>The inks one notification-area icon is drawn in.</summary>
/// <param name="Ink">Ready, transcribing and the warning.</param>
/// <param name="RecordingInk">The microphone while it is open.</param>
/// <param name="PausedInk">The microphone while dictation is paused.</param>
/// <param name="PausedAlpha">How opaque the paused icon is, out of 255.</param>
/// <param name="ByShape">Ready, listening and off are told apart by shape
/// too, the Mac's <c>mic</c>, <c>mic.fill</c> and <c>mic.slash</c>
/// (<see cref="TrayIconArt"/>): a contrast theme's colours are the user's,
/// who may set two of them alike, and its pause cannot be faded. False for
/// the taskbar's designed inks, whose red and fade keep their pictures.</param>
public readonly record struct TrayInks(uint Ink, uint RecordingInk, uint PausedInk, byte PausedAlpha, bool ByShape = false)
{
    /// <summary>The designed inks for the taskbar's theme: its own ink, red
    /// while the microphone is open, and the ink faded while paused.</summary>
    public static TrayInks For(bool lightTaskbar)
    {
        var ink = lightTaskbar ? TrayIconArt.DarkInk : TrayIconArt.LightInk;
        return new TrayInks(ink, TrayIconArt.RecordingInk, ink, TrayIconArt.PausedAlpha);
    }

    /// <summary>
    /// The contrast theme's inks: its text colour, its selection colour while
    /// the microphone is open, and its disabled-text colour, at full strength,
    /// while paused (a faded ink is exactly what a contrast theme exists to
    /// prevent). Each is made opaque: a system colour has no alpha. The
    /// microphone's states take their shapes (<see cref="ByShape"/>).
    /// </summary>
    /// <param name="windowText"><c>SystemColors.WindowText</c>, as ARGB.</param>
    /// <param name="highlight"><c>SystemColors.Highlight</c>, as ARGB.</param>
    /// <param name="grayText"><c>SystemColors.GrayText</c>, as ARGB.</param>
    public static TrayInks HighContrast(uint windowText, uint highlight, uint grayText) =>
        new(Opaque(windowText), Opaque(highlight), Opaque(grayText), 255, ByShape: true);

    /// <summary>The contrast theme's inks while High Contrast is on, whatever
    /// the taskbar's theme; otherwise the designed ones for it.</summary>
    /// <param name="highContrast">Whether High Contrast is on.</param>
    /// <param name="lightTaskbar">Whether the taskbar is light.</param>
    /// <param name="system">The theme's text, selection and disabled-text
    /// colours, read only when High Contrast is on.</param>
    public static TrayInks Choose(bool highContrast, bool lightTaskbar, Func<(uint WindowText, uint Highlight, uint GrayText)> system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!highContrast)
        {
            return For(lightTaskbar);
        }

        var (windowText, highlight, grayText) = system();
        return HighContrast(windowText, highlight, grayText);
    }

    private static uint Opaque(uint argb) => argb | 0xFF00_0000;
}
