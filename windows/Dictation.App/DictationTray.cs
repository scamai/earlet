using System.IO;
using Dictation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Dictation.App;

/// <summary>
/// The notification-area icon and its menu. The menu is
/// <see cref="DictationTrayMenu"/>'s, in its order: the recent result's lines,
/// the learning receipt's, Enable or Pause and Cancel, Show Dictation (bold,
/// and what a left click does) and Open log folder, then Quit. Draws what
/// <see cref="DictationTrayState"/> and the Vocabulary section say and raises
/// <see cref="Chosen"/>; what an item does is <see cref="DictationTrayCommands"/>'s.
/// </summary>
/// <remarks>
/// <para>
/// Windows Forms' <c>NotifyIcon</c>, hosted on the WPF dispatcher thread,
/// which pumps the messages its hidden window needs. The pictures come from
/// <see cref="TrayIconArt"/>, drawn for the taskbar's current theme, or in a
/// contrast theme's own colours while High Contrast is on
/// (<see cref="TrayInks.Choose"/>), and redrawn when the user changes either.
/// </para>
/// <para>
/// The menu is redrawn on every change, open or not, so a menu left open
/// while the state moves on (a dictation ends, a receipt arrives) does not
/// keep offering what is no longer true. What is shown, and whether a change
/// needs a redraw, is <see cref="DictationTrayMenuState"/>'s, tested on Linux;
/// this class only applies it. Every line it can ever show is made
/// once, one per <see cref="DictationTrayMenu.Slots"/> entry, and stays in the
/// strip; a redraw sets each one's text, enabled state and
/// <c>Available</c>, as the menu did before it had a model, and never
/// removes a line, so the item whose click caused the redraw keeps its
/// parent. How an open strip looks while its lines change has never been
/// seen on Windows.
/// </para>
/// </remarks>
internal sealed class DictationTray : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly ILogger<DictationTray> log;
    private readonly System.Windows.Forms.NotifyIcon icon;
    private readonly System.Windows.Forms.ContextMenuStrip menu = new();
    private readonly System.Drawing.Font boldFont =
        new(System.Windows.Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold);

    /// <summary>One line per <see cref="DictationTrayMenu.Slots"/> entry, in
    /// the strip for the menu's whole life.</summary>
    private readonly System.Windows.Forms.ToolStripItem[] lines;
    /// <summary>One picture per state, for the inks in use only: a change of
    /// inks frees the old set's icon handles.</summary>
    private readonly TrayPictures<System.Drawing.Icon> pictures = new(Draw);

    /// <summary>What the icon and the menu show, both halves.</summary>
    private readonly DictationTrayMenuState showing = new();
    private TrayInks inks;
    private bool disposed;

    /// <param name="visible">False for the window render mode
    /// (<see cref="DictationRenderMode"/>): the icon is made but never put in
    /// the notification area.</param>
    public DictationTray(ILogger<DictationTray> log, bool visible = true)
    {
        this.log = log;
        inks = ReadInks();
        lines = [.. DictationTrayMenu.Slots.Select(Line)];
        menu.Items.AddRange(lines);
        Redraw();

        icon = new System.Windows.Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = showing.Shown.ToolTip,
            Icon = Picture(showing.Shown.Icon),
            Visible = visible,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Chosen?.Invoke(this, DictationTrayMenu.DefaultItem);
            }
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>An item was chosen, or the icon left-clicked (the default item).</summary>
    public event EventHandler<DictationTrayItem>? Chosen;

    /// <summary>The controller's half: the picture, the tooltip (which
    /// carries the state; the menu has no headline), the toggle, Cancel and
    /// the recent result.</summary>
    public void Update(DictationTrayState tray)
    {
        if (disposed || !showing.Update(tray))
        {
            return;
        }

        icon.Icon = Picture(tray.Icon);
        icon.Text = tray.ToolTip;
        Redraw();
    }

    /// <summary>The Vocabulary section's half: whether a learning receipt
    /// offers Undo (<see cref="VocabularyViewState.CanUndoLearning"/>).</summary>
    public void UpdateLearning(bool undoable)
    {
        if (disposed || !showing.UpdateLearning(undoable))
        {
            return;
        }

        Redraw();
    }

    /// <summary>A one-line notice from the icon.</summary>
    public void ShowNotice(string text)
    {
        if (!disposed)
        {
            icon.ShowBalloonTip(5000, "Earlet", text, System.Windows.Forms.ToolTipIcon.None);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        // Hidden before disposal, or the icon lingers in the notification
        // area until the pointer passes over it.
        icon.Visible = false;
        // Every line is still in the strip, which disposes them with itself.
        menu.Dispose();
        icon.Dispose();
        boldFont.Dispose();
        pictures.Dispose();
    }

    /// <summary>
    /// Draw <see cref="DictationTrayMenuState.Placed"/>: each entry in its
    /// slot, its text and enabled state set, every other line hidden. The
    /// time, never the words: the menu offers Copy last text without showing
    /// what it copies.
    /// </summary>
    private void Redraw()
    {
        var placed = showing.Placed;
        for (var slot = 0; slot < lines.Length; slot++)
        {
            if (placed[slot] is { } entry && lines[slot] is System.Windows.Forms.ToolStripMenuItem line)
            {
                line.Text = entry.Label;
                line.Enabled = entry.Enabled;
            }

            lines[slot].Available = placed[slot] is not null;
        }
    }

    /// <summary>The line for one slot: a rule, or an item whose click raises
    /// <see cref="Chosen"/>, in bold for the default item.</summary>
    private System.Windows.Forms.ToolStripItem Line(DictationTrayItem item)
    {
        if (item == DictationTrayItem.Separator)
        {
            return new System.Windows.Forms.ToolStripSeparator();
        }

        var line = new System.Windows.Forms.ToolStripMenuItem();
        if (item == DictationTrayMenu.DefaultItem)
        {
            line.Font = boldFont;
        }

        line.Click += (_, _) => Chosen?.Invoke(this, item);
        return line;
    }

    private System.Drawing.Icon Picture(TrayIcon state) => pictures.For(state, inks);

    private static System.Drawing.Icon Draw(TrayIcon state, TrayInks inks)
    {
        using var bytes = new MemoryStream(TrayIconArt.Render(state, inks));
        return new System.Drawing.Icon(bytes, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Raised on the thread that subscribed, which is the dispatcher's. A
        // taskbar theme change is General; turning High Contrast on or off,
        // or choosing another contrast theme, is Accessibility or Color.
        if (disposed || e.Category is not (UserPreferenceCategory.General
            or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color))
        {
            return;
        }

        var now = ReadInks();
        if (now != inks)
        {
            // The old inks' pictures are freed as the new one is drawn, and
            // the new one goes up in the same turn.
            inks = now;
            icon.Icon = Picture(showing.Shown.Icon);
        }
    }

    /// <summary>The inks for now: a contrast theme's while High Contrast is
    /// on, read from the system colours each time, otherwise the taskbar's.</summary>
    private TrayInks ReadInks() => TrayInks.Choose(
        System.Windows.Forms.SystemInformation.HighContrast,
        ReadLightTaskbar(),
        () => (Argb(System.Drawing.SystemColors.WindowText), Argb(System.Drawing.SystemColors.Highlight),
            Argb(System.Drawing.SystemColors.GrayText)));

    private static uint Argb(System.Drawing.Color color) => unchecked((uint)color.ToArgb());

    /// <summary>Whether the taskbar is light: <c>SystemUsesLightTheme</c>.
    /// Absent on Windows 10 before the light taskbar existed, where it is dark.</summary>
    private bool ReadLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception error)
        {
            log.LogDebug(error, "The taskbar theme could not be read; drawing for a dark taskbar");
            return false;
        }
    }
}
