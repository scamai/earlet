namespace Dictation.Core;

/// <summary>
/// One line of the notification-area menu: the Mac menu bar menu's
/// <c>DictationMenuItem</c> and the lines its scene adds around them
/// (<c>DictationRecentResultMenu</c>, <c>DictationLearningMenu</c>), less what
/// Windows does not have.
/// </summary>
public enum DictationTrayItem
{
    /// <summary>A rule between two groups (<see cref="DictationTrayMenu.Layout"/>).</summary>
    Separator,

    /// <summary>"Recover last dictation · &lt;time&gt;": a take that could not
    /// be transcribed, or that a closing Dictation left, is kept; choosing it
    /// transcribes it again (DictationController.Takes.cs). Windows only for
    /// now: the Mac's follows in its own stage 1.</summary>
    RecoverLastDictation,

    /// <summary>"Your words are still here": an inactive line over Copy last
    /// text, after a paste that failed.</summary>
    RecentRecovery,

    /// <summary>"Copy last text · &lt;time&gt;": the time, never the words.</summary>
    CopyLastText,

    /// <summary>"Remembered your correction": an inactive line, while a
    /// learning receipt offers Undo. Names no word.</summary>
    CorrectionRemembered,

    /// <summary>"Review vocabulary…": the window, at its Vocabulary section.</summary>
    ReviewVocabulary,

    /// <summary>"Undo last learning": the receipt's Undo learning.</summary>
    UndoLearning,

    /// <summary>"Enable dictation" or "Pause dictation".</summary>
    Toggle,

    /// <summary>"Cancel dictation", only while there is something to cancel.</summary>
    Cancel,

    /// <summary>"Show Dictation", the default item: bold, and what a left
    /// click on the icon does. Windows' only window, so it is the Mac's
    /// "Settings…" too.</summary>
    ShowWindow,

    /// <summary>"Open log folder", the window's button.</summary>
    OpenLogFolder,

    /// <summary>"Quit Dictation", last and alone.</summary>
    Quit,
}

/// <summary>One line as the menu draws it.</summary>
/// <param name="Item">What it is, and what choosing it does
/// (<see cref="DictationTrayCommands"/>).</param>
/// <param name="Label">Its text, one of <see cref="DictationCopy"/>'s; empty
/// for a separator. Never a dictated word.</param>
/// <param name="Enabled">Whether it can be chosen now. The two inactive lines
/// never can.</param>
public sealed record DictationTrayEntry(DictationTrayItem Item, string Label, bool Enabled)
{
    public static readonly DictationTrayEntry Separator = new(DictationTrayItem.Separator, string.Empty, Enabled: false);
}

/// <summary>
/// The notification-area menu, in order: the Mac menu bar menu's items
/// (docs/design-macos.md, The menu bar menu) decided here, where a test can
/// see them, so <c>DictationTray</c> only draws them.
/// </summary>
/// <remarks>
/// <para>
/// In order, each group only while it has something to offer, with a
/// separator between two groups:
/// </para>
/// <list type="number">
/// <item>While a take is kept: "Recover last dictation · &lt;time&gt;".</item>
/// <item>While a recent result is held: "Your words are still here" after a
/// failed paste, and "Copy last text · &lt;time&gt;".</item>
/// <item>While a learning receipt offers Undo: "Remembered your correction",
/// "Review vocabulary…" and "Undo last learning".</item>
/// <item>"Enable dictation" or "Pause dictation", and "Cancel dictation" while
/// a dictation is listening or transcribing.</item>
/// <item>"Show Dictation" and "Open log folder".</item>
/// <item>"Quit Dictation".</item>
/// </list>
/// <para>
/// <b>Where Windows differs, and why.</b> No headline: the icon's tooltip
/// carries the state, as Windows icons do, with the headline's states (off,
/// starting, ready, a blocked microphone, listening, transcribing), and a
/// status line is the window's. The Mac's separators set the headline off
/// from the groups under it; with no headline they stand between groups
/// instead, so the menu never opens on a rule. "Show Dictation" is also the
/// Mac's "Settings…" (Windows has one window), and Windows has no "Preview
/// Dictation Indicator" item and no keyboard shortcuts in the menu. The live
/// preview's words (DICTATION_PREVIEW=1) are the indicator's alone: no line
/// here, and not the tooltip, ever names one. Enable/Pause is inactive
/// while a dictation listens, transcribes or types, as the Mac's is while it
/// records or is busy, and so is the window's own Pause button, by the same
/// rule (<see cref="DictationViewState.CanPause"/>). Windows has no permission
/// setup, so it never reads "Finish permission setup…": it keeps "Enable
/// dictation", whose Enable asks Windows about the microphone again, and an
/// Enable from here that comes on still blocked brings the window forward
/// with the fix, as the Mac's item opens its window.
/// </para>
/// </remarks>
public static class DictationTrayMenu
{
    /// <summary>The bold item, and what a left click on the icon does.</summary>
    public const DictationTrayItem DefaultItem = DictationTrayItem.ShowWindow;

