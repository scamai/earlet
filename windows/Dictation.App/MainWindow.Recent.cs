using System.Windows;
using System.Windows.Automation;
using Dictation.Core;

namespace Dictation.App;

// Part of MainWindow (see MainWindow.xaml.cs): the Recent result section —
// the macOS app's, drawn from DictationViewState.Recent. Built and pinned on
// Linux; never seen on Windows.
public partial class MainWindow
{
    /// <summary>The entry the section last drew: "Show text" closes again
    /// for each new one, as the Mac's disclosure does.</summary>
    private Guid? recentShown;

    /// <summary>The section's fixed text, from <see cref="DictationCopy"/>
    /// like every other sentence; the access keys are this window's.</summary>
    private void ShowRecentLabels()
    {
        DismissLastTextButton.Content = WithAccessKey(DictationCopy.DismissLastText, "D");
        AutomationProperties.SetName(DismissLastTextButton, DictationCopy.DismissLastTextName);
        RecentTextExpander.Header = WithAccessKey(DictationCopy.ShowLastText, "w");
        AutomationProperties.SetName(RecentText, DictationCopy.LastTextName);
        RecentFootnote.Text = DictationCopy.RecentFootnote;
    }

    private void RenderRecent(DictationRecentView? recent)
    {
        if (recent is null)
        {
            if (RecentSection.IsKeyboardFocusWithin)
            {
                // Dismissed from the keyboard, and its button is about to go:
                // keep the keyboard in the window rather than on nothing.
                FocusRestingButton();
            }

            RecentSection.Visibility = Visibility.Collapsed;
            RecentText.Clear();
            recentShown = null;
            return;
        }

        // A new result closes "Show text". If the keyboard was in the box,
        // it moves to the section's buttons below, once they are drawn for
        // this result, rather than stay on a box that is no longer shown.
        var refocus = false;
        if (recentShown != recent.Id)
        {
            refocus = RecentTextExpander.IsKeyboardFocusWithin;
            RecentTextExpander.IsExpanded = false;
            recentShown = recent.Id;
        }

        RecentTitle.Text = recent.Title;
        RecentDetail.Text = recent.Detail;
        RecentTime.Text = recent.Time;
        if (RecentText.Text != recent.Text)
        {
            // Only when the words change: a render for anything else must
            // not drop the user's selection or scroll in the box.
            RecentText.Text = recent.Text;
        }

        CopyLastTextButton.Content = recent.Copying ? recent.CopyLabel : WithAccessKey(recent.CopyLabel, "o");
        // Live while copying too, so the keyboard stays on the button; the
        // controller ignores a second click until the first copy answers.
        CopyLastTextButton.IsEnabled = recent.CanCopy || recent.Copying;
        DismissLastTextButton.IsEnabled = recent.CanDismiss;
        RecentCopyMessage.Text = recent.CopyMessage ?? string.Empty;
        RecentCopyMessage.Visibility = recent.CopyMessage is null ? Visibility.Collapsed : Visibility.Visible;
        RecentSection.Visibility = Visibility.Visible;
        if (refocus)
        {
            // Dismiss is live whenever no copy is on its way, and a new
            // result cannot arrive during a copy: a press then is refused.
            (CopyLastTextButton.IsEnabled ? CopyLastTextButton : DismissLastTextButton).Focus();
        }
    }

    private void CopyLastTextClick(object sender, RoutedEventArgs e) => controller.CopyLastText();

    private void DismissLastTextClick(object sender, RoutedEventArgs e) => controller.DismissLastText();
}
