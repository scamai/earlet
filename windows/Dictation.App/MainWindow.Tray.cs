using System.Windows.Threading;

namespace Dictation.App;

// Part of MainWindow (see MainWindow.xaml.cs): what only the window can do for
// the notification-area menu (DictationTrayCommands). Built and pinned on
// Linux; never seen on Windows.
public partial class MainWindow
{
    /// <summary>
    /// The menu's Review vocabulary…: the window, brought forward with its
    /// Vocabulary section scrolled into view, where the remembered word can
    /// be read and removed. The Mac's item opens the window only; this one
    /// scrolls because the Windows window keeps the section below the fold.
    /// </summary>
    /// <remarks>
    /// The keyboard goes where the eye goes: into the section's add-a-word
    /// box, its first field, so the first Tab or arrow does not scroll back to
    /// wherever the focus was, and Narrator names a field of the section it
    /// was brought to. A field, not the "Learn from my corrections" switch
    /// above it, which a stray Space would turn off, taking the receipt with
    /// it.
    /// </remarks>
    private void ReviewVocabulary()
    {
        ShowFromOutside();
        // After the layout pass a window that was hidden has not had yet.
        Dispatcher.InvokeAsync(
            () =>
            {
                VocabularySection.BringIntoView();
                TeachWord.Focus();
            },
            DispatcherPriority.Loaded);
    }
}