    /// <summary>
    /// What the menu offers now, in order, separators left out: the Mac
    /// model's <c>menuItems</c> plus the recent-result and learning lines its
    /// scene adds.
    /// </summary>
    /// <param name="state">The controller's half: the toggle, Cancel and the
    /// recent result.</param>
    /// <param name="learningUndoable">The Vocabulary section's half: a
    /// learning receipt offers Undo (<see cref="VocabularyViewState.CanUndoLearning"/>).</param>
    public static IReadOnlyList<DictationTrayItem> Items(DictationTrayState state, bool learningUndoable)
    {
        var items = new List<DictationTrayItem>();
        if (state.SavedTake is not null)
        {
            items.Add(DictationTrayItem.RecoverLastDictation);
        }

        if (state.Recent is { } recent)
        {
            if (recent.NeedsRecovery)
            {
                items.Add(DictationTrayItem.RecentRecovery);
            }

            items.Add(DictationTrayItem.CopyLastText);
        }

        if (learningUndoable)
        {
            items.AddRange([
                DictationTrayItem.CorrectionRemembered,
                DictationTrayItem.ReviewVocabulary,
                DictationTrayItem.UndoLearning]);
        }

        items.Add(DictationTrayItem.Toggle);
        if (state.CanCancel)
        {
            items.Add(DictationTrayItem.Cancel);
        }

        items.AddRange([DictationTrayItem.ShowWindow, DictationTrayItem.OpenLogFolder, DictationTrayItem.Quit]);
        return items;
    }

    /// <summary>
    /// <paramref name="items"/> with a separator wherever the next item
    /// starts another group: never first, never last, never two together.
    /// The Mac's <c>DictationMenuLayout.entries</c>.
    /// </summary>
    public static IReadOnlyList<DictationTrayItem> Layout(IEnumerable<DictationTrayItem> items)
    {
        var laid = new List<DictationTrayItem>();
        int? group = null;
        foreach (var item in items)
        {
            if (item == DictationTrayItem.Separator)
            {
                continue;
            }

            var next = Group(item);
            if (group is { } previous && previous != next)
            {
                laid.Add(DictationTrayItem.Separator);
            }

            group = next;
            laid.Add(item);
        }

        return laid;
    }

    /// <summary>The menu as the tray draws it: every line with its label and
    /// whether it can be chosen.</summary>
    public static IReadOnlyList<DictationTrayEntry> Entries(DictationTrayState state, bool learningUndoable) =>
        Layout(Items(state, learningUndoable)).Select(item => Entry(item, state)).ToArray();

    /// <summary>
    /// Every line the menu can ever show, in order, with a separator wherever
    /// one can stand. The tray makes one object per slot, once, and only shows
    /// or hides it (<see cref="Place"/>): a redraw never takes a line out of
    /// the strip, not even the one whose click is running, which is when
    /// Cancel dictation, say, stops being offered.
    /// </summary>
    public static IReadOnlyList<DictationTrayItem> Slots { get; } = Layout([
        DictationTrayItem.RecoverLastDictation,
        DictationTrayItem.RecentRecovery,
        DictationTrayItem.CopyLastText,
        DictationTrayItem.CorrectionRemembered,
        DictationTrayItem.ReviewVocabulary,
        DictationTrayItem.UndoLearning,
        DictationTrayItem.Toggle,
        DictationTrayItem.Cancel,
        DictationTrayItem.ShowWindow,
        DictationTrayItem.OpenLogFolder,
        DictationTrayItem.Quit]);

