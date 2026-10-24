using System.Windows;
using System.Windows.Automation;
using Dictation.Core;

namespace Dictation.App;

// Part of MainWindow (see MainWindow.xaml.cs): the Saved recording section —
// a take kept for Recover last dictation (LIMIT 2026-10-10), drawn from
// DictationViewState.SavedTake. Built and pinned on Linux; never seen on
// Windows.
public partial class MainWindow
{
    /// <summary>The section's fixed text, from <see cref="DictationCopy"/>.
    /// Discard has no access key: it deletes a recording, and a stray Alt
    /// chord should not.</summary>
    private void ShowSavedTakeLabels()
    {
        DiscardSavedTakeButton.Content = DictationCopy.DiscardSavedTake;
        AutomationProperties.SetName(DiscardSavedTakeButton, DictationCopy.DiscardSavedTakeName);
        SavedTakeFootnote.Text = DictationCopy.SavedTakeFootnote;
    }

    private void RenderSavedTake(DictationSavedTakeView? saved)
    {
        if (saved is null)
        {
            if (SavedTakeSection.IsKeyboardFocusWithin)
            {
                // Recovered or discarded from the keyboard, and its buttons
                // are about to go: keep the keyboard in the window.
                FocusRestingButton();
            }

            SavedTakeSection.Visibility = Visibility.Collapsed;
            return;
        }

        SavedTakeTitle.Text = saved.Title;
        SavedTakeDetail.Text = saved.Detail;
        SavedTakeTime.Text = saved.Time;
        RecoverButton.Content = saved.Recovering ? saved.RecoverLabel : WithAccessKey(saved.RecoverLabel, "i");
        // Live while recovering too, so the keyboard stays on the button; the
        // controller ignores a second click until the recovery answers.
        RecoverButton.IsEnabled = saved.CanRecover || saved.Recovering;
        DiscardSavedTakeButton.IsEnabled = saved.CanDiscard;
        SavedTakeSection.Visibility = Visibility.Visible;
    }

    private void RecoverClick(object sender, RoutedEventArgs e) => controller.RecoverLastDictation();

    private void DiscardSavedTakeClick(object sender, RoutedEventArgs e) => controller.DiscardSavedTake();
}