    /// <summary>
    /// Which slot draws each of <paramref name="entries"/>: for every one of
    /// <see cref="Slots"/>, the entry it shows, or null to hide it. The entries
    /// are always the slots with some left out, so each lands in the first
    /// slot of its kind after the one before it; a separator lands on the
    /// first free rule, which looks like any other.
    /// </summary>
    /// <exception cref="ArgumentException">An entry has no slot left: the
    /// entries are not in the menu's order.</exception>
    public static IReadOnlyList<DictationTrayEntry?> Place(IReadOnlyList<DictationTrayEntry> entries)
    {
        var placed = new DictationTrayEntry?[Slots.Count];
        var next = 0;
        for (var slot = 0; slot < Slots.Count && next < entries.Count; slot++)
        {
            if (Slots[slot] == entries[next].Item)
            {
                placed[slot] = entries[next++];
            }
        }

        if (next != entries.Count)
        {
            throw new ArgumentException($"{entries[next].Item} has no slot after the lines before it", nameof(entries));
        }

        return placed;
    }

    private static DictationTrayEntry Entry(DictationTrayItem item, DictationTrayState state) => item switch
    {
        DictationTrayItem.Separator => DictationTrayEntry.Separator,
        // Items offers the line only while a take is kept.
        DictationTrayItem.RecoverLastDictation => state.SavedTake is { } saved
            ? new(item, saved.Label, saved.CanRecover)
            : throw new ArgumentException("Recover last dictation without a kept take", nameof(state)),
        DictationTrayItem.RecentRecovery => new(item, DictationCopy.RecentTitleRecovery, Enabled: false),
        // Items offers the line only while a result is held.
        DictationTrayItem.CopyLastText => state.Recent is { } recent
            ? new(item, recent.CopyLabel, recent.CanCopy)
            : throw new ArgumentException("Copy last text without a held result", nameof(state)),
        DictationTrayItem.CorrectionRemembered => new(item, DictationCopy.TrayCorrectionRemembered, Enabled: false),
        DictationTrayItem.ReviewVocabulary => new(item, DictationCopy.ReviewVocabulary, Enabled: true),
        DictationTrayItem.UndoLearning => new(item, DictationCopy.UndoLastLearning, Enabled: true),
        DictationTrayItem.Toggle => new(item, state.ToggleLabel, state.CanToggle),
        DictationTrayItem.Cancel => new(item, DictationCopy.CancelLabel, Enabled: true),
        DictationTrayItem.ShowWindow => new(item, DictationCopy.ShowDictation, Enabled: true),
        DictationTrayItem.OpenLogFolder => new(item, DictationCopy.OpenLogFolder, Enabled: true),
        DictationTrayItem.Quit => new(item, DictationCopy.QuitDictation, Enabled: true),
        _ => throw new ArgumentOutOfRangeException(nameof(item), item, null),
    };

    /// <summary>Which group an item belongs to, in menu order.</summary>
    private static int Group(DictationTrayItem item) => item switch
    {
        DictationTrayItem.RecoverLastDictation => -1,
        DictationTrayItem.RecentRecovery or DictationTrayItem.CopyLastText => 0,
        DictationTrayItem.CorrectionRemembered or DictationTrayItem.ReviewVocabulary
            or DictationTrayItem.UndoLearning => 1,
        DictationTrayItem.Toggle or DictationTrayItem.Cancel => 2,
        DictationTrayItem.ShowWindow or DictationTrayItem.OpenLogFolder => 3,
        DictationTrayItem.Quit => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(item), item, null),
    };
}

/// <summary>
/// What the tray shows now, both halves, and whether a change needs drawing:
/// the controller's <see cref="DictationTrayState"/> and the Vocabulary
/// section's learning half, each handed in as it changes, with the menu
/// placed in <see cref="DictationTrayMenu.Slots"/> for them. Kept here, where
/// a test can see it, so <c>DictationTray</c> only applies what this says.
/// </summary>
/// <remarks>
/// Before the controller's first render it shows dictation off with its toggle
/// inactive. An update equal to what is shown changes nothing and asks for no
/// redraw; any other one does, so a menu left open never keeps offering what
/// is no longer true.
/// </remarks>
public sealed class DictationTrayMenuState
{
    /// <summary>What the tray shows before the controller's first render.</summary>
    public static readonly DictationTrayState BeforeFirstRender =
        new(TrayIcon.Off, DictationCopy.TrayOff, DictationCopy.EnableLabel, CanToggle: false);

    public DictationTrayMenuState()
    {
        Placed = Place();
    }

    /// <summary>The controller's half as last shown: the picture, the
    /// tooltip, the toggle, Cancel and the recent result.</summary>
    public DictationTrayState Shown { get; private set; } = BeforeFirstRender;

    /// <summary>The Vocabulary section's half as last shown: a learning
    /// receipt offers Undo.</summary>
    public bool LearningUndoable { get; private set; }

    /// <summary>For every one of <see cref="DictationTrayMenu.Slots"/>, the
    /// entry it shows now, or null to hide it.</summary>
    public IReadOnlyList<DictationTrayEntry?> Placed { get; private set; }

    /// <summary>The controller rendered.</summary>
    /// <returns>Whether the icon and the menu must be drawn again.</returns>
    public bool Update(DictationTrayState state)
    {
        if (state == Shown)
        {
            return false;
        }

        Shown = state;
        Placed = Place();
        return true;
    }

    /// <summary>The Vocabulary section rendered.</summary>
    /// <returns>Whether the menu must be drawn again.</returns>
    public bool UpdateLearning(bool undoable)
    {
        if (undoable == LearningUndoable)
        {
            return false;
        }

        LearningUndoable = undoable;
        Placed = Place();
        return true;
    }

    private IReadOnlyList<DictationTrayEntry?> Place() =>
        DictationTrayMenu.Place(DictationTrayMenu.Entries(Shown, LearningUndoable));
}

/// <summary>
/// What choosing a notification-area menu item does. The window hands in the
/// three things only it can do (show itself, show its Vocabulary section,
/// quit through its clipboard-safe close); every other item goes to the
/// controller or the Vocabulary section, the same calls the window's own
/// buttons make, so a test drives every item without WinForms.
/// </summary>
/// <remarks>
/// Called on the UI thread, from the menu's click. A separator and the two
/// inactive lines do nothing. Each call guards itself against a state that
/// moved on since the menu was drawn: Cancel with nothing to cancel, Copy last
/// text while it may not start, Pause under a dictation and Undo with no
/// receipt all change nothing. Open log folder that fails brings the window
/// forward, where the status line names the folder, as a failed Enable from
/// the menu does.
/// </remarks>
public sealed class DictationTrayCommands(
    DictationController controller,
    VocabularyModel vocabulary,
    Action showWindow,
    Action reviewVocabulary,
    Action quit)
{
    public Task ChooseAsync(DictationTrayItem item)
    {
        switch (item)
        {
            case DictationTrayItem.RecoverLastDictation:
                controller.RecoverLastDictation();
                break;
            case DictationTrayItem.CopyLastText:
                controller.CopyLastText();
                break;
            case DictationTrayItem.ReviewVocabulary:
                reviewVocabulary();
                break;
            case DictationTrayItem.UndoLearning:
                vocabulary.UndoLastLearning();
                break;
            case DictationTrayItem.Toggle:
                return controller.ToggleAsync();
            case DictationTrayItem.Cancel:
                controller.Cancel();
                break;
            case DictationTrayItem.ShowWindow:
                showWindow();
                break;
            case DictationTrayItem.OpenLogFolder:
                if (!controller.OpenLogFolder())
                {
                    // The sentence naming the folder is on the status line,
                    // of a window the menu is usually used without.
                    showWindow();
                }

                break;
            case DictationTrayItem.Quit:
                quit();
                break;
        }

        return Task.CompletedTask;
    }
}
